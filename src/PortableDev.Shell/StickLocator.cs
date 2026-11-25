using System.Text.Json.Nodes;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;

namespace PortableDev.Shell;

/// <summary>Finds the stick root and the pipe name (kernel.md 4.4, commands.md section 2).</summary>
public static class StickLocator
{
    public static (string? Root, string? Error) Locate(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var full = Path.GetFullPath(explicitRoot);
            return LooksLikeRoot(full)
                ? (full, null)
                : (null, string.Concat("'", full, "' is not a PortableDev stick (missing state or config)"));
        }

        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (LooksLikeRoot(directory.FullName))
            {
                return (directory.FullName, null);
            }

            directory = directory.Parent;
        }

        return (null, "cannot find a PortableDev stick above the current directory (use --root)");
    }

    /// <summary>Reads kernel.pipePrefix from settings without writing anything.</summary>
    public static string PipePrefix(string root)
    {
        try
        {
            var path = Path.Combine(root, "config", "settings.json");
            if (File.Exists(path))
            {
                var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                var prefix = JsonUtil.String(JsonUtil.Object(node, "kernel"), "pipePrefix");
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    return prefix;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            // Fall through to the documented default.
        }

        return ProtocolConstants.DefaultPipePrefix;
    }

    public static string PipeName(string root, string stickId) => PipePrefix(root) + "-" + stickId;

    private static bool LooksLikeRoot(string path)
        => File.Exists(Path.Combine(path, "state", "stick.json")) ||
           (Directory.Exists(Path.Combine(path, "state")) && Directory.Exists(Path.Combine(path, "config")));
}
