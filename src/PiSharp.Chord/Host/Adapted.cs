// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/adapted.ts
using System.Text.Json.Nodes;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Host;

/// <summary>One instance's shared, lazy view for one consumer version. It never changes its provider mid-call.</summary>
internal sealed class AdaptedMembers(ServedType served, string? variant, string label, Batch batch,
    Func<string, object> member, Action<string, Exception> report)
{
    private readonly Dictionary<string, object> members = new(StringComparer.Ordinal);
    private readonly List<Derive> derives = [];
    private readonly Dictionary<string, IKeyedHandle> keyed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<object, MappedKey>> keys = new(StringComparer.Ordinal);
    internal long Version { get; init; }

    internal static ChordError? Refusal(ServedType served, long version, string? variant)
    {
        if (!Accepts(served, version)) return Mismatch(served, version);
        var mapping = Mappings(served, version);
        var old = ContractsInternal.VariantsAt(served.Contract, version).GetValueOrDefault(variant ?? ContractsInternal.Local);
        if (old is null) return new ChordError("contract_mismatch", $"{served.Contract.Name} v{version}: variant {variant} is not available");
        foreach (var name in old.State.Keys)
            if (StateMappingOf(served, mapping, variant, name) is { Refuses: true }) return Refused(served, version, name);
        foreach (var name in old.Keyed.Keys)
            if (mapping?.Keyed.GetValueOrDefault(name).Refuses == true) return Refused(served, version, name);
        return null;
    }

    internal object Member(string name)
    {
        AssertAccepted();
        if (members.TryGetValue(name, out var found)) return found;
        var mapping = StateMappingOf(served, Mappings(served, Version), variant, name);
        if (mapping is { Refuses: true }) throw Refused(served, Version, name);
        if (mapping?.Map is not { } map) return members[name] = member(name);
        var current = ContractsInternal.CurrentVariants(served.Contract)[variant ?? ContractsInternal.Local];
        var kept = current.State.ContainsKey(name);
        var names = kept ? [name] : current.State.Keys.ToArray();
        var sources = names.Select(member).Select(ChordHost.ResolveSource).Cast<object>().ToArray();
        var derive = new Derive(sources, values =>
        {
            object? value;
            if (kept) value = values[0];
            else
            {
                var state = new JsonObject();
                for (var i = 0; i < names.Length; i++) state[names[i]] = AsJson(values[i])?.DeepClone();
                value = state;
            }
            batch.Count("chord.contract.adapts");
            using var lease = batch.RetainCallback?.Invoke(map, served.DeclaringFacet);
            return AsJson(map(value));
        }, batch);
        derive.Attach((source, _) => source, error => report(name, error));
        derives.Add(derive);
        return members[name] = derive;
    }

    internal IKeyedHandle Keyed(string name, IKeyedHandle source)
    {
        AssertAccepted();
        var mapping = Mappings(served, Version)?.Keyed.GetValueOrDefault(name);
        if (mapping is { Refuses: true }) throw Refused(served, Version, name);
        if (mapping?.Map is not { } map) return source;
        if (!keyed.TryGetValue(name, out var value))
            keyed[name] = value = new AdaptedKeyed(source, current => Map(map, current), original => Key(name, original, map),
                (key, context) => WatchKeyAsync(name, source, key, map, context));
        return value;
    }

    internal IWatchable<JsonNode?> KeySource(string name, IWatchable<JsonNode?> source)
    {
        var map = KeyMapping(name);
        return map is null ? source : Key(name, source, map);
    }

    internal (IWatchable<JsonNode?> Source, Action Release) HoldKeySource(string name, IWatchable<JsonNode?> source)
    {
        var map = KeyMapping(name);
        if (map is null) return (source, static () => { });
        var raw = ChordHost.ResolveSource(source)!;
        var shared = SharedKey(name, raw, map);
        shared.Holders++;
        var released = 0;
        return (shared.Derive, () =>
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            OnOwner(() => { ReleaseKey(name, raw, shared); return true; });
        });
    }

    private Func<object?, object?>? KeyMapping(string name)
    {
        AssertAccepted();
        var mapping = Mappings(served, Version)?.Keyed.GetValueOrDefault(name);
        if (mapping is { Refuses: true }) throw Refused(served, Version, name);
        return mapping?.Map;
    }

    internal void Detach(Exception error)
    {
        foreach (var derive in derives) derive.Detach(error);
        foreach (var bySource in keys.Values)
            foreach (var key in bySource.Values) key.Derive.Detach(error);
        derives.Clear(); members.Clear(); keyed.Clear(); keys.Clear();
    }

    private IWatchable<JsonNode?> Key(string name, IWatchable<JsonNode?> source, Func<object?, object?> map)
    {
        var raw = ChordHost.ResolveSource(source)!;
        var found = SharedKey(name, raw, map);
        found.Retained = true; // A factory's at(key) pin lasts for this attempt.
        return found.Derive;
    }
    private MappedKey SharedKey(string name, object raw, Func<object?, object?> map)
    {
        if (!keys.TryGetValue(name, out var bySource)) keys.Add(name, bySource = new(ReferenceEqualityComparer.Instance));
        if (!bySource.TryGetValue(raw, out var found))
        {
            var derive = new Derive([raw], values => Map(map, AsJson(values[0])), batch);
            derive.Attach((value, _) => value, _ => { });
            bySource.Add(raw, found = new(derive));
        }
        return found;
    }
    private async Task<IWatchHandle<JsonNode?>?> WatchKeyAsync(string name, IKeyedHandle handle, string key,
        Func<object?, object?> map, ChordContext context)
    {
        var original = await handle.WatchAsync(key, context);
        if (original is null) return null;
        if (handle is not PiSharp.Chord.Documents.Keyed native) return new MappedWatch(original, value => Map(map, value));
        // The original watch owns the document's residency pin. All readers of this version share one derive
        // of its actual source; stopping the last reader drops that derive and releases the pin.
        try { return OnOwner(() => MakeWatch()); }
        catch { await original.StopAsync(); throw; }
        IWatchHandle<JsonNode?> MakeWatch()
        {
            var raw = native.Slot.Source(key)!;
            var shared = SharedKey(name, raw, map);
            var mapped = shared.Derive.OpenWatch(context);
            shared.Holders++;
            return new LeasedWatch(mapped, original, () => OnOwner(() =>
            {
                ReleaseKey(name, raw, shared);
                return true;
            }));
        }
    }
    private void ReleaseKey(string name, object raw, MappedKey shared)
    {
        if (--shared.Holders != 0 || shared.Retained || !keys.TryGetValue(name, out var bySource) || !bySource.Remove(raw)) return;
        if (bySource.Count == 0) keys.Remove(name);
        shared.Derive.Detach(new ChordError("released", $"{label}: key released", "ended"));
    }
    private JsonNode? Map(Func<object?, object?> map, JsonNode? value)
    { using var lease = batch.RetainCallback?.Invoke(map, served.DeclaringFacet); return batch.Compute(() => { batch.Count("chord.contract.adapts"); return AsJson(map(value)); }); }
    private T OnOwner<T>(Func<T> work)
    {
        if (batch.IsOwnerThread?.Invoke() != false || batch.InvokeOwner is null) return work();
        T result = default!;
        batch.InvokeOwner(() => result = work());
        return result;
    }
    private void AssertAccepted() { if (!Accepts(served, Version)) throw Mismatch(served, Version); }
    private static bool Accepts(ServedType served, long version) => version == served.Contract.Version || served.Provider.Accepted.Any(entry => entry.Version == version);
    private static VersionMappings? Mappings(ServedType served, long version) => served.Provider.Accepts.GetValueOrDefault(version);
    private static StateMapping? StateMappingOf(ServedType served, VersionMappings? mappings, string? variant, string name)
    {
        if (mappings is null) return null;
        if (served.Contract.Kind == ContractKind.Local) return mappings.State.GetValueOrDefault(name).Value is StateMapping local ? local : null;
        return mappings.State.GetValueOrDefault(variant ?? "").Value is StateSection section && section.TryGetValue(name, out var remote) ? remote : null;
    }
    private static JsonNode? AsJson(object? value) => value switch
    { null => null, JsonNode json => json, _ => throw new InvalidOperationException("a state mapping must return a JsonNode or null") };
    private static ChordError Mismatch(ServedType served, long version) => new("contract_mismatch", $"{served.Contract.Name} v{version} is not served here");
    private static ChordError Refused(ServedType served, long version, string name) => new("contract_mismatch", $"{served.Contract.Name} v{version}: member \"{name}\" is refused by its provider");
    private sealed class MappedKey(Derive derive)
    {
        internal Derive Derive { get; } = derive;
        internal int Holders;
        internal bool Retained;
    }

    private sealed class AdaptedKeyed(IKeyedHandle source, Func<JsonNode?, JsonNode?> map,
        Func<IWatchable<JsonNode?>, IWatchable<JsonNode?>> at,
        Func<string, ChordContext, Task<IWatchHandle<JsonNode?>?>> watch) : IKeyedHandle, IUnmappedKeyedHandle
    {
        public Task<JsonObject?> GetUnmappedAsync(string key, ChordContext context) => source is IUnmappedKeyedHandle unmapped
            ? unmapped.GetUnmappedAsync(key, context) : source.GetAsync(key, context);
        public async Task<JsonObject?> GetAsync(string key, ChordContext context)
        {
            var current = await source.GetAsync(key, context);
            return current is null ? null : map(current) as JsonObject ?? throw new InvalidOperationException("a keyed state mapping must return a JsonObject");
        }
        public IAsyncEnumerable<string> Keys(ChordContext context) => source.Keys(context);
        public Task<IWatchHandle<JsonNode?>?> WatchAsync(string key, ChordContext context) => watch(key, context);
        public IWatchable<JsonNode?> At(string key) => at(source.At(key));
        private static ChordError ReadOnly() => new("released", "an adapted keyed member is read-only", "version");
        public Task<TResult> ChangeAsync<TResult>(string key, Func<JsonObject, TResult> change, ChordContext context) => throw ReadOnly();
        public Task ChangeAsync(string key, Action<JsonObject> change, ChordContext context) => throw ReadOnly();
        public Task RetireAsync(string key, ChordContext context) => throw ReadOnly();
    }
    private sealed class LeasedWatch : IWatchHandle<JsonNode?>
    {
        private readonly IWatchHandle<JsonNode?> mapped, original;
        private readonly Action release;
        private int released;
        internal LeasedWatch(IWatchHandle<JsonNode?> mapped, IWatchHandle<JsonNode?> original, Action release)
        { this.mapped = mapped; this.original = original; this.release = release; _ = ObserveClosed(); }
        private async Task ObserveClosed() { await mapped.Closed; await Release(); }
        private async Task Release()
        { if (Interlocked.Exchange(ref released, 1) != 0) return; await original.StopAsync(); release(); }
        public JsonNode? Value => mapped.Value;
        public Task<WatchEnd> Closed => mapped.Closed;
        public void Start(Func<WatchCall<JsonNode?>, ChordContext, Task> listener) => mapped.Start(listener);
        public async Task<WatchEnd> StopAsync() { var result = await mapped.StopAsync(); await Release(); return result; }
    }
    private sealed class MappedWatch : IWatchHandle<JsonNode?>
    {
        private readonly IWatchHandle<JsonNode?> source;
        private readonly Func<JsonNode?, JsonNode?> map;
        private JsonNode? value;
        internal MappedWatch(IWatchHandle<JsonNode?> source, Func<JsonNode?, JsonNode?> map)
        { this.source = source; this.map = map; value = map(source.Value); }
        public JsonNode? Value => value;
        public Task<WatchEnd> Closed => source.Closed;
        public Task<WatchEnd> StopAsync() => source.StopAsync();
        public void Start(Func<WatchCall<JsonNode?>, ChordContext, Task> listener) => source.Start(async (call, context) =>
        {
            if (call is WatchValueCall<JsonNode?> next)
            {
                var mapped = map(next.Value);
                var ops = DeltaDiff.DiffRevisions(value, mapped);
                value = mapped;
                await listener(new WatchValueCall<JsonNode?>(mapped, ops, next.Retired), context);
            }
            else await listener(call, context);
        });
    }
}
