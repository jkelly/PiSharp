// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/host.test.ts ("bind: remote references sharing one wire
// hold"), object.test.ts ("calls for a reference"), reference.test.ts ("a binding moved by a retry alone"), route.test.ts ("routes and
// references sharing one wire hold"), packages/chord/test/remote/hold.test.ts (the two shared-hold cases). Upstream 47e9d74f9 and
// 90ed3e3b1. Ported by reading, against real hosts and raw protocol peers. Cases named 160-* are authored for the native port.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Remote;
using PiSharp.Chord.State;
using static PiSharp.Chord.Contracts.Schemas;
using static ChordSupport;
using StateEntry = PiSharp.Chord.Link.StateEntry;

internal static partial class Program
{
    /// <summary>How many holds <paramref name="peer"/> got since the last count (taken out).</summary>
    private static async Task<int> G6HoldsAt(S6Peer peer, ChordHost host)
    {
        await S6Settled(host);
        var count = 0;
        while (peer.All.OfType<HoldMessage>().Any()) { await peer.Next<HoldMessage>(); count++; }
        return count;
    }

    /// <summary>A server routing sessions to real workers it makes (each serving the tag waiter, or what <paramref name="worker"/>
    /// declares), through <paramref name="resolve"/> when given (it gets what makes the worker's link).</summary>
    private static async Task<(ChordHost Server, RouteHandle Route, Dictionary<string, ChordHost> Workers)> G6Routed(
        Func<ChordHost, Address, Func<Task<ResolvedLink>>, Task<ResolvedLink>>? resolve = null, Action<IFacetHost>? worker = null)
    {
        var workers = new Dictionary<string, ChordHost>(StringComparer.Ordinal); var server = S6Host(Server.Address());
        async Task<ResolvedLink> Made(Address address)
        {
            var made = S6Host(address, principal: new() { ["kind"] = "worker" });
            await made.LoadAsync([new("@test/worker", Facet.Define((h, _) => { if (worker is null) H6ServeWaiter(h, H6Waiter, []); else worker(h); }))], S6Ctx);
            var pair = MemoryLinkPair.Create(); made.Connect(pair.Second, new() { Principal = S6ServerPrincipal }, S6Ctx);
            lock (workers) workers[address.Key] = made;
            return new ResolvedLink(pair.First, new() { Principal = new() { ["kind"] = "worker" } });
        }
        var route = server.Route(Session, new()
        {
            Authorize = (_, _) => true,
            Resolve = (address, _) => resolve is null ? Made(address) : resolve(server, address, () => Made(address)),
        }, S6Ctx);
        await server.LoadAsync([], S6Ctx);
        return (server, route, workers);
    }
    private static async Task G6Close(ChordHost server, Dictionary<string, ChordHost> workers)
    {
        await server.CloseAsync(S6Ctx);
        ChordHost[] all; lock (workers) all = [.. workers.Values];
        foreach (var worker in all) await worker.CloseAsync(S6Ctx);
    }
    private static string G6WorkerKey => Session.Address(("id", JsonValue.Create("S1"))).Key;

    private static readonly StateDefinition G6N = StateDefinition.Define("shown-n", new() { Version = 1, Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 } });
    /// <summary>A remote object on the server: a label, a counter and bump.</summary>
    private static readonly Contract G6Shown = Contract.Remote("app.shown", new()
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(),
        Variants = new() { ["v"] = new() { State = { ["label"] = Str(), ["n"] = Int() }, Methods = { ["bump"] = Method(null, Int()) } } },
    });
    /// <summary>The shown object v2: its label became an object; v1 consumers map back through accepts.</summary>
    private static readonly Contract G6ShownV2 = Contract.Evolve(G6Shown, e => e.State("v", "label", Obj(("text", Str()))));
    private static PluginFacet G6ServeShown(string label, bool native = false, Task? gate = null) => new("@test/served", Facet.Define((h, _) => h.Serve(G6Shown, (_, _, self) =>
    {
        var doc = self.State(G6N);
        if (native) self.Open(_ => Task.FromResult(label), (_, _, _) => Task.CompletedTask);
        return new(new Dictionary<string, object?>
        {
            ["label"] = Watchables.Constant(JsonValue.Create(label)),
            ["n"] = self.Derive([doc], values => ((JsonNode)values[0]!)["n"]!.DeepClone()),
            ["bump"] = (HostMethod)(async (_, ctx) =>
            {
                if (gate is not null) await gate;
                await doc.ChangeAsync(draft => draft["n"] = draft["n"]!.GetValue<int>() + 1, ctx.Context);
                return JsonValue.Create(doc.Value!["n"]!.GetValue<int>());
            }),
        }, "v");
    })));
    private static PluginFacet G6ServeShownV2(string text, bool accept) => new("@test/served", Facet.Define((h, _) => h.Serve(G6ShownV2, (_, _, self) =>
    {
        var doc = self.State(G6N);
        return new(new Dictionary<string, object?>
        {
            ["label"] = Watchables.Constant(new JsonObject { ["text"] = text }),
            ["n"] = self.Derive([doc], values => ((JsonNode)values[0]!)["n"]!.DeepClone()),
            ["bump"] = (HostMethod)(async (_, ctx) =>
            {
                await doc.ChangeAsync(draft => draft["n"] = draft["n"]!.GetValue<int>() + 1, ctx.Context);
                return JsonValue.Create(doc.Value!["n"]!.GetValue<int>());
            }),
        }, "v");
    }, accept ? new(new AcceptsTable { [1] = new() { State = { ["v"] = new StateSection { ["label"] = new StateMapping(value => ((JsonNode)value!)["text"]!.DeepClone()) } } } }) : null)));

    private static IEnumerable<(string, Func<Task>)> HostSync160SharedHoldCases() =>
    [
        // --- host.test.ts: bind ---
        Case("host.bind.shared-one-hold-per-type-version-fingerprint-params-and-placed-address-escaped-parts-never-collide", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            static Contract Keyed(string member, Func<Address> address) => Contract.Remote("app.keyed", new()
            {
                Version = 1, Params = new() { ["a"] = Str(), ["b"] = Str() }, HostAddress = _ => address(),
                Variants = new() { ["v"] = new() { State = { [member] = Int() } } },
            });
            var keyed = Keyed("n", () => Server.Address());
            JsonObject Params(string a = "x", string b = "y") => new() { ["a"] = a, ["b"] = b };
            client.Hold(keyed, Params(), S6Ctx); client.Hold(keyed, Params(), S6Ctx);
            Equal(1, await G6HoldsAt(peer, client), "equal params share one hold");
            // Another version, another fingerprint, another address (HostAddress is not in the fingerprint): one each.
            client.Hold(Contract.Evolve(keyed, e => e.AddState("v", "extra", Int())), Params(), S6Ctx);
            client.Hold(Keyed("m", () => Server.Address()), Params(), S6Ctx);
            var elsewhere = Keyed("n", () => Session.Address(("id", JsonValue.Create("elsewhere"))));
            Equal(keyed.Fingerprint, elsewhere.Fingerprint, "the address is not in the fingerprint");
            client.Hold(elsewhere, Params(), S6Ctx);
            Equal(3, await G6HoldsAt(peer, client), "one hold each");
            // Separators, backslashes and NUL moved between params: all apart.
            var split = new[] { ("x|", "y"), ("x", "|y"), ("x\\", "y"), ("x", "\\y"), ("x\u0000", "y"), ("x", "\u0000y") };
            foreach (var (a, b) in split) client.Hold(keyed, Params(a, b), S6Ctx);
            Equal(split.Length, await G6HoldsAt(peer, client), "escaped parts never collide");
        }),
        Case("host.bind.shared-one-hold-per-caller-equal-principal-json-shares-another-field-order-another-principal-or-none-holds-anew", async () =>
        {
            var (client, peer) = await H6RawProvider(); await using var closing = client;
            var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var holding = new List<Task>();
            void As(JsonObject? principal) => holding.Add(client.RootAsync<object?>(async ctx =>
            {
                _ = ctx.GetAsync(Notes, H6Session()).ContinueWith(_ => { });
                await open.Task; return null;
            }, S6Ctx, new() { Principal = principal }));
            As(new() { ["kind"] = "guest", ["session"] = "S1" }); As(new() { ["kind"] = "guest", ["session"] = "S1" });
            Equal(1, await G6HoldsAt(peer, client), "equal principal JSON shares");
            // Field order is part of the principal's JSON as the wire carries it.
            As(new() { ["session"] = "S1", ["kind"] = "guest" }); As(new() { ["kind"] = "guest", ["session"] = "S2" }); As(null);
            Equal(3, await G6HoldsAt(peer, client), "another field order, another principal and none each hold anew");
            open.TrySetResult(); await Task.WhenAll(holding);
        }),
        Case("host.bind.shared-one-hold-per-callers-incarnation-ctx-get-for-two-calls-of-one-tab-shares-for-another-tabs-holds-anew", async () =>
        {
            var opener = Contract.Remote("app.opener", new()
            {
                Version = 1, Params = new(), HostAddress = _ => Server.Address(),
                Variants = new() { ["v"] = new() { Methods = { ["open"] = Method(null, Int()) } } },
            });
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (tab, server, workers, _) = await E6Relayed(worker: h => H6ServeWaiter(h, H6Waiter, []), server: h => h.Serve(opener, (_, _, _) => new(new Dictionary<string, object?>
            {
                ["open"] = (HostMethod)(async (_, ctx) => { await ctx.GetAsync(H6Waiter, H6Session()); await gate.Task; return JsonValue.Create(1); }),
            }, "v")));
            var other = S6Host(principal: new() { ["kind"] = "owner" }); await other.LoadAsync([], S6Ctx);
            S6Link(server, other, new() { ["kind"] = "owner" });
            using var first = await tab.GetAsync(opener, new(), S6Ctx); using var another = await other.GetAsync(opener, new(), S6Ctx);
            var calls = new List<Task<object?>> { first.CallAsync("open", null, S6Ctx), first.CallAsync("open", null, S6Ctx) };
            await S6Until(() => { lock (workers) return workers.Count == 1 && workers[0].Activity.Value.References == 1; }, "the worker's hold"); await S6Settled(tab, server);
            Equal(1, workers[0].Activity.Value.References, "two calls of one tab share one hold");
            calls.Add(another.CallAsync("open", null, S6Ctx));
            await S6Until(() => workers[0].Activity.Value.References == 2, "another tab's hold");
            gate.TrySetResult();
            Sequence([1, 1, 1], (await Task.WhenAll(calls)).Select(value => ((JsonNode)value!).GetValue<int>()), "every call answered");
            await tab.CloseAsync(S6Ctx); await other.CloseAsync(S6Ctx); await server.CloseAsync(S6Ctx); await workers[0].CloseAsync(S6Ctx);
        }),
        Case("host.bind.shared-self-get-references-hold-on-their-own-while-activating-once-live-and-on-a-re-hold", async () =>
        {
            var user = Contract.Local("app.notes-user", new() { Version = 1, Params = new(), Members = new() { ["count"] = Int() } });
            await using var client = S6Host();
            await client.LoadAsync([new("@test/user", Facet.Define((h, _) => h.Serve(user, (_, _, self) =>
            {
                var notes = self.Get(Notes, H6Session());
                return new(new Dictionary<string, object?> { ["count"] = self.Derive([notes.Member("count")], values => ((JsonNode?)values[0])?.DeepClone()) });
            })))], S6Ctx);
            var peer = H6RawParent(client);
            using var shared = client.Hold(Notes, H6Session(), S6Ctx); using var held = client.Hold(user, new(), S6Ctx);
            var holds = new[] { await peer.Next<HoldMessage>(), await peer.Next<HoldMessage>() };
            foreach (var hold in holds) H6BindHold(peer, hold.Hold);
            await S6Live(shared); await S6Live(held);
            // A reference made now joins the host's own hold, not the dependent's.
            using var joined = client.Hold(Notes, H6Session(), S6Ctx);
            Equal(0, await G6HoldsAt(peer, client), "a join makes no hold");
            peer.Dispose(); await S6Until(() => shared.State.Value.Kind == "loading", "the drop");
            var next = H6RawParent(client);
            await S6Until(() => next.Count<HoldMessage>() == 2, "the re-holds");
            Equal(2, await G6HoldsAt(next, client), "the host's own hold and the dependent's, each re-held");
        }),
        Case("host.bind.shared-a-relay-forwards-two-downstream-holds-of-the-same-key-as-two-upstream-holds", async () =>
        {
            var (tab, server, workers, _) = await E6Relayed(worker: h => H6ServeWaiter(h, H6Waiter, []));
            var other = S6Host(principal: new() { ["kind"] = "owner" }); await other.LoadAsync([], S6Ctx);
            S6Link(server, other, new() { ["kind"] = "owner" });
            using var a = await tab.GetAsync(H6Waiter, H6Session(), S6Ctx); using var b = await tab.GetAsync(H6Waiter, H6Session(), S6Ctx);
            using var c = await other.GetAsync(H6Waiter, H6Session(), S6Ctx);
            await S6Settled(tab, other, server);
            Equal(2, workers[0].Activity.Value.References, "the tab's two references share one hold; the other tab's is another");
            await tab.CloseAsync(S6Ctx); await other.CloseAsync(S6Ctx); await server.CloseAsync(S6Ctx); await workers[0].CloseAsync(S6Ctx);
        }),
        Case("host.bind.shared-revocation-ends-every-reference-sharing-a-hold-and-the-next-one-holds-anew", async () =>
        {
            var (client, peer) = await H6RawProvider(revokedBy: principal => ChordJson.AsString(principal["kind"]) == "guest" ? [principal["session"]?.DeepClone()] : []);
            await using var closing = client;
            var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var ended = new List<string>();
            Task Scope(string name) => client.RootAsync<object?>(async ctx =>
            {
                var notes = await ctx.GetAsync(Notes, H6Session());
                _ = Task.Run(async () => { await (await notes.State.WatchAsync(S6Ctx)).Closed; lock (ended) ended.Add(name); });
                await open.Task; return null;
            }, S6Ctx, new() { Principal = new() { ["kind"] = "guest", ["session"] = "S1" } }).ContinueWith(_ => { });
            var scopes = new List<Task> { Scope("a") };
            H6BindHold(peer, (await peer.Next<HoldMessage>()).Hold);
            scopes.Add(Scope("b"));
            Equal(0, await G6HoldsAt(peer, client), "the second scope joined");
            client.Revoke(JsonValue.Create("S1"), S6Ctx);
            await S6Until(() => { lock (ended) return ended.Count == 2; }, "both references ended");
            lock (ended) Sequence(["a", "b"], ended.Order(StringComparer.Ordinal), "every reference sharing the hold");
            await S6Settled(client);
            Equal(1, peer.Count<ReleaseMessage>(), "one release");
            _ = Scope("c");
            Equal(1, await G6HoldsAt(peer, client), "the next one holds anew");
            open.TrySetResult(); await Task.WhenAll(scopes);
        }),

        // --- object.test.ts, reference.test.ts ---
        Case("host.object.calls-for-a-reference-run-as-the-references-bindings-caller-a-local-object-keeps-no-replica-counts", async () =>
        {
            var who = Contract.Local("app.who", new() { Version = 1, Params = new(), Members = new() { ["who"] = Method(Str(), Str()) } });
            await using var host = S6Host(principal: new() { ["kind"] = "owner" });
            await host.LoadAsync([new("@test/who", Facet.Define((h, _) => h.Serve(who, (_, _, _) => new(new Dictionary<string, object?>
            {
                ["who"] = (HostMethod)((_, ctx) => Task.FromResult<object?>(JsonValue.Create(ChordJson.AsString(ctx.Principal?["kind"]) ?? "none"))),
            }))))], S6Ctx);
            using var reference = await host.GetAsync(who, new(), S6Ctx);
            Equal("owner", ((JsonNode)(await reference.CallAsync("who", JsonValue.Create("?"), S6Ctx))!).GetValue<string>(), "the binding's caller");
            ITarget local = host.Dispatcher.Invoke(() => host.Objects.Values.Single(obj => obj.Label == "app.who"));
            Equal(null, local.GenerationOf("who"), "members on this host are no replicas");
        }),
        Case("host.reference.a-binding-moved-by-a-retry-alone-keeps-its-reference-member-handles-state-watch-and-member-watch-the-sibling-stays-failed", async () =>
        {
            var (client, peer) = await H6RawProvider(new(64)); await using var closing = client;
            var a = client.Hold(Notes, H6Session(), S6Ctx); var b = client.Hold(Notes, H6Session(), S6Ctx);
            var hold = await H6BindNext(peer); await S6Live(a);
            var member = a.Member("count"); var states = new List<string>();
            (await a.State.WatchAsync(S6Ctx)).Start((call, _) => { if (call is NativeWatchValueCall<RefState> value) lock (states) states.Add(value.Value.Kind); return Task.CompletedTask; });
            var counts = ChangesOf(await member.WatchAsync(S6Ctx));
            peer.Send(new StateMessage([new StateEntry(hold, [new StateMember(WireNames(Notes, "editor").State["notes"],
                new Part.Text("[[\"r\",[{\"text\":\"" + new string('x', 100) + "\"}]]]"))], [], [])]));
            await S6Until(() => a.State.Value.Kind == "failed", "the too_large failure");
            a.Retry();
            var own = (await peer.Next<HoldMessage>()).Hold; H6BindHold(peer, own, "[{\"text\":\"a\"}]");
            await S6Live(a); await S6Until(() => counts.Count == 2 && states.Count == 3, "the private hold's value and states");
            Check(ReferenceEquals(member, a.Member("count")), "its member handle is the same");
            Equal(1, H6Int(a), "with the private hold's value"); Equal("failed", b.State.Value.Kind, "the sibling it left stays failed");
            lock (states) Sequence(["failed", "loading", "live"], states, "its state watch followed it");
            Check(counts[0] is WatchFailedCall<JsonNode?> && counts[1] is WatchValueCall<JsonNode?> { Value: var count } && count!.GetValue<int>() == 1, "its member watch: the failure, then the new value");
            a.Release(); b.Release();
        }),

        // --- route.test.ts: routes and references sharing one wire hold ---
        Case("host.route.shared-a-reference-joining-a-hold-whose-resolve-failed-resolves-the-address-again-both-live-on-one-hold", async () =>
        {
            var fail = true; var resolves = 0;
            var (server, _, workers) = await G6Routed((_, _, made) =>
            {
                Interlocked.Increment(ref resolves);
                return fail ? Task.FromException<ResolvedLink>(new InvalidOperationException("no worker")) : made();
            });
            var a = server.Hold(H6Waiter, H6Session(), S6Ctx);
            await S6Until(() => a.State.Value.Kind == "failed", "the failed resolve");
            Equal(FailureKind.Waiting, a.State.Value.Cause!.Kind, "failed waiting");
            fail = false;
            using var b = await server.GetAsync(H6Waiter, H6Session(), S6Ctx);
            await S6Live(a);
            Sequence(["live", "live"], new[] { a.State.Value.Kind, b.State.Value.Kind }, "both live"); Equal(2, resolves, "the join resolved the address again");
            Equal(1, workers[G6WorkerKey].Activity.Value.References, "on one hold");
            await G6Close(server, workers);
        }),
        Case("host.route.shared-a-suspended-address-a-held-reference-stays-loading-a-new-one-holds-on-its-own-and-fails-unavailable", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var (server, route, workers) = await G6Routed(async (_, _, made) => { await gate.Task; return await made(); });
            var a = server.Hold(H6Waiter, H6Session(), S6Ctx);
            await S6Settled(server);
            route.Suspend(Session.Address(("id", JsonValue.Create("S1"))));
            var b = server.Hold(H6Waiter, H6Session(), S6Ctx);
            Equal("loading", a.State.Value.Kind, "the held reference stays loading");
            Check(b.State.Value is { Kind: "failed", Cause: { Kind: FailureKind.Waiting, Error.Code: "unavailable" } } && b.State.Value.Cause!.Error.Message.Contains("suspended", StringComparison.Ordinal),
                "the new one holds on its own and fails unavailable: " + b.State.Value.Cause?.Error.Message);
            gate.TrySetResult();
            // The running resolve still links the address: both go live, each on its own hold.
            await S6Live(a); await S6Live(b);
            await S6Until(() => workers[G6WorkerKey].Activity.Value.References == 2, "two holds at the worker");
            await G6Close(server, workers);
        }),
        Case("host.route.shared-a-resolver-re-entering-for-the-same-key-finds-the-stored-object-one-hold-its-own-reference-released-there-is-not-the-last", async () =>
        {
            var inner = new List<IReference>();
            var (server, _, workers) = await G6Routed((host, _, made) =>
            {
                var joined = host.Hold(H6Waiter, H6Session(), S6Ctx);
                lock (inner) { inner.Add(joined); if (inner.Count == 1) joined.Release(); }
                return made();
            });
            using var a = await server.GetAsync(H6Waiter, H6Session(), S6Ctx);
            await S6Settled(server);
            Sequence(["live", "ended"], new[] { a.State.Value.Kind, inner[0].State.Value.Kind }, "the resolver's own reference released there was not the last");
            Equal(1, workers[G6WorkerKey].Activity.Value.References, "one hold");
            await G6Close(server, workers);
        }),
        Case("host.route.shared-a-resolver-that-fails-after-re-entering-fails-both-references-waiting-the-next-request-resolves-again", async () =>
        {
            var inner = new List<IReference>(); var fail = true;
            var (server, _, workers) = await G6Routed((host, _, made) =>
            {
                lock (inner) if (inner.Count == 0) inner.Add(host.Hold(H6Waiter, H6Session(), S6Ctx));
                return fail ? Task.FromException<ResolvedLink>(new InvalidOperationException("no worker")) : made();
            });
            var a = server.Hold(H6Waiter, H6Session(), S6Ctx);
            await S6Until(() => a.State.Value.Kind == "failed" && inner.Count == 1 && inner[0].State.Value.Kind == "failed", "both failed");
            foreach (var reference in new[] { a, inner[0] })
                Check(reference.State.Value is { Cause.Kind: FailureKind.Waiting } && reference.State.Value.Cause!.Error.Message.Contains("no worker", StringComparison.Ordinal), "failed waiting with the resolver's error");
            fail = false;
            using var next = await server.GetAsync(H6Waiter, H6Session(), S6Ctx);
            await S6Live(a); await S6Live(inner[0]);
            Equal(1, workers[G6WorkerKey].Activity.Value.References, "one hold for all three");
            await G6Close(server, workers);
        }),
        Case("host.route.shared-too-large-part-a1-retrying-alone-re-enters-the-resolver-which-finds-cs-new-entry-a2-and-c-release-in-any-order", async () =>
        {
            var peers = new List<S6Peer>(); var reentered = new List<IReference>(); var reenter = false; var fail = false;
            await using var server = S6Host(Server.Address());
            server.Route(Session, new()
            {
                Authorize = (_, _) => true,
                Resolve = (_, _) =>
                {
                    if (reenter) lock (reentered) reentered.Add(server.Hold(PlacedNotes, H6Session(), S6Ctx));
                    if (fail) return Task.FromException<ResolvedLink>(new InvalidOperationException("no worker"));
                    var pair = MemoryLinkPair.Create(); var peer = new S6Peer(pair.Second, autoAck: true);
                    lock (peers) peers.Add(peer);
                    return Task.FromResult(new ResolvedLink(pair.First, new() { Principal = new() { ["kind"] = "worker" }, Limits = new(64) }));
                },
            }, S6Ctx);
            await server.LoadAsync([], S6Ctx);
            var a1 = server.Hold(PlacedNotes, H6Session(), S6Ctx); var a2 = server.Hold(PlacedNotes, H6Session(), S6Ctx);
            var peer = await W6WorkerAt(peers); var held = await peer.Next<HoldMessage>(); W6BindPlaced(peer, held);
            await S6Live(a2);
            peer.Send(new StateMessage([new StateEntry(held.Hold, [new StateMember(WireNames(PlacedNotes, "editor").State["notes"],
                new Part.Text("[[\"r\",\"" + new string('x', 100) + "\"]]"))], [], [])]));
            await S6Until(() => a1.State.Value.Kind == "failed" && a2.State.Value.Kind == "failed", "both failed");
            Check(new[] { a1, a2 }.All(reference => reference.State.Value is { Cause: { Kind: FailureKind.Broken, Error.Code: "too_large" } }), "broken, too_large");
            var c = server.Hold(PlacedNotes, H6Session(), S6Ctx);
            var owned = await peer.Next<HoldMessage>(); W6BindPlaced(peer, owned); await S6Live(c);
            // The link drops and the address fails to resolve: C waits unlinked, so A1's placement resolves it again.
            fail = true; peer.Dispose();
            await S6Until(() => c.State.Value is { Kind: "failed", Cause.Kind: FailureKind.Waiting }, "C failed waiting");
            fail = false; reenter = true;
            a1.Retry();
            var next = await W6WorkerAt(peers, 1);
            var holds = new[] { await next.Next<HoldMessage>(), await next.Next<HoldMessage>() };
            reenter = false;
            foreach (var each in holds) W6BindPlaced(next, each);
            await S6Live(a1); await S6Live(c); await S6Live(reentered[0]);
            Sequence(["live", "failed", "live", "live"], new[] { a1.State.Value.Kind, a2.State.Value.Kind, c.State.Value.Kind, reentered[0].State.Value.Kind },
                "A1 on its private hold, A2 still failed, C re-held with the re-entered reference joined");
            // Two holds on the new link: C's re-hold (the re-entered reference joined it) and A1's private one.
            a2.Release(); c.Release();
            await S6Settled(server);
            Equal(0, next.Count<ReleaseMessage>(), "nothing released yet");
            reentered[0].Release(); a1.Release();
            await S6Until(() => next.Count<ReleaseMessage>() == 2, "both holds released");
        }),

        // --- hold.test.ts: shared holds across reloads ---
        Case("remote.hold.shared-a-reference-made-during-a-version-dropping-reloads-drain-joins-the-old-live-binding-no-fresh-admission", async () =>
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Upstream's linked() leaves closeTimeout unset here, so the drain has the default deadline (5 s). S6Host's 200 ms close
            // would cap the drain at 200 ms, less than a slow runner needs for the third host below: the drain would time out and
            // cancel the running call before the gate opens.
            await using var server = S6Host(Server.Address(), closeTimeout: TimeSpan.FromSeconds(30)); await using var client = S6Host();
            await server.LoadAsync([G6ServeShown("v1", gate: gate.Task)], S6Ctx); await client.LoadAsync([], S6Ctx);
            S6Link(server, client);
            using var a = await client.GetAsync(G6Shown, new(), S6Ctx);
            // A running call keeps the old attempt open: it drains after the reload commits.
            var running = a.CallAsync("bump", null, S6Ctx);
            await S6Until(() => server.Activity.Value.Calls == 1, "the call running");
            await server.ReloadAsync([G6ServeShownV2("v2", false)], S6Ctx);
            await S6Settled(server, client);
            Check(a.State.Value.Kind == "live", $"the old binding stays live while it drains: {a.State.Value.Kind} {a.State.Value.Cause?.Kind} " +
                $"{a.State.Value.Cause?.Error.Code}: {a.State.Value.Cause?.Error.Message}");
            using var b = client.Hold(G6Shown, new(), S6Ctx);
            Sequence(["live", "\"v1\""], new[] { b.State.Value.Kind, b.Member("label").Value!.ToJsonString() }, "the joiner shares the old live binding");
            Equal(1, server.Activity.Value.References, "with no hold of its own");
            // A separate consumer's new hold is admitted against the new code: v1 is no longer accepted.
            await using var other = S6Host(); await other.LoadAsync([], S6Ctx); S6Link(server, other);
            using var fresh = other.Hold(G6Shown, new(), S6Ctx);
            await S6Until(() => fresh.State.Value.Kind == "failed", "the fresh hold refused");
            Equal("contract_mismatch", fresh.State.Value.Cause!.Error.Code, "v1 is no longer accepted");
            gate.TrySetResult();
            Equal(1, ((JsonNode)(await running)!).GetValue<int>(), "the running call drains on the old code");
            // After the drain both references leave the old binding together.
            await S6Until(() => a.State.Value.Kind == "failed" && b.State.Value.Kind == "failed", "both leave the old binding");
            Check(new[] { a, b }.All(reference => reference.State.Value.Cause!.Error.Code == "contract_mismatch"), "both contract_mismatch");
        }),
        Case("remote.hold.shared-two-references-sharing-one-hold-follow-a-restart-a-dropped-version-and-a-removal-together", async () =>
        {
            await using var server = S6Host(Server.Address()); await using var client = S6Host();
            await server.LoadAsync([G6ServeShown("v1", true)], S6Ctx); await client.LoadAsync([], S6Ctx);
            S6Link(server, client);
            using var a = await client.GetAsync(G6Shown, new(), S6Ctx); using var b = await client.GetAsync(G6Shown, new(), S6Ctx);
            await S6Settled(server, client);
            Equal(1, server.Activity.Value.References, "one hold at the provider");
            var states = new[] { new List<string>(), new List<string>() };
            foreach (var (reference, seen) in new[] { (a, states[0]), (b, states[1]) })
                (await reference.State.WatchAsync(S6Ctx)).Start((call, _) => { if (call is NativeWatchValueCall<RefState> value) lock (seen) seen.Add(value.Value.Kind); return Task.CompletedTask; });
            string Label(IReference reference) => reference.Member("label").Value!.GetValue<string>();
            await server.ReloadAsync([G6ServeShown("v2", true)], S6Ctx); await server.DrainReloadsAsync(S6Ctx);
            await S6Until(() => Label(a) == "v2" && Label(b) == "v2", "the restart reaching both");
            await server.ReloadAsync([G6ServeShownV2("v3", false)], S6Ctx); await server.DrainReloadsAsync(S6Ctx);
            await S6Until(() => a.State.Value.Kind == "failed" && b.State.Value.Kind == "failed", "the dropped version failing both");
            await server.ReloadAsync([], S6Ctx); await S6Settled(server, client);
            await server.ReloadAsync([G6ServeShown("v4")], S6Ctx); await server.DrainReloadsAsync(S6Ctx);
            await S6Until(() => a.State.Value.Kind == "live" && b.State.Value.Kind == "live" && Label(a) == "v4" && Label(b) == "v4", "the type served again");
            await S6Settled(server, client);
            // Each reference sees what one hold of its own would have: both the same states, through the failure, live at the end. (The
            // native provider keeps a restart's hold bound to the old attempt until the successor is live, so a restart shows no loading
            // here; upstream's list starts with one.)
            string[] first, second; lock (states[0]) first = [.. states[0]]; lock (states[1]) second = [.. states[1]];
            Sequence(first, second, "both references follow together");
            Check(first.Contains("failed") && first[^1] == "live", "through the dropped version to the type served again: " + string.Join(",", first));
            Equal(1, server.Activity.Value.References, "still one hold");
        }),
    ];
}
