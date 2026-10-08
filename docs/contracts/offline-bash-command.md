# Explicit offline CLI Bash authorization

The native `session create`, `prompt`, `resume` and `rpc` commands accept an opt-in Bash profile. The existing literal offline Responses or Anthropic provider script still drives the accepted HTTP/SSE mapper, Agent, tool invoker and durable coordinator. Bash effects use the real `NativeProcessRunner`; the provider remains a local injected fake handler. No request reaches a network endpoint.

Use the following options together:

- `--bash-executable <existing absolute executable>`
- `--bash-spill-root <existing absolute directory within the workspace>`
- One or more `--allow-bash-command <exact command string>`
- Optional `--bash-timeout <finite positive seconds>`

Each process must receive its own explicit runtime authorization. Session storage retains the Bash declaration, not the executable, command allowlist, environment or credentials. Reopen resolves source-ordered active declarations through the supplied mandatory-policy registry. Omitting a required Bash binding fails before provider acquisition; selected branches which lack its declaration do not acquire Bash through an implicit prompt. Read-only inspect/tree reject execution options.

## Admission and final action

Any partial Bash configuration is rejected. The profile supports the existing Windows process backend and rejects other platforms rather than choosing a shell. It does not discover executables through PATH or install them. Paths pass the existing bounded absolute-path grammar and native canonicalization. The configured executable cannot alias the reserved session or script. The spill root must be an existing canonical workspace directory. Read/write file tools retain their existing canonical exact-target policies and reserved-file protections.

Commands are an ordinal exact allowlist: 1–16 distinct entries, at most 12,000 UTF-16 characters per entry and 32,768 collectively. They require valid scalar Unicode and cannot contain NUL. Serialized script data cannot install callbacks, transforms, policy code or environment entries. This closed-command approval grants the effects expressed by those commands. It is not substring filtering, filesystem confinement or an arbitrary-shell sandbox; a trusted allowed command can access files and executables beyond the read/write tool allowlists.

The profile creates a fresh `pisharp-bash-<GUID>` directory beneath the spill root. It grants the complete child environment: `SystemRoot` from the Windows known folder, `TEMP` and `TMP` equal to that fresh directory, and `LANG`/`LC_ALL` equal to `C.UTF-8`. There is no inherited PATH, HOME, credential or provider-key overlay. Existing executable startup behavior remains part of the trusted shell selection.

The registered `BashTool` accepts the closed model input `{"command":string,"timeout"?:number}`. Its immutable prepared action exposes executable, `["-c",command]`, cwd, full environment, optional original timeout token and absolute generated `outputPath`. The accepted invoker validates, awaits any trusted action transforms, validates the final action again, then invokes the mandatory policy. The CLI policy requires exactly:

- Registered name/operation `bash` and action kind `Command`.
- Configured canonical executable and workspace cwd, rechecked against current canonical resolution.
- Complete environment equal to the fixed five-entry grant.
- Exact approved command and identical argv command, with no extra arguments.
- No timeout when the option is absent; otherwise the model must supply that exact configured binary64 value. The profile does not silently insert or overwrite timeout.
- A fresh `pi-bash-<32 lowercase hex>.log` output file beneath the same granted spill directory, visible in action Arguments and not existing at authorization.

Timeout must be positive and finite, with at most Int32.MaxValue milliseconds. There is no default timeout. Strict retained argument JSON, duplicate/unknown keys, scalar Unicode and action budgets still apply before effects. The runner receives the same authorized immutable values. Raw spill is lazily opened with CreateNew; successful artifacts remain caller-owned and can be inspected. A setup, cancellation or storage failure can leave a directory or uncertain partial artifact. There is no automatic deletion or retry.

Canonical and lexical identity checks do not exclude a hostile replacement between checks and process/storage acquisition. This profile requires a trusted executable/workspace and grants the whole command semantics; it does not claim hostile-code isolation.

## Output and bounded composition

Progress traverses the real process callback, invoker validation, scheduler and awaited RPC writer. Final model content and source-shaped details are durably appended before a following provider request. Programmatic `StructuredContent` travels in RPC tool updates/ends when present, separately from canonical model/durable messages. No structured field is silently truncated to fit framing.

The opt-in budget chain is:

| Layer | Bash opt-in limit |
| --- | --- |
| Script/input RPC frame and complete HTTP request | 1 MiB, unchanged |
| Model command/raw arguments/final action | 12,000 / 96,000 / 192,000 characters |
| Process captured raw output | 64 MiB |
| Model tail / structured selected source bytes | 50 KiB and 2,000 lines / 1 MiB head and tail |
| Invoker ordinary result / separate structured JSON | 512 KiB characters / 8 MiB characters |
| Provider projector per history entry | 1 MiB characters; complete input/output caps remain 1 MiB |
| Durable record codec | 1 MiB characters / 4 MiB UTF-8 bytes, unchanged |
| Session input inspection | 8 MiB, unchanged |
| RPC event construction, writer and output observation | 8 MiB per output frame |

The 8-MiB frame reserves six JSON-escaped bytes per selected source byte, then headroom for bounded model text, truncation details, paths, omission marker and event envelope. The worst ordinary 50-KiB decoded model tail is charged again in escaped details: approximately 300 KiB raw details plus at most roughly 55 KiB decoded content/notices, beneath the 512-KiB invoker admission. Its serialized canonical content/details remain below the 1-MiB durable record and request-entry caps. Structured head/tail does not enter durable history. These are logical payload budgets, not a total heap or shell resource cap.

Ordinary non-Bash hosts retain their prior 1-MiB RPC output and 65,536-character per-entry projector budgets. Bash input admission remains 1 MiB. A cumulative transcript or get_entries/agent_end response can still exceed its explicit whole-payload limits; that fails honestly rather than pruning authoritative history or reporting a truncated acknowledgment.

A source-backed integration defect was identified while composing this profile: decoded NUL was already allowed in ordinary output and StructuredContent, but the original invoker rejected NUL in the Bash truncation `details.content` string. Root composed the separately owned output-details correction before its first integrated gate. The large-output regression deliberately preserves NUL in the structured head, model tail and raw spill; replacing or stripping those bytes would hide the defect.

Pinned `truncateTail` splits text on LF and joins admitted complete lines when truncation is needed. This drops the final structural LF from the selected truncated model/details tail, while preserving NUL and every other character in those lines. The raw spill and structured head/tail retain the exact original final LF. The regression derives the exact retained complete-line count from the 50-KiB byte budget, then checks exact details content, metadata and formatted model notice independently of the native truncator. It never uses TrimEnd or normalizes the observed result.

## Settlement and evidence

Prompt success remains a durable settlement acknowledgment for one-shot commands; RPC prompt acceptance is separate from `agent_settled`. Cancel waits for the real process job, pipes, progress callbacks, output storage and durable coordinator cleanup, retaining known output and effects. Cancel/timeout do not roll back an allowed command.

Output write or flush failure remains poisoned: it cannot release a held provider callback into success, acquire another scripted response or grant further tool execution. A failure after a completed Bash effect may prevent the subsequent canonical result commit. The host emits only fixed sanitized stderr diagnostics, closes and joins owned lifetimes, and requires explicit state/artifact inspection before retry. It does not archive output or log commands, environment or raw exception payloads in failure diagnostics.

`OfflineBashCommandTests.Cases(dotnetHost,cliDll)` authors six groups: pre-storage config rejection; both provider families through actual compiled child effects/action metadata/next request/reopen/selected branch; exact command and timeout denial; both-family >1-MiB actual raw control/Unicode/NUL output through progress/public final event/spill/acknowledged entries/EOF/reopen; progress-gated real Bash abort; and gated final write/flush failure after an actual completed effect, with no next effect and an independent exclusive session lease. The literal scripts are authored source-informed provider data, not genuine provider response captures. Tests use `C:\Program Files\Git\usr\bin\bash.exe` explicitly; no fallback exists. Expected ordering comes from admitted public events and explicit asynchronous gates, without sleeps.

The cancellation fixture prints only fixed API/phase markers, with a six-second per-phase guard and twelve-second child lifetime. Early failure runs owned cleanup before the harness's outer30-second guard: close stdin to request actual EOF settlement, then bounded process-tree termination if necessary, join process exit and drain the owned output readers. These deadlines protect cleanup and diagnose the stage; they are not timing-based ordering assertions.

Root's first actual integrated gate compiled with zero warnings/errors and recorded52/54 CodingAgent groups passing. The large-output case passed exact structured head/tail, raw spill and >1-MiB public framing before an incorrect fixture requirement for the final structural LF failed. The cancellation case reached the outer30-second guard without phase evidence. The original failed log remains `artifacts/offline-bash-integrated-native.log`; the corrected expectation and bounded diagnostic cleanup require a fresh root run. The authoring lane has executed no processes/builds. Root owns registration, actual validation and independent review; the corrected candidate has no passing or whole-phase closure claim.

The public implementation reference is pinned Pi v0.99.1 SHA `d86654abb8862e201933517d6f1fce9f88dd117f`: [Bash tool](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/bash.ts), [shell configuration and environment](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/utils/shell.ts), and [structured/output accumulator](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/tools/output-accumulator.ts). Whole-helper/progress differential qualification, source shell discovery/environment hooks and command prefixes, Unix/WSL/PowerShell/platform matrices, full live auth/provider CLI and extension/codemode behavior remain mandatory unfinished work; this explicit offline opt-in advances the native durable command path without waiving those contracts.
