using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Server;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;

// Decision 0011 (run W2, WU8): the chat states and dialogs of the mockups, rendered in process from the real partials. The render cases
// build stored entries as the durable engine writes them (messages with usage, timestamps, stop reasons and errors; tool results with their
// restart diagnostics) and check each state's server-rendered markup: streaming with the live footer, reconnecting, background, waiting for
// approval (inline card and the dialog's data), interrupted by a restart, a dropped stream answered again, a provider error with Retry and
// Details, expired with Retry and its banner, the footer's metrics, cost, tokens/s and account, the notices, the session details, the session
// tree and the kept-running toast. The service cases run the Chat area with faux models (an expired account is a scripted failure, never a
// provider), and the HTTP case checks the built app's chat page has every dialog and no inline script. Browser checks come in WU10.
internal static partial class Program
{
    /// <summary>A fixed message time (Unix milliseconds).</summary>
    private const double UiTime = 1_760_000_000_000;

    private sealed class UiRecentChats(params RecentChat[] chats) : IRecentChats
    {
        public Task<IReadOnlyList<RecentChat>> ListAsync(int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RecentChat>>([.. chats.Take(limit)]);
    }

    private static RenderHost UiHost(string name, params RecentChat[] chats) =>
        RenderHost.Create(name, (services, _) => services.AddSingleton<IRecentChats>(new UiRecentChats(chats)));

    private static JsonObject UiEntry(long id, string kind, JsonObject? data, params JsonObject[] messages)
    {
        var entry = new JsonObject { ["id"] = id, ["kind"] = kind, ["model"] = new JsonArray([.. messages.Select(message => (JsonNode)message)]) };
        if (data is not null) entry["data"] = data;
        // As read from the session database (parsed JSON), so numbers read back as any numeric type.
        return JsonNode.Parse(entry.ToJsonString())!.AsObject();
    }

    private static JsonObject UiText(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject UiUser(long id, string text, double time = UiTime) =>
        UiEntry(id, "pi.user", null, new JsonObject { ["role"] = "user", ["content"] = new JsonArray(UiText(text)), ["timestamp"] = time });

    private static JsonObject UiCall(string id, string name, JsonObject args) =>
        new() { ["type"] = "toolCall", ["id"] = id, ["name"] = name, ["arguments"] = args };

    private static JsonObject UiAnswer(long id, string text, string stop = "stop", string? error = null, double? input = null, double? output = null,
        double cacheRead = 0, double cost = 0, double? durationMs = null, double time = UiTime, string provider = "faux", string model = "echo",
        params JsonObject[] calls)
    {
        var content = new JsonArray();
        if (text.Length > 0) content.Add(UiText(text));
        foreach (var call in calls) content.Add(call);
        var message = new JsonObject
        {
            ["role"] = "assistant", ["content"] = content, ["provider"] = provider, ["model"] = model, ["api"] = "faux-api", ["stopReason"] = stop,
            ["timestamp"] = time,
        };
        if (error is not null) message["errorMessage"] = error;
        if (input is not null || output is not null)
            message["usage"] = new JsonObject
            {
                ["input"] = input ?? 0, ["output"] = output ?? 0, ["cacheRead"] = cacheRead, ["cacheWrite"] = 0,
                ["totalTokens"] = (input ?? 0) + (output ?? 0) + cacheRead, ["cost"] = new JsonObject { ["total"] = cost },
            };
        if (durationMs is not null) message["durationMs"] = durationMs;
        return UiEntry(id, "pi.assistant", null, message);
    }

    private static JsonObject UiResult(long id, string callId, string tool, string text, bool isError = false, string? code = null, double? durationMs = null,
        double time = UiTime)
    {
        var message = new JsonObject
        {
            ["role"] = "toolResult", ["toolCallId"] = callId, ["toolName"] = tool, ["content"] = new JsonArray(UiText(text)), ["isError"] = isError,
            ["timestamp"] = time,
        };
        if (durationMs is not null) message["durationMs"] = durationMs;
        var data = code is null ? null
            : new JsonObject { ["diagnostics"] = new JsonArray(new JsonObject { ["severity"] = "error", ["code"] = code, ["message"] = "cut off by " + code }) };
        return UiEntry(id, "pi.tool-result", data, message);
    }

    private static JsonObject UiLive(string text) =>
        new() { ["role"] = "assistant", ["content"] = new JsonArray(UiText(text)), ["model"] = "echo", ["provider"] = "faux" };

    /// <summary>Renders the transcript of <paramref name="entries"/> (and a live message) with _ChatItem, as a page or the stream does.</summary>
    private static async Task<string> UiRenderAsync(RenderHost host, IReadOnlyList<JsonObject> entries, ChatRunState? run = null, JsonObject? live = null,
        string? account = null, BillingKind billing = BillingKind.Metered, IReadOnlyList<PendingApproval>? approvals = null)
    {
        var items = Transcript.Build(entries, live);
        var context = new ChatRenderContext
        {
            SessionId = "ui-chat",
            AgentId = 1,
            Markdown = host.Services.GetRequiredService<MarkdownRenderer>(),
            AccountLabel = account,
            Provider = "faux",
            Billing = billing,
            Facts = ChatFacts.Build(items, entries, run),
            Results = ChatFacts.ToolResults(entries),
            Approvals = (approvals ?? []).ToDictionary(approval => approval.Request.CallId, StringComparer.Ordinal),
        };
        var html = new StringBuilder();
        foreach (var item in items) html.Append(await host.Renderer.RenderAsync("~/Views/Chat/_ChatItem.cshtml", new ChatItemView(item, context)));
        return html.ToString();
    }

    /// <summary>Whether the markup has <paramref name="part"/> as written, or once decoded (Razor encodes characters such as "·" and "≈" in
    /// expressions as character references, which a browser shows as the characters).</summary>
    private static void Has(string html, string part, string what) =>
        Check(html.Contains(part, StringComparison.Ordinal) || WebUtility.HtmlDecode(html).Contains(part, StringComparison.Ordinal),
            $"{what}: expected <{part}> in\n{html}");

    private static void Lacks(string html, string part, string what) =>
        Check(!html.Contains(part, StringComparison.Ordinal) && !WebUtility.HtmlDecode(html).Contains(part, StringComparison.Ordinal),
            $"{what}: did not expect <{part}> in\n{html}");

    /// <summary>A chat binding for the details panel.</summary>
    private static ChatBinding UiBinding() =>
        new("ui-chat-0001-session", "Refactor provider registry", "faux", "echo", "acct-1", "medium", null, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            null, "r1");

    internal static IEnumerable<(string, Func<Task>)> ChatUiCases() =>
    [
        Case("chat.ui.streaming-background-and-reconnecting", async () =>
        {
            await using var host = UiHost("chat-ui-streaming");
            JsonObject[] entries =
            [
                UiUser(1, "apply it and run the tests", UiTime),
                UiAnswer(2, "Editing.", stop: "toolUse", input: 100, output: 20, durationMs: 900, calls: UiCall("c1", "edit", new JsonObject { ["path"] = "a.cs" })),
                UiResult(3, "c1", "edit", "Applied 1 edit.", durationMs: 11),
            ];
            var html = await UiRenderAsync(host, entries, new ChatRunState(true, true, false, false), UiLive("Running the registry tests now"));
            Has(html, "data-tail=\"true\"", "the live answer is the running turn's tail");
            Has(html, "stream-cursor", "the streaming text carries the cursor");
            Has(html, "data-live-status=\"live\">streaming · turn 2 of agent run<", "the live footer");
            Has(html, "data-live-status=\"reconnecting\">paused while reconnecting · turn 2 of agent run<", "the reconnecting footer (shown by data-stream)");
            Has(html, $"data-run-note><i class=\"bi bi-arrow-repeat\" aria-hidden=\"true\"></i>Kept running while you were away · started {ChatDisplay.Clock(UiTime)} · caught up",
                "the background note (shown by data-background)");
            Has(html, "data-stop=\"toolUse\"", "the earlier turn keeps its stop reason (its footer shows on hover)");
            Has(html, "<span class=\"ps-chip\" data-tone=\"ok\">done</span><span class=\"tool-duration\">11 ms</span>", "the finished tool's chip and duration");
            Has(html, $"<time class=\"msg-time\" datetime=\"{ChatDisplay.Iso(UiTime)}\">{ChatDisplay.Clock(UiTime)}</time>", "the message time");
            var idle = await UiRenderAsync(host, entries);
            Lacks(idle, "data-tail=\"true\"", "nothing is the tail when no run goes on");
            Lacks(idle, "data-run-note", "and no background note");
        }),
        Case("chat.ui.waiting-for-approval-inline-and-dialog", async () =>
        {
            await using var host = UiHost("chat-ui-approval");
            JsonObject[] entries =
            [
                UiUser(1, "run the tests"),
                UiAnswer(2, "", stop: "toolUse", calls: UiCall("call_7Hq2abcdefgh", "bash", new JsonObject { ["command"] = "dotnet test --filter ModelRegistry" })),
            ];
            var approval = new PendingApproval(new ApprovalRequest("ui-chat", 1, 7, "call_7Hq2abcdefgh", "bash",
                new JsonObject { ["command"] = "dotnet test --filter ModelRegistry" }), DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_000_000));
            var html = await UiRenderAsync(host, entries, new ChatRunState(true, false, true, false), approvals: [approval]);
            Has(html, "<span class=\"status-dot waiting\" aria-hidden=\"true\"></span>waiting for approval</span>", "the tool card waits");
            Has(html, "data-approval=\"7\" data-tool=\"bash\" data-call=\"call_7Hq2abcdefgh\" data-agent=\"1\" data-since=\"1760000000000\"",
                "the inline card names its call for the dialog");
            Has(html, "data-approval-detail data-command=\"true\">dotnet test --filter ModelRegistry</pre>", "the command");
            Has(html, "<pre hidden data-approval-raw>{", "the raw arguments for the dialog");
            Has(html, "data-action=\"approval-details\"", "Details and deny reason opens the dialog");
            Has(html, "data-decision=\"allowChat\"", "always allow in this chat");
            Has(html, "data-live-status=\"live\">waiting for approval · turn 1 of agent run<", "the live footer says it waits");
        }),
        Case("chat.ui.interrupted-by-restart", async () =>
        {
            await using var host = UiHost("chat-ui-interrupted");
            var back = UiTime + 95_000;
            JsonObject[] entries =
            [
                UiUser(1, "run the tests"),
                UiAnswer(2, "Running them.", stop: "toolUse", calls: UiCall("c1", "bash", new JsonObject { ["command"] = "dotnet test" })),
                UiResult(3, "c1", "bash", "Passed 12\n<harness>The app restarted while this command ran; it was not run again.</harness>", isError: true,
                    code: "interrupted", durationMs: 14_200, time: back),
                UiAnswer(4, "The run was cut off; 12 passed before it.", input: 10, output: 12, durationMs: 1000),
            ];
            var html = await UiRenderAsync(host, entries);
            Has(html, "Interrupted by restart · continued below", "the run break");
            Has(html, $"was back at {ChatDisplay.Clock(back, seconds: true)}", "when the app was back");
            Has(html, ">Not re-run after restart</span>", "the tool card's chip");
            Has(html, "data-restart=\"interrupted\"", "the card says why");
            Has(html, "Output up to the restart", "the partial output");
            Has(html, "<span class=\"tool-duration\">14.2s</span>", "how long it ran");
            Lacks(html, "&lt;harness&gt;", "the harness note is said by the chip instead");
            Lacks(html, "data-tone=\"danger\">error<", "a restart is not shown as a tool error");
            var abandoned = await UiRenderAsync(host,
                [entries[0], entries[1], UiResult(3, "c1", "bash", "<harness>Not started.</harness>", isError: true, code: "abandoned", time: back), entries[3]]);
            Has(abandoned, ">Not started after restart</span>", "a call that never started");
        }),
        Case("chat.ui.dropped-stream-answered-again-and-stop", async () =>
        {
            await using var host = UiHost("chat-ui-aborted");
            JsonObject[] entries =
            [
                UiUser(1, "check the trace"),
                UiAnswer(2, "All 42 tests pa", stop: "aborted", time: UiTime + 5000),
                UiAnswer(3, "All 42 tests pass.", input: 1072, output: 1872, durationMs: 21_400),
            ];
            var html = await UiRenderAsync(host, entries);
            Has(html, $"Stream dropped at {ChatDisplay.Clock(UiTime + 5000, seconds: true)} · no usage reported · answered again below", "the dropped partial");
            Has(html, "msg-partial", "the partial answer is quieter");
            Has(html, "Answered again after a server restart", "the answer after it");
            Equal(1, Regex.Matches(html, "title=\"Regenerate\"").Count, "only the fresh answer offers Regenerate");
            var stopped = await UiRenderAsync(host, [entries[0], entries[1]]);
            Has(stopped, "Stopped · the partial answer is kept", "a Stop keeps the partial");
            Has(stopped, "data-failure=\"aborted\"", "and says how it ended");
        }),
        Case("chat.ui.provider-error-retry-and-details", async () =>
        {
            await using var host = UiHost("chat-ui-error");
            JsonObject[] entries =
            [
                UiUser(1, "go"),
                UiAnswer(2, "", stop: "error", error: "429 Too Many Requests: usage limit reached", time: UiTime + 1000),
                UiAnswer(3, "", stop: "error", error: "429 Too Many Requests: usage limit reached", time: UiTime + 3000),
            ];
            var html = await UiRenderAsync(host, entries);
            Has(html, "retried automatically below", "the first attempt was retried by the engine");
            Has(html, "data-failure=\"error\"", "the failed answer");
            Has(html, "data-error-detail data-error-model=\"faux/echo\"", "the details carry the model");
            Has(html, $"data-error-time=\"{ChatDisplay.Clock(UiTime + 3000, seconds: true)}\"", "and the time");
            Has(html, "data-error-attempts=\"2\"", "and the attempts");
            Has(html, "Auto-retry gave up after 2 attempts.", "the error says the retries gave up");
            Has(html, "data-action=\"retry\" data-entry=\"3\"", "Retry acts on the failed answer");
            Has(html, "data-bs-target=\"#modal-model-picker\"><i class=\"bi bi-arrow-left-right me-1\"></i>Switch model", "Switch model opens the picker");
            Has(html, "data-action=\"error-details\">Details", "Details opens the dialog");
        }),
        Case("chat.ui.expired-retry-and-banner", async () =>
        {
            await using var host = UiHost("chat-ui-expired");
            var failure = ChatFailures.AccountExpired("openai-codex", "refresh_token_reused");
            Equal(ChatFailureKind.Expired, ChatFailures.Classify("error", failure), "the marker");
            Equal(ChatFailureKind.Expired, ChatFailures.Classify("error", "Error: " + failure), "the marker after words the engine adds");
            Equal(ChatFailureKind.Expired, ChatFailures.Classify("error", "OAuth refresh failed: invalid_grant"), "a refused refresh");
            Equal(ChatFailureKind.Error, ChatFailures.Classify("error", "503 overloaded"), "a provider error");
            Equal(ChatFailureKind.Aborted, ChatFailures.Classify("aborted", null), "a dropped stream");
            Equal(ChatFailureKind.None, ChatFailures.Classify("stop", null), "a finished answer");
            Check(!ChatFailures.Display(failure).Contains("account_expired", StringComparison.Ordinal), "the marker is not shown");
            JsonObject[] entries = [UiUser(1, "go"), UiAnswer(2, "", stop: "error", error: failure, provider: "openai-codex", model: "gpt-6.1-sol")];
            var html = await UiRenderAsync(host, entries, account: "Personal");
            Has(html, "data-failure=\"expired\"", "the expired answer");
            Has(html, "Not sent: sign-in expired.", "it says so");
            Has(html, "data-action=\"retry\" data-entry=\"2\"", "with Retry");
            Has(html, "no usage · nothing counted", "nothing was counted");
            Lacks(html, "account_expired", "the marker stays out of the page");
            Lacks(html, "Switch model", "Retry never switches model or account");
            var banner = await host.Renderer.RenderAsync("~/Views/Chat/_ChatBanners.cshtml",
                new ChatBannersView(false, "openai-codex", "OpenAI Codex", "Personal", ChatFailures.Display(failure)));
            Has(banner, "<strong>OpenAI Codex sign-in expired.</strong>", "the banner");
            Has(banner, "refresh_token_reused", "why");
            Has(banner, "href=\"/settings/providers/openai-codex\">Sign in again</a>", "Sign in again goes to the provider");
            var none = await host.Renderer.RenderAsync("~/Views/Chat/_ChatBanners.cshtml", new ChatBannersView(true));
            Has(none, "No providers configured.", "the no-providers banner");
            Lacks(none, "sign-in expired", "and only that");
            Equal("", (await host.Renderer.RenderAsync("~/Views/Chat/_ChatBanners.cshtml", ChatBannersView.None)).Trim(), "no banner");
        }),
        Case("chat.ui.footer-metrics-cost-and-account", async () =>
        {
            await using var host = UiHost("chat-ui-footer");
            JsonObject[] entries =
            [
                UiUser(1, "go"),
                UiAnswer(2, "Done.", input: 1072, output: 1872, cacheRead: 147_968, cost: 0.0357, durationMs: 21_400),
                UiUser(3, "again"),
                UiAnswer(4, "Done again.", input: 10, output: 0),
                UiAnswer(5, "From another provider.", input: 5, output: 5, durationMs: 500, provider: "anthropic", model: "claude"),
            ];
            var html = await UiRenderAsync(host, entries, account: "Personal");
            Has(html, "data-message-metrics data-output-tokens=\"1872\" data-elapsed-ms=\"21400\"", "the footer's raw numbers");
            Has(html, "<i class=\"bi bi-lightning-charge\"></i>147,968 cached", "cached tokens");
            Has(html, ">$0.0357</span>", "a metered cost");
            Has(html, "<i class=\"bi bi-stopwatch\"></i>21.4s", "the duration");
            Has(html, ">87.5 tokens/s</span>", "tokens per second: 1872 ÷ 21.4 s");
            Has(html, ">— tokens/s</span>", "unknown elapsed time");
            Has(html, "title=\"Available when output-token usage and elapsed message time are known.\"", "and why");
            Has(html, "<span class=\"msg-account\" title=\"Provider account used for this message\">faux · Personal</span>", "the account on hover");
            Equal(2, Regex.Matches(html, "class=\"msg-account\"").Count, "only the chat's own provider's answers name its account");
            var subscription = await UiRenderAsync(host, entries, account: "Personal", billing: BillingKind.Subscription);
            Has(subscription, ">≈ $0.0357 list</span>", "a subscription shows the list price");
            var free = await UiRenderAsync(host, entries, billing: BillingKind.Free);
            Lacks(free, "$0.0357", "a free provider shows no price");
            Lacks(free, "msg-account", "a chat without an account names none");
        }),
        Case("chat.ui.notices-and-session-details", async () =>
        {
            await using var host = UiHost("chat-ui-panels");
            var notices = await host.Renderer.RenderAsync("~/Views/Chat/_ChatNotices.cshtml", new ChatNoticesView(true, "summary <failed>", UiTime, "503 overloaded"));
            Has(notices, "Compacting the context…", "compacting");
            Has(notices, "Compaction failed: summary &lt;failed&gt;", "a failed compaction, encoded");
            Has(notices, $"Retrying at <time datetime=\"{ChatDisplay.Iso(UiTime)}\">{ChatDisplay.Clock(UiTime, true)}</time> after: 503 overloaded", "a retry's wait");
            Equal("", (await host.Renderer.RenderAsync("~/Views/Chat/_ChatNotices.cshtml", new ChatNoticesView(false, null, null, null))).Trim(), "no notices");
            var info = await host.Renderer.RenderAsync("~/Views/Chat/_ChatInfo.cshtml", new ChatInfoView
            {
                Binding = UiBinding(),
                AgentId = 1,
                AgentName = "Main",
                Provider = new CatalogProvider("faux", "Faux", null, [ProviderAuthKind.None], BillingKind.Subscription),
                Model = new CatalogModel("faux", "echo", "Echo") { ContextWindow = 272_000 },
                AccountLabel = "Personal",
                ContextTokens = 41_280,
                Usage = new UsageSummary { Input = 62_140, Output = 4_880, CacheRead = 36_864, Cost = 0.1768 },
                Api = "faux-api",
                ApprovalMode = ToolApprovalMode.Ask,
                Tools = ["read", "bash", "edit", "write"],
                AskTools = ["bash", "edit", "write"],
                Instructions = "You are an expert <coding> assistant.",
                Compaction = new SessionCompaction(true, 16_384, 32_768, 20_000),
                Cwd = "C:\\work",
                SessionPath = "C:\\data\\device\\sessions\\ui\\agent.sqlite",
                UserCount = 2,
                AssistantCount = 2,
                ToolCount = 6,
            });
            Has(info, "data-info=\"contextTokens\">41,280</span>", "the context now");
            Has(info, "/ 272,000 · 15%", "of the window");
            Has(info, "style=\"width:15.2%\"", "the meter");
            Has(info, "on · reserve 16,384", "auto-compaction");
            Has(info, "data-info=\"cost\">≈ $0.1768 list</span>", "the list cost of a subscription");
            Has(info, "data-info=\"api\">faux-api</span>", "the API");
            Has(info, "<span class=\"ps-chip\" data-tone=\"accent\">bash</span>", "the active tools");
            Has(info, "Ask before bash, edit and write", "which ask first");
            Has(info, "You are an expert &lt;coding&gt; assistant.", "the system prompt, encoded");
            Has(info, "2 user · 2 assistant · 6 tool", "the messages");
            Has(info, "title=\"ui-chat-0001-session\">ui-chat-</span>", "the short session id");
        }),
        Case("chat.ui.session-tree", async () =>
        {
            await using var host = UiHost("chat-ui-tree");
            ChatTreeNode Node(long id, long? previous, string role, string text, long agent, int tools = 0) =>
                new() { EntryId = id, PreviousId = previous, Role = role, Text = text, AgentId = agent, Tools = tools, OnCurrentPath = agent == 1 };
            var first = Node(1, null, "user", "Why does <ResolveRequestAuth> return the env key?", 1);
            var answer = Node(2, 1, "assistant", "The order of the last two checks is reversed…", 1, tools: 3);
            var apply = Node(3, 2, "user", "Yes, apply it.", 1);
            var applied = Node(4, 3, "assistant", "Applied the edit.", 1);
            var instead = Node(5, 2, "user", "Instead, log a warning.", 2);
            var warning = Node(6, 5, "assistant", "Adding a warning…", 2);
            first.Children.Add(answer);
            answer.Children.Add(apply);
            answer.Children.Add(instead);
            apply.Children.Add(applied);
            instead.Children.Add(warning);
            applied.LeafOf.Add(1);
            applied.Current = true;
            warning.LeafOf.Add(2);
            warning.Running = true;
            var tree = new ChatTreeView("ui-chat", 1, [first], 2, 1, 6, 4, new Dictionary<long, string> { [1] = "Main", [2] = "Fork #2" });
            var html = await host.Renderer.RenderAsync("~/Views/Chat/_ChatTree.cshtml", tree);
            Has(html, "data-summary=\"2 branches · 1 running · 6 entries · leaf #4\"", "the footer's summary");
            Has(html, "data-tone=\"accent\">current leaf</span>", "the shown branch's leaf");
            Has(html, "<span class=\"status-dot live\" aria-hidden=\"true\"></span>running</span>", "the running branch");
            Has(html, "<span class=\"ps-chip ms-auto\">3 tools</span>", "an answer's tool calls");
            Has(html, "data-tree-node data-entry=\"5\" data-prev=\"2\" data-agent=\"2\" data-role=\"user\"", "an entry forks before or at itself on its branch");
            Has(html, "Why does &lt;ResolveRequestAuth&gt; return the env key?", "previews are text");
            Equal(2, Regex.Matches(html, "class=\"tree-run\"").Count, "one nested run per branch where they split");
            Equal(1, Regex.Matches(html, "<ul>").Count, "a straight run stays flat (no nesting per entry)");
            Has(html, "data-action=\"tree-fork-before\" disabled", "Fork before waits for a selection");
            var empty = await host.Renderer.RenderAsync("~/Views/Chat/_ChatTree.cshtml", new ChatTreeView("ui-chat", 1, [], 1, 0, 0, null, new Dictionary<long, string>()));
            Has(empty, "Nothing to show yet", "an empty tree");
        }),
        Case("chat.ui.kept-running-toast", async () =>
        {
            await using var host = UiHost("chat-ui-toast", new RecentChat("a", "Idle chat", DateTimeOffset.UnixEpoch, false),
                new RecentChat("b", "Busy chat", DateTimeOffset.UnixEpoch, true));
            var view = new ChatKeptRunningView([new RecentChat("a", "Refactor <registry>", DateTimeOffset.UnixEpoch, true),
                new RecentChat("b", "Explain the SSE parser", DateTimeOffset.UnixEpoch, true)], "a");
            var html = await host.Renderer.RenderAsync("~/Views/Chat/_ChatKeptRunning.cshtml", view);
            Has(html, "<template id=\"toast-kept-running-template\" data-kept-running=\"a,b\">", "a template the script shows once");
            Has(html, "2 chats kept running while you were away", "the title");
            Has(html, "Refactor &lt;registry&gt;", "titles are text");
            Has(html, "href=\"/chat/b\"", "another chat links to itself");
            Lacks(html, "href=\"/chat/a\"", "the chat this page shows does not");
            var read = await host.Renderer.RenderAsync("~/Views/Chat/_ChatKeptRunning.cshtml", null);
            Has(read, "1 chat kept running while you were away", "without a model it reads the running chats");
            Has(read, "Busy chat", "the running one");
            Lacks(read, "Idle chat", "not the idle one");
            Equal("", (await host.Renderer.RenderAsync("~/Views/Chat/_ChatKeptRunning.cshtml", new ChatKeptRunningView([], null))).Trim(), "no toast when nothing runs");
        }),
        Case("chat.ui.display-helpers", () =>
        {
            Equal("11 ms", ChatDisplay.Duration(11), "milliseconds");
            Equal("14.2s", ChatDisplay.Duration(14_200), "seconds");
            Equal("2m 05s", ChatDisplay.Duration(125_000), "minutes");
            Equal("87.5 tokens/s", ChatDisplay.OutputRate(1872, 21_400).Text, "the rate");
            Equal("— tokens/s", ChatDisplay.OutputRate(null, 21_400).Text, "unknown output");
            Equal("— tokens/s", ChatDisplay.OutputRate(10, 0).Text, "no elapsed time");
            Equal("$0.0357", ChatDisplay.Money(0.0357), "under a dollar");
            Equal("$12.50", ChatDisplay.Money(12.5), "over a dollar");
            Equal("a b c", ChatDisplay.Preview("  a\n b \n\n c "), "a preview is one line");
            Equal("", ChatDisplay.Clock(null), "an unknown time");
        }),
    ];

    internal static IEnumerable<(string, Func<Task>)> ChatUiServiceCases() =>
    [
        Case("chat.ui.service.expired-then-retry-keeps-account", async () =>
        {
            // As WU6's router does for an account whose sign-in can't be used: the request fails with the marker (an error answer), never
            // on another account. Only the first request fails, so Retry's resend works.
            var models = new ExpiringModels(new FauxDurableModels([FauxDurableModels.Echo, FauxDurableModels.EchoSmall]), "please expire",
                ChatFailures.AccountExpired("faux", "refresh_token_reused"));
            await using var chat = await ChatHarness.StartAsync("chat-ui-expired", models, configure: services => services.AddSingleton<IChatAccounts, PlantedAccounts>());
            var created = await chat.Chats.CreateAsync(new ChatCreateRequest("e1", "please expire") { Cwd = chat.Work });
            var id = created.Binding.SessionId;
            await chat.IdleAsync(id);
            Equal(1, models.Failed, "the first request failed");
            var page = await chat.Chats.OpenAsync(id);
            Check(page.Items.OfType<AssistantItem>().Any(), "the failure is a stored answer");
            var failed = page.Items.OfType<AssistantItem>().Last();
            Equal(ChatFailureKind.Expired, page.Context.FactsOf(failed).Failure, "the answer failed as expired: " + failed.Error);
            Equal("faux", page.Banners.ExpiredProvider, "the banner names the chat's provider");
            Check(page.Banners.Detail?.Contains("refresh_token_reused", StringComparison.Ordinal) == true, "and why");
            var html = await chat.Services.GetRequiredService<FragmentRenderer>().RenderAsync("~/Views/Chat/_ChatItem.cshtml", new ChatItemView(failed, page.Context));
            Has(html, "Not sent: sign-in expired.", "the page shows it");
            Has(html, $"data-action=\"retry\" data-entry=\"{failed.Id}\"", "with Retry");
            var branch = await chat.Chats.RetryAsync(id, null, failed.Id!.Value, "retry-1");
            Equal(branch.AgentId, (await chat.Chats.RetryAsync(id, null, failed.Id.Value, "retry-1")).AgentId, "a retried Retry makes nothing new");
            await chat.IdleAsync(id);
            var binding = chat.Bindings.Get(id)!;
            Equal(branch.AgentId, binding.AgentId, "the chat shows the new branch");
            Equal(created.Binding.AccountId, binding.AccountId, "with the same account");
            Equal(created.Binding.Provider + "/" + created.Binding.Model, binding.Provider + "/" + binding.Model, "and the same model");
            Check((await chat.TextAsync(id)).Contains("Echo: please expire", StringComparison.Ordinal), "the message was sent again: " + await chat.TextAsync(id));
            Equal(1, Regex.Matches(await chat.TextAsync(id), "### You").Count, "once, on the new branch");
            var again = await chat.Chats.OpenAsync(id);
            Equal((string?)null, again.Banners.ExpiredProvider, "the banner goes once an answer works");
            Check((await chat.ItemsAsync(id, 1)).OfType<AssistantItem>().Any(item => ChatFailures.IsExpired(item.Error)), "the failed branch stays");
            await ThrowsAsync<ChatException>(() => chat.Chats.RetryAsync(id, null, 1, "retry-bad"), "only an answer can be retried");
        }),
        Case("chat.ui.service.tree-and-auto-compaction", async () =>
        {
            await using var chat = await ChatHarness.StartAsync("chat-ui-tree-service");
            var binding = await chat.CreateAsync("first question", "t1");
            var id = binding.SessionId;
            var items = await chat.ItemsAsync(id);
            var answer = items.OfType<AssistantItem>().First().Id!.Value;
            var user = items.OfType<UserItem>().First().Id;
            var fork = await chat.Chats.ForkAsync(id, null, user, "f1");
            await chat.Chats.SendAsync(id, new ChatSendRequest("s2", "second question on the fork") { AgentId = fork.AgentId });
            await chat.IdleAsync(id);
            var tree = await chat.Chats.TreeAsync(id, null);
            Equal(2, tree.Branches, "two branches");
            Equal(0, tree.Running, "none running");
            Equal(1, tree.Roots.Count, "one first entry");
            var root = tree.Roots[0];
            Equal(user, root.EntryId, "the shared first message");
            Equal(2, root.Children.Count, "the branches split after it");
            Check(root.Children.Any(child => child.EntryId == answer && child.LeafOf.Contains(1)), "Main's answer is its leaf");
            var current = root.Children.Single(child => child.EntryId != answer);
            Equal("user", current.Role, "the fork's own message");
            Equal(fork.AgentId, current.AgentId, "belongs to the fork");
            Check(current.PreviousId >= user, "and follows the shared message: " + current.PreviousId);
            Check(current.Children.Single() is { Current: true, Role: "assistant" }, "the fork's answer is the current leaf");
            var html = await chat.Services.GetRequiredService<FragmentRenderer>().RenderAsync("~/Views/Chat/_ChatTree.cshtml", tree);
            Has(html, "current leaf", "rendered");
            var main = await chat.Chats.TreeAsync(id, 1);
            Check(root.Children.Count == main.Roots[0].Children.Count && main.Leaf == answer, "the tree of Main has Main's leaf");
            var off = chat.Chats.SetAutoCompaction(id, false);
            Check(!off.Enabled, "auto-compaction off");
            var info = await chat.Chats.InfoAsync(chat.Bindings.Get(id)!, fork.AgentId, [], 0, null, null);
            Check(info.Compaction is { Enabled: false }, "the details say so");
            Check(chat.Chats.SetAutoCompaction(id, true).Enabled, "and on again");
            Check(info.Tools.Count > 0 && info.AskTools.All(tool => info.Tools.Contains(tool)), "the details list the offered tools: " + string.Join(",", info.Tools));
        }),
    ];

    internal static IEnumerable<(string, Func<Task>)> ChatUiHttpCases() =>
    [
        Case("chat.ui.http.pages-dialogs-and-no-inline-script", async () =>
        {
            await using var chat = await ChatApp.StartAsync("chat-ui-http");
            var fresh = await chat.App.Get("/chat/new");
            Has(fresh.Text, "data-suggestion=\"Write tests for \"", "the new chat's suggestions");
            Has(fresh.Text, "href=\"/compare\"", "Compare models");
            Has(fresh.Text, "/js/chat-kept-running.js", "the kept-running toast's script");
            var id = await chat.CreateAsync("hello ui", "u1");
            await chat.WaitForAsync(id, "Echo: hello ui");
            var page = await chat.App.Get("/chat/" + id);
            Equal(200, page.Status, "the chat page");
            foreach (var modal in new[] { "modal-model-picker", "modal-rename-session", "modal-session-tree", "modal-tool-approval", "modal-error-details",
                         "modal-compact", "modal-confirm", "modal-shortcuts", "modal-edit-message" })
                Has(page.Text, $"id=\"{modal}\"", "the dialog " + modal);
            foreach (var panel in new[] { "banners", "notices", "pending", "queue", "info" })
                Has(page.Text, $"data-panel=\"{panel}\"", "the panel " + panel);
            Has(page.Text, "Context window", "the session details");
            Has(page.Text, "data-stream-status", "the reconnecting chip");
            Has(page.Text, "data-bs-target=\"#modal-session-tree\"", "the tree button");
            Has(page.Text, ">/compact <span", "the slash commands");
            Check(!Regex.IsMatch(page.Text, @"<script(?![^>]*\bsrc=)[^>]*>"), "no inline script");
            Check(!Regex.IsMatch(page.Text, @"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase), "no inline handlers");
            var tree = await chat.App.Get($"/chat/{id}/tree");
            Equal(200, tree.Status, "the tree");
            Has(tree.Text, "data-tree-node", "its entries");
            Has(tree.Text, "current leaf", "its leaf");
            Equal(404, (await chat.App.Get("/chat/no-such-chat/tree")).Status, "an unknown chat's tree");
            var compaction = await chat.PostAsync($"/api/chats/{id}/compaction", new JsonObject { ["enabled"] = false });
            Equal(200, compaction.Status, "auto-compaction: " + compaction.Text);
            Check(compaction.Body!["enabled"]!.GetValue<bool>() == false, "turned off");
            Equal(400, (await chat.PostAsync($"/api/chats/{id}/compaction", new JsonObject())).Status, "enabled is required");
            Equal(400, (await chat.PostAsync($"/api/chats/{id}/retry", new JsonObject { ["requestId"] = "x", ["entryId"] = 1 })).Status, "a message of yours is not an answer");
            var answer = Regex.Match(page.Text, "data-action=\"retry\" data-entry=\"(\\d+)\"");
            Check(answer.Success, "the answer offers Regenerate");
            var retried = await chat.PostAsync($"/api/chats/{id}/retry", new JsonObject { ["requestId"] = "rg1", ["entryId"] = long.Parse(answer.Groups[1].Value) });
            Equal(200, retried.Status, "Regenerate: " + retried.Text);
            Check(retried.Body!["url"]!.GetValue<string>().StartsWith($"/chat/{id}?agent=", StringComparison.Ordinal), "it opens the new branch");
            await chat.WaitForAsync(id, "Echo: hello ui");
        }),
    ];
}

/// <summary>Faux models whose first request asking <c>trigger</c> ends as WU6's router ends one on an account that can't be used: an error
/// answer (a <see cref="PiSharp.Contracts.StreamError"/>) carrying <c>error</c>. Every other request goes to the faux models.</summary>
internal sealed class ExpiringModels(FauxDurableModels inner, string trigger, string error) : PiSharp.Durable.IDurableModels
{
    private int failed;

    /// <summary>How many requests failed (one at most).</summary>
    public int Failed => Volatile.Read(ref failed);

    public PiSharp.Durable.DurableModel? GetModel(string provider, string modelId) => inner.GetModel(provider, modelId);

    public System.Collections.Immutable.ImmutableArray<PiSharp.Durable.Chord.JsonObjectValue> GetModelsOfType(string type, string? provider) =>
        inner.GetModelsOfType(type, provider);

    public Task<System.Collections.Immutable.ImmutableArray<PiSharp.Durable.Chord.JsonObjectValue>> GetAvailableOfTypeAsync(string type, string? provider,
        CancellationToken cancellationToken) => inner.GetAvailableOfTypeAsync(type, provider, cancellationToken);

    public IAsyncEnumerable<PiSharp.Contracts.StreamEvent> StreamSimple(PiSharp.Durable.DurableModel model,
        System.Collections.Immutable.ImmutableArray<PiSharp.Durable.Chord.JsonValue> messages, PiSharp.Durable.DurableStreamOptions? options,
        CancellationToken cancellationToken) =>
        messages.Length > 0 && messages[^1].ToJson().Contains(trigger, StringComparison.Ordinal) && Interlocked.CompareExchange(ref failed, 1, 0) == 0
            ? Fail(model) : inner.StreamSimple(model, messages, options, cancellationToken);

    public Task<PiSharp.Contracts.AssistantMessage> CompleteSimpleAsync(PiSharp.Durable.DurableModel model,
        System.Collections.Immutable.ImmutableArray<PiSharp.Durable.Chord.JsonValue> messages, PiSharp.Durable.DurableStreamOptions? options,
        CancellationToken cancellationToken) => inner.CompleteSimpleAsync(model, messages, options, cancellationToken);

    public IAsyncEnumerable<PiSharp.Contracts.StreamEvent> StreamDeferred(PiSharp.Durable.DurableModel model, PiSharp.Durable.DurableDeferredHandle handle,
        CancellationToken cancellationToken) => inner.StreamDeferred(model, handle, cancellationToken);

    public Task<PiSharp.Contracts.AssistantMessage> FetchDeferredAsync(PiSharp.Durable.DurableModel model, PiSharp.Durable.DurableDeferredHandle handle,
        CancellationToken cancellationToken) => inner.FetchDeferredAsync(model, handle, cancellationToken);

    public Task CancelDeferredAsync(PiSharp.Durable.DurableModel model, PiSharp.Durable.DurableDeferredHandle handle, CancellationToken cancellationToken) =>
        inner.CancelDeferredAsync(model, handle, cancellationToken);

    private async IAsyncEnumerable<PiSharp.Contracts.StreamEvent> Fail(PiSharp.Durable.DurableModel model)
    {
        await Task.Yield();
        var message = new PiSharp.Contracts.AssistantMessage(model.Api, model.Provider, model.Id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [],
            PiSharp.Contracts.TokenUsage.Zero, PiSharp.Contracts.StopReason.Error,
            PiSharp.Contracts.JsonFields.Empty.Set("errorMessage", PiSharp.Contracts.JsonData.Parse(System.Text.Json.JsonSerializer.Serialize(error))));
        yield return new PiSharp.Contracts.StreamError(PiSharp.Contracts.StopReason.Error, message);
    }
}
