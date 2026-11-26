using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PortableDev.Core.Identity;
using PortableDev.Core.Logging;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;

namespace PortableDev.Kernel.Lock;

/// <summary>
/// Two layers, exactly as kernel.md section 4 describes:
/// a session-local named mutex plus a heartbeat file that covers cross-session and stale-owner cases.
/// </summary>
public sealed class SingleInstanceLock : IDisposable
{
    private const int HeartbeatSeconds = 10;
    private const int StaleSeconds = 60;

    private readonly Mutex _mutex;
    private readonly string _path;
    private readonly KernelLogger _logger;
    private readonly CancellationTokenSource _heartbeat = new();
    private readonly Task _heartbeatTask;
    private bool _disposed;

    private SingleInstanceLock(Mutex mutex, string path, KernelLogger logger, string stickId, string root)
    {
        _mutex = mutex;
        _path = path;
        _logger = logger;
        StickId = stickId;
        Root = root;
        _heartbeatTask = Task.Run(HeartbeatLoopAsync);
    }

    public string StickId { get; }

    public string Root { get; }

    public static (SingleInstanceLock? Lock, string? Error, int ExitCode) Acquire(
        string stickRoot, string stateLockFile, string stickId, string machineId, string kernelVersion, int protocol, KernelLogger logger)
    {
        var mutexName = $@"Local\portabledev-{stickId}";
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (!createdNew)
            {
                // Another kernel in this session already owns the name.
                mutex.Dispose();
                return (null, "another kernel is already running in this session", ExitCodes.Busy);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return (null, "the kernel mutex is owned by another user", ExitCodes.Busy);
        }

        try
        {
            if (File.Exists(stateLockFile))
            {
                var existing = ReadLock(stateLockFile);
                var fresh = existing.HeartbeatAt is not null &&
                            (DateTimeOffset.Now - existing.HeartbeatAt.Value) < TimeSpan.FromSeconds(StaleSeconds);
                if (fresh)
                {
                    mutex.ReleaseMutex();
                    mutex.Dispose();
                    if (existing.Root is not null &&
                        !string.Equals(Path.GetFullPath(existing.Root), Path.GetFullPath(stickRoot), StringComparison.OrdinalIgnoreCase))
                    {
                        // A copied stick shares the id but not the root; explain it instead of a bare "busy".
                        return (null, $"stick id {stickId} is already used by another stick root '{existing.Root}' (copied stick?)", ExitCodes.Busy);
                    }

                    return (null, $"another kernel is using this stick (pid {existing.Pid?.ToString() ?? "?"})", ExitCodes.Busy);
                }

                logger.Warn("lock.takeover", "previous kernel left a stale lock", new JsonObject
                {
                    ["previousPid"] = existing.Pid,
                    ["heartbeatAgeSeconds"] = existing.HeartbeatAt is null
                        ? null
                        : (int?)(DateTimeOffset.Now - existing.HeartbeatAt.Value).TotalSeconds,
                });
            }

            var instance = new SingleInstanceLock(mutex, stateLockFile, logger, stickId, stickRoot);
            instance.WriteLock(machineId, kernelVersion, protocol);
            return (instance, null, ExitCodes.Ok);
        }
        catch (IOException ex)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            return (null, $"cannot write the lock file: {ex.Message}", ExitCodes.Env);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _heartbeat.Cancel();
        try
        {
            _heartbeatTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutting down anyway.
        }

        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A stale lock with an old heartbeat is recoverable on next start.
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned (defensive).
        }

        _mutex.Dispose();
        _heartbeat.Dispose();
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            while (!_heartbeat.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatSeconds), _heartbeat.Token).ConfigureAwait(false);
                try
                {
                    if (File.Exists(_path))
                    {
                        var node = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject ?? new JsonObject();
                        node["heartbeatAt"] = TimeUtil.NowIso();
                        AtomicFile.WriteAllText(_path, node.ToJsonString());
                    }
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
                {
                    _logger.Warn("lock.heartbeat", "heartbeat write failed", new JsonObject { ["error"] = ex.Message });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void WriteLock(string machineId, string kernelVersion, int protocol)
    {
        var document = new JsonObject
        {
            ["schema"] = 1,
            ["pid"] = Environment.ProcessId,
            ["machineId"] = machineId,
            ["kernelVersion"] = kernelVersion,
            ["protocol"] = protocol,
            ["root"] = Root,
            ["startedAt"] = TimeUtil.NowIso(),
            ["heartbeatAt"] = TimeUtil.NowIso(),
            ["state"] = "running",
        };

        AtomicFile.WriteAllText(_path, document.ToJsonString());
    }

    private static (int? Pid, DateTimeOffset? HeartbeatAt, string? Root) ReadLock(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var pid = JsonUtil.Int(node, "pid");
            var root = JsonUtil.String(node, "root");
            var heartbeatText = JsonUtil.String(node, "heartbeatAt");
            DateTimeOffset? heartbeat = null;
            if (heartbeatText is not null && DateTimeOffset.TryParse(heartbeatText, out var parsed))
            {
                heartbeat = parsed;
            }

            if (pid is not null)
            {
                try
                {
                    using var process = Process.GetProcessById(pid.Value);
                    if (process.HasExited)
                    {
                        heartbeat = null;
                    }
                }
                catch (ArgumentException)
                {
                    heartbeat = null;
                }
            }

            return (pid, heartbeat, root);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return (null, null, null);
        }
    }
}
