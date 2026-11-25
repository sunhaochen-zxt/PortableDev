using System.Collections.Concurrent;
using PortableDev.Core.Protocol;

namespace PortableDev.Kernel;

/// <summary>Process-wide runtime state shared by the pipe server, router and builtin commands.</summary>
public sealed class KernelState
{
    private long _nextTask;

    public required string KernelVersion { get; init; }

    public required int Abi { get; init; }

    public required DateTimeOffset BootedAt { get; init; }

    public required string StickId { get; init; }

    public required string Root { get; init; }

    public required string StableRoot { get; init; }

    public required string MachineId { get; init; }

    public required string BootId { get; init; }

    public bool Stopping { get; private set; }

    /// <summary>Force shutdown: kill extensions immediately instead of a graceful drain.</summary>
    public bool ForceShutdown { get; private set; }

    public int ActiveTasks => _activeTasks;

    private int _activeTasks;

    public int SessionCount { get; set; }

    public int ExtensionCount { get; set; }

    public string NextTaskId() => "t" + Interlocked.Increment(ref _nextTask);

    public void EnterTask() => Interlocked.Increment(ref _activeTasks);

    public void LeaveTask() => Interlocked.Decrement(ref _activeTasks);

    public void BeginShutdown(bool force = false)
    {
        Stopping = true;
        ForceShutdown |= force;
    }

}
