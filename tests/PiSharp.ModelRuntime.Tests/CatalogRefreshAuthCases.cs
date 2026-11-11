// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/ai/src/models.ts.
// Authored native regression; not an upstream execution capture.
using System.Net;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.Catalogs;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Models;

// Pi b4afdddf44e059c67ccf5097e5317d913521b470: models.ts refreshModelsAuth. All endpoints and credentials are fake.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CatalogRefreshAuthCases() =>
    [
        Case("catalog-refresh-auth.unconfigured-restores-without-network", RefreshUnconfigured),
        Case("catalog-refresh-auth.expired-oauth-never-resolves-for-public-catalog", RefreshExpiredOAuth),
        Case("catalog-refresh-auth.api-key-is-not-sent-to-public-catalog", RefreshApiKey),
        Case("catalog-refresh-auth.rechecks-configuration", RefreshConfigurationChanges),
        Case("catalog-refresh-auth.auth-check-errors-are-per-provider", RefreshAuthErrors),
        Case("catalog-refresh-auth.personalized-catalog-still-resolves-oauth", RefreshPersonalizedOAuth),
        Case("catalog-refresh-auth.custom-key-resolution-and-base-url-preserved", RefreshCustomKey),
        Case("catalog-refresh-auth.unsupported-catalog-versus-real-failure", RefreshFailures),
    ];

    private static ModelRegistryOptions RefreshOptions(HttpMessageHandler endpoint, InMemoryModelsStore? store = null) => new()
    {
        Environment = Env(), ModelsStore = store ?? new(), CatalogBaseUrl = "https://catalog.invalid",
        CreateCatalogClient = () => new HttpClient(endpoint, disposeHandler: false), FileExists = _ => false,
        Home = Path.GetTempPath(),
    };

    private static async Task RefreshUnconfigured()
    {
        var endpoint = new RefusingHandler();
        var store = new InMemoryModelsStore();
        await store.WriteAsync("groq", new(new JsonArray(JsonNode.Parse("""{"id":"saved-only","provider":"groq","api":"openai-completions"}""")),
            CurrentModelCatalog.GeneratedAtUnixMilliseconds + 1, 1), CancellationToken.None);
        var registry = ModelRegistry.Create(RefreshOptions(endpoint, store));
        var errors = await registry.RefreshAsync(true, true, ["groq"], CancellationToken.None);
        Equal(0, errors.Count, "unconfigured skip is not a refresh failure");
        Equal(0, endpoint.Requests, "force does not bypass configured auth");
        Check(registry.Find("groq", "saved-only") is not null, "saved catalog restored while unconfigured");
        await registry.RefreshAsync(false, true, ["groq"], CancellationToken.None);
        Check(registry.Find("groq", "saved-only") is not null, "saved catalog restored offline");
    }

    private static async Task RefreshExpiredOAuth()
    {
        var expired = new OAuthCredentialSnapshot("expired-access", "expired-refresh", 0);
        var source = new FakeCredentialSource().OAuth("github-copilot", expired);
        var flow = new FakeOAuthFlow();
        var authEndpoint = new RefusingHandler();
        var authentication = new ProviderLiveAuthentication(ProviderAuthCatalog.Find("github-copilot")!, source,
            new LiveProcessEnvironment(Env(), []), () => new HttpClient(authEndpoint), TimeProvider.System, _ => flow);
        var resolutions = 0;
        var endpoint = new FakeEndpoint(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var registry = ModelRegistry.Create(RefreshOptions(endpoint) with
        {
            StoredCredentials = new Dictionary<string, ProviderStoredCredential> { ["github-copilot"] = new("oauth") },
            ResolveOAuthKey = async (_, token) => { resolutions++; return (await authentication.ResolveAsync(token))?.ApiKey; },
        });
        Equal(0, (await registry.RefreshAsync(true, true, ["github-copilot"], CancellationToken.None)).Count, "configured OAuth permits public refresh");
        Equal(1, endpoint.Snapshot().Length, "one public catalog request");
        Check(!endpoint.Snapshot()[0].Headers.ContainsKey("Authorization"), "public catalog receives no access token");
        Equal(0, resolutions, "no credential resolution");
        Equal(0, flow.Refreshes, "expired OAuth stays unrefreshed");
        Equal(0, authEndpoint.Requests, "no auth network requests");
        Equal(expired, await source.ReadAsync("github-copilot", CancellationToken.None), "expired stored token untouched");
    }

    private static async Task RefreshApiKey()
    {
        var endpoint = new FakeEndpoint(_ => new(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var registry = ModelRegistry.Create(RefreshOptions(endpoint) with { Environment = Env(("GROQ_API_KEY", "fake-secret")) });
        Equal(0, (await registry.RefreshAsync(true, true, ["groq"], CancellationToken.None)).Count, "configured environment API key");
        Equal(1, endpoint.Snapshot().Length, "configured API key refreshes catalog");
        Check(!endpoint.Snapshot()[0].Headers.ContainsKey("Authorization"), "API key is not sent to pi.dev");
    }

    private static async Task RefreshConfigurationChanges()
    {
        var configured = false;
        var endpoint = new FakeEndpoint(_ => new(HttpStatusCode.NotFound));
        var registry = ModelRegistry.Create(RefreshOptions(endpoint) with
        { Environment = name => configured && name == "GROQ_API_KEY" ? "key" : null });
        Equal(null, registry.CheckAuth("groq"), "prime unconfigured availability cache");
        configured = true;
        await registry.RefreshAsync(true, true, ["groq"], CancellationToken.None);
        Equal(1, endpoint.Snapshot().Length, "newly configured provider refreshes");
        Equal("GROQ_API_KEY", registry.CheckAuth("groq"), "prime configured availability cache");
        configured = false;
        await registry.RefreshAsync(true, true, ["groq"], CancellationToken.None);
        Equal(1, endpoint.Snapshot().Length, "removed auth no longer permits refresh");
    }

    private static async Task RefreshAuthErrors()
    {
        var failure = new InvalidOperationException("fake auth check failure");
        var endpoint = new FakeEndpoint(_ => new(HttpStatusCode.NotFound));
        var registry = ModelRegistry.Create(RefreshOptions(endpoint) with
        { Environment = name => name == "GROQ_API_KEY" ? throw failure : name == "DEEPSEEK_API_KEY" ? "key" : null });
        var errors = await registry.RefreshAsync(true, true, ["groq", "deepseek"], CancellationToken.None);
        Equal(1, errors.Count, "only failing provider reports an error");
        Check(ReferenceEquals(failure, errors["groq"]), "native auth exception remains identifiable");
        Equal(1, endpoint.Snapshot().Length, "other configured providers continue refreshing");
        Check(endpoint.Snapshot()[0].Url.Contains("/deepseek?", StringComparison.Ordinal), "successful provider refreshed");
    }

    private static async Task RefreshPersonalizedOAuth()
    {
        var resolutions = 0;
        var endpoint = new FakeEndpoint(seen => seen.Url.Contains("/models/user", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") } : new(HttpStatusCode.NotFound));
        var registry = ModelRegistry.Create(RefreshOptions(endpoint) with
        {
            StoredCredentials = new Dictionary<string, ProviderStoredCredential> { ["openrouter"] = new("oauth") },
            ResolveOAuthKey = (_, _) => { resolutions++; return Task.FromResult<string?>("fresh-access"); },
        });
        Equal(0, (await registry.RefreshAsync(true, true, ["openrouter"], CancellationToken.None)).Count, "personalized refresh succeeds");
        Equal(1, resolutions, "personalized catalog resolves OAuth");
        Equal("Bearer fresh-access", endpoint.Snapshot().Single(seen => seen.Url.Contains("/models/user", StringComparison.Ordinal)).Headers["Authorization"],
            "resolved credential goes to personalized provider");
        Check(!endpoint.Snapshot()[0].Headers.ContainsKey("Authorization"), "public catalog phase still has no credentials");
    }

    private static async Task RefreshCustomKey()
    {
        var commands = 0;
        var endpoint = new FakeEndpoint(seen => seen.Url.Contains("/models/user", StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""") } : new(HttpStatusCode.NotFound));
        var registry = ModelRegistry.Create(RefreshOptions(endpoint) with
        { RunCommand = _ => { commands++; return "custom-key"; } });
        registry.RegisterExtensionProvider("groq", new() { ["apiKey"] = "!custom-key" });
        registry.RegisterExtensionProvider("openrouter", new() { ["apiKey"] = "!custom-key", ["baseUrl"] = "https://custom.invalid/v1" });
        await registry.RefreshAsync(true, true, ["groq"], CancellationToken.None);
        Equal(0, commands, "configured custom key does not execute for public catalog");
        Equal(0, (await registry.RefreshAsync(true, true, ["openrouter"], CancellationToken.None)).Count, "custom personalized key succeeds");
        Equal(1, commands, "personalized provider resolves custom credential");
        var personal = endpoint.Snapshot().Single(seen => seen.Url.Contains("/models/user", StringComparison.Ordinal));
        Equal("https://custom.invalid/v1/models/user", personal.Url, "custom provider base URL preserved");
        Equal("Bearer custom-key", personal.Headers["Authorization"], "custom credential preserved");
    }

    private static async Task RefreshFailures()
    {
        var status = HttpStatusCode.NotFound;
        var endpoint = new FakeEndpoint(_ => new(status));
        var registry = ModelRegistry.Create(RefreshOptions(endpoint) with { Environment = Env(("GROQ_API_KEY", "key")) });
        Equal(0, (await registry.RefreshAsync(true, true, ["groq"], CancellationToken.None)).Count, "unsupported public catalog is tolerated");
        status = HttpStatusCode.ServiceUnavailable;
        var errors = await registry.RefreshAsync(true, true, ["groq"], CancellationToken.None);
        Equal("Model catalog request failed for groq: 503", errors["groq"].Message, "real HTTP failure is reported");
        Equal(4, endpoint.Snapshot().Length, "real HTTP failure retains bounded retry behavior");
        var failure = new InvalidOperationException("fake OAuth refresh failure");
        var personal = ModelRegistry.Create(RefreshOptions(endpoint) with
        {
            StoredCredentials = new Dictionary<string, ProviderStoredCredential> { ["openrouter"] = new("oauth") },
            ResolveOAuthKey = (_, _) => Task.FromException<string?>(failure),
        });
        var personalErrors = await personal.RefreshAsync(true, true, ["openrouter"], CancellationToken.None);
        Check(ReferenceEquals(failure, personalErrors["openrouter"]), "personalized auth errors remain real failures");
        Equal(4, endpoint.Snapshot().Length, "credential resolution failure precedes network");
    }
}
