# High-level aborted-work settlement

Agent defaults to AgentCancellationBehavior.SettleAborted. Its explicit Propagate option retains prior task cancellation. Existing low-level ChatClient.StartAsync, TurnRunner.RunAsync, AgentLoopRunner.RunAsync and RunWithContextAsync keep their accepted behavior. Opting-in methods are ChatClient.StartWithAbortSettlementAsync, TurnRunner.RunWithAbortSettlementAsync and AgentLoopRunner.RunWithContextAndAbortSettlementAsync.

## Work and delivery

The original work token reaches transports, tools and finish observer/decision callbacks. Authoritative input, assistant, tool-result, turn and end events use CancellationToken.None in the settling profile. A durable sink can check its delivery token and still append the aborted assistant. That token grants no new effects: work cancellation still closes subsequent preparation, provider/tool admission and queue polls.

A chat with already-canceled work does not call the transport. Its honest empty aborted fallback uses the explicit per-turn clock. After stream start the existing reducer retains observed identity, timestamp, usage, metadata and accumulated text. It invents no provider response ID, signature or usage. Cooperatively canceled preparation/poll callbacks can transition to that fallback; arbitrary faults remain task failures.

The bounded producer keeps cancellation-aware writes. The reader drains without a canceled reader token so cancellation can release a producer blocked on a full channel. Owned iterator cleanup completes before assistant commit or finish callbacks. Sinks are still awaited; delivery faults propagate after cleanup. Trusted code ignoring cancellation can delay settlement indefinitely.

## Terminal finalization

The opting-in ChatRun may retain StreamError(Aborted) already returned by provider MoveNext despite canceled work. The existing reducer validates reason, block count and character limits. It permits no successful terminal/progress after cancellation and reads nothing after a terminal. Without an authoritative abort, existing reducer.Failure(Cancelled) supplies an explicitly native aborted partial and fixed diagnostic, not a provider-authored final or source-exact error string. Unfinished tool fragments remain display-only.

The settling turn emits AssistantMessageStarted from an observed start, or from the final message if no start existed. Separate TurnStreamObserved records retain provider observations. Final high-level messages stamp the real native requested thinking default, thinkingLevel:"off"; comparison does not strip it. The current Agent configuration supplies no alternate thinking level. Standalone low-level methods receive neither stamp nor new lifecycle record.

Cleanup precedes the awaited assistant-end commit. Error/aborted assistants execute no tools. Started batches settle and retain successful outcomes/source-ordered result commits. Finish observer/decision receive the canceled work token. Failed/canceled turns ignore End/Continue and emit turn/end with the settlement token without another request or poll.

Normally delivered cancellation returns ChatFailure and Canceled state, with CancellationRequested retaining the actual token fact. It does not report successful chat. Idle/disposal wait for cleanup and all end listeners. Throwing finish callbacks still fault/cancel after assistant commit; no later turn/end is invented.

## Evidence

AbortLifecycleTests.Cases supplies six offline gated groups. The high-level-aborted-work-settlement-v1 comparison pins finish-decisions input SHA-256 815203f5a198cc2ddf7cb1c7d6856adf46ef051127861391af9e711f7c7500ba and golden SHA-256 9f63f961256a9af071c90220adbca8861e6c813fcfdfcaa4ab9fffaa37169e23. Case seven uses a real native abort; transport and finish observe canceled work, matching source finalSignalAborted:true.

It checks the complete final assistant including thinkingLevel, timestamp, usage/error; complete selected lifecycle events; request history/model; returned/context arrays; remaining queues and zero effects. Native lifecycle records map to source agent_start, turn_start, message start/end, turn end and agent end. Separate TurnStreamObserved records are not double-counted. Only object-key order may differ; raw number tokens, arrays, strings and missing/null remain exact. No golden or signal fact is rewritten.

Other groups cover before-acquisition abort, retained propagation, observed interleaved partial text/tool data, gated cleanup, a delivery-token-checking sink, the last end listener, finish faults, oversized authoritative input and a blocked bounded producer. Successful execution/acceptance belongs to the immutable native receipt, not source presence.

Sources are pinned [Agent](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent.ts), [runLoop/streamAssistantResponse](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/agent/src/agent-loop.ts) and [Responses abort finalization](https://github.com/earendil-works/pi/blob/d86654abb8862e201933517d6f1fce9f88dd117f/packages/ai/src/api/openai-responses.ts) at d86654abb8862e201933517d6f1fce9f88dd117f.

## Remaining required qualification

Once its receipt passes, this comparison closes the specific authored aborted-terminal/canceled-signal composition. It does not qualify genuine mid-read provider error formatting or every high-level fault fallback. The reducer still does not repair upstream-style tool previews; aborted arguments retain owned non-executable values. Upstream partial-abort and preparation/listener-fault captures are needed for those fields/traces.

Cancellation after a successful assistant/tool batch preserves that message and known completed effects, stops further work and returns the native canceled failure envelope. It does not relabel observed success as invented aborted provider content. Pi's possible later canceled provider attempt and handleRunFailure fallback need phase-specific captures. Limits may also prevent another assistant; clock/callback faults remain task failures. These are remaining requirements, not permanent exclusions or full phase acceptance.
