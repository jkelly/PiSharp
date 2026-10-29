---
title: RPC host
description: Serve Pi's JSONL RPC protocol from your own process with PiSharp.Rpc.
---

`pisharp --mode rpc` is a shell around one type: `RpcSessionDispatcher` in `PiSharp.Rpc`. It reads Pi's JSONL commands from a stream, runs them against a `PersistentAgentSession`, and writes responses and events to another stream. Your own process can serve the same protocol over stdin and stdout, a pipe or a socket, with your own transport, tools and extensions. The commands and events themselves are on [RPC protocol](/docs/reference/rpc/).

## Packages

```powershell
dotnet add package PiSharp.Rpc --version 1.2.0.1
dotnet add package PiSharp.CodingAgent --version 1.2.0.1
```

`PiSharp.Rpc` holds the dispatcher, the JSONL reader and writer, the extension UI coordinator and the event projectors. `PiSharp.CodingAgent` holds `PersistentAgentSession`; see [Coding agent](/docs/embed/coding-agent/) and [Sessions](/docs/embed/sessions/).

## Serve a session over stdin and stdout

The dispatcher needs an idle session, a JSONL writer, a clock in Unix milliseconds, and the full description of every model a client may see. This sample creates a session file and serves it until stdin closes:

```csharp
using System.Globalization;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.AI;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Serialization;

public static async Task<int> ServeAsync(IChatTransport transport, string sessionPath, string workingDirectory)
{
    var model = new ModelDescriptor("claude-sonnet-4-5", "anthropic-messages", "anthropic");
    // The model as get_state and get_available_models return it: Pi's Model object.
    var modelWire = JsonData.Parse("""
        {"id":"claude-sonnet-4-5","api":"anthropic-messages","provider":"anthropic","name":"Claude Sonnet 4.5",
         "baseUrl":"https://api.anthropic.com","reasoning":true,"input":["text","image"],
         "contextWindow":200000,"maxTokens":64000,
         "cost":{"input":3,"output":15,"cacheRead":0.3,"cacheWrite":3.75}}
        """);

    var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new
    {
        type = "session", version = 3, id = Guid.NewGuid().ToString(),
        timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        cwd = workingDirectory,
    }));
    Func<long> clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var session = await PersistentAgentSession.CreateAsync(sessionPath, header,
        new AgentConfiguration(model, transport, []), clock, () => Guid.NewGuid().ToString("N")[..8]);

    RpcSessionDispatcher dispatcher;
    try
    {
        dispatcher = new RpcSessionDispatcher(session, new JsonlWriter(Console.OpenStandardOutput()), clock,
            [new RpcModelDefinition(model, modelWire)]);
    }
    catch
    {
        await session.DisposeAsync();
        throw;
    }

    // From here on the dispatcher owns the session and the writer.
    await dispatcher.RunAsync(new JsonlReader(Console.OpenStandardInput()));
    var settlement = await dispatcher.SettleAsync();
    return settlement.OperationFailure is null && settlement.CompletionFailure is null ? 0 : 1;
}
```

Send it a line such as `{"id":"1","type":"prompt","message":"Hello"}` and it answers with a `response` record, then streams `agent_start`, `message_update` and the other events until `agent_settled`.

stdout carries only protocol records, so write your logs to stderr. Build the transport as on [Chat](/docs/embed/chat/), and add tools to the `AgentConfiguration` as on [Agent](/docs/embed/agent/).

### Lifetime

- `RunAsync(reader, token)` is the input loop. It reads the next line while earlier commands still run, so `abort` and `steer` reach a running prompt. A dispatcher has one input loop.
- When the input ends, the token is cancelled, or a fatal error happens, the dispatcher shuts down. It aborts a running prompt, resolves open extension dialogs, disposes the writer, and disposes the session when it owns it.
- `SettleAsync()` waits for that shutdown and returns a `ShutdownSettlement(OperationFailure, CleanupFailures, CompletionFailure)`. `OperationFailure` is an `RpcDispatchException` whose `Failure` is `ResourceLimit`, `InvalidModelDefinition`, `SessionRunFailed`, `OutputFailed`, `CleanupFailed` or `Disposed`.
- `SubmitAsync(command, token)` sends one command without a reader, for tests or in-process clients. `Completion` completes after shutdown. `WaitForIdleAsync` waits until the session is idle and the current run has settled.

## Constructor

`RpcSessionDispatcher(session, output, clock, models, …)` checks its inputs at once. The session must be idle and not running, compacting or editing. Its current model must be one of `models`; otherwise the constructor throws an `RpcDispatchException` with `InvalidModelDefinition`.

| Parameter | Default | Meaning |
| --- | --- | --- |
| `session` | required | The `PersistentAgentSession` to serve |
| `output` | required | The `JsonlWriter` for responses and events. The dispatcher always disposes it. |
| `clock` | required | Unix milliseconds for message timestamps |
| `models` | required | `RpcModelDefinition(Model, WireBody)` for each model. `WireBody` needs `id`, `api` and `provider` equal to the model, plus `name`, `baseUrl`, `reasoning`, `input`, `contextWindow`, `maxTokens` and `cost` with `input`, `output`, `cacheRead` and `cacheWrite`. |
| `options` | `new()` | `RpcDispatchOptions`, below |
| `sessionOwnership` | `Owned` | `Owned` disposes the session at shutdown; `Borrowed` leaves it to you. |
| `inputAdmission` | null | An `IPromptInputAdmission` that `prompt`, `steer` and `follow_up` go through, such as extension `input` handlers |
| `extensionUi` | null | The [`RpcExtensionUiCoordinator`](#extension-ui) that carries extension dialogs |
| `extensionCommandCatalog` | null | An `IRpcExtensionCommandCatalog` behind `get_commands` and `pisharp_complete_extension_command` |
| `sessionOwner` | null | A `ReplaceableAgentSession` whose current session is `session`. It enables the commands that replace or edit the session. |
| `sessionStartup` | null | Runs once when the input loop starts; commands wait for it. `pisharp` raises `session_start` here. |
| `summaryGenerator` | null | An `ISessionSummaryGenerator`. `compact`, automatic compaction and the `pisharp_*` summary commands need it, with `sessionOwner`. |
| `inputAdmissionSelector` | null | Chooses the input admission from the command type (`prompt`, `steer` or `follow_up`), ahead of `inputAdmission` |
| `userBash` | null | An `IUserBashExecutor` for the `bash` command. Without it, `bash` answers an error. |
| `modelRuntime` | null | An `RpcModelRuntime` whose `Available` list `set_model`, `cycle_model` and `get_available_models` read at each command, instead of `models` |
| `compactionSettings` | null | The compaction settings for a model, read by `compact` and `set_auto_compaction` |
| `prepareSessionPath` | null | Runs before `switch_session` opens a file, and returns an undo |

Without `sessionOwner`, `new_session`, `fork`, `clone`, `switch_session`, `compact` and the `pisharp_*` session commands answer with an error, as do commands that need a missing service. The other commands work on the one session. `pisharp` passes every service; see [RPC protocol](/docs/reference/rpc/#entry-points) for what each entry point offers.

### Dispatch options

`RpcDispatchOptions` is a positional record. Raise the byte limits for real use: a tool result with an image easily passes 1 MiB, and a record over `MaximumOutputBytes` can't be written. `pisharp session rpc` uses 32 MiB, and `pisharp --mode rpc` lifts the count limits as Pi has none.

| Option | Default | Meaning |
| --- | --- | --- |
| `MaximumConcurrentCommands` | 8 | Commands in flight at once. The reader waits for a free slot. |
| `MaximumCommandBytes` | 1,048,576 | Size of one command |
| `MaximumOutputBytes` | 1,048,576 | Size of one response or event; at least 256 |
| `MaximumJsonDepth` | 32 | Nesting depth of commands and records |
| `MaximumIdCharacters`, `MaximumCommandTypeCharacters` | 256 and 128 | Length of `id` and `type` |
| `MaximumPromptCharacters`, `MaximumImages` | 65,536 and 16 | Text and images in `prompt`, `steer` and `follow_up` |
| `MaximumModels`, `MaximumModelDefinitionBytes` | 128 and 1,048,576 | The `models` list |
| `MaximumReturnedMessages`, `MaximumReturnedEntries` | Pi's request message budget and 100,000 | Size of `get_messages`, `get_entries` and `get_tree` |
| `MaximumContinuationRuns`, `MaximumPendingToolMessages` | 16 and 128 | Queued follow-up runs and tool messages |

## JSONL streams

`JsonlReader(input, options, ownership)` and `JsonlWriter(output, options, ownership)` frame records with LF. The reader strips one CR before the LF and accepts a final record without one. Both take `JsonlTransportOptions` and borrow the stream unless `ownership` is `JsonlStreamOwnership.Owned`.

| `JsonlTransportOptions` | Default | Meaning |
| --- | --- | --- |
| `ReadBufferBytes` | 4,096 | Read size, 1 to 65,536 |
| `MaximumFrameBytes` | 1,048,576 | Size of one line |
| `MaximumJsonDepth` | 32 | Nesting depth |
| `MaximumPendingWrites` | 16 | Writes waiting for the stream |
| `JavaScriptInput` | `false` | Read each line as Pi's `JSON.parse` does: invalid UTF-8 becomes U+FFFD, a duplicate name keeps the last value, and any JSON value is a record. Off, the reader is strict: it refuses duplicate names, invalid UTF-8 and non-objects. |
| `JavaScriptOutput` | `false` | Write each record as Pi's `JSON.stringify` does: its escapes, number text and property order |
| `KeepsLoneSurrogates` | `false` | Accept escaped lone surrogates in strict mode |

`pisharp --mode rpc` turns on both JavaScript options, so a client written against Pi sees the same bytes. `ReadAdmissionsAsync` reports a line that fails to parse and goes on with the next; the dispatcher answers such a line with a `parse` error. A line over the frame limit ends the stream. Failures are `JsonlTransportException`s with a `JsonlTransportFailure`. `JsonlRecordFormatter.Format(record, options)` writes one record as text, for hosts that print JSONL themselves.

## Extension UI

Extensions that ask for a choice, a confirmation or text reach the client through `RpcExtensionUiCoordinator`, in `PiSharp.Rpc.Ui`. It is an `IExtensionUiProvider`: give the same instance to the extension registry and to the dispatcher.

```csharp
using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using PiSharp.Rpc;
using PiSharp.Rpc.Protocol;
using PiSharp.Rpc.Ui;

public static async Task ServeWithExtensionAsync(PersistentAgentSession session, ImmutableArray<RpcModelDefinition> models,
    Func<long> clock, IPiSharpExtension extension)
{
    await using var ui = new RpcExtensionUiCoordinator();
    await using var registry = new ExtensionRegistry(uiProvider: ui);
    await registry.ActivateAsync("ext-0", extension);

    var dispatcher = new RpcSessionDispatcher(session, new JsonlWriter(Console.OpenStandardOutput()), clock, models,
        extensionUi: ui);
    await dispatcher.RunAsync(new JsonlReader(Console.OpenStandardInput()));
    await dispatcher.SettleAsync();
}
```

Any extension callback that runs while the dispatcher is attached, from a tool the session got through `ExtensionAgentBinding` or from an event you dispatch, shows its dialogs to the client. The coordinator writes each dialog as an `extension_ui_request` record and matches the client's `extension_ui_response` by `id`; the dispatcher routes those responses to it before treating a line as a command. The [protocol page](/docs/reference/rpc/#extension-ui) lists the methods. A coordinator serves one dispatcher. Until a dispatcher attaches it, and after the connection closes, scopes it opens report no UI features.

| Outcome | When |
| --- | --- |
| `Value` | The client answered with `value` or `confirmed` |
| `Cancelled` | The client sent `cancelled: true`, or the callback's token was cancelled |
| `TimedOut` | The dialog's `TimeoutMilliseconds` passed |
| `Unavailable` | No dispatcher, a closed connection, a feature the client doesn't offer, an invalid response, or a size limit |

| Constructor parameter | Default | Meaning |
| --- | --- | --- |
| `options` | `new()` | `RpcExtensionUiOptions`, below |
| `timeProvider` | `TimeProvider.System` | Clock for dialog timeouts |
| `connectionGeneration`, `sessionGeneration` | 1 and 1 | Reported in `Capabilities` |
| `clientCapabilities` | all | The UI features your client handles: any of `Select`, `Confirm`, `Input`, `Editor`, `Notify`, `Status`, `TextWidget`, `Title` and `EditorText` |
| `presentationObserver` | null | An `IRpcExtensionUiPresentationObserver` told when a dialog was written and when it ended |

| `RpcExtensionUiOptions` | Default |
| --- | --- |
| `MaximumOutstandingRequests` | 32 (at most 4,096) |
| `MaximumRequestBytes`, `MaximumResponseBytes` | 65,536 each |
| `MaximumRetainedBytes` | 1,048,576 |
| `MaximumTextCharacters`, `MaximumChoices` | 16,384 and 256 |
| `MaximumJsonDepth`, `MaximumIdCharacters` | 32 and 128 |
| `MaximumDeferredOrdinaryFrames`, `MaximumRetainedOrdinaryBytes` | 8 and 8,388,608: commands held while dialogs are open |

To report an extension failure to the client, call `dispatcher.PublishExtensionErrorAsync(extensionPath, eventName, error)`, which writes an `extension_error` event. `pisharp` connects it to its extensions' error reports. Loading and binding the extensions themselves is on [Host extensions](/docs/embed/extension-host/).

## Event projectors

The dispatcher turns session events into wire records with two public projectors. `pisharp --mode json` uses them as well, to print the same records without the command side.

| Type | Use |
| --- | --- |
| `RpcAgentEventProjector(options)` | `Project(agentEvent, snapshot, historyLength, willRetry)` returns the records for one agent event: `agent_start`, `turn_start`, `message_start`, `message_update`, `message_end`, `tool_execution_*`, `turn_end` and `agent_end`. One instance per session, fed in order. |
| `RpcSessionEventProjector.Project(operationEvent, options)` | The record for a session event, such as `entry_appended`, `thinking_level_changed`, `session_info_changed`, `compaction_start`, `compaction_end`, `auto_retry_start`, `auto_retry_end`, the `summarization_retry_*` events and `agent_settled`, or null |
| `RpcSessionEventProjector.ExtensionError(path, eventName, error, options)` | An `extension_error` record |
