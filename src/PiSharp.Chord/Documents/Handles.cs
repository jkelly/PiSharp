// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/documents/handles.ts (SingletonHandle, KeyedHandle, HandleOf;
// in part)
//
// Document handles (slice 6a §1.2): what `self.state` returns, one per attempt and definition, a thin view of its object's slot bound to
// the attempt's seal token and the definition's version. Area cv2-documents-core ported the handles' public surface, the interfaces apps
// hold. The classes behind them (Singleton, Keyed) and KeyDeclarer, what an attempt's at(key) pins through, read and pin slot.ts's and
// keyed-slot.ts's slots and enter transaction.ts's transactions through Documents.tx, so they land with those (area cv2-documents-slots),
// as do Documents.declare, which makes them, and the handles' cases (handles.test.ts, which also needs createHost, phase 6). Mappings:
// - SingletonHandle<T> and KeyedHandle<T> are ISingletonHandle and IKeyedHandle: T is a static type (a document is a JsonObject). A
//   singleton handle is a watchable of the state layer's kind (IWatchable<JsonNode?>, whose value is the document's JsonObject), so a
//   member or a derive takes it as it takes any source.
// - A draft (upstream Draft<T>) is the JsonObject the change callback edits in place; a change callback returning nothing is an
//   Action<JsonObject>, one returning a result a Func. Upstream's undefined (an absent document) is null.
// - HandleOf<D> is a conditional type: a declaration's handle is an ISingletonHandle, or an IKeyedHandle for a keyed definition.
using System.Text.Json.Nodes;

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
