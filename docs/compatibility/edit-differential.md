# Unchanged public edit reference

Root's first genuine capture **executed successfully**: 12 cases in two fresh children produced byte-identical observations and loaded closure. The unchanged whole edit/edit-diff modules loaded **64 upstream and 432 dependency files**. All 1,997 installed files matched inspected official archives, and the full source remained clean at the pin. The [golden](../../fixtures/pi-v0.99.1/edit/core.expected.json), [lock](../../fixtures/pi-v0.99.1/edit/oracle.lock.json) and [manifest](../../fixtures/pi-v0.99.1/edit/manifest.json) record actual source observations. The [input](../../fixtures/pi-v0.99.1/edit/core.input.json) is authored; expected outputs were neither authored nor copied from native tests. This is a bounded source reference, not independent native differential acceptance or P3-06 closure.

The separate [edit oracle setup](edit-oracle-setup.md) succeeded under the root's approved executor restore: 13 exact packages and 1,997 archive-matched installed files. The fresh oracle is `P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-edit-oracle-v0.99.1`. Its immutable restore receipt SHA-256 is `2c1a92e8cc89f344aa397a12cd2708e2909792ad81ec402a3570b7863e110bea`. The harness pins that actual receipt, setup plan and helper before execution. All older oracles, source bytes, dependencies, locks and goldens remain unchanged.

## Genuine execution boundary

Pinned public Pi commit: `d86654abb8862e201933517d6f1fce9f88dd117f`. The harness imports unchanged **whole** `packages/coding-agent/src/core/tools/edit-diff.ts` and `edit.ts`, plus the public `utils/text.ts` BOM helper. Source SHA-256 values are `f85a9809eb44b9828236050cf38a8e45933e5cfd583a186e9a0e55a664dd3e8d` and `31a368c14cf5437ecac157765660001b16063302d9bd746303d6d1b1dcb5625d` for edit-diff/edit. It does not extract private functions, copy/recompile source, replace dependencies or erase eager renderer imports.

The genuine seam is public `createEditToolDefinition(workspace)`, its supported `prepareArguments` callback and `execute`, with **default real filesystem operations**. Operations are not replaced by a fake filesystem. Additional public helper observations cover `splitBom`, `detectLineEnding`, `normalizeToLF`, `normalizeForFuzzyMatch`, `fuzzyFindText`, `applyEditsToNormalizedContent`, `restoreLineEndings`, `generateDiffString`, `generateUnifiedPatch` and read-only `computeEditsDiff`. Preview runs before tool mutation on the actual owned file. The private argument-preparation function is reached through the returned public callback, not extracted.

Whole edit imported the renderer/TUI/theme graph even though no renderer callback, terminal constructor/start, theme watcher, clipboard or native modifier helper was invoked. No native addon/build or child process was part of this capture. The approved package profile contains the eight session packages plus chalk 6.0.0, diff 8.0.4, get-east-asian-width 1.6.0, highlight.js 10.7.3 and marked 18.0.11. Actual dependency load counts were typebox 370, highlight.js 22, diff 19, cross-spawn 6, chalk and get-east-asian-width 4 each, isexe 2, and marked/path-key/shebang-command/shebang-regex/which 1 each. Partial-json was intentionally retained from the approved session profile and **loaded zero files** in this capture.

Each child receives a fresh owned workspace and credential-free explicit environment. Network and process APIs are blocked by the existing unchanged offline guard. The existing observer forwards the unchanged canonical source resolver and records actual loaded file hashes; an additional admission hook rejects resolution outside the upstream checkout and exact 13 installed packages. No clock/RNG override, sleep or race-based expected ordering is used. Date/Date.now/Math.random identities are checked after observation.

## Authored corpus and observed shape

| Case | Authored input purpose |
| --- | --- |
| reverse-disjoint | Reverse source-order replacements against one original file |
| introduced-later-match | A later old text appears only if an earlier replacement were applied incrementally |
| normalized-ambiguity | An exact occurrence and a smart-quote-normalized equivalent |
| nested-overlap | Nested original regions in one plan |
| no-op | Identical replacement text |
| empty-edits | Empty plan at the public tool boundary |
| empty-old-text | Empty search text |
| unicode-fuzzy-overlay | Fullwidth, combining and supplementary text; fuzzy plus exact edits; untouched normalized-equivalent lines |
| bom-mixed-endings | UTF-8 BOM, CRLF/LF/bare CR, legacy single replacement and a Greek path |
| single-object-preparation | Model-produced single edit object |
| legacy-appended | Legacy pair appended to an existing edit array |
| missing-file-access | Actual access failure for an absent owned relative path |

Inputs supply literal UTF-8 file contents, relative logical paths and tool arguments. Every actual tool path is admitted under that child's `files/` directory before execution, including verification that preparation retains the admitted path. File creation uses exclusive writes. Missing-file input creates no target. No user filesystem is involved.

The expected root is `kind: "captured-unchanged-edit-oracle"` with `observations.cases[]`. Each case records public preparation's supplied-before, supplied-after and prepared values; normalization/matching/fuzzy-find/helper-generated outputs; actual preview; actual tool result; and filesystem before/after. Returned tool result objects are retained completely, including `content`, display `diff`, unified `patch` and `firstChangedLine` when present. Additional own-undefined JSON-pointer inventories expose fields omitted by JSON serialization; this does not claim CLR/JavaScript own-property identity.

Each byte snapshot contains byte length, SHA-256, base64 and decoded UTF-8. Base64 and hash retain BOM/endings exactly; the equality flag is an observed comparison of real before/after bytes. Logical paths are actual authored source inputs and actual output text, not replacements for returned temporary paths. No path, diff, patch, Unicode or newline normalization is applied to golden observations.

Rejected calls retain raw error `name`, `message` and every additional own property except `stack`. Stack is explicitly excluded from this serializable failure-contract observation because it contains harness locations; it is never normalized. No arbitrary text replacement masks an error. Other than object-key comparison order, the shared raw JSON comparator preserves arrays, strings, null/missing and numeric lexemes and rejects duplicate keys. The source itself uses ordinary JavaScript runtime numbers; retained output cannot recover precision the source runtime has already lost. This corpus uses small lengths/indices rather than unsupported precision claims.

## Executed observations and artifact pins

Five tool calls succeeded: reverse-disjoint, unicode-fuzzy-overlay, bom-mixed-endings, single-object-preparation and legacy-appended. Their observed first changed lines are 1, 3, 2, 1 and 1. The reverse plan produced `ALPHA`, unchanged `middle` and `OMEGA`. The fuzzy case preserved both untouched lines, including the original smart quotes and trailing spaces, while changing the targeted Unicode line and the exact tail edit. The BOM case retained its leading UTF-8 BOM, converted the mixed endings to the originally selected CRLF profile and retained the missing final newline. Full source-generated display and unified patch strings remain in the golden.

Seven tool calls rejected. The source could not match the introduced-later text against original content, counted two normalized ambiguity occurrences despite an exact match, rejected nested overlap/no-op/empty old text, rejected empty tool edits at its input boundary, and returned the actual missing-file access message containing `ENOENT`. All six rejected cases with existing files retained their exact original bytes; the missing file stayed absent. Preview calls fulfilled their promises while returning `{ error }` for failed previews; a fulfilled preview promise is not an accepted edit. The golden retains that distinction.

| Frozen artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| capture-edit.mjs | 24,977 | `0b957ef8c2ec1c7a5322b68167886fca3adb115e0991ff8630bbe308328b7e75` |
| core.input.json | 3,909 | `fd07bb0eb503fdcd692822dbe2d6a51d0deae47b16c7a0e56ae7ea80788812f2` |
| core.expected.json | 40,020 | `2b17786c53dc3f36e95e92920066aa64159d6d10ec1e25de2cfe110f91dcb4fb` |
| oracle.lock.json | 124,183 | `26580445d569a3b49cc52890216ce134fc76dc5fcd7cf5a9ce03fea199a8bc8b` |
| manifest.json | 2,149 | `d93a31145efa0aa0844748bdc51cf924ff5e6a5463875fcbc8b8b07c7b529e6d` |

Root's initial capture generated the golden, lock and manifest once. The only subsequent authoring change was this documentation update to record executed evidence; harness, input, golden and lock bytes were not changed. The capture history remains the immutable initial genuine capture. Detached default replay and independent native comparisons remain separate qualification steps.

## Capture and immutable reproduction

Root created the first golden with:

```powershell
$EditNode = 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
$EditOracle = 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-edit-oracle-v0.99.1'
& $EditNode tools/PiReferenceRunner/capture-edit.mjs --capture-new --oracle $EditOracle
```

The runner refuses an existing golden, manifest or lock. Default verification uses the same command **without** `--capture-new`, including from a detached review repository:

```powershell
& $EditNode tools/PiReferenceRunner/capture-edit.mjs --oracle $EditOracle
```

Both before/after read-only setup checks forward the explicit `--oracle` path, preserving the reviewed detached-checkout contract. Each execution uses two fresh children and requires byte-identical observations and loaded closure. The runner verifies canonical loaded source blobs, unchanged full source fingerprint, all installed/archive bytes, packaged license hashes, runtime/setup receipts and harness/input bytes. Default verification checks frozen input/golden/manifest/lock/environment/harness/loaded-module pins before source execution and never rewrites golden files.

First success creates `core.expected.json`, `oracle.lock.json` and `manifest.json` with exclusive creation. The lock records actual loaded source/dependency files and immutable capture history. Actual/report outputs go to `artifacts/edit-reference/`. Temporary directories are created beneath that task's scratch root; links are rejected and both resolved and real paths are verified inside the owned root before recursive cleanup. Cleanup covers only owned temporary workspaces.

Syntax and four read-only CLI/corpus regression groups passed during authoring. Root then executed the genuine first capture, which exited zero, matched its new golden and proved the actual whole-module load, repeatability and source outcomes reported above. No native implementation/build was run by the reference owner, and no independent reviewer acceptance is implied by this developer evidence.

## Native comparison and remaining gaps

Native owner `tool_output` can compare the frozen source preparation, matching/rejection, original-content behavior and saved bytes. The native bounded formatter does not currently claim exact jsdiff tie/patch parity; genuine source display/unified output provides evidence for that gap. Native mandatory invocation/schema/resource admission and its stronger exact-byte reread conflict check also require separate qualification.

This small corpus does not establish cancellation at every await, concurrent edits, external writer races, permissions, invalid encoding, large-input performance, all argument variants, renderer behavior or cross-platform filesystem coverage. Source uses ordinary access/read/write and its actual mutation queue; cancellation after a write need not imply rollback. No full Agent/tool lifecycle, provider, durable-session, CLI or phase closure follows from this source reference. Independent native differential acceptance remains separate.
