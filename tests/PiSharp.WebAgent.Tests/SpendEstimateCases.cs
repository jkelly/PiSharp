using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Spend;
using AuthKind = PiSharp.WebAgent.Contracts.ProviderAuthKind;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

// Decision 0011 (run W3, S1): the Spend Guard's estimator and billing modes. Input tokens are UTF-8 bytes / 3 x 1.1 rounded up; the worst
// case uses the higher of the input and cache-write rates, the higher price tier from 10% below a boundary (actual costs keep
// calculateCost's strict rule, checked against ModelCost.Calculate), and the request's maxTokens, else the reservation cap, never more than
// the model's maximum; the mockups' worked examples come out exactly and every formula shown adds up. A model without a price is Unknown
// until a price is entered; typical is the median of the ledger's last 20 actual costs. Billing: subscriptions (ChatGPT, Copilot, any
// OAuth), API, environment and ambient keys Metered, faux and loopback servers Free, no price Unknown, and an override wins. Cheaper models
// are suggestions only. Most cases use in-memory fakes; the real catalog's tiers and the real accounts run through RoutingHost (fake
// environment, fake provider), and the module is composed as Program.cs composes it. No provider call, no real key.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SpendEstimateCases() =>
    [
        Case("spend.estimate.input-tokens-and-the-worked-examples", async () =>
        {
            Equal(0d, CostEstimator.InputTokens(0), "no input");
            Equal(0d, CostEstimator.InputTokens(-5), "never below 0");
            Equal(2d, CostEstimator.InputTokens(3), "3 bytes are 1.1 tokens, rounded up");
            Equal(12_000d, CostEstimator.InputTokens(32_727), "32,727 bytes are 11,999.9 tokens, rounded up");
            Equal(12_001d, CostEstimator.InputTokens(32_730), "32,730 bytes are exactly 12,001 tokens");
            foreach (var tokens in new[] { 1, 9_900, 12_000, 89_999, 90_000, 100_000, 149_040, 450_000 })
                Equal((double)tokens, CostEstimator.InputTokens(EstBytes(tokens)), "bytes for " + tokens + " tokens");

            var world = new EstWorld(new EstCatalog().Add(EstProvider("anthropic", BillingKind.Metered),
                EstModel("anthropic", "claude-sonnet-5-5", new CatalogCost(2, 10)), EstModel("anthropic", "claude-opus-5-5", new CatalogCost(4, 20))));
            // settings-spend's example: 12,000 x $2.00/M + 8,192 x $10.00/M = $0.0240 + $0.0819 = $0.1059.
            var example = await world.Estimator.EstimateAsync(new SpendEstimateInput("anthropic", "claude-sonnet-5-5", null, EstBytes(12_000)));
            Equal(BillingKind.Metered, example.Billing, "an account-less metered provider");
            Equal(12_000d, example.InputTokens, "input tokens");
            Equal(8_192, example.OutputTokens, "the default reservation cap");
            Equal(OutputTokenBasis.ReservationCap, example.OutputBasis, "why");
            Equal(105_920L, example.WorstMicros, "the worst case in micro-USD");
            Check(example.PriceKnown && example.CountsTowardCaps && example.TypicalMicros is null, "known, counted, no typical without a ledger");
            Equal("12,000 × $2.00/M + 8,192 × $10.00/M = $0.0240 + $0.0819 = $0.1059", SpendEstimateText.Formula(example), "the worked example");
            Equal("≈ $0.11", SpendEstimateText.Chip(example), "the estimate by Send");
            // chat's reserved footer: 149,040 in x $2.00/M + 8,192 max out x $10.00/M = $0.38.
            var footer = world.Estimator.Estimate(new SpendEstimateInput("anthropic", "claude-sonnet-5-5", null, EstBytes(149_040)));
            Equal(380_000L, footer.WorstMicros, "$0.38 reserved");
            Equal("149,040 × $2.00/M + 8,192 × $10.00/M = $0.2981 + $0.0819 = $0.3800", SpendEstimateText.Formula(footer), "its formula");
            // chat-new's tooltip: about 9,900 input x $2/M + 8,192 output x $10/M = $0.1017.
            Equal("9,900 × $2.00/M + 8,192 × $10.00/M = $0.0198 + $0.0819 = $0.1017",
                SpendEstimateText.Formula(world.Estimator.Estimate(new SpendEstimateInput("anthropic", "claude-sonnet-5-5", null, EstBytes(9_900)))), "the composer's tooltip");
            // The confirm modal's period pane: 450,000 x $4 + 8,192 x $20 = $1.80 + $0.16 = $1.96 (two decimals from a dollar).
            var period = world.Estimator.Estimate(new SpendEstimateInput("anthropic", "claude-opus-5-5", null, EstBytes(450_000)));
            Equal(1_963_840L, period.WorstMicros, "$1.96");
            Equal("450,000 × $4.00/M + 8,192 × $20.00/M = $1.80 + $0.16 = $1.96", SpendEstimateText.Formula(period), "its formula");
        }),

        Case("spend.estimate.cache-write-rate-is-the-worst-input-rate", () =>
        {
            var world = new EstWorld(new EstCatalog().Add(EstProvider("anthropic", BillingKind.Metered),
                EstModel("anthropic", "writes-dearer", new CatalogCost(2, 10, 0.1, 2.5)),
                EstModel("anthropic", "no-cache-write", new CatalogCost(3, 15, 0.3, 0)),
                EstModel("anthropic", "writes-cheaper", new CatalogCost(1, 5, 0.1, 0.5))));
            var dearer = world.Estimator.Estimate(new SpendEstimateInput("anthropic", "writes-dearer", null, EstBytes(12_000)));
            Equal(2.5, dearer.Price!.WorstInput, "the cache-write rate is higher");
            Equal(111_920L, dearer.WorstMicros, "12,000 x $2.50/M + 8,192 x $10.00/M");
            Equal("12,000 × $2.50/M + 8,192 × $10.00/M = $0.0300 + $0.0819 = $0.1119", SpendEstimateText.Formula(dearer), "the formula names the rate used");
            Equal(158_880L, world.Estimator.Estimate(new SpendEstimateInput("anthropic", "no-cache-write", null, EstBytes(12_000))).WorstMicros,
                "without a cache-write rate the input rate applies");
            Equal(1.0, world.Estimator.Estimate(new SpendEstimateInput("anthropic", "writes-cheaper", null, EstBytes(12_000))).Price!.WorstInput,
                "a cheaper cache-write rate never lowers the input rate");
        }),

        Case("spend.estimate.tier-boundaries", () =>
        {
            const string haikuCost = """{"input":0.1,"output":0.5,"cacheRead":0.01,"cacheWrite":0.125,"tiers":[{"inputTokensAbove":100000,"input":0.5,"output":2.5,"cacheRead":0.05,"cacheWrite":0.625}]}""";
            var world = new EstWorld(new EstCatalog().Add(EstProvider("anthropic", BillingKind.Metered),
                    EstModel("anthropic", "claude-haiku-5-5", new CatalogCost(0.1, 0.5, 0.01, 0.125)),
                    EstModel("anthropic", "stepped", new CatalogCost(0.2, 1)),
                    EstModel("anthropic", "bad-tier", new CatalogCost(0.2, 1)),
                    EstModel("anthropic", "bad-json", new CatalogCost(0.2, 1))),
                new EstModels()
                    .Add("anthropic", "claude-haiku-5-5", costJson: haikuCost)
                    .Add("anthropic", "stepped", costJson: """{"input":0.2,"output":1,"cacheRead":0,"cacheWrite":0,"tiers":[{"inputTokensAbove":200000,"input":1,"output":4,"cacheRead":0,"cacheWrite":0},{"inputTokensAbove":100000,"input":0.5,"output":2,"cacheRead":0,"cacheWrite":0},{"inputTokensAbove":100000,"input":0.7,"output":3,"cacheRead":0,"cacheWrite":0},{"inputTokensAbove":-1,"input":9,"output":9,"cacheRead":0,"cacheWrite":0}]}""")
                    .Add("anthropic", "bad-tier", costJson: """{"input":0.2,"output":1,"cacheRead":0,"cacheWrite":0,"tiers":[{"inputTokensAbove":100,"input":-1,"output":2,"cacheRead":0,"cacheWrite":0}]}""")
                    .Add("anthropic", "bad-json", costJson: """{"input":"cheap","output":1}"""));
            SpendEstimate At(string model, int tokens) => world.Estimator.Estimate(new SpendEstimateInput("anthropic", model, null, EstBytes(tokens)));

            var below = At("claude-haiku-5-5", 89_999);
            Equal(null, below.Price!.TierInputTokensAbove, "more than 10% below the boundary: the base rates");
            Equal(0.125, below.Price.WorstInput, "the base cache-write rate");
            Equal(15_346L, below.WorstMicros, "89,999 x $0.125/M + 8,192 x $0.50/M, rounded up");
            Equal(null, SpendEstimateText.TierNote(below), "no tier note");
            foreach (var tokens in new[] { 90_000, 99_999, 100_000, 100_001, 500_000 })
            {
                var near = At("claude-haiku-5-5", tokens);
                Equal(100_000d, near.Price!.TierInputTokensAbove, tokens + " tokens use the tier");
                Equal(0.625, near.Price.WorstInput, tokens + " tokens: the tier's cache-write rate");
                Equal(2.5, near.Price.Output, tokens + " tokens: the tier's output rate");
            }
            Equal(76_730L, At("claude-haiku-5-5", 90_000).WorstMicros, "90,000 x $0.625/M + 8,192 x $2.50/M");
            Equal("Within 10% of Claude Haiku 5.5's 100K input-token tier boundary, so the worst case uses its higher price tier: $0.50 / $2.50 per 1M.",
                SpendEstimateText.TierNote(At("claude-haiku-5-5", 95_000), "Claude Haiku 5.5"), "near the boundary");
            Equal("Above 100K input tokens, Claude Haiku 5.5 uses its higher price tier: $0.50 / $2.50 per 1M.",
                SpendEstimateText.TierNote(At("claude-haiku-5-5", 100_001), "Claude Haiku 5.5"), "above it");

            // Actual costs keep calculateCost's rule (strictly above the threshold), as ModelCost.Calculate prices them.
            var haiku = world.Prices.Find("anthropic", "claude-haiku-5-5")!;
            Equal(1, haiku.Tiers.Count, "one tier read from the model JSON");
            var declared = ModelCost.FromJson(JsonDocument.Parse(haikuCost).RootElement);
            foreach (var tokens in new[] { 0d, 90_000, 99_999, 100_000, 100_001, 250_000 })
            {
                var expected = declared.Calculate(tokens, 1_000, 0, 0);
                var rates = haiku.ActualRates(tokens);
                Check(Math.Abs(rates.Input * tokens / 1e6 - expected.Input) < 1e-12 && Math.Abs(rates.Output * 1_000 / 1e6 - expected.Output) < 1e-12,
                    $"{tokens} tokens are billed at ModelCost.Calculate's rates");
                var usage = new SpendUsage(tokens, 1_000);
                Check(Math.Abs(haiku.CostMicros(usage) - expected.Total * 1e6) <= 0.5 + 1e-6, $"{tokens} tokens cost what calculateCost says");
            }
            Equal(null, haiku.ActualRates(100_000).TierInputTokensAbove, "exactly the threshold keeps the base rates");
            Equal(100_000d, haiku.ActualRates(100_001).TierInputTokensAbove, "one token more uses the tier");
            Equal(100_000d, haiku.ActualRates(60_000 + 40_001).TierInputTokensAbove, "input, cache reads and writes all count");
            Equal(46_001L, haiku.CostMicros(new SpendUsage(60_000, 1_000, 20_000, 20_001)),
                "100,001 input tokens in all: 60,000 x $0.50 + 1,000 x $2.50 + 20,000 x $0.05 + 20,001 x $0.625 = 46,000.625 micro-USD, rounded");

            // Several tiers: the greatest threshold that applies; the first of equal thresholds; a threshold of -1 never applies.
            var stepped = world.Prices.Find("anthropic", "stepped")!;
            Equal("200000,100000,100000", string.Join(",", stepped.Tiers.Select(tier => tier.TierInputTokensAbove)), "a -1 threshold never applies, so it is left out");
            Equal(0.2, stepped.ActualRates(0).Input, "nothing applies at 0");
            Equal(0.5, stepped.ActualRates(150_000).Input, "the first of two equal thresholds");
            Equal(1.0, stepped.ActualRates(250_000).Input, "the greatest threshold below the input");
            Equal(1.0, stepped.WorstRates(180_000).Input, "a worst case at 90% of the higher boundary");
            Equal(0.5, stepped.WorstRates(179_999).Input, "and just below that, the lower tier");
            Equal(4.0, At("stepped", 185_000).Price!.Output, "the estimate uses the higher tier's output rate too");
            // A tier that is not allowed drops the tiers; an unreadable cost is ignored; the catalog's base rates still apply.
            Equal(0, world.Prices.Find("anthropic", "bad-tier")!.Tiers.Count, "a negative tier rate drops the tiers");
            Equal(0.2, At("bad-tier", 5_000).Price!.Input, "the base rates apply");
            Equal(0, world.Prices.Find("anthropic", "bad-json")!.Tiers.Count, "an unreadable cost has no tiers");
            Check(world.Models!.Lookups > 0, "tiers were read through IDurableModels.GetModel");
        }),

        Case("spend.estimate.real-catalog-tiers-cache-writes-and-accounts", async () =>
        {
            var key = Planted("sk-ant-");
            await using var host = new RoutingHost("spend-real-catalog");
            var settings = host.Services.GetRequiredService<ISettingsStore>();
            var prices = new ModelPrices(host.Catalog, settings, () => host.Models);
            var billing = new BillingModes(host.Catalog, settings, prices, host.Accounts, () => host.Models);
            var estimator = new CostEstimator(host.Catalog, settings, billing, prices, host.Accounts);
            foreach (var (provider, model) in new[] { ("anthropic", "claude-haiku-5-5"), ("anthropic", "claude-sonnet-5-5"), ("openai-codex", "gpt-5.5") })
                Check(host.Catalog.FindModel(provider, model) is { MaxTokens: >= 8_192 }, $"{provider}/{model} is in the routed catalog");

            Equal(BillingKind.Metered, billing.Resolve("anthropic", null).Mode, "anthropic is metered");
            var anthropic = await host.AddKeyAsync("anthropic", key, "Work key");
            var resolved = billing.Resolve("anthropic", anthropic);
            Equal(new BillingResolution(BillingKind.Metered, BillingKind.Metered, false, "Stored API key"), resolved, "a stored key");
            var haikuBase = estimator.Estimate(new SpendEstimateInput("anthropic", "claude-haiku-5-5", anthropic, EstBytes(80_000)));
            Equal(null, haikuBase.Price!.TierInputTokensAbove, "80,000 tokens: Claude Haiku 5.5's base rates");
            Equal(14_096L, haikuBase.WorstMicros, "80,000 x $0.125/M + 8,192 x $0.50/M");
            var haikuTier = estimator.Estimate(new SpendEstimateInput("anthropic", "claude-haiku-5-5", anthropic, EstBytes(95_000)));
            Equal(100_000d, haikuTier.Price!.TierInputTokensAbove, "95,000 tokens: the catalog's 100K tier");
            Equal(79_855L, haikuTier.WorstMicros, "95,000 x $0.625/M + 8,192 x $2.50/M");
            var sonnet = estimator.Estimate(new SpendEstimateInput("anthropic", "claude-sonnet-5-5", anthropic, EstBytes(12_000)));
            Equal("12,000 × $2.50/M + 8,192 × $10.00/M = $0.0300 + $0.0819 = $0.1119", SpendEstimateText.Formula(sonnet),
                "Claude Sonnet 5.5's cache-write rate ($2.50) is its worst input rate");

            // A ChatGPT sign-in is a subscription: list price (with GPT-5.5's 272K tier), never counted.
            var personal = await host.AddCodexAsync("acct-personal", Planted("access-"), Planted("refresh-"), dueSoon: false);
            Equal(new BillingResolution(BillingKind.Subscription, BillingKind.Subscription, false, "ChatGPT sign-in"), billing.Resolve("openai-codex", personal), "ChatGPT");
            var plus = (await host.Vault.SaveSignInAsync("openai-codex", new OAuthCredentialSnapshot(Planted("access-"), Planted("refresh-"),
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), new Dictionary<string, string> { ["accountId"] = "acct-plus" }), "acct-plus",
                new Dictionary<string, string> { ["plan"] = "plus" }, "Personal", null, default)).Account.Id;
            Equal("ChatGPT Plus", billing.Resolve("openai-codex", plus).Basis, "the plan names it");
            var codex = estimator.Estimate(new SpendEstimateInput("openai-codex", "gpt-5.5", plus, EstBytes(300_000)));
            Equal(BillingKind.Subscription, codex.Billing, "billed as a subscription");
            Equal(272_000d, codex.Price!.TierInputTokensAbove, "above 272K");
            Equal(3_368_640L, codex.WorstMicros, "300,000 x $10/M + 8,192 x $45/M at list price");
            Check(codex.PriceKnown && !codex.CountsTowardCaps, "shown, never counted toward money caps");
            Equal("≈ $3.37 list", SpendEstimateText.Chip(codex), "≈ list");
            var copilot = (await host.Vault.SaveSignInAsync("github-copilot", new OAuthCredentialSnapshot(Planted("access-"), Planted("refresh-"),
                DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds(), new Dictionary<string, string>()), null,
                new Dictionary<string, string> { ["host"] = "github.com" }, "GitHub Copilot", null, default)).Account.Id;
            var copilotBilling = billing.Resolve("github-copilot", copilot);
            Equal(BillingKind.Subscription, copilotBilling.Mode, "Copilot is a subscription");
            Equal(host.Catalog.FindProvider("github-copilot")!.DisplayName + " · github.com", copilotBilling.Basis, "with its host");

            var rows = billing.Accounts();
            foreach (var id in new[] { anthropic, personal, plus, copilot }) Check(rows.Any(row => row.Account?.Id == id), id + " is listed");
            Equal(BillingKind.Subscription, rows.Single(row => row.Account?.Id == plus).Billing.Mode, "the row's mode");
            var json = JsonSerializer.Serialize(rows, SettingsStore.JsonOptions) + JsonSerializer.Serialize(resolved, SettingsStore.JsonOptions);
            Check(!json.Contains(key, StringComparison.Ordinal) && !json.Contains("PLANTED", StringComparison.Ordinal), "no key or token in any billing row");
        }),

        Case("spend.estimate.output-follows-the-request-the-reservation-cap-and-the-model", async () =>
        {
            var world = new EstWorld(new EstCatalog().Add(EstProvider("anthropic", BillingKind.Metered),
                EstModel("anthropic", "big", new CatalogCost(2, 10), maxTokens: 128_000), EstModel("anthropic", "small", new CatalogCost(2, 10), maxTokens: 4_096)));
            SpendEstimate Of(string model, int? maxTokens = null, bool chosen = false) =>
                world.Estimator.Estimate(new SpendEstimateInput("anthropic", model, null, EstBytes(12_000), maxTokens) { ChosenMaxTokens = chosen });

            var reserved = Of("big");
            Equal((8_192, OutputTokenBasis.ReservationCap), (reserved.OutputTokens, reserved.OutputBasis), "no maxTokens: the reservation cap");
            Equal("The request doesn't set maxTokens, so the worst case uses the default reservation cap of 8,192 output tokens.",
                SpendEstimateText.OutputNote(reserved), "said so");
            var small = Of("small");
            Equal((4_096, OutputTokenBasis.ModelMaximum), (small.OutputTokens, small.OutputBasis), "a model that writes less than the cap");
            Equal("Small writes at most 4,096 output tokens.", SpendEstimateText.OutputNote(small, "Small"), "said so");
            var asked = Of("big", 2_000);
            Equal((2_000, OutputTokenBasis.Request), (asked.OutputTokens, asked.OutputBasis), "the request's maxTokens");
            Equal(44_000L, asked.WorstMicros, "12,000 x $2/M + 2,000 x $10/M");
            var chosen = Of("big", 6_500, chosen: true);
            Equal((6_500, OutputTokenBasis.Chosen), (chosen.OutputTokens, chosen.OutputBasis), "the user's cap");
            Equal("You capped the output at 6,500 tokens.", SpendEstimateText.OutputNote(chosen), "said so");
            Equal(chosen.WorstMicros, reserved.WithOutputTokens(6_500).WorstMicros, "the same worst case as SpendEstimate.WithOutputTokens");
            var tooMany = Of("big", 200_000);
            Equal((128_000, OutputTokenBasis.ModelMaximum), (tooMany.OutputTokens, tooMany.OutputBasis), "never more than the model writes");

            await world.Settings.SaveAsync(SpendSettings.CreateDefault() with { ReservationOutputTokens = 16_384 });
            Equal((16_384, OutputTokenBasis.ReservationCap), (Of("big").OutputTokens, Of("big").OutputBasis), "a raised reservation cap");
            Equal(4_096, Of("small").OutputTokens, "still the model's maximum when that is lower");
            Equal((8_192, OutputTokenBasis.ReservationCap), CostEstimator.OutputTokens(null, false, 0, 8_192), "an unknown maximum");
            Equal((3, OutputTokenBasis.Request), CostEstimator.OutputTokens(3, false, 0, 8_192), "a small request with an unknown maximum");
            Equal((8_192, OutputTokenBasis.ReservationCap), CostEstimator.OutputTokens(null, false, 8_192, 8_192), "a maximum equal to the cap");
        }),

        Case("spend.estimate.unknown-prices-entered-prices-and-overrides", async () =>
        {
            var ledger = new EstLedger([5_000]);
            var world = new EstWorld(new EstCatalog()
                    .Add(EstProvider("anthropic", BillingKind.Metered), EstModel("anthropic", "claude-sonnet-5-5", new CatalogCost(2, 10)), EstModel("anthropic", "unlisted-price", null),
                        EstModel("anthropic", "free-model", new CatalogCost(0, 0)))
                    .Add(EstProvider("work-gateway", BillingKind.Unknown), EstModel("work-gateway", "gpt-6.1-sol", null), EstModel("work-gateway", "llama-4", null),
                        EstModel("work-gateway", "zero-default", new CatalogCost(0, 0))),
                accounts: new EstAccounts(EstAccount("acc-ant", "anthropic", AccountKind.ApiKey), EstAccount("acc-gw", "work-gateway", AccountKind.ApiKey)),
                ledger: ledger);
            SpendEstimate Of(string provider, string model, string? account) =>
                world.Estimator.EstimateAsync(new SpendEstimateInput(provider, model, account, EstBytes(12_000))).GetAwaiter().GetResult();

            Equal(new BillingResolution(BillingKind.Unknown, BillingKind.Unknown, false, "No price in the catalog"), world.Billing.Resolve("work-gateway", "acc-gw"),
                "no model of the gateway has a price");
            var unknown = Of("work-gateway", "gpt-6.1-sol", "acc-gw");
            Equal(BillingKind.Unknown, unknown.Billing, "unknown");
            Check(unknown.Price is null && unknown.WorstMicros is null && unknown.TypicalMicros is null && !unknown.PriceKnown && !unknown.CountsTowardCaps,
                "no price, no worst case, never checked against caps");
            Equal((12_000d, 8_192), (unknown.InputTokens, unknown.OutputTokens), "the tokens are still assumed");
            Equal("price unknown", SpendEstimateText.Chip(unknown), "the chip");
            Check(SpendEstimateText.Formula(unknown) is null && SpendEstimateText.TierNote(unknown) is null, "no formula");
            Equal(BillingKind.Unknown, Of("anthropic", "unlisted-price", "acc-ant").Billing, "a metered account's model without a price is unknown");
            Equal(BillingKind.Unknown, Of("work-gateway", "zero-default", "acc-gw").Billing, "under a provider without prices, an all-zero cost is no price");
            Equal(0, ledger.Calls.Count, "unknown prices have no typical cost");
            var zero = Of("anthropic", "free-model", "acc-ant");
            Check(zero.Billing == BillingKind.Metered && zero.WorstMicros == 0 && zero.PriceKnown && SpendEstimateText.Chip(zero) is null,
                "under a metered provider, an all-zero cost is a $0 price");
            Equal(0, ledger.Calls.Count, "nor does a $0 request read one");
            Equal("gpt-6.1-sol,llama-4,zero-default", string.Join(",", world.Prices.Unpriced("work-gateway").Select(model => model.Model.Id)), "the unpriced models");
            Equal("unlisted-price", string.Join(",", world.Prices.Unpriced("anthropic").Select(model => model.Model.Id)), "a metered provider's unpriced model");

            // An entered price makes the model metered (and so the gateway), and never replaces a catalog price.
            await world.Settings.SaveAsync(SpendSettings.CreateDefault() with
            {
                EnteredPrices = [new EnteredPrice("work-gateway", "gpt-6.1-sol", 2_500_000, 10_000_000), new EnteredPrice("anthropic", "claude-sonnet-5-5", 9_000_000, 9_000_000)],
            });
            Equal(new BillingResolution(BillingKind.Metered, BillingKind.Metered, false, "Stored API key"), world.Billing.Resolve("work-gateway", "acc-gw"),
                "metered once a price is entered");
            var entered = Of("work-gateway", "gpt-6.1-sol", "acc-gw");
            Equal(BillingKind.Metered, entered.Billing, "the priced model is metered");
            Equal(SpendPriceSource.Entered, entered.Price!.Source, "at the entered price");
            Equal(111_920L, entered.WorstMicros, "12,000 x $2.50/M + 8,192 x $10.00/M");
            Equal(BillingKind.Unknown, Of("work-gateway", "llama-4", "acc-gw").Billing, "the other model is still unknown");
            Equal("gpt-6.1-sol", world.Prices.Unpriced("work-gateway").Single(model => model.Entered is not null).Model.Id, "the entered price is listed");
            Equal(7_500L, world.Prices.Find("work-gateway", "gpt-6.1-sol")!.CostMicros(new SpendUsage(1_000, 500)), "its actual cost: 1,000 x $2.50 + 500 x $10");
            var catalogFirst = Of("anthropic", "claude-sonnet-5-5", "acc-ant");
            Equal((SpendPriceSource.Catalog, 2.0), (catalogFirst.Price!.Source, catalogFirst.Price.Input), "the catalog's price wins");
            Equal(5_000L, catalogFirst.TypicalMicros, "a metered estimate has its typical cost");

            // A billing override wins: Unknown asks even with a price, Free costs nothing, Subscription is list price only.
            foreach (var (mode, check) in new (BillingKind, Action<SpendEstimate>)[]
            {
                (BillingKind.Unknown, estimate => Check(estimate.Billing == BillingKind.Unknown && estimate.WorstMicros is null, "overridden to unknown")),
                (BillingKind.Free, estimate => Check(estimate.Billing == BillingKind.Free && estimate.WorstMicros == 0 && estimate.Price!.IsFree &&
                    SpendEstimateText.Chip(estimate) is null && SpendEstimateText.Formula(estimate) is null, "overridden to free")),
                (BillingKind.Subscription, estimate => Check(estimate.Billing == BillingKind.Subscription && estimate.WorstMicros == 105_920 && !estimate.CountsTowardCaps &&
                    SpendEstimateText.Chip(estimate) == "≈ $0.0050 list", "overridden to a subscription (typical $0.005 at list price): " + SpendEstimateText.Chip(estimate))),
            })
            {
                await world.Settings.UpdateAsync<SpendSettings>(spend => spend with { EnteredPrices = [], BillingOverrides = [new BillingOverride("anthropic", null, mode)] });
                var resolution = world.Billing.Resolve("anthropic", "acc-ant");
                Equal(new BillingResolution(mode, BillingKind.Metered, true, "Stored API key"), resolution, "the override and what was detected");
                check(Of("anthropic", "claude-sonnet-5-5", "acc-ant"));
            }
        }),

        Case("spend.estimate.typical-is-the-median-of-recent-actual-costs", async () =>
        {
            Equal(null, CostEstimator.Median([]), "none yet");
            Equal(5L, CostEstimator.Median([5]), "one");
            Equal(2L, CostEstimator.Median([3, 1, 2]), "the middle one");
            Equal(25L, CostEstimator.Median([10, 40, 20, 30]), "the mean of the middle two");
            Equal(3L, CostEstimator.Median([1, 2, 3, 4]), "2.5 rounds away from zero");
            Equal(16L, CostEstimator.Median([.. Enumerable.Range(1, 25).Reverse().Select(value => (long)value)]), "only the newest 20 (6 to 25): 15.5, rounded");

            var ledger = new EstLedger([40_000, 10_000, 30_000]);
            var world = new EstWorld(new EstCatalog()
                    .Add(EstProvider("anthropic", BillingKind.Metered), EstModel("anthropic", "claude-sonnet-5-5", new CatalogCost(2, 10)))
                    .Add(FauxModelCatalog.Provider, EstModel(FauxDurableModels.ProviderId, "echo", new CatalogCost(0, 0))),
                ledger: ledger);
            var metered = await world.Estimator.EstimateAsync(new SpendEstimateInput("anthropic", "claude-sonnet-5-5", null, EstBytes(12_000)));
            Equal(30_000L, metered.TypicalMicros, "the median of the last actual costs");
            Equal("anthropic/claude-sonnet-5-5/20", ledger.Calls.Single(), "the ledger was asked for this model's last 20");
            Equal("≈ $0.03", SpendEstimateText.Chip(metered), "the chip shows the typical cost");
            Equal(105_920L, metered.WorstMicros, "the worst case is unchanged: typical is shown, never reserved");
            var free = await world.Estimator.EstimateAsync(new SpendEstimateInput(FauxDurableModels.ProviderId, "echo", null, 100));
            Check(free.TypicalMicros is null && ledger.Calls.Count == 1, "a free request reads no typical cost");

            ledger.Fail = true;
            Equal(null, (await world.Estimator.EstimateAsync(new SpendEstimateInput("anthropic", "claude-sonnet-5-5", null, 100))).TypicalMicros,
                "a ledger that cannot be read leaves typical out");
            Check(world.Logs.Any(line => line.Contains("could not be read", StringComparison.Ordinal)), "and says so in the log");
            ledger.Fail = false;
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await ThrowsAsync<OperationCanceledException>(() => world.Estimator.EstimateAsync(new SpendEstimateInput("anthropic", "claude-sonnet-5-5", null, 100), cancelled.Token),
                "a cancelled estimate");
            var without = new EstWorld(new EstCatalog().Add(EstProvider("anthropic", BillingKind.Metered), EstModel("anthropic", "claude-sonnet-5-5", new CatalogCost(2, 10))));
            Equal(null, (await without.Estimator.EstimateAsync(new SpendEstimateInput("anthropic", "claude-sonnet-5-5", null, 100))).TypicalMicros, "no ledger, no typical");
        }),

        Case("spend.billing.subscriptions-keys-overrides-and-the-accounts-list", async () =>
        {
            var catalog = new EstCatalog()
                .Add(EstProvider("openai-codex", BillingKind.Subscription, AuthKind.OAuth), EstModel("openai-codex", "gpt-5.5", new CatalogCost(5, 30)))
                .Add(EstProvider("anthropic", BillingKind.Metered), EstModel("anthropic", "claude-sonnet-5-5", new CatalogCost(2, 10)))
                .Add(EstProvider("github-copilot", BillingKind.Metered, AuthKind.DeviceCode, AuthKind.Environment), EstModel("github-copilot", "gpt-5.5", new CatalogCost(5, 30)))
                .Add(EstProvider("openai", BillingKind.Metered, AuthKind.ApiKey, AuthKind.Environment), EstModel("openai", "gpt-5.5", new CatalogCost(5, 30)))
                .Add(EstProvider("amazon-bedrock", BillingKind.Metered, AuthKind.ApiKey, AuthKind.Ambient), EstModel("amazon-bedrock", "nova", new CatalogCost(1, 4)))
                .Add(EstProvider("acme", BillingKind.Metered, AuthKind.OAuth), EstModel("acme", "a1", new CatalogCost(1, 1)))
                .Add(EstProvider("local", BillingKind.Free, AuthKind.None), EstModel("local", "tiny", null));
            var accounts = new EstAccounts(
                EstAccount("codex-personal", "openai-codex", AccountKind.OAuth, details: new Dictionary<string, string> { ["plan"] = "plus" }),
                EstAccount("codex-work", "openai-codex", AccountKind.OAuth, details: new Dictionary<string, string> { ["plan"] = "pro" }, active: false),
                EstAccount("copilot", "github-copilot", AccountKind.OAuth, details: new Dictionary<string, string> { ["host"] = "github.com" }),
                EstAccount("copilot-env", "github-copilot", AccountKind.Environment, source: "GH_TOKEN", active: false),
                EstAccount("acc-ant", "anthropic", AccountKind.ApiKey, hint: "sk-ant-…Q7x2"),
                EstAccount("acc-ant-2", "anthropic", AccountKind.ApiKey, active: false),
                EstAccount("env-openai-openai_api_key", "openai", AccountKind.Environment, source: "OPENAI_API_KEY"),
                EstAccount("ambient-amazon-bedrock", "amazon-bedrock", AccountKind.Ambient, source: "AWS credential chain"),
                EstAccount("acme-sso", "acme", AccountKind.OAuth),
                EstAccount("orphan", "retired-provider", AccountKind.ApiKey));
            var world = new EstWorld(catalog, accounts: accounts);
            foreach (var (provider, account, mode, basis) in new (string, string?, BillingKind, string)[]
            {
                ("openai-codex", "codex-personal", BillingKind.Subscription, "ChatGPT Plus"),
                ("openai-codex", "codex-work", BillingKind.Subscription, "ChatGPT Pro"),
                ("openai-codex", null, BillingKind.Subscription, "openai-codex subscription"),
                ("github-copilot", "copilot", BillingKind.Subscription, "github-copilot · github.com"),
                ("github-copilot", "copilot-env", BillingKind.Subscription, "Environment · GH_TOKEN"),
                ("anthropic", "acc-ant", BillingKind.Metered, "Stored API key"),
                ("anthropic", null, BillingKind.Metered, "Paid per token"),
                ("anthropic", "no-such-account", BillingKind.Metered, "Paid per token"),
                ("anthropic", "copilot", BillingKind.Metered, "Paid per token"),
                ("openai", "env-openai-openai_api_key", BillingKind.Metered, "Environment · OPENAI_API_KEY"),
                ("amazon-bedrock", "ambient-amazon-bedrock", BillingKind.Metered, "Ambient · AWS credential chain"),
                ("acme", "acme-sso", BillingKind.Subscription, "acme sign-in"),
                ("local", null, BillingKind.Free, "Free provider"),
                ("retired-provider", "orphan", BillingKind.Unknown, "Not in the model catalog"),
            })
                Equal(new BillingResolution(mode, mode, false, basis), world.Billing.Resolve(" " + provider + " ", account), $"{provider} through {account ?? "no account"}");
            Throws<ArgumentException>(() => world.Billing.Resolve(" ", null), "a provider is required");

            await world.Settings.SaveAsync(SpendSettings.CreateDefault() with
            {
                BillingOverrides = [new BillingOverride("anthropic", null, BillingKind.Free), new BillingOverride("anthropic", "acc-ant", BillingKind.Subscription)],
            });
            Equal(new BillingResolution(BillingKind.Subscription, BillingKind.Metered, true, "Stored API key"), world.Billing.Resolve("anthropic", "acc-ant"),
                "an account's override beats its provider's");
            Equal(new BillingResolution(BillingKind.Free, BillingKind.Metered, true, "Stored API key"), world.Billing.Resolve("anthropic", "acc-ant-2"),
                "the provider's override covers its other accounts");

            var rows = world.Billing.Accounts();
            Equal("openai-codex:codex-personal,openai-codex:codex-work,anthropic:acc-ant,anthropic:acc-ant-2,github-copilot:copilot,github-copilot:copilot-env," +
                "openai:env-openai-openai_api_key,amazon-bedrock:ambient-amazon-bedrock,acme:acme-sso,local:-,retired-provider:orphan",
                string.Join(",", rows.Select(row => row.Provider + ":" + (row.Account?.Id ?? "-"))), "catalog order, a row for an account-less provider, then the rest");
            Equal(BillingKind.Subscription, rows.Single(row => row.Account?.Id == "acc-ant").Billing.Mode, "rows follow the overrides");
            Equal("anthropic", rows.Single(row => row.Account?.Id == "acc-ant").ProviderName, "the display name");
            Equal("retired-provider", rows.Last().ProviderName, "an unlisted provider shows its id");
            Check(rows.All(row => !row.Billing.Basis.Contains("Q7x2", StringComparison.Ordinal)), "the basis never carries a key hint");
        }),

        Case("spend.billing.faux-loopback-and-free-providers", () =>
        {
            var plain = FauxModelCatalog.CreateModels();
            var faux = new EstWorld(new FauxModelCatalog(plain), plain);
            Equal(new BillingResolution(BillingKind.Free, BillingKind.Free, false, "Offline faux models"), faux.Billing.Resolve("faux", null), "plain --faux is free");
            var echo = faux.Estimator.Estimate(new SpendEstimateInput("faux", "echo", null, EstBytes(12_000)));
            Check(echo.Billing == BillingKind.Free && echo.WorstMicros == 0 && echo.Price!.IsFree && echo.PriceKnown && !echo.CountsTowardCaps, "and costs nothing");
            Check(SpendEstimateText.Chip(echo) is null && SpendEstimateText.Formula(echo) is null, "nothing is shown for it");
            Equal(null, faux.Billing.Accounts().Single().Account, "the faux provider is listed without an account");

            var inner = FauxModelCatalog.CreateModels();
            var metered = new EstWorld(new FauxModelCatalog(inner, FauxPricing.Price), new MeteredFauxDurableModels(inner, FauxPricing.Price));
            Equal(new BillingResolution(BillingKind.Metered, BillingKind.Metered, false, "Offline faux models · test list price"), metered.Billing.Resolve("faux", null),
                "--faux-metered is metered (its localhost base URL does not make it free)");
            var priced = metered.Estimator.Estimate(new SpendEstimateInput("faux", "echo", null, EstBytes(12_000)));
            Equal(3.75, priced.Price!.WorstInput, "the faux cache-write rate");
            Equal(167_880L, priced.WorstMicros, "12,000 x $3.75/M + 8,192 x $15/M");

            var world = new EstWorld(new EstCatalog()
                    .Add(EstProvider("lmstudio", BillingKind.Unknown, AuthKind.None), EstModel("lmstudio", "qwen3-coder", null), EstModel("lmstudio", "llama", null))
                    .Add(EstProvider("mixed", BillingKind.Metered), EstModel("mixed", "here", new CatalogCost(1, 1)), EstModel("mixed", "there", new CatalogCost(1, 1)))
                    .Add(EstProvider("free-co", BillingKind.Free), EstModel("free-co", "gift", new CatalogCost(1, 1))),
                new EstModels()
                    .Add("lmstudio", "qwen3-coder", "http://localhost:1234/v1").Add("lmstudio", "llama", "http://localhost:1234/v1")
                    .Add("mixed", "here", "http://127.0.0.1:8080/v1").Add("mixed", "there", "https://api.example.test/v1")
                    .Add("free-co", "gift", "https://api.example.test/v1"));
            Equal(new BillingResolution(BillingKind.Free, BillingKind.Free, false, "localhost:1234 · runs on this machine"), world.Billing.Resolve("lmstudio", null),
                "a loopback server runs on this machine");
            var local = world.Estimator.Estimate(new SpendEstimateInput("lmstudio", "qwen3-coder", null, EstBytes(12_000)));
            Check(local.Billing == BillingKind.Free && local.WorstMicros == 0, "free even without a price");
            Equal("localhost:1234", world.Billing.Accounts().Single(row => row.Provider == "lmstudio").Endpoint, "the row names the endpoint");
            Equal(BillingKind.Metered, world.Billing.Resolve("mixed", null).Mode, "one remote model is enough to be metered");
            Equal("lmstudio", string.Join(",", world.Billing.Accounts().Select(row => row.Provider)), "only providers that need no account are listed without one");
            Equal(new BillingResolution(BillingKind.Free, BillingKind.Free, false, "Free provider"), world.Billing.Resolve("free-co", null), "a provider the catalog marks free");

            foreach (var (url, loopback, endpoint) in new (string?, bool, string)[]
            {
                ("http://localhost:1234/v1", true, "localhost:1234"), ("http://LOCALHOST:11434", true, "localhost:11434"), ("https://localhost", true, "localhost"),
                ("http://127.0.0.1:8080", true, "127.0.0.1:8080"), ("http://127.5.5.5:1", true, "127.5.5.5:1"), ("http://[::1]:5000/v1", true, "[::1]:5000"),
                ("http://models.localhost:9000", true, "models.localhost:9000"), ("https://api.openai.com/v1", false, ""), ("http://localhost.example.com", false, ""),
                ("http://10.0.0.5:1234", false, ""), ("ftp://localhost/models", false, ""), ("localhost:1234", false, ""), (null, false, ""), ("", false, ""),
            })
            {
                Equal(loopback, BillingModes.IsLoopback(url, out var found), $"{url ?? "null"} is loopback");
                Equal(endpoint, found, $"{url ?? "null"}'s endpoint");
            }
        }),

        Case("spend.estimate.cheaper-models-are-suggestions-only", async () =>
        {
            var catalog = new EstCatalog()
                .Add(EstProvider("anthropic", BillingKind.Metered), EstModel("anthropic", "claude-opus-5-5", new CatalogCost(5, 25)),
                    EstModel("anthropic", "claude-sonnet-5-5", new CatalogCost(2, 10)), EstModel("anthropic", "claude-hidden", new CatalogCost(1, 5)),
                    EstModel("anthropic", "claude-haiku-5-5", new CatalogCost(0.1, 0.5)), EstModel("anthropic", "claude-tiny", new CatalogCost(0.05, 0.25)))
                .Add(EstProvider("google", BillingKind.Metered), EstModel("google", "gemini-pro", new CatalogCost(1.25, 10)), EstModel("google", "gemini-flash", new CatalogCost(0.3, 2.5)))
                .Add(EstProvider("openai", BillingKind.Metered), EstModel("openai", "gpt-mini", new CatalogCost(0.1, 0.4)))
                .Add(EstProvider("openai-codex", BillingKind.Subscription, AuthKind.OAuth), EstModel("openai-codex", "gpt-5.5", new CatalogCost(5, 30)))
                .Add(EstProvider("lmstudio", BillingKind.Unknown, AuthKind.None), EstModel("lmstudio", "qwen3-coder", null))
                .Add(EstProvider("work-gateway", BillingKind.Unknown), EstModel("work-gateway", "gpt-6.1-sol", null));
            var accounts = new EstAccounts(EstAccount("acc-ant", "anthropic", AccountKind.ApiKey), EstAccount("acc-g", "google", AccountKind.ApiKey),
                EstAccount("acc-codex", "openai-codex", AccountKind.OAuth), EstAccount("acc-oai", "openai", AccountKind.ApiKey, status: AccountStatus.Expired),
                EstAccount("acc-gw", "work-gateway", AccountKind.ApiKey));
            var world = new EstWorld(catalog, new EstModels().Add("lmstudio", "qwen3-coder", "http://localhost:1234/v1"), accounts);
            await world.Settings.SaveAsync(new ModelsSettings(["anthropic/claude-hidden"]));
            var input = new SpendEstimateInput("anthropic", "claude-opus-5-5", "acc-ant", EstBytes(12_000));
            string Names(IEnumerable<SpendEstimate> estimates) => string.Join(",", estimates.Select(estimate => $"{estimate.Provider}/{estimate.Model}"));

            Equal(264_800L, world.Estimator.Estimate(input).WorstMicros, "the request: 12,000 x $5/M + 8,192 x $25/M");
            var all = world.Estimator.Cheaper(input, count: 10);
            Equal("anthropic/claude-sonnet-5-5,anthropic/claude-haiku-5-5,google/gemini-pro,google/gemini-flash,openai-codex/gpt-5.5,lmstudio/qwen3-coder", Names(all),
                "the same provider first, dearest first, two per provider; hidden models, expired accounts and the model itself are left out");
            Equal("acc-ant,acc-ant,acc-g,acc-g,acc-codex,-", string.Join(",", all.Select(estimate => estimate.Account ?? "-")),
                "its own account for its own provider, each other provider's active account");
            Check(all.All(estimate => estimate.InputTokens == 12_000 && estimate.OutputTokens == 8_192), "the same request");
            Equal(105_920L, all[0].WorstMicros, "each suggestion is priced");
            Check(!all[4].CountsTowardCaps && all[5].Billing == BillingKind.Free, "a subscription and a free model are not counted");
            Equal("anthropic/claude-haiku-5-5,anthropic/claude-tiny,google/gemini-pro,google/gemini-flash,openai-codex/gpt-5.5,lmstudio/qwen3-coder",
                Names(world.Estimator.Cheaper(input, withinMicros: 100_000, count: 10)), "within what is left: Claude Sonnet 5.5 ($0.1059) does not fit $0.10");
            Equal("anthropic/claude-sonnet-5-5,anthropic/claude-haiku-5-5,google/gemini-pro", Names(world.Estimator.Cheaper(input)), "three by default");
            Equal("anthropic/claude-haiku-5-5,anthropic/claude-tiny,openai-codex/gpt-5.5",
                Names(world.Estimator.Cheaper(new SpendEstimateInput("google", "gemini-flash", "acc-g", EstBytes(12_000)))),
                "from the cheapest metered model of its provider, other providers' cheaper ones");
            Equal(0, world.Estimator.Cheaper(new SpendEstimateInput("lmstudio", "qwen3-coder", null, 10)).Count, "nothing is cheaper than free");
            Equal(0, world.Estimator.Cheaper(new SpendEstimateInput("openai-codex", "gpt-5.5", "acc-codex", 10)).Count, "a subscription has nothing counted to save");
            Equal("anthropic/claude-opus-5-5,anthropic/claude-sonnet-5-5,google/gemini-pro,google/gemini-flash,openai-codex/gpt-5.5,lmstudio/qwen3-coder",
                Names(world.Estimator.Cheaper(new SpendEstimateInput("work-gateway", "gpt-6.1-sol", "acc-gw", EstBytes(12_000)), count: 10)),
                "an unknown price: models with a known price");
            Equal(0, world.Estimator.Cheaper(input, count: 0).Count, "none asked for");
            Throws<ArgumentOutOfRangeException>(() => world.Estimator.Cheaper(input, count: -1), "a negative count");
        }),

        Case("spend.estimate.module-registers-without-a-cycle-under-a-guard", async () =>
        {
            var root = Temp("spend-estimate-module");
            try
            {
                // As Program.cs composes the app with --faux-metered: the area's services resolve, and the estimator prices faux models.
                var options = new WebAgentOptions { DataDirectory = Path.Join(root, "app"), Faux = true, FauxMetered = true };
                options.EnsureDirectories();
                var services = new ServiceCollection().AddLogging();
                services.AddSingleton(RoutingCredentials(new Dictionary<string, string>(), new FakeOAuth(), root));
                services.AddWebAgentCore(options);
                services.AddCredentialsArea(options);
                services.AddWebAgentFallbacks(options);
                services.AddSpendArea(options);
                await using (var app = services.BuildServiceProvider())
                {
                    app.GetRequiredService<WebAgentDb>().Migrate();
                    Check(ReferenceEquals(app.GetRequiredService<ICostEstimator>(), app.GetRequiredService<CostEstimator>()), "one estimator");
                    Check(ReferenceEquals(app.GetRequiredService<IBillingModes>(), app.GetRequiredService<BillingModes>()), "one billing resolver");
                    Check(app.GetRequiredService<IDurableModels>() is not null, "the models resolve");
                    var estimate = await app.GetRequiredService<ICostEstimator>().EstimateAsync(new SpendEstimateInput("faux", "echo", null, EstBytes(12_000)));
                    Equal((BillingKind.Metered, 167_880L), (estimate.Billing, estimate.WorstMicros ?? -1), "a priced faux request");
                }

                // A guard that decorates IDurableModels and depends on the estimator (as S3's does) makes no cycle: the estimator reads the
                // decorated models on use, and a registered ledger gives the typical cost.
                var guarded = new WebAgentOptions { DataDirectory = Path.Join(root, "guarded"), Faux = true, FauxMetered = true };
                guarded.EnsureDirectories();
                var ledger = new EstLedger([7]);
                var composed = new ServiceCollection().AddLogging();
                composed.AddSingleton(RoutingCredentials(new Dictionary<string, string>(), new FakeOAuth(), root));
                composed.AddWebAgentCore(guarded);
                composed.AddCredentialsArea(guarded);
                composed.AddWebAgentFallbacks(guarded);
                composed.AddMeteredFaux(guarded);
                composed.AddSingleton<ISpendLedger>(ledger);
                composed.AddSpendEstimation();
                composed.RemoveAll<IDurableModels>();
                composed.AddSingleton<IDurableModels>(provider => new EstGuardModels(provider.GetRequiredService<MeteredFauxDurableModels>(), provider.GetRequiredService<ICostEstimator>()));
                await using var built = composed.BuildServiceProvider();
                built.GetRequiredService<WebAgentDb>().Migrate();
                var guard = (EstGuardModels)built.GetRequiredService<IDurableModels>();
                var priced = await built.GetRequiredService<ICostEstimator>().EstimateAsync(new SpendEstimateInput("faux", "echo", null, EstBytes(12_000)));
                Check(guard.Lookups > 0, "the estimator read the decorated models");
                Equal((167_880L, 7L), (priced.WorstMicros ?? -1, priced.TypicalMicros ?? -1), "priced, with the ledger's typical cost");
                Equal("faux/echo/20", ledger.Calls.Single(), "the ledger was asked once");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("spend.estimate.texts-add-up-and-read-the-same-everywhere", () =>
        {
            Equal("$2.00/M|$0.075/M|$0.0375/M|$1.25/M|$10.00/M|$0.50|$0.00/M", string.Join("|", SpendEstimateText.Rate(2), SpendEstimateText.Rate(0.075),
                SpendEstimateText.Rate(0.0375), SpendEstimateText.Rate(1.25), SpendEstimateText.Rate(10), SpendEstimateText.RateAmount(0.5), SpendEstimateText.Rate(double.NaN)), "rates");
            Equal("100K|272K|1M|1.5M|1.25M|1234K|12,345|200", string.Join("|", new double[] { 100_000, 272_000, 1_000_000, 1_500_000, 1_250_000, 1_234_000, 12_345, 200 }
                .Select(SpendEstimateText.Threshold)), "thresholds");
            Equal("12,000|0|3", string.Join("|", SpendEstimateText.Tokens(12_000), SpendEstimateText.Tokens(-4), SpendEstimateText.Tokens(2.2)), "token counts");
            SpendEstimate Made(BillingKind billing, long? worst, long? typical = null, SpendPrice? price = null) => new()
            {
                Provider = "p", Model = "m", Billing = billing, Price = worst is null ? price : price ?? new SpendPrice(1, 1), WorstMicros = worst, TypicalMicros = typical,
                InputTokens = 1_000, OutputTokens = 1_000,
            };
            Equal("≈ $0.0040", SpendEstimateText.Chip(Made(BillingKind.Metered, 4_000)), "under a cent: four decimals");
            Equal("≈ $0.03", SpendEstimateText.Chip(Made(BillingKind.Metered, 105_920, 30_000)), "the typical cost when known");
            Equal("≈ $0.21 list", SpendEstimateText.Chip(Made(BillingKind.Subscription, 210_000)), "a subscription's list price");
            Equal(null, SpendEstimateText.Chip(Made(BillingKind.Subscription, null)), "a subscription without a list price shows nothing");
            Equal(null, SpendEstimateText.Chip(Made(BillingKind.Metered, 0)), "a $0 model shows nothing");
            Equal("price unknown", SpendEstimateText.Chip(Made(BillingKind.Unknown, null)), "no price");

            // Every formula shown adds up: its parts and total at the same decimals, as many as it takes (up to micro-USD), each part within
            // half a unit of its last decimal, or within one micro-USD when even micro-USD needed the remainder.
            var pattern = new Regex(@"^([\d,]+) × \$([\d.,]+)/M \+ ([\d,]+) × \$([\d.,]+)/M = \$([\d.,]+) \+ \$([\d.,]+) = \$([\d.,]+)$");
            double[] rates = [0, 0.01, 0.0375, 0.075, 0.1, 0.125, 0.15, 0.3, 0.6, 1, 1.1, 1.25, 2, 2.5, 3, 3.75, 4.4, 5, 10, 15, 25, 75];
            var random = new Random(20261029);
            var micro = 0;
            for (var i = 0; i < 3_000; i++)
            {
                var price = new SpendPrice(rates[random.Next(rates.Length)], rates[random.Next(rates.Length)], 0, rates[random.Next(rates.Length)]);
                var input = (double)random.Next(0, 2_000_000);
                var output = random.Next(1, 200_000);
                var estimate = Made(BillingKind.Metered, price.WorstMicros(input, output), price: price) with { InputTokens = input, OutputTokens = output };
                var text = SpendEstimateText.Formula(estimate)!;
                var match = pattern.Match(text);
                Check(match.Success, "a formula reads " + text);
                decimal Number(int group) => decimal.Parse(match.Groups[group].Value, NumberStyles.Number, CultureInfo.InvariantCulture);
                Equal((decimal)input, Number(1), "its input tokens: " + text);
                Equal((decimal)price.WorstInput, Number(2), "its input rate: " + text);
                Equal((decimal)output, Number(3), "its output tokens: " + text);
                Equal(Number(7), Number(5) + Number(6), "the parts add up: " + text);
                var places = match.Groups[7].Value.Split('.')[1].Length;
                if (places == 6) micro++;
                Equal(decimal.Round(SpendMoney.ToUsd(estimate.WorstMicros!.Value), places, MidpointRounding.AwayFromZero), Number(7), "the total is the worst case: " + text);
                var tolerance = Math.Max(0.5m / (decimal)Math.Pow(10, places), 0.000001m);
                Check(Math.Abs(Number(5) - SpendMoney.Cost(input, price.WorstInput) / SpendMoney.MicrosPerDollar) <= tolerance &&
                    Math.Abs(Number(6) - SpendMoney.Cost(output, price.Output) / SpendMoney.MicrosPerDollar) <= tolerance, "each part is close to its exact cost: " + text);
            }
            Check(micro < 300, micro + " of 3,000 formulas needed micro-USD");
        }),

        Case("spend.estimate.input-bytes-and-checks", async () =>
        {
            ImmutableArray<ChordJson> messages = [.. UserTurn("héllo €"), .. UserTurn("plain")];
            Equal((long)messages.Sum(message => Encoding.UTF8.GetByteCount(message.ToJson())), CostEstimator.InputBytes(messages), "the UTF-8 size of the messages' JSON");
            Check(CostEstimator.InputBytes(UserTurn("a longer message")) > CostEstimator.InputBytes(UserTurn("short")), "a longer message is larger");
            Equal(0L, CostEstimator.InputBytes([]), "no messages");

            var world = new EstWorld(new EstCatalog().Add(EstProvider("anthropic", BillingKind.Metered), EstModel("anthropic", "claude-sonnet-5-5", new CatalogCost(2, 10))));
            foreach (var (bad, what) in new (SpendEstimateInput, string)[]
            {
                (new(" ", "claude-sonnet-5-5", null, 1), "no provider"), (new("anthropic", "", null, 1), "no model"),
                (new("anthropic", "claude-sonnet-5-5", null, -1), "a negative size"), (new("anthropic", "claude-sonnet-5-5", null, 1, 0), "maxTokens 0"),
            })
                await ThrowsAsync<ArgumentException>(() => world.Estimator.EstimateAsync(bad), what);
            var trimmed = world.Estimator.Estimate(new SpendEstimateInput(" anthropic ", " claude-sonnet-5-5 ", " ", 10));
            Equal(("anthropic", "claude-sonnet-5-5", (string?)null), (trimmed.Provider, trimmed.Model, trimmed.Account), "trimmed");
            var unlisted = world.Estimator.Estimate(new SpendEstimateInput("nobody", "nothing", null, 10));
            Check(unlisted.Billing == BillingKind.Unknown && unlisted.WorstMicros is null, "a model nobody lists has an unknown price");
            Throws<ArgumentException>(() => world.Prices.Find("anthropic", " "), "a price needs a model");
        }),
    ];

    /// <summary>A request size the estimator counts as exactly <paramref name="tokens"/> input tokens (bytes x 11 / 30, rounded up).</summary>
    private static long EstBytes(int tokens) => (long)decimal.Floor(tokens * 30m / 11m);

    private static CatalogProvider EstProvider(string id, BillingKind billing, params AuthKind[] auth) =>
        new(id, id, null, auth.Length == 0 ? [AuthKind.ApiKey] : auth, billing);

    private static CatalogModel EstModel(string provider, string id, CatalogCost? cost, int maxTokens = 128_000) =>
        new(provider, id, char.ToUpperInvariant(id[0]) + id[1..]) { MaxTokens = maxTokens, ContextWindow = 1_000_000, Cost = cost };

    private static AccountInfo EstAccount(string id, string provider, AccountKind kind, string? source = null, IReadOnlyDictionary<string, string>? details = null,
        bool active = true, string hint = "", AccountStatus status = AccountStatus.Connected) => new()
    {
        Id = id, Provider = provider, Label = id, Kind = kind, Source = source, Details = details ?? new Dictionary<string, string>(), IsActive = active,
        Hint = hint, Status = status,
    };

    /// <summary>The Estimation unit's three services over fakes, with an in-memory settings store and a log.</summary>
    private sealed class EstWorld
    {
        public EstWorld(IModelCatalog catalog, IDurableModels? models = null, IAccountStore? accounts = null, ISpendLedger? ledger = null)
        {
            Models = models as EstModels;
            Prices = new ModelPrices(catalog, Settings, models is null ? null : () => models);
            Billing = new BillingModes(catalog, Settings, Prices, accounts, models is null ? null : () => models);
            var factory = LoggerFactory.Create(logging => logging.AddProvider(new ListLoggerProvider(Lines)));
            Estimator = new CostEstimator(catalog, Settings, Billing, Prices, accounts, ledger is null ? null : () => ledger, factory.CreateLogger<CostEstimator>());
        }

        public EstSettings Settings { get; } = new();

        public EstModels? Models { get; }

        public ModelPrices Prices { get; }

        public BillingModes Billing { get; }

        public CostEstimator Estimator { get; }

        public ConcurrentQueue<string> Lines { get; } = new();

        public IEnumerable<string> Logs => Lines;
    }

    private sealed class EstCatalog : IModelCatalog
    {
        private readonly List<CatalogProvider> providers = [];
        private readonly Dictionary<string, IReadOnlyList<CatalogModel>> models = new(StringComparer.Ordinal);

        public EstCatalog Add(CatalogProvider provider, params CatalogModel[] list)
        {
            providers.Add(provider);
            models[provider.Id] = list;
            return this;
        }

        public IReadOnlyList<CatalogProvider> Providers => providers;

        public IReadOnlyList<CatalogModel> Models(string providerId) => models.TryGetValue(providerId, out var list) ? list : [];
    }

    /// <summary>Models with a base URL and, optionally, catalog JSON with a cost; nothing streams.</summary>
    private sealed class EstModels : IDurableModels
    {
        private readonly ConcurrentDictionary<(string, string), DurableModel> models = new();
        private int lookups;

        public int Lookups => Volatile.Read(ref lookups);

        public EstModels Add(string provider, string id, string baseUrl = "https://api.example.test/v1", string? costJson = null)
        {
            var wire = costJson is null ? ChordObject.Empty : (ChordObject)ChordJson.Parse("{\"cost\":" + costJson + "}");
            models[(provider, id)] = new DurableModel { Id = id, Name = id, Api = "openai-completions", Provider = provider, BaseUrl = baseUrl, Wire = wire };
            return this;
        }

        public DurableModel? GetModel(string provider, string modelId)
        {
            Interlocked.Increment(ref lookups);
            return models.GetValueOrDefault((provider, modelId));
        }

        public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<ChordJson> messages, DurableStreamOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<ChordJson> messages, DurableStreamOptions? options, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A stand-in for the Guard unit's decorator: it wraps the app's models and depends on the estimator.</summary>
    private sealed class EstGuardModels(IDurableModels inner, ICostEstimator estimator) : IDurableModels
    {
        private int lookups;

        public ICostEstimator Estimator { get; } = estimator;

        public int Lookups => Volatile.Read(ref lookups);

        public DurableModel? GetModel(string provider, string modelId)
        {
            Interlocked.Increment(ref lookups);
            return inner.GetModel(provider, modelId);
        }

        public IAsyncEnumerable<StreamEvent> StreamSimple(DurableModel model, ImmutableArray<ChordJson> messages, DurableStreamOptions? options, CancellationToken cancellationToken) =>
            inner.StreamSimple(model, messages, options, cancellationToken);

        public Task<AssistantMessage> CompleteSimpleAsync(DurableModel model, ImmutableArray<ChordJson> messages, DurableStreamOptions? options, CancellationToken cancellationToken) =>
            inner.CompleteSimpleAsync(model, messages, options, cancellationToken);

        public IAsyncEnumerable<StreamEvent> StreamDeferred(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.StreamDeferred(model, handle, cancellationToken);

        public Task<AssistantMessage> FetchDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.FetchDeferredAsync(model, handle, cancellationToken);

        public Task CancelDeferredAsync(DurableModel model, DurableDeferredHandle handle, CancellationToken cancellationToken) =>
            inner.CancelDeferredAsync(model, handle, cancellationToken);
    }

    private sealed class EstAccounts(params AccountInfo[] accounts) : IAccountStore
    {
        public event EventHandler<AccountChangedEventArgs>? Changed { add { } remove { } }

        public IReadOnlyList<AccountInfo> List(string? provider = null) => [.. accounts.Where(account => provider is null || account.Provider == provider)];

        public AccountInfo? Get(string accountId) => accounts.FirstOrDefault(account => account.Id == accountId);

        public AccountInfo? Active(string provider) => accounts.FirstOrDefault(account => account.Provider == provider && account.IsActive);

        public Task<AccountAdded> AddApiKeyAsync(ApiKeyAccountRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountInfo> ReplaceApiKeyAsync(string accountId, string? apiKey, IReadOnlyDictionary<string, string>? settings = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountInfo> RenameAsync(string accountId, string label, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountInfo> SetActiveAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountRemoved> RemoveAsync(string accountId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AccountInfo> MarkExpiredAsync(string accountId, string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Settings sections in memory, normalized on save as the real store does.</summary>
    private sealed class EstSettings : ISettingsStore
    {
        private readonly ConcurrentDictionary<string, object> sections = new(StringComparer.Ordinal);

        public event EventHandler<SettingsChangedEventArgs>? Changed;

        public T Get<T>() where T : class, ISettingsSection<T> => sections.TryGetValue(T.SectionName, out var value) ? (T)value : T.CreateDefault();

        public Task<T> SaveAsync<T>(T value, CancellationToken cancellationToken = default) where T : class, ISettingsSection<T>
        {
            var normalized = value.Normalize();
            sections[T.SectionName] = normalized;
            Changed?.Invoke(this, new SettingsChangedEventArgs(T.SectionName, normalized));
            return Task.FromResult(normalized);
        }

        public Task<T> UpdateAsync<T>(Func<T, T> change, CancellationToken cancellationToken = default) where T : class, ISettingsSection<T> =>
            SaveAsync(change(Get<T>()), cancellationToken);

        public Task<T> ResetAsync<T>(CancellationToken cancellationToken = default) where T : class, ISettingsSection<T>
        {
            sections.TryRemove(T.SectionName, out _);
            var defaults = T.CreateDefault();
            Changed?.Invoke(this, new SettingsChangedEventArgs(T.SectionName, defaults));
            return Task.FromResult(defaults);
        }
    }

    /// <summary>A ledger that only answers the recent actual costs (newest first), recording each question as provider/model/count.</summary>
    private sealed class EstLedger(IReadOnlyList<long> recent) : ISpendLedger
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public bool Fail { get; set; }

        public Task<IReadOnlyList<long>> RecentActualsAsync(string provider, string model, int count = 20, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Enqueue($"{provider}/{model}/{count}");
            return Fail ? throw new InvalidOperationException("the ledger is unreadable") : Task.FromResult(recent);
        }

        public Task<IReadOnlyList<SpendCap>> CapsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpendCap> SaveCapAsync(SpendCap cap, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveCapAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SpendCapStatus>> CapStatusAsync(string? run = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpendDecision> CheckAsync(SpendRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpendReservation> ReserveAsync(SpendRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpendGroupReservation> ReserveGroupAsync(IReadOnlyList<SpendRequest> requests, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Reservation?> GetAsync(string op, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Reservation> SettleAsync(string op, SpendUsage usage, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Reservation> SettleAtEstimateAsync(string op, long estimateMicros, string? reason = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Reservation> ReleaseAsync(string op, string? reason = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Reservation> MarkUncertainAsync(string op, string reason, long? estimateMicros = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Reservation> ResolveAsync(string op, SpendResolution resolution, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> RecoverAsync(string reason, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpendLedgerPage> QueryAsync(SpendLedgerQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpendTotal> TotalAsync(SpendLedgerQuery query, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SpendCacheMeter> CacheMeterAsync(DateTimeOffset month, string? provider = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
