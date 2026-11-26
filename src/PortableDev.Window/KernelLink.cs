using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;

namespace PortableDev.Window;

/// <summary>Minimal kernel client for the window shell: hello, requests, events (spec 4.1-4.3).</summary>
public sealed class KernelLink : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _done = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<JsonArray> _pagesSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pump;
    private string? _pagesTaskId;
    private long _counter;

    private KernelLink(Stream stream) => _stream = stream;

    /// <summary>Raised for every task event: (client task id, event data).</summary>
    public event Action<string, JsonObject>? EventReceived;

    public string KernelVersion { get; private set; } = string.Empty;

    public string Root { get; private set; } = string.Empty;

    public string StickId { get; private set; } = string.Empty;

    public string StableRoot { get; private set; } = string.Empty;

    public static async Task<KernelLink> ConnectAsync(string root, string? kernelDll = null, int timeoutMs = 20000)
    {
        var (stickId, prefix) = ReadIdentity(root);
        var pipeName = prefix + "-" + stickId;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var started = false;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync(1000).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                last = ex;
                stream.Dispose();
                if (!started && kernelDll is not null && File.Exists(kernelDll))
                {
                    started = true;
                    StartKernel(root, kernelDll);
                }

                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }

            var link = new KernelLink(stream) { Root = root, StickId = stickId };
            try
            {
                // A wedged kernel must not hang the shell past the overall deadline.
                await link.HandshakeAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or ProtocolException or OperationCanceledException)
            {
                last = ex;
                stream.Dispose();
                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }

            link._pump = Task.Run(link.PumpAsync);
            return link;
        }

        throw new InvalidOperationException("kernel is not running" + (last is null ? string.Empty : ": " + last.Message));
    }

    private async Task HandshakeAsync()
    {
        await SendAsync(Envelope.Make(Kinds.Hello, new JsonObject
        {
            ["protocol"] = ProtocolConstants.Version,
            ["client"] = "window",
            ["clientVersion"] = "0.0.1",
            ["features"] = new JsonArray(Features.Json),
        })).ConfigureAwait(false);

        var reply = await ReadAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("kernel closed the pipe during the handshake");
        if (reply.Kind != Kinds.HelloOk)
        {
            throw new InvalidOperationException("kernel rejected the window shell: " + (JsonUtil.String(reply.Data, "message") ?? reply.Kind));
        }

        KernelVersion = JsonUtil.String(reply.Data, "kernelVersion") ?? string.Empty;
        StableRoot = JsonUtil.String(reply.Data, "stableRoot") ?? string.Empty;
    }

    public async Task<string> ExecAsync(string[] argv)
    {
        var id = "r" + Interlocked.Increment(ref _counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _done[id] = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var envelope = Envelope.Make(Kinds.Req, new JsonObject
        {
            ["argv"] = new JsonArray(argv.Select(argument => (JsonNode?)argument).ToArray()),
            ["opts"] = new JsonObject(),
        });
        envelope.Id = id;
        await SendAsync(envelope).ConfigureAwait(false);
        return id;
    }

    public async Task<JsonObject> RunAsync(string[] argv, int timeoutMs = 180000)
    {
        var id = await ExecAsync(argv).ConfigureAwait(false);
        var task = _done.GetValueOrDefault(id)?.Task;
        if (task is null)
        {
            throw new InvalidOperationException("request was not registered: " + id);
        }

        var finished = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (finished != task)
        {
            _done.TryRemove(id, out _);
            throw new TimeoutException("kernel did not answer within " + timeoutMs + "ms");
        }

        return await task.ConfigureAwait(false);
    }

    public Task CancelAsync(string taskId)
        => SendWithIdAsync(Envelope.Make(Kinds.Ctl, new JsonObject { ["action"] = CtlActions.Cancel }), taskId);

    public Task ReplyAsync(string taskId, string promptId, JsonNode? value)
        => SendWithIdAsync(Envelope.Make(Kinds.Ctl, new JsonObject
        {
            ["action"] = CtlActions.PromptReply,
            ["promptId"] = promptId,
            ["value"] = value?.DeepClone(),
        }), taskId);

    /// <summary>Runs a command and collects its events (used by the `pd.run` bridge helper).</summary>
    public async Task<JsonObject> RunWithEventsAsync(string[] argv, JsonArray events, int timeoutMs = 180000)
    {
        var id = await ExecAsync(argv).ConfigureAwait(false);
        void Handler(string taskId, JsonObject data)
        {
            if (!string.Equals(taskId, id, StringComparison.Ordinal))
            {
                return;
            }

            if (JsonUtil.String(data, "type") == EventTypes.Done)
            {
                return;
            }

            events.Add(data.DeepClone());
        }

        EventReceived += Handler;
        try
        {
            var waiter = _done.GetValueOrDefault(id)
                ?? throw new InvalidOperationException("request was not registered: " + id);
            var finished = await Task.WhenAny(waiter.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
            if (finished != waiter.Task)
            {
                _done.TryRemove(id, out _);
                throw new TimeoutException("kernel did not answer within " + timeoutMs + "ms");
            }

            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            EventReceived -= Handler;
        }
    }

    private Task SendWithIdAsync(Envelope envelope, string id)
    {
        envelope.Id = id;
        return SendAsync(envelope);
    }

    public async Task<JsonArray> RequestPagesAsync()
    {
        _pagesTaskId = await ExecAsync(["ui", "pages"]).ConfigureAwait(false);
        try
        {
            return await _pagesSignal.Task.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("ui pages did not answer within 20s");
        }
    }

    private async Task PumpAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            Envelope? envelope;
            try
            {
                envelope = await ReadAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            if (envelope is null)
            {
                return;
            }

            if (envelope.Kind == Kinds.Ctl)
            {
                continue;
            }

            if (envelope.Kind != Kinds.Evt || envelope.Id is null)
            {
                continue;
            }

            var data = envelope.Data;
            var type = JsonUtil.String(data, "type");
            if (type == EventTypes.Kv && JsonUtil.String(data, "title") == "pages")
            {
                var items = JsonUtil.Array(data, "items") ?? JsonUtil.Array(JsonUtil.Object(data, "items"), "items");
                if (items is not null)
                {
                    _pagesSignal.TrySetResult(items);
                }
            }

            try
            {
                EventReceived?.Invoke(envelope.Id, data);
            }
            catch (Exception ex)
            {
                // A subscriber must never kill the pump; without it every later call hangs.
                _ = ex;
            }
            if (type == EventTypes.Done)
            {
                if (string.Equals(envelope.Id, _pagesTaskId, StringComparison.Ordinal))
                {
                    var exitCode = JsonUtil.Int(data, "exitCode") ?? -1;
                    if (exitCode != 0)
                    {
                        var error = JsonUtil.Object(data, "error");
                        _pagesSignal.TrySetException(new InvalidOperationException(
                            "ui pages failed: " + (JsonUtil.String(error, "code") ?? "exit " + exitCode)));
                    }
                }

                if (_done.TryRemove(envelope.Id, out var waiter))
                {
                    waiter.TrySetResult(data);
                }
            }
        }
    }

    private async Task SendAsync(Envelope envelope)
    {
        var payload = envelope.ToBytes();
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteFrameAsync(_stream, payload, _lifetime.Token).ConfigureAwait(false);
            await _stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<Envelope?> ReadAsync()
    {
        var frame = await FrameCodec.ReadFrameAsync(_stream, ProtocolConstants.MaxFrameBytes, _lifetime.Token).ConfigureAwait(false);
        return frame is null ? null : Envelope.Parse(frame);
    }

    private static void StartKernel(string root, string kernelDll)
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var host = !string.IsNullOrWhiteSpace(dotnetRoot) && File.Exists(Path.Combine(dotnetRoot, "dotnet.exe"))
            ? Path.Combine(dotnetRoot, "dotnet.exe")
            : "dotnet";
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = host,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = root,
        };
        startInfo.ArgumentList.Add(kernelDll);
        startInfo.ArgumentList.Add("--root");
        startInfo.ArgumentList.Add(root);
        try
        {
            System.Diagnostics.Process.Start(startInfo)?.Dispose();
        }
        catch (Exception)
        {
            // The retry loop reports the real failure if this does not come up.
        }
    }

    private static (string StickId, string Prefix) ReadIdentity(string root)
    {
        var stickFile = Path.Combine(root, "state", "stick.json");
        if (!File.Exists(stickFile))
        {
            throw new InvalidOperationException("not a PortableDev stick (missing state\\stick.json): " + root);
        }

        var stickId = JsonUtil.String(JsonNode.Parse(File.ReadAllText(stickFile)) as JsonObject, "stickId")
            ?? throw new InvalidOperationException("stick.json has no stickId");

        var prefix = "portabledev";
        var settingsFile = Path.Combine(root, "config", "settings.json");
        if (File.Exists(settingsFile))
        {
            prefix = JsonUtil.String(JsonNode.Parse(File.ReadAllText(settingsFile)) as JsonObject, "kernel.pipePrefix") ?? prefix;
        }

        return (stickId, prefix);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_pump is not null)
        {
            try { await _pump.ConfigureAwait(false); } catch (Exception) { }
        }

        _stream.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }
}
