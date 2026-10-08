# Pinned AST inventory setup research

This is dependency and implementation research for the open `P1-04-AST-transitive-census` blocker. It installs nothing, executes no parser/compiler or upstream module, and approves no compatibility profile. The author remains `gpt-6.1-sol`, reasoning effort `xhigh`. The complete eight-phase scope and all 712 required native inventory rows remain mandatory. P1-04, native API/ABI approval, P6/P7 gates and full parity remain **HOLD**.

The [machine-readable setup record](../../compatibility/ast-inventory-setup.json) contains exact unchanged lock entries, official archive URLs and SHA-512 SRI, source hashes/configuration, fourteen workspace manifests, existing oracle availability, staged commands and implementation requirements. It is a research record, not an executable installation plan or a qualified parser result.

## Concrete finding and proposed first step

The pinned root [package manifest](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/package.json) and [lockfile](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/package-lock.json) select **TypeScript 7.0.2**. Its lock entry declares twenty platform-specific optional `@typescript/typescript-*` packages. The Windows x64 entry is also 7.0.2. This metadata does **not** establish that the familiar JavaScript `createSourceFile`, `createProgram` and `TypeChecker` APIs are packaged. Nor does it prove the package contains only a native compiler. Actual archive/API inspection is required before choosing the compiler boundary.

No `typescript`, `@typescript`, `@types/node` or `@babel/parser` package directory exists in the five qualified oracle dependency roots inspected: minimal reference, Responses SDK, session context, edit and reviewed Anthropic SDK. Their immutable qualified receipts are referenced by hash. The upstream public clone has no `node_modules`. The Codex Node runtime has no top-level compiler and no matching compiler/parser directory in its `.pnpm` store. Its bundled `@types/node` **25.9.5** and `undici-types` **7.24.6** differ from the pinned versions and cannot substitute for baseline declarations. These are bounded availability reads, not a new recursive verification of every installed tree.

The smallest useful staged alternative is the exact **four-package Babel parser closure already present in the same upstream lock**. It could produce an AST syntax census without `@types` or provider SDK installation. It cannot resolve TypeScript semantic aliases, inherited/generic members or the full transitive public contract.

The recommended next reviewed action is **inert acquisition/inspection of six exact archives**: the TypeScript wrapper, the selected Windows x64 artifact and the four Babel packages below. Verify SRI before reading members; inspect actual package metadata, parser/compiler APIs, lifecycle/native/bin entries and complete LICENSE/NOTICE text. Acquire no other platform packages. Root can then select the first executable stage and authorize its dedicated helper/restore scope. This research performed no acquisition and encountered no executor approval rejection.

## Exact candidates and authority

All versions, URLs, ranges and declared licenses below come from the pinned lock. No registry response, archive bytes, packaged notice, compiler API or lifecycle assertion was newly acquired or verified here. Exact SRI values and the full original rows, including all twenty TypeScript platform alternatives, are retained in JSON.

| Package | Version | Exact source lock path | Declared license | Purpose |
|---|---:|---|---|---|
| `typescript` | 7.0.2 | `node_modules/typescript` | Apache-2.0 | Pinned compiler wrapper; API/platform admission pending |
| `@typescript/typescript-win32-x64` | 7.0.2 | `node_modules/@typescript/typescript-win32-x64` | Apache-2.0 | Candidate for the observed Windows x64 runtime profile; architecture must be checked before setup |
| `@types/node` | 22.19.19 | `node_modules/@types/node` | MIT | Required by pinned `types: ["node"]`; semantic prerequisite |
| `undici-types` | 6.21.0 | `node_modules/undici-types` | MIT | Sole declared dependency of that Node type package, via `~6.21.0` |
| `@babel/parser` | 7.29.8 | `node_modules/@babel/parser` | MIT | Syntax-only parser candidate; requires `@babel/types ^7.29.8` |
| `@babel/types` | 7.29.8 | `node_modules/@babel/types` | MIT | Requires the two helpers below, both via `^7.29.7` |
| `@babel/helper-string-parser` | 7.29.7 | `node_modules/@babel/helper-string-parser` | MIT | No required dependencies in its lock entry |
| `@babel/helper-validator-identifier` | 7.29.7 | `node_modules/@babel/helper-validator-identifier` | MIT | No required dependencies in its lock entry |

Exact official archive endpoints for the first inspection stage are:

- [TypeScript 7.0.2](https://registry.npmjs.org/typescript/-/typescript-7.0.2.tgz)
- [TypeScript win32-x64 7.0.2](https://registry.npmjs.org/@typescript/typescript-win32-x64/-/typescript-win32-x64-7.0.2.tgz)
- [Babel parser 7.29.8](https://registry.npmjs.org/@babel/parser/-/parser-7.29.8.tgz)
- [Babel types 7.29.8](https://registry.npmjs.org/@babel/types/-/types-7.29.8.tgz)
- [Babel string parser 7.29.7](https://registry.npmjs.org/@babel/helper-string-parser/-/helper-string-parser-7.29.7.tgz)
- [Babel identifier validator 7.29.7](https://registry.npmjs.org/@babel/helper-validator-identifier/-/helper-validator-identifier-7.29.7.tgz)

Node and Undici type archives are separately pinned for a later semantic stage:

- [Node types 22.19.19](https://registry.npmjs.org/@types/node/-/node-22.19.19.tgz)
- [Undici types 6.21.0](https://registry.npmjs.org/undici-types/-/undici-types-6.21.0.tgz)

`--omit=optional` alone must not be assumed to produce a usable TypeScript 7.0.2 compiler. If actual inspection establishes a platform binary/client API requirement, the future setup must explicitly admit only the exact selected platform artifact. Executing a packaged native compiler is a separate concrete effect to review; it is not a native build. No unpinned TypeScript 5/6 fallback is proposed. If the locked package exposes no usable AST/checker API, retain that blocker and use a separately approved syntax-only stage rather than relabeling it semantic proof.

## Semantic dependency boundary

The compiler/platform pair and Node/Undici type pair are **four exact prerequisite candidates**, not a complete public TypeChecker closure. The JSON retains all thirteen locked `@types` package-instance rows plus `undici-types`, including nested `p-retry/node_modules/@types/retry` 0.12.0 and root `@types/retry` 0.12.5. They have different source/test/example demand; this is not blanket installation authorization.

Source-facing examples include `@types/cross-spawn` 6.0.6, `@types/hosted-git-info` 3.0.5, `@types/proper-lockfile` 4.1.4 and its root `@types/retry` dependency, and `@types/semver` 7.7.1. Test/example profiles can require Chai/deep-eql, Estree, ms and other rows. The exact declaration demand must come from real resolution/diagnostics rather than package-name guesses.

Public [AI exports](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/index.ts) include provider options whose source modules import SDK declarations. The manifest selects Anthropic 0.124.0, OpenAI 7.19.0, Bedrock 3.1127.0, Google GenAI 2.21.0 and Smithy node-http-handler 4.12.1. The OpenAI 7.19.0 identity is **`packages/ai/node_modules/openai`**; root `node_modules/openai` is **6.40.0**, and a custom-provider example separately selects Anthropic **0.52.0**. Any semantic host must preserve these package-instance identities and conditional export resolution. Relocating all SDK types into one implicit root would lose source resolution evidence.

The currently qualified oracles cover only selected source runtime seams and their restored dependencies. They do not supply the whole Google/AWS/Smithy declaration closure or all source library types. Optional peers may still be referenced by declarations even when unnecessary for an offline request capture. In particular, SDK optional-peer metadata is not proof that its type imports can be ignored. Native/wasi/renderer packages may contribute declaration files without their runtime being executed. Resolve that demand explicitly before requesting additional exact archives.

The source lock flags install scripts on Google GenAI 2.21.0 and esbuild 0.28.2. Neither script may run in the proposed setup. Required type files must be present in inspected published archives; an absent generated declaration remains a blocker. This research neither proposes a broad workspace `npm ci` nor claims a minimal full checker closure has been computed.

## Runtime, confinement and future command

Use the already pinned absolute Node **v24.19.0** executable:

`P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe`

SHA-256: `3602f2bb1a10f2cbab4c36886218a33c1ab3db87290e73b033c46c77147d0237`.

Reuse only the already verified **npm 11.6.2** bootstrap/archive, SHA-256 `585f95094ee5cb2788ee11d90f2a518a7c9ef6e083fa141d0b63ca3383675a20`, 2,663,834 bytes. Copy verified read-only inputs into a fresh proposed sibling:

`P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-ast-inventory-oracle-v0.99.1`

It did not exist at research time and was not created. A future setup helper must own fresh `archives`, `tools/npm-11.6.2`, `cache`, `config`, `home`, `tmp`, `upstream` and `inventory` directories. Refuse non-owned/nonempty targets, never share a writable cache with an accepted oracle, preserve uncertain writes, and verify resolved ownership confinement before cleanup. Source copying must retain canonical bytes and avoid hardlinks. No existing oracle/cache/source/receipt/golden is changed.

Children need an allowlisted credential-free environment: task-local home/profile/appdata/temp/config/cache; no inherited provider/OAuth secrets, proxy configuration, `NODE_OPTIONS`, `NODE_PATH` or npmrc. Use absolute executable paths and no global PATH/package mutation. An AST/parser or checker reads source/declaration files; it does not import upstream modules, instantiate SDKs or invoke providers. No upstream `prepare`/husky, build, generation, catalog or native lifecycle runs.

After a separately reviewed helper has inspected the four Babel archives and created an exact lockVersion 3 projection, the proposed restore vector is:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-ast-inventory-oracle-v0.99.1\tools\npm-11.6.2\bin\npm-cli.js' `
  ci --ignore-scripts --omit=optional --no-bin-links --no-audit --no-fund `
  --registry=https://registry.npmjs.org/ `
  --cache=P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-ast-inventory-oracle-v0.99.1\cache `
  --userconfig=P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-ast-inventory-oracle-v0.99.1\config\npmrc `
  --globalconfig=P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-ast-inventory-oracle-v0.99.1\config\empty-global.npmrc
```

Its working directory is that fresh sibling, with a private manifest depending only on `@babel/parser: "7.29.8"` and a lock containing the exact four required upstream rows. **This command is design-only: the helper, layout and lock do not exist, and it was not executed.** The TypeScript restore vector is intentionally unresolved until archive/API/platform admission; blindly applying Babel's `--omit=optional` profile would be incorrect.

Ordinary authorized task-local official archive reads/scoped scripts-disabled restore do not create an extra immediate user-permission question. A concrete network invocation must still satisfy the executor's applicable approval when sandbox sockets are restricted. This research requested no network approval and observed no rejection. Later setup authoring/acquisition/restore and inventory code are separate root-owned decisions.

## Full inventory implementation recipe

1. **Freeze all inputs and roots.** Hash complete canonical source and actual parser/compiler/runtime/npm/dependency artifacts. Enumerate every export key and conditional/default branch, source/types/import/require targets, subpath patterns and legacy main/types fallbacks in all fourteen workspace manifests. Retain source-only, public/published and build profiles separately. Coding-agent `rpc-entry` is bundled through its pinned build script; root config also includes additional source aliases/roots. Record generated target mappings and missing targets rather than blindly rewriting every `dist` path.
2. **Parse the complete admitted syntax.** Use a real admitted API with TypeScript/TSX/JS/declaration modes and exact options. Parse errors or unsupported grammar fail completeness. Census every import/export/declaration/member, including inline nested properties, readonly/optional fields, getters/setters, call/construct/index signatures, overload sets and their implementation separately. Retain exact text, modifiers/JSDoc, declaration order, UTF-16 ranges with UTF-8 byte mapping and raw literal/numeric lexemes. Never derive retained number text from the parser's JavaScript `Number` value.
3. **Build the public export graph.** Follow named aliases, default and namespace exports, type-only exports, export stars, collisions/cycles, import-type queries and literal dynamic imports. Keep every condition/pattern branch. Unresolved/computed module specifiers and runtime export/registration mutations become explicit blocker rows. AST syntax alone cannot prove the runtime mutation result.
4. **Resolve the semantic closure.** Use a supported whole compiler/program/checker or packaged client API, `noEmit` and confined host reads. Preserve source `NodeNext` resolution/paths from the original config as a named profile; this is compiler configuration, not a runtime import shim. Resolve exports/aliases via real symbols, declaration merging/namespaces and contributing origins. Traverse inheritance/intersections/unions, generic defaults/constraints/instantiations, indexed/mapped/conditional/infer/import/utility types and callable/constructable objects. Represent cycles with stable identities, not arbitrary depth-based omissions. Inventory actual external package instance/conditions/type directives and every diagnostic before authorizing exact missing declaration archives.
5. **Prove census sensitivity and repeatability.** Independent counts and deliberate mutations must detect omitted files/exports/members, duplicate aliases, missing overload/inheritance/intersection/namespace records, star ambiguity/cycles, conditional targets and unresolved/computed exports. Two clean offline runs need exact loaded parser/compiler/dependency/source/runtime pins. Preserve source/overload/event order and null/missing/undefined distinctions. Separate syntax evidence, semantic closure, behavioral parity and approval states.

The pinned base config uses `strict`, `erasableSyntaxOnly`, `verbatimModuleSyntax`, `types: ["node"]`, ES2024 and `skipLibCheck: true`; the root overrides module/resolution to NodeNext and enables `noEmit`. Source paths and the original include/exclude lists are retained verbatim in JSON. Original build/config exclusions are evidence, **not** approved compatibility exclusions. A complete semantic library/signature claim additionally needs `skipLibCheck: false` diagnostics or an explicit unsatisfied gate. Skipped library checks, fabricated ambient `any` modules and unresolved imports cannot count as complete type resolution.

The existing [native extension census](../extensions/native-api-inventory.md) remains a qualified bounded lexical inventory. Its direct source declarations, context facades, rich callback state, all event/reducer families and source-linked mandatory work remain required. A syntax AST pass would strengthen that census; it would not qualify inherited/transitive contract members or event reducer behavior. Full P1/P6/P7 and all eight-phase acceptance remain open.

## Evidence and remaining blockers

The source lock is 186,139 bytes, SHA-256 `e245cabcefdd23d1ab3cfd21db492e21ca11b7ef4a20f6304aead4dc2b5b569d`. The retained official release source archive is 8,646,242 bytes, SHA-256 `4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b`. Prior receipts record all 2,093 canonical files and the two declared attribute conversions. This research rehashes its listed metadata/evidence inputs; it does not claim a new complete source or installed dependency-tree verification.

Remaining concrete blockers are the TypeScript 7.0.2 packaged API/platform admission; actual Babel parser API/syntax and packaged notice/lifecycle admission; acquisition of exact Node/Undici baseline types for semantic work; resolver-driven complete public SDK/library/optional-peer declaration closure; and independent completeness/gate review. No package installation, new validator, parser, checker, source runtime or build was run here. Only the two new setup research files were written.
