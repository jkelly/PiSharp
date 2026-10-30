using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Spend;

// Decision 0011 (run W3, S0): the Spend Guard's contracts and the priced faux switch. The contracts normalise and validate (caps, settings,
// requests, approvals, resolutions, ledger queries), count money exactly (micro-USD, a worst case rounded up, the mockup's worked example),
// keep days and months in UTC, decide Allow/Ask/Block with Block first, write and parse the stable spend error codes, and send fixed wire
// names. A chat's requests carry source and run (the chat id) beside account, tier and op. --faux-metered stays offline but prices the faux
// provider at its list price and reports usage from text length, in process and in the built app; plain --faux stays free with zero usage.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SpendContractCases() =>
    [
        Case("spend.contract.sources-and-enums-have-fixed-wire-names", () =>
        {
            Equal("chat,playground,compare,agent-compare,bench,arena,drift,cache-warm", string.Join(",", SpendSources.All.Select(source => source.Wire())), "the sources");
            Equal("Tool Bench|Drift Watch|Cache warm", $"{SpendSource.Bench.Label()}|{SpendSource.Drift.Label()}|{SpendSource.CacheWarm.Label()}", "labels");
            foreach (var source in SpendSources.All) Equal(source, SpendSources.Parse(source.Wire()), "round trip " + source);
            Equal(SpendSource.CacheWarm, SpendSources.Parse(" Cache-Warm "), "case and spaces are ignored");
            Check(!SpendSources.TryParse("email", out _) && !SpendSources.TryParse(null, out _), "unknown sources");
            Check(Throws<ArgumentException>(() => SpendSources.Parse("email"), "an unknown source").Message.Contains("cache-warm", StringComparison.Ordinal), "the message lists them");
            foreach (var options in new[] { SettingsStore.JsonOptions, JsonSerializerOptions.Web, new JsonSerializerOptions() })
            {
                Equal("\"cache-warm\"", JsonSerializer.Serialize(SpendSource.CacheWarm, options), "a source");
                Equal(SpendSource.AgentCompare, JsonSerializer.Deserialize<SpendSource>("\"agent-compare\"", options), "a source read back");
                Equal("[\"everything\",\"provider\",\"account\",\"source\"]", JsonSerializer.Serialize(Enum.GetValues<CapScope>(), options), "scopes");
                Equal("[\"request\",\"run\",\"day\",\"month\"]", JsonSerializer.Serialize(Enum.GetValues<CapPeriod>(), options), "windows");
                Equal("[\"block\",\"ask\"]", JsonSerializer.Serialize(Enum.GetValues<CapAction>(), options), "actions");
                Equal("[\"reserved\",\"settled\",\"estimate\",\"uncertain\",\"released\"]", JsonSerializer.Serialize(Enum.GetValues<LedgerStatus>(), options), "ledger statuses");
                Equal("[\"allow\",\"ask\",\"block\"]", JsonSerializer.Serialize(Enum.GetValues<SpendVerdict>(), options), "verdicts");
                Equal("[\"ask\",\"block\",\"allow\"]", JsonSerializer.Serialize(Enum.GetValues<UnknownPriceBehavior>(), options), "unknown-price behaviours");
                Equal("[\"proceed-once\",\"cap-output\"]", JsonSerializer.Serialize(Enum.GetValues<SpendChoice>(), options), "choices");
                Equal("[\"estimate\",\"amount\",\"release\"]", JsonSerializer.Serialize(Enum.GetValues<ResolveChoice>(), options), "resolutions");
            }
            var cap = new SpendCap(3, CapScope.Provider, "anthropic", CapPeriod.Month, 10_000_000, CapAction.Block);
            var json = JsonSerializer.Serialize(cap, SettingsStore.JsonOptions);
            Equal("{\"id\":3,\"scope\":\"provider\",\"key\":\"anthropic\",\"period\":\"month\",\"limitMicros\":10000000,\"action\":\"block\",\"enabled\":true}", json, "a cap's JSON");
            Equal(cap, JsonSerializer.Deserialize<SpendCap>(json, SettingsStore.JsonOptions), "read back");
        }),

        Case("spend.contract.caps-normalize-validate-and-apply", () =>
        {
            Equal(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, 5_000_000, CapAction.Block),
                new SpendCap(0, CapScope.Everything, " ignored ", CapPeriod.Day, 5_000_000, CapAction.Block).Normalize(), "everything has no key");
            Equal("anthropic", new SpendCap(0, CapScope.Provider, " anthropic ", CapPeriod.Month, 1, CapAction.Ask).Normalize().Key, "a provider key is trimmed");
            Equal("cache-warm", new SpendCap(0, CapScope.Source, "Cache-Warm", CapPeriod.Month, 1, CapAction.Ask).Normalize().Key, "a source key is its wire name");
            foreach (var (bad, what) in new (SpendCap, string)[]
            {
                (new(0, CapScope.Provider, " ", CapPeriod.Day, 1, CapAction.Ask), "a provider cap without a provider"),
                (new(0, CapScope.Account, null, CapPeriod.Day, 1, CapAction.Ask), "an account cap without an account"),
                (new(0, CapScope.Source, "email", CapPeriod.Day, 1, CapAction.Ask), "an unknown source"),
                (new(0, CapScope.Everything, null, CapPeriod.Day, 0, CapAction.Ask), "a $0 limit"),
                (new(0, CapScope.Everything, null, CapPeriod.Day, SpendMoney.MaxMicros + 1, CapAction.Ask), "a limit over $1,000,000"),
                (new(0, (CapScope)9, null, CapPeriod.Day, 1, CapAction.Ask), "an unknown scope"),
                (new(0, CapScope.Everything, null, (CapPeriod)9, 1, CapAction.Ask), "an unknown window"),
                (new(0, CapScope.Everything, null, CapPeriod.Day, 1, (CapAction)9), "an unknown action"),
                (new(-1, CapScope.Everything, null, CapPeriod.Day, 1, CapAction.Ask), "a negative id"),
                (new(0, CapScope.Provider, new string('p', SpendCap.MaxKeyLength + 1), CapPeriod.Day, 1, CapAction.Ask), "a key that is too long"),
            })
                Throws<ArgumentException>(() => bad.Normalize(), what);

            var everything = new SpendCap(1, CapScope.Everything, null, CapPeriod.Day, 1, CapAction.Ask);
            var provider = new SpendCap(2, CapScope.Provider, "anthropic", CapPeriod.Month, 1, CapAction.Block);
            var account = new SpendCap(3, CapScope.Account, "acc-1", CapPeriod.Run, 1, CapAction.Ask);
            var source = new SpendCap(4, CapScope.Source, "chat", CapPeriod.Request, 1, CapAction.Ask);
            Check(everything.AppliesTo("openai", null, SpendSource.Bench), "everything applies to every request");
            Check(provider.AppliesTo("anthropic", "acc-9", SpendSource.Chat) && !provider.AppliesTo("openai", "acc-9", SpendSource.Chat), "one provider");
            Check(account.AppliesTo("anthropic", "acc-1", SpendSource.Compare) && !account.AppliesTo("anthropic", null, SpendSource.Compare), "one account");
            Check(source.AppliesTo("openai", null, SpendSource.Chat) && !source.AppliesTo("openai", null, SpendSource.Playground), "one source");
            Check(provider.AppliesTo(Request("op-1", Metered(10), provider: "anthropic")), "a request");
            Check(provider.SameTarget(provider with { Id = 9, LimitMicros = 7, Action = CapAction.Ask }) && !provider.SameTarget(provider with { Period = CapPeriod.Day }),
                "an edit keeps scope, key and window");
            Equal("Per request|Per run|Daily|Monthly", string.Join("|", new[] { source, account, everything, provider }.Select(cap => cap.PeriodLabel)), "window labels");
            Equal("All metered|anthropic|One account|Work key|Chat", string.Join("|", everything.ScopeText(), provider.ScopeText(), account.ScopeText(),
                account.ScopeText("Work key"), source.ScopeText()), "scope labels");

            var daily = new SpendCap(5, CapScope.Everything, null, CapPeriod.Day, 5_000_000, CapAction.Block);
            var status = new SpendCapStatus(daily, 1_840_000, null, null, 80);
            Equal((37, 3_160_000L, false, false), (status.PercentShown!.Value, status.LeftMicros!.Value, status.Warning, status.AtLimit), "$1.84 of $5.00");
            var monthly = new SpendCapStatus(new SpendCap(6, CapScope.Provider, "anthropic", CapPeriod.Month, 10_000_000, CapAction.Block), 8_100_000, null, null, 80);
            Equal((81, true), (monthly.PercentShown!.Value, monthly.Warning), "$8.10 of $10.00 is amber at 80%");
            Check(!new SpendCapStatus(monthly.Cap, 7_999_999, null, null, 80).Warning, "just under 80% is not");
            Check(new SpendCapStatus(monthly.Cap, 8_000_000, null, null, 80).Warning, "exactly 80% is");
            var over = new SpendCapStatus(daily, 6_000_000, null, null, 80);
            Equal((0L, true), (over.LeftMicros!.Value, over.AtLimit), "over the limit, nothing is left");
            var request = new SpendCapStatus(source, null, null, null, 80);
            Check(request.Percent is null && request.LeftMicros is null && !request.Warning && !request.AtLimit, "a request cap has no running amount");
        }),

        Case("spend.contract.money-prices-and-utc-windows", () =>
        {
            Equal(1_840_000L, SpendMoney.FromUsd(1.84m), "dollars to micro-USD");
            Equal(1L, SpendMoney.FromUsd(0.0000005m), "a half rounds away from zero");
            Equal(72L, SpendMoney.FromUsd(0.000072), "a binary64 cost");
            Equal(0L, SpendMoney.FromUsd(double.NaN), "not a number");
            Equal(1.84m, SpendMoney.ToUsd(1_840_000), "exactly back");
            Equal((2L, 1L), (SpendMoney.Ceiling(1.0001m), SpendMoney.Round(1.4999m)), "worst cases round up, actual costs to the nearest");
            Equal("$1.84|$0.3800|$0.0357|$1,206.00|$12|≈ $0.21 list|-$0.50", string.Join("|", SpendMoney.Format(1_840_000), SpendMoney.Format(380_000, 4),
                SpendMoney.Format(35_700, null), SpendMoney.Format(1_206_000_000), SpendMoney.Format(12_000_000, 0), SpendMoney.FormatList(210_000),
                SpendMoney.Format(-500_000)), "formatting");
            Throws<ArgumentOutOfRangeException>(() => SpendMoney.Format(1, 7), "seven decimals");
            foreach (var (text, micros) in new (string, long)[] { ("$1.50", 1_500_000), ("1,000", 1_000_000_000), (" 0.0000004 ", 0), ("0", 0), ("$ 2", 2_000_000) })
                Check(SpendMoney.TryParseUsd(text, out var parsed) && parsed == micros, $"\"{text}\" is {micros}");
            foreach (var text in new[] { "", "-1", "abc", "1e3", "2000000", null })
                Check(!SpendMoney.TryParseUsd(text, out _), $"\"{text}\" is not an amount");

            // The mockup's worked example: 12,000 x $2.00/M + 8,192 x $10.00/M = $0.0240 + $0.0819 = $0.1059.
            var sonnet = new SpendPrice(2, 10);
            Equal((24_000L, 81_920L, 105_920L, "$0.1059"), (sonnet.InputMicros(12_000), sonnet.OutputMicros(8_192), sonnet.WorstMicros(12_000, 8_192),
                SpendMoney.Format(sonnet.WorstMicros(12_000, 8_192), 4)), "the worked example");
            // The confirm modal's request cause: about 90,000 tokens x $4/M + 8,192 x $20/M = $0.36 + $0.16 = $0.52 against a $0.50 cap.
            var opus = new SpendPrice(4, 20);
            Equal((523_840L, "$0.52"), (opus.WorstMicros(90_000, 8_192), SpendMoney.Format(opus.WorstMicros(90_000, 8_192))), "over the request cap");
            Equal(7_000, opus.OutputTokensWithin(90_000, 500_000)!.Value, "capping the output at 7,000 tokens fits $0.50 exactly");
            Equal(500_000L, opus.WorstMicros(90_000, 7_000), "and costs at most the cap");
            Check(opus.OutputTokensWithin(130_000, 500_000) is null, "no output fits when the input alone is over");
            Check(new SpendPrice(4, 0).OutputTokensWithin(1, 500_000) is null, "free output changes nothing");
            var cached = new SpendPrice(3, 15, 0.30, 3.75);
            Equal((3.75, 3_750L), (cached.WorstInput, cached.WorstMicros(1_000, 0)), "the higher of the input and cache-write rates");
            Equal(2_700_000L, cached.CacheSavedMicros(1_000_000), "saved = cache-read tokens x (input - cache read)");
            Equal(4_800L, cached.CostMicros(new SpendUsage(1_000, 100, 1_000, 0)), "an actual cost: 1,000 x $3 + 100 x $15 + 1,000 cached x $0.30");
            var tenth = new SpendPrice(0.1, 0.4);
            Equal((1L, 0L), (tenth.WorstMicros(3, 0), tenth.CostMicros(new SpendUsage(3, 0))), "0.3 micro-USD: a worst case rounds up, a cost to the nearest");
            Equal(new CatalogCost(3, 15, 0.30, 3.75), SpendPrice.From(new CatalogCost(3, 15, 0.30, 3.75)).ToCatalogCost(), "catalog round trip");
            Throws<ArgumentException>(() => new SpendPrice(-1, 1).Normalize(), "a negative rate");
            Throws<ArgumentException>(() => new SpendPrice(double.NaN, 1).Normalize(), "a rate that is not a number");

            var evening = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.FromHours(-5));
            Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), SpendWindows.DayStart(evening), "an evening in New York is the next UTC day");
            Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), SpendWindows.MonthStart(evening), "and the next UTC month");
            var lastSecond = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);
            Equal((new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)), SpendWindows.Of(CapPeriod.Month, lastSecond)!.Value,
                "December's window ends at the new year");
            Equal((new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)), SpendWindows.Of(CapPeriod.Day, lastSecond)!.Value,
                "the last day of the year");
            Check(SpendWindows.Of(CapPeriod.Request, lastSecond) is null && SpendWindows.Of(CapPeriod.Run, lastSecond) is null, "requests and runs have no window");
        }),

        Case("spend.contract.settings-defaults-normalize-and-store", async () =>
        {
            Equal("spend", SpendSettings.SectionName, "the section");
            var defaults = SpendSettings.CreateDefault();
            Equal((80, 8192, UnknownPriceBehavior.Ask, 0, 0, 0), (defaults.WarnPercent, defaults.ReservationOutputTokens, defaults.UnknownPrice,
                defaults.UnknownPriceRules!.Count, defaults.EnteredPrices!.Count, defaults.BillingOverrides!.Count), "the defaults");
            Equal((80, 8192), (new SpendSettings().WarnPercent, new SpendSettings().ReservationOutputTokens), "a bare section has the defaults too");
            var edited = new SpendSettings(90, 4096, UnknownPriceBehavior.Block,
                [new(" work-gateway ", UnknownPriceBehavior.Ask), new("work-gateway", UnknownPriceBehavior.Allow)],
                [new("work-gateway", "gpt-6.1-sol", 1_000_000, 4_000_000), new(" work-gateway ", " gpt-6.1-sol ", 2_500_000, 10_000_000)],
                [new("openai-codex", null, BillingKind.Metered), new("openai-codex", " acc-2 ", BillingKind.Subscription), new("openai-codex", "", BillingKind.Free)]).Normalize();
            Equal("work-gateway:Allow", string.Join(",", edited.UnknownPriceRules!.Select(rule => $"{rule.Provider}:{rule.Behavior}")), "a later rule replaces an earlier one");
            Equal("work-gateway/gpt-6.1-sol:2500000/10000000", string.Join(",", edited.EnteredPrices!.Select(price =>
                $"{price.Provider}/{price.Model}:{price.InputMicrosPerMillion}/{price.OutputMicrosPerMillion}")), "a later price replaces an earlier one, trimmed");
            Equal("openai-codex/acc-2:Subscription,openai-codex/-:Free", string.Join(",", edited.BillingOverrides!.Select(rule =>
                $"{rule.Provider}/{rule.Account ?? "-"}:{rule.Mode}")), "a blank account is the provider's override");
            Equal((UnknownPriceBehavior.Allow, UnknownPriceBehavior.Block), (edited.UnknownPriceFor("work-gateway"), edited.UnknownPriceFor("other")), "per provider, else the default");
            Equal((2.5, 10.0, SpendPriceSource.Entered), (edited.PriceFor("work-gateway", "gpt-6.1-sol")!.ToPrice().Input, edited.PriceFor("work-gateway", "gpt-6.1-sol")!.ToPrice().Output,
                edited.PriceFor("work-gateway", "gpt-6.1-sol")!.ToPrice().Source), "an entered price as rates");
            Check(edited.PriceFor("work-gateway", "other") is null, "no price for another model");
            Equal((BillingKind.Subscription, BillingKind.Free, BillingKind.Free), (edited.OverrideFor("openai-codex", "acc-2")!.Mode, edited.OverrideFor("openai-codex", "acc-3")!.Mode,
                edited.OverrideFor("openai-codex", null)!.Mode), "an account's override wins over its provider's");
            Check(edited.OverrideFor("openai", "acc-2") is null, "no override for another provider");
            foreach (var (bad, what) in new (SpendSettings, string)[]
            {
                (new(0), "a 0% threshold"), (new(101), "a 101% threshold"), (new(80, 0), "no output tokens"), (new(80, 10_000_001), "too many output tokens"),
                (new(80, 8192, (UnknownPriceBehavior)7), "an unknown behaviour"),
                (new(80, 8192, UnknownPriceBehavior.Ask, [new(" ", UnknownPriceBehavior.Ask)]), "a rule without a provider"),
                (new(80, 8192, UnknownPriceBehavior.Ask, null, [new("p", "m", -1, 0)]), "a negative price"),
                (new(80, 8192, UnknownPriceBehavior.Ask, null, [new("p", " ", 1, 1)]), "a price without a model"),
                (new(80, 8192, UnknownPriceBehavior.Ask, null, null, [new("p", null, (BillingKind)9)]), "an unknown billing mode"),
            })
                Throws<ArgumentException>(() => bad.Normalize(), what);

            var root = Temp("spend-settings");
            try
            {
                var path = Path.Join(root, "webagent.db");
                var db = new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration]);
                db.Migrate();
                var store = new SettingsStore(db);
                Equal(JsonSerializer.Serialize(defaults, SettingsStore.JsonOptions), JsonSerializer.Serialize(store.Get<SpendSettings>(), SettingsStore.JsonOptions), "nothing stored reads as the defaults");
                await store.SaveAsync(edited);
                var stored = ReadDb(path, "SELECT value FROM settings WHERE section = 'spend'", reader => reader.GetString(0)).Single();
                Check(stored.Contains("\"unknownPrice\":\"block\"", StringComparison.Ordinal) && stored.Contains("\"mode\":\"subscription\"", StringComparison.Ordinal)
                    && stored.Contains("\"behavior\":\"allow\"", StringComparison.Ordinal), "stored with wire names: " + stored);
                var reopened = new SettingsStore(new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration]));
                Equal(JsonSerializer.Serialize(edited, SettingsStore.JsonOptions), JsonSerializer.Serialize(reopened.Get<SpendSettings>(), SettingsStore.JsonOptions), "read back as saved");
                await ThrowsAsync<ArgumentException>(() => store.SaveAsync(new SpendSettings(0)), "a bad value is not stored");
                Equal(90, new SettingsStore(new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration])).Get<SpendSettings>().WarnPercent, "the stored value stays");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("spend.contract.estimates-requests-and-decisions", () =>
        {
            var metered = Metered(123_456);
            Check(metered.PriceKnown && metered.CountsTowardCaps, "a metered estimate counts");
            Check(metered with { Billing = BillingKind.Subscription } is { PriceKnown: true, CountsTowardCaps: false }, "a subscription is priced but never counts");
            Check(metered with { Billing = BillingKind.Free, WorstMicros = 0 } is { CountsTowardCaps: false }, "free never counts");
            var unknown = metered with { Billing = BillingKind.Unknown, Price = null, WorstMicros = null };
            Check(!unknown.PriceKnown && !unknown.CountsTowardCaps, "an unknown price can't be checked");
            var capped = metered.WithOutputTokens(1_000);
            Equal((1_000, OutputTokenBasis.Chosen, metered.Price!.WorstMicros(metered.InputTokens, 1_000)), (capped.OutputTokens, capped.OutputBasis, capped.WorstMicros!.Value),
                "capping the output works the worst case out again");
            Check(unknown.WithOutputTokens(10).WorstMicros is null, "still unknown with fewer output tokens");
            Equal(0L, (metered with { Billing = BillingKind.Free }).WithOutputTokens(10).WorstMicros!.Value, "still free");
            Throws<ArgumentOutOfRangeException>(() => metered.WithOutputTokens(0), "no output at all");
            Equal(new SpendEstimateInput("openai", "gpt", null, 10, 5), new SpendEstimateInput(" openai ", " gpt ", " ", 10, 5).Normalize(), "an estimate input, trimmed");
            Throws<ArgumentException>(() => new SpendEstimateInput("openai", " ", null, 10).Normalize(), "no model");
            Throws<ArgumentException>(() => new SpendEstimateInput("openai", "gpt", null, -1).Normalize(), "a negative size");
            Throws<ArgumentException>(() => new SpendEstimateInput("openai", "gpt", null, 1, 0).Normalize(), "maxTokens 0");

            var request = (Request(" op-1 ", metered) with { Run = " chat-1 ", Note = new string('n', 300), Estimate = metered with { Account = " acc-1 " } }).Normalize();
            Equal(("op-1", "chat-1", "acc-1", SpendRequest.MaxKeyLength), (request.Op, request.Run, request.Account, request.Note!.Length), "a request, trimmed and its note cut");
            Throws<ArgumentException>(() => Request(" ", metered).Normalize(), "no op");
            Throws<ArgumentException>(() => Request(new string('o', SpendRequest.MaxKeyLength + 1), metered).Normalize(), "an op that is too long");
            Throws<ArgumentException>(() => (Request("op", metered) with { Source = (SpendSource)99 }).Normalize(), "an unknown source");
            Throws<ArgumentException>(() => Request("op", metered with { WorstMicros = -1 }).Normalize(), "a negative worst case");

            var requestCap = new SpendCap(1, CapScope.Everything, null, CapPeriod.Request, 500_000, CapAction.Ask);
            var chatCap = new SpendCap(2, CapScope.Source, "chat", CapPeriod.Run, 2_000_000, CapAction.Ask);
            var monthCap = new SpendCap(3, CapScope.Provider, "anthropic", CapPeriod.Month, 10_000_000, CapAction.Block);
            var askRequest = new SpendCause { Kind = SpendCauseKind.Cap, Action = CapAction.Ask, Cap = requestCap, LeftMicros = 500_000, FitOutputTokens = 6_500 };
            var askChat = new SpendCause { Kind = SpendCauseKind.Cap, Action = CapAction.Ask, Cap = chatCap, SpentMicros = 1_880_000, LeftMicros = 120_000, FitOutputTokens = 3_000 };
            var block = new SpendCause { Kind = SpendCauseKind.Cap, Action = CapAction.Block, Cap = monthCap, SpentMicros = 8_100_000, LeftMicros = 1_900_000, FitOutputTokens = 4_000 };
            var price = new SpendCause { Kind = SpendCauseKind.UnknownPrice, Action = CapAction.Ask };
            Equal("sc-request|sc-chat|sc-period|sc-unknown", string.Join("|", askRequest.Pane, askChat.Pane, block.Pane, price.Pane), "one confirm pane per cause");
            var blocked = SpendDecision.From(metered, [askRequest, block]);
            Equal((SpendVerdict.Block, SpendErrors.Blocked, 3L, CapAction.Block), (blocked.Verdict, blocked.ErrorCode, blocked.BlockingCap!.Id, blocked.Causes[0].Action),
                "Block takes priority and comes first");
            Equal(4_000, blocked.FitOutputTokens!.Value, "the smallest output that fits every cap");
            Equal(SpendVerdict.Block, SpendDecision.From(metered, [askRequest, block], approved: true).Verdict, "an approval never passes a Block cap");
            var asked = SpendDecision.From(metered, [askRequest, askChat]);
            Equal((SpendVerdict.Ask, SpendErrors.Ask, 3_000), (asked.Verdict, asked.ErrorCode, asked.FitOutputTokens!.Value), "two Ask caps");
            var approved = SpendDecision.From(metered, [askRequest, askChat], approved: true);
            Equal((SpendVerdict.Allow, 2, (string?)null), (approved.Verdict, approved.Causes.Count, approved.ErrorCode), "approved once: allowed, causes kept for the ledger");
            var priceless = SpendDecision.From(unknown, [price]);
            Equal((SpendVerdict.Ask, SpendErrors.UnknownPrice, (int?)null), (priceless.Verdict, priceless.ErrorCode, priceless.FitOutputTokens), "an unknown price asks");
            Equal(SpendErrors.Ask, SpendDecision.From(unknown, [price, askRequest]).ErrorCode, "an unknown price with a cap is an Ask");
            Equal(SpendErrors.Blocked, SpendDecision.From(unknown, [price with { Action = CapAction.Block }]).ErrorCode, "an unknown price set to Block blocks");
            Check(SpendDecision.From(metered, [askRequest, askChat with { FitOutputTokens = null }]).FitOutputTokens is null, "no output cap when one cap can't be met");
            Check(SpendDecision.From(metered, []) is { Verdict: SpendVerdict.Allow, ErrorCode: null, FitOutputTokens: null }, "no cause: allowed");
            Check(SpendDecision.Allow(metered).Allowed, "an allowed decision");

            var row = Row("op-1", BillingKind.Metered, ReservationState.Reserved, 100);
            var granted = new SpendReservation(SpendDecision.Allow(metered), row);
            var refused = new SpendReservation(asked, null);
            Check(granted.Reserved && !refused.Reserved, "a reservation is held only when allowed");
            Check(new SpendGroupReservation([granted, granted]).Reserved, "a group with every request reserved");
            var group = new SpendGroupReservation([new SpendReservation(SpendDecision.Allow(metered), null), refused]);
            Check(!group.Reserved && ReferenceEquals(group.Refused, asked), "a refused group names the decision that refused it");
            Check(!new SpendGroupReservation([]).Reserved, "an empty group reserves nothing");

            Equal(new SpendApproval("chat-1", SpendChoice.CapOutput) { MaxOutputTokens = 3_000, Op = "op-7" },
                new SpendApproval(" chat-1 ", SpendChoice.CapOutput) { MaxOutputTokens = 3_000, Op = " op-7 " }.Normalize(), "an approval, trimmed");
            Check(new SpendApproval("chat-1", SpendChoice.ProceedOnce) { Op = " " }.Normalize().Op is null, "a blank op is the run's next call");
            Throws<ArgumentException>(() => new SpendApproval("chat-1", SpendChoice.CapOutput).Normalize(), "cap the output without a token count");
            Throws<ArgumentException>(() => new SpendApproval("chat-1", SpendChoice.ProceedOnce) { MaxOutputTokens = 5 }.Normalize(), "proceed once with a token count");
            Throws<ArgumentException>(() => new SpendApproval(" ", SpendChoice.ProceedOnce).Normalize(), "an approval for no run");
            Equal(new SpendResolution(ResolveChoice.Amount, 320_000), new SpendResolution(ResolveChoice.Amount, 320_000).Normalize(), "an entered amount");
            Throws<ArgumentException>(() => new SpendResolution(ResolveChoice.Amount).Normalize(), "no amount entered");
            Throws<ArgumentException>(() => new SpendResolution(ResolveChoice.Amount, -1).Normalize(), "a negative amount");
            Throws<ArgumentException>(() => new SpendResolution(ResolveChoice.Release, 5).Normalize(), "an amount with release");
            Equal("op-9", new SpendCall { Provider = "faux", Model = "echo", Source = SpendSource.Chat, Op = "op-9", InputBytes = 30, MaxTokens = 7 }
                is { EstimateInput: { Provider: "faux", Model: "echo", InputBytes: 30, MaxTokens: 7 } } call ? call.Op : null, "a call's estimate input");
            Equal((SpendOutcomeKind.Completed, 5.0), (SpendOutcome.Completed(new SpendUsage(1, 5)).Kind, SpendOutcome.Completed(new SpendUsage(1, 5)).Usage!.OutputTokens), "a completed call");
            Equal((SpendOutcomeKind.EndedWithoutUsage, "stream dropped"), (SpendOutcome.EndedWithoutUsage(12, "stream dropped").Kind, SpendOutcome.EndedWithoutUsage(12, "stream dropped").Reason),
                "a dropped call");
        }),

        Case("spend.contract.reservations-count-and-ledger-queries-match", () =>
        {
            var reserved = Row("r1", BillingKind.Metered, ReservationState.Reserved, 380_000);
            var uncertain = reserved with { Op = "r2", State = ReservationState.Uncertain, Reason = "stream dropped, no usage reported" };
            var settled = reserved with { Op = "r3", State = ReservationState.Settled, ActualMicros = 35_700, Settlement = SettlementKind.Actual };
            var estimate = reserved with { Op = "r4", State = ReservationState.Settled, ActualMicros = 7_600, Settlement = SettlementKind.Estimate };
            var released = reserved with { Op = "r5", State = ReservationState.Released, Reason = "HTTP 429 · 3 attempts" };
            var subscription = Row("r6", BillingKind.Subscription, ReservationState.Settled, 60_000) with
            {
                ActualMicros = 51_900, Settlement = SettlementKind.Actual, Provider = "openai-codex", Account = "acc-2",
            };
            var subscriptionOpen = Row("r7", BillingKind.Subscription, ReservationState.Reserved, 60_000) with { Provider = "openai-codex", Account = "acc-2" };
            var unknown = Row("r8", BillingKind.Unknown, ReservationState.Settled, null) with { Provider = "work-gateway", Source = SpendSource.Playground, Account = null };
            var free = Row("r9", BillingKind.Free, ReservationState.Settled, 0) with { ActualMicros = 0, Provider = "lmstudio", Source = SpendSource.Bench, Account = null };
            Equal("380000,380000,35700,7600,0,0,0,0,0", string.Join(",", new[] { reserved, uncertain, settled, estimate, released, subscription, subscriptionOpen, unknown, free }
                .Select(row => row.CountedMicros)), "what counts toward caps");
            Check(!subscription.CountsTowardCaps && !unknown.CountsTowardCaps && !free.CountsTowardCaps, "only metered rows with a price count");
            Equal((380_000L, 0L), (uncertain.UncertainMicros, settled.UncertainMicros), "uncertain spend stays counted, as uncertain");
            Equal((51_900L, 60_000L, 0L), (subscription.ListMicros, subscriptionOpen.ListMicros, reserved.ListMicros), "subscriptions at list price");
            Equal("reserved,uncertain,settled,estimate,released", string.Join(",", new[] { reserved, uncertain, settled, estimate, released }
                .Select(row => JsonSerializer.Serialize(row.Status, SettingsStore.JsonOptions).Trim('"'))), "the ledger's status words");

            var rows = new[] { reserved, uncertain, settled, estimate, released, subscription, subscriptionOpen, unknown, free };
            Equal(new SpendTotal(380_000 * 2 + 35_700 + 7_600, 380_000, 51_900 + 60_000, 9, 1, 1), SpendTotal.Of(rows), "the total of the rows");
            Equal(SpendTotal.Zero, SpendTotal.Of([]), "nothing");

            var morning = new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
            var stamped = rows.Select((row, index) => row with { Created = morning.AddHours(index), Run = index < 3 ? "chat-1" : null }).ToArray();
            string Ops(SpendLedgerQuery query) => string.Join(",", stamped.Where(query.Normalize().Matches).Select(row => row.Op));
            Equal("r1,r2,r3,r4,r5", Ops(new SpendLedgerQuery { Provider = " anthropic " }), "by provider");
            Equal("r2", Ops(new SpendLedgerQuery { Status = LedgerStatus.Uncertain }), "uncertain only");
            Equal("r4", Ops(new SpendLedgerQuery { Status = LedgerStatus.Estimate }), "estimates only");
            Equal("r1,r2,r3", Ops(new SpendLedgerQuery { Run = "chat-1" }), "one chat's rows");
            Equal("r8", Ops(new SpendLedgerQuery { Source = SpendSource.Playground }), "by source");
            Equal("r6,r7", Ops(new SpendLedgerQuery { Billing = BillingKind.Subscription }), "by billing");
            Equal("r3,r4", Ops(new SpendLedgerQuery { From = morning.AddHours(2), To = morning.AddHours(4) }), "a date range, end exclusive");
            Equal("r5", Ops(new SpendLedgerQuery { Search = "http 429" }), "a search ignores case and reads the reason");
            Equal("r9", Ops(new SpendLedgerQuery { Search = "LMSTUDIO" }), "and the provider");
            Equal("r1,r2,r3,r4,r5", Ops(new SpendLedgerQuery { Account = "acc-1" }), "by account");
            Throws<ArgumentException>(() => new SpendLedgerQuery { Limit = 0 }.Normalize(), "no rows per page");
            Throws<ArgumentException>(() => new SpendLedgerQuery { Limit = SpendLedgerQuery.MaxLimit + 1 }.Normalize(), "too many rows per page");
            Throws<ArgumentException>(() => new SpendLedgerQuery { Offset = -1 }.Normalize(), "a negative offset");
            Throws<ArgumentException>(() => new SpendLedgerQuery { From = morning, To = morning.AddDays(-1) }.Normalize(), "a range that ends before it starts");
            Equal(morning, new SpendLedgerQuery { From = morning.ToOffset(TimeSpan.FromHours(2)) }.Normalize().From!.Value.ToUniversalTime(), "times are UTC");
            Equal(TimeSpan.Zero, new SpendLedgerQuery { From = morning.ToOffset(TimeSpan.FromHours(2)) }.Normalize().From!.Value.Offset, "and kept as UTC");
        }),

        Case("spend.contract.errors-format-parse-and-display", () =>
        {
            var metered = Metered(122_904);
            var daily = new SpendCap(3, CapScope.Everything, null, CapPeriod.Day, 5_000_000, CapAction.Block);
            var blocked = SpendDecision.From(metered, [new SpendCause { Kind = SpendCauseKind.Cap, Action = CapAction.Block, Cap = daily, SpentMicros = 4_950_000, LeftMicros = 50_000 }]);
            var message = SpendErrors.Message(blocked, "op-1");
            Check(message.StartsWith("spend_blocked: This request could cost up to $0.1229, over the daily cap of $5.00 for everything metered ($4.95 used, $0.05 left).",
                StringComparison.Ordinal), "the sentence: " + message);
            Check(message.EndsWith(" [spend op=op-1; cap=3]", StringComparison.Ordinal), "the tail: " + message);
            var parsed = SpendErrors.Parse("Provider request failed: " + message)!;
            Equal((SpendErrors.Blocked, "op-1", 3L, false), (parsed.Code, parsed.Op, parsed.CapId!.Value, parsed.CanProceed), "parsed after words before it");
            Check(!parsed.Text.Contains("[spend", StringComparison.Ordinal) && parsed.Text.EndsWith("can't be sent as it is.", StringComparison.Ordinal), "the sentence alone: " + parsed.Text);
            Equal(parsed.Text, SpendErrors.Display(message), "displayed without code or tail");

            var request = new SpendCap(4, CapScope.Everything, null, CapPeriod.Request, 500_000, CapAction.Ask);
            var asked = SpendDecision.From(Metered(523_840), [new SpendCause { Kind = SpendCauseKind.Cap, Action = CapAction.Ask, Cap = request, LeftMicros = 500_000 }]);
            var askMessage = SpendErrors.Message(asked, "op;]2");
            Check(askMessage.StartsWith("spend_ask: This request could cost up to $0.5238, over the per-request cap of $0.50. It is set to Ask", StringComparison.Ordinal), askMessage);
            var askParsed = SpendErrors.Parse(askMessage)!;
            Equal((SpendErrors.Ask, "op2", 4L, true), (askParsed.Code, askParsed.Op, askParsed.CapId!.Value, askParsed.CanProceed), "an op can't break the tail");

            var unknown = Metered(0) with { Provider = "work-gateway", Model = "gpt-6.1-sol", Billing = BillingKind.Unknown, Price = null, WorstMicros = null };
            var priceless = SpendErrors.Message(SpendDecision.From(unknown, [new SpendCause { Kind = SpendCauseKind.UnknownPrice, Action = CapAction.Ask }]));
            Equal("spend_unknown_price: work-gateway/gpt-6.1-sol has no price, so it can't be checked against your caps. Unknown prices are set to Ask: proceed once to send it.",
                priceless, "an unknown price, without a tail when there is no op");
            Equal((SpendErrors.UnknownPrice, (string?)null, (long?)null, true), (SpendErrors.Parse(priceless)!.Code, SpendErrors.Parse(priceless)!.Op, SpendErrors.Parse(priceless)!.CapId,
                SpendErrors.Parse(priceless)!.CanProceed), "parsed");
            Check(SpendErrors.Message(SpendDecision.From(unknown, [new SpendCause { Kind = SpendCauseKind.UnknownPrice, Action = CapAction.Block }]))
                .StartsWith("spend_blocked: work-gateway/gpt-6.1-sol has no price", StringComparison.Ordinal), "an unknown price set to Block");
            Check(SpendErrors.Parse("spend_blocked: tail only [spend cap=12]") is { Op: null, CapId: 12 }, "a cap without an op");
            Throws<ArgumentException>(() => SpendErrors.Message(SpendDecision.Allow(metered), "op"), "an allowed request has no error");
            Check(!SpendErrors.IsSpend("account_expired: sign in again") && !SpendErrors.IsSpend(null) && !SpendErrors.IsSpend("my_spend_blocked: x"), "other errors");
            Equal("Rate limited", SpendErrors.Display("  Rate limited "), "other text is shown trimmed");
            Equal("spend_blocked,spend_ask,spend_unknown_price", string.Join(",", SpendErrors.GuardCodes), "the guard's codes");
            Equal((404, "not_found"), (SpendException.NotFound("x").Status, SpendException.NotFound("x").Code), "404");
            Equal((409, "conflict"), (SpendException.Conflict("x").Status, SpendException.Conflict("x").Code), "409");
            Equal((400, "invalid"), (SpendException.Invalid("x").Status, SpendException.Invalid("x").Code), "400");
        }),

        Case("spend.contract.stream-metadata-carries-source-and-run", () =>
        {
            var chat = SpendMetadata.Chat(SessionStreamMetadata.Fields(("account", "acc-1"), ("tier", "flex")), "chat-1").ToJson();
            Equal("{\"account\":\"acc-1\",\"tier\":\"flex\",\"source\":\"chat\",\"run\":\"chat-1\"}", chat, "a chat's fields");
            Equal("{\"source\":\"chat\",\"run\":\"chat-2\"}", SpendMetadata.Chat(null, "chat-2").ToJson(), "a chat without a binding");
            Equal("{\"source\":\"playground\"}", SpendMetadata.With(SessionStreamMetadata.Fields(("run", "old")), SpendSource.Playground, null).ToJson(), "no run");
            Throws<ArgumentException>(() => SpendMetadata.Chat(null, ""), "a chat needs its id");
            var options = new DurableStreamOptions
            {
                Metadata = JsonObjectValue.Empty.SetItem("user_id", "u").SetItem(SessionStreamMetadata.Key,
                    SpendMetadata.Chat(SessionStreamMetadata.Fields(("account", "acc-1"), ("tier", "flex")), "chat-1").SetItem("op", "op-1")),
            };
            Equal(new SpendStreamFields("acc-1", "flex", "op-1", SpendSource.Chat, "chat-1"), SpendMetadata.Read(options), "read back");
            Equal(new SpendStreamFields(null, null, null, null, null), SpendMetadata.Read((DurableStreamOptions?)null), "no metadata");
            Check(SpendMetadata.Read(SessionStreamMetadata.Fields(("source", "email"))).Source is null, "an unknown source reads as none");
            Check(SessionStreamMetadata.Strip(options)!.Metadata!.ToJson() == "{\"user_id\":\"u\"}", "a route strips all of it before a provider sees it");

            var root = Temp("spend-session-stream");
            try
            {
                var db = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, ChatBindings.Migration, SettingsStore.Migration]);
                db.Migrate();
                var bindings = new ChatBindings(db);
                var now = bindings.Now();
                bindings.Add(new ChatBinding("s1", "Title", "faux", "echo", "acc-1", "off", "flex", 1, now, now, null, "req-1"));
                var device = DeviceHostService.CreateOptions(root, root, FauxModelCatalog.CreateModels(), new SettingsStore(db), bindings, new ApprovalBroker());
                Equal("{\"account\":\"acc-1\",\"tier\":\"flex\",\"source\":\"chat\",\"run\":\"s1\"}", device.SessionStream!("s1")!.ToJson(), "the device's hook");
                Equal("{\"source\":\"chat\",\"run\":\"s2\"}", device.SessionStream!("s2")!.ToJson(), "a session without a binding is still a chat");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("spend.chat.requests-carry-source-run-and-a-fresh-op", async () =>
        {
            var seen = new ConcurrentQueue<SpendStreamFields>();
            var models = new FauxDurableModels([FauxDurableModels.Echo, FauxDurableModels.EchoSmall, FauxModelCatalog.EchoThinking])
            {
                OnRequest = (_, options) => seen.Enqueue(SpendMetadata.Read(options)),
            };
            await using var chat = await ChatHarness.StartAsync("spend-chat-metadata", models);
            var binding = await chat.CreateAsync("hello", "r1");
            await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("r2", "again"));
            await chat.IdleAsync(binding.SessionId);
            var requests = seen.ToList();
            Check(requests.Count >= 2, "a request per answer: " + requests.Count);
            Check(requests.All(fields => fields.Source == SpendSource.Chat && fields.Run == binding.SessionId), "every request names the chat as its source and run: " +
                string.Join(" | ", requests));
            Check(requests.All(fields => !string.IsNullOrEmpty(fields.Op)), "and carries an op");
            Equal(requests.Count, requests.Select(fields => fields.Op).Distinct(StringComparer.Ordinal).Count(), "a new op for each request");
        }),

        Case("spend.faux.switch-implies-faux-and-plain-faux-is-unchanged", () =>
        {
            var local = Path.Join(Path.GetTempPath(), "pisharp-webagent", "never-created");
            string? NoEnvironment(string _) => null;
            var (metered, rest) = WebAgentOptions.FromCommandLine(["--faux-metered", "--urls", "http://127.0.0.1:5999"], NoEnvironment, local);
            Equal((true, true, "--urls http://127.0.0.1:5999"), (metered.Faux, metered.FauxMetered, string.Join(" ", rest)), "--faux-metered implies --faux");
            var (plain, _) = WebAgentOptions.FromCommandLine(["--faux"], NoEnvironment, local);
            Equal((true, false), (plain.Faux, plain.FauxMetered), "plain --faux");
            var (off, _) = WebAgentOptions.FromCommandLine(["--faux-metered=false"], NoEnvironment, local);
            Equal((false, false), (off.Faux, off.FauxMetered), "--faux-metered=false");
            var (both, _) = WebAgentOptions.FromCommandLine(["--faux", "--faux-metered=true"], NoEnvironment, local);
            Equal((true, true), (both.Faux, both.FauxMetered), "both");
            Throws<ArgumentException>(() => WebAgentOptions.FromCommandLine(["--faux-metered=maybe"], NoEnvironment, local), "--faux-metered= takes true or false");

            var catalog = new FauxModelCatalog(FauxModelCatalog.CreateModels());
            Equal((BillingKind.Free, "Faux (offline)"), (catalog.Providers.Single().Billing, catalog.Providers.Single().DisplayName), "plain faux is free");
            Check(catalog.Models("faux").All(model => model.Cost == new CatalogCost(0, 0)), "at no cost");
            var options = new WebAgentOptions { DataDirectory = local, Faux = true };
            var services = new ServiceCollection().AddSingleton(options).AddWebAgentFallbacks(options);
            services.AddSpendArea(options);
            Check(services.All(descriptor => descriptor.ServiceType != typeof(MeteredFauxDurableModels)), "without the priced switch, the Spend area adds no metered faux models");
            using var provider = services.BuildServiceProvider();
            Check(provider.GetRequiredService<IDurableModels>() is FauxDurableModels, "the faux models answer as they are");
            Equal(BillingKind.Free, provider.GetRequiredService<IModelCatalog>().Providers.Single().Billing, "and the catalog stays free");
        }),

        Case("spend.faux.metered-models-report-usage-at-list-price", async () =>
        {
            var inner = new FauxDurableModels([FauxDurableModels.Echo, FauxDurableModels.EchoSmall, FauxModelCatalog.EchoThinking]) { SlowDelay = TimeSpan.FromMilliseconds(200) };
            var models = new MeteredFauxDurableModels(inner);
            Equal(new CatalogCost(3, 15, 0.30, 3.75), models.Price, "the list price: $3 in, $15 out per million");
            var echo = models.GetModel("faux", "echo")!;
            Equal("{\"input\":3,\"output\":15,\"cacheRead\":0.3,\"cacheWrite\":3.75}", echo.Wire!["cost"].ToJson(), "the model's catalog JSON carries the price");
            Check(models.GetModelsOfType("chat", null).All(model => model["cost"].ToJson().Contains("\"output\":15", StringComparison.Ordinal)), "every chat model");
            Check(models.GetModelOfType("chat", "faux", "echo-small") is not null && models.GetModel("faux", "nope") is null, "lookups");

            ImmutableArray<JsonValue> Ask(string text) => [JsonValue.Object([new("role", "user"), new("content", text)])];
            var hello = Ask("hello");
            var input = FauxPricing.Tokens(JsonValue.Array(hello).ToJson());
            Equal(input, MeteredFauxDurableModels.InputTokens(hello), "input tokens are the messages' JSON, a token per 4 characters");
            Check(input > 0, "some input");
            var events = new List<StreamEvent>();
            await foreach (var item in models.StreamSimple(echo, hello, null, CancellationToken.None)) events.Add(item);
            var plain = new List<StreamEvent>();
            await foreach (var item in inner.StreamSimple(inner.GetModel("faux", "echo")!, hello, null, CancellationToken.None)) plain.Add(item);
            Equal(string.Join(",", plain.Select(item => item.GetType().Name)), string.Join(",", events.Select(item => item.GetType().Name)), "the same events as plain faux");
            Equal(TokenUsage.Zero, ((StreamDone)plain[^1]).Message.Usage, "plain faux reports zero usage");
            var usage = ((StreamDone)events[^1]).Message.Usage;
            Equal((input, 3.0), (usage.Input, usage.Output), "\"Echo: hello\" is 11 characters, 3 tokens");
            var cost = input * 3m / 1_000_000m + 3 * 15m / 1_000_000m;
            Equal((cost, input * 3m / 1_000_000m, 45m / 1_000_000m), (usage.Cost.Total, usage.Cost.Input, usage.Cost.Output), "charged at the list price");
            Equal(SpendMoney.Round(cost * 1_000_000m), SpendUsage.From(usage).CostMicros!.Value, "the ledger reads it in micro-USD");
            Equal(usage, (await models.CompleteSimpleAsync(echo, hello, null, CancellationToken.None)).Usage, "complete reports the same usage");

            var tool = await models.CompleteSimpleAsync(echo, Ask("!ls"), null, CancellationToken.None);
            var call = (ToolCallContent)tool.Content.Single();
            Equal((StopReason.ToolUse, (double)FauxPricing.Tokens(call.Name + call.Arguments)), (tool.StopReason, tool.Usage.Output), "a tool call's tokens are its name and arguments");
            Check(tool.Usage.Cost.Total > 0, "and cost something");

            using var stop = new CancellationTokenSource();
            var partial = new List<StreamEvent>();
            await ThrowsAsync<OperationCanceledException>(async () =>
            {
                await foreach (var item in models.StreamSimple(echo, Ask("slow: one two three four"), null, stop.Token))
                {
                    partial.Add(item);
                    if (item is TextDelta) await stop.CancelAsync();
                }
            }, "a cancelled stream");
            Check(partial.OfType<TextDelta>().Any() && !partial.OfType<StreamDone>().Any(), "a cancelled answer reports no usage at all");

            var options = new WebAgentOptions { DataDirectory = Path.Join(Path.GetTempPath(), "pisharp-webagent", "never-created"), Faux = true, FauxMetered = true };
            using var provider = new ServiceCollection().AddSingleton(options).AddWebAgentFallbacks(options).AddSpendArea(options).BuildServiceProvider();
            var registered = provider.GetRequiredService<IDurableModels>() as MeteredFauxDurableModels;
            Check(registered is not null && ReferenceEquals(registered.Inner, provider.GetRequiredService<FauxDurableModels>()), "--faux-metered registers the priced models over the faux ones");
            Equal(1, provider.GetServices<IDurableModels>().Count(), "and only them");
            var priced = provider.GetRequiredService<IModelCatalog>();
            Check(priced is FauxModelCatalog, "the faux catalog");
            Equal((BillingKind.Metered, "Faux (offline, metered)"), (priced.Providers.Single().Billing, priced.Providers.Single().DisplayName), "metered");
            Check(priced.Models("faux").All(model => model.Cost == FauxPricing.Price && registered!.GetModel("faux", model.Id) is not null),
                "every model at the list price, and every listed model resolves");
        }),

        Case("spend.faux.metered-chat-footers-show-the-cost", async () =>
        {
            var faux = FauxModelCatalog.CreateModels();
            await using var chat = await ChatHarness.StartAsync("spend-chat-metered", new MeteredFauxDurableModels(faux),
                configure: services => services.AddSingleton<IModelCatalog>(new FauxModelCatalog(faux, FauxPricing.Price)));
            var binding = await chat.CreateAsync("hello metered", "r1");
            var page = await chat.Chats.OpenAsync(binding.SessionId);
            var renderer = chat.Services.GetRequiredService<PiSharp.Web.Common.FragmentRenderer>();
            var html = string.Concat(await Task.WhenAll(page.Items.Select(item => renderer.RenderAsync("~/Views/Chat/_ChatItem.cshtml", new ChatItemView(item, page.Context)))));
            Check(html.Contains("Echo: hello metered", StringComparison.Ordinal), "the answer");
            Check(html.Contains("Metered at catalog rates", StringComparison.Ordinal) && System.Net.WebUtility.HtmlDecode(html).Contains("5 out × $15.00/M", StringComparison.Ordinal), "its footer shows a metered cost: " + html);
        }),

        Case("spend.faux.app-runs-offline-and-priced-with-faux-metered", async () =>
        {
            var root = Temp("spend-faux-metered");
            try
            {
                var data = Path.Join(root, "data");
                Directory.CreateDirectory(data);
                Directory.CreateDirectory(Path.Join(root, "work"));
                var db = new WebAgentDb(Path.Join(data, "webagent.db"), [CoreMigrations.AppInfo, ChatBindings.Migration, SettingsStore.Migration]);
                db.Migrate();
                await new SettingsStore(db).SaveAsync(new ToolsSettings(Path.Join(root, "work"), ToolApprovalMode.Ask, []));
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data, Faux = false, Arguments = ["--faux-metered"] });
                Equal(true, (await app.Get("/api/health")).Body!["faux"]!.GetValue<bool>(), "--faux-metered alone runs offline");
                var created = await app.SendAsync(HttpMethod.Post, "/api/chats", new System.Text.Json.Nodes.JsonObject { ["requestId"] = "r1", ["text"] = "hello priced" });
                Equal(200, created.Status, "created: " + created.Text);
                var id = created.Body!["id"]!.GetValue<string>();
                var html = "";
                await WaitUntilAsync(async () =>
                {
                    html = (await app.Get("/chat/" + id)).Text;
                    return html.Contains("Echo: hello priced", StringComparison.Ordinal) && html.Contains("Metered at catalog rates", StringComparison.Ordinal);
                }, "the answer with its metered cost", TimeSpan.FromSeconds(30));
                Check(System.Net.WebUtility.HtmlDecode(html).Contains(" in × $3.00/M", StringComparison.Ordinal), "at the faux list price");
                Check(!app.Log.Contains("Exception", StringComparison.Ordinal), "a clean run: " + app.Log);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),
    ];

    /// <summary>A metered estimate of <c>anthropic/claude-sonnet-5-5</c> through <c>acc-1</c> at $2/$10 per million, with this worst case.</summary>
    private static SpendEstimate Metered(long worstMicros) => new()
    {
        Provider = "anthropic",
        Model = "claude-sonnet-5-5",
        Account = "acc-1",
        Billing = BillingKind.Metered,
        Price = new SpendPrice(2, 10),
        InputTokens = 12_000,
        OutputTokens = 8_192,
        WorstMicros = worstMicros,
    };

    private static SpendRequest Request(string op, SpendEstimate estimate, string? provider = null) =>
        new() { Op = op, Source = SpendSource.Chat, Estimate = provider is null ? estimate : estimate with { Provider = provider } };

    private static Reservation Row(string op, BillingKind billing, ReservationState state, long? reserved) => new()
    {
        Op = op,
        Provider = "anthropic",
        Model = "claude-sonnet-5-5",
        Account = "acc-1",
        Source = SpendSource.Chat,
        Billing = billing,
        State = state,
        ReservedMicros = reserved,
        Created = new DateTimeOffset(2026, 10, 29, 11, 24, 12, TimeSpan.Zero),
    };
}
