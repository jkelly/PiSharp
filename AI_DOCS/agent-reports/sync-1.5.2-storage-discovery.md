# Storage discovery: Pi/Pi Durable 1.5.2

Discovery only. No source edits, builds, or test runs. Reference: `upstream-v152` at `b4afdddf44e059c67ccf5097e5317d913521b470`; comparison `f9a26365568127461c47a07a37dcb42c140a3ac5`. Native inspected: `update-1.5.2`, initially based on `9d2b64f94dcb357b293f6b31452dff36388849fe`. Requested worker tier ultrafast; effective tier unverified.

## Changed upstream inventory and applicability

| Upstream files | Change | Native destination/status |
| --- | --- | --- |
| `packages/durable/src/storage/jsonl/storage.ts`, `index.ts`, `node.ts` | Read-only opening, dedicated error, bounded recovery retries | `src/PiSharp.Durable/Storage/Jsonl/JsonlStorage.cs`, `Local/JsonlStorage.Local.cs`: missing |
| `packages/durable/src/storage/sqlite/commit.ts`, `rows.ts`, `statements.ts` (new), `storage.ts` | Prefetch check state, sequential in-memory checks, aggregate accepted writes, bounded SQL statements, decoding outside semantic-refusal handling | `src/PiSharp.Durable.Sqlite/Commit.cs`, `Storage.cs`: already substantially ported before this update; residual statement/coverage differences below |
| `packages/durable/src/storage/memory.ts`, `indexeddb.ts` | Chord import locations only | `Storage/Memory.cs` runtime unchanged; IndexedDB is browser-only, no native backend |
| `packages/durable/src/storage/store/{commit,documents,queries,storage,tables}.ts` | Chord `next` imports become root imports; Context/JsonValue use new modules | `PiSharp.Durable.Store` uses native namespaces already; no direct storage behavior delta in these files. Broader Chord runtime migration can require integration changes. |
| `packages/durable/src/testing/{commit-writes,storage-benchmark,storage-conformance}.ts` | Import paths only | `Testing/StorageConformance.cs`, `StorageBenchmark.cs` and commit extension require no behavioral port |
| `packages/durable/src/testing/env-conformance.ts` | Context import only | Owned by environment discovery, no storage behavior |
| `packages/durable/test/jsonl-storage.test.ts` | Four read-only scenarios and RacingEnv | Add native read-only regression file; no existing read-only cases found |
| `packages/durable/test/sqlite-commit.test.ts` (new) | Strong randomized differential, actual SQL shape/size/index checks, replacement regression | Existing `SqliteBulkCases.cs` covers narrower seeded differential, entry-only statement bounds, coarse large binding limits; expand |
| `packages/durable/test/sqlite-storage.test.ts` | Fault injection targets bulk reads/copy-source reads instead of obsolete per-batch reads | Existing `SqliteStorageCases.cs` already has transaction/copy/storage corruption tests |
| `packages/durable/test/{indexeddb-storage,store-storage,store-storage-commit,store-storage-support}.ts` | Import locations only | No standalone behavior delta |
| `packages/chord/src/store/live-query.ts` (new) | Commit-coherent bounded live queries | Entire facility absent; detailed contracts below |
| `packages/chord/src/store/host-data.ts` (new) | Host-scoped archive export/read/import/delete | Entire facility absent; depends on Chord host-keyed schema and host lifecycle work |

Relevant `packages/durable/CHANGELOG.md` v1.5.1 bullets: JSONL read-only storage; SQL commit calls taking a few statements independent of batch count; malformed stored document record rejects whole call as storage failure. v1.5.2 has no additional Durable bullet. `Tx.submission`, compaction, scheduler, codemode and search are other workers' contracts.

## JSONL contract (implementation required)

Public native additions:

```csharp
public sealed record JsonlStorageOptions {
    public bool Fsync { get; init; }
    public bool ReadOnly { get; init; } // false by default
}
public sealed class JsonlStorageReadOnlyException : Exception {
    // Message exactly: JSONL storage is open read-only
}
```

Both existing `OpenAsync` overloads accept the option through their existing signature. Read-only is a storage-open property, not inferred from `HarnessOptions.ReadOnly` and not a live refresh mode. Reads and ID minting use the recovered memory snapshot; a subsequent writer commit does not update it. `CommitAsync`, even an empty valid call, must reject with the dedicated exception before memory or file mutation, after the normal usable-state check. Closed-state precedence stays as existing upstream behavior.

Algorithm:

1. Resolve absolute directory and `main.jsonl` path as today; skip `CreateDirAsync` entirely when read-only. Missing directory must fail at directory listing without creating anything.
2. Each open attempt constructs a fresh `JsonlStorage` and memory store. Maximum five attempts including the initial attempt.
3. `ReadLinesAsync` becomes instance-aware and returns bytes-read length in `ParsedFile<T>` (full bytes including torn final fragment; missing file size zero). Record the main file size immediately after its successful parse.
4. Parse only complete newline-terminated records. Writable recovery still truncates torn final lines. Read-only skips them in memory and never calls truncate.
5. Read-only never removes `.reclaim` files, truncates unconfirmed sidecar tails, rewrites sidecars, removes expired sidecars, or invokes reclamation. It still performs exactly the same confirmation and corruption checks and loads confirmed logical state.
6. Retry only a `JsonlCorruptionException`, only read-only, only before attempt five, only when `FileInfoAsync(mainPath)` succeeds and its `Size` differs from the recorded main bytes-read size. Non-corruption failures and unchanged-main corruption fail immediately. This addresses a writer appending a marker and reclaiming a sidecar between the viewer's main and sidecar reads.
7. Writable open/commit behavior and binary file format remain unchanged.

Verification needed: exact before/after bytes for every file, and an IFileSystem wrapper recording *all* mutating operations, covering committed/live/terminal task records, unconfirmed sidecar record, torn main/sidecar fragments and leftover `.reclaim`; writable recovery still repairs the same fixture; torn-only main retained; absent directory untouched; snapshot after later writer commit; controlled race produces two main reads and terminal state; permanent corruption one main read; repeated races capped at five. The existing `InstrumentedFileSystem` does not count/create/read all needed operations; use a new wrapper in a new test file to avoid shared ownership conflicts.

## SQLite: already implemented, with a measurable remaining difference

`src/PiSharp.Durable.Sqlite/Commit.cs` is already sourced from upstream commit `7d073328e44337d3292a7db9b0f0308f679c5e97`. It genuinely issues bulk SQL: ID/document/version/current reads through `json_each(?)`, table inserts through `INSERT ... SELECT json_extract(...) FROM json_each(?)`, and large rows through multi-row `VALUES`. It is not a loop merely described as batching.

Already present contracts:

- One database transaction per `CommitAsync`, prefetch before semantic-refusal catch; stored JSON/shape decoding failures reject the transaction.
- Each accepted batch advances ID, document version, and current-address state; first semantic refusal ends accepted prefix; receipts only after transaction success.
- Last task/submission write wins; current-only document revision trimming accounts for intermediate bases and retirement; previous pending writes flush before copying, so copy sees prior accepted batches.
- String totals over 8 Ki characters use bound columns. Bound statement sizes are powers of two within 100 parameters. Small-row arrays chunk at 512 Ki characters with an oversized item allowed alone. Indexed strings preserve lone surrogates via JSON encoding.
- Existing tests check 256 entry-only batches consume five executor statements and contain all entries; a corruption matrix includes invalid record shape, missing base/revision, bad delta shape, version boundary, first/later fault and rollback; copy read failures are covered.

Difference: `CommitState.Read` additionally inserts **every loaded document's address** into its current-address prefetch map (line near 51). Upstream only prefetches addresses created/copied anywhere in this call. Therefore the upstream workload of N batches, each changing an existing document by delta and inserting an entry, should execute **8** statements independent of N; native currently adds a ninth address query. The current entry-only five-statement test does not detect this. The broad bounded-round-trip objective is achieved, but exact statement parity is not.

Do not remove that extra prefetch blindly: native shared `StorageRules.CheckDocumentActions` decrements a retirement only when `currentId(key) == id`, whereas upstream CommitState's private checker decrements an existing retirement and skips pure changes in live-count accounting. Options: add a SQLite-local check mirroring upstream (small scope, no changes to other backends), or coordinate a shared rule update and regress all backends. Prefer the first for disjoint implementation; keep persisted-corruption stricter native handling.

Required evidence additions in `SqliteBulkCases.cs` or a new file:

- Record actual SQL plus parameters, compare N=1 and N=150 doc-delta+entry workloads: eight calls and identical SQL shape if optimizing the remaining prefetch.
- 100 entries with 7,000-character content split into exactly two small JSON arrays, each at most 512 Ki characters; exact statement total.
- 19 large 5-column entry rows yield 80, 10, 5 bound parameters (16+2+1), with quote-heavy values round-tripping.
- Explain every JSON-driven batch read/write and assert no stored-table `SCAN` (virtual-table scan is expected).
- Replacement A -> B and third C in later batch refused for both create-before-retire and retire-before-create order; current B survives the A retirement.
- Broaden differential to submissions, copies, rewindable/current-only histories and compare grouped physical table rows to equivalent individual commits, not only visible latest values. Existing randomized test has entries/tasks/single current-only document and one deliberate duplicate root refusal.

## MSSQL native extension

No upstream MSSQL implementation exists, so the precise SQLite optimization is not a promised T-SQL API. Native currently remains per-batch/per-record SQL and must not be represented as having the SQLite statement batching. Porting a full T-SQL bulk pipeline is a separate native enhancement, not necessary to claim the upstream SQLite change.

However, the storage-failure correctness contract applies: `SqlServerStorage.CheckDocumentActions` currently runs `DocumentRecord.FromJson(Record(row))` inside `CommitAsync`'s `catch when (!transaction.CallFailed)`. A successful DB read followed by malformed persisted JSON leaves `CallFailed=false`, incorrectly returning semantic refusal (and potentially committing earlier batches). `ResolveCopies` likewise needs persisted decode failures separated from genuine missing/mismatched source semantic refusals. Proposed native fix: executor `MarkStorageFailure`/watched stored decode helper, or dedicated wrapper exception/rethrow at commit boundary, consistently for document records/base/revision decoding. Require injected malformed JSON and valid-JSON invalid-shape checks with earlier accepted batch; no receipts and no prefix persisted. This is a distinct implementation owner touching only MSSQL and its tests; do not assert runtime verification without an available local SQL Server test instance.

## Chord live query contracts (requested extension of discovery)

Absent: no `LiveQuery`, `LiveQueries`, `QueryRefresh`, `self.Query` or `chord.query.runs` implementation found. Current `ITableOwner` exposes Label/Scope/AssertLive/Fail only. Native infrastructure exists: `TableHandle`, `TableSql.Select`, `RowCodec`, `State.Source`, Batch, IStagedWrite, commit fences and host attempt lifetimes.

Proposed native surface following existing untyped table/JSON conventions:

```csharp
IWatchable<JsonNode?> Query(ITable table, Query query, long limit);
// Returned JSON value is the ordered array of decoded rows.
internal sealed class LiveQueries;   // per owner, indexed by SQL table
internal sealed class LiveQuery : Source;
internal sealed class QueryRefresh;  // staged read plus post-commit adoption
```

Declaration checks must enforce table ownership, exact TableDefinition identity in `Query`, positive safe-integer limit, and declaration-only lifetime. `LiveQueries` supports add/remove, table lookup and host-kind lookup. `QueryRefresh` deduplicates queries of written tables, stages reads at the last write phase, returns no recorded writes, clears prior runs on every application (group rollback may replay the step), and adopts only after commit, in the same Batch as the object's state. A commit not writing a table must run none of its queries.

`LiveQuery` compiles once, loads before activation publication, fails activation on initial read/decode failure, and tracks adoption generation so slow activation reads cannot overwrite a newer commit result. On rerun it captures failure instead of throwing; adoption fails only that query and reports one `query_failed` per continuous failure episode. Watches started during failure get error plus last good rows. A good run resets the episode and publishes a whole replacement if watchers had no value. Attempt detach removes it, makes reads throw the attempt error, ends watches, suppresses stale publication.

Row reuse is important: key is one stored scalar or canonical composite key; compare physical stored column values against prior adopted rows; reuse identical decoded row objects. Diff through existing revision-diff facility preserves insert/remove window changes. Native JsonNode has single-parent constraints: selecting a stable row value representation or cloning boundary must preserve the identity-based comparison goal without putting one node in multiple parent arrays. The implementer must explicitly resolve this rather than dropping identity reuse silently.

Split: dedicated `Store/LiveQuery.cs` plus new query tests owned by one worker. Host owner/attempt/declaration/staged-commit integration must be owned or patched by the Chord host worker (shared `Host/Attempt.cs`, `Host/Object.cs`, `Documents/Handles.cs`, host recording and transaction staging). Agree exact calls with that worker before edits. Host-kind query lookup depends on the host-column groundwork below; can follow once available.

## Chord host-data contracts and dependencies

Absent, including prerequisite fields: current DocumentAddress/DocumentRecord lacks Host; archive manifest lacks Host; archive column schema lacks host descriptor; no host column table metadata. This is a new feature requiring schema/migration and contract owner work, not an import rename.

Proposed additions (names can follow Chord worker's agreed naming):

```csharp
public sealed record HostImportOptions { public Address? Address { get; init; } public HostExisting Existing { get; init; } }
public sealed record HostDataCounts(long Documents, long Rows);
public sealed record HostArchive(string Host, IReadOnlyList<HostTable> Tables, IReadOnlyList<HostDocument> Documents);
// HostTable: table write, host column name, puts, indexes. HostDocument: record plus materialized JsonNode value.
```

Store-level functions: `ExportHostData(store,address,context)` yields archive parts under queue hold; `ReadHostArchive(parts,context)` parses eagerly to one checked archive; internal `TablesToMigrate`, `HostDataOwners`, `ImportHostData(store,archive,plan,commit,context)` and `DeleteHostData(store,key,commit,context)`. `HostCommit` contains CommitFence plus after-read staged writes; `HostImportPlan` contains canonical target, existing policy and table histories by SQL name. Public host methods belong to Chord host integration, not direct store mutation callers.

Read trust-boundary checks: manifest first exactly once, supported format/schema, canonical well-formed host; tables before documents, unique table name and exactly one host column of host kind; puts belong to open table before indexes and have exact host cell; live history-none records have host and key both equal manifest host, no retiredAt/meta, well-formed owner/namespace/kind, unique document logical address; revisions follow current record in increasing seq, start from base, deltas use base version and apply successfully, final base version equals record version; exact end write count and no later part. All violations `invalid/args`.

Import/delete: one atomic store commit, followed by supplied after-reads and only post-commit adoption. Target must have same address kind as archive. Default existing policy refuses any host data; replace first retires all host records and deletes matching rows. Existing table must be ready and not older than archive; migrate archive rows through live owner's histories when versions differ or archive is migrating; unknown tables created from archive metadata and indexes. Rewrite archived host cell to target before migrations; **re-check against recorded table's own host column after migration**. Refuse rows colliding with another address's primary key. Create records with fresh IDs/commit seq and base materialized values. Deletes count rows via counted SQL, update step.Rows, record deleteWhere writes; count documents from live host records.

Dependencies/ownership: Chord core storage owner supplies Host metadata, schema/index/migration, archive schema/export filter, host column descriptors, `ListHostRecords`, and migration helper; separate host-data worker owns new `Store/HostData.cs` and tests after those contracts settle. Chord host worker coordinates live owners, quiescence/fences, migration owner activation, after-state/query refresh and public host methods in `Host/HostData.cs`. Do not put all these in Durable storage worker or make conflicting edits to core store files.

## Recommended disjoint implementation packages

1. **I-STO-JSONL:** JsonlStorage.cs, Local opener docs, new JsonlReadOnlyCases.cs only. Root registers test enumeration. No shared IFileSystem edits needed.
2. **I-STO-SQLITE:** Sqlite Commit.cs/Storage.cs plus SqliteBulkCases.cs/new detailed bulk cases. SQLite-local checker preferred; do not touch shared StorageRules without coordinating other storage owners.
3. **I-STO-MSSQL:** SqlServerStorage.cs and new/existing SQL Server corruption tests; correctness failure classification only, document non-batched T-SQL explicitly.
4. **I-CHO-QUERY:** new Store/LiveQuery.cs and tests, coordinated hooks with host worker; prerequisite agreement on ITableOwner query registry/report method and host-column descriptor.
5. **I-CHO-HOSTDATA:** new Store/HostData.cs and tests after core host-keyed schema/archive/migration helpers; host public/lifetime integration belongs to host worker.

All build/test execution remains serialized by root. This report is static evidence; none of the proposed tests was run here.

## I-STO-JSONL implementation handoff

Root authorized I-STO-JSONL after discovery. Implemented only:

- `src/PiSharp.Durable/Storage/Jsonl/JsonlStorage.cs`: ReadOnly option, JsonlStorageReadOnlyException, no-write recovery, full-byte-size tracking and five-attempt corruption/race retry; read-only commit guard before memory/file mutation.
- `src/PiSharp.Durable/Storage/Jsonl/Local/JsonlStorage.Local.cs`: option behavior documentation; existing option passthrough preserved.
- `tests/PiSharp.Durable.Tests/Storage/JsonlReadOnlyCases.cs`: eight authored cases. The runner auto-discovers the `JsonlReadOnlyCases()` method, so no Program.cs registration edit is needed.

Case prefix `storage.jsonl.read-only.` and exact suffixes:

1. `skips-repair-and-reclamation-without-writes`
2. `keeps-torn-only-main`
3. `creates-no-missing-directory`
4. `local-open-holds-snapshot`
5. `retries-writer-reclamation-race`
6. `rejects-stable-corruption-without-retry`
7. `caps-recovery-at-five-attempts`
8. `does-not-retry-filesystem-failure`

The audit filesystem rejects and counts append/write/truncate/flush/rename/remove/create-dir/temp-create/cleanup calls. The mixed recovery fixture snapshots all filenames/bytes and includes torn lines, unconfirmed tails, a terminal task sidecar, old document base, and leftover reclaim file; it also confirms writable recovery still repairs the same files. Additional cases cover empty commit refusal, zero receipts, closed-state precedence, local opener passthrough, snapshot lifetime, actual writer reclamation, stable corruption and retry cap with full-size torn tails.

Static validation: `git diff --check` passed for owned source/test paths. No dotnet build or test execution performed, as instructed. Root should run the above prefix plus existing `storage.jsonl.` and runtime-boundary cases under its serialized validation.
