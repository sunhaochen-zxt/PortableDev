using System.Text.Json.Nodes;
using PortableDev.Core.Logging;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;
using PortableDev.Kernel.Dispatch;
using PortableDev.Kernel.Supervisor;

namespace PortableDev.Kernel.Transport;

/// <summary>
/// One pipe peer. A peer is either a client (shell/UI) or an extension; the role is decided by the
/// `hello` payload (spec 4.1 / 5.1), never by the connection order.
/// </summary>
public sealed class ClientConnection : IMessageSink
{
    private readonly Stream _stream;
    private readonly IKernelServices _kernel;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, TaskContext> _tasks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _features = new(StringComparer.Ordinal);
    private const int MaxConcurrentTasks = 8;

    private string _extensionName = string.Empty;
    private bool _isExtension;
    private bool _handshakeDone;

    public ClientConnection(Stream stream, string connectionId, IKernelServices kernel)
    {
        _stream = stream;
        _kernel = kernel;
        SessionId = "s-" + connectionId;
    }

    public string SessionId { get; }

    public bool WantsExtensionEvents => _features.Contains(Features.ExtEvents);

    public async Task RunAsync(CancellationToken serverToken)
    {
        // The server token is the kernel-wide lifetime: when it fires (shutdown), the connection's
        // own lifetime must end too, otherwise clients could keep issuing commands while we stop.
        using var registration = serverToken.Register(() =>
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        });

        await RunCoreAsync().ConfigureAwait(false);
    }

    public async Task SendAsync(Envelope envelope)
    {
        var bytes = envelope.ToBytes();
        await _writeGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteFrameAsync(_stream, bytes, _lifetime.Token).ConfigureAwait(false);
            await _stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task RunCoreAsync()
    {
        _kernel.Sessions.Register(SessionId, this);
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(5));

            Envelope hello;
            try
            {
                var frame = await FrameCodec.ReadFrameAsync(_stream, ProtocolConstants.MaxFrameBytes, handshake.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    return;
                }

                hello = Envelope.Parse(frame);
            }
            catch (OperationCanceledException)
            {
                await SendHelloErrorAsync(ErrorCodes.Protocol, "handshake timed out", "retry: hello must arrive within 5s").ConfigureAwait(false);
                return;
            }

            if (hello.Kind != Kinds.Hello)
            {
                await SendHelloErrorAsync(ErrorCodes.Protocol, "first message must be hello", "send hello before req").ConfigureAwait(false);
                return;
            }

            if (!await HandleHelloAsync(hello).ConfigureAwait(false))
            {
                return;
            }

            _handshakeDone = true;

            if (_isExtension)
            {
                await RunExtensionLoopAsync().ConfigureAwait(false);
                return;
            }

            await RunClientLoopAsync().ConfigureAwait(false);
        }
        catch (ProtocolException ex)
        {
            _kernel.Logger.Warn("conn.protocol", ex.Message, new JsonObject { ["session"] = SessionId });
            if (!_handshakeDone)
            {
                await TrySendHelloErrorAsync(ex.Code, ex.Message, null).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown or peer gone.
        }
        catch (IOException)
        {
            // Peer closed.
        }
        catch (Exception ex)
        {
            _kernel.Logger.Error("conn.crash", "connection handler threw", ex, new JsonObject { ["session"] = SessionId });
        }
        finally
        {
            _lifetime.Cancel();
            TaskContext[] pending;
            lock (_tasks)
            {
                pending = _tasks.Values.ToArray();
                _tasks.Clear();
            }

            foreach (var task in pending)
            {
                task.Cancel();
            }
            _kernel.Sessions.Unregister(SessionId);
            if (_isExtension)
            {
                await _kernel.Supervisor.OnConnectionClosedAsync(_extensionName).ConfigureAwait(false);
            }

            _writeGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task<bool> HandleHelloAsync(Envelope hello)
    {
        var data = hello.Data;
        var role = JsonUtil.String(data, "role") ?? "client";
        var protocol = JsonUtil.Int(data, "protocol") ?? 0;

        if (protocol != ProtocolConstants.Version)
        {
            await SendAsync(Envelope.Make(Kinds.HelloErr, new JsonObject
            {
                ["code"] = ErrorCodes.Protocol,
                ["message"] = $"kernel speaks protocol {ProtocolConstants.Version}, got {protocol}",
                ["hint"] = "stop the kernel and upgrade pd",
                ["retryable"] = false,
            })).ConfigureAwait(false);
            return false;
        }

        if (string.Equals(role, "extension", StringComparison.Ordinal))
        {
            var name = JsonUtil.String(data, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                await SendHelloErrorAsync(ErrorCodes.Protocol, "extension hello requires name", null).ConfigureAwait(false);
                return false;
            }

            var invocationId = JsonUtil.String(data, "invocationId");
            if (!_kernel.Supervisor.ValidateExtensionHello(name, invocationId, out var attachError))
            {
                await SendHelloErrorAsync(ErrorCodes.Protocol, attachError ?? $"extension '{name}' was rejected", "extensions must be started by the kernel").ConfigureAwait(false);
                return false;
            }

            _isExtension = true;
            _extensionName = name;
            _kernel.Sessions.MarkExtension(SessionId);
            _kernel.Supervisor.AttachConnection(name, this);
        }
        else
        {
            foreach (var feature in JsonUtil.Strings(JsonUtil.Array(data, "features")))
            {
                _features.Add(feature);
            }
        }

        var reply = Envelope.Make(Kinds.HelloOk, new JsonObject
        {
            ["protocol"] = ProtocolConstants.Version,
            ["protocolRange"] = new JsonArray(ProtocolConstants.Version, ProtocolConstants.Version),
            ["kernelVersion"] = _kernel.State.KernelVersion,
            ["kernelAbi"] = _kernel.State.Abi,
            ["stickId"] = _kernel.State.StickId,
            ["machineId"] = _kernel.State.MachineId,
            ["root"] = _kernel.State.Root,
            ["stableRoot"] = _kernel.State.StableRoot,
            ["features"] = new JsonArray(Features.Json, Features.Prompt, Features.Cancel, Features.Progress, Features.Subscribe, Features.ExtEvents, Features.Grants, Features.Topics),
            ["bootedAt"] = TimeUtil.Format(_kernel.State.BootedAt),
            ["limits"] = new JsonObject
            {
                ["maxFrame"] = ProtocolConstants.MaxFrameBytes,
                ["maxConcurrent"] = MaxConcurrentTasks,
            },
        });

        await SendAsync(reply).ConfigureAwait(false);

        // Subscribe only after hello.ok: the handshake reply must be the first message a peer sees.
        if (!_isExtension && WantsExtensionEvents)
        {
            _kernel.Sessions.SubscribeExtensions(SessionId);
        }

        _kernel.Logger.Debug("conn.hello", "handshake complete", new JsonObject
        {
            ["session"] = SessionId,
            ["role"] = _isExtension ? "extension" : "client",
            ["name"] = _isExtension ? _extensionName : null,
        });
        return true;
    }

    private async Task RunClientLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var frame = await FrameCodec.ReadFrameAsync(_stream, ProtocolConstants.MaxFrameBytes, _lifetime.Token).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            var envelope = Envelope.Parse(frame);
            switch (envelope.Kind)
            {
                case Kinds.Req:
                    _ = HandleRequestAsync(envelope);
                    break;
                case Kinds.Ctl:
                    await HandleCtlAsync(envelope).ConfigureAwait(false);
                    break;
                case Kinds.Ack:
                    break;
                default:
                    _kernel.Logger.Debug("conn.ignored", "ignoring envelope kind", new JsonObject { ["kind"] = envelope.Kind });
                    break;
            }
        }
    }

    private async Task HandleRequestAsync(Envelope request)
    {
        var argv = JsonUtil.Strings(JsonUtil.Array(request.Data, "argv")).ToArray();
        var opts = JsonUtil.Object(request.Data, "opts") ?? new JsonObject();
        var ctx = JsonUtil.Object(request.Data, "ctx") ?? new JsonObject();
        var id = request.Id;
        if (string.IsNullOrWhiteSpace(id) || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9._-]{1,64}$"))
        {
            // Client-supplied ids become intent file names and audit keys; never let them carry path syntax.
            id = _kernel.State.NextTaskId();
        }

        if (_kernel.State.Stopping)
        {
            await SendAsync(Envelope.Event(id, 1, new JsonObject
            {
                ["type"] = EventTypes.Done,
                ["ok"] = false,
                ["exitCode"] = ExitCodes.Cancelled,
                ["error"] = Error(ErrorCodes.Cancelled, "kernel is shutting down", "retry after it restarts"),
            })).ConfigureAwait(false);
            return;
        }

        TaskContext? task;
        var tooMany = false;
        lock (_tasks)
        {
            if (_tasks.ContainsKey(id))
            {
                task = null;
            }
            else if (_tasks.Count >= MaxConcurrentTasks)
            {
                task = null;
                tooMany = true;
            }
            else
            {
                task = new TaskContext(id, SessionId, argv, opts, ctx, request.Trace ?? string.Empty, this, _kernel.Sessions, _lifetime.Token);
                _tasks[id] = task;
            }
        }

        if (tooMany)
        {
            _kernel.Logger.Warn("task.busy", "too many concurrent requests on one connection", new JsonObject { ["session"] = SessionId, ["limit"] = MaxConcurrentTasks });
            await SendAsync(Envelope.Event(id, 1, new JsonObject
            {
                ["type"] = EventTypes.Done,
                ["ok"] = false,
                ["exitCode"] = ExitCodes.Busy,
                ["error"] = Error(ErrorCodes.Busy, $"too many concurrent requests (limit {MaxConcurrentTasks})", "wait for one to finish or open another connection"),
            })).ConfigureAwait(false);
            return;
        }

        if (task is null)
        {
            // Two live tasks with one id would cross their completion signals and get the extension
            // killed by the watchdog; reject the duplicate instead of silently replacing.
            _kernel.Logger.Warn("task.duplicate", "duplicate task id rejected", new JsonObject { ["task"] = id, ["session"] = SessionId });
            await SendAsync(Envelope.Event(id, 1, new JsonObject
            {
                ["type"] = EventTypes.Done,
                ["ok"] = false,
                ["exitCode"] = ExitCodes.Args,
                ["error"] = Error(ErrorCodes.BadArgs, $"duplicate task id '{id}'", "use a unique id per request"),
            })).ConfigureAwait(false);
            return;
        }

        _kernel.Sessions.RegisterTask(task);
        _kernel.State.EnterTask();
        _kernel.Logger.Debug("task.begin", "handling request", new JsonObject { ["task"] = id, ["cmd"] = argv.FirstOrDefault(), ["argc"] = argv.Length });
        try
        {
            var result = await _kernel.Router.ExecuteAsync(task).ConfigureAwait(false);
            _kernel.Logger.Debug("task.result", "router returned", new JsonObject { ["task"] = id, ["exit"] = result.ExitCode, ["doneEmitted"] = result.DoneEmitted });
            if (!result.DoneEmitted)
            {
                await task.DoneAsync(result.ExitCode == ExitCodes.Ok, result.ExitCode, result.Error, result.Summary, result.Metrics).ConfigureAwait(false);
                _kernel.Logger.Debug("task.done", "done event published", new JsonObject { ["task"] = id, ["exit"] = result.ExitCode });
            }
        }
        catch (OperationCanceledException)
        {
            await task.DoneAsync(false, ExitCodes.Cancelled, Error(ErrorCodes.Cancelled, "cancelled", null), "cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _kernel.Logger.Error("task.crash", "command handler threw", ex, new JsonObject { ["command"] = argv.FirstOrDefault(), ["task"] = id });
            await task.DoneAsync(false, ExitCodes.Internal, Error(ErrorCodes.Internal, ex.Message, null)).ConfigureAwait(false);
        }
        finally
        {
            _kernel.State.LeaveTask();
            lock (_tasks)
            {
                _tasks.Remove(id);
            }

            _kernel.Sessions.CompleteTask(task);
        }
    }

    private async Task HandleCtlAsync(Envelope ctl)
    {
        var action = JsonUtil.String(ctl.Data, "action") ?? string.Empty;
        switch (action)
        {
            case CtlActions.Ping:
                await SendAsync(Envelope.Make(Kinds.Ctl, new JsonObject
                {
                    ["action"] = CtlActions.Pong,
                    ["ts"] = JsonUtil.Int(ctl.Data, "ts"),
                })).ConfigureAwait(false);
                break;

            case CtlActions.Cancel:
                var taskId = ctl.Id ?? JsonUtil.String(ctl.Data, "task");
                if (taskId is not null)
                {
                    var key = _kernel.Sessions.ResolveKey(taskId, SessionId);
                    if (key is not null && _kernel.Sessions.FindTask(key) is { } cancelTask)
                    {
                        cancelTask.Cancel();
                    }
                }

                break;

            case CtlActions.Subscribe:
                var subscribeId = ctl.Id;
                var scope = JsonUtil.String(ctl.Data, "scope");
                if (string.Equals(scope, "ext", StringComparison.Ordinal))
                {
                    _kernel.Sessions.SubscribeExtensions(SessionId);
                }
                else if (scope is not null)
                {
                    if (_features.Contains(Features.Topics))
                    {
                        _kernel.Sessions.SubscribeTopic(scope, SessionId, this);
                    }
                    else
                    {
                        _kernel.Logger.Debug("topic.subscribe", "client did not announce the topics feature", new JsonObject { ["scope"] = scope });
                    }
                }
                else if (subscribeId is not null)
                {
                    // A client may only subscribe to its own tasks; ResolveKey without the session
                    // would let it eavesdrop on another session that happens to use the same id.
                    var subscribeKey = _kernel.Sessions.ResolveKey(subscribeId, SessionId);
                    if (subscribeKey is not null)
                    {
                        _kernel.Sessions.SubscribeTask(subscribeKey, SessionId, this);
                    }
                }

                break;

            case CtlActions.Unsubscribe:
                var unsubscribeId = ctl.Id;
                var unsubscribeScope = JsonUtil.String(ctl.Data, "scope");
                if (string.Equals(unsubscribeScope, "ext", StringComparison.Ordinal))
                {
                    _kernel.Sessions.UnsubscribeExtensions(SessionId);
                }
                else if (unsubscribeScope is not null)
                {
                    _kernel.Sessions.UnsubscribeTopic(unsubscribeScope, SessionId);
                }
                else if (unsubscribeId is not null)
                {
                    var unsubscribeKey = _kernel.Sessions.ResolveKey(unsubscribeId, SessionId) ?? unsubscribeId;
                    _kernel.Sessions.UnsubscribeTask(unsubscribeKey, SessionId);
                }

                break;

            case CtlActions.PromptReply:
                var promptTaskId = ctl.Id;
                if (promptTaskId is not null && !_kernel.Supervisor.DeliverPromptReply(promptTaskId, ctl.Data, SessionId))
                {
                    _kernel.Logger.Debug("prompt.reply", "prompt reply had no owner", new JsonObject { ["task"] = promptTaskId });
                }

                break;

            default:
                _kernel.Logger.Debug("ctl.ignored", "unknown ctl action", new JsonObject { ["action"] = action });
                break;
        }
    }

    private async Task RunExtensionLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var frame = await FrameCodec.ReadFrameAsync(_stream, ProtocolConstants.MaxFrameBytes, _lifetime.Token).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            var envelope = Envelope.Parse(frame);
            _kernel.Supervisor.TouchExtension(_extensionName);
            switch (envelope.Kind)
            {
                case Kinds.ExtReady:
                    _kernel.Supervisor.MarkReady(_extensionName, envelope);
                    break;
                case Kinds.Evt:
                    if (envelope.Data["type"] != null && JsonUtil.String(envelope.Data, "type") == EventTypes.Log)
                    {
                        _kernel.Supervisor.TryGetInvocationId(_extensionName, out var logInvocation);
                        _kernel.Logger.WriteExtensionBatch(_extensionName, new JsonArray(envelope.Data.DeepClone()), logInvocation, null);
                    }

                    if (envelope.Data["type"]?.GetValue<string>() == EventTypes.Ext)
                    {
                        await _kernel.Sessions.BroadcastExtensionEventAsync(envelope).ConfigureAwait(false);
                    }
                    else if (envelope.Id is not null)
                    {
                        // Extensions echo the invoke id, which is the session-scoped task key; legacy
                        // extensions that echo the wire id still work through the fallback lookup.
                        var task = _kernel.Sessions.FindTaskByExtensionId(envelope.Id);
                        if (task is null)
                        {
                            var legacyKey = _kernel.Sessions.ResolveKey(envelope.Id);
                            task = legacyKey is null ? null : _kernel.Sessions.FindTask(legacyKey);
                        }

                        if (task is not null)
                        {
                            envelope.Id = task.Id;
                            await _kernel.Sessions.PublishAsync(task.Key, envelope).ConfigureAwait(false);
                        }
                    }

                    break;
                case Kinds.Event:
                    var topic = JsonUtil.String(envelope.Data, "topic");
                    if (string.IsNullOrWhiteSpace(topic))
                    {
                        _kernel.Logger.Debug("event.topic", "extension event without a topic", new JsonObject { ["extension"] = _extensionName });
                        break;
                    }

                    envelope.Ts ??= TimeUtil.NowIso();
                    await _kernel.Sessions.PublishTopicAsync(topic, envelope).ConfigureAwait(false);
                    break;
                case Kinds.Ctl:
                    switch (JsonUtil.String(envelope.Data, "action"))
                    {
                        case CtlActions.LogWrite:
                            _kernel.Supervisor.TryGetInvocationId(_extensionName, out var invocationId);
                            _kernel.Logger.WriteExtensionBatch(_extensionName, JsonUtil.Array(envelope.Data, "records"), invocationId, null);
                            break;
                        case CtlActions.GrantRequest:
                            await _kernel.Supervisor.ForwardGrantRequestAsync(_extensionName, envelope).ConfigureAwait(false);
                            break;
                        case CtlActions.GrantReply:
                            if (envelope.Id is not null)
                            {
                                _kernel.Supervisor.DeliverGrantReply(envelope.Id, envelope.Data);
                            }

                            break;
                    }

                    break;
                case Kinds.Call:
                    await _kernel.Supervisor.ForwardCallAsync(_extensionName, envelope).ConfigureAwait(false);
                    break;
                case Kinds.CallOk:
                case Kinds.CallErr:
                    _kernel.Supervisor.RouteCallReply(_extensionName, envelope);
                    break;
                default:
                    _kernel.Logger.Debug("ext.ignored", "extension sent an unexpected kind", new JsonObject { ["kind"] = envelope.Kind, ["extension"] = _extensionName });
                    break;
            }
        }
    }

    private Task SendHelloErrorAsync(string code, string message, string? hint)
        => SendAsync(Envelope.Make(Kinds.HelloErr, new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
            ["hint"] = hint,
            ["retryable"] = false,
        }));

    private async Task TrySendHelloErrorAsync(string code, string message, string? hint)
    {
        try
        {
            await SendHelloErrorAsync(code, message, hint).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Peer already gone.
        }
    }

    internal static JsonObject Error(string code, string message, string? hint)
    {
        var error = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };

        if (hint is not null)
        {
            error["hint"] = hint;
        }

        return error;
    }
}
