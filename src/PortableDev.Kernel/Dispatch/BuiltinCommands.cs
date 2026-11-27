using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using PortableDev.Core.Config;
using PortableDev.Kernel.Recovery;
using PortableDev.Core.Logging;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;
using PortableDev.Kernel.Dispatch;
using PortableDev.Kernel.Transport;

namespace PortableDev.Kernel.Dispatch;

public sealed record BuiltinCommand(
    string Name,
    string[] Aliases,
    bool Mutates,
    bool Parallel,
    string Summary,
    string? Usage,
    Func<TaskContext, string[], Task<CommandResult>> Handler);

/// <summary>
/// Commands the kernel itself answers (architecture 3.3 / commands.md 4.5 + 4.7).
/// Extension commands never live here; the router resolves those through the registry.
/// </summary>
public sealed class BuiltinCommandSet
{
    private readonly List<BuiltinCommand> _commands = [];
    private readonly IKernelServices _kernel;

    public BuiltinCommandSet(IKernelServices kernel)
    {
        _kernel = kernel;
        RegisterCore();
    }

    public IReadOnlyList<BuiltinCommand> All => _commands;

    public (BuiltinCommand? Command, int Consumed) Resolve(string[] argv)
    {
        if (argv.Length == 0)
        {
            return (Find("kernel status"), 0);
        }

        if (argv.Length >= 3)
        {
            var three = Find(argv[0] + " " + argv[1] + " " + argv[2]);
            if (three is not null)
            {
                return (three, 3);
            }
        }

        if (argv.Length >= 2)
        {
            var two = Find(argv[0] + " " + argv[1]);
            if (two is not null)
            {
                return (two, 2);
            }
        }

        var one = Find(argv[0]);
        return (one, one is null ? 0 : 1);
    }

    public BuiltinCommand? Find(string name)
        => _commands.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal)) ??
           _commands.FirstOrDefault(c => c.Aliases.Contains(name, StringComparer.Ordinal));

    private void RegisterCore()
    {
        Add("version", ["ver"], false, true, "print kernel version", "pd version [--short]", VersionAsync);
        Add("help", [], false, true, "list commands or show one command", "pd help [命令]", HelpAsync);
        Add("complete", [], false, true, "completion candidates", "pd complete <前缀>", CompleteAsync);
        Add("kernel status", ["status"], false, true, "kernel liveness and identity", "pd kernel status [--json]", KernelStatusAsync);
        Add("kernel ps", [], false, true, "running extension processes", "pd kernel ps [--json]", KernelPsAsync);
        Add("kernel ext list", [], false, true, "installed extensions and their state", "pd kernel ext list [--json]", KernelExtListAsync);
        Add("kernel restart", [], true, false, "restart one extension", "pd kernel restart <名字>", KernelRestartAsync);
        Add("kernel stop", [], true, false, "stop the kernel", "pd kernel stop [--force]", KernelStopAsync);
        Add("kernel log", [], false, true, "kernel fallback log", "pd kernel log [--tail <n>]", KernelLogAsync);
        Add("ui pages", [], false, true, "pages declared by extensions", "pd ui pages [--json]", UiPagesAsync);
        Add("config list", [], false, true, "list settings", "pd config list [--json]", ConfigListAsync);
        Add("config get", [], false, true, "read one setting", "pd config get <键> [--file]", ConfigGetAsync);
        Add("config set", [], true, false, "change one setting", "pd config set <键> <值>", ConfigSetAsync);
        Add("config unset", [], true, false, "reset one setting", "pd config unset <键>", ConfigUnsetAsync);
        Add("ext", ["exts"], false, true, "installed extensions (alias of ext list)", "pd ext [--json]", KernelExtListAsync);
        Add("ext list", ["ext ls"], false, true, "installed extensions and their state", "pd ext list [--json]", KernelExtListAsync);
        Add("ext enable", [], true, false, "enable an extension (needs kernel restart)", "pd ext enable <名字>", ExtEnableAsync);
        Add("ext disable", [], true, false, "disable an extension (needs kernel restart)", "pd ext disable <名字>", ExtDisableAsync);
        Add("ext update", [], true, false, "replace an extension from a directory or zip (needs kernel restart)", "pd ext update <名字> <绝对路径>", ExtUpdateAsync);
        Add("recover", [], true, false, "run the startup recovery now and report what it settled", "pd recover [--json]", RecoverAsync);
    }

    private void Add(string name, string[] aliases, bool mutates, bool parallel, string summary, string? usage,
        Func<TaskContext, string[], Task<CommandResult>> handler)
        => _commands.Add(new BuiltinCommand(name, aliases, mutates, parallel, summary, usage, handler));

    private static CommandResult Ok(string? summary = null, JsonObject? metrics = null) => new(ExitCodes.Ok, null, summary, metrics);

    private static CommandResult Fail(string code, int exitCode, string message, string? hint = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (hint is not null)
        {
            error["hint"] = hint;
        }

        return new CommandResult(exitCode, error, null, null);
    }

    private static (string[] Positional, Dictionary<string, string?> Flags) Parse(string[] args)
    {
        var positional = new List<string>();
        var flags = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var name = arg[2..];
                string? value = null;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }

                flags[name] = value;
            }
            else
            {
                positional.Add(arg);
            }
        }

        return (positional.ToArray(), flags);
    }

    private async Task<CommandResult> VersionAsync(TaskContext ctx, string[] args)
    {
        var (_, flags) = Parse(args);
        var shortForm = flags.ContainsKey("short");
        var summary = shortForm ? _kernel.State.KernelVersion : $"pd {_kernel.State.KernelVersion} (abi {_kernel.State.Abi}, protocol {ProtocolConstants.Version})";
        if (!shortForm)
        {
            await ctx.KvAsync("version", new JsonObject
            {
                ["kernel"] = _kernel.State.KernelVersion,
                ["abi"] = _kernel.State.Abi,
                ["protocol"] = ProtocolConstants.Version,
                ["root"] = _kernel.State.Root,
            }).ConfigureAwait(false);
        }

        return Ok(summary);
    }

    private async Task<CommandResult> HelpAsync(TaskContext ctx, string[] args)
    {
        var (positional, _) = Parse(args);
        if (positional.Length > 0)
        {
            if (_kernel.Registry.Commands.TryResolve(positional[0], out var entry))
            {
                await ctx.KvAsync(positional[0], new JsonObject
                {
                    ["usage"] = entry.Usage,
                    ["summary"] = entry.Summary,
                    ["group"] = entry.Group,
                    ["owner"] = entry.ExtensionName ?? "kernel",
                    ["mutates"] = entry.Mutates,
                    ["parallel"] = entry.Parallel,
                }).ConfigureAwait(false);
                return Ok();
            }

            var suggest = _kernel.Registry.Commands.Suggest(positional[0]);
            return Fail(ErrorCodes.UnknownCommand, ExitCodes.Args, $"unknown command '{positional[0]}'", suggest is null ? null : $"did you mean '{suggest}'?");
        }

        var rows = _kernel.Registry.Commands.All
            .Where(c => !c.Hidden)
            .OrderBy(c => c.Group ?? "core", StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new JsonArray(c.Name, c.Group ?? "core", c.Summary ?? string.Empty))
            .ToArray();

        await ctx.EmitAsync(EventTypes.Table, new JsonObject
        {
            ["title"] = "commands",
            ["columns"] = new JsonArray(
                new JsonObject { ["key"] = "name", ["title"] = "命令" },
                new JsonObject { ["key"] = "group", ["title"] = "分组" },
                new JsonObject { ["key"] = "summary", ["title"] = "说明" }),
            ["rows"] = new JsonArray(rows),
        }).ConfigureAwait(false);

        return Ok($"{rows.Length} commands");
    }

    private async Task<CommandResult> CompleteAsync(TaskContext ctx, string[] args)
    {
        var (positional, _) = Parse(args);
        var prefix = positional.FirstOrDefault() ?? string.Empty;
        var candidates = _kernel.Registry.Commands.All
            .Where(c => !c.Hidden && (c.Name.StartsWith(prefix, StringComparison.Ordinal) || c.Aliases.Any(a => a.StartsWith(prefix, StringComparison.Ordinal))))
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Take(200)
            .ToArray();

        await ctx.EmitAsync(EventTypes.Table, new JsonObject
        {
            ["title"] = "completions",
            ["columns"] = new JsonArray(new JsonObject { ["key"] = "name", ["title"] = "命令" }),
            ["rows"] = new JsonArray(candidates.Select(c => new JsonArray(c)).ToArray()),
        }).ConfigureAwait(false);

        return Ok();
    }

    private async Task<CommandResult> KernelStatusAsync(TaskContext ctx, string[] args)
    {
        _ = args;
        await ctx.KvAsync("kernel", new JsonObject
        {
            ["state"] = _kernel.State.Stopping ? "stopping" : "running",
            ["pid"] = Environment.ProcessId,
            ["kernelVersion"] = _kernel.State.KernelVersion,
            ["abi"] = _kernel.State.Abi,
            ["protocol"] = ProtocolConstants.Version,
            ["stickId"] = _kernel.State.StickId,
            ["root"] = _kernel.State.Root,
            ["stableRoot"] = _kernel.State.StableRoot,
            ["integrity"] = PortableDev.Core.Util.TokenInfo.Describe(),
            ["bootedAt"] = TimeUtil.Format(_kernel.State.BootedAt),
            ["sessions"] = _kernel.Sessions.ConnectionCount,
            ["activeTasks"] = _kernel.State.ActiveTasks,
            ["extensions"] = _kernel.Registry.Extensions.Count,
        }).ConfigureAwait(false);

        return Ok("kernel running");
    }

    /// <summary>Page entries are served through a virtual host; only safe relative paths are allowed.</summary>
    private static bool IsSafePageEntry(string entry)
        => entry.Length > 0
           && !entry.StartsWith("/", StringComparison.Ordinal)
           && !entry.StartsWith("\\", StringComparison.Ordinal)
           && !entry.Contains(':')
           && !entry.Contains("..", StringComparison.Ordinal)
           && !entry.Contains('\0');

    private async Task<CommandResult> UiPagesAsync(TaskContext ctx, string[] args)
    {
        _ = args;
        var pages = new JsonArray();
        var rows = new List<JsonArray>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var manifest in _kernel.Registry.Extensions.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            if (!manifest.Enabled || manifest.Pages is not { } declared)
            {
                continue;
            }

            foreach (var node in declared)
            {
                if (node is not JsonObject page)
                {
                    continue;
                }

                var id = JsonUtil.String(page, "id");
                var entry = JsonUtil.String(page, "entry");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                if (!IsSafePageEntry(entry) || !seen.Add(manifest.Name + ":" + id))
                {
                    _kernel.Logger.Warn("ui.page", "page declaration rejected", new JsonObject
                    {
                        ["extension"] = manifest.Name,
                        ["id"] = id,
                        ["entry"] = entry,
                    });
                    continue;
                }

                var nav = JsonUtil.String(page, "nav");
                if (nav is not ("sidebar" or "menu" or "hidden"))
                {
                    nav = "sidebar";
                }

                var title = JsonUtil.String(page, "title") ?? id;
                pages.Add(new JsonObject
                {
                    ["extension"] = manifest.Name,
                    ["id"] = id,
                    ["title"] = title,
                    ["entry"] = entry,
                    ["icon"] = JsonUtil.String(page, "icon"),
                    ["order"] = JsonUtil.Int(page, "order") ?? 100,
                    ["nav"] = nav,
                });
                rows.Add(new JsonArray(manifest.Name, id, title, entry));
            }
        }

        await ctx.KvAsync("pages", new JsonObject { ["items"] = pages }).ConfigureAwait(false);
        await ctx.EmitAsync(EventTypes.Table, new JsonObject
        {
            ["title"] = "pages",
            ["columns"] = new JsonArray(
                new JsonObject { ["key"] = "extension", ["title"] = "扩展" },
                new JsonObject { ["key"] = "id", ["title"] = "页面" },
                new JsonObject { ["key"] = "title", ["title"] = "标题" },
                new JsonObject { ["key"] = "entry", ["title"] = "入口" }),
            ["rows"] = new JsonArray(rows.Select(row => (JsonNode?)row).ToArray()),
            ["final"] = true,
        }).ConfigureAwait(false);
        return Ok(rows.Count + " page(s)");
    }

    private async Task<CommandResult> KernelPsAsync(TaskContext ctx, string[] args)
    {
        _ = args;
        var rows = _kernel.Supervisor.Snapshot()
            .Select(p => new JsonArray(p.Name, p.State, p.Pid?.ToString() ?? "-", p.Restarts))
            .ToArray();

        await ctx.EmitAsync(EventTypes.Table, new JsonObject
        {
            ["title"] = "extensions",
            ["columns"] = new JsonArray(
                new JsonObject { ["key"] = "name", ["title"] = "扩展" },
                new JsonObject { ["key"] = "state", ["title"] = "状态" },
                new JsonObject { ["key"] = "pid", ["title"] = "PID" },
                new JsonObject { ["key"] = "restarts", ["title"] = "重启" }),
            ["rows"] = new JsonArray(rows),
        }).ConfigureAwait(false);
        return Ok($"{rows.Length} extensions");
    }

    private async Task<CommandResult> ExtEnableAsync(TaskContext ctx, string[] args)
        => await ExtSetEnabledAsync(ctx, args, true).ConfigureAwait(false);

    private async Task<CommandResult> ExtDisableAsync(TaskContext ctx, string[] args)
        => await ExtSetEnabledAsync(ctx, args, false).ConfigureAwait(false);

    private string? FindExtensionDirectory(string name)
    {
        foreach (var root in new[] { Path.Combine(_kernel.Paths.AppRoot, "extensions"), Path.Combine(_kernel.Paths.Root, "extensions") })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var candidate in Directory.GetDirectories(root))
            {
                var manifestPath = Path.Combine(candidate, "ext.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                try
                {
                    if (JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject manifest
                        && string.Equals(JsonUtil.String(manifest, "name"), name, StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    // A broken manifest is reported by the registry, not here.
                }
            }
        }

        return null;
    }

    private async Task<CommandResult> ExtSetEnabledAsync(TaskContext ctx, string[] args, bool enabled)
    {
        var (positional, _) = Parse(args);
        var name = positional.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name))
        {
            return Fail(ErrorCodes.BadArgs, ExitCodes.Args, "ext enable/disable needs an extension name");
        }

        var directory = FindExtensionDirectory(name);
        if (directory is null)
        {
            return Fail(ErrorCodes.ExtMissing, ExitCodes.NotFound, $"extension '{name}' is not installed");
        }

        try
        {
            var manifestPath = Path.Combine(directory, "ext.json");
            if (JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject manifest)
            {
                return Fail(ErrorCodes.ExtMissing, ExitCodes.Rejected, "ext.json is not an object");
            }

            var changed = (JsonUtil.Bool(manifest, "enabled") ?? true) != enabled;
            manifest["enabled"] = enabled;
            AtomicFile.WriteAllText(manifestPath, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            var metrics = new JsonObject { ["name"] = name, ["enabled"] = enabled, ["changed"] = changed, ["restartRequired"] = true };
            await ctx.EmitAsync(EventTypes.Kv, new JsonObject { ["title"] = "extension", ["items"] = metrics.DeepClone() }).ConfigureAwait(false);
            return Ok((enabled ? "enabled " : "disabled ") + name + " (restart the kernel to apply)", metrics);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Fail(ErrorCodes.Io, ExitCodes.Internal, ex.Message);
        }
    }

    private async Task<CommandResult> ExtUpdateAsync(TaskContext ctx, string[] args)
    {
        var (positional, _) = Parse(args);
        var name = positional.ElementAtOrDefault(0);
        var source = positional.ElementAtOrDefault(1);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source))
        {
            return Fail(ErrorCodes.BadArgs, ExitCodes.Args, "ext update needs <name> <directory|zip>");
        }

        if (!Path.IsPathRooted(source))
        {
            return Fail(ErrorCodes.BadArgs, ExitCodes.Args, "ext update needs an absolute source path");
        }

        if (!Directory.Exists(source) && !File.Exists(source))
        {
            return Fail(ErrorCodes.BadArgs, ExitCodes.Args, "source does not exist: " + source);
        }

        var target = FindExtensionDirectory(name) ?? Path.Combine(_kernel.Paths.Root, "extensions", name);
        var staging = Path.Combine(_kernel.Paths.Staging, "ext-update-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(staging);
            if (Directory.Exists(source))
            {
                CopyDirectory(source, staging);
            }
            else
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(source, staging, overwriteFiles: true);
            }

            var manifestPath = Path.Combine(staging, "ext.json");
            if (!File.Exists(manifestPath))
            {
                return Fail(ErrorCodes.PackageInvalid, ExitCodes.Data, "source has no ext.json");
            }

            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
            if (manifest is null || !string.Equals(JsonUtil.String(manifest, "name"), name, StringComparison.Ordinal))
            {
                return Fail(ErrorCodes.PackageInvalid, ExitCodes.Data, "ext.json name does not match '" + name + "'");
            }

            // A running extension keeps its working directory open; stop it before replacing files.
            if (_kernel.Registry.Find(name) is not null)
            {
                await _kernel.Supervisor.StopAsync(name).ConfigureAwait(false);
            }

            for (var attempt = 0; attempt < 50 && Directory.Exists(target); attempt++)
            {
                try
                {
                    Directory.Delete(target, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
            var metrics = new JsonObject { ["name"] = name, ["target"] = target, ["restartRequired"] = true };
            await ctx.EmitAsync(EventTypes.Kv, new JsonObject { ["title"] = "extension", ["items"] = metrics.DeepClone() }).ConfigureAwait(false);
            return Ok("updated " + name + " (restart the kernel to apply)", metrics);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            return Fail(ErrorCodes.Io, ExitCodes.Internal, ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var child in Directory.GetDirectories(source))
        {
            CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)));
        }
    }

    private async Task<CommandResult> RecoverAsync(TaskContext ctx, string[] args)
    {
        var (_, flags) = Parse(args);
        foreach (var unsupported in new[] { "adopt", "drop", "dry-run" })
        {
            if (flags.ContainsKey(unsupported))
            {
                return Fail(ErrorCodes.Unsupported, ExitCodes.Rejected,
                    "pd recover runs the startup policy (recovery.intentDefault); use pd config set to change it");
            }
        }

        var report = new RecoveryService(_kernel.Paths, _kernel.Settings, _kernel.Logger).Run();
        var metrics = new JsonObject
        {
            ["adopted"] = report.Adopted,
            ["dropped"] = report.Dropped,
            ["intentErrors"] = report.IntentErrors,
            ["stagingDeleted"] = report.StagingDeleted,
            ["recordsDeleted"] = report.RecordsDeleted,
        };
        await ctx.EmitAsync(EventTypes.Kv, new JsonObject { ["title"] = "recovery", ["items"] = metrics.DeepClone() }).ConfigureAwait(false);
        return Ok($"recovery: {report.Adopted} adopted, {report.Dropped} dropped, {report.StagingDeleted} staging deleted", metrics);
    }    private async Task<CommandResult> KernelExtListAsync(TaskContext ctx, string[] args)
    {
        _ = args;
        var rows = _kernel.Registry.Extensions
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => new JsonArray(
                e.Name,
                e.Version,
                e.Origin.ToString().ToLowerInvariant(),
                e.State.ToString().ToLowerInvariant(),
                e.Commands.Count,
                e.RejectReason ?? string.Empty))
            .ToArray();

        await ctx.EmitAsync(EventTypes.Table, new JsonObject
        {
            ["title"] = "installed extensions",
            ["columns"] = new JsonArray(
                new JsonObject { ["key"] = "name", ["title"] = "扩展" },
                new JsonObject { ["key"] = "version", ["title"] = "版本" },
                new JsonObject { ["key"] = "origin", ["title"] = "来源" },
                new JsonObject { ["key"] = "state", ["title"] = "状态" },
                new JsonObject { ["key"] = "commands", ["title"] = "命令数" },
                new JsonObject { ["key"] = "reason", ["title"] = "说明" }),
            ["rows"] = new JsonArray(rows),
        }).ConfigureAwait(false);

        return Ok($"{rows.Length} extensions");
    }

    private async Task<CommandResult> KernelRestartAsync(TaskContext ctx, string[] args)
    {
        var (positional, _) = Parse(args);
        var name = positional.FirstOrDefault();
        if (name is null)
        {
            return Fail(ErrorCodes.BadArgs, ExitCodes.Args, "kernel restart needs an extension name", "pd kernel restart <名字>");
        }

        if (_kernel.Registry.Find(name) is null)
        {
            return Fail(ErrorCodes.ExtMissing, ExitCodes.NotFound, $"extension '{name}' is not installed");
        }

        var ok = await _kernel.Supervisor.RestartAsync(name, ctx.Token).ConfigureAwait(false);
        return ok ? Ok($"restarted {name}") : Fail(ErrorCodes.ExtTimeout, ExitCodes.Timeout, $"extension '{name}' did not become ready");
    }

    private async Task<CommandResult> KernelStopAsync(TaskContext ctx, string[] args)
    {
        var (_, flags) = Parse(args);
        await _kernel.RequestShutdownAsync(flags.ContainsKey("force")).ConfigureAwait(false);
        return Ok("kernel stopping");
    }

    private async Task<CommandResult> KernelLogAsync(TaskContext ctx, string[] args) => await LogTailAsync(ctx, args).ConfigureAwait(false);

    private async Task<CommandResult> LogTailAsync(TaskContext ctx, string[] args)
    {
        var (_, flags) = Parse(args);
        var tail = 50;
        if (flags.TryGetValue("tail", out var tailText) && int.TryParse(tailText, out var parsed))
        {
            tail = Math.Clamp(parsed, 1, 5000);
        }

        flags.TryGetValue("level", out var level);
        var lines = ReadTail(_kernel.Paths.KernelLog, tail, level);
        foreach (var line in lines)
        {
            await ctx.EmitAsync(EventTypes.Log, new JsonObject { ["level"] = "info", ["msg"] = line }).ConfigureAwait(false);
        }

        return Ok($"{lines.Count} lines");
    }

    private async Task<CommandResult> ConfigListAsync(TaskContext ctx, string[] args)
    {
        _ = args;
        var rows = SettingCatalog.Definitions
            .Select(d => new JsonArray(d.Path, _kernel.Settings.GetJsonString(d.Path)))
            .ToArray();

        await ctx.EmitAsync(EventTypes.Table, new JsonObject
        {
            ["title"] = "settings",
            ["columns"] = new JsonArray(
                new JsonObject { ["key"] = "key", ["title"] = "键" },
                new JsonObject { ["key"] = "value", ["title"] = "值" }),
            ["rows"] = new JsonArray(rows),
        }).ConfigureAwait(false);
        return Ok($"{rows.Length} settings");
    }

    private async Task<CommandResult> ConfigGetAsync(TaskContext ctx, string[] args)
    {
        var (positional, flags) = Parse(args);
        var key = positional.FirstOrDefault();
        if (key is null)
        {
            return Fail(ErrorCodes.BadArgs, ExitCodes.Args, "config get needs a key", "pd config get <键> [--file]");
        }

        if (SettingCatalog.Find(key) is null)
        {
            return Fail(ErrorCodes.BadArgs, ExitCodes.Args, $"unknown setting '{key}'");
        }

        var fromFile = flags.ContainsKey("file");
        var value = "null";
        if (fromFile)
        {
            try
            {
                var document = File.Exists(_kernel.Paths.SettingsFile)
                    ? JsonNode.Parse(File.ReadAllText(_kernel.Paths.SettingsFile)) as JsonObject
                    : null;
                value = document is null ? "null" : (SettingCatalog.GetPath(document, key)?.ToJsonString() ?? "null");
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new CommandResult(ExitCodes.Internal, new JsonObject
                {
                    ["code"] = ErrorCodes.Internal,
                    ["message"] = "settings.json is not valid JSON: " + ex.Message,
                }, null, null);
            }
        }
        else
        {
            value = _kernel.Settings.GetJsonString(key);
        }

        var changedOnDisk = !fromFile && _kernel.Settings.FileChangedSinceLoad;
        var items = new JsonObject
        {
            ["value"] = value,
            ["source"] = fromFile ? "file" : "memory",
        };
        if (changedOnDisk)
        {
            items["fileChanged"] = true;
            await ctx.LogAsync(LogLevel.Warn, "settings.json changed on disk; restart the kernel to apply it").ConfigureAwait(false);
        }

        await ctx.KvAsync(key, items).ConfigureAwait(false);
        return Ok($"{key} = {value}" + (changedOnDisk ? " (file changed; restart required)" : string.Empty));
    }

    private async Task<CommandResult> ConfigSetAsync(TaskContext ctx, string[] args)
    {
        var (positional, _) = Parse(args);
        if (positional.Length < 2)
        {
            return Fail(ErrorCodes.BadArgs, ExitCodes.Args, "config set needs a key and a value", "pd config set <键> <值>");
        }

        var (ok, error, unknownKey) = _kernel.Settings.Set(positional[0], positional[1]);
        if (!ok)
        {
            return Fail(ErrorCodes.BadArgs, ExitCodes.Args, error ?? "invalid value");
        }

        if (unknownKey)
        {
            await ctx.LogAsync(LogLevel.Warn, $"setting '{positional[0]}' is not declared; value stored but ignored").ConfigureAwait(false);
        }

        var restartRequired = SettingCatalog.Find(positional[0])?.RestartRequired ?? false;
        var metrics = new JsonObject
        {
            ["key"] = positional[0],
            ["value"] = positional[1],
            ["restartRequired"] = restartRequired,
        };
        return Ok($"{positional[0]} = {positional[1]}" + (restartRequired ? " (restart required)" : string.Empty), metrics);
    }

    private Task<CommandResult> ConfigUnsetAsync(TaskContext ctx, string[] args)
    {
        _ = ctx;
        var (positional, _) = Parse(args);
        var key = positional.FirstOrDefault();
        if (key is null)
        {
            return Task.FromResult(Fail(ErrorCodes.BadArgs, ExitCodes.Args, "config unset needs a key", "pd config unset <键>"));
        }

        if (!_kernel.Settings.Unset(key, out var definition) || definition is null)
        {
            return Task.FromResult(Fail(ErrorCodes.BadArgs, ExitCodes.Args, $"unknown setting '{key}'"));
        }
        _kernel.Logger.Info("config.unset", "setting reset to default", new System.Text.Json.Nodes.JsonObject { ["key"] = key });
        var metrics = new System.Text.Json.Nodes.JsonObject
        {
            ["key"] = key,
            ["restartRequired"] = definition.RestartRequired,
        };
        return Task.FromResult(Ok($"{key} reset to default" + (definition.RestartRequired ? " (restart required)" : string.Empty), metrics));
    }

    private static List<string> ReadTail(string path, int count, string? level)
    {
        var lines = new List<string>();
        if (!File.Exists(path))
        {
            return lines;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var buffer = new Queue<string>(count);
        while (reader.ReadLine() is { } line)
        {
            if (level is not null)
            {
                try
                {
                    var node = JsonNode.Parse(line);
                    var recordLevel = JsonUtil.String(node as JsonObject, "level");
                    if (!string.Equals(recordLevel, level, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Corrupt line is still useful; fall through.
                }
            }

            buffer.Enqueue(line);
            while (buffer.Count > count)
            {
                buffer.Dequeue();
            }
        }

        lines.AddRange(buffer);
        return lines;
    }
}
