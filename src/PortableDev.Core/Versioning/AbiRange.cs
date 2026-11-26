namespace PortableDev.Core.Versioning;

/// <summary>
/// ABI range grammar from spec 2.4: `>=1`, `>=1 &lt;3`, `==2`.
/// Only integer comparison is supported here — that is exactly what `requiresKernel` means.
/// </summary>
public sealed class AbiRange
{
    private readonly List<(string Op, int Value)> _terms = [];

    private AbiRange()
    {
    }

    /// <summary>The empty range: matches any ABI. Used as the default for `requiresKernel`.</summary>
    public static AbiRange Unbounded { get; } = new();

    public bool IsUnbounded => _terms.Count == 0;

    public static bool TryParse(string? text, out AbiRange range)
    {
        range = new AbiRange();
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var op = token.StartsWith(">=", StringComparison.Ordinal) ? ">="
                : token.StartsWith("<=", StringComparison.Ordinal) ? "<="
                : token.StartsWith("==", StringComparison.Ordinal) ? "=="
                : token.StartsWith("<", StringComparison.Ordinal) ? "<"
                : token.StartsWith(">", StringComparison.Ordinal) ? ">"
                : token.StartsWith("^", StringComparison.Ordinal) ? "^"
                : string.Empty;

            if (op.Length == 0 || !int.TryParse(token[op.Length..], out var value))
            {
                return false;
            }

            // spec 2.4 lists five comparators: ^N and ~N mean "this ABI and nothing newer".
            if (op is "^")
            {
                range._terms.Add((">=", value));
                range._terms.Add(("<", value + 1));
                continue;
            }

            range._terms.Add((op, value));
        }

        return true;
    }

    /// <summary>An unparsable or unsatisfied range is a non-match (spec 2.4: "解析不了 → 视为不匹配").</summary>
    public bool Matches(int abi)
    {
        foreach (var (op, value) in _terms)
        {
            var ok = op switch
            {
                ">=" => abi >= value,
                "<=" => abi <= value,
                "==" => abi == value,
                "<" => abi < value,
                ">" => abi > value,
                _ => false,
            };

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => string.Join(' ', _terms.Select(t => t.Op + t.Value));
}
