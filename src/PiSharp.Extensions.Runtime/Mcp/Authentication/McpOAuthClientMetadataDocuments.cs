// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/extensions/mcp/oauth.ts callbackId, clientMetadataDocument and
// createProvider's cimd failure.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>Client ID Metadata Documents, chosen like Codex chooses its own, for a sign-in that offers one. PiSharp's sign-in offers
/// them only for an explicit `oauth.clientRegistration: "cimd"` (its `auto` always registers; see McpSignIn). The caller supplies
/// the https base where the application serves them; nothing is fetched or published here.</summary>
public static class McpOAuthClientMetadataDocuments
{
    /// <summary>The loopback callback path the configuration requires for `cimd`.</summary>
    public const string CallbackPath = "/callback";

    /// <summary>12 characters identifying an MCP server URL in callback paths, computed like Codex does.</summary>
    public static string CallbackId(Uri serverUrl)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        if (!serverUrl.IsAbsoluteUri) throw new ArgumentException("Absolute server URL required.", nameof(serverUrl));
        var href = serverUrl.GetLeftPart(UriPartial.Query);
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(href))[..9]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The document to identify as. Without the `iss` parameter in authorization responses (RFC 9207), the
    /// redirect URI and the document are specific to the MCP server, so a response cannot be mixed up with one from
    /// another authorization server (RFC 9700 section 4.4.2.2). Upstream's clientMetadataDocument returns undefined without server
    /// support and createProvider fails only for `cimd`; only `cimd` reaches this in PiSharp, so the failure is raised here.</summary>
    public static McpOAuthClientMetadataDocument Create(Uri documentBase, Uri serverUrl, string redirectUrl, JsonData? metadata)
        => TryCreate(documentBase, serverUrl, redirectUrl, metadata) ?? throw new McpOAuthProtocolException("client_metadata_unsupported",
            "The authorization server does not support Client ID Metadata Documents for public clients; remove oauth.clientRegistration \"cimd\"");

    public static string ServerSpecificPath(string path, Uri serverUrl) => path.TrimEnd('/') + "/" + CallbackId(serverUrl);

    /// <summary>Returns null when the server does not support documents; the caller decides whether DCR is allowed.</summary>
    public static McpOAuthClientMetadataDocument? TryCreate(Uri documentBase, Uri serverUrl, string redirectUrl, JsonData? metadata)
    {
        ArgumentNullException.ThrowIfNull(documentBase); ArgumentNullException.ThrowIfNull(redirectUrl);
        if (!documentBase.IsAbsoluteUri || documentBase.Scheme != "https") throw new ArgumentException("An https document base is required.", nameof(documentBase));
        var value = metadata?.Value ?? default;
        bool Flag(string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
        var publicClients = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("token_endpoint_auth_methods_supported", out var methods) &&
            methods.ValueKind == JsonValueKind.Array && methods.EnumerateArray().Any(method => method.ValueKind == JsonValueKind.String && method.GetString() == "none");
        if (!Flag("client_id_metadata_document_supported") || !publicClients) return null;
        var root = documentBase.AbsoluteUri.TrimEnd('/');
        if (Flag("authorization_response_iss_parameter_supported")) return new($"{root}/client.json", redirectUrl);
        var id = CallbackId(serverUrl);
        var redirect = new UriBuilder(redirectUrl) { Path = ServerSpecificPath(new Uri(redirectUrl).AbsolutePath, serverUrl) };
        return new($"{root}/{id}/client.json", redirect.Uri.AbsoluteUri);
    }
}
