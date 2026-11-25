using System.Runtime.InteropServices;
using System.Text;

namespace PortableDev.Core.Identity;

/// <summary>Volume facts used to derive a drive-letter independent stick id (kernel.md 4.4).</summary>
public sealed record VolumeInfo(string Root, uint Serial, string Label, string FileSystem)
{
    public string SerialHex => Serial.ToString("X8");
}

public static class VolumeReader
{
    /// <summary>Reads volume information for a drive root such as <c>Z:\\</c>.</summary>
    public static VolumeInfo Read(string root)
    {
        var normalized = NormalizeRoot(root);
        var label = new StringBuilder(261);
        var fs = new StringBuilder(261);
        if (!GetVolumeInformationW(normalized, label, label.Capacity, out var serial, out _, out _, fs, fs.Capacity))
        {
            var error = Marshal.GetLastWin32Error();
            throw new IOException($"GetVolumeInformation failed for '{normalized}' (win32 {error})");
        }

        return new VolumeInfo(normalized, serial, label.ToString(), fs.ToString());
    }

    public static bool TryRead(string root, out VolumeInfo? info, out string? error)
    {
        try
        {
            info = Read(root);
            error = null;
            return true;
        }
        catch (IOException ex)
        {
            info = null;
            error = ex.Message;
            return false;
        }
    }

    private static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var pathRoot = Path.GetPathRoot(full) ?? full;
        return pathRoot.EndsWith(Path.DirectorySeparatorChar) ? pathRoot : pathRoot + Path.DirectorySeparatorChar;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);
}
