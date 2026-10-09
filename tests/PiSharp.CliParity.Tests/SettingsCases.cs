using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

// core/settings-manager.ts: global <agentDir>/settings.json and trusted project .pi/settings.json, migrateSettings, deepMergeSettings,
// mergeDefaultTools, getters and core/settings-diagnostics.ts.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SettingsCases() =>
    [
        ("settings.discovery-merge-project-over-global-nested-objects", Sync(() =>
        {
            using var sandbox = new Sandbox("settings-merge");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"theme":"dark","compaction":{"enabled":false,"reserveTokens":1000},"defaultTools":["read","bash"],"sessionDir":"~/sessions","defaultProjectTrust":"never"}""");
            sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "settings.json"), "﻿" + """{"theme":"light","compaction":{"reserveTokens":2000},"defaultTools":["+grep","-bash"],"defaultProjectTrust":"always","toolPolicy":"explicit"}""");
            var settings = PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, projectTrusted: true);
            Equal("light", settings.Theme, "project theme wins");
            Equal("""{"enabled":false,"reserveTokens":2000}""", PiJson.Stringify(settings.Merged["compaction"]), "nested merge");
            Equal("""["read","bash","+grep","-bash"]""", PiJson.Stringify(settings.Merged["defaultTools"]), "modifier lists append");
            Equal(Path.Combine(sandbox.Home, "sessions"), settings.SessionDir(sandbox.Home), "sessionDir tilde");
            Equal("never", settings.DefaultProjectTrust, "defaultProjectTrust is global only");
            Equal("explicit", settings.ToolPolicy, "toolPolicy");
            Equal(0, settings.Errors.Count, "no errors (BOM stripped)");
            var untrusted = PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, projectTrusted: false);
            Equal("dark", untrusted.Theme, "untrusted project layer ignored");
            Equal(null, untrusted.ToolPolicy, "no tool policy without the project layer");
            var replaced = PiSettings.Merge(JsonNode.Parse("""{"defaultTools":["read"]}""")!.AsObject(), JsonNode.Parse("""{"defaultTools":["ls","+grep"]}""")!.AsObject());
            Equal("""["ls","+grep"]""", PiJson.Stringify(replaced["defaultTools"]), "plain names replace");
            Equal("hidden", PiSettings.FromObjects(JsonNode.Parse("""{"theme":"a/b"}""")!.AsObject()).Theme ?? "hidden", "a theme path is not a theme name");
        })),
        ("settings.migrations-queue-websockets-skills-retry", Sync(() =>
        {
            var migrated = PiSettings.Migrate(JsonNode.Parse("""{"queueMode":"all","websockets":true,"skills":{"enableSkillCommands":false,"customDirectories":["~/skills"]},"retry":{"maxDelayMs":5000,"provider":{"timeoutMs":10}}}""")!.AsObject());
            Equal("""{"websockets":true,"skills":["~/skills"],"retry":{"provider":{"timeoutMs":10,"maxRetryDelayMs":5000}},"steeringMode":"all","transport":"websocket","enableSkillCommands":false}""".Replace("\"websockets\":true,", ""),
                PiJson.Stringify(migrated), "migrated settings");
            var kept = PiSettings.Migrate(JsonNode.Parse("""{"queueMode":"all","steeringMode":"one-at-a-time","websockets":false,"transport":"sse","skills":{"customDirectories":[]},"retry":{"maxDelayMs":1,"provider":{"maxRetryDelayMs":7}}}""")!.AsObject());
            Equal("""{"queueMode":"all","steeringMode":"one-at-a-time","websockets":false,"transport":"sse","retry":{"provider":{"maxRetryDelayMs":7}}}""", PiJson.Stringify(kept), "existing values win");
        })),
        ("settings.invalid-file-diagnostic-and-entry-warning", async () =>
        {
            using var sandbox = new Sandbox("settings-invalid");
            var path = sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), "{ not json");
            var settings = PiSettings.Load(sandbox.Cwd, sandbox.AgentDir, projectTrusted: true);
            var diagnostic = settings.DrainDiagnostics().Single();
            Check(diagnostic.Type == "warning" && diagnostic.Message.StartsWith($"Invalid settings file {path}: ", StringComparison.Ordinal), "diagnostic text: " + diagnostic.Message);
            Equal(0, settings.DrainDiagnostics().Length, "drained once");
            var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "hi");
            Equal(0, code, "a broken settings file does not stop the run; " + stderr);
            Equal("ok\n", stdout, "answer");
            Check(stderr.StartsWith($"Warning: Invalid settings file {path}: ", StringComparison.Ordinal) && stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 1,
                "one deduplicated warning: " + stderr);
        }),
        ("settings.default-model-thinking-and-session-dir-through-the-cli", async () =>
        {
            using var sandbox = new Sandbox("settings-cli");
            sandbox.Write(Path.Combine(sandbox.AgentDir, "settings.json"), """{"defaultProvider":"anthropic","defaultModel":"claude-haiku-4-5","defaultThinkingLevel":"off","sessionDir":"custom-sessions"}""");
            var (code, _, stderr) = await sandbox.Run("-p", "hi");
            Equal(0, code, "exit; " + stderr);
            var body = sandbox.Requests[0].Json;
            Equal("claude-haiku-4-5", body.GetProperty("model").GetString(), "settings default model");
            Check(!body.TryGetProperty("thinking", out var thinking) || thinking.GetProperty("type").GetString() == "disabled", "thinking off");
            var custom = Path.Combine(sandbox.Cwd, "custom-sessions");
            Equal(1, Directory.GetFiles(custom, "*.jsonl").Length, "sessionDir setting (relative to the cwd) holds the session");
            Equal(0, sandbox.SessionFiles().Length, "nothing under the default sessions directory");
        }),
    ];
}
