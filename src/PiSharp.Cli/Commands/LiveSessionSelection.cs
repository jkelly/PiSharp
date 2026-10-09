using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.AI.Authentication;
using System.Security.Cryptography;
using PiSharp.AI;
using PiSharp.AI.Catalogs;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Protocols.OpenAICompletions;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

namespace PiSharp.Cli.Commands;

/// <summary>The live route's effects: the process environment reader (read once per name for the session), the provider HTTP
/// handler, and the <c>auth.json</c> path (null: no stored credentials) with the client and clock its OAuth refresh uses.</summary>
internal sealed record LiveSessionRuntime(Func<string, string?> ReadEnvironment, Func<HttpMessageHandler?> CreateHttpHandler,
    string? AuthPath = null, Func<HttpMessageInvoker>? CreateAuthHttp = null, TimeProvider? Time = null, string? ModelsPath = null)
{
    /// <summary>Provider variables read when the session starts (env-api-keys.ts, anthropic.ts and azure-openai-config.ts).</summary>
    internal static readonly ImmutableArray<string> ProviderVariables =
    [
        InjectedAuthenticationResolver.AnthropicAuthToken, InjectedAuthenticationResolver.AnthropicOAuthToken, InjectedAuthenticationResolver.AnthropicApiKey,
        InjectedAuthenticationResolver.AnthropicFederationRuleId, InjectedAuthenticationResolver.AnthropicOrganizationId,
        InjectedAuthenticationResolver.AnthropicServiceAccountId, InjectedAuthenticationResolver.AnthropicIdentityTokenFile,
        InjectedAuthenticationResolver.AnthropicWorkspaceId, "OPENAI_API_KEY", "OPENROUTER_API_KEY", "MISTRAL_API_KEY",
        "AZURE_OPENAI_API_KEY", AzureOpenAIConfiguration.BaseUrlVariable, AzureOpenAIConfiguration.ResourceNameVariable,
        "AZURE_OPENAI_API_VERSION", AzureOpenAIConfiguration.DeploymentNameMapVariable
    ];

    // A stored auth.json credential (written by /login) owns the Anthropic provider; the environment is read only without one.
    internal static LiveSessionRuntime Default => DefaultRuntime.Value;
    private static readonly Lazy<LiveSessionRuntime> DefaultRuntime = new(() =>
    {
        var authPath = PiSharp.Cli.Authentication.AuthJsonCredentialStore.CreateDefault().AuthPath;
        // model-runtime.ts: models.json (and models-store.json) live in the agent directory next to auth.json.
        return new(Environment.GetEnvironmentVariable, () => null, authPath, ModelsPath: Path.Combine(Path.GetDirectoryName(authPath)!, "models.json"));
    });

    /// <summary>The session's model registry (model-runtime.ts create): built-in shards, the runtime's models.json, persisted catalogs
    /// restored without network, the stored <c>auth.json</c> credentials, and a lazily read environment (each name read once).</summary>
    internal async Task<PiSharp.Cli.Models.ModelRegistry> CreateModelRegistryAsync(CancellationToken cancellationToken)
    {
        var environment = new PiSharp.Cli.Authentication.LiveProcessEnvironment(ReadEnvironment, []);
        var stored = new Dictionary<string, ProviderStoredCredential>(StringComparer.Ordinal);
        if (AuthPath is not null && File.Exists(AuthPath))
        {
            var store = new PiSharp.Cli.Authentication.AuthJsonCredentialStore(AuthPath, Time);
            try
            {
                foreach (var (provider, _) in await store.ListAsync(cancellationToken).ConfigureAwait(false))
                    if (await store.ReadEntryAsync(provider, cancellationToken).ConfigureAwait(false) is { } entry)
                        stored[provider] = new(entry.Type, entry.Key, entry.Environment,
                            // github-copilot filterModels: the OAuth credential's availableModelIds (IMPL-A1 keeps JSON-valued fields).
                            entry.Type == "oauth" && PiSharp.AI.Authentication.OAuth.GitHubCopilotOAuth.AvailableModels(
                                await store.ReadAsync(provider, cancellationToken).ConfigureAwait(false)) is { } ids ? [.. ids] : null);
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or PiSharp.AI.Authentication.OAuth.OAuthLifecycleException)
            { stored.Clear(); } // An unreadable store leaves the environment as the only credential source, as for availability upstream.
        }
        var registry = await PiSharp.Cli.Models.ModelRegistry.CreateAsync(new()
        {
            ModelsPath = ModelsPath, Environment = environment.Get, StoredCredentials = stored, CatalogBaseUrl = CatalogBaseUrl, CreateCatalogClient = CreateCatalogClient,
            ModelsStore = ModelsPath is null ? null : new PiSharp.Cli.Models.FileModelsStore(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ModelsPath))!, "models-store.json"))
        }, cancellationToken).ConfigureAwait(false);
        ConfigureRegistry?.Invoke(registry);
        return registry;
    }

    /// <summary>Extension registrations applied to every registry this runtime creates (pi.registerVirtualModel, IMPL-E).</summary>
    internal Action<PiSharp.Cli.Models.ModelRegistry>? ConfigureRegistry { get; init; }

    /// <summary>The session's environment, built once from <see cref="ReadEnvironment"/>.</summary>
    internal PiSharp.Cli.Authentication.LiveProcessEnvironment CreateEnvironment() => new(ReadEnvironment, ProviderVariables);

    /// <param name="configuredApiKey">The models.json <c>providers.anthropic.apiKey</c> (provider-composer composeApiKeyAuth), used when
    /// nothing is stored.</param>
    internal PiSharp.Cli.Authentication.AnthropicLiveAuthentication CreateAnthropicAuthentication(PiSharp.Cli.Authentication.LiveProcessEnvironment environment,
        Func<string?>? configuredApiKey = null) =>
        new(AuthPath is null ? null : new PiSharp.Cli.Authentication.AuthJsonCredentialStore(AuthPath, Time), environment,
            CreateAuthHttp ?? (() => new HttpClient()), Time) { ConfiguredApiKey = configuredApiKey };

    /// <summary>The remote model catalog origin (null: https://pi.dev) and its HTTP client (tests).</summary>
    internal string? CatalogBaseUrl { get; init; }
    internal Func<HttpClient>? CreateCatalogClient { get; init; }

    /// <summary>The home directory the AWS shared config and SSO cache are read from (null: the user profile).</summary>
    internal string? HomeDirectory { get; init; }
    /// <summary>Replaces a provider's OAuth flow (tests); null uses the catalog flow.</summary>
    internal Func<string, PiSharp.Cli.Authentication.OAuthFlowContext, PiSharp.AI.Authentication.OAuth.IProviderOAuth>? OAuthFlows { get; init; }
}
internal sealed class LiveSessionException(string code, string message) : Exception(message)
{ internal string Code { get; } = code; }

/// <summary>Explicit caller selection from byte-pinned released metadata, models.json or a restored catalog; parsing never acquires
/// credentials or sends.</summary>
internal sealed class LiveSessionSelection
{
    /// <summary>The providers whose fixed native factories predate the catalog routes; their unmodified pinned models keep those factories.</summary>
    private static readonly ImmutableHashSet<string> FixedRouteProviders = ["anthropic", "openai", "mistral", "openrouter", "azure"];

    /// <summary>Whether a chat model's API has a native live route. anthropic, azure and mistral keep their provider routes; openai-codex,
    /// github-copilot, amazon-bedrock and the Cloudflare providers need their own auth (IMPL-A1), and google-vertex needs ADC or an API-key
    /// transport mode; everything else streams through the catalog route of its API.</summary>
    internal static bool SupportedApi(string provider, string api) => provider switch
    {
        "anthropic" => api == "anthropic-messages", "mistral" => api == "mistral-conversations",
        "azure" => api is "azure-openai-responses" or "openai-completions",
        // IMPL-A1: per-request auth routes (Bedrock SigV4, Codex OAuth, Copilot tokens, Cloudflare ids).
        _ when LiveProviderRoute.Handles(provider) => LiveProviderRoute.SupportsApi(provider, api),
        _ => api is "openai-completions" or "openai-responses" or "anthropic-messages" or "google-generative-ai" or "pi-messages" or "mistral-conversations"
    };
    internal FrozenCatalogModel Definition { get; }
    internal ModelDescriptor Model { get; }
    internal int MaximumOutputTokens { get; }
    /// <summary>The registry entry the selection came from, and the registry that resolves its request auth (null: legacy pinned parse).</summary>
    internal PiSharp.Cli.Models.RegistryModel? Entry { get; private init; }
    internal PiSharp.Cli.Models.ModelRegistry? Registry { get; private init; }
    /// <summary>The thinking level a <c>--model pattern:level</c> or scoped pattern named (cliThinkingFromModel).</summary>
    internal string? PatternThinkingLevel { get; set; }
    internal ImmutableArray<string> Warnings { get; set; } = [];
    /// <summary>sdk.ts modelFallbackMessage: why the session runs on another model than the one it was continued with (or on none).</summary>
    internal string? FallbackMessage { get; set; }
    /// <summary>The model came from <c>--model</c> (buildSessionOptions options.model), which every session the run creates uses.</summary>
    internal bool FromCliModel { get; set; }
    internal ImmutableArray<PiSharp.Cli.Models.ScopedModel> ScopedModels { get; set; } = [];
    /// <summary>True when the unmodified pinned row of a fixed-route provider is selected (the pre-catalog factories serve it).</summary>
    internal bool FixedRoute => FixedRouteProviders.Contains(Model.Provider) && (Entry is null || Entry.Pinned is not null);
    private LiveSessionSelection(FrozenCatalogModel definition, int maximumOutputTokens)
    { Definition = definition; Model = new(definition.Id, definition.DeclaredApi, definition.Provider); MaximumOutputTokens = maximumOutputTokens; }

    internal static int ParseMaximumTokens(string? maximumTokens)
    {
        var tokens = 1024;
        if (maximumTokens is not null && (!int.TryParse(maximumTokens, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out tokens) || tokens is < 1 or > 8192))
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        return tokens;
    }

    /// <summary>An exact pinned id of a built-in provider (no models.json, no pattern matching, no environment).</summary>
    internal static LiveSessionSelection Parse(string? provider, string? model, string? maximumTokens)
    {
        if (provider is null || !PiSharp.Cli.Models.BuiltinModelCatalog.Has(provider) || string.IsNullOrWhiteSpace(model) ||
            model.Length > 1024 || model.Any(char.IsControl)) throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        var tokens = ParseMaximumTokens(maximumTokens);
        FrozenModelCatalog catalog;
        try { catalog = PiSharp.Cli.Models.BuiltinModelCatalog.Get(provider); }
        catch (PiSharp.Cli.Models.BuiltinCatalogException error) { throw new LiveSessionException(error.Code, error.Message); }
        if (!catalog.TryGetModel(CatalogModelType.Chat, model, out var definition) || !SupportedApi(provider, definition.DeclaredApi))
            throw new LiveSessionException("UnknownLiveModel", "Select a chat model from the pinned provider catalog for the supported live API.");
        var modelLimit = definition.Raw.Value.GetProperty("maxTokens").GetDouble();
        if (tokens > modelLimit) throw new LiveSessionException("LiveOutputLimit", "The requested output limit exceeds the selected model metadata.");
        return new(definition, tokens);
    }

    /// <summary>A resolved registry entry (any provider, models.json custom model or fallback id) for its live route.</summary>
    internal static LiveSessionSelection FromEntry(PiSharp.Cli.Models.RegistryModel entry, PiSharp.Cli.Models.ModelRegistry? registry, string? maximumTokens,
        bool useModelMaximum = false)
    {
        var tokens = useModelMaximum ? 0 : ParseMaximumTokens(maximumTokens);
        // A virtual entry has no route of its own: VirtualModelRoutingTransport.ForLive routes each request (pi.registerVirtualModel).
        if (registry is not null && entry.Type == CatalogModelType.Chat && PiSharp.Cli.Models.VirtualModels.IsVirtual(entry))
        {
            // The catalog row of a virtual selection only describes it; limits it leaves out (0) are its physical routes' business.
            var described = entry.With(json =>
            {
                if (entry.ContextWindow <= 0) json["contextWindow"] = 1;
                if (entry.MaxTokens <= 0) json["maxTokens"] = 1;
            });
            return new(described.ToDefinition(), Math.Max(1, (int)Math.Min(entry.MaxTokens, int.MaxValue))) { Entry = entry, Registry = registry };
        }
        // An extension's streamSimple serves its API (registerProvider): the model streams through the Node host.
        if (registry?.CustomStream(entry.Api) is { } custom && entry.Type == CatalogModelType.Chat && !PiSharp.Cli.Models.VirtualModels.IsVirtual(entry))
        {
            var described = entry.With(json =>
            {
                if (entry.ContextWindow <= 0) json["contextWindow"] = 128000;
                if (entry.MaxTokens <= 0) json["maxTokens"] = 16384;
            });
            var maximum = useModelMaximum ? (int)Math.Clamp(described.MaxTokens, 1, int.MaxValue) : Math.Max(1, tokens);
            return new(described.ToDefinition(), maximum) { Entry = entry, Registry = registry, CustomStream = () => custom(entry) };
        }
        if (entry.Type != CatalogModelType.Chat || PiSharp.Cli.Models.VirtualModels.IsVirtual(entry) || !SupportedApi(entry.Provider, entry.Api))
            throw new LiveSessionException("LiveApiUnavailable",
                $"Model \"{entry.Provider}/{entry.Id}\" uses the {(entry.Api.Length == 0 ? "unknown" : entry.Api)} API, which has no live route in PiSharp yet.");
        FrozenCatalogModel definition;
        try { definition = entry.ToDefinition(); }
        catch (CatalogReadException) { throw new LiveSessionException("UnknownLiveModel", $"Model \"{entry.Provider}/{entry.Id}\" lacks the catalog metadata its live route needs."); }
        // Pi-style entries ask for the model's own maxTokens (simple-options.ts buildBaseOptions).
        if (useModelMaximum) tokens = (int)Math.Clamp(definition.Raw.Value.GetProperty("maxTokens").GetDouble(), 1, int.MaxValue);
        if (tokens > definition.Raw.Value.GetProperty("maxTokens").GetDouble())
            throw new LiveSessionException("LiveOutputLimit", "The requested output limit exceeds the selected model metadata.");
        return new(definition, tokens) { Entry = entry, Registry = registry };
    }
    /// <param name="reresolve">The provider's auth resolution, run again before every request (model-runtime prepareRequest); null keeps
    /// the explicit admitted <paramref name="authentication"/> for the whole session.</param>
    internal ValueTask<LiveSessionConnection> ConnectResolvedAnthropicAsync(AuthenticationResolution authentication,
        HttpMessageHandler? handler = null, CancellationToken cancellationToken = default,
        Func<CancellationToken, ValueTask<AuthenticationResolution>>? reresolve = null)
    {
        if (Model.Provider != "anthropic" || Model.Api != "anthropic-messages")
            throw new ArgumentException("Explicit resolved authentication requires an Anthropic selection.");
        return LiveSessionConnection.ConnectResolvedAsync(this, authentication, handler, cancellationToken, reresolve);
    }

    /// <summary>Anthropic, resolved as upstream resolveProviderAuth does (stored auth.json credential, then the environment's
    /// AUTH_TOKEN, OAUTH_TOKEN, API_KEY, then workload identity federation) at start and before every request.</summary>
    internal async ValueTask<(AuthenticationResolution Authentication, HttpMessageHandler? Handler,
        Func<CancellationToken, ValueTask<AuthenticationResolution>> Reresolve)> ResolveAnthropicAsync(LiveSessionRuntime? runtime, CancellationToken cancellationToken)
    {
        if (Model.Provider != "anthropic") throw new ArgumentException("Anthropic resolution requires an Anthropic selection.");
        runtime ??= LiveSessionRuntime.Default;
        var registry = Registry;
        var authentication = runtime.CreateAnthropicAuthentication(runtime.CreateEnvironment(), registry is null ? null : () => registry.ConfiguredApiKey("anthropic"));
        AuthenticationResolution resolved;
        // Source ModelsError texts: a failed refresh or an unreadable store refuses the session with its reason.
        try { resolved = await authentication.ResolveAsync(cancellationToken).ConfigureAwait(false); }
        catch (PiSharp.AI.Authentication.OAuth.OAuthLifecycleException error) when (error.Failure == PiSharp.AI.Authentication.OAuth.OAuthLifecycleFailure.Refresh)
        { throw new LiveSessionException("LiveAuthenticationFailed", "OAuth refresh failed for anthropic"); }
        catch (Exception error) when (error is PiSharp.AI.Authentication.OAuth.OAuthLifecycleException or InvalidDataException or InvalidOperationException or IOException)
        { throw new LiveSessionException("LiveAuthenticationFailed", error is PiSharp.AI.Authentication.OAuth.OAuthLifecycleException
            ? "Credential store read failed for anthropic" : error.Message); }
        if (resolved is not { Diagnostic: AuthenticationDiagnostic.Resolved, Authentication: not null })
            throw new LiveSessionException("MissingLiveApiKey", "Run /login, or set ANTHROPIC_API_KEY (or ANTHROPIC_AUTH_TOKEN, ANTHROPIC_OAUTH_TOKEN, or the workload identity federation variables) before launching the live session.");
        return (resolved, runtime.CreateHttpHandler(), authentication.ResolveAsync);
    }

    /// <summary>A selection over explicit catalog metadata (fixture rows for providers whose pinned catalog is not shipped).</summary>
    internal static LiveSessionSelection FromDefinition(FrozenCatalogModel definition, int maximumOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!SupportedApi(definition.Provider, definition.DeclaredApi) || maximumOutputTokens is < 1 or > 8192)
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        return new(definition, maximumOutputTokens);
    }

    /// <summary>A virtual selection (registerVirtualModel): the session the router reads its branch from and records state on.</summary>
    internal Func<PiSharp.Cli.Models.IVirtualModelSession?>? VirtualSession { get; set; }
    internal bool IsVirtual => Entry is not null && PiSharp.Cli.Models.VirtualModels.IsVirtual(Entry);

    /// <summary>The extension stream that serves this model's API (registerProvider with streamSimple), or null.</summary>
    internal Func<IChatTransport>? CustomStream { get; private init; }

    /// <summary>This selection resolving its credentials through <paramref name="registry"/> (auth.json, models.json and the environment
    /// as they are now), with the same catalog row, output limit and annotations.</summary>
    internal LiveSessionSelection WithRegistry(PiSharp.Cli.Models.ModelRegistry registry) =>
        new(Definition, MaximumOutputTokens)
        {
            Entry = Entry is null ? null : registry.Find(Entry.Provider, Entry.Id) ?? Entry, Registry = registry, CustomStream = CustomStream,
            PatternThinkingLevel = PatternThinkingLevel, Warnings = Warnings, ScopedModels = ScopedModels, VirtualSession = VirtualSession
        };

    /// <summary>The model of a session that starts without one (sdk.ts createAgentSession when findInitialModel finds none): the Agent
    /// keeps its DEFAULT_MODEL (agent.ts: id, name, api and provider "unknown"), which every mode starts with and reports. Its provider
    /// has no auth, so each prompt is refused as "the selected model" until /model selects a real one; no model_change records it.</summary>
    internal static ModelDescriptor UnselectedModel { get; } = new("unknown", "unknown", "unknown");
    /// <summary>agent.ts DEFAULT_MODEL as get_state, get_available_models and extensions see it.</summary>
    internal static JsonData UnselectedWire { get; } = JsonData.Parse(
        """{"id":"unknown","name":"unknown","api":"unknown","provider":"unknown","baseUrl":"","reasoning":false,"input":[],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":0,"maxTokens":0}""");
    internal bool IsUnselected => Model == UnselectedModel;
    /// <summary>The selection of <see cref="UnselectedModel"/>. Its catalog row only satisfies the catalog shape (one text input and
    /// unit limits); the reported definition is <see cref="UnselectedWire"/>.</summary>
    internal static LiveSessionSelection Unselected()
    {
        var model = UnselectedModel;
        var raw = new
        {
            type = "chat", id = model.Id, api = model.Api, provider = model.Provider, name = "unknown", baseUrl = "",
            reasoning = false, input = new[] { "text" }, contextWindow = 1, maxTokens = 1,
            cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 }
        };
        var catalog = FrozenModelCatalog.ReadProviderJson(model.Provider, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, object> { [model.Api] = new Dictionary<string, object> { ["chat:" + model.Id] = raw } }),
            new(MaximumUtf8Bytes: 4096, MaximumModels: 1, MaximumProperties: 32, MaximumStringCharacters: 128));
        if (!catalog.TryGetModel(CatalogModelType.Chat, model.Id, out var definition)) throw new InvalidOperationException("Unselected model row is invalid.");
        return new(definition, 1);
    }

    internal LiveSessionConnection Connect(LiveSessionRuntime? runtime)
    {
        runtime ??= LiveSessionRuntime.Default;
        if (IsVirtual && Registry is not null) return LiveSessionConnection.ForVirtual(this, runtime);
        if (CustomStream is { } custom) return LiveSessionConnection.ForCustom(this, custom());
        if (LiveProviderRoute.TryConnect(this, runtime) is { } routed) return routed;
        var environment = runtime.CreateEnvironment();
        string? key; IReadOnlyDictionary<string, string>? headers = null;
        if (Registry is null && FixedRouteProviders.Contains(Model.Provider))
        {
            var variable = Model.Provider switch { "openai" => "OPENAI_API_KEY", "openrouter" => "OPENROUTER_API_KEY", "mistral" => "MISTRAL_API_KEY",
                "azure" => "AZURE_OPENAI_API_KEY", _ => "ANTHROPIC_API_KEY" };
            key = environment.Get(variable);
            if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
                throw new LiveSessionException("MissingLiveApiKey", "Set the existing " + variable + " environment variable before launching the live session.");
        }
        else
        {
            // model-registry.ts getApiKeyAndHeaders: stored api_key, models.json apiKey, the provider's variable; then configured headers.
            var registry = Registry ?? PiSharp.Cli.Models.ModelRegistry.Create(new() { Environment = environment.Get });
            var auth = registry.ResolveRequestAuth(Entry ?? PiSharp.Cli.Models.RegistryModel.FromCatalog(Definition), out var error);
            key = auth?.ApiKey;
            if (string.IsNullOrWhiteSpace(key) || key.Length > 4096 || key.Any(char.IsControl))
            {
                var variables = ProviderEnvironmentKeys.GetApiKeyVariables(Model.Provider);
                throw new LiveSessionException("MissingLiveApiKey", error ?? (variables is [var single]
                    ? "Set the existing " + single + " environment variable before launching the live session."
                    : $"No API key found for \"{Model.Provider}\". Set its API key in models.json, auth.json or the environment before launching the live session."));
            }
            headers = auth!.Headers;
        }
        AzureEndpointOptions? azure = null;
        if (Model.Provider == "azure")
        {
            // Azure models ship without a baseUrl: AZURE_OPENAI_BASE_URL, then AZURE_OPENAI_RESOURCE_NAME (azure-openai-config.ts).
            azure = new() { Environment = environment.Snapshot([.. LiveSessionRuntime.ProviderVariables.Where(name => name.StartsWith("AZURE_OPENAI_", StringComparison.Ordinal))]) };
            try { _ = AzureOpenAIConfiguration.ResolveBaseUrl(Definition.BaseUrl, azure); }
            catch (ArgumentException error) { throw new LiveSessionException("LiveAzureEndpoint", error.Message); }
        }
        // The profile joins Agent/session work before disposing factory-owned providers. Injected handlers remain caller-owned.
        return new(this, runtime.CreateHttpHandler(), key) { Azure = azure, RequestHeaders = headers };
    }
}

/// <summary>In-memory credential binding; no secret-bearing record, transcript, log or persistent setting.</summary>
internal sealed class LiveSessionConnection(LiveSessionSelection selection, HttpMessageHandler? handler, string credential) : IDisposable, IAsyncDisposable
{
    private readonly List<NativeHttpModelProvider> _providers = [];
    private LiveSessionSelection Selected => selection;
    private HttpMessageHandler? Handler => handler;
    private bool _disposed;
    private readonly object _resolvedGate = new();
    private readonly AsyncLocal<ResolvedFrame?> _insideResolved = new();
    private sealed class ResolvedFrame(ResolvedFrame? parent)
    {
        internal readonly ResolvedFrame? Parent = parent;
        private int active = 1;
        internal bool Active => Volatile.Read(ref active) != 0;
        internal void Deactivate() => Volatile.Write(ref active, 0);
    }
    // Bind every producer operation in its own execution context. No binding survives a yielded frame.
    private sealed class ResolvedFrameBinding : IDisposable
    {
        private readonly LiveSessionConnection connection;
        private readonly ResolvedFrame? prior;
        private readonly ResolvedFrame frame;
        internal ResolvedFrameBinding(LiveSessionConnection connection)
        {
            this.connection = connection;
            prior = connection._insideResolved.Value;
            frame = new(prior);
            connection._insideResolved.Value = frame;
        }
        public void Dispose()
        {
            frame.Deactivate();
            connection._insideResolved.Value = prior;
        }
    }
    private bool IsInsideActiveResolved()
    {
        for (var frame = _insideResolved.Value; frame is not null; frame = frame.Parent)
            if (frame.Active) return true;
        return false;
    }
    private readonly HashSet<Task> _resolvedOperations = [];
    private readonly List<Exception> _resolvedCleanupFailures = [];
    private AnthropicInjectedTransportLease? _resolvedMain;
    private AuthenticationResolution? _resolvedAuthentication;
    private Task? _resolvedClose;
    // Per-request re-resolution: a changed resolution (a refreshed OAuth token, a new login, a logout to the environment)
    // binds a fresh main lease; replaced leases may still serve in-flight streams and are disposed with the connection.
    private Func<CancellationToken, ValueTask<AuthenticationResolution>>? _reresolve;
    private readonly SemaphoreSlim _rebind = new(1, 1);
    private readonly List<AnthropicInjectedTransportLease> _replacedMains = [];
    /// <summary>Azure endpoint/deployment configuration (the session's AZURE_OPENAI_* values) for provider azure.</summary>
    internal AzureEndpointOptions? Azure { get; init; }
    /// <summary>Resolved models.json provider/model headers and <c>authHeader</c> (getApiKeyAndHeaders), sent with every request.</summary>
    internal IReadOnlyDictionary<string, string>? RequestHeaders { get; init; }
    /// <summary>The per-request auth route of amazon-bedrock, openai-codex, github-copilot, the Cloudflare providers and stored OAuth.</summary>
    internal LiveProviderRoute? ProviderRoute { get; init; }
    internal static async ValueTask<LiveSessionConnection> ConnectResolvedAsync(LiveSessionSelection selected,
        AuthenticationResolution authentication, HttpMessageHandler? handler, CancellationToken token,
        Func<CancellationToken, ValueTask<AuthenticationResolution>>? reresolve = null)
    {
        var main = await AcquireResolvedAsync(selected, authentication, handler, selected.MaximumOutputTokens, false, token).ConfigureAwait(false);
        return new(selected, handler, string.Empty) { _resolvedMain = main, _resolvedAuthentication = authentication, _reresolve = reresolve };
    }

    /// <summary>Source prepareRequest: resolve the provider's auth for this request and bind it. An unchanged resolution keeps the
    /// current lease (and its federation token cache).</summary>
    private async ValueTask<(AnthropicInjectedTransportLease Main, AuthenticationResolution Authentication)> CurrentResolvedAsync(CancellationToken token)
    {
        if (_reresolve is not { } reresolve)
            lock (_resolvedGate) return (_resolvedMain ?? throw new InvalidOperationException("Resolved main lease missing."),
                _resolvedAuthentication ?? throw new InvalidOperationException("Resolved authentication missing."));
        await _rebind.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var next = await reresolve(token).ConfigureAwait(false);
            if (next is not { Diagnostic: AuthenticationDiagnostic.Resolved, Authentication: { } fresh })
                throw new LiveSessionException("MissingLiveApiKey", "Provider is not configured: anthropic");
            AnthropicInjectedTransportLease current; AuthenticationResolution bound;
            lock (_resolvedGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                current = _resolvedMain ?? throw new InvalidOperationException("Resolved main lease missing.");
                bound = _resolvedAuthentication ?? throw new InvalidOperationException("Resolved authentication missing.");
            }
            if (Same(bound.Authentication!, fresh)) return (current, bound);
            var replacement = await AcquireResolvedAsync(Selected, next, Handler, MaximumOutputTokens, false, token).ConfigureAwait(false);
            lock (_resolvedGate)
            {
                if (_disposed) { _replacedMains.Add(replacement); throw new ObjectDisposedException(nameof(LiveSessionConnection)); }
                _replacedMains.Add(current); _resolvedMain = replacement; _resolvedAuthentication = next;
            }
            return (replacement, next);
        }
        finally { _rebind.Release(); }

        static bool Same(ResolvedAuthentication bound, ResolvedAuthentication fresh) =>
            bound.Kind == fresh.Kind && bound.Origin == fresh.Origin && bound.EnvironmentName == fresh.EnvironmentName &&
            string.Equals(bound.Secret, fresh.Secret, StringComparison.Ordinal) &&
            (bound.Kind != AuthenticationKind.WorkloadIdentityFederation || FederationNames.All(name => string.Equals(
                bound.CredentialEnvironment?.GetValue(name), fresh.CredentialEnvironment?.GetValue(name), StringComparison.Ordinal)));
    }
    private static readonly string[] FederationNames = [InjectedAuthenticationResolver.AnthropicFederationRuleId,
        InjectedAuthenticationResolver.AnthropicOrganizationId, InjectedAuthenticationResolver.AnthropicServiceAccountId,
        InjectedAuthenticationResolver.AnthropicIdentityTokenFile, InjectedAuthenticationResolver.AnthropicWorkspaceId];
    private static ValueTask<AnthropicInjectedTransportLease> AcquireResolvedAsync(LiveSessionSelection selected,
        AuthenticationResolution authentication, HttpMessageHandler? handler, int maximum, bool summary, CancellationToken token, bool thinking = false)
    {
        var definition = selected.Definition; var plain = summary && !thinking;
        var projection = AnthropicThinkingCompat(new AnthropicMessagesRequestOptions(MaximumTokens: maximum,
            ModelReasoning: definition.Raw.Value.GetProperty("reasoning").GetBoolean(),
            ModelSupportsImages: definition.DeclaresImageInput, ThinkingEnabled: false,
            MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputBytes: PiPayloadBudget.RequestPayloadBytes,
            CacheRetention: summary ? AnthropicCacheRetention.None : AnthropicCacheRetention.Short), definition.Raw, plain);
        // getApiKeyAndHeaders: models.json provider headers (authHeader with the resolved key) and model headers travel with every request.
        var configured = selected.Registry is { } registry && selected.Entry is { } entry
            ? registry.ConfiguredRequestHeaders(entry, authentication.Authentication is { Kind: not AuthenticationKind.WorkloadIdentityFederation } resolved ? resolved.Secret : null) : null;
        var options = new AnthropicMessagesKeyAuthRequestOptions(MaxTokens: maximum, MaximumPayloadBytes: PiPayloadBudget.RequestPayloadBytes,
            Headers: configured is null ? null : JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(configured)));
        return plain
            ? AnthropicResolvedTransports.AcquireSummaryAsync(selected.Model, new Uri(definition.BaseUrl), authentication,
                maximum, projection, options, handler, token)
            : AnthropicResolvedTransports.AcquireMainAsync(selected.Model, new Uri(definition.BaseUrl), authentication,
                projection, options, definition.Raw, handler, token);
    }
    /// <summary>Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT) anthropic-messages.ts buildParams: a
    /// <c>compat.supportsMidConvoEffort</c> model always sends managed adaptive thinking with its effort markers, including on the
    /// summary route, which carries no level metadata and would otherwise send disabled thinking the model cannot accept.
    /// Disabled thinking is sent only when <c>thinkingLevelMap.off !== null</c>; the metadata-free summary route of such a model
    /// therefore omits thinking entirely, which it expresses by projecting without reasoning (no off level is advertised).</summary>
    private static AnthropicMessagesRequestOptions AnthropicThinkingCompat(AnthropicMessagesRequestOptions projection, JsonData raw, bool summary)
    {
        var value = raw.Value;
        var supportsOff = !(value.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind == System.Text.Json.JsonValueKind.Object &&
            map.TryGetProperty("off", out var off) && off.ValueKind == System.Text.Json.JsonValueKind.Null);
        return projection with
        {
            SupportsMidConversationEffort = value.TryGetProperty("compat", out var compat) && compat.ValueKind == System.Text.Json.JsonValueKind.Object &&
                compat.TryGetProperty("supportsMidConvoEffort", out var mid) && mid.ValueKind == System.Text.Json.JsonValueKind.True,
            SupportsThinkingOff = supportsOff, ModelReasoning = projection.ModelReasoning && (supportsOff || !summary),
            AllowedFallbackModels = NativeProviderFactory.AnthropicFallbackModels(value)
        };
    }
    internal int MaximumOutputTokens => selection.MaximumOutputTokens;
    /// <summary>A virtual selection: its summaries are sized from the routed physical model, not from this selection.</summary>
    internal bool IsVirtual => _virtual is not null;
    private PiSharp.Cli.Models.VirtualModelRoutingTransport? _virtual;
    private readonly List<IDisposable> _virtualConnections = [];
    /// <summary>agent-session.ts with a virtual model: every request asks the router, then streams through the physical model's own
    /// live route (each route's connection is owned here).</summary>
    internal static LiveSessionConnection ForVirtual(LiveSessionSelection selection, LiveSessionRuntime runtime)
    {
        var connection = new LiveSessionConnection(selection, null, "");
        connection._virtual = PiSharp.Cli.Models.VirtualModelRoutingTransport.ForLive(selection.Registry!, selection.Entry!, runtime, null,
            () => selection.VirtualSession?.Invoke(), connection._virtualConnections);
        return connection;
    }
    private IChatTransport? _custom;
    /// <summary>A model an extension's streamSimple serves: every request streams through the Node host.</summary>
    internal static LiveSessionConnection ForCustom(LiveSessionSelection selection, IChatTransport transport) =>
        new(selection, null, "") { _custom = transport };
    private Func<CancellationToken, ValueTask<LiveSessionConnection>>? _deferredConnect;
    private ImmutableArray<string> _deferredLevels;
    private LiveSessionConnection? _deferredInner;
    private readonly SemaphoreSlim _deferredGate = new(1, 1);
    private Task? _deferredClose;
    /// <summary>agent-session.ts: a session starts with a selected model whose provider has no credentials yet; the prompt preflight
    /// refuses prompts until they appear, and the route connects on the first request after that (<paramref name="connect"/>, retried
    /// until it succeeds). Thinking levels come from the catalog row (getSupportedThinkingLevels).</summary>
    internal static LiveSessionConnection Deferred(LiveSessionSelection selection, ImmutableArray<string> levels,
        Func<CancellationToken, ValueTask<LiveSessionConnection>> connect) =>
        new(selection, null, "") { _deferredConnect = connect, _deferredLevels = levels };
    private async Task<LiveSessionConnection> ConnectDeferredAsync(CancellationToken token)
    {
        await _deferredGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _deferredInner ??= await _deferredConnect!(token).ConfigureAwait(false);
        }
        finally { _deferredGate.Release(); }
    }
    private async Task CloseDeferredAsync()
    {
        await _deferredGate.WaitAsync().ConfigureAwait(false);
        try { if (_deferredInner is { } inner) await inner.DisposeAsync().ConfigureAwait(false); }
        finally { _deferredGate.Release(); }
    }
    /// <summary>A transport of a deferred connection: it connects on its first request and then streams through the connected route's
    /// transport of the same kind (main, summary or cache-warming replay).</summary>
    private sealed class DeferredTransport(LiveSessionConnection owner, int? outputTokens, bool summary, bool replay, bool thinking = false) : IChatTransport, IThinkingLevelTransport
    {
        private readonly object gate = new();
        private IChatTransport? bound;
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model)
        {
            if (model != owner.Selected.Model) throw new ArgumentException("Unknown selected model.", nameof(model));
            return summary && !thinking ? ["off"] : owner._deferredLevels;
        }
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LiveSessionConnection? inner = null; string? refused = null;
            try { inner = await owner.ConnectDeferredAsync(cancellationToken).ConfigureAwait(false); }
            catch (ProviderNotConfiguredException error) { refused = error.Message; }
            if (inner is null)
            {
                // model-runtime.ts prepareRequest: the request fails as an error response carrying the ModelsError text.
                var model = request.Model;
                yield return new StreamError(StopReason.Error, new AssistantMessage(model.Api, model.Provider, model.Id, request.Timestamp, [], TokenUsage.Zero,
                    StopReason.Error, JsonFields.Empty.Set("errorMessage", JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(refused)))));
                yield break;
            }
            IChatTransport transport;
            lock (gate) transport = bound ??= replay ? inner.CreateCacheWarmTransport() : inner.CreateTransport(outputTokens, summary, thinking);
            await foreach (var observation in transport.StreamAsync(request, cancellationToken).ConfigureAwait(false)) yield return observation;
        }
    }
    /// <summary>model-runtime.ts prepareRequest ModelsError: the provider is unknown or resolves no auth for this request.</summary>
    internal sealed class ProviderNotConfiguredException(string message) : Exception(message);
    /// <summary>
    /// The summarization route (compaction, branch and bug report summaries) at <paramref name="maximum"/> output tokens, as
    /// compaction.ts completeSummarization sends it: cache retention "none", the request's routing session id, and
    /// <c>reasoning = level</c> only when <c>model.reasoning &amp;&amp; level &amp;&amp; level !== "off"</c> (createSummarizationOptions; branch
    /// summaries pass none). The request is the model's own request (its level metadata bound) at that level, or with reasoning
    /// undefined when there is none.
    /// </summary>
    /// <param name="carriesLevel">The summary sends the session's thinking level (compaction and bug reports; branch summaries
    /// do not). A virtual selection then uses its router's level instead.</param>
    internal IChatTransport CreateSummaryTransport(int maximum, string? level, bool carriesLevel = true)
    {
        // agent-session.ts _getSummarizationRequestAuth: a virtual selection summarizes through the physical model its router picks.
        if (_virtual is not null) return _virtual.Summary(maximum, carriesLevel);
        var raw = selection.Definition.Raw.Value;
        var reasoning = raw.TryGetProperty("reasoning", out var flag) && flag.ValueKind == System.Text.Json.JsonValueKind.True;
        // The provider streams clamp the level to the model's levels (models.ts clampThinkingLevel), as the session already does.
        var effective = reasoning && level is not (null or "" or "off")
            ? AI.Protocols.ProviderShared.ProviderTranscriptAccess.ClampThinkingLevel(selection.Definition.Raw, level) : null;
        if (effective == "off") effective = null;
        // anthropic-messages.ts streamSimple, budget thinking (no compat.forceAdaptiveThinking): adjustMaxTokensForThinking raises the
        // request's cap by the level's budget, up to model.maxTokens. The route's own budget then leaves 1024 answer tokens, as upstream.
        if (effective is not null && selection.Model.Api == "anthropic-messages" &&
            !(raw.TryGetProperty("compat", out var compat) && compat.ValueKind == System.Text.Json.JsonValueKind.Object &&
              compat.TryGetProperty("forceAdaptiveThinking", out var adaptive) && adaptive.ValueKind == System.Text.Json.JsonValueKind.True) &&
            raw.TryGetProperty("maxTokens", out var modelMaximum) && modelMaximum.TryGetDouble(out var ceiling) && ceiling > 0)
            maximum = (int)Math.Max(maximum, Math.Min(Math.Min((double)maximum + effective switch { "minimal" => 1024, "low" => 2048, "medium" => 8192, _ => 16384 },
                ceiling), int.MaxValue));
        // Without reasoning the request is the model's own request with reasoning undefined, which is what the session's route sends
        // at "off" (agent.ts: reasoning = level === "off" ? undefined : level); the model's metadata (thinkingLevelMap.off, fallbacks,
        // managed effort) still shapes it.
        return new SummaryLevelTransport(CreateTransport(maximum, summary: true, thinking: true), effective);
    }

    /// <summary>A summary route whose requests carry exactly the summary's effective reasoning level ("off" on a route that offers
    /// it when there is none; a route without an off level, such as managed effort, gets no level).</summary>
    internal sealed class SummaryLevelTransport(IChatTransport inner, string? level, bool offWhenOffered = true) : IChatTransport
    {
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            inner.StreamAsync(request with { ThinkingLevel = level ?? (offWhenOffered && inner is IThinkingLevelTransport levels &&
                levels.GetSupportedThinkingLevels(request.Model).Contains("off") ? "off" : null) }, cancellationToken);
    }

    /// <param name="summary">An uncached summary route (cache retention "none").</param>
    /// <param name="thinking">With <paramref name="summary"/>: the route keeps the model's thinking binding (its level metadata) and
    /// follows each request's level, as <see cref="CreateSummaryTransport"/> uses it; without it a summary binds thinking off and
    /// metadata-free.</param>
    internal IChatTransport CreateTransport(int? outputTokens = null, bool summary = false, bool thinking = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (thinking && !summary) throw new ArgumentException("Thinking selects a summary binding.", nameof(thinking));
        if (_deferredConnect is not null) return new DeferredTransport(this, outputTokens, summary, replay: false, thinking);
        if (_virtual is not null) return _virtual;
        if (_custom is not null) return _custom;
        if (_resolvedMain is not null)
        {
            lock (_resolvedGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var resolvedMaximum = outputTokens ?? MaximumOutputTokens;
                // A summary may reach the model's own cap (a thinking summary adds its budget, adjustMaxTokensForThinking).
                var limit = summary && selection.Definition.Raw.Value.TryGetProperty("maxTokens", out var modelCap) && modelCap.TryGetDouble(out var cap)
                    ? Math.Max(MaximumOutputTokens, (int)Math.Clamp(cap, 1, int.MaxValue)) : MaximumOutputTokens;
                if (resolvedMaximum <= 0 || resolvedMaximum > limit) throw new ArgumentOutOfRangeException(nameof(outputTokens));
                if (!summary && resolvedMaximum != MaximumOutputTokens) throw new ArgumentException("Main cap is fixed by selection.", nameof(outputTokens));
                return new ResolvedTransport(this, resolvedMaximum, summary) { Thinking = thinking };
            }
        }
        var model = selection.Model; var definition = selection.Definition;
        var maximum = outputTokens ?? MaximumOutputTokens;
        var reasoning = definition.Raw.Value.GetProperty("reasoning").GetBoolean();
        // A thinking-off summary binds without level metadata; a summary with a level keeps the main route's thinking binding.
        var plain = summary && !thinking;
        if (ProviderRoute is { } route) return Own(route.Create(handler, maximum, summary, thinking));
        if (model.Provider is not ("azure" or "anthropic") && !(selection.FixedRoute && RequestHeaders is null))
            return Own(CreateCatalogProvider(maximum, reasoning, summary, thinking));
        if (model.Api == "mistral-conversations")
        {
            var endpoint = new Uri(definition.BaseUrl);
            var costs = definition.Raw.Value.GetProperty("cost");
            var options = new MistralTextOptions(endpoint, SupportsText: true,
                new(costs.GetProperty("input").GetDouble(), costs.GetProperty("output").GetDouble(),
                    costs.GetProperty("cacheRead").GetDouble(), costs.GetProperty("cacheWrite").GetDouble()), "PiSharp")
                { MaxTokens = maximum, Reasoning = !plain && reasoning, CachePrompt = !summary,
                    SupportsImages = definition.DeclaresImageInput };
            var provider = plain
                ? NativeProviderFactory.CreateMistral(model, endpoint, credential, options, handler)
                : NativeProviderFactory.CreateMistralSimple(model, endpoint, credential, definition.Raw, options, handler);
            return Own(provider);
        }
        if (model.Api == "anthropic-messages")
        {
            var provider = NativeProviderFactory.CreateAnthropic(model, new Uri(definition.BaseUrl), credential,
                AnthropicThinkingCompat(new(MaximumTokens: maximum, ModelReasoning: reasoning, ModelSupportsImages: definition.DeclaresImageInput,
                    ThinkingEnabled: false, MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputBytes: PiPayloadBudget.RequestPayloadBytes,
                    CacheRetention: summary ? AnthropicCacheRetention.None : AnthropicCacheRetention.Short), definition.Raw, plain),
                new(MaxTokens: maximum, MaximumPayloadBytes: PiPayloadBudget.RequestPayloadBytes), handler, plain ? null : definition.Raw);
            return Own(provider);
        }
        if (model.Provider == "azure")
        {
            // providers/azure.ts: azure-openai-responses through its Simple stream, openai-completions through the OpenAI client.
            var azure = Azure ?? throw new InvalidOperationException("Azure endpoint configuration missing.");
            // getApiKeyAndHeaders: the configured models.json provider/model headers travel with every request.
            var azureHeaders = RequestHeaders is null ? null : JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(RequestHeaders));
            if (model.Api == "azure-openai-responses")
                return Own(NativeProviderFactory.CreateAzureResponses(model, credential, definition.Raw,
                    new(Reasoning: reasoning, MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes) { ModelSupportsImages = definition.DeclaresImageInput }, maximum, azure, handler,
                    fixedReasoningOff: plain, headers: azureHeaders));
            return Own(NativeProviderFactory.CreateAzureCompletions(model, credential, azure,
                new(Reasoning: reasoning, MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputBytes: PiPayloadBudget.RequestPayloadBytes,
                    ToolDeclarations: new(MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputBytes: PiPayloadBudget.RequestPayloadBytes)) { ModelSupportsImages = definition.DeclaresImageInput },
                new(MaxTokens: maximum, CacheRetention: summary ? CompletionsCacheRetention.None : CompletionsCacheRetention.Short,
                    MaximumPayloadBytes: PiPayloadBudget.RequestPayloadBytes) { ModelMetadata = definition.Raw, Headers = azureHeaders }, handler, plain ? null : definition.Raw));
        }
        if (model.Api == "openai-completions")
        {
            var endpoint = new Uri(definition.BaseUrl.TrimEnd('/') + "/chat/completions");
            var provider = NativeProviderFactory.CreateCompletions(model, endpoint, credential,
                new(Reasoning: reasoning, MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputBytes: PiPayloadBudget.RequestPayloadBytes,
                    ToolDeclarations: new(MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputBytes: PiPayloadBudget.RequestPayloadBytes))
                    { ModelSupportsImages = definition.DeclaresImageInput },
                new(MaxTokens: maximum, CacheRetention: summary ? CompletionsCacheRetention.None : CompletionsCacheRetention.Short,
                    MaximumPayloadBytes: PiPayloadBudget.RequestPayloadBytes) { ModelMetadata = definition.Raw }, handler, plain ? null : definition.Raw);
            return Own(provider);
        }
        var responses = NativeProviderFactory.CreateResponses(model, new Uri(definition.BaseUrl.TrimEnd('/') + "/responses"), credential,
            new(Reasoning: reasoning, MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes) { ModelSupportsImages = definition.DeclaresImageInput },
            new(SupportsMaxOutputTokens: true, MaxOutputTokens: maximum, MaximumPayloadBytes: PiPayloadBudget.RequestPayloadBytes, CacheRetention: summary ? "none" : null), handler, plain ? null : definition.Raw);
        return Own(responses);
    }
    /// <summary>cache-warmer.ts replay: the main route (same cache retention) with a one-token output cap.</summary>
    internal IChatTransport CreateCacheWarmTransport()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_deferredConnect is not null) return new DeferredTransport(this, null, false, replay: true);
        return _resolvedMain is not null ? new ResolvedTransport(this, 1, false) { Replay = true } : CreateTransport(1);
    }
    private IChatTransport Own(NativeHttpModelProvider provider)
    { _providers.Add(provider); return provider.Transport; }

    /// <summary>models.ts createProvider: the provider's API implementation at the model's own base URL with the session key and
    /// the resolved configured headers. The summary binding is metadata-free and uncached, as on the fixed routes.</summary>
    private NativeHttpModelProvider CreateCatalogProvider(int maximum, bool reasoning, bool summary, bool thinking = false)
    {
        var model = selection.Model; var definition = selection.Definition; var plain = summary && !thinking;
        var headers = RequestHeaders is null ? null : JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(RequestHeaders));
        switch (model.Api)
        {
            case "openai-completions":
                return NativeProviderFactory.CreateCatalogCompletions(model, definition.BaseUrl, credential,
                    new(Reasoning: reasoning, MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputBytes: PiPayloadBudget.RequestPayloadBytes,
                        ToolDeclarations: new(MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputBytes: PiPayloadBudget.RequestPayloadBytes)) { ModelSupportsImages = definition.DeclaresImageInput },
                    new(MaxTokens: maximum, CacheRetention: summary ? CompletionsCacheRetention.None : CompletionsCacheRetention.Short,
                        MaximumPayloadBytes: PiPayloadBudget.RequestPayloadBytes) { Headers = headers }, definition.Raw, handler, thinkingProfile: !plain);
            case "openai-responses":
                return NativeProviderFactory.CreateCatalogResponses(model, definition.BaseUrl, credential,
                    new(Reasoning: reasoning, MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes) { ModelSupportsImages = definition.DeclaresImageInput },
                    new(SupportsMaxOutputTokens: true, MaxOutputTokens: maximum, MaximumPayloadBytes: PiPayloadBudget.RequestPayloadBytes, CacheRetention: summary ? "none" : null) { Headers = headers },
                    definition.Raw, handler, thinkingProfile: !plain);
            case "anthropic-messages":
                return NativeProviderFactory.CreateCatalogAnthropic(model, definition.BaseUrl, credential,
                    AnthropicThinkingCompat(new(MaximumTokens: maximum, ModelReasoning: reasoning, ModelSupportsImages: definition.DeclaresImageInput,
                        ThinkingEnabled: false, MaximumMessages: 1024, MaximumEntryCharacters: PiPayloadBudget.RequestEntryCharacters, MaximumInputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputCharacters: PiPayloadBudget.RequestPayloadBytes, MaximumOutputBytes: PiPayloadBudget.RequestPayloadBytes,
                        CacheRetention: summary ? AnthropicCacheRetention.None : AnthropicCacheRetention.Short), definition.Raw, plain),
                    new(MaxTokens: maximum, Headers: headers, MaximumPayloadBytes: PiPayloadBudget.RequestPayloadBytes), definition.Raw, handler, thinkingProfile: !plain);
            case "google-generative-ai":
                return NativeProviderFactory.CreateCatalogGoogle(model, credential, definition.Raw,
                    new PiSharp.AI.Protocols.GoogleGenerativeAI.GoogleGenerativeAIOptions(definition.Raw) { MaxTokens = maximum, Headers = headers }, handler);
            case "pi-messages":
                return NativeProviderFactory.CreateCatalogPiMessages(model, credential, definition.Raw,
                    new PiSharp.AI.Protocols.PiMessages.PiMessagesOptions(definition.Raw)
                    { MaxTokens = maximum, Headers = headers, CacheRetention = summary ? "none" : null }, handler);
            case "mistral-conversations":
            {
                var endpoint = new Uri(definition.BaseUrl);
                var costs = definition.Raw.Value.GetProperty("cost");
                var options = new MistralTextOptions(endpoint, SupportsText: true,
                    new(costs.GetProperty("input").GetDouble(), costs.GetProperty("output").GetDouble(),
                        costs.GetProperty("cacheRead").GetDouble(), costs.GetProperty("cacheWrite").GetDouble()), "PiSharp")
                    { MaxTokens = maximum, Reasoning = !plain && reasoning, CachePrompt = !summary, SupportsImages = definition.DeclaresImageInput,
                      Headers = RequestHeaders?.ToImmutableDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal) };
                return NativeProviderFactory.CreateCatalogMistral(model, definition.BaseUrl, credential, definition.Raw, options, simple: !plain, handler);
            }
            default: throw new LiveSessionException("LiveApiUnavailable", $"The {model.Api} API has no live route in PiSharp yet.");
        }
    }
    public void Dispose()
    {
        if (_resolvedMain is not null || _deferredConnect is not null) throw new InvalidOperationException("Resolved connections require awaited DisposeAsync.");
        if (_disposed) return;
        _disposed = true;
        var failures = new List<Exception>();
        foreach (var provider in _providers)
            try { provider.Dispose(); } catch (Exception error) { failures.Add(error); }
        _providers.Clear();
        lock (_virtualConnections)
        {
            foreach (var routed in _virtualConnections)
                try { routed.Dispose(); } catch (Exception error) { failures.Add(error); }
            _virtualConnections.Clear();
        }
        // Injected HTTP handlers belong to the caller; factory-created handlers belong to each provider.
        if (failures.Count != 0) throw new AggregateException(failures);
    }
    internal void RefuseResolvedCleanupSelfWait()
    {
        if (IsInsideActiveResolved()) throw new InvalidOperationException("A resolved callback cannot join its own connection.");
        Volatile.Read(ref _deferredInner)?.RefuseResolvedCleanupSelfWait();
    }
    public ValueTask DisposeAsync()
    {
        RefuseResolvedCleanupSelfWait();
        if (_deferredConnect is not null)
            lock (_resolvedGate) { _disposed = true; return new(_deferredClose ??= CloseDeferredAsync()); }
        if (_resolvedMain is null) { Dispose(); return ValueTask.CompletedTask; }
        lock (_resolvedGate)
        {
            _disposed = true;
            return new(_resolvedClose ??= CloseResolvedAsync());
        }
    }
    private async Task CloseResolvedAsync()
    {
        await Task.Yield();
        Task[] pending;
        lock (_resolvedGate) pending = _resolvedOperations.ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        var failures = new List<Exception>();
        lock (_resolvedGate) failures.AddRange(_resolvedCleanupFailures);
        // A pending rebind completes (or fails) before its replaced and replacement leases are collected.
        await _rebind.WaitAsync().ConfigureAwait(false);
        AnthropicInjectedTransportLease[] leases;
        lock (_resolvedGate) leases = [.. _replacedMains, _resolvedMain ?? throw new InvalidOperationException("Resolved main lease missing.")];
        foreach (var lease in leases)
        {
            Task? original = null;
            try { using var cleanupFrame = new ResolvedFrameBinding(this); original = lease.DisposeAsync().AsTask(); await original.ConfigureAwait(false); }
            catch (Exception error) { failures.Add((Exception?)original?.Exception ?? error); }
        }
        if (failures.Count != 0) throw new AggregateException("Resolved connection cleanup failed.", failures);
    }
    private sealed class ResolvedTransport(LiveSessionConnection connection, int maximum, bool summary)
        : IChatTransport, IThinkingLevelTransport
    {
        /// <summary>A cache-warming replay: a main-route lease (cache retention kept) at its own output cap.</summary>
        internal bool Replay { get; init; }
        /// <summary>A summary carrying a reasoning level: an uncached main-route lease at the summary cap.</summary>
        internal bool Thinking { get; init; }
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model)
        {
            lock (connection._resolvedGate)
            {
                ObjectDisposedException.ThrowIf(connection._disposed, connection);
                if (model != selectionModel()) throw new ArgumentException("Unknown selected model.");
                if (summary && !Thinking) return ["off"];
                var main = connection._resolvedMain ?? throw new InvalidOperationException("Resolved main lease missing.");
                return ((IThinkingLevelTransport)main.Transport).GetSupportedThinkingLevels(model);
            }
        }
        private ModelDescriptor selectionModel() => connection._resolvedMain?.Model
            ?? throw new InvalidOperationException("Resolved main lease missing.");
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (connection._resolvedGate)
            {
                ObjectDisposedException.ThrowIf(connection._disposed, connection);
                if (connection._resolvedOperations.Count >= 128) throw new InvalidOperationException("Resolved operation admission limit.");
                connection._resolvedOperations.Add(completion.Task);
            }

            AnthropicInjectedTransportLease? summaryLease = null;
            IAsyncEnumerator<StreamEvent>? enumerator = null;
            Exception? streamFailure = null;
            try
            {
                using (var factoryFrame = new ResolvedFrameBinding(connection))
                {
                var (main, authentication) = await connection.CurrentResolvedAsync(cancellationToken).ConfigureAwait(false);
                var transport = main.Transport;
                if (summary || Replay)
                {
                    summaryLease = await AcquireResolvedAsync(connection.Selected, authentication, connection.Handler, maximum, summary, cancellationToken, Thinking).ConfigureAwait(false);
                    transport = summaryLease.Transport;
                }
                enumerator = transport.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
                }
                while (true)
                {
                    Task<bool>? move = null; bool next;
                    try { using var moveFrame = new ResolvedFrameBinding(connection); move = enumerator.MoveNextAsync().AsTask(); next = await move.ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        streamFailure = move is { IsCanceled: true } && error is OperationCanceledException canceled
                            ? new AnthropicAuthenticationOriginalCancellation("CLI stream", move, canceled)
                            : new AnthropicAuthenticationOriginalFailure("CLI stream", move, (Exception?)move?.Exception ?? error);
                        throw streamFailure;
                    }
                    if (!next) break;
                    StreamEvent current;
                    try { using var currentFrame = new ResolvedFrameBinding(connection); current = enumerator.Current; }
                    catch (Exception error)
                    {
                        streamFailure = new AnthropicAuthenticationOriginalFailure("CLI stream current", null, error);
                        throw streamFailure;
                    }
                    yield return current;
                }
            }
            finally
            {
                try
                {
                    var cleanup = new List<Exception>();
                    async Task JoinCleanup(IAsyncDisposable owner)
                    {
                        Task? original = null;
                        try { using var cleanupFrame = new ResolvedFrameBinding(connection); original = owner.DisposeAsync().AsTask(); await original.ConfigureAwait(false); }
                        catch (Exception error) { cleanup.Add(new AnthropicAuthenticationOriginalFailure("CLI stream cleanup", original, (Exception?)original?.Exception ?? error)); }
                    }
                    if (enumerator is not null) await JoinCleanup(enumerator).ConfigureAwait(false);
                    if (summaryLease is not null) await JoinCleanup(summaryLease).ConfigureAwait(false);
                    if (cleanup.Count != 0)
                    {
                        lock (connection._resolvedGate) connection._resolvedCleanupFailures.AddRange(cleanup);
                        if (streamFailure is not null) cleanup.Insert(0, streamFailure);
                        throw new AggregateException("Resolved stream and cleanup evidence.", cleanup);
                    }
                }
                finally
                {
                    lock (connection._resolvedGate) { connection._resolvedOperations.Remove(completion.Task); completion.SetResult(); }
                }
            }
        }
    }

}
