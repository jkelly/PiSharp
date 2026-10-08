using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Sessions.Storage;

internal static class SessionCatalogFrontendTests
{
    private static string host = "", cli = "";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string cliDll)
    {
        host = dotnetHost; cli = cliDll;
        return [
            ("catalog-frontend.numbered-resume-root-leaf-and-safe-display", Selection),
            ("catalog-frontend.pagination-newest-response-and-replacement-generation", Generations),
            ("catalog-frontend.bounded-admission-and-malformed-page-atomicity", Bounds),
            ("catalog-frontend.actual-readonly-cli-list-and-explicit-store-arguments", CliListing),
            ("catalog-frontend.actual-cooked-list-resume-selected-leaf-turn-and-reopen", CookedResume)
        ];
    }
    private static async Task Selection()
    {
        using var view = new StringWriter(); using var frontend = new InteractiveSessionFrontend(view); var sent = Bind(frontend);
        await frontend.LineAsync("/sessions", default); var listing = sent.Single().Value;
        Check(listing.GetProperty("type").GetString() == "pisharp_list_sessions" && !listing.TryGetProperty("cwd", out _) && !listing.TryGetProperty("cursor", out _), "Optional absent fields became null RPC properties.");
        await Page(frontend, sent[0], 1, [Item('a', "same\0\u001b[31m.jsonl"), Item('b', "same.jsonl")]);
        Check(view.ToString().Contains("\\u0000\\u001b[31m", StringComparison.Ordinal) && !view.ToString().Contains('\u001b'), "Listing executed or discarded inert controls.");
        await frontend.LineAsync("/resume 2", default); var latest = sent[^1].Value;
        Check(latest.GetProperty("catalogKey").GetString() == new string('b', 64) && !latest.TryGetProperty("leafId", out _) && !latest.TryGetProperty("root", out _), "Numbered selection did not preserve exact opaque identity and latest leaf.");
        await frontend.LineAsync("/resume 1 left", default); Check(sent[^1].Value.GetProperty("leafId").GetString() == "left", "Explicit leaf was lost.");
        await frontend.LineAsync("/resume 1 @root", default); Check(sent[^1].Value.GetProperty("root").GetBoolean() && !sent[^1].Value.TryGetProperty("leafId", out _), "Root selection acquired a leaf.");
    }
    private static async Task Generations()
    {
        using var view = new StringWriter(); using var frontend = new InteractiveSessionFrontend(view); var sent = Bind(frontend);
        await frontend.LineAsync("/sessions /foreign cwd", default); var old = sent[^1];
        await frontend.LineAsync("/sessions /new cwd", default); var newer = sent[^1];
        await Page(frontend, newer, 1, [Item('b')], "next"); await Page(frontend, old, 1, [Item('a')]);
        await frontend.LineAsync("/sessions-next", default);
        Check(sent[^1].Value.GetProperty("cwd").GetString() == "/new cwd" && sent[^1].Value.GetProperty("cursor").GetString() == "next", "Late old query replaced current pagination filters.");
        var pending = sent[^1];
        await frontend.LineAsync("/edit", default); await frontend.LineAsync("old unsent draft", default);
        await frontend.ObserveAsync(Record(new { type = "session_switched", sessionId = "same", generation = 2 }), default);
        await Page(frontend, pending, 1, [Item('c')], "stale"); var before = sent.Count;
        await frontend.LineAsync("/resume 1", default); await frontend.LineAsync("/sessions-next", default);
        Check(sent.Count == before, "Old-generation cache enabled selection or pagination after replacement.");
        await frontend.ObserveAsync(Record(new { type = "response", command = "fork", success = true, data = new { cancelled = false, generation = 1, text = "stale fork draft" } }), default);
        await frontend.LineAsync("/edit", default); await frontend.LineAsync("fresh draft", default); await frontend.LineAsync("/save", default);
        Check(sent[^1].Value.GetProperty("message").GetString() == "fresh draft", "Stale fork or prior draft crossed replacement generation.");
        await frontend.LineAsync("/sessions", default); await Page(frontend, sent[^1], 2, [Item('d')]);
        await frontend.ObserveAsync(Record(new { type = "session_switched", sessionId = "stale", generation = 1 }), default);
        await frontend.LineAsync("/resume 1", default); Check(sent[^1].Value.GetProperty("catalogKey").GetString() == new string('d', 64), "Stale switch event reset current selection.");
    }
    private static async Task Bounds()
    {
        using var view = new StringWriter(); using var frontend = new InteractiveSessionFrontend(view); var sent = Bind(frontend);
        for (var i = 0; i < 5; i++) await frontend.LineAsync("/sessions", default);
        Check(sent.Count == 4, "Frontend exceeded outstanding catalog query bound.");
        await Page(frontend, sent[0], 1, [Item('a')]); await frontend.LineAsync("/sessions", default); Check(sent.Count == 5, "Completed stale read did not release its own slot.");
        var last = sent[^1]; await Reject(() => Page(frontend, last, 1, [Item('a'), Item('a')]));
        var before = sent.Count; await frontend.LineAsync("/resume 1", default); Check(sent.Count == before, "Partially malformed page published the first choice.");
        await frontend.LineAsync("/sessions", default); await Page(frontend, sent[^1], 1, [Item('b')]);
        await frontend.LineAsync("/sessions", default); await frontend.ObserveAsync(Record(new { type = "response", id = sent[^1].Value.GetProperty("id").GetString(), command = "pisharp_list_sessions", success = false }), default);
        before = sent.Count; await frontend.LineAsync("/resume 1", default); Check(before == sent.Count, "Failed listing retained stale choices despite refresh instruction.");
    }
    private static async Task CliListing()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-catalog-cli-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "left"); var right = Path.Combine(root, "right"); Directory.CreateDirectory(left); Directory.CreateDirectory(right);
        var paths = new[] { Path.Combine(left, "same.jsonl"), Path.Combine(right, "same.jsonl") };
        var data = JsonSerializer.Serialize(new { type = "session", version = 3, id = "same", timestamp = "2026-10-02T00:00:00Z", cwd = root }) + "\nnot a resumable body\n";
        try
        {
            foreach (var path in paths) await File.WriteAllTextAsync(path, data, new UTF8Encoding(false));
            var originals = paths.Select(File.ReadAllBytes).ToArray();
            // Discovery works while another process/owner can retain the target writer. It validates only headers.
            using var writer = new FileStream(paths[0], FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var result = await Child("session", "list", "--session-store", "left=" + left, "--session-store", "right=" + right);
            Check(result.Exit == 0, "Actual compiled CLI discovery failed: " + result.Error);
            var page = JsonData.Parse(result.Output).Value.GetProperty("page"); var items = page.GetProperty("items");
            Check(items.GetArrayLength() == 2 && items[0].GetProperty("key").GetString() != items[1].GetProperty("key").GetString(), "CLI merged identical sessions from separate stores.");
            Check(JsonData.Parse(result.Output).Value.GetProperty("headerOnly").GetBoolean(), "CLI falsely represented full body validity.");
            var invalid = await Child("session", "list", "--session-store", "duplicate=" + left, "--session-store", "duplicate=" + right);
            Check(invalid.Exit == 2 && string.IsNullOrWhiteSpace(invalid.Output), "Duplicate explicit store identities reached discovery.");
            invalid = await Child("session", "list", "--session-store", "left=" + left, "--page-size", "129"); Check(invalid.Exit == 2, "CLI page limit was not enforced.");
            for (var i = 0; i < paths.Length; i++)
            {
                using var read = new FileStream(paths[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var bytes = new MemoryStream(); read.CopyTo(bytes); Check(bytes.ToArray().SequenceEqual(originals[i]), "Readonly CLI changed existing bytes.");
            }
        }
        finally { foreach (var path in paths) if (File.Exists(path)) File.Delete(path); Directory.Delete(left); Directory.Delete(right); Directory.Delete(root); }
    }
    private static async Task<(int Exit, string Output, string Error)> Child(params string[] args)
    {
        using var process = new Process { StartInfo = new(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add(cli); foreach (var argument in args) process.StartInfo.ArgumentList.Add(argument);
        process.Start(); var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); await Task.WhenAll(output, error); throw; }
        return (process.ExitCode, await output, await error);
    }
    private static async Task CookedResume()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-catalog-cooked-" + Guid.NewGuid().ToString("N"));
        var left = Path.Combine(root, "left"); var right = Path.Combine(root, "right"); Directory.CreateDirectory(left); Directory.CreateDirectory(right);
        var source = Path.Combine(left, "same.jsonl"); var target = Path.Combine(right, "same.jsonl"); var script = Path.Combine(root, "script.json");
        var header = JsonSerializer.Serialize(new { type = "session", version = 3, id = "same", timestamp = "2026-10-02T00:00:00Z", cwd = root }) + "\n";
        string State(string id, string value) => JsonSerializer.Serialize(new { type = "custom", id, parentId = (string?)null, timestamp = "2026-10-02T00:00:00Z",
            customType = "pisharp.extension-state", data = new { extensionId = "fixture", entryKind = "state", schemaVersion = 1, data = new { value } } }) + "\n";
        using var process = new Process { StartInfo = new(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        Task? pump = null; Task<string>? errors = null; var started = false;
        try
        {
            await File.WriteAllTextAsync(source, header, new UTF8Encoding(false)); await File.WriteAllTextAsync(target, header + State("left", "left") + State("right", "right"), new UTF8Encoding(false));
            await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[] { SessionCommandTests.CompletionsText("resumed final", required: ["resume current"]) } }), new UTF8Encoding(false));
            var sourceBytes = await File.ReadAllBytesAsync(source); var targetBytes = await File.ReadAllBytesAsync(target);
            foreach (var argument in new[] { cli, "session", "chat", "--session", source, "--workspace", root, "--offline-api", "openai-completions", "--offline-script", script,
                "--session-store", "source=" + left, "--session-store", "target=" + right }) process.StartInfo.ArgumentList.Add(argument);
            process.Start(); started = true; errors = process.StandardError.ReadToEndAsync();
            var lines = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
            pump = Task.Run(async () => { try { while (await process.StandardOutput.ReadLineAsync() is { } line) await lines.Writer.WriteAsync(line); } finally { lines.Writer.TryComplete(); } });
            async Task Wait(Func<string, bool> predicate)
            { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (await lines.Reader.WaitToReadAsync(deadline.Token)) while (lines.Reader.TryRead(out var line)) if (predicate(line)) return; throw new IOException("Cooked host ended before expected observation."); }
            async Task Send(string line) { await process.StandardInput.WriteLineAsync(line); await process.StandardInput.FlushAsync(); }
            await Wait(line => line.StartsWith("[history]", StringComparison.Ordinal));
            await Send("/sessions"); await Wait(line => line.StartsWith("[sessions]", StringComparison.Ordinal));
            // Listing keys are deterministic header identities, so select the actual explicit target from the readonly compiled CLI.
            var listed = await Child("session", "list", "--session-store", "source=" + left, "--session-store", "target=" + right);
            Check(listed.Exit == 0, "Concurrent readonly CLI listing failed.");
            var key = JsonData.Parse(listed.Output).Value.GetProperty("page").GetProperty("items").EnumerateArray().Single(item => item.GetProperty("storeId").GetString() == "target").GetProperty("key").GetString();
            await Send("/resume " + key + " left"); await Wait(line => line == "[session] same");
            await Send("resume current"); await Wait(line => line == "[settled]");
            await Send("/quit"); process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await pump;
            Check(process.ExitCode == 0 && (await errors).Length == 0, "Compiled cooked resume failed after cleanup.");
            Check((await File.ReadAllBytesAsync(source)).SequenceEqual(sourceBytes), "Cooked resume wrote into old A.");
            var reopened = await new SessionLogReader().ReadFileAsync(target); Check(reopened.SourceComplete && reopened.Status == SessionLogReadStatus.Complete, "Cooked target did not reopen as complete history.");
            var bytes = await File.ReadAllBytesAsync(target); Check(bytes.AsSpan().StartsWith(targetBytes), "Cooked prompt changed B's original bytes.");
            var entries = reopened.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToArray();
            var user = entries.Single(entry => entry.Type == "message" && entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "user");
            Check(user.ParentId == "left" && entries.Any(entry => entry.Type == "message" && entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "assistant"), "Actual fresh prompt ignored selected leaf or lost final durable output.");
        }
        finally
        {
            if (started && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            if (pump is not null) await pump; if (errors is not null) await errors;
            foreach (var file in new[] { source, target, script }) if (File.Exists(file)) File.Delete(file);
            Directory.Delete(left); Directory.Delete(right); Directory.Delete(root);
        }
    }
    private static object Item(char key, string filename = "same.jsonl") => new { key = new string(key, 64), fileName = filename, sessionId = "same", cwd = "/cwd", isCurrent = false };
    private static Task Page(InteractiveSessionFrontend frontend, JsonData request, long generation, object[] sessions, string? cursor = null) => frontend.ObserveAsync(Record(new
    { type = "response", id = request.Value.GetProperty("id").GetString(), command = "pisharp_list_sessions", success = true, data = new { generation, sessions, nextCursor = cursor, skippedFiles = 0, unavailableStores = 0 } }), default).AsTask();
    private static List<JsonData> Bind(InteractiveSessionFrontend frontend)
    { var sent = new List<JsonData>(); frontend.Bind((record, _) => { sent.Add(record); return Task.CompletedTask; }); return sent; }
    private static JsonData Record(object value) => JsonData.Parse(JsonSerializer.Serialize(value));
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (InvalidOperationException) { return; } throw new InvalidOperationException("Invalid page was admitted."); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
