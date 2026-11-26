using PortableDev.ProcExt;

if (args.Length >= 2 && string.Equals(args[0], "--admin", StringComparison.Ordinal))
{
    string? resultPath = null;
    string? expectedSha256 = null;
    for (var i = 2; i < args.Length; i++)
    {
        if (string.Equals(args[i], "--sha256", StringComparison.Ordinal) && i + 1 < args.Length)
        {
            expectedSha256 = args[++i];
        }
        else if (resultPath is null)
        {
            resultPath = args[i];
        }
    }

    return AdminMode.Run(args[1], resultPath, expectedSha256);
}

return await ExtensionClient.RunAsync().ConfigureAwait(false);
