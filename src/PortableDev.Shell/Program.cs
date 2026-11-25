using System.Text;
using PortableDev.Core.Identity;
using PortableDev.Core.Protocol;
using PortableDev.Shell;

Console.OutputEncoding = Encoding.UTF8;
StdHandles.DetachInheritance();

var options = ShellOptions.Parse(args);
if (options.Error is not null)
{
    Console.Error.WriteLine(options.Error);
    Console.Error.WriteLine(ShellOptions.Usage);
    return ExitCodes.Args;
}

if (options.ShowUsage)
{
    if (options.Json)
    {
        Console.WriteLine(new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "usage",
            ["text"] = ShellOptions.Usage,
        }.ToJsonString());
    }
    else
    {
        Console.WriteLine(ShellOptions.Usage);
    }

    return ExitCodes.Ok;
}

var (root, locateError) = StickLocator.Locate(options.Root);
if (root is null)
{
    Console.Error.WriteLine(locateError);
    return ExitCodes.Env;
}

var stickId = StickIdentity.LoadOrCreate(root, persist: false).StickId;
var pipeName = StickLocator.PipeName(root, stickId);

using var shell = new PdShell(options, root, pipeName);
using var stop = new CancellationTokenSource();
var presses = 0;
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    var count = Interlocked.Increment(ref presses);
    if (count == 1)
    {
        // Also cancels a connect/auto-start that has no task to cancel yet; once a task exists the
        // cancellation goes through ctl cancel and the token must stay alive for the pipe reads.
        shell.RequestCancel();
        if (!shell.HasTask)
        {
            stop.Cancel();
        }

        return;
    }

    Environment.Exit(ExitCodes.Cancelled);
};

return await shell.RunAsync(stop.Token);
