using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Authentication;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.Protocols.AnthropicMessages;
using PiSharp.AI.Providers;
using PiSharp.Contracts;
using R = PiSharp.AI.Authentication.InjectedAuthenticationResolver;

// Authored offline expectations for the Pi v1.1.0 Anthropic auth sync (work package 2i). These are not
// upstream captures: fake token endpoints, synthetic identity tokens and loopback-only callback requests.
internal static class Program
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const long Epoch = 1_700_000_000_500;
    private static readonly string[] KeyNames = [R.AnthropicAuthToken, R.AnthropicOAuthToken, R.AnthropicApiKey];
    private static readonly string[] Required = [R.AnthropicFederationRuleId, R.AnthropicOrganizationId, R.AnthropicIdentityTokenFile];
    private static readonly string[] Optional = [R.AnthropicServiceAccountId, R.AnthropicWorkspaceId];
    private static int bodyComparisons;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new (string Id, Func<Task> Run)[]
        {
            ("federation.resolves-required-and-optional-env-as-provider-environment", FederationResolves),
            ("federation.service-account-and-workspace-are-optional", FederationOptional),
            ("federation.missing-or-empty-required-variable-is-missing", FederationPartial),
            ("federation.keys-auth-token-and-stored-key-keep-precedence", FederationPrecedence),
            ("federation.cancellation-between-federation-lookups", FederationCancellation),
            ("federation.configure-provider-and-request-auth-selection", FederationConfigure),
            ("federation.secretless-result-is-admitted-with-its-configuration-and-no-send", FederationAdapter),
            ("federation.resolved-provider-exchanges-once-and-sends-bearer-oauth-beta-messages", FederationResolvedProvider),
            ("federation.option-owned-authorization-header-wins-over-federation", FederationOptionHeaderWins),
            ("federation.exchange-request-body-headers-and-expiry", FederationExchange),
            ("federation.exchange-failures-are-redacted-and-bounded", FederationExchangeFailures),
            ("federation.token-cache-advisory-mandatory-invalidate-coalesce", FederationCache),
            ("federation.handler-exchanges-once-applies-bearer-and-beta-invalidates-on-401", FederationHandler),
            ("refresh.cancelled-caller-still-persists-rotated-token", RefreshPersists),
            ("refresh.cancellation-while-waiting-for-lane-skips-refresh", RefreshLaneWait),
            ("refresh.cancellation-before-callback-entry-skips-refresh", RefreshCallbackEntry),
            ("refresh.anthropic-refresh-body-persists-despite-cancellation", RefreshAnthropic),
            ("oauth.copy-code-login-select-prompt-url-and-exchange", CopyCodeLogin),
            ("oauth.copy-code-state-mismatch-and-missing-code", CopyCodeFailures),
            ("oauth.authorization-url-form-encoding", AuthorizationUrl),
            ("oauth.parse-authorization-input-forms", ParseInput),
            ("oauth.browser-manual-redirect-url-and-prompt-cancelled-after-settling", BrowserManual),
            ("oauth.browser-callback-completes-and-shows-sign-in-page", BrowserCallback),
            ("oauth.callback-port-falls-back-when-preferred-port-busy", BrowserFallback),
            ("oauth.default-port-53692-falls-back-when-busy", BrowserDefaultPortFallback),
            ("oauth.callback-server-routes-state-errors-and-claims", CallbackServer),
            ("oauth.method-selection-cancel-and-unknown-method", MethodSelection),
            ("oauth.token-endpoint-failure-diagnostics-omit-body", TokenEndpointFailures),
        };
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(30)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { sourceSha = "abe508e1b89912adde45528136c3221eb69acdd7", status = "AUTHORED NATIVE; SOURCE QUALIFICATION OPEN",
            tests = cases.Length, failures, bodyComparisons, genuineSourceCasesCaptured = 0, results };
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true });
        }
        else Console.WriteLine(JsonSerializer.Serialize(report));
        return failures == 0 ? 0 : 1;
    }

    // ---- shared fakes ----
    private static void Require(bool value, string message = "Authored assertion failed.") { if (!value) throw new InvalidOperationException(message); }
    private static void Equal(string? expected, string? actual) => Require(expected == actual, $"Expected <{expected}> but was <{actual}>.");
    private static void BodyEqual(string expected, string? actual) { bodyComparisons++; Equal(expected, actual); }
    private static async Task<T> Throws<T>(Func<Task> run) where T : Exception
    {
        try { await run(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ProviderEnvironmentSnapshot Env(params (string Name, string? Value)[] values) =>
        new(values.Select(value => KeyValuePair.Create(value.Name, value.Value)));
    private static (string, string?)[] FederationVariables(bool optional = true) => optional
        ? [(R.AnthropicFederationRuleId, "fdrl_test"), (R.AnthropicOrganizationId, "org-test"), (R.AnthropicServiceAccountId, "svac_test"),
           (R.AnthropicIdentityTokenFile, "/tmp/identity.jwt"), (R.AnthropicWorkspaceId, "wrkspc_test")]
        : [(R.AnthropicFederationRuleId, "fdrl_test"), (R.AnthropicOrganizationId, "org-test"), (R.AnthropicIdentityTokenFile, "/tmp/identity.jwt")];
    private static ValueTask<StoredApiKeyCredential?> NoCredential(string _, CancellationToken __) => ValueTask.FromResult<StoredApiKeyCredential?>(null);

    private sealed class Lookup(ProviderEnvironmentSnapshot values, Action<string>? onRead = null) : IInjectedEnvironmentLookup
    {
        public List<string> Names { get; } = [];
        public ValueTask<string?> ReadAsync(string name, CancellationToken cancellationToken)
        { Names.Add(name); onRead?.Invoke(name); return ValueTask.FromResult(values.GetValue(name)); }
    }

    private sealed class Clock(long milliseconds) : TimeProvider
    {
        public long Milliseconds = milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref Milliseconds));
    }

    private sealed record Seen(string Method, string Url, string? Body, Dictionary<string, string> Headers, string? ContentType);
    private sealed class Fake(Func<Seen, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.NonValidated.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var seen = new Seen(request.Method.Method, request.RequestUri!.AbsoluteUri, body, headers, request.Content?.Headers.ContentType?.ToString());
            lock (Requests) Requests.Add(seen);
            return await respond(seen, cancellationToken);
        }
    }
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK, string? requestId = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (requestId is not null) response.Headers.TryAddWithoutValidation("Request-Id", requestId);
        return response;
    }
    private static Fake Respond(string body, HttpStatusCode status = HttpStatusCode.OK, string? requestId = null) =>
        new((_, _) => Task.FromResult(Json(body, status, requestId)));

    private sealed class Source : IAdmittedOAuthCredentialSource
    {
        private readonly object gate = new();
        private OAuthCredentialSnapshot? value;
        public SemaphoreSlim Lane { get; } = new(1, 1);
        public int Writes { get; private set; }
        public Func<Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>>, CancellationToken, Task<OAuthCredentialSnapshot?>>? ModifyOverride { get; set; }
        public void Seed(OAuthCredentialSnapshot seeded) { lock (gate) value = seeded; }
        public OAuthCredentialSnapshot? Current { get { lock (gate) return value; } }
        public Task<OAuthCredentialSnapshot?> ReadAsync(string provider, CancellationToken cancellationToken) => Task.FromResult(Current);
        public Task<OAuthCredentialSnapshot?> ModifyAsync(string provider,
            Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation, CancellationToken cancellationToken) =>
            ModifyOverride is { } modify ? modify(mutation, cancellationToken) : ModifyCoreAsync(mutation, cancellationToken);
        public async Task<OAuthCredentialSnapshot?> ModifyCoreAsync(
            Func<OAuthCredentialSnapshot?, CancellationToken, Task<OAuthCredentialSnapshot?>> mutation, CancellationToken cancellationToken)
        {
            await Lane.WaitAsync(cancellationToken);
            try
            {
                var next = await mutation(Current, cancellationToken);
                // Publish only while the supplied token is live (IAdmittedOAuthCredentialSource contract).
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate) { if (next is not null) { value = next; Writes++; } return value; }
            }
            finally { Lane.Release(); }
        }
    }
    private sealed class Refresh(Func<OAuthCredentialSnapshot, CancellationToken, Task<OAuthCredentialSnapshot>> run) : IAdmittedOAuthRefresh
    {
        public int Calls;
        public Task<OAuthCredentialSnapshot> RefreshAsync(string provider, OAuthCredentialSnapshot current, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return run(current, cancellationToken); }
    }

    private sealed class Interaction : IAnthropicOAuthLoginInteraction
    {
        public Func<string, IReadOnlyList<AnthropicOAuthLoginOption>, CancellationToken, Task<string>> Select { get; init; } = (_, _, _) => Task.FromResult("browser");
        public Func<string, string, CancellationToken, Task<string>> Manual { get; init; } = (_, _, token) => Pending(token);
        public Action<AnthropicOAuthLoginEvent>? OnNotify { get; init; }
        public List<AnthropicOAuthLoginEvent> Events { get; } = [];
        public List<(string Message, IReadOnlyList<AnthropicOAuthLoginOption> Options)> Selects { get; } = [];
        public List<(string Message, string Placeholder, CancellationToken Token)> Prompts { get; } = [];
        public string AuthUrl => Events.Last(item => item.Kind == AnthropicOAuthLoginEventKind.AuthUrl).Url!;
        public void Notify(AnthropicOAuthLoginEvent loginEvent) { Events.Add(loginEvent); OnNotify?.Invoke(loginEvent); }
        public Task<string> SelectAsync(string message, IReadOnlyList<AnthropicOAuthLoginOption> options, CancellationToken cancellationToken)
        { Selects.Add((message, options)); return Select(message, options, cancellationToken); }
        public Task<string> PromptManualCodeAsync(string message, string placeholder, CancellationToken cancellationToken)
        { Prompts.Add((message, placeholder, cancellationToken)); return Manual(message, placeholder, cancellationToken); }
    }
    private static Task<string> Pending(CancellationToken token)
    {
        var pending = Signal<string>();
        token.Register(() => pending.TrySetException(new OperationCanceledException("aborted", token)));
        return pending.Task;
    }
    private static string? Param(string url, string name)
    {
        var query = url[(url.IndexOf('?') + 1)..];
        foreach (var pair in query.Split('&'))
        {
            var equals = pair.IndexOf('=');
            if (Uri.UnescapeDataString(pair[..equals].Replace('+', ' ')) == name) return Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
        }
        return null;
    }
    private static Fake TokenEndpoint(Action<Seen>? observe = null) => new((seen, _) =>
    {
        Require(seen.Url == "https://platform.claude.com/v1/oauth/token" && seen.Method == "POST", "Unexpected token endpoint request.");
        observe?.Invoke(seen);
        return Task.FromResult(Json("""{"access_token":"access-token","refresh_token":"refresh-token","expires_in":3600}"""));
    });
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; } finally { probe.Stop(); }
    }

    // ---- federation precedence ----
    private static async Task FederationResolves()
    {
        var lookup = new Lookup(Env(FederationVariables()));
        var result = await R.ResolveAnthropicApiKeyAsync(lookup, NoCredential);
        var auth = result.Authentication!;
        Require(result.Diagnostic == AuthenticationDiagnostic.Resolved && auth.Kind == AuthenticationKind.WorkloadIdentityFederation &&
            auth.Origin == AuthenticationOrigin.AnthropicWorkloadIdentityFederation && auth.Secret.Length == 0 &&
            auth.EnvironmentName is null && auth.GetAuthorizationHeader() is null);
        foreach (var (name, value) in FederationVariables()) Equal(value, auth.CredentialEnvironment!.GetValue(name));
        Require(lookup.Names.SequenceEqual([.. KeyNames, .. Required, .. Optional]), "Lookup order differs.");
        Require(AnthropicWorkloadIdentityFederation.Configure("anthropic", null, null, auth.CredentialEnvironment!) ==
            new AnthropicFederationConfiguration("org-test", "wrkspc_test", "fdrl_test", "svac_test", "/tmp/identity.jwt"));
        Require(!JsonSerializer.Serialize(result).Contains("fdrl_test", StringComparison.Ordinal) && !auth.ToString().Contains("fdrl_test", StringComparison.Ordinal));
    }

    private static async Task FederationOptional()
    {
        foreach (var extra in new (string, string?)[][] { [], [(R.AnthropicServiceAccountId, ""), (R.AnthropicWorkspaceId, "")] })
        {
            var auth = (await R.ResolveAnthropicApiKeyAsync(new Lookup(Env([.. FederationVariables(false), .. extra])), NoCredential)).Authentication!;
            Require(auth.Kind == AuthenticationKind.WorkloadIdentityFederation);
            Require(auth.CredentialEnvironment!.GetValue(R.AnthropicServiceAccountId) is null && auth.CredentialEnvironment.GetValue(R.AnthropicWorkspaceId) is null);
            Require(AnthropicWorkloadIdentityFederation.Configure("anthropic", null, null, auth.CredentialEnvironment) ==
                new AnthropicFederationConfiguration("org-test", null, "fdrl_test", null, "/tmp/identity.jwt"));
        }
    }

    private static async Task FederationPartial()
    {
        for (var index = 0; index < Required.Length; index++)
            foreach (var replacement in new string?[] { null, "" })
            {
                var values = FederationVariables().Where(item => item.Item1 != Required[index]).Append((Required[index], replacement)).ToArray();
                var lookup = new Lookup(Env(values));
                var result = await R.ResolveAnthropicApiKeyAsync(lookup, NoCredential);
                Require(result.Diagnostic == AuthenticationDiagnostic.Missing && result.Authentication is null);
                Require(lookup.Names.SequenceEqual([.. KeyNames, .. Required[..(index + 1)]]), "Partial federation must stop at the missing variable.");
                Require(AnthropicWorkloadIdentityFederation.Configure("anthropic", null, null, Env(values)) is null);
            }
    }

    private static async Task FederationPrecedence()
    {
        async Task<(ResolvedAuthentication Auth, Lookup Lookup)> Resolve(params (string, string?)[] extra)
        {
            var lookup = new Lookup(Env([.. FederationVariables(), .. extra]));
            return ((await R.ResolveAnthropicApiKeyAsync(lookup, NoCredential)).Authentication!, lookup);
        }
        var (key, keyLookup) = await Resolve((R.AnthropicApiKey, "api-key"));
        Require(key.Kind == AuthenticationKind.ApiKey && key.Origin == AuthenticationOrigin.EnvironmentApiKey && key.Secret == "api-key" && keyLookup.Names.SequenceEqual(KeyNames));
        var (bearer, bearerLookup) = await Resolve((R.AnthropicAuthToken, "auth-token"));
        Require(bearer.Kind == AuthenticationKind.BearerToken && bearer.GetAuthorizationHeader() == "Bearer auth-token" && bearerLookup.Names.SequenceEqual(KeyNames[..1]));
        var (oauth, _) = await Resolve((R.AnthropicOAuthToken, "oauth-token"));
        Require(oauth.Kind == AuthenticationKind.ApiKey && oauth.Origin == AuthenticationOrigin.AnthropicOAuthEnvironmentToken);
        var stored = new Lookup(Env(FederationVariables()));
        var storedAuth = (await R.ResolveAnthropicApiKeyAsync(stored, (_, _) => ValueTask.FromResult<StoredApiKeyCredential?>(new("stored-key")))).Authentication!;
        Require(storedAuth.Origin == AuthenticationOrigin.StoredApiKey && stored.Names.Count == 0);
    }

    private static async Task FederationCancellation()
    {
        using var caller = new CancellationTokenSource();
        var lookup = new Lookup(Env(FederationVariables()), name => { if (name == R.AnthropicOrganizationId) caller.Cancel(); });
        var error = await Throws<OperationCanceledException>(() => R.ResolveAnthropicApiKeyAsync(lookup, NoCredential, caller.Token).AsTask());
        Require(error.CancellationToken == caller.Token && lookup.Names.SequenceEqual([.. KeyNames, .. Required[..2]]));
    }

    private static Task FederationConfigure()
    {
        var env = Env(FederationVariables(false));
        Require(AnthropicWorkloadIdentityFederation.Configure("anthropic", null, null, env) is not null);
        Require(AnthropicWorkloadIdentityFederation.Configure("kimi-coding", null, null, env) is null, "Other anthropic-messages providers never federate.");
        Require(AnthropicWorkloadIdentityFederation.Configure("anthropic", "explicit-key", null, env) is null, "An explicit key wins.");
        Require(AnthropicWorkloadIdentityFederation.Configure("anthropic", "", null, env) is not null);
        foreach (var name in new[] { "Authorization", "x-api-key", "CF-AIG-Authorization" })
            Require(AnthropicWorkloadIdentityFederation.Configure("anthropic", null, [KeyValuePair.Create(name, (string?)"value")], env) is null, name);
        Require(AnthropicWorkloadIdentityFederation.Configure("anthropic", null,
            [KeyValuePair.Create("Authorization", (string?)" \t"), KeyValuePair.Create("x-api-key", (string?)null), KeyValuePair.Create("x-other", (string?)"v")], env) is not null);
        // Request env overrides the process env with truthy values only, like getProviderEnvValue.
        var layered = new ProviderEnvironmentSnapshot(
            new Dictionary<string, string?> { [R.AnthropicOrganizationId] = "org-request", [R.AnthropicFederationRuleId] = "" },
            FederationVariables(false).ToDictionary(item => item.Item1, item => item.Item2));
        var config = AnthropicWorkloadIdentityFederation.Configure("anthropic", null, null, layered)!;
        Require(config.OrganizationId == "org-request" && config.FederationRuleId == "fdrl_test");
        return Task.CompletedTask;
    }

    private static async Task FederationAdapter()
    {
        var resolution = await R.ResolveAnthropicApiKeyAsync(new Lookup(Env(FederationVariables())), NoCredential);
        var model = new ModelDescriptor("claude-test", "anthropic-messages", "anthropic");
        AnthropicInjectedAuthenticationBinding? bound = null; var fake = Respond("{}");
        await using (var lease = await AnthropicInjectedTransportAdapter.AcquireAsync(model, resolution, (selected, binding, _) =>
        {
            bound = binding;
            var provider = AnthropicResolvedProviderFactory.Create(selected, new Uri("https://api.anthropic.com/"), binding, new(1024), handler: fake);
            return ValueTask.FromResult(new AnthropicTransportAdmission(selected, provider.Transport, new Owner(provider)));
        }))
            Require(lease.Model == model);
        Require(bound is { Kind: AuthenticationKind.WorkloadIdentityFederation, ApiKey: null, UseOAuthProjection: false } && bound.Headers.IsEmpty);
        Require(bound!.Federation == new AnthropicFederationConfiguration("org-test", "wrkspc_test", "fdrl_test", "svac_test", "/tmp/identity.jwt"));
        Require(fake.Requests.Count == 0, "Acquisition neither exchanges nor sends.");
        Require(!bound.ToString().Contains("fdrl", StringComparison.Ordinal) && !bound.ToString().Contains("org-test", StringComparison.Ordinal));
        // The secretless federation result still never reaches the key-only factory.
        await Throws<ArgumentException>(() => { using var _ = NativeProviderFactory.CreateAnthropic(model, new Uri("https://api.anthropic.com/"),
            resolution.Authentication!.Secret, new(1024), handler: fake); return Task.CompletedTask; });
    }

    private sealed class Owner(NativeHttpModelProvider provider) : IAsyncDisposable
    { public ValueTask DisposeAsync() { provider.Dispose(); return ValueTask.CompletedTask; } }
    private static readonly ModelDescriptor FederatedModel = new("claude-federated", "anthropic-messages", "anthropic");
    private static ChatRequest FederatedRequest(string text) => new(FederatedModel,
        [new("system", JsonData.Parse("""{"role":"system","content":[{"type":"text","text":"Authored system"}]}""")),
         new("user", JsonData.Parse("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}]}"))]);
    private static HttpResponseMessage MessagesStream()
    {
        static string Frame(string type, object value) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
        var stream = Frame("message_start", new { type = "message_start", message = new { id = "authored-response", role = "assistant", model = FederatedModel.Id, content = Array.Empty<object>(), usage = new { input_tokens = 2, output_tokens = 0 } } })
            + Frame("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } })
            + Frame("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "ok" } })
            + Frame("content_block_stop", new { type = "content_block_stop", index = 0 })
            + Frame("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { output_tokens = 1 } })
            + Frame("message_stop", new { type = "message_stop" });
        return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
    }
    private static async Task<int> Drain(AnthropicInjectedTransportLease lease, ChatRequest request)
    { var frames = 0; await foreach (var _ in lease.Transport.StreamAsync(request)) frames++; return frames; }
    private static Fake FederatedEndpoints(Func<int> nextToken) => new((seen, _) => Task.FromResult(seen.Url == "https://api.anthropic.com/v1/oauth/token"
        ? Json($$"""{"access_token":"federated-{{nextToken()}}","expires_in":3600,"token_type":"Bearer"}""")
        : seen.Url == "https://api.anthropic.com/v1/messages?beta=true" ? MessagesStream() : throw new InvalidOperationException("Unexpected URL.")));
    private static async Task<AuthenticationResolution> ResolveFederation(string path) =>
        await R.ResolveAnthropicApiKeyAsync(new Lookup(Env((R.AnthropicFederationRuleId, "fdrl_test"), (R.AnthropicOrganizationId, "org-test"),
            (R.AnthropicServiceAccountId, "svac_test"), (R.AnthropicIdentityTokenFile, path), (R.AnthropicWorkspaceId, "wrkspc_test"))), NoCredential);

    // Pi abe508e1 anthropic-messages.ts createClient: federation shares the API-key path's default headers and body; the SDK's
    // token auth adds Authorization: Bearer and the oauth-2025-04-20 beta (2i port of @anthropic-ai/sdk 0.129.0).
    private static Task FederationResolvedProvider() => WithIdentityFile("header.payload.signature\n", async path =>
    {
        var resolution = await ResolveFederation(path);
        Require(resolution.Authentication is { Kind: AuthenticationKind.WorkloadIdentityFederation });
        var exchanges = 0; var fake = FederatedEndpoints(() => ++exchanges);
        var projection = new AnthropicMessagesRequestOptions(MaximumTokens: 1024, MaximumMessages: 64, MaximumEntryCharacters: 65_536,
            CacheRetention: AnthropicCacheRetention.Short);
        var options = new AnthropicMessagesKeyAuthRequestOptions(MaxTokens: 1024);
        await using (var lease = await AnthropicResolvedTransports.AcquireMainAsync(FederatedModel, new Uri("https://api.anthropic.com/"), resolution,
            projection, options, handler: fake))
        {
            Require(fake.Requests.Count == 0, "No exchange before the first request.");
            Require(await Drain(lease, FederatedRequest("first")) > 0);
            Require(await Drain(lease, FederatedRequest("second")) > 0);
        }
        Require(exchanges == 1 && fake.Requests.Count == 3, "One exchange serves both requests of the provider client.");
        var exchange = fake.Requests[0];
        Require(exchange.Method == "POST" && exchange.Url == "https://api.anthropic.com/v1/oauth/token" && exchange.ContentType == "application/json");
        BodyEqual("""{"grant_type":"urn:ietf:params:oauth:grant-type:jwt-bearer","assertion":"header.payload.signature","federation_rule_id":"fdrl_test","organization_id":"org-test","service_account_id":"svac_test","workspace_id":"wrkspc_test"}""", exchange.Body);
        Equal("oauth-2025-04-20,oidc-federation-2026-04-01", exchange.Headers["anthropic-beta"]);
        Require(!exchange.Headers.ContainsKey("Authorization") && !exchange.Headers.ContainsKey("x-api-key"));
        foreach (var (seen, text) in new[] { (fake.Requests[1], "first"), (fake.Requests[2], "second") })
        {
            Require(seen.Method == "POST" && seen.Url == "https://api.anthropic.com/v1/messages?beta=true" && seen.ContentType == "application/json");
            Equal("Accept=application/json|anthropic-beta=oauth-2025-04-20|anthropic-dangerous-direct-browser-access=true|anthropic-version=2023-06-01|Authorization=Bearer federated-1|User-Agent=PiSharp",
                string.Join("|", seen.Headers.OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase).Select(header => header.Key + "=" + header.Value)));
            BodyEqual($$$"""{"model":"claude-federated","messages":[{"role":"user","content":[{"type":"text","text":"{{{text}}}","cache_control":{"type":"ephemeral"}}]}],"max_tokens":1024,"stream":true,"system":[{"type":"text","text":"Authored system","cache_control":{"type":"ephemeral"}}]}""", seen.Body);
        }

        // The same request on the API-key path differs only by the credential header and the OAuth beta.
        var keyed = await R.ResolveAnthropicApiKeyAsync(new Lookup(Env((R.AnthropicApiKey, "AUTHORED_KEY"))), NoCredential);
        var keyFake = new Fake((_, _) => Task.FromResult(MessagesStream()));
        await using (var lease = await AnthropicResolvedTransports.AcquireMainAsync(FederatedModel, new Uri("https://api.anthropic.com/"), keyed,
            projection, options, handler: keyFake))
            await Drain(lease, FederatedRequest("first"));
        var key = keyFake.Requests.Single();
        BodyEqual(fake.Requests[1].Body!, key.Body);
        Equal("AUTHORED_KEY", key.Headers["x-api-key"]); Require(!key.Headers.ContainsKey("Authorization"));
        Equal(fake.Requests[1].Headers["anthropic-beta"], key.Headers.TryGetValue("anthropic-beta", out var keyBeta) ? keyBeta + ", oauth-2025-04-20" : "oauth-2025-04-20");
        Equal(string.Join("|", fake.Requests[1].Headers.Keys.Where(name => name is not ("Authorization" or "anthropic-beta")).Order(StringComparer.OrdinalIgnoreCase)),
            string.Join("|", key.Headers.Keys.Where(name => name is not ("x-api-key" or "anthropic-beta")).Order(StringComparer.OrdinalIgnoreCase)));
    });

    // hasRequestAuth(apiKey, options.headers): a request-owned authorization header disables federation, as upstream.
    private static Task FederationOptionHeaderWins() => WithIdentityFile("header.payload.signature", async path =>
    {
        var resolution = await ResolveFederation(path);
        var exchanges = 0; var fake = FederatedEndpoints(() => ++exchanges);
        await using (var lease = await AnthropicResolvedTransports.AcquireMainAsync(FederatedModel, new Uri("https://api.anthropic.com/"), resolution,
            new(1024), new(Headers: JsonData.Parse("""{"authorization":"Bearer option-owned"}""")), handler: fake))
            await Drain(lease, FederatedRequest("first"));
        Require(exchanges == 0);
        var seen = fake.Requests.Single();
        Equal("Bearer option-owned", seen.Headers["authorization"]);
        Require(!(seen.Headers.TryGetValue("anthropic-beta", out var beta) && beta.Contains("oauth-2025-04-20", StringComparison.Ordinal)));
    });

    // ---- federation exchange ----
    private static async Task WithIdentityFile(string content, Func<string, Task> run)
    {
        var directory = Directory.CreateTempSubdirectory("pisharp-anthropic-federation-");
        try
        {
            var path = Path.Combine(directory.FullName, "identity.jwt");
            await File.WriteAllTextAsync(path, content);
            await run(path);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static Task FederationExchange() => WithIdentityFile("  header.payload.signature\n", async path =>
    {
        var fake = Respond("""{"access_token":"federated-token","expires_in":3600,"token_type":"Bearer"}""");
        using var http = new HttpClient(fake);
        var clock = new Clock(Epoch);
        var full = new AnthropicFederationConfiguration("org-test", "wrkspc_test", "fdrl_test", "svac_test", path);
        var token = await AnthropicWorkloadIdentityFederation.ExchangeAsync(http, new Uri("https://api.anthropic.com/"), full, clock);
        Require(token.Token == "federated-token" && token.ExpiresAtUnixSeconds == 1_700_000_000 + 3600 && !token.ToString().Contains("federated", StringComparison.Ordinal));
        var seen = fake.Requests.Single();
        Require(seen.Method == "POST" && seen.Url == "https://api.anthropic.com/v1/oauth/token" && seen.ContentType == "application/json");
        BodyEqual("""{"grant_type":"urn:ietf:params:oauth:grant-type:jwt-bearer","assertion":"header.payload.signature","federation_rule_id":"fdrl_test","organization_id":"org-test","service_account_id":"svac_test","workspace_id":"wrkspc_test"}""", seen.Body);
        Equal("oauth-2025-04-20,oidc-federation-2026-04-01", seen.Headers["anthropic-beta"]);
        Equal("PiSharp oidcFederationProvider", seen.Headers["User-Agent"]);
        Require(!seen.Headers.ContainsKey("Authorization") && !seen.Headers.ContainsKey("x-api-key"));

        var minimal = Respond("""{"access_token":"t","expires_in":"60"}""");
        using var minimalHttp = new HttpClient(minimal);
        var minimalToken = await AnthropicWorkloadIdentityFederation.ExchangeAsync(minimalHttp, new Uri("http://127.0.0.1:8080/base//"),
            new AnthropicFederationConfiguration("org-test", null, "fdrl_test", null, path), clock, "custom-agent");
        Require(minimalToken.ExpiresAtUnixSeconds == 1_700_000_060);
        var minimalSeen = minimal.Requests.Single();
        Equal("http://127.0.0.1:8080/base/v1/oauth/token", minimalSeen.Url);
        BodyEqual("""{"grant_type":"urn:ietf:params:oauth:grant-type:jwt-bearer","assertion":"header.payload.signature","federation_rule_id":"fdrl_test","organization_id":"org-test"}""", minimalSeen.Body);
        Equal("custom-agent", minimalSeen.Headers["User-Agent"]);
    });

    private static Task FederationExchangeFailures() => WithIdentityFile("jwt", async path =>
    {
        var config = new AnthropicFederationConfiguration("org-test", null, "fdrl_test", null, path);
        var api = new Uri("https://api.anthropic.com");
        async Task<AnthropicWorkloadIdentityException> Fail(Fake fake, AnthropicFederationConfiguration? with = null, Uri? baseUri = null)
        {
            using var http = new HttpClient(fake);
            return await Throws<AnthropicWorkloadIdentityException>(() => AnthropicWorkloadIdentityFederation.ExchangeAsync(http, baseUri ?? api, with ?? config));
        }
        var unauthorized = await Fail(Respond("""{"error":"invalid_grant","error_description":"rule mismatch","assertion":"SECRET_JWT","access_token":"SECRET_ACCESS"}""",
            HttpStatusCode.Unauthorized, "req_123"));
        Require(unauthorized.StatusCode == 401 && unauthorized.RequestId == "req_123");
        Equal("""{"error":"invalid_grant","error_description":"rule mismatch"}""", unauthorized.Body);
        Require(unauthorized.Message.StartsWith("""Token exchange failed with status 401 (request-id req_123): {"error":"invalid_grant","error_description":"rule mismatch"} Ensure your federation rule matches your identity token. If your federation rule is scoped to multiple workspaces, set the ANTHROPIC_WORKSPACE_ID""", StringComparison.Ordinal));
        Require(!unauthorized.Message.Contains("SECRET", StringComparison.Ordinal));
        var scoped = await Fail(Respond("{}", HttpStatusCode.Unauthorized), config with { WorkspaceId = "wrkspc_x" });
        Require(!scoped.Message.Contains("ANTHROPIC_WORKSPACE_ID", StringComparison.Ordinal) && scoped.Message.Contains("Workload identity page", StringComparison.Ordinal));
        var server = await Fail(Respond(new string('x', 2500), HttpStatusCode.InternalServerError));
        Require(server.Message == "Token exchange failed with status 500: " + new string('x', 2000) + "... <500 more chars>");
        Equal("Token endpoint returned non-JSON response (status 200)", (await Fail(Respond("not json"))).Message);
        Equal("""Token endpoint response missing access_token: {"error":"x"}""", (await Fail(Respond("""{"error":"x","refresh_token":"SECRET"}"""))).Message);
        Equal("""Token endpoint response: unsupported token_type "mac" (want Bearer)""", (await Fail(Respond("""{"access_token":"a","token_type":"mac","expires_in":1}"""))).Message);
        Equal("Token endpoint response missing required fields: {}", (await Fail(Respond("""{"access_token":"a"}"""))).Message);
        Equal("Token endpoint response missing required fields: {}", (await Fail(Respond("""{"access_token":"a","expires_in":"soon"}"""))).Message);
        // Cleartext, oversized assertions and unreadable/empty identity files never reach the endpoint.
        var untouched = Respond("{}");
        Require((await Fail(untouched, baseUri: new Uri("http://api.anthropic.com"))).Message.StartsWith("Refusing to send credential over non-https token endpoint", StringComparison.Ordinal));
        await File.WriteAllTextAsync(path, new string('j', 16 * 1024 + 1));
        Equal("Identity token is 17 KiB, exceeds the 16 KiB assertion limit", (await Fail(untouched)).Message);
        await File.WriteAllTextAsync(path, " \n");
        Equal($"Identity token file at {path} is empty", (await Fail(untouched)).Message);
        Require((await Fail(untouched, config with { IdentityTokenFile = path + ".missing" })).Message.StartsWith($"Failed to read identity token file at {path}.missing", StringComparison.Ordinal));
        Require(untouched.Requests.Count == 0);
        await File.WriteAllTextAsync(path, new string('j', 16 * 1024));
        var limit = Respond("""{"access_token":"a","expires_in":1}""");
        using var limitHttp = new HttpClient(limit);
        await AnthropicWorkloadIdentityFederation.ExchangeAsync(limitHttp, new Uri("http://localhost:1"), config);
        Require(limit.Requests.Count == 1);
    });

    private static async Task FederationCache()
    {
        var clock = new Clock(1_000_000_000_000);
        var calls = 0;
        Func<CancellationToken, Task<AnthropicFederationAccessToken>>? behaviour = null;
        Task<AnthropicFederationAccessToken> Mint(CancellationToken token)
        {
            Require(!token.CanBeCanceled, "A shared refresh must not inherit a caller token.");
            var call = Interlocked.Increment(ref calls);
            return behaviour?.Invoke(token) ?? Task.FromResult(new AnthropicFederationAccessToken("t" + call, clock.Milliseconds / 1000 + 3600));
        }
        var errors = new List<Exception>(); var errorSeen = Signal<bool>();
        var cache = new AnthropicFederationTokenCache(Mint, clock, error => { lock (errors) errors.Add(error); errorSeen.TrySetResult(true); });
        Equal("t1", await cache.GetTokenAsync()); Equal("t1", await cache.GetTokenAsync()); Require(calls == 1);
        clock.Milliseconds += (3600 - 100) * 1000L;       // 100 s left: advisory, serve stale and refresh in the background
        Equal("t1", await cache.GetTokenAsync()); Require(calls == 2);
        Equal("t2", await cache.GetTokenAsync()); Require(calls == 2);
        clock.Milliseconds += (3600 - 20) * 1000L;        // 20 s left: mandatory, block
        Equal("t3", await cache.GetTokenAsync()); Require(calls == 3);
        cache.Invalidate();
        Equal("t4", await cache.GetTokenAsync()); Require(calls == 4);

        // Advisory failures keep the stale token and back off for five seconds.
        clock.Milliseconds += (3600 - 100) * 1000L;
        behaviour = _ => Task.FromException<AnthropicFederationAccessToken>(new InvalidOperationException("SYNTHETIC_EXCHANGE_FAILURE"));
        Equal("t4", await cache.GetTokenAsync()); await errorSeen.Task; Require(calls == 5 && errors.Count == 1);
        Equal("t4", await cache.GetTokenAsync()); Require(calls == 5, "Advisory refresh must back off.");
        clock.Milliseconds += 6000;
        Equal("t4", await cache.GetTokenAsync()); Require(calls == 6);

        // Concurrent mandatory callers coalesce; a cancelled caller does not cancel the shared exchange.
        var pending = Signal<AnthropicFederationAccessToken>(); behaviour = _ => pending.Task; calls = 0;
        var fresh = new AnthropicFederationTokenCache(Mint, clock);
        using var caller = new CancellationTokenSource();
        var first = fresh.GetTokenAsync(); var second = fresh.GetTokenAsync(); var cancelled = fresh.GetTokenAsync(caller.Token);
        caller.Cancel();
        await Throws<OperationCanceledException>(() => cancelled);
        Require(calls == 1 && !first.IsCompleted);
        pending.SetResult(new("shared", clock.Milliseconds / 1000 + 3600));
        Equal("shared", await first); Equal("shared", await second); Equal("shared", await fresh.GetTokenAsync()); Require(calls == 1);
    }

    private static Task FederationHandler() => WithIdentityFile("header.payload.signature", async path =>
    {
        var exchanges = 0; var statuses = new Queue<HttpStatusCode>([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.OK, HttpStatusCode.OK]);
        var inner = new Fake((seen, _) =>
        {
            if (seen.Url.EndsWith("/v1/oauth/token", StringComparison.Ordinal))
                return Task.FromResult(Json($$"""{"access_token":"federated-{{++exchanges}}","expires_in":3600}"""));
            Require(seen.Url == "https://api.anthropic.com/v1/messages?beta=true");
            return Task.FromResult(new HttpResponseMessage(statuses.Dequeue()) { Content = new StringContent("{}") });
        });
        var config = new AnthropicFederationConfiguration("org-test", null, "fdrl_test", null, path);
        using var client = new HttpClient(new AnthropicFederationHandler(new Uri("https://api.anthropic.com"), config, inner, new Clock(Epoch)));
        async Task<HttpResponseMessage> Send(string? beta = null, string? authorization = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages?beta=true") { Content = new StringContent("{}") };
            if (beta is not null) request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
            if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
            return await client.SendAsync(request);
        }
        Seen Last() => inner.Requests.Last();
        (await Send()).Dispose();
        Equal("Bearer federated-1", Last().Headers["Authorization"]); Equal("oauth-2025-04-20", Last().Headers["anthropic-beta"]);
        (await Send("fine-grained-tool-streaming-2025-05-14,interleaved-thinking-2025-05-14")).Dispose();
        Equal("fine-grained-tool-streaming-2025-05-14,interleaved-thinking-2025-05-14, oauth-2025-04-20", Last().Headers["anthropic-beta"]);
        (await Send("a, oauth-2025-04-20")).Dispose();
        Equal("a, oauth-2025-04-20", Last().Headers["anthropic-beta"]); Equal("Bearer federated-1", Last().Headers["Authorization"]);
        Require(exchanges == 1, "The identity token is exchanged once across requests.");
        using (var unauthorized = await Send()) Require(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "A 401 is returned, not retried.");
        Require(inner.Requests.Count(seen => seen.Url.Contains("/v1/messages", StringComparison.Ordinal)) == 4 && exchanges == 1);
        (await Send()).Dispose();
        Require(exchanges == 2 && Last().Headers["Authorization"] == "Bearer federated-2", "A 401 invalidates the cached token.");
        (await Send(authorization: "Bearer explicit")).Dispose();
        Equal("Bearer explicit", Last().Headers["Authorization"]);
        var exchangeRequest = inner.Requests.First(seen => seen.Url.EndsWith("/v1/oauth/token", StringComparison.Ordinal));
        Require(!exchangeRequest.Headers.ContainsKey("Authorization"), "The exchange does not pass through token application.");
        await Throws<AnthropicWorkloadIdentityException>(() => { using var _ = new AnthropicFederationHandler(new Uri("http://api.anthropic.com"), config, inner); return Task.CompletedTask; });
    });

    // ---- refresh persistence under cancellation ----
    private static OAuthCredentialSnapshot Expiring(string marker = "old") => new(marker + "-access", marker + "-refresh", Epoch + 1000);

    private static async Task RefreshPersists()
    {
        var clock = new Clock(Epoch);
        var source = new Source(); source.Seed(Expiring());
        var entered = Signal<CancellationToken>(); var finish = Signal<OAuthCredentialSnapshot>();
        var refresh = new Refresh((_, token) => { entered.SetResult(token); return finish.Task; });
        using var caller = new CancellationTokenSource();
        var operation = new StoredOAuthLifecycle(source, refresh, clock).ResolveAsync("anthropic", cancellationToken: caller.Token);
        var refreshToken = await entered.Task;
        caller.Cancel();
        Require(!refreshToken.IsCancellationRequested && refreshToken.CanBeCanceled, "Only the refresh timeout bounds a started refresh.");
        var rotated = new OAuthCredentialSnapshot("rotated-access", "rotated-refresh", Epoch + 3_600_000);
        finish.SetResult(rotated);
        var error = await Throws<OperationCanceledException>(() => operation);
        Require(error.CancellationToken == caller.Token && ReferenceEquals(source.Current, rotated) && source.Writes == 1 && refresh.Calls == 1);
    }

    private static async Task RefreshLaneWait()
    {
        var source = new Source(); source.Seed(Expiring());
        var refresh = new Refresh((_, _) => throw new InvalidOperationException("Unexpected refresh."));
        await source.Lane.WaitAsync();
        using var caller = new CancellationTokenSource();
        var operation = new StoredOAuthLifecycle(source, refresh, new Clock(Epoch)).ResolveAsync("anthropic", cancellationToken: caller.Token);
        caller.Cancel();
        var error = await Throws<OperationCanceledException>(() => operation);
        source.Lane.Release();
        Require(error.CancellationToken == caller.Token && refresh.Calls == 0 && source.Writes == 0);
    }

    private static async Task RefreshCallbackEntry()
    {
        var source = new Source(); source.Seed(Expiring());
        var gate = Signal<bool>(); var invoked = Signal<bool>();
        source.ModifyOverride = async (mutation, _) =>
        {
            await gate.Task; // A source that does not observe its token before invoking the callback.
            invoked.SetResult(true);
            return await mutation(source.Current, CancellationToken.None);
        };
        var refresh = new Refresh((_, _) => throw new InvalidOperationException("Unexpected refresh."));
        using var caller = new CancellationTokenSource();
        var operation = new StoredOAuthLifecycle(source, refresh, new Clock(Epoch)).ResolveAsync("anthropic", cancellationToken: caller.Token);
        caller.Cancel(); gate.SetResult(true);
        var error = await Throws<OperationCanceledException>(() => operation);
        Require(invoked.Task.IsCompleted && error.CancellationToken == caller.Token && refresh.Calls == 0 && source.Writes == 0);
    }

    private static async Task RefreshAnthropic()
    {
        var clock = new Clock(Epoch);
        var entered = Signal<bool>(); var release = Signal<bool>(); var endpointToken = Signal<CancellationToken>();
        var fake = new Fake(async (seen, token) =>
        {
            endpointToken.SetResult(token); entered.SetResult(true); await release.Task;
            Require(!token.IsCancellationRequested, "The refresh request must survive caller cancellation.");
            return Json("""{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""");
        });
        using var http = new HttpClient(fake);
        var source = new Source(); source.Seed(Expiring());
        using var caller = new CancellationTokenSource();
        var operation = new StoredOAuthLifecycle(source, new AnthropicOAuth(http, clock), clock).ResolveAsync("anthropic", cancellationToken: caller.Token);
        await entered.Task; caller.Cancel(); release.SetResult(true);
        await Throws<OperationCanceledException>(() => operation);
        var seen = fake.Requests.Single();
        Require(seen.Url == "https://platform.claude.com/v1/oauth/token" && seen.ContentType == "application/json" && seen.Headers["Accept"] == "application/json");
        BodyEqual($$"""{"grant_type":"refresh_token","client_id":"{{ClientId}}","refresh_token":"old-refresh"}""", seen.Body);
        var stored = source.Current!;
        Require(stored.Access == "new-access" && stored.Refresh == "new-refresh" && stored.ExpiresUnixMilliseconds == Epoch + 3_600_000 - 300_000 && source.Writes == 1);
    }

    // ---- OAuth login ----
    private static async Task CopyCodeLogin()
    {
        Seen? exchange = null;
        using var http = new HttpClient(TokenEndpoint(seen => exchange = seen));
        var clock = new Clock(Epoch);
        Interaction? interaction = null;
        interaction = new Interaction
        {
            Select = (_, _, _) => Task.FromResult("copy_code"),
            Manual = (_, _, _) => Task.FromResult("copied-code#" + Param(interaction!.AuthUrl, "state")),
        };
        var credential = await new AnthropicOAuth(http, clock).LoginAsync(interaction);
        Require(credential.Access == "access-token" && credential.Refresh == "refresh-token" && credential.ExpiresUnixMilliseconds == Epoch + 3_600_000 - 300_000);
        var (message, options) = interaction.Selects.Single();
        Equal("Select Anthropic login method:", message);
        Require(options.SequenceEqual([new AnthropicOAuthLoginOption("browser", "Browser login (default)"), new AnthropicOAuthLoginOption("copy_code", "Copy code login (headless)")]));
        var url = interaction.AuthUrl; var state = Param(url, "state")!; var challenge = Param(url, "code_challenge")!;
        Require(url.StartsWith($"https://claude.ai/oauth/authorize?code=true&client_id={ClientId}&response_type=code&redirect_uri=https%3A%2F%2Fplatform.claude.com%2Foauth%2Fcode%2Fcallback&scope=org%3Acreate_api_key+user%3Aprofile+user%3Ainference+user%3Asessions%3Aclaude_code+user%3Amcp_servers+user%3Afile_upload&code_challenge=", StringComparison.Ordinal));
        Require(url.EndsWith("&code_challenge_method=S256&state=" + state, StringComparison.Ordinal));
        Equal(Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(state))), challenge);
        Equal("Complete login in your browser, then copy the code Anthropic shows and paste it here.", interaction.Events[0].Instructions);
        var prompt = interaction.Prompts.Single();
        Require(prompt.Message == "Paste the code Anthropic shows after you sign in:" && prompt.Placeholder == "code#state");
        Equal("Exchanging authorization code for tokens...", interaction.Events.Single(item => item.Kind == AnthropicOAuthLoginEventKind.Progress).Message);
        Require(exchange!.ContentType == "application/json" && exchange.Headers["Accept"] == "application/json");
        BodyEqual($$"""{"grant_type":"authorization_code","client_id":"{{ClientId}}","code":"copied-code","state":"{{state}}","redirect_uri":"https://platform.claude.com/oauth/code/callback","code_verifier":"{{state}}"}""", exchange.Body);
    }

    private static async Task CopyCodeFailures()
    {
        var endpoint = TokenEndpoint();
        using var http = new HttpClient(endpoint);
        foreach (var (input, expected) in new[] { ("code#other-state", "OAuth state mismatch"), ("   ", "Missing authorization code"), ("#", "Missing authorization code") })
        {
            var interaction = new Interaction { Select = (_, _, _) => Task.FromResult("copy_code"), Manual = (_, _, _) => Task.FromResult(input) };
            Equal(expected, (await Throws<InvalidOperationException>(() => new AnthropicOAuth(http).LoginAsync(interaction))).Message);
        }
        Require(endpoint.Requests.Count == 0);
        // An empty state is not checked, and is forwarded as-is ("code#" keeps state "").
        Seen? exchange = null;
        using var forwarding = new HttpClient(TokenEndpoint(seen => exchange = seen));
        await new AnthropicOAuth(forwarding).LoginAsync(new Interaction { Select = (_, _, _) => Task.FromResult("copy_code"), Manual = (_, _, _) => Task.FromResult("code#") });
        Require(exchange!.Body!.Contains("\"code\":\"code\",\"state\":\"\",", StringComparison.Ordinal));
    }

    private static Task AuthorizationUrl()
    {
        Equal($"https://claude.ai/oauth/authorize?code=true&client_id={ClientId}&response_type=code&redirect_uri=http%3A%2F%2Flocalhost%3A53692%2Fcallback&scope=org%3Acreate_api_key+user%3Aprofile+user%3Ainference+user%3Asessions%3Aclaude_code+user%3Amcp_servers+user%3Afile_upload&code_challenge=ch_a-l.l*&code_challenge_method=S256&state=st%2B%2F%3D",
            AnthropicOAuth.BuildAuthorizationUrl(AnthropicOAuth.RedirectUri, "ch_a-l.l*", "st+/="));
        Require(AnthropicOAuth.ClientId == ClientId && AnthropicOAuth.CallbackPort == 53692 && AnthropicOAuth.RedirectUri == "http://localhost:53692/callback" &&
            AnthropicOAuth.CopyCodeRedirectUri == "https://platform.claude.com/oauth/code/callback");
        Equal("127.0.0.1", AnthropicOAuth.CallbackHostFrom(Env()));
        Equal("0.0.0.0", AnthropicOAuth.CallbackHostFrom(Env(("PI_OAUTH_CALLBACK_HOST", "0.0.0.0"))));
        var (verifier, challenge) = AnthropicOAuth.GeneratePkce();
        Require(verifier.Length == 43 && challenge == Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))));
        return Task.CompletedTask;
    }

    private static Task ParseInput()
    {
        foreach (var (input, code, state) in new (string, string?, string?)[]
        {
            ("http://localhost:53692/callback?code=abc&state=xyz", "abc", "xyz"), ("https://x.test/cb?state=s#frag", null, "s"),
            ("  code1#state1 \n", "code1", "state1"), ("a#b#c", "a", "b"), ("#st", "", "st"),
            ("code=c%20d&state=s+t", "c d", "s t"), ("?code=q", "q", null), ("code=", "", null),
            ("bare-code", "bare-code", null), ("", null, null), ("  ", null, null),
        })
        {
            var parsed = AnthropicOAuth.ParseAuthorizationInput(input);
            Require(parsed.Code == code && parsed.State == state, $"Parse mismatch for <{input}>: <{parsed.Code}> <{parsed.State}>.");
        }
        return Task.CompletedTask;
    }

    private static async Task BrowserManual()
    {
        var port = FreePort();
        Seen? exchange = null;
        using var http = new HttpClient(TokenEndpoint(seen => exchange = seen));
        Interaction? interaction = null;
        interaction = new Interaction
        {
            Manual = (_, _, _) => Task.FromResult($"{Param(interaction!.AuthUrl, "redirect_uri")}?code=manual-code&state={Param(interaction.AuthUrl, "state")}"),
        };
        var credential = await new AnthropicOAuth(http, callbackPort: port).LoginAsync(interaction);
        var redirect = $"http://localhost:{port}/callback";
        Require(credential.Access == "access-token" && Param(interaction.AuthUrl, "redirect_uri") == redirect);
        Equal("Complete login in your browser. If the browser is on another machine, paste the final redirect URL here.", interaction.Events[0].Instructions);
        var prompt = interaction.Prompts.Single();
        Require(prompt.Message == "Complete login in your browser, or paste the authorization code / redirect URL here:" && prompt.Placeholder == redirect);
        Require(prompt.Token.IsCancellationRequested, "The manual prompt is cancelled once login settles.");
        Require(exchange!.Body!.Contains("\"code\":\"manual-code\"", StringComparison.Ordinal) && exchange.Body.Contains($"\"redirect_uri\":\"{redirect}\"", StringComparison.Ordinal));
    }

    private static async Task<(OAuthCredentialSnapshot Credential, string Redirect, Seen Exchange, HttpStatusCode Status, string Page)> LoginThroughCallback(int? port = null)
    {
        Seen? exchange = null;
        using var http = new HttpClient(TokenEndpoint(seen => exchange = seen));
        using var browser = new HttpClient();
        Task<HttpResponseMessage>? page = null;
        var interaction = new Interaction
        {
            OnNotify = item =>
            {
                if (item.Kind != AnthropicOAuthLoginEventKind.AuthUrl) return;
                var redirect = new Uri(Param(item.Url!, "redirect_uri")!);
                page = browser.GetAsync($"http://127.0.0.1:{redirect.Port}{redirect.AbsolutePath}?code=browser-code&state={Uri.EscapeDataString(Param(item.Url!, "state")!)}");
            },
        };
        var credential = await new AnthropicOAuth(http, callbackPort: port ?? AnthropicOAuth.CallbackPort).LoginAsync(interaction);
        using var response = await page!;
        return (credential, Param(interaction.AuthUrl, "redirect_uri")!, exchange!, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task BrowserCallback()
    {
        var port = FreePort();
        var login = await LoginThroughCallback(port: port);
        Require(login.Credential.Access == "access-token" && login.Redirect == $"http://localhost:{port}/callback");
        Require(login.Exchange.Body!.Contains("\"code\":\"browser-code\"", StringComparison.Ordinal) && login.Exchange.Body.Contains($"\"redirect_uri\":\"{login.Redirect}\"", StringComparison.Ordinal));
        Require(login.Status == HttpStatusCode.OK && login.Page.Contains("Signed in to Anthropic. You may now close this page.", StringComparison.Ordinal));
    }

    private static async Task BrowserFallback()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0); blocker.Start();
        try
        {
            var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;
            var login = await LoginThroughCallback(port: busy);
            var redirect = new Uri(login.Redirect);
            Require(redirect.Host == "localhost" && redirect.AbsolutePath == "/callback" && redirect.Port != busy, "Expected a free fallback port.");
            Require(login.Exchange.Body!.Contains($"\"redirect_uri\":\"{login.Redirect}\"", StringComparison.Ordinal) && login.Status == HttpStatusCode.OK);
        }
        finally { blocker.Stop(); }
    }

    private static async Task BrowserDefaultPortFallback()
    {
        TcpListener? blocker = new(IPAddress.Loopback, AnthropicOAuth.CallbackPort);
        try { blocker.Start(); }
        catch (SocketException) { blocker = null; } // Already busy or excluded on this host: the fallback still applies.
        try
        {
            var login = await LoginThroughCallback();
            if (blocker is not null) Require(new Uri(login.Redirect).Port != AnthropicOAuth.CallbackPort, "53692 is held, so a free port is used.");
            Require(login.Credential.Access == "access-token" && login.Status == HttpStatusCode.OK);
        }
        finally { blocker?.Stop(); }
    }

    private static async Task CallbackServer()
    {
        using var browser = new HttpClient();
        var server = AnthropicOAuthCallbackServer.Start("Anthropic", "127.0.0.1", 0, "/callback", "localhost", "st");
        try
        {
            Equal($"http://localhost:{server.Port}/callback", server.RedirectUri);
            async Task<(HttpStatusCode, string)> Get(string target)
            {
                using var response = await browser.GetAsync($"http://127.0.0.1:{server.Port}{target}");
                return (response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            var (notFound, notFoundPage) = await Get("/other?code=x&state=st");
            Require(notFound == HttpStatusCode.NotFound && notFoundPage.Contains("Callback route not found.", StringComparison.Ordinal));
            Require((await Get("/callback?code=x&state=bad")).Item1 == HttpStatusCode.BadRequest && !server.WaitAsync().IsCompleted);
            var (missing, missingPage) = await Get("/callback?state=st");
            Require(missing == HttpStatusCode.BadRequest && missingPage.Contains("Missing authorization code.", StringComparison.Ordinal) && !server.WaitAsync().IsCompleted);
            var (denied, deniedPage) = await Get("/callback?error=access_denied&error_description=%3Cdenied%3E&state=st");
            Require(denied == HttpStatusCode.BadRequest && deniedPage.Contains("&lt;denied&gt;", StringComparison.Ordinal) && !deniedPage.Contains("<denied>", StringComparison.Ordinal));
            Equal("Anthropic authorization failed: <denied>", (await Throws<InvalidOperationException>(() => server.WaitAsync())).Message);
            Require((await Get("/callback?code=late&state=st")).Item1 == HttpStatusCode.Conflict);
        }
        finally { server.Close(); }

        var cancelled = AnthropicOAuthCallbackServer.Start("Anthropic", "127.0.0.1", 0, "/callback", null, null);
        Equal($"http://127.0.0.1:{cancelled.Port}/callback", cancelled.RedirectUri);
        cancelled.Cancel(); Require(await cancelled.WaitAsync() is null); cancelled.Close();
        using var abort = new CancellationTokenSource();
        var aborted = AnthropicOAuthCallbackServer.Start("Anthropic", "127.0.0.1", 0, "/callback", "localhost", "st", abort.Token);
        abort.Cancel();
        Equal("Login cancelled", (await Throws<OperationCanceledException>(() => aborted.WaitAsync())).Message);
        aborted.Close();
        var closed = AnthropicOAuthCallbackServer.Start("Anthropic", "127.0.0.1", 0, "/callback", "localhost", "st");
        closed.Close();
        Equal("OAuth callback server closed", (await Throws<InvalidOperationException>(() => closed.WaitAsync())).Message);
        await Throws<OperationCanceledException>(() => { AnthropicOAuthCallbackServer.Start("Anthropic", "127.0.0.1", 0, "/callback", null, null, abort.Token).Close(); return Task.CompletedTask; });
    }

    private static async Task MethodSelection()
    {
        var endpoint = TokenEndpoint();
        using var http = new HttpClient(endpoint);
        var cancelled = new Interaction { Select = (_, _, _) => Task.FromException<string>(new OperationCanceledException("Login cancelled")) };
        Equal("Login cancelled", (await Throws<OperationCanceledException>(() => new AnthropicOAuth(http).LoginAsync(cancelled))).Message);
        var unknown = new Interaction { Select = (_, _, _) => Task.FromResult("device") };
        Equal("Unknown Anthropic login method: device", (await Throws<InvalidOperationException>(() => new AnthropicOAuth(http).LoginAsync(unknown))).Message);
        Require(endpoint.Requests.Count == 0 && cancelled.Events.Count == 0 && unknown.Events.Count == 0);
    }

    private static async Task TokenEndpointFailures()
    {
        using var failing = new HttpClient(Respond("""{"error":"invalid_grant","access_token":"SECRET_BODY"}""", HttpStatusCode.BadRequest));
        var oauth = new AnthropicOAuth(failing);
        var exchange = await Throws<InvalidOperationException>(() => oauth.ExchangeAuthorizationCodeAsync("code", "state", "verifier", AnthropicOAuth.RedirectUri));
        Require(exchange.Message.StartsWith("Token exchange request failed. url=https://platform.claude.com/v1/oauth/token; redirect_uri=http://localhost:53692/callback; response_type=authorization_code; details=HttpRequestException: HTTP request failed. status=400; url=https://platform.claude.com/v1/oauth/token", StringComparison.Ordinal));
        var refresh = await Throws<InvalidOperationException>(() => oauth.RefreshTokenAsync("refresh"));
        Require(refresh.Message.StartsWith("Anthropic token refresh request failed. url=https://platform.claude.com/v1/oauth/token; details=HttpRequestException: HTTP request failed. status=400", StringComparison.Ordinal));
        Require(!(exchange.ToString() + refresh).Contains("SECRET_BODY", StringComparison.Ordinal));
        using var invalid = new HttpClient(Respond("not json"));
        Equal("Token exchange returned invalid JSON. url=https://platform.claude.com/v1/oauth/token",
            (await Throws<InvalidOperationException>(() => new AnthropicOAuth(invalid).ExchangeAuthorizationCodeAsync("c", "s", "v", "r"))).Message);
        using var incomplete = new HttpClient(Respond("""{"access_token":"a"}"""));
        Equal("Anthropic token refresh returned invalid JSON. url=https://platform.claude.com/v1/oauth/token",
            (await Throws<InvalidOperationException>(() => new AnthropicOAuth(incomplete).RefreshTokenAsync("r"))).Message);
    }
}
