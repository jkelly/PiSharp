# Live interactive slice acceptance

Base: `d761b863ba90164cf60c9d79fcef65c83a18183f`. Author: delegated offline acceptance work, 2026-10-04.

## Ownership and integration seam

Acceptance owns `tests/PiSharp.CodingAgent.Tests/LiveInteractiveAcceptance*.cs` and this checklist. CLI/session production composition belongs to integrator thread `01a1036c-b0d2-75ad-a59c-313766ccf0d2`. Build/test execution belongs exclusively to coordinator `01a1036b-12fa-734b-9ce8-6a3a50d3f0c1`. No execution allocation has been received by acceptance.

Use the existing `PiSharp.CodingAgent.Tests` console runner, its `--filter` and `--report` options. Register `LiveInteractiveAcceptanceTests.Cases(adapter)` once in its existing case chain after the production injection API exists. The adapter is a test-local call to that API, not an alternate runtime or test runner. It must forward the supplied HTTP handler, terminal/viewport, RPC observer, diagnostics and cancellation to the actual terminal command composition. It must initialize a fresh durable session when `CreateNew` is true and open that same session when false. Use the supplied explicit model (`live-acceptance-model`, `openai-completions`, `openai`), fixed OpenAI endpoint and inert key. It must use the actual read tool and existing final-path policy for the one supplied permitted read target. Do not load credentials, environment auth, terminal settings from the user's home, or an offline script instead of the injected HTTP handler.

Needed production seam: existing terminal host/RPC composition with injected provider runtime (using a caller-owned HttpMessageHandler), explicit model/auth, owned IConsoleTerminal + ITerminalViewportSource, cancellation and the existing awaited RPC record observer. Existing `TerminalSessionCommand.RunObservedAsync` already offers the latter I/O/observer boundary, but its RPC host still constructs `OfflineSessionProfile` and requires `--offline-script`. The new API must preserve that existing input, renderer, dispatcher, Agent and session lifecycle path. It need not accept acceptance-specific types. The neighboring provider factory worktree exposes `NativeProviderFactory.CreateCompletions(..., handler: ...)` and fixes the endpoint to `https://api.openai.com/v1/chat/completions`; acceptance follows that policy without modifying it. Recovery prompts wait for the existing `agent_settled` boundary, as the dispatcher can reject prompts in the interval after `agent_end`.

## Offline acceptance cases (authored, not executed)

- [ ] Incremental text: hold the HTTP response body after the first SSE delta; observe that delta in actual terminal output before releasing completion; verify one POST and acknowledged assistant text.
- [ ] Permitted real tool: fake provider emits a `read` call for a fresh fixture file. Actual tool reads the unique fixture marker. Require the next HTTP request's matching `tool_call_id` and content, actual tool-end observation, final assistant rendering, and complete durable transcript. The HTTP handler never implements the tool.
- [ ] Abort: hold an actual SSE body read, send Ctrl-C through terminal input, require the body read to cancel and settle, then submit a second prompt successfully and exit. Require no automatic continuation from the canceled turn.
- [ ] Provider failure: return HTTP 400 from injected handler, require a displayed error and settled run, then accept a subsequent prompt successfully. Require complete readable session and released physical I/O.
- [ ] Persistence: close the terminal command, exclusively reopen its session log, reopen the actual terminal/session command, require prior conversation in history and next HTTP request, and verify a second acknowledged assistant message.

The fake HTTP handler has no fallback network transport and only accepts the fixed expected URI. No socket is opened: the caller-owned handler returns authored SSE fixtures or HTTP 400. These checks cannot establish real inference, endpoint credentials, provider availability, actual Windows console restoration, or interactive usability on Joe's terminal. Screen output uses an injected terminal; native console and real inference need separate user-run evidence.

## Future user-run live smoke (not authorized/executed here)

- [ ] Coordinator records the exact assembled commit and successful filtered offline report before handing over a live command.
- [ ] User opens a normal Windows terminal and starts the integrator's documented live command with their chosen provider/model and configured credentials. Record only model/provider, command flags excluding secrets, commit and outcome.
- [ ] Submit a short prompt and see incremental text before completion; edit/recall a second prompt.
- [ ] Authorize one read of a disposable text fixture containing a unique marker; ask for its content. Verify tool display, final answer and continuation.
- [ ] Abort a longer response; verify input returns and a new prompt works.
- [ ] Exit and reopen the same session; verify history and conversational continuation.
- [ ] Confirm normal terminal state after exit. If provider failure occurs, verify bounded diagnostics and prompt recovery without recording secrets or private payloads.

## Evidence classification

Authored test source is a deliverable, not a passing test receipt. Only the coordinator's allocated build/test execution can promote these cases to checked items. User-run real inference is a separate receipt; neither fake HTTP success nor an offline-script terminal demonstration can substitute for it.
