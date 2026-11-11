# Canonical unreleased changelog repair

The new candidate had been added directly to root `CHANGELOG.md`, while `ChangelogSyncCases.Generate` treats `website/src/content/changelog/*.md` as canonical. That caused the exact generated-content assertion to fail.

Added `website/src/content/changelog/1.5.2.md` with `version: 1.5.2`, `kind: sync`, `baseline: 1.5.2`, `status: in-progress`, an `Unreleased` label and no date. The body explicitly begins **UNRELEASED** and links to the candidate notes and applicability map. This follows the existing collection schema and keeps the candidate out of the existing RSS filter, which includes only `status: released`.

Regenerated root `CHANGELOG.md` using the unchanged C# generator contract: numeric version sort, optional date, trimmed body, site-relative Markdown links converted to absolute URLs. The correct header for this undated candidate is `## [1.5.2]`; the body carries the explicit unreleased label.

The previous 1.5.0.1-and-earlier tail was compared against the generated output and is unchanged after the generator's existing LF normalization. No test assertion, generator code, historical website entry or public release ledger was changed. A Python reconstruction of the exact generator contract and scoped whitespace validation passed; no .NET build/test was run.

Root and validation were notified that `CHANGELOG.md` is a changed runtime input and must be captured by the final source cohort. Its new candidate wording defers validation status to the separate notes/map, so final source metadata acceptance need not rewrite this runtime input again.
