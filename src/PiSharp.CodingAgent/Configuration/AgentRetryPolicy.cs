// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/retry.ts.
using System.Text.RegularExpressions;
using PiSharp.Contracts;

namespace PiSharp.CodingAgent.Configuration;

/// <summary>Agent retry preferences; the initial provider call consumes no retry attempt.</summary>
public sealed record AgentRetryPolicy
{
    public const long MaximumSafeInteger = 9_007_199_254_740_991;
    public bool Enabled { get; }
    public int MaxRetries { get; }
    public long BaseDelayMs { get; }
    public long MaxAgentDelayMs { get; }
    public static AgentRetryPolicy Default { get; } = new();

    public AgentRetryPolicy(bool enabled = true, int maxRetries = 3, long baseDelayMs = 2000, long maxAgentDelayMs = 60000)
    {
        if (maxRetries < 0) throw new ArgumentOutOfRangeException(nameof(maxRetries));
        if (baseDelayMs < 0 || baseDelayMs > MaximumSafeInteger) throw new ArgumentOutOfRangeException(nameof(baseDelayMs));
        if (maxAgentDelayMs < 0 || maxAgentDelayMs > MaximumSafeInteger) throw new ArgumentOutOfRangeException(nameof(maxAgentDelayMs));
        Enabled = enabled; MaxRetries = maxRetries; BaseDelayMs = baseDelayMs; MaxAgentDelayMs = maxAgentDelayMs;
    }

    public long DelayMs(int attempt)
    {
        // Match binary64 multiplication then Number.isSafeInteger, including 0 * Infinity.
        var delay = BaseDelayMs * Math.Pow(2, Math.Max(0d, (double)attempt - 1));
        var safe = double.IsFinite(delay) && delay <= MaximumSafeInteger && Math.Truncate(delay) == delay
            ? (long)delay : MaximumSafeInteger;
        return Math.Min(safe, MaxAgentDelayMs);
    }

    // Context overflow exclusion belongs to the caller, before this shared provider classifier.
    public static bool IsRetryableError(StopReason reason, string? errorMessage)
    {
        if (reason != StopReason.Error || string.IsNullOrEmpty(errorMessage)) return false;
        var canonical = Canonicalize(errorMessage);
        return !Permanent.IsMatch(canonical) && Transient.IsMatch(canonical);
    }

    private static readonly Regex Permanent = Pattern(
        "GoUsageLimitError|FreeUsageLimitError|Monthly usage limit reached|available balance|insufficient_quota|out of budget|quota exceeded|billing|subscription_sharing_usage_limit_exceeded|subscription_sharing_workplace_individual_usage_limit_exceeded|\"code\"\\s*:\\s*\"?1308(?![0-9])");
    private static readonly Regex Transient = Pattern(
        "overloaded|server_busy|servers are currently busy|currently experiencing high demand|model is at capacity|rate.?limit|too many requests|(?<![a-z0-9])(?:429|500|502|503|504|520|524)(?![a-z0-9])|service.?(?:is.?)?(?:temporarily.?)?unavailable|server.?error|internal.?error|provider.?returned.?(?:an.?)?error|exceeded request buffer limit while retrying upstream|unable to forward upstream|upstream.?request.?failed|upstream_stream_read_error|stream was interrupted|http/?2 stream failed|network.?error|connection.?error|connection.?refused|connection.?lost|other side closed|fetch failed|request to \\S+ failed, reason:|getaddrinfo|ENOTFOUND|EAI_AGAIN|upstream.?connect|reset before headers|socket hang up|socket connection was closed|timed? out|timeout|terminated|websocket.?closed|websocket.?error|ended without|stream ended before message_stop|stream ended before a terminal response event|http2 request did not get a response|stream disconnected before completion|cut short after the reply had begun|pending stream has been canceled|retry delay|you can retry your request|try your request again|please retry your request|ResourceExhausted|subscription_sharing_usage_unavailable|subscription_sharing_user_unavailable");

    // JavaScript's \s set; .NET's differs (U+0085 in, U+FEFF out).
    private const string JsSpace = "\\u0009-\\u000D\\u0020\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF";

    // Escapes such as \s keep their case; the status lookarounds' [a-z0-9] is upper-cased with the rest, as the text is.
    private static Regex Pattern(string expression) => new(
        Canonicalize(expression, escapes: true).Replace(".?", "[^\\r\\n\\u2028\\u2029]?", StringComparison.Ordinal)
            .Replace("\\s", "[" + JsSpace + "]", StringComparison.Ordinal).Replace("\\S", "[^" + JsSpace + "]", StringComparison.Ordinal),
        RegexOptions.CultureInvariant);

    private static string Canonicalize(string text, bool escapes = false) => string.Create(text.Length, (text, escapes), static (target, state) =>
    {
        var (source, keepEscapes) = state;
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index]; var upper = char.ToUpperInvariant(value);
            // JavaScript non-Unicode /i does not fold non-ASCII characters into ASCII.
            target[index] = value >= 128 && upper < 128 ? value : upper;
            if (keepEscapes && value == '\\' && index + 1 < source.Length) { index++; target[index] = source[index]; }
        }
    });
}
