Optional Node notification publication failure candidate
=======================================================

Branch: `fix/node-notification-failure`. Base: supplied full bundle commit
`5e8ab440826fade20cad90ff7a19eb0337c59d92`. Source target remains Pi v0.99.1,
`d86654abb8862e201933517d6f1fce9f88dd117f`. No v1.0.2 or Extras work.

The canonical checkout denied Git access through its ownership check. No trust
exception was added. The supplied `b55f3f07` bundle requires unavailable commit
`d761b863ba90164cf60c9d79fcef65c83a18183f`; this branch uses the authorized full
acceptance bundle instead. Before editing, both runtime files matched the canonical
checkout and the supplied `75c18ddda73ce7d82954d701a582511d06a63dd1` source manifest
byte-for-byte. The active admission plan and launcher also match that manifest.
The coordinator can cherry-pick the bounded change after checking those identities.

Evidence and bounded change
---------------------------

The pinned Runner's print UI `notify` is a synchronous no-op. The commands and
input-transform corpus sources call notifications without awaiting them. Their
local SHA256/length pins, plus Runner and context types, were checked with
PowerShell against `compatibility/node/corpus.plan.json`; no source was executed.

`module-loader.mjs` already translates notifications into bounded asynchronous
native publication and joins them before returning a receipt. Its former
`publicationFailure ??= error` and truthiness-based receipt checks lose falsy
publication failures. A host publication rejecting with `undefined`, `null`,
`false`, `0`, empty string or NaN could produce `status: fulfilled` and omit the
failure observation. The native adapter relies on that status and
`publicationJoined` to reject failed callbacks. Existing tool-progress handling
already tracks failure separately; the UI path did not.

`createNotificationPublication` now lives in the existing admitted
`tool-progress.mjs` helper. It retains the first failure independently of its
value. The loader uses it for notification enqueue, dialog waiting and final
publication join; receipt rejection and failure observation test the failure
flag. Synchronous notify return, publication ordering, count/byte limits,
typed Print/Json unavailability, RPC capability denial, source return/failure
observations and cancellation checks are preserved. A rejected queue does not
execute later deliveries or retry effects. No process, generation, callback,
native context retirement or cancellation-write ownership is changed.

Authored controls and qualification
----------------------------------

Five `node:test` controls in `notification-publication.test.mjs` cover falsy
rejections and retained first reason, queue suppression, synchronous undefined
return, serial publication with held-settlement join, dialog wait rejection,
truthy synchronous/asynchronous errors and empty success. These use an authored
publication callback, not a live worker or provider. Five tests authored; zero
executed. The coordinator must allocate execution before running:

    node --test tools/NodeCommandInputBridge/notification-publication.test.mjs

Relevant existing controls are `tool-parity.test.mjs`, followed by the
coordinator's admitted command/input and optional Node package regressions.
Actual facade receipt propagation under notification failure still requires a
full worker/native qualification; helper tests alone do not establish it.

PowerShell static checks passed: baseline source identities, all proposed helper
length/SHA256 pins, preservation of every helper path, and proposed plan byte
comparison proving that only two existing helper content pins change.
`git diff --check` passed. No Node, npm, dotnet, native build/test, live/paid API,
install, download, package acquisition or external publication was performed.

Admission and integration blocker
---------------------------------

Active admission and launcher files are untouched. Consequently the current
immutable admission intentionally rejects the changed helper bytes. Exact
content-pin refresh is provided in `notification-pin-refresh.request.json` and
`notification-plan.proposed.json`. The proposed plan changes only the lengths and
hashes of the two existing helpers; there is no helper-path, package, module-root,
policy, source revision or entrypoint expansion. The request gives the proposed
`NodeCommandInputWorkerLaunch.PlanSha256` replacement. It is a proposal, not an
applied trust/admission change. Integration and allocation belong to coordinator
`01a1036b-12fa-734b-9ce8-6a3a50d3f0c1`.

Corpus and remaining source limitations
--------------------------------------

The inspected inventory plan records 11 extension entrypoints, 12 TypeScript
files and 42 authored scenario groups with null upstream/bridge evidence. Those
are authored groups, not 42 executed scenarios. Its prose claiming no Node
projects is stale: current source contains worker, facade, progress/loadout and
host implementations. Separate successor captures do not automatically qualify
the entire original inventory; this change adds zero executed-corpus credit.

Current admission still excludes broader lifecycle/provider/OAuth/custom UI
contracts and named neighboring context operations. Nested `ctx.tools` and
`ctx.executeTool` remain explicitly unsupported. Progress retains its existing
bounded plain-JSON limitation. Native installation remains Node-free; optional
Node dependency, loader and corpus qualification remain separately gated.

Exact changed files
-------------------

- `tools/NodeCommandInputBridge/module-loader.mjs`
- `tools/NodeCommandInputBridge/tool-progress.mjs`
- `tools/NodeCommandInputBridge/notification-publication.test.mjs`
- `tools/NodeCommandInputBridge/notification-plan.proposed.json`
- `tools/NodeCommandInputBridge/notification-pin-refresh.request.json`
- `tools/NodeCommandInputBridge/NOTIFICATION-FAILURE-HANDOFF.md`

Global ledgers, original fixtures, active admission files, native projects and
unrelated .NET projects are untouched. No main merge was performed.
