# DLT — Delta open-change ownership

Scope: applicability row `chord.delta-tracker-open-change`, whose actual target is
`src/PiSharp.Durable/Chord/Delta/Tracker.cs`. This is the Durable immutable-value
tracker, not the separate `PiSharp.Chord` JsonNode tracker or its new wire codec.

Baseline inspected: Pi v1.5.0, commit
`f9a26365568127461c47a07a37dcb42c140a3ac5`,
`packages/chord/src/delta/tracker.ts` (the source lives under `src/delta`, not
`src/next/delta`), `test/delta-tracker/tracker.test.ts`, and
`test/delta-tracker/{retention.test.ts,retention.worker.ts}`. The existing port-map
tracker entry identifies blob `731cc49a8811bca36671a2dc46a0a9266aa36e5e` and marks
the open-change update as its remaining sync item.

## Implementation

- The tracker strongly holds its newest open context in one field. It creates a
  weak competitor handle only when another change begins while that context is
  open. The competitor set is lazy and prunes dead entries on the upstream
  256-demotion budget. A single sequential change allocates no competitor handle
  or set. Prepared replacements never enter or demote the open registry.
- Preparing/aborting releases the matching open/competitor entry. Adoption drops
  both registries and invalidates live open competitors; prepared competitors
  remain readable and fail adoption by revision. No-op adoption does the same.
- Each tracker owns one shared weak self reference and a separate identity token.
  Retained open changes, settled changes, drafts and prepared results do not hold
  their tracker strongly. Foreign/repeated/aborted/stale adoption checks remain.
- Retained CLR draft handles previously retained base trees, ancestors, writes,
  materialized caches and context buffers after settlement. `Draft.cs` now links
  allocated nodes through an intrusive ownership chain (no extra collection for
  the ordinary one-node case). Settlement clears all allocated nodes, including
  detached and read-only descendants, and drops context bases/dirty buffers.
  A retained `Prepared` intentionally continues to own its public immutable base,
  value and operations.

CLR distinction: these are explicit draft methods guarded by shared lifecycle
state, not JavaScript revoked proxies. Cleanup visits allocated draft nodes so a
retained CLR handle releases its payloads immediately. It is O(allocated nodes),
where upstream can release a large overlay through O(1) indirection. This change
does not claim full tracker or Chord parity.

## Verification

Added `tests/PiSharp.Durable.Tests/Chord/TrackerOpenChangeCases.cs`, with nine
authored CLR cases covering no weak handles for 2,000 sequential changes; exact
open/competitor/replacement bookkeeping; all three winner positions; abandoned
draft pinning/demotion/adoption; weak tracker ownership; pruning after 1,025
abandoned changes; retained settled and detached payload collection; 5,000-node
cleanup; and retained prepared public values. GC assertions use non-inlined
construction/probe helpers, actual `WeakReference` targets, forced collections,
and `GC.KeepAlive` for intentional holders.

Serialized validation completed through `impl_ci` using the pinned SDK. The
actual run built Debug (an earlier request proposed Release) and then executed:

```text
E:/AI/PiSharp/PiSharp/artifacts/sample-tools/dotnet/dotnet.exe tests/PiSharp.Durable.Tests/bin/Debug/net10.0/PiSharp.Durable.Tests.dll --only chord.tracker.,chord.delta,chord.fuzz.tracker
```

Receipt:
`artifacts/gap-validation/DLT-tracker-runtime3-cf7dc07ef13942ec8f24ba9c08924c16/receipt.json`.
The run finished at `2026-11-08T23:07:25.1621066Z`; build and test exit codes are
zero, with zero build warnings/errors. **137 selected / 137 declared cases
passed**, including all nine `chord.tracker.open-change.*` cases; zero failures
or skips. Every case is `PASS_AUTHORED_NATIVE_ONLY` and the test output reports
zero upstream captures. The pinned baseline SHA is
`f9a26365568127461c47a07a37dcb42c140a3ac5`.

The receipt records the dirty checkout at HEAD
`14dd34b369fba6d236684da6dfc8a39a03263321`. Its before/after source-manifest
SHA-256 is identical:
`7b4a1a897942a14c500e1a7908b12750c1fdbe9e2f4279bde96a3b53cbbe57d4`,
and `changedDuringRun` is false. Rechecked current owned files against that
manifest after the run; all three match:

| File | SHA-256 |
|---|---|
| `src/PiSharp.Durable/Chord/Delta/Draft.cs` | `283a458a708d6fa4d6398464a39406295b7af5887b82b1d38d86bd151eafdd5b` |
| `src/PiSharp.Durable/Chord/Delta/Tracker.cs` | `0f45cb1d25fd4bd7f86926cf9732fb8e2aa86eb46e9cb8fe530fb3f90b76cae8` |
| `tests/PiSharp.Durable.Tests/Chord/TrackerOpenChangeCases.cs` | `048445b2d0d4efaa661060c7f55c08f3b0be9f57bdfe404d7891c486e36309f2` |

Status: scoped implementation and native runtime validation passed. This receipt
does not establish upstream-captured or complete Chord parity. Shared
applicability/port-map completion bookkeeping remains with the coordinator.
Targeted `git diff --check` passes. No additional build was launched for this
report update.
