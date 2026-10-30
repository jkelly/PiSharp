using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Spend;
using ChordJson = PiSharp.Durable.Chord.JsonValue;

// Decision 0011 (run W3, S5): spend in chat. The render cases build the guard's decisions and the ledger's rows as S3 writes them and check
// the server-rendered states of the chat pages: the composer's estimate by Send (metered, list price, nothing for free, price unknown, the
// caps near their limit) and its over-cap chip (Ask, Block, a chat cap, no price), the tooltip built from encoded parts only; the answers'
// footer marks linked to their ledger rows (metered, list, settled at an estimate, uncertain with Resolve…, price unknown, the running
// answer's live estimate); a call the guard stopped inside the run (the stop card and the banner, with Review…); the session panel's chat
// spend. The app cases run the built app with --faux-metered (offline, priced faux models): a blocked send posts nothing and reserves
// nothing, "proceed once" posts once for a retried request id, "cap the output" sends at the chosen cap, a refused new chat creates no chat,
// and a call stopped inside a run is reviewed and continued after the user's choice.
//
// W3 fix (chat spend): the approval a send gets answers only the call that sends its message. In process (the guard's scripted world, and
// the Chat area over guarded faux models with its tools asking first): the running turn's next call, a compaction, the same text placed
// earlier and another message never use it, the message's own call does once; a message queued behind a running turn keeps its approval
// while that turn's next call is stopped, and withdrawn takes it along; an approval its call did not need is retired, and a send nobody answered is asked about even with
// an approval waiting for the chat; a send that fails after its approval withdraws it and its retry is checked again. In the built app,
// parallel copies of one proceed-once POST store one approval and post once.
//
// W3 fix 2 (chat spend): a stop that ended the run says, on its card and in the spend banner (page and stream), that the messages queued
// behind it wait until the user continues or sends another message (the durable engine starts queued messages only after an answer); and
// what a send notes after its message is posted never fails the send, also on a host whose webagent.db lacks the chat sends table.
internal static partial class Program
{
    private static readonly SpendPrice SpendTestPrice = new(3, 15, 0.3);

    /// <summary>An estimate of 12,000 input and 8,192 output tokens at $3 / $15 per million (worst case $0.1589), or no price.</summary>
    private static SpendEstimate SpendEstimateFor(BillingKind billing, long? typical = null, bool priced = true, string provider = "anthropic",
        string model = "claude-x") => new()
    {
        Provider = provider,
        Model = model,
        Billing = billing,
        Price = billing == BillingKind.Free ? new SpendPrice(0, 0) : priced ? SpendTestPrice : null,
        InputTokens = 12_000,
        OutputTokens = 8_192,
        WorstMicros = billing == BillingKind.Free ? 0 : priced ? 158_880 : null,
        TypicalMicros = typical,
    };

    /// <summary>A ledger row of the chat "ui-chat".</summary>
    private static Reservation SpendRow(string op, ReservationState state, double time, BillingKind billing = BillingKind.Metered, SpendUsage? usage = null,
        long? actual = null, long? reserved = 158_880, SettlementKind? settlement = null, string provider = "faux", string model = "echo", string? reason = null) => new()
    {
        Op = op,
        Provider = provider,
        Model = model,
        Source = SpendSource.Chat,
        Run = "ui-chat",
        Billing = billing,
        State = state,
        ReservedMicros = billing == BillingKind.Unknown ? null : reserved,
        ActualMicros = actual,
        Settlement = settlement,
        InputTokens = 1_000,
        OutputTokens = 8_192,
        Price = billing is BillingKind.Unknown or BillingKind.Free ? null : SpendTestPrice,
        Usage = usage,
        Reason = reason,
        Created = DateTimeOffset.FromUnixTimeMilliseconds((long)time),
    };

    /// <summary>Renders <paramref name="entries"/> (and a live message) with _ChatItem and the spend facts, as the page and the stream do, with
    /// <paramref name="queued"/> messages waiting in the branch's queue.</summary>
    private static async Task<string> SpendRenderAsync(RenderHost host, IReadOnlyList<JsonObject> entries, ChatSpendFacts spend, ChatRunState? run = null,
        JsonObject? live = null, int queued = 0)
    {
        var items = Transcript.Build(entries, live);
        var context = new ChatRenderContext
        {
            SessionId = "ui-chat",
            AgentId = 1,
            Markdown = host.Services.GetRequiredService<MarkdownRenderer>(),
            AccountLabel = "Personal",
            Provider = "faux",
            Billing = BillingKind.Metered,
            Facts = ChatFacts.Build(items, entries, run),
            Results = ChatFacts.ToolResults(entries),
            Spend = spend,
            Stop = ChatSpendStopView.Newest(entries, queued),
        };
        var html = new StringBuilder();
        foreach (var item in items) html.Append(await host.Renderer.RenderAsync("~/Views/Chat/_ChatItem.cshtml", new ChatItemView(item, context)));
        return html.ToString();
    }

    private static Task<string> SpendEstimateRenderAsync(RenderHost host, ChatSpendEstimateView view) =>
        host.Renderer.RenderAsync(ChatSpend.EstimatePartial, view);

    /// <summary>The tooltip's markup as Bootstrap shows it (its text decoded once more).</summary>
    private static string SpendTip(ChatSpendEstimateView view) => WebUtility.HtmlDecode(view.Tooltip);

    private static void SpendTipHas(ChatSpendEstimateView view, string part, string what) =>
        Check(SpendTip(view).Contains(part, StringComparison.Ordinal), $"{what}: expected <{part}> in the tooltip\n{SpendTip(view)}");

    /// <summary>The error of a call the guard stopped, as the durable engine stores it.</summary>
    private static string SpendStopError(CapAction action, string op = "op-9", CapPeriod period = CapPeriod.Request)
    {
        var cap = new SpendCap(4, CapScope.Everything, null, period, 50_000, action);
        var decision = SpendDecision.From(SpendEstimateFor(BillingKind.Metered, provider: "faux", model: "echo"),
            [new SpendCause { Kind = SpendCauseKind.Cap, Action = action, Cap = cap, LeftMicros = 50_000, FitOutputTokens = 2_000 }]);
        return SpendGuard.ErrorPrefix + SpendErrors.Message(decision, op);
    }

    internal static IEnumerable<(string, Func<Task>)> ChatSpendCases() =>
    [
        Case("chat.spend.estimate-by-send", async () =>
        {
            await using var host = UiHost("chat-spend-estimate");
            var names = new ChatSpendNames("Anthropic", "Work");

            var metered = ChatSpendText.Estimate(SpendDecision.Allow(SpendEstimateFor(BillingKind.Metered, typical: 30_000)), names);
            Equal(("≈ $0.03", (string?)null, "allow", (string?)null, (string?)null), (metered.Chip, metered.Over, metered.Verdict, metered.Pane, metered.Code), "a metered estimate");
            var html = await SpendEstimateRenderAsync(host, metered);
            Has(html, "class=\"composer-estimate run-estimate\" type=\"button\" data-spend-chip data-verdict=\"allow\"", "the chip by Send");
            Has(html, ">≈ $0.03</button>", "the typical cost");
            Has(html, "data-bs-html=\"true\" data-bs-custom-class=\"ps-tip-left\"", "its tooltip is markup");
            Has(html, metered.Tooltip, "the tooltip, encoded once more as the title");
            Has(html, "aria-label=\"Estimated cost: about $0.03 typical, $0.16 worst case\"", "its accessible name");
            SpendTipHas(metered, "<strong>≈ $0.03 typical</strong> · median of your last " + CostEstimator.TypicalSampleSize.ToString(CultureInfo.InvariantCulture) +
                " replies on this model", "typical");
            SpendTipHas(metered, "<br>Worst case $0.16: 12,000 × $3.00/M + 8,192 × $15.00/M = $0.0360 + $0.1229 = $0.1589", "the worst case worked out");
            SpendTipHas(metered, "default reservation cap of 8,192 output tokens", "where the output tokens came from");
            SpendTipHas(metered, "Anthropic · Work is metered: counted toward your caps.", "how the account is billed");
            Lacks(html, "data-spend-over", "no over-cap chip");

            var worstOnly = ChatSpendText.Estimate(SpendDecision.Allow(SpendEstimateFor(BillingKind.Metered)), names);
            Equal("≈ $0.16", worstOnly.Chip, "without a typical cost, the worst case");
            SpendTipHas(worstOnly, "<strong>≈ $0.16 worst case</strong>", "says so");

            var list = ChatSpendText.Estimate(SpendDecision.Allow(SpendEstimateFor(BillingKind.Subscription, typical: 120_000)), names);
            Equal("≈ $0.12 list", list.Chip, "a subscription shows the list price");
            SpendTipHas(list, "Anthropic · Work is a subscription: list price only, never counted toward money caps.", "never counted");
            Has(await SpendEstimateRenderAsync(host, list), ">≈ $0.12 list</button>", "rendered");

            var free = ChatSpendText.Estimate(SpendDecision.Allow(SpendEstimateFor(BillingKind.Free)), names);
            Check(!free.Shown && free.Chip is null, "a free model shows nothing");
            Equal("", (await SpendEstimateRenderAsync(host, free)).Trim(), "and renders nothing");
            Equal("", (await SpendEstimateRenderAsync(host, ChatSpendEstimateView.None)).Trim(), "no guard renders nothing");

            var unpriced = ChatSpendText.Estimate(SpendDecision.Allow(SpendEstimateFor(BillingKind.Unknown, priced: false)), names);
            Equal("price unknown", unpriced.Chip, "a model without a price says so, with no number");
            SpendTipHas(unpriced, "anthropic/claude-x has no price, so it isn't counted toward money caps.", "and why");
            Lacks(SpendTip(unpriced), "$", "no amount nothing measured");

            var monthly = new SpendCap(6, CapScope.Provider, "anthropic", CapPeriod.Month, 10_000_000, CapAction.Ask);
            var warned = ChatSpendText.Estimate(SpendDecision.Allow(SpendEstimateFor(BillingKind.Metered, typical: 30_000),
                [new SpendCapStatus(monthly, 8_100_000, null, null, 80)]), names);
            Equal(("81% of anthropic monthly cap", "Anthropic monthly cap: $8.10 of $10.00 used this month, uncertain spend included. Requests ask first at $10.00."),
                (warned.Warnings.Single().Text, warned.Warnings[0].Title), "a cap near its limit");
            var warnedHtml = await SpendEstimateRenderAsync(host, warned);
            Has(warnedHtml, "data-tone=\"warn\" tabindex=\"0\" data-bs-toggle=\"tooltip\"", "the amber chip");
            Has(warnedHtml, "data-spend-warning>81% of anthropic monthly cap</span>", "its text");
            Has(warnedHtml, ">≈ $0.03</button>", "with the estimate");

            var hostile = ChatSpendText.Estimate(SpendDecision.Allow(SpendEstimateFor(BillingKind.Metered, typical: 30_000)),
                new ChatSpendNames("<img src=x onerror=alert(1)>", "<b>acct</b>"));
            var hostileHtml = await SpendEstimateRenderAsync(host, hostile);
            Check(hostile.Tooltip.Contains("&lt;img src=x", StringComparison.Ordinal), "names are encoded inside the tooltip's markup");
            Lacks(hostileHtml, "<img src=x", "so the tooltip can't carry markup");
            Lacks(hostileHtml, "<b>acct</b>", "nor can an account label");
        }),

        Case("chat.spend.estimate-over-cap-states", async () =>
        {
            await using var host = UiHost("chat-spend-over");
            var names = new ChatSpendNames("Anthropic", null);
            var estimate = SpendEstimateFor(BillingKind.Metered, typical: 30_000);

            var requestCap = new SpendCap(4, CapScope.Everything, null, CapPeriod.Request, 100_000, CapAction.Ask);
            var ask = ChatSpendText.Estimate(SpendDecision.From(estimate,
                [new SpendCause { Kind = SpendCauseKind.Cap, Action = CapAction.Ask, Cap = requestCap, LeftMicros = 100_000, FitOutputTokens = 6_266 }]), names);
            Equal(("Over request cap", (string?)null, "ask", "sc-request", SpendErrors.Ask), (ask.Over, ask.Chip, ask.Verdict, ask.Pane, ask.Code), "an Ask cap");
            var askHtml = await SpendEstimateRenderAsync(host, ask);
            Has(askHtml, "class=\"ps-chip run-estimate\" type=\"button\" data-tone=\"warn\" data-spend-over data-verdict=\"ask\" data-pane=\"sc-request\"", "the over-cap chip");
            Has(askHtml, ">Over request cap</button>", "named after the cause");
            Has(askHtml, "aria-label=\"Over the $0.10 per-request cap: worst case $0.16\"", "its accessible name");
            Lacks(askHtml, "data-spend-chip", "instead of the estimate");
            SpendTipHas(ask, "<strong>Over the $0.10 per-request cap</strong>", "the tooltip's head");
            SpendTipHas(ask, "Worst case $0.16: 12,000 × $3.00/M", "the worst case");
            SpendTipHas(ask, "The cap is set to Ask, so Send asks you first. Nothing is reserved until you choose.", "what Send does");

            var chatCap = new SpendCap(5, CapScope.Everything, null, CapPeriod.Run, 2_000_000, CapAction.Block);
            var block = ChatSpendText.Estimate(SpendDecision.From(estimate,
                [new SpendCause { Kind = SpendCauseKind.Cap, Action = CapAction.Block, Cap = chatCap, SpentMicros = 1_900_000, LeftMicros = 100_000, FitOutputTokens = 4_266 }]), names);
            Equal(("Over chat cap", "block", "sc-chat", SpendErrors.Blocked), (block.Over, block.Verdict, block.Pane, block.Code), "a Block chat cap");
            SpendTipHas(block, "<strong>Over the $2.00 per-chat cap</strong>", "named");
            SpendTipHas(block, "$1.90 used in this chat, $0.10 left.", "what the chat used");
            SpendTipHas(block, "The cap is set to Block, so Send can't send it as it is; capping the output at 4,266 tokens fits.", "Block offers no proceed");
            Has(await SpendEstimateRenderAsync(host, block), "data-spend-over data-verdict=\"block\" data-pane=\"sc-chat\"", "rendered");

            foreach (var (period, label) in new[] { (CapPeriod.Day, "Over daily cap"), (CapPeriod.Month, "Over monthly cap") })
                Equal(label, ChatSpendText.OverLabel(new SpendCause
                {
                    Kind = SpendCauseKind.Cap, Action = CapAction.Ask, Cap = new SpendCap(7, CapScope.Everything, null, period, 1_000_000, CapAction.Ask),
                }), "the chip of a " + period + " cap");

            var noPrice = ChatSpendText.Estimate(SpendDecision.From(SpendEstimateFor(BillingKind.Unknown, priced: false),
                [new SpendCause { Kind = SpendCauseKind.UnknownPrice, Action = CapAction.Ask }]), names);
            Equal(("No price", "ask", "sc-unknown", SpendErrors.UnknownPrice), (noPrice.Over, noPrice.Verdict, noPrice.Pane, noPrice.Code), "an unknown price set to Ask");
            SpendTipHas(noPrice, "<strong>No price for anthropic/claude-x</strong>", "named");
            SpendTipHas(noPrice, "Unknown prices are set to Ask, so Send asks you first.", "what Send does");
            var blockedPrice = ChatSpendText.Estimate(SpendDecision.From(SpendEstimateFor(BillingKind.Unknown, priced: false),
                [new SpendCause { Kind = SpendCauseKind.UnknownPrice, Action = CapAction.Block }]), names);
            Equal(("block", SpendErrors.Blocked), (blockedPrice.Verdict, blockedPrice.Code), "an unknown price set to Block");
            SpendTipHas(blockedPrice, "Unknown prices are set to Block, so Send can't send it. Enter a price in Settings › Spend.", "says where to fix it");
        }),

        Case("chat.spend.footer-marks", async () =>
        {
            await using var host = UiHost("chat-spend-footer");
            JsonObject[] entries =
            [
                UiUser(1, "go", UiTime),
                UiAnswer(2, "Done.", input: 1072, output: 1872, cacheRead: 147_968, cost: 0.0357, durationMs: 21_400, time: UiTime + 1_000),
                UiUser(3, "again", UiTime + 10_000),
                UiAnswer(4, "Listed.", input: 2000, output: 3000, cost: 0.0509, time: UiTime + 11_000, provider: "sub", model: "codex"),
                UiUser(5, "more", UiTime + 20_000),
                UiAnswer(6, "All 42 tests pa", stop: "aborted", time: UiTime + 25_000),
                UiUser(7, "and", UiTime + 30_000),
                UiAnswer(8, "Partly", stop: "aborted", time: UiTime + 35_000),
                UiUser(9, "local", UiTime + 40_000),
                UiAnswer(10, "Local answer.", input: 10, output: 10, time: UiTime + 41_000, provider: "local", model: "llama"),
            ];
            Reservation[] rows =
            [
                SpendRow("op-2", ReservationState.Settled, UiTime + 500, usage: new SpendUsage(1072, 1872, 147_968), actual: 35_700, settlement: SettlementKind.Actual),
                SpendRow("op-2-later", ReservationState.Settled, UiTime + 100_000, usage: new SpendUsage(1072, 1872, 147_968), actual: 35_700, settlement: SettlementKind.Actual),
                SpendRow("op-4", ReservationState.Settled, UiTime + 10_500, BillingKind.Subscription, new SpendUsage(2000, 3000), 50_900, settlement: SettlementKind.Actual,
                    provider: "sub", model: "codex"),
                SpendRow("op-6", ReservationState.Uncertain, UiTime + 20_500, reserved: 380_000, reason: "stream dropped, no usage reported"),
                SpendRow("op-8", ReservationState.Settled, UiTime + 30_500, actual: 21_300, settlement: SettlementKind.Estimate, reason: "you stopped it"),
                SpendRow("op-10", ReservationState.Settled, UiTime + 40_500, BillingKind.Unknown, new SpendUsage(10, 10), settlement: SettlementKind.Actual,
                    provider: "local", model: "llama"),
            ];
            var facts = ChatSpendText.Link(entries, rows, running: false, row => ChatSpendText.Mark(row, "Personal"));
            Equal("2:op-2,4:op-4,6:op-6,8:op-8,10:op-10", string.Join(",", facts.Marks.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value.Op}")),
                "each answer finds its own row: by usage nearest in time, else by time");
            Check(facts.Live is null, "nothing runs");
            var html = await SpendRenderAsync(host, entries, facts);
            Has(html, "data-spend-mark=\"metered\">$0.0357</span>", "metered: what it cost");
            Equal(1, Regex.Matches(WebUtility.HtmlDecode(html), Regex.Escape(">$0.0357<")).Count, "shown once (the ledger's mark replaces the message's own cost)");
            Has(html, "Metered at catalog rates: 1,072 in × $3.00/M + 1,872 out × $15.00/M + 147,968 cached × $0.30/M. Counted toward your caps.", "worked out on hover");
            Has(html, "data-spend-mark=\"list\">≈ $0.0509 list</span>", "a subscription's list price");
            Has(html, "Billed through the subscription (Personal), so it isn't counted toward money caps.", "never counted");
            Has(html, "<span class=\"ps-chip\" data-tone=\"warn\" tabindex=\"0\" data-bs-toggle=\"tooltip\"", "the uncertain chip");
            Has(html, ">uncertain</span>", "says uncertain");
            Has(html, "data-spend-mark=\"uncertain\">$0.38 reserved</span>", "the amount that counts until it is resolved");
            Has(html, "so the $0.38 reserved for this request keeps counting toward your caps until you resolve it.", "and why");
            Has(html, "<a class=\"text-ps-faint\" href=\"/history/spend?resolve=op-6\" data-spend-resolve>Resolve…</a>", "Resolve on History › Spend");
            Has(html, "data-spend-mark=\"estimate\">≈ $0.0213 · est.</span>", "settled at an estimate");
            Has(html, "Settled at an estimate from what was received (1,000 in estimated): you stopped it. Counted toward your caps.", "and how");
            Has(html, "data-spend-mark=\"unknownprice\">price unknown</span>", "a model without a price, with no number");
            Equal(1, Regex.Matches(html, "data-spend-resolve").Count, "only the uncertain answer offers Resolve");

            JsonObject[] running = [UiUser(1, "go", UiTime)];
            var held = SpendRow("op-live", ReservationState.Reserved, UiTime + 100, reserved: 125_000);
            var liveFacts = ChatSpendText.Link(running, [held], running: true, row => ChatSpendText.Mark(row, null));
            Check(ReferenceEquals(held, liveFacts.Live), "the running request's reservation");
            var liveHtml = await SpendRenderAsync(host, running, liveFacts, new ChatRunState(true, true, false, false), UiLive("Running the registry tests now"));
            const string streamed = "Running the registry tests now";
            var streamedTokens = CostEstimator.InputTokens(Encoding.UTF8.GetByteCount(streamed));
            string LiveText(double tokens, string unit = "") =>
                $"≈ {SpendMoney.Format(SpendTestPrice.InputMicros(1_000) + SpendTestPrice.OutputMicros(tokens), null)}{unit} · est.";
            Has(liveHtml, $"data-spend-mark=\"live\">{LiveText(streamedTokens)}</span>", "the running answer's estimate from the tokens received so far");
            Has(liveHtml, $"Estimate from the tokens received so far: 1,000 in × $3.00/M + ~{SpendEstimateText.Tokens(streamedTokens)} out × $15.00/M.", "worked out");
            var shortItem = (AssistantItem)Transcript.Build(running, UiLive("Run")).Last();
            var longItem = (AssistantItem)Transcript.Build(running, UiLive(new string('x', 3_000))).Last();
            Check(liveFacts.Print(shortItem) != liveFacts.Print(longItem), "the live mark follows the stream (it is part of the fingerprint)");
            Equal(LiveText(CostEstimator.InputTokens(3_000)), liveFacts.LiveFor(longItem)!.Text, "3,000 bytes received: about 1,100 tokens out");
            Check(liveFacts.LiveFor(longItem)!.Text.StartsWith("≈ $0.019", StringComparison.Ordinal), "about $0.0195");
            Equal(LiveText(1_000, " list"), ChatSpendText.Live(held with { Billing = BillingKind.Subscription }, 1_000)!.Text, "a subscription's live list price");
            Check(ChatSpendText.Live(held with { Billing = BillingKind.Free }, 1_000) is null, "a free model shows none");
            Equal("price unknown", ChatSpendText.Live(held with { Price = null, ReservedMicros = null, Billing = BillingKind.Unknown }, 1_000)!.Text, "nor a number without a price");
        }),

        Case("chat.spend.footer-link-rules", () =>
        {
            JsonObject[] entries =
            [
                UiUser(1, "go", UiTime),
                UiAnswer(2, "One.", input: 100, output: 50, time: UiTime + 1_000),
                UiUser(3, "again", UiTime + 2_000),
                UiAnswer(4, "", stop: "error", error: SpendStopError(CapAction.Block), time: UiTime + 3_000),
                UiUser(5, "later", UiTime + 500_000),
                UiAnswer(6, "Dropped", stop: "aborted", time: UiTime + 501_000),
                UiAnswer(7, "Free one.", input: 5, output: 5, time: UiTime + 502_000, provider: "free", model: "local"),
            ];
            Reservation[] rows =
            [
                SpendRow("op-other-model", ReservationState.Settled, UiTime + 900, usage: new SpendUsage(100, 50), actual: 1_050, settlement: SettlementKind.Actual, model: "other"),
                SpendRow("op-2", ReservationState.Settled, UiTime + 400, usage: new SpendUsage(100, 50), actual: 1_050, settlement: SettlementKind.Actual),
                SpendRow("op-old-uncertain", ReservationState.Uncertain, UiTime + 2_500),
                SpendRow("op-free", ReservationState.Settled, UiTime + 501_500, BillingKind.Free, new SpendUsage(5, 5), 0, settlement: SettlementKind.Actual,
                    provider: "free", model: "local"),
                SpendRow("op-released", ReservationState.Released, UiTime + 3_100),
            ];
            var facts = ChatSpendText.Link(entries, rows, running: false, row => ChatSpendText.Mark(row, null));
            Equal("op-2", facts.For(2)?.Op, "the same usage of the same model, not another model's");
            Check(facts.For(4) is null, "a call the guard stopped has no row and no mark");
            Check(facts.For(6) is null, "an answer without usage only takes a row within two minutes (the uncertain row is 498 s away)");
            Check(facts.For(7) is null, "a free request shows no cost");
            Check(facts.For(null) is null && facts.For(99) is null, "an unknown entry");
            Check(ChatSpendText.Mark(rows[4], null) is null, "a released request shows none");
            Check(ChatSpendText.Mark(SpendRow("op-0", ReservationState.Settled, UiTime, actual: 0, settlement: SettlementKind.Actual), null) is null,
                "nor one that cost nothing");
            var reserved = SpendRow("op-r", ReservationState.Reserved, UiTime + 600_000);
            Check(ChatSpendText.Link(entries, [.. rows, reserved], running: false, row => ChatSpendText.Mark(row, null)).Live is null, "no live row while idle");
            Check(ChatSpendText.Link(entries, [.. rows, reserved], running: true, row => ChatSpendText.Mark(row, null)).Live?.Op == "op-r", "the held row while running");
            Equal("mark:", ChatSpendFacts.Empty.Print((AssistantItem)Transcript.Build(entries, null).Last()), "no ledger, no mark");
        }),

        Case("chat.spend.stop-card-and-banner", async () =>
        {
            await using var host = UiHost("chat-spend-stop");
            var blocked = SpendStopError(CapAction.Block);
            JsonObject[] entries = [UiUser(1, "blocked please", UiTime), UiAnswer(2, "", stop: "error", error: blocked, time: UiTime + 1_000)];
            Equal(ChatFailureKind.Spend, ChatFailures.Classify(ChatEntryMeta.Of(entries[1])?.StopReason, blocked), "a spend stop is its own failure");
            var html = await SpendRenderAsync(host, entries, ChatSpendFacts.Empty);
            Has(html, "data-failure=\"spend\"", "the stopped answer");
            Has(html, "class=\"msg-error msg-spend mt-2\" role=\"alert\" data-spend-stop data-op=\"op-9\" data-code=\"spend_blocked\"", "the stop card names its op");
            Has(html, ">Stopped by a spend cap</div>", "its heading");
            Has(html, "This request could cost up to $0.1589, over the per-request cap of $0.05. It is set to Block, so the request can't be sent as it is. Nothing was sent or reserved.",
                "why, and that nothing was sent");
            Has(html, "data-action=\"spend-review\" data-op=\"op-9\" data-entry=\"2\"", "Review opens the stop's modal");
            Has(html, ">Review…</button>", "a Block offers no proceed");
            Has(html, "href=\"/settings/spend#caps\">Settings › Spend</a>", "and links to Settings › Spend");
            Has(html, "data-tone=\"warn\">stopped</span>", "the footer says stopped");
            Has(html, "nothing sent · nothing counted", "and that nothing counted");
            Lacks(html, "Provider error", "not a provider error");
            Lacks(html, "data-action=\"retry\"", "no Retry: the cap would stop it again");
            Lacks(html, "[spend op=", "the error's tail stays out of the page");

            var asked = SpendStopError(CapAction.Ask, "op-ask");
            var askHtml = await SpendRenderAsync(host, [UiUser(1, "go", UiTime), UiAnswer(2, "", stop: "error", error: asked, time: UiTime + 1_000)], ChatSpendFacts.Empty);
            Has(askHtml, ">Over a spend cap · waiting for your OK</div>", "an Ask cap waits for the user");
            Has(askHtml, ">Review and continue…</button>", "and continues after the choice");
            Has(askHtml, "data-code=\"spend_ask\"", "its code");

            var unknown = SpendGuard.ErrorPrefix + SpendErrors.Message(SpendDecision.From(SpendEstimateFor(BillingKind.Unknown, priced: false, provider: "faux", model: "echo"),
                [new SpendCause { Kind = SpendCauseKind.UnknownPrice, Action = CapAction.Ask }]), "op-u");
            var unknownStop = ChatSpendStopView.From(unknown, 2)!;
            Equal(("No price for this model", "/settings/spend#unknown-prices", true), (unknownStop.Title, unknownStop.SettingsUrl, unknownStop.CanProceed), "an unknown price");
            Check(ChatSpendStopView.From("500 Internal Server Error", 2) is null && ChatSpendStopView.From(null, 2) is null, "other errors are not spend stops");

            var banner = await host.Renderer.RenderAsync("~/Views/Chat/_ChatBanners.cshtml", new ChatBannersView(false) { Spend = ChatSpendStopView.From(blocked, 2) });
            Has(banner, "class=\"app-banner warn\" role=\"alert\" data-banner=\"spend\" data-code=\"spend_blocked\"", "the over-cap banner");
            Has(banner, "<strong>Stopped by a spend cap.</strong>", "says so");
            Has(banner, "Nothing was sent or reserved.", "and that nothing was sent");
            Has(banner, "data-action=\"spend-review\" data-op=\"op-9\" data-entry=\"2\">Review…</button>", "Review");
            Has(banner, "href=\"/settings/spend#caps\">Settings › Spend</a>", "Settings");
            var askBanner = await host.Renderer.RenderAsync("~/Views/Chat/_ChatBanners.cshtml", new ChatBannersView(false) { Spend = ChatSpendStopView.From(asked, 2) });
            Has(askBanner, ">Review and continue…</button>", "an Ask cap's banner continues");
            Equal("", (await host.Renderer.RenderAsync("~/Views/Chat/_ChatBanners.cshtml", ChatBannersView.None)).Trim(), "no banner without a stop");

            // W3 fix 2: the durable engine starts queued messages only after an answer, so a stop that ended the run says what waits behind it.
            Lacks(html + banner, "data-spend-queued", "nothing queued: nothing said about a queue");
            Has(html, "data-code=\"spend_blocked\" data-queued=\"0\"", "the stop card's count is 0");
            Has(banner, "data-code=\"spend_blocked\" data-queued=\"0\"", "and the banner's");
            var waiting = await SpendRenderAsync(host, entries, ChatSpendFacts.Empty, queued: 1);
            Has(waiting, "data-spend-stop data-op=\"op-9\" data-code=\"spend_blocked\" data-queued=\"1\"", "the stop card counts the queued message");
            Has(waiting, "<div class=\"small mt-1\" data-spend-queued><i class=\"bi bi-hourglass-split me-1\" aria-hidden=\"true\"></i>" +
                "The run stopped here, so your queued message waits: it is sent when you continue or send another message.</div>", "and says it waits for the user");
            Equal(1, ChatSpendStopView.Newest(entries, 1)!.Queued, "the newest answer's stop holds it back");
            Equal("The run stopped here, so your 2 queued messages wait: they are sent when you continue or send another message.",
                ChatSpendStopView.Newest(entries, 2)!.QueuedText, "two of them");
            Check(ChatSpendStopView.Newest(entries, 0)!.QueuedText is null, "none");
            var queuedBanner = await host.Renderer.RenderAsync("~/Views/Chat/_ChatBanners.cshtml", new ChatBannersView(false) { Spend = ChatSpendStopView.Newest(entries, 2) });
            Has(queuedBanner, "data-banner=\"spend\" data-code=\"spend_blocked\" data-queued=\"2\"", "the banner counts them");
            Has(queuedBanner, "Nothing was sent or reserved.<span data-spend-queued> The run stopped here, so your 2 queued messages wait: they are sent when you continue " +
                "or send another message.</span>", "and says they wait");
            JsonObject[] later = [.. entries, UiUser(3, "a later message", UiTime + 2_000), UiAnswer(4, "Later.", input: 5, output: 5, time: UiTime + 3_000)];
            Check(ChatSpendStopView.Newest(later, 1) is null, "a stop with a message after it ended no run that holds the queue back");
            var laterHtml = await SpendRenderAsync(host, later, ChatSpendFacts.Empty, queued: 1);
            Has(laterHtml, "data-spend-stop data-op=\"op-9\"", "its card still shows");
            Lacks(laterHtml, "data-spend-queued", "without a word about the queue");
            Equal(2, ChatSpendStopView.Waiting([new InboxItem(7, "followUp", "a"), new InboxItem(8, "steer", "b"), new InboxItem(9, "write", "Context update")]),
                "follow-ups and steers wait; a context update is not a message");
            var markdown = host.Services.GetRequiredService<MarkdownRenderer>();
            var stopped = (AssistantItem)Transcript.Build(entries, null).Last();
            Check(AgentViewHub.Print(stopped, new ChatRenderContext { SessionId = "ui-chat", AgentId = 1, Markdown = markdown, Stop = ChatSpendStopView.Newest(entries, 1) }) !=
                AgentViewHub.Print(stopped, new ChatRenderContext { SessionId = "ui-chat", AgentId = 1, Markdown = markdown }),
                "the stream renders the stop card again when its queue changes (it is part of the fingerprint)");
        }),

        Case("chat.spend.session-panel", async () =>
        {
            await using var host = UiHost("chat-spend-info");
            ChatInfoView Info(ChatSpendSummary? spend) => new()
            {
                Binding = UiBinding(),
                AgentId = 1,
                AgentName = "Main",
                Provider = new CatalogProvider("faux", "Faux", null, [ProviderAuthKind.None], BillingKind.Metered),
                Model = new CatalogModel("faux", "echo", "Echo") { ContextWindow = 272_000 },
                AccountLabel = "Personal",
                ContextTokens = 41_280,
                Usage = new UsageSummary { Input = 62_140, Output = 4_880, CacheRead = 36_864, Cost = 0.1768 },
                Api = "faux-api",
                ApprovalMode = ToolApprovalMode.Ask,
                Tools = ["read"],
                AskTools = [],
                Instructions = "You are helpful.",
                Compaction = new SessionCompaction(true, 16_384, 32_768, 20_000),
                Cwd = "C:\\work",
                SessionPath = "C:\\data\\agent.sqlite",
                UserCount = 2,
                AssistantCount = 2,
                ToolCount = 0,
                Spend = spend,
            };
            var chatId = UiBinding().SessionId;
            var chatCap = new SpendCap(5, CapScope.Everything, null, CapPeriod.Run, 2_000_000, CapAction.Block);
            var daily = new SpendCap(8, CapScope.Everything, null, CapPeriod.Day, 5_000_000, CapAction.Ask);
            var summary = ChatSpendText.Summary(chatId, new SpendTotal(1_234_500, 380_000, 0, 4, 0, 0),
                [new SpendCapStatus(daily, 100_000, null, null, 80), new SpendCapStatus(chatCap, 1_700_000, null, null, 80)], BillingKind.Metered, "Personal");
            var html = await host.Renderer.RenderAsync("~/Views/Chat/_ChatInfo.cshtml", Info(summary));
            Has(html, "data-info=\"account\">Personal · metered</span>", "how the account is billed");
            Has(html, "data-info=\"chatSpend\">$1.23</span>", "the chat's spend");
            Has(html, "4 requests of this chat in the spend ledger; $1.23 counts toward your caps, including $0.38 uncertain until you resolve it.", "worked out");
            Has(html, "href=\"/history/spend?status=uncertain&q=" + chatId + "\" title=\"Resolve it in History › Spend\" data-info=\"chatUncertain\">$0.38 uncertain</a>",
                "the uncertain part links to History › Spend");
            Has(html, "data-info=\"chatCap\"><span class=\"ps-chip\" data-tone=\"warn\">$1.70 of $2.00</span>", "the per-chat cap near its limit");
            Has(html, "Per-chat cap: $1.70 of $2.00 used in this chat, uncertain spend included. Requests stop above $2.00.", "on hover");
            Has(html, "href=\"/history/spend?q=" + chatId + "\" data-info=\"spendHistory\">This chat in History › Spend</a>", "the chat in History › Spend");

            var none = await host.Renderer.RenderAsync("~/Views/Chat/_ChatInfo.cshtml", Info(ChatSpendText.Summary(chatId, SpendTotal.Zero,
                [new SpendCapStatus(daily, 100_000, null, null, 80)], BillingKind.Subscription, null)));
            Has(none, "data-info=\"chatSpend\">—</span>", "no request yet: no number");
            Has(none, "data-info=\"chatCap\">No chat cap</span>", "a day cap is not a chat cap");
            Has(none, "data-info=\"account\">subscription</span>", "a subscription");
            Lacks(none, "chatUncertain", "nothing uncertain");

            var list = ChatSpendText.Summary(chatId, new SpendTotal(0, 0, 180_000, 2, 0, 0), [], BillingKind.Subscription, "Personal");
            Equal(("≈ $0.1800 list", "Personal · subscription"), (list.Text, list.Billing), "subscription use at list price");
            Check(list.Title.Contains("never counted", StringComparison.Ordinal), "never counted");
            Equal("free", ChatSpendText.Summary(chatId, new SpendTotal(0, 0, 0, 3, 0, 3), [], BillingKind.Free, null).Text, "free requests");

            var plain = await host.Renderer.RenderAsync("~/Views/Chat/_ChatInfo.cshtml", Info(null));
            Lacks(plain, "data-info=\"chatSpend\"", "without the spend ledger, no chat spend");
        }),

        Case("chat.spend.answer-and-request-size", () =>
        {
            Check(ChatSpendAnswer.Read(null) is null, "no spend field: not checked here");
            Check(ReferenceEquals(ChatSpendAnswer.Check, ChatSpendAnswer.Read(new JsonObject { ["check"] = true })), "a check");
            Equal((SpendChoice?)SpendChoice.ProceedOnce, ChatSpendAnswer.Read(new JsonObject { ["choice"] = "proceed-once" })!.Choice, "proceed once");
            // As the controller reads a command's body (parsed JSON).
            var capped = ChatSpendAnswer.Read(JsonNode.Parse("""{"choice": "cap-output", "maxOutputTokens": 100}"""))!;
            Equal(((SpendChoice?)SpendChoice.CapOutput, (int?)100), (capped.Choice, capped.MaxOutputTokens), "cap the output");
            Throws<ChatException>(() => ChatSpendAnswer.Read(new JsonObject { ["choice"] = "cap-output" }), "capping needs a number");
            Throws<ChatException>(() => ChatSpendAnswer.Read(JsonNode.Parse("""{"choice": "cap-output", "maxOutputTokens": 0}""")), "of 1 or more");
            Throws<ChatException>(() => ChatSpendAnswer.Read(new JsonObject { ["choice"] = "switch-model" }), "never a model switch");
            Throws<ChatException>(() => ChatSpendAnswer.Read(JsonValue.Create("yes")), "an object");

            JsonObject[] entries =
            [
                UiUser(1, "héllo", UiTime),
                UiAnswer(2, "Answer.", input: 10, output: 5, time: UiTime + 1_000),
                UiAnswer(3, "", stop: "error", error: SpendStopError(CapAction.Block), time: UiTime + 2_000),
                UiAnswer(4, "Dropped", stop: "aborted", time: UiTime + 3_000),
            ];
            long Bytes(JsonObject entry) => entry["model"]!.AsArray().Sum(message => (long)Encoding.UTF8.GetByteCount(message!.ToJsonString()));
            Equal(Bytes(entries[0]) + Bytes(entries[1]), ChatSpend.ContextBytes(entries), "the context the next request sends: stopped and dropped answers left out");
            Equal(0L, ChatSpend.DraftBytes(""), "no draft");
            Equal(6L + ChatSpend.MessageOverhead, ChatSpend.DraftBytes("héllo"), "a draft by its UTF-8 size");
            var fresh = ChatSpend.NewChatCall("faux", "echo", "acct", "hi", "r1");
            Equal(("chat-new:r1", 2L + ChatSpend.MessageOverhead, SpendSource.Chat), (fresh.Run, fresh.InputBytes, fresh.Source), "a new chat's first request");
        }),

        Case("chat.spend.app.blocked-send-posts-nothing-and-proceed-once-posts-once", async () =>
        {
            var root = Temp("chat-spend-app");
            try
            {
                var (app, path) = await SpendChatStartAsync(root);
                await using var _ = app;
                var created = await app.SendAsync(HttpMethod.Post, "/api/chats", new JsonObject { ["requestId"] = "r1", ["text"] = "hello", ["spend"] = new JsonObject { ["check"] = true } });
                Equal((200, true), (created.Status, created.Body?["posted"]?.GetValue<bool>()), "no cap: the check lets it through: " + created.Text);
                var chatId = created.Body!["id"]!.GetValue<string>();
                await GuardIdleAsync(app, chatId, "Echo: hello");

                var estimate = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/spend", new JsonObject { ["text"] = "a private draft" });
                Equal(200, estimate.Status, "the estimate: " + estimate.Text);
                Equal(("allow", true), (estimate.Body!["verdict"]!.GetValue<string>(), estimate.Body["allowed"]!.GetValue<bool>()), "allowed");
                Check(estimate.Body["chip"]!.GetValue<string>().StartsWith("≈ $", StringComparison.Ordinal), "a metered estimate: " + estimate.Text);
                Has(estimate.Body["html"]!.GetValue<string>(), "data-spend-chip", "rendered");
                Lacks(estimate.Text, "a private draft", "the draft is only counted");
                Check(estimate.Response.Headers.CacheControl?.NoStore == true, "never cached: " + estimate.Response.Headers.CacheControl);
                var fresh = await app.SendAsync(HttpMethod.Post, "/api/chats/spend", new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["text"] = "hi" });
                Equal((200, "allow"), (fresh.Status, fresh.Body?["verdict"]?.GetValue<string>()), "a new chat's estimate: " + fresh.Text);
                Equal(404, (await app.SendAsync(HttpMethod.Post, "/api/chats/no-such-chat/spend", new JsonObject { ["text"] = "x" })).Status, "an unknown chat");
                Equal(400, (await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages",
                    new JsonObject { ["requestId"] = "bad", ["text"] = "x", ["spend"] = new JsonObject { ["choice"] = "switch-model" } })).Status, "an unknown choice");

                // A per-request Block cap the next request (8,192 output tokens at $15/M) does not fit, though a 100-token one does.
                var limit = SpendChatInputMicros(path) + 30_000;
                var capId = SpendChatCap(path, limit, "block");
                var over = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/spend", new JsonObject { ["text"] = "draft" });
                Equal(("block", "spend_blocked", "sc-request"), (over.Body!["verdict"]!.GetValue<string>(), over.Body["code"]!.GetValue<string>(), over.Body["pane"]!.GetValue<string>()),
                    "the estimate turns into the over-cap chip: " + over.Text);
                Has(over.Body["html"]!.GetValue<string>(), ">Over request cap</button>", "named after the cause");

                var rowsBefore = SpendChatRows(path);
                var stopsBefore = (await app.Get("/api/spend/stops?run=" + chatId)).Body?["stops"]?.AsArray().Count;
                var blocked = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages",
                    new JsonObject { ["requestId"] = "m1", ["text"] = "blocked please", ["spend"] = new JsonObject { ["check"] = true } });
                Equal((200, false, "spend_blocked"), (blocked.Status, blocked.Body?["posted"]?.GetValue<bool>(), blocked.Body?["code"]?.GetValue<string>()), "a blocked send: " + blocked.Text);
                var spend = blocked.Body!["spend"]!;
                Equal(("block", false, "sc-request"), (spend["verdict"]!.GetValue<string>(), spend["canProceed"]!.GetValue<bool>(), spend["pane"]!.GetValue<string>()), "no proceed");
                Has(spend["modal"]!.GetValue<string>(), "id=\"modal-spend-confirm\"", "with the confirm modal");
                Has(spend["modal"]!.GetValue<string>(), "data-pane=\"sc-request\"", "at the cause's pane");
                Check(spend["capOutputTokens"]?.GetValue<int>() > 0, "which offers an output cap that fits: " + spend.ToJsonString());
                var again = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages",
                    new JsonObject { ["requestId"] = "m1", ["text"] = "blocked please", ["spend"] = new JsonObject { ["choice"] = "proceed-once" } });
                Equal((200, false, "spend_blocked"), (again.Status, again.Body?["posted"]?.GetValue<bool>(), again.Body?["code"]?.GetValue<string>()), "proceed once never passes a Block cap: " + again.Text);
                Equal(rowsBefore, SpendChatRows(path), "nothing was reserved");
                Equal(stopsBefore, (await app.Get("/api/spend/stops?run=" + chatId)).Body?["stops"]?.AsArray().Count, "nothing was stopped: nothing was sent");
                Equal(0, (await app.Get("/api/spend/approvals?run=" + chatId)).Body?["approvals"]?.AsArray().Count, "and no approval was stored");
                Lacks((await app.Get($"/chat/{chatId}/export.md")).Text, "blocked please", "nothing was posted");

                var capped = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages",
                    new JsonObject { ["requestId"] = "m1", ["text"] = "blocked please", ["spend"] = new JsonObject { ["choice"] = "cap-output", ["maxOutputTokens"] = 100 } });
                Equal(200, capped.Status, "capping the output fits: " + capped.Text);
                await GuardIdleAsync(app, chatId, "Echo: blocked please");
                Equal("asked · output capped at 100 tokens", string.Join(" | ", SpendChatNotes(path, chatId)), "sent once at the chosen output cap");

                SpendChatCapAction(path, capId, "ask");
                var ask = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages",
                    new JsonObject { ["requestId"] = "m2", ["text"] = "ask please", ["spend"] = new JsonObject { ["check"] = true } });
                Equal((200, false, "spend_ask"), (ask.Status, ask.Body?["posted"]?.GetValue<bool>(), ask.Body?["code"]?.GetValue<string>()), "an Ask cap asks first: " + ask.Text);
                Check(ask.Body!["spend"]!["canProceed"]!.GetValue<bool>(), "and may proceed once");
                Lacks((await app.Get($"/chat/{chatId}/export.md")).Text, "ask please", "nothing was posted before the choice");
                var once = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages",
                    new JsonObject { ["requestId"] = "m2", ["text"] = "ask please", ["spend"] = new JsonObject { ["choice"] = "proceed-once" } });
                Equal((200, true), (once.Status, once.Body?["posted"]?.GetValue<bool>()), "proceed once: " + once.Text);
                var retried = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages",
                    new JsonObject { ["requestId"] = "m2", ["text"] = "ask please", ["spend"] = new JsonObject { ["choice"] = "proceed-once" } });
                Equal(200, retried.Status, "a retried POST: " + retried.Text);
                Equal(once.Body!["submissionId"]!.GetValue<long>(), retried.Body!["submissionId"]!.GetValue<long>(), "is the same submission");
                await GuardIdleAsync(app, chatId, "Echo: ask please");
                Equal(1, Regex.Matches((await app.Get($"/chat/{chatId}/export.md")).Text, "Echo: ask please").Count, "posted and answered once");
                Equal("asked · output capped at 100 tokens | asked · sent once", string.Join(" | ", SpendChatNotes(path, chatId)), "one request sent once after the user's OK");
                Equal(0, (await app.Get("/api/spend/approvals?run=" + chatId)).Body?["approvals"]?.AsArray().Count, "the approval was used");

                // A new chat: refused before anything exists; proceed once creates it and its first request uses the approval.
                var chats = (await app.Get("/api/chats")).Body!["items"]!.AsArray().Count;
                SpendChatCapAction(path, capId, "block");
                var refused = await app.SendAsync(HttpMethod.Post, "/api/chats",
                    new JsonObject { ["requestId"] = "n1", ["text"] = "new blocked", ["spend"] = new JsonObject { ["check"] = true } });
                Equal((200, false, "spend_blocked"), (refused.Status, refused.Body?["posted"]?.GetValue<bool>(), refused.Body?["code"]?.GetValue<string>()), "a refused new chat: " + refused.Text);
                Has(refused.Body!["spend"]!["modal"]!.GetValue<string>(), "id=\"modal-spend-confirm\"", "with the modal");
                Equal(chats, (await app.Get("/api/chats")).Body!["items"]!.AsArray().Count, "creates no chat");
                SpendChatCapAction(path, capId, "ask");
                var asked = await app.SendAsync(HttpMethod.Post, "/api/chats",
                    new JsonObject { ["requestId"] = "n2", ["text"] = "new asked", ["spend"] = new JsonObject { ["check"] = true } });
                Equal((200, false, "spend_ask"), (asked.Status, asked.Body?["posted"]?.GetValue<bool>(), asked.Body?["code"]?.GetValue<string>()), "asked first: " + asked.Text);
                Equal(chats, (await app.Get("/api/chats")).Body!["items"]!.AsArray().Count, "still no chat");
                var made = await app.SendAsync(HttpMethod.Post, "/api/chats",
                    new JsonObject { ["requestId"] = "n2", ["text"] = "new asked", ["spend"] = new JsonObject { ["choice"] = "proceed-once" } });
                Equal(200, made.Status, "proceed once creates it: " + made.Text);
                var madeId = made.Body!["id"]!.GetValue<string>();
                await GuardIdleAsync(app, madeId, "Echo: new asked");
                Equal("asked · sent once", string.Join(" | ", SpendChatNotes(path, madeId)), "its first request was sent once");
                Equal(chats + 1, (await app.Get("/api/chats")).Body!["items"]!.AsArray().Count, "one chat");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("chat.spend.app.in-run-stop-reviews-and-continues", async () =>
        {
            var root = Temp("chat-spend-stop-app");
            try
            {
                var (app, path) = await SpendChatStartAsync(root);
                await using var _ = app;
                var created = await app.SendAsync(HttpMethod.Post, "/api/chats", new JsonObject { ["requestId"] = "r1", ["text"] = "hello" });
                Equal(200, created.Status, "created: " + created.Text);
                var chatId = created.Body!["id"]!.GetValue<string>();
                await GuardIdleAsync(app, chatId, "Echo: hello");
                var page = await app.Get("/chat/" + chatId);
                Equal(200, page.Status, "the chat page");
                Has(page.Text, "data-spend-estimate-host", "the estimate by Send");
                Has(page.Text, "data-spend-chip", "a metered estimate");
                Has(page.Text, "id=\"modal-spend-confirm\"", "the confirm modal is hosted");
                Has(page.Text, "data-spend-mark=\"metered\"", "the answer's cost from the ledger");
                Has(page.Text, "data-info=\"chatSpend\"", "the chat's spend in the session panel");
                Check(!Regex.IsMatch(page.Text, @"<script(?![^>]*\bsrc=)[^>]*>"), "no inline script");
                var fresh = await app.Get("/chat/new");
                Has(fresh.Text, "data-spend-estimate-host", "the new chat's estimate host");
                Has(fresh.Text, "id=\"modal-spend-confirm\"", "and modal");

                SpendChatCap(path, SpendChatInputMicros(path) + 30_000, "block");
                var sent = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages", new JsonObject { ["requestId"] = "m1", ["text"] = "stopped please" });
                Equal(200, sent.Status, "a send that doesn't check first: " + sent.Text);
                JsonNode? stops = null;
                await WaitUntilAsync(async () =>
                {
                    stops = (await app.Get("/api/spend/stops?run=" + chatId)).Body;
                    var chat = await app.Get("/api/chats/" + chatId);
                    return stops?["stops"]?.AsArray().Count > 0 && chat.Body?["running"]?.GetValue<bool>() == false;
                }, "the guard stops the call inside the run", TimeSpan.FromSeconds(30));
                var op = stops!["stops"]![0]!["op"]!.GetValue<string>();
                string stoppedPage = "";
                await WaitUntilAsync(async () =>
                {
                    stoppedPage = (await app.Get("/chat/" + chatId)).Text;
                    return stoppedPage.Contains("data-banner=\"spend\"", StringComparison.Ordinal);
                }, "the over-cap banner", TimeSpan.FromSeconds(10));
                Has(stoppedPage, "data-failure=\"spend\"", "the stopped call");
                Has(stoppedPage, $"data-spend-stop data-op=\"{op}\" data-code=\"spend_blocked\"", "names its op");
                Has(stoppedPage, $"data-action=\"spend-review\" data-op=\"{op}\"", "Review…");
                Has(stoppedPage, "data-spend-over", "the composer shows the over-cap chip");

                var review = await app.Get($"/api/chats/{chatId}/spend/stops/{Uri.EscapeDataString(op)}");
                Equal(200, review.Status, "the stop's review: " + review.Text);
                Check(review.Response.Headers.CacheControl?.NoStore == true, "never cached");
                Equal(("block", false, "spend_blocked"), (review.Body!["verdict"]!.GetValue<string>(), review.Body["allowed"]!.GetValue<bool>(), review.Body["code"]!.GetValue<string>()),
                    "from the stop's own decision");
                Has(review.Body["modal"]!.GetValue<string>(), "id=\"modal-spend-confirm\"", "with the confirm modal");
                Equal(404, (await app.Get($"/api/chats/{chatId}/spend/stops/no-such-op")).Status, "an unknown stop");
                Equal(404, (await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/spend/continue",
                    new JsonObject { ["requestId"] = "c0", ["op"] = "no-such-op", ["spend"] = new JsonObject { ["choice"] = "proceed-once" } })).Status, "nor continued");

                var check = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/spend/continue", new JsonObject { ["requestId"] = "c1", ["op"] = op });
                Equal((200, false, "spend_blocked"), (check.Status, check.Body?["posted"]?.GetValue<bool>(), check.Body?["code"]?.GetValue<string>()), "continuing as it is is still over: " + check.Text);
                Lacks((await app.Get($"/chat/{chatId}/export.md")).Text, ChatSpend.ContinueText, "nothing was posted");
                var go = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/spend/continue",
                    new JsonObject { ["requestId"] = "c1", ["op"] = op, ["spend"] = new JsonObject { ["choice"] = "cap-output", ["maxOutputTokens"] = 100 } });
                Equal(200, go.Status, "continue after the user's choice: " + go.Text);
                await GuardIdleAsync(app, chatId, "Echo: " + ChatSpend.ContinueText);
                Equal("asked · output capped at 100 tokens", string.Join(" | ", SpendChatNotes(path, chatId)), "the run went on at the chosen output cap");
                Equal(200, (await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/spend/continue",
                    new JsonObject { ["requestId"] = "c1", ["op"] = op, ["spend"] = new JsonObject { ["choice"] = "cap-output", ["maxOutputTokens"] = 100 } })).Status, "a retry");
                Equal(1, Regex.Matches((await app.Get($"/chat/{chatId}/export.md")).Text, Regex.Escape("Echo: " + ChatSpend.ContinueText)).Count, "continues once");
                await WaitUntilAsync(async () => !(await app.Get("/chat/" + chatId)).Text.Contains("data-banner=\"spend\"", StringComparison.Ordinal),
                    "the banner goes once the run went on", TimeSpan.FromSeconds(10));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("chat.spend.approval-answers-only-its-message", async () =>
        {
            await using var world = GuardWorld.Create("chat-spend-submission");
            await world.CapAsync(CapPeriod.Request, 50_000, CapAction.Ask);
            var script = new GuardScript((call, token) => GuardAnswer(call.Model, "answered", GuardUsage(10, 5)));
            var models = world.Over(script);
            var model = GuardModel("metered", "m");
            var given = world.Clock.Now.ToUnixTimeMilliseconds();
            async Task<bool> SendAsync(string op, string text, long placed, double? maxTokens = null) =>
                (await GuardCollect(models.StreamSimple(model, SpendTurn(text, placed), GuardOptions(op, "chat-1", maxTokens: maxTokens), CancellationToken.None)))[^1]
                    is PiSharp.Contracts.StreamDone;

            var key = GuardedDurableModels.SubmissionKey("queued please");
            Equal((key, (long?)(given + 5)), GuardedDurableModels.Submission(SpendTurn("queued please", given + 5)), "a request answers its newest user message");
            Check(key.Length == 32 && !key.Contains("queued", StringComparison.Ordinal), "a key, never the text");
            Equal(((string?)null, (long?)null), GuardedDurableModels.Submission([]), "no user message, no submission");
            var stored = await world.Guard.ApproveStoredAsync(new SpendApproval("chat-1", SpendChoice.ProceedOnce) { Submission = key });
            Equal(key, stored.Approval.Submission, "the approval belongs to the message");

            Check(!await SendAsync("op-running", "an earlier message", given - 5_000), "the running turn's next call does not use it");
            var compaction = await models.CompleteSimpleAsync(model, SpendTurn("queued please", given + 1_000), GuardOptions("op-compact", "chat-1"), CancellationToken.None);
            Equal(PiSharp.Contracts.StopReason.Error, compaction.StopReason, "nor a compaction summary, even one whose newest user message is it");
            Check(!await SendAsync("op-early", "queued please", given - 1), "nor the same text placed before the approval was given");
            Check(!await SendAsync("op-other", "another message", given + 1_000), "nor another message");
            Equal(SpendVerdict.Ask, (await world.Guard.PreflightAsync(GuardCall("", "chat-1"))).Verdict, "nor a preflight that answers no message");
            Equal(0, script.Calls.Count, "nothing was sent");
            Equal(1, (await world.Approvals.WaitingAsync("chat-1")).Count, "it still waits");

            Check(await SendAsync("op-queued", "queued please", given + 2_000), "the call that sends the message uses it");
            Equal("asked · sent once", (await world.Ledger.GetAsync("op-queued"))!.Note, "sent once");
            Equal(key, (await world.Approvals.UsedByAsync("op-queued"))?.Approval.Submission, "used by that call");
            Check(!await SendAsync("op-queued-2", "queued please", given + 2_000), "its turn's next call asks again");
            Equal(1, script.Calls.Count, "one send in all");

            // A run-wide approval answers a preflight, but never the chat's check of a send the user has not answered.
            await world.Guard.ApproveAsync(new SpendApproval("chat-1", SpendChoice.ProceedOnce));
            Equal(SpendVerdict.Allow, (await world.Guard.PreflightAsync(GuardCall("", "chat-1"))).Verdict, "a run-wide approval answers a preflight");
            Equal(SpendVerdict.Ask, (await world.Guard.PreflightAsync(GuardCall("", "chat-1"), withApprovals: false)).Verdict, "not the chat's check");

            // A message's own approval comes before a run-wide one.
            var both = await world.Guard.ApproveStoredAsync(new SpendApproval("chat-1", SpendChoice.ProceedOnce) { Submission = GuardedDurableModels.SubmissionKey("mine please") });
            Check(await SendAsync("op-mine", "mine please", given + 3_000), "sent");
            Equal(both.Id, (await world.Approvals.UsedByAsync("op-mine"))?.Id, "with its own approval");
            Check((await world.Approvals.WaitingAsync("chat-1")) is [{ Approval.Submission: null }], "the run-wide one still waits");

            // A message whose call fits as it is retires its approval: it answers nothing else.
            var small = await world.Guard.ApproveStoredAsync(new SpendApproval("chat-1", SpendChoice.ProceedOnce) { Submission = GuardedDurableModels.SubmissionKey("small please") });
            Check(await SendAsync("op-small", "small please", given + 4_000, maxTokens: 100), "a small call fits the cap");
            Check((await world.Ledger.GetAsync("op-small"))!.Note is null, "without the approval");
            Equal(SpendApprovalStore.Unneeded, SpendApprovalUsedBy(world.Path, small.Id), "which is retired, unneeded");
            Check((await world.Approvals.WaitingAsync("chat-1")) is [{ Approval.Submission: null }], "only the run-wide one waits");
        }),

        Case("chat.spend.queued-send-keeps-its-approval-from-the-running-turn", async () =>
        {
            await using var world = GuardWorld.Create("chat-spend-queued-world");
            await using var chat = await SpendChatHarnessAsync("chat-spend-queued", world);
            var chatId = (await chat.CreateAsync("hello", "r1")).SessionId;
            await chat.Chats.SendAsync(chatId, new ChatSendRequest("m1", "!echo hi"));
            var tool = await chat.PendingAsync(chatId, "bash");
            await world.CapAsync(CapPeriod.Request, 50_000, CapAction.Ask);

            var asked = await ThrowsAsync<ChatSpendException>(() => chat.Chats.SendAsync(chatId, new ChatSendRequest("m2", "queued please") { Spend = ChatSpendAnswer.Check }),
                "a send while the chat runs is checked");
            Equal(SpendErrors.Ask, asked.Code, "and asked about");
            var queued = await chat.Chats.SendAsync(chatId, new ChatSendRequest("m2", "queued please") { Spend = new ChatSpendAnswer(SpendChoice.ProceedOnce) });
            Equal("Queued", queued.Status, "proceed once queues it behind the running turn");
            Check((await world.Approvals.WaitingAsync(chatId)) is [{ Approval.Submission: var submission }] && submission == GuardedDurableModels.SubmissionKey("queued please"),
                "one approval, for that message");

            Equal(ApprovalOutcomeStatus.Decided, (await chat.Chats.DecideApprovalAsync(chatId, tool.Request.TaskId, new ApprovalDecision(ApprovalChoice.AllowOnce))).Status,
                "the running turn goes on");
            await WaitUntilAsync(async () => (await world.Approvals.StopsAsync(chatId)).Count > 0, "the running turn's next call is checked", TimeSpan.FromSeconds(30));
            await chat.IdleAsync(chatId);
            var stops = await world.Approvals.StopsAsync(chatId);
            Check(stops is [{ Code: SpendErrors.Ask }], "the running turn's next call was stopped: the user approved the queued message, not it");
            Lacks(await chat.TextAsync(chatId), "Tool finished", "so it sent nothing");
            Check(world.Rows().All(row => SpendChatNote(world.Path, row.Op) is null), "no call went out on the user's OK");
            var still = (await world.Approvals.WaitingAsync(chatId)).Single();
            Equal(GuardedDurableModels.SubmissionKey("queued please"), still.Approval.Submission, "the approval still waits for the queued message");

            // The run ended on the stop, so the follow-up stays queued (the engine starts queued items only after an answer). The stop card
            // and the spend banner say so (W3 fix 2), on the page and in the stream.
            var page = await chat.Chats.OpenAsync(chatId);
            Equal(((int?)1, (int?)1), (page.Banners.Spend?.Queued, page.Context.Stop?.Queued), "the page's spend banner and stop card count the queued message");
            var renderer = chat.Services.GetRequiredService<FragmentRenderer>();
            var stopCard = await renderer.RenderAsync("~/Views/Chat/_ChatItem.cshtml", new ChatItemView(page.Items.OfType<AssistantItem>().Last(), page.Context));
            Has(stopCard, "data-code=\"spend_ask\" data-queued=\"1\"", "the stop card");
            Has(stopCard, "so your queued message waits: it is sent when you continue or send another message.", "says the message waits for the user");
            Has(await renderer.RenderAsync("~/Views/Chat/_ChatBanners.cshtml", page.Banners), "<span data-spend-queued> The run stopped here, so your queued message waits",
                "and so does the banner");
            var sink = new FrameSink();
            using (chat.Hub.Subscribe(chatId, chat.Bindings.Get(chatId)!.AgentId, sink.Add))
            {
                var whole = await sink.WaitAsync(frame => IsView(frame, true), "the stream's whole view");
                Has(whole["panels"]?["banners"]?.GetValue<string>() ?? "", "data-queued=\"1\"", "the stream's banner");
                Has(Html(whole), "data-spend-stop", "the stream's stop card");
                Has(Html(whole), "data-queued=\"1\"", "counts it too");

                // Withdrawn, it takes its approval with it: a later copy of the same text is not let through on it.
                Check(await chat.Chats.WithdrawAsync(chatId, null, queued.SubmissionId), "the queued message is withdrawn");
                await sink.WaitAsync(frame => IsView(frame, false) && frame["panels"]?["banners"] is JsonValue banners &&
                    banners.GetValue<string>().Contains("data-banner=\"spend\"", StringComparison.Ordinal) &&
                    !banners.GetValue<string>().Contains("data-spend-queued", StringComparison.Ordinal), "the banner says nothing waits once the queue is empty");
                var redrawn = await sink.WaitAsync(frame => IsView(frame, false) && Html(frame).Contains("data-spend-stop", StringComparison.Ordinal) &&
                    Html(frame).Contains("data-queued=\"0\"", StringComparison.Ordinal), "and the stop card is rendered again with nothing queued");
                Lacks(Html(redrawn), "data-spend-queued", "saying nothing about a queue");
            }
            Equal(SpendApprovalStore.Withdrawn, SpendApprovalUsedBy(world.Path, still.Id), "its approval is withdrawn");
            Equal(0, (await world.Approvals.WaitingAsync(chatId)).Count, "nothing waits");
            var again = await ThrowsAsync<ChatSpendException>(() => chat.Chats.SendAsync(chatId, new ChatSendRequest("m3", "queued please") { Spend = ChatSpendAnswer.Check }),
                "the same text sent again");
            Equal(SpendErrors.Ask, again.Code, "is asked about again");
        }),

        Case("chat.spend.unused-approval-never-answers-another-send", async () =>
        {
            await using var world = GuardWorld.Create("chat-spend-unused-world");
            await using var chat = await SpendChatHarnessAsync("chat-spend-unused", world);
            var chatId = (await chat.CreateAsync("hello", "r1")).SessionId;
            await chat.Chats.SendAsync(chatId, new ChatSendRequest("m1", "!echo hi"));
            var tool = await chat.PendingAsync(chatId, "bash");
            var cap = await world.CapAsync(CapPeriod.Request, 50_000, CapAction.Ask);
            await chat.Chats.SendAsync(chatId, new ChatSendRequest("m2", "small please") { Spend = new ChatSpendAnswer(SpendChoice.ProceedOnce) });
            var given = (await world.Approvals.WaitingAsync(chatId)).Single();

            // The cap goes before the message is sent: its call fits as it is, so the user's OK was not needed.
            await world.Ledger.SaveCapAsync(cap with { Enabled = false });
            await chat.Chats.DecideApprovalAsync(chatId, tool.Request.TaskId, new ApprovalDecision(ApprovalChoice.AllowOnce));
            await WaitUntilAsync(async () => (await chat.TextAsync(chatId)).Contains("Echo: small please", StringComparison.Ordinal), "the message is answered",
                TimeSpan.FromSeconds(30));
            await chat.IdleAsync(chatId);
            Equal(SpendApprovalStore.Unneeded, SpendApprovalUsedBy(world.Path, given.Id), "its approval is retired, unneeded");
            Equal(0, (await world.Approvals.WaitingAsync(chatId)).Count, "nothing waits");
            Check(world.Rows().All(row => SpendChatNote(world.Path, row.Op) is null), "no call went out on an approval");

            // Over the cap again, with a run-wide approval left waiting: a send the user has not answered is still asked about.
            await world.Ledger.SaveCapAsync(cap with { Enabled = true });
            await world.Guard.ApproveAsync(new SpendApproval(chatId, SpendChoice.ProceedOnce));
            var later = await ThrowsAsync<ChatSpendException>(() => chat.Chats.SendAsync(chatId, new ChatSendRequest("m3", "later please") { Spend = ChatSpendAnswer.Check }),
                "a check-only send");
            Equal((SpendErrors.Ask, true), (later.Code, later.Result.CanProceed), "is asked about, not let through by an approval it was never given");
            Equal("ask", (await chat.Chats.EstimateAsync(chatId, null, "later please"))?.Verdict, "the composer says so too");
            Lacks(await chat.TextAsync(chatId), "later please", "nothing was posted");
        }),

        Case("chat.spend.failed-send-withdraws-its-approval", async () =>
        {
            await using var world = GuardWorld.Create("chat-spend-failed-world");
            await using var chat = await SpendChatHarnessAsync("chat-spend-failed", world);
            var binding = await chat.CreateAsync("hello", "r1");
            var chatId = binding.SessionId;
            await world.CapAsync(CapPeriod.Request, 50_000, CapAction.Ask);
            var once = new ChatSendRequest("m1", "doomed please") { Spend = new ChatSpendAnswer(SpendChoice.ProceedOnce) };

            // The chat's model is gone from the device: the send is admitted and approved, then fails before its message is posted.
            chat.Bindings.Update(chatId, current => current with { Model = "gone" });
            await ThrowsAsync<HostInvalidException>(() => chat.Chats.SendAsync(chatId, once), "the send fails after its approval");
            Equal(0, (await world.Approvals.WaitingAsync(chatId)).Count, "no approval waits for a message that was not posted");
            Equal(SpendApprovalStore.Withdrawn, ReadDb(world.Path, "SELECT used_by FROM spend_approvals", reader => reader.GetString(0)).Single(), "it was withdrawn");
            Check(!chat.Chats.Spending!.WasAdmitted(chatId + "/m1"), "the send is no longer admitted");
            Lacks(await chat.TextAsync(chatId), "doomed please", "nothing was posted");

            // Its retry is checked again, and posts once with an approval of its own.
            chat.Bindings.Update(chatId, current => current with { Model = binding.Model });
            Equal(SpendErrors.Ask, (await ThrowsAsync<ChatSpendException>(() => chat.Chats.SendAsync(chatId, once with { Spend = ChatSpendAnswer.Check }),
                "a check-only retry")).Code, "is asked about again");
            await chat.Chats.SendAsync(chatId, once);
            await WaitUntilAsync(async () => (await chat.TextAsync(chatId)).Contains("Echo: doomed please", StringComparison.Ordinal), "the retry is answered",
                TimeSpan.FromSeconds(30));
            await chat.IdleAsync(chatId);
            Equal(1, Regex.Matches(await chat.TextAsync(chatId), "Echo: doomed please").Count, "posted once");
            Check(world.Rows().Count(row => SpendChatNote(world.Path, row.Op) == "asked · sent once") == 1, "sent once on the new approval");
            Equal(0, (await world.Approvals.WaitingAsync(chatId)).Count, "nothing waits");
        }),

        Case("chat.spend.parallel-copies-of-a-send-store-one-approval", async () =>
        {
            await using var world = GuardWorld.Create("chat-spend-copies-world");
            await using var chat = await SpendChatHarnessAsync("chat-spend-copies", world);
            var chatId = (await chat.CreateAsync("hello", "r1")).SessionId;
            await world.CapAsync(CapPeriod.Request, 50_000, CapAction.Ask);
            var sends = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
                chat.Chats.SendAsync(chatId, new ChatSendRequest("m1", "copies please") { Spend = new ChatSpendAnswer(SpendChoice.ProceedOnce) }))));
            Equal(1, sends.Select(sent => sent.SubmissionId).Distinct().Count(), "one submission");
            await WaitUntilAsync(async () => (await chat.TextAsync(chatId)).Contains("Echo: copies please", StringComparison.Ordinal), "answered", TimeSpan.FromSeconds(30));
            await chat.IdleAsync(chatId);
            Equal(1L, ReadDb(world.Path, "SELECT COUNT(*) FROM spend_approvals", reader => reader.GetInt64(0)).Single(), "six copies stored one approval");
            Equal(0, (await world.Approvals.WaitingAsync(chatId)).Count, "and none is left unused");
            Equal(1, Regex.Matches(await chat.TextAsync(chatId), "Echo: copies please").Count, "posted once");
        }),

        Case("chat.spend.app.parallel-proceed-once-stores-one-approval", async () =>
        {
            var root = Temp("chat-spend-parallel-app");
            try
            {
                var (app, path) = await SpendChatStartAsync(root);
                await using var _ = app;
                var created = await app.SendAsync(HttpMethod.Post, "/api/chats", new JsonObject { ["requestId"] = "r1", ["text"] = "hello" });
                Equal(200, created.Status, "created: " + created.Text);
                var chatId = created.Body!["id"]!.GetValue<string>();
                await GuardIdleAsync(app, chatId, "Echo: hello");
                SpendChatCap(path, SpendChatInputMicros(path) + 30_000, "ask");
                var asked = await app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages",
                    new JsonObject { ["requestId"] = "m1", ["text"] = "twice please", ["spend"] = new JsonObject { ["check"] = true } });
                Equal((200, false, "spend_ask"), (asked.Status, asked.Body?["posted"]?.GetValue<bool>(), asked.Body?["code"]?.GetValue<string>()), "asked first: " + asked.Text);

                var posts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => app.SendAsync(HttpMethod.Post, $"/api/chats/{chatId}/messages",
                    new JsonObject { ["requestId"] = "m1", ["text"] = "twice please", ["spend"] = new JsonObject { ["choice"] = "proceed-once" } })));
                Check(posts.All(post => post.Status == 200), "both copies answer 200: " + string.Join(" | ", posts.Select(post => post.Text)));
                Equal(posts[0].Body!["submissionId"]!.GetValue<long>(), posts[1].Body!["submissionId"]!.GetValue<long>(), "one submission");
                await GuardIdleAsync(app, chatId, "Echo: twice please");
                Equal(1L, ReadDb(path, $"SELECT COUNT(*) FROM spend_approvals WHERE run = '{chatId.Replace("'", "''", StringComparison.Ordinal)}'", reader => reader.GetInt64(0)).Single(),
                    "two parallel copies stored one approval");
                Equal(0, (await app.Get("/api/spend/approvals?run=" + chatId)).Body?["approvals"]?.AsArray().Count, "and none is left unused");
                Equal("asked · sent once", string.Join(" | ", SpendChatNotes(path, chatId)), "one request sent once");
                Equal(1, Regex.Matches((await app.Get($"/chat/{chatId}/export.md")).Text, "Echo: twice please").Count, "posted and answered once");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("chat.spend.bookkeeping-after-a-post-never-fails-the-send", async () =>
        {
            // A host whose webagent.db lacks spend_chat_sends (migration 1400). The app always has it (ChatModule registers it); before
            // W3 fix 2 a send asked it after its message was posted, and the posted send failed.
            await using var world = GuardWorld.Create("chat-spend-no-sends-world");
            await using var chat = await ChatHarness.StartAsync("chat-spend-no-sends", world.Over(FauxModelCatalog.CreateModels()), configure: services =>
            {
                services.AddSingleton(world.Guard);
                services.RemoveAll<WebAgentDb>();
                services.AddSingleton(provider => new WebAgentDb(provider.GetRequiredService<WebAgentOptions>().DatabasePath,
                    [CoreMigrations.AppInfo, ChatBindings.Migration, SettingsStore.Migration]));
            });
            var spend = chat.Chats.Spending!;
            Throws<SqliteException>(() => spend.Sends.Count(), "the chat sends table is missing");
            var chatId = (await chat.CreateAsync("hello", "r1")).SessionId;

            var sent = await chat.Chats.SendAsync(chatId, new ChatSendRequest("m1", "plain please"));
            Check(sent.SubmissionId > 0, "a send nobody checked is posted and answered, without asking the sends table: " + sent);
            await WaitUntilAsync(async () => (await chat.TextAsync(chatId)).Contains("Echo: plain please", StringComparison.Ordinal), "the message is answered",
                TimeSpan.FromSeconds(30));
            await chat.IdleAsync(chatId);
            Equal(1, Regex.Matches(await chat.TextAsync(chatId), "Echo: plain please").Count, "posted once");

            // What a send that may hold an approval notes after its post: the read fails, it is logged, and the posted send stands.
            spend.Posted(chatId, sent.SubmissionId, chatId + "/m1");

            // A checked send asks the table before anything is posted, so it fails without posting.
            await ThrowsAsync<SqliteException>(() => chat.Chats.SendAsync(chatId, new ChatSendRequest("m2", "checked please") { Spend = ChatSpendAnswer.Check }),
                "a checked send");
            Lacks(await chat.TextAsync(chatId), "checked please", "posts nothing");
        }),
    ];

    /// <summary>The Chat area in process with <paramref name="world"/>'s Spend Guard on its chat pages and over its faux models ($3 / $15
    /// per million tokens; no cap until the case adds one). Its tools ask first.</summary>
    private static Task<ChatHarness> SpendChatHarnessAsync(string name, GuardWorld world) =>
        ChatHarness.StartAsync(name, world.Over(FauxModelCatalog.CreateModels()), configure: services => services.AddSingleton(world.Guard));

    /// <summary>A running turn's request: <paramref name="text"/> as its newest user message, placed at <paramref name="placed"/>, then an
    /// answer (about 3,000 bytes in all).</summary>
    private static ImmutableArray<ChordJson> SpendTurn(string text, long placed) =>
    [
        ChordJson.Object([new("role", "user"), new("content", text), new("timestamp", (ChordJson)(double)placed)]),
        ChordJson.Object([new("role", "assistant"), new("content", new string('x', 2_900))]),
    ];

    /// <summary>What used up the approval <paramref name="id"/>, or null while it waits.</summary>
    private static string? SpendApprovalUsedBy(string path, long id) =>
        ReadDb(path, $"SELECT used_by FROM spend_approvals WHERE id = {id.ToString(CultureInfo.InvariantCulture)}", reader => reader.IsDBNull(0) ? null : reader.GetString(0)).Single();

    /// <summary>The ledger note of the reservation <paramref name="op"/>, or null.</summary>
    private static string? SpendChatNote(string path, string op) =>
        ReadDb(path, $"SELECT note FROM spend_reservations WHERE op = '{op.Replace("'", "''", StringComparison.Ordinal)}'", reader => reader.IsDBNull(0) ? null : reader.GetString(0))
            .SingleOrDefault();

    /// <summary>Starts the built app with --faux-metered over a fresh data folder whose tools ask first; returns it and its webagent.db.</summary>
    private static async Task<(WebAgentApp App, string Db)> SpendChatStartAsync(string root)
    {
        var data = Path.Join(root, "data");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Join(root, "work"));
        var path = Path.Join(data, "webagent.db");
        var setup = new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration]);
        setup.Migrate();
        await new SettingsStore(setup).SaveAsync(new ToolsSettings(Path.Join(root, "work"), ToolApprovalMode.Ask, []));
        SqliteConnection.ClearAllPools();
        return (await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data, Arguments = ["--faux-metered"] }), path);
    }

    /// <summary>The input cost of the largest request reserved so far, in micro-USD ($3 per million input tokens).</summary>
    private static long SpendChatInputMicros(string path) =>
        (long)Math.Ceiling(ReadDb(path, "SELECT input_tokens FROM spend_reservations", reader => reader.GetDouble(0)).DefaultIfEmpty(0).Max() * 3);

    /// <summary>Adds a cap on every metered request to the running app's database (a second ledger would run startup recovery against
    /// the app's rows).</summary>
    private static long SpendChatCap(string path, long limit, string action) => new WebAgentDb(path, []).Transaction((connection, transaction) =>
    {
        WebAgentDb.Execute(connection, transaction, """
            INSERT INTO spend_caps (scope, key, period, limit_micros, action, enabled, created_at, updated_at)
            VALUES ('everything', NULL, 'request', $limit, $action, 1, 0, 0);
            """, ("$limit", limit), ("$action", action));
        return Convert.ToInt64(WebAgentDb.Scalar(connection, transaction, "SELECT last_insert_rowid();"), CultureInfo.InvariantCulture);
    });

    private static void SpendChatCapAction(string path, long id, string action) => new WebAgentDb(path, []).Transaction((connection, transaction) =>
    {
        WebAgentDb.Execute(connection, transaction, "UPDATE spend_caps SET action = $action WHERE id = $id;", ("$action", action), ("$id", id));
        return 0;
    });

    private static long SpendChatRows(string path) => ReadDb(path, "SELECT COUNT(*) FROM spend_reservations", reader => reader.GetInt64(0)).Single();

    /// <summary>The notes of a chat's requests the user answered, oldest first.</summary>
    private static List<string> SpendChatNotes(string path, string chatId) =>
        ReadDb(path, $"SELECT note FROM spend_reservations WHERE note IS NOT NULL AND run = '{chatId.Replace("'", "''", StringComparison.Ordinal)}' ORDER BY created_at, op",
            reader => reader.GetString(0));
}
