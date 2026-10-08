// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/oauth.ts callbackId/clientMetadataDocument.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;

namespace PiSharp.Extensions.Runtime.Mcp.Authentication;

/// <summary>Client ID Metadata Documents for `oauth.clientRegistration: "cimd"`, chosen like Codex chooses its own.
/// The caller supplies the https base where the application serves them; nothing is fetched or published here.</summary>
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
    /// another authorization server (RFC 9700 section 4.4.2.2).</summary>
    public static McpOAuthClientMetadataDocument Create(Uri documentBase, Uri serverUrl, string redirectUrl, JsonData? metadata)
    {
        ArgumentNullException.ThrowIfNull(documentBase); ArgumentNullException.ThrowIfNull(redirectUrl);
        if (!documentBase.IsAbsoluteUri || documentBase.Scheme != "https") throw new ArgumentException("An https document base is required.", nameof(documentBase));
        var value = metadata?.Value ?? default;
        bool Flag(string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var flag) && flag.ValueKind == JsonValueKind.True;
        var publicClients = value.ValueKind == JsonValueKind.Object && value.TryGetProperty("token_endpoint_auth_methods_supported", out var methods) &&
            methods.ValueKind == JsonValueKind.Array && methods.EnumerateArray().Any(method => method.ValueKind == JsonValueKind.String && method.GetString() == "none");
        if (!Flag("client_id_metadata_document_supported") || !publicClients)
            throw new McpOAuthProtocolException("client_metadata_unsupported",
                "The authorization server does not support Client ID Metadata Documents for public clients; remove oauth.clientRegistration \"cimd\"");
        var root = documentBase.AbsoluteUri.TrimEnd('/');
        if (Flag("authorization_response_iss_parameter_supported")) return new($"{root}/client.json", redirectUrl);
        var id = CallbackId(serverUrl);
        var redirect = new UriBuilder(redirectUrl) { Path = $"{CallbackPath}/{id}" };
        return new($"{root}/{id}/client.json", redirect.Uri.AbsoluteUri);
    }
}
