using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace PortableDev.Proc;

/// <summary>What to run and how it should be isolated.</summary>
public sealed record ProcessStartSpec
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Give the child its own console instead of inheriting ours.</summary>
    public bool NewConsole { get; init; }

    /// <summary>Put the child in a new process group (keeps Ctrl+C from reaching it).</summary>
    public bool NewProcessGroup { get; init; }

    /// <summary>
    /// Spawn outside the caller's job. The child must carry CREATE_BREAKAWAY_FROM_JOB and the
    /// caller's job must allow breakaway; there is no way to detach a process after it started.
    /// </summary>
    public bool Detached { get; init; }

    /// <summary>Inheritable stdio handles: pass all three, or none.</summary>
    public IntPtr? StandardInput { get; init; }

    public IntPtr? StandardOutput { get; init; }

    public IntPtr? StandardError { get; init; }
}

/// <summary>A running child; the job (when any) owns the tree that contains it.</summary>
public sealed class SpawnedProcess : IDisposable
{
    private IntPtr _process;
    private IntPtr _thread;

    internal SpawnedProcess(int pid, IntPtr process, IntPtr thread)
    {
        Pid = pid;
        _process = process;
        _thread = thread;
    }

    public int Pid { get; }

    public bool HasExited => NativeMethods.WaitForSingleObject(_process, 0) == NativeMethods.WaitObject0;

    /// <summary>Waits for exit; true when the process ended inside the timeout.</summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        var milliseconds = (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
        return NativeMethods.WaitForSingleObject(_process, milliseconds) == NativeMethods.WaitObject0;
    }

    public bool TryGetExitCode(out int exitCode)
    {
        exitCode = 0;
        if (!NativeMethods.GetExitCodeProcess(_process, out var code)) return false;
        if (code == 259) return false; // STILL_ACTIVE
        exitCode = unchecked((int)code);
        return true;
    }

    /// <summary>Kills this process only; terminate the owning job to kill the whole tree.</summary>
    public void Kill(uint exitCode = 1)
    {
        if (!HasExited)
        {
            NativeMethods.TerminateProcess(_process, exitCode);
        }
    }

    public void Dispose()
    {
        if (_thread != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_thread);
            _thread = IntPtr.Zero;
        }

        if (_process != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_process);
            _process = IntPtr.Zero;
        }
    }
}

/// <summary>Launches children either inside a new job (supervised) or detached from any tree.</summary>
public static class ProcessSpawner
{
    public static (SpawnedProcess Process, JobObject? Job) Spawn(ProcessStartSpec spec, JobLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.FileName))
        {
            throw new ArgumentException("FileName is required", nameof(spec));
        }

        var job = limits is null ? null : JobObject.Create(limits);
        if (job is null)
        {
            return Create(spec, null, ownsJob: false);
        }

        try
        {
            return Create(spec, job, ownsJob: true);
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    /// <summary>Launches a child inside an existing job; the caller keeps ownership of the job.</summary>
    public static SpawnedProcess SpawnInto(ProcessStartSpec spec, JobObject job)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(job);
        if (string.IsNullOrWhiteSpace(spec.FileName))
        {
            throw new ArgumentException("FileName is required", nameof(spec));
        }

        return Create(spec, job, ownsJob: false).Process;
    }

    private static (SpawnedProcess Process, JobObject? Job) Create(ProcessStartSpec spec, JobObject? job, bool ownsJob)
    {
        var startup = BuildStartup(spec, out var inheritHandles);

        var flags = NativeMethods.CreateSuspended;
        if (spec.NewConsole) flags |= NativeMethods.CreateNewConsole;
        if (spec.NewProcessGroup) flags |= NativeMethods.CreateNewProcessGroup;
        if (spec.Detached) flags |= NativeMethods.CreateBreakawayFromJob;

        var environment = IntPtr.Zero;
        if (spec.Environment is not null)
        {
            environment = BuildEnvironmentBlock(spec.Environment);
            flags |= NativeMethods.CreateUnicodeEnvironment;
        }

        var commandLine = new StringBuilder(BuildCommandLine(spec));
        var applicationName = HasPath(spec.FileName) ? spec.FileName : null;
        var created = false;
        try
        {
            if (!NativeMethods.CreateProcessW(
                    applicationName,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles,
                    flags,
                    environment,
                    spec.WorkingDirectory,
                    ref startup,
                    out var info))
            {
                throw NativeMethods.Error("CreateProcess");
            }

            created = true;
            try
            {
                job?.Assign(info.hProcess);

                // Creating suspended closes the race between "child started" and "child is in the
                // job"; the child cannot run a single instruction before it is assigned.
                if (NativeMethods.ResumeThread(info.hThread) == uint.MaxValue)
                {
                    throw NativeMethods.Error("ResumeThread");
                }

                return (new SpawnedProcess(info.dwProcessId, info.hProcess, info.hThread), job);
            }
            catch
            {
                NativeMethods.TerminateProcess(info.hProcess, 1);
                NativeMethods.CloseHandle(info.hProcess);
                NativeMethods.CloseHandle(info.hThread);
                throw;
            }
        }
        catch
        {
            if (!created && ownsJob && job is not null)
            {
                job.Dispose();
            }

            throw;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }
        }
    }

    private static NativeMethods.StartupInfo BuildStartup(ProcessStartSpec spec, out bool inheritHandles)
    {
        var provided = (spec.StandardInput is null ? 0 : 1)
            + (spec.StandardOutput is null ? 0 : 1)
            + (spec.StandardError is null ? 0 : 1);

        if (provided is not (0 or 3))
        {
            throw new ArgumentException("provide all three stdio handles, or none", nameof(spec));
        }

        inheritHandles = provided == 3;
        var startup = new NativeMethods.StartupInfo
        {
            cb = Marshal.SizeOf<NativeMethods.StartupInfo>(),
        };

        if (inheritHandles)
        {
            startup.dwFlags = NativeMethods.StartfUseStdHandles;
            startup.hStdInput = spec.StandardInput!.Value;
            startup.hStdOutput = spec.StandardOutput!.Value;
            startup.hStdError = spec.StandardError!.Value;
        }

        return startup;
    }

    /// <summary>A bare file name relies on the PATH search in CreateProcess (lpApplicationName null).</summary>
    private static bool HasPath(string fileName)
        => fileName.Contains('\\') || fileName.Contains('/') || fileName.Contains(':');

    private static string BuildCommandLine(ProcessStartSpec spec)
    {
        var builder = new StringBuilder();
        builder.Append(Quote(spec.FileName));
        foreach (var argument in spec.Arguments)
        {
            builder.Append(' ').Append(Quote(argument));
        }

        return builder.ToString();
    }

    /// <summary>The MSVCRT argument quoting rules; the same ones the C runtime parses back.</summary>
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"', '\n', '\v']) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder(argument.Length + 2);
        builder.Append('"');

        for (var index = 0; index < argument.Length; index++)
        {
            var backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                index++;
                backslashes++;
            }

            if (index == argument.Length)
            {
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
                continue;
            }

            builder.Append('\\', backslashes).Append(argument[index]);
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var entries = environment
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + "=" + pair.Value);

        var block = string.Join('\0', entries) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }
}
