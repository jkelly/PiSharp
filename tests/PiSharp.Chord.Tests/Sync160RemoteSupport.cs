using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Host;
using PiSharp.Chord.Link;
using PiSharp.Chord.State;
using PiSharp.Telemetry;
using static PiSharp.Chord.Contracts.Schemas;
using static ChordSupport;

// PiSharp-only (no upstream Pi source): the helpers of the chord-remote package's 1.6.0 cases (events, call windows by trust, binding
// fences, opened(incarnation), shared holds). They stand in for the fixtures Pi faac585884ae2c013a6b1400f3e9e77f10b34bac added to
// packages/chord/test/support.ts (Sample, Match, MatchV2, serveMatch, Ticker, NamedTicker, TickerView, serveTickers, ticking, recorder,
// until, waitLive, the raw peer's optional call window and its automatic acks), written against the native hosts.
//
// Turns. The upstream tests run on one JavaScript thread: what a test does between two awaits is one turn, and `await flushed()` lets the
// microtasks run. Here a host's work runs on its dispatcher and a test on a pool thread, so a test that needs "in one turn" runs that part
// with S6Turn (one dispatcher action), and S6Flushed waits until everything posted to the dispatcher so far ran (the event flush included).
internal static partial class Program
{
    /// <summary>An event's value in the tests: a paddle's position.</summary>
    internal static readonly JsonObject S6Sample = Obj(("y", Num()));
    internal static JsonObject S6Y(double y) => new() { ["y"] = y };
    internal static readonly JsonObject S6User = new() { ["kind"] = "user", ["id"] = 7 };
    internal static readonly JsonObject S6Guest = new() { ["kind"] = "guest" };
    internal static readonly JsonObject S6ServerPrincipal = new() { ["kind"] = "server" };

    /// <summary>A server type with events: a player has the score, two event members (ball, paddle) and a method; a watcher (a guest) the
    /// score only. The score is a derive over a writable, so a test commits synchronously.</summary>
    internal static readonly Contract S6Match = Contract.Remote("app.match", new()
    {
        Version = 1, Params = new() { ["id"] = Int() }, HostAddress = _ => Server.Address(),
        Variants = new()
        {
            ["player"] = new() { State = { ["score"] = Int() }, Events = { ["paddle"] = S6Sample, ["ball"] = S6Sample }, Methods = { ["bump"] = Method() } },
            ["watcher"] = new() { State = { ["score"] = Int() } },
        },
    });

    /// <summary><see cref="S6Match"/> with the paddle's values carrying a sample time: an older consumer gets them mapped.</summary>
    internal static readonly Contract S6MatchV2 = Contract.Evolve(S6Match, e => e.Event("player", "paddle", Obj(("y", Num()), ("t", Num()))));

    /// <summary>What a test reaches of each match attempt: its paddle and ball sources, its score's writable and its self.</summary>
    internal sealed class S6MatchProbe
    {
        internal readonly List<IEventSource> Paddles = [], Balls = [];
        internal readonly List<IWritable<int>> Scores = [];
        internal readonly List<ISelf> Selves = [];
        internal IEventSource Paddle => Paddles[^1];
        internal IEventSource Ball => Balls[^1];
        internal IWritable<int> Score => Scores[^1];
    }

    /// <summary>Serves <paramref name="type"/> (<see cref="S6Match"/> or an evolution of it); guests get the watcher variant.</summary>
    internal static void S6ServeMatch(IFacetHost h, S6MatchProbe probe, Contract? type = null, ServeOptions? options = null) =>
        h.Serve(type ?? S6Match, (_, principal, self) =>
        {
            var paddle = self.Events(); var ball = self.Events(); var writable = self.Writable(0);
            lock (probe) { probe.Paddles.Add(paddle); probe.Balls.Add(ball); probe.Scores.Add(writable); probe.Selves.Add(self); }
            var score = self.Derive([writable], values => JsonValue.Create((int)values[0]!));
            if (ChordJson.AsString(principal?["kind"]) == "guest") return new(new Dictionary<string, object?> { ["score"] = score }, "watcher");
            return new(new Dictionary<string, object?>
            {
                ["score"] = score, ["paddle"] = paddle, ["ball"] = ball, ["bump"] = (HostMethod)((_, _) => Task.FromResult<object?>(null)),
            }, "player");
        }, options);

    private static readonly StateDefinition S6TickerCounter = StateDefinition.Define("ticker-counter", new()
    { Version = 1, Schema = Obj(("n", Int())), Initial = () => new() { ["n"] = 0 } });

    /// <summary>A host-lifetime ticker: an event member, a count, and a method emitting inside a transaction that fails.</summary>
    internal static readonly Contract S6Ticker = Contract.Local("app.ticker", new()
    { Version = 1, Params = new(), Members = new() { ["ticks"] = Event(S6Sample), ["count"] = Int(), ["failing"] = Method(S6Sample) } });

    /// <summary>A ticker per name (lives while referenced): what a test makes and drops references to.</summary>
    internal static readonly Contract S6NamedTicker = Contract.Local("app.named-ticker", new()
    { Version = 1, Params = new() { ["name"] = Str() }, Members = new() { ["ticks"] = Event(S6Sample) } });

    /// <summary>Re-exposes the named ticker's member (a view of it).</summary>
    internal static readonly Contract S6TickerView = Contract.Local("app.ticker-view", new()
    { Version = 1, Params = new() { ["name"] = Str() }, Members = new() { ["ticks"] = Event(S6Sample) } });

    /// <summary>What a test reaches of its ticker objects: each attempt's sources and self.</summary>
    internal sealed class S6TickerProbe
    {
        internal readonly List<IEventSource> Sources = [], Named = [];
        internal readonly List<ISelf> Selves = [], NamedSelves = [];
        internal IEventSource Source => Sources[^1];
    }

    /// <summary>Serves the ticker (its sources and selves recorded in <paramref name="probe"/>), named tickers and their views.</summary>
    internal static void S6ServeTickers(IFacetHost h, S6TickerProbe probe)
    {
        h.Serve(S6Ticker, (_, _, self) =>
        {
            var ticks = self.Events(); var counter = self.State(S6TickerCounter);
            lock (probe) { probe.Sources.Add(ticks); probe.Selves.Add(self); }
            return new(new Dictionary<string, object?>
            {
                ["ticks"] = ticks,
                ["count"] = self.Derive([counter], values => ((JsonNode)values[0]!)["n"]!.DeepClone()),
                ["failing"] = (HostMethod)(async (value, ctx) =>
                {
                    await self.TxAsync<object?>(async tx =>
                    {
                        var draft = await tx.DraftAsync(counter); draft["n"] = draft["n"]!.GetValue<int>() + 1;
                        ticks.Emit(((JsonNode?)value)?.DeepClone());
                        throw new InvalidOperationException("refused");
                    }, ctx.Context);
                    return null;
                }),
            });
        });
        h.Serve(S6NamedTicker, (_, _, self) =>
        {
            var ticks = self.Events();
            lock (probe) { probe.Named.Add(ticks); probe.NamedSelves.Add(self); }
            return new(new Dictionary<string, object?> { ["ticks"] = ticks });
        });
        h.Serve(S6TickerView, (parameters, _, self) => new(new Dictionary<string, object?>
        {
            ["ticks"] = self.Get(S6NamedTicker, new() { ["name"] = parameters["name"]!.DeepClone() }).Event("ticks"),
        }));
    }

    /// <summary>A host of the 1.6.0 remote cases: errors and telemetry recorded when given, the windows as given.</summary>
    internal static ChordHost S6Host(Address? address = null, List<HostError>? errors = null, InMemoryTelemetryContext? telemetry = null,
        int? callWindow = null, int? eventWindow = null, JsonObject? principal = null, ICompression? compression = null, TimeSpan? closeTimeout = null)
    {
        var options = new HostOptions
        {
            Address = address, Principal = principal ?? (address is null ? S6User : S6ServerPrincipal), Telemetry = telemetry,
            OnError = error => { if (errors is not null) lock (errors) errors.Add(error); },
            ReadyTimeout = TimeSpan.FromSeconds(3), CloseTimeout = closeTimeout ?? TimeSpan.FromMilliseconds(200),
            CallWindow = callWindow ?? 10_000, EventWindow = eventWindow ?? 32,
            Compression = compression,
        };
        return Hosts.Create(options);
    }

    /// <summary>A loaded host serving the tickers, with its probe (upstream <c>ticking</c>).</summary>
    internal static async Task<(ChordHost Host, S6TickerProbe Probe, List<HostError> Errors)> S6Ticking(InMemoryTelemetryContext? telemetry = null)
    {
        var probe = new S6TickerProbe(); var errors = new List<HostError>();
        var host = S6Host(errors: errors, telemetry: telemetry);
        await host.LoadAsync([new("@test/tickers", Facet.Define((h, _) => S6ServeTickers(h, probe)))], ChordContext.Background);
        return (host, probe, errors);
    }

    /// <summary>A server (at <see cref="Server"/>) and a client connected to it over a memory link (upstream <c>linked</c>): the server
    /// accepts the client as <paramref name="clientPrincipal"/>, the client connects it as its parent.</summary>
    internal static async Task<(ChordHost Server, ChordHost Client)> S6Linked(Action<IFacetHost> serve, JsonObject? clientPrincipal = null,
        List<HostError>? errors = null, InMemoryTelemetryContext? telemetry = null, bool trusted = false, int? eventWindow = null, int? callWindow = null)
    {
        var server = S6Host(Server.Address(), errors, telemetry, eventWindow: eventWindow, callWindow: callWindow);
        var client = S6Host(errors: errors, principal: clientPrincipal ?? S6User);
        await server.LoadAsync([new("@test/served", Facet.Define((h, _) => serve(h)))], ChordContext.Background);
        await client.LoadAsync([], ChordContext.Background);
        S6Link(server, client, clientPrincipal ?? S6User, trusted);
        return (server, client);
    }

    /// <summary>Links <paramref name="child"/> under <paramref name="parent"/> over a memory link.</summary>
    internal static void S6Link(ChordHost parent, ChordHost child, JsonObject? childPrincipal = null, bool trusted = false)
    {
        var pair = MemoryLinkPair.Create();
        parent.Accept(pair.First, new() { Principal = childPrincipal ?? S6User, Trusted = trusted }, ChordContext.Background);
        child.Connect(pair.Second, new() { Principal = S6ServerPrincipal, Trusted = trusted }, ChordContext.Background);
    }

    /// <summary>Runs <paramref name="work"/> as one turn of <paramref name="host"/>: one dispatcher action.</summary>
    internal static void S6Turn(ChordHost host, Action work) => host.Dispatcher.Invoke(work);

    /// <summary>Waits until everything posted to <paramref name="host"/>'s dispatcher so far ran, the event flush included, and what that
    /// posted in turn (upstream <c>flushed</c>).</summary>
    internal static async Task S6Flushed(params ChordHost[] hosts)
    {
        for (var round = 0; round < 4; round++)
            foreach (var host in hosts) await host.Dispatcher.RunAsync(() => Task.CompletedTask);
    }

    /// <summary>Waits (three seconds at most) until <paramref name="check"/> holds (upstream <c>until</c>).</summary>
    internal static async Task S6Until(Func<bool> check, string what = "condition")
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!check())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(what + " did not settle");
            await Task.Delay(5);
        }
    }

    internal static Task S6Live(IReference reference) => S6Until(() => reference.State.Value.Kind == "live", "the reference going live");

    /// <summary>Gives the links time to carry what was sent and the hosts to handle it, for assertions that nothing arrived.</summary>
    internal static async Task S6Settled(params ChordHost[] hosts)
    {
        for (var round = 0; round < 3; round++) { await Task.Delay(15); await S6Flushed(hosts); }
    }

    /// <summary>A listener recording <c>y</c> of every event it gets (upstream <c>recorder</c>): the number, or tag + number.</summary>
    internal static Action<ChordEvent> S6Recorder(List<string> seen, string tag = "") => e =>
    { lock (seen) seen.Add(tag + e.Value!["y"]!.ToJsonString()); };

    internal static string[] S6Seen(List<string> seen) { lock (seen) return [.. seen]; }

    /// <summary>The sum of counter <paramref name="name"/> recorded so far (with attribute <paramref name="reason"/> when given).</summary>
    internal static double S6Count(InMemoryTelemetryContext telemetry, string name, string? reason = null) => telemetry.GetMetrics()
        .Where(metric => metric.Name == name && (reason is null || (metric.Attributes.TryGetValue("reason", out var value) && value?.GetString() == reason)))
        .Sum(metric => metric.Value);

    /// <summary>Fails the live attempt behind <paramref name="self"/> as a runtime failure of its owner does (upstream <c>failLive</c>).</summary>
    internal static void S6FailLive(ChordHost host, ISelf self) =>
        host.Dispatcher.Invoke(() => ((Self)self).Attempt.Owner.Fail(new ChordError("unavailable", "boom")));

    internal static async Task<Exception> S6Rejection(Task work)
    {
        try { await work; }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("expected a failure");
    }

    /// <summary>A hold message of <paramref name="type"/> placed at <paramref name="address"/> (the server by default).</summary>
    internal static HoldMessage S6Hold(Contract type, long id = 0, string parameters = "{}", Address? address = null) => new(id, type.Name,
        type.Version, type.Fingerprint, new Part.Text(parameters), new WireAddress((address ?? Server.Address()).Kind, (address ?? Server.Address()).Params));

    /// <summary>The wire's index of event member <paramref name="member"/> in <paramref name="variant"/> of <paramref name="type"/>.</summary>
    internal static int S6EventIndex(Contract type, string variant, string member, long? version = null) =>
        ContractsInternal.VariantNamesOf(ContractsInternal.VariantsAt(type, version ?? type.Version)[variant]).Events.ToList().IndexOf(member);
    internal static int S6MethodIndex(Contract type, string variant, string member) =>
        ContractsInternal.VariantNamesOf(ContractsInternal.CurrentVariants(type)[variant]).Methods.ToList().IndexOf(member);
    internal static int S6VariantIndex(Contract type, string variant) => ContractsInternal.VariantOrder(type).ToList().IndexOf(variant);
}

/// <summary>
/// A raw protocol peer on one end of a link (upstream <c>RawPeer</c>): it greets with its call window (256 by default, null for none: a
/// trusted link's), records what the host sends in order, and acknowledges bound and state messages by itself when asked to.
/// </summary>
internal sealed class S6Peer : IDisposable
{
    private readonly ILink link;
    private readonly ICodec codec;
    private readonly object gate = new();
    private readonly List<Message> messages = [];
    private readonly List<Message> log = [];
    internal readonly string Incarnation = Guid.NewGuid().ToString();
    internal volatile bool Closed;
    internal bool AutoAck;

    internal S6Peer(ILink link, long? calls = 256, bool autoAck = false, ICodec? codec = null)
    {
        this.link = link; this.codec = codec ?? BinaryCodec.Instance; AutoAck = autoAck;
        link.OnMessage(bytes =>
        {
            var message = this.codec.Decode(bytes);
            lock (gate) { messages.Add(message); log.Add(message); }
            if (AutoAck && message is BoundMessage or StateMessage) Send(new AckMessage { Origin = message.Origin });
        });
        link.OnClose(() => Closed = true);
        Send(new HelloMessage(1, Incarnation, calls));
    }

    internal void Send(Message message) => link.Send(codec.Encode(message));

    /// <summary>What arrived and no <see cref="Next{T}"/> took yet, in order.</summary>
    internal Message[] All { get { lock (gate) return [.. messages]; } }

    /// <summary>Everything that arrived, in order (the hello included).</summary>
    internal Message[] Log { get { lock (gate) return [.. log]; } }

    internal int Count<T>(Func<T, bool>? predicate = null) where T : Message { lock (gate) return log.OfType<T>().Count(x => predicate?.Invoke(x) != false); }

    /// <summary>The next message of kind <typeparamref name="T"/> (matching <paramref name="predicate"/>) not taken yet; waits for it.</summary>
    internal async Task<T> Next<T>(Func<T, bool>? predicate = null) where T : Message
    {
        T? found = null;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (true)
        {
            lock (gate)
            {
                found = messages.OfType<T>().FirstOrDefault(x => predicate?.Invoke(x) != false);
                if (found is not null) { messages.Remove(found); return found; }
            }
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"no {typeof(T).Name} arrived; got [{string.Join(", ", All.Select(m => m.Kind))}]");
            await Task.Delay(5);
        }
    }

    /// <summary>Lets what the host sent so far arrive, then tells whether a <typeparamref name="T"/> waits untaken.</summary>
    internal async Task<bool> Any<T>(Func<T, bool>? predicate = null) where T : Message
    {
        await Task.Delay(40);
        lock (gate) return messages.OfType<T>().Any(x => predicate?.Invoke(x) != false);
    }

    public void Dispose() => link.Close();
}
