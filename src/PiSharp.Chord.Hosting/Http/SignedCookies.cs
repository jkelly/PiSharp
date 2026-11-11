using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PiSharp.Chord.Hosting.Http;

/// <summary>Server-side HMAC-SHA256 cookies, including a signed expiry and constant-time signature comparison.</summary>
public static class SignedCookies
{
    public sealed record SignedValue(string Value, DateTimeOffset? Expires);
    public static string Create(string name, string value, ReadOnlySpan<byte> secret, bool secure, TimeSpan? maxAge = null, bool lax = false, TimeProvider? clock = null)
    {
        CheckName(name); if (secret.IsEmpty) throw new ArgumentException("a signing secret is required", nameof(secret));
        var expires = maxAge is { } age ? ((clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds() + (long)age.TotalSeconds).ToString(CultureInfo.InvariantCulture) : "";
        var payload = Uri.EscapeDataString(value) + "." + expires;
        var result = $"{name}={payload}.{Sign(name, payload, secret)}; HttpOnly; Path=/; SameSite={(lax ? "Lax" : "Strict")}";
        if (secure) result += "; Secure";
        if (maxAge is { } duration) result += "; Max-Age=" + ((long)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        return result;
    }
    public static string? Read(string cookieHeader, string name, ReadOnlySpan<byte> secret, TimeProvider? clock = null)
        => ReadSigned(cookieHeader, name, secret, clock)?.Value;
    public static SignedValue? ReadSigned(string cookieHeader, string name, ReadOnlySpan<byte> secret, TimeProvider? clock = null)
    {
        CheckName(name); if (secret.IsEmpty) return null;
        foreach (var pair in cookieHeader.Split(';'))
        {
            var equal = pair.IndexOf('='); if (equal < 0 || pair[..equal].Trim() != name) continue;
            var signed = pair[(equal + 1)..].Trim(); var last = signed.LastIndexOf('.'); if (last < 0) return null;
            var payload = signed[..last]; var split = payload.LastIndexOf('.'); if (split < 0) return null;
            var signature = signed[(last + 1)..]; var expected = Sign(name, payload, secret);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(signature), Encoding.ASCII.GetBytes(expected))) return null;
            var expires = payload[(split + 1)..];
            DateTimeOffset? expiry = null;
            if (expires.Length > 0)
            {
                if (!long.TryParse(expires, NumberStyles.None, CultureInfo.InvariantCulture, out var unix) || (clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds() >= unix) return null;
                try { expiry = DateTimeOffset.FromUnixTimeSeconds(unix); } catch (ArgumentOutOfRangeException) { return null; }
            }
            try { return new(Uri.UnescapeDataString(payload[..split]), expiry); } catch (UriFormatException) { return null; }
        }
        return null;
    }
    private static string Sign(string name, string payload, ReadOnlySpan<byte> secret) => Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(name + "=" + payload))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static void CheckName(string name) { if (string.IsNullOrEmpty(name) || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))) throw new ArgumentException("invalid cookie name", nameof(name)); }
}
