// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/engine.ts
//
// The documents engine (S13, slice 6a §8, slice 6b §1.5): one record per incarnation of an address, revisions made of bases and deltas, a
// history rule per record (R1); its writes inside the commit step, checked by the writes themselves (the partial unique index on the live
// address, the counted conditional UPDATE on the record's revision), and its reads, of the current state or at a point. The functions
// next/index.ts exports for adapters (marked internal there) are DocumentEngine's public members; the rest are StoreInternal's. A value
// and a delta's ops are JsonNode trees (a delta's ops as their JSON tuples, which loadContent applies with Delta/Apply.cs; upstream checks
// their shapes there, not before). Upstream's DocumentPoint is `number | "current"` (DocumentPoint here), its optional record fields are
// null, and a key the address leaves out is a null Key (a singleton's column keeps "", which a keyed document's empty key shares). Its
// Error is an InvalidOperationException.
using System.Text.Json.Nodes;
using PiSharp.Chord.Delta;
using PiSharp.Chord.Sqlite;
using PiSharp.Contracts;

namespace PiSharp.Chord.Store;

// Inside the namespace, so the store's table names come before the documents layer's namespace (PiSharp.Chord.Documents) in name lookup.
using static PiSharp.Chord.Store.StoreInternal.StoreTables;

/// <summary>A document's address: the owner name, the adapter's namespace (claimed with the owner: Chord's <c>chord</c>, <c>chord.jobs</c>,
/// <c>chord.job-states</c>; Durable's scopes), the kind and, for a keyed document, its key; a singleton has none (upstream
/// <c>DocumentAddress</c>).</summary>
public sealed record DocumentAddress(string Owner, string Namespace, string Kind, string? Key = null, string? Host = null);

/// <summary>What a record keeps (R1), applied at a new base and at retirement (upstream <c>DocumentHistory</c>): <see cref="None"/>
/// (Chord) drops older revisions at a new base and the row and its revisions at retirement; <see cref="Latest"/> drops older revisions at a
/// new base and its revisions at retirement, keeping the row; <see cref="Rewindable"/> keeps everything.</summary>
public enum DocumentHistory { None, Latest, Rewindable }

/// <summary>A stored record: one incarnation of an address, live from <see cref="CreatedAt"/> until <see cref="RetiredAt"/> (half-open;
/// upstream <c>DocumentRecord</c>).</summary>
public sealed record DocumentRecord
{
    /// <summary>The record's id.</summary>
    public required long Id { get; init; }

    /// <summary>The owner name.</summary>
    public required string Owner { get; init; }

    /// <summary>The adapter's namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>The kind.</summary>
    public required string Kind { get; init; }

    /// <summary>A keyed document's key; null for a singleton (as stored records read: <c>""</c> reads as none).</summary>
    public string? Key { get; init; }

    /// <summary>The canonical address key of the host this record belongs to; absent for unscoped and older records.</summary>
    public string? Host { get; init; }

    /// <summary>What the record keeps.</summary>
    public required DocumentHistory History { get; init; }

    /// <summary>The seq that created it.</summary>
    public required long CreatedAt { get; init; }

    /// <summary>The seq that retired it, once retired.</summary>
    public long? RetiredAt { get; init; }

    /// <summary>The adapter's details (Durable: its fork policy and history).</summary>
    public string? Meta { get; init; }

    /// <summary>Counts its changes: a change names the revision it was prepared on.</summary>
    public required long Revision { get; init; }

    /// <summary>The version of its current base.</summary>
    public required long Version { get; init; }

    /// <summary>The schema fingerprint of its current base.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The record's address.</summary>
    public DocumentAddress Address => new(Owner, Namespace, Kind, Key, Host);

    /// <summary>The record in upstream's data form, as the outbox and archives carry it: <c>{ id, owner, namespace, kind, key?, history,
    /// createdAt, retiredAt?, meta?, revision, version, fingerprint }</c>.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["id"] = Id, ["owner"] = Owner, ["namespace"] = Namespace, ["kind"] = Kind };
        if (Key is not null) json["key"] = Key;
        json["history"] = StoreInternal.Name(History);
        json["createdAt"] = CreatedAt;
        if (RetiredAt is { } retiredAt) json["retiredAt"] = retiredAt;
        if (Meta is not null) json["meta"] = Meta;
        if (Host is not null) json["host"] = Host;
        json["revision"] = Revision;
        json["version"] = Version;
        json["fingerprint"] = Fingerprint;
        return json;
    }
}

/// <summary>A base: a whole value at a version (upstream <c>DocumentBase</c>).</summary>
public sealed record DocumentBase(long Version, string Fingerprint, JsonNode? Value)
{
    /// <summary><c>{ version, fingerprint, value }</c>.</summary>
    public JsonObject ToJson() => new() { ["version"] = Version, ["fingerprint"] = Fingerprint, ["value"] = Value?.DeepClone() };
}

/// <summary>A change's content: a new base, or a delta on the current base's version (upstream <c>DocumentContent</c>).</summary>
public abstract record DocumentContent(long Version)
{
    /// <summary><c>{ kind: "base", version, fingerprint, value }</c>.</summary>
    public sealed record Base(long Version, string Fingerprint, JsonNode? Value) : DocumentContent(Version);

    /// <summary><c>{ kind: "delta", version, ops }</c>: decoded operation tuples on the base's version.</summary>
    public sealed record Delta(long Version, JsonArray Ops) : DocumentContent(Version);

    /// <summary><c>base</c> or <c>delta</c>.</summary>
    public string Kind => this is Base ? "base" : "delta";

    /// <summary>The content in upstream's data form.</summary>
    public JsonObject ToJson() => this switch
    {
        Base value => new JsonObject { ["kind"] = Kind, ["version"] = Version, ["fingerprint"] = value.Fingerprint, ["value"] = value.Value?.DeepClone() },
        Delta delta => new JsonObject { ["kind"] = Kind, ["version"] = Version, ["ops"] = delta.Ops.DeepClone() },
        _ => throw new InvalidOperationException("An unknown content."),
    };
}

/// <summary>A stored revision's content: a base's value or a delta's ops, at a version; a revision keeps no fingerprint (upstream
/// <c>RevisionContent</c>).</summary>
public abstract record RevisionContent(long Version)
{
    /// <summary><c>{ kind: "base", version, value }</c>.</summary>
    public sealed record Base(long Version, JsonNode? Value) : RevisionContent(Version);

    /// <summary><c>{ kind: "delta", version, ops }</c>.</summary>
    public sealed record Delta(long Version, JsonArray Ops) : RevisionContent(Version);

    /// <summary><c>base</c> or <c>delta</c>.</summary>
    public string Kind => this is Base ? "base" : "delta";

    /// <summary>The content in upstream's data form.</summary>
    public JsonObject ToJson() => this switch
    {
        Base value => new JsonObject { ["kind"] = Kind, ["version"] = Version, ["value"] = value.Value?.DeepClone() },
        Delta delta => new JsonObject { ["kind"] = Kind, ["version"] = Version, ["ops"] = delta.Ops.DeepClone() },
        _ => throw new InvalidOperationException("An unknown content."),
    };
}

/// <summary>One stored revision as archives carry it: its document, its seq, its content (upstream <c>DocumentRevision</c>).</summary>
public sealed record DocumentRevision(long Id, long Seq, RevisionContent Content);

/// <summary>The engine's writes as the outbox and archives record them (upstream <c>DocumentWrite</c>). A creation carries its base in a
/// commit (stored at <c>createdAt</c>); in an archive it has none and its <c>document.revision</c>s follow it.</summary>
public abstract record DocumentWrite : StoreWrite
{
    private protected DocumentWrite()
    {
    }

    /// <summary><c>{ kind: "document.create", record, base? }</c>.</summary>
    public sealed record Create(DocumentRecord Record, DocumentBase? Base) : DocumentWrite
    {
        /// <inheritdoc/>
        public override string Kind => "document.create";

        /// <inheritdoc/>
        public override JsonObject ToJson()
        {
            var json = new JsonObject { ["kind"] = Kind, ["record"] = Record.ToJson() };
            if (Base is not null) json["base"] = Base.ToJson();
            return json;
        }
    }

    /// <summary><c>{ kind: "document.change", id, revision, content }</c>: a change prepared on <paramref name="Revision"/>.</summary>
    public sealed record Change(long Id, long Revision, DocumentContent Content) : DocumentWrite
    {
        /// <inheritdoc/>
        public override string Kind => "document.change";

        /// <inheritdoc/>
        public override JsonObject ToJson() => new() { ["kind"] = Kind, ["id"] = Id, ["revision"] = Revision, ["content"] = Content.ToJson() };
    }

    /// <summary><c>{ kind: "document.retire", id, revision }</c>.</summary>
    public sealed record Retire(long Id, long Revision) : DocumentWrite
    {
        /// <inheritdoc/>
        public override string Kind => "document.retire";

        /// <inheritdoc/>
        public override JsonObject ToJson() => new() { ["kind"] = Kind, ["id"] = Id, ["revision"] = Revision };
    }

    /// <summary><c>{ kind: "document.revision", id, seq, content }</c>: a kept revision, as an archive carries it.</summary>
    public sealed record KeptRevision(long Id, long Seq, RevisionContent Content) : DocumentWrite
    {
        /// <inheritdoc/>
        public override string Kind => "document.revision";

        /// <inheritdoc/>
        public override JsonObject ToJson() => new() { ["kind"] = Kind, ["id"] = Id, ["seq"] = Seq, ["content"] = Content.ToJson() };
    }
}

/// <summary>A loaded document: its record and its value (a base plus deltas applied once) at a point (upstream <c>StoredDocument</c>).</summary>
/// <param name="Record">The record.</param>
/// <param name="Value">The value.</param>
/// <param name="Version">The version of the base the value was built from.</param>
/// <param name="DeltasSinceBase">The deltas applied after its base (what <c>checkpointWhen</c> sees).</param>
public sealed record StoredDocument(DocumentRecord Record, JsonNode? Value, long Version, int DeltasSinceBase);

/// <summary><see cref="StoredDocument"/> without its record: what <see cref="DocumentEngine.LoadContentAsync"/> reads (upstream
/// <c>Omit&lt;StoredDocument, "record"&gt;</c>).</summary>
public sealed record LoadedContent(JsonNode? Value, long Version, int DeltasSinceBase);

/// <summary>Where a read looks: the current state, or the state after a seq (upstream <c>DocumentPoint</c>, <c>number | "current"</c>).</summary>
public readonly record struct DocumentPoint
{
    private DocumentPoint(long? seq) => Seq = seq;

    /// <summary>The seq the read looks after; null for the current state.</summary>
    public long? Seq { get; }

    /// <summary>The current state.</summary>
    public static DocumentPoint Current => default;

    /// <summary>The state after <paramref name="seq"/>.</summary>
    public static DocumentPoint At(long seq) => new(seq);

    /// <summary>The state after a seq.</summary>
    public static implicit operator DocumentPoint(long seq) => At(seq);

    /// <inheritdoc/>
    public override string ToString() => Seq?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "current";
}

/// <summary>A record as an adapter that keeps its own fields in <c>meta</c> reads it: the meta and, where a read can find retired records
/// (at a point, by id), when it was retired (upstream <c>DocumentEntry</c>, whose <c>retired_at</c> a read of the current state leaves
/// out: null here).</summary>
public record DocumentEntry(string? Meta, long? RetiredAt);

/// <summary>A document entry with what tells whose it is and what it keeps, as a read by id needs (upstream <c>OwnedDocumentEntry</c>).</summary>
public sealed record OwnedDocumentEntry(string? Meta, long? RetiredAt, string Owner, string Namespace, DocumentHistory History)
    : DocumentEntry(Meta, RetiredAt);

/// <summary>What <see cref="DocumentEngine.ListDocumentsAsync"/> scans: one owner's namespace, optionally one kind (upstream
/// <c>DocumentScope</c>).</summary>
public sealed record DocumentScope(string Owner, string Namespace, string? Kind = null);

/// <summary>The engine's functions for adapters (next/index.ts's exports of engine.ts, marked internal there).</summary>
public static class DocumentEngine
{
    /// <summary>Inserts <paramref name="record"/> as it is (id, seqs and revision kept: a creation, an import, a batch), with its base at
    /// <c>createdAt</c> when <paramref name="text"/> (the base's value serialized) is given; a record created retired (an empty lifetime)
    /// keeps what its history keeps (upstream <c>insertDocument</c>).</summary>
    public static async Task InsertDocumentAsync(Step step, DocumentRecord record, string? text)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(record);
        await step.RunAsync(
            $"INSERT INTO {Documents}\n\t\t\t(id, owner, namespace, kind, key, history, created_at, retired_at, meta, host, revision, version, fingerprint)\n" +
            "\t\t\tVALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            record.Id, record.Owner, record.Namespace, record.Kind, record.Key ?? "", StoreInternal.Name(record.History), record.CreatedAt,
            record.RetiredAt, record.Meta, record.Host, record.Revision, record.Version, record.Fingerprint).ConfigureAwait(false);
        if (text is not null) await StoreInternal.InsertRevisionAsync(step, record.Id, record.CreatedAt, "base", record.Version, text).ConfigureAwait(false);
        if (record.RetiredAt is not null) await StoreInternal.DropRetiredAsync(step, record.Id, record.History).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="content"/> to document <paramref name="id"/> (of <paramref name="history"/>) prepared on
    /// <paramref name="revision"/>, in the step's seq: its revision, the counted conditional <c>UPDATE</c> (a delta only on the base's
    /// version), and at a new base the older revisions a <c>none</c> or <c>latest</c> record drops. A record that is not live at
    /// <paramref name="revision"/> throws (memory and disk disagree; upstream <c>writeContent</c>).</summary>
    public static async Task WriteContentAsync(Step step, long id, DocumentHistory history, long revision, DocumentContent content, string text)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(content);
        // As ids: a revision past the safe range would stop counting exactly.
        if (revision >= StoreInternal.MaxSafeIntegerLong) throw new InvalidOperationException($"document {id}: no revision past the safe range");
        await StoreInternal.InsertRevisionAsync(step, id, step.Seq, content.Kind, content.Version, text).ConfigureAwait(false);
        var changed = content switch
        {
            DocumentContent.Delta => await step.RunCountedAsync(
                $"UPDATE {Documents} SET revision = revision + 1\n\t\t\t\t\t\tWHERE id = ? AND revision = ? AND version = ? AND retired_at IS NULL",
                id, revision, content.Version).ConfigureAwait(false),
            DocumentContent.Base value => await step.RunCountedAsync(
                $"UPDATE {Documents} SET revision = revision + 1, version = ?, fingerprint = ?\n\t\t\t\t\t\tWHERE id = ? AND revision = ? AND retired_at IS NULL",
                value.Version, value.Fingerprint, id, revision).ConfigureAwait(false),
            _ => throw new InvalidOperationException("An unknown content."),
        };
        if (changed != 1) throw new InvalidOperationException($"document {id}: not live at revision {revision}");
        if (content is DocumentContent.Base && history != DocumentHistory.Rewindable)
            await step.RunAsync($"DELETE FROM {Revisions} WHERE document_id = ? AND seq < ?", id, step.Seq).ConfigureAwait(false);
    }

    /// <summary>Retires live <paramref name="record"/> in the step's seq by its history (R1): a <c>none</c> record's row and revisions go, a
    /// <c>latest</c> one keeps its row with <c>retired_at</c> and drops its revisions, a <c>rewindable</c> one keeps everything (upstream
    /// <c>retireRecord</c>).</summary>
    public static async Task RetireRecordAsync(Step step, DocumentRecord record)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(record);
        if (record.History != DocumentHistory.None)
            await step.RunAsync($"UPDATE {Documents} SET retired_at = ? WHERE id = ?", step.Seq, record.Id).ConfigureAwait(false);
        await StoreInternal.DropRetiredAsync(step, record.Id, record.History).ConfigureAwait(false);
    }

    /// <summary>The record with <paramref name="id"/>, live or retired, if there is one (upstream <c>readDocument</c>).</summary>
    public static async Task<DocumentRecord?> ReadDocumentAsync(ISqliteExecutor executor, long id)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var row = await executor.GetAsync(StoreInternal.ReadSql, id).ConfigureAwait(false);
        return row is null ? null : StoreInternal.RecordOf(row);
    }

    /// <summary>Live records belonging to a canonical host address key.</summary>
    public static Task<IReadOnlyList<DocumentRecord>> ListHostRecordsAsync(ISqliteExecutor executor, string host, long after, long limit) =>
        StoreInternal.ListHostRecordsAsync(executor, host, after, limit);

    /// <summary>The record at <paramref name="address"/> live at <paramref name="at"/> (the half-open lifetime <c>createdAt &lt;= at &lt;
    /// retiredAt</c>), if any (upstream <c>findDocument</c>).</summary>
    public static async Task<DocumentRecord?> FindDocumentAsync(ISqliteExecutor executor, DocumentAddress address, DocumentPoint at = default)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(address);
        var key = address.Key ?? "";
        var row = at.Seq is { } seq
            ? await executor.GetAsync(StoreInternal.FindAtSql, address.Owner, address.Namespace, address.Kind, key, seq, seq).ConfigureAwait(false)
            : await executor.GetAsync(StoreInternal.FindCurrentSql, address.Owner, address.Namespace, address.Kind, key).ConfigureAwait(false);
        return row is null ? null : StoreInternal.RecordOf(row);
    }

    /// <summary>The entry of the record with <paramref name="id"/>, live or retired, with its owner, namespace and history; null if none
    /// (upstream <c>readDocumentEntry</c>).</summary>
    public static async Task<OwnedDocumentEntry?> ReadDocumentEntryAsync(ISqliteExecutor executor, long id)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var row = await executor.GetAsync($"SELECT meta, retired_at, owner, namespace, history FROM {Documents} WHERE id = ?", id).ConfigureAwait(false);
        return row is null
            ? null
            : new OwnedDocumentEntry(row["meta"] as string, SqliteValues.NullableInteger(row["retired_at"]), (string)row["owner"]!,
                (string)row["namespace"]!, StoreInternal.ParseHistory((string)row["history"]!));
    }

    /// <summary><see cref="FindDocumentAsync"/> reading only what an adapter keeping its fields in <c>meta</c> needs (fewer columns, no
    /// record built; upstream <c>findDocumentEntry</c>).</summary>
    public static async Task<DocumentEntry?> FindDocumentEntryAsync(ISqliteExecutor executor, DocumentAddress address, DocumentPoint at)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(address);
        var key = address.Key ?? "";
        var row = at.Seq is { } seq
            ? await executor.GetAsync(
                $"SELECT meta, retired_at FROM {Documents}\n\tWHERE owner = ? AND namespace = ? AND kind = ? AND key = ? AND created_at <= ? AND (retired_at IS NULL OR retired_at > ?)\n\tORDER BY created_at DESC LIMIT 1",
                address.Owner, address.Namespace, address.Kind, key, seq, seq).ConfigureAwait(false)
            : await executor.GetAsync(
                $"SELECT meta FROM {Documents}\n\tWHERE owner = ? AND namespace = ? AND kind = ? AND key = ? AND retired_at IS NULL",
                address.Owner, address.Namespace, address.Kind, key).ConfigureAwait(false);
        return row is null ? null : StoreInternal.EntryOf(row);
    }

    /// <summary>Loads <paramref name="record"/> at <paramref name="at"/>: its newest base at or below it and the deltas after that base up
    /// to it, applied once; the version is the base's. A record without such a base (one that keeps no history from there) throws
    /// (upstream <c>loadDocument</c>).</summary>
    public static async Task<StoredDocument> LoadDocumentAsync(ISqliteExecutor executor, DocumentRecord record, DocumentPoint at = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var content = await LoadContentAsync(executor, record.Id, at).ConfigureAwait(false);
        return new StoredDocument(record, content.Value, content.Version, content.DeltasSinceBase);
    }

    /// <summary><see cref="LoadDocumentAsync"/>'s content of document <paramref name="id"/> at <paramref name="at"/>, without its record
    /// (an adapter reads the fields it keeps; upstream <c>loadContent</c>).</summary>
    public static async Task<LoadedContent> LoadContentAsync(ISqliteExecutor executor, long id, DocumentPoint at)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var upper = at.Seq ?? StoreInternal.MaxSafeIntegerLong;
        var revisions = await executor.AllAsync(
            $"SELECT seq, kind, version, content FROM {Revisions} WHERE document_id = ? AND seq <= ? AND seq >= (\n" +
            $"\tSELECT max(seq) FROM {Revisions} WHERE document_id = ? AND kind = 'base' AND seq <= ?) ORDER BY seq",
            id, upper, id, upper).ConfigureAwait(false);
        if (revisions.Count == 0 || revisions[0]["kind"] as string != "base") throw new InvalidOperationException($"document {id}: no base");
        var version = SqliteValues.Integer(revisions[0]["version"]);
        var value = JsonUtf16.MutableNode((string)revisions[0]["content"]!);
        for (var index = 1; index < revisions.Count; index++)
        {
            if (SqliteValues.Integer(revisions[index]["version"]) != version) throw new InvalidOperationException($"document {id}: a delta crosses a version");
            value = DeltaApply.Apply(value, JsonUtf16.MutableNode((string)revisions[index]["content"]!)!.AsArray());
        }
        return new LoadedContent(value, version, revisions.Count - 1);
    }

    /// <summary>Up to <paramref name="limit"/> records of <paramref name="scope"/> live at <paramref name="at"/> with ids above
    /// <paramref name="after"/>, by id (upstream <c>listDocuments</c>).</summary>
    public static async Task<IReadOnlyList<DocumentRecord>> ListDocumentsAsync(ISqliteExecutor executor, DocumentScope scope, DocumentPoint at, long after, long limit)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(scope);
        var from = $"SELECT {StoreInternal.RecordColumns} FROM {Documents} WHERE owner = ? AND namespace = ? AND id > ?";
        var rows = await StoreInternal.ListAsync(executor, from, from, scope, at, after, limit).ConfigureAwait(false);
        return [.. rows.Select(StoreInternal.RecordOf)];
    }

    /// <summary><see cref="ListDocumentsAsync"/> reading only what an adapter keeping its fields in <c>meta</c> needs (upstream
    /// <c>listDocumentEntries</c>).</summary>
    public static async Task<IReadOnlyList<DocumentEntry>> ListDocumentEntriesAsync(ISqliteExecutor executor, DocumentScope scope, DocumentPoint at, long after, long limit)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(scope);
        string From(string columns) => $"SELECT {columns} FROM {Documents} WHERE owner = ? AND namespace = ? AND id > ?";
        var rows = await StoreInternal.ListAsync(executor, From("meta"), From("meta, retired_at"), scope, at, after, limit).ConfigureAwait(false);
        return [.. rows.Select(StoreInternal.EntryOf)];
    }
}

internal static partial class StoreInternal
{
    /// <summary>The columns of a record read (upstream <c>RECORD_COLUMNS</c>).</summary>
    internal const string RecordColumns = "id, owner, namespace, kind, key, history, created_at, retired_at, meta, host, revision, version, fingerprint";

    /// <summary>A record by id (upstream <c>READ</c>).</summary>
    internal static readonly string ReadSql = $"SELECT {RecordColumns} FROM {Documents} WHERE id = ?";

    /// <summary>The live record at an address (upstream <c>FIND_CURRENT</c>).</summary>
    internal static readonly string FindCurrentSql =
        $"SELECT {RecordColumns} FROM {Documents}\n\tWHERE owner = ? AND namespace = ? AND kind = ? AND key = ? AND retired_at IS NULL";

    /// <summary>The record at an address live at a point (upstream <c>FIND_AT</c>).</summary>
    internal static readonly string FindAtSql =
        $"SELECT {RecordColumns} FROM {Documents}\n\tWHERE owner = ? AND namespace = ? AND kind = ? AND key = ? AND created_at <= ? AND (retired_at IS NULL OR retired_at > ?)\n\tORDER BY created_at DESC LIMIT 1";

    /// <summary><c>none</c>, <c>latest</c> or <c>rewindable</c>.</summary>
    internal static string Name(DocumentHistory history) => history switch
    {
        DocumentHistory.None => "none",
        DocumentHistory.Latest => "latest",
        _ => "rewindable",
    };

    /// <summary>A stored history.</summary>
    internal static DocumentHistory ParseHistory(string history) => history switch
    {
        "none" => DocumentHistory.None,
        "latest" => DocumentHistory.Latest,
        "rewindable" => DocumentHistory.Rewindable,
        _ => throw new InvalidOperationException($"store: an unknown document history {history}"),
    };

    /// <summary>Stages the creation of a Chord document at <paramref name="address"/> with <paramref name="base"/>; its id is taken from
    /// the allocator when it applies (a saturated allocator refuses the commit) and handed to <paramref name="created"/> (its owner adopts
    /// it after <c>COMMIT</c>). <paramref name="text"/> is the base's value serialized before the queue (rule 8; upstream
    /// <c>createDocument</c>).</summary>
    internal static IStagedWrite CreateDocument(DocumentAddress address, DocumentBase @base, string text, Action<long>? created = null) =>
        new StagedWrite(WriteOrder.Create, async step =>
        {
            var id = step.Ids.Mint();
            step.Created.Add(id);
            created?.Invoke(id);
            var record = new DocumentRecord
            {
                Id = id,
                Owner = address.Owner,
                Namespace = address.Namespace,
                Kind = address.Kind,
                Key = address.Key,
                Host = address.Host,
                History = DocumentHistory.None,
                CreatedAt = step.Seq,
                Revision = 1,
                Version = @base.Version,
                Fingerprint = @base.Fingerprint,
            };
            await DocumentEngine.InsertDocumentAsync(step, record, text).ConfigureAwait(false);
            return [new DocumentWrite.Create(record, @base)];
        });

    /// <summary>Stages a change of Chord document <paramref name="id"/> prepared on <paramref name="revision"/>: a delta on the current
    /// base's version, or a new base. <paramref name="text"/> is the content serialized before the queue (upstream
    /// <c>changeDocument</c>).</summary>
    internal static IStagedWrite ChangeDocument(long id, long revision, DocumentContent content, string text)
    {
        var write = new DocumentWrite.Change(id, revision, content);
        return new StagedWrite(WriteOrder.Write, async step =>
        {
            await DocumentEngine.WriteContentAsync(step, id, DocumentHistory.None, revision, content, text).ConfigureAwait(false);
            return [write];
        });
    }

    /// <summary>Stages the retirement of Chord document <paramref name="id"/> (history <c>none</c>) at <paramref name="revision"/>: its
    /// row and its revisions go (upstream <c>retireDocument</c>).</summary>
    internal static IStagedWrite RetireDocument(long id, long revision)
    {
        var write = new DocumentWrite.Retire(id, revision);
        return new StagedWrite(WriteOrder.Retire, async step =>
        {
            // A Chord document keeps no history (`none`): its row goes with a conditional, counted DELETE (6a's).
            var deleted = await step.RunCountedAsync($"DELETE FROM {Documents} WHERE id = ? AND revision = ? AND retired_at IS NULL", id, revision)
                .ConfigureAwait(false);
            if (deleted != 1) throw new InvalidOperationException($"document {id}: not live at revision {revision}");
            await step.RunAsync($"DELETE FROM {Revisions} WHERE document_id = ?", id).ConfigureAwait(false);
            return [write];
        });
    }

    /// <summary>What a retired record of <paramref name="history"/> drops: a <c>none</c> row too, every revision unless
    /// <c>rewindable</c> (upstream <c>dropRetired</c>).</summary>
    internal static async Task DropRetiredAsync(Step step, long id, DocumentHistory history)
    {
        if (history == DocumentHistory.Rewindable) return;
        if (history == DocumentHistory.None) await step.RunAsync($"DELETE FROM {Documents} WHERE id = ?", id).ConfigureAwait(false);
        await step.RunAsync($"DELETE FROM {Revisions} WHERE document_id = ?", id).ConfigureAwait(false);
    }

    /// <summary>Inserts one revision of document <paramref name="id"/> at <paramref name="seq"/>, <paramref name="text"/> its serialized
    /// content (upstream <c>insertRevision</c>).</summary>
    internal static Task InsertRevisionAsync(Step step, long id, long seq, string kind, long version, string text) =>
        step.RunAsync($"INSERT INTO {Revisions} (document_id, seq, kind, version, content) VALUES (?, ?, ?, ?, ?)", id, seq, kind, version, text);

    /// <summary>Up to <paramref name="limit"/> keys of live documents of <paramref name="address"/>'s kind under its owner after
    /// <paramref name="after"/> (from the first), in code point order (upstream <c>listDocumentKeys</c>; the address's key is not used).</summary>
    internal static async Task<IReadOnlyList<string>> ListDocumentKeysAsync(ISqliteExecutor executor, DocumentAddress address, string? after, long limit)
    {
        // The first page has no lower bound: "" is a key too.
        var from = after is null ? "" : " AND key > ?";
        var sql = $"SELECT key FROM {Documents} WHERE owner = ? AND namespace = ? AND kind = ?{from}\n\t\t\tAND retired_at IS NULL ORDER BY key LIMIT ?";
        object?[] parameters = after is null
            ? [address.Owner, address.Namespace, address.Kind, limit]
            : [address.Owner, address.Namespace, address.Kind, after, limit];
        var rows = await executor.AllAsync(sql, parameters).ConfigureAwait(false);
        return [.. rows.Select(row => (string)row["key"]!)];
    }

    /// <summary>Up to <paramref name="limit"/> stored records, live and retired, with ids above <paramref name="after"/>, by id (an
    /// export's pages; upstream <c>listRecords</c>).</summary>
    internal static async Task<IReadOnlyList<DocumentRecord>> ListRecordsAsync(ISqliteExecutor executor, long after, long limit)
    {
        var rows = await executor.AllAsync($"SELECT {RecordColumns} FROM {Documents} WHERE id > ? ORDER BY id LIMIT ?", after, limit).ConfigureAwait(false);
        return [.. rows.Select(RecordOf)];
    }

    /// <summary>Live records belonging to one host, ordered by id. Older unscoped records never match.</summary>
    internal static async Task<IReadOnlyList<DocumentRecord>> ListHostRecordsAsync(ISqliteExecutor executor, string host, long after, long limit)
    {
        var rows = await executor.AllAsync($"SELECT {RecordColumns} FROM {Documents} WHERE host = ? AND id > ? AND retired_at IS NULL ORDER BY id LIMIT ?", host, after, limit).ConfigureAwait(false);
        return [.. rows.Select(RecordOf)];
    }

    /// <summary>Up to <paramref name="limit"/> stored revisions of document <paramref name="id"/> after seq <paramref name="after"/>, in
    /// seq order (an export's page; upstream <c>listRevisions</c>).</summary>
    internal static async Task<IReadOnlyList<DocumentRevision>> ListRevisionsAsync(ISqliteExecutor executor, long id, long after, long limit)
    {
        var rows = await executor.AllAsync(
            $"SELECT seq, kind, version, content FROM {Revisions} WHERE document_id = ? AND seq > ? ORDER BY seq LIMIT ?", id, after, limit).ConfigureAwait(false);
        return [.. rows.Select(row =>
        {
            // Written by InsertRevisionAsync: a base's value or a delta's ops, as JSON.
            var parsed = JsonUtf16.MutableNode((string)row["content"]!);
            var version = SqliteValues.Integer(row["version"]);
            RevisionContent content = row["kind"] as string == "base" ? new RevisionContent.Base(version, parsed) : new RevisionContent.Delta(version, parsed!.AsArray());
            return new DocumentRevision(id, SqliteValues.Integer(row["seq"]), content);
        })];
    }

    /// <summary>The statements of a list by its shape: with a kind or not, current or at a point (upstream <c>LIST</c>,
    /// <c>LIST_ENTRIES</c>).</summary>
    internal static Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(ISqliteExecutor executor, string current, string atPoint,
        DocumentScope scope, DocumentPoint at, long after, long limit)
    {
        const string currentTail = " AND retired_at IS NULL ORDER BY id LIMIT ?";
        const string atTail = " AND created_at <= ? AND (retired_at IS NULL OR retired_at > ?) ORDER BY id LIMIT ?";
        var (owner, @namespace, kind) = (scope.Owner, scope.Namespace, scope.Kind);
        if (kind is null)
        {
            return at.Seq is { } seq
                ? executor.AllAsync(atPoint + atTail, owner, @namespace, after, seq, seq, limit)
                : executor.AllAsync(current + currentTail, owner, @namespace, after, limit);
        }
        return at.Seq is { } point
            ? executor.AllAsync(atPoint + " AND kind = ?" + atTail, owner, @namespace, after, kind, point, point, limit)
            : executor.AllAsync(current + " AND kind = ?" + currentTail, owner, @namespace, after, kind, limit);
    }

    /// <summary>A record of a row (upstream <c>recordOf</c>): an empty key, a <c>NULL</c> retirement or meta read as none.</summary>
    internal static DocumentRecord RecordOf(IReadOnlyDictionary<string, object?> row)
    {
        var key = (string)row["key"]!;
        return new DocumentRecord
        {
            Id = SqliteValues.Integer(row["id"]),
            Owner = (string)row["owner"]!,
            Namespace = (string)row["namespace"]!,
            Kind = (string)row["kind"]!,
            Key = key.Length == 0 ? null : key,
            History = ParseHistory((string)row["history"]!),
            CreatedAt = SqliteValues.Integer(row["created_at"]),
            RetiredAt = SqliteValues.NullableInteger(row["retired_at"]),
            Meta = row["meta"] as string,
            Host = row.TryGetValue("host", out var host) ? host as string : null,
            Revision = SqliteValues.Integer(row["revision"]),
            Version = SqliteValues.Integer(row["version"]),
            Fingerprint = (string)row["fingerprint"]!,
        };
    }

    /// <summary>An entry of a row: its meta and, when the read selected it, its retirement.</summary>
    internal static DocumentEntry EntryOf(IReadOnlyDictionary<string, object?> row) =>
        new(row["meta"] as string, row.TryGetValue("retired_at", out var retired) ? SqliteValues.NullableInteger(retired) : null);
}
