using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static class SessionCompactionCommandTests
{
    private static string host = "", cli = "";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string cliDll)
    {
        host = dotnetHost; cli = cliDll;
        return [
            ("compaction frontend CLI real durable retain-none reopen and next provider consume summary", DurableNext),
            ("compaction frontend CLI branch threshold admission and acknowledged output truth", BranchAndAdmission),
            ("compaction frontend three offline provider transports honor summary output budget and join", ProviderBudgets),
            ("compaction frontend cooked commands capture generation null boundary and auto configuration", Cooked)
        ];
    }
    private static async Task DurableNext()
    {
        using var files = new Files(); await files.Initialize(); await files.Script("openai-responses", "GENERATED", ["original input", "important-focus"]);
        var before = await File.ReadAllBytesAsync(files.Session);
        var result = await Child(files, files.Args("compact").Concat(new[] { "--retain-none", "--reserve", "64", "--focus", "important-focus" }).ToArray());
        Check(result.Exit == 0 && result.Error == "", "Compiled compaction failed: " + result.Error);
        var receipt = JsonData.Parse(result.Output).Value;
        Check(receipt.GetProperty("checkpointAcknowledged").GetBoolean() && receipt.GetProperty("providerRequests").GetInt32() == 1, "Summary checkpoint or actual request count differs.");
        var after = await File.ReadAllBytesAsync(files.Session); Check(after.AsSpan(0, before.Length).SequenceEqual(before), "Compaction rewrote raw transcript.");
        await using (var reopened = await SessionLogStore.OpenAsync(files.Session))
        {
            var entry = reopened.Snapshot.Entries[^1].WireBody.Value;
            Check(entry.GetProperty("type").GetString() == "compaction" && entry.GetProperty("firstKeptEntryId").GetString() == entry.GetProperty("id").GetString() &&
                !entry.GetProperty("fromHook").GetBoolean() && entry.GetProperty("usage").GetProperty("totalTokens").GetInt64() == 7, "Generated retain-none record framing differs.");
            var context = new SessionContextProjector().ProjectLatest(reopened.Snapshot.Entries);
            Check(context.LlmMessages.Length == 1 && context.LlmMessages[0].WireBody.ToString().Contains("GENERATED", StringComparison.Ordinal), "Reopened compacted context retains old messages.");
        }
        await files.Script("openai-responses", "NEXT", ["GENERATED", "next turn"]);
        var next = await Child(files, "session", "resume", "--session", files.Session, "--workspace", files.Root, "--offline-script", files.ScriptPath, "--message", "next turn");
        Check(next.Exit == 0 && JsonData.Parse(next.Output).Value.GetProperty("requests")[0].GetProperty("requiredHistoryChecks").GetInt32() == 2, "Compiled next turn did not use reopened summary: " + next.Error);
        await using var writer = await SessionLogStore.OpenAsync(files.Session);
        Check(writer.Snapshot.Entries.Count(entry => entry.WireBody.Value.GetProperty("type").GetString() == "compaction") == 1, "Summary checkpoint was duplicated.");
    }
    private static async Task BranchAndAdmission()
    {
        using var files = new Files(); await files.Initialize(branch: true); await files.Script("openai-responses", "BRANCH", ["right input"]);
        var before = await File.ReadAllBytesAsync(files.Session);
        var skipped = await Child(files, files.Args("compact").Concat(new[] { "--automatic", "--context-window", "128000" }).ToArray());
        Check(skipped.Exit == 0 && JsonData.Parse(skipped.Output).Value.GetProperty("status").GetString() == "skipped" &&
            JsonData.Parse(skipped.Output).Value.GetProperty("providerRequests").GetInt32() == 0 && (await File.ReadAllBytesAsync(files.Session)).SequenceEqual(before), "Automatic threshold noop used provider or changed bytes.");
        foreach (var tail in new[] { new[] { "--reserve", "NaN" }, new[] { "--first-kept", "missing" }, new[] { "--retain-none", "--first-kept", "u" }, new[] { "--target", "right" } })
        {
            var rejected = await Child(files, files.Args("compact").Concat(tail).ToArray());
            Check(rejected.Exit != 0 && rejected.Output == "" && !JsonData.Parse(rejected.Error).Value.GetProperty("commitMayHaveOccurred").GetBoolean() &&
                (await File.ReadAllBytesAsync(files.Session)).SequenceEqual(before), "Invalid summary command wrote durable data.");
        }
        var branch = await Child(files, files.Args("branch-summary").Concat(new[] { "--target", "u" }).ToArray());
        Check(branch.Exit == 0 && branch.Error == "", "Actual branch summary failed: " + branch.Error);
        await using (var store = await SessionLogStore.OpenAsync(files.Session))
        {
            var entry = store.Snapshot.Entries[^1]; Check(entry.ParentId == "u" && entry.WireBody.Value.GetProperty("fromId").GetString() == "right" &&
                entry.WireBody.Value.GetProperty("summary").GetString()!.Contains("BRANCH", StringComparison.Ordinal), "Branch summary did not durably move to target ancestor.");
        }
        using var output = new BrokenWriter(); using var errors = new StringWriter();
        await files.Script("openai-responses", "LATE", ["BRANCH"]);
        Check(await SessionSummaryCommand.RunAsync(files.Args("compact").Concat(new[] { "--retain-none" }).ToArray(), output, errors) == 1, "Broken summary output succeeded.");
        var failure = JsonData.Parse(errors.ToString()).Value;
        Check(failure.GetProperty("checkpointAcknowledged").GetBoolean() && failure.GetProperty("commitMayHaveOccurred").GetBoolean() &&
            failure.GetProperty("code").GetString() == "OutputFailed", "Actual acknowledged checkpoint was hidden after output failure.");
        await using var reopened = await SessionLogStore.OpenAsync(files.Session); Check(reopened.Snapshot.Entries[^1].WireBody.Value.GetProperty("type").GetString() == "compaction", "Output failure rolled back summary.");
    }
    private static async Task ProviderBudgets()
    {
        foreach (var api in new[] { "openai-responses", "anthropic-messages", "openai-completions" })
        {
            using var files = new Files(); await files.Initialize(); await files.Script(api, "BUDGET", ["original input"]);
            var result = await Child(files, files.Args("compact").Concat(new[] { "--offline-api", api, "--retain-none", "--reserve", "64" }).ToArray());
            Check(result.Exit == 0 && result.Error == "" && JsonData.Parse(result.Output).Value.GetProperty("providerRequests").GetInt32() == 1,
                "Actual " + api + " summary budget/transport failed: " + result.Error);
            await using var store = await SessionLogStore.OpenAsync(files.Session); Check(store.Snapshot.Entries[^1].WireBody.Value.GetProperty("summary").GetString() == "BUDGET", "Actual provider summary text changed.");
        }
    }
    private static async Task Cooked()
    {
        using var text = new StringWriter(); using var frontend = new InteractiveSessionFrontend(text); var records = new List<JsonData>();
        frontend.Bind((value, _) => { records.Add(value); return Task.CompletedTask; });
        await frontend.LineAsync("/compact focus", default); Check(records[^1].Value.GetProperty("type").GetString() == "pisharp_compact" && records[^1].Value.GetProperty("customInstructions").GetString() == "focus", "Cooked compact did not send explicit native summary.");
        await frontend.LineAsync("/compact-all", default); Check(records[^1].Value.GetProperty("firstKeptEntryId").ValueKind == JsonValueKind.Null, "Cooked retain-none lost explicit null boundary.");
        await frontend.LineAsync("/branch-summary root", default); Check(records[^1].Value.GetProperty("targetId").ValueKind == JsonValueKind.Null, "Cooked branch root changed.");
        await frontend.ObserveAsync(JsonData.Parse("{\"type\":\"session_switched\",\"generation\":2,\"sessionId\":\"fresh\"}"), default);
        await frontend.LineAsync("/auto-compact on", default); Check(records[^1].Value.GetProperty("generation").GetInt64() == 2 && records[^1].Value.GetProperty("enabled").GetBoolean(), "Auto configuration used retired generation.");
        await frontend.LineAsync("/auto-compact off", default); Check(!records[^1].Value.GetProperty("enabled").GetBoolean(), "Cooked auto disable changed.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class BrokenWriter : StringWriter { public override Task WriteLineAsync(string? value) => throw new IOException("owned-output-fault"); }
    private sealed record Result(int Exit, string Output, string Error);
    private static async Task<Result> Child(Files files, params string[] args)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        start.ArgumentList.Add(cli); foreach (var argument in args) start.ArgumentList.Add(argument);
        using var child = Process.Start(start) ?? throw new IOException("Compiled summary command did not start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var output = Read(child.StandardOutput, deadline.Token); var errors = Read(child.StandardError, deadline.Token);
        try { await child.WaitForExitAsync(deadline.Token); return new(child.ExitCode, await output, await errors); }
        finally { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); } deadline.Cancel(); foreach (var task in new[] { output, errors }) try { await task; } catch (Exception) { } }
    }
    private static async Task<string> Read(StreamReader reader, CancellationToken token)
    { var text = new StringBuilder(); var buffer = new char[4096]; while (true) { var count = await reader.ReadAsync(buffer, token); if (count == 0) return text.ToString(); if (count > 1_048_576 - text.Length) throw new IOException("Summary command output exceeds bound."); text.Append(buffer, 0, count); } }
    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-summary-cli-" + Guid.NewGuid().ToString("N"));
        internal string Session => Path.Combine(Root, "session.jsonl"); internal string ScriptPath => Path.Combine(Root, "script.json");
        internal Files() => Directory.CreateDirectory(Root);
        internal string[] Args(string kind) => ["session", kind, "--session", Session, "--workspace", Root, "--offline-script", ScriptPath];
        internal async Task Initialize(bool branch = false)
        {
            var lines = new List<string> { JsonSerializer.Serialize(new { type = "session", version = 3, id = "summary", timestamp = "2026-10-02T00:00:00.000Z", cwd = Root }) };
            string User(string id, string? parentId, string content) => JsonSerializer.Serialize(new { type = "message", id, parentId, timestamp = "2026-10-02T00:00:00.000Z", message = new { role = "user", content, timestamp = 0 } });
            lines.Add(User("u", null, "original input")); if (branch) { lines.Add(User("left", "u", "left input")); lines.Add(User("right", "u", "right input")); }
            await File.WriteAllTextAsync(Session, string.Join('\n', lines) + "\n", Utf8);
        }
        internal Task Script(string api, string text, string[] required)
        {
            object[] events = api switch
            {
                "anthropic-messages" => [new { type = "message_start", message = new { id = "summary", type = "message", role = "assistant", model = "pisharp-offline-session", content = Array.Empty<object>(), stop_reason = (string?)null, usage = new { input_tokens = 4, output_tokens = 0 } } },
                    new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } }, new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } },
                    new { type = "content_block_stop", index = 0 }, new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 3 } }, new { type = "message_stop" }],
                "openai-completions" => [new { id = "summary", @object = "chat.completion.chunk", created = 0, model = "pisharp-offline-completions-session", choices = new[] { new { index = 0, delta = new { role = "assistant", content = text }, finish_reason = (string?)null } } },
                    new { id = "summary", @object = "chat.completion.chunk", created = 0, model = "pisharp-offline-completions-session", choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } }, usage = new { prompt_tokens = 4, completion_tokens = 3, total_tokens = 7 } }],
                _ => [new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "summary", content = Array.Empty<object>() } },
                    new { type = "response.output_text.delta", output_index = 0, item_id = "summary", delta = text }, new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "summary", content = new[] { new { type = "output_text", text } } } },
                    new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 4, output_tokens = 3, total_tokens = 7 } } }]
            };
            return File.WriteAllTextAsync(ScriptPath, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[] { new { requiredInputTexts = required, events } } }), Utf8);
        }
        public void Dispose()
        {
            var root = Path.GetFullPath(Root); Check(Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) && Path.GetFileName(root).StartsWith("pisharp-summary-cli-", StringComparison.Ordinal) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Unowned summary fixture root.");
            foreach (var path in Directory.GetFiles(root)) File.Delete(path); Directory.Delete(root, false);
        }
    }
}
