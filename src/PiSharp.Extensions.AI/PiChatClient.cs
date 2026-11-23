using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using PiSharp.AI;
using PiSharp.Contracts;
using AiTextContent = Microsoft.Extensions.AI.TextContent;
using PiTextContent = PiSharp.Contracts.TextContent;
using PiToolCallContent = PiSharp.Contracts.ToolCallContent;

namespace PiSharp.Extensions.AI;

/// <summary>
/// Exposes any PiSharp <see cref="IChatTransport"/> as a <see cref="Microsoft.Extensions.AI.IChatClient"/>,
/// bridging PiSharp's streaming providers, thinking levels, tool calls, images and service tiers into the
/// standard .NET AI middleware ecosystem.
/// </summary>
public sealed class PiChatClient : IChatClient
{
    private readonly IChatTransport _transport;
    private readonly ModelDescriptor _model;
    private readonly PiChatClientOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ChatClientMetadata _metadata;
    private int _disposed;

    public PiChatClient(IChatTransport transport, ModelDescriptor model, PiChatClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(model);
        _transport = transport;
        _model = model;
        _options = options ?? new();
        _timeProvider = _options.TimeProvider ?? TimeProvider.System;
        _metadata = new(model.Provider, _options.ProviderUri, model.Id);
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var request = BuildRequest(messages, options);
        var responseId = Guid.NewGuid().ToString("N");
        AssistantMessage? completed = null;
        StopReason terminalReason = StopReason.Stop;

        await foreach (var ev in _transport.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (ev)
            {
                case StreamDone done:
                    completed = done.Message;
                    terminalReason = done.Reason;
                    break;
                case StreamError error:
                    ThrowForStreamError(error, cancellationToken);
                    break;
            }
            if (completed is not null) break;
        }

        if (completed is null)
            throw new InvalidOperationException("PiSharp transport stream ended without a terminal event.");

        var contents = new List<AIContent>(completed.Content.Length);
        foreach (var block in completed.Content)
        {
            var mapped = MapAssistantContent(block);
            if (mapped is not null) contents.Add(mapped);
        }

        var message = new ChatMessage(ChatRole.Assistant, contents)
        {
            MessageId = responseId,
            CreatedAt = ToTimestamp(completed.Timestamp),
            RawRepresentation = completed
        };

        return new ChatResponse(message)
        {
            ResponseId = responseId,
            ConversationId = request.SessionId,
            ModelId = completed.Model,
            CreatedAt = ToTimestamp(completed.Timestamp),
            FinishReason = MapStopReason(terminalReason),
            Usage = MapUsage(completed.Usage),
            RawRepresentation = completed
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var request = BuildRequest(messages, options);
        var responseId = Guid.NewGuid().ToString("N");
        var modelId = request.Model.Id;
        DateTimeOffset? createdAt = ToTimestamp(request.Timestamp);
        var streamedBlocks = new HashSet<int>();
        var emittedToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var sawTerminal = false;

        await foreach (var ev in _transport.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (ev)
            {
                case StreamStarted started:
                    modelId = started.Partial.Model;
                    createdAt = ToTimestamp(started.Partial.Timestamp);
                    break;

                case TextStarted started when !string.IsNullOrEmpty(started.Content.Text):
                    streamedBlocks.Add(started.ContentIndex);
                    yield return CreateUpdate(responseId, request.SessionId, modelId, createdAt, new AiTextContent(started.Content.Text), started);
                    break;

                case TextDelta delta when !string.IsNullOrEmpty(delta.Delta):
                    streamedBlocks.Add(delta.ContentIndex);
                    yield return CreateUpdate(responseId, request.SessionId, modelId, createdAt, new AiTextContent(delta.Delta), delta);
                    break;

                case TextEnded ended:
                    if (!streamedBlocks.Contains(ended.ContentIndex) && !string.IsNullOrEmpty(ended.Content))
                    {
                        streamedBlocks.Add(ended.ContentIndex);
                        yield return CreateUpdate(responseId, request.SessionId, modelId, createdAt, new AiTextContent(ended.Content), ended);
                    }
                    break;

                case ThinkingStarted started when !string.IsNullOrEmpty(started.Content.Thinking):
                    streamedBlocks.Add(started.ContentIndex);
                    yield return CreateUpdate(responseId, request.SessionId, modelId, createdAt,
                        CreateReasoningContent(started.Content.Thinking, started.Content.ExtraProperties), started);
                    break;

                case ThinkingDelta delta when !string.IsNullOrEmpty(delta.Delta):
                    streamedBlocks.Add(delta.ContentIndex);
                    yield return CreateUpdate(responseId, request.SessionId, modelId, createdAt,
                        new TextReasoningContent(delta.Delta), delta);
                    break;

                case ThinkingEnded ended:
                {
                    var signature = ExtractString(ended.Properties, "thinkingSignature");
                    if (!streamedBlocks.Contains(ended.ContentIndex) && (!string.IsNullOrEmpty(ended.Content) || signature is not null))
                    {
                        streamedBlocks.Add(ended.ContentIndex);
                        yield return CreateUpdate(responseId, request.SessionId, modelId, createdAt,
                            new TextReasoningContent(ended.Content) { ProtectedData = signature }, ended);
                    }
                    else if (signature is not null)
                    {
                        yield return CreateUpdate(responseId, request.SessionId, modelId, createdAt,
                            new TextReasoningContent(string.Empty) { ProtectedData = signature }, ended);
                    }
                    break;
                }

                case ToolCallEnded ended:
                    streamedBlocks.Add(ended.ContentIndex);
                    emittedToolCallIds.Add(ended.ToolCall.Id);
                    yield return CreateUpdate(responseId, request.SessionId, modelId, createdAt,
                        MapToolCall(ended.ToolCall), ended);
                    break;

                case ContentBlockFinalized finalized:
                    if (streamedBlocks.Add(finalized.ContentIndex))
                    {
                        if (finalized.Content is PiToolCallContent finalizedTool)
                            emittedToolCallIds.Add(finalizedTool.Id);
                        var mapped = MapAssistantContent(finalized.Content);
                        if (mapped is not null)
                            yield return CreateUpdate(responseId, request.SessionId, modelId, createdAt, mapped, finalized);
                    }
                    break;

                case StreamDone done:
                {
                    sawTerminal = true;
                    modelId = done.Message.Model;
                    createdAt = ToTimestamp(done.Message.Timestamp);

                    // Emit any content blocks present in the final message that were never streamed individually.
                    for (var i = 0; i < done.Message.Content.Length; i++)
                    {
                        if (streamedBlocks.Contains(i)) continue;
                        var block = done.Message.Content[i];
                        if (block is PiToolCallContent tc && !emittedToolCallIds.Add(tc.Id)) continue;
                        var mapped = MapAssistantContent(block);
                        if (mapped is not null)
                            yield return CreateUpdate(responseId, request.SessionId, modelId, createdAt, mapped, done);
                    }

                    var terminalContents = new List<AIContent>(1);
                    if (MapUsage(done.Message.Usage) is { } usage)
                        terminalContents.Add(new UsageContent(usage));

                    yield return new ChatResponseUpdate(ChatRole.Assistant, terminalContents)
                    {
                        ResponseId = responseId,
                        MessageId = responseId,
                        ConversationId = request.SessionId,
                        ModelId = modelId,
                        CreatedAt = createdAt,
                        FinishReason = MapStopReason(done.Reason),
                        RawRepresentation = done.Message
                    };
                    yield break;
                }

                case StreamError error:
                    sawTerminal = true;
                    ThrowForStreamError(error, cancellationToken);
                    yield break;
            }
        }

        if (!sawTerminal)
            throw new InvalidOperationException("PiSharp transport stream ended without a terminal event.");
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType == typeof(ChatClientMetadata)) return _metadata;
        if (serviceType.IsInstanceOfType(this)) return this;
        if (serviceType.IsInstanceOfType(_transport)) return _transport;
        if (serviceType == typeof(ModelDescriptor)) return _model;
        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_options.DisposeTransport && _transport is IDisposable disposable)
            disposable.Dispose();
    }

    internal ChatRequest BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var effectiveModel = !string.IsNullOrWhiteSpace(options?.ModelId)
            ? _model with { Id = options.ModelId }
            : _model;
        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        var transcript = ImmutableArray.CreateBuilder<TranscriptEntry>();
        var toolsAdded = BuildToolsArray(options?.Tools);
        if (!string.IsNullOrEmpty(options?.Instructions) || toolsAdded is not null)
        {
            var systemObj = new JsonObject
            {
                ["role"] = "system",
                ["content"] = options?.Instructions ?? string.Empty,
                ["timestamp"] = timestamp
            };
            if (toolsAdded is not null) systemObj["toolsAdded"] = toolsAdded;
            transcript.Add(new TranscriptEntry("system", JsonData.Parse(systemObj.ToJsonString())));
        }

        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message is null) continue;
            AppendMessageEntries(message, effectiveModel, timestamp, toolNamesByCallId, transcript);
        }

        return new ChatRequest(effectiveModel, transcript.ToImmutable(), timestamp)
        {
            ThinkingLevel = ResolveThinkingLevel(options),
            ServiceTier = ResolveStringOption(options, PiChatAdditionalProperties.ServiceTier) ?? _options.ServiceTier,
            SessionId = ResolveStringOption(options, PiChatAdditionalProperties.SessionId) ?? options?.ConversationId ?? _options.SessionId,
            DisableContinuation = ResolveBoolOption(options, PiChatAdditionalProperties.DisableContinuation) ?? _options.DisableContinuation,
            Identity = ResolveIdentityOption(options) ?? _options.Identity
        };
    }

    private static void AppendMessageEntries(
        ChatMessage message,
        ModelDescriptor model,
        long timestamp,
        Dictionary<string, string> toolNamesByCallId,
        ImmutableArray<TranscriptEntry>.Builder transcript)
    {
        var messageTimestamp = message.CreatedAt?.ToUnixTimeMilliseconds() ?? timestamp;

        if (message.Role == ChatRole.System)
        {
            var systemObj = new JsonObject
            {
                ["role"] = "system",
                ["content"] = message.Text ?? string.Empty,
                ["timestamp"] = messageTimestamp
            };
            transcript.Add(new TranscriptEntry("system", JsonData.Parse(systemObj.ToJsonString())));
            return;
        }

        if (message.Role == ChatRole.Tool)
        {
            var results = message.Contents.OfType<FunctionResultContent>().ToList();
            if (results.Count > 0)
            {
                foreach (var result in results)
                    transcript.Add(BuildToolResultEntry(result, toolNamesByCallId, messageTimestamp));
                return;
            }

            var fallback = new JsonObject
            {
                ["role"] = "toolResult",
                ["toolCallId"] = string.Empty,
                ["toolName"] = string.Empty,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message.Text ?? string.Empty }),
                ["isError"] = false,
                ["timestamp"] = messageTimestamp
            };
            transcript.Add(new TranscriptEntry("toolResult", JsonData.Parse(fallback.ToJsonString())));
            return;
        }

        if (message.Role == ChatRole.Assistant)
        {
            var contentBlocks = new JsonArray();
            var hasToolCall = false;
            foreach (var item in message.Contents)
            {
                switch (item)
                {
                    case AiTextContent text:
                        contentBlocks.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text ?? string.Empty });
                        break;
                    case TextReasoningContent reasoning:
                    {
                        var block = new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning.Text ?? string.Empty };
                        if (!string.IsNullOrEmpty(reasoning.ProtectedData))
                            block["thinkingSignature"] = reasoning.ProtectedData;
                        contentBlocks.Add(block);
                        break;
                    }
                    case FunctionCallContent call:
                    {
                        hasToolCall = true;
                        var callId = call.CallId ?? string.Empty;
                        if (callId.Length > 0 && !string.IsNullOrEmpty(call.Name))
                            toolNamesByCallId[callId] = call.Name;
                        contentBlocks.Add(new JsonObject
                        {
                            ["type"] = "toolCall",
                            ["id"] = callId,
                            ["name"] = call.Name ?? string.Empty,
                            ["arguments"] = SerializeArgumentsToNode(call.Arguments)
                        });
                        break;
                    }
                }
            }

            var assistantObj = new JsonObject
            {
                ["role"] = "assistant",
                ["api"] = model.Api,
                ["provider"] = model.Provider,
                ["model"] = model.Id,
                ["content"] = contentBlocks,
                ["usage"] = new JsonObject
                {
                    ["input"] = 0,
                    ["output"] = 0,
                    ["cacheRead"] = 0,
                    ["cacheWrite"] = 0,
                    ["totalTokens"] = 0,
                    ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0, ["total"] = 0 }
                },
                ["stopReason"] = hasToolCall ? "toolUse" : "stop",
                ["timestamp"] = messageTimestamp
            };
            transcript.Add(new TranscriptEntry("assistant", JsonData.Parse(assistantObj.ToJsonString())));
            return;
        }

        // Default / User role (and any custom role projected as user).
        var blocks = new JsonArray();
        var trailingToolResults = new List<FunctionResultContent>();
        var hasImages = false;
        foreach (var item in message.Contents)
        {
            switch (item)
            {
                case AiTextContent text:
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text.Text ?? string.Empty });
                    break;
                case DataContent data when data.HasTopLevelMediaType("image"):
                {
                    var base64 = ExtractBase64(data);
                    if (base64 is not null)
                    {
                        hasImages = true;
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["data"] = base64,
                            ["mimeType"] = data.MediaType ?? "image/png"
                        });
                    }
                    break;
                }
                case FunctionResultContent result:
                    trailingToolResults.Add(result);
                    break;
            }
        }

        if (blocks.Count > 0 || trailingToolResults.Count == 0)
        {
            var userObj = new JsonObject
            {
                ["role"] = "user",
                ["content"] = hasImages
                    ? blocks
                    : blocks.Count == 1 && blocks[0] is JsonObject onlyText && onlyText["text"] is JsonValue tv && tv.TryGetValue<string>(out var single)
                        ? single
                        : blocks.Count == 0
                            ? message.Text ?? string.Empty
                            : blocks,
                ["timestamp"] = messageTimestamp
            };
            transcript.Add(new TranscriptEntry("user", JsonData.Parse(userObj.ToJsonString())));
        }

        foreach (var result in trailingToolResults)
            transcript.Add(BuildToolResultEntry(result, toolNamesByCallId, messageTimestamp));
    }

    private static TranscriptEntry BuildToolResultEntry(
        FunctionResultContent result,
        Dictionary<string, string> toolNamesByCallId,
        long timestamp)
    {
        var callId = result.CallId ?? string.Empty;
        toolNamesByCallId.TryGetValue(callId, out var toolName);
        var blocks = new JsonArray();
        var isError = result.Exception is not null;

        if (result.Exception is not null)
        {
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = result.Exception.Message });
        }
        else
        {
            AppendToolResultValue(result.Result, blocks);
        }

        if (blocks.Count == 0)
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = string.Empty });

        var obj = new JsonObject
        {
            ["role"] = "toolResult",
            ["toolCallId"] = callId,
            ["toolName"] = toolName ?? string.Empty,
            ["content"] = blocks,
            ["isError"] = isError,
            ["timestamp"] = timestamp
        };
        return new TranscriptEntry("toolResult", JsonData.Parse(obj.ToJsonString()));
    }

    private static void AppendToolResultValue(object? value, JsonArray blocks)
    {
        switch (value)
        {
            case null:
                return;
            case string text:
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                return;
            case AiTextContent textContent:
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = textContent.Text ?? string.Empty });
                return;
            case DataContent data when data.HasTopLevelMediaType("image"):
                if (ExtractBase64(data) is { } base64)
                    blocks.Add(new JsonObject { ["type"] = "image", ["data"] = base64, ["mimeType"] = data.MediaType ?? "image/png" });
                return;
            case IEnumerable<AIContent> items:
                foreach (var item in items) AppendToolResultValue(item, blocks);
                return;
            case JsonElement element:
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText() });
                return;
            case JsonData jsonData:
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = jsonData.Value.ValueKind == JsonValueKind.String ? jsonData.Value.GetString() ?? string.Empty : jsonData.ToString() });
                return;
            default:
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = JsonSerializer.Serialize(value) });
                return;
        }
    }

    private static JsonArray? BuildToolsArray(IList<AITool>? tools)
    {
        if (tools is null || tools.Count == 0) return null;
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            if (tool is not AIFunctionDeclaration fn) continue;
            JsonNode parameters;
            if (fn.JsonSchema.ValueKind == JsonValueKind.Object)
            {
                parameters = JsonNode.Parse(fn.JsonSchema.GetRawText(), documentOptions: JsonData.DocumentOptions)
                    ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            }
            else
            {
                parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            }

            array.Add(new JsonObject
            {
                ["name"] = fn.Name,
                ["description"] = fn.Description ?? string.Empty,
                ["parameters"] = parameters
            });
        }
        return array.Count > 0 ? array : null;
    }

    private string? ResolveThinkingLevel(ChatOptions? options)
    {
        if (ResolveStringOption(options, PiChatAdditionalProperties.ThinkingLevel) is { } explicitLevel)
            return explicitLevel;
        if (options?.Reasoning?.Effort is { } effort)
        {
            return effort switch
            {
                ReasoningEffort.None => "off",
                ReasoningEffort.Low => "low",
                ReasoningEffort.Medium => "medium",
                ReasoningEffort.High => "high",
                ReasoningEffort.ExtraHigh => "xhigh",
                _ => null
            };
        }
        return _options.ThinkingLevel;
    }

    private static string? ResolveStringOption(ChatOptions? options, string key) =>
        options?.AdditionalProperties is { } props && props.TryGetValue(key, out var raw) && raw is string text && text.Length > 0
            ? text : null;

    private static bool? ResolveBoolOption(ChatOptions? options, string key) =>
        options?.AdditionalProperties is { } props && props.TryGetValue(key, out var raw) && raw is bool flag
            ? flag : null;

    private static ClientIdentity? ResolveIdentityOption(ChatOptions? options) =>
        options?.AdditionalProperties is { } props && props.TryGetValue(PiChatAdditionalProperties.Identity, out var raw) && raw is ClientIdentity identity
            ? identity : null;

    private static ChatResponseUpdate CreateUpdate(
        string responseId,
        string? conversationId,
        string modelId,
        DateTimeOffset? createdAt,
        AIContent content,
        object raw) =>
        new(ChatRole.Assistant, [content])
        {
            ResponseId = responseId,
            MessageId = responseId,
            ConversationId = conversationId,
            ModelId = modelId,
            CreatedAt = createdAt,
            RawRepresentation = raw
        };

    private static AIContent? MapAssistantContent(AssistantContent content) => content switch
    {
        PiTextContent text => new AiTextContent(text.Text),
        ThinkingContent thinking => CreateReasoningContent(thinking.Thinking, thinking.ExtraProperties),
        PiToolCallContent toolCall => MapToolCall(toolCall),
        _ => null
    };

    private static TextReasoningContent CreateReasoningContent(string text, JsonFields? properties) =>
        new(text) { ProtectedData = ExtractString(properties, "thinkingSignature") };

    private static FunctionCallContent MapToolCall(PiToolCallContent toolCall) =>
        new(toolCall.Id, toolCall.Name, ParseArgumentsDictionary(toolCall.Arguments));

    internal static IDictionary<string, object?> ParseArgumentsDictionary(JsonData arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (arguments.Value.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in arguments.Value.EnumerateObject())
            result[prop.Name] = prop.Value.Clone();
        return result;
    }

    internal static JsonObject SerializeArgumentsToNode(IDictionary<string, object?>? arguments)
    {
        var obj = new JsonObject();
        if (arguments is null || arguments.Count == 0) return obj;
        foreach (var (key, value) in arguments)
        {
            obj[key] = value switch
            {
                null => null,
                JsonNode node => node.DeepClone(),
                JsonData data => JsonNode.Parse(data.ToString(), documentOptions: JsonData.DocumentOptions),
                JsonElement element => JsonNode.Parse(element.GetRawText(), documentOptions: JsonData.DocumentOptions),
                _ => JsonSerializer.SerializeToNode(value, JsonData.SerializerOptions)
            };
        }
        return obj;
    }

    private static UsageDetails? MapUsage(TokenUsage usage)
    {
        var reasoning = ExtractNumber(usage.ExtraProperties, "reasoning");
        if (usage.Input == 0 && usage.Output == 0 && usage.CacheRead == 0 && usage.CacheWrite == 0 && usage.TotalTokens == 0 && reasoning is null)
            return null;

        var details = new UsageDetails
        {
            InputTokenCount = ToLong(usage.Input),
            OutputTokenCount = ToLong(usage.Output),
            TotalTokenCount = ToLong(usage.TotalTokens > 0 ? usage.TotalTokens : usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite),
            CachedInputTokenCount = usage.CacheRead > 0 ? ToLong(usage.CacheRead) : null,
            ReasoningTokenCount = reasoning is > 0 ? ToLong(reasoning.Value) : null
        };
        if (usage.CacheWrite > 0)
            details.AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cacheWriteTokens"] = ToLong(usage.CacheWrite) };
        return details;
    }

    private static ChatFinishReason MapStopReason(StopReason reason) => reason switch
    {
        StopReason.Stop => ChatFinishReason.Stop,
        StopReason.Length => ChatFinishReason.Length,
        StopReason.ToolUse => ChatFinishReason.ToolCalls,
        StopReason.Deferred => new ChatFinishReason("deferred"),
        StopReason.Aborted => new ChatFinishReason("aborted"),
        StopReason.Error => new ChatFinishReason("error"),
        _ => ChatFinishReason.Stop
    };

    private static void ThrowForStreamError(StreamError error, CancellationToken cancellationToken)
    {
        var message = ExtractString(error.Message.ExtraProperties, "errorMessage")
            ?? (error.Reason == StopReason.Aborted ? "Chat request aborted." : "Chat request failed.");
        if (error.Reason == StopReason.Aborted)
            throw new OperationCanceledException(message, cancellationToken);
        throw new InvalidOperationException(message);
    }

    private static string? ExtractBase64(DataContent data)
    {
        if (!data.Data.IsEmpty) return Convert.ToBase64String(data.Data.Span);
        if (!data.Base64Data.IsEmpty) return data.Base64Data.ToString();
        return null;
    }

    private static string? ExtractString(JsonFields? fields, string name) =>
        fields is not null && fields.TryGet(name, out var value) && value is not null && value.Value.ValueKind == JsonValueKind.String
            ? value.Value.GetString() : null;

    private static double? ExtractNumber(JsonFields? fields, string name) =>
        fields is not null && fields.TryGet(name, out var value) && value is not null && value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out var number)
            ? number : null;

    private static long ToLong(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private static DateTimeOffset? ToTimestamp(long unixMilliseconds) =>
        unixMilliseconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds) : null;
}
