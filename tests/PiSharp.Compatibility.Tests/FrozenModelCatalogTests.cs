using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Catalogs;

static class FrozenModelCatalogTests
{
    // Authored small examples of the released API-group/type:id/model shape.
    private const string One = """
        {"wire":{"chat:m":{"type":"chat","id":"m","name":"Small π","api":"wire","provider":"fixture","baseUrl":"","input":["text"],"reasoning":false,"contextWindow":64,"maxTokens":32,"cost":{"input":1.5,"output":2,"cacheRead":0,"cacheWrite":0}}}}
        """;
    private const string Mixed = """
        {"future-chat-api":{"chat:same":{"type":"chat","id":"same","name":"Chat","api":"future-chat-api","provider":"fixture","baseUrl":"opaque endpoint","input":["image","text"],"reasoning":true,"contextWindow":64,"maxTokens":32,"cost":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0}}},"image-api":{"image:same":{"type":"image","id":"same","name":"Image","api":"image-api","provider":"fixture","baseUrl":"","input":["text"],"output":["text","image"],"cost":{"input":0,"output":1,"cacheRead":0,"cacheWrite":0}}},"classifier-api":{"classifier:same":{"type":"classifier","id":"same","name":"Classifier","api":"classifier-api","provider":"fixture","baseUrl":"","input":["text"],"contextWindow":32,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}}}
        """;

    public static void Register(List<(string Name, Func<Task> Run)> tests, string repo)
    {
        tests.Add(("native.catalog.typed-ordinal-lookup-and-declarations", Lookup));
        tests.Add(("native.catalog.image-input-declaration-and-operation-identity", ImageInput));
        tests.Add(("native.catalog.exact-numbers-and-opaque-fields", RawFields));
        tests.Add(("native.catalog.identity-and-recursive-duplicate-rejection", Identities));
        tests.Add(("native.catalog.released-shape-and-numeric-admission", Shape));
        tests.Add(("native.catalog.configured-boundaries", Limits));
        tests.Add(("native.catalog.owned-immutable-values", Ownership));
        tests.Add(("native.catalog.sanitized-errors-and-unicode-policy", Diagnostics));
        tests.Add(("artifact.catalog.released-npm-42-provider-shards", () => ReleasedPackage(repo)));
    }

    private static FrozenModelCatalog Read(string json, FrozenModelCatalogReadOptions? options = null) =>
        FrozenModelCatalog.ReadProviderJson("fixture", Encoding.UTF8.GetBytes(json), options);

    private static Task Lookup()
    {
        var catalog = Read(Mixed);
        Equal("fixture", catalog.Provider); Equal(3, catalog.Models.Length);
        Equal("future-chat-api", catalog.DeclaredApis[0]); Equal("image-api", catalog.DeclaredApis[1]);
        Equal("classifier-api", catalog.DeclaredApis[2]);
        foreach (var type in new[] { CatalogModelType.Chat, CatalogModelType.Image, CatalogModelType.Classifier })
        {
            if (!catalog.TryGetModel(type, "same", out var model)) throw new Exception("Typed model identity was lost.");
            Equal(type, model.Type); Equal("same", model.Id); Equal("fixture", model.Provider);
        }
        if (!catalog.TryGetModel(CatalogModelType.Chat, "same", out var chat)) throw new Exception("Chat missing.");
        Equal("future-chat-api", chat.DeclaredApi); Equal("opaque endpoint", chat.BaseUrl);
        Assert(!catalog.TryGetModel(CatalogModelType.Chat, "SAME", out _), "Lookup changed ordinal identity.");
        Assert(!catalog.TryGetModel(CatalogModelType.Chat, "absent", out _), "Missing model acquired a default.");
        Equal(CatalogModelType.Chat, catalog.Models[0].Type); Equal(CatalogModelType.Image, catalog.Models[1].Type);
        return Task.CompletedTask;
    }

    private static Task RawFields()
    {
        const string precise = "0.123456789012345678901";
        var raw = One.Replace("\"input\":1.5", $"\"input\":{precise}", StringComparison.Ordinal)
            .Replace("\"cost\":", "\"thinkingLevelMap\":{\"off\":null,\"high\":\"opaque\"},\"compat\":{\"future\":true},\"opaque\":{\"ordered\":[\"z\",\"a\"],\"nil\":null,\"huge\":9007199254740993,\"futureNumber\":1e400},\"cost\":", StringComparison.Ordinal);
        var model = Read(raw).Models[0];
        Equal(precise, model.Cost.Value.GetProperty("input").GetRawText());
        Equal("9007199254740993", model.Raw.Value.GetProperty("opaque").GetProperty("huge").GetRawText());
        Equal("1e400", model.Raw.Value.GetProperty("opaque").GetProperty("futureNumber").GetRawText());
        Equal("z", model.Raw.Value.GetProperty("opaque").GetProperty("ordered")[0].GetString());
        Equal(JsonValueKind.Null, model.Raw.Value.GetProperty("opaque").GetProperty("nil").ValueKind);
        Equal(JsonValueKind.Null, model.Raw.Value.GetProperty("thinkingLevelMap").GetProperty("off").ValueKind);
        Assert(!model.Raw.Value.TryGetProperty("promptCache", out _), "Missing metadata became a value.");
        var next = Read(raw.Replace(precise, "0.123456789012345678902", StringComparison.Ordinal)).Models[0];
        Assert(model.Cost.ToString() != next.Cost.ToString(), "Distinct exact decimal lexemes collapsed.");
        var integral = Read(One.Replace("\"input\":1.5", "\"input\":9007199254740993", StringComparison.Ordinal)).Models[0];
        Equal("9007199254740993", integral.Cost.Value.GetProperty("input").GetRawText());
        var scientific = Read(One.Replace("\"input\":1.5", "\"input\":1.50e+0", StringComparison.Ordinal)).Models[0];
        Equal("1.50e+0", scientific.Cost.Value.GetProperty("input").GetRawText());
        return Task.CompletedTask;
    }

    private static Task ImageInput()
    {
        Assert(!Read(One).Models[0].DeclaresImageInput, "Text-only input acquired image capability.");
        foreach (var input in new[] { "[\"image\"]", "[\"text\",\"image\"]", "[\"image\",\"text\"]", "[\"image\",\"image\"]", "[\"\\u0069mage\"]" })
        {
            var model = Read(One.Replace("\"input\":[\"text\"]", "\"input\":" + input, StringComparison.Ordinal)).Models[0];
            Assert(model.DeclaresImageInput, "Validated image input was lost.");
            Equal(input, model.Raw.Value.GetProperty("input").GetRawText());
        }
        var catalog = Read(Mixed);
        Assert(catalog.TryGetModel(CatalogModelType.Chat, "same", out var chat) && chat.DeclaresImageInput,
            "Chat image input was not selected by operation identity.");
        Assert(catalog.TryGetModel(CatalogModelType.Image, "same", out var image) && !image.DeclaresImageInput,
            "Image output or a same-ID chat entry was treated as image input.");
        Assert(catalog.TryGetModel(CatalogModelType.Classifier, "same", out var classifier) && !classifier.DeclaresImageInput,
            "Classifier input acquired the same-ID chat capability.");
        // A declaration is retained even for an unknown API; no executable transport is inferred.
        Equal("future-chat-api", chat!.DeclaredApi);
        var opaque = Read(One.Replace("\"cost\":", "\"compat\":{\"supportsImages\":true},\"inputLimits\":{\"images\":{}},\"cost\":", StringComparison.Ordinal)).Models[0];
        Assert(!opaque.DeclaresImageInput, "Uninterpreted metadata replaced the validated input field.");
        Reject(CatalogReadFailure.InvalidShape, One.Replace("\"input\":[\"text\"]", "\"input\":[\"Image\"]", StringComparison.Ordinal));
        Reject(CatalogReadFailure.InvalidShape, One.Replace("\"input\":[\"text\"],", "", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static Task Identities()
    {
        Reject(CatalogReadFailure.IdentityMismatch, One.Replace("\"provider\":\"fixture\"", "\"provider\":\"Fixture\"", StringComparison.Ordinal));
        Reject(CatalogReadFailure.IdentityMismatch, One.Replace("\"api\":\"wire\"", "\"api\":\"other\"", StringComparison.Ordinal));
        Reject(CatalogReadFailure.IdentityMismatch, One.Replace("\"chat:m\"", "\"chat:other\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(One);
        var first = document.RootElement.GetProperty("wire").GetRawText();
        var second = first.Replace("\"api\":\"wire\"", "\"api\":\"other\"", StringComparison.Ordinal);
        Reject(CatalogReadFailure.DuplicateIdentity, $"{{\"wire\":{first},\"other\":{second}}}");
        Reject(CatalogReadFailure.DuplicateProperty, "{\"wire\":{},\"\\u0077ire\":{}}");
        Reject(CatalogReadFailure.DuplicateProperty, One.Replace("\"cost\":", "\"opaque\":{\"a\":1,\"\\u0061\":2},\"cost\":", StringComparison.Ordinal));
        Reject(CatalogReadFailure.DuplicateProperty, One.Replace("\"input\":1.5", "\"input\":1.5,\"input\":1.5", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static Task Shape()
    {
        foreach (var json in new[] { "{}", "[]", "null", "{\"wire\":null}", "{\"wire\":{}}" }) Reject(CatalogReadFailure.InvalidShape, json);
        Reject(CatalogReadFailure.InvalidShape, One.Replace("\"type\":\"chat\",", "", StringComparison.Ordinal));
        Reject(CatalogReadFailure.UnsupportedModelType, One.Replace("\"chat\"", "\"future-type\"", StringComparison.Ordinal));
        Reject(CatalogReadFailure.InvalidShape, One.Replace("\"reasoning\":false", "\"reasoning\":null", StringComparison.Ordinal));
        Reject(CatalogReadFailure.InvalidShape, One.Replace("\"contextWindow\":64", "\"contextWindow\":0", StringComparison.Ordinal));
        Reject(CatalogReadFailure.InvalidShape, One.Replace("\"maxTokens\":32", "\"maxTokens\":-1", StringComparison.Ordinal));
        Reject(CatalogReadFailure.UnsupportedNumber, One.Replace("\"input\":1.5", "\"input\":1e400", StringComparison.Ordinal));
        Reject(CatalogReadFailure.InvalidShape, One.Replace("\"input\":1.5", "\"input\":\"1.5\"", StringComparison.Ordinal));
        Reject(CatalogReadFailure.InvalidShape, One.Replace("\"input\":[\"text\"]", "\"input\":[]", StringComparison.Ordinal));
        Reject(CatalogReadFailure.InvalidShape, One.Replace("\"input\":[\"text\"]", "\"input\":[\"audio\"]", StringComparison.Ordinal));
        Reject(CatalogReadFailure.InvalidShape, One.Replace("\"cost\":", "\"output\":null,\"cost\":", StringComparison.Ordinal));
        Reject(CatalogReadFailure.InvalidShape, Mixed.Replace("\"output\":[\"text\",\"image\"]", "\"output\":[\"text\"]", StringComparison.Ordinal));
        // Released finite-cost validation does not itself impose nonnegative rates.
        Equal("-1", Read(One.Replace("\"input\":1.5", "\"input\":-1", StringComparison.Ordinal)).Models[0].Cost.Value.GetProperty("input").GetRawText());
        return Task.CompletedTask;
    }

    private static Task Limits()
    {
        var length = Encoding.UTF8.GetByteCount(One); Assert(length > One.Length, "UTF-8 example lacks multibyte text.");
        Equal(1, Read(One, new(MaximumUtf8Bytes: length)).Models.Length);
        Reject(CatalogReadFailure.ResourceLimit, One, new(MaximumUtf8Bytes: length - 1));
        Equal(1, Read(One, new(MaximumDepth: 4)).Models.Length);
        Reject(CatalogReadFailure.ResourceLimit, One, new(MaximumDepth: 3));
        Equal(3, Read(Mixed, new(MaximumModels: 3)).Models.Length);
        Reject(CatalogReadFailure.ResourceLimit, Mixed, new(MaximumModels: 2));
        Equal(1, Read(One, new(MaximumProperties: 17)).Models.Length);
        Reject(CatalogReadFailure.ResourceLimit, One, new(MaximumProperties: 16));
        Equal(1, Read(One, new(MaximumStringCharacters: 13)).Models.Length);
        Reject(CatalogReadFailure.ResourceLimit, One, new(MaximumStringCharacters: 12));
        Equal(1, Read(One, new(MaximumNumberCharacters: 3)).Models.Length);
        Reject(CatalogReadFailure.ResourceLimit, One, new(MaximumNumberCharacters: 2));
        try { Read(One, new(MaximumDepth: 65)); throw new Exception("Invalid options accepted."); }
        catch (ArgumentOutOfRangeException) { }
        return Task.CompletedTask;
    }

    private static Task Ownership()
    {
        var bytes = Encoding.UTF8.GetBytes(Mixed);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var catalog = FrozenModelCatalog.ReadProviderJson("fixture", bytes);
        bytes.AsSpan().Fill((byte)'x');
        Equal(expectedHash, catalog.ProviderJsonSha256); Equal("same", catalog.Models[0].Raw.Value.GetProperty("id").GetString());
        Assert(catalog.Models[0].DeclaresImageInput && !catalog.Models[1].DeclaresImageInput,
            "Caller buffer mutation changed the selected input declaration.");
        var copy = catalog.Models.ToBuilder(); copy.Clear(); Equal(3, catalog.Models.Length);
        using var expected = JsonDocument.Parse(Mixed);
        Equal(expected.RootElement.GetRawText(), catalog.Raw.ToString());
        Equal("image", catalog.Models[1].Raw.Value.GetProperty("type").GetString());
        return Task.CompletedTask;
    }

    private static Task Diagnostics()
    {
        foreach (var raw in new[] { "", "{", One + "false", One.Replace("\"input\":1.5", "\"input\":NaN", StringComparison.Ordinal) })
            Reject(CatalogReadFailure.InvalidJson, raw);
        var privateRaw = One.Replace("\"cost\":", "\"private-payload-marker\":{\"private-key\":1,\"private-key\":2},\"cost\":", StringComparison.Ordinal);
        var error = Reject(CatalogReadFailure.DuplicateProperty, privateRaw);
        Assert(!error.Message.Contains("private", StringComparison.Ordinal) && error.InnerException is null, "Catalog diagnostic leaked rejected content.");
        Reject(CatalogReadFailure.UnsupportedUnicode, One.Replace("Small π", "\\ud800", StringComparison.Ordinal));
        Reject(CatalogReadFailure.UnsupportedUnicode, One.Replace("\"cost\":", "\"opaque\":\"\\ud800\",\"cost\":", StringComparison.Ordinal));
        var paired = Read(One.Replace("Small π", "\\ud83d\\ude00", StringComparison.Ordinal));
        Equal("\U0001F600", paired.Models[0].Name);
        return Task.CompletedTask;
    }

    private static Task ReleasedPackage(string repo)
    {
        const string prefix = "package/dist/providers/data/";
        var package = File.ReadAllBytes(Path.Combine(repo, "artifacts", "released-npm-ai", "pi-ai-0.99.1.tgz"));
        Equal("f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3", Convert.ToHexStringLower(SHA256.HashData(package)));
        var shards = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var compressed = new MemoryStream(package, writable: false))
        using (var gzip = new GZipStream(compressed, CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            {
                if (!entry.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                Assert(entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile, "Pinned catalog member is not a regular file.");
                var name = entry.Name[prefix.Length..];
                Assert(!name.Contains('/') && name.EndsWith(".json", StringComparison.Ordinal) && entry.DataStream is not null, "Pinned catalog member shape differs.");
                using var payload = new MemoryStream(); var buffer = new byte[8192]; int read;
                while ((read = entry.DataStream!.Read(buffer)) != 0)
                {
                    Assert(payload.Length + read <= 2_097_152, "Pinned provider payload exceeds declared reader budget.");
                    payload.Write(buffer, 0, read);
                }
                Assert(shards.TryAdd(name, payload.ToArray()), "Pinned catalog member duplicated.");
            }
        }
        Equal(43, shards.Count);
        var manifestBytes = shards[".manifest.json"];
        Equal("9fbcd337d4bd414a407d7c9f8041ddba2f4d28840eca923013fd8b0636b88b23", Convert.ToHexStringLower(SHA256.HashData(manifestBytes)));
        using var manifest = JsonDocument.Parse(manifestBytes);
        Equal("2026-09-29T18:10:40.924Z", manifest.RootElement.GetProperty("generatedAt").GetString());
        var models = 0; var providers = 0; var apis = new HashSet<string>(StringComparer.Ordinal);
        var typeCounts = new Dictionary<CatalogModelType, int>();
        var imageInputCounts = new Dictionary<CatalogModelType, int>();
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            var bytes = shards[file.Name];
            Equal(file.Value.GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes))); // Hash before native reader admission.
            var provider = file.Name[..^5];
            var catalog = FrozenModelCatalog.ReadProviderJson(provider, bytes);
            Equal(file.Value.GetString(), catalog.ProviderJsonSha256);
            using var original = JsonDocument.Parse(bytes);
            Equal(original.RootElement.GetRawText(), catalog.Raw.ToString());
            var ordinal = 0;
            foreach (var group in original.RootElement.EnumerateObject())
            {
                apis.Add(group.Name);
                foreach (var item in group.Value.EnumerateObject())
                {
                    var raw = item.Value; var id = raw.GetProperty("id").GetString()!;
                    var type = raw.GetProperty("type").GetString() switch
                    {
                        "chat" => CatalogModelType.Chat, "image" => CatalogModelType.Image,
                        "classifier" => CatalogModelType.Classifier, _ => throw new Exception("Pinned type changed.")
                    };
                    if (!catalog.TryGetModel(type, id, out var model)) throw new Exception("Released model lookup failed.");
                    Equal(group.Name, model.DeclaredApi); Equal(raw.GetRawText(), model.Raw.ToString());
                    Equal(raw.GetProperty("cost").GetRawText(), model.Cost.ToString());
                    Equal(raw.GetProperty("input").EnumerateArray().Any(modality => modality.GetString() == "image"), model.DeclaresImageInput);
                    Assert(ReferenceEquals(model, catalog.Models[ordinal++]), "Released model enumeration order changed.");
                    typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;
                    if (model.DeclaresImageInput) imageInputCounts[type] = imageInputCounts.GetValueOrDefault(type) + 1;
                }
            }
            Equal(ordinal, catalog.Models.Length); models += ordinal; providers++;
        }
        Equal(42, providers); Equal(1592, models); Equal(13, apis.Count);
        Equal(1523, typeCounts[CatalogModelType.Chat]); Equal(57, typeCounts[CatalogModelType.Image]);
        Equal(12, typeCounts[CatalogModelType.Classifier]);
        Equal(1054, imageInputCounts[CatalogModelType.Chat]); Equal(55, imageInputCounts[CatalogModelType.Image]);
        Equal(0, imageInputCounts.GetValueOrDefault(CatalogModelType.Classifier));
        return Task.CompletedTask;
    }

    private static CatalogReadException Reject(CatalogReadFailure expected, string json, FrozenModelCatalogReadOptions? options = null)
    {
        try { Read(json, options); }
        catch (CatalogReadException exception) { Equal(expected, exception.Failure); return exception; }
        throw new Exception("Expected catalog rejection did not occur.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
}
