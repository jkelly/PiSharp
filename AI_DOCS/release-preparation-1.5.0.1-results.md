# 1.5.0.1 release preparation — source checkpoint

The website and unpublished 1.5.0.1 candidate are prepared on the unchanged Pi v1.5.0 baseline. The complete published 1.5.0 metadata record is preserved. No version tag, NuGet publication or published GitHub release is part of this task. The immutable commit containing this checkpoint is the input to candidate artifact admission.

## Website

The existing Astro/Starlight design is retained. New guides cover Chord/hosting/legacy, Durable.Store, remote environments and the native daemon, Anthropic threads, evaluations and the complete gap update. SDK/parity/roadmap/reference pages and the candidate changelog are updated. The package inventory contains 28 libraries plus the CLI; five new libraries remain explicitly unpublished with source links. Stable installation labels remain 1.5.0 and the in-progress candidate is excluded from RSS.

Clean npm installation, the 60-page build, 4,383 internal links/fragments, 27 new-guide repository links and desktop/mobile rendering checks passed. Generated CHANGELOG synchronization passed in InteractiveParity. See [WEB1](agent-reports/WEB1.md) and [WEB2](agent-reports/WEB2.md).

## Release corrections

Actual hosted failures at 3024b1e0 were classified from retained artifacts before changing assertions. Product corrections cover loader primary-error classification, Unix mutex/routing/private pipe paths, build-derived daemon version identity, Responses/Codex incremental strict declarations and strict UTF-8 preservation, and the reproduced Node compatibility reload cache defect. Runner corrections retain strict observations while admitting explicitly defined statuses, provisioning rg/fd, passing the selected SDK and verifying the exact historical archive bytes.

Fixture corrections preserve replay/rollback/ownership assertions while accounting for reviewed model catalogs, explicit Anthropic continuation ancestors, persisted thinking checkpoints, current independent bounds and decoded JSON. A late-start fixture now retains its selected runtime rather than silently installing another. Test child logging suppresses only Windows Event Log writes, retaining console warnings, and uses the selected SDK host. Production logging is unchanged.

## Local Windows integration

These are runtime checks with analyzers disabled, not hosted/all-platform or NativeAOT qualification. Console and JSON platform skips remain visible in receipts; selected counts are not all passed counts. Full output and original failed receipts are retained under artifacts/gap-validation, with a compact index in artifacts/release-prep/local/summary.json. The complete earlier implementation ledger is preserved byte-for-byte in [the 3024b1e0 snapshot](validation-status-implementation-3024b1e0.json); the current worktree ledger links to it rather than replacing that evidence.

| Check | Recorded outcome |
| --- | --- |
| Compatibility / physical source integrity | 236 passed; all 2,670 current-byte rows clean |
| CliSync / Models / ModelRuntime | 68 / 113 / 14 passed |
| Chord / legacy cluster / native hosting | 875 / 25 / 14 passed on Windows |
| Tools | 164 selected; passed with the explicit unavailable symlink observation |
| Managed daemon / terminal | 10 selected (9 passed, 1 Unix skip) / 7 passed |
| Extension contracts | 386 passed after the final cancellation-evidence fixture correction |
| MCP parity / Codemode | 47 / 142 passed |
| Interactive parity | 723 selected: 717 passed, 6 explicit platform skips; changelog checks passed |
| CLI parity | Full 220-case run had one stale lone-CR assertion; its corrected exact-output/CRLF case then passed focused. The failed full receipt remains failed. |
| Extension parity | Full 155-case run had two fixture failures and two explicit skips. Both corrected cases passed focused; typed/untyped declarations and late-start profile/no-install controls are retained. The full receipt is not relabeled. |
| WebAgent | The 543-case local run reached its 900-second owner deadline, so it is incomplete. A fresh build passed all 32 runs cases and all 3 idle-exit cases, including the changed strict/UTF-16/plain-zstd/malformed-byte paths. |
| Runner and release controls | 32 Python and 7 Node runner controls; 14 draft-publication controls passed |

Current-byte integrity refresh changed 11 reviewed records and its verifier seal. Historical source hashes, capture bytes and execution pins were not rewritten. CRLF-to-LF normalization follows repository attributes; physical-byte authorities retain their original endings.

## Final admission and publication boundary

[The plan](release-1.5.0.1-plan.md) keeps FINAL responsible for candidate-version package/consumer validation, native publication/smoke, deployment and exact-source hosted results. The candidate package gate must build all 29 archives with normal Release analyzers, verify their source/version/containment, and restore/run a fresh package-only consumer. Native smoke must verify the built 1.5.0.1 handshake and exact packaged binary/notices.

After the source commit is pushed, review Core, Durable, Gap regression and Native environment results at that exact SHA; the macOS suite separately exercises the release-list fixtures. A prior local pass or presence of a workflow is insufficient for publication. The final preparation artifact records immutable source SHA, package hashes, consumer/native receipts and actual hosted run IDs; until then artifact and platform qualification remain pending.

The draft-compatible release helper never creates a tag. A future authorized matching tag push must pass every existing publication dependency, upload validated assets and only then promote an existing draft. Manual workflow runs still publish nothing. Candidate flags remain false, and physical macOS input, real SSH/live providers and excluded platforms retain their separate qualification boundaries.

## Candidate lock metadata follow-up

A normal managed restore after NativeAOT qualification revealed project-reference ranges still recorded earlier versions, including 1.1.0.6 and 1.5.0: locked-mode local builds had retained them. The 29-package plus managed-daemon restore graph now records 1.5.0.1 project ranges before immutable artifact admission. Structural comparison verifies external dependency graphs and hashes are unchanged. Managed lock files are canonical LF, and only current-byte integrity records/seal are refreshed. This follow-up changes no C# or website source; candidate archives and final native manifests must name the follow-up commit. Native distribution checks for the preceding f473befb commit passed on all three hosted platforms, including all 14 Unix terminal cases; those receipts retain their original SHA.

## Hosted gap-fixture follow-up

Exact-source hosted artifacts exposed three additional test-ordering/platform defects, without identifying a new product-runtime failure. HOST2 now makes the PID service depend on the Bootstrap binding being live and the initializer awaits a retained Bootstrap reference; this proves native acquisition completed before shutdown while the initializer remains pending. Full Windows Hosting14 and five focused startup-close repeats passed. ENV4 obtains a Unix physical root independently with /bin/pwd -P, retains exact daemon realpath equality, and adds an ancestor-symlink control; the Windows daemon suite passes with its existing Unix-only observation, and actual macOS validation remains required. The legacy expiry test now records concurrent callbacks in a ConcurrentQueue, waits for callback receipts as well as dictionary detachment, and checks exact a/b/c multiplicity before and after disposing the pool; all25 legacy cases passed. See HOST2 and ENV4 worker reports.

The earlier f473befb source also completed all11 Core jobs successfully: CodingAgent906, Agent238, Transport245, Sessions138, Rpc137, Tools164, ExtensionHost25, Contracts386, Tui44 and Compatibility236, with the offline applicability job. All three native distribution jobs succeeded; their original SHA qualifications remain explicit. These are supporting results, not substitutes for the final candidate SHA.
