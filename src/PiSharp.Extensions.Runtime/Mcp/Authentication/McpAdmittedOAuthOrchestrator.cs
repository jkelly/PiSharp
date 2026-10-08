// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/mcp/src/oauth/flow.ts runFlow/authorizeMcp.
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>Required affine cancellation ownership. After-await callback-entering cancellation must use this hook
/// and directly join its actual returned Task. This supplies no ambient cancellation or storage authority.</summary>
public sealed class McpOAuthOrchestrationCancellationAdmission
{
    private readonly object gate = new();
    private McpAdmittedOAuthOrchestrator? owner;
    internal void Bind(McpAdmittedOAuthOrchestrator value)
    { lock (gate) { if (owner is not null) throw new InvalidOperationException("Cancellation admission is affine."); owner = value; } }
    private McpAdmittedOAuthOrchestrator Capture()
    { lock (gate) return owner ?? throw new InvalidOperationException("Cancellation admission is not bound."); }
    public void Cancel(CancellationTokenSource borrowed) => Capture().CancelOwned(borrowed);
    public Task CancelAsync(CancellationTokenSource borrowed) => Capture().CancelOwnedAsync(borrowed);
}

/// <summary>Bounded original runFlow/authorizeMcp ordering over explicitly admitted operation callbacks.
/// Serial operations and joined close; no ambient discovery, registration, browser, credential storage or default-profile authority.</summary>
public sealed class McpAdmittedOAuthOrchestrator : IAsyncDisposable
{
    private sealed class Frame(McpAdmittedOAuthOrchestrator owner, Frame? parent, Physical? ancestry)
    { internal readonly McpAdmittedOAuthOrchestrator Owner = owner; internal readonly Frame? Parent = parent; internal readonly Physical? Ancestry = ancestry; internal volatile bool Active = true; }
    private sealed class Physical(McpAdmittedOAuthOrchestrator owner, Frame? logical, Physical? parent)
    { internal readonly McpAdmittedOAuthOrchestrator Owner = owner; internal readonly Frame? Logical = logical; internal readonly Physical? Parent = parent; internal volatile bool Active = true; }
    private static readonly AsyncLocal<Frame?> logical = new();
    [ThreadStatic] private static Physical? physical;
    private readonly object gate = new();
    private readonly McpOAuthOrchestrationDependencies dependencies;
    private readonly CancellationTokenSource lifetime = new();
    private Task? active, close;
    private bool closing;
    public McpAdmittedOAuthOrchestrator(McpOAuthOrchestrationDependencies explicitlyAdmitted,
        McpOAuthOrchestrationCancellationAdmission cancellationAdmission)
    {
        ArgumentNullException.ThrowIfNull(explicitlyAdmitted); ArgumentNullException.ThrowIfNull(cancellationAdmission);
        foreach (var callback in new Delegate[] { explicitlyAdmitted.Discover, explicitlyAdmitted.ReadClient,
            explicitlyAdmitted.RegisterClient, explicitlyAdmitted.ReadTokens, explicitlyAdmitted.Refresh,
            explicitlyAdmitted.ReadVerifier, explicitlyAdmitted.ExchangeCode, explicitlyAdmitted.BeginAuthorization, explicitlyAdmitted.SaveTokens,
            explicitlyAdmitted.SaveVerifier, explicitlyAdmitted.Redirect })
            if (callback is null || callback.GetInvocationList().Length != 1) throw new ArgumentException("One admitted callback per dependency required.");
        foreach (var callback in new Delegate?[] { explicitlyAdmitted.SaveDiscovery, explicitlyAdmitted.SaveClient, explicitlyAdmitted.Invalidate, explicitlyAdmitted.ReadAuthorizationState })
            if (callback is not null && callback.GetInvocationList().Length != 1) throw new ArgumentException("One optional admitted callback required.");
        dependencies = explicitlyAdmitted; cancellationAdmission.Bind(this);
    }
    public Task<McpOAuthOrchestrationResult> AuthorizeAsync(McpOAuthOrchestrationOptions options, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(options.ServerUrl);
        if (!options.ServerUrl.IsAbsoluteUri || options.ServerUrl.Scheme is not ("http" or "https"))
            throw new ArgumentException("Absolute proposed HTTP(S) server required.");
        return Start(ct => AuthorizeCore(options, ct), token);
    }
    private Task<T> Start<T>(Func<CancellationToken, Task<T>> body, CancellationToken token)
    {
        RejectReentry(); token.ThrowIfCancellationRequested();
        var logicalParent = logical.Value; var physicalParent = physical;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing, this);
            if (active is { IsCompleted: false }) throw new InvalidOperationException("One authorization flow operation at a time.");
            // Scheduling creates the actual returned operation before any callback can publish. Both ancestors captured above.
            var original = Task.Run(() => Run(body, token, logicalParent, physicalParent));
            active = original; return original;
        }
    }
    private Task<T> Run<T>(Func<CancellationToken, Task<T>> body, CancellationToken token, Frame? parent, Physical? ancestry)
    {
        var previous = physical; var entry = new Physical(this, parent, ancestry); physical = entry;
        try { return RunLogical(body, token, parent, ancestry); }
        finally { entry.Active = false; physical = previous; }
    }
    private async Task<T> RunLogical<T>(Func<CancellationToken, Task<T>> body, CancellationToken token, Frame? parent, Physical? ancestry)
    {
        var previous = logical.Value; var frame = new Frame(this, parent, ancestry); logical.Value = frame;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try { return await body(linked.Token).ConfigureAwait(false); }
        finally { frame.Active = false; logical.Value = previous; }
    }
    private T InvokePhysical<T>(Func<T> invoke)
    {
        var previous = physical; var entry = new Physical(this, logical.Value, previous); physical = entry;
        try { return invoke(); } finally { entry.Active = false; physical = previous; }
    }
    internal void CancelOwned(CancellationTokenSource borrowed)
    { ArgumentNullException.ThrowIfNull(borrowed); InvokePhysical(() => { borrowed.Cancel(); return true; }); }
    internal Task CancelOwnedAsync(CancellationTokenSource borrowed)
    {
        ArgumentNullException.ThrowIfNull(borrowed);
        var parent = logical.Value; var ancestry = physical;
        return Task.Run(() =>
        {
            var previous = physical; var entry = new Physical(this, parent, ancestry); physical = entry;
            try { borrowed.Cancel(); } finally { entry.Active = false; physical = previous; }
        }); // Caller owns and must directly join this exact original, including every cancellation sibling fault.
    }
    private void RejectReentry()
    {
        var pending = new Stack<object>(); var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (logical.Value is { } current) pending.Push(current);
        if (physical is { } entry) pending.Push(entry);
        while (pending.Count != 0)
        {
            var item = pending.Pop(); if (!visited.Add(item)) continue;
            if (item is Frame frame)
            {
                if (frame.Active && ReferenceEquals(frame.Owner, this)) throw new InvalidOperationException("OAuth flow cannot join its active ancestor original.");
                if (frame.Parent is { } parent) pending.Push(parent);
                if (frame.Ancestry is { } captured) pending.Push(captured);
            }
            else if (item is Physical physicalFrame)
            {
                if (physicalFrame.Active && ReferenceEquals(physicalFrame.Owner, this)) throw new InvalidOperationException("OAuth physical callback cannot join its owner.");
                if (physicalFrame.Parent is { } parent) pending.Push(parent);
                if (physicalFrame.Logical is { } captured) pending.Push(captured);
            }
        }
    }
    private void Fence(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate) ObjectDisposedException.ThrowIf(closing, this);
    }

    private async Task<T> Work<T>(string phase, Func<ValueTask<T>> invoke,
        List<McpOAuthOrchestrationOriginal> originals, CancellationToken token)
    {
        Fence(token); Task<T>? original = null; AggregateException? aggregate = null; Exception? direct = null;
        T result = default!;
        try { original = InvokePhysical(invoke).AsTask(); result = await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; aggregate = original is { IsFaulted: true } ? original.Exception : null; }
        originals.Add(new(phase, original, aggregate, direct));
        if (direct is OperationCanceledException canceled && original is { IsCanceled: true })
            throw new McpOAuthFlowCanceledException(original, canceled);
        if (direct is not null) throw new McpOAuthFlowOriginalException(phase, original, aggregate ?? direct, direct);
        Fence(token); return result;
    }
    private async Task Work(string phase, Func<ValueTask> invoke,
        List<McpOAuthOrchestrationOriginal> originals, CancellationToken token)
    {
        Fence(token); Task? original = null; AggregateException? aggregate = null; Exception? direct = null;
        try { original = InvokePhysical(invoke).AsTask(); await original.ConfigureAwait(false); }
        catch (Exception error) { direct = error; aggregate = original is { IsFaulted: true } ? original.Exception : null; }
        originals.Add(new(phase, original, aggregate, direct));
        if (direct is OperationCanceledException canceled && original is { IsCanceled: true })
            throw new McpOAuthFlowCanceledException(original, canceled);
        if (direct is not null) throw new McpOAuthFlowOriginalException(phase, original, aggregate ?? direct, direct);
        Fence(token);
    }
    private static Exception Selected(Exception error)
    {
        while (error is McpOAuthFlowOriginalException wrapped) error = wrapped.Direct;
        return error;
    }
    private async Task<McpOAuthOrchestrationResult> AuthorizeCore(McpOAuthOrchestrationOptions options, CancellationToken token)
    {
        var originals = new List<McpOAuthOrchestrationOriginal>();
        try
        {
            McpOAuthAuthorizationOutcome outcome;
            try { outcome = await RunFlow(options, originals, token).ConfigureAwait(false); }
            catch (Exception error) when (Selected(error) is McpOAuthTokenOperationFailure { Category: McpOAuthTokenFailureCategory.WireOAuthError, Code: "invalid_client" or "unauthorized_client" or "invalid_grant" })
            {
                var selected = (McpOAuthTokenOperationFailure)Selected(error);
                if (dependencies.Invalidate is { } invalidate)
                    await Work("invalidate", () => invalidate(selected.Code == "invalid_grant" ? McpOAuthInvalidation.Tokens : McpOAuthInvalidation.All, token), originals, token).ConfigureAwait(false);
                // The original second run is outside the retry catch. A second failure never loops.
                outcome = await RunFlow(options, originals, token).ConfigureAwait(false);
            }
            return new(outcome, originals.ToImmutableArray());
        }
        catch (McpOAuthFlowCanceledException canceled)
        { throw new McpOAuthOrchestrationCanceledException(originals.ToImmutableArray(), canceled); }
        catch (OperationCanceledException canceled) when (token.IsCancellationRequested)
        { throw new McpOAuthOrchestrationCanceledException(originals.ToImmutableArray(), canceled); }
        catch (Exception error)
        { throw new McpOAuthOrchestrationException("authorize", originals.ToImmutableArray(), error); }
    }
    private async Task<McpOAuthAuthorizationOutcome> RunFlow(McpOAuthOrchestrationOptions options,
        List<McpOAuthOrchestrationOriginal> originals, CancellationToken token)
    {
        // A configured metadata document is trusted as configured, so it must not travel in clear text.
        if (options.AuthorizationServerMetadataUrl is { } configuredMetadata && !SecureEndpoint(configuredMetadata))
            throw new McpOAuthProtocolException("insecure_endpoint", "OAuth authorization server metadata URL must be HTTPS or exact loopback.");
        var discovered = await Work("discover", () => dependencies.Discover(options, token), originals, token).ConfigureAwait(false);
        if (discovered is null) throw new McpOAuthProtocolException("metadata_invalid", "Admitted discovery returned no state.");
        // With a configured metadata URL, discovery is not cached, so changing the URL applies at once.
        if (options.AuthorizationServerMetadataUrl is null && dependencies.SaveDiscovery is { } saveDiscovery)
            await Work("save-discovery", () => saveDiscovery(discovered, token), originals, token).ConfigureAwait(false);
        var resource = McpAdmittedOAuthDiscovery.SelectResource(options.ServerUrl, discovered.ResourceMetadata);
        // `||`, not `??`: an empty scope (for example from `scopes_supported: []`) falls through to the next source.
        var supportedScopes = discovered.ResourceMetadata is { } resourceMetadata &&
            McpAdmittedOAuthDiscovery.Strings(resourceMetadata.Value, "scopes_supported", false) is { } scopes ? string.Join(' ', scopes) : null;
        var scope = !string.IsNullOrEmpty(options.Scope) ? options.Scope : !string.IsNullOrEmpty(supportedScopes) ? supportedScopes : options.ClientMetadataScope;
        var client = await Work("read-client", () => dependencies.ReadClient(token), originals, token).ConfigureAwait(false);
        var metadata = discovered.AuthorizationServerMetadata;
        McpOAuthClientMetadataDocument? document = null;
        if (client is null && options.ClientMetadataDocument is { } describe)
        {
            Fence(token); document = InvokePhysical(() => describe(metadata));
            if (document is not null && (document.Url is null || document.RedirectUrl is null ||
                !Uri.TryCreate(document.Url, UriKind.Absolute, out var documentUrl) || documentUrl.Scheme != "https" || documentUrl.AbsolutePath == "/"))
                throw new McpOAuthProtocolException("client_metadata_url", "Invalid OAuth client metadata URL");
            // The document identifies the client; it is not stored.
            if (document is not null) client = new(document.Url);
        }
        if (client is null)
        {
            if (options.AuthorizationCode is { Length: > 0 }) throw new McpOAuthProtocolException("client_missing", "OAuth client information is missing during code exchange.");
            var saveRegisteredClient = dependencies.SaveClient ?? throw new McpOAuthProtocolException("client_persistence_missing", "OAuth client information cannot be persisted.");
            client = await Work("register-client", () => dependencies.RegisterClient(options, discovered, scope, token), originals, token).ConfigureAwait(false);
            if (client is null) throw new McpOAuthProtocolException("client_invalid", "Admitted registration returned no client.");
            await Work("save-client", () => saveRegisteredClient(client, token), originals, token).ConfigureAwait(false);
        }
        // The document's redirect URI may differ from the installed one, for example by a server-specific path.
        var context = new McpOAuthOrchestrationContext(options, discovered, client, resource, scope, RedirectUrl: document?.RedirectUrl);
        if (options.AuthorizationCode is { Length: > 0 } code)
        {
            // RFC 9207: never send a code from another authorization server to this one.
            if (metadata is { } issuerMetadata && (options.Iss is not null || issuerMetadata.Value.ValueKind == JsonValueKind.Object &&
                issuerMetadata.Value.TryGetProperty("authorization_response_iss_parameter_supported", out var promised) && promised.ValueKind == JsonValueKind.True))
            {
                var issuer = issuerMetadata.Value.ValueKind == JsonValueKind.Object && issuerMetadata.Value.TryGetProperty("issuer", out var named) &&
                    named.ValueKind == JsonValueKind.String ? named.GetString()! : "";
                if (!string.Equals(options.Iss, issuer, StringComparison.Ordinal)) throw new McpOAuthIssuerMismatchException(issuer, options.Iss);
            }
            var verifier = await Work("read-verifier", () => dependencies.ReadVerifier(token), originals, token).ConfigureAwait(false);
            var exchanged = await Work("exchange-code", () => dependencies.ExchangeCode(context, code, verifier, token), originals, token).ConfigureAwait(false);
            // A response without `scope` grants the requested scope; recorded so a step-up can keep it.
            await Work("save-tokens", () => dependencies.SaveTokens(McpOAuthScope.WithScope(exchanged, scope), token), originals, token).ConfigureAwait(false);
            return McpOAuthAuthorizationOutcome.Authorized;
        }
        var existing = options.SkipRefresh ? null : await Work("read-tokens", () => dependencies.ReadTokens(token), originals, token).ConfigureAwait(false);
        if (existing?.RefreshToken is { Length: > 0 } refreshToken)
        {
            try
            {
                var refreshed = await Work("refresh", () => dependencies.Refresh(context, refreshToken, token), originals, token).ConfigureAwait(false);
                // A refresh without `scope` keeps the scope of the grant (RFC 6749 §6).
                await Work("save-tokens", () => dependencies.SaveTokens(McpOAuthScope.WithScope(refreshed, existing.Scope), token), originals, token).ConfigureAwait(false);
                return McpOAuthAuthorizationOutcome.Authorized;
            }
            catch (McpOAuthFlowCanceledException) { throw; }
            catch (Exception error)
            {
                if (Selected(error) is McpOAuthTokenOperationFailure failure &&
                    (failure.Category == McpOAuthTokenFailureCategory.InsecureEndpoint || failure.Code != "server_error")) throw;
                Fence(token); // Preserve genuine cancellation and closing; only ordinary refresh errors may redirect.
            }
        }
        if (dependencies.ReadAuthorizationState is { } readState)
            context = context with { State = await Work("read-authorization-state", () => readState(token), originals, token).ConfigureAwait(false) };
        var authorization = await Work("begin-authorization", () => dependencies.BeginAuthorization(context, token), originals, token).ConfigureAwait(false);
        if (authorization is null || authorization.AuthorizationUrl is null || string.IsNullOrEmpty(authorization.CodeVerifier))
            throw new McpOAuthProtocolException("authorization_invalid", "Admitted authorization proposal is invalid.");
        await Work("save-verifier", () => dependencies.SaveVerifier(authorization.CodeVerifier, token), originals, token).ConfigureAwait(false);
        await Work("redirect", () => dependencies.Redirect(authorization.AuthorizationUrl, token), originals, token).ConfigureAwait(false);
        return McpOAuthAuthorizationOutcome.Redirect;
    }
    private static bool SecureEndpoint(Uri endpoint) => endpoint.IsAbsoluteUri &&
        (endpoint.Scheme == "https" || endpoint.Scheme == "http" && endpoint.IdnHost.ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or "[::1]");
    public ValueTask DisposeAsync()
    {
        RejectReentry();
        var parent = logical.Value; var ancestry = physical;
        lock (gate)
        {
            if (close is not null) return new(close);
            closing = true; var pending = active;
            close = Task.Run(() => RunClose(pending, parent, ancestry)); return new(close);
        }
    }
    private Task RunClose(Task? pending, Frame? parent, Physical? ancestry)
    {
        var previous = physical; var entry = new Physical(this, parent, ancestry); physical = entry;
        try { return CloseCore(pending); } finally { entry.Active = false; physical = previous; }
    }
    private async Task CloseCore(Task? pending)
    {
        var failures = new List<Exception>();
        Task? cancellation = null;
        try { cancellation = CancelOwnedAsync(lifetime); await cancellation.ConfigureAwait(false); }
        catch (Exception direct) { failures.Add(new McpOAuthFlowOriginalException("close-cancellation", cancellation, cancellation?.Exception ?? direct, direct)); }
        if (pending is not null)
            try { await pending.ConfigureAwait(false); }
            catch (OperationCanceledException direct) when (pending.IsCanceled)
            { failures.Add(new McpOAuthFlowCanceledException(pending, direct)); }
            catch (Exception direct) { failures.Add(new McpOAuthFlowOriginalException("close-active", pending, pending.Exception ?? direct, direct)); }
        try { lifetime.Dispose(); } catch (Exception error) { failures.Add(error); }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("OAuth active operation/cancellation/cleanup originals.", failures);
    }
}
