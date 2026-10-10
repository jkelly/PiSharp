using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAIResponses;

public sealed record ResponsesTranscriptProjectionOptions(
    bool Reasoning, bool SupportsDeveloperRole = true, bool SupportsMidConversationSystemMessages = false,
    bool IncludeInitialSystemPrompt = true, ImmutableHashSet<string>? AllowedToolCallProviders = null,
    int MaximumMessages = PiRequestBudget.RequestMessages, int MaximumEntryCharacters = PiRequestBudget.RequestEntryCharacters, int MaximumInputCharacters = PiRequestBudget.RequestPayloadBytes,
    int MaximumContentBlocks = PiRequestBudget.RequestItems, int MaximumJsonDepth = PiSharp.Contracts.JsonData.MaximumDepth,
    int MaximumOutputItems = PiRequestBudget.RequestItems, int MaximumOutputCharacters = PiRequestBudget.RequestPayloadBytes,
    ResponsesToolDeclarationProjectionOptions? ToolDeclarations = null,
    bool SynthesizeMissingToolResults = false)
{
    /// <summary>
    /// Null keeps the text-only profile (image blocks are unsupported content). True projects user and tool-result images as
    /// <c>input_image</c> data URLs; false downgrades them to Pi's non-vision placeholders
    /// (Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/openai-responses-shared.ts, api/transform-messages.ts).
    /// </summary>
    public bool? ModelSupportsImages { get; init; }
}

public enum ResponsesProjectionFailure
{
    InvalidTranscript, UnsupportedContent, UnsupportedNumber, UnsupportedUnicode, UnsupportedSignature,
    UnmatchedToolResult, IdentityCollision, ResourceLimit
}

public sealed class ResponsesProjectionException : Exception
{
    public ResponsesProjectionFailure Failure { get; }
    internal ResponsesProjectionException(ResponsesProjectionFailure failure) : base(failure switch
    {
        ResponsesProjectionFailure.ResourceLimit => "Responses transcript projection exceeds configured limits.",
        ResponsesProjectionFailure.UnmatchedToolResult => "Responses transcript contains unmatched or duplicate tool results.",
        ResponsesProjectionFailure.IdentityCollision => "Responses transcript tool identities are ambiguous.",
        ResponsesProjectionFailure.UnsupportedNumber => "Responses argument number is outside the supported projection profile.",
        ResponsesProjectionFailure.UnsupportedUnicode => "Responses transcript contains unsupported unpaired UTF-16.",
        ResponsesProjectionFailure.UnsupportedSignature => "Responses transcript contains an unsupported reasoning signature.",
        ResponsesProjectionFailure.UnsupportedContent => "Responses transcript contains unsupported content or declarations.",
        _ => "Invalid Responses transcript projection input."
    }) => Failure = failure;
}

/// <summary>Pure, bounded projection of a normalized transcript to a Responses input array. Never sends or executes.</summary>
public sealed class ResponsesTranscriptProjector
{
    private readonly ResponsesTranscriptProjectionOptions _options;
    private readonly ImmutableHashSet<string> _allowed;
    private readonly ResponsesToolDeclarationProjector _tools;
    private static readonly JsonSerializerOptions OutputJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 2 * JsonData.MaximumDepth };

    public ResponsesTranscriptProjector(ResponsesTranscriptProjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _tools = new(options.ToolDeclarations ?? new(MaximumMessages: options.MaximumMessages,
            MaximumEntryCharacters: options.MaximumEntryCharacters, MaximumInputCharacters: options.MaximumInputCharacters,
            MaximumJsonDepth: options.MaximumJsonDepth));
        if (options.MaximumMessages <= 0 || options.MaximumEntryCharacters <= 0 || options.MaximumInputCharacters <= 0 ||
            options.MaximumContentBlocks <= 0 || options.MaximumOutputItems <= 0 || options.MaximumOutputCharacters < 2 ||
            options.MaximumJsonDepth is < 1 or > PiSharp.Contracts.JsonData.MaximumDepth) throw new ArgumentOutOfRangeException(nameof(options), "Invalid Responses projection limits.");
        _allowed = (options.AllowedToolCallProviders ?? ImmutableHashSet.Create("openai", "openai-codex", "opencode"))
            .ToImmutableHashSet(StringComparer.Ordinal);
    }

    public JsonData Project(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = ProjectInput(request, cancellationToken);
            _ = ProjectTools(request, cancellationToken);
            return result;
        }
        catch (JsonException) { throw Failure(ResponsesProjectionFailure.InvalidTranscript); }
        catch (InvalidOperationException) { throw Failure(ResponsesProjectionFailure.InvalidTranscript); }
        catch (KeyNotFoundException) { throw Failure(ResponsesProjectionFailure.InvalidTranscript); }
        catch (FormatException) { throw Failure(ResponsesProjectionFailure.InvalidTranscript); }
        catch (OverflowException) { throw Failure(ResponsesProjectionFailure.InvalidTranscript); }
    }

    // The request factory uses both projections with this profile before allocating its request.
    internal JsonData ProjectInput(ChatRequest request, CancellationToken cancellationToken)
    {
        try { return new Projection(request, _options, _allowed, cancellationToken).Run(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw Failure(ResponsesProjectionFailure.InvalidTranscript); }
    }
    internal JsonData ProjectTools(ChatRequest request, CancellationToken cancellationToken) => _tools.Project(request, cancellationToken);

    private static ResponsesProjectionException Failure(ResponsesProjectionFailure failure) => new(failure);
    private sealed record Entry(string Role, JsonData Body, AssistantMessage? Assistant = null);
    private sealed class Projection(ChatRequest request, ResponsesTranscriptProjectionOptions options,
        ImmutableHashSet<string> allowed, CancellationToken token)
    {
        private readonly JsonArray _output = [];
        private int _outputCharacters = 2;
        private readonly Dictionary<string, string> _idMap = new(StringComparer.Ordinal);
        private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
        private Dictionary<string, string> _grammarInputs = new(StringComparer.Ordinal);

        public JsonData Run()
        {
            if (request.Messages.IsDefault || request.Model is null) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
            if (request.Messages.Length > options.MaximumMessages) throw Failure(ResponsesProjectionFailure.ResourceLimit);
            long characters = 0; var blocks = 0; var entries = new List<Entry>();
            foreach (var entry in request.Messages)
            {
                token.ThrowIfCancellationRequested();
                if (entry is null || entry.WireBody is null) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
                var body = entry.WireBody.Value; var length = body.GetRawText().Length;
                if (length > options.MaximumEntryCharacters || length > options.MaximumInputCharacters - characters)
                    throw Failure(ResponsesProjectionFailure.ResourceLimit);
                characters += length; CheckJson(body, 0);
                if (body.ValueKind != JsonValueKind.Object || String(body, "role") != entry.Role)
                    throw Failure(ResponsesProjectionFailure.InvalidTranscript);
                if (entry.Role is not ("system" or "user" or "assistant" or "toolResult")) throw Failure(ResponsesProjectionFailure.UnsupportedContent);
                if (body.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    if (content.GetArrayLength() > options.MaximumContentBlocks - blocks) throw Failure(ResponsesProjectionFailure.ResourceLimit);
                    blocks += content.GetArrayLength();
                }
                if (entry.Role == "assistant")
                {
                    if (content.ValueKind != JsonValueKind.Array) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
                    foreach (var part in content.EnumerateArray())
                        if (String(part, "type") is not ("text" or "thinking" or "toolCall")) throw Failure(ResponsesProjectionFailure.UnsupportedContent);
                    var assistant = PiWireJson.ReadMessage(body);
                    if (assistant.StopReason is StopReason.Pending or StopReason.Deferred) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
                    entries.Add(new(entry.Role, entry.WireBody, assistant));
                }
                else
                {
                    if (options.ModelSupportsImages is null || entry.Role == "system") _ = ContentText(body, entry.Role == "toolResult");
                    else _ = MediaParts(body, entry.Role == "toolResult");
                    entries.Add(new(entry.Role, entry.WireBody));
                }
            }
            // Pi abe508 openai-responses.ts: grammar input properties come from every tool the transcript declared.
            _grammarInputs = ResponsesGrammar.InputProperties(request, options.ToolDeclarations?.SupportsOpenAIGrammarTools == true, token);
            if (!options.SupportsMidConversationSystemMessages) entries = FoldSystems(entries);
            entries = Transform(entries);
            entries = PairAndOrder(entries);
            var messageIndex = 0; var sourceIndex = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var leadingSystem = sourceIndex++ == 0 && entry.Role == "system";
                if (entry.Role == "system")
                {
                    if (!leadingSystem || options.IncludeInitialSystemPrompt)
                    {
                        var text = RenderSystem(entry.Body.Value, leadingSystem);
                        if (text.Length > 0) Add(new JsonObject { ["role"] = options.Reasoning && options.SupportsDeveloperRole ? "developer" : "system", ["content"] = text });
                    }
                }
                else if (entry.Role == "user")
                {
                    var body = entry.Body.Value; var content = body.GetProperty("content"); var parts = new JsonArray();
                    if (content.ValueKind == JsonValueKind.String) parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = Text(content) });
                    else if (options.ModelSupportsImages is null)
                        foreach (var part in content.EnumerateArray()) parts.Add(new JsonObject { ["type"] = "input_text", ["text"] = String(part, "text") });
                    else foreach (var part in MediaParts(body, false))
                        parts.Add(part.Data is null ? new JsonObject { ["type"] = "input_text", ["text"] = part.Text }
                            : new JsonObject { ["type"] = "input_image", ["detail"] = "auto", ["image_url"] = "data:" + part.Text + ";base64," + part.Data });
                    if (parts.Count == 0) continue;
                    Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                }
                else if (entry.Role == "assistant")
                {
                    var before = _output.Count; ProjectAssistant(entry.Assistant!, messageIndex);
                    if (_output.Count == before) continue;
                }
                else
                {
                    Add(new JsonObject { ["type"] = _grammarInputs.ContainsKey(String(entry.Body.Value, "toolName")) ? "custom_tool_call_output" : "function_call_output",
                        ["call_id"] = CallParts(String(entry.Body.Value, "toolCallId")).Call, ["output"] = ToolOutput(entry.Body.Value) });
                }
                if (!leadingSystem) messageIndex++;
            }
            token.ThrowIfCancellationRequested();
            return JsonData.Parse(_output.ToJsonString(OutputJson));
        }

        private List<Entry> Transform(List<Entry> entries)
        {
            var transformed = new List<Entry>();
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Assistant is { } assistant && assistant.StopReason is not (StopReason.Error or StopReason.Aborted))
                {
                    var same = SameModel(assistant); var content = ImmutableArray.CreateBuilder<AssistantContent>();
                    foreach (var part in assistant.Content)
                        switch (part)
                        {
                            case ThinkingContent thinking:
                                var redacted = Bool(thinking.ExtraProperties, "redacted");
                                var signature = OptionalString(thinking.ExtraProperties, "thinkingSignature");
                                if (redacted && !same) break;
                                if (same && !string.IsNullOrEmpty(signature)) content.Add(thinking);
                                else if (redacted) throw Failure(ResponsesProjectionFailure.UnsupportedSignature);
                                else if (thinking.Thinking.Trim().Length != 0 && !same) content.Add(new TextContent(thinking.Thinking));
                                break;
                            case TextContent text: content.Add(same ? text : new TextContent(text.Text)); break;
                            case ToolCallContent tool:
                                // A nameless call, or one with empty id parts, replays with them (owner decision 13); calls with an empty
                                // call id may share it, as upstream sends them.
                                var id = same ? tool.Id : NormalizeId(tool.Id, assistant);
                                if (id != tool.Id) _idMap[tool.Id] = id;
                                var callPart = CallParts(id).Call;
                                if (callPart.Length > 0 && !_callIds.Add(callPart)) throw Failure(ResponsesProjectionFailure.IdentityCollision);
                                content.Add(new ToolCallContent(id, tool.Name, tool.Arguments, same ? tool.ExtraProperties : null));
                                break;
                        }
                    transformed.Add(entry with { Assistant = assistant with { Content = content.ToImmutable() } });
                }
                else if (entry.Role == "toolResult" && _idMap.TryGetValue(String(entry.Body.Value, "toolCallId"), out var id))
                {
                    var body = JsonNode.Parse(entry.Body.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!.AsObject(); body["toolCallId"] = id;
                    transformed.Add(entry with { Body = JsonData.Parse(body.ToJsonString(OutputJson)) });
                }
                else transformed.Add(entry);
            }
            return transformed;
        }

        private List<Entry> PairAndOrder(List<Entry> entries)
        {
            var result = new List<Entry>(); var pending = new List<(string Id, string Name)>();
            var matched = new HashSet<string>(StringComparer.Ordinal); var held = new List<Entry>();
            void Close()
            {
                // Pi d86654 transform-messages.ts closes orphaned calls before the next
                // user/assistant or EOF, then flushes transparent system updates.
                foreach (var (id, name) in pending)
                {
                    token.ThrowIfCancellationRequested();
                    if (matched.Contains(id)) continue;
                    if (!options.SynthesizeMissingToolResults) throw Failure(ResponsesProjectionFailure.UnmatchedToolResult);
                    var body = new JsonObject { ["role"] = "toolResult", ["toolCallId"] = id,
                        ["toolName"] = name, ["content"] = new JsonArray(new JsonObject
                        { ["type"] = "text", ["text"] = "No result provided" }), ["isError"] = true,
                        ["timestamp"] = request.Timestamp };
                    result.Add(new("toolResult", JsonData.Parse(body.ToJsonString(OutputJson))));
                }
                pending.Clear(); matched.Clear(); result.AddRange(held); held.Clear();
            }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.Assistant is { } assistant)
                {
                    Close();
                    if (assistant.StopReason is StopReason.Error or StopReason.Aborted) continue;
                    foreach (var tool in assistant.Content.OfType<ToolCallContent>())
                    {
                        if (!SharedCallId(tool.Id) && pending.Any(call => call.Id == tool.Id)) throw Failure(ResponsesProjectionFailure.IdentityCollision);
                        pending.Add((tool.Id, tool.Name));
                    }
                    result.Add(entry);
                }
                else if (entry.Role == "toolResult")
                {
                    var id = String(entry.Body.Value, "toolCallId");
                    var resultName = String(entry.Body.Value, "toolName");
                    if (!pending.Any(call => call.Id == id && call.Name == resultName) || !matched.Add(id) && !SharedCallId(id))
                        throw Failure(ResponsesProjectionFailure.UnmatchedToolResult);
                    result.Add(entry);
                }
                else if (entry.Role == "system" && pending.Count > 0) held.Add(entry);
                else { if (entry.Role == "user") Close(); result.Add(entry); }
            }
            Close(); return result;
        }

        private List<Entry> FoldSystems(List<Entry> entries)
        {
            var parts = new List<string>(); var order = new List<string>(); var sections = new Dictionary<string, string>(StringComparer.Ordinal);
            var hasSystem = false;
            foreach (var entry in entries.Where(entry => entry.Role == "system"))
            {
                token.ThrowIfCancellationRequested(); hasSystem = true;
                var text = ContentText(entry.Body.Value, false); if (text.Length > 0) parts.Add(text);
                if (entry.Body.Value.TryGetProperty("sections", out var updates))
                {
                    if (updates.ValueKind != JsonValueKind.Object) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
                    foreach (var property in Properties(updates))
                        if (property.Value.ValueKind == JsonValueKind.Null) { sections.Remove(property.Name); order.Remove(property.Name); }
                        else
                        {
                            if (!sections.ContainsKey(property.Name)) order.Add(property.Name);
                            sections[property.Name] = Text(property.Value);
                        }
                }
            }
            var result = entries.Where(entry => entry.Role != "system").ToList();
            if (hasSystem)
            {
                var sectionNode = new JsonObject(); foreach (var name in order) sectionNode[name] = sections[name];
                var body = new JsonObject { ["role"] = "system", ["content"] = string.Join("\n\n", parts), ["sections"] = sectionNode };
                result.Insert(0, new("system", JsonData.Parse(body.ToJsonString(OutputJson))));
            }
            return result;
        }

        private string RenderSystem(JsonElement body, bool leading)
        {
            var parts = new List<string>(); var content = ContentText(body, false); if (content.Length > 0) parts.Add(content);
            if (body.TryGetProperty("sections", out var sections))
            {
                if (sections.ValueKind != JsonValueKind.Object) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
                foreach (var property in Properties(sections))
                    if (leading) { if (property.Value.ValueKind != JsonValueKind.Null && Text(property.Value).Length > 0) parts.Add(Text(property.Value)); }
                    else parts.Add(property.Value.ValueKind == JsonValueKind.Null ? $"Removed system prompt section \"{property.Name}\"." :
                        $"Updated system prompt section \"{property.Name}\":\n\n{Text(property.Value)}");
            }
            return string.Join("\n\n", parts);
        }

        private void ProjectAssistant(AssistantMessage assistant, int messageIndex)
        {
            var same = SameModel(assistant); var different = assistant.Provider == request.Model.Provider && assistant.Api == request.Model.Api && !same;
            var textIndex = 0;
            foreach (var part in assistant.Content)
            {
                token.ThrowIfCancellationRequested();
                if (part is ThinkingContent thinking)
                {
                    var signature = OptionalString(thinking.ExtraProperties, "thinkingSignature");
                    if (string.IsNullOrEmpty(signature)) continue;
                    JsonData reasoning;
                    try { reasoning = JsonData.Parse(signature); }
                    catch (JsonException) { throw Failure(ResponsesProjectionFailure.UnsupportedSignature); }
                    CheckJson(reasoning.Value, 0);
                    if (reasoning.Value.ValueKind != JsonValueKind.Object || String(reasoning.Value, "type") != "reasoning")
                        throw Failure(ResponsesProjectionFailure.UnsupportedSignature);
                    Add(JsonNode.Parse(reasoning.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!);
                }
                else if (part is TextContent text)
                {
                    var (id, phase) = TextIdentity(OptionalString(text.ExtraProperties, "textSignature"));
                    if (string.IsNullOrEmpty(id)) id = textIndex == 0 ? $"msg_pi_{messageIndex}" : $"msg_pi_{messageIndex}_{textIndex}";
                    else if (id.Length > 64) id = "msg_" + ShortHash(id);
                    textIndex++;
                    var item = new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "output_text", ["text"] = text.Text, ["annotations"] = new JsonArray() }), ["status"] = "completed", ["id"] = id };
                    if (phase is not null) item["phase"] = phase;
                    Add(item);
                }
                else if (part is ToolCallContent tool)
                {
                    var (call, itemId) = CallParts(tool.Id);
                    // Pi abe508 (1.0.0) openai-responses-shared.ts: drop the item id of a different model, and any id that does not
                    // match the replayed item type: function_call ids must be fc_*, custom_tool_call ids ctc_*. Foreign ids were
                    // normalized to fc_*, and a call switches type when grammar tool support differs.
                    var grammar = _grammarInputs.TryGetValue(tool.Name, out var inputProperty);
                    if (different || itemId?.StartsWith(grammar ? "ctc_" : "fc_", StringComparison.Ordinal) != true) itemId = null;
                    JsonObject item;
                    if (grammar)
                    {
                        item = new JsonObject { ["type"] = "custom_tool_call" };
                        if (itemId is not null) item["id"] = itemId;
                        item["call_id"] = call; item["name"] = tool.Name; item["input"] = ResponsesGrammar.Input(tool.Arguments.Value, inputProperty!);
                    }
                    else
                    {
                        item = new JsonObject { ["type"] = "function_call", ["call_id"] = call, ["name"] = tool.Name, ["arguments"] = ArgumentString(tool.Arguments.Value) };
                        if (itemId is not null) item["id"] = itemId;
                    }
                    if (same && tool.ExtraProperties?.TryGet("namespace", out var ns) == true) item["namespace"] = JsonNode.Parse(ns!.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions);
                    Add(item);
                }
            }
        }

        private (string? Id, string? Phase) TextIdentity(string? signature)
        {
            if (string.IsNullOrEmpty(signature)) return (null, null);
            if (signature.StartsWith('{'))
            {
                JsonDocument document;
                try { document = JsonDocument.Parse(signature, JsonData.DocumentOptions); }
                catch (JsonException) { return (signature, null); }
                using (document)
                {
                    var value = document.RootElement; CheckJson(value, 0); _ = JsonData.FromElement(value);
                    if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("v", out var version) && version.ValueKind == JsonValueKind.Number &&
                        version.TryGetDouble(out var v) && v == 1 && value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    {
                        var phase = value.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String ? Text(p) : null;
                        return (Text(id), phase is "commentary" or "final_answer" ? phase : null);
                    }
                }
            }
            return (signature, null);
        }

        private string NormalizeId(string id, AssistantMessage source)
        {
            if (!allowed.Contains(request.Model.Provider) || !id.Contains('|')) return NormalizePart(id);
            var (call, item) = CallParts(id);
            var normalizedItem = source.Provider != request.Model.Provider || source.Api != request.Model.Api ? "fc_" + ShortHash(item!) : NormalizePart(item!);
            if (!normalizedItem.StartsWith("fc_", StringComparison.Ordinal)) normalizedItem = NormalizePart("fc_" + normalizedItem);
            return NormalizePart(call) + "|" + normalizedItem;
        }
        private static string NormalizePart(string value)
        {
            var result = new StringBuilder();
            foreach (var character in value.Take(64)) result.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_');
            return result.ToString().TrimEnd('_');
        }
        // const [callId, itemId] = id.split("|"): either part may be empty (owner decision 13).
        private static (string Call, string? Item) CallParts(string id)
        {
            var parts = id.Split('|');
            if (parts.Length > 2) throw Failure(ResponsesProjectionFailure.IdentityCollision);
            return (parts[0], parts.Length == 2 ? parts[1] : null);
        }
        /// <summary>An id-less call's empty call id, which several calls may share.</summary>
        private static bool SharedCallId(string id) => id.Split('|')[0].Length == 0;
        private bool SameModel(AssistantMessage assistant) => assistant.Provider == request.Model.Provider && assistant.Api == request.Model.Api && assistant.Model == request.Model.Id;

        private string ArgumentString(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
            var result = new StringBuilder(); Write(value); return result.ToString();
            void Write(JsonElement item)
            {
                token.ThrowIfCancellationRequested();
                switch (item.ValueKind)
                {
                    case JsonValueKind.Object:
                        result.Append('{'); var firstProperty = true;
                        foreach (var property in Properties(item))
                        { if (!firstProperty) result.Append(','); firstProperty = false; Quote(result, JsonUtf16.GetName(property)); result.Append(':'); Write(property.Value); }
                        result.Append('}'); break;
                    case JsonValueKind.Array:
                        result.Append('['); var firstItem = true;
                        foreach (var child in item.EnumerateArray()) { if (!firstItem) result.Append(','); firstItem = false; Write(child); }
                        result.Append(']'); break;
                    case JsonValueKind.String: Quote(result, Text(item)); break;
                    case JsonValueKind.Number:
                        var raw = item.GetRawText();
                        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) ||
                            number is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L || raw != number.ToString(CultureInfo.InvariantCulture))
                            throw Failure(ResponsesProjectionFailure.UnsupportedNumber);
                        result.Append(raw); break;
                    case JsonValueKind.True: result.Append("true"); break;
                    case JsonValueKind.False: result.Append("false"); break;
                    case JsonValueKind.Null: result.Append("null"); break;
                    default: throw Failure(ResponsesProjectionFailure.InvalidTranscript);
                }
            }
        }
        private void Add(JsonNode item)
        {
            token.ThrowIfCancellationRequested(); var serialized = item.ToJsonString(OutputJson);
            var size = serialized.Length + (_output.Count == 0 ? 0 : 1);
            if (_output.Count >= options.MaximumOutputItems || size > options.MaximumOutputCharacters - _outputCharacters)
                throw Failure(ResponsesProjectionFailure.ResourceLimit);
            using var document = JsonDocument.Parse(serialized, PiSharp.Contracts.JsonData.DocumentOptions);
            CheckJson(document.RootElement, 1); // The containing output array adds a level.
            _outputCharacters += size; _output.Add(item);
        }
        private void CheckJson(JsonElement value, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                if (depth >= options.MaximumJsonDepth) throw Failure(ResponsesProjectionFailure.ResourceLimit);
                if (value.ValueKind == JsonValueKind.Object)
                    foreach (var property in value.EnumerateObject()) { _ = JsonUtf16.GetName(property); CheckJson(property.Value, depth + 1); }
                else foreach (var child in value.EnumerateArray()) CheckJson(child, depth + 1);
            }
            else if (value.ValueKind == JsonValueKind.String) _ = Text(value); // a lone surrogate only in tool call arguments (TranscriptSurrogates)
        }
        // Pi abe508 openai-responses-shared.ts convertToolResultOutput (after transform-messages.ts downgradeUnsupportedImages).
        private JsonNode ToolOutput(JsonElement body)
        {
            if (options.ModelSupportsImages is null) { var text = ContentText(body, true); return text.Length == 0 ? "(no tool output)" : text; }
            var parts = MediaParts(body, true);
            var result = string.Join('\n', parts.Where(part => part.Data is null).Select(part => part.Text));
            var images = parts.Where(part => part.Data is not null).ToList();
            if (images.Count == 0 || options.ModelSupportsImages != true) return result.Length != 0 ? result : images.Count > 0 ? "(see attached image)" : "(no tool output)";
            var output = new JsonArray();
            if (result.Length != 0) output.Add(new JsonObject { ["type"] = "input_text", ["text"] = result });
            foreach (var image in images) output.Add(new JsonObject { ["type"] = "input_image", ["detail"] = "auto", ["image_url"] = "data:" + image.Text + ";base64," + image.Data });
            return output;
        }

        /// <summary>Text (Data null) and image (Text = MIME type) parts; a non-vision model's images become Pi's placeholders.</summary>
        private List<(string Text, string? Data)> MediaParts(JsonElement body, bool tool)
        {
            var content = body.GetProperty("content"); var result = new List<(string Text, string? Data)>();
            if (!tool && content.ValueKind == JsonValueKind.String) { result.Add((Text(content), null)); return result; }
            if (content.ValueKind != JsonValueKind.Array) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
            var placeholder = tool ? "(tool image omitted: model does not support images)" : "(image omitted: model does not support images)";
            var previousWasPlaceholder = false;
            foreach (var part in content.EnumerateArray())
                switch (String(part, "type"))
                {
                    case "text":
                        var text = String(part, "text"); result.Add((text, null)); previousWasPlaceholder = text == placeholder; break;
                    case "image":
                        var mime = String(part, "mimeType"); var data = String(part, "data");
                        if (options.ModelSupportsImages == true) result.Add((mime, data));
                        else { if (!previousWasPlaceholder) result.Add((placeholder, null)); previousWasPlaceholder = true; }
                        break;
                    default: throw Failure(ResponsesProjectionFailure.UnsupportedContent);
                }
            return result;
        }

        private static string ContentText(JsonElement body, bool arrayOnly)
        {
            var content = body.GetProperty("content");
            if (!arrayOnly && content.ValueKind == JsonValueKind.String) return Text(content);
            if (content.ValueKind != JsonValueKind.Array) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
            return string.Join("\n", content.EnumerateArray().Select(part => String(part, "type") == "text" ? String(part, "text") : throw Failure(ResponsesProjectionFailure.UnsupportedContent)));
        }
        private static string String(JsonElement value, string name) => Text(value.GetProperty(name));
        private static string Text(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String) throw Failure(ResponsesProjectionFailure.InvalidTranscript);
            try { return JsonUtf16.GetString(value); }
            catch (InvalidOperationException) { throw Failure(ResponsesProjectionFailure.UnsupportedUnicode); }
        }
        private static string? OptionalString(JsonFields? fields, string name) => fields?.TryGet(name, out var value) == true ? Text(value!.Value) : null;
        private static bool Bool(JsonFields? fields, string name)
        {
            if (fields?.TryGet(name, out var value) != true) return false;
            return value!.Value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Failure(ResponsesProjectionFailure.InvalidTranscript) };
        }
    }

    private static IEnumerable<JsonProperty> Properties(JsonElement value) => value.EnumerateObject()
        .Select((property, index) => (property, index, key: ArrayIndex(JsonUtf16.GetName(property))))
        .OrderBy(item => item.key is null ? 1 : 0).ThenBy(item => item.key ?? 0).ThenBy(item => item.index).Select(item => item.property);
    private static uint? ArrayIndex(string name) => uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
        index != uint.MaxValue && name == index.ToString(CultureInfo.InvariantCulture) ? index : null;
    // JSON.stringify of a string: a lone surrogate of a tool call's arguments is written as its escape.
    private static void Quote(StringBuilder builder, string value) => JsonUtf16.Quote(builder, value);
    private static string ShortHash(string value)
    {
        unchecked
        {
            uint h1 = 0xdeadbeef, h2 = 0x41c6ce57;
            foreach (var character in value) { h1 = (h1 ^ character) * 2654435761; h2 = (h2 ^ character) * 1597334677; }
            h1 = (h1 ^ (h1 >> 16)) * 2246822507 ^ (h2 ^ (h2 >> 13)) * 3266489909;
            h2 = (h2 ^ (h2 >> 16)) * 2246822507 ^ (h1 ^ (h1 >> 13)) * 3266489909;
            return Base36(h2) + Base36(h1);
        }
        static string Base36(uint value)
        {
            const string digits = "0123456789abcdefghijklmnopqrstuvwxyz"; var result = new StringBuilder();
            do { result.Insert(0, digits[(int)(value % 36)]); value /= 36; } while (value != 0);
            return result.ToString();
        }
    }
}
