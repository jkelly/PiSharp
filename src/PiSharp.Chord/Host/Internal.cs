using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;
using PiSharp.Chord.Registries;

namespace PiSharp.Chord.Host;

internal sealed record Caller(JsonObject? Principal, string Incarnation);
internal sealed record CheckedParams(JsonObject Value, string Key);
internal sealed record ServedType(string Plugin, Contract Contract, HostFactory Factory, ServeOptions Options, Provider Provider)
{
    internal Facet? DeclaringFacet { get; init; }
    internal bool HostLifetime => Contract.Kind == ContractKind.Local && ContractsInternal.DataOf(Contract).Params.Fields.Count == 0;
}
internal sealed record RegistryServing(string Name, long Version, IRegistryProvider Provider)
{
    internal Facet? DeclaringFacet { get; init; }
}
internal sealed record ContributionDeclaration(string Name, long Version, object? Value);
internal sealed record LoadedPlugin(string Name, Facet Facet, IReadOnlyList<ServedType> Types,
    IReadOnlyList<RegistryServing> Registries, IReadOnlyList<ContributionDeclaration> Contributions);
internal sealed record RetryWalk(HashSet<ITarget> Visited, int Hops = 16);
internal sealed record Followed(string Kind, object? Source = null, object? Instance = null, ChordError? Error = null, object? Last = null);
internal interface IHolder
{
    bool Listed => true;
    Caller Caller { get; }
    Contract Contract { get; }
    bool Following { get; }
    ChordObject? Dependent { get; }
    void Retarget();
    void Moved();
    void End(ChordError error);
}
internal interface ITarget
{
    string Label { get; }
    RefState State { get; }
    RefState StateFor(long version, string fingerprint) => State;
    bool Hydrated { get; }
    string? Variant { get; }
    ChordError? LastFailure { get; }
    string? ActivationId { get; }
    bool BlocksFirstPass { get; }
    object? Instance { get; }
    void Hold(IHolder holder);
    void Unhold(IHolder holder);
    /// <summary>Calls a method of the live object for <paramref name="holder"/> (its caller, at its contract's version: an older one is
    /// mapped); over a link the call is the holder's own, cancelled with its release.</summary>
    Task<object?> CallAsync(string method, object? args, IHolder holder, ChordContext context);
    Followed Followed(string member, long version);
    IKeyedHandle KeyedMember(string member, long version);
    /// <summary><c>Retry()</c> from <paramref name="holder"/>, as one step of <paramref name="walk"/>: an attempt now if failed; an object
    /// parked on failed dependencies passes it on; ignored otherwise.</summary>
    void Retry(RetryWalk walk, IHolder? holder = null);
    /// <summary>How many values the shared replica of state member <paramref name="member"/> took (a remote object's; decision "References
    /// share one wire hold"); null without one (and on this host, where members are no replicas).</summary>
    long? GenerationOf(string member) => null;
    /// <summary>The named query <paramref name="name"/> of <c>queries</c> member <paramref name="member"/> of the live object (Pi 1.6.0,
    /// slice 14 §3.1; local objects only). Throws while the object is not live: <c>not_ready</c>, its failure's error, or its end's.</summary>
    OwnQuery QueryMember(string member, string name);
    bool Reaches(ITarget target, HashSet<ITarget> visited);
}

/// <summary>A blocker as <c>parked_long</c> names it (upstream <c>Blocker</c>): a reference's label, <c>registry name</c> or a reader's
/// label, and whether it loads or failed.</summary>
internal sealed record Blocker(string Label, string State);

/// <summary>What a check names its blocker to (upstream <c>Blocked</c>): a parking episode's <c>parked_long</c> clock.</summary>
internal interface IBlocked
{
    void BlockedBy(Blocker? blocker);
}

/// <summary>What a parked attempt does after one check, of its dependencies, of its readers or of its admission (upstream
/// <c>Parking</c>): it goes on; it stays parked showing a state; or it breaks.</summary>
internal abstract record Parking
{
    private Parking()
    {
    }

    /// <summary>Nothing to wait for: the attempt goes on.</summary>
    internal sealed record Ready : Parking;

    /// <summary>It stays parked showing <paramref name="Shown"/> (<c>loading</c>, or <c>failed</c> with a waiting cause).
    /// <paramref name="FirstPass"/>: no blocker here is still without its first outcome, so the object has its own first outcome now.
    /// <paramref name="Cause"/>: why it parked, for a reload's report (null: the report names no failure).</summary>
    internal sealed record Parked(RefState Shown, bool FirstPass, FailureCause? Cause) : Parking;

    /// <summary>It never comes back by itself (a cycle, authority, an ended dependency, the reader's own code): the attempt fails
    /// <c>broken</c> with <paramref name="Error"/>.</summary>
    internal sealed record Broken(ChordError Error) : Parking;

    /// <summary>The shared answer of a check that found nothing to wait for, and its completed task (a check that awaits nothing).</summary>
    internal static readonly Parking Go = new Ready();
    internal static readonly Task<Parking> GoNow = Task.FromResult(Go);
}
internal interface IJobOwner
{
    ChordHost Host { get; }
    Facet? DeclaringFacet { get; }
    Caller Caller { get; }
    ChordContext Context { get; }
    CancellationToken Signal { get; }
    SealToken Token { get; }
    Documents.Documents Documents { get; }
    DocumentsStore? Store { get; }
    void Admit(Task work, Action? cleanup = null);
    void AdmitRun(Task work, Action cleanup);
    void Report(HostError error);
    void CountRuns(int by);
}
internal interface IHostedJob
{
    Task LoadAsync(ChordContext context);
    void Live();
    void Seal(ChordError error, string reason);
    void HandOver();
    Task WriteAtCloseAsync(ChordContext context);
}
internal interface IUnmappedKeyedHandle
{
    Task<JsonObject?> GetUnmappedAsync(string key, ChordContext context);
}
internal static class HostErrors
{
    internal static ChordError Closed() => new("released", "host closed", "closed");
    internal static ChordError Released() => new("released", "reference released");
    internal static ChordError NotReady(string label) => new("not_ready", $"{label}: not ready");
    internal static ChordError InvalidMember(string member) => DataInternal.Invalid("member", $"{member}: not a member of the bound variant");
}

internal static class HostTransactions
{
    internal static readonly AsyncLocal<bool> InTransaction = new();
    internal static void AssertOutside(string operation)
    {
        if (InTransaction.Value) throw DataInternal.Invalid("reentrant", $"{operation} inside an object's transaction");
    }
}
