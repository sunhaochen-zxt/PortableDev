using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Win;

namespace PortableDev.Gui.Engine;

/// <summary>
/// Outcome of resolving the stick. Never throws: the dashboard must be able to show
/// "no stick plugged in" as a normal state rather than a crash.
/// </summary>
public sealed class StickInfo
{
    public string? Root { get; init; }
    public int CandidateCount { get; init; }
    public string? Label { get; init; }
    public string? FileSystem { get; init; }
    public long FreeBytes { get; init; }
    public long TotalBytes { get; init; }
    public RootManifest? Manifest { get; init; }
    public string? ManifestError { get; init; }

    /// <summary>Why the stick could not be resolved (ambiguous, marker missing, ...).</summary>
    public string? Error { get; init; }

    /// <summary>Every root that carries the marker, so the window can name the ambiguity.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    public bool Found => Root is not null;
    public bool Degraded => Manifest is null && Found;
}

public sealed class EngineException : Exception
{
    public EngineException(string message) : base(message) { }
}

/// <summary>
/// The GUI's only door to the engine. It reuses exactly the same Core planning and
/// Win helpers as the CLI, so the GUI can never drift from what 'PortableDev plan'
/// would do. Nothing here mutates the machine: apply/clean go through the CLI exe.
/// </summary>
public static class PortableEngine
{
    /// <summary>Locate the stick and load its manifest without touching the machine.</summary>
    public static StickInfo Locate(string? explicitRoot = null)
    {
        var (root, count, locateError) = VolumeLocator.ResolveDetailed(explicitRoot);
        if (root is null)
            return new StickInfo
            {
                CandidateCount = count,
                Error = locateError,
                // Naming the candidates matters: "multiple sticks found" with no list is
                // not actionable. The CLI prints the same list on stderr.
                Candidates = VolumeLocator.Candidates().Select(c => c.Root).ToList(),
            };

        RootManifest? manifest = null;
        string? error = null;
        try
        {
            manifest = ManifestLoader.Load(root);
        }
        catch (ManifestException ex)
        {
            error = string.Join("; ", ex.Errors);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        long free = 0, total = 0;
        string fs = "?";
        try
        {
            var d = new DriveInfo(root);
            free = d.AvailableFreeSpace;
            total = d.TotalSize;
            fs = d.DriveFormat;
        }
        catch
        {
            // drive stats are cosmetic
        }

        return new StickInfo
        {
            Root = root,
            CandidateCount = count,
            Label = VolumeLocator.LabelOf(root),
            FileSystem = fs,
            FreeBytes = free,
            TotalBytes = total,
            Manifest = manifest,
            ManifestError = error,
        };
    }

    /// <summary>Build the plan for the given stick. Pure: reads registry + filesystem only.</summary>
    public static Plan BuildPlan(StickInfo stick, string? stableOverride = null,
        bool degraded = false, bool strict = false)
    {
        if (stick.Root is null || stick.Manifest is null)
            throw new EngineException("no stick with a valid manifest");

        var stable = stableOverride ?? stick.Manifest.Root.Stable;
        var resolver = new PathResolver(stable, stick.Root);
        var state = ReadState(stick.Root, resolver.StableRoot, degraded ? stick.Root : null);

        return new Planner(stick.Manifest, resolver).Build(state, new PlannerOptions
        {
            NoJunction = degraded,
            TreatMissingToolsAsBlocker = strict,
            HomeAdopt = stick.Manifest.Apply.HomeAdopt,
            EnvironmentVariable = Environment.GetEnvironmentVariable,
        });
    }

    /// <summary>Same state the CLI reads. Exposed so the dashboard can show it verbatim.</summary>
    public static MachineState ReadState(string stickRoot, string stableRoot, string? homeStoreBase = null) =>
        ReadRawState(stickRoot, stableRoot, homeStoreBase);

    private static MachineState ReadRawState(string stickRoot, string stableRoot, string? homeStoreBase = null)
    {
        var reg = new RegistryEnvironment(false);
        var rawPath = reg.ReadUserPathRaw();

        var junctionExists = Directory.Exists(stableRoot) && Junction.IsReparsePoint(stableRoot);
        var junctionTarget = junctionExists ? Junction.GetTarget(stableRoot) : null;

        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in reg.ReadUserEnvRaw())
            current[kv.Key] = Environment.ExpandEnvironmentVariables(kv.Value);

        return new MachineState
        {
            UserPathRaw = rawPath,
            UserPathKind = reg.ReadUserPathKind().ToString(),
            CurrentEnv = current,
            JunctionExists = junctionExists,
            JunctionTarget = junctionTarget,
            StickRoot = stickRoot,
            IsElevated = RegistryEnvironment.IsElevated(),
            HomeLinks = ReadHomeLinks(stickRoot, stableRoot, homeStoreBase),
        };
    }

    /// <summary>
    /// Inspect each declared [[home_dir]] slot. A slot is "linked" when the directory on
    /// the machine is a junction pointing at its store on the stick.
    ///
    /// <paramref name="homeStoreBase"/> is where the links must point: the stable root
    /// normally, the stick itself in degraded mode.
    /// </summary>
    public static IReadOnlyList<HomeLinkState> ReadHomeLinks(
        string stickRoot, string stableRoot, string? homeStoreBase = null)
    {
        var storeRoot = string.IsNullOrWhiteSpace(homeStoreBase) ? stableRoot : homeStoreBase!;
        var list = new List<HomeLinkState>();

        RootManifest manifest;
        try
        {
            manifest = ManifestLoader.Load(stickRoot);
        }
        catch (ManifestException)
        {
            return list;
        }

        foreach (var hd in manifest.HomeDirs)
        {
            var hostRoot = Environment.GetEnvironmentVariable(hd.Root);
            if (string.IsNullOrWhiteSpace(hostRoot))
            {
                hostRoot = hd.Root.ToUpperInvariant() switch
                {
                    "USERPROFILE" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "APPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "LOCALAPPDATA" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    _ => string.Empty,
                };
            }

            var hostPath = Path.Combine(hostRoot, hd.Sub);
            var storeRel = (hd.Store ?? "home/" + hd.Sub).Replace('/', '\\').Trim('\\');
            var storePath = Path.Combine(storeRoot, storeRel);

            // File.GetAttributes also works on a junction whose target is gone, which
            // Directory.Exists silently reports as existing.
            var exists = false;
            try { _ = File.GetAttributes(hostPath); exists = true; } catch { }

            var isLink = exists && Junction.IsReparsePoint(hostPath);
            var target = isLink ? Junction.GetTarget(hostPath) : null;

            var withContent = false;
            if (exists && !isLink)
            {
                try { withContent = Directory.EnumerateFileSystemEntries(hostPath).Any(); }
                catch { withContent = true; }
            }

            list.Add(new HomeLinkState
            {
                Display = hd.Root + "/" + hd.Sub.Replace('\\', '/'),
                RootVar = hd.Root,
                Sub = hd.Sub,
                HostPath = hostPath,
                StorePath = storePath,
                Exists = exists,
                IsLink = isLink,
                LinkTarget = target,
                IsRealWithContent = withContent,
                Notes = hd.For,
            });
        }

        return list;
    }

    public static string Normalize(string path) => PathResolver.NormalizeRoot(path);

    /// <summary>Describe the junction state in one line, in the same words Doctor uses.</summary>
    public static (bool Ok, string Detail) JunctionStatus(string stableRoot, string stickRoot)
    {
        if (!Directory.Exists(stableRoot))
            return (false, stableRoot + " does not exist (run apply)");

        if (!Junction.IsReparsePoint(stableRoot))
            return (false, stableRoot + " exists but is a REAL directory, not a junction");

        var target = Junction.GetTarget(stableRoot);
        if (target is not null && !PathEditor.PathsEqual(target, stickRoot))
            return (false, stableRoot + " -> " + target + " (wrong stick; expected " + stickRoot + ")");

        return (true, stableRoot + " -> " + target);
    }
}
