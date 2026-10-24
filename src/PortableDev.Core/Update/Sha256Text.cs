using System.Text.RegularExpressions;

namespace PortableDev.Core.Update;

/// <summary>
/// Extracts a SHA256 out of a checksum sidecar file.
///
/// The common formats are "&lt;hash&gt;  &lt;filename&gt;" (sha256sum), a bare hash, and
/// "SHA256 (name) = &lt;hash&gt;". All of them contain exactly one 64-hex-digit token, so
/// the token is what is parsed instead of a format.
/// </summary>
public static class Sha256Text
{
    private static readonly Regex Hex64 = new(
        @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])",
        RegexOptions.Compiled);

    /// <summary>
    /// The first 64-hex-digit token, lower-cased, or null when the body has none.
    /// Returning null on anything unexpected is deliberate: a guessed hash would be worse
    /// than no check at all.
    /// </summary>
    public static string? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var m = Hex64.Match(body);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }
}
