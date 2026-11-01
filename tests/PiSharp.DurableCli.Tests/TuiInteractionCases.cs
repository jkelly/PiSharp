// Authored for PiSharp (decision 0005, "PiSharp-only additions"; no upstream counterpart): the `pisharp durable` UI of ask_user_question
// and plan mode over the fake view source and controller. A pending question or plan review (a running pi.live.tools slot whose details
// are pending) replaces the editor with its form; the form answers through the controller; the editor comes back with its draft once the
// call ends. `/plan` switches plan mode and the footer shows it. Every step waits for the screen to show the effect of the key before the
// next one, so no case depends on timing.
using System.Text.Json.Nodes;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Interactive.Mode;
using PiSharp.Durable;
using static TuiTests;

internal static partial class Program
{
    private const string Down = "\u001b[B";
    private const string Up = "\u001b[A";
    private const string Right = "\u001b[C";
    private const string Left = "\u001b[D";
    private const string ShiftTab = "\u001b[Z";
    private const string Escape = "\u001b";

    /// <summary>A multi-select question (Scope) and a single-select one (Approach).</summary>
    private const string TwoQuestions = """
        [{"question":"Which parts should the refactor cover?","header":"Scope","options":[{"label":"Parser","description":"The tokenizer and parser"},
        {"label":"Printer","description":"Output formatting"}],"multiSelect":true},
        {"question":"Which approach do you prefer?","header":"Approach","options":[{"label":"Visitor","description":"A visitor over the tree"},
        {"label":"Switch","description":"Pattern matching"},{"label":"Rewrite","description":"Start over"}],"multiSelect":false}]
        """;

    /// <summary>The single-select question alone.</summary>
    private const string OneQuestion = """
        [{"question":"Which approach do you prefer?","header":"Approach","options":[{"label":"Visitor","description":"A visitor over the tree"},
        {"label":"Switch","description":"Pattern matching"},{"label":"Rewrite","description":"Start over"}],"multiSelect":false}]
        """;

    private const string PlanText = "# Fix the parser\n\n1. Add a failing test.\n2. Fix the tokenizer.\n3. Run the checks.";
    private const string PlanAgent = """{"model":{"provider":"faux","modelId":"faux-1"},"thinkingLevel":"medium","extensions":["coding-tools","pi-prompt","user-interaction","plan-mode","subagent"]}""";

    private static EntryRecord[] QuestionHistory(string questions) =>
    [
        UserEntry(1, "refactor the parser"),
        Entry(2, "pi.assistant", AssistantWire($"[{ToolCallBlock("q1", "ask_user_question", $$"""{"questions":{{questions}}}""")}]", "toolUse")),
    ];

    /// <summary>A turn whose ask_user_question call (task 12) waits for the user.</summary>
    private static DurableView QuestionView(string questions, string agent = AgentDoc) => View(QuestionHistory(questions), agent: agent, live: $$$"""
        {"run":{"taskId":7,"inputs":[1]},"tools":[{"callId":"q1","name":"ask_user_question","taskId":12,"status":"running",
        "details":{"kind":"pisharp.questions","version":1,"state":"pending","questions":{{{questions}}}}}]}
        """);

    /// <summary>The turn after the call ended with <paramref name="result"/>.</summary>
    private static DurableView AnsweredView(string questions, string result, string? details = null, string agent = AgentDoc) =>
        View([.. QuestionHistory(questions), Entry(3, "pi.tool-result", ResultWire("q1", "ask_user_question", result, details))], agent: agent, live: "{}");

    private static string ResultWire(string id, string name, string text, string? details, bool isError = false) =>
        $$"""{"role":"toolResult","toolCallId":"{{id}}","toolName":"{{name}}","content":[{{TextBlock(text)}}],{{(details is null ? "" : $"\"details\":{details},")}}"isError":{{(isError ? "true" : "false")}},"timestamp":1}""";

    private static EntryRecord[] PlanHistory() =>
    [
        UserEntry(1, "plan the parser fix"),
        Entry(2, "pi.assistant", AssistantWire($"[{ToolCallBlock("x1", "exit_plan_mode", "{}")}]", "toolUse")),
    ];

    private const string PlanFile = "/sessions/session-1/plans/1/plan.md";

    /// <summary>A turn in plan mode whose exit_plan_mode call (task 14) waits for the review.</summary>
    private static DurableView PlanView() => View(PlanHistory(), agent: PlanAgent, live: $$$"""
        {"run":{"taskId":7,"inputs":[1]},"tools":[{"callId":"x1","name":"exit_plan_mode","taskId":14,"status":"running",
        "details":{"kind":"pisharp.plan-approval","version":1,"state":"pending","plan":{{{Quote(PlanText)}}},"planFile":"{{{PlanFile}}}",
        "directory":"/sessions/session-1/plans/1"}}]}
        """);

    /// <summary>Sends <paramref name="key"/> and waits until the screen shows <paramref name="shown"/>.</summary>
    private static async Task<string> Press(TuiRun run, string key, string shown)
    {
        run.Send(key);
        return await run.WaitFor(shown);
    }

    /// <summary>Waits until the editor is back: the form's text is gone and the editor shows <paramref name="draft"/>.</summary>
    private static Task<string> EditorBack(TuiRun run, string formText, string? draft = null) => run.WaitUntil(screen =>
        !screen.Contains(formText, StringComparison.Ordinal) && (draft is null || screen.Contains(draft, StringComparison.Ordinal)), "the editor back");

    /// <summary>The end of a question tab's hints: the selector's cancel keys abort, the app's exit key exits (pi's defaults:
    /// "escape/ctrl+c abort  ctrl+d exit").</summary>
    private static string Exits => $"{KeybindingHints.KeyText("tui.select.cancel")} abort  {KeybindingHints.KeyText("app.exit")} exit";

    private static IEnumerable<(string, Func<Task>)> TuiInteractionCases() =>
    [
        Case("tui.question.renders", async () =>
        {
            await using var run = await TuiRun.Start(QuestionView(TwoQuestions));
            var screen = await run.WaitFor("Select all that apply.");
            ShowsInOrder(screen, [
                "Scope   Approach   Submit",
                "Which parts should the refactor cover?",
                "Select all that apply.",
                "→ 1. [ ] Parser — The tokenizer and parser",
                "2. [ ] Printer — Output formatting",
                "3. [ ] Other…",
            ], "the form");
            Check(Flat(screen).Contains("↑↓ navigate  space toggle  1-3 toggle  enter next  ←→ questions  " + Exits, StringComparison.Ordinal), "hints:\n" + screen);
            // The status says what the run waits for; the editor and its border are gone.
            Check(screen.Contains("Waiting for your answer... (esc to abort)", StringComparison.Ordinal), "status:\n" + screen);
            Check(!screen.Contains("Working...", StringComparison.Ordinal), "no working status:\n" + screen);
            // The card: the headers as chips and the questions, then the pending state.
            ShowsInOrder(screen, ["ask_user_question [Scope] [Approach]", "1. Which parts should the refactor cover?", "2. Which approach do you prefer?",
                "Waiting for your answer..."], "the card");
            Console.Error.WriteLine("FRAME tui.question.renders\n" + screen);
            // The second tab shows the single-select question.
            screen = await Press(run, Right, "Select one.");
            ShowsInOrder(screen, ["Which approach do you prefer?", "Select one.", "→ 1. ( ) Visitor — A visitor over the tree", "2. ( ) Switch — Pattern matching",
                "3. ( ) Rewrite — Start over", "4. ( ) Other…"], "the second question");
            Check(Flat(screen).Contains("↑↓ navigate  1-4 pick  enter select  ←→ questions  " + Exits, StringComparison.Ordinal), "single-select hints:\n" + screen);
            Check(run.Controller.Calls.Count == 0, "no call: " + string.Join("; ", run.Controller.Calls));
        }),

        Case("tui.question.single-enter", async () =>
        {
            await using var run = await TuiRun.Start(QuestionView(OneQuestion));
            await run.WaitFor("→ 1. ( ) Visitor");
            await Press(run, Down, "→ 2. ( ) Switch");
            await Press(run, Down, "→ 3. ( ) Rewrite");
            await Press(run, Up, "→ 2. ( ) Switch");
            // Enter picks and moves to the Submit tab.
            var screen = await Press(run, "\r", "Review your answers");
            ShowsInOrder(screen, ["✓ Approach   Submit", "Review your answers", "Approach  Switch", "→ Submit answers", "Decline to answer"], "summary");
            Console.Error.WriteLine("FRAME tui.question.single-enter (Submit tab)\n" + screen);
            await Press(run, "\r", "Sending...");
            await run.Controller.WaitFor("answer 12 Switch");
            // The form stays until the call ends; then the editor is back.
            run.Source.Publish(AnsweredView(OneQuestion, "User answered your questions:\n1. [Approach] Which approach do you prefer?\n   Answer: Switch\nContinue with these answers in mind.",
                """{"kind":"pisharp.questions","version":1,"state":"answered","questions":""" + OneQuestion + ""","answers":[{"selected":["Switch"]}]}"""));
            screen = await EditorBack(run, "Review your answers");
            Check(screen.Contains("✓ [Approach] Switch", StringComparison.Ordinal), "the card shows the answer:\n" + screen);
            Equal("answer 12 Switch", string.Join("; ", run.Controller.Calls), "calls");
            await run.Submit("next");
            await run.Controller.WaitFor("submit Steer next");
        }),

        Case("tui.question.number-keys", async () =>
        {
            await using var run = await TuiRun.Start(QuestionView(TwoQuestions));
            await run.WaitFor("→ 1. [ ] Parser");
            // Multi-select: a digit jumps to its row and toggles it.
            await Press(run, "2", "→ 2. [x] Printer");
            await Press(run, "1", "→ 1. [x] Parser");
            await Press(run, "2", "→ 2. [ ] Printer");
            await Press(run, "\r", "Select one.");
            // Single-select: a digit picks and moves on.
            var screen = await Press(run, "3", "Review your answers");
            ShowsInOrder(screen, ["Scope     Parser", "Approach  Rewrite"], "summary");
            // On the Submit tab, 1 submits.
            run.Send("1");
            await run.Controller.WaitFor("answer 12 Parser / Rewrite");
        }),

        Case("tui.question.multi-space", async () =>
        {
            await using var run = await TuiRun.Start(QuestionView(TwoQuestions));
            await run.WaitFor("→ 1. [ ] Parser");
            await Press(run, " ", "→ 1. [x] Parser");
            await Press(run, Down, "→ 2. [ ] Printer");
            await Press(run, " ", "→ 2. [x] Printer");
            await Press(run, Up, "→ 1. [x] Parser");
            var screen = await Press(run, " ", "→ 1. [ ] Parser");
            Check(screen.Contains("2. [x] Printer", StringComparison.Ordinal), "Printer stays selected:\n" + screen);
            // Enter moves on without toggling.
            screen = await Press(run, "\r", "Select one.");
            Check(screen.Contains("✓ Scope", StringComparison.Ordinal), "Scope answered:\n" + screen);
            await Press(run, "1", "Review your answers");
            run.Send("\r");
            await run.Controller.WaitFor("answer 12 Printer / Visitor");
        }),

        Case("tui.question.other-text", async () =>
        {
            await using var run = await TuiRun.Start(QuestionView(OneQuestion));
            await run.WaitFor("→ 1. ( ) Visitor");
            // The Other row opens an inline input.
            await Press(run, "4", "→ 4. Other:");
            await Press(run, "Use a table", "→ 4. Other: Use a table");
            var screen = await Press(run, "\r", "Review your answers");
            Check(screen.Contains("Approach  Other: \"Use a table\"", StringComparison.Ordinal), "summary:\n" + screen);
            screen = await Press(run, Left, "(•) Other: \"Use a table\"");
            // Esc in the input goes back to the list and keeps the text; it does not abort.
            await Press(run, "\r", "→ 4. Other: Use a table");
            await Press(run, " more", "→ 4. Other: Use a table more");
            await Press(run, Escape, "→ 4. (•) Other: \"Use a table\"");
            Check(!run.Controller.Calls.Contains("abort"), "Esc in the input does not abort");
            // Picking an option clears the Other text of a single-select question.
            await Press(run, "2", "Approach  Switch");
            await Press(run, Left, "4. ( ) Other…");
            await Press(run, "4", "→ 4. Other:");
            await Press(run, "Use a table", "→ 4. Other: Use a table");
            await Press(run, "\r", "Approach  Other: \"Use a table\"");
            run.Send("\r");
            await run.Controller.WaitFor("answer 12 other:Use a table");
        }),

        Case("tui.question.tabs-submit", async () =>
        {
            await using var run = await TuiRun.Start(QuestionView(TwoQuestions));
            await run.WaitFor("Select all that apply.");
            await Press(run, Right, "Select one.");
            await Press(run, Left, "Select all that apply.");
            await Press(run, "\t", "Select one.");
            var screen = await Press(run, "\t", "Review your answers");
            ShowsInOrder(screen, ["Scope     (not answered)", "Approach  (not answered)"], "nothing answered");
            // Submitting with an unanswered question goes to it.
            screen = await Press(run, "\r", "Answer every question first, or decline to answer.");
            Check(screen.Contains("Select all that apply.", StringComparison.Ordinal), "back on Scope:\n" + screen);
            await Press(run, " ", "→ 1. [x] Parser");
            await Press(run, "\t", "Select one.");
            await Press(run, ShiftTab, "Select all that apply.");
            await Press(run, "\t", "Select one.");
            screen = await Press(run, "2", "Review your answers");
            ShowsInOrder(screen, ["✓ Scope   ✓ Approach   Submit", "Scope     Parser", "Approach  Switch"], "both answered");
            Console.Error.WriteLine("FRAME tui.question.tabs-submit (Submit tab)\n" + screen);
            // A failed answer (the runtime refused it) gives the form back.
            run.Controller.NextAnswer = () => Task.FromException(new ArgumentException("refused"));
            run.Send("\r");
            await run.Controller.WaitFor("answer 12 Parser / Switch");
            await run.WaitUntil(text => !text.Contains("Sending...", StringComparison.Ordinal) && text.Contains("→ Submit answers", StringComparison.Ordinal),
                "the form takes input again");
            run.Send("\r");
            await run.WaitUntil(_ => run.Controller.Calls.Count(call => call == "answer 12 Parser / Switch") == 2, "answered again");
        }),

        Case("tui.question.decline", async () =>
        {
            await using var run = await TuiRun.Start(QuestionView(TwoQuestions));
            await run.WaitFor("Select all that apply.");
            await Press(run, "\t", "Select one.");
            await Press(run, "\t", "→ Submit answers");
            await Press(run, Down, "→ Decline to answer");
            run.Send("\r");
            await run.Controller.WaitFor("decline 12");
            run.Source.Publish(AnsweredView(TwoQuestions, "The user declined to answer these questions.",
                """{"kind":"pisharp.questions","version":1,"state":"declined","questions":""" + TwoQuestions + "}"));
            var screen = await EditorBack(run, "Review your answers");
            Check(screen.Contains("Declined to answer.", StringComparison.Ordinal), "the card:\n" + screen);
        }),

        Case("tui.question.esc-aborts", async () =>
        {
            await using var run = await TuiRun.Start(QuestionView(TwoQuestions));
            await run.WaitFor("Select all that apply.");
            await Press(run, " ", "→ 1. [x] Parser");
            run.Send(Escape);
            await run.Controller.WaitFor("abort");
            Equal("abort", string.Join("; ", run.Controller.Calls), "only the abort");
            // Ctrl+C, the selector's other cancel key, aborts too.
            run.Send("\u0003");
            await run.WaitUntil(_ => run.Controller.Calls.Count == 2, "a second abort");
            Equal("abort; abort", string.Join("; ", run.Controller.Calls), "two aborts");
            // The aborted call ends: its card shows the error and the editor is back.
            run.Source.Publish(View([.. QuestionHistory(TwoQuestions), Entry(3, "pi.tool-result", ToolResultWire("q1", "ask_user_question", "Tool execution aborted", true))],
                live: "{}"));
            var screen = await EditorBack(run, "Select all that apply.");
            Check(screen.Contains("Tool execution aborted", StringComparison.Ordinal), "the card shows the result:\n" + screen);
            Check(!screen.Contains("Waiting for your answer", StringComparison.Ordinal), "status cleared:\n" + screen);
        }),

        Case("tui.question.draft-preserved", async () =>
        {
            await using var run = await TuiRun.Start(View(QuestionHistory(OneQuestion)[..1]));
            await Press(run, "half-written idea", "half-written idea");
            run.Source.Publish(QuestionView(OneQuestion));
            var screen = await run.WaitFor("Select one.");
            Check(!screen.Contains("half-written idea", StringComparison.Ordinal), "the editor is replaced:\n" + screen);
            // A later revision of the same pending call keeps the form and what was chosen.
            await Press(run, Down, "→ 2. ( ) Switch");
            run.Source.Publish(QuestionView(OneQuestion) with { Notices = [new DurableNotice(1, DurableNoticeLevel.Info, "a notice")] });
            screen = await run.WaitFor("a notice");
            Check(screen.Contains("→ 2. ( ) Switch", StringComparison.Ordinal), "the same form:\n" + screen);
            await Press(run, "\r", "Review your answers");
            run.Send("\r");
            await run.Controller.WaitFor("answer 12 Switch");
            run.Source.Publish(AnsweredView(OneQuestion, "User answered your questions."));
            await EditorBack(run, "Review your answers", "half-written idea");
            // The draft is the editor's text again: Enter submits it.
            run.Send("\r");
            await run.Controller.WaitFor("submit Steer half-written idea");
        }),

        Case("tui.plan.command-toggle", async () =>
        {
            await using var run = await TuiRun.Start(View([]));
            await run.Submit("/plan");
            await run.Controller.WaitFor("plan on");
            // In plan mode, /plan leaves it.
            run.Source.Publish(View([], agent: PlanAgent));
            await run.WaitFor("main · plan mode ·");
            await run.Submit("/plan");
            await run.Controller.WaitFor("plan off");
            Equal("plan on; plan off", string.Join("; ", run.Controller.Calls), "calls");
        }),

        Case("tui.plan.plan-with-prompt", async () =>
        {
            await using var run = await TuiRun.Start(View([]));
            await run.Submit("/plan   design the cache  ");
            await run.Controller.WaitFor("plan on design the cache");
            // Already in plan mode, /plan <text> still enters (a no-op) and submits.
            run.Source.Publish(View([], agent: PlanAgent));
            await run.WaitFor("main · plan mode ·");
            await run.Submit("/plan and the eviction");
            await run.Controller.WaitFor("plan on and the eviction");
            await run.Submit("/planet");
            await run.Controller.WaitFor("submit Steer /planet");
        }),

        Case("tui.plan.status-badge", async () =>
        {
            await using var run = await TuiRun.Start(View([]));
            var screen = await run.WaitFor("/tasks · /plan ·");
            Check(!screen.Contains("plan mode", StringComparison.Ordinal), "no badge:\n" + screen);
            run.Source.Publish(View([], agent: PlanAgent));
            screen = await run.WaitFor("main · plan mode · faux/faux-1");
            Check(run.Terminal.RawOutput.Contains(Themes.Current.Fg("warning", "plan mode"), StringComparison.Ordinal), "badge in the warning color");
            // A plan review: its status and the card with the whole plan while it waits.
            run.Source.Publish(PlanView());
            screen = await run.WaitFor("Waiting for plan approval... (esc to abort)");
            ShowsInOrder(screen, ["exit_plan_mode", "Plan file: " + PlanFile, "Fix the parser", "1. Add a failing test.", "2. Fix the tokenizer.",
                "3. Run the checks.", "Waiting for your review..."], "the pending card");
            run.Source.Publish(View([]));
            await run.WaitUntil(text => !text.Contains("plan mode", StringComparison.Ordinal) && !text.Contains("Waiting for plan approval", StringComparison.Ordinal),
                "badge and status gone");
        }),

        Case("tui.plan.approval-approve", async () =>
        {
            await using var run = await TuiRun.Start(PlanView());
            var screen = await run.WaitFor("Would you like to proceed?");
            ShowsInOrder(screen, ["Plan ready for review", "Plan file: " + PlanFile, "Fix the parser", "1. Add a failing test.", "3. Run the checks.",
                "Would you like to proceed?", "→ Yes, implement it", "Yes, but don't start yet", "No, keep planning (give feedback)", "No, keep planning"], "the review");
            Console.Error.WriteLine("FRAME tui.plan.approval-approve\n" + screen);
            // A failed review (the runtime refused it) gives the choices back.
            run.Controller.NextAnswer = () => Task.FromException(new ArgumentException("refused"));
            run.Send("\r");
            await run.Controller.WaitFor("review 14 Approve");
            await run.WaitUntil(text => text.Contains("→ Yes, implement it", StringComparison.Ordinal) && !text.Contains("Sending...", StringComparison.Ordinal), "choices back");
            run.Send("\r");
            await run.WaitUntil(_ => run.Controller.Calls.Count(call => call == "review 14 Approve") == 2, "approved again");
            run.Source.Publish(View([.. PlanHistory(), Entry(3, "pi.tool-result", ResultWire("x1", "exit_plan_mode", "The user approved your plan.",
                $$"""{"kind":"pisharp.plan-approval","version":1,"state":"approved","plan":{{Quote(PlanText)}},"planFile":"{{PlanFile}}","directory":"/d","stop":false}"""))],
                live: "{}"));
            screen = await EditorBack(run, "Plan ready for review", "✓ Plan approved; plan mode is off.");
            Check(screen.Contains("Plan: 5 lines (", StringComparison.Ordinal) && !screen.Contains("2. Fix the tokenizer.", StringComparison.Ordinal),
                "the reviewed plan collapses:\n" + screen);
        }),

        Case("tui.plan.approval-stop", async () =>
        {
            await using var run = await TuiRun.Start(PlanView());
            await run.WaitFor("→ Yes, implement it");
            await Press(run, Down, "→ Yes, but don't start yet");
            run.Send("\r");
            await run.Controller.WaitFor("review 14 ApproveAndStop");
            run.Source.Publish(View([.. PlanHistory(), Entry(3, "pi.tool-result", ResultWire("x1", "exit_plan_mode", "The user approved your plan and will tell you when to start.",
                $$"""{"kind":"pisharp.plan-approval","version":1,"state":"approved","plan":{{Quote(PlanText)}},"directory":"/d","stop":true}"""))], live: "{}"));
            await EditorBack(run, "Plan ready for review", "✓ Plan approved; the agent waits for you to say when to start.");
        }),

        Case("tui.plan.approval-reject-feedback", async () =>
        {
            await using var run = await TuiRun.Start(PlanView());
            await run.WaitFor("→ Yes, implement it");
            await Press(run, Down, "→ Yes, but don't start yet");
            await Press(run, Down, "→ No, keep planning (give feedback)");
            var screen = await Press(run, "\r", "What should change in the plan?");
            Check(screen.Contains("The agent stays in plan mode and revises the plan.", StringComparison.Ordinal), "the editor's description:\n" + screen);
            Check(screen.Contains("Fix the parser", StringComparison.Ordinal), "the plan stays shown:\n" + screen);
            // Esc in the feedback editor goes back to the choices.
            await Press(run, "not this", "not this");
            await Press(run, Escape, "→ No, keep planning (give feedback)");
            Check(!run.Controller.Calls.Contains("abort"), "Esc in the editor does not abort");
            await Press(run, "\r", "What should change in the plan?");
            await Press(run, "Add a rollback step.", "Add a rollback step.");
            run.Send("\r");
            await run.Controller.WaitFor("review 14 Reject Add a rollback step.");
            Console.Error.WriteLine("FRAME tui.plan.approval-reject-feedback (after sending)\n" + run.Screen);
            // The agent keeps planning and submits again: a new review.
            run.Source.Publish(View([.. PlanHistory(), Entry(3, "pi.tool-result", ResultWire("x1", "exit_plan_mode", "The user rejected the plan.",
                $$"""{"kind":"pisharp.plan-approval","version":1,"state":"rejected","plan":{{Quote(PlanText)}},"directory":"/d","feedback":"Add a rollback step."}"""))],
                agent: PlanAgent, live: "{}"));
            screen = await EditorBack(run, "Plan ready for review", "✗ Plan rejected; the agent keeps planning.");
            ShowsInOrder(screen, ["✗ Plan rejected; the agent keeps planning.", "Feedback: Add a rollback step."], "the card");
            // "No, keep planning" rejects without feedback.
            run.Source.Publish(PlanView());
            await run.WaitFor("→ Yes, implement it");
            await Press(run, Down, "→ Yes, but don't start yet");
            await Press(run, Down, "→ No, keep planning (give feedback)");
            run.Send(Down);
            await run.WaitUntil(text => text.Split('\n').Any(line => line.Trim() == "→ No, keep planning"), "the last choice");
            run.Send("\r");
            await run.Controller.WaitFor("review 14 Reject");
        }),

        Case("tui.plan.esc-aborts-and-ctrl-d-exits", async () =>
        {
            await using var run = await TuiRun.Start(PlanView());
            await run.WaitFor("→ Yes, implement it");
            run.Send(Escape);
            await run.Controller.WaitFor("abort");
            // In the feedback editor Ctrl+D is the editor's; Esc goes back to the choices.
            await Press(run, Down, "→ Yes, but don't start yet");
            await Press(run, Down, "→ No, keep planning (give feedback)");
            await Press(run, "\r", "What should change in the plan?");
            run.Send("\u0004");
            await Press(run, Escape, "Would you like to proceed?");
            Check(!run.Run.IsCompleted, "still running");
            // Ctrl+D exits with the plan still waiting for review.
            run.Send("\u0004");
            Equal(0, await run.Exited(), "exit code");
            Equal("abort", string.Join("; ", run.Controller.Calls), "calls");
        }),

        Case("tui.question.ctrl-d-exits-with-the-question-waiting", async () =>
        {
            await using var run = await TuiRun.Start(QuestionView(OneQuestion));
            await run.WaitFor("→ 1. ( ) Visitor");
            // In the Other input Ctrl+D is the input's.
            await Press(run, "4", "→ 4. Other:");
            run.Send("\u0004");
            await Press(run, Escape, "→ 4. ( ) Other…");
            Check(!run.Run.IsCompleted, "still running");
            run.Send("\u0004");
            Equal(0, await run.Exited(), "exit code");
            Equal("", string.Join("; ", run.Controller.Calls), "no call");
        }),
    ];
}
