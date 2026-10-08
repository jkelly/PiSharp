# Genuine file-mutation queue reference

This corpus executes the **whole unchanged** public `packages/coding-agent/src/core/tools/file-mutation-queue.ts` at commit `d86654abb8862e201933517d6f1fce9f88dd117f`, SHA-256 `33cb06ac9bcdf32c8b84d9d12e33be44c503a7670f668e003a3262cd34294d11`. The harness imports exported `withFileMutationQueue` directly using Node's native TypeScript stripping. There is no private-key extraction, module replacement, source edit or dependency installation. The module loads only `node:fs/promises` and `node:path`.

The [input](../../fixtures/pi-v0.99.1/file-mutation-queue/core.input.json) supplies authored callbacks, return labels, failure, explicit promise gates and filesystem topology. The [golden](../../fixtures/pi-v0.99.1/file-mutation-queue/core.expected.json) contains genuine callback ordering, checkpoints and returned outcomes from unchanged source. It is not authored expected output. Four cases comprise five concurrency probes plus a resolver-rejection/recovery probe. Two fresh children produced byte-identical observations. [Manifest](../../fixtures/pi-v0.99.1/file-mutation-queue/manifest.json) and [lock](../../fixtures/pi-v0.99.1/file-mutation-queue/oracle.lock.json) retain exact input/golden/harness/runtime/canonical-source/loaded-closure hashes and provenance.

## Observed behavior

Each concurrency probe holds callback A through separate work and cleanup gates, registers callback B next, then registers unrelated callback C. C starts and completes before either A gate is released. The pinned implementation serializes registrations, so C's completion establishes that B's earlier registration passed key resolution while A was held. This avoids using sleeps or assuming how many microtasks an asynchronous filesystem lookup requires. The captured checkpoints record whether B has actually started.

| Target relationship | B started while A work/cleanup remained blocked? |
| --- | --- |
| Same existing file | No |
| Existing file and its directory-junction alias | No |
| Existing file and hardlink to that file | Yes |
| Same missing file through a lexical `..` path | No |
| Missing file through target directory and its junction alias | Yes |

The same-existing-file case throws an authored error in B. The source returns A/C's authored values, rejects B with the exact authored error name/message, and admits a subsequent callback D. A retains its key through awaited callback cleanup; B begins after that cleanup completes. The other probes also admit D after the earlier operations settle. This observes continued operation rather than inspecting the private queue map or claiming its deletion strategy was independently instrumented.

The ordinary Windows junction points entirely inside the owned temporary layout. The harness records that the existing junction file's realpath equals the original. The hardlink's stat identity equals the original, while its realpath differs; the source nevertheless permits concurrent callbacks. Therefore hardlink file identity does not imply source queue-key equivalence. For a missing junction leaf, `realpath` cannot canonicalize the leaf, and the source's lexical fallback allows the two spellings to overlap. These observations must be retained when comparing future native behavior.

The resolver-rejection case supplies an authored absolute path containing NUL. Node rejects it before filesystem access with `TypeError`, `ERR_INVALID_ARG_VALUE`; the exact error message is retained. The callback is not invoked, and a later valid registration succeeds. The malformed absolute path is a fixed authored input, so its actual error message is deterministic without removing or rewriting path text. No access-denied permissions, user files or policy changes are manufactured to force this error.

## Filesystem, isolation and output boundaries

Each child receives a fresh task-local home/workspace under an owned `mkdtemp` root. Only authored files, directories, one directory junction and one hardlink are created. The junction target is checked to remain inside that workspace. Ordinary user junction creation succeeded on this Windows filesystem; a creation failure would block the capture with its original error, without administrative or symlink-policy workarounds.

The existing immutable offline guard blocks real fetch/HTTP/socket/DNS calls and child processes before the capture child loads source. The child environment excludes provider/cloud credentials and user configuration. The resolve/load observer forwards original loader results, admits only the exact unchanged queue file and Node builtins, and records the actual one-file source closure. No provider, SDK or external package is imported.

The source returns callback values, not temporary paths. Callbacks receive no path arguments from the queue and use authored logical labels in their return values and ordering trace. Captures retain these actual source outcomes and raw error fields. Selected filesystem relation booleans are explicitly identified as harness observations; raw inode numbers and temporary paths are not captured and then normalized. There is no clock replacement, RNG replacement, sleep or hidden volatile-output normalization.

Before and after execution, the harness verifies clean pinned HEAD/tree, the complete queue module's exact checkout and canonical Git-blob bytes, runtime and harness hashes. Both children run against the same immutable module and independently owned layouts. Temporary cleanup verifies the resolved absolute root is inside the intended scratch directory before recursive removal. All accepted source, earlier oracles, goldens and user files remain unchanged.

## Offline reproduction

From a clone containing the frozen six-file candidate:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' `
  tools/PiReferenceRunner/capture-file-mutation-queue.mjs `
  --upstream 'P:\PiSharp\root\Documents\Codex\2026-09-30\task-2\Pi-reference-oracle-v0.99.1\upstream'
```

The default mode validates input, golden, lock, harness and source/runtime identities before source execution, repeats twice and compares against the immutable golden. Review outputs go only to `artifacts/file-mutation-queue-reference`. `--capture-new` is a first-creation operation and refuses any existing golden, manifest or lock; never use it for reproduction. No network approval, install, build or provider credential is needed for this pure source module.

This supplies bounded P3-05 source evidence for an in-process queue. It does not establish native differential parity, write/edit transaction behavior, cancellation semantics, platform-independent alias rules, cross-process locking, atomic/durable persistence or complete phase acceptance. Unix symlinks, case-only aliases, `ENOTDIR`, permission-denied resolution and other filesystem errors remain uncaptured by this corpus.
