using System.Text.Json.Serialization;

namespace PortableDev.Core.Model;

/// <summary>
/// Where to look for newer releases of a tool.
///
/// Deliberately declarative: adding a tool must not require code. A tool without an
/// [update] section simply never reports an update, which is the right default for
/// things supplied by hand (Sublime, or an offline archive).
/// </summary>
public sealed class UpdateSpec
{
    /// <summary>github | node | vscode | sevenzip | scoop</summary>
    public string Kind { get; init; } = "github";

    /// <summary>Bucket name for kind=scoop ("Main" when empty).</summary>
    public string? Bucket { get; init; }

    /// <summary>App name inside the bucket for kind=scoop (the tool id when empty).</summary>
    public string? App { get; init; }

    /// <summary>owner/name for kind=github.</summary>
    public string? Repo { get; init; }

    /// <summary>URL of a plain text / JSON index for other kinds.</summary>
    public string? Index { get; init; }

    /// <summary>
    /// Regex applied to an asset name (github) or to the index body (others).
    /// It must expose the version, either as its first group or as a named
    /// group "ver". Other named groups become template placeholders.
    /// </summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>
    /// Download URL template. Placeholders: {version} plus any named group from
    /// Pattern. Empty means "use the URL the source already gave us".
    /// </summary>
    public string? Url { get; init; }

    /// <summary>Optional .sha256 / digest sidecar URL template.</summary>
    public string? Sha256Url { get; init; }

    /// <summary>Strip this prefix from the discovered version before comparing.</summary>
    public string? StripPrefix { get; init; }

    /// <summary>Releases whose tag/name matches this are ignored (e.g. rc, beta).</summary>
    public string? SkipVersionPattern { get; init; }
}

/// <summary>Result of asking upstream for the newest version.</summary>
public sealed class UpdateInfo
{
    public required string ToolId { get; init; }
    public required string CurrentVersion { get; init; }
    public required string AvailableVersion { get; init; }
    public required string DownloadUrl { get; init; }

    /// <summary>
    /// Checksum of the package, when the source declared one up front. Almost always null:
    /// most sources publish the checksum as a sidecar, which
    /// <see cref="Update.UpdateChecker.ResolveSha256Async"/> fetches only when a download
    /// is actually going to happen. Never set this to a guess.
    /// </summary>
    public string? Sha256 { get; init; }

    /// <summary>
    /// Where the checksum sidecar lives, when one was discovered while looking for the
    /// version. Discovering the URL is free; downloading it is not, so it is left to the
    /// download step.
    /// </summary>
    public string? Sha256Url { get; init; }

    public long? SizeBytes { get; init; }

    /// <summary>For bucket packages: the subdirectory holding the payload.</summary>
    public string? ExtractDir { get; init; }

    /// <summary>For bucket packages: local file name when the url renames its download.</summary>
    public string? RenameTo { get; init; }

    /// <summary>True when AvailableVersion sorts above CurrentVersion.</summary>
    public bool IsNewer => VersionOrder.Compare(AvailableVersion, CurrentVersion) > 0;

    public string Summary => CurrentVersion + "  →  " + AvailableVersion;
}

/// <summary>Dotted-numeric comparison that tolerates odd suffixes (rc1, windows.3).</summary>
public static class VersionOrder
{
    public static int Compare(string a, string b)
    {
        var x = Split(a);
        var y = Split(b);
        var n = Math.Max(x.Count, y.Count);
        for (var i = 0; i < n; i++)
        {
            var xi = i < x.Count ? x[i] : 0;
            var yi = i < y.Count ? y[i] : 0;
            if (xi != yi) return xi.CompareTo(yi);
        }
        // Same numbers: a plain release beats a suffixed one ("1.0" > "1.0-rc1").
        var sa = Suffix(a);
        var sb = Suffix(b);
        if (sa.Length == 0 && sb.Length > 0) return 1;
        if (sb.Length == 0 && sa.Length > 0) return -1;
        return string.CompareOrdinal(sa, sb);
    }

    private static List<int> Split(string v)
    {
        var list = new List<int>();
        var cur = new System.Text.StringBuilder();
        foreach (var ch in v ?? string.Empty)
        {
            if (char.IsDigit(ch)) { cur.Append(ch); continue; }
            if (cur.Length > 0) { list.Add(int.Parse(cur.ToString())); cur.Clear(); }
            if (ch == '-' || ch == '+') break;
        }
        if (cur.Length > 0) list.Add(int.Parse(cur.ToString()));
        return list;
    }

    private static string Suffix(string v)
    {
        var i = v.IndexOfAny(new[] { '-', '+' });
        return i < 0 ? string.Empty : v[i..].TrimStart('-', '+');
    }
}
