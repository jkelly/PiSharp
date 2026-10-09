// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/model-config.ts,
// packages/coding-agent/src/utils/json.ts (stripJsonComments) and utils/text.ts (stripBom).
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Models;

/// <summary>One immutable, credential-blind load of <c>models.json</c>. A missing file is an empty config; a read, parse or schema
/// failure is an empty config with the upstream error text.</summary>
internal sealed partial class ModelsJsonConfig
{
    private readonly ImmutableDictionary<string, JsonObject> providers;
    private readonly ImmutableArray<string> order;
    internal string? Error { get; }

    private ModelsJsonConfig(IEnumerable<(string Id, JsonObject Provider)> providers, string? error)
    {
        var list = providers.ToList();
        this.providers = list.ToImmutableDictionary(pair => pair.Id, pair => pair.Provider, StringComparer.Ordinal);
        order = [.. list.Select(pair => pair.Id)];
        Error = error;
    }

    internal static ModelsJsonConfig Empty { get; } = new([], null);

    internal JsonObject? GetProvider(string id) => providers.TryGetValue(id, out var provider) ? provider : null;
    internal ImmutableArray<string> ProviderIds => order;

    /// <summary>Source ModelConfig.load: null path or a missing file is empty.</summary>
    internal static ModelsJsonConfig Load(string? path)
    {
        if (path is null) return Empty;
        string content;
        try { content = File.ReadAllText(path, new System.Text.UTF8Encoding(false, false)); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return Empty; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new([], $"Failed to load models.json: {error.Message}\n\nFile: {path}"); }
        return Parse(content, path);
    }

    internal static ModelsJsonConfig Parse(string content, string path)
    {
        JsonNode? parsed;
        var stripped = StripJsonComments(StripBom(content));
        try { parsed = JsonTree.Parse(stripped); }
        catch (JsonException error)
        { return new([], $"Failed to parse models.json: {PiSharp.Contracts.Compatibility.JsJsonSyntax.Describe(stripped, error.Message)}\n\nFile: {path}"); }
        var errors = new List<string>();
        ModelsConfigSchema.Validate(parsed, "", errors);
        if (errors.Count > 0)
            return new([], $"Invalid models.json schema:\n{string.Join("\n", errors)}\n\nFile: {path}");
        var root = (JsonObject)parsed!;
        return new(((JsonObject)root["providers"]!).Select(pair => (pair.Key, JsonTree.CloneObject((JsonObject)pair.Value!))), null);
    }

    internal static string StripBom(string content) => content.Length > 0 && content[0] == '﻿' ? content[1..] : content;

    /// <summary>Source stripJsonComments: <c>//</c> line comments and trailing commas outside string literals.</summary>
    internal static string StripJsonComments(string input)
    {
        var withoutComments = LineComments().Replace(input, match => match.Value[0] == '"' ? match.Value : "");
        return TrailingCommas().Replace(withoutComments, match => match.Groups[1].Success ? match.Groups[1].Value : match.Value[0] == '"' ? match.Value : "");
    }

    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|//[^\\n]*")]
    private static partial Regex LineComments();
    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|,(\\s*[}\\]])")]
    private static partial Regex TrailingCommas();
}

/// <summary>The models.json TypeBox schema of model-config.ts, checked structurally. Error lines are
/// <c>  - path: message</c> with formatValidationPath paths and TypeBox's English messages (authored from the TypeBox 1.3 locale).</summary>
internal static class ModelsConfigSchema
{
    internal abstract record Schema;
    internal sealed record Obj(ImmutableArray<(string Name, Schema Schema, bool Optional)> Properties) : Schema;
    internal sealed record Rec(Schema Value) : Schema;
    internal sealed record Arr(Schema Item, int? MaxItems = null) : Schema;
    internal sealed record Str(int? MinLength = null) : Schema;
    internal sealed record Num(double? ExclusiveMinimum = null) : Schema;
    internal sealed record Int(double? Minimum = null, double? Maximum = null) : Schema;
    internal sealed record Bool : Schema;
    internal sealed record Null : Schema;
    internal sealed record Lit(string Value) : Schema;
    internal sealed record Union(ImmutableArray<Schema> Options) : Schema;
    internal sealed record Unknown : Schema;

    private static (string, Schema, bool) O(string name, Schema schema) => (name, schema, true);
    private static (string, Schema, bool) R(string name, Schema schema) => (name, schema, false);
    private static Obj Object(params (string, Schema, bool)[] properties) => new([.. properties]);
    private static Union AnyOf(params Schema[] options) => new([.. options]);
    private static Union Literals(params string[] values) => new([.. values.Select(value => (Schema)new Lit(value))]);
    private static readonly Schema S = new Str(), B = new Bool(), N = new Num();
    private static readonly Schema NonEmpty = new Str(1);

    private static readonly Schema Percentiles = Object(O("p50", N), O("p75", N), O("p90", N), O("p99", N));
    private static readonly Schema OpenRouterRouting = Object(O("allow_fallbacks", B), O("require_parameters", B),
        O("data_collection", Literals("deny", "allow")), O("zdr", B), O("enforce_distillable_text", B), O("order", new Arr(S)),
        O("only", new Arr(S)), O("ignore", new Arr(S)), O("quantizations", new Arr(S)),
        O("sort", AnyOf(S, Object(O("by", S), O("partition", AnyOf(S, new Null()))))),
        O("max_price", Object(O("prompt", AnyOf(N, S)), O("completion", AnyOf(N, S)), O("image", AnyOf(N, S)), O("audio", AnyOf(N, S)), O("request", AnyOf(N, S)))),
        O("preferred_min_throughput", AnyOf(N, Percentiles)), O("preferred_max_latency", AnyOf(N, Percentiles)));
    private static readonly Schema VercelRouting = Object(O("only", new Arr(S)), O("order", new Arr(S)));
    private static readonly string[] Levels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];
    private static readonly Schema LevelMap = new Obj([.. Levels.Select(level => O(level, AnyOf(S, new Null())))]);
    private static readonly Schema Sampling = new Rec(new Unknown());
    private static readonly Schema SamplingByLevel = new Obj([.. Levels.Select(level => O(level, Sampling))]);
    private static readonly Schema KwargScalar = AnyOf(S, N, B, new Null());
    private static readonly Schema Kwarg = AnyOf(KwargScalar, Object(R("$var", Literals("thinking.enabled", "thinking.effort")), O("omitWhenOff", B)));
    private static readonly Schema CompletionsCompat = Object(O("supportsStore", B), O("supportsDeveloperRole", B), O("supportsReasoningEffort", B),
        O("supportsUsageInStreaming", B), O("supportsFinishReason", B), O("maxTokensField", Literals("max_completion_tokens", "max_tokens")),
        O("requiresToolResultName", B), O("requiresAssistantAfterToolResult", B), O("requiresThinkingAsText", B),
        O("requiresReasoningContentOnAssistantMessages", B),
        O("thinkingFormat", Literals("openai", "openrouter", "together", "baseten", "deepseek", "zai", "qwen", "chat-template",
            "qwen-chat-template", "string-thinking", "ant-ling")),
        O("chatTemplateKwargs", new Rec(Kwarg)), O("chatTemplateArgs", new Rec(Kwarg)), O("cacheControlFormat", new Lit("anthropic")),
        O("openRouterRouting", OpenRouterRouting), O("vercelGatewayRouting", VercelRouting), O("supportsOpenAIGrammarTools", B),
        O("supportsStrictMode", B), O("sendSessionAffinityHeaders", B),
        O("sessionAffinityFormat", Literals("openai", "openai-nosession", "openrouter")), O("supportsLongCacheRetention", B), O("vllmPriority", N));
    private static readonly Schema ResponsesCompat = Object(O("supportsDeveloperRole", B),
        O("sessionAffinityFormat", Literals("openai", "openai-nosession", "openrouter")), O("supportsLongCacheRetention", B),
        O("supportsStrictMode", B), O("supportsOpenAIGrammarTools", B), O("supportsMaxOutputTokens", B));
    private static readonly (string, Schema, bool)[] Rates = [R("input", N), R("output", N), R("cacheRead", N), R("cacheWrite", N)];
    private static readonly Schema Tier = Object([R("inputTokensAbove", N), .. Rates]);
    private static readonly Schema Cost = Object([.. Rates, O("tiers", new Arr(Tier))]);
    private static readonly Schema PromptCache = Object(O("short", new Num(0)), O("long", new Num(0)));
    private static readonly Schema Resize = Object(O("maxWidth", new Int(1)), O("maxHeight", new Int(1)), O("maxBytes", new Int(1)), O("jpegQuality", new Int(1, 100)));
    private static readonly Schema InputLimits = Object(O("maxRequestBytes", new Int(1)),
        O("images", Object(O("resize", Resize), O("maxPerMessage", new Int(1)), O("maxPerRequest", new Int(1)))));
    private static readonly Schema AnthropicCompat = Object(O("supportsEagerToolInputStreaming", B), O("supportsLongCacheRetention", B),
        O("sendSessionAffinityHeaders", B), O("supportsCacheControlOnTools", B), O("supportsTemperature", B), O("forceAdaptiveThinking", B),
        O("allowEmptySignature", B), O("supportsStrictTools", B), O("supportsMidConvoEffort", B),
        O("allowedFallbackModels", new Arr(Object(R("provider", NonEmpty), R("model", NonEmpty), R("cost", Cost)), 3)));
    private static readonly Schema Compat = AnyOf(CompletionsCompat, ResponsesCompat, AnthropicCompat);
    private static readonly Schema InputKinds = new Arr(Literals("text", "image"));
    private static readonly Schema StringMap = new Rec(S);
    private static readonly Schema ModelDefinition = Object(R("id", NonEmpty), O("name", NonEmpty), O("api", NonEmpty), O("baseUrl", NonEmpty),
        O("reasoning", B), O("thinkingLevelMap", LevelMap), O("input", InputKinds), O("inputLimits", InputLimits), O("cost", Cost),
        O("promptCache", PromptCache), O("contextWindow", N), O("maxTokens", N), O("samplingParams", Sampling),
        O("samplingParamsByThinkingLevel", SamplingByLevel), O("headers", StringMap), O("compat", Compat));
    private static readonly Schema ModelOverride = Object(O("name", NonEmpty), O("reasoning", B), O("thinkingLevelMap", LevelMap),
        O("input", InputKinds), O("inputLimits", InputLimits),
        O("cost", Object(O("input", N), O("output", N), O("cacheRead", N), O("cacheWrite", N), O("tiers", new Arr(Tier)))),
        O("promptCache", PromptCache), O("contextWindow", N), O("maxTokens", N), O("samplingParams", Sampling),
        O("samplingParamsByThinkingLevel", SamplingByLevel), O("headers", StringMap), O("compat", Compat));
    private static readonly Schema ProviderConfig = Object(O("name", NonEmpty), O("baseUrl", NonEmpty), O("apiKey", NonEmpty), O("api", NonEmpty),
        O("oauth", new Lit("radius")), O("headers", StringMap), O("compat", Compat), O("authHeader", B), O("models", new Arr(ModelDefinition)),
        O("modelOverrides", new Rec(ModelOverride)));
    internal static readonly Schema Root = Object(R("providers", new Rec(ProviderConfig)));

    /// <summary>Validates <paramref name="value"/> against the models.json schema, appending formatted error lines.</summary>
    internal static void Validate(JsonNode? value, string path, List<string> errors) => Check(Root, value, path, errors);

    private static string Format(string path) => path.Length == 0 ? "root" : path;
    private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;

    private static bool Matches(Schema schema, JsonNode? value)
    {
        var scratch = new List<string>(); Check(schema, value, "", scratch); return scratch.Count == 0;
    }

    private static void Check(Schema schema, JsonNode? value, string path, List<string> errors)
    {
        void Fail(string message) => errors.Add($"  - {Format(path)}: {message}");
        var kind = value?.GetValueKind() ?? JsonValueKind.Null;
        switch (schema)
        {
            case Unknown: return;
            case Null: if (kind != JsonValueKind.Null) Fail("must be null"); return;
            case Bool: if (kind is not (JsonValueKind.True or JsonValueKind.False)) Fail("must be boolean"); return;
            case Lit literal:
                if (kind != JsonValueKind.String || value!.GetValue<string>() != literal.Value) Fail("must be equal to constant");
                return;
            case Str text:
                if (kind != JsonValueKind.String) { Fail("must be string"); return; }
                if (text.MinLength is { } minLength && value!.GetValue<string>().Length < minLength)
                    Fail($"must not have fewer than {minLength} characters");
                return;
            case Num number:
                if (kind != JsonValueKind.Number) { Fail("must be number"); return; }
                if (number.ExclusiveMinimum is { } exclusive && !(JsonTree.Double((JsonValue)value!) > exclusive))
                    Fail($"must be > {exclusive.ToString(CultureInfo.InvariantCulture)}");
                return;
            case Int integer:
            {
                var actual = kind == JsonValueKind.Number ? JsonTree.Double((JsonValue)value!) : double.NaN;
                if (!double.IsFinite(actual) || actual != Math.Floor(actual)) { Fail("must be integer"); return; }
                if (integer.Minimum is { } minimum && actual < minimum) Fail($"must be >= {minimum.ToString(CultureInfo.InvariantCulture)}");
                if (integer.Maximum is { } maximum && actual > maximum) Fail($"must be <= {maximum.ToString(CultureInfo.InvariantCulture)}");
                return;
            }
            case Arr array:
            {
                if (value is not JsonArray items) { Fail("must be array"); return; }
                if (array.MaxItems is { } limit && items.Count > limit) Fail($"must not have more than {limit} items");
                for (var index = 0; index < items.Count; index++) Check(array.Item, items[index], Join(path, index.ToString(CultureInfo.InvariantCulture)), errors);
                return;
            }
            case Rec record:
            {
                if (value is not JsonObject entries) { Fail("must be object"); return; }
                foreach (var (key, entry) in entries) Check(record.Value, entry, Join(path, key), errors);
                return;
            }
            case Obj shape:
            {
                if (value is not JsonObject entries) { Fail("must be object"); return; }
                var missing = shape.Properties.Where(property => !property.Optional && !entries.ContainsKey(property.Name)).Select(property => property.Name).ToList();
                if (missing.Count > 0) errors.Add($"  - {Join(path, missing[0])}: must have required properties {string.Join(", ", missing)}");
                foreach (var (name, propertySchema, _) in shape.Properties)
                    if (entries.TryGetPropertyValue(name, out var property)) Check(propertySchema, property, Join(path, name), errors);
                return;
            }
            case Union union:
                if (!union.Options.Any(option => Matches(option, value))) Fail("must match a schema in anyOf");
                return;
        }
    }
}
