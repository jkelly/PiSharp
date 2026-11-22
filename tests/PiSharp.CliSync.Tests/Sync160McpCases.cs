// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/core/agent-session.ts (_restoredToolNames, #11996:
// setActiveToolsByName, _restoreToolsFromTranscript, reload), packages/coding-agent/test/suite/agent-session-mcp.test.ts ("keeps
// restored tools when an extension deactivates only its own tool before they register", "keeps tools tool_search loaded after
// /reload when an extension deactivates only its own tool"). Authored native expectations, never captured from an upstream run.
using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.ToolSelection;
using PiSharp.Contracts;

// Before 1.6.0 any selection that deactivated a tool dropped the pending tools of a restored or reloaded loadout. Extensions that
// register a loader tool and hide it at session_start (pi-web-access and pi-subagents with toolActivation "auto") did exactly that:
// the restore or reload activates the loader as a tool the branch never recorded, the extension deactivates it again, and the MCP
// tools tool_search had loaded, still pending while their server connects, were dropped. Now only a selection that deactivates a
// tool of the restored or reloaded loadout drops them.
internal static partial class Program
{
    private const string LoaderTool = "loader";

    private static IEnumerable<(string Id, Func<Task> Run)> Sync160McpCases() =>
    [
        ("sync160.resume-keeps-pending-tools-when-an-extension-deactivates-only-its-own-tool", ResumeKeepsPendingAfterOwnToolDeactivation),
        ("sync160.resume-drops-pending-tools-when-a-restored-or-modifier-added-tool-is-deactivated", ResumeDropsPendingAfterRestoredToolDeactivation),
        ("sync160.reload-keeps-pending-tools-when-an-extension-deactivates-only-its-own-tool", ReloadKeepsPendingAfterOwnToolDeactivation),
        ("sync160.reload-publications-of-one-reservation-form-one-reloaded-loadout", ReloadReservationFormsOneLoadout),
        ("sync160.tree-navigation-keeps-pending-tools-when-only-an-unrestored-tool-is-deactivated", NavigationKeepsPendingAfterOwnToolDeactivation),
    ];

    private static ReplaceableAgentSession Owner(PersistentAgentSession session) =>
        new(session, (_, _) => throw new InvalidOperationException("No replacement expected."));

    // agent-session-mcp.test.ts "keeps restored tools when an extension deactivates only its own tool before they register".
    private static async Task ResumeKeepsPendingAfterOwnToolDeactivation()
    {
        // The first session: tool_search loaded the deferred docs tool. The extension's loader was registered and hidden at
        // session_start, so the branch never recorded it.
        ImmutableArray<SessionRegisteredTool> first = [Tool("read"), Tool(LoaderTool), Tool(DocsTool, ToolExposure.Deferred)];
        foreach (var scheduled in new[] { false, true })
        {
            var what = scheduled ? "scheduled activation: " : "idle selection: ";
            var path = await RecordedSession(first, ["read", DocsTool]);
            try
            {
                // Resumed while the docs server is still connecting: the restore activates the loader as a newly registered
                // extension tool and keeps the docs tool pending.
                var before = FileBytes(path);
                var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Tool(LoaderTool)]));
                await using var owner = Owner(reopened);
                Names(["read", LoaderTool], reopened.GetActiveTools(), what + "restore activates the unrecorded extension tool");
                Names([DocsTool], reopened.PendingToolNames, what + "restored unregistered tool is pending");
                // session_start: the extension deactivates only its own tool (pi.setActiveTools(pi.getActiveTools().filter(...))),
                // through the idle selection or, as native extensions do during a callback, a scheduled activation.
                if (scheduled) Names(["read"], reopened.ScheduleToolActivation(["read"]).Names, what + "selection");
                else await reopened.SetActiveToolsAsync(["read"]);
                Names(["read"], reopened.GetActiveTools(), what + "the loader is deactivated");
                Names([DocsTool], reopened.PendingToolNames, what + "deactivating only the unrestored loader dropped the pending tools");
                // The server connects: the tool tool_search loaded is active again, and the loader stays off.
                await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred));
                Names(["read", DocsTool], owner.Current.Session.GetActiveTools(), what + "pending tool activates on registration");
                Check(owner.Current.Session.PendingToolNames.IsEmpty, what + "activated tool stayed pending");
                Check(FileBytes(path).SequenceEqual(before), what + "the restore, the selection or the registration wrote to the session file");
                // The loadout equals the recorded one again, so the next prompt records no tool change (no removal of the docs tool).
                var records = SystemRecords(reopened);
                await reopened.PromptAsync(SettledUser("use it")); await reopened.WaitForIdleAsync();
                Equal(records, SystemRecords(reopened), what + "the prompt recorded a tool change");
            }
            finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
        }

        // A selection that only adds a tool and one that deactivates only the loader both keep the pending tools, in either order.
        var mixed = await RecordedSession([Tool("read"), Tool("grep"), Tool(LoaderTool), Tool(DocsTool, ToolExposure.Deferred)], ["read", DocsTool]);
        try
        {
            var reopened = await Reopen(mixed, RestoredRegistry([Tool("read"), Tool("grep") with { DefaultActive = false }, Tool(LoaderTool)]));
            await using var owner = Owner(reopened);
            Names(["read", LoaderTool], reopened.GetActiveTools(), "restored tools");
            await reopened.SetActiveToolsAsync(["read", LoaderTool, "grep"]);
            Names([DocsTool], reopened.PendingToolNames, "add-only selection keeps pending");
            // grep was activated after the restore: it is outside the restored loadout as well.
            await reopened.SetActiveToolsAsync(["read"]);
            Names([DocsTool], reopened.PendingToolNames, "deactivating tools outside the restored loadout dropped the pending tools");
            await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred));
            Names(["read", DocsTool], owner.Current.Session.GetActiveTools(), "pending tool activates on registration");
        }
        finally { Directory.Delete(Path.GetDirectoryName(mixed)!, recursive: true); }
    }

    // setActiveToolsByName: `previous.some((name) => !active.has(name) && this._restoredToolNames.has(name))` still drops the pending
    // tools, and _restoredToolNames is the recorded loadout with the selection's +name/-name modifiers applied.
    private static async Task ResumeDropsPendingAfterRestoredToolDeactivation()
    {
        ImmutableArray<SessionRegisteredTool> first = [Tool("read"), Tool("grep"), Tool(LoaderTool), Tool(DocsTool, ToolExposure.Deferred)];
        ImmutableArray<SessionRegisteredTool> resumed = [Tool("read"), Tool("grep"), Tool(LoaderTool)];
        // The extension deactivates its loader together with a restored tool (like plan mode setting its own loadout).
        var path = await RecordedSession(first, ["read", "grep", DocsTool]);
        try
        {
            var reopened = await Reopen(path, RestoredRegistry(resumed));
            await using var owner = Owner(reopened);
            Names(["read", "grep", LoaderTool], reopened.GetActiveTools(), "restored tools");
            Names([DocsTool], reopened.PendingToolNames, "pending before the selection");
            await reopened.SetActiveToolsAsync(["read"]);
            Check(reopened.PendingToolNames.IsEmpty, "deactivating a restored tool kept the pending tools");
            await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred));
            Names(["read"], owner.Current.Session.GetActiveTools(), "dropped pending tool came back");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }

        // `--tools +loader`: the modifier makes the loader part of the restored loadout, so deactivating it replaces that loadout.
        path = await RecordedSession(first, ["read", DocsTool]);
        try
        {
            var reopened = await Reopen(path, RestoredRegistry([Tool("read"), Tool(LoaderTool)]).RetainToolSelection(AllowedToolSelection.Create(["+" + LoaderTool])));
            await using var owner = Owner(reopened);
            Names(["read", LoaderTool], reopened.GetActiveTools(), "+loader restored");
            Names([DocsTool], reopened.PendingToolNames, "+loader: pending before the selection");
            await reopened.SetActiveToolsAsync(["read"]);
            Check(reopened.PendingToolNames.IsEmpty, "+loader: deactivating the modifier-added tool kept the pending tools");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }

        // `--tools -grep`: the modifier takes grep out of the restored loadout; the loader is still outside it.
        path = await RecordedSession(first, ["read", "grep", DocsTool]);
        try
        {
            var reopened = await Reopen(path, RestoredRegistry(resumed).RetainToolSelection(AllowedToolSelection.Create(["-grep"])));
            await using var owner = Owner(reopened);
            Names(["read", LoaderTool], reopened.GetActiveTools(), "-grep restored");
            Names([DocsTool], reopened.PendingToolNames, "-grep: pending before the selection");
            await reopened.SetActiveToolsAsync(["read"]);
            Names([DocsTool], reopened.PendingToolNames, "-grep: deactivating only the loader dropped the pending tools");
            await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred));
            Names(["read", DocsTool], owner.Current.Session.GetActiveTools(), "-grep: pending tool activates on registration");
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    /// <summary>A reload's catalog publication: the previously active names stay pending, and the reload planner's selection
    /// (<paramref name="active"/>: the previous names plus the extension tools the reloaded runtime activates) is requested.</summary>
    private static Task Reloaded(PersistentAgentSession session, ImmutableArray<SessionRegisteredTool> tools, ImmutableArray<string> active)
    {
        var expected = session.CaptureToolCatalogRegistry();
        return session.PublishRestoringToolCatalogAsync(expected, expected.WithToolCatalog(tools, null), active, () => { });
    }

    // agent-session-mcp.test.ts "keeps tools tool_search loaded after /reload when an extension deactivates only its own tool".
    private static async Task ReloadKeepsPendingAfterOwnToolDeactivation()
    {
        ImmutableArray<SessionRegisteredTool> all = [Tool("read"), Tool("grep"), Tool(LoaderTool), Tool(DocsTool, ToolExposure.Deferred)];
        var (session, directory) = await PendingSession(all);
        try
        {
            // tool_search loaded the docs tool; the extension hid its loader at session_start.
            await session.SetActiveToolsAsync(["read", "grep", DocsTool]);
            // /reload: the extensions load again, which activates the loader as a newly registered tool (includeAllExtensionTools),
            // and the docs server reconnects in the background.
            await Reloaded(session, [Tool("read"), Tool("grep"), Tool(LoaderTool)], ["read", "grep", LoaderTool]);
            Names(["read", "grep", LoaderTool], session.GetActiveTools(), "active after reload");
            Names([DocsTool], session.PendingToolNames, "pending after reload");
            // session_start (reason "reload"): the extension deactivates only its own tool.
            await session.SetActiveToolsAsync(["read", "grep"]);
            Names([DocsTool], session.PendingToolNames, "deactivating only the loader dropped the reloaded pending tools");
            await Publish(session, all, restoring: false);
            Names(["read", "grep", DocsTool], session.GetActiveTools(), "the loaded tool is active again once its server reconnects");
            Check(session.PendingToolNames.IsEmpty, "activated tool stayed pending");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }

        // Deactivating a tool that was active before the reload still replaces the reloaded loadout.
        (session, directory) = await PendingSession(all);
        try
        {
            await session.SetActiveToolsAsync(["read", "grep", DocsTool]);
            await Reloaded(session, [Tool("read"), Tool("grep"), Tool(LoaderTool)], ["read", "grep", LoaderTool]);
            await session.SetActiveToolsAsync(["read", LoaderTool]);
            Check(session.PendingToolNames.IsEmpty, "deactivating a reloaded tool kept the pending tools");
            await Publish(session, all, restoring: false);
            Names(["read", LoaderTool], session.GetActiveTools(), "dropped pending tool resurrected");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }

        // Each reload replaces the reloaded loadout: the loader a first reload left active belongs to the second reload's loadout.
        (session, directory) = await PendingSession(all);
        try
        {
            await session.SetActiveToolsAsync(["read", DocsTool]);
            await Reloaded(session, [Tool("read"), Tool(LoaderTool)], ["read", LoaderTool]);
            await Reloaded(session, [Tool("read"), Tool(LoaderTool)], ["read", LoaderTool]);
            Names([DocsTool], session.PendingToolNames, "pending after the second reload");
            await session.SetActiveToolsAsync(["read"]);
            Check(session.PendingToolNames.IsEmpty, "the loader was active before the second reload, so deactivating it replaces the reloaded loadout");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }
    }

    // A native reload publishes more than one catalog under its replacement reservation: the owned resources (MCP servers) retire
    // first, then the reloaded runtime's catalog is published. Upstream reload has one loadout, the names active before it
    // (`this._restoredToolNames = new Set(this.getActiveToolNames())`), so the names of every publication of the reservation count.
    private static async Task ReloadReservationFormsOneLoadout()
    {
        const string WebTool = "mcp__web__search";
        ImmutableArray<SessionRegisteredTool> all = [Tool("read"), Tool(LoaderTool), Tool(DocsTool, ToolExposure.Deferred), Tool(WebTool, ToolExposure.Deferred)];
        var (session, directory) = await PendingSession(all);
        try
        {
            await session.SetActiveToolsAsync(["read", DocsTool, WebTool]);
            using (var reservation = session.ReserveReplacement())
            {
                // The MCP servers retire: their tools leave the catalog and stay pending.
                var expected = reservation.CaptureToolCatalogRegistry();
                await reservation.PublishToolCatalogAsync(expected, expected.WithToolCatalog([Tool("read"), Tool(LoaderTool)], null), ["read"], () => { });
                Names(["read"], reservation.CaptureActiveToolNames(), "active after the servers retired");
                // The reloaded runtime's catalog: the loader is activated as a newly registered extension tool.
                expected = reservation.CaptureToolCatalogRegistry();
                await reservation.PublishToolCatalogAsync(expected, expected.WithToolCatalog([Tool("read"), Tool(LoaderTool)], null), ["read", LoaderTool], () => { });
            }
            Names(["read", LoaderTool], session.GetActiveTools(), "active after reload");
            Names([DocsTool, WebTool], session.PendingToolNames, "pending after reload");
            // The loader was not active before the reload: deactivating it keeps the pending tools.
            await session.SetActiveToolsAsync(["read"]);
            Names([DocsTool, WebTool], session.PendingToolNames, "deactivating only the loader dropped the reloaded pending tools");
            // The docs server reconnects; the web server is still connecting.
            await Publish(session, [Tool("read"), Tool(LoaderTool), Tool(DocsTool, ToolExposure.Deferred)], restoring: false);
            Names(["read", DocsTool], session.GetActiveTools(), "the docs tool is active again");
            Names([WebTool], session.PendingToolNames, "the web tool is still pending");
            // The docs tool was active before the reload (it retired with the first publication of the reservation), so deactivating
            // it replaces the reloaded loadout.
            await session.SetActiveToolsAsync(["read"]);
            Check(session.PendingToolNames.IsEmpty, "deactivating a tool of the reloaded loadout kept the pending tools");
            await Publish(session, all, restoring: false);
            Names(["read"], session.GetActiveTools(), "dropped pending tool resurrected");
        }
        finally { await session.DisposeAsync(); Directory.Delete(directory, recursive: true); }
    }

    // navigateTree -> _restoreToolsFromTranscript() recomputes _restoredToolNames from the target branch (without modifiers).
    private static async Task NavigationKeepsPendingAfterOwnToolDeactivation()
    {
        var (session, directory) = await PendingSession([Tool("read"), Tool("grep"), Tool(DocsTool, ToolExposure.Deferred)]);
        try
        {
            await Recorded(session, ["read", "grep", DocsTool]); var withDocs = session.Snapshot.Context.LeafId!;
            await Recorded(session, ["read"]); var withoutDocs = session.Snapshot.Context.LeafId!;
            // The docs server disconnects, and an extension registers its loader (kept inactive by the publication).
            await Publish(session, [Tool("read"), Tool("grep"), Tool(LoaderTool)], restoring: false);
            await using var owner = Owner(session);
            Names(["read"], session.GetActiveTools(), "the publication keeps the active names");
            // The branch never mentioned the loader: navigation activates it as a newly registered extension tool.
            await NavigateTo(owner, withDocs);
            Names(["read", "grep", LoaderTool], session.GetActiveTools(), "navigation restores the branch and adds the loader");
            Names([DocsTool], session.PendingToolNames, "navigation keeps the unregistered tool pending");
            // session_tree: the extension deactivates only its own tool.
            await session.SetActiveToolsAsync(["read", "grep"]);
            Names([DocsTool], session.PendingToolNames, "deactivating only the loader dropped the restored pending tools");
            // Deactivating a tool the branch records replaces the restored loadout.
            await session.SetActiveToolsAsync(["read"]);
            Check(session.PendingToolNames.IsEmpty, "deactivating a restored tool kept the pending tools");
            // Navigating again restores the target's loadout and its pending tools; the previous branch's loadout no longer counts.
            await NavigateTo(owner, withoutDocs);
            Check(session.PendingToolNames.IsEmpty, "navigation kept the previous branch's pending tools");
            await NavigateTo(owner, withDocs);
            Names([DocsTool], session.PendingToolNames, "pending again on the branch that declared it");
            await session.SetActiveToolsAsync(session.GetActiveTools().Remove(LoaderTool));
            Names([DocsTool], session.PendingToolNames, "deactivating only the loader after navigating back dropped the pending tools");
            await ConnectDocs(owner, Tool(DocsTool, ToolExposure.Deferred));
            Check(owner.Current.Session.GetActiveTools().Contains(DocsTool) && !owner.Current.Session.GetActiveTools().Contains(LoaderTool),
                "pending tool activates after navigation: " + string.Join(",", owner.Current.Session.GetActiveTools()));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
