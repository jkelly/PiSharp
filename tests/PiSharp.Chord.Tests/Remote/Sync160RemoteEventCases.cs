// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/remote/consumer.test.ts ("events at the consumer"),
// provider.test.ts ("events at the provider"), relay.test.ts ("events through a relay", "events across real hosts", "relayed calls in a
// real worker's finite window"). Ported by reading, against real hosts and raw protocol peers. The upstream turn is one dispatcher action
// here (S6Turn); an inbound message is a turn of its own (see Remote/Consumer.cs), which the consumer cases say where it matters. Cases
// named 160-* are authored for the native port.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Remote;
using PiSharp.Chord.State;
using PiSharp.Telemetry;
using static PiSharp.Chord.Contracts.Schemas;
using static ChordSupport;

internal static partial class Program
{
    private static int E6Paddle => S6EventIndex(S6Match, "player", "paddle");
    private static int E6Ball => S6EventIndex(S6Match, "player", "ball");
    private static HoldMessage E6HoldMatch(long hold, Contract? type = null) => S6Hold(type ?? S6Match, hold, "{\"id\":1}");
    private static WireEvent E6PaddleEvent(long hold, double y, long at = 1, long? member = null) =>
        new(hold, member ?? E6Paddle, at, new Part.Text("{\"y\":" + y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}"));
    private static long[] E6EventAcks(S6Peer peer) => [.. peer.Log.OfType<AckMessage>().Where(ack => ack.Events is not null).Select(ack => ack.Events!.Value)];
    private static EventMessage[] E6Events(S6Peer peer) => [.. peer.Log.OfType<EventMessage>()];
    private static string E6Y(WireEvent entry) => Parts.Parse(entry.Value)!["y"]!.ToJsonString();
    private static void E6BindMatch(S6Peer peer, long hold, string variant = "player", long? origin = null)
    {
        var names = ContractsInternal.VariantNamesOf(ContractsInternal.CurrentVariants(S6Match)[variant]);
        peer.Send(new BoundMessage([new BoundEntry(hold, S6VariantIndex(S6Match, variant),
            [new(names.State.ToList().IndexOf("score"), new Part.Text("0"))], [])]) { Origin = origin });
    }

    /// <summary>A server serving the match and a raw consumer peer on a link it accepted (or connected to, as its parent).</summary>
    private static async Task<(ChordHost Server, S6Peer Peer, S6MatchProbe Probe, List<HostError> Errors)> E6MatchServer(bool parent = false, bool ack = true,
        int? eventWindow = null, InMemoryTelemetryContext? telemetry = null, JsonObject? child = null)
    {
        var errors = new List<HostError>(); var probe = new S6MatchProbe();
        var server = S6Host(Server.Address(), errors, telemetry, eventWindow: eventWindow);
        await server.LoadAsync([new("@test/app", Facet.Define((h, _) => S6ServeMatch(h, probe)))], S6Ctx);
        var pair = MemoryLinkPair.Create(); var peer = new S6Peer(pair.Second, autoAck: ack);
        var options = new LinkOptions { Principal = child ?? new() { ["kind"] = "owner" } };
        if (parent) server.Connect(pair.First, options, S6Ctx); else server.Accept(pair.First, options, S6Ctx);
        return (server, peer, probe, errors);
    }

    /// <summary>A client on a raw provider peer, holding the match bound as <paramref name="variant"/>: its reference, its paddle member
    /// and the hold's id on the wire.</summary>
    private static async Task<(ChordHost Client, S6Peer Peer, IReference Match, IEventMember Paddle, long Hold, List<HostError> Errors, Func<IReadOnlyDictionary<string, double>> Counts)>
        E6EventClient(string variant = "player", PartLimits? limits = null)
    {
        var errors = new List<HostError>(); var (telemetry, context, counts) = await Metered();
        var client = S6Host(errors: errors, telemetry: telemetry); await client.LoadAsync([], S6Ctx);
        var pair = MemoryLinkPair.Create(); client.Connect(pair.First, new() { Principal = S6ServerPrincipal, Limits = limits }, context);
        var peer = new S6Peer(pair.Second);
        var got = client.GetAsync(S6Match, new() { ["id"] = 1 }, S6Ctx);
        var hold = await peer.Next<HoldMessage>(); E6BindMatch(peer, hold.Hold, variant);
        var match = await got;
        return (client, peer, match, match.Event("paddle"), hold.Hold, errors, counts);
    }

    /// <summary>A server serving the match itself and routing sessions to raw workers, and a raw tab.</summary>
    private static async Task<(ChordHost Server, List<S6Peer> Workers, S6Peer Raw, S6MatchProbe Probe)> E6EventRelay()
    {
        var workers = new List<S6Peer>(); var probe = new S6MatchProbe();
        var server = S6Host(Server.Address());
        server.Route(Session, new()
        {
            Authorize = (_, _) => true,
            Resolve = (_, _) =>
            {
                var pair = MemoryLinkPair.Create(); var peer = new S6Peer(pair.Second);
                lock (workers) workers.Add(peer);
                return Task.FromResult(new ResolvedLink(pair.First, new() { Principal = new() { ["kind"] = "worker" } }));
            },
        }, S6Ctx);
        await server.LoadAsync([new("@test/app", Facet.Define((h, _) => S6ServeMatch(h, probe)))], S6Ctx);
        return (server, workers, W6Tab(server), probe);
    }

    /// <summary>Holds the placed notes of <paramref name="session"/> with id <paramref name="hold"/> from the raw tab, bound by its raw
    /// worker; the worker and the hold as it received it.</summary>
    private static async Task<(S6Peer Worker, HoldMessage Held)> E6RelayedHold(S6Peer raw, List<S6Peer> workers, long hold, string session, int index)
    {
        raw.Send(W6HoldPlaced(hold, session));
        var worker = await W6WorkerAt(workers, index); var held = await worker.Next<HoldMessage>();
        W6BindPlaced(worker, held); await raw.Next<BoundMessage>();
        return (worker, held);
    }
    private static EventMessage E6WorkerEvents(HoldMessage held, double[] ys, long? hold = null) =>
        new([.. ys.Select(y => E6PaddleEvent(hold ?? held.Hold, y, 1, 0))]) { Origin = held.Origin };
    private static string[] E6TabEvents(S6Peer raw) => [.. E6Events(raw).Select(message => string.Join(",", message.Events.Select(entry => entry.Hold + ":" + E6Y(entry))))];
    private static string[] E6AcksOf(S6Peer worker) => [.. worker.Log.OfType<AckMessage>().Where(ack => ack.Events is not null).Select(ack => ack.Origin + "/" + ack.Events)];

    // Made on first use: a static initializer here could run before the support file's (S6Sample).
    private static readonly Lazy<Contract> E6PlacedMatchType = new(() => Contract.Remote("app.placed-match", new()
    {
        Version = 1, Params = new() { ["session"] = Str() }, HostAddress = values => Session.Address(("id", values["session"])),
        Variants = new() { ["player"] = new() { Events = { ["paddle"] = S6Sample } } },
    }));
    private static readonly Lazy<Contract> E6LobbyType = new(() => Contract.Remote("app.lobby", new()
    {
        Version = 1, Params = new() { ["session"] = Str() }, HostAddress = _ => Server.Address(),
        Variants = new() { ["player"] = new() { Events = { ["paddle"] = S6Sample } } },
    }));
    private static Contract E6PlacedMatch => E6PlacedMatchType.Value;
    private static Contract E6Lobby => E6LobbyType.Value;

    /// <summary>A tab, a server and real workers (upstream <c>relayed</c>): the server routes sessions to workers it makes, each serving
    /// what <paramref name="worker"/> declares and announcing <paramref name="workerCallWindow"/>; the tab is the server's child.</summary>
    private static async Task<(ChordHost Tab, ChordHost Server, List<ChordHost> Workers, List<HostError> Errors)> E6Relayed(
        Action<IFacetHost>? worker = null, Action<IFacetHost>? server = null, int? workerCallWindow = null)
    {
        var workers = new List<ChordHost>(); var errors = new List<HostError>();
        var relay = S6Host(Server.Address(), errors);
        relay.Route(Session, new()
        {
            Authorize = (_, _) => true,
            Resolve = async (address, _) =>
            {
                var made = S6Host(address, errors, callWindow: workerCallWindow, principal: new() { ["kind"] = "worker" });
                await made.LoadAsync(worker is null ? [] : [new("@test/worker", Facet.Define((h, _) => worker(h)))], S6Ctx);
                var pair = MemoryLinkPair.Create(); made.Connect(pair.Second, new() { Principal = S6ServerPrincipal }, S6Ctx);
                lock (workers) workers.Add(made);
                return new ResolvedLink(pair.First, new() { Principal = new() { ["kind"] = "worker" } });
            },
        }, S6Ctx);
        await relay.LoadAsync(server is null ? [] : [new("@test/server", Facet.Define((h, _) => server(h)))], S6Ctx);
        var tab = S6Host(errors: errors, principal: new() { ["kind"] = "owner" }); await tab.LoadAsync([], S6Ctx);
        S6Link(relay, tab, new() { ["kind"] = "owner" });
        return (tab, relay, workers, errors);
    }

    private static IEnumerable<(string, Func<Task>)> RemoteSync160EventCases() =>
    [
        // --- events at the consumer ---
        Case("remote.consumer.events-deliver-the-entries-in-order-with-their-stamps-and-acknowledge-each-message-after-its-flush", async () =>
        {
            // Upstream: both messages arrive in one inbound turn and get one ack { events: 2 } after the flush. Here each inbound message
            // is a turn of its own: each is acknowledged after the flush that delivered it, and the acks add up to the messages consumed.
            var (client, peer, _, paddle, hold, _, _) = await E6EventClient(); await using var closing = client;
            var seen = new List<string>(); var acksAtDelivery = new List<int>();
            await paddle.OnAsync(e => { lock (seen) { seen.Add(e.Value!["y"]!.ToJsonString() + "@" + e.At); acksAtDelivery.Add(E6EventAcks(peer).Length); } }, S6Ctx);
            peer.Send(new EventMessage([E6PaddleEvent(hold, 1, 10), E6PaddleEvent(hold, 2, 11)]));
            peer.Send(new EventMessage([E6PaddleEvent(hold, 3, 12)]));
            await S6Until(() => E6EventAcks(peer).Sum() == 2, "both messages acknowledged");
            Sequence(["1@10", "2@11", "3@12"], S6Seen(seen), "in order, with their stamps");
            lock (seen) Check(acksAtDelivery[0] == 0 && acksAtDelivery[1] == 0 && acksAtDelivery[2] <= 1,
                "no message is acknowledged before its entries were delivered: " + string.Join(",", acksAtDelivery));
            Check(E6EventAcks(peer).All(count => count >= 1), "every ack counts the messages it covers");
        }),
        Case("remote.consumer.events-acknowledge-dropped-entries-too-an-unknown-hold-a-member-nobody-listens-to-a-value-past-the-part-limit", async () =>
        {
            var (client, peer, match, paddle, hold, _, counts) = await E6EventClient(limits: new(16)); await using var closing = client;
            var seen = new List<string>();
            await paddle.OnAsync(S6Recorder(seen), S6Ctx);
            peer.Send(new EventMessage([E6PaddleEvent(hold + 7, 1)]));
            peer.Send(new EventMessage([E6PaddleEvent(hold, 2, 1, E6Ball)]));
            peer.Send(new EventMessage([new WireEvent(hold, E6Paddle, 1, new Part.Text("{\"y\":3,\"pad\":\"" + new string('x', 32) + "\"}"))]));
            peer.Send(new EventMessage([E6PaddleEvent(hold, 4)]));
            await S6Until(() => E6EventAcks(peer).Sum() == 4, "four messages acknowledged");
            Sequence(["4"], S6Seen(seen), "only the last was delivered");
            Equal(1.0, S6Count((InMemoryTelemetryContext)client.Options.Telemetry!, "chord.events.dropped", "too_large"), "the oversized value was counted dropped");
            // The oversized value failed nothing: the hold stays bound.
            Equal("live", match.State.Value.Kind, "the hold stays bound"); Check(!peer.Closed, "and the link open");
            GC.KeepAlive(counts);
        }),
        Case("remote.consumer.events-inflate-nothing-for-a-member-nobody-listens-to-nor-parse-it-acknowledged-all-the-same", async () =>
        {
            var (client, peer, _, paddle, hold, _, counts) = await E6EventClient(); await using var closing = client;
            var seen = new List<string>();
            await paddle.OnAsync(S6Recorder(seen), S6Ctx);
            Part Deflated(int y) => new Part.Deflated(NativeCompression.Instance.Deflate(System.Text.Encoding.UTF8.GetBytes("{\"y\":" + y + "}")));
            peer.Send(new EventMessage([new WireEvent(hold, E6Ball, 1, Deflated(1))]));
            await S6Until(() => E6EventAcks(peer).Sum() == 1, "the first message acknowledged");
            Equal(0.0, counts().GetValueOrDefault("chord.link.inflates"), "nothing inflated for a member nobody listens to");
            peer.Send(new EventMessage([new WireEvent(hold, E6Paddle, 1, Deflated(2))]));
            await S6Until(() => E6EventAcks(peer).Sum() == 2, "the second message acknowledged");
            Sequence(["2"], S6Seen(seen), "the listened member's value was inflated and delivered");
            Equal(1.0, counts().GetValueOrDefault("chord.link.inflates"), "one inflate");
        }),
        Case("remote.consumer.events-close-the-link-for-a-member-index-outside-the-bound-version", async () =>
        {
            var (client, peer, _, _, hold, errors, _) = await E6EventClient(); await using var closing = client;
            peer.Send(new EventMessage([E6PaddleEvent(hold, 1, 1, 9)]));
            await S6Until(() => peer.Closed, "the link closing");
            lock (errors) Check(errors.Count == 1 && errors[0].Kind == "protocol_error", "one protocol error");
        }),
        Case("remote.consumer.events-160-an-earlier-bindings-event-is-delivered-in-its-own-turn-none-while-rebinding-then-the-new-bindings", async () =>
        {
            // Upstream sends an event, a loading and a bound in one inbound turn and asserts the event (of the earlier binding) reaches
            // nobody. There is no inbound turn spanning several messages here: the event is flushed while its binding is still current.
            // What holds on both sides: an event arriving while the hold rebinds is dropped, and after a bound only that binding's arrive.
            var (client, peer, match, paddle, hold, _, _) = await E6EventClient(); await using var closing = client;
            var seen = new List<string>();
            await paddle.OnAsync(S6Recorder(seen), S6Ctx);
            peer.Send(new EventMessage([E6PaddleEvent(hold, 1)]));
            peer.Send(new LoadingMessage(hold));
            peer.Send(new EventMessage([E6PaddleEvent(hold, 2)]));
            E6BindMatch(peer, hold, "player");
            peer.Send(new EventMessage([E6PaddleEvent(hold, 3)]));
            await S6Until(() => E6EventAcks(peer).Sum() == 3, "three messages acknowledged");
            Sequence(["1", "3"], S6Seen(seen), "nothing while it was not live");
            peer.Send(new LoadingMessage(hold)); E6BindMatch(peer, hold, "watcher");
            await S6Until(() => match.Variant == "watcher", "the watcher binding");
            Check(!peer.Closed, "the link stays open");
        }),
        Case("remote.consumer.events-deliver-none-while-bound-to-a-variant-without-the-member-and-again-once-a-binding-has-it", async () =>
        {
            var (client, peer, match, paddle, hold, _, _) = await E6EventClient("watcher"); await using var closing = client;
            var seen = new List<string>();
            await paddle.OnAsync(S6Recorder(seen), S6Ctx);
            peer.Send(new LoadingMessage(hold)); E6BindMatch(peer, hold, "player");
            await S6Until(() => match.Variant == "player" && match.State.Value.Kind == "live", "the player binding");
            peer.Send(new EventMessage([E6PaddleEvent(hold, 5)]));
            await S6Until(() => S6Seen(seen).Length == 1, "the event");
            Sequence(["5"], S6Seen(seen), "delivered once a binding has the member");
        }),

        // --- events at the provider ---
        Case("remote.provider.events-send-a-turns-events-as-one-event-message-per-link-and-origin-after-the-state-published-before-the-flush", async () =>
        {
            var (server, peer, probe, _) = await E6MatchServer(); await using var closing = server;
            peer.Send(E6HoldMatch(0)); peer.Send(E6HoldMatch(1));
            await peer.Next<BoundMessage>(); await peer.Next<BoundMessage>();
            var start = peer.Log.Length; var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            S6Turn(server, () => { probe.Paddle.Emit(S6Y(1)); probe.Score.Set(1); probe.Paddle.Emit(S6Y(2)); });
            await S6Until(() => E6Events(peer).Length == 1, "the event message"); await S6Settled(server);
            Sequence(["state", "event"], peer.Log.Skip(start).Select(message => message.Kind), "the state published in the turn goes first, then one event message");
            var message = E6Events(peer).Single();
            Sequence([$"0/{E6Paddle}/1", $"1/{E6Paddle}/1", $"0/{E6Paddle}/2", $"1/{E6Paddle}/2"],
                message.Events.Select(entry => $"{entry.Hold}/{entry.Member}/{E6Y(entry)}"), "entry order, then hold order");
            Check(message.Events.All(entry => entry.At >= before), "stamped at the emit");
        }),
        Case("remote.provider.events-send-one-message-per-origin-each-with-its-origin", async () =>
        {
            var (server, peer, probe, _) = await E6MatchServer(parent: true); await using var closing = server;
            peer.Send(E6HoldMatch(0)); peer.Send(E6HoldMatch(0) with { Origin = 3 });
            await peer.Next<BoundMessage>(); await peer.Next<BoundMessage>();
            probe.Paddle.Emit(S6Y(1));
            await S6Until(() => E6Events(peer).Length == 2, "two event messages");
            Sequence(["/1", "3/1"], E6Events(peer).Select(message => message.Origin + "/" + message.Events.Count), "one per origin, each with its origin");
        }),
        Case("remote.provider.events-send-none-before-a-bound-the-state-window-holds-back", async () =>
        {
            var (server, peer, probe, _) = await E6MatchServer(ack: false); await using var closing = server;
            peer.Send(E6HoldMatch(0)); await peer.Next<BoundMessage>();
            // The window's two slots: the bound and a state, unacknowledged; a new hold's bound waits.
            probe.Score.Set(1); await peer.Next<StateMessage>();
            peer.Send(E6HoldMatch(1));
            await S6Until(() => server.Dispatcher.Invoke(() => server.Activity.Value.References == 2), "the second hold arriving"); await S6Settled(server);
            probe.Paddle.Emit(S6Y(1));
            await S6Until(() => E6Events(peer).Length == 1, "the first event message");
            Sequence(["0"], E6Events(peer)[0].Events.Select(entry => entry.Hold.ToString()), "only the bound hold");
            peer.Send(new AckMessage()); await peer.Next<BoundMessage>();
            probe.Paddle.Emit(S6Y(2));
            await S6Until(() => E6Events(peer).Length == 2, "the second event message");
            Sequence(["0", "1"], E6Events(peer)[1].Events.Select(entry => entry.Hold.ToString()), "both once the second is bound");
        }),
        Case("remote.provider.events-send-nothing-to-a-variant-without-the-member", async () =>
        {
            var (server, peer, probe, _) = await E6MatchServer(child: S6Guest); await using var closing = server;
            peer.Send(E6HoldMatch(0));
            Equal((long)S6VariantIndex(S6Match, "watcher"), (await peer.Next<BoundMessage>()).Holds[0].Variant, "a guest is bound as a watcher");
            probe.Paddle.Emit(S6Y(1));
            await S6Settled(server);
            Equal(0, E6Events(peer).Length, "a watcher has no event members");
        }),
        Case("remote.provider.events-send-nothing-for-an-emit-its-object-fails-after-in-the-same-turn", async () =>
        {
            var (server, peer, probe, _) = await E6MatchServer(); await using var closing = server;
            peer.Send(E6HoldMatch(0)); await peer.Next<BoundMessage>();
            S6Turn(server, () => { probe.Paddle.Emit(S6Y(1)); S6FailLive(server, probe.Selves[0]); });
            await peer.Next<FailedMessage>(); await S6Settled(server);
            Equal(0, E6Events(peer).Length, "no event to a binding not shown live");
        }),
        Case("remote.provider.events-send-nothing-re-exposed-an-emit-its-source-fails-after-in-the-same-turn-reaches-no-hold-of-the-view", async () =>
        {
            var tickSource = Contract.Local("app.tick-source", new() { Version = 1, Params = new(), Members = new() { ["ticks"] = Event(S6Sample) } });
            var tickView = Contract.Remote("app.tick-view", new()
            {
                Version = 1, Params = new(), HostAddress = _ => Server.Address(),
                Variants = new() { ["v"] = new() { Events = { ["ticks"] = S6Sample } } },
            });
            var sources = new List<(IEventSource Source, ISelf Self)>();
            await using var server = S6Host(Server.Address());
            await server.LoadAsync([new("@test/app", Facet.Define((h, _) =>
            {
                h.Serve(tickSource, (_, _, self) =>
                {
                    var ticks = self.Events(); lock (sources) sources.Add((ticks, self));
                    return new(new Dictionary<string, object?> { ["ticks"] = ticks });
                });
                h.Serve(tickView, (_, _, self) => new(new Dictionary<string, object?> { ["ticks"] = self.Get(tickSource, new()).Event("ticks") }, "v"));
            }))], S6Ctx);
            var pair = MemoryLinkPair.Create(); server.Accept(pair.First, new() { Principal = new() { ["kind"] = "owner" } }, S6Ctx);
            using var peer = new S6Peer(pair.Second, autoAck: true);
            peer.Send(S6Hold(tickView)); await peer.Next<BoundMessage>();
            var (source, self) = sources[0];
            source.Emit(S6Y(1));
            await S6Until(() => E6Events(peer).Length == 1, "the re-exposed event");
            Sequence(["1"], E6Events(peer)[0].Events.Select(E6Y), "through the view");
            S6Turn(server, () => { source.Emit(S6Y(2)); S6FailLive(server, self); });
            await S6Settled(server);
            Equal(1, E6Events(peer).Length, "nothing of an emit its source failed after");
        }),
        Case("remote.provider.events-keep-state-and-event-credits-apart-events-flow-while-the-state-window-is-full-and-the-reverse", async () =>
        {
            var (server, peer, probe, _) = await E6MatchServer(ack: false, eventWindow: 1); await using var closing = server;
            peer.Send(E6HoldMatch(0)); await peer.Next<BoundMessage>();
            probe.Score.Set(1); await peer.Next<StateMessage>();
            probe.Score.Set(2);
            // State waits for a slot; an event goes.
            probe.Paddle.Emit(S6Y(1));
            await S6Until(() => E6Events(peer).Length == 1, "the event going out"); await S6Settled(server);
            Equal(1, peer.Count<StateMessage>(), "the second state still waits");
            // The event window is full now; state acks open the state window only.
            peer.Send(new AckMessage()); peer.Send(new AckMessage());
            await S6Until(() => peer.Count<StateMessage>() == 2, "the waiting state");
            probe.Paddle.Emit(S6Y(2));
            await S6Settled(server);
            Equal(1, E6Events(peer).Length, "the event window is still full: dropped");
            // An event ack frees the event window, not a state slot.
            peer.Send(new AckMessage(1));
            await S6Settled(server);
            probe.Paddle.Emit(S6Y(3));
            await S6Until(() => E6Events(peer).Length == 2, "the next event");
            Sequence(["3"], E6Events(peer)[1].Events.Select(E6Y), "sent once acknowledged");
        }),
        Case("remote.provider.events-drop-a-flushs-events-while-the-event-window-is-full-counted-and-send-again-once-acknowledged", async () =>
        {
            var telemetry = new InMemoryTelemetryContext();
            var (server, peer, probe, _) = await E6MatchServer(eventWindow: 2, telemetry: telemetry); await using var closing = server;
            peer.Send(E6HoldMatch(0)); await peer.Next<BoundMessage>();
            foreach (var y in new[] { 1, 2, 3 })
            {
                S6Turn(server, () => { probe.Paddle.Emit(S6Y(y)); probe.Paddle.Emit(S6Y(y * 10)); });
                await S6Flushed(server);
            }
            await S6Until(() => E6Events(peer).Length == 2, "two event messages"); await S6Settled(server);
            Sequence(["1,10", "2,20"], E6Events(peer).Select(message => string.Join(",", message.Events.Select(E6Y))), "the third flush was dropped");
            // The dropped round encoded nothing: the window is decided before a value is encoded.
            Equal(6.0, S6Count(telemetry, "chord.events.emitted"), "emitted"); Equal(4.0, S6Count(telemetry, "chord.events.sent"), "sent");
            Equal(2.0, S6Count(telemetry, "chord.events.dropped", "window"), "dropped for the window"); Equal(4.0, S6Count(telemetry, "chord.events.encodes"), "encodes");
            peer.Send(new AckMessage(2));
            await S6Settled(server);
            probe.Paddle.Emit(S6Y(4));
            await S6Until(() => E6Events(peer).Length == 3, "the next event message");
            Sequence(["4"], E6Events(peer)[2].Events.Select(E6Y), "sent again once acknowledged");
        }),
        Case("remote.provider.events-encode-each-value-once-per-representation-1-and-10-holders-one-more-for-a-mapped-version", async () =>
        {
            var telemetry = new InMemoryTelemetryContext(); var paddles = new List<IEventSource>();
            await using var server = S6Host(Server.Address(), telemetry: telemetry);
            await server.LoadAsync([new("@test/app", Facet.Define((h, _) => h.Serve(S6MatchV2, (_, _, self) =>
            {
                var paddle = self.Events(); lock (paddles) paddles.Add(paddle);
                return new(new Dictionary<string, object?>
                {
                    ["score"] = Watchables.Constant(JsonValue.Create(0)), ["paddle"] = paddle, ["ball"] = self.Events(),
                    ["bump"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
                }, "player");
            }, new(new AcceptsTable { [1] = new() { Events = { ["player"] = new StateSection
                { ["paddle"] = new StateMapping(value => new JsonObject { ["y"] = ((JsonNode)value!)["y"]!.DeepClone() }) } } } }))))], S6Ctx);
            var pair = MemoryLinkPair.Create(); server.Accept(pair.First, new() { Principal = new() { ["kind"] = "owner" } }, S6Ctx);
            using var peer = new S6Peer(pair.Second, autoAck: true);
            async Task<double> Encodes(double y)
            {
                var before = S6Count(telemetry, "chord.events.encodes"); var messages = E6Events(peer).Length;
                paddles[0].Emit(new JsonObject { ["y"] = y, ["t"] = 0 });
                await S6Until(() => E6Events(peer).Length == messages + 1, "the event message"); await S6Settled(server);
                return S6Count(telemetry, "chord.events.encodes") - before;
            }
            int Bound() => peer.Log.OfType<BoundMessage>().Sum(message => message.Holds.Count);
            peer.Send(E6HoldMatch(0, S6MatchV2)); await peer.Next<BoundMessage>();
            Equal(1.0, await Encodes(1), "one holder: one encode");
            for (var id = 1; id < 10; id++) peer.Send(E6HoldMatch(id, S6MatchV2));
            await S6Until(() => Bound() == 10, "ten holds bound");
            Equal(1.0, await Encodes(2), "ten holders: still one encode");
            // An older consumer: its mapped values are a representation of their own, encoded once for all its holds.
            peer.Send(E6HoldMatch(10)); peer.Send(E6HoldMatch(11));
            await S6Until(() => Bound() == 12, "the older holds bound");
            Equal(2.0, await Encodes(3), "one more for the mapped version");
            var last = E6Events(peer)[^1];
            Sequence(["{\"y\":3}", "{\"y\":3}"], last.Events.Where(entry => entry.Hold >= 10).Select(entry => Parts.Parse(entry.Value)!.ToJsonString()), "the older holds get the mapped value");
            Equal(0.0, S6Count(telemetry, "chord.data.walks"), "never walked");
        }),
        Case("remote.provider.events-retire-a-provider-sides-record-only-once-its-event-tickets-drained", async () =>
        {
            // Upstream asserts the provider side is collected after the event ack. Here the link's provider sides are counted directly.
            var (server, peer, probe, _) = await E6MatchServer(); await using var closing = server;
            int Sides() => server.Dispatcher.Invoke(() => server.Links.Single().Providers);
            peer.Send(E6HoldMatch(0)); peer.Send(E6HoldMatch(1));
            await peer.Next<BoundMessage>(); await peer.Next<BoundMessage>();
            probe.Paddle.Emit(S6Y(1));
            await S6Until(() => E6Events(peer).Length == 1, "the event message");
            peer.Send(new ReleaseMessage(0)); peer.Send(new ReleaseMessage(1));
            await S6Until(() => server.Dispatcher.Invoke(() => server.Activity.Value.References == 0), "both holds released"); await S6Settled(server);
            // Nothing held, its event message unacknowledged: the side stays until its ack.
            Equal(1, Sides(), "the side stays while its event ticket is out");
            peer.Send(new AckMessage(1));
            await S6Until(() => Sides() == 0, "the side retiring");
            Check(!peer.Closed, "the link stays open");
        }),

        // --- events through a relay ---
        Case("remote.relay.events-forward-an-upstream-event-message-at-once-with-the-tabs-origin-never-acknowledging-it-themselves", async () =>
        {
            var (server, workers, raw, _) = await E6EventRelay(); await using var closing = server;
            var (worker, held) = await E6RelayedHold(raw, workers, 0, "S1", 0);
            worker.Send(E6WorkerEvents(held, [1, 2]));
            await S6Until(() => E6Events(raw).Length == 1, "the forwarded message"); await S6Settled(server);
            Sequence(["0:1,0:2"], E6TabEvents(raw), "forwarded as it came");
            Equal(null, E6Events(raw)[0].Origin, "with the tab's origin (none on its own link)");
            Equal(0, E6AcksOf(worker).Length, "the relay acknowledges nothing itself");
            raw.Send(new AckMessage(1));
            await S6Until(() => E6AcksOf(worker).Length == 1, "the ticket returned");
            Sequence([held.Origin + "/1"], E6AcksOf(worker), "to its sender, with the origin");
        }),
        Case("remote.relay.events-return-each-ticket-to-its-sender-the-relays-own-events-and-two-workers-on-one-tab-origin-acks-spanning-them", async () =>
        {
            var (server, workers, raw, probe) = await E6EventRelay(); await using var closing = server;
            var one = await E6RelayedHold(raw, workers, 0, "S1", 0); var two = await E6RelayedHold(raw, workers, 1, "S2", 1);
            raw.Send(E6HoldMatch(2)); await raw.Next<BoundMessage>();
            // Tickets in order: worker one, the relay's own, worker two, worker one.
            one.Worker.Send(E6WorkerEvents(one.Held, [1])); await S6Until(() => E6Events(raw).Length == 1, "the first");
            probe.Paddle.Emit(S6Y(2)); await S6Until(() => E6Events(raw).Length == 2, "the relay's own");
            two.Worker.Send(E6WorkerEvents(two.Held, [3])); await S6Until(() => E6Events(raw).Length == 3, "the third");
            one.Worker.Send(E6WorkerEvents(one.Held, [4])); await S6Until(() => E6Events(raw).Length == 4, "the fourth");
            Sequence(["1", "2", "3", "4"], E6Events(raw).Select(message => string.Join(",", message.Events.Select(E6Y))), "in arrival order");
            raw.Send(new AckMessage(3));
            await S6Until(() => E6AcksOf(one.Worker).Length == 1 && E6AcksOf(two.Worker).Length == 1, "the first three tickets returned"); await S6Settled(server);
            Sequence([one.Held.Origin + "/1"], E6AcksOf(one.Worker), "worker one's first ticket");
            Sequence([two.Held.Origin + "/1"], E6AcksOf(two.Worker), "worker two's ticket");
            raw.Send(new AckMessage(1));
            await S6Until(() => E6AcksOf(one.Worker).Length == 2, "the last ticket returned");
            Sequence([one.Held.Origin + "/1", one.Held.Origin + "/1"], E6AcksOf(one.Worker), "worker one's second ticket");
        }),
        Case("remote.relay.events-filter-entries-of-holds-not-relayed-over-the-link-after-a-release-some-then-all-forwarded-and-never-acked-by-the-relay", async () =>
        {
            var (server, workers, raw, _) = await E6EventRelay(); await using var closing = server;
            var (worker, held) = await E6RelayedHold(raw, workers, 0, "S1", 0);
            raw.Send(W6HoldPlaced(1, "S1")); var other = await worker.Next<HoldMessage>(); W6BindPlaced(worker, other); await raw.Next<BoundMessage>();
            raw.Send(new ReleaseMessage(1)); await worker.Next<ReleaseMessage>();
            worker.Send(new EventMessage([.. E6WorkerEvents(held, [1]).Events, .. E6WorkerEvents(held, [2], 1).Events]) { Origin = held.Origin });
            worker.Send(E6WorkerEvents(held, [3], 1));
            await S6Until(() => E6Events(raw).Length == 2, "both messages forwarded"); await S6Settled(server);
            Sequence(["0:1", ""], E6TabEvents(raw), "the released hold's entries are filtered; an emptied message is forwarded all the same");
            Equal(0, E6AcksOf(worker).Length, "never acknowledged by the relay");
            raw.Send(new AckMessage(2));
            await S6Until(() => E6AcksOf(worker).Length == 1, "the tickets returned");
            Sequence([held.Origin + "/2"], E6AcksOf(worker), "both tickets in one ack");
        }),
        Case("remote.relay.events-credit-two-senders-behind-an-upstream-relay-in-order-a-filtered-second-message-behind-an-unconsumed-first", async () =>
        {
            // The raw worker plays a relay with two senders behind it: its acks must arrive in its messages' order.
            var (server, workers, raw, _) = await E6EventRelay(); await using var closing = server;
            var (worker, held) = await E6RelayedHold(raw, workers, 0, "S1", 0);
            raw.Send(W6HoldPlaced(1, "S1")); var other = await worker.Next<HoldMessage>(); W6BindPlaced(worker, other); await raw.Next<BoundMessage>();
            raw.Send(new ReleaseMessage(1)); await worker.Next<ReleaseMessage>();
            // The first sender's message, then the second's, all of whose entries the relay filters.
            worker.Send(E6WorkerEvents(held, [1])); worker.Send(E6WorkerEvents(held, [2], 1));
            await S6Until(() => E6Events(raw).Length == 2, "both forwarded"); await S6Settled(server);
            // An early ack of the filtered one would credit the first sender's message, still unconsumed at the tab.
            Equal(0, E6AcksOf(worker).Length, "no early ack");
            raw.Send(new AckMessage(1));
            await S6Until(() => E6AcksOf(worker).Length == 1, "the first ticket"); await S6Settled(server);
            Sequence([held.Origin + "/1"], E6AcksOf(worker), "one ticket");
            raw.Send(new AckMessage(1));
            await S6Until(() => E6AcksOf(worker).Length == 2, "the second ticket");
            Sequence([held.Origin + "/1", held.Origin + "/1"], E6AcksOf(worker), "in order");
        }),
        Case("remote.relay.events-consume-the-tickets-of-an-upstream-link-that-dropped-never-crediting-its-replacement", async () =>
        {
            var (server, workers, raw, _) = await E6EventRelay(); await using var closing = server;
            var first = await E6RelayedHold(raw, workers, 0, "S1", 0);
            first.Worker.Send(E6WorkerEvents(first.Held, [1]));
            await S6Until(() => E6Events(raw).Length == 1, "the first event");
            first.Worker.Dispose();
            await raw.Next<LoadingMessage>();
            var next = await W6WorkerAt(workers, 1); var held = await next.Next<HoldMessage>(); W6BindPlaced(next, held);
            await raw.Next<BoundMessage>();
            next.Send(E6WorkerEvents(held, [2]));
            await S6Until(() => E6Events(raw).Length == 2, "the second event");
            raw.Send(new AckMessage(1));
            await S6Settled(server);
            Equal(0, E6AcksOf(next).Length, "the dropped link's ticket credits nobody");
            raw.Send(new AckMessage(1));
            await S6Until(() => E6AcksOf(next).Length == 1, "the replacement's own ticket");
            Sequence([held.Origin + "/1"], E6AcksOf(next), "credited once");
        }),
        Case("remote.relay.events-retire-the-tabs-origin-only-once-its-tickets-drained-closed-upstream-after-the-last-ack", async () =>
        {
            var (server, workers, raw, _) = await E6EventRelay(); await using var closing = server;
            var (worker, held) = await E6RelayedHold(raw, workers, 0, "S1", 0);
            worker.Send(E6WorkerEvents(held, [1]));
            await S6Until(() => E6Events(raw).Length == 1, "the event");
            raw.Send(new ReleaseMessage(0)); await worker.Next<ReleaseMessage>(); await S6Settled(server);
            Equal(0, worker.Count<ClosedMessage>(), "the origin stays while a ticket is out");
            raw.Send(new AckMessage(1));
            await S6Until(() => worker.Count<ClosedMessage>() == 1, "closed upstream");
            Equal(held.Origin, worker.Log.OfType<ClosedMessage>().Single().Origin, "naming the origin");
        }),
        Case("remote.relay.events-drop-the-tabs-tickets-when-it-closes-one-closed-upstream-later-events-of-the-origin-ignored", async () =>
        {
            var (server, workers, raw, _) = await E6EventRelay(); await using var closing = server;
            var (worker, held) = await E6RelayedHold(raw, workers, 0, "S1", 0);
            worker.Send(E6WorkerEvents(held, [1]));
            await S6Until(() => E6Events(raw).Length == 1, "the event");
            raw.Dispose();
            await S6Until(() => worker.Count<ClosedMessage>() == 1, "closed upstream"); await S6Settled(server);
            Equal(held.Origin, worker.Log.OfType<ClosedMessage>().Single().Origin, "one closed, naming the origin");
            worker.Send(E6WorkerEvents(held, [2]));
            await S6Settled(server);
            Check(!worker.Closed, "a later event of the closed origin is ignored");
        }),

        // --- across real hosts ---
        Case("remote.relay.events-across-real-hosts-reach-a-tab-from-a-worker-through-the-relay-and-through-the-servers-re-exposure", async () =>
        {
            var paddles = new List<IEventSource>();
            var (tab, server, workers, _) = await E6Relayed(
                worker: h => h.Serve(E6PlacedMatch, (_, _, self) =>
                {
                    var paddle = self.Events(); lock (paddles) paddles.Add(paddle);
                    return new(new Dictionary<string, object?> { ["paddle"] = paddle }, "player");
                }),
                server: h => h.Serve(E6Lobby, (parameters, _, self) => new(new Dictionary<string, object?>
                {
                    ["paddle"] = self.Get(E6PlacedMatch, new() { ["session"] = parameters["session"]!.DeepClone() }).Event("paddle"),
                }, "player")));
            await using var closeTab = tab; await using var closeServer = server;
            using var direct = await tab.GetAsync(E6PlacedMatch, new() { ["session"] = "S1" }, S6Ctx);
            using var lobby = await tab.GetAsync(E6Lobby, new() { ["session"] = "S1" }, S6Ctx);
            var seen = new List<string>(); var stamps = new HashSet<long>();
            await direct.Event("paddle").OnAsync(e => { lock (seen) { seen.Add("direct" + e.Value!["y"]!.ToJsonString()); stamps.Add(e.At); } }, S6Ctx);
            await lobby.Event("paddle").OnAsync(e => { lock (seen) { seen.Add("lobby" + e.Value!["y"]!.ToJsonString()); stamps.Add(e.At); } }, S6Ctx);
            // The tab's direct hold and the server's hold for the lobby are two holds on the worker's one object.
            await S6Settled(tab, server);
            IEventSource paddle; lock (paddles) paddle = paddles[^1];
            paddle.Emit(S6Y(1));
            await S6Until(() => S6Seen(seen).Length == 2, "both deliveries");
            Sequence(["direct1", "lobby1"], S6Seen(seen).Order(StringComparer.Ordinal), "through the relay and through the re-exposure");
            Equal(1, stamps.Count, "the stamp is the worker's, unchanged");
            foreach (var worker in workers.ToArray()) await worker.CloseAsync(S6Ctx);
        }),
        Case("remote.relay.window-a-real-workers-finite-window-is-never-exceeded-when-a-hold-with-a-forwarded-call-is-released", async () =>
        {
            var waiter = Contract.Remote("app.waiter", new()
            {
                Version = 1, Params = new() { ["session"] = Str(), ["id"] = Int() }, HostAddress = values => Session.Address(("id", values["session"])),
                Variants = new() { ["v"] = new() { Methods = { ["wait"] = Method(null, Int()) } } },
            });
            var runs = new List<int>();
            var (tab, server, workers, errors) = await E6Relayed(workerCallWindow: 1, worker: h => h.Serve(waiter, (parameters, _, _) =>
            {
                var id = parameters["id"]!.GetValue<int>();
                return new(new Dictionary<string, object?>
                {
                    ["wait"] = (HostMethod)(async (_, ctx) =>
                    {
                        lock (runs) runs.Add(id);
                        try { await Task.Delay(Timeout.Infinite, ctx.AbortSignal); } catch (OperationCanceledException) { }
                        return JsonValue.Create(id);
                    }),
                }, "v");
            }));
            await using var closeTab = tab; await using var closeServer = server;
            var a = await tab.GetAsync(waiter, new() { ["session"] = "S1", ["id"] = 1 }, S6Ctx);
            using var b = await tab.GetAsync(waiter, new() { ["session"] = "S1", ["id"] = 2 }, S6Ctx);
            _ = a.CallAsync("wait", null, S6Ctx).ContinueWith(_ => { });
            await S6Until(() => { lock (runs) return runs.Count == 1; }, "the first call running");
            using var second = new CancellationTokenSource();
            _ = b.CallAsync("wait", null, ChordContext.WithAbortSignal(second.Token, S6Ctx)).ContinueWith(_ => { });
            await S6Settled(tab, server);
            // The worker's window of 1: b's call waits at the relay.
            lock (runs) Sequence([1], runs, "one call at the worker");
            a.Release();
            await S6Until(() => { lock (runs) return runs.Count == 2; }, "the waiting call running");
            lock (runs) Sequence([1, 2], runs, "the release went before the next call");
            await S6Settled(tab, server);
            lock (errors) Equal(0, errors.Count, "no protocol error: " + string.Join("; ", errors.Select(error => error.Kind + " " + error.Error?.Message)));
            Equal("live", b.State.Value.Kind, "the other reference stays live");
            second.Cancel();
            foreach (var worker in workers.ToArray()) await worker.CloseAsync(S6Ctx);
        }),
    ];
}
