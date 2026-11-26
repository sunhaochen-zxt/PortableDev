using PortableDev.ProcExt;

if (args.Length >= 2 && string.Equals(args[0], "--admin", StringComparison.Ordinal))
{
    return AdminMode.Run(args[1], args.Length > 2 ? args[2] : null);
}

return await ExtensionClient.RunAsync().ConfigureAwait(false);
