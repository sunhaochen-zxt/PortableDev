namespace PortableDev.Kernel;

/// <summary>Command-line options for `pd-kernel.exe`.</summary>
public sealed record KernelOptions
{
    public required string Root { get; init; }

    public required string AppRoot { get; init; }

    public bool RunOnce { get; init; }

    public bool Init { get; init; }

    public bool Verbose { get; init; }

    public string? PipePrefixOverride { get; init; }

    public static (KernelOptions? Options, string? Error) Parse(string[] args)
    {
        var root = Directory.GetCurrentDirectory();
        string? app = null;
        var runOnce = false;
        var init = false;
        var verbose = false;
        string? pipePrefix = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root":
                    if (++i >= args.Length)
                    {
                        return (null, "--root requires a path");
                    }

                    root = args[i];
                    break;
                case "--app":
                    if (++i >= args.Length)
                    {
                        return (null, "--app requires a path");
                    }

                    app = args[i];
                    break;
                case "--pipe-prefix":
                    if (++i >= args.Length)
                    {
                        return (null, "--pipe-prefix requires a value");
                    }

                    pipePrefix = args[i];
                    break;
                case "--once":
                    runOnce = true;
                    break;
                case "--init":
                    init = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--help":
                case "-h":
                    return (null, "usage: pd-kernel --root <stick-root> [--app <dir>] [--once] [--init] [--verbose] [--pipe-prefix <name>]");
                default:
                    return (null, $"unknown argument '{args[i]}'");
            }
        }

        var fullRoot = Path.GetFullPath(root);
        var appRoot = Path.GetFullPath(app ?? Path.Combine(fullRoot, "app"));
        return (new KernelOptions
        {
            Root = fullRoot,
            AppRoot = appRoot,
            RunOnce = runOnce,
            Init = init,
            Verbose = verbose,
            PipePrefixOverride = pipePrefix,
        }, null);
    }
}
