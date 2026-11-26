using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PortableDev.Proc;

/// <summary>
/// Opens stdin/stdout/stderr named pipes created by a caller and keeps the inheritable handles that
/// become the child's standard handles. Data never passes through the launcher afterwards.
/// </summary>
public sealed class StdioPipes : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

    // "\.pipe" without any escaping games: two backslashes, dot, backslash, "pipe", backslash.
    private static readonly string PipePrefix = new string(['\\', '\\', '.', '\\', 'p', 'i', 'p', 'e', '\\']);

    private int _disposed;

    private StdioPipes(IntPtr stdin, IntPtr stdout, IntPtr stderr)
    {
        Stdin = stdin;
        Stdout = stdout;
        Stderr = stderr;
    }

    public IntPtr Stdin { get; private set; }

    public IntPtr Stdout { get; private set; }

    public IntPtr Stderr { get; private set; }

    /// <summary>Opens the three pipes; names may be bare (`pd-x-in`) or full (with the pipe prefix).</summary>
    public static StdioPipes Open(string stdinName, string stdoutName, string stderrName)
    {
        var stdin = OpenPipe(stdinName, GenericRead);
        try
        {
            var stdout = OpenPipe(stdoutName, GenericWrite);
            try
            {
                var stderr = OpenPipe(stderrName, GenericWrite);
                return new StdioPipes(stdin, stdout, stderr);
            }
            catch
            {
                CloseHandle(stdout);
                throw;
            }
        }
        catch
        {
            CloseHandle(stdin);
            throw;
        }
    }

    private static IntPtr OpenPipe(string name, uint access)
    {
        var path = name.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase) ? name : PipePrefix + name;

        var attributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = 1,
        };

        var handle = CreateFileW(path, access, 0, ref attributes, OpenExisting, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"cannot open stdio pipe '{name}' (win32={error})");
        }

        return handle;
    }

    /// <summary>
    /// Duplicates this process's stdin/stdout/stderr so a child can inherit them. When
    /// <paramref name="redirectStdoutToStderr"/> is set the child writes its stdout to our stderr,
    /// which keeps an NDJSON event stream on stdout clean.
    /// </summary>
    public static StdioPipes FromCurrentProcess(bool redirectStdoutToStderr = false)
    {
        var process = GetCurrentProcess();
        var stdinDup = Duplicate(process, GetStdHandle(StandardInput));
        try
        {
            var stdoutDup = redirectStdoutToStderr
                ? Duplicate(process, GetStdHandle(StandardError))
                : Duplicate(process, GetStdHandle(StandardOutput));
            try
            {
                var stderrDup = Duplicate(process, GetStdHandle(StandardError));
                return new StdioPipes(stdinDup, stdoutDup, stderrDup);
            }
            catch
            {
                CloseHandle(stdoutDup);
                throw;
            }
        }
        catch
        {
            CloseHandle(stdinDup);
            throw;
        }
    }

    private static IntPtr Duplicate(IntPtr process, IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "this process has no standard handle to duplicate");
        }

        if (!DuplicateHandle(process, handle, process, out var duplicate, 0, 1, DuplicateSameAccess))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot duplicate a standard handle");
        }

        return duplicate;
    }

    public void Dispose()
    {
        // Idempotent: the spawner disposes in a finally block and callers may dispose again.
        // Zero the handles before closing so a second call cannot close a recycled handle value.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var stdin = Stdin;
        var stdout = Stdout;
        var stderr = Stderr;
        Stdin = IntPtr.Zero;
        Stdout = IntPtr.Zero;
        Stderr = IntPtr.Zero;
        if (stdin != IntPtr.Zero) CloseHandle(stdin);
        if (stdout != IntPtr.Zero) CloseHandle(stdout);
        if (stderr != IntPtr.Zero) CloseHandle(stderr);
    }

    private const int StandardInput = -10;
    private const int StandardOutput = -11;
    private const int StandardError = -12;
    private const uint DuplicateSameAccess = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr sourceHandle,
        IntPtr targetProcess,
        out IntPtr targetHandle,
        uint desiredAccess,
        int inheritHandle,
        uint options);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
