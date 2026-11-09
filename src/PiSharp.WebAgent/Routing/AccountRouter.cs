// Decision 0011 (run W2, WU6): the Web Agent's HostTransport. Every model request of a chat carries the chat's account as
// metadata.pisharp.account (ChatBindings.StreamFields through DurableDeviceOptions.SessionStream); RouteAsync reads it, strips
// metadata.pisharp (no provider ever sees it: the host route sends only the transcript, the clamped thinking level and the session id), and
// streams the request through that account's live route (PiSharp.ModelRuntime's LazyModelTransport over a LiveRouteRuntime whose
// credentials are that account's WebCredentialSource and whose environment is that account's AccountEnvironment). Each account has its own
// runtime, registry and routes ("generation"), so one account's key, sign-in or OAuth refresh is never used for another's request; a new
// key, a removal or a status change starts a new generation for the next request.
//
// No fallback: a request whose account is missing (unbound chat), unknown or removed, of another provider, expired or unavailable is not
// sent, and ends with an AccountRouteErrors text (the chat's "expired" state). Nothing switches to the active account or to an environment
// variable. A provider's 401 or a rejected refresh marks the account expired, so later requests (and the settings pages) see it; a refresh
// that failed on the network does not, and its error is retryable. Other provider errors pass through unchanged, except that the account's
// own key or tokens, should a provider echo one, are replaced by "[redacted]" (the error text reaches the chat page).
//
// W3, CP: a custom provider of models.json is routed with the provider's own key, whatever account the chat names (chats of custom
// providers bind none): its vault account when it has one, else a key-less generation whose credential is the file's $VAR reference (read
// from a host environment that shows only the variables the provider references) or, with no key at all, the placeholder "no-key". An
// unset variable or a rejected key ends the request with an AccountRouteErrors text that points to Settings > Custom providers; a key-less
// generation is never marked expired (there is no account). Every generation reads the store's routes copy as its models.json, so model
// overrides and custom headers apply, and a change of the file retires every generation for the next request. A built-in provider's entry
// in that copy holds only its modelOverrides (CustomProviderJson.RouteCopy), so the file never changes where a built-in account's key goes:
// that is the account's own baseUrl setting, or the catalog's. The variables a custom
// provider references are read when its generation is made; a request that finds one changed (or set, or unset) retires that generation, so
// the variable is read again on each request in effect (only a hash of the values is kept, in memory).
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.CustomProviders;

namespace PiSharp.WebAgent.Routing;

/// <summary>Routes a chat's model requests through the chat's own provider account. A singleton; safe from many threads.</summary>
public sealed class AccountRouter : IAsyncDisposable
{
    /// <summary>The <c>metadata.pisharp</c> field that names the account: <c>account</c>.</summary>
    public const string AccountField = "account";

    private readonly RuntimeModelCatalog catalog;
    private readonly IAccountStore accounts;
    private readonly ICredentialVault vault;
    private readonly CredentialsOptions credentials;
    private readonly RoutingOptions options;
    private readonly ILogger log;
    private readonly ICustomProviderSource? custom;
    private readonly ConcurrentDictionary<string, Lazy<Task<Generation>>> generations = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Lazy<Task<Generation>>> retired = new();
    private readonly ConcurrentDictionary<string, string> variableStamps = new(StringComparer.Ordinal);
    private int disposed;
    private readonly object lifecycle = new();
    private Task? close;
    private long customVersion;

    /// <summary>A router over the accounts of <paramref name="accounts"/> and their secrets in <paramref name="vault"/>, and the custom
    /// providers of <paramref name="custom"/> (null: none).</summary>
    public AccountRouter(RuntimeModelCatalog catalog, IAccountStore accounts, ICredentialVault vault, CredentialsOptions credentials,
        RoutingOptions options, ILogger<AccountRouter>? log = null, ICustomProviderSource? custom = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(accounts); ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(credentials); ArgumentNullException.ThrowIfNull(options);
        this.catalog = catalog; this.accounts = accounts; this.vault = vault; this.credentials = credentials; this.options = options;
        this.log = (ILogger?)log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        this.custom = custom;
        accounts.Changed += OnChanged;
    }

    /// <summary>The provider handler (<see cref="RoutingOptions.ProviderHandler"/>), or null for the routes' own.</summary>
    internal HttpMessageHandler? ProviderHandler => options.ProviderHandler;

    /// <summary>The routing clock.</summary>
    internal TimeProvider Time => options.Time ?? credentials.Time;

    /// <summary>
    /// The HostTransport of <see cref="NativeDurableModelsOptions"/>: the transport of <paramref name="model"/> through the account that
    /// <paramref name="streamOptions"/>' <c>metadata.pisharp.account</c> names: its main route, or its summary route when the request asks
    /// for another output cap or no caching. Never null: a request that cannot be routed throws, and NativeDurableModels ends the stream
    /// with the exception's message as its error.
    /// </summary>
    /// <exception cref="AccountRouteException">The account cannot be used (an <see cref="AccountRouteErrors"/> text).</exception>
    /// <exception cref="InvalidOperationException">The model is not in the catalog.</exception>
    public async ValueTask<IChatTransport?> RouteAsync(DurableModel model, DurableStreamOptions? streamOptions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var accountId = SessionStreamMetadata.Field(streamOptions, AccountField);
        streamOptions = SessionStreamMetadata.Strip(streamOptions);
        var row = catalog.Find(model.Provider, model.Id)
            ?? throw new InvalidOperationException($"The model {model.Provider}/{model.Id} is not in the Web Agent's catalog, so the request was not sent.");
        var providerName = catalog.DisplayName(model.Provider);
        RetireOnCustomChange();
        var customProvider = catalog.Custom(model.Provider);
        var account = customProvider is null ? Admit(accountId, model.Provider, providerName) : CustomAccount(customProvider, providerName);
        var generationKey = account is not null ? account.Id : KeylessKey + customProvider!.Id;
        if (customProvider is { Variables.Count: > 0 }) RetireOnVariableChange(generationKey, customProvider);
        Generation generation;
        try
        {
            generation = account is not null
                ? await GenerationAsync(generationKey, () => CreateAsync(account, customProvider), cancellationToken).ConfigureAwait(false)
                : await GenerationAsync(generationKey, () => CreateKeylessAsync(customProvider!), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is CredentialUnavailableException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Account {Account} could not be read for {Provider}/{Model}.", account?.Id ?? "(none)", model.Provider, model.Id);
            throw new AccountRouteException(AccountRouteErrors.Unavailable, Describe(AccountRouteErrors.Unavailable, providerName, customProvider));
        }
        var isolated = streamOptions?.ThinkingBudgets is not null || streamOptions?.WebsocketConnectTimeoutMs is not null;
        var route = generation.Route(row.Entry, model, isolated ? streamOptions : null);
        IChatTransport transport = route;
        if (streamOptions?.MaxTokens is not null || streamOptions?.CacheRetention == "none")
        {
            var maximum = streamOptions?.MaxTokens is { } cap && double.IsFinite(cap) && cap >= 1 ? (int)Math.Min(cap, int.MaxValue)
                : (int)Math.Clamp(model.MaxTokens, 1, int.MaxValue);
            transport = route.Summary(maximum, streamOptions?.Reasoning);
        }
        if (isolated) transport = new OwnedRequestTransport(generation, route, transport);
        return new AccountTransport(this, generation, transport, providerName);
    }

    /// <summary>The account a request may use, or the reason it may not.</summary>
    private AccountInfo Admit(string? accountId, string provider, string providerName)
    {
        string? reason = null;
        AccountInfo? account = null;
        if (string.IsNullOrEmpty(accountId)) reason = AccountRouteErrors.Unbound;
        else if ((account = accounts.Get(accountId)) is null) reason = AccountRouteErrors.Removed;
        else if (account.Provider != provider) reason = AccountRouteErrors.Mismatch;
        else if (account.Status == AccountStatus.Expired) reason = AccountRouteErrors.Expired;
        else if (account.Status == AccountStatus.Unavailable) reason = AccountRouteErrors.Unavailable;
        else if (account.Kind == AccountKind.OAuth && !WebCredentialSource.SignInProviders.Contains(provider)) reason = AccountRouteErrors.Unavailable;
        if (reason is null) return account!;
        log.LogInformation("A {Provider} request was not sent: account {Account} is {Reason}.", provider, accountId ?? "(none)", reason);
        throw new AccountRouteException(reason, AccountRouteErrors.Message(reason, providerName));
    }

    /// <summary>The generation prefix of a custom provider routed without an account.</summary>
    private const string KeylessKey = "custom:";

    /// <summary>The vault account a custom provider's requests use (its active API key account), or null when it has none (the file's
    /// reference, or no key); the reason it cannot be used otherwise.</summary>
    private AccountInfo? CustomAccount(CustomProviderInfo provider, string providerName)
    {
        string? reason = null;
        if (provider.FileKey == CustomKeySource.Environment)
        {
            if (provider.KeyVariable is { } variable && string.IsNullOrEmpty(credentials.Environment(variable))) reason = AccountRouteErrors.Unavailable;
        }
        else if (accounts.Active(provider.Id) is { Kind: AccountKind.ApiKey } stored)
        {
            if (stored.Status == AccountStatus.Expired) reason = AccountRouteErrors.Expired;
            else if (stored.Status == AccountStatus.Unavailable) reason = AccountRouteErrors.Unavailable;
            else return stored;
        }
        if (reason is null) return null;
        log.LogInformation("A {Provider} request was not sent: its key is {Reason}.", provider.Id, reason);
        throw new AccountRouteException(reason, Describe(reason, providerName, provider));
    }

    /// <summary>Retires every generation when the custom providers' file changed (a new routes copy), so the next request reads it.</summary>
    private void RetireOnCustomChange()
    {
        if (custom is null) return;
        var version = catalog.CustomVersion;
        if (Interlocked.Exchange(ref customVersion, version) == version) return;
        lock (lifecycle)
            foreach (var key in generations.Keys)
                if (generations.TryRemove(key, out var old)) retired.Enqueue(old);
    }

    /// <summary>Retires the generation <paramref name="key"/> when a variable that <paramref name="provider"/> references changed since the
    /// last request, so the next generation reads it again. Only a SHA-256 hash of the values is kept.</summary>
    private void RetireOnVariableChange(string key, CustomProviderInfo provider)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in provider.Variables)
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\u0001" + (credentials.Environment(name) is { } value ? "=" + value : "\u0002") + "\u0000"));
        var stamp = Convert.ToHexString(hash.GetHashAndReset());
        var previous = variableStamps.GetValueOrDefault(key);
        if (previous == stamp) return;
        variableStamps[key] = stamp;
        lock (lifecycle)
            if (previous is not null && generations.TryRemove(key, out var stale)) retired.Enqueue(stale);
    }

    private async Task<Generation> GenerationAsync(string key, Func<Task<Generation>> create, CancellationToken cancellationToken)
    {
        Lazy<Task<Generation>> lazy;
        Task<Generation> creation;
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            lazy = generations.GetOrAdd(key, _ => new Lazy<Task<Generation>>(create, LazyThreadSafetyMode.ExecutionAndPublication));
            // Starting creation and admitting its owner are atomic with the close snapshot.
            creation = lazy.Value;
        }
        try { return await creation.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (creation.IsFaulted || creation.IsCanceled)
        {
            // A failed generation is not kept: the next request tries again (the account may be fixed meanwhile).
            lock (lifecycle) generations.TryRemove(KeyValuePair.Create(key, lazy));
            throw;
        }
    }

    /// <summary>The account's runtime: its credential source and environment (and, for a custom provider, the variables its headers
    /// reference), the routing HTTP handler, the credentials area's sign-in client, flows and clock, and a registry of the built-in catalog
    /// and the routes copy of models.json with the account's stored entry (and base URL setting).</summary>
    private async Task<Generation> CreateAsync(AccountInfo account, CustomProviderInfo? customProvider)
    {
        var source = new WebCredentialSource(account, vault, accounts, log);
        var environment = AccountEnvironment.For(account, credentials.Environment);
        if (customProvider is { Variables.Count: > 0 })
        {
            var own = environment;
            var variables = customProvider.Variables.ToHashSet(StringComparer.Ordinal);
            environment = name => variables.Contains(name) ? credentials.Environment(name) : own(name);
        }
        var handlers = new ProviderHandlerOwner(options);
        try
        {
            var runtime = Runtime(environment, source, account.Provider, BaseUrl(account), handlers);
            var registry = await runtime.CreateModelRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            log.LogDebug("Account {Account} ({Provider}) has a new route generation.", account.Id, account.Provider);
            return new Generation(account.Id, account.Provider, account, source, runtime, registry, customProvider, handlers);
        }
        catch { handlers.Dispose(); throw; }
    }

    /// <summary>A custom provider's runtime without an account: the file's $VAR reference (from a host environment that shows only the
    /// variables the provider references, and non-secret configuration), or the placeholder key when it has none.</summary>
    private async Task<Generation> CreateKeylessAsync(CustomProviderInfo provider)
    {
        var source = new CustomKeyCredentialSource(provider.Id, provider.FileKey == CustomKeySource.Environment ? null : CustomProviderJson.PlaceholderKey);
        var variables = provider.Variables.ToHashSet(StringComparer.Ordinal);
        var handlers = new ProviderHandlerOwner(options);
        try
        {
            var runtime = Runtime(name => variables.Contains(name) || AccountEnvironment.Configuration.Contains(name) ? credentials.Environment(name) : null,
                source, provider.Id, null, handlers);
            var registry = await runtime.CreateModelRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            log.LogDebug("Custom provider {Provider} has a new route generation.", provider.Id);
            return new Generation(KeylessKey + provider.Id, provider.Id, null, null, runtime, registry, provider, handlers);
        }
        catch { handlers.Dispose(); throw; }
    }

    private LiveRouteRuntime Runtime(Func<string, string?> environment, PiSharp.Cli.Authentication.IProviderCredentialSource source, string provider, string? baseUrl, ProviderHandlerOwner handlers) =>
        new(environment, handlers.Get, credentials.CreateHttp, Time, ModelsPath: custom?.Current.RoutesPath)
        {
            Credentials = () => source,
            OAuthFlows = (id, context) => WebCredentialSource.SignInProviders.Contains(id)
                ? SignInFlows.CreateFlow(id, context.Http, credentials, model => catalog.Find("github-copilot", model) is not null)
                : throw new InvalidOperationException($"{id} does not sign in in the Web Agent."),
            // A run whose request the capture hook must change or record (RunHttpContext.NeedsHttp: a patch, strict tools, a cache edit, an
            // API version, retries, a timeout, or raw payloads) goes over SSE: the hook cannot reach a WebSocket frame, and its options would
            // otherwise be dropped. Read per request, in the run's async flow.
            Transport = () => (Runs.RunHttpScope.Current is { NeedsHttp: true } || NativeDurableModels.HasActiveProviderPayloadHook) ? "sse" : options.CodexTransport,
            HomeDirectory = credentials.Home,
            CreateCatalogClient = () => new HttpClient(new OfflineCatalogHandler()),
            ConfigureRegistry = baseUrl is not null
                ? registry => registry.RegisterExtensionProvider(provider, new System.Text.Json.Nodes.JsonObject { ["baseUrl"] = baseUrl })
                : null,
        };

    /// <summary>The <c>baseUrl</c> setting of an API key account (an http or https URL), or null.</summary>
    internal static string? BaseUrl(AccountInfo account) =>
        account.Kind == AccountKind.ApiKey && account.Settings.TryGetValue("baseUrl", out var value) &&
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? value.TrimEnd('/') : null;

    private void OnChanged(object? sender, AccountChangedEventArgs change)
    {
        if (change.Change is not (AccountChange.CredentialReplaced or AccountChange.Removed or AccountChange.StatusChanged)) return;
        // Requests already streaming keep their generation; it is closed with the router.
        lock (lifecycle)
            if (generations.TryRemove(change.AccountId, out var old)) retired.Enqueue(old);
    }

    // ─── Errors ─────────────────────────────────────────────────────────────

    /// <summary>A provider's error terminal, reviewed: an account error, a retryable refresh failure, or the provider's own text with the
    /// account's secrets redacted.</summary>
    private async Task<StreamError> ReviewAsync(Generation generation, StreamError error, int failuresBefore, string providerName)
    {
        var original = ErrorText(error.Message);
        var reason = await ClassifyAsync(generation, original, error.NativeSourceException, failuresBefore).ConfigureAwait(false);
        var text = reason is null ? await RedactAsync(generation, original).ConfigureAwait(false) : Describe(reason, providerName, generation.Custom);
        if (text is null || text == original) return error;
        Report(generation, reason);
        return error with { Message = WithError(error.Message, text) };
    }

    /// <summary>An exception from the route before any event, reviewed: an account error (or the route's own refusal, such as a Copilot
    /// model the account cannot use) as an error terminal; null to let the exception through.</summary>
    private async Task<StreamError?> ReviewAsync(Generation generation, ChatRequest request, Exception error, int failuresBefore, string providerName)
    {
        var reason = await ClassifyAsync(generation, error.Message, error, failuresBefore).ConfigureAwait(false);
        var text = reason is not null ? Describe(reason, providerName, generation.Custom) : error is LiveSessionException refused ? refused.Message : null;
        if (text is null) return null;
        Report(generation, reason ?? "refused");
        var model = request.Model;
        return new StreamError(StopReason.Error, new AssistantMessage(model.Api, model.Provider, model.Id, request.Timestamp, [], TokenUsage.Zero,
            StopReason.Error, ErrorFields(null, text)));
    }

    private void Report(Generation generation, string? reason) =>
        log.LogWarning("A {Provider} request with account {Account} failed: {Reason}.", generation.Provider, generation.Key, reason ?? "provider error");

    private const string Network = "network";

    private static string Describe(string reason, string providerName, CustomProviderInfo? customProvider) =>
        reason == Network ? AccountRouteErrors.RefreshNetwork(providerName)
        : customProvider is not null ? AccountRouteErrors.CustomMessage(reason, providerName, customProvider.FileKey == CustomKeySource.Environment ? customProvider.KeyVariable : null)
        : AccountRouteErrors.Message(reason, providerName);

    /// <summary>Why a failed request failed, when it is the account's fault: it was removed, expired or became unavailable meanwhile (a
    /// rejected refresh marks it expired), a refresh failed on the network, the route found no credential, or the provider answered 401
    /// (which marks it expired). Null for anything else.</summary>
    private async Task<string?> ClassifyAsync(Generation generation, string? text, Exception? error, int failuresBefore)
    {
        if (generation.Account is null)
        {
            // A custom provider without an account: nothing to mark; its key is the file's reference (or none).
            if (IsMissingCredential(text, error)) return AccountRouteErrors.Unavailable;
            return IsUnauthorized(text, error) ? AccountRouteErrors.Expired : null;
        }
        var id = generation.Account.Id;
        var account = accounts.Get(id);
        if (account is null) return AccountRouteErrors.Removed;
        if (account.Status == AccountStatus.Expired) return AccountRouteErrors.Expired;
        if (account.Status == AccountStatus.Unavailable) return AccountRouteErrors.Unavailable;
        if (generation.Source is { } source && source.Failures != failuresBefore && source.LastFailureTransient) return Network;
        if (IsMissingCredential(text, error)) return AccountRouteErrors.Unavailable;
        if (IsUnauthorized(text, error))
        {
            try { await accounts.MarkExpiredAsync(id, "The provider rejected the credential (401); replace the key or sign in again.", CancellationToken.None).ConfigureAwait(false); }
            catch (KeyNotFoundException) { return AccountRouteErrors.Removed; }
            return AccountRouteErrors.Expired;
        }
        return null;
    }

    private static bool IsMissingCredential(string? text, Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case CredentialUnavailableException:
                case OAuthLifecycleException:
                case LiveSessionException { Code: "MissingLiveApiKey" or "LiveAuthenticationFailed" }:
                    return true;
            }
        }
        return text is not null && (text.StartsWith("Provider is not configured: ", StringComparison.Ordinal) ||
            text.StartsWith("No API key for provider: ", StringComparison.Ordinal) || text.StartsWith("No API key found for ", StringComparison.Ordinal));
    }

    private static bool IsUnauthorized(string? text, Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
            if (current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }) return true;
        // The providers' texts: "401 <message>" (shared error text, Pi messages), "OpenAI API error (401): ..." (Responses, Mistral),
        // "... failed with HTTP 401." (Google), "... with status 401" / "status code 401". (OpenAI Codex's text carries no status.)
        return text is not null && (text == "401" || text.StartsWith("401 ", StringComparison.Ordinal) ||
            text.Contains("API error (401)", StringComparison.Ordinal) || text.Contains("HTTP 401", StringComparison.Ordinal) ||
            text.Contains("status 401", StringComparison.Ordinal) || text.Contains("status code 401", StringComparison.Ordinal));
    }

    /// <summary><paramref name="text"/> with the account's key and tokens (or a custom provider's referenced variables) replaced by
    /// <c>[redacted]</c>; the same text when none occurs.</summary>
    private async Task<string?> RedactAsync(Generation generation, string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var variable in generation.Custom?.Variables ?? [])
            if (credentials.Environment(variable) is { Length: >= 8 } value && text.Contains(value, StringComparison.Ordinal)) text = text.Replace(value, "[redacted]", StringComparison.Ordinal);
        if (generation.Account is not { } account) return text;
        AccountCredential? credential;
        try { credential = await vault.ReadAsync(account.Id).ConfigureAwait(false); }
        catch (Exception error) when (error is CredentialUnavailableException or InvalidDataException or IOException) { return text; }
        foreach (var secret in new[] { credential?.ApiKey, credential?.OAuth?.Access, credential?.OAuth?.Refresh })
            if (secret is { Length: >= 8 } && text.Contains(secret, StringComparison.Ordinal)) text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }

    /// <summary>The <c>errorMessage</c> of an assistant message, or null.</summary>
    internal static string? ErrorText(AssistantMessage message) =>
        message.ExtraProperties is { } extra && extra.TryGet("errorMessage", out var value) && value is { Value.ValueKind: JsonValueKind.String }
            ? value.Value.GetString() : null;

    private static JsonFields ErrorFields(AssistantMessage? message, string text) =>
        (message?.ExtraProperties ?? JsonFields.Empty).Set("errorMessage", JsonData.Parse(JsonSerializer.Serialize(text)));

    private static AssistantMessage WithError(AssistantMessage message, string text) => message with { ExtraProperties = ErrorFields(message, text) };

    /// <summary>Closes every route the router opened (the provider handler stays the caller's).</summary>
    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            if (close is null)
            {
                Volatile.Write(ref disposed, 1);
                accounts.Changed -= OnChanged;
                var all = generations.Values.Concat(retired).ToArray();
                generations.Clear(); retired.Clear();
                close = CloseAsync(all);
            }
            return new(close);
        }
    }

    private async Task CloseAsync(IEnumerable<Lazy<Task<Generation>>> all)
    {
        foreach (var lazy in all)
        {
            try { await (await lazy.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { log.LogDebug(error, "Closing a route generation failed."); }
        }
    }

    /// <summary>One account's (or one key-less custom provider's) runtime, registry and model routes, as of one credential version.</summary>
    private sealed class Generation(string key, string provider, AccountInfo? account, WebCredentialSource? source, LiveRouteRuntime runtime,
        ModelRegistry registry, CustomProviderInfo? customProvider, ProviderHandlerOwner handlers) : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<(string Provider, string Id), LazyModelTransport> routes = new();
        private readonly HashSet<LazyModelTransport> isolatedRoutes = [];
        private readonly object gate = new();
        private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active;
        private Task? close;

        /// <summary>The generation's key: the account id, or <c>custom:</c> and the provider id.</summary>
        public string Key { get; } = key;

        public string Provider { get; } = provider;

        /// <summary>The account, or null for a custom provider routed without one.</summary>
        public AccountInfo? Account { get; } = account;

        public WebCredentialSource? Source { get; } = source;

        /// <summary>The custom provider, or null for a built-in provider's account.</summary>
        public CustomProviderInfo? Custom { get; } = customProvider;

        public int Failures => Source?.Failures ?? 0;

        public LazyModelTransport Route(RegistryModel entry, DurableModel model, DurableStreamOptions? requestOptions)
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(close is not null, this);
                if (requestOptions is null)
                    return routes.GetOrAdd((model.Provider, model.Id), _ => new LazyModelTransport(entry, model.Descriptor, () => registry, runtime));
                var requestRuntime = runtime with
                {
                    ThinkingBudgets = requestOptions.ThinkingBudgets?.ToData(),
                    WebSocketConnectTimeout = requestOptions.WebsocketConnectTimeoutMs is { } timeout
                        ? TimeSpan.FromMilliseconds(timeout) : runtime.WebSocketConnectTimeout,
                };
                var isolated = new LazyModelTransport(entry, model.Descriptor, () => registry, requestRuntime);
                isolatedRoutes.Add(isolated);
                return isolated;
            }
        }

        public IDisposable BeginStream()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(close is not null, this);
                active++;
                return new ActiveStream(this);
            }
        }

        private sealed class ActiveStream(Generation owner) : IDisposable
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                lock (owner.gate)
                    if (--owner.active == 0 && owner.close is not null) owner.drained.TrySetResult();
            }
        }

        public async ValueTask ReleaseRouteAsync(LazyModelTransport route)
        {
            try { await route.DisposeAsync().ConfigureAwait(false); }
            finally { lock (gate) isolatedRoutes.Remove(route); }
        }

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (close is null)
                {
                    if (active == 0) drained.TrySetResult();
                    close = CloseAsync();
                }
                return new(close);
            }
        }

        private async Task CloseAsync()
        {
            await drained.Task.ConfigureAwait(false);
            var failures = new List<Exception>();
            foreach (var route in routes.Values.Concat(isolatedRoutes))
                try { await route.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
            routes.Clear(); isolatedRoutes.Clear();
            try { handlers.Dispose(); }
            catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }

    /// <summary>The per-request route keeps its thinking capabilities and is released when its stream settles.</summary>
    private sealed class OwnedRequestTransport(Generation generation, LazyModelTransport owner, IChatTransport inner)
        : IChatTransport, IThinkingLevelTransport
    {
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) =>
            inner is IThinkingLevelTransport thinking ? thinking.GetSupportedThinkingLevels(model) : owner.GetSupportedThinkingLevels(model);

        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await foreach (var item in inner.StreamAsync(request, cancellationToken).ConfigureAwait(false)) yield return item;
            }
            finally { await generation.ReleaseRouteAsync(owner).ConfigureAwait(false); }
        }
    }

    /// <summary>One lazily created HTTP pool per generation. Wrappers are owned; an injected handler is borrowed.</summary>
    private sealed class ProviderHandlerOwner(RoutingOptions options) : IDisposable
    {
        private readonly object gate = new();
        private HttpMessageHandler? handler;
        private bool disposed;

        public HttpMessageHandler Get()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return handler ??= NativeDurableModels.WrapProviderHandler(options.ProviderHandler is { } borrowed
                    ? new BorrowedHandler(borrowed)
                    : options.CreateDefaultProviderHandler?.Invoke() ?? new SocketsHttpHandler { AllowAutoRedirect = false });
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                handler?.Dispose();
            }
        }

        private sealed class BorrowedHandler(HttpMessageHandler inner) : HttpMessageHandler
        {
            private readonly HttpMessageInvoker invoker = new(inner, disposeHandler: false);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                invoker.SendAsync(request, cancellationToken);
            protected override void Dispose(bool disposing)
            {
                if (disposing) invoker.Dispose();
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>A route's transport for one account: errors are reviewed (<see cref="ReviewAsync(Generation, StreamError, int, string)"/>);
    /// everything else passes through.</summary>
    private sealed class AccountTransport(AccountRouter router, Generation generation, IChatTransport inner, string providerName)
        : IChatTransport, IThinkingLevelTransport
    {
        public ImmutableArray<string> GetSupportedThinkingLevels(ModelDescriptor model) =>
            inner is IThinkingLevelTransport levels ? levels.GetSupportedThinkingLevels(model) : ["off"];

        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var active = generation.BeginStream();
            var failures = generation.Failures;
            var yielded = false;
            Exception? thrown = null;
            IAsyncEnumerator<StreamEvent>? events = null;
            try
            {
                try { events = inner.StreamAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken); }
                catch (Exception error) when (!IsCancellation(error, cancellationToken)) { thrown = error; }
                while (events is not null)
                {
                    StreamEvent? current = null;
                    try { if (await events.MoveNextAsync().ConfigureAwait(false)) current = events.Current; }
                    catch (Exception error) when (!IsCancellation(error, cancellationToken)) { thrown = error; }
                    if (current is null) break;
                    if (current is StreamError { Reason: StopReason.Error } failed)
                    {
                        yield return await router.ReviewAsync(generation, failed, failures, providerName).ConfigureAwait(false);
                        yield break;
                    }
                    yielded = true;
                    yield return current;
                }
            }
            finally
            {
                if (events is not null) await events.DisposeAsync().ConfigureAwait(false);
            }
            if (thrown is null) yield break;
            // Only a failure before any event becomes an error terminal; a failure mid-stream is the stream's (ChatRun settles it).
            var converted = yielded ? null : await router.ReviewAsync(generation, request, thrown, failures, providerName).ConfigureAwait(false);
            if (converted is null) ExceptionDispatchInfo.Throw(thrown);
            yield return converted;
        }

        private static bool IsCancellation(Exception error, CancellationToken cancellationToken) =>
            error is OperationCanceledException && cancellationToken.IsCancellationRequested;
    }

    /// <summary>The catalog client of a route registry: catalogs are never refreshed over the network here.</summary>
    private sealed class OfflineCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The Web Agent does not refresh model catalogs over the network.");
    }

    /// <summary>The credential of a custom provider routed without an account: the placeholder key, or nothing (the file's reference is
    /// the registry's then). It never reads the vault and holds no secret.</summary>
    private sealed class CustomKeyCredentialSource(string provider, string? placeholder) : PiSharp.Cli.Authentication.IProviderCredentialSource
    {
        public Task<PiSharp.Cli.Authentication.StoredCredentialEntry?> ReadEntryAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(id == provider && placeholder is not null ? new PiSharp.Cli.Authentication.StoredCredentialEntry("api_key", placeholder, null) : null);

        public Task<IReadOnlyList<(string Provider, string Type)>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(string Provider, string Type)>>(placeholder is null ? [] : [(provider, "api_key")]);

        public Task<OAuthCredentialSnapshot?> ReadAsync(string id, CancellationToken cancellationToken) => Task.FromResult<OAuthCredentialSnapshot?>(null);

        public Task<OAuthCredentialSnapshot?> ModifyAsync(string id, Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation,
            CancellationToken cancellationToken) => Task.FromResult<OAuthCredentialSnapshot?>(null);
    }
}
