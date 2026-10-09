// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session-runtime.ts (switchSession:
// SessionManager.open(sessionPath)) and packages/coding-agent/src/core/session-manager.ts (open, _setSessionFile, newSession).
using System.Text.Json.Nodes;

// switch_session (and /resume, /import) open any session file the way SessionManager.open does, wherever it is: the session directory
// becomes the file's directory, a missing file is a new session created lazily at that path, an empty file gets its header, and a
// non-empty file that is not a session is refused with upstream's text. Expectations were checked against Pi 1.1.0 run with node.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SwitchPathCases() =>
    [
        ("switch.session-files-outside-the-session-directory", async () =>
        {
            using var sandbox = new Sandbox("switch-outside");
            var elsewhere = Path.Combine(sandbox.Root, "elsewhere");
            Directory.CreateDirectory(elsewhere);
            var outside = SeedSession(sandbox, "outside.jsonl", "anthropic", "claude-sonnet-4-5", elsewhere);
            var missing = Path.Combine(elsewhere, "missing.jsonl");
            var empty = sandbox.Write(Path.Combine(elsewhere, "empty.jsonl"), "");
            var junk = sandbox.Write(Path.Combine(elsewhere, "junk.jsonl"), "this is not a session\n");
            static string Json(string text) => JsonValue.Create(text)!.ToJsonString();
            var responses = await RpcSequence(sandbox, ["--mode", "rpc", "--provider", "anthropic", "--model", "claude-sonnet-4-5"],
                """{"id":"1","type":"switch_session","sessionPath":""" + Json(outside) + "}",
                """{"id":"2","type":"get_state"}""",
                """{"id":"3","type":"get_messages"}""",
                """{"id":"4","type":"new_session"}""",
                """{"id":"5","type":"get_state"}""",
                """{"id":"6","type":"switch_session","sessionPath":""" + Json(missing) + "}",
                """{"id":"7","type":"get_state"}""",
                """{"id":"8","type":"switch_session","sessionPath":""" + Json(empty) + "}",
                """{"id":"9","type":"get_messages"}""",
                """{"id":"10","type":"switch_session","sessionPath":""" + Json(junk) + "}",
                """{"id":"11","type":"get_state"}""");
            JsonNode Data(int index) => responses[index]["data"]!;
            string Response(int index) => responses[index].ToJsonString();
            // An existing session file anywhere opens with its history.
            Check(responses[0]["success"]!.GetValue<bool>() && Data(0)["cancelled"]!.GetValue<bool>() == false, "outside: " + Response(0));
            Equal(outside, Data(1)["sessionFile"]!.GetValue<string>(), "outside session file");
            Equal(2, (Data(2)["messages"] as JsonArray)!.Count, "outside history");
            // SessionManager.open derives the session directory from the file: a new session goes next to it.
            Equal(elsewhere, Path.GetDirectoryName(Data(4)["sessionFile"]!.GetValue<string>()), "new session directory");
            // A missing file is a new session at that path, written only once it has a response.
            Check(responses[5]["success"]!.GetValue<bool>(), "missing: " + Response(5));
            Equal(missing, Data(6)["sessionFile"]!.GetValue<string>(), "missing session file");
            Equal(0, Data(6)["messageCount"]?.GetValue<int>() ?? 0, "missing session is empty");
            Check(!File.Exists(missing), "a missing session file is created lazily");
            // An empty file gets a session header.
            Check(responses[7]["success"]!.GetValue<bool>() && (Data(8)["messages"] as JsonArray)!.Count == 0, "empty: " + Response(7));
            Check(JsonNode.Parse(File.ReadLines(empty).First())!["type"]!.GetValue<string>() == "session", "empty file gets a header");
            // sdk.ts: a session without messages records its model and thinking level at once (Pi 1.1.0 writes all three lines).
            Names(["session", "model_change", "thinking_level_change"], File.ReadLines(empty).Select(line => JsonNode.Parse(line)!["type"]!.GetValue<string>()), "empty file entries");
            Equal("claude-sonnet-4-5", JsonNode.Parse(File.ReadLines(empty).ElementAt(1))!["modelId"]!.GetValue<string>(), "recorded model");
            Equal("medium", JsonNode.Parse(File.ReadLines(empty).ElementAt(2))!["thinkingLevel"]!.GetValue<string>(), "recorded thinking level");
            // A non-empty file that is not a session is refused, unchanged, and the current session stays.
            Check(!responses[9]["success"]!.GetValue<bool>() && responses[9]["error"]!.GetValue<string>() == "Session file is not a valid pi session: " + junk,
                "junk: " + Response(9));
            Equal("this is not a session\n", File.ReadAllText(junk), "junk file unchanged");
            Equal(empty, Data(10)["sessionFile"]!.GetValue<string>(), "the current session stays");
        }),
        ("switch.empty-session-file-at-startup-records-model-and-thinking", async () =>
        {
            using var sandbox = new Sandbox("startup-empty");
            var empty = sandbox.Write(Path.Combine(sandbox.Root, "elsewhere", "empty.jsonl"), "");
            // Pi 1.1.0 (--session empty --model claude-sonnet-4-5 --thinking high): header, model_change and thinking_level_change at once.
            var responses = await RpcSequence(sandbox, ["--mode", "rpc", "--session", empty, "--provider", "anthropic", "--model", "claude-sonnet-4-5", "--thinking", "high"],
                """{"id":"1","type":"get_state"}""");
            Equal("high", responses[0]["data"]!["thinkingLevel"]!.GetValue<string>(), "thinking level");
            var entries = File.ReadLines(empty).Select(line => JsonNode.Parse(line)!).ToArray();
            Names(["session", "model_change", "thinking_level_change"], entries.Select(entry => entry["type"]!.GetValue<string>()), "startup entries");
            Equal("claude-sonnet-4-5", entries[1]["modelId"]!.GetValue<string>(), "recorded model");
            Equal("high", entries[2]["thinkingLevel"]!.GetValue<string>(), "recorded thinking level");
        }),
    ];
}
