# C-HD — host-keyed state and live host-data integration

Implementation in the isolated `update-1.5.2` worktree, against upstream `b4afdddf44e059c67ccf5097e5317d913521b470`. No builds or .NET test runs performed by this worker; root owns serialized validation. The requested execution tier cannot be verified by this worker.

## Shared API decisions

Agreed with C-K and C-STORE before boundary edits:

- `StateOptions.KeyHost : HostKind?`, `StateDefinition.KeyHost`, with host keys implying the existing `Keyed` flag. Existing string-keyed APIs remain.
- `IHostKeyedHandle` carries `KeyHost` and Address-valued `GetAsync`, `Keys`, `WatchAsync`, `At`, `ChangeAsync`, `RetireAsync`. `ITx` gains matching Address overloads for draft/retire. `HostKeyed` is a distinct public-interface wrapper; its internal `Inner` feeds existing pinning and adaptation machinery.
- `Schemas.Keyed(HostKind, JsonNode)` and editor `Keyed`/`AddKeyed` host overloads preserve the old overloads. `Snapshot.KeyHost` is optional, so existing keyed fingerprint nodes remain byte-identical; host keyed nodes append `key: kindName` in upstream order. Accepts refuses key-kind changes because a value mapper cannot map keys.
- C-STORE owns trailing `DocumentAddress.Host`/`DocumentRecord.Host`, SQLite/schema/archive migration, host table columns, Store/HostData and LiveQuery. Documents persist their canonical key as `Host`, and refuse ownership reinterpretation when loading or retiring stored data.
- C-K owns `ISelf.HostKeyedState`, `IReference.HostKeyedMember`, `IStoreSelf.Query`, recording metadata/source validation, partial-class declarations, activation gating, object query registries and query detach. The gate wraps hydration, table preparation, document and query loads; native opening is outside the gate to avoid user callback deadlocks.
- C-HD owns new `Host/HostData.cs`, implementing the partial-class methods above plus public host data methods. Query construction uses C-STORE `LiveQuery(TableHandle, Query, long, IQueryOwner, Batch)`; adoption uses `QueryRefresh`. Transactions request `StoreInternal.RefreshOf` only for table change objects whose `HasWrites` is true.

## Public host-data methods

```csharp
StoreExport ChordHost.ExportHostData(Address address, ChordContext context);
Task<HostDataCounts> ChordHost.ImportHostDataAsync(
    IEnumerable<JsonNode?> parts, ChordContext context, HostImportOptions? options = null);
Task<HostDataCounts> ChordHost.ImportHostDataAsync(
    IAsyncEnumerable<JsonNode?> parts, ChordContext context, HostImportOptions? options = null);
Task<HostDataCounts> ChordHost.DeleteHostDataAsync(Address address, ChordContext context);
```

`HostImportOptions`, `HostExisting`, `HostDataCounts` are defined by C-STORE. The host validates the archive in full before queuing its operation, gathers needed table histories, activates a missing served local owner between exclusive sections, holds affected document lines in identity order, re-reads resident/pinned keys and live queries after writes, and adopts in one postcommit batch. Unreadable changed documents fail their owner after that batch. Temporary references and line holds release on all paths. Admission observes caller cancellation; admitted work receives a cancellation-free context.

`HostDataGate` serializes operation turns FIFO and excludes activation load readers only inside exclusive sections. A canceled waiting turn still chains through its predecessor; a canceled line waiter releases the line when its pending acquisition eventually finishes.

`ContractsInternal.KeyOfAddress(HostKind, object?)` accepts typed and JSON address values, validates exact parameter fields and primitive/literal types, and recomputes keys without trusting cached `Address.Key`. `AddressOfKey` verifies escaped syntax and canonical typed round-trip; `KindOfKey` returns the prefix. HostKind exposes convenience wrappers and `Address.ToJson()` provides the store's portable row representation.

Documents cancellation follows the frozen source: it awaits the line and callback, checks the signal before preparing the commit, discards a rejected uncommitted draft, and reports `transaction_cancelled` once via the owning host. Once queued, a successful commit is not canceled by its caller. Queries run only after successful table writes and adopt with the same transaction batch.

## Authored regression cases

The existing runner discovers the four new `*Cases` methods automatically (ten cases); no Program.cs edit is needed:

- `contracts.host-keyed.*`: canonical escaping and typed decoding, forged cached keys, missing/extra/wrong fields and unsafe integers, old and new fingerprint goldens, string/address and kind-change refusal.
- `documents.host-keyed.*`: schema fingerprint preservation, slot key-kind fencing, persisted ownership mismatch on read and retirement.
- `host.host-data.*`: gate drain/exclusion, canceled FIFO turn ordering, live persisted document plus query replacement/remap/deletion and second-address isolation, cold pinned document import, cancellation reporting and draft discard.
- `host.host-data.telemetry-*`: successful commit document/operation/row counts on the caller span, no event for no-op commits, duration histogram on the host adapter including an untraced caller.

Executed: `git diff --check` on owned code/tests passed. An isolated Node call imported the frozen upstream `contracts/fingerprint.ts` and evaluated the exact `items: keyed(str())` normal forms: old string-keyed hash `3abb26d9a7cda828`, host kind `tenant` hash `a34f82c281b7a441`; both are pinned in the new contracts case. No live service, credentials, build, or deployment was used.

Root validation should run new prefixes `contracts.host-keyed.`, `documents.host-keyed.`, `host.host-data.`, plus existing contracts, documents and host keyed/adapted test groups. Broader host-data archive, migration and query failure/diff cases are owned by C-STORE.

## Integration corrections

Full-r3 exposed a cancellation regression from an overly eager cancellation registration. Restored the precise upstream ordering (await callback, then check cancellation in `Transaction.Commit`; no caller cancellation after queuing), retaining the new diagnostic. The existing cancellation assertion is unchanged. Validation also exposed live query physical projection missing `$v`; C-STORE's live-query owner fixed the projection so same-row comparisons can read that field. Query failure reports now carry the owning table name in addition to object/error.

Coordinated the remaining document transaction telemetry: optional host telemetry on `IDocumentsOwner`, successful commit summary on `Transaction`, and an additive optional row-count callback on C-STORE's `CommitFence`. Timing begins immediately before commit preparation and ends after successful adoption. The caller span gets `chord.commit` with documents/ops/rows/duration, while the host's adapter receives `chord.commit.duration`; no-op transactions record neither.

The validation owner's focused repairs run `sync152-chord-repairs-focused-1ab3ab7996e7406bbb503aa793e980b2` compiled with zero errors. Cancellation and commit-telemetry regressions reached successful completion. Host-data refresh then exposed an authored assertion using `JsonNode.GetValue<int>()` against RowCodec's normalized Double cell; corrected that assertion to the existing number accessor, preserving the exact expected value. Final rerun remains with the validation owner.
