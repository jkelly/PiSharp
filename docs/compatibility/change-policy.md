# Baseline and expected-output changes

Keep the upstream target at its immutable SHA. Changes to `baseline.lock.json`, inventory classifications, fixture inputs, expected outputs or provenance are explicit reviewable changes; do not update them to make a failing comparison pass.

Record the old/new source or artifact hashes, the reason, exact capture command, runtime/platform and affected requirement IDs. Expected upstream outputs require an unmodified supported reference seam. New authored expectations retain their authored provenance and cannot be relabeled as upstream captures. Store actual outputs separately under ignored `artifacts/` and review mismatches before considering an expectation change.

Run source/fixture validation, repeat reference captures in clean isolated homes, and deliberate comparator mutations. Preserve raw arrays, strings, missing/null values, usage and opaque fields. Any future volatile-field allowlist or stream-equivalence profile needs a separately named version and tests that prove forbidden changes remain visible.

Only source/tool authors can propose evidence changes; release/scope reviewers approve parity exceptions and gate closure. Current broad inventory rows remain Deferred until individually enumerated and qualified. A mandatory Deferred row blocks a full native-v1 claim. No current reference or native synthetic result closes Phase 1 or Phase 2.
