# Reversible native extension callback quiescence

Source-only candidate. This implementation and its eleven new cases have not been compiled or executed.
It supplies the stop-admission and callback-drain portion of original P6-09. All original package and
phase gates remain open; it does not implement a complete reload command or establish unload success.

`RegistrationScope.QuiesceAsync(token)` and `LoadedExtension.QuiesceAsync(token)` pause one owner
generation. The registry stops admitting new callbacks before waiting for callbacks already leased to
that owner. Its current snapshot omits the owner's tools, commands, observations, and reducers. Calls
through an older snapshot still undergo the live state check and reject the paused generation.
Existing names, owner identity, registration order, and resource charges remain reserved. Other owners
continue to dispatch. A second pause is rejected while the first is pending or held.

The returned `RegistrationQuiescenceLease` means the admitted callbacks actually drained. This includes
retired registration callbacks, awaited command completions, admitted participants not yet reached in a
multi-owner dispatch, and the registry's awaited tool-update delivery and UI-context cleanup. It does
not include work a plugin started outside the host-owned callback contracts. Lifetime cancellation,
plugin shutdown, published snapshot deletion, and ALC unload have not happened at this point.

Disposing the lease resumes the same generation once. Cancelling a pending pause also restores admission
without cancelling or abandoning the original callbacks. A host may impose a deadline by cancelling
this wait; that is a cancelled pause, not a successful drain, shutdown, or reload. Actual scope or registry
shutdown wins over resume and cancels the pending wait while continuing to own and join the original
callbacks. A retained old lease cannot revive a disposed generation or affect its replacement.

```csharp
// Stage and validate replacement metadata before changing the live generation.
using var pause = await loaded.QuiesceAsync(reloadCancellation);
// Capture host state here only after every admitted callback has drained.
// If staging is cancelled or validation fails, lease disposal resumes this generation.

// Once committing shutdown, retain and await the actual cleanup task.
await loaded.DisposeAsync();
// Disposing pause now cannot resume the closed scope.
```

A callback cannot await a pause of an owner leased by its own dispatch. That would wait for its own
settlement, including a later participant in a captured multi-owner dispatch. It fails with the existing
`ReentrantDisposal` diagnostic and operation `quiesce` before changing admission. These APIs are
host runtime methods; no extension SDK ABI or feature declaration changes.

The loader keeps the existing published package and owner reservation through a pause. Its existing
disposal path still joins callbacks, invokes shutdown once, releases owned resources, and requests
cooperative unload. Release the quiescence lease before expecting collection because it owns a scope
reference. An unload request is distinct from proven collection. Replacement activation, rollback after
failed activation, generation state reconstruction, restart-required policy for leaked or hung work,
and the hundred-cycle P6-09 gate are still outstanding.

Future permitted validation must run the eleven `quiescence.` groups in the native extension suite,
then the complete extension, agent, session, and full native suites. The published case requires the
existing freshly published, explicitly approved Windows fixtures. No old blocked products are test inputs.
No production code forces GC, changes security settings, launches another process, or acquires a dependency.
