// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/jobs/run-states.ts
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Documents;
using PiSharp.Chord.Store;

namespace PiSharp.Chord.Jobs;

internal sealed class RunStates : IDocumentSlot, IPersistedSlot
{
    private readonly Persistence? persistence;
    private readonly Action<string, ImmutableArray<Op>> changed;
    private readonly Action<double> heldDeadline;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, Tracker<JsonObject>> trackers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stored> stored = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HeldState> held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SlotDefinition> older = new(StringComparer.Ordinal);
    private sealed class HeldState { public PendingOps Pending { get; } = new(); public bool Base; public double Deadline = double.PositiveInfinity; }
    public RunStates(StateDefinition definition, Documents.Documents documents, Persistence? persistence,
        Action<string, ImmutableArray<Op>> changed, Action<double> heldDeadline, TimeProvider clock)
    {
        Definition = new(definition); Owner = documents; this.persistence = persistence;
        this.changed = changed; this.heldDeadline = heldDeadline; this.clock = clock;
    }
    public Documents.Documents Owner { get; }
    public IPersistedSlot? Persisted => persistence is null ? null : this;
    public Persistence Persistence => persistence!;
    public SlotDefinition Definition { get; private set; }
    public void Redefine(StateDefinition definition)
    {
        Definition.Check(definition);
        if (definition.Version != Definition.Current.Version)
            foreach (var id in trackers.Keys) older.TryAdd(id, Definition);
        Definition = new(definition);
    }
    private SlotDefinition DefinitionOf(string id) => older.GetValueOrDefault(id) ?? Definition;
    public JsonObject? Value(string id) => Current(id)?.Value;
    public JsonObject? Migrated(string id, ChordContext context) => older.TryGetValue(id, out var old) && Value(id) is { } value
        ? Definition.Current.Migrate(value, old.Current.Version, context) : Value(id);
    public Tracker<JsonObject>? Current(string key) => trackers.GetValueOrDefault(key);
    public JsonObject Initial(ChordContext context) => throw new ChordError("released", $"{Definition.Current.Name}: the run's document is retired", "settled");
    public bool Cold(string key) => false;
    public Stored? Stored(string key) => stored.GetValueOrDefault(key);
    public Task LoadAsync(string key, ChordContext context, SealToken? token = null) => Task.CompletedTask; // Always pinned; activation loads before dispatch.
    public StoredChange Stage(string key, DocumentChange change, Stored? was, List<IStagedWrite> writes, CommitPersistence policy)
    {
        if (policy.Kind == "deferred" && was is not null && !change.Retires && change.Prepared is not null)
            return new StoredChange.HeldChange(was, policy.WithinMs ?? 1000);
        return Persistence.Stage(key, DefinitionOf(key).Current, was, change, writes, held.TryGetValue(key, out var h) ? HeldOps(h) : null);
    }
    public void Adopt(string key, Tracker<JsonObject> tracker, Prepared<JsonObject> prepared, StoredChange? change)
    {
        tracker.Adopt(prepared);
        if (change is StoredChange.HeldChange pending) Hold(key, prepared, pending);
        else if (change is not null) Settle(key, DocumentsInternal.StoredAfter(change));
        changed(key, prepared.Ops);
    }
    public void Retire(string key) { older.Remove(key); trackers.Remove(key); stored.Remove(key); held.Remove(key); }
    public void Create(string id, JsonObject value, Stored? previous)
    { trackers[id] = Tracker.Track(value); if (previous is not null) stored[id] = previous; }
    public Action StageCreate(string id, JsonObject value, Func<List<IStagedWrite>> writes)
    {
        if (persistence is null) return () => Create(id, value, null);
        var definition = Definition.Current; long made = 0;
        writes().Add(StoreInternal.CreateDocument(persistence.At(id), new DocumentBase(definition.Version, definition.Fingerprint, value),
            ChordJson.Stringify(value), id => made = id));
        return () => Create(id, value, new Stored(made, 1, definition.Version, 0));
    }
    public IReadOnlyList<string> HeldIds => [.. held.Keys];
    public double Deadline(string id) => held.GetValueOrDefault(id)?.Deadline ?? double.PositiveInfinity;
    public Action? StageHeld(string id, List<IStagedWrite> writes)
    {
        if (!held.TryGetValue(id, out var h) || !stored.TryGetValue(id, out var prior) || Current(id) is not { } tracker) return null;
        var definition = DefinitionOf(id).Current; var pending = HeldOps(h); var asBase = pending.Base || prior.Version != definition.Version;
        var ops = new JsonArray([.. pending.Ops.Select(x => (JsonNode)x.ToJson())]);
        DocumentContent content = asBase ? new DocumentContent.Base(definition.Version, definition.Fingerprint, tracker.Value)
            : new DocumentContent.Delta(definition.Version, ops);
        writes.Add(StoreInternal.ChangeDocument(prior.Id, prior.Revision, content, ChordJson.Stringify(asBase ? tracker.Value : ops)));
        var after = new Stored(prior.Id, prior.Revision + 1, definition.Version, asBase ? 0 : prior.DeltasSinceBase + 1);
        return () => { if (held.GetValueOrDefault(id) == h) Settle(id, after); };
    }
    private void Hold(string id, Prepared<JsonObject> prepared, StoredChange.HeldChange change)
    {
        var h = held.GetValueOrDefault(id) ?? new HeldState();
        if (DefinitionOf(id).Current.CheckpointWhen(prepared.Value, prepared.Ops, new CheckpointInfo(change.Stored.DeltasSinceBase + (h.Pending.Size > 0 ? 1 : 0)))) h.Base = true;
        if (h.Base) h.Pending.Take(); else h.Pending.Push(prepared.Ops);
        h.Deadline = Math.Min(h.Deadline, clock.GetUtcNow().ToUnixTimeMilliseconds() + change.WithinMs);
        held[id] = h; if (double.IsFinite(h.Deadline)) heldDeadline(h.Deadline);
    }
    private static Held HeldOps(HeldState h)
    {
        if (h.Base) return new([], true);
        var taken = h.Pending.Take();
        if (taken.Kind == PendingTakeKind.Replace) { h.Base = true; return new([], true); }
        var ops = taken.Ops; h.Pending.Push(ops); return new(ops, false);
    }
    private void Settle(string id, Stored? after) { if (after is null) stored.Remove(id); else stored[id] = after; held.Remove(id); }
    public void Drop() { older.Clear(); trackers.Clear(); stored.Clear(); held.Clear(); }
}
