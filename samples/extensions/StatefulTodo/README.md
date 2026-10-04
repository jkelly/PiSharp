# Stateful Todo native sample

This compiled C# extension provides the `todo` tool through the experimental
native package loader. It depends on the shared extension and JSON contracts;
it has no Node dependency, external service, credential or external state file.
The explicit package owner is `sample.todo`, entry type is
`StatefulTodo.StatefulTodoExtension`, assembly is `StatefulTodo.dll`, and tool
registration ID and name are both `todo`.

The ordinary tool behavior is derived from the unchanged pi v0.99.1 example
`packages/coding-agent/examples/extensions/todo.ts`, SHA-256
`e46824d00217e25242c186d41837cc84ca81b23f978500323448502a9a424ee2`.
The original MIT notice is retained in `StatefulTodoExtension.cs`. The original
`StringEnum` helper supplies a string `enum`, and the sample preserves the
public parameter values:

```json
{"type":"object","properties":{"action":{"type":"string","enum":["list","add","toggle","clear"]},"text":{"type":"string","description":"Todo text (for add)"},"id":{"type":"number","description":"Todo ID (for toggle)"}},"required":["action"]}
```

`additionalProperties` remains omitted. Text and ID remain optional; the host
validates supplied string/finite-number types and the action enum before
execution, and revalidates the final prepared action before applying policy.
The sample does no TypeBox conversion, cleaning, string coercion or schema
rewriting. No new primitive schema behavior is implemented in this sample.

## State and operations

At each admitted call, the sample requires the optional
`IExtensionSessionContext.SessionSnapshot`. The host captures it once from the
actual durably acknowledged active ancestry. It contains immutable raw session
entries, including metadata and unknown properties, rather than an LLM-only
message projection. A missing view rejects execution instead of creating an
empty state. Its generation identifies the current host attachment; it does
not promise general session replacement or full upstream generation parity.

The sample scans successful `todo` tool-result entries in branch order and
copies the latest complete `details.todos` and `details.nextId` values into a
fresh local list. It returns an independent JSON result; no retained mutable
list, previous invocation object or `JsonDocument` is shared between calls.
Malformed successful Todo state fails explicitly. Native `isError: true`
results from schema, policy or execution failures are skipped: their diagnostic
details are not a Todo-owned state snapshot. That is an explicit native
integration adaptation. Ordinary Todo semantic errors still return successful
tool content with the original `error` field and unchanged valid state.

| Arguments | Result text and state |
| --- | --- |
| `{"action":"list"}` | `No todos`, or `[ ] #1: text` / `[x] #1: text` lines; state unchanged |
| `{"action":"add","text":"text"}` | `Added todo #1: text`; append an incomplete Todo and increment `nextId` |
| `{"action":"toggle","id":1}` | `Todo #1 completed` or `Todo #1 uncompleted`; toggle that Todo |
| `{"action":"clear"}` | `Cleared N todos`; empty list and reset `nextId` to 1 |

Missing/empty add text, missing toggle ID and an absent normal integer ID retain
the original content/details error shapes. Normal semantic errors do not set
the native tool-error disposition. The supported comparison scope is ordinary
finite integer IDs and strings. Generated and restored IDs must be positive
exact integers within the JavaScript safe-integer range. General exponent,
fractional-ID formatting, out-of-range arithmetic, TypeBox coercion and every
JavaScript numeric corner are not claimed as source parity.

State becomes durable only when the actual host commits the returned tool
result. A local callback mutation followed by failed validation, revoked
lifetime or failed commit cannot update a hidden sample cache. Selecting an
ancestor reconstructs the state of that ancestor; a sibling's later results
are absent from the view. The host must commit the first tool result before
capturing the second callback view when an assistant emits several calls in
one batch. The tests contain two adds in one actual assistant batch for all
three provider families, so an omitted commit barrier loses ID/state and fails.
They do not silently restrict the sample to one call per turn.

Each execution checks the actual operation, session and extension cancellation
tokens and sample disposal before reading state and before returning. The host
owns callback admission, the stale-context fence, snapshot limits, active-branch
selection, final authorization and durable writes. A copied JSON snapshot is
read-only historical data and grants no operation capability.

## Package use and verification

Root's native publisher registers this project under `todo` and publishes it
to `artifacts/extensions/published-fixtures/todo`. The project excludes shared
contract runtime copies so the loader uses its existing shared ABI identities.
Package execution needs the normal explicit artifact/manifest hash, scope,
workspace/session/generation and snapshot-root approval. The test manifest
requires `owned-descriptor-callbacks` and `session-branch-snapshot`. The normal
CLI extension arguments are:

```text
--extension-package <absolute published package>
--extension-manifest <absolute manifest JSON>
--extension-approval <absolute approval JSON>
--extension-snapshot-root <absolute owned snapshot directory>
--enable-extension-tool todo
```

Create, prompt and independent resume must use that same exact approved
loadout. Initial durable declarations and actual provider requests contain
`read`, `write` and `todo`. `--deny-extension-tool todo` retains the declaration
but denies its final action; it is not substituted with unknown-tool failure.
There is no default activation or artificial diagnostic tool. The sample's
state cannot bypass native file or tool policy.

`NativeExtensionSessionCommandTests.TodoCases(dotnetHost, cliDll)` registers
four authored Node-free groups, reusing existing compiled-process and recorded
provider helpers:

1. Responses two-call batch, add/toggle/list/semantic errors/clear, physical
   acknowledgement, independent resume and selected sibling/main restoration.
2. The same complete sequence through Anthropic Messages.
3. The same complete sequence through Chat Completions.
4. Three sequential RPC prompts and actual EOF/reader/process settlement,
   physical `get_entries` acknowledgement, invalid type/action/required-field
   rejection, final tool denial, and independent reopen preserving prior state.

The initial recorded HTTP request has a complete independently authored body
predicate. Actual tool identities, serialized call arguments, complete result
content/details, approved native action targets, byte-prefix preservation and
exclusive store committed length are asserted. Live RPC storage reads share
read/write while the real writer remains owned. Source-like semantic errors
retain valid details, while schema/policy failures publish no Todo snapshot.
Those process cases prove no changed durable state; independent host/registry
counter regressions provide direct proof that denied callbacks were never
admitted. The sample does not write debug files or expose a testing tool solely
to obtain an execution counter.

These sources are authored until root builds/publishes, runs the actual groups
and native regression gate, and independently reviews the immutable result.
No source-only check constitutes a runtime pass.

## Mandatory remaining compatibility work

This is an authored native stateful tool, not execution of the whole original
JavaScript Todo extension. The original `/todos` custom TUI component,
`renderCall`, expanded/collapsed `renderResult`, key/theme/width behavior,
`session_start` / `session_tree` source event lifecycle and exact JavaScript
shallow-reference alias observations remain mandatory compatibility work.
Native immutable historical results deliberately make no shallow-alias parity
promise. The original four Todo corpus IDs remain `todo-sequence`,
`todo-shallow-alias`, `todo-branch-restore` and `todo-custom-ui`; these native
tests do not increment their execution/qualification counts. All eleven
extensions, forty-two original scenarios and broader phase exits remain open
where not separately qualified. No upstream source or frozen golden is changed.
