# Third-party notices

The CLI's embedded Models/openai.json, openrouter.json and anthropic.json are byte-for-byte provider catalog shards from the already admitted Pi AI 0.99.1 package (SHA256 f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3). The Pi MIT notice below applies. Their provenance and individual hashes are recorded in docs/contracts/native-live-terminal-composition.md; catalog metadata is separate from live provider qualification.

The native parsed Anthropic Messages mapper substantially adapts public Pi `packages/ai/src/api/anthropic-messages.ts`, under the Pi MIT notice below. Authored offline DTO tests establish native behavior separately from genuine provider/SDK reference observations. No Anthropic SDK, Node runtime or network dependency is included in the native mapper.

Native edit matching and replacement substantially adapt public Pi `packages/coding-agent/src/core/tools/edit.ts`, `edit-diff.ts` and `utils/text.ts`, under the Pi MIT notice below. The source hashes are `31a368c14cf5437ecac157765660001b16063302d9bd746303d6d1b1dcb5625d`, `f85a9809eb44b9828236050cf38a8e45933e5cfd583a186e9a0e55a664dd3e8d` and `bb323f9607c499115b532021f4f0163c6df04d834aee9c32c3c77b273bd2d7c6`. Its bounded native Myers formatter is an independently authored algorithm; exact upstream jsdiff formatting remains to be qualified. No Node dependency is bundled with the native edit adapter.

PiSharp's current porting baseline is public Pi version 1.1.0, commit `abe508e1b89912adde45528136c3221eb69acdd7` (see `compatibility/target.lock.json`). The adaptation records in this file name the upstream version and source hashes each adaptation was derived from; a record is updated when its source is re-ported against the current baseline. Records for version 0.99.1 remain accurate for code that has not been re-ported.

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

## Anthropic SDK reviewed development oracle

The eight exact upstream-locked packages in [the actual scoped restore receipt](compatibility/anthropic-oracle-setup.json) are development-oracle dependencies only. The receipt retains every observed packaged LICENSE/NOTICE text verbatim and its byte hash, including the SDK's root MIT license and internal qs BSD-3-Clause notice. Packages are @anthropic-ai/sdk0.124.0, @babel/runtime7.29.2, @stablelib/base641.0.1, fast-sha2561.3.0, json-schema-to-ts3.1.1, partial-json0.1.7, standardwebhooks1.1.1 and ts-algebra2.0.0. No package is bundled into the native product.

The exact standardwebhooks archive has no packaged root license or notice; its manifest declares MIT. It is recorded as declared-MIT-only, packagedRootLicenseVerified:false. Separate upstream library MIT declaration and repository root Apache license remain unresolved evidence; the Apache text supplies no MIT grant. This task-local development admission does not establish redistribution/legal closure. P1-02, signature/attestation and broader license review remain HOLD. Every other package passed the packaged root-license evidence check. Scripts/builds/binlinks/optional peers were disabled; no credentials, paid provider calls or permanent system changes were used.
