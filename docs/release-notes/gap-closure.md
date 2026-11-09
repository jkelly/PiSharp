# Upstream gap implementation (unreleased)

This work implements the November 8 audit plan against Pi 1.5.0 (`f9a26365568127461c47a07a37dcb42c140a3ac5`) and the explicitly reviewed changes through `7d073328e44337d3292a7db9b0f0308f679c5e97`. The product version remains 1.5.0. This is a set of reviewed backports and native implementations, not a claim that every later upstream change has been ported.

## Providers and model configuration

- Correct local schema references, root definitions, tool-history declarations, request headers, retry classification, and simple output budgets.
- Add Responses transcript tool handling, reviewed model catalogue updates, classifier provider overrides, and Copilot System One routing.
- Add bounded Anthropic message continuation with request compatibility checks, OAuth projection, cleanup, and a shared cache-warming policy.
- Preserve UTF-16 JSON values and metadata names through prepared requests, optional captures, and public wire round trips; keep malformed-field and resource-limit checks.
- Apply the effective per-request Responses service tier to usage pricing when the provider omits its tier echo.
- Carry reasoning budgets, WebSocket timeout settings, caller identity, and account request hooks through the native and hosted Durable routes.

## Agent, tools, and extension behavior

- Preserve mixed line endings during edits; bound large-file reads and search output; fix relative search globs, argument expansion, Unicode truncation, and persisted thinking selection.
- Handle MCP SSE line endings and callback refreshes, join shutdown work, preserve RPC response ordering, and forward hosted settings.
- Add shell authentication commands and accurate package-update results.
- Add native macOS modifier queries and Node overlay handles with lifetime, focus, visibility, and resize synchronization. The extension runtime fixture uses the reviewed Pi 1.5.0 package set.
- Bound process capture while preserving process-tree cleanup, and retain immutable streamed tool-argument snapshots without repeated whole-history copying.

## Durable and Chord

- Separate corrupted stored records from semantic transaction refusals, bound prepared statements, and add ordered bulk SQLite execution.
- Add per-conversation compaction configuration and transaction lookup.
- Implement Chord's host, reload, jobs, links, remote routes, hosting utilities, and distinct legacy protocol-4 runtime/cluster support.
- Add the SQLite telemetry recorder and the Durable delta tracker optimization.
- Exercise Durable store and registry adapters against the real Chord host in examples 35–37. New independently reproduced upstream captures cover restart, compaction, and the complete stored-schema/table/job migration corpus.

## Remote environments and tooling

- Add the pi-env framing protocol, client lifecycle, native daemon, filesystem and process adapters, watches, SSH bootstrap and verified artifact cache.
- Add Unix PTY support through a native shim. Windows terminal creation returns an explicit unsupported result; ordinary Windows process and filesystem operations remain available.
- Prepare NativeAOT daemon artifacts and five experimental NuGet libraries. See [package admission](gap-admission.md). Nothing is published by this change.
- Add the repository evaluation harness and required regression workflows, with bounded process ownership, per-case evidence, and exact checkout provenance.

## Compatibility and validation

Frozen upstream pins and historical capture evidence are retained. Current implementation mappings and runtime receipts are separate from that historical evidence. CLR ownership, Unicode backend behavior, and the existing fail-stop policy after an uncertain session write remain documented native choices.

The implementation worktree's [acceptance report](../../AI_DOCS/gap-implementation-results.md) and [validation ledger](../../AI_DOCS/validation-status.json) record local results. Windows validation cannot establish physical macOS key behavior or Unix PTY behavior; the new Linux/macOS workflows define those executable checks but have not yet run in this worktree. Live paid-provider, real SSH-host, and publication operations are not prerequisites for the offline contract tests and have not been inferred from them.
