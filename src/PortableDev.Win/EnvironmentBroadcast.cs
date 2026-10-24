using System;
using System.Runtime.InteropServices;

namespace PortableDev.Win;

/// <summary>
/// Tells every top-level window that the environment changed.
///
/// Without this the registry is correct but Explorer keeps its old environment
/// block, so every process started from the shell afterwards — including a brand
/// new terminal window — inherits a stale PATH. The user sees "command not found"
/// even though apply reported success. That failure mode is silent and looks like
/// a bug in the tool, so the broadcast is not optional.
/// </summary>
public static class EnvironmentBroadcast
{
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SMTO_BLOCK = 0x0001;

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    /// <summary>
    /// Broadcast the change. Returns false when the broadcast could not be sent;
    /// callers treat that as a warning, never as a failure, because the registry
    /// write itself already succeeded and a reboot fixes the rest.
    /// </summary>
    public static bool Notify()
    {
        try
        {
            var result = SendMessageTimeout(
                HWND_BROADCAST, WM_SETTINGCHANGE, UIntPtr.Zero, "Environment",
                SMTO_ABORTIFHUNG | SMTO_BLOCK, 5000, out _);
            return result != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }
}
