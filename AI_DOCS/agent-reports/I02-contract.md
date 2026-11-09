# I02 Anthropic continuation contract

Parent approved this decomposition before stateful implementation on 2026-11-08.

* `AnthropicMessageThreadStore` owns at most 10,000 session entries, each only a hash, prefix count and reply ID (or disabled marker). Factories accept an injected store and default to `Shared`. `Cleanup(sessionId)` removes one; `Cleanup()` clears all and invalidates in-flight completions. Host lifecycle wires per-session cleanup.
* Thread eligibility requires prompt caching, nonempty session ID, and explicit `SupportsMessageThreads` or exact provider `anthropic` and endpoint host `api.anthropic.com`. Gateways require opt-in. `ChatRequest.DisableContinuation` bypasses both state consumption and state update.
* Compare final projected/hook-adjusted payload, endpoint, model and fixed request settings. Normalize message string content to text blocks and ignore message cache markers. Never retain full transcript or credentials in store. Consume each continuation under lock once; stale/concurrent replies cannot overwrite newer requests or resurrect cleaned sessions.
* Attach structured payload metadata to owned HTTP requests; never parse arbitrary caller HTTP bodies. Existing direct HTTP factories without this metadata remain stateless.
* Only pre-stream known thread errors may retry once: expired/already-continued/in-flight/fingerprint mismatch sends full create; unsupported sends full stateless and disables session. Strict-tool fallback remains independently bounded. Hook and cleanup ownership remains intact.
* Record only successfully cleaned, uncancelled terminal replies with response ID and no fallback response model. Convert the assistant reply using the same projector options as next-request replay. Retain fallback diagnostics even if the resend fails.
* Shared cache warm policy supplies maxTokens=1, maxRetries=0 and DisableContinuation=true. Replays cannot read/write foreground continuation state.
* Fake HTTP regressions cover definitions, initial/create/continue, payload mutation, settings changes, expiry/unsupported fallback, cancellation, cleanup, concurrency, gateway defaults and warm isolation. No live requests/keys.
