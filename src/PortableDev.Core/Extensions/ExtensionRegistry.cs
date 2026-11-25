using PortableDev.Core.Protocol;
using PortableDev.Core.Util;

namespace PortableDev.Core.Extensions;

/// <summary>
/// Scans `app\\extensions` (builtin, read-only baseline) and `extensions` (user, overrides builtin by name),
/// validates manifests and builds the single command table (architecture 3.8 / kernel.md 3).
/// </summary>
public sealed class ExtensionRegistry
{
    private readonly Dictionary<string, ExtensionManifest> _byName = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    private ExtensionRegistry(CommandTable commands)
    {
        Commands = commands;
    }

    public CommandTable Commands { get; }

    public IReadOnlyCollection<ExtensionManifest> Extensions => _byName.Values;

    public IReadOnlyList<string> Warnings => _warnings;

    public ExtensionManifest? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Scan order is deterministic: priority ascending, then name.</summary>
    public static ExtensionRegistry Scan(string stickRoot, string appRoot, int kernelAbi, CommandTable? table = null)
    {
        table ??= new CommandTable();
        var registry = new ExtensionRegistry(table);
        var candidates = new List<ExtensionManifest>();
        var builtinDir = Path.Combine(appRoot, "extensions");
        var userDir = Path.Combine(stickRoot, "extensions");

        foreach (var (dir, origin) in new[] { (builtinDir, ExtensionOrigin.Builtin), (userDir, ExtensionOrigin.User) })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var extensionDir in Directory.EnumerateDirectories(dir))
            {
                var manifestPath = Path.Combine(extensionDir, "ext.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var (manifest, errors) = ParseManifest(manifestPath, extensionDir, origin);
                if (manifest is null)
                {
                    registry._warnings.Add($"{Path.GetFileName(extensionDir)}: {string.Join("; ", errors)}");
                    continue;
                }

                if (errors.Count > 0)
                {
                    manifest.State = ExtensionState.Rejected;
                    manifest.RejectReason = string.Join("; ", errors);
                }

                if (origin == ExtensionOrigin.User &&
                    candidates.Any(c => c.Origin == ExtensionOrigin.User && string.Equals(c.Name, manifest.Name, StringComparison.Ordinal)))
                {
                    registry._warnings.Add($"{Path.GetFileName(extensionDir)}: duplicate user extension name '{manifest.Name}'");
                    continue;
                }

                candidates.Add(manifest);
            }
        }

        // User extensions override builtin ones with the same name (kernel.md 1.1).
        var userNames = candidates
            .Where(c => c.Origin == ExtensionOrigin.User && c.Enabled && c.State != ExtensionState.Rejected)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates.Where(c => c.Origin == ExtensionOrigin.Builtin && userNames.Contains(c.Name)))
        {
            candidate.State = ExtensionState.Disabled;
            candidate.RejectReason = "overridden by a user extension";
        }

        foreach (var manifest in candidates
                     .Where(c => c.State != ExtensionState.Disabled)
                     .OrderBy(c => c.Priority)
                     .ThenBy(c => c.Name, StringComparer.Ordinal))
        {
            registry._byName[manifest.Name] = manifest;

            if (!manifest.Enabled)
            {
                manifest.State = ExtensionState.Disabled;
                continue;
            }

            if (manifest.State == ExtensionState.Rejected)
            {
                continue;
            }

            if (manifest.Platform is not null && !IsPlatformMatch(manifest.Platform))
            {
                manifest.State = ExtensionState.Rejected;
                manifest.RejectReason = $"platform '{manifest.Platform}' does not match this machine";
                continue;
            }

            if (!manifest.RequiresKernel.Matches(kernelAbi))
            {
                manifest.State = ExtensionState.Rejected;
                manifest.RejectReason = $"requiresKernel '{manifest.RequiresKernel}' does not match ABI {kernelAbi}";
                continue;
            }

            string? conflict = null;
            foreach (var command in manifest.Commands)
            {
                conflict = table.TryRegister(new CommandEntry
                {
                    Name = command.Name,
                    Aliases = command.Aliases,
                    ExtensionName = manifest.Name,
                    Mutates = command.Mutates,
                    Parallel = command.Parallel,
                    Summary = command.Summary,
                    Usage = command.Usage,
                    Group = command.Group,
                    Hidden = command.Hidden,
                    Deprecated = command.Deprecated is not null,
                });

                if (conflict is not null)
                {
                    break;
                }
            }

            if (conflict is not null)
            {
                table.Unregister(manifest.Name);
                manifest.State = ExtensionState.Rejected;
                manifest.RejectReason = $"command or alias '{conflict}' is already registered";
                registry._warnings.Add($"{manifest.Name}: rejected, '{conflict}' already registered");
                continue;
            }

            manifest.State = ExtensionState.Ready;
        }

        return registry;
    }

    private static (ExtensionManifest? Manifest, IReadOnlyList<string> Errors) ParseManifest(string path, string directory, ExtensionOrigin origin)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return (null, [$"cannot read ext.json: {ex.Message}"]);
        }

        return ExtensionManifest.Parse(json, directory, origin);
    }

    private static bool IsPlatformMatch(string platform)
    {
        if (!platform.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return platform.EndsWith("x64", StringComparison.OrdinalIgnoreCase) == Environment.Is64BitProcess;
    }

    /// <summary>Start order: dependencies first (best effort; cycles are reported, not fatal).</summary>
    public IReadOnlyList<ExtensionManifest> StartOrder()
    {
        var result = new List<ExtensionManifest>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        void Visit(ExtensionManifest manifest)
        {
            if (result.Contains(manifest) || !visiting.Add(manifest.Name))
            {
                return;
            }

            foreach (var dependency in manifest.Depends)
            {
                var name = dependency.Split('>', '@', '=')[0].Trim();
                if (_byName.TryGetValue(name, out var found) && found.State == ExtensionState.Ready)
                {
                    Visit(found);
                }
            }

            visiting.Remove(manifest.Name);
            result.Add(manifest);
        }

        foreach (var manifest in _byName.Values.Where(m => m.State == ExtensionState.Ready).OrderBy(m => m.Priority))
        {
            Visit(manifest);
        }

        return result;
    }
}
