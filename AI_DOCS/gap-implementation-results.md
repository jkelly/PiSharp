# Gap implementation and local acceptance

The implementation is in `E:/AI/PiSharp/gap-implementation`, branch `impl/upstream-gap-closure`, based on `14dd34b369fba6d236684da6dfc8a39a03263321`. The work is not published. The reference is Pi 1.5.0 at `f9a26365568127461c47a07a37dcb42c140a3ac5`, with the individually reviewed changes through `7d073328e44337d3292a7db9b0f0308f679c5e97`.

The full authorized plan has source implementations: all 23 corrections, the nine capability groups, Chord and its separate legacy runtime, hosting, pi-env, Anthropic message threads, schema/store captures, evaluation tooling, package preparation, and regression workflows. Required final local gates passed. [Current mappings](../compatibility/gap-status-2026-11-08.json) identify source and test paths; [release notes](../docs/release-notes/gap-closure.md) describe the behavior. Historical captures, source pins, and historical acceptance claims remain separate.

## Observed Windows validation

These are selected suites and case groups, not a sum of distinct assertions. The [validation ledger](validation-status.json), [source qualifications](validation-source-qualifications.json), and individual reports retain failures, reruns, test arguments, source fingerprints, and logs.

| Scope | Result | Evidence |
|---|---|---|
| Full Chord | 875/875 passed; stable final source | `artifacts/gap-validation/Chord-full-final-4a8a84d4f26e4e3ca1465d9ee204b8ac` |
| Legacy cluster/manager | 25/25 passed; stable source | `artifacts/gap-validation/LG3-cluster-stable9-f9c36549f8cf4821ad5169b8602a0fb4` |
| Hosting, real plugins, HTTP, workers | 14/14 passed | `artifacts/gap-validation/N1-hosting-final-d2cadb7125d44a72a940241579dfc5b6`; concurrent changes were outside the hosting/process implementation |
| Full RPC | 137/137 passed | latest `RPC-full-stable9` receipt in the ledger |
| Schema | 34/34 passed; stable final source | `artifacts/gap-validation/Schema-r6-19e8e3d7201c49369686f0b412de2414` |
| Model catalogue/configuration | 113/113 passed; stable source | [I06](agent-reports/I06.md) |
| SQLite storage and bulk operations | 163/163 passed; stable source | [I12](agent-reports/I12.md), [I18](agent-reports/I18.md) |
| Delta tracker | 137/137 passed; stable source | [DLT](agent-reports/DLT.md) |
| Real Chord/Durable integration | 7/7 passed; validation-helper-only source drift | [X1](agent-reports/X1.md) |
| Windows NativeAOT distribution | 9/9 standalone smoke checks passed on final runtime sources; only RID package-lock changes during publication | `artifacts/gap-validation/NativeAOT-release-final-d7bc09f1dd10420cb432aae5324ab090`, `artifacts/env-native-local-validation-r8/evidence/manifest.json` |
| Anthropic threads and prepared requests | 4/4 reported groups passed, including UTF-16 hook/replay and invalid-payload controls | `artifacts/gap-validation/Threads-r6-b72ba3d2d7324ab185d50fc6467fa12a` |
| Anthropic Simple | 13/13 passed | `artifacts/gap-validation/AnthropicSimple-r6-6c186ceda50e4160aa1008ea9bed222f` |
| Node overlay regressions | 5/5 passed | `artifacts/gap-validation/Overlay-final-79ef8611b5cb4627b90ef40aee68a4ae` |
| Offline provider differential | 30 cases accepted: 29 source matches plus one explicit native-policy difference | `artifacts/gap-validation/ProviderOffline-r6-54cfdf3d20f241ddae5408d2350a6f79` |
| Provider compatibility and transport | 236/236 compatibility and 245/245 transport checks passed | `artifacts/gap-validation/Compatibility-r6-4e12dcb4d07b40b7b95c727624ffea39`, `artifacts/gap-validation/Transport-r6-2ea9d6722493412891f8941e5dda7b78` |
| Azure and provider APIs | 17/17 and 60/60 passed | `Azure-r6`, `ProviderApis-r6` receipts in the validation ledger |
| Normal Release packages and external consumer | 29 packages built/validated; fresh external consumer passed with stable final source | `artifacts/package-consumer-final-887432f2333b42819b937a8359517be7/receipt.json` |
| Unpackaged applications | WebAgent and evaluation tool normal Release builds passed with zero warnings/errors | `WebAgent-normal-Release`, `Evals-normal-Release` receipts |
| Fresh production Node runtime installation | 1 required case passed, no skips; 123 packages installed into a fresh private cache | `artifacts/gap-validation/runtime-installer-local-333e21e351be480abb18775e1299e916`; installer/manifest unchanged during concurrent provider edits |
| Mapping/provenance/manifest controls | 10/10 passed, no skips | `artifacts/gap-validation/root-final-structure.log` |
| CI runner controls | 28/28 passed, including exact platform-skip accounting | [CI2](agent-reports/CI2.md) |

Additional targeted provider, MCP, tool, lifecycle, Env, identity, and CLI results are recorded in the ledger and their individual reports. Most local runtime suites disable analyzers to isolate execution; the package build, standalone application builds, and native publication provide separate normal Release/analyzer checks. The generated ICU data has only two `GeneratedCode` annotations; [inverse-byte verification](agent-reports/tui-generated-data-analyzers.md) confirms no data change.

The final native executable is 5,773,312 bytes, SHA-256 `25c85ef42b715051196bd90780b2b52ea197f615facb61802a91ef4380190c87`. Its publication changed ten RID-dependent lockfiles and no C# sources; the source qualification records those differences. The subsequent managed restore and final package consumer used stable source fingerprints. Package admission remains distinct from publication: `packageConsumptionProven=true`, `releaseAccepted=false`.

The offline provider differential retains one explicit native-policy difference: Mistral callback failures use the existing bounded native diagnostic. Its original upstream expected value and `Matched=false` remain visible; exact native output is checked separately. This does not count as upstream parity. See [the policy evidence](agent-reports/ProviderOffline-native-policy.md).

SQLite measurements demonstrate fewer executor calls (770 to 5 for 256 batches). Their elapsed-time comparison includes an artificial per-statement delay and is not a raw SQLite throughput claim. See [measurement evidence](validation-measurements.json).

## Qualification boundaries

Local execution is Windows x64. Linux/macOS CI, Unix PTY/native process behavior, physical Apple Terminal modifiers, real SSH hosts, Android/other-RID artifacts, and live provider accounts remain separate environment checks. Windows PTY creation deliberately returns `ENOTSUP`. The workflows are implemented but have not been run on hosted CI in this worktree.

Native C# plugins and framed process transports retain the documented language/runtime differences from arbitrary TypeScript/npm plugins, Node IPC, and browser builds. Cloudflare and browser-only adapters remain explicit platform exclusions. No package, release, or deployment was published.

## Main push preparation

The implementation was copied into the clean integration worktree on `codex/close-upstream-gaps-main`, based on the same `14dd34b3` as `origin/main`. A fresh Git checkout canonicalized line endings in 268 files, with no other source-content differences. The 58 affected current-byte integrity records were refreshed; historical hashes and capture pins were preserved.

The canonical checkout passed all 236 compatibility checks, all 245 transport checks, and all ten mapping/provenance/manifest controls. These additional receipts are under the integration worktree's `artifacts/gap-validation/push-checkout-*` and `artifacts/push-validation`. The earlier native and package receipts above retain their original implementation-worktree fingerprints.
