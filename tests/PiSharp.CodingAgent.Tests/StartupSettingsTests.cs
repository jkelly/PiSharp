using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

internal static class StartupSettingsTests
{
    public const string Prefix = "settings startup ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "user project invocation precedence merges nested data and replaces arrays", Merge),
        (Prefix + "BOM duplicates legacy queueMode and missing null semantics", JsonSemantics),
        (Prefix + "read and parse failures isolate layers with fixed diagnostics", Failures),
        (Prefix + "cancellation joins original settings read and skips next layer", Cancellation),
        (Prefix + "actual RPC host applies injected settings and CLI overrides", Wired),
        (Prefix + "actual RPC host loads explicit physical user and project JSON", Physical),
        (Prefix + "no flags avoid settings reads and malformed settings preserve protocol", Defaults),
        (Prefix + "CLI invalid paths duplicate options and invalid modes reject before reads", InvalidArguments),
        (Prefix + "host cancellation awaits settings read before returning", HostCancellation)
    ];
    private static string P(string name) => Path.Combine(Path.GetTempPath(), "pisharp-settings-injected-" + name + ".json");
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
    internal sealed class Files : IStartupSettingsFileSystem
    {
        internal Dictionary<string, string?> Text { get; } = new(StringComparer.Ordinal);
        internal List<string> Reads { get; } = [];
        internal Func<string, CancellationToken, ValueTask<string?>>? Pending;
        public ValueTask<string?> ReadTextAsync(string path, CancellationToken token)
        { Reads.Add(path); return Pending is not null ? Pending(path, token) : ValueTask.FromResult(Text.GetValueOrDefault(path)); }
    }
    private static async Task Merge()
    {
        var fs = new Files(); var user = P("user"); var project = P("project");
        fs.Text[user] = "{\"steeringMode\":\"all\",\"followUpMode\":\"all\",\"nested\":{\"a\":1,\"b\":2},\"list\":[1],\"defaultTools\":[\"read\"]}";
        fs.Text[project] = "{\"steeringMode\":\"one-at-a-time\",\"nested\":{\"b\":3},\"list\":[],\"defaultTools\":[\"+bash\"]}";
        var snapshot = await StartupSettings.LoadAsync(new(user, project, JsonData.Parse("{\"steeringMode\":\"all\",\"nested\":{\"a\":4}}")), fs);
        Equal(AgentPendingInputMode.All, snapshot.SteeringMode); Equal(AgentPendingInputMode.All, snapshot.FollowUpMode);
        var raw = snapshot.Values.Value; Equal(4, raw.GetProperty("nested").GetProperty("a").GetInt32());
        Equal(3, raw.GetProperty("nested").GetProperty("b").GetInt32()); Equal(0, raw.GetProperty("list").GetArrayLength());
        Equal("read", raw.GetProperty("defaultTools")[0].GetString()); Equal("+bash", raw.GetProperty("defaultTools")[1].GetString());
        Equal(user + "|" + project, string.Join('|', fs.Reads)); Equal(0, snapshot.Diagnostics.Length);
        fs.Text[user] = "{}"; Equal(4, snapshot.Values.Value.GetProperty("nested").GetProperty("a").GetInt32());
    }
    private static async Task JsonSemantics()
    {
        var fs = new Files(); var path = P("json");
        fs.Text[path] = "\uFEFF{\"queueMode\":\"all\",\"followUpMode\":\"all\",\"followUpMode\":null}";
        var result = await StartupSettings.LoadAsync(new(UserPath: path), fs);
        Equal(AgentPendingInputMode.All, result.SteeringMode); Equal(AgentPendingInputMode.OneAtATime, result.FollowUpMode);
        Equal(false, result.Values.Value.TryGetProperty("queueMode", out _));
        fs.Text[path] = "{\"queueMode\":\"all\",\"steeringMode\":null,\"followUpMode\":false}";
        result = await StartupSettings.LoadAsync(new(UserPath: path), fs);
        Equal(AgentPendingInputMode.OneAtATime, result.SteeringMode); Equal(true, result.Values.Value.TryGetProperty("queueMode", out _));
        Equal(0, result.Diagnostics.Length);
        foreach (var value in new[] { "0", "\"\"", "null" })
        {
            fs.Text[path] = "{\"steeringMode\":" + value + "}";
            Equal(AgentPendingInputMode.OneAtATime, (await StartupSettings.LoadAsync(new(UserPath: path), fs)).SteeringMode);
        }
    }
    private static async Task Failures()
    {
        var fs = new Files(); var user = P("user"); var project = P("project");
        fs.Text[user] = "{bad secret-marker"; fs.Text[project] = "{\"followUpMode\":\"all\"}";
        var result = await StartupSettings.LoadAsync(new(user, project), fs);
        Equal(AgentPendingInputMode.All, result.FollowUpMode); Equal("InvalidJson", result.Diagnostics.Single().Code);
        fs.Pending = (path, _) => path == user ? throw new IOException("secret-marker") : ValueTask.FromResult(fs.Text[project]);
        result = await StartupSettings.LoadAsync(new(user, project), fs); Equal("ReadFailed", result.Diagnostics.Single().Code);
        Equal(false, JsonSerializer.Serialize(result.Diagnostics).Contains("secret-marker", StringComparison.Ordinal));
        fs.Pending = null; fs.Text[user] = "{\"steeringMode\":\"unsupported\"}";
        result = await StartupSettings.LoadAsync(new(user), fs); Equal("InvalidQueueMode:steeringMode", result.Diagnostics.Single().Code);
        fs.Text[user] = null; Equal(0, (await StartupSettings.LoadAsync(new(user), fs)).Diagnostics.Length);
    }
    private static async Task Cancellation()
    {
        using var stop = new CancellationTokenSource(); var entered = Signal(); var release = Signal(); var joined = false;
        var fs = new Files { Pending = async (_, token) => { Equal(stop.Token, token); entered.TrySetResult(); await release.Task; joined = true; return "{}"; } };
        var original = StartupSettings.LoadAsync(new(P("user"), P("project")), fs, stop.Token);
        Exception? primaryFailure = null; Exception? settlementFailure = null;
        try
        {
            await Task.WhenAny(entered.Task, original);
            if (!entered.Task.IsCompletedSuccessfully)
            { await original; throw new InvalidOperationException("Original settings read was not entered."); }
            stop.Cancel(); Equal(false, original.IsCompleted);
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            release.TrySetResult();
            // Join even when a preceding assertion throws. Record settlement separately
            // so it cannot replace that primary failure during finally unwinding.
            try { await original; }
            catch (Exception error)
            {
                settlementFailure = error;
                if (primaryFailure is not null && !ReferenceEquals(primaryFailure, error) &&
                    !(error is OperationCanceledException own && own.CancellationToken == stop.Token && stop.IsCancellationRequested))
                    primaryFailure.Data["OriginalSettingsReadSettlementFailure"] = error;
            }
        }
        if (settlementFailure is not OperationCanceledException canceled)
            throw new InvalidOperationException("Expected original settings-read cancellation.", settlementFailure);
        Equal(stop.Token, canceled.CancellationToken);
        Equal(true, joined); Equal(1, fs.Reads.Count);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wired()
    {
        using var fixture = new Fixture(); var fs = new Files();
        fs.Text[fixture.User] = "{\"steeringMode\":\"all\",\"followUpMode\":\"one-at-a-time\"}";
        fs.Text[fixture.Project] = "{\"steeringMode\":\"one-at-a-time\",\"followUpMode\":\"all\",\"extensions\":[\"not-admitted\"],\"defaultTools\":[\"read\"]}";
        await using var host = new Host(fixture, fs, ["--user-settings", fixture.User, "--project-settings", fixture.Project, "--steering-mode", "all"]);
        var state = (await host.Response("state", "get_state")).Value.GetProperty("data");
        Equal("all", state.GetProperty("steeringMode").GetString()); Equal("all", state.GetProperty("followUpMode").GetString());
        fs.Text[fixture.Project] = "{}";
        state = (await host.Response("again", "get_state")).Value.GetProperty("data"); Equal("all", state.GetProperty("followUpMode").GetString());
        var commands = (await host.Response("commands", "get_commands")).Value.GetProperty("data").GetProperty("commands"); Equal(0, commands.GetArrayLength());
        Equal(2, fs.Reads.Count); Equal(0, await host.Finish()); Equal("", host.Error.ToString());
    }
    private static async Task Physical()
    {
        using var fixture = new Fixture();
        await File.WriteAllTextAsync(fixture.User, "{\"steeringMode\":\"all\",\"followUpMode\":\"all\"}");
        await File.WriteAllTextAsync(fixture.Project, "{\"steeringMode\":\"one-at-a-time\"}");
        await using var host = new Host(fixture, null, ["--user-settings", fixture.User, "--project-settings", fixture.Project, "--follow-up-mode", "one-at-a-time"]);
        var state = (await host.Response("state", "get_state")).Value.GetProperty("data");
        Equal("one-at-a-time", state.GetProperty("steeringMode").GetString()); Equal("one-at-a-time", state.GetProperty("followUpMode").GetString());
        Equal(0, await host.Finish()); Equal("", host.Error.ToString());
    }
    private static async Task Defaults()
    {
        using var fixture = new Fixture(); var fs = new Files { Pending = (_, _) => throw new InvalidOperationException("Unexpected implicit settings read.") };
        await using (var host = new Host(fixture, fs, []))
        {
            var state = (await host.Response("state", "get_state")).Value.GetProperty("data"); Equal("one-at-a-time", state.GetProperty("steeringMode").GetString());
            Equal(0, fs.Reads.Count); Equal(0, await host.Finish());
        }
        fs.Pending = null; fs.Text[fixture.User] = "{bad secret-marker";
        await using var invalid = new Host(fixture, fs, ["--user-settings", fixture.User, "--follow-up-mode", "all"]);
        Equal("all", (await invalid.Response("state", "get_state")).Value.GetProperty("data").GetProperty("followUpMode").GetString());
        Equal(0, await invalid.Finish()); var diagnostic = JsonData.Parse(invalid.Error.ToString()).Value;
        Equal("settings_diagnostic", diagnostic.GetProperty("type").GetString()); Equal("InvalidJson", diagnostic.GetProperty("code").GetString());
        Equal(false, invalid.Error.ToString().Contains("secret-marker", StringComparison.Ordinal));
    }
    private static async Task HostCancellation()
    {
        using var fixture = new Fixture(); var entered = Signal(); var release = Signal(); var joined = false;
        var fs = new Files { Pending = async (_, _) => { entered.TrySetResult(); await release.Task; joined = true; return "{}"; } };
        await using var host = new Host(fixture, fs, ["--user-settings", fixture.User, "--project-settings", fixture.Project]);
        try { await Task.WhenAny(entered.Task, host.Completion); Equal(true, entered.Task.IsCompletedSuccessfully); host.Cancel(); Equal(false, host.Completion.IsCompleted); }
        finally { release.TrySetResult(); }
        Equal(1, await host.Completion); Equal(true, joined); Equal(1, fs.Reads.Count); Equal(true, host.Error.ToString().Contains("Canceled", StringComparison.Ordinal));
    }
    private static async Task InvalidArguments()
    {
        using var fixture = new Fixture();
        foreach (var flags in new[] { new[] { "--user-settings", "relative.json" },
            new[] { "--project-settings", fixture.Project, "--project-settings", fixture.Project },
            new[] { "--steering-mode", "invalid" }, new[] { "--follow-up-mode", "" } })
        {
            var fs = new Files(); await using var host = new Host(fixture, fs, flags);
            Equal(2, await host.Completion); Equal(0, fs.Reads.Count);
        }
    }
    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-settings-owned-" + Guid.NewGuid().ToString("N"));
        internal string User => Path.Combine(Root, "user.json"); internal string Project => Path.Combine(Root, "project.json");
        internal string Script => Path.Combine(Root, "script.json");
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Script, "{\"schemaVersion\":1,\"turns\":[{\"events\":[{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"total_tokens\":0}}}]}]}");
        }
        public void Dispose()
        {
            var target = Path.GetFullPath(Root); var parent = Path.GetFullPath(Path.GetTempPath());
            if (!target.StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(target).StartsWith("pisharp-settings-owned-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid owned cleanup root.");
            Directory.Delete(target, recursive: true);
        }
    }
    internal sealed class Host : IAsyncDisposable
    {
        private readonly Channel<JsonData> records = Channel.CreateUnbounded<JsonData>();
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        private readonly BoundedRpcConnection connection;
        internal StringWriter Error { get; } = new(); internal Task<int> Completion { get; }
        internal Host(Fixture fixture, IStartupSettingsFileSystem? fs, string[] flags, string mode = "new-memory")
        {
            connection = new((record, _) => { records.Writer.TryWrite(record); return ValueTask.CompletedTask; });
            string[] args = ["session", "rpc", "--session", Path.Combine(fixture.Root, "session.jsonl"), "--workspace", fixture.Root,
                "--offline-script", fixture.Script, "--session-mode", mode, .. flags];
            Completion = fs is null ? RpcSessionCommand.RunAsync(args, connection.Input, connection.Output, Error, deadline.Token) :
                RpcSessionCommand.RunWithSettingsAsync(args, connection.Input, connection.Output, Error, fs, deadline.Token);
        }
        internal void Cancel() => deadline.Cancel();
        internal async Task MaterializeConversation()
        {
            const string id = "materialize";
            await connection.SendAsync(JsonData.Parse(JsonSerializer.Serialize(new
                { id, type = "prompt", message = "Persist the offline startup-tool fixture." })), deadline.Token);
            var accepted = false; var ended = false;
            while (!accepted || !ended)
            {
                var record = (await records.Reader.ReadAsync(deadline.Token)).Value;
                var type = record.GetProperty("type").GetString();
                if (type == "response" && record.GetProperty("id").GetString() == id)
                { Equal(true, record.GetProperty("success").GetBoolean()); accepted = true; }
                if (type == "agent_end") ended = true;
            }
        }
        internal async Task<JsonData> Response(string id, string type)
        {
            await connection.SendAsync(JsonData.Parse(JsonSerializer.Serialize(new { id, type })), deadline.Token);
            while (true)
            {
                var record = await records.Reader.ReadAsync(deadline.Token);
                if (record.Value.GetProperty("type").GetString() == "response" && record.Value.GetProperty("id").GetString() == id)
                { Equal(true, record.Value.GetProperty("success").GetBoolean()); return record; }
            }
        }
        internal async Task<int> Finish() { connection.CompleteInput(); return await Completion; }
        public async ValueTask DisposeAsync()
        {
            connection.CompleteInput(); if (!Completion.IsCompleted) deadline.Cancel();
            try { await Completion; } finally { await connection.DisposeAsync(); deadline.Dispose(); Error.Dispose(); }
        }
    }
}
