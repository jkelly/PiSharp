using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.AI.ModelOperations;
using PiSharp.CodingAgent.Usage;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

// Usage totals, cache statistics and cache warming: Pi v1.1.0 packages/coding-agent/src/core/{usage-totals,cache-stats,
// cache-warmer}.ts. The cache-stats and cache-warmer groups port test/cache-stats.test.ts and test/cache-warmer.test.ts
// (fake timers become ManualTimeProvider); usage totals have no upstream test and are authored from the source.
internal static partial class Program
{
    private static readonly SessionEntryCodec UsageCodec = new();

    internal static string Num(double value) => JsonSerializer.Serialize(value);
    internal static string Num(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string UsageJson(long input = 0, long output = 0, long cacheRead = 0, long cacheWrite = 0, double costInput = 0,
        double costOutput = 0, double costCacheRead = 0, double costCacheWrite = 0, double costTotal = 0, long? totalTokens = null, string extra = "") =>
        $$$"""{"input":{{{input}}},"output":{{{output}}},"cacheRead":{{{cacheRead}}},"cacheWrite":{{{cacheWrite}}},"totalTokens":{{{totalTokens ?? input + output + cacheRead + cacheWrite}}}{{{extra}}},"cost":{"input":{{{Num(costInput)}}},"output":{{{Num(costOutput)}}},"cacheRead":{{{Num(costCacheRead)}}},"cacheWrite":{{{Num(costCacheWrite)}}},"total":{{{Num(costTotal)}}}}}""";

    internal static string UsageJson(TokenUsage usage) =>
        $$$"""{"input":{{{usage.Input}}},"output":{{{usage.Output}}},"cacheRead":{{{usage.CacheRead}}},"cacheWrite":{{{usage.CacheWrite}}},"totalTokens":{{{usage.TotalTokens}}},"cost":{"input":{{{Num(usage.Cost.Input)}}},"output":{{{Num(usage.Cost.Output)}}},"cacheRead":{{{Num(usage.Cost.CacheRead)}}},"cacheWrite":{{{Num(usage.Cost.CacheWrite)}}},"total":{{{Num(usage.Cost.Total)}}}}}""";

    internal static SessionEntry AssistantEntry(string id, string usage, string provider = "test", string model = "test-model", long timestamp = 0,
        string extra = "", string stopReason = "stop", string entryTimestamp = "2026-01-01T00:00:00.000Z") =>
        UsageCodec.Parse($$$"""{"type":"message","id":"{{{id}}}","parentId":null,"timestamp":"{{{entryTimestamp}}}","message":{"role":"assistant","content":[],"api":"anthropic-messages","provider":"{{{provider}}}","model":"{{{model}}}","usage":{{{usage}}},"stopReason":"{{{stopReason}}}","timestamp":{{{timestamp}}}{{{extra}}}}}""");

    internal static SessionEntry UsageEntry(string id, string kind, string usage, string timestamp, string provider = "test", string model = "test-model",
        string? note = null) =>
        UsageCodec.Parse($$"""{"type":"usage","id":"{{id}}","parentId":null,"timestamp":"{{timestamp}}","kind":"{{kind}}","provider":"{{provider}}","model":"{{model}}","usage":{{usage}}{{(note is null ? "" : ",\"note\":" + JsonSerializer.Serialize(note))}}}""");

    private static SessionEntry CompactionEntry(string id, string usage = "") =>
        UsageCodec.Parse($$"""{"type":"compaction","id":"{{id}}","parentId":null,"timestamp":"","summary":"s","firstKeptEntryId":"a","tokensBefore":0{{(usage.Length == 0 ? "" : ",\"usage\":" + usage)}}}""");

    private static IEnumerable<(string, Func<Task>)> UsageTotalsCases()
    {
        yield return Case("usage-totals/add-and-combine", () =>
        {
            var totals = UsageTotalsCalculator.CreateUsageTotals();
            UsageTotalsCalculator.AddUsageToTotals(totals, new(10, 20, 30, 40, 100, new(0.1m, 0.2m, 0.3m, 0.4m, 1m)));
            UsageTotalsCalculator.AddUsageToTotals(totals, new(1, 2, 3, 4, 10, new(0, 0, 0, 0, 0.5m)));
            Equal((11d, 22d, 33d, 44d, 1.5), (totals.Input, totals.Output, totals.CacheRead, totals.CacheWrite, totals.Cost), "totals");
            UsageTotalsCalculator.AddWireUsageToTotals(totals, JsonDocument.Parse(UsageJson(1, 1, 1, 1, costTotal: 0.25)).RootElement);
            Equal((12d, 23d, 34d, 45d, 1.75), (totals.Input, totals.Output, totals.CacheRead, totals.CacheWrite, totals.Cost), "wire usage");
            var split = JsonFields.Empty.Set("cacheWrite1h", JsonData.Parse("7")).Set("other", JsonData.Parse("1"));
            var combined = UsageTotalsCalculator.CombineUsage(new(1, 2, 3, 10, 16, new(1, 2, 3, 4, 10), split), new(1, 1, 1, 1, 4, new(1, 1, 1, 1, 4)));
            Equal((2L, 3L, 4L, 11L, 20L), (combined.Input, combined.Output, combined.CacheRead, combined.CacheWrite, combined.TotalTokens), "combined tokens");
            Equal((2m, 3m, 4m, 5m, 14m), (combined.Cost.Input, combined.Cost.Output, combined.Cost.CacheRead, combined.Cost.CacheWrite, combined.Cost.Total), "combined cost");
            Equal("cacheWrite1h=7", string.Join(",", combined.ExtraProperties!.Values.Select(pair => pair.Key + "=" + pair.Value)), "only the optional splits survive");
            var reasoning = UsageTotalsCalculator.CombineUsage(new(0, 0, 0, 0, 0, new(0, 0, 0, 0, 0), JsonFields.Empty.Set("reasoning", JsonData.Parse("5"))),
                new(0, 0, 0, 0, 0, new(0, 0, 0, 0, 0), JsonFields.Empty.Set("reasoning", JsonData.Parse("6"))));
            Equal("reasoning=11", string.Join(",", reasoning.ExtraProperties!.Values.Select(pair => pair.Key + "=" + pair.Value)), "reasoning sum");
            Check(UsageTotalsCalculator.CombineUsage(TokenUsage.Zero, TokenUsage.Zero).ExtraProperties is null, "no splits when neither reports them");
        });

        yield return Case("usage-totals/cost-breakdown", () =>
        {
            var toolResult = UsageCodec.Parse($$$"""{"type":"message","id":"t","parentId":null,"timestamp":"","message":{"role":"toolResult","toolCallId":"c","toolName":"x","isError":false,"content":[],"timestamp":0,"usage":{{{UsageJson(input: 5, costTotal: 0.25)}}}}}""");
            var branch = UsageCodec.Parse($$"""{"type":"branch_summary","id":"b","parentId":null,"timestamp":"","fromId":"a","summary":"s","usage":{{UsageJson(output: 3, costTotal: 0.5)}}}""");
            var breakdown = UsageTotalsCalculator.GetUsageCostBreakdown([
                AssistantEntry("a1", UsageJson(input: 100, output: 10, costTotal: 1)),
                AssistantEntry("a2", UsageJson(input: 50, costTotal: 2), extra: ",\"responseModel\":\"served-model\""),
                AssistantEntry("a3", UsageJson(cacheRead: 7, costTotal: 0.5)),
                AssistantEntry("a4", UsageJson(), provider: "free", model: "zero"),
                UsageEntry("u", "cache_warm", UsageJson(cacheRead: 9, costTotal: 0.75), "2026-01-01T00:00:00.000Z", "anthropic", "claude-opus-4-6"),
                toolResult, branch, CompactionEntry("c", UsageJson(input: 1, costTotal: 0.25)), CompactionEntry("c2"),
            ]);
            Equal("test/served-model=2/50;test/test-model=1.5/117;Tools/summaries=1/9;anthropic/claude-opus-4-6=0.75/9",
                string.Join(";", breakdown.Select(item => $"{item.Key}={item.Cost.ToString(CultureInfo.InvariantCulture)}/{item.Tokens}")), "breakdown");
        });

        yield return Case("usage-totals/usage-entry-shares-model-key", () =>
        {
            var breakdown = UsageTotalsCalculator.GetUsageCostBreakdown([
                AssistantEntry("a", UsageJson(input: 1, costTotal: 1)),
                UsageEntry("u", "cache_warm", UsageJson(cacheRead: 1, costTotal: 1), "2026-01-01T00:00:00.000Z")]);
            Equal("test/test-model=2/2", string.Join(";", breakdown.Select(item => $"{item.Key}={item.Cost.ToString(CultureInfo.InvariantCulture)}/{item.Tokens}")),
                "a usage entry adds to the provider/model bucket");
        });
    }

    // ----- cache-stats.test.ts -----

    private static readonly IModelPriceSource CacheReadPrice = new DelegateModelPriceSource((_, _) => new ModelCost(0, 0, 0.3, 0));

    private static SessionEntry StatsTurn(string id, long input = 0, long cacheRead = 0, long cacheWrite = 0, double costCacheRead = 0,
        double costCacheWrite = 0, string model = "test-model", long timestamp = 0) =>
        AssistantEntry(id, UsageJson(input, 10, cacheRead, cacheWrite, costCacheRead: costCacheRead, costCacheWrite: costCacheWrite, totalTokens: 0), model: model, timestamp: timestamp);

    private static AssistantMessage StatsMessage(long input = 0, long cacheRead = 0, long cacheWrite = 0, decimal costCacheRead = 0,
        decimal costCacheWrite = 0, string model = "test-model", long timestamp = 0) =>
        new("anthropic-messages", "test", model, timestamp, [], new(input, 10, cacheRead, cacheWrite, 0, new(0, 0, costCacheRead, costCacheWrite, 0)), StopReason.Stop);

    private static IEnumerable<(string, Func<Task>)> CacheStatsCases()
    {
        // Turn 1: fresh 100k cache write at $3.75/M; turn 2: healthy, everything read back at $0.30/M.
        SessionEntry Turn1() => StatsTurn("t1", cacheWrite: 100_000, costCacheWrite: 0.375, timestamp: 0);
        SessionEntry Turn2() => StatsTurn("t2", cacheRead: 100_000, cacheWrite: 5_000, costCacheRead: 0.03, costCacheWrite: 0.019, timestamp: 60_000);
        static void Close(double expected, double actual, string what) => Check(Math.Abs(expected - actual) < 5e-6, $"{what}: {actual}");

        yield return Case("cache-stats/accumulates-missed-tokens-and-cost", () =>
        {
            var totals = CacheStats.ComputeCacheWaste([Turn1(), Turn2(), StatsTurn("t3", cacheWrite: 110_000, costCacheWrite: 0.4125, timestamp: 120_000)], CacheReadPrice);
            Equal(105_000d, totals.MissedTokens, "missed tokens");
            Close(0.36225, totals.MissedCost, "105k at ($3.75 - $0.30)/M");
        });
        yield return Case("cache-stats/healthy-sessions-count-nothing", () =>
        {
            var totals = CacheStats.ComputeCacheWaste([Turn1(), Turn2()], CacheReadPrice);
            Equal((0d, 0d), (totals.MissedTokens, totals.MissedCost), "healthy");
        });
        yield return Case("cache-stats/skips-turn-after-compaction", () =>
            Equal(0d, CacheStats.ComputeCacheWaste([Turn1(), CompactionEntry("c"), StatsTurn("r", cacheWrite: 20_000, costCacheWrite: 0.075)], CacheReadPrice).MissedTokens,
                "reset"));
        yield return Case("cache-stats/counts-model-switches", () =>
        {
            var totals = CacheStats.ComputeCacheWaste([Turn1(), StatsTurn("o", cacheWrite: 100_000, costCacheWrite: 0.375, model: "other-model")], CacheReadPrice);
            Equal((100_000d, 1), (totals.MissedTokens, totals.MissCount), "model switch");
        });
        yield return Case("cache-stats/skips-providers-without-cache-reports", () =>
            Equal(0d, CacheStats.ComputeCacheWaste([StatsTurn("a", input: 100_000), StatsTurn("b", input: 110_000)], CacheReadPrice).MissedTokens, "no cache"));
        yield return Case("cache-stats/collects-misses-by-entry", () =>
        {
            var miss = StatsTurn("m", cacheWrite: 110_000, costCacheWrite: 0.4125, timestamp: 120_000);
            var misses = CacheStats.CollectCacheMisses([Turn1(), Turn2(), miss], CacheReadPrice);
            Equal(1, misses.Count, "count");
            Equal(105_000d, misses[miss].MissedTokens, "keyed by the paying entry");
        });
        yield return Case("cache-stats/detects-miss-with-idle-time", () =>
        {
            var miss = CacheStats.DetectCacheMiss([Turn1(), Turn2()], StatsMessage(cacheWrite: 110_000, costCacheWrite: 0.4125m, timestamp: 600_000), CacheReadPrice);
            Check(miss is not null, "miss");
            Equal(105_000d, miss!.MissedTokens, "tokens"); Close(0.36225, miss.MissedCost, "cost");
            Equal(540_000d, miss.IdleMs, "600s - 60s"); Equal(false, miss.ModelChanged, "model");
        });
        yield return Case("cache-stats/flags-model-switches", () =>
        {
            var miss = CacheStats.DetectCacheMiss([Turn1(), Turn2()], StatsMessage(cacheWrite: 110_000, costCacheWrite: 0.4125m, model: "other-model", timestamp: 120_000), CacheReadPrice);
            Equal((105_000d, true), (miss!.MissedTokens, miss.ModelChanged), "switch");
        });
        yield return Case("cache-stats/only-cache-warm-usage-refreshes", () =>
        {
            var message = StatsMessage(cacheWrite: 110_000, costCacheWrite: 0.4125m, timestamp: 600_000);
            var warmUsage = UsageJson(cacheRead: 100_000);
            var afterWarm = CacheStats.DetectCacheMiss([Turn1(), UsageEntry("w", "cache_warm", warmUsage, "1970-01-01T00:08:20.000Z")], message, CacheReadPrice);
            var afterOther = CacheStats.DetectCacheMiss([Turn1(), UsageEntry("o", "custom_operation", warmUsage, "1970-01-01T00:08:20.000Z")], message, CacheReadPrice);
            Equal((100_000d, 600_000d), (afterWarm!.IdleMs, afterOther!.IdleMs), "idle after usage entries");
        });
        yield return Case("cache-stats/healthy-turn-and-first-turn", () =>
        {
            Check(CacheStats.DetectCacheMiss([Turn1(), Turn2()], StatsMessage(cacheRead: 105_000, cacheWrite: 2_000, costCacheRead: 0.0315m, costCacheWrite: 0.0075m, timestamp: 120_000),
                CacheReadPrice) is null, "healthy");
            Check(CacheStats.DetectCacheMiss([], StatsMessage(cacheWrite: 100_000, costCacheWrite: 0.375m), CacheReadPrice) is null, "first turn");
        });
        yield return Case("cache-stats/price-fallback-and-noise-floor", () =>
        {
            // A cache-read-only provider (OpenAI style) reports a total miss as plain input; the read price comes from the model.
            var miss = CacheStats.DetectCacheMiss([StatsTurn("a", input: 1_000, cacheRead: 50_000, costCacheRead: 0.015)],
                new AssistantMessage("openai-responses", "test", "test-model", 0, [], new(60_000, 0, 0, 0, 0, new(0.075m, 0, 0, 0, 0)), StopReason.Stop), CacheReadPrice);
            Equal(51_000d, miss!.MissedTokens, "tokens"); Close(51_000 * (0.075 / 60_000 - 0.3 / 1_000_000), miss.MissedCost, "fallback read price");
            Check(CacheStats.DetectCacheMiss([Turn1()], StatsMessage(cacheRead: 98_976, cacheWrite: 5_000), CacheReadPrice) is null, "1024 missed tokens are noise");
            Equal(CacheStats.CacheTtlMs, 300_000d, "TTL");
        });
    }

    // ----- cache-warmer.test.ts -----

    private const string AdaptiveModelJson = """{"id":"claude-opus-4-6","name":"Claude Opus 4.6","provider":"anthropic","api":"anthropic-messages","reasoning":true,"cost":{"input":5,"output":25,"cacheRead":0.5,"cacheWrite":6.25},"compat":{"forceAdaptiveThinking":true,"supportsStrictTools":true},"promptCache":{"short":300,"long":3600}}""";
    private const string BudgetModelJson = """{"id":"claude-sonnet-4-5","provider":"anthropic","api":"anthropic-messages","cost":{"input":3,"output":15,"cacheRead":0.3,"cacheWrite":3.75},"compat":{"supportsStrictTools":true},"promptCache":{"short":300,"long":3600}}""";
    private const string OpenAIModelJson = """{"id":"gpt-5","provider":"openai","api":"openai-responses","cost":{"input":1.25,"output":10,"cacheRead":0.125,"cacheWrite":0},"promptCache":{"short":300,"long":86400}}""";
    private static CacheWarmModel WarmModel(string json) => CacheWarmModel.FromJson(JsonDocument.Parse(json).RootElement);
    private static readonly CacheWarmModel AdaptiveModel = WarmModel(AdaptiveModelJson), BudgetModel = WarmModel(BudgetModelJson), OpenAIModel = WarmModel(OpenAIModelJson);
    private static readonly CacheWarmModel UnknownModel = AdaptiveModel with { PromptCacheShortSeconds = null, PromptCacheLongSeconds = null };
    private static readonly TokenUsage WarmUsage = new(0, 1, 100, 0, 101, new(0, 0, 0.01m, 0, 0.01m));

    private static AssistantMessage WarmResponse(CacheWarmModel model, StopReason stopReason = StopReason.Length) =>
        new(model.Api, model.Provider, model.Id, 0, [], WarmUsage, stopReason);

    private static IReadOnlyList<SessionEntry> BranchWithPrompt(long promptTokens) =>
        [AssistantEntry("a", UsageJson(output: 10, cacheRead: promptTokens, costCacheRead: 0.01, costTotal: 0.01), "anthropic", "claude-opus-4-6")];

    private sealed class WarmHarness
    {
        internal readonly List<(CacheWarmRequest Request, CacheWarmReplayOverrides Overrides, CancellationToken Signal)> Calls = [];
        internal readonly List<CacheWarmingDecisionEvent> Events = [];
        internal readonly List<SessionEntry> Warmed = [];
        internal readonly List<(string Kind, string Provider, string Model, TokenUsage Usage, string? Note, SessionEntry Entry)> Appended = [];
        internal string Mode = CacheWarmingModes.Idle;
        internal IReadOnlyList<SessionEntry> Branch = BranchWithPrompt(100_000);
        internal readonly ManualTimeProvider Time = new();
        internal CacheWarmer Warmer = null!;

        internal WarmHarness(Func<CacheWarmModel, Task<AssistantMessage>>? result = null, Func<CacheWarmingDecisionEvent, Task<string?>>? decide = null,
            string? mode = null, IReadOnlyList<SessionEntry>? branch = null)
        {
            if (mode is not null) Mode = mode;
            if (branch is not null) Branch = branch;
            var count = 0;
            Warmer = new((request, overrides, signal) =>
                {
                    Calls.Add((request, overrides, signal));
                    return (result ?? (model => Task.FromResult(WarmResponse(model))))(request.Model);
                },
                (kind, provider, model, usage, note) =>
                {
                    var entry = UsageEntry("usage-" + ++count, kind, UsageJson(usage), "2026-01-01T00:00:00.000Z", provider, model, note);
                    Appended.Add((kind, provider, model, usage, note, entry));
                    return Task.FromResult(entry);
                },
                () => Branch, () => Mode,
                async decisionEvent => { Events.Add(decisionEvent); return (decide is null ? null : await decide(decisionEvent)) ?? decisionEvent.Action; },
                Time, _ => null);
            Warmer.OnWarmed = Warmed.Add;
        }

        internal Task Advance(double ms) => Time.AdvanceAsync(ms, () => Warmer.LastRefresh);

        internal Task RefreshNow() => Warmer.Refresh(Warmer.ActiveRunForTests ?? throw new InvalidOperationException("expected an active cache-warming run"));
    }

    private static CacheWarmRequest WarmRequest(CacheWarmModel? model = null, CacheWarmRequestOptions? options = null, object? payload = null) =>
        new(model ?? AdaptiveModel, options ?? new(), payload);

    private static IEnumerable<(string, Func<Task>)> CacheWarmerCases()
    {
        static bool Current() => true;
        static void Close(double expected, double actual, string what) => Check(Math.Abs(expected - actual) < 1e-9, $"{what}: {actual}");

        yield return Case("cache-warmer/eligibility-and-timing", () =>
        {
            string? NoEnv(string _) => null;
            Equal("300000,3600000,null,3600000,86400000,null", string.Join(",", new[]
            {
                CacheWarmer.GetPromptCacheTtlMs(AdaptiveModel, null, NoEnv),
                CacheWarmer.GetPromptCacheTtlMs(AdaptiveModel, new("long"), NoEnv),
                CacheWarmer.GetPromptCacheTtlMs(AdaptiveModel, new("none"), NoEnv),
                CacheWarmer.GetPromptCacheTtlMs(AdaptiveModel, new(Env: new Dictionary<string, string?> { ["PI_CACHE_RETENTION"] = "long" }), NoEnv),
                CacheWarmer.GetPromptCacheTtlMs(OpenAIModel, new("long"), NoEnv),
                CacheWarmer.GetPromptCacheTtlMs(UnknownModel, null, NoEnv),
            }.Select(value => value?.ToString(CultureInfo.InvariantCulture) ?? "null")), "ttl");
            Equal(3_600_000d, CacheWarmer.GetPromptCacheTtlMs(AdaptiveModel, null, name => name == "PI_CACHE_RETENTION" ? "long" : null), "process PI_CACHE_RETENTION");
            Equal(300_000d, CacheWarmer.GetPromptCacheTtlMs(AdaptiveModel, new(Env: new Dictionary<string, string?> { ["PI_CACHE_RETENTION"] = "" }), _ => "short"), "empty scoped value falls through");
            Equal("270000,50000,null", string.Join(",", new[] { 300_000d, 60_000, 10_000 }.Select(ttl => CacheWarmer.GetCacheWarmingDelayMs(ttl)?.ToString(CultureInfo.InvariantCulture) ?? "null")), "delay");
            Equal("False,True,True,True", string.Join(",", CacheWarmer.IsReplayable(BudgetModel, new(Reasoning: "medium")), CacheWarmer.IsReplayable(BudgetModel, null),
                CacheWarmer.IsReplayable(AdaptiveModel, new(Reasoning: "medium")), CacheWarmer.IsReplayable(OpenAIModel, new(Reasoning: "medium"))), "replayable");
            Equal("streaming|streaming|off|idle", string.Join("|", CacheWarmingModes.Resolve(null), CacheWarmingModes.Resolve("always"), CacheWarmingModes.Resolve("off"),
                CacheWarmingModes.Resolve("idle")), "modes");
        });

        yield return Case("cache-warmer/replays-profitable-requests-preserving-options", async () =>
        {
            var harness = new WarmHarness();
            var payload = new object(); using var original = new CancellationTokenSource();
            var request = WarmRequest(AdaptiveModel, new(Reasoning: "high"), payload);
            harness.Warmer.Start(request, Current);
            Equal("scheduled", harness.Warmer.Status.State, "armed");
            await harness.Advance(270_000);
            Equal(1, harness.Calls.Count, "one replay");
            Check(ReferenceEquals(harness.Calls[0].Request, request) && ReferenceEquals(harness.Calls[0].Request.Payload, payload), "request replayed exactly as sent");
            Equal(new CacheWarmReplayOverrides(1, 0), harness.Calls[0].Overrides, "maxTokens 1, maxRetries 0");
            Check(harness.Calls[0].Signal != original.Token && harness.Calls[0].Signal.CanBeCanceled, "the run's own signal");
            Equal(("cache_warming_decision", 1d, "warm"), (harness.Events[0].Type, harness.Events[0].ContinuationProbability, harness.Events[0].Action), "event");
            Close(0.575, harness.Events[0].MissCost, "miss cost"); Close(0.050025, harness.Events[0].WarmCost, "warm cost");
            Equal(("cache_warm", "anthropic", "claude-opus-4-6", (string?)null), (harness.Appended[0].Kind, harness.Appended[0].Provider, harness.Appended[0].Model, harness.Appended[0].Note), "appendUsage");
            Equal(WarmUsage, harness.Appended[0].Usage, "usage persisted");
            Check(harness.Warmed.Count == 1 && ReferenceEquals(harness.Warmed[0], harness.Appended[0].Entry), "onWarmed gets the persisted entry");
            await harness.Advance(270_000);
            Equal(2, harness.Calls.Count, "repeated refresh");
            harness.Warmer.Cancel();
            Equal("Inactive (inactive)", CacheWarmer.FormatCacheWarmingStatus(harness.Warmer.Status, 0), "cancelled");
        });

        yield return Case("cache-warmer/no-refresh-after-safe-deadline", async () =>
        {
            var harness = new WarmHarness();
            harness.Warmer.Start(WarmRequest(), Current);
            // A five-minute cache is scheduled for 4m30s and retains 15 seconds of the 30-second expiry margin.
            harness.Time.SetUtcNow(285_001); harness.Time.ClearTimers();
            await harness.RefreshNow();
            Equal(0, harness.Calls.Count, "no call");
            Equal(("inactive", "cache refresh deadline missed"), (harness.Warmer.Status.State, harness.Warmer.Status.Reason), "status");
        });

        yield return Case("cache-warmer/rechecks-deadline-after-extension-decision", async () =>
        {
            WarmHarness harness = null!;
            harness = new(decide: async _ => { await Task.Yield(); harness.Time.SetUtcNow(285_001); return "warm"; });
            harness.Warmer.Start(WarmRequest(), Current);
            harness.Time.ClearTimers();
            await harness.RefreshNow();
            Equal(0, harness.Calls.Count, "no call");
            Equal(("inactive", "cache refresh deadline missed"), (harness.Warmer.Status.State, harness.Warmer.Status.Reason), "status");
        });

        yield return Case("cache-warmer/economic-decisions-and-extension-overrides", async () =>
        {
            var unprofitable = new WarmHarness(branch: BranchWithPrompt(5_000));
            unprofitable.Warmer.Start(WarmRequest(), Current);
            await unprofitable.Advance(270_000);
            var status = unprofitable.Warmer.Status;
            Equal((0, "inactive", "stop", true, false), (unprofitable.Calls.Count, status.State, status.Decision!.Action, status.Decision.EconomicsAvailable, status.ExtensionOverride == true),
                "unprofitable");
            Equal("expected savings below threshold", status.Reason, "reason");
            Equal("Stopped (100% continuation probability while agent is running, expected savings $0.026 < $0.050 -> stop)",
                CacheWarmer.FormatCacheWarmingStatus(status, 0), "stopped status text");

            var forced = new WarmHarness(branch: BranchWithPrompt(5_000), decide: _ => Task.FromResult<string?>("warm"));
            forced.Warmer.Start(WarmRequest(), Current);
            await forced.Advance(270_000);
            Equal(1, forced.Calls.Count, "forced");
            Equal("extension override", forced.Appended[0].Note, "note");
            Equal("Cache warmed (extension override): $0.010", CacheWarmer.FormatCacheWarmingUsage(forced.Warmed[0]), "usage text");
            forced.Warmer.Cancel();

            var vetoed = new WarmHarness(decide: _ => Task.FromResult<string?>("stop"));
            vetoed.Warmer.Start(WarmRequest(), Current);
            await vetoed.Advance(270_000);
            Equal((0, "inactive", true, "stopped by extension"), (vetoed.Calls.Count, vetoed.Warmer.Status.State, vetoed.Warmer.Status.ExtensionOverride == true, vetoed.Warmer.Status.Reason), "vetoed");
            Equal("Stopped (extension override, 100% continuation probability while agent is running, expected savings $0.525 >= $0.050)",
                CacheWarmer.FormatCacheWarmingStatus(vetoed.Warmer.Status, 0), "vetoed text");

            var unavailable = new WarmHarness(branch: BranchWithPrompt(0));
            unavailable.Warmer.Start(WarmRequest(), Current);
            Equal(("inactive", "cache economics unavailable"), (unavailable.Warmer.Status.State, unavailable.Warmer.Status.Reason), "unavailable");
            await unavailable.Advance(270_000);
            Equal(0, unavailable.Calls.Count, "no call");
            Equal("Inactive (cache economics unavailable)", CacheWarmer.FormatCacheWarmingStatus(unavailable.Warmer.Status, 0), "unavailable text");

            var failing = new WarmHarness(decide: _ => throw new InvalidOperationException("extension failed"));
            failing.Warmer.Start(WarmRequest(), Current);
            await failing.Advance(270_000);
            Equal(1, failing.Calls.Count, "a failing extension falls back to pi's decision");
            failing.Warmer.Cancel();
        });

        yield return Case("cache-warmer/stops-for-unsupported-context-and-mode-changes", async () =>
        {
            var unsupported = new WarmHarness { Mode = CacheWarmingModes.Off };
            unsupported.Warmer.Start(WarmRequest(), Current);
            Equal("cache warming disabled", unsupported.Warmer.Status.Reason, "off");
            unsupported.Mode = CacheWarmingModes.Idle;
            unsupported.Warmer.Start(WarmRequest(UnknownModel), Current);
            Equal("cache lifetime unavailable", unsupported.Warmer.Status.Reason, "unknown lifetime");
            unsupported.Warmer.Start(WarmRequest(AdaptiveModel, new("none")), Current);
            Equal("request disabled prompt caching", unsupported.Warmer.Status.Reason, "retention none");
            unsupported.Warmer.Start(WarmRequest(BudgetModel, new(Reasoning: "high")), Current);
            Equal("request cannot be replayed safely", unsupported.Warmer.Status.Reason, "budget thinking");

            var stillCurrent = true;
            unsupported.Warmer.Start(WarmRequest(), () => stillCurrent);
            stillCurrent = false;
            Equal("conversation context changed", unsupported.Warmer.Status.Reason, "context changed");
            await unsupported.Advance(270_000);
            Equal(0, unsupported.Calls.Count, "no call after context change");

            unsupported.Warmer.Start(WarmRequest(), Current);
            unsupported.Mode = CacheWarmingModes.Off;
            await unsupported.Advance(270_000);
            Equal(0, unsupported.Calls.Count, "no call after mode off");

            var streaming = new WarmHarness(mode: CacheWarmingModes.Streaming, branch: BranchWithPrompt(400_000));
            streaming.Warmer.Start(WarmRequest(), Current);
            streaming.Warmer.OnAgentSettled();
            Equal("agent run settled", streaming.Warmer.Status.Reason, "streaming settles");

            var idle = new WarmHarness(branch: BranchWithPrompt(400_000));
            idle.Warmer.Start(WarmRequest(), Current);
            idle.Warmer.OnAgentSettled();
            Equal(("scheduled", "idle", 0.15), (idle.Warmer.Status.State, idle.Warmer.Status.Decision!.Phase, idle.Warmer.Status.Decision.ContinuationProbability), "idle keeps warming");
            Equal("Decision in 4m 30s (15% continuation probability, expected savings $0.145 >= $0.050 -> warm)",
                CacheWarmer.FormatCacheWarmingStatus(idle.Warmer.Status, 0), "idle status text");
            idle.Mode = CacheWarmingModes.Streaming; idle.Warmer.OnModeChanged();
            Equal("agent run settled", idle.Warmer.Status.Reason, "mode change to streaming stops an idle run");
        });

        yield return Case("cache-warmer/safety-windows", async () =>
        {
            var idle = new WarmHarness(branch: BranchWithPrompt(400_000));
            idle.Warmer.Start(WarmRequest(), Current);
            idle.Warmer.OnAgentSettled();
            for (var refresh = 0; refresh < 10; refresh++) await idle.Advance(270_000);
            // Refreshes at 4m30s steps; the one after 27m would be due past the 30-minute idle horizon.
            Equal((6, "30-minute idle safety limit reached"), (idle.Calls.Count, idle.Warmer.Status.Reason), "idle horizon");

            var streaming = new WarmHarness(branch: BranchWithPrompt(400_000));
            streaming.Warmer.Start(WarmRequest(), Current);
            for (var refresh = 0; refresh < 20; refresh++) await streaming.Advance(270_000);
            Equal((13, "one-hour safety limit reached"), (streaming.Calls.Count, streaming.Warmer.Status.Reason), "streaming horizon");
        });

        yield return Case("cache-warmer/aborts-replaced-requests-and-skips-failed-refreshes", async () =>
        {
            var release = new TaskCompletionSource<AssistantMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new WarmHarness(result: _ => release.Task);
            pending.Warmer.Start(WarmRequest(), Current);
            await pending.Time.AdvanceAsync(270_000, () => Task.CompletedTask);
            Equal("refreshing", pending.Warmer.Status.State, "in flight");
            pending.Warmer.Start(WarmRequest(), Current);
            Check(pending.Calls[0].Signal.IsCancellationRequested, "replaced request aborted");
            release.SetResult(WarmResponse(AdaptiveModel));
            await pending.Warmer.LastRefresh;
            Equal(0, pending.Appended.Count, "a replaced refresh is not recorded");
            pending.Warmer.Cancel();
            await pending.Advance(600_000);
            Equal(1, pending.Calls.Count, "no further refreshes");

            var failed = new WarmHarness(result: model => Task.FromResult(WarmResponse(model, StopReason.Error)));
            failed.Warmer.Start(WarmRequest(), Current);
            await failed.Advance(270_000);
            Equal((1, 0), (failed.Calls.Count, failed.Appended.Count), "failed refreshes are not recorded");
            Equal("scheduled", failed.Warmer.Status.State, "warming continues");
            failed.Warmer.Cancel();

            var throwing = new WarmHarness(result: _ => Task.FromException<AssistantMessage>(new HttpRequestException("offline")));
            throwing.Warmer.Start(WarmRequest(), Current);
            await throwing.Advance(270_000);
            Equal(("scheduled", 0), (throwing.Warmer.Status.State, throwing.Appended.Count), "errors are best effort");
            throwing.Warmer.Cancel();
        });

        yield return Case("cache-warmer/formats-status-and-usage", () =>
        {
            var decision = new CacheWarmingDecision("idle", 0.013, 0.621, 0.6, 0.36, true, "warm");
            Equal("Decision in 3m 42s (60% continuation probability, expected savings $0.360 >= $0.050 -> warm)",
                CacheWarmer.FormatCacheWarmingStatus(new("scheduled", null, 222_000, decision), 0), "scheduled");
            Equal("Decision in 1h 1s (60% continuation probability, expected savings $0.360 >= $0.050 -> warm)",
                CacheWarmer.FormatCacheWarmingStatus(new("scheduled", null, 3_600_500, decision), 0), "hours");
            Equal("Decision now (60% continuation probability, expected savings $0.360 >= $0.050 -> warm)",
                CacheWarmer.FormatCacheWarmingStatus(new("scheduled", null, 0, decision), 0), "now");
            Equal("Warming cache (60% continuation probability, expected savings -$0.100 < $0.050 -> stop)",
                CacheWarmer.FormatCacheWarmingStatus(new("refreshing", null, 0, decision with { ExpectedSavings = -0.1, Action = "stop" }), 0), "negative");
            Equal("Inactive (unknown reason)", CacheWarmer.FormatCacheWarmingStatus(new("inactive"), 0), "no reason");
            var usage = new TokenUsage(0, 1, 100, 0, 101, new(0.00004m, 0.00005m, 0.02940725m, 0, 0.02949725m));
            var entry = UsageEntry("w", "cache_warm", UsageJson(usage), "2026-01-01T00:00:00.000Z", "anthropic", "claude-opus-4-6", "extension override");
            Equal("Cache warmed (extension override): $0.029497", CacheWarmer.FormatCacheWarmingUsage(entry), "usage");
            Equal("Cache warmed: $0.012", CacheWarmer.FormatCacheWarmingUsage(UsageEntry("x", "cache_warm", UsageJson(costTotal: 0.012), "2026-01-01T00:00:00.000Z")), "trailing zeros");
            Equal("""{"type":"cache_warming_decision","warmCost":0.05,"missCost":0.5,"continuationProbability":0.15,"action":"warm"}""",
                new CacheWarmingDecisionEvent(0.05, 0.5, 0.15, "warm").ToJson().ToString(), "event json");
        });
    }
}

/// <summary>A manual clock and timer queue standing in for vitest fake timers. Timers fire in due order while advancing.</summary>
internal sealed class ManualTimeProvider(long startMs = 0) : TimeProvider
{
    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        internal DateTimeOffset? Due;
        internal void Fire() { Due = null; callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + dueTime; return true; }
        public void Dispose() => Due = null;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.FromUnixTimeMilliseconds(startMs);

    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void SetUtcNow(long ms) => _now = DateTimeOffset.FromUnixTimeMilliseconds(ms);
    public void ClearTimers() { foreach (var timer in _timers) timer.Dispose(); }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period); _timers.Add(timer);
        return timer;
    }

    /// <summary>Advance the clock, firing due timers in order and awaiting <paramref name="settle"/> after each.</summary>
    public async Task AdvanceAsync(double ms, Func<Task> settle)
    {
        var target = _now.AddMilliseconds(ms);
        while (true)
        {
            var next = _timers.Where(timer => timer.Due is { } due && due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
            if (next is null) break;
            _now = next.Due!.Value;
            next.Fire();
            await settle();
        }
        _now = target;
    }
}
