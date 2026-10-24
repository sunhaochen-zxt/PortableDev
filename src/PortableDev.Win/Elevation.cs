using System.Runtime.InteropServices;

namespace PortableDev.Win;

/// <summary>
/// Launches a short-lived elevated helper process through the UAC consent prompt
/// so the main program never has to run entirely as administrator. Only the
/// steps that genuinely need administrator rights (creating or deleting the
/// stable junction under C:\) go through here.
/// </summary>
public static class Elevation
{
    private const uint SeeMaskNoCloseProcess = 0x00000040;
    private const uint SeeMaskNoAsync = 0x00000100;
    private const uint SeeMaskFlagNoUi = 0x00000400;
    private const int SwHide = 0;
    private const int ErrorCancelled = 1223;
    private const uint WaitObject0 = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo lpExecInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public sealed record RunResult(
        bool Ok,
        bool Cancelled,
        bool TimedOut,
        int ExitCode,
        string? Error)
    {
        public static RunResult Success() => new(true, false, false, 0, null);
    }

    /// <summary>
    /// Run <paramref name="fileName"/> with <paramref name="arguments"/> elevated.
    /// Returns a result instead of throwing; UAC cancellation is a normal outcome.
    /// </summary>
    public static RunResult RunElevated(string fileName, string arguments, int timeoutMs = 180_000)
    {
        if (IsElevated())
        {
            // Nothing to elevate relative to an already-elevated parent. Callers
            // should normally avoid this, but behave sanely if they do not.
            return new RunResult(false, false, false, -1,
                "already elevated; run the step inline instead of brokering it");
        }

        var sei = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskNoCloseProcess | SeeMaskNoAsync | SeeMaskFlagNoUi,
            hwnd = IntPtr.Zero,
            lpVerb = "runas",
            lpFile = fileName,
            lpParameters = arguments,
            lpDirectory = SafeDirectoryOf(fileName),
            nShow = SwHide,
        };

        if (!ShellExecuteExW(ref sei))
        {
            var err = Marshal.GetLastWin32Error();
            return err == ErrorCancelled
                ? new RunResult(false, true, false, -1, "the UAC prompt was declined")
                : new RunResult(false, false, false, -1, $"ShellExecuteEx failed, Win32 error {err}");
        }

        try
        {
            if (sei.hProcess == IntPtr.Zero)
                return new RunResult(false, false, false, -1,
                    "no process handle returned; the elevated helper did not start");

            var wait = WaitForSingleObject(sei.hProcess, (uint)Math.Max(1, timeoutMs));
            if (wait != WaitObject0)
                return new RunResult(false, false, true, -1,
                    $"the elevated helper did not finish within {timeoutMs / 1000}s");

            if (!GetExitCodeProcess(sei.hProcess, out var code))
                return new RunResult(false, false, false, -1, "could not read the helper exit code");

            return code == 0
                ? RunResult.Success()
                : new RunResult(false, false, false, (int)code, $"the elevated helper exited with code {code}");
        }
        finally
        {
            CloseHandle(sei.hProcess);
        }
    }

    /// <summary>True when the current process token is a member of the Administrators group.</summary>
    public static bool IsElevated() => RegistryEnvironment.IsElevated();

    private static string? SafeDirectoryOf(string path)
    {
        try { return Path.GetDirectoryName(path); }
        catch { return null; }
    }
}

/// <summary>
/// Works out how to re-launch this program, whether it was started through its
/// own apphost (PortableDev.exe) or through the shared dotnet host
/// (dotnet PortableDev.dll). The elevated helper must be invoked the same way
/// or it will start the wrong thing.
/// </summary>
public static class SelfInvocation
{
    /// <summary>Path to the executable to launch.</summary>
    public static string File => Environment.ProcessPath ?? string.Empty;

    /// <summary>
    /// Arguments that must precede the real ones (the .dll path when the host is
    /// dotnet.exe, otherwise empty).
    /// </summary>
    public static IReadOnlyList<string> Prefix { get; } = BuildPrefix();

    /// <summary>Full command line (without the leading program name) for <paramref name="args"/>.</summary>
    public static string BuildArguments(params string[] args)
        => Core.CommandLine.Join(Prefix.Concat(args));

    private static IReadOnlyList<string> BuildPrefix()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return Array.Empty<string>();

        var name = Path.GetFileNameWithoutExtension(exe);
        if (!name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();

        // Running under `dotnet PortableDev.dll`: the helper needs the assembly path.
        var assembly = typeof(SelfInvocation).Assembly.Location;
        return string.IsNullOrEmpty(assembly) ? Array.Empty<string>() : new[] { assembly };
    }

    /// <summary>True when the current process is the apphost, not the shared host.</summary>
    public static bool IsSelfContainedHost
        => !string.Equals(Path.GetFileNameWithoutExtension(File), "dotnet", StringComparison.OrdinalIgnoreCase);
}
