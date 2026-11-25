using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace PortableDev.ProcExt;

/// <summary>Best-effort Windows process enumeration and the level classification (proc.md 6).</summary>
internal static class SystemProcesses
{
    private const uint Th32CsSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessTerminate = 0x0001;
    private const uint TokenQuery = 0x0008;

    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe",
    };

    public static JsonObject Snapshot(string? filter)
    {
        var processes = new JsonArray();
        var denied = 0;
        foreach (var entry in Enumerate())
        {
            if (!string.IsNullOrEmpty(filter)
                && entry.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                && !string.Equals(entry.Pid.ToString(CultureInfo.InvariantCulture), filter, StringComparison.Ordinal))
            {
                continue;
            }

            var item = Describe(entry.Pid, entry.Name, entry.ParentPid);
            if (item["level"]?.GetValue<int>() == 1)
            {
                denied++;
            }

            processes.Add(item);
        }

        return new JsonObject { ["processes"] = processes, ["denied"] = denied };
    }

    public static JsonObject Describe(int pid, string? name = null, int parentPid = 0)
    {
        if (name is null && pid > 0)
        {
            name = SafeName(pid);
        }

        var critical = name is not null && CriticalNames.Contains(name);
        var handle = pid > 0 ? OpenProcess(ProcessQueryLimitedInformation, false, pid) : IntPtr.Zero;
        string? path = null;
        string? started = null;
        string? user = null;
        var level = 0;

        if (pid <= 0 || handle == IntPtr.Zero)
        {
            level = pid <= 4 ? 2 : 1;
        }
        else
        {
            try
            {
                path = QueryPath(handle);
                started = StartTime(handle);
                user = TokenUser(handle);
                if (IsCritical(handle, name))
                {
                    level = 2;
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        if (critical)
        {
            level = 2;
        }

        return new JsonObject
        {
            ["pid"] = pid,
            ["ppid"] = parentPid,
            ["name"] = name,
            ["path"] = path,
            ["startTime"] = started,
            ["user"] = user,
            ["level"] = level,
        };
    }

    /// <summary>Ends one process; refuses critical ones unless the caller opted in.</summary>
    public static JsonObject Kill(int pid, bool forceCritical)
    {
        if (pid <= 4)
        {
            return Error("ERR_UNSUPPORTED", "pid " + pid + " is a system process PD refuses to touch");
        }

        var described = Describe(pid);
        var level = described["level"]?.GetValue<int>() ?? 0;
        if (level == 2 && !forceCritical)
        {
            return Error("ERR_UNSUPPORTED", "critical process: pass --force-critical to end it anyway");
        }

        var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return error == 87
                ? Error("ERR_PROC_NOT_FOUND", "no process with pid " + pid)
                : Error("ERR_ELEVATION", "cannot open pid " + pid + ": access denied (try --elevated)");
        }

        try
        {
            if (!TerminateProcess(handle, 1))
            {
                return Error("ERR_IO", "TerminateProcess failed: " + Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            CloseHandle(handle);
        }

        return new JsonObject { ["ok"] = true, ["pid"] = pid, ["level"] = level };
    }

    public static JsonObject Error(string code, string message) => new()
    {
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private sealed record Entry(int Pid, int ParentPid, string Name);

    private static IEnumerable<Entry> Enumerate()
    {
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == new IntPtr(-1))
        {
            yield break;
        }

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                yield break;
            }

            do
            {
                yield return new Entry((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile ?? string.Empty);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private static string? SafeName(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? QueryPath(IntPtr handle)
    {
        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
    }

    private static string? StartTime(IntPtr handle)
    {
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
        {
            return null;
        }

        var value = ((long)creation.High << 32) | creation.Low;
        return value <= 0 ? null : DateTime.FromFileTimeUtc(value).ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? TokenUser(IntPtr handle)
    {
        if (!OpenProcessToken(handle, TokenQuery, out var token))
        {
            return null;
        }

        try
        {
            GetTokenInformation(token, 1, IntPtr.Zero, 0, out var needed);
            if (needed <= 0)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!GetTokenInformation(token, 1, buffer, (int)needed, out _))
                {
                    return null;
                }

                var sid = Marshal.ReadIntPtr(buffer);
                var name = new StringBuilder(256);
                var domain = new StringBuilder(256);
                var nameLength = (uint)name.Capacity;
                var domainLength = (uint)domain.Capacity;
                if (LookupAccountSidW(null, sid, name, ref nameLength, domain, ref domainLength, out _))
                {
                    var account = name.ToString();
                    var prefix = domain.Length > 0 ? domain + "\\" : string.Empty;
                    return prefix + account;
                }

                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static bool IsCritical(IntPtr handle, string? name)
    {
        if (name is not null && CriticalNames.Contains(name))
        {
            return true;
        }

        try
        {
            return IsProcessCritical(handle, out var critical) && critical;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(IntPtr process, out bool critical);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountSidW(string? system, IntPtr sid, StringBuilder name, ref uint nameLength,
        StringBuilder domain, ref uint domainLength, out int use);
}
