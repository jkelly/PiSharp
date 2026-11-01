using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.CustomProviders;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Settings;
using PiSharp.WebAgent.Setup;
using PiSharp.WebAgent.Spend;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

// Decision 0011 (run W3, integration fixes): the fixes after the W3 review, each with the case that would have caught it. The chat sends the
// Spend Guard admitted, and the approvals they stored, are kept in webagent.db (ChatSpendSends, migration 1400) across a restart; the guard
// tags a reserved call's final message with its ledger op (pisharp.spendOp, stored with the answer) and strips the tag from every request
// it forwards, and a footer links to that row exactly even when two answers report the same usage; a recovered request's resend leaves its
// reported usage on the uncertain row, which Resolve can then settle at the reported cost, and every change of a row is announced
// (SpendLedger.Changed) so an open chat refreshes its footers; ProviderDirectory treats a custom provider of models.json as one (its key
// source, Settings › Custom providers, no generic key modal, connected only when routed with a usable key, so the first-run redirect is
// right); the layouts share one head partial; the setup wizard's "Add server" opens the editor once (?add=lmstudio); and a user's message is
// rendered both as typed and as sanitized Markdown, for Settings › Appearance to pick. No case calls a provider or holds a real key.
internal static partial class Program
{
    private static readonly IWebAgentMigration[] SendsMigrations = [CoreMigrations.AppInfo, ChatSpendSends.Migration];

    internal static IEnumerable<(string, Func<Task>)> W3IntegrationFixCases() =>
    [
        Case("w3fix.chat-sends.admitted-ids-and-approvals-survive-a-restart", () =>
        {
            var root = Temp("w3fix-sends");
            try
            {
                var path = Path.Join(root, "webagent.db");
                var clock = new GuardClock(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
                var db = new WebAgentDb(path, SendsMigrations);
                Check(db.Migrate().Contains(1400), "migration 1400 creates spend_chat_sends");
                var sends = new ChatSpendSends(db, clock);
                Check(!sends.IsAdmitted("chat-1/r1"), "nothing admitted yet");
                sends.Admit("chat-1/r1");
                sends.Admit("chat-1/r1");
                sends.Approved("new/r2", 42);
                Equal(2L, sends.Count(), "one row per send");

                var restarted = new ChatSpendSends(new WebAgentDb(path, SendsMigrations), clock);
                Check(restarted.IsAdmitted("chat-1/r1") && restarted.IsAdmitted("new/r2"), "the next run still knows both sends");
                Equal((long?)null, restarted.ApprovalOf("chat-1/r1"), "a send that stored no approval");
                Equal((long?)42, restarted.ApprovalOf("new/r2"), "the approval a send stored");

                clock.Now += ChatSpendSends.Retention + TimeSpan.FromDays(1);
                restarted.Admit("chat-2/r3");
                Check(!restarted.IsAdmitted("chat-1/r1") && restarted.IsAdmitted("chat-2/r3"), "rows older than the retention are pruned as new ones are written");
                Throws<ArgumentException>(() => restarted.Admit(new string('x', ChatSpendSends.MaxKeyLength + 1)), "an over-long key");

                var memory = new ChatSpendSends(null);
                memory.Approved("chat-9/r9", 7);
                Check(memory.IsAdmitted("chat-9/r9") && memory.ApprovalOf("chat-9/r9") == 7, "without a database the sends are kept in memory");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                DeleteTemp(root);
            }
        }),

        Case("w3fix.chat-sends.the-chat-area-registers-its-migration-in-the-spend-range", () =>
        {
            var services = new ServiceCollection();
            services.AddChatArea(new WebAgentOptions { DataDirectory = Temp("w3fix-sends-di") });
            var migrations = services.Where(service => service.ServiceType == typeof(IWebAgentMigration) && service.ImplementationInstance is IWebAgentMigration)
                .Select(service => ((IWebAgentMigration)service.ImplementationInstance!).Id).ToList();
            Check(migrations.Contains(ChatSpendSends.Migration.Id), "ChatModule registers migration 1400: " + string.Join(",", migrations));
            Check(ChatSpendSends.Migration.Id is >= 1400 and <= 1499, "in S5's chat part of the spend range");
            Check(services.Any(service => service.ServiceType == typeof(ChatSpendSends)), "ChatSpendSends is a service");
        }),

        Case("w3fix.guard.tags-the-answer-with-its-ledger-op-and-strips-it-from-requests", async () =>
        {
            await using var world = GuardWorld.Create("w3fix-tag");
            var script = new GuardScript((call, token) => GuardAnswer(call.Model, "hi there", GuardUsage(10, 5)));
            var models = world.Over(script);
            var model = GuardModel("metered", "m");
            var events = await GuardCollect(models.StreamSimple(model, GuardMessages(3000), GuardOptions("op-tag", "chat-t", maxTokens: 1000), CancellationToken.None));
            var done = events[^1] as StreamDone ?? throw new InvalidOperationException("the stream ended with StreamDone");
            Equal("op-tag", TagOf(done.Message), "the final message names its ledger row");
            var wire = JsonNode.Parse(PiWireJson.WriteMessage(done.Message).ToString())!.AsObject();
            Equal("op-tag", GuardedDurableModels.LedgerOpOf(wire), "the tag survives the wire form the durable engine stores");
            Equal("op-tag", ChatEntryMeta.Of(new JsonObject { ["model"] = new JsonArray(wire) })?.SpendOp, "and a stored entry reads it back");

            ImmutableArray<ChordJson> history =
            [
                ChordJson.Object([new("role", "user"), new("content", "first")]),
                ChordJson.Object([new("role", "assistant"), new("content", "hi there"), new(GuardedDurableModels.AnswerField, ChordJson.Object([new(GuardedDurableModels.AnswerOpField, "op-tag")]))]),
                ChordJson.Object([new("role", "user"), new("content", "second")]),
            ];
            await GuardCollect(models.StreamSimple(model, history, GuardOptions("op-next", "chat-t", maxTokens: 1000), CancellationToken.None));
            var sent = script.Calls.Last();
            Equal(3, sent.Messages.Length, "every message is forwarded");
            Check(sent.Messages.All(message => message is not ChordObject item || !item.ContainsKey(GuardedDurableModels.AnswerField)), "no provider sees the tag");
            var completed = await models.CompleteSimpleAsync(model, history, GuardOptions("op-complete", "chat-t", maxTokens: 1000), CancellationToken.None);
            Equal("op-complete", TagOf(completed), "a completion is tagged too");
            Check(script.Calls.Last().Messages.All(message => message is not ChordObject item || !item.ContainsKey(GuardedDurableModels.AnswerField)), "and forwarded untagged");

            var unguarded = await GuardCollect(models.StreamSimple(GuardModel("free", "f"), GuardMessages(100), GuardOptions("op-free", "chat-t"), CancellationToken.None));
            Equal("op-free", TagOf(((StreamDone)unguarded[^1]).Message), "a free call still has its row");
        }),

        Case("w3fix.footer.links-the-tagged-row-even-when-two-answers-report-the-same-usage", () =>
        {
            var first = UiAnswer(2, "One.", input: 100, output: 50, time: UiTime + 1_000);
            var second = UiAnswer(4, "Two.", input: 100, output: 50, time: UiTime + 2_000);
            var orphan = UiAnswer(6, "Three.", input: 100, output: 50, time: UiTime + 3_000);
            TagEntry(first, "op-b");
            TagEntry(second, "op-a");
            TagEntry(orphan, "op-gone");
            JsonObject[] entries = [UiUser(1, "a", UiTime), first, UiUser(3, "b", UiTime + 1_500), second, UiUser(5, "c", UiTime + 2_500), orphan];
            Reservation[] rows =
            [
                SpendRow("op-a", ReservationState.Settled, UiTime + 900, usage: new SpendUsage(100, 50), actual: 1_050, settlement: SettlementKind.Actual),
                SpendRow("op-b", ReservationState.Settled, UiTime + 1_900, usage: new SpendUsage(100, 50), actual: 1_050, settlement: SettlementKind.Actual),
                SpendRow("op-c", ReservationState.Settled, UiTime + 2_900, usage: new SpendUsage(100, 50), actual: 1_050, settlement: SettlementKind.Actual),
            ];
            var facts = ChatSpendText.Link(entries, rows, running: false, row => ChatSpendText.Mark(row, null));
            Equal("2:op-b,4:op-a", string.Join(",", facts.Marks.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value.Op}")),
                "each tagged answer links its own row (time and usage would pick the other), and one whose row is gone links none");

            var untagged = ChatSpendText.Link([UiAnswer(2, "One.", input: 100, output: 50, time: UiTime + 1_000)], rows, running: false, row => ChatSpendText.Mark(row, null));
            Equal("op-a", untagged.For(2)?.Op, "an answer stored before the tag still links by usage and time");
        }),

        Case("w3fix.ledger.a-resend-reports-usage-on-the-uncertain-row-and-resolve-settles-at-it", async () =>
        {
            await using var world = GuardWorld.Create("w3fix-reported");
            var changes = new ConcurrentQueue<(string Op, ReservationState State)>();
            var model = GuardModel("metered", "m");
            var messages = GuardMessages(3000);
            _ = await world.Executor.ReserveAsync(GuardCall("op-crash", "chat-r", maxTokens: 1000));
            world.Restart();
            world.Ledger.Changed += row => changes.Enqueue((row.Op, row.State));
            var recovered = world.Over(new GuardScript((call, token) => GuardAnswer(call.Model, "answered again", GuardUsage(10, 5))));
            await GuardCollect(recovered.StreamSimple(model, messages, GuardOptions("op-crash", "chat-r", maxTokens: 1000), CancellationToken.None));
            var row = (await world.Ledger.GetAsync("op-crash"))!;
            Equal(ReservationState.Uncertain, row.State, "a recovered request is never settled on its own");
            Equal((10.0, 5.0), (row.Usage?.InputTokens ?? -1, row.Usage?.OutputTokens ?? -1), "the usage its resend reported is kept on the row");
            Equal((long?)105, row.ReportedMicros, "at the row's own price");
            Equal(1, world.Rows().Count, "the resend reused the row");
            Check(changes.Any(change => change is ("op-crash", ReservationState.Uncertain)), "the change was announced");

            var settled = await world.Ledger.ResolveAsync("op-crash", new SpendResolution(ResolveChoice.Reported));
            Equal((ReservationState.Settled, SettlementKind.Actual, 105L, true), (settled.State, settled.Settlement ?? SettlementKind.Entered, settled.ActualMicros ?? -1,
                settled.ResolvedByUser), "settled at the reported cost by the user");
            Check(changes.Any(change => change is ("op-crash", ReservationState.Settled)), "the resolve was announced");
            var after = await world.Ledger.ReportUsageAsync("op-crash", new SpendUsage(1, 1));
            Equal((ReservationState.Settled, 10.0, 105L), (after.State, after.Usage?.InputTokens ?? -1, after.ActualMicros ?? -1), "a settled row takes no more usage");

            var silent = world.Over(new GuardScript((call, token) => GuardAnswer(call.Model, "no usage", null)));
            await GuardCollect(silent.StreamSimple(model, messages, GuardOptions("op-silent", "chat-r", maxTokens: 1000), CancellationToken.None));
            var unreported = (await world.Ledger.GetAsync("op-silent"))!;
            Equal((ReservationState.Uncertain, (long?)null), (unreported.State, unreported.ReportedMicros), "an answer without usage leaves nothing reported");
            await ThrowsAsync<ArgumentException>(() => world.Ledger.ResolveAsync("op-silent", new SpendResolution(ResolveChoice.Reported)), "no reported cost to settle at");
            await ThrowsAsync<ArgumentException>(() => world.Ledger.ReportUsageAsync("op-silent", new SpendUsage(-1, 0)), "negative usage");
        }),

        Case("w3fix.providers.a-custom-provider-shows-its-key-source-and-counts-only-when-usable", async () =>
        {
            await using var host = new CustomHost("w3fix-custom-dir", environment: new Dictionary<string, string> { ["W3FIX_SET_KEY"] = "test-value-not-a-key" });
            var signIns = host.Services.GetRequiredService<ISignInFlows>();
            var credentials = host.Services.GetRequiredService<CredentialsOptions>();
            var directory = new ProviderDirectory(host.Catalog, host.Accounts, signIns, credentials, host.Service);
            var setup = new SetupState(directory, host.Services.GetRequiredService<ISettingsStore>(), host.Catalog, host.Options);
            Check(setup.RedirectsToSetup(), "nothing is connected at first");

            await host.Service.SaveAsync(ProviderDraft("unsetgw", "https://gw-unset.example.test/v1", new CustomProviderKeyDraft { Source = "env", Variable = "W3FIX_UNSET_KEY" }));
            var unset = directory.Find("unsetgw") ?? throw new InvalidOperationException("the custom provider is listed");
            Equal((CustomKeySource.Environment, false, ProviderState.NotConfigured), (unset.Custom?.KeySource ?? CustomKeySource.None, unset.Custom?.VariableSet ?? true, unset.State),
                "a $VAR whose variable is unset is not connected");
            Check(setup.RedirectsToSetup() && !setup.HasConnection(), "so the first run still goes to the wizard");
            Equal("env · W3FIX_UNSET_KEY · not set", unset.Custom!.Chip, "the chip says so");

            await host.Service.SaveAsync(ProviderDraft("lmstudio", "http://localhost:1234/v1"));
            await host.Service.SaveAsync(ProviderDraft("setgw", "https://gw-set.example.test/v1", new CustomProviderKeyDraft { Source = "env", Variable = "W3FIX_SET_KEY" }));
            var key = Planted("sk-w3fix-");
            await host.Service.SaveAsync(ProviderDraft("vaulted", "https://gw-vault.example.test/v1", new CustomProviderKeyDraft { Source = "stored", ApiKey = key }));
            var list = directory.List();
            Equal(list.Count, list.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count(), "each provider once");
            ProviderEntry Get(string id) => list.Single(entry => entry.Id == id);

            var local = Get("lmstudio");
            Equal((CustomKeySource.None, ProviderState.Connected, false, ApiKeyForm.None, BillingKind.Free),
                (local.Custom?.KeySource ?? CustomKeySource.Unsupported, local.State, local.NoCredentials, local.KeyForm, local.Billing), "a local server needs no key");
            Equal("/settings/custom-providers", local.Href, "it is edited in Settings › Custom providers");
            Check(local.Description.Contains("custom provider from models.json", StringComparison.Ordinal) &&
                !local.Description.Contains("Connect with an API key", StringComparison.Ordinal) && !local.Description.Contains("Offline test", StringComparison.Ordinal),
                "it is described as what it is: " + local.Description);
            var set = Get("setgw");
            Equal((CustomKeySource.Environment, true, ProviderState.Environment, "env · W3FIX_SET_KEY"),
                (set.Custom!.KeySource, set.Custom.VariableSet, set.State, set.Custom.Chip), "a set variable connects it");
            var vaulted = Get("vaulted");
            Equal((CustomKeySource.Stored, ProviderState.Connected, "vaulted key", false), (vaulted.Custom!.KeySource, vaulted.State, vaulted.Active?.Label ?? "",
                vaulted.TakesApiKey), "a stored key is its vault account, and the generic key modal is not offered");
            Check(!vaulted.Description.Contains(key, StringComparison.Ordinal) && !(vaulted.Active?.Hint ?? "").Contains(key, StringComparison.Ordinal), "never the key");
            Check(setup.HasConnection() && !setup.RedirectsToSetup(), "a usable custom provider ends the redirect");

            var offline = new ProviderDirectory(host.Catalog, host.Accounts, signIns, new CredentialsOptions { Offline = true }, host.Service);
            Check(offline.List().Where(entry => entry.Custom is not null).All(entry => entry.State == ProviderState.NotConfigured), "with --faux no custom provider is connected");
            Check(offline.Find("lmstudio")!.Description.Contains("With --faux it isn't routed", StringComparison.Ordinal), "and it says why");
            Equal(null, new ProviderDirectory(host.Catalog, host.Accounts, signIns, credentials).Find("unsetgw")?.Custom, "without the area there is no custom provider");
        }),

        Case("w3fix.providers.pages-link-custom-providers-and-note-their-vault-keys", async () =>
        {
            var root = Temp("w3fix-custom-app");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data") });
                var key = Planted("sk-w3fix-app-");
                var saved = await app.SendAsync(HttpMethod.Post, "/api/custom-providers", new JsonObject
                {
                    ["id"] = "vaultedgw", ["name"] = "Vaulted GW", ["baseUrl"] = "https://gw.example.test/v1", ["api"] = "openai-completions",
                    ["key"] = new JsonObject { ["source"] = "stored", ["apiKey"] = key },
                    ["models"] = new JsonArray(new JsonObject { ["id"] = "m1" }),
                }, origin: app.Origin);
                Equal(200, saved.Status, "saved: " + saved.Text);

                var providers = (await app.Get("/settings/providers")).Text;
                var row = System.Text.RegularExpressions.Regex.Match(providers, "<div class=\"provider-row\" data-provider-row=\"vaultedgw\".*?data-custom-provider-link", System.Text.RegularExpressions.RegexOptions.Singleline);
                Check(row.Success, "the custom provider's row");
                Check(row.Value.Contains("href=\"/settings/custom-providers\"", StringComparison.Ordinal), "links to Settings › Custom providers");
                Check(row.Value.Contains("data-custom-key=\"stored\"", StringComparison.Ordinal) && row.Value.Contains("stored key", StringComparison.Ordinal), "shows its key source");
                Check(!row.Value.Contains("data-key-form", StringComparison.Ordinal), "offers no generic key modal");
                Check(row.Value.Contains("https://gw.example.test/v1", StringComparison.Ordinal), "shows its base URL");

                var detail = await app.Get("/settings/providers/vaultedgw");
                Equal(200, detail.Status, "the detail page");
                Check(detail.Text.Contains("data-custom-provider-note", StringComparison.Ordinal) &&
                    System.Net.WebUtility.HtmlDecode(detail.Text).Contains("Custom provider · models.json", StringComparison.Ordinal),
                    "says it is a custom provider");
                Check(!detail.Text.Contains("id=\"danger-zone\"", StringComparison.Ordinal) && !detail.Text.Contains("needs no key or sign-in", StringComparison.Ordinal),
                    "no danger zone and no faux text");

                var accounts = (await app.Get("/settings/accounts")).Text;
                Check(accounts.Contains("data-accounts-custom-keys", StringComparison.Ordinal) && accounts.Contains("data-accounts-custom-key=\"vaultedgw\"", StringComparison.Ordinal),
                    "Accounts notes the custom provider's vault key");
                Check(accounts.Contains("Vaulted GW key", StringComparison.Ordinal) && accounts.Contains("href=\"/settings/custom-providers\"", StringComparison.Ordinal), "by its label, with the link");

                foreach (var text in new[] { providers, detail.Text, accounts, saved.Text, app.Log })
                    Check(!text.Contains(key, StringComparison.Ordinal), "the key never reaches a page, an answer or the log");
            }
            finally
            {
                DeleteTemp(root);
            }
        }),

        Case("w3fix.setup.add-server-opens-the-custom-provider-editor-once", () =>
        {
            var connect = File.ReadAllText(WebProjectFile("Views", "Setup", "_SetupConnect.cshtml"));
            Check(connect.Contains("href=\"/settings/custom-providers?add=lmstudio\" data-setup-add-server", StringComparison.Ordinal), "the wizard links with ?add=lmstudio");
            var script = File.ReadAllText(WebProjectFile("wwwroot", "js", "custom-providers.js"));
            Check(script.Contains("searchParams.get('add')", StringComparison.Ordinal) && script.Contains("searchParams.delete('add')", StringComparison.Ordinal) &&
                script.Contains("history.replaceState", StringComparison.Ordinal), "the page reads ?add= once and drops it from the address");
            Check(System.Text.RegularExpressions.Regex.IsMatch(script, @"Object\.hasOwn\(TEMPLATES, template\)\) return;\s+openEditor\(blankDraft\(template\)\)"),
                "and opens the editor on a known template only");
            Check(!script.Contains("modal=", StringComparison.Ordinal), "not the mockup's ?modal=");
        }),

        Case("w3fix.layouts.share-one-head", async () =>
        {
            foreach (var layout in new[] { WebProjectFile("Views", "Shared", "_Layout.cshtml"), WebProjectFile("Views", "Setup", "_SetupLayout.cshtml") })
            {
                var text = File.ReadAllText(layout);
                Check(text.Contains("<partial name=\"_AppHead\" model=\"title\" />", StringComparison.Ordinal) && text.Contains("<partial name=\"_AppScripts\"", StringComparison.Ordinal),
                    Path.GetFileName(layout) + " renders the shared head and scripts");
                Check(!text.Contains("src=\"~/js/appearance.js\"", StringComparison.Ordinal) && !text.Contains("<partial name=\"_WebCommonHead\"", StringComparison.Ordinal) &&
                    !text.Contains("<meta charset", StringComparison.Ordinal), Path.GetFileName(layout) + " repeats none of it");
            }
            await using var app = await WebAgentApp.StartAsync("w3fix-heads");
            foreach (var path in new[] { "/settings/appearance", "/setup" })
            {
                var html = (await app.Get(path)).Text;
                var head = html[..Math.Max(0, html.IndexOf("</head>", StringComparison.Ordinal))];
                Check(head.Contains("<meta charset=\"utf-8\">", StringComparison.Ordinal) && head.Contains(" · PiSharp Web Agent</title>", StringComparison.Ordinal) &&
                    head.Contains("/js/appearance.js?v=", StringComparison.Ordinal) && head.Contains("/css/site.css?v=", StringComparison.Ordinal), path + ": the shared head");
                Equal(1, System.Text.RegularExpressions.Regex.Matches(html, "/js/site\\.js").Count, path + ": site.js once");
            }
        }),

        Case("w3fix.chat.a-user-message-renders-as-typed-and-as-sanitized-markdown", async () =>
        {
            await using var host = UiHost("w3fix-user-markdown");
            var html = await UiRenderAsync(host, [UiUser(1, "Use **bold**, `code` and <script>alert(1)</script>", UiTime)]);
            Has(html, "data-entry-text data-user-md>Use **bold**, `code` and &lt;script&gt;alert(1)&lt;/script&gt;</div>", "as typed, encoded (what Copy and Edit read)");
            Has(html, "<div class=\"msg-content msg-user-md\"><p>Use <strong>bold</strong>, <code>code</code>", "and as Markdown");
            Check(!html.Contains("<script>alert", StringComparison.Ordinal), "never a script");
            Equal(1, System.Text.RegularExpressions.Regex.Matches(html, "data-entry-text").Count, "one text Copy and Edit read");

            var styles = File.ReadAllText(WebProjectFile("wwwroot", "css", "appearance.css"));
            Check(styles.Contains(".msg-user .msg-content.msg-user-md { display: var(--ap-user-md, none); }", StringComparison.Ordinal), "the Markdown is hidden by default");
            Check(styles.Contains("[data-user-markdown=\"on\"] { --ap-user-md: inline-block; --ap-user-plain: none; }", StringComparison.Ordinal), "and shown by the attribute");
            Check(PiSharp.WebAgent.Appearance.AppearancePrefs.All.Any(pref => pref is { Key: "userMarkdown", Attribute: "data-user-markdown", Default: "off" }),
                "Settings › Appearance offers it, off by default");
        }),
    ];

    private static string? TagOf(AssistantMessage message) =>
        message.ExtraProperties is { } extra && extra.TryGet(GuardedDurableModels.AnswerField, out var value) && value is { Value.ValueKind: JsonValueKind.Object } &&
        value.Value.TryGetProperty(GuardedDurableModels.AnswerOpField, out var op) ? op.GetString() : null;

    private static void TagEntry(JsonObject entry, string op) =>
        ((JsonObject)((JsonArray)entry["model"]!)[0]!)[GuardedDurableModels.AnswerField] = new JsonObject { [GuardedDurableModels.AnswerOpField] = op };
}
