using PortableDev.Core.Protocol;

namespace PortableDev.Shell;

/// <summary>
/// Shell-level switches: connection and presentation only (commands.md section 2).
/// Everything else is forwarded to the kernel verbatim (architecture 3.8).
/// </summary>
public sealed class ShellOptions
{
    public bool Json { get; private set; }

    public bool Quiet { get; private set; }

    public bool NoColor { get; private set; }

    public bool Trace { get; private set; }

    public bool Yes { get; private set; }

    public bool Verbose { get; private set; }

    /// <summary>Debug: dump the environment snapshot to a temp file outside state (kernel.md 7.1).</summary>
    public bool TraceEnv { get; private set; }

    /// <summary>Shell-only: return the child process exit code as our own exit code.</summary>
    public bool Propagate { get; private set; }

    public bool ShowUsage { get; private set; }

    public string? Root { get; private set; }

    public string? RawJson { get; private set; }

    public string? Error { get; private set; }

    public List<string> Forwarded { get; } = [];

    public static string Usage => """
usage: pd [--root <path>] [--json] [--quiet] [--no-color] [--trace] <command> [args...]

  --root <path>   stick root (default: nearest parent with state\stick.json)
  --json          print the event stream as NDJSON
  --quiet         only errors and the final result
  --no-color      disable ANSI colour
  --trace         print protocol frames to stderr
  --trace-env     write the environment snapshot to a temp file (debug)
  --propagate     exit with the child process code (pd run / pd shell)
  --raw <json>    send one raw protocol request (emergency escape hatch)
  --help          show this help; with a command, show that command help

Run "pd help" for the command list (served by the kernel).
""";

    public static ShellOptions Parse(string[] args)
    {
        var options = new ShellOptions();
        var help = false;
        var forwarded = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--root":
                    if (++i >= args.Length)
                    {
                        options.Error = "--root requires a path";
                        return options;
                    }

                    options.Root = args[i];
                    break;

                case "--json":
                    options.Json = true;
                    break;

                case "--no-color":
                    options.NoColor = true;
                    break;

                case "--quiet":
                case "-q":
                    options.Quiet = true;
                    break;

                case "--trace":
                    options.Trace = true;
                    break;

                case "--propagate":
                    options.Propagate = true;
                    break;

                case "--help":
                case "-h":
                    help = true;
                    break;

                case "--raw":
                    if (++i >= args.Length)
                    {
                        options.Error = "--raw requires a JSON payload";
                        return options;
                    }

                    options.RawJson = args[i];
                    break;

                case "--":
                    forwarded.AddRange(args[(i + 1)..]);
                    i = args.Length;
                    break;

                case "--yes":
                case "-y":
                    options.Yes = true;
                    break;

                case "--verbose":
                case "-v":
                    options.Verbose = true;
                    break;

                case "--trace-env":
                    options.TraceEnv = true;
                    break;

                default:
                    forwarded.Add(arg);
                    break;
            }
        }

        var command = forwarded.FirstOrDefault(a => !a.StartsWith("-", StringComparison.Ordinal));
        if (help)
        {
            if (command is null)
            {
                options.ShowUsage = true;
            }
            else
            {
                forwarded.Remove(command);
                forwarded.Insert(0, command);
                forwarded.Insert(0, "help");
                forwarded.RemoveRange(2, forwarded.Count - 2);
            }
        }

        options.Forwarded.AddRange(forwarded);
        return options;
    }
}
