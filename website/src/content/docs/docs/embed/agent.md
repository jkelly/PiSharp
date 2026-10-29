---
title: Agent loop
description: Run PiSharp.Agent's tool-calling loop with your own tools, hooks and events.
---

`PiSharp.Agent` runs Pi's agent loop. It sends the transcript to the model, streams the reply, runs the tool calls the model asks for, adds the results to the transcript and asks again, until the model stops. It ports Pi's [`packages/agent`](https://github.com/earendil-works/pi/tree/v1.2.0/packages/agent). It keeps the transcript in memory only and ships no tools of its own: for a session file see [Coding agent](/docs/embed/coding-agent/), and for Pi's file and shell tools see [Built-in tools](/docs/embed/coding-agent/#built-in-tools).

## Install

```powershell
dotnet add package PiSharp.Agent --version 1.2.0.1
```

`PiSharp.Agent` brings `PiSharp.AI` and `PiSharp.Contracts`. Everything on this page is in the `PiSharp.Agent` namespace. [Chat and providers](/docs/embed/chat/) explains transports, models and transcript entries.

## How the loop works

An `Agent` holds three things: an `AgentConfiguration` (the model, the transport and the tools), the transcript, and a queue of pending input. `PromptAsync` adds your input to the transcript and runs turns:

1. The agent builds a `ChatRequest` from the transcript and streams the model's reply.
2. The final assistant message is added to the transcript.
3. If the reply has tool calls, the agent runs them and adds one tool result message per call.
4. It checks the steering queue, then starts the next turn.
5. When a reply has no tool calls, the agent checks the follow-up queue. If it is empty, the run ends.

Each step is reported as an event to your `IAgentEventSink`. The loop awaits the sink, so events arrive in order.

A tool has two halves. A `ToolDefinition` gives the agent a name and an `IToolExecutor` to run. A declaration (name, description and JSON schema for the arguments) tells the model the tool exists. Declarations travel in the transcript: put them in the `toolsAdded` array of a system message, as Pi does. The agent does not check arguments against the schema; your executor validates its own input.

## A first agent

This program gives the model one tool, `get_time`, and asks a question that needs it. It reads the key from `OPENROUTER_API_KEY`.

```csharp
using System.Globalization;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.AI.Providers;
using PiSharp.Contracts;

var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
    ?? throw new InvalidOperationException("Set OPENROUTER_API_KEY.");
var model = new ModelDescriptor("anthropic/claude-sonnet-4.5", "openai-completions", "openrouter");
using var provider = NativeProviderFactory.CreateCompletions(model,
    new Uri("https://openrouter.ai/api/v1/chat/completions"), key);

var configuration = new AgentConfiguration(model, provider, [new ToolDefinition("get_time", new TimeTool())]);
await using var agent = new Agent(configuration, Clock, new ConsoleSink());

var system = Entry("system", new
{
    role = "system",
    content = "You are a helpful assistant. Use tools when they help.",
    timestamp = Clock(),
    toolsAdded = new[] { TimeTool.Declaration.Value },
});
var user = Entry("user", new { role = "user", content = "What time is it in Tokyo?", timestamp = Clock() });

var result = await agent.PromptAsync([system, user]);
Console.WriteLine($"\nStopped: {result.Reason} after {result.Turns.Length} turns");

static long Clock() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
static TranscriptEntry Entry(string role, object body) => new(role, JsonData.Parse(JsonSerializer.Serialize(body)));

sealed class TimeTool : IToolExecutor
{
    public static JsonData Declaration { get; } = JsonData.Parse("""
        {
          "name": "get_time",
          "description": "Get the current local time in a time zone.",
          "parameters": {
            "type": "object",
            "properties": { "zone": { "type": "string", "description": "An IANA time zone id, such as Asia/Tokyo." } },
            "required": ["zone"]
          }
        }
        """);

    public ValueTask<ToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var arguments = invocation.Call.Arguments.Value;
        if (!arguments.TryGetProperty("zone", out var zone) || zone.ValueKind != JsonValueKind.String)
            return ValueTask.FromResult(ToolResult.Error(ToolFailureKind.InvalidArguments, "zone must be a string."));
        try
        {
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(zone.GetString()!));
            return ValueTask.FromResult(ToolResult.Success(local.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)));
        }
        catch (TimeZoneNotFoundException)
        {
            return ValueTask.FromResult(ToolResult.Error(ToolFailureKind.InvalidArguments, $"Unknown time zone: {zone.GetString()}"));
        }
    }
}

sealed class ConsoleSink : IAgentEventSink
{
    public ValueTask EmitAsync(AgentEvent observation, CancellationToken cancellationToken)
    {
        switch (observation)
        {
            case TurnStreamObserved { Event: TextDelta delta }:
                Console.Write(delta.Delta);
                break;
            case ToolExecutionStarted started:
                Console.WriteLine($"\n> {started.Invocation.Call.Name} {started.Invocation.Call.Arguments}");
                break;
            case ToolExecutionEnded ended:
                Console.WriteLine($"< {string.Concat(ended.Outcome.Result.Content.Select(block => block.Text))}");
                break;
        }
        return ValueTask.CompletedTask;
    }
}
```

The first turn ends with a `get_time` call. The agent runs `TimeTool`, adds the result and starts a second turn, in which the model answers. `result.Transcript` holds the whole conversation, and `agent.Snapshot.Messages` keeps it for the next prompt.

## Tools

### Executors

`IToolExecutor.ExecuteAsync(ToolInvocation, CancellationToken)` runs one call. The `ToolInvocation` carries:

| Member | Meaning |
| --- | --- |
| `Call` | The `ToolCallContent`: `Id`, `Name` and `Arguments` (a `JsonData` object) |
| `AssistantMessage` | The reply that made the call |
| `SourceIndex` | The call's position in that reply |

Return a `ToolResult`. `ToolResult.Success(text)` and `ToolResult.Error(kind, message)` cover most tools. The full constructor is `ToolResult(Content, Details, IsError, Terminate, Failure)`: `Content` is the text the model sees, `Details` is JSON for your UI that the model does not see, and `Terminate` asks the loop to stop after this batch.

When something goes wrong the agent reports it to the model as an error result instead of throwing:

| `ToolFailureKind` | Cause |
| --- | --- |
| `UnknownTool` | The model called a name with no `ToolDefinition`: `Tool <name> not found`. |
| `InvalidArguments` | Your executor rejected the arguments. |
| `Blocked` | A hook or policy blocked the call. |
| `ExecutionError` | Your executor threw. The message is the exception's message. |
| `HookError` | A tool hook threw. |
| `Canceled` | The run was aborted during the call: `Operation aborted`. |
| `Truncated` | The reply hit its token limit, so its tool calls may be incomplete. They are not run. |

To report progress, implement the second overload, `ExecuteAsync(invocation, onProgress, cancellationToken)`, and await `onProgress(partialResult, cancellationToken)`. Each report becomes a `ToolExecutionUpdated` event. The default implementation of that overload calls the first one.

### Parallel and sequential

`ToolExecutionMode.Parallel` runs a reply's tool calls at the same time. `Sequential` runs them one after another. Set the mode for all tools in `AgentConfiguration.ExecutionMode`, or for one tool in `ToolDefinition.ExecutionMode`. If any tool in a batch is sequential, the whole batch runs sequentially. Results are added to the transcript in call order either way.

The loop ends after a batch in which every result sets `Terminate`.

### Tool hooks

`IToolHooks` runs around every call. `BeforeExecutionAsync` can block a call, and `AfterExecutionAsync` can replace its result:

```csharp
sealed class BlockTool(string name) : IToolHooks
{
    public ValueTask<ToolPreflightDecision> BeforeExecutionAsync(ToolInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.FromResult(invocation.Call.Name == name
            ? new ToolPreflightDecision(Block: true, Reason: $"{name} is disabled in this app.")
            : ToolPreflightDecision.Allow);

    public ValueTask<ToolResult> AfterExecutionAsync(ToolInvocation invocation, ToolResult result, CancellationToken cancellationToken) =>
        ValueTask.FromResult(result);
}
```

```csharp
var configuration = new AgentConfiguration(model, provider, tools, ToolHooks: new BlockTool("delete_file"));
```

A blocked call returns a `Blocked` error with your `Reason`, or `Tool execution was blocked` without one. `ToolPreflightDecision.Terminate` marks that result as terminating, like `ToolResult.Terminate`.

### Prepared tools and action policies

The built-in tools use a stricter path. An `IPreparedToolAdapter` turns a call into a `PreparedToolAction`: the final path or command, its arguments, working directory and environment. A `ToolInvoker` asks your `IToolActionPolicy.AuthorizeAsync(invocation, action, cancellationToken)` before it runs anything, so the policy sees exactly what will happen. `ToolInvoker` implements `IToolExecutor`, so its tools plug into the same `ToolDefinition`. [Built-in tools](/docs/embed/coding-agent/#built-in-tools) shows a policy.

## Configuration

`AgentConfiguration(Model, Transport, Tools, ToolHooks, ExecutionMode, Hooks)` is a record. `Configure(configuration)` swaps it between runs.

| Member | Default | Meaning |
| --- | --- | --- |
| `Model` | required | The model to call. |
| `Transport` | required | Any `IChatTransport`, such as a provider or a `ModelTransportRegistry`. |
| `Tools` | required | The `ToolDefinition` list. Names must be unique. |
| `ToolHooks` | null | An `IToolHooks` for every call. |
| `ExecutionMode` | `Parallel` | How a batch of calls runs. |
| `Hooks` | null | `AgentHooks` for the loop itself (below). |
| `ThinkingLevel` | `off` | Sent with every request. It must be a level the transport supports for the model. |
| `SessionId` | null | Sent with every request as `ChatRequest.SessionId`. |
| `ServiceTier` | null | A `Func<string?>` read for each request. Experimental and unreleased (`PISHARP_TIERS001`); see [Service tiers](/docs/guides/service-tiers/). |

`AgentHooks` lets you change requests and turn boundaries:

| Hook | Called | Use it to |
| --- | --- | --- |
| `PrepareRequest(snapshot, cancellationToken)` | Before each request | Build the `ChatRequest` yourself from the `AgentLoopSnapshot` (`TurnIndex`, `Model`, `Transcript`). Without it the request is the model and the transcript. |
| `TransformRequestMessages` | After the request is prepared | Change the messages sent, without changing the transcript. |
| `FinalTransformRequestMessages` | Last, before sending | A final request-only change. |
| `PrepareNextTurn(turn, cancellationToken)` | Before each turn after the first | Return extra input messages for that turn. They go ahead of any steering messages. |
| `FinishTurn(turn, cancellationToken)` | After each turn | Observe the turn. |
| `FinishTurnDecision(turn, cancellationToken)` | After each turn | Return `AgentLoopFinishAction.End` or `Continue` to override the default. |
| `BeforePrompt` | Before a prompt run starts | Return extra `custom` history messages to add ahead of the run's inputs. |
| `PrepareRequestBoundary` | At each request boundary | Advanced: publish a new configuration and system messages before the request, as the coding agent does for tool loadouts. |

`AgentOptions` sets the limits of one agent:

| Option | Default | Meaning |
| --- | --- | --- |
| `Loop` | `AgentLoopOptions(MaximumTurns: 16, MaximumTranscriptMessages: 1_000_000)` | Turn and transcript limits per run. |
| `Queue` | `AgentPendingInputQueueOptions()` | Steering and follow-up limits (below). |
| `MaximumTools` | 128 | Tools per configuration. |
| `StreamCapacity` | 32 | Stream events buffered per turn. |
| `MaximumSubscribers` | 128 | Extra sinks added with `Subscribe`. |
| `CancellationBehavior` | `SettleAborted` | What an abort does to `PromptAsync` (see [Aborts and errors](#aborts-and-errors)). |
| `ProgressDelivery` | Pi-compatible delivery | `ToolProgressDeliveryOptions`: how progress reports are delivered and bounded. |
| `TimeProvider` | null | A clock for `DurationMs` on assistant messages and tool outcomes. |

## Events

Your sink's `EmitAsync(AgentEvent, CancellationToken)` receives these records, in this order within a run:

| Event | Members | When |
| --- | --- | --- |
| `AgentLoopStarted` | none | The run starts. |
| `AgentLoopInputMessageStarted`, `AgentLoopInputMessageEnded` | `Message` | An input message (prompt, steering or follow-up) joins the transcript. |
| `AgentLoopTurnStarted` | `TurnIndex` | A turn starts. |
| `AssistantMessageStarted` | `Message` | The reply starts streaming. |
| `TurnStreamObserved` | `Event` | Each `StreamEvent` of the reply, such as `TextDelta`. |
| `AssistantMessageEnded` | `Message` | The reply is final and is added to the transcript. |
| `ToolExecutionStarted` | `Invocation` | A tool call starts. |
| `ToolExecutionUpdated` | `Invocation`, `PartialResult` | The tool reported progress. |
| `ToolExecutionEnded` | `Outcome` | A call finished. `Outcome.Result` is its `ToolResult`, and `Outcome.IsError` tells whether it failed. |
| `ToolResultMessageStarted`, `ToolResultMessageEnded` | `Message` | A tool result message joins the transcript. |
| `AgentLoopTurnEnded` | `Turn` | A turn ended. `Turn.Result` holds the `ChatResult` and the tool batch. |
| `AgentLoopEnded` | `Result` | The run ended. |

`Subscribe(sink)` adds another sink and returns an `IDisposable` that removes it.

## Steering and follow-up

You can queue input while the agent works:

- `Steer(message)` queues a message the agent delivers after the current turn's tool calls finish, before the next request. Use it to redirect the agent mid-task.
- `FollowUp(message)` queues a message the agent delivers only when it would otherwise stop. Use it to chain the next task.

Here `User(text)` builds a user entry the same way `Entry` does in the first sample:

```csharp
var run = agent.PromptAsync(User("Summarize the release notes."));
agent.Steer(User("Keep it under 100 words."));
agent.FollowUp(User("Now list three open questions."));
var result = await run;
```

`SteeringMode` and `FollowUpMode` choose how much the agent takes at a time: `AgentPendingInputMode.OneAtATime` (the default) delivers one queued message per check, and `All` delivers the whole queue. `PeekQueuedMessages()`, `ClearQueues()` and `GetPendingInputQueueSnapshot()` inspect and clear the queues.

`AgentPendingInputQueueOptions` bounds them:

| Option | Default |
| --- | --- |
| `SteeringMode`, `FollowUpMode` | `OneAtATime` |
| `MaximumMessagesPerQueue` | 256 |
| `MaximumMessageCharacters` | 65,536 |
| `MaximumCharactersPerQueue` | 1,048,576 |
| `MaximumJsonDepth` | 32 |

A message over a limit throws `AgentPendingInputException` with `Failure` set to `InvalidInput` or `ResourceLimit`.

## Running again

- `PromptAsync(message)` or `PromptAsync(messages)` adds input and runs. Inputs must have the role `system`, `user` or `custom`, and the `role` in the wire body must match.
- `ContinueAsync()` runs again without new input, for example after a failure. The transcript must end with a `user`, `toolResult` or `custom` message. If it ends with an assistant reply, `ContinueAsync` takes the queued steering messages, or else the follow-ups, and throws when both queues are empty.
- `ReplaceMessages(messages)` replaces the transcript between runs. History may also hold `assistant` and `toolResult` messages.
- `Snapshot` returns an `AgentSnapshot`: the `Messages`, `Model`, `Tools`, `PendingInputs`, queue counts, `IsRunning`, the last `Failure` and more.
- `WaitForIdleAsync()` waits for the current run to settle.

Only one run happens at a time. Calling `PromptAsync`, `ContinueAsync`, `Configure` or `ReplaceMessages` during a run throws `InvalidOperationException("Agent is already processing.")`.

## Aborts and errors

`Abort()` cancels the current run and returns false when nothing is running. Canceling the token you pass to `PromptAsync` does the same. `DisposeAsync` aborts and waits for the run to finish.

`AgentOptions.CancellationBehavior` decides how an aborted run ends:

| Value | `PromptAsync` after an abort |
| --- | --- |
| `SettleAborted` (default) | The stream settles with an aborted assistant message, and the task usually completes with a result whose `Reason` is `ChatFailure`. |
| `Propagate` | The task is canceled and throws `OperationCanceledException`. |

After any run, `Snapshot.Failure` says how it ended: null for success, `Canceled` for an abort, `ChatFailure` for a provider failure, and `RunFault` for an exception. `AgentLoopResult.Reason` is one of:

| `AgentLoopStopReason` | Meaning |
| --- | --- |
| `Completed` | The model stopped and no follow-up was queued. |
| `ChatFailure` | A request failed or was aborted. The failed reply is in the last turn's `Result.Chat`. |
| `TurnLimit` | The run reached `MaximumTurns`. |
| `TranscriptLimit` | The transcript reached `MaximumTranscriptMessages`. |

`AgentLoopResult` also has the `Transcript`, the `Turns`, `PendingMessages` that were queued but not delivered, and `RunMessages`, the messages this run added.

## Without the Agent class

`Agent` wraps three lower-level classes that you can use directly when you manage the transcript yourself:

| Class | Role |
| --- | --- |
| `ToolBatchScheduler(tools, hooks, executionMode, ...)` | Runs one reply's tool calls. |
| `TurnRunner(chatClient, scheduler, thinkingLevel, sessionId)` | Runs one turn: the request, the stream and the tool batch. |
| `AgentLoopRunner(turnRunner, model, clock, options)` | Runs turns until the model stops, with callbacks for each request and for queued input. |

```csharp
var queue = new AgentPendingInputQueue();
var turns = new TurnRunner(new ChatClient(provider), new ToolBatchScheduler([new ToolDefinition("get_time", new TimeTool())]));
var runner = new AgentLoopRunner(turns, model, Clock, new AgentLoopOptions(MaximumTurns: 8));

var callbacks = new AgentLoopCallbacks(
    (snapshot, cancellationToken) => ValueTask.FromResult(new ChatRequest(snapshot.Model, snapshot.Transcript)),
    GetSteeringMessages: queue.GetSteeringMessagesAsync,
    GetFollowUpMessages: queue.GetFollowUpMessagesAsync);

var result = await runner.RunAsync([system, user], callbacks, new ConsoleSink());
```

`RunWithContextAsync(history, newInputs, callbacks, sink, cancellationToken)` runs with existing history. `ChatClient` and `ChatRequest` are in the `PiSharp.AI` namespace.

## Compared with Pi

- In Pi a tool object carries its schema and its `execute` function together. In PiSharp the declaration and the executor are separate: the executor is a `ToolDefinition`, and the declaration reaches the model through the transcript.
- A plain `IToolExecutor` gets its arguments as the model sent them, unvalidated. The built-in tools parse and check their own arguments.
- PiSharp adds explicit limits where Pi has none: turns per run, transcript length, queue sizes and stream buffering.
