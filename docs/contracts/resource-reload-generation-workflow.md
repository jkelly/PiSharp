# Independent resource reload generation workflow

Implemented leaf against native main `754251be28928b24eaae5895b744c423450c91a3`
(tree `738429cf70aa4609938e398d3ef00b5836bf157c`). Actual CLI reload,
activation publication, discovery and session integration remain **OPEN**.
The coordinator owns those imports and all native execution. This patch changes
no activation, loader, session, MCP, skills, test runner or package lock files.

## Source contract and adaptation

Pinned Pi v0.99.1 [AgentSession.reload](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/agent-session.ts)
emits `session_shutdown` with reason `reload`, invalidates the old runner, reloads
settings/resources, rebuilds the runtime, and, when bindings exist, runs
`beforeSessionStart`, `session_start` with reason `reload`, unhandled MCP reporting
and extension resource additions. It preserves previous flag values and active
tool names while including all extension tools. The source does not revive the
invalidated runner on subsequent failure.
[Resource loader reload](https://github.com/badlogic/pi-mono/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/resource-loader.ts)
reloads settings and catalogs and retains the previously resolved project trust.
This leaf does not grant trust, discover sources, resolve packages or perform IO.

The native adaptation permits preparation of a distinct **unexposed, already
approved** candidate before the old shutdown/invalidation boundary. This provides
rollback for preparation or shutdown failure; it does not claim that this staging
order is the upstream runtime's order. Host preparation must not publish resources,
run a new generation's public callbacks or modify the old authoritative catalogs.
After invalidation, there is no rollback to old authority. A failed or ambiguous
publication is recorded explicitly rather than treating a disposed old binding as
usable. These are experimental native ownership rules, not full reload parity.

## API and ownership

Namespace: `PiSharp.Extensions.Runtime.Reloading`.

* `ResourceReloadPlan(current, replacement, maximumEntriesPerCatalog=4096)` captures
  immutable `ResourceReloadEntry(Kind, Key, Fingerprint)` sequences. Fields are
  nonblank, bounded to 2048 characters; each catalog has unique ordinal `(kind,key)`
  identities and a bounded entry count. Replacement-order additions/changes precede
  current-order removals. Identical content still permits reload; the plan does not
  suppress lifecycle events. Fingerprints are host-provided identity evidence,
  not verified hashes. Typed payloads remain in the host generation. Arbitrary
  resource kinds admit later skills/MCP catalogs without dependency on their types.
* `ResourceReloadWorkflow<TGeneration>(old, plan, operations, hostLifetime)` is one
  attempt. `ReloadAsync(admissionToken)` returns the same original task to every
  admitted concurrent/repeated caller. It is not a reload reservation, a registry,
  an admission grant or a disposal owner for the session. The host must retain its
  existing serialized session/reload reservation through this original join.
* `ResourceReloadOperations<TGeneration>` borrows `StageAsync`, `ShutdownAsync`,
  `InvalidateAndDrainAsync`, `PublishAsync`, `CleanupAsync`, `StartAndExtendAsync`.
  All are invoked outside the workflow lock. Stage owns its partial acquisitions
  until it returns a distinct candidate. The candidate must not own old resources.
  Shutdown uses the old captured generation and the existing shutdown/reporter
  guards. Invalidation rejects held contexts and callbacks and joins original
  admitted operations/reporters. Publication is the coordinator's atomic approved
  binding publication. Cleanup directly awaits original loader/registry/reporter
  disposal. No outer cancelled wait or detached replacement cleanup is allowed.
* `ResourceReloadPublication(Authority, Failure?)` is the publication owner's
  acknowledgement. `New` means the exact candidate is authoritative, including
  when an original post-commit failure is attached. `None` means neither generation
  is authoritative. `Unknown` means only the owner can recover the publication
  state. None/Unknown require the original failure. `Old`, malformed receipts and
  thrown publication callbacks yield Unknown. The workflow does not authorize
  publication itself or infer whether a throwing callback committed.
* `ResourceReloadReceipt<TGeneration>` retains the old/candidate identities,
  authority, completed boundaries, cleanup attempts and original exception objects
  tagged by phase. No exception messages are copied into wire diagnostics. Cleanup
  failure means its original operation settled with failure, not that unload or
  disposal succeeded. The receipt deliberately keeps the recovery handles.

## Failure and cancellation boundaries

Stage/shutdown failure leaves old authority and joins candidate rollback. Shutdown
may have invoked handlers even when its reporter failed; its failure is not retried.
An invalidation/drain throw yields Unknown, rolls back the unpublished candidate,
and leaves old recovery to the host: the workflow cannot assume admission was
restored or that failed invalidation is safe to dispose. Acknowledged invalidation
permits joined old retirement regardless of publication outcome. None publication
also joins candidate cleanup; Unknown retains the candidate for host recovery.
New publication never cleans the new generation as rollback. Old cleanup failure
or publication failure skips subsequent start/resource effects. Successful old
cleanup precedes `StartAndExtendAsync`; start/reporting failure retains new authority.
All primary and cleanup failures retain their original identity and order.

Caller cancellation is admission-only. After admission, the explicitly borrowed
**host reload lifetime**, which must survive old-generation retirement, controls
staging/shutdown/start. Invalidation, publication and cleanup have no cancellation
argument and always join the original operation. Cancellation thrown by a delegate,
including foreign cancellation, is retained as that phase's original cause. An
AsyncLocal guard refuses callback/reporter self-waits of this attempt. Existing host
reporter disposal and session reentry guards remain necessary, including cross-attempt
reentry; this leaf does not replace them.

## Coordinator integration and qualification

Keep the exclusive activation/loader/profile changes with their coordinator. The
host must supply approved catalogs and payloads, preserve flag values/active tools,
stage binding diagnostics against the actual candidate snapshot, reject stale old
contexts at invalidation, and wire the original `reload` command to its serialized
reservation. Host cleanup must drain the existing loadout diagnostic bridge and
loader/registry disposal tasks. `StartAndExtendAsync` must preserve the source's
conditional bindings behavior, before-session-start callback, reload reason,
unhandled MCP reporting, and extension-provided resource additions.

Register `.Concat(ResourceReloadWorkflowTests.Cases())` once in the existing
extension runner and add `ResourceReloadWorkflowTests.Prefix` to its original-join
branch (do not wrap these tests in the runner's detachable `WaitAsync`). Registration
is deliberately excluded from this leaf because `Program.cs` is reserved.

Ten framework-only synthetic adapter groups are authored, **not executed**:
immutable bounded catalog differences; successful ordered original retirement;
held invalidation despite host cancellation; stage rollback; throwing trusted shutdown reporter and held rollback; ambiguous
invalidation; None publication plus both cleanup failures; uncertain publication;
post-commit authority; cancellation and callback self-wait. These test the workflow
boundary, not actual stale registry scopes or real CLI reload parity.

Coordinator qualification must additionally exercise actual reload entrypoints,
changed/removed skills/prompts/extensions/MCP catalogs, previous flag/active-tool
carryover, callbacks and reporters held across invalidation, stale native contexts,
real failed loader cleanup, publication acknowledgement loss, and shutdown/close
joins. Build and affected full-suite evidence must bind to the imported frozen
source. No execution, full parity, original-source compilation, PiMessages API,
platform or release acceptance is claimed here.
