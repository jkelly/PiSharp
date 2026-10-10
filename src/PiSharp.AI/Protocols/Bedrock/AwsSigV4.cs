// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/bedrock-converse-stream.ts (SigV4 through the AWS SDK's
// default signer). Native port of the AWS Signature Version 4 algorithm as @smithy/signature-v4 applies it: path normalization and
// double URI escaping for non-S3 services, sorted escaped query, trimmed lower-cased headers, x-amz-content-sha256 (applyChecksum).
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PiSharp.AI.Protocols.Bedrock;

/// <summary>Static AWS credentials. ToString never reveals the secret or session token.</summary>
public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken = null, DateTimeOffset? Expiration = null)
{
    /// <summary>The provider that produced these credentials (status display only).</summary>
    public string? Source { get; init; }
    public override string ToString() => $"AwsCredentials ({Source ?? "unknown"}) [redacted]";
}

/// <summary>One request to sign. Path is the request path as sent (already escaped segments stay escaped; the canonical path
/// escapes them again, as the SDK does for every service except S3). Query values are unescaped.</summary>
public sealed record AwsSigningRequest(string Method, string Host, string Path,
    IReadOnlyList<KeyValuePair<string, string>> Query, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Body);

public sealed record AwsSignature(string CanonicalRequest, string StringToSign, string Signature, string Authorization,
    IReadOnlyList<KeyValuePair<string, string>> AddedHeaders);

public static class AwsSigV4
{
    public const string Algorithm = "AWS4-HMAC-SHA256";
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    // @smithy/signature-v4 ALWAYS_UNSIGNABLE_HEADERS.
    private static readonly HashSet<string> Unsignable = new(StringComparer.Ordinal)
    {
        "authorization", "cache-control", "connection", "expect", "from", "keep-alive", "max-forwards", "pragma", "referer", "te",
        "trailer", "transfer-encoding", "upgrade", "user-agent", "x-amzn-trace-id"
    };

    /// <summary>Signs a request. <paramref name="applyChecksum"/> adds x-amz-content-sha256 (the SDK default for Bedrock).</summary>
    public static AwsSignature Sign(AwsSigningRequest request, AwsCredentials credentials, string region, string service,
        DateTimeOffset time, bool applyChecksum = true, bool normalizePath = true)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(credentials);
        ArgumentException.ThrowIfNullOrEmpty(region); ArgumentException.ThrowIfNullOrEmpty(service);
        var longDate = time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var shortDate = longDate[..8];
        var added = new List<KeyValuePair<string, string>> { new("x-amz-date", longDate) };
        if (!string.IsNullOrEmpty(credentials.SessionToken)) added.Add(new("x-amz-security-token", credentials.SessionToken));
        var payloadHash = Convert.ToHexStringLower(SHA256.HashData(request.Body ?? []));
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Put(string name, string value)
        {
            var lower = name.ToLowerInvariant();
            if (Unsignable.Contains(lower)) return;
            var normalized = CollapseWhitespace(value.Trim());
            headers[lower] = headers.TryGetValue(lower, out var existing) ? existing + "," + normalized : normalized;
        }
        var hasHost = false; var hasChecksum = false;
        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("host", StringComparison.OrdinalIgnoreCase)) hasHost = true;
            if (name.Equals("x-amz-content-sha256", StringComparison.OrdinalIgnoreCase)) hasChecksum = true;
            if (name.Equals("x-amz-date", StringComparison.OrdinalIgnoreCase) || name.Equals("x-amz-security-token", StringComparison.OrdinalIgnoreCase)) continue;
            Put(name, value);
        }
        if (!hasHost) Put("host", request.Host);
        if (applyChecksum && !hasChecksum) { added.Add(new("x-amz-content-sha256", payloadHash)); Put("x-amz-content-sha256", payloadHash); }
        foreach (var (name, value) in added) if (name != "x-amz-content-sha256") Put(name, value);
        var signedHeaders = string.Join(';', headers.Keys);
        var canonical = new StringBuilder()
            .Append(request.Method.ToUpperInvariant()).Append('\n')
            .Append(CanonicalPath(request.Path, normalizePath)).Append('\n')
            .Append(CanonicalQuery(request.Query)).Append('\n');
        foreach (var (name, value) in headers) canonical.Append(name).Append(':').Append(value).Append('\n');
        canonical.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);
        var canonicalRequest = canonical.ToString();
        var scope = $"{shortDate}/{region}/{service}/aws4_request";
        var stringToSign = $"{Algorithm}\n{longDate}\n{scope}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";
        var key = SigningKey(credentials.SecretAccessKey, shortDate, region, service);
        var signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));
        var authorization = $"{Algorithm} Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}";
        return new(canonicalRequest, stringToSign, signature, authorization, added);
    }

    /// <summary>kSigning = HMAC(HMAC(HMAC(HMAC("AWS4" + secret, date), region), service), "aws4_request").</summary>
    public static byte[] SigningKey(string secret, string shortDate, string region, string service)
    {
        var date = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secret), Encoding.UTF8.GetBytes(shortDate));
        var regional = HMACSHA256.HashData(date, Encoding.UTF8.GetBytes(region));
        var serviceKey = HMACSHA256.HashData(regional, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(serviceKey, "aws4_request"u8.ToArray());
    }

    /// <summary>getCanonicalPath with uriEscapePath: drop empty and "." segments, resolve "..", keep a leading and trailing
    /// slash, then escape the whole path (so an already escaped "%" becomes "%25") and restore "/".</summary>
    public static string CanonicalPath(string path, bool normalize = true)
    {
        path = string.IsNullOrEmpty(path) ? "/" : path;
        if (!normalize) return EscapeUri(path).Replace("%2F", "/", StringComparison.Ordinal);
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(segment);
        }
        var normalized = (path.StartsWith('/') ? "/" : "") + string.Join('/', segments) + (segments.Count > 0 && path.EndsWith('/') ? "/" : "");
        return EscapeUri(normalized).Replace("%2F", "/", StringComparison.Ordinal);
    }

    /// <summary>getCanonicalQuery: escaped keys sorted, then values (excluding x-amz-signature).</summary>
    public static string CanonicalQuery(IReadOnlyList<KeyValuePair<string, string>> query)
    {
        if (query is null || query.Count == 0) return "";
        return string.Join('&', query.Where(pair => !pair.Key.Equals("x-amz-signature", StringComparison.OrdinalIgnoreCase))
            .Select(pair => (Key: EscapeUri(pair.Key), Value: EscapeUri(pair.Value ?? "")))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value));
    }

    /// <summary>@smithy/util-uri-escape escapeUri: encodeURIComponent plus !'()* escaped. Unreserved: A-Z a-z 0-9 - _ . ~.</summary>
    public static string EscapeUri(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var unit in Encoding.UTF8.GetBytes(value))
            if (unit is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
                builder.Append((char)unit);
            else builder.Append('%').Append(unit.ToString("X2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length); var space = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character)) { if (!space) builder.Append(' '); space = true; }
            else { builder.Append(character); space = false; }
        }
        return builder.ToString();
    }
}
