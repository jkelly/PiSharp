using System.Collections.Immutable;
using System.Net;
using System.Text;
using PiSharp.Cli.Mcp.Authentication;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.CodingAgent.Tests;

/// <summary>Supplied exchanges/browser/store only. These are authored controls, never live acquisition.</summary>
internal static class McpDefaultOAuthHostControls
{
    private static readonly object recordsGate = new();
    private static readonly List<(string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)> records = [];
    private static readonly Dictionary<Task, AggregateException?> cached = new(ReferenceEqualityComparer.Instance);
    private sealed class OriginalsFailure(Exception[] failures,
        (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] originals)
        : IOException("Default OAuth fixture primary and cleanup originals retained.", new AggregateException(failures))
    { internal (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] RawOriginals { get; } = originals; }
    internal static (string Phase, Task Original, AggregateException? Aggregate, Exception? Direct)[] CapturedOriginals
    { get { lock (recordsGate) return records.ToArray(); } }
    internal static IEnumerable<(string Name, Func<Task> Test)> Cases()
    {
        yield return ("default OAuth registration/PKCE/browser composition persists each value once", Registration);
        yield return ("default OAuth validated code exchange translates tokens without duplicate persistence", Code);
        yield return ("default OAuth wire invalid-grant retry is distinct from local malformed-token fallback", Provenance);
        yield return ("default OAuth endpoint admission refuses before physical HTTP and callback reentry", Admission);
        yield return ("default OAuth held browser original joins cancellation and stable close", HeldBrowser);
        yield return ("default OAuth concurrent unauthorized requests share actual refresh and retain multifault/OCE originals", SharedAndFaults);
    }
    private sealed class Store : IMcpAdmittedOAuthStateStore
    {
        internal McpOAuthState? Value { get; set; }
        internal List<McpOAuthState> Writes { get; } = [];
        internal Task? SaveOriginal { get; set; }
        public ValueTask<McpOAuthState?> LoadAsync() => ValueTask.FromResult(Value);
        public ValueTask SaveAsync(McpOAuthState state) { if (SaveOriginal is { } original) return new(original); Value = state; Writes.Add(state); return ValueTask.CompletedTask; }
    }
    private sealed class Physical(Func<CancellationToken, ValueTask<HttpResponseMessage>> send, Func<Task>? stop = null) : IMcpAdmittedHttpRequestOperation
    {
        public ValueTask<HttpResponseMessage> SendAsync(CancellationToken token) => send(token);
        public Task StopAsync() => stop?.Invoke() ?? Task.CompletedTask;
    }
    private sealed class FailedBody(Task<int> original) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(original);
    }
    private sealed class LateOwnedContent(Exception disposalFault) : HttpContent
    {
        internal bool Disposed { get; private set; }
        protected override bool TryComputeLength(out long length) { length = 0; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
        protected override void Dispose(bool disposing)
        {
            if (disposing && !Disposed) { Disposed = true; base.Dispose(disposing); throw disposalFault; }
            base.Dispose(disposing);
        }
    }
    private static readonly Uri Server = new("https://mcp.example.test/mcp");
    private static readonly Uri Callback = new("https://application.example.test/oauth/callback");
    private const string Metadata = "{\"issuer\":\"https://issuer.example.test/\",\"authorization_endpoint\":\"https://issuer.example.test/authorize\",\"token_endpoint\":\"https://issuer.example.test/token\",\"registration_endpoint\":\"https://issuer.example.test/register\",\"response_types_supported\":[\"code\"],\"code_challenge_methods_supported\":[\"S256\"],\"token_endpoint_auth_methods_supported\":[\"none\"]}";
    private static McpOAuthState Cached(string? refresh = null) => new(Server.AbsoluteUri,
        JsonData.Parse("{\"client_id\":\"admitted-client\"}"),
        refresh is null ? null : new("old", "Bearer", RefreshToken: refresh), CodeVerifier: "stored-verifier",
        OAuthState: "admitted-state", Discovery: JsonData.Parse("{\"authorizationServerUrl\":\"https://issuer.example.test/\",\"authorizationServerMetadata\":" + Metadata + "}"));
    private static McpDefaultOAuthHostResources Resources(Store store, McpAdmittedHttpRequestFactory physical,
        Func<Uri, CancellationToken, ValueTask>? redirect = null) => new(Server, Callback,
            JsonData.Parse("{\"redirect_uris\":[\"https://application.example.test/oauth/callback\"],\"client_name\":\"admitted fixture\",\"scope\":\"offline_access read\"}"),
            store, () => 1000, entropyToken => ValueTask.FromResult(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()),
            redirect ?? ((_, _) => ValueTask.CompletedTask),
            (state, _) => state == "admitted-state" ? ValueTask.CompletedTask : ValueTask.FromException(new InvalidOperationException("response-state mismatch")),
            physical, (endpoint, purpose) => true, new(), AuthorizationState: stateToken => ValueTask.FromResult<string?>("admitted-state"));
    private static HttpResponseMessage Response(int status, string text) => new((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<T> Own<T>(string phase, Task<T> original)
    {
        Exception? direct = null;
        try { return await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; throw; }
        finally { Record(phase, original, direct); }
    }
    private static async Task Own(string phase, Task original)
    {
        Exception? direct = null;
        try { await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; throw; }
        finally { Record(phase, original, direct); }
    }
    private static void Record(string phase, Task original, Exception? direct)
    {
        lock (recordsGate)
        {
            if (!cached.TryGetValue(original, out var aggregate)) { aggregate = original.IsFaulted ? original.Exception : null; cached.Add(original, aggregate); }
            records.Add((phase, original, aggregate, direct));
        }
    }
    private static async Task<Exception> Failed(string phase, Task original)
    { try { await Own(phase, original).ConfigureAwait(false); } catch (Exception error) { return error; } throw new InvalidOperationException("Expected actual failure."); }
    private static bool Contains(Exception root, Exception leaf)
    {
        var pending = new Stack<Exception>(); var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); pending.Push(root); var edges = 0;
        while (pending.Count != 0)
        {
            var value = pending.Pop(); if (!seen.Add(value)) continue; Check(seen.Count <= 1024, "Fault graph bound."); if (ReferenceEquals(value, leaf)) return true;
            IEnumerable<Exception> children = value is AggregateException aggregate ? aggregate.InnerExceptions : value.InnerException is { } inner ? new[] { inner } : [];
            foreach (var child in children) { Check(++edges <= 4096, "Fault graph edge bound."); pending.Push(child); }
        }
        return false;
    }
    private static void Export(string prefix, McpDefaultOAuthHost host, McpOAuthOrchestrationResult? result = null)
    {
        lock (recordsGate)
        {
            foreach (var row in host.CapturedOriginals)
                if (row.Original is { } original) records.Add((prefix + ":" + row.Phase, original, row.Aggregate, row.Direct));
            if (result is not null) foreach (var row in result.Originals)
                if (row.Original is { } original) records.Add((prefix + ":flow:" + row.Phase, original, row.Aggregate, row.Direct));
        }
    }
    private static void NoNewCleanupLeaves(Exception cleanup, Exception prior)
    {
        var allowed = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var stack = new Stack<Exception>(); stack.Push(prior);
        var priorEdges = 0; var canceledTasks = new HashSet<Task>(ReferenceEqualityComparer.Instance); var canceledTokens = new HashSet<CancellationToken>();
        while (stack.Count != 0)
        {
            var item = stack.Pop(); if (!allowed.Add(item)) continue; Check(allowed.Count <= 1024, "Prior graph bound.");
            if (item is OperationCanceledException canceled) canceledTokens.Add(canceled.CancellationToken);
            if (item is McpOAuthFlowCanceledException flow) canceledTasks.Add(flow.Original);
            if (item is McpDefaultOAuthHostFailure host) foreach (var row in host.Originals)
                if (row.Original is { IsCanceled: true } original) canceledTasks.Add(original);
            if (item is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions) { Check(++priorEdges <= 4096, "Prior edge bound."); stack.Push(child); }
            else if (item.InnerException is { } child) { Check(++priorEdges <= 4096, "Prior edge bound."); stack.Push(child); }
        }
        stack.Push(cleanup); var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var edges = 0;
        while (stack.Count != 0)
        {
            var item = stack.Pop(); if (!seen.Add(item)) continue; Check(seen.Count <= 1024, "Cleanup graph bound.");
            if (allowed.Contains(item)) continue;
            if (item is TaskCanceledException taskCanceled && taskCanceled.Task is { } original && canceledTasks.Contains(original) &&
                canceledTokens.Contains(taskCanceled.CancellationToken) && taskCanceled.CancellationToken.IsCancellationRequested) continue;
            Check(item is AggregateException or McpDefaultOAuthHostFailure or McpOAuthOrchestrationException or McpOAuthFlowOriginalException or McpOAuthFlowCanceledException or McpOAuthOrchestrationCanceledException,
                "Unexpected new cleanup cause.");
            IEnumerable<Exception> children = item is AggregateException aggregate ? aggregate.InnerExceptions : item.InnerException is { } child ? new[] { child } : [];
            Check(children.Any(), "Unexpected empty cleanup wrapper.");
            foreach (var descendant in children) { Check(++edges <= 4096, "Cleanup graph edge bound."); stack.Push(descendant); }
        }
    }
    private static async Task Close(string prefix, McpDefaultOAuthHost host, Exception? primary,
        McpOAuthOrchestrationResult? result = null, Exception? expectedPrior = null)
    {
        var failures = new List<Exception>(); if (primary is not null) failures.Add(primary);
        try { await Own(prefix + ":close", host.DisposeAsync().AsTask()).ConfigureAwait(false); }
        catch (Exception error)
        {
            if (expectedPrior is not null)
                try { NoNewCleanupLeaves(error, expectedPrior); } catch (Exception criterion) { failures.Add(error); failures.Add(criterion); }
            else failures.Add(error);
        }
        finally { Export(prefix, host, result); }
        if (failures.Count != 0) throw new OriginalsFailure(failures.ToArray(), CapturedOriginals);
    }
    private static async Task Registration()
    {
        var store = new Store(); var requests = new List<(Uri Endpoint, string Body)>(); Uri? redirected = null;
        McpAdmittedHttpRequestFactory physical = request => new Physical(async requestToken =>
        {
            var body = request.Content is null ? "" : await Own("registration:request-body", request.Content.ReadAsStringAsync()).ConfigureAwait(false);
            requests.Add((request.RequestUri!, body));
            return request.Method == HttpMethod.Get ? Response(200, request.RequestUri!.AbsolutePath.Contains("oauth-protected-resource", StringComparison.Ordinal)
                ? "{\"resource\":\"https://mcp.example.test/mcp\",\"authorization_servers\":[\"https://issuer.example.test/\"]}" : Metadata)
                : Response(201, "{\"client_id\":\"registered-client\",\"client_id_issued_at\":\"discard\",\"client_secret_expires_at\":7,\"unknown_registration_field\":\"kept\"}");
        });
        var host = McpDefaultOAuthHost.Install(Resources(store, physical, (uri, _) => { redirected = uri; return ValueTask.CompletedTask; }));
        McpOAuthOrchestrationResult? result = null; Exception? primary = null;
        try
        {
            result = await Own("registration:authorize", host.AuthorizeAsync(host.Options())).ConfigureAwait(false);
            Check(result.Outcome == McpOAuthAuthorizationOutcome.Redirect && redirected is not null, "Genuine browser redirect missing.");
            Check(requests.Count == 3 && requests[2].Endpoint.AbsolutePath == "/register" && requests[2].Body.Contains("offline_access read", StringComparison.Ordinal), "Registration request mapping changed.");
            Check(store.Writes.Count == 3 && store.Value!.ClientInformation!.Value.GetProperty("unknown_registration_field").GetString() == "kept" && store.Value.CodeVerifier is { Length: 43 }, "Single persistence or raw registration fields changed.");
            var admittedClient = store.Value!.ClientInformation!.Value;
            Check(!admittedClient.TryGetProperty("client_id_issued_at", out _) && admittedClient.GetProperty("client_secret_expires_at").GetInt32() == 7 &&
                admittedClient.GetProperty("redirect_uris").GetArrayLength() == 0, "Actual registration parser normalization differs from original.");
            Check(redirected!.Query.Contains("code_challenge_method=S256", StringComparison.Ordinal) && redirected.Query.Contains("prompt=consent", StringComparison.Ordinal), "Actual PKCE/scope proposal missing.");
        }
        catch (Exception error) { primary = error; throw; }
        finally { await Close("registration", host, primary, result).ConfigureAwait(false); }
    }
    private static async Task Code()
    {
        var store = new Store { Value = Cached() }; var requests = 0;
        var host = McpDefaultOAuthHost.Install(Resources(store, request => new Physical(async requestToken =>
        {
            var body = await Own("code:request-body", request.Content!.ReadAsStringAsync()).ConfigureAwait(false); requests++;
            Check(body.Contains("grant_type=authorization_code", StringComparison.Ordinal) && body.Contains("code=actual-code", StringComparison.Ordinal) && body.Contains("code_verifier=stored-verifier", StringComparison.Ordinal) && body.Contains("client_id=admitted-client", StringComparison.Ordinal), "Actual code form differs.");
            return Response(200, "{\"access_token\":\"replacement\",\"token_type\":\"Bearer\",\"expires_in\":[\"2\"]}");
        })));
        McpOAuthOrchestrationResult? result = null; Exception? primary = null;
        try
        {
            _ = await Failed("code:wrong-state", host.CompleteAuthorizationAsync("actual-code", "wrong-state")).ConfigureAwait(false);
            Check(requests == 0 && store.Writes.Count == 0, "Wrong state acquired endpoint or persistence.");
            result = await Own("code:complete", host.CompleteAuthorizationAsync("actual-code", "admitted-state")).ConfigureAwait(false);
            Check(result.Outcome == McpOAuthAuthorizationOutcome.Authorized && requests == 1 && store.Writes.Count == 2 && store.Value!.Tokens!.AccessToken == "replacement" && store.Value.TokensExpireAt == 3000, "Token-only exchange persisted more than once or coercion/expiry differs.");
        }
        catch (Exception error) { primary = error; throw; }
        finally { await Close("code", host, primary, result).ConfigureAwait(false); }
    }
    private static async Task Provenance()
    {
        foreach (var wire in new[] { true, false })
        {
            var store = new Store { Value = Cached("actual-refresh") }; var tokenRequests = 0; var redirects = 0;
            var host = McpDefaultOAuthHost.Install(Resources(store, proposedRequest => new Physical(requestToken =>
            { tokenRequests++; return ValueTask.FromResult(Response(wire ? 400 : 200, wire ? "{\"error\":\"invalid_grant\"}" : "[]")); }), (_, _) => { redirects++; return ValueTask.CompletedTask; }));
            McpOAuthOrchestrationResult? result = null; Exception? primary = null;
            try
            {
                result = await Own("provenance:authorize:" + wire, host.AuthorizeAsync(host.Options())).ConfigureAwait(false);
                Check(result.Outcome == McpOAuthAuthorizationOutcome.Redirect && tokenRequests == 1 && redirects == 1, "Retry/fallback route changed.");
                Check((store.Value!.Tokens is null) == wire && result.Originals.Count(row => row.Phase == "invalidate") == (wire ? 1 : 0), "Wire invalid-grant must invalidate once; local parse failure must not masquerade as wire provenance.");
            }
            catch (Exception error) { primary = error; throw; }
            finally { await Close("provenance:" + wire, host, primary, result).ConfigureAwait(false); }
        }
    }
    private static async Task Admission()
    {
        var store = new Store { Value = Cached("refresh") }; var physical = 0; var refusal = 0; McpDefaultOAuthHost? host = null;
        var resources = Resources(store, proposedRequest => { physical++; return new Physical(requestToken => ValueTask.FromResult(Response(200, "{}"))); });
        resources = resources with { AdmitEndpoint = (_, _) =>
        {
            try { _ = host!.DisposeAsync(); } catch (InvalidOperationException) { refusal++; }
            try { _ = host!.AuthorizeAsync(host.Options()); } catch (InvalidOperationException) { refusal++; }
            return false;
        } };
        host = McpDefaultOAuthHost.Install(resources);
        McpOAuthOrchestrationResult? result = null; Exception? primary = null; Exception? expected = null;
        try
        {
            // Ordinary refresh admission failure can fall back; redirect admission is also denied.
            expected = await Failed("admission:authorize", host.AuthorizeAsync(host.Options())).ConfigureAwait(false);
            Check(physical == 0 && refusal == 4, "Endpoint refusal acquired HTTP or same-owner reentry altered lifecycle.");
        }
        catch (Exception error) { primary = error; throw; }
        finally { await Close("admission", host, primary, result, expected).ConfigureAwait(false); }
    }
    private static async Task HeldBrowser()
    {
        var store = new Store { Value = Cached() }; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var cancelSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refusal = 0; McpDefaultOAuthHost? host = null; CancellationTokenRegistration registration = default;
        host = McpDefaultOAuthHost.Install(Resources(store, proposedRequest => throw new InvalidOperationException("Cached browser route must not acquire HTTP."), (redirectUri, token) =>
        {
            registration = token.Register(() =>
            { try { _ = host!.DisposeAsync(); } catch (InvalidOperationException) { refusal++; } cancelSeen.TrySetResult(); });
            entered.TrySetResult(); return new ValueTask(held.Task);
        }));
        var original = host.AuthorizeAsync(host.Options()); Task? close = null; Exception? primary = null; Exception? expected = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            close = host.DisposeAsync().AsTask(); Check(ReferenceEquals(close, host.DisposeAsync().AsTask()), "Close original changed.");
            await cancelSeen.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Check(!close.IsCompleted && !original.IsCompleted && refusal == 1, "Close detached held browser original or cancellation reentry joined owner.");
            held.TrySetResult(); expected = await Failed("held:authorize", original).ConfigureAwait(false);
            var closeError = await Failed("held:close", close).ConfigureAwait(false); NoNewCleanupLeaves(closeError, expected);
            Check(host.CapturedOriginals.Any(row => row.Phase == "browser:redirect" && ReferenceEquals(row.Original, held.Task)), "Exact held browser Task lost.");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            held.TrySetResult(); registration.Dispose(); var faults = new List<Exception>();
            foreach (var task in new[] { original, close ?? host.DisposeAsync().AsTask() })
                try { await Own("held:finally", task).ConfigureAwait(false); } catch (Exception error) { faults.Add(error); }
            Export("held", host);
            if (primary is not null) throw new OriginalsFailure(new[] { primary }.Concat(faults).ToArray(), CapturedOriginals);
            foreach (var fault in faults)
                try { NoNewCleanupLeaves(fault, expected ?? throw new InvalidOperationException("Expected held failure not observed.")); }
                catch (Exception criterion) { throw new OriginalsFailure(new[] { fault, criterion }, CapturedOriginals); }
        }
        await LateResponseCustody().ConfigureAwait(false);
    }
    private static async Task LateResponseCustody()
    {
        using var cancel = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopLeaf = new IOException("late-success-stop-original"); var disposeLeaf = new IOException("late-success-response-disposal");
        var stop = Task.FromException(stopLeaf); var content = new LateOwnedContent(disposeLeaf);
        var acquired = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        var host = McpDefaultOAuthHost.Install(Resources(new Store { Value = Cached("refresh") }, request =>
            new Physical(token => { entered.TrySetResult(); return new(send.Task); }, () => { stopped.TrySetResult(); return stop; })));
        var authorize = host.AuthorizeAsync(host.Options(), cancel.Token); Exception? expected = null; Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            host.CancellationAdmission.Cancel(cancel);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Check(!authorize.IsCompleted && !content.Disposed, "Held acquisition detached or unacquired resource disposed.");
            send.TrySetResult(acquired);
            expected = await Failed("late-success:authorize", authorize).ConfigureAwait(false);
            var retained = host.CapturedOriginals;
            Check(content.Disposed && send.Task.IsCompletedSuccessfully && retained.Any(row => row.Aggregate is not null &&
                Contains(row.Aggregate, disposeLeaf) && Contains(row.Aggregate, stopLeaf)) &&
                retained.Any(row => ReferenceEquals(row.Original, stop) && row.Aggregate is not null && Contains(row.Aggregate, stopLeaf)) &&
                retained.Any(row => row.Phase == "http:Refresh:response-dispose" && row.Original is null && ReferenceEquals(row.Direct, disposeLeaf)),
                "Late successful response custody/disposal or sibling stop fault lost.");
            Check(host.CapturedOriginals.Any(row => ReferenceEquals(row.Original, send.Task) && row.Direct is null && row.Aggregate is null),
                "Successful physical send original was relabeled as canceled.");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            send.TrySetResult(acquired);
            try { await Own("late-success:finally", authorize).ConfigureAwait(false); }
            catch (Exception error) { if (expected is null) primary = primary is null ? error : new OriginalsFailure([primary, error], CapturedOriginals); }
            await Close("late-success", host, primary, expectedPrior: expected).ConfigureAwait(false);
        }
    }
    private static async Task SharedAndFaults()
    {
        var challengedStore = new Store { Value = Cached("refresh") }; var challengeSends = 0; Uri? challengeRedirect = null;
        var challengedHost = McpDefaultOAuthHost.Install(Resources(challengedStore, request =>
        { challengeSends++; throw new InvalidOperationException("Insufficient-scope challenge must skip actual token refresh."); }) with
        { Redirect = (uri, token) => { challengeRedirect = uri; return ValueTask.CompletedTask; } });
        using var challengedResponse = Response(401, "");
        challengedResponse.Headers.Add("WWW-Authenticate", "Bearer error=\"insufficient_scope\", scope=\"new_scope offline_access\"");
        Exception? challengePrimary = null;
        try
        {
            _ = await Failed("challenge:authorize", challengedHost.Authentication.OnUnauthorized!(
                new McpHttpUnauthorizedContext(challengedResponse, Server, "old"), CancellationToken.None).AsTask()).ConfigureAwait(false);
            Check(challengeSends == 0 && challengeRedirect is not null &&
                challengeRedirect.Query.Contains("scope=new_scope+offline_access", StringComparison.Ordinal) &&
                challengeRedirect.Query.Contains("prompt=consent", StringComparison.Ordinal),
                "Actual challenge scope or insufficient-scope skip-refresh redirect lost.");
        }
        catch (Exception error) { challengePrimary = error; throw; }
        finally { await Close("challenge", challengedHost, challengePrimary).ConfigureAwait(false); }
        var store = new Store { Value = Cached("refresh") }; var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var sends = 0;
        var host = McpDefaultOAuthHost.Install(Resources(store, proposedRequest => new Physical(requestToken => { sends++; entered.TrySetResult(); return new(response.Task); })));
        using var unauthorized = Response(401, ""); var context = new McpHttpUnauthorizedContext(unauthorized, Server, "old");
        var completedResponse = Response(200, "{\"access_token\":\"new\",\"token_type\":\"Bearer\"}");
        var callback = host.Authentication.OnUnauthorized!; var first = callback(context, CancellationToken.None).AsTask(); Task? second = null; Exception? primary = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); second = callback(context, CancellationToken.None).AsTask();
            var until = DateTime.UtcNow.AddSeconds(5);
            while (host.AdmittedOriginals.Count(row => row.Phase == "orchestrator:shared-unauthorized") < 2)
            { Check(DateTime.UtcNow < until, "Second actual shared refresh Task was not admitted."); await Task.Yield(); }
            response.TrySetResult(completedResponse);
            await Own("shared:first", first).ConfigureAwait(false); await Own("shared:second", second).ConfigureAwait(false);
            var rows = host.CapturedOriginals.Where(row => row.Phase == "orchestrator:shared-unauthorized").ToArray();
            Check(sends == 1 && rows.Length == 2 && ReferenceEquals(rows[0].Original, rows[1].Original) && store.Writes.Count == 2, "Shared actual refresh or single token persistence lost.");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            response.TrySetResult(completedResponse);
            var joinFaults = new List<Exception>();
            foreach (var task in second is null ? new[] { first } : new[] { first, second })
                try { await Own("shared:finally", task).ConfigureAwait(false); } catch (Exception error) { joinFaults.Add(error); }
            if (primary is not null) joinFaults.Insert(0, primary);
            await Close("shared", host, joinFaults.Count == 0 ? null : new OriginalsFailure(joinFaults.ToArray(), CapturedOriginals)).ConfigureAwait(false);
        }
        var left = new InvalidOperationException("actual-left"); var right = new IOException("actual-right"); var stopFault = new IOException("actual-stop");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        foreach (var isCanceled in new[] { false, true })
        {
            var faultStore = new Store { Value = Cached("refresh") }; var oce = new OperationCanceledException("actual-faulted-OCE", CancellationToken.None);
            var send = isCanceled ? Task.FromCanceled<HttpResponseMessage>(canceled.Token) : Task.FromException<HttpResponseMessage>(new AggregateException(left, right, oce));
            var stop = Task.FromException(stopFault); var faultHost = McpDefaultOAuthHost.Install(Resources(faultStore,
                proposedRequest => new Physical(requestToken => new(send), () => stop)) with { Redirect = (redirectUri, redirectToken) => ValueTask.FromException(new IOException("redirect-stop")) });
            Exception? failurePrimary = null; Exception? expected = null;
            try
            {
                expected = await Failed("faults:authorize:" + isCanceled, faultHost.AuthorizeAsync(faultHost.Options())).ConfigureAwait(false);
                var rows = faultHost.CapturedOriginals; var raw = rows.Single(row => ReferenceEquals(row.Original, send));
                Check(send.IsCanceled == isCanceled && raw.Direct is not null && rows.Any(row => ReferenceEquals(row.Original, stop) && Contains(row.Aggregate!, stopFault)), "Actual canceled/faulted original or stop leaf lost.");
                if (!isCanceled) Check(send.IsFaulted && Contains(raw.Aggregate!, left) && Contains(raw.Aggregate!, right) && Contains(raw.Aggregate!, oce), "Faulted-OCE or full multi-fault inventory flattened.");
            }
            catch (Exception error) { failurePrimary = error; throw; }
            finally { await Close("faults:" + isCanceled, faultHost, failurePrimary, expectedPrior: expected).ConfigureAwait(false); }
        }
        foreach (var bodyFailure in new[] { false, true })
        {
            var leaf = new IOException(bodyFailure ? "actual-body-original" : "actual-store-original");
            Task<int>? bodyOriginal = bodyFailure ? Task.FromException<int>(leaf) : null; Task? saveOriginal = bodyFailure ? null : Task.FromException(leaf);
            var dependencyStore = new Store { Value = Cached("refresh"), SaveOriginal = bodyFailure ? null : saveOriginal };
            var dependencyHost = McpDefaultOAuthHost.Install(Resources(dependencyStore, request => new Physical(token =>
            {
                if (!bodyFailure) return ValueTask.FromResult(Response(200, "{\"access_token\":\"new\",\"token_type\":\"Bearer\"}"));
                return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailedBody(bodyOriginal!)) });
            })) with { Redirect = (uri, token) => ValueTask.FromException(new IOException("body-fallback-redirect")) });
            Exception? expected = null; Exception? primaryFailure = null;
            try
            {
                expected = await Failed("dependencies:authorize", dependencyHost.AuthorizeAsync(dependencyHost.Options())).ConfigureAwait(false);
                Task exact = bodyFailure ? bodyOriginal! : saveOriginal!;
                Check(dependencyHost.CapturedOriginals.Any(row => ReferenceEquals(row.Original, exact) && row.Aggregate is not null && Contains(row.Aggregate, leaf)), "Actual body/store original fault lost.");
                Check(dependencyStore.Writes.Count == (bodyFailure ? 1 : 0), "Failed dependency was treated as a committed state write.");
            }
            catch (Exception error) { primaryFailure = error; throw; }
            finally { await Close("dependencies:" + bodyFailure, dependencyHost, primaryFailure, expectedPrior: expected).ConfigureAwait(false); }
        }
    }
}
