---
title: Steering, follow-ups & cancellation
description: Redirect a running agent mid-task, queue follow-up prompts, cancel tool batches cleanly and send urgent mailbox messages.
---

**What happens in production:** An agent is halfway through a five-turn refactor when the user realizes they want a different approach, or wants to queue the next task while the agent finishes, or presses `Esc` to abort a slow tool. Handling live input cleanly requires separating **steering** (interrupt between turns), **follow-up** (wait until idle), and **cancellation** (stop now).

## Three kinds of live intervention

| Intervention | When it takes effect | `PiSharp.Agent` | `PiSharp.Durable` | `PiSharp.CustomAgents` teams |
| --- | --- | --- | --- | --- |
| **Steer** | After the current tool batch finishes, **before** the next LLM call. | `agent.Steer(entry)` | `root.SubmitAsync(SubmissionDraft.Steer(...))` or inbox steering | `SendAsync(to, text, new AgentSendOptions { Urgent = true })` |
| **Follow-up** | Only after the model finishes with no tool calls. | `agent.FollowUp(entry)` | `root.SubmitAsync(SubmissionDraft.FollowUp(...))` | Normal `SendAsync(to, text)` |
| **Abort / Stop** | Immediately cancels active streams & tool `CancellationToken`s. | `agent.Abort()` or cancel `PromptAsync` token | `root.AbortAsync(ctx)` | `stop_agent` tool or `run.AbortAsync()` |

## Recipe 1: Steering and follow-ups in `PiSharp.Agent`

```csharp
using PiSharp.Agent;

var options = new AgentOptions
{
    Queue = new AgentPendingInputQueueOptions
    {
        SteeringMode = AgentPendingInputMode.OneAtATime,
        FollowUpMode = AgentPendingInputMode.OneAtATime,
    },
    CancellationBehavior = AgentCancellationBehavior.SettleAborted,
};

await using var agent = new Agent(configuration, Clock, sink, options);

var run = agent.PromptAsync(User("Audit PaymentClient.cs and update retry tests."));

// Delivered right after the current tool batch completes, before the next model request:
agent.Steer(User("Keep the existing public constructor signature unchanged."));

// Delivered only once the model finishes the task with no more tool calls:
agent.FollowUp(User("Now summarize the remaining failure cases in three bullets."));

var result = await run;
```

- Set `SteeringMode` or `FollowUpMode` to `AgentPendingInputMode.All` if you want every queued message injected together in one turn instead of one at a time.
- Inspect or clear queued entries anytime via `agent.PeekQueuedMessages()`, `agent.GetPendingInputQueueSnapshot()`, and `agent.ClearQueues()`.

## Recipe 2: Clean cancellation & resuming after an abort

`AgentOptions.CancellationBehavior` chooses how `PromptAsync` settles when `agent.Abort()` is called:
- **`SettleAborted` (default):** cancels in-flight streams and tools, appends a settled aborted assistant message to the transcript, and returns an `AgentLoopResult` with `Reason = AgentLoopStopReason.ChatFailure` and `agent.Snapshot.Failure = AgentFailure.Canceled`.
- **`Propagate`:** rethrows `OperationCanceledException` to the caller.

Once settled, you can continue the conversation with `PromptAsync(newUserInput)` — or call `agent.ContinueAsync()` to resume directly from a trailing `user`, `toolResult`, or queued steering/follow-up message.

:::note[New in 1.6.0: aborted tool batches]
From PiSharp 1.6.0, pressing `Esc` in the CLI while a tool batch is executing writes an explicit `Operation aborted` tool result for **every** call in the batch so the transcript stays structurally valid for follow-up turns.
:::

## Recipe 3: Durable inbox steering & urgent team messages

In `PiSharp.Durable`, queued messages live in the transactional **inbox** so a crash never duplicates or drops a user message (`19-inbox`, `20-late-join`, `34-submit-in-commit`).

In `PiSharp.CustomAgents`, the host or another agent can nudge a busy subagent without killing it:

```csharp
// Urgent = true steers the subagent at its next turn boundary instead of waiting for idle
await session.Host.SendAsync("researcher#1", "Focus only on releases after January 2026.",
    new AgentSendOptions { Urgent = true, Id = "steer-research-1" });
```

For workflows (`WorkflowRun<TData>`), calling `await run.AbortAsync()` triggers the workflow's `.OnAbort((data, ctx, ct) => ...)` cleanup handler with the last committed `TData` before finalizing `WorkflowRunStatus.Aborted`.

## Pitfalls & limits

- **Only one `PromptAsync` can run at a time on one `Agent` instance.** Calling `PromptAsync`, `ContinueAsync`, `Configure`, or `ReplaceMessages` while running throws `InvalidOperationException("Agent is already processing.")`. Queue input with `Steer` / `FollowUp` instead, or wait on `agent.WaitForIdleAsync()`.
- **Queue ceilings:** `AgentPendingInputQueueOptions` caps queues at 256 messages, 65,536 chars per message, and 1 MiB total by default; exceeding limits throws `AgentPendingInputException`.
- **Steer does not kill an active tool:** steering waits for the current tool batch to finish so files and APIs are not left half-written. Use `Abort()` when you need immediate cancellation.

## Try it now

```powershell
# Durable inbox steering, late-join observers, turn-limit continue, and submit-in-commit
dotnet run --project samples/Durable.Examples -- --storage sqlite 19-inbox 20-late-join 33-turn-limit-and-continue 34-submit-in-commit

# Desktop newline-delimited JSON host loop
dotnet run --project samples/agents/30-DesktopWorkflowAssistant -- --self-test
```
