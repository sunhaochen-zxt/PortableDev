using System.Diagnostics;
using System.Text.Json;
using PortableDev.Core;
using PortableDev.Win;

namespace PortableDev.App;

/// <summary>Runs each tool's declared [[verify]] commands.</summary>
public static class Verifier
{
    public static int Run(CommandOptions opts)
    {
        var (root, count) = VolumeLocator.Resolve(opts.Root);
        if (root is null)
        {
            Console.Error.WriteLine(count == 0 ? "stick not found" : "multiple sticks found; use --root");
            return 4;
        }

        var manifest = ManifestLoader.Load(root);
        var resolver = new PathResolver(opts.Stable ?? manifest.Root.Stable, root);

        var results = new List<VerifyResult>();
        foreach (var (id, tool) in manifest.Tools.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!tool.Tool.Enabled) continue;

            var toolRoot = Path.Combine(root, PathResolver.ToolRelativeDir(tool).Replace('/', '\\'));
            foreach (var v in tool.Verify)
            {
                results.Add(Execute(id, toolRoot, v));
            }

            if (tool.Verify.Count == 0)
                results.Add(new VerifyResult(id, "(no verify declared)", true, null, 0));
        }

        if (opts.Json)
        {
            var json = JsonSerializer.Serialize(results.Select(r => new
            {
                tool = r.Tool, command = r.Command, ok = r.Ok, output = r.Output, exitCode = r.ExitCode,
            }), new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
        }
        else
        {
            foreach (var r in results)
            {
                var mark = r.Ok ? "PASS" : "FAIL";
                Console.WriteLine($"{mark}  {r.Tool,-12} {r.Command}");
                if (!r.Ok && !string.IsNullOrWhiteSpace(r.Output))
                    Console.WriteLine($"      {r.Output!.Replace("\n", "\n      ")}");
            }

            var fail = results.Count(r => !r.Ok);
            Console.WriteLine();
            Console.WriteLine($"{results.Count - fail}/{results.Count} passed");
            return fail == 0 ? 0 : 1;
        }

        return results.All(r => r.Ok) ? 0 : 1;
    }

    private static VerifyResult Execute(string toolId, string toolRoot, PortableDev.Core.Model.ToolManifest.VerifySpec spec)
    {
        // `run` is a whole command line, so it is tokenized with the same quoting rules
        // Windows uses — not split on the first space, which mangles any exe path that
        // contains one and turns a script's arguments into a single blob.
        var tokens = CommandLine.Tokenize(spec.Run);
        if (tokens.Count == 0)
            return new VerifyResult(toolId, spec.Run, false, "verify command is empty", -1);

        var exe = tokens[0];
        var args = tokens.Skip(1).ToList();

        var resolved = ResolveExecutable(toolRoot, exe);

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = toolRoot,
        };

        // .cmd / .bat are not real executables: Process.Start cannot launch them
        // directly, so route through cmd.exe. npm, npx, tsc and code are all .cmd.
        var isScript = resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        if (isScript)
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(resolved);
        }
        else
        {
            psi.FileName = resolved;
        }

        // ArgumentList, not Arguments: the runtime quotes each token, so an argument
        // with spaces survives as one argument instead of being re-split by the child.
        foreach (var a in args) psi.ArgumentList.Add(a);

        var pathPrefix = $"{toolRoot};{Path.Combine(toolRoot, "bin")};{toolRoot}\\cmd";
        psi.Environment["PATH"] = $"{pathPrefix};{Environment.GetEnvironmentVariable("PATH")}";

        // Never leak Electron's "run as node" mode into tool children. If the host
        // process is an Electron app it sets ELECTRON_RUN_AS_NODE=1, which makes
        // VSCode's Code.exe behave as a plain node interpreter.
        psi.Environment.Remove("ELECTRON_RUN_AS_NODE");
        psi.Environment.Remove("ELECTRON_NO_ATTACH_CONSOLE");
        psi.Environment.Remove("VSCODE_DEV");

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return new VerifyResult(toolId, spec.Run, false, "process failed to start", -1);

            // Drain both pipes concurrently. Reading stdout to completion first deadlocks
            // as soon as a tool fills the other pipe's buffer (typically a tool that writes
            // its diagnostics to stderr): the child blocks on write, we block on read, and
            // the 60 s timeout below is never reached because we are not waiting on the
            // process at all. async drain lets WaitForExit own the timeout.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000))
            {
                try { p.Kill(); } catch { }
                return new VerifyResult(toolId, spec.Run, false, "timeout", -1);
            }

            var output = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim();
            var expectOk = Matches(spec, output);
            var ok = p.ExitCode == 0 && expectOk;

            if (!ok)
            {
                // A bare FAIL next to the tool's own output explains nothing: "PowerShell 7.6.6"
                // looks like a healthy answer even when the manifest expected 7.6.8. State the
                // reason first, then the raw output.
                var why = new List<string>();
                if (p.ExitCode != 0) why.Add($"exit code {p.ExitCode}");
                if (!expectOk) why.Add("output does not contain " + DescribeExpect(spec));
                output = string.Join("; ", why) + "\n" + output;
            }

            return new VerifyResult(toolId, spec.Run, ok, output, p.ExitCode);
        }
        catch (Exception ex)
        {
            return new VerifyResult(toolId, spec.Run, false, ex.Message, -1);
        }
    }

    private static bool Matches(PortableDev.Core.Model.ToolManifest.VerifySpec spec, string output)
    {
        if (spec.ExpectAny.Count > 0)
            return spec.ExpectAny.Any(e => output.Contains(e, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrEmpty(spec.Expect)) return true;
        return output.Contains(spec.Expect!, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeExpect(PortableDev.Core.Model.ToolManifest.VerifySpec spec)
    {
        if (spec.ExpectAny.Count > 0)
            return "\"" + string.Join("\" or \"", spec.ExpectAny) + "\"";
        return "\"" + spec.Expect + "\"";
    }

    private static string ResolveExecutable(string toolRoot, string exe)
    {
        if (Path.IsPathRooted(exe)) return exe;

        var dirs = new[] { toolRoot, Path.Combine(toolRoot, "bin"), Path.Combine(toolRoot, "cmd") };

        // Do not use Path.HasExtension: "ld.lld" and "clang-cl" contain dots but are
        // not extensions. Only treat a known executable suffix as "already complete".
        var known = new[] { ".exe", ".cmd", ".bat", ".com" };
        var hasExecExtension = known.Any(k => exe.EndsWith(k, StringComparison.OrdinalIgnoreCase));

        // Extensioned candidates first. Node and VS Code ship an extensionless
        // shell wrapper (npm, code) next to npm.cmd / code.cmd; picking the
        // wrapper would make Process.Start fail with "not a valid application".
        var extensions = hasExecExtension ? new[] { string.Empty } : new[] { ".exe", ".cmd", ".bat" };
        foreach (var dir in dirs)
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, exe + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // Bare name last (e.g. a real ELF-like file or a pathless tool).
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        return exe;
    }
}

public sealed record VerifyResult(string Tool, string Command, bool Ok, string? Output, int ExitCode);
