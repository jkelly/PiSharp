using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using System.Text.Json.Nodes;
using PiSharp.Cli.Commands;

internal static class SkillCliHostTests
{
    internal const string Prefix = "skill CLI host ";
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "actual one-shot selected content reaches model and repeat resume does not duplicate descriptions", OneShot),
        (Prefix + "actual RPC command catalog stays inert until explicit prompt", RpcCatalog),
        (Prefix + "skill-free create then selected repeat resume replaces one durable catalog", FromNoSkills),
        (Prefix + "RPC A to B then no skills withdraws old descriptors", RpcTransitions)
    ];
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Skill host contract failed."); }
    private static async Task<string> Skill(StartupSettingsTests.Fixture fixture)
    {
        var path = Path.Combine(fixture.Root, "fixture.md");
        await File.WriteAllTextAsync(path, "---\nname: host-review\ndescription: Explicit host skill instructions\n---\nFollow actual host skill instructions.");
        return path;
    }
    private static async Task OneShot()
    {
        using var fixture = new StartupSettingsTests.Fixture(); var skill = await Skill(fixture);
        var script = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Script))!;
        script["turns"]![0]!["requiredInputTexts"] = new JsonArray(JsonValue.Create("available_skills"), JsonValue.Create("Follow actual host skill instructions."));
        await File.WriteAllTextAsync(fixture.Script, script.ToJsonString());
        var path = Path.Combine(fixture.Root, "session.jsonl");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (var output = new StringWriter()) using (var error = new StringWriter())
        {
            Check(await SessionCommands.RunAsync(["session", "create", "--session", path, "--workspace", fixture.Root,
                "--skill", skill], output, error, stop.Token) == 0); Check(error.ToString() == "");
        }
        for (var i = 0; i < 2; i++)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Check(await SessionCommands.RunAsync(["session", "resume", "--session", path, "--workspace", fixture.Root,
                "--offline-script", fixture.Script, "--message", "/skill:host-review user request", "--skill", skill], output, error, stop.Token) == 0);
            Check(error.ToString() == ""); using var report = JsonDocument.Parse(output.ToString());
            Check(report.RootElement.GetProperty("requests").GetArrayLength() == 1);
            Check(report.RootElement.GetProperty("requests")[0].GetProperty("historyRequirementsSatisfied").GetBoolean());
        }
        await CheckCatalog(path, "host-review");
    }
    private static async Task CheckCatalog(string path, string? name, string? absent = null)
    {
        var messages = ImmutableArray.CreateBuilder<TranscriptEntry>();
        foreach (var line in await File.ReadAllLinesAsync(path))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("role", out var role))
                messages.Add(new(role.GetString()!, JsonData.FromElement(message)));
        }
        var replay = new SessionSystemReplay().Replay(messages.ToImmutable());
        Check(replay.Prompt.Split("<available_skills>", StringSplitOptions.None).Length == (name is null ? 1 : 2));
        if (name is not null) Check(replay.Prompt.Contains("<name>" + name + "</name>", StringComparison.Ordinal));
        if (absent is not null) Check(!replay.Prompt.Contains("<name>" + absent + "</name>", StringComparison.Ordinal));
        // All updates after creation are section-only, so unrelated base instructions are never copied.
        Check(messages.Where(message => message.Role == "system" && message.WireBody.Value.GetProperty("content").GetString()!.Length > 0).Count() == 1);
    }
    private static async Task FromNoSkills()
    {
        using var fixture = new StartupSettingsTests.Fixture(); var skill = await Skill(fixture);
        var path = Path.Combine(fixture.Root, "session.jsonl");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using (var output = new StringWriter()) using (var error = new StringWriter())
        {
            Check(await SessionCommands.RunAsync(["session", "create", "--session", path, "--workspace", fixture.Root], output, error, stop.Token) == 0);
            Check(error.ToString() == "");
        }
        var script = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Script))!;
        script["turns"]![0]!["requiredInputTexts"] = new JsonArray(JsonValue.Create("available_skills"), JsonValue.Create("Follow actual host skill instructions."));
        await File.WriteAllTextAsync(fixture.Script, script.ToJsonString());
        for (var i = 0; i < 2; i++)
        {
            using var output = new StringWriter(); using var error = new StringWriter();
            Check(await SessionCommands.RunAsync(["session", "resume", "--session", path, "--workspace", fixture.Root,
                "--offline-script", fixture.Script, "--message", "/skill:host-review user request", "--skill", skill], output, error, stop.Token) == 0);
            Check(error.ToString() == ""); using var report = JsonDocument.Parse(output.ToString());
            Check(report.RootElement.GetProperty("requests")[0].GetProperty("historyRequirementsSatisfied").GetBoolean());
            await CheckCatalog(path, "host-review");
        }
    }
    private static async Task RpcTransitions()
    {
        using var fixture = new StartupSettingsTests.Fixture(); var skillA = await Skill(fixture);
        var skillB = Path.Combine(fixture.Root, "replacement.md");
        await File.WriteAllTextAsync(skillB, "---\nname: host-replacement\ndescription: Replacement catalog only\n---\nReplacement instructions.");
        var path = Path.Combine(fixture.Root, "session.jsonl");
        using (var output = new StringWriter()) using (var error = new StringWriter())
            Check(await SessionCommands.RunAsync(["session", "create", "--session", path, "--workspace", fixture.Root, "--skill", skillA], output, error) == 0);
        await using (var host = new StartupSettingsTests.Host(fixture, null, ["--skill", skillB], "open"))
        {
            var commands = (await host.Response("catalog", "get_commands")).Value.GetProperty("data").GetProperty("commands");
            Check(commands.EnumerateArray().Any(row => row.GetProperty("name").GetString() == "skill:host-replacement"));
            Check(!commands.EnumerateArray().Any(row => row.GetProperty("name").GetString() == "skill:host-review"));
            await host.MaterializeConversation(); Check(await host.Finish() == 0); Check(host.Error.ToString() == "");
        }
        await CheckCatalog(path, "host-replacement", "host-review");
        await using (var host = new StartupSettingsTests.Host(fixture, null, [], "open"))
        { await host.MaterializeConversation(); Check(await host.Finish() == 0); Check(host.Error.ToString() == ""); }
        await CheckCatalog(path, null, "host-replacement");
    }
    private static async Task RpcCatalog()
    {
        using var fixture = new StartupSettingsTests.Fixture(); var skill = await Skill(fixture);
        await using var host = new StartupSettingsTests.Host(fixture, null, ["--skill", skill]);
        var response = await host.Response("skills", "get_commands");
        var commands = response.Value.GetProperty("data").GetProperty("commands");
        Check(commands.EnumerateArray().Any(row => row.GetProperty("name").GetString() == "skill:host-review" && row.GetProperty("source").GetString() == "skill"));
        Check(!File.Exists(Path.Combine(fixture.Root, "session.jsonl")));
        Check(await host.Finish() == 0); Check(host.Error.ToString() == "");
    }
}