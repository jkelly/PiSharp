using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime.Mcp;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

// Source-only offline controls: synthetic state and explicitly supplied in-memory exchanges/handler.
internal static class McpOAuthDiscoveryRefreshTests
{
    internal sealed record OriginalEvidence(Task Original, AggregateException? Aggregate, Exception? Direct);
    private static readonly ConcurrentQueue<OriginalEvidence> originals = new();
    private static readonly ConcurrentDictionary<Task, OriginalEvidence> joined = new(ReferenceEqualityComparer.Instance);
    internal static OriginalEvidence[] CapturedOriginals => originals.ToArray();
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("mcp-oauth.discovery-exact-protected-resource-and-three-issuer-candidates", Discovery),
        ("mcp-oauth.issuer-and-resource-mismatch-prevent-token-effects", Validation),
        ("mcp-oauth.actual-runtime-refresh-persistence-and-rotated-token-no-second-refresh", RuntimeRefresh),
        ("mcp-oauth.concurrent-unauthorized-share-one-held-refresh-original-and-reentry-refuses", SharedRefresh),
        ("mcp-oauth.authorization-required-and-coded-token-error-remain-explicit", RequiredAndErrors),
        ("mcp-oauth.exact-store-and-exchange-original-multifault-and-faulted-oce", OriginalFaults)
    ];
    private static readonly Uri Server = new("https://offline.invalid/resource/mcp");
    private static readonly Uri Issuer = new("https://issuer.invalid/tenant");
    private static readonly Uri TokenEndpoint = new("https://issuer.invalid/token");
    private const string Resource = "{\"resource\":\"https://offline.invalid/resource\",\"authorization_servers\":[\"https://issuer.invalid/tenant\"],\"scopes_supported\":[\"read\"]}";
    private const string Metadata = "{\"issuer\":\"https://issuer.invalid/tenant\",\"authorization_endpoint\":\"https://issuer.invalid/authorize\",\"token_endpoint\":\"https://issuer.invalid/token\",\"response_types_supported\":[\"code\"],\"token_endpoint_auth_methods_supported\":[\"client_secret_post\"]}";
    private const string Tokens = "{\"access_token\":\"synthetic-new\",\"token_type\":\"Bearer\",\"expires_in\":\"60\"}";
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Require(bool value) { if (!value) throw new IOException("OAuth discovery/refresh control failed."); }
    private static async Task<Exception?> Join(Task original)
    {
        if (joined.TryGetValue(original, out var prior)) return prior.Aggregate ?? prior.Direct;
        Exception? direct = null; try { await original.ConfigureAwait(false); } catch (Exception error) { direct = error; }
        var aggregate = original.IsFaulted ? original.Exception : null; var record = new OriginalEvidence(original, aggregate, direct);
        joined.TryAdd(original, record); originals.Enqueue(record); return aggregate ?? direct;
    }
    private static void Throw(List<Exception> errors)
    { if (errors.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw(); if (errors.Count > 1) throw new AggregateException(errors); }
    private static IEnumerable<Exception> Graph(Exception root)
    {
        var queue = new Queue<Exception>(); var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var edges = 0; queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var value = queue.Dequeue(); if (!seen.Add(value)) continue; if (seen.Count > 1024) throw new IOException("Fixture exception node bound."); yield return value;
            IEnumerable<Exception> children = value is AggregateException all ? all.InnerExceptions : value.InnerException is { } inner ? [inner] : [];
            foreach (var child in children) { if (++edges > 4096) throw new IOException("Fixture exception edge bound."); queue.Enqueue(child); }
        }
    }
    private static bool ExactProtocolOutcome(Exception? error, Func<Exception, bool> terminal)
    {
        if (error is null) return false; var aggregates = 0; var wrappers = 0; var leaves = 0;
        foreach (var item in Graph(error))
        {
            if (item is AggregateException all) { if (all.InnerExceptions.Count != 1) return false; aggregates++; }
            else if (item is McpOAuthFlowOriginalException flow)
            {
                if (flow.Phase != "shared-refresh" || flow.Original is not { IsFaulted: true } || flow.Evidence is not AggregateException evidence ||
                    evidence.InnerExceptions.Count != 1 || !ReferenceEquals(flow.Direct, evidence.InnerExceptions[0]) || !ReferenceEquals(flow.InnerException, evidence)) return false;
                wrappers++;
            }
            else if (terminal(item)) leaves++;
            else return false;
        }
        return leaves == 1 && wrappers <= 1 && aggregates == wrappers + 1;
    }
    private static void Expected(bool condition, Exception? originalFailure)
    {
        if (condition) return; var predicate = new IOException("Expected OAuth outcome or complete failure inventory did not match.");
        if (originalFailure is not null) throw new AggregateException(predicate, originalFailure); throw predicate;
    }
    private sealed class Store : IMcpAdmittedOAuthStateStore
    {
        internal McpOAuthState? Current = new(Server.AbsoluteUri, Tokens: new("synthetic-old", "Bearer", RefreshToken: "synthetic-refresh"));
        internal int Loads, Saves; internal Task<McpOAuthState?>? LoadOriginal;
        internal Action? Saving;
        internal Func<Task>? SavingAsync;
        internal readonly List<Task> CallbackOriginals = [];
        public ValueTask<McpOAuthState?> LoadAsync() { Loads++; return LoadOriginal is { } original ? new(original) : ValueTask.FromResult(Current); }
        public ValueTask SaveAsync(McpOAuthState state)
        {
            Saves++; Saving?.Invoke(); Current = state;
            if (SavingAsync is not null) { var original = SavingAsync(); CallbackOriginals.Add(original); return new(original); }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class Exchange
    {
        internal readonly List<McpOAuthExchangeRequest> Requests = [];
        internal readonly List<Task<McpOAuthExchangeResponse>> UserOriginals = [];
        internal Func<McpOAuthExchangeRequest, Task<McpOAuthExchangeResponse>>? Override;
        internal ValueTask<McpOAuthExchangeResponse> Run(McpOAuthExchangeRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests.Add(request);
            var original = Override?.Invoke(request) ?? Task.FromResult(new McpOAuthExchangeResponse(200, request.Purpose switch
            { McpOAuthExchangePurpose.ProtectedResourceMetadata => Resource, McpOAuthExchangePurpose.AuthorizationServerMetadata => Metadata, _ => Tokens }));
            UserOriginals.Add(original); return new(original);
        }
        internal async Task Cleanup(List<Exception> errors, bool expectedFault = false)
        {
            foreach (var task in UserOriginals.Distinct<Task<McpOAuthExchangeResponse>>(ReferenceEqualityComparer.Instance))
            { var error = await Join(task); if (error is not null && (!expectedFault || errors.Count > 0)) errors.Add(error); }
        }
    }
    private static McpHttpUnauthorizedContext Context(string? rejected = "synthetic-old", string? challenge = "Bearer")
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized); if (challenge is not null) response.Headers.TryAddWithoutValidation("WWW-Authenticate", challenge);
        return new(response, Server, rejected);
    }
    private static McpAdmittedOAuthRefreshAdapter Adapter(Store store, Exchange exchange, McpOAuthCancellationAdmission? cancellation = null) => new(Server, store, exchange.Run, () => 1000, cancellation ?? new(), new("synthetic-client", "synthetic-secret", "client_secret_post"));
    private static async Task Discovery()
    {
        await MalformedResourceFallback();
        var exchange = new Exchange(); var errors = new List<Exception>(); var ordinal = 0;
        exchange.Override = request => Task.FromResult(new McpOAuthExchangeResponse(++ordinal switch { 1 => 404, 3 => 404, 4 => 502, _ => 200 }, ordinal == 2 ? Resource : Metadata));
        try
        {
            var original = McpAdmittedOAuthDiscovery.DiscoverAsync(Server, exchange.Run); var error = await Join(original); if (error is not null) throw error;
            var result = original.Result; Require(result.AuthorizationServerUrl == Issuer.AbsoluteUri && result.AuthorizationServerMetadata is not null && result.ResourceMetadata is not null);
            Require(exchange.Requests.Select(item => item.Endpoint.AbsoluteUri).SequenceEqual(new[] {
                "https://offline.invalid/.well-known/oauth-protected-resource/resource/mcp", "https://offline.invalid/.well-known/oauth-protected-resource",
                "https://issuer.invalid/.well-known/oauth-authorization-server/tenant", "https://issuer.invalid/.well-known/openid-configuration/tenant", "https://issuer.invalid/tenant/.well-known/openid-configuration" }));
            Require(exchange.Requests.All(item => item.Method == HttpMethod.Get && item.Body is null && item.Headers["Accept"] == "application/json" && item.Headers["MCP-Protocol-Version"] == "2025-11-25"));
        }
        catch (Exception error) { errors.Add(error); } finally { await exchange.Cleanup(errors); }
        Throw(errors);
    }
    private static async Task MalformedResourceFallback()
    {
        foreach (var body in new[] { "{\"resource\":\"not-a-url\"}", "{\"resource\":\"https://offline.invalid/resource\",\"authorization_servers\":[\"not-a-url\"]}" })
        {
            var exchange = new Exchange { Override = request => Task.FromResult(new McpOAuthExchangeResponse(200,
                request.Purpose == McpOAuthExchangePurpose.ProtectedResourceMetadata ? body : Metadata.Replace(Issuer.AbsoluteUri, "https://offline.invalid/", StringComparison.Ordinal))) };
            var errors = new List<Exception>();
            try
            {
                var original = McpAdmittedOAuthDiscovery.DiscoverAsync(Server, exchange.Run); var error = await Join(original); if (error is not null) throw error;
                Require(original.Result.ResourceMetadata is null && original.Result.AuthorizationServerUrl == "https://offline.invalid/" && exchange.Requests.Count == 2 &&
                    exchange.Requests[1].Endpoint.AbsoluteUri == "https://offline.invalid/.well-known/oauth-authorization-server");
            }
            catch (Exception error) { errors.Add(error); } finally { await exchange.Cleanup(errors); }
            Throw(errors);
        }
    }
    private static async Task Validation()
    {
        foreach (var issuer in new[] { true, false })
        {
            var store = new Store(); var exchange = new Exchange(); var errors = new List<Exception>(); var context = Context();
            exchange.Override = request => Task.FromResult(new McpOAuthExchangeResponse(200, request.Purpose == McpOAuthExchangePurpose.ProtectedResourceMetadata
                ? issuer ? Resource : Resource.Replace("https://offline.invalid/resource\"", "https://other.invalid/resource\"", StringComparison.Ordinal)
                : Metadata.Replace("\"issuer\":\"https://issuer.invalid/tenant\"", "\"issuer\":\"https://wrong.invalid/tenant\"", StringComparison.Ordinal)));
            try
            {
                // Resource mismatch keeps a matching issuer; issuer mismatch refuses discovery first.
                if (!issuer) exchange.Override = request => Task.FromResult(new McpOAuthExchangeResponse(200, request.Purpose == McpOAuthExchangePurpose.ProtectedResourceMetadata ? Resource.Replace("https://offline.invalid/resource\"", "https://other.invalid/resource\"", StringComparison.Ordinal) : Metadata));
                var original = Adapter(store, exchange).CreateAuthentication().OnUnauthorized!(context, default).AsTask(); var error = await Join(original);
                Expected(original.IsFaulted && ExactProtocolOutcome(error, item => item is McpOAuthProtocolException protocol && protocol.Code == (issuer ? "issuer_mismatch" : "resource_mismatch")) && exchange.Requests.All(item => item.Purpose != McpOAuthExchangePurpose.RefreshToken) && store.Current?.Tokens?.AccessToken == "synthetic-old", error);
            }
            catch (Exception error) { errors.Add(error); }
            finally { await exchange.Cleanup(errors); try { context.Response.Dispose(); } catch (Exception error) { errors.Add(error); } }
            Throw(errors);
        }
    }
    private sealed class RuntimeServer : HttpMessageHandler
    {
        internal int Calls; internal readonly List<string> Authorization = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Require(request.RequestUri == Server); Authorization.Add(request.Headers.Authorization?.ToString() ?? ""); var ordinal = ++Calls;
            var body = await (request.Content ?? throw new IOException("No actual runtime body.")).ReadAsStringAsync(token); var value = JsonData.Parse(body).Value;
            if (!value.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            var result = "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"serverInfo\":{\"name\":\"offline-refresh\",\"version\":\"1\"}}";
            var response = new HttpResponseMessage(ordinal == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK)
            { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}", Encoding.UTF8, "application/json") };
            if (ordinal == 1) response.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer"); return response;
        }
    }
    private static async Task RuntimeRefresh()
    {
        var store = new Store(); var exchange = new Exchange(); var adapter = Adapter(store, exchange); var auth = adapter.CreateAuthentication();
        var handler = new RuntimeServer(); var client = new HttpClient(handler, false); McpServerRuntime? runtime = null; var errors = new List<Exception>(); var context = Context();
        try
        {
            var entry = new McpServerEntry("refresh", McpConfigurationReader.Validate("refresh", JsonData.Parse("{\"url\":\"https://offline.invalid/resource/mcp\"}").Value).Config!, "admitted-offline", McpConfigurationScope.Extension);
            var options = new McpRuntimeOptions(1, "0.99.1");
            var factory = AdmittedMcpHttpChannelFactory.Create(entry, new(Server, ImmutableDictionary<string, string>.Empty, new(false, false)), client, options, authentication: auth);
            runtime = new(entry, options, factory, (publication, _) => ValueTask.FromResult(new McpCatalogPublicationReceipt(publication.Current.Generation, publication.Current.Revision, true)));
            var original = runtime.ConnectAsync(); var error = await Join(original); if (error is not null) throw error;
            Require(runtime.Snapshot.Catalog.Connected && handler.Calls == 3 && handler.Authorization.SequenceEqual(new[] { "Bearer synthetic-old", "Bearer synthetic-new", "Bearer synthetic-new" }));
            var refresh = exchange.Requests.Single(item => item.Purpose == McpOAuthExchangePurpose.RefreshToken);
            Require(refresh.Endpoint == TokenEndpoint && refresh.Method == HttpMethod.Post && refresh.Headers["Content-Type"] == "application/x-www-form-urlencoded" && !refresh.Headers.ContainsKey("Authorization") &&
                refresh.Body == "grant_type=refresh_token&refresh_token=synthetic-refresh&resource=https%3A%2F%2Foffline.invalid%2Fresource&client_id=synthetic-client&client_secret=synthetic-secret");
            Require(store.Saves == 2 && store.Current?.Tokens is { AccessToken: "synthetic-new", RefreshToken: "synthetic-refresh", ExpiresIn: 60 } && store.Current!.TokensExpireAt == 61000 && store.Current!.Discovery is not null);
            var count = exchange.Requests.Count; var repeated = auth.OnUnauthorized!(context, default).AsTask(); error = await Join(repeated); if (error is not null) throw error;
            Require(exchange.Requests.Count == count && store.Saves == 2);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            if (runtime is not null) try { var original = runtime.DisposeAsync().AsTask(); var error = await Join(original); if (error is not null) errors.Add(error); } catch (Exception error) { errors.Add(error); }
            await exchange.Cleanup(errors);
            foreach (var dispose in new Action[] { context.Response.Dispose, client.Dispose, handler.Dispose }) try { dispose(); } catch (Exception error) { errors.Add(error); }
        }
        Throw(errors);
    }
    private static async Task WaitSignal(Task signal, Task original)
    {
        var source = new CancellationTokenSource(); var deadline = Task.Delay(TimeSpan.FromSeconds(10), source.Token); var errors = new List<Exception>();
        try { var winner = await Task.WhenAny(signal, original, deadline); if (!ReferenceEquals(winner, signal)) throw new IOException("Shared refresh original ended before held entry.", original.IsFaulted ? original.Exception : null); await signal; }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            try { var cancel = source.CancelAsync(); var error = await Join(cancel); if (error is not null) errors.Add(error); } catch (Exception error) { errors.Add(error); }
            var timer = await Join(deadline); if (deadline.IsFaulted && timer is not null) errors.Add(timer); try { source.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        Throw(errors);
    }
    private static async Task SharedRefresh()
    {
        await NormalRegisterAncestry();
        var store = new Store(); var exchange = new Exchange(); var entered = Gate(); var held = new TaskCompletionSource<McpOAuthExchangeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        McpAdmittedHttpAuthentication? auth = null; Task? reentry = null; var tokenCalls = 0; var errors = new List<Exception>(); Task? first = null, second = null; var context = Context();
        exchange.Override = request =>
        {
            if (request.Purpose != McpOAuthExchangePurpose.RefreshToken) return Task.FromResult(new McpOAuthExchangeResponse(200, request.Purpose == McpOAuthExchangePurpose.ProtectedResourceMetadata ? Resource : Metadata));
            tokenCalls++; reentry = auth!.Token(default).AsTask(); entered.TrySetResult(); return held.Task;
        };
        auth = Adapter(store, exchange).CreateAuthentication();
        try
        {
            first = auth.OnUnauthorized!(context, default).AsTask(); await WaitSignal(entered.Task, first);
            second = auth.OnUnauthorized!(context, default).AsTask(); Require(!first.IsCompleted && !second.IsCompleted && tokenCalls == 1);
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            held.TrySetResult(new(200, Tokens));
            foreach (var original in new[] { first, second }) if (original is not null) { var error = await Join(original); if (error is not null) errors.Add(error); }
            if (reentry is not null) { var error = await Join(reentry); if (error is null || reentry.IsCanceled || !Graph(error).Any(item => item is InvalidOperationException)) errors.Add(error ?? new IOException("Provider reentry was not refused.")); }
            await exchange.Cleanup(errors); try { context.Response.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        try { Require(tokenCalls == 1 && store.Saves == 2 && store.Current?.Tokens?.AccessToken == "synthetic-new"); } catch (Exception error) { errors.Add(error); }
        Throw(errors);
    }
    private static async Task NormalRegisterAncestry()
    {
        foreach (var mode in Enumerable.Range(0, 8))
        {
            var duringSave = mode == 1 || mode >= 4;
            var marker = new AsyncLocal<object?>(); var previous = marker.Value; var captured = new object(); marker.Value = captured;
            using var cancellation = new CancellationTokenSource(); var errors = new List<Exception>(); var rejected = new List<Task>();
            var scope = new McpOAuthCancellationAdmission(); var store = new Store(); var exchange = new Exchange(); var context = Context(); var auth = Adapter(store, exchange, scope).CreateAuthentication();
            var otherStore = new Store(); var otherAuth = Adapter(otherStore, new Exchange()).CreateAuthentication();
            var nestedScope = new McpOAuthCancellationAdmission(); var nestedStore = new Store(); var nestedAuth = Adapter(nestedStore, new Exchange(), nestedScope).CreateAuthentication();
            Task? main = null, unrelated = null, cancellationOriginal = null; var invocations = 0; var loadsBefore = 0;
            using var registration = cancellation.Token.Register(() =>
            {
                invocations++; loadsBefore = store.Loads;
                try { Require(ReferenceEquals(marker.Value, captured)); } catch (Exception error) { errors.Add(error); }
                // Never block on an unexpectedly accepted self-join: retain and join it after the actual owner returns.
                var candidates = new List<Task> { auth.Token(default).AsTask(), auth.OnUnauthorized!(context, default).AsTask() };
                if (mode >= 6) { candidates.Add(nestedAuth.Token(default).AsTask()); candidates.Add(nestedAuth.OnUnauthorized!(context, default).AsTask()); }
                foreach (var original in candidates)
                {
                    rejected.Add(original);
                    if (!original.IsCompleted) { errors.Add(new IOException("Frame-free cancellation reentry was admitted.")); continue; }
                    Exception? direct = null;
                    try { original.GetAwaiter().GetResult(); errors.Add(new IOException("Cancellation reentry succeeded.")); }
                    catch (InvalidOperationException error) { direct = error; }
                    catch (Exception error) { direct = error; errors.Add(error); }
                    finally
                    {
                        var record = new OriginalEvidence(original, original.IsFaulted ? original.Exception : null, direct);
                        joined.TryAdd(original, record); originals.Enqueue(record);
                    }
                }
                unrelated = otherAuth.Token(default).AsTask();
                try { Require(unrelated.IsCompletedSuccessfully && store.Loads == loadsBefore); }
                catch (Exception error) { errors.Add(error); }
            });
            marker.Value = new object();
            async Task CancelAfterAwait()
            {
                await Task.Yield();
                var admitted = mode >= 6 ? nestedScope : scope;
                if (mode is 3 or 5 or 7)
                {
                    cancellationOriginal = admitted.CancelAsync(cancellation);
                    var error = await Join(cancellationOriginal); if (error is not null) throw error;
                }
                else admitted.Cancel(cancellation);
            }
            if (duringSave && mode == 1) store.Saving = () => cancellation.Cancel();
            else if (duringSave) store.SavingAsync = CancelAfterAwait;
            else if (mode >= 2) exchange.Override = async request =>
            {
                if (request.Purpose == McpOAuthExchangePurpose.RefreshToken) await CancelAfterAwait();
                return new(200, request.Purpose switch
                { McpOAuthExchangePurpose.ProtectedResourceMetadata => Resource, McpOAuthExchangePurpose.AuthorizationServerMetadata => Metadata, _ => Tokens });
            };
            else exchange.Override = request =>
            {
                if (request.Purpose == McpOAuthExchangePurpose.RefreshToken) cancellation.Cancel();
                return Task.FromResult(new McpOAuthExchangeResponse(200, request.Purpose switch
                { McpOAuthExchangePurpose.ProtectedResourceMetadata => Resource, McpOAuthExchangePurpose.AuthorizationServerMetadata => Metadata, _ => Tokens }));
            };
            try { main = auth.OnUnauthorized!(context, default).AsTask(); var error = await Join(main); if (error is not null) errors.Add(error); }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                foreach (var original in rejected)
                {
                    var error = await Join(original);
                    if (error is null || original.IsCanceled || error is not AggregateException { InnerExceptions.Count: 1 } aggregate || aggregate.InnerExceptions[0] is not InvalidOperationException)
                        errors.Add(error ?? new IOException("Expected exact reentry refusal original."));
                }
                if (unrelated is not null) { var error = await Join(unrelated); if (error is not null) errors.Add(error); }
                if (cancellationOriginal is not null) { var error = await Join(cancellationOriginal); if (error is not null) errors.Add(error); }
                foreach (var original in store.CallbackOriginals) { var error = await Join(original); if (error is not null) errors.Add(error); }
                await exchange.Cleanup(errors); try { context.Response.Dispose(); } catch (Exception error) { errors.Add(error); }
                marker.Value = previous;
            }
            try { Require(invocations == 1 && rejected.Count == (mode >= 6 ? 4 : 2) && main?.IsCompletedSuccessfully == true && otherStore.Loads == 1 && nestedStore.Loads == 0 && store.Saves == 2); }
            catch (Exception error) { errors.Add(error); }
            Throw(errors);
        }
    }
    private static async Task RequiredAndErrors()
    {
        await EmptyOptionalTokens();
        foreach (var insufficient in new[] { true, false })
        {
            var store = new Store(); if (!insufficient) store.Current = store.Current! with { Tokens = new("synthetic-old", "Bearer") };
            var exchange = new Exchange(); var context = Context(challenge: insufficient ? "Bearer error=\"insufficient_scope\"" : "Bearer"); var errors = new List<Exception>();
            try
            {
                var original = Adapter(store, exchange).CreateAuthentication().OnUnauthorized!(context, default).AsTask(); var error = await Join(original);
                Expected(original.IsFaulted && ExactProtocolOutcome(error, item => item is McpOAuthAuthorizationRequiredException) && exchange.Requests.All(item => item.Purpose != McpOAuthExchangePurpose.RefreshToken) && store.Saves == (insufficient ? 0 : 1), error);
            }
            catch (Exception error) { errors.Add(error); } finally { await exchange.Cleanup(errors); try { context.Response.Dispose(); } catch (Exception error) { errors.Add(error); } }
            Throw(errors);
        }
        var failing = new Exchange { Override = request => Task.FromResult(new McpOAuthExchangeResponse(200, request.Purpose switch
            { McpOAuthExchangePurpose.ProtectedResourceMetadata => Resource, McpOAuthExchangePurpose.AuthorizationServerMetadata => Metadata, _ => "{\"error\":\"invalid_grant\",\"error_description\":\"synthetic failure\"}" })) };
        var state = new Store(); var errorContext = Context(); var failures = new List<Exception>();
        try
        {
            var original = Adapter(state, failing).CreateAuthentication().OnUnauthorized!(errorContext, default).AsTask(); var error = await Join(original);
            Expected(original.IsFaulted && ExactProtocolOutcome(error, item => item is McpOAuthProtocolException protocol && protocol.Code == "invalid_grant") && state.Saves == 1 && state.Current?.Tokens?.AccessToken == "synthetic-old", error);
        }
        catch (Exception error) { failures.Add(error); } finally { await failing.Cleanup(failures); try { errorContext.Response.Dispose(); } catch (Exception error) { failures.Add(error); } }
        Throw(failures);
        await AdditionalTokenContracts();
    }
    private static async Task EmptyOptionalTokens()
    {
        foreach (var field in new[] { "refresh_token", "scope", "id_token" })
        {
            var malformed = JsonSerializer.Serialize(new Dictionary<string, string> { ["access_token"] = "synthetic-new", ["token_type"] = "Bearer", [field] = "" });
            var exchange = new Exchange { Override = request => Task.FromResult(new McpOAuthExchangeResponse(200, request.Purpose switch
            { McpOAuthExchangePurpose.ProtectedResourceMetadata => Resource, McpOAuthExchangePurpose.AuthorizationServerMetadata => Metadata, _ => malformed })) };
            var store = new Store(); var context = Context(); var errors = new List<Exception>();
            try
            {
                var original = Adapter(store, exchange).CreateAuthentication().OnUnauthorized!(context, default).AsTask(); var error = await Join(original);
                // Pi v1.1.0 (packages/mcp/src/oauth/types.ts): servers send `""` for optional fields they have no value
                // for, so an empty optional token field is absent rather than malformed (v0.99.1 rejected it).
                Expected(error is null && !original.IsFaulted && store.Saves == 2 && store.Current?.Tokens?.AccessToken == "synthetic-new" &&
                    store.Current?.Tokens?.RefreshToken == "synthetic-refresh" && store.Current?.Tokens?.Scope is null && store.Current?.Tokens?.IdToken is null, error);
            }
            catch (Exception error) { errors.Add(error); }
            finally { await exchange.Cleanup(errors); try { context.Response.Dispose(); } catch (Exception error) { errors.Add(error); } }
            Throw(errors);
        }
    }
    private static async Task AdditionalTokenContracts()
    {
        // Pi v1.1.0: `expires_in: null` is absent (no expiry); v0.99.1 coerced it to 0, an immediately expired token.
        foreach (var row in new[] { (Json: "null", Seconds: (double?)null), (Json: "\"0x10\"", Seconds: 16d), (Json: "[\"2\"]", Seconds: 2d) })
        {
            var store = new Store(); var exchange = new Exchange(); var context = Context(); var errors = new List<Exception>();
            exchange.Override = request => Task.FromResult(new McpOAuthExchangeResponse(200, request.Purpose switch
            { McpOAuthExchangePurpose.ProtectedResourceMetadata => Resource, McpOAuthExchangePurpose.AuthorizationServerMetadata => Metadata.Replace("client_secret_post", "client_secret_basic", StringComparison.Ordinal),
                _ => "{\"access_token\":\"synthetic-rotated\",\"token_type\":\"Bearer\",\"refresh_token\":\"synthetic-next-refresh\",\"expires_in\":" + row.Json + "}" }));
            try
            {
                var auth = new McpAdmittedOAuthRefreshAdapter(Server, store, exchange.Run, () => 1000, new McpOAuthCancellationAdmission(), new("synthetic-client", "synthetic-secret", "client_secret_basic")).CreateAuthentication();
                var original = auth.OnUnauthorized!(context, default).AsTask(); var error = await Join(original); if (error is not null) throw error;
                var request = exchange.Requests.Single(item => item.Purpose == McpOAuthExchangePurpose.RefreshToken);
                Require(request.Headers["Authorization"] == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-client:synthetic-secret")) && request.Body is not null && !request.Body.Contains("client_secret=", StringComparison.Ordinal) &&
                    !request.Body.Contains("client_id=", StringComparison.Ordinal) && store.Current?.Tokens?.RefreshToken == "synthetic-next-refresh" && store.Current?.TokensExpireAt == 1000 + row.Seconds * 1000);
            }
            catch (Exception error) { errors.Add(error); } finally { await exchange.Cleanup(errors); try { context.Response.Dispose(); } catch (Exception error) { errors.Add(error); } }
            Throw(errors);
        }
        var foreign = new Store { Current = new("https://different.invalid/mcp", Tokens: new("never-deliver-foreign", "Bearer", RefreshToken: "never-refresh-foreign")) };
        var unused = new Exchange(); var failures = new List<Exception>();
        try { var original = Adapter(foreign, unused).CreateAuthentication().Token(default).AsTask(); var error = await Join(original); if (error is not null) throw error; Require(original.Result is null && unused.Requests.Count == 0 && foreign.Saves == 0); }
        catch (Exception error) { failures.Add(error); } finally { await unused.Cleanup(failures); }
        Throw(failures);
    }
    private static async Task CanceledExchange()
    {
        var held = new TaskCompletionSource<McpOAuthExchangeResponse>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = Gate();
        var exchange = new Exchange { Override = _ => { entered.TrySetResult(); return held.Task; } }; var store = new Store(); var context = Context();
        var source = new CancellationTokenSource(); Task? original = null; var errors = new List<Exception>(); Exception? observed = null;
        try
        {
            original = Adapter(store, exchange).CreateAuthentication().OnUnauthorized!(context, source.Token).AsTask(); await WaitSignal(entered.Task, original);
            var cancellation = source.CancelAsync(); var error = await Join(cancellation); if (error is not null) throw error;
            Require(!original.IsCompleted && !held.Task.IsCompleted); // Cancellation does not detach admitted callback work.
        }
        catch (Exception error) { errors.Add(error); }
        finally
        {
            held.TrySetCanceled(source.Token);
            if (original is not null) { observed = await Join(original); if (errors.Count > 0 && observed is not null) errors.Add(observed); }
            await exchange.Cleanup(errors, expectedFault: true);
            try { context.Response.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { source.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        try
        {
            Expected(original is { IsCanceled: true, IsFaulted: false } && held.Task.IsCanceled && observed is McpOAuthFlowCanceledException canceled &&
                canceled.InnerException is McpOAuthFlowCanceledException nested && ReferenceEquals(nested.Original, held.Task) &&
                nested.Direct.CancellationToken == canceled.CancellationToken && store.Saves == 0 && exchange.Requests.Count == 1, observed);
        }
        catch (Exception error) { errors.Add(error); }
        Throw(errors);
    }
    private static async Task OriginalFaults()
    {
        await OwnedCancellationFaults();
        var leaf = new IOException("shared stored token fault"); var nested = new AggregateException(leaf, leaf); var graph = new AggregateException(leaf, nested);
        var load = Task.FromException<McpOAuthState?>(graph); var store = new Store { LoadOriginal = load }; var exchange = new Exchange(); var errors = new List<Exception>();
        try
        {
            var original = Adapter(store, exchange).CreateAuthentication().Token(default).AsTask(); var error = await Join(original);
            var outer = error as AggregateException; var flow = outer is { InnerExceptions.Count: 1 } ? outer.InnerExceptions[0] as McpOAuthFlowOriginalException : null;
            var owned = flow?.Evidence as AggregateException; var stored = owned is { InnerExceptions.Count: 1 } ? owned.InnerExceptions[0] as McpOAuthStoreException : null;
            var raw = stored?.Evidence as AggregateException;
            Expected(original.IsFaulted && !original.IsCanceled && flow is { Phase: "state-read" } && ReferenceEquals(flow.Direct, stored) && stored is not null && ReferenceEquals(stored.Original, load) &&
                raw is { InnerExceptions.Count: 1 } && ReferenceEquals(raw.InnerExceptions[0], graph) && graph.InnerExceptions.Count == 2 && ReferenceEquals(graph.InnerExceptions[0], leaf) &&
                ReferenceEquals(graph.InnerExceptions[1], nested) && nested.InnerExceptions.Count == 2 && ReferenceEquals(nested.InnerExceptions[0], leaf) && ReferenceEquals(nested.InnerExceptions[1], leaf) && exchange.Requests.Count == 0, error);
        }
        catch (Exception error) { errors.Add(error); }
        finally { var error = await Join(load); if (errors.Count > 0 && error is not null) errors.Add(error); await exchange.Cleanup(errors); }
        Throw(errors);
        var faultedOce = new OperationCanceledException("unrequested HTTP original"); var http = Task.FromException<McpOAuthExchangeResponse>(faultedOce); var broken = new Exchange { Override = _ => http }; var context = Context(); var failures = new List<Exception>();
        try
        {
            var original = Adapter(new(), broken).CreateAuthentication().OnUnauthorized!(context, default).AsTask(); var error = await Join(original);
            var outer = error as AggregateException; var sharing = outer is { InnerExceptions.Count: 1 } ? outer.InnerExceptions[0] as McpOAuthFlowOriginalException : null;
            var flight = sharing?.Evidence as AggregateException; var exchangeFault = flight is { InnerExceptions.Count: 1 } ? flight.InnerExceptions[0] as McpOAuthFlowOriginalException : null;
            var raw = exchangeFault?.Evidence as AggregateException;
            Expected(original.IsFaulted && !original.IsCanceled && sharing is { Phase: "shared-refresh" } && ReferenceEquals(sharing.Direct, exchangeFault) &&
                exchangeFault is { Phase: "http-exchange" } && ReferenceEquals(exchangeFault.Original, http) && ReferenceEquals(exchangeFault.Direct, faultedOce) &&
                raw is { InnerExceptions.Count: 1 } && ReferenceEquals(raw.InnerExceptions[0], faultedOce) && broken.Requests.Count == 1, error);
        }
        catch (Exception error) { failures.Add(error); }
        finally { await broken.Cleanup(failures, expectedFault: true); try { context.Response.Dispose(); } catch (Exception error) { failures.Add(error); } }
        Throw(failures);
        await CanceledExchange();
    }
    private static async Task OwnedCancellationFaults()
    {
        using var source = new CancellationTokenSource(); var left = new IOException("synthetic cancel shared leaf"); var oce = new OperationCanceledException("synthetic faulted cancellation callback");
        var nested = new AggregateException(left, left); using var first = source.Token.Register(() => throw oce); using var second = source.Token.Register(() => throw nested);
        var scope = new McpOAuthCancellationAdmission(); var exchange = new Exchange(); var store = new Store(); var context = Context(); var errors = new List<Exception>(); Task? cancelOriginal = null; Exception? outcome = null;
        exchange.Override = async request =>
        {
            if (request.Purpose != McpOAuthExchangePurpose.RefreshToken) return new(200, request.Purpose == McpOAuthExchangePurpose.ProtectedResourceMetadata ? Resource : Metadata);
            await Task.Yield(); cancelOriginal = scope.CancelAsync(source);
            // The exact aggregate is deliberately passed through; the fixture records this cancellation original before propagating.
            var failure = await Join(cancelOriginal); if (failure is not null) throw failure;
            throw new IOException("Cancellation faults disappeared.");
        };
        try
        {
            var original = Adapter(store, exchange, scope).CreateAuthentication().OnUnauthorized!(context, default).AsTask(); var observed = outcome = await Join(original);
            var captured = CapturedOriginals.SingleOrDefault(item => ReferenceEquals(item.Original, cancelOriginal));
            var aggregate = captured?.Aggregate;
            var rawCancel = aggregate is { InnerExceptions.Count: 1 } ? aggregate.InnerExceptions[0] as AggregateException : null;
            var exact = cancelOriginal is { IsFaulted: true, IsCanceled: false } && rawCancel is { InnerExceptions.Count: 2 } &&
                rawCancel.InnerExceptions.Any(item => ReferenceEquals(item, oce)) && rawCancel.InnerExceptions.Any(item => ReferenceEquals(item, nested)) &&
                nested.InnerExceptions.Count == 2 && nested.InnerExceptions.All(item => ReferenceEquals(item, left));
            var outer = observed as AggregateException;
            var shared = outer is { InnerExceptions.Count: 1 } ? outer.InnerExceptions[0] as McpOAuthFlowOriginalException : null;
            var refresh = shared?.Evidence as AggregateException;
            var http = refresh is { InnerExceptions.Count: 1 } ? refresh.InnerExceptions[0] as McpOAuthFlowOriginalException : null;
            var exchangeFailure = http?.Evidence as AggregateException;
            Expected(exact && original.IsFaulted && !original.IsCanceled && shared is { Phase: "shared-refresh" } &&
                ReferenceEquals(shared.Direct, http) && http is { Phase: "http-exchange" } &&
                ReferenceEquals(http.Direct, aggregate) && exchangeFailure is { InnerExceptions.Count: 1 } &&
                ReferenceEquals(exchangeFailure.InnerExceptions[0], aggregate) && Graph(observed!).Count() == 10 && store.Saves == 1, observed);
        }
        catch (Exception error) { errors.Add(error); if (outcome is not null && !ReferenceEquals(error, outcome)) errors.Add(outcome); }
        finally
        {
            if (cancelOriginal is not null) _ = await Join(cancelOriginal);
            // Expected exchange failure is acknowledged through the exact cancellation aggregate above; extra exchange originals must not be discarded.
            foreach (var task in exchange.UserOriginals)
            {
                var error = await Join(task);
                if (error is not null && (cancelOriginal is null || error is not AggregateException { InnerExceptions.Count: 1 } taskFailure ||
                    !ReferenceEquals(taskFailure.InnerExceptions[0], CapturedOriginals.Single(item => ReferenceEquals(item.Original, cancelOriginal)).Aggregate))) errors.Add(error);
            }
            try { context.Response.Dispose(); } catch (Exception error) { errors.Add(error); }
        }
        Throw(errors);
    }
}
