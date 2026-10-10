// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (compact,
// _getSummarizationRequestAuth), packages/coding-agent/src/core/model-runtime.ts (prepareRequest: "Unknown provider", "Provider is not
// configured") and packages/coding-agent/src/core/compaction/compaction.ts (getSummarizationFailure).
using System.Text.Json.Nodes;

// Summaries (compaction here) of a model whose provider has no auth, or of the unknown model, fail as Pi 1.1.0 does when run: the
// summarization auth lookup finds nothing, the request fails in prepareRequest, and getSummarizationFailure names it.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SummaryAuthCases() =>
    [
        ("summaries.compaction-without-auth-or-model-fails-in-prepare-request", async () =>
        {
            using var sandbox = new Sandbox("summary-auth");
            sandbox.Vars.Remove("ANTHROPIC_API_KEY");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"compaction":{"keepRecentTokens":1}}""");
            var session = SeedSession(sandbox, "two.jsonl", "anthropic", "claude-haiku-4-5");
            File.AppendAllText(session, string.Join("\n",
                "{\"type\":\"message\",\"id\":\"a5\",\"parentId\":\"a4\",\"timestamp\":\"2026-10-09T10:00:00.005Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"second question with some words in it\"}],\"timestamp\":3}}",
                "{\"type\":\"message\",\"id\":\"a6\",\"parentId\":\"a5\",\"timestamp\":\"2026-10-09T10:00:00.006Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"second answer with some words in it\"}],\"api\":\"anthropic-messages\",\"provider\":\"anthropic\",\"model\":\"claude-haiku-4-5\",\"usage\":{\"input\":3,\"output\":2,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":5,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}},\"stopReason\":\"stop\",\"timestamp\":4}}") + "\n");
            async Task Compact(string[] args, string expected)
            {
                var events = new List<JsonNode>();
                var responses = await RpcSequence(sandbox, ["--mode", "rpc", .. args, "--session", session], events, ["""{"id":"1","type":"compact"}"""]);
                Check(!responses[0]["success"]!.GetValue<bool>() && responses[0]["error"]!.GetValue<string>() == expected, "compact response: " + responses[0].ToJsonString());
                var end = events.Single(line => line["type"]?.GetValue<string>() == "compaction_end");
                Equal("Compaction failed: " + expected, end["errorMessage"]?.GetValue<string>(), "compaction_end");
            }
            // The selected model's provider has no auth: rpc-mode.ts compact error and compaction_end (Pi 1.1.0 run with node).
            await Compact(["--provider", "anthropic", "--model", "claude-haiku-4-5"], "Summarization failed: Provider is not configured: anthropic");
            // No model at all: the Agent's DEFAULT_MODEL has no provider.
            await Compact([], "Summarization failed: Unknown provider: unknown");
            Equal(0, sandbox.Requests.Count, "nothing sent");
            Check(!File.ReadAllText(session).Contains("\"type\":\"compaction\"", StringComparison.Ordinal), "nothing compacted");
        }),
    ];
}
