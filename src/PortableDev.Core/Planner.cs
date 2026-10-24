using PortableDev.Core.Model;

namespace PortableDev.Core;

/// <summary>
/// Turns (manifest, machine state) into a <see cref="Plan"/>. Pure: no I/O, no registry,
/// no filesystem writes. Everything the applier does must first appear here.
/// </summary>
public sealed class Planner
{
    private readonly RootManifest _manifest;
    private readonly PathResolver _resolver;

    public Planner(RootManifest manifest, PathResolver resolver)
    {
        _manifest = manifest;
        _resolver = resolver;
    }

    public Plan Build(MachineState state, PlannerOptions? options = null)
    {
        options ??= new PlannerOptions();

        // Use the resolver's root, not the manifest's: --stable must override it.
        var stableRoot = _resolver.StableRoot;
        var steps = new List<PlanStep>();
        var warnings = new List<PlanWarning>();

        // ---- 1. Junction -------------------------------------------------------
        var degraded = options.NoJunction;

        if (degraded)
        {
            // Degraded mode: never touch C:\Dev. PATH points at the real stick path.
            steps.Add(new PlanStep
            {
                Kind = "junction",
                Change = ChangeKind.None,
                Target = stableRoot,
                After = "(skipped: no-junction mode)",
            });

            warnings.Add(new PlanWarning
            {
                Code = "degraded",
                Message = "No-junction mode: PATH references the stick's real path, which breaks if the drive letter changes.",
            });
        }
        else
        {
            var junctionPointsAtRoot = state.JunctionExists &&
                !string.IsNullOrEmpty(state.JunctionTarget) &&
                PathEditor.PathsEqual(state.JunctionTarget!, state.StickRoot);

            if (!state.JunctionExists)
            {
                steps.Add(new PlanStep
                {
                    Kind = "junction",
                    Change = ChangeKind.Add,
                    Target = stableRoot,
                    After = _resolver.StickRoot,
                });
            }
            else if (!junctionPointsAtRoot)
            {
                steps.Add(new PlanStep
                {
                    Kind = "junction",
                    Change = ChangeKind.Update,
                    Target = stableRoot,
                    Before = state.JunctionTarget,
                    After = _resolver.StickRoot,
                });
            }
            else
            {
                steps.Add(new PlanStep
                {
                    Kind = "junction",
                    Change = ChangeKind.None,
                    Target = stableRoot,
                    After = _resolver.StickRoot,
                });
            }
        }

        // ---- 2. PATH -----------------------------------------------------------
        var managedBefore = PathEditor.ManagedEntries(state.UserPathRaw, stableRoot);

        // Degraded mode rebuilds the entries on the stick root rather than textually
        // replacing "C:\Dev" inside the built strings: a string replace also rewrites any
        // path that merely starts with the root (C:\Dev vs C:\Development).
        var desired = _resolver
            .BuildPathEntries(_manifest, options.NoJunction ? _resolver.StickRoot : null)
            .Select(e => e.Entry)
            .ToList();

        // Compare MANAGED entries only. Comparing the merged PATH flagged
        // untouched foreign entries (C:\Windows, ...) as "added" on every run,
        // breaking the no-op property of a second apply.
        var managedDesired = PathEditor.Split(string.Join(";", desired));

        var removed = managedBefore
            .Where(b => !managedDesired.Any(a => PathEditor.PathsEqual(a, b)))
            .ToList();
        var added = managedDesired
            .Where(a => !managedBefore.Any(b => PathEditor.PathsEqual(a, b)))
            .ToList();

        foreach (var r in removed)
            steps.Add(new PlanStep { Kind = "path", Change = ChangeKind.Remove, Target = r, Before = r });

        foreach (var a in added)
            steps.Add(new PlanStep { Kind = "path", Change = ChangeKind.Add, Target = a, After = a });

        if (removed.Count == 0 && added.Count == 0)
            steps.Add(new PlanStep { Kind = "path", Change = ChangeKind.None, Target = "PATH", After = "unchanged" });

        var newPathRaw = PathEditor.Rebuild(state.UserPathRaw, stableRoot, desired);

        // ---- 3. Environment ----------------------------------------------------
        var envToSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Two declarations of the same name used to be silent last-wins. Say so instead:
        // a variable whose value depends on file order is a bug waiting to happen.
        void DeclareEnv(EnvVarSpec spec, string value, string source)
        {
            if (envToSet.TryGetValue(spec.Name, out var previous) &&
                !string.Equals(previous, value, StringComparison.Ordinal))
            {
                warnings.Add(new PlanWarning
                {
                    Code = "env-conflict",
                    Message = $"{spec.Name} is declared more than once with different values; " +
                              $"{source} wins ({previous} -> {value}).",
                });
            }

            envToSet[spec.Name] = value;
            steps.Add(EnvStep(state, spec.Name, value));
        }

        foreach (var e in _manifest.Env)
            DeclareEnv(e, _resolver.Expand(e.Value), "manifest.toml");

        foreach (var (id, tool) in _manifest.Tools)
        {
            if (!tool.Tool.Enabled) continue;
            foreach (var e in tool.Env)
                DeclareEnv(e, _resolver.Expand(e.Value, tool), id + ".toml");
        }

        // ---- 4. Tool presence --------------------------------------------------
        foreach (var (id, tool) in _manifest.Tools)
        {
            if (!tool.Tool.Enabled) continue;

            var rel = PathResolver.ToolRelativeDir(tool);
            var real = $@"{_resolver.StickRoot}\{rel}";
            var present = options.DirectoryExists(real);

            steps.Add(new PlanStep
            {
                Kind = "tool",
                Change = present ? ChangeKind.None : ChangeKind.Add,
                Target = $"{id} {tool.Tool.Version}",
                After = present ? real : $"{real} (missing)",
            });

            if (!present)
            {
                warnings.Add(new PlanWarning
                {
                    Code = "tool-missing",
                    Message = $"{id} {tool.Tool.Version} not found at {real}",
                    Blocker = options.TreatMissingToolsAsBlocker,
                });
            }
        }

        // ---- 5. Foreign PATH entries that shadow our commands ------------------
        var foreign = PathEditor.ForeignEntries(state.UserPathRaw, stableRoot);
        var provided = _manifest.Tools.Values
            .Where(t => t.Tool.Enabled)
            .SelectMany(t => t.Tool.Provides)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var f in foreign)
        {
            var leaf = f.TrimEnd('\\').Split('\\').LastOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(leaf)) continue;

            foreach (var cmd in provided)
            {
                if (leaf.Equals(cmd, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(new PlanWarning
                    {
                        Code = "shadowed",
                        Message = $"Foreign PATH entry '{f}' may shadow '{cmd}'. Not removed automatically.",
                    });
                }
            }
        }

        // ---- 6. Home links -----------------------------------------------------
        // Tools that read HOME/APPDATA lose their state with the machine. Each declared
        // slot becomes a junction on the system drive pointing into the stick.
        var homeBase = degraded ? _resolver.StickRoot : stableRoot;

        foreach (var hd in _manifest.HomeDirs)
        {
            var display = hd.Root + "/" + hd.Sub.Replace('\\', '/');
            var state_ = state.HomeLinks.FirstOrDefault(s =>
                string.Equals(s.Display, display, StringComparison.OrdinalIgnoreCase))
                ?? Unobserved(display, hd, homeBase, options);

            if (state_.IsLink &&
                !string.IsNullOrEmpty(state_.LinkTarget) &&
                PathEditor.PathsEqual(state_.LinkTarget!, state_.StorePath))
            {
                steps.Add(new PlanStep
                {
                    Kind = "home",
                    Change = ChangeKind.None,
                    Target = state_.Display,
                    After = state_.HostPath + " -> " + state_.StorePath,
                });
                continue;
            }

            if (state_.IsRealWithContent)
            {
                // Never move somebody's data silently. Adopt only when told to.
                if (!options.HomeAdopt)
                {
                    steps.Add(new PlanStep
                    {
                        Kind = "home",
                        Change = ChangeKind.None,
                        Target = state_.Display,
                        After = "(skipped: real directory in the way)",
                    });
                    warnings.Add(new PlanWarning
                    {
                        Code = "home-blocked",
                        Message = $"{state_.HostPath} already exists and is a real directory. " +
                                  "Its contents are left alone; set home_adopt = true to move them onto the stick.",
                    });
                    continue;
                }

                steps.Add(new PlanStep
                {
                    Kind = "home",
                    Change = ChangeKind.Add,
                    Target = state_.Display,
                    Before = state_.HostPath + " (real directory, contents will move)",
                    After = state_.HostPath + " -> " + state_.StorePath,
                });
                continue;
            }

            steps.Add(new PlanStep
            {
                Kind = "home",
                Change = state_.IsLink ? ChangeKind.Update : ChangeKind.Add,
                Target = state_.Display,
                Before = state_.IsLink ? state_.LinkTarget : null,
                After = state_.HostPath + " -> " + state_.StorePath,
            });
        }

        return new Plan
        {
            StableRoot = stableRoot,
            StickRoot = _resolver.StickRoot,
            Degraded = degraded,
            HomeBase = homeBase,
            // Home links sit on the system drive, but a junction needs no privilege there
            // either (only symlinks do), so they never add a UAC prompt.
            NeedsElevation = !degraded &&
                steps.Any(s => s.Kind == "junction" && s.Change != ChangeKind.None) &&
                !state.IsElevated,
            Steps = steps,
            Warnings = warnings,
            HomeLinks = state.HomeLinks,
            NewUserPathRaw = newPathRaw,
            EnvToSet = envToSet,
        };
    }

    /// <summary>
    /// A declared slot that the caller did not describe. Assuming "absent" is the honest
    /// reading: the plan then proposes creating it, which converges once it exists.
    /// </summary>
    private HomeLinkState Unobserved(
        string display, RootManifest.HomeDirSection hd, string homeBase, PlannerOptions options)
    {
        // Injected rather than read from the process environment: Core must not depend on
        // ambient state. Without a lookup the slot is reported at its display path, which
        // is the honest answer for a slot the caller did not describe.
        var hostRoot = options.EnvironmentVariable?.Invoke(hd.Root) ?? string.Empty;
        return new HomeLinkState
        {
            Display = display,
            RootVar = hd.Root,
            Sub = hd.Sub,
            HostPath = hostRoot.Length == 0 ? display : Path.Combine(hostRoot, hd.Sub),
            StorePath = Path.Combine(homeBase, (hd.Store ?? "home/" + hd.Sub).Replace('/', '\\').Trim('\\')),
            Exists = false,
            IsLink = false,
            Notes = hd.For,
        };
    }

    private PlanStep EnvStep(MachineState state, string name, string value)
    {
        state.CurrentEnv.TryGetValue(name, out var current);
        var change = current is null
            ? ChangeKind.Add
            : string.Equals(current, value, StringComparison.Ordinal)
                ? ChangeKind.None
                : ChangeKind.Update;

        return new PlanStep
        {
            Kind = "env",
            Change = change,
            Target = name,
            Before = current,
            After = value,
        };
    }
}

public sealed class PlannerOptions
{
    public bool NoJunction { get; init; }

    /// <summary>Move existing real directories into the stick instead of refusing to link them.</summary>
    public bool HomeAdopt { get; init; }
    public bool TreatMissingToolsAsBlocker { get; init; }
    public Func<string, bool> DirectoryExists { get; init; } = _ => true;

    /// <summary>
    /// How to resolve a home-dir root variable ("APPDATA", "USERPROFILE", ...) for a slot
    /// the caller did not describe. Injected so the planner performs no ambient reads; the
    /// CLI and GUI pass <c>Environment.GetEnvironmentVariable</c>.
    /// </summary>
    public Func<string, string?>? EnvironmentVariable { get; init; }
}
