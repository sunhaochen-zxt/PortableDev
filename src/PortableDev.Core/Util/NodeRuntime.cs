namespace PortableDev.Core.Util;

/// <summary>
/// Locates the Node.js host used by the built-in extensions. The stick ships its own copy
/// under <c>&lt;stick&gt;\app\runtime\node\node.exe</c>, so extensions work on a machine without Node
/// installed; resolution order is explicit override, bundled copy, then PATH.
/// </summary>
public static class NodeRuntime
{
    public const string RelativeDirectory = @"app\runtime\node";

    /// <summary>Absolute path to the bundled Node host for a stick, or null when the stick has none.</summary>
    public static string? BundledHost(string? stickRoot)
    {
        if (string.IsNullOrWhiteSpace(stickRoot))
        {
            return null;
        }

        try
        {
            var host = Path.Combine(Path.GetFullPath(stickRoot!), "app", "runtime", "node", "node.exe");
            return File.Exists(host) ? host : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The Node host to run an extension: PD_NODE override, bundled copy, else "node".</summary>
    public static string ResolveHost(string? stickRoot)
    {
        var overridden = Environment.GetEnvironmentVariable("PD_NODE");
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return Path.GetFullPath(overridden);
        }

        return BundledHost(stickRoot) ?? "node";
    }

    /// <summary>True when the host is the copy shipped with the stick (used by doctor).</summary>
    public static bool IsBundled(string? stickRoot) => BundledHost(stickRoot) is not null;
}
