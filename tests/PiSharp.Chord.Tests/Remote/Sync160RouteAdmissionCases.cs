// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/host/route-admission.test.ts (upstream 8270d9af6, slice 14 §2.3;
// test lists 15 and 21). Ported by reading, against real hosts, real workers and raw protocol peers (S6Peer): the relay's route asks an
// admission that reads the gate store on the server. Upstream's `transported()` is A6Delivered (the hosts' dispatchers flushed and time
// for the links). Upstream's `decisions` (every Decision declared, weakly) is read here as the route admission's decisions table.
// Upstream turns are microtasks; here a turn is one dispatcher action, so "N turns into the settle" posts N dispatcher actions before the
// denial commits.
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
    private static readonly StateDefinition R6Modes = StateDefinition.Define("gate.modes", new()
    { Version = 1, Schema = Obj(("modes", Map(Str()))), Initial = () => new() { ["modes"] = new JsonObject() } });

    /// <summary>Who may reach which session, by principal kind: the store the route's admission reads.</summary>
    private static readonly Contract R6Gate = Contract.Local("test.gate", new()
    {
        Version = 1, Params = new(), Members = new() { ["modes"] = Map(Str()), ["set"] = Method(Obj(("who", Str()), ("mode", Str()))) },
    });

    private static void R6ServeGate(IFacetHost h) => h.Serve(R6Gate, (_, _, self) =>
    {
        var state = self.State(R6Modes);
        return new(new Dictionary<string, object?>
        {
            ["modes"] = self.Derive([state], values => ((JsonNode)values[0]!)["modes"]!.DeepClone()),
            ["set"] = (HostMethod)(async (arguments, ctx) =>
            {
                var who = ((JsonNode)arguments!)["who"]!.GetValue<string>(); var mode = ((JsonNode)arguments!)["mode"]!.GetValue<string>();
                await state.ChangeAsync(draft => { ((JsonObject)draft["modes"]!)[who] = mode; }, ctx.Context);
                return null;
            }),
        });
    });

    /// <summary>The route's admission: yes in the gate for the principal's kind allows; throw makes the decision throw.</summary>
    private static readonly RouteAdmit R6Admit = (principal, _, self) => self.Derive([self.Get(R6Gate, new()).Member("modes")], values =>
    {
        var mode = ((JsonNode?)values[0])?[principal["kind"]!.GetValue<string>()]?.GetValue<string>();
        if (mode == "throw") throw new InvalidOperationException("the gate threw");
        return mode == "yes" ? Admission.Allow() : Admission.Deny("no access");
    });

    /// <summary>Serves <see cref="PlacedNotes"/> on a worker (upstream <c>serveNotes(h, PlacedNotes)</c>, without the note store): an
    /// editor for everyone but guests, the count of what was added, a wait that ends with its call.</summary>
    private static void R6ServeNotes(IFacetHost h) => h.Serve(PlacedNotes, (_, principal, self) =>
    {
        var list = self.Writable<JsonNode?>(new JsonArray());
        var notes = self.Derive([list], values => ((JsonNode?)values[0])?.DeepClone());
        var count = self.Derive([list], values => JsonValue.Create(((JsonArray)values[0]!).Count));
        HostMethod wait = async (_, ctx) =>
        {
            try { await Task.Delay(Timeout.Infinite, ctx.AbortSignal); } catch (OperationCanceledException) { }
            return null;
        };
        if (principal?["kind"]?.GetValue<string>() == "guest")
            return new(new Dictionary<string, object?> { ["notes"] = notes, ["count"] = count, ["wait"] = wait }, "reader");
        return new(new Dictionary<string, object?>
        {
            ["notes"] = notes, ["count"] = count, ["wait"] = wait,
            ["add"] = (HostMethod)((arguments, _) =>
            {
                var next = (JsonArray)list.Value!.DeepClone(); next.Add(((JsonNode)arguments!).DeepClone()); list.Set(next);
                return Task.FromResult<object?>(JsonValue.Create(next.Count));
            }),
            ["fail"] = (HostMethod)((_, _) => throw new InvalidOperationException("plain")),
            ["echo"] = (HostMethod)((arguments, _) => Task.FromResult(arguments)),
        }, "editor");
    });

    /// <summary>A tab, a server routing sessions, and its workers (upstream <c>relayed</c> with a route admission): what each resolve made,
    /// the resolves in order, and each host's errors.</summary>
    private sealed class R6Relay : IAsyncDisposable
    {
        internal required ChordHost Tab { get; init; }
        internal required ChordHost Server { get; init; }
        internal required RouteHandle Route { get; init; }
        internal Dictionary<string, ChordHost> Workers { get; } = new(StringComparer.Ordinal);
        internal List<string> Resolves { get; } = [];
        internal List<HostError> TabErrors { get; } = [];
        internal List<HostError> ServerErrors { get; } = [];
        internal List<HostError> WorkerErrors { get; } = [];
        internal IReference? Gate { get; set; }
        internal readonly List<ChordHost> Others = [];

        internal Task Set(string mode, string who = "owner") =>
            Gate!.CallAsync("set", new JsonObject { ["who"] = who, ["mode"] = mode }, ChordContext.Background);
        internal string[] ResolvesNow { get { lock (Resolves) return [.. Resolves]; } }
        internal ChordHost? Worker(string id) { lock (Workers) return Workers.GetValueOrDefault(R6Key(id)); }
        internal int WorkerReferences(string id = "S1") => Worker(id)?.Activity.Value.References ?? 0;
        internal ChordHost[] Hosts { get { lock (Workers) return [Tab, Server, .. Others, .. Workers.Values]; } }

        public async ValueTask DisposeAsync()
        {
            await Server.CloseAsync(ChordContext.Background);
            await Tab.CloseAsync(ChordContext.Background);
            foreach (var other in Others) await other.CloseAsync(ChordContext.Background);
            ChordHost[] workers; lock (Workers) workers = [.. Workers.Values];
            foreach (var worker in workers) await worker.CloseAsync(ChordContext.Background);
        }
    }

    private static string R6Key(string id) => Session.Address(("id", JsonValue.Create(id))).Key;

    private static async Task<R6Relay> R6Relayed(RouteAdmit? admit = null, Action<IFacetHost>? server = null, Action<IFacetHost>? worker = null,
        Func<Address, Func<Task<ResolvedLink>>, Task<ResolvedLink>>? resolve = null, bool gate = false)
    {
        R6Relay? relay = null;
        var serverHost = Hosts.Create(new()
        {
            Address = Server.Address(), Principal = new() { ["kind"] = "server" }, CloseTimeout = TimeSpan.FromMilliseconds(500),
            OnError = error => { lock (relay!.ServerErrors) relay.ServerErrors.Add(error); },
        });
        async Task<ResolvedLink> Made(Address address)
        {
            lock (relay!.Resolves) relay.Resolves.Add(address.Key);
            var principal = new JsonObject { ["kind"] = "worker", ["session"] = address.Params["id"]!.DeepClone() };
            var made = Hosts.Create(new()
            {
                Address = address, Principal = principal, CloseTimeout = TimeSpan.FromMilliseconds(500),
                OnError = error => { lock (relay.WorkerErrors) relay.WorkerErrors.Add(error); },
            });
            var pair = MemoryLinkPair.Create();
            made.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
            _ = made.LoadAsync([new("@test/worker", Facet.Define((h, _) => { R6ServeNotes(h); worker?.Invoke(h); }))], ChordContext.Background);
            lock (relay.Workers) relay.Workers[address.Key] = made;
            return new ResolvedLink(pair.First, new() { Principal = principal.DeepClone().AsObject() });
        }
        var route = serverHost.Route(Session, new()
        {
            Authorize = (_, _) => true,
            Resolve = (address, _) => resolve is null ? Made(address) : resolve(address, () => Made(address)),
            Admit = admit,
        }, ChordContext.Background);
        var tab = Hosts.Create(new() { CloseTimeout = TimeSpan.FromMilliseconds(500), OnError = error => { lock (relay!.TabErrors) relay.TabErrors.Add(error); } });
        relay = new R6Relay { Tab = tab, Server = serverHost, Route = route };
        await serverHost.LoadAsync([new("@test/server", Facet.Define((h, _) => { if (gate) R6ServeGate(h); server?.Invoke(h); }))], ChordContext.Background);
        await tab.LoadAsync([], ChordContext.Background);
        var link = MemoryLinkPair.Create();
        serverHost.Accept(link.First, new() { Principal = new() { ["kind"] = "owner" } }, ChordContext.Background);
        tab.Connect(link.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
        if (gate) relay.Gate = await serverHost.GetAsync(R6Gate, new(), ChordContext.Background);
        return relay;
    }

    /// <summary>A relay whose route asks <see cref="R6Admit"/> over the gate (upstream <c>gated</c>).</summary>
    private static Task<R6Relay> R6Gated(Func<Address, Func<Task<ResolvedLink>>, Task<ResolvedLink>>? resolve = null) =>
        R6Relayed(R6Admit, resolve: resolve, gate: true);

    /// <summary>A client connected to <paramref name="relay"/>'s server as <paramref name="principal"/> (upstream <c>tabOf</c>).</summary>
    private static async Task<ChordHost> R6TabOf(R6Relay relay, JsonObject principal)
    {
        var tab = Hosts.Create(new() { CloseTimeout = TimeSpan.FromMilliseconds(500), OnError = _ => { } });
        await tab.LoadAsync([], ChordContext.Background);
        var pair = MemoryLinkPair.Create();
        relay.Server.Accept(pair.First, new() { Principal = principal }, ChordContext.Background);
        tab.Connect(pair.Second, new() { Principal = new() { ["kind"] = "server" } }, ChordContext.Background);
        relay.Others.Add(tab);
        return tab;
    }

    /// <summary>A resolve whose workers are raw peers playing the provider, collected in order (upstream <c>rawWorkers</c>).</summary>
    private static Func<Address, Func<Task<ResolvedLink>>, Task<ResolvedLink>> R6RawWorkers(List<S6Peer> peers) => (address, _) =>
    {
        var pair = MemoryLinkPair.Create();
        lock (peers) peers.Add(new S6Peer(pair.Second));
        return Task.FromResult(new ResolvedLink(pair.First, new() { Principal = new() { ["kind"] = "worker", ["session"] = address.Params["id"]!.DeepClone() } }));
    };

    private static S6Peer R6Raw(List<S6Peer> peers, int index) { lock (peers) return peers[index]; }
    private static int R6Count(List<S6Peer> peers) { lock (peers) return peers.Count; }

    /// <summary>A raw tab on a new link the server accepts as <paramref name="principal"/> (upstream <c>rawTab</c>).</summary>
    private static S6Peer R6RawTab(ChordHost server, JsonObject principal)
    {
        var pair = MemoryLinkPair.Create();
        server.Accept(pair.First, new() { Principal = principal }, ChordContext.Background);
        return new S6Peer(pair.Second);
    }

    /// <summary>Sends a hold of PlacedNotes for S1 with id <paramref name="hold"/> (upstream <c>holdPlaced</c>).</summary>
    private static void R6HoldPlaced(S6Peer peer, long hold) =>
        peer.Send(S6Hold(PlacedNotes, hold, """{"session":"S1"}""", Session.Address(("id", JsonValue.Create("S1")))));

    /// <summary><paramref name="held"/>'s binding on the raw worker: the editor variant with count (upstream <c>bound</c>).</summary>
    private static BoundMessage R6Bound(HoldMessage held, int count)
    {
        var names = WireNames(PlacedNotes, "editor");
        return new BoundMessage([new BoundEntry(held.Hold, names.Variant,
            [new(names.State["notes"], new Part.Text("[]")), new(names.State["count"], new Part.Text(count.ToString()))], [])]) { Origin = held.Origin };
    }

    /// <summary>A state of count for <paramref name="held"/> on the raw worker (upstream <c>state</c>).</summary>
    private static StateMessage R6State(HoldMessage held, int count) => new([new StateEntry(held.Hold,
        [new StateMember(WireNames(PlacedNotes, "editor").State["count"], new Part.Text("[[\"r\"," + count + "]]"))], [], [])]) { Origin = held.Origin };

    /// <summary>The kinds of what <paramref name="peer"/> received (the hello included), from <paramref name="from"/> on.</summary>
    private static string[] R6Kinds(S6Peer peer, int from = 0) => [.. peer.Log.Skip(from).Select(message => message.Kind)];

    /// <summary>The route admission's decisions now, weakly (upstream counts every Decision declared).</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference[] R6Decisions(R6Relay relay) => relay.Server.Dispatcher.Invoke(() =>
        (((HostRoute)R6RouteField.GetValue(relay.Route)!).Admission?.DecisionsNow ?? []).Select(decision => new WeakReference(decision)).ToArray());

    private static readonly System.Reflection.FieldInfo R6RouteField =
        typeof(RouteHandle).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single(field => field.FieldType == typeof(HostRoute));

    private static async Task<string> R6Told(Task work) => A6Told(await S6Rejection(work));

    private static string R6Cause(IReference reference) => $"{reference.State.Value.Kind} {reference.State.Value.Cause?.Kind}";

    /// <summary>A local type of the server whose open member a test flips (upstream's <c>Open</c>).</summary>
    private static readonly Contract R6Open = Contract.Local("test.open", new() { Version = 1, Params = new(), Members = new() { ["open"] = Str() } });

    /// <summary>A relay whose route admission allows while the server's open flag says yes, and flips it to no
    /// <paramref name="turns"/> dispatcher turns after its decision was first computed (or after admit ran, with
    /// <paramref name="fromAdmit"/>).</summary>
    private static async Task<R6Relay> R6Flipping(int turns, bool fromAdmit)
    {
        IWritable<JsonNode?>? flag = null; ChordHost? server = null; var flipping = 0;
        void Flip(int remaining)
        {
            if (remaining > 0) server!.Dispatcher.Post(() => Flip(remaining - 1));
            else flag?.Set(JsonValue.Create("no"));
        }
        void Start() { if (Interlocked.Exchange(ref flipping, 1) == 0) server!.Dispatcher.Post(() => Flip(turns)); }
        var relay = await R6Relayed((_, _, self) =>
        {
            if (fromAdmit) Start();
            return self.Derive([self.Get(R6Open, new()).Member("open")], values =>
            {
                if (!fromAdmit) Start();
                return ((JsonNode?)values[0])?.GetValue<string>() == "yes" ? Admission.Allow() : Admission.Deny("closed");
            });
        }, server: h => h.Serve(R6Open, (_, _, self) =>
        {
            var open = self.Writable<JsonNode?>(JsonValue.Create("yes"));
            flag = open;
            return new(new Dictionary<string, object?> { ["open"] = self.Derive([open], values => ((JsonNode?)values[0])?.DeepClone()) });
        }));
        server = relay.Server;
        return relay;
    }

    private static IEnumerable<(string, Func<Task>)> RemoteSync160RouteAdmissionCases()
    {
        // --- before the first allow ---
        yield return Case("host.route-admission.before-the-first-allow.nothing-is-resolved-or-forwarded-a-denied-hold-starts-no-host-an-allow-later-binds-it", async () =>
        {
            await using var r = await R6Gated();
            var notes = r.Tab.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            Equal("loading", notes.State.Value.Kind, "loading at first");
            await A6Delivered(r.Hosts);
            Equal("failed denied forbidden: no access", A6Cause(notes.State.Value), "denied");
            Equal("forbidden: no access", await R6Told(r.Tab.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx)), "a getter rejects");
            Equal(0, r.ResolvesNow.Length + r.Hosts.Length - 2, "nothing resolved, no worker");
            await r.Set("yes");
            await A6Delivered(r.Hosts);
            Equal("live 0", $"{notes.State.Value.Kind} {A6Read(notes, "count")}", "bound once allowed");
            Equal(R6Key("S1"), string.Join(",", r.ResolvesNow), "one resolve");
            lock (r.ServerErrors) lock (r.TabErrors) lock (r.WorkerErrors)
                Equal(0, r.ServerErrors.Count + r.TabErrors.Count + r.WorkerErrors.Count, "no errors");
        });
        yield return Case("host.route-admission.before-the-first-allow.routehandle-retry-resolves-nothing-while-every-hold-of-the-address-is-denied-or-undecided", async () =>
        {
            await using var r = await R6Gated();
            var notes = r.Tab.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            r.Route.Retry(Session.Address(("id", JsonValue.Create("S1"))));
            await A6Delivered(r.Hosts);
            r.Route.Retry(Session.Address(("id", JsonValue.Create("S1"))));
            await A6Delivered(r.Hosts);
            Equal("failed Denied", R6Cause(notes), "denied");
            Equal(0, r.ResolvesNow.Length, "nothing resolved");
            await r.Set("yes");
            await A6Delivered(r.Hosts);
            Equal(R6Key("S1"), string.Join(",", r.ResolvesNow), "resolved once allowed");
        });
        yield return Case("host.route-admission.before-the-first-allow.an-admission-holding-a-type-placed-behind-its-own-route-fails-the-hold-invalid-cycle-a-throwing-admit-breaks-it", async () =>
        {
            await using (var cyclic = await R6Relayed((_, address, self) =>
            {
                var behind = self.Get(PlacedNotes, new() { ["session"] = address.Params["id"]!.DeepClone() });
                return self.Derive([behind.Member("count")], _ => Admission.Allow());
            }))
            {
                var notes = cyclic.Tab.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
                await A6Delivered(cyclic.Hosts);
                Equal("failed broken invalid/cycle", $"{notes.State.Value.Kind} {notes.State.Value.Cause?.Kind.ToString().ToLowerInvariant()} " +
                    $"{notes.State.Value.Cause?.Error.Code}/{notes.State.Value.Cause?.Error.Reason}", "invalid/cycle");
                Equal(0, cyclic.ResolvesNow.Length, "nothing resolved");
            }
            await using var throwing = await R6Relayed((_, _, _) => throw new InvalidOperationException("admit threw"));
            var broken = throwing.Tab.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            await A6Delivered(throwing.Hosts);
            Equal("failed Broken", R6Cause(broken), "broken");
            Check(broken.State.Value.Cause!.Error.Message.Contains("admit threw"), "names the throw: " + broken.State.Value.Cause!.Error.Message);
            Equal(0, throwing.ResolvesNow.Length, "nothing resolved");
        });

        // --- a decision that changes while it is settled ---
        // Upstream's DeniedAfterItsRead source queues the denial in the very read of the decision; here the decision's compute posts it.
        yield return Case("host.route-admission.settled.is-not-answered-with-what-the-settle-read-before-a-denial-between-the-read-and-its-continuation-starts-nothing", async () =>
        {
            await using var r = await R6Flipping(0, fromAdmit: false);
            var notes = r.Tab.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            await A6Delivered(r.Hosts);
            Equal("failed Denied", R6Cause(notes), "denied");
            Equal(0, r.WorkerReferences(), "nothing held at a worker");
        });
        // The same through an ordinary source that changes turns dispatcher turns after admit ran: whichever step of the settle the denial
        // meets, the hold ends denied and holds nothing at a host.
        for (var turns = 0; turns < 8; turns++)
        {
            var n = turns;
            yield return Case($"host.route-admission.settled.ends-denied-whenever-the-denial-arrives-{n}-turns-into-the-settle", async () =>
            {
                await using var r = await R6Flipping(n, fromAdmit: true);
                var notes = r.Tab.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
                await A6Delivered(r.Hosts);
                Equal("failed Denied", R6Cause(notes), "denied");
                Equal(0, r.WorkerReferences(), "nothing held at a worker");
            });
        }

        // --- a denial ---
        yield return Case("host.route-admission.a-denial.unbinds-the-principals-holds-released-at-the-worker-replicas-dropped-calls-in-flight-forbidden-revoked", async () =>
        {
            await using var r = await R6Gated();
            await r.Set("yes");
            var notes = await r.Tab.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            Equal(1, r.WorkerReferences(), "held at the worker");
            var waiting = R6Told(notes.CallAsync("wait", null, A6Ctx));
            await A6Delivered(r.Hosts);
            await r.Set("no");
            Check((await waiting).StartsWith("forbidden/revoked", StringComparison.Ordinal), "the call in flight: " + await waiting);
            await A6Delivered(r.Hosts);
            Equal("failed Denied", R6Cause(notes), "denied");
            Equal("throws forbidden: no access", A6Read(notes, "count"), "no value");
            Equal<string?>(null, notes.Variant, "no variant");
            Equal(0, r.WorkerReferences(), "released at the worker");
            // Allowed again: loading at once, not "no access" until the hold is bound; then forwarded behind the fence (the worker answers
            // the ping), under the tab's hold.
            var states = await A6States(notes);
            await r.Set("yes");
            await A6Delivered(r.Hosts);
            Equal("loading,live", string.Join(",", A6Now(states)), "its states");
            Equal("live 0", $"{notes.State.Value.Kind} {A6Read(notes, "count")}", "bound again");
            Equal(1, r.WorkerReferences(), "held again");
        });
        yield return Case("host.route-admission.a-denial.is-per-principal-and-address-another-principals-holds-on-the-address-stay-bound", async () =>
        {
            await using var r = await R6Gated();
            await r.Set("yes"); await r.Set("yes", "guest");
            var guest = await R6TabOf(r, new() { ["kind"] = "guest", ["session"] = "S1" });
            var owners = await r.Tab.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            var guests = await guest.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            Equal("editor reader", $"{owners.Variant} {guests.Variant}", "their variants");
            await r.Set("no", "guest");
            await A6Delivered(r.Hosts);
            Equal("failed Denied", R6Cause(guests), "the guest denied");
            Equal("live", owners.State.Value.Kind, "the owner stays bound");
            Equal(1, r.WorkerReferences(), "one hold at the worker");
        });

        // --- one decision per principal and address ---
        yield return Case("host.route-admission.one-decision.is-shared-by-its-holds-and-released-with-the-last-gc", async () =>
        {
            await using var r = await R6Gated();
            await r.Set("yes");
            var second = await R6TabOf(r, new() { ["kind"] = "owner" });
            var before = R6Decisions(r).Length;
            var a = await r.Tab.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            var b = await second.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            // Two holds of one principal on one address: one decision. Another address: another.
            Equal(1, R6Decisions(r).Length - before, "one decision");
            var other = await r.Tab.GetAsync(PlacedNotes, new() { ["session"] = "S2" }, A6Ctx);
            var made = R6Decisions(r);
            Equal(2, made.Length - before, "two decisions");
            a.Release();
            await A6Delivered(r.Hosts);
            // Still held by the second tab: denied with it.
            await r.Set("no");
            await A6Delivered(r.Hosts);
            Equal("failed Denied", R6Cause(b), "denied with the second tab");
            b.Release(); other.Release();
            await A6Delivered(r.Hosts);
            Equal(0, R6Decisions(r).Length, "none left");
            await ExpectCollected(made);
        });
        yield return Case("host.route-admission.one-decision.a-failed-source-leaves-bound-holds-alone-and-fails-new-ones-waiting-it-heals-with-the-source", async () =>
        {
            await using var r = await R6Gated();
            await r.Set("yes");
            var bound = await r.Tab.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            await r.Set("throw");
            await A6Delivered(r.Hosts);
            Equal("live", bound.State.Value.Kind, "left alone");
            var second = await R6TabOf(r, new() { ["kind"] = "owner" });
            var late = second.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            await A6Delivered(r.Hosts);
            Equal("failed Waiting", R6Cause(late), "a new hold waits");
            Check(late.State.Value.Cause!.Error.Message.Contains("the gate threw"), "names the throw: " + late.State.Value.Cause!.Error.Message);
            await r.Set("yes");
            await A6Delivered(r.Hosts);
            Equal("live live", $"{bound.State.Value.Kind} {late.State.Value.Kind}", "healed");
        });
        yield return Case("host.route-admission.one-decision.a-source-that-fails-again-and-again-still-leaves-the-holds-forwarded-under-the-last-allow-alone", async () =>
        {
            await using var r = await R6Gated();
            await r.Set("yes");
            var bound = await r.Tab.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            await r.Set("throw");
            await A6Delivered(r.Hosts);
            // The source changes for another principal: this one's decision is read again, and throws again.
            await r.Set("no", "guest"); await r.Set("yes", "guest");
            await A6Delivered(r.Hosts);
            Equal("live 1", $"{bound.State.Value.Kind} {r.WorkerReferences()}", "left alone");
            // A denial is what takes them back.
            await r.Set("no");
            await A6Delivered(r.Hosts);
            Equal("failed Denied", R6Cause(bound), "denied");
        });
        yield return Case("host.route-admission.one-decision.a-new-link-of-the-address-holds-nothing-while-the-source-is-failed-forwarded-holds-wait-until-it-recovers-fail-closed", async () =>
        {
            var raw = new List<S6Peer>();
            await using var r = await R6Gated(R6RawWorkers(raw));
            await r.Set("yes");
            var notes = r.Tab.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            await A6Delivered(r.Hosts);
            await r.Set("throw");
            await A6Delivered(r.Hosts);
            // Still held on its link: left alone.
            Equal(0, R6Kinds(R6Raw(raw, 0)).Count(kind => kind == "release"), "no release");
            R6Raw(raw, 0).Dispose();
            await A6Delivered(r.Hosts);
            // A new link is new work at the address's host: nothing is resolved without an allow that can be read.
            Equal(1, R6Count(raw), "nothing resolved");
            Equal("failed Waiting", R6Cause(notes), "waiting");
            Check(notes.State.Value.Cause!.Error.Message.Contains("the gate threw"), "names the throw: " + notes.State.Value.Cause!.Error.Message);
            await r.Set("yes");
            await A6Delivered(r.Hosts);
            // The source is back with its allow: resolved, and held once.
            Equal("hello,hold", string.Join(",", R6Kinds(R6Raw(raw, 1))), "held once on the new link");
            Equal("loading", notes.State.Value.Kind, "loading until bound");
        });
        yield return Case("host.route-admission.one-decision.a-source-that-fails-while-the-addresss-link-is-down-takes-the-forwarded-holds-back-one-that-only-loads-does-not", async () =>
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var raw = new List<S6Peer>();
            var workers = R6RawWorkers(raw);
            await using var r = await R6Gated(async (address, made) =>
            {
                if (R6Count(raw) > 0) await pending.Task;
                return await workers(address, made);
            });
            await r.Set("yes");
            var notes = r.Tab.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            await A6Delivered(r.Hosts);
            // The link drops under an allow: the hold waits for the next link, which is being resolved.
            R6Raw(raw, 0).Dispose();
            await A6Delivered(r.Hosts);
            Equal("loading", notes.State.Value.Kind, "loading");
            // The source fails before that link is there: the link, once made, holds nothing.
            await r.Set("throw");
            await A6Delivered(r.Hosts);
            Equal("failed Waiting", R6Cause(notes), "waiting");
            pending.SetResult();
            await A6Delivered(r.Hosts);
            Equal("hello", string.Join(",", R6Kinds(R6Raw(raw, 1))), "the new link holds nothing");
            await r.Set("yes");
            await A6Delivered(r.Hosts);
            Equal("hello,hold", string.Join(",", R6Kinds(R6Raw(raw, 1))), "held once allowed");
        });

        // --- the fence after a denial ---
        yield return Case("host.route-admission.fence.drops-what-was-in-transit-for-the-released-hold-and-forwards-it-again-under-its-id-only-after-the-pong", async () =>
        {
            var (r, raw, worker, owner, guest, mine, theirs) = await R6Fenced();
            await using var closing = r;
            owner.Send(new CallMessage(7, 0, WireNames(PlacedNotes, "editor").Methods["wait"]));
            await A6Delivered(r.Hosts);
            var seen = worker.Log.Length;
            await r.Set("no");
            await A6Delivered(r.Hosts);
            // The denial: released upstream, the call in flight answered, the hold failed.
            Equal("release", string.Join(",", R6Kinds(worker, seen)), "released upstream");
            Equal("error,failed", string.Join(",", R6Kinds(owner, 2)), "the call answered, the hold failed");
            var error = (ErrorMessage)owner.Log[2];
            Equal("forbidden/revoked", $"{error.Error.Json["error"]?["code"]}/{error.Error.Json["error"]?["reason"]}", "revoked");
            Equal("denied", ((FailedMessage)owner.Log[3]).Cause.Kind, "failed denied");
            // What the worker sent before it saw the release, arriving now: a state, an event, the call's result.
            var tabSeen = owner.Log.Length;
            int Acks() => worker.Log.Count(message => message is AckMessage);
            var acked = Acks();
            worker.Send(R6State(mine, 1));
            worker.Send(new EventMessage([new WireEvent(mine.Hold, 0, 1, new Part.Text("1"))]) { Origin = mine.Origin });
            worker.Send(new ResultMessage(7) { Origin = mine.Origin });
            await A6Delivered(r.Hosts);
            // An event message is forwarded with what is left of it (its ticket is the downstream's to return): nothing.
            Equal("event 0", string.Join(",", owner.Log.Skip(tabSeen).Select(message => message is EventMessage events ? $"event {events.Events.Count}" : message.Kind)),
                "an empty event message");
            // The dropped state is still acknowledged: the worker's window does not stall.
            Equal(1, Acks() - acked, "the dropped state acknowledged");
            await r.Set("yes");
            await A6Delivered(r.Hosts);
            // Allowed: the tab is told loading at once (the person has access again), the worker gets a ping, and no hold before its pong.
            Equal("event,loading", string.Join(",", R6Kinds(owner, tabSeen)), "loading at once");
            Equal("release,ping", string.Join(",", R6Kinds(worker, seen).Where(kind => kind != "ack")), "a ping, no hold");
            worker.Send(R6State(mine, 2));
            worker.Send(R6Bound(mine, 3));
            // The other principal's hold of the same id is undisturbed meanwhile.
            worker.Send(R6State(theirs, 5));
            await A6Delivered(r.Hosts);
            Equal("event,loading", string.Join(",", R6Kinds(owner, tabSeen)), "nothing before the pong");
            Equal("hello,bound,state", string.Join(",", R6Kinds(guest)), "the guest undisturbed");
            worker.Send(new PongMessage());
            await A6Delivered(r.Hosts);
            var again = (HoldMessage)worker.Log[^1];
            Equal($"hold {mine.Hold}", $"{again.Kind} {again.Hold}", "forwarded again under its id");
            worker.Send(R6Bound(again, 9));
            await A6Delivered(r.Hosts);
            Equal("event,loading,bound", string.Join(",", R6Kinds(owner, tabSeen)), "bound again");
        });
        yield return Case("host.route-admission.fence.a-call-that-crossed-the-denial-on-the-wire-is-answered-forbidden-revoked-by-the-relay-and-forwarded-nowhere", async () =>
        {
            var (r, _, worker, owner, _, _, _) = await R6Fenced();
            await using var closing = r;
            await r.Set("no");
            await A6Delivered(r.Hosts);
            var seen = worker.Log.Length;
            // Sent by a tab that has not read its failed yet.
            owner.Send(new CallMessage(9, 0, WireNames(PlacedNotes, "editor").Methods["wait"]));
            await A6Delivered(r.Hosts);
            var error = (ErrorMessage)owner.Log[^1];
            Equal("9 forbidden/revoked: hold 0: no access", $"{error.Call} {error.Error.Json["error"]?["code"]}/{error.Error.Json["error"]?["reason"]}: {error.Error.Json["error"]?["message"]}",
                "answered by the relay");
            Equal(0, R6Kinds(worker, seen).Length, "forwarded nowhere");
        });
        yield return Case("host.route-admission.fence.a-denial-before-the-pong-ends-the-fence-the-next-allow-sends-a-new-one", async () =>
        {
            var (r, _, worker, _, _, mine, _) = await R6Fenced();
            await using var closing = r;
            await r.Set("no"); await r.Set("yes"); await r.Set("no");
            await A6Delivered(r.Hosts);
            var seen = worker.Log.Length;
            // The first fence's pong: nothing is forwarded for a denied principal.
            worker.Send(new PongMessage());
            await A6Delivered(r.Hosts);
            Equal(0, R6Kinds(worker, seen).Length, "nothing forwarded");
            await r.Set("yes");
            await A6Delivered(r.Hosts);
            Equal("ping", string.Join(",", R6Kinds(worker, seen)), "a new fence");
            worker.Send(new PongMessage());
            await A6Delivered(r.Hosts);
            Equal(mine.Hold, ((HoldMessage)worker.Log[^1]).Hold, "forwarded after its pong");
        });
        yield return Case("host.route-admission.fence.deny-and-allow-three-times-in-a-row-forward-the-hold-once-after-the-last-pong", async () =>
        {
            var (r, _, worker, _, _, _, _) = await R6Fenced();
            await using var closing = r;
            var seen = worker.Log.Length;
            for (var round = 0; round < 3; round++) { await r.Set("no"); await r.Set("yes"); }
            await A6Delivered(r.Hosts);
            Equal("release,ping,ping,ping", string.Join(",", R6Kinds(worker, seen)), "one release, three pings");
            worker.Send(new PongMessage()); worker.Send(new PongMessage());
            await A6Delivered(r.Hosts);
            Check(!R6Kinds(worker, seen).Contains("hold"), "no hold before the last pong");
            worker.Send(new PongMessage());
            await A6Delivered(r.Hosts);
            Equal("release,ping,ping,ping,hold", string.Join(",", R6Kinds(worker, seen)), "held once");
        });
        yield return Case("host.route-admission.fence.the-link-lost-before-the-pong-the-next-link-holds-what-the-decision-allows-then-with-no-fence", async () =>
        {
            var (r, raw, worker, owner, _, _, _) = await R6Fenced();
            await using var closing = r;
            await r.Set("no"); await r.Set("yes");
            await A6Delivered(r.Hosts);
            worker.Dispose();
            await A6Delivered(r.Hosts);
            var next = R6Raw(raw, 1);
            // Both principals are allowed: both holds, no ping first.
            Equal("hello,hold,hold", string.Join(",", R6Kinds(next)), "both holds");
            Check(!owner.Closed, "the tab's link stays");
            // Denied while the link is down: the next link holds only the other principal's.
            await r.Set("no");
            next.Dispose();
            await A6Delivered(r.Hosts);
            Equal("hello,hold", string.Join(",", R6Kinds(R6Raw(raw, 2))), "the guest's hold alone");
        });

        // --- across reloads (test 21) ---
        yield return Case("host.route-admission.reloads.the-sources-plugin-reloaded-to-an-equal-allow-no-resolve-no-new-upstream-hold-the-call-across-it-completes", async () =>
        {
            await using var r = await R6Gated();
            await r.Set("yes");
            var notes = await r.Tab.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            var adding = notes.CallAsync("add", new JsonObject { ["text"] = "x" }, A6Ctx);
            await r.Server.ReloadAsync([new("@test/server", Facet.Define((h, _) => R6ServeGate(h)))], A6Ctx);
            await A6Delivered(r.Hosts);
            Equal("1", (await adding as JsonNode)?.ToJsonString(), "the call across it completes");
            await A6Delivered(r.Hosts);
            Equal("live 1", $"{notes.State.Value.Kind} {A6Read(notes, "count")}", "live");
            Equal($"{R6Key("S1")} 1", $"{string.Join(",", r.ResolvesNow)} {r.WorkerReferences()}", "no new resolve or upstream hold");
        });
        yield return Case("host.route-admission.reloads.the-source-removed-leaves-bound-holds-alone-and-fails-new-ones-restored-its-new-store-decides-again", async () =>
        {
            await using var r = await R6Gated();
            await r.Set("yes");
            var bound = await r.Tab.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            await r.Server.ReloadAsync([new("@test/server", Facet.Define((_, _) => { }))], A6Ctx);
            await A6Delivered(r.Hosts);
            Equal("live", bound.State.Value.Kind, "left alone");
            var second = await R6TabOf(r, new() { ["kind"] = "owner" });
            var late = second.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            await A6Delivered(r.Hosts);
            Equal("failed Waiting", R6Cause(late), "a new hold waits");
            await r.Server.ReloadAsync([new("@test/server", Facet.Define((h, _) => R6ServeGate(h)))], A6Ctx);
            await A6Delivered(r.Hosts);
            // A new store object: its modes start over, so the principal is denied until allowed again (the fence).
            Equal("failed Denied|failed Denied", $"{R6Cause(bound)}|{R6Cause(late)}", "both denied");
            var gate = await r.Server.GetAsync(R6Gate, new(), A6Ctx);
            await gate.CallAsync("set", new JsonObject { ["who"] = "owner", ["mode"] = "yes" }, A6Ctx);
            await A6Delivered(r.Hosts);
            Equal("live live", $"{bound.State.Value.Kind} {late.State.Value.Kind}", "both live");
        });
        yield return Case("host.route-admission.reloads.the-source-reloaded-while-a-resolve-is-pending-the-hold-binds-once-the-link-is-there", async () =>
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var r = await R6Gated(async (_, made) => { await pending.Task; return await made(); });
            await r.Set("yes");
            var notes = r.Tab.Hold(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            await A6Delivered(r.Hosts);
            await r.Server.ReloadAsync([new("@test/server", Facet.Define((h, _) => R6ServeGate(h)))], A6Ctx);
            await A6Delivered(r.Hosts);
            Equal("loading", notes.State.Value.Kind, "loading");
            pending.SetResult();
            await A6Delivered(r.Hosts);
            await S6Until(() => notes.State.Value.Kind == "live", "the hold binding");
            // One resolve, not one per decision change.
            Equal(R6Key("S1"), string.Join(",", r.ResolvesNow), "one resolve");
        });
        yield return Case("host.route-admission.reloads.an-object-behind-the-route-reloaded-is-an-ordinary-rebind-no-decision-is-asked-nothing-resolved", async () =>
        {
            await using var r = await R6Gated();
            await r.Set("yes");
            var notes = await r.Tab.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, A6Ctx);
            var before = R6Decisions(r);
            await r.Worker("S1")!.ReloadAsync([new("@test/worker", Facet.Define((h, _) => R6ServeNotes(h)))], A6Ctx);
            await A6Delivered(r.Hosts);
            await S6Until(() => notes.State.Value.Kind == "live", "the rebind");
            var after = R6Decisions(r);
            Check(after.Length == before.Length && after.Zip(before).All(pair => ReferenceEquals(pair.First.Target, pair.Second.Target)), "no decision asked");
            Equal(R6Key("S1"), string.Join(",", r.ResolvesNow), "nothing resolved");
        });
    }

    /// <summary>A server routing to a raw worker, an owner's raw tab and a guest's, each holding S1 as its hold 0, both bound (count 0); the
    /// worker's holds by tab (upstream <c>fenced</c>).</summary>
    private static async Task<(R6Relay Relay, List<S6Peer> Raw, S6Peer Worker, S6Peer Owner, S6Peer Guest, HoldMessage Mine, HoldMessage Theirs)> R6Fenced()
    {
        var raw = new List<S6Peer>();
        var r = await R6Gated(R6RawWorkers(raw));
        await r.Set("yes"); await r.Set("yes", "guest");
        var owner = R6RawTab(r.Server, new() { ["kind"] = "owner" });
        var guest = R6RawTab(r.Server, new() { ["kind"] = "guest", ["session"] = "S1" });
        R6HoldPlaced(owner, 0);
        await A6Delivered(r.Hosts);
        R6HoldPlaced(guest, 0);
        await A6Delivered(r.Hosts);
        var worker = R6Raw(raw, 0);
        var holds = worker.Log.OfType<HoldMessage>().ToArray();
        Equal(2, holds.Length, "two holds at the worker");
        worker.Send(R6Bound(holds[0], 0));
        worker.Send(R6Bound(holds[1], 0));
        await A6Delivered(r.Hosts);
        Equal("hello,bound|hello,bound", $"{string.Join(",", R6Kinds(owner))}|{string.Join(",", R6Kinds(guest))}", "both bound");
        return (r, raw, worker, owner, guest, holds[0], holds[1]);
    }
}
