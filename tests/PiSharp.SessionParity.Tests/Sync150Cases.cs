// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): core/compaction/compaction.ts
// (createSummarizationOptions sets no maxTokens, #9512; generateTurnPrefixSummary appends the /compact instructions, #11058),
// core/compaction/branch-summarization.ts (generateBranchSummary thinkingLevel; prepareBranchEntries applies the latest context edit
// per target through projectContextEntry) and core/agent-session.ts (navigateTree; _omitRecoveryAttempt omits the tool results that
// answer the omitted message). The cases are modeled on the upstream tests branch-summarization.test.ts,
// compaction-summary-reasoning.test.ts and regressions/11227-recovery-omits-tool-results.test.ts; their expectations are authored,
// never captured from an upstream run.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;
using static EventFixture;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        Case("sync150.summaries-send-no-output-cap-and-focus-the-split-turn-prefix", SummariesUseModelLimitAndFocusPrefix),
        Case("sync150.branch-summary-thinking-level-follows-model-reasoning", BranchSummaryThinkingLevel),
        Case("sync150.branch-entries-apply-context-edits", BranchEntriesApplyContextEdits),
        Case("sync150.tree-summary-carries-thinking-level-and-context-edits", TreeSummaryLevelAndEdits),
        Case("sync150.recovery-omits-tool-results-with-their-call", RecoveryOmitsToolResults),
    ];

    private static async Task SummariesUseModelLimitAndFocusPrefix()
    {
        foreach (var modelMaximum in new[] { 0d, 1024d })
        {
            await using var f = await CreateAsync();
            var generator = new RecordingSummary();
            await f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(new(KeepRecentTokens: 1), ContextWindow: 128_000,
                SummaryOptions: new(ModelMaximumTokens: modelMaximum, CustomInstructions: "keep migration decisions")), generator);
            Check(generator.Requests.Select(request => request.Kind).SequenceEqual([SessionSummaryKind.History, SessionSummaryKind.TurnPrefix]),
                "compaction summary kinds: " + string.Join(",", generator.Requests.Select(request => request.Kind)));
            foreach (var request in generator.Requests)
            {
                // No reserveTokens-based budget: an unknown limit leaves the cap to the route, a known one is the model's.
                Equal<double?>(modelMaximum > 0 ? modelMaximum : null, request.MaximumOutputTokens, modelMaximum + " " + request.Kind + " maxTokens");
                Check(request.Prompt.EndsWith("\n\nAdditional focus: keep migration decisions", StringComparison.Ordinal),
                    request.Kind + " prompt lost the compact instructions");
            }
        }
    }

    private static SessionBranchSummaryPlan OneUserBranch() => new SessionBranchSummaryPlanner().Prepare([new SessionEntryCodec().Parse(
        """{"type":"message","id":"branch-user","parentId":null,"timestamp":"1970-01-01T00:00:00.001Z","message":{"role":"user","content":"Abandoned request","timestamp":1}}""")]);

    // branch-summarization.test.ts "passes the thinking level only for reasoning-capable models": xhigh, off, and a non-reasoning model.
    private static Task BranchSummaryThinkingLevel()
    {
        var plan = OneUserBranch();
        var seen = new[] { (true, "xhigh"), (true, "off"), (false, "high") }.Select(pair => SessionSummaryRequestBuilder.Branch(plan, Model,
            Guid.CreateVersion7().ToString(), new(ModelSupportsReasoning: pair.Item1, ThinkingLevel: pair.Item2)).ThinkingLevel).ToArray();
        Check(seen.SequenceEqual(["xhigh", null, null]), "branch reasoning: " + string.Join(",", seen.Select(level => level ?? "-")));
        Equal<double?>(null, SessionSummaryRequestBuilder.Branch(plan, Model, Guid.CreateVersion7().ToString()).MaximumOutputTokens, "branch maxTokens");
        return Task.CompletedTask;
    }

    // branch-summarization.test.ts "applies context edits to branch entries": an omitted failed attempt and a replaced answer.
    private static Task BranchEntriesApplyContextEdits()
    {
        var codec = new SessionEntryCodec();
        SessionEntry Parse(object entry) => codec.Parse(JsonSerializer.Serialize(entry));
        static string Stamp(int n) => DateTimeOffset.FromUnixTimeMilliseconds(n).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        JsonElement Assistant(string text, StopReason reason, int n) =>
            PiWireJson.WriteMessage(Response(text: text) with { StopReason = reason, Timestamp = n }).Value;
        ImmutableArray<SessionEntry> branch =
        [
            Parse(new { type = "message", id = "u1", parentId = (string?)null, timestamp = Stamp(1), message = new { role = "user", content = "Investigate login", timestamp = 1 } }),
            Parse(new { type = "message", id = "failed", parentId = "u1", timestamp = Stamp(2), message = Assistant("FAILED_ATTEMPT", StopReason.Error, 2) }),
            Parse(new { type = "context_edit", id = "e1", parentId = "failed", timestamp = Stamp(3), targetId = "failed", replacement = (object?)null }),
            Parse(new { type = "message", id = "ok", parentId = "e1", timestamp = Stamp(4), message = Assistant("ORIGINAL_TEXT", StopReason.Stop, 4) }),
            Parse(new { type = "context_edit", id = "e2", parentId = "ok", timestamp = Stamp(5), targetId = "ok", replacement = new { content = "REPLACED_TEXT" } }),
        ];
        var serialized = string.Join("\n", new SessionBranchSummaryPlanner().Prepare(branch).Messages.Select(message => message.WireBody.ToString()));
        Check(serialized.Contains("Investigate login", StringComparison.Ordinal), "the user request is missing");
        Check(!serialized.Contains("FAILED_ATTEMPT", StringComparison.Ordinal), "the omitted attempt reached the summary");
        Check(!serialized.Contains("ORIGINAL_TEXT", StringComparison.Ordinal), "the replaced text reached the summary");
        Check(serialized.Contains("REPLACED_TEXT", StringComparison.Ordinal), "the replacement is missing");
        return Task.CompletedTask;
    }

    // /tree with a summary: navigateTree passes the session's thinking level, and the abandoned branch's context edits apply.
    private static async Task TreeSummaryLevelAndEdits()
    {
        await using var f = await CreateAsync();
        var sessionId = f.Session.Snapshot.Log.Header.Id;
        await f.Session.ConfigureAsync(new(ThinkingLevel: "high"));
        await f.Session.AppendContextEditAsync(sessionId, new("u1", JsonData.Parse("""{"content":"edited abandoned request"}""")));
        await f.Session.AppendContextEditAsync(sessionId, new("a1", null));
        var generator = new RecordingSummary();
        var view = f.Owner.CaptureTree(f.Owner.Current);
        var receipt = await f.Owner.NavigateTreeAsync(view.Attachment, new("a0", view.Revision)
            { Options = new(Summarize: true), Execution = new(generator) });
        Equal(SessionTreeNavigationDisposition.Selected, receipt.Disposition, "navigation");
        var request = generator.Requests.Single();
        Equal(SessionSummaryKind.Branch, request.Kind, "kind");
        Equal<string?>("high", request.ThinkingLevel, "tree summary thinking level");
        Equal<double?>(null, request.MaximumOutputTokens, "tree summary maxTokens");
        Check(request.Prompt.Contains("edited abandoned request", StringComparison.Ordinal), "the replacement is missing from the tree summary");
        Check(!request.Prompt.Contains(new string('y', 100), StringComparison.Ordinal), "the replaced request reached the tree summary");
        Check(!request.Prompt.Contains("[Assistant]: second", StringComparison.Ordinal), "the omitted answer reached the tree summary");
    }

    // 11227: length-stop recovery omits the truncated call and the result that answers it, so the retried request carries neither.
    private static async Task RecoveryOmitsToolResults()
    {
        await using var f = await CreateAsync(ToolCall("call_truncated") with { StopReason = StopReason.Length }, Response(text: "done"));
        var summary = new RecordingSummary();
        f.Session.ConfigureAutomaticCompaction(summary, new(true, 128, 80), contextWindow: 100_000);
        f.Session.ConfigureAutomaticRecovery(1_000);
        await f.Session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"check status","timestamp":20}""")));
        await f.Session.WaitForIdleAsync();
        Equal(2, f.Transport.Requests.Count, "provider requests");
        Equal(1, summary.Requests.Count(request => request.Kind == SessionSummaryKind.History), "recovery compaction");
        var edits = f.Session.Snapshot.Log.Entries.Where(entry => entry.Kind == SessionEntryKind.ContextEdit)
            .Select(entry => f.Session.Snapshot.Log.ById[entry.WireBody.Value.GetProperty("targetId").GetString()!]).ToArray();
        Check(edits.Select(target => target.WireBody.Value.GetProperty("message").GetProperty("role").GetString()).SequenceEqual(["assistant", "toolResult"]),
            "omitted roles: " + string.Join(",", edits.Select(target => target.WireBody.Value.GetProperty("message").GetProperty("role").GetString())));
        var retried = f.Transport.Requests[1].Messages;
        Check(!retried.Any(message => message.Role == "toolResult"), "the truncated call's result reached the retried request");
        Check(!retried.Where(message => message.Role == "assistant").Any(message => PiWireJson.ReadMessage(message.WireBody.Value).StopReason == StopReason.Length),
            "the length-stopped attempt reached the retried request");
    }
}
