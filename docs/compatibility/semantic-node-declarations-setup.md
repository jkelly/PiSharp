# Two-package Node declaration admission

This is a new gpt-6.1-sol/xhigh setup candidate for stage A of the actual whole-project declaration plan. The original qualified oracle, its receipt, all 2,623 immutable files, existing setup/profile scripts, source and goldens remain unchanged. No compiler or package runtime is imported by the new helper. Its three modes perform read-only checking, exclusive offline copying, and inert archive admission; the helper has no network, npm, child-process or native-execution action.

Root acquired exactly `@types/node`22.19.19 and `undici-types`6.21.0 from the official source-lock URLs after an actual executor approval. The preserved acquisition receipt is `task-2/semantic-node-declaration-archives-second/acquisition.json`, SHA-256 `51d629aff533ae361fa27230b6ecbed6d12ec202c1d841b10b73330143fcecbe`. The raw Node archive is 445,223 bytes, SHA-256 `c32937b40ab720ef6242de0bf4c8b8e48f1e4a29fbb4cb9d9f596471ee58d5c4`; Undici is 21,020 bytes, SHA-256 `cfa1a9a5e18988850e548f4f6b629c58da32dc32bebb116e58ea301e516fd16d`. Both match the canonical lock's complete SHA-512 SRI in the [new plan](../../compatibility/semantic-node-declarations.plan.json). This authoring task made no request or archive write.

Inert inspection observed the following actual packaged fields and bytes:

| Package | Tar/member shape | Declaration fields | Root license |
| --- | --- | --- | --- |
| `@types/node`22.19.19 | Publisher root `node v22.19/`; 83 headers, 10 directories, 73 regular files; 2,490,880 expanded bytes | `types:index.d.ts`; `typesVersions: <=5.6 → ts5.6/*`; 70 declaration files; dependency `undici-types:~6.21.0`; scripts `{}`; no bin | MIT LICENSE, 1,141 bytes, SHA-256 `c2cfccb812fe482101a8f04597dfc5a9991a6b2748266c47ac91b6a5aae15383` |
| `undici-types`6.21.0 | Publisher root `package/`; 41 headers and regular files; 115,712 expanded bytes | `types:index.d.ts`; 38 declaration files; no typesVersions/dependency/scripts/bin fields | MIT LICENSE, 1,090 bytes, SHA-256 `a6db8096b2707bc0102d256917d4d33f298ba36d8c3f25de067a2b5bb379db27` |

Neither archive contains a root NOTICE. The helper retains full observed LICENSE texts, raw manifests, all file hashes and all regular file bytes. This is packaged evidence rather than a legal or redistribution approval. The Node manifest is 4,275 bytes with SHA-256 `ba8f6326a685af9e7dce89a99f55e0dc3cef450046acf00a20f7cb673562f581`; Undici's manifest is 1,193 bytes with SHA-256 `11f873b423b96a5ad444a099685ca6b9de1379dcd83fd8d368e757ddb53658e4`.

The separately owned [archive parser](../../tools/SemanticInventory/node-declaration-archive.mjs) adapts the reviewed `inspect-ast-archives.mjs` function without changing that inspector or rewriting archive headers. Each archive must use its exact pinned publisher root. The actual Node archive additionally uses a USTAR time-header layout: 130-byte prefix at 345, NUL separator at 475, strict safe-octal time fields at 476/488, and zero reserved tail at 500–511, with exact USTAR magic/version. All 83 Node headers use those time fields. Undici uses the original 155-byte USTAR prefix layout. The new parser retains the original strict checksum, UTF-8, numeric, padding, terminal, local-PAX, path, device/ADS, duplicate/case-collision, parent-conflict, link and member bounds. The original inspector remains hash pinned. Unsupported roots/header layouts fail; no blind suffix, NUL, link or traversal exception is added.

Author tests passed 79/79 and syntax checks passed for all three new JavaScript modules. They inspected the exact acquired archives twice without extraction and tested authored malformed archives/manifests: source SRI/hash/size changes, prefix/header/time/reserved bytes, unsafe paths, links/devices, collisions, malformed UTF-8/JSON, lifecycle/bin/native payloads and resource bounds. No oracle assembly, compiler import, process or network operation occurred. This test count is an infrastructure result and passes no phase task.

`--check` is the default and performs only read-only verification. It uses the hash-bound original `setup-typescript-semantic-oracle.mjs` verification to prove the complete original 2,623-file source/compiler/root-manifest receipt before trusting it. It also verifies the exact acquisition receipt and both acquired archives. If the new root exists, it requires exact ownership, prepared/admitted receipts, all source/compiler/declaration byte hashes and known state; uncertain state is retained and rejected.

Root alone runs `--prepare` after reviewing this candidate. It refuses an existing new root and copies the original 2,093 source files, 529 compiler/native files and unchanged root manifest by byte writes, with no hardlinks. It copies the two already acquired raw archives and acquisition receipt into the new owned `archives/` directory. Source/compiler bytes are verified before and after copying, with a fixed owner record and full prepared file receipt. It downloads nothing and extracts no package.

Root alone runs `--admit` afterward. Both source-SRI archives are fully inspected before an admission marker or declaration write. Regular files are copied inertly into canonical `upstream/node_modules/@types/node` and `upstream/node_modules/undici-types`, adding exactly 114 observed package files and retaining the downlevel Node declarations. Only approved regular members can reach the owned destination. An interrupted admission is preserved and cannot be retried or overwritten automatically. The final receipt covers all 2,737 payload files plus exact archive/member/manifests/licenses/source/compiler pins; the original oracle is verified again. `--check` subsequently re-inspects archives and requires the immutable receipt bytes to match regenerated observations.

Exact commands for root review from this worktree or a detached checkout:

```powershell
$nodeDeclRuntime = 'P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe'
$nodeDeclOracle = 'P:/PiSharp/root/Documents/Codex/2026-09-30/task-2/Pi-typescript-semantic-declarations-node-v0.99.1'
& $nodeDeclRuntime tools/SemanticInventory/semantic-node-declarations.test.mjs
& $nodeDeclRuntime tools/SemanticInventory/setup-semantic-node-declarations.mjs --check --oracle $nodeDeclOracle
# Root-only mutations after review; no acquisition/network action occurs here.
& $nodeDeclRuntime tools/SemanticInventory/setup-semantic-node-declarations.mjs --prepare --oracle $nodeDeclOracle
& $nodeDeclRuntime tools/SemanticInventory/setup-semantic-node-declarations.mjs --admit --oracle $nodeDeclOracle
& $nodeDeclRuntime tools/SemanticInventory/setup-semantic-node-declarations.mjs --check --oracle $nodeDeclOracle
```

Actual oracle preparation/admission remains pending. The inert declaration census records 1,246 Node and 157 Undici lexical reference/import candidates with exact offsets and full unchanged member hashes. Those counts include comments/conditional or downlevel source and are explicitly not AST/typechecker dependency edges. Every actual declaration file remains available to the subsequent genuine checker; nothing is replaced with stubs or filtered into fabricated semantic expectations.

A new hash-qualified full-project profile and receipt must target this new root with all mutable outputs under caller-selected fresh scratch. The old fixed-path whole-project profile and captures are immutable. Resolving Node ambient declarations has not yet been measured and does not close the other 34 observed package instances, residual diagnostics, SDK declaration ownership, strict-library validation or exhaustive public type/member/signature/alias closure. All 79 mandatory phase IDs remain in the ledger and all eight phase gates remain OPEN.
