using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using PortableDev.Proc;

namespace PortableDev.ProcExt;

/// <summary>In-memory registry of processes this extension spawned (one job per process group).</summary>
internal sealed class ManagedProcesses
{
    private readonly ConcurrentDictionary<string, Entry> _items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, byte> _reaped = new();
    private long _counter;

    public string NextId()
        => "p-" + Interlocked.Increment(ref _counter).ToString("x4", CultureInfo.InvariantCulture);

    public void Add(string procId, string kind, int pid, string? command, JobObject? job)
    {
        _items[procId] = new Entry(procId, kind, pid, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), command, job);
        _reaped.TryRemove(pid, out _);
    }

    public void Remove(string procId)
    {
        if (_items.TryRemove(procId, out var removed))
        {
            _reaped[removed.Pid] = 0;
        }
    }

    public bool WasReaped(int pid) => _reaped.ContainsKey(pid);

    public bool Kill(int pid)
    {
        foreach (var entry in _items.Values)
        {
            if (entry.Pid != pid)
            {
                continue;
            }

            try
            {
                entry.Job?.Terminate();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    public JsonArray Snapshot()
    {
        var items = new JsonArray();
        foreach (var entry in _items.Values.OrderBy(e => e.ProcId, StringComparer.Ordinal))
        {
            items.Add(new JsonObject
            {
                ["procId"] = entry.ProcId,
                ["kind"] = entry.Kind,
                ["pid"] = entry.Pid,
                ["startTime"] = entry.StartTimeUtc,
                ["command"] = entry.Command,
            });
        }

        return items;
    }

    private sealed record Entry(string ProcId, string Kind, int Pid, string StartTimeUtc, string? Command, JobObject? Job);
}
