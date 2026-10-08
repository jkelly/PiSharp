# Completions public observation and canonical ownership

The public Completions run has one producer, one event consumer and one live
`CompletionsSourceMessage`. A source event retains that message handle and an
owned immutable `Emission`. Reading `Snapshot` observes the handle's current
revision at that actual read. Holding a Start event until later can therefore
show final content or an admitted failure; its emission stays the original
pending Start. Immutable canonical events remain independent of that alias.

## Actual consumer handoff

Public mode permits synchronous channel continuations. A waiting source reader
clears its read/next admission and releases its lock before settling the actual
consumer promise. Its continuation can observe the current revision and issue
another next or return without a stale admission flag. Canonical mode keeps its
existing per-frame advance acknowledgement and asynchronous channel
continuations. No public publication waits for the consumer's next request.

This is a native waiting-consumer contract. It does not reproduce every
JavaScript microtask schedule. In particular, a fully synchronous authored HTTP
invocation can progress inside the run constructor before the caller can
register a consumer. That first-invocation live-alias difference remains open.
No delayed callback, `Task.Yield`, retained-final substitution or artificial
alias snapshot is used to conceal it.

Returning the public consumer transfers its bounded queue to the one owned
discard drain. It leaves the producer running. An unread or returned consumer
can obtain `SourceResult` when the bounded producer reaches its semantic
terminal; an unread full queue still requires consumption or return. Canceling
the reader, canceling the run or disposing joins the actual retained work.

## Publication witnesses

`OnSourcePublished` is an optional invocation-scoped synchronous observer. It is
called at the actual source publication after snapshot admission and before
event delivery, including a terminal publication. It runs outside run locks.
Each witness contains a run-local sequence starting at one, a process-monotonic
`Stopwatch` timestamp and the actual owned immutable emission. Clock frequency
is `CompletionsSourcePublication.TimestampFrequency`; ticks are neither a wall
clock nor evidence of JavaScript scheduling equivalence.

Public `SourcePublications` and `SourceEmissions` retain the same emission
objects. The existing 4,096-emission and 4,194,304-character aggregate limits
apply to this shared retention. Every emission also retains its configured
character/UTF-8 byte bounds. Witness metadata does not create another payload
copy or an unbounded queue. Canonical mode does not retain a publication ledger.

The observer can run before the run handle is returned. Producer callbacks
cannot join their own disposal. If the observer throws, its subsequent
notifications are disabled before the producer reports its sanitized failure;
it cannot recursively observe that failure. The owned source still closes and
the canonical completion still joins cleanup. Arbitrary exception messages are
never public failure data. An explicit bounded `CompletionsPublicFailure` may
supply public text.

The lifecycle differential records public `emit:*` milestones through this
actual callback. It does not reconstruct their timing from final retention.
All original cases, assertions, deadlines, caps and source hashes stay intact.

## Safe source failure observations

When source snapshot capture is already enabled, a real canonical terminal
carries a separate owned source observation. Its error text comes only from
explicitly admitted bounded public failure data or a fixed native public
literal, including `Request was aborted`. The public projection omits the
internally originated native classification. The default producer now keeps it
in [typed native diagnostics](native-chat-diagnostics.md), outside Canonical
`Message`. Fine/coarse classification, stop reason and immutable event authority
retain their existing policy.
Ordinary compact mode with capture disabled gains no full terminal observation.

`ChatRun` may synthesize its compact cancellation terminal after its existing
work-cancellation guard rejects an actual yielded provider Aborted terminal.
For Completions only, it retains that terminal's actual owned source observation
before the guard and attaches it to the resulting compact Aborted terminal's
`SourceDrainSnapshot`. Admission checks the actual terminal and observed
message's API, provider, model, timestamp and aborted error shape, and adds an
8,388,608-character/UTF-8-byte ceiling. This additional ceiling never increases
the producer's configured bound. Missing, malformed, oversized and foreign
observations stay absent. No observation is recovered from a later completion,
another invocation or a successful terminal.

The compact cancellation terminal and result retain their native message and
remain consistent. The diagnostic observation grants no authority to execute
tools, write durable state or continue a session.

## Remaining compatibility gate

The sealed base lifecycle triage contains 420 observations (201 canonical and
219 public), across 14 failing strict cases; it does not represent 420 distinct
bugs. This candidate must be qualified with the complete unchanged strict
suite. The waiting-reader change does not close the constructor timing family
B, or justify rewriting canonical Start for B2. Safe terminal observations do
not replace the native final-result failure schema. Joined canonical settlement
at held cancellation intentionally remains distinct from the semantic source
result (D). Canonical return ABI (E), raw signed-zero JSON token (F), request
headers (H), reader DTO comparison (I), enqueue/read measurement (J), and all
uncovered comparator fields remain mandatory work. Real G publication witnesses
must be assessed from their actual receipts, including any residual difference.

The complete 723,445-byte source golden is unchanged:
`829ec6d61f05e0064b6ca38799e264b6b4a6f7a2029d4f499e81d84b5475b81b`.
This contract and bounded candidate do not establish full provider acceptance,
the full eight-phase/all-79-package gate, or a parity waiver.
