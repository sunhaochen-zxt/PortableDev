using PortableDev.Win;

namespace PortableDev.App;

public sealed record BrokerOutcome(
    bool Ok,
    bool Cancelled,
    string? Error,
    int ExitCode,
    ElevatedJob.JobResult? Result);

/// <summary>
/// Parent side of the elevation broker: re-launches this program through UAC with
/// <c>--elevated-job</c> and reads back the outcome. Small on purpose.
/// </summary>
public static class Broker
{
    public static BrokerOutcome RunJunctionJob(string job, string link, string? target, string tmpDir)
    {
        if (Elevation.IsElevated())
            return new BrokerOutcome(false, false,
                "already elevated; run the step inline instead of brokering it", -1, null);

        if (string.IsNullOrEmpty(SelfInvocation.File))
            return new BrokerOutcome(false, false, "cannot determine this program's own path", -1, null);

        string resultFile;
        try
        {
            Directory.CreateDirectory(tmpDir);
            resultFile = Path.Combine(tmpDir, $"job-{Guid.NewGuid():N}.json");
        }
        catch (Exception ex)
        {
            return new BrokerOutcome(false, false, "cannot create the job folder under the stick: " + ex.Message, -1, null);
        }

        var args = new List<string>
        {
            "--elevated-job",
            "--job", job,
            "--link", link,
            "--result", resultFile,
        };

        if (target is not null)
        {
            args.Add("--target");
            args.Add(target);
        }

        var run = Elevation.RunElevated(SelfInvocation.File, SelfInvocation.BuildArguments(args.ToArray()));

        var parsed = ElevatedJob.Read(resultFile);
        try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { /* best effort */ }

        var ok = parsed?.Ok ?? false;
        var error = parsed?.Error;

        if (parsed is null)
        {
            // The helper never got far enough to write a result. The usual cause is
            // that it could not start at all, which for a framework-dependent build
            // means it could not find .NET: an elevated child does NOT inherit the
            // caller's process-local environment, so DOTNET_ROOT set in a shell does
            // not reach it. Shipping builds are self-contained, so this is a
            // development-only failure mode, but it must stay diagnosable.
            error = run.Cancelled
                ? "the UAC prompt was declined, so nothing was changed"
                : $"the elevated helper did not start (exit 0x{unchecked((uint)run.ExitCode):X8}). " +
                  "If you are running a development build, use the self-contained build from the " +
                  "stick's app folder: an elevated child does not inherit DOTNET_ROOT.";
        }
        else if (ok && parsed.Error is not null)
        {
            error = parsed.Error;
        }

        return new BrokerOutcome(ok, run.Cancelled, error, run.ExitCode, parsed);
    }

    /// <summary>Independent check that the junction really is what we asked for.</summary>
    public static bool JunctionMatches(string link, string target)
        => Junction.IsReparsePoint(link) &&
           PortableDev.Core.PathEditor.PathsEqual(Junction.GetTarget(link) ?? string.Empty, target);
}
