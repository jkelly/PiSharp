using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

internal static class AnthropicThreadJson
{
    internal static JsonNode Parse(string json) => JsonUtf16.MutableNode(json)!;
    internal static string Serialize(JsonNode node) => JsonUtf16.ToJsonString(node);
}

/// <summary>Bounded process-local Anthropic continuation state. Stores hashes and reply IDs, never prompts or credentials.
/// Cleanup invalidates outstanding requests too, so a late reply cannot resurrect a closed session.</summary>
public sealed class AnthropicMessageThreadStore
{
    public static AnthropicMessageThreadStore Shared { get; } = new();
    private sealed record State(long Generation, int Count = 0, string? Hash = null, string? ReplyId = null, bool Disabled = false);
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _entries = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private long _generation;
    public AnthropicMessageThreadStore(int maximumSessions = 10_000)
    { if (maximumSessions <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSessions)); _capacity = maximumSessions; }
    public int Count { get { lock (_gate) return _entries.Count; } }
    public void Cleanup(string? sessionId = null)
    { lock (_gate) { if (sessionId is null) _entries.Clear(); else _entries.Remove(sessionId); } }
    internal AnthropicThreadAttempt? Prepare(AnthropicThreadContext context)
    {
        lock (_gate)
        {
            _entries.TryGetValue(context.SessionId, out var old);
            if (old?.Disabled == true) return null;
            var generation = ++_generation;
            var payload = AnthropicThreadJson.Parse(context.Payload.ToString()).AsObject();
            if (payload["messages"] is not JsonArray messages) return null;
            var continuation = old?.ReplyId is not null && messages.Count > old.Count && Hash(context, messages.Take(old.Count)) == old.Hash;
            if (continuation)
            {
                payload.Remove("system"); payload.Remove("tools");
                payload["messages"] = new JsonArray(messages.Skip(old!.Count).Select(m => m?.DeepClone()).ToArray());
                payload["thread"] = new JsonObject { ["type"] = "continue", ["previous_message_id"] = old.ReplyId };
            }
            else payload["thread"] = new JsonObject { ["type"] = "create" };
            // Replace before the network call: a reply ID may only be consumed once, including concurrent callers.
            Set(context.SessionId, new(generation));
            return new(context, generation, JsonData.Parse(AnthropicThreadJson.Serialize(payload)));
        }
    }
    private void Set(string session, State state)
    {
        _entries.Remove(session); _entries.Add(session, state);
        if (_entries.Count > _capacity) _entries.Remove(_entries.Keys.First());
    }
    internal void Disable(AnthropicThreadAttempt attempt)
    { lock (_gate) if (Current(attempt)) Set(attempt.Context.SessionId, new(attempt.Generation, Disabled: true)); }
    private bool Current(AnthropicThreadAttempt attempt) => _entries.TryGetValue(attempt.Context.SessionId, out var state) && state.Generation == attempt.Generation;
    internal void Complete(AnthropicThreadAttempt attempt, AssistantMessage reply, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        var properties = reply.ExtraProperties;
        if (reply.StopReason is StopReason.Error or StopReason.Aborted ||
            properties is null || !properties.TryGet("responseId", out var id) || id!.Value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(JsonUtf16.GetString(id.Value)) || JsonUtf16.GetString(id.Value).Length > 4096 || properties.TryGet("responseModel", out _)) return;
        var context = attempt.Context;
        try
        {
            // Convert exactly as the next transcript projection does, then discard generated trailing tool-result placeholders.
            var entry = new TranscriptEntry("assistant", PiWireJson.WriteMessage(reply));
            var projected = new AnthropicMessagesRequestProjector(context.Projection).Project(new(context.Model, [entry]));
            var messages = AnthropicThreadJson.Parse(context.Payload.ToString())["messages"]!.AsArray().Select(m => m?.DeepClone()).ToList();
            messages.AddRange(AnthropicThreadJson.Parse(projected.ToString())["messages"]!.AsArray().Where(m => m?["role"]?.GetValue<string>() == "assistant").Select(m => m?.DeepClone()));
            var hash = Hash(context, messages);
            lock (_gate) if (!token.IsCancellationRequested && Current(attempt)) Set(context.SessionId, new(attempt.Generation, messages.Count, hash, JsonUtf16.GetString(id.Value)));
        }
        catch (Exception error) when (error is AnthropicRequestException or JsonException or InvalidOperationException or ArgumentException)
        { /* A reply that cannot be replayed safely never becomes continuation state or fails a successful request. */ }
    }
    private static string Hash(AnthropicThreadContext context, IEnumerable<JsonNode?> messages)
    {
        var settings = AnthropicThreadJson.Parse(context.Payload.ToString()).AsObject();
        settings.Remove("messages"); settings.Remove("max_tokens"); settings.Remove("stream");
        var normalized = new JsonArray(messages.Select(m => Normalize(m)).ToArray());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(context.Model.Provider + "\n" + context.Endpoint + "\n" + AnthropicThreadJson.Serialize(settings) + AnthropicThreadJson.Serialize(normalized))));
    }
    private static JsonNode? Normalize(JsonNode? node)
    {
        if (node is JsonArray array) return new JsonArray(array.Select(Normalize).ToArray());
        if (node is not JsonObject obj) return node?.DeepClone();
        var result = new JsonObject();
        foreach (var (key, value) in obj)
        {
            if (key == "cache_control") continue;
            result[key] = key == "content" && value is JsonValue text && text.TryGetValue<string>(out var content)
                ? new JsonArray(new JsonObject { ["type"] = "text", ["text"] = content }) : Normalize(value);
        }
        return result;
    }
}

internal sealed record AnthropicThreadContext(AnthropicMessageThreadStore Store, string SessionId, ModelDescriptor Model,
    string Endpoint, JsonData Payload, AnthropicMessagesRequestOptions Projection, Func<JsonData, CancellationToken, HttpRequestMessage> Rebuild)
{
    internal const string Beta = "message-threads-2026-08-12";
    internal static readonly HttpRequestOptionsKey<AnthropicThreadContext> RequestKey = new("PiSharp.Anthropic.MessageThread");
    internal static bool Eligible(AnthropicMessagesRequestOptions projection, ModelDescriptor model, Uri endpoint, string? session) =>
        projection.CacheRetention != AnthropicCacheRetention.None && !string.IsNullOrEmpty(session) &&
        (projection.SupportsMessageThreads ?? (model.Provider == "anthropic" && endpoint.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase)));
    internal static JsonData WithBeta(JsonData payload)
    {
        var root = AnthropicThreadJson.Parse(payload.ToString()).AsObject();
        var betas = root["betas"] as JsonArray ?? new();
        if (!betas.Any(b => b?.GetValue<string>() == Beta)) betas.Add(Beta);
        root["betas"] = betas;
        return JsonData.Parse(AnthropicThreadJson.Serialize(root));
    }
}
internal sealed record AnthropicThreadAttempt(AnthropicThreadContext Context, long Generation, JsonData Payload)
{
    internal static string? FallbackReason(Exception error)
    {
        if (error is not ProviderDisplayException { InStream: false } rejected) return null;
        // ProviderDisplayException preserves the SDK-style status + complete JSON error body.
        var start = rejected.Message.IndexOf('{'); string? code = null;
        if (start >= 0) try
        {
            using var document = JsonDocument.Parse(rejected.Message[start..]);
            if (document.RootElement.TryGetUtf16Property("error", out var body) && body.TryGetUtf16Property("details", out var details) &&
                details.TryGetUtf16Property("error_code", out var value) && value.ValueKind == JsonValueKind.String) code = JsonUtf16.GetString(value);
        }
        catch (JsonException) { }
        if (code is "thread_not_found" or "thread_already_continued" or "thread_continue_in_flight" or "thread_fingerprint_mismatch" or "thread_unsupported_request") return code;
        return code is null && rejected.Status == 400 && rejected.Message.Contains("thread", StringComparison.Ordinal) ? "400" : null;
    }
    internal JsonData RetryPayload(bool unsupported)
    {
        var full = AnthropicThreadJson.Parse(Context.Payload.ToString()).AsObject();
        full.Remove("thread");
        if (!unsupported) full["thread"] = new JsonObject { ["type"] = "create" };
        return JsonData.Parse(AnthropicThreadJson.Serialize(full));
    }
    internal static JsonData Diagnostic(Exception error, string reason, TimeProvider clock) => JsonData.Parse(JsonUtf16.ToJsonString(new JsonObject
    {
        ["type"] = "anthropic_thread_fallback", ["timestamp"] = clock.GetUtcNow().ToUnixTimeMilliseconds(),
        ["error"] = new JsonObject { ["message"] = JsonUtf16.StringNode(error.Message) }, ["details"] = new JsonObject { ["reason"] = reason }
    }, System.Text.Encodings.Web.JavaScriptEncoder.Default));
}
