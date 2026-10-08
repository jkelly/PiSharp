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

    // _restoreToolsFromTranscript restores by name and _applyToolLoadout declares the current registry's tool, so a recorded
    // declaration that changed since (an MCP server that updated its schema) is replaced, and a now-hidden tool is skipped.
    private static SessionRegisteredTool Changed(string name, ToolExposure exposure = ToolExposure.Direct) =>
        new(JsonData.Parse(JsonSerializer.Serialize(new { name, description = name + " tool, updated", parameters = new { type = "object", properties = new { query = new { type = "string" } } } })),
            new NoAdapter(name)) { Exposure = exposure, IsExtension = true };

    private static string Description(JsonElement message, string tool) =>
        message.GetProperty("toolsAdded").EnumerateArray().Single(declaration => declaration.GetProperty("name").GetString() == tool).GetProperty("description").GetString()!;

    private static async Task<SessionTreeNavigationReceipt> NavigateTo(ReplaceableAgentSession owner, string target)
    {
        var view = owner.CaptureTree(owner.Current);
        var receipt = await owner.NavigateTreeAsync(view.Attachment, new(target, view.Revision));
        Equal(SessionTreeNavigationDisposition.Selected, receipt.Disposition, "navigation to " + target);
        return receipt;
    }

    private static async Task OpenReplacesChangedDeclarations()
    {
        var path = await RecordedSession([Tool("read"), Tool(DocsTool)], ["read", DocsTool]);
        try
        {
            // The docs server now reports a changed schema: the tool is restored with it instead of DeclarationMismatch.
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Changed(DocsTool)]));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read", DocsTool], reopened.GetActiveTools(), "changed declaration restored by name");
            Check(reopened.PendingToolNames.IsEmpty, "a registered tool became pending");
            var record = reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("message");
            Equal(DocsTool + " tool, updated", Description(record, DocsTool), "restored loadout records the current declaration");
            Names(["read", DocsTool], SystemTools(record, "toolsRemoved"), "recorded declarations replaced");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }

        // Tree navigation to a branch recorded with the old declaration restores it with the current one.
        var (session, directory) = await PendingSession([Tool("read"), Tool(DocsTool)]);
        try
        {
            await session.SetActiveToolsAsync(["read", DocsTool]); var withDocs = session.Snapshot.Context.LeafId!;
            await session.SetActiveToolsAsync(["read"]);
            await Publish(session, [Tool("read"), Changed(DocsTool)], restoring: false);
            await using var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("No replacement expected."));
            var restored = await NavigateTo(owner, withDocs);
            Names(["read", DocsTool], session.GetActiveTools(), "navigation restores the changed declaration by name");
            var record = restored.Context.Ancestry[^1];
            Equal(withDocs, record.ParentId, "current declaration recorded on the target branch");
            Equal(DocsTool + " tool, updated", Description(record.WireBody.Value.GetProperty("message"), DocsTool), "navigation records the current declaration");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task RestoreSkipsHiddenTools()
    {
        var path = await RecordedSession([Tool("read"), Tool(DocsTool)], ["read", DocsTool]);
        try
        {
            // The docs tool is now hidden: it is skipped silently instead of UnsupportedDeclaration, and stays pending like
            // every restored name that was not applied, so it activates if its exposure becomes declarable again.
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Tool(DocsTool, ToolExposure.Hidden)]));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read"], reopened.GetActiveTools(), "hidden tool skipped");
            Names([DocsTool], reopened.PendingToolNames, "skipped hidden tool pending");
            Names(["read"], SystemTools(reopened.Snapshot.Log.Entries[^1].WireBody.Value.GetProperty("message"), "toolsAdded"), "recorded without the hidden tool");
            await ConnectDocs(owner, Tool(DocsTool));
            Names(["read", DocsTool], owner.Current.Session.GetActiveTools(), "tool activates once it is declarable again");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }

        var (session, directory) = await PendingSession([Tool("read"), Tool(DocsTool)]);
        try
        {
            await session.SetActiveToolsAsync(["read", DocsTool]); var withDocs = session.Snapshot.Context.LeafId!;
            await session.SetActiveToolsAsync(["read"]);
            await Publish(session, [Tool("read"), Tool(DocsTool, ToolExposure.Hidden)], restoring: false);
            await using var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("No replacement expected."));
            await NavigateTo(owner, withDocs);
            Names(["read"], session.GetActiveTools(), "navigation skips the hidden tool");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task NavigationWithoutSystemMessageKeepsTools()
    {
        var (session, directory) = await PendingSession([Tool("read"), Tool("grep"), Tool(DocsTool)]);
        try
        {
            // Session creation records the model and thinking level but no system message.
            var start = session.Snapshot.Context.LeafId!;
            Check(!session.Snapshot.Context.LlmMessages.Any(message => message.Role == "system"), "fixture start declares a system message");
            await session.SetActiveToolsAsync(["read", "grep", DocsTool]); var withDocs = session.Snapshot.Context.LeafId!;
            await session.SetActiveToolsAsync(["read", "grep"]);
            await Publish(session, [Tool("read"), Tool("grep")], restoring: false);
            await using var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("No replacement expected."));
            await NavigateTo(owner, withDocs);
            Names([DocsTool], session.PendingToolNames, "pending before the navigation");
            // The target branch has no system message: the current tools are kept and pending tools are dropped. Nothing is
            // written by the navigation; the next request records the kept tools before it is sent.
            var kept = await NavigateTo(owner, start);
            Equal(start, kept.LeafId, "navigation wrote a record");
            Names(["read", "grep"], session.GetActiveTools(), "current tools kept on a branch without a system message");
            Check(session.PendingToolNames.IsEmpty, "navigation kept pending tools");
            await session.PromptAsync(SettledUser("go")); await session.WaitForIdleAsync();
            Names(["read", "grep"], session.Snapshot.Agent.Tools.Select(tool => tool.Name), "kept tools declared to the request");
            var record = session.Snapshot.Context.LlmMessages.Last(message => message.Role == "system").WireBody.Value;
            Names(["read", "grep"], SystemTools(record, "toolsAdded"), "kept tools recorded at the next request");
            await ConnectDocs(owner, Tool(DocsTool));
            Names(["read", "grep"], owner.Current.Session.GetActiveTools(), "dropped pending tool activated");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
