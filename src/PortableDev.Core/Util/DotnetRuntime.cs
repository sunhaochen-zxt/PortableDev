using System.Text.Json.Nodes;

namespace PortableDev.Core.Util;

/// <summary>
/// Locates the .NET host that runs the portable apps. The stick ships its own runtime under
/// <c>&lt;stick&gt;\app\runtime\dotnet.exe</c> so a stick works on a machine without .NET installed;
/// resolution order is explicit override, bundled runtime, DOTNET_ROOT, then PATH.
/// </summary>
public static class DotnetRuntime
{
    /// <summary>Directory (relative to the stick root) that carries the bundled host + shared runtime.</summary>
    public const string RelativeDirectory = @"app\runtime";

    /// <summary>Absolute path to the bundled runtime host for a stick, or null when the stick has none.</summary>
    public static string? BundledHost(string? stickRoot)
    {
        if (string.IsNullOrWhiteSpace(stickRoot))
        {
            return null;
        }

        try
        {
            var host = Path.Combine(Path.GetFullPath(stickRoot!), "app", "runtime", "dotnet.exe");
            return File.Exists(host) ? host : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The dotnet host to run a dll: PD_DOTNET override, bundled runtime, DOTNET_ROOT, else "dotnet".</summary>
    public static string ResolveHost(string? stickRoot)
    {
        var host = ResolveHostOrNull(stickRoot);
        return host ?? "dotnet";
    }

    /// <summary>Same as ResolveHost but null means "fall back to PATH".</summary>
    public static string? ResolveHostOrNull(string? stickRoot)
    {
        var overridden = Environment.GetEnvironmentVariable("PD_DOTNET");
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        var bundled = BundledHost(stickRoot);
        if (bundled is not null)
        {
            return bundled;
        }

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The directory to expose as DOTNET_ROOT for child processes, or null when unknown.</summary>
    public static string? ResolveRoot(string? stickRoot)
    {
        var configured = Environment.GetEnvironmentVariable("PD_DOTNET");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetDirectoryName(Path.GetFullPath(configured));
        }

        var bundled = BundledHost(stickRoot);
        if (bundled is not null)
        {
            return Path.GetDirectoryName(bundled);
        }

        return Environment.GetEnvironmentVariable("DOTNET_ROOT");
    }

    /// <summary>True when the host is the one bundled with the stick (used by doctor/diagnostics).</summary>
    public static bool IsBundled(string? stickRoot)
        => BundledHost(stickRoot) is not null;

    /// <summary>Human-readable description for doctor output, without leaking machine paths unnecessarily.</summary>
    public static string Describe(string? stickRoot)
    {
        var bundled = BundledHost(stickRoot);
        if (bundled is not null)
        {
            var version = ReadRuntimeVersion(bundled);
            return "bundled" + (version is null ? string.Empty : " " + version);
        }

        var host = ResolveHostOrNull(stickRoot);
        return host is null ? "PATH" : "external " + host;
    }

    private static string? ReadRuntimeVersion(string dotnetHost)
    {
        try
        {
            var runtimeDir = Path.Combine(Path.GetDirectoryName(dotnetHost)!, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(runtimeDir))
            {
                return null;
            }

            var versions = Directory.GetDirectories(runtimeDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return versions.Length == 0 ? null : versions[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
