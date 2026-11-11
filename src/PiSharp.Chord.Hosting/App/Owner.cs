// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/owner.ts.
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Http;

namespace PiSharp.Chord.Hosting.App;

/// <summary>A process-local, single-use owner sign-in link. Its token is carried only in a URL fragment.</summary>
public sealed class OwnerLink
{
    public const string Path = "/_chord/owner";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly object gate = new();
    private readonly TimeProvider time;
    private string? hash;
    private DateTimeOffset expires;
    public Facet? Facet { get; }

    public OwnerLink(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    public OwnerLink(JsonObject owner, Sessions sessions, TimeProvider? time = null) : this(time)
    {
        var principal = AppPrincipals.Require(owner);
        Facet = PiSharp.Chord.Host.Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, new HttpContribution
        {
            Routes = new Dictionary<string, HttpRoute>
            {
                [$"GET {Path}"] = new((request, _) => Task.FromResult<HttpResult>(new HttpResult.Html(FragmentPage(Path, request.Nonce))), Page: true, Public: true),
                [$"POST {Path}"] = new(async (request, context) =>
                {
                    var body = await request.JsonAsync(4096, context.AbortSignal).ConfigureAwait(false);
                    if (!AuthHttp.TryString(body, "token", out var token) || !Consume(token)) return AuthHttp.Error(403);
                    var issued = await sessions.IssueAsync(principal, "owner browser", SessionKind.Browser, request.Provenance.Kind != "local", context.Context).ConfigureAwait(false);
                    return AuthHttp.SignedIn(issued.Cookie);
                }, Public: true),
            },
        }));
    }

    /// <summary>Replaces the current link, so all earlier links immediately stop authenticating.</summary>
    public string Renew()
    {
        var token = Secrets.NewToken();
        lock (gate) { hash = Secrets.HashToken(token); expires = time.GetUtcNow() + Lifetime; }
        return token;
    }

    public bool Consume(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256) return false;
        lock (gate)
        {
            if (hash is null || !Secrets.MatchesHash(token, hash)) return false;
            hash = null;
            return time.GetUtcNow() < expires;
        }
    }

    /// <summary>Creates a fresh owner URL. A wildcard listener is displayed through loopback.</summary>
    public string Link(Uri listening)
    {
        ArgumentNullException.ThrowIfNull(listening);
        var address = new UriBuilder(listening) { Path = Path, Query = "", Fragment = Renew() };
        if (address.Host is "0.0.0.0" or "*" or "+") address.Host = "127.0.0.1";
        else if (address.Host is "::" or "[::]") address.Host = "::1";
        return address.Uri.AbsoluteUri;
    }

    /// <summary>Reuses only a valid session of this owner; replacing the owner revokes the previous file session.</summary>
    public static async Task<string> EnsureTokenFileAsync(string path, JsonObject owner, Sessions sessions, ChordContext context)
    {
        var existing = await Secrets.ReadSecretAsync(path).ConfigureAwait(false);
        if (existing is not null)
        {
            var principal = await sessions.VerifyAsync(existing, context).ConfigureAwait(false);
            if (AppPrincipals.Same(principal, owner)) { Layout.PrivateFile(path); return existing; }
            if (principal is not null)
            {
                var dot = existing.IndexOf('.');
                if (dot > 0) await sessions.RevokeAsync(existing[..dot], context).ConfigureAwait(false);
            }
        }
        var issued = await sessions.IssueAsync(owner, "owner token file", SessionKind.Device, false, context).ConfigureAwait(false);
        try
        {
            if (!await Secrets.WriteSecretAsync(path, issued.Token, replace: existing is not null).ConfigureAwait(false))
            {
                await sessions.RevokeAsync(issued.Id, ChordContext.Background).ConfigureAwait(false);
                var winner = await Secrets.ReadSecretAsync(path).ConfigureAwait(false);
                if (winner is not null && AppPrincipals.Same(await sessions.VerifyAsync(winner, context).ConfigureAwait(false), owner)) return winner;
                throw new IOException("The owner token file changed during creation.");
            }
        }
        catch
        {
            await sessions.RevokeAsync(issued.Id, ChordContext.Background).ConfigureAwait(false);
            throw;
        }
        return issued.Token;
    }

    public static string FragmentPage(string path, string nonce)
    {
        // The fragment is cleared before any request, and is never interpolated into HTML or a request URL.
        var endpoint = JsonSerializer.Serialize(path);
        return "<!doctype html><html><head><meta name=referrer content=no-referrer><title>Sign in</title></head>" +
            "<body><p id=status>Signing in…</p><script nonce=\"" + WebUtility.HtmlEncode(nonce) + "\">\n" +
            "const token = location.hash.slice(1); history.replaceState(null, '', location.pathname);\n" +
            "fetch(" + endpoint + ", { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ token }) })\n" +
            ".then(r => { if (r.ok) location.replace('/'); else document.getElementById('status').textContent = 'This link is used up or expired.'; })\n" +
            ".catch(() => { document.getElementById('status').textContent = 'Unable to sign in.'; });\n</script></body></html>";
    }
}

internal static class AuthHttp
{
    internal static HttpResult Error(int status) => new HttpResult.Empty { Status = status };
    internal static bool TryString(JsonNode? body, string key, out string text)
    {
        text = "";
        return body is JsonObject json && json[key] is JsonValue value && value.TryGetValue(out text!);
    }
    internal static HttpResult SignedIn(string cookie) => new HttpResult.Json(JsonValue.Create("signed in"))
    { Headers = new Dictionary<string, string[]> { ["Set-Cookie"] = [cookie], ["Cache-Control"] = ["no-store"] } };
}
