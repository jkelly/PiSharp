# Third-party notices

The CLI's embedded Models/*.json (all 42 provider catalog shards, amazon-bedrock through zai-coding-cn) are byte-for-byte copies of `package/dist/providers/data/*.json` from the Pi AI 1.5.0 package `@earendil-works/pi-ai@1.5.0` (SHA256 e320fe7bd2b927782ba9a37d356fe5837f1aa96a46432610e1b3b9ccb748ea5c, npm integrity sha512-VeWxNqTHSqRCf8AcY/6kvNwh+neRL6MPxZPePVztIQy6qqK9MR/YoCSN/ZgDnIyCD3pqEXgRyQbzk4KV4uvjbg==). Each matches its hash in the package's `providers/data/.manifest.json` (SHA256 50e0e74cb54dada0a6972c2619e56e99830ee01857810719b6b913cd29e04e87, generatedAt 2026-11-02T10:43:28.346Z); the hashes are recorded in `BuiltinModelCatalog.ShardHashes` and verified before parsing. They replace the earlier openai, openrouter and anthropic shards from the Pi AI 0.99.1 package (SHA256 f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3) and the earlier mistral.json copied from the v0.99.1 source catalog. The Pi MIT notice below applies. Catalog metadata is separate from live provider qualification.

The native parsed Anthropic Messages mapper substantially adapts public Pi `packages/ai/src/api/anthropic-messages.ts`, under the Pi MIT notice below. Authored offline DTO tests establish native behavior separately from genuine provider/SDK reference observations. No Anthropic SDK, Node runtime or network dependency is included in the native mapper.

Native edit matching and replacement substantially adapt public Pi `packages/coding-agent/src/core/tools/edit.ts`, `edit-diff.ts` and `utils/text.ts`, under the Pi MIT notice below. The source hashes are `31a368c14cf5437ecac157765660001b16063302d9bd746303d6d1b1dcb5625d`, `f85a9809eb44b9828236050cf38a8e45933e5cfd583a186e9a0e55a664dd3e8d` and `bb323f9607c499115b532021f4f0163c6df04d834aee9c32c3c77b273bd2d7c6`. Its bounded native Myers formatter is an independently authored algorithm; exact upstream jsdiff formatting remains to be qualified. No Node dependency is bundled with the native edit adapter.

PiSharp's current porting baseline is public Pi version 1.5.0, commit `f9a26365568127461c47a07a37dcb42c140a3ac5` (see `compatibility/target.lock.json`). The adaptation records in this file name the upstream version and source hashes each adaptation was derived from; a record is updated when its source is re-ported against the current baseline. Records for version 0.99.1 remain accurate for code that has not been re-ported.

PiSharp contracts, assistant stream reduction, parsed Responses text/function mapping, transcript projection, bounded catalog/request construction, append-only loop continuation and tool-batch scheduling substantially adapt public Pi types and behavior from [earendil-works/pi](https://github.com/earendil-works/pi), version 0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`. Relevant sources are `packages/ai/src/types.ts`, `packages/ai/src/utils/assistant-message-frame.ts`, `packages/ai/src/utils/event-stream.ts`, `packages/ai/src/api/{openai-responses,openai-responses-shared}.ts`, `packages/ai/src/api/transform-messages.ts`, `packages/ai/src/utils/{transcript,text,hash}.ts`, and `packages/agent/src/{agent-loop,types}.ts`. The Responses source SHA-256 is `85db12efcd109d505c98846e34c45cb4a3260edcee88ef28c8afd1db20eac26a`; additional projector source pins are retained in `tools/PiReferenceRunner/responses-replay-lock.json`. The development reference runner imports a separate unmodified public checkout. The following upstream notice is retained for copied or substantially adapted material.

```text
MIT License

Copyright (c) 2025 Mario Zechner

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Inputs under `fixtures/pi-v0.99.1/providers` and `reference-inputs` are newly authored synthetic data covered by the PiSharp repository license. Expected files under `reference-oracle` record only the pinned upstream queue/terminal seam using those synthetic inputs. Authored provider fixture expectations are separately labeled and are not upstream captures.

The pure native tool output head/tail primitive substantially adapts public Pi `packages/coding-agent/src/core/tools/truncate.ts`, SHA-256 `8e4507c3ed7ca7548cf7c2d7f77d07f2ae63a38b756789cbb44e9062db6c8762`, covered by the Pi MIT notice above. Its authored boundary corpus and unchanged upstream observations are distinguished in the tool-truncation fixture manifest and provenance. No Node package is required by the native primitive.

The native read/write tools and explicit path resolver substantially adapt the pinned public `packages/coding-agent/src/core/tools/{read,write,path-utils,paths}.ts`, covered by that same Pi MIT notice. Source SHA-256 pins are respectively `297d26092979651091f44de8862a6fd55aaf59e7252f8fb8dd3d7cb3f9771518`, `2ee0a438c24cf83fefd733e349d55a21aa5b633c18934b2c80759fcf14b6f297`, `ab6f420d2388a41366113c17a25e356867820b87b02a169d044cfcf83e1997d3`, and `64c3ebef724fa21ed0042e127908b4323a5d2f1b8aaa91eee550442332fe8502`. The first native text profile has explicit unfinished content and filesystem identity requirements; it does not bundle upstream packages or qualify full built-in tool parity.

Native session record, migration and stored context semantics substantially adapt public `packages/coding-agent/src/core/session-manager.ts` and `messages.ts`, under the Pi MIT notice above. Complete source/loaded module pins and unchanged observed outputs are retained in `fixtures/pi-v0.99.1/session-context/oracle.lock.json`. Native flush/checkpoint hardening and explicit migration profiles remain separately documented. Development-only session imports use eight exact official upstream-lock packages; archive/license bytes and install provenance are retained in `compatibility/session-context-oracle-setup.json`. Those packages and their runtimes are not bundled with the native product; complete transitive/license/signature review remains open.

The standalone native streaming JSON preview substantially adapts public Pi `packages/ai/src/utils/json-parse.ts` (covered by the Pi notice above) and the bounded parsing behavior of [partial-json](https://github.com/promplate/partial-json-parser-js), version 0.1.7. The original `dist/index.js` SHA-256 is `23f750aa1170c830b7ef180135852317886cc58ce7d2597e5eeee044539072c7`; `dist/options.js` is `779b2aded6d4e2ec6171ea9d5d5d97551e8c3258b6e78a01d2e9fc6235865b36`. The C# port has no JavaScript runtime dependency. Its original license is retained:

```text
MIT License

Copyright (c) 2023 Promplate Dev Team

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

Separate agent, continuation, frame, JSON-preview, parsed Responses and Responses transcript replay fixture families record behavior observed from the unchanged pinned public source with newly authored synthetic inputs. The Responses model rates are authored test values. The awaited-loop and continuation captures explicitly disclose their harness clock overrides. These captures do not qualify production provider authentication, full HTTP/SSE equivalence or catalog prices. Source/dependency and fixture provenance is retained in their manifests and development-only locks.

No upstream npm package, native asset, font or Node runtime is bundled in the native product. Inert official release-source and pi-ai package archives are retained separately as development evidence under `artifacts/released-baseline` and `artifacts/released-npm-ai`; their native/Wasm payloads are neither extracted nor executed. The pi-ai tarball contains no named license file, while its package/README/registry declarations state MIT; the verified complete source license above is separate authority. Transitive, generated-data, native-asset and redistribution review remains open before packaging. MIT copyright permission does not settle project naming or trademark rights; that review remains separate.

The Node extension bridge installs, on first use and with the user's npm, Pi 1.2.0's `@earendil-works/pi-coding-agent`, `pi-ai`, `pi-tui` and `pi-agent-core` with `typebox` 1.3.27 and `jiti` 2.7.0, and from 1.5.0 overrides `undici` (MIT) to 8.12.3, the version Pi 1.5.0 pins. These packages are downloaded from the npm registry into the user's agent folder and checked against its integrity; none is bundled in the tool or a package.

Selected OS access behavior and error categories in `src/PiSharp.Tools/Files/LocalFileAccessProbe.cs` substantially adapt [libuv 1.52.1](https://github.com/libuv/libuv/tree/v1.52.1) `src/win/fs.c`, `src/win/error.c` and `src/unix/fs.c`. The native port has no libuv or Node binary dependency. The current Windows metadata/ReadOnly and finite error mapping profile has developer tests; Linux/macOS branches and the full error table remain unqualified. Both upstream copyright statements and the MIT permission notice are retained:

```text
MIT License

Copyright (c) 2015-present libuv project contributors.
Copyright Joyent, Inc. and other Node contributors. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Pi v1.1.0 sync adaptations

Native Anthropic workload identity federation (`src/PiSharp.AI/Authentication/AnthropicWorkloadIdentityFederation.cs`) adapts the token exchange, token cache and bearer handling of the Anthropic TypeScript SDK, `@anthropic-ai/sdk` 0.129.0 (`src/lib/credentials/{oidc-federation,token-cache,types,identity-token}.ts` and the token-auth part of `client.ts`), which is distributed under the MIT License by Anthropic, PBC. No SDK code or package is bundled; the adaptation is a native reimplementation covered by authored offline tests.

Native shell output sanitizing (`src/PiSharp.Tools/Processes/ShellAnsiText.cs`) adapts the patterns of `ansi-regex` and `strip-ansi`, MIT License, Copyright (c) Sindre Sorhus <sindresorhus@gmail.com> (https://sindresorhus.com), as used by Pi `packages/coding-agent/src/utils/ansi.ts`.

Both MIT licenses grant the same permissions, under the same conditions, as the Pi MIT notice above.

The CLI ships Pi v1.5.0 `packages/coding-agent/docs/llama-cpp.md` unchanged as `docs/llama-cpp.md` (source `src/PiSharp.Cli/docs/llama-cpp.md`), under the Pi MIT notice above.

## Pi v1.5.0 configuration schemas

`src/PiSharp.CodingAgent/Schemas/{models,settings,keybindings,theme}.schema.json` are byte-for-byte copies of Pi v1.5.0 `packages/coding-agent/schemas/*.schema.json` (commit `f9a26365568127461c47a07a37dcb42c140a3ac5`; git blobs `c51a82c358ef1d92d4b2a2481be1e9d37258057e`, `c331ae27a714e2fca19f9bb7b95f67f7f2f6e367`, `aa3b3e9be501475ce465855ee676abe32b8b9c2e` and `95350a58db931d116d982d3212dbfd2c5c946760`; the keybindings and theme schemas are unchanged since v1.2.0), embedded to validate models.json, keybindings and themes, under the Pi MIT notice above. The built-in `dark.json` and `light.json` themes are the v1.2.0 files, unchanged in v1.5.0.

## Pi v1.5.0 JSON Schema validation (pi-schema)

`src/PiSharp.Contracts/Schema/` ports Pi v1.5.0 `packages/schema` (pi-schema, commit `f9a26365568127461c47a07a37dcb42c140a3ac5`), which validates and coerces tool arguments, under the Pi MIT notice above. Its format checks (`SchemaFormats.cs`) are adapted, through pi-schema's `src/formats/typebox/`, from TypeBox 1.3.27, MIT License, Copyright (c) 2017-2026 Haydn Paterson (pi-schema ships the license as `LICENSES/typebox-LICENSE`); the TypeBox 1.3.27 Compile port that `src/PiSharp.Agent/TypeBoxJsonSchema.cs` uses for the configuration files is under the same license. Both MIT licenses grant the same permissions, under the same conditions, as the Pi MIT notice above.

The Node extension bridge carries the same pi-schema package mechanically ported to ESM by `tools/NodeBridge/pi-host/virtual/port-upstream.mjs` (`tools/NodeBridge/pi-host/virtual/schema/`, TypeScript types stripped), with the TypeBox license headers kept in `schema/formats/typebox/`.

The pi-schema suite (`tests/PiSharp.Schema.Tests`, a test project, never packed or shipped) vendors pi-schema's test fixtures unmodified: the JSON-Schema-Test-Suite at commit `3f05891cac5d51ffa068d7076d1372130e00c829` (MIT, Copyright (c) 2012 Julian Berman; `fixtures/json-schema-test-suite/LICENSE`) and the official JSON Schema meta-schemas (`fixtures/meta-schemas/`, published by the JSON Schema organization under AFL-3.0 or BSD-3-Clause).

## Pi v1.5.0 telemetry (pi-telemetry)

`src/PiSharp.Telemetry/` (released as experimental from 1.5.0 in the `PiSharp.Telemetry` package) ports Pi v1.5.0 `packages/telemetry` (pi-telemetry, commit `f9a26365568127461c47a07a37dcb42c140a3ac5`): the span, link, carrier and metric contract, typed schemas, the no-op and in-memory adapters, the trace store, `renderTrace` and the adapter conformance kit. `src/PiSharp.AI/Telemetry/AiTelemetry.cs` ports the request tracing of `packages/ai/src/telemetry.ts` from the same commit. Both are under the Pi MIT notice above; pi-telemetry has no dependencies of its own, and nothing in either exports or sends telemetry.

## Pi v1.5.0 Chord v2 (@earendil-works/chord/next)

The unreleased gap-closure package preparation admits `PiSharp.Chord`, `PiSharp.Chord.Hosting`, and `PiSharp.Durable.Store` for local validation. In addition to the original next layers below, current source ports the v1.5.0 legacy Chord and native mappings of `next-node/plugins`, `next-node/http`, and `next-node/workers`, under the same Pi MIT notice. The former repository-only status describes the published 1.5.0 release, not this prepared package list. No Node package or browser bundle is embedded. Managed dependencies remain declared separately in NuGet metadata; Chord.Hosting uses the ASP.NET Core shared framework.

`PiSharp.Env.Protocol` and `PiSharp.Env` prepare managed protocol/client packages derived from Pi's environment sources under the Pi MIT notice. `PiSharp.Env.Daemon` is a separate source/native artifact, not embedded in those packages. Its original C process/PTY shim is MIT. Any distributed NativeAOT executable must also carry notices for its actual bundled runtime/dependencies. Optional verified upstream Android executables have distinct source/package/hash provenance described in `docs/env-ssh.md`; managed client admission alone makes no native binary distribution claim.

`src/PiSharp.Chord/` (historically repository-only in the published 1.5.0 release; now prepared for local experimental package admission, with publication pending) ports Chord v2 from Pi v1.5.0 `packages/chord/src/next/`, the `@earendil-works/chord/next` entry point (commit `f9a26365568127461c47a07a37dcb42c140a3ac5`), under the Pi MIT notice above. Its current direct package references are `PiSharp.Contracts`, `PiSharp.Telemetry` and `PiSharp.Tools`; Tools supplies shared native child containment and brings Agent and AI transitively. It bundles no Node package; its suite (`tests/PiSharp.Chord.Tests`, a test project, never packed or shipped) ports the matching tests from `packages/chord/test/next/` and holds upstream's schema-switch fixtures (`packages/chord/test/next/schema/recorded.json` and `stored/`) unchanged, as test input.

## Anthropic SDK reviewed development oracle

The eight exact upstream-locked packages in [the actual scoped restore receipt](compatibility/anthropic-oracle-setup.json) are development-oracle dependencies only. The receipt retains every observed packaged LICENSE/NOTICE text verbatim and its byte hash, including the SDK's root MIT license and internal qs BSD-3-Clause notice. Packages are @anthropic-ai/sdk0.124.0, @babel/runtime7.29.2, @stablelib/base641.0.1, fast-sha2561.3.0, json-schema-to-ts3.1.1, partial-json0.1.7, standardwebhooks1.1.1 and ts-algebra2.0.0. No package is bundled into the native product.

The exact standardwebhooks archive has no packaged root license or notice; its manifest declares MIT. It is recorded as declared-MIT-only, packagedRootLicenseVerified:false. Separate upstream library MIT declaration and repository root Apache license remain unresolved evidence; the Apache text supplies no MIT grant. This task-local development admission does not establish redistribution/legal closure. P1-02, signature/attestation and broader license review remain HOLD. Every other package passed the packaged root-license evidence check. Scripts/builds/binlinks/optional peers were disabled; no credentials, paid provider calls or permanent system changes were used.

## Codemode (Jint, Acornima)

Codemode (`src/PiSharp.Codemode`) substantially adapts Pi v1.1.0 `packages/codemode/src/{declarations,identifier,source,types}.ts`, `runtime/{host,worker,protocol}.ts` and `packages/coding-agent/src/extensions/codemode/{execute,tool,renderer,index}.ts`, under the Pi MIT notice above. `Runtime/codemode-prelude.js` is the unchanged evaluated `PRELUDE_SOURCE` of `packages/codemode/src/runtime/prelude-source.ts` (SHA-256 `224cd74082a03a57105af78e1fe205bda692f096252ff4202121016dd78f6228`), and the CLI ships `packages/coding-agent/docs/codemode.md` as `docs/codemode.md`. Scripts run in Jint 4.16.3 (BSD-2-Clause, Copyright (c) 2013, Sebastien Ros) with its parser Acornima 1.7.0 (BSD-3-Clause, Copyright (c) Adam Simon; a fork of Esprima.NET, BSD-3-Clause, Copyright (c) Sebastien Ros). Their license texts and notices ship as `licenses/Jint.LICENSE.txt`, `licenses/Jint.CREDITS.txt`, `licenses/Acornima.LICENSE.txt` and `licenses/Acornima.NOTICE.txt` (sources in `third-party/`). Jint's CREDITS lists Esprima (BSD), the V8 FastDtoa algorithm (Copyright 2010 the V8 project authors), MimeKit date parsing (MIT, Copyright (C) 2012-2022 .NET Foundation and Contributors) and Mozilla's Java port of FastDtoa, under the Mozilla Public License 2.0 (https://mozilla.org/MPL/2.0/), on which Jint's number serialization is based. The source of that MPL-2.0 code, as shipped in Jint.dll, is available from the Jint repository at tag v4.16.3: https://github.com/sebastienros/jint/tree/v4.16.3 (number conversion under `Jint/Native/Number/Dtoa/`).

## Image codec (SkiaSharp)

The `read` tool's image decoding, resizing and re-encoding (`src/PiSharp.Tools.Skia`) use SkiaSharp 4.152.1 (MIT, Copyright (c) Microsoft Corporation and Xamarin) with its native assets for Windows, macOS and Linux (`SkiaSharp.NativeAssets.Linux.NoDependencies`). The native library bundles Skia (BSD-3-Clause, Copyright Google) and the components listed in its notices file: ANGLE, HarfBuzz, etc1 (Apache-2.0), giflib, libpng, the DNG SDK, expat, FreeType, ICU, imgui, jsoncpp, libjpeg-turbo, libwebp, piex, SDL, sfntly, SPIR-V Headers, SPIR-V Tools and zlib. The complete texts ship as `licenses/SkiaSharp.LICENSE.txt` and `licenses/SkiaSharp.THIRD-PARTY-NOTICES.txt` (sources in `third-party/`; the notices file's SHA-256 is `21504c46c4c58aa64c1055bd2dcbc5f9a136b4b8c412ed3cc6740e22c5b127f5`, identical across the Win32, macOS and Linux.NoDependencies assets). The image decision logic ports Pi v1.1.0 `packages/coding-agent/src/utils/{mime,image-process,image-resize,image-resize-core,image-convert,tool-result-images}.ts` under the Pi MIT notice above, and the test suite embeds fixture images from Pi's `image-processing.test.ts` and `tools.test.ts` (MIT).

## Syntax highlighting (highlight.js)

Interactive mode highlights code with a C# port of highlight.js 10.7.3 (`src/PiSharp.Cli/Interactive/Mode/Theme/Highlight`; BSD-3-Clause, Copyright (c) 2006, Ivan Sagalaev), the library Pi v1.1.0 `packages/coding-agent/src/utils/syntax-highlight.ts` registers. The engine ports `lib/core.js`; the grammars of every `lib/languages/*.js` are extracted by `tools/HljsGrammars/extract-grammars.mjs` into `hljs-grammars.json.gz` (with V8's case-folding table for case-insensitive patterns), and the output is verified against the original library's (`tests/PiSharp.InteractiveParity.Tests/Highlight`). `HljsTimSort.cs` follows V8's `Array.prototype.sort`. The license text ships as `licenses/highlight.js.LICENSE.txt` (source in `third-party/`).

## HTML export scripts (marked, marked-cjk-friendly, highlight.js)

The HTML session export embeds Pi v1.5.0 `packages/coding-agent/src/core/export-html/vendor/*.min.js` byte-for-byte (`src/PiSharp.CodingAgent/Export/Assets/vendor`, commit `f9a26365568127461c47a07a37dcb42c140a3ac5`): marked v18.0.11 (MIT, Copyright (c) 2018-2026, MarkedJS; Copyright (c) 2011-2018, Christopher Jeffrey; git blob `9b8644b8c2f708da81365ff387d805dedeafd77d`), marked-cjk-friendly v0.1.2 (MIT, Copyright (c) 2024 Tatsunori Uchino, and authors and contributors of original packages; git blob `1931333ed83a86bde59356f4328afe7a450ab096`) and Highlight.js v11.9.0 (BSD-3-Clause, (c) 2006-2023 its contributors). Each file keeps its license header. Both MIT licenses grant the same permissions, under the same conditions, as the Pi MIT notice above.

The Node extension bridge carries marked-cjk-friendly v0.1.2's emphasis tokenizer and CJK character class, de-minified from that file with its copyright and license notice, in `tools/NodeBridge/pi-host/virtual/vendor/marked-cjk-friendly.mjs`.

## Mermaid diagrams (grok-mermaid)

Interactive mode renders Mermaid code blocks as terminal diagrams with a C# port of grok-mermaid 0.2.3 (`src/PiSharp.Cli/Interactive/Mode/Mermaid`; Apache-2.0, Copyright 2023-2026 SpaceXAI and Copyright 2026 Alexey Zaytsev), the library Pi v1.1.0 `packages/coding-agent/src/modes/interactive/components/mermaid.ts` uses (ported in `Components/MermaidTransformer.cs` under the Pi MIT notice above). The port was made from the package's `src/*.ts` and verified byte for byte against the original's output (`tools/GrokMermaid`, `tests/PiSharp.InteractiveParity.Tests/Mermaid`). The license text ships as `licenses/grok-mermaid.LICENSE.txt` (source in `third-party/`); the package has no NOTICE file. `MermaidUnicodeData.g.cs` also carries Unicode character data (Unicode License v3, Copyright Unicode, Inc.).

## Google Application Default Credentials (google-auth-library)

Vertex authentication (`src/PiSharp.AI/Protocols/GoogleVertex/GoogleVertexAuthentication.cs`) adapts google-auth-library 10.6.2 (Apache-2.0, Copyright Google LLC), the library Pi v1.1.0 `packages/ai/src/api/google-vertex.ts` uses through `@google/genai`: authorized_user, service_account, GCE metadata, impersonated_service_account, external_account_authorized_user and external_account (STS token exchange, service account impersonation, identity pool file/url/certificate sources, AWS and executable sources). It is a native reimplementation; no Google package is bundled. The license text ships as `licenses/google-auth-library.LICENSE.txt` (source in `third-party/`); the package has no NOTICE file.

## Durable SQLite storage (Microsoft.Data.Sqlite, SQLitePCLRaw, SQLite)

The experimental durable SQLite storage (`src/PiSharp.Durable.Sqlite`, decision 0005; released as experimental from 1.2.0.1 in the `PiSharp.Durable.Sqlite` package and the `PiSharp.Cli` tool) ports Pi v1.5.0 (commit `f9a26365568127461c47a07a37dcb42c140a3ac5`) `packages/durable/src/storage/sqlite/{migrations,schema,storage}.ts` and the parts of `packages/chord/src/sqlite/{database,index,migrations,schema}.ts` they use, and Pi v1.2.0 (commit `06a466935536f8c9bcacb2d5efc0fc021be1bdff`) `packages/durable/src/storage/sqlite/node.ts`, whose driver v1.5.0 moved to `packages/chord/src/sqlite/node.ts`, under the Pi MIT notice above. Its schema statements are upstream's v1.5.0 text unchanged. It runs on Microsoft.Data.Sqlite 10.0.12 (MIT, © Microsoft Corporation) and SQLitePCLRaw.bundle_e_sqlite3, SQLitePCLRaw.core, SQLitePCLRaw.lib.e_sqlite3 and SQLitePCLRaw.provider.e_sqlite3 2.1.12 (Apache-2.0, Copyright 2014-2024 SourceGear, LLC), all pinned exactly. The native e_sqlite3 library they ship for each platform is SQLite 3.53.3, which is in the public domain; the `PiSharp.Cli` tool carries the build for every runtime identifier `SQLitePCLRaw.lib.e_sqlite3` has (`runtimes/<rid>/native/`). The packages declare SPDX license expressions and contain no license files, so the texts come from the sources the packages name. `licenses/Microsoft.Data.Sqlite.LICENSE.txt` is `src/efcore/LICENSE.txt` of github.com/dotnet/dotnet at commit `95017c711e6afc1085133d440e42b4bd78155701`, the repository commit in the package's nuspec (SHA-256 `ae48df11a335dc1a615f4f938b69cba73bcf4485c4f97af49b38efb0f216353b`). `licenses/SQLitePCLRaw.LICENSE.txt` and `licenses/SQLitePCLRaw.NOTICE.txt` are `LICENSE.TXT` and `NOTICE.TXT` of github.com/ericsink/SQLitePCL.raw at tag v2.1.12, commit `ca835d21508bff43121c65081035840ac5006c4c` (SHA-256 `cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30` and `485b276b3d2bfaa26df348e1e5c84df3648981e09b88531a6a53006f0705c24b`); the NOTICE also covers SQLCipher and OpenSSL, which other SQLitePCLRaw bundles use and e_sqlite3 does not include. `licenses/SQLite.NOTICE.txt` records SQLite's public-domain blessing from `src/sqlite.h.in` at tag version-3.53.3. The four texts ship under `licenses/` in `PiSharp.Durable.Sqlite` and the `PiSharp.Cli` tool; sources are in `third-party/`.

## Durable SQL Server storage (Microsoft.Data.SqlClient)

The experimental durable SQL Server storage (`src/PiSharp.Durable.MSSQL`, decision 0007; not packed or released) is authored for PiSharp; its queries follow the SQLite storage above. It runs on Microsoft.Data.SqlClient 7.1.1 (MIT, © Microsoft Corporation) and Microsoft.Data.SqlClient.SNI.runtime 7.1.0, both pinned exactly. SqlClient's other dependencies (Microsoft.Data.SqlClient.Extensions.Abstractions, Microsoft.Data.SqlClient.Internal.Logging, Microsoft.SqlServer.Server, Microsoft.IdentityModel.*, Microsoft.Extensions.*, Microsoft.Bcl.Cryptography, System.Configuration.ConfigurationManager, System.Security.Cryptography.Pkcs and their dependencies, as `src/PiSharp.Durable.MSSQL/packages.lock.json` lists them) are Microsoft packages under the MIT license. SqlClient declares an SPDX expression and contains no license file, so `licenses/Microsoft.Data.SqlClient.LICENSE.txt` is `LICENSE` of github.com/dotnet/SqlClient at commit `29ce2d856cb5a9450f79ea62c0afc744fd337fde`, the repository commit in the package's nuspec (SHA-256 `9fa73cb72fb654d029c9214f0e3eec32c301a0c23be71b50fe3910e61553fa34`). **The SNI runtime is not open source:** it is the native network library SqlClient loads on Windows, under the Microsoft Software License Terms for the Microsoft.Data.SqlClient.SNI library, which permit distributing its object code inside an application on conditions (distributors and end users must accept terms at least as protective, and the distributor indemnifies Microsoft). `licenses/Microsoft.Data.SqlClient.SNI.LICENSE.txt` is the `LICENSE.txt` the package ships (SHA-256 `9335e8bad875dd7be4eebd55d2335eb6433d1cea61aadb3817af7807bef8932a`). Decision 0007 records that these terms must be reviewed before the backend is packed or released. Sources are in `third-party/`.

## Durable coding tools edit diffs (jsdiff)

The experimental durable `edit` tool (`src/PiSharp.Durable.Tools/Tools/JsDiff.cs`, decision 0005; released as experimental from 1.2.0.1) ports the line diff and unified patch construction of jsdiff 8.0.4 (`diff` on npm; BSD-3-Clause, Copyright (c) 2009-2015, Kevin Decker), which Pi v1.5.0 `packages/durable/src/tools/edit-diff.ts` calls (unchanged since v1.1.0): `libesm/diff/base.js` (SHA-256 `faa81734df6ea7f8034efc5572ad1bbf19c50b300fdd183baf0d7813c5eec3b2`), `libesm/diff/line.js` (`e3ef2d8c8c3a56f6b9fb04609f545b2aae2f84a02c31c5de3201aef1b0545f90`) and `libesm/patch/create.js` (`c71772a9d9ce5933ed5848a33a2f8afaac1eff24430deb6d39cd34d676d80a84`), npm integrity sha512-DPi0FmjiSU5EvQV0++GFDOJ9ASQUVFh5kD+OzOnYdi7n3Wpm9hWWGfB/O2blfHcMVTL5WkQXSnRiK9makhrcnw==. Its output is verified against the original library's (`tests/PiSharp.Durable.Tests/ToolsNodeCases.cs`). No Node package is bundled. The license text is `third-party/jsdiff.LICENSE.txt`, shipped as `licenses/jsdiff.LICENSE.txt` in `PiSharp.Durable.Tools` and the `PiSharp.Cli` tool.

## Durable file listing gitignore matching (ignore, globset)

The experimental durable environment's `listFiles` (`src/PiSharp.Durable/Env/Local/LocalFileListing.cs`, decision 0005; released as experimental from 1.2.0.1) ports the directory walk and gitignore matching of the Rust crates `ignore` 0.4.33 and `globset` 0.4.20 (Unlicense OR MIT, Copyright (c) 2015 Andrew Gallant), which Pi v1.5.0 `packages/env/daemon/src/list.rs` configures (unchanged since v1.2.0): `crates/ignore/src/{walk,dir,gitignore,pathutil}.rs` and `crates/globset/src/{glob,lib,pathutil}.rs` of github.com/BurntSushi/ripgrep at commit `3fce3b5bb0236da2df6d99672afb8a719642eca7` (the ignore-0.4.33 release), used here under the MIT option. The test suite ports the crate's gitignore tests (`tests/PiSharp.Durable.Tests/EnvListFilesCases.cs`). No Rust crate is bundled. The license text is `third-party/ripgrep-ignore.LICENSE-MIT.txt` (`crates/ignore/LICENSE-MIT`, identical to `crates/globset/LICENSE-MIT`), shipped as `licenses/ripgrep-ignore.LICENSE-MIT.txt` in `PiSharp.Durable` and the `PiSharp.Cli` tool.

## Custom agent C# scripts (Roslyn)

The experimental C# script support for custom agents (`src/PiSharp.CustomAgents.Scripting`, decision 0012; released as experimental from 1.2.0.2 in the `PiSharp.CustomAgents.Scripting` package) compiles scripts with the .NET Compiler Platform ("Roslyn"): Microsoft.CodeAnalysis.CSharp and Microsoft.CodeAnalysis.Common 5.6.0 (MIT, Copyright (c) .NET Foundation and Contributors), pinned exactly. Their dependency Microsoft.CodeAnalysis.Analyzers 5.3.0 is restored with its build and analyzer assets excluded and ships nothing. The license text is `third-party/Roslyn.LICENSE.txt`, `License.txt` of github.com/dotnet/roslyn (SHA-256 `ae48df11a335dc1a615f4f938b69cba73bcf4485c4f97af49b38efb0f216353b`, identical to the .NET Foundation MIT text above); it ships as `licenses/Roslyn.LICENSE.txt` in the `PiSharp.CustomAgents.Scripting` package. That package depends on the Roslyn packages and does not bundle them; they carry their own `ThirdPartyNotices.rtf`. The JavaScript support (`src/PiSharp.CustomAgents.JavaScript`) adds no third-party code: it runs on Codemode's Jint and Acornima above.

## Durable network design (durable-pi)

The experimental durable network (decision 0008; `PiSharp.Durable.Hosting`, `PiSharp.Durable.Server` and `PiSharp.Durable.Client` ship inside the `PiSharp.Cli` tool from 1.2.0.2 for `pisharp durable serve`, and are not packed) adapts designs of durable-pi (github.com/disler/durable-pi at commit `37165c619573dbaf425463038bbffecfe576beec`; MIT License, Copyright (c) 2026 IndyDevDan): the device, session and agent model and its catalog, addressing by id or name with derived agent names, the per-session compaction defaults (`apps/dpi-sdk/src/api.ts` `defaultCompaction`), the read-only tool classification and the faux model's prompt grammar (`apps/dpi-backend/src/{store,faux}.ts`). `src/PiSharp.Durable.Hosting` is a native reimplementation; no durable-pi code, Node package, model icon or brand mark is bundled. The license text is `third-party/durable-pi.LICENSE.txt`, shipped as `licenses/durable-pi.LICENSE.txt` in the `PiSharp.Cli` tool.

The device server, client and web hub (`src/PiSharp.Durable.{Server,Client,Web}`) also follow durable-pi's wire contract, stream frames, reducers, hub monitor, proxy and stream multiplexer (`apps/dpi-sdk/src/{api,client,models,transcript}.ts`, `apps/dpi-backend/src/{routes,streams}.ts`, `apps/dpi-playground/apps/dpi-playground-hub/src/{monitor,mux,proxy,guard}.ts`), as native reimplementations.

## PiSharp Web Common, Durable Web and Web Agent (not packed)

`src/PiSharp.Web.Common` (decision 0011), the Razor class library that `src/PiSharp.Durable.Web` (decision 0008) and `src/PiSharp.WebAgent` (decision 0011) share, uses (none of the three is packed or shipped in the tool):
- Markdig 0.41.3 (BSD-2-Clause, Copyright (c) Alexandre Mutel; github.com/xoofx/markdig);
- HtmlSanitizer 9.0.886 (MIT, Copyright 2013-2025 Michael Ganss) and its dependencies AngleSharp 0.17.1 (MIT, Copyright 2013-2022 AngleSharp) and AngleSharp.Css (MIT, Copyright 2016-2023 AngleSharp);
- vendored under `src/PiSharp.Web.Common/wwwroot`: Bootstrap 5.3.3 and Bootstrap Icons 1.11.3 (MIT, Copyright (c) 2011-2024 The Bootstrap Authors; their license texts are `src/PiSharp.Web.Common/wwwroot/lib/*/LICENSE`), and the provider logos of Lobe Icons (`@lobehub/icons-static-svg` 1.95.1, MIT, Copyright (c) 2023 LobeHub; `src/PiSharp.Web.Common/wwwroot/img/providers/LICENSE`).

The browser tests (`tests/PiSharp.Durable.Web.BrowserTests`, decision 0008, and `tests/PiSharp.WebAgent.BrowserTests`, decision 0011; test projects, never packed or shipped) use Microsoft.Playwright 1.63.0 (MIT, Copyright (c) Microsoft Corporation; github.com/microsoft/playwright-dotnet) and its dependencies Microsoft.Bcl.AsyncInterfaces and System.ComponentModel.Annotations (MIT, Copyright (c) .NET Foundation and Contributors). The package carries the Playwright driver (Apache-2.0, Copyright (c) Microsoft Corporation) and a Node.js runtime (MIT, Copyright Node.js contributors) that it starts as a child process. Chromium is downloaded on the test machine by the package's `playwright.ps1 install chromium` (BSD-3-Clause, Copyright The Chromium Authors, with its own third-party notices) and is not part of this repository.
