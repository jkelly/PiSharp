using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Sessions.Serialization;

// cli.reload-tools (0.99.2, agent-session.ts _pendingToolNames): tools active before a reload that the new catalog does not
// register stay pending and activate when they register later, without resurrecting disabled tools. Authored expectations.
internal static partial class Program
{
    private static async Task<(PersistentAgentSession Session, string Directory)> PendingSession(ImmutableArray<SessionRegisteredTool> tools,
        AllowedToolSelection? lifetime = null)
    {
        var directory = Temp("pending-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var ids = 0;
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "pending-header",
            timestamp = "2026-10-08T00:00:00.000Z", cwd = directory }));
        var registry = new SessionRuntimeRegistry([new(SettledModel, new SettledTransport())], tools, new Deny(), new() { LifetimeToolSelection = lifetime });
        var session = await PersistentAgentSession.CreateAsync(Path.Combine(directory, "session.jsonl"), header, registry, SettledModel,
            () => 7, () => "pending-entry-" + Interlocked.Increment(ref ids));
        return (session, directory);
    }

    private static Task Publish(PersistentAgentSession session, ImmutableArray<SessionRegisteredTool> tools, bool restoring)
    {
        var expected = session.CaptureToolCatalogRegistry();
        var replacement = expected.WithToolCatalog(tools, null);
        var active = session.GetActiveTools();
        return restoring ? session.PublishRestoringToolCatalogAsync(expected, replacement, active.Where(name => tools.Any(tool => tool.Adapter.Name == name)).ToImmutableArray(), () => { })
            : session.PublishToolCatalogAsync(expected, replacement, active, () => { });
    }

    private static async Task PendingReloadTools()
    {
        ImmutableArray<SessionRegisteredTool> all = [Tool("read"), Tool("grep"), Tool("mcp__srv__a"), Tool("mcp__srv__b")];
        var (session, directory) = await PendingSession(all);
        try
        {
            // grep is disabled by the user; both MCP tools are active.
            await session.SetActiveToolsAsync(["read", "mcp__srv__a", "mcp__srv__b"]);
            // A reload whose catalog does not register the MCP tools or grep yet: the active MCP tools become pending.
            await Publish(session, [Tool("read")], restoring: true);
            Names(["read"], session.GetActiveTools(), "active after reload");
            Names(["mcp__srv__a", "mcp__srv__b"], session.PendingToolNames, "pending after reload");
            // One server connects later: its registered tool activates; grep, registered again but disabled, stays inactive.
            await Publish(session, [Tool("read"), Tool("grep"), Tool("mcp__srv__a")], restoring: false);
            Names(["read", "mcp__srv__a"], session.GetActiveTools(), "late registration activates the pending tool");
            Names(["mcp__srv__b"], session.PendingToolNames, "still pending");
            // A selection that only adds a tool keeps the pending set; one that deactivates a tool drops it.
            await session.SetActiveToolsAsync(["read", "mcp__srv__a", "grep"]);
            Names(["mcp__srv__b"], session.PendingToolNames, "add-only selection keeps pending");
            await session.SetActiveToolsAsync(["read", "grep"]);
            Check(session.PendingToolNames.IsEmpty, "deactivating selection kept pending tools");
            await Publish(session, all, restoring: false);
            Names(["read", "grep"], session.GetActiveTools(), "dropped pending tool resurrected");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }

        // A run drops pending tools that did not register by then.
        (session, directory) = await PendingSession(all);
        try
        {
            await session.SetActiveToolsAsync(["read", "mcp__srv__a"]);
            await Publish(session, [Tool("read")], restoring: true);
            Names(["mcp__srv__a"], session.PendingToolNames, "pending before run");
            await session.PromptAsync(SettledUser("go")); await session.WaitForIdleAsync();
            Check(session.PendingToolNames.IsEmpty, "run kept pending tools");
            await Publish(session, all, restoring: false);
            Names(["read"], session.GetActiveTools(), "tool registered after the run was resurrected");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }

        // --tools/--exclude-tools keep a name from becoming pending (source _isAllowedTool).
        (session, directory) = await PendingSession(all, AllowedToolSelection.Create(["read", "mcp__srv__a"]));
        try
        {
            await session.SetActiveToolsAsync(["read", "mcp__srv__a"]);
            await Publish(session, [Tool("read")], restoring: true);
            Names(["mcp__srv__a"], session.PendingToolNames, "allowed pending");
            await Publish(session, [Tool("read"), Tool("mcp__srv__a")], restoring: false);
            Names(["read", "mcp__srv__a"], session.GetActiveTools(), "allowed tool activates");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }
    }
}
