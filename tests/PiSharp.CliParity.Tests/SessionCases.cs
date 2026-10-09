using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.Cli.Pi;

// core/session-manager.ts (layout, ids, findById, continueRecent, forkFrom, list) and main.ts createSessionManager through the CLI:
// --continue, --session, --session-id, --fork, --no-session, --name and --session-dir.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SessionCases() =>
    [
        ("sessions.directory-encoding-file-names-and-ids", Sync(() =>
        {
            var agent = OperatingSystem.IsWindows() ? @"C:\agent" : "/agent";
            var cwd = OperatingSystem.IsWindows() ? @"C:\work\my project" : "/work/my project";
            var expected = OperatingSystem.IsWindows() ? @"C:\agent\sessions\--C--work-my project--" : "/agent/sessions/--work-my project--";
            Equal(expected, PiSessions.DefaultSessionDirectoryPath(cwd, agent), "encoded cwd");
            var time = new DateTimeOffset(2026, 10, 8, 12, 34, 56, 789, TimeSpan.Zero);
            var (path, id, timestamp) = PiSessions.NewSessionFile(agent, null, time);
            Equal("2026-10-08T12:34:56.789Z", timestamp, "ISO timestamp");
            Check(Regex.IsMatch(id, "^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"), "uuidv7: " + id);
            Equal(time.ToUnixTimeMilliseconds(), Convert.ToInt64(id.Replace("-", "")[..12], 16), "uuidv7 time prefix");
            Equal(Path.Join(agent, "2026-10-08T12-34-56-789Z_" + id + ".jsonl"), path, "file name");
            PiSessions.AssertValidSessionId("orchestrated.session-1_a");
            foreach (var bad in new[] { "", "-a", "a-", "a b", "a/b" })
                Equal("Session id must be non-empty, contain only alphanumeric characters, '-', '_', and '.', and start and end with an alphanumeric character",
                    Throws<PiSessionException>(() => PiSessions.AssertValidSessionId(bad), "invalid id " + bad).Message, "invalid id text");
        })),
        ("sessions.continue-reuses-the-latest-session-and-no-session-writes-nothing", async () =>
        {
            using var sandbox = new Sandbox("continue");
            var model = new[] { "--provider", "anthropic", "--model", "claude-sonnet-4-5" };
            Equal(0, (await sandbox.Run(["-p", .. model, "first"])).Code, "first run");
            var first = sandbox.SessionFiles().Single();
            Equal(0, (await sandbox.Run(["-p", "-c", .. model, "second"])).Code, "continue run");
            Equal(first, sandbox.SessionFiles().Single(), "same session file");
            var messages = File.ReadLines(first).Select(line => JsonNode.Parse(line)!.AsObject())
                .Where(entry => entry["type"]?.GetValue<string>() == "message" && entry["message"]?["role"]?.GetValue<string>() is "user" or "assistant").ToArray();
            Equal(4, messages.Length, "both exchanges in one file");
            var secondRequest = sandbox.Requests[1].Json.GetProperty("messages");
            Equal(3, secondRequest.GetArrayLength(), "the second request carries the history");
            Equal(0, (await sandbox.Run(["-p", "--no-session", .. model, "third"])).Code, "--no-session run");
            Equal(1, sandbox.SessionFiles().Length, "--no-session writes no file");
            Equal(4, File.ReadLines(first).Count(line => line.Contains("\"role\":\"user\"", StringComparison.Ordinal) || line.Contains("\"role\":\"assistant\"", StringComparison.Ordinal)), "the earlier session is untouched");
        }),
        ("sessions.session-id-open-or-create-name-and-session-dir", async () =>
        {
            using var sandbox = new Sandbox("session-id");
            var model = new[] { "--provider", "anthropic", "--model", "claude-sonnet-4-5" };
            var (code, _, stderr) = await sandbox.Run(["-p", "--session-id", "build-42", "--name", "  Nightly build  ", .. model, "hi"]);
            Equal(0, code, "exit");
            Equal("Warning: No project session found with id 'build-42'; creating a new session with that id.\n", stderr, "creation warning");
            var file = sandbox.SessionFiles().Single();
            Check(file.EndsWith("_build-42.jsonl", StringComparison.Ordinal), "file named by the id");
            Equal("build-42", JsonNode.Parse(File.ReadLines(file).First())!["id"]!.GetValue<string>(), "header id");
            Check(File.ReadLines(file).Any(line => line.Contains("\"type\":\"session_info\"", StringComparison.Ordinal) && line.Contains("\"name\":\"Nightly build\"", StringComparison.Ordinal)), "session_info name");
            (code, _, stderr) = await sandbox.Run(["-p", "--session-id", "build-42", .. model, "again"]);
            Check(code == 0 && stderr == "", "reopened silently: " + stderr);
            Equal(1, sandbox.SessionFiles().Length, "same file");
            var custom = Path.Combine(sandbox.Root, "elsewhere");
            (code, _, _) = await sandbox.Run(["-p", "--session-dir", custom, .. model, "hi"]);
            Equal(0, code, "--session-dir run");
            Equal(1, Directory.GetFiles(custom, "*.jsonl").Length, "--session-dir holds the new session");
            sandbox.Vars["PI_CODING_AGENT_SESSION_DIR"] = Path.Combine(sandbox.Root, "from-env");
            (code, _, _) = await sandbox.Run(["-p", .. model, "hi"]);
            Equal(0, code, "env session dir run");
            Equal(1, Directory.GetFiles(Path.Combine(sandbox.Root, "from-env"), "*.jsonl").Length, "PI_CODING_AGENT_SESSION_DIR");
        }),
        ("sessions.session-by-path-prefix-fork-and-errors", async () =>
        {
            using var sandbox = new Sandbox("session-select");
            var model = new[] { "--provider", "anthropic", "--model", "claude-sonnet-4-5" };
            Equal(0, (await sandbox.Run(["-p", .. model, "origin"])).Code, "origin run");
            var origin = sandbox.SessionFiles().Single();
            var id = JsonNode.Parse(File.ReadLines(origin).First())!["id"]!.GetValue<string>();
            Equal(0, (await sandbox.Run(["-p", "--session", id[..8], .. model, "by prefix"])).Code, "--session <prefix>");
            Equal(1, sandbox.SessionFiles().Length, "prefix opens the same session");
            Equal(0, (await sandbox.Run(["-p", "--session", origin, .. model, "by path"])).Code, "--session <path>");
            var (code, _, stderr) = await sandbox.Run(["-p", "--session", "nomatch", .. model, "x"]);
            Check(code == 1 && stderr == "No session found matching 'nomatch'\n", "not found: " + stderr);
            (code, _, stderr) = await sandbox.Run(["-p", "--fork", id, "--session-id", "forked-1", .. model, "forked"]);
            Equal(0, code, "fork; " + stderr);
            var forked = sandbox.SessionFiles().Single(file => file.EndsWith("_forked-1.jsonl", StringComparison.Ordinal));
            var header = JsonNode.Parse(File.ReadLines(forked).First())!.AsObject();
            Names(["type", "version", "id", "timestamp", "cwd", "parentSession"], header.Select(pair => pair.Key), "fork header fields");
            Equal(origin, header["parentSession"]!.GetValue<string>(), "parentSession");
            Check(File.ReadLines(forked).Count(line => line.Contains("\"type\":\"message\"", StringComparison.Ordinal)) >= 6, "history copied then extended");
            (code, _, stderr) = await sandbox.Run(["-p", "--fork", id, "--session-id", "forked-1", .. model, "x"]);
            Check(code == 1 && stderr == "Session already exists with id 'forked-1'\n", "existing fork target: " + stderr);
            var junk = sandbox.Write("junk.jsonl", "not a session\n");
            (code, _, stderr) = await sandbox.Run(["-p", "--session", junk, .. model, "x"]);
            Check(code == 1 && stderr == $"Error: Session file is not a valid pi session: {junk}\n", "invalid file: " + stderr);
            var fresh = Path.Combine(sandbox.Root, "named", "new.jsonl");
            Equal(0, (await sandbox.Run(["-p", "--session", fresh, .. model, "x"])).Code, "a missing --session path becomes a new session there");
            Check(File.Exists(fresh) && JsonNode.Parse(File.ReadLines(fresh).First())!["type"]!.GetValue<string>() == "session", "new session at the explicit path");
        }),
        ("sessions.resume-without-a-picker-and-console-output-goes-to-stderr", async () =>
        {
            using var sandbox = new Sandbox("resume");
            var (code, stdout, stderr) = await sandbox.RunWith("", "-p", "-r", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "x");
            Check(code == 0 && stdout == "" && stderr == "No session selected\n", "print mode console text on stderr: " + stderr);
            Equal(0, sandbox.Requests.Count, "no prompt sent");
            using var interactiveOut = new StringWriter(); using var interactiveErr = new StringWriter();
            var selected = Path.Combine(sandbox.Root, "picked.jsonl");
            var host = sandbox.Host(interactiveOut, interactiveErr, null, interactive: true, runInteractive: (args, _, _) => Task.FromResult(args.Contains(selected) ? 0 : 3))
                with { SelectSession = (current, all, _) => Task.FromResult<string?>(selected) };
            Equal(0, await PiCommand.RunAsync(["-r", "--provider", "anthropic", "--model", "claude-sonnet-4-5"], host, CancellationToken.None), "the selector's session is used");
        }),
        ("sessions.missing-session-cwd-stops-non-interactive-runs", async () =>
        {
            using var sandbox = new Sandbox("missing-cwd");
            var gone = Path.Combine(sandbox.Root, "gone");
            var file = sandbox.Write("old.jsonl", new JsonObject { ["type"] = "session", ["version"] = 3, ["id"] = "old-session", ["timestamp"] = "2026-01-01T00:00:00.000Z", ["cwd"] = gone }.ToJsonString() + "\n");
            var (code, _, stderr) = await sandbox.Run("-p", "--session", file, "--provider", "anthropic", "--model", "claude-sonnet-4-5", "x");
            Equal(1, code, "exit");
            Equal($"Stored session working directory does not exist: {gone}\nSession file: {file}\nCurrent working directory: {sandbox.Cwd}\n", stderr, "message");
        }),
        ("sessions.resume-applies-the-tool-loadout-in-memory-and-the-next-prompt-records-it", ResumeRecordsLoadoutAtNextPrompt),
        // agent-loop.ts runs tool turns until the model stops and session-manager.ts loads every entry of the file: the Pi entry
        // has no turn, transcript-message, line or record cap (formerly 64 turns, 1024 messages and 10,000 lines/records).
        ("sessions.pi-entry-has-no-turn-transcript-or-record-caps", async () =>
        {
            Equal(int.MaxValue, PiSharp.Cli.Commands.RpcSessionCommand.LoopOptions(pi: true).MaximumTurns, "turns");
            Equal(int.MaxValue, PiSharp.Cli.Commands.RpcSessionCommand.LoopOptions(pi: true).MaximumTranscriptMessages, "transcript messages");
            Equal(int.MaxValue, PiSharp.Cli.Commands.RpcSessionCommand.ReaderOptions(pi: true).MaximumRecords, "records");
            Equal(int.MaxValue, PiSharp.Cli.Commands.RpcSessionCommand.ContextOptions(pi: true).MaximumEntries, "context entries");
            using var sandbox = new Sandbox("no-caps");
            var model = new[] { "--provider", "anthropic", "--model", "claude-sonnet-4-5" };
            const int toolTurns = 70;
            sandbox.Respond = (_, index) => index < toolTurns ? AnthropicToolCall("ls", new { path = "." }, "toolu_" + index) : AnthropicText("done");
            var (code, stdout, stderr) = await sandbox.Run(["-p", "--tools", "ls", .. model, "list"]);
            Equal(0, code, "exit; " + stderr);
            Equal(toolTurns + 1, sandbox.Requests.Count, "every tool turn ran");
            Equal("done\n", stdout, "final text");
            // A session of more than 10,000 records resumes.
            var lines = new List<string> { new JsonObject { ["type"] = "session", ["version"] = 3, ["id"] = "long-session",
                ["timestamp"] = "2026-01-01T00:00:00.000Z", ["cwd"] = sandbox.Cwd }.ToJsonString() };
            lines.Add(new JsonObject { ["type"] = "message", ["id"] = "m0", ["parentId"] = null, ["timestamp"] = "2026-01-01T00:00:00.000Z",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = "first question", ["timestamp"] = 0 } }.ToJsonString());
            for (var index = 1; index <= 10_050; index++)
                lines.Add(new JsonObject { ["type"] = "custom", ["customType"] = "note", ["data"] = new JsonObject { ["n"] = index },
                    ["id"] = "c" + index, ["parentId"] = index == 1 ? "m0" : "c" + (index - 1), ["timestamp"] = "2026-01-01T00:00:00.000Z" }.ToJsonString());
            var file = sandbox.Write("long.jsonl", string.Join("\n", lines) + "\n");
            sandbox.Requests.Clear(); sandbox.Respond = (_, _) => AnthropicText("resumed");
            (code, stdout, stderr) = await sandbox.Run(["-p", "--session", file, .. model, "again"]);
            Equal(0, code, "resume exit; " + stderr);
            var messages = sandbox.Requests.Single().Json.GetProperty("messages");
            Check(messages.GetArrayLength() == 2 && messages[0].GetRawText().Contains("first question", StringComparison.Ordinal), "history replayed: " + messages);
            Check(File.ReadLines(file).Count() > 10_052, "the resumed session was appended to");
        }),
    ];

    // sdk.ts createAgentSession passes the selected tool names (initialActiveToolNames) and the AgentSession constructor applies them
    // in memory with the current tool definitions (_buildRuntime); agent-loop.ts declareToolChanges records the loadout at the next
    // prompt, in a system message before the user message. Opening the session over RPC, reading its state and quitting leaves the
    // file untouched, although its recorded loadout is stale.
    private static async Task ResumeRecordsLoadoutAtNextPrompt()
    {
        using var sandbox = new Sandbox("restored-loadout");
        var model = new[] { "--provider", "anthropic", "--model", "claude-sonnet-4-5" };
        Equal(0, (await sandbox.Run(["-p", "--tools", "read,ls", .. model, "first"])).Code, "first run");
        var file = sandbox.SessionFiles().Single();
        // The recorded loadout is stale: ls was declared differently and an MCP server's tool is no longer registered.
        var lines = File.ReadAllLines(file);
        var index = Array.FindLastIndex(lines, line => JsonNode.Parse(line)!["message"]?["toolsAdded"] is not null);
        Check(index > 0, "the first run recorded its loadout");
        var record = JsonNode.Parse(lines[index])!.AsObject();
        var declared = record["message"]!["toolsAdded"]!.AsArray();
        Names(["read", "ls"], declared.Select(tool => tool!["name"]!.GetValue<string>()), "first run loadout");
        declared.Single(tool => tool!["name"]!.GetValue<string>() == "ls")!["description"] = "an older ls";
        declared.Add(new JsonObject { ["name"] = "mcp__gone__search", ["description"] = "gone", ["parameters"] = new JsonObject { ["type"] = "object" } });
        lines[index] = record.ToJsonString();
        File.WriteAllText(file, string.Join("\n", lines) + "\n");
        var before = File.ReadAllBytes(file);

        async Task<JsonNode[]> Rpc(string commands, bool settles, bool selected = true)
        {
            var gate = new GatedInput(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(commands)));
            if (!settles) gate.Release();
            using var output = new SignalingStream("\"type\":\"agent_settled\"", gate.Release);
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null, rpcInput: gate, rpcOutput: output) with { StdoutIsTty = false };
            Equal(0, await PiCommand.RunAsync(["--mode", "rpc", "--session", file, .. selected ? new[] { "--tools", "read,ls" } : [], .. model], host, CancellationToken.None), "rpc exit; " + stderr);
            return [.. System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!)];
        }
        // With the default tools and with --tools.
        foreach (var selected in new[] { false, true })
        {
            var state = (await Rpc("""{"id":"1","type":"get_state"}""" + "\n", settles: false, selected)).Single(line => line["id"]?.GetValue<string>() == "1");
            Check(state["success"]!.GetValue<bool>(), "get_state: " + state.ToJsonString());
            Check(File.ReadAllBytes(file).SequenceEqual(before), "opening the session, reading its state and quitting wrote to its file: " +
                string.Join("\n", File.ReadAllLines(file).Skip(lines.Length)));
        }

        await Rpc("""{"id":"2","type":"prompt","message":"second"}""" + "\n", settles: true);
        var added = File.ReadAllLines(file).Skip(lines.Length).Select(line => JsonNode.Parse(line)!).ToArray();
        Check(added.Length >= 3, "the prompt appended its entries");
        var loadout = added[0]["message"]!;
        Equal("system", loadout["role"]!.GetValue<string>(), "the loadout record comes first");
        Equal(JsonNode.Parse(lines[^1])!["id"]!.GetValue<string>(), added[0]["parentId"]!.GetValue<string>(), "the record continues the resumed leaf");
        Names(["read", "ls", "mcp__gone__search"], loadout["toolsRemoved"]!.AsArray().Select(tool => tool!["name"]!.GetValue<string>()), "recorded names removed");
        var current = loadout["toolsAdded"]!.AsArray();
        Names(["read", "ls"], current.Select(tool => tool!["name"]!.GetValue<string>()), "selected loadout recorded");
        Check(current.Single(tool => tool!["name"]!.GetValue<string>() == "ls")!["description"]!.GetValue<string>() != "an older ls", "the current ls declaration is recorded");
        Equal("user", added[1]["message"]!["role"]!.GetValue<string>(), "the user message follows the record");
        Equal(added[0]["id"]!.GetValue<string>(), added[1]["parentId"]!.GetValue<string>(), "the user message continues the record");
        Names(["read", "ls"], sandbox.Requests[^1].Json.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!), "the request declares the selected loadout");
    }
}
