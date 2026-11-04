// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/src/storage/store/documents.ts
//
// Durable's documents in the store's documents engine (slice 6b §1.5, R1): a Durable address as an engine address under the storage's
// owner name (its scope as the namespace, its kind and key JSON-encoded as Durable's indexed strings), a Durable record as an engine record
// and back (the Durable record in meta), and a document read at a point. A value crosses between the two JSON models as its text: an
// engine value is a JsonNode, a Durable one a JsonValue. Upstream's DocumentRead union is a record with a CurrentOnly flag.
using PiSharp.Chord.Sqlite;
using PiSharp.Chord.Store;
using PiSharp.Contracts;
using PiSharp.Durable.Storage;
using DurableJson = PiSharp.Durable.Chord.JsonValue;
using DurableObject = PiSharp.Durable.Chord.JsonObjectValue;
using EngineAddress = PiSharp.Chord.Store.DocumentAddress;
using EngineHistory = PiSharp.Chord.Store.DocumentHistory;
using EnginePoint = PiSharp.Chord.Store.DocumentPoint;
using EngineRecord = PiSharp.Chord.Store.DocumentRecord;

namespace PiSharp.Durable.Store;

/// <summary>A document read at a point, as a tagged result: request errors are told after the guarded read (slice 6b §1.4), never thrown
/// inside it (upstream <c>DocumentRead</c>): found (the document, or null when absent), or a record whose history does not reach back to
/// the point (<see cref="CurrentOnly"/>, with its id).</summary>
internal sealed record DocumentRead(StoredDocument? Document, DocumentId? CurrentOnly = null);

/// <summary>StoreStorage's documents (upstream <c>documents.ts</c>).</summary>
internal static class StoreDocuments
{
    /// <summary>The namespaces a storage claims under its owner: Durable's scopes (Chord's namespaces never contain a <c>:</c>).</summary>
    internal static readonly string[] ScopeNamespaces = ["session", "conversation:", "task:"];

    /// <summary>The engine namespace of a Durable scope: <c>session</c>, <c>conversation:&lt;id&gt;</c>, <c>task:&lt;id&gt;</c>.</summary>
    internal static string NamespaceOf(DocumentScope scope) => scope.Kind switch
    {
        DocumentScopeKind.Session => "session",
        DocumentScopeKind.Conversation => $"conversation:{scope.OwnerId}",
        _ => $"task:{scope.OwnerId}",
    };

    /// <summary>The engine address of a Durable address under <paramref name="owner"/>.</summary>
    internal static EngineAddress AddressOf(string kind, DocumentScope scope, string? key, string owner) =>
        new(owner, NamespaceOf(scope), StoreTables.EncodeIndexed(kind), key is null ? null : StoreTables.EncodeIndexed(key));

    /// <summary>The engine address of a Durable creation under <paramref name="owner"/>.</summary>
    internal static EngineAddress AddressOf(IDocumentDescriptor document, string owner) => AddressOf(document.Kind, document.Scope, document.Key, owner);

    /// <summary>What a record keeps: session and task documents and latest conversation ones their latest state.</summary>
    internal static EngineHistory HistoryOf(IDocumentDescriptor record) =>
        record.Scope.Kind == DocumentScopeKind.Conversation && record.History == DocumentHistory.Rewindable ? EngineHistory.Rewindable : EngineHistory.Latest;

    /// <summary>The engine record of a Durable creation at <paramref name="seq"/> (retired at once for an empty lifetime): the Durable record
    /// itself, its creation seq included, in meta, so a read of a live record parses one JSON text from one column, as SqliteStorage's does
    /// (a retirement later sets only the engine's <c>retired_at</c>).</summary>
    internal static EngineRecord EngineRecordOf(DocumentCreate create, string owner, long version, long seq, bool retired)
    {
        var address = AddressOf(create, owner);
        return new EngineRecord
        {
            Id = create.Id.Value,
            Owner = address.Owner,
            Namespace = address.Namespace,
            Kind = address.Kind,
            Key = address.Key,
            History = HistoryOf(create),
            CreatedAt = seq,
            RetiredAt = retired ? seq : null,
            Meta = create.ToRecord(new Seq(seq)).ToJson().ToJson(),
            Revision = 1,
            Version = version,
            Fingerprint = "",
        };
    }

    /// <summary>The Durable record of an engine record of this storage's owner and scopes, null for anyone else's (a Chord document, another
    /// owner's): its id is unknown to this storage.</summary>
    internal static DocumentRecord? DurableRecord(EngineRecord record, string owner) =>
        record.Meta is not { } meta || !OwnedHere(record.Owner, record.Namespace, owner) ? null : RecordOfEntry(meta, record.RetiredAt);

    /// <summary>The Durable record of an engine entry read at one of this storage's addresses or scopes (the record in meta).</summary>
    internal static DocumentRecord RecordOfEntry(DocumentEntry entry) => RecordOfEntry(entry.Meta!, entry.RetiredAt);

    private static DocumentRecord RecordOfEntry(string meta, long? retiredAt)
    {
        // Written by EngineRecordOf, parsed fresh: a retirement since is set on it.
        var record = DocumentRecord.FromJson(DurableJson.Parse(meta));
        return retiredAt is { } retired ? record with { RetiredAt = new Seq(retired) } : record;
    }

    /// <summary>Whether a record is this storage's: <paramref name="owner"/>'s, in one of Durable's scopes (not a Chord document of the
    /// owner).</summary>
    private static bool OwnedHere(string recordOwner, string @namespace, string owner) =>
        recordOwner == owner && (@namespace == "session" || @namespace.StartsWith("conversation:", StringComparison.Ordinal) ||
            @namespace.StartsWith("task:", StringComparison.Ordinal));

    /// <summary>The engine's point of a Durable point.</summary>
    internal static EnginePoint PointOf(DocumentPoint at) => at.Seq is { } seq ? EnginePoint.At(seq.Value) : EnginePoint.Current;

    /// <summary>Document <paramref name="id"/> of <paramref name="owner"/> at <paramref name="at"/>: absent (unknown, someone else's, not
    /// alive at <paramref name="at"/>), a record whose history does not reach back to <paramref name="at"/>, or its value from the base at
    /// or below <paramref name="at"/> and the deltas up to it.</summary>
    internal static async Task<DocumentRead> ReadAtAsync(ISqliteExecutor executor, DocumentId id, DocumentPoint at, string owner)
    {
        var entry = await DocumentEngine.ReadDocumentEntryAsync(executor, id.Value).ConfigureAwait(false);
        if (entry is null || entry.Meta is null || !OwnedHere(entry.Owner, entry.Namespace, owner)) return new DocumentRead(null);
        var record = RecordOfEntry(entry);
        if (at.Seq is not null && entry.History != EngineHistory.Rewindable) return new DocumentRead(null, id);
        if (!StorageRules.IsAliveAt(record, at)) return new DocumentRead(null);
        var loaded = await DocumentEngine.LoadContentAsync(executor, id.Value, PointOf(at)).ConfigureAwait(false);
        // The engine stores what Durable's commits gave it: JSON objects.
        var value = (DurableObject)DurableJson.Parse(JsonUtf16.ToJsonString(loaded.Value));
        return new DocumentRead(new StoredDocument(record, checked((int)loaded.Version), value, loaded.DeltasSinceBase));
    }
}
