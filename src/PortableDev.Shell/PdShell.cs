using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;

using PortableDev.Proc;

namespace PortableDev.Shell;

/// <summary>
/// One command invocation: connect (starting the kernel if needed), handshake, send the request,
/// render the event stream, answer prompts and map the terminal event to an exit code.
/// </summary>
public sealed class PdShell : IDisposable
{
    private static readonly TimeSpan ExecDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Fallback cap for the handshake when kernel.startTimeout is absent or larger.</summary>
    private static readonly TimeSpan MaxHandshakeTimeout = TimeSpan.FromSeconds(20);

    private readonly ShellOptions _options;
    private readonly string _root;
    private readonly string _pipeName;
    private readonly EventRenderer _renderer;
    private KernelLauncher? _launcher;
    private PipeClient? _client;
    private string? _taskId;
    private bool _done;
    private int _exitCode = ExitCodes.Internal;
    private bool _handshakeTransient;
    private string? _handshakeMessage;
    private int? _childExitCode;
    private int _cancelRequested;
    private bool _disposed;
    private int? _preferExitCode;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _execCts = new();
    private readonly object _execTaskGate = new();
    private readonly Dictionary<string, Task> _execTasks = new(StringComparer.Ordinal);
    private JsonObject? _context;

    public PdShell(ShellOptions options, string root, string pipeName)
    {
        _options = options;
        _root = root;
        _pipeName = pipeName;
        _renderer = new EventRenderer(Console.Out, options.Json, options.Quiet, options.Verbose, !options.NoColor);
    }

    /// <summary>True once the request has been sent; false during locate/connect/handshake.</summary>
    public bool HasTask => _taskId is not null;

    /// <summary>Ctrl+C: cancel the running task once; a second press exits immediately.</summary>
    public void RequestCancel()
    {
        if (Interlocked.Exchange(ref _cancelRequested, 1) != 0)
        {
            return;
        }

        var client = _client;
        var taskId = _taskId;
        if (client is null || taskId is null)
        {
            return;
        }

        _renderer.Note("cancelling...");
        _ = Task.Run(async () =>
        {
            try
            {
                await client.SendAsync(new Envelope
                {
                    Kind = Kinds.Ctl,
                    Id = taskId,
                    Ts = TimeUtil.NowIso(),
                    Data = new JsonObject { ["action"] = CtlActions.Cancel },
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // The kernel is already gone; the caller still gets an exit code.
            }
        });
    }

    public async Task<int> RunAsync(CancellationToken token)
    {
        // Probing or stopping must never start a daemon; otherwise a failed CI check or a
        // "why is this dead" look leaves a kernel behind. Only real commands auto-start.
        var noAutoStart = IsCommand("kernel", "status") || IsCommand("kernel", "stop");
        PipeClient? client = null;
        string? connectError = null;
        for (var attempt = 0; ; attempt++)
        {
            (client, connectError) = noAutoStart
                ? (await PipeClient.TryConnectAsync(_pipeName, 500, _options.Trace, token).ConfigureAwait(false), null)
                : await ConnectAsync(token).ConfigureAwait(false);
            if (client is null)
            {
                break;
            }

            _client = client;
            if (await HandshakeAsync(token).ConfigureAwait(false))
            {
                break;
            }

            client.Dispose();
            _client = null;
            if (!_handshakeTransient || attempt >= 10)
            {
                if (_handshakeMessage is { Length: > 0 })
                {
                    Console.Error.WriteLine(_handshakeMessage);
                }

                return _exitCode;
            }

            // A kernel that is still shutting down accepts the pipe and then closes it; wait for it
            // to exit and let the next attempt autostart a fresh one.
            await Task.Delay(250, token).ConfigureAwait(false);
        }

        if (client is null && noAutoStart)
        {
            if (IsCommand("kernel", "stop"))
            {
                _renderer.Note("kernel is not running");
                return ExitCodes.Ok;
            }

            _renderer.Error(ErrorCodes.Env, "kernel is not running", "start it with any command");
            return ExitCodes.Env;
        }

        if (client is null)
        {
            if (token.IsCancellationRequested)
            {
                return ExitCodes.Cancelled;
            }

            Console.Error.WriteLine(connectError);
            return _preferExitCode ?? ExitCodes.Env;
        }

        _client = client;
        try
        {
            if (!await SendRequestAsync().ConfigureAwait(false))
            {
                return _exitCode;
            }

            return await PumpAsync().ConfigureAwait(false);
        }
        finally
        {
            client.Dispose();
            _client = null;
        }
    }

    private bool IsCommand(string first, string second)
        => _options.Forwarded.Count >= 2 &&
           string.Equals(_options.Forwarded[0], first, StringComparison.Ordinal) &&
           string.Equals(_options.Forwarded[1], second, StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _launcher?.Dispose();
    }

    private async Task<(PipeClient? Client, string? Error)> ConnectAsync(CancellationToken token)
    {
        var first = await PipeClient.TryConnectAsync(_pipeName, 300, _options.Trace, token).ConfigureAwait(false);
        if (first is not null)
        {
            return (first, null);
        }

        var (launcher, launchError) = KernelLauncher.Start(_root, _options.Trace);
        if (launcher is null)
        {
            return (null, launchError);
        }

        _launcher = launcher;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string? launcherDetail = null;
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            var client = await PipeClient.TryConnectAsync(_pipeName, 250, _options.Trace, token).ConfigureAwait(false);
            if (client is not null)
            {
                return (client, null);
            }

            // Losing the single-instance race is normal: the launcher may exit because a sibling
            // process won and is about to listen. Remember its status (busy/env exit codes) but keep
            // polling until the deadline instead of failing on the first exit.
            if (launcher.ProcessId is null)
            {
                if (launcherDetail is null)
                {
                    launcherDetail = launcher.LastError.Trim();
                }

                if (launcher.ExitCode is int exitCode && exitCode != ExitCodes.Ok)
                {
                    _preferExitCode = exitCode;
                }
            }

            try
            {
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (token.IsCancellationRequested)
        {
            return (null, null);
        }

        return (null, "kernel did not start listening within 15s"
            + (launcherDetail is { Length: > 0 } ? ": " + launcherDetail : string.Empty));
    }

    private async Task<bool> HandshakeAsync(CancellationToken token)
    {
        _handshakeTransient = false;
        _handshakeMessage = null;
        var hello = new Envelope
        {
            Kind = Kinds.Hello,
            Ts = TimeUtil.NowIso(),
            Data = new JsonObject
            {
                ["protocol"] = ProtocolConstants.Version,
                ["client"] = "cli",
                ["clientVersion"] = ProtocolConstants.KernelVersion,
                ["pid"] = Environment.ProcessId,
                ["session"] = "s-" + Guid.NewGuid().ToString("N")[..8],
                ["features"] = new JsonArray(
                    Features.Json,
                    Features.Ansi,
                    Features.Prompt,
                    Features.Progress,
                    Features.Unicode,
                    Features.Link),
                ["ctx"] = Context.DeepClone(),
            },
        };

        try
        {
            await _client!.SendAsync(hello).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _handshakeTransient = true;
            _handshakeMessage = "kernel closed the connection during handshake";
            _exitCode = ExitCodes.Env;
            return false;
        }

        var timeout = ResolveHandshakeTimeout();
        Envelope? reply;
        try
        {
            // A peer that accepts the pipe but never answers must not hang the shell forever.
            reply = await _client!.ReceiveAsync().WaitAsync(timeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ClosePipe();
            _handshakeTransient = true;
            _handshakeMessage = "kernel did not answer the handshake within " + TimeUtil.FormatDuration(timeout);
            _exitCode = ExitCodes.Env;
            return false;
        }
        catch (OperationCanceledException)
        {
            ClosePipe();
            _exitCode = ExitCodes.Cancelled;
            return false;
        }

        if (reply is null)
        {
            if (token.IsCancellationRequested)
            {
                _exitCode = ExitCodes.Cancelled;
                return false;
            }

            _handshakeTransient = true;
            _handshakeMessage = "kernel closed the connection during handshake";
            _exitCode = ExitCodes.Env;
            return false;
        }

        if (reply.Kind == Kinds.HelloErr)
        {
            var code = JsonUtil.String(reply.Data, "code") ?? ErrorCodes.Protocol;
            var message = JsonUtil.String(reply.Data, "message") ?? "kernel refused the connection";
            if (string.Equals(code, "ERR_CANCELLED", StringComparison.Ordinal) || string.Equals(code, ErrorCodes.Busy, StringComparison.Ordinal))
            {
                // The old kernel is still shutting down; retry once it is gone.
                _handshakeTransient = true;
                _handshakeMessage = message;
                _exitCode = ExitCodes.ForError(code);
                return false;
            }

            _renderer.Error(code, message, JsonUtil.String(reply.Data, "hint"));
            _exitCode = ExitCodes.ForError(code);
            return false;
        }

        if (reply.Kind != Kinds.HelloOk)
        {
            _renderer.Error(ErrorCodes.Protocol, "unexpected handshake reply: " + reply.Kind, null);
            _exitCode = ExitCodes.Env;
            return false;
        }

        if (_options.Trace)
        {
            Console.Error.WriteLine(
                "[pd] kernel " + (JsonUtil.String(reply.Data, "kernelVersion") ?? "?") +
                " abi " + (JsonUtil.Int(reply.Data, "kernelAbi")?.ToString(CultureInfo.InvariantCulture) ?? "?") +
                " stick " + (JsonUtil.String(reply.Data, "stickId") ?? "?"));
        }

        return true;
    }

    private async Task<bool> SendRequestAsync()
    {
        if (_options.RawJson is not null)
        {
            try
            {
                var parsed = Envelope.Parse(Encoding.UTF8.GetBytes(_options.RawJson));
                _taskId = parsed.Id ?? "r-raw";
                await _client!.SendAsync(parsed).ConfigureAwait(false);
                return true;
            }
            catch (ProtocolException ex)
            {
                _renderer.Error(ErrorCodes.BadArgs, ex.Message, "--raw expects one JSON envelope");
                _exitCode = ExitCodes.Args;
                return false;
            }
        }

        _taskId = "r" + Guid.NewGuid().ToString("N")[..6];
        var request = new Envelope
        {
            Kind = Kinds.Req,
            Id = _taskId,
            Ts = TimeUtil.NowIso(),
            Data = new JsonObject
            {
                ["argv"] = new JsonArray(_options.Forwarded.Select(argument => (JsonNode?)JsonValue.Create(argument)).ToArray()),
                ["opts"] = new JsonObject
                {
                    ["json"] = _options.Json,
                    ["yes"] = _options.Yes,
                    ["quiet"] = _options.Quiet,
                    ["verbose"] = _options.Verbose,
                    ["color"] = !_options.NoColor,
                },
                ["ctx"] = Context.DeepClone(),
            },
        };

        await _client!.SendAsync(request).ConfigureAwait(false);
        return true;
    }

    private async Task<int> PumpAsync()
    {
        try
        {
            while (true)
            {
                var envelope = await _client!.ReceiveAsync().ConfigureAwait(false);
                if (envelope is null)
                {
                    if (!_done)
                    {
                        _renderer.Error(ErrorCodes.Io, "kernel disconnected before the command finished", null);
                        return ExitCodes.Internal;
                    }

                    break;
                }

                switch (envelope.Kind)
                {
                    case Kinds.Evt:
                        var type = JsonUtil.String(envelope.Data, "type");
                        if (type == "prompt")
                        {
                            await HandlePromptAsync(envelope.Data).ConfigureAwait(false);
                            continue;
                        }

                        if (type == "exec")
                        {
                            // The kernel relays a client-side launch request (spec 4.7 exec channel):
                            // the shell owns the terminal, so it spawns and reports the exit code back.
                            // Awaiting it here would block the pump from reading ctl cancel, prompts,
                            // done frames and pipe EOF, so it runs as a tracked background task.
                            StartExec(envelope.Id, envelope.Data);
                            continue;
                        }

                        _renderer.Render(envelope.Data);
                        if (type == "done")
                        {
                            ReadDone(envelope.Data);
                            return FinalExitCode();
                        }

                        break;

                    case Kinds.Ctl:
                    {
                        var action = JsonUtil.String(envelope.Data, "action");
                        if (string.Equals(action, CtlActions.Cancel, StringComparison.Ordinal)
                            && envelope.Id is not null
                            && _execCts.TryGetValue(envelope.Id, out var pending))
                        {
                            try { pending.Cancel(); } catch { }
                        }

                        break;
                    }

                    default:
                        if (_options.Trace)
                        {
                            Console.Error.WriteLine("[pd] ignoring envelope kind " + envelope.Kind);
                        }

                        break;
                }
            }

            return FinalExitCode();
        }
        finally
        {
            // The pump is done reading, so any child launched for this request is now orphaned:
            // cancel them and give their reply frames a bounded window before the pipe closes.
            await DrainExecTasksAsync().ConfigureAwait(false);
        }
    }

    private void StartExec(string? taskId, JsonObject request)
    {
        var key = taskId ?? string.Empty;
        var cts = new CancellationTokenSource();

        // Register the cts before the task starts so a ctl cancel arriving while the child is still
        // spawning is routed instead of being dropped.
        _execCts[key] = cts;

        var task = Task.Run(() => HandleExecAsync(taskId, request, cts), CancellationToken.None);
        lock (_execTaskGate)
        {
            _execTasks[key] = task;
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_execTaskGate)
                {
                    if (_execTasks.TryGetValue(key, out var current) && ReferenceEquals(current, completed))
                    {
                        _execTasks.Remove(key);
                    }
                }

                if (completed.IsFaulted)
                {
                    _ = completed.Exception; // observe: a faulted exec task must not go unhandled
                    if (_options.Trace)
                    {
                        var failure = completed.Exception?.GetBaseException();
                        Console.Error.WriteLine("[pd] exec task failed: " + (failure?.Message ?? "unknown"));
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void CancelExecTasks()
    {
        foreach (var pair in _execCts)
        {
            try
            {
                pair.Value.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The exec task already finished and disposed its cts.
            }
        }
    }

    private async Task DrainExecTasksAsync()
    {
        CancelExecTasks();

        Task[] tasks;
        lock (_execTaskGate)
        {
            tasks = _execTasks.Values.ToArray();
        }

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(ExecDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (_options.Trace)
            {
                Console.Error.WriteLine("[pd] exec child did not stop within " + TimeUtil.FormatDuration(ExecDrainTimeout));
            }
        }
        catch (Exception ex)
        {
            // A child that failed to stop cleanly must not hide the command result.
            if (_options.Trace)
            {
                Console.Error.WriteLine("[pd] exec task failed during shutdown: " + ex.GetBaseException().Message);
            }
        }
    }

    private async Task HandleExecAsync(string? taskId, JsonObject request, CancellationTokenSource cts)
    {
        var execId = JsonUtil.String(request, "execId") ?? string.Empty;
        var exitCode = ExitCodes.Internal;
        JsonObject? error = null;
        var ctsKey = taskId ?? string.Empty;
        var cancelled = false;

        try
        {
            var command = JsonUtil.String(request, "command");
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new InvalidOperationException("exec request has no command");
            }

            var arguments = JsonUtil.Strings(JsonUtil.Array(request, "args") ?? new JsonArray()).ToArray();
            var workingDirectory = JsonUtil.String(request, "cwd");
            var detached = JsonUtil.Bool(request, "detach") == true;
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (JsonUtil.Object(request, "env") is { } env)
            {
                foreach (var (key, value) in env)
                {
                    var text = JsonUtil.StringValue(value);
                    if (text is not null)
                    {
                        environment[key] = text;
                    }
                }
            }

            // In --json mode the child's stdout goes to our stderr so the NDJSON stream stays clean.
            using var stdio = StdioPipes.FromCurrentProcess(_options.Json);
            var spec = new ProcessStartSpec
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                Environment = environment.Count == 0 ? null : environment,
                StandardInput = stdio.Stdin,
                StandardOutput = stdio.Stdout,
                StandardError = stdio.Stderr,
                Detached = detached,
                DisableArgumentQuoting = JsonUtil.Bool(request, "verbatimArgs") == true,
            };

            var (process, job) = ProcessSpawner.Spawn(spec, detached ? null : new JobLimits());
            using (process)
            using (job)
            {
                if (detached)
                {
                    exitCode = ExitCodes.Ok;
                }
                else
                {
                    // Wait off the pump thread so incoming cancel/disconnect keeps flowing.
                    try
                    {
                        await Task.Run(() =>
                        {
                            while (!process.WaitForExit(TimeSpan.FromMilliseconds(200)))
                            {
                                cts.Token.ThrowIfCancellationRequested();
                            }
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                    }

                    if (cancelled || cts.Token.IsCancellationRequested)
                    {
                        cancelled = true;
                        TerminateChild(process, job);
                    }
                    else if (!process.TryGetExitCode(out exitCode))
                    {
                        exitCode = ExitCodes.Internal;
                    }
                }
            }

            if (cancelled)
            {
                exitCode = ExitCodes.Cancelled;
                error = new JsonObject { ["code"] = ErrorCodes.Cancelled, ["message"] = "cancelled" };
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            exitCode = ExitCodes.Internal;
            error = new JsonObject { ["code"] = ErrorCodes.Io, ["message"] = ex.Message };
        }
        finally
        {
            if (_execCts.TryGetValue(ctsKey, out var current) && ReferenceEquals(current, cts))
            {
                _execCts.TryRemove(ctsKey, out _);
            }

            cts.Dispose();
        }

        var replyData = new JsonObject
        {
            ["action"] = CtlActions.ExecReply,
            ["execId"] = execId,
            ["exitCode"] = exitCode,
        };
        if (error is not null)
        {
            replyData["error"] = error;
        }

        try
        {
            await _client!.SendAsync(new Envelope
            {
                Kind = Kinds.Ctl,
                Id = taskId,
                Ts = TimeUtil.NowIso(),
                Data = replyData,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Kernel gone; nothing to report.
        }
    }

    /// <summary>Kills the child's whole tree; a job terminate is the only reliable tree kill.</summary>
    private static void TerminateChild(SpawnedProcess process, JobObject? job)
    {
        try
        {
            job?.Terminate();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The job may already be gone; the direct kill below still runs.
        }

        process.Kill();
        _ = process.WaitForExit(TimeSpan.FromSeconds(1));
    }

    private void ReadDone(JsonObject data)
    {
        _done = true;
        _exitCode = JsonUtil.Int(data, "exitCode") ?? ExitCodes.Internal;
        var metrics = JsonUtil.Object(data, "metrics");
        _childExitCode = JsonUtil.Int(metrics, "childExitCode");
    }

    private int FinalExitCode()
        => _options.Propagate && _childExitCode is not null ? _childExitCode.Value : _exitCode;

    private async Task HandlePromptAsync(JsonObject prompt)
    {
        var promptId = JsonUtil.String(prompt, "promptId") ?? string.Empty;
        var kind = JsonUtil.String(prompt, "kind") ?? "input";
        JsonNode? value;

        if (_options.Yes)
        {
            value = kind == "confirm" ? JsonValue.Create(true) : prompt["default"]?.DeepClone();
        }
        else if (Console.IsInputRedirected || _options.Json)
        {
            _renderer.Note("non-interactive: replying null");
            value = null;
        }
        else
        {
            _renderer.Prompt(prompt);
            var secret = JsonUtil.Bool(prompt, "secret") ?? false;
            string? line;
            if (secret && !Console.IsInputRedirected)
            {
                // Secret prompt: read without echoing to the terminal.
                var buffer = new StringBuilder();
                while (true)
                {
                    var keyInfo = Console.ReadKey(intercept: true);
                    if (keyInfo.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        break;
                    }

                    if (keyInfo.Key == ConsoleKey.Backspace)
                    {
                        if (buffer.Length > 0)
                        {
                            buffer.Length -= 1;
                        }

                        continue;
                    }

                    if (!char.IsControl(keyInfo.KeyChar))
                    {
                        buffer.Append(keyInfo.KeyChar);
                    }
                }

                line = buffer.ToString();
            }
            else
            {
                line = Console.ReadLine();
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                value = prompt["default"]?.DeepClone();
            }
            else
            {
                value = kind switch
                {
                    "confirm" => JsonValue.Create(IsAffirmative(line)),
                    "multiselect" => new JsonArray(line
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(part => (JsonNode?)JsonValue.Create(part))
                        .ToArray()),
                    _ => JsonValue.Create(line),
                };
            }
        }

        await _client!.SendAsync(new Envelope
        {
            Kind = Kinds.Ctl,
            Id = _taskId,
            Ts = TimeUtil.NowIso(),
            Data = new JsonObject
            {
                ["action"] = CtlActions.PromptReply,
                ["promptId"] = promptId,
                ["value"] = value,
            },
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// The ctx block is identical for hello and request: build it once (so --trace-env writes one
    /// snapshot) and hand each frame its own clone, because a JsonNode has a single parent.
    /// </summary>
    private JsonObject Context => _context ??= BuildContext();

    /// <summary>
    /// kernel.startTimeout from config\settings.json, capped at 20s: a silent kernel must not hang
    /// the handshake forever.
    /// </summary>
    private TimeSpan ResolveHandshakeTimeout()
    {
        var configured = ReadStartTimeout();
        if (configured is null || configured.Value <= TimeSpan.Zero)
        {
            return MaxHandshakeTimeout;
        }

        return configured.Value > MaxHandshakeTimeout ? MaxHandshakeTimeout : configured.Value;
    }

    private TimeSpan? ReadStartTimeout()
    {
        try
        {
            var path = Path.Combine(_root, "config", "settings.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var document = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            return TimeUtil.ParseDuration(JsonUtil.String(JsonUtil.Object(document, "kernel"), "startTimeout"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Closes the transport after a failed handshake so a half-open pipe cannot linger.</summary>
    private void ClosePipe()
    {
        try
        {
            _client?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Already gone.
        }
    }

    private JsonObject BuildContext()
    {
        var environment = new JsonObject();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            environment[name] = Convert.ToString(entry.Value, CultureInfo.InvariantCulture);
        }

        if (_options.TraceEnv)
        {
            var tracePath = Path.Combine(Path.GetTempPath(), "pd-env-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".json");
            File.WriteAllText(tracePath, environment.ToJsonString());
            Console.Error.WriteLine("[pd] environment snapshot written to " + tracePath);
        }

        return new JsonObject
        {
            ["cwd"] = Directory.GetCurrentDirectory(),
            ["env"] = environment,
            ["tty"] = new JsonObject
            {
                ["cols"] = SafeDimension(width: true),
                ["rows"] = SafeDimension(width: false),
                ["color"] = !_options.NoColor && !Console.IsOutputRedirected,
                ["utf8"] = true,
            },
            ["locale"] = CultureInfo.CurrentUICulture.Name,
            ["timezone"] = TimeZoneInfo.Local.Id,
            ["user"] = Environment.UserName,
            ["interactive"] = !Console.IsInputRedirected,
        };
    }

    private static int SafeDimension(bool width)
    {
        try
        {
            return width ? Console.WindowWidth : Console.WindowHeight;
        }
        catch (IOException)
        {
            return width ? 80 : 25;
        }
    }

    private static bool IsAffirmative(string text)
        => text.Trim().ToLowerInvariant() is "y" or "yes" or "true" or "1" or "是" or "好";
}
