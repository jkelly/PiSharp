// Decision 0011 (run W3, S4): Settings › Spend, History › Spend, the spend modals and /api/spend. The page cases run the built app as a child
// process with --faux-metered (the faux models at a test list price; no provider code path, no network) on a temporary data folder; ledger
// rows are seeded through a SpendLedger on the same webagent.db before the app starts. A planted OPENAI_API_KEY (an environment account)
// must never appear in any page, answer or CSV. The unit cases check the CSV's cells and the filters' query string. The subscription card's
// case signs in to ChatGPT through the fake OAuth endpoints and runs the app without --faux (pages and /api/spend only, nothing sent). The
// saved filters' cases (W3 fix, spend-ui) check the spend-filters section and its round trip through /api/spend/filters and History › Spend.
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Spend;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SpendPageCases() =>
    [
        Case("spend.pages.placeholder-is-gone", () =>
        {
            Check(typeof(PiSharp.WebAgent.Controllers.ShellController).GetMethod("SettingsLater") is null, "Settings › Spend is no later-milestone page: no settings placeholder is left (W4 T4)");
            Check(typeof(PiSharp.WebAgent.Controllers.SpendController).GetMethod("Settings") is not null, "SpendController serves it");
        }),

        Case("spend.pages.csv-cells-are-quoted-and-never-formulas", () =>
        {
            Equal("", SpendEndpoints.Cell(null), "null");
            Equal("plain", SpendEndpoints.Cell("plain"), "plain");
            Equal("\"a,b\"", SpendEndpoints.Cell("a,b"), "a comma");
            Equal("\"say \"\"hi\"\"\"", SpendEndpoints.Cell("say \"hi\""), "quotes doubled");
            Equal("'=SUM(A1)", SpendEndpoints.Cell("=SUM(A1)"), "a formula is neutralised");
            Equal("'+cmd", SpendEndpoints.Cell("+cmd"), "a plus");
            Equal("'@x", SpendEndpoints.Cell("@x"), "an at sign");
            Equal("\"'-1+2,3\"", SpendEndpoints.Cell("-1+2,3"), "a minus that is not a number, quoted for its comma");
            Equal("-0.500000", SpendEndpoints.Cell("-0.500000"), "a negative number stays a number");
            Equal("pisharp-spend-2026-10-21.csv", SpendEndpoints.CsvName(new DateTimeOffset(2026, 10, 21, 23, 0, 0, TimeSpan.Zero)), "the file name is the UTC day");
        }),

        Case("spend.pages.filters-round-trip-through-the-query-string", () =>
        {
            var values = new Dictionary<string, string>
            {
                ["q"] = " chat-1 ", ["from"] = "2026-10-21", ["to"] = "2026-10-01", ["provider"] = "anthropic", ["model"] = "claude-sonnet-5-5",
                ["source"] = "agent-compare", ["account"] = "acc-1", ["status"] = "uncertain", ["page"] = "3", ["size"] = "50", ["resolve"] = "op-1",
            };
            var filter = SpendHistoryFilter.Parse(key => values.GetValueOrDefault(key));
            Equal("chat-1", filter.Search, "trimmed");
            Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 21)), (filter.From!.Value, filter.To!.Value), "a reversed range is put in order");
            Equal((SpendSource.AgentCompare, LedgerStatus.Uncertain, 3, 50, "op-1"), (filter.Source!.Value, filter.Status!.Value, filter.Page, filter.Size, filter.Resolve), "the rest");
            Check(filter.Active, "filtered");
            var query = filter.Query();
            Equal(new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero), query.To!.Value, "the end date is inclusive (up to the next UTC midnight)");
            Equal((100, 50), (query.Offset, query.Limit), "paging");
            Equal("?q=chat-1&from=2026-10-01&to=2026-10-21&provider=anthropic&model=claude-sonnet-5-5&source=agent-compare&account=acc-1&status=uncertain&page=3&size=50",
                filter.QueryString(), "the query string");
            Equal("/history/spend?provider=anthropic", (new SpendHistoryFilter { Provider = "anthropic" }).Href, "defaults are left out");

            var odd = SpendHistoryFilter.Parse(key => key switch
            {
                "model" => "m", "source" => "nope", "status" => "bogus", "size" => "33", "page" => "-2", "from" => "21/10/2026", _ => null,
            });
            Equal((null, null, null, 25, 1, null), (odd.Model, odd.Source, odd.Status, odd.Size, odd.Page, odd.From), "values that are not allowed are left out (a model needs a provider)");
            Check(!odd.Active, "nothing filters");
        }),

        // W3 fix (spend-ui): the confirm modal's parts add up. 41,300 input tokens × $2/M are 82,600 micro-USD and 12,290 output tokens ×
        // $10/M are 122,900, a worst case of 205,500 ($0.2055); rounded to cents on their own they read $0.08 + $0.12 against $0.21. Under a
        // dollar the modal shows four decimals (as the composer's tooltip and the spend errors do), and at any size the input and output are
        // rounded so that they add up to the worst case shown: 1,005,000 + 1,005,000 micro-USD read $1.00 + $1.01 = $2.01, not $1.01 + $1.01.
        Case("spend.pages.confirm-parts-add-up-to-the-worst-case", () =>
        {
            var root = Temp("spend-pages-confirm-parts");
            try
            {
                var catalog = new EstCatalog().Add(EstProvider("anthropic", BillingKind.Metered), EstModel("anthropic", "claude-opus-5-5", new CatalogCost(2, 10)));
                var pages = Sp4Pages(root, catalog);
                foreach (var (input, output, worst, cap, shown) in new (int, int, long, long, string)[]
                {
                    (41_300, 12_290, 205_500, 100_000, "$0.0826 + $0.1229 = $0.2055"),
                    (502_500, 100_500, 2_010_000, 1_000_000, "$1.00 + $1.01 = $2.01"),
                    (1_025, 205, 4_100, 1_000, "$0.0020 + $0.0021 = $0.0041"),
                })
                {
                    var view = pages.ConfirmView(Sp4Call("anthropic", "claude-opus-5-5", input, output), Sp4Over(Sp4Priced("anthropic", "claude-opus-5-5", input, output, worst), cap));
                    var parts = Sp4Parts(view);
                    Equal(shown, $"{view.Parts[0].Value} + {view.Parts[1].Value} = {view.Parts[2].Value}", "the parts as shown");
                    Equal(parts["Worst case"], parts["Input"] + parts["Output"], $"Input + Output is the worst case ({shown})");
                    Equal(SpendMoney.Format(worst, worst < SpendMoney.MicrosPerDollar ? 4 : 2), view.Parts[2].Value, "the worst case is the estimate's, rounded once");
                    Equal(view.Parts[2].Value, view.Lead.Mark, "the lead names the same worst case");
                    Check(Math.Abs(parts["Input"] - (long)input * 2) <= 10_000 && Math.Abs(parts["Output"] - (long)output * 10) <= 10_000, "each part within a cent of its own cost: " + shown);
                }
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        // W3 fix (spend-ui): "The models above change nothing until you pick one yourself" only when the modal lists models; otherwise the
        // footnote is the promise alone. A cheaper model that fits is a suggestion; a catalog with no other model, or an unknown price with
        // no priced model, lists none.
        Case("spend.pages.confirm-footnote-names-models-only-when-it-lists-some", () =>
        {
            var root = Temp("spend-pages-confirm-footnote");
            try
            {
                var two = new EstCatalog().Add(EstProvider("anthropic", BillingKind.Metered),
                    EstModel("anthropic", "claude-opus-5-5", new CatalogCost(2, 10)), EstModel("anthropic", "claude-haiku-5", new CatalogCost(0.5, 2)));
                var call = Sp4Call("anthropic", "claude-opus-5-5", 41_300, 12_290);
                var over = Sp4Over(Sp4Priced("anthropic", "claude-opus-5-5", 41_300, 12_290, 205_500), 100_000);
                var listed = Sp4Pages(Path.Join(root, "two"), two).ConfirmView(call, over);
                Equal("anthropic/claude-haiku-5", string.Join(",", listed.Suggestions.Select(suggestion => suggestion.Provider + "/" + suggestion.Model)), "the cheaper model is suggested");
                Equal(SpendPages.NeverSwitches + " The models above change nothing until you pick one yourself.", listed.Footnote, "with models listed");

                var one = new EstCatalog().Add(EstProvider("anthropic", BillingKind.Metered), EstModel("anthropic", "claude-opus-5-5", new CatalogCost(2, 10)));
                var alone = Sp4Pages(Path.Join(root, "one"), one).ConfirmView(call, over);
                Equal(0, alone.Suggestions.Count, "no other model to suggest");
                Equal("PiSharp never switches your model or account.", alone.Footnote, "no models listed, so none is \"above\"");

                var unpriced = new EstCatalog().Add(EstProvider("gateway", BillingKind.Unknown), EstModel("gateway", "large", null));
                var unknownEstimate = new SpendEstimate { Provider = "gateway", Model = "large", Billing = BillingKind.Unknown, InputTokens = 1_000, OutputTokens = 8_192 };
                var unknown = Sp4Pages(Path.Join(root, "unpriced"), unpriced).ConfirmView(Sp4Call("gateway", "large", 1_000, 8_192),
                    SpendDecision.From(unknownEstimate, [new SpendCause { Kind = SpendCauseKind.UnknownPrice, Action = CapAction.Block }]));
                Equal(("sc-unknown", 0), (unknown.Pane, unknown.Suggestions.Count), "an unknown price with no priced model");
                Equal("PiSharp never switches your model or account.", unknown.Footnote, "the promise alone");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        // W3 fix (spend-ui): offline (--faux, --faux-metered) the catalog is the faux one and no other provider is ever called, so Settings ›
        // Spend lists only the faux provider: a machine's environment key for OpenAI is not an Unknown-price account with an Unknown-prices
        // rule. Online, an account of a provider the catalog does not list is still shown (Unknown price, "Not in the model catalog").
        Case("spend.pages.offline-accounts-are-the-faux-catalogs-only", () =>
        {
            var accounts = new EstAccounts(EstAccount("env-openai-openai_api_key", "openai", PiSharp.WebAgent.Credentials.AccountKind.Environment, source: "OPENAI_API_KEY"));
            foreach (var price in new[] { null, PiSharp.WebAgent.Faux.FauxPricing.Price })
            {
                var catalog = new PiSharp.WebAgent.Faux.FauxModelCatalog(PiSharp.WebAgent.Faux.FauxModelCatalog.CreateModels(), price);
                var settings = new EstSettings();
                var prices = new ModelPrices(catalog, settings);
                var what = price is null ? "--faux" : "--faux-metered";
                var offline = new BillingModes(catalog, settings, prices, accounts) { Offline = true };
                var rows = offline.Accounts();
                Equal("faux:-", string.Join(",", rows.Select(row => row.Provider + ":" + (row.Account?.Id ?? "-"))), what + ": only the faux provider");
                Equal(price is null ? BillingKind.Free : BillingKind.Metered, rows[0].Billing.Mode, what + ": billed as the faux catalog says");
                Check(rows.All(row => row.Billing.Mode != BillingKind.Unknown), what + ": no Unknown-price account");

                var online = new BillingModes(catalog, settings, prices, accounts).Accounts();
                Equal("faux:-,openai:env-openai-openai_api_key", string.Join(",", online.Select(row => row.Provider + ":" + (row.Account?.Id ?? "-"))), what + ": not offline, it is listed");
                Equal(new BillingResolution(BillingKind.Unknown, BillingKind.Unknown, false, "Not in the model catalog"), online[1].Billing, what + ": as an unlisted provider");
                Equal(online[1].Billing, offline.Resolve("openai", "env-openai-openai_api_key"), what + ": Resolve is unchanged offline");
            }
        }),

        Case("spend.pages.settings-caps-and-confirm-round-trip", async () =>
        {
            var envKey = Planted("sk-proj-");
            var root = Temp("spend-pages-settings");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart
                {
                    DataDirectory = Path.Join(root, "data"),
                    Faux = false,
                    Arguments = ["--faux-metered"],
                    Environment = new Dictionary<string, string> { ["OPENAI_API_KEY"] = envKey },
                });
                var seen = new StringBuilder();
                async Task<(int Status, JsonNode? Body, string Text)> Call(HttpMethod method, string path, JsonNode? body = null)
                {
                    var response = await app.SendAsync(method, path, body, origin: method == HttpMethod.Get ? null : app.Origin);
                    seen.AppendLine(response.Text);
                    if (path.StartsWith(SpendEndpoints.Prefix, StringComparison.Ordinal))
                        Check(response.Response.Headers.CacheControl?.NoStore == true, path + " is no-store");
                    return (response.Status, response.Body, response.Text);
                }
                async Task<string> Page(string path)
                {
                    var (status, _, text) = await Call(HttpMethod.Get, path);
                    Equal(200, status, path);
                    Sp4CheckPage(path, text);
                    return WebUtility.HtmlDecode(text);
                }

                // Empty: no caps, no spend.
                var settings = await Page("/settings/spend");
                Check(settings.Contains("No caps yet", StringComparison.Ordinal) && settings.Contains("data-caps-empty", StringComparison.Ordinal), "the empty caps state");
                Check(!settings.Contains("Coming in a later milestone", StringComparison.Ordinal), "not the placeholder");
                foreach (var part in new[] { "id=\"caps\"", "id=\"accounts\"", "id=\"unknown-prices\"", "id=\"reservations\"", "id=\"modal-spend-cap\"", "data-savebar hidden",
                             "spend.reservationOutputTokens · default 8192", "spend.warnPercent · default 80", "/js/spend-settings.js", "/css/spend.css" })
                    Check(settings.Contains(part, StringComparison.Ordinal), "settings has " + part);
                // --faux-metered: the faux provider is Metered at its test price. W3 fix (spend-ui): the environment key's provider has no
                // model in the faux catalog and is never called offline, so it is neither an Accounts row (as "Not in the model catalog ·
                // Unknown price") nor a row under Unknown prices.
                Check(Regex.IsMatch(settings, "data-spend-field=\"billing:faux\\|\" data-provider=\"faux\"[^>]*data-initial=\"metered\">\\s*<option value=\"metered\" selected=\"selected\">Metered \\(detected\\)</option>"),
                    "the faux provider is metered, detected: " + Sp4Around(settings, "billing:faux|"));
                Equal(1, Regex.Matches(settings, "data-spend-field=\"billing:").Count, "offline, the faux provider is the only account: " + Sp4Around(settings, "id=\"accounts\""));
                Check(!settings.Contains("billing:openai|", StringComparison.Ordinal) && !settings.Contains("Not in the model catalog", StringComparison.Ordinal),
                    "the environment account is not listed offline: " + Sp4Around(settings, "id=\"accounts\""));
                Check(!settings.Contains("data-spend-field=\"unknown:openai\"", StringComparison.Ordinal) && !settings.Contains("Unknown price (detected)", StringComparison.Ordinal),
                    "and has no row under Unknown prices: " + Sp4Around(settings, "id=\"unknown-prices\""));
                var envAccounts = await Call(HttpMethod.Get, "/api/credentials/accounts?provider=openai");
                Equal((200, 1), (envAccounts.Status, envAccounts.Body!["accounts"]!.AsArray().Count), "the environment account still exists: " + envAccounts.Text);
                var envId = envAccounts.Body!["accounts"]![0]!["id"]!.GetValue<string>();
                Check(settings.Contains("Example · <span class=\"ps-mono\">", StringComparison.Ordinal) && settings.Contains("data-worst-case-example", StringComparison.Ordinal),
                    "a worked example from a connected metered model");
                Check(!settings.Contains("allowanceGate", StringComparison.Ordinal) && !settings.Contains("Sample data", StringComparison.Ordinal), "no allowance gate without allowance data");

                var history = await Page("/history/spend");
                Check(history.Contains("No spend <em>yet</em>", StringComparison.Ordinal) && history.Contains("webagent.db", StringComparison.Ordinal), "the empty ledger");
                Check(history.Contains("<h1 class=\"app-title\">History</h1>", StringComparison.Ordinal), "History in the top bar");
                Check(Regex.IsMatch(history, "class=\"nav-link active\" aria-current=\"page\" href=\"/history/spend\">Spend</a>"), "the Spend tab is active");
                Check(Regex.IsMatch(history, "<button class=\"btn btn-outline-secondary\" type=\"button\" disabled><i class=\"bi bi-filetype-csv"), "Export CSV is disabled");
                Check(history.Contains("id=\"modal-resolve-reservation\"", StringComparison.Ordinal), "the Resolve modal's shell");

                // Caps: add, add again (idempotent), a different value for the same target (409), edit, invalid, remove twice.
                var daily = new JsonObject { ["scope"] = "everything", ["period"] = "day", ["limit"] = "5.00", ["action"] = "block" };
                var added = await Call(HttpMethod.Post, "/api/spend/caps", daily);
                Equal(200, added.Status, "add: " + added.Text);
                var dailyId = added.Body!["cap"]!["id"]!.GetValue<long>();
                Equal((5_000_000L, "everything", "day", "block", false), (added.Body!["cap"]!["limitMicros"]!.GetValue<long>(), added.Body["cap"]!["scope"]!.GetValue<string>(),
                    added.Body["cap"]!["period"]!.GetValue<string>(), added.Body["cap"]!["action"]!.GetValue<string>(), added.Body["existing"]!.GetValue<bool>()), "the cap");
                var again = await Call(HttpMethod.Post, "/api/spend/caps", daily.DeepClone());
                Equal((200, dailyId, true), (again.Status, again.Body!["cap"]!["id"]!.GetValue<long>(), again.Body["existing"]!.GetValue<bool>()), "the same cap again is the same cap");
                var other = await Call(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "day", ["limit"] = "7", ["action"] = "block" });
                Equal((409, "conflict"), (other.Status, other.Body!["code"]!.GetValue<string>()), "another daily cap for everything metered");
                Check(other.Body!["error"]!.GetValue<string>().Contains("already exists", StringComparison.Ordinal), "says so");
                var bad = await Call(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "day", ["limit"] = "lots", ["action"] = "block" });
                Equal((400, "invalid"), (bad.Status, bad.Body!["code"]!.GetValue<string>()), "a limit that is not an amount");
                Equal(400, (await Call(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["scope"] = "provider", ["period"] = "day", ["limit"] = "1", ["action"] = "ask" })).Status,
                    "a provider cap needs a provider");
                Equal(404, (await Call(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["scope"] = "account", ["key"] = "no-such-account", ["period"] = "day", ["limit"] = "1", ["action"] = "ask" })).Status,
                    "an account cap needs an account that exists");
                Equal(415, (await app.SendAsync(HttpMethod.Post, "/api/spend/caps", text: "scope=everything", contentType: "application/x-www-form-urlencoded", origin: app.Origin)).Status,
                    "JSON writes only");
                Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/spend/caps", daily.DeepClone(), origin: "http://evil.example")).Status, "same origin only");

                var edited = await Call(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["id"] = dailyId, ["limit"] = "6", ["action"] = "ask" });
                Equal((200, 6_000_000L, "ask", "day"), (edited.Status, edited.Body!["cap"]!["limitMicros"]!.GetValue<long>(), edited.Body["cap"]!["action"]!.GetValue<string>(),
                    edited.Body["cap"]!["period"]!.GetValue<string>()), "edit changes the limit and the action, not the window");
                Equal(404, (await Call(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["id"] = 999_999, ["limit"] = "1" })).Status, "editing a cap that is gone");

                var perRequest = await Call(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "request", ["limit"] = "0.01", ["action"] = "ask" });
                Equal(200, perRequest.Status, "a per-request cap");
                var source = await Call(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["scope"] = "source", ["key"] = "Chat", ["period"] = "month", ["limit"] = "40", ["action"] = "block" });
                Equal((200, "chat"), (source.Status, source.Body!["cap"]!["key"]!.GetValue<string>()), "a source cap stores the source's wire name");

                var listed = await Call(HttpMethod.Get, "/api/spend/caps");
                Equal(3, listed.Body!["caps"]!.AsArray().Count, "three caps");
                settings = await Page("/settings/spend");
                Check(!settings.Contains("data-caps-empty", StringComparison.Ordinal), "not empty any more");
                Check(settings.Contains("<div class=\"cap-num\">$0.00 / $6.00</div>", StringComparison.Ordinal), "the daily cap's spend and limit");
                Check(settings.Contains("data-cap-edit data-cap-id=\"" + dailyId + "\"", StringComparison.Ordinal) && settings.Contains("data-cap-amount=\"6.00\"", StringComparison.Ordinal),
                    "its edit button carries its values");
                Check(settings.Contains("worst case per request", StringComparison.Ordinal), "the per-request cap is checked per request");
                Check(Regex.IsMatch(settings, "Over the \\$0\\.01 per-request cap|Fits .*per-request cap"), "the example says which caps it fits");

                // The confirm modal: over the per-request cap (Ask), then over a Block day cap.
                var confirm = await Call(HttpMethod.Post, "/api/spend/confirm", new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["inputBytes"] = 3000 });
                Equal(200, confirm.Status, "confirm: " + confirm.Text);
                Equal(("ask", "sc-request", "spend_ask", true), (confirm.Body!["verdict"]!.GetValue<string>(), confirm.Body["pane"]!.GetValue<string>(), confirm.Body["code"]!.GetValue<string>(),
                    confirm.Body["canProceed"]!.GetValue<bool>()), "asks, for the per-request cap");
                var rawHtml = confirm.Body!["html"]!.GetValue<string>();
                var html = WebUtility.HtmlDecode(rawHtml);
                foreach (var part in new[] { "id=\"modal-spend-confirm\"", "Over your per-request cap", "<span class=\"ps-mono\">faux/echo</span>", "data-spend-choice=\"proceed-once\"",
                             "PiSharp never switches your model or account.", "Worst case", "data-spend-pane=\"sc-request\"" })
                    Check(html.Contains(part, StringComparison.Ordinal), "the modal has " + part);
                Check(!Regex.IsMatch(rawHtml, "\\son[a-z]+\\s*=", RegexOptions.IgnoreCase) && !rawHtml.Contains("<script", StringComparison.Ordinal), "no script in the fragment");
                var tokens = confirm.Body!["capOutputTokens"]?.GetValue<int>();
                Check(tokens is > 0 and < 8192 && html.Contains($"data-max-output-tokens=\"{tokens}\"", StringComparison.Ordinal), "a shorter output that fits is offered: " + tokens);
                // W3 fix (spend-ui): the rendered parts add up to the worst case, and with no model listed the footnote speaks of none.
                var shownParts = Regex.Matches(html, "<span class=\"k\">(?<key>Input|Output|Worst case)[^<]*</span><span class=\"v[^\"]*\">(?<value>[^<]+)</span>")
                    .ToDictionary(match => match.Groups["key"].Value, match => SpendMoney.TryParseUsd(match.Groups["value"].Value, out var micros) ? micros : -1);
                Equal(3, shownParts.Count, "the input, output and worst-case lines: " + Sp4Around(html, "ps-panel"));
                Equal(shownParts["Worst case"], shownParts["Input"] + shownParts["Output"], "the modal's input and output add up to its worst case: " + Sp4Around(html, "ps-panel"));
                Check(!html.Contains("data-spend-suggestion", StringComparison.Ordinal), "no cheaper faux model to suggest");
                Check(html.Contains("data-spend-footnote>PiSharp never switches your model or account.</span>", StringComparison.Ordinal) && !html.Contains("models above", StringComparison.Ordinal),
                    "no models listed, so no \"models above\": " + Sp4Around(html, "data-spend-footnote"));

                var fits = await Call(HttpMethod.Post, "/api/spend/confirm", new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["inputBytes"] = 30, ["maxTokens"] = 16 });
                Equal(("allow", null), (fits.Body!["verdict"]!.GetValue<string>(), fits.Body["html"]?.GetValue<string>()), "a request that fits is allowed with no modal");

                var block = await Call(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["scope"] = "provider", ["key"] = "faux", ["period"] = "month", ["limit"] = "0.01", ["action"] = "block" });
                Equal(200, block.Status, "a blocking month cap for faux");
                var blocked = await Call(HttpMethod.Post, "/api/spend/confirm", new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["inputBytes"] = 3000 });
                Equal(("block", "sc-period", "spend_blocked", false), (blocked.Body!["verdict"]!.GetValue<string>(), blocked.Body["pane"]!.GetValue<string>(), blocked.Body["code"]!.GetValue<string>(),
                    blocked.Body["canProceed"]!.GetValue<bool>()), "blocks, for the month cap first");
                var blockedHtml = WebUtility.HtmlDecode(blocked.Body!["html"]!.GetValue<string>());
                Check(blockedHtml.Contains("disabled aria-describedby=\"spend-confirm-blocked\">Proceed once", StringComparison.Ordinal), "Proceed once is not offered");
                Check(blockedHtml.Contains("It's also over your $0.01 per-request cap, which is set to Ask, but Block takes priority.", StringComparison.Ordinal), "the other cause");
                Check(blockedHtml.Contains("href=\"/settings/spend#caps\"", StringComparison.Ordinal), "where to raise the cap");
                Equal(400, (await Call(HttpMethod.Post, "/api/spend/confirm", new JsonObject { ["model"] = "echo" })).Status, "a call needs a provider");

                // Remove (twice).
                Equal(true, (await Call(HttpMethod.Post, $"/api/spend/caps/{dailyId}/remove")).Body!["removed"]!.GetValue<bool>(), "removed");
                Equal((200, false), await Call(HttpMethod.Post, $"/api/spend/caps/{dailyId}/remove") is var gone ? (gone.Status, gone.Body!["removed"]!.GetValue<bool>()) : default, "removing it again");

                // Settings: the reservation cap, the warning threshold, unknown prices, a billing override and back.
                var saved = await Call(HttpMethod.Post, "/api/spend/settings", new JsonObject { ["warnPercent"] = 70, ["reservationOutputTokens"] = "4096", ["unknownPrice"] = "block" });
                Equal((200, 70, 4096, "block"), (saved.Status, saved.Body!["warnPercent"]!.GetValue<int>(), saved.Body["reservationOutputTokens"]!.GetValue<int>(),
                    saved.Body["unknownPrice"]!.GetValue<string>()), "saved: " + saved.Text);
                Equal(400, (await Call(HttpMethod.Post, "/api/spend/settings", new JsonObject { ["warnPercent"] = 0 })).Status, "a threshold of 0%");
                Equal(400, (await Call(HttpMethod.Post, "/api/spend/settings", new JsonObject { ["unknownPrice"] = "maybe" })).Status, "an unknown behaviour");

                // The provider page's account card before any override: offline the key's provider is not in the faux catalog, so it is
                // never called and Settings › Spend has no Unknown-prices row for it; the card has no spend line pointing there (W3 retest).
                var provider = await Page("/settings/providers/openai");
                Check(!provider.Contains("data-account-spend", StringComparison.Ordinal) && !provider.Contains("#unknown-prices", StringComparison.Ordinal),
                    "an account not used offline has no spend line: " + Sp4Around(provider, "data-account-spend"));

                var overridden = await Call(HttpMethod.Post, "/api/spend/settings", new JsonObject
                {
                    ["billing"] = new JsonArray(new JsonObject { ["provider"] = "openai", ["account"] = envId, ["mode"] = "metered" },
                        new JsonObject { ["provider"] = "faux", ["mode"] = "free" }),
                    ["unknownPriceRules"] = new JsonArray(new JsonObject { ["provider"] = "openai", ["behavior"] = "allow" }),
                });
                Equal(200, overridden.Status, "an override: " + overridden.Text);
                Equal("openai|" + envId + "|metered,faux||free", string.Join(",", overridden.Body!["billingOverrides"]!.AsArray().Select(item =>
                    $"{item!["provider"]!.GetValue<string>()}|{item["account"]?.GetValue<string>()}|{item["mode"]!.GetValue<string>()}")), "kept");
                settings = await Page("/settings/spend");
                Check(Regex.IsMatch(settings, "data-spend-field=\"billing:faux\\|\" data-provider=\"faux\"[^>]*data-initial=\"free\">") &&
                    settings.Contains("override · detected Metered", StringComparison.Ordinal), "the page says it is an override: " + Sp4Around(settings, "id=\"accounts\""));
                Check(!settings.Contains("billing:openai|", StringComparison.Ordinal), "an override does not list an account that is not used offline");
                Check(Regex.IsMatch(settings, "value=\"70\"\\s+data-spend-field=\"warnPercent\" data-initial=\"70\""), "the saved threshold is the field's value and baseline");

                // The provider page's account card of a metered account: today's spend and the daily cap line (none is left).
                provider = await Page("/settings/providers/openai");
                Check(Regex.IsMatch(provider, "data-account-spend=\"metered\">Today \\$0\\.00 · <a href=\"/settings/spend#caps\">no daily cap</a>"),
                    "the metered key's spend line: " + Sp4Around(provider, "data-account-spend"));
                var shared = await Call(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "day", ["limit"] = "2.50", ["action"] = "ask" });
                Equal(200, shared.Status, "a shared daily cap");
                provider = await Page("/settings/providers/openai");
                Check(Regex.IsMatch(provider, "data-account-spend=\"metered\">Today \\$0\\.00 · <a href=\"/settings/spend#caps\">shared daily cap</a> \\$0\\.00 / \\$2\\.50</div>"),
                    "the shared daily cap's line: " + Sp4Around(provider, "data-account-spend"));

                var back = await Call(HttpMethod.Post, "/api/spend/settings", new JsonObject
                {
                    ["billing"] = new JsonArray(new JsonObject { ["provider"] = "openai", ["account"] = envId, ["mode"] = "unknown" }, new JsonObject { ["provider"] = "faux", ["mode"] = "metered" }),
                });
                Equal(0, back.Body!["billingOverrides"]!.AsArray().Count, "choosing the detected mode removes the override");
                Equal(404, (await Call(HttpMethod.Post, "/api/spend/prices", new JsonObject { ["provider"] = "faux", ["model"] = "nope", ["input"] = "1", ["output"] = "2" })).Status,
                    "a price for a model that does not exist");
                Equal(409, (await Call(HttpMethod.Post, "/api/spend/prices", new JsonObject { ["provider"] = "faux", ["model"] = "echo", ["input"] = "1", ["output"] = "2" })).Status,
                    "an entered price never replaces a catalog price");
                Equal(false, (await Call(HttpMethod.Post, "/api/spend/prices/remove", new JsonObject { ["provider"] = "faux", ["model"] = "echo" })).Body!["removed"]!.GetValue<bool>(),
                    "removing a price that was never entered");


                var csv = await Call(HttpMethod.Get, "/api/spend/ledger.csv");
                Equal(200, csv.Status, "an empty CSV");
                Check(csv.Text.TrimStart('﻿').StartsWith("time_utc,op,source,provider,model,account_id,account,billing,status,", StringComparison.Ordinal), "its header");

                Check(!seen.ToString().Contains(envKey, StringComparison.Ordinal), "the planted key never appears in any answer");
                Check(!seen.ToString().Contains(envKey[^12..], StringComparison.Ordinal), "nor its tail");
                Check(!app.Log.Contains(envKey, StringComparison.Ordinal), "nor in the log");
                await app.StopAsync();
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("spend.pages.history-ledger-resolve-and-csv", async () =>
        {
            var root = Temp("spend-pages-history");
            try
            {
                var data = Path.Join(root, "data");
                Directory.CreateDirectory(data);
                var path = Path.Join(data, "webagent.db");
                var db = new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration, .. SpendLedgerMigrations.All]);
                db.Migrate();
                var ledger = new SpendLedger(db, new SettingsStore(db));
                await ledger.SaveCapAsync(new SpendCap(0, CapScope.Everything, null, CapPeriod.Day, 5_000_000, CapAction.Block));
                await ledger.SaveCapAsync(new SpendCap(0, CapScope.Provider, "anthropic", CapPeriod.Month, 450_000, CapAction.Block));
                await Sp4Seed(ledger, "op-settled", Sp4Estimate("anthropic", 120_000), SpendSource.Chat, "chat-1", async op => await ledger.SettleAsync(op, new SpendUsage(1_000, 100) { CostMicros = 3_000 }));
                await Sp4Seed(ledger, "op-uncertain", Sp4Estimate("anthropic", 380_000), SpendSource.Chat, "chat-2",
                    async op => await ledger.MarkUncertainAsync(op, "stream dropped, no usage reported", 317_100));
                await Sp4Seed(ledger, "op-sub", Sp4Estimate("openai-codex", 210_000, BillingKind.Subscription), SpendSource.AgentCompare, "cmp-1",
                    async op => await ledger.SettleAsync(op, new SpendUsage(2_000, 200) { CostMicros = 21_000 }));
                await Sp4Seed(ledger, "op-released", Sp4Estimate("openai", 50_000), SpendSource.Playground, "pg-1", async op => await ledger.ReleaseAsync(op, "failed before any output"));
                await Sp4Seed(ledger, "op-formula", Sp4Estimate("google", 40_000), SpendSource.Bench, "bench-1",
                    async op => await ledger.SettleAsync(op, new SpendUsage(500, 50) { CostMicros = 1_500 }), note: "=HYPERLINK(\"http://evil.example\")");
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = data, Faux = false, Arguments = ["--faux-metered"] });
                async Task<string> Page(string path)
                {
                    var response = await app.Get(path);
                    Equal(200, response.Status, path + ": " + Cut(response.Text));
                    Sp4CheckPage(path, response.Text);
                    return WebUtility.HtmlDecode(response.Text);
                }

                var page = await Page("/history/spend");
                Equal(5, Regex.Matches(page, "<tr data-spend-row=").Count, "five rows");
                Check(page.Contains("data-uncertain-banner", StringComparison.Ordinal) && page.Contains("1 uncertain", StringComparison.Ordinal), "the uncertain banner");
                Check(page.Contains("data-resolve-op=\"op-uncertain\"", StringComparison.Ordinal), "Resolve… for the uncertain row");
                Check(page.Contains("href=\"/chat/chat-1\" title=\"Open chat\"", StringComparison.Ordinal), "a chat row opens its chat");
                Check(!page.Contains("href=\"/chat/pg-1\"", StringComparison.Ordinal), "a Playground row does not");
                Check(page.Contains("≈ $0.0210", StringComparison.Ordinal) && page.Contains("<div class=\"text-ps-faint\">list</div>", StringComparison.Ordinal), "a subscription row at list price");
                Check(page.Contains("$0.0030", StringComparison.Ordinal), "the settled row's actual cost");
                Check(page.Contains("<span class=\"ps-chip\" data-tone=\"warn\">uncertain</span>", StringComparison.Ordinal), "the uncertain status");
                Check(page.Contains("stream dropped, no usage reported", StringComparison.Ordinal), "with its reason");
                Check(page.Contains("$0.38 <small>of $5.00</small>", StringComparison.Ordinal), "today against the daily cap, uncertain included: " + Sp4Around(page, "data-spend-meter=\"Today\""));
                Check(page.Contains("includes $0.38 uncertain", StringComparison.Ordinal), "says the uncertain part");
                Check(Regex.IsMatch(page, "data-tone=\"warn\" title=\"anthropic monthly cap: \\$0\\.38 of \\$0\\.45 · blocks at the cap · above the 80% warning threshold\">anthropic 85% of \\$0\\.45"),
                    "the anthropic month cap above its warning threshold");
                Check(page.Contains("Not counted toward money caps: subscription use ≈ $0.02 list this month.", StringComparison.Ordinal), "what is not counted");
                Check(page.Contains("href=\"/api/spend/ledger.csv\"", StringComparison.Ordinal), "Export CSV");
                Check(page.Contains("Showing 1–5 of 5 requests", StringComparison.Ordinal), "the pager");

                var filtered = await Page("/history/spend?provider=openai&q=");
                Equal(1, Regex.Matches(filtered, "<tr data-spend-row=").Count, "one openai row");
                Check(filtered.Contains("Matching requests", StringComparison.Ordinal) && filtered.Contains("provider: openai", StringComparison.Ordinal), "the filtered state and its chip");
                Check(filtered.Contains("1 of 5 requests", StringComparison.Ordinal), "how many match");
                Check(filtered.Contains("href=\"/api/spend/ledger.csv?provider=openai\"", StringComparison.Ordinal), "the export keeps the filters");
                var none = await Page("/history/spend?q=nothing-matches-this");
                Check(none.Contains("No requests match these filters", StringComparison.Ordinal), "no match");

                var deepLink = await Page("/history/spend?status=uncertain&resolve=op-uncertain");
                Check(deepLink.Contains("data-op=\"op-uncertain\" data-open=\"true\"", StringComparison.Ordinal) && deepLink.Contains("Resolve uncertain spend", StringComparison.Ordinal),
                    "?resolve= opens the modal on load");
                var fragment = await app.Get("/history/spend/resolve?op=op-uncertain");
                Equal(200, fragment.Status, "the modal's content");
                Sp4CheckFragment(fragment.Text);
                var resolveText = WebUtility.HtmlDecode(fragment.Text);
                foreach (var part in new[] { "Settle at $0.32", "Release $0.38", "Save amount", "Counts toward</span><span class=\"v\">today and anthropic monthly</span>", "After settling: today $0.32 of $5.00",
                             "After releasing: today $0.00 of $5.00", "account acc-1" })
                    Check(resolveText.Contains(part, StringComparison.Ordinal), "the modal has " + part + ": " + Sp4Around(resolveText, "Counts toward"));
                Equal(409, (await app.Get("/history/spend/resolve?op=op-settled")).Status, "a settled row has nothing to resolve");
                Equal(404, (await app.Get("/history/spend/resolve?op=nope")).Status, "no such row");

                async Task<(int Status, JsonNode? Body, string Text)> Resolve(JsonObject body)
                {
                    var response = await app.SendAsync(HttpMethod.Post, "/api/spend/resolve", body, origin: app.Origin);
                    return (response.Status, response.Body, response.Text);
                }
                var resolved = await Resolve(new JsonObject { ["op"] = "op-uncertain", ["choice"] = "estimate" });
                Equal(200, resolved.Status, "resolved: " + resolved.Text);
                Equal(("settled", 317_100L, true, false), (resolved.Body!["reservation"]!["state"]!.GetValue<string>(), resolved.Body["reservation"]!["actualMicros"]!.GetValue<long>(),
                    resolved.Body["reservation"]!["resolvedByUser"]!.GetValue<bool>(), resolved.Body["again"]!.GetValue<bool>()), "settled at the estimate");
                Equal("Settled at the estimate, $0.3171. Today's metered spend is $0.32.", resolved.Body!["toast"]!["body"]!.GetValue<string>(), "the resolved toast");
                var twice = await Resolve(new JsonObject { ["op"] = "op-uncertain", ["choice"] = "estimate" });
                Equal((200, true), (twice.Status, twice.Body!["again"]!.GetValue<bool>()), "the same resolution again is fine");
                Equal((409, "conflict"), await Resolve(new JsonObject { ["op"] = "op-uncertain", ["choice"] = "release" }) is var other ? (other.Status, other.Body!["code"]!.GetValue<string>()) : default,
                    "a different one is a conflict");
                Equal(404, (await Resolve(new JsonObject { ["op"] = "nope", ["choice"] = "release" })).Status, "no such row");
                Equal(400, (await Resolve(new JsonObject { ["op"] = "op-settled", ["choice"] = "amount" })).Status, "an amount needs an amount");

                page = await Page("/history/spend");
                Check(!page.Contains("data-uncertain-banner", StringComparison.Ordinal), "no banner after resolving");
                Check(page.Contains("<span class=\"ps-chip\" data-tone=\"outline\">estimate</span>", StringComparison.Ordinal) && page.Contains("resolved by you", StringComparison.Ordinal),
                    "the row is settled at an estimate, by you");
                var settings = await Page("/settings/spend");
                Check(settings.Contains("data-tone=\"ok\" data-uncertain-count>None</span>", StringComparison.Ordinal), "Settings › Spend has no uncertain spend left");
                Check(settings.Contains("<div class=\"cap-num\">$0.32 / $0.45</div>", StringComparison.Ordinal), "the anthropic cap counts the estimate");

                var csv = await app.Get("/api/spend/ledger.csv");
                Equal(200, csv.Status, "the CSV");
                Equal("text/csv", csv.Response.Content.Headers.ContentType?.MediaType, "its type");
                Equal(SpendEndpoints.CsvName(DateTimeOffset.UtcNow), csv.Response.Content.Headers.ContentDisposition?.FileNameStar ?? csv.Response.Content.Headers.ContentDisposition?.FileName?.Trim('"'),
                    "its file name");
                var lines = csv.Text.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                Equal(6, lines.Length, "a header and five rows");
                // W4 L3: the tokens a reservation assumed say so, and the usage the provider reported stands beside them where there is any.
                Equal(SpendEndpoints.CsvHeader, lines[0], "the header");
                Check(lines[0].Contains(",list_usd,assumed_input_tokens,assumed_output_tokens,reported_input_tokens,reported_output_tokens,reported_cache_read_tokens,reported_cache_write_tokens,run,", StringComparison.Ordinal) &&
                    !lines[0].Contains(",input_tokens,", StringComparison.Ordinal) && !lines[0].Contains(",output_tokens,", StringComparison.Ordinal), "the token columns say what they hold: " + lines[0]);
                Check(lines.Any(line => line.Contains(",op-uncertain,chat,anthropic,claude-sonnet-5-5,acc-1,account acc-1,metered,estimate,0.380000,0.317100,0.317100,0.000000,12000,8192,,,,,chat-2,", StringComparison.Ordinal)),
                    "the resolved row, with the tokens it assumed and no reported usage: " + string.Join(" | ", lines));
                Check(lines.Any(line => line.Contains(",op-settled,chat,anthropic,claude-sonnet-5-5,acc-1,account acc-1,metered,settled,0.120000,0.003000,0.003000,0.000000,12000,8192,1000,100,0,0,chat-1,", StringComparison.Ordinal)),
                    "the settled row, with the usage the provider reported beside the tokens it assumed: " + string.Join(" | ", lines));
                Check(lines.Any(line => line.Contains(",op-released,playground,openai,openai-model,acc-1,account acc-1,metered,released,0.050000,,0.000000,0.000000,12000,8192,,,,,pg-1,", StringComparison.Ordinal)),
                    "a released row reports nothing: " + string.Join(" | ", lines));
                Check(lines.Any(line => line.Contains(",\"'=HYPERLINK(\"\"http://evil.example\"\")\",", StringComparison.Ordinal)), "a formula note is neutralised");
                var openai = await app.Get("/api/spend/ledger.csv?provider=openai");
                Equal(2, openai.Text.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length, "the CSV keeps the filters");

                var json = await app.Get("/api/spend/ledger?status=estimate");
                Equal((1, 5), (json.Body!["total"]!.GetValue<int>(), json.Body["totalAll"]!.GetValue<int>()), "the ledger as JSON");
                Equal("account acc-1", json.Body!["items"]![0]!["account"]!.GetValue<string>(), "rows name their account");
                Check(!app.Log.Contains("Exception", StringComparison.Ordinal), "no exception in the log: " + app.Log);
                await app.StopAsync();
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        // W3 fix (tests-ci): the subscription account card. A ChatGPT sign-in is stored through the fake OAuth endpoints (the device code
        // flow, in process), then the real Program runs without --faux (so openai-codex is in the catalog and resolves as Subscription) with
        // no provider variable. Only pages and /api/spend are fetched: nothing is sent to a provider and no refresh is due.
        Case("spend.pages.subscription-account-card-says-list-cost-not-counted", async () =>
        {
            var root = Temp("spend-pages-subscription");
            try
            {
                var access = CodexJwt("acct-sub");
                var refresh = Planted("rt-");
                string accountId;
                await using (var vault = new VaultHost("unused", root: root))
                {
                    CodexDevice(vault.Http);
                    CodexTokens(vault.Http, "dac", access, refresh, verifier: "dcv");
                    var started = await vault.SignIn.StartAsync(new SignInRequest("openai-codex", SignInMethod.DeviceCode));
                    var done = await FinishedAsync(vault.SignIn, started.FlowId);
                    Equal(SignInState.Succeeded, done.State, "the ChatGPT sign-in is stored: " + done.Message);
                    Equal(AccountKind.OAuth, done.Account!.Kind, "a sign-in account");
                    accountId = done.Account.Id;
                }

                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Faux = false, Environment = NoProviderEnvironment() });
                // A shared daily cap: a card that wrongly treated the sign-in as metered would show "shared daily cap $0.00 / $5.00".
                var cap = await app.SendAsync(HttpMethod.Post, "/api/spend/caps", new JsonObject { ["scope"] = "everything", ["period"] = "day", ["limit"] = "5.00", ["action"] = "block" },
                    origin: app.Origin);
                Equal(200, cap.Status, "a shared daily cap: " + cap.Text);

                var response = await app.Get("/settings/providers/openai-codex");
                Equal(200, response.Status, "the provider page: " + Cut(response.Text));
                Sp4CheckPage("/settings/providers/openai-codex", response.Text);
                var page = WebUtility.HtmlDecode(response.Text);
                var card = Sp4AccountCard(page, accountId);
                var line = Regex.Match(card, "<div [^>]*title=\"(?<title>[^\"]*)\" data-account-spend=\"(?<mode>[a-z]+)\">(?<text>.*?)</div>", RegexOptions.Singleline);
                Check(line.Success, "the card has a spend line: " + Sp4Around(card, "data-account-spend"));
                Equal("subscription", line.Groups["mode"].Value, "the sign-in's spend line is the subscription's");
                Equal("Subscription · list cost not counted", line.Groups["text"].Value, "the subscription note");
                Equal("Requests show an ≈ list cost for comparison. It never counts toward spend caps.", line.Groups["title"].Value, "and why");
                Check(line.Value.Contains("text-ps-faint", StringComparison.Ordinal), "in the quiet tone, never a warning");
                Check(!card.Contains("/settings/spend#caps", StringComparison.Ordinal) && !card.Contains("daily cap", StringComparison.Ordinal)
                    && !card.Contains("Today $", StringComparison.Ordinal) && !Regex.IsMatch(card, "\\$\\d+\\.\\d{2} / \\$\\d+\\.\\d{2}"),
                    "no metered spend and no cap progress on the card: " + Sp4Around(card, "data-account-spend"));
                Equal(1, Regex.Matches(card, "list cost not counted").Count, "the note is said once (the plan line names the plan)");
                Check(card.Contains("ChatGPT Plus", StringComparison.Ordinal), "the plan");

                // Settings › Spend agrees: the sign-in is detected as a subscription.
                var settings = await app.Get("/settings/spend");
                Equal(200, settings.Status, "Settings › Spend");
                var spend = WebUtility.HtmlDecode(settings.Text);
                Check(Regex.IsMatch(spend, $"data-spend-field=\"billing:openai-codex\\|{Regex.Escape(accountId)}\" data-provider=\"openai-codex\"[^>]*data-initial=\"subscription\">"),
                    "the sign-in is billed as a subscription: " + Sp4Around(spend, "billing:openai-codex|"));
                Check(spend.Contains("<option value=\"subscription\" selected=\"selected\">Subscription (detected)</option>", StringComparison.Ordinal), "detected");

                NoSecret(response.Text + settings.Text + cap.Text, "a page or answer", access, refresh);
                await app.StopAsync();
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal) && !app.Log.Contains("Exception", StringComparison.Ordinal), "a clean run: " + app.Log);
                NoSecret(app.Log, "the app's log", access, refresh);
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        // W3 fix (spend-ui): History › Spend's saved filters (the mockups' Saved filters menu, which S4 had replaced with fixed quick
        // filters). The section keeps a name and the filters' query string: paging, the page size and ?resolve= are never saved, a filter
        // needs at least one value, and a later filter with the same name (ignoring case) replaces the earlier one in its place.
        Case("spend.pages.saved-filters-normalise", () =>
        {
            var defaults = SpendSavedFilters.CreateDefault().Normalize();
            Equal("Uncertain only=status=uncertain|Settled at an estimate=status=estimate|Chats only=source=chat",
                string.Join("|", defaults.Filters!.Select(filter => filter.Name + "=" + filter.Query)), "a new data folder starts with the filters that need no date");
            Equal("spend-filters", SpendSavedFilters.SectionName, "its own section, not the guard's");
            Equal("provider=anthropic&status=uncertain", SpendSavedFilters.NormalizeQuery("?status=uncertain&page=3&size=50&resolve=op-1&provider=anthropic&bogus=1"),
                "the filters only, in their fixed order");
            Equal("q=a%20b&from=2026-10-01&to=2026-10-31", SpendSavedFilters.NormalizeQuery("to=2026-10-31&from=2026-10-01&q=a+b"), "a search and a date range");
            foreach (var empty in new[] { null, "", "?page=2&size=50", "status=bogus&model=m" })
                Check(Throws<ArgumentException>(() => SpendSavedFilters.NormalizeQuery(empty), "no filter: " + empty).Message.Contains("at least one filter", StringComparison.Ordinal),
                    "says so: " + empty);
            Equal("Anthropic this month", SpendSavedFilters.NormalizeName("  Anthropic \t this   month "), "trimmed, runs of space as one");
            Throws<ArgumentException>(() => SpendSavedFilters.NormalizeName(" "), "an empty name");
            Equal(80, SpendSavedFilters.NormalizeName(new string('n', 80)).Length, "80 characters is allowed");
            Throws<ArgumentException>(() => SpendSavedFilters.NormalizeName(new string('n', 81)), "81 is not");
            var merged = new SpendSavedFilters([new("Mine", "status=uncertain"), new("Chats", "source=chat"), new(" mine ", "?status=estimate&page=4")]).Normalize();
            Equal("mine=status=estimate|Chats=source=chat", string.Join("|", merged.Filters!.Select(filter => filter.Name + "=" + filter.Query)),
                "the later filter called mine replaces the earlier one in its place");
            Equal("/history/spend?status=estimate", merged.Find("MINE")!.Href, "found ignoring case");
            Throws<ArgumentException>(() => new SpendSavedFilters([.. Enumerable.Range(0, SpendSavedFilters.MaxFilters + 1).Select(i => new SpendSavedFilter("f" + i, "source=chat"))]).Normalize(),
                "at most 50");

            var root = Temp("spend-pages-saved-filters-unit");
            try
            {
                var pages = Sp4Pages(root, new EstCatalog().Add(EstProvider("anthropic", BillingKind.Metered), EstModel("anthropic", "claude-opus-5-5", new CatalogCost(2, 10))));
                var filter = new SpendHistoryFilter { Provider = "anthropic", Model = "claude-opus-5-5", Source = SpendSource.Chat, Account = "acc-1", Status = LedgerStatus.Uncertain, Search = "x", Page = 3 };
                Equal("search: x|date|provider: anthropic|model: claude-opus-5-5|source: Chat|account: account acc-1|status: uncertain",
                    string.Join("|", pages.FilterChips(filter with { From = new DateOnly(2026, 10, 1) }).Select(chip => chip.Name == "date" ? "date" : chip.Text)), "the chips");
                Equal("/history/spend?q=x&provider=anthropic&source=chat&account=acc-1&status=uncertain",
                    pages.FilterChips(filter).Single(chip => chip.Name == "model").Href, "a chip's link leaves out that filter and the page");
                Equal("anthropic/claude-opus-5-5 · Chat · account acc-1 · uncertain · “x”", pages.SuggestedFilterName(filter), "the suggested name");
                Equal("anthropic · 2026-10-21", pages.SuggestedFilterName(new SpendHistoryFilter { Provider = "anthropic", From = new DateOnly(2026, 10, 21), To = new DateOnly(2026, 10, 21) }),
                    "one day");
                Equal("", pages.SuggestedFilterName(new SpendHistoryFilter()), "nothing to name");
                Equal(SpendSavedFilters.MaxNameLength, pages.SuggestedFilterName(new SpendHistoryFilter { Search = new string('s', 200) }).Length, "a long suggestion is cut");
                var saved = pages.SavedFilters(new SpendHistoryFilter { Status = LedgerStatus.Uncertain, Page = 2, Size = 50 });
                Equal("Uncertain only:bi-question-circle:True|Settled at an estimate:bi-calculator:False|Chats only:bi-chat-square-text:False",
                    string.Join("|", saved.Select(view => $"{view.Name}:{view.Icon}:{view.Current}")), "the menu marks the filter the page shows, whatever its page");
                Equal(("status: uncertain", "/history/spend?status=uncertain"), (saved[0].Description, saved[0].Href), "described by its chips");
                Check(pages.SavedFilters(new SpendHistoryFilter()).All(view => !view.Current), "nothing is marked on the unfiltered page");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        // W3 fix (spend-ui): the saved filters round trip through /api/spend/filters and the page: the menu lists them (the one the page
        // shows marked), Save current filters… is offered only when a filter is set and suggests a name, a save again is the same filter,
        // the same name replaces, Manage saved filters renames and removes, and what the page shows is what webagent.db holds. A
        // cross-origin write is refused, and no page or answer carries the planted environment key.
        Case("spend.pages.saved-filters-round-trip", async () =>
        {
            var envKey = Planted("sk-proj-");
            var root = Temp("spend-pages-saved-filters");
            try
            {
                var data = Path.Join(root, "data");
                Directory.CreateDirectory(data);
                var path = Path.Join(data, "webagent.db");
                var db = new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration, .. SpendLedgerMigrations.All]);
                db.Migrate();
                var ledger = new SpendLedger(db, new SettingsStore(db));
                await Sp4Seed(ledger, "op-chat", Sp4Estimate("anthropic", 120_000), SpendSource.Chat, "chat-1", async op => await ledger.SettleAsync(op, new SpendUsage(1_000, 100) { CostMicros = 3_000 }));
                await Sp4Seed(ledger, "op-play", Sp4Estimate("openai", 50_000), SpendSource.Playground, "pg-1", async op => await ledger.ReleaseAsync(op, "failed before any output"));
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                await using var app = await WebAgentApp.StartAsync(root, new AppStart
                {
                    DataDirectory = data,
                    Faux = false,
                    Arguments = ["--faux-metered"],
                    Environment = new Dictionary<string, string> { ["OPENAI_API_KEY"] = envKey },
                });
                var seen = new StringBuilder();
                async Task<(int Status, JsonNode? Body, string Text)> Call(HttpMethod method, string path, JsonNode? body = null)
                {
                    var response = await app.SendAsync(method, path, body, origin: method == HttpMethod.Get ? null : app.Origin);
                    seen.AppendLine(response.Text);
                    if (path.StartsWith(SpendEndpoints.Prefix, StringComparison.Ordinal))
                        Check(response.Response.Headers.CacheControl?.NoStore == true, path + " is no-store");
                    return (response.Status, response.Body, response.Text);
                }
                async Task<string> Page(string path)
                {
                    var (status, _, text) = await Call(HttpMethod.Get, path);
                    Equal(200, status, path + ": " + Cut(text));
                    Sp4CheckPage(path, text);
                    return WebUtility.HtmlDecode(text);
                }
                static string Menu(string page) => Sp4Between(page, "data-saved-filters>", "</ul>");
                static string Names(JsonNode? body) =>
                    string.Join("|", body!["filters"]!.AsArray().Select(filter => filter!["name"]!.GetValue<string>() + "=" + filter["query"]!.GetValue<string>()));
                Task<(int Status, JsonNode? Body, string Text)> Save(string name, string query) =>
                    Call(HttpMethod.Post, "/api/spend/filters", new JsonObject { ["name"] = name, ["query"] = query });
                Task<(int Status, JsonNode? Body, string Text)> Manage(params (string Name, string Query)[] filters) =>
                    Call(HttpMethod.Post, "/api/spend/filters/manage",
                        new JsonObject { ["filters"] = new JsonArray([.. filters.Select(filter => (JsonNode)new JsonObject { ["name"] = filter.Name, ["query"] = filter.Query })]) });

                // The unfiltered page: the menu as the mockup draws it, with the starting filters; nothing to save yet.
                var page = await Page("/history/spend");
                var menu = Menu(page);
                Check(page.Contains("<i class=\"bi bi-bookmark me-1\" aria-hidden=\"true\"></i>Saved filters</button>", StringComparison.Ordinal)
                    && !page.Contains("Quick filters", StringComparison.Ordinal), "a Saved filters menu, not quick filters");
                foreach (var (name, href) in new[] { ("Uncertain only", "/history/spend?status=uncertain"), ("Settled at an estimate", "/history/spend?status=estimate"), ("Chats only", "/history/spend?source=chat") })
                    Check(Regex.IsMatch(menu, $"<a class=\"dropdown-item text-truncate\" href=\"{Regex.Escape(href)}\" title=\"[^\"]+\" data-saved-filter=\"{name}\">"), $"the menu has {name}: {menu}");
                Check(menu.Contains("data-bs-target=\"#modal-save-spend-filter\" disabled=\"disabled\" data-save-filter-open>", StringComparison.Ordinal), "nothing to save without a filter: " + menu);
                Check(menu.Contains("data-bs-target=\"#modal-manage-spend-filters\" data-manage-filters-open>", StringComparison.Ordinal), "Manage saved filters is offered");
                foreach (var part in new[] { "id=\"modal-save-spend-filter\"", "id=\"modal-manage-spend-filters\"", "data-mf-row data-query=\"status=uncertain\" data-name=\"Uncertain only\"" })
                    Check(page.Contains(part, StringComparison.Ordinal), "the page has " + part);
                var listed = await Call(HttpMethod.Get, "/api/spend/filters");
                Equal((200, "Uncertain only=status=uncertain|Settled at an estimate=status=estimate|Chats only=source=chat"), (listed.Status, Names(listed.Body)), "the list");

                // Save current filters…: added (paging, size and resolve left out), the same again, then the same name ignoring case replaces it.
                var added = await Save("Anthropic chats", "?provider=anthropic&source=chat&page=2&size=50&resolve=op-chat");
                Equal(200, added.Status, "saved: " + added.Text);
                Equal(("added", "provider=anthropic&source=chat", "/history/spend?provider=anthropic&source=chat", "provider: anthropic · source: Chat", "bi-chat-square-text"),
                    (added.Body!["outcome"]!.GetValue<string>(), added.Body["filter"]!["query"]!.GetValue<string>(), added.Body["filter"]!["href"]!.GetValue<string>(),
                        added.Body["filter"]!["description"]!.GetValue<string>(), added.Body["filter"]!["icon"]!.GetValue<string>()), "the saved filter");
                Equal(("Filter saved", "Anthropic chats is under Saved filters: provider: anthropic · source: Chat."),
                    (added.Body!["toast"]!["title"]!.GetValue<string>(), added.Body["toast"]!["body"]!.GetValue<string>()), "the toast");
                var again = await Save("Anthropic chats", "provider=anthropic&source=chat");
                Equal((200, "existing", 4), (again.Status, again.Body!["outcome"]!.GetValue<string>(), again.Body["filters"]!.AsArray().Count), "the same filter again is the same filter");
                var replaced = await Save("  anthropic   CHATS ", "status=settled&provider=anthropic");
                Equal((200, "replaced", "Saved filter replaced"), (replaced.Status, replaced.Body!["outcome"]!.GetValue<string>(), replaced.Body["toast"]!["title"]!.GetValue<string>()), "replaced");
                Equal("Uncertain only=status=uncertain|Settled at an estimate=status=estimate|Chats only=source=chat|anthropic CHATS=provider=anthropic&status=settled",
                    Names(replaced.Body), "in its place, under the new spelling");
                foreach (var (name, query, why) in new[] { ("", "status=settled", "a name"), ("Paged", "page=2&size=50", "at least one filter"), (new string('n', 81), "status=settled", "at most 80") })
                {
                    var bad = await Save(name, query);
                    Equal((400, "invalid"), (bad.Status, bad.Body!["code"]!.GetValue<string>()), "refused without " + why + ": " + bad.Text);
                }
                Equal(400, (await Call(HttpMethod.Post, "/api/spend/filters", new JsonObject { ["name"] = 5, ["query"] = "status=settled" })).Status, "a name is text");
                Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/spend/filters", new JsonObject { ["name"] = "Evil", ["query"] = "status=settled" }, origin: "http://evil.example")).Status,
                    "a cross-origin save is refused");

                // The page with those filters: the saved one is marked, Save current filters… is offered with a name suggested from the values.
                var filtered = await Page("/history/spend?provider=anthropic&status=settled");
                menu = Menu(filtered);
                Check(menu.Contains("<a class=\"dropdown-item text-truncate active\" href=\"/history/spend?provider=anthropic&status=settled\" title=\"provider: anthropic · status: settled\" aria-current=\"true\" data-saved-filter=\"anthropic CHATS\">",
                    StringComparison.Ordinal), "the filter the page shows is marked: " + menu);
                Check(menu.Contains("data-bs-target=\"#modal-save-spend-filter\" data-save-filter-open>", StringComparison.Ordinal), "Save current filters… is offered");
                var modal = Sp4Between(filtered, "id=\"modal-save-spend-filter\"", "</form>");
                Check(modal.Contains("data-save-filter data-query=\"?provider=anthropic&status=settled\"", StringComparison.Ordinal), "the modal saves the page's filters: " + modal);
                Check(modal.Contains("value=\"anthropic · settled\"", StringComparison.Ordinal), "a suggested name: " + modal);
                Check(modal.Contains("<span class=\"ps-chip\" data-tone=\"accent\">provider: anthropic</span>", StringComparison.Ordinal)
                    && modal.Contains("<span class=\"ps-chip\" data-tone=\"accent\">status: settled</span>", StringComparison.Ordinal), "the filters it saves");
                Check(!modal.Contains("doesn't move with the calendar", StringComparison.Ordinal), "no date note without dates");
                Check((await Page("/history/spend?from=2026-10-01")).Contains("doesn't move with the calendar", StringComparison.Ordinal), "the date note with dates");

                // Manage saved filters: rename and remove in one save; two with one name are refused and change nothing.
                var managed = await Manage(("Uncertain spend", "status=uncertain"), ("anthropic CHATS", "provider=anthropic&status=settled"));
                Equal(200, managed.Status, "managed: " + managed.Text);
                Equal(("Uncertain spend=status=uncertain|anthropic CHATS=provider=anthropic&status=settled", 2, "2 saved filters. Removed 2."),
                    (Names(managed.Body), managed.Body!["removed"]!.GetValue<int>(), managed.Body["toast"]!["body"]!.GetValue<string>()), "renamed and removed");
                var twice = await Manage(("Same", "status=uncertain"), ("same", "source=chat"));
                Equal((400, "invalid"), (twice.Status, twice.Body!["code"]!.GetValue<string>()), "two called same");
                Check(twice.Body!["error"]!.GetValue<string>().Contains("Two saved filters are called same", StringComparison.Ordinal), "says so: " + twice.Text);
                Equal(400, (await Call(HttpMethod.Post, "/api/spend/filters/manage", new JsonObject())).Status, "the list is required, so a missing one never clears it");
                Equal(400, (await Manage(("Nothing", "page=3"))).Status, "a filter without a value");
                Equal("Uncertain spend=status=uncertain|anthropic CHATS=provider=anthropic&status=settled", Names((await Call(HttpMethod.Get, "/api/spend/filters")).Body),
                    "unchanged by the refusals");
                menu = Menu(await Page("/history/spend"));
                Check(menu.Contains("data-saved-filter=\"Uncertain spend\"", StringComparison.Ordinal) && !menu.Contains("Chats only", StringComparison.Ordinal)
                    && menu.IndexOf("Uncertain spend", StringComparison.Ordinal) < menu.IndexOf("anthropic CHATS", StringComparison.Ordinal), "the page shows the managed list in order: " + menu);

                // Everything removed: the menu says so and Manage is not offered; a new save starts the list again.
                var cleared = await Manage();
                Equal((200, "", "No saved filters left. Removed 2."), (cleared.Status, Names(cleared.Body), cleared.Body!["toast"]!["body"]!.GetValue<string>()), "all removed");
                menu = Menu(await Page("/history/spend"));
                Check(menu.Contains("None saved yet.", StringComparison.Ordinal) && menu.Contains("data-bs-target=\"#modal-manage-spend-filters\" disabled=\"disabled\"", StringComparison.Ordinal),
                    "the empty menu: " + menu);
                Equal("added", (await Save("Playground", "source=playground")).Body!["outcome"]!.GetValue<string>(), "saved after clearing");

                NoSecret(seen.ToString(), "a page or answer", envKey);
                await app.StopAsync();
                Check(!app.Log.Contains("Exception", StringComparison.Ordinal), "no exception in the log: " + app.Log);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var stored = new SettingsStore(new WebAgentDb(path, [CoreMigrations.AppInfo, SettingsStore.Migration])).Get<SpendSavedFilters>();
                Equal("Playground=source=playground", string.Join("|", stored.Filters!.Select(filter => filter.Name + "=" + filter.Query)), "webagent.db holds what the page showed");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),
    ];

    /// <summary>The text of <paramref name="html"/> from <paramref name="start"/> to the next <paramref name="end"/>.</summary>
    private static string Sp4Between(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Check(from >= 0, "the page has " + start);
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        Check(to > from, start + " ends with " + end);
        return html[from..to];
    }

    /// <summary>The account card of <paramref name="accountId"/> on a provider page: from its <c>data-account-card</c> to its test result.</summary>
    private static string Sp4AccountCard(string html, string accountId)
    {
        var start = html.IndexOf($"data-account-card=\"{accountId}\"", StringComparison.Ordinal);
        Check(start >= 0, "the page has the card of " + accountId);
        var end = html.IndexOf("data-account-test-result", start, StringComparison.Ordinal);
        Check(end > start, "the card of " + accountId + " ends");
        return html[start..end];
    }

    /// <summary>The shell rules every page keeps: a doctype, no inline script, no inline handler.</summary>
    private static void Sp4CheckPage(string path, string html)
    {
        Check(html.TrimStart().StartsWith("<!doctype html>", StringComparison.Ordinal), path + ": the doctype comes first");
        foreach (Match script in Regex.Matches(html, "<script\\b[^>]*>"))
            Check(script.Value.Contains("src=", StringComparison.Ordinal), $"{path}: no inline script ({script.Value})");
        Check(!Regex.IsMatch(html, "\\son[a-z]+\\s*=", RegexOptions.IgnoreCase), path + ": no inline event handlers");
        Check(!html.Contains("An unhandled exception", StringComparison.Ordinal), path + ": no error page");
    }

    /// <summary>A fragment (the Resolve modal's content): no inline script, no inline handler, no layout.</summary>
    private static void Sp4CheckFragment(string html)
    {
        Check(!html.Contains("<script", StringComparison.Ordinal) && !Regex.IsMatch(html, "\\son[a-z]+\\s*=", RegexOptions.IgnoreCase), "no script in the fragment");
        Check(!html.Contains("<!doctype", StringComparison.OrdinalIgnoreCase), "a fragment, not a page");
    }

    private static string Sp4Around(string text, string marker)
    {
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? "(" + marker + " not found)" : text.Substring(at, Math.Min(600, text.Length - at));
    }

    /// <summary>SpendPages over <paramref name="catalog"/>, settings in memory, a ledger on a webagent.db under <paramref name="root"/> and no
    /// guard (the confirm view needs none).</summary>
    private static SpendPages Sp4Pages(string root, IModelCatalog catalog)
    {
        Directory.CreateDirectory(root);
        var settings = new EstSettings();
        var db = new WebAgentDb(Path.Join(root, "webagent.db"), [CoreMigrations.AppInfo, SettingsStore.Migration, .. SpendLedgerMigrations.All]);
        db.Migrate();
        var prices = new ModelPrices(catalog, settings);
        var billing = new BillingModes(catalog, settings, prices);
        return new SpendPages(new SpendLedger(db, settings), settings, catalog, billing, prices, new CostEstimator(catalog, settings, billing, prices),
            new PiSharp.WebAgent.Hosting.WebAgentOptions { DataDirectory = root },
            Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()));
    }

    /// <summary>A chat call of exactly <paramref name="inputTokens"/> input tokens with <c>maxTokens</c> <paramref name="maxTokens"/>.</summary>
    private static SpendCall Sp4Call(string provider, string model, int inputTokens, int maxTokens) => new()
    {
        Provider = provider, Model = model, Source = SpendSource.Chat, Run = "chat-1", InputBytes = EstBytes(inputTokens), MaxTokens = maxTokens,
    };

    /// <summary>A metered estimate at $2 input and $10 output per million tokens with this worst case.</summary>
    private static SpendEstimate Sp4Priced(string provider, string model, int inputTokens, int outputTokens, long worst) => new()
    {
        Provider = provider, Model = model, Billing = BillingKind.Metered, Price = new SpendPrice(2, 10), InputTokens = inputTokens, OutputTokens = outputTokens,
        WorstMicros = worst,
    };

    /// <summary>Ask: <paramref name="estimate"/> is over a per-request cap of <paramref name="limit"/> micro-USD for everything metered.</summary>
    private static SpendDecision Sp4Over(SpendEstimate estimate, long limit) => SpendDecision.From(estimate,
    [
        new SpendCause { Kind = SpendCauseKind.Cap, Action = CapAction.Ask, Cap = new SpendCap(1, CapScope.Everything, null, CapPeriod.Request, limit, CapAction.Ask), LeftMicros = limit },
    ]);

    /// <summary>The confirm view's money parts by name (Input, Output, Worst case, ...), read back as micro-USD.</summary>
    private static Dictionary<string, long> Sp4Parts(SpendConfirmView view) => view.Parts.ToDictionary(
        part => part.Key.Split(' ')[0] == "Worst" ? "Worst case" : part.Key.Split(' ')[0],
        part => SpendMoney.TryParseUsd(part.Value.TrimStart('≈', ' '), out var micros) ? micros : throw new InvalidOperationException("not an amount: " + part.Value));

    /// <summary>A metered (or <paramref name="billing"/>) estimate of <paramref name="provider"/>'s model through account acc-1: 12,000 input
    /// and 8,192 output tokens at $2/$10 per million, with this worst case.</summary>
    private static SpendEstimate Sp4Estimate(string provider, long worst, BillingKind billing = BillingKind.Metered) => new()
    {
        Provider = provider,
        Model = provider == "anthropic" ? "claude-sonnet-5-5" : provider + "-model",
        Account = "acc-1",
        Billing = billing,
        Price = new SpendPrice(2, 10),
        InputTokens = 12_000,
        OutputTokens = 8_192,
        WorstMicros = worst,
    };

    private static async Task Sp4Seed(SpendLedger ledger, string op, SpendEstimate estimate, SpendSource source, string run, Func<string, Task> end, string? note = null)
    {
        var reserved = await ledger.ReserveAsync(new SpendRequest { Op = op, Source = source, Run = run, Estimate = estimate, Approved = true, Note = note });
        Check(reserved.Reserved, "seeded " + op);
        await end(op);
    }
}
