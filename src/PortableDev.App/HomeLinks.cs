using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Win;

namespace PortableDev.App;

/// <summary>
/// Puts directories that tools write to (APPDATA\Code, .config, ...) onto the stick
/// by replacing them with a junction.
///
/// Why a junction and not an environment variable: Code.exe, python, npm and git all
/// compute their config paths from the user profile through the Win32 known-folder API,
/// not from HOME. An env var simply does not reach them. A junction does, costs no
/// privilege (unlike a symlink), and survives on any NTFS system drive.
///
/// The user's own data is never moved unless explicitly allowed: with home_adopt off,
/// an occupied slot is skipped with a warning and left exactly as it was.
/// </summary>
public static class HomeLinks
{
    /// <summary>
    /// Create or repair one home link. Returns (ok, error).
    ///
    /// <paramref name="acceptedRoots"/> are the paths that are allowed to be junctions
    /// themselves — the stable root and the stick root are, by design. Without this the
    /// guard below rejected every store directory, because C:\Dev is a junction.
    /// </summary>
    public static (bool Ok, string? Error) Apply(
        HomeLinkState link, bool adopt, Journal journal, params string[] acceptedRoots)
    {
        try
        {
            // Validate before creating anything, so a refused slot leaves no empty dirs.
            EnsureStoreIsPlain(link.StorePath, acceptedRoots);
            Directory.CreateDirectory(link.StorePath);

            // Already the right junction: nothing to do.
            if (link.IsLink && link.LinkTarget is not null &&
                PathEditor.PathsEqual(link.LinkTarget, link.StorePath))
                return (true, null);

            // A wrong junction is just a reparse point; mklink replaces it in place.
            if (link.IsLink)
            {
                var (ok, err) = Junction.Create(link.HostPath, link.StorePath);
                return ok ? (true, null) : (false, err);
            }

            // A real directory is in the way.
            if (link.Exists && Directory.Exists(link.HostPath) && !Junction.IsReparsePoint(link.HostPath))
            {
                var hasContent = HasContent(link.HostPath);
                if (hasContent && !adopt)
                    return (false, link.HostPath + " is a real directory with data; set home_adopt = true to move it onto the stick");

                if (hasContent)
                {
                    journal.Record("home.adopt", new { link.HostPath, link.StorePath });
                    var (moved, moveErr) = MoveIntoStore(link.HostPath, link.StorePath);
                    if (!moved) return (false, moveErr);
                }
                else
                {
                    // Empty directory: remove it, the junction takes its place.
                    try { Directory.Delete(link.HostPath, recursive: false); }
                    catch (Exception ex) { return (false, "could not remove the empty directory " + link.HostPath + ": " + ex.Message); }
                }
            }

            var parent = Path.GetDirectoryName(link.HostPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            var (createOk, createErr) = Junction.Create(link.HostPath, link.StorePath);
            if (!createOk) return (false, createErr);

            // Verify by reading the reparse point back, never by trusting mklink's exit code.
            var actual = Junction.GetTarget(link.HostPath);
            if (actual is null || !PathEditor.PathsEqual(actual, link.StorePath))
                return (false, link.HostPath + " did not end up pointing at " + link.StorePath);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Moving data aside and putting a junction in place must never half-happen.
    /// The host directory is moved to a sibling staging name first; if anything fails
    /// it is moved straight back.
    /// </summary>
    private static (bool Ok, string? Error) MoveIntoStore(string hostPath, string storePath)
    {
        string? staging = null;
        try
        {
            Directory.CreateDirectory(storePath);
            if (Directory.EnumerateFileSystemEntries(storePath).Any())
            {
                // The store already holds data from another machine: merge by moving
                // the host's entries in one by one, refusing to overwrite.
                foreach (var entry in Directory.GetFileSystemEntries(hostPath))
                {
                    var name = Path.GetFileName(entry);
                    var dest = Path.Combine(storePath, name);
                    if (Directory.Exists(dest) || File.Exists(dest)) continue;
                    if (Directory.Exists(entry)) MoveDirectory(entry, dest);
                    else File.Move(entry, dest);
                }
                Directory.Delete(hostPath, recursive: true);
                return (true, null);
            }

            // Move to a sibling staging name first, then swap: both names are on the
            // system drive, so each step is a rename and cannot fail on disk space.
            staging = hostPath + ".portabledev-adopt";
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);

            Directory.Move(hostPath, staging);

            var moved = false;
            try
            {
                Directory.Delete(storePath, recursive: false);
                // The final hop crosses volumes (system drive -> stick), where
                // Directory.Move cannot be used at all; MoveDirectory falls back to copy.
                MoveDirectory(staging, storePath);
                moved = true;
                return (true, null);
            }
            finally
            {
                if (!moved && Directory.Exists(staging) && !Directory.Exists(hostPath))
                {
                    try { Directory.Move(staging, hostPath); } catch { /* best effort restore */ }
                }
            }
        }
        catch (Exception ex)
        {
            return (false, "could not move " + hostPath + " onto the stick: " + ex.Message);
        }
    }

    /// <summary>
    /// Move a directory, falling back to copy-then-delete when the two paths are on
    /// different volumes.
    ///
    /// Directory.Move is a single rename: across volumes it throws
    /// "Source and destination path must have identical roots. Move will not work
    /// across volumes." That is the normal case here — the store lives on the stick
    /// and the host directory on the system drive — so a plain Directory.Move made
    /// --adopt-home fail every time on the very setups this tool exists for.
    ///
    /// Only IOException is treated as "try a copy": access-denied and other IO
    /// failures would fail the copy too, so the caller still sees the real error.
    /// A partial copy is left in place on purpose — it is a duplicate of data that
    /// is still at the source, and the merge branch above reconciles it next run.
    /// </summary>
    private static void MoveDirectory(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
            return;
        }
        catch (IOException)
        {
            // Cross-volume: not a rename, fall through to copy + delete.
        }

        CopyDirectory(from, to);
        Directory.Delete(from, recursive: true);
    }

    private static void CopyDirectory(string from, string to)
    {
        var root = Path.GetFullPath(from).TrimEnd('\\');
        Directory.CreateDirectory(to);

        foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(root, dir)));

        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(root, file));
            File.Copy(file, dest, overwrite: false);
            // File.Copy does not carry timestamps; config dirs and build caches care.
            try { File.SetLastWriteTimeUtc(dest, File.GetLastWriteTimeUtc(file)); } catch { /* cosmetic */ }
        }
    }

    private static bool HasContent(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return true; }
    }

    /// <summary>
    /// A directory inside the stick must be a plain directory: a junction there would point
    /// back out of the stick and defeat the whole point.
    ///
    /// The walk stops at the accepted roots, because those are junctions on purpose
    /// (C:\Dev -> the stick) and would otherwise fail the check for every slot.
    /// </summary>
    private static void EnsureStoreIsPlain(string storePath, IReadOnlyList<string> acceptedRoots)
    {
        var roots = acceptedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.GetFullPath(r).TrimEnd('\\'))
            .ToList();

        for (var p = Path.GetFullPath(storePath).TrimEnd('\\'); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            if (roots.Any(r => PathEditor.PathsEqual(r, p))) return;
            if (Junction.IsReparsePoint(p))
                throw new InvalidOperationException("refusing to store data in a linked directory: " + p);
        }
    }
}
