// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/http/http.ts.
// Authored native regression; not an upstream execution capture.
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using PiSharp.Chord;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.Http;
using PiSharp.Telemetry;

internal static class Http152Cases
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static ChordHost Host() => Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromSeconds(2) });
    private static DefaultHttpContext Request(string path)
    {
        var request = new DefaultHttpContext();
        request.Request.Method = "GET"; request.Request.Path = path; request.Request.Host = new("localhost", 8123);
        request.Connection.LocalIpAddress = IPAddress.Loopback; request.Connection.LocalPort = 8123;
        request.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.2");
        request.Response.Body = new MemoryStream(); return request;
    }

    public static async Task AdmissionAndHeaders()
    {
        await using var host = Host(); await using var http = new ChordHttp(host, new() { OnError = _ => { } });
        var unloaded = Request("/private"); await http.HandleAsync(unloaded).WaitAsync(TimeSpan.FromSeconds(2));
        Check(unloaded.Response.StatusCode == 503, "unloaded HTTP must fail immediately");
        var handlers = 0; var wraps = 0; var authenticated = false;
        var contribution = new HttpContribution
        {
            Routes = new Dictionary<string, HttpRoute>
            {
                ["GET /private"] = new((_, _) => { handlers++; return Task.FromResult<HttpResult>(new HttpResult.Empty()); }),
                ["GET /public"] = new((r, _) => { Check(r.RemoteAddress?.ToString() == "127.0.0.2", "peer address trusted a forwarded header"); return Task.FromResult<HttpResult>(new HttpResult.Empty()); }, Public: true),
                ["GET /cross"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.Empty()), CrossSite: true),
                ["GET /error"] = new((_, _) => Task.FromException<HttpResult>(new InvalidOperationException("failure")), Public: true)
            },
            ScriptPaths = ["/assets/", "/entry.js"],
            Relays = [new("trusted", (_, _) => Task.FromResult<RelayAdmission?>(new("https://public.test")))],
            Wraps = [new("observe", async (_, next, _) => { wraps++; return await next(); })]
        };
        await host.LoadAsync([new("chord.http", http.Facet), new("test.http", Facet.Define((_, self) =>
        {
            self.Contribute(HttpRegistries.Http, contribution);
            self.Contribute(HttpRegistries.Authenticators, (HttpAuthenticator)((_, _) => Task.FromResult<JsonObject?>(authenticated ? new() { ["kind"] = "user" } : null)));
        }))], ChordContext.Background);
        var denied = Request("/private"); await http.HandleAsync(denied);
        Check(denied.Response.StatusCode == 401 && handlers == 0 && wraps == 0, "private callback or wrapper ran anonymously");
        var publicRequest = Request("/public"); publicRequest.Request.Headers["X-Forwarded-For"] = "192.0.2.1";
        await http.HandleAsync(publicRequest);
        Check(publicRequest.Response.StatusCode == 200, "explicit public route refused");
        Check(publicRequest.Response.Headers.ContentSecurityPolicy.ToString().Contains("https://public.test/assets/ https://public.test/entry.js 'nonce-"), "script paths not narrowed to admitted origin");
        var cross = Request("/cross"); await http.HandleAsync(cross); Check(cross.Response.StatusCode == 200, "cross-site did not imply public");
        var error = Request("/error"); await http.HandleAsync(error);
        Check(error.Response.StatusCode == 500 && error.Response.Headers.ContentSecurityPolicy.ToString().Contains("http://localhost:8123/assets/"), "error response lost script CSP");
        authenticated = true; var allowed = Request("/private"); await http.HandleAsync(allowed);
        Check(allowed.Response.StatusCode == 200 && handlers == 1, "authenticated route refused");
        var secret = Encoding.UTF8.GetBytes("signed-cookie-regression-secret"); var clock = new FixedClock();
        var cookie = SignedCookies.Create("session", "a.b", secret, false, TimeSpan.FromMinutes(5), clock: clock);
        var signed = SignedCookies.ReadSigned(cookie, "session", secret, clock);
        Check(signed?.Value == "a.b" && signed.Expires == clock.GetUtcNow().AddMinutes(5), "signed expiry was lost");
        Check(SignedCookies.ReadSigned(SignedCookies.Create("session", "a", secret, false, clock: clock), "session", secret, clock)?.Expires is null, "session cookie acquired expiry");
        clock.Now = clock.Now.AddMinutes(5); Check(SignedCookies.ReadSigned(cookie, "session", secret, clock) is null, "expired signed cookie accepted");
    }

    public static async Task TraceTrust()
    {
        var telemetry = new TraceProbe(); await using var host = Host();
        await using var http = new ChordHttp(host, new() { OnError = _ => { }, Telemetry = telemetry });
        await host.LoadAsync([new("chord.http", http.Facet), new("test.http", Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, new HttpContribution
        {
            Routes = new Dictionary<string, HttpRoute> { ["GET /item/:id"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.Text("done")), Public: true) },
            Relays = [new("trusted", (_, _) => Task.FromResult<RelayAdmission?>(new("https://public.test")))]
        })))], ChordContext.Background);
        var direct = Request("/item/private-id"); direct.Request.Headers["traceparent"] = "untrusted";
        await http.HandleAsync(direct); Check(telemetry.Carrier is null, "direct trace was trusted");
        var relayed = Request("/item/another-private-id"); relayed.Request.Headers["X-Forwarded-For"] = "192.0.2.1";
        relayed.Request.Headers["traceparent"] = "trusted-parent"; relayed.Request.Headers["tracestate"] = "vendor=value";
        await http.HandleAsync(relayed); Check(telemetry.Carrier?["traceparent"] == "trusted-parent" && telemetry.Carrier["tracestate"] == "vendor=value", "verified relay carrier lost");
        relayed = Request("/item/secret"); relayed.Request.Headers["X-Forwarded-For"] = "192.0.2.1"; relayed.Request.Headers["traceparent"] = new string('a', 257);
        await http.HandleAsync(relayed); Check(telemetry.Carrier is null, "oversized trace header accepted");
        for (var attempt = 0; attempt < 100 && telemetry.Memory.GetSpans().Any(x => !x.Settled); attempt++) await Task.Delay(10);
        var spans = telemetry.Memory.GetSpans().Where(x => x.Name == "chord.http").ToArray();
        Check(spans.Length == 3 && spans.All(x => x.Settled && x.Attributes["route"]?.GetString() == "GET /item/:id" && x.Attributes["status"]?.GetNumber() == 200), "HTTP telemetry leaked a raw path or did not settle after delivery");
    }

    public static async Task SocketsAndHooks()
    {
        await using var host = Host(); var authenticated = false; var opened = 0; var cleaned = 0; var callbacks = 0;
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var http = new ChordHttp(host, new()
        {
            Port = 0, LinksPath = "/links", OnError = _ => { },
            LinksOpened = (_, _) => { opened++; return Task.FromResult<Func<Task>?>(() => { cleaned++; cleanup.TrySetResult(); return Task.CompletedTask; }); }
        });
        HttpSocket receive = async (socket, _, call) => { callbacks++; await socket.ReceiveAsync(new byte[64].AsMemory(), call.AbortSignal); };
        await host.LoadAsync([new("chord.http", http.Facet), new("test.http", Facet.Define((_, self) =>
        {
            self.Contribute(HttpRegistries.Authenticators, (HttpAuthenticator)((_, _) => Task.FromResult<JsonObject?>(authenticated ? new() { ["kind"] = "user" } : null)));
            self.Contribute(HttpRegistries.Http, new HttpContribution
            {
                Sockets = new Dictionary<string, HttpSocket> { ["/private"] = receive },
                SocketOptions = new Dictionary<string, HttpSocketOptions> { ["/public"] = new(receive, Public: true, MaxPayload: 8) }
            });
        }))], ChordContext.Background);
        var listener = await http.ListeningAsync(ChordContext.Background);
        Uri SocketUri(string path) => new UriBuilder(listener) { Scheme = "ws", Path = path }.Uri;
        foreach (var path in new[] { "/private", "/links" })
        {
            using var denied = new ClientWebSocket(); denied.Options.CollectHttpResponseDetails = true;
            try { await denied.ConnectAsync(SocketUri(path), CancellationToken.None); throw new Exception("anonymous private socket upgraded"); }
            catch (WebSocketException) { Check(denied.HttpStatusCode == HttpStatusCode.Unauthorized, "anonymous socket must return 401"); }
        }
        Check(callbacks == 0 && opened == 0, "denied socket ran a callback");
        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(SocketUri("/public"), CancellationToken.None);
            await socket.SendAsync(new byte[9].AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
            var result = await socket.ReceiveAsync(new byte[64].AsMemory(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Check(result.MessageType == WebSocketMessageType.Close && socket.CloseStatus == WebSocketCloseStatus.MessageTooBig, "oversize did not complete close with 1009");
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None);
        }
        authenticated = true;
        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(SocketUri("/links"), CancellationToken.None);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Check(opened == 1 && cleaned == 1, "link open/cleanup must run exactly once");
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 10, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TraceProbe : ITelemetryContext, ITelemetryPropagation
    {
        public InMemoryTelemetryContext Memory { get; } = new();
        public IReadOnlyDictionary<string, string>? Carrier { get; private set; }
        public ITelemetryMetrics Metrics => Memory;
        public ITelemetryPropagation? Propagation => this;
        public Task<T> StartSpanAsync<T>(SpanOptions options, Func<ITelemetrySpan, Task<T>> callback) => Memory.StartSpanAsync(options, callback);
        public IReadOnlyDictionary<string, string>? Inject(ITelemetryContext context) => Memory.Inject(context);
        public ITelemetryContext Extract(IReadOnlyDictionary<string, string>? carrier) { Carrier = carrier; return Memory; }
    }
}
