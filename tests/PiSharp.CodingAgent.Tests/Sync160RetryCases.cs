// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/ai/test/retry.test.ts (the cases added for #12064, #12124 and #12128) over
// packages/ai/src/utils/retry.ts, whose coding-agent copy is AgentRetryPolicy.
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

internal static class Sync160RetryCases
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("sync160 retry policy matches the Codex websocket connection lifetime limit", () => Retryable(true,
            "Codex error: Responses websocket connection limit reached (60 minutes). Create a new websocket connection to continue.",
            "Codex error: websocket_connection_limit_reached")),
        ("sync160 retry policy does not match a generic connection limit", () => Retryable(false, "connection limit reached")),
        ("sync160 retry policy matches Node socket error codes", () => Retryable(true, "read ECONNRESET", "connect ETIMEDOUT 140.82.113.3:443",
            "write EPIPE", "connect ECONNREFUSED 127.0.0.1:443", "ECONNABORTED", "Connection reset by peer")),
        ("sync160 retry policy does not match socket error codes inside longer identifiers", () => Retryable(false,
            "Invalid value EXECONNRESET", "Unknown field ETIMEDOUTX", "Invalid key ERR_EPIPE_ID")),
        ("sync160 retry policy matches gateway wording for a closed connection", () => Retryable(true,
            """{"type":"error","error":{"type":"api_error","message":"Connection closed."}}""", "Connection closed.", "connection-closed"))
    ];

    private static Task Retryable(bool expected, params string[] messages)
    {
        foreach (var message in messages)
            if (AgentRetryPolicy.IsRetryableError(StopReason.Error, message) != expected)
                throw new InvalidOperationException($"Expected retryable={expected} for: {message}");
        return Task.CompletedTask;
    }
}
