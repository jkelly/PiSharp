// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/mcp/src/oauth/flow.ts registerClient/token requests and packages/mcp/src/oauth/types.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Cli.Mcp.Authentication;

public sealed class McpDefaultOAuthRegistrationFailure(int status, string responseText, Exception? parsing = null)
    : IOException("Admitted OAuth dynamic registration HTTP failure.", parsing)
{
    public int Status { get; } = status;
    public string ResponseText { get; } = responseText;
}

internal sealed class McpDefaultOAuthProtocol(McpDefaultOAuthHost host, McpDefaultOAuthHostResources resources, McpDefaultOAuthHttp http)
{
    private static ImmutableDictionary<string, string> Headers(string content) =>
        ImmutableDictionary<string, string>.Empty.Add("Accept", "application/json").Add("Content-Type", content);
    internal async Task<(McpOAuthAdmittedClient Client, JsonData Raw)> RegisterAsync(McpOAuthOrchestrationOptions options,
        McpOAuthDiscoveredServer discovery, string? scope, CancellationToken token)
    {
        var declared = discovery.AuthorizationServerMetadata;
        var registration = declared is null ? null : Text(declared.Value, "registration_endpoint", false);
        if (declared is not null && registration is null)
            throw new McpOAuthProtocolException("registration_unsupported", "Authorization server does not support dynamic registration.");
        var endpoint = registration is null ? new Uri(new Uri(discovery.AuthorizationServerUrl), "/register") : Url(registration);
        var metadata = options.ClientMetadata.Value;
        if (metadata.ValueKind != JsonValueKind.Object) throw new ArgumentException("OAuth client metadata must be an object.");
        var fields = metadata.EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value.Clone(), StringComparer.Ordinal);
        // OpenID Connect servers assume `web` without `application_type`, which rejects http loopback redirect URIs
        // (MCP SEP-837). Loopback hosts and custom schemes are native (RFC 8252). Set before `scope`, like the original.
        if (!fields.TryGetValue("application_type", out var declaredType) || declaredType.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            var proposed = fields.TryGetValue("redirect_uris", out var uris) && uris.ValueKind == JsonValueKind.Array ? uris.EnumerateArray()
                .Where(uri => uri.ValueKind == JsonValueKind.String).Select(uri => uri.GetString()!) : [];
            fields["application_type"] = JsonSerializer.SerializeToElement(ApplicationType(proposed));
        }
        if (!string.IsNullOrEmpty(scope)) fields["scope"] = JsonSerializer.SerializeToElement(scope);
        var response = await http.SendAsync(endpoint, McpDefaultOAuthHttpPurpose.Registration, HttpMethod.Post,
            Headers("application/json"), JsonSerializer.Serialize(fields), token).ConfigureAwait(false);
        if (response.Status is < 200 or >= 300) throw new McpDefaultOAuthRegistrationFailure(response.Status, response.Body);
        var raw = Object(response.Body); var value = raw.Value;
        var client = Client(value); var redirects = Strings(value, "redirect_uris", false);
        // Original parseClientInformation preserves unknown fields, normalizes missing redirects,
        // and drops non-number timestamp fields before the sole SaveClient callback.
        var normalized = value.EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value.Clone(), StringComparer.Ordinal);
        foreach (var name in new[] { "client_id_issued_at", "client_secret_expires_at" })
            if (value.TryGetProperty(name, out var timestamp) && timestamp.ValueKind != JsonValueKind.Number) normalized.Remove(name);
        normalized["redirect_uris"] = JsonSerializer.SerializeToElement(redirects);
        return (client, JsonData.FromElement(JsonSerializer.SerializeToElement(normalized)));
    }
    internal Task<McpOAuthTokens> ExchangeCodeAsync(McpOAuthOrchestrationContext context, string code, string verifier, CancellationToken token) =>
        TokenAsync(context, [new("grant_type", "authorization_code"), new("code", code), new("code_verifier", verifier),
            new("redirect_uri", context.RedirectUrl ?? resources.RedirectUri.AbsoluteUri)], McpDefaultOAuthHttpPurpose.AuthorizationCode, null, token);
    internal Task<McpOAuthTokens> RefreshAsync(McpOAuthOrchestrationContext context, string refresh, CancellationToken token) =>
        TokenAsync(context, [new("grant_type", "refresh_token"), new("refresh_token", refresh)], McpDefaultOAuthHttpPurpose.Refresh, refresh, token);
    private async Task<McpOAuthTokens> TokenAsync(McpOAuthOrchestrationContext context,
        List<KeyValuePair<string, string>> fields, McpDefaultOAuthHttpPurpose purpose, string? refresh, CancellationToken token)
    {
        var metadata = context.Discovery.AuthorizationServerMetadata;
        var endpoint = metadata is null ? new Uri(new Uri(context.Discovery.AuthorizationServerUrl), "/token") : Url(Text(metadata.Value, "token_endpoint", true)!);
        if (endpoint.Scheme != "https" && endpoint.IdnHost is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"))
        {
            var refusal = new McpOAuthProtocolException("insecure_endpoint", "OAuth token endpoint must be HTTPS or exact loopback.");
            throw new McpOAuthTokenOperationFailure(McpOAuthTokenFailureCategory.InsecureEndpoint, null, refusal);
        }
        if (!string.IsNullOrEmpty(context.Resource)) fields.Add(new("resource", context.Resource));
        var proposal = new McpDefaultOAuthClientAuthentication(endpoint, context.Client, metadata, fields, resources.MaximumBytes);
        ImmutableDictionary<string, string> headers;
        try
        {
            if (resources.AddClientAuthentication is { } customize)
                await host.ObserveAsync("client-authentication", () => customize(proposal, token), token).ConfigureAwait(false);
            else
            {
                var client = context.Client; var supported = metadata is null ? [] : Strings(metadata.Value, "token_endpoint_auth_methods_supported", false);
                var method = SelectMethod(client, supported);
                if (method == "client_secret_basic")
                {
                    if (string.IsNullOrEmpty(client.ClientSecret)) throw new McpOAuthProtocolException("client_authentication", "Basic authentication requires an admitted secret.");
                    proposal.HeaderSet("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(client.ClientId + ":" + client.ClientSecret)));
                }
                else
                {
                    proposal.FormSet("client_id", client.ClientId);
                    if (method == "client_secret_post" && !string.IsNullOrEmpty(client.ClientSecret)) proposal.FormSet("client_secret", client.ClientSecret);
                }
            }
            var snapshot = proposal.Snapshot(); headers = snapshot.Item1; fields = snapshot.Item2.ToList();
        }
        finally { proposal.Retire(); }
        var response = await http.SendAsync(endpoint, purpose, HttpMethod.Post, headers, Form(fields), token).ConfigureAwait(false);
        JsonData? parsed = null; Exception? parsing = null;
        try { parsed = JsonData.Parse(response.Body); } catch (JsonException error) { parsing = error; }
        if (parsed?.Value.ValueKind == JsonValueKind.Object && parsed.Value.TryGetProperty("error", out var errorCode) && errorCode.ValueKind == JsonValueKind.String)
        {
            var code = errorCode.GetString()!;
            var description = parsed.Value.TryGetProperty("error_description", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : code;
            var uri = parsed.Value.TryGetProperty("error_uri", out var errorUri) && errorUri.ValueKind == JsonValueKind.String ? errorUri.GetString() : null;
            throw new McpOAuthTokenOperationFailure(McpOAuthTokenFailureCategory.WireOAuthError, code,
                new McpOAuthProtocolException(code, description, response.Status, uri));
        }
        if (response.Status is < 200 or >= 300)
            throw new McpOAuthTokenOperationFailure(McpOAuthTokenFailureCategory.WireOAuthError, "server_error",
                new McpOAuthProtocolException("server_error", "OAuth token endpoint HTTP failure.", response.Status, original: parsing));
        if (parsed is null || parsed.Value.ValueKind != JsonValueKind.Object)
            throw new McpOAuthProtocolException("token_invalid", "OAuth token response must be a JSON object.", original: parsing);
        var value = parsed.Value; double? expiry = null;
        // `Number(null)` is 0, which would mark the token as expired at once, so `null` and `""` are absent.
        if (value.TryGetProperty("expires_in", out var duration) &&
            !(duration.ValueKind == JsonValueKind.Null || duration.ValueKind == JsonValueKind.String && duration.GetString()!.Length == 0))
        { expiry = Number(duration, false); if (!double.IsFinite(expiry.Value)) throw new McpOAuthProtocolException("token_invalid", "OAuth expiry must coerce to a finite number."); }
        return new(Text(value, "access_token", true)!, Text(value, "token_type", true)!, expiry,
            Text(value, "scope", false), Text(value, "refresh_token", false) ?? refresh, Text(value, "id_token", false));
    }
    internal async Task<McpOAuthAuthorizationProposal> BeginAsync(McpOAuthOrchestrationContext context, CancellationToken token)
    {
        var metadata = context.Discovery.AuthorizationServerMetadata;
        if (metadata is not null && !Strings(metadata.Value, "response_types_supported", true).Contains("code"))
            throw new McpOAuthProtocolException("authorization_unsupported", "Authorization server does not support codes.");
        if (metadata is not null && metadata.Value.TryGetProperty("code_challenge_methods_supported", out _) &&
            !Strings(metadata.Value, "code_challenge_methods_supported", false).Contains("S256"))
            throw new McpOAuthProtocolException("pkce_unsupported", "Authorization server does not support S256.");
        var bytes = await host.ObserveAsync("pkce-entropy", () => resources.Entropy32(token), token).ConfigureAwait(false);
        if (bytes is null || bytes.Length != 32) throw new ArgumentException("Exactly 32 explicitly admitted entropy bytes required.");
        var verifier = Base64Url(bytes); var challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        var endpoint = metadata is null ? new Uri(new Uri(context.Discovery.AuthorizationServerUrl), "/authorize") : Url(Text(metadata.Value, "authorization_endpoint", true)!);
        var query = new List<KeyValuePair<string, string>>
        {
            new("response_type", "code"), new("client_id", context.Client.ClientId), new("code_challenge", challenge),
            new("code_challenge_method", "S256"), new("redirect_uri", context.RedirectUrl ?? resources.RedirectUri.AbsoluteUri)
        };
        if (!string.IsNullOrEmpty(context.State)) query.Add(new("state", context.State));
        if (!string.IsNullOrEmpty(context.Scope))
        {
            query.Add(new("scope", context.Scope));
            if (context.Scope.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("offline_access")) query.Add(new("prompt", "consent"));
        }
        if (!string.IsNullOrEmpty(context.Resource)) query.Add(new("resource", context.Resource));
        var builder = new UriBuilder(endpoint);
        var existing = endpoint.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(pair =>
        { var parts = pair.Split('=', 2); return new KeyValuePair<string, string>(Uri.UnescapeDataString(parts[0].Replace('+', ' ')), parts.Length == 2 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : ""); }).ToList();
        foreach (var pair in query)
        { var first = existing.FindIndex(item => item.Key == pair.Key); existing.RemoveAll(item => item.Key == pair.Key); existing.Insert(first < 0 ? existing.Count : first, pair); }
        builder.Query = Form(existing);
        if (Encoding.UTF8.GetByteCount(builder.Uri.AbsoluteUri) > resources.MaximumBytes) throw new ArgumentException("Authorization proposal exceeds its finite admission.");
        return new(builder.Uri, verifier);
    }
    internal static McpOAuthAdmittedClient Client(JsonElement value) => new(Text(value, "client_id", true)!, Text(value, "client_secret", false),
        value.TryGetProperty("token_endpoint_auth_method", out var hint) && hint.ValueKind == JsonValueKind.String ? hint.GetString() : null);
    internal static JsonData Object(string text)
    { var data = JsonData.Parse(text); if (data.Value.ValueKind != JsonValueKind.Object) throw new McpOAuthProtocolException("metadata_invalid", "OAuth object required."); return data; }
    internal static string? Text(JsonElement value, string key, bool required)
    {
        if (!value.TryGetProperty(key, out var field)) return required ? throw new McpOAuthProtocolException("metadata_invalid", "Required OAuth string missing.") : null;
        // Servers send `null` and `""` for optional fields they have no value for, like `scope: ""`.
        if (!required && (field.ValueKind == JsonValueKind.Null || field.ValueKind == JsonValueKind.String && field.GetString()!.Length == 0)) return null;
        if (field.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(field.GetString())) throw new McpOAuthProtocolException("metadata_invalid", "OAuth string must be nonempty.");
        return field.GetString();
    }
    internal static string[] Strings(JsonElement value, string key, bool required)
    {
        if (!value.TryGetProperty(key, out var field)) return required ? throw new McpOAuthProtocolException("metadata_invalid", "Required OAuth array missing.") : [];
        if (field.ValueKind != JsonValueKind.Array || field.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)) throw new McpOAuthProtocolException("metadata_invalid", "OAuth string array required.");
        return field.EnumerateArray().Select(item => item.GetString()!).ToArray();
    }
    internal static Uri Url(string value)
    { if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new McpOAuthProtocolException("metadata_url", "Absolute OAuth HTTP URL required."); return uri; }
    private static string SelectMethod(McpOAuthAdmittedClient client, string[] supported)
    {
        if ((client.AuthenticationMethod is "client_secret_basic" or "client_secret_post" or "none") && (supported.Length == 0 || supported.Contains(client.AuthenticationMethod))) return client.AuthenticationMethod;
        if (supported.Length == 0) return string.IsNullOrEmpty(client.ClientSecret) ? "none" : "client_secret_basic";
        if (!string.IsNullOrEmpty(client.ClientSecret) && supported.Contains("client_secret_basic")) return "client_secret_basic";
        if (!string.IsNullOrEmpty(client.ClientSecret) && supported.Contains("client_secret_post")) return "client_secret_post";
        return supported.Contains("none") || string.IsNullOrEmpty(client.ClientSecret) ? "none" : "client_secret_post";
    }
    /// <summary>OpenID Connect `application_type` for `redirect_uris`: `native` for loopback hosts and custom schemes.</summary>
    internal static string ApplicationType(IEnumerable<string> redirectUris) => redirectUris.Any(uri =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && (parsed.Scheme is not ("http" or "https") ||
            parsed.IdnHost.ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or "[::1]")) ? "native" : "web";
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Form(IEnumerable<KeyValuePair<string, string>> fields) => string.Join('&', fields.Select(pair => Escape(pair.Key) + "=" + Escape(pair.Value)));
    private static string Escape(string value)
    {
        var result = new StringBuilder();
        foreach (var valueByte in Encoding.UTF8.GetBytes(value))
        {
            if (valueByte is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'*' or (byte)'-' or (byte)'.' or (byte)'_') result.Append((char)valueByte);
            else if (valueByte == (byte)' ') result.Append('+'); else result.Append('%').Append(valueByte.ToString("X2", CultureInfo.InvariantCulture));
        }
        return result.ToString();
    }
    // Original Number() coercion, matching the already reviewed native refresh/code leaves.
    private static double Number(JsonElement value, bool arrayElement)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.GetArrayLength() == 0 ? 0 : value.GetArrayLength() == 1 ? Number(value[0], true) : double.NaN;
        if (value.ValueKind == JsonValueKind.Null) return 0;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return arrayElement ? double.NaN : value.ValueKind == JsonValueKind.True ? 1 : 0;
        if (value.ValueKind != JsonValueKind.String) return double.NaN;
        var text = value.GetString()!; var start = 0; var end = text.Length;
        while (start < end && JsWhite(text[start])) start++; while (end > start && JsWhite(text[end - 1])) end--;
        text = text[start..end]; if (text.Length == 0) return 0;
        if (text.Length > 2 && text[0] == '0' && char.ToLowerInvariant(text[1]) is 'x' or 'b' or 'o')
        {
            var radix = char.ToLowerInvariant(text[1]) switch { 'x' => 16, 'b' => 2, _ => 8 }; double result = 0;
            for (var index = 2; index < text.Length; index++)
            { var digit = text[index] is >= '0' and <= '9' ? text[index] - '0' : char.ToLowerInvariant(text[index]) is >= 'a' and <= 'f' ? char.ToLowerInvariant(text[index]) - 'a' + 10 : -1; if (digit < 0 || digit >= radix) return double.NaN; result = result * radix + digit; }
            return result;
        }
        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var number) ? number : double.NaN;
    }
    private static bool JsWhite(char value) => value is '\u0009' or '\u000B' or '\u000C' or '\u0020' or '\u00A0' or '\uFEFF' or '\u000A' or '\u000D' or '\u2028' or '\u2029' or '\u1680' or '\u202F' or '\u205F' or '\u3000' or >= '\u2000' and <= '\u200A';
}
