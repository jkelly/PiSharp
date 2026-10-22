using System.Collections.Immutable;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Contracts;

// The credential seam (W2 brief, WU2): IProviderCredentialSource replaces the CLI's auth.json store. Credentials resolve through a fake
// source (api_key and OAuth, refreshed and persisted through the source's ModifyAsync), and two sources never cross. Requests go to an
// in-process fake endpoint; OAuth refreshes run on a fake flow; every other client refuses.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CredentialCases() =>
    [
        Case("credentials.api-key-from-a-fake-source-reaches-the-request", CredentialApiKey),
        Case("credentials.environment-only-without-a-source", CredentialEnvironment),
        Case("credentials.oauth-refresh-persists-through-the-fake-source", CredentialOAuth),
        Case("credentials.anthropic-resolves-from-the-fake-source", CredentialAnthropic),
        Case("credentials.two-sources-never-cross", CredentialIsolation),
    ];

    /// <summary>An in-memory credential source: api_key and OAuth entries, with ModifyAsync serialized per source.</summary>
    internal sealed class FakeCredentialSource : IProviderCredentialSource
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly Dictionary<string, StoredCredentialEntry> entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OAuthCredentialSnapshot> oauth = new(StringComparer.Ordinal);
        internal int EntryReads, Modifications;

        internal FakeCredentialSource ApiKey(string provider, string key, IReadOnlyDictionary<string, string>? environment = null)
        { lock (entries) entries[provider] = new("api_key", key, environment); return this; }

        internal FakeCredentialSource OAuth(string provider, OAuthCredentialSnapshot credential)
        { lock (entries) { entries[provider] = new("oauth", null, null); oauth[provider] = credential; } return this; }

        public Task<OAuthCredentialSnapshot?> ReadAsync(string provider, CancellationToken cancellationToken)
        { lock (entries) return Task.FromResult(oauth.GetValueOrDefault(provider)); }

        public async Task<OAuthCredentialSnapshot?> ModifyAsync(string provider,
            Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                Interlocked.Increment(ref Modifications);
                var current = await ReadAsync(provider, cancellationToken);
                var next = await mutation(current, CancellationToken.None);
                if (next is null) return current;
                OAuth(provider, next);
                return next;
            }
            finally { gate.Release(); }
        }

        public Task<StoredCredentialEntry?> ReadEntryAsync(string provider, CancellationToken cancellationToken)
        { Interlocked.Increment(ref EntryReads); lock (entries) return Task.FromResult(entries.GetValueOrDefault(provider)); }

        public Task<IReadOnlyList<(string Provider, string Type)>> ListAsync(CancellationToken cancellationToken)
        { lock (entries) return Task.FromResult<IReadOnlyList<(string, string)>>([.. entries.Select(pair => (pair.Key, pair.Value.Type))]); }
    }

    /// <summary>An OAuth flow whose refresh issues the next access token without a request.</summary>
    internal sealed class FakeOAuthFlow : IProviderOAuth
    {
        internal int Refreshes;
        public string Name => "Fake OAuth";
        public bool IsSubscription => false;
        public string? LoginLabel => null;
        public Task<OAuthCredentialSnapshot> LoginAsync(IProviderAuthInteraction interaction, ProviderLoginOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Login is not part of these cases.");
        public ProviderModelAuth ToAuth(OAuthCredentialSnapshot credential) => new(credential.Access);
        public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
        {
            var next = Interlocked.Increment(ref Refreshes) + 1;
            return Task.FromResult(new OAuthCredentialSnapshot("access-" + next, "refresh-" + next, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()));
        }
    }

    /// <summary>A runtime over explicit inputs only: no auth.json, no models.json, refusing catalog and auth clients.</summary>
    private static LiveRouteRuntime Runtime(Func<string, string?> environment, HttpMessageHandler endpoint, IProviderCredentialSource? source) =>
        new(environment, () => endpoint, CreateAuthHttp: () => new HttpClient(new RefusingHandler()))
        {
            Credentials = source is null ? null : () => source, CreateCatalogClient = () => new HttpClient(new RefusingHandler())
        };

    private static RegistryModel GroqModel(ModelRegistry registry) =>
        registry.Find("groq", ModelResolver.DefaultModelFor("groq")!) ?? throw new InvalidOperationException("The default Groq model is missing.");

    /// <summary>One request through the selection's live route; the fake endpoint completes it.</summary>
    private static async Task<StreamEvent?> SendAsync(LiveRouteRuntime runtime, ModelRegistry registry, RegistryModel model)
    {
        var selection = LiveSessionSelection.FromEntry(model, registry, null);
        using var connection = selection.ConnectRoute(runtime);
        StreamEvent? last = null;
        await foreach (var frame in connection.CreateTransport().StreamAsync(new ChatRequest(selection.Model,
            [new("user", JsonData.Parse("""{"role":"user","content":"hello","timestamp":1}"""))], 1)))
            last = frame;
        return last;
    }

    private static async Task CredentialApiKey()
    {
        var source = new FakeCredentialSource().ApiKey("groq", "gsk-fake-stored");
        var endpoint = new FakeEndpoint(_ => Sse(CompletionsSse));
        var runtime = Runtime(Env(), endpoint, source);
        Check(ReferenceEquals(source, runtime.OpenCredentials()), "the runtime opens the fake source");
        var registry = await runtime.CreateModelRegistryAsync(CancellationToken.None);
        Equal("stored credential", registry.CheckAuth("groq"), "auth check");
        Equal(new ProviderAuthStatus(true, "stored"), registry.GetProviderAuthStatus("groq"), "auth status");
        var model = GroqModel(registry);
        var auth = registry.ResolveRequestAuth(model, out var error);
        Check(auth is { ApiKey: "gsk-fake-stored", Source: "stored credential" } && error is null, "request auth: " + error);
        var last = await SendAsync(runtime, registry, model);
        Check(last is StreamDone, "the stream completes: " + last?.GetType().Name);
        var seen = endpoint.Snapshot();
        Equal(1, seen.Length, "one request");
        Check(seen[0].Url.StartsWith(model.BaseUrl.TrimEnd('/'), StringComparison.Ordinal), "sent to the model's base URL: " + seen[0].Url);
        Equal("Bearer gsk-fake-stored", seen[0].Headers.GetValueOrDefault("Authorization"), "the stored key authenticates the request");
    }

    private static async Task CredentialEnvironment()
    {
        var endpoint = new FakeEndpoint(_ => Sse(CompletionsSse));
        var runtime = Runtime(Env(("GROQ_API_KEY", "gsk-from-env")), endpoint, null);
        Equal(null, runtime.OpenCredentials(), "no source");
        var registry = await runtime.CreateModelRegistryAsync(CancellationToken.None);
        Equal("GROQ_API_KEY", registry.CheckAuth("groq"), "the environment configures Groq");
        await SendAsync(runtime, registry, GroqModel(registry));
        Equal("Bearer gsk-from-env", endpoint.Snapshot()[0].Headers.GetValueOrDefault("Authorization"), "environment key");
        // resolveProviderAuth: a stored credential owns the provider, so the environment is not read for it.
        var stored = Runtime(Env(("GROQ_API_KEY", "gsk-from-env")), endpoint, new FakeCredentialSource().ApiKey("groq", "gsk-stored-wins"));
        var storedRegistry = await stored.CreateModelRegistryAsync(CancellationToken.None);
        Equal("gsk-stored-wins", storedRegistry.ResolveRequestAuth(GroqModel(storedRegistry), out _)?.ApiKey, "the stored key wins");
    }

    private static async Task CredentialOAuth()
    {
        var expired = new OAuthCredentialSnapshot("access-1", "refresh-1", 0);
        var source = new FakeCredentialSource().OAuth("github-copilot", expired);
        var flow = new FakeOAuthFlow();
        var refusing = new RefusingHandler();
        var authentication = new ProviderLiveAuthentication(ProviderAuthCatalog.Find("github-copilot")!, source,
            new LiveProcessEnvironment(Env(), []), () => new HttpClient(refusing), TimeProvider.System, _ => flow);
        var resolved = await authentication.ResolveAsync(CancellationToken.None);
        Check(resolved is { ApiKey: "access-2", Source: "OAuth" }, "the expired credential is refreshed: " + resolved?.Source);
        Equal(1, flow.Refreshes, "one refresh");
        Check(source.Modifications >= 1, "the refresh ran under the source's ModifyAsync");
        Equal("access-2", (await source.ReadAsync("github-copilot", CancellationToken.None))?.Access, "the rotation is persisted in the source");
        var again = await authentication.ResolveAsync(CancellationToken.None);
        Check(again is { ApiKey: "access-2" } && flow.Refreshes == 1, "a valid credential is not refreshed again");
        Equal(0, refusing.Requests, "no request");
        var registry = await Runtime(Env(), new RefusingHandler(), source).CreateModelRegistryAsync(CancellationToken.None);
        Equal("OAuth", registry.CheckAuth("github-copilot"), "the registry reads the stored OAuth credential");
        Check(registry.IsUsingOAuth("github-copilot"), "isUsingOAuth");
    }

    private static async Task CredentialAnthropic()
    {
        var source = new FakeCredentialSource().ApiKey("anthropic", "sk-ant-fake");
        var runtime = Runtime(Env(), new RefusingHandler(), source);
        var resolved = await runtime.CreateAnthropicAuthentication(runtime.CreateEnvironment()).ResolveAsync(CancellationToken.None);
        Equal(AuthenticationDiagnostic.Resolved, resolved.Diagnostic, "the stored key resolves");
        Check(source.EntryReads >= 1, "read through the source");
        var none = Runtime(Env(), new RefusingHandler(), new FakeCredentialSource());
        Check((await none.CreateAnthropicAuthentication(none.CreateEnvironment()).ResolveAsync(CancellationToken.None)).Diagnostic != AuthenticationDiagnostic.Resolved,
            "nothing stored and no environment: unresolved");
    }

    private static async Task CredentialIsolation()
    {
        var endpoint = new FakeEndpoint(_ => Sse(CompletionsSse));
        var first = Runtime(Env(), endpoint, new FakeCredentialSource().ApiKey("groq", "gsk-account-a"));
        var second = Runtime(Env(), endpoint, new FakeCredentialSource().ApiKey("groq", "gsk-account-b"));
        var firstRegistry = await first.CreateModelRegistryAsync(CancellationToken.None);
        var secondRegistry = await second.CreateModelRegistryAsync(CancellationToken.None);
        await SendAsync(second, secondRegistry, GroqModel(secondRegistry));
        await SendAsync(first, firstRegistry, GroqModel(firstRegistry));
        await SendAsync(second, secondRegistry, GroqModel(secondRegistry));
        var keys = endpoint.Snapshot().Select(seen => seen.Headers.GetValueOrDefault("Authorization")).ToImmutableArray();
        Check(keys.SequenceEqual(["Bearer gsk-account-b", "Bearer gsk-account-a", "Bearer gsk-account-b"]), "each runtime sends its own key: " + string.Join(",", keys));
    }
}
