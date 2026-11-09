// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/retry.ts (isRetryableAssistantError, retryDelayMs) over a pi-ai
// wire assistant message. PiSharp.CodingAgent's AgentRetryPolicy ports the same classifier over PiSharp.Contracts stop reasons, but
// PiSharp.Durable does not reference PiSharp.CodingAgent; this port uses its regex translation, and the test project compares the two on
// shared inputs. retryAssistantCall is not ported: generation drives its own durable retry loop.
//
// JavaScript semantics kept: non-Unicode /i canonicalizes by upper-casing, never folding a non-ASCII character into ASCII, and `.` excludes
// the JavaScript line terminators; `\s` and `\S` use JavaScript's whitespace set.
using System.Text.RegularExpressions;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Internal.PiAi;

/// <summary>Transient-error classification and backoff (upstream <c>utils/retry.ts</c>).</summary>
internal static class PiAiRetry
{
    /// <summary>Upstream <c>DEFAULT_MAX_AGENT_RETRY_DELAY_MS</c>.</summary>
    public const double DefaultMaxAgentRetryDelayMs = 60_000;

    private const double MaxSafeInteger = 9_007_199_254_740_991;

    private static readonly Regex NonRetryableProviderLimitError = Pattern(string.Join("|",
        // OpenCode Go/free-tier limits and subscription-limit text.
        "GoUsageLimitError", "FreeUsageLimitError", "Monthly usage limit reached", "available balance",
        // Generic quota/budget/billing exhaustion.
        "insufficient_quota", "out of budget", "quota exceeded", "billing",
        // Sign in with ChatGPT: the subscription's shared usage limit, and a ChatGPT Business limit set by the workspace admin.
        "subscription_sharing_usage_limit_exceeded", "subscription_sharing_workplace_individual_usage_limit_exceeded",
        // Zhipu/Z.ai error code 1308: the plan's usage limit is reached, sent as HTTP 429 (#11537).
        "\"code\"\\s*:\\s*\"?1308(?![0-9])"));

    private static readonly Regex RetryableProviderError = Pattern(string.Join("|",
        // Generic provider load, HTTP status, and server-side transient failures.
        "overloaded", "server_busy", "servers are currently busy", "currently experiencing high demand", "model is at capacity", "rate.?limit",
        "too many requests",
        // Status codes must not be part of a longer word or number, e.g. a request id or byte count (#11057).
        "(?<![a-z0-9])(?:429|500|502|503|504|520|524)(?![a-z0-9])",
        // Also matches "The service is temporarily unavailable" (#10965).
        "service.?(?:is.?)?(?:temporarily.?)?unavailable", "server.?error", "internal.?error",
        // Wrapper/provider text for transient upstream failures, including gateway wording such as "The upstream provider returned an
        // error" (Pi v1.2.0, #10842).
        "provider.?returned.?(?:an.?)?error", "exceeded request buffer limit while retrying upstream",
        // Gateway failures relaying the upstream response stream, as `response.failed` events or error codes (#10920).
        "unable to forward upstream", "upstream.?request.?failed", "upstream_stream_read_error", "stream was interrupted", "http/?2 stream failed",
        // Network, proxy, and fetch transport failures; node-fetch system errors such as a Google ADC token refresh (#10996).
        "network.?error", "connection.?error", "connection.?refused", "connection.?lost", "other side closed", "fetch failed",
        "request to \\S+ failed, reason:", "getaddrinfo",
        "ENOTFOUND", "EAI_AGAIN", "upstream.?connect", "reset before headers", "socket hang up", "socket connection was closed", "timed? out",
        "timeout", "terminated",
        // WebSocket close/error text.
        "websocket.?closed", "websocket.?error",
        // Premature stream endings from SDKs and transports, including Codex CLI proxies' mid-stream disconnect (#11057).
        "ended without", "stream ended before message_stop", "stream ended before a terminal response event",
        "http2 request did not get a response", "stream disconnected before completion", "cut short after the reply had begun", "pending stream has been canceled",
        // Provider-requested retry delay cap failures.
        "retry delay",
        // Explicit retry guidance emitted mid-stream.
        "you can retry your request", "try your request again", "please retry your request",
        // gRPC based providers.
        "ResourceExhausted",
        // Sign in with ChatGPT: usage or user data temporarily unavailable.
        "subscription_sharing_usage_unavailable", "subscription_sharing_user_unavailable"));

    /// <summary>
    /// Whether a failed assistant message looks like a transient provider or transport error (upstream <c>isRetryableAssistantError</c>).
    /// Callers handle context overflow first.
    /// </summary>
    public static bool IsRetryableAssistantError(JsonObjectValue message)
    {
        if (!(message.TryGetValue("stopReason", out var reason) && reason is JsonStringValue { Value: "error" })) return false;
        if (!(message.TryGetValue("errorMessage", out var error) && error is JsonStringValue { Value.Length: > 0 } text)) return false;
        var canonical = Canonicalize(text.Value);
        if (NonRetryableProviderLimitError.IsMatch(canonical)) return false;
        return RetryableProviderError.IsMatch(canonical);
    }

    /// <summary><c>baseDelayMs * 2^(attempt-1)</c>, a non-safe-integer product counting as the largest safe integer, capped at
    /// <paramref name="maxAgentDelayMs"/> (default 60 s) (upstream <c>retryDelayMs</c>).</summary>
    public static double RetryDelayMs(double baseDelayMs, double? maxAgentDelayMs, int attempt)
    {
        var delay = baseDelayMs * Math.Pow(2, Math.Max(0, attempt - 1));
        var safe = double.IsFinite(delay) && Math.Truncate(delay) == delay && Math.Abs(delay) <= MaxSafeInteger ? delay : MaxSafeInteger;
        return Math.Min(safe, maxAgentDelayMs ?? DefaultMaxAgentRetryDelayMs);
    }

    private const string JsSpace = "\\u0009-\\u000D\\u0020\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF";

    private static Regex Pattern(string expression) => new(
        Canonicalize(expression, escapes: true).Replace(".?", "[^\\r\\n\\u2028\\u2029]?", StringComparison.Ordinal)
            .Replace("\\s", "[" + JsSpace + "]", StringComparison.Ordinal).Replace("\\S", "[^" + JsSpace + "]", StringComparison.Ordinal),
        RegexOptions.CultureInvariant);

    private static string Canonicalize(string text, bool escapes = false) => string.Create(text.Length, (text, escapes), static (target, state) =>
    {
        var (source, keepEscapes) = state;
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            var upper = char.ToUpperInvariant(value);
            // JavaScript non-Unicode /i does not fold non-ASCII characters into ASCII.
            target[index] = value >= 128 && upper < 128 ? value : upper;
            // Pattern escapes keep their case: \s is not \S.
            if (keepEscapes && value == '\\' && index + 1 < source.Length) { index++; target[index] = source[index]; }
        }
    });
}
