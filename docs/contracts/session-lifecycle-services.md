# Shared session lifecycle services

`PersistentSessionLifecycle` supplies the same explicitly configured registry, actual-header cwd factory,
storage namespace, catalog and record bounds for initial create/open and attached New/ForkBefore/ForkAt/Clone,
switch and catalog resume. `Attach` returns the owned replaceable coordinator; `ReadOnly` supplies inspection,
import, whole export and selected-branch export without acquiring provider or extension execution authority.
The CLI create/prompt/resume/inspect/tree/copy/migrate/catalog paths use these services. RPC, cooked chat and
terminal chat share the RPC lifecycle owner.

The optional `SessionStorageBackend` supports `InMemory` and `LazyLocal`. Its absolute directory is an
explicit namespace. Memory operations never create that directory or a physical session file. Lazy storage
requires an existing explicit local directory and retains header, configuration, extension state and custom
messages in memory until a user **or assistant** message is checkpointed. That checkpoint writes the complete
accumulated JSONL through the exclusive local CreateNew writer and flushes file contents to disk. An external
destination collision preserves the existing file and reports a poisoned writer; uncertain partial files are
retained for inspection. An empty replacement or setup-only branch remains deferred. The eager local profile
continues through the default local storage factory.

Backend options bound files (2,048 by default), logical retained and active writer bytes (64 MiB), and each log
(16 MiB). Readers and returned immutable snapshots own separate bounded copies. These are logical admission
limits rather than a process-memory measurement.
New-file admission counts every direct file in the lazy namespace, including pre-existing, materialized,
pending and publication temporary files. Enumeration stops at the bound. Materialization and publication
retain their existing slot. External processes can add files outside the backend's admission lock; the
bound governs this backend's admission and cannot arbitrate an entire directory across processes.
The backend owns no background tasks and keeps committed memory checkpoints until its host releases the
backend. Closing a coordinator joins its writer and does not
materialize setup-only sessions. Reopen within the same backend restores their acknowledged data. A new
backend instance cannot recover volatile data. `PersistentAgentSession.SessionFile` is null for memory sessions;
`Path` remains their explicit namespace identity. Memory forks omit a parent-file link. Lazy sessions retain
their configured future file path.

`SessionLogAppendResult.CheckpointAcknowledged` distinguishes accepted backend checkpoints from
`DurableCheckpointAcknowledged`. Snapshots and branch/copy/SDK receipts expose actual storage durability.
Native extension captures and append acknowledgments report `VolatileMemory`, `DeferredLocalFile`, or
`DurableLocalFile`. The existing snapshot budgets and eventual-target preflight remain unchanged. RPC's
explicit memory/deferred mode adds `pisharpPersistence` and a false `pisharpDurableCheckpointAcknowledged`
to state; its `sessionFile` field is the host namespace path. The default durable wire response is unchanged.

The offline RPC/chat/terminal invocation accepts `--session-mode open|new-memory|new-lazy`.
The default opens an existing log. New modes require a new absolute session namespace path and prohibit
an initial leaf selection. Their configured catalog stores must share the backend's explicit directory.
This option does not authorize tool paths or turn persisted cwd metadata into permission.

Read-only inspection retains the original bytes, copy/migration diagnostics, complete tree and selected
context where available. Future or damaged input stays inspection-only. An unsupported selected message
projection reports its typed failure while safe raw export remains available. A captured live view borrows
the owner, validates attachment before and after capture, and grants no mutation capability. Subsequent
appends do not change the captured view.

Whole `NativeExact` import/export preserves original current-v3 bytes. Selected-file `NativeExact` retains
the original header and exact byte segments of the selected ancestry, reports every excluded entry ID, and
generates no new session or entry identity. Selected snapshot export preserves retained raw record bodies
framed with LF; its receipt makes no original-file byte-exact claim. Compatible JSONL conversion retains
explicit migration and semantic-qualification diagnostics. Unknown metadata, absent-extension state and
opaque JSON remain data. Export never dereferences paths or loads CLR types from that data. Clone creates
a fresh identity and parent relationship and may recreate label records; selected export preserves identity
and reports omissions instead.

Replacement rejects pending steering/follow-up queues before allocating identities or files. Actual-header
cwd rebinding uses a trusted host factory after full header validation.
An optional `runtimeForWorkingDirectory` factory returns a fresh `SessionRuntimeLease` containing the
new registry and host-owned async resources. The coordinator joins their release after closing its agent
and writer, including rejected staging, canceled acquisition and failed runtime resolution. A lease may
be claimed only once. The factory owns cleanup for allocation that fails before returning a lease; after
return, the coordinator owns cleanup. Admission and cleanup failures are both reported. The older registry
factory explicitly borrows bindings and supplies no automatic service-disposal promise.
Host-owned subscription rebinding runs through attachment notifications, with old attachment tokens
canceled and all stale writes rejected.
Precommit veto/cancellation joins target cleanup and preserves imported existing files. File publication,
attachment commitment, content durability and cleanup remain separate receipts.

The complete original P4-08 matrix and independent review are required for package closure. This service
does not close P4-05 interoperability, P4-07 recovery/compaction, directory-entry crash durability, complete
production provider/resource setup, platform qualification, or any of the eight original phase gates.
