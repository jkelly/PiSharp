using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Prompts;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;

// extensions/mcp/index.ts renderServersSection and before_agent_start: every enabled server with `deferred` (or codemode) tools is
// listed, sorted by name, as a `tool_search` server with the first line of its description, or of its instructions once
// connected; the intro names only the ways of reaching tools the listed servers use. Direct-only and disabled servers are left out.
internal static partial class Program
{
    private static McpServerEntry McpEntry(string name, string json) =>
        new(name, McpConfigurationReader.Validate(name, JsonData.Parse(json).Value).Config!, "mcp.json", McpConfigurationScope.Global);

    private static async Task ServersSectionForDeferredServers()
    {
        const string Intro = "MCP servers whose tools are not declared to you. Load the tools of `tool_search` servers with `tool_search`.";
        var docs = McpEntry("docs", """{"command":"x","exposure":"deferred","description":"Documentation search.\nMore."}""");
        var beta = McpEntry("beta-tools", """{"command":"x","exposure":"deferred"}""");
        var mixed = McpEntry("mixed", """{"command":"x","exposure":"direct","toolExposure":{"lookup":"deferred"}}""");
        var direct = McpEntry("direct", """{"command":"x","exposure":"direct"}""");
        var off = McpEntry("off", """{"command":"x","exposure":"deferred","enabled":false}""");
        Equal(Intro + "\n- mcp__beta_tools (tool_search): Beta instructions.\n- mcp__docs (tool_search): Documentation search.\n- mcp__mixed (tool_search)",
            McpServersSection.Render([new(docs), new(beta, "Beta instructions.\nSecond."), new(mixed), new(direct), new(off)]), "rendered section");
        Equal(null, McpServersSection.Render([new(direct), new(off)]), "no deferred server, no section");

        // Through the profile's durable prompt sections: the first prompt carries the section; a server's instructions arriving
        // with its background connection append the changed section.
        var root = Temp("section-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        OfflineSessionProfile? profile = null;
        try
        {
            profile = await OfflineSessionProfile.CreateAsync(root, Path.Combine(root, "session.jsonl"), null, [], [], [], default,
                originalSystemPrompt: new OriginalSystemPromptAdmission { CustomPrompt = "Custom." });
            var source = new McpServersPromptSource();
            profile.ConfigureMcpServersPromptSection(source);
            var registry = profile.DecorateDurablePromptRegistry(profile.Registry);
            source.Publish(1, new([beta, direct], []), []);
            var first = registry.PreparePromptSectionMessage([], [], null, 0, default).Message!;
            Equal($"<mcp_servers>\n{Intro}\n- mcp__beta_tools (tool_search)\n</mcp_servers>",
                first.WireBody.Value.GetProperty("sections").GetProperty("mcp_servers").GetString(), "first prompt section");
            Check(source.Connected(1, new(beta, [], "Beta instructions.\nSecond.")), "connected server recorded");
            var changed = registry.PreparePromptSectionMessage([], [first], null, 5, default).Message!;
            Equal($"<mcp_servers>\n{Intro}\n- mcp__beta_tools (tool_search): Beta instructions.\n</mcp_servers>",
                changed.WireBody.Value.GetProperty("sections").GetProperty("mcp_servers").GetString(), "appended section after connection");
        }
        finally
        {
            if (profile is not null) await profile.DisposeAsync();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
