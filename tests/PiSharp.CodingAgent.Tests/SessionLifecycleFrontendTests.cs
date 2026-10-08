using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Contracts;
using PiSharp.Sessions.Storage;

internal static class SessionLifecycleFrontendTests
{
    private static string host = "", cli = "";
    public static (string Name, Func<Task> Run)[] Cases(string dotnetHost, string cliDll)
    {
        host = dotnetHost; cli = cliDll;
        return
        [
            ("session-lifecycle-frontend actual memory RPC replacement listing and shutdown never materialize", MemoryRpc),
            ("session-lifecycle-frontend actual lazy RPC setup and replacement EOF remain unmaterialized", LazyRpc),
            ("session-lifecycle-frontend actual lazy prompt checkpoints before held provider then independently reopens", LazyPrompt),
            ("session-lifecycle-frontend actual memory cooked prompt uses shared RPC and joins shutdown", MemoryCooked)
        ];
    }
    private static Task MemoryRpc() => SetupOnly("new-memory");
    private static Task LazyRpc() => SetupOnly("new-lazy");
    private static async Task SetupOnly(string mode)
    {
        using var files = new Files(); var directory = mode == "new-memory" ? Path.Combine(files.Root, "never-created") : files.Root;
        var source = Path.Combine(directory, "initial.jsonl"); await using var child = new Child(files, source, mode);
        var state = await child.Request(new { type = "get_state", id = "initial" });
        Check(state.GetProperty("success").GetBoolean(), "New backend startup state failed.");
        Check(state.GetProperty("data").GetProperty("pisharpPersistence").GetString() == (mode == "new-memory" ? "VolatileMemory" : "DeferredLocalFile") &&
            !state.GetProperty("data").GetProperty("pisharpDurableCheckpointAcknowledged").GetBoolean(), "Frontend claimed durable setup.");
        Check(!File.Exists(source), "Backend setup created a session file.");
        var changed = await child.Request(new { type = "new_session", id = "replacement", parentSession = "inert-missing-parent.jsonl" });
        Check(changed.GetProperty("success").GetBoolean(), "Backend replacement failed.");
        var target = changed.GetProperty("data").GetProperty("sessionFile").GetString()!;
        Check(target != source && !File.Exists(target) && !File.Exists(source), "Setup replacement eagerly wrote a session.");
        var page = await child.Request(new { type = "pisharp_list_sessions", id = "catalog", pageSize = 32 });
        Check(page.GetProperty("success").GetBoolean() && page.GetProperty("data").GetProperty("sessions").GetArrayLength() == 2,
            "Backend catalog lost the old or new volatile checkpoint.");
        await child.Close();
        Check(!File.Exists(source) && !File.Exists(target) && (mode != "new-memory" || !Directory.Exists(directory)), "EOF materialized backend setup.");
    }
    private static async Task LazyPrompt()
    {
        using var files = new Files(gated: true); var source = Path.Combine(files.Root, "lazy.jsonl");
        await using var child = new Child(files, source, "new-lazy");
        await child.Request(new { type = "get_state", id = "initial" });
        await child.Send(new { type = "prompt", id = "prompt", message = "first accepted user" });
        await child.Wait(record => record.TryGetProperty("type", out var type) && type.GetString() == "message_end" &&
            record.GetProperty("message").GetProperty("role").GetString() == "user");
        Check(File.Exists(source), "Actual accepted user did not materialize before held provider.");
        await using var checkpointFile = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var checkpointBytes = new MemoryStream();
        await checkpointFile.CopyToAsync(checkpointBytes);
        var held = await new SessionLogReader().ReadAsync(new MemoryStream(checkpointBytes.ToArray(), writable: false), leaveOpen: false);
        Check(held.Status == SessionLogReadStatus.Complete && held.ValidatedPrefix.Any(record => record.Entry.Type == "message" &&
            record.Entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "user"), "Actual pending prompt lost its checkpoint.");
        await child.Request(new { type = "get_state", id = "release" });
        await child.Wait(record => record.TryGetProperty("type", out var type) && type.GetString() == "agent_end");
        await child.Close();
        await using var reopened = await SessionLogStore.OpenAsync(source);
        var messages = reopened.Snapshot.Entries.Where(entry => entry.Type == "message").ToArray();
        Check(messages.Length == 3 && messages[0].WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "system" &&
            messages[1].WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "user" &&
            messages[2].WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "assistant" &&
            messages[2].WireBody.Value.GetProperty("message").GetProperty("stopReason").GetString() == "stop" &&
            messages[2].WireBody.Value.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString() == "backend final",
            "Independent writer reopen lost lazy conversation: " + string.Join(" | ", messages.Select(entry => entry.WireBody.ToString())));
    }
    private static async Task MemoryCooked()
    {
        using var files = new Files(); var directory = Path.Combine(files.Root, "never-created-cooked"); var source = Path.Combine(directory, "memory.jsonl");
        await using var child = new Child(files, source, "new-memory", cooked: true);
        await child.WaitLine(line => line.StartsWith("[history]", StringComparison.Ordinal));
        await child.SendLine("first accepted user");
        await child.WaitLine(line => line == "[settled]");
        await child.SendLine("/sessions"); await child.WaitLine(line => line.StartsWith("[sessions]", StringComparison.Ordinal));
        await child.SendLine("/quit"); await child.Close();
        Check(!Directory.Exists(directory) && !File.Exists(source), "Memory cooked session wrote a durable log.");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-lifecycle-front-" + Guid.NewGuid().ToString("N"));
        public string Script => Path.Combine(Root, "offline.json");
        public Files(bool gated = false)
        {
            Directory.CreateDirectory(Root);
            var turn = SessionCommandTests.CompletionsText("backend final", required: ["first accepted user"]);
            var body = JsonSerializer.SerializeToElement(turn);
            var raw = gated ? body.GetRawText()[..^1] + ",\"rpcGate\":{\"releaseOnGetStateId\":\"release\"}}" : body.GetRawText();
            File.WriteAllText(Script, "{\"schemaVersion\":1,\"turns\":[" + raw + "]}", new UTF8Encoding(false));
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
    private sealed class Child : IAsyncDisposable
    {
        private readonly Process process = new();
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = true });
        private readonly Task pump;
        private readonly Task<string> errors;
        private bool closed;
        public Child(Files files, string session, string mode, bool cooked = false)
        {
            process.StartInfo = new(host) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { cli, "session", cooked ? "chat" : "rpc", "--session", session, "--workspace", files.Root,
                "--offline-script", files.Script, "--offline-api", "openai-completions", "--session-mode", mode }) process.StartInfo.ArgumentList.Add(value);
            process.Start(); errors = process.StandardError.ReadToEndAsync();
            pump = Task.Run(async () =>
            { try { while (await process.StandardOutput.ReadLineAsync() is { } line) await lines.Writer.WriteAsync(line); } finally { lines.Writer.TryComplete(); } });
        }
        public Task Send(object record) => SendLine(JsonSerializer.Serialize(record));
        public async Task SendLine(string line) { await process.StandardInput.WriteLineAsync(line); await process.StandardInput.FlushAsync(); }
        public async Task<JsonElement> Request(object record)
        {
            var encoded = JsonSerializer.SerializeToElement(record); var id = encoded.GetProperty("id").GetString(); await Send(record);
            return await Wait(row => row.TryGetProperty("type", out var type) && type.GetString() == "response" &&
                row.TryGetProperty("id", out var value) && value.GetString() == id);
        }
        public async Task<JsonElement> Wait(Func<JsonElement, bool> predicate)
        {
            JsonElement found = default;
            await WaitLine(line => { var row = JsonData.Parse(line).Value; if (!predicate(row)) return false; found = row.Clone(); return true; });
            return found;
        }
        public async Task WaitLine(Func<string, bool> predicate)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await lines.Reader.WaitToReadAsync(deadline.Token)) while (lines.Reader.TryRead(out var line)) if (predicate(line)) return;
            throw new Exception("Actual backend host closed before expected observation: " + await errors);
        }
        public async Task Close()
        {
            if (closed) return; closed = true; process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await pump;
            Check(process.ExitCode == 0, "Actual backend host failed: " + await errors);
        }
        public async ValueTask DisposeAsync()
        {
            try { if (!closed) await Close(); }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                await pump; await errors; process.Dispose();
            }
        }
    }
}
