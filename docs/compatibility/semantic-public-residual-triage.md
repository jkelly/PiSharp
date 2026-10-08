# Complete public semantic residual triage after released catalog admission

The next admissible step is a **fresh, offline, OpenAI-only inert successor**, preserving all 4,581 current payloads and adding the exact official OpenAI 7.19.0 package's 3,548 unchanged members at `upstream/packages/ai/node_modules/openai`. That produces a proposed 8,129-payload successor, including 784 new declarations. Actual whole-config checker captures must determine its result. This proposal supplies no new diagnostic golden, promises no reduction, and does not close P1-04.

This document and its complete census are authored analysis of actual reference evidence. No Node, .NET, compiler/helper, npm, SDK, package runtime, install, build, generator, network operation, Git mutation or oracle mutation ran in this task. PowerShell read files, parsed JSON, inspected inert declaration text and verified the authored analysis. Root owns setup execution, captures, independent qualification and any eventual profile/ledger changes. Requested model routing is Sol6.1/Xhigh; runtime model metadata is unavailable to this author, so selected model execution cannot independently be attested.

## Immutable observation and reproducibility

The qualified input is [`fixtures/semantic-full-project-catalog/expected.json`](../../fixtures/semantic-full-project-catalog/expected.json): **13,305,865 bytes**, SHA-256 `5999081da5235765a1a0dc7090ca770ba32a11e939a824973fb1d3317b7a77df`. The pinned source remains `d86654abb8862e201933517d6f1fce9f88dd117f`, with original lock SHA-256 `e245cabcefdd23d1ab3cfd21db492e21ca11b7ef4a20f6304aead4dc2b5b569d`. Its manifest records the actual released-catalog receipt SHA-256 `2e5fea6e5ed55fdff54dfc507499eb98e0bfba6851a9004d6c5e9b1af09981f4`. Root reports four equal, fresh, complete captures with helper/client exits joined; this author did not run those captures.

[`compatibility/semantic-public-triage.census.json`](../../compatibility/semantic-public-triage.census.json) is **3,574,484 bytes**, SHA-256 `803967f474c4dc51100ad348899a88c3ccfd4b51468e89a542a0dc7388266ff0`. It contains every semantic index **0 through 1,245**, in original order, with the complete original diagnostic object and, for every missing-module record, its complete original unresolved attribution. It also retains all 34 original mandatory lock records and all three original published ownership blocker objects. The derived exact-file, package, code and exact-message aggregates examine every record; examples below do not delimit scope. Its JSON serialization is authored analysis, not a replacement byte format for the raw reference snapshot.

[`tools/SemanticReference/semantic-public-triage.ps1`](../../tools/SemanticReference/semantic-public-triage.ps1) verifies the raw input size/hash before deriving the census, checks all 1,246 original diagnostic/unresolved index relationships, the exhaustive partition and all required scope counts. Its default invocation is read-only and compares the existing census's complete bytes to the fresh static derivation. `-CreateCensus` creates the specific census file exclusively and refuses to overwrite an existing or partial file. No execution of compiler or package code occurs in either mode.

```powershell
& ./tools/SemanticReference/semantic-public-triage.ps1
```

Actual static verification returned `verified-read-only-analysis`, 1,246 records, 711 unresolved records, 675 diagnosed files, 258 exact message strings, and the census byte/hash values above. The first authoring attempts caught a zero-current-row PowerShell collection issue before any census was written; the final helper retains those zero rows and the successful census independently rederives byte-equal. No failed attempt changed reference evidence.

All eight input diagnostic families remain authoritative and unfiltered. Their counts in API order are **0 / 0 / 0 / 0 / 0 / 1,246 / 832 / 0**: configuration parsing, global, program, syntactic, bind, semantic, suggestion and declaration. The unchanged source options include `noEmit`, `skipLibCheck`, NodeNext module/resolution, `resolveJsonModule` and ambient `types: ["node"]`. Original `**/dist/**` and Gondolin configuration exclusions are source facts, not new compatibility exclusions. Zero declaration diagnostics under those options do not qualify declaration or published-package closure.

All 1,661 original roots are present; the actual program has 2,443 source filenames and 735 declaration filenames, including 663 consumed admitted declarations. The 2,093 canonical source rows, 4,581 admitted payloads, 34 original mandatory lock instances, original 43 specifier groups and 150 entrypoint condition rows remain intact. Current unresolved observations cover 31 literal specifiers in 26 instances. There are 23 manifest records, 44 source path-binding rows and 800 candidate module paths. The observed 742 checker module-symbol/export tables contain 7,393 export occurrences, including re-exports and aliases; this is neither a unique public API count nor exhaustive structural type ownership.

## Complete residual census

All **1,246** semantic records occur in **675** files, with **258 exact message strings**. There are **711** code-2307 records in **675** files and **535** other-code records in **143** files. Every non-2307 record shares a file with a missing-module record. Co-location alone cannot establish which missing dependency causes a diagnostic.

| Semantic code | Records | Distinct files | Exact message strings |
| --- | ---: | ---: | ---: |
| 2307 | 711 | 675 | 31 |
| 2322 | 4 | 4 | 4 |
| 2339 | 29 | 8 | 9 |
| 2345 | 10 | 7 | 5 |
| 2347 | 6 | 5 | 1 |
| 2353 | 1 | 1 | 1 |
| 2358 | 1 | 1 | 1 |
| 2366 | 1 | 1 | 1 |
| 2722 | 1 | 1 | 1 |
| 7006 | 366 | 122 | 151 |
| 7019 | 1 | 1 | 1 |
| 7031 | 103 | 25 | 43 |
| 7053 | 4 | 4 | 4 |
| 18046 | 8 | 5 | 5 |

These mutually exclusive **observation families sum to 1,246**. The helper applies the same ordered path/code/text predicates as the previous [leaf triage](semantic-leaf-residual-triage.md): missing module, generated provider wrapper, argument parameter typed `never`, named catalog type, `SandboxConfig`, implicit-any codes, unknown-value code, untyped-generic code, then other. These are reproducible textual observations, not qualified root causes. The generated-wrapper, never-parameter and catalog-named-type families now contain zero records.

| Observation family | Records | Distinct files | Code counts |
| --- | ---: | ---: | --- |
| missing-module | 711 | 675 | 2307:711 |
| implicit-any-callbacks | 470 | 133 | 7006:366, 7019:1, 7031:103 |
| sandbox-inherited-type | 18 | 1 | 2339:17, 2353:1 |
| unknown-value | 8 | 5 | 18046:8 |
| untyped-generic-call | 6 | 5 | 2347:6 |
| other | 33 | 23 | 2322:4, 2339:12, 2345:10, 2358:1, 2366:1, 2722:1, 7053:4 |

| Source package | All semantic | Missing-module | Diagnosed files | Exact messages |
| --- | ---: | ---: | ---: | ---: |
| agent | 122 | 79 | 79 | 39 |
| ai | 369 | 195 | 171 | 91 |
| chord | 49 | 22 | 22 | 19 |
| client | 3 | 3 | 3 | 1 |
| codemode | 9 | 4 | 4 | 7 |
| coding-agent | 582 | 341 | 331 | 117 |
| durable | 41 | 30 | 30 | 11 |
| evals | 18 | 9 | 8 | 12 |
| mcp | 10 | 6 | 6 | 3 |
| protocol | 22 | 3 | 3 | 9 |
| server | 8 | 6 | 6 | 3 |
| session-backends | 6 | 6 | 6 | 1 |
| telemetry | 2 | 2 | 2 | 1 |
| tui | 5 | 5 | 4 | 1 |

Location counts are 1,119 test diagnostics, 105 source diagnostics and 22 example diagnostics. The 535 non-2307 records divide into **465 test / 52 source / 18 example** records. The complete raw-filename census has 675 rows in `byFile`; the complete exact-message census has 258 rows in `byExactMessage`, with counts, diagnosed-file counts and code counts. Paths displayed in this document are addresses beneath the input's `/upstream/` root; raw diagnostic filenames remain unchanged in the machine census and reference snapshot.

Compared to the genuine preceding leaf snapshot, actual semantic counts change **2,259 → 1,246** while unresolved records remain **711 → 711**. Code changes are 2322:9→4, 2339:70→29, 2344:126→0, 2345:811→10, 2698:4→0, 7053:34→4 and 18046:14→8. All other code counts are unchanged. This observed decrease is 1,013 records; it does not justify erasing surviving diagnostics or forecasting future SDK admission results.

## All 34 original mandatory dependency instances

Every current missing-module record has an exact parsed literal and nearest-lock candidate; there are no unparsed or non-lock candidates. The machine census copies the original version, URL, SHA-512 SRI, license declaration, hard/optional/peer edges and bin fields for all original instances. Current zero rows remain obligations rather than exemptions. Actual declaration/package namespace reachability must still be checked.

| Exact lock path | Version | Current 2307 | Literal specifiers and exact counts |
| --- | --- | ---: | --- |
| `node_modules/@anthropic-ai/sandbox-runtime` | 0.0.26 | 1 | `@anthropic-ai/sandbox-runtime`:1 |
| `node_modules/@anthropic-ai/sdk` | 0.124.0 | 4 | `@anthropic-ai/sdk`:3; `@anthropic-ai/sdk/resources/beta/messages/messages.js`:1 |
| `node_modules/@aws-sdk/client-bedrock-runtime` | 3.1127.0 | 1 | `@aws-sdk/client-bedrock-runtime`:1 |
| `node_modules/@google/genai` | 2.21.0 | 6 | `@google/genai`:6 |
| `node_modules/@silvia-odwyer/photon-node` | 0.3.4 | 6 | `@silvia-odwyer/photon-node`:6 |
| `node_modules/@smithy/node-http-handler` | 4.12.1 | 1 | `@smithy/node-http-handler`:1 |
| `node_modules/@smithy/types` | 4.18.0 | 1 | `@smithy/types`:1 |
| `node_modules/@vitest-evals/core` | 0.15.0 | 2 | `@vitest-evals/core`:1; `@vitest-evals/core/node`:1 |
| `node_modules/@xterm/headless` | 5.5.0 | 5 | `@xterm/headless`:5 |
| `node_modules/chalk` | 6.0.0 | 0 | Original row retained |
| `node_modules/cross-spawn` | 7.0.6 | 2 | `cross-spawn`:2 |
| `node_modules/diff` | 8.0.4 | 0 | Original row retained |
| `node_modules/esbuild` | 0.28.2 | 1 | `esbuild`:1 |
| `node_modules/get-east-asian-width` | 1.6.0 | 0 | Original row retained |
| `node_modules/grok-mermaid` | 0.2.3 | 1 | `grok-mermaid`:1 |
| `node_modules/hosted-git-info` | 9.0.3 | 1 | `hosted-git-info`:1 |
| `node_modules/http-proxy-agent` | 9.1.0 | 1 | `http-proxy-agent`:1 |
| `node_modules/ignore` | 7.0.8 | 0 | Original row retained |
| `node_modules/jiti` | 2.7.0 | 3 | `jiti`:2; `jiti/static`:1 |
| `node_modules/marked` | 18.0.11 | 0 | Original row retained |
| `node_modules/minimatch` | 10.2.6 | 2 | `minimatch`:2 |
| `node_modules/ms` | 2.1.3 | 1 | `ms`:1 |
| `node_modules/partial-json` | 0.1.7 | 0 | Original row retained |
| `node_modules/proper-lockfile` | 4.1.2 | 11 | `proper-lockfile`:11 |
| `node_modules/quickjs-wasi` | 3.6.2 | 1 | `quickjs-wasi`:1 |
| `node_modules/semver` | 7.8.5 | 2 | `semver`:2 |
| `node_modules/typebox` | 1.3.27 | 0 | Original row retained |
| `node_modules/undici` | 8.10.2 | 3 | `undici`:3 |
| `node_modules/vitest` | 4.1.11 | 631 | `vitest`:631 |
| `node_modules/vitest-evals` | 0.15.0 | 1 | `vitest-evals/harness`:1 |
| `node_modules/yaml` | 2.9.0 | 0 | Original row retained |
| `packages/ai/node_modules/https-proxy-agent` | 9.1.0 | 1 | `https-proxy-agent`:1 |
| `packages/ai/node_modules/openai` | 7.19.0 | 20 | `openai`:5; `openai/resources/responses/responses.js`:13; `openai/resources/chat/completions.js`:2 |
| `packages/coding-agent/node_modules/@anthropic-ai/sdk` | 0.52.0 | 2 | `@anthropic-ai/sdk`:1; `@anthropic-ai/sdk/resources/messages.js`:1 |

The 631 Vitest records are retained test-source requirements. Its previously measured hard metadata graph is 44 exact instances/58 edges; that is a bounded acquisition/inspection inventory, not a demonstrated minimal declaration graph. The previous triage's 26-seed hard graph has 161 instances/277 edges. Optional peers/native/WASM/lifecycle branches require their own exact inspection and reachability decisions; broad `npm ci`, removing tests, fake declarations and permanent scope reductions are inadmissible substitutes.

Exact already-acquired Anthropic 0.124.0 and its six hard archives remain a separate admissible offline stage, with the existing `standardwebhooks` provenance/redistribution HOLD preserved. Coding-agent's Anthropic 0.52.0 is a distinct, unacquired exact instance. The six locked DefinitelyTyped acquisitions for cross-spawn, hosted-git-info, ms, proper-lockfile, retry and semver remain separate prerequisites, with existing Node/undici-types already admitted. No network acquisition is performed here. AWS/Google/native image, QuickJS, terminal, proxy, esbuild, Mermaid, eval and runtime utility declarations remain named obligations.

## All 52 non-2307 production-source records

The census preserves exact original indices, positions, messages and any nested/related information for every row. These 19 source files comprise the entire current non-2307 production-source residual, not a sample:

| Exact source display path | Records | Code counts |
| --- | ---: | --- |
| `packages/ai/src/api/anthropic-messages.ts` | 3 | 2345:1, 18046:2 |
| `packages/ai/src/api/azure-openai-responses.ts` | 2 | 2339:2 |
| `packages/ai/src/api/bedrock-converse-stream.ts` | 8 | 7006:6, 18046:2 |
| `packages/ai/src/api/google-generative-ai.ts` | 1 | 18046:1 |
| `packages/ai/src/api/google-shared.ts` | 2 | 2322:1, 7006:1 |
| `packages/ai/src/api/google-vertex.ts` | 1 | 18046:1 |
| `packages/ai/src/api/openai-codex-responses.ts` | 1 | 7006:1 |
| `packages/ai/src/api/openai-completions.ts` | 2 | 2339:2 |
| `packages/ai/src/api/openai-responses-shared.ts` | 4 | 2322:1, 7006:3 |
| `packages/ai/src/api/openai-responses.ts` | 2 | 2339:2 |
| `packages/ai/src/api/openrouter-images.ts` | 2 | 2339:2 |
| `packages/chord/src/node/bundle.ts` | 5 | 2322:1, 7031:2, 18046:2 |
| `packages/codemode/src/runtime/worker.ts` | 5 | 7006:5 |
| `packages/coding-agent/src/core/auth-storage.ts` | 2 | 2322:1, 2722:1 |
| `packages/coding-agent/src/core/http-dispatcher.ts` | 1 | 2358:1 |
| `packages/coding-agent/src/modes/interactive/components/mermaid.ts` | 3 | 2366:1, 7006:2 |
| `packages/evals/src/harness.ts` | 3 | 7031:3 |
| `packages/evals/src/report.ts` | 1 | 7031:1 |
| `packages/mcp/src/transports/stdio.ts` | 4 | 7006:4 |

SDK stream values, Bedrock middleware callbacks, esbuild metadata, QuickJS callbacks, Mermaid `Span` exhaustiveness and Undici dispatcher constraints still coexist with absent exact declarations. The 18 example records all concern the sandbox interface inherited from missing `@anthropic-ai/sandbox-runtime`. Auth-storage indices 575/576 still report a possibly undefined `release` function at positions 4559/4607, while index 574 is missing `proper-lockfile`. Google/OpenAI exhaustive `never` assignments and test-source model-capability `never` property diagnostics remain recorded. None is certified an intrinsic upstream error or an automatically resolved dependency cascade. Current semantic codes 2305/2459 are absent; that observation cannot certify every public export.

## OpenAI-only offline admission prerequisite

The current released-catalog oracle has **no** `upstream/packages/ai/node_modules/openai` directory. Its frozen VFS contains only receipted payloads, so a separately installed SDK elsewhere is not a declaration admitted to this whole-config checker. The existing qualified runtime oracle's package is at `../Pi-responses-sdk-oracle-v0.99.1/node_modules/openai`; the exact compressed archive is at `../Pi-responses-sdk-oracle-v0.99.1/archives/openai-7.19.0.tgz`. Reading its manifest/declarations did not load the SDK.

| Existing package evidence | Exact measured or recorded value |
| --- | --- |
| Canonical upstream lock instance | `packages/ai/node_modules/openai`, version 7.19.0 |
| Official locked URL | `https://registry.npmjs.org/openai/-/openai-7.19.0.tgz` |
| Archive bytes / SHA-256 | 2,781,465 / `597532dcb9051ee8aa5edadedb57b798b42add68cc5aec1d67e994d0cfad07f1` |
| Exact upstream SHA-512 SRI | `sha512-MX2s3u2L5racTO0CC/SWpCOasJQBCJrqLKXK+l82cAhdeF8mPMBEe/gxMm0ZFa2xpKpOFLRjxv5afYEZbBXmbQ==` |
| Existing inspection | 16,516 bytes / `43dc4f2d09cea94cdbb0c929fdca6c648c30b1dac7331349b7ed14851de49154` |
| Existing regular member census | 3,548 regular files / 18,477,473 unpacked bytes / 784 declaration filenames |
| Existing package file fingerprint | `bbeea009f26c7ca4d268d364e89ada8da1617d2d63aa0d89ad9b48eb09b8e55a` |
| Existing restored receipt | 21,390 bytes / `e8b26aa89670be23b4e84f7a3f34e987758f738622d6195bcb82a3d498db7fae` |
| Package manifest | 7,323 bytes / `8a855f81669a434ce894e2d1ae2f18e9050e93c57d14091f155cd543056bdc30` |

The manifest's root exports retain CommonJS `require.types: ./index.d.ts` and ESM `types: ./index.d.mts`; `./resources/*.js` exports map to their exact `.js` files. These observed declaration partners exist unchanged:

| Exact packaged declaration path | Bytes | SHA-256 |
| --- | ---: | --- |
| `index.d.ts` | 831 | `ff10696df0fa40595b148e54072d9ee039d57ad6b1a040137c1e9288814f6d01` |
| `index.d.mts` | 840 | `4972e16545016da08ceed5896878a17cfba01492a0ef8d61ae9133c8d48a3d6b` |
| `resources/responses/responses.d.ts` | 337108 | `17e2695d6dff0ee316379534f9f0a231203e5b6efe1d8055737caebc71d8b0a5` |
| `resources/responses/responses.d.mts` | 337122 | `c8410a0c0fb566bbc78bb54c80f6555e83df93519e35bbdd0028f59d05a16f58` |
| `resources/chat/completions.d.ts` | 81 | `ade6780d037d165e57490a40cb89fb1db325e83339f50b2281b91eaa176f8d2c` |
| `resources/chat/completions.d.mts` | 83 | `0fe5c37d7908df602309dbb30cbb7cbd7ec4b19f9fd01da7683dfca87e576bbe` |

The present 20 OpenAI missing imports are **11 production / 9 test** records across **15 exact files**. Production indices are 127, 128, 151, 153, 154, 157, 158, 163, 164, 167 and 168; test indices are 201, 203, 205, 238, 383, 387, 394, 399 and 403. The current source config uses real nearest-package NodeNext resolution for these specifiers; it contains no OpenAI path alias. A future capture must show which exact `.d.ts`/`.d.mts`/`.cts` files enter the real program and which diagnostics remain or appear. This static candidate mapping is not a checker resolution result.

All 3,548 package members, including runtime source/JS/maps and notices, must remain inert exact bytes. The inspected package declares Apache-2.0; its notices are:

| Exact notice member | Bytes | SHA-256 |
| --- | ---: | --- |
| `LICENSE` | 11336 | `636eb7d79da9bb6d515a4b3fd417aa26679eb3cf16396ddab4bc55fa74e616e4` |
| `src/_vendor/partial-json-parser/LICENSE` | 1075 | `cd519ad3d7e012427f978dfb2e3b92ee403d189d7b859ba6bf68fd7e12ca456f` |
| `src/_vendor/zod-to-json-schema/LICENSE` | 746 | `7beaa85b57d7211f6684af6c1d97e6d9ef7ec1851f779152043a47a8700306a9` |
| `src/internal/qs/LICENSE.md` | 1588 | `d8c77eaffed7f1f874b97f66ee47a557ae24fd59bae8ae14f9b1b84f26a94d2f` |

Its scripts/optional peers stay in the unchanged manifest; inert admission runs none. Declaration text includes actual nonrelative imports of `ws`, `zod/v3`, `undici` and `stream/web`. Examples are `resources/responses/ws.d.ts`, `internal/ws-adapter-node.d.ts`, `_vendor/zod-to-json-schema/Options.d.ts`, and `internal/auth/x509-transport-capability.d.ts`; corresponding `.d.mts` files also retain these imports. `internal/shim-types.d.ts` imports the Node builtin `stream/web`, which has already-admitted Node declarations. The `#x509-transport-state` package import's types condition points to unchanged `src/internal/auth/x509-transport-state.cts`; that source path must not be replaced with a fabricated stub.

The exact root lock records already identify **ws 8.21.0**, **zod 3.25.76**, **undici 8.10.2**, **@aws-sdk/credential-provider-node 3.972.82** and **@smithy/signature-v4 5.7.3**. Root `@types/ws` and `@smithy/hash-node` instances are absent. OpenAI declares all six peers optional, but declaration/public API reachability can still make them obligations. Acquired archives for those follow-up candidates are absent in the reviewed sets. No version or ambient declaration may be invented for the absent peers. Root must inspect exact declaration reachability after this one-package admission and retain any newly exposed diagnostics before deciding the next stage. A regex declaration-text census cannot prove minimal or sufficient transitive closure.

The future setup must verify compressed SHA-256/SHA-512, strict member names/kinds/sizes, manifest identity, notices and every regular member; bind a complete member receipt; preserve all predecessor pins; create a fresh exclusive caller-owned successor disjoint from every prior oracle; deny symlinks/traversal/overwrite; and admit only the original 3,548 files at their canonical nearest-lock location. Neither the installed SDK directory nor its prior summary is a substitute for fresh archive admission. The whole original source/config/compiler and all existing declarations/catalogs must remain byte-equal. No symlink, alias, JSON/config edit, source transform or package runtime import is needed for this proposal.

## Public, profile and platform ownership still required

All 150 public entrypoint condition rows remain present. These three original target objects retain `mandatoryMappingOrOwnershipBlocker: true`:

| Manifest / exact field | Literal runtime target | Actual publication evidence |
| --- | --- | --- |
| `packages/ai/package.json`, `bin.pi-ai` | `dist/cli.js` | Acquired exact archive member: 4,762 bytes / `15efe3d849331391ced3f38e55f53609a7952e8359229c8b65c1172e8e9608a5`. Source/declaration ownership and CLI execution are unqualified. |
| `packages/coding-agent/package.json`, `bin.pi` | `dist/bundle/cli.js` | Exact 0.99.1 metadata/SRI exists; archive unacquired. Actual member presence, bytes and packaged notices remain unknown. |
| `packages/tui/package.json`, `main` | `dist/index.js` | Exact 0.99.1 metadata/SRI exists; archive unacquired. Actual member presence, bytes and packaged notices remain unknown. |

The previous false direct-target flags remain preserved separately. Literal runtime fields, source targets, package export/type conditions, actual published members and behavioral ownership are distinct evidence. No `dist`→`src` spelling rule proves ownership. The acquired pi-ai archive is not admitted into this checker VFS; official coding-agent/tui and other published artifacts still require exact acquisition and inert inspection. Structural signatures, alias/re-export owners, namespaces, inaccessible/dynamic cases, all SDK declarations and platform/RID dependencies remain required inventory work.

The [architecture](../architecture.md), [Phase 1](../plans/01-compatibility-target.md), existing [profile/platform draft](profile-platform-matrix.md), `native-v1` and `native-foundation` profile records remain unchanged. Native-v1 retains 45 mandatory seed requirements and mandatory Deferred rows block release. Native-foundation advertises development evidence only and is not a scope reduction. No feature becomes Supported, Intentionally different, Deferred or Out of scope through this census.

| Phase dependency | Required ownership/evidence preserved by this handoff |
| --- | --- |
| P1 | Complete artifact/public surface inventory, release profiles/differences, source-linked rows and independent owner review precede full profile acceptance. |
| [P2](../plans/02-provider-layer.md) | All provider/auth/model transport capabilities, SDK mapping, errors/cancellation and deterministic request/stream fixtures. |
| [P3](../plans/03-agent-loop-and-tools.md) | Authoritative barriers/ordering, tools and nested policy, MCP/codemode dependencies, per-OS shell/path/backend behavior. |
| [P4](../plans/04-sessions.md) | File/wire session schemas, migrations, committed-state ownership, unknown fields and persisted recovery/settlement. |
| [P5](../plans/05-headless-rpc-terminal-ui.md) | SDK/CLI/RPC/UI mode capability contracts, runtime entrypoints, terminal/image/fallback and process/input cleanup. |
| [P6](../plans/06-native-extension-sdk.md) | Public DTO/API signatures, native registration/events/reducers, plugin ABI/lifetime, provider/TUI/native dependency separation. |
| [P7](../plans/07-node-bridge.md) | Exact unmodified extension corpus, loader/public aliases, versioned protocol and declared bridge subset; no mandatory Node dependency in native runtime. |
| [P8](../plans/08-hardening-and-packaging.md) | Per-row platform evidence, clean Node-free native distribution, provenance/notices, real plugin samples and release acceptance. |

Windows x64, Linux x64 and macOS arm64 qualification remains required; no cross-platform capability follows from this Windows compiler observation. Minimum OS/terminal versions, VM/search/secret-storage/native-library choices, bridge runtime/tier certification and complete behavior/parity evidence remain owner work. P1-04 and all eight phase gates remain open. The blocker is incomplete qualified inventory/evidence, not a request for user permission.
