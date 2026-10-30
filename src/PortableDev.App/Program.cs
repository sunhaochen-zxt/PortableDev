using PortableDev.Core;
using PortableDev.Core.Model;
using PortableDev.Win;

namespace PortableDev.App;

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            return Run(args);
        }
        catch (ManifestException ex)
        {
            Console.Error.WriteLine("manifest error:");
            foreach (var e in ex.Errors) Console.Error.WriteLine("  " + e);
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }

        var cmd = args[0].ToLowerInvariant();
        var opts = CommandOptions.Parse(args.Skip(1).ToArray());

        if (opts.Errors.Count > 0)
        {
            foreach (var e in opts.Errors) Console.Error.WriteLine("error: " + e);
            return 2;
        }

        // A misspelt switch used to land in Positional and be ignored, so
        // "apply --no-juntion" wrote the junction anyway. "shell" is exempt: its tail
        // belongs to the terminal it opens, not to this program.
        if (opts.Unknown.Count > 0 && !cmd.Equals("shell", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("unknown option: " + string.Join(", ", opts.Unknown));
            Usage();
            return 2;
        }

        return cmd switch
        {
            "plan" or "--dry-run" => RunPlan(opts),
            "apply" => RunApply(opts),
            "verify" => RunVerify(opts),
            "doctor" => RunDoctor(opts),
            "clean" => RunClean(opts),
            "locate" => RunLocate(opts),
            "gui" => RunGui(opts),
            "shell" => ShellLauncher.Run(opts),
            "update" => UpdateCommand.Run(opts),
            "--elevated-job" => RunElevatedJob(opts),
            _ => Unknown(cmd),
        };
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"unknown command: {cmd}");
        Usage();
        return 2;
    }

    private static void Usage()
    {
        Console.WriteLine("""
            PortableDev - portable development environment manager

            Usage:
              PortableDev <command> [options]

            Commands:
              plan      Show what would change (no side effects)
              apply     Execute the plan
              verify    Run every declared tool verification
              doctor    Diagnose stick, junction, PATH and tools
              clean     Undo apply: junction, home links, env vars and PATH entries
              locate    Print the detected stick root and exit
              gui       Launch the graphical interface
              update    Ask upstream for newer versions (read-only)
              shell     Open a terminal that already has the environment
                        (works even when Explorer has not refreshed its PATH)

            Options:
              --json              Machine-readable output (plan/doctor/verify)
              --yes               Do not prompt for confirmation
              --root <path>       Use this stick root instead of auto-detecting
              --stable <path>     Override the stable junction path (default C:\Dev)
              --no-junction       Degraded mode: no junction, PATH uses the real path
              --no-elevate        Never prompt for UAC; fail with exit code 8 instead
              --test-registry     Use HKCU\Environment-Test instead of the live key
              --strict            Treat missing tools as a blocker
              --adopt-home        Move existing home directories onto the stick
              --no-adopt-home     Never move existing home directories
              --no-rollback       Skip the automatic undo when apply fails
                                  (default: restore the previous registry values)
              --fetch             With update: download the new packages too
              --profile <name>    With shell: also set that profile's variables
                                  (declared as [[profile]] in a tool manifest)
              --trace             Print the computed PATH/env, change nothing
              --dry-run           Alias for plan

            Exit codes:
              0 ok   1 failed   2 bad command line   3 manifest error
              4 stick not found (or ambiguous)   5 blocked by a warning
              6 not confirmed   7 junction failed   8 elevation declined or unavailable
              9 home link failed
            """);
    }

    /// <summary>
    /// Launch the WPF front-end. It is a sibling executable so the GUI keeps its own
    /// lifetime: the console app stays the single write path (apply/clean go through
    /// it), and the GUI remains optional.
    /// </summary>
    private static int RunGui(CommandOptions opts)
    {
        var here = AppContext.BaseDirectory;
        var gui = new[]
        {
            Path.Combine(here, "PortableDev.Gui.exe"),
            Path.Combine(here, "gui", "PortableDev.Gui.exe"),
            Path.Combine(here, "..", "app", "PortableDev.Gui.exe"),
        }.Select(Path.GetFullPath).FirstOrDefault(File.Exists);

        if (gui is null)
        {
            Console.Error.WriteLine("gui not found: build/publish PortableDev.Gui next to this executable.");
            Console.Error.WriteLine("The command line still works: PortableDev plan | apply | verify | doctor");
            return 4;
        }

        // UseShellExecute must be true: with it false the GUI inherits this console's
        // stdio handles, so the caller's pipeline blocks until the window is closed.
        // Shell execution detaches the child cleanly.
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = gui,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(gui)!,
            // Without an explicit normal window state the child inherits this
            // process's STARTUPINFO, which may be SW_HIDE (console-less parent) and
            // leaves the window parked at -32000,-32000, i.e. invisible.
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal,
        };
        if (!string.IsNullOrEmpty(opts.Root)) psi.ArgumentList.Add("--root");
        if (!string.IsNullOrEmpty(opts.Root)) psi.ArgumentList.Add(opts.Root);

        using var p = System.Diagnostics.Process.Start(psi);
        return p is null ? 1 : 0;
    }

    private static (string Stick, RootManifest Manifest, PathResolver Resolver)? Prepare(CommandOptions opts)
    {
        var root = StickLocator.Resolve(opts);
        if (root is null) return null;

        var manifest = ManifestLoader.Load(root);
        var stable = opts.Stable ?? manifest.Root.Stable;
        var resolver = new PathResolver(stable, root);
        return (root, manifest, resolver);
    }

    private static int RunLocate(CommandOptions opts)
    {
        var (root, _, error) = StickLocator.Probe(opts);
        if (root is null)
        {
            Console.Error.WriteLine(error ?? "not found");
            StickLocator.WriteCandidates();
            return 4;
        }

        Console.WriteLine($"root   : {root}");
        Console.WriteLine($"label  : {VolumeLocator.LabelOf(root)}");
        Console.WriteLine($"marker : {Path.Combine(root, VolumeLocator.MarkerFileName)}");

        // Naming the other sticks matters: apply rewrites PATH from whichever root
        // wins, and "the first drive letter" is not a decision anyone should make
        // implicitly.
        var others = VolumeLocator.Candidates()
            .Where(c => !PathEditor.PathsEqual(c.Root, root))
            .ToList();

        if (others.Count > 0)
        {
            Console.WriteLine($"warning: {others.Count} other root(s) present; this run used " +
                              (string.IsNullOrWhiteSpace(opts.Root) ? "auto-detection" : "--root"));
            foreach (var c in others)
                Console.WriteLine($"         other: {c.Root}   id={c.Id ?? "?"}");
        }

        return 0;
    }

    private static int RunPlan(CommandOptions opts)
    {
        var prepared = Prepare(opts);
        if (prepared is null) return 4;
        var (stick, manifest, resolver) = prepared.Value;

        var state = MachineStateReader.Read(stick, resolver.StableRoot, opts.TestRegistry,
            opts.NoJunction ? stick : null);
        var plan = BuildPlan(manifest, resolver, state, opts, stick);

        if (opts.Json) PlanRenderer.WriteJson(plan, Console.Out);
        else PlanRenderer.WriteText(plan, Console.Out);

        return plan.Warnings.Any(w => w.Blocker) ? 5 : 0;
    }

    internal static Plan BuildPlan(RootManifest manifest, PathResolver resolver, MachineState state, CommandOptions opts, string stickRoot)
    {
        // Home links are inspected here, not in MachineStateReader, so that a caller
        // holding only the manifest (tests, the GUI) still gets the same plan.
        if (state.HomeLinks.Count == 0)
        {
            state = new MachineState
            {
                UserPathRaw = state.UserPathRaw,
                UserPathKind = state.UserPathKind,
                CurrentEnv = state.CurrentEnv,
                JunctionExists = state.JunctionExists,
                JunctionTarget = state.JunctionTarget,
                StickRoot = state.StickRoot,
                IsElevated = state.IsElevated,
                HomeLinks = MachineStateReader.ReadHomeLinks(stickRoot, resolver.StableRoot,
                    opts.NoJunction ? stickRoot : null),
            };
        }

        var planner = new Planner(manifest, resolver);
        return planner.Build(state, new PlannerOptions
        {
            NoJunction = opts.NoJunction,
            TreatMissingToolsAsBlocker = opts.TreatMissingToolsAsBlocker,
            HomeAdopt = opts.HomeAdopt ?? manifest.Apply.HomeAdopt,
            DirectoryExists = Directory.Exists,
            FileText = p => File.Exists(p) ? File.ReadAllText(p) : null,
            EnvironmentVariable = Environment.GetEnvironmentVariable,
        });
    }

    private static int RunApply(CommandOptions opts) => Applier.Run(opts);
    private static int RunVerify(CommandOptions opts) => Verifier.Run(opts);
    private static int RunDoctor(CommandOptions opts) => Doctor.Run(opts);
    private static int RunClean(CommandOptions opts) => Applier.Clean(opts);
    private static int RunElevatedJob(CommandOptions opts) => Applier.RunElevatedJob(opts);
}

/// <summary>Parsed command line options.</summary>
public sealed class CommandOptions
{
    public bool Json { get; private set; }
    public bool Yes { get; private set; }
    public bool TestRegistry { get; private set; }
    public bool NoJunction { get; private set; }
    public bool NoElevate { get; private set; }
    public bool TreatMissingToolsAsBlocker { get; private set; }
    public bool Trace { get; private set; }

    /// <summary>--fetch: download updates but do not install them.</summary>
    public bool Fetch { get; private set; }

    /// <summary>--adopt-home / --no-adopt-home: override [apply].home_adopt.</summary>
    public bool? HomeAdopt { get; private set; }

    /// <summary>
    /// --no-rollback: leave a half-applied environment in place when a step fails.
    /// Off by default, because "PATH says the tools are here and they are not" is a
    /// worse place to end up than "nothing changed, here is the error".
    /// </summary>
    public bool NoRollback { get; private set; }

    /// <summary>--profile: with "shell", the named env profile to add to that terminal.</summary>
    public string? Profile { get; private set; }

    public string? Root { get; private set; }
    public string? Stable { get; private set; }

    /// <summary>--elevated-job kind: junction-create | junction-remove.</summary>
    public string? JobKind { get; private set; }

    /// <summary>--link: the stable junction path the elevated helper must write.</summary>
    public string? Link { get; private set; }

    /// <summary>--target: where the junction must point (stick root).</summary>
    public string? Target { get; private set; }

    /// <summary>--result: file the elevated helper writes its outcome to.</summary>
    public string? ResultFile { get; private set; }

    public List<string> Positional { get; } = new();

    /// <summary>
    /// Tokens that look like a switch but match nothing. Collected instead of being
    /// dropped into <see cref="Positional"/>, so a typo fails loudly.
    /// </summary>
    public List<string> Unknown { get; } = new();

    /// <summary>Structural problems, e.g. "--root" with no value after it.</summary>
    public List<string> Errors { get; } = new();

    public static CommandOptions Parse(string[] args)
    {
        var o = new CommandOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--json": o.Json = true; break;
                case "--yes": case "-y": o.Yes = true; break;
                case "--test-registry": o.TestRegistry = true; break;
                case "--no-junction": o.NoJunction = true; break;
                case "--no-elevate": o.NoElevate = true; break;
                case "--strict": o.TreatMissingToolsAsBlocker = true; break;
                case "--trace": o.Trace = true; break;
                case "--fetch": o.Fetch = true; break;
                case "--adopt-home": o.HomeAdopt = true; break;
                case "--no-adopt-home": o.HomeAdopt = false; break;
                case "--no-rollback": o.NoRollback = true; break;
                case "--root":
                    o.Root = Value(args, ref i, a, o);
                    break;
                case "--profile":
                    o.Profile = Value(args, ref i, a, o);
                    break;
                case "--stable":
                    o.Stable = Value(args, ref i, a, o);
                    break;
                case "--job":
                    o.JobKind = Value(args, ref i, a, o);
                    break;
                case "--link":
                    o.Link = Value(args, ref i, a, o);
                    break;
                case "--target":
                    o.Target = Value(args, ref i, a, o);
                    break;
                case "--result":
                    o.ResultFile = Value(args, ref i, a, o);
                    break;
                default:
                    if (a.StartsWith("--root=", StringComparison.Ordinal)) o.Root = a["--root=".Length..];
                    else if (a.StartsWith("--profile=", StringComparison.Ordinal)) o.Profile = a["--profile=".Length..];
                    else if (a.StartsWith("--stable=", StringComparison.Ordinal)) o.Stable = a["--stable=".Length..];
                    else if (a.StartsWith("--job=", StringComparison.Ordinal)) o.JobKind = a["--job=".Length..];
                    else if (a.StartsWith("--link=", StringComparison.Ordinal)) o.Link = a["--link=".Length..];
                    else if (a.StartsWith("--target=", StringComparison.Ordinal)) o.Target = a["--target=".Length..];
                    else if (a.StartsWith("--result=", StringComparison.Ordinal)) o.ResultFile = a["--result=".Length..];
                    else if (a.Length > 1 && a[0] == '-') o.Unknown.Add(a);
                    else o.Positional.Add(a);
                    break;
            }
        }
        return o;
    }

    /// <summary>Consume the value of "--flag value"; record an error when it is missing.</summary>
    private static string? Value(string[] args, ref int i, string flag, CommandOptions o)
    {
        if (i + 1 >= args.Length)
        {
            o.Errors.Add($"{flag} needs a value");
            return null;
        }
        return args[++i];
    }
}
