# Native cooked-input session chat

`session chat` is a native interactive client over the existing `session rpc` host. It submits owned JSON commands to that host and displays the host's acknowledged history, stream events, tool outcomes, queue receipts and published-extension UI requests. The durable coordinator owns the transcript and selected branch. This client owns a local draft and presentation state; those values are never a second transcript or Agent queue.

```text
PiSharp.Cli session create --session C:\work\session.jsonl --workspace C:\work
PiSharp.Cli session chat --session C:\work\session.jsonl --workspace C:\work --offline-script C:\work\script.json
```

The caller supplies the existing bounded literal offline script. Existing RPC options are accepted unchanged, including `--offline-api openai-responses|anthropic-messages|openai-completions`, explicit `--leaf`/`--root`, per-file read/write authorization, optional exact Bash authorization, and separately approved native package activation. No endpoint, credentials, environment key lookup, provider call, package discovery, source build or install is added. `--output` belongs to other session commands and is rejected here before acquiring a durable writer.

The public embedding entrypoint is `InteractiveSessionCommand.RunAsync(string[] args, TextReader input, TextWriter output, TextWriter error, CancellationToken token = default)`. All three services are borrowed. The compiled route owns the actual cancellable stdin stream, decodes strict UTF-8, and uses the existing explicit UTF-8 stdout writer. It does not change console modes, console encoding, code pages or global environment. An embedding reader must implement cancellable `ReadAsync(Memory<char>, token)`; the client awaits the actual read task rather than abandoning it behind a cancellation proxy.

## Cooked commands

| Input | Behavior |
|---|---|
| Ordinary nonempty line | Submit `prompt`; while busy use the existing steering admission. Acceptance and settlement are displayed separately. |
| `/send text` | Submit literal text, including a leading slash. |
| `/edit` | Start or continue a local multiline draft. Ordinary lines append with LF, including leading and interior empty lines. |
| `/show` | Display the local draft. |
| `/save` | Submit the draft, or answer an active extension editor. Empty or otherwise invalid prompts remain subject to authoritative RPC admission. |
| `/cancel` | Discard a local draft, or return `cancelled:true` to the active dialog. |
| `/state` | Read actual running state and pending-message count. |
| `/steer text`, `/follow-up text` | Submit through the existing registered input admission and independent owned queues. |
| `/clear-queue` | Return and display the actual complete removed steering/follow-up arrays. |
| `/abort` | Await actual work cancellation and cleanup. Retained queues remain until explicitly cleared or consumed by later work. |
| `/quit` or stdin EOF | Close command admission. The actual RPC host cancels outstanding work and joins terminal commits, output, package callbacks and durable storage cleanup. |

Unknown slash commands do not submit a prompt. Draft data is held only locally until an admitted submission. A physical line or assembled draft is capped at 65,536 UTF-16 code units; unpaired surrogates are rejected. CR preceding LF is treated as the cooked line delimiter. Other control characters, including inert NUL, remain prompt data and are escaped for display. These rules do not relax action paths, argv, environment or policy.

## Published native dialogs

The client maps the actual `extension_ui_request` record to a cooked interaction and returns the matching `extension_ui_response` on the same RPC connection. A select displays numbered exact options; confirmation accepts only explicit `yes` or `no`; input returns the line (`/answer ` can introduce literal leading-slash text); an editor starts from the exact prefill and appends cooked lines until `/save` or `/cancel`. Invalid answers retain the pending dialog and send no approval. Notification, status, text-widget, title and editor-text records update bounded logical presentation state. Titles do not emit terminal escape sequences.

UI request identity, duplicate/unknown/stale responses, expiry, source defaults and callback ownership remain enforced by the existing RPC UI coordinator. A tool's UI confirmation is not native filesystem authorization: the mandatory final prepared-action policy still runs independently. This is truthfully **RPC UI mode**, with custom terminal components unavailable; no terminal capability or handshake is fabricated. The local renderer does not claim Pi's terminal widgets, focus, screen layout or editor parity.

The baseline wire does not announce individual dialog expiry or carry a tool-scope identity. The client therefore never guesses that one tool's completion closes another pending dialog. A timed-out dialog can remain displayed until explicitly discarded/answered or agent settlement clears the local view; the authoritative host ignores the stale response. Automatic timed-focus retirement remains unfinished presentation work, not a relaxation of approval or callback lifecycle.

An output application performs one awaited display write and flush while the actual RPC shared writer holds its delivery lease. It never waits for a user's dialog answer under that lease. The input loop remains active during provider work and pending dialogs. `/state`, queues and abort therefore travel through actual authoritative command dispatch rather than local flags.

## Bounded ownership and shutdown

The internal byte connection permits at most eight queued/current complete command records, each at most the existing 1 MiB input frame plus LF. It bounds all waiting/active Send/Read/Flush admissions to sixteen. Output accepts one complete host record per flush, with an 8 MiB upper bound including the existing explicit Bash output profile; the actual host retains its separate ordinary 1 MiB output limit. A display write, after control escaping and UTF-8 encoding, is capped at 8 MiB including LF. At most 1,024 returned history messages, 32 retained dialogs, 128 status keys and 128 widget keys are presented; retained dialog JSON and combined status/widget keys and values each have a 1 MiB character cap. These are logical resource limits, not a total heap bound.

The input task, host task and actual output callbacks are joined before returning. The shared connection close cancels pending reads/sends, joins admitted sends/reads/flushes, discards retained current/pending/queued byte records, and closes owned CTS/semaphore state. The view then clears its retained drafts/dialogs/statuses/widgets. Borrowed frontend services remain open. Cancellation does not bypass an already admitted display flush: its real completion is joined. Output write/flush failure reaches the actual host's poisoned delivery path, cannot release gated provider bytes into successful acquisition, and returns a fixed sanitized stderr failure after owned cleanup. There is no retry, replay or success summary over failed stdout. Previously acknowledged effects can remain and require inspection before retry.

`[accepted]` describes command disposition, not durable completion. `[settled]` follows the host's awaited terminal event barriers. Orderly EOF waits the host's durable close and verifies acknowledged physical length through the existing command. Early EOF may commit an aborted assistant/tool result; it does not manufacture successful provider output or start queued continuation.

`[assistant delta]` is streaming preview. `[assistant]` at `message_end` displays the actual acknowledged authoritative content even when final content differs from those deltas; `[assistant ended]` reports its actual stop reason. This deliberately simple presentation can repeat preview text as committed text. It does not replace the final result with a reconstructed preview.

## Evidence and unfinished scope

Six authored groups in `InteractiveSessionCommandTests.Cases(dotnetHost, cliPath)` exercise actual compiled child processes and direct borrowed-service gates: all three real offline HTTP/provider pipelines with read/write tool turns, exact Unicode/control data and durable selected-branch reopen; the actual published C# UI fixture and independent file-policy denial; running-state/queue/clear/abort; held dialog EOF/abort and package cleanup; closed stdout, actual write/flush faults, held flush/caller cancellation and real read joins; multiline ownership, malformed Unicode, resource denial and argument rejection before mutation. Child deadlines bound test failure only, and every owned child/read is joined on cleanup. These are authored native controls, not a new upstream terminal oracle. They are unexecuted until the root's immutable native gate runs.

P5/P6 remain open. This cooked milestone does not implement raw keyboard/escape input, grapheme/cell layout, bracketed paste, resize/VT rendering, alternate screen, Markdown, images, themes, completion, clipboard, terminal focus/custom components, full interactive/configuration CLI compatibility or live provider/auth support. Those remain required work under the full plans. Existing low-level UI expiry/stale/duplicate controls remain unchanged; the new client does not claim a full independent terminal qualification or a completed phase.

Source/contract grounding: [phase 5](../plans/05-headless-rpc-terminal-ui.md), [phase 6](../plans/06-native-extension-sdk.md), [RPC session host](rpc-session-command.md), [native CLI UI activation](native-cli-extension-ui.md), and the pinned public [Pi RPC UI limitations](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/rpc-extension-ui.md). No unchanged upstream terminal capture is claimed by this slice.
