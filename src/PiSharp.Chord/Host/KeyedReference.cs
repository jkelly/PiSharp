// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/host/reference-keys.ts
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using PiSharp.Chord.Data;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Documents;
using PiSharp.Chord.State;

namespace PiSharp.Chord.Host;

// A reference only reads another object's keyed state. Its factory owns all writes.
internal sealed class ReferenceKeyed(Binding binding, string name) : IKeyedHandle, IUnmappedKeyedHandle
{
    private async Task<IKeyedHandle> Ready(ChordContext context)
    {
        binding.AssertUsable(); await binding.ReadyAsync(context); binding.AssertUsable();
        return binding.Target!.KeyedMember(name, binding.Contract.Version);
    }
    public Task<JsonObject?> GetAsync(string key, ChordContext context) => binding.Host.Dispatcher.RunAsync(async () => await (await Ready(context)).GetAsync(key, context));
    public Task<JsonObject?> GetUnmappedAsync(string key, ChordContext context) => binding.Host.Dispatcher.RunAsync(async () =>
    {
        var readable = await Ready(context);
        var raw = binding.Target is ChordObject local && local.FollowingAttempt?.Members.GetValueOrDefault(name) is IKeyedHandle owned ? owned : readable;
        return raw is IUnmappedKeyedHandle unmapped ? await unmapped.GetUnmappedAsync(key, context) : await raw.GetAsync(key, context);
    });
    public async IAsyncEnumerable<string> Keys(ChordContext context)
    {
        var handle = await binding.Host.Dispatcher.RunAsync(() => Ready(context));
        await foreach (var key in handle.Keys(context)) yield return key;
    }
    public Task<IWatchHandle<JsonNode?>?> WatchAsync(string key, ChordContext context) => binding.Host.Dispatcher.RunAsync(async () =>
    {
        // Existence is independent of the consumer's value mapping. Reading an adapted value just to test
        // presence would run that mapping once per watcher, in addition to the shared pinned-key mapping.
        if (await GetUnmappedAsync(key, context) is null) return null;
        var (source, release) = Hold(key);
        try
        {
            await binding.ReadyAsync(context);
            return (IWatchHandle<JsonNode?>)new KeyWatch(await source.WatchAsync(context), release);
        }
        catch { release(); throw; }
    });
    public IWatchable<JsonNode?> At(string key) => binding.Host.Dispatcher.Invoke(() =>
    {
        binding.AssertUsable();
        if (!binding.KeySources.TryGetValue((name, key), out var source))
        {
            source = new ReferenceKeySource(binding, name, key); binding.KeySources.Add((name, key), source); source.Retarget();
        }
        source.Pinned = true;
        return (IWatchable<JsonNode?>)source;
    });
    private (ReferenceKeySource Source, Action Release) Hold(string key)
    {
        if (!binding.KeySources.TryGetValue((name, key), out var source))
        { source = new ReferenceKeySource(binding, name, key); binding.KeySources.Add((name, key), source); source.Retarget(); }
        source.Users++;
        var released = false;
        return (source, () =>
        {
            if (released) return; released = true;
            if (binding.Ended) return;
            binding.Host.Dispatcher.Invoke(() =>
            {
                if (--source.Users > 0 || source.Pinned) return;
                binding.KeySources.Remove((name, key)); source.Release();
            });
        });
    }
    private sealed class KeyWatch : IWatchHandle<JsonNode?>
    {
        private readonly IWatchHandle<JsonNode?> source;
        private Action? release;
        internal KeyWatch(IWatchHandle<JsonNode?> source, Action release)
        { this.source = source; this.release = release; Closed = Closing(); }
        public JsonNode? Value => source.Value;
        public void Start(Func<WatchCall<JsonNode?>, ChordContext, Task> listener) => source.Start(listener);
        public async Task<WatchEnd> StopAsync() { try { return await source.StopAsync(); } finally { Interlocked.Exchange(ref release, null)?.Invoke(); } }
        public Task<WatchEnd> Closed { get; }
        private async Task<WatchEnd> Closing() { try { return await source.Closed; } finally { Interlocked.Exchange(ref release, null)?.Invoke(); } }
    }
    private static Exception ReadOnly() => DataInternal.Invalid("member", "a reference's keyed state is read-only");
    public Task<TResult> ChangeAsync<TResult>(string key, Func<JsonObject, TResult> change, ChordContext context) => Task.FromException<TResult>(ReadOnly());
    public Task ChangeAsync(string key, Action<JsonObject> change, ChordContext context) => Task.FromException(ReadOnly());
    public Task RetireAsync(string key, ChordContext context) => Task.FromException(ReadOnly());
}

internal sealed class ReferenceKeySource(Binding binding, string member, string key) : Source(binding.Host.Batch), ISubscriber
{
    private Source? source;
    private Action? unpin;
    private JsonNode? value, before;
    private Exception? error;
    private bool hasValue, framed, hadBefore, retired, released, pending;
    private bool heldFailure, toldFailure;
    private int generation;
    internal bool Pinned;
    internal int Users;
    public override JsonNode? Value => binding.Host.Dispatcher.Invoke(() =>
    { binding.AssertUsable(); if (error is not null && !heldFailure) throw error; if (!hasValue) throw error ?? HostErrors.NotReady(key); return value; });
    public override WatchStart Start() => error is not null ? WatchStart.Failure(error, hasValue, value) : WatchStart.Of(Value);
    internal void Retarget()
    {
        if (released) return;
        var version = ++generation;
        source?.Unsubscribe(this); source = null; unpin?.Invoke(); unpin = null;
        FinishPending();
        if (binding.Target is not ChordObject { FollowingAttempt.HasSources: true } target)
        {
            var targetState = binding.Target?.State ?? binding.RefState;
            if (targetState.Kind == "failed" && targetState.Cause is { } cause) Fail(cause.Error, holding: true);
            else if (targetState.Kind == "loading" && heldFailure) { error = null; heldFailure = false; }
            return;
        }
        try
        {
            var pinned = target.PinKey(member, key, binding.Contract.Version);
            unpin = pinned.Release; source = ChordHost.ResolveSource(pinned.Source) as Source;
            if (pinned.Loaded.IsCompletedSuccessfully) Adopt(pinned.Source.Value);
            else
            {
                pending = true; binding.PendingKeys++; binding.Moved();
                _ = Loaded();
                async Task Loaded()
                {
                    try
                    {
                        await pinned.Loaded;
                        if (!released && generation == version) Adopt(pinned.Source.Value);
                    }
                    catch (Exception failure) { if (!released && generation == version) Fail(failure); }
                    finally { if (generation == version) { FinishPending(); if (!binding.Ended) binding.Moved(); } }
                }
            }
            source?.Subscribe(this);
        }
        catch (Exception failure) { Fail(failure); }
    }
    private void Adopt(JsonNode? next)
    {
        Batch.Within(() => { Frame(); value = next; hasValue = true; error = null; heldFailure = false; Changed(); }, ChordContext.Background);
    }
    private void Frame() { if (!framed) { before = value; hadBefore = hasValue && error is null && !toldFailure; framed = true; } }
    private void Fail(Exception failure, bool holding = false) => Batch.Within(() =>
    {
        if (holding && error is not null) return;
        Frame(); error = failure; heldFailure = holding && hasValue; toldFailure = true; Changed();
    }, ChordContext.Background);
    private void FinishPending() { if (pending) { pending = false; binding.PendingKeys--; } }
    public void Advance(WatchCall<JsonNode?> call, ChordContext context)
    {
        if (call is WatchValueCall<JsonNode?> change) { retired |= change.Retired; Adopt(change.Value); }
        else if (call is WatchFailedCall<JsonNode?> failed) Fail(failed.Error as Exception ?? DataInternal.AsChordError(failed.Error, key));
    }
    public void End(WatchEnd end) { }
    public void EndAfterDelivery() { }
    public override void Deliver(ChordContext context)
    {
        if (!framed || released) return;
        framed = false;
        if (error is not null) { FailWatches(error, context); return; }
        var ops = hadBefore ? DeltaDiff.DiffRevisions(before, value) : ImmutableArray.Create<Op>(new ReplaceOp(value));
        if (ops.Length > 0 || retired) Publish(value, ops, retired, context);
        before = null; retired = false; toldFailure = false;
    }
    internal void Release()
    {
        if (released) return; released = true; generation++;
        source?.Unsubscribe(this); source = null; unpin?.Invoke(); unpin = null; FinishPending(); EndWatches();
    }
}
