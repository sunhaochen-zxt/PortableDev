using PortableDev.Win;

namespace PortableDev.App;

/// <summary>
/// Resolves the stick for a console command and explains a failure on stderr.
///
/// Every command needs the same answer and the same wording, and "multiple sticks
/// found" used to be a branch no caller could reach: the locator picked the first
/// volume and reported a count nobody acted on. Centralising it here means the
/// ambiguity is surfaced once, identically, everywhere.
/// </summary>
internal static class StickLocator
{
    /// <summary>The stick root, or null after printing why it could not be determined.</summary>
    public static string? Resolve(CommandOptions opts)
    {
        var (root, _, error) = VolumeLocator.ResolveDetailed(opts.Root);
        if (root is not null) return root;

        Console.Error.WriteLine(error ?? "stick not found");
        WriteCandidates();
        return null;
    }

    /// <summary>The same decision without printing, for callers that render their own report.</summary>
    public static (string? Root, int Count, string? Error) Probe(CommandOptions opts) =>
        VolumeLocator.ResolveDetailed(opts.Root);

    /// <summary>List what is plugged in, so the user can copy a --root value.</summary>
    public static void WriteCandidates()
    {
        foreach (var c in VolumeLocator.Candidates())
        {
            var label = VolumeLocator.LabelOf(c.Root);
            Console.Error.WriteLine(
                $"  candidate: {c.Root}" +
                $"   id={c.Id ?? "?"}" +
                $"   label={(string.IsNullOrWhiteSpace(label) ? "?" : label)}");
        }
    }
}
