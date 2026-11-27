using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PortableDev.Proc;

/// <summary>Limits applied to a job object (the process-tree boundary).</summary>
public sealed record JobLimits
{
    /// <summary>Hard per-process commit limit, or null for none.</summary>
    public long? ProcessMemoryBytes { get; init; }

    /// <summary>Maximum number of active processes in the job, or null for none.</summary>
    public uint? ActiveProcesses { get; init; }

    /// <summary>Hard CPU cap in percent of one core (1-100), or null for unlimited.</summary>
    public int? CpuRatePercent { get; init; }

    /// <summary>Kill every member when the last job handle closes (orphan cleanup).</summary>
    public bool KillOnJobClose { get; init; } = true;

    /// <summary>
    /// Let children spawned with CREATE_BREAKAWAY_FROM_JOB leave the tree. Required for detach;
    /// a job without this flag refuses to let a child escape.
    /// </summary>
    public bool AllowBreakaway { get; init; } = true;
}

/// <summary>A Windows job object: the tree boundary used for kill, limits and orphan cleanup.</summary>
public sealed class JobObject : IDisposable
{
    private IntPtr _handle;

    private JobObject(IntPtr handle) => _handle = handle;

    public IntPtr Handle => _handle;

    public static JobObject Create(JobLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw NativeMethods.Error("CreateJobObject");
        }

        try
        {
            var flags = 0u;
            if (limits.KillOnJobClose) flags |= NativeMethods.JobObjectLimitKillOnJobClose;
            if (limits.AllowBreakaway) flags |= NativeMethods.JobObjectLimitBreakawayOk;
            if (limits.ActiveProcesses is not null) flags |= NativeMethods.JobObjectLimitActiveProcess;
            if (limits.ProcessMemoryBytes is not null) flags |= NativeMethods.JobObjectLimitProcessMemory;

            var info = new NativeMethods.JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation
                {
                    LimitFlags = flags,
                    ActiveProcessLimit = limits.ActiveProcesses ?? 0,
                },
                ProcessMemoryLimit = limits.ProcessMemoryBytes is { } memory
                    ? new UIntPtr((ulong)memory)
                    : UIntPtr.Zero,
            };

            if (!NativeMethods.SetInformationJobObject(
                    handle,
                    NativeMethods.JobObjectExtendedLimitInformationClass,
                    ref info,
                    (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
            {
                throw NativeMethods.Error("SetInformationJobObject");
            }

            if (limits.CpuRatePercent is int percent)
            {
                // Hard cap: 1/100 of a percent units, so 50% => 5000.
                var cpu = new NativeMethods.JobObjectCpuRateControlInformation
                {
                    ControlFlags = NativeMethods.JobObjectCpuRateControlEnable | NativeMethods.JobObjectCpuRateControlHardCap,
                    CpuRate = (uint)(Math.Clamp(percent, 1, 100) * 100),
                };
                if (!NativeMethods.SetInformationJobObjectCpuRate(
                        handle,
                        NativeMethods.JobObjectCpuRateControlInformationClass,
                        ref cpu,
                        (uint)Marshal.SizeOf<NativeMethods.JobObjectCpuRateControlInformation>()))
                {
                    throw NativeMethods.Error("SetInformationJobObject(cpu)");
                }
            }

            return new JobObject(handle);
        }
        catch
        {
            NativeMethods.CloseHandle(handle);
            throw;
        }
    }

    /// <summary>Puts an existing process into the job (needs PROCESS_SET_QUOTA and PROCESS_TERMINATE).</summary>
    public void Assign(IntPtr processHandle)
    {
        if (!NativeMethods.AssignProcessToJobObject(_handle, processHandle))
        {
            throw NativeMethods.Error("AssignProcessToJobObject");
        }
    }

    /// <summary>Kills every member of the job: the only reliable tree kill on Windows.</summary>
    public void Terminate(uint exitCode = 1)
    {
        if (!NativeMethods.TerminateJobObject(_handle, exitCode))
        {
            throw NativeMethods.Error("TerminateJobObject");
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        NativeMethods.CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }
}
