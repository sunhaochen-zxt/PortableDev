using System.Runtime.InteropServices;

namespace PortableDev.Win;

/// <summary>
/// Creates and inspects the stable junction (C:\Dev -> &lt;stick&gt;).
/// A junction is used (not a symlink) because it needs no privilege escalation
/// on the NTFS side and works for both elevated and normal processes.
/// </summary>
public static class Junction
{
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const int MAXIMUM_REPARSE_DATA_BUFFER_SIZE = 16 * 1024;
    private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    /// <summary>True when the path exists and is a reparse point.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Target of the junction, or null when it is not one.</summary>
    public static string? GetTarget(string path)
    {
        try
        {
            if (!IsReparsePoint(path)) return null;

            // FileSystemInfo.LinkTarget only reports symbolic links; for a junction
            // (mount point reparse tag) it is null even though the link is valid.
            // The reparse data itself is the only reliable source.
            var fromReparseData = ReadMountPointTarget(path);
            if (!string.IsNullOrEmpty(fromReparseData)) return fromReparseData;

            var target = new DirectoryInfo(path).LinkTarget;
            return string.IsNullOrEmpty(target) ? null : NormalizeLinkTarget(target);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeLinkTarget(string target)
    {
        // The stored substitute name can come back in several forms:
        //   \??\C:\Dev      normal
        //   \\?\C:\Dev      volume-guid style
        //   \??\Global\C:\Dev
        // The Global\ prefix is what the kernel sometimes stores when the reparse
        // data was written by hand; it is not part of the path, so drop it.
        var t = target;
        if (t.StartsWith(@"\??\", StringComparison.Ordinal)) t = t[4..];
        if (t.StartsWith(@"\\?\", StringComparison.Ordinal)) t = t[4..];
        if (t.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase)) t = t[7..];
        // Some filesystems count the mandatory UTF-16 NUL in SubstituteNameLength; keeping
        // it would make every target comparison fail and force a needless re-create.
        return t.TrimEnd('\0').TrimEnd('\\');
    }

    /// <summary>
    /// Create (or recreate) the junction. Requires the target directory to exist.
    /// Returns false with an error message rather than throwing.
    ///
    /// Creation goes through `mklink /J` rather than a hand-built
    /// REPARSE_DATA_BUFFER. The documented buffer layouts that circulate online are
    /// accepted by the kernel but can be stored with a bogus "\??\Global\" prefix on
    /// current Windows builds; mklink is the reference implementation and cannot
    /// drift. Reading still uses FSCTL_GET_REPARSE_POINT, which is unambiguous.
    /// </summary>
    public static (bool Ok, string? Error) Create(string linkPath, string targetPath)
    {
        try
        {
            var link = linkPath.TrimEnd('\\');
            var target = targetPath.TrimEnd('\\');

            if (!Directory.Exists(target))
                return (false, $"target does not exist: {target}");

            if (Directory.Exists(link))
            {
                if (!IsReparsePoint(link))
                    return (false, $"refusing to replace a real directory: {link}");

                // mklink refuses to run when the link path already exists.
                try { Directory.Delete(link, recursive: false); }
                catch (Exception ex) { return (false, $"could not remove the old junction {link}: {ex.Message}"); }
            }

            var parent = Path.GetDirectoryName(link);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                try { Directory.CreateDirectory(parent); }
                catch (UnauthorizedAccessException)
                {
                    return (false, $"access denied creating {link}; run this step with administrator rights");
                }
            }

            var (exitCode, output) = RunMklink(link, target);

            // mklink's own diagnostics are localised, so never parse them: verify by
            // reading the reparse point back instead.
            var actual = GetTarget(link);
            if (actual is not null && PathsEqual(actual, target))
                return (true, null);

            if (Directory.Exists(link) && !IsReparsePoint(link))
                TryDeleteEmptyDirectory(link);

            var detail = string.IsNullOrWhiteSpace(output) ? string.Empty : $" ({output})";
            return exitCode switch
            {
                5 or 1 => (false, $"access denied creating the junction at {link}; run this step with administrator rights{detail}"),
                _ => (false, $"mklink exited with {exitCode} and {link} does not point at {target}{detail}"),
            };
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Runs `mklink /J` and returns its exit code and combined output.</summary>
    private static (int ExitCode, string Output) RunMklink(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) return (-1, "could not start cmd.exe");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, "mklink timed out");
        }

        var text = (stdout.GetAwaiter().GetResult() + " " + stderr.GetAwaiter().GetResult()).Trim();
        var flattened = string.Join(" ",
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()));

        return (process.ExitCode, flattened);
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteEmptyDirectory(string path)
    {
        try { Directory.Delete(path, recursive: false); } catch { /* best effort */ }
    }

    /// <summary>
    /// Reads the mount-point target straight out of the reparse data. This is the
    /// only way to see a junction's destination: the managed LinkTarget property
    /// reports null for mount points.
    /// </summary>
    private static string? ReadMountPointTarget(string path)
    {
        var handle = CreateFileW(
            path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            return null;

        var buffer = Marshal.AllocHGlobal(MAXIMUM_REPARSE_DATA_BUFFER_SIZE);
        try
        {
            var ok = DeviceIoControl(
                handle, FSCTL_GET_REPARSE_POINT, IntPtr.Zero, 0,
                buffer, MAXIMUM_REPARSE_DATA_BUFFER_SIZE, out var returned, IntPtr.Zero);

            if (!ok || returned < 16) return null;

            var tag = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            if (tag != IO_REPARSE_TAG_MOUNT_POINT) return null;

            var substituteNameOffset = Marshal.ReadInt16(buffer, 8);
            var substituteNameLength = Marshal.ReadInt16(buffer, 10);
            if (substituteNameLength <= 0) return null;

            // PathBuffer starts right after the four USHORTs: 8 + 8 = 16.
            // Validate the claimed range against what the driver actually wrote before
            // handing it to Marshal.Copy: a corrupt or hostile reparse point could
            // otherwise make us read past the native buffer, which raises an
            // AccessViolation that no try/catch in managed code can catch.
            var pathBufferBytes = (long)returned - 16;
            if (substituteNameOffset < 0) return null;
            if (substituteNameLength % 2 != 0) return null;
            if (substituteNameOffset + (long)substituteNameLength > pathBufferBytes) return null;

            var chars = new char[substituteNameLength / 2];
            Marshal.Copy(buffer + 16 + substituteNameOffset, chars, 0, chars.Length);
            return NormalizeLinkTarget(new string(chars));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(handle);
        }
    }

    /// <summary>Remove the junction without touching the target contents.</summary>
    public static (bool Ok, string? Error) Remove(string linkPath)
    {
        try
        {
            var link = linkPath.TrimEnd('\\');
            if (!Directory.Exists(link)) return (true, null);

            if (!IsReparsePoint(link))
                return (false, $"refusing to delete a real directory: {link}");

            Directory.Delete(link, recursive: false);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
