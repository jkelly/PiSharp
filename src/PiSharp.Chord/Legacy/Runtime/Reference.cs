using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

/// <summary>A held reference with independent release/status, stable state-member handles, and a consumer token.</summary>
public sealed class LegacyReference : IDisposable
{
    private readonly LegacyHost _host;
    internal readonly LegacyObject Object;
    internal readonly bool WantsState;
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<string, LegacyReferenceMember> _members = [];
    private readonly HashSet<Action<LegacyShape>> _shapes = [];
    private readonly HashSet<Action<string, Exception?>> _statuses = [];
    private string _status = "loading";
    private LegacyImplementation? _implementation;
    internal bool PendingBinding;
    internal LegacyReference(LegacyHost host, LegacyObject owner, LegacyContract contract, bool wantsState)
    { _host = host; Object = owner; Contract = contract; WantsState = wantsState; _ = _ready.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
    public LegacyContract Contract { get; }
    public string Status => _host.Invoke(() => _status);
    public Exception? Error { get; private set; }
    public Task Ready => _ready.Task;
    public string? Variant => _host.Invoke(() => Shape?.Variant);
    public LegacyShape? Shape { get; private set; }
    public ILegacyReadable Member(string name) => _host.Invoke(() =>
    { if (!_members.TryGetValue(name, out var member)) _members[name] = member = new LegacyReferenceMember(this, name); return (ILegacyReadable)member; });
    public object? Native(string name) => _host.Invoke(() =>
    {
        CheckLive(); var members = Contract.Variants[Contract.Scope == "local" ? "" : _implementation!.Variant!];
        if (!members.TryGetValue(name, out var spec) || spec is not LegacyOpaqueMember) throw new ChordException("not_in_variant", name + " is not opaque in this variant");
        var value = _implementation!.Members[name]; return value is LegacyExposed exposed ? exposed.Resource.Value : value;
    });
    public Task<object?> CallAsync(string name, LegacyContext? context = null) => Call(name, false, null, context);
    public Task<object?> CallAsync(string name, JsonNode? args, LegacyContext? context = null) => Call(name, true, args, context);
    public Task<object?> CallNativeAsync(string name, object? args, LegacyContext? context = null)
    {
        if (_host.Invoke(() => _host.InFactory)) return Task.FromException<object?>(new ChordException("not_ready", "method called inside a factory"));
        return _host.RunAsync(async () =>
        {
        if (Contract.Scope != "local") throw ChordException.Invalid("native", "native method values are local only");
        if (context is null) return await _host.RootAsync(ctx => CallNativeAsync(name, args, ctx));
        context.Check(_host); await _host.AwaitReady(this, context.CancellationToken); CheckLive();
        return await Object.CallNativeAsync(this, name, args, context);
        });
    }
    private Task<object?> Call(string name, bool hasArgs, JsonNode? args, LegacyContext? context)
    {
        if (_host.Invoke(() => _host.InFactory)) return Task.FromException<object?>(new ChordException("not_ready", "method called inside a factory"));
        return _host.RunAsync(async () =>
        {
        if (context is null) return await _host.RootAsync(ctx => Call(name, hasArgs, args, ctx));
        context.Check(_host); await _host.AwaitReady(this, context.CancellationToken); CheckLive();
        return await Object.CallAsync(this, name, hasArgs, args, context);
        });
    }
    internal void Changed()
    {
        if (_status is "released" or "gone" || Object.Current is not { } run || Object.Status != "live") return;
        if (WantsState && Object.RemoteContract is not null && !Object.RemoteStateReady) return;
        try
        {
            var adapter = LegacyContract.Bind(run.Service.Contract, run.Service.Options.Accepts, Contract);
            _implementation = adapter is null ? run.Implementation : adapter(run.Implementation);
            var variant = Contract.Scope == "local" ? "" : _implementation.Variant;
            if (variant is null || !Contract.Variants.TryGetValue(variant, out var members)) throw new ChordException("not_in_variant", "provider variant is absent from consumer contract");
            foreach (var (name, spec) in members)
            {
                if (!_implementation.Members.TryGetValue(name, out var value)) throw ChordException.Invalid("implementation", "adapter omitted " + name);
                if (spec is LegacyStateMember state)
                { if (value is not ILegacyReadable source) throw ChordException.Invalid("implementation", name + " must be readable"); if (WantsState) LegacySchema.Assert(state.RawSchema, source.Value, "state", name); }
            }
            Shape = new(Contract.Scope == "local" ? null : variant, members.Keys.ToArray(), run.Service.Contract.Fingerprint);
            SetStatus("live");
            PendingBinding = false;
            foreach (var member in _members.Values) member.Rebind();
            foreach (var listener in _shapes.ToArray()) Safe(() => listener(Shape));
            _ready.TrySetResult();
        }
        catch (Exception error)
        {
            if (PendingBinding && error is ChordException { Code: "contract_mismatch" }) { SetStatus("live"); _ready.TrySetResult(); return; }
            Gone(error);
        }
    }
    internal ILegacyReadable Source(string name)
    {
        CheckLive();
        if (_implementation is null) throw new ChordException("not_ready", "reference awaits the candidate implementation");
        if (!WantsState) throw new ChordException("not_ready", "method-only reference has no state");
        if (!_implementation!.Members.TryGetValue(name, out var value) || value is not ILegacyReadable source) throw new ChordException("not_in_variant", name + " is not state in this variant");
        return source;
    }
    private void CheckLive() { if (_status == "released") throw new ChordException("released", "reference was released"); if (_status == "gone") throw Error ?? new ChordException("gone", "reference is gone"); if (_status != "live" || _implementation is null) throw new ChordException("not_ready", "reference is not ready"); }
    public IDisposable OnShape(Action<LegacyShape> listener) => _host.Invoke(() => { _shapes.Add(listener); if (Shape is not null) Safe(() => listener(Shape)); return new LegacySubscription(() => _host.Invoke(() => _shapes.Remove(listener))); });
    public IDisposable OnStatus(Action<string, Exception?> listener) => _host.Invoke(() => { _statuses.Add(listener); return new LegacySubscription(() => _host.Invoke(() => _statuses.Remove(listener))); });
    private void Safe(Action listener) => _host.Publish(() => { try { listener(); } catch (Exception error) { _host.Report(error); } });
    private void SetStatus(string status) { if (_status == status) return; _status = status; foreach (var listener in _statuses.ToArray()) Safe(() => listener(status, Error)); }
    internal void Gone(Exception error)
    { if (_status is "released" or "gone") return; Error = error; SetStatus("gone"); _ready.TrySetException(error); foreach (var member in _members.Values) member.Detach(); _implementation = null; _shapes.Clear(); _statuses.Clear(); Object.Release(this); }
    internal void Loading()
    {
        if (_status is "released" or "gone" or "loading") return;
        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _ready.Task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        foreach (var member in _members.Values) member.Detach(); SetStatus("loading");
    }
    public void Release() => _host.Invoke(() =>
    { if (_status == "released") return; SetStatus("released"); _ready.TrySetException(new ChordException("released", "reference released before ready")); foreach (var member in _members.Values) member.Detach(); _implementation = null; _shapes.Clear(); _statuses.Clear(); Object.Release(this); });
    public void Dispose() => Release();
    private sealed class LegacyReferenceMember(LegacyReference owner, string name) : ILegacyReadable
    {
        private readonly HashSet<Action<JsonNode?>> _listeners = [];
        private readonly HashSet<Action<LegacyStateEvent>> _events = [];
        private IDisposable? _subscription;
        public JsonNode? Value => owner._host.Invoke(() => owner.Source(name).Value);
        public IDisposable Subscribe(Action<JsonNode?> listener) => owner._host.Invoke(() => { _listeners.Add(listener); if (owner._status == "live") { if (_subscription is null) Rebind(); else owner.Safe(() => listener(Value)); } return new LegacySubscription(() => owner._host.Invoke(() => _listeners.Remove(listener))); });
        public IDisposable Events(Action<LegacyStateEvent> listener) => owner._host.Invoke(() => { _events.Add(listener); if (owner._status == "live") { if (_subscription is null) Rebind(); else owner.Safe(() => listener(new("snap", Value))); } return new LegacySubscription(() => owner._host.Invoke(() => _events.Remove(listener))); });
        internal void Rebind()
        {
            Detach(); if (!owner.WantsState || owner._implementation is null || !owner._implementation.Members.TryGetValue(name, out var value) || value is not ILegacyReadable source) return;
            var first = true;
            _subscription = source.Events(e => { if (first) { first = false; return; } foreach (var listener in _listeners.ToArray()) owner.Safe(() => listener(e.Value?.DeepClone())); foreach (var listener in _events.ToArray()) owner.Safe(() => listener(e)); });
            if (owner._status == "live") { foreach (var listener in _listeners.ToArray()) owner.Safe(() => listener(source.Value)); foreach (var listener in _events.ToArray()) owner.Safe(() => listener(new("reset", source.Value))); }
        }
        internal void Detach() { _subscription?.Dispose(); _subscription = null; }
    }
}
