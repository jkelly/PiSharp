using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;
using PiSharp.Contracts.Compatibility;

namespace PiSharp.AI.Protocols.OpenAICompletions;

public enum CompletionsRequestFailure { InvalidConfiguration, InvalidRequest, InvalidKey, InvalidTranscript, UnsupportedContent, ResourceLimit }
public sealed class CompletionsRequestException : Exception
{
    public CompletionsRequestFailure Failure { get; }
    internal CompletionsRequestException(CompletionsRequestFailure failure) : base(failure switch
    {
        CompletionsRequestFailure.InvalidRequest => "Completions request does not match the configured model.",
        CompletionsRequestFailure.InvalidKey => "Invalid explicit Completions API key.",
        CompletionsRequestFailure.InvalidTranscript => "Invalid Completions transcript.",
        CompletionsRequestFailure.UnsupportedContent => "Completions request requires unfinished content or option support.",
        CompletionsRequestFailure.ResourceLimit => "Completions request exceeds configured limits.",
        _ => "Invalid Completions request configuration."
    }) => Failure = failure;
}

public sealed record CompletionsToolDeclarationProjectionOptions(bool SupportsStrictMode = false,
    int MaximumMessages = 256, int MaximumEntryCharacters = 65_536, int MaximumInputCharacters = 1_048_576,
    int MaximumDeclarations = 1024, int MaximumActiveTools = 128, int MaximumJsonDepth = 32,
    int MaximumOutputCharacters = 1_048_576, int MaximumOutputBytes = 1_048_576)
{
    public bool SupportsOpenAIGrammarTools { get; init; }
}

/// <summary>Ordered system-message declaration replay, projected into Completions function tools.</summary>
public sealed class CompletionsToolDeclarationProjector
{
    private readonly CompletionsToolDeclarationProjectionOptions _options;
    private readonly ResponsesToolDeclarationProjector _declarations;
    public CompletionsToolDeclarationProjector(CompletionsToolDeclarationProjectionOptions? options = null)
    {
        _options = options ?? new();
        try
        {
            _declarations = new(new(SupportsStrictMode: _options.SupportsStrictMode, Strict: false,
                MaximumMessages: _options.MaximumMessages, MaximumEntryCharacters: _options.MaximumEntryCharacters,
                MaximumInputCharacters: _options.MaximumInputCharacters, MaximumDeclarations: _options.MaximumDeclarations,
                MaximumActiveTools: _options.MaximumActiveTools, MaximumJsonDepth: _options.MaximumJsonDepth,
                MaximumOutputCharacters: _options.MaximumOutputCharacters, MaximumOutputBytes: _options.MaximumOutputBytes));
        }
        catch (ArgumentException) { throw CompletionsJson.Fail(CompletionsRequestFailure.InvalidConfiguration); }
    }
    public JsonData Project(ChatRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            if (request.Messages.IsDefault) throw CompletionsJson.Fail(CompletionsRequestFailure.InvalidTranscript);
            if (request.Messages.Length > _options.MaximumMessages) throw CompletionsJson.Fail(CompletionsRequestFailure.ResourceLimit);
            long inputCharacters = 0; var replay = ImmutableArray.CreateBuilder<TranscriptEntry>();
            var grammars = new Dictionary<string, CompletionsGrammar>(StringComparer.Ordinal);
            foreach (var entry in request.Messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is null || entry.WireBody is null) throw CompletionsJson.Fail(CompletionsRequestFailure.InvalidTranscript);
                var length = entry.WireBody.ToString().Length;
                if (length > _options.MaximumEntryCharacters || length > _options.MaximumInputCharacters - inputCharacters)
                    throw CompletionsJson.Fail(CompletionsRequestFailure.ResourceLimit);
                inputCharacters += length; var body = CompletionsJson.Strict(entry.WireBody, _options.MaximumJsonDepth, cancellationToken);
                if (entry.Role == "system" && body.Value.TryGetProperty("toolsAdded", out var added) && added.ValueKind == JsonValueKind.Array)
                {
                    if (_options.SupportsOpenAIGrammarTools && body.Value.TryGetProperty("toolsRemoved", out var removed) && removed.ValueKind == JsonValueKind.Array)
                        foreach (var reference in removed.EnumerateArray()) grammars.Remove(CompletionsJson.String(reference, "name"));
                    JsonObject? projected = null; var index = 0;
                    foreach (var declaration in added.EnumerateArray())
                    {
                        if (_options.SupportsOpenAIGrammarTools && declaration.ValueKind == JsonValueKind.Object)
                        {
                            var name = CompletionsJson.String(declaration, "name");
                            var grammar = CompletionsGrammar.Resolve(declaration, _options.SupportsOpenAIGrammarTools);
                            if (grammar is null) grammars.Remove(name); else grammars[name] = grammar;
                        }
                        if (declaration.ValueKind == JsonValueKind.Object && declaration.TryGetProperty("constrainedSampling", out var sampling) &&
                            (sampling.ValueKind == JsonValueKind.Null || sampling.ValueKind == JsonValueKind.Object &&
                            sampling.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "grammar"))
                        {
                            // The shared helper validates schema/replay; provider-local grammar replaces only the wire declaration.
                            projected ??= JsonNode.Parse(body.ToString())!.AsObject();
                            projected["toolsAdded"]!.AsArray()[index]!.AsObject().Remove("constrainedSampling");
                        }
                        index++;
                    }
                    if (projected is not null) body = JsonData.Parse(projected.ToJsonString(CompletionsJson.Output));
                }
                else if (_options.SupportsOpenAIGrammarTools && entry.Role == "system" && body.Value.TryGetProperty("toolsRemoved", out var removedOnly) && removedOnly.ValueKind == JsonValueKind.Array)
                    foreach (var reference in removedOnly.EnumerateArray()) grammars.Remove(CompletionsJson.String(reference, "name"));
                replay.Add(new(entry.Role, body));
            }
            // The accepted helper owns standard schema preference/require handling and replay order.
            var declarations = _declarations.Project(request with { Messages = replay.ToImmutable() }, cancellationToken);
            var result = new CompletionsJson.ArrayBudget(_options.MaximumActiveTools, _options.MaximumOutputCharacters,
                _options.MaximumOutputBytes, _options.MaximumJsonDepth, cancellationToken);
            foreach (var declaration in declarations.Value.EnumerateArray())
            {
                if (grammars.TryGetValue(CompletionsJson.String(declaration, "name"), out var grammar))
                {
                    result.Add(new JsonObject { ["type"] = "custom", ["custom"] = new JsonObject
                    {
                        ["name"] = CompletionsJson.String(declaration, "name"),
                        ["description"] = declaration.TryGetProperty("description", out var description) ? JsonNode.Parse(description.GetRawText()) : null,
                        ["format"] = new JsonObject { ["type"] = "grammar", ["grammar"] = new JsonObject
                            { ["syntax"] = grammar.Syntax, ["definition"] = grammar.Definition } }
                    } });
                    continue;
                }
                var function = new JsonObject();
                foreach (var property in declaration.EnumerateObject())
                    if (property.Name != "type") function[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                result.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            return CompletionsJson.Source(result.Own(), _options.MaximumOutputCharacters, _options.MaximumOutputBytes,
                _options.MaximumJsonDepth, cancellationToken);
        }
        catch (ResponsesProjectionException error)
        {
            throw CompletionsJson.Fail(error.Failure == ResponsesProjectionFailure.ResourceLimit
                ? CompletionsRequestFailure.ResourceLimit : CompletionsRequestFailure.UnsupportedContent);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw CompletionsJson.Fail(CompletionsRequestFailure.InvalidTranscript); }
    }

    internal IReadOnlyDictionary<string, string> GrammarInputProperties(ChatRequest request, CancellationToken token)
    {
        _ = Project(request, token); // Admit the entire history and its existing resource limits first.
        var declared = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var entry in request.Messages)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Role == "system" && entry.WireBody.Value.TryGetProperty("toolsAdded", out var added))
                foreach (var tool in added.EnumerateArray()) declared[CompletionsJson.String(tool, "name")] = tool;
        }
        // Pi's getDeclaredTools ignores removals and uses the final declaration for each name.
        return declared.Select(pair => (pair.Key, Grammar: CompletionsGrammar.Resolve(pair.Value, _options.SupportsOpenAIGrammarTools)))
            .Where(pair => pair.Grammar is not null).ToDictionary(pair => pair.Key, pair => pair.Grammar!.InputProperty, StringComparer.Ordinal);
    }

    internal JsonData ProjectRequestTools(ChatRequest request, bool supportsAdditions, CancellationToken token)
    {
        // Validate and charge the whole declaration history even when only its initial tools
        // belong at request level. Later declarations still consume the configured budgets.
        var current = Project(request, token);
        if (!supportsAdditions || !CanAnchorAdditions(request, token)) return current;
        return Project(request with { Messages = request.Messages.Length != 0 && request.Messages[0].Role == "system"
            ? [request.Messages[0]] : [] }, token);
    }

    internal static bool CanAnchorAdditions(ChatRequest request, CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in request.Messages)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Role != "system") continue;
            var body = entry.WireBody.Value;
            if (body.TryGetProperty("toolsRemoved", out var removed) && removed.GetArrayLength() != 0) return false;
            if (body.TryGetProperty("toolsAdded", out var added))
                foreach (var tool in added.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    // Source falls back for every redeclaration, including identical definitions.
                    if (!names.Add(CompletionsJson.String(tool, "name"))) return false;
                }
        }
        return true;
    }
}

internal sealed record CompletionsGrammar(string Syntax, string Definition, string InputProperty)
{
    internal static CompletionsGrammar? Resolve(JsonElement tool, bool enabled)
    {
        if (!enabled || !tool.TryGetProperty("constrainedSampling", out var sampling) || sampling.ValueKind == JsonValueKind.Null ||
            sampling.ValueKind != JsonValueKind.Object || !sampling.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "grammar") return null;
        if (!sampling.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Object)
            throw CompletionsJson.Fail(CompletionsRequestFailure.UnsupportedContent);
        string? Variant(string name) => variants.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString()!.Trim().Length != 0 ? value.GetString() : null;
        var lark = Variant("openai_lark"); var regex = Variant("openai_regex");
        if (lark is null && regex is null) throw CompletionsJson.Fail(CompletionsRequestFailure.UnsupportedContent);
        if (!tool.TryGetProperty("parameters", out var schema) || schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var schemaType) || schemaType.ValueKind != JsonValueKind.String || schemaType.GetString() != "object" ||
            !schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array || required.GetArrayLength() != 1 ||
            required[0].ValueKind != JsonValueKind.String || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            throw CompletionsJson.Fail(CompletionsRequestFailure.UnsupportedContent);
        var inputProperty = CompletionsJson.Text(required[0]);
        if (!properties.TryGetProperty(inputProperty, out var property) || property.ValueKind != JsonValueKind.Object ||
            !property.TryGetProperty("type", out var propertyType) || propertyType.ValueKind != JsonValueKind.String || propertyType.GetString() != "string")
            throw CompletionsJson.Fail(CompletionsRequestFailure.UnsupportedContent);
        return new(lark is not null ? "lark" : "regex", lark ?? regex!, inputProperty);
    }
}

internal static class CompletionsJson
{
    internal static readonly JsonSerializerOptions Output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static CompletionsRequestException Fail(CompletionsRequestFailure failure) => new(failure);
    internal static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Fail(CompletionsRequestFailure.InvalidTranscript);
        var text = value.GetString()!; Unicode(text); return text;
    }
    internal static string String(JsonElement value, string name) => Text(value.GetProperty(name));
    internal static void Unicode(string text)
    {
        for (var index = 0; index < text.Length; index++)
            if (char.IsHighSurrogate(text[index]))
            { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) throw Fail(CompletionsRequestFailure.InvalidTranscript); }
            else if (char.IsLowSurrogate(text[index])) throw Fail(CompletionsRequestFailure.InvalidTranscript);
    }
    internal static void Check(JsonElement value, int depth, int maximumDepth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (++depth > maximumDepth) throw Fail(CompletionsRequestFailure.ResourceLimit);
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                { if (!names.Add(property.Name)) throw Fail(CompletionsRequestFailure.InvalidTranscript); Unicode(property.Name); Check(property.Value, depth, maximumDepth, token); }
            }
            else foreach (var item in value.EnumerateArray()) Check(item, depth, maximumDepth, token);
        }
        else if (value.ValueKind == JsonValueKind.String) _ = Text(value);
    }
    internal static JsonData Strict(JsonData data, int maximumDepth, CancellationToken token)
    {
        var owned = JsonData.Parse(data.ToString()); Check(owned.Value, 0, maximumDepth, token); return owned;
    }
    internal static JsonData Source(JsonData data, int maximumCharacters, int maximumBytes, int maximumDepth, CancellationToken token)
    {
        try
        {
            return JsonData.Parse(EcmaScriptJsonProjection.Project(data, new(MaximumInputCharacters: maximumCharacters,
                MaximumInputBytes: maximumBytes, MaximumOutputCharacters: maximumCharacters, MaximumOutputBytes: maximumBytes,
                MaximumDepth: maximumDepth, MaximumStringCharacters: maximumCharacters), token));
        }
        catch (EcmaScriptJsonProjectionException error)
        { throw Fail(error.Failure == EcmaScriptJsonProjectionFailure.ResourceLimit ? CompletionsRequestFailure.ResourceLimit : CompletionsRequestFailure.InvalidTranscript); }
    }
    internal static IEnumerable<JsonProperty> Properties(JsonElement value) => value.EnumerateObject()
        .Select((property, position) => (property, position, numeric: ArrayIndex(property.Name)))
        .OrderBy(item => item.numeric is null ? 1 : 0).ThenBy(item => item.numeric ?? 0).ThenBy(item => item.position)
        .Select(item => item.property);
    private static uint? ArrayIndex(string name) => uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
        value != uint.MaxValue && name == value.ToString(CultureInfo.InvariantCulture) ? value : null;
    internal sealed class ArrayBudget(int maximumItems, int maximumCharacters, int maximumBytes, int maximumDepth, CancellationToken token)
    {
        private readonly JsonArray _items = [];
        private long _characters = 2, _bytes = 2;
        internal void Add(JsonObject item)
        {
            token.ThrowIfCancellationRequested();
            var raw = item.ToJsonString(Output); var separator = _items.Count == 0 ? 0 : 1;
            if (_items.Count >= maximumItems || raw.Length + separator > maximumCharacters - _characters ||
                Encoding.UTF8.GetByteCount(raw) + (long)separator > maximumBytes - _bytes) throw Fail(CompletionsRequestFailure.ResourceLimit);
            using var document = JsonDocument.Parse(raw); Check(document.RootElement, 1, maximumDepth, token);
            _characters += raw.Length + separator; _bytes += Encoding.UTF8.GetByteCount(raw) + separator; _items.Add(item);
        }
        internal JsonData Own() { token.ThrowIfCancellationRequested(); return JsonData.Parse(_items.ToJsonString(Output)); }
    }
}
