using System.Text.Json;

namespace PortableDev.App;

/// <summary>
/// The privileged half of the elevation broker. This runs in a separate, short-lived
/// process that was launched through the UAC prompt, performs exactly one mechanical
/// filesystem operation, writes its outcome to a result file and exits.
///
/// It deliberately does NOT load the manifest, resolve the stick or touch the
/// registry: those need no administrator rights, and keeping the elevated surface
/// as small as possible means a bug here cannot corrupt PATH or environment.
/// </summary>
public static class ElevatedJob
{
    public const string JunctionCreate = "junction-create";
    public const string JunctionRemove = "junction-remove";

    public static int Run(CommandOptions opts)
    {
        var resultFile = opts.ResultFile;

        if (resultFile is null)
        {
            Console.Error.WriteLine("--elevated-job requires --result <file>");
            return 2;
        }

        JobResult result;
        int exit;

        try
        {
            switch (opts.JobKind)
            {
                case JunctionCreate:
                    exit = DoJunctionCreate(opts, out result);
                    break;

                case JunctionRemove:
                    exit = DoJunctionRemove(opts, out result);
                    break;

                default:
                    result = JobResult.Failed(opts.JobKind, $"--elevated-job needs --job {JunctionCreate}|{JunctionRemove}");
                    exit = 2;
                    break;
            }
        }
        catch (Exception ex)
        {
            result = JobResult.Failed(opts.JobKind, ex.Message);
            exit = 1;
        }

        TryWrite(resultFile, result);
        return exit;
    }

    private static int DoJunctionCreate(CommandOptions opts, out JobResult result)
    {
        if (string.IsNullOrWhiteSpace(opts.Link) || string.IsNullOrWhiteSpace(opts.Target))
        {
            result = JobResult.Failed(JunctionCreate, "--link and --target are both required");
            return 2;
        }

        var (ok, err) = Win.Junction.Create(opts.Link!, opts.Target!);
        result = new JobResult
        {
            Ok = ok,
            Job = JunctionCreate,
            Link = opts.Link,
            Target = opts.Target,
            Error = err,
        };

        return ok ? 0 : 7;
    }

    private static int DoJunctionRemove(CommandOptions opts, out JobResult result)
    {
        if (string.IsNullOrWhiteSpace(opts.Link))
        {
            result = JobResult.Failed(JunctionRemove, "--link is required");
            return 2;
        }

        var (ok, err) = Win.Junction.Remove(opts.Link!);
        result = new JobResult
        {
            Ok = ok,
            Job = JunctionRemove,
            Link = opts.Link,
            Error = err,
        };

        return ok ? 0 : 7;
    }

    private static void TryWrite(string path, JobResult result)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (Exception ex)
        {
            // The parent reports a missing result file; make the cause visible too.
            Console.Error.WriteLine("could not write result file: " + ex.Message);
        }
    }

    public sealed class JobResult
    {
        public bool Ok { get; init; }
        public string? Job { get; init; }
        public string? Link { get; init; }
        public string? Target { get; init; }
        public string? Error { get; init; }
        public string Ts { get; init; } = DateTimeOffset.Now.ToString("O");

        public static JobResult Failed(string? job, string error) => new()
        {
            Ok = false,
            Job = job,
            Error = error,
        };
    }

    /// <summary>Reads a result file written by the elevated helper, or null when absent/unreadable.</summary>
    public static JobResult? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<JobResult>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
