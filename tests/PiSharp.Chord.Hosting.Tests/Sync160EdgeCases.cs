// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/node/http/http.ts, packages/chord/src/node/http/upgrades.ts,
// packages/chord/src/node/app/sign-ins.ts, packages/chord/src/node/app/index.ts, packages/chord/src/node/plugins/plugins.ts,
// packages/chord/src/node/surfaces/web.ts.
// Authored native regressions from the Pi 1.6.0 tests packages/chord/test/node/http/upgrades.test.ts, node/app/sign-ins.test.ts,
// node/plugins/plugins.test.ts and node/surfaces/web.test.ts; not an upstream execution capture.
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.App;
using PiSharp.Chord.Hosting.Http;
using PiSharp.Chord.Hosting.Plugins;
using PiSharp.Chord.Hosting.Surfaces;
using PiSharp.Chord.Hosting.Terminal;
using PiSharp.Chord.Surfaces;
using static Sync160EdgeSupport;

internal static class Sync160EdgeCases
{
    private static readonly ChordContext Context = ChordContext.Background;

    // upgrades.test.ts "gives links.opened what terminates that link at once: the others stay open".
    public static async Task LinksOpenedClose()
    {
        await using var host = Hosts.Create(new() { OnError = _ => { }, CloseTimeout = TimeSpan.FromSeconds(2) });
        var gate = new object(); var closes = new List<Action>(); var closed = new List<int>();
        await using var http = new ChordHttp(host, new()
        {
            Port = 0, LinksPath = "/chord", OnError = _ => { },
            LinksOpened = (_, _, close) =>
            {
                int index;
                lock (gate) { closes.Add(close); index = closes.Count - 1; }
                return Task.FromResult<Func<Task>?>(() => { lock (gate) closed.Add(index); return Task.CompletedTask; });
            },
        });
        await host.LoadAsync([new("chord.http", http.Facet), new("test.http", Facet.Define((_, self) =>
            self.Contribute(HttpRegistries.Authenticators, (HttpAuthenticator)((_, _) => Task.FromResult<JsonObject?>(new() { ["kind"] = "owner" })))))], Context);
        var listener = await http.ListeningAsync(Context);
        var uri = new UriBuilder(listener) { Scheme = "ws", Path = "/chord" }.Uri;
        using var first = new ClientWebSocket(); using var second = new ClientWebSocket();
        await first.ConnectAsync(uri, CancellationToken.None); await second.ConnectAsync(uri, CancellationToken.None);
        await Until(() => { lock (gate) return closes.Count == 2; }, "both links were opened");
        // Terminated, not closed with a handshake: the peer sees an abnormal close (upstream: close code 1006), never a close frame.
        var ended = AbnormalCloseAsync(first);
        Action terminate; lock (gate) terminate = closes[0];
        terminate();
        Check(await ended.WaitAsync(TimeSpan.FromSeconds(5)), "a terminated link was closed with a handshake");
        await Until(() => { lock (gate) return closed.Count > 0; }, "the terminated link's cleanup ran");
        lock (gate) Check(closed.SequenceEqual([0]), "only the terminated link was cleaned up");
        Check(second.State == WebSocketState.Open, "another principal's link was closed too");
        // A second call, after the link is gone, does nothing.
        terminate();
        await Task.Delay(50);
        lock (gate) Check(closed.SequenceEqual([0]), "a second close call did something");
        Check(second.State == WebSocketState.Open, "a second close call closed another link");
        await second.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await Until(() => { lock (gate) return closed.Count == 2; }, "the other link's cleanup ran once it closed");
    }

    // Whether `socket` ended without a close frame: the receive fails, or the socket is aborted.
    private static async Task<bool> AbnormalCloseAsync(ClientWebSocket socket)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return false;
            }
        }
        catch (WebSocketException) { return true; }
        catch (IOException) { return true; }
    }

    // sign-ins.test.ts (the renamed sessions.test.ts): the cookie, the contract and the persisted document carry the sign-in names.
    // There is no migration: a store written by 1.5.2 keeps its `chord.app.sessions` document unread, and everyone signs in again.
    public static async Task SignInNames()
    {
        Check(SignIns.CookieName == "chord-sign-in", "the sign-in cookie's name is not chord-sign-in");
        Check(SignIns.Type.Name == "chord.app.sign-ins", "the sign-ins contract's name is not chord.app.sign-ins");
        var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts", "app-sign-in-names", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "store.sqlite"); IssuedSignIn issued;
            await using (var store = await Layout.OpenStoreAsync(file, Context))
            {
                await using var host = Hosts.Create(new() { Store = store, Principal = new() { ["kind"] = "server" }, OnError = _ => { } });
                await using var signIns = new SignIns("test-sign-in-names");
                await host.LoadAsync([new("chord.app.sign-ins", signIns.Facet)], Context); await signIns.AttachAsync(host, Context);
                issued = await signIns.IssueAsync(new() { ["kind"] = "user", ["id"] = "one" }, "browser", SignInKind.Browser, false, Context);
                Check(issued.Cookie.StartsWith("chord-sign-in=", StringComparison.Ordinal), "an issued cookie is not named chord-sign-in: " + issued.Cookie.Split('=')[0]);
                var listed = (await signIns.ListAsync(Context)).Single();
                Check(listed.Id == issued.Id && listed.Kind == SignInKind.Browser, "the issued sign-in was not listed");
                // A cookie under the 1.5.2 name carries no sign-in any more.
                var old = "chord-session=" + issued.Cookie.Split(';')[0].Split('=', 2)[1];
                Check(SignedCookies.Read(old, SignIns.CookieName, Encoding.UTF8.GetBytes("test-sign-in-names")) is null, "a chord-session cookie still signs in");
                Check(SignedCookies.Read(issued.Cookie.Split(';')[0], SignIns.CookieName, Encoding.UTF8.GetBytes("test-sign-in-names")) == issued.Token, "the sign-in cookie does not carry its token");
                await host.CloseAsync(Context);
            }
            var persisted = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
            Check(persisted.Contains("chord.app.sign-ins", StringComparison.Ordinal), "the persisted document is not named chord.app.sign-ins");
            Check(persisted.Contains("signIns", StringComparison.Ordinal), "the persisted document has no signIns field");
            Check(!persisted.Contains("chord.app.sessions", StringComparison.Ordinal), "the 1.5.2 document name chord.app.sessions was written");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // node/app/index.ts exports `appInfoFacet` and `workerInfoFacet` at 1.6.0, for an app that runs hosts of its own (not through
    // startServer or startWorker): a plugin written against the kit's `AppInfo` and `WorkerInfo` finds them there too.
    public static async Task InfoFacetsServeAHostOfItsOwn()
    {
        Check(typeof(Info).IsPublic && typeof(Info).GetMethod(nameof(Info.AppInfoFacet))!.IsPublic && typeof(Info).GetMethod(nameof(Info.WorkerInfoFacet))!.IsPublic,
            "the info facets are not public");
        await using var host = Hosts.Create(new() { OnError = _ => { }, Principal = new() { ["kind"] = "server" } });
        var address = new PiSharp.Chord.Contracts.HostKind("project", new() { ["id"] = PiSharp.Chord.Contracts.Schemas.Str() }).Address(("id", JsonValue.Create("p1")));
        var loaded = await host.LoadAsync([
            new("chord.app.info", Info.AppInfoFacet(new("/data", "own-app", "v1", new() { ["kind"] = "owner" }))),
            new("chord.app.worker-info", Info.WorkerInfoFacet(new(address, new JsonObject { ["seed"] = 7 }))),
        ], Context);
        Check(loaded.Failed.Count == 0, "an info facet failed to load on a host of the app's own");
        using var app = await host.GetAsync(Info.AppInfo, new(), Context);
        Check(app.Member("name").Value?.GetValue<string>() == "own-app" && app.Member("version").Value?.GetValue<string>() == "v1" &&
            app.Member("dataDir").Value?.GetValue<string>() == "/data" && app.Member("owner").Value?["kind"]?.GetValue<string>() == "owner", "the app's info was not served");
        using var worker = await host.GetAsync(Info.WorkerInfo, new(), Context);
        Check(worker.Member("address").Value?["kind"]?.GetValue<string>() == "project" && worker.Member("start").Value?["seed"]?.GetValue<int>() == 7, "the worker's info was not served");
    }

    // plugins.test.ts "discovers again for a named plugin the watcher has not reported yet: removed, or new". The native manager runs
    // discovery at every build, so a build never trusts the watcher's timing: this pins that.
    public static async Task PluginsRediscoverBeforeWatcher()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "artifacts", "pisharp-plugin-rediscover-" + Guid.NewGuid().ToString("N"));
        var plugins = Path.Combine(root, "plugins");
        try
        {
            foreach (var name in new[] { "a", "c" })
            {
                Directory.CreateDirectory(Path.Combine(plugins, name));
                await File.WriteAllTextAsync(Path.Combine(plugins, name, PluginScanner.ManifestFile), JsonSerializer.Serialize(new PluginManifest("test." + name, "plugin.dll")));
            }
            var builder = new PluginBuilder(new() { DotnetPath = "unused", ArtifactDirectory = Path.Combine(root, "artifacts") });
            await using var manager = new PluginManager(new[] { plugins }, builder);
            var first = await manager.BuildAsync();
            Check(first.Plugins.Select(plugin => plugin.Plugin.Manifest.Name).Order(StringComparer.Ordinal).SequenceEqual(["test.a", "test.c"]), "the first build did not discover both plugins");
            // Built in the same turn as the change, before any file event can arrive: the watcher has marked nothing yet.
            Directory.Delete(Path.Combine(plugins, "c"), true);
            var removed = await manager.BuildAsync();
            Check(removed.Plugins.Select(plugin => plugin.Plugin.Manifest.Name).SequenceEqual(["test.a"]), "a removed plugin was still built");
            Check(manager.State.Dirty.ContainsKey("test.c") && manager.State.SetChanged, "a removed plugin was not marked");
            Directory.CreateDirectory(Path.Combine(plugins, "f"));
            await File.WriteAllTextAsync(Path.Combine(plugins, "f", PluginScanner.ManifestFile), JsonSerializer.Serialize(new PluginManifest("test.f", "plugin.dll")));
            var added = await manager.BuildAsync();
            Check(added.Plugins.Select(plugin => plugin.Plugin.Manifest.Name).Order(StringComparer.Ordinal).SequenceEqual(["test.a", "test.f"]), "a new plugin was not built");
            Check(manager.State.Dirty.ContainsKey("test.f"), "a new plugin was not marked");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    // web.test.ts "routes: false": an app with an HTTP stack of its own serves the surface's files, the page's assets and the base hash
    // itself. Natively the surface never contributes routes by itself (`Contribution` is opt-in), so this is `GetFileAsync`, `Built.Base`
    // and the parts a page includes (`BaseScript`, `BaseStyle`, `Set`), with no `Http` registry involved.
    public static async Task SurfaceWithoutRoutes()
    {
        var inputs = BaseInputs.FromHashes([]);
        await using var surface = new SurfaceBuild(new()
        {
            Base = new("web", "entry.js", "/_chord/web/"),
            Builder = new FixedBuilder(() => new("globalThis.started=true;", inputs, "body{margin:0}")),
            Plugins = _ => Task.FromResult<IReadOnlyList<SurfacePluginBuild>>([new("test.notes", new("function(){return 'notes';}", inputs, "p{color:red}"))]),
        });
        var built = await surface.BuildAsync();
        Check(built.Success && built.Base is { Length: > 0 }, "the surface did not build");
        // The base hash now: what an outdated page compares.
        Check(surface.Built?.Base == built.Base && surface.Set.Base == built.Base, "the base hash is not what the scripts set shows");
        // What a page includes now: the base script and stylesheet, the plugins' combined file and their stylesheets.
        Check(surface.BaseScript is { } script && script.StartsWith("/_chord/web/base.", StringComparison.Ordinal), "no base script to include");
        Check(surface.BaseStyle is { } style && style.EndsWith(".css", StringComparison.Ordinal), "no base stylesheet to include");
        var plugin = surface.Set.Plugins.Single();
        Check(plugin.Name == "test.notes" && plugin.Css is not null && surface.Set.Combined.Length > 0, "the page's plugin assets are missing");
        // A file of the surface, served forever; 404 for none.
        foreach (var path in new[] { surface.BaseScript!, surface.BaseStyle!, surface.Set.Combined, plugin.Url, plugin.Css! })
        {
            var file = await surface.GetFileAsync(path);
            Check(file.Status == 200 && file.Headers["Cache-Control"].Single() == "private, max-age=31536000, immutable", "a surface file was not served forever: " + path);
        }
        Check((await surface.GetFileAsync("/_chord/web/none.js")).Status == 404, "a missing surface file was not 404");
        // 503 when the build failed, and no base hash.
        await using var failed = new SurfaceBuild(new() { Base = new("web", "missing.js", "/_chord/web/"), Builder = new FixedBuilder(() => throw new IOException("base broken")) });
        Check((await failed.GetFileAsync("/_chord/web/base.js")).Status == 503, "a failed surface did not answer 503");
        Check(failed.Built is { Success: false, Base: null }, "a failed surface still has a base hash");
    }
    private sealed class FixedBuilder(Func<SurfaceBuildArtifact> build) : ISurfaceBuilder
    {
        public Task<SurfaceBuildArtifact> BuildAsync(SurfaceBuildRequest request, CancellationToken cancellationToken = default)
        {
            try { return Task.FromResult(build()); }
            catch (Exception error) { return Task.FromException<SurfaceBuildArtifact>(error); }
        }
    }
}
