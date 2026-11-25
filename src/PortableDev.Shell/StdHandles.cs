using System.Runtime.InteropServices;

namespace PortableDev.Shell;

/// <summary>
/// The shell must not leak its own std handles into the daemon it spawns: if pd is piped
/// (pd version | findstr ...), an inherited stdout write end would keep the pipe open after pd exits.
/// </summary>
internal static class StdHandles
{
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint HandleFlagInherit = 0x1;

    public static void DetachInheritance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var handleId in new[] { StdInputHandle, StdOutputHandle, StdErrorHandle })
        {
            var handle = GetStdHandle(handleId);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                continue;
            }

            SetHandleInformation(handle, HandleFlagInherit, 0);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);
}
