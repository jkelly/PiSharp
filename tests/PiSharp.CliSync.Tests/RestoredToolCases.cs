using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.ToolSelection;

// cli.reload-tools (0.99.2, agent-session.ts _restoreToolsFromTranscript and _pendingToolNames): a session opened without
// initial tool names, and tree navigation, restore the transcript's loadout. Restored tools that are not registered yet, such
// as tools of an MCP server that is still connecting, stay pending and activate when they register; tools the user turned off
// or --tools/--exclude-tools keep out never become pending. Authored expectations.
internal static partial class Program
{
    private const string DocsTool = "mcp__docs__search";

    private static SessionRuntimeRegistry RestoredRegistry(ImmutableArray<SessionRegisteredTool> tools, AllowedToolSelection? lifetime = null) =>
        new([new(SettledModel, new SettledTransport())], tools, new Deny(), new() { LifetimeToolSelection = lifetime });

    private static Task<PersistentAgentSession> Reopen(string path, SessionRuntimeRegistry registry)
    {
        var ids = 0;
        return PersistentAgentSession.OpenWithRegistryAsync(path, registry, () => 7, () => "restored-" + Guid.NewGuid().ToString("N") + "-" + ++ids);
    }

    /// <summary>The docs server connects in the background: like McpPreparedServer, its tool joins the catalog at an idle boundary
    /// and the session's current active names are kept.</summary>
    private static Task ConnectDocs(ReplaceableAgentSession owner, params SessionRegisteredTool[] extra) =>
        owner.PrepareAndPublishToolCatalogAsync(owner.Current, (expected, _) => ValueTask.FromResult(new PreparedSessionToolCatalog(
            expected.WithToolCatalog([.. expected.RegisteredTools.Where(tool => extra.All(added => added.Adapter.Name != tool.Adapter.Name)), .. extra], null),
            owner.Current.Session.GetActiveTools(), () => { })));

    private static string[] SystemTools(JsonElement message, string field) =>
        message.TryGetProperty(field, out var tools) ? [.. tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)] : [];

    private static async Task<string> RecordedSession(ImmutableArray<SessionRegisteredTool> tools, params ImmutableArray<string>[] selections)
    {
        var (session, directory) = await PendingSession(tools);
        await using (session) foreach (var selection in selections) await session.SetActiveToolsAsync(selection);
        return Path.Combine(directory, "session.jsonl");
    }

    private static async Task OpenRestoresPendingTools()
    {
        ImmutableArray<SessionRegisteredTool> all = [Tool("read"), Tool("grep"), Tool(DocsTool, ToolExposure.Deferred)];
        // tool_search loaded the deferred docs tool; the user then turned grep off.
        var path = await RecordedSession(all, ["read", "grep", DocsTool], ["read", DocsTool]);
        try
        {
            // Reopened while the docs server is still connecting: its tool is not registered, so it is pending, not rejected.
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Tool("grep")]));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read"], reopened.GetActiveTools(), "restored registered tools");
            Names([DocsTool], reopened.PendingToolNames, "restored unregistered tool is pending");
            // The restored loadout is recorded, so the transcript declares only registered tools.
            var record = reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("message");
            Names(["read", DocsTool], SystemTools(record, "toolsRemoved"), "recorded loadout removals");
            Names(["read"], SystemTools(record, "toolsAdded"), "recorded loadout declarations");
            // The server connects: the pending tool activates; grep, registered but turned off, stays off.
            await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred));
            Names(["read", DocsTool], owner.Current.Session.GetActiveTools(), "pending tool activates on registration");
            Check(owner.Current.Session.PendingToolNames.IsEmpty, "activated tool stayed pending");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }

        // A restored tool that registers only after the next prompt starts is not activated.
        path = await RecordedSession(all, ["read", DocsTool]);
        try
        {
            var reopened = await Reopen(path, RestoredRegistry([Tool("read")]));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names([DocsTool], reopened.PendingToolNames, "pending before the prompt");
            await reopened.PromptAsync(SettledUser("go")); await reopened.WaitForIdleAsync();
            Check(reopened.PendingToolNames.IsEmpty, "the prompt kept restored pending tools");
            await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred));
            Names(["read"], owner.Current.Session.GetActiveTools(), "tool registered after the prompt was activated");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    private static async Task OpenKeepsDisabledToolsOff()
    {
        // The transcript names grep, but this run excludes it (--exclude-tools grep): it is neither restored nor pending,
        // and stays off when it registers; the docs tool still restores when its server connects.
        var path = await RecordedSession([Tool("read"), Tool("grep"), Tool(DocsTool)], ["read", "grep", DocsTool]);
        try
        {
            var excluded = AllowedToolSelection.Create(excluded: ["grep"]);
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Tool("grep")], excluded));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read"], reopened.GetActiveTools(), "excluded tool not restored");
            Names([DocsTool], reopened.PendingToolNames, "excluded tool not pending");
            await ConnectDocs(owner, Tool(DocsTool), Tool("grep"));
            Names(["read", DocsTool], owner.Current.Session.GetActiveTools(), "excluded tool stays off after registration");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }

        // A selection that turns a restored tool off before the pending one registers (like an extension restoring its own
        // loadout) replaces the restored loadout: neither the dropped pending tool nor the turned-off tool comes back.
        path = await RecordedSession([Tool("read"), Tool("grep"), Tool(DocsTool, ToolExposure.Deferred)], ["read", "grep", DocsTool]);
        try
        {
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Tool("grep")]));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read", "grep"], reopened.GetActiveTools(), "restored registered tools");
            Names([DocsTool], reopened.PendingToolNames, "pending before the selection");
            await reopened.SetActiveToolsAsync(["read"]);
            Check(reopened.PendingToolNames.IsEmpty, "deactivating selection kept restored pending tools");
            await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred));
            Names(["read"], owner.Current.Session.GetActiveTools(), "dropped pending tool or turned-off grep came back");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    private static async Task NavigationRestoresPendingTools()
    {
        var (session, directory) = await PendingSession([Tool("read"), Tool(DocsTool, ToolExposure.Deferred)]);
        try
        {
            await session.SetActiveToolsAsync(["read", DocsTool]); var withDocs = session.Snapshot.Context.LeafId!;
            await session.SetActiveToolsAsync(["read"]); var withoutDocs = session.Snapshot.Context.LeafId!;
            // The docs server disconnects: its tool leaves the catalog.
            await Publish(session, [Tool("read")], restoring: false);
            await using var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("No replacement expected."));
            async Task<SessionTreeNavigationReceipt> Navigate(string target)
            {
                var view = owner.CaptureTree(owner.Current);
                var receipt = await owner.NavigateTreeAsync(view.Attachment, new(target, view.Revision));
                Equal(SessionTreeNavigationDisposition.Selected, receipt.Disposition, "navigation to " + target);
                return receipt;
            }
            // Navigating to the branch that declared the docs tool restores its loadout instead of rejecting it.
            var restored = await Navigate(withDocs);
            Names(["read"], session.GetActiveTools(), "navigation restores registered tools");
            Names([DocsTool], session.PendingToolNames, "navigation keeps the unregistered tool pending");
            var record = restored.Context.Ancestry[^1];
            Equal(withDocs, record.ParentId, "restored loadout recorded on the target branch");
            Names(["read"], SystemTools(record.WireBody.Value.GetProperty("message"), "toolsAdded"), "recorded navigation loadout");
            // Another branch replaces the pending set.
            await Navigate(withoutDocs);
            Check(session.PendingToolNames.IsEmpty, "navigation kept the previous branch's pending tools");
            await Navigate(withDocs);
            Names([DocsTool], session.PendingToolNames, "pending again on the branch that declared it");
            await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred));
            Names(["read", DocsTool], owner.Current.Session.GetActiveTools(), "pending tool activates after navigation");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
