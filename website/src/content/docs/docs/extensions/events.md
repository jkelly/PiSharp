---
title: Extension events
description: Every event an extension can observe or handle, when it fires and what it can change.
---

PiSharp raises Pi v1.2.0's 41 extension events. Native C# extensions and TypeScript extensions share one registry, so both get every event, in extension load order and then registration order. This page lists each event, when it fires, what it carries and what a handler can change. The C# types are on [C# extension API](/docs/extensions/api/). Pi's [Extensions](https://github.com/earendil-works/pi/blob/v1.2.0/packages/coding-agent/docs/extensions.md) page describes the same events for TypeScript.

## How a C# extension subscribes

A TypeScript extension calls `pi.on(name, handler)` for every event. A C# extension uses one of five kinds of registration, depending on the event:

| Kind | API | Handler gets | Handler returns |
| --- | --- | --- | --- |
| Observer | `registry.Observe(new(id, topic, callback))` | The Pi event object as `JsonData` | Nothing |
| Event handler | `((IExtensionEventHandlerRegistry)registry).RegisterEventHandler(new(id, topic, callback))` | The Pi event object as `JsonData` | Pi's result object as `JsonData`, or null |
| Typed handler | `registry.Register…Handler(new(id, callback))` | A C# record | A C# patch record, or null |
| Lifecycle handler | `RegisterSessionSwitchHandler`, `RegisterSessionCreationHandler`, `RegisterSessionBeforeTreeHandler` | A C# record | A decision |
| User bash handler | `((IExtensionUserBashRegistry)registry).RegisterUserBashHandler(new(id, callback))` | `ExtensionUserBashEvent` | `ExtensionUserBashPatch`, or null |

Every Pi event object has a `type` field with the event name. The host awaits every handler in order, also observers. A handler that throws is reported as an extension error, and the next handler still runs, unless the event's row says otherwise.

## Which events reach which extensions

All 41 events reach C# extensions and TypeScript extensions alike. No event is TypeScript-only. A few results are:

- `session_before_switch` and `session_before_fork`: a C# veto returns only `Continue` or `Cancel`. Pi's other result fields, such as `skipConversationRestore`, need a TypeScript handler.
- `session_before_tree`: a C# handler returns an `ExtensionBeforeTreeResult` with Pi's fields, typed.
- `tool_call`: TypeScript changes `event.input` in place; a C# handler returns replacement arguments.

TypeScript extensions also get their `ctx` object; C# handlers get a [callback context](/docs/extensions/api/#callback-contexts) to cast instead.

## Session events

| Event | Fires | Payload | C# registration | Handler can |
| --- | --- | --- | --- | --- |
| `project_trust` | Before PiSharp loads an untrusted project's extensions and settings. Only user-level and `-e` extensions are loaded yet. | `cwd` | Event handler | Return `{ "trusted": "yes" \| "no" \| "undecided", "remember"?: bool }`. The first answer that isn't `undecided` wins. |
| `resources_discover` | At startup and on `/reload`, before skills, prompt templates and themes load. | `cwd`, `reason` (`startup` or `reload`) | Event handler | Return `{ "skillPaths"?, "promptPaths"?, "themePaths"? }`. Every handler's paths are added. |
| `session_start` | When a session is ready: at startup, after `/reload`, and after a new session, resume or fork. | `reason` (`startup`, `reload`, `new`, `resume` or `fork`), `previousSessionFile` when replacing a session with a file | Observer | Nothing |
| `session_info_changed` | When the session's name changes. | `name` (absent when cleared) | Observer | Nothing |
| `session_before_switch` | Before `/resume` opens another session (`reason` `resume`) or `/new` starts one (`reason` `new`). | `reason`, `targetSessionFile` for `resume` | Switch handler (resume), creation handler (new), and observer | Cancel it |
| `session_before_fork` | Before `/fork` creates a session from an entry. | `entryId`, `position` (`before` or `at`) | Creation handler and observer | Cancel it |
| `session_before_compact` | Before a compaction, manual or automatic. | `preparation`, `branchEntries`, `customInstructions`, `reason`, `willRetry` | Event handler | Return `{ "cancel": true }` to stop it, or `{ "compaction": { "summary", "firstKeptEntryId", "tokensBefore", "usage"?, "details"? } }` to supply the summary. The last result wins, and a cancel stops the dispatch. |
| `session_compact` | After a compaction entry is written. | `compactionEntry`, `fromExtension`, `reason` (`manual`, `threshold` or `overflow`), `willRetry` | Observer | Nothing |
| `session_compact_failed` | When a compaction fails or is aborted. | `reason`, `errorMessage`, `aborted`, `willRetry`, `fromExtension` | Observer | Nothing |
| `session_shutdown` | Before the session ends: on quit, `/reload`, and before a resume, new session or fork replaces it. | `reason` (`quit`, `reload`, `resume`, `new` or `fork`), `targetSessionFile` when known | Observer | Nothing |
| `session_before_tree` | Before `/tree` moves the leaf. | Pi's tree preparation; C# gets `ExtensionBeforeTreeEvent` | Tree handler | Cancel it, supply the branch summary, or override its instructions and label |
| `session_tree` | After `/tree` selects another entry. | `newLeafId`, `oldLeafId` | Observer | Nothing |
| `mcp_servers_change` | When extensions register or unregister MCP servers after the session started. | `servers`: `[{ name, config, extensionPath }]` | Observer | Nothing |

## Agent and turn events

| Event | Fires | Payload | C# registration | Handler can |
| --- | --- | --- | --- | --- |
| `input` | When input arrives, before it is sent or queued: typed in the terminal UI, an RPC `prompt`, `steer` or `follow_up`, or a message from an extension. | `text`, `images`, `source` (`interactive`, `rpc` or `extension`), `streamingBehavior` | Typed (`RegisterInputHandler`) | Transform the text and images, or mark it handled so nothing is sent |
| `before_agent_start` | Before a run starts, from a prompt or from a custom message with `triggerTurn`. | `prompt`, `images`, `systemPrompt`, `messageType` (`user` or `custom`), `message` for a custom run | Typed (`RegisterBeforeAgentStartHandler`) | Add a custom message before the prompt, and replace the system prompt for this run |
| `agent_start` | When the run starts. | none | Observer | Nothing |
| `turn_start` | Before each turn's provider request. | `turnIndex`, `timestamp` | Observer | Nothing |
| `context` | Before each provider request, with the conversation that will be sent. System messages are left out. | `messages` | Typed (`RegisterContextHandler`) | Replace the messages of this request only. History is not changed. |
| `context_with_system` | After `context`, with the full list including system messages. | `messages` | Typed (`RegisterContextWithSystemHandler`) | Replace the messages of this request only |
| `message_start` | When a message starts: user, assistant or tool result. | `message` | Observer | Nothing |
| `message_update` | For each streamed piece of an assistant message. | `message`, `assistantMessageEvent` | Observer | Nothing |
| `message_end` | When a message is complete, before it is saved. | `message` | Observer and event handler | Return `{ "message": … }` to replace it. The role must stay the same; another role is reported and skipped. |
| `turn_end` | After a turn's assistant message and tool results are saved. | `turnIndex`, `message`, `toolResults`, `messageEntryId`, `toolResultEntryIds`, `outcome` (`completed`, `aborted` or `error`), `entries`, `continue`, `context` | Observer and event handler | Return `{ "entries"?, "continue"? }`: session entries to append now, and whether to make one more provider request |
| `agent_before_settle` | When the run is about to end. | `outcome`, `entries`, `continue`, `context` | Event handler | Return `{ "entries"?, "continue"? }`, as for `turn_end` |
| `agent_end` | When the run ends. | `messages`: the run's new messages | Observer | Nothing |
| `agent_settled` | When the run has settled and the session is idle again. | `aborted` | Observer | Nothing |

`context` in the `turn_end` and `agent_before_settle` payloads previews the next request: `contextEntries`, `contextMessages`, `llmMessages`, `pendingMessages` and `canContinue`. Each handler sees the entries and `continue` flag the previous handlers left. Entries the session can't append are reported, and then nothing is committed.

## Tool events

| Event | Fires | Payload | C# registration | Handler can |
| --- | --- | --- | --- | --- |
| `tool_call` | Before a tool runs, after its arguments are validated. | `toolName`, `toolCallId`, `input` | Typed (`RegisterToolCallHandler`) | Replace the arguments, or block the call with `{ "block": true, "reason"?, "terminate"? }` |
| `tool_execution_start` | When a tool starts. | `toolCallId`, `toolName`, `args`, `parentToolCallId` | Observer | Nothing |
| `tool_execution_update` | For each progress update a tool reports. | as `tool_execution_start`, plus `partialResult` | Observer | Nothing |
| `tool_execution_end` | When a tool finishes. | `toolCallId`, `toolName`, `result`, `isError`, `durationMs`, `parentToolCallId` | Observer | Nothing |
| `tool_result` | After a tool finishes, before its result is recorded. | `toolName`, `toolCallId`, `input`, `content`, `details`, `isError` | Typed (`RegisterToolResultHandler`) | Replace `content`, `details`, `structuredContent`, `isError` or `usage` |
| `user_bash` | When the user runs a `!` or `!!` command. | `command`, `excludeFromContext`, `cwd` | User bash handler | Run it through your own shell operations, or supply its result |

`parentToolCallId` is set for calls a tool makes through `ExecuteToolAsync` or code mode.

## Provider and model events

| Event | Fires | Payload | C# registration | Handler can |
| --- | --- | --- | --- | --- |
| `before_provider_request` | Before each provider HTTP request with a JSON body. | `payload`: the request body | Event handler | Return a replacement body. Each handler gets the previous handler's body. |
| `before_provider_headers` | Before each provider HTTP request. | `headers` | Event handler | Return the edited headers object. A header set to `null`, or left out, is removed. |
| `after_provider_response` | When the response headers arrive. | `status`, `headers` (names in lower case) | Observer | Nothing |
| `provider_stream_event` | For each server-sent event frame, or Bedrock event-stream message, of a streaming response. | `data`, `provider`, `api`, `model` | Observer | Nothing |
| `cache_warming_decision` | Before each prompt-cache warming refresh, with Pi's decision filled in. | `warmCost`, `missCost`, `continuationProbability`, `action` | Event handler | Return `{ "action": "warm" \| "stop" }`. The last answer wins. |
| `model_select` | When the model changes. | `model`, `previousModel`, `source` | Observer | Nothing |
| `thinking_level_select` | When the thinking level changes. | `level`, `previousLevel` | Observer | Nothing |
| `ui_prompt_start` | When an extension dialog opens, once for the outermost dialog. | `reason` (`ui_prompt`), `kind`, `title` | Observer | Nothing |
| `ui_prompt_end` | When that dialog closes. | as `ui_prompt_start` | Observer | Nothing |

PiSharp queues `ui_prompt_start` and `ui_prompt_end` and doesn't wait for them, as Pi does, so the dialog doesn't wait for your observer.

## Observers

An observer gets the Pi event object and returns nothing. This one logs slow tools:

```csharp
registry.Observe(new("slow-tools", "tool_execution_end", (observation, context, token) =>
{
    var end = observation.Value;
    if (end.TryGetProperty("durationMs", out var duration) && duration.GetDouble() > 10_000)
        Console.Error.WriteLine($"{end.GetProperty("toolName").GetString()} took {duration.GetDouble() / 1000:F1}s");
    return ValueTask.CompletedTask;
}));
```

`turn_end` and `message_end` take both an observer and an event handler. Use the handler only when you return a result.

## Event handlers

`RegisterEventHandler` is on `IExtensionEventHandlerRegistry`. It takes the topics whose handlers return a result: `project_trust`, `resources_discover`, `session_before_compact`, `cache_warming_decision`, `before_provider_request`, `before_provider_headers`, `message_end`, `turn_end` and `agent_before_settle`. The callback gets the Pi event as `JsonData` and returns Pi's result object, or null for none.

This handler adds a header to every provider request:

```csharp
if (registry is IExtensionEventHandlerRegistry handlers)
    handlers.RegisterEventHandler(new("trace-header", "before_provider_headers", (request, context, token) =>
    {
        var headers = JsonNode.Parse(request.Value.GetProperty("headers").GetRawText())!.AsObject();
        headers["x-trace-id"] = Guid.NewGuid().ToString("N");
        return ValueTask.FromResult<JsonData?>(JsonData.Parse(headers.ToJsonString()));
    }));
```

This one keeps automatic compaction from running when the context is still small enough for the model:

```csharp
if (registry is IExtensionEventHandlerRegistry handlers)
    handlers.RegisterEventHandler(new("manual-compaction", "session_before_compact", (compaction, context, token) =>
        ValueTask.FromResult<JsonData?>(compaction.Value.GetProperty("reason").GetString() == "threshold"
            ? JsonData.Parse("""{"cancel":true}""")
            : null)));
```

## Typed handlers

Typed handlers get a C# record and return a patch, or null to change nothing. Each handler sees the event as the previous handlers left it.

| Event | Record | Patch | How patches combine |
| --- | --- | --- | --- |
| `input` | `ExtensionInputEvent(Text, Source, Images, StreamingBehavior)` | `ExtensionInputPatch(Action, Text, Images)`, `Action` `Continue`, `Transform` or `Handled` | Transforms build on each other. Null `Images` keeps the current images. `Handled` stops the dispatch, and the input isn't sent. |
| `before_agent_start` | `ExtensionBeforeAgentStartEvent(Prompt, SystemPrompt, Images)` with `MessageType` (`User` or `Custom`) and `Message` | `ExtensionBeforeAgentStartPatch(Message, SystemPrompt)` | Every message is kept, in order. Each `SystemPrompt` replaces the one the next handler sees; an empty string is a real replacement. |
| `context` | `ExtensionContextEvent(Messages)` | `ExtensionContextMessagesPatch(Messages)` | Each replacement is the next handler's input. The host puts the leading system message back. An empty array removes every message. |
| `context_with_system` | `ExtensionContextWithSystemEvent(Messages)` | `ExtensionContextMessagesPatch(Messages)` | As `context`, but system messages are included. Removing the leading system message is reported but kept. |
| `tool_call` | `ExtensionToolCallEvent(ToolName, ToolCallId, Arguments, ParentToolCallId)` | `ExtensionToolCallPatch(Arguments, Decision)` | Argument replacements build on each other. The first decision with `"block": true` stops the dispatch. `terminate` also ends the run, and only counts with `block`. |
| `tool_result` | `ExtensionToolResultEvent(ToolName, ToolCallId, Arguments, Result, ParentToolCallId, OutcomeIsError)` | `ExtensionToolResultPatch(Fields)` | Each patch replaces the fields it names among `content`, `details`, `structuredContent`, `isError` and `usage`. New `content` without `structuredContent` removes the old `structuredContent`. |

A handler that throws or returns an invalid patch is reported and skipped, except for `tool_call`: there a failure blocks the call.

The records are in `PiSharp.Extensions.Events`. Two types are named `ExtensionCustomMessage`: the one `before_agent_start` uses is in `PiSharp.Extensions.Events`. Write `new(...)` where the parameter type is known, as below, or qualify the name.

```csharp
registry.RegisterBeforeAgentStartHandler(new("house-style", (start, context, token) =>
{
    if (start.MessageType != ExtensionBeforeAgentStartMessageType.User)
        return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(null);
    return ValueTask.FromResult<ExtensionBeforeAgentStartPatch?>(new(
        Message: new("house-style", Display: false, Content: JsonData.Parse("\"Use British spelling.\"")),
        SystemPrompt: start.SystemPrompt + "\n\nKeep answers short."));
}));

registry.RegisterInputHandler(new("review-shortcut", (input, context, token) =>
    ValueTask.FromResult<ExtensionInputPatch?>(input.Text == ":review"
        ? new(ExtensionInputAction.Transform, "Review the uncommitted changes and list problems.")
        : null)));

registry.RegisterToolResultHandler(new("redact", (result, context, token) =>
{
    if (result.ToolName != "bash" || !result.Result.ToString().Contains("SECRET=", StringComparison.Ordinal))
        return ValueTask.FromResult<ExtensionToolResultPatch?>(null);
    return ValueTask.FromResult<ExtensionToolResultPatch?>(new(JsonData.Parse(
        """{"content":[{"type":"text","text":"[output hidden: it contained a secret]"}]}""")));
}));
```

## Session lifecycle handlers

These registries are optional interfaces on the registry. Cast to them, as the [SessionCheckpoint](https://github.com/jkelly/PiSharp/tree/main/samples/extensions/SessionCheckpoint) sample does.

| Registry | Descriptor | Callback | Returns |
| --- | --- | --- | --- |
| `IExtensionSessionLifecycleRegistry` | `ExtensionSessionSwitchHandlerDescriptor(id, HandleAsync)` | `(ExtensionSessionSwitchEvent(PreviousSessionId, TargetSessionId, TargetPath, SelectedLeafId), context, token)` | `ExtensionSessionSwitchDecision.Continue` or `Cancel` |
| `IExtensionSessionCreationRegistry` | `ExtensionSessionCreationHandlerDescriptor(id, HandleAsync)` | `(ExtensionSessionCreationEvent(PreviousSessionId, Kind, EntryId, ParentSession), context, token)`, `Kind` `New`, `ForkBefore`, `ForkAt` or `Clone` | `Continue` or `Cancel` |
| `IExtensionSessionTreeLifecycleRegistry` | `ExtensionSessionBeforeTreeHandlerDescriptor(id, HandleAsync)` | `(ExtensionBeforeTreeEvent(SessionId, OldLeafId, TargetId, NewLeafId, Options), context, token)`, with `CommonAncestorId` and `EntriesToSummarize` | `ExtensionBeforeTreeResult(Cancel, Summary, CustomInstructions, ReplaceInstructions, Label)`, or null |
| `IExtensionUserBashRegistry` | `ExtensionUserBashHandlerDescriptor(id, HandleAsync)` | `(ExtensionUserBashEvent(Command, ExcludeFromContext, WorkingDirectory), context, token)` | `ExtensionUserBashPatch { Operations }` or `{ Result }`, or null |

The first handler that cancels stops the switch, creation or tree move, and later handlers don't run. An exception from a C# veto handler stops the operation with that error. For `session_before_switch` and `session_before_fork`, the typed handlers run first and the observers after them, only when nobody cancelled.

For the tree, `Summary` is an `ExtensionTreeSummary(Text, Usage, Details)` that replaces the generated branch summary. `CustomInstructions`, `ReplaceInstructions` and `Label` are `ExtensionTreeOverride<T>(Value)` wrappers, so a wrapped null clears the value and a null wrapper leaves it.

For `user_bash`, set exactly one property of the patch. `Operations` is an `IExtensionShellOperations` whose `ExecuteAsync(command, workingDirectory, onData, token)` runs the command, streams output through `onData` and returns the exit code, or null when killed. `Result` is an `ExtensionUserBashResult(Output, ExitCode, Cancelled, Truncated, FullOutputPath)` for a command you already ran. The first valid patch wins. A handler failure is reported, and the command does not fall back to the local shell.

```csharp
if (registry is IExtensionSessionLifecycleRegistry lifecycle)
    lifecycle.RegisterSessionSwitchHandler(new("confirm-switch", async (proposal, context, token) =>
    {
        if (context is not IExtensionUiContext { Ui: var ui } || !ui.Capabilities.Supports(ExtensionUiFeature.Confirm))
            return ExtensionSessionSwitchDecision.Continue;
        var answer = await ui.ConfirmAsync("Switch session", "Open " + proposal.TargetPath + "?", cancellationToken: token);
        return ExtensionUiSourceDefaults.Confirmation(answer)
            ? ExtensionSessionSwitchDecision.Continue : ExtensionSessionSwitchDecision.Cancel;
    }));

if (registry is IExtensionUserBashRegistry bash)
    bash.RegisterUserBashHandler(new("no-deploy", (command, context, token) =>
        ValueTask.FromResult<ExtensionUserBashPatch?>(command.Command.StartsWith("deploy", StringComparison.Ordinal)
            ? new() { Result = new("deploy is disabled in this workspace\n", 1) }
            : null)));
```

## Hosting the events yourself

In your own app, the host raises these events, not the extensions. `ExtensionAgentBinding` raises `context`, `context_with_system`, `tool_call` and `tool_result` around an agent, and `ExtensionRegistry` dispatches the rest. See [Host extensions](/docs/embed/extension-host/#dispatch-events).
