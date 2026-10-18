// PiSharp-only (decision 0008). A device token is a shell: whoever holds it runs commands as the device's user. Tokens are `dpi_` plus the
// base64url of 32 random bytes (durable-pi's format), and the server compares SHA-256 hashes in constant time, so neither the token's
// length nor its prefix leaks through timing.
using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Durable.Server;

/// <summary>Device token generation and checks.</summary>
public static class DeviceTokens
{
    /// <summary>The shortest token a device accepts.</summary>
    public const int MinimumLength = 32;

    /// <summary>A new token: <c>dpi_</c> and 43 base64url characters.</summary>
    public static string Generate() => "dpi_" + Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The token's SHA-256 hash.</summary>
    public static byte[] Hash(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>Whether <paramref name="presented"/> is the token whose hash is <paramref name="expectedHash"/>, in constant time.</summary>
    public static bool Matches(string? presented, ReadOnlySpan<byte> expectedHash) =>
        presented is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Hash(presented), expectedHash);

    /// <summary>Whether a token is long enough to use.</summary>
    public static bool IsStrong(string? token) => token is { Length: >= MinimumLength } && !token.Any(char.IsWhiteSpace);

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
