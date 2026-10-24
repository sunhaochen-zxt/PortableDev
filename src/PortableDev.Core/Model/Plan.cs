using System.Text.Json.Serialization;

namespace PortableDev.Core.Model;

/// <summary>Current state of one home-link slot on this machine.</summary>
public sealed class HomeLinkState
{
    /// <summary>"APPDATA/Code": the env var plus the relative path it is joined with.</summary>
    public required string Display { get; init; }

    /// <summary>The env var or known folder the relative path hangs off.</summary>
    public required string RootVar { get; init; }

    /// <summary>The relative path inside that variable.</summary>
    public required string Sub { get; init; }

    /// <summary>Fully expanded directory on this machine (junction path).</summary>
    public required string HostPath { get; init; }

    /// <summary>Store directory on the stick the link must point at.</summary>
    public required string StorePath { get; init; }

    public bool Exists { get; init; }
    public bool IsLink { get; init; }
    public string? LinkTarget { get; init; }

    /// <summary>True when the host directory is a real directory with contents in it.</summary>
    public bool IsRealWithContent { get; init; }

    /// <summary>Used in reports only, so the user can see what is being redirected.</summary>
    public string? Notes { get; init; }
}

/// <summary>Everything the planner needs to know about the machine right now.</summary>
public sealed class MachineState
{
    /// <summary>Raw HKCU Path value, unexpanded (may contain %VARS%).</summary>
    public string UserPathRaw { get; init; } = string.Empty;

    /// <summary>Registry value kind name: ExpandString | String.</summary>
    public string UserPathKind { get; init; } = "ExpandString";

    /// <summary>Current env var values (expanded) for comparison.</summary>
    public IReadOnlyDictionary<string, string> CurrentEnv { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the stable junction exists.</summary>
    public bool JunctionExists { get; init; }

    /// <summary>Where the stable junction points, when it exists.</summary>
    public string? JunctionTarget { get; init; }

    /// <summary>Resolved portable root (real drive path).</summary>
    public string StickRoot { get; init; } = string.Empty;

    /// <summary>True when running elevated.</summary>
    public bool IsElevated { get; init; }

    /// <summary>One entry per declared home dir, in manifest order.</summary>
    public IReadOnlyList<HomeLinkState> HomeLinks { get; init; } = Array.Empty<HomeLinkState>();
}

/// <summary>Result of planning. Pure data; contains no side effects.</summary>
public sealed class Plan
{
    public string TxId { get; init; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Stable root used by all generated paths.</summary>
    public required string StableRoot { get; init; }

    public required string StickRoot { get; init; }

    /// <summary>True when only path/env are applied, no junction (degraded mode).</summary>
    public bool Degraded { get; init; }

    /// <summary>Where home-link slots point: the stable root, or the stick in degraded mode.</summary>
    public string HomeBase { get; init; } = string.Empty;

    /// <summary>
    /// True when applying this plan needs one UAC prompt (the junction changes and
    /// the current process is not elevated).
    /// </summary>
    public bool NeedsElevation { get; init; }

    public IReadOnlyList<PlanStep> Steps { get; init; } = Array.Empty<PlanStep>();

    public IReadOnlyList<PlanWarning> Warnings { get; init; } = Array.Empty<PlanWarning>();

    /// <summary>New raw HKCU Path value (unexpanded).</summary>
    public required string NewUserPathRaw { get; init; }

    /// <summary>Env vars to write: name -> expanded value.</summary>
    public IReadOnlyDictionary<string, string> EnvToSet { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The home-link slots this plan was built from (needed by the applier).</summary>
    public IReadOnlyList<HomeLinkState> HomeLinks { get; init; } = Array.Empty<HomeLinkState>();

    public bool HasChanges => Steps.Any(s => s.Change != ChangeKind.None);
}

public sealed class PlanStep
{
    public required string Kind { get; init; }
    public required ChangeKind Change { get; init; }
    public required string Target { get; init; }
    public string? Before { get; init; }
    public string? After { get; init; }
}

public sealed class PlanWarning
{
    public required string Code { get; init; }
    public required string Message { get; init; }

    /// <summary>True when this warning blocks apply.</summary>
    public bool Blocker { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeKind
{
    None,
    Add,
    Update,
    Remove,
}
