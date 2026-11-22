// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/test/remote/calls.test.ts, consumer.test.ts ("the call window at the
// consumer"), host-link.test.ts ("call windows by trust"), provider.test.ts ("the call limit", the derive regression), relay.test.ts
// ("relayed calls in the upstream window"), hold.test.ts (binding fences), packages/chord/test/host/route.test.ts ("resolve's opened").
// Ported by reading, against real hosts and raw protocol peers; cases named 160-* are authored for the native port.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Remote;
using PiSharp.Chord.State;
using static PiSharp.Chord.Contracts.Schemas;
using static ChordSupport;

internal static partial class Program
{
    private sealed class W6Call(string name, List<string> sent) : IQueuedCall
    {
        internal readonly string Name = name;
        internal string Owner = "";
        public void Send() => sent.Add(Name);
    }

    /// <summary>A server type whose <c>wait</c> runs until its call is aborted (counted) and whose <c>ping</c> answers at once.</summary>
    private static readonly Contract W6Waits = Contract.Remote("app.waits", new()
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(),
        Variants = new() { ["v"] = new() { Methods = { ["wait"] = Method(), ["ping"] = Method(null, Int()) } } },
    });
    private sealed class W6WaitProbe { internal int Waits, Aborted; internal bool ProbeTx; internal string? TxAfterAbort; }
    private static void W6ServeWaits(IFacetHost h, W6WaitProbe probe) => h.Serve(W6Waits, (_, _, self) => new(new Dictionary<string, object?>
    {
        ["wait"] = (HostMethod)(async (_, ctx) =>
        {
            Interlocked.Increment(ref probe.Waits);
            try { await Task.Delay(Timeout.Infinite, ctx.AbortSignal); }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref probe.Aborted);
                if (!probe.ProbeTx) throw;
                try { await self.TxAsync<object?>(_ => Task.FromResult<object?>(null), ctx.Context); probe.TxAfterAbort = "ran"; }
                catch (Exception error) { probe.TxAfterAbort = error is ChordError chord ? chord.Code : error.GetType().Name; }
                throw;
            }
            return null;
        }),
        ["ping"] = (HostMethod)((_, _) => Task.FromResult<object?>(JsonValue.Create(1))),
    }, "v"));

    /// <summary>A server serving <see cref="W6Waits"/> and a raw peer on a link it accepted (or connected to, as its parent).</summary>
    private static async Task<(ChordHost Server, S6Peer Peer, List<HostError> Errors, W6WaitProbe Probe)> W6WaitServer(
        bool parent = false, int? callWindow = null, bool trusted = false, long? peerCalls = 256)
    {
        var errors = new List<HostError>(); var probe = new W6WaitProbe();
        var server = S6Host(Server.Address(), errors, callWindow: callWindow);
        await server.LoadAsync([new("@test/app", Facet.Define((h, _) => W6ServeWaits(h, probe)))], S6Ctx);
        var pair = MemoryLinkPair.Create(); var peer = new S6Peer(pair.Second, peerCalls, autoAck: true);
        if (parent) server.Connect(pair.First, new() { Principal = new() { ["kind"] = "owner" }, Trusted = trusted }, S6Ctx);
        else server.Accept(pair.First, new() { Principal = new() { ["kind"] = "owner" }, Trusted = trusted }, S6Ctx);
        return (server, peer, errors, probe);
    }

    private static readonly int W6Wait = S6MethodIndex(W6Waits, "v", "wait");
    private static readonly int W6PlacedWait = WireNames(PlacedNotes, "editor").Methods["wait"];

    /// <summary>A server routing the session kind to raw workers (each announcing <paramref name="workerCalls"/>), on links it gives as
    /// <paramref name="trusted"/>.</summary>
    private static async Task<(ChordHost Server, List<S6Peer> Workers, List<HostError> Errors)> W6Relay(long? workerCalls = 256, bool trusted = false,
        PiSharp.Telemetry.InMemoryTelemetryContext? telemetry = null)
    {
        var workers = new List<S6Peer>(); var errors = new List<HostError>();
        var server = S6Host(Server.Address(), errors, telemetry);
        server.Route(Session, new()
        {
            Authorize = (_, _) => true,
            Resolve = (_, _) =>
            {
                var pair = MemoryLinkPair.Create(); var peer = new S6Peer(pair.Second, workerCalls);
                lock (workers) workers.Add(peer);
                return Task.FromResult(new ResolvedLink(pair.First, new() { Principal = new() { ["kind"] = "worker" }, Trusted = trusted }));
            },
        }, S6Ctx);
        await server.LoadAsync([], S6Ctx);
        return (server, workers, errors);
    }

    /// <summary>A raw tab on a new link the server accepts as an owner; it acknowledges bound and state by itself unless told not to.</summary>
    private static S6Peer W6Tab(ChordHost server, bool trusted = false, bool ack = true)
    {
        var pair = MemoryLinkPair.Create();
        server.Accept(pair.First, new() { Principal = new() { ["kind"] = "owner" }, Trusted = trusted }, S6Ctx);
        return new S6Peer(pair.Second, autoAck: ack);
    }

    private static HoldMessage W6HoldPlaced(long hold, string session = "S1") => new(hold, PlacedNotes.Name, PlacedNotes.Version, PlacedNotes.Fingerprint,
        new Part.Text("{\"session\":\"" + session + "\"}"), new WireAddress("session", new JsonObject { ["id"] = session }));

    private static async Task<S6Peer> W6WorkerAt(List<S6Peer> workers, int index = 0)
    {
        await S6Until(() => { lock (workers) return workers.Count > index; }, "the route resolving");
        lock (workers) return workers[index];
    }

    /// <summary>Binds <paramref name="held"/> on a raw worker with the placed notes' editor (or reader) variant.</summary>
    private static void W6BindPlaced(S6Peer worker, HoldMessage held, int count = 0, string variant = "editor")
    {
        var names = WireNames(PlacedNotes, variant);
        worker.Send(new BoundMessage([new BoundEntry(held.Hold, names.Variant,
            [new(names.State["notes"], new Part.Text("[]")), new(names.State["count"], new Part.Text(count.ToString()))], [])]) { Origin = held.Origin });
    }

    private static long[] W6CallsOf(S6Peer peer) => [.. peer.Log.OfType<CallMessage>().Select(call => call.Call)];
    private static string[] W6AnswersOf(S6Peer peer) => [.. peer.Log.Select(message => message switch
    {
        ResultMessage result => result.Call + " ok",
        ErrorMessage error => error.Call + " " + ChordJson.AsString(error.Error.Json["error"]?["message"] ?? error.Error.Json["message"]),
        _ => null,
    }).OfType<string>()];
    private static WireError W6Chord(string code, string message) => WireError.FromException(new ChordError(code, message));
    private static void W6CallFrom(S6Peer raw, IEnumerable<int> ids, long hold = 0)
    { foreach (var call in ids) raw.Send(new CallMessage(call, hold, W6PlacedWait)); }

    /// <summary>A raw tab holding the placed notes of S1, bound through the relay at a raw worker announcing 2 calls.</summary>
    private static async Task<(ChordHost Server, S6Peer Raw, List<S6Peer> Workers, S6Peer Worker, HoldMessage Held)> W6Bound(bool trusted = false, bool ack = true)
    {
        var (server, workers, _) = await W6Relay(workerCalls: 2);
        var raw = W6Tab(server, trusted, ack);
        raw.Send(W6HoldPlaced(0));
        var worker = await W6WorkerAt(workers); var held = await worker.Next<HoldMessage>();
        W6BindPlaced(worker, held);
        await raw.Next<BoundMessage>();
        if (!ack) raw.Send(new AckMessage());
        return (server, raw, workers, worker, held);
    }

    private static readonly Contract W6Kept = Contract.Remote("app.kept", new()
    {
        Version = 1, Params = new(), HostAddress = _ => Server.Address(),
        Variants = new()
        {
            ["a"] = new() { State = { ["label"] = Str() }, Methods = { ["bump"] = Method(null, Int()), ["ping"] = Method(null, Int()) } },
            ["b"] = new() { State = { ["label"] = Str() }, Methods = { ["ping"] = Method(null, Int()) } },
        },
    });
    private static PluginFacet W6KeptFacet(int n, string variant, List<string> ran, List<ISelf>? selves = null) => new("@test/app", Facet.Define((h, _) =>
        h.Serve(W6Kept, (_, _, self) =>
        {
            if (selves is not null) lock (selves) selves.Add(self);
            var members = new Dictionary<string, object?>
            {
                ["label"] = Watchables.Constant(JsonValue.Create("v" + n)),
                ["ping"] = (HostMethod)((_, _) => { lock (ran) ran.Add("ping"); return Task.FromResult<object?>(JsonValue.Create(n)); }),
            };
            if (variant == "a") members["bump"] = (HostMethod)((_, _) => { lock (ran) ran.Add("bump"); return Task.FromResult<object?>(JsonValue.Create(n)); });
            return new(members, variant);
        })));

    private static IEnumerable<(string, Func<Task>)> RemoteSync160WindowCases() =>
    [
        // --- calls.test.ts ---
        Case("remote.calls.sends-every-call-at-once-to-a-peer-that-announced-no-window-after-its-hello", () =>
        {
            var sent = new List<string>(); var window = new CallWindow<W6Call>();
            window.Submit(new W6Call("early", sent));
            Equal(0, sent.Count, "before the hello every call waits");
            window.Opened(null);
            for (var call = 0; call < 300; call++) window.Submit(new W6Call(call.ToString(), sent));
            Equal(301, sent.Count, "no window: every call goes at once");
        }),
        Case("remote.calls.takes-out-the-waiting-calls-a-predicate-matches-in-order-and-leaves-the-rest-queued", () =>
        {
            var sent = new List<string>(); var window = new CallWindow<W6Call>();
            window.Opened(1);
            foreach (var (name, owner) in new[] { ("A1", "a"), ("A2", "a"), ("B1", "b"), ("A3", "a") }) window.Submit(new W6Call(name, sent) { Owner = owner });
            Sequence(["A2", "A3"], window.RemoveWhere(queued => queued.Owner == "a").Select(call => call.Name), "the owner's waiting calls, in order");
            window.Freed();
            Sequence(["A1", "B1"], sent, "the other owner's call takes the freed slot");
            Equal(0, window.RemoveWhere(_ => true).Count, "nothing waits any more");
        }),

        // --- consumer.test.ts: the call window at the consumer ---
        Case("remote.consumer.sends-every-call-at-once-to-a-peer-that-announced-no-window", async () =>
        {
            await using var client = S6Host(); await client.LoadAsync([], S6Ctx);
            var pair = MemoryLinkPair.Create(); client.Connect(pair.First, new() { Principal = S6ServerPrincipal }, S6Ctx);
            using var peer = new S6Peer(pair.Second, calls: null);
            var got = client.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, S6Ctx);
            var hold = await peer.Next<HoldMessage>(); W6BindPlaced(peer, hold);
            using var notes = await got;
            for (var call = 0; call < 300; call++) _ = notes.CallAsync("wait", null, S6Ctx);
            await S6Until(() => peer.Count<CallMessage>() == 300, "300 calls in flight");
            Check(peer.Log.OfType<CallMessage>().All(call => call.Hold == hold.Hold), "all on its hold");
        }),
        Case("remote.consumer.sends-the-calls-parked-while-rebinding-after-the-ack-of-the-new-bound", async () =>
        {
            await using var client = S6Host(); await client.LoadAsync([], S6Ctx);
            var pair = MemoryLinkPair.Create(); client.Connect(pair.First, new() { Principal = S6ServerPrincipal }, S6Ctx);
            using var peer = new S6Peer(pair.Second, calls: 1);
            var got = client.GetAsync(PlacedNotes, new() { ["session"] = "S1" }, S6Ctx);
            var hold = await peer.Next<HoldMessage>(); W6BindPlaced(peer, hold);
            using var one = await got; await peer.Next<AckMessage>();
            peer.Send(new LoadingMessage(hold.Hold));
            await S6Until(() => one.State.Value.Kind == "loading", "the rebinding");
            _ = one.CallAsync("wait", null, S6Ctx);
            await S6Settled(client);
            var before = peer.Log.Length;
            W6BindPlaced(peer, hold);
            await S6Until(() => peer.Log.Length >= before + 2, "the ack and the parked call");
            Sequence(["ack", "call"], peer.Log.Skip(before).Select(message => message.Kind), "the ack precedes the parked call");
            Equal(hold.Hold, ((CallMessage)peer.Log[before + 1]).Hold, "the parked call's hold");
        }),

        // --- host-link.test.ts and provider.test.ts: call windows by trust ---
        Case("remote.host-link.call-windows-announce-none-on-a-link-given-as-trusted-the-hosts-callwindow-otherwise-accept-and-connect", async () =>
        {
            foreach (var parent in new[] { false, true })
            {
                var trusted = await W6WaitServer(parent, callWindow: 8, trusted: true); await using var a = trusted.Server;
                Equal(null, (await trusted.Peer.Next<HelloMessage>()).Calls, $"trusted (parent {parent}): no window announced");
                var untrusted = await W6WaitServer(parent, callWindow: 8); await using var b = untrusted.Server;
                Equal(8L, (await untrusted.Peer.Next<HelloMessage>()).Calls, $"untrusted (parent {parent}): the host's CallWindow");
            }
            var byDefault = await W6WaitServer(); await using var c = byDefault.Server;
            Equal(10_000L, (await byDefault.Peer.Next<HelloMessage>()).Calls, "10,000 by default");
        }),
        Case("remote.host-link.call-windows-announce-none-on-a-routes-link-its-resolve-gave-as-trusted-the-hosts-callwindow-otherwise", async () =>
        {
            foreach (var trusted in new[] { true, false })
            {
                var (server, workers, _) = await W6Relay(trusted: trusted); await using var closing = server;
                using var reference = server.Hold(PlacedNotes, new() { ["session"] = "S1" }, S6Ctx);
                var worker = await W6WorkerAt(workers); await worker.Next<HoldMessage>();
                Equal(trusted ? null : 10_000L, (await worker.Next<HelloMessage>()).Calls, $"route link trusted {trusted}");
            }
        }),
        Case("remote.host-link.call-windows-asymmetric-trust-a-client-trusting-its-server-keeps-within-the-servers-window", async () =>
        {
            var errors = new List<HostError>(); var probe = new W6WaitProbe();
            await using var server = S6Host(Server.Address(), errors, callWindow: 2);
            await server.LoadAsync([new("@test/app", Facet.Define((h, _) => W6ServeWaits(h, probe)))], S6Ctx);
            await using var client = S6Host(errors: errors); await client.LoadAsync([], S6Ctx);
            var pair = MemoryLinkPair.Create();
            server.Accept(pair.First, new() { Principal = new() { ["kind"] = "owner" } }, S6Ctx);
            client.Connect(pair.Second, new() { Principal = S6ServerPrincipal, Trusted = true }, S6Ctx);
            using var waits = await client.GetAsync(W6Waits, new(), S6Ctx);
            using var first = new CancellationTokenSource();
            _ = waits.CallAsync("wait", null, ChordContext.WithAbortSignal(first.Token, S6Ctx)).ContinueWith(_ => { });
            for (var call = 0; call < 2; call++) _ = waits.CallAsync("wait", null, S6Ctx).ContinueWith(_ => { });
            await S6Until(() => probe.Waits == 2, "two calls running"); await S6Settled(server, client);
            Equal(2, probe.Waits, "the third waits for the server's window of 2");
            first.Cancel();
            await S6Until(() => probe.Waits == 3, "the third call running");
            lock (errors) Equal(0, errors.Count, "no protocol error: " + string.Join("; ", errors.Select(error => error.Kind + " " + error.Error?.Message)));
        }),
        Case("remote.provider.call-limit-counts-calls-per-origin-against-n-8-on-each-of-two-origins-run-one-more-on-one-closes-the-link", async () =>
        {
            var (server, peer, errors, probe) = await W6WaitServer(parent: true, callWindow: 8); await using var closing = server;
            foreach (var origin in new long[] { 1, 2 })
            {
                peer.Send(S6Hold(W6Waits) with { Origin = origin }); await peer.Next<BoundMessage>(bound => bound.Origin == origin);
                for (var id = 0; id < 8; id++) peer.Send(new CallMessage(id, 0, W6Wait) { Origin = origin });
            }
            await S6Until(() => probe.Waits == 16, "8 calls on each origin"); Check(!peer.Closed, "both origins are within their windows");
            peer.Send(new CallMessage(8, 0, W6Wait) { Origin = 1 });
            await S6Until(() => peer.Closed, "the link closing");
            lock (errors) Check(errors.Any(error => error.Kind == "protocol_error" && error.Error!.Message.Contains("call 8: more than 8 calls", StringComparison.Ordinal)),
                "a protocol error names the call and the window");
        }),
        Case("remote.provider.call-limit-closes-an-untrusted-link-for-one-call-more-than-the-window", async () =>
        {
            var (server, peer, errors, probe) = await W6WaitServer(callWindow: 8); await using var closing = server;
            peer.Send(S6Hold(W6Waits)); await peer.Next<BoundMessage>();
            for (var id = 0; id <= 8; id++) peer.Send(new CallMessage(id, 0, W6Wait));
            await S6Until(() => peer.Closed, "the link closing");
            Equal(8, probe.Waits, "the window's calls ran");
            lock (errors) Check(errors.Any(error => error.Kind == "protocol_error" && error.Error!.Message.Contains("call 8: more than 8 calls", StringComparison.Ordinal)),
                "a protocol error names the call and the window");
        }),
        Case("remote.provider.call-limit-never-closes-a-trusted-link-for-running-calls-300-run-where-the-window-would-be-8", async () =>
        {
            var (server, peer, errors, probe) = await W6WaitServer(callWindow: 8, trusted: true); await using var closing = server;
            peer.Send(S6Hold(W6Waits)); await peer.Next<BoundMessage>();
            for (var id = 0; id < 300; id++) peer.Send(new CallMessage(id, 0, W6Wait));
            await S6Until(() => probe.Waits == 300, "300 calls running");
            Check(!peer.Closed, "the trusted link stays open");
            lock (errors) Equal(0, errors.Count, "no error");
        }),
        Case("remote.provider.160-a-link-closing-aborts-its-inbound-calls-and-a-transaction-under-the-aborted-context-is-refused", async () =>
        {
            // The discovery report's open item 5.8: a served call's context is aborted when its link closes (ProviderSide.Close cancels
            // it), and a transaction started under that aborted context does not run.
            // 1. The closing link held the object's last hold: the object ends with it, and the transaction is refused released.
            var (server, peer, errors, probe) = await W6WaitServer(); await using var closing = server; probe.ProbeTx = true;
            peer.Send(S6Hold(W6Waits)); await peer.Next<BoundMessage>();
            peer.Send(new CallMessage(0, 0, W6Wait));
            await S6Until(() => probe.Waits == 1, "the call running");
            peer.Dispose();
            await S6Until(() => probe.Aborted == 1 && probe.TxAfterAbort is not null, "the call's abort");
            Equal("released", probe.TxAfterAbort, "the object ended with its last hold: its transaction is refused");
            // 2. Another link of the same principal still holds the object: it stays live, the call is aborted all the same, and the
            // transaction under its aborted context is cancelled (reported transaction_cancelled).
            probe.TxAfterAbort = null;
            var keeping = MemoryLinkPair.Create(); server.Accept(keeping.First, new() { Principal = new() { ["kind"] = "owner" } }, S6Ctx);
            using var keeper = new S6Peer(keeping.Second, autoAck: true); keeper.Send(S6Hold(W6Waits)); await keeper.Next<BoundMessage>();
            var again = MemoryLinkPair.Create(); server.Accept(again.First, new() { Principal = new() { ["kind"] = "owner" } }, S6Ctx);
            var second = new S6Peer(again.Second, autoAck: true); second.Send(S6Hold(W6Waits)); await second.Next<BoundMessage>();
            second.Send(new CallMessage(0, 0, W6Wait));
            await S6Until(() => probe.Waits == 2, "the second call running");
            second.Dispose();
            await S6Until(() => probe.Aborted == 2 && probe.TxAfterAbort is not null, "the second call's abort");
            Check(probe.TxAfterAbort is "OperationCanceledException" or "TaskCanceledException",
                "a transaction under the aborted call context is cancelled, got: " + probe.TxAfterAbort);
            await S6Until(() => { lock (errors) return errors.Any(error => error.Kind == "transaction_cancelled"); }, "the host reporting the cancelled transaction");
            Check(!keeper.Closed, "the other link is untouched");
        }),
        Case("remote.provider.a-derive-read-between-two-changes-of-its-source-in-one-commit-travels-once", async () =>
        {
            var gauge = Contract.Remote("test.gauge", new()
            {
                Version = 1, Params = new(), HostAddress = _ => Server.Address(),
                Variants = new() { ["main"] = new() { State = { ["reading"] = Obj(("n", Int())) } } },
            });
            IWritable<int>? level = null; IWatchable<JsonNode?>? reading = null; var errors = new List<HostError>();
            var (server, client) = await S6Linked(h => h.Serve(gauge, (_, _, self) =>
            {
                var source = self.Writable(1); var derived = self.Derive([source], values => new JsonObject { ["n"] = (int)values[0]! });
                level = source; reading = derived;
                return new(new Dictionary<string, object?> { ["reading"] = derived }, "main");
            }), errors: errors);
            await using var closeServer = server; await using var closeClient = client;
            using var held = await client.GetAsync(gauge, new(), S6Ctx);
            Equal(1, held.Member("reading").Value!["n"]!.GetValue<int>(), "the snapshot");
            // One commit of the server: its source changes, something reads the derive, its source changes again.
            server.Dispatcher.Invoke(() => server.Batch.Commit(() =>
            {
                level!.Set(2); Equal(2, reading!.Value!["n"]!.GetValue<int>(), "read between the two changes"); level.Set(3);
            }, S6Ctx));
            await S6Until(() => held.Member("reading").Value!["n"]!.GetValue<int>() == 3, "the commit's value");
            await S6Settled(server, client);
            lock (errors) Equal(0, errors.Count, "no protocol error");
            Equal("live", held.State.Value.Kind, "the link stays open");
        }),

        // --- relay.test.ts: relayed calls in the upstream window ---
        Case("remote.relay.window-wait-per-upstream-window-the-workers-2-of-five-calls-the-rest-in-order-as-answers-free-slots", async () =>
        {
            var (server, raw, _, worker, held) = await W6Bound(); await using var closing = server;
            W6CallFrom(raw, [0, 1, 2, 3, 4]);
            await S6Until(() => W6CallsOf(worker).Length == 2, "two forwarded"); await S6Settled(server);
            Sequence([0L, 1L], W6CallsOf(worker), "the worker's window of 2");
            worker.Send(new ResultMessage(0) { Origin = held.Origin });
            worker.Send(new ErrorMessage(1, W6Chord("unavailable", "no")) { Origin = held.Origin });
            await S6Until(() => W6CallsOf(worker).Length == 4, "the next two"); await S6Settled(server);
            Sequence([0L, 1L, 2L, 3L], W6CallsOf(worker), "in order as answers free slots");
            Sequence(["0 ok", "1 no"], W6AnswersOf(raw), "the answers reach the tab");
            Check(!raw.Closed, "the tab's link stays open");
        }),
        Case("remote.relay.window-wait-per-upstream-window-from-a-trusted-downstream-link-too-none-announced-to-the-tab-2-upstream", async () =>
        {
            var (server, raw, _, worker, _) = await W6Bound(trusted: true); await using var closing = server;
            Equal(null, raw.Log.OfType<HelloMessage>().Single().Calls, "no window announced to the trusted tab");
            W6CallFrom(raw, Enumerable.Range(0, 20));
            await S6Until(() => W6CallsOf(worker).Length == 2, "two forwarded"); await S6Settled(server);
            Sequence([0L, 1L], W6CallsOf(worker), "the upstream window still holds");
            Check(!raw.Closed, "the tab's link stays open");
        }),
        Case("remote.relay.window-abort-a-waiting-call-leaves-the-queue-unsent-a-forwarded-one-is-aborted-upstream-and-frees-its-slot", async () =>
        {
            var (server, raw, _, worker, held) = await W6Bound(); await using var closing = server;
            W6CallFrom(raw, [0, 1, 2, 3]);
            await S6Until(() => W6CallsOf(worker).Length == 2, "two forwarded"); await S6Settled(server);
            raw.Send(new AbortMessage(2)); raw.Send(new AbortMessage(0));
            await S6Until(() => W6CallsOf(worker).Length == 3, "the freed slot's call"); await S6Settled(server);
            Sequence([0L, 1L, 3L], W6CallsOf(worker), "call 2 never went");
            var aborts = worker.Log.OfType<AbortMessage>().ToArray();
            Equal(1, aborts.Length, "one abort upstream"); Equal(0L, aborts[0].Call, "of the forwarded call"); Equal(held.Origin, aborts[0].Origin, "with the origin");
        }),
        Case("remote.relay.window-release-the-holds-waiting-calls-go-unsent-its-forwarded-ones-free-their-slots-for-the-origins-others", async () =>
        {
            var (server, raw, _, worker, held) = await W6Bound(); await using var closing = server;
            raw.Send(W6HoldPlaced(1)); var other = await worker.Next<HoldMessage>(); W6BindPlaced(worker, other); await raw.Next<BoundMessage>();
            W6CallFrom(raw, [0, 1]); W6CallFrom(raw, [2], 1); W6CallFrom(raw, [3]);
            await S6Until(() => W6CallsOf(worker).Length == 2, "two forwarded"); await S6Settled(server);
            Sequence([0L, 1L], W6CallsOf(worker), "the window of 2");
            raw.Send(new ReleaseMessage(0));
            await S6Until(() => W6CallsOf(worker).Length == 3, "the other hold's call"); await S6Settled(server);
            var releases = worker.Log.OfType<ReleaseMessage>().ToArray();
            Equal(1, releases.Length, "one release upstream"); Equal(held.Origin, releases[0].Origin, "with the origin");
            Sequence([0L, 1L, 2L], W6CallsOf(worker), "the released hold's waiting call never went");
            // The release first: the upstream counts the released hold's forwarded calls until it arrives.
            Sequence(["call 0", "call 1", "release", "call 2"], worker.Log.Select(message => message switch
            { ReleaseMessage => "release", CallMessage call => "call " + call.Call, _ => null }).OfType<string>(), "the release precedes the next call");
        }),
        Case("remote.relay.window-a-downstream-close-waiting-calls-never-go-the-worker-gets-one-closed", async () =>
        {
            var (server, raw, _, worker, held) = await W6Bound(); await using var closing = server;
            W6CallFrom(raw, [0, 1, 2]);
            await S6Until(() => W6CallsOf(worker).Length == 2, "two forwarded"); await S6Settled(server);
            raw.Dispose();
            await worker.Next<ClosedMessage>(); await S6Settled(server);
            Sequence([0L, 1L], W6CallsOf(worker), "the waiting call never went");
            var ends = worker.Log.Where(message => message is ReleaseMessage or AbortMessage or ClosedMessage).ToArray();
            Equal(1, ends.Length, "one closed, no release or abort"); Equal(held.Origin, ends[0].Origin, "naming the origin");
        }),
        Case("remote.relay.window-an-upstream-drop-forwarded-and-waiting-calls-fail-unavailable-none-sent-on-the-next-link", async () =>
        {
            var (server, raw, workers, worker, _) = await W6Bound(); await using var closing = server;
            W6CallFrom(raw, [0, 1, 2, 3]);
            await S6Until(() => W6CallsOf(worker).Length == 2, "two forwarded"); await S6Settled(server);
            worker.Dispose();
            await S6Until(() => W6AnswersOf(raw).Length == 4, "every call answered");
            Sequence(["0", "1", "2", "3"], W6AnswersOf(raw).Select(answer => answer.Split(' ')[0]).Order(), "forwarded and waiting calls fail");
            Check(raw.Log.OfType<ErrorMessage>().All(error => ChordJson.AsString(error.Error.Json["error"]!["code"]) == "unavailable"), "unavailable");
            await S6Until(() => raw.Count<LoadingMessage>() == 1, "one loading");
            var next = await W6WorkerAt(workers, 1); W6BindPlaced(next, await next.Next<HoldMessage>());
            await raw.Next<BoundMessage>(); await S6Settled(server);
            Equal(0, W6CallsOf(next).Length, "no call is resent on the next link");
        }),
        Case("remote.relay.window-loading-the-waiting-calls-are-rejected-unavailable-the-forwarded-ones-kept", async () =>
        {
            var (server, raw, _, worker, held) = await W6Bound(); await using var closing = server;
            W6CallFrom(raw, [0, 1, 2, 3]);
            await S6Until(() => W6CallsOf(worker).Length == 2, "two forwarded"); await S6Settled(server);
            worker.Send(new LoadingMessage(held.Hold) { Origin = held.Origin });
            await S6Until(() => W6AnswersOf(raw).Length == 2, "the waiting calls answered");
            Sequence(["2 hold 0: rebinding", "3 hold 0: rebinding"], W6AnswersOf(raw), "rejected unavailable");
            await S6Until(() => raw.Count<LoadingMessage>() == 1, "the loading forwarded");
            Check(Array.FindIndex(raw.Log, m => m is ErrorMessage) < Array.FindIndex(raw.Log, m => m is LoadingMessage), "rejected before the loading is forwarded");
            W6BindPlaced(worker, held, 0, "reader");
            worker.Send(new ResultMessage(0) { Origin = held.Origin });
            await S6Until(() => W6AnswersOf(raw).Length == 3, "the kept call's answer"); await S6Settled(server);
            Sequence([0L, 1L], W6CallsOf(worker), "a bound of another variant gets none of them");
            Equal("0 ok", W6AnswersOf(raw)[^1], "the forwarded call was kept");
        }),
        Case("remote.relay.window-a-call-crossing-the-loading-notice-still-waiting-when-the-new-bounds-ack-passes-rejected-before-the-ack-goes-up", async () =>
        {
            var (server, raw, _, worker, held) = await W6Bound(ack: false); await using var closing = server;
            await worker.Next<AckMessage>();
            W6CallFrom(raw, [0, 1]);
            await S6Until(() => W6CallsOf(worker).Length == 2, "two forwarded");
            worker.Send(new LoadingMessage(held.Hold) { Origin = held.Origin });
            await raw.Next<LoadingMessage>();
            // The tab had not learnt of the loading when it called: the old indexes.
            W6CallFrom(raw, [2]);
            await S6Settled(server);
            W6BindPlaced(worker, held, 0, "reader");
            await raw.Next<BoundMessage>(); await S6Settled(server);
            Equal(0, W6AnswersOf(raw).Length, "nothing answered while the bound is unacknowledged");
            var acks = worker.Count<AckMessage>();
            raw.Send(new AckMessage());
            await S6Until(() => W6AnswersOf(raw).Length == 1, "the waiting call rejected");
            Sequence(["2 hold 0: rebound"], W6AnswersOf(raw), "rejected: it carries the old indexes");
            await S6Until(() => worker.Count<AckMessage>() == acks + 1, "the ack going up");
            // Made with the new binding: it waits and goes as a slot frees.
            W6CallFrom(raw, [3]);
            worker.Send(new ResultMessage(0) { Origin = held.Origin });
            await S6Until(() => W6CallsOf(worker).Length == 3, "the new binding's call"); await S6Settled(server);
            Sequence([0L, 1L, 3L], W6CallsOf(worker), "call 2 never went up");
        }),
        Case("remote.relay.window-failed-and-gone-the-waiting-calls-are-rejected-with-their-cause", async () =>
        {
            var (server, raw, _, worker, held) = await W6Bound(); await using var closing = server;
            raw.Send(W6HoldPlaced(1)); var other = await worker.Next<HoldMessage>(); W6BindPlaced(worker, other); await raw.Next<BoundMessage>();
            W6CallFrom(raw, [0, 1]); W6CallFrom(raw, [2]); W6CallFrom(raw, [3], 1);
            await S6Until(() => W6CallsOf(worker).Length == 2, "two forwarded"); await S6Settled(server);
            worker.Send(new FailedMessage(held.Hold, new("waiting", W6Chord("unavailable", "parked"))) { Origin = held.Origin });
            worker.Send(new GoneMessage(other.Hold, W6Chord("unavailable", "ended")) { Origin = other.Origin });
            await S6Until(() => W6AnswersOf(raw).Length == 2, "both waiting calls answered"); await S6Settled(server);
            Sequence(["2 parked", "3 ended"], W6AnswersOf(raw), "each with its hold's cause");
            Sequence([0L, 1L], W6CallsOf(worker), "neither went up");
        }),

        // --- hold.test.ts: binding fences ---
        Case("remote.hold.fences-each-unacknowledged-bound-a-b-c-before-any-ack-a-call-between-is-decoded-by-what-its-consumer-had-applied", async () =>
        {
            var ran = new List<string>();
            await using var server = S6Host(Server.Address()); await server.LoadAsync([W6KeptFacet(1, "a", ran)], S6Ctx);
            var pair = MemoryLinkPair.Create(); server.Accept(pair.First, new() { Principal = new() { ["kind"] = "owner" } }, S6Ctx);
            using var peer = new S6Peer(pair.Second);
            peer.Send(S6Hold(W6Kept)); await peer.Next<BoundMessage>(); peer.Send(new AckMessage());
            // A (variant a, acknowledged), then B (variant b) and C (variant a again), neither acknowledged.
            await server.ReloadAsync([W6KeptFacet(2, "b", ran)], S6Ctx);
            Equal(1L, (await peer.Next<BoundMessage>()).Holds[0].Variant, "B binds variant b");
            await server.ReloadAsync([W6KeptFacet(3, "a", ran)], S6Ctx);
            Equal(0L, (await peer.Next<BoundMessage>()).Holds[0].Variant, "C binds variant a");
            // Sent before B was applied: index 0 is A's bump.
            peer.Send(new CallMessage(0, 0, 0));
            Equal(3, Parts.Parse((await peer.Next<ResultMessage>()).Value!)!.GetValue<int>(), "answered by the current code");
            Sequence(["bump"], ran.ToArray(), "decoded by A's names");
            // B applied and acknowledged, C not yet: index 0 is B's ping (one fence would still decode it by A's names).
            peer.Send(new AckMessage()); peer.Send(new CallMessage(1, 0, 0));
            await peer.Next<ResultMessage>();
            Sequence(["bump", "ping"], ran.ToArray(), "decoded by B's names");
            // C acknowledged: index 0 is bump again.
            peer.Send(new AckMessage()); peer.Send(new CallMessage(2, 0, 0));
            await peer.Next<ResultMessage>();
            Sequence(["bump", "ping", "bump"], ran.ToArray(), "decoded by C's names");
            Check(!peer.Closed, "the link stays open");
        }),
        Case("remote.hold.drops-a-fence-with-the-ack-of-its-bound-a-consumer-that-never-calls-keeps-none", async () =>
        {
            // Upstream asserts the failures the fences kept are collected. Here the hold's fences are read directly: after every bound
            // was acknowledged the hold keeps none, whatever it was told before.
            var selves = new List<ISelf>(); var ran = new List<string>();
            await using var server = S6Host(Server.Address()); await server.LoadAsync([W6KeptFacet(1, "a", ran, selves)], S6Ctx);
            var pair = MemoryLinkPair.Create(); server.Accept(pair.First, new() { Principal = new() { ["kind"] = "owner" } }, S6Ctx);
            // It acknowledges every bound and sends no call.
            using var peer = new S6Peer(pair.Second, autoAck: true);
            peer.Send(S6Hold(W6Kept)); await peer.Next<BoundMessage>();
            IncomingHold Hold() => server.Dispatcher.Invoke(() => server.Objects.Values.SelectMany(obj => obj.Holders).OfType<IncomingHold>().Single());
            var fenced = 0;
            for (var round = 0; round < 4; round++)
            {
                ISelf live; lock (selves) live = selves[^1];
                S6FailLive(server, live);
                await peer.Next<FailedMessage>();
                peer.Send(new RetryMessage(0, 16));
                await peer.Next<BoundMessage>();
                // Each failure it is told is what the fence of the next bound keeps for calls sent before it, until that bound's ack.
                await S6Until(() => { var hold = Hold(); return server.Dispatcher.Invoke(() => hold.Acknowledged()); }, "the bound's ack ending its fence");
                fenced++;
            }
            Equal(4, fenced, "four rebinds, each fence gone with its ack");
            var last = Hold(); Equal(0, server.Dispatcher.Invoke(() => last.Fences), "no fence is left");
        }),

        // --- route.test.ts: resolve's opened ---
        Case("host.route.resolves-opened-is-told-the-peers-incarnation-at-the-handshake-before-the-peers-first-call-is-handled", async () =>
        {
            var who = Contract.Remote("app.who", new()
            {
                Version = 1, Params = new(), HostAddress = _ => Server.Address(),
                Variants = new() { ["v"] = new() { Methods = { ["incarnation"] = Method(null, Str()) } } },
            });
            var events = new List<string>(); ChordHost? worker = null;
            await using var server = S6Host(Server.Address());
            server.Route(Session, new()
            {
                Authorize = (_, _) => true,
                Resolve = async (address, resolving) =>
                {
                    var pair = MemoryLinkPair.Create(); var principal = new JsonObject { ["kind"] = "worker" };
                    Action<string> opened = incarnation => { lock (events) events.Add("opened " + incarnation); };
                    if (address.Key.Contains("dead", StringComparison.Ordinal))
                    {
                        pair.Second.Close();
                        return new ResolvedLink(pair.First, new() { Principal = principal }, opened);
                    }
                    var made = S6Host(address, principal: principal); worker = made;
                    made.Connect(pair.Second, new() { Principal = S6ServerPrincipal }, S6Ctx);
                    await made.LoadAsync([], S6Ctx);
                    // Its first call, made before the server has the link: it goes out with the handshake.
                    _ = Task.Run(async () =>
                    {
                        using var reference = await made.GetAsync(who, new(), S6Ctx);
                        var answer = (JsonNode?)await reference.CallAsync("incarnation", null, S6Ctx);
                        lock (events) events.Add("answered " + answer!.GetValue<string>());
                    });
                    return new ResolvedLink(pair.First, new() { Principal = principal }, opened);
                },
            }, S6Ctx);
            await server.LoadAsync([new("@test/server", Facet.Define((h, _) => h.Serve(who, (_, _, _) => new(new Dictionary<string, object?>
            {
                ["incarnation"] = (HostMethod)((_, ctx) => { lock (events) events.Add("handled " + ctx.Incarnation); return Task.FromResult<object?>(JsonValue.Create(ctx.Incarnation)); }),
            }, "v"))))], S6Ctx);
            using var held = server.Hold(PlacedNotes, new() { ["session"] = "S1" }, S6Ctx);
            await S6Until(() => { lock (events) return events.Count == 3; }, "the worker's call answered");
            var incarnation = worker!.Incarnation;
            lock (events) Sequence(["opened " + incarnation, "handled " + incarnation, "answered " + incarnation], events, "opened first");
            using var dead = server.Hold(PlacedNotes, new() { ["session"] = "dead" }, S6Ctx);
            await S6Until(() => dead.State.Value.Kind == "failed", "the dead link's failure"); await S6Settled(server);
            lock (events) Equal(3, events.Count, "never for a link closed before its handshake");
            await worker.CloseAsync(S6Ctx);
        }),
    ];
}
