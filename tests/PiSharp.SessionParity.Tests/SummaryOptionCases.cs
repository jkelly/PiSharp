// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (_runDefaultCompaction passes the
// session thinking level and `undefined, // sessionId`; branch summaries go through generateBranchSummary), compaction/compaction.ts
// (createSummarizationOptions, completeSummarization: cacheRetention "none", sessionId ?? uuidv7()) and branch-summarization.ts.
using System.Runtime.CompilerServices;
using PiSharp.AI;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using static EventFixture;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SummaryOptionCases() =>
    [
        Case("summary.compaction-uses-session-thinking-level-and-fresh-uuidv7-route", CompactionSummaryOptions),
        Case("summary.branch-summary-has-no-thinking-and-a-fresh-uuidv7-route", BranchSummaryOptions),
        Case("summary.transport-generator-forwards-level-and-route-to-the-request", GeneratorForwardsOptions),
    ];

    private sealed class RecordingSummary : ISessionSummaryGenerator
    {
        internal readonly List<SessionSummaryRequest> Requests = [];
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken token = default)
        { lock (Requests) Requests.Add(request); return ValueTask.FromResult(new SessionGeneratedSummary("authored summary", TokenUsage.Zero)); }
    }

    private static bool IsUuidV7(string value) => Guid.TryParseExact(value, "D", out _) && value.Length == 36 && value[14] == '7' && value[19] is '8' or '9' or 'a' or 'b';

    private static async Task CompactionSummaryOptions()
    {
        foreach (var (level, expected) in new[] { ("high", "high"), ("off", (string?)null) })
        {
            await using var f = await CreateAsync();
            await f.Session.ConfigureAsync(new(ThinkingLevel: level));
            var generator = new RecordingSummary(); var sessionId = f.Session.Snapshot.Log.Header.Id;
            await f.Session.CompactAsync(sessionId, new(new(KeepRecentTokens: 1), ContextWindow: 128_000), generator);
            // The split turn summarizes its history and its turn prefix (generateSummaryWithUsage, generateTurnPrefixSummary).
            Check(generator.Requests.Select(request => request.Kind).SequenceEqual([SessionSummaryKind.History, SessionSummaryKind.TurnPrefix]),
                "compaction summary kinds: " + string.Join(",", generator.Requests.Select(request => request.Kind)));
            foreach (var request in generator.Requests)
            {
                // createSummarizationOptions: reasoning = thinkingLevel unless it is "off" (the route applies model.reasoning).
                Equal(expected, request.ThinkingLevel, level + " " + request.Kind + " thinking level");
                // agent-session.ts passes no sessionId, so each completeSummarization call routes with a fresh uuidv7.
                Check(IsUuidV7(request.SessionId) && request.SessionId != sessionId, "compaction route is not a fresh uuidv7: " + request.SessionId);
                Equal("none", request.CacheRetention, "cache retention");
                Equal(Math.Floor((request.Kind == SessionSummaryKind.History ? 0.8 : 0.5) * 16_384), request.MaximumOutputTokens, request.Kind + " maxTokens");
            }
            Check(generator.Requests[0].SessionId != generator.Requests[1].SessionId, "history and turn prefix shared a route");
        }
    }

    private static async Task BranchSummaryOptions()
    {
        await using var f = await CreateAsync();
        await f.Session.ConfigureAsync(new(ThinkingLevel: "high"));
        // Leave a branch whose summary returns to the thinking-level entry, so the session keeps its "high" level.
        var anchor = f.Session.Snapshot.Context.LeafId!;
        await f.Session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"more","timestamp":20}""")));
        var generator = new RecordingSummary(); var sessionId = f.Session.Snapshot.Log.Header.Id;
        await f.Session.SummarizeBranchAsync(sessionId, new(anchor), generator);
        var request = generator.Requests.Single();
        Equal(SessionSummaryKind.Branch, request.Kind, "kind");
        // generateBranchSummary builds { apiKey, headers, env, signal, maxTokens }: no reasoning and no session id.
        Equal<string?>(null, request.ThinkingLevel, "branch thinking level");
        Check(IsUuidV7(request.SessionId) && request.SessionId != sessionId, "branch route is not a fresh uuidv7: " + request.SessionId);
        Equal(4096d, request.MaximumOutputTokens, "branch maxTokens");
        Equal("none", request.CacheRetention, "cache retention");
    }

    private sealed class RecordingTransport : IChatTransport
    {
        internal readonly List<ChatRequest> Requests = [];
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            Requests.Add(request); await Task.CompletedTask;
            var message = new AssistantMessage(request.Model.Api, request.Model.Provider, request.Model.Id, 1, [new TextContent("summary")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            yield return new StreamDone(StopReason.Stop, message);
        }
    }

    private static async Task GeneratorForwardsOptions()
    {
        var transport = new RecordingTransport();
        var generated = await new TransportSessionSummaryGenerator(_ => transport, () => 5).GenerateAsync(
            new(SessionSummaryKind.History, Model, "SYS", "PROMPT", 100, "medium", "019a0000-0000-7000-8000-000000000001"));
        Equal("summary", generated.Text, "summary text");
        var request = transport.Requests.Single();
        Equal("medium", request.ThinkingLevel, "request thinking level");
        Equal("019a0000-0000-7000-8000-000000000001", request.SessionId, "request session id");
        Check(request.Messages.Select(message => message.Role).SequenceEqual(["system", "user"]), "summary request roles");
    }

}
