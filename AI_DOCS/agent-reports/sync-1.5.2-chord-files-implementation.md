# C-A2 — native Files implementation

Implemented against the reviewed Chord discovery and frozen `packages/chord/src/node/app/{files,files-index,files-entry,files-copy,blobs,uploads,folder-views}.ts` source.

## Public seam

- `FilesService.OpenAsync(ChordStore, directory, context, FilesCaps? = null, maxUploads = 64, hostKey = null, TimeProvider? = null)`; the caller owns/disposes this service before closing the store.
- `FilesFacet.Create(service)`, with `FilesFacet.Files` and `FilesFacet.FilesFolder` contracts. Bootstrap adds a `PluginFacet` for it. The server can pass its canonical host key; each worker should leave the key null because its database already has one worker owner, and a whole-store worker archive can then remap the worker address without rewriting the files document's key.
- Typed file/version/list/summary operations, upload lifecycle, `Folder(...)` watchable, `Summary` keyed read-only handle and `SnapshotBlobsAsync(...)`.
- `CopyFiles.CopyAsync(sourceStore, sourceDirectory, targetStore, targetDirectory, context, sourceHostKey = null, targetHostKey = null)`.

## Storage mapping

Native storage uses one versioned Store adapter document rather than upstream's four tables plus usage/summary documents. Address owner and namespace are `chord.app.files`, kind `index`, key and host are the supplied optional host key. The document contains every space's file rows, folders, persistent path high-water marks and summaries, plus blob reference counts and physical usage. The entire mutation is read/prepared and applied within the Store commit-series transaction; it produces a normal Store document write and participates in archives/outbox. Preparation refusals perform no writes. Reads validate imported metadata and derive physical usage/reference counts from file rows.

This mapping preserves commit atomicity while avoiding raw SQL or separate file-backed metadata. It trades indexed large-library query performance for simple complete snapshots: queries run over the in-memory committed snapshot, and each mutation serializes that snapshot. This is an explicit native representation choice, not a claim of byte-identical upstream Store tables.

## Behavior

Blobs are immutable files under `files/blobs/`. Uploads hold one file stream under `.uploads/<run>/`, accept offsets on a FIFO independently of outstanding writes, own copied input buffers and chain their writes. Their accepted bytes count against total storage. Finish waits writes and flushes before the index commit; cancellation/close/failure removes unfinished bytes. Startup removes previous upload runs and blobs without committed references. Copy shares blob references within a service; exporting to another store holds the source commit queue through snapshot acquisition and every blob read, then copies only this adapter's document and referenced blob bytes into a destination owned by the operation.

Cleanup distinguishes read-only preparation refusals from admitted metadata writes. Once preparation passed and writes may have begun, any exception preserves the installed blob/destination, because a COMMIT can have succeeded even when its completion or adoption fails. The next successful startup reconciles orphan blobs against durable metadata. This deliberately retains some data after an ambiguous failure instead of deleting bytes which committed metadata may reference.

Deletes retain path high-water marks, including remove-space; move retains version numbers and merges destination marks. Summaries count logical version bytes, while quotas charge each physical blob once. Folder and keyed-summary sources publish from the metadata commit batch and can be consumed by native Chord references. Folder windows hold the first 500 entries plus `more`; ordinary lists default to 100 and cap at 1000, folders preceding latest file versions.

## Verification handoff

`tests/PiSharp.Chord.Hosting.Tests/FilesCases.cs`, entry point `FilesCases.RunAsync()`, covers real SQLite persistence/reopen, idempotent writes/in-flight keys, path and folder conflicts, ordered accepted uploads and offset refusals, upload/byte quotas, range reads, shared blob deletion, high-water move/remove/recreation, public byte-bearing facet calls and native member watches, 501-folder windows and paging, orphan sweeps, expiry/cancellation and copy success/owned rollback. Injected successful-COMMIT-then-throw cases cover put, finish and copy; a gated metadata-read test verifies source removal waits until copy owns the bytes.

No builds or runtime tests were run by this owner; root owns serialized .NET validation. New files were reviewed statically. Requested service tier remains unverified.
