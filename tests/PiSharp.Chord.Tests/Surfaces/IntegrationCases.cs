// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/surfaces/active-theme.ts, packages/chord/src/surfaces/active-language.ts, packages/chord/src/surfaces/page-state.ts.
// Authored native regression; not an upstream execution capture.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Surfaces;
using SurfaceMessages = PiSharp.Chord.Surfaces.Messages;
using static ChordSupport;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SurfacesIntegrationCases() =>
    [
        Case("surfaces.client.completed-predecessor-never-holds-gate-across-dispatch", async () =>
        {
            var initial = new ScriptSet("base", "/one.js", [new("plugin", "one", "/one.js")]);
            var next = new ScriptSet("base", "/two.js", [new("plugin", "two", "/two.js")]);
            await using var server = Hosts.Create(new() { Address = Scripts.Server.Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { } });
            var loaded = await server.LoadAsync([new("scripts", Facet.Define((host, _) => host.Serve(Scripts.WebScripts, (_, _, _) =>
                new(new Dictionary<string, object?> { ["set"] = PiSharp.Chord.State.Watchables.Constant(initial.ToJson()) }, "main"))))], ChordContext.Background);
            Equal(0, loaded.Failed.Count, "server script facet loaded");
            RegisteredScript Script(string digest) => new("plugin", digest, (_, module) => module.Exports = Facet.Define((_, _) => { }));
            using var releaseFetch = new ManualResetEventSlim();
            var fetchEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcherEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcherQueued = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetches = 0;
            var client = await Client.StartAsync(new()
            {
                Scripts = Scripts.WebScripts, Base = "base", Initial = [Script("one")], Environment = new SurfaceClock(),
                FetchScript = (entry, _) =>
                {
                    if (Interlocked.Increment(ref fetches) == 1)
                    {
                        fetchEntered.TrySetResult();
                        if (!releaseFetch.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fetch barrier was not released");
                    }
                    return Task.FromResult(Script(entry.Digest));
                },
                Open = events =>
                {
                    var pair = MemoryLinkPair.Create();
                    server.Accept(pair.First, new() { Principal = new() { ["kind"] = "user", ["id"] = "barrier" } }, ChordContext.Background);
                    events.Opened(); return pair.Second;
                },
                ServerBase = _ => Task.FromResult<string?>("base"), Outdated = () => { }, OnError = _ => { },
            });
            try
            {
                // StartAsync already joined the initial update: the predecessor is completed. The first
                // fetch then holds the external call while a dispatcher callback tries to enqueue.
                var external = Task.Run(() => client.UpdateAsync(next));
                await fetchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                client.Host.Dispatcher.Post(() =>
                {
                    dispatcherEntered.TrySetResult();
                    try { dispatcherQueued.TrySetResult(client.UpdateAsync(next)); }
                    catch (Exception error) { dispatcherQueued.TrySetException(error); }
                });
                await dispatcherEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                releaseFetch.Set();
                var dispatched = await dispatcherQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.WhenAll(external, dispatched).WaitAsync(TimeSpan.FromSeconds(5));
                Equal(1, fetches, "queued update observes predecessor's accepted digest");
            }
            finally
            {
                releaseFetch.Set();
                await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }),
        Case("surfaces.preferences.active-followers-read-initial-picks-and-follow-changes", async () =>
        {
            await using var store = await MemoryStore();
            await using var server = Hosts.Create(new() { Address = Scripts.Server.Address(), Principal = new() { ["kind"] = "server" }, Store = store, OnError = _ => { } });
            var serverLoad = await server.LoadAsync([new("theme-choice", ThemeChoice.Facet()), new("locale-choice", LocaleChoice.Facet())], ChordContext.Background);
            Equal(0, serverLoad.Failed.Count, "server preferences loaded");
            await using var client = Hosts.Create(new() { OnError = _ => { } });
            var text = SurfaceMessages.Define("greeting", new Dictionary<string, Catalog>
            { ["en"] = new() { ["hello"] = "Hello" }, ["de"] = new() { ["hello"] = "Hallo" } });
            var firstTheme = Themes.FallbackThemes[ThemeMode.Dark] with { Id = "default" };
            var pickedTheme = firstTheme with { Id = "picked" };
            var clientLoad = await client.LoadAsync([
                new("theme-kit", Themes.Facet()), new("language-kit", SurfaceTranslations.Facet()),
                new("a-default", Facet.Define((_, self) => self.Contribute(Themes.Registry, firstTheme))),
                new("z-picked", Facet.Define((_, self) => { self.Contribute(Themes.Registry, pickedTheme); self.Contribute(SurfaceTranslations.Registry, text.Translations); })),
            ], ChordContext.Background);
            Equal(0, clientLoad.Failed.Count, "client registries loaded");
            var pair = MemoryLinkPair.Create();
            server.Accept(pair.First, new() { Principal = new() { ["kind"] = "user", ["id"] = "picker" } }, ChordContext.Background);
            client.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
            using var themes = await client.GetAsync(ThemeChoice.Contract, new(), ChordContext.Background);
            using var locales = await client.GetAsync(LocaleChoice.Contract, new(), ChordContext.Background);
            await themes.CallAsync("choose", new JsonObject { ["dark"] = "picked", ["light"] = null }, ChordContext.Background);
            await locales.CallAsync("choose", JsonValue.Create("de"), ChordContext.Background);
            var initialTheme = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var initialLanguage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changedTheme = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changedLanguage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var changing = 0;
            using var themeFollower = await ActiveTheme.FollowAsync(client, ThemeMode.Dark, theme =>
            {
                if (theme.Id == "picked") initialTheme.TrySetResult();
                if (Volatile.Read(ref changing) != 0 && theme.Id == "default") changedTheme.TrySetResult();
            }, ChordContext.Background);
            using var languageFollower = await ActiveLanguage.FollowAsync(client, ["en"], language =>
            {
                if (language.Locale == "de") initialLanguage.TrySetResult();
                if (Volatile.Read(ref changing) != 0 && language.Locale == "en") changedLanguage.TrySetResult();
            }, _ => { }, ChordContext.Background);
            await Task.WhenAll(initialTheme.Task, initialLanguage.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Equal("Hallo", text.In(ActiveLanguage.HostLanguage(client).Read()).Text("hello"), "initial chosen locale used by host messages");
            Volatile.Write(ref changing, 1);
            await themes.CallAsync("choose", new JsonObject { ["dark"] = "default", ["light"] = null }, ChordContext.Background);
            await locales.CallAsync("choose", JsonValue.Create("en"), ChordContext.Background);
            await Task.WhenAll(changedTheme.Task, changedLanguage.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Equal("Hello", text.In(ActiveLanguage.HostLanguage(client).Read()).Text("hello"), "live chosen locale used by host messages");
        }),
        Case("surfaces.preferences.persist-canonical-principals-and-live-choice", async () =>
        {
            await using var store = await MemoryStore();
            await using var server = Hosts.Create(new() { Address = Scripts.Server.Address(), Principal = new() { ["kind"] = "server" }, Store = store, OnError = _ => { } });
            var loaded = await server.LoadAsync([new("theme-choice", ThemeChoice.Facet()), new("locale-choice", LocaleChoice.Facet())], ChordContext.Background);
            Equal(0, loaded.Failed.Count, "server preference facets loaded");
            await using var alice = Hosts.Create(new() { OnError = _ => { } });
            await using var sameAlice = Hosts.Create(new() { OnError = _ => { } });
            await using var bob = Hosts.Create(new() { OnError = _ => { } });
            foreach (var host in new[] { alice, sameAlice, bob }) await host.LoadAsync([], ChordContext.Background);
            void Connect(ChordHost host, JsonObject principal)
            {
                var pair = MemoryLinkPair.Create(); server.Accept(pair.First, new() { Principal = principal }, ChordContext.Background);
                host.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
            }
            Connect(alice, new() { ["kind"] = "user", ["id"] = "alice" });
            Connect(sameAlice, new() { ["id"] = "alice", ["kind"] = "user" });
            Connect(bob, new() { ["kind"] = "user", ["id"] = "bob" });
            using var first = await alice.GetAsync(ThemeChoice.Contract, new(), ChordContext.Background);
            using var second = await sameAlice.GetAsync(ThemeChoice.Contract, new(), ChordContext.Background);
            using var other = await bob.GetAsync(ThemeChoice.Contract, new(), ChordContext.Background);
            var watched = await second.Member("pick").WatchAsync(ChordContext.Background);
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            watched.Start((call, _) =>
            {
                if (call is WatchValueCall<JsonNode?> value && value.Value?["dark"]?.GetValue<string>() == "my-theme") changed.TrySetResult();
                return Task.CompletedTask;
            });
            await first.CallAsync("choose", new JsonObject { ["dark"] = "my-theme", ["light"] = null }, ChordContext.Background);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal("my-theme", second.Member("pick").Value?["dark"]?.GetValue<string>(), "canonical principal shares choice");
            Check(other.Member("pick").Value?["dark"] is null, "other principal isolated");
            await server.ReloadAsync([new("theme-choice", ThemeChoice.Facet()), new("locale-choice", LocaleChoice.Facet())], ChordContext.Background);
            using var locale = await alice.GetAsync(LocaleChoice.Contract, new(), ChordContext.Background);
            await locale.CallAsync("choose", JsonValue.Create("de-CH"), ChordContext.Background);
            Equal("de-CH", locale.Member("pick").Value?.GetValue<string>(), "locale stored");
            using var refreshed = await alice.GetAsync(ThemeChoice.Contract, new(), ChordContext.Background);
            Equal("my-theme", refreshed.Member("pick").Value?["dark"]?.GetValue<string>(), "choice survives facet reload");
            await watched.StopAsync();
        }),
        Case("surfaces.client.keeps-good-code-and-retries-newest-on-reconnect", async () =>
        {
            var initial = new ScriptSet("base", "/combined.js", [new("plugin", "one", "/one.js")]);
            await using var server = Hosts.Create(new() { Address = Scripts.Server.Address(), Principal = new() { ["kind"] = "server" }, OnError = _ => { } });
            var scriptsDefinition = StateDefinition.Define("client.scripts", new() { Version = 1, Schema = Scripts.Schema, Initial = initial.ToJson });
            ISingletonHandle? scriptState = null;
            var loaded = await server.LoadAsync([new("scripts", Facet.Define((facet, _) => facet.Serve(Scripts.WebScripts, (_, _, self) =>
            {
                scriptState = self.State(scriptsDefinition);
                return new(new Dictionary<string, object?> { ["set"] = scriptState }, "main");
            })))], ChordContext.Background);
            Equal(0, loaded.Failed.Count, "server script facet loaded");
            var type = Contract.Local("surface.value", new() { Version = 1, Params = new(), Members = new() { ["value"] = Schemas.Int() } });
            var bad = true; var fetched = 0; var links = new List<ILink>(); var errors = new List<ClientError>();
            RegisteredScript Script(string digest, int value) => new("plugin", digest, (_, module) =>
            {
                if (digest == "two" && bad) throw new InvalidOperationException("new plugin failed");
                module.Exports = Facet.Define((facet, _) => facet.Serve(type, (_, _, _) =>
                    new(new Dictionary<string, object?> { ["value"] = PiSharp.Chord.State.Watchables.Constant(JsonValue.Create(value)) })));
            });
            var retryScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var environment = new SurfaceClock { Scheduled = delay => { if (delay == 250) retryScheduled.TrySetResult(); } };
            await using var client = await Client.StartAsync(new()
            {
                Scripts = Scripts.WebScripts, Base = "base", Initial = [Script("one", 1)], Environment = environment,
                FetchScript = (entry, _) => { fetched++; return Task.FromResult(Script(entry.Digest, 2)); },
                Open = events =>
                {
                    var pair = MemoryLinkPair.Create(); links.Add(pair.Second);
                    server.Accept(pair.First, new() { Principal = new() { ["kind"] = "user", ["id"] = "test" } }, ChordContext.Background);
                    events.Opened(); return pair.Second;
                },
                ServerBase = _ => Task.FromResult<string?>("base"), Outdated = () => throw new Exception("unexpected outdated"), OnError = errors.Add,
            });
            using var value = await client.Host.GetAsync(type, new(), ChordContext.Background);
            Equal(1, value.Member("value").Value!.GetValue<int>(), "initial native plugin loaded");
            var update = new ScriptSet("base", "/combined-two.js", [new("plugin", "two", "/two.js")]);
            // The authoritative server set and the direct update agree, so reconnect never sees an older set.
            await scriptState!.ChangeAsync(draft =>
            { draft["base"] = update.Base; draft["combined"] = update.Combined; draft["plugins"] = update.ToJson()["plugins"]!.DeepClone(); }, ChordContext.Background);
            await client.UpdateAsync(update);
            Equal(1, value.Member("value").Value!.GetValue<int>(), "failed newest retains old code");
            bad = false; var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var watch = await value.Member("value").WatchAsync(ChordContext.Background);
            watch.Start((call, _) => { if (call is WatchValueCall<JsonNode?> next && next.Value?.GetValue<int>() == 2) accepted.TrySetResult(); return Task.CompletedTask; });
            links[^1].Close();
            // Flush the host's close notification before virtual time admits the new parent link.
            client.Host.Dispatcher.Invoke(() => { });
            await retryScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            environment.Advance(250);
            await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(fetched >= 2, "reconnect fetched failed newest again"); Equal(2, value.Member("value").Value!.GetValue<int>(), "newest finally runs");
            Check(errors.Any(error => error.Kind == "plugins"), "plugin failure was reported"); await watch.StopAsync();
            await client.StopAsync();
        }),
    ];
}
