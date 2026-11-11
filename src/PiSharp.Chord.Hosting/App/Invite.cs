// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/invite.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Http;

namespace PiSharp.Chord.Hosting.App;

/// <summary>An optional owner-managed invitation facet. Unused invitations are forgotten on restart.</summary>
public sealed class Invite
{
    public const string Path = "/_chord/invite";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private sealed record Pending(JsonObject Principal, string Label, DateTimeOffset Expires);
    private readonly object gate = new();
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly TimeProvider time;
    private readonly int maxPending;
    public Facet Facet { get; }

    public Invite(JsonObject owner, Sessions sessions, int maxPending = 1000, TimeProvider? time = null)
    {
        if (maxPending < 1) throw new ArgumentOutOfRangeException(nameof(maxPending));
        this.maxPending = maxPending; this.time = time ?? TimeProvider.System;
        var ownerPrincipal = AppPrincipals.Require(owner);
        Facet = PiSharp.Chord.Host.Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, new HttpContribution
        {
            Routes = new Dictionary<string, HttpRoute>
            {
                ["POST /_chord/invites"] = new(async (request, context) =>
                {
                    if (!AppPrincipals.Same(context.Principal, ownerPrincipal)) return AuthHttp.Error(403);
                    var body = await request.JsonAsync(16384, context.AbortSignal).ConfigureAwait(false);
                    if (body is not JsonObject json || json["principal"] is not JsonObject principal || !AuthHttp.TryString(body, "label", out var label)) return AuthHttp.Error(400);
                    JsonObject checkedPrincipal;
                    try { checkedPrincipal = AppPrincipals.Require(principal); } catch (ArgumentException) { return AuthHttp.Error(400); }
                    var token = Create(checkedPrincipal, label);
                    return token is null ? AuthHttp.Error(429) : new HttpResult.Json(new JsonObject { ["path"] = Path + "#" + token })
                    { Headers = new Dictionary<string, string[]> { ["Cache-Control"] = ["no-store"] } };
                }),
                [$"GET {Path}"] = new((request, _) => Task.FromResult<HttpResult>(new HttpResult.Html(OwnerLink.FragmentPage(Path, request.Nonce))), Page: true, Public: true),
                [$"POST {Path}"] = new(async (request, context) =>
                {
                    var body = await request.JsonAsync(4096, context.AbortSignal).ConfigureAwait(false);
                    var invite = AuthHttp.TryString(body, "token", out var token) ? Consume(token) : null;
                    if (invite is null) return AuthHttp.Error(403);
                    var issued = await sessions.IssueAsync(invite.Principal, invite.Label, SessionKind.Browser, request.Provenance.Kind != "local", context.Context).ConfigureAwait(false);
                    return AuthHttp.SignedIn(issued.Cookie);
                }, Public: true),
            },
        }));
    }

    private string? Create(JsonObject principal, string label)
    {
        lock (gate)
        {
            Expire();
            if (pending.Count >= maxPending) return null;
            var token = Secrets.NewToken();
            pending.Add(Secrets.HashToken(token), new((JsonObject)principal.DeepClone(), label[..Math.Min(label.Length, 100)], time.GetUtcNow() + Lifetime));
            return token;
        }
    }
    private Pending? Consume(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256) return null;
        lock (gate)
        {
            Expire();
            return pending.Remove(Secrets.HashToken(token), out var invite) ? invite : null;
        }
    }
    private void Expire()
    { foreach (var key in pending.Where(entry => entry.Value.Expires <= time.GetUtcNow()).Select(entry => entry.Key).ToArray()) pending.Remove(key); }
}
