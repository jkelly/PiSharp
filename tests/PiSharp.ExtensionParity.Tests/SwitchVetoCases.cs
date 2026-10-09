// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/agent-session-runtime.ts (switchSession:
// session_before_switch, then SessionManager.open) and packages/coding-agent/src/core/session-manager.ts (_setSessionFile).
using System.Text.Json.Nodes;

// A switch an extension cancels in session_before_switch leaves its target as it was: upstream opens the path only after the veto,
// so an empty file stays empty, a missing one is not created and a header-only session gains no entries.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SwitchVetoCases() =>
    [
        ("switch-veto.cancelled-switch-leaves-the-target-unchanged", SwitchVeto),
    ];

    private static async Task SwitchVeto()
    {
        using var sandbox = NodeSandbox("switch-veto");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "veto.ts"), """
            export default function (pi: any) {
              pi.on("session_before_switch", async () => ({ cancel: true }));
            }
            """);
        var elsewhere = Path.Combine(sandbox.Root, "elsewhere");
        var empty = sandbox.Write(Path.Combine(elsewhere, "empty.jsonl"), "");
        var missing = Path.Combine(elsewhere, "missing.jsonl");
        var cwd = JsonValue.Create(sandbox.Cwd)!.ToJsonString();
        var headerText = "{\"type\":\"session\",\"version\":3,\"id\":\"01a00000-0000-7000-8000-00000000000a\",\"timestamp\":\"2026-10-09T10:00:00.000Z\",\"cwd\":" + cwd + "}\n";
        var headerOnly = sandbox.Write(Path.Combine(elsewhere, "header-only.jsonl"), headerText);
        string[] commands =
        [
            """{"id":"1","type":"switch_session","sessionPath":""" + JsonValue.Create(empty)!.ToJsonString() + "}",
            """{"id":"2","type":"switch_session","sessionPath":""" + JsonValue.Create(missing)!.ToJsonString() + "}",
            """{"id":"3","type":"switch_session","sessionPath":""" + JsonValue.Create(headerOnly)!.ToJsonString() + "}",
            """{"id":"4","type":"get_state"}"""
        ];
        var sent = 1;
        var (code, records, stderr) = await RunRpc(sandbox, [.. Model, "-e", extension], [commands[0]], (record, _) => IsResponse(record, "4"),
            react: (record, push) => { if (record["type"]?.GetValue<string>() == "response" && sent < commands.Length) push(commands[sent++]); });
        Equal(0, code, "exit; " + stderr);
        foreach (var id in new[] { "1", "2", "3" })
        {
            var response = records.Single(record => IsResponse(record, id));
            Check(response["success"]!.GetValue<bool>() && response["data"]!["cancelled"]!.GetValue<bool>(), "vetoed " + id + ": " + response.ToJsonString());
        }
        Equal("", File.ReadAllText(empty), "the empty file stays empty");
        Check(!File.Exists(missing), "the missing file is not created");
        Equal(headerText, File.ReadAllText(headerOnly), "the header-only session gains no entries");
        Check(records.Single(record => IsResponse(record, "4"))["data"]!["sessionFile"]!.GetValue<string>() is { } current &&
            current != empty && current != missing && current != headerOnly, "the current session stays");
    }
}
