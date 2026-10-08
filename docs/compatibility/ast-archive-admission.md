# Exact compiler and parser archive admission

Six official npm archives and six exact-version metadata bodies were acquired and inspected as inert data. Every archive matches the unchanged Pi v0.99.1 lock's SHA-512 SRI and the registry metadata's SHA-1 shasum. Packaged versions, licenses and required/optional/peer dependency declarations match the frozen lock. This provides artifact/API setup evidence; it does not qualify a parser, execute a compiler, install packages or close the public transitive inventory gate. The owner remains `gpt-6.1-sol`, reasoning effort `xhigh`.

The [admission record](../../compatibility/ast-archive-admission.json) retains all 729 regular member hashes, complete manifest and version-metadata bodies, nine complete packaged notices, actual request/response provenance, literal entrypoint presence, native header observations and bounded API text excerpts. The [inspector](../../tools/SurfaceInventory/inspect-ast-archives.mjs) only reads tar members in memory. It never imports acquired code, extracts members, invokes a subprocess, installs packages or runs a compiler/native binary. The prior [setup recipe](ast-inventory-setup.md) and its [frozen JSON](../../compatibility/ast-inventory-setup.json), root commit `a5370c9e53c694c4d1c99686150e5eb0df8d7d99`, remain unchanged historical research.

## Actual acquired bytes

The raw forensic directory is `artifacts/ast-archive-inspection` in the isolated surface worktree. It was verified absent before acquisition and was created only after all six packages' metadata/archive reads and integrity checks succeeded. Its thirteen files are six archives, six metadata bodies and an acquisition receipt. Writes use exclusive create; no prior evidence was overwritten. The directory is ignored by the repository's existing `artifacts/` rule. Archive contents were never extracted to disk.

| Package | Version | Compressed bytes | Regular files | Regular member bytes | Archive SHA-256 |
|---|---:|---:|---:|---:|---|
| `typescript` | 7.0.2 | 365,612 | 416 | 2,497,498 | `da2513f4b95176d6dde8b51aab7afe8a927656c9d277369793f77f7e59371c08` |
| `@typescript/typescript-win32-x64` | 7.0.2 | 9,776,626 | 113 | 28,365,027 | `61fc4e141d2bc687db580e71bbfa63b9c209f0310645d82ca1b457eb3a24fd19` |
| `@babel/parser` | 7.29.8 | 439,060 | 8 | 2,000,369 | `20f1459b23066ba65d0dec68ee59ff5e2284c72fe22f98a7153a6cccef903c80` |
| `@babel/types` | 7.29.8 | 301,854 | 178 | 2,750,769 | `514a4ed09355fe1417f03e89c5a5f518745e2eb75a4069d25b55a3e7298069de` |
| `@babel/helper-string-parser` | 7.29.7 | 8,383 | 5 | 31,816 | `124565ecd300d7187973060ba6b1d1c4f296e55f0489ddfd6af794f516fed127` |
| `@babel/helper-validator-identifier` | 7.29.7 | 14,245 | 9 | 48,813 | `7a31904bc45f1bb232d5e5dd797d12296f4a27fb4bf0f6d517827700f997dc1f` |

Total compressed archive bytes: **10,905,780**. Registry `dist.fileCount` and `dist.unpackedSize` agree with each package's observed regular-file count and regular-member bytes. The complete original SRI, official exact archive/metadata URLs and actual metadata body hashes are retained in JSON. The acquisition receipt is 24,201 bytes, SHA-256 `e9e2b9618376f2abd6862a70cac6127ae71420cde6234a616e8a4516bc705422`.

The first default-sandbox anonymous metadata read failed before any artifact writes, with an empty underlying socket-error message. The concrete `require_escalated` anonymous read-only request was then accepted by the executor review, and acquisition exited zero. There was no approval-review rejection. All twelve successful requests were HTTPS GETs to `registry.npmjs.org`, status 200, without redirects, authorization headers, client credentials, inherited proxy use or provider calls. Each actual URL, request headers, response raw headers, timestamps, size and body hash is retained. Those observations are not regenerated or normalized during offline verification.

## TypeScript 7.0.2 has a native-backed unstable API

The compiler wrapper archive's actual `package.json` differs materially from the old JavaScript Compiler API assumption:

- Package root `.` exports **`./lib/version.cjs`**, 113 bytes, SHA-256 `b0a2b4a3976dad0f31325bd18eeee8e7d2a1ed674d3e486b1ad5c501207960a9`. Its complete text exports `version` and `versionMajorMinor`. Importing `typescript` at the package root is not the classic `createProgram`/`createSourceFile` compiler boundary.
- Explicit exports provide `./unstable/sync`, `./unstable/async`, `./unstable/fs`, `./unstable/proto` and AST/scanner/visitor/factory utilities. Every literal declared export target inspected is present. These package-defined **unstable** APIs are genuine possibilities; they were not loaded or called here.
- `bin/tsc` imports `lib/tsc.js`. The latter reads `#getExePath` and invokes the resolved executable via `execFileSync` on Windows. Its exact inert text is 609 bytes, SHA-256 `cbdfbf11c26ed00dfc073155e316a42d6d6d8a387be61006b82fa9aa93ac572e`.
- `lib/getExePath.js`, 3,051 bytes, SHA-256 `96c10fd3a18e165d7323de8e314ac8d755ad96811f9fbc304a5b3ba4e05285cc`, constructs `@typescript/typescript-${process.platform}-${process.arch}`, resolves its `package.json`, and locates `lib/tsc.exe` on Windows. It throws when the platform package/executable is absent. Therefore `--omit=optional` plus only the wrapper cannot be assumed to create a usable compiler setup.
- The acquired Windows x64 artifact contains **`lib/tsc.exe`**, 24,520,544 bytes, SHA-256 `f9ecfbdc93753d2c972d66a8d0d75f5bd737fd4a5f88b422d9091ea282bcb2c7`. Its first bytes have a PE/DOS `MZ` header. This identifies a packaged native artifact; its validity, runtime effects and compiler behavior were not tested.

The native-backed API boundary is observable in actual packaged client text and declarations:

| Packaged file | Observed inert boundary | SHA-256 |
|---|---|---|
| `dist/api/async/client.js` | Line 56 spawns `resolveExePath(options)` with stdio pipes; request methods establish/communicate with that API server | `13bf0dbfb818a623e72f119d8cd15fa6ef5b18bf040d216c84971830f7c37c1d` |
| `dist/api/syncChannel.js` | Windows path creates a named pipe and spawns the executable with `--pipe`; POSIX path uses stdio descriptors | `132714a6e2dd7fd079f7f67bf9ed41d4123ab2ab4d94043a34ea3b1a8f1fb001` |
| `dist/api/sync/client.js` | `apiRequest` sends JSON through the synchronous channel | `4cca322e20fff9460d1843a62b2cac4ee963109b4d07e984bed9f54e7baa10b8` |
| `dist/api/options.d.ts` | Published `APIOptions`/spawn options expose `tsserverPath`, `cwd`, filesystem callbacks and timing collection; socket connection has a pipe option | `8a70549485f4a82692fae418c58f5b84b81303b750deb2de397f0a2df85c4f14` |
| `dist/api/sync/api.d.ts` | `API.parseConfigFile`/`updateSnapshot`, project `program`/`checker`, source files, syntax/semantic diagnostics, aliases/exports/base types/properties are declared | `a67066054ffeaf274a49f62e67e3c8b687761d270718ae38e8dc4e1d3530ef1f` |

The declared semantic recipe is consequently `API` -> snapshot -> project -> `program`/`checker`, rather than an invented root `createProgram` call. The packaged declarations include `getSourceFile`, `getSyntacticDiagnostics`, `getSemanticDiagnostics`, `getTypeAtLocation`, `getBaseTypes`, `getPropertiesOfType`, `getAliasedSymbol`, `isUnknownSymbol` and `getExportsOfModule`. Public API access, exact supported options, complete module/declaration closure, diagnostics and disposal still need real qualification after a separately reviewed setup/execution scope.

The AST utility export is not proof of a standalone TypeScript parser. In `dist/ast/factory.generated.js`, the observed `createSourceFile` takes already supplied statements, EOF token, text and names and constructs a node; it does not parse source text as the older compiler function did. This is 144,711 bytes, SHA-256 `330f890fb1a4a072d1abf483f38196e399e40b8a797047157f51f8e7b2368199`. The archive also exposes a lexical scanner and visitor utilities. Their presence alone cannot certify grammar parsing or a semantic inventory.

Both TypeScript version metadata bodies declare repository `microsoft/TypeScript` and `gitHead` `2bd066d87f5bafd315be9f40889d0a60b9e58e0b`. These are publisher metadata tied to the acquired exact-version responses and SRI-locked archive bytes. The corresponding compiler source commit was not separately acquired or verified, and registry signatures/attestations were not verified. It is not a commit-qualified source-capture or legal approval claim.

## Babel is an exact four-package syntax candidate

Actual manifests confirm:

`@babel/parser 7.29.8` -> `@babel/types ^7.29.8` -> `@babel/helper-string-parser ^7.29.7` and `@babel/helper-validator-identifier ^7.29.7`.

The frozen upstream rows resolve those ranges to exactly the four acquired versions. No required dependency, optional dependency or peer dependency is added by their packaged manifests. None of the six packages has a packaged `scripts` property or an install/prepare lifecycle key. The parser has a `bin` declaration, as does the TypeScript wrapper; any future install must disable bin links and invoke only the separately reviewed API boundary. No CLI was invoked here.

`@babel/parser` has CommonJS main `lib/index.js` and types `typings/babel-parser.d.ts`, both present. The JS entry is 513,214 bytes, SHA-256 `6969920ae0610df927b6b3e675d1309372c268e36d391652af8e3e0183cbe8f8`. Its inert text exports `parse` and `parseExpression`. The 9,334-byte declaration file, SHA-256 `e6ca59368dce5a594dcde9bbb6ae640d668fa6c28c31639dd2a75b731bb036a2`, declares these functions, a TypeScript syntax plugin/options and parser controls including `errorRecovery`, `sourceType`, tokens and ranges. This identifies an implementable candidate API, not an executed AST result.

Both Babel helpers' `exports` metadata refers to **`./lib/index.d.ts`**, but the corresponding archive members are absent. Their CommonJS JS entries are present. This absence does not fabricate a runtime parser failure, and it must remain visible in any semantic/type-checking profile. No declaration shim or newer helper version is proposed. Babel `types` has its declared main/legacy declaration entries present; the full semantic reachability of those declaration files is unqualified.

A later reviewed four-package scripts-disabled restore could qualify a syntax-only corpus using the actual parser and TypeScript plugin, fail on parse errors and inventory source declarations/exports/imports/members. It does not need Node ambient types merely to parse source text. Complete TypeScript symbols, imported types, inheritance/intersections/generic instantiations, declaration merging and provider SDK closure remain distinct work. All existing native mandatory event/reducer/context/operation rows remain required.

## Packaged notices and provenance boundary

Complete exact notice text and byte/hash provenance are retained in the admission JSON:

| Package family | Packaged notice | Bytes | SHA-256 |
|---|---|---:|---|
| TypeScript wrapper and Windows artifact, each | `LICENSE` | 9,197 | `a7d00bfd54525bc694b6e32f64c7ebcf5e6b7ae3657be5cc12767bce74654a47` |
| TypeScript wrapper and Windows artifact, each | `NOTICE.txt` | 48,860 | `f5c708b59114507b8b27b48181b6883d106bbca0c1634bbee45b5e344237b66b` |
| TypeScript wrapper | `vendor/vscode-jsonrpc/License.txt` | 1,095 | `ec9ee83580841e8eb687aca9867f221503809ba6426c7f876ede17d91b9fcfd0` |
| Babel parser | `LICENSE` | 1,086 | `2e97627cb278aa7556fb9e8817368302301a595b6c7582512b8d74c57b773652` |
| Babel types and each helper, three copies | `LICENSE` | 1,106 | `117da2af0d4ce0fe1c8e19b5cff9dcd806adf973d328d27b11d4448c4ff24f76` |

The TypeScript notice includes third-party material; the vendored vscode-jsonrpc license has MIT text. These retained bytes improve artifact-specific notice evidence but do not decide legal redistribution closure. Pi itself remains the separately verified pinned **MIT** baseline. No existing license matrix, notice, provenance or gate record was edited. npm/bootstrap/internal tool notices and compiler/vendor source authority remain their own requirements.

## Inspector bounds and verification

The reader enforces 32 MiB compressed per archive, 128 MiB total compressed, 192 MiB expanded per tar, 160 MiB per member, 12,000 headers, 32 path components, 1,024 path UTF-8 bytes, 255 component bytes, 64 KiB PAX, 2 MiB metadata/notice text and 8 MiB scanned API text. JSON uses the existing pinned strict duplicate-rejecting/lossless numeric admission parser. UTF-8 text decoding is fatal on malformed bytes; metadata and complete notices retain exact decoded characters and original byte hashes, including CRLF.

Tar validation checks checksum/numeric encoding/bounds/alignment/padding/terminal blocks, package-root confinement, absolute/traversal/backslash/NUL/reserved Windows names, duplicate/case collisions and file-parent conflicts. Links, devices, global PAX and unsupported extensions are rejected. Supported local PAX is bounded and limited to explicitly listed attributes. All observed six archives have no PAX metadata headers; unsupported archives fail rather than being interpreted speculatively. Native regular-file bytes are permitted **as inert evidence**, never extracted or executed.

The default invocation reparses all six retained archives and compares byte-identical generated admission JSON with the frozen record, including its historical acquisition timestamps/headers. It does not fetch or rewrite. Two fresh offline default processes actually exited zero and reproduced the identical record, SHA-256 `b019f67110323622948e54771e015f7fbe2e8a4d1ec99ed1be450064f914c3e2`. This is developer evidence, not independent acceptance. `--raw-root` provides an explicit read-only path for an isolated review checkout; file hashes, SRI, record tree and admission comparison still apply. `--acquire` refuses an existing raw root or admission file before any network request. No updater/regenerator exists.

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  'tools\SurfaceInventory\inspect-ast-archives.mjs'
```

From a detached review checkout, retain its own committed tool/admission/recipe/dependency files and point only the raw evidence read to:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  'tools\SurfaceInventory\inspect-ast-archives.mjs' --raw-root `
  'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\PiSharp-native-extension-surface-work\artifacts\ast-archive-inspection'
```

The optional `--self-test` mode tests synthetic tar attacks and limits plus invalid UTF-8/duplicate JSON without network, extraction or acquired code execution. Its pure parser probes are separate from real parser/compiler and semantic census qualification. Root owns independent execution/review of those probes and the accepted candidate.

## Next stages and unresolved gates

The smallest next syntax setup is the admitted four-package Babel candidate, followed by real TypeScript-plugin corpus and complete AST syntax inventory. The genuine pinned semantic alternative requires the actual TypeScript wrapper plus exact Windows package and explicitly reviewed **native compiler/API-server execution**. The packaged public API can use supported `tsserverPath`, `cwd` and filesystem callbacks; no private function extraction, imported SDK shim, compiler substitute or source transform is needed or permitted. A later guard must narrowly admit that pinned executable/IPC and deterministic cleanup, rather than silently bypass the existing reference guard's process prohibition.

Neither path is installed or executed now. Exact `@types/node 22.19.19`/`undici-types 6.21.0` remain unacquired prerequisites for full semantic configuration. Real resolver diagnostics must determine the complete public SDK/library/optional-peer declaration closure, preserve nested package identities, enumerate all export conditions/patterns/aliases/overloads/namespace/inheritance/intersection branches and retain missing/computed/dynamic cases as blockers. `skipLibCheck:true`, unavailable types and Babel helper declaration absences cannot be relabeled complete semantic proof.

P1-04 AST/transitive completeness, all 712 mandatory native inventory rows, native SDK ABI/syntax decisions, P6/P7 integration, redistribution/legal approval and all eight-phase parity remain open. This candidate provides exact acquired artifact and inert API evidence only.
