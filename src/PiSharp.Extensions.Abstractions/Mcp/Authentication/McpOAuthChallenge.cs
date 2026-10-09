// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/oauth.ts (OAuthChallenge as
// createMcpAuthProvider's onChallenge receives it from parseWwwAuthenticate, and signInMcpServer's `challenge`).
using System.Text.RegularExpressions;

namespace PiSharp.Extensions.Mcp.Authentication;

/// <summary>The server's last `WWW-Authenticate` challenge: a sign-in uses its resource metadata URL and scope, and an
/// `insufficient_scope` error asks for more scope (a step-up).</summary>
public sealed record McpOAuthChallenge(string? Error = null, string? Scope = null, Uri? ResourceMetadataUrl = null)
{
    /// <summary>Whether the server asks for more scope than the current grant.</summary>
    public bool IsStepUp => Error == "insufficient_scope";

    /// <summary>A Bearer or DPoP challenge's `error`, `scope` and `resource_metadata`; an empty value counts as absent, and another
    /// scheme yields an empty challenge.</summary>
    public static McpOAuthChallenge Parse(string? header)
    {
        var text = header ?? "";
        var scheme = text.TrimStart().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.Equals(scheme, "Bearer", StringComparison.OrdinalIgnoreCase) && !string.Equals(scheme, "DPoP", StringComparison.OrdinalIgnoreCase))
            return new();
        string? Field(string name)
        {
            var match = Regex.Match(text, "(?:^|[,\\s])" + name + "=(?:\"([^\"]*)\"|([^\\s,]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
            var found = match.Success ? match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value : null;
            return string.IsNullOrEmpty(found) ? null : found;
        }
        var metadata = Field("resource_metadata") is { } requested && Uri.TryCreate(requested, UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "https" or "http" ? parsed : null;
        return new(Field("error"), Field("scope"), metadata);
    }
}
