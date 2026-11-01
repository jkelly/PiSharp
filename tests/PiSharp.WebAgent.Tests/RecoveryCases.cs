using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Routing;

// Decision 0011 (run W2, WU10): never lose a run, with the real server process. The built app runs as a child process (WebAgentApp) over a
// case folder, and each case kills it with Process.Kill (its whole process tree, no shutdown: a crash) and starts it again on the same data:
// killed mid-stream (a faux "slow:" answer), the chat ends with exactly one final answer and the cut-off partial as an aborted entry; killed
// mid-bash (a "!" command that writes a marker file, then sleeps), the command ran once and its card says "Not re-run after restart";
// killed while an approval waits, the same call asks again after each restart and runs once when allowed. These run with --faux. The fourth
// case needs accounts, which the faux provider has none of, so it runs the real routing (no --faux) against a loopback fake of OpenAI's
// Responses API (LoopbackProvider, in this process): two API key accounts of one provider, each with the fake as its base URL; the active
// account changes while the chat's request streams, the server is killed and restarted, and the recovered request, the chat's next
// request and a new chat show that a running chat keeps its own account while new chats take the new active one. No request leaves this
// computer, the keys are planted test values sent only in JSON bodies, and nothing is read from the user's own data folder or environment.
internal static partial class Program
{
    /// <summary>The app as a child process over a case folder (<c>data</c> and <c>work</c>), whose Settings › Tools working directory is
    /// <c>work</c>; <see cref="KillAndRestartAsync"/> crashes it and starts it again. Disposing stops it and removes the folder.</summary>
    private sealed class RecoveryApp : IAsyncDisposable
    {
        private readonly AppStart start;

        private RecoveryApp(string root, AppStart start, WebAgentApp app)
        {
            Root = root;
            this.start = start;
            App = app;
        }

        public string Root { get; }

        public string Work => Path.Join(Root, "work");

        public WebAgentApp App { get; private set; }

        /// <summary>Starts the app (<paramref name="faux"/> by default) with Tools working in <c>work</c> under <paramref name="mode"/>.</summary>
        public static async Task<RecoveryApp> StartAsync(string name, ToolApprovalMode mode, bool faux = true)
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
                await new SettingsStore(db).SaveAsync(new ToolsSettings(Path.Join(root, "work"), mode, []));
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var start = new AppStart { DataDirectory = data, Faux = faux };
                return new RecoveryApp(root, start, await WebAgentApp.StartAsync(root, start));
            }
            catch
            {
                DeleteTemp(root);
                throw;
            }
        }

        /// <summary>Kills the app and its process tree (Process.Kill: nothing shuts down, as in a crash) and starts it again on the same data.</summary>
        public async Task KillAndRestartAsync()
        {
            await App.DisposeAsync();
            App = await WebAgentApp.StartAsync(Root, start);
        }

        public Task<(int Status, JsonNode? Body, string Text, HttpResponseMessage Response)> PostAsync(string path, JsonObject body) =>
            App.SendAsync(HttpMethod.Post, path, body);

        /// <summary>Creates a chat in <c>work</c> with its first message; returns its id.</summary>
        public async Task<string> CreateAsync(string text, string requestId, string? provider = null, string? model = null)
        {
            var body = new JsonObject { ["requestId"] = requestId, ["text"] = text, ["cwd"] = Work };
            if (provider is not null) body["provider"] = provider;
            if (model is not null) body["model"] = model;
            var created = await PostAsync("/api/chats", body);
            Equal(200, created.Status, "created: " + created.Text);
            return created.Body!["id"]!.GetValue<string>();
        }

        /// <summary>Whether the chat runs, as the app says.</summary>
        public async Task<bool> RunningAsync(string chatId) => (await App.Get("/api/chats/" + chatId)).Body?["running"]?.GetValue<bool>() == true;

        /// <summary>The chat's page (its current branch's stored transcript).</summary>
        public async Task<string> PageAsync(string chatId)
        {
            var page = await App.Get("/chat/" + chatId);
            Equal(200, page.Status, "the chat page");
            return page.Text;
        }

        /// <summary>Waits until the chat is idle and its page satisfies <paramref name="done"/>; returns the page.</summary>
        public async Task<string> WaitIdleAsync(string chatId, Func<string, bool> done, string what, TimeSpan? limit = null)
        {
            var page = "";
            await WaitUntilAsync(async () => !await RunningAsync(chatId) && done(page = await PageAsync(chatId)), $"chat {chatId}: {what}",
                limit ?? TimeSpan.FromSeconds(60));
            return page;
        }

        /// <summary>The tool calls of the chat waiting for a decision, once there are <paramref name="count"/>.</summary>
        public async Task<JsonArray> ApprovalsAsync(string chatId, int count)
        {
            JsonArray items = [];
            await WaitUntilAsync(async () => (items = (await App.Get($"/api/chats/{chatId}/approvals")).Body!["items"]!.AsArray()).Count == count,
                $"{count} call(s) of chat {chatId} wait for a decision", TimeSpan.FromSeconds(45));
            return items;
        }

        /// <summary>Follows agent <paramref name="agentId"/> of the chat on the app's stream (GET /api/stream, as a tab does) until a view frame's
        /// rendered items satisfy <paramref name="match"/>.</summary>
        public async Task WatchAsync(string chatId, long agentId, Func<string, bool> match, string what)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(App.Base, "/api/stream"));
            using var response = await App.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8);
            var sse = new SseReader(reader);
            var hello = await sse.NextAsync(frame => frame["sub"]?.GetValue<string>() == ChatStreamMux.HelloKey, "the stream's hello");
            var key = ChatStreamMux.AgentKey(chatId, agentId);
            var put = await App.SendAsync(HttpMethod.Put, "/api/stream/" + hello["frame"]!["streamId"]!.GetValue<string>(),
                new JsonObject { ["subs"] = new JsonArray(key) });
            Equal(200, put.Status, "subscribed: " + put.Text);
            await sse.NextAsync(frame => frame["sub"]?.GetValue<string>() == key && frame["frame"]?["type"]?.GetValue<string>() == "view" &&
                match(Html(frame["frame"]!.AsObject())), what, TimeSpan.FromSeconds(30));
        }

        /// <summary>Stores an API key account through <c>POST /api/credentials/accounts</c> (the key only ever travels in this JSON body).</summary>
        public async Task<(string Id, bool BecameActive)> AddKeyAsync(string provider, string key, string label, string baseUrl)
        {
            var added = await PostAsync("/api/credentials/accounts", new JsonObject
            {
                ["provider"] = provider, ["apiKey"] = key, ["label"] = label, ["settings"] = new JsonObject { ["baseUrl"] = baseUrl },
            });
            Equal(200, added.Status, "the key was stored: " + added.Text);
            return (added.Body!["account"]!["id"]!.GetValue<string>(), added.Body!["becameActive"]?.GetValue<bool>() == true);
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            DeleteTemp(Root);
        }
    }

    /// <summary>A fake of OpenAI's Responses API on a loopback port of this process: it records every request (lower-case header names) and
    /// answers <see cref="Answer"/> as a stream of Responses events, a word at a time. The first request <see cref="Hold"/> matches stops
    /// after two words until <see cref="Release"/> (or until the client goes away) and completes <see cref="Held"/>.</summary>
    private sealed class LoopbackProvider : IAsyncDisposable
    {
        public const string Answer = "Hello from the loopback provider";

        private readonly WebApplication app;
        private int holds;

        private LoopbackProvider(WebApplication app) => this.app = app;

        /// <summary>The base URL an account's <c>baseUrl</c> setting names: <c>http://127.0.0.1:{port}/v1</c>.</summary>
        public string BaseUrl { get; private set; } = "";

        public ConcurrentQueue<FakeRequest> Requests { get; } = new();

        public Func<FakeRequest, bool>? Hold { get; set; }

        public TaskCompletionSource<FakeRequest> Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The recorded requests whose body has <paramref name="text"/>.</summary>
        public List<FakeRequest> With(string text) => [.. Requests.Where(request => request.Body?.Contains(text, StringComparison.Ordinal) == true)];

        public static async Task<LoopbackProvider> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var web = builder.Build();
            var provider = new LoopbackProvider(web);
            web.MapPost("/{**path}", (RequestDelegate)provider.ServeAsync);
            await web.StartAsync();
            provider.BaseUrl = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/v1";
            return provider;
        }

        private async Task ServeAsync(HttpContext http)
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(http.RequestAborted);
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var header in http.Request.Headers) headers[header.Key.ToLowerInvariant()] = header.Value.ToString();
            var recorded = new FakeRequest(http.Request.Method, new Uri($"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}"), headers, body);
            Requests.Enqueue(recorded);
            if (http.Request.Path.Value?.EndsWith("/responses", StringComparison.Ordinal) != true)
            {
                http.Response.StatusCode = 404;
                return;
            }
            var hold = Hold?.Invoke(recorded) == true && Interlocked.Increment(ref holds) == 1;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            var sequence = 0;
            async Task SendAsync(string type, JsonObject data)
            {
                data["type"] = type;
                data["sequence_number"] = sequence++;
                await http.Response.WriteAsync($"event: {type}\ndata: {data.ToJsonString()}\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
            }
            JsonObject Part(string text) => new() { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() };
            JsonObject Item(string status, JsonArray content) =>
                new() { ["id"] = "msg_1", ["type"] = "message", ["status"] = status, ["role"] = "assistant", ["content"] = content };
            try
            {
                await SendAsync("response.created", new JsonObject
                {
                    ["response"] = new JsonObject { ["id"] = "resp_1", ["object"] = "response", ["status"] = "in_progress", ["output"] = new JsonArray() },
                });
                await SendAsync("response.output_item.added", new JsonObject { ["output_index"] = 0, ["item"] = Item("in_progress", []) });
                await SendAsync("response.content_part.added", new JsonObject
                {
                    ["item_id"] = "msg_1", ["output_index"] = 0, ["content_index"] = 0, ["part"] = Part(""),
                });
                var words = Answer.Split(' ');
                for (var index = 0; index < words.Length; index++)
                {
                    if (hold && index == 2)
                    {
                        Held.TrySetResult(recorded);
                        await Release.Task.WaitAsync(http.RequestAborted);
                    }
                    await SendAsync("response.output_text.delta", new JsonObject
                    {
                        ["item_id"] = "msg_1", ["output_index"] = 0, ["content_index"] = 0, ["delta"] = index == 0 ? words[index] : " " + words[index],
                    });
                }
                await SendAsync("response.output_text.done", new JsonObject { ["item_id"] = "msg_1", ["output_index"] = 0, ["content_index"] = 0, ["text"] = Answer });
                await SendAsync("response.content_part.done", new JsonObject
                {
                    ["item_id"] = "msg_1", ["output_index"] = 0, ["content_index"] = 0, ["part"] = Part(Answer),
                });
                await SendAsync("response.output_item.done", new JsonObject { ["output_index"] = 0, ["item"] = Item("completed", [Part(Answer)]) });
                await SendAsync("response.completed", new JsonObject
                {
                    ["response"] = new JsonObject
                    {
                        ["id"] = "resp_1", ["object"] = "response", ["status"] = "completed", ["output"] = new JsonArray(),
                        ["usage"] = new JsonObject
                        {
                            ["input_tokens"] = 12, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 }, ["output_tokens"] = 5,
                            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }, ["total_tokens"] = 17,
                        },
                    },
                });
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
                // The app went away mid-stream (it was killed).
            }
        }

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5))) await app.StopAsync(limit.Token);
            await app.DisposeAsync();
        }
    }

    private const string AssistantTag = "<article class=\"msg msg-assistant\"";

    /// <summary>The assistant messages of rendered chat markup, each from its <c>&lt;article&gt;</c> tag to its end tag, in order.</summary>
    private static List<string> AssistantArticles(string html)
    {
        var articles = new List<string>();
        for (var at = html.IndexOf(AssistantTag, StringComparison.Ordinal); at >= 0; at = html.IndexOf(AssistantTag, at + 1, StringComparison.Ordinal))
        {
            var end = html.IndexOf("</article>", at, StringComparison.Ordinal);
            articles.Add(end < 0 ? html[at..] : html[at..(end + "</article>".Length)]);
        }
        return articles;
    }

    /// <summary>The distinct words <c>w001</c>... of a slow answer that <paramref name="html"/> shows.</summary>
    private static int SlowWords(string html) => Regex.Matches(html, @"\bw\d{3}\b").Select(match => match.Value).Distinct(StringComparer.Ordinal).Count();

    internal static IEnumerable<(string, Func<Task>)> RecoveryCases() =>
    [
        Case("recovery.kill-mid-stream-gives-exactly-one-answer", async () =>
        {
            await using var app = await RecoveryApp.StartAsync("recovery-stream", ToolApprovalMode.Ask);
            // The faux model answers a "slow:" message a word every 40 ms, and the app commits progress about every 150 ms: 150 words take
            // about 6 s, so the server is killed well before the end, once a committed partial answer is on the stream.
            var words = Enumerable.Range(1, 150).Select(index => $"w{index:000}").ToList();
            var id = await app.CreateAsync("slow: " + string.Join(' ', words), "r1");
            await app.WatchAsync(id, 1, html => AssistantArticles(html).Any(answer => answer.Contains("w010", StringComparison.Ordinal) &&
                !answer.Contains("w150", StringComparison.Ordinal)), "a committed partial answer");
            await app.KillAndRestartAsync();

            var page = await app.WaitIdleAsync(id, html => AssistantArticles(html).Any(answer => answer.Contains("w150", StringComparison.Ordinal)),
                "the answer after the restart");
            var answers = AssistantArticles(page);
            Equal(2, answers.Count, "the cut-off partial and one answer:\n" + string.Join("\n", answers));
            var (partial, final) = (answers[0], answers[1]);
            Check(partial.Contains("data-stop=\"aborted\"", StringComparison.Ordinal) && partial.Contains("data-failure=\"aborted\"", StringComparison.Ordinal),
                "the partial is an aborted entry: " + partial);
            Check(partial.Contains("data-answered-again", StringComparison.Ordinal), "it says it was answered again below: " + partial);
            Check(SlowWords(partial) is >= 10 and < 150 && partial.Contains("w001", StringComparison.Ordinal), $"the partial is the answer's start ({SlowWords(partial)} words)");
            Check(final.Contains("data-stop=\"stop\"", StringComparison.Ordinal), "the answer after it finished: " + final);
            Equal(150, SlowWords(final), "the answer is whole");
            Check(final.Contains("Answered again after a server restart", StringComparison.Ordinal), "and says it came after the restart");
            Equal(1, answers.Count(answer => answer.Contains("data-stop=\"stop\"", StringComparison.Ordinal)), "exactly one final answer");
            var markdown = (await app.App.Get($"/chat/{id}/export.md")).Text;
            Equal(1, Count(markdown, "### You"), "your message was sent once: " + markdown);
            Check(!await app.RunningAsync(id), "nothing runs");
        }),

        Case("recovery.kill-mid-bash-runs-the-command-once-and-says-not-re-run", async () =>
        {
            // Tools run without asking here, so the command starts at once; the approval cases are web-approval's.
            await using var app = await RecoveryApp.StartAsync("recovery-bash", ToolApprovalMode.Auto);
            var marker = Path.Join(app.Work, "marker.txt");
            var id = await app.CreateAsync("!echo ran >> marker.txt && sleep 60", "r1");
            await WaitUntilAsync(() => Task.FromResult(Lines(marker).Length == 1), "the command wrote its marker", TimeSpan.FromSeconds(45));
            Check(await app.RunningAsync(id), "the command still runs (it sleeps)");
            await app.KillAndRestartAsync();

            var page = await app.WaitIdleAsync(id, html => html.Contains("Tool finished: bash", StringComparison.Ordinal), "the model got the cut-off result");
            Equal(1, Lines(marker).Length, "the command ran once: it was not run again after the restart");
            Has(page, "data-restart=\"interrupted\"", "the tool card says a restart cut it off");
            Has(page, ">Not re-run after restart</span>", "the tool card's chip");
            Has(page, "Output up to the restart", "the partial output");
            Has(page, "Interrupted by restart · continued below", "the answer with the call says the run went on");
            Lacks(page, "data-approval=", "nothing asked");

            // A later restart finds the call settled: nothing runs again.
            await app.KillAndRestartAsync();
            var again = await app.WaitIdleAsync(id, html => html.Contains("Not re-run after restart", StringComparison.Ordinal), "the same transcript");
            Equal(1, Lines(marker).Length, "still once after another restart");
            Equal(page.Split(AssistantTag).Length, again.Split(AssistantTag).Length, "no new answer");
        }),

        Case("recovery.kill-while-an-approval-waits-asks-again-and-runs-once", async () =>
        {
            await using var app = await RecoveryApp.StartAsync("recovery-approval", ToolApprovalMode.Ask);
            var marker = Path.Join(app.Work, "marker.txt");
            var id = await app.CreateAsync("!echo approved >> marker.txt", "r1");
            var waiting = (await app.ApprovalsAsync(id, 1))[0]!;
            var call = waiting["callId"]!.GetValue<string>();
            Equal("bash", waiting["tool"]!.GetValue<string>(), "bash asks");
            Has(await app.PageAsync(id), $"data-call=\"{call}\"", "the page shows the prompt");
            for (var crash = 1; crash <= 2; crash++)
            {
                await app.KillAndRestartAsync();
                var asked = (await app.ApprovalsAsync(id, 1))[0]!;
                Equal(call, asked["callId"]!.GetValue<string>(), $"the same call asks again after crash {crash}");
                Equal("echo approved >> marker.txt", asked["command"]!.GetValue<string>(), "with its command");
                var page = await app.PageAsync(id);
                Has(page, $"data-approval=\"{asked["taskId"]!.GetValue<long>()}\"", $"the page shows the prompt after crash {crash}");
                Has(page, $"data-call=\"{call}\"", "for the same call");
                Equal(0, Lines(marker).Length, "nothing ran while it waited");
                Check(await app.RunningAsync(id), "the chat counts as running while it waits");
            }
            var task = (await app.ApprovalsAsync(id, 1))[0]!["taskId"]!.GetValue<long>();
            var decided = await app.PostAsync($"/api/chats/{id}/approvals/{task}", new JsonObject { ["decision"] = "allow" });
            Equal(200, decided.Status, "allowed: " + decided.Text);
            await app.WaitIdleAsync(id, html => html.Contains("Tool finished: bash", StringComparison.Ordinal), "the command ran");
            Equal(1, Lines(marker).Length, "the command ran once");

            // After the decision and the run, a crash asks nothing and runs nothing again.
            await app.KillAndRestartAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));
            Equal(0, (await app.App.Get($"/api/chats/{id}/approvals")).Body!["items"]!.AsArray().Count, "nothing asks after the restart");
            Check(!await app.RunningAsync(id), "nothing runs");
            Equal(1, Lines(marker).Length, "still once");
        }),

        // Real routing (no --faux): the faux provider has no accounts. Every provider request goes to LoopbackProvider on 127.0.0.1.
        Case("recovery.switching-the-active-account-leaves-a-running-chat-on-its-own", async () =>
        {
            await using var provider = await LoopbackProvider.StartAsync();
            await using var app = await RecoveryApp.StartAsync("recovery-account", ToolApprovalMode.Ask, faux: false);
            var model = ModelOf(new RuntimeModelCatalog(), "openai", "openai-responses", "gpt-4.1");
            var keyA = Planted("sk-proj-a");
            var keyB = Planted("sk-proj-b");
            var (a, aActive) = await app.AddKeyAsync("openai", keyA, "Personal", provider.BaseUrl);
            var (b, bActive) = await app.AddKeyAsync("openai", keyB, "Work", provider.BaseUrl);
            Check(aActive && !bActive, "the first key is the active account; a second key never becomes active by itself");
            provider.Hold = request => request.Body?.Contains("hold-one", StringComparison.Ordinal) == true;

            var one = await app.CreateAsync("hold-one: the first chat", "r1", "openai", model);
            var held = await provider.Held.Task.WaitAsync(TimeSpan.FromSeconds(45));
            Equal("Bearer " + keyA, held.Header("authorization"), "the chat's request uses the account it was created with");
            Check(await app.RunningAsync(one), "the chat is running");
            var activated = await app.PostAsync($"/api/credentials/accounts/{b}/activate", []);
            Equal(200, activated.Status, "B is made active while the chat runs: " + activated.Text);
            var during = (await app.App.Get("/api/chats/" + one)).Body!;
            Equal("Personal", during["account"]?.GetValue<string>(), "the running chat still names its own account");

            // The server dies with the request half answered, after the switch; the restart sends the interrupted request again.
            await app.KillAndRestartAsync();
            await app.WaitIdleAsync(one, html => AssistantArticles(html).Any(answer => answer.Contains(LoopbackProvider.Answer, StringComparison.Ordinal) &&
                answer.Contains("data-stop=\"stop\"", StringComparison.Ordinal)), "the recovered answer");
            var first = provider.With("hold-one");
            Equal(2, first.Count, "the interrupted request and its one recovery");
            Check(first.All(request => request.Header("authorization") == "Bearer " + keyA), "both with the chat's own account A, not the new active B");

            // The running chat's next request is prepared after the switch, and still uses its own account.
            Equal(200, (await app.PostAsync($"/api/chats/{one}/messages", new JsonObject { ["requestId"] = "m2", ["text"] = "after-the-switch" })).Status,
                "sent");
            await app.WaitIdleAsync(one, html => AssistantArticles(html).Count(answer => answer.Contains(LoopbackProvider.Answer, StringComparison.Ordinal)) >= 2,
                "the second answer");
            Equal("Bearer " + keyA, provider.With("after-the-switch").Single().Header("authorization"), "the chat's next request uses A");

            // A new chat takes the new active account.
            var two = await app.CreateAsync("chat-two: a new chat", "r2", "openai", model);
            await app.WaitIdleAsync(two, html => html.Contains(LoopbackProvider.Answer, StringComparison.Ordinal), "the new chat's answer");
            Equal("Bearer " + keyB, provider.With("chat-two").Single().Header("authorization"), "a new chat binds the new active account B");
            Equal("Work", (await app.App.Get("/api/chats/" + two)).Body!["account"]?.GetValue<string>(), "the new chat names B");
            Equal("Personal", (await app.App.Get("/api/chats/" + one)).Body!["account"]?.GetValue<string>(), "the first chat still names A");

            var sent = string.Join("\n", provider.Requests.Select(request =>
                string.Join("\n", request.Headers.Select(pair => pair.Key + ": " + pair.Value)) + "\n" + request.Body));
            Check(!sent.Contains("\"pisharp\"", StringComparison.Ordinal), "no chat metadata reached the provider");
            Check(!sent.Contains(a, StringComparison.Ordinal) && !sent.Contains(b, StringComparison.Ordinal), "no account id reached the provider");
            var shown = await app.PageAsync(one) + await app.PageAsync(two) + (await app.App.Get("/api/chats")).Text +
                (await app.App.Get("/api/credentials/accounts?provider=openai")).Text + app.App.Log;
            NoSecret(shown, "the pages, answers and log", keyA, keyB);
        }),
    ];
}
