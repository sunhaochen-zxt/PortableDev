using System.Text.Json.Nodes;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;
using PortableDev.Core.Versioning;

namespace PortableDev.Core.Extensions;

public enum ExtensionOrigin
{
    Builtin,
    User,
}

public enum ExtensionState
{
    Disabled,
    Rejected,
    Ready,
    Started,
    Crashed,
}

public sealed record Capability(string Name, bool Mutates, int Version = 1);

public sealed record CommandSpec
{
    public required string Name { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public string? Summary { get; init; }

    public string? Usage { get; init; }

    public string? Group { get; init; }

    public string? Since { get; init; }

    public string? Deprecated { get; init; }

    public bool Hidden { get; init; }

    public bool Mutates { get; init; }

    public bool Parallel { get; init; }

    public JsonArray? Args { get; init; }

    public JsonObject? ExitCodes { get; init; }

    public JsonArray? Examples { get; init; }
}

public sealed record LifecycleSpec
{
    public string Start { get; init; } = "on-demand";

    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public int MaxConcurrency { get; init; } = 1;

    public string Restart { get; init; } = "on-crash";

    public int MaxRestarts { get; init; } = 3;

    public TimeSpan RestartBackoff { get; init; } = TimeSpan.FromSeconds(2);
}

/// <summary>Tolerant `ext.json` model (spec 2). Unknown fields are ignored on purpose.</summary>
public sealed class ExtensionManifest
{
    public required string Directory { get; init; }

    public required ExtensionOrigin Origin { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string? Title { get; init; }

    public bool Enabled { get; init; } = true;

    public int Priority { get; init; } = 100;

    public string? Entry { get; init; }

    public string Interpreter { get; init; } = "exe";

    public IReadOnlyList<string> EntryArgs { get; init; } = [];

    public string? Cwd { get; init; }

    public JsonObject? Env { get; init; }

    public bool InheritEnv { get; init; }

    public AbiRange RequiresKernel { get; init; } = AbiRange.Unbounded;

    public string? RequiresCli { get; init; }

    public string? Platform { get; init; }

    public IReadOnlyList<string> Depends { get; init; } = [];

    public IReadOnlyList<Capability> Provides { get; init; } = [];

    public IReadOnlyList<string> Requires { get; init; } = [];

    public IReadOnlyList<CommandSpec> Commands { get; init; } = [];

    public JsonArray? Pages { get; init; }

    public JsonArray? Permissions { get; init; }

    public LifecycleSpec Lifecycle { get; init; } = new();

    public ExtensionState State { get; set; } = ExtensionState.Ready;

    public string? RejectReason { get; set; }

    public bool OverridesBuiltin { get; set; }

    public string EntryPath => Entry is null ? Directory : Path.Combine(Directory, Entry);

    private static string? ResolveInside(string? value, string directory)
        => string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(Path.Combine(directory, value));

    public static (ExtensionManifest? Manifest, IReadOnlyList<string> Errors) Parse(string json, string directory, ExtensionOrigin origin)
    {
        var errors = new List<string>();
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException ex)
        {
            return (null, [$"invalid JSON: {ex.Message}"]);
        }

        if (root is null)
        {
            return (null, ["ext.json must be a JSON object"]);
        }

        var name = JsonUtil.String(root, "name") ?? string.Empty;
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9-]*$"))
        {
            errors.Add($"invalid extension name '{name}'");
        }

        var version = JsonUtil.String(root, "version") ?? string.Empty;
        if (version.Length == 0)
        {
            errors.Add("version is required");
        }

        var requiresKernel = AbiRange.Unbounded;
        var requiresKernelText = JsonUtil.String(root, "requiresKernel");
        if (requiresKernelText is not null && !AbiRange.TryParse(requiresKernelText, out requiresKernel))
        {
            errors.Add($"unparsable requiresKernel '{requiresKernelText}'");
        }

        var commands = new List<CommandSpec>();
        foreach (var node in JsonUtil.Array(root, "commands") ?? [])
        {
            if (node is not JsonObject command)
            {
                continue;
            }

            var commandName = JsonUtil.String(command, "name") ?? string.Empty;
            if (!System.Text.RegularExpressions.Regex.IsMatch(commandName, "^[a-z][a-z0-9-]*$"))
            {
                errors.Add($"invalid command name '{commandName}'");
                continue;
            }

            var mutates = JsonUtil.Bool(command, "mutates") ?? false;
            var parallel = JsonUtil.Bool(command, "parallel") ?? false;
            if (mutates && parallel)
            {
                errors.Add($"command '{commandName}' declares mutates=true together with parallel=true");
                continue;
            }

            commands.Add(new CommandSpec
            {
                Name = commandName,
                Aliases = JsonUtil.Strings(JsonUtil.Array(command, "aliases")).ToArray(),
                Summary = JsonUtil.String(command, "summary"),
                Usage = JsonUtil.String(command, "usage"),
                Group = JsonUtil.String(command, "group"),
                Since = JsonUtil.String(command, "since"),
                Deprecated = JsonUtil.String(command, "deprecated"),
                Hidden = JsonUtil.Bool(command, "hidden") ?? false,
                Mutates = mutates,
                Parallel = parallel,
                Args = JsonUtil.Array(command, "args"),
                ExitCodes = JsonUtil.Object(command, "exitCodes"),
                Examples = JsonUtil.Array(command, "examples"),
            });
        }

        var provides = new List<Capability>();
        foreach (var node in JsonUtil.Array(root, "provides") ?? [])
        {
            if (node is JsonValue)
            {
                var textValue = JsonUtil.StringValue(node);
                if (!string.IsNullOrWhiteSpace(textValue))
                {
                    provides.Add(new Capability(textValue, Mutates: true));
                }
            }
            else if (node is JsonObject capability)
            {
                var capabilityName = JsonUtil.String(capability, "name");
                if (!string.IsNullOrWhiteSpace(capabilityName))
                {
                    provides.Add(new Capability(capabilityName, JsonUtil.Bool(capability, "mutates") ?? true, JsonUtil.Int(capability, "version") ?? 1));
                }
            }
        }

        var lifecycleNode = JsonUtil.Object(root, "lifecycle");
        var lifecycle = new LifecycleSpec
        {
            Start = JsonUtil.String(lifecycleNode, "start") ?? "on-demand",
            StartTimeout = TimeUtil.ParseDuration(JsonUtil.String(lifecycleNode, "startTimeout")) ?? TimeSpan.FromSeconds(5),
            IdleTimeout = TimeUtil.ParseDuration(JsonUtil.String(lifecycleNode, "idleTimeout")) ?? TimeSpan.FromMinutes(5),
            HeartbeatTimeout = TimeUtil.ParseDuration(JsonUtil.String(lifecycleNode, "heartbeatTimeout")) ?? TimeSpan.FromSeconds(30),
            StopTimeout = TimeUtil.ParseDuration(JsonUtil.String(lifecycleNode, "stopTimeout")) ?? TimeSpan.FromSeconds(3),
            MaxConcurrency = JsonUtil.Int(lifecycleNode, "maxConcurrency") ?? 1,
            Restart = JsonUtil.String(lifecycleNode, "restart") ?? "on-crash",
            MaxRestarts = JsonUtil.Int(lifecycleNode, "maxRestarts") ?? 3,
            RestartBackoff = TimeUtil.ParseDuration(JsonUtil.String(lifecycleNode, "restartBackoff")) ?? TimeSpan.FromSeconds(2),
        };

        var manifest = new ExtensionManifest
        {
            Directory = directory,
            Origin = origin,
            Name = name,
            Version = version,
            Title = JsonUtil.String(root, "title"),
            Enabled = JsonUtil.Bool(root, "enabled") ?? true,
            Priority = JsonUtil.Int(root, "priority") ?? 100,
            Entry = JsonUtil.String(root, "entry"),
            Interpreter = JsonUtil.String(root, "interpreter") ?? "exe",
            EntryArgs = JsonUtil.Strings(JsonUtil.Array(root, "entryArgs")).ToArray(),
            Cwd = ResolveInside(JsonUtil.String(root, "cwd"), directory),
            Env = JsonUtil.Object(root, "env"),
            InheritEnv = JsonUtil.Bool(root, "inheritEnv") ?? false,
            RequiresKernel = requiresKernel,
            RequiresCli = JsonUtil.String(root, "requiresCli"),
            Platform = JsonUtil.String(root, "platform"),
            Depends = JsonUtil.Strings(JsonUtil.Array(root, "depends")).ToArray(),
            Provides = provides,
            Requires = JsonUtil.Strings(JsonUtil.Array(root, "requires")).ToArray(),
            Commands = commands,
            Pages = JsonUtil.Array(root, "pages"),
            Permissions = JsonUtil.Array(root, "permissions"),
            Lifecycle = lifecycle,
        };

        if (manifest.Enabled && string.IsNullOrWhiteSpace(manifest.Entry))
        {
            errors.Add("entry is required when enabled=true");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Entry))
        {
            var entryPath = Path.GetFullPath(Path.Combine(directory, manifest.Entry));
            var directoryPath = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
            if (!entryPath.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("entry must stay inside the extension directory");
            }
        }

        return (manifest, errors);
    }
}

