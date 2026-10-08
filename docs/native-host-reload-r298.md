# Native host reload ownership boundary (source-only)

Base: `ee44d9184e71327a8c54c171360121d2d3e01d30`, tree
`5cc133b92160ec2e5d982173137db7d318a72448`. This change does not supersede the
separately frozen `36c2df3` qualification or claim that new code was compiled or run.

`NativeHostReloadCoordinator.ReloadAsync` calls the actual
`ReplaceableAgentSession.RunReloadAsync`, which holds the existing mutation semaphore
and `PersistentAgentSession.ReserveReplacement`. It invokes the existing
`HostReloadPlanner` with captured active names, immutable host flags and the actual
session's lifetime tool policy. Candidate settings/resources remain host dependencies.
`BuildRuntimeAsync` must return a fresh `SessionRuntimeLease` owning all acquired
runtime resources plus a single synchronous prepared-registry commit. The owner claims
that lease during staging, before invalidation; staging factories own partial
acquisitions they cannot return. `CleanupPreparationAsync` handles only state outside
the runtime lease.

Invalidation rejects the exact previous attachment, cancels its lifetime and nested
invocation authority, joins admitted catalog reads, stops and retires the existing
owned resources, and drains the old registry diagnostics. The source session and
writer remain reserved. In particular, an MCP retirement may publish a withdrawal
catalog, so the final predecessor is captured **after** those operations complete.

Publication uses the existing durable `PublishToolCatalogAsync` transaction. A new
registry combines the staged bindings with the original final-action policy, lifetime
selection and invocation limits. Its invocation scope has a fresh attachment generation
and cancellation source. The synchronous commit installs the prepared registry,
attachment, invocation lifetime and runtime lease before the session gate opens.
The actual existing runtime-binding helper binds the candidate while still reserved.
After successful binding the persistent reservation is released; the owner mutation
reservation remains held through attachment notification, old-runtime cleanup and
start/extension lifecycle callbacks. This allows lifecycle callbacks to read the
published session without racing a host replacement.

No old authority is restored after invalidation. Failed or uncertain publication
permanently closes admission, retires the writer and joins candidate cleanup. A
post-publication start or notification failure retains the published generation.
Repeated calls on one coordinator return the same original task; simultaneous
coordinators for the same attachment join it. A fresh coordinator can retry a completed
pre-invalidation failure, while previous callers retain their original task. Both
attachment and attempt retention are bounded. Host close joins the original attempt
through the existing retirement list, not a canceled waiting wrapper.

The owner/session lock order was checked across every `ReplaceableAgentSession`
partial. Free-running binding capture now snapshots owner state, releases the owner
gate, reads the session, and revalidates attachment identity. Tree publication still
enters the session under the owner gate, but it holds the same exclusive mutation
semaphore and cannot overlap reload publication. Other owner-gate sites only capture
state or queue work; they do not synchronously enter the session gate. Runtime lease
claim under the owner gate invokes no callbacks; runtime owner binding invokes its
callback only after releasing the lease gate.

The original task's full fault aggregate is retained for new asynchronous boundaries.
Existing owned stop/body/publication and runtime-disposal awaits preserve every sibling
fault; a faulted `OperationCanceledException` stays faulted. Existing single ordinary
fault identity is preserved. Native operation and prepared-commit multicasts are rejected
before invoking them. Phase, lifecycle and synchronous cancellation reentry is rejected.

`NativeHostReloadTests.Cases()` provides ten source-only controls using real disk-backed
`PersistentSessionLifecycle` sessions and synthetic runtime/provider dependencies:
durable reopen and generation/policy/flag retention; withdrawal-before-publication;
stage rejection and retry; prepared multicast rejection; held stop/body/runtime faults
through concurrent close; post-invalidation commit failure; held staging during close;
callback reentry; faulted OCE versus canceled task; and retained authority after start
failure. No provider, native extension, process, network or package execution is needed
by those controls. No tests were run in this source-authoring task.

Remaining integration is explicit: the root-owned CLI startup/command composition must
construct this coordinator from its actual admitted settings/resource/extension factory
and route the user reload command to its returned task. No production startup caller is
claimed by this patch. It must supply the current flag snapshot, approved
`ResourceReloadPlan`, candidate-only callbacks, a fresh lease with the real native/MCP
owner-binding callback, and its actual prepared-registry commit. On success retain
`receipt.Current` and `receipt.Workflow.Candidate.Payload` for the next coordinator;
on an Old receipt retain the previous state. Do not create a second activation owner.

Root-owned test registration is also pending: add
`.Concat(NativeHostReloadTests.Cases())` to `tests/PiSharp.CodingAgent.Tests/Program.cs`
and admit its `native-host-reload.` prefix to the intended bounded selection. The
registered inventory and qualification grants must be updated separately.
