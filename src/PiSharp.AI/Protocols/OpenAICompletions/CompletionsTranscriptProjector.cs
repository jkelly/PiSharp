using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.OpenAICompletions;

public sealed record CompletionsTranscriptProjectionOptions(bool Reasoning = false, bool SupportsDeveloperRole = true,
    bool SupportsMidConversationSystemMessages = false, bool RequiresAssistantAfterToolResult = false,
    bool RequiresToolResultName = false, bool RequiresThinkingAsText = false,
    bool RequiresReasoningContentOnAssistantMessages = false, CompletionsToolDeclarationProjectionOptions? ToolDeclarations = null,
    int MaximumMessages = PiRequestBudget.RequestMessages, int MaximumEntryCharacters = PiRequestBudget.RequestEntryCharacters, int MaximumInputCharacters = PiRequestBudget.RequestPayloadBytes,
    int MaximumContentBlocks = PiRequestBudget.RequestItems, int MaximumOutputMessages = PiRequestBudget.RequestItems, int MaximumJsonDepth = 32,
    int MaximumOutputCharacters = PiRequestBudget.RequestPayloadBytes, int MaximumOutputBytes = PiRequestBudget.RequestPayloadBytes)
{
    // ModelDescriptor currently contains identity only. Keep capability local to this provider,
    // without changing its existing positional constructor/deconstruction contract.
    public bool ModelSupportsImages { get; init; }

    public bool SupportsMidConversationToolAdditions { get; init; }
}

/// <summary>Pure request-time projection. Canonical source bodies are borrowed, validated and never rewritten.</summary>
public sealed class CompletionsTranscriptProjector
{
    private readonly CompletionsTranscriptProjectionOptions _options;
    public CompletionsTranscriptProjector(CompletionsTranscriptProjectionOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaximumMessages <= 0 || _options.MaximumEntryCharacters <= 0 || _options.MaximumInputCharacters <= 0 ||
            _options.MaximumContentBlocks <= 0 || _options.MaximumOutputMessages <= 0 || _options.MaximumJsonDepth is < 1 or > 64 ||
            _options.MaximumOutputCharacters < 2 || _options.MaximumOutputBytes < 2)
            throw CompletionsJson.Fail(CompletionsRequestFailure.InvalidConfiguration);
    }
    public JsonData Project(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        try { return new Projection(request, _options, cancellationToken).Run(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw CompletionsJson.Fail(CompletionsRequestFailure.InvalidTranscript); }
    }
    private sealed record Entry(string Role, JsonData Body, string? ResultWireId = null);
    private sealed class Projection(ChatRequest request, CompletionsTranscriptProjectionOptions options, CancellationToken token)
    {
        private readonly CompletionsJson.ArrayBudget _output = new(options.MaximumOutputMessages, options.MaximumOutputCharacters,
            options.MaximumOutputBytes, options.MaximumJsonDepth, token);
        private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> _wireIds = new(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, string> _grammarInputs = new Dictionary<string, string>();
        public JsonData Run()
        {
            if (request.Model is null || request.Messages.IsDefault) throw Fail(CompletionsRequestFailure.InvalidTranscript);
            if (request.Messages.Length > options.MaximumMessages) throw Fail(CompletionsRequestFailure.ResourceLimit);
            long input = 0; var blocks = 0; var entries = new List<Entry>();
            foreach (var message in request.Messages)
            {
                token.ThrowIfCancellationRequested();
                if (message is null || message.WireBody is null) throw Fail(CompletionsRequestFailure.InvalidTranscript);
                var raw = message.WireBody.ToString();
                if (raw.Length > options.MaximumEntryCharacters || raw.Length > options.MaximumInputCharacters - input)
                    throw Fail(CompletionsRequestFailure.ResourceLimit);
                input += raw.Length;
                var body = CompletionsJson.Strict(message.WireBody, options.MaximumJsonDepth, token);
                if (body.Value.ValueKind != JsonValueKind.Object || String(body.Value, "role") != message.Role)
                    throw Fail(CompletionsRequestFailure.InvalidTranscript);
                if (message.Role is not ("system" or "user" or "assistant" or "toolResult")) throw Fail(CompletionsRequestFailure.UnsupportedContent);
                if (body.Value.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                { if (content.GetArrayLength() > options.MaximumContentBlocks - blocks) throw Fail(CompletionsRequestFailure.ResourceLimit); blocks += content.GetArrayLength(); }
                entries.Add(new(message.Role, body));
            }
            if (options.ToolDeclarations?.SupportsOpenAIGrammarTools == true)
                _grammarInputs = new CompletionsToolDeclarationProjector(options.ToolDeclarations).GrammarInputProperties(request, token);
            CompletionsToolDeclarationProjector? additions = null;
            var anchorsAdditions = false;
            if (options.SupportsMidConversationSystemMessages && options.SupportsMidConversationToolAdditions)
            {
                additions = new(options.ToolDeclarations);
                _ = additions.Project(request, token); // Validate every declaration before splitting it across messages.
                anchorsAdditions = CompletionsToolDeclarationProjector.CanAnchorAdditions(request, token);
            }
            if (!options.SupportsMidConversationSystemMessages) entries = FoldSystems(entries);
            entries = Pair(entries);
            string? lastRole = null;
            for (var index = 0; index < entries.Count; index++)
            {
                token.ThrowIfCancellationRequested(); var entry = entries[index]; var body = entry.Body.Value;
                if (options.RequiresAssistantAfterToolResult && lastRole == "toolResult" && entry.Role == "user")
                    _output.Add(new() { ["role"] = "assistant", ["content"] = "I have processed the tool results." });
                switch (entry.Role)
                {
                    case "system":
                        if (index > 0 && anchorsAdditions && body.TryGetProperty("toolsAdded", out var added) && added.GetArrayLength() != 0)
                        {
                            var tools = additions!.Project(request with { Messages = [new("system", entry.Body)] }, token);
                            // Tool-bearing instructions always use system, before their separately rendered
                            // text/developer instruction. Pairing has already deferred them past pending results.
                            _output.Add(new() { ["role"] = "system", ["tools"] = JsonNode.Parse(tools.ToString()) });
                        }
                        var system = RenderSystem(body, index == 0);
                        if (system.Length != 0) _output.Add(new() { ["role"] = options.Reasoning && options.SupportsDeveloperRole ? "developer" : "system", ["content"] = system });
                        break;
                    case "user":
                        var user = Content(body);
                        if (user.ValueKind == JsonValueKind.String) _output.Add(new() { ["role"] = "user", ["content"] = Text(user) });
                        else
                        {
                            var parts = new JsonArray(); var previousWasImagePlaceholder = false;
                            foreach (var part in Array(user))
                            {
                                token.ThrowIfCancellationRequested();
                                switch (String(part, "type"))
                                {
                                    case "text":
                                        var text = String(part, "text");
                                        if (text.Length != 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                                        // Source literal placeholder text also suppresses the following image.
                                        // Empty text breaks the run before the later empty-text filtering.
                                        previousWasImagePlaceholder = text == "(image omitted: model does not support images)";
                                        break;
                                    case "image":
                                        var mime = String(part, "mimeType"); var data = String(part, "data");
                                        if (options.ModelSupportsImages)
                                            parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:" + mime + ";base64," + data } });
                                        else if (!previousWasImagePlaceholder)
                                            parts.Add(new JsonObject { ["type"] = "text", ["text"] = "(image omitted: model does not support images)" });
                                        previousWasImagePlaceholder = true;
                                        break;
                                    default: throw Fail(CompletionsRequestFailure.UnsupportedContent);
                                }
                            }
                            if (parts.Count == 0) continue;
                            _output.Add(new() { ["role"] = "user", ["content"] = parts });
                        }
                        break;
                    case "assistant": if (!ProjectAssistant(body)) continue; break;
                    case "toolResult":
                        lastRole = ProjectToolGroup(entries, ref index) ? "user" : "toolResult";
                        continue;
                }
                lastRole = entry.Role;
            }
            return CompletionsJson.Source(_output.Own(), options.MaximumOutputCharacters, options.MaximumOutputBytes, options.MaximumJsonDepth, token);
        }
        private bool ProjectToolGroup(List<Entry> entries, ref int index)
        {
            const string placeholder = "(tool image omitted: model does not support images)";
            var images = new List<JsonObject>(); var next = index;
            for (; next < entries.Count && entries[next].Role == "toolResult"; next++)
            {
                token.ThrowIfCancellationRequested(); var entry = entries[next]; var body = entry.Body.Value;
                var pieces = new List<string>(); var hasImages = false; var previousWasPlaceholder = false;
                var content = Content(body);
                if (content.ValueKind == JsonValueKind.String) pieces.Add(Text(content)); // Retain existing native legacy text admission.
                else foreach (var part in Array(content))
                {
                    token.ThrowIfCancellationRequested();
                    switch (String(part, "type"))
                    {
                        case "text":
                            var text = String(part, "text"); pieces.Add(text); previousWasPlaceholder = text == placeholder;
                            break;
                        case "image":
                            var mime = String(part, "mimeType"); var data = String(part, "data");
                            if (options.ModelSupportsImages)
                            {
                                hasImages = true;
                                images.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:" + mime + ";base64," + data } });
                            }
                            else if (!previousWasPlaceholder) pieces.Add(placeholder);
                            previousWasPlaceholder = true;
                            break;
                        default: throw Fail(CompletionsRequestFailure.UnsupportedContent);
                    }
                }
                var resultText = string.Join("\n", pieces);
                var result = new JsonObject { ["role"] = "tool",
                    ["content"] = resultText.Length != 0 ? resultText : hasImages ? "(see attached image)" : "(no tool output)",
                    ["tool_call_id"] = entry.ResultWireId ?? String(body, "toolCallId") };
                if (options.RequiresToolResultName && body.TryGetProperty("toolName", out var name) && Text(name).Length != 0) result["name"] = Text(name);
                _output.Add(result);
            }
            index = next - 1;
            if (images.Count == 0) return false;
            if (options.RequiresAssistantAfterToolResult)
                _output.Add(new() { ["role"] = "assistant", ["content"] = "I have processed the tool results." });
            var attached = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Attached image(s) from tool result:" });
            foreach (var image in images) attached.Add(image);
            _output.Add(new() { ["role"] = "user", ["content"] = attached });
            return true;
        }
        private List<Entry> FoldSystems(List<Entry> entries)
        {
            var content = new List<string>(); var sections = new Dictionary<string, string>(StringComparer.Ordinal); var order = new List<string>();
            var any = false;
            foreach (var entry in entries.Where(entry => entry.Role == "system"))
            {
                token.ThrowIfCancellationRequested(); any = true;
                var text = ContentText(entry.Body.Value); if (text.Length != 0) content.Add(text);
                if (entry.Body.Value.TryGetProperty("sections", out var updates))
                    foreach (var update in Properties(updates))
                        if (update.Value.ValueKind == JsonValueKind.Null) { sections.Remove(update.Name); order.Remove(update.Name); }
                        else { if (!sections.ContainsKey(update.Name)) order.Add(update.Name); sections[update.Name] = Text(update.Value); }
            }
            var result = entries.Where(entry => entry.Role != "system").ToList();
            if (any)
            {
                var body = new JsonObject { ["role"] = "system", ["content"] = string.Join("\n\n", content) }; var sectionNode = new JsonObject();
                foreach (var name in order) sectionNode[name] = sections[name]; body["sections"] = sectionNode;
                result.Insert(0, new("system", JsonData.Parse(body.ToJsonString(CompletionsJson.Output))));
            }
            return result;
        }
        private List<Entry> Pair(List<Entry> entries)
        {
            var result = new List<Entry>(); var held = new List<Entry>(); var pending = new List<(string Id, string Name)>();
            var found = new HashSet<string>(StringComparer.Ordinal);
            void Close()
            {
                foreach (var call in pending)
                    if (!found.Contains(call.Id))
                    {
                        if (result.Count >= options.MaximumOutputMessages) throw Fail(CompletionsRequestFailure.ResourceLimit);
                        var body = new JsonObject { ["role"] = "toolResult", ["toolCallId"] = call.Id, ["toolName"] = call.Name,
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "No result provided" }),
                            ["isError"] = true, ["timestamp"] = request.Timestamp };
                        result.Add(new("toolResult", JsonData.Parse(body.ToJsonString(CompletionsJson.Output)), _ids[call.Id]));
                    }
                pending.Clear(); found.Clear(); result.AddRange(held); held.Clear();
            }
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested(); var body = entry.Body.Value;
                if (entry.Role == "assistant")
                {
                    Close(); var reason = String(body, "stopReason");
                    if (reason is "error" or "aborted") continue;
                    if (reason is "pending" or "deferred") throw Fail(CompletionsRequestFailure.InvalidTranscript);
                    var same = SameModel(body);
                    foreach (var part in Array(Content(body)))
                        if (String(part, "type") == "toolCall")
                        {
                            var id = Identity(part, "id"); var name = Identity(part, "name");
                            var wireId = same ? id : NormalizeId(id);
                            // Every preserved call reserves both identities for the entire request history.
                            // A binding is assigned once, so a later turn cannot rename an earlier pair.
                            // An id-less call replays with id "" (owner decision 13): every such call shares it, as upstream sends them.
                            if (wireId.Length == 0) { if (_ids.GetValueOrDefault(id, wireId) != wireId) throw Fail(CompletionsRequestFailure.InvalidTranscript); _ids[id] = wireId; }
                            else if (!_ids.TryAdd(id, wireId) || !_wireIds.Add(wireId))
                                throw Fail(CompletionsRequestFailure.InvalidTranscript);
                            pending.Add((id, name));
                        }
                    result.Add(entry);
                }
                else if (entry.Role == "system" && pending.Count != 0) held.Add(entry);
                else
                {
                    if (entry.Role == "user") Close();
                    if (entry.Role == "toolResult")
                    {
                        var id = String(body, "toolCallId"); found.Add(id);
                        // Resolve in transcript order, including unmatched results preceding a future call.
                        result.Add(entry with { ResultWireId = _ids.GetValueOrDefault(id, id) });
                    }
                    else result.Add(entry);
                }
            }
            Close(); return result;
        }
        private bool ProjectAssistant(JsonElement body)
        {
            var same = SameModel(body); var texts = new List<string>(); var thoughts = new List<(string Text, string? Signature)>();
            var calls = new JsonArray(); JsonNode? details = null; var legacyDetails = new JsonArray();
            foreach (var part in Array(Content(body)))
            {
                token.ThrowIfCancellationRequested();
                switch (String(part, "type"))
                {
                    case "text": var text = String(part, "text"); if (text.Trim().Length != 0) texts.Add(text); break;
                    case "thinking":
                        var thinking = String(part, "thinking"); var signature = Optional(part, "thinkingSignature");
                        var redacted = part.TryGetProperty("redacted", out var redaction) && redaction.ValueKind == JsonValueKind.True;
                        if (!same && redacted) break;
                        if (!same) { if (thinking.Trim().Length != 0) texts.Add(thinking); break; }
                        if (details is null && signature is not null) details = ParseDetails(signature);
                        if (thinking.Trim().Length != 0) thoughts.Add((thinking, signature)); break;
                    case "toolCall":
                        var id = Identity(part, "id"); var name = Identity(part, "name"); var arguments = part.GetProperty("arguments");
                        if (arguments.ValueKind != JsonValueKind.Object) throw Fail(CompletionsRequestFailure.InvalidTranscript);
                        // Source JSON.stringify changes the request projection only; retained canonical tokens remain untouched.
                        if (_grammarInputs.TryGetValue(name, out var inputProperty))
                        {
                            if (!arguments.TryGetProperty(inputProperty, out var grammarInput) || grammarInput.ValueKind != JsonValueKind.String)
                                throw Fail(CompletionsRequestFailure.InvalidTranscript);
                            calls.Add(new JsonObject { ["id"] = _ids[id], ["type"] = "custom",
                                ["custom"] = new JsonObject { ["name"] = name, ["input"] = Text(grammarInput) } });
                        }
                        else
                        {
                            var argumentText = CompletionsJson.Source(JsonData.FromElement(arguments), options.MaximumInputCharacters,
                                options.MaximumOutputBytes, options.MaximumJsonDepth, token).ToString();
                            calls.Add(new JsonObject { ["id"] = _ids[id], ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = argumentText } });
                        }
                        var legacy = same ? Optional(part, "thoughtSignature") : null;
                        if (legacy is not null && ParseLegacy(legacy) is { } legacyDetail) legacyDetails.Add(legacyDetail);
                        break;
                    default: throw Fail(CompletionsRequestFailure.UnsupportedContent);
                }
            }
            var message = new JsonObject { ["role"] = "assistant", ["content"] = options.RequiresAssistantAfterToolResult ? JsonValue.Create("") : null };
            if (options.RequiresThinkingAsText && thoughts.Count != 0)
            {
                var parts = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = string.Join("\n\n", thoughts.Select(item => item.Text)) });
                foreach (var text in texts) parts.Add(new JsonObject { ["type"] = "text", ["text"] = text }); message["content"] = parts;
            }
            else
            {
                if (texts.Count != 0) message["content"] = string.Concat(texts);
                if (details is null && legacyDetails.Count == 0 && thoughts.Count != 0 && thoughts[0].Signature is { } field &&
                    field is "reasoning_content" or "reasoning" or "reasoning_text")
                    message[request.Model.Provider == "opencode-go" && field == "reasoning" ? "reasoning_content" : field] = string.Join("\n", thoughts.Select(item => item.Text));
            }
            if (calls.Count != 0) message["tool_calls"] = calls;
            if (details is not null) message["reasoning_details"] = details;
            else if (legacyDetails.Count != 0) message["reasoning_details"] = legacyDetails;
            if (options.RequiresReasoningContentOnAssistantMessages && options.Reasoning && !message.ContainsKey("reasoning_content")) message["reasoning_content"] = "";
            if (calls.Count == 0 && texts.Count == 0 && !(options.RequiresThinkingAsText && thoughts.Count != 0)) return false;
            _output.Add(message); return true;
        }
        private JsonNode? ParseDetails(string signature)
        {
            try
            {
                var owned = JsonData.Parse(signature); CompletionsJson.Check(owned.Value, 0, options.MaximumJsonDepth, token);
                if (owned.Value.ValueKind != JsonValueKind.Array || owned.Value.GetArrayLength() == 0 ||
                    owned.Value.EnumerateArray().Any(value => !ValidDetail(value))) return null;
                return JsonNode.Parse(owned.ToString());
            }
            catch (JsonException) { return null; }
        }
        private static bool ValidDetail(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("type", out var kind) || kind.ValueKind != JsonValueKind.String) return false;
            foreach (var name in new[] { "id", "format", "index" })
                if (value.TryGetProperty(name, out var field) && !(field.ValueKind == (name == "index" ? JsonValueKind.Number : JsonValueKind.String) || name == "id" && field.ValueKind == JsonValueKind.Null)) return false;
            var required = kind.GetString() switch { "reasoning.summary" => "summary", "reasoning.text" => "text", "reasoning.encrypted" => "data", _ => null };
            return required is not null && value.TryGetProperty(required, out var text) && text.ValueKind == JsonValueKind.String &&
                (!value.TryGetProperty("signature", out var signature) || kind.GetString() != "reasoning.text" || signature.ValueKind is JsonValueKind.String or JsonValueKind.Null);
        }
        private JsonNode? ParseLegacy(string signature)
        {
            try
            {
                var data = JsonData.Parse(signature); CompletionsJson.Check(data.Value, 0, options.MaximumJsonDepth, token); var value = data.Value;
                return ValidDetail(value) && String(value, "type") == "reasoning.encrypted" && Optional(value, "id") is { Length: > 0 } && String(value, "data").Length > 0
                    ? JsonNode.Parse(data.ToString()) : null;
            }
            catch (JsonException) { return null; }
        }
        private bool SameModel(JsonElement body) => String(body, "api") == request.Model.Api && String(body, "provider") == request.Model.Provider && String(body, "model") == request.Model.Id;
        private string NormalizeId(string id)
        {
            var separator = id.IndexOf('|');
            if (separator < 0) return request.Model.Provider == "openai" && id.Length > 40 ? id[..40] : id;
            string Clean(string text) => new(text.Select(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray());
            var call = Clean(id[..separator]); var item = Clean(id[(separator + 1)..]); var combined = item.Length == 0 ? call : call + "_" + item;
            if (combined.Length <= 40) return combined;
            var hash = ShortHash(id)[..8]; return call[..Math.Min(call.Length, Math.Max(1, 40 - hash.Length - 1))] + "_" + hash;
        }
        private static string ShortHash(string text)
        {
            unchecked
            {
                uint h1 = 0xdeadbeef, h2 = 0x41c6ce57;
                foreach (var character in text) { h1 = (h1 ^ character) * 2654435761u; h2 = (h2 ^ character) * 1597334677u; }
                h1 = (h1 ^ h1 >> 16) * 2246822507u ^ (h2 ^ h2 >> 13) * 3266489909u;
                h2 = (h2 ^ h2 >> 16) * 2246822507u ^ (h1 ^ h1 >> 13) * 3266489909u;
                return Base36(h2) + Base36(h1);
            }
        }
        private static string Base36(uint number)
        { const string digits = "0123456789abcdefghijklmnopqrstuvwxyz"; var text = ""; do { text = digits[(int)(number % 36)] + text; number /= 36; } while (number != 0); return text; }
        private static JsonElement Content(JsonElement body) => body.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null ? content : JsonData.Parse("[]").Value;
        private static string ContentText(JsonElement body)
        {
            var content = Content(body); if (content.ValueKind == JsonValueKind.String) return Text(content);
            return string.Join("\n", Array(content).Select(part => String(part, "type") == "text" ? String(part, "text") : throw Fail(CompletionsRequestFailure.UnsupportedContent)));
        }
        private static string RenderSystem(JsonElement body, bool leading)
        {
            var parts = new List<string>(); var text = ContentText(body); if (text.Length != 0) parts.Add(text);
            if (body.TryGetProperty("sections", out var sections))
                foreach (var section in Properties(sections))
                    if (leading) { if (section.Value.ValueKind != JsonValueKind.Null && Text(section.Value).Length != 0) parts.Add(Text(section.Value)); }
                    else parts.Add(section.Value.ValueKind == JsonValueKind.Null ? $"Removed system prompt section \"{section.Name}\"." : $"Updated system prompt section \"{section.Name}\":\n\n{Text(section.Value)}");
            return string.Join("\n\n", parts);
        }
        private static IEnumerable<JsonProperty> Properties(JsonElement value) => value.ValueKind == JsonValueKind.Object ? CompletionsJson.Properties(value) : throw Fail(CompletionsRequestFailure.InvalidTranscript);
        private static JsonElement.ArrayEnumerator Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Fail(CompletionsRequestFailure.InvalidTranscript);
        private static string? Optional(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.ValueKind != JsonValueKind.Null ? Text(item) : null;
        // A nameless or id-less call keeps its empty name/id (owner decision 13).
        private static string Identity(JsonElement value, string name) { var text = String(value, name); return text.Contains('\0') ? throw Fail(CompletionsRequestFailure.InvalidTranscript) : text; }
        private static string String(JsonElement value, string name) => CompletionsJson.String(value, name);
        private static string Text(JsonElement value) => CompletionsJson.Text(value);
        private static CompletionsRequestException Fail(CompletionsRequestFailure failure) => CompletionsJson.Fail(failure);
    }
}
