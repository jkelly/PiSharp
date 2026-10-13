// tui.ts's editor handlers (runDurableTui's submit, followUp, abort, exit, selectModel, selectConversation, cycleThinking), the inbox
// queue (#syncQueue), the task panel (#syncTasks), notices (#syncNotices) and the theme from pi's settings.
using PiSharp.Cli.Durable;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Durable;
using static TuiTests;

internal static partial class Program
{
    private const string Busy = """{"run":{"taskId":7,"inputs":[1]}}""";

    private static IEnumerable<(string, Func<Task>)> TuiInputCases() =>
    [
        Case("tui.submit.idle-prompts", async () =>
        {
            await using var run = await TuiRun.Start(View([]));
            await run.Submit("  hello there  ");
            // The controller prompts when idle and steers when busy; the TUI always submits with whenBusy "steer".
            await run.Controller.WaitFor("submit Steer hello there");
            await run.Submit("   ");
            await Task.Delay(200);
            Equal("submit Steer hello there", string.Join("; ", run.Controller.Calls), "blank input is ignored");
            await run.WaitUntil(text => !text.Contains("hello there", StringComparison.Ordinal), "the editor cleared");
        }),

        Case("tui.submit.busy-steers-and-follows-up", async () =>
        {
            await using var run = await TuiRun.Start(View([UserEntry(1, "long task")], live: Busy));
            await run.WaitFor("Working... (esc to abort)");
            await run.Submit("also check the tests");
            await run.Controller.WaitFor("submit Steer also check the tests");
            run.Send("then write docs");
            await run.WaitFor("then write docs");
            run.Send(FollowUpKey);
            await run.Controller.WaitFor("submit FollowUp then write docs");
            await run.WaitUntil(text => !text.Contains("then write docs", StringComparison.Ordinal), "the editor cleared by the follow-up key");
            // An empty editor sends no follow-up.
            run.Send(FollowUpKey);
            // The inbox shows what waits for a boundary, in order.
            run.Source.Publish(View([UserEntry(1, "long task")], live: Busy, inbox: """
                {"items":[{"id":1,"mode":"steer","content":"also check the tests"},
                {"id":2,"mode":"followUp","content":[{"type":"text","text":"then "},{"type":"image","data":"x","mimeType":"image/png"},{"type":"text","text":"write docs"}]},
                {"id":3,"mode":"write","entry":{"kind":"note"}}]}
                """));
            var screen = await run.WaitFor("[write] <note>");
            ShowsInOrder(screen, ["[steer] also check the tests", "[followUp] then write docs", "[write] <note>"], "queue");
            Equal(2, run.Controller.Calls.Count, "calls: " + string.Join("; ", run.Controller.Calls));
        }),

        Case("tui.command.model", async () =>
        {
            await using var run = await TuiRun.Start(View([]));
            await run.Submit("/model");
            var screen = await run.WaitFor("Select model:");
            // The shown conversation's model first, then the others in the runtime's order.
            var first = screen.IndexOf("faux-1", StringComparison.Ordinal);
            Check(first >= 0 && first < screen.IndexOf("claude-sonnet-4-5", StringComparison.Ordinal) &&
                screen.IndexOf("claude-sonnet-4-5", StringComparison.Ordinal) < screen.IndexOf("gpt-5", StringComparison.Ordinal), "order:\n" + screen);
            Check(!screen.Contains("/compact · /tasks", StringComparison.Ordinal) || screen.Contains("Select model:", StringComparison.Ordinal), "selector shown");
            run.Send("gpt");
            await Task.Delay(100);
            run.Send("\r");
            await run.Controller.WaitFor("setModel openai/gpt-5");
            await run.WaitUntil(text => !text.Contains("Select model:", StringComparison.Ordinal), "the editor back");
            // The model key opens it too; Escape cancels without aborting.
            run.Send("\u000c");
            await run.WaitFor("Select model:");
            run.Send("\u001b[B");
            run.Send("\u001b");
            await run.WaitUntil(text => !text.Contains("Select model:", StringComparison.Ordinal), "cancelled");
            await Task.Delay(100);
            Equal("setModel openai/gpt-5", string.Join("; ", run.Controller.Calls), "no other call");
            // The editor has focus again.
            await run.Submit("after");
            await run.Controller.WaitFor("submit Steer after");
        }),

        Case("tui.command.agents", async () =>
        {
            await using var run = await TuiRun.Start(View([]));
            await run.Submit("/agents");
            var screen = await run.WaitFor("Switch to:");
            // Newest conversation first; the shown one is marked; a subagent shows its task.
            Check(screen.Contains("subagent 2", StringComparison.Ordinal) && screen.Contains("Summarize the README", StringComparison.Ordinal), "subagent:\n" + screen);
            Check(screen.Contains("(shown)", StringComparison.Ordinal), "shown mark:\n" + screen);
            Check(screen.IndexOf("subagent 2", StringComparison.Ordinal) < screen.IndexOf("(shown)", StringComparison.Ordinal), "newest first");
            run.Send("\r");
            await run.Controller.WaitFor("switch 2");
            await run.WaitUntil(text => !text.Contains("Switch to:", StringComparison.Ordinal), "the editor back");
            await run.Submit("/agents");
            await run.WaitFor("Switch to:");
            run.Send("\u001b[B");
            run.Send("\r");
            await run.Controller.WaitFor("switch 1");
        }),

        Case("tui.command.compact", async () =>
        {
            await using var run = await TuiRun.Start(View([]));
            await run.Submit("/compact");
            await run.Controller.WaitFor("compact");
            await run.Submit("/compact   keep the API notes  ");
            await run.Controller.WaitFor("compact keep the API notes");
            await run.Submit("/compactly");
            await run.Controller.WaitFor("submit Steer /compactly");
        }),

        Case("tui.command.tasks", async () =>
        {
            await using var run = await TuiRun.Start(View([]));
            var graph = Graph(
                Node(1, "pi.run", RootId, State(DurableTaskStatus.Running, "generation")),
                Node(2, "pi.tool", RootId, State(DurableTaskStatus.Running, "execute"), owner: 1, conversations: [2]),
                Node(3, "pi.run", new ConversationId(2), State(DurableTaskStatus.Waiting, "tools", System.Collections.Immutable.ImmutableArray.Create(new TaskId(4)), JoinPolicy.FailFast)),
                Node(4, "pi.tool", new ConversationId(2), State(DurableTaskStatus.Completing, null, outcome: TaskOutcomeStatus.Failed), owner: 3,
                    background: true, abortRequested: true),
                Node(5, "pi.compaction", RootId, State(DurableTaskStatus.Pending, null)));
            var open = false;
            run.Controller.OnCall = call =>
            {
                if (call != "toggleTasks") return;
                open = !open;
                run.Source.Publish(View([], tasks: open ? graph : null));
            };
            await run.Submit("/tasks");
            var screen = await run.WaitFor("Tasks (5 live, /tasks to hide)");
            ShowsInOrder(screen, [
                "Tasks (5 live, /tasks to hide)",
                "pi.run #1: running generation",
                "pi.tool #2: running execute owns conversation 2",
                "pi.run #3: waiting on 4",
                "pi.tool #4: completing (failed) [background, aborting]",
                "pi.compaction #5: pending undefined",
            ], "task panel");
            Check(screen.Contains("\n     pi.tool #2", StringComparison.Ordinal) && screen.Contains("\n         pi.tool #4", StringComparison.Ordinal),
                "nested by owner:\n" + screen);
            await run.Submit("/tasks");
            await run.WaitUntil(text => !text.Contains("Tasks (", StringComparison.Ordinal), "panel hidden");
        }),

        Case("tui.key.abort-and-thinking", async () =>
        {
            await using var run = await TuiRun.Start(View([UserEntry(1, "work")], live: Busy));
            run.Send("\u001b");
            await run.Controller.WaitFor("abort");
            run.Send("\u001b[Z");
            await run.Controller.WaitFor("cycleThinking");
            run.Source.Publish(View([UserEntry(1, "work")], agent: """{"model":{"provider":"faux","modelId":"faux-1"},"thinkingLevel":"high"}"""));
            await run.WaitFor("thinking:high");
            var colored = Themes.Current.GetThinkingBorderColor("high")("─");
            Check(run.Terminal.RawOutput.Contains(colored[..colored.IndexOf('─')] + "───", StringComparison.Ordinal), "editor border in the level's color");
        }),

        Case("tui.key.ctrl-c-exits", async () =>
        {
            await using var run = await TuiRun.Start(View([UserEntry(1, "work")], live: Busy));
            run.Send("draft");
            await run.WaitFor("draft");
            run.Send("\u0003");
            Equal(0, await run.Exited(), "exit code");
            Equal(0, run.Controller.Calls.Count, "no abort: the durable run continues");
            Equal(0, run.Source.Subscribers, "unsubscribed");
        }),

        Case("tui.key.ctrl-d-exits-when-empty", async () =>
        {
            await using var run = await TuiRun.Start(View([]));
            run.Send("ab");
            await run.WaitFor("ab");
            run.Send("\u001b[D");
            run.Send("\u0004");
            await run.WaitUntil(text => text.Split('\n').Any(line => line.Trim() == "a"), "Ctrl+D deletes forward in a non-empty editor");
            Check(!run.Run.IsCompleted, "still running");
            run.Send("\u0001\u000b");
            await run.WaitUntil(text => !text.Split('\n').Any(line => line.Trim() == "a"), "editor emptied");
            run.Send("\u0004");
            Equal(0, await run.Exited(), "exit code");
        }),

        Case("tui.exit.cancellation", async () =>
        {
            await using var run = await TuiRun.Start(View([]));
            await run.Cancel();
            Equal(0, await run.Exited(), "exit code");
        }),

        Case("tui.notices.last-four", async () =>
        {
            DurableNotice Notice(long id, DurableNoticeLevel level, string message) => new(id, level, message);
            var notices = new[]
            {
                Notice(1, DurableNoticeLevel.Info, "notice one"),
                Notice(2, DurableNoticeLevel.Info, "Compacted 12 entries"),
                Notice(3, DurableNoticeLevel.Warning, "Model faux-1 is slow"),
                Notice(4, DurableNoticeLevel.Error, "Prompt failed: overloaded"),
                Notice(5, DurableNoticeLevel.Info, "Switched to subagent 2"),
            };
            await using var run = await TuiRun.Start(View(Conversation([]), notices[..1]));
            await run.WaitFor("notice one");
            run.Source.Publish(View(Conversation([]), notices));
            var screen = await run.WaitFor("Switched to subagent 2");
            Check(!screen.Contains("notice one", StringComparison.Ordinal), "only the newest four:\n" + screen);
            ShowsInOrder(screen, ["Compacted 12 entries", "Model faux-1 is slow", "Prompt failed: overloaded", "Switched to subagent 2"], "notices");
            var raw = run.Terminal.RawOutput;
            Check(raw.Contains(Themes.Current.Fg("error", "Prompt failed: overloaded"), StringComparison.Ordinal), "error color");
            Check(raw.Contains(Themes.Current.Fg("warning", "Model faux-1 is slow"), StringComparison.Ordinal), "warning color");
            Check(raw.Contains(Themes.Current.Fg("muted", "Switched to subagent 2"), StringComparison.Ordinal), "info color");
        }),

        Case("tui.theme.from-settings", async () =>
        {
            await using (var light = await TuiRun.Start(View([]), settingsJson: """{"theme":"light"}"""))
                await light.WaitUntil(_ => Themes.CurrentThemeName == "light", "the light theme");
            await using (var dark = await TuiRun.Start(View([]), settingsJson: """{"theme":"dark"}"""))
                await dark.WaitUntil(_ => Themes.CurrentThemeName == "dark", "the dark theme");
        }),
    ];
}
