// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/staging.ts
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Jobs;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Host;

// Setup and recording have no publication authority. In particular a recording never declares a real document,
// claims store ownership, opens a native, follows a source, starts a job, or invokes a transaction callback.
internal static class PluginStaging
{
    private static readonly Regex PluginName = new("^(?:@[a-z0-9~-][a-z0-9._~-]*/)?[a-z0-9~-][a-z0-9._~-]*$", RegexOptions.CultureInvariant);
    internal static void CheckNames(IEnumerable<PluginFacet> facets)
    {
        foreach (var facet in facets)
            if (facet.Name is null || !PluginName.IsMatch(facet.Name)) throw new ArgumentException($"\"{facet.Name}\" is not a valid plugin name");
    }

    internal static LoadedPlugin Stage(PluginFacet plugin, ChordHost host)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (plugin.Name is null || !PluginName.IsMatch(plugin.Name)) throw new ArgumentException($"\"{plugin.Name}\" is not a valid plugin name");
        ArgumentNullException.ThrowIfNull(plugin.Facet);
        var setup = new Setup(plugin.Name, host, plugin.Facet);
        try
        {
            using var lease = host.RetainCallback(plugin.Facet.Setup, plugin.Facet);
            plugin.Facet.Setup(setup, setup);
            return new(plugin.Name, plugin.Facet, setup.Types, setup.Registries, setup.Contributions);
        }
        catch (Exception error) { throw Named(error, $"plugin \"{plugin.Name}\""); }
        finally { setup.Open = false; }
    }

    internal static Exception? Add(ICollection<LoadedPlugin> staged, LoadedPlugin plugin)
    {
        foreach (var type in plugin.Types)
        {
            var previous = staged.FirstOrDefault(other => other.Types.Any(served => served.Contract.Name == type.Contract.Name));
            if (previous is not null) return new ArgumentException($"plugin \"{plugin.Name}\": {type.Contract.Name} is served twice (plugin \"{previous.Name}\")");
        }
        foreach (var registry in plugin.Registries)
        {
            var previous = staged.FirstOrDefault(other => other.Registries.Any(served => served.Name == registry.Name));
            if (previous is not null) return new ArgumentException($"plugin \"{plugin.Name}\": registry {registry.Name} is served twice (plugin \"{previous.Name}\")");
        }
        staged.Add(plugin);
        return null;
    }

    internal static RecordedFactory Record(ChordObject owner, ServedType served, ChordContext context)
    {
        var self = new RecordingSelf(owner, served, context);
        try
        {
            using var lease = owner.Host.RetainCallback(served.Factory, served.DeclaringFacet);
            var implementation = served.Factory(owner.Parameters.Value, served.Contract.Kind == ContractKind.Local ? null : owner.Caller.Principal, self);
            ValidateImplementation(served.Contract, implementation);
            return self.Recorded;
        }
        catch (Exception error) { throw Named(error, $"plugin \"{served.Plugin}\", {owner.Label}"); }
        finally { self.Declaring = false; }
    }

    internal static void ValidateImplementation(Contract contract, HostImplementation implementation)
    {
        ArgumentNullException.ThrowIfNull(implementation);
        var variant = contract.Kind == ContractKind.Local ? ContractsInternal.Local : implementation.Variant;
        if (variant is null || !ContractsInternal.CurrentVariants(contract).TryGetValue(variant, out var shape))
            throw new ArgumentException($"{contract.Name}: unknown variant \"{variant}\"");
        if (contract.Kind == ContractKind.Local && implementation.Variant is not null)
            throw new ArgumentException($"{contract.Name}: a local implementation has no variant");
        var members = implementation.Members ?? throw new ArgumentException($"{contract.Name}: missing implementation members");
        foreach (var member in shape.State.Keys)
            if (!members.TryGetValue(member, out var value) || value is not IWatchable<JsonNode?>)
                throw new ArgumentException($"{contract.Name}.{member}: expected a state source");
        foreach (var (member, snapshot) in shape.Keyed)
        {
            members.TryGetValue(member, out var value);
            if (snapshot.KeyHost is { } expected)
            {
                if (value is not IHostKeyedHandle hostKeyed || hostKeyed.KeyHost.Kind != expected.Kind)
                    throw new ArgumentException($"{contract.Name}.{member}: expected a handle keyed by {expected.Kind}");
            }
            else if (value is not IKeyedHandle) throw new ArgumentException($"{contract.Name}.{member}: expected a string-keyed handle");
        }
        foreach (var member in shape.Methods.Keys)
            if (!members.TryGetValue(member, out var value) || value is not HostMethod)
                throw new ArgumentException($"{contract.Name}.{member}: expected a method");
        foreach (var member in members.Keys)
            if (!shape.State.ContainsKey(member) && !shape.Keyed.ContainsKey(member) && !shape.Methods.ContainsKey(member))
                throw new ArgumentException($"{contract.Name}.{member}: not a member of the implementation variant");
    }

    internal static bool MigratesTables(ChordObject owner, RecordedFactory recorded) => owner.Host.Store is { } store &&
        recorded.Tables.Any(table => store.Table(StoreInternal.TableSqlName(owner.Served.Contract.Name, table.Name)) is { } stored && stored.Version < table.Version);

    private static Exception Named(Exception error, string where) => new ArgumentException($"{where}: {error.Message}", error);

    private sealed class Setup(string plugin, ChordHost host, Facet declaringFacet) : IFacetHost, IFacetSelf
    {
        internal bool Open = true;
        internal readonly List<ServedType> Types = [];
        internal readonly List<RegistryServing> Registries = [];
        internal readonly List<ContributionDeclaration> Contributions = [];
        private void Check() { if (!Open) throw new InvalidOperationException($"plugin \"{plugin}\": declarations are valid only during setup"); }
        public void Serve(Contract type, HostFactory factory, ServeOptions? options = null)
        {
            Check(); ArgumentNullException.ThrowIfNull(factory); ContractsInternal.DataOf(type);
            if (Types.Any(served => served.Contract.Name == type.Name)) throw new ArgumentException($"{type.Name} is served twice");
            if (type.Kind == ContractKind.Remote && (host.Options.Principal is null || host.Options.Address is null))
                throw new ArgumentException($"{type.Name}: a host without a principal and address serves only local types");
            options ??= new();
            if (options.Linger < TimeSpan.Zero) throw new ArgumentException($"{type.Name}: linger is negative");
            Types.Add(new(plugin, type, factory, options, DataInternal.BindProvider(type, options.Accepts ?? new())) { DeclaringFacet = declaringFacet });
        }
        public void Serve<C, V>(Registry<C, V> registry, RegistryProvider<C, V> provider)
        {
            Check(); ArgumentNullException.ThrowIfNull(registry);
            if (Registries.Any(served => served.Name == registry.Name)) throw new ArgumentException($"registry {registry.Name} is served twice");
            RegistriesInternal.CheckProvider(registry.Name, provider);
            Registries.Add(new(registry.Name, registry.Version, provider) { DeclaringFacet = declaringFacet });
        }
        public INativeWatchable<IReadOnlyList<Presence>> References(Contract type) { Check(); return host.References(type); }
        public void Contribute<C, V>(Registry<C, V> registry, object? value)
        {
            Check(); ArgumentNullException.ThrowIfNull(registry);
            if (Contributions.Any(contribution => contribution.Name == registry.Name)) throw new ArgumentException($"registry {registry.Name} is contributed to twice");
            Contributions.Add(new(registry.Name, registry.Version, value));
        }
    }
}

internal sealed record RecordedRequest(string Name, long Version, Contract? Contract = null, JsonObject? Parameters = null);
internal sealed record RecordedFactory(int Natives, IReadOnlyList<StateDefinition> Documents, IReadOnlyList<TableDefinition> Tables,
    IReadOnlyList<RecordedRequest> Requests, IReadOnlyList<ContributionDeclaration> Contributions, IReadOnlyList<JobDefinition> Jobs);

internal sealed partial class RecordingSelf(ChordObject owner, ServedType served, ChordContext context) : IStoreSelf, IJobDeclarer
{
    internal bool Declaring = true;
    private int natives;
    private readonly Dictionary<string, StateDefinition> documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TableDefinition> tables = new(StringComparer.Ordinal);
    private readonly List<RecordedRequest> requests = [];
    private readonly Dictionary<string, ContributionDeclaration> contributions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JobDefinition> jobs = new(StringComparer.Ordinal);
    internal RecordedFactory Recorded => new(natives, [.. documents.Values], [.. tables.Values], requests, [.. contributions.Values], [.. jobs.Values]);
    private void Check(string declaration) { if (!Declaring) throw new InvalidOperationException($"{owner.Label}: self.{declaration} is valid only in the factory"); }
    private ChordStore Persistable(string what)
    {
        if (!served.HostLifetime) throw new ArgumentException($"{owner.Label}: {what}: persist needs a stable identity (a local type with empty params)");
        return owner.Host.Store ?? throw new ArgumentException($"{owner.Label}: {what}: persist needs a store");
    }
    private void RecordState(StateDefinition definition, bool keyed)
    {
        Check("state"); ArgumentNullException.ThrowIfNull(definition);
        if (definition.Keyed != keyed) throw new ArgumentException($"{definition.Name}: use {(definition.Keyed ? "KeyedState" : "State")} for this definition");
        if (!documents.TryAdd(definition.Name, definition)) throw new ArgumentException($"{owner.Label}: state \"{definition.Name}\" is declared twice");
        if (definition.Persist) Persistable($"state \"{definition.Name}\"").Claims.AssertDocuments(served.Contract.Name, "chord", owner);
        owner.Documents.Check(definition);
    }
    public ISingletonHandle State(StateDefinition definition) { RecordState(definition, false); return RecordingValue.Instance; }
    public IKeyedHandle KeyedState(StateDefinition definition) { RecordState(definition, true); return RecordingValue.Instance; }
    public IWatchable<JsonNode?> Derive(IReadOnlyList<object> sources, Func<IReadOnlyList<object?>, JsonNode?> compute)
    { Check("derive"); ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(compute); return RecordingValue.Instance; }
    public IReference Get(Contract type, JsonObject parameters)
    {
        Check("get"); var (value, _) = DataInternal.CheckParams(type, parameters, context);
        requests.Add(new(type.Name, type.Version, type, value)); return RecordingValue.Instance;
    }
    public IRegistryValue<V> Registry<C, V>(Registry<C, V> registry)
    { Check("registry"); requests.Add(new(registry.Name, registry.Version)); return new RecordingRegistry<V>(); }
    public IWritable<T> Writable<T>(T initial) { Check("writable"); return new RecordingNative<T>(initial); }
    public void RetryOn(params object[] sources)
    {
        Check("retryOn");
        foreach (var source in sources)
            if (source is not IRecordingRegistry && source is not IRegistryValueSource && source is not Constant)
                throw new ArgumentException($"{owner.Label}: self.retryOn takes registry values and constants (sources that outlive the attempt)");
    }
    public INative<T> Open<T>(Func<ChordContext, Task<T>> open, Func<T, NativeCloseReason, ChordContext, Task> close, NativeOptions? options = null)
    { Check("open"); ArgumentNullException.ThrowIfNull(open); ArgumentNullException.ThrowIfNull(close); natives++; return new RecordingNative<T>(); }
    public Task<T> TxAsync<T>(Func<ITx, Task<T>> work, ChordContext context) => throw new InvalidOperationException($"{owner.Label}: self.tx runs in calls, not in a factory");
    public IWatchable<JsonNode?> Follow(FollowAttach attach) { Check("follow"); ArgumentNullException.ThrowIfNull(attach); return RecordingValue.Instance; }
    public void On(IWatchable<JsonNode?> source, Func<WatchValueCall<JsonNode?>, CallContext, Task> handler) { Check("on"); }
    public void On<T>(INativeWatchable<T> source, Func<NativeWatchValueCall<T>, CallContext, Task> handler) { Check("on"); }
    public void OnLive(Func<CallContext, Task> handler) { Check("onLive"); }
    public ITable Table(TableDefinition definition)
    {
        Check("table"); ArgumentNullException.ThrowIfNull(definition);
        var store = Persistable($"table \"{definition.Name}\"");
        if (!tables.TryAdd(definition.Name, definition)) throw new ArgumentException($"{owner.Label}: table \"{definition.Name}\" is declared twice");
        store.Claims.AssertTable(StoreInternal.TableSqlName(served.Contract.Name, definition.Name), owner);
        return new RecordingTable(definition);
    }
    public void Contribute<C, V>(Registry<C, V> registry, object? value)
    {
        Check("contribute");
        if (!served.HostLifetime) throw new ArgumentException($"{owner.Label}: self.contribute is valid only in a local type with empty params");
        if (!contributions.TryAdd(registry.Name, new(registry.Name, registry.Version, value)))
            throw new ArgumentException($"{owner.Label}: registry {registry.Name} is contributed to twice");
    }
    public IJobHandle DeclareJob(JobDefinition definition, JobImplementation implementation)
    {
        Check("job");
        if (!jobs.TryAdd(definition.Name, definition)) throw new ArgumentException($"{owner.Label}: job \"{definition.Name}\" is declared twice");
        if (definition.Persist is not null)
        {
            var store = Persistable($"job \"{definition.Name}\"");
            store.Claims.AssertDocuments(served.Contract.Name, "chord.jobs", owner);
            store.Claims.AssertDocuments(served.Contract.Name, "chord.job-states", owner);
        }
        HostedJob.Check(owner.Documents, definition);
        return new RecordingJob();
    }
}

// These values are recording tokens, not simulated live services. Observing or mutating them would execute work in a
// declaration phase and is refused; factories may retain them in the returned member map without using them.
internal interface IRecordingValue;
internal sealed class RecordingValue : ISingletonHandle, IKeyedHandle, IReference, IRecordingValue
{
    internal static RecordingValue Instance { get; } = new();
    private static InvalidOperationException NotActive() => new("a recording handle cannot be used before activation");
    public JsonNode? Value => null;
    public string? Variant => null;
    INativeWatchable<RefState> IReference.State => new RecordingNative<RefState>(RefState.Loading);
    public Task<IWatchHandle<JsonNode?>> WatchAsync(ChordContext context) => throw NotActive();
    public Task<TResult> ChangeAsync<TResult>(Func<JsonObject, TResult> change, ChordContext context) => throw NotActive();
    public Task ChangeAsync(Action<JsonObject> change, ChordContext context) => throw NotActive();
    public Task RetireAsync(ChordContext context) => throw NotActive();
    public Task<JsonObject?> GetAsync(string key, ChordContext context) => throw NotActive();
    public IAsyncEnumerable<string> Keys(ChordContext context) => throw NotActive();
    public Task<IWatchHandle<JsonNode?>?> WatchAsync(string key, ChordContext context) => throw NotActive();
    public IWatchable<JsonNode?> At(string key) => this;
    public Task<TResult> ChangeAsync<TResult>(string key, Func<JsonObject, TResult> change, ChordContext context) => throw NotActive();
    public Task ChangeAsync(string key, Action<JsonObject> change, ChordContext context) => throw NotActive();
    public Task RetireAsync(string key, ChordContext context) => throw NotActive();
    public IWatchable<JsonNode?> Member(string name) => this;
    public IKeyedHandle KeyedMember(string name) => this;
    public Task<object?> CallAsync(string name, object? arguments, ChordContext context) => throw NotActive();
    public void Retry() => throw NotActive();
    public void Release() { }
    public void Dispose() { }
}
internal class RecordingNative<T>(T initial = default!) : IWritable<T>, INative<T>, IRegistryValue<T>, IRecordingValue
{
    public T Value => initial;
    public void Set(T value) => throw new InvalidOperationException("a recording value cannot be changed before activation");
    public Task<INativeWatchHandle<T>> WatchAsync(ChordContext context) => throw new InvalidOperationException("a recording value cannot be watched before activation");
    public NativeObservation<T> Observe(INativeObserver<T> observer, ChordContext context) => throw new InvalidOperationException("a recording value cannot be observed before activation");
}
internal interface IRecordingRegistry;
internal sealed class RecordingRegistry<T> : RecordingNative<T>, IRecordingRegistry;
internal sealed class RecordingJob : IJobHandle
{
    private static InvalidOperationException Inert() => new("a recording job cannot execute before activation");
    public IWatchable<JsonNode?> Running => RecordingValue.Instance;
    public Task<IJobRun> StartAsync(JsonNode? args, ChordContext context, StartOptions? options = null) => throw Inert();
    public Task<JobOutcome> OutcomeAsync(string id, ChordContext context) => throw Inert();
    public Task<AbortResult> AbortAsync(string id, ChordContext context) => throw Inert();
}
internal sealed class RecordingTable(TableDefinition? definition = null) : ITable
{
    internal TableDefinition? Definition { get; } = definition;
    private static InvalidOperationException Inert() => new("a recording table cannot be used before activation");
    public Task<Page> PageAsync(Query query, long limit, ChordContext context, PageOptions? options = null) => throw Inert();
    public IAsyncEnumerable<IReadOnlyDictionary<string, JsonNode?>> Rows(Query query, ChordContext context) => throw Inert();
    public Task<IReadOnlyDictionary<string, JsonNode?>?> GetAsync(object? key, ChordContext context) => throw Inert();
    public Task<long> CountAsync(Query query, ChordContext context) => throw Inert();
}
