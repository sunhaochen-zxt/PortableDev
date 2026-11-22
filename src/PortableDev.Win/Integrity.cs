using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PortableDev.Win;

/// <summary>
/// Reads the mandatory integrity level (IL) of the current process token.
///
/// Why this matters here: Windows starts a process at the *lower* of two levels —
/// the parent token's and the executable image's own integrity label. So a stick
/// whose folder carries a "Low" label makes every program started from there run at
/// Low, and a Low process may not write to a directory that is Medium with
/// no-write-up. On this project that surfaced as <c>apply</c> failing with
/// "Access to the path ... is denied" and <c>verify</c> hanging on Flutter's startup
/// lock, while the same commands typed into a normal shell worked — because the
/// shell was Medium and the published executable was Low.
///
/// Nothing here changes anything; it only reports.
/// </summary>
public static class Integrity
{
    // TOKEN_INFORMATION_CLASS.TokenIntegrityLevel
    private const int TokenIntegrityLevel = 25;
    private const uint TokenQuery = 0x0008;

    // SECURITY_MANDATORY_*_RID
    private const uint RidUntrusted = 0x0000;
    private const uint RidLow = 0x1000;
    private const uint RidMedium = 0x2000;
    private const uint RidHigh = 0x3000;
    private const uint RidSystem = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle, int informationClass, IntPtr information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthorityIndex);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Name of the level behind a mandatory RID: "Low", "Medium", "High", "System".
    /// Unknown RIDs are reported as-is so a future Windows level is still visible.
    /// </summary>
    public static string NameOf(uint rid) => rid switch
    {
        0 => "Untrusted",
        RidLow => "Low",
        RidMedium => "Medium",
        RidHigh => "High",
        RidSystem => "System",
        _ => "0x" + rid.ToString("X"),
    };

    /// <summary>True for the levels that may write to a Medium no-write-up directory.</summary>
    public static bool CanWriteToMedium(uint rid) => rid >= RidMedium;

    /// <summary>
    /// The current process's integrity level, or null when it could not be read.
    /// Null is not treated as a failure by callers: the check is advisory.
    /// </summary>
    public static uint? CurrentRid()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return null;

            // First call asks for the size; the second fills the buffer.
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var size);
            if (size <= 0) return null;

            buffer = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _)) return null;

            var label = Marshal.PtrToStructure<SidAndAttributes>(buffer);
            if (label.Sid == IntPtr.Zero) return null;

            var count = Marshal.ReadByte(GetSidSubAuthorityCount(label.Sid));
            if (count == 0) return null;

            // The level is the last sub-authority of the token's integrity SID.
            var last = GetSidSubAuthority(label.Sid, (uint)(count - 1));
            return last == IntPtr.Zero ? null : (uint)Marshal.ReadInt32(last);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>Name of the current process's integrity level, or null when unknown.</summary>
    public static string? CurrentName()
    {
        var rid = CurrentRid();
        return rid is null ? null : NameOf(rid.Value);
    }
}
