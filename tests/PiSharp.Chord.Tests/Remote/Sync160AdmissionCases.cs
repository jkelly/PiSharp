// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): the admission cases of packages/chord/test/remote/relay.test.ts ("failed { denied }:
// the waiting calls are rejected forbidden/revoked") and path.test.ts ("a gated path", slice 14 §2.3), upstream 8270d9af6. Ported by
// reading. Upstream's path cases drive a Path with hand-made holders and links whose fences the test runs by hand; here the holders are
// hand-made IUpstreams and the links real HostLinks to raw peers, so a fence is a real ping and running it is the peer's pong (a fence
// that was cancelled still sent its ping: its pong then holds nothing, which is what these cases check of it). Path calls run on the
// host's dispatcher, where a fence's continuation runs.
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.Remote;
using static ChordSupport;
using DataFailure = PiSharp.Chord.Data.FailureCause;

internal static partial class Program
{
    /// <summary>A holder recording what it is told (upstream's <c>made(name)</c>).</summary>
    private sealed class P6Holder(string name, List<string> log, Dictionary<HostLink, string> links) : IUpstream
    {
        internal string Name => name;
        public Caller Caller { get; } = new(new JsonObject { ["kind"] = name }, name);
        public void Attach(HostLink link) { lock (log) log.Add($"{name}@{links[link]}"); }
        public void Dropped(ChordError error) { lock (log) log.Add($"{name} dropped"); }
        public void Unreachable(ChordError error) { lock (log) log.Add($"{name} unreachable"); }
        public void Withheld(DataFailure cause) { lock (log) log.Add($"{name} {cause.Kind.ToString().ToLowerInvariant()}"); }
        public void Admitted() { lock (log) log.Add($"{name} admitted"); }
    }

    /// <summary>A gate admitting the holders named in its set (upstream's <c>gate</c>).</summary>
    private sealed class P6Gate(HashSet<string> admitted, List<string> log, List<string> events) : IPathGate
    {
        public bool Joined(IUpstream holder) => admitted.Contains(((P6Holder)holder).Name);
        public void Left(IUpstream holder) { lock (log) log.Add($"{((P6Holder)holder).Name} left"); }
        public void Unlinked() { lock (events) events.Add("unlinked"); }
    }

    /// <summary>A gated path on a host, the holders' log, the path's events, and real links to raw peers (upstream <c>gated</c>).</summary>
    private sealed class P6World : IAsyncDisposable
    {
        internal required ChordHost Host { get; init; }
        internal RemotePath Path { get; set; } = null!;
        internal List<string> Log { get; } = [];
        internal List<string> Events { get; } = [];
        internal Dictionary<HostLink, string> Names { get; } = new(ReferenceEqualityComparer.Instance);
        internal Dictionary<string, S6Peer> Peers { get; } = new(StringComparer.Ordinal);

        internal P6Holder Made(string name) => new(name, Log, Names);
        internal void On(Action<RemotePath> work) => Host.Dispatcher.Invoke(() => work(Path));
        internal T On<T>(Func<RemotePath, T> work) => Host.Dispatcher.Invoke(() => work(Path));
        internal string[] LogNow { get { lock (Log) return [.. Log]; } }
        internal string[] EventsNow { get { lock (Events) return [.. Events]; } }

        /// <summary>A real link named <paramref name="name"/> to a raw peer, open once the peer greeted.</summary>
        internal async Task<HostLink> Link(string name)
        {
            var pair = MemoryLinkPair.Create();
            var link = Host.Dispatcher.Invoke(() => Host.MakeLink(pair.First, new() { Principal = new() { ["kind"] = "worker" } }, LinkRole.Route, ChordContext.Background));
            Names[link] = name;
            Peers[name] = new S6Peer(pair.Second);
            await S6Until(() => Host.Dispatcher.Invoke(() => link.Open), "the link opening");
            return link;
        }

        /// <summary>How many pings (fences) the peer of link <paramref name="name"/> got.</summary>
        internal int Pings(string name) => Peers[name].Count<PingMessage>();

        /// <summary>Runs the oldest fence of link <paramref name="name"/>: its peer answers a ping.</summary>
        internal async Task Pong(string name) { Peers[name].Send(new PongMessage()); await S6Settled(Host); }

        public async ValueTask DisposeAsync()
        {
            foreach (var peer in Peers.Values) peer.Dispose();
            await Host.CloseAsync(ChordContext.Background);
        }
    }

    private static async Task<P6World> P6Gated(params string[] admitted)
    {
        var host = S6Host(Server.Address());
        await host.LoadAsync([], ChordContext.Background);
        var world = new P6World { Host = host };
        world.Path = new RemotePath((kind, _) => { lock (world.Events) world.Events.Add(kind); }, null,
            _ => new P6Gate([.. admitted], world.Log, world.Events));
        return world;
    }

    private static readonly DataFailure P6Denied = new(FailureKind.Denied, new ChordError("forbidden", "no"));

    private static IEnumerable<(string, Func<Task>)> RemoteSync160AdmissionCases() =>
    [
        // --- relay.test.ts ---
        Case("remote.relay.window-failed-denied-the-waiting-calls-are-rejected-forbidden-revoked-as-calls-in-flight-are", async () =>
        {
            var (server, raw, _, worker, held) = await W6Bound(); await using var closing = server;
            W6CallFrom(raw, [0, 1, 2]);
            await S6Until(() => W6CallsOf(worker).Length == 2, "two forwarded"); await S6Settled(server);
            worker.Send(new FailedMessage(held.Hold, new("denied", W6Chord("forbidden", "no access"))) { Origin = held.Origin });
            await S6Until(() => W6AnswersOf(raw).Any(answer => answer.StartsWith("2 ", StringComparison.Ordinal)), "call 2 answered"); await S6Settled(server);
            // Call 2 waited for the worker's window: access was withdrawn under it too.
            var answer = raw.Log.OfType<ErrorMessage>().Single(message => message.Call == 2).Error.Json["error"]!;
            Equal("forbidden/revoked: hold 0: no access", $"{answer["code"]}/{answer["reason"]}: {answer["message"]}", "revoked");
            Sequence([0L, 1L], W6CallsOf(worker), "call 2 never went up");
        }),

        // --- path.test.ts: a gated path (slice 14 §2.3) ---
        Case("remote.path.gated.holds-only-what-its-gate-admits-on-its-links-and-wants-a-link-only-for-those", async () =>
        {
            await using var w = await P6Gated("a");
            P6Holder a = w.Made("a"), b = w.Made("b");
            w.On(path => path.Attach(b));
            // Withheld: no link is asked for it.
            Equal(" True False", $"{string.Join(",", w.EventsNow)} {w.On(path => path.Held)} {w.On(path => path.Wanted)}", "withheld");
            w.On(path => path.Attach(a));
            Equal("attached True", $"{string.Join(",", w.EventsNow)} {w.On(path => path.Wanted)}", "a link wanted");
            var first = await w.Link("1");
            w.On(path => path.Connected(first));
            Equal("a@1", string.Join(",", w.LogNow), "a held on the link");
            w.On(path => path.Forward(b));
            Equal("a@1,b admitted,b@1", string.Join(",", w.LogNow), "b forwarded");
            w.On(path => path.Disconnected(first));
            // Its gate is told after the holders, before the route: it may take back what it cannot forward on a new link.
            Equal("attached,unlinked,dropped", string.Join(",", w.EventsNow), "the gate before the route");
            w.On(path => path.Unreachable(new ChordError("unavailable", "down")));
            // Upstream's holders are kept in a Set in their attach order; the native path's holders are a hash set: the same four
            // entries, in its order.
            Equal("a dropped,a unreachable,b dropped,b unreachable", string.Join(",", w.LogNow.Skip(3).Order(StringComparer.Ordinal)), "dropped, then unreachable");
            w.On(path => path.Detach(a));
            Equal("a left", w.LogNow[^1], "left");
        }),
        Case("remote.path.gated.forwards-a-holder-a-denial-released-on-the-link-only-behind-a-fence-one-never-forwarded-needs-none", async () =>
        {
            await using var w = await P6Gated("a");
            P6Holder a = w.Made("a"), b = w.Made("b");
            w.On(path => { path.Attach(a); path.Attach(b); });
            var first = await w.Link("1");
            w.On(path => { path.Connected(first); path.Withhold(a, P6Denied); path.Withhold(b, P6Denied); });
            Equal("a@1,a denied,b denied", string.Join(",", w.LogNow), "both denied");
            w.On(path => { path.Forward(b); path.Forward(a); });
            await S6Settled(w.Host);
            // b was never on the link: held at once. a is told it is admitted, and waits for its fence.
            Equal("b admitted,b@1,a admitted 1", $"{string.Join(",", w.LogNow.Skip(3))} {w.Pings("1")}", "one fence, for a");
            await w.Pong("1");
            Equal("a@1", string.Join(",", w.LogNow.Skip(6)), "a held after its pong");
            // Denied again and allowed: it was on the link again, so a new fence.
            w.On(path => { path.Withhold(a, P6Denied); path.Forward(a); });
            await S6Settled(w.Host);
            Equal(2, w.Pings("1"), "a new fence");
        }),
        Case("remote.path.gated.a-holder-its-gate-takes-back-when-the-link-is-lost-wants-no-link-and-is-on-none-until-it-is-forwarded-again", async () =>
        {
            await using var w = await P6Gated("a");
            var a = w.Made("a");
            w.On(path => path.Attach(a));
            var first = await w.Link("1");
            w.On(path => path.Connected(first));
            w.On(path => path.Disconnected(first));
            // What a gate does in unlinked when it cannot read an allow.
            w.On(path => path.Withhold(a, new DataFailure(FailureKind.Waiting, new ChordError("unavailable", "source failed"))));
            Equal("True False", $"{w.On(path => path.Withholds(a))} {w.On(path => path.Wanted)}", "withheld, no link wanted");
            var second = await w.Link("2");
            w.On(path => path.Connected(second));
            Equal("a@1,a dropped,a waiting", string.Join(",", w.LogNow), "on no link");
            // Never on the second link: forwarded there with no fence.
            w.On(path => path.Forward(a));
            await S6Settled(w.Host);
            Equal("a admitted,a@2 0", $"{string.Join(",", w.LogNow.Skip(3))} {w.Pings("2")}", "held at once, no fence");
            Equal(1, w.EventsNow.Count(kind => kind == "unlinked"), "one unlinked");
        }),
        Case("remote.path.gated.a-denial-before-the-fence-ran-cancels-it-a-lost-link-drops-it-and-the-next-link-holds-what-is-admitted", async () =>
        {
            await using var w = await P6Gated("a");
            var a = w.Made("a");
            w.On(path => path.Attach(a));
            var first = await w.Link("1");
            w.On(path => { path.Connected(first); path.Withhold(a, P6Denied); path.Forward(a); path.Withhold(a, P6Denied); });
            await S6Settled(w.Host);
            // The first fence's ping went out; cancelled, its pong holds nothing.
            await w.Pong("1");
            Equal("a@1,a denied,a admitted,a denied", string.Join(",", w.LogNow), "the cancelled fence held nothing");
            w.On(path => path.Forward(a));
            await S6Settled(w.Host);
            Equal(2, w.Pings("1"), "a new fence");
            w.On(path => path.Disconnected(first));
            // Not held on the lost link (its fence was pending): not told of the drop twice, held on the next at once.
            var second = await w.Link("2");
            w.On(path => path.Connected(second));
            await w.Pong("1");
            Equal("a@1,a denied,a admitted,a denied,a admitted,a dropped,a@2", string.Join(",", w.LogNow), "held on the next link");
        }),
    ];
}
