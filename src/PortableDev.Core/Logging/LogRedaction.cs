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

    /// <summary>Field names whose values should be marked sensitive (encryption will use this later).</summary>
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
    /// Removes whole-snapshot dumps recursively instead of throwing (logging must never fail the caller).
    /// Named values (including secrets) are allowed and get marked sensitive by <see cref="LogRecord"/>.
    /// </summary>
    public static int Validate(JsonObject? fields) => fields is null ? 0 : RemoveBulkSnapshots(fields);

    private static int RemoveBulkSnapshots(JsonObject obj)
    {
        var removed = 0;
        foreach (var name in obj.Select(pair => pair.Key).ToArray())
        {
            var value = obj[name];
            if (IsBulkSnapshot(name))
            {
                obj.Remove(name);
                removed++;
                continue;
            }

            if (value is JsonObject nested)
            {
                removed += RemoveBulkSnapshots(nested);
            }
            else if (value is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item is JsonObject child)
                    {
                        removed += RemoveBulkSnapshots(child);
                    }
                }
            }
        }

        return removed;
    }
}
