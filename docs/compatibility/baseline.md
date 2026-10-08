# Partial immutable baseline evidence

The source target is public Pi v0.99.1 at `d86654abb8862e201933517d6f1fce9f88dd117f`. The local checkout HEAD, tag resolution and tree are recorded in [baseline.lock.json](../../compatibility/baseline.lock.json). The coding-agent and AI package versions are 0.99.1; the repository root package version is 0.0.3.

The lock records a reproducible `git archive --format=tar` SHA-256, canonical Git blob bytes and Windows checkout bytes for selected source files, package/shrinkwrap/install lockfiles, generated catalog imports and provider stubs. Checkout CRLF conversion is visible through separate byte counts and hashes. The source tar is reproducible from the pinned Git objects and is not retained or presented as a published release tarball. The exact preinstalled Node 24.19.0 executable is hashed for development reference runs. No runtime or upstream package is bundled into the native product.

The executable/distribution baseline remains incomplete. Released npm/standalone artifacts are not acquired or hashed. The tracked provider `.models.ts` files import absent `providers/data/*.json`; the generated model data manifest is also absent. No catalog has been reconstructed or regenerated. Costs/model metadata remain unfrozen. Lockfiles are captured, but package bytes and transitive license files have not been acquired. Node distributor archive provenance and the runtime/platform matrix remain open.

[surfaces.json](../../compatibility/surfaces.json) seeds 46 requirements covering each planned surface family and all ten `KnownApi` values. It is explicitly a seed rather than a complete per-export/command/event/capability enumeration. Every row remains Deferred in [parity.json](../../compatibility/parity.json), including mandatory rows. Authored contract tests and narrow upstream queue captures do not close these rows or Phase 1.

The [provenance record](../../compatibility/provenance.json) distinguishes public source adaptation, newly authored synthetic data and captured queue behavior. [Third-party notices](../../THIRD-PARTY-NOTICES.md) retain the complete upstream MIT notice. Transitive redistribution review, naming review, scope decisions and contract/platform signoff remain open with owners in the lock and provenance records.

Run the checker from the repository root with the exact locked Node executable:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/CompatibilityReport/check.mjs --upstream 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-upstream-v0.99.1'
```

The checker reads immutable blobs and recreates the source archive in memory, validates metadata and fixture checksums, and prints remaining blockers. It never regenerates expected outputs or downloads dependencies. Without `--upstream`, it validates repository evidence but reports zero source files verified.
