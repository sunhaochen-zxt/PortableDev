using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Win;

namespace PortableDev.App;

/// <summary>
/// Opens a terminal that already has the portable environment, without depending on
/// the registry reaching this session.
///
/// Why this exists: writing HKCU\Environment is only half the job. Explorer caches
/// its environment block and only refreshes it when it receives WM_SETTINGCHANGE —
/// and Windows blocks that message when the sender runs at a higher integrity level
/// than the receiver (UIPI). An elevated apply therefore cannot refresh a
/// medium-integrity Explorer, and every terminal opened afterwards still sees the
/// old PATH. This command sidesteps the whole problem by setting the variables on
/// the child process directly, so it works from any shell, elevated or not, with no
/// logout and no Explorer restart.
/// </summary>
public static class ShellLauncher
{
    /// <summary>Absolute path of the stick's pwsh.exe, or null when it is not installed.</summary>
    private static string? FindPwsh(RootManifest manifest, PathResolver resolver)
    {
        if (!manifest.Tools.TryGetValue("powershell", out var tool)) return null;
        if (!tool.Tool.Enabled) return null;

        var exe = Path.Combine(resolver.ToolRoot(tool), "pwsh.exe");
        return File.Exists(exe) ? exe : null;
    }

    public static int Run(CommandOptions opts)
    {
        var (root, count) = VolumeLocator.Resolve(opts.Root);
        if (root is null)
        {
            Console.Error.WriteLine(count == 0 ? "stick not found" : "multiple sticks found; use --root");
            return 4;
        }

        var manifest = ManifestLoader.Load(root);
        var stable = opts.Stable ?? manifest.Root.Stable;
        var resolver = new PathResolver(stable, root);

        // Desired state straight from the manifest: the same values apply would write,
        // but computed here so a stale registry cannot make this command wrong.
        var desiredEntries = resolver.BuildPathEntries(manifest).Select(e => e.Entry).ToList();
        var newPathRaw = PathEditor.Rebuild(
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty, resolver.StableRoot, desiredEntries);
        var newPath = Environment.ExpandEnvironmentVariables(newPathRaw);

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in manifest.Env)
            env[spec.Name] = resolver.Expand(spec.Value);
        foreach (var (id, tool) in manifest.Tools)
        {
            if (!tool.Tool.Enabled) continue;
            foreach (var spec in tool.Env)
                env[spec.Name] = resolver.Expand(spec.Value, tool);
        }

        // Pick the default shell from what is actually installed: since the stick
        // ships PowerShell 7, "shell" should open pwsh, not the built-in 5.1. Asking
        // for powershell/ps explicitly still gets you 5.1, on purpose.
        var pwshPath = FindPwsh(manifest, resolver);

        // A bare word selects a known shell; anything that looks like a path is used
        // verbatim (never lower-cased: a path may carry meaning in its casing).
        var arg = opts.Positional.Count > 0 ? opts.Positional[0] : "auto";
        var (file, args) = arg.ToLowerInvariant() switch
        {
            "auto" => pwshPath is not null
                ? (pwshPath, new[] { "-NoLogo", "-NoExit" })
                : ("powershell.exe", new[] { "-NoLogo", "-NoExit" }),
            "cmd" => ("cmd.exe", new[] { "/k" }),
            "powershell" or "ps" => ("powershell.exe", new[] { "-NoLogo", "-NoExit" }),
            "pwsh" => (pwshPath ?? "pwsh.exe", new[] { "-NoLogo", "-NoExit" }),
            "wt" => ("wt.exe", Array.Empty<string>()),
            _ => (arg, Array.Empty<string>()),
        };

        // CreateNoWindow must stay false: "start" needs a console to give the child
        // its own window. With it true the command silently does nothing.
        var psi = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("start");
        psi.ArgumentList.Add("\"\"");               // window title, required by start
        psi.ArgumentList.Add(file);
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["PATH"] = newPath;
        foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        // Never leak Electron's run-as-node flag into the new shell.
        psi.Environment.Remove("ELECTRON_RUN_AS_NODE");

        if (opts.Trace)
        {
            Console.WriteLine($"stable : {resolver.StableRoot}");
            Console.WriteLine($"stick  : {root}");
            Console.WriteLine($"PATH   : {newPath}");
            foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"env    : {kv.Key} = {kv.Value}");
            Console.WriteLine($"launch : {file} {string.Join(' ', args)}");
            return 0;
        }

        using var p = Process.Start(psi);
        if (p is null)
        {
            Console.Error.WriteLine("could not start a terminal");
            return 1;
        }

        Console.WriteLine($"opened {file} with {desiredEntries.Count} PATH entr(ies) and {env.Count} variable(s).");
        Console.WriteLine("This window has the environment whether or not the registry has reached Explorer yet.");
        return 0;
    }
}
