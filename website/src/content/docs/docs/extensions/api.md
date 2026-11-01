---
title: C# extension API
description: Everything a native C# extension can register and call.
---

This page is the reference for the native extension API in `PiSharp.Extensions.Abstractions`. It lists every registration method, descriptor, context and UI call a C# extension can use. To build and load your first extension, follow [C# extensions](/docs/extensions/build-an-extension/) first. The events themselves, with their payloads and results, are on [Extension events](/docs/extensions/events/).

## Packages and namespaces

Reference the packages with `ExcludeAssets="runtime"`, as the [project file](/docs/extensions/build-an-extension/#project-file) in the tutorial shows. The host supplies the assemblies at run time.

| Namespace | Package | What it holds |
| --- | --- | --- |
| `PiSharp.Extensions` | `PiSharp.Extensions.Abstractions` | `IPiSharpExtension`, the registry, descriptors, callback contexts, UI, session actions and the optional registries |
| `PiSharp.Extensions.Events` | `PiSharp.Extensions.Abstractions` | Typed event records and patches: `ExtensionToolCallEvent`, `ExtensionInputPatch` and the rest |
| `PiSharp.Extensions.Facade.Context` | `PiSharp.Extensions.Abstractions` | The Pi-style command facade: `IExtensionCommandFacade` and its optional parts |
| `PiSharp.Extensions.Mcp.Registration` | `PiSharp.Extensions.Abstractions` | `IExtensionMcpServerRegistry` |
| `PiSharp.Contracts` | `PiSharp.Contracts` | `JsonData`, `TranscriptEntry`, `ToolExposure`, `ToolNamespace`, `ToolSchemaOrigin`, `ToolReach` |
| `PiSharp.Extensions.Runtime.Facade.Context` | `PiSharp.Extensions.Runtime` | `ExtensionCommandFacade.CreateCommand`, the adapter for facade commands |

Every JSON value is a `JsonData`. Its `Value` property is a `System.Text.Json.JsonElement`, and `JsonData.Parse(string)` makes one.

## The extension class

An extension is one class that implements `IPiSharpExtension`.

| Member | Called |
| --- | --- |
| `ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)` | Once, when the host loads the extension. Register everything here. |
| `ValueTask DisposeAsync()` | Once, after the host unloads the extension (reload, shutdown or a failed load). |

Registration is a transaction. The host publishes your tools, commands and handlers together when `InitializeAsync` returns. If it throws, nothing is published, the host disposes the extension and reports a load error.

Don't start timers, watchers or sockets in `InitializeAsync`. Start them from a `session_start` observer and stop them at `session_shutdown`, as Pi asks. `registry.ExtensionLifetimeCancellationToken` is cancelled when the host unloads you. Before it calls `DisposeAsync`, the host waits for your callbacks that are still running.

In `pisharp`, `/reload` loads a fresh copy of your assembly and creates a new instance, so state in fields doesn't survive it. Keep state on the session branch instead (see [Session state](#session-state)).

## The registry

`InitializeAsync` receives an `IExtensionRegistry`.

| Member | Description |
| --- | --- |
| `OwnerId` | The host's id for this loaded extension, for example `pi-native-0` in `pisharp`. |
| `OwnerGeneration` | The load generation. It changes on every load. |
| `ContractProfile` | The contract profile string, `experimental-registration-0`. |
| `Features` | The [features](#features) this host supports. Check them before you rely on an optional capability. |
| `ExtensionLifetimeCancellationToken` | Cancelled when the extension unloads. |
| `RegisterTool(ExtensionToolDescriptor)` | Adds a [tool](#tools). |
| `RegisterCommand(ExtensionCommandDescriptor)` | Adds a [slash command](#commands). |
| `Observe(ExtensionObservationDescriptor)` | Adds an [observer](#observers) for one event topic. |
| `RegisterBeforeAgentStartHandler(ExtensionBeforeAgentStartHandlerDescriptor)` | Handles `before_agent_start`. |
| `RegisterContextHandler(ExtensionContextHandlerDescriptor)` | Handles `context`. |
| `RegisterContextWithSystemHandler(ExtensionContextWithSystemHandlerDescriptor)` | Handles `context_with_system`. |
| `RegisterInputHandler(ExtensionInputHandlerDescriptor)` | Handles `input`. |
| `RegisterToolCallHandler(ExtensionToolCallHandlerDescriptor)` | Handles `tool_call`. |
| `RegisterToolResultHandler(ExtensionToolResultHandlerDescriptor)` | Handles `tool_result`. |

Every `Register…` method and `Observe` return an `IExtensionRegistration` with `OwnerId`, `OwnerGeneration` and `RegistrationId`. Dispose it to remove that one registration. Everything else leaves with the extension.

Each descriptor starts with a `RegistrationId`. It is unique within your extension and made of letters, digits, `.`, `_` and `-`, up to 128 characters. Tool names follow the same rule. A tool name that another loaded extension already registered is refused. Command names can be any non-empty text without whitespace or control characters. When two extensions register the same command name, `pisharp` invokes them as `name:1`, `name:2` and so on, as Pi does.

The `Register…` methods throw when a descriptor is invalid, an id is used twice, a name is taken, or a host limit is reached. Let the exception escape `InitializeAsync` so the host reports the load error.

### Features

`registry.Features` lists the capabilities of the host you are running in.

| Feature | Constant | Meaning |
| --- | --- | --- |
| `session-branch-snapshot` | | Contexts carry the current branch in `SessionSnapshot`. |
| `session-durable-entries` | `ExtensionSessionActionFeatures.DurableEntries` | `AppendSessionEntryAsync` works. |
| `session-replacement` | `ExtensionSessionActionFeatures.Replacement` | Commands can switch sessions. |
| `session-creation` | `ExtensionSessionActionFeatures.Creation` | Commands can create, fork and clone sessions. |
| `session-opaque-records` | `ExtensionSessionActionFeatures.OpaqueRecords` | Session entries keep numbers exactly as written. |
| `session-catalog` | `ExtensionSessionActionFeatures.Catalog` | Contexts can list stored sessions, and commands can resume them. |
| `session-context-edits` | `ExtensionSessionActionFeatures.ContextEdits` | `AppendContextEditAsync` works. |
| `callback-tool-activation` | `ExtensionToolActivationFeatures.Feature` | Callbacks can read and set the active tools. |
| `tool-renderer-resolvers` | `ExtensionToolRendererFeatures.Feature` | `RegisterToolRenderer` works. |

The list also names the contract features that every current host has: `before-agent-start`, `context-request-transform`, `context-with-system-request-transform`, `transactional-registration`, `owned-descriptor-callbacks`, `awaited-observations`, `registered-input-tool-reducers` and `tool-invocation-context`. `pisharp` supports every feature in both tables.

## Tools

A tool is an `ExtensionToolDescriptor(RegistrationId, Name, Description, Parameters, ExecuteAsync)`. `Parameters` is the JSON Schema of the arguments. The host validates the model's arguments against it before your callback runs.

The callback is `ExtensionToolCallback`: `(JsonData arguments, IExtensionToolContext context, CancellationToken token) => ValueTask<JsonData>`. It returns Pi's tool result object:

| Field | Required | Meaning |
| --- | --- | --- |
| `content` | yes | An array of `{ "type": "text", "text": … }` and `{ "type": "image", "data": …, "mimeType": … }` blocks. The model sees this. |
| `details` | yes | Any JSON value. The session keeps it with the result; renderers and later calls read it. |
| `structuredContent` | no | Machine-readable output, for [code mode](/docs/guides/codemode/) and `OutputSchema`. |
| `usage` | no | Token usage of work the tool did, as a Pi usage object. |
| `isError` | no | `true` reports a failure to the model. |
| `terminate` | no | `true` ends the run after this tool batch. |

The descriptor also has these optional `init` properties:

| Property | Default | Meaning |
| --- | --- | --- |
| `PrepareInitialArgumentsAsync` | null | Rewrites the raw arguments before validation, as Pi's `prepareArguments` does. If it throws, the call fails with the exception's message. |
| `ParametersOrigin` | `ToolSchemaOrigin.JsonSchema` | How `Parameters` was produced. `TypeBox` also converts TypeBox 1.x schemas. |
| `ValidationParameters` | null | A different schema to validate against. Null validates against `Parameters`. |
| `Exposure` | `ToolExposure.Direct` | `Direct` declares the tool to the model. `ModelOnly` declares it but code mode can't call it. `Codemode` makes it callable only from code mode scripts. `Deferred` declares it once `tool_search` finds it. `Hidden` makes it unreachable. |
| `Namespace` | null | A `ToolNamespace(Name, Description)` that groups the tool in code mode and `tool_search`. |
| `DefaultActive` | `true` | Whether a `Direct` or `ModelOnly` tool starts active. Other exposures need explicit activation. |
| `PrepareLoadout` | null | A pure, synchronous `Func<ToolLoadout, ToolLoadoutChanges?>` that adjusts the declared tool set while this tool is active. |
| `PromptGuidelines` | empty | Lines for the system prompt's guidelines, as Pi's `promptGuidelines`. |
| `PromptSnippet` | null | The tool's one-line entry in the system prompt's tool list. |
| `ConstrainedSampling` | null | Provider sampling constraints for the declaration, for example a grammar. |
| `Renderers` | null | `ExtensionToolRenderers(RenderShell, RenderCall, RenderResult)` for the terminal UI. |
| `Annotations` | null | MCP-style hints: `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`. |
| `SequentialExecution` | `false` | `true` runs a batch that contains this tool one call at a time. |
| `OutputSchema` | null | JSON Schema of `structuredContent`. |
| `ReachesTools` | null | For tools that call or load other tools: which tools a call with the given input may reach. |

### Typed tools

`ExtensionTypedTool.Define` wraps a callback that takes and returns your own types. It deserializes the arguments and serializes the result with source-generated `JsonTypeInfo`s, then goes through the same validation and limits as any tool.

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Contracts;
using PiSharp.Extensions;

public sealed record WordCountArguments(string Text);
public sealed record WordCountDetails(int Words);

[JsonSerializable(typeof(WordCountArguments))]
[JsonSerializable(typeof(WordCountDetails))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class WordCountJson : JsonSerializerContext { }

public sealed class WordCountExtension : IPiSharpExtension
{
    private static readonly JsonData Schema = JsonData.Parse(
        """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""");

    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        registry.RegisterTool(ExtensionTypedTool.Define("word-count", "word_count", "Counts the words in a text",
            Schema, WordCountJson.Default.WordCountArguments, WordCountJson.Default.WordCountDetails,
            (arguments, context, token) =>
            {
                var words = arguments.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                var content = JsonData.Parse(JsonSerializer.Serialize(new[] { new { type = "text", text = $"{words} words" } }));
                return ValueTask.FromResult(new ExtensionTypedToolResult<WordCountDetails>(content, new(words)));
            }));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

| Type | Members |
| --- | --- |
| `ExtensionTypedToolResult<TDetails>(Content, Details)` | `Content` must be a JSON array. Optional `StructuredContent`, `Usage`, `IsError`, `Terminate`. `ToJson(detailsType)` builds the raw result. |
| `ExtensionTypedToolConversionOptions` | `MaximumUtf8Bytes` (262,144) and `MaximumJsonDepth` (32) of the converted result. |
| `ReportUpdateAsync<TDetails>(partialResult, detailsType, token)` | Extension method on `IExtensionToolInvocationContext` that sends a typed progress update. |

### The tool context

The context a tool receives implements more than `IExtensionToolContext`. Cast it to reach the rest:

| Interface | Members |
| --- | --- |
| `IExtensionToolContext` | `Tools`: the callable tool names. `ExecuteToolAsync(name, arguments, options)`: calls another tool through the normal pipeline and returns an `ExtensionToolCallOutcome(ToolCallId, ToolName, Result, IsError, ParentToolCallId)`. |
| `IExtensionToolInvocationContext` | `ToolCallId`, `ParentToolCallId`, `SessionGeneration`, `CallDepth`, and `ReportUpdateAsync(partialResult, token)`, which sends a `tool_execution_update`. One update may be pending at a time. |
| `IExtensionToolActivationContext` | `GetActiveTools()` and `SetActiveTools(names)`, which returns an `ExtensionToolActivationSelection(Revision, Names)`. Needs `callback-tool-activation`. |

`ExtensionExecuteToolOptions(CancellationToken, OnUpdate)` passes a token and a callback for the nested tool's updates. When nested calls aren't available, `ExecuteToolAsync` returns an error outcome instead of throwing. Every callback context also has the members in [Callback contexts](#callback-contexts).

## Commands

A command is an `ExtensionCommandDescriptor(RegistrationId, Name, Description, ExecuteAsync)`. The user runs it as `/name`. The callback is `(JsonData arguments, IExtensionCommandContext context, CancellationToken token) => ValueTask`.

In `pisharp`, `arguments` is a JSON string that holds the text after the command name. `/checkpoint before refactor` passes `"before refactor"`, so read it with `arguments.Value.GetString()`.

| Property | Meaning |
| --- | --- |
| `GetArgumentCompletionsAsync` | `(string prefix, CancellationToken) => ValueTask<JsonData>`. Returns a JSON array of Pi autocomplete items (`value`, `label`, optional `description`), or JSON `null`. |
| `SourcePath` | The file the command comes from, for display. |

The command context implements every [callback context](#callback-contexts) interface, plus these:

| Interface | Members |
| --- | --- |
| `IExtensionCommandCatalogContext` | `CommandCatalogRevision`, and `CommandCatalog`: the host's command list as JSON. |
| `IExtensionSessionCommandContext` | `SwitchSessionAsync(absolutePath, useLatestLeaf, selectedLeafId, token)`. Returns a fresh context for the new session, or null when a handler cancelled. Needs `session-replacement`. |
| `IExtensionSessionCreationCommandContext` | `CreateSessionAsync(ExtensionSessionCreationRequest(Kind, EntryId, ParentSession), token)`, where `Kind` is `New`, `ForkBefore`, `ForkAt` or `Clone`. Returns `ExtensionSessionCreationResult(Context, SelectedText)` or null. Needs `session-creation`. |
| `IExtensionSessionCatalogCommandContext` | `ResumeSessionAsync(catalogKey, useLatestLeaf, selectedLeafId, token)`. Needs `session-catalog`. |
| `IExtensionSessionTreeCommandContext` | `NavigateTreeAsync(ExtensionSessionTreeRequest(TargetId, Summarize, CustomInstructions, ReplaceInstructions, Label), token)`, as Pi's `navigateTree`. |

After a switch, fork or new session, the old context is stale. Its session actions throw. Use the context the call returned. The [SessionCheckpoint](https://github.com/jkelly/PiSharp/tree/main/samples/extensions/SessionCheckpoint) sample shows a command that saves an entry, switches sessions and saves again in the new one.

## Observers

`Observe(new ExtensionObservationDescriptor(RegistrationId, Topic, ObserveAsync))` subscribes to one event. The callback is `(JsonData observation, IExtensionContext context, CancellationToken token) => ValueTask`. It receives the Pi event object, with its `type`, and can't change anything.

```csharp
registry.Observe(new("on-start", "session_start", async (observation, context, token) =>
{
    var reason = observation.Value.GetProperty("reason").GetString();
    if (context is IExtensionUiContext ui && ui.Ui.Capabilities.Supports(ExtensionUiFeature.Notify))
        await ui.Ui.PublishAsync(new ExtensionUiNotify("Session " + reason, ExtensionUiNotifyKind.Info), token);
}));
```

The host awaits observers in registration order. Every topic, its payload, and the handler types that can change an event are on [Extension events](/docs/extensions/events/).

## Typed handlers

The six handler methods on the registry take a descriptor of `(RegistrationId, HandleAsync)`. `HandleAsync` is an `ExtensionReducerCallback<TEvent, TPatch>`: `(TEvent snapshot, IExtensionContext context, CancellationToken token) => ValueTask<TPatch?>`. Return null to change nothing.

| Method | Event record | Patch |
| --- | --- | --- |
| `RegisterBeforeAgentStartHandler` | `ExtensionBeforeAgentStartEvent` | `ExtensionBeforeAgentStartPatch(Message, SystemPrompt)` |
| `RegisterContextHandler` | `ExtensionContextEvent(Messages)` | `ExtensionContextMessagesPatch(Messages)` |
| `RegisterContextWithSystemHandler` | `ExtensionContextWithSystemEvent(Messages)` | `ExtensionContextMessagesPatch(Messages)` |
| `RegisterInputHandler` | `ExtensionInputEvent(Text, Source, Images, StreamingBehavior)` | `ExtensionInputPatch(Action, Text, Images)` |
| `RegisterToolCallHandler` | `ExtensionToolCallEvent(ToolName, ToolCallId, Arguments, ParentToolCallId)` | `ExtensionToolCallPatch(Arguments, Decision)` |
| `RegisterToolResultHandler` | `ExtensionToolResultEvent(ToolName, ToolCallId, Arguments, Result, ParentToolCallId, OutcomeIsError)` | `ExtensionToolResultPatch(Fields)` |

The records are in `PiSharp.Extensions.Events`. How each patch combines with other handlers' patches is on [Extension events](/docs/extensions/events/#typed-handlers).

This guard blocks `rm -rf` in `bash` calls:

```csharp
registry.RegisterToolCallHandler(new("no-rm-rf", (call, context, token) =>
{
    var blocked = call.ToolName == "bash" &&
        call.Arguments.Value.TryGetProperty("command", out var command) &&
        command.GetString()?.Contains("rm -rf", StringComparison.Ordinal) == true;
    return ValueTask.FromResult<ExtensionToolCallPatch?>(blocked
        ? new(Decision: JsonData.Parse("""{"block":true,"reason":"rm -rf is not allowed"}"""))
        : null);
}));
```

## Optional registries

The registry object implements more interfaces than `IExtensionRegistry`. Cast it to reach them. In `pisharp` every cast succeeds; another host may not offer all of them.

| Interface | Members | Notes |
| --- | --- | --- |
| `IExtensionEventHandlerRegistry` | `RegisterEventHandler(ExtensionEventHandlerDescriptor(RegistrationId, Topic, HandleAsync))` | Handlers for events that return a result, as raw Pi JSON. See [events](/docs/extensions/events/#event-handlers). |
| `IExtensionEventBusRegistry` | `Events`: an `IExtensionEventBus` with `Emit(channel, data)` and `On(channel, handler)`, which returns an `IDisposable` | The bus TypeScript extensions reach as `pi.events`. `Events` is also an `IAsyncExtensionEventBus` with `OnAsync`, `OnValueTask` and `DrainAsync`. |
| `IExtensionSessionLifecycleRegistry` | `RegisterSessionSwitchHandler(ExtensionSessionSwitchHandlerDescriptor)` | Can cancel a session switch. |
| `IExtensionSessionCreationRegistry` | `RegisterSessionCreationHandler(ExtensionSessionCreationHandlerDescriptor)` | Can cancel a new session, fork or clone. |
| `IExtensionSessionTreeLifecycleRegistry` | `RegisterSessionBeforeTreeHandler(ExtensionSessionBeforeTreeHandlerDescriptor)` | Can cancel tree navigation or supply its summary. |
| `IExtensionUserBashRegistry` | `RegisterUserBashHandler(ExtensionUserBashHandlerDescriptor)` | Can run the user's `!` commands elsewhere or supply their result. |
| `IExtensionToolRendererRegistry` | `RegisterToolRenderer(ExtensionToolRendererDescriptor(RegistrationId, Resolve))` | Chooses renderers for any tool by name, including tools that aren't registered yet. `Resolve(toolName, next)` returns `next() ?? mine` to fill in only. |
| `IExtensionMcpServerRegistry` | `RegisterMcpServer(name, config)`, `UnregisterMcpServer(name)`, `GetMcpServers()` | `config` has the shape of an `mcpServers` entry. A server in `mcp.json` with the same name wins. Throws `NotSupportedException` when the host runs no MCP servers. See [MCP](/docs/guides/mcp/). |
| `IExtensionModelOperationProviderRegistry` | `RegisterModelOperationProvider(ExtensionModelOperationProvider)`, `UnregisterModelOperationProvider(name)` | Classifier and image providers. See [Classifier and image providers](/docs/extensions/build-an-extension/#classifier-and-image-providers). |

The handler signatures of the session and `user_bash` registries are on [Extension events](/docs/extensions/events/#session-lifecycle-handlers).

## Callback contexts

Every callback gets a context. All contexts implement `IExtensionContext`:

| Member | Meaning |
| --- | --- |
| `OwnerId`, `OwnerGeneration` | The extension that owns the callback. |
| `OperationCancellationToken` | Cancelled when the operation the callback serves is cancelled. |
| `SessionCancellationToken` | Cancelled when the session the callback belongs to ends or is replaced. |
| `ExtensionLifetimeCancellationToken` | Cancelled when the extension unloads. |

The `CancellationToken` parameter of the callback combines all three.

The contexts `pisharp` passes also implement these interfaces. Each works only when the host has the matching feature; otherwise its members throw or return nothing.

| Interface | Members | Feature |
| --- | --- | --- |
| `IExtensionUiContext` | `Ui`: the [UI](#ui) | none |
| `IExtensionSessionContext` | `SessionSnapshot`: an `ExtensionSessionSnapshot(SessionId, Generation, SelectedLeafId, BranchEntries)`, or null without a session | `session-branch-snapshot` |
| `IExtensionSessionActionsContext` | `AppendSessionEntryAsync(entryKind, schemaVersion, data, token)` | `session-durable-entries` |
| `IExtensionSessionCatalogContext` | `ListSessionsAsync(ExtensionSessionCatalogQuery(PageSize, Cursor, WorkingDirectory), token)` returns an `ExtensionSessionCatalogPage(Items, NextCursor, SkippedFiles, UnavailableStores)` | `session-catalog` |
| `IExtensionSessionContextEditContext` | `AppendContextEditAsync(targetId, replacement, token)` replaces what an earlier entry contributes to the model's context. A null `replacement` removes it. | `session-context-edits` |
| `IExtensionSessionCompactionContext` | `AppendCompactionSummaryAsync(ExtensionSessionCompactionSummary(FirstKeptEntryId, Text, Usage, Details), token)` writes a compaction you computed yourself | `session-durable-entries` |
| `IExtensionToolActivationContext` | `GetActiveTools()`, `SetActiveTools(names)` | `callback-tool-activation` |

`BranchEntries` holds the raw JSON of every entry on the selected branch, from the root to the leaf, with unknown properties kept. `Persistence` says whether the session is a local file (`DurableLocalFile`), in memory (`VolatileMemory`) or a file not written yet (`DeferredLocalFile`).

## UI

`IExtensionUiContext.Ui` is an `IExtensionUi`. Check `Ui.Capabilities` before you show something.

| Member | Returns |
| --- | --- |
| `Capabilities` | `ExtensionUiCapabilities(Mode, ConnectionGeneration, SessionGeneration, Features)` with `Supports(feature)` |
| `SelectAsync(title, choices, options, token)` | `ExtensionUiOutcome<string>`: the chosen item |
| `ConfirmAsync(title, message, options, token)` | `ExtensionUiOutcome<bool>` |
| `InputAsync(title, placeholder, options, token)` | `ExtensionUiOutcome<string>` |
| `EditorAsync(title, prefill, token)` | `ExtensionUiOutcome<string>`: multi-line text |
| `PublishAsync(notification, token)` | `ExtensionUiOutcome<ExtensionUiPublication>` |

`ExtensionUiDialogOptions(TimeoutMilliseconds)` sets a timeout for `Select`, `Confirm` and `Input`. `0` turns the timer off.

`Mode` is `Tui` in the terminal UI, `Rpc` in RPC mode, and `Print` (with no features) in print and JSON modes. The features are `Select`, `Confirm`, `Input`, `Editor`, `Notify`, `Status`, `TextWidget`, `Title`, `EditorText` and `CustomTerminalComponent`. RPC mode offers all but `CustomTerminalComponent`.

An outcome has a `Kind`: `Value`, `Cancelled`, `TimedOut` or `Unavailable`. Read `Value` only when `Kind` is `Value`; otherwise it throws. An unavailable outcome also has an `UnavailableReason`: `NoUi`, `NotConnected`, `Disconnected`, `StaleContext`, `UnsupportedCapability`, `InvalidResponse`, `ResourceLimit` or `OutputFailed`. `ExtensionUiSourceDefaults.Confirmation(outcome)` is `true` only for a confirmed value, so a missing UI never counts as approval. `ExtensionUiSourceDefaults.Text(outcome)` returns the text or null.

`PublishAsync` takes one of these notifications:

| Notification | Pi call |
| --- | --- |
| `ExtensionUiNotify(Message, Kind)`, with `Kind` `Info`, `Warning` or `Error` | `ctx.ui.notify` |
| `ExtensionUiStatus(Key, Text)`; null `Text` clears it | `ctx.ui.setStatus` |
| `ExtensionUiTextWidget(Key, Lines, Placement)`, with `Placement` `AboveEditor` or `BelowEditor`; null `Lines` removes it | `ctx.ui.setWidget` with lines |
| `ExtensionUiTitle(Title)` | `ctx.ui.setTitle` |
| `ExtensionUiEditorText(Text)` | `ctx.ui.setEditorText` |

```csharp
if (context is IExtensionUiContext { Ui: var ui } && ui.Capabilities.Supports(ExtensionUiFeature.Confirm))
{
    var answer = await ui.ConfirmAsync("Delete branch", "Remove the scratch branch?", new(TimeoutMilliseconds: 30_000), token);
    if (ExtensionUiSourceDefaults.Confirmation(answer))
        await ui.PublishAsync(new ExtensionUiStatus("cleanup", "scratch branch removed"), token);
}
```

## Session state

Keep state on the session branch, not in fields. The branch changes when the user forks or moves in the tree, and your state then follows it.

There are two ways:

- **Tool details.** Return the state in your tool result's `details`, and rebuild it from the `toolResult` messages in `SessionSnapshot.BranchEntries`. [StatefulTodo](https://github.com/jkelly/PiSharp/tree/main/samples/extensions/StatefulTodo) does this.
- **Extension entries.** Write a custom entry with `AppendSessionEntryAsync` and read the latest one with `ExtensionSessionState.ReadLatest`.

`AppendSessionEntryAsync(entryKind, schemaVersion, data, token)` writes a custom entry with `customType` `pisharp.extension-state`. Its `data` is an envelope of `extensionId` (your `OwnerId`), `entryKind`, `schemaVersion` and your `data`. `entryKind` is up to 128 letters, digits, `.`, `_` or `-`, and `schemaVersion` is at least 1. The call returns an `ExtensionSessionEntryAcknowledgment(SessionId, Generation, Entry, Sequence, ByteOffset, ByteLength, SelectedLeafId)` once the entry is written. `DurableCheckpointAcknowledged` is true when it is on disk.

`ExtensionSessionState.ReadLatest(context, entryKind, expectedSchemaVersion, token)` is an extension method on `IExtensionSessionContext`. It returns the newest entry on the branch with your `OwnerId` and that `entryKind`, as an `ExtensionSessionStateValue(EntryId, EntryKind, SchemaVersion, Data)`, or null. A matching entry with another schema version throws `ExtensionSessionStateException` with `IncompatibleVersion`, so you migrate old data yourself. Without a session snapshot it throws `InvalidOperationException`.

:::caution[Owner ids change]
`ReadLatest` matches on `OwnerId`. In `pisharp` the owner id comes from the extension's load position (`pi-native-0`, `pi-native-1`…) and gains a suffix after `/reload` (`pi-native-0-r1`). Entries an earlier load wrote are then no longer found. For state that must survive reloads and changes to the extension list, keep it in tool `details`, or read the `pisharp.extension-state` entries from `BranchEntries` yourself.
:::

```csharp
registry.RegisterCommand(new("note", "note", "Remember a note on this branch", async (arguments, context, token) =>
{
    if (context is not IExtensionSessionActionsContext session) return;
    var previous = session.ReadLatest("note", cancellationToken: token)?.Data.Value.GetProperty("text").GetString();
    var text = arguments.Value.GetString() ?? "";
    await session.AppendSessionEntryAsync("note", 1, JsonData.Parse(JsonSerializer.Serialize(new { text, previous })), token);
}));
```

## Facade commands

`IExtensionCommandFacade` gives a command a context shaped like Pi's `ExtensionCommandContext`, with synchronous reads. Create the command with `ExtensionCommandFacade.CreateCommand(registrationId, name, description, callback)` from `PiSharp.Extensions.Runtime`, and register it as usual. Add a `PiSharp.Extensions.Runtime` package reference with `ExcludeAssets="runtime"`; `pisharp` shares its own copy.

The callback is `(JsonData arguments, IExtensionCommandFacade context, CancellationToken token) => ValueTask`.

| Member | Meaning |
| --- | --- |
| `OwnerId`, `OwnerGeneration` | The owning extension |
| `Cwd` | The working directory |
| `Model` | The current model as Pi model JSON, or null |
| `IsIdle`, `HasPendingMessages` | Whether the agent is idle, and whether messages are queued |
| `SystemPrompt` | The current system prompt |
| `SessionId`, `LeafId` | The session and its selected leaf |
| `GetBranch()` | The branch entries as raw JSON |
| `NewSessionAsync(parentSession, token)` | Starts a new session |
| `ForkAsync(entryId, before, token)` | Forks before (default) or at an entry |
| `SwitchSessionAsync(absolutePath, token)` | Opens another session file |

The three session calls return the facade of the new session, or null when a handler cancelled. The facade object also implements these interfaces:

| Interface | Members |
| --- | --- |
| `IExtensionSessionGraphFacade` | Pi's read-only session manager: `GetEntry`, `GetLeafEntry`, `GetEntries`, `GetBranch(entryId)`, `GetTree`, `GetHeader`, `SessionFile`, `SessionName`, `GetLabel`, `SessionCwd`, `SessionDirectory`, `BuildContextEntries`, `BuildSessionProjection` |
| `IExtensionHostActionFacade` | `WaitForIdleAsync`, `ReloadAsync`, `CompactAsync(customInstructions)` |
| `IExtensionSettingsThinkingReadFacade` | `GetSettings()`, `GetThinkingLevel()` |
| `IExtensionSystemPromptOptionsReadFacade` | `GetSystemPromptOptions()` |
| `IExtensionTreeCommandFacade` | `NavigateTreeAsync(request, token)` |
| `IExtensionSessionBehaviorFacade` | `SetThinkingLevelAsync`, `Abort`, `CompactWithCallbacksAsync`, and `NewSessionWithSessionAsync`, `ForkWithSessionAsync`, `SwitchSessionWithSessionAsync`, which run a callback in the new session |
| `IExtensionLifecycleHandoffFacade` | `RequestShutdown`, `RequestReload` |
| `IExtensionModelOperationsFacade` | `GetModelsOfType`, `GetModelOfType`, `GetAvailableOfTypeAsync`, `ClassifyAsync`, `GenerateImagesAsync` |
| `IExtensionExecCommandFacade` | `ExecAsync(command, arguments, options)` |

A member the host doesn't support throws `NotSupportedException`. In `pisharp`, `ExecAsync` throws: plain `pisharp` has no execution host for native extensions.

```csharp
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime.Facade.Context;

registry.RegisterCommand(ExtensionCommandFacade.CreateCommand("retry-last", "retry-last",
    "Fork the session before the last user message", async (arguments, pi, token) =>
    {
        var lastUser = pi.GetBranch().LastOrDefault(entry =>
            entry.Value.TryGetProperty("message", out var message) &&
            message.TryGetProperty("role", out var role) && role.GetString() == "user");
        if (lastUser is null) return;
        if (pi is IExtensionHostActionFacade host) await host.WaitForIdleAsync(token);
        await pi.ForkAsync(lastUser.Value.GetProperty("id").GetString()!, before: true, token);
    }));
```

## What native extensions can't do yet

Some parts of Pi's `ExtensionAPI` have contracts in the package but no native registration in `pisharp`. A TypeScript extension through the [Node bridge](/docs/extensions/node-bridge/) can use them:

- Flags (`registerFlag`, `getFlag`).
- Chat providers (`registerProvider` for chat models). Classifier and image providers work natively.
- `sendMessage`, `sendUserMessage` and `setModel`.
- Shortcuts, message renderers and virtual models.
- `exec` from a command.

The session vetoes return only continue or cancel. Pi's extra results, such as `skipConversationRestore` for `session_before_fork`, need a TypeScript extension.

## Manifest and discovery

A native extension folder holds a `pisharp-extension.json` with `assembly` and `entryType`, next to the build output. `pisharp` finds it in the same places as TypeScript extensions and loads it into its own collectible assembly load context. Assemblies the host already has, such as PiSharp's own and .NET's, are shared; the rest load from your folder through its `.deps.json`. The manifest fields, folder layout and search paths are in [C# extensions](/docs/extensions/build-an-extension/#the-extension-folder).

The entry type must be public (or nested public), not abstract and not generic, implement `IPiSharpExtension`, and have a parameterless constructor. Otherwise the load fails with one of these errors:

| Error | Cause |
| --- | --- |
| `pisharp-extension.json must name its "assembly".` | `assembly` (or `entryType`) is missing or empty |
| `Extension assembly does not exist: <path>` | The file `assembly` names isn't there |
| `Type "<entryType>" not found in <file>` | No type with that full name |
| `Type "<entryType>" must be a public IPiSharpExtension with a parameterless constructor` | The type breaks one of the rules above |

To load extensions in your own app instead of `pisharp`, see [Host extensions](/docs/embed/extension-host/).
