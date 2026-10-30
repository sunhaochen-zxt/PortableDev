namespace PortableDev.Core.Model;

/// <summary>Root manifest (manifest.toml).</summary>
public sealed class RootManifest
{
    public int Schema { get; init; } = 1;
    public RootSection Root { get; init; } = new();
    public ApplySection Apply { get; init; } = new();

    /// <summary>Tool ids in the order their path entries are prepended.</summary>
    public IReadOnlyList<string> PathOrder { get; init; } = Array.Empty<string>();

    public IReadOnlyList<EnvVarSpec> Env { get; init; } = Array.Empty<EnvVarSpec>();

    /// <summary>All tool manifests, keyed by id, in load order.</summary>
    public IReadOnlyDictionary<string, ToolManifest> Tools { get; init; } =
        new Dictionary<string, ToolManifest>(StringComparer.OrdinalIgnoreCase);

    public sealed class RootSection
    {
        /// <summary>Stable short junction path, e.g. C:\Dev.</summary>
        public string Stable { get; init; } = @"C:\Dev";

        /// <summary>Marker file name used to identify the stick.</summary>
        public string Marker { get; init; } = ".portabledev-root";
    }

    public sealed class ApplySection
    {
        public string BackupDir { get; init; } = "state/backup";
        public string JournalDir { get; init; } = "state/journal";

        /// <summary>Scratch directory for the elevated helper's result files.</summary>
        public string TempDir { get; init; } = "state/tmp";

        /// <summary>
        /// Move an existing real directory into the stick instead of failing when a home
        /// link slot is occupied. This is the only setting that rewrites state which was
        /// not created by PortableDev, so it is opt-in.
        /// </summary>
        public bool HomeAdopt { get; init; }
    }

    /// <summary>
    /// Where the tools that read HOME keep their data, so it travels on the stick
    /// instead of dying with the machine.
    ///
    /// Every entry becomes a junction on the system drive pointing into the stick
    /// ("home links"). The user profile folder is never redirected: NTFS does not
    /// allow a junction over it, and the browser/Installer profiles must not move.
    /// </summary>
    public IReadOnlyList<HomeDirSection> HomeDirs { get; init; } = Array.Empty<HomeDirSection>();

    /// <summary>
    /// Named environment profiles, gathered from every tool manifest.
    ///
    /// A profile is a set of variables that must not be applied globally: setting
    /// EnterpriseWDK or ProgramFiles(x86) for every process on the machine would change
    /// how unrelated programs behave. They are injected only into a shell that asks for
    /// them ("PortableDev shell --profile msvc") or into a script that does.
    /// </summary>
    public IReadOnlyList<ProfileSpec> Profiles { get; init; } = Array.Empty<ProfileSpec>();

    /// <summary>Find a profile by name, or null. Matching is case-insensitive.</summary>
    public ProfileSpec? FindProfile(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public sealed class HomeDirSection
    {
        /// <summary>Environment variable holding the directory on this machine, e.g. "APPDATA".</summary>
        public string Root { get; init; } = string.Empty;

        /// <summary>Path relative to that variable, e.g. "Code" or ".config".</summary>
        public required string Sub { get; init; }

        /// <summary>Folder on the stick, relative to the stick root. Defaults to "home/&lt;sub&gt;".</summary>
        public string? Store { get; init; }

        /// <summary>Human-readable owner, e.g. "VSCode".</summary>
        public string? For { get; init; }
    }
}

/// <summary>One environment variable to set at apply time.</summary>
public sealed class EnvVarSpec
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// A named set of environment variables that is never written to the registry.
/// Declared as [[profile]] in a tool manifest, where {{TOOL}} expands to that tool.
/// </summary>
public sealed class ProfileSpec
{
    public required string Name { get; init; }

    /// <summary>Id of the tool that declared the profile; drives {{TOOL}} expansion.</summary>
    public string? ToolId { get; init; }

    public IReadOnlyList<EnvVarSpec> Env { get; init; } = Array.Empty<EnvVarSpec>();

    public string? Notes { get; init; }
}

/// <summary>
/// One generated launcher at the stick root (<c>&lt;Name&gt;.cmd</c>), declared as
/// <c>[[launch]]</c> in a tool manifest.
/// </summary>
public sealed class LaunchSpec
{
    /// <summary>File name without the .cmd extension, e.g. "Tabby".</summary>
    public required string Name { get; init; }

    /// <summary>Executable to start, relative to the tool directory.</summary>
    public required string Exe { get; init; }

    /// <summary>Working directory, relative to the tool directory. "." = the tool dir.</summary>
    public string Dir { get; init; } = ".";

    /// <summary>
    /// Arguments. <c>{{STICK}}</c> renders as <c>%~dp0</c> (the launcher's own folder, i.e.
    /// the stick root), the other placeholders expand like everywhere else.
    /// </summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Extra variables for the launched process only.</summary>
    public IReadOnlyList<EnvVarSpec> Env { get; init; } = Array.Empty<EnvVarSpec>();

    public string? Notes { get; init; }
}

/// <summary>Single tool manifest (manifest.d/*.toml).</summary>
public sealed class ToolManifest
{
    public int Schema { get; init; } = 1;
    public ToolSection Tool { get; init; } = new();

    /// <summary>Directories (relative to the tool root) to put on PATH, in order.</summary>
    public IReadOnlyList<PathEntry> Path { get; init; } = Array.Empty<PathEntry>();

    /// <summary>Environment variables this tool contributes.</summary>
    public IReadOnlyList<EnvVarSpec> Env { get; init; } = Array.Empty<EnvVarSpec>();

    /// <summary>Optional named profiles; see <see cref="ProfileSpec"/>.</summary>
    public IReadOnlyList<ProfileSpec> Profiles { get; init; } = Array.Empty<ProfileSpec>();

    /// <summary>
    /// Launchers to generate at the stick root: <c>[[launch]]</c>.
    ///
    /// A launcher is data, not code: "start this exe from this directory with these
    /// arguments". Keeping it in the manifest means the version-pinned path lives in one
    /// place instead of being copy-pasted into a hand-written .cmd.
    /// </summary>
    public IReadOnlyList<LaunchSpec> Launch { get; init; } = Array.Empty<LaunchSpec>();

    public IReadOnlyList<VerifySpec> Verify { get; init; } = Array.Empty<VerifySpec>();

    /// <summary>Where to look for newer versions. Null means "never check".</summary>
    public UpdateSpec? Update { get; init; }

    /// <summary>Optional scripting hooks. Schema reserved, not executed in P0.</summary>
    public HookSpec? Prepare { get; init; }
    public HookSpec? Post { get; init; }

    public sealed class ToolSection
    {
        public string Id { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string? Title { get; init; }

        /// <summary>zip | tar.gz | 7z-sfx | raw | prebuilt</summary>
        public string Kind { get; init; } = "zip";

        /// <summary>Archive path relative to the stick root. Empty for prebuilt.</summary>
        public string? Archive { get; init; }

        public string? Sha256 { get; init; }

        /// <summary>1 = drop the single top-level directory when extracting.</summary>
        public int Strip { get; init; }

        /// <summary>For kind=raw: the destination file name inside the tool dir.</summary>
        public string? Rename { get; init; }

        /// <summary>For kind=prebuilt: tool dir relative to stick root.</summary>
        public string? Dir { get; init; }

        /// <summary>For prebuilt UIs (VSCode): portable data dir name.</summary>
        public string? Data { get; init; }

        public string? Notes { get; init; }

        /// <summary>Command names this tool claims, used for conflict detection.</summary>
        public IReadOnlyList<string> Provides { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();

        /// <summary>False excludes the tool from path/env application.</summary>
        public bool Enabled { get; init; } = true;
    }

    public sealed class PathEntry
    {
        /// <summary>Directory under the tool root; "." means the root itself.</summary>
        public string Dir { get; init; } = ".";
    }

    public sealed class VerifySpec
    {
        public required string Run { get; init; }
        public string? Expect { get; init; }

        /// <summary>Alternatives: any match succeeds.</summary>
        public IReadOnlyList<string> ExpectAny { get; init; } = Array.Empty<string>();
    }

    public sealed class HookSpec
    {
        public required string Script { get; init; }
        public int TimeoutSeconds { get; init; } = 120;
    }
}
