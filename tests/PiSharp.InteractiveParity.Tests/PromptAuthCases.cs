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
