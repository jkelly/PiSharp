using Microsoft.Extensions.DependencyInjection;
using PiSharp.Durable;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Shell;

// Decision 0011 (run W2, WU7): ChatService on a real durable device with faux models: create and send, a request id that comes twice makes
// one chat and one message, fork and edit-and-resend make branches (a retry makes no second one), a busy chat refuses a model change and a
// compaction with 409, rename, export and delete. W5 live test: a new chat starts on a connected provider, and the model picker offers only
// connected providers (plus the selected one), as Settings › Defaults and Settings › Providers say.
internal static partial class Program
{
    private static int Users(IReadOnlyList<TranscriptItem> items, string text) => items.OfType<UserItem>().Count(item => item.Text == text);

    internal static IEnumerable<(string, Func<Task>)> ChatServiceCases() =>
    [
        Case("chat.service.create-and-send", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-service-create");
            var binding = await chat.CreateAsync("hello there", "c1");
            Equal("hello there", binding.Title, "titled from the first message");
            Equal("faux", binding.Provider, "the default provider");
            Equal("echo", binding.Model, "the first model");
            Equal("off", binding.Thinking, "clamped to the model");
            Equal(chat.Work, chat.Session(binding.SessionId).Cwd, "it works in Settings › Tools' folder");
            Check((await chat.TextAsync(binding.SessionId)).Contains("Echo: hello there", StringComparison.Ordinal), "the first answer");
            var sent = await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s1", "second"));
            Equal(1L, sent.AgentId, "the root agent");
            await chat.IdleAsync(binding.SessionId);
            Check((await chat.TextAsync(binding.SessionId)).Contains("Echo: second", StringComparison.Ordinal), "the second answer");
            var recent = await chat.Chats.ListAsync(10);
            Equal(binding.SessionId, recent.Single().Id, "the sidebar lists it");
            Check(!recent[0].Running, "idle");
            await ThrowsAsync<ChatException>(() => chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s2", "  ")), "an empty message");
            await ThrowsAsync<ChatException>(() => chat.Chats.CreateAsync(new ChatCreateRequest("c9", "x") { Provider = "faux", Model = "nope" }), "an unknown model");
            Equal(404, (await ThrowsAsync<ChatException>(() => chat.Chats.SendAsync("nope", new ChatSendRequest("s3", "x")), "an unknown chat")).Status, "404");
        }),
        Case("chat.service.same-request-id-one-message", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-service-dedupe");
            var first = await chat.Chats.CreateAsync(new ChatCreateRequest("c1", "hello") { Cwd = chat.Work });
            var again = await chat.Chats.CreateAsync(new ChatCreateRequest("c1", "hello") { Cwd = chat.Work });
            Equal(first.Binding.SessionId, again.Binding.SessionId, "one chat");
            Equal(first.SubmissionId, again.SubmissionId, "one first message");
            Equal(1, chat.Bindings.List().Count, "one binding");
            Equal(1, chat.Device.Host.Sessions.Count, "one session");
            await chat.IdleAsync(first.Binding.SessionId);
            var a = await chat.Chats.SendAsync(first.Binding.SessionId, new ChatSendRequest("s1", "again"));
            var b = await chat.Chats.SendAsync(first.Binding.SessionId, new ChatSendRequest("s1", "again"));
            Equal(a.SubmissionId, b.SubmissionId, "the retry names the same submission");
            await chat.IdleAsync(first.Binding.SessionId);
            var items = await chat.ItemsAsync(first.Binding.SessionId);
            Equal(1, Users(items, "hello"), "one first message");
            Equal(1, Users(items, "again"), "one second message");
        }),
        Case("chat.service.fork-and-edit", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-service-fork");
            var binding = await chat.CreateAsync("hello", "c1");
            await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s1", "second"));
            await chat.IdleAsync(binding.SessionId);
            var items = await chat.ItemsAsync(binding.SessionId, 1);
            var firstAnswer = items.OfType<AssistantItem>().First(item => item.Blocks.OfType<TextBlockView>().Any(block => block.Text.Contains("Echo: hello", StringComparison.Ordinal)));
            var fork = await chat.Chats.ForkAsync(binding.SessionId, 1, firstAnswer.Id!.Value, "f1");
            Equal(fork.AgentId, (await chat.Chats.ForkAsync(binding.SessionId, 1, firstAnswer.Id!.Value, "f1")).AgentId, "a retry makes no second fork");
            Equal(fork.AgentId, chat.Bindings.Get(binding.SessionId)!.AgentId, "the chat shows the fork");
            var forked = await chat.TextAsync(binding.SessionId, fork.AgentId);
            Check(forked.Contains("Echo: hello", StringComparison.Ordinal) && !forked.Contains("second", StringComparison.Ordinal), "the fork ends at the entry: " + forked);
            var page = await chat.Chats.OpenAsync(binding.SessionId);
            Equal(fork.AgentId, page.Agent.Id, "the page opens the fork");
            Equal(AgentKind.Fork, page.Agent.Kind, "a fork");

            var second = items.OfType<UserItem>().Single(item => item.Text == "second");
            var edited = await chat.Chats.EditAsync(binding.SessionId, 1, second.Id, "second, edited", "e1");
            Equal(edited.AgentId, (await chat.Chats.EditAsync(binding.SessionId, 1, second.Id, "second, edited", "e1")).AgentId, "a retry makes no second branch");
            await chat.IdleAsync(binding.SessionId);
            var branch = await chat.ItemsAsync(binding.SessionId, edited.AgentId);
            Equal(1, Users(branch, "second, edited"), "the edited message, once");
            Equal(0, Users(branch, "second"), "not the old one");
            Equal(1, Users(branch, "hello"), "the history before it");
            Check(Transcript.ToMarkdown(branch).Contains("Echo: second, edited", StringComparison.Ordinal), "answered");
            Equal(1, Users(await chat.ItemsAsync(binding.SessionId, 1), "second"), "the old branch stays");
            Equal(edited.AgentId, chat.Bindings.Get(binding.SessionId)!.AgentId, "the chat shows the new branch");
            var first = items.OfType<UserItem>().Single(item => item.Text == "hello");
            var fromStart = await chat.Chats.EditAsync(binding.SessionId, 1, first.Id, "hi instead", "e2");
            await chat.IdleAsync(binding.SessionId);
            var restart = await chat.ItemsAsync(binding.SessionId, fromStart.AgentId);
            Equal(1, Users(restart, "hi instead"), "editing the first message starts a fresh branch");
            Equal(0, Users(restart, "hello"), "without the old message");
            await ThrowsAsync<ChatException>(() => chat.Chats.EditAsync(binding.SessionId, 1, firstAnswer.Id!.Value, "x", "e3"), "an answer is not editable");
        }),
        Case("chat.service.busy-refuses-model-change", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-service-busy");
            var binding = await chat.CreateAsync("hello", "c1");
            await chat.Chats.SendAsync(binding.SessionId, new ChatSendRequest("s1", "slow: " + string.Join(' ', Enumerable.Range(0, 150).Select(i => "word" + i))));
            await WaitUntilAsync(async () => (await chat.Session(binding.SessionId).AgentsAsync()).Any(agent => agent.Busy), "the chat runs");
            var refused = await ThrowsAsync<ChatException>(() => chat.Chats.ChangeModelAsync(binding.SessionId, new ChatModelChange { Thinking = "off" }), "a busy model change");
            Equal(409, refused.Status, "409");
            Equal(ChatErrors.Busy, refused.Code, "busy");
            Equal(409, (await ThrowsAsync<ChatException>(() => chat.Chats.CompactAsync(binding.SessionId, null, null, "k1"), "a busy compaction")).Status, "409");
            await chat.Chats.AbortAsync(binding.SessionId);
            await chat.IdleAsync(binding.SessionId);
            var changed = await chat.Chats.ChangeModelAsync(binding.SessionId, new ChatModelChange { Provider = "faux", Model = "echo-thinking", Thinking = "high", Tier = "flex" });
            Equal("echo-thinking", changed.Model, "the model");
            Equal("high", changed.Thinking, "the level");
            Equal("flex", changed.Tier, "the tier");
            var configuration = await chat.Session(binding.SessionId).ConfigurationAsync(changed.AgentId);
            Equal(new ModelRef("faux", "echo-thinking"), configuration.Model, "the agent follows");
            Equal("high", configuration.ThinkingLevel, "and its level");
            var maxed = await chat.Chats.ChangeModelAsync(binding.SessionId, new ChatModelChange { Thinking = "max" });
            Check(maxed.Thinking is "off" or "minimal" or "low" or "medium" or "high", "max is clamped to a level the model and host take: " + maxed.Thinking);
            Equal(400, (await ThrowsAsync<ChatException>(() => chat.Chats.ChangeModelAsync(binding.SessionId, new ChatModelChange { Provider = "faux", Model = "nope" }),
                "an unknown model")).Status, "400");
            Equal(400, (await ThrowsAsync<ChatException>(() => chat.Chats.ChangeModelAsync(binding.SessionId, new ChatModelChange { Tier = "fa st!" }), "a bad tier")).Status, "400");
        }),
        Case("chat.service.rename-export-delete", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-service-manage");
            var binding = await chat.CreateAsync("hello", "c1");
            var renamed = await chat.Chats.RenameAsync(binding.SessionId, "  Planning notes ");
            Equal("Planning notes", renamed.Title, "renamed");
            Equal("Planning notes", chat.Session(binding.SessionId).Name, "the session too");
            var markdown = await chat.Chats.ExportMarkdownAsync(binding.SessionId);
            Check(markdown.StartsWith("# Planning notes", StringComparison.Ordinal) && markdown.Contains("Echo: hello", StringComparison.Ordinal), "exported: " + markdown);
            await chat.Chats.ArchiveAsync(binding.SessionId);
            await chat.Chats.ArchiveAsync(binding.SessionId);
            Equal(0, (await chat.Chats.ListAsync(10)).Count, "gone from the sidebar");
            Equal(404, Throws<ChatException>(() => chat.Chats.Require(binding.SessionId), "a deleted chat").Status, "404");
            Check(chat.Bindings.Get(binding.SessionId)!.IsArchived, "kept as archived");
        }),
        Case("chat.service.new-chat-starts-on-a-connected-provider", async () =>
        {
            // With only an OpenAI key, every page started on the catalog's first provider, which had no account (W5 live test).
            var directory = SetupDirectory([SetupKey()]);
            await using var chat = await ChatHarness.StartAsync("chat-service-connected", configure: services =>
            {
                services.AddSingleton<IModelCatalog>(SetupCatalog());
                services.AddSingleton(directory);
            });
            var page = chat.Chats.NewChat();
            Equal("openai/gpt-x", $"{page.Provider}/{page.Model}", "the first connected provider's first model, not the catalog's first");
            await chat.Settings.SaveAsync(new DefaultsSettings("anthropic", "claude-a"));
            Equal("openai", chat.Chats.NewChat().Provider, "a saved default whose provider isn't connected is passed over");
            await chat.Settings.SaveAsync(new DefaultsSettings("openai", "gpt-plain"));
            Equal("openai/gpt-plain", $"{chat.Chats.NewChat().Provider}/{chat.Chats.NewChat().Model}", "a connected saved default");
            var renderer = chat.Services.GetRequiredService<FragmentRenderer>();
            var picker = await renderer.RenderAsync("~/Views/Shared/_ProviderModelSelect.cshtml", new ProviderModelSelect("mp") { Provider = "openai", Model = "gpt-x" });
            Check(picker.Contains("value=\"openai\"", StringComparison.Ordinal), "the connected provider");
            Check(!picker.Contains("value=\"anthropic\"", StringComparison.Ordinal) && !picker.Contains("value=\"openai-codex\"", StringComparison.Ordinal),
                "no provider without an account");
            var bound = await renderer.RenderAsync("~/Views/Shared/_ProviderModelSelect.cshtml", new ProviderModelSelect("mp") { Provider = "anthropic", Model = "claude-a" });
            Check(bound.Contains("value=\"anthropic\"", StringComparison.Ordinal), "a chat's own provider still shows after it disconnects");
            var listed = await renderer.RenderAsync("~/Views/Shared/_ProviderModelSelect.cshtml",
                new ProviderModelSelect("ovr") { Providers = ["anthropic", "openai-codex"] });
            Check(listed.Contains("value=\"anthropic\"", StringComparison.Ordinal) && listed.Contains("value=\"openai-codex\"", StringComparison.Ordinal) &&
                !listed.Contains("value=\"openai\"", StringComparison.Ordinal), "a caller's own list is kept as it is");
        }),
    ];
}
