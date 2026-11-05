// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/chord/src/next/store/index.ts and README.md. index.ts only re-exports: the
// store for apps, the public types of PiSharp.Chord.Store. openStore, Store and StoreOptions are Tables.OpenStoreAsync, IStore and
// StoreOptions (Store.cs); Backend and sqliteBackend are Backend and Tables.SqliteBackend (Backend.cs); Outbox and OutboxCommit are Outbox
// and OutboxCommit (Outbox.cs); StoreWrite is StoreWrite (Writes.cs); defineTable, evolveTable, query, json, the expression helpers and
// compareText come from the table areas' files (Table.cs, TableEvolve.cs, Query.cs, JsonColumn.cs, Expression.cs, Text.cs), whose
// TypeScript-only types (Columns, PutRowOf, RowOf, KeyOf, PatchOf, SetOf, ColumnRefs) have no C# counterpart: rows are JSON objects
// checked at the boundary. ArchivePart and ArchiveManifest are a part's JSON, a JsonObject (Archive.cs); Store's export is IStore.Export,
// whose StoreExport iterates the parts (Export.cs); importStore is Tables.ImportStoreAsync (Import.cs); encodeJsonl and decodeJsonl are
// Tables.EncodeJsonl and Tables.DecodeJsonl (Jsonl.cs). What the README lists for adapters is public here, as upstream's next/index.ts
// exports it marked internal: StoreOwnerTables and OwnerTable (Adapter.cs), the documents engine (DocumentEngine, Engine.cs),
// ChordStore.CommitSeriesAsync, MintId and Ids (Store.cs) and Tables.ApplyBatchAsync (ApplyBatch.cs); refusalOf (Refusal.cs) and
// ChordStore's members that take Chord's internal types (the commit, the batch, the claims) are internal, for Chord's own layers.
// Everything is under the assembly's experimental attribute.
namespace PiSharp.Chord.Store;
