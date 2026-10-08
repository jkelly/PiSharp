# Explicit borrowed grep context reader

`AdmittedGrepContextReader(string admittedRoot, IFileOperations operations,
GrepContextReadAdmission admission)` implements `IGrepContextReader` using
explicitly supplied host resources. `GrepContextReadAdmission` is a required
`ValueTask<bool>` callback receiving the exact canonical path, maximum bytes
and caller cancellation token. Supplying file operations is not admission.
There is no default operations implementation or permissive admission callback.

The supplied root must be absolute and already canonical. Each read checks its
bounded absolute input and lexical containment, verifies the canonical root,
resolves the canonical file inside that root, and awaits the host's admission
for the exact path/budget. After admission it rechecks that exact path before
calling `operations.ReadAsync` with the same budget/token. False admission and
escaping paths fail before byte acquisition. Returned size is checked after
the original read joins. Existing GrepTool handles UTF-8 decoding, the
per-invocation cache, output bounds and the upstream unreadable-file marker.

The byte budget is 1 through `GrepTool.MaximumContextFileBytes`; the owning
operations must enforce that bound during acquisition. The adapter directly
awaits original canonicalization, admission and read tasks. Cancellation never
abandons them. A successful held read followed by cancellation reports the
caller token; an original read/admission failure retains its exception identity
even when cancellation occurs concurrently. Borrowed host resources are never
disposed by this adapter. The host keeps them alive until its invocations join.

This remains the existing path-based profile: there is no atomic handle
identity, protection against filesystem swaps between the check and open, or
shared search/read snapshot guarantee. It installs neither rg/fd executors nor
filesystem grants and does not change native process ownership or tool policy.

## Serialized integration still required

The disjoint implementation supports this explicit owning-host composition:

```csharp
var reader = new AdmittedGrepContextReader(canonicalRoot, admittedFiles, existingReadAdmission);
var grep = new GrepTool(cwd, home, admittedExecutor, admittedFiles, reader);
```

`BuiltinToolCatalog` forwards its optional `IGrepContextReader` to GrepTool.
`OfflineSessionProfile.CreateAsync` accepts an explicit borrowed `OfflineGrepHost`:
search requires its final-action policy, and context requires both the profile's
exact read-target grant and the host's per-read admission. Reserved targets are
rechecked after admission settles. The host retains capability lifetime.
Ordinary command-line startup supplies no grep/find executor or implicit grant;
the internal profile seam has three authored integration groups. Native execution
and end-to-end command-line context availability remain unqualified.

`GrepContextHostAdapterTests.Cases()` contains seven authored groups covering
exact borrowing, denial and exception identity, lexical/canonical/post-grant
containment, pre-cancellation and bounds, invalid UTF-8 via the real grep
adapter, zero-context/no-read and positive-context caching through ToolInvoker,
final policy denial, held original admission and held original read/cleanup.
They use fake host I/O and no executable or physical file acquisition.

The seven cases are registered in `tests/PiSharp.Tools.Tests/Program.cs`, with
their declaring type in the direct-await exemption. The held original-task groups
must not be wrapped in the ordinary runner timeout. Exact report identities use the
`tools.HostAdapter` prefix followed by `ExactBorrowing`, `Denial`,
`Containment`, `ReturnedBounds`, `ToolComposition`, `AdmissionCancellation`,
and `ReadCancellation`; filter `grep host adapter` selects all seven.
All adapter and profile integration cases remain unexecuted.

## Base and placement limitation

Requested canonical main is `2405f51e2fd2fe3889b871c2c13e8e0dfae8d7db`, tree
`31074f90390ac126017e0d398a4c08c577add13d` (R208). Git refused local main's
metadata ownership and also refused a separate R199 source clone. No trust,
ownership or alternate-identity change was attempted. The owned checkout was
created from the prior owned repository plus existing R163/R179/R205 bundles.
The R205 bundle's SHA was verified against R207. Its reviewed candidate
`88ecdb9e8a7ced5454317edd9bce9894db364415` has exactly the requested main tree,
as independently checked with `git rev-parse HEAD^{tree}`. This patch has that
candidate as its parent, not the inaccessible merge commit. The coordinator
can apply the three-file patch onto current main; exact merge-parent ancestry
requires coordinator placement. Canonical main and active worktrees were not
edited, and no trust rejection was bypassed.
