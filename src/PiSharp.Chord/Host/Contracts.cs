// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/chord/src/host/{host,facet,attempt,reference,ctx}.ts
// 1.6.0 here: events and call windows (chord-remote), query_failed's Query and HostDeleteOptions (chord-store), named queries, their
// readers and lists on ISelf, IStoreSelf and IReference (chord-queries), a served type's admission (ServeOptions.Admit and the factory
// that takes the admitted value; the admission's own types are in Host/Admission.cs).
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;
using PiSharp.Telemetry;

namespace PiSharp.Chord.Host;

/// <summary>Creation options of a Chord host. The caller retains ownership of Store.</summary>
public sealed class HostOptions
{
    public Address? Address { get; init; }
    public JsonObject? Principal { get; init; }
    public PrincipalKeyFunction? PrincipalKey { get; init; }
    public ITelemetryContext? Telemetry { get; init; }
    public required Action<HostError> OnError { get; init; }
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan ReloadDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public Func<JsonObject, SpanAttributes>? PrincipalAttributes { get; init; }
    public TimeSpan HeartbeatPing { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HeartbeatClose { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>How many concurrent calls of one origin this host accepts on a link it was not given as trusted (its call window,
    /// announced in <c>hello</c>); a positive number. On a trusted link it announces none (Pi 1.6.0, decision "Call windows only for
    /// untrusted links"): the app's own hosts never queue calls behind each other.</summary>
    public int CallWindow { get; init; } = 10_000;
    /// <summary>How many <c>event</c> messages of its own this host leaves unacknowledged per link and origin (Pi 1.6.0, decision "Events:
    /// fire-and-forget members"); a positive number. A flush finding the window full drops that origin's events (counted): nothing waits
    /// for a slow consumer.</summary>
    public int EventWindow { get; init; } = 32;
    public IStore? Store { get; init; }
    public PiSharp.Chord.Link.ICompression? Compression { get; init; }
    public Func<JsonObject, IEnumerable<JsonNode?>>? RevokedBy { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed record HostError(string Kind, Exception? Error = null, string? Object = null, string? Member = null,
    FailureCause? Failure = null, int? Position = null, string? Job = null, string? Id = null, int? Restarts = null,
    string? WaitingFor = null, string? State = null, double? Since = null, string? Registry = null,
    string? Plugin = null, long? Version = null, string? Message = null, IReadOnlyList<string>? Plugins = null, string? Query = null);
/// <summary>What <c>DeleteHostDataAsync</c> takes besides the address: <paramref name="Plugin"/> limits the delete to the records and rows
/// the named plugin's objects own (upstream <c>HostDeleteOptions</c>).</summary>
public sealed record HostDeleteOptions(string? Plugin = null);
public sealed record GetOptions(TimeSpan? ReadyTimeout = null);
public sealed class RootOptions
{
    public SpanOptions? Trace { get; init; }
    private JsonObject? principal;
    public JsonObject? Principal { get => principal; init { principal = value; HasPrincipal = true; } }
    internal bool HasPrincipal { get; private init; }
}
/// <summary>What <c>host.Serve</c> takes besides the type and the factory. <paramref name="Admit"/> (Pi 1.6.0, slice 14 §2.1) is the
/// type's admission, for remote types whose contract declares an <c>Admission</c>, and only those (either without the other, or on a
/// local type, is a declaration error): run first in every attempt, it reads data about the caller and returns a watchable of
/// <c>Admission.Allow(value)</c> or <c>Admission.Deny(reason)</c>; the factory is handed the value.</summary>
public sealed record ServeOptions(AcceptsTable? Accepts = null, TimeSpan? Linger = null, bool Untraced = false, Admit? Admit = null);
public sealed record PluginFailure(string Plugin, Exception Error);
public sealed record LoadReport(IReadOnlyList<PluginFailure> Failed);
public sealed record CommittedReload(IReadOnlyList<string> Added, IReadOnlyList<string> Changed,
    IReadOnlyList<string> Removed, IReadOnlyList<string> Failed);
public sealed record ReloadOptions(IReadOnlyList<PluginFailure>? Failed = null);
public sealed record ReloadedObject(string Object, string Fate, FailureCause? Failure = null);
public sealed record ReloadReport(CommittedReload Plugins, IReadOnlyList<PluginFailure> Failed, IReadOnlyList<ReloadedObject> Objects);
public sealed record Presence(JsonObject Parameters, JsonObject? Principal, string Incarnation);
public sealed record ActivityCounts(int References, int Calls, int Jobs, int Lingering);
public sealed record RefState(string Kind, FailureCause? Cause = null, double? RetryAt = null, ChordError? Error = null)
{
    public static RefState Loading { get; } = new("loading");
    public static RefState Live { get; } = new("live");
    public static RefState Failed(FailureCause cause, double? retryAt = null) => new("failed", cause, retryAt);
    public static RefState Ended(ChordError error) => new("ended", Error: error);
}

public delegate Task<object?> HostMethod(object? arguments, CallContext context);
public delegate HostImplementation HostFactory(JsonObject parameters, JsonObject? principal, ISelf self);
public sealed record HostImplementation(IReadOnlyDictionary<string, object?> Members, string? Variant = null);
public sealed record PluginFacet(string Name, Facet Facet);
public sealed class Facet
{
    internal Action<IFacetHost, IFacetSelf> Setup { get; }
    private Facet(Action<IFacetHost, IFacetSelf> setup) => Setup = setup;
    public static Facet Define(Action<IFacetHost, IFacetSelf> setup) => new(setup ?? throw new ArgumentNullException(nameof(setup)));
}
public interface IFacetHost
{
    void Serve(Contract type, HostFactory factory, ServeOptions? options = null);
    /// <summary>Serves a remote type whose contract declares an <c>Admission</c> (Pi 1.6.0, slice 14 §2.1): <c>options.Admit</c> is
    /// required, and <paramref name="factory"/> gets what it allowed after the principal. (A factory that does not need the value may be
    /// served through the other overload, with <c>options.Admit</c>.)</summary>
    void Serve(Contract type, AdmittedHostFactory factory, ServeOptions options) => throw new NotSupportedException("Admission is not available on this facet host.");
    void Serve<C, V>(Registry<C, V> registry, RegistryProvider<C, V> provider);
    INativeWatchable<IReadOnlyList<Presence>> References(Contract type);
}
public interface IFacetSelf { void Contribute<C, V>(Registry<C, V> registry, object? value); }
public interface IReference : IDisposable
{
    INativeWatchable<RefState> State { get; }
    string? Variant { get; }
    IWatchable<JsonNode?> Member(string name);
    IKeyedHandle KeyedMember(string name);
    IHostKeyedHandle HostKeyedMember(string name) => throw new NotSupportedException("Host-keyed members are not available on this reference.");
    /// <summary>The event member <paramref name="name"/> (Pi 1.6.0, decision "Events: fire-and-forget members"): its listeners get what
    /// this reference receives while it is bound to an instance whose variant has the member; never an event emitted before it bound.</summary>
    IEventMember Event(string name) => throw new NotSupportedException("Event members are not available on this reference.");
    /// <summary>The named queries of <c>queries</c> member <paramref name="name"/> by name (Pi 1.6.0, slice 14 §3.1): each reads its
    /// provider's declaration while the provider is live (<c>self.LiveQuery</c>, <c>self.Count</c>, <c>self.List</c>, <c>PageAsync</c>).
    /// Named queries are local: only a reference to an object of this host has them.</summary>
    IReadOnlyDictionary<string, INamedQuery> QueriesMember(string name) => throw new NotSupportedException("Queries members are not available on this reference.");
    Task<object?> CallAsync(string name, object? arguments, ChordContext context);
    void Retry();
    void Release();
}
public interface ISelf
{
    ISingletonHandle State(StateDefinition definition);
    IKeyedHandle KeyedState(StateDefinition definition);
    IHostKeyedHandle HostKeyedState(StateDefinition definition) => throw new NotSupportedException("Host-keyed state is not available on this implementation.");
    /// <summary>An event source of this attempt (Pi 1.6.0, decision "Events: fire-and-forget members"), implementing an event member:
    /// <c>Emit</c> delivers a JSON value once to each reference bound to the object before it, at the event flush, after the state
    /// published before it; never kept, never folded, dropped for a holder that cannot keep up. Emits are no transaction work; after the
    /// attempt's seal <c>Emit</c> throws <c>released</c>.</summary>
    IEventSource Events() => throw new NotSupportedException("Event sources are not available on this implementation.");
    /// <summary>A named query read live (Pi 1.6.0, slice 14 §3): the first <paramref name="first"/> rows of <paramref name="query"/> for
    /// <paramref name="args"/>, and whether a further row exists, as a JSON watchable of <c>{ "rows": [...], "more": bool }</c>.
    /// <paramref name="args"/> is JSON (a <see cref="JsonNode"/>, null for JSON null) or a JSON watchable of the arguments (a derive, a
    /// reference's member): the member shows the previous rows until the read for the new arguments has run. Opened once this object's
    /// dependencies are live and before its natives; a commit that can change its rows runs it again in its step. Another object's query
    /// is read through its reference (<c>self.Get(Store, new()).QueriesMember("member")["name"]</c>), the owner's own through what
    /// <c>self.Queries</c> returned.</summary>
    IWatchable<JsonNode?> LiveQuery(INamedQuery query, object? args, long first) => throw new NotSupportedException("Named queries are not available on this implementation.");
    /// <summary>The exact number of rows of a named query for <paramref name="args"/>, live (slice 14 §3.1). Expensive: every run reads
    /// every matching row, nothing caps it, and it runs again on every write to its tables except an update that assigns none of its
    /// condition's columns. Count what a key or an index bounds.</summary>
    IWatchable<JsonNode?> Count(INamedQuery query, object? args) => throw new NotSupportedException("Named queries are not available on this implementation.");
    /// <summary>A list a screen loads more of (slice 14 §4): the rows of a named query in pages of <c>options.First</c> between fixed
    /// bookmarks, so loaded rows stay live, <c>MoreAsync</c> sends only the new rows, and a row that moves leaves no gap and no duplicate.
    /// <paramref name="name"/> is the list's within this object: its bookmarks survive a reload by it. One source,
    /// <c>[(namedQuery, args)]</c>; the arguments may be a JSON watchable of them.</summary>
    IListHandle List(string name, IReadOnlyList<(INamedQuery Query, object? Args)> sources, ListOptions options) => throw new NotSupportedException("Lists are not available on this implementation.");
    IWatchable<JsonNode?> Derive(IReadOnlyList<object> sources, Func<IReadOnlyList<object?>, JsonNode?> compute);
    IReference Get(Contract type, JsonObject parameters);
    IRegistryValue<V> Registry<C, V>(Registry<C, V> registry);
    IWritable<T> Writable<T>(T initial);
    void RetryOn(params object[] sources);
    INative<T> Open<T>(Func<ChordContext, Task<T>> open, Func<T, NativeCloseReason, ChordContext, Task> close, NativeOptions? options = null);
    Task<T> TxAsync<T>(Func<ITx, Task<T>> work, ChordContext context);
    IWatchable<JsonNode?> Follow(FollowAttach attach);
    void On(IWatchable<JsonNode?> source, Func<WatchValueCall<JsonNode?>, CallContext, Task> handler);
    void On<T>(INativeWatchable<T> source, Func<NativeWatchValueCall<T>, CallContext, Task> handler);
    void OnLive(Func<CallContext, Task> handler);
}
public interface IStoreSelf : ISelf
{
    ITable Table(TableDefinition definition);
    IWatchable<JsonNode?> Query(ITable table, Query query, long limit) => throw new NotSupportedException("Live queries are not available on this implementation.");
    /// <summary>Named queries over <paramref name="table"/>, a table this factory declared (Pi 1.6.0, slice 14 §3.1): one per entry of
    /// <paramref name="builders"/>, each a pure function of its arguments returning a query of the table. What it returns implements a
    /// <c>queries</c> member of the contract (the queries listed there; the others are private to this object and bring their argument
    /// schema). <c>options.Map</c> turns a row (and a joined query's partner) into the item readers get, from the columns <c>Reads</c>
    /// (and <c>PartnerReads</c>) list: a change of another column is no change of the item.</summary>
    IReadOnlyDictionary<string, INamedQuery> Queries(ITable table, QueriesOptions options, IReadOnlyDictionary<string, QueryBuilder> builders) =>
        throw new NotSupportedException("Named queries are not available on this implementation.");
    void Contribute<C, V>(Registry<C, V> registry, object? value);
}
public enum NativeCloseReason { End, Close, Reload }
public sealed record NativeOptions(TimeSpan? CloseTimeout = null, string? Key = null);
public interface INative<out T> { T Value { get; } }
public interface IFollowObserver
{
    void Advance(WatchCall<JsonNode?> call, ChordContext context);
    void End(WatchEnd end);
}
public sealed record FollowAttachment(JsonNode? Value, Action Detach);
public delegate Task<FollowAttachment?> FollowAttach(IFollowObserver observer, ChordContext context);

public static class Hosts
{
    public static ChordHost Create(HostOptions options) => new(options);
}
