using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Resources;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;

namespace PiSharp.Extensions.Runtime.Mcp;

/// <summary>Admitted MCP session method bridge. No ambient transport acquisition, host publication or credentials.</summary>
public sealed class McpServerRuntime : IAsyncDisposable
{
    private sealed class Connection(IMcpAdmittedRequestChannel channel)
    {
        internal IMcpAdmittedRequestChannel Channel { get; } = channel;
        internal Lazy<Task> Close { get; } = new(channel.CloseAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        internal JsonData? Initialize;
        internal bool HasTools, HasResources, Ready;
        internal string? Instructions;
    }
    private readonly object gate = new();
    private readonly McpServerEntry entry;
    private readonly McpRuntimeOptions options;
    private readonly McpAdmittedChannelFactory acquire;
    private readonly McpCatalogPublisher publish;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim publicationGate = new(1, 1), refreshGate = new(1, 1);
    private readonly AsyncLocal<bool> publishing = new();
    private readonly HashSet<Task> operations = [];
    private readonly Lazy<Task> close;
    private Lazy<Task<Connection>>? opening;
    private Connection? connection;
    private sealed record Retirement(Connection Connection, IMcpAdmittedChannelRetirement? Deferred)
    {
        internal Task JoinAsync() => Deferred?.JoinAsync() ?? Connection.Close.Value;
    }
    private readonly Dictionary<Connection, Retirement> retiring = [];
    private McpRuntimeSnapshot snapshot;
    private ImmutableDictionary<string, string> nameOwners = ImmutableDictionary<string, string>.Empty;
    private ImmutableArray<McpPlannedTool> planned = [];
    private bool closed;
    private Exception? publicationFailure;

    public McpServerRuntime(McpServerEntry entry, McpRuntimeOptions options, McpAdmittedChannelFactory acquire, McpCatalogPublisher publish)
    {
        this.entry = entry ?? throw new ArgumentNullException(nameof(entry));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
        if (acquire.GetInvocationList().Length != 1 || publish.GetInvocationList().Length != 1)
            throw new ArgumentException("One owning channel acquisition and catalog publisher required.");
        if (options.Generation <= 0 || string.IsNullOrEmpty(options.ClientVersion) ||
            options.Roots is { } roots && roots.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException("MCP generation/version/roots shape differs.", nameof(options));
        if (options.Limits is not { MaximumPages: > 0 and <= 1000, MaximumTools: > 0, MaximumSchemaBytes: > 0,
            MaximumResponseBytes: > 0, MaximumCatalogBytes: > 0 }) throw new ArgumentException("Finite positive MCP limits required.", nameof(options));
        snapshot = new(options.Generation, 0, new(entry, [], Connected: false), null, []);
        close = new(CloseCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public McpRuntimeSnapshot Snapshot { get { lock (gate) return snapshot; } }

    /// <summary>Capture a resource callback from this ready runtime, with the actual prepared invocation.
    /// No channel escapes; the captured runtime generation remains distinct from extension owner generation.</summary>
    public McpResourceServer CaptureResourceServer(IExtensionToolInvocationContext invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (publishing.Value) throw new InvalidOperationException("MCP callback cannot capture its owning runtime.");
        var identity = new McpInvocationIdentity(invocation.OwnerId, invocation.OwnerGeneration, invocation.SessionGeneration, invocation.ToolCallId, invocation.ParentToolCallId);
        if (string.IsNullOrWhiteSpace(identity.OwnerId) || identity.OwnerGeneration <= 0 || identity.SessionGeneration <= 0 || string.IsNullOrWhiteSpace(identity.ToolCallId))
            throw new InvalidOperationException("Resource capture requires an actual native invocation identity.");
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(McpServerRuntime));
            if (publicationFailure is not null || connection is not { Ready: true, HasResources: true } || !snapshot.Catalog.Connected)
                throw new InvalidOperationException("Only a ready admitted resource runtime can be captured.");
        }
        ValueTask<JsonData> Request(long generation, string method, JsonData? parameters, McpRequestOptions requestOptions, CancellationToken token)
        {
            if (requestOptions.InvocationIdentity != identity) throw new InvalidOperationException("Resource callback belongs to a different captured invocation.");
            return new(RequestResourceAsync(generation, method, parameters, requestOptions, invocation, token));
        }
        return new(entry.Name, options.Generation, entry.Config.TimeoutSeconds * 1000, Request);
    }

    /// <summary>Prepared host resource dispatch through the existing operation/connection/retirement owner.
    /// A definite disconnect retires this lease once; the read/list original is never replayed.</summary>
    public Task<JsonData> RequestResourceAsync(long capturedGeneration, string method, JsonData? parameters,
        McpRequestOptions requestOptions, IExtensionToolInvocationContext invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestOptions); ArgumentNullException.ThrowIfNull(invocation);
        if (publishing.Value) throw new InvalidOperationException("MCP callback cannot reenter an owning runtime operation.");
        if (capturedGeneration != options.Generation) throw new InvalidOperationException("Stale captured MCP resource generation.");
        var identity = new McpInvocationIdentity(invocation.OwnerId, invocation.OwnerGeneration, invocation.SessionGeneration, invocation.ToolCallId, invocation.ParentToolCallId);
        if (requestOptions.InvocationIdentity != identity || string.IsNullOrWhiteSpace(identity.OwnerId) || identity.OwnerGeneration <= 0 ||
            identity.SessionGeneration <= 0 || string.IsNullOrWhiteSpace(identity.ToolCallId))
            throw new InvalidOperationException("Resource requests require the actual captured native invocation identity.");
        if (!double.IsFinite(requestOptions.TimeoutMilliseconds) || requestOptions.TimeoutMilliseconds <= 0 || requestOptions.MaximumResponseBytes <= 0 || requestOptions.OnProgress is not null)
            throw new ArgumentException("Bounded resource request options with runtime-owned progress required.", nameof(requestOptions));
        if (method is not ("resources/list" or "resources/templates/list" or "resources/read")) throw new ArgumentException("Only resource methods are admitted.", nameof(method));
        if (parameters is not null && parameters.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Resource parameters must be an object.", nameof(parameters));
        var key = method == "resources/read" ? "uri" : "cursor";
        if (parameters is not null && (parameters.Value.EnumerateObject().Any(property => property.Name != key) ||
            parameters.Value.TryGetProperty(key, out var supplied) && supplied.ValueKind != JsonValueKind.String) ||
            method == "resources/read" && (parameters is null || !String(parameters.Value, "uri", out var uri) || string.IsNullOrWhiteSpace(uri)))
            throw new ArgumentException("Invalid resource method parameters.", nameof(parameters));
        return ResourceRetriedAsync(method, parameters, requestOptions, invocation, cancellationToken);
    }
    /// <summary>runtime.ts withClient(readOnly): reading and listing resources is retried once after a transient HTTP error (250 ms
    /// later), and once on a new session when the server no longer knows the session.</summary>
    private async Task<JsonData> ResourceRetriedAsync(string method, JsonData? parameters, McpRequestOptions requestOptions,
        IExtensionToolInvocationContext invocation, CancellationToken cancellationToken)
    {
        try { return await ResourceOwnedAsync(method, parameters, requestOptions, invocation, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested && entry.Config.Transport == McpTransportKind.Http &&
            (SessionExpired(error) || Find<McpHttpStatusException>(error) is { } status && Transient(status)))
        {
            if (SessionExpired(error)) { try { await DisconnectCoreAsync().ConfigureAwait(false); } catch (Exception) { /* The retry opens a new connection. */ } }
            else await Task.Delay(ConnectRetryDelays[0], cancellationToken).ConfigureAwait(false);
        }
        return await ResourceOwnedAsync(method, parameters, requestOptions, invocation, cancellationToken).ConfigureAwait(false);
    }
    private async Task<JsonData> ResourceOwnedAsync(string method, JsonData? parameters, McpRequestOptions supplied,
        IExtensionToolInvocationContext invocation, CancellationToken cancellationToken)
    {
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, invocation.OperationCancellationToken,
            invocation.SessionCancellationToken, invocation.ExtensionLifetimeCancellationToken);
        Task<JsonData>? runtimeOriginal = null; CancellationToken dispatchedToken = default;
        try
        {
            runtimeOriginal = RunAsync(async requestToken =>
            {
                dispatchedToken = requestToken;
                var current = await GetConnectionAsync(requestToken).ConfigureAwait(false);
                if (!current.HasResources) throw new InvalidOperationException("MCP server has no resource capability.");
                var progress = new ResourceInvocationProgress(entry.Name, method, invocation, requestToken, publishing, options.Limits.MaximumTools);
                var request = supplied with { TimeoutMilliseconds = Math.Min(supplied.TimeoutMilliseconds, entry.Config.TimeoutSeconds * 1000),
                    MaximumResponseBytes = Math.Min(supplied.MaximumResponseBytes, options.Limits.MaximumResponseBytes), OnProgress = progress.ReportAsync };
                Task<JsonData>? original = null; JsonData? result = null; Exception? failure = null; var channelInvoked = false;
                try
                {
                    lock (gate)
                    {
                        requestToken.ThrowIfCancellationRequested();
                        if (closed || !ReferenceEquals(connection, current) || !current.Ready) throw new InvalidOperationException("MCP resource lease has retired.");
                        var prior = publishing.Value; publishing.Value = true;
                        try { channelInvoked = true; original = current.Channel.RequestAsync(method, parameters, request, requestToken).AsTask(); }
                        finally { publishing.Value = prior; }
                    }
                    result = await JoinResourceOriginal(original ?? throw new InvalidOperationException("Missing resource request original."), "channel request", requestToken).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure = original is null && channelInvoked && error is OperationCanceledException ?
                        new McpResourceCallbackException("synchronous channel request", null, error) : error;
                }
                var progressFailure = await progress.JoinAsync().ConfigureAwait(false);
                var originalFaults = original?.Exception?.Flatten().InnerExceptions;
                if (failure is McpRuntimeDisconnectedException || originalFaults?.Any(error => error is McpRuntimeDisconnectedException) == true)
                {
                    try { await RetireDisconnectedAsync(current).ConfigureAwait(false); }
                    catch (Exception cleanup)
                    {
                        // Retain the physical retirement aggregate even if its async owner selected only one exception.
                        var physical = current.Close.IsValueCreated ? current.Close.Value : null;
                        var retained = physical is { IsFaulted: true } ? new McpResourceCallbackException("channel retirement", physical, physical.Exception!) : cleanup;
                        failure = failure is null ? retained : new AggregateException(failure, retained);
                    }
                }
                if (failure is not null && progressFailure is not null && !ReferenceEquals(failure, progressFailure)) throw new AggregateException(failure, progressFailure);
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                if (progressFailure is not null) ExceptionDispatchInfo.Capture(progressFailure).Throw();
                requestToken.ThrowIfCancellationRequested();
                if (result is null) throw new McpRuntimeProtocolException("Missing MCP resource result.");
                CheckResponse(result);
                if (Encoding.UTF8.GetByteCount(result.ToString()) > request.MaximumResponseBytes || result.Value.ValueKind != JsonValueKind.Object ||
                    !result.Value.TryGetProperty(method == "resources/read" ? "contents" : method == "resources/list" ? "resources" : "resourceTemplates", out var items) || items.ValueKind != JsonValueKind.Array)
                    throw new McpRuntimeProtocolException("Invalid or oversized MCP resource method result.");
                return result;
            }, owned.Token);
            return await runtimeOriginal.ConfigureAwait(false);
        }
        catch (OperationCanceledException cancellation)
        {
            var proved = runtimeOriginal is { IsCanceled: true } &&
                (owned.IsCancellationRequested && cancellation.CancellationToken == owned.Token ||
                 dispatchedToken.IsCancellationRequested && cancellation.CancellationToken == dispatchedToken);
            if (!proved) throw new McpResourceCallbackException("unowned runtime cancellation", runtimeOriginal, (Exception?)runtimeOriginal?.Exception ?? cancellation);
            // Project only already-proven linked-token cancellation back to the supplied leaf token domain.
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw;
        }
    }
    private static async Task<T> JoinResourceOriginal<T>(Task<T> original, string operation, CancellationToken token)
    {
        try { return await original.ConfigureAwait(false); }
        catch (Exception failure)
        {
            if (original.IsCanceled && token.IsCancellationRequested && failure is OperationCanceledException cancellation && cancellation.CancellationToken == token) throw;
            throw new McpResourceCallbackException(operation, original, (Exception?)original.Exception ?? failure);
        }
    }
    public Task<McpRuntimeSnapshot> ConnectAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    { await GetConnectionAsync(token).ConfigureAwait(false); return Snapshot; }, cancellationToken);

    /// <summary>runtime.ts reconnect: drop the current connection (joining its close) and connect again, republishing the
    /// tools, for example after a sign-in or from the `/mcp` manager.</summary>
    public Task<McpRuntimeSnapshot> ReconnectAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        await DisconnectCoreAsync().ConfigureAwait(false);
        await GetConnectionAsync(token).ConfigureAwait(false); return Snapshot;
    }, cancellationToken);

    /// <summary>runtime.ts fetchResources: how many resources and resource templates the server lists, for the counts of `mcp list`
    /// and `/mcp`. MCP App resources (`ui://` URIs, `profile=mcp-app` HTML) are left out; a list that fails counts none, and a
    /// server without the resources capability has none.</summary>
    public Task<(int Resources, int Templates)> CountResourcesAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        var current = await GetConnectionAsync(token).ConfigureAwait(false);
        if (!current.HasResources) return (0, 0);
        async Task<int> CountAsync(string method, string key)
        {
            var count = 0; var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null;
            try
            {
                for (var page = 0; page < options.Limits.MaximumPages; page++)
                {
                    var response = await current.Channel.RequestAsync(method, cursor is null ? null : Json(new { cursor }), RequestOptions(), token).ConfigureAwait(false);
                    CheckResponse(response);
                    if (response.Value.ValueKind != JsonValueKind.Object || !response.Value.TryGetProperty(key, out var items) || items.ValueKind != JsonValueKind.Array) return count;
                    count += items.EnumerateArray().Count(item => !IsMcpAppResource(item));
                    if (!response.Value.TryGetProperty("nextCursor", out var next) || next.ValueKind != JsonValueKind.String || !cursors.Add(next.GetString()!)) return count;
                    cursor = next.GetString();
                }
                return count;
            }
            catch (Exception) when (!token.IsCancellationRequested) { return 0; }
        }
        var counts = await Task.WhenAll(CountAsync("resources/list", "resources"), CountAsync("resources/templates/list", "resourceTemplates")).ConfigureAwait(false);
        return (counts[0], counts[1]);
    }, cancellationToken);

    /// <summary>resources.ts isMcpAppResource: user interfaces for hosts that render them.</summary>
    private static bool IsMcpAppResource(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        var uri = String(item, "uri", out var direct) ? direct : String(item, "uriTemplate", out var template) ? template : "";
        if (uri!.StartsWith("ui://", StringComparison.Ordinal)) return true;
        return String(item, "mimeType", out var mime) && System.Text.RegularExpressions.Regex.IsMatch(mime!, ";\\s*profile\\s*=\\s*\"?mcp-app\"?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    /// <summary>runtime.ts signOut: drop the current connection without reconnecting; the next call connects again.</summary>
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => RunAsync(async _ =>
    { await DisconnectCoreAsync().ConfigureAwait(false); return true; }, cancellationToken);

    private async Task DisconnectCoreAsync()
    {
        Lazy<Task<Connection>>? pending; lock (gate) pending = opening;
        if (pending is not null) try { await pending.Value.ConfigureAwait(false); } catch (Exception) { /* The failed attempt left no connection. */ }
        Connection? current; lock (gate) current = connection;
        if (current is not null) await RetireDisconnectedAsync(current).ConfigureAwait(false);
    }

    public Task<McpRuntimeSnapshot> RefreshToolsAsync(CancellationToken cancellationToken = default) => RunAsync(async token =>
    {
        await refreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var current = await GetConnectionAsync(token).ConfigureAwait(false);
            ImmutableArray<JsonData> tools;
            try { tools = current.HasTools ? await ListToolsAsync(current, token).ConfigureAwait(false) : ImmutableArray<JsonData>.Empty; }
            catch (McpRuntimeDisconnectedException failure)
            {
                try { await RetireDisconnectedAsync(current).ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
                throw;
            }
            await PublishAsync(current, tools, token).ConfigureAwait(false);
            return Snapshot;
        }
        finally { refreshGate.Release(); }
    }, cancellationToken);

    /// <summary>The channel/host notification pump must await this original; no detached refresh tasks are created.</summary>
    public Task<bool> HandleNotificationAsync(string method, CancellationToken cancellationToken = default)
    {
        if (method != "notifications/tools/list_changed") return Task.FromResult(false);
        return RefreshNotificationAsync(cancellationToken);
    }
    private async Task<bool> RefreshNotificationAsync(CancellationToken token)
    { await RefreshToolsAsync(token).ConfigureAwait(false); return true; }

    /// <summary>Host invokes this only inside its prepared/final-authorized tool callback. Raw MCP result remains owned JSON.</summary>
    public Task<JsonData> CallToolAsync(string toolName, JsonData arguments, IExtensionToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolName); ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(context);
        if (publishing.Value) throw new InvalidOperationException("MCP callback cannot reenter an owning runtime operation.");
        if (arguments.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("MCP arguments object required.", nameof(arguments));
        return CallOwnedAsync(toolName, arguments, context, cancellationToken);
    }
    private async Task<JsonData> CallOwnedAsync(string toolName, JsonData arguments, IExtensionToolInvocationContext context, CancellationToken token)
    {
        var identity = new McpInvocationIdentity(context.OwnerId, context.OwnerGeneration, context.SessionGeneration, context.ToolCallId, context.ParentToolCallId);
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(token, context.OperationCancellationToken,
            context.SessionCancellationToken, context.ExtensionLifetimeCancellationToken);
        return await RunAsync(async requestToken =>
        {
            for (var attempt = 1; ; attempt++)
            {
            var current = await GetConnectionAsync(requestToken).ConfigureAwait(false);
            var progress = new InvocationProgress(entry.Name, toolName, context, requestToken, publishing);
            JsonData? result = null; Exception? failure = null;
            try
            {
                result = await current.Channel.RequestAsync("tools/call", Json(new { name = toolName, arguments = arguments.Value }),
                    RequestOptions(progress.ReportAsync) with { InvocationIdentity = identity }, requestToken).ConfigureAwait(false);
            }
            catch (Exception error) { failure = error; }
            var progressFailure = await progress.JoinAsync().ConfigureAwait(false);
            // runtime.ts withClient: the server no longer knows the session (restart, deploy), so it did not run the call. Retry once on
            // a new session; other failures are not retried, since the server may already have run the call.
            if (attempt == 1 && failure is not null && progressFailure is null && SessionExpired(failure))
            {
                try { await RetireDisconnectedAsync(current).ConfigureAwait(false); } catch (Exception) { /* The retry opens a new connection. */ }
                continue;
            }
            if (failure is McpRuntimeDisconnectedException)
                try { await RetireDisconnectedAsync(current).ConfigureAwait(false); } catch (Exception cleanup) { failure = new AggregateException(failure, cleanup); }
            if (failure is not null && progressFailure is not null && !ReferenceEquals(failure, progressFailure)) throw new AggregateException(failure, progressFailure);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (progressFailure is not null) ExceptionDispatchInfo.Capture(progressFailure).Throw();
            requestToken.ThrowIfCancellationRequested();
            if (result is null) throw new McpRuntimeProtocolException("Missing MCP tools/call result");
            CheckResponse(result); return ValidateToolResult(result);
            }
        }, owned.Token).ConfigureAwait(false);
    }

    /// <summary>The HTTP server answered 404 for the session (McpSessionExpiredError).</summary>
    private static bool SessionExpired(Exception error) => Find<McpHttpStatusException>(error) is { SessionExpired: true };
    /// <summary>runtime.ts isTransientError: network failures and 408, 429 and 5xx other than 501.</summary>
    private static bool Transient(Exception error) =>
        Find<McpHttpStatusException>(error) is { } status ? status.StatusCode is 408 or 429 || status.StatusCode >= 500 && status.StatusCode != 501
            : Find<HttpRequestException>(error) is not null;
    private static T? Find<T>(Exception? error) where T : Exception
    {
        for (; error is not null; error = error.InnerException)
        {
            if (error is T match) return match;
            if (error is AggregateException aggregate) foreach (var inner in aggregate.InnerExceptions) if (Find<T>(inner) is { } nested) return nested;
        }
        return null;
    }
    /// <summary>runtime.ts CONNECT_RETRY_DELAYS_MS: delays between attempts to connect to an HTTP server that failed transiently.</summary>
    private static readonly TimeSpan[] ConnectRetryDelays = [TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];

    private async Task<Connection> GetConnectionAsync(CancellationToken token)
    {
        await JoinRetirementsAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); Lazy<Task<Connection>> owner;
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(McpServerRuntime));
            if (!entry.Config.Enabled) throw new InvalidOperationException("MCP server is disabled.");
            if (publicationFailure is not null) throw new InvalidOperationException("MCP publication outcome requires owning-host resolution.", publicationFailure);
            if (connection is { Ready: true } connected) return connected;
            owner = opening ??= new(ConnectCoreAsync, LazyThreadSafetyMode.ExecutionAndPublication);
        }
        // A canceled waiter does not abandon the shared physical startup original. Close owns its lifetime token.
        var result = await owner.Value.ConfigureAwait(false); token.ThrowIfCancellationRequested(); return result;
    }

    private async Task<Connection> ConnectOnceAsync()
    {
        Connection? acquired = null;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();
            var channel = await acquire(entry, lifetime.Token).ConfigureAwait(false);
            acquired = new(channel ?? throw new InvalidOperationException("An admitted channel lease is required."));
            lock (gate) { if (!closed) connection = acquired; }
            lifetime.Token.ThrowIfCancellationRequested();
            if (options.Roots is { } roots) await channel.ConfigureRootsAsync(roots, lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            await channel.StartAsync(lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            var initialize = await channel.RequestAsync("initialize", Json(new
            {
                protocolVersion = "2025-11-25", capabilities = options.Roots is null ? JsonData.EmptyObject.Value : Json(new { roots = new { } }).Value,
                clientInfo = new { name = "pi", version = options.ClientVersion }
            }), RequestOptions(), lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested(); CheckResponse(initialize);
            var value = initialize.Value;
            if (value.ValueKind != JsonValueKind.Object || !String(value, "protocolVersion", out var version) ||
                !value.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("serverInfo", out var info) || info.ValueKind != JsonValueKind.Object ||
                !String(info, "name", out _) || !String(info, "version", out _) ||
                value.TryGetProperty("instructions", out var instructions) && instructions.ValueKind != JsonValueKind.String)
                throw new McpRuntimeProtocolException("Invalid MCP initialize result");
            if (version is not ("2025-11-25" or "2025-06-18" or "2025-03-26" or "2024-11-05"))
                throw new McpRuntimeProtocolException("MCP server selected unsupported protocol version " + version);
            await channel.SetProtocolVersionAsync(version!, lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            await channel.NotifyAsync("notifications/initialized", null, lifetime.Token).ConfigureAwait(false);
            lifetime.Token.ThrowIfCancellationRequested();
            acquired.HasTools = capabilities.TryGetProperty("tools", out var toolsCapability) && Truthy(toolsCapability);
            acquired.HasResources = capabilities.TryGetProperty("resources", out _);
            acquired.Instructions = value.TryGetProperty("instructions", out instructions) ? instructions.GetString()?.Trim() : null;
            if (string.IsNullOrEmpty(acquired.Instructions)) acquired.Instructions = null;
            var tools = acquired.HasTools ? await ListToolsAsync(acquired, lifetime.Token).ConfigureAwait(false) : ImmutableArray<JsonData>.Empty;
            acquired.Initialize = initialize;
            await PublishAsync(acquired, tools, lifetime.Token, reconnect: true).ConfigureAwait(false);
            lock (gate) acquired.Ready = !closed;
            lifetime.Token.ThrowIfCancellationRequested();
            return acquired;
        }
        catch (Exception failure)
        {
            if (acquired is not null)
            {
                lock (gate) { if (ReferenceEquals(connection, acquired)) connection = null; }
                try { await acquired.Close.Value.ConfigureAwait(false); }
                catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
            }
            throw;
        }
    }

    /// <summary>runtime.ts open: HTTP servers that fail with a transient error (network, 408, 429, 5xx) get two more attempts, 250 ms
    /// and 1 s apart; closing the runtime stops the wait.</summary>
    private async Task<Connection> ConnectCoreAsync()
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { return await ConnectOnceAsync().ConfigureAwait(false); }
                catch (Exception error) when (entry.Config.Transport == McpTransportKind.Http && attempt < ConnectRetryDelays.Length &&
                    !lifetime.IsCancellationRequested && Transient(error))
                {
                    try { await Task.Delay(ConnectRetryDelays[attempt], lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { ExceptionDispatchInfo.Capture(error).Throw(); }
                }
            }
        }
        finally { lock (gate) opening = null; }
    }

    private async Task<ImmutableArray<JsonData>> ListToolsAsync(Connection current, CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<JsonData>(); var cursors = new HashSet<string>(StringComparer.Ordinal); string? cursor = null; long retainedBytes = 0;
        for (var page = 0; page < options.Limits.MaximumPages; page++)
        {
            var response = await current.Channel.RequestAsync("tools/list", cursor is null ? null : Json(new { cursor }),
                RequestOptions(), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); CheckResponse(response); var value = response.Value;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
                throw new McpRuntimeProtocolException("Invalid MCP tools/list result");
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object || !String(tool, "name", out _) ||
                    !tool.TryGetProperty("inputSchema", out var schema) || schema.ValueKind != JsonValueKind.Object)
                    throw new McpRuntimeProtocolException("Invalid entry in MCP tools/list result");
                retainedBytes += Encoding.UTF8.GetByteCount(tool.GetRawText());
                if (result.Count >= options.Limits.MaximumTools || retainedBytes > options.Limits.MaximumCatalogBytes ||
                    Encoding.UTF8.GetByteCount(schema.GetRawText()) > options.Limits.MaximumSchemaBytes)
                    throw new McpRuntimeProtocolException("MCP tools/list configured tool/schema/catalog limit exceeded");
                result.Add(JsonData.FromElement(tool));
            }
            if (!value.TryGetProperty("nextCursor", out var next)) return result.ToImmutable();
            if (next.ValueKind != JsonValueKind.String) throw new McpRuntimeProtocolException("Invalid MCP tools/list cursor");
            cursor = next.GetString()!;
            if (!cursors.Add(cursor)) throw new McpRuntimeProtocolException("MCP tools/list returned duplicate cursor: " + cursor);
        }
        throw new McpRuntimeProtocolException("MCP tools/list exceeded " + options.Limits.MaximumPages.ToString(CultureInfo.InvariantCulture) + " pages");
    }

    private async Task PublishAsync(Connection current, ImmutableArray<JsonData> originals, CancellationToken token, bool reconnect = false)
    {
        await publicationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested(); McpRuntimeSnapshot previous; ImmutableArray<McpPlannedTool> old; ImmutableDictionary<string, string> owners;
            lock (gate)
            {
                if (closed || !ReferenceEquals(connection, current)) throw new OperationCanceledException(lifetime.Token);
                if (publicationFailure is not null) throw new InvalidOperationException("MCP publication outcome requires owning-host resolution.", publicationFailure);
                previous = snapshot; old = planned; owners = nameOwners;
            }
            var tools = originals.Select(raw =>
            {
                var value = raw.Value;
                var annotationTitle = value.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Object && String(annotations, "title", out var title) ? title : null;
                return new McpOfferedTool(value.GetProperty("name").GetString()!, JsonData.FromElement(value.GetProperty("inputSchema")),
                    String(value, "description", out var description) ? description : null, String(value, "title", out var toolTitle) ? toolTitle : null, annotationTitle);
            }).ToImmutableArray();
            var catalog = new McpServerToolSnapshot(entry, tools, current.Instructions, HasResources: current.HasResources);
            // A reconnect that finds the published tools unchanged (a new session after an expired one, `/mcp` reconnect) needs no new
            // catalog: the registered tools reach the new connection. This also lets a call reconnect while its run holds the catalog.
            if (reconnect && previous.Revision > 0 && previous.Catalog.Instructions == current.Instructions && previous.Catalog.HasResources == current.HasResources &&
                previous.OriginalTools.Select(tool => tool.ToString()).SequenceEqual(originals.Select(tool => tool.ToString()), StringComparer.Ordinal))
            {
                lock (gate) snapshot = previous with { Catalog = previous.Catalog with { Connected = true }, InitializeResult = current.Initialize };
                return;
            }
            var next = new McpRuntimeSnapshot(options.Generation, checked(previous.Revision + 1), catalog, current.Initialize, originals);
            var plan = McpCatalogPlanner.Plan([catalog], previousNameOwners: owners);
            var names = plan.Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            var withdrawn = old.Where(tool => !names.Contains(tool.Name)).Select(tool => tool with { Exposure = ToolExposure.Hidden, McpExposure = McpExposure.Hidden }).ToImmutableArray();
            publishing.Value = true;
            McpCatalogPublicationReceipt receipt;
            try
            {
                receipt = await publish(new(next, plan.Tools, withdrawn), token).ConfigureAwait(false);
                if (!receipt.Published || receipt.Generation != next.Generation || receipt.Revision != next.Revision)
                    throw new InvalidOperationException("Matching actual MCP catalog publication receipt required.");
            }
            catch (Exception error) { lock (gate) publicationFailure ??= error; throw; }
            finally { publishing.Value = false; }
            // The matching receipt is retained even when cancellation follows a completed host commit.
            lock (gate) { snapshot = next; planned = plan.Tools; nameOwners = plan.NameOwners; }
        }
        finally { publicationGate.Release(); }
    }

    private async Task RetireDisconnectedAsync(Connection previous)
    {
        Retirement retained;
        bool callback;
        lock (gate)
        {
            callback = previous.Channel is IMcpNotificationRetirementChannel { IsInNotificationCallback: true };
            if (!retiring.TryGetValue(previous, out retained!))
            {
                // Queue ownership before clearing the connection; never poison Close's Lazy with a
                // borrowed callback self-wait rejection or drop the retired lease after disconnect.
                var deferred = callback ? ((IMcpNotificationRetirementChannel)previous.Channel).RetireAfterNotification() : null;
                retained = new(previous, deferred); retiring.Add(previous, retained);
            }
            if (ReferenceEquals(connection, previous))
            { connection = null; snapshot = snapshot with { Catalog = snapshot.Catalog with { Connected = false } }; }
        }
        if (!callback) { await retained.JoinAsync().ConfigureAwait(false); lock (gate) retiring.Remove(previous); }
    }
    private async Task JoinRetirementsAsync()
    {
        Retirement[] originals; lock (gate) originals = retiring.Values.ToArray();
        var failures = new List<Exception>();
        foreach (var original in originals)
        {
            try { await original.JoinAsync().ConfigureAwait(false); lock (gate) retiring.Remove(original.Connection); }
            catch (Exception error) { if (!failures.Any(existing => ReferenceEquals(existing, error))) failures.Add(error); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> run, CancellationToken token)
    {
        if (publishing.Value) throw new InvalidOperationException("MCP callback cannot reenter an owning runtime operation.");
        token.ThrowIfCancellationRequested(); var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(McpServerRuntime));
            if (publicationFailure is not null) throw new InvalidOperationException("MCP publication outcome requires owning-host resolution.", publicationFailure);
            operations.Add(settled.Task);
        }
        try { using var owned = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token); return await run(owned.Token).ConfigureAwait(false); }
        finally { lock (gate) operations.Remove(settled.Task); settled.TrySetResult(); }
    }

    public Task CloseAsync()
    {
        if (publishing.Value) throw new InvalidOperationException("MCP callback cannot await its own close.");
        lock (gate)
        {
            if (connection?.Channel is IMcpNotificationRetirementChannel { IsInNotificationCallback: true } ||
                retiring.Keys.Any(item => item.Channel is IMcpNotificationRetirementChannel { IsInNotificationCallback: true }))
                throw new InvalidOperationException("MCP notification callback cannot join its runtime close");
            closed = true;
        }
        return close.Value;
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
    private async Task CloseCoreAsync()
    {
        var failures = new List<Exception>(); Connection? owned; Task[] admitted; Retirement[] retained;
        lock (gate) { owned = connection; admitted = operations.ToArray(); retained = retiring.Values.ToArray(); }
        var retirementOriginals = new List<Task>();
        Task? cancellation = null, physicalClose = null;
        // A request cancellation callback can join physical work until the channel initiates stop.
        // Capture both actual originals before joining either; CancelAsync alone is not physical stop.
        try { cancellation = lifetime.CancelAsync(); } catch (Exception error) { failures.Add(error); }
        if (owned is not null) try { physicalClose = owned.Close.Value; } catch (Exception error) { failures.Add(error); }
        foreach (var original in retained)
        {
            // External owner close starts physical stop immediately, even if notification retirement
            // is still waiting for its borrowed callback to return.
            try { retirementOriginals.Add(original.Connection.Close.Value); } catch (Exception error) { failures.Add(error); }
            try { retirementOriginals.Add(original.JoinAsync()); } catch (Exception error) { failures.Add(error); }
        }
        if (cancellation is not null) try { await cancellation.ConfigureAwait(false); } catch (Exception error) { failures.Add(RetainCloseFailure(cancellation, error)); }
        if (physicalClose is not null) try { await physicalClose.ConfigureAwait(false); } catch (Exception error) { failures.Add(RetainCloseFailure(physicalClose, error)); }
        // Startup that acquires a lease after the close fence closes it itself before its original settles.
        await Task.WhenAll(admitted).ConfigureAwait(false);
        // A disconnect may have retired a lease after the first close snapshot. Its exact receipt
        // stays reachable until this owner has joined all admitted runtime operations.
        lock (gate) retained = retiring.Values.ToArray();
        foreach (var original in retained)
        {
            try { retirementOriginals.Add(original.Connection.Close.Value); } catch (Exception error) { failures.Add(error); }
            try { retirementOriginals.Add(original.JoinAsync()); } catch (Exception error) { failures.Add(error); }
        }
        foreach (var original in retirementOriginals.Distinct())
            try { await original.ConfigureAwait(false); }
            catch (Exception error)
            {
                var retainedFailure = RetainCloseFailure(original, error);
                if (!failures.Any(existing => ReferenceEquals(existing, retainedFailure))) failures.Add(retainedFailure);
            }
        lock (gate) { connection = null; snapshot = snapshot with { Catalog = snapshot.Catalog with { Connected = false } }; }
        lifetime.Dispose(); publicationGate.Dispose(); refreshGate.Dispose();
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException(failures);
    }

    private static Exception RetainCloseFailure(Task original, Exception selected)
    {
        if (original.Exception is { InnerExceptions.Count: > 1 } complete) return complete;
        if (selected is OperationCanceledException) return new McpResourceCallbackException("runtime close", original, (Exception?)original.Exception ?? selected);
        return selected;
    }

    private static JsonData ValidateToolResult(JsonData result)
    {
        var value = result.Value;
        if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Array)
            throw new McpRuntimeProtocolException("Invalid MCP tools/call result");
        if (value.TryGetProperty("structuredContent", out var structured) && structured.ValueKind != JsonValueKind.Object)
            throw new McpRuntimeProtocolException("Invalid MCP tools/call structured content");
        if (value.TryGetProperty("content", out _)) return result;
        var fields = value.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value, StringComparer.Ordinal);
        fields.Add("content", Array.Empty<object>()); return Json(fields);
    }
    private static bool String(JsonElement value, string name, out string? text)
    { text = null; if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false; text = property.GetString(); return true; }
    private static JsonData Json(object value) => JsonData.Parse(JsonSerializer.Serialize(value));
    private McpRequestOptions RequestOptions(McpProgressCallback? onProgress = null) =>
        new(entry.Config.TimeoutSeconds * 1000, onProgress) { MaximumResponseBytes = options.Limits.MaximumResponseBytes };
    private void CheckResponse(JsonData value)
    { if (Encoding.UTF8.GetByteCount(value.ToString()) > options.Limits.MaximumResponseBytes) throw new McpRuntimeProtocolException("MCP configured response byte limit exceeded"); }
    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.String => value.GetString()!.Length != 0,
        _ => true
    };

    private sealed class ResourceInvocationProgress(string server, string method, IExtensionToolInvocationContext context,
        CancellationToken token, AsyncLocal<bool> callback, int maximumUpdates)
    {
        private readonly object gate = new(); private readonly List<Task> originals = []; private Task pending = Task.CompletedTask; private bool closed;
        private Exception? admissionFailure;
        internal ValueTask ReportAsync(McpProgress progress, CancellationToken callbackToken)
        {
            lock (gate)
            {
                if (closed) throw new InvalidOperationException("Resource progress admission closed.");
                if (originals.Count >= maximumUpdates)
                {
                    admissionFailure ??= new McpRuntimeProtocolException("Resource progress configured update limit exceeded.");
                    throw admissionFailure;
                }
                var previous = pending;
                pending = ReportCoreAsync(previous, progress, callbackToken); originals.Add(pending);
                return new(pending);
            }
        }
        private async Task ReportCoreAsync(Task previous, McpProgress progress, CancellationToken callbackToken)
        {
            await Task.Yield();
            await previous.ConfigureAwait(false);
            using var update = CancellationTokenSource.CreateLinkedTokenSource(token, callbackToken);
            CheckCancellation(update.Token);
            var total = progress.Total is { } supplied ? "/" + supplied.ToString(CultureInfo.InvariantCulture) : "";
            var text = progress.Message ?? "Progress " + progress.Progress.ToString(CultureInfo.InvariantCulture) + total;
            var prior = callback.Value; callback.Value = true;
            try
            {
                // Preserve the physical ReportUpdateAsync task via an explicit retained non-generic join.
                Task report;
                try { report = context.ReportUpdateAsync(Json(new { content = new[] { new { type = "text", text } }, details = new { server, tool = method } }), update.Token).AsTask(); }
                catch (Exception failure) { throw new McpResourceCallbackException("synchronous progress update", null, failure); }
                try { await report.ConfigureAwait(false); }
                catch (Exception failure)
                {
                    if (report.IsCanceled && update.IsCancellationRequested && token.IsCancellationRequested && failure is OperationCanceledException cancellation && cancellation.CancellationToken == update.Token)
                        throw new OperationCanceledException(token);
                    throw new McpResourceCallbackException("progress update", report, (Exception?)report.Exception ?? failure);
                }
            }
            finally { callback.Value = prior; }
            CheckCancellation(update.Token);
        }
        private void CheckCancellation(CancellationToken updateToken)
        {
            if (!updateToken.IsCancellationRequested) return;
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            throw new McpResourceCallbackException("progress callback cancellation", null, new OperationCanceledException(updateToken));
        }
        internal async Task<Exception?> JoinAsync()
        {
            Task[] admitted; Exception? rejected; lock (gate) { closed = true; admitted = originals.ToArray(); rejected = admissionFailure; }
            var failures = new List<Exception>();
            if (rejected is not null) failures.Add(rejected);
            foreach (var original in admitted)
                try { await original.ConfigureAwait(false); }
                catch (Exception error)
                {
                    var retained = original.IsFaulted ? (Exception)original.Exception! :
                        token.IsCancellationRequested && error is OperationCanceledException cancellation && cancellation.CancellationToken == token ? error :
                        new McpResourceCallbackException("progress operation", original, error);
                    if (!failures.Any(existing => ReferenceEquals(existing, retained))) failures.Add(retained);
                }
            return failures.Count == 0 ? null : failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
    }
    private sealed class InvocationProgress(string server, string tool, IExtensionToolInvocationContext context, CancellationToken token, AsyncLocal<bool> callback)
    {
        private readonly object gate = new(); private Task pending = Task.CompletedTask; private bool closed;
        internal ValueTask ReportAsync(McpProgress progress, CancellationToken callbackToken)
        {
            lock (gate)
            {
                if (closed) throw new InvalidOperationException("MCP progress admission closed.");
                var previous = pending;
                pending = previous.ContinueWith(async _ =>
                {
                    await previous.ConfigureAwait(false);
                    using var update = CancellationTokenSource.CreateLinkedTokenSource(token, callbackToken);
                    update.Token.ThrowIfCancellationRequested();
                    var total = progress.Total is { } supplied ? "/" + supplied.ToString(CultureInfo.InvariantCulture) : "";
                    var text = progress.Message ?? "Progress " + progress.Progress.ToString(CultureInfo.InvariantCulture) + total;
                    callback.Value = true;
                    try { await context.ReportUpdateAsync(Json(new { content = new[] { new { type = "text", text } }, details = new { server, tool } }), update.Token).ConfigureAwait(false); }
                    finally { callback.Value = false; }
                    update.Token.ThrowIfCancellationRequested();
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
                return new(pending);
            }
        }
        internal async Task<Exception?> JoinAsync()
        { Task original; lock (gate) { closed = true; original = pending; } try { await original.ConfigureAwait(false); return null; } catch (Exception error) { return error; } }
    }
}
