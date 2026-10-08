using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Prompts;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Sessions.Context;

// Upstream: packages/coding-agent/src/extensions/mcp/index.ts (before_agent_start renders `mcp_servers` into
// systemPromptOptions.sections) and core/system-prompt.ts + agent-session.ts (diffSystemPromptSections appends a changed
// section as a system message). Expected text is typed from that source; no upstream execution.
internal static partial class Program
{
    private static McpServerEntry McpEntry(string name, string json) =>
        new(name, McpConfigurationReader.Validate(name, JsonData.Parse(json).Value).Config!, "mcp.json", McpConfigurationScope.Global);

    private static async Task McpServersPromptSection()
    {
        var root = Temp("mcp-prompt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        OfflineSessionProfile? profile = null;
        try
        {
            profile = await OfflineSessionProfile.CreateAsync(root, Path.Combine(root, "session.jsonl"), null, [], [], [], default,
                originalSystemPrompt: new OriginalSystemPromptAdmission { CustomPrompt = "Custom." });
            var source = new McpServersPromptSource();
            profile.ConfigureMcpServersPromptSection(source);
            Throws<InvalidOperationException>(() => profile.ConfigureMcpServersPromptSection(new()), "second section source");
            var registry = profile.DecorateDurablePromptRegistry(profile.Registry);
            var cwd = Wrap("cwd", Path.GetFullPath(root).Replace('\\', '/'));
            const string Intro = "MCP servers whose tools are not declared to you. Call the tools of `codemode` servers from codemode scripts. Load the tools of `tool_search` servers with `tool_search`.";

            // Prompt start before any server connected: every configured server, `docs` by its description.
            var docs = McpEntry("docs", """{"url":"https://docs.example/mcp","description":"Docs search.\nSecond line."}""");
            var later = McpEntry("later-server", """{"command":"x","exposure":"deferred"}""");
            var direct = McpEntry("direct", """{"command":"x","exposure":"direct"}""");
            source.Publish(1, new([docs, later, direct], []), []);
            var firstSection = Wrap("mcp_servers", Intro + "\n- mcp__docs (codemode): Docs search.\n- mcp__later_server (tool_search)");
            var first = registry.PreparePromptSectionMessage([], [], null, 0, default).Message!;
            Equal("system", first.Role, "first prompt role");
            Names(["role", "content", "timestamp", "sections"], first.WireBody.Value.EnumerateObject().Select(property => property.Name), "first prompt fields");
            Equal("", first.WireBody.Value.GetProperty("content").GetString(), "first prompt content");
            SectionsEqual([("preamble", "Custom."), ("cwd", cwd), ("mcp_servers", firstSection)],
                [.. first.WireBody.Value.GetProperty("sections").EnumerateObject().Select(property => KeyValuePair.Create(property.Name, property.Value.GetString()!))],
                "first prompt");

            // Unchanged at the next prompt start: nothing is appended.
            var (unchanged, _) = registry.PreparePromptSectionMessage([], [first], null, 1, default);
            Equal(null, unchanged, "unchanged section appends nothing");

            // A background server connected and reported instructions: only the changed section is appended.
            Check(source.Connected(1, new(later, [], "Searches later.\nIgnored.")), "connected server recorded");
            Check(!source.Connected(2, new(later, [], "Other generation.")), "unknown generation ignored");
            var changed = registry.PreparePromptSectionMessage([], [first], null, 5, default).Message!;
            var changedSection = Wrap("mcp_servers", Intro + "\n- mcp__docs (codemode): Docs search.\n- mcp__later_server (tool_search): Searches later.");
            Equal("{\"role\":\"system\",\"content\":\"\",\"timestamp\":5,\"sections\":{\"mcp_servers\":" + JsonSerializer.Serialize(changedSection) + "}}",
                changed.WireBody.ToString(), "appended change message");
            Equal(changedSection, changed.WireBody.Value.GetProperty("sections").GetProperty("mcp_servers").GetString(), "appended section text");

            // The servers left: the section is removed with a null value.
            source.Publish(2, new([direct], []), []);
            var removed = registry.PreparePromptSectionMessage([], [first, changed], null, 9, default).Message!;
            Equal("{\"role\":\"system\",\"content\":\"\",\"timestamp\":9,\"sections\":{\"mcp_servers\":null}}", removed.WireBody.ToString(), "removal message");
            var (afterRemoval, _) = registry.PreparePromptSectionMessage([], [first, changed, removed], null, 10, default);
            Equal(null, afterRemoval, "removed section stays removed");
            // Replayed in order, the transcript's current sections end without the section.
            var replayed = new SessionSystemReplay().Replay([first, changed, removed], default).CurrentMessage!;
            Check(!replayed.WireBody.Value.GetProperty("sections").TryGetProperty("mcp_servers", out _), "replay keeps the removal");

            // Like a before_agent_start handler: set in place, append when new, delete when null.
            var snapshot = OriginalSystemPromptBuilder.Capture(new OriginalSystemPromptAdmission { CustomPrompt = "C.",
                Sections = [KeyValuePair.Create("mcp_servers", "old"), KeyValuePair.Create("extra", "x")] }, Path.GetFullPath(root), []);
            Names(["mcp_servers", "extra"], OriginalSystemPromptBuilder.WithSection(snapshot, "mcp_servers", "new").Input.Sections.Select(row => row.Key), "set in place");
            Equal("new", OriginalSystemPromptBuilder.WithSection(snapshot, "mcp_servers", "new").Input.Sections[0].Value, "set value");
            Names(["extra"], OriginalSystemPromptBuilder.WithSection(snapshot, "mcp_servers", null).Input.Sections.Select(row => row.Key), "delete");
            Check(ReferenceEquals(snapshot, OriginalSystemPromptBuilder.WithSection(snapshot, "absent", null)), "deleting an absent section keeps the snapshot");
            Throws<ArgumentException>(() => OriginalSystemPromptBuilder.WithSection(snapshot, "preamble", "x"), "preamble is not a custom section");
        }
        finally
        {
            if (profile is not null) await profile.DisposeAsync();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
