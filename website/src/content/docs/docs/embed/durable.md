---
title: Durable library
description: "Build crash-safe agents on PiSharp.Durable: the harness, storage, tools and tests."
---

`PiSharp.Durable` is the library behind [`pisharp durable`](/docs/guides/durable/). It is a port of Pi's `pi-durable` v1.2.0 package: an agent harness that commits every model turn, streamed partial, tool call and queued message to storage before anything sees it. Open the same storage after a crash and the harness finishes what was running. This page shows how to build your own agent on it.

:::caution[Experimental]
The durable libraries ship as experimental in PiSharp 1.2.0.1. Every public type carries `[Experimental("PISHARP_DURABLE001")]`, and the API may change or be removed in any release without notice. A few members described here are on `main` only and marked "(experimental, unreleased)".
:::

## Install

Add the packages and suppress the experimental diagnostic:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <NoWarn>$(NoWarn);PISHARP_DURABLE001</NoWarn>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="PiSharp.Durable" Version="1.2.0.1" />
  <PackageReference Include="PiSharp.Durable.Sqlite" Version="1.2.0.1" />
  <PackageReference Include="PiSharp.Durable.Tools" Version="1.2.0.1" />
</ItemGroup>
```

| Package | Namespaces | Contents |
| --- | --- | --- |
| `PiSharp.Durable` | `PiSharp.Durable`, `PiSharp.Durable.Chord`, `PiSharp.Durable.Storage`, `PiSharp.Durable.Storage.Jsonl`, `PiSharp.Durable.Env`, `PiSharp.Durable.Testing` | Sessions, the harness, tasks, JSON values, memory and JSONL storage, the execution environment contract, conformance kits |
| `PiSharp.Durable.Sqlite` | `PiSharp.Durable.Sqlite` | SQLite storage over `Microsoft.Data.Sqlite` |
| `PiSharp.Durable.Tools` | `PiSharp.Durable.Tools`, `PiSharp.Durable.Tools.Env` | The local execution environment, the coding tools, `tool_search`, durable code mode, and the media tools (experimental, unreleased) |

## How it fits together

- A **session** (`ISession`) is a transactional store of conversations, entries, documents, tasks and submissions over an `IStorage`. Every change is a commit; a commit either lands whole or not at all.
- A **harness** (`IHarness`) is a session that also runs agents. It owns the built-in generation, tool and compaction tasks, the inbox of queued messages, and conversation handles.
- A **registry** holds your code: extensions with tools, prompt sections, hooks, wraps and task definitions. Nothing in the registry is saved. Conversations store the names of the extensions and tools they use, and the harness resolves those names against the registry each time.
- A **conversation** (`IConversation`) is one transcript with its agent choices: model, thinking level, extensions, tools, instructions, working directory.
- A **task** is a small state machine whose checkpoint is saved after every step. Model turns and tool calls are tasks too, which is why they resume.
- **Model access** (`IDurableModels`) is how generation reaches a model. The harness never talks to a provider any other way.

The values the library stores are `PiSharp.Durable.Chord.JsonValue` trees: `JsonValue.Object`, `JsonValue.Array`, `JsonValue.Parse`, implicit conversions from `string`, `double` and `bool`, and `ToJson()` to print one.

## A first agent

This program runs one turn on in-memory storage. The model is scripted, so it runs offline: it first calls a `clock` tool, then answers.

```csharp
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;

var context = DurableContext.Background;

var clock = Define.Tool(new ToolRegistration
{
    Name = "clock",
    Description = "Returns the current UTC time.",
    Replay = ToolReplay.Safe,
    Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult
    {
        Output = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", DateTimeOffset.UtcNow.ToString("O"))])]),
    }),
});

var registry = Registry.Create();
registry.Install(new Extension
{
    Name = "assistant",
    Tools = [clock],
    Sections = [Define.Section("preamble", "You are a concise assistant.", tag: false)],
});

var models = new ScriptedModels(
    ScriptedReply.ToolCall("clock", "{}"),
    ScriptedReply.Answer("It is afternoon in UTC."));

var harness = await Harness.OpenAsync(new MemoryStorage(), new HarnessOptions { Models = models, Registry = registry }, context);
var root = await harness.RootAsync(context, new AgentChange { Model = new ModelRef("scripted", "scripted-1") });

var submission = await root.SubmitAsync(SubmissionDraft.Input("What time is it?"), context);
var settled = await submission.WaitAsync(context);
if (settled is { Status: SubmissionStatus.Done, Answer: { } answer })
{
    var entry = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answer), context);
    Console.WriteLine(entry?.Model?[0].ToJson());
}
else
{
    Console.WriteLine($"unanswered: {settled.Reason}");
}

await harness.CloseAsync(context);
```

- `Registry.Create()` returns a registry that already holds the built-in tasks. `Harness.OpenAsync` throws for a registry without them.
- `RootAsync` returns the root conversation. The first call creates it with the given agent choices in one commit. Later calls, including after a restart, return it and ignore the arguments.
- `SubmitAsync` queues the input; `WaitAsync` resolves when it is answered or can no longer be. The answer is an entry of kind `pi.assistant` whose `Model` holds the assistant message as wire JSON.
- `CloseAsync` closes the harness and its storage.

The scripted model implements `IDurableModels`. It answers each request with the next reply, as pi-ai's faux provider does:

```csharp
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

public sealed record ScriptedReply(string? Text, ToolCallContent? Call)
{
    public static ScriptedReply Answer(string text) => new(text, null);

    public static ScriptedReply ToolCall(string name, string argumentsJson) =>
        new(null, new ToolCallContent("call-" + Guid.NewGuid().ToString("N")[..8], name, JsonData.Parse(argumentsJson)));
}

public sealed class ScriptedModels(params ScriptedReply[] replies) : IDurableModels
{
    private readonly ConcurrentQueue<ScriptedReply> queue = new(replies);

    private static readonly DurableModel Model = new()
    {
        Id = "scripted-1", Name = "Scripted", Api = "scripted", Provider = "scripted", ContextWindow = 128000, MaxTokens = 16384,
    };

    public DurableModel? GetModel(string provider, string modelId) =>
        provider == Model.Provider && modelId == Model.Id ? Model : null;

    public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages,
        DurableStreamOptions? options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var partial = new AssistantMessage(model.Api, model.Provider, model.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [],
            TokenUsage.Zero, StopReason.Pending);
        if (!queue.TryDequeue(out var reply))
        {
            yield return new StreamError(StopReason.Error, partial with
            {
                StopReason = StopReason.Error,
                ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"No more scripted replies\"")),
            });
            yield break;
        }
        yield return new StreamStarted(partial);
        if (reply.Call is { } call)
        {
            yield return new ToolCallStarted(0, call with { Arguments = JsonData.Parse("{}") });
            yield return new ToolCallDelta(0, call.Arguments.ToString());
            yield return new ToolCallEnded(0, call);
            yield return new StreamDone(StopReason.ToolUse, partial with { Content = [call], StopReason = StopReason.ToolUse });
            yield break;
        }
        var text = reply.Text ?? "";
        yield return new TextStarted(0, new TextContent(""));
        yield return new TextDelta(0, text);
        yield return new TextEnded(0, text);
        yield return new StreamDone(StopReason.Stop, partial with { Content = [new TextContent(text)], StopReason = StopReason.Stop });
    }

    public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages,
        DurableStreamOptions? options, CancellationToken cancellationToken)
    {
        StreamTerminalEvent? terminal = null;
        await foreach (var item in StreamSimple(model, messages, options, cancellationToken))
            if (item is StreamTerminalEvent end) terminal = end;
        return terminal!.Message;
    }

    public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        Task.FromException<AssistantMessage>(new NotSupportedException());

    public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

:::tip
The custom agents library has a ready-made scripted model, `FauxAgentModels` in `PiSharp.CustomAgents.Testing` (experimental, unreleased). See [Custom agents](/docs/custom-agents/overview/).
:::

## Real models

`NativeDurableModels` streams through PiSharp's built-in providers. Pass an environment reader for API keys: with none, it reads no environment variables.

```csharp
using PiSharp.Durable;

using var models = new NativeDurableModels(new NativeDurableModelsOptions
{
    Environment = Environment.GetEnvironmentVariable,
});
var model = models.GetModel("openai", "gpt-6-sol") ?? throw new InvalidOperationException("Unknown model");
Console.WriteLine($"native route: {NativeDurableModels.Supports(model)}");
Console.WriteLine("API key from: " + string.Join(", ", NativeDurableModels.ApiKeyVariables("openai")));
```

`Supports` says whether the adapter can stream a model on its own: its API has a native route and its provider needs no host auth. `ApiKeyVariables` lists the variables it reads a provider's key from, in order. Pass the harness `models` as `HarnessOptions.Models`, and dispose it when the harness is closed.

| `NativeDurableModelsOptions` | Meaning |
| --- | --- |
| `Providers` | The built-in providers to register; null registers every one with a catalog. |
| `Catalogs` | Host catalogs, each replacing its provider's built-in catalog. |
| `Environment` | Reads one environment variable for key lookup. Null reads none. |
| `ResolveApiKey` | The host's key for a request, consulted before the environment. |
| `HttpHandler` | The HTTP handler for every request; caller-owned. |
| `HostTransport` | The host's own route for a request, tried before the native routes. `pisharp durable` uses it for OAuth and models.json providers. |
| `TimeProvider` | Clock of timestamps and request deadlines. |
| `ModelOperations` | The registry behind `GetModelsOfType`, `ClassifyAsync` and `GenerateImagesAsync` (code mode's `models.*`). See [Model operations](/docs/embed/model-operations/). |

[`samples/Durable.Chat`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Chat) is a complete chat app on `NativeDurableModels`.

To write your own model access, implement `IDurableModels`. `GetModel`, `StreamSimple`, `CompleteSimpleAsync` and the three deferred-response methods are required. `GetModelsOfType`, `GetAvailableOfTypeAsync`, `GetModelOfType`, `ClassifyAsync` and `GenerateImagesAsync` have default implementations that report nothing.

## Harness options

`Harness.OpenAsync(IStorage storage, HarnessOptions options, DurableContext context)` opens a harness over storage. Tasks that were running when the last process stopped go back to pending. Nothing runs until you call `Resume()` or a method that asks for progress: `SubmitAsync`, `CompactAsync`, `AbortAsync`, `WaitAsync`, `WaitForTaskAsync` or `WaitForIdleAsync`.

| `HarnessOptions` | Meaning |
| --- | --- |
| `Models` | Required. The model access generation uses. |
| `Registry` | Required. The registry; it may keep changing while the harness runs. |
| `Settings` | Harness-wide run policy (`HarnessSettings`). |
| `Env` | `Func<EnvTarget, DurableContext, Task<IExecutionEnv?>>`: builds a conversation's execution environment at each use. `EnvTarget` carries the conversation id, its agent's `Cwd` and a document reader. Without it, tools that need files or a shell fail with an error result. |
| `ConversationCreated` | Runs in every commit that creates or forks a conversation, after the built-in documents exist. |
| `Now` | The harness clock in milliseconds. |
| `TimeProvider` | Timers for sleeps and idle expiry. Default `TimeProvider.System`. |
| `OnReport` | Receives failures that don't fail the calling operation, such as a throwing hook, and the error that fails the session. |

`HarnessSettings` is read at every use and never copied, so you can derive from it and override getters to follow a settings file. A null property takes the default:

| `HarnessSettings` | Meaning and default |
| --- | --- |
| `Extensions` | Every installed extension, in install order. |
| `Stream` | `ConversationStreamOptions`: `Transport`, `TimeoutMs`, `MaxRetries`, `MaxRetryDelayMs`, `Headers`, `Metadata`, `CacheRetention`, `Deferred`. |
| `Retry` | `RetrySettings`: enabled, 3 retries, 2000 ms base delay, 60000 ms longest delay. |
| `Compaction` | `CompactionSettings`: enabled, `ReserveTokens` 16384, `KeepRecentTokens` 20000, `BackgroundTokens` 32768 (0 turns background compaction off). |
| `Progress` | `ProgressSettings`: commit partial answers and tool output at most every 100 ms. |
| `ToolExecution` | `ToolExecutionMode.Parallel`. |
| `SteeringMode`, `FollowUpMode` | `QueueMode.OneAtATime`. |
| `ContextRetentionMs` | 600000: how long an idle conversation keeps its last context in memory. |
| `DocumentRetentionMs` | Ten minutes: how long an unobserved document stays loaded. |

## The harness

`IHarness` extends `ISession` with:

| Member | What it does |
| --- | --- |
| `Resume()` | Starts task scheduling. Idempotent. |
| `RootAsync(context, agent, init)` | The root conversation (id 1), created on the first call. |
| `ConversationAsync(id, context)` | A conversation by id, or null. |
| `CreateConversationAsync(options, context)` | Creates a conversation. `ConversationCreateOptions(ownership)` takes an `Agent` change and an `Init` callback. |
| `GetTaskAsync`, `WaitForTaskAsync`, `AbortTaskAsync` | Read, await or abort a task by id. |
| `SubmissionAsync`, `AbortSubmissionAsync` | A submission by id, or abort one. |
| `WaitForIdleAsync(context)` | Waits until no task is runnable. |
| `UsageAsync(context)` | The summed token usage of every conversation. |
| `TaskGraphAsync`, `WatchTaskGraphAsync` | The live task graph (see [Inbox and task graph](#inbox-and-task-graph)). |
| `InspectAsync(context)` | A diagnostic snapshot of tasks and scheduling. |

From `ISession` it has `CommitAsync` (four overloads over an `ITx`), `SnapshotAsync` and `WatchDocAsync` for documents, `DocumentStateAsync`, `SubscribeCommits`, `SubscribeClose`, `Closed` and `CloseAsync`. `Session.Create(storage)` gives you a bare session without agents; examples 00 to 05 use one.

## Conversations

`IConversation` is one transcript:

| Member | What it does |
| --- | --- |
| `Id` | The conversation id. |
| `AgentAsync(context)` | The agent choices resolved against the registry: model, thinking level, extensions, tools, sections, instructions, cwd. |
| `ConfigureAsync(change, context)` | Applies an `AgentChange`. |
| `SubmitAsync(draft, context)` | Queues an input or a passive write; returns an `ISubmission`. |
| `CompactAsync(instructions, context)` | Starts a manual compaction task. |
| `ResetAsync(handoff, context)` | Starts the context over from a reset entry. |
| `CommitAsync(...)` | A transaction scoped to this conversation. |
| `ContextAsync(context, at)` | The transcript and the model context at the head or at an entry. |
| `EntriesAsync(query, limit, cursor, context)` | A page of entries. |
| `ForkAsync(at, options, context)` | A new conversation that shares history up to an entry. |
| `AbortAsync`, `WaitForIdleAsync` | Abort the running turn, or wait until the conversation is idle. |
| `ViewStateAsync`, `WatchAsync` | A view of the conversation for a UI, and a watch of it. |

`AgentChange` sets the agent choices. A set field replaces the stored one, `Patch<T>.Clear` clears it, and a null field changes nothing:

| `AgentChange` | Type |
| --- | --- |
| `Model` | `ModelRef(provider, modelId)` |
| `ThinkingLevel` | `off`, `minimal`, `low`, `medium`, `high` or `xhigh` |
| `Extensions` | `ExtensionSelection.Only(...)` or `ExtensionSelection.Edit(add, remove)` |
| `Tools` | `ToolSelection.Only(...)` or `ToolSelection.Without(...)` |
| `ModelTools` | `ModelToolSelection.Only(...)` or `ModelToolSelection.Edit(remove, add)`: the tools offered to the model, including deferred ones it loaded |
| `Instructions` | Text rendered after every extension section |
| `Cwd` | Working directory, passed to `HarnessOptions.Env` |
| `ServiceTier` | (experimental, unreleased) `auto`, `normal`, `economy`, `priority`, `fast` or `ultrafast`. A child conversation starts with its owner's. See [Service tiers](/docs/guides/service-tiers/). |

`SubmissionDraft.Input(content, whenBusy, requestId, entry)` makes an input. `whenBusy` decides what happens while the conversation is working: `WhenBusy.Steer` places it at the next boundary of the running turn, `WhenBusy.FollowUp` waits until the turn ends, and `WhenBusy.Reject` throws `ConversationBusyException`. A `requestId` deduplicates within the conversation, so a retried submit after a crash doesn't queue twice. `SubmissionDraft.Write(entry, requestId)` appends an entry without starting a turn.

`ISubmission.WaitAsync` returns a `SubmissionRecord` with `Status` (`Queued`, `Placed`, `Done`, `Unanswered`), `Type` (`Input` or `Write`), `Entry`, `Answer`, `Reason` and `Detail`.

## Extensions and tools

An `Extension` has a unique `Name` and any of `Tools`, `Sections`, `Hooks`, `Wraps` and `Tasks`. `registry.Install(extension)` replaces an installed extension of the same name in place, so you can reload code while the harness runs. `registry.Uninstall(name)` removes one. Tool names must be unique within an extension, and task names across the registry.

`Define` builds the parts:

| Method | Builds |
| --- | --- |
| `Define.Tool(registration)` | A tool. |
| `Define.Section(key, text or render, tag)` | A system prompt section. Keys match `^[a-z][a-z0-9_-]*$`; `instructions` is reserved. A render callback gets the conversation's agent and environment. |
| `Define.Hook(task, handlers)` | Hooks on a built-in task: `GenerationHooks` on `HarnessTasks.GenerationTask`, `ToolHooks` on `HarnessTasks.ToolTask`, `CompactionHooks` on `HarnessTasks.CompactionTask`. |
| `Define.WrapTool(tool, wrapper)` | Replaces whichever registration of that tool wins with a wrapped one. |
| `Define.WrapSection(key, wrapper)` | The same for a section. |
| `Define.Extension(extension)` | Returns the extension; useful when its tools refer to it. |

`ToolRegistration` describes a tool:

| Property | Meaning |
| --- | --- |
| `Name`, `Description` | Required. |
| `Parameters` | JSON Schema of the arguments, a `JsonObjectValue`. Default: an empty object schema. |
| `Execute` | Required. `Task<ToolExecutionResult> (JsonValue arguments, IToolExecutionApi api, DurableContext context)`. |
| `Replay` | `ToolReplay.Unsafe` (default) or `ToolReplay.Safe`. See [Replay after a crash](#replay-after-a-crash). |
| `ExecutionMode` | `Parallel` or `Sequential`. Default: the settings' `ToolExecution`. One sequential call makes its whole round sequential. |
| `PrepareArguments` | Repairs arguments models often get wrong, such as a JSON string where an array belongs, before validation. Must be pure. |
| `StructuredOutputSchema` | Schema of `StructuredOutput`, for programs that call the tool. |
| `Callers` | Who may call it: `ToolCaller.Model`, `ToolCaller.Tools` (through `ExecuteToolAsync`), or both (null, the default). |
| `Deferred` | Kept out of the prompt until the model loads it with `tool_search`. |
| `OutputLimits` | `MaxBytes` (50 KiB), `MaxLines` (2000) and `Retain` (`Head` or `Tail`). |

`ToolExecutionResult` has `Output` (content blocks as a `JsonArrayValue`, such as `{"type":"text","text":...}` and `{"type":"image","data":...,"mimeType":...}`), `StructuredOutput`, `IsError`, `Details`, `Diagnostics`, `Usage` and `Control`. A tool that throws produces an error result for the model.

`IToolExecutionApi` gives a running call its `TaskId`, `ConversationId`, `CallId`, the conversation's `AgentAsync`, `Models`, `Env`, streaming `Output`, `DetailsAsync`, `CommitAsync`, memos (`MemoAsync`), child tasks (`CreateTaskAsync`, `WaitForTaskAsync`) and nested tool calls (`ExecuteToolAsync`).

### Replay after a crash

Each tool call is a `pi.tool` task. Before it runs the tool, it commits an intent checkpoint that records the arguments and the tool's replay policy. If the process dies while the tool runs, the next open finds that checkpoint and decides:

- The call reruns only if the checkpoint recorded `safe` and the tool registered now is still `ToolReplay.Safe`. Its running output is cleared first.
- Otherwise the call settles as failed with the result "Tool *name* was interrupted and may have partially run", and the conversations the call owned are aborted. The model sees that result and decides what to do.

A child task of a tool that is not replay-safe is abandoned after a restart, because nothing will wait for it.

A replay-safe tool must be safe to run twice. Memos make that practical: `api.MemoAsync(name, candidate, context)` stores a value for this call unless one exists and returns the one that won, and `api.MemoAsync(name, context)` reads it. Store a key before the side effect and pass it to a service that ignores repeated keys:

```csharp
using PiSharp.Durable;
using PiSharp.Durable.Chord;

static ToolRegistration CreateChargeTool(IPaymentService payments) => Define.Tool(new ToolRegistration
{
    Name = "charge",
    Description = "Charge the customer's card.",
    Parameters = (JsonObjectValue)JsonValue.Parse("""{"type":"object","required":["cents"],"properties":{"cents":{"type":"number"}}}"""),
    Replay = ToolReplay.Safe,
    Execute = async (arguments, api, context) =>
    {
        // The first run stores a key. A rerun after a crash gets the same key back.
        var key = (JsonStringValue)await api.MemoAsync("idempotency-key", JsonValue.String(Guid.NewGuid().ToString("N")), context);
        var cents = ((JsonNumberValue)((JsonObjectValue)arguments)["cents"]).Value;
        var receipt = await payments.ChargeAsync(key.Value, cents, context.CancellationToken);
        return new ToolExecutionResult
        {
            Output = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", $"Charged. Receipt {receipt}.")])]),
        };
    },
});

public interface IPaymentService
{
    Task<string> ChargeAsync(string idempotencyKey, double cents, CancellationToken cancellationToken);
}
```

## Tasks

A task definition names its phases. Each phase handler does its work and commits the next checkpoint or the outcome through `runtime.CommitAsync`. The checkpoint's `phase` field picks the next handler. A crash between doing and committing reruns that phase, so make each phase safe to repeat, with a memo where needed.

```csharp
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;

var context = DurableContext.Background;

var report = Tasks.DefineTask(new TaskDefinition
{
    Name = "example.report",
    Version = 1,
    Initial = _ => JsonValue.Object([new("phase", "send")]),
    Phases = new Dictionary<string, TaskPhaseHandler>
    {
        ["send"] = async (task, runtime, taskContext) =>
        {
            // The memo records that the side effect happened, so a rerun skips it.
            if (await runtime.MemoAsync("sent", taskContext) is null)
            {
                await sendReport(taskContext.CancellationToken);
                await runtime.MemoAsync("sent", JsonValue.Boolean(true), taskContext);
            }
            await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Object([new("sent", true)])), taskContext);
        },
    },
    Abort = (_, runtime, taskContext) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), taskContext),
});

var registry = Registry.Create();
registry.Install(new Extension { Name = "reports", Tasks = [report] });
var harness = await Harness.OpenAsync(new MemoryStorage(), new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry }, context);
var root = await harness.RootAsync(context);

var id = await root.CommitAsync(tx => tx.CreateTaskAsync(report, JsonValue.Object([new("to", "ops")]),
    new TaskOptions(TaskOwnership.Conversation)), context);
var done = await harness.WaitForTaskAsync(id, context);
Console.WriteLine((done.State as SettlingTaskState)?.Outcome.ToJson());
await harness.CloseAsync(context);
```

`sendReport` stands for your side effect. A crash after the send and before the memo sends again, so that step must still tolerate a repeat. `NextTaskState` also has `Running(checkpoint)`, `Waiting(checkpoint, on, policy)` to wait for child tasks, and `Failed(message, detail)`. `harness.AbortTaskAsync(id)` runs the `Abort` handler instead of the phases.

## Inbox and task graph

Messages queued while a conversation works live in its `pi.inbox` document, `Inbox.InboxDoc`. Its shape is `{"items": [...]}`, where an item is `{id, mode, content, entry?}` with `mode` `steer` or `followUp`, or `{id, mode: "write", entry}`. Read it with `harness.SnapshotAsync(Inbox.InboxDoc, conversation.Id, context)`, or watch it with `WatchDocAsync`.

The other built-in documents are `Live.LiveDoc` (`pi.live`: the partial answer and running tools), `Agents.AgentDoc` (`pi.agent`: the stored choices), `UsageLedger.UsageDoc` (`pi.usage`) and `ProviderIdentity.ProviderDoc` (`pi.provider`).

The task graph shows every live task and who owns it:

```csharp
using var graph = await harness.TaskGraphAsync(context);
foreach (var (id, node) in graph.Value.Tasks)
    Console.WriteLine($"{id.Value} {node.Kind} {node.State.Status} {node.State.Phase} owner={node.Owner?.Value}");
```

`graph.Subscribe(...)` delivers each new graph, and `TaskGraph.ToJson()` prints it as `{"tasks": {...}}`. `pisharp durable`'s `/tasks` panel is built on this.

## Storage

| Storage | Open with | Notes |
| --- | --- | --- |
| `MemoryStorage` | `new MemoryStorage()` | Nothing survives the process. For tests and samples. |
| `JsonlStorage` | `await JsonlStorage.OpenAsync(directory, context, options)` | Append-only JSONL files. `JsonlStorageOptions.Fsync` controls flushing. Keeps the records it wrote in memory, so long sessions grow; see [known issues](/docs/guides/durable/#known-issues). |
| `SqliteStorage` | `await SqliteStorage.OpenAsync(path, options)` | One database file. `SqliteStorageOptions` has `WalAutoCheckpointPages` and `BusyTimeoutMs`. What `pisharp durable` uses. |

The formats are upstream's, so Pi's `pi-durable` v1.2.0 and PiSharp read each other's storage. To survive a crash, open the same file again:

```csharp
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;

var context = DurableContext.Background;
var path = Path.Combine(AppContext.BaseDirectory, "session.sqlite");

var harness = await Harness.OpenAsync(await SqliteStorage.OpenAsync(path), options, context);
var root = await harness.RootAsync(context, new AgentChange { Model = new ModelRef("openai", "gpt-6-sol") });
// ... work, crash, or close.
await harness.CloseAsync(context);

// The next process opens the same file. Unfinished turns and tasks continue.
var reopened = await Harness.OpenAsync(await SqliteStorage.OpenAsync(path), options, context);
reopened.Resume();
await reopened.WaitForIdleAsync(context);
await reopened.CloseAsync(context);
```

Here `options` is your `HarnessOptions`. Keep one writer per storage: the storage classes take no lock across processes. `pisharp durable` adds its own lock directory next to each session.

## Built-in tools

`PiSharp.Durable.Tools` has the tools `pisharp durable` uses:

| Type | What it gives you |
| --- | --- |
| `CodingTools` | `CodingTools.Extension` (named `coding-tools`: `read`, `write`, `edit`, `bash`, none replay-safe), `CreateCodingTools(ReadToolOptions)` to process images from `read`, and `CreateReadTool`, `CreateWriteTool`, `CreateEditTool`, `CreateBashTool(BashToolOptions)` and `CreatePowerShellTool(PowerShellToolOptions)` one by one. They work through `HarnessOptions.Env` and fail without one. |
| `LocalExecutionEnv` | An `IExecutionEnv` over the local file system and shell: `new LocalExecutionEnv(cwd)`, or `LocalExecutionEnvOptions` with `Cwd`, `ShellPath`, `ShellEnv` and `Watch`. On Windows the shell is Git Bash. |
| `ToolSearch` | `ToolSearch.CreateExtension()` (named `tool-search`) adds `tool_search`, which searches the conversation's deferred tools and loads the matches. Replay-safe. |
| `DurableCodemode` | `DurableCodemode.CreateExtension(new CodemodeToolOptions { Sandbox = ... })` adds the `codemode` tool, which runs JavaScript that calls tools. It replays deterministically after a crash, and refuses when the engine, module version or script changed. See [Code mode](/docs/embed/codemode/) for the sandbox. |
| `MediaTools` | (experimental, unreleased) Image, video, speech, transcription, music, embedding, rerank and decision tools. See [Media library](/docs/embed/media/#media-tools-in-your-own-harness). |

A coding agent in a few lines:

```csharp
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;

var registry = Registry.Create();
registry.Install(CodingTools.Extension);
registry.Install(new Extension { Name = "prompt", Sections = [Define.Section("preamble", "You are a careful coding agent.", tag: false)] });

var options = new HarnessOptions
{
    Models = models,
    Registry = registry,
    Env = (target, _) => Task.FromResult<IExecutionEnv?>(new LocalExecutionEnv(target.Cwd ?? Directory.GetCurrentDirectory())),
};
var harness = await Harness.OpenAsync(new MemoryStorage(), options, DurableContext.Background);
var root = await harness.RootAsync(DurableContext.Background, new AgentChange
{
    Model = new ModelRef("openai", "gpt-6-sol"),
    Cwd = Directory.GetCurrentDirectory(),
});
```

## Testing

`PiSharp.Durable.Testing` holds upstream's conformance kits, so you can check your own storage or execution environment against the same cases PiSharp's do:

| Type | Use |
| --- | --- |
| `StorageConformance.Create(provider)` | The storage cases. The provider hands out a storage per case: `Task StorageConformanceProvider(Func<IStorage, Task> use)`. A failure throws `StorageConformanceException`. |
| `EnvConformance.Create(provider or options)` | The execution environment cases. `EnvConformanceOptions` has `WithEnv`, `Assertions`, `Shell` (default `sh -c`), `Symlinks` (default true) and `ListFiles` (default false). Some cases set `TimeoutMs`. |
| `ConformanceRunner` | `RegisterStorageConformance` and `RegisterEnvConformance` register the cases with an `IConformanceTestRunner` (`Describe`, `It`, `Assertions`) for runners shaped like upstream's. |
| `ConformanceAssertions.Create(failure)` | Assertions that throw your runner's exception. |
| `StorageBenchmark` | Upstream's storage benchmarks. |

Each case has a `Name`, a `Slug` for test ids and a `Run` function. Run them from any test framework:

```csharp
using PiSharp.Durable.Storage;
using PiSharp.Durable.Testing;

foreach (var testCase in StorageConformance.Create(use => use(new MemoryStorage())))
{
    Console.WriteLine(testCase.Slug);
    await testCase.Run();
}
```

To test your own agent, script the model as in [A first agent](#a-first-agent), close the harness partway through, open the same storage again and check what it finishes. PiSharp's crash tests go further and kill a real process at checkpoints.

## Samples

[`samples/Durable.Examples`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Examples) ports upstream's examples. They run offline on scripted models, except 16. Run all of them, or pick by number or name:

```powershell
dotnet run --project samples/Durable.Examples -- 14 20
dotnet run --project samples/Durable.Examples -- --storage sqlite --dir ./examples-out 13-recovery
```

| Example | Shows |
| --- | --- |
| `00-conversation` | A session stores conversations; commits are transactions. |
| `01-documents` | A rewindable document next to the entries, read now and as of an entry. |
| `02-forks` | Fork a conversation at an entry; documents follow their fork setting. |
| `03-owned-conversations` | A background task that owns a child conversation, created in one commit. |
| `04-chord-state` | A document exposed as read-only replicated state. |
| `05-watches` | A serialized async watch of a document. |
| `06-harness` | Open a harness with a registry; create the root conversation. |
| `07-configuration` | Per-conversation agent choices and live harness settings. |
| `08-harness-conversations` | Conversation handles, typed entries, creating and forking conversations. |
| `09-context` | Transcript history and model context: heads, edits, synthesized results. |
| `10-registry-reload` | Reload an extension by reinstalling it; wrap and uninstall. |
| `11-extension-state` | An extension with its own per-conversation document. |
| `12-tasks` | A durable task state machine with phases and abort. |
| `13-recovery` | Close and reopen; the task continues and a memo prevents a duplicate effect. |
| `14-chat` | A chat turn on a scripted model. |
| `15-system-prompt` | Sections, wrapped sections, per-conversation instructions, an environment that follows the cwd. |
| `16-real-model` | Stream from OpenAI through `NativeDurableModels` (needs `OPENAI_API_KEY`). |
| `17-coding-tools` | The coding tools over `LocalExecutionEnv`, with tool hooks timing a call. |
| `18-print` | Print mode: one prompt, one answer, like `pi -p`. |
| `19-json` | JSON mode: agent events or raw view frames as JSON lines. |
| `20-inbox` | Submissions while busy: follow-up, steer, write, withdraw, reject. |
| `21-late-join` | A client that attaches mid-run gets the current state, then changes. |
| `22-subagent-foreground` | A subagent tool, and a UI attaching to the child. |
| `23-subagent-background` | Persistent background subagents that survive a restart. |
| `24-child-tasks` | A task that owns child tasks, and the task graph. |
| `25-compaction` | A long chat summarized to keep the context small. |
| `26-coding-agent` | A small coding agent with settings backed by a file. |
| `27-plan-mode` | A read-only planning mode that restores the tools when it ends. |
| `28-reviewer` | A reviewer agent on a cheaper model beside the main one. |
| `29-sandbox-per-conversation` | A sandbox per conversation, recorded in a document. |
| `30-tool-override` | Replace `bash` for some conversations, and wrap whichever `bash` wins. |
| `31-reload-and-restart` | Reload an extension while a call runs, then restart. |

[`samples/Durable.Chat`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Chat) sends one prompt to a real model, prints the answer, then reopens the storage and prints the stored answer. Its API key comes from the provider's environment variable:

```powershell
dotnet run --project samples/Durable.Chat -- --provider anthropic --model claude-sonnet-5 "Name three prime numbers"
```

Its options are `--provider`, `--model`, `--thinking <level>`, `--storage memory|jsonl|sqlite` (default sqlite), `--dir <path>`, `--continue` to reopen `--dir`, and `--tools [--cwd <path>]` to give the model `read`, `write`, `edit` and `bash`.

Upstream's [`pi-durable` README](https://github.com/earendil-works/pi/blob/v1.2.0/packages/durable/README.md) and [specification](https://github.com/earendil-works/pi/blob/v1.2.0/packages/durable/docs/spec.md) describe the design in depth.
