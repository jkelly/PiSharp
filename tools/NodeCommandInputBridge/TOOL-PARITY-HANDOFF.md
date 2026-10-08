Tool parity and admission successor — review required
====================================================

This branch is `codex/node-tool-parity`, based on preserved candidate `5c1ebd5b4bd9bd6eea3cd7f9c55846428e74bd88`. That candidate, its `ADMISSION-HANDOFF.md`, and its `pin-update.request.json` are unchanged. The older handoff describes the ancestor and is not this successor's pin authority. This successor requires independent reviewer clearance before any coordinator runtime allocation. No Node/dotnet/build/test execution has occurred.

Implemented using existing contracts
-----------------------------------

- Original `Runner.createToolContext(actualToolCallId, actualSignal)` creates the fifth argument. Its guarded `tools` getter and `executeTool` method are checked. Both bind to named `UnsupportedHostOperation` diagnostics (`ctx.tools`, `ctx.executeTool`); nested invocation remains unsupported. No empty/undefined stand-in silently grants or omits those members.
- A pure owner-admitted `command-input.prepare` request has no host callback handle or context. Whole original `packages/ai/src/utils/validation.ts` `validateToolArguments` receives the retained definition and its actual TypeBox schema object. Source/schema before/after and return/failure observations are retained. Native descriptors use existing `PrepareInitialArgumentsAsync`. Native final validation and authorization after tool-call hooks remain unchanged; no hook-time re-preparation is added.
- Source `onUpdate` remains synchronous and returns undefined. Its queue is bounded to 16 updates, 65536 UTF8 bytes per update, 262144 cumulative bytes, with one native publication pending at a time. Progress snapshots prevent later source mutation from changing publication. Overflow, invalid JSON or failure remains a recorded failed operation even if source catches an exception. Functions, symbols, nonfinite numbers, accessors, arbitrary prototypes and sparse arrays are explicitly rejected.
- Each native update checks the live owner callback handle, operation ID, callback ID, actual tool-call/parent identity and increasing sequence. The native sink rejects overlap, duplicate/out-of-order sequence, overflow, delivery after failure and stale delivery after retirement. Actual `ReportUpdateAsync` completion and cancellation are joined before the matching delivery receipt. No effect is retried after a lost/failed reply.
- Source tool execution, progress, UI publication, original settlement and existing physical cancellation-write fences complete before native context retirement. Late source progress is rejected after source settlement. Falsy source/publication throws are retained as failures.

Exact admission preparation
---------------------------

`compatibility/node/command-input-bridge.plan.json` now declares admission revision `bounded-tier-a-tool-parity-1`. `NodeCommandInputWorkerLaunch` verifies that revision and has matching EntrySha256/PlanSha256. This is an authored candidate, not reviewer approval or execution evidence.

`tool-parity-admission.request.json` is the exact machine-readable handoff. It holds:

- Plan SHA256 `abdd66ab03c89157bc8bd63aeae708daa09be0b15b8a1437d8007a301044b9ee`.
- Worker EntrySha256 `a8d7672257d521223a444b61814081822d0463a77b5785eeffccf662fd912ef4`.
- Worker, loader, unchanged wire, admission, context and progress helper rows. Helpers are all held by the existing supervisor pin machinery and admitted by the plan's module ledger.
- The same four selected original extension sources: commands, input-transform, pirate, hello. Pirate is 1461 bytes / `dd6ce684bbe7630e4a749fe133ef8b8b875adbc7e31284b17fe0b8157d51e013`; Hello is 640 bytes / `0aa4e9800c2526914d4c1edb00b2cfa9bd9dd6da5218289994cccd5f5bfa4934`.
- Additional explicit pin for the original validator, already inside the admitted upstream tree: 9902 bytes / `460786b57dead200e411b9afec916a5049c96ebcff4da082096456ac06d44fb7`.

Pi remains v0.99.1 / `d86654abb8862e201933517d6f1fce9f88dd117f`. The fifteen-package dependency admission, Jiti/runtime pins, canonical virtual map, protected-paths predecessor, reference manifests and expected hashes are unchanged. `ms` and `with-deps` stay unadmitted. No SDK, Agent, terminal/RPC, package policy or CLI selection contracts changed. The supervisor revision/pin files are the separately authorized admission successor.

Authored controls, not run
-------------------------

Ten new `tool-parity.test.mjs` cases cover serialized delivery, synchronous return, immutable snapshots, held-publication join, count/frame/cumulative limits, malformed JSON values, cancellation before/during publication, receipt mismatch/failure, falsy rejection, closed source callbacks, tool context shape and named unsupported nested operations. Context-unit controls use an authored Runner double; they do not independently certify upstream nested execution.

`NativeAdmissionTests/NativeToolProgressTests.cs` exercises the real internal native delivery class using a synthetic invocation sink: operation/callback/parent mismatch, held publication, backpressure, duplicate sequence, owner-close join, stale sink, cancellation, publication failure and inclusive byte/count limits. Test-only reflection accesses the internal class; no production API or friend-assembly access was added. `--sink-only` runs these controls without a worker once the coordinator assigns native execution.

The original Pirate/Hello native registry scenario now checks live-schema preparation, missing-name rejection, original context shape and actual host-call identity in retained source receipts, and joined progress settlement. Original Hello emits no updates; authored progress faults are distinct from actual original tool progress corpus evidence. The seven ancestor admission controls remain unchanged.

After reviewer clearance, the coordinator must compile/run using its approved offline configuration and fresh run roots, run the legacy command/input regressions, and inspect cancellation/publication receipts. Tests, schema/coercion comparisons, full-duplex progress fault scenarios and independent corpus matching remain pending. This work claims no CLI/package/Agent-pipeline qualification, no new original update corpus, no shipping gate and zero executed-corpus credit.

Static evidence completed: source pin lengths/hashes, helper/plan SHA consistency, preservation of ancestor handoffs, and `git diff --check`. No source factories, dependency downloads, product runtime processes or API calls were executed.
