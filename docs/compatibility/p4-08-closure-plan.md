# P4-08 original package closure plan

Target: the complete unchanged P4-08 scope in `docs/plans/04-sessions.md`, lines 104-113 in the original plan. Base `8cba8a60c9e8cb4452b15dae2913c365f633e719` received independent bounded catalog/resume ACCEPT, and its 1,855-file seal has been preserved and verified. Runtime ownership was released to this development lane. Neither a new test count nor this plan closes the package or any phase gate.

Original work clauses and required evidence:

1. One shared lifecycle API for create/open/resume/list/fork/clone/import/export, used by embedders and CLI. Existing owner/lifecycle paths cover durable operations; move independent one-shot construction onto the same service, and integrate read-only/import/export through it.
2. Read-only session views and an in-memory implementation. Supply immutable selected-branch views without provider/tool/plugin or writer acquisition, safe future/damaged inspection, and a genuine bounded memory backend with the same append/identity/queue/selected-context rules. Memory acknowledgment must explicitly report volatility, never file durability.
3. Recreate cwd-dependent services and rebind subscriptions on replacement. The optional owned runtime factory creates a fresh registry/services lease from the validated actual header; joins release after agent/writer closure and rejected staging. Qualify distinct model/provider/tool/policy instances, repeated replacement/disposal, veto/cancellation/open failure and subscription transfer. Persisted cwd remains data and cannot widen permissions.
4. Paginated/cancelable listing that avoids eager image-body parsing. Retain catalog controls and qualify both explicit memory and local stores, cancellation, malformed names, separate-store identity and Windows/Unix cwd grouping.
5. Known header metadata and parent-session links. Preserve complete read-only/import/export headers and known new/fork relationships; absent parent paths remain inert metadata.
6. Namespaced branch JSON state with extension ID/kind/schema version, preserving compatible wire shape. Retain helper/version errors; qualify sibling branches, source-disabled state through export/import/reload, state exclusion from model input, and hidden custom-message context contribution.
7. Fixture distinctions: fork versus in-file branch; clone versus selected-branch export; empty/in-memory session; repeated replace/dispose/rebind; fresh/stale contexts and cleanup. Every original listed fixture needs a linked package result, not inference from unrelated counts.

The pinned Pi `SessionManager._hasConversation/_persist` at upstream `d86654abb8862e201933517d6f1fce9f88dd117f` requires lazy local persistence: setup-only sessions remain in memory, and the first user **or assistant** message materializes the complete accumulated log. This includes interrupted first turns; waiting for the first assistant would lose the accepted prompt. Fork/clone follow the same conversation rule. Include this backend obligation in this package milestone; retain the eager durable option explicitly.

Closure requires the complete package matrix, unchanged upstream comparisons for applicable backend/lifecycle semantics, full exact native/Commands/Input/terminal/SDK/reservation gates, and independent Astra review. Preserve all original scopes/dependencies and all eight OPEN phase gates. P4-05 import/export interoperability, P4-07 automatic recovery/compaction, directory-entry crash durability and the broader original port remain mandatory; implementing their API connections here does not close those packages.

## Complete original package matrix

Every row below is an original work, fixture or exit clause, not a replacement subset. The targeted results
are development evidence. Exact immutable full gates and independent original-criteria acceptance remain
required; the original package remains in progress at this candidate's creation.

| Original clause | Implementation and concrete evidence |
| --- | --- |
| Create/open/resume/list/fork/clone/import/export shared by embedders and CLI | `PersistentSessionLifecycle.CreateAsync/OpenAsync/Attach/ListAsync/ReadOnly`; initial CLI create/prompt/resume, RPC creation/switch/resume, copy/migrate/catalog and inspect/tree use these services. Existing creation/resume SDK/frontend groups remain; new backend New/fork/clone groups and four actual RPC/cooked groups pass. |
| Read-only views | `SessionLifecycleReadOnly` owns inspection bytes/tree/context and captures borrowed active views without authority. Seven groups qualify future/damaged/unsupported projection distinctions, immutable snapshots, exact/compatible and selected export, cancellation cleanup and known late publication receipts. |
| In-memory implementation | Genuine `SessionStorageBackend.InMemory` performs no filesystem operation, retains acknowledged data only in its explicitly owned namespace, and reports volatility in store/branch/copy/SDK receipts. Storage and backend groups include real queueing, selected state, New/fork/clone, independent backend reopen and joined cleanup. Source `memory-zero-files` comparison covers four complete raw checkpoints. |
| Recreate cwd-dependent services and rebind subscriptions | Actual validated header cwd is offered to the owned runtime factory on every create/open/replacement. `WorkingDirectoryRebinding` creates distinct registries, providers, tool adapters and final-action policies across four actual replacements and two independent reopens; verifies joined old-service release, subscription transfer and stale authority rejection, in both backend modes. `OwnedRuntimeFailures` joins fresh service release after veto, staged cancellation, creation rollback, factory-return cancellation, failed model resolution and failed cleanup, retaining source usability. Host factories control authorization; persisted cwd grants none. |
| Pagination/cancellation without parsing every image body | Eight unchanged `SessionCatalogTests` cover bounded header-only reads, pagination, cursor/query identity, reader-close cancellation and resource/cleanup faults. New read-only memory catalog and actual RPC memory/lazy listings use the same configured catalog seam. |
| Known header metadata and parent links | Read-only/import/export retain full raw headers, unknown metadata and inert missing parent paths. New/Fork/Clone retain their explicit relationships; memory forks omit parent-file metadata. Source fork/header/parent comparisons use all retained raw fields and identities. |
| Namespaced state, record kind/schema version, compatible wire shape | Existing actual native SDK/state groups retain owner/kind/schema behavior and explicit version errors. Actual memory/deferred/durable SDK captures and append scopes report persistence truthfully without raising snapshot budgets. Disabled-extension selected export/import/reload restores schema-2 opaque state, rejects schema-1 reads and preserves complete wire data in all three storage profiles. |
| Same session ID in separate stores | Existing `SessionCatalogTests.IdentityAndHeaderOnly`, `SessionCatalogResumeTests.SelectedBranch` and actual SDK resume fixtures distinguish configured store identity and attachment generations. |
| Malformed names | Existing `SessionCatalogTests.InvalidHeadersAndBounds` rejects unsafe names and malformed bounded headers. |
| Windows/Unix cwd grouping | Existing `SessionCatalogTests.CwdGroups` groups both path syntaxes lexically without filesystem resolution. |
| Fork versus in-file branch | Backend sibling/root/return and fork groups retain source physical siblings while forks retain selected ancestry under fresh identity. Genuine source `in-file-branch-keeps-siblings` and `fork-selected-fresh-parent` retain complete physical/selected records. Native planner fork comparison checks complete header and entries, without path or ID replacement. |
| Clone versus selected-branch export | Selected export preserves original session/record identity and exact retained byte segments, with all exclusions reported. Clone uses fresh identity/parent and reconstructed labels. `SelectedExport` plus actual backend clone/fork schedules and every source-checkpoint selected export distinguish the two operations. |
| Missing parent path | Existing catalog/header tests and all read-only copy/selected-export fixtures retain missing parent metadata without opening it. |
| Canceled listing | Existing catalog and actual coordinator held-reader tests join physical cleanup; memory read-only listing also rejects the canceled token. |
| Disabled extension on reload and two sibling states | `DisabledStateAndHiddenContext` exports selected left ancestry, imports and opens it with no registered extension/provider call, then exercises the actual native snapshot/state helper. It preserves huge opaque numbers and schema errors, excludes right state/context and verifies the source right branch remains available. Existing actual SDK and backend selected branch tests cover both siblings and explicit root restoration. |
| State excluded from model; hidden messages follow specified context | The disabled-extension roundtrip asserts hidden `display:false` custom messages remain runtime context and model user messages, while state and sibling payloads stay excluded. All 26 genuine source checkpoints compare complete runtime and LLM JSON through the native projector and selected export, including source open/reload. |
| Repeated replace/dispose/rebind | New backend tests cover actual queues, stale generations, repeated header bindings/subscriptions, veto/cancellation, imported target preservation, fresh writes and independent writer reopen; unchanged reservation and SDK probes remain mandatory. |
| Exit: one consistent lifecycle API | All initial, attached, provider-free and copy/read-only paths above use the same services. The full candidate and independent review must establish this conjunction across the original matrix. |
| Exit: explicit branch-aware state/version errors for Phase 6 | Independently established on accepted 8cba; retained current state helpers, eventual snapshot preflight and actual native scope receipts extend it to memory/deferred storage. |

## Genuine source comparison boundary

`capture-session-lifecycle.mjs` executes the unchanged whole pinned `SessionManager` and `convertToLlm`, with
the previously qualified exact eight dependencies. It retains complete raw generated IDs, clocks, cwd,
filenames, parent links, entries, projections, physical UTF-8/base64 and own-undefined observations. A separately
declared semantic/relational contract is compared between two fresh source runs. The new fixture's complete
raw capture is 1,112,034 bytes, SHA256 `88cc15ccaeb7ddf8946d6d2a954cd4851c4c92ecc18bdf76b50ed4c180a5b13d`.
No old golden, source module, clock or RNG was changed. The initial read-only preflight timed out before any
source schedule; its original log remains. A fresh retry succeeded under the identical 20-second setup/child
deadlines and 8 MiB child output limit, with all source/dependency/loader/runtime pins rechecked after both
natural child exits.

Eight native differential groups replay every one of the 26 complete raw checkpoints through actual
memory/lazy storage and the shared read-only/selected export services. They compare **all** header, physical
entry, selected ancestry, runtime message and model message JSON, actual materialization and complete retained
fork header/entries. This is raw checkpoint/projection/storage/fork comparison, supplemented by actual native
coordinator/frontend schedules. It is not a claim of the complete upstream agent/provider/compaction or
platform corpus. Generated native namespace paths and native JSON escaping need not equal source paths or
physical byte framing; the original source bytes remain preserved and semantic records are fully compared.

The first immutable candidate `e743cd45b58d0ffdf03629175b6ecc8735e7a45e` passed its full native gate but
received independent static HOLD for service recreation, whitespace/CRLF lazy exact-copy handling and
materialized-file admission. Its source, failed qualification and static review remain preserved. The
corrected candidate adds the owned lease API, actual exact-copy framing schedules and namespace admission
tests. New immutable full gates and independent review remain required; the HOLD is not silently retired.
