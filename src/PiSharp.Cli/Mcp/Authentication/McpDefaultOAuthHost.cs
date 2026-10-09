// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/mcp/src/oauth/flow.ts adaptOAuthProvider, packages/mcp/src/oauth/discovery.ts
// and packages/coding-agent/src/extensions/mcp/oauth.ts signInMcpServer.
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

namespace PiSharp.Cli.Mcp.Authentication;

/// <summary>Application generation composition of reviewed OAuth discovery/state/orchestration.
/// One persistence owner; token-only translators never write state. Resources remain borrowed.
/// The generation owner joins channel stop and this stable DisposeAsync original independently.</summary>
public sealed class McpDefaultOAuthHost : IAsyncDisposable
{
    private readonly McpDefaultOAuthHostResources resources;
    private readonly McpAdmittedOAuthStateProvider state;
    private readonly McpAdmittedOAuthOrchestrator orchestrator;
    private readonly McpOAuthOrchestrationCancellationAdmission orchestrationCancellation = new();
    private readonly Dictionary<McpOAuthAdmittedClient, JsonData> registered = new(ReferenceEqualityComparer.Instance);
    private readonly object authorizationGate = new();
    private Task<McpOAuthOrchestrationResult>? sharedAuthorization;
    private readonly McpDefaultOAuthHttp http;
    private readonly McpDefaultOAuthProtocol protocol;

    private sealed class GuardedStore(McpDefaultOAuthHost host, IMcpAdmittedOAuthStateStore borrowed) : IMcpAdmittedOAuthStateStore
    {
        public ValueTask<McpOAuthState?> LoadAsync()
        { var token = host.CurrentToken; host.Fence(token); return new(host.ObserveAsync("store:load", borrowed.LoadAsync, token)); }
        public ValueTask SaveAsync(McpOAuthState value)
        { var token = host.CurrentToken; host.Fence(token); return new(host.ObserveAsync("store:save", () => borrowed.SaveAsync(value), token)); }
    }

    public static McpDefaultOAuthHost Install(McpDefaultOAuthHostResources explicitlyAdmitted) => new(explicitlyAdmitted);
    private McpDefaultOAuthHost(McpDefaultOAuthHostResources admitted)
    {
        ArgumentNullException.ThrowIfNull(admitted); resources = admitted;
        ArgumentNullException.ThrowIfNull(admitted.Store); ArgumentNullException.ThrowIfNull(admitted.CancellationAdmission);
        if (!admitted.Server.IsAbsoluteUri || admitted.Server.Scheme is not ("http" or "https") || !admitted.RedirectUri.IsAbsoluteUri ||
            admitted.ClientMetadata.Value.ValueKind != JsonValueKind.Object || admitted.MaximumBytes is < 1024 or > 1_048_576)
            throw new ArgumentException("Finite exact server, redirect and client metadata admission required.");
        foreach (var callback in new Delegate[] { admitted.UnixMilliseconds, admitted.Entropy32, admitted.Redirect, admitted.ValidateAuthorizationState, admitted.Http, admitted.AdmitEndpoint })
            if (callback is null || callback.GetInvocationList().Length != 1) throw new ArgumentException("Exactly one explicitly admitted callback required.");
        foreach (var callback in new Delegate?[] { admitted.AuthorizationState, admitted.AddClientAuthentication })
            if (callback is not null && callback.GetInvocationList().Length != 1) throw new ArgumentException("One optional callback required.");
        if (Encoding.UTF8.GetByteCount(admitted.ClientMetadata.ToString()) > admitted.MaximumBytes) throw new ArgumentException("Client metadata exceeds finite bound.");
        state = new(admitted.Server, new GuardedStore(this, admitted.Store), () => InvokeBorrowed(() =>
        { var now = admitted.UnixMilliseconds(); return double.IsFinite(now) ? now : throw new ArgumentException("Finite admitted clock required."); }));
        if (admitted.ClientMetadataDocumentBase is { } documentBase)
        {
            if (!documentBase.IsAbsoluteUri || documentBase.Scheme != "https") throw new ArgumentException("An https Client ID Metadata Document base is required.");
            clientMetadataDocument = metadata => McpOAuthClientMetadataDocuments.Create(documentBase, admitted.Server, admitted.RedirectUri.AbsoluteUri, metadata);
        }
        if (admitted.RequestTimeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)))
            throw new ArgumentException("A positive finite OAuth request timeout is required.");
        http = new(this, admitted); protocol = new(this, admitted, http);
        orchestrator = new(new(
            (options, token) => new(DiscoverAsync(options, token)),
            (discovery, token) => new(SaveDiscoveryAsync(discovery, token)),
            token => new(ReadClientAsync(token)),
            (client, token) => new(SaveClientAsync(client, token)),
            (options, discovery, scope, token) => new(RegisterAsync(options, discovery, scope, token)),
            token => new(ReadTokensAsync(token)),
            (context, refresh, token) => new(protocol.RefreshAsync(context, refresh, token)),
            token => new(ObserveAsync("state:verifier", () => new ValueTask<string>(state.CodeVerifierAsync()), token)),
            (context, code, verifier, token) => new(protocol.ExchangeCodeAsync(context, code, verifier, token)),
            (context, token) => new(protocol.BeginAsync(context, token)),
            (tokens, token) => new(ObserveAsync("state:save-tokens", () => new ValueTask(state.SaveTokensAsync(tokens)), token)),
            (verifier, token) => new(ObserveAsync("state:save-verifier", () => new ValueTask(state.SaveCodeVerifierAsync(verifier)), token)),
            (uri, token) => new(RedirectAsync(uri, token)),
            (kind, token) => new(ObserveAsync("state:invalidate", () => new ValueTask(state.InvalidateAsync(kind)), token)),
            admitted.AuthorizationState is null ? null : token => new(ObserveAsync("authorization-state", () => admitted.AuthorizationState(token), token))),
            orchestrationCancellation);
        // Last: failed construction does not bind the caller's affine admission.
        admitted.CancellationAdmission.Bind(this);
    }

    public McpAdmittedHttpAuthentication Authentication => new(
        token => new(Start(ct => ReadAuthenticationTokenAsync(ct), token)),
        (context, token) => new(Start(async ct => { await UnauthorizedAsync(context, ct).ConfigureAwait(false); return true; }, token)));
    public McpDefaultOAuthHostCancellationAdmission CancellationAdmission => resources.CancellationAdmission;
    public McpAdmittedHttpRequestFactory CreateAuthenticatedRequestFactory(McpAdmittedHttpRequestFactory physical) =>
        McpAuthenticatedHttpRequestFactory.Create(physical, resources.Server, Authentication, resources.MaximumBytes);
    public Task<McpOAuthOrchestrationResult> AuthorizeAsync(McpOAuthOrchestrationOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ServerUrl != resources.Server || options.ClientMetadata.ToString() != resources.ClientMetadata.ToString() ||
            !ReferenceEquals(options.ClientMetadataDocument, clientMetadataDocument) || options.AuthorizationServerMetadataUrl != resources.AuthorizationServerMetadataUrl)
            throw new ArgumentException("Authorization options belong to the exact installed server/client metadata admission.");
        if (options.AuthorizationCode is not null || options.Iss is not null) throw new ArgumentException("Code exchange requires the explicit response-state validation entry.");
        return Start(ct => ObserveAsync("orchestrator:authorize", () => new ValueTask<McpOAuthOrchestrationResult>(orchestrator.AuthorizeAsync(options, ct)), ct), token);
    }
    /// <summary>Validates the response state, then the RFC 9207 <paramref name="iss"/> of the authorization response
    /// against the authorization server metadata, before the code is sent to any token endpoint.</summary>
    public Task<McpOAuthOrchestrationResult> CompleteAuthorizationAsync(string code, string? returnedState,
        string? scope = null, CancellationToken token = default, string? iss = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        return Start(async ct =>
        {
            await ObserveAsync("response:validate-state", () => resources.ValidateAuthorizationState(returnedState, ct), ct).ConfigureAwait(false);
            return await ObserveAsync("orchestrator:exchange-code", () => new ValueTask<McpOAuthOrchestrationResult>(orchestrator.AuthorizeAsync(Options(code, scope, iss: iss), ct)), ct).ConfigureAwait(false);
        }, token);
    }
    public McpOAuthOrchestrationOptions Options(string? code = null, string? scope = null, bool skipRefresh = false, string? iss = null) =>
        new(resources.Server, resources.ClientMetadata, McpDefaultOAuthProtocol.Text(resources.ClientMetadata.Value, "scope", false),
            clientMetadataDocument, scope, code, skipRefresh, iss, resources.AuthorizationServerMetadataUrl);
    private readonly Func<JsonData?, McpOAuthClientMetadataDocument?>? clientMetadataDocument;
    private Task<McpOAuthState> ReadStateAsync(CancellationToken token) =>
        ObserveAsync("state:read", () => new ValueTask<McpOAuthState>(state.ReadAsync()), token);
    private async Task<McpOAuthTokens?> ReadTokensAsync(CancellationToken token) => (await ReadStateAsync(token).ConfigureAwait(false)).Tokens;
    private async Task<string?> ReadAuthenticationTokenAsync(CancellationToken token) =>
        (await ReadStateAsync(token).ConfigureAwait(false)).Tokens?.AccessToken;
    private async Task<McpOAuthAdmittedClient?> ReadClientAsync(CancellationToken token)
    {
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        return current.ClientInformation is { } information ? McpDefaultOAuthProtocol.Client(information.Value) : null;
    }
    private async Task<McpOAuthAdmittedClient> RegisterAsync(McpOAuthOrchestrationOptions options,
        McpOAuthDiscoveredServer discovery, string? scope, CancellationToken token)
    {
        var result = await protocol.RegisterAsync(options, discovery, scope, token).ConfigureAwait(false);
        Fence(token); lock (gate) registered.Add(result.Client, result.Raw); return result.Client;
    }
    private Task SaveClientAsync(McpOAuthAdmittedClient client, CancellationToken token)
    {
        JsonData value;
        lock (gate)
        {
            if (!registered.Remove(client, out value!))
            {
                var fields = new Dictionary<string, object?> { ["client_id"] = client.ClientId };
                if (client.ClientSecret is not null) fields["client_secret"] = client.ClientSecret;
                if (client.AuthenticationMethod is not null) fields["token_endpoint_auth_method"] = client.AuthenticationMethod;
                value = JsonData.FromElement(JsonSerializer.SerializeToElement(fields));
            }
        }
        return ObserveAsync("state:save-client", () => new ValueTask(state.SaveClientInformationAsync(value)), token);
    }
    private Task SaveDiscoveryAsync(McpOAuthDiscoveredServer discovery, CancellationToken token)
    {
        var fields = new Dictionary<string, object?> { ["authorizationServerUrl"] = discovery.AuthorizationServerUrl };
        if (discovery.AuthorizationServerMetadata is { } metadata) fields["authorizationServerMetadata"] = metadata.Value;
        if (discovery.ResourceMetadata is { } resource) fields["resourceMetadata"] = resource.Value;
        lock (authorizationGate) if (challengedMetadata is { } challenged) fields["resourceMetadataUrl"] = challenged.AbsoluteUri;
        return ObserveAsync("state:save-discovery", () => new ValueTask(state.SaveDiscoveryAsync(JsonData.FromElement(JsonSerializer.SerializeToElement(fields)))), token);
    }
    private async Task<McpOAuthDiscoveredServer> DiscoverAsync(McpOAuthOrchestrationOptions options, CancellationToken token)
    {
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        McpAdmittedOAuthExchange exchange = (request, ct) => new(http.SendAsync(request.Endpoint, McpDefaultOAuthHttpPurpose.Discovery,
            request.Method, request.Headers, request.Body, ct));
        // With a configured metadata URL, discovery is neither read from nor written to the cache.
        if (options.AuthorizationServerMetadataUrl is null && current.Discovery is { } cached && cached.Value.ValueKind == JsonValueKind.Object && cached.Value.TryGetProperty("authorizationServerUrl", out _))
        {
            if (Encoding.UTF8.GetByteCount(cached.ToString()) > resources.MaximumBytes) throw new McpOAuthProtocolException("metadata_bound", "Cached discovery exceeds its finite admission.");
            var issuer = McpDefaultOAuthProtocol.Text(cached.Value, "authorizationServerUrl", true)!;
            _ = McpDefaultOAuthProtocol.Url(issuer);
            JsonData? metadata = cached.Value.TryGetProperty("authorizationServerMetadata", out var authorization) ? JsonData.FromElement(authorization) :
                await ObserveAsync("discovery:authorization-server", () => new ValueTask<JsonData?>(McpAdmittedOAuthDiscovery.DiscoverAuthorizationServerAsync(issuer, exchange, token, resources.MaximumBytes)), token).ConfigureAwait(false);
            if (metadata is not null)
            {
                foreach (var name in new[] { "issuer", "authorization_endpoint", "token_endpoint" }) _ = McpDefaultOAuthProtocol.Url(McpDefaultOAuthProtocol.Text(metadata.Value, name, true)!);
                _ = McpDefaultOAuthProtocol.Strings(metadata.Value, "response_types_supported", true);
                if (McpDefaultOAuthProtocol.Text(metadata.Value, "issuer", true)!.TrimEnd('/') != issuer.TrimEnd('/')) throw new McpOAuthProtocolException("issuer_mismatch", "Cached discovery issuer differs.");
            }
            var resource = cached.Value.TryGetProperty("resourceMetadata", out var value) ? JsonData.FromElement(value) : null;
            _ = McpAdmittedOAuthDiscovery.SelectResource(options.ServerUrl, resource);
            return new(issuer, metadata, resource);
        }
        Uri? resourceMetadata;
        lock (authorizationGate) resourceMetadata = challengedMetadata;
        return await ObserveAsync("discovery:resource", () => new ValueTask<McpOAuthDiscoveredServer>(McpAdmittedOAuthDiscovery.DiscoverAsync(options.ServerUrl, exchange, resourceMetadata, token, resources.MaximumBytes,
            options.AuthorizationServerMetadataUrl)), token).ConfigureAwait(false);
    }
    private Uri? challengedMetadata;
    /// <summary>signInMcpServer resourceMetadataUrl: discovery starts at the resource metadata URL of the challenge that asked for the sign-in.</summary>
    internal void UseChallenge(Uri? resourceMetadataUrl) { lock (authorizationGate) challengedMetadata = resourceMetadataUrl; }
    private async Task UnauthorizedAsync(McpHttpUnauthorizedContext context, CancellationToken token)
    {
        if (context.ServerUrl != resources.Server) throw new ArgumentException("Unauthorized response belongs to another MCP endpoint.");
        var challenge = context.Response.Headers.TryGetValues("WWW-Authenticate", out var headers) ? string.Join(',', headers) : "";
        if (Encoding.UTF8.GetByteCount(challenge) > resources.MaximumBytes) throw new McpOAuthProtocolException("challenge_bound", "OAuth challenge exceeds admission.");
        string? Field(string name)
        {
            var match = Regex.Match(challenge, "(?:^|[,\\s])" + name + "=(?:\"([^\"]*)\"|([^\\s,]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            // An empty value (`scope=""`) carries no information, so it counts as absent.
            var found = match.Success ? match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value : null;
            return string.IsNullOrEmpty(found) ? null : found;
        }
        var scheme = challenge.TrimStart().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        var supported = string.Equals(scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.Equals(scheme, "DPoP", StringComparison.OrdinalIgnoreCase);
        var skip = supported && Field("error") == "insufficient_scope";
        var requestedScope = supported ? Field("scope") : null;
        var requested = supported ? Field("resource_metadata") : null;
        var metadata = requested is not null && Uri.TryCreate(requested, UriKind.Absolute, out var proposed) ? McpDefaultOAuthProtocol.Url(proposed.AbsoluteUri) : null;
        var current = await ReadStateAsync(token).ConfigureAwait(false);
        Task<McpOAuthOrchestrationResult> original;
        lock (authorizationGate)
        {
            if (sharedAuthorization is { IsCompleted: false }) original = sharedAuthorization;
            else
            {
                if (!skip && context.RejectedToken is not null && current.Tokens?.AccessToken is { } replacement && replacement != context.RejectedToken) return;
                challengedMetadata = metadata;
                // A step-up keeps the scope granted so far, since the challenge may list only the missing scopes.
                var scope = skip ? McpOAuthScope.StepUp(current.Tokens?.Scope, requestedScope) : requestedScope;
                original = sharedAuthorization = InvokeBorrowed(() => orchestrator.AuthorizeAsync(Options(scope: scope, skipRefresh: skip), token));
            }
        }
        var result = await ObserveAsync("orchestrator:shared-unauthorized", () => new ValueTask<McpOAuthOrchestrationResult>(original), token).ConfigureAwait(false);
        if (result.Outcome == McpOAuthAuthorizationOutcome.Redirect) throw new McpOAuthAuthorizationRequiredException();
    }
    private Task RedirectAsync(Uri uri, CancellationToken token)
    {
        if (!InvokeBorrowed(() => resources.AdmitEndpoint(uri, McpDefaultOAuthHttpPurpose.AuthorizationRedirect))) throw new InvalidOperationException("Authorization redirect URL is not caller-admitted.");
        return ObserveAsync("browser:redirect", () => resources.Redirect(uri, token), token);
    }

    // Owner and callback custody lives in this new leaf; no shared registry/session lifecycle is edited.
    private sealed class Frame(McpDefaultOAuthHost owner, Frame? parent, Physical? ancestry, CancellationToken token)
    { internal readonly McpDefaultOAuthHost Owner = owner; internal readonly Frame? Parent = parent; internal readonly Physical? Ancestry = ancestry; internal readonly CancellationToken Token = token; internal volatile bool Active = true; }
    private sealed class Physical(McpDefaultOAuthHost owner, Frame? logicalParent, Physical? parent)
    { internal readonly McpDefaultOAuthHost Owner = owner; internal readonly Frame? Logical = logicalParent; internal readonly Physical? Parent = parent; internal volatile bool Active = true; }
    private static readonly AsyncLocal<Frame?> logical = new();
    [ThreadStatic] private static Physical? physical;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly HashSet<Task> pending = new(ReferenceEqualityComparer.Instance);
    private readonly List<McpDefaultOAuthHostOriginal> originals = [];
    private readonly List<(string Phase, Task Original)> admittedOriginals = [];
    internal ImmutableArray<(string Phase, Task Original)> AdmittedOriginals { get { lock (gate) return admittedOriginals.ToImmutableArray(); } }
    private void AdmitOriginal(string phase, Task original) { lock (gate) admittedOriginals.Add((phase, original)); }
    private readonly Dictionary<Task, AggregateException?> aggregates = new(ReferenceEqualityComparer.Instance);
    private Task? close;
    private bool closing;
    private CancellationToken CurrentToken => logical.Value is { } frame && ReferenceEquals(frame.Owner, this) ? frame.Token : CancellationToken.None;
    public ImmutableArray<McpDefaultOAuthHostOriginal> CapturedOriginals { get { lock (gate) return originals.ToImmutableArray(); } }
    private void Record(string phase, Task? original, Exception? direct)
    {
        lock (gate)
        {
            AggregateException? aggregate = null;
            if (original is not null && !aggregates.TryGetValue(original, out aggregate))
            { aggregate = original.IsFaulted ? original.Exception : null; aggregates.Add(original, aggregate); }
            originals.Add(new(phase, original, aggregate, direct));
        }
        if (direct is McpOAuthOrchestrationException failed) Import(failed.Originals);
        if (direct is McpOAuthOrchestrationCanceledException canceled) Import(canceled.Originals);
    }
    private void Import(ImmutableArray<McpOAuthOrchestrationOriginal> raw)
    {
        lock (gate) foreach (var row in raw)
        {
            if (row.Original is { } task) aggregates.TryAdd(task, row.Aggregate);
            originals.Add(new("flow:" + row.Phase, row.Original, row.Aggregate, row.Direct));
        }
    }
    internal T InvokeBorrowed<T>(Func<T> callback)
    {
        var previous = physical; var entry = new Physical(this, logical.Value, previous); physical = entry;
        try { return callback(); } finally { entry.Active = false; physical = previous; }
    }
    private void RejectReentry()
    {
        var work = new Stack<object>(); var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (logical.Value is { } frame) work.Push(frame); if (physical is { } entry) work.Push(entry);
        while (work.Count != 0)
        {
            var item = work.Pop(); if (!seen.Add(item)) continue;
            if (item is Frame current)
            {
                if (current.Active && ReferenceEquals(current.Owner, this)) throw new InvalidOperationException("OAuth host cannot join its active callback ancestor.");
                if (current.Parent is { } parent) work.Push(parent); if (current.Ancestry is { } ancestry) work.Push(ancestry);
            }
            else if (item is Physical actual)
            {
                if (actual.Active && ReferenceEquals(actual.Owner, this)) throw new InvalidOperationException("OAuth host cannot join its actual physical callback.");
                if (actual.Parent is { } parent) work.Push(parent); if (actual.Logical is { } ancestry) work.Push(ancestry);
            }
        }
    }
    internal void ValidateExternalOwnerCall() => RejectReentry();
    private void Fence(CancellationToken token) { token.ThrowIfCancellationRequested(); lock (gate) ObjectDisposedException.ThrowIf(closing, this); }
    private Task<T> Start<T>(Func<CancellationToken, Task<T>> body, CancellationToken token)
    {
        RejectReentry(); token.ThrowIfCancellationRequested(); var parent = logical.Value; var ancestry = physical;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this); Task<T>? original = null;
            original = Task.Run(async () =>
            {
                // This gate prevents user callbacks before the actual original is published in pending.
                lock (gate) { }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
                var previous = logical.Value; var frame = new Frame(this, parent, ancestry, linked.Token); logical.Value = frame;
                try { Fence(linked.Token); return await body(linked.Token).ConfigureAwait(false); }
                finally { frame.Active = false; logical.Value = previous; lock (gate) pending.Remove(original!); }
            });
            pending.Add(original); return original;
        }
    }
    internal async Task<T> ObserveAsync<T>(string phase, Func<ValueTask<T>> callback, CancellationToken token, Action<T>? transferCustody = null)
    {
        Task<T>? original = null; Exception? direct = null; T result = default!;
        try
        {
            token.ThrowIfCancellationRequested(); original = InvokeBorrowed(callback).AsTask(); AdmitOriginal(phase, original);
            result = await original.ConfigureAwait(false);
            // A late successful acquisition still transfers its real resource to the caller's
            // cleanup inventory before cancellation can turn this observation into a failure.
            transferCustody?.Invoke(result);
        }
        catch (Exception error) { direct = error; }
        Record(phase, original, direct);
        if (result is McpOAuthOrchestrationResult flow) Import(flow.Originals);
        if (direct is OperationCanceledException canceled && original is { IsCanceled: true } && token.IsCancellationRequested && canceled.CancellationToken == token) ExceptionDispatchInfo.Capture(canceled).Throw();
        if (direct is not null) throw new McpDefaultOAuthHostFailure(phase, CapturedOriginals, original is { IsFaulted: true } ? CachedAggregate(original)! : direct);
        token.ThrowIfCancellationRequested(); return result;
    }
    internal async Task ObserveAsync(string phase, Func<ValueTask> callback, CancellationToken token)
    {
        Task? original = null; Exception? direct = null;
        try { token.ThrowIfCancellationRequested(); original = InvokeBorrowed(callback).AsTask(); AdmitOriginal(phase, original); await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; }
        Record(phase, original, direct);
        if (direct is OperationCanceledException canceled && original is { IsCanceled: true } && token.IsCancellationRequested && canceled.CancellationToken == token) ExceptionDispatchInfo.Capture(canceled).Throw();
        if (direct is not null) throw new McpDefaultOAuthHostFailure(phase, CapturedOriginals, original is { IsFaulted: true } ? CachedAggregate(original)! : direct);
        token.ThrowIfCancellationRequested();
    }
    private AggregateException? CachedAggregate(Task task) { lock (gate) return aggregates[task]; }
    internal void ObserveDisposal(string phase, Action dispose)
    {
        Exception? direct = null;
        try { InvokeBorrowed(() => { dispose(); return true; }); }
        catch (Exception error) { direct = error; }
        // IDisposable has no Task original. Preserve its real direct fault without inventing one.
        Record(phase, null, direct);
        if (direct is not null) ExceptionDispatchInfo.Capture(direct).Throw();
    }
    internal void CancelOwned(CancellationTokenSource source)
    { ArgumentNullException.ThrowIfNull(source); InvokeBorrowed(() => { orchestrationCancellation.Cancel(source); return true; }); }
    internal Task CancelOwnedAsync(CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source); var parent = logical.Value; var ancestry = physical;
        return Task.Run(() =>
        {
            var previous = physical; var entry = new Physical(this, parent, ancestry); physical = entry;
            try { orchestrationCancellation.Cancel(source); } finally { entry.Active = false; physical = previous; }
        });
    }
    public ValueTask DisposeAsync()
    {
        RejectReentry(); var parent = logical.Value; var ancestry = physical;
        lock (gate)
        {
            if (close is not null) return new(close);
            closing = true; var operations = pending.ToArray();
            close = Task.Run(() => CloseAsync(operations, parent, ancestry)); return new(close);
        }
    }
    private async Task CloseAsync(Task[] operations, Frame? parent, Physical? ancestry)
    {
        var previous = logical.Value; var frame = new Frame(this, parent, ancestry, CancellationToken.None); logical.Value = frame;
        var failures = new List<Exception>();
        try
        {
            try { await ObserveAsync("host:close-cancellation", () => new ValueTask(CancelOwnedAsync(lifetime)), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            foreach (var original in operations)
            {
                Exception? direct = null;
                try { await original.ConfigureAwait(false); } catch (Exception error) { direct = error; failures.Add(error); }
                Record("host:close-operation", original, direct);
            }
            try { await ObserveAsync("orchestrator:close", orchestrator.DisposeAsync, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { failures.Add(error); }
            try { lifetime.Dispose(); } catch (Exception error) { failures.Add(error); }
        }
        finally { frame.Active = false; logical.Value = previous; }
        if (failures.Count != 0) throw new McpDefaultOAuthHostFailure("close", CapturedOriginals,
            failures.Count == 1 ? failures[0] : new AggregateException("OAuth host cancellation/active operations/orchestrator cleanup retained.", failures));
    }
}
