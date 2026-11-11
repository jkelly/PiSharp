# MCP transport implementation, Pi 1.5.2

Implemented against `upstream-v152` commit `b4afdddf44e059c67ccf5097e5317d913521b470`, following the reviewed MCP discovery contract. No OAuth or CLI command files changed. Requested high reasoning / ultrafast service; effective service tier is unverified.

## Changes

- Added public `IMcpPipe`, `McpPipeHandlers`, and `McpPipeOptions` in `Extensions.Abstractions/Mcp/Transport/McpPipeContracts.cs`. The caller opens and owns the program/tree; there is no process, connection, or environment acquisition in the transport. A caller can wrap `RemoteProcess` by forwarding `Write`, `CloseStdin`, `Close`, and its `Task<int>` as `Task`; no new Env dependency is required.
- Added `McpPipeTransport`: output works while Open is pending, single start, serialized asynchronous receive delivery, send/exit fences, late-open shutdown, graceful stdin-close then timed kill, stable joined close, original exit errors before incomplete-frame errors and disconnect, and bounded last-byte stderr diagnostics.
- Added a shared single-pass JSONL reader and bounded byte-tail helper. Retained fragments are copied, reusable incoming buffers are safe, each incoming byte is scanned once, CRLF and blank frames work, malformed or oversized frames recover at the next newline for Pipe. Existing stdio uses the same parser while retaining its terminal framing-error behavior.
- Added optional `McpWireCallbacks.Error` without changing existing constructor/deconstruction or `Disconnected` meaning.
- HTTP errors now read at most 8192 retained bytes plus one lookahead, omit incomplete UTF-8 at a truncated boundary, retain `ResponseText` / `ResponseTruncated`, and display a surrogate-safe 200-character excerpt. Diagnostic read failure keeps the HTTP status; cancellation still propagates; response disposal remains owned by the existing cleanup path. POST is never replayed.
- HTTP JSON accepts an initial UTF-8 BOM after its existing byte-limit check, including byte-by-byte input.
- `McpProcessLease` captures the last bytes with a fixed ring rather than the first bytes, and defaults to 64 KiB. Its explicit capacity remains capped at 1 MiB. No process-lifetime changes are needed: Windows launches already enter a private non-breakaway, kill-on-close job; `TerminateAndConfirmAsync` terminates that job and checks `ActiveProcesses == 0` independently of the parent process's exit.

## Regression source and registration

Root must register `McpTransportTests.Sync152Cases()` in `tests/PiSharp.Extensions.ContractTests/Program.cs`. Eleven new cases in `McpSync152TransportTests.cs` cover:

- fragmented/reused-byte input, CRLF/blank frames, exact frame bound, malformed and fragmented oversized-frame recovery;
- stderr tail and chunk callbacks, exit error ordering, incomplete EOF, rejected send after exit;
- graceful and forced close, stable close identity, output during Open, close during Open, joining a held async receiver;
- unstarted/repeated/closed admission;
- bounded HTTP diagnostic reads, exact 8192-byte body, invalid truncated UTF-8 prevention, read-failure fallback;
- exact JSON byte limit, oversize JSON, BOM, multibyte and one-byte splits; oversized SSE event disposal.

`McpProcessLeaseTests.Cases()` now includes an automatically registered partial-read ring-wrap and exact-capacity test, and its existing bounded-capture test expects the tail. Existing `McpSseFramingTests` already exercise BOM, multibyte splits, every CR/LF/CRLF boundary, CR-only dispatch while the stream stays open, exact line limit, and event overflow; existing HTTP ownership suites cover cancellation and cleanup-original settlement.

## Verification

Executed a scoped `git diff --check` covering the transport contracts/runtime, process lease, and changed/new tests; it passed. No dotnet build or test was launched by this worker, per root's serialized-build instruction. Root should build/run `PiSharp.Extensions.ContractTests` and `PiSharp.CodingAgent.Tests` after registration, and report actual results centrally.

No real remote connection or executable was acquired. The native early-parent-exit/descendant guarantee is supported by the existing Windows job implementation; a new real-process smoke test has not been claimed.
