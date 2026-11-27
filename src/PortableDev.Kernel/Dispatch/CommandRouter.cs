using System.Text.Json.Nodes;
using PortableDev.Core.Extensions;
using PortableDev.Core.Protocol;
using PortableDev.Core.Util;
using PortableDev.Kernel.Dispatch;

namespace PortableDev.Kernel.Dispatch;

/// <summary>
/// Resolves argv to a builtin or an extension command and enforces the single global serialization
/// rule: mutating commands queue, read-only ones run in parallel (spec 2.8 / 4.8).
/// </summary>
public sealed class CommandRouter
{
    private readonly IKernelServices _kernel;
    private readonly BuiltinCommandSet _builtins;
    private readonly SemaphoreSlim _exclusive = new(1, 1);

    public CommandRouter(IKernelServices kernel)
    {
        _kernel = kernel;
        _builtins = new BuiltinCommandSet(kernel);
    }

    public IReadOnlyList<BuiltinCommand> Builtins => _builtins.All;

    private (CommandEntry? Entry, int Matched) ResolveExtensionCommand(string[] argv)
    {
        var limit = Math.Min(argv.Length, 3);
        for (var take = limit; take >= 1; take--)
        {
            var name = string.Join(' ', argv.Take(take));
            if (_kernel.Registry.Commands.TryResolve(name, out var candidate) && candidate.ExtensionName is not null)
            {
                return (candidate, take);
            }
        }

        return (null, 0);
    }

    public async Task<CommandResult> ExecuteAsync(TaskContext ctx)
    {
        if (ctx.Argv.Length == 0)
        {
            return await _builtins.Find("kernel status")!.Handler(ctx, []).ConfigureAwait(false);
        }

        var (builtin, consumed) = _builtins.Resolve(ctx.Argv);
        if (builtin is not null)
        {
            var rest = ctx.Argv[consumed..];
            return await RunSerializedAsync(builtin.Mutates && !builtin.Parallel, ctx, () => builtin.Handler(ctx, rest)).ConfigureAwait(false);
        }

        var head = ctx.Argv[0];
        // Longest-prefix resolution: an extension may register "source list" next to "source", so
        // read-only subcommands can run in parallel while the mutating parent stays exclusive.
        var (entry, _) = ResolveExtensionCommand(ctx.Argv);
        if (entry is not null)
        {
            // spec 4.1: extension commands are exclusive by default; only parallel:true opts out.
            // Extensions still receive argv without the first token, so their own parsing is unchanged.
            return await RunSerializedAsync(!entry.Parallel, ctx,
                () => _kernel.Supervisor.InvokeAsync(ctx, entry, ctx.Argv[1..])).ConfigureAwait(false);
        }

        if (string.Equals(head, "log", StringComparison.Ordinal))
        {
            return new CommandResult(ExitCodes.NotFound, new JsonObject
            {
                ["code"] = ErrorCodes.LogProviderMissing,
                ["message"] = "no log provider is installed",
                ["hint"] = "pd kernel log shows the kernel fallback file",
            }, null, null);
        }

        var suggest = _kernel.Registry.Commands.Suggest(head);
        var hint = suggest is null ? null : $"did you mean '{suggest}'?";
        _kernel.Logger.Debug("cmd.unknown", "unknown command", new JsonObject { ["command"] = head, ["suggestion"] = suggest });
        return new CommandResult(ExitCodes.Args, new JsonObject
        {
            ["code"] = ErrorCodes.UnknownCommand,
            ["message"] = $"unknown command '{head}'",
            ["hint"] = hint,
            ["retryable"] = false,
        }, null, null);
    }

    private async Task<CommandResult> RunSerializedAsync(bool exclusive, TaskContext ctx, Func<Task<CommandResult>> action)
    {
        if (!exclusive)
        {
            return await action().ConfigureAwait(false);
        }

        await _exclusive.WaitAsync(ctx.Token).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _exclusive.Release();
        }
    }
}
