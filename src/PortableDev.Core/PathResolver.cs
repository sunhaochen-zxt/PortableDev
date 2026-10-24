using PortableDev.Core.Model;

namespace PortableDev.Core;

/// <summary>
/// Resolves manifest placeholders into concrete paths.
/// Rule (plan §3): every path resolves to the concrete versioned tool dir.
/// The stick itself never contains symlinks (exFAT has no reparse points),
/// so only the single stable junction on NTFS is ever a link.
/// </summary>
public sealed class PathResolver
{
    public const string RootToken = "{{ROOT}}";
    public const string ToolToken = "{{TOOL}}";
    public const string ToolsToken = "{{TOOLS}}";
    public const string HomeToken = "{{HOME}}";

    private readonly string _stableRoot;
    private readonly string _stickRoot;

    public PathResolver(string stableRoot, string stickRoot)
    {
        _stableRoot = NormalizeRoot(stableRoot);
        _stickRoot = NormalizeRoot(stickRoot);
    }

    public string StableRoot => _stableRoot;
    public string StickRoot => _stickRoot;

    public static string NormalizeRoot(string path)
    {
        var s = (path ?? string.Empty).Trim();
        if (s.Length == 0) return s;
        s = s.Replace('/', '\\');
        return s.TrimEnd('\\');
    }

    /// <summary>
    /// Concrete root of the installed tool on the stable path.
    ///
    /// This goes through <see cref="ToolRelativeDir"/>, so an explicit [tool].dir is honoured.
    /// Building "tools\&lt;id&gt;\&lt;version&gt;" here instead made PATH and verify disagree
    /// whenever a manifest pinned its directory: after an upgrade PATH pointed at the new
    /// version while every check still ran the old one.
    /// </summary>
    public string ToolRoot(ToolManifest tool) =>
        $@"{_stableRoot}\{ToolRelativeDir(tool)}";

    /// <summary>Same as <see cref="ToolRoot"/> but on the real stick path.</summary>
    public string ToolRootReal(ToolManifest tool) =>
        $@"{_stickRoot}\{ToolRelativeDir(tool)}";

    /// <summary>
    /// Tool root on an arbitrary base path. Degraded mode (no junction) builds its PATH
    /// entries with this instead of rewriting "C:\Dev" inside already-built strings: a
    /// textual replace also hits any folder whose name merely starts with the root.
    /// </summary>
    public static string ToolRootOn(string baseRoot, ToolManifest tool) =>
        $@"{NormalizeRoot(baseRoot)}\{ToolRelativeDir(tool)}";

    /// <summary>
    /// A tool's directory relative to the stick root ("tools/python/3.13.17" or the explicit
    /// prebuilt dir). Returned with backslashes.
    /// </summary>
    public static string ToolRelativeDir(ToolManifest tool)
    {
        var explicitDir = tool.Tool.Dir;
        if (!string.IsNullOrWhiteSpace(explicitDir))
            return explicitDir!.Replace('/', '\\').TrimStart('\\');

        return $@"tools\{tool.Tool.Id}\{tool.Tool.Version}";
    }

    /// <summary>Replace every known token. Unknown tokens are left untouched.</summary>
    public string Expand(string raw, ToolManifest? tool = null)
    {
        if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;

        var s = raw;
        s = s.Replace(ToolsToken, $@"{_stableRoot}\tools", StringComparison.OrdinalIgnoreCase);
        s = s.Replace(HomeToken, $@"{_stableRoot}\home", StringComparison.OrdinalIgnoreCase);
        s = s.Replace(RootToken, _stableRoot, StringComparison.OrdinalIgnoreCase);

        if (tool is not null)
            s = s.Replace(ToolToken, ToolRoot(tool), StringComparison.OrdinalIgnoreCase);

        return s.Replace('/', '\\');
    }

    /// <summary>
    /// Full PATH entry for one tool path entry. "." means the tool root.
    /// Always normalized, never trailing-slash, never quoted.
    /// </summary>
    public string PathEntryFor(ToolManifest tool, ToolManifest.PathEntry entry)
    {
        var root = ToolRoot(tool);
        var rel = (entry.Dir ?? ".").Replace('/', '\\').Trim();
        if (rel.Length == 0 || rel == ".") return root;
        return $@"{root}\{rel.TrimStart('\\').TrimEnd('\\')}";
    }

    /// <summary>
    /// All PATH entries for the enabled tools, in path_order then per-tool declaration
    /// order. <paramref name="baseRootOverride"/> builds them on another root (degraded
    /// mode points PATH at the stick itself).
    /// </summary>
    public IReadOnlyList<(string ToolId, string Entry)> BuildPathEntries(
        RootManifest manifest, string? baseRootOverride = null)
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = manifest.PathOrder;

        var ordered = new List<string>(order);
        // Tools not listed in path_order still apply, appended in id order for determinism.
        foreach (var id in manifest.Tools.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (!ordered.Contains(id, StringComparer.OrdinalIgnoreCase))
                ordered.Add(id);
        }

        foreach (var id in ordered)
        {
            if (!manifest.Tools.TryGetValue(id, out var tool)) continue;
            if (!tool.Tool.Enabled) continue;

            var root = baseRootOverride is null ? ToolRoot(tool) : ToolRootOn(baseRootOverride, tool);

            foreach (var pe in tool.Path)
            {
                var rel = (pe.Dir ?? ".").Replace('/', '\\').Trim();
                var full = rel.Length == 0 || rel == "."
                    ? root
                    : $@"{root}\{rel.TrimStart('\\').TrimEnd('\\')}";

                if (seen.Add(full))
                    result.Add((id, full));
            }
        }

        return result;
    }
}
