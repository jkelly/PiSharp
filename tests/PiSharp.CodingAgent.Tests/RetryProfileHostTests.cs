using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Output;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

internal static class RetryProfileHostTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("retry-profile. ordinary command defaults and injected RPC settings require no persistence write", Startup),
        ("retry-profile. held persistence original precedes retry ACK and updated state", Acknowledgment),
        ("retry-profile. observed EOF cancels settings admission and joins its held original", Eof),
        ("retry-profile. persistence failure returns failed ACK and retains the old live preference", Failure)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static async Task Startup()
    {
        foreach (var injected in new[] { false, true })
        {
            using var files = new Files(); var writes = 0;
            Task Persist(bool enabled, CancellationToken token) { writes++; throw new InvalidOperationException("Read-only startup persisted a preference."); }
            await files.Create(injected, Persist);
            await using var host = new Host(files, injected, Persist);
            var state = await host.Response(new { id = "state", type = "get_state" });
            Check(state.Value.GetProperty("success").GetBoolean(), "Ordinary startup state failed.");
            var data = state.Value.GetProperty("data");
            Check(data.GetProperty("autoRetryEnabled").GetBoolean() == !injected && !data.GetProperty("isRetrying").GetBoolean(),
                "Ordinary host did not bind the effective retry settings/defaults.");
            var abort = await host.Response(new { id = "abort", type = "abort_retry" });
            Check(abort.Value.GetProperty("success").GetBoolean() && abort.Value.GetProperty("command").GetString() == "abort_retry",
                "Idle abort_retry did not acknowledge the actual supported command.");
            Check(writes == 0 && !host.History.Any(IsRetryEvent), "Read-only startup or idle abort acquired persistence/provider retry work.");
            Check(await host.Finish() == 0 && host.Error.ToString() == "", "Ordinary startup failed to close its ownership.");
            Check(files.Settings.Reads == (injected ? 1 : 0), "Settings projection used an ambient or duplicate read.");
        }
    }

    private static async Task Acknowledgment()
    {
        using var files = new Files(); await files.Create();
        var entered = Gate(); var original = Gate(); var calls = 0;
        await using var host = new Host(files, true, (enabled, token) =>
        { Check(enabled, "Preference callback changed the requested boolean."); calls++; entered.TrySetResult(); return original.Task; });
        var before = await host.Response(new { id = "before", type = "get_state" });
        Check(!before.Value.GetProperty("data").GetProperty("autoRetryEnabled").GetBoolean(), "Injected disabled policy was ignored.");
        await host.Send(new { id = "enable", type = "set_auto_retry", enabled = true });
        Exception? assertion = null;
        try
        {
            await host.BeforeCompletion(entered.Task);
            Check(calls == 1 && !host.HasResponse("enable") && !host.Completion.IsCompleted,
                "Retry command acknowledged before its original persistence checkpoint.");
            var observed = await host.Response(new { id = "during", type = "get_state" });
            Check(!observed.Value.GetProperty("data").GetProperty("autoRetryEnabled").GetBoolean() && !host.HasResponse("enable"),
                "Live retry preference changed before the persistence original acknowledged.");
        }
        catch (Exception error) { assertion = error; }
        finally { original.TrySetResult(); }
        var acknowledgment = await host.AwaitResponse("enable");
        if (assertion is not null) throw assertion;
        Check(acknowledgment.Value.GetProperty("success").GetBoolean(), "Acknowledged persistence produced a failed response.");
        var after = await host.Response(new { id = "after", type = "get_state" });
        Check(after.Value.GetProperty("data").GetProperty("autoRetryEnabled").GetBoolean() && calls == 1,
            "Acknowledged retry policy was not visible in the ordinary host.");
        Check(await host.Finish() == 0 && !host.History.Any(IsRetryEvent), "Settings change invented a provider retry or failed cleanup.");
    }

    private static async Task Eof()
    {
        using var files = new Files(); await files.Create();
        var entered = Gate(); var cancelled = Gate(); var original = Gate(); var calls = 0;
        CancellationTokenRegistration cancellation = default;
        await using var host = new Host(files, true, (enabled, token) =>
        {
            calls++; Check(enabled, "EOF control changed requested preference.");
            cancellation = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); return original.Task;
        });
        await host.Response(new { id = "before", type = "get_state" });
        await host.Send(new { id = "enable", type = "set_auto_retry", enabled = true });
        Exception? assertion = null;
        try
        {
            await host.BeforeCompletion(entered.Task); host.CompleteInput();
            await host.BeforeCompletion(cancelled.Task);
            Check(!host.Completion.IsCompleted && !original.Task.IsCompleted && calls == 1,
                "Observed EOF discarded the actual settings original instead of joining it.");
        }
        catch (Exception error) { assertion = error; }
        finally { original.TrySetResult(); cancellation.Dispose(); }
        var code = await host.Completion;
        if (assertion is not null) throw assertion;
        Check(code == 0 && calls == 1, "Acknowledged original settings work did not settle orderly EOF.");
    }

    private static async Task Failure()
    {
        using var files = new Files(); await files.Create();
        var marker = new IOException("retry profile original persistence fault"); var calls = 0;
        await using var host = new Host(files, true, (enabled, token) =>
        { calls++; return Task.FromException(marker); });
        var response = await host.Response(new { id = "enable", type = "set_auto_retry", enabled = true });
        Check(!response.Value.GetProperty("success").GetBoolean(), "Failed persistence produced a success acknowledgment.");
        var state = await host.Response(new { id = "after", type = "get_state" });
        Check(state.Value.GetProperty("success").GetBoolean() && !state.Value.GetProperty("data").GetProperty("autoRetryEnabled").GetBoolean() && calls == 1,
            "Failed preference checkpoint changed the admitted live retry policy.");
        // Public JSONL preserves failure disposition; exception object identity is checked in the core leaf.
        var code = await host.Finish();
        Check(code == 1 && host.Error.ToString().Contains("CleanupFailed", StringComparison.Ordinal),
            "Owning host suppressed the failed original settings work during final cleanup.");
    }

    private static bool IsRetryEvent(JsonData record) => record.Value.GetProperty("type").GetString() is "auto_retry_start" or "auto_retry_end";
    private sealed class Settings(string expected) : IStartupSettingsFileSystem
    {
        internal int Reads;
        public ValueTask<string?> ReadTextAsync(string absolutePath, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Check(absolutePath == expected, "Host read an unadmitted settings path."); Reads++; return ValueTask.FromResult<string?>("{\"retry\":{\"enabled\":false,\"maxRetries\":7,\"baseDelayMs\":0}}"); }
    }
    private sealed class Files : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "pisharp-retry-profile-" + Guid.NewGuid().ToString("N"));
        internal string Session => Path.Combine(Root, "session.jsonl");
        internal string Script => Path.Combine(Root, "script.json");
        internal string User => Path.Combine(Root, "settings.json");
        internal readonly Settings Settings;
        internal Files() { Directory.CreateDirectory(Root); Settings = new(User); }
        internal async Task Create(bool jsonOverload = false, Func<bool, CancellationToken, Task>? persist = null)
        {
            var output = new StringWriter(); var error = new StringWriter();
            string[] args = ["session", "create", "--session", Session, "--workspace", Root];
            var code = jsonOverload
                ? await SessionCommands.RunAsync(args, output, error, new SessionJsonEventOutputOptions(), persistRetryEnabledOriginal: persist)
                : await SessionCommands.RunAsync(args, output, error, persistRetryEnabledOriginal: persist);
            Check(code == 0 && error.ToString() == "", "Ordinary session fixture create failed.");
            await File.WriteAllTextAsync(Script, "{\"schemaVersion\":1,\"turns\":[{\"events\":[{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"total_tokens\":0}}}]}]}", new UTF8Encoding(false));
        }
        public void Dispose()
        {
            var actual = Path.GetFullPath(Root);
            Check(actual.StartsWith(Path.GetFullPath(Path.GetTempPath()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                Path.GetFileName(actual).StartsWith("pisharp-retry-profile-", StringComparison.Ordinal), "Invalid owned fixture root.");
            Directory.Delete(actual, true);
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        private readonly Channel<JsonData> records = Channel.CreateUnbounded<JsonData>();
        private readonly List<JsonData> history = [];
        private readonly BoundedRpcConnection connection;
        internal readonly StringWriter Error = new();
        internal readonly Task<int> Completion;
        internal JsonData[] History { get { lock (history) return history.ToArray(); } }
        internal Host(Files files, bool injected, Func<bool, CancellationToken, Task> persist)
        {
            connection = new((record, token) =>
            { lock (history) history.Add(record); records.Writer.TryWrite(record); return ValueTask.CompletedTask; });
            string[] args = ["session", "rpc", "--session", files.Session, "--workspace", files.Root, "--offline-script", files.Script,
                .. (injected ? new[] { "--user-settings", files.User } : Array.Empty<string>())];
            Completion = Run();
            async Task<int> Run()
            {
                try
                {
                    return injected
                        ? await RpcSessionCommand.RunWithSettingsAsync(args, connection.Input, connection.Output, Error, files.Settings,
                            persistRetryEnabledOriginal: persist)
                        : await RpcSessionCommand.RunAsync(args, connection.Input, connection.Output, Error, persistRetryEnabledOriginal: persist);
                }
                finally { records.Writer.TryComplete(); }
            }
        }
        internal bool HasResponse(string id) => History.Any(record => record.Value.GetProperty("type").GetString() == "response" &&
            record.Value.TryGetProperty("id", out var value) && value.GetString() == id);
        internal Task Send(object command) => connection.SendAsync(JsonData.Parse(JsonSerializer.Serialize(command)), CancellationToken.None);
        internal async Task<JsonData> Response(object command)
        {
            var record = JsonData.Parse(JsonSerializer.Serialize(command));
            await connection.SendAsync(record, CancellationToken.None);
            return await AwaitResponse(record.Value.GetProperty("id").GetString()!);
        }
        internal async Task<JsonData> AwaitResponse(string id)
        {
            while (true)
            {
                JsonData record;
                try { record = await records.Reader.ReadAsync(); }
                catch (ChannelClosedException)
                { var code = await Completion; throw new InvalidOperationException($"Ordinary host closed before {id}; exit {code}; {Error}"); }
                if (record.Value.GetProperty("type").GetString() == "response" && record.Value.TryGetProperty("id", out var value) && value.GetString() == id)
                    return record;
            }
        }
        internal async Task BeforeCompletion(Task expected)
        { Check(ReferenceEquals(await Task.WhenAny(expected, Completion), expected), "Host completed before the expected original callback boundary."); await expected; }
        internal void CompleteInput() => connection.CompleteInput();
        internal async Task<int> Finish() { CompleteInput(); return await Completion; }
        public async ValueTask DisposeAsync()
        { CompleteInput(); try { await Completion; } finally { await connection.DisposeAsync(); Error.Dispose(); } }
    }
}
