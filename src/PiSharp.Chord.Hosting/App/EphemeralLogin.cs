// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/ephemeral.ts.
using System.Text.Json.Nodes;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Http;

namespace PiSharp.Chord.Hosting.App;

/// <summary>An explicitly enabled public, pick-a-name login. Every login gets a fresh identity.</summary>
public static class EphemeralLogin
{
    public static Facet CreateFacet(Sessions sessions) => Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, new HttpContribution
    {
        Routes = new Dictionary<string, HttpRoute>
        {
            ["POST /_chord/login"] = new(async (request, context) =>
            {
                var body = await request.JsonAsync(4096, context.AbortSignal).ConfigureAwait(false);
                if (!AuthHttp.TryString(body, "name", out var supplied) || supplied.Trim().Length == 0) return AuthHttp.Error(400);
                var name = supplied.Trim()[..Math.Min(supplied.Trim().Length, 40)];
                var principal = new JsonObject { ["kind"] = "user", ["id"] = Secrets.NewToken(12), ["name"] = name };
                var issued = await sessions.IssueAsync(principal, name, SessionKind.Ephemeral, request.Provenance.Kind != "local", context.Context).ConfigureAwait(false);
                return new HttpResult.Json(principal) { Headers = new Dictionary<string, string[]> { ["Set-Cookie"] = [issued.Cookie], ["Cache-Control"] = ["no-store"] } };
            }, Public: true),
        },
    }));
}
