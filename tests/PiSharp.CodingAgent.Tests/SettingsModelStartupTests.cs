using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Settings;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class SettingsModelStartupTests
{
    public const string Prefix = "settings model startup ";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        (Prefix + "merged defaults and individual CLI identity overrides use pinned catalogs", Catalog),
        (Prefix + "thinking uses per-model global CLI precedence and actual non-off capabilities", Thinking),
        (Prefix + "actual RPC startup applies settings and clamps off for reasoning model without sends", Wired),
        (Prefix + "actual RPC startup CLI overrides settings model and thinking", Overrides),
        (Prefix + "durable resume preserves recorded thinking until explicit CLI override", Resume),
        (Prefix + "response wait observes missing-session host completion without deadline cancellation", MissingSession),
        (Prefix + "invalid selections reject without HTTP and invalid CLI rejects before settings IO", Invalid)
    ];
    private static void Equal<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}."); }
    private static StartupSettingsSnapshot Capture(string json) => new(JsonData.Parse(json),
        PiSharp.Agent.AgentPendingInputMode.OneAtATime, PiSharp.Agent.AgentPendingInputMode.OneAtATime, []);
    private static Task Catalog()
    {
        var settings = Capture("{\"defaultProvider\":\"openai\",\"defaultModel\":\"o3\"}");
        Equal("o3", new SettingsModelSelection(null, null, null).Resolve(settings).Model.Id);
        Equal("gpt-4", new SettingsModelSelection(null, "gpt-4", null).Resolve(settings).Model.Id);
        Equal("o3", new SettingsModelSelection("openai", null, "1024").Resolve(settings).Model.Id);
        Equal("gpt-4", new SettingsModelSelection("openai", "gpt-4", null).Resolve(Capture("{\"defaultProvider\":7,\"defaultModel\":false}")).Model.Id);
        try { _ = new SettingsModelSelection(null, null, null).Resolve(Capture("{\"defaultProvider\":\"openai\",\"defaultModel\":\"missing-model\"}")); }
        catch (LiveSessionException error) when (error.Code == "UnknownLiveModel") { return Task.CompletedTask; }
        throw new InvalidOperationException("Unknown model accepted.");
    }
    private static Task Thinking()
    {
        var model = new ModelDescriptor("o3", "openai-responses", "openai");
        var settings = Capture("{\"defaultThinkingLevel\":\"low\",\"modelThinkingLevels\":{\"openai/o3\":\"high\"}}");
        Equal("high", SettingsModelSelection.Thinking(settings, model, null, false, ["low", "medium", "high"]));
        Equal("medium", SettingsModelSelection.Thinking(settings, model, "medium", false, ["low", "medium", "high"]));
        Equal("low", SettingsModelSelection.Thinking(settings, model, "off", false, ["low", "medium", "high"]));
        Equal("medium", SettingsModelSelection.Thinking(Capture("{}"), model, null, false, ["low", "medium", "high"]));
        Equal("low", SettingsModelSelection.Thinking(Capture("{\"defaultThinkingLevel\":\"low\"}"), model, null, false, ["low", "medium", "high"]));
        Equal<string?>(null, SettingsModelSelection.Thinking(settings, model, null, true, ["low", "medium", "high"]));
        Equal("high", SettingsModelSelection.Thinking(settings, model, "max", true, ["low", "medium", "high"]));
        Equal("off", SettingsModelSelection.Thinking(settings, model, null, false, ["off"]));
        Equal("low", SettingsModelSelection.Clamp("unknown", ["low", "medium", "high"]));
        return Task.CompletedTask;
    }
    private static async Task Wired()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.User, "{\"defaultProvider\":\"openai\",\"defaultModel\":\"gpt-4\",\"defaultThinkingLevel\":\"high\"}");
        File.WriteAllText(fixture.Project, "{\"defaultModel\":\"o3\",\"modelThinkingLevels\":{\"openai/o3\":\"off\"}}");
        await using var host = new Host(fixture, ["--user-settings", fixture.User, "--project-settings", fixture.Project]);
        var state = (await host.Response("state", "get_state")).Value.GetProperty("data");
        Equal("o3", state.GetProperty("model").GetProperty("id").GetString());
        Equal("low", state.GetProperty("thinkingLevel").GetString());
        var levels = (await host.Response("levels", "get_available_thinking_levels")).Value.GetProperty("data").GetProperty("levels");
        Equal("low|medium|high", string.Join('|', levels.EnumerateArray().Select(level => level.GetString())));
        Equal(0, await host.Finish()); Equal("", host.Error.ToString()); Equal(0, fixture.Handler.Sends);
    }
    private static async Task Overrides()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.User, "{\"defaultProvider\":\"openai\",\"defaultModel\":\"o3\",\"defaultThinkingLevel\":\"low\"}");
        // Pi 1.1.0 resolveCliModel: a bare "gpt-4" is ambiguous (azure, openai) when both are authenticated, so the provider is explicit.
        await using var host = new Host(fixture, ["--user-settings", fixture.User, "--provider", "openai", "--model", "gpt-4", "--thinking", "high"]);
        var state = (await host.Response("state", "get_state")).Value.GetProperty("data");
        Equal("gpt-4", state.GetProperty("model").GetProperty("id").GetString());
        Equal("off", state.GetProperty("thinkingLevel").GetString());
        Equal(0, await host.Finish()); Equal(0, fixture.Handler.Sends);
    }
    private static async Task Invalid()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.User, "{\"defaultProvider\":\"openai\",\"defaultModel\":\"unknown\"}");
        // Pi 1.1.0 findInitialModel: a saved default that does not exist falls back to the first available model.
        await using (var host = new Host(fixture, ["--user-settings", fixture.User]))
        {
            var state = (await host.Response("state", "get_state")).Value.GetProperty("data");
            Equal(true, state.GetProperty("model").GetProperty("id").GetString() is { Length: > 0 } id && id != "unknown");
            Equal(0, await host.Finish()); Equal(false, host.Error.ToString().Contains("UnknownLiveModel", StringComparison.Ordinal));
        }
        var reads = fixture.CredentialReads;
        // An unreadable selected path would be diagnosed if the host got as far as settings IO.
        await using (var host = new Host(fixture, ["--user-settings", fixture.Root, "--thinking", "invalid"]))
        { Equal(2, await host.Completion); Equal(false, host.Error.ToString().Contains("settings_diagnostic", StringComparison.Ordinal)); }
        Equal(0, fixture.Handler.Sends); Equal(reads, fixture.CredentialReads);
    }
    private static async Task Resume()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.User, "{\"defaultProvider\":\"openai\",\"defaultModel\":\"o3\",\"defaultThinkingLevel\":\"high\"}");
        await using (var created = new Host(fixture, ["--user-settings", fixture.User], "new-lazy"))
        {
            Equal("high", (await created.Response("first", "get_state")).Value.GetProperty("data").GetProperty("thinkingLevel").GetString());
            Equal(0, await created.Finish());
        }
        // Setup and queries do not checkpoint conversation: this lazy backend intentionally creates no disk file.
        Equal(false, File.Exists(fixture.Session));
        // Resume tests require an independently durable input, not the retired host's volatile setup checkpoint.
        await fixture.SeedDurableSession();
        Equal(true, File.Exists(fixture.Session));
        File.WriteAllText(fixture.User, "{\"defaultProvider\":\"openai\",\"defaultModel\":\"o3\",\"defaultThinkingLevel\":\"low\"}");
        await using (var resumed = new Host(fixture, ["--user-settings", fixture.User], "open"))
        {
            Equal("high", (await resumed.Response("resumed", "get_state")).Value.GetProperty("data").GetProperty("thinkingLevel").GetString());
            Equal(0, await resumed.Finish());
        }
        await using (var overridden = new Host(fixture, ["--user-settings", fixture.User, "--thinking", "off"], "open"))
        {
            Equal("low", (await overridden.Response("override", "get_state")).Value.GetProperty("data").GetProperty("thinkingLevel").GetString());
            Equal(0, await overridden.Finish());
        }
        Equal(0, fixture.Handler.Sends);
    }
    private static async Task MissingSession()
    {
        using var fixture = new Fixture();
        await using var host = new Host(fixture, ["--provider", "openai", "--model", "o3"], "open");
        try { await host.Response("missing", "get_state"); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("RPC host completed before missing/get_state", StringComparison.Ordinal))
        {
            Equal(true, host.Completion.IsCompletedSuccessfully);
            Equal(false, host.DeadlineExpired); Equal(false, File.Exists(fixture.Session)); Equal(0, fixture.Handler.Sends);
            if (await host.Completion == 0) throw new InvalidOperationException("Missing session host succeeded.");
            return;
        }
        throw new InvalidOperationException("Missing session returned a response or waited until cancellation.");
    }
    private sealed class NoSendHandler : HttpMessageHandler
    {
        internal int Sends;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Interlocked.Increment(ref Sends); throw new InvalidOperationException("Startup preference fixture must not send HTTP."); }
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-settings-model-owned-" + Guid.NewGuid().ToString("N"));
        internal string User => Path.Combine(Root, "user.json"); internal string Project => Path.Combine(Root, "project.json");
        internal string Session => Path.Combine(Root, "session.jsonl");
        internal NoSendHandler Handler { get; } = new(); internal int CredentialReads;
        internal Fixture() => Directory.CreateDirectory(Root);
        internal LiveSessionRuntime Runtime => new(_ => { CredentialReads++; return "inert-offline-fixture-key"; }, () => Handler);
        internal async Task SeedDurableSession()
        {
            var codec = new SessionEntryCodec(); const string timestamp = "2026-10-05T00:00:00.000Z";
            var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = "settings-model-resume", timestamp, cwd = Root }));
            await using var store = await SessionLogStore.CreateNewAsync(Session, header);
            await store.AppendAsync([
                codec.Parse(JsonSerializer.Serialize(new { type = "model_change", id = "model", parentId = (string?)null,
                    timestamp, provider = "openai", modelId = "o3" })),
                codec.Parse(JsonSerializer.Serialize(new { type = "thinking_level_change", id = "thinking", parentId = "model",
                    timestamp, thinkingLevel = "high" }))]);
        }
        public void Dispose()
        {
            Handler.Dispose(); var target = Path.GetFullPath(Root);
            if (!target.StartsWith(Path.GetFullPath(Path.GetTempPath()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                !Path.GetFileName(target).StartsWith("pisharp-settings-model-owned-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid owned root.");
            Directory.Delete(target, true);
        }
    }
    private sealed class Host : IAsyncDisposable
    {
        private readonly Channel<JsonData> records = Channel.CreateUnbounded<JsonData>();
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        private readonly BoundedRpcConnection connection;
        internal StringWriter Error { get; } = new(); internal Task<int> Completion { get; }
        internal bool DeadlineExpired => deadline.IsCancellationRequested;
        internal Host(Fixture fixture, string[] flags, string mode = "new-memory")
        {
            connection = new((record, _) => { records.Writer.TryWrite(record); return ValueTask.CompletedTask; });
            string[] args = ["session", "rpc", "--session", fixture.Session, "--workspace", fixture.Root,
                "--live", "--session-mode", mode, .. flags];
            Completion = Run();
            async Task<int> Run()
            {
                try
                {
                    return await RpcSessionCommand.RunWithPresentationAsync(args, connection.Input, connection.Output, Error,
                        null!, deadline.Token, liveRuntime: fixture.Runtime);
                }
                finally { records.Writer.TryComplete(); }
            }
        }
        internal async Task<JsonData> Response(string id, string type)
        {
            await connection.SendAsync(JsonData.Parse(JsonSerializer.Serialize(new { id, type })), deadline.Token);
            while (true)
            {
                JsonData record;
                try { record = await records.Reader.ReadAsync(deadline.Token); }
                catch (ChannelClosedException)
                {
                    var exit = await Completion; // Join the original host before reporting startup/run failure.
                    throw new InvalidOperationException($"RPC host completed before {id}/{type}; exit {exit}; {Error}");
                }
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
