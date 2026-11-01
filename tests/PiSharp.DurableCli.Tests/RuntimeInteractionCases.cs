// Authored for PiSharp (decision 0005, "PiSharp-only additions"; no upstream counterpart): `pisharp durable`'s setup and runtime of
// ask_user_question and plan mode against a Harness on a temporary agent directory with a scripted model. The registry installs
// user-interaction and plan-mode, the default selection leaves plan-mode out, `/plan` enters and leaves it with its notices and directory,
// a question still waiting after a close shows its form again after --continue, and a plan written in plan mode is approved in the TUI.
using System.Text.Json;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Pi;
using PiSharp.Cli.Prompts;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Tools;
using PiSharp.Tui.Pi;

internal static partial class Program
{
    private const string ApproachQuestion = """
        {"questions":[{"question":"Which approach do you prefer?","header":"Approach","options":[{"label":"Visitor","description":"A visitor over the tree"},
        {"label":"Switch","description":"Pattern matching"}],"multiSelect":false}]}
        """;

    private const string DefaultTools = "read,write,edit,bash,ask_user_question,exit_plan_mode,subagent";

    private static IRegistry CodingRegistry(string directory) => DurableHarnessSetup.CreateCodingRegistry(PiSettings.FromObjects(new JsonObject()), directory,
        new DurablePromptResources(directory, directory, new OriginalPromptDocumentation("", "", "")));

    private static async Task<Agent> RootAgent(DurableSession durable) =>
        await (await durable.Harness.ConversationAsync(ConversationId.Root, DurableRuntime.Context))!.AgentAsync(DurableRuntime.Context);

    private static string Names(IEnumerable<ToolRegistration> tools) => string.Join(",", tools.Select(tool => tool.Name));

    /// <summary>The tool name of a tool result message.</summary>
    private static string? ToolNameOf(JsonObjectValue? message) =>
        message is not null && message.TryGetValue("toolName", out var name) && name is JsonStringValue text ? text.Value : null;

    private static IEnumerable<(string, Func<Task>)> RuntimeInteractionCases() =>
    [
        Case("setup.registry-installs-interaction-and-plan", () =>
        {
            var directory = Temp("setup-interaction");
            try
            {
                var snapshot = CodingRegistry(directory).Snapshot();
                Equal("coding-tools,pi-prompt,tool-search,user-interaction,plan-mode", string.Join(",", snapshot.Installed().Select(extension => extension.Name)),
                    "installed extensions");
                Equal("ask_user_question,exit_plan_mode", Names(snapshot.GetExtension(UserInteraction.ExtensionName)?.Tools ?? []), "user-interaction's tools");
                var planMode = snapshot.GetExtension(PlanMode.ExtensionName) ?? throw new InvalidOperationException("plan-mode is not installed");
                Check(planMode.Tools is null or { Count: 0 }, "plan-mode adds no tool");
                Equal("write,edit", string.Join(",", (planMode.Wraps ?? []).OfType<ToolWrap>().Select(wrap => wrap.Tool)), "plan-mode confines write and edit");
                Equal(1, planMode.Sections?.Count ?? 0, "plan-mode's prompt section");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),

        Case("setup.default-selection-excludes-plan-mode", () =>
        {
            var directory = Temp("setup-plan-default");
            try
            {
                var registry = CodingRegistry(directory);
                registry.Install(DurableSubagent.Extension);
                var settings = DurableHarnessSetup.CreateHarnessSettings(PiSettings.FromObjects(new JsonObject()), registry);
                Equal("coding-tools,pi-prompt,user-interaction,subagent", string.Join(",", settings.Extensions!.Select(extension => extension.Name)),
                    "the default selection");
                var agent = Agents.ResolveAgent(null, registry.Snapshot(), Agents.ResolveSettings(settings), error => throw error);
                Equal(DefaultTools, Names(agent.Tools), "the tools offered");
                Check(!agent.Extensions.Any(extension => extension.Name == PlanMode.ExtensionName), "plan-mode not selected");
                // A deferred tool turns tool_search on; plan-mode stays off.
                registry.Install(Define.Extension(new Extension
                {
                    Name = "tickets",
                    Tools = [Define.Tool(new ToolRegistration { Name = "lookup_ticket", Description = "Looks up a ticket.", Deferred = true,
                        Execute = (_, _, _) => Task.FromResult(new ToolExecutionResult()) })],
                }));
                Equal("coding-tools,pi-prompt,tool-search,user-interaction,subagent,tickets",
                    string.Join(",", settings.Extensions!.Select(extension => extension.Name)), "with a deferred tool");
            }
            finally { Directory.Delete(directory, recursive: true); }
        }),

        Case("runtime.plan.toggle-notices-and-directory", async () =>
        {
            await using var agent = new TestAgent("plan-toggle");
            await using var durable = await agent.OpenAsync(ScriptedStreams.Queue());
            var expected = Path.Join(durable.Location.Directory, "plans", ConversationId.Root.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var entered = $"Plan mode: write and edit are limited to {expected}. /plan leaves it.";
            await durable.Controller.SetPlanModeAsync(true, null);
            var view = await UntilView(durable.View, current => DurableTuiState.IsPlanMode(current.Conversation) &&
                current.Notices.Any(notice => notice.Message == entered), "plan mode on");
            Check(Directory.Exists(expected), "the plan directory is created");
            Equal("read,write,edit,bash,ask_user_question,exit_plan_mode", Names((await RootAgent(durable)).Tools), "the plan-mode tools");
            Check((await PlanMode.StateAsync(durable.Harness, ConversationId.Root, DurableRuntime.Context))?.Directory == expected, "the state's directory");
            // Entering again does nothing.
            await durable.Controller.SetPlanModeAsync(true, null);
            Equal(1, durable.View.Current.Notices.Count(notice => notice.Message.StartsWith("Plan mode:", StringComparison.Ordinal)), "one notice");
            await durable.Controller.SetPlanModeAsync(false, null);
            await UntilView(durable.View, current => !DurableTuiState.IsPlanMode(current.Conversation) &&
                current.Notices.Any(notice => notice.Message == "Plan mode off: write and edit work anywhere again."), "plan mode off");
            Equal(DefaultTools, Names((await RootAgent(durable)).Tools), "the tools are back");
            await durable.Controller.SetPlanModeAsync(false, null);
            await UntilView(durable.View, current => current.Notices.Any(notice => notice.Message == "Plan mode is not on."), "already off");
            Check(!view.Notices.Any(notice => notice.Level == DurableNoticeLevel.Error), "no error: " + string.Join(" | ", view.Notices.Select(notice => notice.Message)));
        }),

        Case("runtime.plan.directory-setting", async () =>
        {
            await using var agent = new TestAgent("plan-setting", settings:
                """{"defaultProvider":"faux","defaultModel":"faux-1","defaultThinkingLevel":"low","planMode":{"directory":"~/plans"}}""");
            await using var durable = await agent.OpenAsync(ScriptedStreams.Queue());
            var expected = Path.Join(agent.Home, "plans", $"{durable.Location.Id}-1");
            await durable.Controller.SetPlanModeAsync(true, null);
            await UntilView(durable.View, current => current.Notices.Any(notice =>
                notice.Message == $"Plan mode: write and edit are limited to {expected}. /plan leaves it."), "entered: " + expected);
            Check(Directory.Exists(expected), "created");
            // A relative setting is relative to the session's working directory.
            var relative = PiSettings.FromObjects((JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""{"planMode":{"directory":"../plans"}}""")!);
            Equal(Path.Join(Path.GetFullPath(Path.Combine(durable.Location.Cwd, "../plans")), $"{durable.Location.Id}-3"),
                DurableHarnessSetup.PlanDirectory(durable.Location, new ConversationId(3), relative, agent.Home), "relative");
            Equal(Path.Join(durable.Location.Directory, "plans", "3"),
                DurableHarnessSetup.PlanDirectory(durable.Location, new ConversationId(3), PiSettings.FromObjects(new JsonObject()), agent.Home), "default");
        }),

        // The question waits through a close: --continue reruns the call, which publishes the same pending state, and the TUI shows the form
        // again. Answering it there completes the call once and the turn goes on.
        Case("runtime.question.reopen-shows-pending", async () =>
        {
            await using var agent = new TestAgent("question-reopen");
            var first = new ScriptedStreams(request => ScriptedRequest.Role(request.Last!) == "toolResult"
                ? ScriptedReply.Failure("unexpected request")
                : ScriptedReply.Call(UserQuestions.ToolName, ApproachQuestion, "q1"));
            string directory;
            TaskId waiting;
            await using (var durable = await agent.OpenAsync(first))
            {
                directory = durable.Location.Directory;
                await durable.Controller.SubmitAsync("Refactor the parser.", WhenBusy.Steer);
                var view = await UntilView(durable.View, current => DurableTuiState.PendingInteraction(current.Conversation) is not null, "the question waits");
                waiting = DurableTuiState.PendingInteraction(view.Conversation)!.TaskId;
            }
            var second = new ScriptedStreams(request => ScriptedRequest.Role(request.Last!) == "toolResult" &&
                ScriptedRequest.Text(request.Last).Contains("Answer: Switch", StringComparison.Ordinal)
                    ? ScriptedReply.Answer("Going with pattern matching.")
                    : ScriptedReply.Failure("unexpected request"));
            await using var reopened = await agent.OpenAsync(second, continueSession: true);
            Equal(directory, reopened.Location.Directory, "the same session");
            var terminal = new VirtualTerminal(120, 40);
            var run = StartTui(reopened, terminal, agent.AgentDir);
            var screen = await terminal.WaitForTextAsync("Select one.", 30_000);
            Check(screen.Contains("→ 1. ( ) Visitor — A visitor over the tree", StringComparison.Ordinal), "the form again:\n" + screen);
            Check(screen.Contains("Waiting for your answer... (esc to abort)", StringComparison.Ordinal), "status:\n" + screen);
            Equal(waiting, DurableTuiState.PendingInteraction(reopened.View.Current.Conversation)?.TaskId, "the same call");
            terminal.Send("2");
            await terminal.WaitForTextAsync("Review your answers");
            terminal.Send("\r");
            await terminal.WaitForTextAsync("Going with pattern matching.", 30_000);
            var done = await UntilView(reopened.View, Idle, "idle");
            var results = done.Conversation.Entries.Where(entry => entry.Kind == "pi.tool-result").ToList();
            Equal(1, results.Count, "one result for the call");
            Check(UserText(results[0]).StartsWith("User answered your questions:\n1. [Approach] Which approach do you prefer?\n   Answer: Switch", StringComparison.Ordinal),
                "the result: " + UserText(results[0]));
            screen = await terminal.WaitForAsync(text => text.Contains("✓ [Approach] Switch", StringComparison.Ordinal) && !text.Contains("Select one.", StringComparison.Ordinal),
                "the card shows the answer and the editor is back");
            Equal(1, second.Requests.Count, "one model request after the answer");
            // The call has ended: answering again is a notice, not an error.
            await reopened.Controller.AnswerQuestionAsync(waiting, QuestionResponse.Answered(new QuestionAnswer(["Visitor"])));
            await UntilView(reopened.View, current => current.Notices.Any(notice => notice.Message == "That question is no longer pending."), "not pending");
            Check(!reopened.View.Current.Notices.Any(notice => notice.Level == DurableNoticeLevel.Error),
                "no error notice: " + string.Join(" | ", reopened.View.Current.Notices.Select(notice => notice.Message)));
            terminal.Send("\u0004");
            Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)), "exit code");
        }),

        // End to end: `/plan <text>` enters plan mode and submits; the agent writes its plan into the plan directory and submits it with
        // exit_plan_mode; the TUI shows the review; approving it ends plan mode and the agent goes on.
        Case("runtime.plan.e2e-write-plan-and-approve", async () =>
        {
            await using var agent = new TestAgent("plan-e2e");
            string? planDirectory = null;
            var streams = new ScriptedStreams(request =>
            {
                var last = request.Last!;
                if (ScriptedRequest.Role(last) == "user")
                    return ScriptedReply.Call("write", JsonSerializer.Serialize(new { path = Path.Join(planDirectory, "plan.md"), content = "# Fix the parser\n\n1. Add a failing test.\n" }), "w1");
                return ToolNameOf(last) switch
                {
                    "write" => ScriptedReply.Call(PlanMode.ExitToolName, "{}", "x1"),
                    PlanMode.ExitToolName when ScriptedRequest.Text(last).StartsWith("The user approved your plan.", StringComparison.Ordinal) =>
                        ScriptedReply.Answer("Implementing the approved plan."),
                    _ => ScriptedReply.Failure("unexpected request: " + ScriptedRequest.Text(last)),
                };
            });
            await using var durable = await agent.OpenAsync(streams);
            planDirectory = DurableHarnessSetup.PlanDirectory(durable.Location, ConversationId.Root, durable.Settings, agent.Home);
            var terminal = new VirtualTerminal(400, 50); // wide: the plan paths are long and must not wrap
            var run = StartTui(durable, terminal, agent.AgentDir);
            await terminal.WaitForTextAsync("/compact · /tasks");
            await Type(terminal, "/plan fix the parser");
            var screen = await terminal.WaitForTextAsync("Would you like to proceed?", 30_000);
            TuiTests.ShowsInOrder(screen, ["Plan ready for review", "Plan file: " + Path.Join(planDirectory, "plan.md"), "Fix the parser", "1. Add a failing test."], "the review");
            Check(TuiTests.Flat(screen).Contains("main · plan mode ·", StringComparison.Ordinal), "the badge:\n" + screen);
            Check(screen.Contains($"Plan mode: write and edit are limited to {planDirectory}. /plan leaves it.", StringComparison.Ordinal), "the notice:\n" + screen);
            terminal.Send("\r");
            await terminal.WaitForTextAsync("Implementing the approved plan.", 30_000);
            var done = await UntilView(durable.View, current => Idle(current) && !DurableTuiState.IsPlanMode(current.Conversation), "approved and idle");
            Equal("# Fix the parser\n\n1. Add a failing test.\n", File.ReadAllText(Path.Join(planDirectory, "plan.md")), "the plan file");
            Equal(DefaultTools, Names((await RootAgent(durable)).Tools), "the full tool set again");
            Check(!done.Notices.Any(notice => notice.Level == DurableNoticeLevel.Error), "no error: " + string.Join(" | ", done.Notices.Select(notice => notice.Message)));
            screen = await terminal.WaitForAsync(text => text.Contains("✓ Plan approved; plan mode is off.", StringComparison.Ordinal) &&
                !text.Contains("plan mode ·", StringComparison.Ordinal), "the card's outcome, no badge");
            terminal.Send("\u0004");
            Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)), "exit code");
        }),
    ];
}
