// Authored for PiSharp (no upstream test file): the crash scenarios of the PiSharp-only ask_user_question tool and plan mode (decision 0005,
// "PiSharp-only additions: ask_user_question and plan mode"), run by the crash.* driver (CrashCases.cs, CrashScenarios.cs) on SQLite and
// JSONL:
// - ask-user.waiting: the child is killed while ask_user_question waits for an answer. After reopen the call reruns, publishes the same
//   question as pending again, and the answer the parent gives completes the same call with exactly one result.
// - ask-user.answered: the child is killed right after the answer's commit landed, before the call settled. After reopen the rerun finds the
//   stored answer and settles once, without waiting.
// - plan-approval.approved: the child is killed right after the approval's commit landed. The approval, the restored agent and the plan
//   state were one commit, so after reopen plan mode is off, and the rerun of exit_plan_mode returns the approval once.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using static ChatTests;
using static Program;
using static SessionTests;

/// <summary>The ask-user and plan-approval crash scenarios.</summary>
internal static class InteractionCrash
{
    private static readonly DurableContext Ctx = DurableContext.Background;

    /// <summary>The "Other" text of the answer whose commit the answered checkpoint kills at; no earlier commit contains it.</summary>
    private const string AnswerToken = "zebra-crash-token";

    internal static bool Uses(string scenario) => scenario is "ask-user" or "plan-approval";

    private static string Project(string workDir) => Path.Combine(workDir, "project");

    private static string Plans(string workDir) => Path.Combine(workDir, "plans", "1");

    private static readonly UserQuestion Color = new("Which color should the button be?", "Color",
        [new QuestionOption("Red", "Warm"), new QuestionOption("Blue", "Cool")], false);

    /// <summary>The environment of the scenario's conversations: the project directory under the storage's directory.</summary>
    internal static Func<EnvTarget, DurableContext, Task<IExecutionEnv?>>? Env(string scenario, string workDir)
    {
        if (!Uses(scenario)) return null;
        Directory.CreateDirectory(Project(workDir));
        Directory.CreateDirectory(Plans(workDir));
        var env = new LocalExecutionEnv(Project(workDir));
        return (_, _) => Task.FromResult<IExecutionEnv?>(env);
    }

    /// <summary>The commit the checkpoint kills after.</summary>
    internal static CrashScenarios.CommitTrigger? Trigger(string scenario, string checkpoint) => (scenario, checkpoint) switch
    {
        ("ask-user", "answered") => new(Contains(AnswerToken), true),
        ("plan-approval", "approved") => new(Contains("\"approved\""), true),
        _ => null,
    };

    private static Func<IReadOnlyList<StorageWrite>, bool> Contains(string text) =>
        writes => writes.Any(write => write.ToJson().ToJson().Contains(text, StringComparison.Ordinal));

    /// <summary>The registry and faux model, identical in the child and the parent.</summary>
    internal static ChatSetup BuildSetup(string scenario, string workDir)
    {
        var setup = new ChatSetup(new FauxModels(new FauxOptions { Api = "faux-crash" }));
        setup.Registry.Install(CodingTools.CreateCodingTools());
        setup.Registry.Install(UserInteraction.CreateExtension());
        setup.Registry.Install(PlanMode.CreateExtension());
        InteractionTests.DefaultSelection(setup);
        InteractionTests.Script(setup, (_, results) => scenario == "ask-user"
            ? results.Count == 0 ? InteractionTests.Call((UserQuestions.ToolName, InteractionTests.Questions(Color), "q1")) : InteractionTests.Done
            : results.Count switch
            {
                0 => PlanModeTests.Write(Path.Combine(Plans(workDir), "plan.md"), PlanModeTests.PlanText, "w1"),
                1 => PlanModeTests.Exit(),
                _ => InteractionTests.Done,
            });
        return setup;
    }

    /// <summary>The child's steps: start the turn and wait for its interaction, then stop at the checkpoint or give the response the
    /// storage checkpoint kills after.</summary>
    internal static async Task RunAsync(string scenario, string checkpoint, CrashGate gate, ChatSetup setup, IConversation root, string workDir)
    {
        if (scenario == "plan-approval") await PlanMode.EnterAsync(root, setup.Registry, Plans(workDir), Ctx);
        await root.SubmitAsync(SubmissionDraft.Input(scenario == "ask-user" ? "go" : "plan", requestId: "r1"), Ctx);
        var pending = (await InteractionTests.WaitPending(root))[0];
        switch (checkpoint)
        {
            case "waiting":
                await gate.At("waiting");
                break;
            case "answered":
                await UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer([], AnswerToken)), Ctx);
                break;
            case "approved":
                await PlanMode.ApproveAsync(root, setup.Registry, pending.TaskId, stop: false, Ctx);
                break;
        }
    }

    // ─── Parent assertions ──────────────────────────────────────────────────

    private static async Task<List<JsonObjectValue>> Results(CrashTests.Recovery recovery, string tool) =>
        [.. ToolsTests.Results(await AllEntries(recovery.Root)).Where(result => S(result["toolName"]) == tool)];

    private static async Task Finished(CrashTests.Recovery recovery)
    {
        await recovery.Idle();
        var all = await AllEntries(recovery.Root);
        Seq(["pi.user"], all.Where(entry => entry.Kind == "pi.user").Select(entry => entry.Kind), "one user entry");
        var answers = all.Where(entry => entry.Kind == "pi.assistant" && S(((JsonObjectValue)entry.Model![0])["stopReason"]) == "stop").ToList();
        Equal(1, answers.Count, "one answer");
        Equal("done", TextOf(answers[0].Model![0]), "final answer");
        var submission = await recovery.ByRequest("r1");
        Equal(SubmissionStatus.Done, submission!.Status, "submission");
        Equal(0, (await UserInteraction.PendingAsync(recovery.Root, Ctx)).Count, "nothing pending");
        // Only the request after the tool result is made after reopen.
        Equal(1, recovery.FauxCalls, "requests after reopen");
    }

    private static async Task CompletedTool(CrashTests.Recovery recovery, string tool)
    {
        var tasks = (await recovery.Harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = "pi.tool" }, 20), Ctx)).Items;
        foreach (var task in tasks) MatchJson("""{"status":"completed"}""", OutcomeJson(task), "tool outcome");
        Check(tasks.Any(), "tool tasks");
    }

    internal static async Task VerifyWaiting(CrashTests.Recovery recovery)
    {
        recovery.Harness.Resume();
        var pending = (await InteractionTests.WaitPending(recovery.Root))[0];
        Equal(UserQuestions.DetailsKind, pending.Kind, "kind");
        Equal("q1", pending.CallId, "the same call");
        await UserQuestions.AnswerAsync(recovery.Root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Blue"])), Ctx);
        await Finished(recovery);
        var results = await Results(recovery, UserQuestions.ToolName);
        Equal(1, results.Count, "exactly one result");
        Equal("User answered your questions:\n1. [Color] Which color should the button be?\n   Answer: Blue\nContinue with these answers in mind.",
            InteractionTests.Text(results[0]), "result");
        await CompletedTool(recovery, UserQuestions.ToolName);
    }

    internal static async Task VerifyAnswered(CrashTests.Recovery recovery)
    {
        recovery.Harness.Resume();
        await Finished(recovery);
        var results = await Results(recovery, UserQuestions.ToolName);
        Equal(1, results.Count, "exactly one result");
        Check(InteractionTests.Text(results[0]).Contains($"Other: \"{AnswerToken}\"", StringComparison.Ordinal), "the stored answer: " + InteractionTests.Text(results[0]));
        await CompletedTool(recovery, UserQuestions.ToolName);
    }

    internal static async Task VerifyApproved(CrashTests.Recovery recovery)
    {
        // The approval commit was atomic: plan mode is already off before anything runs again.
        var state = await PlanMode.StateAsync(recovery.Harness, recovery.Root.Id, Ctx);
        Check(state is { Active: false } && state.Approved is not null, "approved before reopen ran anything");
        Check(!PlanMode.IsActive(await recovery.Harness.SnapshotAsync(Agents.AgentDoc, recovery.Root.Id, Ctx)), "agent restored");
        recovery.Harness.Resume();
        await Finished(recovery);
        var exits = await Results(recovery, PlanMode.ExitToolName);
        Equal(1, exits.Count, "exactly one approval result");
        Equal(PlanModeTests.Approved(Path.Combine(Path.GetFullPath(Plans(recovery.WorkDir)), "plan.md"), PlanModeTests.PlanText),
            InteractionTests.Text(exits[0]), "result");
        Equal(1, (await Results(recovery, "write")).Count, "one write result");
        Equal("approved", (await PlanMode.StateAsync(recovery.Harness, recovery.Root.Id, Ctx))!.Submissions.Single().Status, "submission");
        await CompletedTool(recovery, PlanMode.ExitToolName);
    }
}
