// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/compaction/compaction.ts (createSummarizationOptions,
// completeSummarization), compaction/branch-summarization.ts and core/agent-session.ts (_runDefaultCompaction, branch summaries).
using System.Net;
using System.Text;
using PiSharp.AI;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;

// Expected bodies were captured by running the installed @earendil-works/pi-ai@1.1.0 completeSimple with exactly the options
// completeSummarization sends ({ maxTokens, apiKey, sessionId, cacheRetention: "none" } plus reasoning = the session's thinking
// level for compaction on a reasoning model; branch summaries pass no reasoning) and context { systemPrompt: "SYS", messages:
// [user "PROMPT"] } against a local HTTP server.
internal static partial class Program
{
    private sealed record SummaryRequestCase(string Provider, string Model, string KeyName, string Url, int Maximum, string? Level,
        string Body, string? Beta);

    private static readonly SummaryRequestCase[] SummaryRequestCases =
    [
        // Budget thinking: the summary cap plus the medium budget (adjustMaxTokensForThinking), interleaved-thinking beta, no caching.
        new("anthropic", "claude-sonnet-4-5", "ANTHROPIC_API_KEY", "https://api.anthropic.com/v1/messages?beta=true", 8000, "medium",
            """{"model":"claude-sonnet-4-5","messages":[{"role":"user","content":[{"type":"text","text":"PROMPT"}]}],"max_tokens":16192,"stream":true,"system":[{"type":"text","text":"SYS"}],"thinking":{"type":"enabled","budget_tokens":8192,"display":"summarized"}}""",
            "interleaved-thinking-2025-05-14"),
        new("anthropic", "claude-sonnet-4-5", "ANTHROPIC_API_KEY", "https://api.anthropic.com/v1/messages?beta=true", 4096, null,
            """{"model":"claude-sonnet-4-5","messages":[{"role":"user","content":[{"type":"text","text":"PROMPT"}]}],"max_tokens":4096,"stream":true,"system":[{"type":"text","text":"SYS"}],"thinking":{"type":"disabled"}}""",
            null),
        // Adaptive thinking: effort from the session level, the cap unchanged.
        new("anthropic", "claude-opus-4-6", "ANTHROPIC_API_KEY", "https://api.anthropic.com/v1/messages?beta=true", 8000, "high",
            """{"model":"claude-opus-4-6","messages":[{"role":"user","content":[{"type":"text","text":"PROMPT"}]}],"max_tokens":8000,"stream":true,"system":[{"type":"text","text":"SYS"}],"thinking":{"type":"adaptive","display":"summarized"},"output_config":{"effort":"high"}}""",
            null),
        new("anthropic", "claude-opus-4-6", "ANTHROPIC_API_KEY", "https://api.anthropic.com/v1/messages?beta=true", 4096, null,
            """{"model":"claude-opus-4-6","messages":[{"role":"user","content":[{"type":"text","text":"PROMPT"}]}],"max_tokens":4096,"stream":true,"system":[{"type":"text","text":"SYS"}],"thinking":{"type":"disabled"}}""",
            null),
        // Without reasoning the model's metadata still applies: managed effort sends "high" with its marker, and claude-fable-5
        // (thinkingLevelMap.off null) sends no thinking but keeps its server-side fallbacks.
        new("anthropic", "claude-sonnet-5-5", "ANTHROPIC_API_KEY", "https://api.anthropic.com/v1/messages?beta=true", 4096, null,
            """{"model":"claude-sonnet-5-5","messages":[{"role":"user","content":[{"type":"text","text":"PROMPT"}]},{"role":"system","content":[],"output_config":{"effort":"high"}}],"max_tokens":4096,"stream":true,"system":[{"type":"text","text":"SYS"}],"thinking":{"type":"adaptive","display":"summarized","block_binding":{"prefix_mismatch_behavior":"drop_block"}},"output_config":{"effort":"high"}}""",
            "mid-conversation-output-config-2026-07-01,thinking-binding-controls-2026-08-01"),
        new("anthropic", "claude-fable-5", "ANTHROPIC_API_KEY", "https://api.anthropic.com/v1/messages?beta=true", 4096, null,
            """{"model":"claude-fable-5","messages":[{"role":"user","content":[{"type":"text","text":"PROMPT"}]}],"max_tokens":4096,"stream":true,"system":[{"type":"text","text":"SYS"}],"fallbacks":[{"model":"claude-opus-4-8"},{"model":"claude-opus-5"}]}""",
            "server-side-fallback-2026-07-01"),
        new("openai", "gpt-5.1", "OPENAI_API_KEY", "https://api.openai.com/v1/responses", 4096, null,
            """{"model":"gpt-5.1","input":[{"role":"developer","content":"SYS"},{"role":"user","content":[{"type":"input_text","text":"PROMPT"}]}],"stream":true,"store":false,"max_output_tokens":4096,"reasoning":{"effort":"none"}}""",
            null),
        new("google", "gemini-2.5-pro", "GEMINI_API_KEY", "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-pro:streamGenerateContent?alt=sse", 4096, null,
            """{"contents":[{"parts":[{"text":"PROMPT"}],"role":"user"}],"systemInstruction":{"parts":[{"text":"SYS"}],"role":"user"},"generationConfig":{"maxOutputTokens":4096,"thinkingConfig":{"thinkingBudget":0}}}""",
            null),
        new("openai", "gpt-5", "OPENAI_API_KEY", "https://api.openai.com/v1/responses", 8000, "high",
            """{"model":"gpt-5","input":[{"role":"developer","content":"SYS"},{"role":"user","content":[{"type":"input_text","text":"PROMPT"}]}],"stream":true,"store":false,"max_output_tokens":8000,"reasoning":{"effort":"high","summary":"auto"},"include":["reasoning.encrypted_content"]}""",
            null),
        new("openai", "gpt-5", "OPENAI_API_KEY", "https://api.openai.com/v1/responses", 4096, null,
            """{"model":"gpt-5","input":[{"role":"developer","content":"SYS"},{"role":"user","content":[{"type":"input_text","text":"PROMPT"}]}],"stream":true,"store":false,"max_output_tokens":4096}""",
            null),
        new("mistral", "magistral-medium-latest", "MISTRAL_API_KEY", "https://api.mistral.ai/v1/chat/completions", 8000, "medium",
            """{"model":"magistral-medium-latest","stream":true,"messages":[{"role":"system","content":"SYS"},{"role":"user","content":[{"type":"text","text":"PROMPT"}]}],"max_tokens":8000,"reasoning_effort":"high"}""",
            null),
        new("mistral", "magistral-medium-latest", "MISTRAL_API_KEY", "https://api.mistral.ai/v1/chat/completions", 4096, null,
            """{"model":"magistral-medium-latest","stream":true,"messages":[{"role":"system","content":"SYS"},{"role":"user","content":[{"type":"text","text":"PROMPT"}]}],"max_tokens":4096,"reasoning_effort":"none"}""",
            null),
        new("google", "gemini-2.5-flash", "GEMINI_API_KEY", "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse", 8000, "medium",
            """{"contents":[{"parts":[{"text":"PROMPT"}],"role":"user"}],"systemInstruction":{"parts":[{"text":"SYS"}],"role":"user"},"generationConfig":{"maxOutputTokens":8000,"thinkingConfig":{"includeThoughts":true,"thinkingBudget":8192}}}""",
            null),
        new("google", "gemini-2.5-flash", "GEMINI_API_KEY", "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse", 4096, null,
            """{"contents":[{"parts":[{"text":"PROMPT"}],"role":"user"}],"systemInstruction":{"parts":[{"text":"SYS"}],"role":"user"},"generationConfig":{"maxOutputTokens":4096,"thinkingConfig":{"thinkingBudget":0}}}""",
            null),
    ];

    /// <summary>The summary request each family sends through the production summary route (LiveSessionConnection.CreateSummaryTransport
    /// under TransportSessionSummaryGenerator), for compaction with the session's thinking level and for a branch summary.</summary>
    private static async Task SummaryRequestsFollowUpstream()
    {
        const string sessionId = "019a0000-0000-7000-8000-000000000001";
        var mismatches = new List<string>();
        foreach (var test in SummaryRequestCases)
            try { await SummaryRequestCase(test); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException) { mismatches.Add(test.Provider + "/" + test.Model + "/" + (test.Level ?? "-") + ": " + error.Message); }
        if (mismatches.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, mismatches));
        async Task SummaryRequestCase(SummaryRequestCase test)
        {
            var label = test.Provider + "/" + test.Model + "/" + (test.Level ?? "-");
            var endpoint = new LiveEndpoint(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("""{"error":{"message":"capture"}}""", Encoding.UTF8, "application/json") });
            var selection = LiveSessionSelection.Parse(test.Provider, test.Model, "8192");
            using var connection = selection.Connect(new(Env((test.KeyName, "test-key")), () => endpoint));
            var generator = new TransportSessionSummaryGenerator(request => connection.CreateSummaryTransport((int)request.MaximumOutputTokens, request.ThinkingLevel));
            var request = new SessionSummaryRequest(test.Level is null ? SessionSummaryKind.Branch : SessionSummaryKind.History, selection.Model,
                "SYS", "PROMPT", test.Maximum, test.Level, sessionId);
            try { await generator.GenerateAsync(request); throw new InvalidOperationException(label + " summary succeeded against a rejecting endpoint"); }
            catch (PiSharp.Sessions.Compaction.SessionCompactionException) { }
            var sent = endpoint.Snapshot().Single();
            Equal(test.Url, sent.Url, label + " url");
            Equal(test.Body, sent.Body, label + " body");
            Equal(test.Beta, sent.Headers.TryGetValue("anthropic-beta", out var beta) ? beta : null, label + " anthropic-beta");
            // cacheRetention "none": the routing session id never becomes a cache key or an affinity header.
            foreach (var name in new[] { "session_id", "x-client-request-id", "x-session-id", "x-session-affinity", "x-affinity" })
                Check(!sent.Headers.ContainsKey(name), label + " sent " + name);
        }
        // azure-openai-responses sends prompt_cache_key = sessionId whatever the cache retention: the summary's routing id.
        foreach (var (maximum, level, reasoning) in new[] { (8000, "high", ",\"reasoning\":{\"effort\":\"high\",\"summary\":\"auto\"},\"include\":[\"reasoning.encrypted_content\"]"), (4096, (string?)null, "") })
        {
            var azure = new LiveEndpoint(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
            var selection = LiveSessionSelection.Parse("azure", "gpt-5.4", "8192");
            using (var connection = selection.Connect(new(Env(("AZURE_OPENAI_API_KEY", "azure-key"), ("AZURE_OPENAI_RESOURCE_NAME", "pisharp-res")), () => azure)))
            {
                try { await new TransportSessionSummaryGenerator(request => connection.CreateSummaryTransport((int)request.MaximumOutputTokens, request.ThinkingLevel))
                    .GenerateAsync(new(SessionSummaryKind.History, selection.Model, "SYS", "PROMPT", maximum, level, sessionId)); }
                catch (PiSharp.Sessions.Compaction.SessionCompactionException) { }
            }
            var sent = azure.Snapshot().Single();
            Equal("https://pisharp-res.openai.azure.com/openai/v1/responses?api-version=v1", sent.Url, "azure summary url");
            Equal("{\"model\":\"gpt-5.4\",\"input\":[{\"role\":\"developer\",\"content\":\"SYS\"},{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"PROMPT\"}]}],\"stream\":true,\"prompt_cache_key\":\"" +
                sessionId + "\",\"store\":false,\"max_output_tokens\":" + maximum + reasoning + "}", sent.Body, "azure summary body " + (level ?? "-"));
        }
        // A model without reasoning drops the session level (model.reasoning gate), keeping the thinking-off summary request.
        var plain = new LiveEndpoint(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        var gpt41 = LiveSessionSelection.Parse("openai", "gpt-4.1", "8192");
        using (var connection = gpt41.Connect(new(Env(("OPENAI_API_KEY", "test-key")), () => plain)))
        {
            try { await new TransportSessionSummaryGenerator(request => connection.CreateSummaryTransport(4096, request.ThinkingLevel))
                .GenerateAsync(new(SessionSummaryKind.History, gpt41.Model, "SYS", "PROMPT", 4096, "high", sessionId)); }
            catch (PiSharp.Sessions.Compaction.SessionCompactionException) { }
        }
        Equal("""{"model":"gpt-4.1","input":[{"role":"system","content":"SYS"},{"role":"user","content":[{"type":"input_text","text":"PROMPT"}]}],"stream":true,"store":false,"max_output_tokens":4096}""",
            plain.Snapshot().Single().Body, "non-reasoning summary body");
    }
}
