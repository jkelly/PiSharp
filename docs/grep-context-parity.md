# Positive grep context in the admitted Tools profile

Baseline: Pi v0.99.1, `d86654abb8862e201933517d6f1fce9f88dd117f`,
`packages/coding-agent/src/core/tools/grep.ts` (SHA-256
`88be3d00217d1a1caf8a9e7bb2b8d2e6d96edfa4b790c2cf76c746ab36b8841b`).
This slice starts directly from coordinator candidate
`01fd0178e8d7e0f40ee5d037d30c894331c926b4`, tree
`1c3eb5bf8191cd79c6ef674eb23a451fb0bc6b87`, from the R152/R155 handoffs.

## Host contract

`IGrepContextReader.ReadAsync(string absolutePath, int maximumBytes,
CancellationToken cancellationToken)` returns UTF-8 bytes and directly joins
its original I/O and cleanup, including cancellation. The host borrows this
capability to `GrepTool` as optional fifth constructor argument `contextReader`;
the host retains its lifetime and disposal ownership. No local reader is
constructed automatically. Existing constructors/call sites without this
capability keep zero-context operation; positive context fails before search.
An owning host must explicitly supply an admitted bounded reader before it
advertises positive-context availability. CLI/catalog activation is outside
this slice and remains with its owner.

The exact final policy-authorized action carries `context`. Every match,
including undisplayed matches, passes lexical and canonical target containment
and existing receipt bounds before any context read. Canonical containment is
checked again immediately before each first read. No change is made to binary
admission, process arguments, spills, native cleanup, or executable acquisition.
There is no atomic file identity/snapshot guarantee between the completed
search and context reads; this retains the existing path-based containment
profile, rather than promising protection against concurrent filesystem swaps.

## Original behavior and explicit bounds

For positive context, each selected match produces its own block: match lines
use `path:line: text`, neighbors use `path-line- text`. Overlapping blocks and
duplicate matches are repeated in original match order. File reads are cached
by absolute path only within one invocation. CRLF and bare CR become LF;
trailing LF retains its empty split line. Context text comes from the reader,
while zero context continues to use the match receipt and performs no reads.
Ordinary I/O/read denial produces Pi's `(unable to read file)` marker and is
cached. Cancellation, malformed UTF-8/NUL, resource failures and containment
errors are not hidden as read-denial markers. Unrelated exceptions remain
execution failures through the existing invoker.

The admitted profile accepts integer context 0-100, reads at most 256 KiB/file,
caches at most 1 MiB and 128 paths, and admits at most 100000 split lines/file.
The remaining aggregate byte budget is passed to each read; trusted readers
must enforce it while acquiring data. Returned length is checked defensively.
Formatting admits at most 8 MiB before reporting a resource error. These are
explicit profile limits beyond the upstream unbounded read operation; they do
not claim full upstream arbitrary-size/fractional-context equivalence.

Line text keeps the existing 500 UTF-16-code-unit truncation behavior. Only the
whole-line 50 KiB head is retained in memory, with full bounded formatted
line/UTF-8-byte counts for truncation metadata. Match-limit, byte-limit and
line-truncation notices retain the upstream ordering. Formatting directly
awaits the borrowed read; no cancellation race, detached task, timeout wrapper,
or early output completion abandons the original read.

## Authored regression inventory; not executed here

Seven groups added to `tests/PiSharp.Tools.Tests/GrepToolTests.cs`, registered in
its existing `Cases()` inventory and therefore in the existing Tools runner:

| Exact method / report identity | Coverage |
| --- | --- |
| `tools.ContextFormatting` | Repeated overlapping blocks, multiple paths, UTF-8/CRLF/bare CR, source rather than receipt text, per-invocation one-read cache, file target, boundaries/trailing LF, zero/no-match no-read |
| `tools.ContextReadFailures` | Cached IOException/denial markers, explicit resource failure, readable empty file, int.MaxValue line arithmetic |
| `tools.ContextTruncation` | Only displayed matches read, 500-character context lines, match-limit notice order, Unicode 50 KiB whole-line head, exact totals compared with existing pure truncator |
| `tools.ContextPolicy` | Final-action denial before effects, absent borrowed capability, transformed capability denial and authorized transformed context |
| `tools.ContextContainment` | Undisplayed escaping receipt prevents every read; canonical escape between receipt check and read prevents acquisition |
| `tools.ContextBounds` | Negative/fractional/101 context, malformed UTF-8/NUL, per-file byte and line ceiling, aggregate byte/file cache limits before extra read, formatted-output ceiling |
| `tools.ContextCancellation` | Held original read after original process/spill join; canceled caller cannot finish until read cleanup joins; both successful and canceled read completion |

The original eleven grep groups remain registered (18 grep groups total).
Coordinator can select the seven new groups with the existing runner filter
`--filter "grep positive context"`, then all grep with `--filter "grep"` and
the affected full Tools suite under its separately frozen build allocation.
Held cancellation groups stay within `GrepToolTests`, whose runner directly
awaits them and does not attach its ordinary 20-second timeout wrapper.
No builds, test groups, rg/fd processes, or upstream compiler/oracle were run
by this author. Source review and formal execution remain coordinator-owned.
