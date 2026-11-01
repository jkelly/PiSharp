using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Spend;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

// Decision 0011 (run W3, S3): the Spend Guard. In process, over a temporary webagent.db with the ledger's and the guard's tables, a scripted
// estimator ($3 / $15 per million tokens; providers "metered", "sub", "free" and "unknown") and scripted models (no provider, no network):
// a call is reserved before it is sent and settled from how it ended (usage -> actual; error or cancel before output -> released; cancel
// after output -> the estimate; a stream that ends without usage after output -> uncertain); a Block cap never switches the model and sends
// nothing; an Ask needs exactly one approval; "cap the output" lowers maxTokens only when chosen; a recovered request reuses its op;
// subscription calls are recorded at list price and never blocked by money caps; preflight reserves nothing; a Compare group reserves all
// or none through GuardedRunExecutor. End to end, the built app with --faux-metered guards a chat's durable requests and answers
// /api/spend/preflight, approvals and stops.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SpendGuardCases() =>
    [
        Case("spend.guard.registration-decorates-the-final-models-once", async () =>
        {
            Equal("1100:spend: approvals,1101:spend: stops,1102:spend: approval uses,1103:spend: approval submissions",
                string.Join(",", SpendGuardMigrations.All.Select(migration => $"{migration.Id}:{migration.Name}")), "the migrations");
            Check(SpendGuardMigrations.All.All(migration => MigrationArea.Of(migration.Id) == MigrationArea.Spend), "inside the spend range");

            var root = Temp("spend-guard-registration");
            var options = new WebAgentOptions { DataDirectory = Path.Join(root, "data"), Faux = true, FauxMetered = true };
            options.EnsureDirectories();
            var services = new ServiceCollection().AddLogging();
            services.AddWebAgentCore(options);
            services.AddWebAgentFallbacks(options);
            services.AddSpendArea(options);
            services.AddSpendGuard();
            Equal(1, services.Count(descriptor => descriptor.ServiceType == typeof(IDurableModels)), "one models registration, even when the guard is added twice");
            Equal(1, services.Count(descriptor => descriptor.ImplementationInstance is IWebAgentMigration { Id: 1100 }), "its migrations once");
            await using (var provider = services.BuildServiceProvider())
            {
                var models = provider.GetRequiredService<IDurableModels>();
                Check(models is GuardedDurableModels { Inner: MeteredFauxDurableModels }, "the guard wraps the priced faux models");
                Check(ReferenceEquals(((GuardedDurableModels)models).Inner, provider.GetRequiredService<MeteredFauxDurableModels>()), "the app's own instance");
                Check(ReferenceEquals(provider.GetRequiredService<ISpendGuard>(), provider.GetRequiredService<SpendGuard>()), "one guard");
                Check(models.GetModel("faux", "echo") is not null && models.GetModelsOfType("chat", "faux").Length > 0, "lookups pass through");
                provider.GetRequiredService<WebAgentDb>().Migrate();
                var answer = await GuardCollect(models.StreamSimple(models.GetModel("faux", "echo")!, [ChordJson.Object([new("role", "user"), new("content", "hello")])],
                    GuardOptions("op-int", "chat-int"), CancellationToken.None));
                Check(answer[^1] is StreamDone, "the priced faux model answers through the guard");
                var row = (await provider.GetRequiredService<ISpendLedger>().GetAsync("op-int"))!;
                Check(row is { State: ReservationState.Settled, Billing: BillingKind.Metered, Settlement: SettlementKind.Actual, ActualMicros: > 0, Run: "chat-int" },
                    "with the real estimator and ledger: reserved, then settled at the reported cost");
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteTemp(root);

            var script = new GuardScript((call, token) => GuardAnswer(call.Model, "never", null));
            var bare = new ServiceCollection().AddSingleton<IDurableModels>(script).AddSpendGuard();
            await using (var provider = bare.BuildServiceProvider())
            {
                var models = (GuardedDurableModels)provider.GetRequiredService<IDurableModels>();
                Check(ReferenceEquals(models.Inner, script), "an instance registration is the inner models");
                var events = await GuardCollect(models.StreamSimple(GuardModel("metered", "m"), GuardMessages(100), GuardOptions("op-1", "chat-1"), CancellationToken.None));
                var error = (StreamError)events.Single();
                Check(SpendStreamText(error.Message)!.StartsWith(SpendGuard.ErrorPrefix, StringComparison.Ordinal) &&
                    SpendStreamText(error.Message)!.Contains("nothing was sent", StringComparison.Ordinal), "a guard that cannot check fails closed: " + SpendStreamText(error.Message));
                Equal(0, script.Calls.Count, "and sends nothing");
            }
            var typed = new ServiceCollection().AddSingleton<IDurableModels, FauxDurableModels>().AddSpendGuard();
            await using (var provider = typed.BuildServiceProvider())
                Check(provider.GetRequiredService<IDurableModels>() is GuardedDurableModels { Inner: FauxDurableModels }, "a type registration is built for the guard");
        }),

        Case("spend.guard.allowed-call-passes-through-and-settles-at-usage", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-allow");
            var usage = GuardUsage(10, 5);
            var script = new GuardScript((call, token) => call.Model.Provider == "metered" && call.Model.Id == "silent"
                ? GuardAnswer(call.Model, "no usage", null) : GuardAnswer(call.Model, "hi there", call.Model.Provider == "free" ? null : usage));
            var models = world.Over(script);
            var model = GuardModel("metered", "m");
            var messages = GuardMessages(3000);
            var options = GuardOptions("op-a", "chat-1", maxTokens: 1000);
            var events = await GuardCollect(models.StreamSimple(model, messages, options, CancellationToken.None));
            Equal(5, events.Count, "every event passes through");
            Check(events[^1] is StreamDone { Message.Usage: var reported } && ReferenceEquals(reported, usage), "unchanged");
            var sent = script.Calls.Single();
            Check(ReferenceEquals(sent.Model, model) && sent.Messages == messages && ReferenceEquals(sent.Options, options), "the same model, messages and options");
            var row = (await world.Ledger.GetAsync("op-a"))!;
            Equal((ReservationState.Settled, SettlementKind.Actual, 105L), (row.State, row.Settlement ?? SettlementKind.Entered, row.ActualMicros ?? -1), "settled at the reported cost");
            Equal((SpendSource.Chat, "chat-1", "metered", "m", 1000), (row.Source, row.Run, row.Provider, row.Model, row.OutputTokens), "the call as reserved");
            Equal(GuardPrices.Rates.WorstMicros(CostEstimator.InputTokens(CostEstimator.InputBytes(messages)), 1000), row.ReservedMicros ?? -1, "its worst case was reserved");

            await GuardCollect(models.StreamSimple(GuardModel("metered", "silent"), messages, GuardOptions("op-b", "chat-1"), CancellationToken.None));
            var silent = (await world.Ledger.GetAsync("op-b"))!;
            Equal((ReservationState.Uncertain, "the answer finished without reporting usage"), (silent.State, silent.Reason), "a metered answer without usage is uncertain");
            await GuardCollect(models.StreamSimple(GuardModel("free", "f"), messages, GuardOptions("op-c", "chat-1"), CancellationToken.None));
            Equal(ReservationState.Settled, (await world.Ledger.GetAsync("op-c"))!.State, "a free answer without usage settles");

            var completed = await models.CompleteSimpleAsync(model, messages, GuardOptions("op-d", null, source: "playground"), CancellationToken.None);
            Equal(StopReason.Stop, completed.StopReason, "a completion");
            var summary = (await world.Ledger.GetAsync("op-d"))!;
            Equal((ReservationState.Settled, 105L, SpendSource.Playground, (string?)null), (summary.State, summary.ActualMicros ?? -1, summary.Source, summary.Run), "settled too");

            var unmarked = await GuardCollect(models.StreamSimple(model, messages, null, CancellationToken.None));
            Check(unmarked[^1] is StreamDone, "a request without metadata.pisharp");
            var rows = world.Rows();
            Check(rows.Any(row => row.Op.StartsWith("call-", StringComparison.Ordinal) && row.State == "settled"), "is a chat call under a fresh op: " + string.Join(",", rows));
        }),

        Case("spend.guard.block-never-switches-the-model-and-sends-nothing", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-block");
            var cap = await world.CapAsync(CapPeriod.Request, 50_000, CapAction.Block);
            var script = new GuardScript((call, token) => GuardAnswer(call.Model, "answered", GuardUsage(10, 5)));
            var models = world.Over(script);
            var model = GuardModel("metered", "big");
            var events = await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-1", "chat-1"), CancellationToken.None));
            var stopped = (StreamError)events.Single();
            Equal((StopReason.Error, "metered", "big", model.Api), (stopped.Message.StopReason, stopped.Message.Provider, stopped.Message.Model, stopped.Message.Api), "the error names the model asked for");
            var text = SpendStreamText(stopped.Message)!;
            Check(text.StartsWith(SpendGuard.ErrorPrefix + SpendErrors.Blocked + ": ", StringComparison.Ordinal) && text.Contains("billing", StringComparison.Ordinal), "a billing error: " + text);
            var parsed = SpendErrors.Parse(text)!;
            Equal((SpendErrors.Blocked, "op-1", (long?)cap.Id, false), (parsed.Code, parsed.Op, parsed.CapId, parsed.CanProceed), "the code, the op and the cap");
            Equal(0, script.Calls.Count, "nothing was sent");
            Check(await world.Ledger.GetAsync("op-1") is null, "nothing was reserved");
            var stop = (await world.Guard.StoppedAsync("op-1"))!;
            Equal((SpendErrors.Blocked, cap.Id, "chat-1", "sc-request"), (stop.Code, stop.Decision.BlockingCap!.Id, stop.Call.Run, stop.Decision.Causes[0].Pane), "the stop is recorded");

            await world.Guard.ApproveAsync(new SpendApproval("chat-1", SpendChoice.ProceedOnce));
            var again = await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-2", "chat-1"), CancellationToken.None));
            Check(again.Single() is StreamError { Message.Model: "big" }, "proceed once never passes a Block cap");
            Equal(0, script.Calls.Count, "still nothing sent");
            Equal(0, (await world.Approvals.WaitingAsync("chat-1")).Count, "the approval was used up by that stopped call");
            var completion = await models.CompleteSimpleAsync(model, GuardMessages(3000), GuardOptions("op-3", "chat-1"), CancellationToken.None);
            Check(completion.StopReason == StopReason.Error && completion.Model == "big" && SpendErrors.Parse(SpendStreamText(completion))?.Code == SpendErrors.Blocked,
                "a completion is stopped the same way");

            await world.Guard.ApproveAsync(new SpendApproval("chat-1", SpendChoice.CapOutput) { MaxOutputTokens = 1000 });
            var options = GuardOptions("op-4", "chat-1");
            var capped = await GuardCollect(models.StreamSimple(model, GuardMessages(3000), options, CancellationToken.None));
            Check(capped[^1] is StreamDone, "capping the output so it fits sends it");
            var sent = script.Calls.Single();
            Check(ReferenceEquals(sent.Model, model) && sent.Options!.MaxTokens == 1000 && sent.Options.Metadata!.ToJson() == options.Metadata!.ToJson(),
                "the same model and metadata, with maxTokens lowered to the chosen cap");
            var row = (await world.Ledger.GetAsync("op-4"))!;
            Equal((1000, "asked · output capped at 1,000 tokens"), (row.OutputTokens, row.Note), "reserved at the capped worst case");
            Check(await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-5", "chat-1"), CancellationToken.None)) is [StreamError], "the next call is blocked again");
            Equal(1, script.Calls.Count, "one send in all");
        }),

        Case("spend.guard.ask-needs-exactly-one-approval", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-ask");
            await world.CapAsync(CapPeriod.Request, 50_000, CapAction.Ask);
            var script = new GuardScript((call, token) => GuardAnswer(call.Model, "answered", GuardUsage(10, 5)));
            var models = world.Over(script);
            var model = GuardModel("metered", "m");
            async Task<bool> SendAsync(string op, string run) =>
                (await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions(op, run), CancellationToken.None)))[^1] is StreamDone;

            Check(!await SendAsync("op-1", "chat-1"), "an Ask cap stops the call");
            var asked = SpendErrors.Parse(SpendStreamText(((StreamError)(await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-1b", "chat-1"),
                CancellationToken.None))).Single()).Message))!;
            Equal((SpendErrors.Ask, true), (asked.Code, asked.CanProceed), "with spend_ask, which the user may answer");
            await world.Guard.ApproveAsync(new SpendApproval("chat-2", SpendChoice.ProceedOnce));
            Check(!await SendAsync("op-2", "chat-1"), "another chat's approval does not answer it");
            Equal(0, script.Calls.Count, "nothing sent yet");

            await world.Guard.ApproveAsync(new SpendApproval("chat-1", SpendChoice.ProceedOnce));
            Equal(SpendVerdict.Allow, (await world.Guard.PreflightAsync(GuardCall("op-3", "chat-1"))).Verdict, "preflight sees the approval");
            Equal(1, (await world.Approvals.WaitingAsync("chat-1")).Count, "without using it");
            Check(await SendAsync("op-3", "chat-1"), "one approval sends one call");
            var row = (await world.Ledger.GetAsync("op-3"))!;
            Equal(("asked · sent once", 8192), (row.Note, row.OutputTokens), "as it was (the output is not capped)");
            Check(script.Calls.Single().Options!.MaxTokens is null, "maxTokens is left alone");
            Check(!await SendAsync("op-4", "chat-1"), "the next call asks again");
            Equal(1, script.Calls.Count, "one send");

            await world.Guard.ApproveAsync(new SpendApproval("chat-1", SpendChoice.ProceedOnce));
            var racing = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() => SendAsync("race-" + index, "chat-1"))));
            Equal(1, racing.Count(sent => sent), "one approval, eight calls at once: exactly one is sent");
            Equal(2, script.Calls.Count, "two sends in all");

            await world.Guard.ApproveAsync(new SpendApproval("chat-1", SpendChoice.ProceedOnce) { Op = "op-x" });
            Check(!await SendAsync("op-y", "chat-1"), "an approval of one op answers no other");
            Check(await SendAsync("op-x", "chat-1"), "it answers its own op");
            await world.Guard.ApproveAsync(new SpendApproval("chat-1", SpendChoice.ProceedOnce));
            world.Clock.Now += SpendApprovalStore.Lifetime + TimeSpan.FromSeconds(1);
            Check(!await SendAsync("op-late", "chat-1"), "an approval lapses unused");
            Equal(3, script.Calls.Count, "three sends in all");

            var unknown = await GuardCollect(models.StreamSimple(GuardModel("unknown", "u"), GuardMessages(3000), GuardOptions("op-u", "chat-3"), CancellationToken.None));
            Equal(SpendErrors.UnknownPrice, SpendErrors.Parse(SpendStreamText(((StreamError)unknown.Single()).Message))?.Code, "an unknown price asks first");
            await world.Guard.ApproveAsync(new SpendApproval("chat-3", SpendChoice.ProceedOnce));
            Check((await GuardCollect(models.StreamSimple(GuardModel("unknown", "u"), GuardMessages(3000), GuardOptions("op-u2", "chat-3"), CancellationToken.None)))[^1] is StreamDone,
                "and is sent once approved");
            Equal((BillingKind.Unknown, (long?)null), ((await world.Ledger.GetAsync("op-u2"))!.Billing, (await world.Ledger.GetAsync("op-u2"))!.ReservedMicros), "recorded without a price");
        }),

        Case("spend.guard.cancel-or-error-before-first-token-releases", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-release");
            var script = new GuardScript((call, token) => GuardBeforeOutput(call.Model, call.Options!.Metadata!.ToJson(), token));
            var models = world.Over(script);
            var model = GuardModel("metered", "m");

            using (var cancel = new CancellationTokenSource())
            {
                var seen = new List<StreamEvent>();
                await ThrowsAsync<OperationCanceledException>(async () =>
                {
                    await foreach (var item in models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-wait", "c"), cancel.Token))
                    {
                        seen.Add(item);
                        cancel.Cancel();
                    }
                }, "a cancel before any output");
                Check(seen.Single() is StreamStarted, "only the start arrived");
            }
            var cancelled = (await world.Ledger.GetAsync("op-wait"))!;
            Equal((ReservationState.Released, "cancelled before any output", 0L), (cancelled.State, cancelled.Reason, cancelled.CountedMicros), "released");

            Check((await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-error", "c"), CancellationToken.None)))[^1] is StreamError,
                "a provider error passes through");
            var failed = (await world.Ledger.GetAsync("op-error"))!;
            Check(failed.State == ReservationState.Released && failed.Reason!.StartsWith("provider error before any output", StringComparison.Ordinal), "released: " + failed.Reason);
            await ThrowsAsync<IOException>(() => GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-throw", "c"), CancellationToken.None)), "a thrown failure");
            Equal(ReservationState.Released, (await world.Ledger.GetAsync("op-throw"))!.State, "released");
            await foreach (var item in models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-wait-2", "c"), CancellationToken.None)) break;
            Equal((ReservationState.Released, "cancelled before any output"), ((await world.Ledger.GetAsync("op-wait-2"))!.State, (await world.Ledger.GetAsync("op-wait-2"))!.Reason),
                "a reader that stops before any output releases it");

            await using var lease = await world.Executor.ReserveAsync(GuardCall("op-complete", "c"));
            await ThrowsAsync<InvalidOperationException>(() => lease.CompleteAsync(model.Descriptor, (send, token) => Task.FromException<AssistantMessage>(new InvalidOperationException("refused"))),
                "a completion that throws");
            Equal(ReservationState.Released, (await world.Ledger.GetAsync("op-complete"))!.State, "released");
        }),

        Case("spend.guard.eof-after-tokens-is-uncertain-and-cancel-after-tokens-settles-at-the-estimate", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-uncertain");
            var script = new GuardScript((call, token) => GuardAfterOutput(call.Model, call.Options!.Metadata!.ToJson(), "partial answer", token));
            var models = world.Over(script);
            var model = GuardModel("metered", "m");
            var messages = GuardMessages(3000);
            var input = CostEstimator.InputTokens(CostEstimator.InputBytes(messages));
            var seenTokens = CostEstimator.InputTokens(System.Text.Encoding.UTF8.GetByteCount("partial answer"));

            var dropped = await GuardCollect(models.StreamSimple(model, messages, GuardOptions("op-eof", "c"), CancellationToken.None));
            Check(dropped[^1] is TextDelta, "the stream ended after output without a final message");
            var eof = (await world.Ledger.GetAsync("op-eof"))!;
            Equal((ReservationState.Uncertain, SpendLedger.RecoveryReason), (eof.State, eof.Reason), "uncertain");
            Equal((GuardPrices.Rates.WorstMicros(input, seenTokens), eof.ReservedMicros ?? -1), (eof.EstimateMicros ?? -1, eof.CountedMicros), "with an estimate, and still counted");

            await GuardCollect(models.StreamSimple(model, messages, GuardOptions("op-error", "c"), CancellationToken.None));
            var failed = (await world.Ledger.GetAsync("op-error"))!;
            Check(failed.State == ReservationState.Uncertain && failed.Reason!.Contains("after output", StringComparison.Ordinal), "an error after output is uncertain: " + failed.Reason);
            await ThrowsAsync<IOException>(() => GuardCollect(models.StreamSimple(model, messages, GuardOptions("op-throw", "c"), CancellationToken.None)), "a thrown failure after output");
            Equal(ReservationState.Uncertain, (await world.Ledger.GetAsync("op-throw"))!.State, "uncertain");

            using (var cancel = new CancellationTokenSource())
            {
                await ThrowsAsync<OperationCanceledException>(async () =>
                {
                    await foreach (var item in models.StreamSimple(model, messages, GuardOptions("op-wait", "c"), cancel.Token))
                        if (item is TextDelta) cancel.Cancel();
                }, "a cancel after output");
            }
            var settled = (await world.Ledger.GetAsync("op-wait"))!;
            Equal((ReservationState.Settled, SettlementKind.Estimate, GuardPrices.Rates.WorstMicros(input, seenTokens)),
                (settled.State, settled.Settlement ?? SettlementKind.Actual, settled.ActualMicros ?? -1), "settled at the input estimate plus the output seen");

            await foreach (var item in models.StreamSimple(model, messages, GuardOptions("op-aborted", "c"), CancellationToken.None)) { }
            var aborted = (await world.Ledger.GetAsync("op-aborted"))!;
            Equal((ReservationState.Settled, SettlementKind.Estimate), (aborted.State, aborted.Settlement ?? SettlementKind.Actual), "an aborted answer after output, too");
        }),

        Case("spend.guard.partial-usage-after-output-is-uncertain-or-settles-at-the-estimate", async () =>
        {
            // A real provider reports usage before the stream ends (Anthropic's message_start: the input alone), and its error and aborted
            // terminals carry it. That partial usage is never the actual cost: the estimate is used, raised to at least what it reports.
            await using var world = GuardWorld.Create("spend-guard-partial");
            var inputOnly = GuardUsage(1000, 0);
            var large = GuardUsage(5000, 40);
            var script = new GuardScript((call, token) => GuardAfterOutput(call.Model, call.Options!.Metadata!.ToJson(), "partial answer", token,
                call.Options.Metadata.ToJson().Contains("-large\"", StringComparison.Ordinal) ? large : inputOnly));
            var models = world.Over(script);
            var model = GuardModel("metered", "m");
            var messages = GuardMessages(3000);
            var input = CostEstimator.InputTokens(CostEstimator.InputBytes(messages));
            var seenTokens = CostEstimator.InputTokens(System.Text.Encoding.UTF8.GetByteCount("partial answer"));
            var estimate = GuardPrices.Rates.WorstMicros(input, seenTokens);
            var inputCost = SpendUsage.From(inputOnly).CostMicros!.Value;
            Check(inputCost is > 0 && inputCost < estimate, $"the input-only usage ({inputCost}) costs less than the estimate ({estimate})");

            var errored = await GuardCollect(models.StreamSimple(model, messages, GuardOptions("op-error", "c"), CancellationToken.None));
            Check(errored[^1] is StreamError { Message.Usage.Input: 1000 }, "the error terminal carries the input usage");
            var failed = (await world.Ledger.GetAsync("op-error"))!;
            Equal((ReservationState.Uncertain, (SettlementKind?)null, (long?)null, estimate), (failed.State, failed.Settlement, failed.ActualMicros, failed.EstimateMicros ?? -1),
                "an error after output with input-only usage is uncertain at the estimate, not settled at the input cost");
            Check(failed.Reason!.Contains("after output", StringComparison.Ordinal) && failed.CountedMicros == failed.ReservedMicros, "still counted in full: " + failed.Reason);

            await GuardCollect(models.StreamSimple(model, messages, GuardOptions("op-aborted", "c"), CancellationToken.None));
            var aborted = (await world.Ledger.GetAsync("op-aborted"))!;
            Equal((ReservationState.Settled, SettlementKind.Estimate, estimate), (aborted.State, aborted.Settlement ?? SettlementKind.Actual, aborted.ActualMicros ?? -1),
                "an aborted answer after output with input-only usage settles at the estimate");

            var largeCost = SpendUsage.From(large).CostMicros!.Value;
            Check(largeCost > GuardPrices.Rates.WorstMicros(input, 40), $"the large usage ({largeCost}) costs more than the estimate");
            await GuardCollect(models.StreamSimple(model, messages, GuardOptions("op-error-large", "c"), CancellationToken.None));
            var failedLarge = (await world.Ledger.GetAsync("op-error-large"))!;
            Equal((ReservationState.Uncertain, largeCost), (failedLarge.State, failedLarge.EstimateMicros ?? -1), "uncertain at no less than the partial usage reports");
            await GuardCollect(models.StreamSimple(model, messages, GuardOptions("op-aborted-large", "c"), CancellationToken.None));
            var abortedLarge = (await world.Ledger.GetAsync("op-aborted-large"))!;
            Equal((ReservationState.Settled, SettlementKind.Estimate, largeCost), (abortedLarge.State, abortedLarge.Settlement ?? SettlementKind.Actual, abortedLarge.ActualMicros ?? -1),
                "settled at no less than the partial usage reports");

            // The same rules for a completion (its final message only).
            AssistantMessage Ended(StopReason reason, string text, TokenUsage usage) =>
                new(model.Api, model.Provider, model.Id, 1, text.Length > 0 ? [new TextContent(text)] : [], usage, reason);
            async Task<Reservation> CompleteAsync(string op, AssistantMessage message)
            {
                await world.Executor.CompleteAsync(GuardCall(op, "c"), model.Descriptor, (send, token) => Task.FromResult(message));
                return (await world.Ledger.GetAsync(op))!;
            }
            Equal(ReservationState.Uncertain, (await CompleteAsync("done-error", Ended(StopReason.Error, "partial answer", inputOnly))).State, "a completion's error after output");
            var completedAborted = await CompleteAsync("done-aborted", Ended(StopReason.Aborted, "partial answer", inputOnly));
            Equal((ReservationState.Settled, SettlementKind.Estimate), (completedAborted.State, completedAborted.Settlement ?? SettlementKind.Actual), "a completion's abort after output");
            Equal(ReservationState.Released, (await CompleteAsync("done-error-before", Ended(StopReason.Error, "", inputOnly))).State,
                "an error before any output is released even with input usage");
            var counted = await CompleteAsync("done-aborted-tokens", Ended(StopReason.Aborted, "", GuardUsage(1000, 30)));
            Equal((ReservationState.Settled, SettlementKind.Estimate, GuardPrices.Rates.WorstMicros(input, 30)),
                (counted.State, counted.Settlement ?? SettlementKind.Actual, counted.ActualMicros ?? -1), "output tokens the provider counted are output seen");
            var done = await CompleteAsync("done-stop", Ended(StopReason.Stop, "answer", GuardUsage(10, 5)));
            Equal((SettlementKind.Actual, 105L), (done.Settlement ?? SettlementKind.Estimate, done.ActualMicros ?? -1), "a finished answer still settles at its usage");
        }),

        Case("spend.guard.recovery-reuses-the-op", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-recovery");
            var script = new GuardScript((call, token) => GuardAnswer(call.Model, "answered", GuardUsage(10, 5)));
            var model = GuardModel("metered", "m");
            var left = await world.Guard.ReserveCallAsync(GuardCall("op-r", "chat-1"));
            Check(left.Reserved && left.LedgerOp == "op-r", "a request reserved, then the process died before it ended");

            world.Restart();
            var models = world.Over(script);
            var events = await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-r", "chat-1"), CancellationToken.None));
            Check(events[^1] is StreamDone, "the recovered request is sent");
            Equal(1, script.Calls.Count, "once");
            var row = (await world.Ledger.GetAsync("op-r"))!;
            Equal((ReservationState.Uncertain, SpendLedger.RecoveryReason), (row.State, row.Reason), "its reservation was reused as recovery left it (uncertain, for the user)");
            Equal(1, world.Rows().Count(row => row.Op.StartsWith("op-r", StringComparison.Ordinal)), "nothing was reserved twice");

            await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-s", "chat-1"), CancellationToken.None));
            await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-s", "chat-1"), CancellationToken.None));
            Equal("op-s:settled,op-s~2:settled", string.Join(",", world.Rows().Where(row => row.Op.StartsWith("op-s", StringComparison.Ordinal)).Select(row => row.Op + ":" + row.State)),
                "an op sent again after it settled is a new provider call under a new key");

            await world.CapAsync(CapPeriod.Request, 50_000, CapAction.Ask);
            await world.Guard.ApproveAsync(new SpendApproval("chat-2", SpendChoice.CapOutput) { MaxOutputTokens = 500 });
            var capped = await world.Guard.ReserveCallAsync(GuardCall("op-c", "chat-2"));
            Equal((true, (int?)500), (capped.Reserved, capped.MaxTokens), "reserved with the chosen output cap");
            world.Restart();
            models = world.Over(script);
            await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-c", "chat-2"), CancellationToken.None));
            Equal(500.0, script.Calls.Last().Options!.MaxTokens ?? -1, "the recovered request is sent with the output cap its approval chose");
            Equal(ReservationState.Uncertain, (await world.Ledger.GetAsync("op-c"))!.State, "and its row waits for the user");
        }),

        Case("spend.guard.subscription-at-list-price-never-blocked-by-money-caps", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-subscription");
            await world.CapAsync(CapPeriod.Request, 10_000, CapAction.Block);
            var daily = await world.CapAsync(CapPeriod.Day, 10_000, CapAction.Block);
            var usage = GuardUsage(1000, 2000);
            var script = new GuardScript((call, token) => GuardAnswer(call.Model, "subscribed", usage));
            var models = world.Over(script);
            var messages = GuardMessages(3000);
            Check((await GuardCollect(models.StreamSimple(GuardModel("metered", "m"), messages, GuardOptions("op-m", "chat-1"), CancellationToken.None))) is [StreamError],
                "a metered call over the caps is blocked");
            var events = await GuardCollect(models.StreamSimple(GuardModel("sub", "codex"), messages, GuardOptions("op-s", "chat-1"), CancellationToken.None));
            Check(events[^1] is StreamDone, "a subscription call is never blocked by a money cap");
            var row = (await world.Ledger.GetAsync("op-s"))!;
            var list = SpendUsage.From(usage).CostMicros!.Value;
            Equal((BillingKind.Subscription, ReservationState.Settled, list, 0L), (row.Billing, row.State, row.ListMicros, row.CountedMicros), "recorded at list price, not counted");
            Equal(GuardPrices.Rates.WorstMicros(CostEstimator.InputTokens(CostEstimator.InputBytes(messages)), 8192), row.ReservedMicros ?? -1, "its worst case at list price");
            Equal(0L, (await world.Ledger.CapStatusAsync()).Single(status => status.Cap.Id == daily.Id).SpentMicros ?? -1, "the daily cap counts nothing");
            var preflight = await SpendGuardEndpoints.PreflightAsync(world.Guard, GuardCall("", "chat-1", provider: "sub"), cheaper: false);
            Check(preflight.Allowed && preflight.Chip!.EndsWith(" list", StringComparison.Ordinal), "preflight: allowed, shown as list price: " + preflight.Chip);
        }),

        Case("spend.guard.preflight-reserves-nothing", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-preflight");
            var cap = await world.CapAsync(CapPeriod.Request, 50_000, CapAction.Ask);
            var call = GuardCall("", "chat-1");
            var answer = await SpendGuardEndpoints.PreflightAsync(world.Guard, call, cheaper: true);
            Equal((SpendVerdict.Ask, SpendErrors.Ask, true, "sc-request"), (answer.Verdict, answer.Code, answer.CanProceed, answer.Pane), "an Ask, with its pane");
            Check(answer.Sentence!.Contains("per-request cap", StringComparison.Ordinal) && answer.FitOutputTokens is > 0 && answer.Approval is null, "why, and what output cap fits");
            Equal(cap.Id, answer.Causes.Single().Cap!.Id, "the cap");
            Equal(0, world.Rows().Count, "nothing reserved");
            Equal(0, (await world.Approvals.StopsAsync("chat-1")).Count, "and no stop recorded");
            var json = JsonSerializer.Serialize(answer, SettingsStore.JsonOptions);
            Check(json.Contains("\"verdict\":\"ask\"", StringComparison.Ordinal) && json.Contains("\"pane\":\"sc-request\"", StringComparison.Ordinal) &&
                json.Contains("\"code\":\"spend_ask\"", StringComparison.Ordinal) && json.Contains("\"billing\":\"metered\"", StringComparison.Ordinal), "its JSON: " + json);

            await world.Guard.ApproveStoredAsync(new SpendApproval("chat-1", SpendChoice.CapOutput) { MaxOutputTokens = answer.FitOutputTokens });
            var approved = await SpendGuardEndpoints.PreflightAsync(world.Guard, call, cheaper: false);
            Check(approved.Allowed && approved.Approval is { Choice: SpendChoice.CapOutput } && approved.Estimate.OutputBasis == OutputTokenBasis.Chosen,
                "with the approval waiting, preflight allows it at the chosen output cap");
            Equal(1, (await world.Approvals.WaitingAsync("chat-1")).Count, "the approval is still unused");
            Equal(0, world.Rows().Count, "still nothing reserved");

            var body = new SpendPreflightBody("metered", "m", null, null, " chat-1 ", null, 10, "héllo", 100, null, null).ToCall();
            Equal((16L, SpendSource.Chat, "chat-1", (int?)100), (body.InputBytes, body.Source, body.Run, body.MaxTokens), "a body counts the draft's bytes only");
            Check(!new SpendPreflightBody("p", "m", null, null, null, null, null, "secret draft", null, null, null).ToString().Contains("secret", StringComparison.Ordinal),
                "a body never shows the draft");
            Throws<ArgumentException>(() => new SpendPreflightBody("p", "m", null, "email", null, null, null, null, null, null, null).ToCall(), "an unknown source");
            Throws<ArgumentException>(() => new SpendPreflightBody("p", "m", null, null, null, null, -1, null, null, null, null).ToCall(), "a negative size");
            Equal(SpendChoice.CapOutput, new SpendApprovalBody("chat-1", "cap-output", 10, null).ToApproval().Choice, "a body's choice");
            Throws<ArgumentException>(() => new SpendApprovalBody("chat-1", "always", null, null).ToApproval(), "an unknown choice");
            Throws<ArgumentException>(() => new SpendApprovalBody("chat-1", "cap-output", null, null).ToApproval(), "capping needs a token count");
            Throws<ArgumentException>(() => new SpendApprovalBody(" ", "proceed-once", null, null).ToApproval(), "an approval needs a run");
        }),

        Case("spend.guard.compare-group-reserves-all-or-none", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-group");
            await world.CapAsync(CapPeriod.Run, 100_000, CapAction.Block);
            var sends = 0;
            var model = GuardModel("metered", "m");
            GuardedStreamSend send = (guarded, token) =>
            {
                Interlocked.Increment(ref sends);
                return GuardAnswer(model, "column " + guarded.Call.Op, GuardUsage(10, 5));
            };
            IReadOnlyList<SpendCall> Columns(int count, string run) =>
                [.. Enumerable.Range(1, count).Select(index => GuardCall($"{run}-col-{index}", run, source: "compare", maxTokens: 1000))];

            await using (var group = await world.Executor.ReserveGroupAsync(Columns(6, "cmp-1")))
            {
                Check(!group.Reserved && group.Leases.All(lease => !lease.Reserved), "six columns over the run cap: none reserved");
                Equal(SpendErrors.Blocked, group.Reservation.Refused!.ErrorCode, "blocked");
                foreach (var lease in group.Leases)
                    Check(await GuardCollect(lease.StreamAsync(model.Descriptor, send)) is [StreamError { Message.Model: "m" }], "each column ends with the spend error");
            }
            Equal(0, sends, "nothing was sent");
            Equal(0, world.Rows().Count, "nothing was reserved");

            await using (var group = await world.Executor.ReserveGroupAsync(Columns(3, "cmp-2")))
            {
                Check(group.Reserved && group.Leases.All(lease => lease.Reserved), "three columns fit: all reserved");
                foreach (var lease in group.Leases.Take(2)) Check((await GuardCollect(lease.StreamAsync(model.Descriptor, send)))[^1] is StreamDone, "a column runs");
                Throws<InvalidOperationException>(() => group.Leases[0].StreamAsync(model.Descriptor, send), "a lease is sent once");
            }
            Equal(2, sends, "two columns sent");
            Equal("cmp-2-col-1:settled,cmp-2-col-2:settled,cmp-2-col-3:released", string.Join(",", world.Rows().OrderBy(row => row.Op, StringComparer.Ordinal).Select(row => row.Op + ":" + row.State)),
                "the column never started is released when the group is disposed");

            var single = await GuardCollect(world.Executor.StreamAsync(GuardCall("solo", "cmp-3", source: "playground", maxTokens: 1000), model.Descriptor, send));
            Check(single[^1] is StreamDone, "one call through the executor");
            Equal(3, sends, "sent");
            var partial = GuardPartial(model);
            var recorded = new PiSharp.AI.RecordedChatTransport([new StreamStarted(partial), new TextStarted(0, new TextContent("")), new TextDelta(0, "recorded"),
                new StreamDone(StopReason.Stop, partial with { Content = [new TextContent("recorded")], Usage = GuardUsage(10, 5), StopReason = StopReason.Stop })]);
            var replayed = await GuardCollect(world.Executor.StreamAsync(GuardCall("replay", "cmp-4", source: "playground", maxTokens: 1000), model.Descriptor,
                (guarded, token) => recorded.StreamAsync(new PiSharp.AI.ChatRequest(model.Descriptor, []), token)));
            Equal((4, ReservationState.Settled, 105L), (replayed.Count, (await world.Ledger.GetAsync("replay"))!.State, (await world.Ledger.GetAsync("replay"))!.ActualMicros ?? -1),
                "a recorded transport's stream passes through and settles");
            var completed = await world.Executor.CompleteAsync(GuardCall("solo-2", "cmp-3", source: "bench", maxTokens: 1000), model.Descriptor, (guarded, token) =>
                Task.FromResult(new AssistantMessage(model.Api, model.Provider, model.Id, 1, [new TextContent("done")], GuardUsage(10, 5), StopReason.Stop)));
            Equal((StopReason.Stop, ReservationState.Settled, SpendSource.Bench), (completed.StopReason, (await world.Ledger.GetAsync("solo-2"))!.State, (await world.Ledger.GetAsync("solo-2"))!.Source),
                "a completion through the executor settles");
            await ThrowsAsync<ArgumentException>(() => world.Executor.ReserveGroupAsync([GuardCall("a", "r1"), GuardCall("b", "r2")]), "a group belongs to one run");
            await ThrowsAsync<ArgumentException>(() => world.Executor.ReserveGroupAsync([GuardCall("a", "r1"), GuardCall("a", "r1")]), "two calls never share an op");
        }),

        Case("spend.guard.recovered-compare-columns-keep-the-chosen-output-cap", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-group-recovery");
            // Three columns at 4,000 output tokens ($0.0633 each) ask under a $0.10 run cap; capped at 500 ($0.0108 each) they fit.
            await world.CapAsync(CapPeriod.Run, 100_000, CapAction.Ask);
            IReadOnlyList<SpendCall> columns = [.. Enumerable.Range(1, 3).Select(index => GuardCall($"cmp-col-{index}", "cmp-r", source: "compare", maxTokens: 4000))];
            var asked = await world.Guard.ReserveGroupAsync(columns);
            Check(!asked.Reserved && asked.Refused?.ErrorCode == SpendErrors.Ask, "the group asks first");
            var approval = await world.Guard.ApproveStoredAsync(new SpendApproval("cmp-r", SpendChoice.CapOutput) { MaxOutputTokens = 500 });
            var approved = await world.Guard.ReserveGroupAsync(columns);
            Check(approved.Reserved, "capped, the group fits");
            Equal("500,500,500", string.Join(",", approved.Items.Select(item => item.MaxTokens)), "every column is sent with the chosen output cap");
            foreach (var column in columns)
                Equal((500, approval.Id), ((await world.Ledger.GetAsync(column.Op))?.OutputTokens ?? -1, (await world.Approvals.UsedByAsync(column.Op))?.Id ?? -1),
                    "reserved at the capped output, with the approval recorded against " + column.Op);

            // The process dies before the columns end; the next start finds their reservations (startup recovery made them uncertain).
            world.Restart();
            var recovered = await world.Guard.ReserveGroupAsync(columns);
            Check(recovered.Reserved && recovered.Items.All(item => item.Reservation.Existing), "the recovered group reuses every reservation");
            Equal("500,500,500", string.Join(",", recovered.Items.Select(item => item.MaxTokens)), "each recovered column keeps the chosen output cap");
            Check(recovered.Items.All(item => item.Approval?.Id == approval.Id), "and the approval that let it through");

            // Each column re-runs as a durable request under its own op after the restart.
            var script = new GuardScript((call, token) => GuardAnswer(call.Model, "column", GuardUsage(10, 5)));
            var models = world.Over(script);
            foreach (var column in columns)
                Check((await GuardCollect(models.StreamSimple(GuardModel("metered", "m"), GuardMessages(3000), GuardOptions(column.Op, "cmp-r", source: "compare", maxTokens: 4000),
                    CancellationToken.None)))[^1] is StreamDone, "a recovered column is resent");
            Equal("500,500,500", string.Join(",", script.Calls.Select(call => call.Options!.MaxTokens)), "each recovered column is resent with the capped maxTokens");
            Equal("cmp-col-1:uncertain,cmp-col-2:uncertain,cmp-col-3:uncertain", string.Join(",", world.Rows().OrderBy(row => row.Op, StringComparer.Ordinal).Select(row => row.Op + ":" + row.State)),
                "nothing reserved twice, and the rows wait for the user");

            // An approval whose group still could not be sent let nothing through.
            var refused = await world.Guard.ApproveStoredAsync(new SpendApproval("cmp-x", SpendChoice.CapOutput) { MaxOutputTokens = 500 });
            var taken = (await world.Approvals.TakeAsync("cmp-x", null, ["cmp-x-1", " cmp-x-2 ", "cmp-x-1"]))!;
            Equal((refused.Id, "cmp-x-1", refused.Id), (taken.Id, taken.UsedBy, (await world.Approvals.UsedByAsync("cmp-x-2"))?.Id ?? -1), "taken for both columns");
            await world.Approvals.MarkUsedByAsync(taken.Id, SpendApprovalStore.Refused);
            Check(await world.Approvals.UsedByAsync("cmp-x-1") is null && await world.Approvals.UsedByAsync("cmp-x-2") is null, "refused: no column keeps it");
            await ThrowsAsync<ArgumentException>(() => world.Approvals.TakeAsync("cmp-x", null, Array.Empty<string>()), "an approval lets at least one reservation through");

            // An approval used before the uses table existed is carried over from used_by.
            var root = Temp("spend-guard-uses-upgrade");
            try
            {
                var path = Path.Join(root, "webagent.db");
                var before = new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration, SpendGuardMigrations.Approvals, SpendGuardMigrations.Stops]);
                before.Migrate();
                before.Execute("""
                    INSERT INTO spend_approvals (run, op, choice, max_output_tokens, created_at, expires_at, used_at, used_by) VALUES
                      ('r', NULL, 'cap-output', 300, 1, 2, 1, 'op-old'), ('r', NULL, 'cap-output', 200, 1, 2, 1, 'refused');
                    """);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var after = new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration, .. SpendGuardMigrations.All]);
                Equal("1102,1103", string.Join(",", after.Migrate()), "the upgrade applies the uses table and the submission column");
                var store = new SpendApprovalStore(after);
                Equal(300, (await store.UsedByAsync("op-old"))?.Approval.MaxOutputTokens ?? -1, "the old approval is found by its reservation");
                Check(await store.UsedByAsync(SpendApprovalStore.Refused) is null, "a refused one is not carried over");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("spend.guard.deferred-answer-keeps-the-reservation", async () =>
        {
            await using var world = GuardWorld.Create("spend-guard-deferred");
            var model = GuardModel("metered", "m");
            var handle = JsonFields.Empty.Set("deferred", JsonData.Parse("{\"provider\":\"metered\",\"modelId\":\"m\",\"api\":\"test-api\",\"id\":\"h1\"}"));
            var script = new GuardScript((call, token) => GuardAnswer(call.Model, "", null, StopReason.Deferred, handle))
            {
                Fetch = deferred => new AssistantMessage(model.Api, model.Provider, model.Id, 2, [new TextContent("later")], GuardUsage(10, 5), StopReason.Stop),
            };
            var models = world.Over(script);
            var events = await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-d", "c"), CancellationToken.None));
            Check(events[^1] is StreamDone { Message.StopReason: StopReason.Deferred }, "a deferred response");
            Equal(ReservationState.Reserved, (await world.Ledger.GetAsync("op-d"))!.State, "keeps its reservation");
            var final = await models.FetchDeferredAsync(model, new DurableDeferredHandle(ChordObject.Empty.SetItem("id", "h1")), CancellationToken.None);
            Equal(StopReason.Stop, final.StopReason, "the deferred answer");
            Equal((ReservationState.Settled, 105L), ((await world.Ledger.GetAsync("op-d"))!.State, (await world.Ledger.GetAsync("op-d"))!.ActualMicros ?? -1), "settles it");

            await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-e", "c"), CancellationToken.None));
            await models.CancelDeferredAsync(model, new DurableDeferredHandle(ChordObject.Empty.SetItem("id", "h1")), CancellationToken.None);
            Equal(ReservationState.Uncertain, (await world.Ledger.GetAsync("op-e"))!.State, "a cancelled deferred answer is uncertain");
        }),

        Case("spend.guard.messages-say-billing-and-parse", () =>
        {
            var estimate = new SpendEstimate { Provider = "metered", Model = "m", Billing = BillingKind.Metered, Price = GuardPrices.Rates, InputTokens = 1100, OutputTokens = 8192, WorstMicros = 126_180 };
            var cap = new SpendCap(7, CapScope.Everything, null, CapPeriod.Day, 100_000, CapAction.Ask);
            var decision = SpendDecision.From(estimate, [new SpendCause { Kind = SpendCauseKind.Cap, Action = CapAction.Ask, Cap = cap, SpentMicros = 0, LeftMicros = 100_000 }]);
            var message = SpendGuard.ErrorMessage(decision, "op;500]");
            Check(message.StartsWith("Spend Guard billing check: spend_ask: ", StringComparison.Ordinal), message);
            var parsed = SpendErrors.Parse(message)!;
            Equal((SpendErrors.Ask, "op500", (long?)7), (parsed.Code, parsed.Op, parsed.CapId), "the code, the cleaned op and the cap");
            Check(SpendErrors.Display(message).StartsWith("This request could cost up to", StringComparison.Ordinal), "the sentence");
            Throws<ArgumentException>(() => SpendGuard.ErrorMessage(SpendDecision.Allow(estimate), "op"), "an allowed call has no spend error");
            var failure = SpendGuard.UncheckedMessage(new InvalidOperationException("database is locked sk-proj-abcdefghijklmnopqrstuvwxyz0123456789"));
            Check(failure.Contains("billing", StringComparison.Ordinal) && !SpendErrors.IsSpend(failure) && !failure.Contains("sk-proj-abcdefghijklmnop", StringComparison.Ordinal),
                "a guard failure is a billing error without a code or a secret: " + failure);
        }),

        Case("spend.guard.app-guards-a-chat-end-to-end", async () =>
        {
            var root = Temp("spend-guard-app");
            try
            {
                var data = Path.Join(root, "data");
                Directory.CreateDirectory(data);
                Directory.CreateDirectory(Path.Join(root, "work"));
                var path = Path.Join(data, "webagent.db");
                var setup = new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration]);
                setup.Migrate();
                await new SettingsStore(setup).SaveAsync(new ToolsSettings(Path.Join(root, "work"), ToolApprovalMode.Ask, []));
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data, Arguments = ["--faux-metered"] });

                var created = await app.SendAsync(HttpMethod.Post, "/api/chats", new JsonObject { ["requestId"] = "r1", ["text"] = "hello" });
                Equal(200, created.Status, "created: " + created.Text);
                var chatId = created.Body!["id"]!.GetValue<string>();
                await GuardIdleAsync(app, chatId, "Echo: hello");
                var first = ReadDb(path, "SELECT op, source, run, state, billing, reserved_micros, input_tokens FROM spend_reservations",
                    reader => (Op: reader.GetString(0), Source: reader.GetString(1), Run: reader.IsDBNull(2) ? null : reader.GetString(2), State: reader.GetString(3),
                        Billing: reader.GetString(4), Reserved: reader.GetInt64(5), Input: reader.GetDouble(6)));
                Check(first.Count >= 1 && first.All(row => row is { Source: "chat", State: "settled", Billing: "metered" } && row.Run == chatId),
                    "the chat's requests were reserved and settled under its id: " + string.Join(" | ", first));

                // A per-request Block cap the next answer (8,192 output tokens at $15/M) does not fit, but a 100-token one does.
                var inputMicros = (long)Math.Ceiling(first.Max(row => row.Input) * 3);
                var limit = inputMicros + 10_000;
                // Written to the app's database directly (a second ledger would run startup recovery against the running app's rows).
                var capId = new WebAgentDb(path, []).Transaction((connection, transaction) =>
                {
                    WebAgentDb.Execute(connection, transaction, """
                        INSERT INTO spend_caps (scope, key, period, limit_micros, action, enabled, created_at, updated_at)
                        VALUES ('everything', NULL, 'request', $limit, 'block', 1, 0, 0);
                        """, ("$limit", limit));
                    return Convert.ToInt64(WebAgentDb.Scalar(connection, transaction, "SELECT last_insert_rowid();"), System.Globalization.CultureInfo.InvariantCulture);
                });

                var preflight = await app.SendAsync(HttpMethod.Post, "/api/spend/preflight",
                    new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["run"] = chatId, ["inputBytes"] = 3000, ["cheaper"] = true });
                Equal(200, preflight.Status, "preflight: " + preflight.Text);
                Equal(("block", "spend_blocked", false, "sc-request"), (preflight.Body!["verdict"]!.GetValue<string>(), preflight.Body["code"]!.GetValue<string>(),
                    preflight.Body["canProceed"]!.GetValue<bool>(), preflight.Body["pane"]!.GetValue<string>()), "preflight blocks it: " + preflight.Text);
                Equal("no-store", preflight.Response.Headers.CacheControl?.ToString(), "never cached");
                Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/spend/preflight", text: "{}", contentType: "text/plain")).Status, "commands are JSON");
                Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/spend/approvals", new JsonObject { ["run"] = chatId, ["choice"] = "proceed-once" }, origin: "http://evil.example")).Status,
                    "no cross-origin approval");
                var invalid = await app.SendAsync(HttpMethod.Post, "/api/spend/preflight", new JsonObject { ["provider"] = "faux" });
                Equal((400, "invalid"), (invalid.Status, invalid.Body?["error"]?["code"]?.GetValue<string>()), "a call needs a model: " + invalid.Text);

                var blocked = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages", new JsonObject { ["requestId"] = "m1", ["text"] = "blocked please" });
                Equal(200, blocked.Status, "sent: " + blocked.Text);
                JsonNode? stops = null;
                await WaitUntilAsync(async () =>
                {
                    stops = (await app.Get("/api/spend/stops?run=" + chatId)).Body;
                    var chat = await app.Get("/api/chats/" + chatId);
                    return stops?["stops"]?.AsArray().Count > 0 && chat.Body?["running"]?.GetValue<bool>() == false;
                }, "the chat's request is stopped", TimeSpan.FromSeconds(30));
                var stop = stops!["stops"]![0]!;
                Equal(("spend_blocked", capId, "faux", "echo"), (stop["decision"]!["code"]!.GetValue<string>(), stop["decision"]!["causes"]![0]!["cap"]!["id"]!.GetValue<long>(),
                    stop["provider"]!.GetValue<string>(), stop["model"]!.GetValue<string>()), "the stop names the cap and the model asked for");
                var byOp = await app.Get("/api/spend/stops/" + Uri.EscapeDataString(stop["op"]!.GetValue<string>()));
                Equal((200, "sc-request"), (byOp.Status, byOp.Body?["decision"]?["pane"]?.GetValue<string>()), "the stop by its op");
                Equal(404, (await app.Get("/api/spend/stops/no-such-op")).Status, "an unknown stop");
                var export = (await app.Get($"/chat/{chatId}/export.md")).Text;
                Check(!export.Contains("Echo: blocked please", StringComparison.Ordinal), "nothing answered the blocked message: " + export);
                Check(ReadDb(path, "SELECT COUNT(*) FROM spend_reservations WHERE state <> 'settled'", reader => reader.GetInt64(0)).Single() == 0, "nothing reserved for it");

                var approval = await app.SendAsync(HttpMethod.Post, "/api/spend/approvals",
                    new JsonObject { ["run"] = chatId, ["choice"] = "cap-output", ["maxOutputTokens"] = 100 });
                Equal((200, "cap-output"), (approval.Status, approval.Body?["approvals"]?[0]?["choice"]?.GetValue<string>()), "approved: " + approval.Text);
                Equal(1, (await app.Get("/api/spend/approvals?run=" + chatId)).Body?["approvals"]?.AsArray().Count, "waiting");
                Equal(400, (await app.SendAsync(HttpMethod.Post, "/api/spend/approvals", new JsonObject { ["run"] = chatId, ["choice"] = "always" })).Status, "an unknown choice");

                var resent = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages", new JsonObject { ["requestId"] = "m2", ["text"] = "after cap" });
                Equal(200, resent.Status, "sent: " + resent.Text);
                await GuardIdleAsync(app, chatId, "Echo: after cap");
                var capped = ReadDb(path, "SELECT note, output_tokens, state FROM spend_reservations WHERE note IS NOT NULL", reader => (Note: reader.GetString(0), Output: reader.GetInt64(1), State: reader.GetString(2)));
                Check(capped.Count == 1 && capped[0] is { Note: "asked · output capped at 100 tokens", Output: 100, State: "settled" }, "sent once at the chosen output cap: " + string.Join(" | ", capped));
                Equal(0, (await app.Get("/api/spend/approvals?run=" + chatId)).Body?["approvals"]?.AsArray().Count, "the approval was used");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),
    ];

    /// <summary>Waits until the chat shows <paramref name="text"/> and is idle.</summary>
    private static async Task GuardIdleAsync(WebAgentApp app, string chatId, string text) =>
        await WaitUntilAsync(async () =>
        {
            var chat = await app.Get("/api/chats/" + chatId);
            var markdown = (await app.Get($"/chat/{chatId}/export.md")).Text;
            return markdown.Contains(text, StringComparison.Ordinal) && chat.Body?["running"]?.GetValue<bool>() == false;
        }, $"chat {chatId} shows {text}", TimeSpan.FromSeconds(30));

    private static DurableModel GuardModel(string provider, string id) => new() { Id = id, Name = id, Api = "test-api", Provider = provider, MaxTokens = 32_000 };

    /// <summary>One user message of about <paramref name="bytes"/> bytes as wire JSON.</summary>
    private static ImmutableArray<ChordJson> GuardMessages(int bytes) => [ChordJson.Object([new("role", "user"), new("content", new string('x', Math.Max(1, bytes - 30)))])];

    private static DurableStreamOptions GuardOptions(string op, string? run, string source = "chat", double? maxTokens = null) => new()
    {
        Metadata = ChordObject.Empty.SetItem("user_id", "u").SetItem(SessionStreamMetadata.Key, SessionStreamMetadata.Fields(("op", op), ("source", source), ("run", run))),
        MaxTokens = maxTokens,
    };

    private static SpendCall GuardCall(string op, string? run, string provider = "metered", string source = "chat", int? maxTokens = null) => new()
    {
        Provider = provider,
        Model = "m",
        Source = SpendSources.Parse(source),
        Run = run,
        Op = op,
        InputBytes = CostEstimator.InputBytes(GuardMessages(3000)),
        MaxTokens = maxTokens,
    };

    private static TokenUsage GuardUsage(int input, int output) => FauxPricing.Usage(input, output, FauxPricing.Price);

    private static string? SpendStreamText(AssistantMessage message) =>
        message.ExtraProperties is { } extra && extra.TryGet("errorMessage", out var value) && value is { Value.ValueKind: JsonValueKind.String } ? value.Value.GetString() : null;

    private static async Task<List<StreamEvent>> GuardCollect(IAsyncEnumerable<StreamEvent> stream)
    {
        var events = new List<StreamEvent>();
        await foreach (var item in stream) events.Add(item);
        return events;
    }

    private static AssistantMessage GuardPartial(DurableModel model) => new(model.Api, model.Provider, model.Id, 1, [], TokenUsage.Zero, StopReason.Pending);

    private static async IAsyncEnumerable<StreamEvent> GuardAnswer(DurableModel model, string text, TokenUsage? usage, StopReason reason = StopReason.Stop, JsonFields? extra = null)
    {
        await Task.Yield();
        var partial = GuardPartial(model);
        yield return new StreamStarted(partial);
        if (text.Length > 0)
        {
            yield return new TextStarted(0, new TextContent(""));
            yield return new TextDelta(0, text);
            yield return new TextEnded(0, text);
        }
        yield return new StreamDone(reason, partial with { Content = text.Length > 0 ? [new TextContent(text)] : [], Usage = usage ?? TokenUsage.Zero, StopReason = reason, ExtraProperties = extra });
    }

    /// <summary>A stream that fails before any output, as its op says: <c>op-error</c> ends with a provider error, <c>op-throw</c> throws, any
    /// other op waits for its cancel.</summary>
    private static async IAsyncEnumerable<StreamEvent> GuardBeforeOutput(DurableModel model, string metadata, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        var partial = GuardPartial(model);
        yield return new StreamStarted(partial);
        if (metadata.Contains("\"op-error\"", StringComparison.Ordinal))
        {
            yield return new StreamError(StopReason.Error, partial with
            {
                StopReason = StopReason.Error, ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"HTTP 500: upstream failed\"")),
            });
            yield break;
        }
        if (metadata.Contains("\"op-throw\"", StringComparison.Ordinal)) throw new IOException("the connection was reset");
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    /// <summary>A stream that ends after <paramref name="text"/>, as its op starts: <c>op-eof</c> just stops, <c>op-error</c> ends with a
    /// provider error, <c>op-throw</c> throws, <c>op-aborted</c> ends aborted, any other op waits for its cancel. The error and aborted
    /// terminals carry <paramref name="usage"/> (none by default): the partial usage a real provider reports before the end (Anthropic's
    /// message_start).</summary>
    private static async IAsyncEnumerable<StreamEvent> GuardAfterOutput(DurableModel model, string metadata, string text, [EnumeratorCancellation] CancellationToken cancellationToken,
        TokenUsage? usage = null)
    {
        await Task.Yield();
        var partial = GuardPartial(model) with { Usage = usage ?? TokenUsage.Zero };
        yield return new StreamStarted(partial);
        yield return new TextStarted(0, new TextContent(""));
        yield return new TextDelta(0, text);
        if (metadata.Contains("\"op-eof", StringComparison.Ordinal)) yield break;
        if (metadata.Contains("\"op-error", StringComparison.Ordinal))
        {
            yield return new StreamError(StopReason.Error, partial with
            {
                Content = [new TextContent(text)], StopReason = StopReason.Error, ExtraProperties = JsonFields.Empty.Set("errorMessage", JsonData.Parse("\"socket hang up\"")),
            });
            yield break;
        }
        if (metadata.Contains("\"op-throw", StringComparison.Ordinal)) throw new IOException("the connection was reset");
        if (metadata.Contains("\"op-aborted", StringComparison.Ordinal))
        {
            yield return new StreamError(StopReason.Aborted, partial with { Content = [new TextContent(text)], StopReason = StopReason.Aborted });
            yield break;
        }
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    /// <summary>One request the scripted models received.</summary>
    private sealed record GuardSent(DurableModel Model, ImmutableArray<ChordJson> Messages, DurableStreamOptions? Options);

    /// <summary>Models that answer from a script and record what they were sent (no provider, no network).</summary>
    private sealed class GuardScript(Func<GuardSent, CancellationToken, IAsyncEnumerable<StreamEvent>> respond) : IDurableModels
    {
        public ConcurrentQueue<GuardSent> Calls { get; } = new();

        public Func<DurableDeferredHandle, AssistantMessage>? Fetch { get; init; }

        public DurableModel? GetModel(string provider, string modelId) => GuardModel(provider, modelId);

        public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<ChordJson> messages, DurableStreamOptions? options, CancellationToken cancellationToken)
        {
            var sent = new GuardSent(model, messages, options);
            Calls.Enqueue(sent);
            return respond(sent, cancellationToken);
        }

        public async Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<ChordJson> messages, DurableStreamOptions? options,
            CancellationToken cancellationToken)
        {
            StreamTerminalEvent? end = null;
            await foreach (var item in StreamSimple(model, messages, options, cancellationToken)) if (item is StreamTerminalEvent terminal) end = terminal;
            return end?.Message ?? throw new InvalidOperationException("The script ended without a final message.");
        }

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The script streams no deferred answer.");

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            Task.FromResult(Fetch!(handle));

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Prices every call at $3 / $15 per million tokens: provider <c>sub</c> is a subscription (list price), <c>free</c> is free,
    /// <c>unknown</c> has no price, any other is metered. Output: the call's maxTokens, else the reservation cap (8,192).</summary>
    private sealed class GuardPrices : ICostEstimator
    {
        public static SpendPrice Rates { get; } = new(3, 15);

        public Task<SpendEstimate> EstimateAsync(SpendEstimateInput input, CancellationToken cancellationToken = default)
        {
            var call = input.Normalize();
            var billing = call.Provider switch
            {
                "sub" => BillingKind.Subscription,
                "free" => BillingKind.Free,
                "unknown" => BillingKind.Unknown,
                _ => BillingKind.Metered,
            };
            var price = billing switch
            {
                BillingKind.Unknown => null,
                BillingKind.Free => new SpendPrice(0, 0),
                _ => Rates,
            };
            var tokens = CostEstimator.InputTokens(call.InputBytes);
            var output = call.MaxTokens ?? 8192;
            return Task.FromResult(new SpendEstimate
            {
                Provider = call.Provider,
                Model = call.Model,
                Account = call.Account,
                Billing = billing,
                Price = price,
                InputTokens = tokens,
                OutputTokens = output,
                OutputBasis = call.MaxTokens is null ? OutputTokenBasis.ReservationCap : call.ChosenMaxTokens ? OutputTokenBasis.Chosen : OutputTokenBasis.Request,
                WorstMicros = price is null ? null : billing == BillingKind.Free ? 0 : price.WorstMicros(tokens, output),
            });
        }
    }

    /// <summary>A clock the cases move.</summary>
    private sealed class GuardClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    /// <summary>A temporary webagent.db with the ledger's and the guard's tables, and a guard over it on a movable clock.</summary>
    private sealed class GuardWorld : IAsyncDisposable
    {
        private static readonly IWebAgentMigration[] Migrations = [CoreMigrations.AppInfo, SettingsStore.Migration, .. SpendLedgerMigrations.All, .. SpendGuardMigrations.All];

        private GuardWorld(string root)
        {
            Root = root;
            Path = System.IO.Path.Join(root, "webagent.db");
            Db = new WebAgentDb(Path, Migrations);
            Db.Migrate();
            Settings = new SettingsStore(Db);
            Restart();
        }

        public string Root { get; }

        public string Path { get; }

        public WebAgentDb Db { get; }

        public SettingsStore Settings { get; }

        public GuardClock Clock { get; } = new(new DateTimeOffset(2026, 10, 29, 11, 0, 0, TimeSpan.Zero));

        public GuardPrices Prices { get; } = new();

        public SpendLedger Ledger { get; private set; } = null!;

        public SpendApprovalStore Approvals { get; private set; } = null!;

        public SpendGuard Guard { get; private set; } = null!;

        public GuardedRunExecutor Executor { get; private set; } = null!;

        public static GuardWorld Create(string name) => new(Temp(name));

        /// <summary>A new ledger, approval store and guard on the same file, as the next start of the app opens them (the ledger runs startup
        /// recovery before its first call).</summary>
        public void Restart()
        {
            var db = new WebAgentDb(Path, Migrations);
            Ledger = new SpendLedger(db, Settings, Clock);
            Approvals = new SpendApprovalStore(db, Clock);
            Guard = new SpendGuard(Prices, Ledger, Approvals, Clock);
            Executor = new GuardedRunExecutor(Guard);
        }

        public GuardedDurableModels Over(IDurableModels inner) => new(inner, Executor);

        public Task<SpendCap> CapAsync(CapPeriod period, long micros, CapAction action) =>
            Ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, period, micros, action));

        public List<(string Op, string State)> Rows() =>
            ReadDb(Path, "SELECT op, state FROM spend_reservations ORDER BY created_at, op", reader => (reader.GetString(0), reader.GetString(1)));

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteTemp(Root);
            return ValueTask.CompletedTask;
        }
    }
}
