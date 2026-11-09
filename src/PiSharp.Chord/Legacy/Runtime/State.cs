using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

public abstract class LegacySource : ILegacyReadable
{
    private readonly HashSet<Action<JsonNode?>> _listeners = [];
    private readonly HashSet<Action<LegacyStateEvent>> _events = [];
    internal readonly HashSet<LegacyDerived> Dependents = [];
    internal readonly List<JsonNode> ExposedSchemas = [];
    internal LegacySource(LegacyHost host) => Host = host;
    internal LegacyHost Host { get; }
    public abstract JsonNode? Value { get; }
    internal abstract JsonNode? RawValue { get; }
    public IDisposable Subscribe(Action<JsonNode?> listener) => Host.Invoke(() =>
    { _listeners.Add(listener); Deliver(() => listener(Value)); return new LegacySubscription(() => Host.Invoke(() => _listeners.Remove(listener))); });
    public IDisposable Events(Action<LegacyStateEvent> listener) => Host.Invoke(() =>
    { _events.Add(listener); Deliver(() => listener(new("snap", Value))); return new LegacySubscription(() => Host.Invoke(() => _events.Remove(listener))); });
    internal virtual void Validate(JsonNode? value) { if (Host.Dev) foreach (var schema in ExposedSchemas) LegacySchema.Assert(schema, value, "state", "exposed state"); }
    internal void Notify(string type, IReadOnlyList<Delta.Op>? operations = null)
    {
        var events = _events.ToArray(); var listeners = _listeners.ToArray(); var value = Value;
        Host.Publish(() =>
        {
            foreach (var listener in events) if (_events.Contains(listener)) Deliver(() => listener(new(type, value?.DeepClone(), operations)));
            foreach (var listener in listeners) if (_listeners.Contains(listener)) Deliver(() => listener(value?.DeepClone()));
        });
    }
    private void Deliver(Action action) { try { action(); } catch (Exception error) { Host.Report(error); } }
    internal void Publish(JsonNode? value, string type, bool validate, bool diff = true)
    {
        var next = new Dictionary<LegacySource, JsonNode?> { [this] = value };
        var visited = new HashSet<LegacyDerived>(); var order = new List<LegacyDerived>();
        void Visit(LegacySource source) { foreach (var derived in source.Dependents) if (visited.Add(derived)) { Visit(derived); order.Add(derived); } }
        Visit(this); order.Reverse();
        if (validate) Validate(value);
        foreach (var derived in order) { var candidate = derived.Compute(s => s is LegacySource ls && next.TryGetValue(ls, out var replacement) ? replacement : s.Value); if (validate) derived.Validate(candidate); next[derived] = candidate; }
        var operations = next.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Delta.Op>)Delta.DeltaDiff.DiffRevisions(pair.Key.RawValue, pair.Value));
        // Every dependent adopts before any observer can run.
        foreach (var (source, candidate) in next) source.Install(candidate);
        using (Host.BatchPublication()) foreach (var source in next.Keys)
            if (type != "ops" || operations[source].Count != 0)
                source.Notify(type, type != "ops" ? null : ReferenceEquals(source, this) && !diff ? [new Delta.ReplaceOp(value)] : operations[source]);
    }
    internal abstract void Install(JsonNode? value);
}
public sealed class LegacyCell : LegacySource
{
    private JsonNode? _value;
    private bool _initialized;
    private readonly LegacyObject _owner;
    internal JsonNode Schema;
    internal LegacyCell(LegacyObject owner, string key, JsonNode schema, JsonNode? initial, bool initialized) : base(owner.Host)
    { _owner = owner; Key = key; Schema = schema.DeepClone(); _initialized = initialized; if (initialized) { LegacySchema.Assert(Schema, initial, "state", $"initial value of state {key}"); _value = initial?.DeepClone(); } }
    public string Key { get; }
    public override JsonNode? Value => Host.Invoke(() => _initialized ? _value?.DeepClone() : throw new ChordException("not_ready", $"state {Key} is not ready"));
    internal override JsonNode? RawValue => _initialized ? _value : throw new ChordException("not_ready", $"state {Key} is not ready");
    public void Change(LegacyContext context, Func<JsonNode?, JsonNode?> change) => Host.Invoke(() =>
    { _owner.Guard(context); var value = change(Value); LegacySchema.Assert(Schema, value, "state", $"state {Key}"); Publish(value?.DeepClone(), "ops", true); });
    public void Change(LegacyContext context, Action<JsonNode?> change) => Change(context, value =>
    {
        if (change.Method.IsDefined(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false)) throw new ArgumentException("cell change callbacks must be synchronous");
        change(value); return value;
    });
    public void Replace(LegacyContext context, JsonNode? value, bool diff = false) => Host.Invoke(() =>
    { _owner.Guard(context); _ = RawValue; Publish(value?.DeepClone(), "ops", true, diff); });
    internal override void Validate(JsonNode? value) { if (Host.Dev) LegacySchema.Assert(Schema, value, "state", $"state {Key}"); base.Validate(value); }
    internal void Initialize(JsonNode? value) { LegacySchema.Assert(Schema, value, "state", $"initial value of state {Key}"); _value = value?.DeepClone(); _initialized = true; }
    internal override void Install(JsonNode? value) { _value = value?.DeepClone(); _initialized = true; }
    internal void Reset(JsonNode? value, JsonNode schema) { Schema = schema.DeepClone(); Publish(value?.DeepClone(), "reset", false); }
}
public sealed class LegacyDerived : LegacySource, IDisposable
{
    private readonly IReadOnlyList<ILegacyReadable> _sources;
    private readonly Func<IReadOnlyList<JsonNode?>, JsonNode?> _compute;
    private JsonNode? _value;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _attached;
    internal LegacyDerived(LegacyHost host, IReadOnlyList<ILegacyReadable> sources, Func<IReadOnlyList<JsonNode?>, JsonNode?> compute) : base(host) { _sources = sources; _compute = compute; }
    public override JsonNode? Value => Host.Invoke(() => _value?.DeepClone());
    internal override JsonNode? RawValue => _value;
    internal JsonNode? Compute(Func<ILegacyReadable, JsonNode?> read) => _compute(_sources.Select(read).ToArray())?.DeepClone();
    internal void Attach(JsonNode? candidate = null, bool hasCandidate = false)
    {
        _value = hasCandidate ? candidate?.DeepClone() : Compute(s => s.Value); _attached = true;
        foreach (var source in _sources)
            if (source is LegacySource local) local.Dependents.Add(this);
            else _subscriptions.Add(source.Subscribe(_ => { if (_attached) Host.Invoke(() => { try { Publish(Compute(s => s.Value), "ops", true); } catch (Exception error) { Host.Report(error); } }); }));
    }
    internal override void Install(JsonNode? value) => _value = value?.DeepClone();
    public void Dispose() { _attached = false; foreach (var source in _sources) if (source is LegacySource local) local.Dependents.Remove(this); foreach (var subscription in _subscriptions) subscription.Dispose(); _subscriptions.Clear(); }
}
public sealed class LegacyResource<T> : ILegacyResourceHandle
{
    private readonly LegacyResourceCore _core;
    internal LegacyResource(LegacyResourceCore core) => _core = core;
    public string Key => _core.Key;
    public T Value => _core.Owner.Host.Invoke(() => _core.Owner.Host.InFactory ? throw new ChordException("not_ready", $"resource {Key} is not ready inside a factory") : (T)_core.Value!);
    internal LegacyResourceCore Core => _core;
    LegacyResourceCore ILegacyResourceHandle.Core => _core;
}
internal interface ILegacyResourceHandle { LegacyResourceCore Core { get; } }
internal sealed class LegacyExposed(LegacyResourceCore resource) { internal LegacyResourceCore Resource { get; } = resource; }
internal sealed class LegacyResourceCore(LegacyObject owner, string key)
{
    internal LegacyObject Owner { get; } = owner;
    internal string Key { get; } = key;
    internal int Version;
    internal bool Exclusive;
    internal IReadOnlyList<object> Dependencies = [];
    internal Func<CancellationToken, Task<object?>> Open = null!;
    internal Func<object?, Task>? Close;
    internal object? Value => IsOpen ? _value : throw new ChordException("not_ready", $"resource {Key} is not ready");
    private object? _value;
    internal bool IsOpen;
    internal async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (IsOpen) return;
        foreach (var dependency in Dependencies) { if (dependency is LegacyResourceCore resource) await resource.OpenAsync(cancellationToken); else if (dependency is LegacyReference reference) await reference.Ready.WaitAsync(cancellationToken); }
        var value = await Open(cancellationToken);
        if (cancellationToken.IsCancellationRequested) { if (Close is not null) await Close(value); cancellationToken.ThrowIfCancellationRequested(); }
        _value = value; IsOpen = true; Owner.OpenOrder.Add(this);
    }
    internal async Task CloseAsync() { if (!IsOpen) return; IsOpen = false; var value = _value; _value = null; Owner.OpenOrder.Remove(this); if (Close is not null) await Close(value); }
}
