# Experimental native extension registration

This is the bounded P6-03 registration prototype, with profile `experimental-registration-0`. Its API is **unfrozen**. The entry point uses the phase plan's `IPiSharpExtension : IAsyncDisposable` and `InitializeAsync(IExtensionRegistry, CancellationToken)` names. Registration ownership, atomic publication and cooperative cleanup are implemented here. The complete required native API inventory and approval baseline remain prerequisites to an SDK version freeze.

`PiSharp.Extensions.Abstractions` owns extension-specific descriptors, registration handles and context interfaces. Message, content, schema JSON and JSON state remain owned by `PiSharp.Contracts`; this slice uses its immutable `JsonData`. The abstractions reference only Contracts. The runtime references the abstractions, without Agent, session stores, provider SDKs, dependency injection or UI libraries. `ContractProfile` and an immutable feature list disclose this limited contract to a consumer.

## Activation and ownership

The host calls `ExtensionRegistry.ActivateAsync(ownerId, extension, initializationToken)`. An admitted owner receives a monotonically increasing generation and one `RegistrationScope` implementing `IExtensionRegistry`. Tools, commands and observation subscriptions carry that owner, generation and a caller-supplied registration ID. Their disposable handles remove one specific registration, so disposing an old handle cannot remove a replacement using the same ID.

Initialization reserves names, IDs and resource budgets in a `StagedRegistrationSet`. Staged entries are invisible to `CaptureSnapshot()` and dispatch. The host awaits the initializer, checks cancellation at the commit boundary and publishes all its live entries in one immutable snapshot. A thrown registration error, initializer error or cancellation closes the scope, removes every staged entry and awaits plugin disposal. A healthy owner's entries remain usable when another owner fails. Exceptions identify the owner and host operation; arbitrary plugin exception text is retained as inner evidence rather than interpolated into the host diagnostic.

Ownership transfers to the registry after owner admission. Rejection before admission, such as an invalid or duplicate owner, leaves disposal with the caller. Initialization may perform bounded setup, but must not start ongoing resources that require session activation. The host transaction rolls back its registrations; it cannot undo arbitrary plugin side effects. There is no session-activation notification in this prototype.

After activation, registering or disposing a handle publishes a fresh snapshot immediately at the registry gate. This is a safe boundary for these isolated descriptor callbacks. Updating actual model tool declarations, Agent execution state and session state awaits the P6-06/P6-07 integration boundary.

## Collision and ordering policy

Owner IDs and registration IDs are ordinal, case-sensitive bounded ASCII identifiers composed of letters, digits, `.`, `_` and `-`. A registration ID is unique across all three kinds within one owner. Tool names and command names have separate global namespaces; each is reserved during initialization and remains exclusive while registered. Multiple observation registrations may use one topic. Initialization order determines owner order, and registration order determines callback order within an owner, including after asynchronous initializers complete in a different order.

The default reserved tool set is `read`, `bash`, `powershell`, `edit`, `write`, `grep`, `find`, `ls`. The default reserved command set is `help`, `quit`, `exit`, `reload`, `settings`, `trust`, `permissions`. Host configuration can supply additional or replacement sets, bounded to 256 names per set. These are explicit prototype reservations, not a claim that the full core-command inventory is complete. No built-in override declaration or authorization broker exists in this slice; replacement remains a P6-06 prerequisite.

Conflict checks have a deterministic precedence: inactive scope, duplicate registration ID, reserved name, duplicate name, resource limit. Descriptor shape is checked before those host-state checks. All failures leave the registry's published registrations unchanged.

## Captured dispatch and cancellation

Host methods `InvokeToolAsync`, `InvokeCommandAsync` and `DispatchObservationsAsync` accept a captured snapshot. Before calling plugin code, admission verifies registry identity, the exact registration and current owner generation. A snapshot containing a removed registration or a previous generation cannot start a fresh dispatch for it. The host captures and leases every selected observation handler before invoking the first one; removing a handle during an admitted dispatch does not change that dispatch. New registrations participate in later snapshots.

No host gate remains held while invoking or awaiting a callback, cancellation listeners or plugin disposal. Observations are awaited in deterministic order and have no state-changing result. In this prototype a callback failure or cancellation propagates and ends that observation dispatch; all admitted leases are released. This rule is limited to the prototype observation callback. It is not a reducer or an error policy for the baseline's wider event catalog.

Contexts expose three separately owned tokens:

| Token | Owner and meaning |
| --- | --- |
| `OperationCancellationToken` | Caller of this single dispatch operation |
| `SessionCancellationToken` | Host session lifetime supplied to the dispatch |
| `ExtensionLifetimeCancellationToken` | Registry generation; canceled by scope or registry disposal |

The callback's cancellation argument links those tokens for convenient cooperative cancellation. The context retains the original three tokens and the owner generation. Tool and command contexts are distinct interfaces. Read-only session views, nested tool execution, idle wait, reload, session replacement and fresh session-context generations remain P6-06/P6-07 prerequisites. The current context exposes none of those actions.

The tool callback returns bounded owned JSON, and the command callback returns no result. These are functional descriptor callbacks used to test ownership; they are not an Agent tool/result/progress adapter or a session command implementation. Schema metadata must be an object, but this registry does not perform full JSON Schema argument validation or authorization.

## Disposal and limits

Scope disposal first stops admission and removes its live entries from the current snapshot. It then cancels the extension lifetime, joins the initializer and every admitted callback, and awaits `IPiSharpExtension.DisposeAsync`. A throwing cancellation listener is recorded without skipping those joins or disposal. Concurrent callers receive the same actual settlement task. The owner ID remains reserved through cleanup. A failed plugin disposal retains that bounded reservation and rejects a replacement owner; a host restart can be required. A listener failure followed by successful plugin disposal reports the error but releases the reservation after all joins. Registry disposal closes all scopes and awaits all their cleanup even when one fails. These rules cover tracked work; arbitrary plugin resources still depend on cooperative shutdown.

Disposal called from an owned initializer, callback or cleanup that would wait on itself throws `ReentrantDisposal` before closing the target. An observation callback also cannot dispose another owner whose callback was admitted by the same dispatch. Registry disposal from its own callbacks is denied. A host can request disposal outside those callbacks. This cooperative guard does not terminate arbitrary blocking code, timers, or cancellation listeners that synchronously wait on their own shutdown; such code can require a host restart.

The configurable default bounds are 128 admitted owners, 1,024 registrations in total, 128 registrations per owner, 1,048,576 metadata characters, 128 characters per identifier, 4,096 characters per description, 65,536 raw JSON characters, JSON depth 32 and 32 concurrent dispatch operations. Limits are inclusive. Metadata counts the owner ID once plus each live registration's ID, name/topic, description and raw schema JSON. Staged and active entries share the same cumulative budget. Removed entries still used by admitted callbacks remain charged until their leases finish. Owners in cleanup and reservations retained after failed plugin shutdown count against the owner and metadata limits.

JSON admission first bounds and strictly reparses retained raw syntax, then validates unique property names, finite numbers and Unicode scalar strings/keys. This includes permissive `JsonData.FromElement` inputs: retained comments and trailing commas are rejected. Opaque JSON NUL values and keys are inert data and remain allowed. Missing properties, explicit null, property order, whitespace and numeric tokens such as `1.0` and `9007199254740993` are preserved in the original owned value. Identifiers and descriptions reject NUL. Descriptions also reject malformed Unicode.

The limits describe registry-owned resources and admitted operations, not a byte-accurate heap limit. A caller retaining historical snapshots, disposed handles, context values or returned JSON retains those objects itself. No collectible assembly context or unload guarantee is implemented here.

## Source evidence and remaining surface

The reference is Pi v0.99.1, SHA `d86654abb8862e201933517d6f1fce9f88dd117f`, read from the unchanged local public source snapshot. `packages/coding-agent/src/core/extensions/loader.ts:270` registers observation handlers with unsubscribe functions; `runner.ts:268` snapshots each extension's handler array. `loader.ts:288` requires tool parameter metadata to be an object. `loader.ts:295` and `:304` use maps for tool and command registration; native duplicate rejection and reserved-name diagnostics are a declared policy difference. `loader.ts:593` awaits extension initialization, commits loading state on success and aborts it on failure. Those source observations motivate this slice; they do not establish full extension behavior parity.

The required inventory still includes all 41 baseline `on` overloads and their individual reducers; flags; shortcuts; message, markdown and entry renderers; provider registration overloads and unregister; MCP registration/unregister/get; virtual-model registration/unregister; and the event bus. Full tool descriptors require preparation, input/output schema, exposure, namespace, annotations, default activation, loadout preparation, execution mode, progress and renderer state. Ordinary, tool, command and replacement-session contexts still require their respective complete operations. No row is removed because this prototype does not exercise it.

Manifest/discovery/trust, published-assembly loading, dependency isolation, actual tools/commands, session/durable state, UI/provider adapters, reload/unload and SDK authoring assets remain P6-04 through P6-10 work. No third-party assembly is loaded or granted trust by this registry.

## Authored verification

`PiSharp.Extensions.ContractTests` is a console behavior suite with seven groups: asynchronous consumer publication; failure/cancellation rollback including a throwing lifetime listener and gated cleanup; deterministic conflicts; removal during captured observation dispatch; cancellation and shared disposal settlement; stale generations/callback errors/all-owner cleanup; and inclusive cumulative resource limits plus strict retained JSON admission. Ordering uses explicit task gates, with no timing-based assertions.

`Consumer/PublishedConsumer.csproj` targets the inherited `net10.0`, enables dynamic loading and references only abstractions and Contracts. Its assembly registers and executes mock echo/count/observation callbacks in the behavior suite. Publishing and loading that package through a trusted loader, dependency conflict fixtures, full public API approval and forbidden-dependency release gates still require host validation. This slice does not claim those gates have run.
