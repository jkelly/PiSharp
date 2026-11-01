using System.Text.Json.Nodes;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;

// The provider layer hook (W2 brief, WU2): the built-in llama.cpp provider stays in PiSharp.Cli behind IRegistryProviderLayer and
// ProviderAuthCatalog.BuiltinExtensions. Without the hook there is no llama.cpp anywhere; a fake layer shows what a hook composes.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> LayerCases() =>
    [
        Case("layers.llama-absent-without-the-hook", LayerLlamaAbsent),
        Case("layers.fake-layer-composes-refreshes-and-authenticates", LayerFake),
        Case("layers.models-json-takes-auth-from-the-layer", LayerModelsJson),
    ];

    /// <summary>A provider layer with one chat model per refresh state, a server-URL auth check and a scripted refresh.</summary>
    private sealed class FakeLayer(string providerId) : IRegistryProviderLayer
    {
        internal bool IsLoaded = true;
        internal readonly List<RegistryLayerRefreshContext> Refreshes = [];
        private IReadOnlyList<RegistryModel> models = [Model(providerId, "restored-0")];

        public string ProviderId => providerId;
        public bool Loaded => IsLoaded;
        public IReadOnlyList<RegistryModel> Models => models;

        public async Task RefreshAsync(RegistryLayerRefreshContext context)
        {
            Refreshes.Add(context);
            if (context.Stored is { } stored)
                await context.Publish(new(Update: () => models = [.. stored.Models.OfType<JsonObject>().Select(model => RegistryModel.FromJson((JsonObject)model.DeepClone()))]));
            if (!context.AllowNetwork) return;
            var refreshed = Model(providerId, "refreshed-1");
            await context.Publish(new(new ModelsStoreEntry(new JsonArray(refreshed.CloneJson()), CheckedAt: 1), Update: () => models = [refreshed]));
        }

        public string? CheckAuth(ProviderStoredCredential? credential, Func<string, string?> environment) =>
            credential is { Type: "api_key" } ? "stored credential" : environment("FAKE_LAYER_URL") is not null ? "FAKE_LAYER_URL" : null;

        public ModelRequestAuth? ResolveRequestAuth(ProviderStoredCredential? credential, Func<string, string?> environment, out string? error)
        {
            error = null;
            if (environment("FAKE_LAYER_URL") is not { } url) { error = $"No API key found for \"{providerId}\""; return null; }
            return new(credential?.Key ?? "local", null, new Dictionary<string, string>(StringComparer.Ordinal) { ["FAKE_LAYER_URL"] = url }, "FAKE_LAYER_URL");
        }

        internal static RegistryModel Model(string provider, string id) => RegistryModel.FromJson(new JsonObject
        {
            ["id"] = id, ["name"] = id, ["api"] = "openai-completions", ["provider"] = provider, ["baseUrl"] = "http://127.0.0.1:9/v1", ["reasoning"] = false,
            ["input"] = new JsonArray("text"), ["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 },
            ["contextWindow"] = 4096, ["maxTokens"] = 1024
        });
    }

    private static async Task LayerLlamaAbsent()
    {
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "PiSharp.Cli"), "PiSharp.Cli is not loaded");
        var environment = Env(("LLAMA_BASE_URL", "http://127.0.0.1:9/v1"), ("LLAMA_API_KEY", "llama-key"));
        var refusing = new RefusingHandler();
        var registry = ModelRegistry.Create(new() { Environment = environment, CreateCatalogClient = () => new HttpClient(refusing) });
        Check(!registry.GetProviderIds().Contains("llama.cpp"), "no llama.cpp provider");
        Equal(null, registry.Layer("llama.cpp"), "no llama.cpp layer");
        Equal(null, registry.CheckAuth("llama.cpp"), "llama.cpp is not configured even with LLAMA_BASE_URL");
        Check(registry.GetAllOfType(CatalogModelType.Classifier).All(model => model.Provider != "llama.cpp"), "no llama.cpp classifier");
        // The runtime's registries (a models store included) compose no layer either.
        var root = Path.Combine(Path.GetTempPath(), "pisharp-modelruntime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtime = new LiveRouteRuntime(environment, () => refusing, ModelsPath: Path.Combine(root, "models.json"))
            { CreateCatalogClient = () => new HttpClient(refusing) };
            var composed = await runtime.CreateModelRegistryAsync(CancellationToken.None);
            Check(!composed.GetProviderIds().Contains("llama.cpp") && composed.Options.ProviderLayers.Count == 0, "the runtime adds no layer");
            Check(composed.Options.ModelsStore is FileModelsStore { Path: var store } && store.StartsWith(root, StringComparison.Ordinal), "models-store.json next to models.json");
        }
        finally { Directory.Delete(root, true); }
        // ProviderAuthCatalog: the CLI's module initializer never ran, so no built-in extension lists llama.cpp.
        Equal(null, ProviderAuthCatalog.BuiltinExtensions, "no built-in extension hook");
        Equal(null, ProviderAuthCatalog.Find("llama.cpp"), "no llama.cpp auth entry");
        Check(!ProviderAuthCatalog.LoginOptions().Any(option => option.Provider.Id == "llama.cpp"), "no llama.cpp login option");
        Equal(0, refusing.Requests, "no request");
    }

    private static async Task LayerFake()
    {
        var layer = new FakeLayer("fake-local");
        var store = new InMemoryModelsStore();
        var restored = FakeLayer.Model("fake-local", "restored-1");
        await store.WriteAsync("fake-local", new ModelsStoreEntry(new JsonArray(restored.CloneJson()), CheckedAt: 1), CancellationToken.None);
        var refusing = new RefusingHandler();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var registry = await ModelRegistry.CreateAsync(new()
        {
            Environment = name => environment.GetValueOrDefault(name), ModelsStore = store, ProviderLayers = [layer],
            CreateCatalogClient = () => new HttpClient(refusing), FileExists = _ => false, Home = Path.GetTempPath()
        }, CancellationToken.None);
        // Composed after the pi-ai built-ins; the startup refresh restored the stored catalog without network.
        var ids = registry.GetProviderIds();
        Equal(BuiltinProviders.All.Length, ids.ToList().IndexOf("fake-local"), "after the built-ins: " + string.Join(",", ids));
        Check(layer.Refreshes is [{ AllowNetwork: false, Stored: not null }], "one offline startup refresh with the stored catalog");
        Check(registry.Find("fake-local", "restored-1") is not null, "the restored model");
        Equal(layer, registry.Layer("fake-local"), "the registered layer");
        // Auth is the layer's while models.json and extensions leave the provider alone.
        Equal(null, registry.CheckAuth("fake-local"), "dormant without a server URL");
        Check(registry.ResolveRequestAuth(registry.Find("fake-local", "restored-1")!, out var dormant) is null && dormant == "No API key found for \"fake-local\"", "no request auth: " + dormant);
        environment["FAKE_LAYER_URL"] = "http://127.0.0.1:9/v1";
        await registry.RefreshAsync(allowNetwork: false, force: null, providers: ["fake-local"], CancellationToken.None);
        Equal("FAKE_LAYER_URL", registry.CheckAuth("fake-local"), "configured");
        var auth = registry.ResolveRequestAuth(registry.Find("fake-local", "restored-1")!, out var error);
        Check(auth is { ApiKey: "local", Source: "FAKE_LAYER_URL" } && auth.Environment!["FAKE_LAYER_URL"] == "http://127.0.0.1:9/v1" && error is null, "layer request auth: " + error);
        // A live refresh of only this provider publishes into the models store.
        var errors = await registry.RefreshAsync(allowNetwork: true, force: null, providers: ["fake-local"], CancellationToken.None);
        Equal(0, errors.Count, "refresh errors");
        Check(registry.Find("fake-local", "refreshed-1") is not null && registry.Find("fake-local", "restored-1") is null, "the refreshed catalog replaced the restored one");
        Check((await store.ReadAsync("fake-local", CancellationToken.None))?.Models.Count == 1, "persisted");
        // An unloaded layer registers no provider.
        layer.IsLoaded = false;
        await registry.RefreshAsync(allowNetwork: false, force: null, providers: ["fake-local"], CancellationToken.None);
        Check(!registry.GetProviderIds().Contains("fake-local") && registry.CheckAuth("fake-local") is null, "unloaded");
        Equal(0, refusing.Requests, "no catalog request");
    }

    private static async Task LayerModelsJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-modelruntime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var models = Path.Combine(root, "models.json");
            await File.WriteAllTextAsync(models, """{"providers":{"fake-local":{"baseUrl":"http://127.0.0.1:9/v1","apiKey":"configured-key","api":"openai-completions"}}}""");
            var layer = new FakeLayer("fake-local");
            var registry = ModelRegistry.Create(new()
            {
                ModelsPath = models, Environment = Env(("FAKE_LAYER_URL", "http://127.0.0.1:9/v1")), ProviderLayers = [layer],
                CreateCatalogClient = () => new HttpClient(new RefusingHandler()), FileExists = _ => false, Home = root
            });
            Equal(null, registry.GetError(), "models.json loads");
            Equal("configured API key", registry.CheckAuth("fake-local"), "models.json owns the provider's auth");
            Equal("configured-key", registry.ResolveRequestAuth(registry.Find("fake-local", "restored-0")!, out _)?.ApiKey, "models.json key");
        }
        finally { Directory.Delete(root, true); }
    }
}
