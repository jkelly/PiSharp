using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Agent;
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

    /// <summary>The session file as written so far, read beside the session's writer.</summary>
    private static byte[] FileBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }

    private static string[] SystemTools(JsonElement message, string field) =>
        message.TryGetProperty(field, out var tools) ? [.. tools.EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)] : [];

    private static async Task<string> RecordedSession(ImmutableArray<SessionRegisteredTool> tools, params ImmutableArray<string>[] selections)
    {
        var (session, directory) = await PendingSession(tools);
        await using (session) foreach (var selection in selections) await Recorded(session, selection);
        return Path.Combine(directory, "session.jsonl");
    }

    /// <summary>setActiveToolsByName applies in memory; the next prompt records the selection (declareToolChanges).</summary>
    private static async Task Recorded(PersistentAgentSession session, ImmutableArray<string> names)
    {
        await session.SetActiveToolsAsync(names);
        await session.PromptAsync(SettledUser("record " + string.Join(",", names))); await session.WaitForIdleAsync();
    }

    private static async Task OpenRestoresPendingTools()
    {
        ImmutableArray<SessionRegisteredTool> all = [Tool("read"), Tool("grep"), Tool(DocsTool, ToolExposure.Deferred)];
        // tool_search loaded the deferred docs tool; the user then turned grep off.
        var path = await RecordedSession(all, ["read", "grep", DocsTool], ["read", DocsTool]);
        try
        {
            // Reopened while the docs server is still connecting: its tool is not registered, so it is pending, not rejected.
            var before = FileBytes(path);
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Tool("grep")]));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read"], reopened.GetActiveTools(), "restored registered tools");
            Names([DocsTool], reopened.PendingToolNames, "restored unregistered tool is pending");
            // The restored loadout is applied in memory: opening writes nothing (the next request records it).
            Check(FileBytes(path).SequenceEqual(before), "opening the session wrote to its file");
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
            var count = reopened.Snapshot.Log.Entries.Length; var leaf = reopened.Snapshot.Context.LeafId;
            await reopened.PromptAsync(SettledUser("go")); await reopened.WaitForIdleAsync();
            Check(reopened.PendingToolNames.IsEmpty, "the prompt kept restored pending tools");
            // The run recorded the restored loadout before the prompt, so the transcript declares only registered tools.
            var record = RecordedAtPrompt(reopened, count, leaf);
            // declareToolChanges records the difference: the unregistered tool is removed, read is unchanged.
            Names([DocsTool], SystemTools(record, "toolsRemoved"), "recorded loadout removals");
            Names([], SystemTools(record, "toolsAdded"), "recorded loadout declarations");
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
            var before = FileBytes(path);
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Tool("grep")]));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read", "grep"], reopened.GetActiveTools(), "restored registered tools");
            Names([DocsTool], reopened.PendingToolNames, "pending before the selection");
            Check(FileBytes(path).SequenceEqual(before), "opening the session wrote to its file");
            await reopened.SetActiveToolsAsync(["read"]);
            Check(reopened.PendingToolNames.IsEmpty, "deactivating selection kept restored pending tools");
            await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred));
            Names(["read"], owner.Current.Session.GetActiveTools(), "dropped pending tool or turned-off grep came back");
            // setActiveToolsByName and the registration apply in memory: the next prompt records the deactivated tools' removal,
            // the unregistered docs tool included.
            Check(FileBytes(path).SequenceEqual(before), "the selection or the registration wrote to the session file");
            var count = reopened.Snapshot.Log.Entries.Length; var leaf = reopened.Snapshot.Context.LeafId;
            await reopened.PromptAsync(SettledUser("go")); await reopened.WaitForIdleAsync();
            var selection = RecordedAtPrompt(reopened, count, leaf);
            Names(["grep", DocsTool], SystemTools(selection, "toolsRemoved"), "the prompt removes the deactivated names");
            Names([], SystemTools(selection, "toolsAdded"), "the prompt adds nothing");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    private static async Task NavigationRestoresPendingTools()
    {
        var (session, directory) = await PendingSession([Tool("read"), Tool(DocsTool, ToolExposure.Deferred)]);
        try
        {
            await Recorded(session, ["read", DocsTool]); var withDocs = session.Snapshot.Context.LeafId!;
            await Recorded(session, ["read"]); var withoutDocs = session.Snapshot.Context.LeafId!;
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
            // Navigating to the branch that declared the docs tool restores its loadout instead of rejecting it, in memory: the
            // navigation writes no record (the next request records it).
            var count = session.Snapshot.Log.Entries.Length;
            var restored = await Navigate(withDocs);
            Names(["read"], session.GetActiveTools(), "navigation restores registered tools");
            Names([DocsTool], session.PendingToolNames, "navigation keeps the unregistered tool pending");
            Equal(withDocs, restored.LeafId, "navigation recorded the restored loadout");
            Equal(count, session.Snapshot.Log.Entries.Length, "navigation wrote to the session file");
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
            var before = FileBytes(path);
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Changed(DocsTool)]));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read", DocsTool], reopened.GetActiveTools(), "changed declaration restored by name");
            Check(reopened.PendingToolNames.IsEmpty, "a registered tool became pending");
            Check(FileBytes(path).SequenceEqual(before), "opening the session wrote to its file");
            var count = reopened.Snapshot.Log.Entries.Length; var leaf = reopened.Snapshot.Context.LeafId;
            await reopened.PromptAsync(SettledUser("go")); await reopened.WaitForIdleAsync();
            var record = RecordedAtPrompt(reopened, count, leaf);
            Equal(DocsTool + " tool, updated", Description(record, DocsTool), "restored loadout records the current declaration");
            Names([DocsTool], SystemTools(record, "toolsRemoved"), "the changed declaration is removed");
            Names([DocsTool], SystemTools(record, "toolsAdded"), "and added again");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }

        // Tree navigation to a branch recorded with the old declaration restores it with the current one.
        var (session, directory) = await PendingSession([Tool("read"), Tool(DocsTool)]);
        try
        {
            await Recorded(session, ["read", DocsTool]); var withDocs = session.Snapshot.Context.LeafId!;
            await Recorded(session, ["read"]);
            await Publish(session, [Tool("read"), Changed(DocsTool)], restoring: false);
            await using var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("No replacement expected."));
            var count = session.Snapshot.Log.Entries.Length;
            await NavigateTo(owner, withDocs);
            Names(["read", DocsTool], session.GetActiveTools(), "navigation restores the changed declaration by name");
            Equal(count, session.Snapshot.Log.Entries.Length, "navigation wrote to the session file");
            await session.PromptAsync(SettledUser("go")); await session.WaitForIdleAsync();
            var record = RecordedAtPrompt(session, count, withDocs);
            Equal(DocsTool + " tool, updated", Description(record, DocsTool), "the next prompt records the current declaration on the target branch");
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
            Names(["read"], reopened.Snapshot.Agent.Tools.Select(tool => tool.Name), "the agent's loadout leaves the hidden tool out");
            // The registration activates it again with the recorded declaration: the declared tools are unchanged, so nothing is recorded.
            var count = reopened.Snapshot.Log.Entries.Length;
            await ConnectDocs(owner, Tool(DocsTool));
            Equal(count, owner.Current.Session.Snapshot.Log.Entries.Length, "an unchanged loadout was recorded");
            Names(["read", DocsTool], owner.Current.Session.GetActiveTools(), "tool activates once it is declarable again");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }

        var (session, directory) = await PendingSession([Tool("read"), Tool(DocsTool)]);
        try
        {
            await Recorded(session, ["read", DocsTool]); var withDocs = session.Snapshot.Context.LeafId!;
            await Recorded(session, ["read"]);
            await Publish(session, [Tool("read"), Tool(DocsTool, ToolExposure.Hidden)], restoring: false);
            await using var owner = new ReplaceableAgentSession(session, (_, _) => throw new InvalidOperationException("No replacement expected."));
            var count = session.Snapshot.Log.Entries.Length;
            await NavigateTo(owner, withDocs);
            Names(["read"], session.GetActiveTools(), "navigation skips the hidden tool");
            await session.PromptAsync(SettledUser("go")); await session.WaitForIdleAsync();
            var record = RecordedAtPrompt(session, count, withDocs);
            Names([DocsTool], SystemTools(record, "toolsRemoved"), "the prompt removes the hidden tool");
            Names([], SystemTools(record, "toolsAdded"), "the prompt adds nothing");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>agent-loop declareToolChanges: the run's first entries are the loadout record (a system message on the previous leaf)
    /// and then the prompt's user message on it. Returns the record's message.</summary>
    private static JsonElement RecordedAtPrompt(PersistentAgentSession session, int count, string? leaf)
    {
        var added = session.Snapshot.Log.Entries.Skip(count).ToArray();
        Check(added.Length >= 2, "the prompt wrote no loadout record");
        Equal(leaf, added[0].ParentId, "loadout record parent");
        Equal("system", added[0].WireBody.Value.GetProperty("message").GetProperty("role").GetString(), "the run's first entry is the loadout record");
        Equal(added[0].Id, added[1].ParentId, "the prompt follows the loadout record");
        Equal("user", added[1].WireBody.Value.GetProperty("message").GetProperty("role").GetString(), "the prompt's user message follows the record");
        Equal(1, added.Count(entry => entry.WireBody.Value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
            message.GetProperty("role").GetString() == "system"),
            "loadout records written by the run");
        return added[0].WireBody.Value.GetProperty("message");
    }

    private sealed class CapturingTransport(List<ChatRequest> requests) : IChatTransport
    {
        private readonly SettledTransport _inner = new();
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken token = default)
        { lock (requests) requests.Add(request); return _inner.StreamAsync(request, token); }
    }

    private static string[] DeclaredTools(ChatRequest request) =>
        [.. new PiSharp.Sessions.Context.SessionSystemReplay().Replay(request.Messages).Tools.Select(tool => tool.Value.GetProperty("name").GetString()!)];

    // Source _restoreToolsFromTranscript applies the restored loadout in memory; agent-loop declareToolChanges records it before the
    // next prompt's messages. Between open and that prompt, operations the source does not persist leave the file untouched and
    // keep the restored loadout; those it persists (a custom message) write only their own entry.
    private static async Task OpenRecordsRestoredLoadoutAtFirstPrompt()
    {
        var path = await RecordedSession([Tool("read"), Tool("grep"), Tool(DocsTool)], ["read", DocsTool], ["read", "grep", DocsTool]);
        try
        {
            var before = FileBytes(path);
            var requests = new List<ChatRequest>();
            var registry = new SessionRuntimeRegistry([new(SettledModel, new CapturingTransport(requests))], [Tool("read"), Changed("grep")], new Deny());
            var reopened = await Reopen(path, registry);
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            Names(["read", "grep"], reopened.GetActiveTools(), "restored loadout");
            Names(["read", "grep"], reopened.Snapshot.Agent.Tools.Select(tool => tool.Name), "the agent runs the restored loadout");
            Names([DocsTool], reopened.PendingToolNames, "restored unregistered tool is pending");
            // Reads, an unchanged selection and tree navigation (without a summary) persist nothing.
            Names(["read", "grep"], reopened.GetToolActivationSelection().Names, "activation selection");
            Equal(reopened.GetToolActivationSelection().Revision, reopened.ScheduleToolActivation(["read", "grep"]).Revision, "an unchanged selection changed the activation");
            var leaf = reopened.Snapshot.Context.LeafId!;
            // The leaf of the first selection's exchange, before the second selection's record.
            var earlier = reopened.Snapshot.Context.Ancestry.Last(entry => entry.WireBody.Value.TryGetProperty("message", out var message) &&
                message.GetProperty("role").GetString() == "system").ParentId!;
            await NavigateTo(owner, earlier);
            Names(["read"], reopened.GetActiveTools(), "the earlier branch's loadout");
            await NavigateTo(owner, leaf);
            Names(["read", "grep"], reopened.GetActiveTools(), "the navigation back restored the loadout");
            Names([DocsTool], reopened.PendingToolNames, "the navigation back restored the pending tool");
            Check(FileBytes(path).SequenceEqual(before), "opening, reading or navigating the session wrote to its file");

            // A custom message is persisted at once (sendCustomMessage), resolved with the restored loadout; no loadout record joins it.
            var count = reopened.Snapshot.Log.Entries.Length;
            await reopened.SendCustomMessageAsync(reopened.Snapshot.Log.Header.Id, new("note", JsonData.Parse("\"remember\""), true), triggerTurn: false);
            Equal(count + 1, reopened.Snapshot.Log.Entries.Length, "the custom message wrote one entry");
            Equal("custom_message", reopened.Snapshot.Log.Entries[^1].Type, "the custom message entry");
            Names(["read", "grep"], reopened.GetActiveTools(), "the custom message kept the restored loadout");

            // The first prompt records the restored loadout before its user message: the recorded names are removed and the
            // registered tools declared with their current declarations.
            count = reopened.Snapshot.Log.Entries.Length; leaf = reopened.Snapshot.Context.LeafId!;
            await reopened.PromptAsync(SettledUser("go")); await reopened.WaitForIdleAsync();
            var record = RecordedAtPrompt(reopened, count, leaf);
            Names([DocsTool, "grep"], SystemTools(record, "toolsRemoved"), "unregistered and changed tools removed, in recorded order");
            Names(["grep"], SystemTools(record, "toolsAdded"), "changed tool added with its current declaration");
            Equal("grep tool, updated", Description(record, "grep"), "current declaration recorded");
            Names(["read", "grep"], DeclaredTools(requests.Single()), "the request declares the restored loadout");
            Check(reopened.PendingToolNames.IsEmpty, "the prompt kept pending tools");

            // The loadout is recorded: the next prompt writes no loadout record.
            count = reopened.Snapshot.Log.Entries.Length;
            await reopened.PromptAsync(SettledUser("again")); await reopened.WaitForIdleAsync();
            Check(reopened.Snapshot.Log.Entries.Skip(count).All(entry => !entry.WireBody.Value.TryGetProperty("message", out var message) ||
                message.GetProperty("role").GetString() != "system"), "a later prompt recorded the loadout again");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }

        // A selection between open and the first prompt replaces the restored loadout: the prompt records the selection.
        path = await RecordedSession([Tool("read"), Tool("grep"), Tool(DocsTool)], ["read", "grep", DocsTool]);
        try
        {
            var before = FileBytes(path);
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Tool("grep")]));
            await using var owner = new ReplaceableAgentSession(reopened, (_, _) => throw new InvalidOperationException("No replacement expected."));
            reopened.ScheduleToolActivation(["grep"]);
            Names(["grep"], reopened.GetActiveTools(), "scheduled selection");
            Check(reopened.PendingToolNames.IsEmpty, "a deactivating selection kept the restored pending tools");
            Check(FileBytes(path).SequenceEqual(before), "a scheduled selection wrote to the session file");
            var count = reopened.Snapshot.Log.Entries.Length; var leaf = reopened.Snapshot.Context.LeafId;
            await reopened.PromptAsync(SettledUser("go")); await reopened.WaitForIdleAsync();
            var record = RecordedAtPrompt(reopened, count, leaf);
            Names(["read", DocsTool], SystemTools(record, "toolsRemoved"), "the deselected and unregistered tools removed");
            Names([], SystemTools(record, "toolsAdded"), "the kept tool is not declared again");
            Names(["grep"], reopened.Snapshot.Agent.Tools.Select(tool => tool.Name), "the agent runs the selection");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    // agent-loop declareToolChanges with pi-ai getToolStateChanges/withToolChanges, as Pi 1.1.0 writes them (captured with the
    // installed Pi: a removal is {"role","content","sections","timestamp","toolsRemoved"}, a resumed changed declaration
    // {..., "toolsAdded":[ls,read], "toolsRemoved":[{"name":"ls"},{"name":"mcp__gone__search"}]}, a reorder writes no tool change).
    private static void ToolChangeRecords()
    {
        static TranscriptEntry System(string json) => new("system", JsonData.Parse(json));
        var registry = new SessionRuntimeRegistry([new(SettledModel, new SettledTransport())],
            [Tool("read"), Changed("grep"), Tool("ls"), Tool("find")], new Deny(), new()
            {
                PreparePromptSections = request => new(new object(), [KeyValuePair.Create("tools", string.Join(",", request.SelectedTools))], () => { })
            });
        static string Declaration(string name, string description) =>
            JsonSerializer.Serialize(new { name, description, parameters = new { type = "object" } });
        var transcript = ImmutableArray.Create(System("{\"role\":\"system\",\"content\":\"\",\"timestamp\":1,\"toolsAdded\":[" +
            string.Join(",", Declaration("read", "read tool"), Declaration("grep", "grep tool"), Declaration("gone", "gone tool")) + "]}"));
        // Changed declarations are removed and added again; added follows the loadout's order, removed the recorded order.
        var change = registry.CreateToolChangeMessage(transcript, ["ls", "grep", "read"], 5, default)!;
        Equal("{\"role\":\"system\",\"content\":\"\",\"timestamp\":5,\"toolsAdded\":[" +
            Declaration("ls", "ls tool") + "," + JsonSerializer.Serialize(new { name = "grep", description = "grep tool, updated",
                parameters = new { type = "object", properties = new { query = new { type = "string" } } } }) +
            "],\"toolsRemoved\":[{\"name\":\"grep\"},{\"name\":\"gone\"}]}", change.WireBody.Value.GetRawText(), "tool change record");
        // A removal only, and no record when the declared tools are unchanged (also when only their order changes).
        var applied = transcript.Add(change);
        Equal("{\"role\":\"system\",\"content\":\"\",\"timestamp\":6,\"toolsRemoved\":[{\"name\":\"read\"}]}",
            registry.CreateToolChangeMessage(applied, ["ls", "grep"], 6, default)!.WireBody.Value.GetRawText(), "removal record");
        Equal(null, registry.CreateToolChangeMessage(applied, ["read", "grep", "ls"], 7, default), "a reorder records no tool change");
        // With prompt sections the tool change follows them: role, content, sections, timestamp, toolsAdded, toolsRemoved.
        var (merged, _) = registry.PreparePromptSectionMessage(["ls", "find"], applied,
            registry.CreateToolChangeMessage(applied, ["ls", "find"], 8, default), 8, default);
        Names(["role", "content", "sections", "timestamp", "toolsAdded", "toolsRemoved"],
            merged!.WireBody.Value.EnumerateObject().Select(property => property.Name), "merged record fields");
        Equal("{\"tools\":\"ls,find\"}", merged.WireBody.Value.GetProperty("sections").GetRawText(), "merged sections");
        Equal(8, merged.WireBody.Value.GetProperty("timestamp").GetInt64(), "merged timestamp");
        var (sectionsOnly, _) = registry.PreparePromptSectionMessage(["read"], [], null, 9, default);
        Equal("{\"role\":\"system\",\"content\":\"\",\"sections\":{\"tools\":\"read\"},\"timestamp\":9}", sectionsOnly!.WireBody.Value.GetRawText(), "sections record");
    }

    // agent-session.ts setActiveToolsByName and _refreshToolRegistry apply at idle in memory; the next prompt records the
    // difference (captured with the installed Pi: a selection writes nothing, a reorder records no tool change).
    private static async Task IdleSelectionAndCatalogRecordedAtNextPrompt()
    {
        var (session, directory) = await PendingSession([Tool("read"), Tool("grep"), Tool("ls")]);
        var path = Path.Combine(directory, "session.jsonl");
        try
        {
            await Recorded(session, ["read", "grep"]);
            var before = FileBytes(path);
            await session.SetActiveToolsAsync(["grep", "read"]);
            Names(["grep", "read"], session.GetActiveTools(), "the selection's order");
            Names(["grep", "read"], session.Snapshot.Agent.Tools.Select(tool => tool.Name), "the agent runs the selection");
            Check(FileBytes(path).SequenceEqual(before), "an idle selection wrote to the session file");
            var count = session.Snapshot.Log.Entries.Length;
            await session.PromptAsync(SettledUser("reordered")); await session.WaitForIdleAsync();
            Check(session.Snapshot.Log.Entries.Skip(count).All(entry => !entry.WireBody.Value.TryGetProperty("message", out var message) ||
                message.GetProperty("role").GetString() != "system"), "a reorder recorded a tool change");
            Names(["grep", "read"], session.GetActiveTools(), "the prompt kept the selection's order");

            before = FileBytes(path);
            await session.SetActiveToolsAsync(["ls"]);
            await Publish(session, [Tool("read"), Changed("grep"), Tool("ls")], restoring: false);
            Names(["ls"], session.GetActiveTools(), "the catalog change keeps the selection");
            Check(FileBytes(path).SequenceEqual(before), "an idle selection or catalog publication wrote to the session file");
            count = session.Snapshot.Log.Entries.Length; var leaf = session.Snapshot.Context.LeafId;
            await session.PromptAsync(SettledUser("go")); await session.WaitForIdleAsync();
            var record = RecordedAtPrompt(session, count, leaf);
            Names(["ls"], SystemTools(record, "toolsAdded"), "the next prompt declares the selected tool");
            Names(["read", "grep"], SystemTools(record, "toolsRemoved"), "and removes the deselected ones in recorded order");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }
    }

    // agent-session.ts systemPrompt and emitBeforeAgentStart(_baseSystemPromptOptions): _rebuildSystemPrompt applies a selection to the
    // prompt at once, before the next prompt records it; exportToHtml lists agent.state.tools, the in-memory loadout.
    private static async Task InMemoryLoadoutPrompt()
    {
        var directory = Temp("prompt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); var ids = 0;
        var started = new List<string>();
        var hooks = new AgentHooks
        {
            BeforePrompt = (start, token) =>
            {
                lock (started) started.Add(new PiSharp.Sessions.Context.SessionSystemReplay().Replay(start.History.AddRange(start.Inputs), token).Prompt);
                return ValueTask.FromResult(new AgentPromptPreparation([]));
            }
        };
        var registry = new SessionRuntimeRegistry([new(SettledModel, new SettledTransport(), Hooks: hooks)], [Tool("read"), Tool("grep")], new Deny(), new()
        {
            PreparePromptSections = request => new(new object(), [KeyValuePair.Create("tools", "<tools>" + string.Join(",", request.SelectedTools) + "</tools>")], () => { })
        });
        var header = new PiSharp.Sessions.Serialization.SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "prompt-header",
            timestamp = "2026-10-08T00:00:00.000Z", cwd = directory }));
        var session = await PersistentAgentSession.CreateAsync(Path.Combine(directory, "session.jsonl"), header, registry, SettledModel,
            () => 7, () => "prompt-entry-" + Interlocked.Increment(ref ids));
        try
        {
            await Recorded(session, ["read"]);
            Equal("<tools>read</tools>", started.Single(), "before_agent_start of the recorded selection");
            Equal("<tools>read</tools>", session.GetSystemPrompt(), "prompt after the run");
            await session.SetActiveToolsAsync(["grep", "read"]);
            // The transcript still declares the recorded prompt; the in-memory prompt is the selection's.
            Equal("<tools>read</tools>", new PiSharp.Sessions.Context.SessionSystemReplay().Replay(session.Snapshot.Context.Messages).Prompt, "transcript prompt");
            Equal("<tools>grep,read</tools>", session.GetSystemPrompt(), "getSystemPrompt after an idle selection");
            Names(["grep", "read"], session.GetActiveToolDeclarations().Select(tool => tool.Value.GetProperty("name").GetString()!), "exported tools");
            await session.PromptAsync(SettledUser("go")); await session.WaitForIdleAsync();
            Equal("<tools>grep,read</tools>", started[^1], "before_agent_start sees the in-memory prompt");
            Equal("<tools>grep,read</tools>", new PiSharp.Sessions.Context.SessionSystemReplay().Replay(session.Snapshot.Context.Messages).Prompt, "recorded by the prompt");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }
    }

    private static async Task NavigationWithoutSystemMessageKeepsTools()
    {
        var (session, directory) = await PendingSession([Tool("read"), Tool("grep"), Tool(DocsTool)]);
        try
        {
            // Session creation records the model and thinking level but no system message.
            var start = session.Snapshot.Context.LeafId!;
            Check(!session.Snapshot.Context.LlmMessages.Any(message => message.Role == "system"), "fixture start declares a system message");
            await Recorded(session, ["read", "grep", DocsTool]); var withDocs = session.Snapshot.Context.LeafId!;
            await Recorded(session, ["read", "grep"]);
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
