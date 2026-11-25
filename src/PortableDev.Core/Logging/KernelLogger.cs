using System.Text.Json.Nodes;
using PortableDev.Core.Util;

namespace PortableDev.Core.Logging;

/// <summary>
/// The kernel's own logger. It builds contract records, filters by level, and fans out:
/// provider first (when attached), otherwise the fallback file. Extension batches are sanitized here,
/// before they can reach any sink.
/// </summary>
public sealed class KernelLogger : ILogSink, IDisposable
{
    private readonly FileLogSink _file;
    private readonly LogProviderClient _provider;
    private readonly string _bootId;
    private readonly string _stickId;
    private readonly string _machineId;
    private long _seq;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _unitSeq = new(StringComparer.Ordinal);
    private bool _disposed;

    public KernelLogger(LogOptions fileOptions, string bootId, string stickId, string machineId)
    {
        _file = new FileLogSink(fileOptions);
        _bootId = bootId;
        _stickId = stickId;
        _machineId = machineId;
        _provider = new LogProviderClient(this);
    }

    public LogLevel MinLevel { get; set; } = LogLevel.Info;

    public string BootId => _bootId;

    public string FallbackPath => _file.FilePath;

    public LogProviderClient Provider => _provider;

    /// <summary>Wired by the kernel host to `ExtensionSupervisor.TrySend`.</summary>
    public Func<string, PortableDev.Core.Protocol.Envelope, bool>? SendToProvider { get; set; }

    public bool TrySendToProvider(string provider, PortableDev.Core.Protocol.Envelope envelope)
        => SendToProvider?.Invoke(provider, envelope) ?? false;

    public void WriteToFallback(LogRecord record) => _file.Write(record);

    public bool IsEnabled(LogLevel level) => level >= MinLevel;

    private long NextUnitSeq(string unit) => _unitSeq.AddOrUpdate(unit, 1, (_, current) => current + 1);

    public void Debug(string evt, string? message = null, JsonObject? fields = null, string? trace = null, string? task = null)
        => Write(LogLevel.Debug, evt, message, fields, trace, task);

    public void Info(string evt, string? message = null, JsonObject? fields = null, string? trace = null, string? task = null)
        => Write(LogLevel.Info, evt, message, fields, trace, task);

    public void Warn(string evt, string? message = null, JsonObject? fields = null, string? trace = null, string? task = null)
        => Write(LogLevel.Warn, evt, message, fields, trace, task);

    public void Error(string evt, string? message = null, Exception? exception = null, JsonObject? fields = null, string? trace = null, string? task = null)
    {
        if (exception is not null)
        {
            fields ??= new JsonObject();
            fields["exception"] = exception.GetType().Name;
            fields["exceptionMessage"] = exception.Message;
        }

        Write(LogLevel.Error, evt, message, fields, trace, task);
    }

    public void Write(LogLevel level, string evt, string? message, JsonObject? fields = null, string? trace = null, string? task = null)
    {
        if (!IsEnabled(level))
        {
            return;
        }

        var dropped = LogRedaction.Validate(fields);
        var record = new LogRecord
        {
            Ts = TimeUtil.NowIso(),
            Pri = LogRecord.Priority(level),
            Level = LogLevels.ToText(level),
            Msg = message,
            Evt = evt,
            Seq = Interlocked.Increment(ref _seq),
            BootId = _bootId,
            Unit = "kernel",
            Pid = Environment.ProcessId,
            StickId = _stickId,
            MachineId = _machineId,
            Transport = "kernel",
            Trace = trace,
            Task = task,
            Fields = fields,
        };

        Write(record);

        if (dropped > 0)
        {
            Write(LogLevel.Warn, "log.sanitized", "a whole environment snapshot was dropped from a log record", new JsonObject
            {
                ["dropped"] = dropped,
            });
        }
    }

    public void Write(LogRecord record)
    {
        if (record.Pri > LogRecord.Priority(MinLevel))
        {
            return;
        }

        if (!_provider.TryWrite(record))
        {
            _file.Write(record);
        }
    }

    public void Write(IReadOnlyList<LogRecord> records)
    {
        foreach (var record in records)
        {
            Write(record);
        }
    }

    /// <summary>Accepts a `ctl log.write` batch from an extension (defense in depth: sanitize again).</summary>
    public void WriteExtensionBatch(string unit, JsonArray? records, string? invocationId, int? pid)
    {
        if (records is null)
        {
            return;
        }

        var accepted = new List<LogRecord>(records.Count);
        foreach (var node in records)
        {
            if (node is not JsonObject raw)
            {
                continue;
            }

            var record = LogRecord.SanitizeExtension(raw, unit, _bootId, _stickId, _machineId, invocationId, pid, out var spoofed, out var redacted);
            record.Seq = NextUnitSeq(unit);
            if (spoofed > 0 || redacted > 0)
            {
                accepted.Add(new LogRecord
                {
                    Ts = TimeUtil.NowIso(),
                    Pri = LogRecord.Priority(LogLevel.Warn),
                    Level = "warn",
                    Msg = "extension log record was sanitized",
                    Evt = "log.sanitized",
                    Seq = Interlocked.Increment(ref _seq),
                    BootId = _bootId,
                    Unit = unit,
                    InvocationId = invocationId,
                    StickId = _stickId,
                    MachineId = _machineId,
                    Transport = "kernel",
                    Fields = new JsonObject { ["spoofed"] = spoofed, ["redacted"] = redacted },
                });
            }

            accepted.Add(record);
        }

        Write(accepted);
    }

    public void Flush()
    {
        _provider.Flush();
        _file.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Flush();
        _provider.Dispose();
        _file.Dispose();
    }
}
