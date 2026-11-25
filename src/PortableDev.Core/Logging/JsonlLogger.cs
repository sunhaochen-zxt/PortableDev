using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortableDev.Core.Util;

namespace PortableDev.Core.Logging;

/// <summary>
/// Append-only JSONL writer with size-based rotation (kernel.md 7.1).
/// One instance owns one stream file (`kernel.jsonl`, `log.jsonl`, ...).
/// </summary>
public sealed class JsonlLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly LogOptions _options;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = false };
    private FileStream? _stream;
    private long _written;
    private bool _disposed;

    public JsonlLogger(LogOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        System.IO.Directory.CreateDirectory(_options.Directory);
        OpenLocked();
    }

    public string FilePath => _options.PrimaryPath;

    public LogLevel Level
    {
        get => _options.Level;
        set => _options.Level = value;
    }

    public bool IsEnabled(LogLevel level) => level >= _options.Level && !_disposed;

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

        LogRedaction.Validate(fields);

        var record = new JsonObject
        {
            ["ts"] = TimeUtil.NowIso(),
            ["level"] = LogLevels.ToText(level),
            ["stream"] = _options.StreamName,
            ["evt"] = evt,
        };

        if (message is not null)
        {
            record["msg"] = message;
        }

        if (trace is not null)
        {
            record["trace"] = trace;
        }

        if (task is not null)
        {
            record["task"] = task;
        }

        if (fields is not null)
        {
            foreach (var (name, value) in fields)
            {
                if (!record.ContainsKey(name))
                {
                    record[name] = value?.DeepClone();
                }
            }
        }

        AppendLine(JsonSerializer.Serialize(record, _json));
    }

    /// <summary>Appends a pre-built contract record (used by <see cref="FileLogSink"/>).</summary>
    public void WriteJson(System.Text.Json.Nodes.JsonObject record)
    {
        if (_disposed)
        {
            return;
        }

        AppendLine(record.ToJsonString(_json));
    }

    private void AppendLine(string line)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            if (_written > 0 && _written + bytes > _options.MaxSizeBytes)
            {
                RotateLocked();
            }

            try
            {
                var payload = Encoding.UTF8.GetBytes(line + "\n");
                _stream!.Write(payload, 0, payload.Length);
                _stream.Flush();
                _written += payload.Length;
            }
            catch (IOException ex)
            {
                BootstrapLog.Write($"kernel log write failed: {ex.Message}");
            }
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            _stream?.Flush();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream?.Flush();
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void OpenLocked()
    {
        System.IO.Directory.CreateDirectory(_options.Directory);
        _stream = new FileStream(
            _options.PrimaryPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.SequentialScan);
        _written = _stream.Length;
    }

    private void RotateLocked()
    {
        _stream?.Flush();
        _stream?.Dispose();
        _stream = null;

        var keep = Math.Max(1, _options.KeepFiles);
        for (var index = keep; index >= 1; index--)
        {
            var source = index == 1 ? _options.PrimaryPath : _options.RotatedPath(index - 1);
            var target = _options.RotatedPath(index);
            if (File.Exists(source))
            {
                try
                {
                    File.Move(source, target, overwrite: true);
                }
                catch (IOException ex)
                {
                    BootstrapLog.Write($"kernel log rotation failed: {ex.Message}");
                }
            }
        }

        OpenLocked();
    }
}
