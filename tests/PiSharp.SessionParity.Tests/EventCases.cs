using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Output;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;
using PiSharp.Sessions.Compaction;
using static EventFixture;

// Pi v1.1.0 packages/coding-agent/src/core/agent-session.ts (AgentSessionEvent, _willRetryAfterAgentEnd, _prepareRetry,
// _omitRecoveryAttempt, setThinkingLevel, _summarizationRetryCallbacks), packages/ai/src/utils/retry.ts (retryAssistantCall),
// modes/rpc/rpc-mode.ts (event output, extension_error) and modes/json-event.ts (toJsonEvent), by reading.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> EventCases() =>
    [
        Case("events.rpc.agent-end-will-retry-then-auto-retry-records-exact", WillRetryAndAutoRetry),
        Case("events.rpc.agent-end-will-retry-false-without-retry-and-at-exhausted-budget", WillRetryFalse),
        Case("events.rpc.thinking-level-changed-exact-before-response-only-on-change", ThinkingLevelChanged),
        Case("events.rpc.entry-appended-extension-custom-entry-exact", EntryAppended),
        Case("events.rpc.summarization-retry-scheduled-attempt-finished-exact-for-compaction", SummarizationRetryCompaction),
        Case("events.rpc.summarization-retry-branch-summary-source-and-non-retryable-failure", SummarizationRetryBranchAndFailure),
        Case("events.rpc.extension-error-record-exact", ExtensionErrorRecord),
        Case("events.json.session-events-and-agent-end-will-retry", JsonModeSessionEvents),
        Case("events.projector.session-event-records-exact", ProjectorRecords),
    ];

    private static async Task WillRetryAndAutoRetry()
    {
        await using var f = await CreateAsync(Response(StopReason.Error, error: "503 overloaded"), Response(text: "recovered"));
        f.Session.ConfigureAutomaticRetry(new(maxRetries: 2, baseDelayMs: 0), originalDelay: (_, _) => Task.CompletedTask);
        f.StartRpc(); await f.PromptAsync();
        var frames = f.Frames(); var lines = f.Output.Lines();
        var ends = frames.Select((frame, index) => (frame, index)).Where(pair => Type(pair.frame) == "agent_end").ToArray();
        Equal(2, ends.Length, "agent_end count");
        Check(ends[0].frame.Value.GetProperty("willRetry").GetBoolean(), "The failed run's agent_end did not announce the retry.");
        Check(!ends[1].frame.Value.GetProperty("willRetry").GetBoolean(), "The recovered run's agent_end announced a retry.");
        var start = Array.IndexOf(lines, """{"type":"auto_retry_start","attempt":1,"maxAttempts":2,"delayMs":0,"errorMessage":"503 overloaded"}""");
        var end = Array.IndexOf(lines, """{"type":"auto_retry_end","success":true,"attempt":1}""");
        var appended = Array.FindIndex(frames, frame => Type(frame) == "entry_appended");
        Check(start > ends[0].index && appended > start && end > appended && ends[1].index > end, "Retry record order differs: " + string.Join("\n", lines));
        var entry = frames[appended].Value.GetProperty("entry");
        Equal("context_edit", entry.GetProperty("type").GetString(), "omission entry type");
        Check(f.Session.Snapshot.Log.Entries.Any(logged => JsonElement.DeepEquals(logged.WireBody.Value, entry)), "entry_appended is not the acknowledged entry.");
        Equal("""{"type":"entry_appended","entry":""" + entry.GetRawText() + "}", lines[appended], "entry_appended record");
        Equal(2, f.Transport.Calls, "provider calls");
    }

    private static async Task WillRetryFalse()
    {
        await using (var plain = await CreateAsync(Response(StopReason.Error, error: "503 overloaded")))
        {
            plain.StartRpc(); await plain.PromptAsync();
            var end = plain.Frames().Single(frame => Type(frame) == "agent_end");
            Check(!end.Value.GetProperty("willRetry").GetBoolean(), "willRetry without configured retry.");
        }
        await using var f = await CreateAsync(Response(StopReason.Error, error: "503 overloaded"), Response(StopReason.Error, error: "503 overloaded"));
        f.Session.ConfigureAutomaticRetry(new(maxRetries: 1, baseDelayMs: 0), originalDelay: (_, _) => Task.CompletedTask);
        f.StartRpc(); await f.PromptAsync();
        var ends = f.Frames().Where(frame => Type(frame) == "agent_end").Select(frame => frame.Value.GetProperty("willRetry").GetBoolean()).ToArray();
        Check(ends.SequenceEqual([true, false]), "Exhausted budget still announced a retry: " + string.Join(",", ends));
        Check(f.Output.Lines().Contains("""{"type":"auto_retry_end","success":false,"attempt":1,"finalError":"503 overloaded"}"""), "Final retry failure record differs.");
        // A non-retryable error never announces a retry.
        await using var fatal = await CreateAsync(Response(StopReason.Error, error: "invalid api key"));
        fatal.Session.ConfigureAutomaticRetry(new(maxRetries: 3, baseDelayMs: 0), originalDelay: (_, _) => Task.CompletedTask);
        fatal.StartRpc(); await fatal.PromptAsync();
        Check(!fatal.Frames().Single(frame => Type(frame) == "agent_end").Value.GetProperty("willRetry").GetBoolean(), "Non-retryable error announced a retry.");
    }

    private static async Task ThinkingLevelChanged()
    {
        await using var f = await CreateAsync(); var rpc = f.StartRpc();
        await rpc.SubmitAsync(JsonData.Parse("""{"type":"set_thinking_level","id":"t1","level":"high"}"""));
        await rpc.SubmitAsync(JsonData.Parse("""{"type":"set_thinking_level","id":"t2","level":"high"}"""));
        var lines = f.Output.Lines();
        var changed = Array.IndexOf(lines, """{"type":"thinking_level_changed","level":"high"}""");
        var response = Array.FindIndex(lines, line => line.Contains("\"id\":\"t1\"", StringComparison.Ordinal));
        Check(changed >= 0 && changed < response, "thinking_level_changed missing or after the response: " + string.Join("\n", lines));
        Equal(1, lines.Count(line => line.Contains("thinking_level_changed", StringComparison.Ordinal)), "events for an unchanged level");
    }

    private static async Task EntryAppended()
    {
        await using var f = await CreateAsync(); f.StartRpc();
        var receipt = await f.Session.AppendExtensionEntryAsync(f.Session.Snapshot.Log.Header.Id,
            new("parity-extension", "state", 1, JsonData.Parse("""{"count":1}""")));
        var lines = f.Output.Lines();
        Equal("""{"type":"entry_appended","entry":""" + receipt.Entry.WireBody.Value.GetRawText() + "}", lines.Single(), "entry_appended record");
    }

    private sealed class FlakySummary(params string?[] failures) : ISessionSummaryGenerator
    {
        internal int Calls;
        public ValueTask<SessionGeneratedSummary> GenerateAsync(SessionSummaryRequest request, CancellationToken token = default)
        {
            var index = Calls++;
            if (index < failures.Length && failures[index] is { } message)
                throw new SessionCompactionException(SessionCompactionFailure.SummaryFailed) { ProviderErrorMessage = message };
            return ValueTask.FromResult(new SessionGeneratedSummary("authored summary", TokenUsage.Zero));
        }
    }

    private static async Task SummarizationRetryCompaction()
    {
        await using var f = await CreateAsync();
        f.Session.ConfigureAutomaticRetry(new(maxRetries: 3, baseDelayMs: 0), originalDelay: (_, _) => Task.CompletedTask);
        f.StartRpc(); var generator = new FlakySummary("503 overloaded", "terminated");
        await f.Session.CompactAsync(f.Session.Snapshot.Log.Header.Id, new(new(KeepRecentTokens: 1), ContextWindow: 128_000), generator);
        var lines = f.Output.Lines().Where(line => !line.Contains("\"type\":\"compaction_end\"", StringComparison.Ordinal)).ToArray();
        string[] expected =
        [
            """{"type":"compaction_start","reason":"manual"}""",
            """{"type":"summarization_retry_scheduled","attempt":1,"maxAttempts":3,"delayMs":0,"errorMessage":"503 overloaded"}""",
            """{"type":"summarization_retry_attempt_start","source":"compaction","reason":"manual"}""",
            """{"type":"summarization_retry_scheduled","attempt":2,"maxAttempts":3,"delayMs":0,"errorMessage":"terminated"}""",
            """{"type":"summarization_retry_attempt_start","source":"compaction","reason":"manual"}""",
            """{"type":"summarization_retry_finished"}"""
        ];
        Check(lines.SequenceEqual(expected), "Summarization retry records differ:\n" + string.Join("\n", f.Output.Lines()));
        Check(f.Output.Lines()[^1].StartsWith("""{"type":"compaction_end","reason":"manual","result":{""", StringComparison.Ordinal), "Compaction did not complete after the retries.");
        // Three history attempts, then any split-turn prefix request.
        Check(generator.Calls >= 3, "summary calls");
    }

    private static async Task SummarizationRetryBranchAndFailure()
    {
        await using var f = await CreateAsync();
        f.Session.ConfigureAutomaticRetry(new(maxRetries: 3, baseDelayMs: 0), originalDelay: (_, _) => Task.CompletedTask);
        f.StartRpc();
        await f.Session.SummarizeBranchAsync(f.Session.Snapshot.Log.Header.Id, new(null), new FlakySummary("rate limit exceeded"));
        Check(f.Output.Lines().SequenceEqual([
            """{"type":"summarization_retry_scheduled","attempt":1,"maxAttempts":3,"delayMs":0,"errorMessage":"rate limit exceeded"}""",
            """{"type":"summarization_retry_attempt_start","source":"branchSummary"}""",
            """{"type":"summarization_retry_finished"}"""]), "Branch summary retry records differ:\n" + string.Join("\n", f.Output.Lines()));
        // A non-retryable provider error fails the first attempt without retry events.
        await using var g = await CreateAsync();
        g.Session.ConfigureAutomaticRetry(new(maxRetries: 3, baseDelayMs: 0), originalDelay: (_, _) => Task.CompletedTask);
        g.StartRpc(); var generator = new FlakySummary("invalid api key");
        await ThrowsAsync<SessionCompactionException>(() => g.Session.CompactAsync(g.Session.Snapshot.Log.Header.Id,
            new(new(KeepRecentTokens: 1), ContextWindow: 128_000), generator), "non-retryable summary failure");
        Check(!g.Output.Lines().Any(line => line.Contains("summarization_retry", StringComparison.Ordinal)) && generator.Calls == 1, "A non-retryable error was retried.");
    }

    private static async Task ExtensionErrorRecord()
    {
        await using var f = await CreateAsync(); var rpc = f.StartRpc();
        await rpc.PublishExtensionErrorAsync("/extensions/parity", "turn_end", "boom \"quoted\"");
        Equal("""{"type":"extension_error","extensionPath":"/extensions/parity","event":"turn_end","error":"boom \"quoted\""}""",
            f.Output.Lines().Single(), "extension_error record");
    }

    private static async Task JsonModeSessionEvents()
    {
        await using var f = await CreateAsync(Response(StopReason.Error, error: "503 overloaded"), Response(text: "recovered"));
        f.Session.ConfigureAutomaticRetry(new(maxRetries: 2, baseDelayMs: 0), originalDelay: (_, _) => Task.CompletedTask);
        var text = new StringWriter();
        await using (var output = new SessionJsonEventOutput(f.Session, text, null, f.Owner))
        {
            await output.StartAsync();
            await f.Session.PromptAsync(new TranscriptEntry("user", JsonData.Parse("""{"role":"user","content":"go","timestamp":1}""")));
            await f.Session.ConfigureAsync(new(ThinkingLevel: "low"));
        }
        var lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var agentEnds = lines.Where(line => line.StartsWith("""{"type":"agent_end",""", StringComparison.Ordinal)).ToArray();
        Check(agentEnds.Length == 2 && agentEnds[0].EndsWith("\"willRetry\":true}", StringComparison.Ordinal) &&
            agentEnds[1].EndsWith("\"willRetry\":false}", StringComparison.Ordinal), "JSON agent_end willRetry differs.");
        foreach (var record in new[] { """{"type":"auto_retry_start","attempt":1,"maxAttempts":2,"delayMs":0,"errorMessage":"503 overloaded"}""",
            """{"type":"auto_retry_end","success":true,"attempt":1}""", """{"type":"agent_settled","aborted":false}""",
            """{"type":"thinking_level_changed","level":"low"}""" })
            Check(lines.Contains(record), "JSON mode lacks " + record + ":\n" + string.Join("\n", lines));
        Check(lines.Any(line => line.StartsWith("""{"type":"entry_appended","entry":{"type":"context_edit",""", StringComparison.Ordinal)), "JSON mode lacks entry_appended.");
    }

    private static Task ProjectorRecords()
    {
        string Project(SessionOperationEvent value) => RpcSessionEventProjector.Project(value)!.ToString();
        Equal("""{"type":"thinking_level_changed","level":"xhigh"}""", Project(new SessionThinkingLevelChanged(1, "xhigh", "low")), "thinking");
        Equal("""{"type":"summarization_retry_attempt_start","source":"compaction","reason":"overflow"}""",
            Project(new SessionSummarizationRetryAttemptStarted(1, "compaction", SessionCompactionReason.Overflow)), "attempt overflow");
        Equal("""{"type":"summarization_retry_attempt_start","source":"compaction","reason":"threshold"}""",
            Project(new SessionSummarizationRetryAttemptStarted(1, "compaction", SessionCompactionReason.Threshold)), "attempt threshold");
        Equal("""{"type":"summarization_retry_finished"}""", Project(new SessionSummarizationRetryFinished(1, false, 2, "x")), "finished");
        Equal("""{"type":"session_info_changed"}""", Project(new SessionInfoChanged(1, null)), "info cleared");
        Equal("""{"type":"auto_retry_end","success":false,"attempt":3,"finalError":"Retry cancelled"}""",
            Project(new SessionAutoRetryEnded(1, false, 3, "Retry cancelled")), "retry end");
        Check(RpcSessionEventProjector.Project(new SessionModelSelected(1, Model, null, "set")) is null, "model_select has no wire record");
        return Task.CompletedTask;
    }
}
