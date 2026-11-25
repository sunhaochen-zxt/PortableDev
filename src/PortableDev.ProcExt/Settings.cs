using System.Text.Json.Nodes;

namespace PortableDev.ProcExt;

/// <summary>Reads the `proc.*` settings the kernel publishes (kernel.md 2.1); callers fall back to defaults.</summary>
internal static class Settings
{
    private static readonly object Gate = new();
    private static JsonObject? _cached;

    public static int ProcessMemoryMB => ReadInt("proc.processMemoryMB", 1024);

    public static int ActiveProcesses => ReadInt("proc.activeProcesses", 64);

    public static int ScriptTimeoutSeconds => ReadInt("proc.scriptTimeout", 120);

    private static int ReadInt(string key, int fallback)
    {
        var root = Environment.GetEnvironmentVariable("PD_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            return fallback;
        }

        JsonObject? settings;
        lock (Gate)
        {
            if (_cached is null)
            {
                try
                {
                    var path = Path.Combine(root, "config", "settings.json");
                    _cached = File.Exists(path)
                        ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                        : new JsonObject();
                }
                catch (Exception)
                {
                    _cached = new JsonObject();
                }
            }

            settings = _cached;
        }

        var value = settings?[key]?.GetValueKind() == System.Text.Json.JsonValueKind.Number
            ? settings[key]!.GetValue<int>()
            : (int?)null;
        return value is > 0 ? value.Value : fallback;
    }
}
