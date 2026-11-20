using System;
using System.Runtime.InteropServices;

namespace PortableDev.Win;

/// <summary>
/// Hard links for files that must live outside their version directory.
///
/// A junction only works on directories; a hard link is the file equivalent, needs no
/// administrator rights, and — unlike a path — stores no drive letter at all, so it keeps
/// working when the stick gets a different one.
/// </summary>
public static class HardLink
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    public static (bool Ok, string? Error) Create(string linkPath, string targetPath)
    {
        try
        {
            if (!System.IO.File.Exists(targetPath))
                return (false, "目标文件不存在：" + targetPath);

            if (CreateHardLinkW(linkPath, targetPath, IntPtr.Zero)) return (true, null);

            var code = Marshal.GetLastWin32Error();
            return (false, code == 183
                ? "目标已存在：" + linkPath
                : "CreateHardLink 失败（错误码 " + code + "）");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
