// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/types.ts (Model, ImageModel, ClassifierModel shapes) and
// packages/ai/src/utils/model-operations.ts (getModelType) / models.ts (modelsAreEqual).
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;

namespace PiSharp.Cli.Models;

/// <summary>JSON helpers with JavaScript object semantics: a duplicate key keeps its last value (JSON.parse).</summary>
internal static class JsonTree
{
    internal static JsonNode? From(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => Object(element),
        JsonValueKind.Array => new JsonArray([.. element.EnumerateArray().Select(From)]),
        JsonValueKind.Null => null,
        _ => JsonValue.Create(element.Clone())
    };
    private static JsonObject Object(JsonElement element)
    {
        var result = new JsonObject();
        foreach (var property in element.EnumerateObject()) result[property.Name] = From(property.Value);
        return result;
    }
    internal static JsonNode? Parse(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        return From(document.RootElement);
    }
    internal static JsonObject CloneObject(JsonObject value) => (JsonObject)value.DeepClone();
    internal static string? String(JsonObject value, string name) =>
        value.TryGetPropertyValue(name, out var node) && node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
    internal static double? Number(JsonObject value, string name) =>
        value.TryGetPropertyValue(name, out var node) && node is JsonValue scalar && scalar.GetValueKind() == JsonValueKind.Number ? Double(scalar) : null;
    /// <summary>A JSON number as a double, whether it was parsed or built from a CLR integer.</summary>
    internal static double Double(JsonValue scalar) =>
        scalar.TryGetValue<double>(out var number) ? number : scalar.TryGetValue<int>(out var integer) ? integer :
        scalar.TryGetValue<long>(out var wide) ? wide : double.Parse(scalar.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
    internal static bool? Boolean(JsonObject value, string name) =>
        value.TryGetPropertyValue(name, out var node) && node is JsonValue scalar && scalar.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? scalar.GetValue<bool>() : null;
    internal static JsonObject? Object(JsonObject value, string name) =>
        value.TryGetPropertyValue(name, out var node) ? node as JsonObject : null;
    /// <summary>Object spread <c>{ ...base, ...override }</c>; null inputs act as empty; values are cloned.</summary>
    internal static JsonObject Spread(JsonObject? first, JsonObject? second)
    {
        var result = new JsonObject();
        foreach (var source in new[] { first, second })
            if (source is not null) foreach (var (key, value) in source) result[key] = value?.DeepClone();
        return result;
    }
}

internal static class JsonTreeMaps
{
    /// <summary>Object spread of string maps; both null is null.</summary>
    internal static IReadOnlyDictionary<string, string>? Spread(IReadOnlyDictionary<string, string>? first, IReadOnlyDictionary<string, string>? second)
    {
        if (first is null && second is null) return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in new[] { first, second }) if (source is not null) foreach (var (key, value) in source) result[key] = value;
        return result;
    }
}

/// <summary>One catalog entry of any model type: an owned JSON object plus its pinned shard row when it is an unmodified built-in.</summary>
internal sealed class RegistryModel
{
    private readonly JsonObject json;
    internal CatalogModelType Type { get; }
    internal string Id { get; }
    internal string Provider { get; }
    internal string Api { get; }
    internal string? Name { get; }
    internal string BaseUrl { get; }
    internal bool Reasoning { get; }
    internal ImmutableArray<string> Input { get; }
    internal double ContextWindow { get; }
    internal double MaxTokens { get; }
    /// <summary>The verified shard row this entry is, byte-for-byte, when no layer changed it.</summary>
    internal FrozenCatalogModel? Pinned { get; }

    private RegistryModel(JsonObject json, FrozenCatalogModel? pinned)
    {
        this.json = json; Pinned = pinned;
        Type = JsonTree.String(json, "type") switch
        {
            null or "chat" => CatalogModelType.Chat, "image" => CatalogModelType.Image, "classifier" => CatalogModelType.Classifier,
            _ => throw new ArgumentException("Unsupported model type.", nameof(json))
        };
        Id = JsonTree.String(json, "id") ?? throw new ArgumentException("Model id missing.", nameof(json));
        Provider = JsonTree.String(json, "provider") ?? throw new ArgumentException("Model provider missing.", nameof(json));
        Api = JsonTree.String(json, "api") ?? "";
        Name = JsonTree.String(json, "name");
        BaseUrl = JsonTree.String(json, "baseUrl") ?? "";
        Reasoning = JsonTree.Boolean(json, "reasoning") ?? false;
        Input = json["input"] is JsonArray input ? [.. input.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : "")] : [];
        ContextWindow = JsonTree.Number(json, "contextWindow") ?? 0;
        MaxTokens = JsonTree.Number(json, "maxTokens") ?? 0;
    }

    internal static RegistryModel FromCatalog(FrozenCatalogModel row) => new((JsonObject)JsonTree.From(row.Raw.Value)!, row);
    /// <summary>An entry from caller JSON (models.json, extensions, remote catalogs); the object is cloned.</summary>
    internal static RegistryModel FromJson(JsonObject json) => new(JsonTree.CloneObject(json), null);
    /// <summary>A changed copy; the edit runs on a clone and the result is no longer pinned.</summary>
    internal RegistryModel With(Action<JsonObject> edit)
    {
        var copy = JsonTree.CloneObject(json); edit(copy); return new(copy, null);
    }
    internal JsonObject CloneJson() => JsonTree.CloneObject(json);
    internal JsonObject? Compat => JsonTree.Object(json, "compat");
    internal JsonObject? Headers => JsonTree.Object(json, "headers");
    internal string? GetString(string name) => JsonTree.String(json, name);
    internal bool DeclaresImageInput => Input.Contains("image", StringComparer.Ordinal);
    internal string Reference => Provider + "/" + Id;
    internal string ToJsonString() => json.ToJsonString();

    /// <summary>Source modelsAreEqual: same type, id and provider.</summary>
    internal bool SameIdentity(RegistryModel other) => Type == other.Type && Id == other.Id && Provider == other.Provider;

    /// <summary>The validated catalog row for the native transports: the pinned shard row, or this entry read as a one-model shard.</summary>
    internal FrozenCatalogModel ToDefinition()
    {
        if (Pinned is not null) return Pinned;
        var typeName = Type switch { CatalogModelType.Image => "image", CatalogModelType.Classifier => "classifier", _ => "chat" };
        var row = CloneJson();
        row["type"] = typeName;
        var shard = new JsonObject { [Api] = new JsonObject { [typeName + ":" + Id] = row } };
        var catalog = FrozenModelCatalog.ReadProviderJson(Provider, Encoding.UTF8.GetBytes(shard.ToJsonString()));
        return catalog.TryGetModel(Type, Id, out var model) ? model : throw new InvalidOperationException("Synthetic catalog row missing.");
    }

    public override string ToString() => Reference;
}
