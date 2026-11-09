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
    bool Hydrated { get; }
    string? Variant { get; }
    ChordError? LastFailure { get; }
    string? ActivationId { get; }
    bool BlocksFirstPass { get; }
    object? Instance { get; }
    void Hold(IHolder holder);
    void Unhold(IHolder holder);
    Task<object?> CallAsync(string method, object? args, ChordContext context, Caller caller, long version);
    Followed Followed(string member, long version);
    IKeyedHandle KeyedMember(string member, long version);
    void Retry(RetryWalk walk);
    bool Reaches(ITarget target, HashSet<ITarget> visited);
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
