# Validation coordinator: final handoff

Local validation is complete; no queue or product process remains active. Exact final results are in `validation-final-acceptance.md` and `.json`. Full historical receipts and per-case results remain in `validation-status.json` and ignored `artifacts/gap-validation` evidence. No historical IMPLEMENTATION_STATUS claim was used as current acceptance.

## Ownership and process control

One file lease serialized every shared build and test. The helper used SDK10.0.401 at the exact local path and an isolated NuGet cache. Processes started suspended, entered an owned kill-on-close Windows job before resume, inherited only their I/O handles, and were terminated/joined with bounded whole-job accounting. No generic SDK/user process was killed. The initial recovery canceled owners' obsolete queued sessions; one lost owned helper was identified from its exact task command/start time and terminated. Early no-execution lock timeouts and interrupted receipts remain infrastructure history, not product test failures.

Every receipt records SDK/platform, arguments/filters, raw stdout/stderr and dirty-worktree source manifests before/after execution. Test success requires positive actual selected cases. Nineteen controls cover JSON, strict NDJSON, stderr failures, diagnostic probes, numeric summaries, zero selections and nested skips. Atomic status writes cannot abort a queue merely because a reader briefly holds the old JSON file.

## Build policy

Runtime-only builds explicitly used RunAnalyzers=false after diagnosing an unchanged3.7MB TUI data source. Exactly two generated-data attributes fixed that baseline hotspot; inverse hashing proved original data/algorithm bytes unchanged. The subsequent normal Release rebuild passed in6.17s with analyzers enabled. Final29-package Release build, WebAgent/Evals Release builds and NativeAOT retained normal analyzers; no product analyzer policy was disabled.

## Final outcomes

- Full current Compatibility236, Transport245, Schema34, Chord875, Threads4 observed groups, AnthropicSimple13, Azure17 and Provider APIs60 passed. R6's one console group includes18 exact placement assertions.
- Legacy cluster25, hosting14, RPC137, forwarding2, original compaction/model mapping16 and overlay5 passed. Hosting's own files were unchanged; its unrelated Anthropic/lock deltas are recorded.
- Windows NativeAOT publish, signed canonical dependency notices and all9 isolated protocol checks passed. Final binarySHA256 is25c85ef42b715051196bd90780b2b52ea197f615facb61802a91ef4380190c87. Its ten before/after changes were RID-dependent lock files only; no code changed.
- Final normal Release built and validated29 packages. The fresh external consumer admitted5 actual nupkgs, required both experimental diagnostics, compiled with opt-in and ran real protocol/environment/hosting/byte-schema/SQLite/public-signature checks. Exact source hash1b14874943524f66b87ece8e0cfaa79e7f59b520dfaafc4b2632fd9f9d75e05c remained unchanged.
- The exact production installer case passed1/1, zero skips, in a fresh private cache; it installed123 public packages with registry integrity and provenance verification. This is separate from fixture ABI testing.
- Offline provider contracts passed30 with29 source-derived matches and1 explicit native-policy difference. Original source mismatch/expected values remain visible; no upstream execution is invented.
- I18 actual SQLite sources passed163. `validation-measurements.json` separately validates770/5 calls and12040/78ms for256 batches with injected1ms per executor call, excluding BEGIN/COMMIT. These are not raw SQLite throughput figures and create no synthetic test cases.

`validation-source-qualifications.json` records exact lock-only and unrelated-source deltas, plus inverse-byte proof for the post-Chord Hosting friend declaration. Native/managed RID restores legitimately change lock files; the final managed restore preceded packing so the strict external-consumer source fingerprint stayed stable.

## Structural and platform boundaries

The bounded hosted runner passed28 offline controls; manifests passed3 tests. Root's final10 mapping/provenance/manifest controls passed with zero skips and whitespace checks passed. Final current derivative refresh changed0 records; historical pins remained untouched. Required release jobs all receive the release SHA. Dedicated OS policies preserve actual case identities: Windows daemon10=9PASS+1SKIP; legacy Unix25=24PASS+1SKIP, with markers stored as observations rather than invented extra cases.

The full Tools wrapper run passed164 groups, but its Windows symlink branch emitted IOException and remains unqualified. Core hosted manifests reject that observation. Hosted three-OS execution, Unix native PTY behavior, physical macOS modifier input, separate reviewed native source admission and publication were not performed or inferred. Counts overlap and are not summed into a distinct-test total. No required local gate remains failed, and no publication occurred.
