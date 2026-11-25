using System.Security.Cryptography;
using System.Text;

namespace PortableDev.Core.Util;

public static class Sha256Util
{
    public static string HexLower(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string HexLower(string text) => HexLower(Encoding.UTF8.GetBytes(text));

    public static async Task<string> HexLowerAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Short content id used in store paths (16 hex chars, spec 6.1).</summary>
    public static string ShortHex(string text, int length = 16)
    {
        var hex = HexLower(text);
        return hex.Length <= length ? hex : hex[..length];
    }
}
