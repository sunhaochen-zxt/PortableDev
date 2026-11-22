using System.Text.Json;
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Win;

namespace PortableDev.App;

/// <summary>Read-only environment diagnosis: stick, junction, PATH, tools.</summary>
public static class Doctor
{
    public static int Run(CommandOptions opts)
    {
        var findings = new List<Finding>();

        var (root, count) = VolumeLocator.Resolve(opts.Root);
        findings.Add(new Finding("stick", root is not null, root ?? $"not found ({count} candidate(s))"));

        if (root is null)
        {
            Report(findings, opts);
            return 4;
        }

        var label = VolumeLocator.LabelOf(root);
        findings.Add(new Finding("volume-label", true, label ?? "(none)"));

        var drive = new DriveInfo(root);
        findings.Add(new Finding("free-space",
            drive.AvailableFreeSpace > 2L * 1024 * 1024 * 1024,
            $"{drive.AvailableFreeSpace / 1024.0 / 1024 / 1024:N1} GB free of {drive.TotalSize / 1024.0 / 1024 / 1024:N1} GB"));

        RootManifest manifest;
        try
        {
            manifest = ManifestLoader.Load(root);
            findings.Add(new Finding("manifest", true, $"{manifest.Tools.Count} tool(s), {manifest.Env.Count} env var(s)"));
        }
        catch (ManifestException ex)
        {
            findings.Add(new Finding("manifest", false, string.Join("; ", ex.Errors)));
            Report(findings, opts);
            return 3;
        }

        var resolver = new PathResolver(opts.Stable ?? manifest.Root.Stable, root);
        var stable = resolver.StableRoot;

        // Junction
        var isLink = Directory.Exists(stable) && Junction.IsReparsePoint(stable);
        var target = isLink ? Junction.GetTarget(stable) : null;
        if (!Directory.Exists(stable))
            findings.Add(new Finding("junction", false, $"{stable} does not exist (run apply)"));
        else if (!isLink)
            findings.Add(new Finding("junction", false, $"{stable} exists but is a REAL directory, not a junction"));
        else if (target is not null && !PathEditor.PathsEqual(target, root))
            findings.Add(new Finding("junction", false, $"{stable} -> {target} (wrong stick; expected {root})"));
        else
            findings.Add(new Finding("junction", true, $"{stable} -> {target}"));

        // PATH
        var reg = new RegistryEnvironment(opts.TestRegistry);
        var rawPath = reg.ReadUserPathRaw();
        var kind = reg.ReadUserPathKind();
        findings.Add(new Finding("path-kind",
            kind == Microsoft.Win32.RegistryValueKind.ExpandString || kind == Microsoft.Win32.RegistryValueKind.String,
            kind.ToString()));

        var managed = PathEditor.ManagedEntries(rawPath, stable);
        findings.Add(new Finding("path-managed", managed.Count > 0, $"{managed.Count} entr(ies) under {stable}"));

        var duplicates = PathEditor.Split(rawPath)
            .GroupBy(e => e.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        findings.Add(new Finding("path-duplicates", duplicates.Count == 0,
            duplicates.Count == 0 ? "none" : string.Join(" | ", duplicates)));

        // Filesystem
        findings.Add(new Finding("filesystem-ntfs",
            string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase),
            drive.DriveFormat + (drive.DriveFormat.Equals("exFAT", StringComparison.OrdinalIgnoreCase)
                ? " (no symlinks; PATH is rebuilt per session, so this is expected)"
                : "")));

        // Tools
        var missing = new List<string>();
        foreach (var (id, tool) in manifest.Tools)
        {
            if (!tool.Tool.Enabled) continue;
            var dir = Path.Combine(root, PathResolver.ToolRelativeDir(tool).Replace('/', '\\'));
            if (!Directory.Exists(dir)) missing.Add(id);
        }
        findings.Add(new Finding("tools", missing.Count == 0,
            missing.Count == 0 ? "all present" : "missing: " + string.Join(", ", missing)));

        // Home links: the directories that should travel on the stick.
        if (manifest.HomeDirs.Count > 0)
        {
            var links = MachineStateReader.ReadHomeLinks(root, stable);

            // Only a link pointing somewhere else is broken. "Not linked yet" and
            // "a real directory with data in it" are both legitimate resting states:
            // nothing here should push the user to act on every run.
            var wrong = links
                .Where(l => l.IsLink && (l.LinkTarget is null || !PathEditor.PathsEqual(l.LinkTarget, l.StorePath)))
                .ToList();
            var blocked = links.Where(l => l.Exists && !l.IsLink && l.IsRealWithContent).ToList();
            var linked = links.Count(l => l.IsLink && l.LinkTarget is not null && PathEditor.PathsEqual(l.LinkTarget, l.StorePath));

            var detail = $"{linked} linked";
            if (blocked.Count > 0) detail += ", " + blocked.Count + " left alone (has data, home_adopt off)";
            if (wrong.Count > 0) detail += ", pointing elsewhere: " + string.Join(", ", wrong.Select(w => w.Display));

            findings.Add(new Finding("home-links", wrong.Count == 0, detail));
        }

        // Integrity level. Windows starts a process at the lower of the parent token's
        // level and the executable image's own label, so an app folder that carries a
        // "Low" label yields a Low process whose children cannot write to state\ or
        // tools\ (Medium, no-write-up). That failure looks like a permission bug in
        // apply/verify, so name the real cause here instead.
        var integrityRid = Integrity.CurrentRid();
        var integrityName = integrityRid is null ? null : Integrity.NameOf(integrityRid.Value);
        var canWrite = integrityRid is null || Integrity.CanWriteToMedium(integrityRid.Value);
        findings.Add(new Finding("integrity", canWrite,
            integrityRid is null
                ? "unknown (could not read the process token)"
                : canWrite
                    ? integrityName!
                    : $"{integrityName} (the app folder carries a Low integrity label, so this process " +
                      $"and every tool it starts cannot write to {root}\\state or {root}\\tools). Fix: " +
                      $"icacls \"{AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar)}\" /setintegritylevel (OI)(CI)M /T"));

        // Elevation (informational)
        findings.Add(new Finding("elevated", true,
            RegistryEnvironment.IsElevated() ? "yes" : "no (junction creation may need one UAC prompt)"));

        Report(findings, opts);
        return findings.Any(f => !f.Ok && f.Blocking) ? 1 : 0;
    }

    private static void Report(List<Finding> findings, CommandOptions opts)
    {
        if (opts.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                findings.Select(f => new { check = f.Name, ok = f.Ok, detail = f.Detail }),
                new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        foreach (var f in findings)
            Console.WriteLine($"{(f.Ok ? "OK  " : "WARN")}  {f.Name,-18} {f.Detail}");

        var bad = findings.Count(f => !f.Ok);
        Console.WriteLine();
        Console.WriteLine(bad == 0 ? "all checks passed" : $"{bad} check(s) need attention");
    }

    public sealed record Finding(string Name, bool Ok, string Detail, bool Blocking = false);
}
