// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/oauth.ts createMcpAuthProvider (refresh)
// and packages/mcp/src/oauth/types.ts parseOAuthTokens.
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>Affine caller admission. Store/exchange owners must route cancellation that can invoke
/// callbacks after an await through these hooks and join the returned original before returning.
/// Direct cancellation after awaited callback work is outside this admitted API.</summary>
public sealed class McpOAuthCancellationAdmission
{
    private readonly object gate = new();
    private McpAdmittedOAuthRefreshAdapter? owner;
    internal void Bind(McpAdmittedOAuthRefreshAdapter candidate)
    { lock (gate) { if (owner is not null) throw new InvalidOperationException("OAuth cancellation admission is affine."); owner = candidate; } }
    private McpAdmittedOAuthRefreshAdapter Capture()
    { lock (gate) return owner ?? throw new InvalidOperationException("OAuth cancellation admission is not bound."); }
    public void Cancel(CancellationTokenSource explicitlyBorrowedSource) => Capture().CancelOwned(explicitlyBorrowedSource);
    public Task CancelAsync(CancellationTokenSource explicitlyBorrowedSource) => Capture().CancelOwnedAsync(explicitlyBorrowedSource);
}

/// <summary>Borrowed refresh-only OAuth adapter. All returned callback work is owned by the
/// authenticated HTTP operation; there is no detached refresh, default store or browser flow.</summary>
public sealed class McpAdmittedOAuthRefreshAdapter
{
    private sealed class Frame(McpAdmittedOAuthRefreshAdapter owner, Frame? parent, PhysicalFrame? ancestry) { internal readonly McpAdmittedOAuthRefreshAdapter Owner = owner; internal readonly Frame? Parent = parent; internal readonly PhysicalFrame? Ancestry = ancestry; internal volatile bool Active = true; }
    private sealed class PhysicalFrame(McpAdmittedOAuthRefreshAdapter owner, Frame? logical, PhysicalFrame? parent)
    { internal readonly McpAdmittedOAuthRefreshAdapter Owner = owner; internal readonly Frame? Logical = logical; internal readonly PhysicalFrame? Parent = parent; internal volatile bool Active = true; }
    [ThreadStatic] private static PhysicalFrame? physical;
    private sealed class GuardedStore(McpAdmittedOAuthRefreshAdapter owner, IMcpAdmittedOAuthStateStore store) : IMcpAdmittedOAuthStateStore
    {
        public ValueTask<McpOAuthState?> LoadAsync() => owner.InvokeDependency(store.LoadAsync);
        public ValueTask SaveAsync(McpOAuthState value) => owner.InvokeDependency(() => store.SaveAsync(value));
    }
    private T InvokeDependency<T>(Func<T> invoke)
    {
        var previous = physical; var entry = new PhysicalFrame(this, frame.Value, previous); physical = entry;
        try { return invoke(); }
        finally { entry.Active = false; physical = previous; }
    }
    internal void CancelOwned(CancellationTokenSource source)
    { ArgumentNullException.ThrowIfNull(source); InvokeDependency(() => { source.Cancel(); return true; }); }
    internal Task CancelOwnedAsync(CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var logicalParent = frame.Value; var physicalParent = physical;
        // Return the actual cancellation original. Its caller must join it; nothing observes or detaches it here.
        return Task.Run(() =>
        {
            var previous = physical; var owned = new PhysicalFrame(this, logicalParent, physicalParent); physical = owned;
            try { source.Cancel(); }
            finally { owned.Active = false; physical = previous; }
        });
    }
    private readonly Uri server;
    private readonly McpAdmittedOAuthStateProvider state;
    private readonly McpAdmittedOAuthExchange exchange;
    private readonly McpOAuthAdmittedClient? configuredClient;
    private readonly McpAdmittedOAuthRefreshClientAuthentication? addClientAuthentication;
    private readonly int maximumBytes;
    private readonly object gate = new();
    private static readonly AsyncLocal<Frame?> frame = new();
    private Task? inFlight;
    public McpAdmittedOAuthRefreshAdapter(Uri exactServer, IMcpAdmittedOAuthStateStore explicitlyAdmittedStore,
        McpAdmittedOAuthExchange explicitlyAdmittedExchange, Func<double> unixMilliseconds, McpOAuthCancellationAdmission cancellationAdmission,
        McpOAuthAdmittedClient? configuredClient = null, int maximumBytes = 1_048_576)
        : this(exactServer, explicitlyAdmittedStore, explicitlyAdmittedExchange, unixMilliseconds, cancellationAdmission, configuredClient, maximumBytes, null)
    { }
    /// <summary>Explicit refresh-only customization admission; original constructor remains unchanged.
    /// The custom callback replaces default client authentication and must join all owned work before returning.</summary>
    public McpAdmittedOAuthRefreshAdapter(Uri exactServer, IMcpAdmittedOAuthStateStore explicitlyAdmittedStore,
        McpAdmittedOAuthExchange explicitlyAdmittedExchange, Func<double> unixMilliseconds, McpOAuthCancellationAdmission cancellationAdmission,
        McpOAuthAdmittedClient? configuredClient, int maximumBytes, McpAdmittedOAuthRefreshClientAuthentication? addClientAuthentication)
    {
        McpAdmittedOAuthDiscovery.ValidateAdmission(exactServer, explicitlyAdmittedExchange, maximumBytes);
        ArgumentNullException.ThrowIfNull(explicitlyAdmittedStore); ArgumentNullException.ThrowIfNull(unixMilliseconds);
        if (unixMilliseconds.GetInvocationList().Length != 1) throw new ArgumentException("One admitted OAuth clock required.");
        if (addClientAuthentication is { } custom && custom.GetInvocationList().Length != 1) throw new ArgumentException("One admitted custom refresh authentication callback required.");
        this.addClientAuthentication = addClientAuthentication;
        server = exactServer; exchange = (request, token) => InvokeDependency(() => explicitlyAdmittedExchange(request, token)); this.configuredClient = configuredClient; this.maximumBytes = maximumBytes;
        if (configuredClient is not null) ValidateClient(configuredClient);
        state = new(server, new GuardedStore(this, explicitlyAdmittedStore), () => InvokeDependency(() =>
        { var now = unixMilliseconds(); return double.IsFinite(now) ? now : throw new McpOAuthProtocolException("clock_invalid", "Admitted OAuth clock must return finite unix milliseconds."); }));
        ArgumentNullException.ThrowIfNull(cancellationAdmission); cancellationAdmission.Bind(this);
    }
    public McpAdmittedHttpAuthentication CreateAuthentication() => new(token => new(ReadTokenAsync(token)),
        (context, token) => new(UnauthorizedAsync(context, token)));
    private void RejectReentry()
    {
        var pending = new Stack<object>(); var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (frame.Value is { } logical) pending.Push(logical);
        if (physical is { } entry) pending.Push(entry);
        while (pending.Count != 0)
        {
            var item = pending.Pop(); if (!seen.Add(item)) continue;
            if (item is Frame current)
            {
                if (current.Active && ReferenceEquals(current.Owner, this)) throw new InvalidOperationException("OAuth callback cannot join its active provider ancestor work.");
                if (current.Parent is { } parent) pending.Push(parent);
                if (current.Ancestry is { } ancestry) pending.Push(ancestry);
            }
            else if (item is PhysicalFrame currentPhysical)
            {
                if (currentPhysical.Active && ReferenceEquals(currentPhysical.Owner, this)) throw new InvalidOperationException("OAuth dependency cannot join its physical owner or ancestor work.");
                if (currentPhysical.Parent is { } parent) pending.Push(parent);
                if (currentPhysical.Logical is { } ancestry) pending.Push(ancestry);
            }
        }
    }
    private async Task<string?> ReadTokenAsync(CancellationToken token)
    {
        RejectReentry(); token.ThrowIfCancellationRequested(); var previous = frame.Value; var owned = new Frame(this, previous, physical); frame.Value = owned;
        try { return (await ReadState(token).ConfigureAwait(false)).Tokens?.AccessToken; }
        finally { owned.Active = false; frame.Value = previous; }
    }
    private async Task UnauthorizedAsync(McpHttpUnauthorizedContext context, CancellationToken token)
    {
        RejectReentry(); ArgumentNullException.ThrowIfNull(context); token.ThrowIfCancellationRequested();
        if (context.ServerUrl != server) throw new InvalidOperationException("OAuth adapter belongs to the exact admitted MCP server.");
        var previous = frame.Value; var owned = new Frame(this, previous, physical); frame.Value = owned;
        try
        {
            var challenge = Challenge(context.Response);
            // The original skips refresh for insufficient scope and proceeds to authorization.
            // This bounded adapter refuses before refresh; browser/code acquisition is not admitted.
            if (challenge.Error == "insufficient_scope") throw new McpOAuthAuthorizationRequiredException();
            var current = await ReadState(token).ConfigureAwait(false);
            Task original;
            lock (gate)
            {
                if (inFlight is null && context.RejectedToken is not null && current.Tokens?.AccessToken is { } replacement && replacement != context.RejectedToken) return;
                original = inFlight ??= RefreshAsync(challenge.MetadataUrl, token);
            }
            await McpOAuthAdmittedWork.Invoke("shared-refresh", () => new ValueTask(original), token).ConfigureAwait(false);
        }
        finally { owned.Active = false; frame.Value = previous; }
    }
    private Task<McpOAuthState> ReadState(CancellationToken token) =>
        McpOAuthAdmittedWork.Invoke("state-read", () => new ValueTask<McpOAuthState>(state.ReadAsync()), token);
    private async Task RefreshAsync(Uri? resourceMetadataUrl, CancellationToken token)
    {
        var previous = frame.Value; var ancestry = physical;
        await Task.Yield(); // Publish the one shared original before invoking admitted dependencies; ancestry captured before yielding.
        var owned = new Frame(this, previous, ancestry); frame.Value = owned;
        try
        {
            token.ThrowIfCancellationRequested(); var stored = await ReadState(token).ConfigureAwait(false);
            var discovered = await Discover(stored.Discovery, resourceMetadataUrl, token).ConfigureAwait(false);
            var discoveryFields = new Dictionary<string, object?> { ["authorizationServerUrl"] = discovered.AuthorizationServerUrl };
            if (discovered.AuthorizationServerMetadata is { } metadata) discoveryFields["authorizationServerMetadata"] = metadata.Value;
            if (discovered.ResourceMetadata is { } resourceMetadata) discoveryFields["resourceMetadata"] = resourceMetadata.Value;
            if (resourceMetadataUrl is not null) discoveryFields["resourceMetadataUrl"] = resourceMetadataUrl.AbsoluteUri;
            var savedDiscovery = JsonData.FromElement(JsonSerializer.SerializeToElement(discoveryFields));
            token.ThrowIfCancellationRequested();
            await McpOAuthAdmittedWork.Invoke("save-discovery", () => new ValueTask(state.SaveDiscoveryAsync(savedDiscovery)), token).ConfigureAwait(false);
            var resource = McpAdmittedOAuthDiscovery.SelectResource(server, discovered.ResourceMetadata);
            var client = configuredClient ?? StoredClient(stored.ClientInformation);
            var refreshToken = stored.Tokens?.RefreshToken;
            if (client is null || string.IsNullOrEmpty(refreshToken)) throw new McpOAuthAuthorizationRequiredException();
            ValidateClient(client);
            var endpoint = discovered.AuthorizationServerMetadata is { } authorizationMetadata
                ? McpAdmittedOAuthDiscovery.Url(McpAdmittedOAuthDiscovery.Text(authorizationMetadata.Value, "token_endpoint", true)!)
                : new Uri(McpAdmittedOAuthDiscovery.Url(discovered.AuthorizationServerUrl), "/token");
            if (endpoint.Scheme != "https" && endpoint.IdnHost is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"))
                throw new McpOAuthProtocolException("insecure_endpoint", "OAuth token endpoint must be HTTPS or exact loopback.");
            var fields = new List<KeyValuePair<string, string>> { new("grant_type", "refresh_token"), new("refresh_token", refreshToken) };
            if (resource is not null) fields.Add(new("resource", resource));
            var headers = ImmutableDictionary<string, string>.Empty.Add("Accept", "application/json").Add("Content-Type", "application/x-www-form-urlencoded");
            if (addClientAuthentication is { } customAuthentication)
            {
                var proposal = new McpOAuthRefreshClientAuthenticationProposal(endpoint, client, discovered.AuthorizationServerMetadata, headers, fields, maximumBytes);
                try
                {
                    token.ThrowIfCancellationRequested();
                    await McpOAuthAdmittedWork.Invoke("refresh-client-authentication", () => InvokeDependency(() => customAuthentication(proposal, token)), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    (headers, fields) = proposal.Snapshot();
                }
                finally { proposal.Retire(); }
            }
            else
            {
                var supported = discovered.AuthorizationServerMetadata is { } declared ? McpAdmittedOAuthDiscovery.Strings(declared.Value, "token_endpoint_auth_methods_supported", false) ?? [] : [];
                var method = SelectAuthentication(client, supported);
                if (method == "client_secret_basic")
                {
                    if (string.IsNullOrEmpty(client.ClientSecret)) throw new McpOAuthProtocolException("client_authentication", "Explicit client secret is required by selected Basic authentication.");
                    headers = headers.Add("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(client.ClientId + ":" + client.ClientSecret)));
                }
                else
                {
                    fields.Add(new("client_id", client.ClientId));
                    if (method == "client_secret_post" && !string.IsNullOrEmpty(client.ClientSecret)) fields.Add(new("client_secret", client.ClientSecret));
                }
            }
            token.ThrowIfCancellationRequested();
            var body = string.Join("&", fields.Select(field => Form(field.Key) + "=" + Form(field.Value)));
            if (Encoding.UTF8.GetByteCount(body) > maximumBytes) throw new McpOAuthProtocolException("request_bound", "OAuth refresh body exceeds its admitted finite bound.");
            var response = await McpAdmittedOAuthDiscovery.Exchange(new(endpoint, HttpMethod.Post, headers, body, McpOAuthExchangePurpose.RefreshToken), exchange, token, maximumBytes).ConfigureAwait(false);
            // A refresh without `scope` keeps the scope of the grant (RFC 6749 §6), so a later step-up can keep it.
            var tokens = McpOAuthScope.WithScope(ParseTokens(response, refreshToken), stored.Tokens?.Scope);
            token.ThrowIfCancellationRequested();
            await McpOAuthAdmittedWork.Invoke("save-tokens", () => new ValueTask(state.SaveTokensAsync(tokens)), token).ConfigureAwait(false);
        }
        finally { owned.Active = false; frame.Value = previous; lock (gate) inFlight = null; }
    }
    private async Task<McpOAuthDiscoveredServer> Discover(JsonData? cached, Uri? resourceMetadataUrl, CancellationToken token)
    {
        if (cached is null || cached.Value.ValueKind != JsonValueKind.Object || !cached.Value.TryGetProperty("authorizationServerUrl", out var issuerValue))
            return await McpAdmittedOAuthDiscovery.DiscoverAsync(server, exchange, resourceMetadataUrl, token, maximumBytes).ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(cached.ToString()) > maximumBytes) throw new McpOAuthProtocolException("metadata_bound", "Cached OAuth discovery exceeds its admitted bound.");
        var issuer = issuerValue.ValueKind == JsonValueKind.String ? issuerValue.GetString()! : throw new McpOAuthProtocolException("metadata_invalid", "Cached discovery issuer must be a string.");
        _ = McpAdmittedOAuthDiscovery.Url(issuer);
        var authorization = cached.Value.TryGetProperty("authorizationServerMetadata", out var metadata) ? McpAdmittedOAuthDiscovery.ParseAuthorization(metadata.GetRawText())
            : await McpAdmittedOAuthDiscovery.DiscoverAuthorizationServerAsync(issuer, exchange, token, maximumBytes).ConfigureAwait(false);
        if (authorization is not null && McpAdmittedOAuthDiscovery.Text(authorization.Value, "issuer", true)!.TrimEnd('/') != issuer.TrimEnd('/'))
            throw new McpOAuthProtocolException("issuer_mismatch", "Cached authorization issuer does not match admitted discovery issuer.");
        var resource = cached.Value.TryGetProperty("resourceMetadata", out var value) ? McpAdmittedOAuthDiscovery.ParseResource(value.GetRawText()) : null;
        return new(issuer, authorization, resource);
    }
    private (string? Error, Uri? MetadataUrl) Challenge(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = response.Headers.TryGetValues("WWW-Authenticate", out var values) ? string.Join(",", values) : "";
        if (Encoding.UTF8.GetByteCount(text) > maximumBytes) throw new McpOAuthProtocolException("challenge_bound", "OAuth challenge exceeds admitted bound.");
        var scheme = text.TrimStart().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        if (scheme is not ("bearer" or "dpop")) return (null, null);
        string? Field(string name)
        {
            var result = Regex.Match(text, "(?:^|[,\\s])" + name + "=(?:\"([^\"]*)\"|([^\\s,]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return result.Success ? result.Groups[1].Success ? result.Groups[1].Value : result.Groups[2].Value : null;
        }
        var requested = Field("resource_metadata"); Uri? metadataUrl = null;
        if (requested is not null && Uri.TryCreate(requested, UriKind.Absolute, out var parsed)) metadataUrl = McpAdmittedOAuthDiscovery.Url(parsed.AbsoluteUri);
        return (Field("error"), metadataUrl);
    }
    private static McpOAuthAdmittedClient? StoredClient(JsonData? value)
    {
        if (value is null) return null;
        var parsed = McpAdmittedOAuthDiscovery.Json(value.ToString());
        return new(McpAdmittedOAuthDiscovery.Text(parsed.Value, "client_id", true)!, McpAdmittedOAuthDiscovery.Text(parsed.Value, "client_secret", false), McpAdmittedOAuthDiscovery.Text(parsed.Value, "token_endpoint_auth_method", false));
    }
    private void ValidateClient(McpOAuthAdmittedClient client)
    {
        if (string.IsNullOrEmpty(client.ClientId) || Encoding.UTF8.GetByteCount(client.ClientId) + (long)Encoding.UTF8.GetByteCount(client.ClientSecret ?? "") > maximumBytes)
            throw new ArgumentException("Finite explicitly admitted OAuth client information required.");
    }
    private static string SelectAuthentication(McpOAuthAdmittedClient client, string[] supported)
    {
        var hinted = client.AuthenticationMethod;
        if ((hinted is "client_secret_basic" or "client_secret_post" or "none") && (supported.Length == 0 || supported.Contains(hinted))) return hinted;
        if (supported.Length == 0) return !string.IsNullOrEmpty(client.ClientSecret) ? "client_secret_basic" : "none";
        if (!string.IsNullOrEmpty(client.ClientSecret) && supported.Contains("client_secret_basic")) return "client_secret_basic";
        if (!string.IsNullOrEmpty(client.ClientSecret) && supported.Contains("client_secret_post")) return "client_secret_post";
        return supported.Contains("none") ? "none" : !string.IsNullOrEmpty(client.ClientSecret) ? "client_secret_post" : "none";
    }
    private static string Form(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal).Replace("~", "%7E", StringComparison.Ordinal).Replace("%2A", "*", StringComparison.Ordinal);
    private static double Number(JsonElement value, bool arrayElement)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() == 0) return 0;
            return value.GetArrayLength() == 1 ? Number(value[0], arrayElement: true) : double.NaN;
        }
        if (value.ValueKind == JsonValueKind.Null) return 0;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return arrayElement ? double.NaN : value.ValueKind == JsonValueKind.True ? 1 : 0;
        if (value.ValueKind != JsonValueKind.String) return double.NaN;
        var text = value.GetString()!; var start = 0; var end = text.Length;
        while (start < end && JsWhite(text[start])) start++;
        while (end > start && JsWhite(text[end - 1])) end--;
        text = text[start..end]; if (text.Length == 0) return 0;
        if (text.Length > 2 && text[0] == '0' && (char.ToLowerInvariant(text[1]) is 'x' or 'b' or 'o'))
        {
            var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'b' => 2, _ => 8 }; double result = 0;
            for (var index = 2; index < text.Length; index++)
            {
                var digit = text[index] is >= '0' and <= '9' ? text[index] - '0' : char.ToLowerInvariant(text[index]) is >= 'a' and <= 'f' ? char.ToLowerInvariant(text[index]) - 'a' + 10 : -1;
                if (digit < 0 || digit >= radix) return double.NaN; result = result * radix + digit;
            }
            return result;
        }
        const NumberStyles style = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;
        return double.TryParse(text, style, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
    }
    private static bool JsWhite(char value) => value is '\u0009' or '\u000B' or '\u000C' or '\u0020' or '\u00A0' or '\uFEFF' or '\u000A' or '\u000D' or '\u2028' or '\u2029' or '\u1680' or '\u202F' or '\u205F' or '\u3000' or >= '\u2000' and <= '\u200A';
    private static McpOAuthTokens ParseTokens(McpOAuthExchangeResponse response, string refresh)
    {
        JsonData? parsed = null; Exception? parsing = null;
        try { parsed = JsonData.Parse(response.Body); } catch (JsonException parseError) { parsing = parseError; }
        if (parsed?.Value.ValueKind == JsonValueKind.Object && parsed.Value.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            var description = parsed.Value.TryGetProperty("error_description", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : error.GetString()!;
            var errorUri = parsed.Value.TryGetProperty("error_uri", out var uri) && uri.ValueKind == JsonValueKind.String ? uri.GetString() : null;
            throw new McpOAuthProtocolException(error.GetString()!, description, response.Status, errorUri);
        }
        if (response.Status is < 200 or >= 300) throw new McpOAuthProtocolException("server_error", "OAuth token endpoint HTTP failure.", response.Status, original: parsing);
        if (parsed is null || parsed.Value.ValueKind != JsonValueKind.Object) throw new McpOAuthProtocolException("token_invalid", "OAuth token endpoint must return a JSON object.", original: parsing);
        var value = parsed.Value; double? expires = null;
        // `Number(null)` is 0, which would mark the token as expired at once, so `null` and `""` are absent.
        if (value.TryGetProperty("expires_in", out var expiry) &&
            !(expiry.ValueKind == JsonValueKind.Null || expiry.ValueKind == JsonValueKind.String && expiry.GetString()!.Length == 0))
        {
            expires = Number(expiry, arrayElement: false);
            if (!double.IsFinite(expires.Value)) throw new McpOAuthProtocolException("token_invalid", "OAuth expires_in must coerce to a finite number.");
        }
        return new(McpAdmittedOAuthDiscovery.Text(value, "access_token", true)!, McpAdmittedOAuthDiscovery.Text(value, "token_type", true)!, expires,
            McpAdmittedOAuthDiscovery.Text(value, "scope", false), McpAdmittedOAuthDiscovery.Text(value, "refresh_token", false) ?? refresh,
            McpAdmittedOAuthDiscovery.Text(value, "id_token", false));
    }
}
