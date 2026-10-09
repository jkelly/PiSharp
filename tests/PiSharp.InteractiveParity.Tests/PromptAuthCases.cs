// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session.ts (prompt: model and auth
// validation), packages/coding-agent/src/core/sdk.ts (a session without a model) and packages/coding-agent/src/modes/interactive/
// interactive-mode.ts (the prompt error is shown, the editor keeps working; /login and /model).
using static Expect;

/// <summary>Interactive mode starts without credentials or without any model; prompts are refused with upstream's texts until /login
/// (and /model) make one usable, and the next prompt then runs.</summary>
internal static class PromptAuthCases
{
    private static void LoginHost(InteractiveHarness pi) => pi.Configure = context => context with
    {
        Login = new PiSharp.Cli.Authentication.ProviderLoginHost(
            new PiSharp.Cli.Authentication.AuthJsonCredentialStore(Path.Combine(pi.AgentDir, "auth.json")), () => new HttpClient())
    };

    /// <summary>/login anthropic with an API key, as a user would type it.</summary>
    private static async Task LoginAnthropicAsync(InteractiveHarness pi)
    {
        pi.Type("/login anthropic");
        await Task.Delay(300);
        pi.Type("\r");
        await Task.Delay(300);
        pi.Type("\r");
        await pi.WaitFor("Select authentication method for Anthropic:");
        pi.Type("\u001b[B");
        await Task.Delay(100);
        pi.Type("\r");
        await pi.WaitFor("Enter Anthropic API key");
        await Task.Delay(100);
        pi.Type("sk-after-login");
        pi.Type("\r");
        await pi.WaitFor("Saved API key for Anthropic");
    }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // bug-report.ts: summarizeForBugReport finds no auth, the request fails in model-runtime.ts prepareRequest and
        // getSummarizationFailure names it; the /bug flow shows "Failed to write bug report summary: …".
        yield return ("e2e.prompt-auth.bug-summary-without-key", async () =>
        {
            await using var pi = new InteractiveHarness("bug-no-key");
            pi.Vars.Remove("ANTHROPIC_API_KEY");
            pi.Start("--provider", "anthropic", "--model", "claude-haiku-4-5", "--tui-mode", "regular");
            await pi.WaitFor("escape interrupt");
            await pi.Submit("/bug");
            await pi.WaitFor("What went wrong?");
            pi.Type("summary without credentials");
            pi.Type("\r");
            await pi.WaitFor("Include the session transcript?");
            pi.Type("\u001b[B");
            await Task.Delay(100);
            pi.Type("\r");
            await pi.WaitFor("Attach a summary written by");
            pi.Type("\r");
            await pi.WaitFor("Upload sends the report");
            pi.Type("\u001b[B");
            await Task.Delay(100);
            pi.Type("\r");
            await pi.WaitUntil(text => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Contains("Failed to write bug report summary: Bug report summary failed: Provider is not configured: anthropic", StringComparison.Ordinal), "summary error");
            Check(Directory.GetFiles(pi.Cwd, "pi-bug-report-*.zip").Length == 0, "no report written");
            Equal(0, pi.Requests.Count, "nothing sent");
        });

        // sdk.ts: a continued session whose model's provider has no auth runs on findInitialModel's pick, and interactive mode shows
        // modelFallbackMessage (Pi 1.1.0 picks openai/gpt-5.5 with only an OpenAI key).
        yield return ("e2e.prompt-auth.continue-falls-back-with-a-warning", async () =>
        {
            await using var pi = new InteractiveHarness("continue-fallback");
            pi.Vars.Remove("ANTHROPIC_API_KEY"); pi.Vars["OPENAI_API_KEY"] = "sk-openai";
            var cwd = System.Text.Json.Nodes.JsonValue.Create(pi.Cwd)!.ToJsonString();
            pi.Write(Path.Combine(PiSharp.Cli.Pi.PiSessions.DefaultSessionDirectoryPath(pi.Cwd, pi.AgentDir), "2026-10-09T10-00-00-000Z_seed.jsonl"), string.Join("\n",
                "{\"type\":\"session\",\"version\":3,\"id\":\"01a00000-0000-7000-8000-000000000001\",\"timestamp\":\"2026-10-09T10:00:00.000Z\",\"cwd\":" + cwd + "}",
                "{\"type\":\"model_change\",\"id\":\"a1\",\"parentId\":null,\"timestamp\":\"2026-10-09T10:00:00.001Z\",\"provider\":\"anthropic\",\"modelId\":\"claude-haiku-4-5\"}",
                "{\"type\":\"thinking_level_change\",\"id\":\"a2\",\"parentId\":\"a1\",\"timestamp\":\"2026-10-09T10:00:00.002Z\",\"thinkingLevel\":\"off\"}",
                "{\"type\":\"message\",\"id\":\"a3\",\"parentId\":\"a2\",\"timestamp\":\"2026-10-09T10:00:00.003Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"seeded question\"}],\"timestamp\":1}}",
                "{\"type\":\"message\",\"id\":\"a4\",\"parentId\":\"a3\",\"timestamp\":\"2026-10-09T10:00:00.004Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"seeded answer\"}],\"api\":\"anthropic-messages\",\"provider\":\"anthropic\",\"model\":\"claude-haiku-4-5\",\"usage\":{\"input\":3,\"output\":2,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":5,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}},\"stopReason\":\"stop\",\"timestamp\":2}}") + "\n");
            pi.Start("-c", "--tui-mode", "regular");
            await pi.WaitFor("escape interrupt");
            await pi.WaitFor("Could not restore model anthropic/claude-haiku-4-5. Using openai/gpt-5.5");
            await pi.WaitUntil(text => text.Contains("gpt-5.5", StringComparison.Ordinal) && text.Contains("seeded answer", StringComparison.Ordinal), "fallback model and the continued history");
        });

        yield return ("e2e.prompt-auth.selected-model-without-key-login-then-prompt", async () =>
        {
            await using var pi = new InteractiveHarness("prompt-no-key");
            pi.Vars.Remove("ANTHROPIC_API_KEY");
            LoginHost(pi);
            pi.Start("--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular");
            await pi.WaitFor("escape interrupt");
            await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5", StringComparison.Ordinal), "footer shows the selected model");
            await pi.Submit("first try");
            await pi.WaitFor("No API key found for anthropic.");
            Equal(0, pi.Requests.Count, "nothing sent");
            await LoginAnthropicAsync(pi);
            await pi.Submit("second try");
            await pi.WaitFor("Hello from the fake model.");
            Equal(1, pi.Requests.Count, "one request after /login");
            Contains(pi.Requests[0], "second try", "the request carries the new prompt");
            Check(!pi.Requests[0].Contains("first try", StringComparison.Ordinal), "the refused prompt never reached the history");
        });

        yield return ("e2e.prompt-auth.no-model-start-login-and-model-command", async () =>
        {
            await using var pi = new InteractiveHarness("prompt-no-model");
            pi.Vars.Remove("ANTHROPIC_API_KEY");
            LoginHost(pi);
            pi.Start("--tui-mode", "regular");
            await pi.WaitFor("escape interrupt");
            // sdk.ts modelFallbackMessage: formatNoModelsAvailableMessage, shown as a warning.
            await pi.WaitFor("No models available. Use /login to log into a provider via OAuth or API key.");
            await pi.Submit("first try");
            // The session keeps the Agent's DEFAULT_MODEL (provider "unknown"): formatNoApiKeyFoundMessage("unknown").
            await pi.WaitFor("No API key found for the selected model.");
            Equal(0, pi.Requests.Count, "nothing sent");
            await LoginAnthropicAsync(pi);
            pi.Type("/model claude-sonnet-4-5");
            await Task.Delay(300);
            pi.Type("\r");
            await Task.Delay(300);
            pi.Type("\r");
            await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5 •", StringComparison.Ordinal), "footer shows the selected model");
            await pi.Submit("second try");
            await pi.WaitFor("Hello from the fake model.");
            Equal(1, pi.Requests.Count, "one request after /login and /model");
            Contains(pi.Requests[0], "\"model\":\"claude-sonnet-4-5\"", "the request goes to the selected model");
            Check(!pi.Requests[0].Contains("first try", StringComparison.Ordinal), "the refused prompt never reached the history");
            var file = pi.SessionFiles().Single();
            await pi.WaitUntil(_ => InteractiveHarness.ReadShared(file).Contains("\"modelId\":\"claude-sonnet-4-5\"", StringComparison.Ordinal), "model_change recorded");
            Check(!InteractiveHarness.ReadShared(file).Contains("\"modelId\":\"unknown\"", StringComparison.Ordinal), "no model_change for the unknown model");
        });
    }
}
