// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/types.ts (BaseModel, ImageModel, ClassifierModel,
// ModelCost) and packages/ai/src/models.ts (calculateCost).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

namespace PiSharp.AI.ModelOperations;

/// <summary>types.ts <c>ModelCostTier</c>: rates per million tokens for requests whose total input exceeds a threshold.</summary>
public sealed record ModelCostTier(double InputTokensAbove, double Input, double Output, double CacheRead, double CacheWrite);

/// <summary>types.ts <c>ModelCost</c>: binary64 rates per million tokens, as the catalog declares them.</summary>
public sealed record ModelCost(double Input, double Output, double CacheRead, double CacheWrite, ImmutableArray<ModelCostTier> Tiers = default)
{
    public static ModelCost Free { get; } = new(0, 0, 0, 0);

    /// <summary>Reads a catalog <c>cost</c> object. Throws <see cref="FormatException"/>.</summary>
    public static ModelCost FromJson(JsonElement cost)
    {
        if (cost.ValueKind != JsonValueKind.Object) throw new FormatException("Model cost must be an object.");
        static double Rate(JsonElement table, string name) =>
            table.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var rate) && double.IsFinite(rate)
                ? rate : throw new FormatException($"Model cost {name} must be a finite number.");
        var tiers = ImmutableArray.CreateBuilder<ModelCostTier>();
        if (cost.TryGetProperty("tiers", out var declared) && declared.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            if (declared.ValueKind != JsonValueKind.Array) throw new FormatException("Model cost tiers must be an array.");
            foreach (var tier in declared.EnumerateArray())
            {
                if (tier.ValueKind != JsonValueKind.Object) throw new FormatException("Model cost tiers must be objects.");
                tiers.Add(new(Rate(tier, "inputTokensAbove"), Rate(tier, "input"), Rate(tier, "output"), Rate(tier, "cacheRead"), Rate(tier, "cacheWrite")));
            }
        }
        return new(Rate(cost, "input"), Rate(cost, "output"), Rate(cost, "cacheRead"), Rate(cost, "cacheWrite"), tiers.ToImmutable());
    }

    /// <summary>models.ts <c>calculateCost</c> for usage without 1h cache writes: the tier with the greatest threshold
    /// strictly below input + cacheRead + cacheWrite prices the whole request.</summary>
    public (double Input, double Output, double CacheRead, double CacheWrite, double Total) Calculate(double input, double output,
        double cacheRead, double cacheWrite)
    {
        var inputTokens = input + cacheRead + cacheWrite;
        (double Input, double Output, double CacheRead, double CacheWrite) rates = (Input, Output, CacheRead, CacheWrite);
        var matched = -1d;
        foreach (var tier in Tiers.IsDefault ? [] : Tiers)
            if (inputTokens > tier.InputTokensAbove && tier.InputTokensAbove > matched)
            { rates = (tier.Input, tier.Output, tier.CacheRead, tier.CacheWrite); matched = tier.InputTokensAbove; }
        var inputCost = rates.Input / 1000000 * input;
        var outputCost = rates.Output / 1000000 * output;
        var readCost = rates.CacheRead / 1000000 * cacheRead;
        // Anthropic's 2x 1h write rate does not apply: classifier and image usage reports no 1h writes.
        var writeCost = (rates.CacheWrite * cacheWrite + rates.Input * 2 * 0) / 1000000;
        return (inputCost, outputCost, readCost, writeCost, inputCost + outputCost + readCost + writeCost);
    }
}

/// <summary>types.ts <c>BaseModel</c>: one catalog entry of any type. <see cref="Metadata"/> is the whole catalog
/// object, the form scripts and extensions read.</summary>
public abstract record OperationModel(string Id, string Name, string Api, string Provider, string BaseUrl,
    ImmutableArray<string> Input, ModelCost Cost)
{
    public abstract ModelType Type { get; }
    /// <summary>Model headers (<c>headers</c>), merged under the request's auth and option headers.</summary>
    public ImmutableArray<KeyValuePair<string, string>> Headers { get; init; } = [];
    /// <summary>The catalog object this entry came from, or a projection of the typed fields.</summary>
    public JsonData? Metadata { get; init; }
    public ModelDescriptor Descriptor => new(Id, Api, Provider);
    public bool AcceptsImages => !Input.IsDefault && Input.Contains("image");

    /// <summary>The catalog JSON with <c>baseUrl</c> replaced when auth resolution moved it.</summary>
    public JsonData ToJson()
    {
        var node = Metadata is { } metadata ? JsonNode.Parse(metadata.ToString())!.AsObject() : Project();
        node["baseUrl"] = BaseUrl;
        return JsonData.Parse(node.ToJsonString());
    }

    /// <summary>The same JSON without <c>headers</c>, which can carry credentials (codemode <c>toModelInfo</c>).</summary>
    public JsonData ToPublicJson()
    {
        var node = JsonNode.Parse(ToJson().ToString())!.AsObject();
        node.Remove("headers");
        return JsonData.Parse(node.ToJsonString());
    }

    private protected virtual JsonObject Project()
    {
        var node = new JsonObject
        {
            ["type"] = Type switch { ModelType.Image => "image", ModelType.Classifier => "classifier", _ => "chat" },
            ["id"] = Id, ["name"] = Name, ["api"] = Api, ["provider"] = Provider, ["baseUrl"] = BaseUrl,
            ["input"] = new JsonArray([.. Input.Select(item => (JsonNode)item)]),
            ["cost"] = new JsonObject { ["input"] = Cost.Input, ["output"] = Cost.Output, ["cacheRead"] = Cost.CacheRead, ["cacheWrite"] = Cost.CacheWrite }
        };
        if (!Cost.Tiers.IsDefaultOrEmpty)
            node["cost"]!["tiers"] = new JsonArray([.. Cost.Tiers.Select(tier => (JsonNode)new JsonObject
            {
                ["inputTokensAbove"] = tier.InputTokensAbove, ["input"] = tier.Input, ["output"] = tier.Output,
                ["cacheRead"] = tier.CacheRead, ["cacheWrite"] = tier.CacheWrite
            })]);
        if (!Headers.IsDefaultOrEmpty)
        {
            var headers = new JsonObject();
            foreach (var (name, value) in Headers) headers[name] = value;
            node["headers"] = headers;
        }
        return node;
    }

    /// <summary>Reads one catalog object of any type (<c>type</c> absent means chat). Throws <see cref="FormatException"/>.</summary>
    public static OperationModel FromJson(JsonData value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = value.Value;
        if (json.ValueKind != JsonValueKind.Object) throw new FormatException("A model must be an object.");
        static string Text(JsonElement json, string name, bool allowEmpty = false) =>
            json.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && (allowEmpty || field.GetString()!.Length != 0)
                ? field.GetString()! : throw new FormatException($"Model {name} must be a string.");
        static ImmutableArray<string> Modalities(JsonElement json, string name) =>
            json.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.Array &&
            field.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String && item.GetString() is "text" or "image")
                ? [.. field.EnumerateArray().Select(item => item.GetString()!)] : throw new FormatException($"Model {name} must list text/image modalities.");
        var type = !json.TryGetProperty("type", out var typeValue) || typeValue.ValueKind == JsonValueKind.Null ? "chat"
            : typeValue.ValueKind == JsonValueKind.String ? typeValue.GetString() : throw new FormatException("Model type must be a string.");
        var id = Text(json, "id"); var name = json.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString()! : id;
        var api = Text(json, "api"); var provider = Text(json, "provider"); var baseUrl = Text(json, "baseUrl", allowEmpty: true);
        var input = Modalities(json, "input");
        var cost = json.TryGetProperty("cost", out var costValue) ? ModelCost.FromJson(costValue) : throw new FormatException("Model cost is required.");
        var headers = ImmutableArray.CreateBuilder<KeyValuePair<string, string>>();
        if (json.TryGetProperty("headers", out var headerValues) && headerValues.ValueKind == JsonValueKind.Object)
            foreach (var header in headerValues.EnumerateObject())
                if (header.Value.ValueKind == JsonValueKind.String) headers.Add(new(header.Name, header.Value.GetString()!));
        OperationModel model = type switch
        {
            "classifier" => new ClassifierModel(id, name, api, provider, baseUrl, input, cost,
                json.TryGetProperty("contextWindow", out var window) && window.ValueKind == JsonValueKind.Number ? window.GetDouble() : 0),
            "image" => new ImageModel(id, name, api, provider, baseUrl, input, cost, Modalities(json, "output")),
            "chat" => new ChatModel(id, name, api, provider, baseUrl, input, cost),
            _ => throw new FormatException("Unsupported model type.")
        };
        return model with { Headers = headers.ToImmutable(), Metadata = value };
    }

    /// <summary>One released catalog entry; the caller-owned catalog stays the source of truth.</summary>
    public static OperationModel FromCatalog(FrozenCatalogModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return FromJson(model.Raw);
    }
}

/// <summary>types.ts <c>ClassifierModel</c>: usable with classification only.</summary>
public sealed record ClassifierModel(string Id, string Name, string Api, string Provider, string BaseUrl,
    ImmutableArray<string> Input, ModelCost Cost, double ContextWindow) : OperationModel(Id, Name, Api, Provider, BaseUrl, Input, Cost)
{
    public override ModelType Type => ModelType.Classifier;
    private protected override JsonObject Project() { var node = base.Project(); node["contextWindow"] = ContextWindow; return node; }
}

/// <summary>types.ts <c>ImageModel</c>: usable with image generation only. <see cref="Output"/> always includes
/// <c>"image"</c>; <c>"text"</c> means the model can also return text blocks.</summary>
public sealed record ImageModel(string Id, string Name, string Api, string Provider, string BaseUrl,
    ImmutableArray<string> Input, ModelCost Cost, ImmutableArray<string> Output) : OperationModel(Id, Name, Api, Provider, BaseUrl, Input, Cost)
{
    public override ModelType Type => ModelType.Image;
    private protected override JsonObject Project()
    {
        var node = base.Project();
        node["output"] = new JsonArray([.. (Output.IsDefault ? [] : Output).Select(item => (JsonNode)item)]);
        return node;
    }
}

/// <summary>A chat catalog entry, listed by the registry's typed reads. Chat requests use the chat providers.</summary>
public sealed record ChatModel(string Id, string Name, string Api, string Provider, string BaseUrl,
    ImmutableArray<string> Input, ModelCost Cost) : OperationModel(Id, Name, Api, Provider, BaseUrl, Input, Cost)
{
    public override ModelType Type => ModelType.Chat;
}
