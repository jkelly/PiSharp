using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Tools.Files;

internal static class NativeGrepInstallationTests
{
    internal const string Prefix = "native grep installation ";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "actual session forwards catalog reader and preserves explicit activation and defaults", ActualProfile),
        (Prefix + "search denial exact read grant host denial and reserved target precede context effects", Denials),
        (Prefix + "ordinary profile cannot select grep without explicit borrowed host", Unavailable)
    ];
    private static async Task ActualProfile()
    {
        using var fixture = new Fixture(); var search = new Search(await new LocalFileOperations().CanonicalizeAsync(fixture.Source, default)); var files = new Files();
        var admission = new Admission(true); var grants = 0;
        var host = new OfflineGrepHost(search, files, admission, (path, maximum, token) =>
        {
            Check(path == search.Path && maximum == GrepTool.MaximumContextFileBytes && !token.IsCancellationRequested,
                "Context callback did not receive exact admitted path/bound/token.");
            grants++; return ValueTask.FromResult(true);
        });
        await using var profile = await Profile(fixture, host, [fixture.Source]);
        await using var session = await Session(fixture, profile); profile.AttachOwner(session);
        Check(session.GetActiveTools().SequenceEqual(["read", "write"]), "Grep registration changed default activation.");
        await session.SetActiveToolsAsync(["grep"]);
        await session.PromptAsync(Input());
        var result = Result(session);
        Check(!result.GetProperty("isError").GetBoolean() && Text(result).Contains("before-context", StringComparison.Ordinal) &&
            Text(result).Contains("after-context", StringComparison.Ordinal), "Actual session/catalog did not use injected reader.");
        Check(search.Calls == 1 && admission.Calls == 1 && files.Reads == 1 && grants == 1 &&
            admission.Target == profile.Workspace && files.LastPath == search.Path, "Borrowed effects or admissions were skipped/repeated.");
        await profile.DisposeAsync();
        Check(!search.Disposed && !files.Disposed, "Profile disposed borrowed host capabilities.");
    }
    private static async Task Denials()
    {
        foreach (var scenario in new[] { "search", "read-target", "host-read", "reserved-session" })
        {
            using var fixture = new Fixture();
            var search = new Search(await new LocalFileOperations().CanonicalizeAsync(scenario == "reserved-session" ? fixture.Log : fixture.Source, default));
            var files = new Files(); var admission = new Admission(scenario != "search"); var grants = 0;
            var host = new OfflineGrepHost(search, files, admission, (_, _, _) =>
            { grants++; return ValueTask.FromResult(scenario != "host-read"); });
            var reads = scenario == "read-target" ? ImmutableArray<string>.Empty : ImmutableArray.Create(fixture.Source);
            await using var profile = await Profile(fixture, host, reads);
            await using var session = await Session(fixture, profile); profile.AttachOwner(session);
            await session.SetActiveToolsAsync(["grep"]); await session.PromptAsync(Input());
            var result = Result(session);
            Check(files.Reads == 0 && !Text(result).Contains("before-context", StringComparison.Ordinal), "Denied context acquired bytes.");
            Check(search.Calls == (scenario == "search" ? 0 : 1) && grants == (scenario == "host-read" ? 1 : 0),
                "Search/read/host/reserved denial did not precede the corresponding effect.");
            if (scenario == "search") Check(result.GetProperty("isError").GetBoolean(), "Search denial became successful output.");
        }
    }
    private static async Task Unavailable()
    {
        using var fixture = new Fixture();
        try
        {
            await using var unexpected = await OfflineSessionProfile.CreateAsync(fixture.Root, fixture.Log, null,
                [], [], [], default, offlineApi: "openai-completions", toolSelection: new(["grep"], false));
            throw new InvalidOperationException("Ordinary profile silently supplied grep capabilities.");
        }
        catch (SessionCommandException error) when (error.Failure == SessionCommandFailure.InvalidArguments) { }
    }
    private static Task<OfflineSessionProfile> Profile(Fixture fixture, OfflineGrepHost host, ImmutableArray<string> reads)
    {
        // Search only the ordinary file; the reserved case returns a contained sibling to test separate read admission.
        var turns = new object[] { SessionCommandTests.CompletionsTool("grep", "search", new { pattern = "hit", path = fixture.Root, context = 1 }),
            SessionCommandTests.CompletionsText("done") };
        return OfflineSessionProfile.CreateAsync(fixture.Root, fixture.Log, null,
            turns.Select(turn => JsonData.Parse(JsonSerializer.Serialize(turn))).ToImmutableArray(), reads, [], default,
            offlineApi: "openai-completions", grepHost: host);
    }
    private static async Task<PersistentAgentSession> Session(Fixture fixture, OfflineSessionProfile profile)
    {
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
            id = "grep-installation", timestamp = "2026-10-05T00:00:00.000Z", cwd = profile.Workspace })); var sequence = 0;
        var session = await PersistentAgentSession.CreateAsync(fixture.Log, header, profile.Registry,
            profile.SelectedModel, () => 1, () => "entry-" + ++sequence);
        try { await session.ConfigureAsync(new(SystemMessage: new("system", profile.InitialSystem))); return session; }
        catch (Exception original)
        {
            try { await session.DisposeAsync(); }
            catch (Exception cleanup) { throw new AggregateException("Session setup and cleanup failed.", original, cleanup); }
            throw;
        }
    }
    private static TranscriptEntry Input() => new("user", JsonData.Parse("""{"role":"user","content":"grep fixture","timestamp":1}"""));
    private static JsonElement Result(PersistentAgentSession session) => session.Snapshot.Context.LlmMessages
        .Single(entry => entry.Role == "toolResult").WireBody.Value;
    private static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Admission(bool allow) : IToolActionPolicy
    {
        public int Calls; public string? Target;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; Target = action.Target; return ValueTask.FromResult(new ToolActionAuthorization(allow)); }
    }
    private sealed class Search(string path) : IGrepExecutor, IDisposable
    {
        public string Path { get; } = System.IO.Path.GetFullPath(path); public int Calls; public bool Disposed;
        public ValueTask<ImmutableArray<GrepMatch>> GrepAsync(GrepExecutionRequest request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult(ImmutableArray.Create(new GrepMatch(Path, 2, "hit"))); }
        public void Dispose() => Disposed = true;
    }
    private sealed class Files : IFileOperations, IDisposable
    {
        private readonly LocalFileOperations _local = new(); public int Reads; public string? LastPath; public bool Disposed;
        public ValueTask<bool> ExistsAsync(string path, CancellationToken token) => _local.ExistsAsync(path, token);
        public ValueTask<string> CanonicalizeAsync(string path, CancellationToken token) => _local.CanonicalizeAsync(path, token);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string path, int maximum, CancellationToken token)
        { Reads++; LastPath = path; return _local.ReadAsync(path, maximum, token); }
        public ValueTask CreateDirectoryAsync(string path, CancellationToken token) => throw new InvalidOperationException("Unexpected directory effect.");
        public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token) => throw new InvalidOperationException("Unexpected write effect.");
        public void Dispose() => Disposed = true;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        public string Root { get; } public string Source => System.IO.Path.Combine(Root, "source.txt"); public string Log => System.IO.Path.Combine(Root, "session.jsonl");
        public Fixture()
        {
            Root = System.IO.Path.Combine(_parent, "pisharp-grep-installation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root); File.WriteAllText(Source, "before-context\nhit\nafter-context\n");
        }
        public void Dispose()
        {
            if (System.IO.Path.GetDirectoryName(Root) != _parent || !System.IO.Path.GetFileName(Root).StartsWith("pisharp-grep-installation-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid owned fixture root.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
