// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/app.ts, packages/chord/src/node/app/worker.ts.
// Authored native regression; not an upstream execution capture.
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Host;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Hosting.App;
using PiSharp.Chord.Hosting.Http;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Hosting.Surfaces;
using PiSharp.Chord.TelemetryIngest;
using PiSharp.Telemetry;

internal static class AppStartTests
{
    private static readonly ChordContext Context = ChordContext.Background;
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    public static async Task StartupAndRecovery()
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "app-start", Guid.NewGuid().ToString("N")));
        var plugins = Path.Combine(root, "built-in"); Directory.CreateDirectory(plugins);
        var data = Path.Combine(root, "data"); var requested = 0;
        AppOptions Options(string? source = null) => new()
        {
            Name = "test-app", DataDir = data, Root = source ?? plugins, Api = new("test-app-api", ["v1"]),
            Principal = new(new() { ["kind"] = "server" }, new() { ["kind"] = "owner", ["id"] = "one" }),
            OnError = _ => { },
            CoreFacets = services => [new("test.http", Facet.Define((_, self) => self.Contribute(HttpRegistries.Http, new HttpContribution
            {
                Routes = new Dictionary<string, HttpRoute>
                {
                    ["GET /health"] = new((_, _) => Task.FromResult<HttpResult>(new HttpResult.Text("ready")), Public: true),
                    ["GET /private"] = new((_, _) => { Interlocked.Increment(ref requested); return Task.FromResult<HttpResult>(new HttpResult.Text("private")); }),
                },
            })))],
        };
        try
        {
            string token, ownerToken;
            await using (var app = await App.StartServerAsync(Options(), Context))
            {
                Check(app.Listening.IsLoopback && app.Listening.Port > 0, "application did not bind loopback on a free port");
                await Task.WhenAll(app.BasesBuilt, app.BaseWatched);
                using var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = app.Listening };
                Check(await client.GetStringAsync("/health") == "ready", "core route was not available after startup");
                Check((await client.GetAsync("/private")).StatusCode == HttpStatusCode.Unauthorized && requested == 0, "private handler ran anonymously");
                ownerToken = (await File.ReadAllTextAsync(app.OwnerTokenFile)).Trim();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerToken);
                Check(await client.GetStringAsync("/private") == "private" && requested == 1, "owner token did not authenticate");
                var issued = await app.Sessions.IssueAsync(new() { ["kind"] = "user", ["id"] = "persistent" }, "device", SessionKind.Device, false, Context);
                token = issued.Token;
                var seq = app.Store.LastSeq;
                for (var i = 0; i < 5; i++) Check(await app.Sessions.VerifyAsync(token, Context) is not null, "issued session failed verification");
                Check(app.Store.LastSeq == seq, "session verification wrote lastUsed more than once a minute");
                var serial = new Uri(app.OwnerLink); var oneTime = serial.Fragment[1..];
                using var browser = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = app.Listening };
                using var post = await browser.PostAsync("/_chord/owner", new StringContent(new JsonObject { ["token"] = oneTime }.ToJsonString(), Encoding.UTF8, "application/json"));
                Check(post.IsSuccessStatusCode && post.Headers.TryGetValues("Set-Cookie", out _), "owner fragment could not sign in");
                using var replay = await browser.PostAsync("/_chord/owner", new StringContent(new JsonObject { ["token"] = oneTime }.ToJsonString(), Encoding.UTF8, "application/json"));
                Check(replay.StatusCode == HttpStatusCode.Forbidden, "owner link was not single use");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await app.CloseAsync(ChordContext.WithAbortSignal(cancelled.Token, Context)); } catch (OperationCanceledException) { }
                await app.CloseAsync(Context);
                var persisted = await File.ReadAllBytesAsync(app.Layout.StoreFile);
                Check(!Encoding.UTF8.GetString(persisted).Contains(token, StringComparison.Ordinal), "plaintext session token was stored in SQLite");
            }
            await using (var app = await App.StartServerAsync(Options(), Context))
            {
                Check(await app.Sessions.VerifyAsync(token, Context) is { } principal && principal["id"]!.GetValue<string>() == "persistent", "persistent session did not survive restart");
                Check((await File.ReadAllTextAsync(app.OwnerTokenFile)).Trim() == ownerToken, "same owner did not reuse its file session after restart");
                await app.Sessions.RevokeAsync(token.Split('.')[0], Context);
                Check(await app.Sessions.VerifyAsync(token, Context) is null, "revoked session still authenticates");
            }
            var missing = Path.Combine(root, "missing-root");
            try { await App.StartServerAsync(Options(missing), Context); throw new InvalidOperationException("missing required root was accepted"); }
            catch (PluginError) { }
            await using var recovered = await App.StartServerAsync(Options(), Context);
            Check(recovered.Listening.Port > 0, "partial startup failure retained the data directory lock");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static async Task SessionExpiryAndEphemeralLinks()
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "app-sessions", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = await Layout.OpenStoreAsync(Path.Combine(root, "store.sqlite"), Context);
            await using var host = Hosts.Create(new() { Store = store, Principal = new() { ["kind"] = "server" }, OnError = _ => { } });
            var clock = new SessionClock(); await using var sessions = new Sessions("test-session-key", maxEphemeral: 2, time: clock);
            await host.LoadAsync([new("app.sessions", sessions.Facet)], Context); await sessions.AttachAsync(host, Context);
            var principal = new JsonObject { ["kind"] = "user", ["id"] = "one" };
            var first = await sessions.IssueAsync(principal, "device", SessionKind.Device, true, Context);
            Check(first.Cookie.Contains("Secure", StringComparison.Ordinal) && first.Cookie.Contains("HttpOnly", StringComparison.Ordinal) && first.Cookie.Contains("SameSite=Strict", StringComparison.Ordinal), "persistent cookie security attributes missing");
            Check(await sessions.VerifyAsync("__proto__.bad", Context) is null && await sessions.VerifyAsync("constructor.bad", Context) is null && await sessions.VerifyAsync(new string('x', 10000), Context) is null, "malicious token ID was accepted");
            clock.Now += TimeSpan.FromMinutes(2); var seq = store.LastSeq;
            Check(await sessions.VerifyAsync(first.Token, Context) is not null && store.LastSeq == seq + 1, "last-used touch was not persisted");
            seq = store.LastSeq; await sessions.VerifyAsync(first.Token, Context); Check(store.LastSeq == seq, "same-minute touch wrote again");
            clock.Now += Sessions.IdleExpiry + TimeSpan.FromMilliseconds(1);
            Check(await sessions.VerifyAsync(first.Token, Context) is null, "unused session did not expire after 400 days");
            var a = await sessions.IssueAsync(principal, "a", SessionKind.Ephemeral, false, Context);
            var b = await sessions.IssueAsync(principal, "b", SessionKind.Ephemeral, false, Context);
            var c = await sessions.IssueAsync(principal, "c", SessionKind.Ephemeral, false, Context);
            Check(await sessions.VerifyAsync(a.Token, Context) is null && await sessions.VerifyAsync(b.Token, Context) is not null, "ephemeral cap did not evict oldest");
            Check((await sessions.ListAsync(Context)).All(info => info.Kind != SessionKind.Ephemeral), "ephemeral sessions were listed");
            Check(!c.Cookie.Contains("Max-Age", StringComparison.Ordinal), "ephemeral cookie has persistent lifetime");
            clock.Advance(TimeSpan.FromMinutes(1));
            Check(await sessions.VerifyAsync(b.Token, Context) is null && await sessions.VerifyAsync(c.Token, Context) is null, "ephemeral session without initial link did not expire");

            using var temporary = new EphemeralSessions(2, TimeSpan.FromMinutes(1), clock);
            temporary.Issue("linked", Secrets.HashToken("secret"), principal);
            var close = temporary.LinkOpened("linked")!; clock.Advance(TimeSpan.FromMinutes(2));
            Check(temporary.Verify("linked", "secret") is not null, "active ephemeral link expired");
            close(); close(); clock.Advance(TimeSpan.FromSeconds(30));
            var reclose = temporary.LinkOpened("linked")!; clock.Advance(TimeSpan.FromMinutes(1));
            Check(temporary.Verify("linked", "secret") is not null, "reconnect failed to cancel expiry");
            reclose(); clock.Advance(TimeSpan.FromMinutes(1));
            Check(temporary.Verify("linked", "secret") is null, "last link close did not expire session");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static async Task BaseBuildIsolation()
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "app-base", Guid.NewGuid().ToString("N")));
        var plugins = Path.Combine(root, "plugins"); Directory.CreateDirectory(plugins);
        try
        {
            var reports = new List<AppError>();
            var surface = new SurfaceBuild(new() { Builder = new FailedSurfaceBuilder(), Base = new("web", "missing.js", "/_surface/") });
            await using var app = await App.StartServerAsync(new()
            {
                Name = "base-isolation", DataDir = Path.Combine(root, "data"), Root = plugins, Api = new("test-app", ["v1"]),
                Principal = new(new() { ["kind"] = "server" }, new() { ["kind"] = "owner" }),
                Surfaces = [surface], OnError = error => reports.Add(error),
            }, Context);
            await app.BasesBuilt;
            Check(reports.Any(error => error.Kind == "base_build" && error.Base == "/_surface/"), "failed base was not reported");
            using var client = new HttpClient { BaseAddress = app.Listening };
            Check((await client.GetAsync("/_surface/base.js")).StatusCode == HttpStatusCode.ServiceUnavailable, "failed surface did not isolate its route");
            Check((await client.GetAsync("/_chord/owner")).IsSuccessStatusCode, "surface failure stopped the application");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class FailedSurfaceBuilder : ISurfaceBuilder
    {
        public Task<SurfaceBuildArtifact> BuildAsync(SurfaceBuildRequest request, CancellationToken cancellationToken = default)
            => Task.FromException<SurfaceBuildArtifact>(new IOException("fixture base build failure"));
    }
    public static async Task TelemetryShutdown()
    {
        var recorder = new TelemetryRecorder(() => "worker-test", 10);
        await recorder.StartSpanAsync(new SpanOptions("held-ingest"), _ => Task.FromResult(0));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = 0;
        var sender = new TelemetrySender(recorder, false, _ => { entered.TrySetResult(); return held.Task; },
            () => { Interlocked.Increment(ref released); held.TrySetResult(); });
        var pump = new WorkerTelemetryPump(sender, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(30));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await pump.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await pump.DisposeAsync();
        Check(released == 1, "telemetry shutdown did not release its held ingest exactly once");
        Check(held.Task.IsCompleted, "telemetry shutdown left the original ingest held");
    }
    public static async Task CleanupRetainsStoreAndLock()
    {
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "app-cleanup", Guid.NewGuid().ToString("N")));
        var plugins = Path.Combine(root, "plugins"); Directory.CreateDirectory(plugins);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = Contract.Local("test.app.late-closer", new() { Version = 1, Params = [], Members = [] });
        try
        {
            await using var app = await App.StartServerAsync(new()
            {
                Name = "cleanup", DataDir = Path.Combine(root, "data"), Root = plugins, Api = new("test-api", ["v1"]),
                Principal = new(new() { ["kind"] = "server" }, new() { ["kind"] = "owner" }),
                Timeouts = new(Close: TimeSpan.FromMilliseconds(20)), OnError = _ => { },
                CoreFacets = services => [new("test.late-closer", Facet.Define((host, _) => host.Serve(lifetime, (_, _, self) =>
                {
                    self.Open(_ => Task.FromResult(new object()), async (_, _, _) =>
                    {
                        entered.TrySetResult(); await release.Task;
                        try
                        {
                            await using var parts = services.Store.Export(Context).GetAsyncEnumerator();
                            await parts.MoveNextAsync();
                            exported.TrySetResult();
                        }
                        catch (Exception error) { exported.TrySetException(error); throw; }
                    });
                    return new(new Dictionary<string, object?>());
                })))],
            }, Context);
            using var held = await app.Host.GetAsync(lifetime, new(), Context);
            var closing = app.CloseAsync(Context);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Task.Delay(80);
                Check(!closing.IsCompleted, "app close returned before the original native closer settled");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await app.CloseAsync(ChordContext.WithAbortSignal(cancelled.Token, Context)); } catch (OperationCanceledException) { }
                try
                {
                    await using var duplicate = await Layout.OpenAsync(app.DataDir, Context);
                    throw new InvalidOperationException("app released its directory lock while a native closer still used the store");
                }
                catch (IOException) { }
            }
            finally { release.TrySetResult(); }
            await closing.WaitAsync(TimeSpan.FromSeconds(2));
            await exported.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await using var reopened = await Layout.OpenAsync(app.DataDir, Context);
        }
        finally { release.TrySetResult(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class SessionClock : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new ManualTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
        public void Advance(TimeSpan delta)
        {
            Now += delta;
            foreach (var timer in timers.ToArray()) timer.Fire();
        }
        private sealed class ManualTimer(SessionClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? due;
            private TimeSpan period;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { if (disposed) return false; this.period = period; due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime; return true; }
            public void Fire()
            { if (disposed || due is null || due > clock.Now) return; due = period == Timeout.InfiniteTimeSpan ? null : clock.Now + period; callback(state); }
            public void Dispose() { disposed = true; due = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
