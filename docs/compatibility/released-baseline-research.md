# Official Pi 0.99.1 released baseline evidence

The publisher's source archive contains the release's generated catalog. Its 2,093 tracked regular files match canonical `git archive` payloads at `d86654abb8862e201933517d6f1fce9f88dd117f`: 2,091 also match raw Git blobs, while `pi-test.bat` and `pi-test.ps1` contain exactly the LF-to-CRLF conversion required by the pinned `.gitattributes`. Its only 43 added regular files are the catalog manifest and 42 provider JSON files. This freezes an observed official release-source catalog without reconstruction. Published npm package bytes and full runtime equivalence remain unqualified in this evidence slice.

The seven-file evidence candidate consists of this document and six files under `artifacts/released-baseline/`: `acquire.mjs`, `pi-0.99.1-source.tar.gz`, `SHA256SUMS`, `metadata.json`, `inspection.json`, and `evidence.lock.json`. That directory is normally ignored by Git; the lead must explicitly include these exact files when preserving the candidate. Existing baseline and oracle locks, source, fixtures, notices, status, and product code were left unchanged.

## Acquired authority and integrity

The official [v0.99.1 release](https://github.com/earendil-works/pi/releases/tag/v0.99.1) was published on 2026-09-29 at 18:27:00 UTC. Anonymous HTTPS reads acquired the [publisher source archive](https://github.com/earendil-works/pi/releases/download/v0.99.1/pi-0.99.1-source.tar.gz), [SHA256SUMS](https://github.com/earendil-works/pi/releases/download/v0.99.1/SHA256SUMS), the [official GitHub release metadata](https://api.github.com/repos/earendil-works/pi/releases/tags/v0.99.1), and 13 exact npm version documents. Raw metadata response bodies, byte counts, SHA-256 values, timestamps and public response headers are retained in `metadata.json`. Temporary signed redirect query parameters are omitted from recorded redirect URLs.

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Publisher source archive | 8,646,242 | `4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b` |
| Publisher SHA256SUMS | 823 | `1490fbb52c4cbf6f175b1b66147702b5e6584504277fc0c424745c32770bdbba` |
| Catalog `.manifest.json` inside archive | 3,779 | `58698cae7d0a3b270f360072c6106435ca1870a178254bd80d5dbfb4716734e6` |
| Source `LICENSE` inside archive | 1,069 | `0457f5bcec3b3b211605dfb5d1a49042fd638f3686a410fe099c24a25af13c48` |

The downloaded source archive hash agrees with both the downloaded checksum entry and the official API asset digest and byte size. GitHub reports `immutable: false` and `target_commitish: main`; those fields alone do not establish immutable source identity. The local byte lock and comparison against the exact pinned Git commit provide the qualified identity evidence. GitHub's automatically generated source zip/tar links are different artifacts and do not contain the ignored release catalog.

The archive was parsed in memory without extraction or execution. The parser validates tar header checksums and bounds, rejects duplicate member paths, absolute/traversing names, unsupported member types and escaping links, and confines materialized names to `pi-0.99.1/`. The observed archive contains 2,136 regular files and 249 directories, with no links or PAX metadata headers. All 2,093 tracked file payloads equal the canonical pinned Git archive payloads; there are no differing archive payloads. The two declared line-ending conversions account for the only differences from raw Git blobs. Seven prebuilt native/Wasm assets were hashed as inert data and never executed; their individual paths and hashes are recorded in `inspection.json`.

Astra's independent review of exact `70c7b18de6a6b95b9e2d927775483117390de5d1` accepted bounded inert archive/provenance evidence and identified low-severity wording error L1: the handoff incorrectly described all 2,093 files as raw-blob exact. Its independent raw-blob parser establishes 2,091 exact matches plus only the two required conversions. `pi-test.bat` is 338 bytes in its blob and 352 in the archive; `pi-test.ps1` is 1,737 and 1,805 respectively. The verifier already compares canonical archive output correctly; no archive, lock, checksum, inspection, source or executable code was changed for this wording correction. Evidence: `P:/PiSharp/root/Documents/Codex/2026-09-30/task-3/candidate-evidence/70c7b18de6a6b95b9e2d927775483117390de5d1/review.md`.

## Catalog snapshot

The schema-6 catalog manifest records `generatedAt: 2026-09-29T18:07:44.679Z` and structure hash `58511a57fb2db5e984ee62857d8079aec6ff800e19226c327c118e7f57ea916b`. All 42 provider file hashes and the sorted provider/model/API structure hash match the manifest. The snapshot contains 1,592 model entries: 1,523 chat, 57 image, and 12 classifier entries across 13 API identifiers. These counts describe catalog entries, not a provider transport qualification. All original JSON bytes and numeric lexemes remain in the source archive; inspection does not rewrite prices or use parsed floating-point values as replacement evidence.

The historical initial baseline gap text says 43 tracked provider stubs. Direct inspection of the pinned aggregator and provider directory establishes **42**; the new archive has **43 catalog additions including the manifest**. The initial lock remains immutable, and this document records the correction explicitly.

The pinned [archive creator](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/scripts/create-source-archive.sh) adds ignored catalog data to a temporary index based on the release commit and archives the augmented tree. It does not add a standalone source-commit metadata file. The [release workflow](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/.github/workflows/build-binaries.yml) hydrates catalog data before making this source archive, then builds binary assets from that archive. The observed tracked-byte identity and manifest checks qualify this specific source artifact.

Npm publication runs a separate checkout/build. The workflow and [publisher](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/scripts/publish.mjs) do not prove that separately hydrated npm catalog bytes equal the source-archive snapshot. That equality requires acquiring the official `pi-ai` tarball and comparing its packaged catalog. The [AI package build](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/package.json) copies generated data into `dist/providers/data`; actual tarball presence is still unverified.

The live catalog is another source of data. The pinned client requests [pi.dev provider catalogs](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/remote-catalog-provider.ts) and merges a newer remote overlay. Its [publication protocol](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/scripts/model-catalog-protocol.ts) selects a compatible revision through a mutable index. A current `pi-version=0.99.1` response would not establish the release's original catalog. No live model/catalog/provider endpoint was called or used for reconstruction.

## Exact npm version availability

All 13 public workspace packages returned HTTP 200 for their exact official registry version document. Each reports version `0.99.1`, `gitHead: d86654abb8862e201933517d6f1fce9f88dd117f`, and `license: MIT`. Exact `dist.tarball`, SHA-512 SRI, SHA-1 shasum, file count, unpacked size, signature records and SLSA provenance URL are retained per package in `inspection.json`; signature and attestation contents were not fetched or cryptographically verified.

| Public package | Official exact version metadata |
| --- | --- |
| `@earendil-works/pi-agent-core` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-agent-core/0.99.1) |
| `@earendil-works/pi-ai` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-ai/0.99.1) |
| `@earendil-works/chord` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fchord/0.99.1) |
| `@earendil-works/pi-client` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-client/0.99.1) |
| `@earendil-works/pi-codemode` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-codemode/0.99.1) |
| `@earendil-works/pi-coding-agent` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-coding-agent/0.99.1) |
| `@earendil-works/pi-durable` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-durable/0.99.1) |
| `@earendil-works/pi-mcp` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-mcp/0.99.1) |
| `@earendil-works/pi-protocol` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-protocol/0.99.1) |
| `@earendil-works/pi-server` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-server/0.99.1) |
| `@earendil-works/pi-telemetry` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-telemetry/0.99.1) |
| `@earendil-works/pi-tui` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-tui/0.99.1) |
| `@earendil-works/pi-session-backend-sqlite-node` | [0.99.1](https://registry.npmjs.org/@earendil-works%2fpi-session-backend-sqlite-node/0.99.1) |

The fourteenth source workspace, `@earendil-works/pi-evals`, declares `private: true` and has no license field. It was excluded from public registry acquisition according to the pinned public-workspace selection rule. A package's version/gitHead/license metadata is a declaration; it does not substitute for tarball byte integrity, packaged notices, attestation verification, or full dependency licensing review.

## Offline verification and remaining acquisition

Run from the implementation repository; the source path can point to any clean checkout of the exact pinned source and tag:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  artifacts/released-baseline/acquire.mjs --verify --source `
  'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-reference-oracle-v0.99.1\upstream'
```

Verification performs no network reads and checks the locked Node executable, five immutable evidence/harness files, raw metadata hashes, official archive checksums, tar confinement, pinned source/tag/cleanliness, catalog identity and manifest hashes, and exact frozen inspection output. Two repeated successful runs qualify deterministic inspection of these bytes. `--acquire-new` and `--inspect-new` refuse existing outputs and cannot regenerate accepted evidence in place.

The next bounded anonymous acquisition can inspect [the official AI tarball](https://registry.npmjs.org/@earendil-works/pi-ai/-/pi-ai-0.99.1.tgz) against its recorded SHA-512 SRI, compare `dist/providers/data` to this source snapshot, and read packaged notices without installation or module execution. Acquiring the other 12 exact tarballs and publisher installer package/lock would establish packaging/dependency inputs, but would still leave full dependency bytes/licenses, signature/attestation authority, SDK/runtime behavior, cross-platform execution, catalog contract tests and native differential qualification open.

The acquired source `LICENSE` is byte-identical to the pinned MIT license, copyright 2025 Mario Zechner. Generated data's upstream inputs and the archive's native/Wasm assets require their own licensing review; this evidence does not apply the root license to every dependency or embedded asset. Source acquisition and a catalog byte freeze are useful P1-01/P1-02/P2-05 evidence, while full phase completion remains open.
