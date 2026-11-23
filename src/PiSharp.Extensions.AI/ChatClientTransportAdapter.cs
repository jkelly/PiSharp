using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
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
/// Reverse bridge exposing any <see cref="Microsoft.Extensions.AI.IChatClient"/> as a PiSharp <see cref="IChatTransport"/>,
/// enabling <c>PiSharp.Agent</c>, <c>PiSharp.CodingAgent</c>, <c>PiSharp.Durable</c> and <c>PiSharp.CustomAgents</c> to
/// run over any M.E.AI-compatible provider or middleware pipeline.
/// </summary>
public sealed class ChatClientTransportAdapter : IChatTransport, IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly ChatClientTransportAdapterOptions _options;
    private int _disposed;

    public ChatClientTransportAdapter(IChatClient chatClient, ChatClientTransportAdapterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        _chatClient = chatClient;
        _options = options ?? new();
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StreamEvent> StreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);

        var (messages, chatOptions) = ConvertRequest(request);
        var model = request.Model;
        var timestamp = request.Timestamp;

        yield return new StreamStarted(new AssistantMessage(
            model.Api, model.Provider, model.Id, timestamp, [], TokenUsage.Zero, StopReason.Pending));

        var blocks = new List<AssistantContent>();
        var usedCallIds = new HashSet<string>(StringComparer.Ordinal);
        var nextIndex = 0;
        int? openTextIndex = null;
        var textBuffer = new StringBuilder();
        int? openThinkingIndex = null;
        var thinkingBuffer = new StringBuilder();
        string? thinkingSignature = null;
        ChatFinishReason? finishReason = null;
        long inputTokens = 0;
        long outputTokens = 0;
        long cacheReadTokens = 0;
        long cacheWriteTokens = 0;
        long totalTokens = 0;
        long reasoningTokens = 0;
        string? streamErrorMessage = null;
        var aborted = false;

        IAsyncEnumerator<ChatResponseUpdate>? enumerator = null;
        try
        {
            enumerator = _chatClient.GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    aborted = true;
                    streamErrorMessage = ex.Message;
                    break;
                }
                catch (Exception ex)
                {
                    streamErrorMessage = ex.Message;
                    break;
                }

                if (!hasNext) break;
                var update = enumerator.Current;
                if (update is null) continue;

                if (update.FinishReason is { } reason)
                    finishReason = reason;

                foreach (var content in update.Contents)
                {
                    switch (content)
                    {
                        case AiTextContent text when !string.IsNullOrEmpty(text.Text):
                        {
                            if (openThinkingIndex is { } tIdx)
                            {
                                var endedThinking = CloseThinking(tIdx, thinkingBuffer, thinkingSignature, blocks);
                                openThinkingIndex = null;
                                thinkingSignature = null;
                                yield return endedThinking;
                            }
                            if (openTextIndex is null)
                            {
                                openTextIndex = nextIndex++;
                                textBuffer.Clear();
                                yield return new TextStarted(openTextIndex.Value, new PiTextContent(string.Empty));
                            }
                            textBuffer.Append(text.Text);
                            yield return new TextDelta(openTextIndex.Value, text.Text);
                            break;
                        }

                        case TextReasoningContent reasoning:
                        {
                            if (string.IsNullOrEmpty(reasoning.Text) && string.IsNullOrEmpty(reasoning.ProtectedData))
                                break;
                            if (openTextIndex is { } txtIdx)
                            {
                                var endedText = CloseText(txtIdx, textBuffer, blocks);
                                openTextIndex = null;
                                yield return endedText;
                            }
                            if (openThinkingIndex is null)
                            {
                                openThinkingIndex = nextIndex++;
                                thinkingBuffer.Clear();
                                thinkingSignature = null;
                                yield return new ThinkingStarted(openThinkingIndex.Value, new ThinkingContent(string.Empty));
                            }
                            if (!string.IsNullOrEmpty(reasoning.ProtectedData))
                                thinkingSignature = reasoning.ProtectedData;
                            if (!string.IsNullOrEmpty(reasoning.Text))
                            {
                                thinkingBuffer.Append(reasoning.Text);
                                yield return new ThinkingDelta(openThinkingIndex.Value, reasoning.Text);
                            }
                            break;
                        }

                        case FunctionCallContent call:
                        {
                            if (openTextIndex is { } txtIdx)
                            {
                                var endedText = CloseText(txtIdx, textBuffer, blocks);
                                openTextIndex = null;
                                yield return endedText;
                            }
                            if (openThinkingIndex is { } tIdx)
                            {
                                var endedThinking = CloseThinking(tIdx, thinkingBuffer, thinkingSignature, blocks);
                                openThinkingIndex = null;
                                thinkingSignature = null;
                                yield return endedThinking;
                            }

                            var idx = nextIndex++;
                            var callId = AllocateCallId(call.CallId, idx, usedCallIds);
                            var callName = string.IsNullOrWhiteSpace(call.Name) ? $"tool_{idx}" : call.Name;
                            var argsNode = PiChatClient.SerializeArgumentsToNode(call.Arguments);
                            var argsJson = argsNode.ToJsonString();
                            var argsData = JsonData.Parse(argsJson);
                            var finalCall = new PiToolCallContent(callId, callName, argsData);

                            yield return new ToolCallStarted(idx, new PiToolCallContent(callId, callName, JsonData.EmptyObject));
                            yield return new ToolCallDelta(idx, argsJson);
                            yield return new ToolCallEnded(idx, finalCall);
                            blocks.Add(finalCall);
                            break;
                        }

                        case UsageContent usage when usage.Details is { } d:
                            if (d.InputTokenCount is { } inp) inputTokens += inp;
                            if (d.OutputTokenCount is { } outp) outputTokens += outp;
                            if (d.CachedInputTokenCount is { } cached) cacheReadTokens += cached;
                            if (d.ReasoningTokenCount is { } reas) reasoningTokens += reas;
                            if (d.TotalTokenCount is { } tot) totalTokens += tot;
                            if (d.AdditionalCounts is { } extra && extra.TryGetValue("cacheWriteTokens", out var cw))
                                cacheWriteTokens += cw;
                            break;

                        case ErrorContent err:
                            streamErrorMessage = string.IsNullOrWhiteSpace(err.Message) ? "Provider reported an error." : err.Message;
                            break;
                    }

                    if (streamErrorMessage is not null) break;
                }

                if (streamErrorMessage is not null) break;
            }
        }
        finally
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        var tokenUsage = BuildTokenUsage(inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, totalTokens, reasoningTokens);

        if (aborted || streamErrorMessage is not null)
        {
            var reason = aborted || cancellationToken.IsCancellationRequested ? StopReason.Aborted : StopReason.Error;
            var errorMsg = string.IsNullOrWhiteSpace(streamErrorMessage)
                ? (reason == StopReason.Aborted ? "Chat request aborted." : "Chat request failed.")
                : streamErrorMessage;
            var extra = JsonFields.Empty.Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(errorMsg)));
            yield return new StreamError(reason, new AssistantMessage(
                model.Api, model.Provider, model.Id, timestamp, [.. blocks], tokenUsage, reason, extra));
            yield break;
        }

        if (openTextIndex is { } remainingTextIdx)
            yield return CloseText(remainingTextIdx, textBuffer, blocks);
        if (openThinkingIndex is { } remainingThinkingIdx)
            yield return CloseThinking(remainingThinkingIdx, thinkingBuffer, thinkingSignature, blocks);

        var finalStopReason = ResolveStopReason(finishReason, blocks);
        var finalMessage = new AssistantMessage(
            model.Api, model.Provider, model.Id, timestamp, [.. blocks], tokenUsage, finalStopReason);
        yield return new StreamDone(finalStopReason, finalMessage);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_options.DisposeChatClient)
            _chatClient.Dispose();
    }

    private (List<ChatMessage> Messages, ChatOptions Options) ConvertRequest(ChatRequest request)
    {
        var chatOptions = _options.BaseOptions?.Clone() ?? new ChatOptions();
        chatOptions.ModelId = request.Model.Id;
        if (!string.IsNullOrEmpty(request.SessionId))
            chatOptions.ConversationId = request.SessionId;
        if (MapThinkingLevel(request.ThinkingLevel) is { } effort)
            chatOptions.Reasoning = new ReasoningOptions { Effort = effort };

        var additional = chatOptions.AdditionalProperties ??= [];
        if (!string.IsNullOrEmpty(request.ThinkingLevel))
            additional[PiChatAdditionalProperties.ThinkingLevel] = request.ThinkingLevel;
        if (!string.IsNullOrEmpty(request.ServiceTier))
            additional[PiChatAdditionalProperties.ServiceTier] = request.ServiceTier;
        if (!string.IsNullOrEmpty(request.SessionId))
            additional[PiChatAdditionalProperties.SessionId] = request.SessionId;
        if (request.DisableContinuation)
            additional[PiChatAdditionalProperties.DisableContinuation] = true;
        if (request.Identity is not null)
            additional[PiChatAdditionalProperties.Identity] = request.Identity;

        var systemParts = new List<string>();
        var sections = new List<KeyValuePair<string, string>>();
        var tools = new List<JsonObject>();
        var conversation = new List<ChatMessage>();

        foreach (var entry in request.Messages.IsDefault ? [] : request.Messages)
        {
            if (entry?.WireBody is not { } wire) continue;
            if (JsonNode.Parse(wire.ToString(), documentOptions: JsonData.DocumentOptions) is not JsonObject body)
                continue;

            var role = entry.Role;
            if (role == "system")
            {
                var text = ExtractContentText(body["content"]);
                if (text.Length > 0) systemParts.Add(text);
                if (body["sections"] is JsonObject patch)
                {
                    foreach (var (name, value) in patch)
                    {
                        var idx = sections.FindIndex(pair => pair.Key == name);
                        if (value is null)
                        {
                            if (idx >= 0) sections.RemoveAt(idx);
                        }
                        else if (value is JsonValue str && str.TryGetValue<string>(out var updated))
                        {
                            if (idx >= 0) sections[idx] = new(name, updated);
                            else sections.Add(new(name, updated));
                        }
                    }
                }
                ApplyToolDeltas(body, tools);
            }
            else if (role == "user")
            {
                conversation.Add(ConvertUserMessage(body));
            }
            else if (role == "assistant")
            {
                var stopReason = GetString(body, "stopReason");
                if (stopReason is "error" or "aborted") continue;
                conversation.Add(ConvertAssistantMessage(body));
            }
            else if (role == "toolResult")
            {
                conversation.Add(ConvertToolResultMessage(body));
            }
        }

        foreach (var (_, sectionText) in sections)
            if (sectionText.Length > 0) systemParts.Add(sectionText);

        var messages = new List<ChatMessage>(conversation.Count + 1);
        var fullSystem = string.Join("\n\n", systemParts);
        if (fullSystem.Length > 0)
            messages.Add(new ChatMessage(ChatRole.System, fullSystem));
        messages.AddRange(conversation);

        if (tools.Count > 0)
        {
            var toolList = chatOptions.Tools ??= [];
            foreach (var tool in tools)
            {
                var name = GetString(tool, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var description = GetString(tool, "description");
                var schemaText = tool["parameters"]?.ToJsonString() ?? "{\"type\":\"object\",\"properties\":{}}";
                using var doc = JsonDocument.Parse(schemaText, JsonData.DocumentOptions);
                toolList.Add(AIFunctionFactory.CreateDeclaration(name, description, doc.RootElement.Clone()));
            }
        }

        return (messages, chatOptions);
    }

    private static void ApplyToolDeltas(JsonObject systemBody, List<JsonObject> tools)
    {
        if (systemBody["toolsRemoved"] is JsonArray removed)
        {
            foreach (var item in removed.OfType<JsonObject>())
            {
                var name = GetString(item, "name");
                if (name is not null) tools.RemoveAll(existing => GetString(existing, "name") == name);
            }
        }
        if (systemBody["toolsAdded"] is JsonArray added)
        {
            foreach (var item in added.OfType<JsonObject>())
            {
                var name = GetString(item, "name");
                var idx = name is null ? -1 : tools.FindIndex(existing => GetString(existing, "name") == name);
                if (idx >= 0) tools[idx] = (JsonObject)item.DeepClone();
                else tools.Add((JsonObject)item.DeepClone());
            }
        }
    }

    private static ChatMessage ConvertUserMessage(JsonObject body)
    {
        var contentNode = body["content"];
        if (contentNode is JsonValue val && val.TryGetValue<string>(out var plain))
            return new ChatMessage(ChatRole.User, plain);

        var contents = new List<AIContent>();
        if (contentNode is JsonArray blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>())
            {
                var type = GetString(block, "type");
                if (type == "text")
                {
                    contents.Add(new AiTextContent(GetString(block, "text") ?? string.Empty));
                }
                else if (type == "image" && GetString(block, "data") is { Length: > 0 } base64)
                {
                    var mimeType = GetString(block, "mimeType") ?? "image/png";
                    try { contents.Add(new DataContent(Convert.FromBase64String(base64), mimeType)); }
                    catch (FormatException) { contents.Add(new AiTextContent("(invalid image data omitted)")); }
                }
            }
        }
        return new ChatMessage(ChatRole.User, contents);
    }

    private static ChatMessage ConvertAssistantMessage(JsonObject body)
    {
        var contents = new List<AIContent>();
        if (body["content"] is JsonArray blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>())
            {
                var type = GetString(block, "type");
                if (type == "text")
                {
                    contents.Add(new AiTextContent(GetString(block, "text") ?? string.Empty));
                }
                else if (type == "thinking")
                {
                    contents.Add(new TextReasoningContent(GetString(block, "thinking") ?? string.Empty)
                    {
                        ProtectedData = GetString(block, "thinkingSignature")
                    });
                }
                else if (type == "toolCall")
                {
                    var id = GetString(block, "id") ?? string.Empty;
                    var name = GetString(block, "name") ?? string.Empty;
                    var argsJson = block["arguments"]?.ToJsonString() ?? "{}";
                    var argsDict = PiChatClient.ParseArgumentsDictionary(JsonData.Parse(argsJson));
                    contents.Add(new FunctionCallContent(id, name, argsDict));
                }
            }
        }
        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static ChatMessage ConvertToolResultMessage(JsonObject body)
    {
        var callId = GetString(body, "toolCallId") ?? string.Empty;
        var isError = body["isError"] is JsonValue flag && flag.TryGetValue<bool>(out var err) && err;
        var text = ExtractContentText(body["content"]);
        var items = new List<AIContent>();
        if (body["content"] is JsonArray blocks)
        {
            foreach (var block in blocks.OfType<JsonObject>())
            {
                var type = GetString(block, "type");
                if (type == "text")
                    items.Add(new AiTextContent(GetString(block, "text") ?? string.Empty));
                else if (type == "image" && GetString(block, "data") is { Length: > 0 } base64)
                {
                    var mimeType = GetString(block, "mimeType") ?? "image/png";
                    try { items.Add(new DataContent(Convert.FromBase64String(base64), mimeType)); }
                    catch (FormatException) { /* omit malformed image */ }
                }
            }
        }

        object? payload = items.Any(c => c is DataContent) ? items : text;
        var resultContent = new FunctionResultContent(callId, payload);
        if (isError) resultContent.Exception = new InvalidOperationException(text.Length > 0 ? text : "Tool execution failed.");
        return new ChatMessage(ChatRole.Tool, [resultContent]);
    }

    private static TextEnded CloseText(int index, StringBuilder buffer, List<AssistantContent> blocks)
    {
        var text = buffer.ToString();
        buffer.Clear();
        blocks.Add(new PiTextContent(text));
        return new TextEnded(index, text);
    }

    private static ThinkingEnded CloseThinking(int index, StringBuilder buffer, string? signature, List<AssistantContent> blocks)
    {
        var text = buffer.ToString();
        buffer.Clear();
        var props = string.IsNullOrEmpty(signature)
            ? null
            : JsonFields.Empty.Set("thinkingSignature", JsonData.Parse(JsonSerializer.Serialize(signature)));
        blocks.Add(new ThinkingContent(text, props));
        return new ThinkingEnded(index, text, props);
    }

    private static string AllocateCallId(string? preferred, int index, HashSet<string> used)
    {
        var candidate = string.IsNullOrWhiteSpace(preferred) ? $"call_{index}" : preferred;
        if (used.Add(candidate)) return candidate;
        for (var suffix = 1; ; suffix++)
        {
            var unique = $"{candidate}_{suffix}";
            if (used.Add(unique)) return unique;
        }
    }

    private static StopReason ResolveStopReason(ChatFinishReason? finishReason, List<AssistantContent> blocks)
    {
        if (finishReason == ChatFinishReason.Length) return StopReason.Length;
        if (finishReason == ChatFinishReason.ToolCalls) return StopReason.ToolUse;
        if (blocks.Any(static b => b is PiToolCallContent)) return StopReason.ToolUse;
        return StopReason.Stop;
    }

    private static TokenUsage BuildTokenUsage(
        long input, long output, long cacheRead, long cacheWrite, long total, long reasoning)
    {
        if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0 && total == 0 && reasoning == 0)
            return TokenUsage.Zero;

        var computedTotal = total > 0 ? total : input + output + cacheRead + cacheWrite;
        var extra = reasoning > 0
            ? JsonFields.Empty.Set("reasoning", JsonData.Parse(JsonSerializer.Serialize(reasoning)))
            : null;
        return new TokenUsage(input, output, cacheRead, cacheWrite, computedTotal, new UsageCost(0, 0, 0, 0, 0), extra);
    }

    private static ReasoningEffort? MapThinkingLevel(string? level) => level?.Trim().ToLowerInvariant() switch
    {
        "off" => ReasoningEffort.None,
        "minimal" or "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        "xhigh" or "max" => ReasoningEffort.ExtraHigh,
        _ => null
    };

    private static string ExtractContentText(JsonNode? node) => node switch
    {
        null => string.Empty,
        JsonValue val when val.TryGetValue<string>(out var text) => text,
        JsonArray array => string.Join("\n", array.OfType<JsonObject>()
            .Where(b => GetString(b, "type") == "text")
            .Select(b => GetString(b, "text") ?? string.Empty)),
        _ => string.Empty
    };

    private static string? GetString(JsonObject obj, string name) =>
        obj[name] is JsonValue val && val.TryGetValue<string>(out var text) ? text : null;
}
