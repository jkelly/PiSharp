// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/api/anthropic-messages.ts (stream: the preferred strict
// tool fallback, #10409) and packages/ai/src/utils/diagnostics.ts (appendAssistantMessageDiagnostic).
using System.Text.Json.Nodes;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

/// <summary>When Anthropic rejects a request's strict tool set (grammar size, strict tool count or union-typed parameters), the
/// request is sent once more with strict "prefer" tools non-strict; "require" tools stay strict. Only a rejected request is
/// downgraded, never an already-started stream, a hook failure or a second rejection.</summary>
internal static class AnthropicStrictToolsFallback
{
    /// <summary>Set by the request factories on a request that sent a "prefer" tool strict: builds the downgraded request.</summary>
    internal static readonly HttpRequestOptionsKey<Func<CancellationToken, HttpRequestMessage>> RequestKey = new("PiSharp.Anthropic.PreferredStrictToolsFallback");

    private static readonly string[] Rejections =
    [
        "The compiled grammar is too large",
        "Too many strict tools (",
        "Schemas contains too many parameters with union types (",
    ];

    /// <summary>Upstream: an Anthropic.APIError with status 400 and type invalid_request_error whose message names a strict-tool limit.</summary>
    internal static bool IsStrictToolLimitRejection(Exception error) =>
        error is ProviderDisplayException { InStream: false, Status: 400, ErrorType: "invalid_request_error" } rejected &&
        Rejections.Any(text => rejected.Message.Contains(text, StringComparison.Ordinal));

    internal static Func<CancellationToken, HttpRequestMessage>? FallbackOf(HttpRequestMessage request) =>
        request.Options.TryGetValue(RequestKey, out var fallback) ? fallback : null;

    /// <summary>The anthropic_strict_tools_fallback diagnostic, stamped with Date.now() when the fallback is chosen.</summary>
    internal static JsonData Diagnostic(Exception rejection, TimeProvider clock) => JsonData.Parse(new JsonObject
    {
        ["type"] = "anthropic_strict_tools_fallback",
        ["timestamp"] = clock.GetUtcNow().ToUnixTimeMilliseconds(),
        ["error"] = new JsonObject { ["message"] = rejection.Message },
        ["details"] = new JsonObject { ["reason"] = "strict_tool_limits_exceeded" },
    }.ToJsonString());
}

/// <summary>Diagnostics one invocation records before its response (upstream appends them to the output message when they occur).</summary>
internal sealed class AnthropicRequestDiagnostics
{
    private readonly List<JsonData> _items = [];
    internal IReadOnlyList<JsonData> Items { get { lock (_items) return [.. _items]; } }
    internal void Add(JsonData diagnostic) { lock (_items) _items.Add(diagnostic); }
}
