using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;

namespace PortableDev.ProcExt;

/// <summary>
/// The extension side of the kernel protocol (spec 4.1 / 5.1-5.3): connect to PD_PIPE, prove the
/// invocation id, answer ext.invoke / call frames and emit evt frames back.
/// </summary>
public sealed class ExtensionClient : IDisposable
{
    private const string ExtensionVersion = "0.0.1";

    private readonly string _name = Environment.GetEnvironmentVariable("PD_EXTENSION") ?? "proc";
    private readonly string _invocationId = Environment.GetEnvironmentVariable("PD_INVOCATION_ID") ?? string.Empty;
    private readonly string _pipeName = Environment.GetEnvironmentVariable("PD_PIPE") ?? string.Empty;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task> _inflight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private NamedPipeClientStream? _stream;
    private long _sequence;

    public static async Task<int> RunAsync()
    {
        using var client = new ExtensionClient();
        return await client.RunCoreAsync().ConfigureAwait(false);
    }

    private async Task<int> RunCoreAsync()
    {
        if (string.IsNullOrEmpty(_pipeName))
        {
            Console.Error.WriteLine("PD_PIPE is not set; proc must be started by pd-kernel");
            return ExitCodes.Args;
        }

        try
        {
            _stream = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await _stream.ConnectAsync(5000, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
            Console.Error.WriteLine("cannot connect to the kernel pipe: " + ex.Message);
            return ExitCodes.Env;
        }

        await SendAsync(Envelope.Make(Kinds.Hello, new JsonObject
        {
            ["protocol"] = ProtocolConstants.Version,
            ["role"] = "extension",
            ["name"] = _name,
            ["version"] = ExtensionVersion,
            ["features"] = new JsonArray(),
            ["invocationId"] = _invocationId,
        })).ConfigureAwait(false);

        var handshake = await ReadAsync().ConfigureAwait(false);
        if (handshake is null)
        {
            Console.Error.WriteLine("kernel closed the pipe during the handshake");
            return ExitCodes.Env;
        }

        if (handshake.Kind == Kinds.HelloErr)
        {
            Console.Error.WriteLine("kernel rejected the extension: "
                + (JsonUtil.String(handshake.Data, "message") ?? "unknown reason"));
            return ExitCodes.Env;
        }

        if (handshake.Kind != Kinds.HelloOk)
        {
            Console.Error.WriteLine("unexpected handshake reply: " + handshake.Kind);
            return ExitCodes.Env;
        }

        await SendAsync(Envelope.Make(Kinds.ExtReady, new JsonObject
        {
            ["name"] = _name,
            ["version"] = ExtensionVersion,
            ["protocol"] = ProtocolConstants.Version,
            ["capabilities"] = new JsonArray(
                Capability("proc.spawn", mutates: true),
                Capability("proc.snapshot", mutates: false),
                Capability("proc.kill", mutates: true),
                Capability("proc.system", mutates: false)),
        })).ConfigureAwait(false);

        await ReadLoopAsync().ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        return ExitCodes.Ok;
    }

    private async Task ReadLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            Envelope? envelope;
            try
            {
                envelope = await ReadAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException)
            {
                break;
            }

            if (envelope is null)
            {
                break;
            }

            switch (envelope.Kind)
            {
                case Kinds.Ctl:
                    await HandleControlAsync(envelope).ConfigureAwait(false);
                    break;
                case Kinds.ExtInvoke:
                    Track(envelope.Id, () => Commands.HandleInvokeAsync(this, envelope.Id!, envelope.Data));
                    break;
                case Kinds.Call:
                    Track(envelope.Id, () => Commands.HandleCallAsync(this, envelope.Id!, envelope.Data));
                    break;
                default:
                    // Unknown kinds are ignored on purpose (forward compatibility, spec 0).
                    break;
            }
        }
    }

    private void Track(string? id, Func<Task> body)
    {
        if (id is null)
        {
            return;
        }

        var task = Task.Run(body);
        _inflight[id] = task;
        _ = task.ContinueWith(
            finished => _inflight.TryRemove(id, out _),
            TaskScheduler.Default);
    }

    private async Task HandleControlAsync(Envelope envelope)
    {
        var action = JsonUtil.String(envelope.Data, "action");
        if (string.Equals(action, CtlActions.Ping, StringComparison.Ordinal))
        {
            await SendAsync(Envelope.Make(Kinds.Ctl, new JsonObject
            {
                ["action"] = CtlActions.Pong,
                ["ts"] = JsonUtil.String(envelope.Data, "ts"),
            })).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync()
    {
        var tasks = _inflight.Values.ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Kernel is going away; in-flight tasks are abandoned on purpose.
        }
    }

    public async Task SendAsync(Envelope envelope)
    {
        var stream = _stream;
        if (stream is null)
        {
            return;
        }

        var payload = envelope.ToBytes();
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteFrameAsync(stream, payload, _lifetime.Token).ConfigureAwait(false);
            await stream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The kernel is gone; drop the frame.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task EventAsync(string taskKey, string type, JsonObject payload)
    {
        payload["type"] = type;
        return SendAsync(new Envelope
        {
            Kind = Kinds.Evt,
            Id = taskKey,
            Seq = Interlocked.Increment(ref _sequence),
            Ts = TimeUtil.NowIso(),
            Data = payload,
        });
    }

    /// <summary>
    /// Publishes one key/value event (spec 4.4 / TaskContext.KvAsync): the text renderer prints the
    /// optional title and the "items" object, so callers must never flatten the pairs into the event.
    /// </summary>
    public Task KvAsync(string taskKey, string title, JsonObject items)
        => EventAsync(taskKey, EventTypes.Kv, new JsonObject
        {
            ["title"] = title,
            ["items"] = items.DeepClone(),
        });

    public Task LogAsync(string taskKey, string level, string message)
        => EventAsync(taskKey, EventTypes.Log, new JsonObject { ["level"] = level, ["msg"] = message });

    /// <summary>Publishes one topic event (spec 4.9/5.8): no task, no reply; the kernel fans it out.</summary>
    public Task TopicEventAsync(string topic, string eventName, JsonObject payload)
        => SendAsync(Envelope.Make(Kinds.Event, new JsonObject
        {
            ["topic"] = topic,
            ["event"] = eventName,
            ["payload"] = payload,
        }));

    /// <summary>Writes one structured record to the log provider (spec 5.3.1); fire and forget.</summary>
    public Task LogEventAsync(string evt, string level, string message, JsonObject? fields = null)
    {
        var priority = level switch
        {
            "error" => 3,
            "warn" => 4,
            "debug" => 7,
            _ => 6,
        };

        var record = new JsonObject
        {
            ["ts"] = TimeUtil.NowIso(),
            ["pri"] = priority,
            ["level"] = level,
            ["msg"] = message,
            ["evt"] = evt,
        };

        if (fields is not null)
        {
            foreach (var (key, value) in fields)
            {
                record[key] = value?.DeepClone();
            }
        }

        return SendAsync(Envelope.Make(Kinds.Ctl, new JsonObject
        {
            ["action"] = CtlActions.LogWrite,
            ["schema"] = 1,
            ["records"] = new JsonArray(record),
        }));
    }

    public Task DoneAsync(string taskKey, int exitCode, string? summary = null, JsonObject? error = null, JsonObject? metrics = null)
    {
        var data = new JsonObject
        {
            ["type"] = EventTypes.Done,
            ["ok"] = exitCode == ExitCodes.Ok,
            ["exitCode"] = exitCode,
        };

        if (summary is not null)
        {
            data["summary"] = summary;
        }

        if (error is not null)
        {
            data["error"] = error;
        }

        if (metrics is not null)
        {
            data["metrics"] = metrics;
        }

        return SendAsync(new Envelope
        {
            Kind = Kinds.Evt,
            Id = taskKey,
            Seq = Interlocked.Increment(ref _sequence),
            Ts = TimeUtil.NowIso(),
            Data = data,
        });
    }

    public Task CallOkAsync(string callId, JsonNode? result)
    {
        var envelope = Envelope.Make(Kinds.CallOk, new JsonObject { ["result"] = result?.DeepClone() });
        envelope.Id = callId;
        return SendAsync(envelope);
    }

    public Task CallErrorAsync(string callId, string code, string message)
    {
        var envelope = Envelope.Make(Kinds.CallErr, new JsonObject { ["code"] = code, ["message"] = message });
        envelope.Id = callId;
        return SendAsync(envelope);
    }

    private async Task<Envelope?> ReadAsync()
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        var frame = await FrameCodec.ReadFrameAsync(stream, ProtocolConstants.MaxFrameBytes, _lifetime.Token)
            .ConfigureAwait(false);
        return frame is null ? null : Envelope.Parse(frame);
    }

    private static JsonObject Capability(string name, bool mutates) => new()
    {
        ["name"] = name,
        ["version"] = 1,
        ["mutates"] = mutates,
    };

    public void Dispose()
    {
        _lifetime.Cancel();
        _stream?.Dispose();
        _writeGate.Dispose();
        _lifetime.Dispose();
    }
}
