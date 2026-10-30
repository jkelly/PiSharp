using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.AI.ModelOperations;
using PiSharp.CodingAgent.Usage;
using PiSharp.Sessions.Serialization;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Spend;

// Decision 0011 (run W3, S2): the Spend Guard's ledger, caps and startup recovery over a temporary webagent.db. The plan's B cases: 20
// parallel $0.10 reservations against a $1 cap let exactly 10 through (two ledgers on one file, so the SQLite BEGIN IMMEDIATE transaction
// and not only the in-process lock keeps the cap); a group reserves all or nothing; the same op never reserves twice; UTC days and months
// roll over at 00:00 UTC; subscription, free and unknown-price requests are never checked against money caps (unknown prices ask, block
// or pass as the setting says); recovery marks reservations a previous run left reserved as uncertain, never settled, in process and in
// the built app; Resolve settles at the estimate, at an entered amount, or releases. Also: caps round-trip and refuse a second cap on one
// target; the decision's causes, panes, fit and 80% warnings; the first ending wins; the ledger page's SQL agrees with
// SpendLedgerQuery.Matches and SpendTotal.Of; the cache meter's saved and wasted, its miss rule checked against CacheStats.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SpendLedgerCases() =>
    [
        Case("spend.ledger.migrations-and-registration", async () =>
        {
            Equal("1000:spend: caps,1001:spend: reservations", string.Join(",", SpendLedgerMigrations.All.Select(migration => $"{migration.Id}:{migration.Name}")), "the migrations");
            Check(SpendLedgerMigrations.All.All(migration => MigrationArea.Of(migration.Id) == MigrationArea.Spend), "inside the spend range");
            await using var fixture = LedgerFixture.Create("spend-ledger-migrations");
            Check(fixture.Db.AppliedMigrations().Contains(1000) && fixture.Db.AppliedMigrations().Contains(1001), "applied");
            Equal(0, fixture.Db.Migrate().Count, "applied once");
            Equal("spend_caps,spend_reservations", string.Join(",", ReadDb(fixture.Path, "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'spend%' ORDER BY name",
                reader => reader.GetString(0))), "the tables");

            var data = Path.Join(fixture.Root, "app-data");
            var options = new WebAgentOptions { DataDirectory = data };
            var services = new ServiceCollection().AddLogging();
            services.AddWebAgentCore(options);
            services.AddWebAgentFallbacks(options);
            services.AddSpendArea(options);
            Check(services.Any(descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)), "a hosted service is registered");
            await using var provider = services.BuildServiceProvider();
            var ledger = provider.GetRequiredService<ISpendLedger>();
            Check(ledger is SpendLedger && ReferenceEquals(ledger, provider.GetRequiredService<SpendLedger>()), "one SpendLedger is the app's ISpendLedger");
            Check(provider.GetRequiredService<WebAgentDb>().Migrations.Select(migration => migration.Id).Intersect([1000, 1001]).Count() == 2, "the app's database gets the ledger tables");
            var recovery = provider.GetRequiredService<SpendRecovery>();
            await recovery.StartAsync(CancellationToken.None);
            Check(((SpendLedger)ledger).Started && recovery.Recovered == 0, "startup recovery ran on an empty ledger");
            Equal(0, (await ledger.CapsAsync()).Count, "no caps yet");
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }),

        Case("spend.ledger.caps-round-trip-in-order-one-per-target", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-caps");
            var ledger = fixture.Ledger;
            var daily = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, 5_000_000, CapAction.Block));
            var monthly = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, " ignored ", CapPeriod.Month, 40_000_000, CapAction.Ask));
            var anthropic = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Provider, " anthropic ", CapPeriod.Month, 10_000_000, CapAction.Block));
            var request = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, 500_000, CapAction.Ask));
            var warm = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Source, "Cache-Warm", CapPeriod.Day, 1_000_000, CapAction.Block));
            Check(new[] { daily, monthly, anthropic, request, warm }.Select(cap => cap.Id).Distinct().Count() == 5 && daily.Id > 0, "each cap gets its own id");
            Equal((null as string, "anthropic", "cache-warm"), (monthly.Key, anthropic.Key, warm.Key), "stored normalized");
            var caps = await ledger.CapsAsync();
            Equal("request/everything,day/everything,day/source:cache-warm,month/everything,month/provider:anthropic",
                string.Join(",", caps.Select(cap => $"{cap.Period.ToString().ToLowerInvariant()}/{cap.Scope.ToString().ToLowerInvariant()}{(cap.Key is null ? "" : ":" + cap.Key)}")),
                "by window, then scope (the mockup's order)");
            Equal(daily, caps[1], "read back as saved");
            Equal("everything||day|5000000|block|1,provider|anthropic|month|10000000|block|1,source|cache-warm|day|1000000|block|1",
                string.Join(",", ReadDb(fixture.Path, "SELECT scope, key, period, limit_micros, action, enabled FROM spend_caps WHERE id IN ($1) ORDER BY id"
                    .Replace("$1", $"{daily.Id}, {anthropic.Id}, {warm.Id}", StringComparison.Ordinal),
                    reader => $"{reader.GetString(0)}|{(reader.IsDBNull(1) ? "" : reader.GetString(1))}|{reader.GetString(2)}|{reader.GetInt64(3)}|{reader.GetString(4)}|{reader.GetInt64(5)}")),
                "stored with wire names");

            var edited = await ledger.SaveCapAsync(anthropic with { LimitMicros = 12_000_000, Action = CapAction.Ask, Enabled = false });
            Equal(anthropic with { LimitMicros = 12_000_000, Action = CapAction.Ask, Enabled = false }, edited, "an edit changes the limit, action and enabled flag");
            Equal(edited, (await ledger.CapsAsync()).Single(cap => cap.Id == anthropic.Id), "and is stored");
            await ThrowsAsync<ArgumentException>(() => ledger.SaveCapAsync(anthropic with { Period = CapPeriod.Day }), "an edit never changes the window");
            await ThrowsAsync<ArgumentException>(() => ledger.SaveCapAsync(anthropic with { Key = "openai" }), "nor the key");
            await ThrowsAsync<ArgumentException>(() => ledger.SaveCapAsync(daily with { LimitMicros = 0 }), "a $0 limit");
            var missing = await ThrowsAsync<SpendException>(() => ledger.SaveCapAsync(daily with { Id = 999 }), "an unknown id");
            Equal((404, SpendErrors.NotFound), (missing.Status, missing.Code), "404");
            var twice = await ThrowsAsync<SpendException>(() => ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, 1_000_000, CapAction.Ask)), "a second daily cap");
            Equal((409, SpendErrors.Conflict), (twice.Status, twice.Code), "409");
            Check(twice.Message.Contains("A daily cap for everything metered already exists", StringComparison.Ordinal), "the message says which: " + twice.Message);
            Check(await ledger.SaveCapAsync(new SpendCap(0, CapScope.Provider, "anthropic", CapPeriod.Day, 1_000_000, CapAction.Ask)) is { Id: > 0 }, "another window of the same scope is a new target");

            Check(await ledger.RemoveCapAsync(warm.Id), "removed");
            Check(!await ledger.RemoveCapAsync(warm.Id), "already gone");
            Equal(5, (await ledger.CapsAsync()).Count, "what is left");
        }),

        Case("spend.ledger.twenty-parallel-reservations-fit-a-dollar-cap", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-parallel");
            var cap = await fixture.Ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, 1_000_000, CapAction.Block));
            // Two ledgers on one file, as two processes would be: each has its own lock, so only BEGIN IMMEDIATE keeps them from overshooting.
            var other = fixture.Reopen();
            await fixture.Ledger.RecoverAtStartAsync();
            await other.RecoverAtStartAsync();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var racers = Enumerable.Range(0, 20).Select(index => Task.Run(async () =>
            {
                await start.Task;
                return await (index % 2 == 0 ? fixture.Ledger : other).ReserveAsync(LedgerRequest($"op-{index:00}", LedgerEstimate(100_000)));
            })).ToList();
            start.SetResult();
            var results = await Task.WhenAll(racers);
            Equal(10, results.Count(result => result.Reserved), "exactly 10 of 20 $0.10 requests fit $1.00");
            var refused = results.Where(result => !result.Reserved).ToList();
            Check(refused.All(result => result.Decision.Verdict == SpendVerdict.Block && result.Decision.BlockingCap == cap && result.Decision.ErrorCode == SpendErrors.Blocked),
                "the other 10 are blocked by the cap");
            Check(refused.All(result => result.Decision.Causes.Single() is { SpentMicros: 1_000_000, LeftMicros: 0, FitOutputTokens: null, Pane: "sc-period" }),
                "each with the cap full");
            Equal(10, ReadDb(fixture.Path, "SELECT COUNT(*) FROM spend_reservations", reader => reader.GetInt32(0)).Single(), "only the 10 are in the ledger");
            var total = await other.TotalAsync(new SpendLedgerQuery());
            Equal((1_000_000L, 10), (total.CountedMicros, total.Requests), "they add up to the cap exactly");
            var status = (await fixture.Ledger.CapStatusAsync()).Single();
            Equal((1_000_000L, 100, true, true), (status.SpentMicros!.Value, status.PercentShown!.Value, status.AtLimit, status.Warning), "the cap is full");
        }),

        Case("spend.ledger.group-reserves-all-or-nothing", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-group");
            var ledger = fixture.Ledger;
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, 1_000_000, CapAction.Block));
            Check((await ledger.ReserveAsync(LedgerRequest("solo", LedgerEstimate(300_000)))).Reserved, "$0.30 used");
            SpendRequest Column(string op, long worst) => LedgerRequest(op, LedgerEstimate(worst), SpendSource.Compare, "cmp-1");

            var refused = await ledger.ReserveGroupAsync([Column("c1", 300_000), Column("c2", 300_000), Column("c3", 300_000)]);
            Check(!refused.Reserved && refused.Items.All(item => item.Reservation is null), "three $0.30 columns do not fit $0.70: none is reserved");
            Equal("allow,allow,block", string.Join(",", refused.Items.Select(item => item.Decision.Verdict.ToString().ToLowerInvariant())), "the third refused the group");
            Equal((900_000L, 100_000L), (refused.Refused!.Causes.Single().SpentMicros, refused.Refused.Causes.Single().LeftMicros!.Value), "checked with the group's earlier columns counted");
            Equal(0, (await ledger.QueryAsync(new SpendLedgerQuery { Run = "cmp-1" })).Total, "nothing of the group was written");
            Equal(300_000L, (await ledger.TotalAsync(new SpendLedgerQuery())).CountedMicros, "the cap still holds only the solo request");

            var fits = await ledger.ReserveGroupAsync([Column("c1", 300_000), Column("c2", 300_000)]);
            Check(fits.Reserved && fits.Refused is null && fits.Items.All(item => !item.Existing && item.Reservation!.State == ReservationState.Reserved), "two fit: both are reserved");
            Equal(2, (await ledger.QueryAsync(new SpendLedgerQuery { Run = "cmp-1" })).Total, "both written");

            var mixed = await ledger.ReserveGroupAsync([Column("c1", 300_000), Column("c4", 200_000)]);
            Check(!mixed.Reserved, "a new column over the cap refuses the group");
            Check(mixed.Items[0] is { Existing: true, Reserved: true } && mixed.Items[0].Reservation == fits.Items[0].Reservation, "an op already reserved keeps its reservation");
            Check(mixed.Items[1] is { Reserved: false } && mixed.Items[1].Decision.Verdict == SpendVerdict.Block, "the new one is refused");
            Check(await ledger.GetAsync("c4") is null, "and not written");
            var again = await ledger.ReserveGroupAsync([Column("c1", 300_000), Column("c2", 300_000)]);
            Check(again.Reserved && again.Items.All(item => item.Existing), "a retried group finds its reservations and reserves nothing twice");
            Equal(900_000L, (await ledger.TotalAsync(new SpendLedgerQuery())).CountedMicros, "counted once");

            await ThrowsAsync<ArgumentException>(() => ledger.ReserveGroupAsync([Column("d1", 1), Column("d1", 1)]), "two requests share an op");
            await ThrowsAsync<ArgumentException>(() => ledger.ReserveGroupAsync([]), "an empty group");
        }),

        Case("spend.ledger.same-op-never-reserves-twice", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-idempotent");
            var ledger = fixture.Ledger;
            var first = await ledger.ReserveAsync(LedgerRequest("op-x", LedgerEstimate(100_000)));
            Check(first is { Reserved: true, Existing: false }, "reserved");
            var again = await ledger.ReserveAsync(LedgerRequest("  op-x ", LedgerEstimate(900_000) with { Provider = "openai" }));
            Check(again is { Reserved: true, Existing: true } && again.Reservation == first.Reservation && again.Decision.Allowed, "the same op returns the first reservation unchanged");
            Check((await ledger.CheckAsync(LedgerRequest("op-x", LedgerEstimate(5_000_000)))).Allowed, "a preflight of a reserved op allows it");

            var racers = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => ledger.ReserveAsync(LedgerRequest("op-y", LedgerEstimate(100_000))))));
            Equal((1, 9), (racers.Count(result => !result.Existing), racers.Count(result => result.Existing)), "ten at once: one reserves, nine find it");
            Check(racers.All(result => result.Reservation == racers[0].Reservation), "all hold the same row");

            var settled = await ledger.SettleAsync("op-x", new SpendUsage(1_000, 100) { CostMicros = 3_000 });
            var later = await ledger.ReserveAsync(LedgerRequest("op-x", LedgerEstimate(100_000)));
            Check(later.Existing && later.Reservation == settled && later.Reservation!.State == ReservationState.Settled, "an ended op is returned as it ended, whatever its state");
            Equal(2, ReadDb(fixture.Path, "SELECT COUNT(*) FROM spend_reservations", reader => reader.GetInt32(0)).Single(), "two ops, two rows");
            Equal(103_000L, (await ledger.TotalAsync(new SpendLedgerQuery())).CountedMicros, "op-x at its actual cost, op-y reserved");
        }),

        Case("spend.ledger.utc-day-and-month-roll-over", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-rollover", new DateTimeOffset(2026, 10, 31, 23, 59, 59, 900, TimeSpan.Zero));
            var ledger = fixture.Ledger;
            var day = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, 1_000_000, CapAction.Block));
            var month = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Month, 1_000_000, CapAction.Ask));
            Check((await ledger.ReserveAsync(LedgerRequest("oct", LedgerEstimate(600_000)))).Reserved, "$0.60 on the last evening of October (UTC)");
            var late = await ledger.ReserveAsync(LedgerRequest("oct-2", LedgerEstimate(600_000)));
            Check(!late.Reserved && late.Decision.Verdict == SpendVerdict.Block, "a second one does not fit the day");
            Equal($"{day.Id}:block,{month.Id}:ask", string.Join(",", late.Decision.Causes.Select(cause => $"{cause.Cap!.Id}:{cause.Action.ToString().ToLowerInvariant()}")),
                "nor the month (Block first)");

            fixture.Clock.Now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
            var november = await ledger.ReserveAsync(LedgerRequest("nov", LedgerEstimate(600_000)));
            Check(november.Reserved && november.Decision.Causes.Count == 0, "at 00:00 UTC on 1 November a new day and a new month start");
            var statuses = await ledger.CapStatusAsync();
            var dayStatus = statuses.Single(status => status.Cap.Id == day.Id);
            var monthStatus = statuses.Single(status => status.Cap.Id == month.Id);
            Equal((600_000L, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)),
                (dayStatus.SpentMicros!.Value, dayStatus.WindowStart!.Value, dayStatus.WindowEnd!.Value), "the day window");
            Equal((600_000L, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)), (monthStatus.SpentMicros!.Value, monthStatus.WindowEnd!.Value), "the month window");
            Equal(600_000L, (await ledger.TotalAsync(new SpendLedgerQuery { From = SpendWindows.MonthStart(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero)),
                To = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) })).CountedMicros, "October holds the October request");

            // The computer's time zone changes nothing: 20:00 on 30 November at UTC-5 is 01:00 on 1 December UTC.
            fixture.Clock.Now = new DateTimeOffset(2026, 11, 30, 20, 0, 0, TimeSpan.FromHours(-5));
            var december = (await ledger.CapStatusAsync()).Single(status => status.Cap.Id == month.Id);
            Equal((0L, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)), (december.SpentMicros!.Value, december.WindowStart!.Value), "a new UTC month");
            Equal(SpendWindows.MonthStart(fixture.Clock.Now), december.WindowStart!.Value, "as SpendWindows says");
        }),

        Case("spend.ledger.subscription-free-and-unknown-price-are-not-capped", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-billing");
            var ledger = fixture.Ledger;
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, 10_000, CapAction.Block));
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, 10_000, CapAction.Block));
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Provider, "openai-codex", CapPeriod.Month, 10_000, CapAction.Block));

            var subscription = await ledger.ReserveAsync(LedgerRequest("sub", LedgerEstimate(5_000_000, "openai-codex", "personal", BillingKind.Subscription, model: "gpt-6.1-sol")));
            Check(subscription.Reserved && subscription.Decision.Causes.Count == 0 && subscription.Decision.Warnings.Count == 0, "a $5 subscription request passes $0.01 caps");
            Equal((5_000_000L, false, 0L, 5_000_000L), (subscription.Reservation!.ReservedMicros!.Value, subscription.Reservation.CountsTowardCaps,
                subscription.Reservation.CountedMicros, subscription.Reservation.ListMicros), "recorded at list price, never counted");
            var free = await ledger.ReserveAsync(LedgerRequest("free", LedgerEstimate(0, "lmstudio", null, BillingKind.Free, price: new SpendPrice(0, 0))));
            Check(free.Reserved && free.Reservation!.CountedMicros == 0, "a free request passes");
            Check((await ledger.CapStatusAsync()).All(status => status.SpentMicros is null or 0), "nothing counts toward the caps");
            Check((await ledger.ReserveAsync(LedgerRequest("met", LedgerEstimate(20_000)))).Decision.Verdict == SpendVerdict.Block, "a metered $0.02 request is blocked");

            var unknown = LedgerRequest("unk", LedgerEstimate(null, "work-gateway", "env", BillingKind.Unknown, model: "llama-4-maverick"));
            var asked = await ledger.CheckAsync(unknown);
            Check(asked.Verdict == SpendVerdict.Ask && asked.ErrorCode == SpendErrors.UnknownPrice && asked.Causes.Single() is { Kind: SpendCauseKind.UnknownPrice, Pane: "sc-unknown", Cap: null },
                "an unknown price asks by default, and no cap is checked");
            var noPrice = await ledger.CheckAsync(LedgerRequest("unk-2", LedgerEstimate(null, "work-gateway", "env", BillingKind.Metered, model: "llama-4-maverick")));
            Check(noPrice.ErrorCode == SpendErrors.UnknownPrice, "so does a metered model without a price");
            await fixture.Settings.UpdateAsync<SpendSettings>(spend => spend with { UnknownPriceRules = [new("work-gateway", UnknownPriceBehavior.Block)] });
            var blocked = await ledger.ReserveAsync(unknown with { Approved = true });
            Check(!blocked.Reserved && blocked.Decision.ErrorCode == SpendErrors.Blocked && blocked.Decision.Causes.Single().Action == CapAction.Block, "Block blocks, approved or not");
            await fixture.Settings.UpdateAsync<SpendSettings>(spend => spend with { UnknownPriceRules = [new("work-gateway", UnknownPriceBehavior.Allow)] });
            Check((await ledger.CheckAsync(unknown)).Allowed, "Allow sends it");
            await fixture.Settings.UpdateAsync<SpendSettings>(spend => spend with { UnknownPriceRules = [] });
            var approved = await ledger.ReserveAsync(unknown with { Approved = true, Note = "asked · sent once" });
            Check(approved.Reserved && approved.Decision.Causes.Single().Kind == SpendCauseKind.UnknownPrice, "approved once, it is sent with its cause kept");
            Equal((null as long?, null as SpendPrice, "asked · sent once"), (approved.Reservation!.ReservedMicros, approved.Reservation.Price, approved.Reservation.Note), "with nothing reserved");

            var listed = await ledger.SettleAsync("sub", new SpendUsage(1_240, 1_980) { CostMicros = 22_280 });
            Equal((22_280L, 22_280L, 0L), (listed.ActualMicros!.Value, listed.ListMicros, listed.CountedMicros), "settled at its list price, still not counted");
            var tokens = await ledger.SettleAsync("unk", new SpendUsage(2_410, 512));
            Check(tokens.ActualMicros is null && tokens.Usage == new SpendUsage(2_410, 512) && tokens.Status == LedgerStatus.Settled, "an unknown price records tokens without a cost");
            Equal(0L, (await ledger.SettleAsync("free", new SpendUsage(5_000, 900) { CostMicros = 0 })).ActualMicros, "free costs nothing");
            var total = await ledger.TotalAsync(new SpendLedgerQuery());
            Equal((0L, 22_280L, 3, 1, 1), (total.CountedMicros, total.ListMicros, total.Requests, total.UnknownPriceRequests, total.FreeRequests), "the totals");
        }),

        Case("spend.ledger.causes-panes-fit-and-warnings", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-causes");
            var ledger = fixture.Ledger;
            // $7.90 of anthropic spend this month, before any cap.
            await ledger.ReserveAsync(LedgerRequest("seed", LedgerEstimate(7_900_000), run: "chat-seed"));
            await ledger.SettleAtEstimateAsync("seed", 7_900_000);
            var request = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, 500_000, CapAction.Ask));
            var anthropic = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Provider, "anthropic", CapPeriod.Month, 10_000_000, CapAction.Block));
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Month, 40_000_000, CapAction.Ask));
            var run = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Run, 1_000_000, CapAction.Ask));
            var account = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Account, "acc-2", CapPeriod.Day, 50_000, CapAction.Block));
            var playground = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Source, "playground", CapPeriod.Day, 10_000, CapAction.Block));
            await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, 1, CapAction.Block, Enabled: false));

            // The confirm modal's request cause: about 90,000 tokens x $4/M + 8,192 x $20/M = $0.52 against $0.50; 7,000 output tokens fit.
            var opus = LedgerEstimate(523_840, price: new SpendPrice(4, 20)) with { InputTokens = 90_000, Model = "claude-opus-5-5" };
            var ask = await ledger.CheckAsync(LedgerRequest("q1", opus, run: "chat-1"));
            Check(ask.Verdict == SpendVerdict.Ask && ask.ErrorCode == SpendErrors.Ask, "over the per-request cap, which asks");
            Check(ask.Causes.Single() is { Kind: SpendCauseKind.Cap, Action: CapAction.Ask, SpentMicros: 0, LeftMicros: 500_000, FitOutputTokens: 7_000, Pane: "sc-request" } cause
                && cause.Cap == request, "the cause, its pane and the output that fits");
            Equal(7_000, ask.FitOutputTokens!.Value, "the decision's fit");
            Check(ask.Warnings.Single() is { SpentMicros: 8_423_840, Warning: true } warning && warning.Cap == anthropic, "anthropic would be at 84%: an amber warning");
            Check(SpendErrors.Message(ask, "q1").StartsWith("spend_ask: This request could cost up to $0.5238, over the per-request cap of $0.50.", StringComparison.Ordinal),
                "the error message: " + SpendErrors.Message(ask, "q1"));
            var approved = await ledger.ReserveAsync(LedgerRequest("q1", opus, run: "chat-1") with { Approved = true });
            Check(approved.Reserved && approved.Decision.Allowed && approved.Decision.Causes.Count == 1, "approved once, it is reserved with its Ask cause kept");
            var capped = await ledger.CheckAsync(LedgerRequest("q1b", opus.WithOutputTokens(ask.FitOutputTokens.Value), run: "chat-2"));
            Check(capped.Allowed && capped.Causes.Count == 0, "with the output capped to 7,000 tokens it fits");

            var big = LedgerEstimate(2_200_000);
            var block = await ledger.ReserveAsync(LedgerRequest("q2", big, run: null) with { Approved = true });
            Check(!block.Reserved && block.Decision.Verdict == SpendVerdict.Block && block.Decision.BlockingCap == anthropic, "over the anthropic month: Block, approved or not");
            Equal("block,ask", string.Join(",", block.Decision.Causes.Select(cause => cause.Action.ToString().ToLowerInvariant())), "Block first, then the Ask");
            Check(block.Decision.Causes[0] is { SpentMicros: 8_423_840, LeftMicros: 1_576_160, Pane: "sc-period" }, "what is used and left of the month");

            // A run cap counts the run (a chat) only; without a run key it does not apply.
            await ledger.ReserveAsync(LedgerRequest("r1", LedgerEstimate(450_000), run: "chat-9"));
            var second = await ledger.ReserveAsync(LedgerRequest("r2", LedgerEstimate(450_000), run: "chat-9"));
            Check(second.Reserved && second.Decision.Warnings.Any(status => status.Cap == run && status.SpentMicros == 900_000), "$0.90 of the $1.00 run cap: a warning");
            var third = await ledger.CheckAsync(LedgerRequest("r3", LedgerEstimate(450_000), run: "chat-9"));
            Check(third.Causes.Single() is { Pane: "sc-chat", SpentMicros: 900_000, LeftMicros: 100_000, FitOutputTokens: 7_600 } runCause && runCause.Cap == run,
                "the run cap asks, and 7,600 output tokens fit the $0.10 left (12,000 x $2/M + 7,600 x $10/M)");
            Check((await ledger.CheckAsync(LedgerRequest("r4", LedgerEstimate(450_000), run: null))).Causes.Count == 0, "no run key, no run cap");
            Check((await ledger.CheckAsync(LedgerRequest("r5", LedgerEstimate(450_000), run: "chat-10"))).Causes.Count == 0, "another chat has its own run");

            var other = await ledger.CheckAsync(LedgerRequest("a1", LedgerEstimate(60_000) with { Account = "acc-2" }, run: null));
            Check(other.Verdict == SpendVerdict.Block && other.BlockingCap == account, "the account cap applies to its account");
            Check((await ledger.CheckAsync(LedgerRequest("a2", LedgerEstimate(60_000), run: null))).Allowed, "and not to another");
            var source = await ledger.CheckAsync(LedgerRequest("s1", LedgerEstimate(20_000), SpendSource.Playground, run: null));
            Check(source.Verdict == SpendVerdict.Block && source.BlockingCap == playground, "the source cap applies to its source");
            Check((await ledger.CheckAsync(LedgerRequest("s2", LedgerEstimate(20_000), run: null))).Allowed, "a disabled $0.000001 cap and other sources' caps change nothing");
            Equal(0, (await ledger.QueryAsync(new SpendLedgerQuery { Search = "q2" })).Total, "a blocked request is never written");
        }),

        Case("spend.ledger.first-ending-wins", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-endings");
            var ledger = fixture.Ledger;
            foreach (var op in new[] { "a", "b", "d", "e", "f" }) await ledger.ReserveAsync(LedgerRequest(op, LedgerEstimate(105_920)));
            await ledger.ReserveAsync(LedgerRequest("c", LedgerEstimate(130_000, price: new SpendPrice(2.5, 10) { Source = SpendPriceSource.Entered })));

            var reported = await ledger.SettleAsync("a", new SpendUsage(12_000, 500) { CostMicros = 29_500 });
            Check(reported is { State: ReservationState.Settled, Settlement: SettlementKind.Actual, ActualMicros: 29_500, Status: LedgerStatus.Settled } && reported.Ended is not null,
                "settled at the reported cost");
            Equal(reported, await ledger.GetAsync("a"), "stored exactly as returned");
            Equal(reported, await ledger.SettleAsync("a", new SpendUsage(1, 1) { CostMicros = 9 }), "a second settle changes nothing");
            Equal(reported, await ledger.ReleaseAsync("a"), "nor does a release");
            Equal(reported, await ledger.MarkUncertainAsync("a", "late"), "nor marking it uncertain");
            Equal(29_000L, (await ledger.SettleAsync("b", new SpendUsage(12_000, 500))).ActualMicros, "no reported cost: 12,000 x $2/M + 500 x $10/M");
            Equal(35_000L, (await ledger.SettleAsync("c", new SpendUsage(12_000, 500) { CostMicros = 0 })).ActualMicros, "an entered price: 12,000 x $2.50/M + 500 x $10/M");

            var estimate = await ledger.SettleAtEstimateAsync("d", 40_000, "cancelled after output");
            Check(estimate is { Status: LedgerStatus.Estimate, ActualMicros: 40_000, EstimateMicros: 40_000, Reason: "cancelled after output" }, "settled at an estimate");
            var released = await ledger.ReleaseAsync("e", "HTTP 429 · 3 attempts");
            Check(released is { Status: LedgerStatus.Released, CountedMicros: 0, Reason: "HTTP 429 · 3 attempts", ActualMicros: null }, "released");
            var uncertain = await ledger.MarkUncertainAsync("f", "stream dropped, no usage reported", 31_710);
            Check(uncertain is { Status: LedgerStatus.Uncertain, CountedMicros: 105_920, UncertainMicros: 105_920, EstimateMicros: 31_710 }, "uncertain keeps counting");
            Equal(uncertain, await ledger.SettleAsync("f", new SpendUsage(12_000, 500) { CostMicros = 29_000 }), "an uncertain row is never settled by usage");
            Equal(uncertain, await ledger.SettleAtEstimateAsync("f", 1), "nor at an estimate");
            Equal(uncertain, await ledger.ReleaseAsync("f"), "nor released");

            foreach (var (run, what) in new (Func<Task>, string)[]
            {
                (() => ledger.SettleAsync("zz", new SpendUsage(1, 1)), "settle"), (() => ledger.SettleAtEstimateAsync("zz", 1), "settle at estimate"),
                (() => ledger.ReleaseAsync("zz"), "release"), (() => ledger.MarkUncertainAsync("zz", "x"), "mark uncertain"),
                (() => ledger.ResolveAsync("zz", new SpendResolution(ResolveChoice.Release)), "resolve"),
            })
                Equal(404, (await ThrowsAsync<SpendException>(run, "an unknown op: " + what)).Status, what + " of an unknown op");
            Check(await ledger.GetAsync("zz") is null, "an unknown op reads as null");
            await ThrowsAsync<ArgumentException>(() => ledger.MarkUncertainAsync("b", " "), "a blank reason");
            await ThrowsAsync<ArgumentOutOfRangeException>(() => ledger.SettleAtEstimateAsync("b", -1), "a negative estimate");
            await ThrowsAsync<ArgumentException>(() => ledger.SettleAsync("b", new SpendUsage(-1, 0)), "negative tokens");
            await ThrowsAsync<ArgumentException>(() => ledger.GetAsync(" "), "a blank op");

            var total = await ledger.TotalAsync(new SpendLedgerQuery());
            Equal((29_500L + 29_000 + 35_000 + 40_000 + 105_920, 105_920L, 6), (total.CountedMicros, total.UncertainMicros, total.Requests), "what counts");
        }),

        Case("spend.ledger.resolve-at-estimate-amount-or-release", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-resolve");
            var ledger = fixture.Ledger;
            // The fixture's dropped chat request: 149,040 in x $2.00/M + 8,192 x $10.00/M = $0.38 reserved; about 1,906 output tokens arrived
            // before the drop, so the estimate is 149,040 x $2.00/M + 1,906 x $10.00/M = $0.3171.
            var price = new SpendPrice(2, 10);
            Equal((380_000L, 317_140L), (price.WorstMicros(149_040, 8_192), price.WorstMicros(149_040, 1_906)), "the fixture's arithmetic");
            foreach (var op in new[] { "u1", "u2", "u3", "u4", "kept" }) await ledger.ReserveAsync(LedgerRequest(op, LedgerEstimate(380_000)));
            await ledger.MarkUncertainAsync("u1", "stream dropped, no usage reported", 317_140);
            foreach (var op in new[] { "u2", "u3", "u4" }) await ledger.MarkUncertainAsync(op, "stream dropped, no usage reported");
            Equal((1_900_000L, 1_520_000L), ((await ledger.TotalAsync(new SpendLedgerQuery())).CountedMicros, (await ledger.TotalAsync(new SpendLedgerQuery())).UncertainMicros),
                "uncertain spend counts until it is resolved");

            var atEstimate = await ledger.ResolveAsync("u1", new SpendResolution(ResolveChoice.Estimate));
            Check(atEstimate is { State: ReservationState.Settled, Status: LedgerStatus.Estimate, ActualMicros: 317_140, ResolvedByUser: true, CountedMicros: 317_140 }
                && atEstimate.Reason == "stream dropped, no usage reported", "at the estimate from what arrived ($0.3171)");
            Equal(380_000L, (await ledger.ResolveAsync("u2", new SpendResolution(ResolveChoice.Estimate))).ActualMicros, "no estimate: the reserved amount");
            var entered = await ledger.ResolveAsync("u3", new SpendResolution(ResolveChoice.Amount, 250_000));
            Check(entered is { Status: LedgerStatus.Settled, Settlement: SettlementKind.Entered, ActualMicros: 250_000, ResolvedByUser: true }, "at an amount the user entered");
            var released = await ledger.ResolveAsync("u4", new SpendResolution(ResolveChoice.Release));
            Check(released is { State: ReservationState.Released, CountedMicros: 0, ResolvedByUser: true }, "released");

            var again = await ThrowsAsync<SpendException>(() => ledger.ResolveAsync("u1", new SpendResolution(ResolveChoice.Release)), "resolving twice");
            Equal((409, SpendErrors.Conflict), (again.Status, again.Code), "409 for a row that is not uncertain");
            Equal(409, (await ThrowsAsync<SpendException>(() => ledger.ResolveAsync("kept", new SpendResolution(ResolveChoice.Estimate)), "a reserved row")).Status, "a reserved row");
            await ThrowsAsync<ArgumentException>(() => ledger.ResolveAsync("u1", new SpendResolution(ResolveChoice.Amount)), "an amount without an amount");
            await ThrowsAsync<ArgumentException>(() => ledger.ResolveAsync("u1", new SpendResolution(ResolveChoice.Estimate, 5)), "an estimate with an amount");
            var total = await ledger.TotalAsync(new SpendLedgerQuery());
            Equal((317_140L + 380_000 + 250_000 + 380_000, 0L), (total.CountedMicros, total.UncertainMicros), "nothing uncertain is left");
            Equal(4, (await ledger.QueryAsync(new SpendLedgerQuery { Search = "stream dropped" })).Total, "the reason stays on the row");
        }),

        Case("spend.ledger.recovery-marks-stale-reservations-uncertain", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-recovery");
            var before = fixture.Ledger;
            await before.ReserveAsync(LedgerRequest("drop", LedgerEstimate(380_000)));
            await before.ReserveAsync(LedgerRequest("done", LedgerEstimate(100_000)));
            await before.SettleAsync("done", new SpendUsage(1_000, 100) { CostMicros = 3_000 });
            await before.ReserveAsync(LedgerRequest("gone", LedgerEstimate(100_000)));
            await before.ReleaseAsync("gone");

            // A restart: a new ledger on the same file, and the app's SpendRecovery at start.
            var after = fixture.Reopen();
            var recovery = new SpendRecovery(after);
            await recovery.StartAsync(CancellationToken.None);
            Equal(1, recovery.Recovered, "the one reserved row");
            var dropped = (await after.GetAsync("drop"))!;
            Check(dropped is { State: ReservationState.Uncertain, Reason: SpendLedger.RecoveryReason, ActualMicros: null, CountedMicros: 380_000 } && dropped.Ended is not null,
                "uncertain with the reason, still counted, not settled");
            Equal(LedgerStatus.Settled, (await after.GetAsync("done"))!.Status, "a settled row is left alone");
            Equal(LedgerStatus.Released, (await after.GetAsync("gone"))!.Status, "so is a released one");
            Equal(380_000L, (await after.TotalAsync(new SpendLedgerQuery())).UncertainMicros, "the uncertain spend");

            var reused = await after.ReserveAsync(LedgerRequest("drop", LedgerEstimate(380_000)));
            Check(reused.Existing && reused.Reservation!.State == ReservationState.Uncertain, "a recovered request with the same op finds its reservation");
            Equal(LedgerStatus.Uncertain, (await after.SettleAsync("drop", new SpendUsage(149_040, 900) { CostMicros = 307_080 })).Status, "and never settles it");

            await after.ReserveAsync(LedgerRequest("live", LedgerEstimate(100_000)));
            await recovery.StartAsync(CancellationToken.None);
            Equal((1, ReservationState.Reserved), (recovery.Recovered, (await after.GetAsync("live"))!.State), "recovery runs once: this run's reservations are left alone");

            // The ledger recovers before its first call even when SpendRecovery has not started yet.
            var third = fixture.Reopen();
            Check(!third.Started, "a new ledger has not recovered yet");
            Check((await third.ReserveAsync(LedgerRequest("next", LedgerEstimate(100_000)))).Reserved, "its first call");
            Check(third.Started && (await third.GetAsync("live"))!.State == ReservationState.Uncertain && await third.RecoverAtStartAsync() == 1, "recovered first");
            Equal(ReservationState.Reserved, (await third.GetAsync("next"))!.State, "not the reservation that call made");

            Equal(1, await third.RecoverAsync("app stopping"), "an explicit recovery marks every reserved row");
            Equal("app stopping", (await third.GetAsync("next"))!.Reason, "with its reason");
            await ThrowsAsync<ArgumentException>(() => third.RecoverAsync(" "), "a blank reason");
        }),

        Case("spend.ledger.app-start-recovers-stale-reservations", async () =>
        {
            var root = Temp("spend-ledger-app");
            try
            {
                var data = Path.Join(root, "data");
                var path = Path.Join(data, "webagent.db");
                Directory.CreateDirectory(data);
                Directory.CreateDirectory(Path.Join(root, "work"));
                // A previous run that was killed while a request streamed: its reservation is still reserved.
                var db = new WebAgentDb(path, LedgerMigrations);
                db.Migrate();
                var store = new SettingsStore(db);
                await store.SaveAsync(new ToolsSettings(Path.Join(root, "work"), ToolApprovalMode.Ask, []));
                var earlier = new SpendLedger(db, store);
                await earlier.ReserveAsync(LedgerRequest("op-killed", LedgerEstimate(380_000)));
                await earlier.ReserveAsync(LedgerRequest("op-finished", LedgerEstimate(100_000)));
                await earlier.SettleAsync("op-finished", new SpendUsage(1_000, 100) { CostMicros = 3_000 });
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data, Faux = false, Arguments = ["--faux-metered"] });
                List<(string Op, string State, string? Reason, long Counted)> rows = [];
                await WaitUntilAsync(() =>
                {
                    rows = ReadDb(path, "SELECT op, state, reason, counted_micros FROM spend_reservations ORDER BY op",
                        reader => (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt64(3)));
                    return Task.FromResult(rows.Any(row => row is { Op: "op-killed", State: "uncertain" }));
                }, "the app's start marks the stale reservation uncertain", TimeSpan.FromSeconds(30));
                Equal("op-finished:settled::3000,op-killed:uncertain:stream dropped, no usage reported:380000",
                    string.Join(",", rows.Select(row => $"{row.Op}:{row.State}:{row.Reason}:{row.Counted}")), "uncertain, still counted, the settled row untouched");
                Check(!app.Log.Contains("Exception", StringComparison.Ordinal), "a clean start: " + app.Log);
                await app.StopAsync();
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("spend.ledger.queries-and-totals-match-the-reference-rules", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-query", new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.Zero));
            var ledger = fixture.Ledger;
            var inserted = new List<string>();
            async Task Add(string op, DateTimeOffset at, SpendEstimate estimate, SpendSource source, string? run, Func<Task>? end = null, string? note = null)
            {
                fixture.Clock.Now = at;
                Check((await ledger.ReserveAsync(LedgerRequest(op, estimate, source, run) with { Note = note, Approved = true })).Reserved, "seeded " + op);
                inserted.Add(op);
                if (end is not null) await end();
            }
            var today = new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero);
            await Add("r01", new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero), LedgerEstimate(90_000), SpendSource.Chat, "chat-old",
                () => ledger.SettleAsync("r01", new SpendUsage(1_000, 100) { CostMicros = 3_000 }));
            await Add("r02", today.AddHours(9), LedgerEstimate(120_000, "openai", "env-openai", model: "gpt-5.6-terra"), SpendSource.Arena, "arena-1",
                () => ledger.SettleAsync("r02", new SpendUsage(1_240, 2_410) { CostMicros = 31_400 }), "revealed after your vote");
            await Add("r03", today.AddHours(9), LedgerEstimate(80_000, "openai-codex", "personal", BillingKind.Subscription, model: "gpt-6.1-sol"), SpendSource.Arena, "arena-1",
                () => ledger.SettleAsync("r03", new SpendUsage(1_240, 1_980) { CostMicros = 22_280 }), "revealed after your vote");
            await Add("r04", today.AddHours(10).AddMinutes(52), LedgerEstimate(295_937), SpendSource.CacheWarm, "chat-1",
                () => ledger.SettleAsync("r04", new SpendUsage(0, 1, 147_968) { CostMicros = 14_807 }), "maxTokens 1 · counts as cache waste");
            await Add("r05", today.AddHours(11).AddMinutes(18), LedgerEstimate(null, "work-gateway", "env-contoso", BillingKind.Unknown, model: "llama-4-maverick"),
                SpendSource.Playground, "r_01JA9Y8C2V", () => ledger.SettleAsync("r05", new SpendUsage(2_410, 512)), "asked · sent once");
            await Add("r06", today.AddHours(11).AddMinutes(24).AddSeconds(12), LedgerEstimate(380_000), SpendSource.Chat, "chat-1",
                () => ledger.MarkUncertainAsync("r06", "stream dropped, no usage reported", 317_140));
            await Add("r07", today.AddHours(11).AddMinutes(26), LedgerEstimate(100_000), SpendSource.Chat, "chat-1",
                () => ledger.SettleAsync("r07", new SpendUsage(1_072, 1_872, 147_968) { CostMicros = 35_700 }), "retry after restart");
            await Add("r08", today.AddHours(11).AddMinutes(30), LedgerEstimate(60_000, "google", "acc-g", model: "gemini-3.8-flash"), SpendSource.Compare, "cmp-1",
                () => ledger.ReleaseAsync("r08", "HTTP 429 · 3 attempts"));
            await Add("r09", today.AddHours(11).AddMinutes(30), LedgerEstimate(0, "lmstudio", null, BillingKind.Free, price: new SpendPrice(0, 0), model: "qwen-3-coder"), SpendSource.Chat,
                "chat-2", () => ledger.SettleAsync("r09", new SpendUsage(900, 300)));
            await Add("r10", today.AddHours(-3), LedgerEstimate(200_000), SpendSource.Chat, "chat-3", () => ledger.SettleAtEstimateAsync("r10", 150_000, "cancelled after output"));
            await Add("r11", today.AddHours(12), LedgerEstimate(70_000), SpendSource.Bench, "bench-1", note: "Résumé check");
            await Add("r12", today.AddHours(12).AddTicks(5_000), LedgerEstimate(70_000, account: null), SpendSource.Drift, null);

            var rows = new List<Reservation>();
            foreach (var op in inserted) rows.Add((await ledger.GetAsync(op))!);
            var expectedOrder = rows.Select((row, index) => (row, index)).OrderByDescending(pair => pair.row.Created).ThenByDescending(pair => pair.index).Select(pair => pair.row).ToList();
            var queries = new SpendLedgerQuery[]
            {
                new(), new() { Provider = "anthropic" }, new() { Model = " claude-sonnet-5-5 " }, new() { Account = "acc-1" }, new() { Source = SpendSource.Arena },
                new() { Status = LedgerStatus.Uncertain }, new() { Status = LedgerStatus.Estimate }, new() { Status = LedgerStatus.Settled }, new() { Status = LedgerStatus.Released },
                new() { Status = LedgerStatus.Reserved }, new() { Billing = BillingKind.Subscription }, new() { Billing = BillingKind.Unknown }, new() { Run = "chat-1" },
                new() { Search = "STREAM" }, new() { Search = "RÉSUMÉ" }, new() { Search = "cache-warm" }, new() { Search = "r_01ja9y" }, new() { Search = "nothing like it" },
                new() { From = today, To = today.AddDays(1) }, new() { From = today.AddHours(12).AddTicks(1) }, new() { To = today.AddHours(12).AddTicks(5_000) },
                new() { From = SpendWindows.MonthStart(today), Provider = "anthropic", Status = LedgerStatus.Settled },
                new() { Limit = 3, Offset = 2 }, new() { Limit = 4, Offset = 10 }, new() { Source = SpendSource.Chat, Limit = 2, Offset = 1 },
            };
            foreach (var query in queries)
            {
                var normalized = query.Normalize();
                var matching = expectedOrder.Where(normalized.Matches).ToList();
                var page = await ledger.QueryAsync(query);
                var what = System.Text.Json.JsonSerializer.Serialize(query, SettingsStore.JsonOptions);
                Equal(string.Join(",", matching.Skip(normalized.Offset).Take(normalized.Limit).Select(row => row.Op)), string.Join(",", page.Items.Select(row => row.Op)), "rows of " + what);
                Equal((matching.Count, rows.Count), (page.Total, page.TotalAll), "counts of " + what);
                Equal(SpendTotal.Of(matching), await ledger.TotalAsync(query), "the total of " + what);
            }
            var all = await ledger.QueryAsync(new SpendLedgerQuery { Limit = 100 });
            Check(all.Items.SequenceEqual(expectedOrder), "rows read back exactly, newest first");
            var day = await ledger.TotalAsync(new SpendLedgerQuery { From = today, To = today.AddDays(1) });
            Equal((31_400L + 14_807 + 380_000 + 35_700 + 70_000 + 70_000, 380_000L, 22_280L, 10, 1, 1),
                (day.CountedMicros, day.UncertainMicros, day.ListMicros, day.Requests, day.UnknownPriceRequests, day.FreeRequests), "today's totals");

            Equal("35700,14807,3000", string.Join(",", await ledger.RecentActualsAsync("anthropic", "claude-sonnet-5-5")), "actual settlements only, newest first");
            Equal("35700", string.Join(",", await ledger.RecentActualsAsync(" anthropic ", "claude-sonnet-5-5", 1)), "the last one");
            Equal(0, (await ledger.RecentActualsAsync("anthropic", "claude-opus-5-5")).Count, "none for another model");
            await ThrowsAsync<ArgumentOutOfRangeException>(() => ledger.RecentActualsAsync("anthropic", "claude-sonnet-5-5", 0), "a count of 0");
            await ThrowsAsync<ArgumentException>(() => ledger.QueryAsync(new SpendLedgerQuery { Limit = 0 }), "a page of 0 rows");
        }),

        Case("spend.ledger.cache-meter-saved-and-wasted-per-utc-month", async () =>
        {
            var t0 = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
            await using var fixture = LedgerFixture.Create("spend-ledger-cache", t0);
            var ledger = fixture.Ledger;
            var price = new SpendPrice(2, 10, 0.10, 2.50);
            var estimate = LedgerEstimate(400_000, price: price);
            async Task<Reservation> Turn(string op, DateTimeOffset at, SpendUsage usage, SpendSource source = SpendSource.Chat, string run = "chat-c", SpendEstimate? with = null)
            {
                fixture.Clock.Now = at;
                await ledger.ReserveAsync(LedgerRequest(op, with ?? estimate, source, run));
                fixture.Clock.Now = at.AddSeconds(5);
                return await ledger.SettleAsync(op, usage);
            }
            var t1 = new SpendUsage(1_072, 500, 0, 140_000);
            var t2 = new SpendUsage(1_000, 300, 141_072, 0);
            var t3 = new SpendUsage(1_200, 410, 0, 142_372);
            await Turn("t1", t0, t1);
            await Turn("t2", t0.AddMinutes(1), t2);
            await Turn("t3", t0.AddMinutes(20), t3);
            var warm = await Turn("w1", t0.AddMinutes(21), new SpendUsage(0, 1, 147_968), SpendSource.CacheWarm);
            Equal(14_807L, warm.ActualMicros, "the cache warm: 147,968 cached x $0.10/M + 1 out x $10/M");
            await Turn("sub", t0.AddMinutes(22), new SpendUsage(1_000, 100, 200_000), run: "chat-s",
                with: LedgerEstimate(100_000, "openai-codex", "personal", BillingKind.Subscription, price, "gpt-6.1-sol"));
            await Turn("nov", new DateTimeOffset(2026, 11, 1, 0, 0, 1, TimeSpan.Zero), new SpendUsage(1_000, 100, 100_000), run: "chat-n");

            Equal(268_037L, price.CacheSavedMicros(141_072), "t2 saved 141,072 x ($2.00 - $0.10)/M");
            var rows = ReadDb(fixture.Path, "SELECT op, cache_saved_micros, cache_missed_micros FROM spend_reservations ORDER BY created_at",
                reader => (Op: reader.GetString(0), Saved: reader.GetInt64(1), Missed: reader.GetInt64(2))).ToDictionary(row => row.Op);
            Equal((0L, 0L), (rows["t1"].Saved, rows["t1"].Missed), "the first turn has no previous request");
            Equal((268_037L, 0L), (rows["t2"].Saved, rows["t2"].Missed), "a hit");
            Check(rows["t3"].Missed > 0 && rows["t3"].Saved == 0, "t3 re-sent the whole prompt after an idle gap: a miss");
            Equal((0L, 0L), (rows["w1"].Saved, rows["w1"].Missed), "a cache warm is all waste, neither saved nor a miss");

            // The miss rule agrees with CacheStats on the same three turns.
            var entries = new[] { LedgerTurnEntry("e1", null, t0, t1, price), LedgerTurnEntry("e2", "e1", t0.AddMinutes(1), t2, price), LedgerTurnEntry("e3", "e2", t0.AddMinutes(20), t3, price) };
            var misses = CacheStats.CollectCacheMisses(entries, new DelegateModelPriceSource((_, _) => new ModelCost(price.Input, price.Output, price.CacheRead, price.CacheWrite)));
            var miss = misses.Values.Single();
            Equal(142_072d, miss.MissedTokens, "CacheStats counts 142,072 missed tokens");
            Equal(miss.MissedTokens, SpendCacheMiss.MissedTokens(SpendCacheMiss.PromptTokens(t2), t3, reportedCache: true), "and so does the ledger");
            Check(Math.Abs(SpendMoney.FromUsd(miss.MissedCost) - rows["t3"].Missed) <= 1, $"the same cost: {miss.MissedCost} vs {rows["t3"].Missed} micro-USD");

            var october = await ledger.CacheMeterAsync(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.FromHours(-7)));
            Equal((268_037L, rows["t3"].Missed + 14_807), (october.SavedMicros, october.WastedMicros), "October: t2's saving; t3's miss and the cache warm; not the subscription");
            Equal(october, await ledger.CacheMeterAsync(t0, "anthropic"), "the same for anthropic");
            Equal(new SpendCacheMeter(0, 0), await ledger.CacheMeterAsync(t0, "openai-codex"), "subscription use saves and wastes no money");
            Equal(new SpendCacheMeter(price.CacheSavedMicros(100_000), 0), await ledger.CacheMeterAsync(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero)), "November");

            // Parallel requests of one run never count as each other's previous request.
            fixture.Clock.Now = t0.AddMinutes(30);
            await ledger.ReserveAsync(LedgerRequest("p1", estimate, SpendSource.Compare, "cmp-c"));
            await ledger.ReserveAsync(LedgerRequest("p2", estimate, SpendSource.Compare, "cmp-c"));
            fixture.Clock.Now = t0.AddMinutes(31);
            await ledger.SettleAsync("p1", new SpendUsage(10_000, 100, 0, 50_000));
            await ledger.SettleAsync("p2", new SpendUsage(60_000, 100, 0, 0));
            Equal(0L, ReadDb(fixture.Path, "SELECT cache_missed_micros FROM spend_reservations WHERE op = 'p2'", reader => reader.GetInt64(0)).Single(), "no miss between parallel columns");
        }),

        Case("spend.ledger.cap-status-uses-the-warning-threshold", async () =>
        {
            await using var fixture = LedgerFixture.Create("spend-ledger-status");
            var ledger = fixture.Ledger;
            await ledger.ReserveAsync(LedgerRequest("seed", LedgerEstimate(2_600_000), run: "chat-1"));
            await ledger.ReserveAsync(LedgerRequest("sub", LedgerEstimate(9_000_000, "openai-codex", "personal", BillingKind.Subscription), run: "chat-1"));
            var request = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Request, 500_000, CapAction.Ask));
            var run = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Run, 5_000_000, CapAction.Ask));
            var day = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, 5_000_000, CapAction.Block));
            var month = await ledger.SaveCapAsync(new SpendCap(0, CapScope.Provider, "anthropic", CapPeriod.Month, 10_000_000, CapAction.Block, Enabled: false));

            var statuses = await ledger.CapStatusAsync(" chat-1 ");
            Equal(string.Join(",", (await ledger.CapsAsync()).Select(cap => cap.Id)), string.Join(",", statuses.Select(status => status.Cap.Id)), "in the caps' order");
            var byId = statuses.ToDictionary(status => status.Cap.Id);
            Check(byId[request.Id] is { SpentMicros: null, WindowStart: null, Warning: false }, "a request cap has no running amount");
            Check(byId[run.Id] is { SpentMicros: 2_600_000, PercentShown: 52, Warning: false, WindowStart: null }, "the chat's run: 52%, the subscription not counted");
            Check(byId[day.Id] is { SpentMicros: 2_600_000, PercentShown: 52, Warning: false, WarnPercent: 80 }, "below 80%");
            Check(byId[month.Id] is { SpentMicros: 2_600_000, Cap.Enabled: false }, "a disabled cap still shows what counts toward it");
            Check((await ledger.CapStatusAsync()).Single(status => status.Cap.Id == run.Id).SpentMicros is null, "no run named: no run amount");

            await fixture.Settings.UpdateAsync<SpendSettings>(spend => spend with { WarnPercent = 50 });
            var amber = (await ledger.CapStatusAsync("chat-1")).ToDictionary(status => status.Cap.Id);
            Check(amber[day.Id] is { Warning: true, WarnPercent: 50 } && amber[run.Id].Warning, "at 50% the same caps are amber");
            var warned = await ledger.CheckAsync(LedgerRequest("next", LedgerEstimate(100_000), run: "chat-2"));
            Check(warned.Allowed && warned.Warnings.Any(status => status.Cap == day && status.SpentMicros == 2_700_000), "a decision's warnings use the setting too");
        }),
    ];

    private static IWebAgentMigration[] LedgerMigrations => [CoreMigrations.AppInfo, SettingsStore.Migration, .. SpendLedgerMigrations.All];

    /// <summary>A metered estimate of <paramref name="model"/> (<c>anthropic/claude-sonnet-5-5</c>) through <paramref name="account"/>, 12,000
    /// input and 8,192 output tokens at <paramref name="price"/> ($2/$10 per million), with this worst case (null: an unknown price).</summary>
    private static SpendEstimate LedgerEstimate(long? worst, string provider = "anthropic", string? account = "acc-1", BillingKind billing = BillingKind.Metered,
        SpendPrice? price = null, string model = "claude-sonnet-5-5") => new()
    {
        Provider = provider,
        Model = model,
        Account = account,
        Billing = billing,
        Price = worst is null ? null : price ?? new SpendPrice(2, 10),
        InputTokens = 12_000,
        OutputTokens = 8_192,
        WorstMicros = worst,
    };

    private static SpendRequest LedgerRequest(string op, SpendEstimate estimate, SpendSource source = SpendSource.Chat, string? run = "chat-1") =>
        new() { Op = op, Source = source, Run = run, Estimate = estimate };

    /// <summary>An assistant message entry of a Pi session with <paramref name="usage"/> priced at <paramref name="price"/>, for CacheStats.</summary>
    private static SessionEntry LedgerTurnEntry(string id, string? parent, DateTimeOffset at, SpendUsage usage, SpendPrice price)
    {
        static double Cost(double tokens, double rate) => tokens * rate / 1_000_000;
        var cost = new JsonObject
        {
            ["input"] = Cost(usage.InputTokens, price.Input), ["output"] = Cost(usage.OutputTokens, price.Output),
            ["cacheRead"] = Cost(usage.CacheReadTokens, price.CacheRead), ["cacheWrite"] = Cost(usage.CacheWriteTokens, price.CacheWrite),
            ["total"] = Cost(usage.InputTokens, price.Input) + Cost(usage.OutputTokens, price.Output) + Cost(usage.CacheReadTokens, price.CacheRead) +
                Cost(usage.CacheWriteTokens, price.CacheWrite),
        };
        var entry = new JsonObject
        {
            ["type"] = "message", ["id"] = id, ["parentId"] = parent, ["timestamp"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["message"] = new JsonObject
            {
                ["role"] = "assistant", ["content"] = new JsonArray(), ["api"] = "anthropic-messages", ["provider"] = "anthropic", ["model"] = "claude-sonnet-5-5",
                ["usage"] = new JsonObject
                {
                    ["input"] = usage.InputTokens, ["output"] = usage.OutputTokens, ["cacheRead"] = usage.CacheReadTokens, ["cacheWrite"] = usage.CacheWriteTokens,
                    ["totalTokens"] = usage.InputTokens + usage.OutputTokens + usage.CacheReadTokens + usage.CacheWriteTokens, ["cost"] = cost,
                },
                ["stopReason"] = "stop", ["timestamp"] = at.ToUnixTimeMilliseconds(),
            },
        };
        return new SessionEntryCodec().Parse(entry.ToJsonString());
    }

    /// <summary>A clock the cases move.</summary>
    private sealed class LedgerClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    /// <summary>A temporary webagent.db with the ledger's tables, its settings and a ledger on a movable clock.</summary>
    private sealed class LedgerFixture : IAsyncDisposable
    {
        private LedgerFixture(string root, DateTimeOffset now)
        {
            Root = root;
            Path = System.IO.Path.Join(root, "webagent.db");
            Db = new WebAgentDb(Path, LedgerMigrations);
            Db.Migrate();
            Settings = new SettingsStore(Db);
            Clock = new LedgerClock(now);
            Ledger = new SpendLedger(Db, Settings, Clock);
        }

        public string Root { get; }

        public string Path { get; }

        public WebAgentDb Db { get; }

        public SettingsStore Settings { get; }

        public LedgerClock Clock { get; }

        public SpendLedger Ledger { get; }

        public static LedgerFixture Create(string name, DateTimeOffset? now = null) =>
            new(Temp(name), now ?? new DateTimeOffset(2026, 10, 29, 11, 0, 0, TimeSpan.Zero));

        /// <summary>Another ledger on the same file, as the next run of the app would open it.</summary>
        public SpendLedger Reopen() => new(new WebAgentDb(Path, LedgerMigrations), Settings, Clock);

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteTemp(Root);
            return ValueTask.CompletedTask;
        }
    }
}
