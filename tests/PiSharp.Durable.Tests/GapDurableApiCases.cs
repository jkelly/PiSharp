// Authored regressions for candidate upstream 922c3ff2, 9ca15920 and dbc61d64.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Contracts;
using static ChatTests;
using static HarnessTests;
using static SessionTests;

internal static partial class Program
{
    private static readonly DurableContext GapContext = DurableContext.Background;

    private static IEnumerable<(string, Func<Task>)> GapDurableApiCases()
    {
        yield return Case("gap.durable.native-google-thinking-budget", async () =>
        {
            var handler = new RecordingHandler(_ => Sse("data: {\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":2,\"candidatesTokenCount\":1,\"totalTokenCount\":3}}\n\n"));
            using var models = Models(new() { ["GEMINI_API_KEY"] = "test-key" }, handler);
            var model = models.GetModel("google", "gemini-2.5-flash")!;
            var answer = await models.CompleteSimpleAsync(model, UserTurn("hi"), new DurableStreamOptions
            { Reasoning = "high", ThinkingBudgets = O("""{"high":2345}""") }, CancellationToken.None);
            Equal(StopReason.Stop, answer.StopReason, "Google request succeeds");
            Equal(2345, handler.Requests.Single().Json.GetProperty("generationConfig").GetProperty("thinkingConfig").GetProperty("thinkingBudget").GetInt32(), "Google wire budget");
        });
        yield return Case("gap.durable.native-thinking-budget", async () =>
        {
            var handler = new RecordingHandler(_ => Sse(AnthropicText));
            using var models = Models(new() { ["ANTHROPIC_API_KEY"] = "test-key" }, handler);
            var model = models.GetModel("anthropic", "claude-sonnet-4-5")!;
            var answer = await models.CompleteSimpleAsync(model, UserTurn("hi"), new DurableStreamOptions
            { Reasoning = "high", MaxTokens = 8000, ThinkingBudgets = O("""{"high":4321}""") }, CancellationToken.None);
            Equal(StopReason.Stop, answer.StopReason, "native request succeeds");
            Equal(4321, handler.Requests.Single().Json.GetProperty("thinking").GetProperty("budget_tokens").GetInt32(), "wire budget");
            Equal(12321, handler.Requests.Single().Json.GetProperty("max_tokens").GetInt32(), "budget expands output cap");
        });
        yield return Case("gap.durable.compaction-precedence", () =>
        {
            var settings = Agents.ResolveSettings(new HarnessSettings { Compaction = new()
            {
                Enabled = true, ReserveTokens = 10, KeepRecentTokens = 20, BackgroundTokens = 30,
                ModelOverrides = new Dictionary<string, CompactionOverride> { ["p/m"] = new() { Enabled = true, ReserveTokens = 40 } }
            }});
            var local = new CompactionSettings { Enabled = false, ReserveTokens = 50, KeepRecentTokens = 60,
                ModelOverrides = new Dictionary<string, CompactionOverride> { ["p/m"] = new() { BackgroundTokens = 70 } } };
            Equal(new CompactionPolicy(false, 40, 60, 70), Compaction.PolicyFor(settings, new("p", "m"), local), "precedence and explicit false");
            Equal(new CompactionPolicy(false, 50, 60, 30), Compaction.PolicyFor(settings, new("p", "other"), local), "unmatched override");
            local = local with { ModelOverrides = new Dictionary<string, CompactionOverride> { ["p/m"] = new() { Enabled = true, ReserveTokens = 80 } } };
            Equal(new CompactionPolicy(true, 80, 60, 30), Compaction.PolicyFor(settings, new("p", "m"), local), "conversation model can explicitly enable");
        });
        yield return Case("gap.durable.compaction-reopen-inherit-replace-clear", async () =>
        {
            var path = Temp("gap-agent");
            async Task<IHarness> Open() => (await OpenHarness(await JsonlStorage.OpenAsync(path, GapContext))).Harness;
            var harness = await Open();
            var overrides = new Dictionary<string, CompactionOverride> { ["p/m"] = new() { ReserveTokens = 42 } };
            var root = await harness.RootAsync(GapContext, new() { Compaction = new CompactionSettings { Enabled = false, ModelOverrides = overrides } });
            overrides["p/m"] = new() { ReserveTokens = 99 };
            await root.ConfigureAsync(new() { Cwd = "unchanged-policy" }, GapContext);
            Equal(42d, (await root.AgentAsync(GapContext)).Compaction!.ModelOverrides!["p/m"].ReserveTokens, "stored copy");
            var entry = await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "message", Model = JsonValue.Array(User("fork point")) }), GapContext);
            var fork = await root.ForkAsync(entry.Id, new(ConversationOwnership.Ownerless), GapContext);
            Equal(false, (await fork.AgentAsync(GapContext)).Compaction!.Enabled, "fork inherits");
            var owner = Tasks.DefineTask(new TaskDefinition
            {
                Name = "gap.owner", Version = 1, Initial = _ => O("""{"phase":"never"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler> { ["never"] = (_, _, _) => Task.CompletedTask },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            await root.CommitAsync(async tx =>
            {
                var task = await tx.CreateTaskAsync(owner, JsonObjectValue.Empty, new TaskOptions(TaskOwnership.Conversation));
                var child = await tx.CreateConversationAsync(ConversationOwnership.Task(task));
                var childState = (await tx.DocAsync(Agents.AgentDoc, child.Id)).ToValue();
                MatchJson("""{"compaction":{"enabled":false,"modelOverrides":{"p/m":{"reserveTokens":42}}}}""", childState, "task-owned child inherits");
            }, GapContext);
            await harness.CloseAsync(GapContext);
            harness = await Open(); root = await harness.RootAsync(GapContext);
            Equal(false, (await root.AgentAsync(GapContext)).Compaction!.Enabled, "reopened explicit false");
            await root.ConfigureAsync(new() { Compaction = new CompactionSettings { KeepRecentTokens = 7 } }, GapContext);
            var replaced = (await root.AgentAsync(GapContext)).Compaction!;
            Check(replaced.Enabled is null && replaced.ModelOverrides is null, "replacement removes old fields");
            Equal(7d, replaced.KeepRecentTokens, "replacement field");
            foreach (var invalid in new[] { -1d, 1.5, double.NaN, double.PositiveInfinity, 9007199254740992d })
                await ThrowsAsync<ArgumentOutOfRangeException>(() => root.ConfigureAsync(new() { Compaction = new CompactionSettings
                { ModelOverrides = new Dictionary<string, CompactionOverride> { ["p/m"] = new() { BackgroundTokens = invalid } } } }, GapContext), "invalid override");
            Equal(7d, (await root.AgentAsync(GapContext)).Compaction!.KeepRecentTokens, "rejected writes preserve policy");
            await root.ConfigureAsync(new() { Compaction = Patch<CompactionSettings>.Clear }, GapContext);
            Check((await root.AgentAsync(GapContext)).Compaction is null, "clear restores inheritance");
            await harness.CloseAsync(GapContext);
        });
        yield return Case("gap.durable.public-transaction-submission", async () =>
        {
            var session = Session.Create(new MemoryStorage());
            var conversation = await session.CommitAsync(tx => tx.CreateConversationAsync(ConversationOwnership.Ownerless), GapContext);
            var record = await session.CommitAsync(tx => tx.CreateSubmissionAsync(new()
            { ConversationId = conversation.Id, Type = SubmissionType.Input, Status = SubmissionStatus.Queued, RequestId = "gap" }), GapContext);
            ITx? saved = null;
            await session.CommitAsync(async tx =>
            {
                saved = tx;
                Equal(record, await tx.GetSubmissionAsync(record.Id), "public committed lookup");
                Check(await tx.GetSubmissionAsync(new SubmissionId(99999)) is null, "missing submission");
                await tx.AppendEntryAsync(conversation.Id, new EntryDraft { Kind = "message", Model = JsonValue.Array(User("write")) });
                await ThrowsAsync<ReadAfterWriteException>(() => tx.GetSubmissionAsync(record.Id), "read after write");
            }, GapContext);
            await ThrowsAsync<InvalidOperationException>(() => saved!.GetSubmissionAsync(record.Id), "settled transaction");
            await session.CloseAsync(GapContext);
        });
        yield return Case("gap.durable.stream-options-reopen-pinned", async () =>
        {
            var path = Temp("gap-options");
            var setup = Setup(); var reached = new HarnessTests.Deferred(); DurableStreamOptions? replayed = null;
            setup.Stream = new() { WebsocketConnectTimeoutMs = 321, ThinkingBudgets = O("""{"high":5432,"low":1024}""") };
            setup.Faux.SetResponses(new FauxResponseFactory(async request => { reached.Resolve(); await Aborted(request.Signal); throw new InvalidOperationException(); }),
                FauxStep.From(request => { replayed = request.Options; return FauxAssistantMessage("answer"); }));
            var opened = await OpenChat(await JsonlStorage.OpenAsync(path, GapContext), setup);
            opened.Harness.Resume();
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("hi"), GapContext)).Id;
            await reached.Task;
            await opened.Harness.CloseAsync(GapContext);
            setup.Stream = new() { WebsocketConnectTimeoutMs = 999, ThinkingBudgets = O("""{"high":9999}""") };
            opened = await OpenChat(await JsonlStorage.OpenAsync(path, GapContext), setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, GapContext))!.WaitAsync(GapContext)).Status, "replayed submission");
            Equal(321d, replayed?.WebsocketConnectTimeoutMs, "pinned handshake");
            SameJson("""{"high":5432,"low":1024}""", replayed?.ThinkingBudgets, "pinned budgets");
            await opened.Harness.CloseAsync(GapContext);
        });
        foreach (var reason in new[] { "threshold", "background", "overflow" })
            yield return Case("gap.durable.compaction-disabled-" + reason, async () =>
            {
                var setup = Setup(new FauxOptions { ContextWindow = 1000 });
                setup.Compaction = new() { Enabled = false, ReserveTokens = 300, KeepRecentTokens = 50, BackgroundTokens = reason == "background" ? 500 : 0 };
                var opened = await OpenChat(new MemoryStorage(), setup); opened.Harness.Resume();
                for (var i = 0; i < 3; i++)
                {
                    setup.Faux.SetResponses(FauxAssistantMessage(CompactionTests.Text("answer", 100)));
                    var input = await opened.Root.SubmitAsync(SubmissionDraft.Input(CompactionTests.Text("user", 100)), GapContext);
                    Equal(SubmissionStatus.Done, (await input.WaitAsync(GapContext)).Status, "history");
                }
                setup.Compaction = setup.Compaction with { Enabled = true };
                await opened.Root.ConfigureAsync(new() { Compaction = new CompactionSettings { Enabled = false } }, GapContext);
                setup.Faux.SetResponses(reason == "overflow" ? FauxAssistantMessage("", "error", errorMessage: "prompt is too long: 250000 tokens > 1000 maximum") : FauxAssistantMessage("done"));
                var next = await opened.Root.SubmitAsync(SubmissionDraft.Input(CompactionTests.Text("more", reason == "background" ? 20 : 200)), GapContext);
                await next.WaitAsync(GapContext);
                var tasks = await opened.Harness.InspectAsync(GapContext);
                Check(tasks.Tasks.All(task => task.Record.Kind != "pi.compaction"), "disabled policy prevents automatic task");
                if (reason == "threshold")
                {
                    AddHooks(setup.Registry, HarnessTasks.CompactionTask, new CompactionHooks
                    { BeforeCompact = (_, _, _) => Task.FromResult<CompactionDecision?>(CompactionDecision.FromSummary("manual summary")) });
                    var manual = await opened.Root.CompactAsync(null, GapContext);
                    MatchJson("""{"status":"completed"}""", OutcomeJson(await opened.Harness.WaitForTaskAsync(manual, GapContext)), "manual compaction ignores disabled");
                }
                await opened.Harness.CloseAsync(GapContext);
            });
    }
}
