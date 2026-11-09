// Candidate stream-option regressions use only fake HTTP, credentials and temporary agent directories.
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Pi;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage.Jsonl;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> GapDurableHostCases()
    {
        yield return Case("gap.durable-host.configured-timeout-pinned-on-reopen", async () =>
        {
            var settings = PiSettings.FromObjects(new System.Text.Json.Nodes.JsonObject());
            var bridge = DurableHarnessSetup.CreateHarnessSettings(settings);
            Check(bridge.Stream!.WebsocketConnectTimeoutMs is null, "absent setting does not change checkpoints");
            settings.SetField("global", "websocketConnectTimeoutMs", System.Text.Json.Nodes.JsonValue.Create(123));
            var models = new GapPinnedModels();
            var context = DurableContext.Background;
            var path = Temp("configured-pinned-timeout");
            async Task<IHarness> Open() => await Harness.OpenAsync(await JsonlStorage.OpenAsync(path, context),
                new HarnessOptions { Models = models, Registry = Registry.Create(), Settings = bridge }, context);
            var harness = await Open();
            var root = await harness.RootAsync(context, new() { Model = new ModelRef("fake", "m") });
            var first = await root.SubmitAsync(SubmissionDraft.Input("first"), context);
            await models.Reached.Task;
            await harness.CloseAsync(context);
            settings.SetField("global", "websocketConnectTimeoutMs", System.Text.Json.Nodes.JsonValue.Create(456));
            models.Block = false;
            harness = await Open(); root = await harness.RootAsync(context);
            await (await harness.SubmissionAsync(first.Id, context))!.WaitAsync(context);
            await (await root.SubmitAsync(SubmissionDraft.Input("next"), context)).WaitAsync(context);
            Check(models.Timeouts.SequenceEqual(new double?[] { 123, 123, 456 }), "in-flight timeout is pinned; next preparation uses changed global setting");
            await harness.CloseAsync(context);
        });
        yield return Case("gap.durable-host.thinking-budgets-main-summary", async () =>
        {
            await using var agent = new TestAgent("budget", models: """
                {"providers":{"lab":{"baseUrl":"http://127.0.0.1:8123/v1","apiKey":"test-key","api":"openai-completions",
                "models":[{"id":"budget-model","reasoning":true,"input":["text"],"contextWindow":32768,"maxTokens":8192,
                "compat":{"supportsThinkingTokenBudget":true}}]}}}
                """);
            var endpoint = new FakeEndpoint();
            await using var models = await DurableModels.CreateAsync(agent.Runtime(() => endpoint));
            var model = models.GetModel("lab", "budget-model")!;
            foreach (var (summary, customBudget) in new[] { (false, true), (true, true), (false, false) })
            {
                var options = new DurableStreamOptions { Reasoning = "high",
                    ThinkingBudgets = customBudget ? (JsonObjectValue)JsonValue.Parse("""{"high":2345}""") : null,
                    WebsocketConnectTimeoutMs = customBudget ? null : 300,
                    CacheRetention = summary ? "none" : "short", MaxTokens = summary ? 5000 : null };
                var answer = await models.CompleteSimpleAsync(model, [JsonValue.Parse("""{"role":"user","content":"hi","timestamp":1}""")], options, CancellationToken.None);
                Equal(StopReason.Stop, answer.StopReason, "host response");
                using var body = JsonDocument.Parse(endpoint.Snapshot()[^1].Body!);
                Equal(customBudget ? 2345 : 7168, body.RootElement.GetProperty("thinking_token_budget").GetInt32(), "host reasoning survives options wrapper");
            }
        });
        yield return Case("gap.durable-host.websocket-handshake-timeout", async () =>
        {
            await using var agent = new TestAgent("handshake");
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"https://api.openai.com/auth":{"chatgpt_account_id":"acct-test"}}""")).TrimEnd('=').Replace('+','-').Replace('/','_');
            var token = "eyJhbGciOiJub25lIn0." + payload + ".fake";
            await File.WriteAllTextAsync(Path.Combine(agent.AgentDir, "auth.json"), $$$"""{"openai-codex":{"type":"oauth","refresh":"fake","access":"{{{token}}}","expires":4000000000000,"accountId":"acct-test"}}""");
            var endpoint = new GapHandshakeEndpoint();
            await using var models = await DurableModels.CreateAsync(agent.Runtime(() => endpoint));
            var model = models.GetAvailableSnapshot().First(item => item.Provider == "openai-codex");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await models.CompleteSimpleAsync(model, [JsonValue.Parse("""{"role":"user","content":"hi","timestamp":1}""")],
                new DurableStreamOptions { WebsocketConnectTimeoutMs = 30 }, deadline.Token);
            Check(endpoint.HandshakeCanceled, "configured handshake deadline cancels stalled upgrade");
            Check(endpoint.Elapsed < TimeSpan.FromSeconds(3), "deadline uses request setting rather than provider default");
        });
    }

    private sealed class GapPinnedModels : IDurableModels
    {
        internal bool Block { get; set; } = true;
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<double?> Timeouts { get; } = [];
        public DurableModel? GetModel(string provider, string id) => new() { Provider = provider, Id = id, Name = id, Api = "fake", ContextWindow = 128000, MaxTokens = 4096 };
        public async IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            Timeouts.Add(options?.WebsocketConnectTimeoutMs);
            if (Block) { Reached.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            yield return new StreamDone(StopReason.Stop, new AssistantMessage("fake", model.Provider, model.Id, 1, [new TextContent("done")], TokenUsage.Zero, StopReason.Stop));
        }
        public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<JsonValue> messages, DurableStreamOptions? options, CancellationToken token) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken token) => throw new NotSupportedException();
        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken token) => throw new NotSupportedException();
        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class GapHandshakeEndpoint : HttpMessageHandler
    {
        private readonly System.Diagnostics.Stopwatch clock = new();
        internal bool HandshakeCanceled { get; private set; }
        internal TimeSpan Elapsed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Upgrade.Any(value => value.Name.Equals("websocket", StringComparison.OrdinalIgnoreCase)))
            {
                clock.Restart();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { HandshakeCanceled = true; Elapsed = clock.Elapsed; throw; }
            }
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"message":"offline handshake probe"}}""", Encoding.UTF8, "application/json") };
        }
    }
}
