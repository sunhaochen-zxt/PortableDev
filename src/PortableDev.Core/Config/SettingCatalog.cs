using System.Text.Json.Nodes;

namespace PortableDev.Core.Config;

/// <summary>The authoritative defaults for `config\\settings.json` (kernel.md 2.1).</summary>
public static class SettingCatalog
{
    public static readonly IReadOnlyList<SettingDefinition> Definitions =
    [
        new("schema", SettingKind.Int, 1, Min: 1, Max: 1000),
        new("root.stableRoot", SettingKind.Path, @"C:\Dev", RestartRequired: true),
        new("root.stickRoot", SettingKind.Path, string.Empty),
        new("kernel.pipePrefix", SettingKind.String, "portabledev", RestartRequired: true),
        new("kernel.logLevel", SettingKind.Enum, "info", ["debug", "info", "warn", "error"], RestartRequired: true),
        new("kernel.idleExit", SettingKind.Duration, "0", RestartRequired: true),
        new("kernel.startTimeout", SettingKind.Duration, "5s", Min: 1, Max: 60),
        new("kernel.envMode", SettingKind.Enum, "full", ["full", "proxy-only"]),
        new("install.allowScripts", SettingKind.Enum, "local", ["local", "always", "never"]),
        new("install.defaultChannel", SettingKind.Enum, "stable", ["stable", "beta", "dev"]),
        new("install.maxConcurrent", SettingKind.Int, 2, Min: 1, Max: 4),
        new("install.verifyAfterInstall", SettingKind.Bool, true),
        new("proc.processMemoryMB", SettingKind.Int, 1024, Min: 64, Max: 16384),
        new("proc.activeProcesses", SettingKind.Int, 64, Min: 1, Max: 1024),
        new("proc.scriptTimeout", SettingKind.Duration, "120s", Min: 1, Max: 3600),
        new("gc.keepGenerations", SettingKind.Int, 50, Min: 5, Max: 500),
        new("gc.keepBlobs", SettingKind.Bool, true),
        new("gc.stagingMaxAge", SettingKind.Duration, "24h", Min: 0, Max: 365 * 24 * 3600),
        new("log.level", SettingKind.Enum, "info", ["debug", "info", "warn", "error"], RestartRequired: true),
        new("log.maxSizeMB", SettingKind.Int, 50, Min: 1, Max: 1000, RestartRequired: true),
        new("log.keepFiles", SettingKind.Int, 10, Min: 1, Max: 100, RestartRequired: true),
        new("log.encryption", SettingKind.Enum, "none", ["none"]),
        new("log.retentionDays", SettingKind.Int, 30, Min: 1, Max: 3650),
        new("log.auditRetentionDays", SettingKind.Int, 7, Min: 1, Max: 3650),
        new("log.batchBytes", SettingKind.Int, 65536, Min: 4096, Max: 1048576),
        new("log.rateLimit", SettingKind.Int, 5000, Min: 0, Max: 1000000),
        new("network.timeout", SettingKind.Duration, "30s", Min: 1, Max: 600),
        new("network.retries", SettingKind.Int, 3, Min: 0, Max: 10),
        new("network.proxy", SettingKind.String, "system"),
        new("elevation.mode", SettingKind.Enum, "on-demand", ["on-demand"]),
        new("recovery.intentDefault", SettingKind.Enum, "adopt", ["adopt", "drop"], RestartRequired: true),
        new("ui.theme", SettingKind.Enum, "dark", ["dark", "light", "system"]),
        new("ui.language", SettingKind.Enum, "zh-CN", ["zh-CN", "en"]),
    ];

    public static SettingDefinition? Find(string path)
        => Definitions.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Coerces a raw command-line value into the JSON type the setting expects.</summary>
    public static bool TryCoerce(SettingDefinition definition, string raw, out JsonNode? value, out string? error)
    {
        error = null;
        value = null;
        var text = raw.Trim();

        switch (definition.Kind)
        {
            case SettingKind.Int:
                if (!int.TryParse(text, out var number))
                {
                    error = $"{definition.Path} expects an integer";
                    return false;
                }

                if (definition.Min is not null && number < definition.Min)
                {
                    error = $"{definition.Path} must be >= {definition.Min}";
                    return false;
                }

                if (definition.Max is not null && number > definition.Max)
                {
                    error = $"{definition.Path} must be <= {definition.Max}";
                    return false;
                }

                value = number;
                return true;

            case SettingKind.Bool:
                if (!bool.TryParse(text, out var flag))
                {
                    error = $"{definition.Path} expects true or false";
                    return false;
                }

                value = flag;
                return true;

            case SettingKind.Duration:
                var duration = Util.TimeUtil.ParseDuration(text);
                if (duration is null)
                {
                    error = $"{definition.Path} expects a duration such as 10m or 0";
                    return false;
                }

                var seconds = duration.Value.TotalSeconds;
                if (definition.Min is not null && seconds < definition.Min)
                {
                    error = $"{definition.Path} must be >= {definition.Min}s";
                    return false;
                }

                if (definition.Max is not null && seconds > definition.Max)
                {
                    error = $"{definition.Path} must be <= {definition.Max}s";
                    return false;
                }

                value = text;
                return true;

            case SettingKind.Enum:
                var values = definition.EnumValues ?? [];
                if (!values.Contains(text, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"{definition.Path} must be one of: {string.Join(", ", values)}";
                    return false;
                }

                value = values.First(v => string.Equals(v, text, StringComparison.OrdinalIgnoreCase));
                return true;

            default:
                if (text.Length == 0 && definition.DefaultValue is not null)
                {
                    value = definition.DefaultValue.DeepClone();
                    return true;
                }

                value = text;
                return true;
        }
    }

    /// <summary>Builds a fresh settings document containing every default.</summary>
    public static JsonObject CreateDefaultDocument()
    {
        var root = new JsonObject();
        foreach (var definition in Definitions)
        {
            SetPath(root, definition.Path, definition.DefaultValue?.DeepClone());
        }

        return root;
    }

    public static JsonNode? GetPath(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    public static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        var segments = path.Split('.');
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (current[segments[i]] is not JsonObject child)
            {
                child = new JsonObject();
                current[segments[i]] = child;
            }

            current = child;
        }

        current[segments[^1]] = value;
    }
}
