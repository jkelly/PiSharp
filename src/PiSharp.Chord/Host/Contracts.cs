// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/{host,facet,attempt,reference,ctx}.ts
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
    public TimeSpan HeartbeatPing { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan HeartbeatClose { get; init; } = TimeSpan.FromSeconds(30);
    public IStore? Store { get; init; }
    public PiSharp.Chord.Link.ICompression? Compression { get; init; }
    public Func<JsonObject, IEnumerable<JsonNode?>>? RevokedBy { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

public sealed record HostError(string Kind, Exception? Error = null, string? Object = null, string? Member = null,
    FailureCause? Failure = null, int? Position = null, string? Job = null, string? Id = null, int? Restarts = null,
    string? WaitingFor = null, string? State = null, double? Since = null, string? Registry = null,
    string? Plugin = null, long? Version = null, string? Message = null, IReadOnlyList<string>? Plugins = null);
public sealed record GetOptions(TimeSpan? ReadyTimeout = null);
public sealed class RootOptions
{
    private JsonObject? principal;
    public JsonObject? Principal { get => principal; init { principal = value; HasPrincipal = true; } }
    internal bool HasPrincipal { get; private init; }
}
public sealed record ServeOptions(AcceptsTable? Accepts = null, TimeSpan? Linger = null, bool Untraced = false);
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
    Task<object?> CallAsync(string name, object? arguments, ChordContext context);
    void Retry();
    void Release();
}
public interface ISelf
{
    ISingletonHandle State(StateDefinition definition);
    IKeyedHandle KeyedState(StateDefinition definition);
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
