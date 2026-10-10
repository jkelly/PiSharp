// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/bedrock-converse-stream.ts (stream request building:
// convertMessages, buildSystemPrompt, convertToolConfig, buildAdditionalModelRequestFields, streamSimple and the model capability
// predicates). The body is the ConverseStream REST JSON the SDK 3.1127.0 serializes (members in its schema's order, blobs as base64).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.AI.Protocols.ProviderShared;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.Bedrock;

/// <summary>Bedrock stream options (BedrockOptions plus the shared StreamOptions the port uses).</summary>
public sealed record BedrockConverseOptions
{
    public string? Region { get; init; }
    public string? Profile { get; init; }
    /// <summary>"auto", "any", "none" or {"type":"tool","name":...}.</summary>
    public JsonData? ToolChoice { get; init; }
    /// <summary>Custom token budgets for minimal/low/medium/high.</summary>
    public ImmutableDictionary<string, int>? ThinkingBudgets { get; init; }
    public bool? InterleavedThinking { get; init; }
    /// <summary>"summarized" (default) or "omitted".</summary>
    public string? ThinkingDisplay { get; init; }
    public ImmutableDictionary<string, string>? RequestMetadata { get; init; }
    public string? BearerToken { get; init; }
    /// <summary>The resolved auth apiKey (a stored bearer token).</summary>
    public string? ApiKey { get; init; }
    /// <summary>An explicit output cap; Simple requests default to the model cap clamped to the context window.</summary>
    public double? MaxTokens { get; init; }
    public double? Temperature { get; init; }
    /// <summary>"none", "short" or "long"; null reads PI_CACHE_RETENTION, else short.</summary>
    public string? CacheRetention { get; init; }
    /// <summary>Request headers; a null value is dropped. x-amz-*, authorization and host are never overridden.</summary>
    public ImmutableDictionary<string, string?>? Headers { get; init; }
    /// <summary>Provider-scoped env (a stored credential's env) consulted before the process environment.</summary>
    public ImmutableDictionary<string, string>? Environment { get; init; }
    /// <summary>Per-request auth resolution (model-runtime prepareRequest): its apiKey and env replace <see cref="ApiKey"/> and
    /// <see cref="Environment"/> for that request.</summary>
    public Func<CancellationToken, ValueTask<(string? ApiKey, ImmutableDictionary<string, string>? Environment)>>? Auth { get; init; }
    /// <summary>The SDK's standard retry mode attempts (AWS_MAX_ATTEMPTS, default 3).</summary>
    public int? MaxAttempts { get; init; }
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    public Func<double>? Random { get; init; }
    public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask<JsonData?>>? OnPayload { get; init; }
    public Func<JsonData, ModelDescriptor, CancellationToken, ValueTask>? OnResponse { get; init; }
    public int MaximumPayloadBytes { get; init; } = 32 * 1024 * 1024;
    public int MaximumContentBlocks { get; init; } = int.MaxValue;
    /// <summary>The accumulated response, bounded like every other provider's (the SDK has no bound of its own).</summary>
    public int MaximumResponseCharacters { get; init; } = PiRequestBudget.StreamCharacters;
}

/// <summary>The catalog model fields the Bedrock stream reads.</summary>
internal sealed class BedrockModel
{
    internal BedrockModel(ModelDescriptor descriptor, JsonData metadata)
    {
        Descriptor = descriptor; Raw = metadata.Value;
        Name = Raw.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        BaseUrl = Raw.TryGetProperty("baseUrl", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString()! : "";
        Reasoning = Raw.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.True;
        SupportsImages = Raw.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array && input.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image");
        MaxTokens = Raw.TryGetProperty("maxTokens", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetDouble() : 0;
        ContextWindow = Raw.TryGetProperty("contextWindow", out var window) && window.ValueKind == JsonValueKind.Number ? window.GetDouble() : 0;
        SupportsStrictMode = Raw.TryGetProperty("compat", out var compat) && compat.ValueKind == JsonValueKind.Object &&
            compat.TryGetProperty("supportsStrictMode", out var strict) && strict.ValueKind == JsonValueKind.True;
        Cost = Raw.TryGetProperty("cost", out var cost) ? PromptLengthPricing.ReadCost(cost) : (0, 0, 0, 0, []);
    }
    internal ModelDescriptor Descriptor { get; }
    internal JsonElement Raw { get; }
    internal string Id => Descriptor.Id;
    internal string? Name { get; }
    internal string BaseUrl { get; }
    internal bool Reasoning { get; }
    internal bool SupportsImages { get; }
    internal double MaxTokens { get; }
    internal double ContextWindow { get; }
    internal bool SupportsStrictMode { get; }
    internal (decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, ImmutableArray<TokenRateTier> Tiers) Cost { get; }
}

/// <summary>Resolved per-request stream options (the stream() BedrockOptions after streamSimple).</summary>
internal sealed record BedrockStreamOptions(double? MaxTokens, string? Reasoning, ImmutableDictionary<string, int>? ThinkingBudgets);

internal static partial class BedrockConverseRequest
{
    private const string EmptyTextPlaceholder = "<empty>";
    private const string ThinkingBindingControlsBeta = "thinking-binding-controls-2026-08-01";

    /// <summary>streamSimple: base output cap clamped to the context, then the Claude budget adjustment for thinking levels.</summary>
    internal static BedrockStreamOptions Simple(BedrockModel model, IReadOnlyList<JsonObject> transcript, BedrockConverseOptions options, string? level)
    {
        var maxTokens = ClampToContext(model, transcript, options.MaxTokens ?? model.MaxTokens);
        if (level is null or "off") return new(maxTokens, null, null);
        if (IsAnthropicClaude(model))
        {
            if (SupportsAdaptiveThinking(model.Id, model.Name)) return new(maxTokens, level, options.ThinkingBudgets);
            // adjustMaxTokensForThinking(base.maxTokens, model.maxTokens, level, budgets)
            var budgets = new Dictionary<string, int>(StringComparer.Ordinal) { ["minimal"] = 1024, ["low"] = 2048, ["medium"] = 8192, ["high"] = 16384 };
            foreach (var (name, value) in options.ThinkingBudgets ?? ImmutableDictionary<string, int>.Empty) budgets[name] = value;
            var clamped = level is "xhigh" or "max" ? "high" : level;
            double thinkingBudget = budgets[clamped];
            var adjusted = Math.Min(maxTokens + thinkingBudget, model.MaxTokens);
            if (adjusted <= thinkingBudget) thinkingBudget = Math.Min(thinkingBudget, Math.Max(0, adjusted - 1024));
            var capped = ClampToContext(model, transcript, adjusted);
            var custom = (options.ThinkingBudgets ?? ImmutableDictionary<string, int>.Empty)
                .SetItem(clamped, (int)Math.Min(thinkingBudget, Math.Max(0, capped - 1024)));
            return new(capped, level, custom);
        }
        return new(maxTokens, level, options.ThinkingBudgets);
    }

    private static double ClampToContext(BedrockModel model, IReadOnlyList<JsonObject> transcript, double maxTokens)
    {
        if (model.ContextWindow <= 0) return Math.Max(1, maxTokens);
        var available = model.ContextWindow - ProviderTranscript.EstimateContextTokens(transcript) - 4096;
        return Math.Min(maxTokens, Math.Max(1, available));
    }

    /// <summary>The ConverseStream body for one request.</summary>
    internal static JsonObject Build(BedrockModel model, ChatRequest request, BedrockConverseOptions options, BedrockStreamOptions stream,
        Func<string, string?> env, string? configuredRegion)
    {
        // Bedrock has no mid-conversation system messages; fold them into the leading prompt.
        var collapsed = ProviderTranscript.CollapseSystemMessages(ProviderTranscript.Parse(request.Messages));
        var cacheRetention = options.CacheRetention ?? (env("PI_CACHE_RETENTION") == "long" ? "long" : "short");
        var initial = collapsed.Count > 0 && ProviderTranscript.Role(collapsed[0]) == "system" ? collapsed[0] : null;
        var systemPrompt = initial is null ? null : ProviderTranscript.SystemText(initial);
        var inferenceMaxTokens = stream.MaxTokens ?? (IsAnthropicClaude(model) ? model.MaxTokens : null);
        // The SDK's schema serializer writes ConverseStreamRequest members in the schema's order.
        var body = new JsonObject { ["messages"] = ConvertMessages(collapsed, model, request, cacheRetention, env) };
        if (BuildSystemPrompt(systemPrompt, model, cacheRetention, env) is { } system) body["system"] = system;
        var inference = new JsonObject();
        if (inferenceMaxTokens is { } tokens) inference["maxTokens"] = (long)tokens;
        if (options.Temperature is { } temperature) inference["temperature"] = temperature;
        body["inferenceConfig"] = inference;
        if (ConvertToolConfig(ProviderTranscript.CurrentTools(collapsed), options.ToolChoice, model.SupportsStrictMode) is { } tools) body["toolConfig"] = tools;
        if (BuildAdditionalModelRequestFields(model, options, stream, configuredRegion) is { } additional) body["additionalModelRequestFields"] = additional;
        if (options.RequestMetadata is { } metadata)
        {
            var values = new JsonObject(); foreach (var (key, value) in metadata) values[key] = value; body["requestMetadata"] = values;
        }
        return body;
    }

    internal static string Sanitize(string text)
    {
        // sanitizeSurrogates: unpaired surrogates are removed.
        var builder = new System.Text.StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < text.Length && char.IsLowSurrogate(text[index + 1])) { builder.Append(character).Append(text[++index]); }
                continue;
            }
            if (char.IsLowSurrogate(character)) continue;
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static JsonObject CachePoint(string retention) => retention == "long"
        ? new JsonObject { ["type"] = "default", ["ttl"] = "1h" } : new JsonObject { ["type"] = "default" };

    private static JsonArray? BuildSystemPrompt(string? prompt, BedrockModel model, string retention, Func<string, string?> env)
    {
        if (string.IsNullOrEmpty(prompt)) return null;
        var blocks = new JsonArray(new JsonObject { ["text"] = Sanitize(prompt) });
        if (retention != "none" && SupportsPromptCaching(model, env)) blocks.Add(new JsonObject { ["cachePoint"] = CachePoint(retention) });
        return blocks;
    }

    /// <summary>normalizeToolCallId: [^a-zA-Z0-9_-] become "_", at most 64 characters.</summary>
    internal static string NormalizeToolCallId(string id)
    {
        var sanitized = ToolIdCharacters().Replace(id, "_");
        return sanitized.Length > 64 ? sanitized[..64] : sanitized;
    }
    [GeneratedRegex("[^a-zA-Z0-9_-]")] private static partial Regex ToolIdCharacters();

    private static JsonObject? NonBlankText(string text)
    {
        var sanitized = Sanitize(text);
        return sanitized.Trim().Length == 0 ? null : new JsonObject { ["text"] = sanitized };
    }

    private static JsonArray ConvertMessages(List<JsonObject> context, BedrockModel model, ChatRequest request, string retention, Func<string, string?> env)
    {
        var withoutInitial = context.Count > 0 && ProviderTranscript.Role(context[0]) == "system" ? context.Skip(1).ToList() : context;
        var messages = ProviderTranscript.Transform(withoutInitial, model.Descriptor, model.SupportsImages, NormalizeToolCallId, request.Timestamp);
        var result = new JsonArray();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index]; var role = ProviderTranscript.Role(message);
            switch (role)
            {
                case "user":
                {
                    var content = new JsonArray();
                    if (message["content"] is JsonValue text && text.TryGetValue<string>(out var value))
                        content.Add(NonBlankText(value) ?? new JsonObject { ["text"] = EmptyTextPlaceholder });
                    else
                    {
                        foreach (var block in (message["content"] as JsonArray)?.OfType<JsonObject>() ?? [])
                            switch (ProviderTranscript.Text(block, "type"))
                            {
                                case "text": if (NonBlankText(ProviderTranscript.Text(block, "text") ?? "") is { } textBlock) content.Add(textBlock); break;
                                case "image": content.Add(new JsonObject { ["image"] = ImageBlock(block) }); break;
                            }
                        if (content.Count == 0) content.Add(new JsonObject { ["text"] = EmptyTextPlaceholder });
                    }
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                    break;
                }
                case "assistant":
                {
                    if (message["content"] is not JsonArray blocks || blocks.Count == 0) continue;
                    var content = new JsonArray();
                    foreach (var block in blocks.OfType<JsonObject>())
                        switch (ProviderTranscript.Text(block, "type"))
                        {
                            case "text": if (NonBlankText(ProviderTranscript.Text(block, "text") ?? "") is { } textBlock) content.Add(textBlock); break;
                            case "toolCall":
                                content.Add(new JsonObject { ["toolUse"] = new JsonObject
                                {
                                    ["toolUseId"] = ProviderTranscript.Text(block, "id"), ["name"] = ProviderTranscript.Text(block, "name"),
                                    ["input"] = SanitizeDocument(block["arguments"]?.DeepClone() ?? new JsonObject())
                                } });
                                break;
                            case "thinking":
                            {
                                if (block["redacted"] is JsonValue redacted && redacted.TryGetValue<bool>(out var isRedacted) && isRedacted)
                                {
                                    // Encrypted reasoning replays its stored payload; an undecodable payload drops the block.
                                    if (DecodeRedacted(ProviderTranscript.Text(block, "thinkingSignature")) is { Length: > 0 } bytes)
                                        content.Add(new JsonObject { ["reasoningContent"] = new JsonObject { ["redactedContent"] = Convert.ToBase64String(bytes) } });
                                    continue;
                                }
                                var thinking = Sanitize(ProviderTranscript.Text(block, "thinking") ?? "");
                                if (thinking.Trim().Length == 0) continue;
                                if (IsAnthropicClaude(model))
                                {
                                    var signature = ProviderTranscript.Text(block, "thinkingSignature");
                                    content.Add(string.IsNullOrEmpty(signature) || signature.Trim().Length == 0
                                        ? new JsonObject { ["text"] = thinking }
                                        : new JsonObject { ["reasoningContent"] = new JsonObject { ["reasoningText"] = new JsonObject { ["text"] = thinking, ["signature"] = signature } } });
                                }
                                else content.Add(new JsonObject { ["reasoningContent"] = new JsonObject { ["reasoningText"] = new JsonObject { ["text"] = thinking } } });
                                break;
                            }
                        }
                    if (content.Count == 0) continue;
                    result.Add(new JsonObject { ["role"] = "assistant", ["content"] = content });
                    break;
                }
                case "toolResult":
                {
                    // Consecutive tool results share one user message.
                    var results = new JsonArray();
                    var next = index;
                    while (next < messages.Count && ProviderTranscript.Role(messages[next]) == "toolResult")
                    {
                        var toolResult = messages[next];
                        var isError = toolResult["isError"] is JsonValue error && error.TryGetValue<bool>(out var failed) && failed;
                        results.Add(new JsonObject { ["toolResult"] = new JsonObject
                        {
                            ["toolUseId"] = ProviderTranscript.Text(toolResult, "toolCallId"),
                            ["content"] = ToolResultContent(toolResult["content"] as JsonArray), ["status"] = isError ? "error" : "success"
                        } });
                        next++;
                    }
                    index = next - 1;
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = results });
                    break;
                }
            }
        }
        if (retention != "none" && SupportsPromptCaching(model, env) && result.Count > 0 && result[^1] is JsonObject last &&
            ProviderTranscript.Text(last, "role") == "user" && last["content"] is JsonArray lastContent)
            lastContent.Add(new JsonObject { ["cachePoint"] = CachePoint(retention) });
        return result;
    }

    private static JsonArray ToolResultContent(JsonArray? content)
    {
        var result = new JsonArray();
        foreach (var block in content?.OfType<JsonObject>() ?? [])
            if (ProviderTranscript.Text(block, "type") == "image") result.Add(new JsonObject { ["image"] = ImageBlock(block) });
            else if (NonBlankText(ProviderTranscript.Text(block, "text") ?? "") is { } text) result.Add(text);
        if (result.Count == 0) result.Add(new JsonObject { ["text"] = EmptyTextPlaceholder });
        return result;
    }

    private static JsonObject ImageBlock(JsonObject block)
    {
        var format = ProviderTranscript.Text(block, "mimeType") switch
        {
            "image/jpeg" or "image/jpg" => "jpeg", "image/png" => "png", "image/gif" => "gif", "image/webp" => "webp",
            var other => throw new ProviderTranscriptException($"Unknown image type: {other}")
        };
        // atob then the SDK's base64 encoder: the decoded bytes travel re-encoded.
        var bytes = Convert.FromBase64String(ProviderTranscript.Text(block, "data") ?? "");
        return new JsonObject { ["format"] = format, ["source"] = new JsonObject { ["bytes"] = Convert.ToBase64String(bytes) } };
    }

    private static byte[]? DecodeRedacted(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        try { return Convert.FromBase64String(signature); } catch (FormatException) { return null; }
    }

    /// <summary>sanitizeBedrockDocument: empty object keys are dropped at every depth.</summary>
    private static JsonNode? SanitizeDocument(JsonNode? value)
    {
        switch (value)
        {
            case JsonArray array:
                var items = array.Select(item => SanitizeDocument(item?.DeepClone())).ToArray();
                return new JsonArray(items);
            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, nested) in obj) if (key.Length > 0) result[key] = SanitizeDocument(nested?.DeepClone());
                return result;
            default: return value;
        }
    }

    private static JsonObject? ConvertToolConfig(List<JsonObject> tools, JsonData? toolChoice, bool supportsStrictMode)
    {
        if (tools.Count == 0) return null;
        var choice = toolChoice?.Value;
        if (choice is { ValueKind: JsonValueKind.String } none && none.GetString() == "none") return null;
        var specs = new JsonArray();
        foreach (var tool in tools)
        {
            var strict = ProviderTranscript.ResolveStrict(tool, supportsStrictMode);
            var spec = new JsonObject
            {
                ["name"] = ProviderTranscript.Text(tool, "name"),
                ["inputSchema"] = new JsonObject { ["json"] = ProviderTranscript.ToolParameters(tool, strict) },
                ["description"] = ProviderTranscript.Text(tool, "description")
            };
            if (strict == true) spec["strict"] = true;
            specs.Add(new JsonObject { ["toolSpec"] = spec });
        }
        var config = new JsonObject { ["tools"] = specs };
        if (choice is { } selected)
        {
            if (selected.ValueKind == JsonValueKind.String && selected.GetString() == "auto") config["toolChoice"] = new JsonObject { ["auto"] = new JsonObject() };
            else if (selected.ValueKind == JsonValueKind.String && selected.GetString() == "any") config["toolChoice"] = new JsonObject { ["any"] = new JsonObject() };
            else if (selected.ValueKind == JsonValueKind.Object && selected.TryGetProperty("type", out var type) && type.GetString() == "tool")
                config["toolChoice"] = new JsonObject { ["tool"] = new JsonObject { ["name"] = selected.GetProperty("name").GetString() } };
        }
        return config;
    }

    private static readonly ImmutableDictionary<string, string> GptEffort = new Dictionary<string, string>
    { ["minimal"] = "low", ["low"] = "low", ["medium"] = "medium", ["high"] = "high", ["xhigh"] = "xhigh", ["max"] = "max" }.ToImmutableDictionary();
    private static readonly ImmutableDictionary<string, string> GptOssEffort = new Dictionary<string, string>
    { ["minimal"] = "low", ["low"] = "low", ["medium"] = "medium", ["high"] = "high", ["xhigh"] = "high", ["max"] = "high" }.ToImmutableDictionary();

    private static JsonObject? BuildAdditionalModelRequestFields(BedrockModel model, BedrockConverseOptions options, BedrockStreamOptions stream, string? configuredRegion)
    {
        if (stream.Reasoning is not { } level || !model.Reasoning) return null;
        if (IsAnthropicClaude(model))
        {
            // GovCloud rejects thinking.display and block binding.
            var govCloud = configuredRegion?.StartsWith("us-gov-", StringComparison.OrdinalIgnoreCase) == true ||
                model.Id.StartsWith("us-gov.", StringComparison.OrdinalIgnoreCase) || model.Id.StartsWith("arn:aws-us-gov:", StringComparison.OrdinalIgnoreCase);
            var display = govCloud ? null : options.ThinkingDisplay ?? "summarized";
            var binding = !govCloud && SupportsThinkingBlockBinding(model);
            var adaptive = SupportsAdaptiveThinking(model.Id, model.Name);
            JsonObject result;
            if (adaptive)
            {
                var thinking = new JsonObject { ["type"] = "adaptive" };
                if (display is not null) thinking["display"] = display;
                if (binding) thinking["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" };
                result = new JsonObject { ["thinking"] = thinking, ["output_config"] = new JsonObject { ["effort"] = MapThinkingLevelToEffort(model, level) } };
                if (binding) result["anthropic_beta"] = new JsonArray(ThinkingBindingControlsBeta);
            }
            else
            {
                var defaults = new Dictionary<string, int> { ["minimal"] = 1024, ["low"] = 2048, ["medium"] = 8192, ["high"] = 16384, ["xhigh"] = 16384, ["max"] = 16384 };
                var budgetLevel = level is "xhigh" or "max" ? "high" : level;
                var budget = stream.ThinkingBudgets is { } custom && custom.TryGetValue(budgetLevel, out var configured) ? configured : defaults[level];
                var thinking = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
                if (display is not null) thinking["display"] = display;
                result = new JsonObject { ["thinking"] = thinking };
            }
            if (!adaptive && (options.InterleavedThinking ?? true)) result["anthropic_beta"] = new JsonArray("interleaved-thinking-2025-05-14");
            return result;
        }
        var candidates = MatchCandidates(model.Id, model.Name);
        if (candidates.Any(value => value.Contains("gpt-oss", StringComparison.Ordinal)))
            return new JsonObject { ["reasoning_effort"] = GptOssEffort[level] };
        if (candidates.Any(value => value.Contains("gpt-", StringComparison.Ordinal)))
        {
            var (_, mapped) = ProviderTranscript.MappedLevel(model.Raw, level);
            return new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = mapped ?? GptEffort[level] } };
        }
        return null;
    }

    private static string MapThinkingLevelToEffort(BedrockModel model, string level)
    {
        if (level == "xhigh" && SupportsNativeXhighEffort(model)) return "xhigh";
        var (_, mapped) = ProviderTranscript.MappedLevel(model.Raw, level);
        if (mapped is not null) return mapped;
        return level switch { "minimal" or "low" => "low", "medium" => "medium", _ => "high" };
    }

    /// <summary>getModelMatchCandidates: id and name, lower-cased, plus [\s_.:]+ collapsed to "-".</summary>
    internal static List<string> MatchCandidates(string id, string? name)
    {
        var values = name is { Length: > 0 } ? new[] { id, name } : [id];
        return values.SelectMany(value => { var lower = value.ToLowerInvariant(); return new[] { lower, Separators().Replace(lower, "-") }; }).ToList();
    }
    [GeneratedRegex(@"[\s_.:]+")] private static partial Regex Separators();

    internal static bool SupportsAdaptiveThinking(string id, string? name) => MatchCandidates(id, name).Any(value =>
        value.Contains("opus-4-6") || value.Contains("opus-4-7") || value.Contains("opus-4-8") || value.Contains("opus-5") ||
        value.Contains("sonnet-4-6") || value.Contains("sonnet-5") || value.Contains("haiku-5") || value.Contains("fable-5"));

    private static bool SupportsNativeXhighEffort(BedrockModel model) => MatchCandidates(model.Id, model.Name).Any(value =>
        value.Contains("opus-4-7") || value.Contains("opus-4-8") || value.Contains("opus-5") || value.Contains("sonnet-5") ||
        value.Contains("haiku-5") || value.Contains("fable-5"));

    private static bool SupportsThinkingBlockBinding(BedrockModel model) => SupportsNativeXhighEffort(model);

    internal static bool IsAnthropicClaude(BedrockModel model)
    {
        var id = model.Id.ToLowerInvariant(); var name = model.Name?.ToLowerInvariant() ?? "";
        return id.Contains("anthropic.claude") || id.Contains("anthropic/claude") || name.Contains("anthropic.claude") ||
            name.Contains("anthropic/claude") || name.Contains("claude");
    }

    private static bool SupportsPromptCaching(BedrockModel model, Func<string, string?> env)
    {
        var candidates = MatchCandidates(model.Id, model.Name);
        if (!candidates.Any(value => value.Contains("claude"))) return env("AWS_BEDROCK_FORCE_CACHE") == "1";
        return candidates.Any(value => value.Contains("fable-5") || value.Contains("opus-5") || value.Contains("sonnet-5") || value.Contains("haiku-5")) ||
            candidates.Any(value => value.Contains("-4-")) || candidates.Any(value => value.Contains("claude-3-7-sonnet")) ||
            candidates.Any(value => value.Contains("claude-3-5-haiku"));
    }
}
