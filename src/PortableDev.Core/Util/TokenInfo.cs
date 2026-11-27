using System.Runtime.InteropServices;

namespace PortableDev.Core.Util;

/// <summary>Process token integrity (UAC elevation) helpers.</summary>
public static class TokenInfo
{
    public const uint MediumIntegrity = 0x2000;
    public const uint HighIntegrity = 0x3000;

    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevelClass = 25;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, uint length, out uint returned);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>Token integrity sub-authority (S-1-16-xxxx), or 0 when unreadable.</summary>
    public static uint Integrity()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token))
        {
            return 0;
        }

        var buffer = IntPtr.Zero;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevelClass, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
            {
                return 0;
            }

            buffer = Marshal.AllocHGlobal((int)needed);
            if (!GetTokenInformation(token, TokenIntegrityLevelClass, buffer, needed, out _))
            {
                return 0;
            }

            var sid = Marshal.ReadIntPtr(buffer);
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0)
            {
                return 0;
            }

            return (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            CloseHandle(token);
        }
    }

    /// <summary>True when the current process runs at High integrity or above.</summary>
    public static bool IsElevated() => Integrity() >= HighIntegrity;

    /// <summary>Human readable level for logs/status output.</summary>
    public static string Describe()
    {
        var integrity = Integrity();
        return integrity switch
        {
            0 => "unknown",
            >= HighIntegrity => "high (" + integrity + ")",
            >= MediumIntegrity => "medium (" + integrity + ")",
            _ => "low (" + integrity + ")",
        };
    }
}
