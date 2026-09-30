# Phase 6 Native extension SDK

## Goal and scope

Deliver PiSharp's first-class, versioned native extension SDK and trusted plugin runtime. A published `net10.0` plugin must register useful capabilities, participate in the specified lifecycle, survive supported session operations and produce actionable errors. PiSharp remains a JIT .NET 10 application with no Node dependency. This phase implements native extension support; it does not promise TypeScript source compatibility, unrestricted safe execution of third-party code, or guaranteed unload of arbitrary in-process code.

Compatibility reference: Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. Proposed filenames below describe work to create, not existing implementation. P6-G is the native extension exit gate. Its subgates allow SDK work to overlap earlier phases without accepting incomplete integration.

## Entry conditions and dependencies

- Phase 1 provides the immutable source snapshot, extension API inventory, fixture conventions, licensing review and compatibility/deviation manifest. Resolve the required native extension surface before freezing the SDK; a representative demo is insufficient.
- Phase 2 provides model/provider DTOs, normalized streaming, cancellation, catalog and authentication abstractions. Provider plugin integration depends on these contracts, not all production provider adapters being complete.
- Phase 3 provides awaited lifecycle barriers, tool preflight/execution, final-argument authorization, nested tool invocation, execution IDs and ordered transcript commits. These are hard prerequisites for accepting tool/event integration.
- Phase 4 provides session generations, read-only branch views, durable custom records, replacement transactions and state reconstruction. Loading and reducer unit tests may start before this integration is ready.
- Phase 5 provides UI capability contracts, RPC dialog transport and TUI component hosting. Headless native tools can pass first; custom component acceptance waits for the terminal host.
- Phase 8 owns release-wide parity closure and packaging. Phase 6 must publish its deferred rows explicitly; a row needed for native v1 cannot disappear merely because a sample does not exercise it.

## Ordered work packages

### P6-01 — Freeze the native contract and parity inventory

Create `src/PiSharp.Extensions.Abstractions/`, `docs/extensions/native-api.md`, `compatibility/extensions/native-surface.json` and an API approval baseline under `tests/PiSharp.Extensions.ContractTests/`.

Specify the stable entry interface `IPiSharpExtension : IAsyncDisposable` with `InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)`. Keep the existing plan's `IExtensionRegistry` name consistently. Separate event/tool context from command context. The ordinary context exposes read-only snapshots and bounded host actions; `IExtensionToolContext` adds nested execution; `IExtensionCommandContext` alone adds idle-wait, reload and session/tree changes. Separate operation, session and extension-lifetime cancellation tokens and explain their ownership.

Use host-owned DTOs for content, schemas, errors, identifiers and JSON state. Exclude provider SDK objects, concrete session stores, service containers and terminal-library classes from the stable ABI. Define a discoverable feature/capability version mechanism so future optional interfaces do not repeatedly break compiled v1 plugins. Record intentional native syntax differences from TypeScript independently from behavioral differences.

Evidence: a reviewed public API snapshot, compile-only consumer fixture, forbidden-dependency test and a compatibility row for every required registration, context operation and event family. Exit this package with no ambiguous owner for a public DTO or lifecycle operation.

### P6-02 — Specify typed events and individual reducers

Create `compatibility/extensions/event-catalog.json`, `src/PiSharp.Extensions.Abstractions/Events/`, `src/PiSharp.Extensions.Runtime/Dispatch/` and `tests/PiSharp.Extensions.Tests/EventReducerTests.cs`.

Each catalog row declares: event name and baseline source location; event/result type; allowed modes and context operations; ordering; snapshot behavior; reducer; validation; error/timeout/cancellation policy; persistence; reentrancy restrictions; and fixture IDs. Generate reference documentation from this catalog. Do not implement all hooks as a broadcast channel or one generic last-result-wins reducer.

Required semantics to encode and verify:

| Family | Native contract and compatibility behavior |
|---|---|
| Observation | No state-changing result; await required barriers and stream observers; failures are reported according to the pinned event contract |
| Input | Transformations compose; handled input ends propagation |
| Tool call | Proposed argument replacements compose; first block stops dispatch; hook failure blocks execution; native replacements translate Pi's in-place input mutation |
| Tool result | Field patches compose; omitted and explicitly supplied fields differ; replacing model content without replacement structured content clears the latter |
| Final message | Replacement preserves role; invalid role change is diagnosed according to the pinned behavior |
| Session pre-action | Cancellation short-circuits; cancellation and handler failure are separate cases, not interchangeable |
| User shell | First valid operations/result handler claims the command; failure must never fall through to local shell execution |
| Context | Conversation-only transforms run before full-transcript transforms; preserve the required system/tool state rules |
| Before-agent/prompt | Compose structured prompt options and contributed messages; whole-prompt replacement remains explicit |
| Provider request/headers | Request payload replacements compose; header changes are mutation-derived patches with ignored return values in the compatibility adapter |
| Boundary continuation | Turn-end and before-settle reduce proposed entries and continuation under validation; settled observation cannot start additional automatic work |
| Cache warming | Last explicit action wins |
| Trust/resource discovery | Trust is evaluated before project code activation; aggregate resource contributions using the pinned contract and provenance |

Enumerate every baseline event in the inventory, including provider-stream, UI-prompt, model/thinking selection, MCP changes and session metadata events. The table is an implementation grouping, not a replacement for the full list. Preserve baseline failure behavior for compatibility hooks; add mandatory core authorization as separately documented hardening rather than silently changing all hooks to fail-closed.

Evidence: at least one successful, no-result, invalid-result, failure and cancellation fixture for each applicable reducer; two-handler composition fixtures; add/remove-during-dispatch fixtures proving current-dispatch snapshots; invalid continuation/context cases; exact source-linked expectations. No unclassified mandatory catalog row at P6-G.

### P6-03 — Implement registration ownership and transactional activation

Create `RegistrationScope`, `ExtensionRegistry`, `StagedRegistrationSet`, conflict diagnostics and registration-lifetime tests in `src/PiSharp.Extensions.Runtime/Registration/`.

All handlers, tools, commands, flags, shortcuts, providers and renderers carry an extension owner ID, registration ID and lifetime. Initialization stages host changes; successful completion commits them atomically. Failure removes staged registrations and produces a diagnostic identifying the owner and operation. This transaction cannot undo arbitrary plugin side effects; the initialization contract prohibits starting ongoing resources before session activation.

Define collision policy explicitly. Reserve core commands and security controls. Built-in tool replacement requires an explicit target/override declaration and effective configuration; it must not replace authorization enforcement. Dynamic changes publish a new registry snapshot at a safe boundary. Disposed handles stop future dispatch, without altering an already captured dispatch snapshot.

Evidence: failure at each registration point leaves no active residue; duplicate-owner/ID/name cases produce deterministic diagnostics; a successful extension remains usable when another fails; registration removal during dispatch has the specified effect.

### P6-04 — Build manifest, discovery and trust gates

Create `schemas/pisharp-extension.schema.json`, `ExtensionManifest`, `ExtensionDiscovery`, `ExtensionTrustPolicy` and package validation fixtures.

Manifest fields: schema version, stable ID, package version, host API range, runtime kind, assembly and entry type, TFM/RIDs, required features, declared capabilities, resource paths and explicit overrides. Distinguish integrity from trust: a valid hash proves identity, not safety. Resolve compatibility, canonical paths and trust before assembly loading, module initializers, source builds or installation scripts can execute.

Use dedicated user/project PiSharp extension directories plus explicit settings/CLI paths. Specify deterministic ordering and canonical-path/identity deduplication. Existing `.pi` paths require an explicit import/compatibility choice. Do not install dependencies during ordinary discovery. Validate archive traversal, entrypoint escape, symlinks, missing files, incompatible versions and unsupported RID/TFM combinations. Provide list/inspect/enable/disable diagnostics with source and effective scope.

Evidence: denied project trust creates zero plugin process/module/constructor execution markers; malformed manifests never enter the loader; equivalent paths do not double-load; manifest/permission scope changes invalidate relevant prior enablement according to the documented trust policy.

### P6-05 — Implement published-assembly loading and dependency resolution

Create `PluginLoadContext`, `PluginAssemblyLoader`, versioned extraction/shadow-copy support and loader test packages under `tests/fixtures/extensions/native/`.

Use one collectible `AssemblyLoadContext` and `AssemblyDependencyResolver` per trusted extension. Share the exact `PiSharp.Extensions.Abstractions` assembly from the default context; fail clearly on unsupported ABI requirements rather than loading a private second identity. SDK-generated projects target `net10.0`, publish `.deps.json` and private dependencies, enable dynamic loading and exclude the host contract's runtime copy. Handle managed, native and RID-specific dependencies deliberately.

Validate two plugins depending on conflicting versions of a private library. Validate missing transitive/native dependencies and unwanted extra framework requirements. Do not restore or build arbitrary third-party projects as a side effect of loading. The optional local-development build command is separately trusted and explicit.

Evidence: real published fixture packages load on the supported OS matrix; conflict fixtures remain isolated; contract identity is identical to the host; malformed/missing dependency diagnostics include remediation; native-only installations do not probe for Node.

### P6-06 — Connect tools and safe host operations

Create native tool descriptors/binding helpers, `ExtensionToolAdapter`, `ExtensionActionBroker` and typed `ToolResult`/progress contracts.

Represent input/output JSON Schema, exposure, namespace, annotations, execution mode, prompt guidance, content blocks, structured data, details, usage and termination explicitly. Typed C# input records are optional authoring sugar over a canonical schema; retain a raw-schema escape hatch. Define missing/null/clear semantics for result patches. Validate tool arguments after extension transformations before evaluating authorization against the final target.

Nested execution uses Phase 3's normal pipeline and parent call IDs. Bound depth and report recursion problems without rejecting legitimate repeated calls solely because their tool names match. Distinguish cancellation, error, successful partial output and unknown external outcomes. Dynamic activation updates model declarations and transcript/tool state according to the session contract.

Evidence: native custom tool participates in a complete model turn; nested denied calls cannot bypass authorization; changed target/arguments trigger the correct final approval evaluation; result redaction cannot leave stale structured content; file-mutating parallel tools use the complete mutation queue; usage and terminal status are correct.

### P6-07 — Connect session lifecycle, commands and durable state

Create generation-checked context implementations, command execution adapters and namespaced extension-state helpers.

Wire session start/shutdown, switch/fork/tree/compaction and before/after-settle operations into Phases 3–4. Enforce the distinction between lifecycle context and command context. A lifecycle callback cannot await a run that is waiting for that callback. Release coordinator locks before calling plugin code. Fresh contexts follow successful session replacement; stale contexts reject state-changing operations.

Custom entries contain extension ID, entry kind, data schema version and JSON. Branch-sensitive reconstruction reads the active branch only. Unknown records survive load/export when a plugin is absent. Define migration ownership and failure behavior; do not silently rewrite old session files. External plugin storage is explicitly separate from branch history.

Evidence: fork/switch/resume fixtures reconstruct the correct state; stale callback cannot append to a replacement session; nested callback and dialog scenarios finish without deadlock; cancelled session operations commit no partial state; absent plugin entries round-trip unchanged.

### P6-08 — Add UI and provider extension adapters

Create `IExtensionUi`, capability descriptors, optional `PiSharp.Tui.Abstractions`, renderer adapters and provider registration adapters.

Bind select/confirm/input/editor and notifications/status/text widgets to Phase 5 capabilities. Make terminal components explicitly TUI-only; RPC and headless behavior must be defined for every method. Unavailable confirmation never becomes approval. Keep terminal renderer internals out of the core SDK, and give every renderer/component an owner and disposal lifetime.

Provider plugins attach to Phase 2's model catalog/transport boundary and preserve normalized streams plus provider-specific metadata. Support startup registration before model selection, runtime changes, unregister/restore behavior, cancellation and redacted diagnostics. Expose authentication through the chosen broker, acknowledging that a trusted provider implementation may need scoped credentials to call its service. Do not claim arbitrary trusted code cannot inspect memory. Add MCP/virtual model registration to the required-surface inventory; finish required adapters here or assign an explicit Phase 8 closure row.

Evidence: the same dialog sample works in TUI/RPC and fails safely in noninteractive mode; custom component unsupported capability is clear; renderer disposal frees registrations; provider stream/auth/catalog fixtures preserve terminal outcomes and override semantics; teardown removes every owned registration.

### P6-09 — Implement quiescence, reload and failure recovery

Create `ExtensionLifetime`, `ReloadCoordinator`, `ExtensionGeneration`, unload diagnostics and stress tests.

Reload order: stop admission, reach idle or approved cancellation boundary, drain/quiesce owned callbacks/tools/streams, cancel lifetime, invoke idempotent shutdown/disposal, detach registrations and renderer/provider references, invalidate old generation, request ALC unload, then activate replacement resources and reconstruct state. Stage replacements before activation where possible; never run two generations' ongoing resources simultaneously. Preserve previous package/config for rollback.

Use versioned package paths for Windows. Check collectible-context release in tests via weak references after releasing host roots. Normal execution must not repeatedly force GC. Diagnose leaks from timers, statics, delegates, serializers and unfinished enumerators. In-process timeouts cannot terminate arbitrary managed/native code; a hung or rooted plugin requires restart. Do not silently start a duplicate replacement while the old extension is still active. Worker supervision/hard termination is a separate mode, not an ALC property.

Evidence: 100 reload cycles for cooperative fixtures produce no active old generation, duplicate events, surviving tracked resources or uncollected ALCs under the test harness; intentionally leaking and hanging fixtures produce explicit restart-required outcomes instead of false success; cancelled reload and failed replacement preserve a documented recoverable state. The cycle count is a proposed regression gate, not a promise that arbitrary plugins unload.

### P6-10 — Publish SDK authoring assets and release evidence

Create `src/PiSharp.Extensions.Sdk/`, native extension templates, samples, API mapping/migration docs, manifest checker and extension diagnostics guide.

Port representative behaviors: hello/tool, input/context transform, permission gate, stateful todo with branch restoration, session checkpoint, dynamic tools, provider registration, dialogs/status and custom rendering. Each sample has a minimal scenario and automated assertion. Provide normal build/publish/package instructions, debugging guidance, versioning policy, side-effect and thread-safety rules, no-UI behavior, trust limitations and reload troubleshooting. Roslyn scripting is explicitly deferred convenience; native assemblies are the stable contract.

Publish `artifacts/extensions/p6-native-report.json` and a readable companion containing baseline SHA, SDK/API versions, platform results, fixture hashes, supported/deferred/different rows, diagnostics, unload evidence and approval reviewers. Store no credentials or private project content.

## Work sequencing and parallelism

Critical path: P6-01 → P6-02/P6-03 → P6-06/P6-07 → P6-09 → P6-10. Loader path P6-04 → P6-05 can run alongside reducer work after identity/manifest contracts stabilize. P6-08 UI work waits for Phase 5 component/dialog contracts; provider work can proceed with Phase 2 fake transports. Schema tooling, fixtures and sample documentation can run in parallel, but must consume the approved ABI. Do not wait until Phase 5 finishes to begin P6-01–P6-05.

## P6-G measurable exit gate

1. Every mandatory native-surface and event-catalog row has a passing test on applicable supported platforms; every deliberate deviation has an approved reason and fixture. No silent or unclassified API fallback remains.
2. Published native plugins load with conflicting private dependencies and one shared contract identity. A clean native-only installation completes tool/session/RPC scenarios with Node absent from PATH and absent from the package manifest.
3. End-to-end fixtures prove ordered reducers, permitted nested execution, no lock-held-across-callback deadlocks, final-argument policy enforcement and stale-generation rejection.
4. Branch-state and unknown-entry fixtures round-trip, and UI/provider adapters pass their relevant contracts. Required wider integrations deferred to Phase 8 remain visible and block full native v1 acceptance there.
5. Cooperative reload stress passes; deliberately uncooperative plugins produce truthful restart-required diagnostics. Trust denial occurs before executable work.
6. API snapshot and sample consumers build; documented package, trust, disable and troubleshooting procedures are reproducible by a second engineer.

## Risks and effort assumptions

Budget approximately 9–15 human engineer-weeks for this track, assuming Phases 2–5 expose the specified primitives and an experienced .NET engineer plus review/test support. Contract/reducer ownership is roughly 2–3 weeks; loading/trust/lifecycle 2–3; tool/session integration 2–4; adapters/samples/hardening 3–5. These bands overlap and are not independent commitments. Work already completed in earlier phases must be credited, not counted twice in the overall 30–50 engineer-week native estimate. Re-estimate after P6-02 and the first integrated reload.

Primary risks are ABI churn, hidden event ordering, reentrant deadlocks, inability to unload arbitrary code, unsafe trust assumptions and unbounded custom UI scope. Mitigate through catalog-driven contracts, deliberate profile differences, late SDK freeze, fixture-driven integration and honest restart behavior. ALC and ordinary process separation are never described as security sandboxes.

## Primary references

- [Pi v0.99.1 extension guide](https://github.com/earendil-works/pi/blob/v0.99.1/packages/coding-agent/docs/extensions.md)
- [Pinned runner/reducers](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/src/core/extensions/runner.ts)
- [Pinned extension types](https://raw.githubusercontent.com/earendil-works/pi/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/extensions/types.ts)
- [Pinned loader](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/src/core/extensions/loader.ts)
- [RPC extension UI](https://github.com/earendil-works/pi/blob/v0.99.1/packages/coding-agent/docs/rpc-extension-ui.md)
- [Custom providers](https://github.com/earendil-works/pi/blob/v0.99.1/packages/coding-agent/docs/custom-provider.md)
- [Microsoft plugin loading](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)
- [AssemblyLoadContext concepts](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext)
- [Cooperative unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)
- [Native AOT limitations](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
