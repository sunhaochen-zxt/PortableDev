using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;

namespace PortableDev.Shell;

/// <summary>
/// One command invocation: connect (starting the kernel if needed), handshake, send the request,
/// render the event stream, answer prompts and map the terminal event to an exit code.
/// </summary>
public sealed class PdShell : IDisposable
{
    private readonly ShellOptions _options;
    private readonly string _root;
    private readonly string _pipeName;
    private readonly EventRenderer _renderer;
    private KernelLauncher? _launcher;
    private PipeClient? _client;
    private string? _taskId;
    private bool _done;
    private int _exitCode = ExitCodes.Internal;
    private int? _childExitCode;
    private int _cancelRequested;
    private bool _disposed;
    private int? _preferExitCode;

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
        var (client, connectError) = noAutoStart
            ? (await PipeClient.TryConnectAsync(_pipeName, 500, _options.Trace, token).ConfigureAwait(false), null)
            : await ConnectAsync(token).ConfigureAwait(false);
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
            if (!await HandshakeAsync(token).ConfigureAwait(false))
            {
                return _exitCode;
            }

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
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            var client = await PipeClient.TryConnectAsync(_pipeName, 250, _options.Trace, token).ConfigureAwait(false);
            if (client is not null)
            {
                return (client, null);
            }

            if (launcher.ProcessId is null)
            {
                var detail = launcher.LastError.Trim();
                if (launcher.ExitCode is int exitCode)
                {
                    _preferExitCode = exitCode;
                }

                return (null, "kernel exited before listening" + (detail.Length > 0 ? ": " + detail : string.Empty));
            }

            await Task.Delay(100, token).ConfigureAwait(false);
        }

        return (null, "kernel did not start listening within 15s");
    }

    private async Task<bool> HandshakeAsync(CancellationToken token)
    {
        _ = token;
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
                ["ctx"] = BuildContext(),
            },
        };

        await _client!.SendAsync(hello).ConfigureAwait(false);
        var reply = await _client.ReceiveAsync().ConfigureAwait(false);
        if (reply is null)
        {
            Console.Error.WriteLine("kernel closed the connection during handshake");
            _exitCode = ExitCodes.Env;
            return false;
        }

        if (reply.Kind == Kinds.HelloErr)
        {
            var code = JsonUtil.String(reply.Data, "code") ?? ErrorCodes.Protocol;
            var message = JsonUtil.String(reply.Data, "message") ?? "kernel refused the connection";
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
                ["ctx"] = BuildContext(),
            },
        };

        await _client!.SendAsync(request).ConfigureAwait(false);
        return true;
    }

    private async Task<int> PumpAsync()
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

                    _renderer.Render(envelope.Data);
                    if (type == "done")
                    {
                        ReadDone(envelope.Data);
                        return FinalExitCode();
                    }

                    break;

                case Kinds.Ctl:
                    break;

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
