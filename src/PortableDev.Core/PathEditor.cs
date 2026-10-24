namespace PortableDev.Core;

/// <summary>
/// Pure manipulation of a raw (unexpanded) Windows PATH string.
///
/// Rules:
///  - Only entries under the stable root are ever touched. Foreign entries are
///    preserved byte-for-byte, including their order and %VAR% forms.
///  - Entries are unquoted for comparison (Windows stores both forms) but the
///    canonical output is unquoted, because quoting inside a REG_EXPAND_SZ PATH
///    confuses some tools and shell scripts.
///  - Empty/whitespace entries are dropped (they are a known PATH-injection bug).
/// </summary>
public static class PathEditor
{
    public static IReadOnlyList<string> Split(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return Array.Empty<string>();

        var parts = rawPath!.Split(';', StringSplitOptions.None);
        var list = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length == 0) continue;
            list.Add(t);
        }
        return list;
    }

    public static string Unquote(string entry)
    {
        var t = entry.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
            t = t[1..^1].Trim();
        return t;
    }

    /// <summary>Case-insensitive, separator-insensitive path comparison.</summary>
    public static bool PathsEqual(string a, string b)
    {
        var x = Unquote(a).Replace('/', '\\').TrimEnd('\\');
        var y = Unquote(b).Replace('/', '\\').TrimEnd('\\');
        return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="entry"/> lives under <paramref name="stableRoot"/>.</summary>
    public static bool IsUnderRoot(string entry, string stableRoot)
    {
        var e = Unquote(entry).Replace('/', '\\');
        var r = PathResolver.NormalizeRoot(stableRoot);

        if (!e.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return false;
        if (e.Length == r.Length) return true;
        return e[r.Length] == '\\';
    }

    /// <summary>
    /// Produce the new PATH: managed entries removed, then <paramref name="desiredEntries"/>
    /// prepended in order, then every foreign entry in its original order.
    /// </summary>
    public static string Rebuild(string? rawPath, string stableRoot, IReadOnlyList<string> desiredEntries)
    {
        var existing = Split(rawPath);
        var foreign = new List<string>();
        var seenForeign = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in existing)
        {
            if (IsUnderRoot(e, stableRoot)) continue;   // managed -> dropped, re-added below
            var key = Unquote(e);
            if (seenForeign.Add(key)) foreign.Add(Unquote(e));
        }

        var output = new List<string>();
        var seenManaged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in desiredEntries)
        {
            var v = Unquote(d);
            if (seenManaged.Add(v)) output.Add(v);
        }

        output.AddRange(foreign);
        return string.Join(";", output);
    }

    /// <summary>Entries currently managed (under the stable root), in their current order.</summary>
    public static IReadOnlyList<string> ManagedEntries(string? rawPath, string stableRoot) =>
        Split(rawPath).Where(e => IsUnderRoot(e, stableRoot)).Select(Unquote).ToList();

    /// <summary>Entries that are not managed.</summary>
    public static IReadOnlyList<string> ForeignEntries(string? rawPath, string stableRoot) =>
        Split(rawPath).Where(e => !IsUnderRoot(e, stableRoot)).Select(Unquote).ToList();
}
