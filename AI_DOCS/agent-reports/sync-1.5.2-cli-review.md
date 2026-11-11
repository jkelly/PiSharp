# Focused CLI integration review

Read-only review against frozen Pi 1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`, covering missing-cwd import/fork/switch lifecycle, input-image normalization, cache replay ownership, MCP OAuth sign-in, package updates and provider login integration. No implementation edits, builds, live requests or credentials used during this review. Existing rebranded CIMD and public-exception sanitization decisions were treated as intentional differences.

## Findings

### P2: session replacement disposes resources before cache replay is cancelled and joined

`src/PiSharp.Cli/Commands/ProfileRuntimeView.cs:163` starts disposal of the native resource; line 164 starts disposal of the captured view/extension. The `retire` callback only runs at line 168, after both settle. That callback reaches `OfflineSessionProfile.RuntimeView.cs:207`, whose new cache handling is only `warmer.Cancel()`.

The new `CancelAndJoinAsync` is invoked only by overall `CloseProfileViewsAsync` (`OfflineSessionProfile.RuntimeView.cs:253`), not per-attachment retirement. Therefore a timer-fired idle cache replay that is still inside the old transport, or its decision callback, can overlap disposal of the old provider and extension during switch/reload. The old attachment can remain current until replacement publication, so `IsCurrent` alone does not fence this interval. Cancellation after disposing the provider also does not join the original replay task.

The input/cache owner confirmed there is no other per-attachment cache join hook and reported the same issue to root. Proposed fix: expose an awaited per-attachment stop-and-join hook and invoke it before either native or captured-view disposal, retaining existing cleanup failure aggregation and one-shot disposal identity. Do not merely move `Cancel()` earlier.

Focused regression: hold a timer-driven replay at its original HTTP/body operation and/or captured decision callback; start a real session switch and a reload. Assert cancellation is requested, old provider and old extension disposal have not begun, and replacement completion waits until the held original settles. Release it, then verify one disposal, no stale usage append, and the new attachment remains usable. Existing `cache-warmer/cancel-joins-active-replay` tests the helper itself, not this ownership path.

### P2: current-model JPEG quality is discarded by the new image normalizer

`src/PiSharp.Cli/Extensions/PromptImageNormalizer.cs:51` constructs resize options from current-model width, height and byte limits, but unconditionally supplies `defaults.JpegQuality`. The model schema accepts `inputLimits.images.resize.jpegQuality`; the frozen source `_normalizePromptImages` passes the entire resize object to `processImage`, and the native `ImageProcessor.Resize` actually uses `ImageResizeOptions.JpegQuality` as its initial encode candidate.

A custom model specifying quality 95 or 60 is therefore silently encoded with 80 at delivery. This changes the selected bytes and can change whether additional downscaling is needed, despite the new feature's promise to apply current model limits to queued/idle images. Default catalogs using quality 80 conceal the omission.

Proposed fix: read the validated `jpegQuality` field with the other resize values and preserve its legal range/default. Focused regression: a fake image codec with an oversized source records the quality list passed to `Encode`; give current model quality 60 and assert the first requested quality is 60 for both an idle prompt and a queued delivery, while absent quality still uses 80. The input/cache owner has been notified for confirmation.

## Reviewed without another reportable defect

- Missing-cwd flow: continue uses effective cwd without rewriting stored header; fork preserves original source and records the effective target cwd; switch preparation runs under source reservation; rollback retains a newly owned file when staged cleanup fails; imports track the original parent and remove only the copied temporary file when cancelled/forked.
- Input normalization: user role checks, validation before public events, pending identity replacement by reference, metadata/non-image preservation, failed-image hints, and runtime reattachment were inspected. No text-equality queue matching was introduced.
- MCP sign-in: split state check before exchange; redirect URI validation; refresh-only behavior; issuer handling through the existing orchestrator; lock-late cancellation checks and persistence of rotated tokens; explicit client-document policy and per-agent credential paths. Non-cancellable already-sent refreshes match the frozen source ownership contract.
- Package/auth integration: configured-order changed-identity result events, stable pinned/no-change handling, warning-only typed OAuth refresh failures, credential write before catalog refresh, cancellation diagnostics after successful login, and Radius plan preservation of unrelated config were inspected. No additional concrete data-loss or secret-disclosure regression was established.

These findings are source-level. Runtime reproduction remains with the serialized validation owner; no claim is made that the rest of the reviewed surface is exhaustively proven.
