# PUB4: authenticated draft lookup correction

Observed publication API behavior: the authenticated by-tag endpoint returned HTTP 404 for `v1.5.0.1` while the release-list endpoint exposed its prepared draft (ID `438703276`). The original 14 offline controls incorrectly modeled that draft as visible by tag. Root handled the running immutable publication separately; this patch corrects the helper for subsequent runs.

Owned changes are limited to `tools/release/publish-github-release.mjs`, its `.test.mjs`, and this report.

The helper now checks authenticated `GET releases?per_page=100&page=N` after by-tag absence or a by-tag draft. It validates the array and every release's tag, positive safe-integer ID, and boolean draft state; follows at most 20 pages; and refuses incomplete pagination, duplicate IDs, ambiguous matching tags, or disagreement between the two lookups. A published matching release still refuses before any mutation. A genuine absence permits creation only after the listing terminates. A matching draft uploads all supplied validated packages, verifies the source tag again, rechecks the same release by ID and exact tag, then promotes it. Upload or recheck failure leaves promotion unattempted.

Existing tag-push, exact-source-SHA, package validation, authentication/network/HTTP/JSON strictness, and structured-argv guards remain in place. The helper creates no Git tag. Workflow gates and publication conditions were not edited.

Validation: `node --test tools/release/publish-github-release.test.mjs` — **22 tests passed, 0 failed, 0 skipped**. The original 14 controls remain, adapted for listing and ID lookup. Eight additions cover the observed by-tag-404/listed-draft path, a second-page draft, listing auth/network/JSON/HTTP failures, malformed listing entries, ambiguity and lookup disagreement, published releases found through listings, pagination exhaustion, and failed or changed by-ID rechecks. Recorded fake commands verify upload-before-promotion and no creation when a draft exists.

No .NET commands, Git operations, or remote mutations were performed. Source is ready for root's independent offline controls and review; the immutable release tag was not changed.
