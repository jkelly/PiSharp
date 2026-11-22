// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/node/http/http.ts, packages/chord/src/node/http/upgrades.ts,
// packages/chord/src/node/app/sign-ins.ts, packages/chord/src/node/app/index.ts, packages/chord/src/node/plugins/plugins.ts,
// packages/chord/src/node/surfaces/web.ts, packages/chord/src/tui/scope.ts.
// Authored native regressions from the Pi 1.6.0 tests packages/chord/test/node/http/upgrades.test.ts, node/app/sign-ins.test.ts,
// node/plugins/plugins.test.ts, node/surfaces/web.test.ts and tui/scope.test.ts; not an upstream execution capture.
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

    // scope.test.ts "reads it narrowed while live in the variant; a change of its variant calls changed, once per state". Upstream
    // counts exactly two redraws for the rebind (loading, then live); a native watch may deliver two quick states as one call, so one
    // or two here (terminal.160.scope-variant-one-watch-per-state counts the watches on a reference whose state the test sets).
    public static async Task ScopeVariantFollowsRebind()
    {
        await using var desk = await DeskAsync();
        var changes = 0;
        using var scope = new TerminalScope(desk.Client, () => Interlocked.Increment(ref changes));
        var held = scope.Hold(Desk, new());
        var editor = scope.Variant(held, "editor"); var reader = scope.Variant(held, "reader");
        Check(reader() is null, "a reference that is not bound yet was read in a variant");
        await Until(() => ReferenceEquals(reader(), held), "the reference is live in the reader variant");
        Check(editor() is null, "a reader was read as an editor");
        await Task.Delay(50);
        var before = Volatile.Read(ref changes);
        // Another allowed value while it is held: loading, then live in the other variant.
        await desk.Admit("editor");
        await Until(() => ReferenceEquals(editor(), held), "the reference is live in the editor variant");
        await editor()!.CallAsync("write", JsonValue.Create("edited"), Context);
        Check(reader() is null, "an editor was still read as a reader");
        await Task.Delay(50);
        // One watch of the reference's state, whoever asked for it: a redraw per state.
        var redraws = Volatile.Read(ref changes) - before;
        Check(redraws is 1 or 2, $"the rebind redrew {redraws} times");
        await desk.Admit("none");
        await Until(() => held.State.Value.Kind == "failed", "the reference is denied");
        Check(held.State.Value.Cause?.Kind == PiSharp.Chord.Data.FailureKind.Denied, "the reference failed for another cause");
        Check(editor() is null && reader() is null, "a denied reference was read in a variant");
        scope.Dispose();
        Check(editor() is null && reader() is null, "a released reference was read in a variant");
    }

    // scope.test.ts, the same case's "one watch of the reference's state, whoever asked for it: a redraw per state", and variant.test.ts
    // "while it loads between two variants it still says the last one". Upstream counts redraws across an admission's rebind; a native
    // watch may deliver two quick states as one call, so the watches are counted instead, on a reference whose state the test sets.
    public static async Task ScopeVariantOneWatchPerState()
    {
        await using var host = Hosts.Create(new() { OnError = _ => { } });
        await host.LoadAsync([], Context);
        var changes = 0;
        using var scope = new TerminalScope(host, () => Interlocked.Increment(ref changes));
        var reference = new FakeReference();
        var reader = scope.Variant(reference, "reader"); var editor = scope.Variant(reference, "editor");
        var again = scope.Variant(reference, "reader");
        await Until(() => Volatile.Read(ref changes) == 1, "the scope began following the reference's state");
        Check(reference.Source.Watches == 1, $"the scope followed one reference's state {reference.Source.Watches} times");
        Check(reader() is null && editor() is null, "a loading reference was read in a variant");
        reference.Bound = "reader"; reference.Source.Set(RefState.Live);
        await Until(() => Volatile.Read(ref changes) == 2, "one redraw for one state");
        Check(ReferenceEquals(reader(), reference) && ReferenceEquals(again(), reference) && editor() is null, "a live reader was not read as one");
        // Loading between two variants: the reference still says its last variant.
        reference.Source.Set(RefState.Loading);
        await Until(() => Volatile.Read(ref changes) == 3, "one redraw for the loading state");
        Check(reference.Variant == "reader" && reader() is null && editor() is null, "a loading reference was read in its last variant");
        reference.Bound = "editor"; reference.Source.Set(RefState.Live);
        await Until(() => Volatile.Read(ref changes) == 4, "one redraw for the other variant");
        Check(ReferenceEquals(editor(), reference) && reader() is null, "the other variant was not read");
        // Another scope follows the same state with a watch of its own.
        using (var other = new TerminalScope(host, () => { })) { other.Variant(reference, "editor"); Check(reference.Source.Watches == 2, "a second scope did not follow the state itself"); }
        scope.Dispose();
        await Until(() => reference.Source.Stopped == 2, "disposed scopes stopped their watches");
        reference.Source.Set(RefState.Loading); await Task.Delay(30);
        Check(Volatile.Read(ref changes) == 4, "a disposed scope redrew");
        // A released reference throws from Variant: it reads as nothing.
        reference.Source.Set(RefState.Live); reference.Released = true;
        Check(editor() is null, "a released reference was read in a variant");
        Check(Variants.LiveIn<FakeReference>(null, "editor") is null, "no reference was read in a variant");
    }
    private sealed class FakeReference : IReference
    {
        public FakeState Source { get; } = new();
        public string? Bound; public bool Released;
        public INativeWatchable<RefState> State => Source;
        public string? Variant => Released ? throw new PiSharp.Chord.Data.ChordError("unavailable", "released") : Bound;
        public IWatchable<JsonNode?> Member(string name) => throw new NotSupportedException();
        public IKeyedHandle KeyedMember(string name) => throw new NotSupportedException();
        public Task<object?> CallAsync(string name, object? arguments, ChordContext context) => throw new NotSupportedException();
        public void Retry() { }
        public void Release() => Released = true;
        public void Dispose() => Release();
    }
    private sealed class FakeState : INativeWatchable<RefState>
    {
        private readonly object gate = new(); private readonly List<Handle> handles = [];
        private RefState value = RefState.Loading;
        public int Watches, Stopped;
        public RefState Value { get { lock (gate) return value; } }
        public Task<INativeWatchHandle<RefState>> WatchAsync(ChordContext context)
        {
            var handle = new Handle(this);
            lock (gate) { Watches++; handles.Add(handle); }
            return Task.FromResult<INativeWatchHandle<RefState>>(handle);
        }
        public void Set(RefState next)
        {
            Handle[] current;
            lock (gate) { value = next; current = [.. handles]; }
            foreach (var handle in current) handle.Deliver(next);
        }
        private sealed class Handle(FakeState owner) : INativeWatchHandle<RefState>
        {
            private readonly TaskCompletionSource<WatchEnd> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Func<NativeWatchCall<RefState>, ChordContext, Task>? listener;
            public RefState Value => owner.Value;
            public Task<WatchEnd> Closed => closed.Task;
            public void Start(Func<NativeWatchCall<RefState>, ChordContext, Task> listener) => this.listener = listener;
            public void Deliver(RefState next) { if (!closed.Task.IsCompleted) _ = listener?.Invoke(new NativeWatchValueCall<RefState>(next), ChordContext.Background); }
            public Task<WatchEnd> StopAsync()
            {
                if (closed.TrySetResult(WatchEnd.Stopped)) lock (owner.gate) { owner.Stopped++; owner.handles.Remove(this); }
                return closed.Task;
            }
        }
    }

    // scope.test.ts "follows a reference the scope does not hold, until dispose".
    public static async Task ScopeVariantOfForeignReference()
    {
        await using var desk = await DeskAsync();
        var changes = 0;
        var scope = new TerminalScope(desk.Client, () => Interlocked.Increment(ref changes));
        var held = desk.Client.Hold(Desk, new(), Context);
        var editor = scope.Variant(held, "editor");
        await Until(() => held.State.Value.Kind == "live", "the reference is live");
        await Task.Delay(50);
        var before = Volatile.Read(ref changes);
        await desk.Admit("editor");
        await Until(() => ReferenceEquals(editor(), held) && Volatile.Read(ref changes) > before, "the scope followed the foreign reference's variant");
        scope.Dispose();
        await Task.Delay(50);
        var after = Volatile.Read(ref changes);
        await desk.Admit("reader");
        await Until(() => held.Variant == "reader" && held.State.Value.Kind == "live", "the reference is a reader again");
        await Task.Delay(50);
        Check(Volatile.Read(ref changes) == after, "a disposed scope redrew");
        Check(editor() is null, "a reader was read as an editor after the scope was disposed");
        held.Release();
    }

    // scope.test.ts "keeps no reference its holder released, while the scope lives (GC)".
    public static async Task ScopeKeepsNoReleasedReference()
    {
        await using var desk = await DeskAsync();
        using var scope = new TerminalScope(desk.Client, () => { });
        var released = await HoldReadReleaseAsync(desk.Client, scope);
        Check(await CollectedAsync(released), "the scope kept a reference its holder released");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> HoldReadReleaseAsync(ChordHost client, TerminalScope scope)
    {
        var held = client.Hold(Desk, new(), Context);
        var reader = scope.Variant(held, "reader");
        await Until(() => ReferenceEquals(reader(), held), "the reference is live in the reader variant");
        held.Release();
        Check(reader() is null, "a released reference was read in a variant");
        return new WeakReference(held);
    }
}
