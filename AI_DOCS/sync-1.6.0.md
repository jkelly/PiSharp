# Pi and Pi Durable 1.6.0 update

Authorized 2026-11-21 by the project owner: update PiSharp to Pi and Pi Durable 1.6.0. This covers every applicable change in v1.5.2..v1.6.0. It does not authorize a push, a pull request, a tag, publication or deployment.

## Frozen inputs

- Integration branch: `sync/pi-1.6.0`, worktree `E:/AI/PiSharp/PiSharp/.claude/worktrees/sync-pi-1.6.0`, from PiSharp main `343ece01`.
- Upstream v1.6.0: `E:/AI/PiSharp/upstream-v160`, commit `faac585884ae2c013a6b1400f3e9e77f10b34bac` (Pi and Pi Durable are both in this repository; Durable is `packages/durable`).
- Upstream v1.5.2: `E:/AI/PiSharp/upstream-v152`, commit `b4afdddf44e059c67ccf5097e5317d913521b470`.
- Both upstream checkouts are read-only references. Diff with `git -C E:/AI/PiSharp/upstream-v160 diff v1.5.2 v1.6.0 -- <path>`; the per-package `CHANGELOG.md` `[1.6.0]` sections list the user-visible changes.
- `apps/` (p2, p2-mac, p3) are upstream applications with no native port; they are recorded as not applicable, as in the 1.5.2 sync.

## Rules for every work package

1. Work only in the worktree and on the branch your brief names. Do not touch `E:/AI/PiSharp/PiSharp` itself, other worktrees or the upstream checkouts. Do not push, open pull requests, tag or publish. No live provider calls and no credentials.
2. Commit to your branch in focused commits (one feature or fix group each). End each commit message with `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`. Do not commit `bin/`, `obj/`, `artifacts/` or `packages.lock.json` files unless you changed a package dependency on purpose.
3. Port behavior, not version strings. For each upstream change in your area decide: `ported` (implemented with a regression case), `already-ported` (name the C# evidence), `no-op` (say why: TypeScript-only, browser-only, upstream build tooling, Node-specific with no native counterpart) or `deferred` (say exactly what is missing and why). Never mark something ported without code and a test that ran.
4. Port the upstream tests for your area as cases in the area's existing C# suite. New cases go in a new file named `Sync160<Area>Cases.cs` (or beside the cases they extend) and are registered in that suite's `Program.cs`. Do not weaken or delete an existing case to make it pass; if upstream changed the expected behavior, update the expectation and say so in your report.
5. Each ported C# file starts with a header naming the upstream commit and source files, for example `// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/agent/src/agent-loop.ts.` When you bring a file up to v1.6.0, move its header to the v1.6.0 commit. A file with no upstream counterpart starts with `// PiSharp-only (no upstream Pi source): ...`.
6. Root owns these; do not edit them, and list in your report anything you need changed there: `Directory.Build.props`, `CHANGELOG.md`, `README.md`, `IMPLEMENTATION_STATUS.md`, `THIRD-PARTY-NOTICES.md`, `PiSharp.slnx`, `compatibility/**`, `PUBLIC-DERIVATIVE-INTEGRITY.json`, `tools/PublicDerivativeIntegrity.mjs`, `website/**`, `.github/**`, `docs/decisions/**`, `docs/release-notes/**` and every version string or version expectation. (The `meta` package is the exception for the paths its brief names.)
7. Stay inside the source paths your brief gives you. If a correct port needs a change in another package's paths, make the smallest change that keeps the solution building, and call it out in your report so the merge can reconcile it.

## Build and test

The machine's default SDK is not the pinned one. Use the pinned SDK directly:

```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1 MSBUILDDISABLENODEREUSE=1
DOTNET=/e/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe
$DOTNET build tests/<Suite>/<Suite>.csproj -c Release -p:RestoreSources=https://api.nuget.org/v3/index.json -p:RestoreLockedMode=false -nologo -v q
$DOTNET tests/<Suite>/bin/Release/net10.0/<Suite>.dll [suite arguments]
```

- The suites under `tests/` are console programs: each prints a report and exits 1 when a case fails. Read the suite's `Program.cs` for its arguments (`--report`, `--filter`, `--only` differ per suite).
- Build the CLI at the default version (never pass `-p:VersionPrefix`).
- `PiSharp.CodingAgent.Tests` needs `--dotnet-host <dotnet.exe> --cli <path to src/PiSharp.Cli/bin/Release/net10.0/PiSharp.Cli.dll>`; without `artifacts/extensions/published-fixtures` about 50 of its cases skip, which is expected.
- `PiSharp.Agent.Tests` has one case (`released-radius-all-28-row-routing`) that fails when the ignored `artifacts/released-npm-ai/pi-ai-0.99.1.tgz` is missing; that is known and not yours.
- Run long builds and suites with a generous timeout or in the background; do not run two builds of the same worktree at once.
- If a suite fails in a case you did not touch, check whether it also fails on the base commit before you spend time on it, and report it either way.

## Report

Write `AI_DOCS/agent-reports/sync-1.6.0-<package>.md` in your worktree and commit it. It holds: a table with one row per upstream source file or changelog bullet in your area (status from rule 3, C# files, test cases), the suites you ran with pass/fail/skip counts, anything deferred, and anything root or another package must do. Your final message repeats the essentials: commits, suite counts, deferred items, cross-package needs. State plainly what did not run or did not pass.

## Work packages

| id | branch | area | upstream paths | depends on |
| --- | --- | --- | --- | --- |
| meta | sync/1.6.0-meta | source lock, file inventory, applicability table, version pins, Node runtime pins, virtual modules, decision record | repository root, every `package.json` | none |
| ai-catalog | sync/1.6.0-ai-catalog | exact 1.6.0 model catalog, default models, Qwen Token Plan providers and key variables | `packages/ai/catalog`, `packages/ai/src/providers`, `packages/ai/src/env-api-keys.ts` | none |
| ai-providers | sync/1.6.0-ai-providers | provider wire behavior, retries, strict-mode fallbacks, session resources | `packages/ai/src/api`, `packages/ai/src/utils`, `packages/ai/src/providers/compat-schema.ts` | none |
| core-small | sync/1.6.0-core-small | agent abort results, env listing and daemon output, MCP server summary | `packages/agent`, `packages/env`, `packages/mcp` | none |
| tui | sync/1.6.0-tui | terminal UI library | `packages/tui` | none |
| cli-tools | sync/1.6.0-cli-tools | find, read, grep, bash output, exec exit codes, HTML export | `packages/coding-agent/src/core/tools`, `core/exec.ts`, `core/export-html` | none |
| cli-session | sync/1.6.0-cli-session | session runtime, modes, diagnostics, packages, extensions, llama.cpp and MCP extensions | `packages/coding-agent/src` outside `core/tools`, `core/export-html` and `modes/interactive/components` | none |
| durable-core | sync/1.6.0-durable-core | Durable harness: signals, parks, gates, hook tasks, record versions | `packages/durable/src/harness`, `src/signals.ts`, `src/session`, `src/types.ts` | none |
| chord-discovery | (report only) | decompose the Chord changes into implementation packages | `packages/chord` | none |
| cli-ui | sync/1.6.0-cli-ui | interactive components and interactive mode | `packages/coding-agent/src/modes/interactive` | tui, cli-session |
| durable-edge | sync/1.6.0-durable-edge | code-mode namespaces, image fitting, durable tools, env listing, examples, hosts built on Durable | `packages/durable/src/codemode`, `src/images`, `src/tools`, `src/env`, `test/examples` | durable-core |
| chord-* | sync/1.6.0-chord-* | set by chord-discovery | `packages/chord` | chord-discovery |

Root merges the package branches into `sync/pi-1.6.0`, then owns the changelog, website, status files, the integrity re-seal and the integrated validation.
