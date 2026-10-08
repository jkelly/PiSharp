# Coding-agent command, RPC and SDK declaration inventory

This bounded P1-03 inventory expands the existing family seeds into 712 individually source-linked rows in [coding-agent-command-rpc-inventory.json](../../compatibility/coding-agent-command-rpc-inventory.json). The baseline is public Pi v0.99.1, commit `d86654abb8862e201933517d6f1fce9f88dd117f`, tree `200bd10bb146773516f862a02b8aaebeed163e00`. Inventory ID: `coding-agent-command-rpc-inventory-v1`; baseline ID: `pi-v0.99.1@d86654abb8862e201933517d6f1fce9f88dd117f`.

The inventory enumerates named entries in five selected primary modules: the CLI parser/help, built-in slash registry, RPC declarations, RPC handler and coding-agent root SDK index. Eight supporting files establish package entry declarations, CLI mode selection, event inheritance/JSON projection and JSONL wiring. The complete command-handler, SDK-member, configuration, resource, provider and platform inventory remains open. No existing surface/parity/profile/status file is changed by this slice.

Every row has its exact source path, canonical SHA-256, Git blob ID, symbol and location; baseline ID; mode/platform condition; seed requirement; owner/phase; planned test ID and obligation; and an explicit distinction between static declaration/control-flow evidence and behavioral acceptance. All 712 implementation acceptance values remain **Deferred**, with implementation evidence **Unverified**. This inventory closes no phase gate and contains no captured expected behavior or native parity assertion.

## Enumerated scope

| Surface | Count | Meaning of the count |
| --- | ---: | --- |
| Core CLI parser branches | 41 | 40 primary options plus the `--` delimiter; 58 spellings when aliases are included |
| Other parser grammar | 4 | `@file`, dynamic long extension flags, unknown short options and positional messages |
| Help-declared CLI subcommands | 8 | `install`, `remove`, `uninstall`, `update`, `list`, `config`, `auth`, `mcp` |
| Help examples / additional flags | 4 | Two auth subcommand examples, subcommand `-l`, and dynamic extension example `--plan` |
| Built-in slash registry entries | 24 | All names in `BUILTIN_SLASH_COMMANDS`; handler/mode outcomes remain unqualified |
| RPC stdin commands / stdout success responses | 33 / 33 | Every `RpcCommand` variant has a matching ordered dispatch case and success-response declaration |
| RPC generic error response | 1 | `response` with arbitrary command string, `success:false`, error and optional correlation ID |
| Extension UI request / response variants | 9 / 3 | All method declarations and all value/confirmed/cancelled reply declarations |
| RPC extension UI adapter members | 28 | Includes emitted requests and the actual source's no-op/unsupported/default-return methods |
| Forwarded session event variants | 24 | 23 discriminator strings; summarization retry start has two distinct source variants |
| Nested assistant event declarations | 12 | Union availability; runtime emission inside `message_update` remains explicitly unverified |
| Handler extension-error record | 1 | Direct `extension_error` output outside the response union |
| Protocol/mode wiring obligations | 8 | Framing, parsing, concurrency, backpressure, EOF/signals and CLI mode selection |
| Selected module export declarations | 18 | Eight absent from package root, ten also represented at root; these are separate observations, not extra root exports |
| Public SDK root named exports | 457 | 156 value exports, 301 explicitly type-only exports; no wildcard clauses |
| Package entry declarations | 4 | Root plus `./rpc-entry`, `./client` and `./experimental/plugin`; subpath export inventories remain open |

Counts enumerate declarations/control-flow anchors. A union member, help line or export name does not establish a working product capability. Required test IDs are planned obligations, with no attached fixture or completed execution.

## CLI names and parser boundaries

The parser spells its options as follows. Aliases share their primary row and retain their exact order.

| Primary option | Aliases | Primary option | Aliases |
| --- | --- | --- | --- |
| `--help` | `-h` | `--version` | `-v` |
| `--mode` | | `--continue` | `-c` |
| `--resume` | `-r` | `--provider` | |
| `--model` | | `--api-key` | |
| `--system-prompt` | | `--append-system-prompt` | |
| `--name` | `-n` | `--no-session` | |
| `--session` | | `--session-id` | |
| `--fork` | | `--session-dir` | |
| `--models` | | `--no-tools` | `-nt` |
| `--no-builtin-tools` | `-nbt` | `--tools` | `-t` |
| `--exclude-tools` | `-xt` | `--thinking` | |
| `--print` | `-p` | `--export` | |
| `--extension` | `-e` | `--no-extensions` | `-ne` |
| `--skill` | | `--prompt-template` | |
| `--theme` | | `--use-theme` | |
| `--no-skills` | `-ns` | `--no-prompt-templates` | `-np` |
| `--no-themes` | | `--no-context-files` | `-nc` |
| `--list-models` | | `--tui-mode` | |
| `--verbose` | | `--approve` | `-a` |
| `--no-approve` | `-na` | `--offline` | |

The separate `--` delimiter preserves `@file` classification for subsequent arguments. Missing values are not handled uniformly: branches guarded only by a following argument can fall through to unknown-long-flag handling, whereas `--mode`, `--name`, `--use-theme` and `--tui-mode` have explicit diagnostics. `--models` retains trimmed empty comma items; `--tools` and `--exclude-tools` remove them. `--print` can consume a following `---`-prefixed message, but leaves ordinary options and `@files` separate. These are statically inspected branch facts awaiting genuine differential tests, not rewritten expectations.

The parser accepts `text/json/rpc` mode values. Supporting `main.ts` selects RPC first, then JSON, then print when `--print` or non-TTY input/output applies, otherwise interactive. It delegates dedicated auth/package/config/MCP commands before ordinary parsing. Accordingly, `-l` belongs to help-declared subcommand scope; `--plan` is an extension-registration example. Neither is added to the built-in parser count.

The complete slash registry is `/settings`, `/model`, `/tree`, `/thinking`, `/scoped-models`, `/export`, `/import`, `/share`, `/bug`, `/copy`, `/name`, `/session`, `/changelog`, `/hotkeys`, `/fork`, `/clone`, `/trust`, `/login`, `/logout`, `/new`, `/compact`, `/resume`, `/reload`, `/quit`. Registry descriptions and argument hints are retained verbatim as declarations. The interactive dispatch and eligibility of each name in print/JSON/RPC still require source inspection and tests. External actions such as gist creation, bug reporting and credential work must use fake adapters in offline qualification.

## RPC command and event obligations

| Group | Command names |
| --- | --- |
| Prompt/queue/session start | `prompt`, `steer`, `follow_up`, `abort`, `clear_queue`, `new_session` |
| State/model | `get_state`, `set_model`, `cycle_model`, `get_available_models` |
| Thinking/queue modes | `set_thinking_level`, `cycle_thinking_level`, `get_available_thinking_levels`, `set_steering_mode`, `set_follow_up_mode` |
| Compaction/retry | `compact`, `set_auto_compaction`, `set_auto_retry`, `abort_retry` |
| Shell | `bash`, `abort_bash` |
| Session/export | `get_session_stats`, `export_html`, `switch_session`, `fork`, `clone`, `get_fork_messages`, `get_entries`, `get_tree`, `get_last_assistant_text`, `set_session_name` |
| Messages/discovery | `get_messages`, `get_commands` |

The actual handler starts `prompt` asynchronously and emits its success response through preflight completion. Input-line callbacks launch command handling without awaiting prior lines. Tests must measure overlapping requests and authoritative response/event ordering using awaited gates; a serial request simulator would miss this contract. `get_commands` collects extension, prompt and skill commands in that order, with `skill:` prefixes; it does not append the built-in slash registry. `get_entries` slices strictly after a known `since` entry and returns an error for an unknown ID. Session replacement/fork/clone handlers rebind after a non-cancelled result.

The session stream inherits core agent/turn/message/tool events, replaces `agent_end` with the session form containing `willRetry`, and adds settlement, queue, entry/info/thinking, compaction/retry and shell-update records. Tool events can include `parentToolCallId`. The JSON adapter changes `message_update` to cumulative usage plus a nested assistant event, removes cumulative `message`/`partial` snapshots, and adds tool-call ID/name at `toolcall_start`. The 12 nested union members are declarations; the inventory explicitly leaves their actual emission paths unresolved. Agent-loop fixtures cannot by themselves qualify coding-agent session augmentation or RPC output.

The nine UI methods are `select`, `confirm`, `input`, `editor`, `notify`, `setStatus`, `setWidget`, `setTitle`, `set_editor_text`. RPC supplies genuine mode-specific limitations: terminal input, custom components, loader customization and several theme/editor operations have no-op/default/unsupported implementations. Those members have their own rows and tests. Such upstream RPC limits do not authorize exclusions from PiSharp's native C# SDK or its interactive mode.

The JSONL helper writes exactly one LF per JSON record and reads LF-delimited UTF-8, removing trailing CR and emitting a nonempty final unterminated record at EOF. Unicode separators inside payloads are not delimiters. Handler JSON parsing and TypeScript casts do not establish complete runtime DTO validation. Backpressure, malformed input, optional versus missing fields, stale UI replies, EOF during active operations, disposal and descendant cleanup remain required test work. The explicit signal guard adds SIGHUP only outside Windows; no OS behavior is qualified by that declaration.

## SDK classification and native mapping

Each of the 457 root names has a row with its re-export origin and exact explicit type syntax. Public value exports include functions, classes and constants; an exported class can also be used as a TypeScript type. The 301 `export type`/`type` names do not create JavaScript runtime values. This source-level classification does not verify the released `dist/index.js` or declarations, and it does not convert TypeScript source compatibility into a native release requirement without a reviewed difference/mapping record.

The root includes extension contracts, tool definitions/factories, runtime/session composition, compaction/projection, settings/resources/trust, model/auth routing, MCP/codemode/tool-search, modes/RPC, terminal components/themes, clipboard/images and shell helpers. The largest origin is `core/extensions/index.ts`: 15 value names and 141 type names. Tool-index exports account for 17 values and 38 types; session-manager exports for nine values and 20 types; UI components for 31 values and six types. All remaining origins and names are present individually in the JSON.

`Mode`, `isValidThinkingLevel`, `normalizeSessionName`, `printHelp`, `BuiltinSlashCommand`, `BUILTIN_SLASH_COMMANDS`, `RpcSlashCommand` and `RpcCommandType` are exported by the selected source modules but absent from the root list. They are marked module exports outside the declared root, with no claim that the package supports arbitrary deep imports. Conversely, `Args`, `parseArgs`, slash-command metadata types and the root RPC types are recognized through the root index. Package metadata's separate `./client`, `./experimental/plugin` and `./rpc-entry` entries are retained as named entries pending their own source/build/distribution inventory; none is silently folded into stable root exports or excluded.

PiSharp remains a native .NET 10 C# product with a first-class C# extension SDK and Node-free core/runtime, as required by the [architecture](../architecture.md) and [profile/platform draft](profile-platform-matrix.md). Root SDK rows retain the existing composition/P4 seed owner and identify implementation collaborators/phases for provider, tool, extension or mode definitions. Those hints require parent reconciliation rather than silently reassigning existing families. Native C# signature/lifetime/type mapping, real consumer workflows, released-artifact verification, and separate optional TypeScript bridge support remain pending.

## Source verification and remaining work

All 13 consulted checkout files were byte-identical to `git show <pinned SHA>:<path>` canonical blobs. Their SHA-256, Git object ID, byte counts and checkout comparison appear in `sourceFiles`. The five primary canonical SHA-256 values are:

| Source | Canonical SHA-256 |
| --- | --- |
| `packages/coding-agent/src/cli/args.ts` | `f5d9a107ee96b10c70a506532b2243064fbae9ad36a805d789081d1d88d62966` |
| `packages/coding-agent/src/core/slash-commands.ts` | `f0fee97e4b10d337593736ff8d9370fe5ea2c713d357397dba88c69677f07ce6` |
| `packages/coding-agent/src/modes/rpc/rpc-types.ts` | `be06a1d53916e03e9274cce9e4833692b235494e4047f1f7139d7f0147daae1a` |
| `packages/coding-agent/src/modes/rpc/rpc-mode.ts` | `d534e0fa1484844a097963d5f4fb4df9397c5a1b5e29e42aed7575ab6f67989b` |
| `packages/coding-agent/src/index.ts` | `775b147324d9bdce8d3cc8a0eacf1d434251fc12b748f4db1ebd5bfb189b65ac` |

Supporting files are `packages/coding-agent/package.json`, `src/main.ts`, `src/modes/index.ts`, `src/modes/json-event.ts`, `src/modes/rpc/jsonl.ts`, `src/core/agent-session.ts`, `packages/agent/src/types.ts` and `packages/ai/src/types.ts` (coding-agent `src` prefixes apply to the abbreviated middle entries). Full-file pins do not mean every implementation line was behaviorally reviewed.

Static verification checked unique row IDs, source hashes and line/offset anchors, owners/target phases/planned obligations, all Deferred/no-execution flags, the 33-command union/dispatch agreement and the named export/flag counts. The pinned source checkout remained clean before and after. The review used the existing task-local Node runtime and Git read operations for metadata extraction only; it loaded no upstream modules, dependencies or SDK, ran no builds, and performed no network/credential/provider/public-write operations. No clock, ID or output normalization was involved.

The JSON artifact's SHA-256 is `b45c7e117fdb46dde823de9c81717349fdc09fb0b265b706f6433afc13484a14`. Rows use one JSON object per physical line to keep this detailed static artifact compact; it is ordinary JSON, not JSONL. This digest identifies the review draft, not a behavioral golden.

The remaining work is concrete and owner assigned: experience/P5 must enumerate dedicated subcommand parsers and real slash/mode handlers, flags, exit codes and configuration/resources; RPC/P5 must qualify real client/entry artifacts, malformed DTOs, concurrency, event reachability and lifecycle/UI behavior; composition/P4 must expand every exported member/signature and released root/subpath artifact; extensions/P6 must inventory actual dynamic registrations and required native SDK/MCP/codemode contracts; compatibility/P1 must reconcile this slice into the parent-owned inventory/checker/provenance/profile artifacts and expand all other required families. Pending modules/platforms remain pending requirements, with no new exclusion or Supported claim. Windows source inspection does not provide Linux/macOS runtime evidence. Independent acceptance and all implementation tests remain outstanding.

Source location offsets use an exclusive `endOffset`; displayed `endLine` is inclusive of the last character in that region. Lead canonical-byte validation corrected newline-boundary end-line labels before committing the inventory. This metadata correction changes no source observation or behavioral acceptance.
