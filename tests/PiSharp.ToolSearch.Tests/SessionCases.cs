using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Discovery;
using PiSharp.Sessions.Serialization;

// tool-search/tool.ts searchAndLoad over a session: only registered `codemode`/`deferred` tools that are not active are
// searched; matches are activated through the session's tool selection (agent-session setActiveToolsByName), so the next request
// declares them and the transcript records the change like any other loadout change; a session reopened from its transcript
// (agent-session _restoreToolsFromTranscript) restores them, pending until their server registers them again.
internal static partial class Program
{
    private static readonly ModelDescriptor SearchModel = new("search-model", "openai-responses", "authored-provider");

    private sealed class TextTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.Yield();
            var message = new AssistantMessage(SearchModel.Api, SearchModel.Provider, SearchModel.Id, 1, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop);
            yield return new StreamStarted(message with { Content = [], StopReason = StopReason.Pending });
            yield return new TextStarted(0, new("")); yield return new TextDelta(0, "done"); yield return new TextEnded(0, "done");
            yield return new StreamDone(StopReason.Stop, message);
        }
    }
    private sealed class NoAdapter(string name) : IPreparedToolAdapter
    {
        public string Name => name;
        public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Deny : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ToolActionAuthorization(false));
    }

    private static readonly ToolNamespace DocsNamespace = new("mcp__docs", "Documentation");
    private static SessionRegisteredTool Registered(string name, ToolExposure exposure, string description, ToolNamespace? group = null) =>
        new(name == ToolSearch.Name
            ? JsonData.Parse(JsonSerializer.Serialize(new { name, description, parameters = McpDiscoveryToolIdentity.ToolSearchSchema.Value }))
            : JsonData.Parse(JsonSerializer.Serialize(new { name, description, parameters = new { type = "object" } })), new NoAdapter(name))
        { Exposure = exposure, Namespace = group, IsExtension = name != "read" };

    private static ImmutableArray<SessionRegisteredTool> SearchCatalog(bool docs = true) =>
    [
        Registered("read", ToolExposure.Direct, "Read a file."),
        Registered(ToolSearch.Name, ToolExposure.ModelOnly, ToolSearch.Description),
        Registered("mcp__web__search", ToolExposure.Direct, "Search the web documentation."),
        .. docs ? new[]
        {
            Registered("mcp__docs__search", ToolExposure.Deferred, "Search the documentation.\nUse precise terms.", DocsNamespace),
            Registered("mcp__docs__fetch", ToolExposure.Deferred, "Fetch a documentation page.", DocsNamespace),
            Registered("mcp__docs__gone", ToolExposure.Hidden, "Search retired documentation.", DocsNamespace)
        } : []
    ];

    private static SessionRuntimeRegistry SearchRegistry(bool docs = true) =>
        new([new(SearchModel, new TextTransport())], SearchCatalog(docs), new Deny());

    private static async Task<(PersistentAgentSession Session, string Path)> SearchSession()
    {
        var directory = Temp("session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var ids = 0;
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "search-header",
            timestamp = "2026-10-08T00:00:00.000Z", cwd = directory }));
        var path = Path.Combine(directory, "session.jsonl");
        var session = await PersistentAgentSession.CreateAsync(path, header, SearchRegistry(), SearchModel, () => 7,
            () => "search-entry-" + Interlocked.Increment(ref ids));
        await session.SetActiveToolsAsync(["read", ToolSearch.Name]);
        return (session, path);
    }

    private static TranscriptEntry User(string text) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = 7 })));
    private static async Task Prompt(PersistentAgentSession session, string text) { await session.PromptAsync(User(text)); await session.WaitForIdleAsync(); }

    /// <summary>Tool names each recorded tool change adds, in transcript order.</summary>
    private static string[][] RecordedAdditions(PersistentAgentSession session) => [.. session.Snapshot.Log.Entries
        .Where(entry => entry.WireBody.Value.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "system" &&
            message.TryGetProperty("toolsAdded", out _))
        .Select(entry => entry.WireBody.Value.GetProperty("message").GetProperty("toolsAdded").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!).ToArray())];

    private static async Task SearchLoadsForNextRequest()
    {
        var (session, path) = await SearchSession();
        try
        {
            var recorded = RecordedAdditions(session).Length;
            var loaded = McpToolSearch.SearchAndLoad(session, "search documentation", 1, CancellationToken.None);
            Names(["mcp__docs__search"], loaded.Select(tool => tool.Name), "best match within the limit");
            Equal("Loaded 1 tool. They are available from your next call:\n- mcp__docs__search: Search the documentation.", ToolSearch.FormatResult(loaded), "result text");
            // Selected now, declared from the next request: the transcript records the change when that request starts.
            Names(["read", ToolSearch.Name, "mcp__docs__search"], session.GetActiveTools(), "selection after the search");
            Equal(recorded, RecordedAdditions(session).Length, "recorded before the next request");
            await Prompt(session, "next");
            Names(["read", ToolSearch.Name, "mcp__docs__search"], RecordedAdditions(session)[^1], "recorded with the next request");
            // Active tools, direct tools and hidden tools are not searched; the rest of the deferred tools still are.
            Names(["mcp__docs__fetch"], McpToolSearch.SearchAndLoad(session, "search documentation", 8, CancellationToken.None).Select(tool => tool.Name),
                "only inactive deferred tools");
            Names([], McpToolSearch.SearchAndLoad(session, "documentation", 8, CancellationToken.None).Select(tool => tool.Name), "everything loaded");
            Names([], McpToolSearch.SearchAndLoad(session, "kubernetes", 8, CancellationToken.None).Select(tool => tool.Name), "no match");
            Names(["read", ToolSearch.Name, "mcp__docs__search", "mcp__docs__fetch"], session.GetActiveTools(), "no match changes nothing");
        }
        finally { await session.DisposeAsync(); Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    private static async Task LoadedToolsPersist()
    {
        var (session, path) = await SearchSession();
        try
        {
            await using (session)
            {
                McpToolSearch.SearchAndLoad(session, "fetch page", 8, CancellationToken.None);
                await Prompt(session, "first");
                await Prompt(session, "second");
                Names(["read", ToolSearch.Name, "mcp__docs__fetch"], session.GetActiveTools(), "loaded tool stays active across turns");
                Equal(1, RecordedAdditions(session).Count(names => names.Contains("mcp__docs__fetch")), "recorded once");
            }
            var ids = 0;
            // Reopened from the transcript with the docs server connected: the loaded tool is restored.
            await using (var reopened = await PersistentAgentSession.OpenWithRegistryAsync(path, SearchRegistry(), () => 9, () => "reopened-" + ++ids))
                Names(["read", ToolSearch.Name, "mcp__docs__fetch"], reopened.GetActiveTools(), "restored from the transcript");
            // Reopened while the docs server is still connecting: pending until it registers its tools.
            var connecting = await PersistentAgentSession.OpenWithRegistryAsync(path, SearchRegistry(docs: false), () => 9, () => "reopened-" + ++ids);
            await using var owner = new ReplaceableAgentSession(connecting, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read", ToolSearch.Name], connecting.GetActiveTools(), "registered part restored");
            Names(["mcp__docs__fetch"], connecting.PendingToolNames, "loaded tool pending");
            await owner.PrepareAndPublishToolCatalogAsync(owner.Current, (expected, _) => ValueTask.FromResult(new PreparedSessionToolCatalog(
                expected.WithToolCatalog(SearchCatalog(), null), owner.Current.Session.GetActiveTools(), () => { })));
            Names(["read", ToolSearch.Name, "mcp__docs__fetch"], owner.Current.Session.GetActiveTools(), "pending loaded tool activates on registration");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }
}
