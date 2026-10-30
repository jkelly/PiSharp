// Decision 0011 (run W4, R0): the run engine. In process, over a temporary data folder: a Playground-style run on the priced faux models
// (--faux-metered) records one runs.db row with its usage, cost (the ledger row the Spend Guard settled) and events; options the model does
// not take are refused before anything is reserved or sent; a Block cap refuses the run with the confirm modal's answer; a recovered chat
// call (the same op after a restart) is one row. With real routing over a fake provider (ProviderFake: no request leaves the process) the
// run's sampling options and headers reach the request body, raw payloads are off by default, and when they are on the planted key is
// never in runs.db. End to end, the built app streams a run over SSE with catch-up, keeps it going when the tab closes, records a chat's
// calls, and after a kill marks the running run interrupted and never retries it.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;
using PiSharp.WebAgent.Runs;
using PiSharp.WebAgent.Spend;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> RunEngineCases() =>
    [
        Case("runs.engine.playground-run-records-one-row-with-usage-and-cost", async () =>
        {
            await using var host = RunsHost.Faux("runs-engine-playground");
            Check(host.Models is RecordingDurableModels { Inner: GuardedDurableModels { Inner: MeteredFauxDurableModels } },
                "the recorder wraps the Spend Guard over the priced faux models: " + host.Models.GetType().Name);
            Check(host.Engine.Models is MeteredFauxDurableModels, "the engine streams through the unguarded models, under its own lease");

            var start = await host.Engine.StartAsync(new RunSpec
            {
                Provider = "faux",
                Model = "echo",
                Title = "First run",
                SystemPrompt = "Be brief.",
                Temperature = 0.2,
                MaxTokens = 200,
                Messages = [new RunMessage { Text = "hello" }],
            });
            Check(start is { Posted: true, Refusal: null, Id: not null } && start.Ids.Count == 1, "posted");
            var run = await RunsFinishedAsync(host.Store, start.Id!);
            Equal(RunStatus.Completed, run.Status, "completed: " + run.Error);
            Equal(("call", "playground", "faux", "echo", "First run"), (run.Kind, run.Source, run.Provider, run.Model, run.Title), "the row");
            Equal("run-" + run.Id, run.Op, "keyed by its op");
            Equal("Echo: hello", run.OutputText, "the answer");
            Check(run.Usage is { Input: > 0, Output: > 0 } && run.Usage.Total >= run.Usage.Input + run.Usage.Output, "usage: " + run.Usage);
            Check(run is { CostMicros: > 0, CostKind: "actual", Billing: "metered", TtftMs: not null, DurationMs: not null }, "cost and timings: " + run);
            var ledger = (await host.Services.GetRequiredService<ISpendLedger>().GetAsync(run.SpendOp!))!;
            Check(ledger is { State: ReservationState.Settled, Settlement: SettlementKind.Actual, Source: SpendSource.Playground } && ledger.ActualMicros == run.CostMicros,
                "the cost is the ledger row the guard settled: " + ledger);
            Equal(run.Id, ledger.Run, "under the run's id");
            Check(run.Options?["temperature"]?.GetValue<double>() == 0.2 && run.Options["maxTokens"]?.GetValue<int>() == 200, "the options are kept: " + run.Options);
            Check(run.Options?["applied"]?.AsArray().Select(item => item!.GetValue<string>()).Contains("temperature") == true, "and what was applied");
            Check(run.Messages?["systemPrompt"]?.GetValue<string>() == "Be brief." && run.Messages["messages"]?.AsArray().Count == 1, "the system prompt and the message: " + run.Messages);
            var events = host.Store.Events(run.Id);
            Check(events.Count >= 3 && events.Select(item => item.Seq).SequenceEqual(Enumerable.Range(1, events.Count).Select(seq => (long)seq)), "its events in order");
            Equal("done", events[^1].Event["type"]?.GetValue<string>(), "ending with done");
            Equal(1L, host.Store.Count(), "one row: the recorder did not record the engine's call again");
            Equal(RunStatus.Completed, host.Services.GetRequiredService<RunLive>().Find(run.Id)?.Final?["status"]?.GetValue<string>(), "the live log ended with the status");
        }),

        Case("runs.engine.refused-options-and-spend-send-nothing", async () =>
        {
            await using var host = RunsHost.Faux("runs-engine-refused");
            var plan = host.Engine.Plan(new RunSpec { Provider = "faux", Model = "echo", Headers = new Dictionary<string, string> { ["x-trace"] = "1" }, Messages = [new RunMessage { Text = "hi" }] });
            Check(!plan.Allowed && plan.Refused.Any(item => item.Option == RunModelOptions.Headers), "the faux models take no headers");
            var error = await ThrowsAsync<RunOptionException>(() => host.Engine.StartAsync(plan.Spec), "a refused option fails the start");
            Check(error.Message.Contains("header", StringComparison.OrdinalIgnoreCase), "it says which: " + error.Message);
            Throws<ArgumentException>(() => RunSpec.Parse(JsonNode.Parse("""{"provider":"faux","model":"echo","messages":[{"text":"x"}],"headers":{"Authorization":"x"}}""")!).Normalize(),
                "a credential header is refused");
            Throws<ArgumentException>(() => RunSpec.Parse(JsonNode.Parse("""{"provider":"faux","model":"echo","messages":[{"text":"x"}],"source":"chat"}""")!).Normalize(),
                "a run is never a chat");
            Throws<ArgumentException>(() => host.Engine.Plan(new RunSpec { Provider = "faux", Model = "no-such-model", Messages = [new RunMessage { Text = "x" }] }), "an unknown model");
            Equal(0L, host.Store.Count(), "nothing recorded");

            await host.Services.GetRequiredService<ISpendLedger>().SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, 1, CapAction.Block));
            var blocked = await host.Engine.StartAsync(new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Text = "too dear" }] });
            Check(blocked is { Posted: false, Refusal.Code: SpendErrors.Blocked }, "a Block cap refuses the run: " + blocked.Refusal?.Code);
            Check(blocked.Refusal!.Message.Length > 0 && blocked.Refusal.Decision.Causes.Count > 0, "with the guard's decision for the confirm modal");
            Equal(0L, host.Store.Count(), "a refused run is not recorded");
            Equal(0, ReadDb(Path.Join(host.Options.DataDirectory, "webagent.db"), "SELECT COUNT(*) FROM spend_reservations", reader => reader.GetInt32(0)).Single(), "nothing reserved");
        }),

        Case("runs.engine.a-compare-group-records-each-column-and-a-cancel-aborts", async () =>
        {
            await using var host = RunsHost.Faux("runs-engine-group");
            var group = await host.Engine.StartGroupAsync(
            [
                new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Text = "left" }] },
                new RunSpec { Provider = "faux", Model = "echo-small", Messages = [new RunMessage { Text = "right" }] },
            ]);
            Check(group is { Posted: true, GroupId: not null } && group.Ids.Count == 2, "both columns posted");
            var columns = new List<RunRecord>();
            foreach (var id in group.Ids) columns.Add(await RunsFinishedAsync(host.Store, id));
            Check(columns.All(run => run is { Status: RunStatus.Completed, Source: "compare", CostMicros: > 0 } && run.GroupId == group.GroupId),
                "each column is a row of the group: " + string.Join(" | ", columns));
            Equal("Echo: left|Echo: right", string.Join("|", columns.Select(run => run.OutputText)), "each its own answer");
            Equal(2, host.Store.List(new RunQuery { GroupId = group.GroupId }).Items.Count, "the group's rows");
            var ledger = host.Services.GetRequiredService<ISpendLedger>();
            foreach (var run in columns) Equal(group.GroupId, (await ledger.GetAsync(run.SpendOp!))!.Run, "reserved under the group");
            await ThrowsAsync<ArgumentException>(() => host.Engine.StartGroupAsync([]), "a group has a column");

            var slow = await host.Engine.StartAsync(new RunSpec { Provider = "faux", Model = "echo", Messages = [new RunMessage { Text = "slow: " + string.Join(" ", Enumerable.Range(1, 200)) }] });
            await WaitUntilAsync(() => Task.FromResult(host.Services.GetRequiredService<RunLive>().Find(slow.Id!)?.Items.Count > 2), "the slow run streams");
            Check(host.Engine.Cancel(slow.Id!), "cancelled");
            var aborted = await RunsFinishedAsync(host.Store, slow.Id!);
            Equal(RunStatus.Aborted, aborted.Status, "a cancelled run is aborted: " + aborted.Error);
            Check(aborted.OutputText?.StartsWith("1", StringComparison.Ordinal) == true && aborted.OutputText.Length < 200 * 4, "with what it had answered: " + aborted.OutputText);
            Check(!host.Engine.Cancel(slow.Id!), "only once");
        }),

        Case("runs.recorder.a-recovered-chat-call-is-one-row", async () =>
        {
            var root = Temp("runs-recorder-recovered");
            try
            {
                var data = Path.Join(root, "data");
                string first;
                await using (var host = RunsHost.Faux(root, data))
                {
                    // The process dies mid-stream: the row stays running, as a kill leaves it.
                    var model = host.Models.GetModel("faux", "echo")!;
                    var stream = host.Models.StreamSimple(model, UserTurn("slow: one two three four five six seven eight"), RunsChatOptions("op-rec", "chat-rec"), CancellationToken.None)
                        .GetAsyncEnumerator();
                    Check(await stream.MoveNextAsync() && await stream.MoveNextAsync(), "the call started");
                    var row = host.Store.GetByOp("op-rec")!;
                    Check(row is { Status: RunStatus.Running, Kind: RunKind.Call, Source: "chat", Session: "chat-rec", MessagesRef: "chat:chat-rec", GroupId: null }, "recorded as a chat call: " + row);
                    first = row.Id;
                }
                SqliteClearPools();
                await using (var host = RunsHost.Faux(root, data))
                {
                    Equal(RunStatus.Interrupted, host.Store.Get(first)?.Status, "a restart marks it interrupted");
                    var model = host.Models.GetModel("faux", "echo")!;
                    var answer = await GuardCollect(host.Models.StreamSimple(model, UserTurn("slow: one two three four five six seven eight"),
                        RunsChatOptions("op-rec", "chat-rec"), CancellationToken.None));
                    Check(answer[^1] is StreamDone, "the recovered request answered");
                    var rows = ReadDb(Path.Join(data, RunsDb.FileName), "SELECT id, status FROM runs WHERE op = 'op-rec'", reader => (Id: reader.GetString(0), Status: reader.GetString(1)));
                    Equal(1, rows.Count, "a recovered call is one row");
                    Equal((first, RunStatus.Completed), rows[0], "the same row, completed");
                    var row = host.Store.Get(first)!;
                    Check(row is { OutputText: "one two three four five six seven eight", Usage.Output: > 0, CostMicros: > 0, Error: null }, "with the recovered answer and its cost: " + row);
                    Equal(row.Id, host.Store.List(new RunQuery { Source = "chat" }).Items.Single().Id, "History lists it once");

                    await GuardCollect(host.Models.StreamSimple(model, UserTurn("hello"), RunsChatOptions("op-next", "chat-rec"), CancellationToken.None));
                    Equal(2L, host.Store.Count(), "another op is another row");
                    Equal("Echo: hello", host.Store.GetByOp("op-next")?.OutputText, "recorded");
                }
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("runs.engine.options-reach-the-provider-and-raw-payloads-are-redacted", async () =>
        {
            await using var host = RunsHost.Routing("runs-engine-routing");
            var key = Planted("sk-proj-");
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", key));
            var model = ModelOf(host.Services.GetRequiredService<IModelCatalog>(), "openai", "openai-responses", "gpt-4.1");
            var options = host.Services.GetRequiredService<RoutingOptions>();
            Check(options.ProviderHandler is RunCaptureHandler { InnerHandler: ProviderFake }, "the capture hook wraps the provider handler the host registered");

            RunSpec Spec(string text) => new()
            {
                Provider = "openai",
                Model = model,
                Temperature = 0.3,
                TopP = 0.9,
                Headers = new Dictionary<string, string> { ["x-run-test"] = "yes" },
                Messages = [new RunMessage { Text = text }],
            };
            var start = await host.Engine.StartAsync(Spec("first run"));
            Check(start.Posted, "posted: " + start.Refusal?.Message);
            var run = await RunsFinishedAsync(host.Store, start.Id!);
            Equal(RunStatus.Completed, run.Status, "completed: " + run.Error);
            Equal("Hello world", run.OutputText, "the provider's answer");
            Equal((12.0, 3.0), (run.Usage.Input, run.Usage.Output), "its usage");
            Check(run is { CostMicros: > 0, CostKind: "actual", Billing: "metered" } && run.Account is not null, "cost and account: " + run);
            var sent = host.Provider.Requests.Single(request => request.Body?.Contains("first run", StringComparison.Ordinal) == true);
            var body = JsonNode.Parse(sent.Body!)!;
            Equal((0.3, 0.9), (body["temperature"]!.GetValue<double>(), body["top_p"]!.GetValue<double>()), "the sampling options reach the request body");
            Equal("yes", sent.Header("x-run-test"), "and the header");
            Check(body["model"]?.GetValue<string>() == model && body["stream"]?.GetValue<bool>() == true, "the request is otherwise the transport's own");
            Equal(RunRawState.None, run.RawState, "raw payloads are off by default");
            Equal(0, host.Store.Raw(run.Id).Count, "none stored");

            var refused = await ThrowsAsync<RunOptionException>(() => host.Engine.StartAsync(Spec("x") with { TopK = 5 }), "the Responses API takes no top K");
            Check(refused.Plan.Refused.Any(item => item.Option == RunModelOptions.TopK), "named: " + refused.Message);
            Equal(1, host.Provider.Requests.Count, "nothing was sent for it");

            await host.Services.GetRequiredService<ISettingsStore>().SaveAsync(new RunHistorySettings(StoreRawPayloads: true));
            var second = await host.Engine.StartAsync(Spec("second run"));
            var raw = await RunsFinishedAsync(host.Store, second.Id!);
            Equal(RunStatus.Completed, raw.Status, "completed: " + raw.Error);
            Equal(RunRawState.Stored, raw.RawState, "raw payloads stored when turned on");
            var items = host.Store.Raw(raw.Id);
            var request = items.First(item => item.Kind == "request");
            Check(request.Data.Contains("second run", StringComparison.Ordinal) && request.Data.Contains(RunRedaction.Redacted, StringComparison.Ordinal),
                "the request with its credential redacted: " + request.Data[..Math.Min(400, request.Data.Length)]);
            Check(items.Count(item => item.Kind == "sse") >= 9 && items.Any(item => item.Kind == "sse" && item.Data.Contains("response.output_text.delta", StringComparison.Ordinal)),
                "each raw SSE line: " + string.Join(",", items.Select(item => item.Kind)));
            Check(items.Any(item => item.Kind == "response") && items.Any(item => item.Kind == "timing" && item.Data.Contains("first-byte", StringComparison.Ordinal)), "the response headers and timing marks");
            NoSecret(string.Join("\n", items.Select(item => item.Data)), "the raw payloads", key);
            NoSecret(RunsFileText(host.Runs.Path), "runs.db", key);
            NoSecret(host.LogText, "the log", key);
        }),

        Case("runs.app.streams-over-sse-records-chats-and-a-restart-marks-a-run-interrupted", async () =>
        {
            var root = Temp("runs-app");
            try
            {
                var data = Path.Join(root, "data");
                Directory.CreateDirectory(data);
                Directory.CreateDirectory(Path.Join(root, "work"));
                var setup = new WebAgentDb(Path.Join(data, "webagent.db"), [CoreMigrations.AppInfo, SettingsStore.Migration]);
                setup.Migrate();
                await new SettingsStore(setup).SaveAsync(new ToolsSettings(Path.Join(root, "work"), ToolApprovalMode.Ask, []));
                SqliteClearPools();
                var start = new AppStart { DataDirectory = data, Arguments = ["--faux-metered"] };
                string slowId;
                await using (var app = await WebAgentApp.StartAsync(root, start))
                {
                    var posted = await app.SendAsync(HttpMethod.Post, "/api/runs", new JsonObject
                    {
                        ["provider"] = "faux",
                        ["model"] = "echo",
                        ["temperature"] = 0.5,
                        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = "hello app" }),
                    });
                    Equal(200, posted.Status, "posted: " + posted.Text);
                    Check(posted.Body!["posted"]!.GetValue<bool>(), "a run: " + posted.Text);
                    Equal("no-store", posted.Response.Headers.CacheControl?.ToString(), "never cached");
                    var id = posted.Body["id"]!.GetValue<string>();
                    Equal($"/api/runs/{id}/events", posted.Body["events"]!.GetValue<string>(), "where its events are");

                    var frames = await RunsSseAsync(app, $"/api/runs/{id}/events");
                    var status = frames[^1];
                    Equal("status", status.Event, "the stream ends with the status");
                    Equal("completed", status.Data!["status"]!.GetValue<string>(), "completed: " + status.Data.ToJsonString());
                    Check(status.Data["costMicros"]!.GetValue<long>() > 0 && status.Data["usage"]!["output"]!.GetValue<double>() > 0, "with its usage and cost");
                    var events = frames.Where(frame => frame.Event == "event").ToList();
                    Check(events.Count >= 3 && events.Select(frame => frame.Id).SequenceEqual(Enumerable.Range(1, events.Count).Select(seq => seq.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                        "each event with its sequence number as the SSE id");
                    Equal("done", events[^1].Data!["event"]!["type"]!.GetValue<string>(), "the last event");
                    var caught = await RunsSseAsync(app, $"/api/runs/{id}/events", lastEventId: "2");
                    Equal("3", caught.First(frame => frame.Event == "event").Id, "Last-Event-ID catches up after it");
                    Equal(404, (await app.Get("/api/runs/no-such-run/events")).Status, "an unknown run");

                    var row = await app.Get($"/api/runs/{id}");
                    Equal(200, row.Status, "the run: " + row.Text);
                    Equal(("completed", "Echo: hello app", false), (row.Body!["run"]!["status"]!.GetValue<string>(), row.Body["run"]!["outputText"]!.GetValue<string>(),
                        row.Body["running"]!.GetValue<bool>()), "stored");

                    var refused = await app.SendAsync(HttpMethod.Post, "/api/runs", new JsonObject
                    {
                        ["provider"] = "faux",
                        ["model"] = "echo",
                        ["headers"] = new JsonObject { ["x-trace"] = "1" },
                        ["messages"] = new JsonArray(new JsonObject { ["text"] = "x" }),
                    });
                    Equal((400, "invalid"), (refused.Status, refused.Body?["error"]?["code"]?.GetValue<string>()), "an option the model does not take: " + refused.Text);
                    Equal("headers", refused.Body!["error"]!["refused"]![0]!["option"]!.GetValue<string>(), "named");
                    Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/runs", new JsonObject { ["provider"] = "faux" }, origin: "http://evil.example")).Status, "no cross-origin run");
                    Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/runs", text: "{}", contentType: "text/plain")).Status, "runs are JSON");

                    var chat = await app.SendAsync(HttpMethod.Post, "/api/chats", new JsonObject { ["requestId"] = "r1", ["text"] = "hello chat" });
                    Equal(200, chat.Status, "chat: " + chat.Text);
                    var chatId = chat.Body!["id"]!.GetValue<string>();
                    await GuardIdleAsync(app, chatId, "Echo: hello chat");
                    var chats = await app.Get("/api/runs?source=chat&totals=true");
                    var calls = chats.Body!["items"]!.AsArray();
                    Check(calls.Count >= 1 && calls.All(call => call!["session"]?.GetValue<string>() == chatId && call["status"]!.GetValue<string>() == "completed" &&
                        call["costMicros"]!.GetValue<long>() > 0), "the chat's calls are recorded with its id and cost: " + chats.Text);
                    var ops = ReadDb(Path.Join(data, "webagent.db"), $"SELECT op FROM spend_reservations WHERE run = '{chatId}'", reader => reader.GetString(0));
                    Check(calls.Select(call => call!["spendOp"]?.GetValue<string>()).All(op => op is not null && ops.Contains(op)), "each names the ledger row of its cost");
                    Equal(calls.Count, chats.Body["totals"]!["count"]!.GetValue<int>(), "the totals count them");

                    var slow = await app.SendAsync(HttpMethod.Post, "/api/runs", new JsonObject
                    {
                        ["provider"] = "faux",
                        ["model"] = "echo",
                        ["messages"] = new JsonArray(new JsonObject { ["text"] = "slow: " + string.Join(" ", Enumerable.Range(1, 400).Select(n => "w" + n)) }),
                    });
                    slowId = slow.Body!["id"]!.GetValue<string>();
                    // A tab watches the first events, then closes: the run goes on.
                    var firstFrames = await RunsSseAsync(app, $"/api/runs/{slowId}/events", take: 3);
                    Equal(3, firstFrames.Count(frame => frame.Event == "event"), "the first events streamed live");
                    var seen = firstFrames.Max(frame => long.Parse(frame.Id!, System.Globalization.CultureInfo.InvariantCulture));
                    await WaitUntilAsync(async () =>
                    {
                        var live = await RunsSseAsync(app, $"/api/runs/{slowId}/events?after={seen + 5}", take: 1);
                        return live.Count == 1 && live[0].Event == "event";
                    }, "the run keeps going after its tab closed");
                    Equal(true, (await app.Get($"/api/runs/{slowId}")).Body!["running"]!.GetValue<bool>(), "still running");
                    await app.StopAsync();
                }
                SqliteClearPools();
                await using (var app = await WebAgentApp.StartAsync(root, start))
                {
                    var row = await app.Get($"/api/runs/{slowId}");
                    Equal(("interrupted", false), (row.Body!["run"]!["status"]!.GetValue<string>(), row.Body["running"]!.GetValue<bool>()), "a restart marks it interrupted: " + row.Text);
                    Equal(RunStore.InterruptedMessage, row.Body["run"]!["error"]!.GetValue<string>(), "and says so");
                    var frames = await RunsSseAsync(app, $"/api/runs/{slowId}/events");
                    Equal(("status", "interrupted"), (frames[^1].Event, frames[^1].Data!["status"]!.GetValue<string>()), "its stream ends at once");
                    await Task.Delay(500);
                    Equal("interrupted", (await app.Get($"/api/runs/{slowId}")).Body!["run"]!["status"]!.GetValue<string>(), "never retried");
                    var storage = await app.Get("/api/runs/storage");
                    Check(storage.Body!["count"]!.GetValue<long>() >= 3 && storage.Body["settings"]!["storeRawPayloads"]!.GetValue<bool>() == false &&
                        storage.Body["settings"]!["retentionDays"]!.GetValue<int>() == 30, "storage and the default settings: " + storage.Text);
                    // A per-request Block cap (written to the app's database directly, as the spend cases do): the run is refused with the
                    // confirm modal's answer, and nothing is recorded or reserved.
                    new WebAgentDb(Path.Join(data, "webagent.db"), []).Execute("""
                        INSERT INTO spend_caps (scope, key, period, limit_micros, action, enabled, created_at, updated_at)
                        VALUES ('everything', NULL, 'request', 1, 'block', 1, 0, 0);
                        """);
                    var before = storage.Body["count"]!.GetValue<long>();
                    var blocked = await app.SendAsync(HttpMethod.Post, "/api/runs", new JsonObject
                    {
                        ["provider"] = "faux",
                        ["model"] = "echo",
                        ["messages"] = new JsonArray(new JsonObject { ["text"] = "too dear" }),
                    });
                    Equal(200, blocked.Status, "refused, not failed: " + blocked.Text);
                    var spend = blocked.Body!["spend"]!;
                    Equal((false, "spend_blocked", "block", "sc-request", false), (blocked.Body["posted"]!.GetValue<bool>(), blocked.Body["code"]!.GetValue<string>(),
                        spend["verdict"]!.GetValue<string>(), spend["pane"]!.GetValue<string>(), spend["canProceed"]!.GetValue<bool>()), "the confirm modal's answer: " + blocked.Text);
                    Check(spend["html"]!.GetValue<string>().Contains("data-pane=\"sc-request\"", StringComparison.Ordinal) && spend["estimate"]!["worstMicros"]!.GetValue<long>() > 0 &&
                        spend["estimate"]!["chip"]!.GetValue<string>().Length > 0, "with its markup and estimate");
                    Equal(before, (await app.Get("/api/runs/storage")).Body!["count"]!.GetValue<long>(), "nothing recorded");
                    Equal(200, (await app.SendAsync(HttpMethod.Post, $"/api/runs/{slowId}/pin", new JsonObject { ["pinned"] = true })).Status, "pinned");
                    Equal(true, (await app.Get($"/api/runs/{slowId}")).Body!["run"]!["pinned"]!.GetValue<bool>(), "stays pinned");
                    var deleted = await app.SendAsync(HttpMethod.Post, "/api/runs/delete", new JsonObject { ["ids"] = new JsonArray(slowId) });
                    Equal(1, deleted.Body!["deleted"]!.GetValue<int>(), "deleted: " + deleted.Text);
                    Equal(404, (await app.Get($"/api/runs/{slowId}")).Status, "gone");
                }
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("runs.engine.codex-takes-its-service-tier-and-the-patched-options", async () =>
        {
            // CodexTransport auto (WebSocket first): a run whose request the hook must change goes over SSE, where the hook sees it.
            await using var host = RunsHost.Routing("runs-engine-codex", codexTransport: null);
            var vault = host.Services.GetRequiredService<CredentialVault>();
            await vault.SaveSignInAsync("openai-codex", new PiSharp.AI.Authentication.OAuth.OAuthCredentialSnapshot(CodexJwt("acct-run"), Planted("rt-"),
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), new Dictionary<string, string> { ["accountId"] = "acct-run" }),
                "acct-run", new Dictionary<string, string>(), "ChatGPT acct-run", null, default);
            var model = ModelOf(host.Services.GetRequiredService<IModelCatalog>(), "openai-codex", "openai-codex-responses");
            var caps = host.Engine.Capabilities("openai-codex", model)!;
            Equal("auto,default,flex,priority", string.Join(",", caps.ServiceTiers), "the Codex backend's tiers");
            Check(caps.Find(RunModelOptions.ServiceTier) is { Supported: true }, "the service tier is offered on Codex");

            var weather = new RunTool("get_weather", "Weather", new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["city"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("city"),
                ["additionalProperties"] = false,
            }, Strict: true);
            var spec = new RunSpec
            {
                Provider = "openai-codex",
                Model = model,
                ServiceTier = "priority",
                TextVerbosity = "high",
                SessionId = "run-session-1",
                ParallelToolCalls = false,
                Tools = [weather],
                Messages = [new RunMessage { Text = "codex tier run" }],
            };
            var start = await host.Engine.StartAsync(spec);
            Check(start.Posted, "posted: " + start.Refusal?.Message);
            var run = await RunsFinishedAsync(host.Store, start.Id!);
            Equal(RunStatus.Completed, run.Status, "completed: " + run.Error + " " + run.Notes);
            Equal("Hello world", run.OutputText, "the provider's answer");
            Check(host.Provider.Requests.All(request => request.Method == "POST"),
                "over SSE, never the WebSocket the hook cannot change: " + string.Join(", ", host.Provider.Requests.Select(request => request.Key)));
            var sent = host.Provider.Requests.Single(request => request.Body?.Contains("codex tier run", StringComparison.Ordinal) == true);
            Check(sent.Url.AbsolutePath.EndsWith("/codex/responses", StringComparison.Ordinal), "the Codex endpoint: " + sent.Url);
            Equal("zstd", sent.Header("content-encoding"), "the patched body is a zstd frame again");
            var body = JsonNode.Parse(sent.Body!)!;
            Equal("priority", body["service_tier"]?.GetValue<string>(), "the body carries service_tier");
            Equal("high", body["text"]?["verbosity"]?.GetValue<string>(), "text verbosity");
            Equal(false, body["parallel_tool_calls"]?.GetValue<bool>(), "parallel tool calls off");
            Equal("run-session-1", body["prompt_cache_key"]?.GetValue<string>(), "the session id as prompt_cache_key");
            Equal("run-session-1", sent.Header("session-id"), "and as Codex's session header");
            Equal(true, body["tools"]?[0]?["strict"]?.GetValue<bool>(), "the strict tool");
            Check(body["model"]?.GetValue<string>() == model && body["input"] is JsonArray && body["stream"]?.GetValue<bool>() == true, "otherwise the transport's own request");
            Check(run.Options?["applied"]?.AsArray().Select(item => item!.GetValue<string>()).Contains(RunModelOptions.ServiceTier) == true, "History records the tier as applied");

            var count = host.Provider.Requests.Count;
            var refused = await ThrowsAsync<RunOptionException>(() => host.Engine.StartAsync(spec with { ServiceTier = "scale" }), "scale on Codex");
            Check(refused.Message.Contains("Codex backend takes auto, default, flex, priority", StringComparison.Ordinal), "named: " + refused.Message);
            Equal(count, host.Provider.Requests.Count, "nothing was sent for it");
        }),
    ];

    /// <summary>The services of the run engine in process: the core, the spend area and the runs area over faux models (priced), or over
    /// real routing with a fake provider.</summary>
    internal sealed class RunsHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly string root;
        private readonly bool ownsRoot;

        private RunsHost(ServiceProvider provider, string root, bool ownsRoot, WebAgentOptions options, ProviderFake fake, ConcurrentQueue<string> logs)
        {
            this.provider = provider;
            this.root = root;
            this.ownsRoot = ownsRoot;
            Options = options;
            Provider = fake;
            Logs = logs;
        }

        public WebAgentOptions Options { get; }

        public IServiceProvider Services => provider;

        public IDurableModels Models => provider.GetRequiredService<IDurableModels>();

        public RunEngine Engine => provider.GetRequiredService<RunEngine>();

        public RunStore Store => provider.GetRequiredService<RunStore>();

        public RunsDb Runs => provider.GetRequiredService<RunsDb>();

        public IAccountStore Accounts => provider.GetRequiredService<IAccountStore>();

        /// <summary>The fake provider (only used by <see cref="Routing"/>).</summary>
        public ProviderFake Provider { get; }

        public ConcurrentQueue<string> Logs { get; }

        public string LogText => string.Join("\n", Logs);

        /// <summary>Over the priced faux models (--faux --faux-metered) in a new temporary folder.</summary>
        public static RunsHost Faux(string name)
        {
            var root = Temp(name);
            return Faux(root, Path.Join(root, "data"), ownsRoot: true);
        }

        /// <summary>Over the priced faux models with its data in <paramref name="data"/> (the caller removes <paramref name="root"/>).</summary>
        public static RunsHost Faux(string root, string data, bool ownsRoot = false)
        {
            var options = new WebAgentOptions { DataDirectory = data, Faux = true, FauxMetered = true };
            options.EnsureDirectories();
            var services = new ServiceCollection().AddLogging();
            services.AddWebAgentCore(options);
            services.AddWebAgentFallbacks(options);
            services.AddSpendArea(options);
            services.AddRunsArea(options);
            return Build(services, root, ownsRoot, options);
        }

        /// <summary>Over real routing (not --faux) with <see cref="Provider"/> as every provider request's handler (Codex over
        /// <paramref name="codexTransport"/>: SSE by default, null for auto).</summary>
        public static RunsHost Routing(string name, string? codexTransport = "sse")
        {
            var root = Temp(name);
            var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data") };
            options.EnsureDirectories();
            var fake = new ProviderFake();
            var logs = new ConcurrentQueue<string>();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(new ListLoggerProvider(logs)));
            services.AddSingleton(RoutingCredentials(new Dictionary<string, string>(), new FakeOAuth(), root));
            services.AddSingleton(new RoutingOptions { ProviderHandler = fake, CodexTransport = codexTransport });
            services.AddWebAgentCore(options);
            services.AddCredentialsArea(options);
            services.AddRoutingArea(options);
            services.AddWebAgentFallbacks(options);
            services.AddSpendArea(options);
            services.AddRunsArea(options);
            return Build(services, root, ownsRoot: true, options, fake, logs);
        }

        private static RunsHost Build(IServiceCollection services, string root, bool ownsRoot, WebAgentOptions options, ProviderFake? fake = null,
            ConcurrentQueue<string>? logs = null)
        {
            var provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebAgentDb>().Migrate();
            return new RunsHost(provider, root, ownsRoot, options, fake ?? new ProviderFake(), logs ?? new ConcurrentQueue<string>());
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteClearPools();
            if (ownsRoot) DeleteTemp(root);
        }
    }

    /// <summary>The stream options of a chat's durable request (metadata.pisharp with its op, source chat and the chat id).</summary>
    private static DurableStreamOptions RunsChatOptions(string op, string chat) => new()
    {
        Metadata = ChordObject.Empty.SetItem(SessionStreamMetadata.Key, SessionStreamMetadata.Fields(("op", op), ("source", "chat"), ("run", chat))),
    };

    /// <summary>The run <paramref name="id"/> once it has ended.</summary>
    private static async Task<RunRecord> RunsFinishedAsync(RunStore store, string id)
    {
        RunRecord? run = null;
        await WaitUntilAsync(() =>
        {
            run = store.Get(id);
            return Task.FromResult(run is not null && run.Status != RunStatus.Running);
        }, "run " + id + " ends", TimeSpan.FromSeconds(30));
        return run!;
    }

    /// <summary>runs.db and its WAL as text (Latin-1, so every byte is one character), to search for what must not be there.</summary>
    private static string RunsFileText(string path)
    {
        var text = new StringBuilder();
        foreach (var file in new[] { path, path + "-wal" })
        {
            if (!File.Exists(file)) continue;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            text.Append(Encoding.Latin1.GetString(copy.ToArray()));
        }
        return text.ToString();
    }

    /// <summary>The SSE frames of <paramref name="path"/>: all of them until the server ends the stream, or the first <paramref name="take"/>
    /// event frames, after which the connection is closed (a tab that goes away).</summary>
    private static async Task<List<(string? Id, string Event, JsonNode? Data)>> RunsSseAsync(WebAgentApp app, string path, string? lastEventId = null, int? take = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(app.Base, path));
        if (lastEventId is not null) request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await app.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Equal(200, (int)response.StatusCode, "the event stream of " + path);
        Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType, "an event stream");
        Check(response.Headers.CacheControl is { NoCache: true, NoTransform: true }, "never cached or transformed");
        var frames = new List<(string? Id, string Event, JsonNode? Data)>();
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(body, Encoding.UTF8);
        string? id = null, kind = null, data = null;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Length == 0)
            {
                if (kind is not null || data is not null)
                {
                    frames.Add((id, kind ?? "message", data is null ? null : JsonNode.Parse(data)));
                    if (take is { } count && frames.Count(frame => frame.Event == "event") >= count) break;
                }
                (id, kind, data) = (null, null, null);
                continue;
            }
            if (line.StartsWith(':')) continue;
            if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
            else if (line.StartsWith("event: ", StringComparison.Ordinal)) kind = line[7..];
            else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = data is null ? line[6..] : data + "\n" + line[6..];
        }
        return frames;
    }
}
