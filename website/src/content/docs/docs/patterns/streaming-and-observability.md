---
title: Streaming UIs, watches & telemetry
description: Stream tokens, partial tool-call previews and progress updates to your UI, fold durable state updates with WatchAsync, and trace runs with PiSharp.Telemetry.
---

**What happens in production:** Users staring at a blank spinner for 20 seconds assume the agent froze — while operators trying to debug a slow tool batch need timing spans and structured logs without leaking prompts to third-party SaaS endpoints. PiSharp separates live streaming sinks, durable state watches, and zero-egress opt-in telemetry.

## Three observability surfaces

| Need | API | Layer |
| --- | --- | --- |
| **Token-by-token streaming, tool progress & live UI updates** | `IAgentEventSink` + `StreamingJsonParser` (`PISHARP_AI001`) + `IToolExecutor` progress callback | `PiSharp.Agent` / `PiSharp.AI` |
| **Reactive crash-safe conversation & task-graph state** | `IConversation.WatchAsync`, `IHarness.WatchTaskGraphAsync`, `ISession.WatchDocAsync`, Chord `ViewBridge` | `PiSharp.Durable` / `PiSharp.Durable.Store` |
| **Structured spans, links & metrics with zero background export** | `PiSharp.Telemetry` (`PISHARP_TELEMETRY001`) | `PiSharp.AI` / `PiSharp.Durable` |

## Recipe 1: Live token & tool-progress sink (`PiSharp.Agent`)

The loop awaits `IAgentEventSink.EmitAsync` in strict causal order, and tools that implement the `onProgress` overload emit `ToolExecutionUpdated` events while running:

```csharp
using PiSharp.Agent;
using PiSharp.Contracts;

sealed class LiveUiSink : IAgentEventSink
{
    public ValueTask EmitAsync(AgentEvent observation, CancellationToken ct)
    {
        switch (observation)
        {
            case TurnStreamObserved { Event: TextDelta delta }:
                Console.Write(delta.Delta);
                break;
            case ToolExecutionStarted started:
                Console.WriteLine($"\n› {started.Invocation.Call.Name} {started.Invocation.Call.Arguments}");
                break;
            case ToolExecutionUpdated progress:
                Console.WriteLine($"  … {string.Concat(progress.PartialResult.Content.Select(b => b.Text))}");
                break;
            case ToolExecutionEnded ended:
                var status = ended.Outcome.IsError ? "ERR" : "OK";
                Console.WriteLine($"‹ [{status}] {ended.Outcome.Invocation.Call.Name} ({ended.Outcome.DurationMs} ms)");
                break;
        }
        return ValueTask.CompletedTask;
    }
}
```

Use `agent.Subscribe(extraSink)` to attach temporary observers (for example, per-request logging or UI panels) and dispose the returned handle when finished. For live previews of streaming tool arguments before a tool finishes parsing, use `StreamingJsonParser` / `AssistantStreamReducer.GetToolArgumentsPreview` (`PISHARP_AI001`).

## Recipe 2: Watching folded durable views (`WatchAsync`)

In `PiSharp.Durable` (from 1.5.0), polling getters are replaced by reactive watches (`IConversation.WatchAsync`, `IHarness.WatchTaskGraphAsync`, `ISession.WatchDocAsync`). Rapid consecutive commits automatically **fold** into a single update carrying `Value` and `Ops`, so slow UIs never fall behind:

```csharp
using PiSharp.Durable;
using PiSharp.Durable.Chord;

await using var watch = await root.WatchAsync(call =>
{
    if (call is WatchValueCall<ConversationView> update)
    {
        Console.WriteLine($"Entries: {update.Value.Entries.Length}, Running: {update.Value.IsRunning}");
    }
    else if (call is WatchFailedCall<ConversationView> failed)
    {
        Console.Error.WriteLine($"Watch failed: {failed.Error}");
    }
    return ValueTask.CompletedTask;
}, DurableContext.Background);
```

- Tune how often streamed partial assistant text and tool output commit via `ProgressSettings.PersistWithinMs` (default ~1000 ms; set `0` to persist every chunk immediately).
- When hosting on `PiSharp.Chord`, `ViewBridge` projects typed immutable Durable views into Chord JSON members (`Durable.Chord.Examples` `36-durable-views`).

## Recipe 3: Zero-egress tracing (`PiSharp.Telemetry`) & review hooks *(1.6.0)*

`PiSharp.Telemetry` records spans and metrics **only** when your host explicitly creates and passes a telemetry span (for instance, with its `InMemoryTelemetry` adapter for tests or your own OpenTelemetry bridge). Nothing is ever transmitted externally by PiSharp itself.

:::note[1.6.0 source candidate: `OnYield` & `AfterTools` review hooks]
On `sync/pi-1.6.0` (unreleased), Durable adds `GenerationHooks.OnYield` (`45-review-on-yield`) and `GenerationHooks.AfterTools` (`46-after-tools-terminate`), letting an extension launch a durable `HookTask` to audit, gate, or terminate an assistant response before it is finalized for the user.
:::

## Pitfalls & limits

- **Keep `EmitAsync` non-blocking:** `PiSharp.Agent` awaits every sink callback before moving to the next event; offload slow disk/network I/O to a bounded channel if your UI consumer can stall.
- **Keep `AgentToolContext.Output(...)` concise:** running tool output streams to UIs while executing and also forms part of what the model sees when the tool finishes.

## Try it now

```powershell
# Durable watch folding (3 commits folded into 1 update) + Chord ViewBridge projection
dotnet run --project samples/Durable.Examples -- --storage sqlite 04-watches
dotnet run --project samples/Durable.Chord.Examples -- 36

# 1.6.0 source candidate: OnYield answer review & AfterTools termination hooks
dotnet run --project samples/Durable.Examples -- --storage sqlite 45-review-on-yield 46-after-tools-terminate
```
