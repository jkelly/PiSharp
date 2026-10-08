# Profile close and empty reload ownership correction

Base: `ba9a8db8a5076c5bc444c804982a5d96a6166c3d` (frozen R305 composition).

This successor rejects profile disposal from its own native/view cleanup callback before inspecting or creating the cached profile close task. The callback context surrounds combined native and view disposal, and also surrounds startup view disposal when ownership was never transferred. External callers continue joining the same cached original. Combined cleanup still starts both originals and retains complete fault inventories, including faulted `OperationCanceledException` tasks.

Dependency-only reload now constructs an independent empty view lifetime. `SessionRuntimeLease.WrapResourcesBeforeClaim` transfers the exact admitted native resources into the view ownership wrapper on the original lease, without changing its binder, registry, or identity. A reserved synchronous construction phase runs outside the lease gate and excludes claim, binding, disposal, and recursive decoration. Invalid or throwing construction leaves the original resources owned by the original lease. The profile releases its new hold if adoption fails; after adoption, candidate rejection disposes that same lease and removes the exact staged attachment. No activation or lifecycle callback is fabricated.

Profile hook retirement now compares the exact delegates installed by the prior profile activation. Empty-view binding preserves replacement callbacks installed by the admitted native binder. Binding a new native activation rejects conflicting foreign callbacks before overwriting them.

Source controls added:

- `RuntimeLeaseWrappingTests.Cases()` (2): invalid/multicast/alias/throwing construction, reservation against recursive wrapping and disposal, stable close identity, complete native original faults.
- `ProfileCloseAndEmptyReloadTests.Cases()` (3): actual MCP-bound held native cleanup with profile-close reentry, actual dependency-only reload retaining lease/binder identity through start and direct-owner close, failed publication joining candidate cleanup without reviving old metadata.
- `ProfileRuntimeViewNativeTests.Cases()` gains one case: actual native activations followed by an empty view preserve a foreign same-owner callback while clearing only exact prior profile hooks.

New case providers require registration by the parent in the reserved test Program; the existing native provider already enumerates its additional case. No Program, MCP factory, or MCP profile admission source is changed here.

Validation is source-only: call signatures and visibility checked against the exact net10.0 projects and current records/delegates; no compiler, build, test, package, provider, process activation, or runtime validation was run. New async code contains no ref-struct locals; state gates do not span awaits or wrapping callbacks. Execution remains subject to a fresh grant.
