using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Tools;
using PiSharp.Tools.Files;
using PiSharp.Tools.Processes;

internal static class BuiltinToolInstallationTests
{
    internal const string Prefix = "builtin installation ";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "ordered coding readonly and empty selections require explicit capabilities", Selection),
        (Prefix + "borrowed search executors retain exact final policy and are never called on denial", SearchPolicy),
        (Prefix + "catalog edit waits for preceding write on the same owned mutation queue", SharedQueue),
        (Prefix + "actual profile explicit edit activation persists and requires exact read and write grants", ProfileEdit),
        (Prefix + "actual profile read-only write-only and ungranted edit targets remain denied", ProfileDenials),
        (Prefix + "in-process CLI resume restores explicit edit selection and requires both grants", CliResume)
    ];

    private static Task Selection()
    {
        using var files = new Fixture(); var basic = new BuiltinToolCatalog(files.Workspace, files.Workspace);
        Names(["read", "edit", "write", "ls"], basic.Registered.Select(tool => tool.Name));
        Names(["ls", "read"], basic.Select(["ls", "read"]).Select(tool => tool.Name));
        Check(basic.Select([]).IsEmpty, "Empty selection substituted defaults.");
        foreach (var names in new ImmutableArray<string>[] { default, ["read", "read"], ["READ"], ["find"], ["unknown"] })
            Throws<ArgumentException>(() => basic.Select(names));
        Throws<ArgumentException>(() => basic.CodingTools());
        Throws<ArgumentException>(() => basic.ReadOnlyTools());
        var runner = new NoProcess();
        var bash = new BashTool(runner, new(Environment.ProcessPath!, files.Workspace,
            ImmutableDictionary<string, string>.Empty, files.Workspace));
        var find = new SearchFind(); var grep = new SearchGrep(files.Target);
        var complete = new BuiltinToolCatalog(files.Workspace, files.Workspace, bash: bash, find: find, grep: grep);
        Names(["read", "bash", "edit", "write", "grep", "find", "ls"], complete.Registered.Select(tool => tool.Name));
        Names(["read", "bash", "edit", "write"], complete.CodingTools().Select(tool => tool.Name));
        Names(["read", "grep", "find", "ls"], complete.ReadOnlyTools().Select(tool => tool.Name));
        Check(complete.Registered.All(tool => tool.Declaration.Value.GetProperty("name").GetString() == tool.Name), "Catalog mismatched declarations and adapters.");
        Check(find.Calls == 0 && grep.Calls == 0 && runner.Calls == 0, "Catalog selection executed a borrowed capability.");
        return Task.CompletedTask;
    }

    private static async Task SearchPolicy()
    {
        using var files = new Fixture(); var find = new SearchFind(); var grep = new SearchGrep(files.Target);
        var catalog = new BuiltinToolCatalog(files.Workspace, files.Workspace, find: find, grep: grep);
        var adapters = catalog.Select(["find", "grep"]).Select(tool => tool.Adapter).ToArray();
        var denied = new ToolInvoker(adapters, new Permit(_ => false));
        foreach (var name in new[] { "find", "grep" })
        {
            var result = await denied.ExecuteAsync(Invocation(name, new { pattern = "data", path = files.Workspace }), default);
            Check(result.Failure?.Kind == ToolFailureKind.Blocked, "Catalog bypassed the mandatory policy.");
        }
        Check(find.Calls == 0 && grep.Calls == 0, "Denied search executed a borrowed executor.");
        var policy = new Permit(action => action.Target == files.Workspace && action.Operation == action.ToolName);
        var invoker = new ToolInvoker(adapters, policy);
        var found = await invoker.ExecuteAsync(Invocation("find", new { pattern = "data", path = files.Workspace }), default);
        var matched = await invoker.ExecuteAsync(Invocation("grep", new { pattern = "data", path = files.Workspace }), default);
        Check(!found.IsError && found.Content.Single().Text == "data.txt", "Existing find formatting changed.");
        Check(!matched.IsError && matched.Content.Single().Text == "data.txt:1: data", "Existing grep formatting changed.");
        Check(find.Request!.SearchPath == files.Workspace && grep.Request!.SearchPath == files.Workspace &&
            find.Calls == 1 && grep.Calls == 1, "Executor lost the final exact search target.");
    }

    private static async Task SharedQueue()
    {
        using var files = new Fixture(); var operations = new HeldFiles();
        var catalog = new BuiltinToolCatalog(files.Workspace, files.Workspace, operations);
        var invoker = new ToolInvoker(catalog.Select(["write", "edit"]).Select(tool => tool.Adapter), new Permit(_ => true));
        Task<ToolResult>? writing = null, editing = null;
        try
        {
            writing = invoker.ExecuteAsync(Invocation("write", new { path = files.Target, content = "written" }), default).AsTask();
            if (await Task.WhenAny(operations.Entered.Task, writing) != operations.Entered.Task)
                throw new InvalidOperationException("Write settled before its owned barrier.");
            editing = invoker.ExecuteAsync(Invocation("edit", new { path = files.Target,
                edits = new[] { new { oldText = "written", newText = "edited" } } }), default).AsTask();
            Check(!editing.IsCompleted && operations.Reads == 0 && catalog.MutationSnapshot.RegisteredOperations == 2 &&
                catalog.MutationSnapshot.RegisteredKeys == 1, "Edit escaped the preceding write reservation.");
            operations.Release.TrySetResult();
            Check(!(await writing).IsError && !(await editing).IsError, "Queued write/edit did not settle successfully.");
            Check(await File.ReadAllTextAsync(files.Target) == "edited" && catalog.MutationSnapshot.RegisteredOperations == 0,
                "Edit did not observe committed write content or leaked reservations.");
        }
        finally
        {
            operations.Release.TrySetResult();
            foreach (var pending in new[] { writing, editing }) if (pending is not null) { try { await pending; } catch { } }
        }
    }

    private static async Task ProfileEdit()
    {
        using var files = new Fixture();
        var turns = Turns(SessionCommandTests.CompletionsTool("edit", "edit-one", new { path = files.Target,
            edits = new[] { new { oldText = "original", newText = "changed" } } }),
            SessionCommandTests.CompletionsText("edited", required: ["Successfully replaced 1 block(s)"]));
        await using var profile = await Profile(files, turns, [files.Target], [files.Target]);
        await using var session = await NewSession(files, profile); profile.AttachOwner(session);
        Names(["read", "write"], session.GetActiveTools());
        await session.SetActiveToolsAsync(["edit", "read"]);
        Names(["edit", "read"], session.GetActiveTools());
        var declarations = new SessionSystemReplay().Replay(session.Snapshot.Context.LlmMessages).Tools;
        Names(["edit", "read"], declarations.Select(tool => tool.Value.GetProperty("name").GetString()!));
        await session.PromptAsync(Input());
        Check(session.Snapshot.Fault is null && profile.UsedTurns == 2 && await File.ReadAllTextAsync(files.Target) == "changed",
            "Real profile did not execute the explicitly activated edit.");
        var actions = JsonSerializer.SerializeToElement(profile.Actions).EnumerateArray().ToArray();
        Check(actions.Length == 1 && actions[0].GetProperty("ToolName").GetString() == "edit" &&
            actions[0].GetProperty("allowed").GetBoolean() && actions[0].GetProperty("Target").GetString() == files.Target,
            "Edit escaped exact read/write policy admission.");
        var result = Results(session).Single();
        Check(!result.GetProperty("isError").GetBoolean() && result.GetProperty("details").TryGetProperty("patch", out _),
            "Installed edit lost its existing result metadata.");
    }

    private static async Task ProfileDenials()
    {
        foreach (var grants in new[] { 0, 1, 2 })
        {
            using var files = new Fixture();
            var turns = Turns(SessionCommandTests.CompletionsTool("edit", "denied", new { path = files.Target,
                edits = new[] { new { oldText = "original", newText = "changed" } } }), SessionCommandTests.CompletionsText("denied settled"));
            await using var profile = await Profile(files, turns, grants == 1 ? [files.Target] : [], grants == 2 ? [files.Target] : []);
            await using var session = await NewSession(files, profile); profile.AttachOwner(session);
            await session.SetActiveToolsAsync(["edit"]); await session.PromptAsync(Input());
            Check(session.Snapshot.Fault is null && profile.UsedTurns == 2 && await File.ReadAllTextAsync(files.Target) == "original",
                "Missing read or write grant did not preserve the file.");
            var actions = JsonSerializer.SerializeToElement(profile.Actions).EnumerateArray().ToArray();
            Check(actions.Length == 1 && !actions[0].GetProperty("allowed").GetBoolean() && Results(session).Single().GetProperty("isError").GetBoolean(),
                "Explicit activation manufactured filesystem permission.");
        }
        using var reserved = new Fixture();
        try
        {
            await using var unexpected = await Profile(reserved, [], [reserved.Session], [reserved.Session]);
            throw new InvalidOperationException("Session log was admitted as an edit read/write grant.");
        }
        catch (SessionCommandException error) when (error.Failure == SessionCommandFailure.ReservedTarget) { }
    }

    private static async Task CliResume()
    {
        using var files = new Fixture();
        await using (var profile = await Profile(files, [], [], []))
        {
            await using var session = await NewSession(files, profile); profile.AttachOwner(session);
            await session.SetActiveToolsAsync(["edit", "read"]);
        }
        await File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[]
        {
            SessionCommandTests.CompletionsTool("edit", "resumed-edit", new { path = files.Target,
                edits = new[] { new { oldText = "original", newText = "resumed" } } }),
            SessionCommandTests.CompletionsText("resume complete", required: ["Successfully replaced 1 block(s)"])
        } }));
        using var output = new StringWriter(); using var error = new StringWriter();
        var exit = await SessionCommands.RunAsync(["session", "resume", "--session", files.Session, "--workspace", files.Workspace,
            "--offline-api", "openai-completions", "--offline-script", files.Script, "--message", "edit on resume",
            "--allow-read", files.Target, "--allow-write", files.Target], output, error);
        Check(exit == 0 && error.ToString() == "" && await File.ReadAllTextAsync(files.Target) == "resumed",
            "In-process resume did not restore explicitly selected edit: " + output + error);
        using var body = JsonDocument.Parse(output.ToString());
        var requests = body.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        Check(requests.Length == 2, "Resumed edit did not finish the literal turns.");
        foreach (var request in requests)
            Names(["edit", "read"], request.GetProperty("toolNames").EnumerateArray().Select(name => name.GetString()!));
    }

    private static Task<OfflineSessionProfile> Profile(Fixture files, ImmutableArray<JsonData> turns,
        ImmutableArray<string> reads, ImmutableArray<string> writes) => OfflineSessionProfile.CreateAsync(files.Workspace, files.Session, null,
            turns, reads, writes, default, offlineApi: "openai-completions");
    private static async Task<PersistentAgentSession> NewSession(Fixture files, OfflineSessionProfile profile)
    {
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "builtin-installation",
            timestamp = "2026-10-05T00:00:00.000Z", cwd = profile.Workspace })); var sequence = 0;
        var session = await PersistentAgentSession.CreateAsync(files.Session, header, profile.Registry, profile.SelectedModel, () => 1, () => "entry-" + ++sequence);
        try { await session.ConfigureAsync(new(SystemMessage: new("system", profile.InitialSystem))); return session; }
        catch (Exception original)
        {
            try { await session.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Session setup and cleanup failed.", original, cleanup); }
            throw;
        }
    }
    private static ToolInvocation Invocation(string name, object arguments)
    {
        var call = new ToolCallContent("catalog-call", name, JsonData.Parse(JsonSerializer.Serialize(arguments)));
        return new(new AssistantMessage("fixture-api", "fixture-provider", "fixture-model", 0, [call], TokenUsage.Zero, StopReason.ToolUse), call, 0);
    }
    private static ImmutableArray<JsonData> Turns(params object[] turns) => turns.Select(turn => JsonData.Parse(JsonSerializer.Serialize(turn))).ToImmutableArray();
    private static TranscriptEntry Input() => new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"edit fixture\",\"timestamp\":1}"));
    private static IEnumerable<JsonElement> Results(PersistentAgentSession session) => session.Snapshot.Context.LlmMessages
        .Where(entry => entry.Role == "toolResult").Select(entry => entry.WireBody.Value);
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual) => Check(expected.SequenceEqual(actual), "Ordered tool selection changed.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action operation) where T : Exception
    { try { operation(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Permit(Func<PreparedToolAction, bool> allow) : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new ToolActionAuthorization(allow(action))); }
    }
    private sealed class SearchFind : IFindExecutor
    {
        public int Calls; public FindExecutionRequest? Request;
        public bool SupportsPattern(string pattern) => pattern == "data";
        public ValueTask<ImmutableArray<string>> FindAsync(FindExecutionRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Request = request; return ValueTask.FromResult(ImmutableArray.Create("data.txt")); }
    }
    private sealed class SearchGrep(string target) : IGrepExecutor
    {
        public int Calls; public GrepExecutionRequest? Request;
        public ValueTask<ImmutableArray<GrepMatch>> GrepAsync(GrepExecutionRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Request = request; return ValueTask.FromResult(ImmutableArray.Create(new GrepMatch(target, 1, "data\n"))); }
    }
    private sealed class NoProcess : IProcessRunner
    {
        public int Calls;
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("No process execution is authored in this catalog fixture."); }
    }
    private sealed class HeldFiles : IDirectoryFileOperations
    {
        private readonly LocalFileOperations _local = new(); private int _writes;
        public int Reads; public TaskCompletionSource Entered { get; } = Gate(); public TaskCompletionSource Release { get; } = Gate();
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) => _local.ExistsAsync(path, token);
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) => _local.CanonicalizeAsync(path, token);
        public ValueTask<bool> IsDirectoryAsync(string path, CancellationToken token) => _local.IsDirectoryAsync(path, token);
        public ValueTask<ImmutableArray<string>> ReadDirectoryAsync(string path, int count, int characters, CancellationToken token) => _local.ReadDirectoryAsync(path, count, characters, token);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximum, CancellationToken token)
        { Reads++; return _local.ReadAsync(path, maximum, token); }
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) => _local.CreateDirectoryAsync(path, token);
        public async ValueTask WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken token)
        { if (Interlocked.Increment(ref _writes) == 1) { Entered.TrySetResult(); await Release.Task; } await _local.WriteAsync(path, content, token); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; } public string Workspace => Path.Combine(Root, "workspace");
        public string Target => Path.Combine(Workspace, "data.txt"); public string Session => Path.Combine(Workspace, "session.jsonl");
        public string Script => Path.Combine(Root, "script.json");
        public Fixture()
        {
            Root = Path.Combine(_parent, "pisharp-builtin-installation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Workspace); File.WriteAllText(Target, "original");
        }
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-builtin-installation-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid owned fixture root.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
