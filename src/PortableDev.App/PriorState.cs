using System.Globalization;
using System.Text.Json;

namespace PortableDev.App;

/// <summary>One environment variable as it was before the first apply that wrote it.</summary>
public sealed record PriorEnv(string Name, string? Value, bool Existed, string? Kind);

/// <summary>The user PATH as it was before the first apply that rewrote it.</summary>
public sealed record PriorPath(string? Value, bool Existed, string? Kind);

/// <summary>
/// What <c>apply</c> found when it first changed this machine, recovered from its journals.
///
/// Why read journals instead of a saved snapshot: <c>clean</c> is supposed to leave the
/// machine exactly as <c>apply</c> found it, but it runs a long time later — the machine may
/// have been rebooted into a restored image, the manifest may have been edited, and the
/// only durable record of the original values is what apply wrote down before it mutated
/// anything. The earliest apply that touched a value wins, because that is "before apply".
///
/// Journals written before the fields were recorded (and journals of <c>--test-registry</c>
/// runs) are ignored: the former because they carry no prior value, the latter because the
/// throwaway key is not machine state. <c>clean</c> then falls back to a conservative
/// comparison against the current manifest.
/// </summary>
public static class PriorState
{
    public sealed class Snapshot
    {
        public IReadOnlyDictionary<string, PriorEnv> Env { get; init; } =
            new Dictionary<string, PriorEnv>(StringComparer.OrdinalIgnoreCase);

        public PriorPath? Path { get; init; }

        public int JournalsRead { get; init; }

        public bool IsEmpty => Env.Count == 0 && Path is null;
    }

    public static Snapshot Read(string journalDir)
    {
        var env = new Dictionary<string, PriorEnv>(StringComparer.OrdinalIgnoreCase);
        PriorPath? path = null;

        var journals = new List<(DateTimeOffset Timestamp, string File)>();
        if (Directory.Exists(journalDir))
        {
            foreach (var file in Directory.GetFiles(journalDir, "*.jsonl"))
            {
                var name = System.IO.Path.GetFileName(file);
                // clean-* journals describe a teardown, not the state apply met.
                if (name.StartsWith("clean-", StringComparison.OrdinalIgnoreCase)) continue;

                var ts = FirstTimestamp(file);
                if (ts is null) continue;
                journals.Add((ts.Value, file));
            }
        }

        journals.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        var examined = 0;
        foreach (var (_, file) in journals)
        {
            if (TryReadJournal(file, env, ref path)) examined++;
        }

        return new Snapshot { Env = env, Path = path, JournalsRead = examined };
    }

    private static bool TryReadJournal(string file, Dictionary<string, PriorEnv> env, ref PriorPath? path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(file); }
        catch { return false; }

        var isTestRegistry = false;
        var parsedAny = false;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                parsedAny = true;
                var root = doc.RootElement;
                if (!root.TryGetProperty("kind", out var kindEl)) continue;
                if (!root.TryGetProperty("payload", out var payload)) continue;
                var kind = kindEl.GetString();

                if (string.Equals(kind, "backup", StringComparison.Ordinal))
                {
                    if (payload.TryGetProperty("testRegistry", out var test) && test.ValueKind == JsonValueKind.True)
                        isTestRegistry = true;
                    continue;
                }

                // A --test-registry run mutates HKCU\Environment-Test; those values say nothing
                // about the real machine and must never be restored by clean.
                if (isTestRegistry) continue;

                if (string.Equals(kind, "env.write", StringComparison.Ordinal))
                {
                    if (!payload.TryGetProperty("name", out var nameEl)) continue;
                    var name = nameEl.GetString();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (env.ContainsKey(name)) continue;   // the earliest apply is "before apply"

                    // Journals from before these fields existed cannot be used: no prior value.
                    if (!payload.TryGetProperty("existed", out var existedEl)) continue;
                    if (existedEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;

                    env[name] = new PriorEnv(
                        name,
                        StringOf(payload, "before"),
                        existedEl.GetBoolean(),
                        StringOf(payload, "kind"));
                }
                else if (string.Equals(kind, "path.write", StringComparison.Ordinal))
                {
                    if (path is not null) continue;
                    if (!payload.TryGetProperty("existed", out var existedEl)) continue;
                    if (existedEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;

                    path = new PriorPath(
                        StringOf(payload, "before"),
                        existedEl.GetBoolean(),
                        StringOf(payload, "kind"));
                }
            }
            catch (JsonException)
            {
                // A half-written line from a killed run: ignore it, the rest is still usable.
            }
        }

        return parsedAny;
    }

    private static string? StringOf(JsonElement payload, string property) =>
        payload.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static DateTimeOffset? FirstTimestamp(string file)
    {
        try
        {
            using var reader = new StreamReader(file);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var parsed))
                    return parsed;

                return null;   // first record without a timestamp: not one of ours
            }
        }
        catch { /* unreadable journal is not a reason to fail clean */ }

        return null;
    }
}

