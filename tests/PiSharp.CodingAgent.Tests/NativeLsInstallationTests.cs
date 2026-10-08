using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Tools.Files;

internal static class NativeLsInstallationTests
{
    internal const string Prefix = "native ls installation ";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "actual profile preserves defaults schema read write and policy-mediated listing", ActualProfile),
        (Prefix + "actual profile denies ungranted workspace root and outside directory targets", WorkspaceDenials),
        (Prefix + "real CLI resume restores explicit activation and read-directory grant", CliResume),
        (Prefix + "schema comparison accepts registration escaping and rejects public schema changes", SchemaComparison)
    ];

    private static bool SameDeclaration(JsonData expected, JsonData actual) => JsonElement.DeepEquals(expected.Value, actual.Value);

    private static Task SchemaComparison()
    {
        const string original = """{"name":"ls","description":"Directory '/'","parameters":{"type":"object","properties":{"path":{"type":"string"},"limit":{"type":"number"}},"additionalProperties":false}}""";
        var expected = JsonData.Parse(original);
        // Exercise the same default serializer used by CreateActivationMessage. It
        // escapes apostrophes while retaining every declaration/schema value.
        var registered = JsonData.Parse(JsonSerializer.Serialize(expected.Value));
        Check(expected.ToString() != registered.ToString() && SameDeclaration(expected, registered),
            "Registration escaping was mistaken for a changed public declaration.");
        foreach (var changed in new[]
        {
            original.Replace("\"path\":{\"type\":\"string\"}", "\"path\":{\"type\":\"number\"}", StringComparison.Ordinal),
            original.Replace("\"limit\":{\"type\":\"number\"}", "\"limit\":{\"type\":\"string\"}", StringComparison.Ordinal),
            original.Replace("\"additionalProperties\":false", "\"required\":[\"path\"],\"additionalProperties\":false", StringComparison.Ordinal),
            original.Replace("\"additionalProperties\":false", "\"additionalProperties\":true", StringComparison.Ordinal),
            original.Replace(",\"limit\":{\"type\":\"number\"}", "", StringComparison.Ordinal),
            original.Replace("Directory '/'", "Directory contents", StringComparison.Ordinal)
        }) Check(changed != original && !SameDeclaration(expected, JsonData.Parse(changed)),
            "Structural comparison accepted a changed property type, required field, property policy, declaration or description.");
        return Task.CompletedTask;
    }

    private static async Task ActualProfile()
    {
        using var files = new Fixture();
        var turns = Turns(SessionCommandTests.CompletionsTool("ls", "listing", new { path = files.Listing }),
            SessionCommandTests.CompletionsTool("read", "original-read", new { path = files.Source }, required: [".hidden", "alpha", "Beta/"]),
            SessionCommandTests.CompletionsTool("write", "original-write", new { path = files.Target, content = "original write semantics" }, required: ["original read semantics"]),
            SessionCommandTests.CompletionsText("installation complete", required: ["Successfully wrote"]));
        await using var profile = await Profile(files, turns, [files.Listing, files.Source], [files.Target]);
        await using var session = await NewSession(files, profile);
        profile.AttachOwner(session);
        Names(["read", "write"], session.GetActiveTools());
        var original = new ReadWriteTools(profile.Workspace, profile.Workspace);
        var defaults = new SessionSystemReplay().Replay(session.Snapshot.Context.LlmMessages).Tools;
        Names(["read", "write"], defaults.Select(tool => tool.Value.GetProperty("name").GetString()!));
        Check(defaults.Zip(original.Declarations).All(pair => pair.First.ToString() == pair.Second.ToString()), "Existing read/write declarations changed.");
        await session.SetActiveToolsAsync(["read", "ls", "write"]);
        Names(["read", "ls", "write"], session.GetActiveTools());
        var declaration = new SessionSystemReplay().Replay(session.Snapshot.Context.LlmMessages).Tools.Single(tool => tool.Value.GetProperty("name").GetString() == "ls");
        Check(SameDeclaration(new LsTool(profile.Workspace, profile.Workspace).Declaration, declaration), "Session registration changed ls schema.");
        var parameters = declaration.Value.GetProperty("parameters");
        Check(parameters.GetProperty("properties").GetProperty("path").GetProperty("type").GetString() == "string" &&
            parameters.GetProperty("properties").GetProperty("limit").GetProperty("type").GetString() == "number" &&
            !parameters.TryGetProperty("required", out _) && !declaration.Value.TryGetProperty("namespace", out _), "Optional schema or unannotated built-in metadata changed.");
        await session.PromptAsync(Input());
        Check(session.Snapshot.Fault is null && profile.UsedTurns == 4, "Actual session did not finish all original tool turns.");
        var actions = JsonSerializer.SerializeToElement(profile.Actions).EnumerateArray().ToArray();
        Names(["ls", "read", "write"], actions.Select(action => action.GetProperty("ToolName").GetString()!));
        Check(actions.All(action => action.GetProperty("allowed").GetBoolean()), "Exact explicit tool grants failed.");
        Check(actions[0].GetProperty("Target").GetString() == await Canonical(files.Listing), "Policy lost canonical listing target.");
        var result = ToolResults(session).Single(value => value.GetProperty("toolName").GetString() == "ls");
        Check(!result.GetProperty("isError").GetBoolean() && ResultText(result) == ".hidden\nalpha\nBeta/" &&
            !result.TryGetProperty("details", out _), "Actual persistent listing changed content or fabricated details.");
        Check(await File.ReadAllTextAsync(files.Target) == "original write semantics" && await File.ReadAllTextAsync(files.Source) == "original read semantics", "Read/write effects changed.");
        foreach (var request in JsonSerializer.SerializeToElement(profile.Requests).EnumerateArray())
            Names(["read", "ls", "write"], request.GetProperty("toolNames").EnumerateArray().Select(name => name.GetString()!));
    }

    private static async Task WorkspaceDenials()
    {
        using var files = new Fixture();
        var turns = Turns(SessionCommandTests.CompletionsTool("ls", "no-grant", new { path = files.Ungranted }),
            SessionCommandTests.CompletionsTool("ls", "outside", new { path = files.Outside }),
            SessionCommandTests.CompletionsTool("ls", "root", new { }), SessionCommandTests.CompletionsText("denials complete"));
        await using var profile = await Profile(files, turns, [files.Listing], []);
        await using var session = await NewSession(files, profile); profile.AttachOwner(session);
        await session.SetActiveToolsAsync(["ls"]); await session.PromptAsync(Input());
        Check(session.Snapshot.Fault is null && profile.UsedTurns == 4, "Denial flow failed to settle.");
        var actions = JsonSerializer.SerializeToElement(profile.Actions).EnumerateArray().ToArray();
        Check(actions.Length == 3 && actions.All(action => action.GetProperty("ToolName").GetString() == "ls" && !action.GetProperty("allowed").GetBoolean()), "Listing bypassed exact grant or workspace boundary.");
        var results = ToolResults(session).ToArray();
        Check(results.Length == 3 && results.All(value => value.GetProperty("isError").GetBoolean()), "Denied listing produced successful content.");
        Check(results.All(value => !ResultText(value).Contains("secret-marker", StringComparison.Ordinal)), "Denied directory names reached the transcript.");
        try
        {
            await using var unexpected = await Profile(files, [], [files.Session], []);
            throw new InvalidOperationException("Reserved session target was admitted as a listing read grant.");
        }
        catch (SessionCommandException error) when (error.Failure == SessionCommandFailure.ReservedTarget) { }
    }

    private static async Task CliResume()
    {
        using var files = new Fixture();
        await using (var profile = await Profile(files, [], [], []))
        {
            await using var session = await NewSession(files, profile); profile.AttachOwner(session);
            Names(["read", "write"], session.GetActiveTools());
            await session.SetActiveToolsAsync(["read", "ls", "write"]);
        }
        await File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[]
        {
            SessionCommandTests.CompletionsTool("ls", "resumed-list", new { path = files.Listing, limit = 2 }),
            SessionCommandTests.CompletionsText("CLI listing complete", required: [".hidden", "alpha", "2 entries limit reached"])
        } }));
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await SessionCommands.RunAsync(["session", "resume", "--session", files.Session, "--workspace", files.Workspace,
            "--offline-api", "openai-completions", "--offline-script", files.Script, "--message", "list on resume", "--allow-read", files.Listing], output, error);
        Check(exit == 0 && error.ToString() == "", "Real in-process CLI resume failed: " + output + error);
        using var report = JsonDocument.Parse(output.ToString());
        var actions = report.RootElement.GetProperty("actions");
        Check(actions.GetArrayLength() == 1 && actions[0].GetProperty("ToolName").GetString() == "ls" &&
            actions[0].GetProperty("allowed").GetBoolean() && actions[0].GetProperty("Target").GetString() == await Canonical(files.Listing), "CLI grant/registration bypassed final policy.");
        foreach (var request in report.RootElement.GetProperty("requests").EnumerateArray())
            Names(["read", "ls", "write"], request.GetProperty("toolNames").EnumerateArray().Select(name => name.GetString()!));
        // The command and all session/profile owners have settled before a fresh reader observes the durable tool result.
        var log = await new PiSharp.Sessions.Storage.SessionLogReader().ReadFileAsync(files.Session);
        var result = log.ValidatedPrefix.Where(record => record.Entry.Type == "message")
            .Select(record => record.Entry.WireBody.Value.GetProperty("message"))
            .Single(message => message.GetProperty("role").GetString() == "toolResult" && message.GetProperty("toolName").GetString() == "ls");
        Check(ResultText(result) == ".hidden\nalpha\n\n[2 entries limit reached. Use limit=4 for more]" &&
            result.GetProperty("details").GetProperty("entryLimitReached").GetDouble() == 2, "CLI durable listing limit result changed.");
    }

    private static Task<OfflineSessionProfile> Profile(Fixture files, ImmutableArray<JsonData> turns,
        ImmutableArray<string> reads, ImmutableArray<string> writes) => OfflineSessionProfile.CreateAsync(files.Workspace, files.Session, null,
            turns, reads, writes, default, offlineApi: "openai-completions");
    private static async Task<PersistentAgentSession> NewSession(Fixture files, OfflineSessionProfile profile)
    {
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "ls-installation",
            timestamp = "2026-10-04T00:00:00.000Z", cwd = profile.Workspace })); var sequence = 0;
        var session = await PersistentAgentSession.CreateAsync(files.Session, header, profile.Registry, profile.SelectedModel, () => 1, () => "entry-" + ++sequence);
        try { await session.ConfigureAsync(new(SystemMessage: new("system", profile.InitialSystem))); return session; }
        catch (Exception original)
        {
            try { await session.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Session setup and cleanup failed.", original, cleanup); }
            throw;
        }
    }
    private static ImmutableArray<JsonData> Turns(params object[] turns) => turns.Select(turn => JsonData.Parse(JsonSerializer.Serialize(turn))).ToImmutableArray();
    private static TranscriptEntry Input() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"list fixture\",\"timestamp\":1}"));
    private static IEnumerable<JsonElement> ToolResults(PersistentAgentSession session) => session.Snapshot.Context.LlmMessages
        .Where(entry => entry.Role == "toolResult").Select(entry => entry.WireBody.Value);
    private static string ResultText(JsonElement value) => value.GetProperty("content")[0].GetProperty("text").GetString()!;
    private static async Task<string> Canonical(string path) => await new LocalFileOperations().CanonicalizeAsync(path, default);
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Ordered tool selection changed.");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; } public string Workspace => Path.Combine(Root, "workspace");
        public string Listing => Path.Combine(Workspace, "listing"); public string Ungranted => Path.Combine(Workspace, "ungranted");
        public string Outside => Path.Combine(Root, "outside"); public string Session => Path.Combine(Workspace, "session.jsonl");
        public string Script => Path.Combine(Workspace, "script.json"); public string Source => Path.Combine(Workspace, "source.txt");
        public string Target => Path.Combine(Workspace, "target.txt");
        public Fixture()
        {
            Root = Path.Combine(_parent, "pisharp-ls-installation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Listing, "Beta")); Directory.CreateDirectory(Ungranted); Directory.CreateDirectory(Outside);
            File.WriteAllText(Path.Combine(Listing, ".hidden"), "h"); File.WriteAllText(Path.Combine(Listing, "alpha"), "a");
            File.WriteAllText(Source, "original read semantics");
            File.WriteAllText(Path.Combine(Ungranted, "secret-marker-inside"), "secret"); File.WriteAllText(Path.Combine(Outside, "secret-marker-outside"), "secret");
        }
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-ls-installation-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid owned fixture root.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
