# Isolated public edit oracle setup

Root execution update, 1 October: reviewed candidate01f4e2f9ca0ade3f45a01feba7aecfa22ad2aae5 was prepared and restored successfully through the applicable executor auto-review. Thirteen exact packages and1,997 installed regular files match inspected canonical-lock archives. Five new anonymous official archives were acquired; eight existing verified archives were copied read-only. Scripts, native builds, bin links, global configuration and provider access remained disabled. Actual receipt: [edit-oracle-setup.json](../../compatibility/edit-oracle-setup.json), SHA-2562c1a92e8cc89f344aa397a12cd2708e2909792ad81ec402a3570b7863e110bea. Whole edit captures and independent qualification remain pending. The frozen plan/helper below describes the original pre-execution scope and was not rewritten.

This three-file candidate prepares a **new, task-local 13-package reference oracle** for unchanged public Pi edit exports. Only read-only preflight has run. Preparation, acquisition of five new archives, dependency restore, whole edit-module loading and genuine edit captures remain pending. This is a developer evidence prerequisite; it adds no dependency to the native CLI and closes no phase gate.

The [plan](../../compatibility/edit-oracle-plan.json) retains exact canonical upstream lock entries, source/runtime pins and the accepted eight-package session receipt. The [helper](../../tools/PiReferenceRunner/setup-edit-oracle.mjs) implements separate read-only check, local preparation and explicit reviewed restore modes. Existing source checkouts, dependencies, archives, caches, harnesses and goldens remain immutable.

## Source boundary and dependency authority

Public source: `https://github.com/earendil-works/pi`, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. Canonical `package-lock.json` SHA-256: `e245cabcefdd23d1ab3cfd21db492e21ca11b7ef4a20f6304aead4dc2b5b569d`. The plan pins 20 relevant files individually and requires the full 2,093-file canonical fingerprint `2d65bfaee0e2556cb82ae7e0425de68560ecc3be4451f69ceff3bcc9c6144aa3`.

`edit-diff.ts`, SHA-256 `f85a9809eb44b9828236050cf38a8e45933e5cfd583a186e9a0e55a664dd3e8d`, imports `diff` and the path utilities. The unchanged whole `edit.ts`, SHA-256 `31a368c14cf5437ecac157765660001b16063302d9bd746303d6d1b1dcb5625d`, also eagerly imports its renderer. That renderer imports the whole public TUI index and interactive theme, which require marked, east Asian width, chalk and syntax highlighting. Avoiding renderer callbacks does not erase their eager imports. Private-function extraction, copying source functions, dependency shims or replacing modules would change this boundary.

| Package | Exact canonical root-lock version | Declared license | Archive source |
| --- | --- | --- | --- |
| chalk | 6.0.0 | MIT | New exact official registry archive |
| diff | 8.0.4 | BSD-3-Clause | New exact official registry archive |
| get-east-asian-width | 1.6.0 | MIT | New exact official registry archive |
| highlight.js | 10.7.3 | BSD-3-Clause | New exact official registry archive |
| marked | 18.0.11 | MIT | New exact official registry archive |
| cross-spawn | 7.0.6 | MIT | Verified session archive copied read-only |
| isexe | 2.0.0 | ISC | Verified session archive copied read-only |
| partial-json | 0.1.7 | MIT | Verified session archive copied read-only |
| path-key | 3.1.1 | MIT | Verified session archive copied read-only |
| shebang-command | 2.0.0 | MIT | Verified session archive copied read-only |
| shebang-regex | 3.0.0 | MIT | Verified session archive copied read-only |
| typebox | 1.3.27 | MIT | Verified session archive copied read-only |
| which | 2.0.2 | ISC | Verified session archive copied read-only |

The five new packages have no dependencies or optional/peer dependencies in the canonical lock. The existing cross-spawn chain is cross-spawn to path-key, shebang-command and which; shebang-command to shebang-regex; which to isexe. There are 12 packages in the static required edit graph. The approved 13-package profile also retains partial-json from the qualified session profile; this does not claim edit imports it. Actual loaded closure must be measured during a later capture.

All five URLs are exact `https://registry.npmjs.org/<name>/-/<name>-<version>.tgz` URLs. Their complete SHA-512 SRI values and unchanged original lock objects appear in the plan. Marked and which declare command bins; bin links are disabled. The older development-only cross-spawn closure and provider SDKs are excluded. Source-lock license and absent lifecycle markers are declarations; the five new packaged manifests, script/native evidence and license texts have not been acquired or observed.

The helper hashes the accepted session restore receipt before parsing it: SHA-256 `3e43ae32f145a1cb62d277deda4659d91e78fd49bb0615f88b06fa9c8f2c7f61`, 15,667 bytes. Each of its eight archives must match SHA-512 SRI, recorded archive/manifest SHA-256 and every original installed file. These 1,431 installed files are reused as read-only evidence, never copied into the new installation or linked to it. Preparation copies the eight verified archives and an independently owned copy of the original cache. Original receipt/archive/installed bytes are rechecked after preparation and when verifying restore; original source and cache are checked separately.

## Confined setup and concrete commands

Approved new root: `P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-edit-oracle-v0.99.1`. It was absent at plan creation and at read-only preflight. The existing `Pi-session-context-oracle-v0.99.1` supplies source, eight archives and cache read-only. The older two- and three-package oracles remain untouched.

| Mode | Effects |
| --- | --- |
| Default or `--check` | Read-only source/runtime/npm/archive/installed verification; never creates, downloads or installs |
| `--prepare` | Fresh local source clone without hardlinks, owned home/temp/config/cache, exact root manifest/lock, confined npm unpack and eight copied archives; no network or dependency restore |
| `--restore` | Five exact anonymous registry archive reads and inspection, then reviewed task-local 13-package npm ci |

Root reviews and freezes the three files before invoking these concrete commands from the implementation repository:

```powershell
$EditNode = 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
& $EditNode tools/PiReferenceRunner/setup-edit-oracle.mjs --check
& $EditNode tools/PiReferenceRunner/setup-edit-oracle.mjs --prepare
# Root submits this exact command to applicable executor network auto-review:
& $EditNode tools/PiReferenceRunner/setup-edit-oracle.mjs --restore
```

Detached review checkouts use the explicit read-only path:

```powershell
& $EditNode tools/PiReferenceRunner/setup-edit-oracle.mjs --check --oracle 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-edit-oracle-v0.99.1'
```

An explicit oracle is permitted only with an explicit `--check`. It must equal the plan's canonical approved root. Mutation modes retain the approved task-2 sibling layout and reject overrides. Unknown, duplicate, incomplete and conflicting arguments fail before setup effects. Links/junctions in relevant path ancestry are rejected. Existing nonempty, incomplete, differently owned or changed targets are preserved and rejected; exclusive writes and receipts pin ownership, plan/helper/parser bytes. There is no deletion, reset or uncertain-state overwrite mode.

Before and after setup, the source clone must have clean HEAD at the pin and all 2,093 canonical Git blobs. On this Windows checkout, 2,091 files match raw Git bytes and exactly `pi-test.bat`/`pi-test.ps1` use their declared attribute-required LF-to-CRLF conversion. Those checkout bytes are retained separately from canonical source hashes. Clone uses `--local --no-hardlinks`, an empty owned template/hooks configuration and no Git network.

Node v24.19.0 uses its absolute pinned executable, SHA-256 `3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237`. The existing npm 11.6.2 archive must match 2,663,834 bytes, SHA-256 `585f95094ee5cb2788ee11d90f2a518a7c9ef6e083fa141d0b63ca3383675a20` and its plan SRI. Its 2,133 regular files are inspected before unpacking only into the new tools directory. No global installation is performed.

Only the inert `inspectPackageArchive` export of the accepted SDK setup helper is imported, after checking SHA-256 `1da4ce53bf9b7ccc462bad540294353fd7ab42ba35ca171d3921916409d2b513`. Import does not execute its setup main. The bounded parser rejects escaping/absolute/device paths, links, special members, native addons, duplicate/case-colliding members and file/directory conflicts. Each dependency must match the canonical identity, license, dependencies and bins. Unexpected optional/peer/bundled dependencies, `gypfile` and preinstall/install/postinstall/prepare declarations fail admission; their presence would be reported rather than bypassed. Other packaged script declarations are retained as inert evidence. Root license text must satisfy MIT, ISC or BSD-3-Clause textual admission; full licensing/signature/attestation review remains open.

Restore permits only the five listed HTTPS archive URLs, no redirects, inherited proxy environment or credential headers. SRI and inert inspection complete before npm ci. npm receives `--ignore-scripts --omit=optional --omit=peer --no-audit --no-fund --bin-links=false --registry=https://registry.npmjs.org/`, explicit new-root prefix/cache and empty owned user/global configs. Child environment uses task-local home/appdata/temp and omits inherited user/npm/git/provider/cloud/proxy credentials, `NODE_OPTIONS` and certificate overrides. Its temporary PATH contains only the pinned Node directory; no permanent PATH/system changes occur.

Successful restore requires exactly 13 package directories, every installed file equal to its inspected archive, unchanged root manifest/lock, matching hidden npm lock identity and unchanged original source/cache/archive/installed evidence. The helper records actual installed counts/tree hashes and packaged license bytes only after success. It preserves partial writes on failure. Applicable executor rejection must be reported with its actual reason; it is not bypassed. This candidate has performed no preparation, download or installation.

## Later genuine capture boundary

After successful root restore, load unchanged whole edit-diff/edit modules using the canonical experimental source resolver and pinned observer/offline guard. Call public matching, normalization and diff helpers with small authored inputs. Observe the public factory's supported `prepareArguments` if useful. For effects, invoke `createEditToolDefinition` with default real filesystem operations on verified-confined owned temporary files, recording input/output bytes and actual returned result/errors/diff/patch. Temp cleanup must verify the resolved target before recursive removal. No user files, renderer callbacks, terminal constructors/start, theme watchers, clipboard or native modifier helpers are invoked.

The first cases should cover reversed disjoint edits against original content, a later match introduced only by an earlier replacement, fuzzy-normalized ambiguity, overlap/no-op/empty input, supplementary/combining/fullwidth characters, untouched line preservation, BOM and mixed endings. Capture the actual display diff, unified patch and first-changed-line values; do not author their expected outputs. Snapshot undefined-property omissions separately where needed. No clock/RNG override is needed. Run two fresh credential-free children with network/process disabled and freeze genuine observed bytes only after deterministic agreement.

Source edit uses ordinary filesystem writes and the source mutation queue. The native stronger reread conflict check, resource bounds and bounded formatter can have explicit admission/output differences. Actual source observations characterize those differences; they do not imply full native jsdiff tie/patch, cancellation rollback, renderer, OS or P3-06 parity. Whole module loading, actual loaded closure, real effect capture and independent native differential acceptance remain pending.
