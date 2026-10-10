// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/provider-composer.ts (applyModelsJson,
// modelFromJson, findModelDefaults, applyModelOverride, mergeCompat, mergeInputLimits, mergeSamplingParamsByThinkingLevel,
// rawModelHeaders, resolveCompatibilityRequestConfig, configuredRequestAuthStatus, withConfiguredAuth).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;

namespace PiSharp.Cli.Models;

/// <summary>Source AuthStatus.</summary>
internal sealed record ProviderAuthStatus(bool Configured, string? Source = null, string? Label = null);

/// <summary>The models.json layer over a provider's catalog. Errors use the upstream texts; the registry prefixes the provider.</summary>
internal static class ModelProviderComposer
{
    private static readonly string[] NestedCompat = ["openRouterRouting", "vercelGatewayRouting", "chatTemplateKwargs", "chatTemplateArgs"];
    private static readonly string[] Levels = ["off", "minimal", "low", "medium", "high", "xhigh", "max"];

    private static bool Has(JsonObject? value, string name) => value is not null && value.TryGetPropertyValue(name, out var node) && node is not null;
    private static JsonNode? Get(JsonObject? value, string name) => value is not null && value.TryGetPropertyValue(name, out var node) ? node : null;
    private static void SetOrRemove(JsonObject target, string name, JsonNode? value)
    { if (value is null) target.Remove(name); else target[name] = value; }

    /// <summary>Source mergeCompat: a shallow spread with the routing and chat-template objects merged one level deeper.</summary>
    internal static JsonObject? MergeCompat(JsonObject? baseCompat, JsonObject? overrideCompat)
    {
        if (overrideCompat is null) return baseCompat is null ? null : JsonTree.CloneObject(baseCompat);
        var merged = JsonTree.Spread(baseCompat, overrideCompat);
        foreach (var key in NestedCompat)
        {
            var first = Get(baseCompat, key) as JsonObject; var second = Get(overrideCompat, key) as JsonObject;
            if (first is not null || second is not null) merged[key] = JsonTree.Spread(first, second);
        }
        return merged;
    }

    private static JsonObject? MergeInputLimits(JsonObject? baseLimits, JsonObject? overrideLimits)
    {
        if (overrideLimits is null) return baseLimits is null ? null : JsonTree.CloneObject(baseLimits);
        var merged = JsonTree.Spread(baseLimits, overrideLimits);
        var baseImages = Get(baseLimits, "images") as JsonObject;
        if (Get(overrideLimits, "images") is JsonObject images)
        {
            var mergedImages = JsonTree.Spread(baseImages, images);
            var baseResize = Get(baseImages, "resize") as JsonObject;
            SetOrRemove(mergedImages, "resize", Get(images, "resize") is JsonObject resize ? JsonTree.Spread(baseResize, resize) : baseResize?.DeepClone());
            merged["images"] = mergedImages;
        }
        else SetOrRemove(merged, "images", baseImages?.DeepClone());
        return merged;
    }

    private static JsonObject? MergeSamplingByLevel(JsonObject? baseMap, JsonObject? overrideMap)
    {
        if (overrideMap is null) return baseMap is null ? null : JsonTree.CloneObject(baseMap);
        var merged = JsonTree.Spread(baseMap, null);
        foreach (var level in Levels)
            if (Get(overrideMap, level) is JsonObject parameters) merged[level] = JsonTree.Spread(Get(baseMap, level) as JsonObject, parameters);
        return merged;
    }

    /// <summary>Source applyModelOverride (chat models only).</summary>
    internal static RegistryModel ApplyModelOverride(RegistryModel model, JsonObject @override) => model.With(json =>
    {
        void Replace(string name) { if (Has(@override, name)) json[name] = @override[name]!.DeepClone(); }
        JsonObject? Merge(string name) => Get(@override, name) is JsonObject value ? JsonTree.Spread(Get(json, name) as JsonObject, value) : null;
        Replace("name"); Replace("reasoning");
        if (Merge("thinkingLevelMap") is { } map) json["thinkingLevelMap"] = map;
        Replace("input");
        SetOrRemove(json, "inputLimits", MergeInputLimits(Get(json, "inputLimits") as JsonObject, Get(@override, "inputLimits") as JsonObject));
        if (Get(@override, "cost") is JsonObject cost)
        {
            var current = Get(json, "cost") as JsonObject;
            var next = new JsonObject();
            foreach (var field in new[] { "input", "output", "cacheRead", "cacheWrite", "tiers" })
                if ((Get(cost, field) ?? Get(current, field)) is { } value) next[field] = value.DeepClone();
            json["cost"] = next;
        }
        if (Merge("promptCache") is { } cache) json["promptCache"] = cache;
        Replace("contextWindow"); Replace("maxTokens");
        if (Merge("samplingParams") is { } sampling) json["samplingParams"] = sampling;
        SetOrRemove(json, "samplingParamsByThinkingLevel",
            MergeSamplingByLevel(Get(json, "samplingParamsByThinkingLevel") as JsonObject, Get(@override, "samplingParamsByThinkingLevel") as JsonObject));
        SetOrRemove(json, "compat", MergeCompat(Get(json, "compat") as JsonObject, Get(@override, "compat") as JsonObject));
    });

    /// <summary>Source findModelDefaults: the same chat id, else a model of the requested api, else an openai-completions model, else the
    /// first chat model.</summary>
    internal static RegistryModel? FindDefaults(IReadOnlyList<RegistryModel> models, string id, string? api)
    {
        var chat = models.Where(model => model.Type == CatalogModelType.Chat).ToList();
        return chat.FirstOrDefault(model => model.Id == id) ?? (api is null ? null : chat.FirstOrDefault(model => model.Api == api)) ??
            chat.FirstOrDefault(model => model.Api == "openai-completions") ?? chat.FirstOrDefault();
    }

    /// <summary>Source modelFromJson.</summary>
    internal static RegistryModel ModelFromJson(string providerId, JsonObject definition, JsonObject config, RegistryModel? defaults)
    {
        var id = JsonTree.String(definition, "id")!;
        var api = JsonTree.String(definition, "api") ?? JsonTree.String(config, "api") ?? (defaults?.Api is { Length: > 0 } inherited ? inherited : null);
        if (api is null) throw new InvalidOperationException($"Provider {providerId}, model {id}: no \"api\" specified. Set at provider or model level.");
        var baseUrl = JsonTree.String(definition, "baseUrl") ?? JsonTree.String(config, "baseUrl") ?? defaults?.BaseUrl;
        if (string.IsNullOrEmpty(baseUrl)) throw new InvalidOperationException($"Provider {providerId}: \"baseUrl\" is required when defining custom models.");
        if (JsonTree.Number(definition, "contextWindow") is <= 0) throw new InvalidOperationException($"Provider {providerId}, model {id}: invalid contextWindow");
        if (JsonTree.Number(definition, "maxTokens") is <= 0) throw new InvalidOperationException($"Provider {providerId}, model {id}: invalid maxTokens");
        var model = new JsonObject { ["id"] = id, ["name"] = JsonTree.String(definition, "name") ?? id, ["api"] = api, ["provider"] = providerId, ["baseUrl"] = baseUrl,
            ["reasoning"] = JsonTree.Boolean(definition, "reasoning") ?? false };
        void Copy(string name) { if (Get(definition, name) is { } value) model[name] = value.DeepClone(); }
        Copy("thinkingLevelMap");
        model["input"] = Get(definition, "input")?.DeepClone() ?? new JsonArray("text");
        Copy("inputLimits");
        model["cost"] = Get(definition, "cost")?.DeepClone() ?? new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 };
        Copy("promptCache");
        model["contextWindow"] = Get(definition, "contextWindow")?.DeepClone() ?? 128000;
        model["maxTokens"] = Get(definition, "maxTokens")?.DeepClone() ?? 16384;
        Copy("samplingParams"); Copy("samplingParamsByThinkingLevel");
        if (MergeCompat(Get(config, "compat") as JsonObject, Get(definition, "compat") as JsonObject) is { } compat) model["compat"] = compat;
        return RegistryModel.FromJson(model);
    }

    /// <summary>Source applyModelsJson: the provider's base models with baseUrl/compat overrides, then custom models upserted by chat id.</summary>
    internal static List<RegistryModel> ApplyModelsJson(string providerId, IReadOnlyList<RegistryModel> baseModels, JsonObject? config)
    {
        if (config is null) return [.. baseModels];
        var oauth = JsonTree.String(config, "oauth");
        var baseUrl = JsonTree.String(config, "baseUrl");
        if (oauth is not null && baseUrl is null) throw new InvalidOperationException($"Provider {providerId}: \"baseUrl\" is required when \"oauth\" is set.");
        var definitions = Get(config, "models") as JsonArray;
        var overrides = Get(config, "modelOverrides") as JsonObject;
        if ((definitions is null || definitions.Count == 0) && baseUrl is null && !Has(config, "headers") && !Has(config, "compat") &&
            (overrides is null || overrides.Count == 0) && !Has(config, "apiKey") && oauth is null && !config.ContainsKey("authHeader"))
            throw new InvalidOperationException($"Provider {providerId}: must specify \"baseUrl\", \"headers\", \"compat\", \"modelOverrides\", or \"models\".");
        var compat = Get(config, "compat") as JsonObject;
        var models = baseModels.Select(model =>
        {
            var url = oauth == "radius" ? model.BaseUrl : baseUrl ?? model.BaseUrl;
            var chatCompat = model.Type == CatalogModelType.Chat && compat is not null;
            if (url == model.BaseUrl && !chatCompat) return model;
            return model.With(json =>
            {
                json["baseUrl"] = url;
                if (chatCompat) SetOrRemove(json, "compat", MergeCompat(Get(json, "compat") as JsonObject, compat));
            });
        }).ToList();
        foreach (var node in definitions ?? [])
        {
            var definition = (JsonObject)node!;
            var id = JsonTree.String(definition, "id")!;
            var existing = models.FindIndex(model => model.Type == CatalogModelType.Chat && model.Id == id);
            var defaults = FindDefaults(models, id, JsonTree.String(definition, "api") ?? JsonTree.String(config, "api"));
            var model = ModelFromJson(providerId, definition, config, defaults);
            if (existing >= 0) models[existing] = model; else models.Add(model);
        }
        return models;
    }

    /// <summary>Source applyExtension (provider-composer.ts): an extension's registerProvider config replaces the provider's models with its
    /// own definitions (defaults from a same-id model), or, without models, sets their baseUrl.</summary>
    internal static List<RegistryModel> ApplyExtension(string providerId, IReadOnlyList<RegistryModel> models, JsonObject? config)
    {
        if (config is null) return [.. models];
        if (Get(config, "models") is not JsonArray definitions)
            return JsonTree.String(config, "baseUrl") is { } baseUrl ? [.. models.Select(model => model.With(json => json["baseUrl"] = baseUrl))] : [.. models];
        return [.. definitions.OfType<JsonObject>().Select(definition => ModelFromJson(providerId, definition, config,
            FindDefaults(models, JsonTree.String(definition, "id")!, JsonTree.String(definition, "api") ?? JsonTree.String(config, "api"))))];
    }

    /// <summary>Source composeModelProvider getAllModels for the models.json layer: applyModelsJson, then modelOverrides on chat models.</summary>
    internal static List<RegistryModel> Compose(string providerId, IReadOnlyList<RegistryModel> baseModels, JsonObject? config)
    {
        var models = ApplyModelsJson(providerId, baseModels, config);
        if (Get(config, "modelOverrides") is not JsonObject overrides || overrides.Count == 0) return models;
        return models.Select(model => model.Type == CatalogModelType.Chat && Get(overrides, model.Id) is JsonObject @override
            ? ApplyModelOverride(model, @override) : model).ToList();
    }

    internal static IReadOnlyDictionary<string, string>? StringMap(JsonObject? value)
    {
        if (value is null) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in value) if (node is JsonValue text && text.TryGetValue<string>(out var s)) result[key] = s;
        return result;
    }

    /// <summary>Source configuredHeaders (models.json only).</summary>
    internal static IReadOnlyDictionary<string, string>? ConfiguredHeaders(JsonObject? config) => StringMap(Get(config, "headers") as JsonObject);

    /// <summary>Source rawModelHeaders: models.json override headers, then custom model headers (chat models only).</summary>
    internal static IReadOnlyDictionary<string, string>? RawModelHeaders(RegistryModel model, JsonObject? config)
    {
        if (model.Type != CatalogModelType.Chat || config is null) return null;
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Get(config, "modelOverrides") is JsonObject overrides && Get(overrides, model.Id) is JsonObject @override &&
            StringMap(Get(@override, "headers") as JsonObject) is { } overrideHeaders)
            foreach (var (key, value) in overrideHeaders) headers[key] = value;
        if (Get(config, "models") is JsonArray definitions &&
            definitions.OfType<JsonObject>().FirstOrDefault(entry => JsonTree.String(entry, "id") == model.Id) is { } definition &&
            StringMap(Get(definition, "headers") as JsonObject) is { } definitionHeaders)
            foreach (var (key, value) in definitionHeaders) headers[key] = value;
        return headers.Count > 0 ? headers : null;
    }

    /// <summary>Source configuredRequestAuthStatus: how models.json <c>apiKey</c> configures auth, without resolving it.</summary>
    internal static ProviderAuthStatus? ConfiguredRequestAuthStatus(JsonObject? config, ConfigValueResolver resolver)
    {
        if (ApiKey(config) is not { } value) return null;
        if (ConfigValueResolver.IsCommand(value)) return new(true, "models_json_command");
        var names = ConfigValueResolver.GetEnvVarNames(value);
        if (names.Count > 0) return resolver.IsConfigured(value) ? new(true, "environment", string.Join(", ", names)) : new(false);
        return new(true, "models_json_key");
    }

    /// <summary>Source withConfiguredAuth: configured headers over the auth headers, and <c>Authorization: Bearer</c> for authHeader.</summary>
    internal static Dictionary<string, string>? WithConfiguredAuth(string? apiKey, IReadOnlyDictionary<string, string>? authHeaders,
        IReadOnlyDictionary<string, string>? configured, bool authHeader)
    {
        Dictionary<string, string>? merged = authHeaders is null && configured is null ? null : new(StringComparer.Ordinal);
        foreach (var source in new[] { authHeaders, configured })
            if (source is not null) foreach (var (key, value) in source) merged![key] = value;
        if (authHeader)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("authHeader requires a resolved API key");
            merged ??= new(StringComparer.Ordinal);
            merged["Authorization"] = "Bearer " + apiKey;
        }
        return merged;
    }

    /// <summary>model-runtime mergeHeaders: an override replaces every case-insensitively equal name.</summary>
    internal static Dictionary<string, string>? MergeHeaders(IReadOnlyDictionary<string, string>? first, IReadOnlyDictionary<string, string>? second)
    {
        if (first is null && second is null) return null;
        var merged = new Dictionary<string, string>(first ?? ImmutableDictionary<string, string>.Empty, StringComparer.Ordinal);
        foreach (var (name, value) in second ?? ImmutableDictionary<string, string>.Empty)
        {
            foreach (var existing in merged.Keys.Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList()) merged.Remove(existing);
            merged[name] = value;
        }
        return merged;
    }

    internal static bool AuthHeader(JsonObject? config) => config is not null && (JsonTree.Boolean(config, "authHeader") ?? false);
    internal static string? ApiKey(JsonObject? config) => config is null ? null : JsonTree.String(config, "apiKey");
}
