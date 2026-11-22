// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/extensions/mcp/index.ts (renderServersSection with the
// library's mcpServerSummary), packages/mcp/src/naming.ts (mcpServerSummary), packages/mcp/test/naming.test.ts,
// packages/coding-agent/src/core/agent-session.ts (_restoredToolNames, #11996), packages/coding-agent/test/suite/agent-session-mcp.test.ts
// ("keeps restored tools when an extension deactivates only its own tool before they register").
// Authored native expectations, never captured from an upstream run.
using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Configuration;

internal static partial class Program
{
    internal static IEnumerable<(string Id, Func<Task> Run)> Sync160McpCases() =>
    [
        ("sync160.servers-section-summary-is-the-library-server-summary", ServersSectionSummary160),
        ("sync160.resume-keeps-the-loaded-tool-when-only-an-unrestored-tool-is-deactivated", ResumeKeepsLoadedTool160),
    ];

    private static McpServerEntry Entry160(string name, string? description)
    {
        var json = JsonSerializer.Serialize(description is null ? new Dictionary<string, object> { ["command"] = "x", ["exposure"] = "deferred" }
            : new Dictionary<string, object> { ["command"] = "x", ["exposure"] = "deferred", ["description"] = description });
        var result = McpConfigurationReader.Validate(name, JsonData.Parse(json).Value);
        return new(name, result.Config ?? throw new InvalidOperationException(result.Error), "mcp.json", McpConfigurationScope.Global);
    }

    // naming.ts mcpServerSummary(description, instructions): `description?.trim() || instructions?.trim() || ""`, then the trimmed
    // first line. The extension's own serverSummary (before 1.6.0) took the first line of the untrimmed instructions, so
    // instructions that start with a blank line gave no summary; String.prototype.trim decides what is blank.
    private static Task ServersSectionSummary160()
    {
        const string Intro = "MCP servers whose tools are not declared to you. Load the tools of `tool_search` servers with `tool_search`.";
        (string Name, string? Description, string? Instructions, string? Summary)[] servers =
        [
            // packages/mcp/test/naming.test.ts.
            ("alpha", " Docs search.\nMore. ", "Ignored.", "Docs search."),
            ("bravo", null, "\n  Web search for current information.\nDetails.", "Web search for current information."),
            ("charlie", "  ", "From instructions.", "From instructions."),
            ("delta", null, null, null),
            // CRLF line ends, leading blank lines and blank-only text.
            ("echo", "First line.\r\nSecond.", "Ignored.", "First line."),
            ("foxtrot", null, "\r\n\r\n  CRLF instructions.\r\nMore.", "CRLF instructions."),
            ("golf", "\n\n\tReal description.\nMore.", "Ignored.", "Real description."),
            ("hotel", null, " \n\t \r\n", null),
            ("india", "", "An empty description falls through.", "An empty description falls through."),
            ("juliet", " \n ", " \n\n ", null),
            // String.prototype.trim: U+FEFF, U+00A0 and U+2028 are white space; U+0085 is not, so that description is not blank.
            ("kilo", "\uFEFF\u00A0Trimmed like JavaScript.\u2028", "Ignored.", "Trimmed like JavaScript."),
            ("lima", "\u0085", "Not used.", "\u0085"),
        ];
        var expected = string.Join('\n', servers.Select(server => $"- mcp__{server.Name} (tool_search)" + (server.Summary is null ? "" : ": " + server.Summary)).Prepend(Intro));
        Equal(expected, McpServersSection.Render(servers.Select(server => new McpServerListing(Entry160(server.Name, server.Description), server.Instructions))),
            "rendered section");

        // Through the session's prompt source: the summary appears once the server connected and sent its instructions.
        var source = new McpServersPromptSource();
        var bravo = Entry160("bravo", null);
        source.Publish(1, new([bravo], []), []);
        Equal(Intro + "\n- mcp__bravo (tool_search)", source.Render(1), "before the server connected");
        Check(source.Connected(1, new(bravo, [], "\n  Web search for current information.\nDetails.")), "connected server recorded");
        Equal(Intro + "\n- mcp__bravo (tool_search): Web search for current information.", source.Render(1), "instructions that start with a blank line");
        return Task.CompletedTask;
    }

    /// <summary>Polls a session that background servers publish to; an owner operation may hold it meanwhile.</summary>
    private static async Task Until160(Func<bool> condition, Rpc rpc, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try { if (condition()) return; } catch (InvalidOperationException) { }
            if (rpc.Completion.IsCompleted || DateTime.UtcNow > deadline) throw new TimeoutException(what + "; " + Snapshot(rpc.Error));
            await Task.Delay(20);
        }
    }

    // #11996 in a production session: tool_search loaded a deferred server's tool; the session is resumed while that server still
    // connects, so the tool is pending. A tool the branch never recorded is activated as newly registered (here the tool of a
    // direct server added since; upstream an extension's loader tool) and then deactivated on its own, as pi-web-access and
    // pi-subagents deactivate their loader at session_start. The pending tool must survive that and be declared once its server
    // connected; before 1.6.0 any deactivation dropped it.
    private static Task ResumeKeepsLoadedTool160() => WithRoot("sync160-resume", """{"mcpServers":{"docs":{"command":"docs-server","exposure":"deferred"}}}""", async fixture =>
    {
        fixture.Behaviors["docs"] = (["search"], null, false, null);
        var first = new Endpoint(() => Call("toolu_search", "tool_search", new { query = "search", limit = 1 }), () => Text("loaded"));
        await using (var rpc = new Rpc(Args(fixture.Root, "new-lazy"), first, fixture.Host()))
        {
            await Settled(fixture, rpc, 1);
            await rpc.Prompt("p1", "load docs");
            Equal(0, await rpc.Finish(), "first exit code; " + rpc.Error);
        }
        Check(ToolResults(first.Snapshot()[1]).Single().StartsWith("Loaded 1 tool. They are available from your next call:\n- mcp__docs__search:", StringComparison.Ordinal),
            "tool_search loaded the docs tool: " + ToolResults(first.Snapshot()[1]).Single());
        Check(RecordedLoadouts(fixture.Root)[^1].Contains("mcp__docs__search"), "the branch records the loaded tool");

        // Resumed with one more server, whose direct tool the branch never recorded; the docs server connects only when released.
        await File.WriteAllTextAsync(Path.Combine(fixture.Agent, "mcp.json"),
            """{"mcpServers":{"docs":{"command":"docs-server","exposure":"deferred"},"web":{"command":"web-server","exposure":"direct"}}}""");
        var resumed = new Fixture(fixture.Root);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resumed.Behaviors["docs"] = (["search"], release.Task, false, null);
        resumed.Behaviors["web"] = (["lookup"], null, false, null);
        var second = new Endpoint(() => Text("again"));
        var lines = File.ReadAllLines(Path.Combine(fixture.Root, "session.jsonl")).Length;
        await using (var rpc = new Rpc(Args(fixture.Root, "open"), second, resumed.Host()))
            try
            {
                var manager = await resumed.Manager.Task.WaitAsync(TimeSpan.FromSeconds(30));
                PersistentAgentSession? attached = null;
                await Until160(() => (attached = manager.Session()?.Attachment.Session) is { } current && current.GetActiveTools().Contains("mcp__web__lookup"), rpc,
                    "the new server's direct tool was not activated");
                var session = attached!;
                Check(session.PendingToolNames.Contains("mcp__docs__search"), "the restored tool is pending while its server connects: " + string.Join(",", session.PendingToolNames));
                Check(!session.GetActiveTools().Contains("mcp__docs__search"), "the docs tool is not registered yet");
                // Deactivate only the tool outside the restored loadout.
                for (var attempt = 0; ; attempt++)
                {
                    try { await session.SetActiveToolsAsync([.. session.GetActiveTools().Where(name => name != "mcp__web__lookup")]); break; }
                    catch (InvalidOperationException) when (attempt < 100) { await Task.Delay(20); }
                }
                Check(session.PendingToolNames.Contains("mcp__docs__search"), "deactivating a tool outside the restored loadout dropped the pending tool");
                release.TrySetResult();
                await Until160(() => session.GetActiveTools().Contains("mcp__docs__search"), rpc, "the pending tool was not activated when its server connected");
                Check(!session.GetActiveTools().Contains("mcp__web__lookup"), "the deactivated tool came back");
                await rpc.Prompt("p2", "use the docs");
                Equal(0, await rpc.Finish(), "resumed exit code; " + rpc.Error);
            }
            finally { release.TrySetResult(); }
        var declared = ToolNames(second.Snapshot().Single());
        Check(declared.Contains("tool_search") && declared.Contains("mcp__docs__search") && !declared.Contains("mcp__web__lookup"),
            "the resumed request declares the loaded tool and not the deactivated one: " + string.Join(",", declared));
        // The resumed session never records the loaded tool's removal.
        var removed = File.ReadAllLines(Path.Combine(fixture.Root, "session.jsonl")).Skip(lines).Select(line => JsonDocument.Parse(line).RootElement)
            .Where(entry => entry.TryGetProperty("message", out var message) && message.TryGetProperty("toolsRemoved", out _))
            .SelectMany(entry => entry.GetProperty("message").GetProperty("toolsRemoved").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)).ToArray();
        Check(!removed.Contains("mcp__docs__search"), "the resumed session removed the loaded tool: " + string.Join(",", removed));
    });
}
