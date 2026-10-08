# Native tool activation and exposure

This bounded P6-03/P6-06 successor is authored, unbuilt and unexecuted above preserved `7452a7b2a355e1f603a31eb693def91a9f565df1`. It connects native descriptor metadata to actual model declaration and normal nested execution. It does not close either package or a phase gate.

The pinned semantic reference is Pi v0.99.1 `d86654abb8862e201933517d6f1fce9f88dd117f`. Local released archive inspection checked `packages/coding-agent/src/core/extensions/types.ts` lines 492-509 (exposure), 535-558 (loadout), 599-610 (default activation/loadout callback), 1705-1718 (active APIs), and `agent-session.ts` lines 1475-1544 (selection/callable construction), 1730-1734 (transcript restore), 3511-3519 (registration defaults). The archive is SHA256 `4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b`. These are source observations; no source module or differential test ran.

## Implemented behavior

`ExtensionToolDescriptor` and captured `ExtensionToolRegistrationInfo` now carry `ToolExposure` and `DefaultActive`. Invalid enum values fail staged registration, retaining its existing rollback/owner lifetime rules. Existing descriptors retain Direct/default-active behavior.

| Exposure | Automatic initial activation | Nested callable | Explicit model activation |
|---|---|---|---|
| Direct | DefaultActive=true | While active | Yes |
| ModelOnly | DefaultActive=true | Never | Yes |
| Codemode | Never | Whenever registered | Yes |
| Deferred | Never | Whenever registered | Yes |
| Hidden | Never | Never | Ignored |

`ExtensionAgentBinding.Registrations`, `Adapters` and `RegisteredToolDeclarations` retain every captured registration. `ActiveRegistrations`, `Tools` and `CreateDeclarationMessage` represent only model-active tools. An explicit binding option `ActiveToolNames` chooses an ordered deduplicated set; unknown/hidden names are ignored. Its captured invoker separately filters root and nested reachability, with ordinal name matching. Exposing an adapter as registered grants no final-action permission.

The actual native CLI composition copies approved tool metadata into `SessionRegisteredTool`. Its initial system message declares descriptor-default active tools only; configured approval/denial behavior remains intact. Approved but default-inactive tools stay registered. Codemode/deferred remain available to the nested broker even when undeclared. Hidden registration remains observable metadata and fails direct execution/declaration admission. No provider transport or terminal file changed.

`PersistentAgentSession.GetActiveTools()` returns the current ordered model-active set. Host `SetActiveToolsAsync(ImmutableArray<string>, CancellationToken)` uses the existing ConfigureAsync idle reservation, staging validation and acknowledged durable publication. Unknown and hidden names are ignored, duplicates preserve first order, and an identical ordered set adds no record. The transaction appends a source-shaped system delta removing the previous active set and adding the new ordered declarations. Real subsequent requests replay that delta; reopen restores it. Busy runs, pending input, replaced/disposed coordinators and precommit cancellation do not publish another loadout. The API cannot combine an active-name update with a caller-supplied system update.

The runtime resolves a fresh immutable model-active/callable selection while preserving the real session-owned generation and lifetime. Every reachable effect still passes existing initial preparation, hook replacement, final validation, mandatory final-action policy, actual executor and owned result/progress settlement. Activation never grants permission. Model-only and hidden names cannot enter nested execution even through the same shared invoker; inactive direct names cannot enter roots or nested calls. Codemode/deferred execute through that identical pipeline. A captured run/context retains its loadout until settlement; ended/stale capabilities cannot be reused after activation.

## Authored evidence

Seven `native tool activation ` groups in `ToolActivationTests.cs` are registered once and directly awaited by the Extensions.ContractTests runner. They cover all five exposures/defaults, registered-versus-declared catalogs, explicit activation and ordering, actual durable native Agent requests and nested calls, final transformed-argument policy and denied target, hidden transcript rejection, registration rollback, no-op checkpoints/reopen, busy and pending-input boundaries, cancellation before/during activation, held original nested cleanup and retired capabilities. Live inspection uses FileShare.ReadWrite. Cleanup releases holds and awaits original work; no outer diagnostic timeout detaches these groups.

Tests authored: 7. Tests executed: 0. Native builds: 0. SDK/provider/upstream executions: 0. Source checks include preserved predecessor identities, whitespace, registration and allowed-path inspection. They do not establish compilation or runtime correctness. After authorization, use exact fresh product admission and run these groups plus existing nested broker/host, runtime configuration, replacement, hooks and complete adjacent gates. Existing batch candidate `7452a7b2` remains unchanged in its separate checkout.

## Deliberate boundary and remaining features

This is a host-owned durable idle activation API. Native extension callback contexts do not yet expose the pinned global getActiveTools/setActiveTools API; enabling a tool during a running callback is not implemented. Native host mutation rejects a busy run rather than deferring it to the next turn. Initial default activation is produced by binding/CLI declaration composition; CreateAsync with a bare runtime registry still needs its explicit initial declaration transaction.

At the original activation checkpoint, still open: extension-context activation capability and safe next-turn scheduling; prepareLoadout callbacks, description patches and hiddenDeclarations request projection; full callable descriptors on ctx.tools; namespace/annotations/prompt guidance/output schema/execution-mode descriptor fields; registration-refresh activation rules and override changes; full settings/--tools/defaultTools mapping; loadout hook ordering/failure semantics; optional Node facade mapping; runtime differential evidence and independent acceptance. Current codemode/deferred have identical execution reachability; their distinct discovery/listing behavior requires the remaining loadout/search metadata work. P6-03/P6-06 and all original phase gates remain open.

Integration note: this feature branch preserves the `7452a7b2` base and does not include the separate actual-build correction `96e1532e78f2a9c1530db494422af17e3557a1b2` or terminal compiler correction. Integrate those reviewed successors before runtime qualification; source-only checks here are not a compile pass.

The isolated loadout successor implements the bounded preparation/request-projection subset described in [native-tool-loadout.md](native-tool-loadout.md); callback activation scheduling and the remaining descriptor/Node semantics stay open.

Successor update: [native callback activation](native-callback-tool-activation.md) now authors the production native SDK logical setter and next-request durable scheduler publication. Earlier gap statements above describe this checkpoint. New behavior remains unbuilt/unexecuted and does not close the remaining descriptor, Node or original phase obligations.
