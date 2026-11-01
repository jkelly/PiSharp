using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

internal static partial class Program
{
    private const string Question =
        """tool: ask_user_question {"questions":[{"question":"Which version?","header":"Version","options":[{"label":"1.2.0.2","description":"C# fixes"},{"label":"1.2.1","description":"Next Pi"}],"multiSelect":false}]}""";

    // The conversation: the composer, the transcript, forks, renames, the slash menu, questions and plans (PLAN.md Appendix C items 4-9 and 16,
    // and the PiSharp items 29 and 30).
    internal static IEnumerable<(string, Func<Task>)> ConversationCases() =>
    [
        // Item 4: a Markdown reply, and a bash call reaching Done with its result.
        Browse("browser.send-markdown-and-bash", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("send");
            await page.OpenAgentAsync(session);
            await page.SendAsync("**release** notes with `code` and a [link](https://example.com/notes)");
            var answer = page.Locator("#thread article.msg-assistant .msg-content").Filter(new() { HasText = "Echo:" });
            await Expect(answer.Locator("strong")).ToHaveTextAsync("release");
            await Expect(answer.Locator("code")).ToHaveTextAsync("code");
            await Expect(answer.Locator("a")).ToHaveAttributeAsync("href", "https://example.com/notes");
            await Expect(page.Locator("#thread article.msg-user").Last).ToContainTextAsync("**release** notes");

            await page.SendAsync("!echo hello-from-bash");
            var card = page.Locator("#thread details.tool-card").Filter(new() { Has = page.Locator(".tool-name", new() { HasText = "bash" }) });
            await Expect(card.Locator(".tool-status")).ToHaveTextAsync("Done");
            await Expect(card.Locator(".tool-args-summary")).ToContainTextAsync("echo hello-from-bash");
            await card.Locator("summary").ClickAsync();
            await Expect(card.Locator(".tool-body pre").Last).ToContainTextAsync("hello-from-bash");
            await Expect(page.Thread()).ToContainTextAsync("Tool finished: bash");
            await Expect(page.Locator("[data-action='interrupt']")).ToBeDisabledAsync();
        }),

        // Item 5: while busy, Interrupt is enabled; a follow-up is queued and withdrawn; reject mode refuses with 409; a steer is answered
        // after the tool round.
        Browse("browser.busy-follow-up-reject-and-steer", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("busy");
            await page.OpenAgentAsync(session);
            var interrupt = page.Locator("[data-action='interrupt']");
            await Expect(interrupt).ToBeDisabledAsync();
            await page.SendAsync("!sleep 10");
            await Expect(interrupt).ToBeEnabledAsync();
            await Expect(page.Locator("[data-status='working']")).ToBeVisibleAsync();

            await page.SendAsync("after the run");
            var chip = page.Locator(".queue-chip").Filter(new() { HasText = "after the run" });
            await Expect(chip.Locator(".queue-kind")).ToHaveTextAsync("Queued");
            await chip.Locator("[data-action='withdraw']").ClickAsync();
            await page.ToastAsync("Withdrawn");
            await Expect(chip).ToHaveCountAsync(0);

            var box = page.Locator("#composerInput");
            await page.Locator("#whenBusy").SelectOptionAsync("reject");
            await box.FillAsync("refused while busy");
            await box.PressAsync("Enter");
            var banner = page.Locator("#errorBanner");
            await Expect(banner).ToBeVisibleAsync();
            await Expect(banner).ToContainTextAsync("409 busy");
            await Expect(box).ToHaveValueAsync("refused while busy");
            await box.FillAsync("");

            await page.Locator("#whenBusy").SelectOptionAsync("steer");
            await page.SendAsync("steer note");
            await Expect(banner).ToBeHiddenAsync();
            await Expect(page.Thread()).ToContainTextAsync("Echo: steer note", new() { Timeout = 30_000 });
            await Expect(interrupt).ToBeDisabledAsync(new() { Timeout = 30_000 });
            await Expect(page.Thread()).Not.ToContainTextAsync("after the run");
            await Expect(page.Thread()).Not.ToContainTextAsync("refused while busy");
        }),

        // Item 6.
        Browse("browser.escape-interrupts", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("escape");
            await page.OpenAgentAsync(session);
            var interrupt = page.Locator("[data-action='interrupt']");
            await page.SendAsync("!sleep 30");
            await Expect(interrupt).ToBeEnabledAsync();
            await page.Keyboard.PressAsync("Escape");
            await page.ToastAsync("Foreground work interrupted");
            await Expect(interrupt).ToBeDisabledAsync();
            await Expect(page.Locator("[data-status='working']")).ToBeHiddenAsync();
            var card = page.Locator("#thread details.tool-card").Filter(new() { Has = page.Locator(".tool-name", new() { HasText = "bash" }) });
            await Expect(card.Locator(".tool-status")).Not.ToHaveTextAsync("running");
        }),

        // Items 7 and 8: a fork at an entry is selected in the breadcrumb and the tree, and its history ends at that entry; the picker switches.
        Browse("browser.fork-breadcrumb-and-picker", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("forks");
            await page.OpenAgentAsync(session);
            await page.SendAsync("first");
            await Expect(page.Thread()).ToContainTextAsync("Echo: first");
            await page.SendAsync("second");
            await Expect(page.Thread()).ToContainTextAsync("Echo: second");

            var first = page.Locator("#thread article.msg-assistant").Filter(new() { HasText = "Echo: first" });
            var entry = await first.GetAttributeAsync("data-entry");
            await first.HoverAsync();
            await first.Locator("[data-action='fork']").ClickAsync();
            await Expect(page.Locator("[data-pr-text]")).ToContainTextAsync($"entry #{entry}");
            await page.PromptAsync("alt-path");
            await page.ToastAsync($"Forked at entry #{entry}");
            await Expect(page).ToHaveURLAsync(new Regex(@"/a/\d+$"));
            var fork = long.Parse(Regex.Match(page.Url, @"/a/(\d+)$").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Check(fork != 1, "the fork is a new agent");
            await page.AgentReadyAsync();
            await Expect(page.Locator("[data-crumbs]")).ToContainTextAsync("fork alt-path");
            await Expect(page.Locator("[data-crumbs] [aria-current='page']")).ToContainTextAsync("alt-path");
            await Expect(page.Row(session, fork)).ToHaveClassAsync(Selected);
            await Expect(page.Row(session, fork)).ToHaveAttributeAsync("title", $"Fork of agent #1 at entry #{entry}");
            await Expect(page.Locator("[data-agent-source]")).ToContainTextAsync($"at entry #{entry}");
            await Expect(page.Thread()).ToContainTextAsync("Echo: first");
            await Expect(page.Thread()).Not.ToContainTextAsync("second");
            var last = await page.EvaluateAsync<string?>("() => [...document.querySelectorAll('#thread [data-entry]')].map(e => e.dataset.entry).filter(Boolean).at(-1)");
            Equal(entry, last, "the fork's history ends at the fork entry");

            await page.Locator(".agent-head [aria-label='Browse conversations']").ClickAsync();
            var picker = page.Locator("[data-agent-picker]");
            await Expect(picker).ToBeVisibleAsync();
            await Expect(picker.Locator(".dropdown-item")).ToHaveCountAsync(2);
            await Expect(picker.Locator(".dropdown-item").Filter(new() { HasText = "alt-path" })).ToContainTextAsync($"Forked at entry #{entry}");
            await picker.Locator(".dropdown-item[href$='/a/1']").ClickAsync();
            await Expect(page).ToHaveURLAsync(new Regex(Regex.Escape(AgentPath(session, 1)) + "$"));
            await page.AgentReadyAsync();
            await Expect(page.Thread()).ToContainTextAsync("Echo: second");
            await Expect(page.Row(session, 1)).ToHaveClassAsync(Selected);
        }),

        // Item 9.
        Browse("browser.rename-session-and-agent", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("draft");
            await page.OpenAgentAsync(session);
            var row = page.Row(session);
            await row.HoverAsync();
            await row.Locator("[data-action='rename-session']").ClickAsync();
            await page.PromptAsync("release-1-2");
            await page.ToastAsync("Name saved");
            await Expect(row.Locator(".row-name")).ToHaveTextAsync("release-1-2");
            await Expect(page.Locator("[data-session-tab] .ins-title")).ToHaveTextAsync("release-1-2");

            await page.Locator(".agent-head [data-action='rename-agent']").ClickAsync();
            await page.PromptAsync("planner");
            await page.ToastAsync("Name saved");
            await Expect(page.Row(session, 1).Locator(".row-name")).ToHaveTextAsync("planner");
            await Expect(page.Locator("[data-crumbs] [aria-current='page']")).ToContainTextAsync("planner");

            await page.Locator(".agent-head [data-action='rename-agent']").ClickAsync();
            await page.Locator("#pr-input").FillAsync("123");
            await page.Locator("[data-pr-submit]").ClickAsync();
            await Expect(page.Locator("[data-pr-error]")).ToBeVisibleAsync();
            await page.Locator("#modal-prompt [data-bs-dismiss='modal']").First.ClickAsync();
            await Expect(page.Locator("#modal-prompt")).ToBeHiddenAsync();
        }),

        // Item 16: "/" lists 9 commands; /ta opens Tasks; /compact toasts; /reset with a note hides the old replies.
        Browse("browser.slash-menu", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("slash");
            await page.OpenAgentAsync(session);
            await page.SendAsync("hello before the reset");
            await Expect(page.Thread()).ToContainTextAsync("Echo: hello before the reset");

            var box = page.Locator("#composerInput");
            var menu = page.Locator("#slashMenu");
            await box.FillAsync("/");
            await Expect(menu).ToBeVisibleAsync();
            await Expect(menu.Locator(".slash-item:visible")).ToHaveCountAsync(9);
            await box.FillAsync("/ta");
            await Expect(menu.Locator(".slash-item:visible")).ToHaveCountAsync(1);
            await Expect(menu.Locator(".slash-item:visible .cmd")).ToHaveTextAsync("/tasks");
            await box.PressAsync("Enter");
            await Expect(menu).ToBeHiddenAsync();
            await Expect(page.Locator("#insPanes")).ToHaveAttributeAsync("data-active-pane", "ins-tasks");
            await box.FillAsync("/zz");
            await Expect(menu).ToBeHiddenAsync();
            await box.FillAsync("");

            await page.SendAsync("/compact");
            await page.ToastAsync("Compaction requested");
            await Expect(page.Locator("[data-ctx-compacting]")).ToBeHiddenAsync(new() { Timeout = 30_000 });
            await Expect(page.Locator("[data-status='working']")).ToBeHiddenAsync(new() { Timeout = 30_000 });

            await page.SendAsync("/reset fresh start note");
            await page.ToastAsync("New context started");
            await Expect(page.Thread()).ToContainTextAsync("fresh start note");
            await Expect(page.Thread()).Not.ToContainTextAsync("Echo: hello before the reset");
            await page.SendAsync("after the reset");
            await Expect(page.Thread()).ToContainTextAsync("Echo: after the reset");
        }),

        // PiSharp item 29: an ask_user_question form, answered in the waiting panel.
        Browse("browser.answer-a-question", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("questions");
            await page.OpenAgentAsync(session);
            await page.SendAsync(Question);
            var form = page.Locator(".pending-panel form[data-pending='questions']");
            await Expect(form).ToBeVisibleAsync();
            await Expect(form.Locator("legend")).ToHaveTextAsync("Which version?");
            await Expect(page.Locator("[data-status='waiting']")).ToBeVisibleAsync();
            var card = page.Locator("#thread details.tool-card").Filter(new() { Has = page.Locator(".tool-name", new() { HasText = "ask_user_question" }) });
            await Expect(card.Locator(".tool-status")).ToHaveTextAsync("Waiting for you");
            await form.Locator("label.choice-card", new() { HasText = "1.2.1" }).ClickAsync();
            await form.Locator("[data-action='answer-questions']").ClickAsync();
            await page.ToastAsync("Answer sent");
            await Expect(page.Locator(".pending-panel")).ToHaveCountAsync(0);
            await Expect(page.Locator("[data-status='waiting']")).ToBeHiddenAsync();
            await Expect(card.Locator(".tool-status")).ToHaveTextAsync("Done");
            await Expect(page.Thread()).ToContainTextAsync("Tool finished: ask_user_question");
            await Expect(page.Locator("#thread article.msg-assistant .msg-content").Last).ToContainTextAsync("1.2.1");
        }),

        // PiSharp item 30: plan mode, then a plan approved from the waiting panel.
        Browse("browser.approve-a-plan", async (hub, window) =>
        {
            var page = window.Page;
            var session = await hub.SessionAsync("plans");
            await page.OpenAgentAsync(session);
            await page.Locator("#planMode").CheckAsync();
            await page.ToastAsync("Plan mode on");
            await page.SendAsync("""tool: exit_plan_mode {"plan":"# Release plan\n\n1. Bump the version\n2. Tag the release"}""");
            var form = page.Locator(".pending-panel form[data-pending='plan']");
            await Expect(form).ToBeVisibleAsync();
            await Expect(form.Locator(".plan-text")).ToContainTextAsync("Bump the version");
            await form.Locator("[data-action='review-plan'][data-approved='true']").ClickAsync();
            await page.ToastAsync("Plan approved");
            await Expect(page.Locator(".pending-panel")).ToHaveCountAsync(0);
            await Expect(page.Thread()).ToContainTextAsync("Tool finished: exit_plan_mode");
            await Expect(page.Locator("#planMode")).Not.ToBeCheckedAsync();
        }),
    ];
}
