// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/suite/regressions/10470-rpc-session-replacement-start-once.test.ts,
// test/suite/print-mode-session-start-events.test.ts and test/agent-session-runtime-events.test.ts (session_start after reload) over
// packages/coding-agent/src/modes/rpc/rpc-mode.ts, src/modes/print-mode.ts (subscribe before bindExtensions; no second rebind after a
// replacing command) and src/core/agent-session.ts (reload restarts the extensions the host bound).
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160SessionStartCases() =>
    [
        ("sync160.rpc-session-replacement-runs-session-start-once-and-streams-its-entries", RpcReplacementStartsOnce),
        ("sync160.json-mode-streams-entries-appended-by-session-start-handlers", JsonModeSessionStartEvents),
        ("sync160.reload-emits-session-shutdown-then-session-start-once", ReloadRestartsExtensions),
    ];

    /// <summary>session_start appends a custom entry naming its reason and how many starts ran before it; every lifecycle event is
    /// also written to probe.log.</summary>
    private const string Sync160StartMarkerExtension = Probe + """
        export default function (pi: any) {
          let starts = 0;
          pi.on("session_start", (event: any) => { const marker = `${event.reason}:${starts++}`; log("start", marker); pi.appendEntry("start-marker", marker); });
          pi.on("session_shutdown", (event: any) => { log("shutdown", event.reason); });
          pi.on("resources_discover", (event: any) => { log("discover", event.reason); });
          pi.registerCommand("renew", { description: "start a new session", handler: async (_args: string, ctx: any) => { await ctx.newSession(); } });
          pi.registerCommand("again", { description: "reload", handler: async (_args: string, ctx: any) => { await ctx.reload(); } });
        }
        """;

    private static string[] Sync160StartMarkers(IEnumerable<JsonNode> records) =>
        [.. records.Where(record => record["type"]?.GetValue<string>() == "entry_appended" && record["entry"]?["type"]?.GetValue<string>() == "custom" &&
            record["entry"]?["customType"]?.GetValue<string>() == "start-marker").Select(record => record["entry"]!["data"]!.GetValue<string>())];

    private static string[][] Sync160Probe(Sandbox sandbox) => File.Exists(Path.Combine(sandbox.Cwd, "probe.log"))
        ? [.. File.ReadAllLines(Path.Combine(sandbox.Cwd, "probe.log")).Where(line => line.Length > 0).Select(line => JsonSerializer.Deserialize<string[]>(line)!)]
        : [];

    // #10470: RPC new_session, switch_session, fork and clone ran the extension session_start (and resources_discover) handlers twice
    // for the new session, and RPC clients did not receive events those handlers emitted, such as entry_appended.
    private static async Task RpcReplacementStartsOnce()
    {
        using var sandbox = NodeSandbox("sync160-start-once");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "start.ts"), Sync160StartMarkerExtension);
        string? sessionFile = null, userEntry = null;
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model, "-e", extension], ["""{"id":"p","type":"prompt","message":"hello"}"""],
            (record, _) => IsResponse(record, "c"), react: (record, push) =>
            {
                if (record["type"]?.GetValue<string>() == "agent_settled" && sessionFile is null && userEntry is null)
                { push("""{"id":"s","type":"get_state"}"""); push("""{"id":"e","type":"get_entries"}"""); return; }
                if (record["type"]?.GetValue<string>() != "response") return;
                Check(record["success"]!.GetValue<bool>(), "command failed: " + record.ToJsonString());
                switch (record["id"]?.GetValue<string>())
                {
                    case "s": sessionFile = record["data"]!["sessionFile"]!.GetValue<string>(); break;
                    case "e":
                        userEntry = record["data"]!["entries"]!.AsArray().First(entry => entry!["type"]!.GetValue<string>() == "message" &&
                            entry["message"]!["role"]!.GetValue<string>() == "user")!["id"]!.GetValue<string>();
                        push("""{"id":"n","type":"new_session"}""");
                        break;
                    case "n": push("""{"id":"w","type":"switch_session","sessionPath":""" + JsonValue.Create(sessionFile)!.ToJsonString() + "}"); break;
                    case "w": push("""{"id":"f","type":"fork","entryId":""" + JsonValue.Create(userEntry)!.ToJsonString() + "}"); break;
                    case "f": push("""{"id":"w2","type":"switch_session","sessionPath":""" + JsonValue.Create(sessionFile)!.ToJsonString() + "}"); break;
                    case "w2": push("""{"id":"c","type":"clone"}"""); break;
                }
            });
        Equal(0, code, "exit; " + stderr);
        // One session_start per session, with the reason of the command that replaced it, and the entry each handler appended is
        // streamed to the client once (the startup one included).
        string[] expected = ["startup:0", "new:1", "resume:2", "fork:3", "resume:4", "fork:5"];
        var probe = Sync160Probe(sandbox);
        Equal(string.Join(",", expected), string.Join(",", probe.Where(line => line[0] == "start").Select(line => line[1])), "session_start handlers");
        Equal(string.Join(",", expected), string.Join(",", Sync160StartMarkers(records)), "streamed entry_appended events");
        // Each streamed entry follows the replacement's own records and precedes the command's response.
        foreach (var (id, marker) in new[] { ("n", "new:1"), ("w", "resume:2"), ("f", "fork:3"), ("w2", "resume:4"), ("c", "fork:5") })
        {
            var response = records.FindIndex(record => IsResponse(record, id));
            var appended = records.FindIndex(record => record["type"]?.GetValue<string>() == "entry_appended" && record["entry"]?["data"]?.GetValue<string>() == marker);
            Check(appended >= 0 && appended < response, $"{marker} is streamed before the {id} response");
        }
        // resources_discover is asked once per run here (the extensions and their resources stay loaded across replacements), never
        // twice for one session.
        var discovers = probe.Count(line => line[0] == "discover");
        Check(discovers <= expected.Length, "resources_discover ran more than once per session: " + discovers);
        Equal("startup", probe.First(line => line[0] == "discover")[1], "the first resources_discover");
    }

    // --mode json dropped events emitted by extension session_start handlers, such as entry_appended for entries they append, at
    // startup and after an extension replaces the session.
    private static async Task JsonModeSessionStartEvents()
    {
        using var sandbox = NodeSandbox("sync160-json-start");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "start.ts"), Sync160StartMarkerExtension);
        var (code, stdout, stderr) = await sandbox.Run(["--mode", "json", .. Model, "-e", extension, "/renew"]);
        Equal(0, code, "exit; " + stderr);
        var output = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToList();
        Equal("session", output[0]["type"]?.GetValue<string>(), "the session header comes first");
        Equal("startup:0,new:1", string.Join(",", Sync160StartMarkers(output)), "streamed start markers");
        Equal(0, sandbox.Requests.Count, "the command was handled without a model request");
    }

    // AgentSession.reload() did not emit session_start to extensions when the SDK host bound them with bindExtensions({}), after it
    // had already emitted session_shutdown. Every native host binds its extensions the same way, so a reload always restarts them.
    private static async Task ReloadRestartsExtensions()
    {
        using var sandbox = NodeSandbox("sync160-reload-start");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "start.ts"), Sync160StartMarkerExtension);
        var (code, _, stderr) = await sandbox.Run(["-p", .. Model, "-e", extension, "/again"]);
        Equal(0, code, "exit; " + stderr);
        var lifecycle = Sync160Probe(sandbox).Where(line => line[0] is "start" or "shutdown").Select(line => line[0] + " " + line[1].Split(':')[0]).ToArray();
        // The reloaded extension instance counts its own starts, so only the reasons are compared.
        Equal("start startup,shutdown reload,start reload,shutdown quit", string.Join(",", lifecycle), "lifecycle events");
    }
}
