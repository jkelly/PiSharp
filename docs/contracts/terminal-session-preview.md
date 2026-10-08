# Native terminal session preview contract

This tranche connects the owned native VT renderer to the existing durable RPC
session composition through an explicit Windows preview command:

```text
session terminal --terminal-preview --session <existing absolute JSONL> --workspace <existing absolute directory> --offline-script <absolute JSON> [existing rpc options]
```

The preview displays Unicode as ASCII escapes. The ordinary `session chat`
command retains its cooked input and text presentation. This contract describes
the source candidate and its required checks. The new console-close lifecycle
candidate awaits execution. Runtime receipts and the independent frozen-snapshot
review are required before acceptance. The requested source-authoring lane is
Sol 6.1 Xhigh; runtime model metadata is unavailable, so this document makes no
model attestation.

## Composition and ownership

`TerminalSessionCommand.RunAsync(string[], IConsoleTerminal,
ITerminalViewportSource, TextWriter, CancellationToken)` borrows its console and
viewport source. CLI dispatch acquires the actual standard-console lease before
calling the command, awaits the command, then awaits lease disposal before
reporting an acquisition or restoration error. The command never disposes that
borrowed lease or changes the process standard-handle table.

The explicit terminal route and its real-console fixture opt into the
root-owned `TerminalLeaseOptions.FollowActiveScreenBuffer` setting. It defaults
to false for existing lease callers. When enabled, each owned write and viewport
query opens and disposes the current active `CONOUT$` buffer under the lease's
lifetime protection. The acquisition-time output duplicate remains the exact
original-state/restoration handle. This option addresses alternate-buffer
cursor/output and geometry routing; it does not establish arbitrary protocol
mode restoration or general terminal capability parity.

The command reuses `RpcSessionCommand.RunWithPresentationAsync`,
`BoundedRpcConnection`, and the accepted `InteractiveSessionFrontend`. Submitted
lines go through the existing `LineAsync` command and dialog parser. The
frontend's identity-aware publication, retirement, and pending-response barriers
remain authoritative. The view owns presentation, not a second dialog state or
an alternative coordinator. Canonical prompt, provider message, tool arguments,
tool result, and durable transcript data remain in the existing composition.

The root-owned presentation seam is
`IInteractiveSessionPresentation.PresentAsync(string, CancellationToken)`.
The previous text-writer frontend constructor remains available. The root-owned
viewport seam is `ITerminalViewportSource.ReadViewport()`, returning
`TerminalViewport(Columns, Rows, Left, Top, BufferColumns, BufferRows)` from the
owned native output duplicate. The new source files add a bounded terminal view
and an end-of-draft input editor over those seams.

The view serializes presentation, draft changes, diagnostics, resize redraws,
and trusted terminal entry/exit. Entry enables the alternate screen and bracketed
paste; exit disables bracketed paste and leaves the alternate screen. An entry
write is marked as requiring cleanup before it is awaited, including partial
write failure. Renderer disposal and all admitted view operations settle before
the awaited exit write. These commands assume the preview owns those protocol
modes initially; the lease does not capture an arbitrary prior alternate-screen
or bracketed-paste state. General protocol-state preservation is an open gate.

Shutdown cancels and joins the input producer, consumer, escape timer, resize
timer, RPC host, connection, and view before returning. Every actual read or
write is awaited through physical settlement. A producer I/O, decoder, or
admission failure stores its original exception before canceling a consumer
that may be awaiting display. Ctrl+C reaches command shutdown from the producer
even while the consumer is awaiting display. A cancellation signal alone does
not constitute completion of a held physical write. Diagnostics are presented
through the serialized view and copied to the borrowed error writer after the
view leaves the alternate screen. They do not write VT instructions supplied by
model or tool text.

The explicit full-screen host can open
`WindowsConsoleCloseScope.Open(WindowsConsoleTerminal)` over its verified live
lease. This process-local scope handles actual control types 2, 5, and 6; types
0 and 1 return `FALSE` for their existing Ctrl+C/Ctrl+Break behavior. It records
the first actual type, cancels a private token through an owned observing
`CancelAsync` task, and waits up to four seconds for application cleanup. The
delegate remains rooted through registration and admitted native callbacks.
Callback, cancellation, admission, timeout, and unregister failures remain
observable. Only one scope is admitted per process, with at most eight admitted
native callbacks.

`JoinCancellationAsync(CancellationToken token = default)` awaits managed
cancellation callbacks separately from the native handler. A no-close join
returns immediately. Cancellation errors are recorded when the observing task
sees them and rethrown on join/disposal; no task is detached by a canceled join
token. The host joins its command and console lease, joins managed cancellation,
and physically flushes final required diagnostics/receipts before calling
`SignalCleanupComplete()`. It then awaits scope disposal, which unregisters and
joins native callbacks before releasing resources. An unregister failure keeps
the installed delegate rooted through process exit and fails the scope. Windows
may terminate a close-event process after the handler returns despite `TRUE`.
Cleanup timeout is failure, and a final pre-signal receipt does not claim the
still-waiting native callback has joined.

## Explicit preview display and editor profile

The width policy identifier is `pisharp-escaped-ascii-preview-v1`. Every rendered
printable ASCII character has width one. The independently qualified
`PinnedEastAsianWidth` primitive does not change this policy or establish full
grapheme width parity.

The view validates UTF-16 and enumerates runtime `StringInfo` text elements.
It projects each complete element before wrapping:

| Source fragment | View projection |
| --- | --- |
| Printable ASCII except backslash | Same printable character |
| Literal backslash | Two backslashes |
| Tab | Visible `\t` |
| LF, CR, or CRLF | Logical row break |
| Other BMP scalar, including controls | Lowercase four-digit `\u` escape |
| Supplementary scalar | Lowercase eight-digit `\U` escape |
| Text element longer than 256 UTF-16 units | `[oversized cluster]` |
| Projected fragment wider than the current row | `?`, as a whole fragment |

The existing frontend first applies its own `ChatEditor.Display` control
escaping to presentation text. The view subsequently projects the resulting
literal backslashes. Input drafts enter the view directly. The two paths can
therefore produce different visible escaping for the same control scalar. This
mapping is a bounded preview projection, not a reversible or lossless encoding.
It never authorizes untrusted ESC, C1, OSC, CSI, or paste delimiters to become
terminal commands. Whole projected elements are retained and wrapped without
cutting a surrogate pair or one of the runtime text elements.

Input preserves canonical text and accepts decoder text/paste events. Enter
submits the entire draft; embedded pasted newlines do not submit separate
prompts. Shift+Enter adds a newline when reported by the decoder. Backspace
removes the final complete runtime text element. Escape clears the draft and
uses the existing `/cancel` path. `/quit` uses the existing frontend command;
Ctrl+D on an empty draft is a logical quit, distinct from an actual native read
returning zero. Unsupported keys and unknown protocol sequences do not become
output instructions. Cursor editing, selection, completion, and main-screen
parity are outside this bounded editor profile and remain later requirements.

Runtime `StringInfo` segmentation is not qualified against the pinned upstream
`Intl.Segmenter`, Unicode-v properties, or RGI data. This preview is not full
Unicode terminal-cell parity, even though canonical Unicode input and durable
content are preserved.

## Bounds and geometry

| Resource | Hard bound |
| --- | --- |
| Terminal read chunk | 4,096 UTF-16 units |
| Decoded queued input events | 32; overflow fails admission |
| Canonical draft | 65,536 UTF-16 units |
| One presentation input | 8 MiB plus its frontend newline |
| Retained projected history | 32,768 characters, evicting whole fragments |
| One projected text element | 256 UTF-16 units before replacement |
| Concurrent admitted view operations | 16 |
| Captured diagnostic text | 8,192 characters |
| Admitted physical viewport | 4,096 columns by 1,024 rows |
| Rendered region | At most 256 columns by 64 rows, 16,384 cells |
| Visible draft rows | At most three |

The viewport must fit inside the independently reported native buffer, including
its left and top offsets. The view queries actual geometry on each draw and on
its owned 100 ms resize timer. A geometry change invalidates the renderer cache
and awaits a new complete frame. The timer cadence is an observation cadence,
not test evidence that a resize occurred. Larger viewports use the stated
bounded presentation region. The tests below use zero-origin physical windows;
nonzero viewport origins and arbitrary host buffer behavior remain qualification
requirements.

The renderer retains its accepted bounded full/diff frame behavior: serialized
physical writes, cursor output, explicit invalidation, and cache commit only
after the awaited physical write. Failed or canceled writes invalidate the
cache. This tranche does not change renderer limits or select a Unicode width
policy by heuristic.

## Required whole-workflow witnesses

`TerminalSessionCommandTests.Cases(string dotnetHost, string cliPath)` registers
the following cases. `WindowsConPtyTerminalSessionFixture.TryRunWorkerAsync`
returns `Task<int?>` and recognizes `--windows-terminal-session-worker`; root
dispatch calls it before normal test argument parsing. All names begin with
`terminal-session.` and are awaited directly under their owned cleanup/deadline
discipline, without an outer timeout that detaches the actual child.

| Case | Required witness |
| --- | --- |
| `terminal-session.actual-workflow` | Actual ConPTY nonce input, recorded provider stream, published native confirm callback, invalid response then approval, actual native write tool, durable final content, idle resize, resize while the published callback holds the active turn, exact native cells/cursor, quit, physical flush acknowledgement, and separate-process reopen |
| `terminal-session.actual-standard-handles` | The complete dialog/tool/save/reopen lane in a hidden newly created classic console, using genuine `GetStdHandle` character devices and `WindowsConsoleTerminal.OpenAsync` |
| `terminal-session.actual-control-c` | Actual ConPTY Ctrl+C while the dialog awaits input, no approval or subsequent write effect, joined callback/RPC/native workers, complete durable error, and strict restoration |
| `terminal-session.actual-conpty-input-close` | Physical close of the parent's ConPTY input endpoint, genuine control type 2, joined managed cancellation, canceled durable tool result without approval/effect, strict lease restoration, physically flushed final cleanup receipt before signal, and actual child exit |
| `terminal-session.actual-native-write-fault` | An actual failing `WriteConsoleW` on an invalid handle in the test-owned output wrapper, no approval or subsequent effect, joined shutdown, and strict restoration |
| `terminal-session.actual-shrink-exit` | Actual ConPTY resize to 20 by 4, draft/cancel input witness, quit without resizing back, native main-buffer geometry, and independently evaluated strict cursor restoration outcome |
| `terminal-session.actual-redirected-cli-admission` | Compiled CLI with redirected handles rejects terminal admission without VT output, durable mutation, or a tool effect |
| `terminal-session.held-display-producer-failure` | Explicit virtual physical write remains owned until release after a real decoder failure cancels the blocked consumer |
| `terminal-session.held-display-control-c` | Explicit virtual physical write remains owned until release after producer-side Ctrl+C cancels the blocked consumer |

The complete lane uses the existing published native UI package, approved by
task-local artifact hashes, and the real offline `openai-responses` recorded SSE
composition. The dialog result must be `approved: true` in RPC capability scope.
The saved tool effect and authoritative durable assistant content include
Unicode and controls; equality is checked against their canonical values,
independently of the escape display. Exclusive `SessionLogStore.OpenAsync` after
the command validates physical durable acknowledgement. A separate contained
process reopens the same acknowledged prefix, displays its prior content,
submits another nonce-bearing prompt, and verifies no callback or tool replay.
The test does not reconstruct the authoritative final message from deltas.

Every screen receipt is authored after an awaited real renderer write and uses
`ReadConsoleOutputW`, `GetConsoleScreenBufferInfo`, and `GetConsoleCursorInfo`.
The fixture reopens an owned `CONOUT$` observation handle after that write and
disposes it after observation: a handle opened before alternate-screen entry
continues to identify the original main buffer. The original handle remains the
independent main-buffer restoration witness. Failed screen/cursor observations
retain their actual native cells and state before throwing, and unexpected
worker completion is reported directly rather than obscured by subsequent EOF.
It checks actual cells, dimensions, visible cursor, and physically settled lease
write counters. No VT screen emulator, synthetic program counter, sleep,
`Task.Yield`, timeout-as-success, or resize-back substitutes for an observation.
Resize during an active turn is specifically held at the real published native
dialog; sustained provider streaming under native backpressure remains a later
qualification requirement.

The native fault case is a disclosed test-wrapper injection with an actual
failing native call. It does not establish production `WriteConsoleW`
backpressure, short-write recovery, or a failure of the shared owned output
duplicate. The held-display cases explicitly use a virtual sink and do not
claim physical Windows backpressure evidence.

The prior `actual-native-eof` experiment proved that closing this ConPTY input
pipe generates `CTRL_CLOSE_EVENT` rather than a zero-length `ReadConsoleW` read.
Its immutable failure receipts remain retained. The current scenario is named
`actual-conpty-input-close` and qualifies actual close cleanup; genuine native
read EOF remains an open conformance gate. Ctrl+D is not substituted. The shrink case retains
current geometry without restoring its prior dimensions. If the original
absolute cursor lies outside the actual current buffer, a typed
`RestorationFailed` with `RestorationConfirmed == false` is the expected strict
lease outcome and is recorded as unqualified shrink restoration. Observing that
limitation is not acceptance of universal shrink-exit restoration.

The earlier isolated native EOF worker registered a test-owned
`SetConsoleCtrlHandler` probe. Its rooted Winapi callback wrote a bounded
exclusive JSON file with the actual DWORD event type, process ID, and nonce,
called `FileStream.Flush(true)`, and returned `FALSE` to preserve default
termination. It captured actual control type 2, process 2068, nonce `d512416c`,
and default exit `0xC000013A`. Its physical child joins, missing final cleanup
receipt, original source snapshot, and probe files remain preserved. This was
an event-type experiment, not a passing read EOF test.

The current close worker opens the production close scope before running the
command and links that scope's token. Before signaling completion it verifies
actual type 2, no callback timeout/error, managed cancellation joined, exact
lease restoration and native I/O worker joins, complete physically acknowledged
durable history with a canceled native tool result, no approval or file effect,
and callback/package retirement. It creates and flushes an exclusive bounded
`conpty-input-close-cleanup.json`, then awaits the final control packet flush.
Only afterward does it signal cleanup and await scope disposal. Parent accepts
native forced exit `0xC000013A` or natural zero exit only with that actual
type-2 final cleanup proof. The pre-signal scope snapshot truthfully shows the
native handler still active; the parent physically joins process exit. Missing
receipts, unsuccessful restoration, timeout, or another exit still fail.

## Physical child and evidence discipline

The fixture owns each actual process handle and a kill-on-close job. It creates
children suspended, assigns containment, then resumes them. The classic console
uses `CREATE_NEW_CONSOLE` and hidden startup flags. Its input arrives through
actual `WriteConsoleInputW` records; ConPTY input arrives through its physical
parent pipe. It does not replace standard handles, attach to an executor
console, or allocate a console in the executor.

The fixture observes up to 128 by 16 native cells. Receipts are capped at
32,768 characters, 256 packets per process, and 4,096 retained packets per suite.
ConPTY output retention is capped at 65,536 bytes while the owned drain continues
through EOF. Truncation fails qualification. Raw bytes and their SHA-256 are
retained, but conhost reserialization prevents comparing them to a renderer byte
golden. Each process's nonce input and output are witnessed by exact native draft
cells and visible cursor position before Enter submits it. Conhost can place
control sequences between changed nonce characters, so contiguous raw nonce
bytes are retained as an informational `actualRawNonceBytesObserved` value,
not a pass criterion. A separate `actualNativeNonceOutputObserved` receipt
records the actual native draft witness. Raw output must still be nonempty,
bounded, hashed, and physically drained and joined. Reopen waits for exact prior
`FINAL` content in native cells; the bounded visible history can clip its leading
`[history]` label.

The 20 second case deadline signals failure. Cleanup still requests contained
termination when needed, awaits the real process signal, joins native ConPTY
close and output drain, and then releases handles. It reports actual exit,
termination, close, and drain evidence. A successful job termination is not
followed by a redundant direct termination race. A killed child does not imply
that its lease restored the console. An unsupported native hang requires the
root's containing watchdog; no detached task is treated as completion.

All control writes await their auto-flush. Disposing the control writer after
its peer has exited can still report a broken pipe. That cleanup exception is
retained separately; it neither replaces an earlier worker failure nor fails a
protocol that already completed with an actual joined qualified-exit child and all
required physical witnesses. Before complete protocol settlement it remains a
failure.

Each workflow allocates a fresh task-local evidence root, records ownership,
and uses exclusive file creation. Package copies, approval, scripts, canonical
durable log, effect file, marker events, and receipts remain there for review.
Only a contained test child changes its temporary and marker environment.
Original standard-handle values/types are compared after lease disposal, but
this is not qualification of every inherited alias or unrelated process handle.

## Remaining acceptance gates

The parent preserved the initial integrated build failure (`CS4007`) and the
subsequent corrected focused run. The corrected build had zero warnings/errors;
the redirected admission and both held-display cases passed, while all six
physical workflow cases failed before normal input began. Their native lease
workers and parent cleanup joined. The initial fixture queried its pre-entry
main-buffer handle after alternate-screen entry, omitted a screen receipt on
validation failure, and could mask the command outcome with pipe disposal or
EOF. The source correction described above awaits fresh parent execution; those
original failures remain evidence and are not relabeled as passing scenarios.

The parent's later native-cursor focused run built with zero warnings/errors
and passed shrink-exit plus the three previously passing cases. Whole workflow,
Ctrl+C, and native write fault reached their expected final worker receipts but
failed an incorrect contiguous-raw-nonce wait. Standard-console first-session
tool/dialog/save completed, while reopen waited for a clipped history label.
The source corrects those two observation assumptions without weakening native
cell, cursor, durable context, raw drain, or process-join requirements. Fresh
execution remains required. The genuine native EOF scenario instead exited with
`0xC000013A` after physical input closure and produced no final command or
restoration receipt. That is an unresolved native EOF lifecycle failure; it is
not a passing platform deviation and is not replaced with Ctrl+D.

The subsequent parent run reached eight passing workflow cases out of nine.
The event-type probe and source classification established that the ninth was
console close, not read EOF. The renamed close-scope candidate awaits its own
fresh execution and independent acceptance; the earlier failure is not
retroactively counted as passing.

The focused workflow requires actual execution, physical cleanup receipts, and
independent review of one immutable snapshot. Genuine EOF, real standard-handle
acquisition, shrink restoration, arbitrary viewport origins, prior VT protocol
state, sustained streaming, and native backpressure must retain failures as
blockers when observed.

Full pinned grapheme/property/RGI terminal width, maintained .NET renderer
candidate measurement and ADR selection, all requested OS/PTY matrices, plugin
and extension conformance, main-screen behavior, and the complete P5-07/08/09
and broader phase gates remain mandatory. The researched Terminal.Gui candidate
is not tested, rejected, selected, or measured by this tranche. A passing bounded
Windows preview lane does not close those gates.
