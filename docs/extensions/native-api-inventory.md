# Native extension surface inventory

This is the source-linked P6-01 inventory for Pi v0.99.1 at `d86654abb8862e201933517d6f1fce9f88dd117f`. It records required work before the native SDK API approval baseline. It does not approve a native ABI or qualify extension behavior. Every required behavioral row remains **Deferred**; P6-01, P6-G, P7-G and the broader P1-04 census gate remain **HOLD**. The authoring owner remains `gpt-6.1-sol`, reasoning effort `xhigh`.

The complete machine-readable record is [native-surface.json](../../compatibility/extensions/native-surface.json). The source bytes come from the already acquired, committed official [source archive](../../artifacts/released-baseline/pi-0.99.1-source.tar.gz), SHA-256 `4d99d3c9ed6db41f88ce7ba36d478b06a9386f4e81fa0c1ea3f93e681c99e83b`, 8,646,242 bytes. The [release inspection](../../artifacts/released-baseline/inspection.json) records comparison of all 2,093 canonical files with zero differing tracked files, and separate publisher-added catalog data. Its release tag/asset provenance is recorded evidence, not a signature or immutable-release attestation. The historical [baseline lock](../../compatibility/baseline.lock.json) independently pins the extension types, loader and runner Git blobs. This inventory changes neither artifact.

## Census and evidence boundary

The inventory retains 185 named `types.ts` declarations including function overloads: 180 exported declaration rows and five local supporting declarations. It records all 562 direct interface-member signatures, including optional/readonly flags, overload identity, exact callback and nested-inline-option grammar, source offsets, lines and SHA-256 of each selected source range. It adds 177 focused surface rows, all 41 `ExtensionAPI.on` event names, two shared event-bus methods, and 49 context-facade signatures: 15 selected `SessionManager` read methods, four selected footer methods, and 30 `ModelRegistry` method/overload/implementation rows. These counts overlap; they are not a sum of distinct implemented capabilities.

Forty-five retained source files have byte count, SHA-256, raw Git blob SHA-1 and commit-addressed primary source links. Seventy-four direct static/dynamic-import text references from nine extension/communication seam files retain their exact signatures and resolved local target paths. Full pinned extension, RPC UI, custom-provider and virtual-model guides are included as hashed source evidence. The inventory differentiates declarations exported directly from `types.ts`, through the extension barrel, and through the package root. For example, `ReplacedSessionContext` and `ExtensionMode` appear in the extension barrel but are absent from the pinned package-root extension re-export lists. Their required behavior remains visible.

The validator uses a comment/string-aware balanced-delimiter lexical scanner over these exact files. It cross-checks declarations and direct members with separate line-heading censuses and compares the generated inventory against retained archive bytes. This establishes a bounded direct declaration census. It is **not a TypeScript AST/type-checker proof**, a recursive expansion of imported type utilities, or a complete public-method census of every concrete class reachable through arbitrary deep imports. Inline nested fields remain in their enclosing exact signature; they are not counted as independently AST-expanded properties. General regex literals and nested template/interpolation grammar are outside the scanner certification. Unsupported interface-member/end grammar fails explicitly. The `P1-04-AST-transitive-census` blocker remains open.

`gitBlob` is computed locally as SHA-1 over `blob <byte count>\0` plus exact source bytes. No Git invocation, source module import, renderer, provider, subprocess, package installation, archive extraction or asset execution occurs in this lane. Selected source files use the archive's exact LF bytes; no CRLF conversion, string normalization, object-key normalization or numeric-value normalization is applied to source signatures.

## Registration and host actions

`ExtensionAPI` has 73 direct member rows: 41 event overloads and 32 remaining rows, including the two provider-registration overloads. Required registrations/actions are:

| Boundary | Required members |
|---|---|
| Subscription | `on` for every event below; returned per-registration unsubscribe |
| Tools | `registerTool`, `getActiveTools`, `getAllTools`, `setActiveTools` |
| Commands, shortcuts and flags | `registerCommand`, `registerShortcut`, `registerFlag`, `getFlag`, `getCommands` |
| Rendering | `registerMessageRenderer`, `registerMarkdownTransformer`, `registerEntryRenderer` |
| Messages and durable records | `sendMessage`, `sendUserMessage`, `appendEntry` |
| Session metadata | `setSessionName`, `getSessionName`, `setLabel` |
| Host execution/settings | `exec`, `getSettings` |
| Model/thinking selection | `setModel`, `getThinkingLevel`, `setThinkingLevel` |
| Providers | `registerProvider(Provider)`, `registerProvider(name, ProviderConfig)`, `unregisterProvider` |
| MCP | `registerMcpServer`, `unregisterMcpServer`, `getMcpServers` |
| Virtual models | `registerVirtualModel`, `unregisterVirtualModel` |
| Shared bus | `events.emit`, `events.on` |

The proposed native owner is explicit per row: registry coordinator for owner/registration/lifetime identity, tool adapter for tool descriptors, action broker for execution and session actions, UI adapter for renderer ownership, and provider/catalog/auth adapters for providers, MCP and virtual models. All consume host-owned contracts. These are assigned integration responsibilities, not existing approved ABI type layouts.

The source loader awaits synchronous or asynchronous factories, keeps registrations on the extension object, stages flag defaults and selected runtime changes, commits after factory success, and discards loading bus subscriptions/pending changes on failure. A native transaction must account for every owned registration and cannot undo arbitrary plugin side effects. The pinned guide prohibits starting ongoing processes, sockets, watchers or timers in factories; resources start at session activation and close idempotently at shutdown.

Collision policies differ by capability in source. Within an extension, maps replace repeated tool/command/flag/shortcut/renderer identities; one Markdown transformer is retained. Across extensions, runner tool/flag and custom renderer lookups take the first matching extension; Markdown transformers aggregate; colliding command invocation names are disambiguated; shortcuts protect reserved core bindings and otherwise permit later extension replacement with diagnostics. MCP validates ownership/configuration, and registered virtual/provider identities have their own replacement/restoration rules. The native collision/security policy needs its own explicit approval and fixtures; no single inferred collision policy is claimed.

`events.emit` returns synchronously while async safe listeners continue without an awaited publication barrier. Listener failure is caught/logged by the bus wrapper. `pi.on` dispatchers instead await their captured handler lists. The native observation prototype cannot treat these two mechanisms as the same contract.

## Contexts and permissions

| Context | Required direct members | Ownership and allowed work |
|---|---|---|
| `ProjectTrustContext` | `cwd`, `mode`, `hasUI`, UI `select/confirm/input/notify` subset | Trust decision before project code activation; restricted context |
| `ExtensionContext` | `ui`, `mode`, `hasUI`, `cwd`, `sessionManager`, `modelRegistry`, `model`, `scopedModels`, `thinkingLevel`, `isIdle`, `isProjectTrusted`, `signal`, `abort`, `hasPendingMessages`, `shutdown`, `getContextUsage`, `compact`, `getSystemPrompt` | Ordinary event/tool context; bounded actions; no command-only wait/replacement |
| `ExtensionToolContext` | Inherits ordinary context; adds `tools`, `executeTool` | Nested normal P3 pipeline, final argument checks, parent-call IDs and caller-owned default signal |
| `ExtensionCommandContext` | Inherits ordinary context; adds `getSystemPromptOptions`, `waitForIdle`, `newSession`, `fork`, `navigateTree`, `switchSession`, `reload` | User command boundary; session changes/idle wait unsafe inside lifecycle barriers |
| `ReplacedSessionContext` | Inherits command context; adds awaited `sendMessage`, `sendUserMessage` | Fresh replacement-session work; old captured context invalid after switch/fork/new/reload |

Source ordinary/context getters are synchronous and lazy, and source command-context construction preserves getter descriptors to retain stale checks. `compact` is fire-and-forget with callback completion/error options. A native cancellation contract must separate operation, session generation and extension lifetime tokens and must not convert existing synchronous getters into asynchronous bridge calls. Actual stale-generation and reentrant lock/barrier fixtures remain required.

`ReadonlySessionManager` is a typed `Pick` of 15 source methods, not a runtime security sandbox or a deep immutable snapshot. Its inventory includes working/session paths and IDs, selected leaf/entry/labels, active branch, context/projection, header, all entries, tree and session name. Branch-sensitive reconstruction must use the active branch rather than abandoned alternative histories. Source command `setup` receives a concrete `SessionManager`; the native ABI must express owned initialization actions instead of exposing the store.

`ModelRegistry` is a concrete source compatibility facade, not merely model metadata. It exposes synchronous catalog lookup plus refresh, credential resolution/status, provider reads/writes, virtual-model changes, normalized stream/complete and classifier operations. All 30 source signature rows remain visible. Its raw SDK/runtime objects and credentials require host-owned native broker capabilities with explicit permission/secret ownership, rather than crossing the stable ABI accidentally. Footer factories similarly receive a four-method read/subscription capability with an owner-bound unsubscribe.

## Tools, results, providers and UI

`ToolDefinition` retains all 19 members: identity/label/description, prompt snippet/guidelines, TypeBox parameters, constrained sampling, render shell, `prepareArguments`, output schema, exposure, namespace, annotations, default activation, `prepareLoadout`, execution mode, execution, call rendering and result rendering. `ToolLoadout` retains declared/callable/registered sets and exposure/namespace lookups. The five exposures (`direct`, `model-only`, `codemode`, `deferred`, `hidden`) have different model visibility/callability/activation semantics. `hidden` withdrawal is not an invented unregister-tool API. Author annotation hints are not verified authorization facts.

`prepareArguments` and `prepareLoadout` are synchronous. Tool execution is promise-returning; its update callback is source-defined `void`, invocation-scoped, and late updates are ignored by the upstream tool runtime. `AgentToolResult` has required content/details and optional structured content, usage, error and termination fields; nested usage/result ownership follows P3. Tool-result event patches expose only content/details/structuredContent/isError/usage, with no termination patch field. Raw result fields and patch fields must remain distinct. Renderer callbacks return local components and receive shared rich state, prior component, redraw invalidation, complete-argument/progress/error flags and working directory. This rich JS/local component state needs an explicit native TUI boundary and bridge handles; it cannot be represented as ordinary JSON by assumption.

`RegisteredCommand` retains argument completions that may return synchronously or as a promise, and an awaited command handler. Shortcuts and general extension handlers permit synchronous or promise callbacks; factory initialization permits both. The signatures retain nested callback alternatives even where the inventory's broad callback-shape label does not type-check them.

Provider registration retains both full `Provider` and legacy `ProviderConfig` forms, image/classifier implementations, model variants, request instrumentation, custom headers, auth, refresh/publish and OAuth callbacks. Legacy refresh returns model definitions; a full provider's publish/getModels contract differs. Startup queuing and runtime immediate registration, unregister/built-in restoration, cancellation and credentials remain required qualification rows. Virtual routing retains synchronous/promise route callbacks and branch-owned JSON state semantics; direct requests and state persistence differ. MCP server registration is required and can lead to process/network operations in the host; this source-only inventory performs none.

All 29 `ExtensionUIContext` direct signatures remain required, including both widget overloads: dialogs; notification; terminal input; status/working/thinking indicators; widgets/footer/header/title; custom focus/overlay components; paste/editor text/editor; autocomplete/editor factories; theme/catalog switching; and tool expansion. The pinned guides distinguish full TUI, RPC-supported dialogs/notifications/text widgets, and no UI in JSON/print. RPC custom components and several TUI-only methods return defaults/no-ops; widget factories are ignored there. Native unavailable confirmation must not become approval. Capability diagnostics and explicit intentional mode differences require tests; this inventory does not accept silent fallback as native coverage.

## Complete event families

All rows below are mandatory, source-linked in the JSON and behaviorally Deferred. Ordinary `ExtensionHandler` is `(event, ExtensionContext) => Promise<result | void> | result | void`; trust uses its special context. Captured dispatch order is extension order then handler-registration order. Add/remove during an active dispatch does not change its captured list. Per-event scheduling, error policy and reducer are recorded individually.

| Family / source dispatcher | Event names |
|---|---|
| Trust / `emitProjectTrustEvent` | `project_trust` |
| Discovery / `emitResourcesDiscover` | `resources_discover` |
| Session observations / `emit` | `session_start`, `session_info_changed`, `session_compact`, `session_compact_failed`, `session_shutdown`, `session_tree` |
| Session pre-action / `emit` | `session_before_switch`, `session_before_fork`, `session_before_compact`, `session_before_tree` |
| MCP / `emit` | `mcp_servers_change` |
| Conversation transforms / `emitContext` | `context`, `context_with_system` |
| Cache / `emitCacheWarmingDecision` | `cache_warming_decision` |
| Provider / individual request/header dispatchers and `emit` | `before_provider_request`, `before_provider_headers`, `after_provider_response`, `provider_stream_event` |
| Prompt / `emitBeforeAgentStart` | `before_agent_start` |
| Agent observations / `emit` | `agent_start`, `agent_end`, `agent_settled` |
| Actionable boundaries / `emitBoundary` | `agent_before_settle`, `turn_end` |
| UI observations / `emit` | `ui_prompt_start`, `ui_prompt_end` |
| Turn observation / `emit` | `turn_start` |
| Message observations / `emit` | `message_start`, `message_update` |
| Final message / `emitMessageEnd` | `message_end` |
| Tool execution observations / `emit` | `tool_execution_start`, `tool_execution_update`, `tool_execution_end` |
| Selection / `emit` | `model_select`, `thinking_level_select` |
| Tool policy/result / individual dispatchers | `tool_call`, `tool_result` |
| User shell / `emitUserBash` | `user_bash` |
| Input / `emitInput` | `input` |

Reducers cannot share a generic last-result-wins rule:

- Input transformations compose; handled short-circuits. Images use nullish fallback.
- Tool-call input mutation composes on the shared event; first block stops. Handler rejection propagates to block execution in the caller pipeline.
- Tool-result defined-field patches compose. Content replacement without non-undefined structured content deletes stale structured content; null and undefined remain distinct.
- Final-message replacement composes while preserving the current role. A role change is diagnosed/skipped; handler failures are reported and dispatch continues.
- Session pre-actions retain each latest truthy result and stop at cancel. A handler throw is reported/continued and is not itself cancellation.
- User shell accepts the first valid exactly-one operations/result claim. Invalid result or failure is diagnosed and rethrown; local execution must not follow that failure.
- Conversation-only transforms precede full transcript transforms. System/tool replay state is restored after changed conversation lists; full-transcript loss of leading system is diagnosed but honored in source.
- Request replacements compose for every non-undefined return, including null. Header return values are ignored while shared header mutations compose; provider-boundary null deletes a header.
- Prompt options are shared structured sections with explicit full-prompt override and aggregated message contributions.
- Boundaries compose entries/continue and rebuild preview after each handler; a later handler can repair invalid entries. Final invalid state clears entries/continuation. Persistence belongs to the caller's validated transaction.
- Cache warming uses the last explicit action. Resource discovery aggregates provenance-tagged paths. Trust continues on undecided and takes the first other decision; this helper is not a general runtime result validator.

UI prompt events are queued as microtasks with `void emit`, so the surrounding UI call does not await the observation boundary. MCP registry change likewise invokes `void emit` before missing-handler diagnostics. Provider stream guide semantics require awaited parsed-event observation before normalization. A native awaited barrier profile must retain and qualify these actual scheduling differences, rather than filtering them out of traces. Observation event types are notification-only, but source JS objects are not universally deep frozen; native immutable snapshots are an intentional contract choice needing approval.

## Native syntax and closure responsibilities

Intentional native syntax is recorded separately from Deferred behavior: `IPiSharpExtension : IAsyncDisposable`, `InitializeAsync(IExtensionRegistry, CancellationToken)`, host-owned schemas/content/errors/JSON/identifiers, capability/version discovery, immutable typed decisions, and optional native TUI contracts replace TypeScript generics/JS mutation/terminal objects. The exact stable ABI, names, layouts and forbidden dependency/API approval tests are still pending. The current registration ownership prototype is experimental and cannot qualify this wider inventory by implementing a sample subset.

Required integration rows retain transactional registration, trust-before-activation/discovery/loading, final tool policy/nested execution, session generations and branch state, UI/provider/MCP/virtual adapters, drain/cancel/dispose/reload/unload, and SDK/package release evidence. P6 requires cooperative reload evidence and truthful restart-required handling for rooted/hung code. Collectible ALC loading and same-user Node processes are not security sandboxes.

The optional Node bridge retains the full approved Phase7 plan. P7-01 still needs 8-12 real pinned unmodified extension scenarios, source/license/import/API/dependency evidence, actual upstream/prototype executions and proceed/narrow/defer decision. P7-02 needs a per-export/method Tier A/B/C matrix with diagnostics for unsupported registrations. This inventory supplies neither gate. P3 authorized actions, P4 immutable snapshots/generations, P5 UI capabilities and approved P6 registry/reducer/lifetime contracts precede protocol/full-duplex callbacks, workers, loader aliases, synchronous read snapshots, mutation translation, recovery and corpus certification. Native-only installation must remain Node-free.

## Offline validation and remaining blockers

Root owns execution and review. From the isolated checkout or an exact committed review checkout containing retained artifacts:

```powershell
& 'P:\PiSharp\root\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe' tools/SurfaceInventory/native-extension-surface.test.mjs
```

The command accepts no arguments, verifies frozen inventory/authority/archive/reader/parser/plan bytes, reconstructs the direct census, and rejects 21 deliberate deletions or false classifications. It also includes an authored nested-signature/overload parser probe and an unsupported-member rejection probe: 24 expected checks in total. This document does not claim they have run. Root's reported execution result is separate evidence. The script writes only its JSON report to stdout and has no Git/compiler/network/install/subprocess/extraction path. Developer tooling uses the already pinned Node 24.19.0; no Node dependency is added to PiSharp's native ABI/runtime.

Remaining blockers are AST/transitive-public-type closure, actual mode/provider/extension behavior fixtures, reviewed stable ABI/consumer/forbidden-dependency approval, integration of every mandatory adapter, and optional bridge feasibility/support/certification. The licensing/redistribution matrix's existing HOLD is unchanged. No old fixture, baseline lock, phase status, notice, upstream source or qualified oracle was edited.
