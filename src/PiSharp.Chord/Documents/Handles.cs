// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/handles.ts
//
// Document handles (slice 6a §1.2): what `self.state` returns, one per attempt and definition, a thin view of its object's slot bound to
// the attempt's seal token and the definition's version. Areas cv2-documents-core (the public surface, the interfaces apps hold) and
// cv2-documents-slots (the classes behind them, Singleton and Keyed, and KeyDeclarer) ported it. Mappings:
// - SingletonHandle<T> and KeyedHandle<T> are ISingletonHandle and IKeyedHandle: T is a static type (a document is a JsonObject). A
//   singleton handle is a watchable of the state layer's kind (IWatchable<JsonNode?>, whose value is the document's JsonObject), so a
//   member or a derive takes it as it takes any source.
// - A draft (upstream Draft<T>) is the JsonObject the change callback edits in place; a change callback returning nothing is an
//   Action<JsonObject>, one returning a result a Func. Upstream's undefined (an absent document) is null.
// - HandleOf<D> is a conditional type: a declaration's handle is an ISingletonHandle, or an IKeyedHandle for a keyed definition
//   (Documents.Declare returns the one its definition's Keyed picks).
// - Promises are Tasks: a refusal upstream's async methods reject with (the version fence of watch) is a faulted task here, not a throw.
//   The keyed handle's reads pass the slot an Access, its token and version (upstream passes the handle itself).
using System.Text.Json.Nodes;
using PiSharp.Chord.State;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;

namespace PiSharp.Chord.Documents;

/// <summary>A singleton document (upstream <c>SingletonHandle</c>): a watchable that always has a value (<c>initial()</c> until written,
/// and after a retire).</summary>
public interface ISingletonHandle : IWatchable<JsonNode?>
{
    /// <summary>One transaction that drafts this document and passes the draft to <paramref name="change"/>; completes with its result
    /// once committed.</summary>
    Task<TResult> ChangeAsync<TResult>(Func<JsonObject, TResult> change, ChordContext context);

    /// <inheritdoc cref="ChangeAsync{TResult}(Func{JsonObject, TResult}, ChordContext)"/>
    Task ChangeAsync(Action<JsonObject> change, ChordContext context);

    /// <summary>One transaction that retires this document: back to <c>initial()</c>.</summary>
    Task RetireAsync(ChordContext context);
}

/// <summary>
/// Keyed documents (upstream <c>KeyedHandle</c>): many per object, by string key. Not a watchable itself: each key is, through
/// <see cref="WatchAsync"/> and <see cref="At"/>; a live collection is a singleton holding it.
/// </summary>
public interface IKeyedHandle
{
    /// <summary>The document's value, null when absent (persisted and not in memory: loaded on the object's line).</summary>
    Task<JsonObject?> GetAsync(string key, ChordContext context);

    /// <summary>The keys of the present documents, in code point order.</summary>
    IAsyncEnumerable<string> Keys(ChordContext context);

    /// <summary>
    /// A watch of the document at <paramref name="key"/>, null when absent. It follows the address: a retirement is one frame with
    /// <c>retired: true</c> and <c>initial()</c>, a later creation a root replacement; it ends with the object.
    /// </summary>
    Task<IWatchHandle<JsonNode?>?> WatchAsync(string key, ChordContext context);

    /// <summary>
    /// In the factory only: the document at <paramref name="key"/> as a watchable (a state member, a derive source, an <c>on</c> source),
    /// <c>initial()</c> while absent, loaded before the object goes live and pinned while this attempt lives.
    /// </summary>
    IWatchable<JsonNode?> At(string key);

    /// <summary>One transaction that drafts the document at <paramref name="key"/> and passes the draft to <paramref name="change"/>;
    /// completes with its result once committed.</summary>
    Task<TResult> ChangeAsync<TResult>(string key, Func<JsonObject, TResult> change, ChordContext context);

    /// <inheritdoc cref="ChangeAsync{TResult}(string, Func{JsonObject, TResult}, ChordContext)"/>
    Task ChangeAsync(string key, Action<JsonObject> change, ChordContext context);

    /// <summary>One transaction that retires the document at <paramref name="key"/> (a no-op when absent).</summary>
    Task RetireAsync(string key, ChordContext context);
}

/// <summary>Documents whose keys are addresses of one declared host kind.</summary>
public interface IHostKeyedHandle
{
    HostKind KeyHost { get; }
    Task<JsonObject?> GetAsync(Address key, ChordContext context);
    IAsyncEnumerable<Address> Keys(ChordContext context);
    Task<IWatchHandle<JsonNode?>?> WatchAsync(Address key, ChordContext context);
    IWatchable<JsonNode?> At(Address key);
    Task<TResult> ChangeAsync<TResult>(Address key, Func<JsonObject, TResult> change, ChordContext context);
    Task ChangeAsync(Address key, Action<JsonObject> change, ChordContext context);
    Task RetireAsync(Address key, ChordContext context);
}

/// <summary>The address boundary around the existing string-keyed storage and watch machinery.</summary>
internal sealed class HostKeyed(IKeyedHandle inner, HostKind kind) : IHostKeyedHandle
{
    internal IKeyedHandle Inner { get; } = inner;
    public HostKind KeyHost { get; } = kind;

    internal string Key(Address key) => ContractsInternal.KeyOfAddress(KeyHost, key)
        ?? throw DataInternal.Invalid("args", $"expected an address of {KeyHost.Kind}");

    public Task<JsonObject?> GetAsync(Address key, ChordContext context)
    {
        try { return Inner.GetAsync(Key(key), context); }
        catch (Exception error) { return Task.FromException<JsonObject?>(error); }
    }
    public async IAsyncEnumerable<Address> Keys(ChordContext context)
    {
        await foreach (var key in Inner.Keys(context).ConfigureAwait(false))
            yield return ContractsInternal.AddressOfKey(KeyHost, key);
    }
    public Task<IWatchHandle<JsonNode?>?> WatchAsync(Address key, ChordContext context)
    {
        try { return Inner.WatchAsync(Key(key), context); }
        catch (Exception error) { return Task.FromException<IWatchHandle<JsonNode?>?>(error); }
    }
    public IWatchable<JsonNode?> At(Address key) => Inner.At(Key(key));
    public Task<TResult> ChangeAsync<TResult>(Address key, Func<JsonObject, TResult> change, ChordContext context)
    {
        try { return Inner.ChangeAsync(Key(key), change, context); }
        catch (Exception error) { return Task.FromException<TResult>(error); }
    }
    public Task ChangeAsync(Address key, Action<JsonObject> change, ChordContext context)
    {
        try { return Inner.ChangeAsync(Key(key), change, context); }
        catch (Exception error) { return Task.FromException(error); }
    }
    public Task RetireAsync(Address key, ChordContext context)
    {
        try { return Inner.RetireAsync(Key(key), context); }
        catch (Exception error) { return Task.FromException(error); }
    }
}

/// <summary>
/// A singleton's handle (upstream <c>Singleton</c>). Reads go to the slot (allowed after the seal); <see cref="ChangeAsync{TResult}"/> and
/// <see cref="RetireAsync"/> take the line under the attempt's token. Exposed as a member or read by a derive, it resolves to the slot itself.
/// </summary>
internal sealed class Singleton : ISingletonHandle
{
    public Singleton(SingletonSlot slot, SealToken token, long version)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(token);
        Slot = slot;
        Token = token;
        Version = version;
    }

    public SingletonSlot Slot { get; }

    public SealToken Token { get; }

    /// <summary>The version of the definition it was declared with: the slot refuses it once its values are newer (R15).</summary>
    public long Version { get; }

    public JsonNode? Value
    {
        get
        {
            Slot.Definition.AssertVersion(Version);
            return Slot.Value;
        }
    }

    public Task<IWatchHandle<JsonNode?>> WatchAsync(ChordContext context)
    {
        try
        {
            Slot.Definition.AssertVersion(Version);
            // The slot's values are this handle's documents.
            return Task.FromResult<IWatchHandle<JsonNode?>>(Slot.OpenWatch(context));
        }
        catch (Exception error)
        {
            return Task.FromException<IWatchHandle<JsonNode?>>(error);
        }
    }

    public Task<TResult> ChangeAsync<TResult>(Func<JsonObject, TResult> change, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Slot.Owner.TxAsync(async tx => change(await tx.DraftAsync(this).ConfigureAwait(false)), Token, context);
    }

    public Task ChangeAsync(Action<JsonObject> change, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ChangeAsync(draft =>
        {
            change(draft);
            return true;
        }, context);
    }

    public Task RetireAsync(ChordContext context) =>
        Slot.Owner.TxAsync(async tx =>
        {
            await tx.RetireAsync(this).ConfigureAwait(false);
            return true;
        }, Token, context);
}

/// <summary>Who declares an attempt's <c>at(key)</c> pins (upstream <c>KeyDeclarer</c>): its factory, which alone may (the attempt).</summary>
internal interface IKeyDeclarer
{
    /// <summary>Pins <paramref name="key"/> of <paramref name="slot"/> for the attempt and returns its source; throws outside the factory.</summary>
    KeySource At(KeyedSlot slot, string key);
}

/// <summary>A keyed handle (upstream <c>Keyed</c>): reads go to the slot; changes, retirements and cold reads take the line under the
/// attempt's token.</summary>
internal sealed class Keyed : IKeyedHandle
{
    private readonly IKeyDeclarer declarer;
    private readonly Access access;

    public Keyed(KeyedSlot slot, SealToken token, long version, IKeyDeclarer declarer)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(declarer);
        Slot = slot;
        Token = token;
        Version = version;
        this.declarer = declarer;
        access = new Access(token, version);
    }

    public KeyedSlot Slot { get; }

    public SealToken Token { get; }

    /// <summary>As <see cref="Singleton.Version"/>.</summary>
    public long Version { get; }

    public Task<JsonObject?> GetAsync(string key, ChordContext context) => Slot.GetAsync(key, access, context);

    public IAsyncEnumerable<string> Keys(ChordContext context) => Slot.Keys(access, context);

    public Task<IWatchHandle<JsonNode?>?> WatchAsync(string key, ChordContext context) => Slot.WatchAsync(key, access, context);

    // The source's values are this slot's documents.
    public IWatchable<JsonNode?> At(string key) => declarer.At(Slot, key);

    public Task<TResult> ChangeAsync<TResult>(string key, Func<JsonObject, TResult> change, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Slot.Owner.TxAsync(async tx => change(await tx.DraftAsync(this, key).ConfigureAwait(false)), Token, context);
    }

    public Task ChangeAsync(string key, Action<JsonObject> change, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ChangeAsync(key, draft =>
        {
            change(draft);
            return true;
        }, context);
    }

    public Task RetireAsync(string key, ChordContext context) =>
        Slot.Owner.TxAsync(async tx =>
        {
            await tx.RetireAsync(this, key).ConfigureAwait(false);
            return true;
        }, Token, context);
}
