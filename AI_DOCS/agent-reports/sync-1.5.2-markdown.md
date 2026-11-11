# Pi 1.5.2 Markdown transformer surfaces

Source: frozen `upstream-v152` working tree, `packages/coding-agent/src/{core/extensions/{loader,runner,types}.ts,modes/interactive/components/{branch-summary-message,compaction-summary-message,custom-message,skill-invocation-message}.ts}`. Service tier is unverified.

## Changes

- Added native `custom`, `summary`, and `changelog` message type constants.
- Node registration stores `{ transformer, messageTypes: Set }`; omitted options retain the existing user/assistant/assistant-thinking default. Explicit empty set disables every surface. Re-registration replaces the registration; exact type matching preserves extension load order. Failures/non-string results preserve the current pipeline text and later registrations still run.
- Descriptor remains a boolean registration-presence field, now testing the callback inside the registration object. Native request transport retains context and boolean admission; the runtime owns filtering.
- Default custom message and expanded skill content use `custom`; expanded branch/compaction payloads use `summary`. Headers are appended after transformation. Constructors accept persisted entry ID/timestamp and transformer list. Custom renderer and collapsed branches bypass transformation.
- Startup changelog and extension-update Markdown call the shared changelog factory. `ui_flow` owns that factory, slash-command call sites, and Events message-constructor metadata plumbing.
- Bounded NodeCommandInputBridge already rejects every defined Markdown registration, including new objects, with UnsupportedRegistration; no callback coercion or compatibility relaxation was needed.

## Evidence

- PASS `node tools/NodeBridge/markdown-transform.test.mjs`: all six surfaces; legacy defaults; subset; explicit empty; re-registration; load order; exception/non-string isolation; persisted context; presence descriptor/late registration notification; exact matching; missing type; bounded bridge rejection.
- PASS scoped `git diff --check` (line-ending normalization notices only).
- Added native authored `sync152.markdown-custom-and-summary-payload-metadata` and `sync152.markdown-six-surface-contexts` in MessageComponentCases. Cover payload/header separation, collapsed/custom-renderer bypass, width, persisted metadata, all six contexts, and native pipeline isolation.
- Added end-to-end native/Node `bridge.sync152-markdown-registration-surfaces-and-isolation` in BridgeCases. Covers actual native descriptor ingestion and native transform requests for all six surfaces with the registration shapes/order/errors/metadata above.
- Updated prior bridge renderer test to supply assistant context; missing context correctly does not match default surfaces.
- Native builds/test execution delegated to root for serialized validation. No .NET builds run by this agent. These are authored native expectations, not upstream runtime captures.
