# I-AI protocol implementation

Implemented the reviewed A1/A2 contract; no builds or runtime tests were launched by this worker. `git diff --check` passes for the owned changed files.

## Runtime changes

- `Protocols/ProviderShared/StrictToolLimits.cs`: thread-safe process-local shared Anthropic/Bedrock session set. At most 10,000 entries, eviction in original insertion order (re-marking does not refresh order), null IDs ignored. `SessionResources` cleans one session or all sessions. Cleanup removes the matching linked-list node too, so auxiliary memory is bounded.
- Both Anthropic request factories consult current cache state in `Prepare` and unprepared `CreateCore`, after request identity validation. They use the effective configured/request SessionId and their existing downgraded factory. Existing already-prepared/hook-replacement bodies remain unchanged. The actual fallback delegate marks state before creating its retry; that delegate is only invoked after the transport's qualified strict-limit rejection. No changes to callback/public-error sanitization.
- Bedrock's stream loop starts with shared cache state and marks it before the fallback retry. `CreateRequestAsync` projects the same state for request inspection. Existing strict-required tools continue to use the strict schema.
- OAuth-projected Anthropic requests now use `claude-cli/2.1.322`; the corresponding AnthropicResolved fixture assertion was updated. `ClientIdentity.cs` remains META-owned.

## Offline regressions registered in ProviderSync Program

- `strict-session.bounded-insertion-order-cleanup-and-concurrency`: null IDs, 10,000-entry cap, duplicate insertion order, one/all/empty-session cleanup, and concurrent bounded insertion.
- `strict-session.key-authenticated-and-prepared-requests-persist`: key/authenticated factories across direct, prepared, onPayload and onResponse paths; second request skips fallback; required tools stay strict; other sessions unaffected; cleanup restores strict even on reused factories.
- `strict-session.failed-fallback-still-persists`: failure of the fallback still leaves session state; disableContinuation does not disable the independent strict cache.
- `strict-session.anthropic-bedrock-share-state`: Anthropic rejection changes Bedrock projection; Bedrock rejection changes Anthropic projection; next Bedrock call sends only one non-strict-preferred attempt.
- `strict-session.refusals-hooks-and-cancellation-do-not-persist`: required-only, wrong status/type, unrelated schema error, hook exception, and cancellation do not mark state.

`Properties/StrictToolLimitTestsAccess.cs` grants only `PiSharp.ProviderSync.Tests` internal access for the cache-policy assertions. Existing no-session fallback tests retain their original semantics, with clarified comments.

Root should run ProviderSync, AnthropicResolved, AnthropicThreads, ProviderApis and any ordinary integration suites it chooses. No live provider or credentials are required.

## Cache-owner follow-up

At cli_input_cache's request, added `ProviderRouteOptions.CacheRetention` in `NativeProviderFactory.ProviderRoutes.cs`: null means short; none/short/long are forwarded to Completions, Responses, and Anthropic bindings; summaries always use none. Invalid values are rejected at route creation. The CLI cache owner resolves effective runtime/scoped retention and supplies it to the route.

Registered `cache.provider-route-retention-and-summary-override` with 15 offline wire cases (three protocols, null/short/long/none and long-summary). This verifies prompt-cache key/retention fields and Anthropic cache markers. No builds run.
