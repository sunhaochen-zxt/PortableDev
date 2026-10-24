namespace PortableDev.Core;

/// <summary>What an undo step reverses.</summary>
public enum UndoKind
{
    /// <summary>One user environment variable.</summary>
    Env,

    /// <summary>The user PATH value.</summary>
    Path,

    /// <summary>A home-link junction that did not exist before this apply.</summary>
    HomeLink,
}

/// <summary>
/// One reversible mutation, recorded immediately BEFORE the write it describes so a
/// failure halfway through a loop can still undo the writes that did happen.
/// </summary>
public sealed class UndoStep
{
    public required UndoKind Kind { get; init; }

    /// <summary>Env var name, the literal "PATH", or the junction path.</summary>
    public required string Target { get; init; }

    /// <summary>
    /// The raw, unexpanded previous value for <see cref="UndoKind.Env"/> and
    /// <see cref="UndoKind.Path"/>. Never the expanded value: writing that back would
    /// bake %USERPROFILE% into the registry.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// False for an env var that did not exist before the write. Such a variable must be
    /// deleted on undo, not set to an empty string.
    /// </summary>
    public bool Existed { get; init; } = true;

    /// <summary>For <see cref="UndoKind.HomeLink"/>: the target the link was given.</summary>
    public string? StorePath { get; init; }
}

/// <summary>
/// Reverse-order undo stack for <c>apply</c>.
///
/// Scope, deliberately: registry writes (environment variables and PATH) plus home links
/// this run created from nothing. The stable junction is NOT part of it - it is idempotent,
/// the next apply wants it anyway, and undoing it needs a second UAC prompt. Do not
/// "complete" this stack with the junction: the extra prompt buys nothing.
///
/// The stack is pure data so the ordering rule is unit-testable; the App layer performs
/// the actual IO.
/// </summary>
public sealed class UndoStack
{
    private readonly List<UndoStep> _steps = new();

    public int Count => _steps.Count;

    public void Push(UndoStep step) => _steps.Add(step);

    /// <summary>The steps in the order they must be undone: the last mutation first.</summary>
    public IReadOnlyList<UndoStep> Reverse()
    {
        var copy = new List<UndoStep>(_steps);
        copy.Reverse();
        return copy;
    }
}
