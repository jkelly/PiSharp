using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Contracts;

namespace PiSharp.AI.Catalogs;

public enum CatalogModelType { Chat, Image, Classifier }

public sealed record FrozenModelCatalogReadOptions(
    int MaximumUtf8Bytes = 2_097_152, int MaximumDepth = 32, int MaximumModels = 4096,
    int MaximumProperties = 65_536, int MaximumStringCharacters = 65_536, int MaximumNumberCharacters = 256);

public enum CatalogReadFailure
{
    InvalidJson, InvalidShape, DuplicateProperty, IdentityMismatch, DuplicateIdentity,
    UnsupportedModelType, UnsupportedNumber, UnsupportedUnicode, ResourceLimit
}

public sealed class CatalogReadException : Exception
{
    public CatalogReadFailure Failure { get; }
    internal CatalogReadException(CatalogReadFailure failure) : base(failure switch
    {
        CatalogReadFailure.DuplicateProperty => "Catalog JSON contains duplicate object properties.",
        CatalogReadFailure.IdentityMismatch => "Catalog model identity does not match its provider, group, or key.",
        CatalogReadFailure.DuplicateIdentity => "Catalog contains duplicate model identities.",
        CatalogReadFailure.UnsupportedModelType => "Catalog contains an unsupported model type.",
        CatalogReadFailure.UnsupportedNumber => "Catalog required number exceeds the released finite-number profile.",
        CatalogReadFailure.UnsupportedUnicode => "Catalog contains unsupported string encoding.",
        CatalogReadFailure.ResourceLimit => "Catalog exceeds configured resource limits.",
        CatalogReadFailure.InvalidShape => "Catalog does not match the released provider JSON shape.",
        _ => "Catalog is not valid complete JSON."
    }) => Failure = failure;
}

/// <summary>Owned catalog metadata. DeclaredApi and raw capabilities do not establish transport support.</summary>
public sealed class FrozenCatalogModel
{
    public CatalogModelType Type { get; }
    public string Id { get; }
    public string Provider { get; }
    public string DeclaredApi { get; }
    public string Name { get; }
    public string BaseUrl { get; }
    public JsonData Raw { get; }
    public JsonData Cost { get; }
    /// <summary>True exactly when validated input metadata contains "image". This does not establish transport support.</summary>
    public bool DeclaresImageInput { get; }

    internal FrozenCatalogModel(CatalogModelType type, string id, string provider, string api,
        string name, string baseUrl, JsonElement raw)
    {
        Type = type; Id = id; Provider = provider; DeclaredApi = api; Name = name; BaseUrl = baseUrl;
        Raw = JsonData.FromElement(raw); Cost = JsonData.FromElement(raw.GetProperty("cost"));
        DeclaresImageInput = raw.GetProperty("input").EnumerateArray().Any(modality => modality.GetString() == "image");
    }
}

/// <summary>Pure, immutable reader for a single caller-supplied released provider JSON shard.</summary>
public sealed class FrozenModelCatalog
{
    private readonly ImmutableDictionary<(CatalogModelType Type, string Id), FrozenCatalogModel> _lookup;
    public string Provider { get; }
    public string ProviderJsonSha256 { get; }
    public JsonData Raw { get; }
    public ImmutableArray<string> DeclaredApis { get; }
    public ImmutableArray<FrozenCatalogModel> Models { get; }

    private FrozenModelCatalog(string provider, string sha256, JsonData raw, ImmutableArray<string> apis,
        ImmutableArray<FrozenCatalogModel> models,
        ImmutableDictionary<(CatalogModelType Type, string Id), FrozenCatalogModel> lookup)
    {
        Provider = provider; ProviderJsonSha256 = sha256; Raw = raw; DeclaredApis = apis; Models = models; _lookup = lookup;
    }

    public bool TryGetModel(CatalogModelType type, string id, [NotNullWhen(true)] out FrozenCatalogModel? model)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _lookup.TryGetValue((type, id), out model);
    }

    public static FrozenModelCatalog ReadProviderJson(string provider, ReadOnlyMemory<byte> utf8Json,
        FrozenModelCatalogReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        options ??= new();
        if (options.MaximumUtf8Bytes <= 0 || options.MaximumDepth is < 1 or > 64 || options.MaximumModels <= 0 ||
            options.MaximumProperties <= 0 || options.MaximumStringCharacters <= 0 || options.MaximumNumberCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid catalog read limits.");
        if (provider.Length == 0) throw new CatalogReadException(CatalogReadFailure.InvalidShape);
        if (provider.Length > options.MaximumStringCharacters || utf8Json.Length > options.MaximumUtf8Bytes)
            throw new CatalogReadException(CatalogReadFailure.ResourceLimit);
        var bytes = utf8Json.ToArray(); // Sever caller ownership before parsing or retaining any value.
        ValidateTokens(bytes, options);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = options.MaximumDepth });
            var root = document.RootElement;
            RequireObject(root);
            var apis = ImmutableArray.CreateBuilder<string>();
            var models = ImmutableArray.CreateBuilder<FrozenCatalogModel>();
            var lookup = ImmutableDictionary.CreateBuilder<(CatalogModelType Type, string Id), FrozenCatalogModel>();
            foreach (var group in root.EnumerateObject())
            {
                if (group.Name.Length == 0) throw new CatalogReadException(CatalogReadFailure.InvalidShape);
                RequireObject(group.Value); apis.Add(group.Name);
                foreach (var entry in group.Value.EnumerateObject())
                {
                    if (models.Count >= options.MaximumModels) throw new CatalogReadException(CatalogReadFailure.ResourceLimit);
                    var value = entry.Value; RequireObject(value);
                    var typeName = RequiredString(value, "type");
                    var type = typeName switch
                    {
                        "chat" => CatalogModelType.Chat, "image" => CatalogModelType.Image,
                        "classifier" => CatalogModelType.Classifier,
                        _ => throw new CatalogReadException(CatalogReadFailure.UnsupportedModelType)
                    };
                    var id = RequiredString(value, "id");
                    var declaredProvider = RequiredString(value, "provider");
                    var api = RequiredString(value, "api");
                    if (declaredProvider != provider || api != group.Name || entry.Name != $"{typeName}:{id}")
                        throw new CatalogReadException(CatalogReadFailure.IdentityMismatch);
                    var name = RequiredString(value, "name");
                    var baseUrl = RequiredString(value, "baseUrl", allowEmpty: true);
                    RequireModalities(value, "input", requireImage: false);
                    if (type == CatalogModelType.Image) RequireModalities(value, "output", requireImage: true);
                    else if (value.TryGetProperty("output", out _)) throw new CatalogReadException(CatalogReadFailure.InvalidShape);
                    if (type == CatalogModelType.Chat)
                    {
                        if (!value.TryGetProperty("reasoning", out var reasoning) || reasoning.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new CatalogReadException(CatalogReadFailure.InvalidShape);
                        RequireNumber(value, "contextWindow", positive: true);
                        RequireNumber(value, "maxTokens", positive: true);
                    }
                    else if (type == CatalogModelType.Classifier) RequireNumber(value, "contextWindow", positive: true);
                    if (!value.TryGetProperty("cost", out var cost)) throw new CatalogReadException(CatalogReadFailure.InvalidShape);
                    RequireObject(cost);
                    foreach (var field in new[] { "input", "output", "cacheRead", "cacheWrite" }) RequireNumber(cost, field, positive: false);
                    if (lookup.ContainsKey((type, id))) throw new CatalogReadException(CatalogReadFailure.DuplicateIdentity);
                    var model = new FrozenCatalogModel(type, id, provider, api, name, baseUrl, value);
                    lookup.Add((type, id), model); models.Add(model);
                }
            }
            if (models.Count == 0) throw new CatalogReadException(CatalogReadFailure.InvalidShape);
            return new(provider, Convert.ToHexStringLower(SHA256.HashData(bytes)), JsonData.FromElement(root),
                apis.ToImmutable(), models.ToImmutable(), lookup.ToImmutable());
        }
        catch (JsonException) { throw new CatalogReadException(CatalogReadFailure.InvalidJson); }
    }

    private static void ValidateTokens(byte[] bytes, FrozenModelCatalogReadOptions options)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = options.MaximumDepth + 1 });
        var names = new Stack<HashSet<string>?>();
        var properties = 0;
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    if (reader.CurrentDepth >= options.MaximumDepth) throw new CatalogReadException(CatalogReadFailure.ResourceLimit);
                    names.Push(reader.TokenType == JsonTokenType.StartObject ? new(StringComparer.Ordinal) : null);
                }
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) names.Pop();
                else if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.String)
                {
                    string text;
                    try { text = reader.GetString()!; }
                    catch (InvalidOperationException) { throw new CatalogReadException(CatalogReadFailure.UnsupportedUnicode); }
                    if (text.Length > options.MaximumStringCharacters) throw new CatalogReadException(CatalogReadFailure.ResourceLimit);
                    if (reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (++properties > options.MaximumProperties) throw new CatalogReadException(CatalogReadFailure.ResourceLimit);
                        if (!names.Peek()!.Add(text)) throw new CatalogReadException(CatalogReadFailure.DuplicateProperty);
                    }
                }
                else if (reader.TokenType == JsonTokenType.Number && reader.ValueSpan.Length > options.MaximumNumberCharacters)
                    throw new CatalogReadException(CatalogReadFailure.ResourceLimit);
            }
        }
        catch (JsonException) { throw new CatalogReadException(CatalogReadFailure.InvalidJson); }
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new CatalogReadException(CatalogReadFailure.InvalidShape);
    }

    private static string RequiredString(JsonElement value, string field, bool allowEmpty = false)
    {
        if (!value.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.String)
            throw new CatalogReadException(CatalogReadFailure.InvalidShape);
        var text = property.GetString()!;
        if (!allowEmpty && text.Length == 0) throw new CatalogReadException(CatalogReadFailure.InvalidShape);
        return text;
    }

    private static void RequireModalities(JsonElement value, string field, bool requireImage)
    {
        if (!value.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.Array || property.GetArrayLength() == 0)
            throw new CatalogReadException(CatalogReadFailure.InvalidShape);
        var hasImage = false;
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not ("text" or "image"))
                throw new CatalogReadException(CatalogReadFailure.InvalidShape);
            hasImage |= item.GetString() == "image";
        }
        if (requireImage && !hasImage) throw new CatalogReadException(CatalogReadFailure.InvalidShape);
    }

    private static void RequireNumber(JsonElement value, string field, bool positive)
    {
        if (!value.TryGetProperty(field, out var property) || property.ValueKind != JsonValueKind.Number)
            throw new CatalogReadException(CatalogReadFailure.InvalidShape);
        // Admission matches the released schema's finite-number check. Never replace the raw numeric token.
        if (!property.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new CatalogReadException(CatalogReadFailure.UnsupportedNumber);
        if (positive && number <= 0) throw new CatalogReadException(CatalogReadFailure.InvalidShape);
    }
}
