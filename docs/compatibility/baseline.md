# Partial immutable baseline evidence

## Current target: Pi v1.5.0 (source-level re-freeze)

Under [decision 0013](../decisions/0013-pi-1.5.0-sync.md), the source target is public Pi **v1.5.0** at `f9a26365568127461c47a07a37dcb42c140a3ac5` (tree `b7734e15f2c1a10846284f77034a2214746a4da6`), recorded in [target.lock.json](../../compatibility/target.lock.json). The sync goes straight from v1.2.0: v1.3.0 and v1.4.0 get no lock of their own. [refreeze-baseline.mjs](../../tools/CompatibilityReport/refreeze-baseline.mjs) generated it from the v1.2.0 target lock under the unchanged hash policy:
- it carries forward the previous 121 source-file paths and drops the two that upstream removed: `ai/scripts/generate-models.ts` (the catalog generator moved to `ai/catalog/`) and `ai/src/utils/assistant-message-frame.ts` (removed in v1.3.0);
- it adds 78:
  - the new pi-ai sources: `ai/src/auth/oauth/trusted-url.ts`, `ai/src/providers/schema-builders.ts`, `ai/src/telemetry.ts` and `ai/src/utils/{identity,openrouter-cost,streaming-json,string-enum,version}.ts`;
  - the catalog generator: `ai/catalog/{build,define,generate,reasoning}.ts`, `ai/catalog/sources/*.ts`, `ai/catalog/providers/index.ts` and `ai/catalog/families/*.ts`;
  - `coding-agent/src/core/{client-identity,pi-ai-schemas}.ts` and `coding-agent/src/core/extensions/pi-ai-extension-view.ts`;
  - the new `schema` package (`package.json` and its 43 `src` files);
  - the `telemetry` package (`package.json` and its 8 `src` files), now in scope.

Of the 197 rows, 92 changed, 27 are byte-identical and 78 are new. The coding-agent, AI, agent, TUI, codemode, MCP, schema and telemetry package versions are 1.5.0; the root package is 0.0.3. The re-freeze tool now records the version of every package with a tracked file; regenerated from the v1.1.0 lock at v1.2.0, it reproduces the v1.2.0 lock byte for byte apart from `acquiredAt`. The open gaps carry forward unchanged.

One `surfaces.json` requirement cited a removed file. `stream.indexed-events` (indexed text, thinking and tool-call frame reduction) named `assistant-message-frame.ts`. It now names `ai/src/types.ts`, where v1.5.0 defines the indexed `AssistantMessageEvent`s. Its status and evidence are unchanged (`Deferred`, `NarrowEvidenceOnly`).

Three earlier locks stay byte-for-byte:
- [target.v1.2.0.lock.json](../../compatibility/target.v1.2.0.lock.json) is the v1.2.0 target lock, named by `previousTarget`. The v1.2.0 event catalog and command/RPC inventory below were generated for that target and stay validated against it.
- [target.v1.1.0.lock.json](../../compatibility/target.v1.1.0.lock.json) is the v1.1.0 target lock. The v1.1.0 inventories stay validated against it.
- [baseline.lock.json](../../compatibility/baseline.lock.json) is the v0.99.1 **evidence** lock, named by `previousBaseline`. `fixtures/pi-v0.99.1/**`, its captures, `provenance.json` and every frozen inventory that hashes that lock remain validated against it. No fixture or frozen inventory was recaptured, relabelled or re-sealed.

Target consumers read `target.lock.json`: `surfaces.json`, `parity.json`, the release record, the release and packaging scripts, and the website. `check.mjs` validates `surfaces.json`, `parity.json` and `--upstream` against the target lock, and provenance and fixtures against the evidence lock.

To verify the target lock against a clean upstream checkout without the fixture corpus or the pinned development Node executable:

```powershell
node tools/CompatibilityReport/verify-baseline.mjs --upstream <checkout of v1.5.0>
```

It verified all 197 files and the archive hash (252 checks).

Source citations in PiSharp files that named v1.2.0 (`06a4669…`) were re-pinned to v1.5.0 by [repin-sources.mjs](../../tools/CompatibilityReport/repin-sources.mjs) only where every cited upstream file is byte-identical between the two tags: 250 citations in 250 files. The other 633 citations, in 630 files, stay on v1.2.0 until their area is ported. For the `<path> (commit <sha>)` and `<path> at <sha>` forms the tool now also reads the path before the commit: before that, it moved the THIRD-PARTY-NOTICES record of the four embedded configuration schemas on the strength of the theme files named after the commit, although the models and settings schemas changed upstream. 19 more could not be parsed and are checked by hand with their area. [sync-1.5.0-worklist.json](../../compatibility/sync-1.5.0-worklist.json) lists them all by area.

The v1.5.0 event catalog and command/RPC inventory are not generated yet. Until they are, the v1.2.0 inventories are the newest; see the work list.

## Previous target: Pi v1.2.0

Under [decision 0006](../decisions/0006-pi-1.2.0-sync.md), the source target was public Pi **v1.2.0** at `06a466935536f8c9bcacb2d5efc0fc021be1bdff` (tree `e8872645392bc4d214e3153104864e4b69d99ed0`), now preserved in [target.v1.2.0.lock.json](../../compatibility/target.v1.2.0.lock.json). `refreeze-baseline.mjs` generated that lock from the v1.1.0 target lock:
- it carried forward the previous 103 source-file paths, none of which upstream removed;
- it added 18: the new configuration-schema sources (`ai/src/providers/{compat-schema,model-schema}.ts`, `coding-agent/src/core/{settings-schema,settings-defaults,keybindings-schema,key-id-schema}.ts`, `coding-agent/src/modes/interactive/theme/{theme-schema,theme-tokens}.ts` and the four published `coding-agent/schemas/*.schema.json`), the OpenRouter per-key catalog (`ai/src/providers/{openrouter,openrouter-api}.ts`, `coding-agent/src/core/remote-catalog-provider.ts`), and `codemode/src/{declarations,source}.ts` and `mcp/src/client.ts`.

Of the 121 rows, 21 changed, 82 were byte-identical and 18 were new. The coding-agent, AI, agent, TUI, codemode and MCP package versions were 1.2.0. It verified all 121 files and the archive hash (176 checks).

Source citations that named v1.1.0 (`abe508e…`) were re-pinned to v1.2.0 where every cited upstream file was byte-identical, and the rest moved with their area's port. [sync-1.2.0-worklist.json](../../compatibility/sync-1.2.0-worklist.json) ended with no pending citation.

## Earlier target: Pi v1.1.0

Under [decision 0002](../decisions/0002-pi-1.1.0-sync.md), the source target was public Pi **v1.1.0** at `abe508e1b89912adde45528136c3221eb69acdd7` (tree `77d78fae728879c16550b1f874785aed7851233c`), now preserved in [target.v1.1.0.lock.json](../../compatibility/target.v1.1.0.lock.json). `refreeze-baseline.mjs` generated that lock from the v0.99.1 lock:
- it carried forward the previous 99 source-file paths;
- it dropped the two that upstream removed (`providers/azure-openai-responses.models.ts` and `coding-agent/npm-shrinkwrap.json`);
- it added six (`providers/azure.ts`, `providers/azure.models.ts`, `api/azure-openai-config.ts`, `api/openai-decisions.ts`, `api/classifier-shared.ts`, `agent/src/agent-loop.ts`).

Of the 103 rows, 35 changed and 62 were byte-identical. The coding-agent and AI package versions were 1.1.0.

### Surface inventories: v0.99.1 preserved, v1.1.0 and v1.2.0 refreshed

Two whole-file surface inventories are pinned by other evidence. They therefore follow the lock precedent: the v0.99.1 files stay byte-for-byte as evidence, and the v1.1.0 refresh lives in new files beside them.

| Inventory | Preserved v0.99.1 evidence | v1.1.0 inventory |
| --- | --- | --- |
| Extension event catalog (41 events) | [event-catalog.json](../../compatibility/extensions/event-catalog.json) | [event-catalog.v1.1.0.json](../../compatibility/extensions/event-catalog.v1.1.0.json) |
| Command, RPC and SDK declarations | [coding-agent-command-rpc-inventory.json](../../compatibility/coding-agent-command-rpc-inventory.json) (712 rows) | [coding-agent-command-rpc-inventory.v1.1.0.json](../../compatibility/coding-agent-command-rpc-inventory.v1.1.0.json) (716 rows) |

The v0.99.1 files remain the ones pinned or validated elsewhere:
- [public-entrypoints.plan.json](../../compatibility/public-entrypoints.plan.json), [command-rpc-inventory.md](command-rpc-inventory.md) and [public-surface-inventory.md](public-surface-inventory.md) pin the command/RPC inventory.
- `EventReducerTests` checks the event catalog against the P6-01 native-surface inventory.

New work reads the v1.2.0 files (below).

Two generators produce both versions:
- [build-event-catalog.mjs](../../tools/SurfaceInventory/build-event-catalog.mjs)
- [build-command-rpc-inventory.mjs](../../tools/SurfaceInventory/build-command-rpc-inventory.mjs)

Each generator works as follows:
- **Ref and bytes.** It takes the ref from a lock file and reads canonical Git blob bytes with `git show`.
- **Carried forward.** It keeps the authored row text of the v0.99.1 file.
- **Re-derived.** It recomputes every source pin, span, embedded declaration and count.
- **Native status.** It applies an authored overlay. The overlays are [event-catalog.v1.1.0.overlay.json](../../compatibility/extensions/event-catalog.v1.1.0.overlay.json) and [coding-agent-command-rpc-inventory.v1.1.0.overlay.json](../../compatibility/coding-agent-command-rpc-inventory.v1.1.0.overlay.json).

With the v0.99.1 lock and no overlay, both generators reproduce the preserved files byte-for-byte.

Upstream changes between the two versions:
- **Event catalog.** There are no added or removed events. Every span was recomputed. Two event declarations changed: `AgentSettledEvent` gained `aborted` and `ToolExecutionEndEvent` gained `durationMs`.
- **Command/RPC inventory.**
  - It adds `--no-mcp` and the root exports `ToolRendererResolver`, `ToolRenderers` and `QuietStartup`.
  - It records the changed `--models` and `--tools` parser branches and the `agent_settled` and `tool_execution_end` declarations.
  - RPC types, the RPC handler and the slash registry are byte-identical.

Native status:
- **What counts as Implemented.** A surface is recorded as implemented only where an authored test case exercises it. Each such test is named by suite, file and case.
- **Acceptance.** `implementationAcceptance` stays Deferred.
- **Native-only surfaces.** The `mcp_diagnostic` stderr record and the `LiveAuthenticationFailed` and `LiveAzureEndpoint` failure codes have no upstream counterpart. They are listed under `nativeOnlySurfaces`.

[inventory-refresh.test.mjs](../../tools/SurfaceInventory/inventory-refresh.test.mjs) checks both versions offline. With `PI_UPSTREAM` set to a Pi checkout that has both tags, it also re-runs both generators:

```powershell
$env:PI_UPSTREAM = '<Pi checkout with v0.99.1 and v1.1.0>'; node --test tools/SurfaceInventory/inventory-refresh.test.mjs
```

The v1.2.0 refreshes ([event-catalog.v1.2.0.json](../../compatibility/extensions/event-catalog.v1.2.0.json) with its [overlay](../../compatibility/extensions/event-catalog.v1.2.0.overlay.json), and [coding-agent-command-rpc-inventory.v1.2.0.json](../../compatibility/coding-agent-command-rpc-inventory.v1.2.0.json)) were generated from the v1.1.0 inventories with `--lock compatibility/target.lock.json` while v1.2.0 was the target, and now stay validated against `target.v1.2.0.lock.json`, as the v1.1.0 inventories stay validated against `target.v1.1.0.lock.json`. Between the two:
- `before_agent_start` is a union on `messageType`: `BeforeAgentStartUserEvent` and `BeforeAgentStartCustomEvent` are new root exports, and its runner dispatcher takes a trigger;
- the removed `./client` and `./experimental/plugin` package entries are kept and marked `removedUpstream`.

The command/RPC inventory records checkout bytes when the checkout is at the target tag, so the v1.2.0 inventories reproduce from a checkout at v1.2.0:

```powershell
$env:PI_UPSTREAM_V1_2_0 = '<Pi checkout at v1.2.0 with v1.1.0>'; node --test tools/SurfaceInventory/inventory-refresh.test.mjs
```

The v1.5.0 refreshes, [event-catalog.v1.5.0.json](../../compatibility/extensions/event-catalog.v1.5.0.json) and [coding-agent-command-rpc-inventory.v1.5.0.json](../../compatibility/coding-agent-command-rpc-inventory.v1.5.0.json), were generated from the v1.2.0 inventories with `--lock compatibility/target.lock.json`. They have no overlay yet: native statuses carry over from v1.2.0 until each area reassesses them ([sync-1.5.0-applicability.json](../../compatibility/sync-1.5.0-applicability.json)). Between the two:
- **The events are unchanged.** The 41 extension events keep their declarations, subscriptions and dispatchers.
- **New root exports.** `KeybindingHints`, `PackageInstallFailure` and `formatReadLineRange` are new root exports, giving 721 rows and 465 root exports.
- **Changed text.** The text of the `mcp` help line and the `/import`, `/share` and `/resume` registry entries changed. The generator finds these lines again by their command or entry name. Their `observed.statement` follows the new text.
- **Removed entries.** The entries v1.2.0 marked `removedUpstream` are carried forward as they were.

```powershell
$env:PI_UPSTREAM_V1_5_0 = '<Pi checkout at v1.5.0 with v1.2.0>'; node --test tools/SurfaceInventory/inventory-refresh.test.mjs
```

## Evidence baseline: Pi v0.99.1

The source target was public Pi v0.99.1 at `d86654abb8862e201933517d6f1fce9f88dd117f`. The local checkout HEAD, tag resolution and tree are recorded in [baseline.lock.json](../../compatibility/baseline.lock.json). The coding-agent and AI package versions are 0.99.1; the repository root package version is 0.0.3.

The lock records a reproducible `git archive --format=tar` SHA-256, canonical Git blob bytes and Windows checkout bytes for selected source files, package/shrinkwrap/install lockfiles, generated catalog imports and provider stubs. Checkout CRLF conversion is visible through separate byte counts and hashes. The source tar is reproducible from the pinned Git objects and is not retained or presented as a published release tarball. The exact preinstalled Node 24.19.0 executable is hashed for development reference runs. No runtime or upstream package is bundled into the native product.

The executable/distribution baseline remains incomplete. Released npm/standalone artifacts are not acquired or hashed. The tracked provider `.models.ts` files import absent `providers/data/*.json`; the generated model data manifest is also absent. No catalog has been reconstructed or regenerated. Costs/model metadata remain unfrozen. Lockfiles are captured, but package bytes and transitive license files have not been acquired. Node distributor archive provenance and the runtime/platform matrix remain open.

[surfaces.json](../../compatibility/surfaces.json) seeds 46 requirements covering each planned surface family and all ten `KnownApi` values. It is explicitly a seed rather than a complete per-export/command/event/capability enumeration. Every row remains Deferred in [parity.json](../../compatibility/parity.json), including mandatory rows. Authored contract tests and narrow upstream queue captures do not close these rows or Phase 1.

The [provenance record](../../compatibility/provenance.json) distinguishes public source adaptation, newly authored synthetic data and captured queue behavior. [Third-party notices](../../THIRD-PARTY-NOTICES.md) retain the complete upstream MIT notice. Transitive redistribution review, naming review, scope decisions and contract/platform signoff remain open with owners in the lock and provenance records.

Run the checker from the repository root with the exact locked Node executable:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/CompatibilityReport/check.mjs --upstream 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-upstream-v0.99.1'
```

The checker reads immutable blobs and recreates the source archive in memory, validates metadata and fixture checksums, and prints remaining blockers. It never regenerates expected outputs or downloads dependencies. Without `--upstream`, it validates repository evidence but reports zero source files verified.
