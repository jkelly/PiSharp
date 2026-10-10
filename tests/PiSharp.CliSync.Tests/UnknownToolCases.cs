using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Runtime;

// Names that select no registered tool (agent-session.ts _applyToolLoadout, setActiveToolsByName and _refreshToolRegistry; sdk.ts
// initialActiveToolNames; cli/args.ts getToolListError at abe508e1b89912adde45528136c3221eb69acdd7): an initial selection (--tools,
// defaultTools, initial names) and an explicit activation ignore them instead of failing, and they do not become pending. Only
// malformed --tools lists are CLI errors. A name a --tools allowlist matches activates when its tool registers. Authored expectations.
internal static partial class Program
{
    private static string[] RecordedTools(string path) =>
        File.ReadAllLines(path).Select(line => JsonDocument.Parse(line).RootElement)
            .Where(record => record.TryGetProperty("message", out var message) && message.TryGetProperty("toolsAdded", out _))
            .Select(record => SystemTools(record.GetProperty("message"), "toolsAdded")).LastOrDefault() ?? [];

    private static Task InitialSelectionIgnoresUnregisteredNames() => WithLiveRoot("unknown-initial", async root =>
    {
        async Task<string[]> Create(string name, params string[] flags)
        {
            var path = Path.Combine(root, name + ".jsonl"); var output = new StringWriter(); var error = new StringWriter();
            Equal(0, await SessionCommands.RunAsync(["session", "create", "--session", path, "--workspace", root, .. flags], output, error), name + " exit; " + error);
            Equal("", error.ToString(), name + " stderr");
            return RecordedTools(path);
        }
        // --tools: unknown names, a tool this profile does not register and an MCP tool of no configured server are ignored.
        Names(["read"], await Create("cli", "--tools", "read,missing_tool,grep,mcp__docs__search"), "--tools keeps only registered names");
        Names([], await Create("cli-only-unknown", "-t", "missing_tool"), "--tools naming only unknown tools selects none");
        // defaultTools from settings, alone and changed by +name/-name entries.
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, """{"defaultTools":["missing_tool","read","edit"]}""");
        Names(["read", "edit"], await Create("settings", "--user-settings", settings), "defaultTools keeps only registered names");
        Names(["read", "write"], await Create("modifiers", "--user-settings", settings, "--tools", "+missing_other,-edit,+write"), "+name/-name keep only registered names");
        // Malformed lists are still argument errors (args.ts getToolListError).
        var invalid = new StringWriter();
        Equal(2, await SessionCommands.RunAsync(["session", "create", "--session", Path.Combine(root, "mixed.jsonl"), "--workspace", root,
            "--tools", "missing_tool,+read"], new StringWriter(), invalid), "mixed list exit");
        Equal("--tools: tool names cannot be mixed with +name or -name entries", JsonDocument.Parse(invalid.ToString()).RootElement.GetProperty("message").GetString(), "mixed list message");

        // SDK initialActiveToolNames: an open with initial names drops the unregistered one; it is not pending.
        var recorded = await RecordedSession([Tool("read"), Tool("grep")], ["read", "grep"]);
        try
        {
            var registry = new SessionRuntimeRegistry([new(SettledModel, new SettledTransport())], [Tool("read"), Tool("grep")], new Deny(),
                new() { InitialActiveToolNames = ["missing_tool", "grep", DocsTool] });
            var before = FileBytes(recorded);
            await using var opened = await Reopen(recorded, registry);
            Names(["grep"], opened.GetActiveTools(), "initial names keep only registered tools");
            Check(opened.PendingToolNames.IsEmpty, "an ignored initial name became pending");
            // The constructor applies the initial names in memory (_buildRuntime); the first prompt records them.
            Check(FileBytes(recorded).SequenceEqual(before), "opening with initial names wrote to the session file");
            var count = opened.Snapshot.Log.Entries.Length; var leaf = opened.Snapshot.Context.LeafId;
            await opened.PromptAsync(SettledUser("go")); await opened.WaitForIdleAsync();
            var record = RecordedAtPrompt(opened, count, leaf);
            Names(["read"], SystemTools(record, "toolsRemoved"), "initial selection removes the unselected tool");
            Names([], SystemTools(record, "toolsAdded"), "initial selection keeps grep's recorded declaration");
        }
        finally { Directory.Delete(Path.GetDirectoryName(recorded)!, recursive: true); }
    });

    private static async Task AllowlistedMcpToolActivatesOnRegistration()
    {
        // The CLI: a direct MCP server's tool listed in --tools is active from the first request; a listed MCP tool no server
        // registers is ignored, and the unlisted one of the same server stays off (an mcp__ entry filters MCP tools).
        await WithLiveRoot("allowlisted-mcp", async root =>
        {
            var agent = Path.Combine(root, "agent"); Directory.CreateDirectory(agent);
            await File.WriteAllTextAsync(Path.Combine(agent, "mcp.json"), """{"mcpServers":{"docs":{"command":"docs-server","exposure":"direct"}}}""");
            var host = new McpSessionHost(agent, Path.Combine(root, "home"), () => [KeyValuePair.Create("PATH", root)])
            { CreateChannel = entry => (actual, token) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(new FakeMcpChannel("search")) };
            var provider = new LiveEndpoint(seen => seen.Url == MessagesUrl ? AnthropicStream() : throw new InvalidOperationException("Unexpected URL " + seen.Url));
            await using var rpc = new LiveRpc([.. LiveArgs(root, "anthropic", "claude-sonnet-4-5"), "--tools", "read,mcp__docs__search,mcp__docs__missing,missing_tool"],
                new(Env(("ANTHROPIC_API_KEY", "env-key")), () => provider), host);
            await RunPrompts(rpc, "first");
            Equal("", string.Join("\n", McpDiagnostics(rpc.Error.ToString())), "MCP diagnostics");
            Names(["read", "mcp__docs__search"], ToolNames(provider.Snapshot().Single()), "first request declarations");
        });

        // A session whose --tools allowlist names a tool that is not registered yet: the initial selection ignores it (it is not
        // pending); when its server registers it, the refresh activates it because the allowlist names it (_refreshToolRegistry).
        ImmutableArray<SessionRegisteredTool> start = [Tool("read"), Tool("grep")];
        var lifetime = AllowedToolSelection.Create(["read", DocsTool]);
        var recorded = await RecordedSession(start, ["read", "grep"]);
        try
        {
            var registry = new SessionRuntimeRegistry([new(SettledModel, new SettledTransport())], start, new Deny(),
                new() { LifetimeToolSelection = lifetime, InitialActiveToolNames = lifetime.InitialNames });
            var opened = await Reopen(recorded, registry);
            await using var owner = new ReplaceableAgentSession(opened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read"], opened.GetActiveTools(), "initial selection before registration");
            Check(opened.PendingToolNames.IsEmpty, "an unregistered allowlisted name became pending");
            ImmutableArray<SessionRegisteredTool> later = [Tool("read"), Tool("grep"), Tool(DocsTool), Tool("mcp__docs__other")];
            await owner.PrepareAndPublishToolCatalogAsync(owner.Current, (expected, _) =>
            {
                var replacement = expected.WithToolCatalog(later, null);
                return ValueTask.FromResult(new PreparedSessionToolCatalog(replacement, AllowedToolSelection.SelectReloaded(replacement.LifetimeToolSelection,
                    owner.Current.Session.GetActiveTools(), [.. replacement.RegisteredTools.Select(tool =>
                        new ToolSelectionDescriptor(tool.Adapter.Name, tool.Exposure, tool.DefaultActive, tool.IsExtension))], null, null), () => { }));
            });
            Names(["read", DocsTool], owner.Current.Session.GetActiveTools(), "the allowlisted tool activates when it registers");
        }
        finally { Directory.Delete(Path.GetDirectoryName(recorded)!, recursive: true); }
    }

    private static async Task ExplicitActivationIgnoresUnknownNames()
    {
        // grep is capped out (--tools read,write,mcp__docs__*): naming it, or an unknown tool, is ignored instead of failing.
        var (session, directory) = await PendingSession([Tool("read"), Tool("write"), Tool("grep")], AllowedToolSelection.Create(["read", "write", "mcp__docs__*"]));
        try
        {
            await session.SetActiveToolsAsync(["read", "missing_tool", "grep"]);
            Names(["read"], session.GetActiveTools(), "SetActiveTools ignores unknown and capped-out names");
            var before = session.Snapshot.Log.Sequence;
            await session.SetActiveToolsAsync(["grep", "read", "missing_tool"]);
            Equal(before, session.Snapshot.Log.Sequence, "an unchanged selection after ignoring names appended a record");
            // The extension setActiveTools facade schedules the same selection.
            var scheduled = session.ScheduleToolActivation(["write", "missing_tool", "grep", "read"]);
            Names(["write", "read"], scheduled.Names, "scheduled activation ignores unknown and capped-out names");
            Check(session.PendingToolNames.IsEmpty, "an ignored name became pending");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }

        // Unknown names never join the pending set, and an add-only selection keeps the restored pending tools.
        var path = await RecordedSession([Tool("read"), Tool("grep"), Tool(DocsTool, ToolExposure.Deferred)], ["read", DocsTool]);
        try
        {
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Tool("grep")]));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names([DocsTool], reopened.PendingToolNames, "restored pending tool");
            await reopened.SetActiveToolsAsync(["read", "grep", "missing_tool"]);
            Names(["read", "grep"], reopened.GetActiveTools(), "add-only selection");
            Names([DocsTool], reopened.PendingToolNames, "the unknown name joined the pending set or the add-only selection dropped it");
            await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred), Tool("missing_tool"));
            Names(["read", "grep", DocsTool], owner.Current.Session.GetActiveTools(), "only the restored pending tool activates on registration");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }
}
