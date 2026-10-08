using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>Pi v0.99.1 bounded metadata candidate selection. Every proposed URI is passed only
/// to an explicitly admitted exchange; this class supplies no HTTP client or endpoint authority.</summary>
public static class McpAdmittedOAuthDiscovery
{
    public static async Task<McpOAuthDiscoveredServer> DiscoverAsync(Uri server, McpAdmittedOAuthExchange exchange,
        Uri? resourceMetadataUrl = null, CancellationToken token = default, int maximumBytes = 1_048_576)
    {
        ValidateAdmission(server, exchange, maximumBytes); token.ThrowIfCancellationRequested();
        JsonData? resource = null;
        try
        {
            var response = await Fetch(resourceMetadataUrl ?? AtOrigin(server, "/.well-known/oauth-protected-resource" + Suffix(server.AbsolutePath)),
                McpOAuthExchangePurpose.ProtectedResourceMetadata, exchange, token, maximumBytes).ConfigureAwait(false);
            if (resourceMetadataUrl is null && server.AbsolutePath != "/" && Miss(response.Status))
                response = await Fetch(AtOrigin(server, "/.well-known/oauth-protected-resource"), McpOAuthExchangePurpose.ProtectedResourceMetadata, exchange, token, maximumBytes).ConfigureAwait(false);
            if (!Success(response.Status)) throw new McpOAuthProtocolException("metadata_http", "Protected resource metadata HTTP failure.", response.Status);
            try { resource = ParseResource(response.Body); }
            catch (McpOAuthProtocolException error) when (error.Code == "metadata_url")
            { /* Only parsed metadata URL failure is a tolerant protected-resource miss. */ }
        }
        catch (McpOAuthProtocolException error) when (error.Code is "metadata_http" or "metadata_invalid")
        { /* Original server-info discovery tolerates missing/malformed protected-resource metadata. */ }
        var authorization = resource is { } value && Strings(value.Value, "authorization_servers", false) is { Length: > 0 } servers
            ? servers[0] : AtOrigin(server, "/").AbsoluteUri;
        var metadata = await DiscoverAuthorizationServerAsync(authorization, exchange, token, maximumBytes).ConfigureAwait(false);
        return new(authorization, metadata, resource);
    }
    public static async Task<JsonData?> DiscoverAuthorizationServerAsync(string issuer, McpAdmittedOAuthExchange exchange,
        CancellationToken token = default, int maximumBytes = 1_048_576)
    {
        var endpoint = Url(issuer); ValidateAdmission(endpoint, exchange, maximumBytes);
        var suffix = Suffix(endpoint.AbsolutePath);
        var candidates = new List<Uri> { AtOrigin(endpoint, "/.well-known/oauth-authorization-server" + suffix), AtOrigin(endpoint, "/.well-known/openid-configuration" + suffix) };
        if (suffix.Length > 0) candidates.Add(AtOrigin(endpoint, suffix + "/.well-known/openid-configuration"));
        foreach (var candidate in candidates)
        {
            token.ThrowIfCancellationRequested();
            var response = await Fetch(candidate, McpOAuthExchangePurpose.AuthorizationServerMetadata, exchange, token, maximumBytes).ConfigureAwait(false);
            if (!Success(response.Status))
            { if (Miss(response.Status)) continue; throw new McpOAuthProtocolException("metadata_http", "Authorization metadata HTTP failure.", response.Status); }
            var metadata = ParseAuthorization(response.Body);
            if (!string.Equals(TrimSlash(Text(metadata.Value, "issuer", true)!), TrimSlash(issuer), StringComparison.Ordinal))
                throw new McpOAuthProtocolException("issuer_mismatch", "Authorization server metadata issuer does not match its admitted discovery issuer.");
            return metadata;
        }
        return null;
    }
    public static string? SelectResource(Uri server, JsonData? metadata)
    {
        if (metadata is null) return null;
        var text = Text(metadata.Value, "resource", true)!; var resource = Url(text);
        if (!string.Equals(server.Scheme, resource.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(server.IdnHost, resource.IdnHost, StringComparison.OrdinalIgnoreCase) || server.Port != resource.Port ||
            !WithSlash(server.AbsolutePath).StartsWith(WithSlash(resource.AbsolutePath), StringComparison.Ordinal))
            throw new McpOAuthProtocolException("resource_mismatch", "Protected resource metadata does not cover the admitted MCP server.");
        return text;
    }
    internal static async Task<McpOAuthExchangeResponse> Fetch(Uri endpoint, McpOAuthExchangePurpose purpose,
        McpAdmittedOAuthExchange exchange, CancellationToken token, int maximumBytes)
    {
        var headers = ImmutableDictionary<string, string>.Empty.Add("Accept", "application/json").Add("MCP-Protocol-Version", "2025-11-25");
        return await Exchange(new(endpoint, HttpMethod.Get, headers, null, purpose), exchange, token, maximumBytes).ConfigureAwait(false);
    }
    internal static async Task<McpOAuthExchangeResponse> Exchange(McpOAuthExchangeRequest request,
        McpAdmittedOAuthExchange exchange, CancellationToken token, int maximumBytes)
    {
        token.ThrowIfCancellationRequested();
        long requestBytes = Encoding.UTF8.GetByteCount(request.Endpoint.AbsoluteUri) + Encoding.UTF8.GetByteCount(request.Body ?? "");
        foreach (var header in request.Headers) requestBytes += Encoding.UTF8.GetByteCount(header.Key) + (long)Encoding.UTF8.GetByteCount(header.Value);
        if (requestBytes > maximumBytes) throw new McpOAuthProtocolException("request_bound", "Proposed OAuth exchange exceeds its admitted finite bound.");
        var response = await McpOAuthAdmittedWork.Invoke("http-exchange", () => exchange(request, token), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (response is null || response.Status is < 100 or > 599 || response.Body is null)
            throw new McpOAuthProtocolException("invalid_exchange", "Admitted HTTP exchange returned an invalid response envelope.");
        if (Encoding.UTF8.GetByteCount(response.Body) > maximumBytes)
            throw new McpOAuthProtocolException("metadata_bound", "Admitted OAuth response exceeds its finite byte bound.");
        return response;
    }
    internal static JsonData ParseResource(string body)
    {
        var value = Json(body); _ = Url(Text(value.Value, "resource", true)!);
        foreach (var server in Strings(value.Value, "authorization_servers", false) ?? []) _ = Url(server);
        _ = Strings(value.Value, "scopes_supported", false); return value;
    }
    internal static JsonData ParseAuthorization(string body)
    {
        var value = Json(body);
        foreach (var key in new[] { "issuer", "authorization_endpoint", "token_endpoint" }) _ = Url(Text(value.Value, key, true)!);
        _ = Strings(value.Value, "response_types_supported", true);
        foreach (var key in new[] { "scopes_supported", "grant_types_supported", "token_endpoint_auth_methods_supported", "code_challenge_methods_supported" }) _ = Strings(value.Value, key, false);
        if (Text(value.Value, "registration_endpoint", false) is { } registration) _ = Url(registration);
        return value;
    }
    internal static JsonData Json(string text)
    {
        try { var value = JsonData.Parse(text); if (value.Value.ValueKind != JsonValueKind.Object) throw new JsonException(); return value; }
        catch (JsonException error) { throw new McpOAuthProtocolException("metadata_invalid", "OAuth JSON object metadata is invalid: " + error.GetType().Name, original: error); }
    }
    internal static string? Text(JsonElement value, string name, bool required)
    {
        if (!value.TryGetProperty(name, out var field))
        { if (!required) return null; throw new McpOAuthProtocolException("metadata_invalid", "Missing OAuth metadata string: " + name); }
        if (field.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(field.GetString()))
            throw new McpOAuthProtocolException("metadata_invalid", "Invalid OAuth metadata string: " + name);
        return field.GetString();
    }
    internal static string[]? Strings(JsonElement value, string name, bool required)
    {
        if (!value.TryGetProperty(name, out var field))
        { if (!required) return null; throw new McpOAuthProtocolException("metadata_invalid", "Missing OAuth metadata array: " + name); }
        if (field.ValueKind != JsonValueKind.Array) throw new McpOAuthProtocolException("metadata_invalid", "Invalid OAuth metadata array: " + name);
        var result = new List<string>();
        foreach (var item in field.EnumerateArray())
        { if (item.ValueKind != JsonValueKind.String) throw new McpOAuthProtocolException("metadata_invalid", "Invalid OAuth metadata array item: " + name); result.Add(item.GetString()!); }
        return result.ToArray();
    }
    internal static Uri Url(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var value) || value.Scheme is not ("http" or "https"))
            throw new McpOAuthProtocolException("metadata_url", "An explicitly admitted HTTP metadata URL is required.");
        return value;
    }
    internal static Uri AtOrigin(Uri uri, string path)
    { var origin = new UriBuilder(uri.Scheme, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port) { Path = path, Query = "", Fragment = "" }; return origin.Uri; }
    internal static void ValidateAdmission(Uri endpoint, McpAdmittedOAuthExchange exchange, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(endpoint); ArgumentNullException.ThrowIfNull(exchange);
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") || maximumBytes < 1 || exchange.GetInvocationList().Length != 1)
            throw new ArgumentException("Finite caller-admitted HTTP OAuth exchange required.");
    }
    private static bool Success(int status) => status is >= 200 and < 300;
    private static bool Miss(int status) => status is >= 400 and < 500 or 502;
    private static string Suffix(string path) => path.EndsWith('/') ? path[..^1] : path;
    private static string TrimSlash(string text) => text.EndsWith('/') ? text[..^1] : text;
    private static string WithSlash(string path) => path.EndsWith('/') ? path : path + "/";
}
