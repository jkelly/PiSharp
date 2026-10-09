// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/sdk.ts (createAgentSession: restore the branch's
// model, else modelFallbackMessage and findInitialModel), packages/coding-agent/src/core/virtual-models.ts (getBranchSelection) and
// packages/coding-agent/src/main.ts (buildSessionOptions: --model wins, scoped models only for a new session).
using System.Text.Json.Nodes;
using PiSharp.Cli.Models;
using PiSharp.Cli.Pi;

// A continued session (--session, -c) runs on its branch's model when that model exists and its provider has configured auth;
// otherwise on findInitialModel's pick, or on the unknown model when nothing is available. Expectations were checked against Pi 1.1.0
// run with node on the same session file.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ResumeModelCases() =>
    [
        ("resume.restores-the-branch-model-or-falls-back-like-sdk", async () =>
        {
            using var sandbox = new Sandbox("resume-model");
            var haiku = SeedSession(sandbox, "haiku.jsonl", "anthropic", "claude-haiku-4-5");
            // The provider has auth: the branch's model is restored (FindInitialModel alone would pick another anthropic model).
            Equal("anthropic/claude-haiku-4-5", await StateModel(sandbox, "--session", haiku), "restored");
            var (code, stdout, stderr) = await sandbox.Run("-p", "--session", haiku, "again");
            Check(code == 0 && stdout == "ok\n", $"print on the restored model: {code} {stdout} {stderr}");
            Equal("claude-haiku-4-5", sandbox.Requests[^1].Json.GetProperty("model").GetString(), "the request goes to the restored model");
            Equal(3, sandbox.Requests[^1].Json.GetProperty("messages").GetArrayLength(), "the history is continued");
            // --model wins over the session's model (buildSessionOptions sets options.model).
            Equal("anthropic/claude-sonnet-4-5", await StateModel(sandbox, "--session", haiku, "--provider", "anthropic", "--model", "claude-sonnet-4-5"), "--model");
            // A model that no longer exists: findInitialModel's anthropic default (Pi 1.1.0: anthropic/claude-opus-4-8).
            var gone = SeedSession(sandbox, "gone.jsonl", "anthropic", "claude-gone-9");
            Equal("anthropic/claude-opus-4-8", await StateModel(sandbox, "--session", gone), "unknown model falls back");
            // No auth for the session's provider: another provider's default (Pi 1.1.0: openai/gpt-5.5).
            sandbox.Vars.Remove("ANTHROPIC_API_KEY"); sandbox.Vars["OPENAI_API_KEY"] = "sk-openai";
            var other = SeedSession(sandbox, "other.jsonl", "anthropic", "claude-haiku-4-5");
            Equal("openai/gpt-5.5", await StateModel(sandbox, "--session", other), "no auth falls back");
            // Nothing available: the unknown model, whose prompt is refused.
            sandbox.Vars.Remove("OPENAI_API_KEY");
            var none = SeedSession(sandbox, "none.jsonl", "anthropic", "claude-haiku-4-5");
            Equal("unknown/unknown", await StateModel(sandbox, "--session", none), "nothing available");
            (code, stdout, stderr) = await sandbox.Run("-p", "--session", none, "again");
            Check(code == 1 && stdout == "" && stderr == ModelListing.NoApiKeyFoundMessage("unknown") + "\n", $"print without a model: {code} {stdout} {stderr}");
            Check(!File.ReadAllText(none).Contains("\"text\":\"again\"", StringComparison.Ordinal), "the refused prompt is not persisted");
        }),
        ("resume.switch-session-falls-back-to-find-initial-model", async () =>
        {
            using var sandbox = new Sandbox("resume-switch");
            var gone = SeedSession(sandbox, "gone.jsonl", "anthropic", "claude-gone-9");
            var haiku = SeedSession(sandbox, "haiku.jsonl", "anthropic", "claude-haiku-4-5");
            // rpc-mode.ts switch_session (and /resume) recreate the session as sdk.ts does: a branch model that is not available falls
            // back to findInitialModel as a continued session, not to the model in use (Pi 1.1.0: claude-opus-4-8 after haiku was set).
            var responses = await RpcSequence(sandbox, ["--mode", "rpc"],
                """{"id":"1","type":"set_model","provider":"anthropic","modelId":"claude-haiku-4-5"}""",
                """{"id":"2","type":"switch_session","sessionPath":""" + JsonValue.Create(gone)!.ToJsonString() + "}",
                """{"id":"3","type":"get_state"}""",
                """{"id":"4","type":"switch_session","sessionPath":""" + JsonValue.Create(haiku)!.ToJsonString() + "}",
                """{"id":"5","type":"get_state"}""");
            Check(responses[1]["success"]!.GetValue<bool>(), "switched: " + responses[1].ToJsonString());
            Equal("claude-opus-4-8", responses[2]["data"]!["model"]!["id"]!.GetValue<string>(), "unavailable model falls back");
            Equal("claude-haiku-4-5", responses[4]["data"]!["model"]!["id"]!.GetValue<string>(), "available model restored");
        }),
    ];

    /// <summary>RPC commands sent one at a time, each after the previous response; the responses in order.</summary>
    private static async Task<List<JsonNode>> RpcSequence(Sandbox sandbox, string[] args, params string[] commands)
    {
        var input = new ScriptedInput(); var responses = new List<JsonNode>();
        using var output = new LineOutput(line =>
        {
            if (line["type"]?.GetValue<string>() != "response") return;
            int count; lock (responses) { responses.Add(line); count = responses.Count; }
            if (count < commands.Length) input.Send(commands[count]); else input.Complete();
        });
        input.Send(commands[0]);
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        Equal(0, await PiCommand.RunAsync(args, host, CancellationToken.None), "rpc exit; " + stderr);
        return responses;
    }

    /// <summary>A continued session in the sandbox's cwd: one exchange on <paramref name="provider"/>/<paramref name="model"/>.</summary>
    private static string SeedSession(Sandbox sandbox, string name, string provider, string model)
    {
        var cwd = JsonValue.Create(sandbox.Cwd)!.ToJsonString();
        return sandbox.Write(Path.Combine(PiSessions.DefaultSessionDirectoryPath(sandbox.Cwd, sandbox.AgentDir), name), string.Join("\n",
            "{\"type\":\"session\",\"version\":3,\"id\":\"01a00000-0000-7000-8000-" + Guid.NewGuid().ToString("N")[..12] + "\",\"timestamp\":\"2026-10-09T10:00:00.000Z\",\"cwd\":" + cwd + "}",
            "{\"type\":\"model_change\",\"id\":\"a1\",\"parentId\":null,\"timestamp\":\"2026-10-09T10:00:00.001Z\",\"provider\":\"" + provider + "\",\"modelId\":\"" + model + "\"}",
            "{\"type\":\"thinking_level_change\",\"id\":\"a2\",\"parentId\":\"a1\",\"timestamp\":\"2026-10-09T10:00:00.002Z\",\"thinkingLevel\":\"off\"}",
            "{\"type\":\"message\",\"id\":\"a3\",\"parentId\":\"a2\",\"timestamp\":\"2026-10-09T10:00:00.003Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"first\"}],\"timestamp\":1}}",
            "{\"type\":\"message\",\"id\":\"a4\",\"parentId\":\"a3\",\"timestamp\":\"2026-10-09T10:00:00.004Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"api\":\"anthropic-messages\",\"provider\":\"" + provider + "\",\"model\":\"" + model + "\",\"usage\":{\"input\":3,\"output\":2,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":5,\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}},\"stopReason\":\"stop\",\"timestamp\":2}}") + "\n");
    }

    /// <summary>The provider/id of the model get_state reports for an RPC run with <paramref name="args"/>.</summary>
    private static async Task<string> StateModel(Sandbox sandbox, params string[] args)
    {
        var input = new ScriptedInput();
        JsonNode? state = null;
        using var output = new LineOutput(line =>
        {
            if (line["type"]?.GetValue<string>() == "response" && line["id"]?.GetValue<string>() == "s") { state = line; input.Complete(); }
        });
        input.Send("""{"id":"s","type":"get_state"}""");
        using var stdout = new StringWriter(); using var stderr = new StringWriter();
        var host = sandbox.Host(stdout, stderr, null, rpcInput: input, rpcOutput: output) with { StdoutIsTty = false };
        var code = await PiCommand.RunAsync(["--mode", "rpc", .. args], host, CancellationToken.None);
        Check(code == 0 && state is not null, $"rpc {string.Join(' ', args)}: {code} {stderr}");
        var model = state!["data"]!["model"]!;
        return model["provider"]!.GetValue<string>() + "/" + model["id"]!.GetValue<string>();
    }
}
