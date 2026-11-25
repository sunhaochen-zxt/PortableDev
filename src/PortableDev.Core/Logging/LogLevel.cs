namespace PortableDev.Core.Logging;

/// <summary>Log levels accepted by `kernel.logLevel` / `log.level` (kernel.md 2.1).</summary>
public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

public static class LogLevels
{
    public static bool TryParse(string? text, out LogLevel level)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case "debug": level = LogLevel.Debug; return true;
            case "info": level = LogLevel.Info; return true;
            case "warn":
            case "warning": level = LogLevel.Warn; return true;
            case "error": level = LogLevel.Error; return true;
            default: level = LogLevel.Info; return false;
        }
    }

    public static string ToText(LogLevel level) => level switch
    {
        LogLevel.Debug => "debug",
        LogLevel.Warn => "warn",
        LogLevel.Error => "error",
        _ => "info",
    };
}
