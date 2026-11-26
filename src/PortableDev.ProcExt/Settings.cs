using System.Text.Json;
using System.Text.Json.Nodes;

namespace PortableDev.ProcExt;

/// <summary>
/// Reads the proc.* settings the kernel publishes (kernel.md 2.1); callers fall back to defaults.
/// settings.json is nested ({'proc': {...}}), and durations are stored as "120s".
/// </summary>
internal static class Settings
{
    private static readonly object Gate = new();
    private static JsonObject? _cached;
    private static string? _cachedRoot;

    public static int ProcessMemoryMB => ReadInt("proc.processMemoryMB", 1024);

    public static int ActiveProcesses => ReadInt("proc.activeProcesses", 64);

    public static int ScriptTimeoutSeconds => ReadDurationSeconds("proc.scriptTimeout", 120);

    private static JsonNode? Find(string dotted)
    {
        var root = Environment.GetEnvironmentVariable("PD_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        lock (Gate)
        {
            if (_cached is null || !string.Equals(_cachedRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                _cachedRoot = root;
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

            JsonNode? node = _cached;
            foreach (var segment in dotted.Split('.'))
            {
                if (node is not JsonObject current || !current.TryGetPropertyValue(segment, out var child))
                {
                    return null;
                }

                node = child;
            }

            return node;
        }
    }

    private static int ReadInt(string key, int fallback)
    {
        var node = Find(key);
        if (node is not JsonValue value || !value.TryGetValue<int>(out var number) || number <= 0)
        {
            return fallback;
        }

        return number;
    }

    private static int ReadDurationSeconds(string key, int fallback)
    {
        var node = Find(key);
        if (node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue<int>(out var plain))
        {
            return plain > 0 ? plain : fallback;
        }

        if (value.TryGetValue<string>(out var text))
        {
            var seconds = ParseDurationSeconds(text);
            return seconds is > 0 ? seconds.Value : fallback;
        }

        return fallback;
    }

    /// <summary>Accepts the same short forms as kernel.md: 500ms / 30s / 5m / 1h; bare numbers are seconds.</summary>
    private static int? ParseDurationSeconds(string text)
    {
        var raw = (text ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Length == 0)
        {
            return null;
        }

        var digits = 0;
        while (digits < raw.Length && char.IsDigit(raw[digits]))
        {
            digits++;
        }

        if (digits == 0 || !int.TryParse(raw[..digits], out var amount))
        {
            return null;
        }

        var unit = raw[digits..].Trim();
        var multiplier = unit switch
        {
            "" or "s" or "sec" or "secs" => 1,
            "ms" => 0.001,
            "m" or "min" or "mins" => 60,
            "h" or "hr" or "hrs" => 3600,
            _ => 0,
        };
        if (multiplier == 0)
        {
            return null;
        }

        var seconds = (int)Math.Round(amount * multiplier);
        return seconds > 0 ? seconds : null;
    }
}