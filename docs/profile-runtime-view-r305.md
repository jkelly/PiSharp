# Immutable profile runtime views

This source successor includes frozen CLI routing c4a31ec and the e3a040d
candidate-stop correction (rebased as 5b87ccc). Successful binding racing close
is a separate preceding commit, 003a902.

Each prepared view contains the actual native activation, prompt-template and
skill captures, skill diagnostic target, and native registry. The profile's
registry, input selector, catalog, completion, loadout diagnostic and lifecycle
readers capture that immutable view for the exact attachment. Reload stages a
candidate entry keyed by its actual AgentSessionAttachment object; owner.Current
remains the sole active publication pointer. Core publication changes Current
before calling the same runtime lease's binder. Failed cleanup removes only
that exact entry. Missing views after attachment never fall back to startup.

The actual profile reload wrapper requires prepared views when old built-in
metadata exists. It invokes old activation shutdown with reason `reload`,
updates the acknowledged skill system section before start, then invokes the
candidate activation's start with reason `reload`. Final quit dispatch uses the
view captured with its retained shutdown snapshot, not a later current view.
The existing extension shutdown overload still defaults to `quit`.

Callers load a fresh native activation through existing explicit admission, call
`profile.BindPreparedExtension(activation)` to bind the exact profile policy and
invoker limits, assemble the native registry, and call:

```
profile.PrepareRuntimeView(candidate, activation, prompts, skills, diagnostic,
    nativeRegistry, nativeResources, bindNative, commitPreparedRegistry)
```

This returns PreparedNativeHostReload with the same candidate runtime lease;
it does not wrap an already claimed lease or create another activation owner.
The prepared commit installs the admitted extension target identities into the
same final policy. Caller resources remain caller-owned on preflight rejection;
after the method returns, the returned lease owns their cleanup. Callers must
dispose returned preparation if subsequent caller-owned staging throws.

Initial and navigation acquisition integration in parent-owned files:

1. Capture Registry and exact policy once before awaiting admitted acquisition.
2. After validating returned identities, call
   `CaptureInitialRuntimeViewOwnership(nativeResources, generation)`.
3. Place `ownership.Resources` in the admission's NativeResources and
   `ownership.BindOwner` in its single-cast BindProfileView callback.
4. The existing factory invokes BindProfileView after activation.BindOwner in
   the same returned lease. Factory failure cleanup joins NativeResources.
5. Replace the final direct startup extension AttachOwner call with
   `profile.AttachRuntimeView(owner)`; it is idempotent after factory binding.

The transfer routine executes no borrowed callbacks. If it rejects before
returning, the acquisition caller still owns native/discovery resources and
must join their cleanup. The first transfer moves the profile's only startup
activation hold; there is no retained profile hold blocking final disposal.
Initial metadata freezes at binding, after startup template/skill loading.
Navigation acquires another hold on the exact existing view before retiring
the old runtime. New reload activations require distinct lifetimes. Last release
starts actual activation retirement before draining captured users, then joins
both. Combined native/view disposal starts both originals, joins all faults,
retains original Task status/Exception, and exposes a stable close task.
Final profile disposal joins transferred owners rather than directly disposing
the startup extension again.

Parent-owned OfflineSessionProfile.Mcp.cs, McpSessionRuntimeFactory.cs, admission
contracts and Program are intentionally unchanged in this patch. They must be
composed with the coordinated BindProfileView changes before claiming initial
native activation integration. No default resource/provider/native acquisition
is manufactured. The existing supplied NativeHostReloadOperations remain the
explicit preparation authority; callers must use the view preparation above.

Source-only controls: ProfileRuntimeViewTests (three) cover shared navigation
lifetime with held users and multifault original disposal, actual persistent
navigation plus staged template/native-registry publication, and rejected
publication without startup metadata resurrection. ProfileRuntimeViewNativeTests
(one) loads two explicit fixture activations and checks old reload versus new
quit lifecycle and disposal order. Its new fixture source is in the existing
isolated PublishedFixture.SessionLifecycle project. Register these groups in
the parent-owned test Program. No build, test, compiler, native fixture,
provider call, trust operation or HTTP request was executed.

Static inspection checked constructor/callback signatures, nullable imports,
net10.0 project references, primary-constructor capture warnings, and async
locals (no ref structs across await). This is source review, not runtime proof.
