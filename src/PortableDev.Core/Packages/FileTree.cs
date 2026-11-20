using System;
using System.IO;

namespace PortableDev.Core.Packages;

/// <summary>
/// Recursive delete that treats reparse points as links instead of directories.
///
/// <c>Directory.Delete(path, recursive: true)</c> walks into junctions and fails with a
/// parameter error, which left half-deleted tool directories behind. A toolkit that puts
/// persist junctions inside its own trees has to be able to remove those trees.
/// </summary>
public static class FileTree
{
    public static void Delete(string path)
    {
        if (IsLink(path))
        {
            // Deleting a junction removes the link, never what it points at.
            Directory.Delete(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }

        foreach (var entry in Directory.GetFileSystemEntries(path))
        {
            if (IsLink(entry)) Directory.Delete(entry);
            else if (Directory.Exists(entry)) Delete(entry);
            else File.Delete(entry);
        }

        Directory.Delete(path);
    }

    private static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }
}
