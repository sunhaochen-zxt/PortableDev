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

    // ---- An object's own label (a folder on the stick), not the process's ---------------

    private const int SeFileObject = 1;
    private const uint LabelSecurityInformation = 0x00000010;
    private const byte SystemMandatoryLabelAceType = 0x11;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetNamedSecurityInfoW(
        string objectName, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, out IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("advapi32.dll")]
    private static extern bool GetAce(IntPtr acl, uint aceIndex, out IntPtr ace);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    /// <summary>
    /// The integrity level stored on a file or folder, or null when it has none or cannot be
    /// read. A folder's label is inherited by the folders created inside it later, which is
    /// why one Low root is enough to make a whole stick Low.
    /// </summary>
    public static uint? RidOf(string path)
    {
        var sacl = IntPtr.Zero;
        var descriptor = IntPtr.Zero;
        try
        {
            var rc = GetNamedSecurityInfoW(path, SeFileObject, LabelSecurityInformation,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out sacl, out descriptor);
            if (rc != 0 || sacl == IntPtr.Zero) return null;

            var aceCount = Marshal.ReadInt16(sacl, 4);
            for (var i = 0; i < aceCount; i++)
            {
                if (!GetAce(sacl, (uint)i, out var ace) || ace == IntPtr.Zero) continue;
                if (Marshal.ReadByte(ace) != SystemMandatoryLabelAceType) continue;

                // SYSTEM_MANDATORY_LABEL_ACE: ACE_HEADER (4 bytes), ACCESS_MASK (4), then the SID.
                var sid = IntPtr.Add(ace, 8);
                if (Marshal.ReadByte(sid, 1) == 0) return null;
                // S-1-16-<rid>: the level is the first (and only) sub-authority.
                return (uint)Marshal.ReadInt32(IntPtr.Add(sid, 8));
            }
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return null;
        }
        finally
        {
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
        }
    }

    /// <summary>
    /// Raise the label on a folder (and through inheritance on everything created under it
    /// later) to Medium. The write goes through icacls, the reference implementation: building
    /// the label ACE by hand is the same trap as hand-built junction buffers. Only the label
    /// changes; permissions, owner and contents are left alone.
    /// </summary>
    public static (bool Ok, string? Error) EnsureMedium(string path)
    {
        if (!System.IO.Directory.Exists(path))
            return (false, "not a directory: " + path);

        var psi = new System.Diagnostics.ProcessStartInfo("icacls.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("/setintegritylevel");
        psi.ArgumentList.Add("(OI)(CI)M");
        psi.ArgumentList.Add("/C");
        psi.ArgumentList.Add("/Q");

        string output;
        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return (false, "could not start icacls");
            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "icacls timed out");
            }
            if (process.ExitCode != 0)
                return (false, $"icacls exited {process.ExitCode}: {output.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        // The exit code is not proof; read the label back.
        var rid = RidOf(path);
        if (rid is null) return (false, "label unreadable after the write");
        if (!CanWriteToMedium(rid.Value)) return (false, "label is still " + NameOf(rid.Value));
        return (true, null);
    }

    /// <summary>
    /// Same repair, but through the elevation broker (ShellExecute + runas).
    ///
    /// A process that is itself Low cannot change a label whichever API it calls: the
    /// integrity check applies to the caller. Launching icacls.exe works because its image
    /// is Medium, so the child's effective level is the lower of the (elevated) token and
    /// that image - Medium. An already-elevated parent needs no consent prompt; otherwise
    /// UAC asks once, which is the same prompt the junction already produces.
    /// </summary>
    public static (bool Ok, string? Error) EnsureMediumViaBroker(string path)
    {
        if (!System.IO.Directory.Exists(path))
            return (false, "not a directory: " + path);

        // Absolute path on purpose: resolving "icacls.exe" through PATH would let a folder
        // on the stick answer the call instead of the system binary.
        var icacls = System.IO.Path.Combine(Environment.SystemDirectory, "icacls.exe");
        if (!System.IO.File.Exists(icacls))
            return (false, "icacls.exe not found in " + Environment.SystemDirectory);

        var psi = new System.Diagnostics.ProcessStartInfo(icacls,
            "\"" + path + "\" /setintegritylevel (OI)(CI)M /C /Q")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };

        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null) return (false, "could not start icacls");
            if (!process.WaitForExit(300_000))
            {
                try { process.Kill(); } catch { }
                return (false, "icacls timed out");
            }
            if (process.ExitCode != 0)
                return (false, $"icacls exited {process.ExitCode}" + (process.ExitCode == 1223
                    ? " (the UAC prompt was declined)"
                    : string.Empty));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        var rid = RidOf(path);
        if (rid is not null && CanWriteToMedium(rid.Value)) return (true, null);
        return (false, "label is still " + NameOf(rid ?? 0));
    }
}
