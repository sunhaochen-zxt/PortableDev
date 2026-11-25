namespace PortableDev.Core.Logging;

/// <summary>Rotation limits mirroring `log.maxSizeMB` / `log.keepFiles`.</summary>
public sealed class LogOptions
{
    public LogOptions(string directory, string streamName)
    {
        Directory = directory;
        StreamName = streamName;
    }

    public string Directory { get; }

    public string StreamName { get; }

    public LogLevel Level { get; set; } = LogLevel.Info;

    public long MaxSizeBytes { get; set; } = 50L * 1024 * 1024;

    public int KeepFiles { get; set; } = 10;

    public string PrimaryPath => Path.Combine(Directory, StreamName + ".jsonl");

    public string RotatedPath(int index) => Path.Combine(Directory, $"{StreamName}.{index}.jsonl");
}
