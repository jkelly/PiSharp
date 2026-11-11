// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/owner.ts, packages/chord/src/node/app/pairing.ts, packages/chord/src/node/app/invite.ts, packages/chord/src/node/app/ephemeral.ts.
// Authored native regression; not an upstream execution capture.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PiSharp.Chord;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.App;
using PiSharp.Chord.Hosting.Http;

internal static class AppAuthRouteTests
{
    private static readonly ChordContext Context = ChordContext.Background;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    public static Task OwnerLinksAndCaps()
    {
        var time = new AuthClock(); var link = new OwnerLink(time);
        var old = link.Renew(); var current = link.Renew();
        Check(!link.Consume(old) && !link.Consume("wrong") && link.Consume(current) && !link.Consume(current), "owner links must be current and single use");
        var expired = link.Renew(); time.Now += OwnerLink.Lifetime;
        Check(!link.Consume(expired), "owner link accepted at expiry");
        Check(new Uri(link.Link(new("http://0.0.0.0:8123"))).Host == "127.0.0.1", "wildcard link did not use loopback");
        Check(new Uri(link.Link(new("http://[::]:8123"))).Host.Trim('[', ']') == "::1", "IPv6 wildcard link did not use bracketed loopback");
        var pairings = new Pairings(new(2, 1), time);
        var first = pairings.Begin("device", "peer-a")!;
        Check(pairings.Begin("device", "peer-a") is null, "per-peer cap ignored");
        Check(pairings.Begin("device", "peer-b") is not null && pairings.Begin("device", "peer-c") is null, "global anonymous cap ignored");
        Check(pairings.Begin("owner device", "peer-a", owner: true) is not null, "owner was subject to anonymous cap");
        Check(pairings.Waiting(first.Code.ToLowerInvariant()) is not null && pairings.Poll(first.Poll)?.Status == "waiting", "pending pairing could not be found");
        Check(pairings.Poll("garbage") is null && pairings.Poll(new string('x', 300)) is null, "malformed poll accepted");
        time.Now += Pairings.Lifetime;
        Check(pairings.Poll(first.Poll) is null && pairings.Begin("new", "peer-a") is not null, "expired pairings retained capacity");
        var page = OwnerLink.FragmentPage(OwnerLink.Path, "nonce");
        Check(page.IndexOf("history.replaceState", StringComparison.Ordinal) < page.IndexOf("fetch(", StringComparison.Ordinal), "fragment was sent before it was cleared");
        Check(!page.Contains("location.search", StringComparison.Ordinal) && page.Contains("body: JSON.stringify({ token })", StringComparison.Ordinal), "owner token did not stay out of query strings");
        return Task.CompletedTask;
    }

    public static async Task RoutesAndInvites()
    {
        var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "chord-app-auth", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = await Layout.OpenStoreAsync(System.IO.Path.Combine(directory, "store.sqlite"), Context);
            var time = new AuthClock();
            await using var host = Hosts.Create(new() { Store = store, Principal = new() { ["kind"] = "server" }, OnError = _ => { } });
            await using var sessions = new Sessions("test-cookie-secret-owner-pairing", time: time);
            await using var http = new ChordHttp(host, new() { OnError = _ => { } });
            var owner = new JsonObject { ["kind"] = "owner", ["id"] = "one" };
            var link = new OwnerLink(owner, sessions, time);
            var pairings = new Pairings(owner, sessions, new(2, 1), time);
            var invites = new Invite(owner, sessions, maxPending: 1, time);
            await host.LoadAsync([
                new("chord.http", http.Facet), new("chord.sessions", sessions.Facet),
                new("chord.owner", link.Facet!), new("chord.pairing", pairings.Facet!),
                new("app.invites", invites.Facet), new("app.login", EphemeralLogin.CreateFacet(sessions)),
                new("test.relay", Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, new HttpContribution
                {
                    Relays = [new("test-relay", (_, _) => Task.FromResult<RelayAdmission?>(new("https://relay.test")))],
                    Routes = new Dictionary<string, HttpRoute> { ["GET /rolling"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.Empty())) },
                }))),
            ], Context);
            await sessions.AttachAsync(host, Context);

            var badOwner = await Send(http, "POST", OwnerLink.Path, new() { ["token"] = "bad" });
            Check(badOwner.Response.StatusCode == 403, "wrong owner token accepted");
            var token = link.Renew();
            var signedIn = await Send(http, "POST", OwnerLink.Path, new() { ["token"] = token });
            Check(signedIn.Response.StatusCode == 200, "owner could not sign in");
            var cookie = signedIn.Response.Headers.SetCookie.ToString();
            Check(cookie.Contains("HttpOnly", StringComparison.Ordinal) && cookie.Contains("SameSite=Strict", StringComparison.Ordinal), "owner cookie security attributes missing");
            Check((await Send(http, "POST", OwnerLink.Path, new() { ["token"] = token })).Response.StatusCode == 403, "owner link replay accepted");
            time.Now += TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1);
            var renewed = await Send(http, "GET", "/rolling", new(), cookie);
            Check(renewed.Response.StatusCode == 200 && renewed.Response.Headers.SetCookie.ToString().Contains("Max-Age", StringComparison.Ordinal), "daily session cookie was not renewed");

            var first = Read(await Send(http, "POST", Pairings.Path, new() { ["label"] = "device" }));
            var pairCode = first["code"]!.GetValue<string>(); var poll = first["poll"]!.GetValue<string>();
            Check((await Send(http, "POST", Pairings.Path, new() { ["label"] = "other" }, forwarded: "203.0.113.8")).Response.StatusCode == 429, "spoofed forwarded address bypassed per-peer pairing cap");
            Check((await Send(http, "POST", Pairings.Path + "/" + pairCode, new())).Response.StatusCode == 401, "anonymous pairing approval admitted");
            var other = await sessions.IssueAsync(new() { ["kind"] = "user", ["id"] = "other" }, "other", SessionKind.Device, false, Context);
            Check((await Send(http, "POST", Pairings.Path + "/" + pairCode, new(), bearer: other.Token)).Response.StatusCode == 403, "non-owner pairing approval admitted");
            Check((await Send(http, "POST", Pairings.Path + "/" + pairCode, new(), cookie)).Response.StatusCode == 200, "owner pairing approval failed");
            var approved = Read(await Send(http, "POST", Pairings.Path + "/poll", new() { ["poll"] = poll }));
            Check(approved["status"]!.GetValue<string>() == "approved" && AppPrincipals.Same(await sessions.VerifyAsync(approved["token"]!.GetValue<string>(), Context), owner), "pairing token had wrong principal");
            Check((await Send(http, "POST", Pairings.Path + "/poll", new() { ["poll"] = poll })).Response.StatusCode == 404, "approved pairing token delivered twice");

            var concurrent = pairings.Begin("concurrent", "another-peer")!;
            var count = (await sessions.ListAsync(Context)).Count;
            var approvals = await Task.WhenAll(pairings.ApproveAsync(concurrent.Code, Context), pairings.ApproveAsync(concurrent.Code, Context));
            Check(approvals.Count(value => value) == 1 && (await sessions.ListAsync(Context)).Count == count + 1, "concurrent approvals issued duplicate sessions");

            var invited = new JsonObject { ["kind"] = "user", ["id"] = "invited" };
            Check((await Send(http, "POST", "/_chord/invites", new() { ["principal"] = invited.DeepClone(), ["label"] = "friend" }, bearer: other.Token)).Response.StatusCode == 403, "non-owner made an invitation");
            var invite = Read(await Send(http, "POST", "/_chord/invites", new() { ["principal"] = invited.DeepClone(), ["label"] = "friend" }, cookie));
            var inviteToken = invite["path"]!.GetValue<string>().Split('#')[1];
            Check((await Send(http, "POST", "/_chord/invites", new() { ["principal"] = invited.DeepClone(), ["label"] = "overflow" }, cookie)).Response.StatusCode == 429, "invitation cap ignored");
            Check((await Send(http, "POST", Invite.Path, new() { ["token"] = inviteToken })).Response.StatusCode == 200, "invitation did not sign in");
            Check((await Send(http, "POST", Invite.Path, new() { ["token"] = inviteToken })).Response.StatusCode == 403, "invitation replay accepted");
            invite = Read(await Send(http, "POST", "/_chord/invites", new() { ["principal"] = invited.DeepClone(), ["label"] = "expire" }, cookie));
            inviteToken = invite["path"]!.GetValue<string>().Split('#')[1]; time.Now += Invite.Lifetime;
            Check((await Send(http, "POST", Invite.Path, new() { ["token"] = inviteToken })).Response.StatusCode == 403, "invitation accepted at expiry");

            var loginOne = Read(await Send(http, "POST", "/_chord/login", new() { ["name"] = "Guest" }));
            var loginTwo = Read(await Send(http, "POST", "/_chord/login", new() { ["name"] = "Guest" }));
            Check(loginOne["id"]!.GetValue<string>() != loginTwo["id"]!.GetValue<string>(), "ephemeral logins reused an identity");

            var ownerFile = System.IO.Path.Combine(directory, "owner-token");
            var fileToken = await OwnerLink.EnsureTokenFileAsync(ownerFile, owner, sessions, Context);
            Check(fileToken == await OwnerLink.EnsureTokenFileAsync(ownerFile, (JsonObject)JsonNode.Parse("{\"id\":\"one\",\"kind\":\"owner\"}")!, sessions, Context), "equivalent owner did not reuse its file session");
            var newOwner = new JsonObject { ["kind"] = "owner", ["id"] = "two" };
            var replaced = await OwnerLink.EnsureTokenFileAsync(ownerFile, newOwner, sessions, Context);
            Check(replaced != fileToken && await sessions.VerifyAsync(fileToken, Context) is null && AppPrincipals.Same(await sessions.VerifyAsync(replaced, Context), newOwner), "owner change did not revoke and replace the file session");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<DefaultHttpContext> Send(ChordHttp http, string method, string path, JsonObject body,
        string? cookie = null, string? bearer = null, string? forwarded = null)
    {
        var request = new DefaultHttpContext();
        request.Request.Method = method; request.Request.Path = path; request.Request.Host = new("localhost", 8123);
        request.Connection.LocalIpAddress = IPAddress.Loopback; request.Connection.LocalPort = 8123;
        request.Connection.RemoteIpAddress = IPAddress.Loopback;
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString()); request.Request.Body = new MemoryStream(bytes);
        request.Request.ContentType = "application/json"; request.Request.ContentLength = bytes.Length;
        if (cookie is not null) request.Request.Headers.Cookie = cookie;
        if (bearer is not null) request.Request.Headers.Authorization = "Bearer " + bearer;
        if (forwarded is not null) request.Request.Headers["X-Forwarded-For"] = forwarded;
        request.Response.Body = new MemoryStream(); await http.HandleAsync(request); return request;
    }
    private static JsonObject Read(DefaultHttpContext context)
    {
        Check(context.Response.StatusCode == 200, "expected successful JSON response, got " + context.Response.StatusCode);
        return (JsonObject)JsonNode.Parse(((MemoryStream)context.Response.Body).ToArray())!;
    }
    private sealed class AuthClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
