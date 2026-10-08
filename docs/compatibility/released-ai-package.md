# Official pi-ai 0.99.1 package evidence

The official npm `@earendil-works/pi-ai@0.99.1` tarball has been acquired and verified against the previously frozen registry SHA-512 SRI and SHA-1. All 42 packaged provider JSON files are byte-identical to the publisher's release-source catalog. The packaged manifest differs only in its generation timestamp. The tarball contains no named license/notice file; its package metadata and README declare MIT, while the complete pinned source license remains separate evidence.

The new six-file candidate consists of this document and `artifacts/released-npm-ai/{acquire.mjs,pi-ai-0.99.1.tgz,acquisition.json,inspection.json,evidence.lock.json}`. That artifacts directory is ignored by default; preserve these exact five artifact files explicitly. Earlier archives, locks, fixtures, oracle harnesses, notices, source, status and product files remain unchanged.

## Artifact identity

The anonymous acquisition used only the [official exact tarball](https://registry.npmjs.org/@earendil-works/pi-ai/-/pi-ai-0.99.1.tgz). Its authority is the raw [official 0.99.1 version document](https://registry.npmjs.org/@earendil-works%2fpi-ai/0.99.1), already frozen in the preceding release evidence. That document reports `gitHead: d86654abb8862e201933517d6f1fce9f88dd117f`. This field is a registry declaration, not a build reproducibility proof.

| Property | Observed value |
| --- | --- |
| Tarball bytes | 760,966 |
| SHA-256 | `f9f44692157d0bf5679c4a17304a310028231d7daaeaaea3b73252f4b7a264d3` |
| Verified SHA-512 SRI | `sha512-4nV9JKc94iPX8bwdGPc2nTuVPKIPsffhnp3WoN9NYCNqbtoOF8LhYcIs/+Sn/alroqJK/5QRu6/Z6Ck+n0hyBA==` |
| Verified SHA-1 | `2945bf014fbb314bd37b919e83317560e0a8fa1d` |
| Regular files | 813, matching registry metadata |
| Unpacked payload bytes | 3,944,328, matching registry metadata |
| Packaged `package.json` SHA-256 | `86a8ccb86ce00af196fc7d48417f82e99c6da40c68d8d138ace2a4f0fda4090d` |

The package's `package.json` is byte-identical to the canonical pinned source file. The acquisition record retains HTTP status, timestamp, public content headers, and integrity results. `inspection.json` records all 813 file paths, payload sizes and SHA-256 values. Archives remain compressed and inert: no installation, extraction, package/module execution, provider requests or credential use occurred.

In-memory tar inspection validated bounds/header checksums and confined all members to `package/`, rejecting duplicate/absolute/traversing paths, unsupported types and escaping links. The observed package has 813 regular members, no directories, no links, and no PAX headers. The source archive used for comparison is pinned to SHA-256 `4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b` and the previously accepted all-file source identity evidence.

## Catalog comparison and baseline choice

Both archives contain the same 42 provider JSON names plus `.manifest.json`; every provider JSON payload matches exactly. This preserves all model fields, costs, compatibility metadata, object/array/string/null shapes and original numeric lexemes as archived bytes. The catalog has 1,592 entries: 1,523 chat, 57 image and 12 classifier entries across 13 API identifiers, as established by the earlier source inspection.

| Catalog stamp | Release source archive | npm package |
| --- | --- | --- |
| Path | `packages/ai/src/providers/data/.manifest.json` | `dist/providers/data/.manifest.json` |
| Generation time | `2026-09-29T18:07:44.679Z` | `2026-09-29T18:10:40.924Z` |
| Manifest SHA-256 | `58698cae7d0a3b270f360072c6106435ca1870a178254bd80d5dbfb4716734e6` | `9fbcd337d4bd414a407d7c9f8041ddba2f4d28840eca923013fd8b0636b88b23` |
| Structure SHA-256 | `58511a57fb2db5e984ee62857d8079aec6ff800e19226c327c118e7f57ea916b` | same |

The schema-6 packaged manifest covers exactly the 42 packaged provider files and all its recorded hashes match. A separate read-only byte comparison confirmed that replacing the source manifest's generation timestamp with the npm timestamp produces the exact npm manifest bytes; there are no other manifest differences. The strict archive comparison retains the difference and therefore reports `allSourceAndPackageCatalogBytesIdentical: false`.

For an npm-consumer catalog baseline, use the verified npm provider files and its own manifest, including its timestamp. For the publisher's source/binary-release baseline, retain the original source manifest. The provider model data currently coincides, but the differently stamped artifacts should keep their separate provenance. Catalog bytes are now available from actual released artifacts without regeneration or a live catalog overlay. Runtime behavior and native catalog contracts still require separate qualification.

## License and dependency declarations

No packaged file is named `LICENSE`, `LICENCE`, `COPYING`, `NOTICE`, or `THIRD-PARTY-NOTICES`. The packaged README is byte-identical to the pinned source README, SHA-256 `db41abb75b98b28b76ec77bc3f140303083e97bddb0183c30cfd6702976f47f2`, and ends with a License section stating MIT. Package and registry declarations also state MIT. These declarations do not embed the full license text in the tarball. The [pinned complete source license](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/LICENSE), already byte-verified in release-source evidence, is MIT, copyright 2025 Mario Zechner. Packaged license absence remains explicit in the report; complete licensing review is still open.

Packaged runtime dependency declarations equal the pinned source declarations:

| Dependency | Declared version |
| --- | --- |
| `@anthropic-ai/sdk` | `0.124.0` |
| `@aws-sdk/client-bedrock-runtime` | `3.1127.0` |
| `@earendil-works/pi-telemetry` | `^0.99.1` |
| `@google/genai` | `2.21.0` |
| `@smithy/node-http-handler` | `4.12.1` |
| `http-proxy-agent` | `9.1.0` |
| `https-proxy-agent` | `9.1.0` |
| `openai` | `7.19.0` |
| `partial-json` | `0.1.7` |
| `typebox` | `1.3.27` |

There are no declared optional, peer or bundled dependencies; Node requires `>=22.19.0`. These are declarations, not an acquired transitive dependency graph. No dependency was resolved or installed, and package scripts were merely recorded. Earlier approved two-dependency developer-oracle setup remains separate. Registry signature/provenance verification, all other npm tarballs, full transitive/native/data licensing, SDK execution, cross-platform behavior and full provider/native parity remain open.

## Offline verification

From the implementation repository:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  artifacts/released-npm-ai/acquire.mjs --verify
```

The default mode is offline verification. It checks the pinned Node executable, four immutable npm evidence/harness files, four earlier release-baseline inputs, official registry identity and SRI/SHA-1, archive confinement, all frozen comparison results and exact inspection bytes. `--baseline PATH` can name the frozen `artifacts/released-baseline` directory in another checkout. Two repeated verification runs produced the same frozen report. `--acquire-new` and `--inspect-new` exclusively create first-time artifacts and refuse existing outputs before proceeding. No full phase closure is claimed.
