using System.Text.Json.Nodes;

namespace PortableDev.Core.Logging;

/// <summary>Kernel fallback: appends to `kernel.jsonl` (rotation and retention unchanged).</summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    private readonly JsonlLogger _writer;

    public FileLogSink(LogOptions options) => _writer = new JsonlLogger(options);

    public string FilePath => _writer.FilePath;

    public void Write(LogRecord record) => _writer.WriteJson(record.ToJson());

    public void Write(IReadOnlyList<LogRecord> records)
    {
        foreach (var record in records)
        {
            Write(record);
        }
    }

    public void Flush() => _writer.Flush();

    public void Dispose() => _writer.Dispose();
}
