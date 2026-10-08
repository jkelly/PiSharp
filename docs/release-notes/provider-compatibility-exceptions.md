# Provider compatibility policy note

Three intentional native Anthropic streaming differences are approved:

- Robust handling of split CRLF input.
- Discarding data arriving after cancellation, with actual operations and cleanup still joined.
- Sanitized error messages instead of raw upstream diagnostics.

The supported claim is compatible behavior with these stated exceptions. Exact equivalence and complete provider or port coverage are not claimed. The [compatibility record](../compatibility/approved-provider-differences.md) defines the narrow scope and how comparisons must retain the differing observations.

This is a documentation-only policy update. It changes no production or test code and supplies no new execution evidence. Payload callback qualification/integration and public snapshot review remain required; failed tests and unrelated missing functionality are not waived. The later payload callback composition has accepted bounded runtime results, summarized below; this policy note itself supplies no execution evidence.

## Later bounded execution

See [provider source-projection evidence](../verification/provider-source-projection.md) for the later candidate-specific observation and acceptance status. Historical source-only statements above describe their original freeze, not a claim about every later composition.
