using System.Text;
using System.Text.Json.Nodes;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Data;

// Decision 0011 (run W2, WU7): the chat pages, commands and stream over HTTP, against the built app with --faux and a temporary data folder
// whose Settings › Tools working directory is the case's own folder: pages render; a create or send that comes twice makes one chat and one
// message; a fork opens on its own URL; the stream says hello, sends the whole view after the snapshot and then the batch changes; a busy chat
// answers 409; an approval is listed until the first decision (a second one gets 409) and is asked again after a crash.
internal static partial class Program
{
    /// <summary>A started app whose data and working folder are under <c>Root</c>; disposing stops it and removes the folder.</summary>
    private sealed class ChatApp : IAsyncDisposable
    {
        public required string Root { get; init; }

        public required WebAgentApp App { get; set; }

        public string Data => Path.Join(Root, "data");

        public string Work => Path.Join(Root, "work");

        public static async Task<ChatApp> StartAsync(string name)
        {
            var root = Temp(name);
            try
            {
                Directory.CreateDirectory(Path.Join(root, "data"));
                Directory.CreateDirectory(Path.Join(root, "work"));
                var db = new WebAgentDb(Path.Join(root, "data", "webagent.db"), [CoreMigrations.AppInfo, ChatBindings.Migration, SettingsStore.Migration]);
                db.Migrate();
                await new SettingsStore(db).SaveAsync(new ToolsSettings(Path.Join(root, "work"), ToolApprovalMode.Ask, []));
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                return new ChatApp { Root = root, App = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data") }) };
            }
            catch
            {
                DeleteTemp(root);
                throw;
            }
        }

        /// <summary>Kills the app (a crash) and starts it again on the same folder.</summary>
        public async Task RestartAsync()
        {
            await App.DisposeAsync();
            App = await WebAgentApp.StartAsync(Root, new AppStart { DataDirectory = Data });
        }

        public Task<(int Status, JsonNode? Body, string Text, HttpResponseMessage Response)> PostAsync(string path, JsonObject body) =>
            App.SendAsync(HttpMethod.Post, path, body);

        public async Task<string> CreateAsync(string text, string requestId)
        {
            var created = await PostAsync("/api/chats", new JsonObject { ["requestId"] = requestId, ["text"] = text });
            Equal(200, created.Status, "created: " + created.Text);
            return created.Body!["id"]!.GetValue<string>();
        }

        /// <summary>Waits until the chat's current branch has <paramref name="text"/> and the chat is idle; returns the Markdown export.</summary>
        public async Task<string> WaitForAsync(string chatId, string text)
        {
            var markdown = "";
            await WaitUntilAsync(async () =>
            {
                var chat = await App.Get("/api/chats/" + chatId);
                markdown = (await App.Get($"/chat/{chatId}/export.md")).Text;
                return markdown.Contains(text, StringComparison.Ordinal) && chat.Body?["running"]?.GetValue<bool>() == false;
            }, $"chat {chatId} shows {text}", TimeSpan.FromSeconds(30));
            return markdown;
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            DeleteTemp(Root);
        }
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>Reads `data:` frames of an SSE stream.</summary>
    private sealed class SseReader(StreamReader reader)
    {
        public List<JsonObject> Seen { get; } = [];

        public async Task<JsonObject> NextAsync(Func<JsonObject, bool> match, string what, TimeSpan? limit = null)
        {
            using var timeout = new CancellationTokenSource(limit ?? TimeSpan.FromSeconds(30));
            try
            {
                while (true)
                {
                    var line = await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("The stream ended before " + what);
                    if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                    var message = JsonNode.Parse(line[6..])!.AsObject();
                    Seen.Add(message);
                    if (match(message)) return message;
                }
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Timed out waiting for " + what + "; saw " + string.Join(" | ", Seen.Select(frame => frame["sub"] + ":" + frame["frame"]?["type"])));
            }
        }
    }

    internal static IEnumerable<(string, Func<Task>)> ChatHttpCases() =>
    [
        Case("chat.http.pages-create-and-dedupe", async () =>
        {
            await using var chat = await ChatApp.StartAsync("chat-http-pages");
            var home = await chat.App.Get("/");
            Equal(200, home.Status, "home");
            Equal("/chat/new", home.Response.RequestMessage!.RequestUri!.AbsolutePath, "home opens a new chat");
            var fresh = await chat.App.Get("/chat/new");
            Check(fresh.Text.Contains("data-chat-new", StringComparison.Ordinal) && fresh.Text.Contains("What can I help with?", StringComparison.Ordinal), "the new chat page");
            Check(fresh.Text.Contains(chat.Work, StringComparison.Ordinal), "it names the working folder");
            Check(!fresh.Text.Contains("<script>", StringComparison.Ordinal), "no inline script");
            var body = new JsonObject { ["requestId"] = "r1", ["text"] = "hello http" };
            var first = await chat.PostAsync("/api/chats", body);
            var second = await chat.PostAsync("/api/chats", (JsonObject)body.DeepClone());
            Equal(200, first.Status, "created: " + first.Text);
            var id = first.Body!["id"]!.GetValue<string>();
            Equal(id, second.Body!["id"]!.GetValue<string>(), "a retried create returns the same chat");
            Equal("/chat/" + id, first.Body!["url"]!.GetValue<string>(), "its page");
            var markdown = await chat.WaitForAsync(id, "Echo: hello http");
            Equal(1, Count(markdown, "### You"), "one first message: " + markdown);
            Equal(1, (await chat.App.Get("/api/chats")).Body!["items"]!.AsArray().Count, "one chat");
            var page = await chat.App.Get("/chat/" + id);
            Equal(200, page.Status, "the chat page");
            Check(page.Text.Contains("Echo: hello http", StringComparison.Ordinal), "the stored transcript renders");
            Check(page.Text.Contains($"data-stream-key=\"chat/{id}/1\"", StringComparison.Ordinal), "the page names its stream");
            Check(page.Text.Contains("chat-link", StringComparison.Ordinal) && page.Text.Contains("hello http", StringComparison.Ordinal), "the sidebar lists it");
            Equal(404, (await chat.App.Get("/chat/no-such-chat")).Status, "an unknown chat");
            Equal(415, (await chat.App.SendAsync(HttpMethod.Post, "/api/chats", text: "hello", contentType: "text/plain")).Status, "commands are JSON");
            Equal(403, (await chat.App.SendAsync(HttpMethod.Post, "/api/chats", new JsonObject { ["requestId"] = "x", ["text"] = "y" }, origin: "http://evil.example")).Status,
                "no cross-origin command");
            Equal(400, (await chat.PostAsync("/api/chats", new JsonObject { ["text"] = "no id" })).Status, "a create needs a request id");
        }),
        Case("chat.http.send-twice-one-message-and-fork", async () =>
        {
            await using var chat = await ChatApp.StartAsync("chat-http-send");
            var id = await chat.CreateAsync("hello", "r1");
            await chat.WaitForAsync(id, "Echo: hello");
            var send = new JsonObject { ["requestId"] = "m1", ["text"] = "only once" };
            var a = await chat.PostAsync($"/api/chats/{id}/messages", send);
            var b = await chat.PostAsync($"/api/chats/{id}/messages", (JsonObject)send.DeepClone());
            Equal(200, a.Status, "sent: " + a.Text);
            Equal(a.Body!["submissionId"]!.GetValue<long>(), b.Body!["submissionId"]!.GetValue<long>(), "the same submission");
            var markdown = await chat.WaitForAsync(id, "Echo: only once");
            Equal(2, Count(markdown, "### You"), "one more message: " + markdown);
            Equal(1, Count(markdown, "\n\nonly once\n"), "sent once: " + markdown);
            var entry = long.Parse(markdown[(markdown.IndexOf("### Agent (#", StringComparison.Ordinal) + 12)..].Split(')')[0], System.Globalization.CultureInfo.InvariantCulture);
            var fork = await chat.PostAsync($"/api/chats/{id}/fork", new JsonObject { ["requestId"] = "f1", ["entryId"] = entry });
            Equal(200, fork.Status, "forked: " + fork.Text);
            var url = fork.Body!["url"]!.GetValue<string>();
            Check(url.EndsWith("?agent=" + fork.Body!["agentId"]!.GetValue<long>(), StringComparison.Ordinal), "the fork's page: " + url);
            Equal(fork.Body!["agentId"]!.GetValue<long>(), (await chat.PostAsync($"/api/chats/{id}/fork", new JsonObject { ["requestId"] = "f1", ["entryId"] = entry })).Body!["agentId"]!.GetValue<long>(),
                "a retried fork");
            var forkPage = await chat.App.Get(url);
            Equal(200, forkPage.Status, "the fork renders");
            Check(forkPage.Text.Contains("Echo: hello", StringComparison.Ordinal) && !forkPage.Text.Contains("Echo: only once", StringComparison.Ordinal), "the fork's transcript");
            var renamed = await chat.PostAsync($"/api/chats/{id}/rename", new JsonObject { ["title"] = "Renamed" });
            Equal("Renamed", renamed.Body!["title"]!.GetValue<string>(), "renamed");
            Equal(200, (await chat.PostAsync($"/api/chats/{id}/archive", [])).Status, "deleted");
            Equal(404, (await chat.App.Get("/chat/" + id)).Status, "a deleted chat's page");
        }),
        Case("chat.http.stream-hello-snapshot-batch", async () =>
        {
            await using var chat = await ChatApp.StartAsync("chat-http-stream");
            var id = await chat.CreateAsync("hello", "r1");
            await chat.WaitForAsync(id, "Echo: hello");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(chat.App.Base, "/api/stream"));
            using var response = await chat.App.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType, "an event stream");
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(), Encoding.UTF8);
            var sse = new SseReader(reader);
            var hello = await sse.NextAsync(_ => true, "the first frame");
            Equal("hello", hello["sub"]!.GetValue<string>(), "hello first");
            var streamId = hello["frame"]!["streamId"]!.GetValue<string>();
            var key = ChatStreamMux.AgentKey(id, 1);
            var put = await chat.App.SendAsync(HttpMethod.Put, "/api/stream/" + streamId, new JsonObject { ["subs"] = new JsonArray(key, "chats") });
            Equal(200, put.Status, "subscribed: " + put.Text);
            var whole = await sse.NextAsync(frame => frame["sub"]?.GetValue<string>() == key && frame["frame"]?["type"]?.GetValue<string>() == "view", "the view");
            Check(whole["frame"]!["reset"]!.GetValue<bool>(), "the whole view first");
            Check(HasSource(whole["frame"]!.AsObject(), "snapshot"), "after the snapshot");
            Check(Html(whole["frame"]!.AsObject()).Contains("Echo: hello", StringComparison.Ordinal), "the stored answer");
            if (!sse.Seen.Any(frame => frame["sub"]?.GetValue<string>() == "chats"))
                await sse.NextAsync(frame => frame["sub"]?.GetValue<string>() == "chats", "the sidebar list");
            Equal(200, (await chat.PostAsync($"/api/chats/{id}/messages", new JsonObject { ["requestId"] = "m1", ["text"] = "streamed" })).Status, "sent");
            var batch = await sse.NextAsync(frame => frame["sub"]?.GetValue<string>() == key && frame["frame"]?["reset"]?.GetValue<bool>() == false &&
                HasSource(frame["frame"]!.AsObject(), "batch") && Html(frame["frame"]!.AsObject()).Contains("Echo: streamed", StringComparison.Ordinal), "the batch change");
            Check(batch["frame"]!["state"] is JsonObject, "with the state");
            Equal(404, (await chat.App.SendAsync(HttpMethod.Put, "/api/stream/nope", new JsonObject { ["subs"] = new JsonArray(key) })).Status, "a closed stream");
            Equal(400, (await chat.App.SendAsync(HttpMethod.Put, "/api/stream/" + streamId, new JsonObject { ["subs"] = new JsonArray("chat/../1") })).Status, "a bad key");
            Equal(400, (await chat.App.SendAsync(HttpMethod.Put, "/api/stream/" + streamId, new JsonObject { ["subs"] = new JsonArray("chat/unknown/1") })).Status, "an unknown chat");
        }),
        Case("chat.http.approval-busy-and-crash", async () =>
        {
            await using var chat = await ChatApp.StartAsync("chat-http-approval");
            var id = await chat.CreateAsync("hello", "r1");
            await chat.WaitForAsync(id, "Echo: hello");
            Equal(200, (await chat.PostAsync($"/api/chats/{id}/messages", new JsonObject { ["requestId"] = "m1", ["text"] = MarkerCommand })).Status, "sent");
            JsonNode? approvals = null;
            await WaitUntilAsync(async () => (approvals = (await chat.App.Get($"/api/chats/{id}/approvals")).Body!["items"])!.AsArray().Count == 1, "the call asks",
                TimeSpan.FromSeconds(30));
            Equal("bash", approvals![0]!["tool"]!.GetValue<string>(), "bash asks");
            Equal(409, (await chat.PostAsync($"/api/chats/{id}/model", new JsonObject { ["thinking"] = "off" })).Status, "a waiting chat is busy");
            var page = await chat.App.Get("/chat/" + id);
            Check(page.Text.Contains("awaiting-card", StringComparison.Ordinal), "the page shows the prompt");
            await chat.RestartAsync();
            await WaitUntilAsync(async () => (approvals = (await chat.App.Get($"/api/chats/{id}/approvals")).Body!["items"])!.AsArray().Count == 1,
                "the call asks again after the crash", TimeSpan.FromSeconds(45));
            Equal(0, Lines(Path.Join(chat.Work, "marker.txt")).Length, "nothing ran");
            var task = approvals![0]!["taskId"]!.GetValue<long>();
            var decided = await chat.PostAsync($"/api/chats/{id}/approvals/{task}", new JsonObject { ["decision"] = "allow" });
            Equal(200, decided.Status, "decided: " + decided.Text);
            var late = await chat.PostAsync($"/api/chats/{id}/approvals/{task}", new JsonObject { ["decision"] = "deny", ["reason"] = "late" });
            Equal(409, late.Status, "a second decision");
            Equal(ChatErrors.AlreadyDecided, late.Body!["error"]!["code"]!.GetValue<string>(), "already decided");
            Equal(400, (await chat.PostAsync($"/api/chats/{id}/approvals/{task}", new JsonObject { ["decision"] = "maybe" })).Status, "an unknown decision");
            await chat.WaitForAsync(id, "Tool finished: bash");
            Equal(1, Lines(Path.Join(chat.Work, "marker.txt")).Length, "the command ran once");
        }),
    ];
}
