Bounded original Tier-A admission candidate
=========================================

Base: `7452a7b2a355e1f603a31eb693def91a9f565df1`. Worktree branch: `codex/node-admission`.
Pi: v0.99.1, `d86654abb8862e201933517d6f1fce9f88dd117f`. Read-only local upstream `git rev-parse HEAD` returned that commit. The unchanged pirate source is 1461 bytes, SHA256 `dd6ce684bbe7630e4a749fe133ef8b8b875adbc7e31284b17fe0b8157d51e013`. Hello is 640 bytes, SHA256 `0aa4e9800c2526914d4c1edb00b2cfa9bd9dd6da5218289994cccd5f5bfa4934`. Both byte lengths and hashes were read locally. No source copies, additional packages, package policy changes, or reference-golden changes.

Implementation
--------------

`admission.mjs` selects 1–8 ordered sources from four immutable original source pins: commands, input-transform, pirate, hello. This is a general descriptor translation path over a bounded source catalog, not arbitrary file admission. Selection occurs before factory loading; whole original public loader factories are awaited. Commands, non-UI tools, input handlers and before-agent-start handlers retain their actual functions in Node. There are at most 64 registrations. Duplicate command/tool names, unknown sources, byte/hash mismatches and unsupported surfaces reject activation. The native transactional registry still owns activation, conflicts, schema admission, cancellation, dispatch and cleanup. The optional bridge stays absent from native-only code paths.

`NodeCommandInputExtension` accepts optional `ImmutableArray<string> sourcePaths`. For example:

```csharp
new NodeCommandInputExtension(launch, workspace,
    sourcePaths: [NodeTierAAdmission.PirateSource, NodeTierAAdmission.HelloSource]);
```

Leaving the selection at default preserves legacy command/input IDs and optional second input instance. Explicit selection uses deterministic per-factory IDs and cannot also request the legacy duplication option. Source identity, selection pins, factory counts, callback identity uniqueness and descriptor bounds are verified before registering any descriptors.

Pirate is the meaningful additional original corpus candidate: its command toggles the same original closure read by its hook, which changes only the outgoing system prompt. Native before-agent-start results accept only the `systemPrompt` patch; richer messages/options explicitly fail. Non-UI tool execution requires `IExtensionToolInvocationContext.ToolCallId`, passes all five original execute arguments and the actual cancellation signal, and returns the complete JSON result. Tool updates and nested execution remain explicitly unavailable in this bounded path. Native initial/final argument validation and tool policy remain authoritative. The separate Hello preparation profile retains its original behavior; this generic descriptor route does not claim that profile's coercion parity. Native UI uses the tool name rather than the original presentation label.

Flags and shortcuts have no methods on the existing native registry and reject with `UnsupportedRegistration` plus the named surface. Message/entry renderers, markdown transformers, tool renderCall/renderResult and unsupported tool metadata reject with the same diagnostic. `ctx.ui.custom` explicitly throws an unsupported-operation diagnostic. No custom UI object is serialized or dropped silently. Other lifecycle events, providers, virtual models and MCP server registrations reject. Existing immutable package resolution, dependency inventories, filesystem/network/process guards and reference goldens remain unchanged.

Cross-lane integration required before execution
-----------------------------------------------

This commit deliberately does not edit SDK, native Agent, terminal/RPC, supervisor, package admission, or compatibility manifests. The current supervisor will reject the changed helper bytes. **Do not bypass that rejection.** The supervisor/qualification owner must review an approved successor and use `pin-update.request.json` to:

1. Replace worker and loader helper rows and add `admission.mjs` to held helper pins and the loader module admission list in the approved command/input plan. Keep `wire.mjs` and all canonical/source-reference pins unchanged.
2. Hold the pirate/hello source rows as immutable source inputs, update the worker EntrySha256, and recompute the approved PlanSha256. Preserve the original reference expected hash; it does not qualify the new pirate scenario.
3. Record the broader admitted source subset and its explicit unsupported surfaces in that successor qualification. Any CLI/package selection exposure and durable approved declaration belongs to its owner and needs separate evidence; none is claimed here.

Tests and evidence
------------------

Authored, **not run**:

- `admission.test.mjs`: seven offline Node tests for unadmitted sources/count, byte tampering, original callback identity, named unsupported registrations/renderers/hooks, duplicate names/budgets, and pending provider rejection. Coordinator command: approved Node executable with `--test tools/NodeCommandInputBridge/admission.test.mjs`.
- `NativeAdmissionTests/NativeAdmissionTests.csproj`: native registry plus actual worker scenario. Covers atomic owned registrations, initial pirate state, enable/disable closure behavior, unchanged input snapshot, hello using actual host identity, joined source settlement/context retirement, disposal/input verification and stale-snapshot denial. Requires explicit `--node`, `--repo`, `--run-root`, `--oracle`, `--jiti`, `--reference`, `--command-input-reference`; run root must be fresh below an existing caller-owned parent, outside immutable input roots. It has no added package dependencies. Coordinator owns offline restore/build/run and required approved runtime paths.

Executed: local read-only source commit/hash/length checks and `git diff --check` only. No Node, dotnet, build, tests, upstream captures, provider/API calls or package execution. The native test prints success only after all assertions and source cleanup complete; no success report exists yet. Native registry tests are not CLI/package/Agent-pipeline evidence. Existing command/input regressions, coordinator compilation, cancellation/fault cases and independent original corpus comparison remain required before any support or executed-corpus credit. Executed corpus credit: zero.

Local instruction discovery found CONTRIBUTING.md and no AGENTS.md or .agents instructions in the worktree/source integration checkout. Its normal test-run guidance is superseded by the delegation's coordinator-only execution instruction.
