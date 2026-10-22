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
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.WebAgent.Credentials;

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
    private readonly ConcurrentDictionary<string, Lazy<Task<Generation>>> generations = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Lazy<Task<Generation>>> retired = new();
    private int disposed;

    /// <summary>A router over the accounts of <paramref name="accounts"/> and their secrets in <paramref name="vault"/>.</summary>
    public AccountRouter(RuntimeModelCatalog catalog, IAccountStore accounts, ICredentialVault vault, CredentialsOptions credentials,
        RoutingOptions options, ILogger<AccountRouter>? log = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(accounts); ArgumentNullException.ThrowIfNull(vault);
        ArgumentNullException.ThrowIfNull(credentials); ArgumentNullException.ThrowIfNull(options);
        this.catalog = catalog; this.accounts = accounts; this.vault = vault; this.credentials = credentials; this.options = options;
        this.log = (ILogger?)log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
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
        var account = Admit(accountId, model.Provider, providerName);
        Generation generation;
        try { generation = await GenerationAsync(account, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is CredentialUnavailableException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Account {Account} could not be read for {Provider}/{Model}.", account.Id, model.Provider, model.Id);
            throw new AccountRouteException(AccountRouteErrors.Unavailable, AccountRouteErrors.Message(AccountRouteErrors.Unavailable, providerName));
        }
        var route = generation.Route(row.Entry, model);
        IChatTransport transport = route;
        if (streamOptions?.MaxTokens is not null || streamOptions?.CacheRetention == "none")
        {
            var maximum = streamOptions?.MaxTokens is { } cap && double.IsFinite(cap) && cap >= 1 ? (int)Math.Min(cap, int.MaxValue)
                : (int)Math.Clamp(model.MaxTokens, 1, int.MaxValue);
            transport = route.Summary(maximum, streamOptions?.Reasoning);
        }
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

    private async Task<Generation> GenerationAsync(AccountInfo account, CancellationToken cancellationToken)
    {
        var lazy = generations.GetOrAdd(account.Id, _ => new Lazy<Task<Generation>>(() => CreateAsync(account), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            // A failed generation is not kept: the next request tries again (the account may be fixed meanwhile).
            generations.TryRemove(KeyValuePair.Create(account.Id, lazy));
            throw;
        }
    }

    /// <summary>The account's runtime: its credential source and environment, the routing HTTP handler, the credentials area's sign-in
    /// client, flows and clock, and a registry of the built-in catalog with the account's stored entry (and base URL setting).</summary>
    private async Task<Generation> CreateAsync(AccountInfo account)
    {
        var source = new WebCredentialSource(account, vault, accounts, log);
        var runtime = new LiveRouteRuntime(AccountEnvironment.For(account, credentials.Environment), () => options.ProviderHandler, credentials.CreateHttp,
            Time, ModelsPath: null)
        {
            Credentials = () => source,
            OAuthFlows = (provider, context) => WebCredentialSource.SignInProviders.Contains(provider)
                ? SignInFlows.CreateFlow(provider, context.Http, credentials, id => catalog.Find("github-copilot", id) is not null)
                : throw new InvalidOperationException($"{provider} does not sign in in the Web Agent."),
            Transport = () => options.CodexTransport,
            HomeDirectory = credentials.Home,
            CreateCatalogClient = () => new HttpClient(new OfflineCatalogHandler()),
            ConfigureRegistry = BaseUrl(account) is { } baseUrl
                ? registry => registry.RegisterExtensionProvider(account.Provider, new System.Text.Json.Nodes.JsonObject { ["baseUrl"] = baseUrl })
                : null,
        };
        var registry = await runtime.CreateModelRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        log.LogDebug("Account {Account} ({Provider}) has a new route generation.", account.Id, account.Provider);
        return new Generation(account, source, runtime, registry);
    }

    /// <summary>The <c>baseUrl</c> setting of an API key account (an http or https URL), or null.</summary>
    internal static string? BaseUrl(AccountInfo account) =>
        account.Kind == AccountKind.ApiKey && account.Settings.TryGetValue("baseUrl", out var value) &&
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? value.TrimEnd('/') : null;

    private void OnChanged(object? sender, AccountChangedEventArgs change)
    {
        if (change.Change is not (AccountChange.CredentialReplaced or AccountChange.Removed or AccountChange.StatusChanged)) return;
        // Requests already streaming keep their generation; it is closed with the router.
        if (generations.TryRemove(change.AccountId, out var old)) retired.Enqueue(old);
    }

    // ─── Errors ─────────────────────────────────────────────────────────────

    /// <summary>A provider's error terminal, reviewed: an account error, a retryable refresh failure, or the provider's own text with the
    /// account's secrets redacted.</summary>
    private async Task<StreamError> ReviewAsync(Generation generation, StreamError error, int failuresBefore, string providerName)
    {
        var original = ErrorText(error.Message);
        var reason = await ClassifyAsync(generation, original, error.NativeSourceException, failuresBefore).ConfigureAwait(false);
        var text = reason is null ? await RedactAsync(generation.Account.Id, original).ConfigureAwait(false) : Describe(reason, providerName);
        if (text is null || text == original) return error;
        Report(generation, reason);
        return error with { Message = WithError(error.Message, text) };
    }

    /// <summary>An exception from the route before any event, reviewed: an account error (or the route's own refusal, such as a Copilot
    /// model the account cannot use) as an error terminal; null to let the exception through.</summary>
    private async Task<StreamError?> ReviewAsync(Generation generation, ChatRequest request, Exception error, int failuresBefore, string providerName)
    {
        var reason = await ClassifyAsync(generation, error.Message, error, failuresBefore).ConfigureAwait(false);
        var text = reason is not null ? Describe(reason, providerName) : error is LiveSessionException refused ? refused.Message : null;
        if (text is null) return null;
        Report(generation, reason ?? "refused");
        var model = request.Model;
        return new StreamError(StopReason.Error, new AssistantMessage(model.Api, model.Provider, model.Id, request.Timestamp, [], TokenUsage.Zero,
            StopReason.Error, ErrorFields(null, text)));
    }

    private void Report(Generation generation, string? reason) =>
        log.LogWarning("A {Provider} request with account {Account} failed: {Reason}.", generation.Account.Provider, generation.Account.Id, reason ?? "provider error");

    private const string Network = "network";

    private static string Describe(string reason, string providerName) =>
        reason == Network ? AccountRouteErrors.RefreshNetwork(providerName) : AccountRouteErrors.Message(reason, providerName);

    /// <summary>Why a failed request failed, when it is the account's fault: it was removed, expired or became unavailable meanwhile (a
    /// rejected refresh marks it expired), a refresh failed on the network, the route found no credential, or the provider answered 401
    /// (which marks it expired). Null for anything else.</summary>
    private async Task<string?> ClassifyAsync(Generation generation, string? text, Exception? error, int failuresBefore)
    {
        var id = generation.Account.Id;
        var account = accounts.Get(id);
        if (account is null) return AccountRouteErrors.Removed;
        if (account.Status == AccountStatus.Expired) return AccountRouteErrors.Expired;
        if (account.Status == AccountStatus.Unavailable) return AccountRouteErrors.Unavailable;
        if (generation.Source.Failures != failuresBefore && generation.Source.LastFailureTransient) return Network;
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

    /// <summary><paramref name="text"/> with the account's key and tokens replaced by <c>[redacted]</c>; the same text when none occurs.</summary>
    private async Task<string?> RedactAsync(string accountId, string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        AccountCredential? credential;
        try { credential = await vault.ReadAsync(accountId).ConfigureAwait(false); }
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
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        accounts.Changed -= OnChanged;
        var all = generations.Values.Concat(retired).ToList();
        generations.Clear();
        foreach (var lazy in all)
        {
            if (!lazy.IsValueCreated || !lazy.Value.IsCompletedSuccessfully) continue;
            try { await lazy.Value.Result.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { log.LogDebug(error, "Closing a route generation failed."); }
        }
    }

    // ─── Generations and transports ─────────────────────────────────────────

    /// <summary>One account's runtime, registry and model routes, as of one credential version.</summary>
    private sealed class Generation(AccountInfo account, WebCredentialSource source, LiveRouteRuntime runtime, ModelRegistry registry) : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<(string Provider, string Id), LazyModelTransport> routes = new();

        public AccountInfo Account { get; } = account;

        public WebCredentialSource Source { get; } = source;

        public LazyModelTransport Route(RegistryModel entry, DurableModel model) =>
            routes.GetOrAdd((model.Provider, model.Id), _ => new LazyModelTransport(entry, model.Descriptor, () => registry, runtime));

        public async ValueTask DisposeAsync()
        {
            foreach (var route in routes.Values) await route.DisposeAsync().ConfigureAwait(false);
            routes.Clear();
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
            var failures = generation.Source.Failures;
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
}
