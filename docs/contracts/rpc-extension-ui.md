# RPC extension UI

This experimental P5-06/P6-08 slice binds native extension callbacks to the same RPC connection and JSONL writer used by `RpcSessionDispatcher`. It supplies mode-specific UI outcomes and correlation; it does not supply a terminal renderer, enable plugins automatically, or close either phase.

## Host binding

`PiSharp.Extensions.IExtensionUiContext` is an optional extension of the existing context interface. Tool, command, observation, and registered reducer callbacks keep their existing signatures. The registry supplies a fresh UI scope for each admitted callback and joins its admitted work before releasing the owner-generation lease. Captured scopes become unavailable after that callback closes. Standalone reducer contexts have not acquired a renderer through this change.

The host creates one `RpcExtensionUiCoordinator`, passes it as the optional `uiProvider` of `ExtensionRegistry`, and passes that same object as the final optional `extensionUi` argument of `RpcSessionDispatcher`. The dispatcher attaches its actual shared writer once. It also owns connection shutdown of that coordinator, including with a borrowed session. The registry borrows the provider; registry disposal closes callback scopes rather than independently disposing the shared connection. Connection and session generation numbers are explicit positive host inputs, not inferred Agent turn counters.

Absent a provider, callbacks receive Print-mode empty capabilities and `Unavailable(NoUi)`. `UnavailableExtensionUiProvider` allows another explicitly selected mode, including Json, to expose the same unavailable behavior. These bindings execute no remote request. RPC advertises the source's nine baseline dialog/notification features without requiring a handshake. An explicitly supplied client capability snapshot intersects that baseline. It is immutable and describes offered protocol features; it does not certify that a particular remote client renders them. Custom terminal components remain unavailable and require the later terminal integration.

## Outcomes and source wire fields

`ExtensionUiOutcome<T>` distinguishes `Value`, `Cancelled`, `TimedOut`, and `Unavailable`. Reading `Value` for another kind throws. `ExtensionUiSourceDefaults.Confirmation` grants approval only for `Value(true)`; false, missing values, cancellation, timeout, and unavailable UI all deny approval. Text source defaults return CLR absence for non-value outcomes, while `Value("")` remains an empty string. This UI outcome does not bypass the binding's complete argument validator or the existing final-action authorization policy.

Requests retain the pinned `rpc-mode.ts` / `rpc-types.ts` field names:

| Method | Required fields after type/id/method | Optional fields |
| --- | --- | --- |
| select | title, options | timeout |
| confirm | title, message | timeout |
| input | title | placeholder, timeout |
| editor | title | prefill |
| notify | message | notifyType |
| setStatus | statusKey | statusText |
| setWidget | widgetKey | widgetLines, widgetPlacement |
| setTitle | title | — |
| set_editor_text | text | — |

Every request uses `type: "extension_ui_request"` and a unique connection-local opaque string ID. Correlation uses a fresh nonce plus a bounded monotonic sequence; it retains no unbounded retired-ID table. Notification enum spellings are `info`/`warning`/`error` and widget placements `aboveEditor`/`belowEditor`. Optional CLR null fields are omitted: a status/widget clear remains absent, while an empty widget array, empty placeholder, or empty editor prefill remains present. Inert text preserves valid Unicode and NUL. Requests are not written into the conversation or durable session log.

Replies use `type: "extension_ui_response"`, id, and either `value`, `confirmed`, or `cancelled`. The dispatcher checks these replies before ordinary command capacity. Unknown IDs and duplicate resolutions are ignored and produce no ordinary acknowledgement. A matching `cancelled: true` wins over result fields. Select/input/editor accept string values, including empty strings; confirm accepts true or false. Missing fields and JSON null produce `Unavailable(InvalidResponse)` rather than approval. Malformed field types also produce that outcome. Strict JSON, decoded duplicate names, Unicode, depth, and byte limits are checked even for unknown IDs; malformed or over-budget records can poison the connection. This is disclosed native admission hardening relative to source's unchecked response casts, not a full malformed-JavaScript-value parity claim.

Pinned source `rpc-mode.ts` installs a pending dialog before output, checks an already-aborted signal before sending, treats a truthy timeout as an optional `setTimeout`, and omits a timeout field for editor. Native finite numeric timeout fields use the accepted ECMAScript JSON projection. Zero disables scheduling; positive fractions truncate to integral milliseconds with a minimum of one; finite negative or above-int32 delays schedule one millisecond, matching Node timer delay rules. Nonfinite option values are rejected. The injected `TimeProvider` permits controlled deadlines without a current-clock dependency in tests. Source resolves timeout/abort to false or undefined; native retains `TimedOut`/`Cancelled` and its explicit source-default helper supplies those safe defaults.

## Capacity, publication, and lifetime

The default coordinator admits at most 32 outstanding requests, 65,536 UTF-8 bytes per request/reply, 1 MiB retained request plus resolved-text bytes, 16,384 UTF-16 units per text field, 256 choices/widget lines, depth 32, and 128 ID characters. A dialog remains charged through request publication, response selection, timer/registration cleanup, and task settlement; notification charges remain until its actual write settles. A response cannot approve a failed publication. Cancellation can suppress a queued UI write; after a write enters the shared writer, the connection token owns its complete write/flush and cleanup. All admitted work has an installed task before becoming visible to responses or shutdown.

With UI attached, ordinary active and deferred frames share one retained-byte budget (default 8 MiB). Active count stays under `RpcDispatchOptions.MaximumConcurrentCommands`; the additional FIFO admits at most eight deferred ordinary frames. Reading continues past that bounded FIFO to admit UI replies. An ordinary frame ahead of a reply therefore cannot occupy all command capacity and prevent the callback from resolving. Deferred frames retain their original owned input and receive a single normal or closing-before-acceptance response. Command ordering and output use the existing shared writer lease; there is no second output queue. Without the coordinator, the prior ordinary admission path remains in place.

Exhausting pending UI, deferred-frame, retained-byte, or output limits closes the connection coherently with a resource failure. This bounds a client that keeps sending ordinary frames instead of its required dialog reply. Direct-submit retained-byte exhaustion uses the same closure. EOF, read failure, write failure, and shared disposal resolve pending dialogs **before** joining the Agent/extension callback. Shutdown cancels cooperative UI writes, joins actual admitted UI tasks and entered writer cleanup, then releases the writer and session according to their existing ownership options. Scope/connection disposal share settlement tasks. Cancellation listeners throwing must not skip remaining cleanup. No default disconnect outcome grants approval, and shutdown preserves queued Agent input without launching another provider turn.

Native disconnect resolution and awaited output cleanup are ownership guarantees stronger than the pinned mode's bare pending map. Their exact JavaScript shutdown/timing equivalence is not asserted here. Cleanup failures remain visible rather than being suppressed to manufacture a successful output history.

## Developer gates and remaining dependencies

Root registration is `RpcExtensionUiTests.Cases()`: six groups exercise real transactional registry callbacks, an explicit complete empty-object schema validator, the Extension final-action policy, `ExtensionAgentBinding`, a recorded provider transport, `PersistentAgentSession`, real session storage, and the actual dispatcher/JSONL writer. They cover all four dialogs plus an explicit true/false effect decision, source-shaped notifications, a held durable result checkpoint and reopen, unavailable/capability-limited UI, controlled timeout and caller/session/owner cancellation, duplicate/null/missing/stale replies, registered reducer context binding, an ordinary frame ahead of a reply under held output, shared quota closure, EOF/write failure, concurrent disposal, and actual callback/output cleanup barriers. The fake provider is recorded `IChatTransport`, not evidence of HTTP/provider parity. These cases have not been executed by the author; root owns compilation and serial native execution.

CLI startup activation, full terminal rendering/custom components, the source runner's `ui_prompt_start`/`ui_prompt_end` nesting and microtask event behavior, every remaining UI method, nonfinite/rich JavaScript argument representation, and broader P5/P6 lifecycle parity remain required followups. This slice neither changes the legacy handshake-free protocol nor treats those obligations as exclusions from future parity work.
