// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/api/anthropic-messages.ts (buildParams/convertMessages/convertTools,
// getBetaFeatures and insertThinkingLevelMessages for compat.supportsMidConvoEffort)
// and packages/ai/src/api/constrained-sampling.ts (strict conversion with Anthropic's unsupported keywords).
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI.Protocols.AnthropicMessages;

public enum AnthropicCacheRetention { None, Short, Long }
public enum AnthropicRequestFailure { InvalidTranscript, UnsupportedContent, UnsupportedUnicode, UnsupportedNumber, UnsupportedStrictSchema, UnmatchedToolResult, IdentityCollision, ResourceLimit }
public sealed class AnthropicRequestException : Exception
{
    public AnthropicRequestFailure Failure { get; }
    internal AnthropicRequestException(AnthropicRequestFailure failure) : base(failure switch
    {
        AnthropicRequestFailure.ResourceLimit => "Anthropic request projection exceeds configured limits.",
        AnthropicRequestFailure.UnmatchedToolResult => "Anthropic transcript has unmatched or duplicate tool results.",
        AnthropicRequestFailure.IdentityCollision => "Anthropic request tool identities are ambiguous.",
        AnthropicRequestFailure.UnsupportedUnicode => "Anthropic request contains unsupported unpaired UTF-16.",
        AnthropicRequestFailure.UnsupportedNumber => "Anthropic request contains an unsupported nonfinite number.",
        AnthropicRequestFailure.UnsupportedStrictSchema => "Anthropic tool requires an unsupported strict schema.",
        AnthropicRequestFailure.UnsupportedContent => "Anthropic request contains unsupported content or declarations.",
        _ => "Invalid Anthropic request projection input."
    }) => Failure = failure;
}
public sealed record AnthropicMessagesRequestOptions(
    int MaximumTokens, bool ModelReasoning = false, bool ModelSupportsImages = true,
    bool? ThinkingEnabled = null, bool ForceAdaptiveThinking = false, int ThinkingBudgetTokens = 1024,
    string ThinkingDisplay = "summarized", string? Effort = null, bool SupportsThinkingOff = true,
    decimal? Temperature = null, bool SupportsTemperature = true, bool AllowEmptyThinkingSignatures = false,
    AnthropicCacheRetention CacheRetention = AnthropicCacheRetention.Short, bool SupportsLongCacheRetention = true,
    bool SupportsEagerToolInputStreaming = true, bool SupportsStrictTools = false, bool SupportsCacheControlOnTools = true,
    bool OAuthProjection = false, bool InterleavedThinking = true, bool SupportsMidConversationSystemMessages = false,
    ImmutableArray<string> AllowedFallbackModels = default, ImmutableArray<string> BetaFeatures = default,
    JsonData? ToolChoice = null, string? UserId = null,
    int MaximumMessages = PiRequestBudget.RequestMessages, int MaximumEntryCharacters = PiRequestBudget.RequestEntryCharacters, int MaximumInputCharacters = PiRequestBudget.RequestPayloadBytes,
    // anthropic.ts convertTools declares every tool of the context: no tool or declaration count bound (payload bytes bound the size).
    int MaximumContentBlocks = PiRequestBudget.RequestItems, int MaximumDeclarations = int.MaxValue, int MaximumActiveTools = int.MaxValue,
    int MaximumProjectedMessages = PiRequestBudget.RequestItems, int MaximumJsonDepth = 32,
    int MaximumOutputCharacters = PiRequestBudget.RequestPayloadBytes, int MaximumOutputBytes = PiRequestBudget.RequestPayloadBytes,
    bool SupportsMidConversationToolChanges = false, bool SupportsMidConversationEffort = false)
{
    /// <summary>Upstream stream(): the effort a managed (compat.supportsMidConvoEffort) response records as providerThinkingLevel.</summary>
    public string? ProviderThinkingLevel => SupportsMidConversationEffort ? Effort ?? "high" : null;
}

/// <summary>Pure owned transcript-to-request projection. No network, credential, clock or execution authority.</summary>
public sealed class AnthropicMessagesRequestProjector
{
    private readonly AnthropicMessagesRequestOptions _options;
    private static readonly JsonSerializerOptions OutputJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] ClaudeCodeNames = ["Read", "Write", "Edit", "Bash", "Grep", "Glob", "AskUserQuestion",
        "EnterPlanMode", "ExitPlanMode", "KillShell", "NotebookEdit", "Skill", "Task", "TaskOutput", "TodoWrite", "WebFetch", "WebSearch"];
    private static readonly string[] UnsupportedStrictKeys = ["$ref", "$defs", "definitions", "allOf", "oneOf", "patternProperties",
        "dependentSchemas", "dependencies", "unevaluatedProperties", "propertyNames", "contains", "prefixItems", "not", "if", "then", "else"];
    // Keywords Anthropic strict tool use rejects for the whole request; such "prefer" tools are sent non-strict.
    private static readonly string[] AnthropicStrictUnsupportedKeywords = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "multipleOf", "maxItems", "uniqueItems", "minContains", "maxContains", "minProperties", "maxProperties"];
    private static readonly string[] AnthropicStrictStringFormats = ["date-time", "time", "date", "duration", "email", "hostname", "uri", "ipv4", "ipv6", "uuid"];
    public AnthropicMessagesRequestProjector(AnthropicMessagesRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options); _options = options;
        if (options.MaximumTokens <= 0 || options.ThinkingBudgetTokens < 0 || options.MaximumMessages <= 0 ||
            options.MaximumEntryCharacters <= 0 || options.MaximumInputCharacters <= 0 || options.MaximumContentBlocks <= 0 ||
            options.MaximumDeclarations <= 0 || options.MaximumActiveTools <= 0 || options.MaximumProjectedMessages <= 0 ||
            options.MaximumJsonDepth is < 1 or > 64 || options.MaximumOutputCharacters < 2 || options.MaximumOutputBytes < 2 ||
            !Enum.IsDefined(options.CacheRetention) || options.ThinkingDisplay is not ("summarized" or "omitted") ||
            options.Effort is not (null or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid Anthropic request profile or limits.");
    }
    public JsonData Project(ChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        try { return new Projection(request, _options, cancellationToken).Run(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        { throw Fail(AnthropicRequestFailure.InvalidTranscript); }
    }
    private static AnthropicRequestException Fail(AnthropicRequestFailure failure) => new(failure);
    private sealed record Entry(string Role, JsonData Body, AssistantMessage? Assistant = null);
    private sealed class Projection(ChatRequest request, AnthropicMessagesRequestOptions options, CancellationToken token)
    {
        private readonly List<Entry> _entries = [];
        private readonly JsonArray _messages = [];
        private readonly List<JsonObject> _pendingSystems = [];
        private readonly Dictionary<int, string> _assistantLevels = [];
        private readonly Dictionary<string, JsonElement> _tools = new(StringComparer.Ordinal);
        private readonly List<string> _toolOrder = [];
        private readonly Dictionary<string, string> _idMap = new(StringComparer.Ordinal);
        private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
        private readonly List<(string Id, string Name)> _pendingCalls = [];
        private readonly HashSet<string> _answered = new(StringComparer.Ordinal);
        private readonly List<string> _systemText = [];
        private readonly Dictionary<string, string> _sections = new(StringComparer.Ordinal);
        private readonly List<string> _sectionOrder = [];
        private const string DeferredPlaceholderName = "__pi_deferred_placeholder__";
        private readonly List<JsonElement> _nativeDeclarations = [];
        private bool _nativeToolChanges;
        private bool _lastWasToolResults;
        private long _retainedCharacters;
        private long _retainedBytes;
        private long _inputCharacters;
        private int _projectedBlocks;
        private JsonObject? Cache => options.CacheRetention == AnthropicCacheRetention.None ? null :
            options.CacheRetention == AnthropicCacheRetention.Long && options.SupportsLongCacheRetention
                ? new() { ["type"] = "ephemeral", ["ttl"] = "1h" } : new() { ["type"] = "ephemeral" };
        public JsonData Run()
        {
            if (request.Model is null || request.Model.Api != "anthropic-messages" || string.IsNullOrWhiteSpace(request.Model.Id) ||
                string.IsNullOrWhiteSpace(request.Model.Provider) || request.Messages.IsDefault) throw Fail(AnthropicRequestFailure.InvalidTranscript);
            Unicode(request.Model.Id); Unicode(request.Model.Provider);
            CheckOptionSizes();
            Admit();
            PrepareNativeTools();
            // This first pass follows transformMessages, including identities from subsequently skipped failed turns.
            foreach (var entry in _entries)
                if (entry.Assistant is { } assistant && !SameModel(assistant))
                    foreach (var tool in assistant.Content.OfType<ToolCallContent>())
                    { var normalized = NormalizeId(tool.Id); if (normalized != tool.Id) _idMap[tool.Id] = normalized; }
            for (var index = 0; index < _entries.Count; index++)
            {
                token.ThrowIfCancellationRequested(); var entry = _entries[index];
                if (entry.Role == "system")
                {
                    if (options.SupportsMidConversationSystemMessages && index != 0)
                    {
                        var text = RenderUpdate(entry.Body.Value);
                        var blocks = new JsonArray();
                        if (text.Length > 0) blocks.Add(TextBlock(text));
                        if (_nativeToolChanges) AddToolChanges(entry.Body.Value, blocks);
                        if (blocks.Count > 0)
                        {
                            if (_pendingSystems.Count >= options.MaximumProjectedMessages) throw Fail(AnthropicRequestFailure.ResourceLimit);
                            _pendingSystems.Add(new() { ["role"] = "system", ["content"] = blocks });
                        }
                    }
                }
                else if (entry.Role == "user")
                {
                    CloseCalls(); var content = entry.Body.Value.TryGetProperty("content", out var supplied) ? supplied : default;
                    if (content.ValueKind == JsonValueKind.String)
                    { var text = Text(content); if (Trim(text).Length > 0) AddMessage("user", JsonValue.Create(text)!); }
                    else
                    {
                        var blocks = ContentBlocks(content, user: true);
                        var filtered = new JsonArray(blocks.Where(block => block["type"]!.GetValue<string>() != "text" || Trim(block["text"]!.GetValue<string>()).Length > 0).Select(block => (JsonNode?)block).ToArray());
                        if (filtered.Count > 0) AddMessage("user", filtered);
                    }
                }
                else if (entry.Assistant is { } assistant)
                {
                    CloseCalls();
                    if (assistant.StopReason is StopReason.Error or StopReason.Aborted) continue;
                    FlushSystems(); var blocks = new JsonArray(); var same = SameModel(assistant);
                    foreach (var block in assistant.Content)
                    {
                        token.ThrowIfCancellationRequested();
                        if (block is TextContent text)
                        { if (Trim(text.Text).Length > 0) blocks.Add(TextBlock(text.Text)); }
                        else if (block is ThinkingContent thinking)
                        {
                            var redacted = Boolean(thinking.ExtraProperties, "redacted");
                            var signature = OptionalString(thinking.ExtraProperties, "thinkingSignature");
                            if (redacted) { if (same) blocks.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = signature ?? throw Fail(AnthropicRequestFailure.UnsupportedContent) }); }
                            else if (!same)
                            { if (Trim(thinking.Thinking).Length > 0) blocks.Add(TextBlock(thinking.Thinking)); }
                            else
                            {
                                var signed = signature is not null && Trim(signature).Length > 0;
                                if (Trim(thinking.Thinking).Length == 0 && !signed) continue;
                                blocks.Add(signed || options.AllowEmptyThinkingSignatures
                                    ? new JsonObject { ["type"] = "thinking", ["thinking"] = thinking.Thinking, ["signature"] = signed ? signature : "" }
                                    : TextBlock(thinking.Thinking));
                            }
                        }
                        else if (block is ToolCallContent tool)
                        {
                            var id = same ? tool.Id : NormalizeId(tool.Id);
                            if (string.IsNullOrWhiteSpace(id) || !_callIds.Add(id)) throw Fail(AnthropicRequestFailure.IdentityCollision);
                            var name = ToolName(tool.Name); if (string.IsNullOrWhiteSpace(name)) throw Fail(AnthropicRequestFailure.UnsupportedContent);
                            blocks.Add(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = Node(tool.Arguments.Value) });
                            _pendingCalls.Add((id, name));
                        }
                        else throw Fail(AnthropicRequestFailure.UnsupportedContent);
                    }
                    if (blocks.Count == 0) continue;
                    var at = _messages.Count; AddMessage("assistant", blocks);
                    // convertMessages: only exact Anthropic Messages responses of this provider replay their recorded effort.
                    if (options.SupportsMidConversationEffort && assistant.Api == "anthropic-messages" && assistant.Provider == request.Model.Provider &&
                        assistant.ExtraProperties?.TryGet("providerThinkingLevel", out var recorded) == true && recorded!.Value.ValueKind == JsonValueKind.String &&
                        recorded.Value.GetString() is "low" or "medium" or "high" or "xhigh" or "max") _assistantLevels[at] = recorded.Value.GetString()!;
                }
                else if (entry.Role == "toolResult")
                {
                    var body = entry.Body.Value; var original = String(body, "toolCallId");
                    var id = _idMap.GetValueOrDefault(original, original);
                    if (!_pendingCalls.Any(call => call.Id == id) || !_answered.Add(id)) throw Fail(AnthropicRequestFailure.UnmatchedToolResult);
                    var isError = body.GetProperty("isError");
                    if (isError.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Fail(AnthropicRequestFailure.InvalidTranscript);
                    var content = body.TryGetProperty("content", out var supplied) ? supplied : default;
                    AddResult(id, ResultContent(content), isError.GetBoolean());
                }
            }
            CloseCalls(); FlushSystems(); AttachLastCache();
            if (options.SupportsMidConversationEffort) InsertEffortMessages();
            var root = new JsonObject { ["model"] = request.Model.Id, ["messages"] = _messages, ["max_tokens"] = options.MaximumTokens, ["stream"] = true };
            var system = options.SupportsMidConversationSystemMessages ? InitialSystem() : FoldedSystem();
            var systemBlocks = new JsonArray();
            if (options.OAuthProjection) systemBlocks.Add(CacheText("You are Claude Code, Anthropic's official CLI for Claude."));
            if (system.Length > 0) systemBlocks.Add(CacheText(system));
            if (systemBlocks.Count > 0) root["system"] = systemBlocks;
            if (options.Temperature is { } temperature && options.ThinkingEnabled != true && !options.SupportsMidConversationEffort && options.SupportsTemperature) root["temperature"] = JsonNode.Parse(Number((double)temperature));
            var tools = ConvertTools(); if (tools.Count > 0) root["tools"] = tools;
            var betas = Betas(); if (betas.Count > 0) root["betas"] = betas;
            // Managed effort models always use adaptive thinking so prefix mismatches are dropped; per-turn effort rides the markers.
            if (options.SupportsMidConversationEffort)
            {
                root["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = options.ThinkingDisplay,
                    ["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" } };
                root["output_config"] = new JsonObject { ["effort"] = "high" };
            }
            else if (options.ModelReasoning && options.ThinkingEnabled == true)
            {
                root["thinking"] = options.ForceAdaptiveThinking
                    ? new JsonObject { ["type"] = "adaptive", ["display"] = options.ThinkingDisplay }
                    : new JsonObject { ["type"] = "enabled", ["budget_tokens"] = options.ThinkingBudgetTokens == 0 ? 1024 : options.ThinkingBudgetTokens, ["display"] = options.ThinkingDisplay };
                if (options.ForceAdaptiveThinking && options.Effort is not null) root["output_config"] = new JsonObject { ["effort"] = options.Effort };
            }
            else if (options.ModelReasoning && options.ThinkingEnabled == false && options.SupportsThinkingOff) root["thinking"] = new JsonObject { ["type"] = "disabled" };
            if (options.UserId is not null) { Unicode(options.UserId); root["metadata"] = new JsonObject { ["user_id"] = options.UserId }; }
            if (options.ToolChoice is { } choice)
            {
                // Validate retained caller syntax before conversion can erase comments or trailing commas.
                // CheckOptionSizes has already charged these raw characters against the input budget.
                var value = JsonData.Parse(choice.ToString()).Value;
                CheckJson(value, 0);
                if (value.ValueKind == JsonValueKind.String)
                { var type = Text(value); if (type is not ("auto" or "any" or "none")) throw Fail(AnthropicRequestFailure.UnsupportedContent); root["tool_choice"] = new JsonObject { ["type"] = type }; }
                else
                {
                    if (value.ValueKind != JsonValueKind.Object || String(value, "type") != "tool") throw Fail(AnthropicRequestFailure.UnsupportedContent);
                    _ = String(value, "name"); root["tool_choice"] = Node(value);
                }
            }
            if (!options.AllowedFallbackModels.IsDefaultOrEmpty)
            {
                if (options.AllowedFallbackModels.Length > options.MaximumActiveTools) throw Fail(AnthropicRequestFailure.ResourceLimit);
                var fallbacks = new JsonArray();
                foreach (var model in options.AllowedFallbackModels) { token.ThrowIfCancellationRequested(); if (string.IsNullOrWhiteSpace(model)) throw Fail(AnthropicRequestFailure.UnsupportedContent); Unicode(model); fallbacks.Add(new JsonObject { ["model"] = model }); }
                root["fallbacks"] = fallbacks;
            }
            var raw = root.ToJsonString(OutputJson);
            if (raw.Length > options.MaximumOutputCharacters || Encoding.UTF8.GetByteCount(raw) > options.MaximumOutputBytes) throw Fail(AnthropicRequestFailure.ResourceLimit);
            var owned = JsonData.Parse(raw); CheckJson(owned.Value, 0); token.ThrowIfCancellationRequested(); return owned;
        }
        private void Admit()
        {
            if (request.Messages.Length > options.MaximumMessages) throw Fail(AnthropicRequestFailure.ResourceLimit);
            long characters = _inputCharacters; var blocks = 0; var declarations = 0;
            foreach (var entry in request.Messages)
            {
                token.ThrowIfCancellationRequested();
                if (entry is null || entry.WireBody is null) throw Fail(AnthropicRequestFailure.InvalidTranscript);
                var raw = entry.WireBody.ToString();
                if (raw.Length > options.MaximumEntryCharacters || raw.Length > options.MaximumInputCharacters - characters) throw Fail(AnthropicRequestFailure.ResourceLimit);
                characters += raw.Length; var owned = JsonData.Parse(raw); var body = owned.Value; CheckJson(body, 0);
                if (body.ValueKind != JsonValueKind.Object || String(body, "role") != entry.Role) throw Fail(AnthropicRequestFailure.InvalidTranscript);
                if (entry.Role is not ("system" or "user" or "assistant" or "toolResult")) throw Fail(AnthropicRequestFailure.UnsupportedContent);
                if (body.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                { if (content.GetArrayLength() > options.MaximumContentBlocks - blocks) throw Fail(AnthropicRequestFailure.ResourceLimit); blocks += content.GetArrayLength(); }
                AssistantMessage? assistant = null;
                if (entry.Role == "assistant")
                {
                    assistant = PiWireJson.ReadMessage(body);
                    if (assistant.StopReason is StopReason.Pending or StopReason.Deferred) throw Fail(AnthropicRequestFailure.InvalidTranscript);
                }
                if (entry.Role == "system")
                {
                    var text = ContentText(body); if (text.Length > 0) _systemText.Add(text);
                    if (body.TryGetProperty("sections", out var sections))
                        foreach (var property in Properties(sections))
                        {
                            if (property.Value.ValueKind == JsonValueKind.Null) { _sections.Remove(property.Name); _sectionOrder.Remove(property.Name); }
                            else { if (!_sections.ContainsKey(property.Name)) _sectionOrder.Add(property.Name); _sections[property.Name] = Text(property.Value); }
                        }
                    foreach (var property in new[] { "toolsRemoved", "toolsAdded" })
                    {
                        if (!body.TryGetProperty(property, out var values)) continue;
                        foreach (var declaration in Array(values))
                        {
                            token.ThrowIfCancellationRequested(); if (++declarations > options.MaximumDeclarations) throw Fail(AnthropicRequestFailure.ResourceLimit);
                            var name = String(declaration, "name"); if (string.IsNullOrWhiteSpace(name)) throw Fail(AnthropicRequestFailure.UnsupportedContent);
                            if (property == "toolsRemoved") { if (_tools.Remove(name)) _toolOrder.Remove(name); }
                            else
                            {
                                if (!_tools.ContainsKey(name)) { if (_tools.Count >= options.MaximumActiveTools) throw Fail(AnthropicRequestFailure.ResourceLimit); _toolOrder.Add(name); }
                                _tools[name] = declaration;
                            }
                        }
                    }
                }
                _entries.Add(new(entry.Role, owned, assistant));
            }
        }
        private bool SameModel(AssistantMessage message) => message.Api == request.Model.Api && message.Provider == request.Model.Provider && message.Model == request.Model.Id;
        private void PrepareNativeTools()
        {
            if (!options.SupportsMidConversationSystemMessages || !options.SupportsMidConversationToolChanges ||
                _entries.Count == 0 || _entries[0].Role != "system" ||
                !_entries[0].Body.Value.TryGetProperty("toolsAdded", out var initial) || initial.GetArrayLength() == 0) return;
            // inline-tools-2026-09-15: the request-level list stays the initial tools plus the placeholder. Every later
            // declaration, including a same-name redefinition, is defined by value in its own tool_addition block.
            _nativeDeclarations.AddRange(Array(initial));
            if (_nativeDeclarations.Count >= options.MaximumActiveTools) throw Fail(AnthropicRequestFailure.ResourceLimit);
            _nativeToolChanges = true;
        }
        private void AddToolChanges(JsonElement body, JsonArray blocks)
        {
            var added = body.TryGetProperty("toolsAdded", out var additions) ? Array(additions).ToArray() : [];
            var redefined = added.Select(tool => String(tool, "name")).ToHashSet(StringComparer.Ordinal);
            if (body.TryGetProperty("toolsRemoved", out var removals))
                foreach (var tool in Array(removals))
                {
                    token.ThrowIfCancellationRequested(); var name = String(tool, "name");
                    // A new definition under the same name replaces the old one, so no removal is needed.
                    if (redefined.Contains(name)) continue;
                    blocks.Add(new JsonObject { ["type"] = "tool_removal", ["tool"] = new JsonObject { ["type"] = "tool_reference", ["name"] = ToolName(name) } });
                }
            // Native hardening: one record cannot define the reserved placeholder or two tools under one projected name.
            var defined = new HashSet<string>(StringComparer.Ordinal) { DeferredPlaceholderName };
            foreach (var tool in added)
            {
                token.ThrowIfCancellationRequested(); var name = ToolName(String(tool, "name"));
                if (!defined.Add(name)) throw Fail(AnthropicRequestFailure.IdentityCollision);
                blocks.Add(new JsonObject { ["type"] = "tool_addition", ["tool"] = new JsonObject { ["type"] = "tool_definition", ["definition"] = ConvertTool(tool, name) } });
            }
        }
        private void CheckOptionSizes()
        {
            long characters = request.Model.Id.Length + request.Model.Provider.Length;
            void Add(string value)
            {
                token.ThrowIfCancellationRequested(); Unicode(value);
                if (value.Length > options.MaximumInputCharacters - characters) throw Fail(AnthropicRequestFailure.ResourceLimit);
                characters += value.Length;
            }
            if (characters > options.MaximumInputCharacters) throw Fail(AnthropicRequestFailure.ResourceLimit);
            if (options.UserId is not null) Add(options.UserId);
            if (!options.BetaFeatures.IsDefault)
            {
                if (options.BetaFeatures.Length > options.MaximumDeclarations) throw Fail(AnthropicRequestFailure.ResourceLimit);
                foreach (var feature in options.BetaFeatures) Add(feature ?? throw Fail(AnthropicRequestFailure.UnsupportedContent));
            }
            if (!options.AllowedFallbackModels.IsDefault)
            {
                if (options.AllowedFallbackModels.Length > options.MaximumActiveTools) throw Fail(AnthropicRequestFailure.ResourceLimit);
                foreach (var model in options.AllowedFallbackModels) Add(model ?? throw Fail(AnthropicRequestFailure.UnsupportedContent));
            }
            if (options.ToolChoice is { } choice) Add(choice.ToString());
            _inputCharacters = characters;
        }
        private string ToolName(string name) => options.OAuthProjection ? ClaudeCodeNames.FirstOrDefault(value => value.ToLowerInvariant() == name.ToLowerInvariant()) ?? name : name;
        private static string NormalizeId(string id) => new(id.Take(64).Select(value => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' ? value : '_').ToArray());
        private string FoldedSystem()
        {
            var parts = new List<string>(); var content = string.Join("\n\n", _systemText); if (content.Length > 0) parts.Add(content);
            foreach (var key in OrderedKeys(_sectionOrder)) if (_sections[key].Length > 0) parts.Add(_sections[key]);
            return string.Join("\n\n", parts);
        }
        private string InitialSystem()
        {
            if (_entries.Count == 0 || _entries[0].Role != "system") return "";
            var body = _entries[0].Body.Value; var parts = new List<string>(); var content = ContentText(body); if (content.Length > 0) parts.Add(content);
            if (body.TryGetProperty("sections", out var sections))
                foreach (var property in Properties(sections)) if (property.Value.ValueKind != JsonValueKind.Null && Text(property.Value).Length > 0) parts.Add(Text(property.Value));
            return string.Join("\n\n", parts);
        }
        private string RenderUpdate(JsonElement body)
        {
            var parts = new List<string>(); var content = ContentText(body); if (content.Length > 0) parts.Add(content);
            if (body.TryGetProperty("sections", out var sections))
                foreach (var property in Properties(sections)) parts.Add(property.Value.ValueKind == JsonValueKind.Null
                    ? "Removed system prompt section \"" + property.Name + "\"."
                    : "Updated system prompt section \"" + property.Name + "\":\n\n" + Text(property.Value));
            return string.Join("\n\n", parts);
        }
        private static string ContentText(JsonElement body)
        {
            if (!body.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null) return "";
            if (content.ValueKind == JsonValueKind.String) return Text(content);
            return string.Join("\n", Array(content).Where(block => String(block, "type") == "text").Select(block => String(block, "text")));
        }
        private JsonObject CacheText(string text) { var result = TextBlock(text); if (Cache is { } cache) result["cache_control"] = cache; return result; }
        private List<JsonObject> ContentBlocks(JsonElement content, bool user)
        {
            var result = new List<JsonObject>(); var previousPlaceholder = false;
            if (content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return result;
            foreach (var block in Array(content))
            {
                token.ThrowIfCancellationRequested(); var type = String(block, "type");
                if (type == "text")
                { var text = String(block, "text"); result.Add(TextBlock(text)); previousPlaceholder = text == (user ? "(image omitted: model does not support images)" : "(tool image omitted: model does not support images)"); }
                else if (type == "image")
                {
                    if (!options.ModelSupportsImages)
                    { if (!previousPlaceholder) result.Add(TextBlock(user ? "(image omitted: model does not support images)" : "(tool image omitted: model does not support images)")); previousPlaceholder = true; continue; }
                    var mime = String(block, "mimeType"); if (mime is not ("image/jpeg" or "image/png" or "image/gif" or "image/webp")) throw Fail(AnthropicRequestFailure.UnsupportedContent);
                    result.Add(new() { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = String(block, "data") } }); previousPlaceholder = false;
                }
                else throw Fail(AnthropicRequestFailure.UnsupportedContent);
            }
            return result;
        }
        private JsonNode ResultContent(JsonElement content)
        {
            var blocks = ContentBlocks(content, user: false);
            if (blocks.All(block => block["type"]!.GetValue<string>() == "text")) return JsonValue.Create(string.Join("\n", blocks.Select(block => block["text"]!.GetValue<string>())))!;
            if (blocks.All(block => block["type"]!.GetValue<string>() != "text")) blocks.Insert(0, TextBlock("(see attached image)"));
            return new JsonArray(blocks.Select(block => (JsonNode?)block).ToArray());
        }
        private void CloseCalls()
        {
            foreach (var call in _pendingCalls) if (!_answered.Contains(call.Id)) AddResult(call.Id, JsonValue.Create("No result provided")!, true);
            _pendingCalls.Clear(); _answered.Clear();
        }
        private void AddResult(string id, JsonNode content, bool isError)
        {
            var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content, ["is_error"] = isError };
            Charge(block);
            if (_lastWasToolResults)
            {
                if (++_projectedBlocks > options.MaximumContentBlocks) throw Fail(AnthropicRequestFailure.ResourceLimit);
                _messages[^1]!["content"]!.AsArray().Add(block);
            }
            else { AddMessage("user", new JsonArray(block), charge: false); _lastWasToolResults = true; }
        }
        // insertThinkingLevelMessages: a marker before each recorded assistant turn, then the active effort last (after the cache breakpoint).
        private void InsertEffortMessages()
        {
            static JsonObject Marker(string effort) => new() { ["role"] = "system", ["content"] = new JsonArray(), ["output_config"] = new JsonObject { ["effort"] = effort } };
            if (_messages.Count + _assistantLevels.Count + 1 > options.MaximumProjectedMessages) throw Fail(AnthropicRequestFailure.ResourceLimit);
            foreach (var (index, effort) in _assistantLevels.OrderByDescending(pair => pair.Key))
            { token.ThrowIfCancellationRequested(); var marker = Marker(effort); Charge(marker); _messages.Insert(index, marker); }
            var active = Marker(options.Effort ?? "high"); Charge(active); _messages.Add(active);
        }
        private void FlushSystems() { foreach (var system in _pendingSystems) AddMessage("system", system["content"]!.DeepClone()); _pendingSystems.Clear(); }
        private void AddMessage(string role, JsonNode content, bool charge = true)
        {
            token.ThrowIfCancellationRequested(); if (_messages.Count >= options.MaximumProjectedMessages) throw Fail(AnthropicRequestFailure.ResourceLimit);
            if (content is JsonArray blocks) { if (blocks.Count > options.MaximumContentBlocks - _projectedBlocks) throw Fail(AnthropicRequestFailure.ResourceLimit); _projectedBlocks += blocks.Count; }
            var message = new JsonObject { ["role"] = role, ["content"] = content }; if (charge) Charge(message); _messages.Add(message); _lastWasToolResults = false;
        }
        private void AttachLastCache()
        {
            if (Cache is not { } cache || _messages.Count == 0) return;
            var message = _messages[^1]!.AsObject(); var role = message["role"]!.GetValue<string>(); if (role is not ("user" or "system")) return;
            if (message["content"] is JsonValue text) message["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text.GetValue<string>(), ["cache_control"] = cache });
            else if (message["content"] is JsonArray blocks && blocks.Count > 0) blocks[^1]!["cache_control"] = cache;
        }
        private JsonArray ConvertTools()
        {
            var result = new JsonArray(); var names = new HashSet<string>(StringComparer.Ordinal);
            if (_nativeToolChanges) names.Add(DeferredPlaceholderName);
            var declarations = _nativeToolChanges ? _nativeDeclarations : _toolOrder.Select(key => _tools[key]).ToList();
            for (var position = 0; position < declarations.Count; position++)
            {
                token.ThrowIfCancellationRequested(); var name = ToolName(String(declarations[position], "name"));
                if (!names.Add(name)) throw Fail(AnthropicRequestFailure.IdentityCollision);
                var tool = ConvertTool(declarations[position], name);
                // Native changes keep the breakpoint on the last initial tool; the placeholder follows uncached.
                if (options.SupportsCacheControlOnTools && position == declarations.Count - 1 && Cache is { } cache) tool["cache_control"] = cache;
                Charge(tool); result.Add(tool);
            }
            if (_nativeToolChanges)
            {
                var placeholder = new JsonObject { ["name"] = DeferredPlaceholderName,
                    ["description"] = "Reserved placeholder. Never available. Never call this.",
                    ["input_schema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject(), ["required"] = new JsonArray() },
                    ["defer_loading"] = true };
                Charge(placeholder); result.Add(placeholder);
            }
            return result;
        }
        private JsonObject ConvertTool(JsonElement declaration, string name)
        {
            var parameters = declaration.GetProperty("parameters"); if (parameters.ValueKind != JsonValueKind.Object) throw Fail(AnthropicRequestFailure.UnsupportedContent);
            var schema = Node(parameters)!.AsObject(); var strict = false;
            if (declaration.TryGetProperty("constrainedSampling", out var config) && config.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
            {
                var kind = String(config, "type");
                if (kind == "json_schema")
                {
                    var preference = String(config, "strict"); if (preference is not ("prefer" or "require")) throw Fail(AnthropicRequestFailure.UnsupportedContent);
                    if (options.SupportsStrictTools)
                    {
                        try { var candidate = schema.DeepClone().AsObject(); MakeStrict(candidate); if (!IsType(candidate, "object")) throw new StrictSchemaException(); schema = candidate; strict = true; }
                        catch (StrictSchemaException) { if (preference == "require") throw Fail(AnthropicRequestFailure.UnsupportedStrictSchema); }
                    }
                    else if (preference == "require") throw Fail(AnthropicRequestFailure.UnsupportedStrictSchema);
                }
                else if (kind != "grammar") throw Fail(AnthropicRequestFailure.UnsupportedContent);
            }
            if (schema["properties"] is not (null or JsonObject) || schema["required"] is not (null or JsonArray) ||
                schema["required"] is JsonArray required && required.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw Fail(AnthropicRequestFailure.UnsupportedContent);
            var input = strict ? schema.DeepClone().AsObject() : new JsonObject();
            input["type"] = "object"; input["properties"] = schema["properties"]?.DeepClone() ?? new JsonObject(); input["required"] = schema["required"]?.DeepClone() ?? new JsonArray();
            var tool = new JsonObject { ["name"] = name, ["description"] = String(declaration, "description"), ["input_schema"] = input };
            if (options.SupportsEagerToolInputStreaming) tool["eager_input_streaming"] = true;
            if (strict) tool["strict"] = true;
            return tool;
        }
        private JsonArray Betas()
        {
            var values = new List<string>();
            if (!options.BetaFeatures.IsDefault)
            {
                if (options.BetaFeatures.Length > options.MaximumDeclarations) throw Fail(AnthropicRequestFailure.ResourceLimit);
                foreach (var value in options.BetaFeatures) { if (value is null) throw Fail(AnthropicRequestFailure.UnsupportedContent); Unicode(value); var text = Trim(value); if (text.Length > 0 && !values.Contains(text, StringComparer.Ordinal)) values.Add(text); }
            }
            else
            {
                if (options.OAuthProjection) values.AddRange(["claude-code-20250219", "oauth-2025-04-20"]);
                if (_tools.Count > 0 && !options.SupportsEagerToolInputStreaming) values.Add("fine-grained-tool-streaming-2025-05-14");
                if (options.ModelReasoning && options.ThinkingEnabled == true && options.InterleavedThinking && !options.ForceAdaptiveThinking) values.Add("interleaved-thinking-2025-05-14");
                if (!options.AllowedFallbackModels.IsDefaultOrEmpty) values.Add("server-side-fallback-2026-07-01");
                if (options.SupportsMidConversationEffort) values.AddRange(["mid-conversation-output-config-2026-07-01", "thinking-binding-controls-2026-08-01"]);
                if (_nativeToolChanges) values.Add("inline-tools-2026-09-15");
            }
            return new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }
        private void Charge(JsonNode node)
        {
            var raw = node.ToJsonString(OutputJson); _retainedCharacters += raw.Length; _retainedBytes += Encoding.UTF8.GetByteCount(raw);
            if (_retainedCharacters > options.MaximumOutputCharacters || _retainedBytes > options.MaximumOutputBytes) throw Fail(AnthropicRequestFailure.ResourceLimit);
        }
        private void MakeStrict(JsonNode? node)
        {
            token.ThrowIfCancellationRequested();
            if (node is not JsonObject schema || UnsupportedStrictKeys.Any(schema.ContainsKey) ||
                schema.Any(pair => AnthropicStrictUnsupported(pair.Key, pair.Value))) throw new StrictSchemaException();
            if (schema.ContainsKey("anyOf"))
            {
                if (schema["anyOf"] is not JsonArray variants || variants.Count == 0) throw new StrictSchemaException();
                foreach (var variant in variants)
                { if (variant is JsonObject value && (IsType(value, "object") || IsType(value, "array") || value["type"] is JsonArray types && types.Any(item => item?.ToJsonString() is "\"object\"" or "\"array\"") || value.ContainsKey("properties") || value.ContainsKey("items"))) throw new StrictSchemaException(); MakeStrict(variant); }
            }
            if (schema.ContainsKey("items")) MakeStrict(schema["items"]);
            if (schema.ContainsKey("properties") && !IsType(schema, "object")) throw new StrictSchemaException();
            if (!IsType(schema, "object")) return;
            if (schema.ContainsKey("additionalProperties") && schema["additionalProperties"]?.ToJsonString() != "false" ||
                schema.ContainsKey("properties") && schema["properties"] is not JsonObject ||
                schema.ContainsKey("required") && (schema["required"] is not JsonArray requiredArray || requiredArray.Any(item => item is not JsonValue value || !value.TryGetValue<string>(out _)))) throw new StrictSchemaException();
            var properties = schema["properties"] as JsonObject ?? new JsonObject(); var names = OrderedKeys(properties.Select(item => item.Key)).ToArray();
            var required = schema["required"] is JsonArray original ? original.Select(item => item!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) : new(StringComparer.Ordinal);
            if (required.Any(key => !properties.ContainsKey(key))) throw new StrictSchemaException();
            foreach (var name in names)
            {
                var property = properties[name]; MakeStrict(property);
                if (!required.Contains(name) && !AllowsNull(property)) properties[name] = new JsonObject { ["anyOf"] = new JsonArray(property?.DeepClone(), new JsonObject { ["type"] = "null" }) };
            }
            schema["required"] = new JsonArray(names.Select(name => (JsonNode?)JsonValue.Create(name)).ToArray()); schema["additionalProperties"] = false;
        }
        private void CheckJson(JsonElement value, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
            {
                if (++depth > options.MaximumJsonDepth) throw Fail(AnthropicRequestFailure.ResourceLimit);
                if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) { Unicode(property.Name); CheckJson(property.Value, depth); }
                else foreach (var item in value.EnumerateArray()) CheckJson(item, depth);
            }
            else if (value.ValueKind == JsonValueKind.String) Unicode(Text(value));
        }
    }
    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };
    private static JsonNode? Node(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var result = new JsonObject(); foreach (var property in Properties(value)) result[property.Name] = Node(property.Value); return result;
            case JsonValueKind.Array: return new JsonArray(value.EnumerateArray().Select(Node).ToArray());
            case JsonValueKind.String: return JsonValue.Create(Text(value));
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number)) throw Fail(AnthropicRequestFailure.UnsupportedNumber);
                return JsonNode.Parse(Number(number));
            case JsonValueKind.True: return JsonValue.Create(true);
            case JsonValueKind.False: return JsonValue.Create(false);
            case JsonValueKind.Null: return null;
            default: throw Fail(AnthropicRequestFailure.InvalidTranscript);
        }
    }
    private static string Number(double value)
    {
        if (!double.IsFinite(value)) throw Fail(AnthropicRequestFailure.UnsupportedNumber);
        if (value == 0) return "0";
        var negative = value < 0; var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOf('E'); var exponent = exponentAt >= 0 ? int.Parse(text.AsSpan(exponentAt + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
        var significand = exponentAt >= 0 ? text[..exponentAt] : text; var dot = significand.IndexOf('.');
        var position = (dot >= 0 ? dot : significand.Length) + exponent; var digits = significand.Replace(".", "", StringComparison.Ordinal);
        while (digits.Length > 1 && digits[0] == '0') { digits = digits[1..]; position--; }
        while (digits.Length > 1 && digits[^1] == '0') digits = digits[..^1];
        var prefix = negative ? "-" : "";
        if (position > 0 && position <= 21) return prefix + (position >= digits.Length ? digits + new string('0', position - digits.Length) : digits.Insert(position, "."));
        if (position <= 0 && position > -6) return prefix + "0." + new string('0', -position) + digits;
        var power = position - 1;
        return prefix + digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (power >= 0 ? "+" : "") + power.ToString(CultureInfo.InvariantCulture);
    }
    private static bool AnthropicStrictUnsupported(string key, JsonNode? value) => AnthropicStrictUnsupportedKeywords.Contains(key, StringComparer.Ordinal) ||
        key == "minItems" && !(value is JsonValue count && count.GetValueKind() == JsonValueKind.Number && count.GetValue<double>() is 0 or 1) ||
        key == "format" && !(value is JsonValue format && format.TryGetValue<string>(out var text) && AnthropicStrictStringFormats.Contains(text, StringComparer.Ordinal));
    private static bool IsType(JsonObject value, string type) => value["type"] is JsonValue node && node.TryGetValue<string>(out var actual) && actual == type;
    private static bool AllowsNull(JsonNode? node) => node is JsonObject schema && (IsType(schema, "null") ||
        schema["type"] is JsonArray types && types.Any(value => value?.ToJsonString() == "\"null\"") ||
        schema.ContainsKey("const") && schema["const"] is null || schema["enum"] is JsonArray values && values.Any(value => value is null) ||
        schema["anyOf"] is JsonArray variants && variants.Any(AllowsNull));
    private static bool Boolean(JsonFields? properties, string name)
    {
        if (properties?.TryGet(name, out var value) != true || value!.Value.ValueKind == JsonValueKind.Null) return false;
        return value.Value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.Value.GetBoolean() : throw Fail(AnthropicRequestFailure.UnsupportedContent);
    }
    private static string? OptionalString(JsonFields? properties, string name) => properties?.TryGet(name, out var value) == true && value!.Value.ValueKind != JsonValueKind.Null ? Text(value.Value) : null;
    private static string String(JsonElement value, string name) => Text(value.GetProperty(name));
    private static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Fail(AnthropicRequestFailure.InvalidTranscript);
        try { return value.GetString()!; } catch (ArgumentException) { throw Fail(AnthropicRequestFailure.UnsupportedUnicode); }
        catch (InvalidOperationException) { throw Fail(AnthropicRequestFailure.UnsupportedUnicode); }
    }
    private static JsonElement.ArrayEnumerator Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Fail(AnthropicRequestFailure.InvalidTranscript);
    private static IEnumerable<JsonProperty> Properties(JsonElement value) => value.ValueKind == JsonValueKind.Object
        ? value.EnumerateObject().Select((property, position) => (property, position, index: IndexKey(property.Name))).OrderBy(item => item.index is null ? 1 : 0).ThenBy(item => item.index ?? 0).ThenBy(item => item.position).Select(item => item.property)
        : throw Fail(AnthropicRequestFailure.InvalidTranscript);
    private static IEnumerable<string> OrderedKeys(IEnumerable<string> keys) => keys.Select((key, position) => (key, position, index: IndexKey(key)))
        .OrderBy(item => item.index is null ? 1 : 0).ThenBy(item => item.index ?? 0).ThenBy(item => item.position).Select(item => item.key);
    private static uint? IndexKey(string value) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
        number != uint.MaxValue && value == number.ToString(CultureInfo.InvariantCulture) ? number : null;
    private static string Trim(string value) => value.Trim(['\u0009', '\u000A', '\u000B', '\u000C', '\u000D', '\u0020', '\u00A0', '\u1680',
        '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006', '\u2007', '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F', '\u3000', '\uFEFF']);
    private static void Unicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index])) { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) throw Fail(AnthropicRequestFailure.UnsupportedUnicode); }
            else if (char.IsLowSurrogate(value[index])) throw Fail(AnthropicRequestFailure.UnsupportedUnicode);
    }
    private sealed class StrictSchemaException : Exception { }
}
