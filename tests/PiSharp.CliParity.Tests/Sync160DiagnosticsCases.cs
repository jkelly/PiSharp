// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/model-fallback-diagnostic.test.ts,
// test/model-scope-session-replacement.test.ts and test/rpc-replacement-diagnostics.test.ts over packages/coding-agent/src/main.ts
// (createRuntime diagnostics), src/modes/diagnostics-stderr.ts, src/modes/rpc/rpc-mode.ts and src/modes/print-mode.ts
// (setRebindSession: takeUnreportedDiagnostics to stderr), and src/cli/args.ts (the Qwen Token Plan key variables in --help).
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Pi;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160DiagnosticsCases() =>
    [
        // "Could not restore model" was shown only at interactive startup. A /resume into a session whose model is gone fell back to
        // another model silently, and other modes never showed it (found while fixing #11264).
        ("sync160.diagnostics.model-fallback-is-written-to-stderr-when-rpc-switch-session-falls-back", async () =>
        {
            using var sandbox = new Sandbox("sync160-fallback-switch");
            var gone = SeedSession(sandbox, "gone.jsonl", "anthropic", "claude-gone-9");
            var haiku = SeedSession(sandbox, "haiku.jsonl", "anthropic", "claude-haiku-4-5");
            var (responses, stdout, stderr) = await Sync160Rpc(sandbox, ["--mode", "rpc", "--no-session"], null,
                """{"id":"1","type":"switch_session","sessionPath":""" + JsonValue.Create(haiku)!.ToJsonString() + "}",
                """{"id":"2","type":"switch_session","sessionPath":""" + JsonValue.Create(gone)!.ToJsonString() + "}",
                """{"id":"3","type":"get_state"}""",
                """{"id":"4","type":"switch_session","sessionPath":""" + JsonValue.Create(haiku)!.ToJsonString() + "}",
                """{"id":"5","type":"switch_session","sessionPath":""" + JsonValue.Create(gone)!.ToJsonString() + "}");
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "every command succeeded: " + Sync160Failed(responses));
            var model = responses[2]["data"]!["model"]!;
            var expected = $"Warning: Could not restore model anthropic/claude-gone-9. Using {model["provider"]!.GetValue<string>()}/{model["id"]!.GetValue<string>()}\n";
            // A session whose model is restored reports nothing; the same fallback is not repeated for a later replacement.
            Equal(expected, stderr, "stderr");
            Check(!stdout.Contains("Could not restore model", StringComparison.Ordinal), "nothing on stdout, which carries the RPC protocol");
        }),
        ("sync160.diagnostics.model-fallback-is-written-to-stderr-after-rpc-fork", async () =>
        {
            using var sandbox = new Sandbox("sync160-fallback-fork");
            var gone = SeedSession(sandbox, "gone.jsonl", "anthropic", "claude-gone-9");
            // The startup fallback is reported at startup; the fork of the same branch falls back the same way, which is the same
            // diagnostic and so is not repeated.
            var (responses, stdout, stderr) = await Sync160Rpc(sandbox, ["--mode", "rpc", "--session", gone], null,
                """{"id":"1","type":"get_state"}""", """{"id":"2","type":"fork","entryId":"a3"}""", """{"id":"3","type":"get_state"}""");
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "every command succeeded: " + Sync160Failed(responses));
            var model = responses[0]["data"]!["model"]!;
            Equal($"Warning: Could not restore model anthropic/claude-gone-9. Using {model["provider"]!.GetValue<string>()}/{model["id"]!.GetValue<string>()}\n", stderr, "stderr");
        }),
        ("sync160.diagnostics.model-fallback-is-reported-at-startup-in-every-mode", async () =>
        {
            using var sandbox = new Sandbox("sync160-fallback-startup");
            var gone = SeedSession(sandbox, "gone.jsonl", "anthropic", "claude-gone-9");
            var model = await StateModel(sandbox, "--session", gone);
            var warning = $"Warning: Could not restore model anthropic/claude-gone-9. Using {model}\n";
            var (code, stdout, stderr) = await sandbox.Run("-p", "--session", gone, "again");
            Check(code == 0 && stdout == "ok\n", $"print: {code} {stdout} {stderr}");
            Equal(warning, stderr, "print mode stderr");
            // The fallback model's answer is the branch's selection from then on, so each run continues its own copy.
            (code, stdout, stderr) = await sandbox.Run("--mode", "json", "--session", SeedSession(sandbox, "gone-json.jsonl", "anthropic", "claude-gone-9"), "again");
            Check(code == 0 && !stdout.Contains("Could not restore model", StringComparison.Ordinal), $"json: {code} {stderr}");
            Equal(warning, stderr, "json mode stderr");
            // A restored model and --model report nothing.
            var haiku = SeedSession(sandbox, "haiku.jsonl", "anthropic", "claude-haiku-4-5");
            (code, _, stderr) = await sandbox.Run("-p", "--session", haiku, "again");
            Check(code == 0 && stderr == "", $"restored model: {code} {stderr}");
            (code, _, stderr) = await sandbox.Run("-p", "--session", SeedSession(sandbox, "gone-model.jsonl", "anthropic", "claude-gone-9"), "--provider", "anthropic", "--model", "claude-sonnet-4-5", "again");
            Check(code == 0 && stderr == "", $"--model: {code} {stderr}");
            // Interactive mode gets it as a startup diagnostic (shown in the chat), no longer as the separate fallback message, which
            // stays for the session without any model (login guidance).
            PiEntryOptions? seen = null;
            using var interactiveOut = new StringWriter(); using var interactiveErr = new StringWriter();
            var host = sandbox.Host(interactiveOut, interactiveErr, null, interactive: true, runInteractive: (_, options, _) => { seen = options; return Task.FromResult(0); });
            gone = SeedSession(sandbox, "gone-interactive.jsonl", "anthropic", "claude-gone-9");
            Equal(0, await PiCommand.RunAsync(["--session", gone], host, CancellationToken.None), "interactive exit; " + interactiveErr);
            Check(seen is not null, "interactive mode ran");
            Check(seen!.StartupDiagnostics.Any(diagnostic => diagnostic.Type == "warning" && "Warning: " + diagnostic.Message + "\n" == warning),
                "startup diagnostics: " + string.Join(" | ", seen.StartupDiagnostics.Select(diagnostic => diagnostic.Message)));
            Check(seen.Selection.FallbackMessage is null, "no separate fallback message: " + seen.Selection.FallbackMessage);
            Equal("", interactiveErr.ToString(), "interactive stderr");
            Equal(0, seen.RuntimeDiagnostics.TakeUnreported().Length, "the startup diagnostics were taken");
            sandbox.Vars.Remove("ANTHROPIC_API_KEY");
            seen = null;
            Equal(0, await PiCommand.RunAsync(["--session", gone], host, CancellationToken.None), "interactive exit without a model; " + interactiveErr);
            Check(seen!.Selection.FallbackMessage is { Length: > 0 } && !seen.StartupDiagnostics.Any(diagnostic => diagnostic.Message.Contains("Could not restore", StringComparison.Ordinal)),
                "without any model the message is login guidance, not a diagnostic");
        }),
        // #11264: the runtime factory runs again on /new, /resume and /fork. The model scope warnings are startup diagnostics,
        // reported once.
        ("sync160.diagnostics.unmatched-enabled-models-pattern-is-reported-once-not-again-on-new-session", async () =>
        {
            using var sandbox = new Sandbox("sync160-scope-once");
            Sync160Settings(sandbox, "anthropic/claude-sonnet-4-5", "github-copilot/missing-model");
            var haiku = SeedSession(sandbox, "haiku.jsonl", "anthropic", "claude-haiku-4-5");
            var (responses, stdout, stderr) = await Sync160Rpc(sandbox, ["--mode", "rpc"], null,
                """{"id":"1","type":"new_session"}""",
                """{"id":"2","type":"switch_session","sessionPath":""" + JsonValue.Create(haiku)!.ToJsonString() + "}",
                """{"id":"3","type":"fork","entryId":"a3"}""",
                """{"id":"4","type":"new_session"}""");
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "every command succeeded: " + Sync160Failed(responses));
            Equal("Warning: No models match pattern \"github-copilot/missing-model\"\n", stderr, "stderr");
        }),
        // The runtime factory runs again on every new_session, but only the startup diagnostics were reported, so a problem that
        // first appeared on replacement went nowhere (found while fixing #11264).
        ("sync160.diagnostics.a-diagnostic-that-first-appears-on-rpc-new-session-is-written-to-stderr-once", async () =>
        {
            using var sandbox = new Sandbox("sync160-replacement-diagnostic");
            Sync160Settings(sandbox, "anthropic/claude-sonnet-4-5");
            var (responses, stdout, stderr) = await Sync160Rpc(sandbox, ["--mode", "rpc", "--no-session"], answered =>
            {
                if (answered == 1) Sync160Settings(sandbox, "anthropic/claude-sonnet-4-5", "github-copilot/missing-model");
            }, """{"id":"1","type":"new_session"}""", """{"id":"2","type":"new_session"}""", """{"id":"3","type":"new_session"}""");
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "every command succeeded: " + Sync160Failed(responses));
            Equal(1, Regex.Matches(stderr, "Warning: No models match pattern \"github-copilot/missing-model\"").Count, "reported once: " + stderr);
            Equal("Warning: No models match pattern \"github-copilot/missing-model\"\n", stderr, "stderr");
            Check(!stdout.Contains("No models match pattern", StringComparison.Ordinal), "nothing on stdout");
        }),
        ("sync160.diagnostics.a-settings-error-that-first-appears-on-replacement-is-written-to-stderr-once", async () =>
        {
            using var sandbox = new Sandbox("sync160-replacement-settings");
            var (responses, stdout, stderr) = await Sync160Rpc(sandbox, ["--mode", "rpc", "--no-session"], answered =>
            {
                if (answered == 1) sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), "{ not json");
            }, """{"id":"1","type":"new_session"}""", """{"id":"2","type":"new_session"}""", """{"id":"3","type":"new_session"}""");
            Check(responses.All(response => response["success"]!.GetValue<bool>()), "every command succeeded: " + Sync160Failed(responses));
            var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check(lines.Length == 1 && lines[0].StartsWith("Warning: ", StringComparison.Ordinal) && lines[0].Contains("settings", StringComparison.OrdinalIgnoreCase),
                "one settings warning: " + stderr);
        }),
        ("sync160.diagnostics.stderr-lines-follow-diagnostics-stderr", () =>
        {
            Equal("Error: e", PiDiagnosticsStderr.Format("error", "e", false), "error");
            Equal("Warning: w", PiDiagnosticsStderr.Format("warning", "w", false), "warning");
            Equal("i", PiDiagnosticsStderr.Format("info", "i", false), "info");
            Equal("\u001b[31mError: e\u001b[39m", PiDiagnosticsStderr.Format("error", "e", true), "red error");
            Equal("\u001b[33mWarning: w\u001b[39m", PiDiagnosticsStderr.Format("warning", "w", true), "yellow warning");
            Equal("\u001b[2mi\u001b[22m", PiDiagnosticsStderr.Format("info", "i", true), "dim info");
            return Task.CompletedTask;
        }),
        ("sync160.help.names-the-qwen-token-plan-team-and-individual-key-variables", () =>
        {
            var help = PiHelp.Text();
            foreach (var line in new[]
            {
                "  QWEN_TOKEN_PLAN_TEAM_API_KEY     - Qwen Token Plan Team API key (international region)",
                "  QWEN_TOKEN_PLAN_TEAM_CN_API_KEY  - Qwen Token Plan Team API key (China region)",
                "  QWEN_TOKEN_PLAN_INDIVIDUAL_API_KEY - Qwen Token Plan Individual API key (international region)",
                "  QWEN_TOKEN_PLAN_INDIVIDUAL_CN_API_KEY - Qwen Token Plan Individual API key (China region)"
            }) Check(help.Replace("\r\n", "\n", StringComparison.Ordinal).Contains(line + "\n", StringComparison.Ordinal), "help line: " + line);
            Check(!help.Contains("  QWEN_TOKEN_PLAN_API_KEY ", StringComparison.Ordinal) && !help.Contains("  QWEN_TOKEN_PLAN_CN_API_KEY ", StringComparison.Ordinal), "the shared variables are no longer listed");
            return Task.CompletedTask;
        }),
    ];

    private static string Sync160Failed(List<JsonNode> responses) =>
        string.Join(" | ", responses.Where(response => !response["success"]!.GetValue<bool>()).Select(response => response.ToJsonString()));

    private static void Sync160Settings(Sandbox sandbox, params string[] enabledModels) =>
        sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), new JsonObject
        {
            ["defaultProvider"] = "anthropic", ["defaultModel"] = "claude-sonnet-4-5",
            ["enabledModels"] = new JsonArray([.. enabledModels.Select(pattern => (JsonNode)pattern)])
        }.ToJsonString());

    /// <summary>RPC commands sent one at a time, each after the previous response (<paramref name="answered"/> runs in between with the
    /// number of responses so far); the responses, everything written to the RPC output and everything written to stderr.</summary>
    private static async Task<(List<JsonNode> Responses, string Stdout, string Stderr)> Sync160Rpc(Sandbox sandbox, string[] args, Action<int>? answered, params string[] commands)
    {
        var input = new ScriptedInput(); var responses = new List<JsonNode>(); var all = new System.Text.StringBuilder();
        using var output = new LineOutput(line =>
        {
            lock (all) all.Append(line.ToJsonString()).Append('\n');
            if (line["type"]?.GetValue<string>() != "response") return;
            int count; lock (responses) { responses.Add(line); count = responses.Count; }
            answered?.Invoke(count);
            if (count < commands.Length) input.Send(commands[count]); else input.Complete();
        });
        input.Send(commands[0]);
        using var stdout = new StringWriter { NewLine = "\n" }; using var stderr = new StringWriter { NewLine = "\n" };
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        Equal(0, await PiCommand.RunAsync(args, host, CancellationToken.None), "rpc exit; " + stderr);
        return (responses, all.ToString(), stderr.ToString());
    }
}
