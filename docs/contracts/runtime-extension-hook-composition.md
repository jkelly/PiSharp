# Runtime extension hook composition

This P3-04/P3-09/P6-06 bridge composes native and explicitly enabled extension tools before authorization. It uses the existing prepared-tool pipeline and transactional registration dispatch; it adds no tool execution broker, file/shell grant, plugin activation, or UI binding.

`ExtensionAgentBinding.Adapters` exposes the immutable adapter array already created from `Snapshot.Tools`. Each adapter retains that same registry snapshot, owner-generation target, complete host-supplied schema validator, result-value limits, and session cancellation token. `PreparedHooks` exposes the binding's existing long-lived registered hook dispatcher when the snapshot contains tool-call or tool-result handlers; otherwise it is null. `Tools` continues to use these exact adapters and that exact hook instance. Its existing standalone constructor, policy, local transforms, declarations, and execution behavior remain available.

The composing host registers captured declarations with the corresponding `binding.Adapters` rather than wrapping `binding.Tools` executors. It supplies the borrowed hooks through the nonpositional `SessionRuntimeRegistryOptions.PreparedToolHooks` property. Constructor preflight validates the selected invoker limits even with no active tools. Each `Resolve` replays existing selected transcript declarations and creates one `ToolInvoker.WithPreparedHooks` over the active native and extension adapters. Without that property, the previous `new ToolInvoker` path remains unchanged. No callback is invoked during registry construction or resolution.

For example, after validating explicit host activation and choosing the enabled tools:

```csharp
var runtimeOptions = new SessionRuntimeRegistryOptions
{
    PreparedToolHooks = binding.PreparedHooks
};
// Each SessionRegisteredTool pairs a captured declaration with its raw prepared
// adapter. Include native adapters in the same array; supply one final policy.
var runtime = new SessionRuntimeRegistry(models, registrations, finalPolicy, runtimeOptions);
```

Registration ordering and the host's enabled-name filter must pair each adapter with its matching captured declaration. Runtime registry admission still checks name, declaration, limits, and execution mode. These borrowed objects do not keep disposed owners executable. The host continues to own registry/session lifetime and must not replay arbitrary standalone binding-local transforms through an already-authorized executor. This bridge specifically composes the captured adapters and registered prepared hooks. Existing model-bound `IToolHooks` remain their separate legacy scheduler contract; the bridge does not install a duplicate copy of registered callbacks there.

The single invoker preserves the established sequence:

1. Resolve and prepare through the active tool's trusted adapter, then validate its complete initial schema/action.
2. Dispatch the captured registered tool-call hooks over that validated input. First block stops propagation; hook errors prevent execution.
3. Reprepare a replacement through the same adapter, then apply the invoker's configured transforms and final schema/action validation. The original authoritative assistant call/history remains unchanged.
4. Invoke the host's mandatory final-action policy once. Policy and execution receive the identical final action. Native paths/commands retain their existing authority; Extension actions retain their exact owner/generation/registration target.
5. Execute through that adapter, join admitted progress/owned cleanup, and apply registered tool-result patches before terminal events and canonical result commit.

Registered result semantics are inherited rather than reimplemented. Omitted/nullish patch fields follow the existing source after-hook rules; model-content replacement clears stale structured output. Complete owned result metadata and execution error disposition stay separate, and canonical/durable/provider projections retain their respective established shapes and budgets. Native progress still traverses the runtime's named adapter wrapper and the ordinary invoker/scheduler progress scopes.

In a sequential batch, each tool's canonical result commit completes before the next tool's registered before hook. The original assistant and matching call must already be acknowledged, but that assistant need not remain the last history entry throughout the batch. The mixed regression asserts the assistant tail for the first native call and the committed native result tail for the following extension call, while retaining the original assistant arguments. Registry tool dispatch currently passes the same final owned argument instance through to the callback; the regression retains that identity control as well as identical policy-to-native-adapter action identity.

The captured dispatcher also keeps the existing registry-generation behavior. Adding a handler does not modify an already captured binding or admitted dispatch. Removal revokes that owner and makes an old captured hook snapshot fail closed before policy/effects. A fresh idle-host binding/runtime selection can use the new snapshot, including a null hook instance after the last hook is removed. This property does not perform a dynamic reload or automatically replace a live session's runtime registry.

`RuntimeExtensionHookCompositionTests.Cases()` contributes five developer groups. They use an actual registry, complete authored path schema validators, prepared native file actions, Extension adapters, the real runtime registry, an actual `PersistentAgentSession`, and actual `SessionLogStore` flush/checkpoint/read/reopen paths. The mixed two-turn case checks exact initial/hook/final-policy/execution/result ordering, one final policy per executed tool, immutable original call data, native progress, a held committed result observer, next-request history, and no reexecution on reopen. Other groups cover initial/final schema, first block, hook error, policy denial, captured addition/removal, owner/session cancellation with held actual callback cleanup, legacy defaults, zero-active invoker limit admission, active-tool limit rejection, and loadout removal.

The provider in these groups is a recorded `IChatTransport`; these are composition/durability regressions, not HTTP/SDK parity receipts. Root registered, compiled, and serially ran the corrected aggregate gate: 595/595 groups passed, including all five new groups and 69/69 extension groups, with zero build warnings/errors and Node absent. The complete log is `artifacts/runtime-extension-hook-composition-final-native.log`, with the eight ordinary native/transport JSON reports beside it. Root's first correctly configured extension gate reached 68/69 groups and exposed the new mixed test's obsolete assistant-tail expectation for the second sequential call; the more precise history assertions above correct that authored expectation while keeping all numeric/effect/policy checks. A separate inherited JSON-output test now uses an explicit actual-executor cleanup witness before its original cleanup-count assertion. Earlier failed logs and reports remain preserved. Independent acceptance is pending. CLI activation, complete schema tooling, additional hook families, nested calls, UI/provider integration, and the remaining P3/P6 phase gates remain required work outside this bridge.
