using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Chats in a browser (decision 0011, run W2, WU10): a new chat streams its answer; a reload in the middle of an answer catches up from the
// last saved point and shows the answer once; an approval decided in one tab closes the dialog in another, which says so; the approval
// dialog comes back after a reload; and a fork from the session tree opens a new branch without the forked-off message. The faux model's
// grammar drives them: "slow: …" streams one word every 40 ms, "!command" calls bash (which asks first in the default Ask mode).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ChatCases() =>
    [
        Browse("browser.chat.a-new-chat-streams-its-answer", async (agent, window) =>
        {
            var page = window.Page;
            await page.GotoAsync("/chat/new");
            await Expect(page.Locator(".chat-layout[data-chat-new]")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-composer-input]")).ToBeFocusedAsync();
            await page.Locator("[data-composer-input]").FillAsync("slow: " + Words(120));
            await page.Locator("[data-composer-input]").PressAsync("Enter");
            await page.WaitForURLAsync(new Regex(@"/chat/(?!new\b)[^/?#]+$"));
            var chatId = Uri.UnescapeDataString(new Uri(page.Url).AbsolutePath.Split('/')[2]);
            await page.ChatReadyAsync();

            var answer = page.Answers().Last;
            await Expect(answer).ToContainTextAsync("w005");
            var partial = await answer.InnerTextAsync();
            Check(!partial.Contains("w120", StringComparison.Ordinal), "the answer is seen while it streams: " + partial);
            await Expect(page.Layout()).ToHaveAttributeAsync("data-busy", "true");
            // W5 live test: the running controls are .d-flex, which beat [hidden] until site.css's [hidden] rule, so they showed after the run.
            await Expect(page.Locator("[data-running-controls] [data-action='steer']")).ToBeVisibleAsync();
            await Expect(page.Locator("[data-send]")).ToBeHiddenAsync();
            await Expect(answer).ToContainTextAsync("w120", new() { Timeout = 30_000 });
            await page.IdleAsync(agent, chatId);
            await Expect(page.Layout()).ToHaveAttributeAsync("data-busy", "false");
            await Expect(page.Locator("[data-running-controls]")).ToBeHiddenAsync();
            await Expect(page.Locator("[data-send]")).ToBeVisibleAsync();
            await Expect(page.Answers()).ToHaveCountAsync(1);
            await Expect(page.Thread().Locator("article.msg-user")).ToHaveCountAsync(1);
            await Expect(answer).ToHaveAttributeAsync("data-stop", "stop");
            var final = await answer.InnerTextAsync();
            Equal(120, SlowWords(final), "every word");
            Equal(120, Regex.Matches(final, @"\bw\d{3}\b").Count, "no word twice");
            await Expect(page.Locator($"[data-recent-chats] a.chat-link[href='/chat/{Uri.EscapeDataString(chatId)}']")).ToHaveCountAsync(1);
        }),

        Browse("browser.chat.a-reload-mid-stream-catches-up", async (agent, window) =>
        {
            var chatId = await agent.CreateChatAsync("slow: " + Words(150), "reload-mid-stream");
            var page = window.Page;
            await page.OpenChatAsync(chatId);
            var answer = page.Answers().Last;
            await Expect(answer).ToContainTextAsync("w010");
            var before = SlowWords(await answer.InnerTextAsync());
            Check(before < 150, $"the reload comes in the middle of the answer ({before} words)");

            await page.ReloadAsync();
            await page.ChatReadyAsync();
            await Expect(page.Layout()).ToHaveAttributeAsync("data-background", "true");
            answer = page.Answers().Last;
            await Expect(answer).ToContainTextAsync($"w{before:000}");
            await Expect(answer).ToContainTextAsync("w150", new() { Timeout = 30_000 });
            await page.IdleAsync(agent, chatId);
            await Expect(page.Layout()).Not.ToHaveAttributeAsync("data-background", "true");
            await Expect(page.Answers()).ToHaveCountAsync(1);
            var text = await answer.InnerTextAsync();
            Equal(150, SlowWords(text), "every word, once, after the reload");
            Equal(150, Regex.Matches(text, @"\bw\d{3}\b").Count, "no word twice");
            await Expect(answer).ToHaveAttributeAsync("data-stop", "stop");
        }),

        Browse("browser.chat.an-approval-decided-in-one-tab-closes-the-dialog-in-the-other", async (agent, window) =>
        {
            var chatId = await agent.CreateChatAsync("!echo approved >> marker.txt", "two-tabs");
            var first = window.Page;
            var second = await window.NewPageAsync();
            await first.OpenChatAsync(chatId);
            await second.OpenChatAsync(chatId);
            var card = first.Thread().Locator(".awaiting-card[data-approval]");
            await Expect(card).ToHaveCountAsync(1);
            await Expect(card).ToContainTextAsync("echo approved >> marker.txt");
            await Expect(second.Thread().Locator(".awaiting-card[data-approval]")).ToHaveCountAsync(1);

            var dialog = await OpenApprovalDialogAsync(second);
            await Expect(dialog.Locator("[data-approval-input]")).ToHaveTextAsync("echo approved >> marker.txt");
            Equal(0, agent.Lines("marker.txt").Length, "nothing ran before the decision");

            await card.Locator("[data-action='approve'][data-decision='allow']").ClickAsync();
            await Expect(dialog).ToBeHiddenAsync();
            await second.ToastAsync("Already decided");
            await second.ToastAsync("This tool call was answered in another tab.");
            foreach (var page in new[] { first, second })
            {
                await Expect(page.Thread()).ToContainTextAsync("Tool finished: bash", new() { Timeout = 30_000 });
                await Expect(page.Locator(".awaiting-card[data-approval]")).ToHaveCountAsync(0);
            }
            await Expect(first.Locator("#toasts .toast", new() { HasText = "Already decided" })).ToHaveCountAsync(0);
            await agent.WaitIdleAsync(chatId);
            Equal(1, agent.Lines("marker.txt").Length, "the command ran once");
        }),

        Browse("browser.chat.the-approval-dialog-survives-a-reload", async (agent, window) =>
        {
            var chatId = await agent.CreateChatAsync("!echo once >> marker.txt", "dialog-reload");
            var page = window.Page;
            await page.OpenChatAsync(chatId);
            var dialog = await OpenApprovalDialogAsync(page);
            await Expect(dialog.Locator("[data-approval-input]")).ToHaveTextAsync("echo once >> marker.txt");

            await page.ReloadAsync();
            await page.ChatReadyAsync();
            await Expect(dialog).ToBeVisibleAsync();
            await Expect(dialog.Locator("[data-approval-input]")).ToHaveTextAsync("echo once >> marker.txt");
            await Expect(dialog.Locator("[data-approval-tool]").First).ToHaveTextAsync("bash");
            Equal(0, agent.Lines("marker.txt").Length, "the reload decided nothing");

            await dialog.Locator("button[type='submit'][data-decision='allow']").ClickAsync();
            await Expect(dialog).ToBeHiddenAsync();
            await Expect(page.Thread()).ToContainTextAsync("Tool finished: bash", new() { Timeout = 30_000 });
            await Expect(page.Locator(".awaiting-card[data-approval]")).ToHaveCountAsync(0);
            await Expect(page.Locator("#toasts .toast", new() { HasText = "Already decided" })).ToHaveCountAsync(0);
            await agent.WaitIdleAsync(chatId);
            Equal(1, agent.Lines("marker.txt").Length, "the command ran once");

            // Decided: a reload no longer brings the dialog back.
            await page.ReloadAsync();
            await page.ChatReadyAsync();
            await Expect(dialog).ToBeHiddenAsync();
        }),

        Browse("browser.chat.a-fork-from-the-tree-opens-a-new-branch", async (agent, window) =>
        {
            var chatId = await agent.CreateChatAsync("first message", "fork-tree");
            await agent.WaitIdleAsync(chatId);
            var page = window.Page;
            await page.OpenChatAsync(chatId);
            await Expect(page.Thread()).ToContainTextAsync("Echo: first message");
            var mainAgent = await page.Layout().GetAttributeAsync("data-agent");
            await page.SendAsync("second message");
            await Expect(page.Thread()).ToContainTextAsync("Echo: second message", new() { Timeout = 30_000 });
            await page.IdleAsync(agent, chatId);

            var tree = await OpenTreeAsync(page);
            await Expect(tree.Locator("[data-tree-footer]")).ToContainTextAsync("1 branch ·");
            var second = tree.Locator("[data-tree-node][data-role='user']", new() { HasText = "second message" });
            await Expect(second).ToHaveCountAsync(1);
            await second.ClickAsync();
            await Expect(second).ToHaveClassAsync(new Regex(@"\bselected\b"));
            await Expect(tree.Locator("[data-tree-selected-text]")).ToHaveTextAsync("second message");
            await tree.Locator("[data-action='tree-fork-before']").ClickAsync();

            await page.WaitForURLAsync(new Regex(@"/chat/[^/?#]+\?agent=\d+$"));
            await page.ChatReadyAsync();
            var branch = await page.Layout().GetAttributeAsync("data-agent");
            Check(branch != mainAgent, $"the fork is a new branch ({mainAgent} → {branch})");
            Equal(branch, new Uri(page.Url).Query.Split('=')[1], "the page shows the new branch");
            await Expect(page.Thread()).ToContainTextAsync("Echo: first message");
            await Expect(page.Thread()).Not.ToContainTextAsync("second message");
            await Expect(page.Thread().Locator("article.msg-user")).ToHaveCountAsync(1);

            tree = await OpenTreeAsync(page);
            await Expect(tree.Locator("[data-tree-footer]")).ToContainTextAsync("2 branches ·");
            await Expect(tree.Locator("[data-tree-node][data-role='user']", new() { HasText = "second message" })).ToHaveCountAsync(1);
            await tree.Locator("[data-bs-dismiss='modal']").Last.ClickAsync();
            await Expect(tree).ToBeHiddenAsync();
        }),
    ];

    /// <summary>Opens the approval dialog from the inline card's menu ("Details and deny reason…").</summary>
    private static async Task<ILocator> OpenApprovalDialogAsync(IPage page)
    {
        var card = page.Thread().Locator(".awaiting-card[data-approval]");
        await Expect(card).ToHaveCountAsync(1);
        await card.Locator("button[aria-label='More approval options']").ClickAsync();
        await card.Locator("[data-action='approval-details']").ClickAsync();
        var dialog = page.Locator("#modal-tool-approval");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog).ToHaveClassAsync(new Regex(@"\bshow\b"));
        return dialog;
    }

    /// <summary>Opens the session tree from the top bar and waits until its entries loaded.</summary>
    private static async Task<ILocator> OpenTreeAsync(IPage page)
    {
        await page.Locator("button[title='Session tree']").ClickAsync();
        var tree = page.Locator("#modal-session-tree");
        await Expect(tree).ToBeVisibleAsync();
        await Expect(tree.Locator("[data-tree]")).ToHaveCountAsync(1);
        await Expect(tree.Locator("[data-tree-footer]")).Not.ToBeEmptyAsync();
        return tree;
    }

    /// <summary>The distinct words <c>w001</c>… in a text.</summary>
    private static int SlowWords(string text) => Regex.Matches(text, @"\bw\d{3}\b").Select(match => match.Value).Distinct(StringComparer.Ordinal).Count();
}
