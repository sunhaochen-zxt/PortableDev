using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Win;

namespace PortableDev.App;

/// <summary>Gathers the real machine state for the planner. Read-only.</summary>
public static class MachineStateReader
{
    public static MachineState Read(string stickRoot, string stableRoot, bool testSuffix = false, string? homeStoreBase = null)
    {
        var reg = new RegistryEnvironment(testSuffix);
        var rawPath = reg.ReadUserPathRaw();

        var junctionExists = Directory.Exists(stableRoot) && Junction.IsReparsePoint(stableRoot);
        var junctionTarget = junctionExists ? Junction.GetTarget(stableRoot) : null;

        // Expanded values let the planner tell "already set" from "needs update".
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in reg.ReadUserEnvRaw())
        {
            current[kv.Key] = Environment.ExpandEnvironmentVariables(kv.Value);
        }

        // Also consider what this process inherited, so a first run on a machine
        // that already got the vars via another shell is reported correctly.
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var name = e.Key?.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            if (name.Equals("Path", StringComparison.OrdinalIgnoreCase)) continue;
            if (!current.ContainsKey(name)) current[name] = e.Value?.ToString() ?? string.Empty;
        }

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
    /// Inspect every declared [[home_dir]] slot: does the directory exist, is it a link,
    /// where does it point, and does it hold a real directory's worth of data?
    ///
    /// <paramref name="storeBase"/> is where the links must point: the stable root normally,
    /// the stick itself in degraded mode (there is no junction to reach it through).
    /// </summary>
    public static IReadOnlyList<HomeLinkState> ReadHomeLinks(
        string stickRoot, string stableRoot, string? storeBase = null)
    {
        var storeRoot = string.IsNullOrWhiteSpace(storeBase) ? stableRoot : storeBase!;
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
                hostRoot = hd.Root.Equals("USERPROFILE", StringComparison.OrdinalIgnoreCase)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : hd.Root.Equals("APPDATA", StringComparison.OrdinalIgnoreCase)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                        : hd.Root.Equals("LOCALAPPDATA", StringComparison.OrdinalIgnoreCase)
                            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                            : string.Empty;
            }

            var hostPath = Path.Combine(hostRoot, hd.Sub);
            var storeRel = (hd.Store ?? "home/" + hd.Sub).Replace('/', '\\').Trim('\\');
            var storePath = Path.Combine(storeRoot, storeRel);

            // File.GetAttributes works on a junction even when its target is gone;
            // Directory.Exists and friends also return true for a dangling link.
            var exists = false;
            try { _ = File.GetAttributes(hostPath); exists = true; } catch { }

            var isLink = exists && Junction.IsReparsePoint(hostPath);
            var target = isLink ? Junction.GetTarget(hostPath) : null;
            var realWithContent = exists && !isLink && HasAnyEntry(hostPath);

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
                IsRealWithContent = realWithContent,
                Notes = hd.For,
            });
        }

        return list;
    }

    private static bool HasAnyEntry(string dir)
    {
        try
        {
            using var e = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            return e.MoveNext();
        }
        catch
        {
            return false;
        }
    }
}
