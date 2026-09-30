# Phase 5 Headless RPC then terminal UI

## Outcome and boundaries

Implementation and tests below are planned, not completed.

Deliver usable print and JSON modes, a compatible RPC subprocess, then a terminal interface over the same PiSharp.CodingAgent coordinator. Frontends observe committed state and submit commands; none owns the transcript. This phase contains two acceptance gates: P5A headless and P5B terminal. Native plugins are integrated through phase 6 contracts, with fake host services until its runtime is ready. Neither gate requires Node.

Baseline facts: Pi v0.99.1 uses LF JSONL, optional string request IDs, asynchronous command responses, protocol-only stdout and orderly disposal on stdin EOF. Prompt acceptance differs from settlement; handled prompts need not start a run. JSON mode includes a session header; RPC does not. RPC supports dialogs but lacks terminal components. These facts come from the pinned references below. Task designs, capability negotiation, limits and estimates are PiSharp proposals.

## Entry conditions and ownership

Require phase 1 CLI/RPC inventories and golden fixtures; phase 3 cancellation, queue and tool events; phase 4 session projection and coordinator APIs. Begin terminal research earlier against fake streams. The experience lead owns Cli/Rpc/Tui; the runtime lead reviews coordinator boundaries; QA owns cross-platform traces. Freeze the small UI-service contract jointly with phase 6 before either side implements it.

## Work packages

### P5-01 Freeze CLI and configuration behavior

Owner: Cli/CodingAgent. Input: pinned inventory and settings/resources contracts. Implement parsing, mode selection, precedence, resource discovery, help, exit codes and separate PiSharp home. Cover redirected streams, piped prompts, @files, conflicts, offline behavior and explicit trust. Output: CLI matrix and golden invocations. Evidence: subprocess tests, including RPC rejection of @file. Package/auth/MCP commands route to their owning modules.

### P5-02 Implement one shot output adapters

Owner: Cli. Prerequisite: P5-01. Add print final-text output and JSON event projection, with diagnostics on stderr. Preserve delta-only updates, finalized messages and JSON's opening session header. Output: mode adapters and fixture transcripts. Evidence: success, multiple supplied prompts, tool/provider failure, cancellation, noUI and redirected-output cases; no cursor controls or secrets in ordinary machine output.

### P5-03 Implement bounded JSONL transport

Owner: Rpc. Input: frozen wire DTOs. Frame bytes on LF only, tolerate preceding CR, decode fragmented UTF-8, serialize complete records through one writer and propagate backpressure. Output: transport plus limits/deviation policy. Evidence: every-byte fragmentation, CRLF, U+2028/U+2029, malformed JSON, excessive frames, slow readers and broken pipes. Never interleave log bytes, discard authoritative events or buffer without bounds.

### P5-04 Implement asynchronous command dispatch

Owner: Rpc/CodingAgent. Prerequisites: P5-03 and coordinator. Bound concurrent command admission and keep reading while operations await; correlate responses by optional ID rather than order. Maintain one response per ordinary command, separate UI responses, and generation-checked session changes. Output: command-handler inventory. Evidence: concurrent get_state, prompt, abort, compaction, model changes and fork/switch; late completions cannot mutate a replacement session. Serialization protects state transitions, not entire asynchronous commands.

### P5-05 Prove lifecycle and client correctness

Owner: Rpc and optional Rpc.Client. Prerequisite: P5-04. Add event subscriptions before submission, disposition-aware waits, deadlines, cancellation and EOF disposal. Evidence: started/queued/handled prompts, rapid settlement, failures after acceptance, retries after agent_end and outstanding dialogs at shutdown. Preserve baseline abort behavior: queued work remains unless clear_queue runs first. Output: reference-client report and C# sample; no false promise that every successful response means completed work.

### P5-06 Define mode specific UI capabilities

Owner: Tui.Abstractions/Rpc; jointly reviewed by phase 6. Define typed requests and Value, Cancelled, TimedOut, Unavailable outcomes; map baseline wire defaults separately. Legacy RPC remains handshake-free. Rich capability negotiation is an opt-in PiSharp protocol extension. Output: mode capability matrix and fake UI host. Evidence: confirm never approves on noUI/timeout/disconnect; duplicate, stale and cancelled dialog responses resolve once. Custom terminal components require an explicit terminal capability.

### P5-07 Select the renderer through a spike

Owner: Tui. Timebox: 3–5 engineer-days. Compare a thin owned VT renderer with one maintained .NET candidate behind ITerminal/IInputSource/IClipboard. Exercise streaming, editor, resize, main scrollback, alternate screen, overlay and image fallback. Output: executable experiments, measurements and ADR. Prefer owned bounded-line/cell layout plus differential writes unless the candidate demonstrably fits. A failed spike triggers re-estimation, not an unsupported library promise.

### P5-08 Build terminal input and text layout

Owner: Tui; prerequisite: P5-07. Add OS terminal adapters, raw input, incremental escape parsing, bracketed paste, key bindings, multiline editing and focus. Separate grapheme editing from display-cell width; pin width data and terminal-specific fallback policy. Output: input/editor/layout modules. Evidence: combining marks, CJK, emoji/ZWJ, ambiguous width, tabs, split sequences, huge paste, IME cursor placement and interruption. Restore terminal modes on normal exit and recoverable failures.

### P5-09 Build rendering and interaction

Owner: Tui; prerequisites: P5-06/08. Add Markdown/code/tool views, themes, completion, selectors, overlays and native component adapter. Implement main-screen scrollback and alternate-screen viewport, resize invalidation, diff rendering and supported synchronized writes. Add Kitty/iTerm2 image capability paths with text fallback, including iTerm2 alternate-screen fallback. Evidence: streaming while scrolled up, nested focus, selection/copy, small windows, image cleanup and terminal-control injection sanitization.

### P5-10 Qualify terminal and headless delivery

Owner: QA with experience lead. Prerequisites: P5-01–09. Combine deterministic virtual-screen tests, real PTY/ConPTY tests and recorded real-terminal checks on Windows x64, Linux x64 and macOS arm64. Cover Ctrl+C/Escape, queued-text restoration, child-process cleanup, resize storms, SSH/multiplexer fallbacks and non-TTY paths. Output: versioned terminal matrix, accessibility checklist and compatibility deviations; phase 6 reruns plugin-backed scenarios when available.

## Gates and evidence

P5A requires all mandatory CLI/JSON/RPC rows passing, clean stdout bytes and an unchanged reference client pointed at PiSharp through configuration or a launcher only. Store normalized traces, stdout/stderr captures and artifact versions. Reference-client tests may use Node in development; the product and native smoke suite must pass without it.

P5B requires the declared OS/terminal matrix, bounded component widths, restored terminal state, keyboard-only operation, text-only/error fallbacks and measured responsive streaming. No high-severity protocol, state-loss or terminal corruption defects remain. Unknown terminal features degrade explicitly. Integrations dependent on real native plugins require a phase 6 rerun before native release.

## Sequence effort and risks

P5-01–06 establish P5A; P5-07 can run in parallel. P5-08–10 establish P5B. QA fixture authoring runs throughout. Allow roughly 6–9 engineer-weeks inside the overall native budget, assuming stable core contracts, one experience engineer and shared QA. Re-estimate after the spike and P5A. Main risks are terminal width disagreement, deadlocks under backpressure, capability drift and late CLI scope discovery; each has a named test or decision above.

## Pinned sources

- [RPC framing and lifecycle](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/rpc.md)
- [RPC commands and queue semantics](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/rpc-commands.md)
- [JSON event projection](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/json.md)
- [RPC UI limitations and cancellation](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/rpc-extension-ui.md)
- [CLI commands and modes](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/cli.md)
- [TUI rendering and component contract](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/tui/README.md)
