using System.Text.Json.Nodes;

namespace PortableDev.Window;

/// <summary>Command-line options of the window shell and its self-test.</summary>
public sealed record WindowOptions(string Root, string Extensions, string? SelfTest, string? Smoke, string? KernelDll)
{
    public static WindowOptions Parse(string[] args)
    {
        string? root = null;
        string? extensions = null;
        string? selfTest = null;
        string? smoke = null;
        string? kernelDll = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--root":
                    root = ++index < args.Length ? args[index] : root;
                    break;
                case "--extensions":
                    extensions = ++index < args.Length ? args[index] : extensions;
                    break;
                case "--self-test":
                    selfTest = ++index < args.Length ? args[index] : selfTest;
                    break;
                case "--smoke":
                    smoke = ++index < args.Length ? args[index] : smoke;
                    break;
                case "--kernel-dll":
                    kernelDll = ++index < args.Length ? args[index] : kernelDll;
                    break;
            }
        }

        root ??= Environment.GetEnvironmentVariable("PD_ROOT");
        root ??= LocateRoot();
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidOperationException("cannot find the stick root");
        }

        // A launcher may pass the folder with a trailing separator or stray quotes.
        root = root.Trim().Trim('"');
        while (root.Length > 3 && (root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            root = root[..^1];
        }

        extensions ??= LocateExtensions(root);
        kernelDll ??= LocateKernelDll(root, kernelDll);

        // WebView2's virtual host mapping needs an absolute folder.
        extensions = Path.GetFullPath(extensions);
        return new WindowOptions(root, extensions, selfTest, smoke, kernelDll);
    }

    private static string? LocateRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 5 && directory is not null; depth++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "state", "stick.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string? LocateKernelDll(string root, string? explicitPath)
    {
        if (explicitPath is not null)
        {
            return explicitPath;
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "pd-kernel.dll"),
            Path.Combine(AppContext.BaseDirectory, "app", "pd-kernel.dll"),
            Path.Combine(root, "app", "pd-kernel.dll"),
            Path.Combine(root, "pd-kernel.dll"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private static string LocateExtensions(string root)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "extensions"),
            Path.Combine(AppContext.BaseDirectory, "..", "extensions"),
            Path.Combine(root, "app", "extensions"),
            Path.Combine(root, "extensions"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (Directory.Exists(full))
            {
                return full;
            }
        }

        return Path.Combine(root, "extensions");
    }
}

/// <summary>Headless self-test: exercises the kernel link and the UI page bridge plumbing.</summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(WindowOptions options)
    {
        var report = new JsonObject { ["ok"] = false };
        try
        {
            await using var link = await KernelLink.ConnectAsync(options.Root, options.KernelDll).ConfigureAwait(false);
            report["kernelVersion"] = link.KernelVersion;
            report["stickId"] = link.StickId;

            var pages = await link.RequestPagesAsync().ConfigureAwait(false);
            report["pages"] = pages.DeepClone();

            var version = await link.RunAsync(["version"]).ConfigureAwait(false);
            report["versionExitCode"] = version["exitCode"]?.GetValue<int>() ?? -1;

            var list = await link.RunAsync(["list"]).ConfigureAwait(false);
            report["listExitCode"] = list["exitCode"]?.GetValue<int>() ?? -1;

            var entryCount = 0;
            foreach (var node in pages)
            {
                if (node is JsonObject page && page["entry"]?.GetValue<string>() is not null)
                {
                    entryCount++;
                }
            }

            report["ok"] = entryCount >= 3
                && (version["exitCode"]?.GetValue<int>() ?? -1) == 0
                && (list["exitCode"]?.GetValue<int>() ?? -1) == 0;
        }
        catch (Exception ex)
        {
            report["error"] = ex.Message;
        }

        File.WriteAllText(options.SelfTest!, report.ToJsonString());
        return report["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }
}
