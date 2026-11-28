// Pi fb0fe823539f6d04850f6e34748c01f01f212e1f (MIT): packages/durable/src/harness/types.ts, harness/usage.ts,
// signals.ts and types.ts. Native contract cases exercise the released discriminants and optional image usage.
using PiSharp.Durable;
using PiSharp.Durable.Chord;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync170Cases()
    {
        yield return Case("sync170.tagged-public-results", () =>
        {
            Equal("stored", new CommitResult(2).Kind, "stored");
            Equal("refused", new CommitResult(1, new IOException()).Kind, "refused after one stored batch");
            Equal("expired", SignalState.ExpiredKey.Kind, "expired signal");
            Equal("value", SignalState.Of(JsonValue.Null).Kind, "delivered JSON null");
            Equal("sleep", ToolPark.Sleeping(until: 1).Kind, "sleep");
            Equal("wait", ToolPark.Waiting([new TaskId(1)], JoinPolicy.Any).Kind, "wait");
            Equal("tool", new ToolWrap("read", tool => tool).Kind, "tool wrapper");
            Equal("section", new SectionWrap("prompt", section => section).Kind, "section wrapper");
            Equal("continue", OnYieldResult.Continue("continue").Kind, "continue");
            Equal("tasks", OnYieldResult.Tasks([]).Kind, "tasks");
            Equal("decline", CompactionDecision.Declined.Kind, "decline");
            Equal("summary", CompactionDecision.FromSummary("").Kind, "summary");
        });

        yield return Case("sync170.accumulates-optional-image-output-in-usage", () =>
        {
            var empty = (JsonObjectValue)J("""{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"totalTokens":0,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0,"total":0}}""");
            var withImages = empty.ToBuilder();
            withImages["imageOutput"] = 7;
            var sum = UsageLedger.AddUsage(empty, withImages.ToImmutable());
            sum = UsageLedger.AddUsage(sum, empty);
            sum = UsageLedger.AddUsage(sum, withImages.ToImmutable());
            Equal("14", sum["imageOutput"].ToJson(), "image output adds over legacy usage");
            Check(!UsageLedger.AddUsage(empty, empty).ContainsKey("imageOutput"), "missing counter stays absent");
        });
    }
}
