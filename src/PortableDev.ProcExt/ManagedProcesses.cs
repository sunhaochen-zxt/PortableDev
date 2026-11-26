using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using PortableDev.Proc;

namespace PortableDev.ProcExt;

/// <summary>
/// In-memory registry of processes this extension spawned (one job per process group). Every entry
/// has exactly one owner: whichever caller removes it from the map also terminates/disposes its job
/// under that entry's own lock, so a concurrent proc.kill and the spawner cleanup can never dispose
/// the same job twice.
/// </summary>
internal sealed class ManagedProcesses
{
    /// <summary>How many recently reaped pids are remembered for pid-reuse diagnostics.</summary>
    private const int MaxReaped = 1024;

    private readonly ConcurrentDictionary<string, Entry> _items = new(StringComparer.Ordinal);
    private readonly object _reapedGate = new();
    private readonly Dictionary<int, long> _reaped = new();
    private readonly Queue<(int Pid, long Sequence)> _reapedOrder = new();
    private long _reapedSequence;
    private long _counter;

    public string NextId()
        => "p-" + Interlocked.Increment(ref _counter).ToString("x4", CultureInfo.InvariantCulture);

    public void Add(string procId, string kind, int pid, string? command, JobObject? job)
    {
        ForgetReaped(pid);
        _items[procId] = new Entry(procId, kind, pid, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), command, job);
    }

    /// <summary>
    /// The spawner's cleanup: releases the entry only while the spawner still owns it. When a
    /// concurrent <see cref="TryTerminate"/> already removed it, that path owns the job and this is
    /// a no-op.
    /// </summary>
    public void Remove(string procId)
    {
        if (!_items.TryRemove(procId, out var removed))
        {
            return;
        }

        Release(removed, terminate: false);
        RememberReaped(removed.Pid);
    }

    /// <summary>
    /// The single termination path: atomically removes the entry, then terminates and disposes its
    /// job under the entry's own lock. Returns false once another owner has already released it.
    /// </summary>
    public bool TryTerminate(string procId)
    {
        if (!_items.TryRemove(procId, out var entry))
        {
            return false;
        }

        Release(entry, terminate: true);
        RememberReaped(entry.Pid);
        return true;
    }

    public bool Kill(int pid)
    {
        foreach (var entry in _items.Values)
        {
            if (entry.Pid == pid && TryTerminate(entry.ProcId))
            {
                return true;
            }
        }

        return false;
    }

    public bool WasReaped(int pid)
    {
        lock (_reapedGate)
        {
            return _reaped.ContainsKey(pid);
        }
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

    private static void Release(Entry entry, bool terminate)
    {
        lock (entry.Gate)
        {
            if (entry.Released)
            {
                return;
            }

            entry.Released = true;
            if (terminate && entry.Job is not null)
            {
                try
                {
                    entry.Job.Terminate();
                }
                catch (Exception)
                {
                    // The tree may already be dead; disposal below still owns the handle.
                }
            }

            try
            {
                entry.Job?.Dispose();
            }
            catch (Exception)
            {
                // Nothing else can release the entry now; a failed job close must not block cleanup.
            }
        }
    }

    private void RememberReaped(int pid)
    {
        lock (_reapedGate)
        {
            var sequence = ++_reapedSequence;
            _reaped[pid] = sequence;
            _reapedOrder.Enqueue((pid, sequence));

            // Bound the FIFO itself so repeated reaping of the same pid cannot grow the queue;
            // the dictionary can never outgrow it (every live key owns one queued sequence).
            // Stale queue entries (a pid that was reaped again later) are skipped.
            while (_reapedOrder.Count > MaxReaped)
            {
                var (oldPid, oldSequence) = _reapedOrder.Dequeue();
                if (_reaped.TryGetValue(oldPid, out var current) && current == oldSequence)
                {
                    _reaped.Remove(oldPid);
                }
            }
        }
    }

    private void ForgetReaped(int pid)
    {
        lock (_reapedGate)
        {
            _reaped.Remove(pid);
        }
    }

    private sealed class Entry
    {
        public Entry(string procId, string kind, int pid, string startTimeUtc, string? command, JobObject? job)
        {
            ProcId = procId;
            Kind = kind;
            Pid = pid;
            StartTimeUtc = startTimeUtc;
            Command = command;
            Job = job;
        }

        public string ProcId { get; }

        public string Kind { get; }

        public int Pid { get; }

        public string StartTimeUtc { get; }

        public string? Command { get; }

        public JobObject? Job { get; }

        public object Gate { get; } = new();

        public bool Released { get; set; }
    }
}
