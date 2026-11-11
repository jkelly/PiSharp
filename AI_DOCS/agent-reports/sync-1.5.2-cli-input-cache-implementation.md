# I-CLI-INPUT-CACHE implementation

Frozen reference: Pi v1.5.2 `b4afdddf44e059c67ccf5097e5317d913521b470`, compared with v1.5.0 `f9a26365568127461c47a07a37dcb42c140a3ac5`. Requested high/ultrafast; effective service tier unverified.

## Changes

- Added the native `AgentOptions.NormalizeInput` / `AgentLoopCallbacks.NormalizeInput` delivery hook. It executes before `message_start`, persistence, and provider submission. High-level pending input replacement uses the admitted message reference, never its text. Role changes are refused and normal validation remains active.
- `PersistentAgentSession.InputNormalizer` supplies the host callback through the existing bridge for both ordinary and registry-backed construction. It composes with a caller's existing AgentOptions normalizer. Lifecycle reservations and custom-message behavior are unchanged.
- `PromptImageNormalizer` processes each delivered user image using current `images.autoResize` and `inputLimits.images.resize` metadata, preserving other content blocks and message/image metadata. It appends conversion/resize/omission hints to the first text block (or a new block), and removes failed images. Immutable unchanged messages keep their identity.
- Every profile runtime-view attachment binds the normalizer, including session replacement/reload. Image-only and identical-text inputs remain distinct queue entries; no content/text key is used. RPC already publishes the authoritative queue snapshot on input message start, and atomic queue removal already accepts image-only values, so no additional RPC queue matcher was introduced.
- The default `cacheWarming` mode is now `idle` in core, schema, interactive settings and selector configuration. HTTP idle timeout is unchanged: upstream's changed idle default is cache warming, not HTTP timeout.
- Cache warmer replay captures the connection that sent each request. Replay routes are cached per connection rather than globally on the profile; model changes cannot replay through the first model's transport. Current-attachment and disposed-session checks stop stale work.
- Added `CacheWarmer.CancelAndJoinAsync`, tracking original timer-driven refresh tasks. Profile close cancels and joins these before view/provider teardown; forgotten attachments cancel their warmers.
- Fixed the underlying live option gap: scoped/provider `PI_CACHE_RETENTION` (nonempty scoped value first) is resolved to an explicit retention on the connection, passed to normal and warm Anthropic/Completions/Responses/PiMessages/Bedrock/Codex route options, and exposed to the warmer through `CacheWarmingOptions`. Summary routes still explicitly disable caching. Resolved Anthropic routes and deferred routes receive the same runtime environment choice. AI owner added `ProviderRouteOptions.CacheRetention` forwarding in its owned factory.
- Replay retains request/session/thinking metadata and sets `DisableContinuation=true`; the one-token replay route keeps its original provider options. Native retry options already default to zero and these live constructors never override them, so no new retry API was necessary. Codex's upstream API likewise does not implement a max-output-token field.

## Tests and registration

New `tests/PiSharp.CodingAgent.Tests/QueuedImageNormalizationTests.cs`, `QueuedImageNormalizationTests.Cases()` (root registered):

1. Current model resize limits, preserved non-image/image metadata, invalid image omission, unchanged identity.
2. Two image-only values with identical empty text, late normalization, ordered separate identity, and consistency between started events, persisted content and provider requests.
3. Atomic image-only queue removal.
4. Idle default and independent replay retaining request options.
5. Actual captured native Responses/Completions HTTP bodies: environment long retention on main + one-token replay, none on summary; scoped precedence.

Existing registered `tests/PiSharp.SessionParity.Tests/UsageCases.cs`:

- Updated the default-mode expectation to `idle|idle|off|idle`.
- Added `cache-warmer/cancel-joins-active-replay` with held original request and virtual time.
- Added `cache-warmer/idle-default-keeps-warming` with virtual time.

AI owner registered its provider-route retention matrix separately.

## Validation

No dotnet build or test run was launched: root owns serialized builds/runs. Scoped `git diff --check` was run; line endings on untouched portions of existing CRLF-tracked files were preserved to avoid whole-file diffs. Suggested root commands after build:

```powershell
& "C:/Program Files/dotnet/dotnet.exe" run --no-build --project tests/PiSharp.CodingAgent.Tests -- --dotnet-host "C:/Program Files/dotnet/dotnet.exe" --cli "E:/AI/PiSharp/update-1.5.2/src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll" --filter "queued image"
& "C:/Program Files/dotnet/dotnet.exe" run --no-build --project tests/PiSharp.CodingAgent.Tests -- --dotnet-host "C:/Program Files/dotnet/dotnet.exe" --cli "E:/AI/PiSharp/update-1.5.2/src/PiSharp.Cli/bin/Debug/net10.0/PiSharp.Cli.dll" --filter "cache warming"
& "C:/Program Files/dotnet/dotnet.exe" run --no-build --project tests/PiSharp.SessionParity.Tests -- --only "cache-warmer/"
```

Commands assume root builds Debug/net10.0 first. The actual CodingAgent runner `--filter` and SessionParity `--only` parsing were inspected; these commands were not executed.

## Post-review retirement and JPEG fixes

Read-only review confirmed that per-attachment `ProfileRuntimeViewOwnership.CloseAsync` previously disposed native/view resources before `ForgetRuntimeView` cancelled its warmer. Added exact-attachment capture at binding and an awaited `StopCacheWarmingAsync(attachment)` before starting either native/view disposal original. Failures feed the existing aggregation and do not skip either cleanup. Process shutdown retains its all-warmer join; stale retirement cannot stop the new session's warmer.

Added `cache warming attachment retirement joins replay before resources and leaves current warmer alive` to the already registered `QueuedImageNormalizationTests` cases. It uses a held replay original, virtual timer and actual profile lifecycle replacement to assert cancellation, blocked old disposal, eventual exactly-once native disposal and surviving current timer.

The model resize object also includes `jpegQuality`; normalization now forwards it. The codec fixture explicitly requests quality 95 and verifies that 95 is the first encoder quality (instead of default 80).

No dotnet build/test was launched; validator owns the build slot. Scoped diff check passed after these edits.

## Anthropic runtime isolation correction

Validation found that asking the legacy `LiveSessionRuntime.ReadEnvironment` again for retention broke explicitly supplied authentication's isolation and duplicated an implicit-route read. Corrected this with an internal snapshot handoff: `ResolveAnthropicRouteAsync` and the CLI `ResolveAnthropicAsync` extension now return a fourth tuple item, `CacheRetention`, computed from the already captured `LiveProcessEnvironment`. CLI and ModelRuntime consumers pass that value to the connection. Explicit resolved-auth calls use only supplied/scoped data and default short, without accessing the legacy runtime, process environment or registry. These methods/types are internal; no public deconstruction API changed. Existing strict tests were not weakened.

## Full-suite fixture correction

The first CodingAgent run exposed two incorrect new fixture assumptions, not production failures. The delivery helper included an unsupported `custom` content block; only the pure normalization test now uses that arbitrary block, while real delivery uses supported text/image blocks. The retention test assumed the requested one-token cap was emitted literally by Responses; frozen `api/openai-responses.ts` lines 37/376 requires a minimum of 16 output tokens. The test now asserts 16 for Responses and 1 for Completions, preserving the existing provider rule. The older prompt-template image fixture used invalid PNG bytes and is being corrected by the CLI session worker to a real small PNG. No production validation or image processing was weakened.

The subsequent ImageCapabilityFrontend RPC/replacement failures had the same invalid `AA==` PNG fixture. The first-run stderr trace showed the omission hint in the fresh `message_start`, before historical reopen. Replaced `ImageJson` and its exact projected data URL with the same valid 1x1 PNG and updated the summary data-exclusion check. Canonical history prefix, opposite-capability projection, ABA/stale authority and exact image assertions remain unchanged. No production changes were needed.
