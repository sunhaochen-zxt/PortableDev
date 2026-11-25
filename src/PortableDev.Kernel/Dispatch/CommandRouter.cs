using System.Text.Json.Nodes;
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
        if (_kernel.Registry.Commands.TryResolve(head, out var entry) && entry.ExtensionName is not null)
        {
            return await RunSerializedAsync(entry.Mutates && !entry.Parallel, ctx,
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
