using System.Text.Json.Nodes;
using PortableDev.Core.Logging;
using PortableDev.Core.Protocol;
using PortableDev.Kernel.Transport;

namespace PortableDev.Kernel.Dispatch;

/// <summary>One command invocation; owns its event sequence and completion signal.</summary>
public sealed class TaskContext
{
    private readonly SessionRegistry _sessions;
    private readonly CancellationTokenSource _cts;
    private long _seq;
    private long _lastActivity = Environment.TickCount64;

    public TaskContext(string id, string sessionId, string[] argv, JsonObject opts, JsonObject clientCtx, string trace, IMessageSink sink, SessionRegistry sessions, CancellationToken sessionToken)
    {
        Id = id;
        SessionId = sessionId;
        Key = sessionId + "/" + id;
        // What the extension sees: unique across sessions, and safe as a file name (no path separators).
        ExtensionId = sessionId + "-" + id;
        Argv = argv;
        Opts = opts;
        ClientCtx = clientCtx;
        Trace = trace;
        Sink = sink;
        _sessions = sessions;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        Completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public string Id { get; }

    /// <summary>Session-scoped registry key: two clients may both use id \"r1\" without colliding.</summary>
    public string Key { get; }

    /// <summary>Path-safe, globally unique id handed to extensions (echoed back on every event).</summary>
    public string ExtensionId { get; }

    public string SessionId { get; }

    public string[] Argv { get; }

    public JsonObject Opts { get; }

    public JsonObject ClientCtx { get; }

    public string Trace { get; }

    public IMessageSink Sink { get; }

    public CancellationToken Token => _cts.Token;

    public TaskCompletionSource<CommandResult> Completion { get; }

    /// <summary>True only when the extension itself published a `done` for this task.</summary>
    public bool DoneFromExtension { get; private set; }

    public void MarkDoneFromExtension() => DoneFromExtension = true;

    /// <summary>Records that the extension is still alive; the watchdog measures silence, not total time.</summary>
    public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    public TimeSpan SinceActivity => TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _lastActivity)));

    public void Cancel()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    public async Task EmitAsync(string type, JsonObject data)
    {
        var seq = Interlocked.Increment(ref _seq);
        var payload = new JsonObject
        {
            ["type"] = type,
        };

        foreach (var (name, value) in data)
        {
            if (!payload.ContainsKey(name))
            {
                payload[name] = value?.DeepClone();
            }
        }

        var envelope = Envelope.Event(Id, seq, payload);
        envelope.Trace = Trace;
        await _sessions.PublishAsync(Key, envelope).ConfigureAwait(false);
    }

    public Task LogAsync(LogLevel level, string message, JsonObject? fields = null)
    {
        var data = new JsonObject
        {
            ["level"] = LogLevels.ToText(level),
            ["msg"] = message,
        };

        if (fields is not null)
        {
            foreach (var (name, value) in fields)
            {
                if (!data.ContainsKey(name))
                {
                    data[name] = value?.DeepClone();
                }
            }
        }

        return EmitAsync(EventTypes.Log, data);
    }

    public Task StepAsync(string stepId, string title, string state)
        => EmitAsync(EventTypes.Step, new JsonObject
        {
            ["stepId"] = stepId,
            ["title"] = title,
            ["state"] = state,
        });

    public Task KvAsync(string title, JsonObject items)
        => EmitAsync(EventTypes.Kv, new JsonObject
        {
            ["title"] = title,
            ["items"] = items.DeepClone(),
        });

    public long NextSeq() => Interlocked.Increment(ref _seq);

    /// <summary>Publishes the terminal `done` event (spec 4.3: it must be the last event of the task).</summary>
    public async Task DoneAsync(bool ok, int exitCode, JsonObject? error = null, string? summary = null, JsonObject? metrics = null)
    {
        var data = new JsonObject
        {
            ["type"] = EventTypes.Done,
            ["ok"] = ok,
            ["exitCode"] = exitCode,
        };

        if (summary is not null)
        {
            data["summary"] = summary;
        }

        if (error is not null)
        {
            data["error"] = error.DeepClone();
        }

        if (metrics is not null)
        {
            data["metrics"] = metrics.DeepClone();
        }

        var envelope = Envelope.Event(Id, NextSeq(), data);
        envelope.Trace = Trace;
        await _sessions.PublishAsync(Key, envelope).ConfigureAwait(false);
    }

    public void Complete(int exitCode, JsonObject? error = null, string? summary = null, JsonObject? metrics = null)
        => Completion.TrySetResult(new CommandResult(exitCode, error, summary, metrics));
}

public sealed record CommandResult(int ExitCode, JsonObject? Error, string? Summary, JsonObject? Metrics, bool DoneEmitted = false);
