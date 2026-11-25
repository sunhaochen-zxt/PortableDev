using PortableDev.Core.Util;

namespace PortableDev.Kernel;

public static class MachineId
{
    /// <summary>A stable, non-identifying machine label used by the lock file and records (spec 4.1).</summary>
    public static string Current()
        => "m-" + Sha256Util.HexLower($"{Environment.MachineName}:{Environment.OSVersion.VersionString}:{Environment.ProcessorCount}")[..12];
}
