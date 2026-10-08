# Explicit native session CLI commands

These one-shot commands join the registry-backed durable coordinator, selected-ancestry projection, actual local file tools and the accepted injected HTTP/SSE Responses path. They are a native P4/P5 integration prerequisite. Command names, reports and scripted provider behavior are authored native contracts, not a genuine upstream CLI/RPC comparison or complete Phase 4/5 implementation.

## Invocation

Invoke the compiled CLI with explicit paths:

```text
session create --session <new absolute JSONL> --workspace <existing absolute directory>
session prompt --session <existing JSONL> --workspace <existing absolute directory> --offline-script <absolute JSON> --message <text>
session resume --session <existing JSONL> --workspace <existing absolute directory> --offline-script <absolute JSON> --message <text>
session inspect --session <existing JSONL>
session tree --session <existing JSONL>
```

Prompt, resume, inspect and tree accept either `--leaf <entry ID>` or `--root`. Omission selects the physical latest entry; explicit root selects empty ancestry. Prompt and resume both supply exactly one explicit new user message; neither synthesizes a continuation prompt. Appending from an older leaf creates an in-file sibling branch. Existing siblings and source bytes remain intact.

Prompt and resume accept repeatable `--allow-read <absolute file>` and `--allow-write <absolute file>`. Permission applies to each listed canonical final file target, not the entire workspace. The workspace must already exist. Create uses create-new semantics and initializes durable model, off-thinking and system/tool-declaration records without acquiring a provider turn. Its explicit model is `pisharp-offline-session` / `openai-responses` / `openai`.

All paths must be fully qualified, control-free and valid UTF-16, with at most 4096 characters. Windows UNC/device-prefix paths are rejected. Local absolute path acceptance does not qualify all filesystem types. A reopened header's working directory must match the canonical explicit workspace. There is no default home/session directory, configuration discovery, import, migration or automatic repair.

## Authored offline provider script

The script is inert JSON data. Each turn contains literal parsed Responses DTOs and optional request-history text checks:

```json
{
  "schemaVersion": 1,
  "turns": [
    {
      "requiredInputTexts": ["my explicit user message"],
      "events": [
        {
          "type": "response.output_item.added",
          "output_index": 0,
          "item": {"type": "message", "id": "msg_offline", "content": []}
        },
        {
          "type": "response.output_text.delta",
          "output_index": 0,
          "item_id": "msg_offline",
          "delta": "offline answer"
        },
        {
          "type": "response.output_item.done",
          "output_index": 0,
          "item": {
            "type": "message",
            "id": "msg_offline",
            "content": [{"type": "output_text", "text": "offline answer"}]
          }
        },
        {
          "type": "response.completed",
          "response": {
            "status": "completed",
            "output": [],
            "usage": {"input_tokens": 1, "output_tokens": 1, "total_tokens": 2}
          }
        }
      ]
    }
  ]
}
```

A private injected HttpMessageHandler serves these turns at `https://offline-session.invalid/v1/responses`. The actual native request factory, HTTP response/body lifecycle, SSE decoder, parsed wire mapper, ChatClient, Agent loop and durable coordinator run. No DNS, socket, server or real provider is used. The request carries an authored inert local authorization value solely to exercise the accepted explicit-key factory; no credential, environment variable or key store is read. That value is never supplied by the user and is not written into session records.

Each `requiredInputTexts` value must appear as an ordinal substring of some string leaf in the actual projected request input. This is an authored assertion over observed history, not a schema, exact transcript comparison or genuine oracle. Missing checks, excess provider turns, malformed DTOs and incomplete wire terminal state cause failure through the native provider path. Extra unused script turns also prevent successful command classification. Unknown script properties are inert data and cannot execute code.

The script must be strict UTF-8 JSON, at most 1 MiB, with at most 32 container levels, 64 turns, 256 events per turn and 4096 total events. Events must be objects. Each turn has at most 128 history checks of 4096 UTF-16 characters each. Invalid Unicode and duplicate decoded JSON property names fail admission. Script syntax and structural bounds are checked before acquiring a session writer; protocol-specific DTO validation occurs in the accepted mapper during the run.

## File effects and ownership

The registry restores active source-ordered tool declarations and selected model from disk using explicit borrowed bindings and mandatory prepared-action policy. Existing tool calls are history, not instructions to rerun effects. Only registered UTF-8 text read/write adapters are supplied by these commands.

Admission canonicalizes each allowed target using existing-segment symlink/junction resolution, requires it below the canonical workspace and rejects the session and current script as targets. Final-action authorization requires the exact allowlisted canonical path, matching read/write operation and tool name, the configured working directory, and no command arguments or environment fields. Authorization and final resolved targets appear in the report.

A write explicitly authorizes creation/overwrite of that file and creation of its missing parent directories. It is the accepted bounded write implementation, not atomic replacement. Read and write content are capped at 64 KiB; read output also retains the accepted truncation rules. Prepared argument/action/result limits are 64/128/128 KiB in UTF-16 characters. At most 128 paths per permission kind are admitted. Shell, process, edit and other effects are not registered here.

These are trusted local filesystem controls. Canonical paths are not hostile filesystem identities: hard-link aliases, concurrent replacement and time-of-check/time-of-use races are not eliminated. Windows case folding conservatively groups path aliases and can reject distinctions in case-sensitive directories. Stronger identity protection and platform/filesystem qualification remain work; no general sandbox claim is made.

The command owns its scripted client/handler, coordinator and writer. Each provider request and response/body follow the accepted transport ownership contract. Caller-supplied output writers are borrowed. A modifying command captures the acknowledged durable snapshot, awaits coordinator/writer disposal, and verifies the final file length against `CommittedByteLength` before emitting a success report. Input, finalized assistant and tool-result appends use the coordinator's awaited durable barriers before provider/tool continuation. The underlying durability profile remains local file flush, not a universal power-loss or network-filesystem guarantee.

## Reports, inspection and failure

Exactly one UTF-8-compatible JSON record terminated by LF is emitted on stdout for a settled result. Machine errors use one fixed JSONL diagnostic on stderr. There are no cursor controls, incidental logs, request authorization headers or raw exception strings. Reports intentionally contain explicitly requested paths and acknowledged message/history data; consumers must treat them as session data.

Modifying reports expose `schemaVersion:1`, `type:"session_command_result"`, command, status, selected/previous/physical leaf IDs, committed byte length, `durableCheckpointAcknowledged:true`, restored model/thinking state, final assistant, observed request hashes/check counts, final-action decisions and used/script turn counts. Provider/script reports are labeled `authored-offline-wire-script-native-command` with `upstreamDifferential:false` and `networkUsed:false`. IDs and storage timestamps are fresh native values; fixture wire DTOs are authored deterministic inputs.

Inspect and tree only read the bounded session reader and pure projector. They preserve bytes and never acquire a writer, provider or tool. Inspect includes selected raw entries, runtime messages and canonical model messages. Tree reports physical source order, roots and each node's `Id`, `ParentId`, `Type` and ordered `children`. Both include source hash, byte count and sanitized reader diagnostics.

Only a complete accepted header/log receives a selected projection. Incomplete, malformed or reader-truncated input reports `status:"recovery_required"` and `resumeEligibility:"recovery_required"`, with no successful model context. A valid log reports `resumeEligibility:"requires_explicit_runtime_binding_and_authorization"`: valid bytes alone do not guarantee that a future command can bind every model/tool or runtime capability. Reader bounds are 8 MiB and 10,000 lines/records; they are admission limits, not a total lifetime file or heap cap. Output is capped at 1 MiB in characters and UTF-8 bytes.

Exit 0 denotes completed, acknowledged command work. Exit 1 denotes run/tool failure, incomplete inspection, cancellation or an acquisition/settlement failure. A settled run with tool errors reports `completed_with_errors` and retains known committed history/effects. Exit 2 denotes argument, path, script or explicit bound rejection. Fixed errors conservatively indicate that modifying effects may have completed; they never imply rollback. Inspect actual durable state before retrying uncertain work.

Cancellation before admission prevents acquisition. During an admitted run, cooperative work cancellation and the accepted aborted assistant/durable settlement behavior apply; owned cleanup is awaited before the command returns. A canceled or faulted operation never gains a successful command report merely from a usable prefix. Broken output sinks can prevent delivery of a report even after acknowledged disk effects; terminal/report delivery is not transactional with persistence.

## Evidence and unfinished scope

`SessionCommandTests.Cases(dotnetHost, cliDll)` supplies six authored groups. Five run the real compiled CLI in independently launched child processes; the cancellation group invokes the same public command dispatcher with an already canceled token. Tests cover durable create → read/write tool turns → independent resume, actual restored request history, allowed and denied final targets, existing source/script byte preservation, append-only selected-leaf sibling branching, read-only inspection/root/tree, damaged-log rejection, duplicate/conflicting arguments, script limits and canceled admission.

Child invocations use explicit executable/DLL paths, argument lists without shell interpolation, a 15-second deadline, bounded concurrent stdout/stderr collection, and owned process cleanup. No sleeps or real provider calls are used. Writing this contract does not assert a passed build or independent review.

Upstream source research uses pinned `d86654abb8862e201933517d6f1fce9f88dd117f`: [CLI arguments](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/cli/args.ts), [session manager and tree projection](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/src/core/session-manager.ts), and [session format](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/coding-agent/docs/session-format.md). Native explicit command grammar, reports, limits, scripted provider, durable acknowledgement and per-file policy are authored extensions; no unchanged-upstream command execution is claimed.

Interactive UI, full print/JSON/RPC protocol and client compatibility, live/provider credential setup, broader runtime configuration, session import/export/migration, repair-copy UX, automatic recovery and platform/filesystem qualification remain unfinished. The historical `--offline-demo` path is separate from these reusable session commands.
