# Public package and entrypoint ledger

Root checkpoint: the generator executed read-only against the pinned source and produced `artifacts/public-entrypoints-ledger.json` (1925136 bytes, SHA-256 `c9f758a4844025909912ea1fcf6e65d038b0bb79f2742a3ee1056bce2b161349`). The actual census is 2093 source files, 2091 raw checkout matches plus two declared conversions, 23 manifests, 13 published packages and 150 entry declarations. Twelve published archives remain unacquired. The corrected parser/model harness passed 51/51 tests; an initial duplicate-package synthetic fixture failed because acquired-AI validation preceded the intended duplicate check, and its log is preserved. Only that fixture identity changed. Existing 712 rows and all Deferred/Unverified fields remain untouched. The ledger and counts establish static evidence only; public member/signature parsing and behavioral/native acceptance remain unfinished.

This is a static P1-03 expansion for public Pi v0.99.1 at commit `d86654abb8862e201933517d6f1fce9f88dd117f`, tree `200bd10bb146773516f862a02b8aaebeed163e00`. The [plan](../../compatibility/public-entrypoints.plan.json), [generator](../../tools/SurfaceInventory/build-public-entrypoints.mjs) and [parser/model tests](../../tools/SurfaceInventory/build-public-entrypoints.test.mjs) establish source and declared package boundaries before a larger contract inventory. They do not establish behavioral, file/wire, native SDK or optional bridge parity.

The complete [Phase 1 plan](../plans/01-compatibility-target.md), [architecture](../architecture.md), [eight execution plans](../plans/README.md) and parent-owned [profile/platform draft](profile-platform-matrix.md) remain authoritative. C#/.NET 10 with a JIT host, native C# extensions and a Node-free core remain the native target. Development-only Node tooling does not become a runtime dependency. Existing parity, profiles, implementation status and mandatory requirements are unchanged.

## Scope and evidence

The plan pins the canonical 2,093-file Git tree and all 23 `package.json` files. Thirteen package manifests correspond to published v0.99.1 registry metadata. Other manifests describe the private monorepo root, evals, installation metadata and examples. Here, `private` records the upstream npm publication field; all inspected upstream content is public canonical source. No private PiDotNet implementation or context is consulted.

Each source record carries its path, mode, Git blob ID, byte count, SHA-256 and pinned source URL. Git object bytes are checked against their blob identity. Checkout bytes are compared separately; the only admitted conversions are the exact pinned `pi-test.bat` and `pi-test.ps1` LF-to-CRLF transformations, grounded in the pinned `.gitattributes`. The output reports the actual raw/conversion counts rather than treating transformed checkout bytes as canonical bytes. Complete file accounting is not complete contract review.

The [existing 712-row CLI/RPC/SDK declaration inventory](command-rpc-inventory.md) is retained by its exact digest, `b45c7e117fdb46dde823de9c81717349fdc09fb0b265b706f6433afc13484a14`. The generator validates its pinned baseline, count and Deferred/Unverified/no-execution fields, then references it unchanged. It does not duplicate its rows or treat the 457 root export names as independently qualified features.

Every manifest's full metadata is retained. Entrypoint records enumerate all declared `exports` branches, including source/types/import conditions, ordered fallback arrays, explicit null blocks, wildcard targets, `main`, `types` and executable declarations. Identity uses the canonical manifest path and escaped JSON pointer; condition order and branch ordinals are preserved. The generator does not select an active condition or load an entrypoint. Manifest publication rules, scripts, optional dependencies and experimental declarations remain visible.

| Published package | Declared public boundary to review |
| --- | --- |
| `@earendil-works/pi-coding-agent` | Root SDK, `rpc-entry`, `pi`; source-only `client` and `experimental/plugin` |
| `@earendil-works/pi-ai` | Root, `compat`, OAuth/Bedrock/Bun entrypoints, three wildcard families and `pi-ai` |
| `@earendil-works/pi-agent-core` | Root, Node and context/environment/reducer/session/testing harness subpaths, `experimental/pico3`, package metadata |
| `@earendil-works/chord` | Root, context, delta, bundler, Node and package metadata |
| `@earendil-works/pi-mcp` | Root, OAuth and testing |
| `@earendil-works/pi-codemode` | Root, declarations, source parser and worker |
| `@earendil-works/pi-tui` | Root main/types plus shipped modules/assets; no restrictive `exports` map |
| `@earendil-works/pi-telemetry` | Root and testing |
| `@earendil-works/pi-client` | Root, Unix and package metadata |
| `@earendil-works/pi-protocol` | Root |
| `@earendil-works/pi-server` | Root, testing and Unix |
| `@earendil-works/pi-durable` | Root, environments, memory/JSONL/SQLite storage, testing and package metadata |
| `@earendil-works/pi-session-backend-sqlite-node` | Root; migration assets need subsequent tracing |

Direct targets already present in canonical source and explicit `source` conditions identify actual canonical files. A `dist`-to-`src` candidate remains **Unverified**, even when a corresponding file exists. Build transformations and source maps require separate evidence. Wildcard expansion preserves nested substitutions and deterministic path order; patterns with more than one target wildcard fail explicitly pending parser/owner review. Missing source candidates remain Unverified. No source record establishes installed or executable JavaScript behavior.

Declared workspace dependency closure starts at coding-agent and follows manifest `dependencies` only. It does not establish import reachability, dynamic loads, optional dependency use, compatibility scope or required runtime features. Full manifest metadata and unresolved obligations retain those questions. No package-name exclusion is assigned.

## Released artifacts

Only the acquired AI tarball qualifies released-file presence in this ledger. Its SHA-256, byte count, SHA-512 SRI and SHA-1 are checked against the plan, acquisition record, inspection and historical receipt. The tar is decompressed in memory and never extracted or executed. Header checksums, member kinds, confined paths, duplicate names, sizes, padding and end blocks are checked; every regular file's bytes/hash are compared with the pinned 813-file inspection. Its package manifest must match canonical source bytes before model construction.

The [AI inspection](../../artifacts/released-npm-ai/inspection.json) is separate from the earlier [registry/source inspection](../../artifacts/released-baseline/inspection.json). Historical registry `archiveAcquired:false` fields are retained verbatim; current qualified presence is reported separately. Registry signature and attestation verification remain unresolved. Archive presence does not establish dependency closure, import success, public member contracts or native compatibility.

The other twelve published archives are explicitly **Unacquired**, and their released targets remain **Unverified**. A source declaration or publication allowlist does not establish tarball contents. Coding-agent's `client` and `experimental/plugin` have source-only conditions, while declared publication excludes related compiled directories and does not list `src`; actual released usability remains unresolved. TUI's absent `exports` map also prevents treating root declarations as its whole admitted module boundary.

An explicit null export is recorded as a blocked upstream declaration. It is not a PiSharp profile exclusion. An absent target in the qualified AI archive is recorded as `AbsentInQualifiedArchive`, with no implementation acceptance credit.

## Run contract and qualification

Use the pinned Node 24.19.0 executable. From an isolated PiSharp candidate checkout, run:

```powershell
& 'P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe' --test tools/SurfaceInventory/build-public-entrypoints.test.mjs
& 'P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe' tools/SurfaceInventory/build-public-entrypoints.mjs --source 'P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-reference-oracle-v0.99.1/upstream'
```

The generator accepts exactly one `--source` argument. It has no output-writing, setup, restore, capture, install or network mode. It reads pinned repository inputs and canonical Git objects, checks the source checkout, and writes deterministic JSON to stdout. There is no timestamp, absolute checkout root or behavior normalization in the ledger. A caller may save stdout to a separately approved evidence path and compare two fresh outputs; no existing artifact is overwritten by the helper.

Tests use authored in-memory parser/model/archive fixtures. They cover condition/fallback ordering, escaped pointer identities, nested wildcard substitutions, confinement, duplicates, missing metadata/targets, canonical-content tampering and archive identity/member tampering. They import only this helper and Node built-ins, with no upstream execution or fixture regeneration. Their execution results and actual ledger counts must be reported by the isolated qualification runner; authorship supplies no passing count.

## Remaining contract and profile work

All entrypoint implementation acceptance remains **Deferred**, with evidence **Unverified**, behavioral captures **0**, native parity assertions **0** and phase gates closed **0**. Static `PresentInQualifiedArchive` and `DeclaredCanonicalSourcePresent` states describe their own evidence layer only.

A later qualified TypeScript parser must resolve public re-export origins, aliases, member/signature/overload contracts, type-only/runtime distinctions and external definitions. Owner review must then author individually testable behavior rows and crosswalk them to the existing 46 families. No AST or behavioral closure is claimed here.

Dynamic imports, lazy providers, workers, generated catalogs/assets, native addons, optional dependencies, public experimental paths and examples retain explicit review obligations. The P2 provider/auth/capability matrix, P3 Agent/tool/MCP/codemode effects, P4 SDK/session lifecycle and persistence, P5 CLI/RPC/UI contracts, P6 native extension ABI/reducers, P7 bridge corpus and P8 platform/release gates remain open according to their owning plans. Raw numeric semantics, missing/null distinctions and observable ordering require later executable comparisons; this metadata parser is not their comparator.

Parent/product and subsystem owners retain decisions about requirement granularity, experimental/product dependency classification, preview positive lists, deliberate semantic differences and exact OS/RID/terminal/library qualification. Mandatory Deferred requirements still block full parity. Unacquired artifacts and unchosen dependencies remain named gaps, with no new permanent exclusion or immediate user action requested by this ledger.
