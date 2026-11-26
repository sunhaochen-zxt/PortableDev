using System.Text.Json.Nodes;

namespace PortableDev.Core.Logging;

/// <summary>
/// Human-facing, value-free summary of an environment snapshot.
/// </summary>
public readonly struct EnvFingerprint
{
    public EnvFingerprint(int count) => Count = count;

    public int Count { get; }

    public override string ToString() => LogRedaction.Summarize(Count);
}

/// <summary>
/// Environment logging rules (kernel.md 7.1):
/// named values are allowed, whole snapshots are not, and sensitive-looking names are marked.
/// </summary>
public static class LogRedaction
{
    /// <summary>Field names that would smuggle a whole environment snapshot into a record.</summary>
    private static readonly string[] BulkSnapshotNames = ["env", "environment"];

    /// <summary>Placeholder emitted in place of sensitive values while log encryption is off.</summary>
    public const string RedactedPlaceholder = "[REDACTED]";

    /// <summary>Field names whose values must be masked and marked sensitive.</summary>
    private static readonly string[] SensitivePrefixes = ["secret", "token", "password", "credential", "apikey", "api_key", "auth"];

    /// <summary>True when the field is a whole snapshot rather than one named value.</summary>
    public static bool IsBulkSnapshot(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        foreach (var prefix in BulkSnapshotNames)
        {
            if (normalized == prefix)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when the field name looks like secret material; the record is marked, not dropped.</summary>
    public static bool IsSensitive(string name)
    {
        var normalized = name.Trim().ToLowerInvariant();
        foreach (var prefix in SensitivePrefixes)
        {
            if (normalized == prefix ||
                normalized.Contains(prefix, StringComparison.Ordinal) ||
                normalized.StartsWith(prefix + ".", StringComparison.Ordinal) ||
                normalized.StartsWith(prefix + "_", StringComparison.Ordinal) ||
                normalized.EndsWith("." + prefix, StringComparison.Ordinal) ||
                normalized.EndsWith("_" + prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Human-facing, value-free summary.</summary>
    public static string Summarize(int count) => count <= 0
        ? "no environment variables injected"
        : $"{count} environment variables injected";

    /// <summary>
    /// Removes whole-snapshot dumps recursively instead of throwing (logging must never fail the caller),
    /// and masks sensitive-named values so plaintext credentials never hit disk.
    /// </summary>
    public static int Validate(JsonObject? fields) => Redact(fields, out _);

    /// <summary>Recursively removes bulk snapshots, masks sensitive values, and reports whether any sensitive key was seen.</summary>
    public static int Redact(JsonObject? fields, out bool sensitive)
    {
        sensitive = false;
        return fields is null ? 0 : RedactObject(fields, ref sensitive, 0);
    }

    private static int RedactObject(JsonObject obj, ref bool sensitive, int depth)
    {
        var removed = 0;
        if (depth >= 8)
        {
            return 0;
        }

        foreach (var name in obj.Select(pair => pair.Key).ToArray())
        {
            var value = obj[name];
            if (IsBulkSnapshot(name))
            {
                obj.Remove(name);
                removed++;
                continue;
            }

            if (IsSensitive(name))
            {
                sensitive = true;
                obj[name] = RedactedPlaceholder;
                continue;
            }

            removed += RedactNode(value, ref sensitive, depth + 1);
        }

        return removed;
    }

    private static int RedactNode(JsonNode? node, ref bool sensitive, int depth)
    {
        if (depth >= 8)
        {
            return 0;
        }

        if (node is JsonObject nested)
        {
            return RedactObject(nested, ref sensitive, depth);
        }

        if (node is JsonArray array)
        {
            var removed = 0;
            foreach (var item in array)
            {
                removed += RedactNode(item, ref sensitive, depth + 1);
            }

            return removed;
        }

        return 0;
    }
}
