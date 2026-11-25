using PortableDev.ProcExt;

if (args.Length >= 2 && string.Equals(args[0], "--admin", StringComparison.Ordinal))
{
    return AdminMode.Run(args[1]);
}

return await ExtensionClient.RunAsync().ConfigureAwait(false);
