using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PortableDev.Core.Logging;

/// <summary>
/// The kernel side of the log-provider contract: find the provider by capability, batch records over
/// `ctl log.write`, and fall back to the file sink when no provider is attached.
/// </summary>
public sealed class LogProviderClient : IDisposable
{
    private const int QueueCapacity = 8192;
    private const int BatchLimit = 64;

    private readonly KernelLogger _owner;
    private readonly Channel<LogRecord> _queue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly object _stateGate = new();
    private string? _providerName;
    private long _dropped;
    private int _providerPriority = int.MaxValue;
    private bool _conflicted;
    private bool _disposed;

    public LogProviderClient(KernelLogger owner)
    {
        _owner = owner;
        // SingleReader is false because Flush() and Dispose() also drain the channel; itemDropped
        // tracks evictions under DropOldest (where TryWrite always returns true).
        _queue = Channel.CreateBounded<LogRecord>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            },
            _ => Interlocked.Increment(ref _dropped));
        _pump = Task.Run(PumpAsync);
    }

    public string? ProviderName
    {
        get { lock (_stateGate) { return _providerName; } }
    }

    /// <summary>Two providers claimed the same priority; ingest is refused until the kernel restarts.</summary>
    public bool Conflicted
    {
        get { lock (_stateGate) { return _conflicted; } }
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Called when a provider announces `log.ingest` and becomes ready. Lowest priority wins; two
    /// providers with the same priority are both refused (spec 5.3.1) and records go to the fallback.
    /// </summary>
    public void Attach(string providerName, int priority = 0)
    {
        string? conflictFirst = null;
        lock (_stateGate)
        {
            if (_conflicted)
            {
                return;
            }

            if (_providerName is null)
            {
                _providerName = providerName;
                _providerPriority = priority;
                return;
            }

            if (string.Equals(_providerName, providerName, StringComparison.Ordinal))
            {
                return;
            }

            if (priority < _providerPriority)
            {
                _providerName = providerName;
                _providerPriority = priority;
                return;
            }

            if (priority == _providerPriority)
            {
                conflictFirst = _providerName;
                _conflicted = true;
                _providerName = null;
            }
        }

        if (conflictFirst is not null)
        {
            _owner.Error("log.provider", "two log providers share the same priority; refusing both", null, new JsonObject
            {
                ["first"] = conflictFirst,
                ["second"] = providerName,
                ["priority"] = priority,
            });
        }
    }

    public void Detach(string providerName)
    {
        lock (_stateGate)
        {
            if (_conflicted)
            {
                return;
            }

            if (string.Equals(_providerName, providerName, StringComparison.Ordinal))
            {
                _providerName = null;
            }
        }
    }

    /// <summary>True when the record was accepted by the provider path; false means "write the fallback".</summary>
    public bool TryWrite(LogRecord record)
    {
        if (ProviderName is null)
        {
            return false;
        }

        if (!_queue.Writer.TryWrite(record))
        {
            Interlocked.Increment(ref _dropped);
        }

        return true;
    }

    public void Write(IReadOnlyList<LogRecord> records)
    {
        foreach (var record in records)
        {
            TryWrite(record);
        }
    }

    public void Flush()
    {
        while (_queue.Reader.TryRead(out var record))
        {
            SendBatch([record]);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // Shutting down.
        }

        // Records already accepted must not vanish with the queue: fall back to the file sink.
        while (_queue.Reader.TryRead(out var record))
        {
            _owner.WriteToFallback(record);
        }

        _cts.Dispose();
    }

    private async Task PumpAsync()
    {
        var buffer = new List<LogRecord>(BatchLimit);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                buffer.Clear();
                if (await _queue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (buffer.Count < BatchLimit && _queue.Reader.TryRead(out var record))
                    {
                        buffer.Add(record);
                    }
                }

                if (buffer.Count == 0)
                {
                    continue;
                }

                SendBatch(buffer);
                if (Interlocked.Read(ref _dropped) > 0 && buffer[0].Evt != "log.dropped")
                {
                    var dropped = Interlocked.Exchange(ref _dropped, 0);
                    SendBatch([new LogRecord
                    {
                        Ts = PortableDev.Core.Util.TimeUtil.NowIso(),
                        Pri = 4,
                        Level = "warn",
                        Msg = $"{dropped} log records dropped (queue full)",
                        Evt = "log.dropped",
                        BootId = buffer[0].BootId,
                        Unit = "kernel",
                        StickId = buffer[0].StickId,
                        MachineId = buffer[0].MachineId,
                        Transport = "kernel",
                    }]);
                }

                await Task.Delay(50, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void SendBatch(IReadOnlyList<LogRecord> records)
    {
        var provider = ProviderName;
        if (provider is null)
        {
            // Provider vanished mid-batch: keep the records in the fallback instead of losing them.
            foreach (var record in records)
            {
                _owner.WriteToFallback(record);
            }

            return;
        }

        var payload = new JsonArray();
        foreach (var record in records)
        {
            payload.Add(record.ToJson());
        }

        var envelope = new PortableDev.Core.Protocol.Envelope
        {
            Kind = PortableDev.Core.Protocol.Kinds.Ctl,
            Data = new JsonObject
            {
                ["action"] = PortableDev.Core.Protocol.CtlActions.LogWrite,
                ["schema"] = 1,
                ["records"] = payload,
            },
        };

        if (!_owner.TrySendToProvider(provider, envelope))
        {
            foreach (var record in records)
            {
                _owner.WriteToFallback(record);
            }
        }
    }
}
