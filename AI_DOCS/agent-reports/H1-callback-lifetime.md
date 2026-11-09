# H1 callback lifetime lease seam

Source complete and frozen. All six core callback lifetime cases passed centrally; N1 real collectible plugin acceptance remains separate. This is a narrow PiSharp plugin integration addition, not a change to upstream close deadlines or a host-wide keepalive policy.

## Contract

`ChordHost.RegisterCallbackLifetime(Func<Delegate, IDisposable?> acquire)` registers an optional, thread-safe callback owner. A resolver returns null for callbacks it does not own. The returned registration can be disposed after host close; removal affects only future acquisitions, never outstanding leases. Registration uses the existing locked copy-on-write `Followers<T>` collection so callbacks and runtime disposal can run on different threads.

The compatible overload `RegisterCallbackLifetime(Func<Delegate, Facet?, IDisposable?> acquire)` also supplies the immutable facet that declared a callback. Staging records that identity on served types and registry providers; old attempts keep their original identity across reload. N1 can retain both the actual delegate's generation and its declaring facet's generation, including callbacks built in a shared library. Registry contributions carry the same opaque provenance through public `RegistryEntry<C>.CallbackOwner` (null for callers that do not supply it), allowing composed HTTP handlers to retain contributor generations independently of the registry provider.

The core references no Hosting or AssemblyLoadContext type. N1 installs its `PluginCallbacks.Acquire` resolver before load and owns generation lookup, retirement, HTTP callback dependencies and actual ALC unload. Without a resolver, invocation helpers call the original delegate directly.

## Lifetime rules

* Host method/replacement and result mapper are retained until the original method task and result mapping finish. Public call cancellation and bounded work-drain cleanup do not dispose that lease.
* Routines retain the original plugin delegate metadata rather than a core closure wrapping it. Jobs retain original Run and Finish tasks through the same helper.
* Native declarations retain both open and close owners continuously. Pending opens keep ownership after activation/close cancellation; eventual values close under that ownership. A native close past its public timeout keeps its original task and resource lease until actual settlement. Its cancellation source also remains owned until then.
* Follow declarations retain the attachment owner through asynchronous attachment and resource lifetime. A late attachment detaches before releasing ownership; a returned detach callback is retained while installed.
* Synchronous facet setup, factory/recording factory, method argument adapters, contract state/key mappings, registry composition, contribution adaptation and value adaptation run under callback leases. These use actual delegate identities.

Host close and request timeout semantics are unchanged. Optional owners may bound their own wait for generations; a timeout is not evidence that an executing callback is safe to unload.

## Changes

New `Host/CallbackLifetime.cs`; narrow invocation/lifetime changes in `Host/Attempt.cs`, `Native.cs`, `Follow.cs`, `Staging.cs`, `Adapted.cs`, `Host.cs`, `Jobs/Job.cs`, and the Batch/registry callback bridges. Root authorized the Jobs wrapping after J1 finished; N1 owns the Hosting changes separately.

Six authored regressions in `tests/PiSharp.Chord.Tests/Host/CallbackLifetimeCases.cs` (`host.callback-lifetime`) cover a method surviving caller/close deadlines, an abandoned native open followed by late cleanup, a native close surviving its bound, a late follow attachment/detach, synchronous registry callbacks/adapters, and immutable old/new declaring facet identities across reload. N1 additionally strengthened its real collectible plugin fixture with held HostMethod/native-close callbacks, shared-library factories, and late private dependency loads after public deadlines.

Independent read-only review inspected the original-task and continuous-resource lease paths, then the immutable declaration owner propagation, and found no H1 seam issue. N1 separately owns cached/shared Facet identity handling and the real ALC tests.

I13's `artifacts/gap-validation/Chord-full-stable8-4485082fbe664d08ba6e4a384065d679/receipt.json` records a successful build with zero warnings/errors and all six `host.callback-lifetime` plus all 22 `host.runtime` cases passing. The full suite ran 875 actual cases: 874 passed; its sole failure was the preexisting project-reference allowlist assertion omitting the newly approved Tools dependency. That run's overall result is failed, not a full-suite pass. The source fingerprint changed only for `src/PiSharp.Chord/Legacy/Runtime/Object.cs` and `src/PiSharp.Tools/packages.lock.json`; the callback seam and its tests were unchanged during execution. Analyzers were disabled for this runtime build. No builds or tests were run by H1.

The subsequent central receipt `artifacts/gap-validation/Chord-full-stable9-82826a7a4b014cefa3835fa7f3161ef9/receipt.json` passed all 875 actual cases with zero warnings/errors and identical before/after source hash `5b9bc1b8588bb2a68def88fffff986f5c6640a4dcae3a5472b0729083377db08`. This supersedes the earlier full-suite failure. It retains the runtime-build analyzer-disabled qualification.

After stable9, root approved the sole H1 follow-up: `InternalsVisibleTo` for `PiSharp.Chord.Hosting` in the Chord project. N1 uses the internal setup delegate to create a unique `Facet.Define(original.Setup)` declaration wrapper per evaluated generation. This preserves the original callback and public facet identity semantics while preventing cached/static shared Facet keys from colliding or retaining retired generations. The edit changes assembly friend metadata only; stable9 precedes it. N1 owns wrapper evaluation, unchanged-digest reuse, its regression and centralized build/runtime revalidation.
