namespace PortableDev.Core;

/// <summary>One directory on some volume that carries a portable-dev root marker.</summary>
public sealed record StickCandidate(string Root, string? Id);

/// <summary>Outcome of choosing between the candidates that were found.</summary>
public sealed record StickSelectionResult(
    string? Root,
    int Count,
    bool Ambiguous,
    IReadOnlyList<StickCandidate> Candidates,
    string? Error)
{
    public bool Resolved => Root is not null;
}

/// <summary>
/// Decides which stick to use. Pure, so the decision can be tested without any
/// volume being plugged in.
///
/// The rule is deliberately strict: when more than one root is present, selection
/// fails instead of silently picking the first one. Two sticks can hold different
/// manifests, and applying the wrong one rewrites the machine's PATH to point at
/// toolchains that are not there — a mistake the user cannot see coming, because
/// the drive letter order is whatever Windows happened to assign.
/// </summary>
public static class StickSelection
{
    /// <summary>
    /// Pick a root from the discovered candidates.
    ///
    /// <paramref name="explicitRoot"/> is honoured verbatim when it matches a
    /// candidate; the marker check for an explicit path is the caller's business
    /// (it may point at a folder the scan never looks inside).
    /// </summary>
    public static StickSelectionResult Select(
        IReadOnlyList<StickCandidate> candidates, string? explicitRoot = null)
    {
        candidates ??= Array.Empty<StickCandidate>();

        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var wanted = Normalize(explicitRoot!);
            var hit = candidates.FirstOrDefault(c =>
                string.Equals(Normalize(c.Root), wanted, StringComparison.OrdinalIgnoreCase));

            return hit is null
                ? new(null, candidates.Count, false, candidates,
                    $"'{Normalize(explicitRoot!)}' was not among the discovered roots")
                : new(hit.Root, candidates.Count, false, candidates, null);
        }

        if (candidates.Count == 0)
            return new(null, 0, false, candidates, "no portable-dev root found");

        if (candidates.Count == 1)
            return new(candidates[0].Root, 1, false, candidates, null);

        // Ambiguous. Name every candidate so the message tells the user which
        // --root to pass, instead of making them go and look.
        var list = string.Join(", ", candidates.Select(c =>
            c.Root + (string.IsNullOrWhiteSpace(c.Id) ? string.Empty : $" (id {c.Id})")));

        return new(null, candidates.Count, true, candidates,
            $"{candidates.Count} portable-dev roots found: {list}. Pass --root <path> to choose one.");
    }

    /// <summary>Compare roots regardless of trailing separators: "E:\" and "E:" are one place.</summary>
    public static string Normalize(string root) =>
        (root ?? string.Empty).Trim().TrimEnd('\\', '/');
}
