// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/events.ts.
// Events (decision "Events: fire-and-forget members"): the third member kind's delivery on one host. An emit, or an event arriving over a
// link, takes the host's next counter value and waits in the host's list for the event flush. At the flush each entry goes from its node
// (an object's own source, a re-exposed member, an older version's mapping, a remote object's member) to the taps of the bindings made
// before it (a hold over a link, a reference, the re-exposing nodes behind a reference): first every link's message, then the listeners,
// in entry order, then subscription order.
//
// The flush's turn. Upstream queues the flush as a microtask of the turn that pushed the first entry; commits flush synchronously, so
// every state message published before the flush runs is sent before it. There are no microtasks here: the first entry posts the flush to
// the host's dispatcher (HostDispatcher.Post, one FIFO shared with every message a link delivers and every continuation of host work).
// A commit sends its state messages synchronously inside the dispatcher action that made it, so what one dispatcher action published,
// before or after its emit, is on the wire before the flush posted behind that action runs: the same order, with "turn" read as "one
// dispatcher action". Code that leaves the dispatcher between an emit and a commit (ConfigureAwait(false), another thread) is two turns.
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Link;

namespace PiSharp.Chord.Host;

/// <summary>One event waiting for the flush: its node, its value and stamp (the emitting host's Unix milliseconds), the host's counter
/// value it took, and its value encoded for links once one needs it (one encode per representation).</summary>
internal sealed class EventEntry(EventNode node, JsonNode? value, long at, long n)
{
    internal readonly EventNode Node = node;
    internal readonly JsonNode? Value = value;
    internal readonly long At = at, N = n;
    internal Part? Part;
}

/// <summary>Where a node delivers: one binding (a hold over a link, a reference, an older version's mapping), made when the host's counter
/// was <paramref name="boundAt"/>; it gets only entries with a higher counter value.</summary>
internal sealed class Tap(long boundAt, Action<EventEntry, EventFlush> deliver)
{
    internal readonly long BoundAt = boundAt;
    internal readonly Action<EventEntry, EventFlush> Deliver = deliver;
}

/// <summary>What sends a flush's events on a link once every entry was visited: one message per link and origin.</summary>
internal interface IEventSender
{
    void SendEvents();
}

/// <summary>What taps hand to the flush while it visits them: a link's sender (once per flush), a listener's call.</summary>
internal sealed class EventFlush
{
    internal readonly List<IEventSender> Senders = [];
    internal readonly List<(Subscription Subscription, EventEntry Entry)> Calls = [];
    internal void Sending(IEventSender sender) => Senders.Add(sender);
    internal void Call(Subscription subscription, EventEntry entry) => Calls.Add((subscription, entry));
}

/// <summary>An instance a binding's event members tap (upstream <c>Attachable.eventNode</c>): an attempt on this host, a remote object's
/// binding. None for a member its variant lacks.</summary>
internal interface IEventInstance
{
    EventNode? EventNode(string member, long version);
}

/// <summary>A host's events: its emit counter, its list waiting for the flush and the flush itself, and the order of its subscriptions.
/// Nothing is kept once a flush ran. Used on the host's dispatcher only.</summary>
internal sealed class HostEvents(ChordHost host)
{
    private long counter, subscriptions;
    private List<EventEntry> list = [];

    /// <summary>The counter's value now: a binding made now gets only events pushed after it.</summary>
    internal long Counter => counter;
    internal ChordMetrics Metrics => ChordTelemetry.Metrics(host.Telemetry.Metrics);
    internal ChordHost Host => host;

    /// <summary>Appends an event of <paramref name="node"/> stamped <paramref name="at"/> with the counter's next value; the first of a
    /// turn posts the flush.</summary>
    internal void Push(EventNode node, JsonNode? value, long at)
    {
        var n = ++counter;
        if (list.Count == 0) host.Dispatcher.Post(Flush);
        list.Add(new(node, value, at, n));
    }

    /// <summary>The next subscription's place in the listeners' order.</summary>
    internal long NextSubscription() => ++subscriptions;

    // Visits every entry (its node's taps), sends what links collected, then calls the listeners in entry order, then subscription order;
    // what they emit goes into the next flush.
    private void Flush()
    {
        var entries = list; list = [];
        var flush = new EventFlush();
        foreach (var entry in entries) entry.Node.Deliver(entry, flush);
        foreach (var sender in flush.Senders) sender.SendEvents();
        IEnumerable<(Subscription Subscription, EventEntry Entry)> calls = flush.Calls.Count > 1
            ? flush.Calls.OrderBy(call => call.Entry.N).ThenBy(call => call.Subscription.Order) : flush.Calls;
        foreach (var (subscription, entry) in calls.ToArray()) subscription.Call(entry, Report);
    }

    private void Report(Exception error) => host.Report(new HostError("listener_error", error));
}

/// <summary>A source of events on a host: its taps are the bindings it delivers to. An object's own source, a re-exposed member, an older
/// version's mapping and a remote object's member are nodes.</summary>
internal class EventNode
{
    private readonly List<Tap> taps = [];

    /// <summary>Whether any binding taps it: an event arriving for a node nobody taps is dropped on arrival.</summary>
    internal bool Tapped => taps.Count > 0;
    internal void Tap(Tap tap) { if (!taps.Contains(tap)) taps.Add(tap); }
    internal void Untap(Tap tap) => taps.Remove(tap);

    /// <summary>Delivers <paramref name="entry"/> to every tap whose binding was made before it.</summary>
    internal void Deliver(EventEntry entry, EventFlush flush)
    {
        for (var index = 0; index < taps.Count; index++)
            if (taps[index].BoundAt < entry.N) taps[index].Deliver(entry, flush);
    }

    /// <summary>Its instance ended: no binding taps it any more.</summary>
    internal void Clear() => taps.Clear();
}

/// <summary>A subscription (<c>IEventMember.OnAsync</c>): its listener until <see cref="Stop"/>, its context's abort or its owner's end; a
/// call due when it stopped is skipped (the listener is let go at once, on any thread; its owner forgets it on the dispatcher). A listener
/// that throws is reported (<c>listener_error</c>) and its subscription ends.</summary>
internal sealed class Subscription : IEventSubscription
{
    internal readonly long Order;
    private readonly HostDispatcher dispatcher;
    private Action<ChordEvent>? listener;
    private Action<Subscription>? stopped;
    private CancellationTokenRegistration abort;

    internal Subscription(long order, Action<ChordEvent> listener, ChordContext context, Action<Subscription> stopped, HostDispatcher dispatcher)
    {
        Order = order; this.listener = listener; this.stopped = stopped; this.dispatcher = dispatcher;
        if (context.AbortSignal is { CanBeCanceled: true } signal) abort = signal.Register(Stop);
    }

    internal bool Stopped => Volatile.Read(ref listener) is null;

    public void Stop()
    {
        if (Interlocked.Exchange(ref listener, null) is null) return;
        var told = Interlocked.Exchange(ref stopped, null);
        abort.Dispose(); abort = default;
        if (told is null) return;
        if (dispatcher.IsCurrent) told(this); else dispatcher.Post(() => told(this));
    }

    /// <summary>Calls its listener with <paramref name="entry"/>'s value and stamp, unless it stopped.</summary>
    internal void Call(EventEntry entry, Action<Exception> report)
    {
        if (Volatile.Read(ref listener) is not { } current) return;
        try { current(new ChordEvent(entry.Value, entry.At)); }
        catch (Exception error) { report(error); Stop(); }
    }
}

/// <summary>An object's own source (<c>self.Events()</c>), owned by its attempt: an emit takes the host's next counter value and waits for
/// the flush; its own subscriptions tap it from their subscription on. After the seal an emit throws.</summary>
internal sealed class OwnEventSource(Attempt attempt) : EventNode, IEventSource
{
    private Dictionary<Subscription, Tap>? subscriptions;

    /// <summary>Whether <paramref name="owner"/> owns it: an object implements its event members with its own attempt's sources only.</summary>
    internal bool OwnedBy(Attempt owner) => ReferenceEquals(attempt, owner);

    public void Emit(JsonNode? value)
    {
        var host = attempt.Owner.Host;
        host.Dispatcher.Invoke(() =>
        {
            if (attempt.Ended is { } error) throw error;
            host.Events.Metrics.Counter("chord.events.emitted").Add(1);
            host.Events.Push(this, value, host.Options.TimeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        });
    }

    public Task<IEventSubscription> OnAsync(Action<ChordEvent> listener, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(listener); ArgumentNullException.ThrowIfNull(context);
        var host = attempt.Owner.Host;
        return host.Dispatcher.Invoke(() => EventSubscriptions.Subscribe(host, listener, context, subscription =>
        {
            var tap = new Tap(host.Events.Counter, (entry, flush) => flush.Call(subscription, entry));
            (subscriptions ??= []).Add(subscription, tap); Tap(tap);
        }, subscription => { if (subscriptions?.Remove(subscription, out var tap) == true) Untap(tap); }));
    }

    /// <summary>Its attempt ended: every binding's tap goes, and its own subscriptions end.</summary>
    internal void Detach()
    {
        Clear();
        foreach (var subscription in subscriptions?.Keys.ToArray() ?? []) subscription.Stop();
    }
}

internal static class EventSubscriptions
{
    /// <summary>Subscribes <paramref name="listener"/> under <paramref name="context"/> with <paramref name="install"/> (its owner keeps it
    /// and taps for it), at once; fails with the cancellation of an aborted context (installing nothing). On the dispatcher.</summary>
    internal static Task<IEventSubscription> Subscribe(ChordHost host, Action<ChordEvent> listener, ChordContext context,
        Action<Subscription> install, Action<Subscription> stopped)
    {
        if (context.AbortSignal is { IsCancellationRequested: true } signal)
            return Task.FromException<IEventSubscription>(new OperationCanceledException(signal));
        var subscription = new Subscription(host.Events.NextSubscription(), listener, context, stopped, host.Dispatcher);
        install(subscription);
        // Its context aborted while it was made: its abort found nothing installed yet.
        if (subscription.Stopped) stopped(subscription);
        return Task.FromResult<IEventSubscription>(subscription);
    }
}

// The kernel's event hunks (upstream attempt.ts `events`, `eventNode`, the member check's event sources and the detach step; members.ts
// `checkEvents`; staging.ts `events`), kept in this file as partial declarations.
internal sealed partial class Attempt : IEventInstance
{
    // The event members: the attempt's own sources, or nodes fed by the references they re-expose; none before the members check.
    private Dictionary<string, EventNode>? eventNodes;

    /// <summary>What an emit after its seal throws; null while it is not sealed.</summary>
    internal ChordError? Ended => Sealed ? sealedError ?? new ChordError("released", $"{Owner.Label}: attempt ended", "ended") : null;

    /// <summary>Event member <paramref name="member"/>'s node as a binding of <paramref name="version"/> taps it: its own at its version,
    /// else its shared mapping for that version; null outside the variant (or removed since that version), before the members check, and
    /// for a version its provider does not serve.</summary>
    public EventNode? EventNode(string member, long version) =>
        eventNodes is null ? null : version == Served.Contract.Version ? eventNodes.GetValueOrDefault(member) : Adaptation(version).Event(member);

    // After the members check: re-exposed members are fed by their references from now on (the object's bindings tap them once live).
    private void BindEventMembers()
    {
        var variant = Served.Contract.Kind == ContractKind.Local ? ContractsInternal.Local : Implementation!.Variant!;
        var events = ContractsInternal.CurrentVariants(Served.Contract)[variant].Events;
        if (events.Count == 0) return;
        eventNodes = new(StringComparer.Ordinal);
        foreach (var member in events.Keys)
        {
            if (Members[member] is OwnEventSource own) eventNodes.Add(member, own);
            else { var reexposed = new ReexposedEvents((ReferenceEvents)Members[member]!); eventNodes.Add(member, reexposed); reexposed.Attach(); }
        }
    }

    private void DetachEvents()
    {
        if (Self.EventSources is { } sources) foreach (var source in sources) source.Detach();
        if (eventNodes is not null) foreach (var node in eventNodes.Values) if (node is ReexposedEvents reexposed) reexposed.Detach();
    }
}

internal sealed partial class Self
{
    internal List<OwnEventSource>? EventSources;
    public IEventSource Events()
    {
        Declare("events");
        var source = new OwnEventSource(Attempt); (EventSources ??= []).Add(source); return source;
    }
}

internal sealed partial class RecordingSelf
{
    public IEventSource Events() { Check("events"); return RecordingEvents.Instance; }
}

/// <summary>A recording's event source and event member: inert, as every recording handle is. An emit delivers nothing; a subscription
/// is refused (nothing is observed in a declaration phase).</summary>
internal sealed class RecordingEvents : IEventSource, IRecordingValue
{
    internal static RecordingEvents Instance { get; } = new();
    public void Emit(JsonNode? value) { }
    public Task<IEventSubscription> OnAsync(Action<ChordEvent> listener, ChordContext context) =>
        throw new InvalidOperationException("a recording handle cannot be used before activation");
}

internal static class EventImplementations
{
    /// <summary>Whether <paramref name="value"/> implements an event member of <paramref name="attempt"/>'s object: its own source or a
    /// reference's event member (the declaration boundary: only Chord-made event members); staging (no attempt) takes a recording's.</summary>
    internal static bool Accepts(object? value, Attempt? attempt) => value switch
    {
        OwnEventSource own => attempt is not null && own.OwnedBy(attempt),
        ReferenceEvents => attempt is not null,
        IRecordingValue => attempt is null,
        _ => false,
    };
}
