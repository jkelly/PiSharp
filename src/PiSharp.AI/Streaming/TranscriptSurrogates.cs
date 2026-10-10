// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/sanitize-unicode.ts.
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI;

/// <summary>
/// A transcript's strings may hold lone surrogates, as Pi's JavaScript strings do (an RPC prompt, a tool call's arguments, a session line).
/// Pi's provider converters pass message text through <c>sanitizeSurrogates</c>, which drops every lone surrogate, while a tool call's
/// arguments go to the wire as the object they are, so <c>JSON.stringify</c> keeps a lone surrogate there as its escape. This applies
/// that once, before any provider sees the request: every string of every message loses its lone surrogates, except the strings inside
/// a <c>toolCall</c> block's <c>arguments</c>. Those stay only for the APIs whose request projection writes them back escaped as Pi does;
/// the others (google-generative-ai, google-vertex, bedrock-converse-stream and the rest) build their requests from System.Text.Json
/// nodes that cannot hold a lone surrogate, so an argument's lone surrogate is dropped there too rather than failing the request.
/// </summary>
internal static class TranscriptSurrogates
{
    private static readonly HashSet<string> KeepsArguments = new(StringComparer.Ordinal)
    {
        "anthropic-messages", "openai-completions", "openai-responses", "azure-openai-responses", "openai-codex-responses", "mistral-conversations",
        "google-generative-ai", "google-vertex"
    };

    internal static ChatRequest Sanitize(ChatRequest request)
    {
        // pi-messages.ts sends Pi's own messages to Pi's service unchanged (JSON.stringify keeps every lone surrogate, escaped).
        if (request.Model.Api == "pi-messages" || request.Messages.IsDefaultOrEmpty) return request;
        ImmutableArray<TranscriptEntry>.Builder? builder = null;
        for (var index = 0; index < request.Messages.Length; index++)
        {
            var entry = request.Messages[index];
            var raw = entry?.WireBody?.ToString();
            if (raw is null || !JsonUtf16.HasEscapedSurrogate(raw)) { builder?.Add(entry!); continue; }
            if (builder is null) { builder = ImmutableArray.CreateBuilder<TranscriptEntry>(request.Messages.Length); builder.AddRange(request.Messages, index); }
            var text = new StringBuilder(raw.Length);
            Write(text, entry!.WireBody.Value, keep: false, keepArguments: KeepsArguments.Contains(request.Model.Api));
            builder.Add(entry with { WireBody = JsonData.Parse(text.ToString()) });
        }
        return builder is null ? request : request with { Messages = builder.MoveToImmutable() };
    }

    /// <summary>sanitize-unicode.ts sanitizeSurrogates: drops every lone surrogate.</summary>
    internal static string Drop(string text)
    {
        if (JsonUtf16.IsWellFormed(text)) return text;
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) builder.Append(character).Append(text[++index]);
            else if (!char.IsSurrogate(character)) builder.Append(character);
        }
        return builder.ToString();
    }

    // Recursive: a transcript value is no deeper than an owned JsonData.
    private static void Write(StringBuilder builder, JsonElement value, bool keep, bool keepArguments)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                var isToolCall = !keep && keepArguments && value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("toolCall");
                foreach (var property in value.EnumerateObject())
                {
                    if (!first) builder.Append(',');
                    first = false;
                    var name = JsonUtf16.GetName(property);
                    JsonUtf16.Quote(builder, keep ? name : Drop(name)); builder.Append(':');
                    Write(builder, property.Value, keep || isToolCall && name == "arguments", keepArguments);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var index = 0;
                foreach (var item in value.EnumerateArray()) { if (index++ > 0) builder.Append(','); Write(builder, item, keep, keepArguments); }
                builder.Append(']');
                break;
            case JsonValueKind.String:
                var text = JsonUtf16.GetString(value);
                JsonUtf16.Quote(builder, keep ? text : Drop(text));
                break;
            default: builder.Append(value.GetRawText()); break;
        }
    }
}
