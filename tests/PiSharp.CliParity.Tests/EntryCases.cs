using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Pi;

// The Pi entry end to end through PiCommand.RunAsync (the real Program entry minus the console): print text and JSON output,
// the session layout it writes, @file and piped stdin input, the tool gates and RPC mode, with a fake Anthropic endpoint.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> EntryCases() =>
    [
        ("entry.print-text-final-assistant-text-and-session-layout", PrintText),
    ];

    // print-mode.ts text: the last assistant message's text blocks, each followed by a newline; nothing else on stdout. main.ts:
    // a new session file under <agentDir>/sessions/--<cwd>--/<timestamp>_<uuidv7>.jsonl whose header carries that id.
    private static async Task PrintText()
    {
        using var sandbox = new Sandbox("print-text");
        sandbox.Respond = (_, _) => AnthropicText("Hello from the model");
        var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "Say hi");
        Equal(0, code, "exit code; stderr: " + stderr);
        Equal("Hello from the model\n", stdout, "stdout");
        Equal("", stderr, "stderr");
        var files = sandbox.SessionFiles();
        Equal(1, files.Length, "session files");
        var expectedDir = PiSessions.DefaultSessionDirectoryPath(sandbox.Cwd, sandbox.AgentDir);
        Equal(expectedDir, Path.GetDirectoryName(files[0]), "session directory");
        var header = JsonNode.Parse(File.ReadLines(files[0]).First())!.AsObject();
        var name = Path.GetFileNameWithoutExtension(files[0]);
        Check(name.EndsWith("_" + header["id"]!.GetValue<string>(), StringComparison.Ordinal), "file name ends with the header id: " + name);
        Equal(header["timestamp"]!.GetValue<string>().Replace(':', '-').Replace('.', '-'), name[..name.IndexOf('_')], "file timestamp");
        Equal(sandbox.Cwd, header["cwd"]!.GetValue<string>(), "header cwd");
        Equal(1, sandbox.Requests.Count, "provider requests");
        var body = sandbox.Requests[0].Json;
        Equal(64000, body.GetProperty("max_tokens").GetInt32(), "model maxTokens");
    }
}
