using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Chat;

// Decision 0011 (run W2, WU7): AgentViewHub, the shared view feeds: a subscriber gets the whole view first (source "snapshot") and then the
// changes of each commit (source "batch"); two subscribers of one agent share one watch; and no rendered fragment, view state, sidebar frame
// or page carries a credential, while each model request carries the chat's account id and tier.
internal static partial class Program
{
    /// <summary>A view subscriber that keeps its frames.</summary>
    private sealed class FrameSink
    {
        private readonly ConcurrentQueue<JsonObject> frames = new();

        public void Add(JsonObject frame) => frames.Enqueue(frame);

        public IReadOnlyList<JsonObject> Frames => [.. frames];

        public async Task<JsonObject> WaitAsync(Func<JsonObject, bool> match, string what, TimeSpan? limit = null)
        {
            JsonObject? found = null;
            await WaitUntilAsync(() =>
            {
                found = frames.FirstOrDefault(match);
                return Task.FromResult(found is not null);
            }, what, limit ?? TimeSpan.FromSeconds(30));
            return found!;
        }
    }

    private static bool IsView(JsonObject frame, bool reset) => ChatDisplay.Str(frame, "type") == "view" && ChatDisplay.Bool(frame, "reset") == reset;

    private static bool HasSource(JsonObject frame, string source) => frame["source"] is JsonArray sources && sources.Any(item => item?.GetValue<string>() == source);

    private static string Html(JsonObject frame) =>
        string.Concat((frame["items"]?["upsert"] as JsonArray ?? []).Select(item => item?["html"]?.GetValue<string>() ?? ""));

    /// <summary>Accounts whose sign-in holds a secret the pages must never show.</summary>
    private sealed class PlantedAccounts : IChatAccounts
    {
        public const string Secret = "sk-planted-SECRET-0123456789abcdef";

        public static ChatAccount Account { get; } = new("acct-planted", "Personal (planted)");

        public Task<ChatAccount?> BindAsync(string provider, CancellationToken cancellationToken = default) => Task.FromResult<ChatAccount?>(Account);

        public Task<ChatAccount?> FindAsync(string provider, string accountId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ChatAccount?>(accountId == Account.Id ? Account : null);

        /// <summary>The account's key, as a credential store would hold it.</summary>
        public static string KeyFor(string accountId) => accountId == Account.Id ? Secret : "";
    }

    internal static IEnumerable<(string, Func<Task>)> ChatStreamCases() =>
    [
        Case("chat.hub.snapshot-then-batch", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-hub-batch");
            var binding = await chat.CreateAsync("hello", "c1");
            var sink = new FrameSink();
            using (chat.Hub.Subscribe(binding.SessionId, 1, sink.Add))
            {
                var whole = await sink.WaitAsync(frame => IsView(frame, true), "the whole view");
                Check(HasSource(whole, "snapshot"), "the whole view follows the snapshot: " + whole["source"]?.ToJsonString());
                Check(Html(whole).Contains("Echo: hello", StringComparison.Ordinal), "with the stored answer");
                Check(whole["state"]?["running"]?.GetValue<bool>() == false, "idle");
                await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s1", "next one"));
                var batch = await sink.WaitAsync(frame => IsView(frame, false) && HasSource(frame, "batch") && Html(frame).Contains("Echo: next one", StringComparison.Ordinal),
                    "a change with the new answer");
                Check(batch["items"]?["order"] is JsonArray { Count: >= 4 }, "the order lists every item");
                await sink.WaitAsync(frame => IsView(frame, false) && frame["state"]?["running"]?.GetValue<bool>() == false &&
                    frame["seq"]!.GetValue<long>() >= batch["seq"]!.GetValue<long>(), "the run ends");
            }
            await WaitUntilAsync(() => Task.FromResult(chat.Hub.FeedCount == 0), "the feed stops with its last subscriber");
        }),
        Case("chat.hub.two-subscribers-share-one-feed", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-hub-shared");
            var binding = await chat.CreateAsync("hello", "c1");
            var before = chat.Hub.FeedsStarted;
            var a = new FrameSink();
            var b = new FrameSink();
            var first = chat.Hub.Subscribe(binding.SessionId, 1, a.Add);
            await a.WaitAsync(frame => IsView(frame, true), "the first tab's view");
            var second = chat.Hub.Subscribe(binding.SessionId, 1, b.Add);
            var late = await b.WaitAsync(frame => IsView(frame, true), "the second tab's whole view");
            Check(Html(late).Contains("Echo: hello", StringComparison.Ordinal), "a late subscriber gets the whole view");
            Equal(1, chat.Hub.FeedsStarted - before, "one watch for both");
            Equal(1, chat.Hub.FeedCount, "one feed");
            await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s1", "shared"));
            var seenA = await a.WaitAsync(frame => IsView(frame, false) && Html(frame).Contains("Echo: shared", StringComparison.Ordinal), "the first tab sees the answer");
            var seenB = await b.WaitAsync(frame => IsView(frame, false) && Html(frame).Contains("Echo: shared", StringComparison.Ordinal), "the second tab sees it");
            Equal(seenA["seq"]!.GetValue<long>(), seenB["seq"]!.GetValue<long>(), "the same render");
            first.Dispose();
            Equal(1, chat.Hub.FeedCount, "the feed stays for the second tab");
            second.Dispose();
            Equal(0, chat.Hub.FeedCount, "and stops with it");
            var third = new FrameSink();
            using (chat.Hub.Subscribe(binding.SessionId, 1, third.Add))
                await third.WaitAsync(frame => IsView(frame, true), "a new subscriber after the feed stopped");
            Equal(2, chat.Hub.FeedsStarted - before, "a new watch");
        }),
        Case("chat.hub.no-secret-in-fragments", async () =>
        {
            var requests = new ConcurrentQueue<(string? Account, string? Tier)>();
            var models = new FauxDurableModels([FauxDurableModels.Echo, FauxDurableModels.EchoSmall])
            {
                // As WU6's routed models do: the key is looked up server-side from the request's account id and never returned.
                OnRequest = (model, options) =>
                {
                    var account = SessionStreamMetadata.Field(options, "account");
                    _ = PlantedAccounts.KeyFor(account ?? "");
                    requests.Enqueue((account, SessionStreamMetadata.Field(options, "tier")));
                },
            };
            await using var chat = await ChatHarness.StartAsync("chat-hub-secret", models,
                configure: services => services.AddSingleton<IChatAccounts, PlantedAccounts>());
            var created = await chat.Chats.CreateAsync(new ChatCreateRequest("c1", "hello") { Cwd = chat.Work, Tier = "flex" });
            var id = created.Binding.SessionId;
            await chat.IdleAsync(id);
            Equal(PlantedAccounts.Account.Id, created.Binding.AccountId, "the chat is bound to the account it was created with");
            Check(!requests.IsEmpty && requests.All(request => request is { Account: "acct-planted", Tier: "flex" }),
                "every request carried the account id and tier: " + string.Join(",", requests));
            var sink = new FrameSink();
            using var subscription = chat.Hub.Subscribe(id, 1, sink.Add);
            await sink.WaitAsync(frame => IsView(frame, true), "the view");
            await chat.Chats.SendAsync(id, new ChatSendRequest("s1", "!echo shown"));
            var pending = await chat.PendingAsync(id);
            await sink.WaitAsync(frame => Html(frame).Contains("data-approval=", StringComparison.Ordinal) ||
                (frame["panels"]?["pending"]?.GetValue<string>() ?? "").Contains("data-approval=", StringComparison.Ordinal), "the prompt is rendered");
            await chat.Chats.DecideApprovalAsync(id, pending.Request.TaskId, new ApprovalDecision(ApprovalChoice.AllowOnce));
            await chat.IdleAsync(id);
            await sink.WaitAsync(frame => Html(frame).Contains("Tool finished: bash", StringComparison.Ordinal), "the answer is rendered");
            var page = await chat.Chats.OpenAsync(id);
            var renderer = chat.Services.GetRequiredService<PiSharp.Web.Common.FragmentRenderer>();
            var pageHtml = string.Concat(await Task.WhenAll(page.Items.Select(item => renderer.RenderAsync("~/Views/Chat/_ChatItem.cshtml", new ChatItemView(item, page.Context)))));
            var everything = string.Join("\n", sink.Frames.Select(frame => frame.ToJsonString())) + pageHtml +
                chat.Services.GetRequiredService<ChatStreamMux>().ChatsFrame().ToJsonString() + await chat.Chats.ExportMarkdownAsync(id);
            Check(!everything.Contains(PlantedAccounts.Secret, StringComparison.Ordinal), "no credential anywhere");
            Check(!everything.Contains("sk-planted", StringComparison.Ordinal), "not even its prefix");
            Check(pageHtml.Contains(PlantedAccounts.Account.Label, StringComparison.Ordinal), "the account shows by its label");
            Check(sink.Frames.Any(frame => frame["state"]?["account"]?.GetValue<string>() == PlantedAccounts.Account.Label), "the view state names the label");
        }),
        Case("chat.mux.keys", () =>
        {
            Equal("chat/abc-1/2", ChatStreamMux.AgentKey("abc-1", 2), "a key");
            Equal(("abc-1", 2L), ChatStreamMux.ParseAgentKey("chat/abc-1/2")!.Value, "parsed");
            Equal(("a b", 3L), ChatStreamMux.ParseAgentKey(ChatStreamMux.AgentKey("a b", 3))!.Value, "escaped ids round-trip");
            Check(ChatStreamMux.ParseAgentKey("chat/abc/0") is null, "agent ids start at 1");
            Check(ChatStreamMux.ParseAgentKey("chat/abc") is null, "an agent is required");
            Check(ChatStreamMux.ParseAgentKey("device/abc/1") is null, "only chat keys");
        }),
    ];
}
