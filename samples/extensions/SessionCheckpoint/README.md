# Session checkpoint native sample

This ordinary compiled C# sample registers `/checkpoint`, a `session_start` observer and a typed pre-switch handler. Its owner is `sample.checkpoint`, assembly `SessionCheckpoint.dll`, and entry type `SessionCheckpoint.SessionCheckpointExtension`. It uses the shared JSON/extension contracts without retaining a session store, provider, mutable branch history or external service.

The command durably saves a namespaced custom entry in A, optionally switches to an existing B, reconstructs state from B's captured selected ancestry and saves through the returned fresh context. It then attempts an append through the still-active A context and requires rejection. The JSON round-trip option switches back to A, saves through generation 3 and proves that the original generation 1 context remains stale. Opaque records and siblings remain in the physical file; selected branch reconstruction excludes unrelated siblings.

```text
/checkpoint
/checkpoint C:\absolute\B.jsonl
/checkpoint {"target":"C:\\absolute\\B.jsonl","leafId":"selected-entry","returnPath":"C:\\absolute\\A.jsonl","roundTrip":true,"label":"checkpoint"}
```

Interactive replacement requires an explicit true confirmation. No-UI, timeout, cancellation, disconnect and a false answer veto it. A headless caller can explicitly include `"confirmSwitch":false` in its admitted command JSON; this option permits that command's switch without treating unavailable UI as approval. Direct RPC switching retains the sample's normal confirmation requirement. `"confirmCheckpoint":true` adds an additional checkpoint confirmation after B commits, useful when the user wants to decide whether to save there. Cancelling then keeps B current and saves no replacement checkpoint.

The session_start observer also shows "Resume checkpoint session" whenever reason is resume and the host supports confirmation, including each committed switch to an existing session. This dialog is awaited after the new attachment publishes and before SwitchSessionAsync returns its fresh context. It is separate from the pre-switch veto and the later optional checkpoint confirmation. Its answer is ignored by this informational observer and cannot undo a committed replacement. Interactive callers must route its reply as well as the pre-switch reply; an A-to-B-to-A command sees it on startup, B and returned A.

The recorded state includes phase, label, selected leaf, session ID, generation and count of prior sample-owned custom entries in the selected branch. Schema version 1 is explicit. The sample owns interpretation; it does not migrate or rewrite old entries. Every write awaits the physical host acknowledgment. The optional fixture marker environment variable is used by automated process tests to witness initialization/session-start/disposal; it is not state storage.

When the host advertises `session-creation`, the sample also registers `/checkpoint-create` and a typed pre-effect creation confirmation. Enable that command separately in the exact approval and with `--enable-extension-command checkpoint-create`.

```text
/checkpoint-create {"kind":"new"}
/checkpoint-create {"kind":"before","entryId":"user-entry"}
/checkpoint-create {"kind":"at","entryId":"selected-entry"}
/checkpoint-create {"kind":"clone","confirm":false}
```

Successful creation saves an `after-create` checkpoint through the fresh returned context and verifies stale-source rejection. `before` returns the user message's selected text to the editor when supported. An optional `confirmCheckpoint:true` pauses after the new attachment commits; cancellation retains that file and writes no checkpoint. The explicit headless `confirm:false` applies to that admitted command only. The native backend eagerly creates durable sibling files; lazy persistence and in-memory parity remain open. See [the lifecycle contract](../../../docs/contracts/native-session-lifecycle.md).

Build and publish with the pinned .NET 10 SDK and locked dependencies:

```powershell
dotnet restore samples/extensions/SessionCheckpoint/SessionCheckpoint.csproj --configfile NuGet.Config --locked-mode --disable-parallel
dotnet publish samples/extensions/SessionCheckpoint/SessionCheckpoint.csproj --no-restore -c Release --output artifacts/extensions/published-fixtures/checkpoint
```

The existing native fixture publisher performs these steps. Execution uses the normal exact package/manifest hashes, declared command/observation capabilities and explicit workspace/session approval. The manifest requires `owned-descriptor-callbacks`, `session-branch-snapshot`, `session-durable-entries` and `session-replacement`. Enable the approved command with `--enable-extension-command checkpoint`; there is no default activation or Node dependency.

`NativeExtensionSessionCommandTests.CheckpointCases` contains five whole-process groups: all three offline provider profiles without acquiring a turn; actual RPC rollback/veto/abort/EOF; abort and EOF after B publication; one-shot report/print/JSON for both A→B and A→B→A; and a cooked CLI with actual startup and replacement replies. Each group joins its physical children/readers and verifies acknowledged bytes, reopened writers and package cleanup. See [the host contract](../../../docs/contracts/native-session-replacement.md) for bounds and remaining scope. This sample is a bounded P6-10 authoring asset, not the complete SDK or full Pi checkpoint extension parity.
