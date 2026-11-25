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

    private StdioPipes(IntPtr stdin, IntPtr stdout, IntPtr stderr)
    {
        Stdin = stdin;
        Stdout = stdout;
        Stderr = stderr;
    }

    public IntPtr Stdin { get; }

    public IntPtr Stdout { get; }

    public IntPtr Stderr { get; }

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

    public void Dispose()
    {
        if (Stdin != IntPtr.Zero) CloseHandle(Stdin);
        if (Stdout != IntPtr.Zero) CloseHandle(Stdout);
        if (Stderr != IntPtr.Zero) CloseHandle(Stderr);
    }

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
