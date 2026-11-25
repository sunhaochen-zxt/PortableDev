using PortableDev.Core.Logging;
using PortableDev.Core.Protocol;
using PortableDev.Kernel;

var (options, error) = KernelOptions.Parse(args);
if (options is null)
{
    var usage = error?.StartsWith("usage:", StringComparison.Ordinal) == true;
    if (usage)
    {
        Console.WriteLine(error);
        return ExitCodes.Ok;
    }

    Console.Error.WriteLine(error);
    return ExitCodes.Args;
}

await using var host = new KernelHost(options);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    _ = host.RequestShutdownAsync(force: false);
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    _ = host.RequestShutdownAsync(force: false);
};

try
{
    return await host.RunAsync(cancellation.Token);
}
catch (Exception ex)
{
    // A broken settings file or an unexpected startup failure must leave a breadcrumb, not a CLR crash dump.
    BootstrapLog.Write("kernel failed: " + ex);
    return ExitCodes.Internal;
}
