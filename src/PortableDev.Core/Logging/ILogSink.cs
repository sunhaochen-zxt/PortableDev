namespace PortableDev.Core.Logging;

/// <summary>A sink that accepts already-built records (file fallback or the log provider).</summary>
public interface ILogSink
{
    void Write(LogRecord record);

    void Write(IReadOnlyList<LogRecord> records);

    void Flush();
}
