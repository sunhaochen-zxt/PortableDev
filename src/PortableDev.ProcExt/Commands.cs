using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;
using PortableDev.Proc;

namespace PortableDev.ProcExt;

/// <summary>Command handlers (kernel command table) and capability handlers (extensions calling us).</summary>
internal static class Commands
{
    private static readonly ManagedProcesses Managed = new();

    public static async Task HandleInvokeAsync(ExtensionClient client, string taskKey, JsonObject data)
    {
        var command = JsonUtil.String(data, "cmd") ?? string.Empty;
        // The kernel may route a longer entry ("proc list") so read-only subcommands run in
        // parallel; the first token selects the handler, argv still carries the sub-command.
        var verb = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        var argv = JsonUtil.Strings(JsonUtil.Array(data, "argv")).ToArray();

        try
        {
            switch (verb)
            {
                case "proc":
                    await HandleProcAsync(client, taskKey, argv).ConfigureAwait(false);
                    break;
                default:
                    await client.DoneAsync(
                        taskKey,
                        ExitCodes.Args,
                        error: Error(ErrorCodes.UnknownCommand, "unknown command: " + command)).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            await client.DoneAsync(
                taskKey,
                ExitCodes.Internal,
                error: Error(ErrorCodes.Internal, ex.Message)).ConfigureAwait(false);
        }
    }

    public static async Task HandleCallAsync(ExtensionClient client, string callId, JsonObject data)
    {
        var capability = JsonUtil.String(data, "cap") ?? string.Empty;
        var args = JsonUtil.Object(data, "args") ?? new JsonObject();

        try
        {
            switch (capability)
            {
                case "proc.spawn":
                    await client.CallOkAsync(callId, await SpawnAsync(client, args).ConfigureAwait(false)).ConfigureAwait(false);
                    break;

                case "proc.snapshot":
                    await client.CallOkAsync(callId, new JsonObject { ["processes"] = Managed.Snapshot() }).ConfigureAwait(false);
                    break;

                case "proc.kill":
                {
                    var pid = JsonUtil.Int(args, "pid") ?? 0;
                    var killed = Managed.Kill(pid);
                    await client.CallOkAsync(callId, new JsonObject { ["ok"] = killed, ["pid"] = pid }).ConfigureAwait(false);
                    break;
                }

                case "proc.system":
                {
                    var filter = JsonUtil.String(args, "filter");
                    var result = JsonUtil.Bool(args, "elevated") == true
                        ? Elevated.Run(new JsonObject { ["action"] = "system", ["filter"] = filter })
                        : SystemProcesses.Snapshot(filter);
                    if (result["error"] is JsonObject systemError)
                    {
                        await client.CallErrorAsync(
                            callId,
                            JsonUtil.String(systemError, "code") ?? ErrorCodes.Internal,
                            JsonUtil.String(systemError, "message") ?? "cannot enumerate system processes").ConfigureAwait(false);
                        break;
                    }

                    await client.CallOkAsync(callId, result).ConfigureAwait(false);
                    break;
                }

                default:
                    await client.CallErrorAsync(callId, ErrorCodes.Unsupported, "capability not implemented yet: " + capability)
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            await client.CallErrorAsync(callId, ErrorCodes.Internal, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one supervised process until it exits (or the timeout fires) and answers with its exit
    /// code. The caller owns the stdio pipes; we only hand the inherited handles to the child.
    /// </summary>
    private static async Task<JsonObject> SpawnAsync(ExtensionClient client, JsonObject args)
    {
        await Task.Yield();

        var command = JsonUtil.String(args, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("proc.spawn requires command");
        }

        var arguments = JsonUtil.Strings(JsonUtil.Array(args, "args")).ToArray();
        var cwd = JsonUtil.String(args, "cwd");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (JsonUtil.Object(args, "env") is { } env)
        {
            foreach (var (key, value) in env)
            {
                var text = JsonUtil.StringValue(value);
                if (text is not null)
                {
                    environment[key] = text;
                }
            }
        }

        var stdinPipe = JsonUtil.String(args, "stdinPipe") ?? throw new ArgumentException("proc.spawn requires stdinPipe");
        var stdoutPipe = JsonUtil.String(args, "stdoutPipe") ?? throw new ArgumentException("proc.spawn requires stdoutPipe");
        var stderrPipe = JsonUtil.String(args, "stderrPipe") ?? throw new ArgumentException("proc.spawn requires stderrPipe");

        var timeoutMs = JsonUtil.Int(args, "timeoutMs") ?? 0;
        if (timeoutMs <= 0)
        {
            timeoutMs = Settings.ScriptTimeoutSeconds * 1000;
        }

        int? cpuRatePercent = JsonUtil.Int(args, "cpuRatePercent");
        if (cpuRatePercent is int percent && (percent < 1 || percent > 100))
        {
            throw new ArgumentException("cpuRatePercent must be between 1 and 100");
        }

        var limits = new JobLimits
        {
            ProcessMemoryBytes = Math.Max(1, JsonUtil.Int(args, "memoryLimitMB") ?? Settings.ProcessMemoryMB) * 1024L * 1024L,
            ActiveProcesses = (uint)Math.Max(1, JsonUtil.Int(args, "activeProcessLimit") ?? Settings.ActiveProcesses),
            CpuRatePercent = cpuRatePercent,
        };

        var stdio = StdioPipes.Open(stdinPipe, stdoutPipe, stderrPipe);
        SpawnedProcess process;
        JobObject? job;
        try
        {
            (process, job) = ProcessSpawner.Spawn(new ProcessStartSpec
            {
                FileName = command,
                Arguments = arguments,
                WorkingDirectory = cwd,
                Environment = environment.Count == 0 ? null : environment,
                StandardInput = stdio.Stdin,
                StandardOutput = stdio.Stdout,
                StandardError = stdio.Stderr,
            }, limits);
        }
        finally
        {
            // The child holds inherited copies; closing ours lets EOF propagate through the pipes.
            stdio.Dispose();
        }

        var procId = Managed.NextId();
        Managed.Add(procId, "script", process.Pid, command, job);
        await client.LogEventAsync("proc.spawn", "info", "spawned managed process", new JsonObject
        {
            ["procId"] = procId,
            ["pid"] = process.Pid,
            ["kind"] = "script",
            ["command"] = command,
        }).ConfigureAwait(false);
        await client.TopicEventAsync("proc", "proc.started", new JsonObject
        {
            ["procId"] = procId,
            ["pid"] = process.Pid,
            ["kind"] = "script",
            ["command"] = command,
        }).ConfigureAwait(false);
        try
        {
            var exited = process.WaitForExit(TimeSpan.FromMilliseconds(timeoutMs));
            var timedOut = !exited;
            if (!exited)
            {
                // The registry owns the job: terminating through it disposes the job exactly once,
                // so a concurrent proc.kill cannot race this path onto a disposed handle.
                Managed.TryTerminate(procId);
                process.WaitForExit(TimeSpan.FromSeconds(3));
            }

            var gotExit = process.TryGetExitCode(out var exitCode);
            if (!gotExit)
            {
                exitCode = -1;
            }
            await client.LogEventAsync("proc.exit", timedOut ? "warn" : "info", "managed process exited", new JsonObject
            {
                ["procId"] = procId,
                ["pid"] = process.Pid,
                ["exitCode"] = exitCode,
                ["timedOut"] = timedOut,
            }).ConfigureAwait(false);
            await client.TopicEventAsync("proc", "proc.exited", new JsonObject
            {
                ["procId"] = procId,
                ["pid"] = process.Pid,
                ["exitCode"] = exitCode,
                ["timedOut"] = timedOut,
            }).ConfigureAwait(false);
            return new JsonObject
            {
                ["ok"] = gotExit,
                ["procId"] = procId,
                ["pid"] = process.Pid,
                ["exitCode"] = exitCode,
                ["timedOut"] = timedOut,
            };
        }
        finally
        {
            // Remove disposes the job only while this spawner still owns the entry; a concurrent
            // TryTerminate (or the timeout path above) already did it otherwise.
            Managed.Remove(procId);
            process.Dispose();
        }
    }

    private static async Task HandleProcAsync(ExtensionClient client, string taskKey, string[] argv)
    {
        var verb = argv.Length > 0 ? argv[0] : "help";
        switch (verb)
        {
            case "echo":
            {
                var text = string.Join(' ', argv.Skip(1));
                await client.LogAsync(taskKey, "info", "proc echo: " + text).ConfigureAwait(false);
                await client.DoneAsync(taskKey, ExitCodes.Ok, summary: "proc " + text).ConfigureAwait(false);
                break;
            }

            case "snapshot":
            {
                var snapshot = Managed.Snapshot();
                await client.KvAsync(taskKey, "managed processes", new JsonObject { ["managed"] = snapshot.Count })
                    .ConfigureAwait(false);
                await client.DoneAsync(taskKey, ExitCodes.Ok, summary: snapshot.Count + " managed process(es)")
                    .ConfigureAwait(false);
                break;
            }

            case "list":
            {
                var snapshot = Managed.Snapshot();
                var rows = new JsonArray();
                foreach (var node in snapshot)
                {
                    if (node is not JsonObject entry)
                    {
                        continue;
                    }

                    rows.Add(new JsonArray(
                        JsonUtil.String(entry, "procId"),
                        JsonUtil.Int(entry, "pid"),
                        JsonUtil.String(entry, "kind"),
                        JsonUtil.String(entry, "command")));
                }

                await client.EventAsync(taskKey, EventTypes.Table, new JsonObject
                {
                    ["key"] = "processes",
                    ["title"] = "managed processes",
                    ["columns"] = new JsonArray(
                        new JsonObject { ["key"] = "procId", ["title"] = "ID", ["type"] = "string" },
                        new JsonObject { ["key"] = "pid", ["title"] = "PID", ["type"] = "number" },
                        new JsonObject { ["key"] = "kind", ["title"] = "KIND", ["type"] = "string" },
                        new JsonObject { ["key"] = "command", ["title"] = "COMMAND", ["type"] = "string" }),
                    ["rows"] = rows,
                    ["final"] = true,
                }).ConfigureAwait(false);
                await client.DoneAsync(taskKey, ExitCodes.Ok, summary: snapshot.Count + " managed process(es)")
                    .ConfigureAwait(false);
                break;
            }

            case "info":
            {
                if (argv.Length < 2
                    || !int.TryParse(argv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var infoPid)
                    || infoPid <= 0)
                {
                    await client.DoneAsync(taskKey, ExitCodes.Args, error: Error(ErrorCodes.BadArgs, "usage: pd proc info <pid>"))
                        .ConfigureAwait(false);
                    break;
                }

                JsonObject? found = null;
                foreach (var node in Managed.Snapshot())
                {
                    if (node is JsonObject entry && JsonUtil.Int(entry, "pid") == infoPid)
                    {
                        found = entry;
                        break;
                    }
                }

                if (found is null)
                {
                    await client.DoneAsync(taskKey, ExitCodes.NotFound, error: Error(ErrorCodes.ProcNotFound, "no managed process with pid " + infoPid))
                        .ConfigureAwait(false);
                    break;
                }

                await client.KvAsync(taskKey, "managed process", new JsonObject { ["process"] = found.DeepClone() })
                    .ConfigureAwait(false);
                await client.DoneAsync(taskKey, ExitCodes.Ok, summary: "pid " + infoPid).ConfigureAwait(false);
                break;
            }

            case "system":
            {
                var systemFlags = ParseFlags(argv.Skip(1).ToArray());
                var filter = systemFlags.GetValueOrDefault("filter");
                var result = systemFlags.ContainsKey("elevated")
                    ? Elevated.Run(new JsonObject { ["action"] = "system", ["filter"] = filter })
                    : SystemProcesses.Snapshot(filter);

                if (result["error"] is JsonObject systemError)
                {
                    await client.DoneAsync(taskKey, ExitCodeForError(systemError), error: (JsonObject)systemError.DeepClone())
                        .ConfigureAwait(false);
                    break;
                }

                var processes = result["processes"] as JsonArray ?? new JsonArray();
                var rows = new JsonArray();
                foreach (var node in processes)
                {
                    if (node is not JsonObject item)
                    {
                        continue;
                    }

                    rows.Add(new JsonArray(
                        JsonUtil.Int(item, "pid"),
                        JsonUtil.Int(item, "ppid"),
                        JsonUtil.String(item, "name"),
                        JsonUtil.Int(item, "level"),
                        JsonUtil.String(item, "path")));
                }

                await client.EventAsync(taskKey, EventTypes.Table, new JsonObject
                {
                    ["key"] = "system-processes",
                    ["title"] = "system processes",
                    ["columns"] = new JsonArray(
                        new JsonObject { ["key"] = "pid", ["title"] = "PID", ["type"] = "number" },
                        new JsonObject { ["key"] = "ppid", ["title"] = "PPID", ["type"] = "number" },
                        new JsonObject { ["key"] = "name", ["title"] = "NAME", ["type"] = "string" },
                        new JsonObject { ["key"] = "level", ["title"] = "LEVEL", ["type"] = "number" },
                        new JsonObject { ["key"] = "path", ["title"] = "PATH", ["type"] = "path" }),
                    ["rows"] = rows,
                    ["final"] = true,
                }).ConfigureAwait(false);

                var denied = JsonUtil.Int(result, "denied") ?? 0;
                await client.DoneAsync(taskKey, ExitCodes.Ok,
                    summary: processes.Count + " process(es)" + (denied > 0 ? ", " + denied + " need admin" : string.Empty))
                    .ConfigureAwait(false);
                break;
            }

            case "kill":
            {
                if (argv.Length < 2
                    || !int.TryParse(argv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                    || pid <= 0)
                {
                    await client.DoneAsync(taskKey, ExitCodes.Args, error: Error(ErrorCodes.BadArgs, "usage: pd proc kill <pid>"))
                        .ConfigureAwait(false);
                    break;
                }

                if (!Managed.Kill(pid))
                {
                    if (Managed.WasReaped(pid))
                    {
                        await client.DoneAsync(taskKey, ExitCodes.NotFound, error: Error(ErrorCodes.ProcNotFound, "no managed process with pid " + pid)).ConfigureAwait(false);
                        break;
                    }

                    var killFlags = ParseFlags(argv.Skip(1).ToArray());
                    var forceCritical = killFlags.ContainsKey("force-critical");
                    var wantElevated = killFlags.ContainsKey("elevated");
                    var result = SystemProcesses.Kill(pid, forceCritical);
                    if (result["error"] is JsonObject killError
                        && wantElevated
                        && string.Equals(JsonUtil.String(killError, "code"), ErrorCodes.Elevation, StringComparison.Ordinal))
                    {
                        result = Elevated.Run(new JsonObject
                        {
                            ["action"] = "kill",
                            ["pid"] = pid,
                            ["forceCritical"] = forceCritical,
                        });
                    }

                    if (result["error"] is JsonObject finalError)
                    {
                        await client.DoneAsync(taskKey, ExitCodeForError(finalError), error: (JsonObject)finalError.DeepClone())
                            .ConfigureAwait(false);
                        break;
                    }

                    await client.DoneAsync(taskKey, ExitCodes.Ok, summary: "killed pid " + pid).ConfigureAwait(false);
                    break;
                }

                await client.DoneAsync(taskKey, ExitCodes.Ok, summary: "killed " + pid).ConfigureAwait(false);
                break;
            }

            default:
                await client.DoneAsync(
                    taskKey,
                    ExitCodes.Args,
                    error: Error(ErrorCodes.BadArgs, "unknown subcommand: " + verb)).ConfigureAwait(false);
                break;
        }
    }

    private static Dictionary<string, string?> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string? value = null;
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }

            flags[argument[2..]] = value;
        }

        return flags;
    }

    private static int ExitCodeForError(JsonObject error)
        => (JsonUtil.String(error, "code") ?? ErrorCodes.Internal) switch
        {
            ErrorCodes.BadArgs => ExitCodes.Args,
            ErrorCodes.Unsupported or ErrorCodes.ProcProtected => ExitCodes.Rejected,
            ErrorCodes.Elevation => ExitCodes.Permission,
            ErrorCodes.ProcNotFound => ExitCodes.NotFound,
            _ => ExitCodes.Internal,
        };

    private static JsonObject Error(string code, string message) => new()
    {
        ["code"] = code,
        ["message"] = message,
    };
}
