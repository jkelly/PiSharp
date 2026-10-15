using System.Text.Json;

// Upstream: extensions/codemode/index.ts (codemode is registered, inactive, with every session), extensions/mcp/index.ts
// (scriptNeedsServer), extensions/codemode/execute.ts (nested calls through ctx.executeTool, with no limit of their own) and
// test/suite/agent-session-codemode.test.ts ("resolves bash calls to structured results", "resolves read calls to text ... and
// to image blocks that image() shows").
internal static partial class Program
{
    private const string TinyPngFile = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static async Task<(Seen[] Requests, string Error)> OneSession(string root, Fixture fixture, string[] flags, params Func<HttpResponseMessage>[] script)
    {
        var provider = new Endpoint(script);
        await using var rpc = new Rpc(SessionArgs(root, "new-memory", flags), provider, fixture.Host());
        await rpc.Prompt("p1", "go");
        Equal(0, await rpc.Finish(), string.Join(" ", flags) + " exit code; " + rpc.Error);
        return (provider.Snapshot(), rpc.Error.ToString());
    }

    private static IEnumerable<(string, Func<Task>)> SessionParityCases() =>
    [
        // codemode is registered with every session, inactive: --tools, --no-mcp and defaultTools select it without any MCP server.
        Case("session.codemode-registered-in-every-session", () => WithSessionRoot("registered", null, async (root, fixture) =>
        {
            var (requests, error) = await OneSession(root, fixture, []);
            Check(!ToolNamesOf(requests[0]).Contains("codemode"), "inactive by default: " + string.Join(",", ToolNamesOf(requests[0])));
            Names([], Diagnostics(error), "nothing reported");
            (requests, _) = await OneSession(root, fixture, ["--tools", "read,codemode"], () => CallReply("toolu_cm", "codemode", new { code = "return 1 + 1" }), () => TextReply("done"));
            Names(["read", "codemode"], ToolNamesOf(requests[0]), "--tools selects codemode");
            Check(ToolResultTexts(requests[1]).Single().EndsWith("Output:\n\n2", StringComparison.Ordinal) || ToolResultTexts(requests[1]).Single().EndsWith("\n2", StringComparison.Ordinal),
                "the script ran: " + ToolResultTexts(requests[1]).Single());
            (requests, _) = await OneSession(root, fixture, ["--no-mcp", "--tools", "codemode"]);
            Names(["codemode"], ToolNamesOf(requests[0]), "--no-mcp keeps codemode");
            var settings = Path.Combine(root, "settings.json");
            await File.WriteAllTextAsync(settings, """{"defaultTools":["read","codemode"]}""");
            (requests, _) = await OneSession(root, fixture, ["--user-settings", settings]);
            Check(ToolNamesOf(requests[0]).Contains("codemode") && ToolNamesOf(requests[0]).Contains("read"), "defaultTools selects codemode: " + string.Join(",", ToolNamesOf(requests[0])));
            Check(fixture.Servers.IsEmpty, "no MCP server involved");
        })),
        // execute.ts: nested calls go through ctx.executeTool with no limit of their own (the shared invoker used to stop a tree at
        // 128 calls), sequentially and in parallel.
        Case("session.nested-calls-are-not-capped", () => WithSessionRoot("uncapped", CodemodeDocs, async (root, fixture) =>
        {
            const string code = "let sequential = 0;\nfor (let i = 0; i < 150; i++) { await tools.mcp__docs__search({ query: \"s\" + i }); sequential++; }\nconst parallel = await Promise.all(Array.from({ length: 150 }, (_, i) => tools.mcp__docs__search({ query: \"p\" + i })));\nreturn [sequential, parallel.length];";
            var provider = new Endpoint(() => CallReply("toolu_many", "codemode", new { code }), () => TextReply("done"));
            await using var rpc = new Rpc(SessionArgs(root, "new-memory"), provider, fixture.Host());
            await WaitConnected(fixture, rpc);
            await rpc.Prompt("p1", "search a lot");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            var result = ToolResultTexts(provider.Snapshot()[1]).Single();
            Check(result.StartsWith("Script completed", StringComparison.Ordinal) && result.EndsWith("[150,150]", StringComparison.Ordinal), "result: " + result[..Math.Min(result.Length, 600)]);
            Equal(300, fixture.Servers.Single().Calls.Count, "every call reached the server");
            Check(fixture.Servers.Single().Calls.All(call => call.Identity?.ParentToolCallId == "toolu_many"), "every call has the codemode parent");
        })),
        // scriptNeedsServer: a script waits for the codemode servers it needs inside its call (index.ts tool_call); the server's tools
        // are published during the run, so the script calls them once the server connected (the case id predates this).
        Case("session.prompt-waits-for-connecting-codemode-servers", () => WithSessionRoot("waits", CodemodeDocs, async (root, fixture) =>
        {
            fixture.InitializeDelay = TimeSpan.FromSeconds(2);
            var provider = new Endpoint(() => CallReply("toolu_cm", "codemode", new { code = "const r = await tools.mcp__docs__search({ query: \"install\" });\ntext(r.content[0].text);" }), () => TextReply("done"));
            await using var rpc = new Rpc(SessionArgs(root, "new-memory"), provider, fixture.Host());
            await rpc.Prompt("p1", "right away");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            var result = ToolResultTexts(provider.Snapshot()[1]).Single();
            Check(result.StartsWith("Script completed", StringComparison.Ordinal) && result.EndsWith("Found: install guide.", StringComparison.Ordinal), "the first prompt's script reached the server: " + result);
        })),
        // index.ts tool_call (Pi 1.2.0 reachesTools): before codemode runs, the session waits for the servers its call may reach and no
        // others: a script that names mcp__docs waits for docs but not for a slower server; a script that uses a discovery global
        // (searchTools) reaches every tool, so it waits for every server. The prompt itself waits for neither (codemode exposure).
        Case("session.codemode-waits-only-for-the-servers-its-call-reaches", () => WithSessionRoot("reach", """{"mcpServers":{"docs":{"command":"docs-server"},"slow":{"command":"slow-server"}}}""", async (root, fixture) =>
        {
            fixture.Delays["docs"] = TimeSpan.FromSeconds(1); fixture.Delays["slow"] = TimeSpan.FromSeconds(6);
            var provider = new Endpoint(
                () => CallReply("toolu_docs", "codemode", new { code = "const r = await tools.mcp__docs__search({ query: \"install\" });\ntext(r.content[0].text);" }),
                () => CallReply("toolu_all", "codemode", new { code = "text(typeof searchTools);" }),
                () => TextReply("done"));
            await using var rpc = new Rpc(SessionArgs(root, "new-memory"), provider, fixture.Host());
            await rpc.Prompt("p1", "right away");
            Equal(0, await rpc.Finish(), "exit code; " + rpc.Error);
            var requests = provider.Snapshot();
            Equal(3, requests.Length, "requests");
            var docs = fixture.Servers.Single(server => server.Name == "docs"); var slow = fixture.Servers.Single(server => server.Name == "slow");
            var first = ToolResultTexts(requests[1]).Single();
            Check(first.StartsWith("Script completed", StringComparison.Ordinal) && first.EndsWith("Found: install guide.", StringComparison.Ordinal), "the script reached docs: " + first);
            Check(docs.ListedAt != 0 && requests[0].At < docs.ListedAt, "the prompt did not wait for the codemode server");
            Check(slow.ListedAt == 0 || slow.ListedAt > requests[1].At, "the docs script did not wait for slow");
            Check(slow.ListedAt != 0 && slow.ListedAt < requests[2].At, "the searchTools script waited for slow");
            Check(ToolResultTexts(requests[2]).Single().EndsWith("function", StringComparison.Ordinal), "second script: " + ToolResultTexts(requests[2]).Single());
        })),
        // agent-session-codemode.test.ts: bash resolves to its structured result, also for a non-zero exit code; read resolves to the
        // text of a text file and to an image block that image() shows.
        Case("session.read-and-bash-resolve-to-structured-values", () => WithSessionRoot("structured", null, async (root, fixture) =>
        {
            const string bash = @"C:\Program Files\Git\usr\bin\bash.exe";
            var notes = Path.Combine(root, "notes.txt"); await File.WriteAllTextAsync(notes, "hello");
            var pixel = Path.Combine(root, "pixel.png"); await File.WriteAllBytesAsync(pixel, Convert.FromBase64String(TinyPngFile));
            var flags = new List<string> { "--tools", "read,bash,codemode", "--allow-read", notes, "--allow-read", pixel };
            var bashCode = "";
            if (OperatingSystem.IsWindows() && File.Exists(bash))
            {
                flags.AddRange(["--bash-executable", bash, "--bash-spill-root", root, "--allow-bash-command", "echo out; exit 3"]);
                bashCode = "const r = await tools.bash({ command: \"echo out; exit 3\" });\ntext(JSON.stringify([r.output, r.exit_code, typeof r.wall_time_seconds]));\n";
            }
            var code = "text(await tools.read({ path: \"notes.txt\" }));\nconst shot = await tools.read({ path: \"pixel.png\" });\ntext(shot.note);\nimage(shot);\n" + bashCode;
            var (requests, error) = await OneSession(root, fixture, [.. flags], () => CallReply("toolu_cm", "codemode", new { code }), () => TextReply("done"));
            var body = JsonDocument.Parse(requests[1].Body!).RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").EnumerateArray()
                .Single(block => block.GetProperty("type").GetString() == "tool_result").GetProperty("content");
            var texts = string.Join("\n", body.EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));
            Check(texts.Contains("hello\n==> text 2/", StringComparison.Ordinal) && texts.Contains("[Image saved to ", StringComparison.Ordinal), "read results: " + texts);
            Check(body.EnumerateArray().Any(part => part.GetProperty("type").GetString() == "image"), "image() shows the read image: " + body);
            if (bashCode.Length > 0) Check(texts.Contains("[\"out\\n\",3,\"number\"]", StringComparison.Ordinal), "bash structured result: " + texts);
        })),
    ];
}
