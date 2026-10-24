using System.Runtime.InteropServices;
using System.Text;
using PortableDev.Core;

namespace PortableDev.Win;

/// <summary>Finds the stick by marker file + id, never by "first removable drive".</summary>
public static class VolumeLocator
{
    public const string MarkerFileName = ".portabledev-root";

    /// <summary>All ready fixed/removable drive roots, e.g. "E:\".</summary>
    public static IEnumerable<string> EnumerateDriveRoots()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                root = d.RootDirectory.FullName;
            }
            catch
            {
                continue;
            }
            yield return root;
        }
    }

    /// <summary>
    /// Every directory that contains a portable-dev root marker.
    /// Checks the drive root itself plus a small set of conventional folder names,
    /// so both the "marker at volume root" and "PortableDev folder on the volume"
    /// layouts are supported.
    /// </summary>
    public static IReadOnlyList<(string Root, string? Id)> FindPortableRoots()
    {
        var found = new List<(string, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var driveRoot in EnumerateDriveRoots())
        {
            foreach (var candidate in CandidateRoots(driveRoot))
            {
                var marker = Path.Combine(candidate, MarkerFileName);
                try
                {
                    if (!File.Exists(marker)) continue;
                    var normalized = candidate.TrimEnd('\\');
                    if (!seen.Add(normalized)) continue;
                    found.Add((normalized, ReadId(marker)));
                }
                catch
                {
                    // unreadable location -> ignore
                }
            }
        }
        return found;
    }

    /// <summary>Marker at the volume root, or inside a PortableDev folder on it.</summary>
    private static IEnumerable<string> CandidateRoots(string driveRoot)
    {
        yield return driveRoot;

        foreach (var name in new[] { "PortableDev", "portabledev", "PortableDevRoot" })
        {
            string p;
            try { p = Path.Combine(driveRoot, name); }
            catch { continue; }
            yield return p;
        }
    }

    private static string? ReadId(string markerPath)
    {
        try
        {
            foreach (var line in File.ReadLines(markerPath))
            {
                var t = line.Trim();
                var idx = t.IndexOf("\"id\"", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var colon = t.IndexOf(':', idx);
                if (colon < 0) continue;
                var v = t[(colon + 1)..].Trim().Trim(',').Trim('"');
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    /// <summary>
    /// Resolve the stick root. When several sticks are present the caller must
    /// disambiguate: this returns null (never "the first one") so that applying the
    /// wrong manifest is impossible by accident.
    /// </summary>
    public static (string? Root, int Count) Resolve(string? explicitRoot = null)
    {
        var d = ResolveDetailed(explicitRoot);
        return (d.Root, d.Count);
    }

    /// <summary>
    /// Same decision as <see cref="Resolve"/>, but carries the reason it failed so the
    /// CLI can print something actionable ("pass --root E:\PortableDev") instead of a
    /// bare "stick not found".
    /// </summary>
    public static (string? Root, int Count, string? Error) ResolveDetailed(string? explicitRoot = null)
    {
        // An explicit root wins even when the scan would not have looked there:
        // --root may point at any folder that carries the marker.
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var r = explicitRoot!.Trim().TrimEnd('\\', '/');
            if (r.Length == 0)
                return (null, 0, "--root was given an empty path");

            if (!File.Exists(Path.Combine(r, MarkerFileName)))
                return (null, 0, $"'{r}' has no {MarkerFileName}; is that the stick root?");

            return (r, 1, null);
        }

        var candidates = FindPortableRoots()
            .Select(c => new StickCandidate(c.Root, c.Id))
            .ToList();

        var selection = StickSelection.Select(candidates);
        return selection.Resolved
            ? (selection.Root, selection.Count, null)
            : (null, selection.Count, selection.Error);
    }

    /// <summary>Every candidate root, for the "which stick did you mean?" message.</summary>
    public static IReadOnlyList<(string Root, string? Id)> Candidates() => FindPortableRoots();

    /// <summary>Volume label, for diagnostics only.</summary>
    public static string? LabelOf(string driveRoot)
    {
        try
        {
            var d = new DriveInfo(driveRoot);
            return d.IsReady ? d.VolumeLabel : null;
        }
        catch
        {
            return null;
        }
    }
}
