---
title: Durable library
description: "Build crash-safe agents on PiSharp.Durable: the harness, storage, tools and tests."
---

`PiSharp.Durable` is the library behind [`pisharp durable`](/docs/guides/durable/). It is a port of Pi's `pi-durable` v1.5.0 package: an agent harness that commits every model turn, streamed partial, tool call and queued message to storage before anything sees it. Open the same storage after a crash and the harness finishes what was running. This page shows how to build your own agent on it.

:::caution[Experimental]
The durable libraries ship as experimental from PiSharp 1.2.0.1, the members marked "(from 1.2.0.2)" from 1.2.0.2, and those marked "(from 1.5.0)" from 1.5.0. Every public type carries `[Experimental("PISHARP_DURABLE001")]`, and the API may change or be removed in any release without notice. PiSharp 1.5.0 follows `pi-durable` v1.5.0 and breaks code and storage written for 1.2.0.x: see [Upgrading from 1.2.0.x](#upgrading-from-120x).
:::

## Install

Add the packages and suppress the experimental diagnostic:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <NoWarn>$(NoWarn);PISHARP_DURABLE001</NoWarn>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="PiSharp.Durable" Version="1.5.2" />
  <PackageReference Include="PiSharp.Durable.Sqlite" Version="1.5.2" />
  <PackageReference Include="PiSharp.Durable.Tools" Version="1.5.2" />
</ItemGroup>
```

| Package | Namespaces | Contents |
| --- | --- | --- |
| `PiSharp.Durable` | `PiSharp.Durable`, `PiSharp.Durable.Chord`, `PiSharp.Durable.Storage`, `PiSharp.Durable.Storage.Jsonl`, `PiSharp.Durable.Env`, `PiSharp.Durable.Testing` | Sessions, the harness, tasks, JSON values, memory and JSONL storage, the execution environment contract, conformance kits |
| `PiSharp.Durable.Sqlite` | `PiSharp.Durable.Sqlite` | SQLite storage over `Microsoft.Data.Sqlite` |
| `PiSharp.Durable.Tools` | `PiSharp.Durable.Tools`, `PiSharp.Durable.Tools.Env` | The local execution environment, the coding tools, `tool_search`, durable code mode, and, from 1.2.0.2, the media tools |

The harness's telemetry types come from `PiSharp.Telemetry`, which `PiSharp.Durable` references. PiSharp **1.5.0.1** includes [Chord and native hosting](/docs/embed/chord/) and [PiSharp.Durable.Store](/docs/embed/durable-store/). Use version 1.5.2 for every PiSharp sibling package.

## Upgrading from 1.2.0.x

PiSharp 1.5.0 follows `pi-durable` v1.5.0, which changed the API and the stored formats:

- **Getters became watches.** `ISession.DocumentStateAsync`, `IConversation.ViewStateAsync` and `IHarness.TaskGraphAsync` are gone; use `WatchDocAsync`, `IConversation.WatchAsync` and `IHarness.WatchTaskGraphAsync`, whose `Value` is the current value. See [Watching and observing](#watching-and-observing).
- **Agent events are gone.** Upstream removed `watchEvents` and its event types. A renderer renders the conversation view instead. PiSharp keeps a projection of the v1.2.0 events inside the CLI's device host (`PiSharp.Durable.Hosting`, not a package) for its wire format; libraries don't get it.
- **Watch listeners take tagged calls.** A listener gets a `WatchCall<T>` (a `WatchValueCall<T>` with `Value`, `Ops` and `Retired`, or a `WatchFailedCall<T>`) and a context. A document watch follows its address, so a retirement no longer ends it.
- **Renamed members.** `SubscribeCommits` is `ObserveCommits(CommitObserver)`; `SessionEnd` has a `Kind` (`Closed` or `Failed`, with its `Error`); a document change in a commit is tagged `change` or `retire`; `OnReport` takes one `Report` tagged by kind (see [Harness options](#harness-options)); `TaskInspectionKind.Ready` is `Pending`.
- **Every document definition states `checkpointWhen`.** `Documents.DefineConversationDoc` and the other `Define*Doc` methods take a required `DocumentCheckpointPredicate`, before the optional migration. Pass `(_, _, _) => false` to keep ordinary changes as deltas.
- **Custom storage commits batches.** `IStorage.CommitAsync(batches, receive, context)` stores several commits in one call and returns a `CommitResult`. `StorageConformance` checks the contract; `CommitWritesAsync` in `PiSharp.Durable.Testing` commits one batch.
- **Stored formats moved on, with no way back.** Opening a 1.2.0.x SQLite file migrates it to schema version 2, which PiSharp 1.2.0.x refuses to open. Tasks and documents the new version writes (`pi.tool` version 3, `pi.generation` version 2, `pi.live` version 3) are too new for 1.2.0.x, which leaves such tasks blocked (`task_too_old`). Keep a copy of a session before you open it with 1.5.0 if you may need to go back.
- **One-time effects.** The built-in tools are declared with a different key order, so the next turn of a 1.2.0.x conversation records their declarations once more. A model request that was in flight when a 1.2.0.x process died counts as a failed attempt and waits for the retry backoff, instead of being sent again.
- **Streamed progress is deferred.** Partial answers and tool output are stored about once a second, so a crash loses up to about a second of streamed text. `ProgressSettings.PersistWithinMs = 0` stores each one at once.
- **The examples were renumbered** to upstream v1.5.0's names (see [Samples](#samples)).

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
The custom agents library has a ready-made scripted model, `FauxAgentModels` in `PiSharp.CustomAgents.Testing` (experimental). See [Custom agents](/docs/custom-agents/overview/).
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
| `Models` | The model access generation uses. Set it or `CurrentModels`. |
| `CurrentModels` | (from 1.5.0) A function returning the current model access, called at each use, so you can replace your model configuration without reopening the harness. |
| `Identity` | (from 1.5.0) The `ClientIdentity` of every model request through the harness, tools and hooks included. PiSharp's default sends no attribution. |
| `Registry` | Required. The registry (an `IRegistryReader`; `Registry.Create()` returns one); it may keep changing while the harness runs. |
| `Settings` | Harness-wide run policy (`HarnessSettings`). |
| `CurrentSettings` | (from 1.5.0) A function returning the current `HarnessSettings`, called at each use. Set it or `Settings`. |
| `Env` | `Func<EnvTarget, DurableContext, Task<IExecutionEnv?>>`: builds a conversation's execution environment at each use. `EnvTarget` carries the conversation id, its agent's `Cwd` and a document reader. Without it, tools that need files or a shell fail with an error result. |
| `ConversationCreated` | Runs in every commit that creates or forks a conversation, after the built-in documents exist. |
| `Now` | The harness clock in milliseconds. Sleeping tasks store their deadlines on it, so every process that opens one storage must use the same clock. |
| `TimeProvider` | The default clock and timers. Default `TimeProvider.System`. |
| `SetTimer` | (from 1.5.0) Arms every timer of the session, the scheduler and its tasks: progress pacing, freeing idle memory and, without `OnWake`, resuming sleeping tasks. Default: `TimeProvider`'s timers. See [Sleeping and waking](#sleeping-and-waking). |
| `OnWake` | (from 1.5.0) For a host that wakes its process itself: the harness then arms no timer for sleeps and calls this with `Wake()` whenever it changes. |
| `Telemetry` | (from 1.5.0) A host-owned `ITelemetryContext` for the harness's spans, events and metrics. See [Telemetry](#telemetry). |
| `OnReport` | Receives failures that don't fail the calling operation, such as a throwing hook or a faulted task, and the error that fails the session. From 1.5.0 it gets one `Report`: the `Error` and its `Kind` (`StorageFailed`, `TaskFaulted`, `TaskInterrupted`, `MigrationFailed`, `TaskOutdated`, `TaskError`, `ExtensionError`, `HostError` or `ListenerError`), with the `Task`, `Extension`, `ConversationId`, `Callback`, `Reason` or `Restarts` where they apply. |
| `ReadOnly` | (from 1.5.0) Opens a viewer of a session another process drives: nothing is recovered, every commit with writes throws `ReadOnlySessionException`, and nothing runs. |

`HarnessSettings` is read at every use and never copied, so you can derive from it and override getters to follow a settings file. A null property takes the default:

| `HarnessSettings` | Meaning and default |
| --- | --- |
| `Extensions` | Every installed extension, in install order. |
| `Stream` | `ConversationStreamOptions`: `Transport`, `TimeoutMs`, `MaxRetries`, `MaxRetryDelayMs`, `Headers`, `Metadata`, `CacheRetention`, `Deferred`. |
| `Retry` | `RetrySettings`: enabled, 3 retries, 2000 ms base delay, 60000 ms longest delay. |
| `Compaction` | `CompactionSettings`: enabled, `ReserveTokens` 16384, `KeepRecentTokens` 20000, `BackgroundTokens` 32768 (0 turns background compaction off). From 1.5.0, `ModelOverrides`, keyed by `"provider/modelId"`, replaces these fields for one model. |
| `Progress` | `ProgressSettings`: commit partial answers and tool output at most every 100 ms (`PartialIntervalMs`, `OutputIntervalMs`), and store them within 1000 ms (`PersistWithinMs`, from 1.5.0; 0 stores each at once). See [Deferred commits](#deferred-commits). |
| `ToolExecution` | `ToolExecutionMode.Parallel`. |
| `SteeringMode`, `FollowUpMode` | `QueueMode.OneAtATime`. |
| `ContextRetentionMs` | 600000: how long an idle conversation keeps its last context in memory. |
| `DocumentRetentionMs` | Ten minutes: how long an unobserved document stays loaded. |
| `AfterFailedRun` | (from 1.5.0) `Keep`: what a run that fails (a model error, no model, its turn limit, crashes) does with the inputs queued behind it. `Continue` starts the next run with them, `Withdraw` settles them unanswered. |
| `RestartLimit` | (from 1.5.0) 5: how many times in a row a crash, eviction or reset may cut a task off before it ends instead of running again. `double.PositiveInfinity` never ends one. |

## The harness

`IHarness` extends `ISession` with:

| Member | What it does |
| --- | --- |
| `Resume()` | Starts task scheduling. Idempotent. Sleeping tasks that are due become runnable, so a host's wakeup calls it. |
| `Wake()` | (from 1.5.0) When the session next needs a running process: `Active` while some task can run or runs, `At` for the earliest sleep. |
| `RootAsync(context, agent, init)` | The root conversation (id 1), created on the first call. |
| `ConversationAsync(id, context)` | A conversation by id, or null. |
| `CreateConversationAsync(options, context)` | Creates a conversation. `ConversationCreateOptions(ownership)` takes an `Agent` change and an `Init` callback. |
| `GetTaskAsync`, `WaitForTaskAsync`, `AbortTaskAsync` | Read, await or abort a task by id. |
| `SubmissionAsync`, `AbortSubmissionAsync` | A submission by id, or abort one. |
| `WaitForIdleAsync(context)` | Waits until no task is runnable. |
| `UsageAsync(context)` | The summed token usage of every conversation. |
| `WatchTaskGraphAsync` | A watch of the live task graph (see [Inbox and task graph](#inbox-and-task-graph)). `TaskGraphAsync` is gone from 1.5.0: read the watch's `Value`. |
| `ObserveConversationAsync`, `ObserveTaskGraphAsync` | (from 1.5.0) Synchronous observers of a conversation's view and of the task graph. See [Watching and observing](#watching-and-observing). |
| `InspectAsync(context)` | A diagnostic snapshot of tasks and scheduling. |

From `ISession` it has `CommitAsync` (four overloads over an `ITx`, each with optional `CommitOptions`), `SnapshotAsync` and `WatchDocAsync` for documents, `ObserveDocAsync`, `ObserveCommits` (a `CommitObserver` of stored commits), `SubscribeClose`, `Closed` (a `SessionEnd` of kind `Closed` or `Failed`) and `CloseAsync`. `Session.Create(storage)` gives you a bare session without agents; examples 00 to 04 use one.

## Conversations

`IConversation` is one transcript:

| Member | What it does |
| --- | --- |
| `Id` | The conversation id. |
| `AgentAsync(context)` | The agent choices resolved against the registry: model, thinking level, extensions, tools, sections, instructions, cwd. |
| `ConfigureAsync(change, context)` | Applies an `AgentChange`. |
| `SubmitAsync(draft, context)` | Queues an input or a passive write; returns an `ISubmission`. |
| `SubmitAsync(tx, draft)` | (from 1.5.0) Admits a submission inside your own commit, with the same rules, and returns its id. |
| `CompactAsync(instructions, context)` | Starts a manual compaction task. |
| `ResetAsync(handoff, context)` | Starts the context over from a reset entry. |
| `CommitAsync(...)` | A transaction scoped to this conversation. |
| `ContextAsync(context, at)` | The transcript and the model context at the head or at an entry. |
| `EntriesAsync(query, limit, cursor, context)` | A page of the raw history, which compaction never changes. |
| `ForkAsync(at, options, context)` | A new conversation that shares history up to an entry. |
| `AbortAsync`, `WaitForIdleAsync` | Abort the running turn, or wait until the conversation is idle. |
| `WatchAsync(context, window)` | A watch of the conversation's view for a UI: a window of its newest entries and its documents. `ViewStateAsync` is gone from 1.5.0. |

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
| `ServiceTier` | (from 1.2.0.2) `auto`, `normal`, `economy`, `priority`, `fast` or `ultrafast`. A child conversation starts with its owner's. See [Service tiers](/docs/guides/service-tiers/). |
| `MaxTurns` | (from 1.5.0) The tool rounds one run may take, an integer of 1 or more. The round that reaches it runs its tools, then the run ends unanswered and `pi.live.runFailure` says `turn_limit`. Task-owned children and forks inherit it. |

`SubmissionDraft.Input(content, whenBusy, requestId, entry)` makes an input. `whenBusy` decides what happens while the conversation is working: `WhenBusy.Steer` places it at the next boundary of the running turn, `WhenBusy.FollowUp` waits until the turn ends, and `WhenBusy.Reject` throws `ConversationBusyException`. A `requestId` deduplicates within the conversation, so a retried submit after a crash doesn't queue twice. `SubmissionDraft.Write(entry, requestId)` appends an entry without starting a turn. From 1.5.0, `SubmissionDraft.Continue(requestId)` starts a run on the context as it is, without a user message, on an idle conversation only, and an input with `whenIdle: WhenIdle.Reject` throws `ConversationIdleException` instead of starting a run on an idle conversation.

When a run's newest turn ends without an answer, `pi.live` keeps why in `runFailure`: `model_error`, `no_model`, `aborted`, `reset`, `faulted`, `orphaned`, `interrupted`, `restart_limit`, `nothing_to_answer` or `turn_limit`, with the run's task and inputs.

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

From 1.5.0 a crash loop ends: open counts each task a crash, eviction or reset cut off, and once a task reaches `HarnessSettings.RestartLimit` it ends instead of running again (a tool call with an error result, a run with `runFailure` reason `restart_limit`, a task of your own `faulted`), reported to `OnReport`. A model request the process died during is a failed attempt under the retry settings, not a request sent again.

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

`sendReport` stands for your side effect. A crash after the send and before the memo sends again, so that step must still tolerate a repeat. `NextTaskState` also has `Running(checkpoint)`, `Waiting(checkpoint, on, policy)` to wait for child tasks, `Sleeping(checkpoint, until)` and `Failed(message, detail)`. `harness.AbortTaskAsync(id)` runs the `Abort` handler instead of the phases.

### Sleeping and waking

From 1.5.0 a task waits for time by ending its phase asleep. The sleep is stored, so the task holds nothing while it sleeps, survives restarts and resumes at the checkpoint once the harness clock reaches `until`:

```csharp
["wait"] = (task, runtime, taskContext) =>
    runtime.SleepUntilAsync(runtime.Now() + 60_000, JsonValue.Object([new("phase", "deliver")]), taskContext),
```

There is no sleep in the middle of a phase: a phase reruns from its top after a restart, so put the work after the wait in the next phase. `NextTaskState.Sleeping(checkpoint, until)` from `runtime.CommitAsync` is the same sleep, committed with other writes. The built-in retry backoffs and deferred-response polls sleep this way too, and `ITaskRuntime.SetTimer` arms a timer that ends with the invocation.

The harness's timers go through `HarnessOptions.SetTimer` and its times through `Now`, so a fake clock and fake timers drive a whole harness in tests. While scheduling runs, a timer resumes sleeping tasks when they are due. A host whose process can be stopped wakes it itself instead: with `OnWake`, the harness arms no timer for sleeps and tells the host what `Wake()` says whenever that changes. The host keeps its own wakeup armed for `At`, and while `Active`, and calls `Resume()` when it fires.

### Work at the end of a run

From 1.5.0, `GenerationHooks.AfterRun` runs after a run answered and returns tasks for the end of the run, built with `Define.RunEndTask(task, input)`. The answer is committed first; the run stays busy until the tasks end, so input sent meanwhile waits for them too. The hook gets a `RunSummary` (`Reason`, `First`, `Answer`, `Last`), and `api.EntriesAsync` reads the run's own entries between `First` and `Last` from the raw history, which a compaction never changes:

```csharp
AfterRun = async (run, api, hookContext) =>
{
    var range = new EntryQueryBounds { MinEntryId = run.First, MaxEntryId = run.Last, Order = ScanOrder.Ascending };
    var page = await api.EntriesAsync(range, 1000, null, hookContext);
    var edited = page.Items.Any(entry => entry.Kind == "pi.tool-result");
    return edited ? [Define.RunEndTask(runTests, JsonValue.Object([new("attempt", 1)]))] : null;
},
```

`IHookApi.EntryAsync` and `ITaskRuntime.EntriesAsync` read the same history. A tool round whose result asks for a handoff no longer ends the run: the reset entry is written and the same run continues in the new context, so the submission settles with the eventual answer. Example `32-tests-after-edits` runs a project's tests after every run that edited files.

## Your own documents

`Documents.DefineConversationDoc(kind, version, fork, initial, checkpointWhen, migrate)` and the other `Define*Doc` methods define a document. From 1.5.0 `checkpointWhen` is required: it returns true to store a change as a complete value instead of a delta. `(_, _, _) => false` keeps changes as deltas, which suits a document that retires with its task.

```csharp
static readonly ConversationDocToken Notes = Documents.DefineConversationDoc("example.notes", 1, DocumentFork.Current,
    () => JsonValue.Object([new("text", "")]), checkpointWhen: (_, _, _) => false);
```

## Inbox and task graph

Messages queued while a conversation works live in its `pi.inbox` document, `Inbox.InboxDoc`. Its shape is `{"items": [...]}`, where an item is `{id, mode, content, entry?}` with `mode` `steer` or `followUp`, or `{id, mode: "write", entry}`. Read it with `harness.SnapshotAsync(Inbox.InboxDoc, conversation.Id, context)`, or watch it with `WatchDocAsync`.

The other built-in documents are `Live.LiveDoc` (`pi.live`: the partial answer and running tools), `Agents.AgentDoc` (`pi.agent`: the stored choices), `UsageLedger.UsageDoc` (`pi.usage`) and `ProviderIdentity.ProviderDoc` (`pi.provider`).

The task graph shows every live task and who owns it:

```csharp
var graph = await harness.WatchTaskGraphAsync(context);
foreach (var (id, node) in graph.Value.Tasks)
    Console.WriteLine($"{id.Value} {node.Kind} {node.State.Status} {node.State.Phase} owner={node.Owner?.Value} blocked={node.Blocked}");
await graph.StopAsync();
```

`graph.Start(...)` delivers each new graph, and `TaskGraph.ToJson()` prints it as `{"tasks": {...}}`. A node's `Blocked` (from 1.5.0) says why a live task cannot run: `MissingTask`, `TaskTooOld` or `MigrationFailed`. `pisharp durable`'s `/tasks` panel is built on this.

## Watching and observing

A watch (`IWatchHandle<T>`) has the current `Value` and one listener, which you install with `Start`. Calls never overlap: commits made while the listener runs arrive in the next call, folded, with the newest value and the operations that turn the previous value into it. `StopAsync` ends a watch, and `Closed` completes when it ends.

```csharp
var notes = await harness.WatchDocAsync(Notes, root.Id, context);
notes?.Start(async (call, _) =>
{
    if (call is WatchValueCall<JsonObjectValue> change)
        Console.WriteLine($"{change.Value.ToJson()} retired={change.Retired}");
});
```

From 1.5.0:

- **A document watch follows its address** (kind, scope and key), not one document. A retirement arrives as a call with `Retired` set and the last value, and a document created there later arrives in a later call.
- **A conversation's view is a window.** `IConversation.WatchAsync(context, window)` starts with the newest 50 entries (`ConversationWindow.Min`), takes appends, and drops its oldest entries in one step once it holds more than `Max`. `ConversationTranscript` merges every value of such a watch into what a client has, marks gaps, and loads earlier pages on demand. Example `31-transcript` shows it.
- **Observers** see every change, unfolded, synchronously at its publication: `ObserveDocAsync`, `ObserveConversationAsync` and `ObserveTaskGraphAsync` take an `Observer<T>` (an advance callback and an end callback) and return an `Observation<T>` with the value at attachment and `Detach()`. An observer must not block or call the session. `ObserveCommits(CommitObserver)` observes stored commits the same way.
- **There are no agent events.** Render the conversation view; its operations say what changed.

## Deferred commits

From 1.5.0 a commit can wait in memory: `CommitAsync(..., new CommitOptions(CommitPersistence.Deferred(withinMs)))`. A deferred commit that only changes existing conversation documents is adopted in memory at once, so watches and observers see it, and stored with the next commit that reads or writes those documents, at close, or within `withinMs` (default `ProgressSettings.PersistWithinMs`, 1000 ms). Any other commit is stored at once. A crash loses what is held, so defer only state whose loss is acceptable. The harness defers its own progress commits: streamed partial answers and running tool output.

## Storage

| Storage | Open with | Notes |
| --- | --- | --- |
| `MemoryStorage` | `new MemoryStorage()` | Nothing survives the process. For tests and samples. |
| `JsonlStorage` | `await JsonlStorage.OpenAsync(directory, context, options)` | Append-only JSONL files. `JsonlStorageOptions.Fsync` controls flushing. Keeps the records it wrote in memory, so long sessions grow; see [known issues](/docs/guides/durable/#known-issues). |
| `SqliteStorage` | `await SqliteStorage.OpenAsync(path, options)` | One database file. `SqliteStorageOptions` has `WalAutoCheckpointPages`, `BusyTimeoutMs` and, from 1.5.0, `Prefix`, which prefixes every table and index name so the durable tables can share a database with your own. What `pisharp durable` uses. |

The formats follow upstream's. PiSharp's tests open, resume and rewrite sessions that Pi's `pi-durable` v1.2.0 wrote, comparing with v1.5.0's forms of those records; no session written by `pi-durable` v1.5.0 has been compared yet. SQLite files are at schema version 2 from 1.5.0, which adds the sleeping task status; opening an older file migrates it, and there is no way back (see [Upgrading from 1.2.0.x](#upgrading-from-120x)). To survive a crash, open the same file again:

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

From 1.5.0 a session no longer waits for each commit to be stored before it starts the next one: commits that arrive while a storage call is in flight go out together in the next call (group commit), each with its own sequence number and outcome, published in order. Work runs on while earlier commits are being stored, until it touches something one of them wrote. The session also keeps recently read records, the newest entry of each conversation and the documents of live tasks in memory, so an ordinary agent run reads little from storage. [durable-benchmarks.md](https://github.com/jkelly/PiSharp/blob/main/docs/durable-benchmarks.md) has PiSharp's own throughput numbers, measured on one machine.

## Built-in tools

`PiSharp.Durable.Tools` has the tools `pisharp durable` uses:

| Type | What it gives you |
| --- | --- |
| `CodingTools` | `CodingTools.Extension` (named `coding-tools`: `read`, `write`, `edit`, `bash`, none replay-safe), `CreateCodingTools(ReadToolOptions)` to process images from `read`, and `CreateReadTool`, `CreateWriteTool`, `CreateEditTool`, `CreateBashTool(BashToolOptions)` and `CreatePowerShellTool(PowerShellToolOptions)` one by one. They work through `HarnessOptions.Env` and fail without one. |
| `LocalExecutionEnv` | An `IExecutionEnv` over the local file system and shell: `new LocalExecutionEnv(cwd)`, or `LocalExecutionEnvOptions` with `Cwd`, `ShellPath`, `ShellEnv` and `Watch`. On Windows the shell is Git Bash. |
| `ToolSearch` | `ToolSearch.CreateExtension()` (named `tool-search`) adds `tool_search`, which searches the conversation's deferred tools and loads the matches. Replay-safe. |
| `DurableCodemode` | `DurableCodemode.CreateExtension(new CodemodeToolOptions { Sandbox = ... })` adds the `codemode` tool, which runs JavaScript that calls tools. It replays deterministically after a crash, and refuses when the engine, module version or script changed. See [Code mode](/docs/embed/codemode/) for the sandbox. |
| `MediaTools` | (from 1.2.0.2) Image, video, speech, transcription, music, embedding, rerank and decision tools. See [Media library](/docs/embed/media/#media-tools-in-your-own-harness). |

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
| `CommitWritesAsync` | (from 1.5.0) An extension that commits one batch through `IStorage.CommitAsync`, for tests of a storage. |

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

To test your own agent, script the model as in [A first agent](#a-first-agent), close the harness partway through, open the same storage again and check what it finishes. Pass `Now` and `SetTimer` to run it on a fake clock. PiSharp's crash tests go further and kill a real process at checkpoints.

## Telemetry

From 1.5.0, `HarnessOptions.Telemetry` takes a host-owned `ITelemetryContext` from `PiSharp.Telemetry`, with optional propagation and metrics; without one, nothing is recorded. `DurableTelemetry.Schema` lists its typed spans, events and metrics. Attributes hold record IDs and fixed categories, never messages, arguments or results; the IDs point to the transcript. Every async call takes a `DurableContext`, which carries cancellation and its current span (`DurableContext.Background` never cancels and records nothing).

- **`pi.durable.submit`** covers a public submission and its commit. For `SubmitAsync(tx, draft)` it covers the admission only: its result is provisional until your commit is stored, and the commit's events go to your commit's context. Each input starts a new trace linked to its caller's current span, and a write is a child of its caller. A duplicate `RequestId` keeps the original submission and its carrier.
- **`pi.durable.phase`** covers one run or abort phase of a task, with its decision commit; never the next phase, a durable wait or a restart. It names the task, the phase, the attempt, whether it resumed and its outcome, and for a tool phase the tool and call. Nested tasks and background compactions inherit the phase that created them, and model requests get its context. Hooks add no spans.
- **Failures.** A faulted, failed or orphaned outcome sets error status with `task_faulted`, `task_failed` or `task_orphaned` and the outcome's diagnostic, cut to 500 characters. Diagnostics can contain caller content; no exception detail is recorded. An aborted outcome keeps status ok.
- **Reports.** Every `OnReport` report also becomes a `pi.durable.report` event on its current span, with only its kind, a fixed reason, the extension and the task ID. `pi.durable.reports` counts reports by kind. The `OnReport` callback is unchanged.
- **Events.** Committed entries and settled submissions emit `pi.durable.entry` and `pi.durable.submission`, a running task's committed phase change `pi.durable.checkpoint`, and a nested call answered from its stored result `pi.durable.replay`.
- **Propagation** stores an opaque trace carrier on submission and task records, without a storage migration, so a phase that runs after a restart joins the trace it started in (its first invocation after a reopen is marked resumed). `TaskOptions.Trace` inherits the creator's carrier when null; `TaskTrace.Of` sets one and `TaskTrace.Root` starts at the adapter root without storing one. A run started for several queued inputs takes the first input's trace and links the others.
- **Metrics** record `pi.durable.phase.duration` (milliseconds, by task, phase and outcome) and `pi.durable.tasks.active` (live tasks by task name), without record IDs. Closing the Harness releases its active counts.

## Options added in 1.5.0.1

These options ship in PiSharp 1.5.0.1 on the unchanged Pi 1.5.0 baseline.

| API | Behavior |
| --- | --- |
| `AgentChange.Compaction` / agent compaction settings | Persist a per-conversation compaction override, including model overrides, alongside agent configuration. |
| `ITx.GetSubmissionAsync(SubmissionId)` | Read a committed submission before table writes or transaction settlement. External transaction implementations must override the compatibility default to support this read. |
| `DurableStreamOptions.ThinkingBudgets` | Carry structured reasoning budgets through native and hosted model requests. |
| `DurableStreamOptions.WebsocketConnectTimeoutMs` | Carry an explicit connection timeout to compatible transports. |
| `DurableStreamOptions.Identity` / `DurableModelIdentity.WithClientIdentity` | Merge model defaults and request identity field by field. Request fields take precedence. |
| `DurableStreamOptions.OnPayload` | Run the asynchronous payload hook with the model and cancellation token; retain prepared-request fidelity. |

The [model contracts](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Durable/Harness/Models.cs), [transaction contract](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Durable/TypesSession.cs), [agent configuration](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Durable/Harness/Types.cs) and [native adapter](https://github.com/jkelly/PiSharp/blob/main/src/PiSharp.Durable/Models/NativeDurableModels.cs) define exact types and propagation. Store corruption is distinguished from semantic transaction refusals; prepared statement caches are bounded and SQLite supports ordered bulk execution. Delta tracking avoids repeated whole-history copying. The measured executor-call reduction is not a raw SQLite throughput claim: its timing fixture includes artificial delay.

## Registries from Chord

`RegistryReader.Create` in `PiSharp.Durable.Store` from 1.5.0.1 adapts a registry composed by the real Chord v2 host, so a harness follows extensions contributed by plugins. [Examples 35–37](/docs/embed/durable-store/) now exercise host load, shared storage, followed views and registry contributions. The adapter is published as experimental.

## Samples

[`samples/Durable.Examples`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Examples) ports upstream v1.5.0's examples 00 to 34, under v1.5.0's names. They run offline on scripted models, except 15. Run all of them, or pick by number or name:

```powershell
dotnet run --project samples/Durable.Examples -- 13 19
dotnet run --project samples/Durable.Examples -- --storage sqlite --dir ./examples-out 12-recovery
```

PiSharp 1.2.0.x numbered them as upstream v1.2.0 did: v1.5.0 dropped `04-chord-state`, so the examples after it moved down by one, and added 31 to 37.

| Example | Shows |
| --- | --- |
| `00-conversation` | A session stores conversations; commits are transactions. |
| `01-documents` | A rewindable document next to the entries, read now and as of an entry. |
| `02-forks` | Fork a conversation at an entry; documents follow their fork setting. |
| `03-owned-conversations` | A background task that owns a child conversation, created in one commit. |
| `04-watches` | A serialized async watch of a document; commits made while it is busy arrive folded. |
| `05-harness` | Open a harness with a registry; create the root conversation. |
| `06-configuration` | Per-conversation agent choices and live harness settings. |
| `07-harness-conversations` | Conversation handles, typed entries, creating and forking conversations. |
| `08-context` | Transcript history and model context: heads, edits, synthesized results. |
| `09-registry-reload` | Reload an extension by reinstalling it; wrap and uninstall. |
| `10-extension-state` | An extension with its own per-conversation document. |
| `11-tasks` | A durable task state machine with phases and abort. |
| `12-recovery` | Close and reopen; the task continues and a memo prevents a duplicate effect. |
| `13-chat` | A chat turn on a scripted model. |
| `14-system-prompt` | Sections, wrapped sections, per-conversation instructions, an environment that follows the cwd. |
| `15-real-model` | Stream from OpenAI through `NativeDurableModels` (needs `OPENAI_API_KEY`). |
| `16-coding-tools` | The coding tools over `LocalExecutionEnv`, with tool hooks timing a call. |
| `17-print` | Print mode: one prompt, one answer, like `pi -p`. |
| `18-json` | JSON mode: the conversation view's frames as JSON lines. |
| `19-inbox` | Submissions while busy: follow-up, steer, write, withdraw, reject. |
| `20-late-join` | A client that attaches mid-run gets the current state, then changes. |
| `21-subagent-foreground` | A subagent tool, and a UI attaching to the child. |
| `22-subagent-background` | Persistent background subagents that survive a restart. |
| `23-child-tasks` | A task that owns child tasks that sleep, and the task graph. |
| `24-compaction` | A long chat summarized to keep the context small. |
| `25-coding-agent` | A small coding agent with settings backed by a file. |
| `26-plan-mode` | A read-only planning mode that restores the tools when it ends. |
| `27-reviewer` | A reviewer agent on a cheaper model beside the main one. |
| `28-sandbox-per-conversation` | A sandbox per conversation, recorded in a document. |
| `29-tool-override` | Replace `bash` for some conversations, and wrap whichever `bash` wins. |
| `30-reload-and-restart` | Reload an extension while a call runs, then restart. |
| `31-transcript` | (from 1.5.0) A client's transcript over the windowed conversation view, with gaps and earlier pages loaded on demand. |
| `32-tests-after-edits` | (from 1.5.0) An `AfterRun` hook runs the tests after every run that edited files; a failure starts a fix run. |
| `33-turn-limit-and-continue` | (from 1.5.0) A turn limit stops a long run; a continue resumes it without a user message. |
| `34-submit-in-commit` | (from 1.5.0) Messages that only steer a running agent, admitted inside the host's own commit. |

PiSharp 1.5.0.1 adds `35-durable-on-the-store`, `36-durable-views` and `37-harness-registry` in the separate [Durable.Chord.Examples project](/docs/embed/durable-store/#run-examples-3537), using the real host and SQLite.

[`samples/Durable.Chat`](https://github.com/jkelly/PiSharp/tree/main/samples/Durable.Chat) sends one prompt to a real model, prints the answer, then reopens the storage and prints the stored answer. Its API key comes from the provider's environment variable:

```powershell
dotnet run --project samples/Durable.Chat -- --provider anthropic --model claude-sonnet-5 "Name three prime numbers"
```

Its options are `--provider`, `--model`, `--thinking <level>`, `--storage memory|jsonl|sqlite` (default sqlite), `--dir <path>`, `--continue` to reopen `--dir`, and `--tools [--cwd <path>]` to give the model `read`, `write`, `edit` and `bash`.

Upstream's [`pi-durable` README](https://github.com/earendil-works/pi/blob/v1.5.0/packages/durable/README.md), [specification](https://github.com/earendil-works/pi/blob/v1.5.0/packages/durable/docs/spec.md) and [group commit design](https://github.com/earendil-works/pi/blob/v1.5.0/packages/durable/docs/group-commit.md) describe the design in depth.
