# Owner-bound native flag facade (source candidate)

This optional facade covers original Pi 0.99.1 `registerFlag(name, {type, description?, default?})` and `getFlag(name)` semantics. Original source is pinned at `d86654abb8862e201933517d6f1fce9f88dd117f`, `packages/coding-agent/src/core/extensions/loader.ts` lines 322–363 and `types.ts` lines 1635–1651. This is a native adaptation, not the JavaScript ABI or a completed CLI parser integration.

`ExtensionFlagValue` represents boolean|string. A null return/default means absent; false and empty strings are present values. Validation rejects a default whose kind differs from the declared kind before admitting metadata. Registering the same name replaces its definition while retaining its actual native marker and the first pending default. Existing host values take precedence. A value is visible to an extension only if that extension registered its name.

The host supplies its admitted values explicitly as `IExtensionHostFlagValues`; the included `ExtensionHostFlagValues` copies initial values and supports explicit host updates. It does not inspect arguments, environment, credentials, or process state. A custom dependency is trusted synchronous host code and must commit a default batch atomically, with existing values winning. It must not call back into the facade. No work is launched by this dependency contract.

The integration boundary is explicit:

```csharp
var values = new ExtensionHostFlagValues(admittedFlagValues);
using var flags = new ExtensionFlagRegistrationHost(registry, values);
// Inside the admitted extension InitializeAsync:
// var api = flags.Bind(owner); api.RegisterFlag(...);
var originalActivation = registry.ActivateAsync(ownerId, extension);
var scope = await originalActivation;
try { flags.CommitOwnerFlags(scope); }
catch (Exception commitFault) {
    var originalClose = scope.DisposeAsync().AsTask();
    try { await originalClose; }
    catch (Exception cleanupFault) { throw new AggregateException(commitFault, cleanupFault); }
    throw;
}
// The host may now expose this owner through its admitted application pipeline.
```

The registry remains authoritative for owner identity/generation, native metadata admission, snapshot publication and lifetime. Bind rejects a foreign registry owner. Each flag occupies a real native observation registration with topic `facade-flag-metadata`. These observations are lifetime metadata with completed no-op callbacks; they implement no flag parsing, provider routing, model selection or message effects. `CaptureFlags` returns definitions only when their exact native marker is present in the actual registry snapshot.

Defaults stay private until genuine native activation and the explicit host commit succeed. They remain first-pending-wins between those two boundaries. The host must commit before exposing the owner through its application pipeline, and close the real scope if host commit fails. Failed initialization cannot commit pending defaults. After commit, active definitions commit defaults through the supplied host value dependency. Defaults are shared host runtime values and persist after an owner closes, matching the original shared runtime map; a closed or stale facade cannot read or mutate them. Host disposal retires its metadata but does not dispose the registry or extensions.

The bounded native profile permits 128 bound owners and 128 flags per owner, names 1–128 UTF-16 code units, descriptions/string values up to 4096 code units, and at most 1024 host values. Native registry quotas apply in addition. These admission limits are native resource policy, not an assertion that upstream imposes those limits. Native handles support explicit removal as an additional native lifecycle operation. No global registry, activation, parser, or central facade file changes are included.

The standalone `PiSharp.RegistrationModelFacade.Tests` project defines six meaningful groups: staged replacement/override/absence and post-activation staging; validation before native metadata; failed actual initialization rollback and original fault identity; foreign owners/removal/generation identity; held real observation callback through owner close; and held operation cancellation with two original task faults. Tasks are captured once, directly awaited, and reported with terminal status and reference-indexed complete original aggregate/caught exception graphs. The adapter preserves existing raw callback await semantics: awaiting a task with multiple faults selects one exception, while the test retains the original task's full inventory separately. The fixture uses source-generated report metadata and does not disable reflection globally in the existing host.

No build, restore, test, or runtime qualification has been executed for this candidate. Next decisive action is an independently reviewed root-owned finite qualification of this standalone project against the exact candidate, followed by admitted host wiring if accepted. `registerProvider`, `unregisterProvider`, OAuth/custom stream routing, extension model catalog/selectors, `sendMessage`, and `sendUserMessage` remain separate missing facade/host integrations in lane 2; this flag slice does not close them.
