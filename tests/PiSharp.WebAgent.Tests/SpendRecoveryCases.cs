using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Spend;

// Decision 0011 (run W3, T3): the Spend Guard when the server dies mid-answer, with the real server process. The built app runs with the
// priced faux provider (--faux-metered: offline, but Metered at a list price and reporting usage) as a child process over a case folder, and
// each case kills its process tree (Process.Kill, no shutdown: a crash) and starts it again on the same data. Killed while a faux "slow:"
// answer streams, the request's reservation was still reserved; after the restart startup recovery has made it uncertain ("stream dropped,
// no usage reported"), the durable engine sends the interrupted request again under the same op, and the guard reuses that row: it is never
// reserved a second time, never settled automatically (not even by the recovered answer's usage), and keeps counting toward the caps until
// the user resolves it in History › Spend. The chat still ends with exactly one answer. A daily Block cap with room for one worst case but
// not two proves both halves: the recovered request is not checked again (a second reservation would not fit), and the next message is
// refused (409 spend_blocked, nothing posted) until the uncertain row is released, after which the same request id posts once and settles
// at its actual cost; one more crash leaves those settled and released rows as they were. Nothing here calls a provider.
internal static partial class Program
{
    /// <summary>The app with <c>--faux-metered</c> as a child process over a case folder (<c>data</c> and <c>work</c>), whose Settings › Tools
    /// working directory is <c>work</c>; <see cref="KillAndRestartAsync"/> crashes it and starts it again. Disposing stops it and removes the
    /// folder.</summary>
    private sealed class MeteredRecoveryApp : IAsyncDisposable
    {
        private readonly AppStart start;

        private MeteredRecoveryApp(string root, AppStart start, WebAgentApp app)
        {
            Root = root;
            this.start = start;
            App = app;
        }

        public string Root { get; }

        public string Work => Path.Join(Root, "work");

        public WebAgentApp App { get; private set; }

        public static async Task<MeteredRecoveryApp> StartAsync(string name)
        {
            var root = Temp(name);
            try
            {
                var data = Path.Join(root, "data");
                Directory.CreateDirectory(data);
                Directory.CreateDirectory(Path.Join(root, "work"));
                // Seeded before the first start, so the device never starts in the user's home folder.
                var db = new WebAgentDb(Path.Join(data, "webagent.db"), [CoreMigrations.AppInfo, ChatBindings.Migration, SettingsStore.Migration]);
                db.Migrate();
                await new SettingsStore(db).SaveAsync(new ToolsSettings(Path.Join(root, "work"), ToolApprovalMode.Ask, []));
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var start = new AppStart { DataDirectory = data, Faux = true, Arguments = ["--faux-metered"] };
                return new MeteredRecoveryApp(root, start, await WebAgentApp.StartAsync(root, start));
            }
            catch
            {
                DeleteTemp(root);
                throw;
            }
        }

        /// <summary>Kills the app and its process tree (nothing shuts down, as in a crash) and starts it again on the same data.</summary>
        public async Task KillAndRestartAsync()
        {
            await App.DisposeAsync();
            App = await WebAgentApp.StartAsync(Root, start);
        }

        public Task<(int Status, JsonNode? Body, string Text, HttpResponseMessage Response)> PostAsync(string path, JsonObject body) =>
            App.SendAsync(HttpMethod.Post, path, body);

        /// <summary>Creates a chat in <c>work</c> with its first message (the faux model, as the chat's default); returns its id.</summary>
        public async Task<string> CreateAsync(string text, string requestId)
        {
            var created = await PostAsync("/api/chats", new JsonObject { ["requestId"] = requestId, ["text"] = text, ["cwd"] = Work });
            Equal(200, created.Status, "created: " + created.Text);
            return created.Body!["id"]!.GetValue<string>();
        }

        public async Task<bool> RunningAsync(string chatId) => (await App.Get("/api/chats/" + chatId)).Body?["running"]?.GetValue<bool>() == true;

        public async Task<string> PageAsync(string chatId)
        {
            var page = await App.Get("/chat/" + chatId);
            Equal(200, page.Status, "the chat page");
            return page.Text;
        }

        public async Task<string> WaitIdleAsync(string chatId, Func<string, bool> done, string what)
        {
            var page = "";
            await WaitUntilAsync(async () => !await RunningAsync(chatId) && done(page = await PageAsync(chatId)), $"chat {chatId}: {what}", TimeSpan.FromSeconds(60));
            return page;
        }

        /// <summary>Follows agent 1 of the chat on the app's stream until a view frame's rendered items satisfy <paramref name="match"/>.</summary>
        public async Task WatchAsync(string chatId, Func<string, bool> match, string what)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(App.Base, "/api/stream"));
            using var response = await App.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8);
            var sse = new SseReader(reader);
            var hello = await sse.NextAsync(frame => frame["sub"]?.GetValue<string>() == ChatStreamMux.HelloKey, "the stream's hello");
            var key = ChatStreamMux.AgentKey(chatId, 1);
            var put = await App.SendAsync(HttpMethod.Put, "/api/stream/" + hello["frame"]!["streamId"]!.GetValue<string>(),
                new JsonObject { ["subs"] = new JsonArray(key) });
            Equal(200, put.Status, "subscribed: " + put.Text);
            await sse.NextAsync(frame => frame["sub"]?.GetValue<string>() == key && frame["frame"]?["type"]?.GetValue<string>() == "view" &&
                match(Html(frame["frame"]!.AsObject())), what, TimeSpan.FromSeconds(30));
        }

        /// <summary>Every ledger row (History › Spend's JSON, <c>GET /api/spend/ledger</c>), newest first, and how many the ledger holds.</summary>
        public async Task<(List<JsonObject> Rows, int TotalAll)> LedgerAsync()
        {
            var ledger = await App.Get("/api/spend/ledger?size=100");
            Equal(200, ledger.Status, "the ledger: " + ledger.Text);
            return ([.. ledger.Body!["items"]!.AsArray().Select(item => item!.AsObject())], ledger.Body!["totalAll"]!.GetValue<int>());
        }

        /// <summary>The ledger rows of the chat's requests (run = the chat id).</summary>
        public async Task<List<JsonObject>> RowsAsync(string chatId) =>
            [.. (await LedgerAsync()).Rows.Where(item => LedgerText(item, "run") == chatId)];

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteTemp(Root);
        }
    }

    /// <summary>A text field of a ledger item's reservation (<c>op</c>, <c>state</c>, <c>reason</c>, ...), or null.</summary>
    private static string? LedgerText(JsonObject item, string name) => item["reservation"]?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>A whole-number field of a ledger item's reservation (<c>reservedMicros</c>, <c>actualMicros</c>), or null.</summary>
    private static long? LedgerMicros(JsonObject item, string name) => item["reservation"]?[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;

    /// <summary>US dollars for the cap API: <paramref name="micros"/> as <c>0.186432</c>.</summary>
    private static string CapUsd(long micros) => (micros / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);

    internal static IEnumerable<(string, Func<Task>)> SpendRecoveryCases() =>
    [
        Case("spend.recovery.kill-mid-stream-leaves-the-reservation-uncertain-and-answers-once", async () =>
        {
            await using var app = await MeteredRecoveryApp.StartAsync("spend-recovery-stream");
            Check((await app.App.Get("/api/health")).Body?["fauxMetered"]?.GetValue<bool>() == true, "the app runs the priced faux provider");

            // 150 words, one every 40 ms: about 6 s, so the server is killed well before the end.
            var words = Enumerable.Range(1, 150).Select(index => $"w{index:000}").ToList();
            var id = await app.CreateAsync("slow: " + string.Join(' ', words), "r1");
            JsonObject reserved = null!;
            await WaitUntilAsync(async () => (await app.RowsAsync(id)) is [var only] && LedgerText(reserved = only, "state") == "reserved",
                "the request's worst case is reserved before it is sent", TimeSpan.FromSeconds(30));
            var op = LedgerText(reserved, "op")!;
            var worst = LedgerMicros(reserved, "reservedMicros") ?? 0;
            Check(worst > 0, "a metered request reserves its worst case: " + reserved.ToJsonString());
            Equal("metered", LedgerText(reserved, "billing"), "the priced faux provider is Metered");
            Equal("chat", LedgerText(reserved, "source"), "a chat's request");

            // Room for one worst case, not two: added while the request runs, so it decides only what is reserved from now on.
            var capped = await app.PostAsync("/api/spend/caps", new JsonObject
            {
                ["scope"] = "everything", ["period"] = "day", ["limit"] = CapUsd(worst * 3 / 2), ["action"] = "block",
            });
            Equal(200, capped.Status, "the daily cap: " + capped.Text);

            await app.WatchAsync(id, html => AssistantArticles(html).Any(answer => answer.Contains("w010", StringComparison.Ordinal) &&
                !answer.Contains("w150", StringComparison.Ordinal)), "a committed partial answer");
            Equal("reserved", LedgerText((await app.RowsAsync(id)).Single(), "state"), "the request is still running when the server dies");
            await app.KillAndRestartAsync();

            // Startup recovery runs before anything else touches the ledger.
            var recovered = (await app.RowsAsync(id)).Single();
            Equal(op, LedgerText(recovered, "op"), "the same row");
            Equal("uncertain", LedgerText(recovered, "state"), "a reservation the dead process left reserved is uncertain after the restart");
            Equal(SpendLedger.RecoveryReason, LedgerText(recovered, "reason"), "with the reason");

            var page = await app.WaitIdleAsync(id, html => AssistantArticles(html).Any(answer => answer.Contains("w150", StringComparison.Ordinal)),
                "the answer after the restart");
            var answers = AssistantArticles(page);
            Equal(2, answers.Count, "the cut-off partial and one answer:\n" + string.Join("\n", answers));
            Check(answers[0].Contains("data-stop=\"aborted\"", StringComparison.Ordinal), "the partial is an aborted entry: " + answers[0]);
            Check(answers[1].Contains("data-stop=\"stop\"", StringComparison.Ordinal) && SlowWords(answers[1]) == 150, "the whole answer, once: " + answers[1]);
            Equal(1, answers.Count(answer => answer.Contains("data-stop=\"stop\"", StringComparison.Ordinal)), "exactly one final answer");
            Equal(1, Count((await app.App.Get($"/chat/{id}/export.md")).Text, "### You"), "your message was sent once");

            // The recovered request reused its reservation: one row in the whole ledger, still uncertain, though the faux answer reported usage.
            var (all, total) = await app.LedgerAsync();
            Equal(1, total, "the recovered request reserved nothing new: " + string.Join("\n", all.Select(item => item.ToJsonString())));
            var after = all.Single();
            Equal(op, LedgerText(after, "op"), "the row is the request's own op (no op~2)");
            Equal("uncertain", LedgerText(after, "state"), "never settled automatically, not even by the recovered answer's usage");
            Check(LedgerMicros(after, "actualMicros") is null && LedgerText(after, "settlement") is null, "no actual amount: " + after.ToJsonString());
            Check(after["reservation"]?["resolvedByUser"]?.GetValue<bool>() == false, "nobody resolved it");
            Equal(worst, after["countedMicros"]!.GetValue<long>(), "it keeps counting at the reserved amount");

            // It still counts toward the daily cap: the next message does not fit, and nothing is posted.
            var send = new JsonObject { ["requestId"] = "m2", ["text"] = "after the crash", ["spend"] = new JsonObject { ["check"] = true } };
            var refused = await app.PostAsync($"/api/chats/{id}/messages", (JsonObject)send.DeepClone());
            Equal(409, refused.Status, "over the daily cap with the uncertain spend counted: " + refused.Text);
            Equal(SpendErrors.Blocked, refused.Body?["error"]?["code"]?.GetValue<string>(), "a Block cap");
            Equal(1, Count((await app.App.Get($"/chat/{id}/export.md")).Text, "### You"), "nothing was posted");
            Equal(1, (await app.LedgerAsync()).TotalAll, "and nothing reserved");

            // Another crash changes nothing: an uncertain row waits for the user.
            await app.KillAndRestartAsync();
            var again = (await app.RowsAsync(id)).Single();
            Equal("uncertain", LedgerText(again, "state"), "still uncertain after another restart");
            Equal(SpendLedger.RecoveryReason, LedgerText(again, "reason"), "with the same reason");
            Equal(2, AssistantArticles(await app.WaitIdleAsync(id, _ => true, "the chat after another restart")).Count, "no new answer");

            // History › Spend lists it with Resolve…, and the chat links its footer there.
            var history = await app.App.Get("/history/spend");
            Equal(200, history.Status, "History › Spend");
            Has(history.Text, $"data-spend-row=\"{op}\" data-status=\"uncertain\"", "the uncertain row");
            Has(history.Text, $"data-resolve-op=\"{op}\"", "with Resolve…");
            Has(await app.PageAsync(id), "/history/spend?resolve=" + Uri.EscapeDataString(op), "the chat links the uncertain spend to its Resolve");

            // Released by the user, it stops counting; the refused message (same request id) now fits and posts once.
            var released = await app.PostAsync("/api/spend/resolve", new JsonObject { ["op"] = op, ["choice"] = "release" });
            Equal(200, released.Status, "released: " + released.Text);
            Equal("released", released.Body?["reservation"]?["state"]?.GetValue<string>(), "the row is released");
            Check(released.Body?["reservation"]?["resolvedByUser"]?.GetValue<bool>() == true, "by the user");
            var sent = await app.PostAsync($"/api/chats/{id}/messages", (JsonObject)send.DeepClone());
            Equal(200, sent.Status, "the message fits now: " + sent.Text);
            var final = await app.WaitIdleAsync(id, html => html.Contains("Echo: after the crash", StringComparison.Ordinal), "the next answer");
            Equal(2, Count((await app.App.Get($"/chat/{id}/export.md")).Text, "### You"), "the second message was posted once");
            var rows = await app.RowsAsync(id);
            Equal(2, rows.Count, "one new reservation for the new request: " + string.Join("\n", rows.Select(item => item.ToJsonString())));
            var next = rows.Single(item => LedgerText(item, "op") != op);
            Equal("settled", LedgerText(next, "state"), "the new request settled");
            Equal("actual", LedgerText(next, "settlement"), "at the cost its usage reported");
            Check(LedgerMicros(next, "actualMicros") is > 0 and var actual && actual < LedgerMicros(next, "reservedMicros"), "below its worst case: " + next.ToJsonString());
            Equal("released", LedgerText(rows.Single(item => LedgerText(item, "op") == op), "state"), "the old row stays released");
            Check(!final.Contains("data-failure=\"spend\"", StringComparison.Ordinal), "no spend stop in the chat");

            // Recovery touches only rows left reserved: after one more crash the settled row and the released one are as they were.
            await app.KillAndRestartAsync();
            var kept = await app.RowsAsync(id);
            Equal(string.Join("\n", rows.Select(item => item.ToJsonString()).Order(StringComparer.Ordinal)),
                string.Join("\n", kept.Select(item => item.ToJsonString()).Order(StringComparer.Ordinal)), "a restart leaves settled and resolved rows alone");
            Equal(2, Count((await app.App.Get($"/chat/{id}/export.md")).Text, "### You"), "and posts nothing again");
        }),
    ];
}
