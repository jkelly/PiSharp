// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/writes.ts
//
// The store's write vocabulary (StoreWrite, slice 6a §7, §8; decision "Writes carry their versions"): one change of the store each, as the
// outbox and archives record it. Upstream's StoreWrite is the union DocumentWrite | TableWrite; here it is an abstract record whose
// variants are the table writes (TableWrites.cs) and the documents engine's writes (DocumentWrite, engine.ts). Area cv2-store-tables added
// this file with the table writes, which a staged write returns; area cv2-store-engine, which owns writes.ts, adds DocumentWrite.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Store;

/// <summary>One change of the store, tagged by <see cref="Kind"/> (upstream <c>StoreWrite</c>): a table write or a document write.
/// Immutable.</summary>
public abstract record StoreWrite
{
    private protected StoreWrite()
    {
    }

    /// <summary>The write's tag, as upstream spells it: <c>table</c>, <c>index</c>, <c>dropIndex</c>, <c>alter</c>, <c>put</c>,
    /// <c>update</c>, <c>delete</c>, <c>updateWhere</c>, <c>deleteWhere</c>, or a <c>document.*</c> one.</summary>
    public abstract string Kind { get; }

    /// <summary>The write in upstream's data form, as the outbox and archives carry it (<c>{ kind, ... }</c>, in upstream's key order).</summary>
    public abstract JsonObject ToJson();

    /// <summary>The write's JSON text (<c>JSON.stringify</c>).</summary>
    public sealed override string ToString() => ChordJson.Stringify(ToJson());
}
