# Approved provider compatibility differences

The compatibility target is compatible behavior with the three explicit exceptions below. These are approved behavior choices for the native Anthropic streaming path relative to the pinned Pi v0.99.1 reference. They are not a claim of exact upstream equivalence or complete provider/port coverage.

| Area | Approved native behavior | Comparison consequence |
| --- | --- | --- |
| Split CRLF input | Correctly handle a CRLF delimiter split across input chunks. Do not reproduce the reference parser failure for that boundary. | Preserve the recorded reference failure and native result as different outcomes; classify this specific framing difference as approved. |
| Data arriving after cancellation | Discard data that arrives after cancellation while retaining cancellation checks and awaiting the actual in-flight operations and cleanup. | Late content, events and dependent usage can differ where the reference consumes that data before settling cancellation. This does not permit abandoning tasks or owners. |
| Error diagnostics | Keep user-facing error messages sanitized instead of copying raw upstream provider, parser or cleanup diagnostics. | Message text can differ. Original fault identity and task/cleanup evidence must still be retained in their appropriate private diagnostic channels. |

Approval applies only to these three behavior differences and their demonstrated direct consequences. Comparisons must continue to report the underlying observations and mismatches. An approved exception must not be recorded as an exact match or used to discard inconvenient evidence.

This decision does not waive missing functionality, failed tests, payload callback qualification or integration, public snapshot review, or the requirements for any whole-provider or whole-port completion claim. Numerical cost projection, wire shapes, callback ordering, ownership, source snapshots and other differences require their own evidence. The decision does not approve additional exceptions for mutable alias semantics or unrelated input handling.

The current payload callback composition has accepted bounded runtime results, summarized below; broader integration and release claims remain separate. Documentation of these decisions does not promote source review to runtime acceptance or qualify a release. See the [release note](../release-notes/provider-compatibility-exceptions.md) and the [Anthropic stream contract](../contracts/anthropic-messages.md).

## Later bounded execution

See [provider source-projection evidence](../verification/provider-source-projection.md) for the later candidate-specific observation and acceptance status. Historical source-only statements above describe their original freeze, not a claim about every later composition.
