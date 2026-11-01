---
title: RPC protocol
description: Pi's JSONL RPC protocol in PiSharp, and the commands PiSharp adds.
---

`pisharp --mode rpc` speaks Pi's RPC protocol on stdin and stdout, so a client written for `pi --mode rpc` works unchanged. This page lists the framing, every command and event, and the `pisharp_*` commands PiSharp adds. Pi's own documents describe each payload in full: [rpc.md](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/rpc.md), [rpc-commands.md](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/rpc-commands.md) and [rpc-extension-ui.md](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/rpc-extension-ui.md). To serve the protocol from your own process, see [RPC host](/docs/embed/rpc/).

## Entry points

| Entry point | Framing | Limits | Extensions |
| --- | --- | --- | --- |
| `pisharp --mode rpc` | Pi's: each line is read with `JSON.parse` rules and written as `JSON.stringify` writes it | Pi's: no count limits, frames up to the JavaScript string length | Pi's TypeScript extensions and C# extensions, with extension UI |
| `pisharp session rpc` | Strict: one JSON object per line, no duplicate names | 32 MiB frames, 65,536-character prompts, 16 images | A C# extension from the session options, with extension UI |

Both serve every command below, including the `pisharp_*` ones. `pisharp session rpc` needs `--session`, `--workspace`, and `--offline-script` or `--live`; see [CLI](/docs/reference/cli/#session). `--mode rpc` takes the usual session and model options of [CLI](/docs/reference/cli/), and refuses `@path` arguments.

## Framing

- Each record is one JSON value on one line, ending in LF. A CR before the LF is dropped, and a last line without LF still counts.
- A command is an object with a `type` and an optional `id`. The response repeats the `id`.
- The server reads the next line while earlier commands run, so responses can arrive in a different order. Match them by `id`.
- A response is `{"id", "type": "response", "command", "success": true, "data"}`, where `id` and `data` may be absent, or `{"id", "type": "response", "command", "success": false, "error"}`.
- A line that isn't valid JSON gets `{"type": "response", "command": "parse", "success": false, "error": "Failed to parse command: …"}` with no `id`. An unknown type gets the error `Unknown command: <type>`.
- Events are objects with a `type` and no `id`. They arrive between responses.
- Extension dialogs are `extension_ui_request` records from the server, answered with `extension_ui_response` records. See [Extension UI](#extension-ui).

When stdin closes, the server aborts a running prompt, closes open dialogs and exits.

## Commands

These are Pi v1.2.0's commands. Fields marked `?` are optional.

| Command | Fields | Does |
| --- | --- | --- |
| `prompt` | `message`, `images?`, `streamingBehavior?` (`steer` or `followUp`) | Sends a user message and starts a run. During a run, `streamingBehavior` queues it instead. |
| `steer` | `message`, `images?` | Queues a message for the running agent, delivered after the current tool calls |
| `follow_up` | `message`, `images?` | Queues a message delivered when the agent would stop |
| `abort` | | Stops the current run |
| `clear_queue` | | Empties both queues and returns what they held |
| `new_session` | `parentSession?` | Starts a new session |
| `get_state` | | Model, thinking level, streaming and compaction state, queue modes, session file and id, message counts |
| `set_model` | `provider`, `modelId` | Switches the model |
| `cycle_model` | | Switches to the next model, the `--models` scope first |
| `get_available_models` | | Lists the models a client can switch to |
| `set_thinking_level` | `level` | Sets the thinking level, clamped to what the model supports |
| `cycle_thinking_level` | | Switches to the next thinking level |
| `get_available_thinking_levels` | | Lists the model's thinking levels |
| `set_steering_mode`, `set_follow_up_mode` | `mode` (`all` or `one-at-a-time`) | How many queued messages each delivery takes |
| `compact` | `customInstructions?` | Compacts the session now |
| `set_auto_compaction` | `enabled` | Turns automatic compaction on or off |
| `set_auto_retry` | `enabled` | Turns automatic retry of failed requests on or off |
| `abort_retry` | | Cancels a pending retry |
| `bash` | `command`, `excludeFromContext?` | Runs a shell command for the user, as `!` does in the terminal UI |
| `abort_bash` | | Stops that command |
| `get_session_stats` | | Token, cost and message totals |
| `export_html` | `outputPath?` | Writes the session as HTML and returns its path |
| `switch_session` | `sessionPath` | Opens another session file |
| `fork` | `entryId` | Starts a new session from before an earlier user message and returns its text |
| `clone` | | Copies the current branch into a new session |
| `get_fork_messages` | | The user messages `fork` accepts |
| `get_entries` | `since?` | The session's entries, optionally after an entry id |
| `get_tree` | | The session tree and the current leaf |
| `get_last_assistant_text` | | The text of the last assistant message |
| `set_session_name` | `name` | Sets the session's display name |
| `get_messages` | | The messages of the current context |
| `get_commands` | | The slash commands a client can offer: extension commands, prompt templates and skills |

### What PiSharp adds to them

- `fork` takes `position`: `before`, the default and Pi's behavior, which needs a user message and leaves it out, or `at`, which forks at any entry and keeps it.
- `switch_session` takes `leafId` to open the session at that entry, or `root: true` to open it with an empty branch, instead of at its latest leaf.
- The responses of `new_session`, `fork`, `clone` and `switch_session` add `sessionId`, `sessionFile` and `generation` to Pi's `cancelled`.
- `get_state` adds `pisharpGeneration`, the session generation (below), and `pisharpRunOwnerSettled`, which is `true` when no run holds the session. It can still be `false` just after `agent_settled`. It adds `pisharpOperationActive`, `pisharpOperationPhase` and `pisharpOperationGeneration` when automatic recovery is on, `pisharpEditingContext` during a context edit, and `pisharpPersistence` with `pisharpDurableCheckpointAcknowledged` when the session isn't a flushed local file.

## PiSharp commands

The `pisharp_*` commands reach session features that Pi has only in its terminal UI or SDK. Most take a `generation`: the session generation the client last saw. It is `pisharpGeneration` in `get_state`, and every `session_switched` event and session-replacing response carries the new one. A command with an older generation fails, so a client never acts on a session it no longer shows.

| Command | Fields | Response `data` |
| --- | --- | --- |
| `pisharp_complete_extension_command` | `command`, `prefix` | `completions`: the extension's argument completions, or null |
| `pisharp_list_sessions` | `pageSize?` (1 to 128, default 32), `cursor?`, `cwd?` | `generation`, `sessions`, `nextCursor`, `skippedFiles`, `unavailableStores`. Each session has `key`, `storeId`, `fileName`, `sessionFile`, `sessionId`, `createdTimestamp`, `cwd`, `cwdGroup`, `parentSession`, `fileBytes`, `modifiedUtc` and `isCurrent`. |
| `pisharp_resume_session` | `catalogKey` (a listed `key`), `leafId?` or `root?` | `cancelled`, `sessionId`, `sessionFile`, `generation` |
| `pisharp_context_edit` | `generation`, `targetId`, `replacement` (null removes the content) | `checkpointAcknowledged`, `sessionId`, `generation`, `entryId`, `leafId`, `targetId` |
| `pisharp_compact` | `generation`, `customInstructions?`, `reserveTokens?` (16,384), `keepRecentTokens?` (20,000), `contextWindow?` (128,000), `automatic?` (`false`), `firstKeptEntryId?` | `checkpointAcknowledged`, `skipped`, `generation`, `sessionId`, and `entryId` and `leafId` unless skipped |
| `pisharp_branch_summary` | `generation`, `targetId` (an entry id, or null for the root), `customInstructions?`, `replaceInstructions?` (`false`), `reserveTokens?`, `contextWindow?` | As `pisharp_compact` |
| `pisharp_set_auto_compaction` | `generation`, `enabled`, `reserveTokens?`, `keepRecentTokens?`, `contextWindow?`, `customInstructions?` | none |
| `pisharp_restore_queue` | `generation`, `currentText` | `generation`, `count`, `text`. Moves the queued text messages back into the editor: `text` is them and `currentText`, separated by blank lines. A `queue_update` event follows. |
| `pisharp_interrupt` | `generation`, `currentText` | As `pisharp_restore_queue`, and it also stops the current run, as interrupting a run does in the terminal UI |
| `pisharp_capture_navigation` | `id`, `generation`, `mode` (`tree` or `fork`) | `viewId`, `mode`, `generation`, `leafId`, `initialIndex`, `choices` (`entryId`, `label`). Opens a chooser over the tree, or over the user messages for `fork`. Needs an idle session. |
| `pisharp_select_navigation` | `id`, `generation`, `mode`, `viewId`, `targetId` | `viewId`, `mode`, `disposition` (`Selected`, `NoOp`, `Vetoed` or `Aborted`), `generation`, `editorText`, `leafId`, `sessionId`. `tree` moves the leaf; `fork` starts a new session. |
| `pisharp_retire_navigation` | `id`, `generation`, `mode`, `captureId` (the `id` of the capture command) | `retired: true`. Closes the chooser without a choice. |

A chooser allows one selection. The session commands need the session host `pisharp` provides; an embedded host without it answers them with an error.

## Events

| Event | Fields | Means |
| --- | --- | --- |
| `agent_start` | | A run started |
| `turn_start` | | One model request and its tool calls began |
| `message_start` | `message` | A message began |
| `message_update` | `message`, `assistantMessageEvent` | The assistant message streamed: `text_*`, `thinking_*` and `toolcall_*` start, delta and end, then `done` or `error` |
| `message_end` | `message` | A message is complete |
| `tool_execution_start` | `toolCallId`, `toolName`, `args` | A tool began |
| `tool_execution_update` | `toolCallId`, `toolName`, `args`, `partialResult` | A tool reported progress |
| `tool_execution_end` | `toolCallId`, `toolName`, `result`, `isError`, `durationMs?` | A tool finished |
| `turn_end` | `message`, `toolResults` | The turn's assistant message and tool results |
| `agent_end` | `messages`, `willRetry` | The run ended, with its new messages |
| `agent_settled` | `aborted` | The session is idle. Send the next prompt after this. |
| `queue_update` | `steering`, `followUp` | The queued messages changed |
| `compaction_start` | `reason` (`manual`, `threshold` or `overflow`) | Compaction began |
| `compaction_end` | `reason`, `result`, `aborted`, `willRetry`, `errorMessage?` | Compaction finished |
| `auto_retry_start` | `attempt`, `maxAttempts`, `delayMs`, `errorMessage` | A failed request will be retried |
| `auto_retry_end` | `success`, `attempt`, `finalError?` | Retrying ended |
| `summarization_retry_scheduled` | `attempt`, `maxAttempts`, `delayMs`, `errorMessage` | A failed summary will be retried |
| `summarization_retry_attempt_start` | `source` (`compaction` or `branchSummary`), `reason?` | A summary retry began |
| `summarization_retry_finished` | | Summary retries ended |
| `entry_appended` | `entry` | An entry was written to the session file |
| `session_info_changed` | `name` | The session's name changed |
| `thinking_level_changed` | `level` | The thinking level changed |
| `bash_execution_update` | `id?`, `delta` | Output of a `bash` command |
| `extension_error` | `extensionPath`, `event`, `error` | An extension handler failed |

PiSharp adds:

| Event | Fields | Means |
| --- | --- | --- |
| `session_switched` | `sessionFile`, `sessionId`, `generation` | The served session was replaced, by `new_session`, `fork`, `clone`, `switch_session`, a `pisharp_*` command or an extension. Requests now go to the new session. |
| `pisharp_chat_diagnostic` | `schemaVersion`, `data` | After `turn_end`, when the provider call left a diagnostic in the session |
| `pisharp_recovery_started` | `operationGeneration`, `reason`, `willRetry` | Automatic recovery of an interrupted operation began |
| `pisharp_recovery_ended` | `operationGeneration`, `status`, `willRetry`, `checkpointId?` | Recovery ended |
| `pisharp_operation_settled` | `operationGeneration`, `status` | A recovered operation settled |

The `pisharp_recovery_*` and `pisharp_operation_settled` events appear only when automatic recovery or retry is configured.

## Extension UI

When an extension asks for input, the server writes an `extension_ui_request` with an `id` and a `method`. Dialogs wait for an `extension_ui_response` with the same `id`; the other methods need no answer.

| `method` | Fields | Answer |
| --- | --- | --- |
| `select` | `title`, `options`, `timeout?` | `{"value": "<option>"}` |
| `confirm` | `title`, `message`, `timeout?` | `{"confirmed": true}` or `false` |
| `input` | `title`, `placeholder?`, `timeout?` | `{"value": "<text>"}` |
| `editor` | `title`, `prefill?` | `{"value": "<text>"}` |
| `notify` | `message`, `notifyType?` (`info`, `warning` or `error`) | none |
| `setStatus` | `statusKey`, `statusText?` (absent clears it) | none |
| `setWidget` | `widgetKey`, `widgetLines?` (absent removes it), `widgetPlacement?` (`aboveEditor` or `belowEditor`) | none |
| `setTitle` | `title` | none |
| `set_editor_text` | `text` | none |

An answer is `{"type": "extension_ui_response", "id": "<request id>", …}` with `value`, `confirmed`, or `"cancelled": true` to dismiss the dialog. `timeout` is in milliseconds: when it passes, the dialog ends as if dismissed and a later answer is ignored. An answer with an unknown `id` is ignored too. Custom components, themes, headers and footers don't exist over RPC; [rpc-extension-ui.md](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/rpc-extension-ui.md) lists what each UI call does there.
