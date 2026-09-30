# PiSharp architecture and implementation strategy

Status: proposed implementation plan. No implementation or conformance results are claimed.

PiSharp is a fresh C# and .NET 10 port of the Pi coding-agent product, with native C# plugins and an optional Node.js compatibility bridge. The native core has no Node requirement.

## 1 Recommended approach

Build PiSharp as a fresh, cross-platform .NET 10 implementation of Pi’s stable coding-agent experience. Preserve observable behavior, session data and automation contracts, while expressing the internals as small C# libraries. Start with a headless agent and compatibility tests, then build the terminal experience on the same engine. Treat extension support as a core architectural concern from the first milestone. Implement the orchestration directly in C#; do not introduce another agent framework unless a measured compatibility benefit justifies it.

The extension strategy is a first-class native C# SDK, a versioned out-of-process protocol and a separately scoped Node.js compatibility host for selected existing Pi TypeScript extensions. The bridge advertises a tested subset rather than unrestricted source compatibility. PiSharp runs fully without Node.

This plan does not include code changes or executed conformance tests. The initial scope is the terminal coding-agent product, RPC and SDK, including the behavior they need from the wider Pi repository. Desktop/web products and a port of every experimental framework, service and storage backend are separate expansions.


| Decision | Recommended default | Why |
| --- | --- | --- |
| Product | PiSharp; fresh standalone repository | Independent ownership, packaging and versioning |
| Runtime | net10.0 with a JIT runtime | Supports dynamic native extensions; .NET 10 is LTS through 14 November 2028 [N1] |
| Compatibility | Pi v0.99.1 at an immutable commit | A fixed target makes parity measurable while upstream continues changing |
| Extensions | Native C# plugins; optional Node bridge | Design decision; native core has no Node requirement |
| Existing TypeScript | Opt-in Node bridge with a support matrix | Useful migration path without claiming unrestricted source compatibility |
| Persistence | Pi-compatible JSONL sessions; separate PiSharp home | Keeps import and interchange possible without changing existing Pi data |
| Delivery | Headless slice, SDK, CLI, TUI, then release hardening | Tests the hard contracts before terminal presentation hides defects |

## 2 Baseline and the meaning of a port

### Source baseline

The former badlogic/pi-mono URL now resolves to earendil-works/pi. Use release v0.99.1, released 29 September 2026, pinned to commit d86654abb8862e201933517d6f1fce9f88dd117f. Record the SHA in fixtures, build metadata and compatibility reports. Do not compare PiSharp against a moving main branch. [P1, P2]

The release contains 14 workspace packages, including ai, agent, coding-agent, tui, chord, telemetry, codemode, mcp, durable, protocol, client, server, evals and session-backends/sqlite-node. The published CLI depends on more than the older, commonly described AI/agent/TUI layers. Its required dependency behavior belongs in the inventory; experimental client/server and durable framework APIs do not automatically become public PiSharp requirements. [P2, P3]

### Three separate compatibility promises


| Compatibility | Target | Evidence required |
| --- | --- | --- |
| Behavioral parity | Same supported workflows and meaningful outcomes, including ordering, cancellation and errors | Differential traces, deterministic tool tests and end-to-end scenarios |
| File and wire compatibility | Read/write the supported Pi session format and implement the selected RPC contract | Round-trip fixtures, schema checks and an unchanged reference RPC client |
| TypeScript source compatibility | Only the bridge subset declared for the selected release | Unmodified real extensions exercised through a capability matrix |

Matching command names or providing similar features is not enough to claim parity. Maintain a machine-readable manifest with one row per public command, tool, event, session entry, provider capability and extension hook. Each row must be Supported, Intentionally different, Deferred or Out of scope, with a test and a reason. Any mandatory item left Deferred blocks a full native v1 parity claim.

### Scope for native version 1

- Include the coding-agent SDK, interactive CLI, print/JSON/RPC modes, built-in tools, sessions and branching, compaction and context editing, model/provider selection, configuration and authentication flows required by the pinned product, resource loading, commands, prompts, skills, themes, native extensions, and required MCP/codemode behavior.
- Inventory all provider and model transport capabilities at the baseline. Implement representative providers first; finish the mandatory provider matrix before calling the native v1 port complete. An earlier preview may ship with a clearly stated subset.
- Defer separate server/client products, full Chord/Pico or durable APIs, alternate SQLite backends, browser/web interfaces and unrelated monorepo services unless the baseline proves a dependency is required for stable CLI behavior.
- Do not make a desktop GUI, a hosted service, a new agent framework or unrestricted JavaScript compatibility prerequisites for the first native port.
## 3 Proposed architecture

Use dependency inversion around a small, terminal-independent runtime. Keep Pi’s wire DTOs separate from native public APIs: a serialization change should not force a breaking C# SDK change, and a convenient C# type should not silently alter a Pi-compatible payload.


| Project or package | Responsibility | Dependency rule |
| --- | --- | --- |
| PiSharp.Contracts | Messages, content blocks, model capabilities, errors, identifiers and versioned event contracts | No terminal or provider SDK references |
| PiSharp.AI | Provider registry, streaming adapters, request translation, authentication abstractions and usage | Depends on Contracts |
| PiSharp.Agent | Turn state machine, tool scheduling, steering, follow-up and cancellation | No filesystem session or UI assumptions |
| PiSharp.Sessions | JSONL store, tree projection, compaction checkpoints, imports and context edits | Uses Agent contracts |
| PiSharp.Tools | Read, write, edit, shell and other baseline tool behavior; path/process adapters | All host-mediated actions pass policy |
| PiSharp.Extensions.Abstractions | Stable extension entry point, descriptors, typed hooks and host services | Minimal, versioned public surface |
| PiSharp.Extensions.Runtime | Discovery, manifests, dependency loading, dispatch and lifecycle | Host implementation only |
| PiSharp.ExtensionHost | Worker supervision; optional PiSharp.Compatibility.Node bridge | IPC contract separated from SDK |
| PiSharp.CodingAgent | Composition root, settings/resources, sessions, provider and extension coordination | Embeddable without CLI |
| PiSharp.Cli and PiSharp.Rpc | Argument handling, print/JSON modes and newline JSON automation | Thin adapters over CodingAgent |
| PiSharp.Tui | Terminal rendering, editor, input, dialogs and custom component host | No ownership of agent state |
| PiSharp.Compatibility.Tests | Golden fixtures, reference harness, session/RPC compatibility and matrix reports | Tests the pinned Pi release |

A compact repository can start with fewer physical projects and split only when boundaries stabilize. Preserve these logical responsibilities immediately. Use nullable reference types, analyzers, deterministic builds, dependency lock files and central package version management. Prefer records and explicit discriminated hierarchies for messages and events; keep unknown JSON fields available for compatible round-tripping.

Add PiSharp.Extensions.Sdk for templates and schema/binding helpers, PiSharp.Mcp and PiSharp.Codemode for required integrations, and PiSharp.Rpc.Client/PiSharp.Testing for client and harness utilities when their boundaries warrant separate assemblies. Keep host/DI dependencies at the composition boundary.

### State ownership

A session coordinator owns committed state and sequence numbers. A turn runner owns the current model stream and tool batch. Providers own transport state. Extensions receive scoped contexts, and frontends render observations. No frontend, extension or provider adapter can independently mutate the authoritative transcript.

Serialize short state transitions, but release coordination locks before awaiting providers, extension hooks, user dialogs, IPC or nested tools. Resume with a generation check so a response from an old session cannot affect a replacement session. Make state transition tests independent of network timing.

## 4 Provider and streaming design

Define a PiSharp-owned provider interface that returns IAsyncEnumerable of typed events and a finalized assistant result. Represent text, reasoning, images, tool calls, usage and provider-specific metadata without collapsing them into a single string. Use System.Text.Json for serialization and preserve unknown provider fields where the baseline requires them.

Use Microsoft.Extensions.AI as an optional interoperability adapter, not as the sole compatibility model. Its IChatClient abstraction supports streamed multimodal chat and its ecosystem includes telemetry and function invocation. PiSharp should own tool invocation itself, so upstream ordering, authorization and continuation semantics are not replaced by generic middleware. Prove each mapping with fixtures before adopting it. [N2]

The baseline separates provider identity from wire API and includes ten chat protocol IDs. Design IModelProvider for catalog/authentication separately from IChatTransport; handle SSE and any required WebSocket transport explicitly. Preserve indexed/interleaved content blocks and opaque reasoning metadata. A setup error can precede start, while successful streams need balanced content events and a terminal result. Avoid cloning the whole transcript on every token. [P17, P18]

### Streaming contract

- Separate transport fragments, incremental content updates and committed messages. Partial tool-call JSON is display state only; it must not authorize execution before final parsing and validation.
- Assign stable message, content-block and tool-call IDs. Preserve provider request IDs, finish reasons, usage and relevant reasoning signatures in the correct scope. Record redacted diagnostics separately from the conversation.
- Use bounded Channels where producer/consumer buffering is needed. Apply backpressure rather than dropping authoritative events. Slow terminal rendering may coalesce transient visual updates, but persistence and protocol consumers must receive every required event. Channels provide explicit capacity and full-buffer behavior. [N3]
- Propagate CancellationToken through transport, tools and hooks. Distinguish user cancellation, timeout, provider rejection, malformed stream and plugin failure. Define exactly one terminal outcome per operation, including streams that fail after partial output.
- Retry only operations proven safe. A provider retry must never rerun a completed shell command or file mutation. Apply retry limits and jitter; preserve the cause and attempt history without leaking credentials.
### Provider delivery order

First implement one direct API provider and a second provider with different streaming and tool semantics, with OpenAI and Anthropic as practical starting candidates. Next add compatible endpoints, OAuth-backed flows and remaining baseline providers in risk order. The final matrix must distinguish transport support, tools, images, reasoning controls, authentication, cancellation and usage reporting. A model appearing in a list is not proof that its capabilities work.

## 5 Agent loop and built in tools

Implement the turn runner as an explicit asynchronous state machine: assemble context, issue a provider request, finalize the assistant message, pass lifecycle barriers, preflight tools, execute the batch, commit ordered results and evaluate continuation. Keep cancellation and failure transitions visible rather than hiding them in event callbacks.

### Parity critical ordering

At the baseline, tool preflight is sequential and allowed tools normally run in parallel. A tool requiring sequential execution serializes the batch. Completion events follow actual finish order; persisted results retain assistant tool-call order. The awaited message_end barrier precedes tools. Before-tool hooks follow validation and after-tool hooks precede final events. All results in a batch must request termination to suppress automatic continuation. Steering is applied after the current assistant/tool turn, with follow-up work later. These are required compatibility tests. [P4]

Keep low-level run completion separate from settled session completion. agent_end closes one run; agent_settled indicates that automatic continuation work is finished. SessionManager is the authoritative finalized context. A prompt arriving while busy needs an explicit steering or follow-up treatment. [P5]

### Tool pipeline

For every core-mediated tool call, resolve the tool, parse and validate its arguments, apply permitted extension transforms, revalidate the final arguments, enforce authorization, execute with limits, normalize the result, run result hooks and commit the outcome. Authorization must inspect the actual final action and must also apply to nested tool invocations. Preserve baseline hook order around this pipeline; record additional policy checks as intentional hardening.

- File tools: test encodings, BOMs, Unicode paths, newline preservation, exact-match edit ambiguity, missing files, binary data, large outputs and symlinks. Use atomic replacement where possible; do not label an edit successful until its final contents are verified.
- Shell tools: provide a process abstraction with explicit executable and arguments, working directory, environment, timeout, output limit, progress and exit status. Kill the relevant process tree on cancellation and distinguish truncation from complete output.
- Search and discovery tools: decide whether to bundle utilities such as ripgrep or use managed equivalents after measuring parity. Pin and license any bundled native binaries; do not silently depend on a developer’s PATH.
The release’s tool inventory is read, bash, powershell, edit, write, grep, find and ls. The edit tool accepts disjoint edits against the original file, while retaining the legacy single replacement form; preserve its file-mutation queue. Shared truncation is 2,000 lines or 50 KiB, with UTF-8-safe head/tail behavior chosen by tool. These are explicit golden fixtures, not approximate UX details. [P14, P15, P16]

- MCP and codemode: inventory the actual baseline call path, tool schemas, lifecycle, permissions and result envelopes. Implement only the required surface initially, but make missing features explicit rather than silently bypassing them.
Parallelism is a compatibility behavior, not permission to introduce data races. Tag tool operations with execution classes and resource information where useful, while retaining the baseline scheduler’s required behavior. Any stronger conflict serialization is a declared deviation tested for user impact.

## 6 Sessions context and persistence

Preserve the CLI’s JSONL version 3 entry tree, including entry IDs, parent IDs and branch-relative context projection. Account for separate header and message timestamps, model/thinking changes, usage, system-message deltas, compaction and branch summaries, labels, names, custom state, custom context messages and context_edit entries. The newer durable/SQLite infrastructure is a separate concern and is not a reason to replace the CLI session format. [P6, P7]

### Storage rules

- Use a dedicated PiSharp home directory by default. Import from an explicit path or copy; never silently overwrite Pi’s settings, credentials or session files.
- Keep an append-only event log as the source of truth and derive current branch/context projections from it. Unknown entries and fields should survive import/export without being interpreted as active instructions.
- Add legacy v1/v2 session import as a compatibility tranche, preserving the original file and reporting migration. Namespace extension records by extension ID, kind and schema version; persist JSON data rather than CLR type names or delegates. Rehydrate branch-sensitive extension state from the active branch.
- Serialize writers per session. Define durable flush checkpoints, interrupted-tail recovery, file locking, size limits and backup behavior. A corrupt or truncated tail must be reported and recoverable without destroying prior entries.
- Offer a Pi-compatible export and a native export when additional PiSharp state cannot be represented losslessly. Compatibility export must disclose unsupported native data rather than discard it silently.
### Context management

Model context is a projection, not the raw log. Implement compaction, branch switching and context edits against explicit checkpoints and fixtures. Preserve tool-call/result pairing and the intended system prompt when trimming context. Summaries and tool output remain untrusted data. Capture token estimates as estimates until a provider supplies authoritative usage.

A session switch, fork, reload or cancellation increments a generation token. Outstanding workers may finish cleanup, but their stale results cannot append to the new active branch. Make recovery, session deletion and export behavior independently testable.

## 7 Extension architecture

The native SDK should make the safe, common path simple: register tools and commands, subscribe to typed lifecycle hooks, request a dialog, store extension state and contribute status information. Keep the public abstraction package small enough to support third-party extensions without tying them to the CLI implementation.


| Extension route | Best use | Limit and recommendation |
| --- | --- | --- |
| Compiled C# | First-class PiSharp tools, hooks and terminal features | Primary v1 route; explicit installation and trust |
| Worker process | Language-neutral tools and hooks; restartable plugins | Versioned JSON protocol; crash isolation alone is not a security sandbox |
| Node bridge | Migration of selected existing Pi extensions | Opt-in; exact compatibility manifest and unsupported-API errors |
| C# scripts | Quick local experiments | Optional later feature; compile to a worker or trusted assembly, with clear trust prompts |
| Embedded JavaScript engine | Small pure-language scripts | Do not use as the general Pi compatibility strategy; Node/npm and TUI APIs remain missing |

### Native package and manifest

Define a manifest containing extension ID, version, entry point, PiSharp SDK compatibility range, runtime requirements, dependencies, declared capabilities and optional UI capabilities. Lock resolved package versions and hashes. Support local development folders and explicit packaged installs; do not execute code merely because a repository includes an extension file.

Package the published extension output with its .deps.json and private dependencies, while sharing PiSharp.Extensions.Abstractions from the host. Validate framework/runtime targets, archive paths and symlinks before code executes. Resolve name collisions deterministically, require explicit built-in override intent and reserve core policy controls. Add API semver checks and optional capability interfaces rather than repeatedly changing the required entry interface.

Use one AssemblyLoadContext and AssemblyDependencyResolver per trusted extension to avoid most dependency collisions. Share the exact contract assembly from the default context so interface identity is consistent. Unload is cooperative, so leaked event handlers, tasks or static references may require a host restart. AssemblyLoadContext is not a security boundary; untrusted code must not be loaded into the trusted process. [N4, N5, N6]

### Native SDK shape

- IPiSharpExtension supplies registration and asynchronous lifecycle cleanup. IExtensionRegistry registers tools, commands, shortcuts, renderers and typed hook handlers. Keep descriptors immutable after registration where possible.
- IExtensionContext exposes scoped read-only session/model views and narrowly defined host actions. A separate command context provides idle-wait and session-changing operations, preventing lifecycle callbacks from triggering self-deadlocks.
- ToolDefinition declares JSON Schema, execution behavior and an asynchronous implementation. ToolResult separates model-visible content, structured data and presentation metadata. Test native JSON Schema validation against Pi’s baseline schemas.
- Typed hook results express Continue, Replace, Block, Cancel or Contribute as appropriate to the event. Prefer explicit replacement values to shared mutable dictionaries, while preserving observable Pi composition semantics.
- IExtensionUi covers dialogs, notifications and status. Put custom terminal components in a separate PiSharp.Tui.Abstractions package so a headless worker need not reference terminal internals.
### Extension lifecycle

Discover and validate, resolve dependencies, establish trust, load, register, activate for the session, dispatch, cancel and dispose. Long-lived resources start on session activation, and shutdown must be idempotent. Track every handler, timer, child process and disposable subscription so reload can clean them up. Reject calls made through stale session contexts. Failure reports identify the extension, operation and recoverable next step.

Stage registrations transactionally before activation; roll back host registrations on failure. Reload at a safe idle/cancel boundary, stop new calls, quiesce work, cancel lifetime tokens, detach callbacks and invalidate the session generation before activating the replacement. Use versioned extraction or shadow copies for Windows. If a trusted in-process plugin hangs or leaks roots, report restart-required; only process termination provides a hard reset. Preserve the previous package/config for rollback.

## 8 Hook ordering and reentrancy

Port reducers deliberately. Pi hooks do not all use the same return-value rule. The runner snapshots subscriptions and awaits handlers in registration/load order. Input transformations chain; handled input short-circuits. Tool-call handlers can transform or block; tool-result field replacements compose. Session pre-actions can cancel. User-shell handlers claim the command, and handler failure must not accidentally fall through to a local shell. Context and provider-payload hooks have their own composition rules. Continuation hooks contribute additional work; settled notification is observation only. [P8]

Encode one reducer per event family and test the reducer independently. A bridge can translate JavaScript mutation into explicit C# results, but must preserve the downstream observable result. Clearing structured tool content when a content replacement omits it, preserving message roles and distinguishing mutation-only header hooks are examples that need targeted fixtures. [P8, P9]

### Dispatch and deadlock rules

- Separate ordered hook dispatch from asynchronous observation. A channel broadcast cannot substitute for an awaited lifecycle barrier.
- Never hold the session coordinator mutex while invoking extension code or waiting on a worker response. Use immutable snapshots and version-checked commits.
- Allow nested ExecuteTool only through the same registry, validation, authorization, cancellation and hook path. Bound recursion and detect cycles; do not expose an unchecked internal bypass.
- Session-changing commands wait for a safe boundary through their distinct command context. A lifecycle handler must not await the same run that is awaiting that handler.
- Parallel sibling tools do not imply a global total event order. Capture the partial ordering that matters and retain deterministic transcript commit order.
### Mode specific user interfaces

Expose UI capabilities explicitly. The baseline terminal can host custom components; RPC supports a narrower dialog/notification route; JSON and print modes have no interactive UI. A generic hasUI flag is insufficient to infer terminal component support. If a confirmation UI is unavailable, fail or return a declared unavailable result; never interpret its absence as approval. [P9, P10]

## 9 Existing JavaScript and TypeScript extensions

A Node sidecar is the strongest practical compatibility option because existing Pi extensions may depend on Node APIs, npm packages and Pi’s module interfaces. A generic embedded JavaScript engine does not provide that environment. The bridge should emulate only a published contract, use the pinned upstream loader behavior as a reference and keep Node out of a native-only installation.

### Bridge contract

Run an extension worker over framed JSON messages with a negotiated protocol version, request IDs, session generation, cancellation, deadlines and explicit capability declarations. Reserve stdout for protocol traffic and stderr for logs. Support callbacks in both directions without blocking the read loop: a hook may request a dialog or execute a host tool while the original hook call remains in flight.

- Serialize descriptors, messages, tool arguments/results and typed hook decisions. Functions, arbitrary class instances, cyclic objects and terminal component objects cannot cross the process boundary directly.
- Keep module resolution, transpilation and extension dependency installation inside a documented compatibility host. Treat npm lifecycle scripts and native modules as executable code requiring trust; do not run install hooks during ordinary discovery.
- Convert unsupported registrations into clear startup diagnostics. Never accept a callback that will silently be ignored later.
- Preserve synchronous Pi getters with local snapshots where sound; do not silently turn synchronous APIs into async calls while claiming unchanged source compatibility. Start with one worker per compatibility session to preserve cross-extension ordering, and document its shared failure/trust scope.
- Attach handler IDs and resource handles, and revoke them on unsubscribe/reload/session replacement. Test worker crashes before, during and after a callback; never replay an action with side effects merely to recover the worker.
### Compatibility tiers


| Tier | Bridge target | Release evidence |
| --- | --- | --- |
| A | Non-UI tools, commands, simple lifecycle hooks and persisted state | Unmodified representative extensions pass the same scripted scenarios |
| B | Dialogs, status, provider hooks and richer orchestration | Bidirectional RPC, capability, cancellation and reducer tests pass |
| C | Custom terminal components and deep imports into Pi internals | No initial compatibility promise; rewrite to native SDK or investigate a separate UI host |

Choose a representative extension corpus during the baseline milestone: at least one custom tool, command, input transform, tool blocker, context transform, persistent-state extension, provider customization and UI dialog. Add a custom component example as an expected unsupported case. Coverage should be API-based, not a claim based on a few successful demos.

### Migration path

Ship a mapping guide from Pi APIs to PiSharp APIs, sample native ports, a manifest checker and a diagnostic report listing supported/unsupported registrations. Keep file conventions compatible where practical, but use distinct package names and a declared SDK version. The native port remains useful without the Node bridge.

## 10 Terminal command line and operating systems

Implement print/JSON and RPC modes before the full-screen TUI. This creates a stable test surface and allows embedding before terminal work is complete. Keep command parsing, output formatting and exit codes separate from the application runtime.

### RPC compatibility

The baseline RPC protocol uses LF-delimited JSON and accepts CRLF, with optional string correlation IDs and asynchronous response ordering. Keep stdout protocol-only and continuously drain output. Prompt acceptance is distinct from completion; handled prompts may start no run. Use agent_settled as the completion boundary. Treat stdin closure as orderly disposal. Do not split on Unicode U+2028 or U+2029. [P11]

### Terminal design

The baseline TUI supports main-screen scrollback and an alternate-screen mode, differential rendering, synchronized writes, raw input/paste, focus/overlays and terminal images. Its components render bounded-width lines. Use those behaviors to judge the prototype rather than choosing a library on appearance alone. [P13]

- Prototype the renderer before selecting a UI dependency. Test streaming layout, multiline editing, selection/copy, Markdown/code display, resize, Unicode graphemes, wide characters, colors, alternate screen and scrollback behavior.
- Create ITerminal, IInputSource, IClipboard and renderer abstractions. Keep terminal capability detection and fallback modes visible. A non-TTY invocation must not emit cursor-control sequences.
- Use golden screen snapshots and pseudo-terminal integration tests. Compare functional behavior rather than requiring identical pixels where platform rendering differs.
- Support accessibility-minded defaults: readable contrast, reduced motion, text-only fallbacks and keyboard-only operation. Make status and error information available outside color alone.
### Supported platform proposal

Target Windows x64, Linux x64 and macOS arm64 for the initial CI and release matrix, then add Linux arm64 and macOS x64 when tested. Use runtime-supported OS versions at release time. Publish any narrower PiSharp matrix explicitly; .NET’s availability alone does not prove terminal, process or clipboard behavior.

Test filesystem case sensitivity, permissions, executable bits, newline conventions, path separators, spaces and long paths. Treat Bash-on-Windows and native PowerShell as distinct execution modes: changing the shell language is a compatibility change, not an invisible platform adaptation. Validate Ctrl+C, child-process termination, terminal restoration and redirected input on each supported OS.

## 11 Security packaging and maintenance

### Trust boundaries

An extension manifest is a declaration, not enforcement. A trusted in-process plugin can access filesystem, network and processes directly. A same-user worker process improves crash containment and restartability but retains the user’s OS authority unless separately restricted. If PiSharp offers an untrusted extension mode, require an actual OS sandbox, restricted identity or container policy and test it against escape attempts. [N4]

- Keep authorization checks outside extension override points and run them after final argument transformation. Apply the same rules to host tools, nested tools, direct commands, MCP actions and shell access.
- Do not autoload or trust executable repository content. Show the resolved source and permissions at explicit installation/enablement. Lock versions; verify hashes and provide disable/remove controls.
- Store credentials through platform-appropriate secret storage where available. Redact secrets from exception messages, transcripts, diagnostics and telemetry. Never export auth data with sessions.
- Enforce request, output, file, memory, recursion and execution limits. Validate plugin protocol frames and tool schemas. Treat model output, files, tool results and retrieved text as untrusted inputs.
- Keep telemetry off or content-free by default, document what leaves the machine and provide local diagnostics. Record auditable action metadata without capturing unnecessary prompts or source files.
### Distribution

Start with a .NET global tool and self-contained OS-specific archives. Keep native-only installs independent of Node. Build and test signed/checksummed releases where the chosen channels support them, publish an SBOM and retain upstream copyright and license notices. Audit the pinned repository and bundled dependencies before redistributing code or assets.

The upstream release is MIT licensed; retain the required copyright and permission notice with any redistributed portions. Preserve third-party notices separately and distinguish PiSharp branding from upstream. [P19]

Use a JIT host for dynamic plugins. NativeAOT does not support runtime dynamic assembly loading/code generation; consider it later for a statically composed CLI or a frontend that delegates dynamic plugins to a JIT worker. Trimming and single-file publishing require their own plugin discovery tests. [N7]

### Upstream maintenance

Separate PiSharp’s semantic version from its Pi compatibility baseline. Publish both in diagnostics and release notes. For each upstream upgrade, generate an API/schema inventory diff, refresh golden fixtures, classify changes, run differential tests and update the compatibility report before moving the baseline. Avoid silently adopting main-branch behavior.

## 12 Testing and evidence

Treat the original implementation as a behavioral reference, not merely a source of names. Use recorded provider streams and deterministic fake tools to remove model nondeterminism. Normalize only fields such as timestamps and generated IDs that are explicitly allowed to differ; never normalize away ordering, content, errors or completion semantics.


| Test layer | Required scenarios | Release evidence |
| --- | --- | --- |
| Unit and property tests | Reducers, state transitions, schema validation, Unicode/chunk parsing, context projection and serialization | All mandatory contracts pass |
| Differential agent tests | Parallel tools, sequential override, barriers, steering, follow-up, cancellation, failures and continuation | Pi and PiSharp normalized traces agree |
| Provider conformance | Fragmented content/tool arguments, malformed streams, reasoning, usage, auth expiry and retries | Per-provider capability matrix |
| Session compatibility | Import/export, unknown fields, fork/tree, compaction, context edits, interrupted writes and recovery | Golden JSONL corpus and loss report |
| RPC integration | Partial UTF-8, CRLF, out-of-order replies, accepted/handled prompts, EOF, slow readers and stderr separation | Unchanged reference client succeeds |
| Extension conformance | Ordering, cancellation reducers, reentrancy, stale contexts, unload/reload, failures and nested tools | Native SDK and bridge tier reports |
| Terminal and OS | Resize, Unicode, paste, Ctrl+C, shell differences, process trees, paths and redirected streams | CI plus documented real-terminal checks |
| Security and resilience | Untrusted input, denied actions, malformed plugin frames, secret redaction and resource limits | No unresolved release-blocking defect |
| Performance | Cold/warm start, idle/streaming memory, large sessions, event throughput and UI responsiveness | Measured budget and regression report |

### Acceptance policy

Every mandatory matrix row needs an automated test or a documented manual verification where automation is unreliable. A release requires all mandatory tests passing on supported platforms, no unresolved critical/high-severity defects, complete compatibility/deviation notes and a repeatable installation path. Define numerical startup, memory and throughput budgets after the first end-to-end prototype, then enforce them in CI. Do not invent precision before measuring.

Use live-provider smoke tests only with explicit credentials and spending controls; keep routine CI on deterministic fixtures. Maintain a small licensed, synthetic workspace corpus for tool tests. Never capture secrets or private customer repositories into golden files.

## 13 Eight phases and acceptance gates

The order below resolves semantic and extension risks before polishing the interface. Parallel work is possible after contracts stabilize, but a later milestone is not accepted merely because its UI is demonstrable.


| Phase | Deliverables | Exit gate |
| --- | --- | --- |
| 1 Freeze compatibility target | Pinned inventory, parity manifest, source/license review, fixture harness and risk spikes | G0: target and deviations approved; top risks reproduced with fixtures |
| 2 Provider layer | Contracts, registry, contrasting provider adapters, streaming, cancellation and diagnostics | G1: recorded streams produce correct finalized results; required provider matrix progresses |
| 3 Agent loop and tools | Turn loop, scheduling, tool pipeline, authorization, steering and follow-up | G2: end-to-end coding task and ordering/failure traces pass |
| 4 Sessions | JSONL store, branching, compaction, context edits, imports and SDK composition | G3a: session round-trips and active-branch context projections match |
| 5 Headless RPC then terminal UI | Print/JSON/RPC, CLI configuration/resources, full TUI and terminal capability tests | G3b: reference RPC client passes; G5 UI: terminal scenarios pass across supported OSes |
| 6 Native extension SDK | SDK, manifests, loader, reducers, lifecycle, UI capability contracts and samples | G4: representative extensions pass reentrancy, cancellation and reload tests |
| 7 Optional Node bridge | Node host, Tier A then Tier B adapters, corpus and migration diagnostics | GB: advertised APIs pass unmodified-extension tests; unsupported cases fail clearly |
| 8 Hardening and packaging | Provider/MCP/codemode parity closure, security, performance, cross-platform packages and guides | G6: every mandatory native row passes; clean-machine installs and release evidence pass |

### Planning effort

Budget an initial 30 to 50 engineer-weeks for native v1, assuming two experienced engineers, part-time QA/review, access to representative provider accounts and no requirement for every experimental monorepo API. A rough elapsed range is five to eight months with that staffing, including integration and stabilization. These are human-equivalent planning ranges, not estimates of automated execution time or a delivery promise.

A usable headless preview should be a deliberately smaller early release, with its own supported provider/tool list. Budget an additional 6 to 12 engineer-weeks for a bounded TypeScript bridge after its spike; full custom-TUI and arbitrary deep-import compatibility is unbounded until investigated. Re-estimate at G0 and the session/RPC gates using measured gaps rather than carrying this initial range forward unchanged.

### Workstreams and ownership

- Runtime lead: contracts, providers, agent loop and sessions. Owns parity trace review and the public SDK boundary.
- Extension and experience lead: extension SDK/host, CLI/RPC/TUI and sample migrations. Owns reentrancy and capability behavior.
- Shared review and QA: source inventory, cross-platform fixtures, security, release packaging and compatibility documentation. No feature author accepts their own parity exception alone.
## 14 Risks and decision log


| Risk | Impact | Mitigation and decision trigger |
| --- | --- | --- |
| Moving upstream target | Permanent scope growth | Pin release SHA; upgrade only through an explicit inventory/fixture review |
| Monorepo scope ambiguity | Unexpected framework work | Limit v1 to stable coding-agent requirements; separate full-monorepo expansion |
| Hidden ordering differences | Intermittent corrupt context or tools | Reference traces, explicit reducers and barriers before UI work |
| TypeScript and custom TUI gap | Overstated compatibility | Advertise tiers; bridge spike; native migration path |
| Plugin reentrancy and reload | Deadlocks or stale writes | Generation guards, no lock across callbacks, cleanup and stress tests |
| Provider/auth churn | Late failures and credential risk | Capability matrix, adapter isolation, fixtures and bounded live smoke tests |
| Cross-platform shell behavior | Different or unsafe commands | Explicit shells, platform-specific tests and documented differences |
| Session round-trip loss | Damaged user history | Separate home, copy/import, unknown-field retention and reversible exports |
| Plugin trust assumptions | Arbitrary code execution | Explicit trust; real isolation for untrusted mode; no sandbox claims for ALC |
| NativeAOT chosen too early | Dynamic extensions fail | JIT default; AOT only as a separately qualified distribution |
| Packaging or license gaps | Unshippable build | Early notice/dependency inventory and clean-machine release tests |

### Decisions ready to adopt


| ID | Decision | Status or revisit condition |
| --- | --- | --- |
| D1 | Use PiSharp with C# and net10.0 | Project identity and target runtime; JIT packaging is recommended |
| D2 | Use v0.99.1 release SHA as reference | Recommended baseline; change only through G0 scope review |
| D3 | Fresh standalone PiSharp project | Public implementation baseline |
| D4 | Prioritize behavioral and session/RPC parity | Recommended; no automatic TypeScript source-compatibility promise |
| D5 | Native C# plugins and optional Node bridge | Design decision; Node-free core is a requirement |
| D6 | Use a versioned worker protocol and bridge tiers | Recommended implementation detail; spike and corpus determine optional bridge scope |
| D7 | Keep CLI JSONL and a separate PiSharp home | Recommended safety and compatibility default |
| D8 | Defer full experimental monorepo parity | Revisit only if a concrete required workflow depends on it |

## 15 First implementation sprint

The first sprint should produce evidence, not a broad skeleton with empty interfaces. Its stopping point is an executable vertical slice and a measured account of the remaining compatibility risks.

- Freeze the reference release, inventory public surfaces and licenses, and create the parity manifest with mandatory versus optional rows.
- Capture deterministic streams and agent traces for a single-turn answer, two parallel tools, a sequential batch, cancellation, a blocked tool and a continuation/settled sequence.
- Implement the smallest Contracts, AI and Agent slice needed to replay those fixtures and run one safe read/edit task in a disposable workspace.
- Prototype a native extension with a tool, command and blocking hook; prove nested tool invocation and a session switch cannot deadlock or commit stale state.
- Prototype terminal input/rendering and one Node bridge callback. Use the results to choose dependencies and confirm whether the optional bridge is worth prioritizing.
- Publish the G0/G1 evidence, revise effort estimates and record deviations before expanding provider and UI breadth.
### Definition of completion

PiSharp native v1 is complete when a user can install it on every advertised OS, use the supported coding-agent workflows, resume and exchange compatible sessions, automate it through the declared RPC contract, build native extensions from the published SDK, and see a truthful compatibility report backed by the acceptance suite. The optional Node bridge is complete only for its explicitly tested tiers. A full Pi monorepo port remains a separate claim.

## Sources

Pi references below are pinned to release v0.99.1 or its immutable commit unless stated otherwise. Microsoft documentation describes runtime constraints. Architecture, packaging choices, budgets and milestone proposals in this plan are recommendations rather than upstream guarantees.

- **P1** Pi v0.99.1 release. [Release page](https://github.com/earendil-works/pi/releases/tag/v0.99.1)

- **P2** Immutable reference commit and repository tree. [Commit d86654a](https://github.com/earendil-works/pi/commit/d86654abb8862e201933517d6f1fce9f88dd117f)

- **P3** Published coding-agent dependencies and exports. [Coding agent package manifest](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/package.json)

- **P4** Agent loop, event barriers and tool execution semantics. [Agent README](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/agent/README.md)

- **P5** SDK session authority and completion boundaries. [Coding agent SDK](https://raw.githubusercontent.com/earendil-works/pi/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/sdk.md)

- **P6** Session JSONL version 3 entry and context model. [Session format](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/docs/session-format.md)

- **P7** Separate durable runtime and storage abstractions. [Durable README](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/durable/README.md)

- **P8** Event dispatch reducers and hook composition. [Extension runner source](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/src/core/extensions/runner.ts)

- **P9** Extension contexts, hook types and UI contracts. [Extension types source](https://raw.githubusercontent.com/earendil-works/pi/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/extensions/types.ts)

- **P10** Extension capabilities and authoring model. [Extension documentation](https://github.com/earendil-works/pi/blob/v0.99.1/packages/coding-agent/docs/extensions.md)

- **P11** Newline JSON RPC and completion behavior. [RPC documentation](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/docs/rpc.md)

- **P12** Extension module loading and package resolution. [Extension loader source](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/src/core/extensions/loader.ts)

- **P13** Terminal rendering, screen modes and components. [TUI README](https://raw.githubusercontent.com/earendil-works/pi/d86654abb8862e201933517d6f1fce9f88dd117f/packages/tui/README.md)

- **P14** Built-in tool inventory and factories. [Tool registry source](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/src/core/tools/index.ts)

- **P15** Multi-edit matching and file update semantics. [Edit tool source](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/src/core/tools/edit.ts)

- **P16** Output truncation and byte limits. [Truncation source](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/coding-agent/src/core/tools/truncate.ts)

- **P17** Provider abstractions and stream behavior. [AI README](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/ai/README.md)

- **P18** Provider wire APIs, content and event types. [AI types source](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/packages/ai/src/types.ts)

- **P19** Upstream MIT license. [Pi license](https://raw.githubusercontent.com/earendil-works/pi/v0.99.1/LICENSE)

- **N1** .NET 10 LTS lifecycle. [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)

- **N2** IChatClient and .NET AI interoperability. [Microsoft Extensions AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)

- **N3** Bounded asynchronous producer and consumer queues. [System Threading Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels)

- **N4** Plugin contracts, dependency loading and untrusted-code warning. [Create a .NET app with plugins](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)

- **N5** Assembly loading scopes and type identity. [AssemblyLoadContext concepts](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext)

- **N6** Cooperative plugin unload constraints. [Assembly unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)

- **N7** Dynamic loading and runtime code generation limitations. [Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
