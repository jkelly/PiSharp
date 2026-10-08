# License evidence matrix

This lane consolidates actual retained evidence for P1-02. The machine-readable [matrix](../../compatibility/license-evidence-matrix.json) separates license declarations, complete packaged notice bytes and version-associated source notice bytes. **P1-02 remains HOLD.** The matrix supplies evidence; it grants no legal approval, redistribution closure or completion claim for any of the eight implementation phases.

The implementation owner remains gpt-6.1-sol/xhigh. Existing baseline, notices, source, SDKs, setup receipts and goldens are unchanged. No package is installed, resolved or executed in this lane. Six anonymous read-only official registry/GitHub requests were approved by the applicable executor review; no credentials or provider endpoints were used. The new source response bodies are retained verbatim inside the matrix with byte counts and SHA-256 values.

## Pi source and released artifacts

The exact pinned Pi root license is **MIT**, copyright 2025 Mario Zechner: 1069 bytes, SHA-256 `0457f5bcec3b3b211605dfb5d1a49042fd638f3686a410fe099c24a25af13c48`, Git blob `b0a8e9b81083294360c69b4ec45d3d39a2b28197`. Its complete text is retained in the matrix and the existing third-party notices. It matches the official release-source LICENSE. There is no Pi root Apache/MIT discrepancy at this baseline. See the [pinned Pi LICENSE](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/LICENSE).

The source identity remains commit `d86654abb8862e201933517d6f1fce9f88dd117f`, with all 2093 canonical tracked files verified by the existing acquisition/setup evidence. The acquired checkout differs only through the two precise declared CRLF conversions in pi-test.bat and pi-test.ps1. The canonical fingerprint is `2d65bfaee0e2556cb82ae7e0425de68560ecc3be4451f69ceff3bcc9c6144aa3`; the acquired fingerprint is `676a5df7a526ab0fb6a78f734b57093bb7571913fc17251d02cb0c84527dcf21`.

| Retained artifact | Exact identity | License evidence and remaining scope |
| --- | --- | --- |
| Official release source archive | 8646242 bytes; SHA-256 `4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b` | Complete Pi MIT LICENSE; all 2093 tracked files match pinned source and 43 generated catalog files are additional. Native/generated/third-party scope remains open. |
| Official pi-ai0.99.1 npm archive | 760966 bytes; SHA-256 `f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3`; 813 files; recorded SHA-512 SRI and SHA-1 verified | Manifest, registry and README declare MIT. No named packaged LICENSE/NOTICE/COPYING file exists. The complete pinned source grant is separate retained authority. Notice delivery and full transitive scope remain open. |
| Thirteen released Pi package metadata records | Exact version0.99.1, reported GitHead matching pinned commit, per-body SHA-256/SRI/URL in the matrix | All declare MIT. Only pi-ai tarball was acquired; twelve other tarballs have no inspected packaged-notice evidence here. |
| Generated provider catalog | 42 providers, 1592 models, 13 API identifiers; source and npm manifest hashes retained separately | All provider data bytes match; manifests retain different timestamps and hashes. No reconstruction or numeric rewrite. Generator-input/data-license scope remains unreviewed. |

Artifact provenance is the [official release](https://github.com/earendil-works/pi/releases/tag/v0.99.1), retained exact publisher checksum file, official npm version metadata and SRI-verified archives. HTTPS/SRI/SHA identity and a registry-reported GitHead do not constitute verified signatures or publication attestations. Existing metadata contains signature/attestation declarations; the matrix preserves their unverified status.

## Development dependencies and packaged notices

Three exact installed profiles are reconciled: edit13packages/1997files, Anthropic8packages/2393files, and Responses3packages/4942files. Deduplicating shared package identity gives **21 distinct packages and 7929 installed files**. Twenty packages contain packaged root notice bytes. The sole missing packaged root notice is standardwebhooks1.1.1. There are 24 retained named package/vendor notice files across the distinct packages.

Each matrix row records official archive URL, upstream-lock path, version, SRI, archive SHA-256, manifest SHA-256 and installed-tree fingerprint. Each notice links its complete verbatim text through an exact immutable receipt JSON pointer and byte hash. A declared SPDX value and the observed family of a particular notice text are separate fields; neither is a blanket approval for every file or redistribution choice.

| Package | Declared license | Observed packaged notice files |
| --- | --- | --- |
| `@anthropic-ai/sdk 0.124.0` | MIT | `LICENSE` (MIT); `src/internal/qs/LICENSE.md` (BSD-3-Clause) |
| `@babel/runtime 7.29.2` | MIT | `LICENSE` (MIT) |
| `@stablelib/base64 1.0.1` | MIT | `LICENSE` (MIT) |
| `chalk 6.0.0` | MIT | `license` (MIT) |
| `cross-spawn 7.0.6` | MIT | `LICENSE` (MIT) |
| `diff 8.0.4` | BSD-3-Clause | `LICENSE` (BSD-3-Clause) |
| `fast-sha256 1.3.0` | Unlicense | `LICENSE` (Unlicense) |
| `get-east-asian-width 1.6.0` | MIT | `license` (MIT) |
| `highlight.js 10.7.3` | BSD-3-Clause | `LICENSE` (BSD-3-Clause) |
| `isexe 2.0.0` | ISC | `LICENSE` (ISC) |
| `json-schema-to-ts 3.1.1` | MIT | `LICENSE` (MIT) |
| `marked 18.0.11` | MIT | `LICENSE` (MIT) |
| `openai 7.19.0` | Apache-2.0 | `LICENSE` (Apache-2.0); `src/_vendor/partial-json-parser/LICENSE` (MIT); `src/_vendor/zod-to-json-schema/LICENSE` (ISC); `src/internal/qs/LICENSE.md` (BSD-3-Clause) |
| `partial-json 0.1.7` | MIT | `LICENSE` (MIT) |
| `path-key 3.1.1` | MIT | `license` (MIT) |
| `shebang-command 2.0.0` | MIT | `license` (MIT) |
| `shebang-regex 3.0.0` | MIT | `license` (MIT) |
| `standardwebhooks 1.1.1` | MIT | Absent; exact eight-file archive |
| `ts-algebra 2.0.0` | MIT | `LICENSE` (MIT) |
| `typebox 1.3.27` | MIT | `license` (MIT) |
| `which 2.0.2` | ISC | `LICENSE` (ISC) |

The SDK rows retain vendored notices too: OpenAI includes partial-json MIT, zod-to-json-schema ISC and qs BSD-3-Clause texts in addition to its Apache-2.0 root license; Anthropic includes its root MIT and internal qs BSD-3-Clause texts. Marked's full multi-section license file remains in its exact receipt; the table's observed MIT family is a text classification, not a substitute for the complete file. fast-sha256 retains its complete Unlicense text. No SDK or dependency is included in the native runtime by this evidence lane.

The source/root license of Pi is therefore distinct from the Apache-2.0 license of the OpenAI development SDK. Historical receipts retain their original qualification-pending status; later runtime capture locks carry separate qualification. No receipt is rewritten to imply legal closure or a packaged notice that was absent.

## Newly identified standard-webhooks source notice

The exact official [standardwebhooks1.1.1 version metadata](https://registry.npmjs.org/standardwebhooks/1.1.1) reports GitHead `b4d2c14fc5b4ccff3ff271e3b087dff812254c59`. Its recorded eight-file count and SRI match the already acquired archive. The JavaScript package.json at that commit is byte-identical to the packaged/installed manifest: 1033 bytes, SHA-256 `686397f0f0202e0b693cfd3f6241d7a8da3df326f9642867a484fbbd295690b6`. The full untruncated official GitHub tree and commit-addressed source bodies are retained, and the observed file bytes reproduce the tree-reported Git blob IDs.

That source tree contains a complete [libraries/LICENSE](https://github.com/standard-webhooks/standard-webhooks/blob/b4d2c14fc5b4ccff3ff271e3b087dff812254c59/libraries/LICENSE): **MIT**, copyright2023Svix, 1088 bytes, SHA-256 `5ec8c7b26b64d881a6706617bed25c049f97f2f35de034c756de8546fd6dbe27`, Git blob `55b14b3a3ce649a2f1b05428dcc9c6db95b37c04`. This parent-library notice is separate from the [repository root Apache-2.0 LICENSE](https://github.com/standard-webhooks/standard-webhooks/blob/b4d2c14fc5b4ccff3ff271e3b087dff812254c59/LICENSE), whose observed bytes are also retained. The distinction concerns standard-webhooks, not Pi.

This is stronger source evidence than a package MIT declaration alone. The npm archive still omits the notice. The immutable original Anthropic receipt remains declared-MIT-only/packagedRootLicenseVerified:false. The matrix adds sourceAuthority separately and does not promote the new parent-library text to a packaged notice. The registry GitHead, matching manifest and GitHub blob observations associate the source version; signatures/provenance are not verified and legal scope is not approved by this lane.

Root can now review the exact scoped MIT source notice and notice-delivery action without guessing a copyright holder or substituting an unrelated root license. This lane does not publish an issue or change third-party notices. Existing Apache references in docs and compatibility JSON were reviewed; they concern standard-webhooks or the OpenAI SDK, with no erroneous Pi attribution found.

## Tooling, embedded assets and actionable gaps

The preinstalled pinned Node24.19.0 binary is development tooling, with origin and executable hash retained. This lane acquires no Node license notice and audits no Node redistribution. The previously acquired npm11.6.2 bootstrap archive has Artistic-2.0 manifest metadata and its complete root notice retained in the matrix; 193 named notice files are observed in its bundle, whose full vendored-license scope remains open. npm is not invoked in this lane.

Eight exact tracked native/embedded assets are listed by source path, Git blob, byte count and SHA-256: six platform .node prebuilds, an example doom.wasm and the pico harness ZIP. These are inert release-source evidence. The only tracked named LICENSE/NOTICE/COPYING file found by this limited filename inventory is Pi's root LICENSE. This does not prove that there are no copyright headers or additional obligations inside vendored code, compiled assets or archives. No native asset is executed or admitted to the native product here.

The matrix records seven open actions:

- Review standardwebhooks source grant/version association and notice delivery while preserving the archive's missing-notice fact.
- Review complete Pi MIT notice delivery for any pi-ai archive redistribution.
- Select and inspect the remaining released package/provider/native dependency artifacts needed for the intended distribution.
- Review generated catalog inputs and dataset/provider terms against the frozen publisher data.
- Establish source/build/dependency notice evidence for retained native and embedded assets.
- Verify any required publisher signatures/attestations separately from SRI/SHA byte identity.
- Review actual release contents and carried notices for native ports, development archives and tooling before the gate owner's distribution decision.

The current native product/no-Node-or-SDK-bundling statement is inherited from existing notices. This lane does not inspect a published native release. Legal approval and the eight phase completion decisions remain with root/the gate owner.

## Offline check for root

The new [validator](../../tools/CompatibilityReport/license-evidence.test.mjs) pins the matrix bytes and all sixteen retained evidence inputs. It validates complete notice hashes/JSON-pointer relationships, the three exact package profiles, deduplicated counts, the public version-to-source bridge and the distinctions above. It also safely reads committed source/pi-ai tarballs in memory to check the Pi notice, eight inert asset payload hashes, packaged notice absence and matching manifest bytes; it writes or extracts nothing. Eighteen deliberate mutations must fail, including wrong Pi license attribution, a source grant promoted to a packaged grant, false legal/signature/data/native closure and a changed retained binary hash. This is evidence validation, not a licensing decision.

Root executes the offline check after review:

```powershell
& 'P:/PiSharp/root/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/bin/node.exe' tools/CompatibilityReport/license-evidence.test.mjs
```

At author handoff, the matrix assembly verified all referenced notice text lengths/hashes and overlapping profile identity, and the new validator passed Node syntax checking. The validator's twenty checks remain pending root execution. There were no dependency installations, SDK/source executions, provider calls, source/golden mutations or public writes.
