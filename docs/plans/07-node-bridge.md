# Phase 7 Optional Node bridge

## Goal, scope and prerequisites

Run a published, tested subset of existing TypeScript extensions against PiSharp's native engine. Node is optional: installing, starting and using native PiSharp must require neither Node nor npm. Reference Pi v0.99.1 at commit `d86654abb8862e201933517d6f1fce9f88dd117f`. Do not substitute a C# front end around the complete upstream Pi engine and call that a native port.

Inputs: Phase 1 supplies pinned source/corpus licensing; Phase 3 supplies the authorized action broker; Phase 4 supplies immutable session snapshots/generations; Phase 5 supplies UI capability contracts; Phase 6 supplies registries, typed reducers and lifecycle ownership. Protocol/corpus spikes may begin earlier, but P7-G integration depends on those contracts. Proposed project names remain `PiSharp.ExtensionHost` and `PiSharp.Compatibility.Node`.

## Ordered work packages

### P7-01 — Run the compatibility feasibility spike

Select 8–12 real, pinned extensions: tools, commands, input/context transforms, blocker, persistent state, provider customization and dialog; include a custom component as expected unsupported. Record source/license/hash, imports, API calls, dependencies and scenario. Execute unmodified source against upstream and a minimal bridge prototype with deterministic fakes. Output: `compatibility/node/corpus.json`, normalized traces and a proceed/narrow/defer decision. Do not infer coverage from successful module loading alone.

### P7-02 — Freeze a narrow support matrix

Create `compatibility/node/support-matrix.json` and `docs/extensions/node-compatibility.md`. Tier A covers non-UI tools, commands, simple hooks and JSON state. Tier B adds selected dialogs/status, provider hooks and richer orchestration. Tier C custom components/deep imports has no initial promise. For every export/method classify supported, intentionally different or unsupported, with fixtures and diagnostics. Unsupported registration must fail clearly instead of being silently ignored.

### P7-03 — Define the versioned full-duplex protocol

Create `schemas/extension-worker-v1.schema.json` and protocol DTOs in `PiSharp.ExtensionHost`. Specify handshake/version/features, worker/session generations, IDs, registration/callback handles, requests/responses, progress/streams, cancellation, errors and shutdown. Reserve stdout for protocol and stderr for diagnostics. Set size/depth limits, framing and bounded backpressure. Readers must service callbacks while original requests await replies. Output: protocol golden fixtures and adversarial parser tests, including Unicode, partial frames and unknown versions.

### P7-04 — Supervise the optional Node worker

Implement worker discovery/startup, explicit runtime compatibility checks, heartbeat/exit observation, cancellation and process-tree cleanup. Start one worker per compatibility session to preserve shared extension ordering/event bus; document shared crash/trust scope. Keep Node out of native dependency graphs and default installation. Output: missing/wrong Node, startup failure, crash/hang and clean shutdown tests. Do not install Node automatically during native startup.

### P7-05 — Reproduce module loading within the supported boundary

Build the Node host with a pinned transpilation/module loader and controlled public-package aliases. Honor dependency roots and documented ESM/CJS behavior. Stage registrations until asynchronous factories complete; discard them on failure. Inspect package manifests before trusting install scripts/native addons. Ordinary discovery performs no npm install. Output: local/package/dependency fixtures and resolved-import diagnostics. Imported upstream runtime helpers require explicit adapters; loading an unrelated upstream registry must not create disconnected state.

### P7-06 — Translate registrations, callbacks and mutations

Keep JS functions in Node under opaque IDs; register C# proxies with owner/generation scopes. Implement descriptors, tool execution/progress, commands, supported hooks, unsubscribe and JSON state. Translate in-place mutations to Phase 6 typed decisions without changing reducer order. Distinguish absent/null/undefined and clear semantics; reject cyclic/functions/arbitrary objects crossing the wire unless explicitly represented. Output: two-extension composition, tool-result redaction, handler removal and failed-factory rollback tests.

### P7-07 — Preserve context and reentrant host operations

Provide synchronized read snapshots for supported synchronous getters; never silently change them to async. Specify freshness at callback entry and after brokered mutations. Route host tools, shell helpers and session actions through the native broker, retaining nested IDs and policy. Allow bidirectional callbacks without lock-held waits; revoke stale handles after replacement. Output: nested tool→hook→dialog scenarios, stale replies, snapshot consistency and final-argument authorization tests. Direct Node filesystem/process APIs remain trusted OS access, not sandboxed broker operations.

### P7-08 — Add bounded Tier B adapters

Implement only selected UI/provider features after Tier A parity. Dialog requests correlate responses and cancellation; unavailable UI denies safely. Provider adapters preserve normalized stream events, instrumentation, usage and terminal outcomes. Define authentication/secret access narrowly and redact logs. Keep custom TUI objects unsupported; any future render/input proxy is separately scoped. Output: TUI/RPC/headless cases, provider setup/partial-stream failure and auth-cancellation fixtures with explicitly unsupported neighboring APIs.

### P7-09 — Harden recovery and unknown outcomes

Maintain in-flight operation records and generation fences. Worker death cancels/reports pending callbacks; distinguish definite failure from an action whose side effect may already have occurred. Never blindly retry mutation calls after a lost reply. Enforce resource/payload limits; continuously drain streams; release registrations on shutdown. Output: crashes before execution, after side effect and before response; duplicate/out-of-order/stale messages; queue saturation; stdout contamination; malicious frames. Same-user process separation provides restartability, not an OS security boundary.

### P7-10 — Certify the corpus and ship migration evidence

Run each advertised corpus scenario against upstream and PiSharp from identical synthetic inputs. Compare meaningful event partial order, results, custom session data, errors and cancellation; normalize only nondeterministic fields explicitly. Add native migration examples for unsupported cases. Publish `artifacts/extensions/p7-node-report.json` with baseline/runtime/protocol versions, corpus hashes, platform results and per-API support. Ship bridge installation/removal separately; prove native workflows before and after removal with Node absent.

## Sequencing and P7-G exit gate

Critical path: P7-01/02 → P7-03 → P7-04/05 → P7-06/07 → P7-09/10. Runtime supervision and module resolution parallelize once protocol/identity stabilize. Tier B is separately gated; do not postpone a useful Tier A bridge for arbitrary terminal parity.

P7-G requires all advertised API rows and corresponding unmodified-extension scenarios to pass on supported platforms; unsupported cases produce clear diagnostics. Full-duplex callbacks, cancellation, generation invalidation, bounded queues and unknown-outcome handling must pass fault injection. No Node dependency appears in native-only installs. A tested subset release is acceptable; a blanket compatibility claim is not.

## Effort, risks and decisions

Budget 6–12 additional engineer-weeks including a 1–2 week spike, assuming Phase 6 is stable and scope stays bounded. Allocate roughly 1–2 weeks protocol/supervision, 2–4 facade/Tier A integration and 2–4 hardening plus selected Tier B, with overlap and re-estimation at P7-01/02. Broad providers, native addons or arbitrary custom TUI are separate estimates. Proceed only if the real corpus demonstrates value without requiring the complete upstream engine. Main risks: synchronous API mismatch, hidden module identity, reentrancy, unknown side effects and overstated security/coverage.

## References

- [Pinned extension loader](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/src/core/extensions/loader.ts)
- [Pinned extension contracts](https://github.com/earendil-works/pi/blob/v0.99.1/packages/coding-agent/docs/extensions.md)
- [RPC UI limitations](https://github.com/earendil-works/pi/blob/v0.99.1/packages/coding-agent/docs/rpc-extension-ui.md)
- [Provider stream contracts](https://github.com/earendil-works/pi/blob/v0.99.1/packages/coding-agent/docs/custom-provider.md)
