namespace PortableDev.Core;

/// <summary>
/// Builds Windows command lines using the same quoting rules the CRT and
/// CommandLineToArgvW expect. Needed whenever this program re-launches itself
/// (the elevated helper), because a path with a space or a trailing backslash
/// silently turns into the wrong argument otherwise.
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Quote a single argument. Arguments that a Windows command line parser would
    /// read back unchanged are returned as-is, so command lines stay readable.
    /// A trailing backslash always needs quoting, because the parser would
    /// otherwise treat it as escaping the closing quote.
    /// </summary>
    public static string Quote(string value)
    {
        var needsQuotes =
            value.Length == 0 ||
            value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) >= 0 ||
            value.EndsWith('\\');

        if (!needsQuotes)
            return value;

        var sb = new System.Text.StringBuilder(value.Length + 8);
        sb.Append('"');

        var backslashes = 0;
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    backslashes++;
                    break;

                case '"':
                    // Backslashes before a quote must be doubled, plus one to
                    // escape the quote itself:  \"  ->  \\\"
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    break;

                default:
                    if (backslashes > 0)
                    {
                        sb.Append('\\', backslashes);
                        backslashes = 0;
                    }
                    sb.Append(c);
                    break;
            }
        }

        // Trailing backslashes are doubled so they do not escape the closing quote.
        if (backslashes > 0)
            sb.Append('\\', backslashes * 2);

        sb.Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> arguments)
        => string.Join(" ", arguments.Select(Quote));

    public static string Join(params string[] arguments) => Join((IEnumerable<string>)arguments);

    /// <summary>
    /// Split a command line back into tokens, the inverse of <see cref="Join"/>.
    ///
    /// Needed for <c>[[verify]].run</c>, which is written as one string in the manifest
    /// but has to reach the child process as separate arguments. Splitting on the first
    /// space — the previous behaviour — breaks on any executable whose path contains a
    /// space (<c>"C:\Program Files\x.exe" --version</c> became the program
    /// <c>"C:\Program</c>) and hands a .cmd script one giant argument instead of several.
    ///
    /// Quoting follows the same rules the CRT uses: a quote toggles quoted mode, and
    /// <c>\"</c> is a literal quote. Deliberately no support for the exotic
    /// "backslashes before a closing quote" cases — a verify command that needs them is
    /// better written as a script.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(commandLine)) return tokens;

        var s = commandLine;
        var i = 0;

        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;

            var sb = new System.Text.StringBuilder();
            var started = false;
            var inQuotes = false;

            while (i < s.Length)
            {
                var c = s[i];

                if (c == '"')
                {
                    // A quote only toggles quoting; it is not part of the value. An empty
                    // "" is a real (empty) argument, so mark the token started.
                    inQuotes = !inQuotes;
                    started = true;
                    i++;
                    continue;
                }

                if (c == '\\' && i + 1 < s.Length && s[i + 1] == '"')
                {
                    sb.Append('"');
                    i += 2;
                    started = true;
                    continue;
                }

                // Whitespace separates arguments only outside quotes; inside them it is
                // part of the argument ("C:\Program Files\x.exe", "import ssl,sqlite3").
                if (char.IsWhiteSpace(c) && !inQuotes) break;

                sb.Append(c);
                i++;
                started = true;
            }

            if (started || sb.Length > 0) tokens.Add(sb.ToString());
        }

        return tokens;
    }
}
