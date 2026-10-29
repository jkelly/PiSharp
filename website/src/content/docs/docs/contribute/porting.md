---
title: How the port tracks Pi
description: Baselines, syncs, decision records, integrity pins and compatibility reports.
---

PiSharp is a port of a fixed Pi release, not of whatever Pi's `main` holds. Today that release is Pi v1.2.0. This page shows where the repository records that baseline, how a sync to a new Pi release works, and where the decisions and intentional differences live. Most of these files are under [`compatibility/`](https://github.com/jkelly/PiSharp/tree/main/compatibility) and [`docs/`](https://github.com/jkelly/PiSharp/tree/main/docs). For how a Pi release maps to a PiSharp version, see [Versioning](/docs/versioning/).

## The baseline lock

[`compatibility/target.lock.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/target.lock.json) pins the Pi release PiSharp targets:

| Field | Holds |
| --- | --- |
| `source` | The tag (`v1.2.0`), commit (`06a466935536f8c9bcacb2d5efc0fc021be1bdff`), tree and acquisition date. |
| `previousBaseline` | The v0.99.1 evidence lock, [`baseline.lock.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/baseline.lock.json), which the historical fixtures stay validated against. |
| `previousTarget` | The previous target, Pi v1.1.0, preserved byte for byte as [`target.v1.1.0.lock.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/target.v1.1.0.lock.json). |
| `packageVersions` | The versions of the upstream packages at that tag. |
| `hashPolicy` | How rows are hashed: git blob bytes, with checkout bytes recorded separately, and a `git archive` tar of the commit. |
| `referenceRuntime` | The Node.js version the reference runs used. |
| `artifacts` | One row for the source archive and one for each upstream source file PiSharp tracks, with its hashes. |
| `gaps` | Known gaps, carried forward from lock to lock. |

The release scripts, the compatibility checker and this website read the lock. The website shows its tag as the baseline, and `build-release-packages.ps1` refuses a version whose first three numbers don't match it. [`docs/compatibility/baseline.md`](https://github.com/jkelly/PiSharp/blob/main/docs/compatibility/baseline.md) records what each re-freeze changed.

## Re-freeze the baseline

The tools are in [`tools/CompatibilityReport/`](https://github.com/jkelly/PiSharp/tree/main/tools/CompatibilityReport). `refreeze-baseline.mjs` and `verify-baseline.mjs` need a clean Pi checkout with the tag at `HEAD`, and neither touches fixtures.

| Tool | Does |
| --- | --- |
| `refreeze-baseline.mjs` | Writes a new target lock. It carries the previous lock's source paths forward, drops files the new tag removed, adds the paths you name with `--add`, and hashes every row under the same policy. |
| `verify-baseline.mjs` | Checks `target.lock.json` against a checkout at the locked tag, plus the inventory links and the preserved evidence lock. |
| `check.mjs` | The full check: the lock, the inventories, and every fixture manifest under `fixtures/pi-v0.99.1`. Takes `--upstream` and `--out`. Many of its checks require the exact Node.js executable named in the lock's `referenceRuntime` (Node v24.19.0 on Windows, matched by hash). `verify-baseline.mjs` runs the same `--upstream` checks without it. |
| `repin-sources.mjs` | Moves source citations to the new commit where that is safe, and writes the sync work list. See [Sync work lists](#sync-work-lists). |
| `refresh-model-catalogs.mjs` | Copies the model catalog shards from a published `pi-ai` package and updates the pins that name it. |
| `compare.mjs` | Compares an expected and an actual JSON file. |

A re-freeze from v1.1.0 to v1.2.0 looks like this:

```sh
node tools/CompatibilityReport/refreeze-baseline.mjs --upstream <pi checkout> --tag v1.2.0 \
  --previous compatibility/target.v1.1.0.lock.json --previous-path compatibility/target.v1.1.0.lock.json \
  --evidence compatibility/baseline.lock.json --evidence-path compatibility/baseline.lock.json \
  --add <new paths> --decision docs/decisions/0006-pi-1.2.0-sync.md --out compatibility/target.lock.json
node tools/CompatibilityReport/verify-baseline.mjs --upstream <pi checkout>
```

Preserve the old target lock under its version name first, so `--previous` reads it. With `--evidence`, the old target is recorded as `previousTarget`. The tool prints how many rows changed, were added or were removed.

:::caution
Don't update a lock, a fixture or an expected output to make a failing comparison pass. [`docs/compatibility/change-policy.md`](https://github.com/jkelly/PiSharp/blob/main/docs/compatibility/change-policy.md) says what a reviewable evidence change records.
:::

## A sync to a new Pi release

A sync starts with a decision record that approves it. [Decision 0006](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0006-pi-1.2.0-sync.md) is the model. A foundation stage lands first: the decision, the applicability table, the re-freeze, the citation re-pin and the work list. Area work then ports the behavior changes, and each ported change gets authored tests.

### Applicability files

An applicability file lists every upstream change in the range and what PiSharp does about it. [`sync-1.2.0-applicability.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/sync-1.2.0-applicability.json) covers Pi v1.1.0 to v1.2.0 in 57 rows, and maps each of the 70 upstream changelog bullets to a row. Each row has an `id`, an `area`, a `status`, the `changelog` bullets and `upstream` files it covers, the `change`, the `pisharp` files it touches, and `notes`, `evidence` and `deferred`.

| Status | Meaning |
| --- | --- |
| `ported` | Required, and implemented with authored tests. `evidence` names the commits and tests. |
| `ported-with-deferral` | Ported except the part `deferred` names. The decision records the deferral. |
| `already-ported` | PiSharp already behaved this way. |
| `no-op` | Nothing to change. `evidence` says why. |
| `exclude` | Outside the port, under the linked decision. |
| `branch-port` | Ported on a feature branch and tracked by its own port map and work list. |

The older [`sync-1.1.0-applicability.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/sync-1.1.0-applicability.json) uses `port`, `verify`, `unchanged` and `exclude`.

### Sync work lists

Every ported file cites the upstream file it comes from. `repin-sources.mjs` reads each citation of the old commit, and moves it to the new commit only when every upstream path it names has the same git object id at both tags. Everything else stays on the old commit and goes to the work list until its area is ported.

```sh
node tools/CompatibilityReport/repin-sources.mjs --upstream <pi repo with both tags> --from v1.1.0 --to v1.2.0 --write
```

Without `--write` it only reports. It never rewrites historical records such as release notes, decisions, earlier sync records, fixtures and captured logs.

| File | Holds |
| --- | --- |
| [`sync-1.2.0-worklist.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/sync-1.2.0-worklist.json) | The generated re-pin result: 365 citations moved, none pending, and the excluded historical ones. Its `nonHeaderPins` section lists pins that are not citation headers, such as the model catalog shards. |
| [`durable-sync-1.2.0-worklist.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/durable-sync-1.2.0-worklist.json) | The durable port's v1.2.0 work, in areas sized S to XL, with exclusions and an order. |
| [`durable-sync-1.2.0-repin.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/durable-sync-1.2.0-repin.json) | The durable port's re-pin result, including the citations each area moved by hand. |

## Source citations and PiSharp-only files

Each ported C# file starts with a comment that names the upstream commit, the license and the source files:

```csharp
// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/agent/src/agent-loop.ts.
```

A file with no upstream counterpart says so instead:

```csharp
// PiSharp-only (no upstream Pi source): the service tiers a session can select, and their mapping to each provider API's own field.
```

Some durable files read `// PiSharp-only since pi-durable 1.2.0 (...)`, for code that upstream removed or never had in that release. The custom agents files start with `// Decision 0012: ...`, because they port nothing. Keep these headers accurate when you change a file: the re-pin tool and the work lists depend on them.

## Decision records

[`docs/decisions/`](https://github.com/jkelly/PiSharp/tree/main/docs/decisions) holds the owner's decisions. Read the one for an area before you change its scope.

| Record | Decides |
| --- | --- |
| [0001](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0001-initial-implementation-scope.md) | The first implementation milestone: an offline .NET 10 stream seam and a reproducible record of the pinned Pi v0.99.1 source. |
| [0002](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0002-pi-1.1.0-sync.md) | The sync to Pi v1.1.0. |
| [0003](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0003-codemode-javascript-engine.md) | Code mode runs on Jint. |
| [0004](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0004-full-parity-owner-decisions.md) | Owner decisions for full Pi v1.1.0 parity. |
| [0005](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0005-pi-durable-experimental.md) | The experimental pi-durable port, released as experimental in 1.2.0.1. |
| [0006](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0006-pi-1.2.0-sync.md) | The sync to Pi v1.2.0. |
| [0007](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0007-media-generation.md) | Media generation, embeddings, model operations and service tiers: PiSharp-only, experimental and unreleased. |
| [0012](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/0012-custom-agents.md) | Custom agents, workflows and subagents on the durable harness: experimental and unreleased. |
| [ecmascript-json-boundary](https://github.com/jkelly/PiSharp/blob/main/docs/decisions/ecmascript-json-boundary.md) | `EcmaScriptJsonProjection`: where native JSON values are turned into the text JavaScript's `JSON.stringify(JSON.parse(text))` would produce. |

There are no records 0008 to 0011.

## Intentional differences

Where PiSharp behaves differently from Pi on purpose, the difference is recorded with the decision that allowed it:

- The decision records list them: 0005 has numbered "Intentional difference" items, 0006 has "Notes: implementation differences and close-out fixes", and 0007 covers the media and tier features.
- Applicability rows record per-change outcomes, deferrals and exclusions.
- The durable port map marks files as `pisharp-only`, `excluded` or `skipped`, with a note.
- [`docs/compatibility/approved-provider-differences.md`](https://github.com/jkelly/PiSharp/blob/main/docs/compatibility/approved-provider-differences.md) and [`docs/release-notes/provider-compatibility-exceptions.md`](https://github.com/jkelly/PiSharp/blob/main/docs/release-notes/provider-compatibility-exceptions.md) record three approved Anthropic streaming differences.
- The `deviations` section of `website/src/data/parity.json` lists the user-visible ones, intentional and still open, on the [parity page](/parity/).

## The durable port map

[`compatibility/durable-port-map.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/durable-port-map.json) maps every upstream pi-durable file to its PiSharp counterpart. It pins Pi v1.2.0 and records changes against Pi v1.1.0. Each of its 214 rows has the upstream path, blob id and line count, the PiSharp files, the phase, a `status` and an `upstreamChange`.

| Status | Meaning |
| --- | --- |
| `planned`, `in-progress`, `ported` | The port's progress. |
| `needs-sync` | Upstream changed the file since it was ported. |
| `removed-upstream` | Upstream deleted the file. |
| `pisharp-only` | PiSharp code with no upstream file. |
| `excluded`, `skipped` | Not ported, with the reason in `note`. |

`upstreamChange` is `added`, `modified`, `renamed` or `removed`. The map is built from git objects, and the durable workflow checks it on pushes and pull requests to `main`:

```sh
node tools/DurablePortMap/build-port-map.mjs --upstream <pi repo with both commits> --check
```

## Two parity files

Two files with the same name serve different readers:

| File | For | Holds |
| --- | --- | --- |
| [`compatibility/parity.json`](https://github.com/jkelly/PiSharp/blob/main/compatibility/parity.json) | The compatibility checker | 46 formal requirements, all `Deferred`, each with an owner, phase, test id and evidence state. It claims no upstream or native parity, and `check.mjs` reads it. |
| [`website/src/data/parity.json`](https://github.com/jkelly/PiSharp/blob/main/website/src/data/parity.json) | People | The [parity page](/parity/): per-area status (`ported`, `partial`, `planned`, `wont-port`, `unknown`) with notes and evidence, the deviations, and the phases. Update it by hand when a port lands. |

## Derivative integrity pins

The public repository is an export, and some of its files differ from the bytes that historical capture records hashed. [`PUBLIC-DERIVATIVE-INTEGRITY.json`](https://github.com/jkelly/PiSharp/blob/main/PUBLIC-DERIVATIVE-INTEGRITY.json) maps each such file's original hash and length to its current ones. It has 2,670 rows across `src/`, `tests/`, `docs/`, `tools/`, `fixtures/`, `compatibility/` and root files such as `Directory.Build.props`, `README.md` and `PiSharp.slnx`. It also holds historical execution pins, which never change, and current dependency pins.

[`tools/PublicDerivativeIntegrity.mjs`](https://github.com/jkelly/PiSharp/blob/main/tools/PublicDerivativeIntegrity.mjs) holds the table's own SHA-256 and rejects a table with any other digest. When a tool such as `check.mjs` resolves a historical pin, it reads the file's bytes from your working copy and requires the current hash and length. It refuses symbolic links and non-canonical paths.

When you change a file the table lists, re-seal it:

```sh
node tools/refresh-derivative-integrity.mjs --check
node tools/refresh-derivative-integrity.mjs
```

`--check` prints the rows whose current bytes changed and exits with 1 if there are any. Without `--check`, the tool rewrites the current columns and the dependency pins, and writes the new digest into `PublicDerivativeIntegrity.mjs`. It never rewrites the original columns or the historical pins, and fails if a pinned file is missing. If you move a pinned file, pass `--move old=new`. Commit the table and the `.mjs` in the same pull request as the change, as the release preparation does.

:::caution[Line endings]
The tool hashes the bytes in your working copy, not the bytes git stores. [`.gitattributes`](https://github.com/jkelly/PiSharp/blob/main/.gitattributes) stores `.cs`, `.json`, `.md`, `.mjs`, `.ps1`, `.csproj`, `.props`, `.slnx` and similar files with LF, and keeps some files byte-exact (`-text`), including the table itself. If your editor saved a file with CRLF, the seal pins bytes a fresh clone never has. Check with `git ls-files --eol <file>`, which should show `w/lf` for those files, before you re-seal.
:::

No workflow runs `--check`, so run it yourself before you commit a change to a pinned file.
