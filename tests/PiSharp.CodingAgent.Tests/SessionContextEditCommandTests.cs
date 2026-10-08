using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

internal static class SessionContextEditCommandTests
{
    private static string host = "", cli = "";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string cliDll)
    {
        host = dotnetHost; cli = cliDll;
        return [
            ("context-edit CLI actual append export reopen and next authored provider request use edited input", DurableNextRequest),
            ("context-edit CLI actual selected branch root target and strict replacement failures preserve complete source", BranchAdmission),
            ("context-edit CLI replacement exact cap EOF and cancellation join actual readonly file cleanup before writer", ReplacementIo),
            ("context-edit CLI prospective response bound and known checkpoint output failure remain honest", OutputTruth),
            ("context-edit cooked frontend exact JSON omission generation and invalid input admission", CookedCommands)
        ];
    }
    private static async Task DurableNextRequest()
    {
        using var files = new Files(); await files.Initialize(); var original = await File.ReadAllBytesAsync(files.Session);
        await File.WriteAllTextAsync(files.Replacement, "{\"content\":\"edited input\",\"extra\":{\"exact\":true}}", Utf8);
        var edited = await Child(files, Arguments(files));
        Check(edited.Exit == 0 && edited.Error == "", "Compiled context editor failed: " + edited.Error);
        var report = JsonData.Parse(edited.Output).Value;
        Check(report.GetProperty("checkpointAcknowledged").GetBoolean() && report.GetProperty("providerRequests").GetInt32() == 0,
            "Context editor did not acknowledge actual append or called a provider.");
        var after = await File.ReadAllBytesAsync(files.Session); Check(after.AsSpan(0, original.Length).SequenceEqual(original), "CLI editor rewrote history.");
        var export = Path.Combine(files.Root, "export.jsonl");
        var copied = await Child(files, "session", "copy", "--source", files.Session, "--destination", export, "--format", "native-exact");
        Check(copied.Exit == 0 && (await File.ReadAllBytesAsync(export)).SequenceEqual(after), "Model editing changed raw export semantics.");
        await using (var reopened = await SessionLogStore.OpenAsync(export))
        {
            Check(reopened.Snapshot.Entries.Length == 2 && reopened.Snapshot.Entries[0].WireBody.Value.GetProperty("message").GetProperty("content").GetString() == "original input",
                "Raw export lost original content.");
            var context = new SessionContextProjector().Project(reopened.Snapshot.Entries, reopened.Snapshot.LeafId);
            Check(context.LlmMessages[0].WireBody.Value.GetProperty("content").GetString() == "edited input" &&
                reopened.Snapshot.Entries[1].WireBody.Value.GetProperty("replacement").GetProperty("extra").GetProperty("exact").GetBoolean(), "Export/reopen lost replacement shape.");
        }
        var script = Path.Combine(files.Root, "script.json");
        await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[] { new {
            requiredInputTexts = new[] { "edited input", "next turn" }, events = new object[] {
                new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg-next", content = Array.Empty<object>() } },
                new { type = "response.output_text.delta", output_index = 0, item_id = "msg-next", delta = "next answer" },
                new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "msg-next", content = new[] { new { type = "output_text", text = "next answer" } } } },
                new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 } } }
            } } } }), Utf8);
        var resumed = await Child(files, "session", "resume", "--session", export, "--workspace", files.Root, "--offline-script", script, "--message", "next turn");
        Check(resumed.Exit == 0 && JsonData.Parse(resumed.Output).Value.GetProperty("requests")[0].GetProperty("requiredHistoryChecks").GetInt32() == 2,
            "Independent compiled next provider request did not consume the edited history.");
        Check((await File.ReadAllBytesAsync(files.Session)).SequenceEqual(after), "Independent exported-file continuation wrote original source.");
    }
    private static async Task BranchAdmission()
    {
        using var files = new Files(); await files.Initialize(branch: true); var original = await File.ReadAllBytesAsync(files.Session);
        await File.WriteAllTextAsync(files.Replacement, "null", Utf8);
        foreach (var tail in new[] { new[] { "--leaf", "left" }, new[] { "--root" } })
        {
            var invalid = await Child(files, Arguments(files, "right").Concat(tail).ToArray());
            Check(invalid.Exit == 1 && invalid.Output == "" && (await File.ReadAllBytesAsync(files.Session)).SequenceEqual(original), "Inactive/root target changed file.");
        }
        foreach (var target in new[] { "missing", "source", "system" })
        {
            var invalid = await Child(files, Arguments(files, target));
            Check(invalid.Exit == 1 && !JsonData.Parse(invalid.Error).Value.GetProperty("commitMayHaveOccurred").GetBoolean() &&
                (await File.ReadAllBytesAsync(files.Session)).SequenceEqual(original), "Missing/header/system target was editable.");
        }
        foreach (var raw in new[] { "{\"content\":\"a\",\"content\":\"b\"}", "{\"content\":true}", "{\"content\":\"\\uD800\"}", "{}" })
        {
            await File.WriteAllTextAsync(files.Replacement, raw, Utf8);
            var invalid = await Child(files, Arguments(files));
            Check(invalid.Exit == 1 && (await File.ReadAllBytesAsync(files.Session)).SequenceEqual(original), "Invalid replacement changed raw source.");
        }
        await File.WriteAllTextAsync(files.Replacement, "null", Utf8);
        var accepted = await Child(files, Arguments(files, "left").Concat(new[] { "--leaf", "left" }).ToArray());
        Check(accepted.Exit == 0, "Explicit active selected branch could not edit.");
        await using var store = await SessionLogStore.OpenAsync(files.Session);
        Check(store.Snapshot.Entries[^1].ParentId == "left" && new SessionContextProjector().Project(store.Snapshot.Entries, "right").LlmMessages.Any(
            message => message.WireBody.Value.GetProperty("content").ToString().Contains("right", StringComparison.Ordinal)), "Branch-relative edit leaked into sibling.");
    }
    private static async Task ReplacementIo()
    {
        using var files = new Files(); await files.Initialize(); await File.WriteAllTextAsync(files.Replacement, "null", Utf8);
        var held = new HeldRead(new FileStream(files.Replacement, FileMode.Open, FileAccess.Read, FileShare.Read), atEof: true, holdDispose: true);
        using var cancellation = new CancellationTokenSource(); using var output = new StringWriter(); using var error = new StringWriter();
        var original = await File.ReadAllBytesAsync(files.Session);
        var work = SessionContextEditCommand.RunAsync(Arguments(files), output, error, cancellation.Token, new(MaximumReplacementBytes: 4), (_, _) => ValueTask.FromResult<Stream>(held));
        try
        {
            await held.EofEntered.Task.WaitAsync(Bound); cancellation.Cancel(); await held.DisposeEntered.Task.WaitAsync(Bound);
            Check(!work.IsCompleted && (await File.ReadAllBytesAsync(files.Session)).SequenceEqual(original), "Canceled replacement abandoned cleanup or opened writer before EOF.");
            held.DisposeRelease.TrySetResult(); Check(await work.WaitAsync(Bound) == 1, "Canceled replacement succeeded.");
            Check(JsonData.Parse(error.ToString()).Value.GetProperty("code").GetString() == "Canceled" && held.Closed, "Replacement cancellation did not retain cause and actual close.");
        }
        finally { cancellation.Cancel(); held.DisposeRelease.TrySetResult(); await work; }
        using var acceptedOutput = new StringWriter(); using var acceptedError = new StringWriter();
        Check(await SessionContextEditCommand.RunAsync(Arguments(files), acceptedOutput, acceptedError, options: new(MaximumReplacementBytes: 4)) == 0,
            "Exact-cap replacement was rejected before its actual EOF probe.");
        var before = await File.ReadAllBytesAsync(files.Session); await File.WriteAllTextAsync(files.Replacement, "null ", Utf8);
        using var overOutput = new StringWriter(); using var overError = new StringWriter();
        Check(await SessionContextEditCommand.RunAsync(Arguments(files), overOutput, overError, options: new(MaximumReplacementBytes: 4)) == 1 &&
            (await File.ReadAllBytesAsync(files.Session)).SequenceEqual(before), "One byte over replacement cap was admitted.");
    }
    private static async Task OutputTruth()
    {
        using var files = new Files(); await files.Initialize(); await File.WriteAllTextAsync(files.Replacement, "{\"content\":\"private-replacement-value\"}", Utf8);
        var original = await File.ReadAllBytesAsync(files.Session); using var boundedOutput = new StringWriter(); using var boundedError = new StringWriter();
        Check(await SessionContextEditCommand.RunAsync(Arguments(files), boundedOutput, boundedError, options: new(MaximumOutputBytes: 256)) == 1 &&
            (await File.ReadAllBytesAsync(files.Session)).SequenceEqual(original), "CLI prospective output bound was enforced after effects.");
        using var canceled = new CancellationTokenSource(); using var broken = new BrokenWriter(canceled); using var errors = new StringWriter();
        Check(await SessionContextEditCommand.RunAsync(Arguments(files), broken, errors, canceled.Token) == 1, "Known-checkpoint delivery fault succeeded.");
        var report = JsonData.Parse(errors.ToString()).Value;
        Check(report.GetProperty("checkpointAcknowledged").GetBoolean() && report.GetProperty("commitMayHaveOccurred").GetBoolean() &&
            report.GetProperty("code").GetString() == "OutputFailed" && !errors.ToString().Contains("private-replacement-value", StringComparison.Ordinal),
            "CLI output fault denied known checkpoint or leaked replacement.");
        await using var store = await SessionLogStore.OpenAsync(files.Session); Check(store.Snapshot.Entries.Length == 2, "Late output cancellation rolled back a committed edit.");
    }
    private static async Task CookedCommands()
    {
        using var text = new StringWriter(); using var frontend = new InteractiveSessionFrontend(text); var sent = new List<JsonData>();
        frontend.Bind((record, _) => { sent.Add(record); return Task.CompletedTask; });
        await frontend.LineAsync("/context-omit u", default);
        Check(sent[^1].Value.GetProperty("replacement").ValueKind == JsonValueKind.Null && sent[^1].Value.GetProperty("generation").GetInt64() == 1, "Cooked omission lost null/generation.");
        await frontend.LineAsync("/context-replace u {\"content\":\"exact\",\"meta\":null}", default);
        Check(sent[^1].Value.GetProperty("replacement").GetProperty("meta").ValueKind == JsonValueKind.Null, "Cooked replacement flattened exact wrapper.");
        var count = sent.Count;
        foreach (var line in new[] { "/context-replace u null", "/context-replace u {\"prose\":true}", "/context-omit u extra", "/context-replace" })
            await frontend.LineAsync(line, default);
        Check(sent.Count == count, "Malformed cooked edit became a prompt or write command.");
        await frontend.ObserveAsync(JsonData.Parse("{\"type\":\"session_switched\",\"generation\":2,\"sessionId\":\"B\"}"), default);
        await frontend.LineAsync("/context-omit u", default); Check(sent[^1].Value.GetProperty("generation").GetInt64() == 2, "Cooked editor retained old generation.");
    }
    private static string[] Arguments(Files files, string target = "u") => ["session", "context-edit", "--session", files.Session, "--workspace", files.Root, "--target", target, "--replacement", files.Replacement];
    private sealed record Result(int Exit, string Output, string Error);
    private static async Task<Result> Child(Files files, params string[] arguments)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        start.ArgumentList.Add(cli); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start }; using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Check(process.Start(), "Compiled editor child did not start."); var output = Read(process.StandardOutput, deadline.Token); var error = Read(process.StandardError, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, await output, await error); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } deadline.Cancel(); foreach (var task in new[] { output, error }) try { await task; } catch (Exception) { } }
    }
    private static async Task<string> Read(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        while (true) { var count = await reader.ReadAsync(buffer, token); if (count == 0) return result.ToString(); if (count > 1_048_576 - result.Length) throw new InvalidOperationException("Editor child output exceeds bound."); result.Append(buffer, 0, count); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class BrokenWriter(CancellationTokenSource cancellation) : StringWriter
    { public override Task WriteLineAsync(string? value) { cancellation.Cancel(); throw new IOException("private-output-fault"); } }
    private sealed class HeldRead(Stream inner, bool atEof, bool holdDispose) : Stream
    {
        internal readonly TaskCompletionSource EofEntered = Gate(), DisposeEntered = Gate(), DisposeRelease = Gate(); internal bool Closed;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await inner.ReadAsync(buffer, token);
            if (read == 0 && atEof) { EofEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return read;
        }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); Closed = true; DisposeEntered.TrySetResult(); if (holdDispose) await DisposeRelease.Task; GC.SuppressFinalize(this); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "PiSharp-cli-context-edit-" + Guid.NewGuid().ToString("N"));
        internal string Session => Path.Combine(Root, "source.jsonl"); internal string Replacement => Path.Combine(Root, "replacement.json");
        internal Files() => Directory.CreateDirectory(Root);
        internal async Task Initialize(bool branch = false)
        {
            var header = JsonSerializer.Serialize(new { type = "session", version = 3, id = "source", timestamp = "2026-10-02T00:00:00.000Z", cwd = Root });
            string User(string id, string? parent, string content) => JsonSerializer.Serialize(new { type = "message", id, parentId = parent, timestamp = "2026-10-02T00:00:00.000Z", message = new { role = "user", content, timestamp = 0 } });
            var rows = new List<string> { header, User("u", null, "original input") };
            if (branch) { rows.Add(User("left", "u", "left input")); rows.Add(User("right", "u", "right input")); rows.Add("{\"type\":\"message\",\"id\":\"system\",\"parentId\":\"right\",\"timestamp\":\"2026-10-02T00:00:00.000Z\",\"message\":{\"role\":\"system\",\"content\":\"inert\",\"timestamp\":0}}"); }
            await File.WriteAllTextAsync(Session, string.Join("\r\n", rows) + "\r\n", Utf8);
        }
        public void Dispose()
        {
            var root = Path.GetFullPath(Root); Check(Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
                Path.GetFileName(root).StartsWith("PiSharp-cli-context-edit-", StringComparison.Ordinal) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0, "Unowned CLI fixture cleanup root.");
            foreach (var path in Directory.GetFiles(root)) File.Delete(path); Directory.Delete(root, false);
        }
    }
}
