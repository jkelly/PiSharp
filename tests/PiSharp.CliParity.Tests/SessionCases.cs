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
        ("sessions.missing-session-cwd-stops-non-interactive-runs", async () =>
        {
            using var sandbox = new Sandbox("missing-cwd");
            var gone = Path.Combine(sandbox.Root, "gone");
            var file = sandbox.Write("old.jsonl", new JsonObject { ["type"] = "session", ["version"] = 3, ["id"] = "old-session", ["timestamp"] = "2026-01-01T00:00:00.000Z", ["cwd"] = gone }.ToJsonString() + "\n");
            var (code, _, stderr) = await sandbox.Run("-p", "--session", file, "--provider", "anthropic", "--model", "claude-sonnet-4-5", "x");
            Equal(1, code, "exit");
            Equal($"Stored session working directory does not exist: {gone}\nSession file: {file}\nCurrent working directory: {sandbox.Cwd}\n", stderr, "message");
        }),
    ];
}
