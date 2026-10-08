# Joined Completions failure text

This refines the failure text boundary described in
[Completions public observation](completions-public-observation.md). The source
observation and canonical result retain separate ownership and settlement.
Explicitly admitted public error text can now appear in the joined canonical
message while native failure classification remains intact.

## Admitted semantic failure

`CompletionsRun` projects error text only after its actual source result,
cleanup and bounded drain have joined. The source result must have completed
successfully, its existing semantic terminal must already be an Error, and the
producer must have retained an explicit `CompletionsPublicFailure`.

The projection replaces only the message's `errorMessage`. It preserves the
terminal reason, typed native primary/cleanup diagnostics, other message data, source
observation snapshots and native `ChatFailure` classification. It creates a
new immutable terminal record and uses that same message for canonical delivery
and completion. A source failure never gains success or tool/durable authority.

The admitted message is rechecked against the run's existing source value
character, UTF-8 byte and serialization limits. Failed source admission or a
projection that exceeds those limits retains the original sanitized canonical
failure. Arbitrary exception messages do not qualify. No configured limit or
retained publication ledger bound increases.

A physical cleanup fault can turn semantic Done into a canonical Error. That
cleanup-created error keeps its existing native sanitized text and failure
marker, including when the cleanup exception itself carries admitted public
failure data. Cleanup failure diagnostics and completion barriers remain
unchanged.

## Matched aborted observation

`ChatRun` retains an actual yielded Completions Aborted observation before its
existing cancellation guard. The existing admission verifies the invocation's
API, provider, model, timestamp, error shape, ownership and bounds.

After the producer and any detached drain have joined, the resulting Aborted
terminal may use the fixed literal `Request was aborted` only if that admitted
observation contains that exact public literal. Missing text, arbitrary text,
foreign identity, another API, malformed presence data and oversized
observations cannot supply canonical error text. The code does not forward an
observed string or read a later final result to manufacture one.

The terminal reason, cancellation guard, native failure classification and
diagnostic failure message remain unchanged. The immutable terminal and
completion expose the same canonical message. An accepted source drain
observation remains the actual owned observation, without rewriting its data.

## Validation and remaining gate

The regression additions cover admitted and private acquisition failures,
admitted and private publication observer faults, held physical cleanup,
inclusive 8,192-byte public failure admission, failed configured source
admission, cleanup-created failure and matched/absent/arbitrary/foreign/other-API
abort text. Abort controls also cover a UTF-8 byte overflow whose character
count is below the existing diagnostic ceiling.

The sealed parent candidate had 667 passing native cases and 14 failing strict
lifecycle cases with 383 observations (178 canonical, 205 public). This follow-on
requires a new complete native gate and all 14 unchanged strict cases. It
targets four explicitly admitted final error-text differences and the matched
abort final text; their correction must be established by actual new receipts.

Native failure marker differences and canonical fixtures that supply authored
text as ordinary exceptions remain mandatory corrections. Constructor/live
alias timing, immutable Start views, joined settlement, return ABI, raw
signed-zero JSON tokens, request headers, reader DTOs and enqueue/read
measurement gaps also remain mandatory. No comparison, fixture, golden,
assertion, deadline or cap is changed by this follow-on, and no observation is
excluded from the strict gate.

The complete source golden remains 723,445 bytes with SHA-256
`829ec6d61f05e0064b6ca38799e264b6b4a6f7a2029d4f499e81d84b5475b81b`.
This bounded correction does not close the eight-phase/all-79-package gate.
