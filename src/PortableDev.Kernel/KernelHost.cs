using System.Text.Json.Nodes;
using PortableDev.Core.Config;
using PortableDev.Core.Extensions;
using PortableDev.Core.Identity;
using PortableDev.Core.Logging;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;
using PortableDev.Kernel.Dispatch;
using PortableDev.Kernel.Lock;
using PortableDev.Kernel.Recovery;
using PortableDev.Kernel.Supervisor;
using PortableDev.Kernel.Transport;

namespace PortableDev.Kernel;

/// <summary>Wires the kernel together: settings, identity, lock, recovery, registry, pipe server.</summary>
public sealed class KernelHost : IKernelServices, IAsyncDisposable
{
    private readonly KernelOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private SingleInstanceLock? _lock;
    private bool _disposed;

    public KernelHost(KernelOptions options) => _options = options;

    public KernelState State { get; private set; } = null!;

    public KernelPaths Paths { get; private set; } = null!;

    public SettingsStore Settings { get; private set; } = null!;

    public ExtensionRegistry Registry { get; private set; } = null!;

    public KernelLogger Logger { get; private set; } = null!;

    public SessionRegistry Sessions { get; private set; } = null!;

    public ExtensionSupervisor Supervisor { get; private set; } = null!;

    public CommandRouter Router { get; private set; } = null!;

    public string PipePrefix => _options.PipePrefixOverride ?? Settings?.GetString("kernel.pipePrefix") ?? ProtocolConstants.DefaultPipePrefix;

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        Paths = new KernelPaths(_options.Root, _options.AppRoot);
        if (_options.Init)
        {
            Paths.EnsureLayout();
        }

        if (!Paths.LooksLikeStick())
        {
            Console.Error.WriteLine($"not a PortableDev stick: '{Paths.Root}' has no state/ or config/ (use --init to create the layout)");
            return ExitCodes.Env;
        }

        BootstrapLog.Configure(Paths.BootstrapLog);
        Settings = SettingsStore.Load(Paths.SettingsFile);

        var logLevel = LogLevels.TryParse(Settings.GetString("log.level"), out var parsedLevel) ? parsedLevel : LogLevel.Info;
        var identity = StickIdentity.LoadOrCreate(Paths.Root);
        var machineId = MachineId.Current();
        var bootId = "b-" + Sha256Util.HexLower(Guid.NewGuid().ToString("N"))[..12];
        Logger = new KernelLogger(new LogOptions(Paths.Logs, "kernel")
        {
            Level = logLevel,
            MaxSizeBytes = Math.Max(1, Settings.GetInt("log.maxSizeMB", 50)) * 1024L * 1024L,
            KeepFiles = Math.Max(1, Settings.GetInt("log.keepFiles", 10)),
        }, bootId, identity.StickId, machineId)
        {
            MinLevel = logLevel,
        };
        State = new KernelState
        {
            KernelVersion = ProtocolConstants.KernelVersion,
            Abi = ProtocolConstants.KernelAbi,
            BootedAt = DateTimeOffset.Now,
            StickId = identity.StickId,
            Root = Paths.Root,
            StableRoot = Settings.GetString("root.stableRoot") ?? ProtocolConstants.DefaultStableRoot,
            MachineId = machineId,
            BootId = bootId,
        };

        Logger.Info("kernel.boot", "kernel starting", new JsonObject
        {
            ["version"] = State.KernelVersion,
            ["abi"] = State.Abi,
            ["protocol"] = ProtocolConstants.Version,
            ["pid"] = Environment.ProcessId,
            ["root"] = State.Root,
            ["stickId"] = State.StickId,
            ["stickSource"] = identity.Source,
            ["machineId"] = State.MachineId,
        });

        if (Settings.WasReset)
        {
            Logger.Warn("settings.reset", "settings.json was unreadable; defaults applied", new JsonObject
            {
                ["backup"] = Settings.BackupPath,
            });
        }

        if (!string.Equals(identity.Source, "volume", StringComparison.Ordinal))
        {
            Logger.Warn("identity.source", "stick id was not derived from the volume serial", new JsonObject
            {
                ["source"] = identity.Source,
            });
        }

        var (instance, lockError, exitCode) = SingleInstanceLock.Acquire(
            Paths.Root, Paths.LockFile, State.StickId, State.MachineId, State.KernelVersion, ProtocolConstants.Version, Logger);
        if (instance is null)
        {
            Logger.Error("kernel.lock", "cannot acquire the single-instance lock", null, new JsonObject { ["error"] = lockError });
            Logger.Dispose();
            return exitCode;
        }

        _lock = instance;

        Sessions = new SessionRegistry();
        Supervisor = new ExtensionSupervisor(this);
        Router = new CommandRouter(this);
        // Builtins are registered first: a user extension that wants a builtin name is rejected,
        // never the other way around (architecture 3.8: no silent takeover of builtin commands).
        var commandTable = new CommandTable();
        RegisterBuiltinCommands(commandTable);
        Registry = ExtensionRegistry.Scan(Paths.Root, Paths.AppRoot, State.Abi, commandTable);
        Logger.SendToProvider = (name, envelope) => Supervisor.TrySend(name, envelope);

        var providers = Registry.Extensions
            .Where(e => e.State == ExtensionState.Ready && e.Provides.Any(p => p.Name == "log.ingest" && p.Version == 1))
            .OrderBy(e => e.Priority)
            .ToArray();
        if (providers.Length > 1 && providers[0].Priority == providers[1].Priority)
        {
            Logger.Error("log.provider", "conflicting log providers; refusing to pick one", null, new JsonObject
            {
                ["candidates"] = new JsonArray(providers.Select(p => (JsonNode?)p.Name).ToArray()),
            });
        }
        else if (providers.Length > 0)
        {
            Logger.Info("log.provider", "log provider selected", new JsonObject { ["extension"] = providers[0].Name });
        }

        State.ExtensionCount = Registry.Extensions.Count;
        Logger.Info("registry.scan", "extensions scanned", new JsonObject
        {
            ["extensions"] = Registry.Extensions.Count,
            ["commands"] = Registry.Commands.All.Count,
            ["warnings"] = new JsonArray(Registry.Warnings.Select(w => (JsonNode?)w).ToArray()),
        });

        foreach (var required in new[] { "log", "storage" })
        {
            if (Registry.Find(required) is not { State: ExtensionState.Ready })
            {
                Logger.Warn("kernel.required-extension", "required built-in extension is missing or rejected", new JsonObject
                {
                    ["extension"] = required,
                });
            }
        }

        var recovery = new RecoveryService(Paths, Settings, Logger).Run();
        if (recovery.Adopted + recovery.Dropped + recovery.StagingDeleted + recovery.RecordsDeleted > 0)
        {
            Logger.Warn("kernel.recovery", "recovery changed state", new JsonObject
            {
                ["adopted"] = recovery.Adopted,
                ["dropped"] = recovery.Dropped,
                ["stagingDeleted"] = recovery.StagingDeleted,
                ["recordsDeleted"] = recovery.RecordsDeleted,
            });
        }

        if (_options.RunOnce)
        {
            Logger.Info("kernel.once", "recovery-only run finished", null);
            return ExitCodes.Ok;
        }

        var pipe = new PipeServer(
            PipePrefix,
            State.StickId,
            Logger,
            (stream, connectionId) => new ClientConnection(stream, connectionId, this));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var idleTask = StartIdleTimerAsync(linked.Token);
        var pipeTask = pipe.RunAsync(linked.Token);
        try
        {
            await Supervisor.StartEagerAsync(linked.Token).ConfigureAwait(false);
            _ = Task.Run(() => ReplayFallbackAsync(linked.Token), CancellationToken.None);
            await pipeTask.ConfigureAwait(false);
        }
        finally
        {
            // Close client connections and stop accepting first, then flush what is still queued.
            linked.Cancel();
            Logger.Flush();
            State.BeginShutdown(State.ForceShutdown);
            Supervisor.BeginShutdown();
            await Supervisor.StopAllAsync(State.ForceShutdown).ConfigureAwait(false);
            await idleTask.ConfigureAwait(false);
            Logger.Info("kernel.stop", "kernel stopped", null);
        }

        return ExitCodes.Ok;
    }

    public Task RequestShutdownAsync(bool force)
    {
        if (State is not null && !State.Stopping)
        {
            State.BeginShutdown(force);
            Supervisor?.BeginShutdown();
            Logger?.Info("kernel.shutdown", "shutdown requested", new JsonObject { ["force"] = force });
        }

        // Give the in-flight task (typically `kernel stop` itself) a moment to publish its done event
        // before the server token tears the client connections down.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150)).ConfigureAwait(false);
            try
            {
                _shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already gone.
            }
        });

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Supervisor is not null)
        {
            await Supervisor.StopAllAsync().ConfigureAwait(false);
        }

        _lock?.Dispose();
        _shutdown.Dispose();
        Logger?.Dispose();
    }

    private void RegisterBuiltinCommands(CommandTable commandTable)
    {
        foreach (var builtin in Router.Builtins)
        {
            var entry = new CommandEntry
            {
                Name = builtin.Name,
                Aliases = builtin.Aliases,
                IsBuiltin = true,
                Mutates = builtin.Mutates,
                Parallel = builtin.Parallel,
                Summary = builtin.Summary,
                Usage = builtin.Usage,
                Group = "kernel",
                Hidden = false,
            };

            var conflict = commandTable.TryRegister(entry);
            if (conflict is not null)
            {
                Logger.Warn("command.conflict", "builtin command conflicts with another builtin", new JsonObject
                {
                    ["command"] = builtin.Name,
                    ["conflict"] = conflict,
                });
            }
        }
    }

    /// <summary>Sends records written before the provider attached, then stops touching the fallback file.</summary>
    private async Task ReplayFallbackAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < 100 && Logger.Provider.ProviderName is null && !cancellationToken.IsCancellationRequested; attempt++)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (Logger.Provider.ProviderName is null || !File.Exists(Logger.FallbackPath))
            {
                return;
            }

            var records = new List<LogRecord>();
            // The kernel logger keeps this file open for append; read with a share mode that
            // tolerates the writer, otherwise every replay fails with "cannot access the file".
            using var fallback = new FileStream(Logger.FallbackPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fallback, System.Text.Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (JsonNode.Parse(line) is not JsonObject document || !LogRecord.TryParse(document, out var record))
                {
                    continue;
                }

                if (string.Equals(record.BootId, State.BootId, StringComparison.Ordinal))
                {
                    records.Add(record);
                }
            }

            if (records.Count > 0)
            {
                Logger.Provider.Write(records);
                Logger.Info("log.replay", "replayed fallback records into the provider", new JsonObject { ["count"] = records.Count });
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("log.replay", "fallback replay failed", new JsonObject { ["error"] = ex.Message });
        }
    }

    private async Task StartIdleTimerAsync(CancellationToken cancellationToken)
    {
        var idle = TimeUtil.ParseDuration(Settings.GetString("kernel.idleExit"));
        if (idle is null || idle.Value <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(idle.Value, cancellationToken).ConfigureAwait(false);
                if (State.ActiveTasks == 0 && Sessions.ClientConnectionCount == 0)
                {
                    Logger.Info("kernel.idle", "idle exit", new JsonObject { ["idle"] = TimeUtil.FormatDuration(idle.Value) });
                    await RequestShutdownAsync(force: false).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
