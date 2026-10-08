# Native cooked dialog retirement

This slice closes stale dialog focus in native `session chat` using the actual RPC UI coordinator's terminal decision. The frontend restores its local multiline draft and removes a canceled editor or queued dialog when the corresponding admitted host request retires. It does not infer expiry from a timer in the frontend, a tool-end event, or Agent settlement.

The integration remains the existing offline durable `RpcSessionCommand` host, activated native package registry, registered input admission, shared JSONL writer, and bounded cooked frontend. `InteractiveSessionCommand` selects the host's internal native presentation entrypoint and supplies that same frontend as the observer. The public ordinary RPC entrypoint supplies no observer.

## Optional in-process observation

`IRpcExtensionUiPresentationObserver` is an opt-in native host contract in `PiSharp.Rpc.Ui`. It adds no JSON record, field, handshake, capability, request ID format, or extension SDK UI method. Existing `extension_ui_request` and `extension_ui_response` records retain their baseline fields. Ordinary RPC clients can continue consuming exactly those records without implementing the observer.

Each admitted dialog captures `RpcExtensionUiPresentationIdentity`: the actual opaque request ID, connection generation, session generation, extension owner ID, and owner generation. These values come from that coordinator and callback scope. They are not turn counters or a guessed frontend lifetime. Full record equality identifies the local active or queued view to retire.

`PublishedAsync` is entered only after the complete actual shared writer write and flush succeed. The writer lease and coordinator gate are released before the native callback. A terminal non-value decision selected before native presentation can suppress that callback even when the entered wire publication subsequently succeeds. An early valid response does not bypass the actual publication barrier.

`RetiredAsync` is attempted exactly once for every admitted dialog with an observer, after its publication attempt and any entered `PublishedAsync` callback settle. It carries the selected terminal outcome and unavailable reason, together with separate `PublicationSucceeded` and `PresentationEntered` flags. A flushed request that expires before native presentation has `PublicationSucceeded=true` and `PresentationEntered=false`. A write or flush failure has both flags false. An entered presentation callback can fail after successful publication; both flags then remain true and the caller receives an unavailable result.

Retirement also runs when no native view was entered, so a renderer need not assume it saw every request. Non-dialog notifications retain their existing wire-driven presentation and do not acquire these callbacks. An admission rejected before it owns a bounded request slot creates no retirement obligation.

## Focus and input

In native mode, the wire observation path handles notifications while the coordinator observer presents dialogs. This prevents a dialog from being displayed before its own real flush settles or being displayed twice through the wire and observer paths.

An answer uses the exact wire request ID and baseline response fields. Sending that answer marks the visible dialog as awaiting host retirement. It retains focus until the matching terminal observation arrives, suppresses duplicate answers, and keeps `/state`, `/steer`, `/follow-up`, `/clear-queue`, `/abort`, and `/quit` available. A late response therefore cannot turn a queued dialog's answer into an ordinary prompt or approve a replacement owner. The host still selects only one outcome and ignores duplicate or unknown response IDs.

Retirement removes only the matching active or queued view. Removing the active view clears its separate dialog editor and promotes the next retained live dialog. Removing a queued view leaves the active editor intact. A retirement from another connection, session, owner, or owner generation cannot remove a same-ID local view. Agent settlement has no native dialog-focus side effect.

The main chat draft remains separate from the dialog editor. Its multiline data, leading/interior blank lines, Unicode, and inert controls survive timeout, callback cancellation, owner replacement, and dialog editor cleanup. Editor response text retains the exact prefill and appended LF lines. Display continues escaping controls through the existing cooked renderer. UI confirmation remains independent from mandatory final prepared-action authorization.

The constructor's legacy frontend mode retains its wire-only behavior. The older cooked contract's limitation that individual timed dialogs can remain visible until local dismissal/Agent settlement is superseded for native `session chat` by this opt-in observer. It continues to describe a legacy wire-only client, since the baseline protocol itself has no retirement record.

## Ownership, bounds, and failures

Callbacks execute outside the coordinator gate and shared writer lease. They are sequential for one admitted request; callbacks for different requests can overlap. Frontend state remains protected by its own short state lock and output rendering semaphore.

An admitted dialog stays charged to the existing request-count and retained-byte budgets until both callbacks, timers, cancellation registrations, and task cleanup finish. There is no additional unbounded callback queue, retained retired-ID table, or independent deadline scheduler. Retained frontend dialog count/characters and all existing connection/output limits remain unchanged.

Scope disposal joins its actual entered callbacks before releasing its owner scope. Connection shutdown chooses pending terminal outcomes, cancels cooperative native presentation, and shares one cleanup task across concurrent disposals. That task joins entered publication and retirement callbacks before disposing connection state. The RPC host continues joining extension callbacks, output, durable terminal records, package leases, and its actual session writer before returning.

Observers must cooperate with connection cancellation and must not await disposal of the scope/connection currently invoking them. Cancellation does not detach a callback that has already entered an external write or flush; real cleanup waits for that operation to settle. The native frontend changes its matching focus state before any cancellable retirement display, allowing EOF cleanup even when no further text can be rendered.

A publication or presentation callback fault poisons the host through its existing output-failure path and supplies no usable approval. A retirement callback or timer/registration cleanup fault remains a cleanup failure rather than a successful result. Private callback diagnostics are not copied into protocol output. There is no retry or replay.

## Authored gates

Root owns compilation, test registration, published-fixture preparation, and serialized native execution. The author has not executed these cases. Required registrations are:

- `RpcUiPresentationRetirementTests.Cases()` in `PiSharp.Rpc.Tests`.
- `InteractiveSessionRetirementTests.Cases(cliDll)` in `PiSharp.CodingAgent.Tests`.

The existing root-owned friend declarations permit RPC test attachment and cooked frontend access. The cooked host group uses the already published `artifacts/extensions/published-fixtures/cli-ui` package. It performs in-process actual host invocations without starting a child, building a fixture, modifying environment variables, or requiring Node.

| Authored group | Evidence sought |
| --- | --- |
| RPC held actual flush and controlled expiry | No callback/result before real flush; exact wire fields; timeout and late approvals resolve once; accurate exposure flags |
| RPC early response and observer reentry | Early response joins real flush; observer can use the same shared writer and open another scope without lock deadlock |
| RPC caller/operation/session/owner/scope cancellation | Every owned cancellation retires once and supplies no source-default approval |
| RPC owner replacement and duplicates | Unique actual IDs and owner generations; stale/duplicate answers cannot resolve a successor |
| RPC held retirement and shared close | Callback remains charged to capacity; scope and concurrent connection disposal join the entered callback |
| RPC write/flush/presentation/retirement faults | Accurate exposure flags; unavailable/cleanup outcomes and host poisoning; actual timer cleanup |
| Cooked controlled timeout and editor cancellation | Actual coordinator event restores a multiline main draft and clears only the dialog editor; exact controls and prefill preserved |
| Cooked matching identities and queued retirement | Full generation matching, live queue preservation, removal of only the retired queued view, owner replacement and late response isolation |
| Cooked answer ownership and legacy mode | Focus remains pending until actual retirement; duplicate answer suppression; queue/state commands remain available; baseline response bytes contain no native metadata |
| Cooked actual host abort/EOF with held callbacks | Entered presentation flush and retirement joins; durable writer remains exclusively owned while held; no later effect/provider turn; retained queue receipts, package snapshot cleanup, complete durable reopen |

The host group combines the actual native package, Completions offline HTTP/provider profile, prepared `write` action, bounded frontend connection, authoritative abort/EOF, and local durable storage. It verifies that canceled callback outcomes appear in durable tool results and that queued text does not become canonical history. The earlier `InteractiveSessionCommandTests` remain the compiled command route and separate final-action policy controls.

Host fixture creation and RPC reopen use the same explicitly approved package/tool flags. This is required because registry reopen restores the durable initial `toolsAdded` loadout; package activation at reopen alone does not introduce an undeclared tool. The regression asserts that `fixture.cli.ui` is declared in the created durable context before waiting for a real dialog.

Host variants are labeled by abort/EOF and the held callback. A failing variant retains a bounded JSON diagnostic under `artifacts/native-dialog-retirement-failures/` before joining cleanup and deleting its temporary workspace. It records the failure stage, actual host task status, bounded display/stderr and last observed authoritative state, callback/flush witnesses, identities/outcomes, exact fixture API/tool/action, invocation flags, and script/manifest/approval/assembly hashes. Each retained diagnostic is capped at 128 KiB including LF; the failure message also contains bounded display/stderr. These diagnostics are test evidence and add no product output.

This is a cooked presentation-lifecycle increment. It does not implement or qualify raw keyboard input, VT/ConPTY, resize, grapheme/cell layout, terminal focus/components, full interactive CLI compatibility, live provider/auth flows, the complete phase 5/6 plans, or unchanged upstream terminal equivalence. Grounding is [phase 5](../plans/05-headless-rpc-terminal-ui.md), [interactive host and editor](interactive-session-command.md), and [RPC UI ownership](rpc-extension-ui.md).
