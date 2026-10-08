using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Models;

// Authored from packages/coding-agent/src/core/remote-catalog-provider.ts, core/models-store.ts and
// modes/interactive/model-catalog-refresh.ts, following test/remote-catalog-provider.test.ts, test/models-store.test.ts and
// test/model-catalog-refresh.test.ts (v1.1.0). A fake pi.dev catalog answers in process.
internal static partial class Program
{
    private sealed class CatalogEndpoint(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal readonly List<(string Url, Dictionary<string, string> Headers)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add((request.RequestUri!.ToString(), request.Headers.ToDictionary(pair => pair.Key, pair => pair.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ? request.Headers.UserAgent.ToString() : string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Catalog(string json, string? etag = "\"v1\"", DateTimeOffset? lastModified = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag);
        response.Content.Headers.LastModified = lastModified ?? DateTimeOffset.FromUnixTimeMilliseconds(BuiltinModelCatalog.GeneratedAtUnixMilliseconds + 60_000);
        return response;
    }

    private const string RemoteGroq = """
        {"models":[{"id":"openai/gpt-oss-120b","name":"GPT OSS 120B (remote)","api":"openai-completions","baseUrl":"https://api.groq.com/openai/v1",
          "reasoning":true,"input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":131072,"maxTokens":65536},
          {"id":"groq-new","name":"Groq New","api":"openai-completions","baseUrl":"https://api.groq.com/openai/v1","reasoning":false,"input":["text"],
          "cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":8192,"maxTokens":1024},
          {"id":"future","type":"video"},{"name":"no id"}]}
        """;

    private static IEnumerable<(string, Func<Task>)> RemoteCases() =>
    [
        ("remote.refresh-merges-persists-and-revalidates", RemoteRefresh),
        ("remote.not-found-older-catalogs-and-failures", RemoteFailures),
        ("remote.parse-catalog-shapes", Sync(RemoteParse)),
        ("remote.file-store-round-trip", RemoteFileStore),
        ("remote.refresh-coordinator-shares-runs", RemoteCoordinator),
    ];

    private static ModelRegistry RemoteRegistry(CatalogEndpoint endpoint, IModelsStore store, Func<DateTimeOffset> now) => ModelRegistry.Create(new()
    {
        Environment = Env(("GROQ_API_KEY", "k")), ModelsStore = store, CatalogBaseUrl = "https://catalog.invalid",
        CreateCatalogClient = () => new HttpClient(endpoint, disposeHandler: false), Now = now
    });

    private static async Task RemoteRefresh()
    {
        var phase = 0;
        var endpoint = new CatalogEndpoint(request => phase == 0 ? Catalog(RemoteGroq) :
            request.Headers.TryGetValues("if-none-match", out var tags) && tags.Single() == "\"v1\"" ? new HttpResponseMessage(HttpStatusCode.NotModified) :
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var store = new InMemoryModelsStore(); var clock = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        var registry = RemoteRegistry(endpoint, store, () => clock);
        var errors = await registry.RefreshAsync(true, null, ["groq"], CancellationToken.None);
        Equal(0, errors.Count, "no errors");
        var request = endpoint.Requests.Single();
        Equal("https://catalog.invalid/api/models/providers/groq?types=chat%2Cimage%2Cclassifier", request.Url, "catalog URL");
        Equal("application/json", request.Headers["accept"], "accept");
        Check(System.Text.RegularExpressions.Regex.IsMatch(request.Headers["User-Agent"], @"^pi/1\.1\.0 \((win32|linux|darwin); dotnet/[0-9.]+; [a-z0-9]+\)$"), "user agent: " + request.Headers["User-Agent"]);
        Check(!request.Headers.ContainsKey("if-none-match"), "no validator without a cached body");
        Equal("GPT OSS 120B (remote)", registry.Find("groq", "openai/gpt-oss-120b")!.Name, "remote entry replaces the built-in in place");
        Equal("groq/groq-new", registry.GetAll().Where(model => model.Provider == "groq").Last().Reference, "new entries are appended");
        var stored = (await store.ReadAsync("groq", CancellationToken.None))!;
        Equal(2, stored.Models.Count, "unsupported types and id-less entries dropped");
        Equal("\"v1\"", stored.ETag, "etag stored verbatim"); Equal(1_800_000_000_000d, stored.CheckedAt, "checkedAt");
        Equal((double)(BuiltinModelCatalog.GeneratedAtUnixMilliseconds + 60_000), stored.LastModified, "lastModified");
        // Within four hours nothing is requested; the restored overlay stays.
        clock = clock.AddHours(3); phase = 1;
        var fresh = RemoteRegistry(endpoint, store, () => clock);
        await fresh.RefreshAsync(true, null, ["groq"], CancellationToken.None);
        Equal(1, endpoint.Requests.Count, "fresh catalog not revalidated");
        Equal("GPT OSS 120B (remote)", fresh.Find("groq", "openai/gpt-oss-120b")!.Name, "restored overlay");
        // Forced (or stale) revalidation sends the validator; 304 keeps the overlay and moves checkedAt.
        await fresh.RefreshAsync(true, true, ["groq"], CancellationToken.None);
        Equal("\"v1\"", endpoint.Requests[^1].Headers["if-none-match"], "validator sent");
        Equal(clock.ToUnixTimeMilliseconds(), (long)(await store.ReadAsync("groq", CancellationToken.None))!.CheckedAt!.Value, "304 updates checkedAt");
        Check(fresh.Find("groq", "groq-new") is not null, "304 keeps the overlay");
        // Startup restore never touches the network.
        var offline = await ModelRegistry.CreateAsync(new() { Environment = Env(), ModelsStore = store, CatalogBaseUrl = "https://catalog.invalid",
            CreateCatalogClient = () => throw new InvalidOperationException("network at startup") }, CancellationToken.None);
        Check(offline.Find("groq", "groq-new") is not null, "startup restores without network");
    }

    private static async Task RemoteFailures()
    {
        var status = HttpStatusCode.NotFound;
        var endpoint = new CatalogEndpoint(_ => new HttpResponseMessage(status));
        var store = new InMemoryModelsStore(); var clock = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        var registry = RemoteRegistry(endpoint, store, () => clock);
        await registry.RefreshAsync(true, null, ["groq"], CancellationToken.None);
        var stored = (await store.ReadAsync("groq", CancellationToken.None))!;
        Check(stored.Models.Count == 0 && stored.LastModified == 0 && stored.ETag is null && stored.CheckedAt == 1_800_000_000_000d, "404 persists an empty, checked entry");
        status = HttpStatusCode.ServiceUnavailable; clock = clock.AddHours(5);
        var errors = await registry.RefreshAsync(true, null, ["groq"], CancellationToken.None);
        Equal("Model catalog request failed for groq: 503", errors["groq"].Message, "transient failure reported");
        Equal(4, endpoint.Requests.Count, "503 retried twice after the first attempt");
        // A stored catalog that is not newer than the built-in data is ignored.
        var older = new InMemoryModelsStore();
        await older.WriteAsync("groq", new(new JsonArray(JsonNode.Parse("""{"id":"stale","provider":"groq","api":"openai-completions"}""")),
            BuiltinModelCatalog.GeneratedAtUnixMilliseconds, 1), CancellationToken.None);
        var restored = await ModelRegistry.CreateAsync(new() { Environment = Env(), ModelsStore = older }, CancellationToken.None);
        Check(restored.Find("groq", "stale") is null, "older catalog ignored");
        // radius: a stored gateway catalog replaces the shipped baseline.
        var radius = new InMemoryModelsStore();
        await radius.WriteAsync("radius", new(new JsonArray(JsonNode.Parse("""{"id":"only","provider":"radius","api":"pi-messages","name":"Only"}"""))), CancellationToken.None);
        var replaced = await ModelRegistry.CreateAsync(new() { Environment = Env(), ModelsStore = radius }, CancellationToken.None);
        Names(["radius/only"], replaced.GetAll().Where(model => model.Provider == "radius").Select(model => model.Reference), "radius catalog replaces the baseline");
    }

    private static void RemoteParse()
    {
        Names(["a", "b"], RemoteCatalogOverlay.ParseCatalog("p", JsonNode.Parse("""[{"id":"a"},{"id":"b","type":"chat"},{"id":"c","type":"audio"}]""")).Select(model => model.Id), "array");
        Names(["a"], RemoteCatalogOverlay.ParseCatalog("p", JsonNode.Parse("""{"x":{"id":"a","provider":"other"}}""")).Select(model => model.Provider + model.Id).Select(id => id[1..]), "object of entries");
        Equal("p", RemoteCatalogOverlay.ParseCatalog("p", JsonNode.Parse("""{"models":[{"id":"a","provider":"other"}]}""")).Single().Provider, "provider is forced");
        Equal("Invalid model catalog for provider \"p\"", Throws<InvalidOperationException>(() => RemoteCatalogOverlay.ParseCatalog("p", JsonNode.Parse("3")), "scalar").Message, "invalid");
    }

    private static Task RemoteFileStore() => WithTemp("store", async root =>
    {
        var path = Path.Combine(root, "agent", "models-store.json");
        var store = new FileModelsStore(path);
        Equal<ModelsStoreEntry?>(null, await store.ReadAsync("groq", CancellationToken.None), "missing file");
        Check(!File.Exists(path), "reads create nothing");
        await store.WriteAsync("groq", new(new JsonArray(JsonNode.Parse("""{"id":"a"}""")), 5, 6, "\"e\""), CancellationToken.None);
        await store.WriteAsync("xai", new([]), CancellationToken.None);
        Equal("{\n  \"groq\": {\n    \"models\": [\n      {\n        \"id\": \"a\"\n      }\n    ],\n    \"lastModified\": 5,\n    \"checkedAt\": 6,\n    \"etag\": \"\\\"e\\\"\"\n  },\n  \"xai\": {\n    \"models\": []\n  }\n}",
            await File.ReadAllTextAsync(path), "two-space JSON");
        await store.DeleteAsync("groq", CancellationToken.None);
        Check(await store.ReadAsync("groq", CancellationToken.None) is null && await store.ReadAsync("xai", CancellationToken.None) is not null, "delete");
        Check(!File.Exists(path + ".lock"), "lock released");
    });

    private static async Task RemoteCoordinator()
    {
        var runs = 0; var gate = new TaskCompletionSource();
        var runtime = new object();
        Task Refresh(CancellationToken token) { Interlocked.Increment(ref runs); return gate.Task.WaitAsync(token); }
        using var first = new CancellationTokenSource();
        var a = ModelCatalogRefreshCoordinator.Shared.RefreshAsync(runtime, Refresh, first.Token);
        var b = ModelCatalogRefreshCoordinator.Shared.RefreshAsync(runtime, Refresh, CancellationToken.None);
        first.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => a, "first caller cancels only its wait");
        Check(!b.IsCompleted, "second waiter still waits");
        gate.SetResult();
        await b;
        Equal(1, runs, "one shared run");
        var c = ModelCatalogRefreshCoordinator.Shared.RefreshAsync(runtime, Refresh, CancellationToken.None);
        await c; Equal(2, runs, "a finished run is not reused");
    }
}
