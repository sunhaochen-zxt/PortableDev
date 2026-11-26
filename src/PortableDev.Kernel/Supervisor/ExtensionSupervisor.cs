using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PortableDev.Core.Extensions;
using PortableDev.Core.Logging;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;
using PortableDev.Kernel.Dispatch;
using PortableDev.Kernel.Transport;

namespace PortableDev.Kernel.Supervisor;

public sealed record ExtensionProcessInfo(string Name, string State, int? Pid, int Restarts);

/// <summary>
/// Owns extension processes: on-demand start, handshake wait, heartbeat watchdog, restart policy and
/// `ext.invoke` relay (spec 5, architecture 4.8).
/// </summary>
public sealed class ExtensionSupervisor
{
    private readonly IKernelServices _kernel;
    private readonly ConcurrentDictionary<string, ExtensionRuntime> _runtime = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCall> _pendingCalls = new(StringComparer.Ordinal);
    private volatile bool _stopping;

    public ExtensionSupervisor(IKernelServices kernel) => _kernel = kernel;

    public IReadOnlyList<ExtensionProcessInfo> Snapshot()
        => _runtime.Values
            .OrderBy(r => r.Manifest.Name, StringComparer.Ordinal)
            .Select(r => new ExtensionProcessInfo(r.Manifest.Name, r.State, r.Process?.HasExited == false ? r.Process.Id : null, r.Restarts))
            .ToArray();

    public void AttachConnection(string name, ClientConnection connection)
    {
        var runtime = GetOrCreate(name);
        runtime.Connection = connection;
        runtime.State = "starting";
        _kernel.Logger.Debug("ext.attach", "extension connected", new JsonObject { ["extension"] = name });
    }

    public void MarkReady(string name, Envelope ready)
    {
        var runtime = GetOrCreate(name);
        runtime.State = "ready";
        runtime.Capabilities = ParseCapabilities(ready.Data);
        runtime.Ready.TrySetResult(true);
        if (runtime.Capabilities.Any(c => c.Name == "log.ingest" && c.Version == 1))
        {
            _kernel.Logger.Provider.Attach(name, runtime.Manifest.Priority);
        }

        _kernel.Logger.Info("ext.ready", "extension is ready", new JsonObject
        {
            ["extension"] = name,
            ["pid"] = runtime.Process?.HasExited == false ? runtime.Process!.Id : null,
            ["capabilities"] = new JsonArray(runtime.Capabilities.Select(c => (JsonNode?)c.Name).ToArray()),
        });
        EmitExtEvent(name, "ready", null);
    }

    /// <summary>Any frame from the extension proves it is alive; resets every in-flight watchdog.</summary>
    public void TouchExtension(string name)
    {
        if (_runtime.TryGetValue(name, out var runtime))
        {
            foreach (var task in runtime.InFlight.Values)
            {
                task.Touch();
            }
        }
    }

    /// <summary>Fire-and-forget send to a running extension; false when it is not connected.</summary>
    public bool TrySend(string name, Envelope envelope)
    {
        if (!_runtime.TryGetValue(name, out var runtime) || runtime.Connection is null)
        {
            return false;
        }

        _ = SafeSendAsync(runtime.Connection, envelope);
        return true;
    }

    /// <summary>Routes a client prompt reply to the extension that owns the task (spec 4.6).</summary>
    public bool DeliverPromptReply(string taskId, JsonObject data, string? sessionId = null)
        => DeliverToTaskOwner(taskId, Kinds.Ctl, data, sessionId);

    /// <summary>Routes a storage grant reply back to the extension that asked (spec 5.4).</summary>
    public bool DeliverGrantReply(string taskId, JsonObject data, string? senderName = null)
    {
        // Only the extension that was asked (the storage.grant provider) may answer; any other
        // extension could otherwise forge grants into another task.
        var granter = FindProvider("storage.grant");
        if (senderName is not null && granter is not null && !string.Equals(senderName, granter, StringComparison.Ordinal))
        {
            _kernel.Logger.Warn("grant.spoof", "grant reply from an extension that was not asked", new JsonObject { ["extension"] = senderName });
            return false;
        }

        return DeliverToTaskOwner(taskId, Kinds.Ctl, data);
    }

    /// <summary>Sends a message to whichever extension currently owns the given task.</summary>
    public bool IsReady(string name)
        => _runtime.TryGetValue(name, out var runtime) && runtime.State == "ready";

    public bool DeliverToTaskOwner(string taskId, string kind, JsonObject data, string? sessionId = null)
    {
        // Extensions address tasks by ExtensionId; clients know only their own wire id.
        var task = _kernel.Sessions.FindTaskByExtensionId(taskId);
        if (task is null && _kernel.Sessions.FindTask(taskId) is { } byKey)
        {
            task = byKey;
        }

        if (task is null)
        {
            var key = _kernel.Sessions.ResolveKey(taskId, sessionId);
            if (key is not null)
            {
                task = _kernel.Sessions.FindTask(key);
            }
        }

        if (task is null)
        {
            return false;
        }

        if (sessionId is not null && !string.Equals(task.SessionId, sessionId, StringComparison.Ordinal))
        {
            _kernel.Logger.Warn("ctl.spoof", "reply for another session's task", new JsonObject { ["task"] = taskId });
            return false;
        }

        foreach (var runtime in _runtime.Values)
        {
            if (!runtime.InFlight.ContainsKey(task.ExtensionId))
            {
                continue;
            }

            var connection = runtime.Connection;
            if (connection is null)
            {
                return false;
            }

            _ = SafeSendAsync(connection, new Envelope
            {
                Kind = kind,
                Id = task.ExtensionId,
                Ts = TimeUtil.NowIso(),
                Data = (JsonObject)data.DeepClone(),
            });
            return true;
        }

        return false;
    }

    /// <summary>Finds the extension that provides a capability, preferring the lowest priority (spec 5.6).</summary>
    public string? FindProvider(string capability)
    {
        var candidates = _kernel.Registry.Extensions
            .Where(e => e.State == ExtensionState.Ready && e.Provides.Any(p => p.Name == capability))
            .OrderBy(e => e.Priority)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();
        return candidates.Length > 0 ? candidates[0].Name : null;
    }

    /// <summary>Starts an extension if needed; true when it is ready to receive messages.</summary>
    public async Task<bool> StartIfNeededAsync(string name, CancellationToken cancellationToken)
    {
        var manifest = _kernel.Registry.Find(name);
        return manifest is not null && await EnsureRunningAsync(manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Relays an extension-to-extension capability call (spec 5.6).</summary>
    public async Task ForwardCallAsync(string from, Envelope call)
    {
        var capability = JsonUtil.String(call.Data, "cap");
        var id = call.Id;
        if (string.IsNullOrWhiteSpace(capability) || string.IsNullOrWhiteSpace(id))
        {
            await SendCallErrorAsync(from, id, ErrorCodes.BadArgs, "call requires cap and id").ConfigureAwait(false);
            return;
        }

        var provider = FindProvider(capability);
        if (provider is null)
        {
            await SendCallErrorAsync(from, id, ErrorCodes.ExtMissing, "no extension provides " + capability).ConfigureAwait(false);
            return;
        }

        if (!_pendingCalls.TryAdd(id, new PendingCall(from, provider)))
        {
            await SendCallErrorAsync(from, id, ErrorCodes.Busy, "call id " + id + " is already in flight").ConfigureAwait(false);
            return;
        }

        if (!await StartIfNeededAsync(provider, CancellationToken.None).ConfigureAwait(false))
        {
            _pendingCalls.TryRemove(id, out _);
            await SendCallErrorAsync(from, id, ErrorCodes.ExtTimeout, "extension " + provider + " did not become ready").ConfigureAwait(false);
            return;
        }

        // Capability calls can legitimately take minutes (install/unpack); provider death is detected
        // by its own watchdog/connection, so this is only a last-resort leak guard.
        _ = ExpireCallAsync(id, TimeSpan.FromMinutes(30));

        var forward = new Envelope
        {
            Kind = Kinds.Call,
            Id = id,
            Trace = call.Trace,
            Ts = TimeUtil.NowIso(),
            Data = (JsonObject)call.Data.DeepClone(),
        };
        forward.Data["from"] = from;

        if (!TrySend(provider, forward))
        {
            _pendingCalls.TryRemove(id, out _);
            await SendCallErrorAsync(from, id, ErrorCodes.ExtMissing, "extension " + provider + " is not connected").ConfigureAwait(false);
        }
    }

    private async Task ExpireCallAsync(string id, TimeSpan timeout)
    {
        try
        {
            await Task.Delay(timeout).ConfigureAwait(false);
            if (_pendingCalls.TryRemove(id, out var pending))
            {
                await SendCallErrorAsync(pending.From, id, ErrorCodes.Timeout, "capability call timed out after " + TimeUtil.FormatDuration(timeout)).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Shutting down.
        }
    }

    /// <summary>Routes a call.ok / call.err back to the caller; a different extension cannot answer it.</summary>
    public void RouteCallReply(string from, Envelope reply)
    {
        if (reply.Id is null || !_pendingCalls.TryGetValue(reply.Id, out var pending))
        {
            _kernel.Logger.Debug("call.orphan", "call reply has no caller", new JsonObject { ["id"] = reply.Id });
            return;
        }

        if (!string.Equals(pending.Provider, from, StringComparison.Ordinal))
        {
            // Validate BEFORE removing: a forged reply must not delete the real pending entry
            // (that would drop the provider's genuine answer and hang the caller until timeout).
            _kernel.Logger.Warn("call.spoof", "call reply came from a different extension", new JsonObject
            {
                ["id"] = reply.Id,
                ["expected"] = pending.Provider,
                ["actual"] = from,
            });
            return;
        }

        if (!_pendingCalls.TryRemove(new KeyValuePair<string, PendingCall>(reply.Id, pending)))
        {
            _kernel.Logger.Debug("call.orphan", "call reply lost a race with cleanup", new JsonObject { ["id"] = reply.Id });
            return;
        }

        TrySend(pending.From, new Envelope
        {
            Kind = reply.Kind,
            Id = reply.Id,
            Ts = reply.Ts ?? TimeUtil.NowIso(),
            Data = (JsonObject)reply.Data.DeepClone(),
        });
    }

    /// <summary>Relays a grant request to the storage provider (spec 5.4).</summary>
    public async Task ForwardGrantRequestAsync(string from, Envelope request)
    {
        // A grant request may only be sent on behalf of a task this extension actually owns.
        if (request.Id is null || !OwnsTask(from, request.Id))
        {
            SendGrantError(from, request.Id, ErrorCodes.BadArgs, "grant.request must carry the id of a task owned by " + from);
            return;
        }

        var provider = FindProvider("storage.grant");
        if (provider is null)
        {
            SendGrantError(from, request.Id, ErrorCodes.ExtMissing, "no extension provides storage.grant");
            return;
        }

        if (!await StartIfNeededAsync(provider, CancellationToken.None).ConfigureAwait(false))
        {
            SendGrantError(from, request.Id, ErrorCodes.ExtTimeout, "storage extension did not become ready");
            return;
        }

        var forward = new Envelope
        {
            Kind = Kinds.Ctl,
            Id = request.Id,
            Trace = request.Trace,
            Ts = TimeUtil.NowIso(),
            Data = (JsonObject)request.Data.DeepClone(),
        };
        forward.Data["from"] = from;

        if (!TrySend(provider, forward))
        {
            SendGrantError(from, request.Id, ErrorCodes.ExtMissing, "storage extension is not connected");
        }
    }

    private void SendGrantError(string to, string? taskId, string code, string message)
    {
        if (taskId is null)
        {
            return;
        }

        _ = DeliverToTaskOwner(taskId, Kinds.Ctl, new JsonObject
        {
            ["action"] = CtlActions.GrantReply,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });
    }

    private Task SendCallErrorAsync(string to, string? id, string code, string message)
    {
        if (id is null)
        {
            return Task.CompletedTask;
        }

        TrySend(to, new Envelope
        {
            Kind = Kinds.CallErr,
            Id = id,
            Ts = TimeUtil.NowIso(),
            Data = new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } },
        });
        return Task.CompletedTask;
    }

    public bool TryGetInvocationId(string name, out string? invocationId)
    {
        invocationId = _runtime.TryGetValue(name, out var runtime) ? runtime.InvocationId : null;
        return invocationId is not null;
    }

    /// <summary>True while the named extension owns the given task key (used for grant/call ownership).</summary>
    public bool OwnsTask(string name, string taskKey)
        => _runtime.TryGetValue(name, out var runtime) && runtime.InFlight.ContainsKey(taskKey);

    /// <summary>An extension connection is only trusted when it presents the invocation id we spawned it with.</summary>
    public bool ValidateExtensionHello(string name, string? invocationId, out string? error)
    {
        error = null;
        if (!_runtime.TryGetValue(name, out var runtime))
        {
            error = $"extension '{name}' was not started by this kernel";
            return false;
        }

        if (runtime.Process is not { HasExited: false })
        {
            error = $"extension '{name}' is not running";
            return false;
        }

        if (string.IsNullOrEmpty(invocationId) ||
            !string.Equals(runtime.InvocationId, invocationId, StringComparison.Ordinal))
        {
            error = $"extension '{name}' did not present its invocation id";
            return false;
        }

        if (runtime.Connection is not null)
        {
            error = $"extension '{name}' is already connected";
            return false;
        }

        return true;
    }

    /// <summary>Starts every extension whose manifest asks for `start: eager`.</summary>
    public async Task StartEagerAsync(CancellationToken cancellationToken)
    {
        foreach (var manifest in _kernel.Registry.Extensions.Where(e => e.State == ExtensionState.Ready && e.Lifecycle.Start == "eager").OrderBy(e => e.Priority))
        {
            try
            {
                var ok = await EnsureRunningAsync(manifest, cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    _kernel.Logger.Warn("ext.eager", "eager extension did not become ready", new JsonObject { ["extension"] = manifest.Name });
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static async Task SafeSendAsync(ClientConnection connection, Envelope envelope)
    {
        try
        {
            await connection.SendAsync(envelope).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Peer gone or shutting down; the connection loop cleans up.
        }
    }

    public Task OnConnectionClosedAsync(string name)
    {
        if (_runtime.TryGetValue(name, out var runtime))
        {
            FailInFlight(runtime, $"extension '{name}' disconnected while a task was running");
            runtime.Connection = null;
            // A live process without a pipe is useless: kill it so the next command starts a fresh one.
            // Keeping it would leave Ready completed and every later invoke would see a stale "ready".
            runtime.Ready = NewReadySignal();
            if (runtime.Process is { HasExited: false })
            {
                KillProcessTree(runtime);
            }

            runtime.State = runtime.Process is { HasExited: false } ? "starting" : "exited";
        }

        return Task.CompletedTask;
    }

    public async Task<CommandResult> InvokeAsync(TaskContext ctx, CommandEntry entry, string[] args)
    {
        var name = entry.ExtensionName ?? string.Empty;
        var manifest = _kernel.Registry.Find(name);
        if (manifest is null)
        {
            return new CommandResult(ExitCodes.NotFound, new JsonObject
            {
                ["code"] = ErrorCodes.ExtMissing,
                ["message"] = $"extension '{name}' is not installed",
            }, null, null);
        }

        if (!await EnsureRunningAsync(manifest, ctx.Token).ConfigureAwait(false))
        {
            return new CommandResult(ExitCodes.Timeout, new JsonObject
            {
                ["code"] = ErrorCodes.ExtTimeout,
                ["message"] = $"extension '{name}' did not become ready",
                ["hint"] = $"check pd kernel log and the extension's output",
            }, null, null);
        }

        var runtime = GetOrCreate(name);
        runtime.InFlight[ctx.ExtensionId] = ctx;
        try
        {
            return await InvokeCoreAsync(ctx, name, manifest, runtime, args, entry).ConfigureAwait(false);
        }
        finally
        {
            runtime.InFlight.TryRemove(ctx.ExtensionId, out _);
            ScheduleIdleStop(runtime);
        }
    }

    /// <summary>Stops an on-demand extension once it has been quiet for its idleTimeout.</summary>
    private void ScheduleIdleStop(ExtensionRuntime runtime)
    {
        var idle = runtime.Manifest.Lifecycle.IdleTimeout;
        if (_stopping || runtime.Manifest.Lifecycle.Start == "eager" || idle <= TimeSpan.Zero)
        {
            return;
        }

        runtime.IdleCts?.Cancel();
        var cancel = new CancellationTokenSource();
        runtime.IdleCts = cancel;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(idle, cancel.Token).ConfigureAwait(false);
                if (_stopping || !runtime.InFlight.IsEmpty)
                {
                    return;
                }

                if (!await runtime.StartGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
                {
                    return;
                }

                try
                {
                    if (!_stopping && runtime.InFlight.IsEmpty && runtime.Process is { HasExited: false })
                    {
                        StopProcess(runtime, "idle timeout");
                        _kernel.Logger.Info("ext.idle", "extension stopped after idling", new JsonObject
                        {
                            ["extension"] = runtime.Manifest.Name,
                            ["idleTimeout"] = TimeUtil.FormatDuration(idle),
                        });
                        EmitExtEvent(runtime.Manifest.Name, "stopped", "idle");
                    }
                }
                finally
                {
                    runtime.StartGate.Release();
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or ObjectDisposedException)
            {
                // Cancelled by new work or shutdown.
            }
        });
    }

    private void SendPing(ExtensionRuntime runtime, TaskContext ctx)
    {
        var connection = runtime.Connection;
        if (connection is null)
        {
            return;
        }

        _ = SafeSendAsync(connection, new Envelope
        {
            Kind = Kinds.Ctl,
            Id = ctx.ExtensionId,
            Ts = TimeUtil.NowIso(),
            Data = new JsonObject { ["action"] = CtlActions.Ping, ["ts"] = TimeUtil.NowIso() },
        });
    }

    private async Task<CommandResult> InvokeCoreAsync(TaskContext ctx, string name, ExtensionManifest manifest, ExtensionRuntime runtime, string[] args, CommandEntry entry)
    {
        var connection = runtime.Connection;
        if (connection is null)
        {
            return new CommandResult(ExitCodes.NotFound, new JsonObject
            {
                ["code"] = ErrorCodes.ExtMissing,
                ["message"] = $"extension '{name}' is not connected",
            }, null, null);
        }

        var invoke = new Envelope
        {
            Kind = Kinds.ExtInvoke,
            // The extension echoes this id back on every event; use the session-scoped key so two
            // clients may pick the same wire id without their completion signals crossing.
            Id = ctx.ExtensionId,
            Trace = ctx.Trace,
            Ts = TimeUtil.NowIso(),
            Data = new JsonObject
            {
                ["taskKey"] = ctx.ExtensionId,
                ["cmd"] = entry.Name,
                ["argv"] = new JsonArray(args.Select(a => (JsonNode?)a).ToArray()),
                ["ctx"] = ctx.ClientCtx.DeepClone(),
                ["opts"] = ctx.Opts.DeepClone(),
                ["deadline"] = string.Empty,
            },
        };

        try
        {
            await connection.SendAsync(invoke).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            return new CommandResult(ExitCodes.NotFound, new JsonObject
            {
                ["code"] = ErrorCodes.ExtMissing,
                ["message"] = $"extension '{name}' is no longer connected",
                ["hint"] = ex.Message,
            }, null, null);
        }

        using var cancelForward = ctx.Token.Register(() =>
        {
            _ = SafeSendAsync(connection, new Envelope
            {
                Kind = Kinds.Ctl,
                Id = ctx.ExtensionId,
                Data = new JsonObject { ["action"] = CtlActions.Cancel, ["id"] = ctx.ExtensionId },
            });
        });

        var watchdog = manifest.Lifecycle.HeartbeatTimeout;
        var slice = TimeSpan.FromMilliseconds(Math.Clamp(watchdog.TotalMilliseconds / 4, 250, 5000));
        while (true)
        {
            var silent = ctx.SinceActivity;
            if (silent >= watchdog)
            {
                _kernel.Logger.Warn("ext.watchdog", "extension stopped answering; killing its process tree", new JsonObject
                {
                    ["extension"] = name,
                    ["timeoutSeconds"] = (int)watchdog.TotalSeconds,
                });
                KillProcessTree(runtime);
                return new CommandResult(ExitCodes.Timeout, new JsonObject
                {
                    ["code"] = ErrorCodes.ExtTimeout,
                    ["message"] = $"extension '{name}' did not answer within {TimeUtil.FormatDuration(watchdog)}",
                    ["retryable"] = true,
                }, null, null);
            }

            if (silent >= watchdog / 2)
            {
                // Spec 5.5: a quiet extension gets a ping; any pong (or event) resets the silence window,
                // so a healthy long task is not killed just because it has nothing to report.
                SendPing(runtime, ctx);
            }

            var wait = watchdog - silent;
            if (wait > slice)
            {
                wait = slice;
            }

            try
            {
                var result = await ctx.Completion.Task.WaitAsync(wait, ctx.Token).ConfigureAwait(false);
                return new CommandResult(result.ExitCode, result.Error, result.Summary, result.Metrics, DoneEmitted: ctx.DoneFromExtension);
            }
            catch (TimeoutException)
            {
                // Poll again; a pong may have refreshed SinceActivity meanwhile.
            }
        }
    }

    public async Task<bool> RestartAsync(string name, CancellationToken cancellationToken)
    {
        var manifest = _kernel.Registry.Find(name);
        if (manifest is null)
        {
            return false;
        }

        var runtime = GetOrCreate(name);
        await runtime.StartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            StopProcess(runtime, "restart");
            runtime.Ready = NewReadySignal();
            return await StartProcessAsync(runtime, manifest, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            runtime.StartGate.Release();
        }
    }

    /// <summary>Stops accepting new work and prevents crash restarts from racing the shutdown.</summary>
    public void BeginShutdown() => _stopping = true;

    public async Task StopAllAsync(bool force = false)
    {
        _stopping = true;
        foreach (var runtime in _runtime.Values)
        {
            if (runtime.Process is not { HasExited: false })
            {
                continue;
            }

            if (force)
            {
                runtime.IdleCts?.Cancel();
                KillProcessTree(runtime);
                continue;
            }

            // Do not block shutdown on a restart that is still holding the gate: kill the tree directly.
            if (!await runtime.StartGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false))
            {
                KillProcessTree(runtime);
                continue;
            }

            try
            {
                runtime.IdleCts?.Cancel();
                StopProcess(runtime, "kernel shutdown");
            }
            finally
            {
                runtime.StartGate.Release();
            }
        }
    }

    private async Task<bool> EnsureRunningAsync(ExtensionManifest manifest, CancellationToken cancellationToken)
    {
        var runtime = GetOrCreate(manifest.Name);
        if (runtime.Connection is not null && runtime.State == "ready")
        {
            return true;
        }

        await runtime.StartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runtime.Connection is not null && runtime.State == "ready")
            {
                return true;
            }

            if (runtime.Process is { HasExited: false })
            {
                var ready = await runtime.Ready.Task.WaitAsync(manifest.Lifecycle.StartTimeout, cancellationToken).ConfigureAwait(false);
                if (ready && runtime.Connection is not null && runtime.State == "ready")
                {
                    return true;
                }

                // Process alive but the pipe is gone (stale ready): restart from scratch.
                StopProcess(runtime, "stale connection");
                runtime.Ready = NewReadySignal();
            }

            return await StartProcessAsync(runtime, manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            runtime.StartGate.Release();
        }
    }

    private async Task<bool> StartProcessAsync(ExtensionRuntime runtime, ExtensionManifest manifest, CancellationToken cancellationToken)
    {
        // The budget is about restarts, not the initial start: Restarts==0 must always be allowed.
        // (The old condition only applied to restart="never", i.e. exactly the case that never restarts.)
        if (runtime.Restarts > 0 && runtime.Restarts >= manifest.Lifecycle.MaxRestarts)
        {
            runtime.State = "failed";
            return false;
        }

        var entry = manifest.EntryPath;
        if (!File.Exists(entry))
        {
            runtime.State = "failed";
            runtime.RejectReason = $"entry file '{manifest.Entry}' is missing";
            _kernel.Logger.Error("ext.entry", "extension entry is missing", null, new JsonObject
            {
                ["extension"] = manifest.Name,
                ["entry"] = entry,
            });
            EmitExtEvent(manifest.Name, "crashed", runtime.RejectReason);
            return false;
        }

        var (fileName, arguments) = ResolveInterpreter(manifest, entry);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = manifest.Cwd ?? manifest.Directory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        if (manifest.InheritEnv)
        {
            // Explicit opt-in: the extension asked to inherit the kernel's environment.
            foreach (System.Collections.DictionaryEntry environmentEntry in Environment.GetEnvironmentVariables())
            {
                var key = Convert.ToString(environmentEntry.Key, System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(key))
                {
                    startInfo.Environment[key] = Convert.ToString(environmentEntry.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }
        }

        foreach (var name in new[] { "SystemRoot", "windir", "TEMP", "TMP", "PATH" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        // Framework-dependent .NET extensions (proc) must resolve the runtime shipped on the stick,
        // not whatever the machine happens to have installed.
        if (ResolveDotnetRoot() is { Length: > 0 } dotnetRoot)
        {
            startInfo.Environment["DOTNET_ROOT"] = dotnetRoot;
        }

        startInfo.Environment["PD_ROOT"] = _kernel.State.Root;
        startInfo.Environment["PD_STATE"] = _kernel.Paths.State;
        startInfo.Environment["PD_STABLE_ROOT"] = _kernel.State.StableRoot;
        startInfo.Environment["PD_PIPE"] = $"{PipePrefix()}-{_kernel.State.StickId}";
        startInfo.Environment["PD_STICK_ID"] = _kernel.State.StickId;
        startInfo.Environment["PD_KERNEL_ABI"] = _kernel.State.Abi.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["PD_PROTOCOL"] = ProtocolConstants.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["PD_EXTENSION"] = manifest.Name;
        runtime.InvocationId = "i-" + Guid.NewGuid().ToString("N")[..8];
        startInfo.Environment["PD_INVOCATION_ID"] = runtime.InvocationId;

        if (manifest.Env is not null)
        {
            foreach (var (key, value) in manifest.Env)
            {
                startInfo.Environment[key] = JsonUtil.StringValue(value) ?? string.Empty;
            }
        }

        // Reset before starting: a fast extension can answer before Start() returns.
        runtime.Ready = NewReadySignal();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _kernel.Logger.Debug("ext.stdout", e.Data, new JsonObject { ["extension"] = manifest.Name });
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _kernel.Logger.Warn("ext.stderr", e.Data, new JsonObject { ["extension"] = manifest.Name });
            }
        };
        process.Exited += (_, _) => OnProcessExited(runtime, process);

        try
        {
            process.Start();
            // Publish the process before it can connect: a fast extension opens the pipe immediately.
            runtime.Process = process;
            runtime.State = "starting";
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            runtime.State = "failed";
            _kernel.Logger.Error("ext.start", "cannot start extension", ex, new JsonObject { ["extension"] = manifest.Name, ["file"] = fileName });
            EmitExtEvent(manifest.Name, "crashed", ex.Message);
            return false;
        }

        runtime.LastStart = DateTimeOffset.Now;
        _kernel.Logger.Info("ext.spawn", "extension process started", new JsonObject
        {
            ["extension"] = manifest.Name,
            ["pid"] = process.Id,
            ["interpreter"] = manifest.Interpreter,
        });
        EmitExtEvent(manifest.Name, "starting", null);

        try
        {
            return await runtime.Ready.Task.WaitAsync(manifest.Lifecycle.StartTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            runtime.RejectReason = $"did not answer within {TimeUtil.FormatDuration(manifest.Lifecycle.StartTimeout)}";
            KillProcessTree(runtime);
            EmitExtEvent(manifest.Name, "crashed", runtime.RejectReason);
            return false;
        }
    }

    /// <summary>Answers every caller waiting on a dead provider instead of letting them time out.</summary>
    private void FailPendingCallsFor(string provider, string reason)
    {
        foreach (var pair in _pendingCalls.ToArray())
        {
            if (!string.Equals(pair.Value.Provider, provider, StringComparison.Ordinal))
            {
                continue;
            }

            if (_pendingCalls.TryRemove(new KeyValuePair<string, PendingCall>(pair.Key, pair.Value)))
            {
                _ = SendCallErrorAsync(pair.Value.From, pair.Key, ErrorCodes.ExtTimeout, reason);
            }
        }
    }

    private void OnProcessExited(ExtensionRuntime runtime, Process process)
    {
        if (!ReferenceEquals(runtime.Process, process))
        {
            // Stale event from a process we already stopped or replaced; do not clobber the new one.
            process.Dispose();
            return;
        }

        var exitCode = runtime.Process?.ExitCode;
        runtime.Connection = null;
        _kernel.Logger.Provider.Detach(runtime.Manifest.Name);
        FailInFlight(runtime, $"extension '{runtime.Manifest.Name}' exited while a task was running");
        FailPendingCallsFor(runtime.Manifest.Name, $"extension '{runtime.Manifest.Name}' exited while a capability call was pending");
        runtime.State = exitCode == 0 ? "exited" : "crashed";
        _kernel.Logger.Warn("ext.exit", "extension process exited", new JsonObject
        {
            ["extension"] = runtime.Manifest.Name,
            ["exitCode"] = exitCode,
        });
        EmitExtEvent(runtime.Manifest.Name, runtime.State, exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (runtime.State == "crashed" && runtime.Manifest.Lifecycle.Restart == "on-crash" &&
            runtime.Restarts < runtime.Manifest.Lifecycle.MaxRestarts && !_stopping)
        {
            runtime.Restarts++;
            _ = Task.Run(async () =>
            {
                await Task.Delay(runtime.Manifest.Lifecycle.RestartBackoff).ConfigureAwait(false);
                if (_stopping)
                {
                    return;
                }

                try
                {
                    // WaitAsync(TimeSpan) returns false on timeout instead of throwing; ignoring
                    // the result double-starts the extension and corrupts the gate count.
                    if (!await runtime.StartGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false))
                    {
                        return;
                    }

                    try
                    {
                        if (runtime.Process is null or { HasExited: true })
                        {
                            await StartProcessAsync(runtime, runtime.Manifest, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        runtime.StartGate.Release();
                    }
                }
                catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException)
                {
                    // Shutting down.
                }
            });
        }
    }

    private void StopProcess(ExtensionRuntime runtime, string reason)
    {
        if (runtime.Process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _kernel.Logger.Warn("ext.stop", "cannot stop extension", new JsonObject { ["extension"] = runtime.Manifest.Name, ["reason"] = reason });
            }
        }

        runtime.Connection = null;
        runtime.State = "stopped";
        runtime.Process?.Dispose();
        runtime.Process = null;
    }

    private void KillProcessTree(ExtensionRuntime runtime)
    {
        if (runtime.Process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _kernel.Logger.Warn("ext.kill", "process tree kill failed", new JsonObject { ["extension"] = runtime.Manifest.Name });
            }
        }

        runtime.Connection = null;
        runtime.Ready.TrySetResult(false);
    }

    private void EmitExtEvent(string name, string state, string? reason)
    {
        var data = new JsonObject
        {
            ["type"] = EventTypes.Ext,
            ["name"] = name,
            ["state"] = state,
        };

        if (reason is not null)
        {
            data["reason"] = reason;
        }

        var pid = _runtime.TryGetValue(name, out var runtime) && runtime.Process is { HasExited: false } ? runtime.Process!.Id : (int?)null;
        if (pid is not null)
        {
            data["pid"] = pid.Value;
        }

        var envelope = Envelope.Event(ProtocolConstants.SystemTaskId, 0, data);
        _ = _kernel.Sessions.BroadcastExtensionEventAsync(envelope);
    }

    /// <summary>The dotnet host that runs a dll-based extension: our own host first, then the shipped runtime.</summary>
    private static string ResolveDotnetExecutable()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null
            && string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        var root = ResolveDotnetRoot();
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "dotnet";
    }

    /// <summary>The dotnet root that hosts this kernel: env override, else derived from the runtime path.</summary>
    private static string? ResolveDotnetRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        // <root>/shared/Microsoft.NETCore.App/<version> -> <root>
        var runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
            .TrimEnd('\\', '/');
        return Directory.GetParent(runtimeDirectory)?.Parent?.Parent?.FullName;
    }

    private (string FileName, string[] Arguments) ResolveInterpreter(ExtensionManifest manifest, string entry)
    {
        var args = new List<string>();
        var interpreter = manifest.Interpreter;
        if (interpreter == "auto")
        {
            interpreter = Path.GetExtension(entry).ToLowerInvariant() switch
            {
                ".ps1" => "pwsh",
                ".py" => "python",
                ".js" => "node",
                ".cmd" or ".bat" => "cmd",
                ".dll" => "dotnet",
                _ => "exe",
            };
        }

        switch (interpreter)
        {
            case "exe":
                // FileName is already the entry; adding it again passed the path as an argument.
                args.AddRange(manifest.EntryArgs);
                return (entry, args.ToArray());
            case "pwsh":
                args.AddRange(["-NoProfile", "-File", entry]);
                args.AddRange(manifest.EntryArgs);
                return ("pwsh", args.ToArray());
            case "python":
                args.Add(entry);
                args.AddRange(manifest.EntryArgs);
                return ("python", args.ToArray());
            case "node":
                args.Add(entry);
                args.AddRange(manifest.EntryArgs);
                return ("node", args.ToArray());
            case "dotnet":
                args.Add(entry);
                args.AddRange(manifest.EntryArgs);
                return (ResolveDotnetExecutable(), args.ToArray());
            case "cmd":
                args.AddRange(["/c", entry]);
                args.AddRange(manifest.EntryArgs);
                return ("cmd", args.ToArray());
            default:
                args.Add(entry);
                return (entry, args.ToArray());
        }
    }

    /// <summary>Capabilities may be declared as strings or as objects with a version (spec 2.5).</summary>
    private static IReadOnlyList<Capability> ParseCapabilities(System.Text.Json.Nodes.JsonObject ready)
    {
        var result = new List<Capability>();
        foreach (var node in JsonUtil.Array(ready, "capabilities") ?? [])
        {
            if (node is System.Text.Json.Nodes.JsonObject capability)
            {
                var name = JsonUtil.String(capability, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(new Capability(name, JsonUtil.Bool(capability, "mutates") ?? false, JsonUtil.Int(capability, "version") ?? 1));
                }
            }
            else
            {
                var name = JsonUtil.StringValue(node);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Add(new Capability(name, true));
                }
            }
        }

        return result;
    }

    private static TaskCompletionSource<bool> NewReadySignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Fails every task waiting on a dead extension so the client gets a `done` instead of a hang.</summary>
    private static void FailInFlight(ExtensionRuntime runtime, string reason)
    {
        foreach (var key in runtime.InFlight.Keys.ToArray())
        {
            if (runtime.InFlight.TryRemove(key, out var task))
            {
                task.Complete(ExitCodes.Timeout, new JsonObject
                {
                    ["code"] = ErrorCodes.ExtTimeout,
                    ["message"] = reason,
                    ["retryable"] = true,
                });
            }
        }
    }

    private string PipePrefix() => _kernel.PipePrefix;

    private ExtensionRuntime GetOrCreate(string name)
        => _runtime.GetOrAdd(name, n => new ExtensionRuntime(_kernel.Registry.Find(n) ?? throw new InvalidOperationException($"unknown extension '{n}'")));

    private sealed record PendingCall(string From, string Provider);

    private sealed class ExtensionRuntime(ExtensionManifest manifest)
    {
        public ExtensionManifest Manifest { get; } = manifest;

        public SemaphoreSlim StartGate { get; } = new(1, 1);

        public Process? Process { get; set; }

        public ClientConnection? Connection { get; set; }

        public TaskCompletionSource<bool> Ready { get; set; } = NewReadySignal();

        public string State { get; set; } = "stopped";

        public string? RejectReason { get; set; }

        public int Restarts { get; set; }

        public DateTimeOffset? LastStart { get; set; }

        public IReadOnlyList<Capability> Capabilities { get; set; } = [];

        public string? InvocationId { get; set; }

        public CancellationTokenSource? IdleCts { get; set; }

        public System.Collections.Concurrent.ConcurrentDictionary<string, TaskContext> InFlight { get; } = new(StringComparer.Ordinal);
    }
}
