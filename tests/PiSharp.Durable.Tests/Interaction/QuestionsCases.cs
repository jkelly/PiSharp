// Authored for PiSharp (no upstream test file): the questions.* cases of the PiSharp-only ask_user_question tool (decision 0005,
// "PiSharp-only additions: ask_user_question and plan mode"). The flow cases run on memory (questions.*), JSONL (questions.jsonl.*) and
// SQLite (questions.sqlite.*); questions.schema and questions.registration need no storage.
using System.Collections.Immutable;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Internal.PiAi.Validation;
using PiSharp.Durable.Tools;
using static ChatTests;
using static InteractionTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> QuestionsCases() =>
    [
        .. QuestionsTests.Static("questions."),
        .. InteractionTests.Backends().SelectMany(backend => QuestionsTests.Flows("questions." + backend.Prefix, backend.Location)),
    ];
}

/// <summary>The questions.* cases.</summary>
internal static class QuestionsTests
{
    private static JsonObjectValue Ask(UserQuestion question, string id = "q1") => Call((UserQuestions.ToolName, Questions(question), id));

    /// <summary>Asks <paramref name="question"/> once per turn; answers "done" once the result is in.</summary>
    private static void AskOnce(ChatSetup setup, params UserQuestion[] questions) =>
        Script(setup, (_, results) => results.Count == 0 ? Call((UserQuestions.ToolName, Questions(questions), "q1")) : Done);

    internal static IEnumerable<(string, Func<Task>)> Static(string prefix)
    {
        yield return Case(prefix + "schema", () =>
        {
            var tool = UserQuestions.CreateTool();
            Equal("""{"type":"object","required":["questions"],"properties":{"questions":{"type":"array","items":{"type":"object","required":["question","header","options","multiSelect"],"properties":{"question":{"type":"string","description":"The complete question to ask the user, clear and specific."},"header":{"type":"string","maxLength":12,"description":"A very short label for the question, shown as a tab (at most 12 characters), for example \"Auth method\"."},"options":{"type":"array","items":{"type":"object","required":["label","description"],"properties":{"label":{"type":"string","description":"The choice as the user sees and selects it, in a few words."},"description":{"type":"string","description":"What this choice means or what happens if it is chosen."}}},"minItems":2,"maxItems":4,"description":"The 2-4 choices. Do not add an \"Other\" option; the user can always answer in their own words."},"multiSelect":{"type":"boolean","description":"Whether the user may select more than one option."}}},"minItems":1,"maxItems":4,"description":"The 1-4 questions to ask."}}}""",
                tool.Parameters.ToJson(), "parameters");
            Check(tool.Description.StartsWith("Ask the user 1-4 multiple-choice questions and wait for the answers.", StringComparison.Ordinal) &&
                tool.Description.EndsWith("call exit_plan_mode.", StringComparison.Ordinal), "description");
            bool Valid(JsonValue args)
            {
                var prepared = tool.PrepareArguments!(args);
                return ToolArgumentValidation.ValidateJson(tool.Name, tool.Parameters.ToJson(), prepared.ToJson(), tool.ParametersOrigin).IsValid;
            }
            Check(Valid(Questions(Color)), "one question");
            Check(Valid(Questions(Color, Features, Question("Scope", "How far?", false, "A", "B"), Question("Docs", "Docs?", false, "Yes", "No"))), "four questions");
            Check(!Valid(Questions(Color, Features, Question("A", "a?", false, "x", "y"), Question("B", "b?", false, "x", "y"), Question("C", "c?", false, "x", "y"))),
                "five questions");
            Check(!Valid(Obj(("questions", JsonValue.Array()))), "no question");
            Check(!Valid(Questions(Question("A header too long", "Long?", false, "x", "y"))), "header over 12 characters");
            Check(!Valid(Questions(Question("One", "One option?", false, "x"))), "one option");
            Check(!Valid(Questions(Question("Five", "Five options?", false, "a", "b", "c", "d", "e"))), "five options");
            // Repairs: questions as a JSON string, a single question object, a missing multiSelect.
            var single = Color.ToJson().Remove("multiSelect");
            var repaired = (JsonObjectValue)tool.PrepareArguments!(Obj(("questions", single.ToJson())));
            Equal(Questions(Color).ToJson(), repaired.ToJson(), "repaired arguments");
            Check(Valid(Obj(("questions", single))), "single object");
        });

        yield return Case(prefix + "registration", () =>
        {
            var extension = UserInteraction.CreateExtension();
            Equal("user-interaction", extension.Name, "extension name");
            Seq(["ask_user_question", "exit_plan_mode"], extension.Tools!.Select(tool => tool.Name), "tools");
            foreach (var tool in extension.Tools!)
            {
                Equal(ToolReplay.Safe, tool.Replay, tool.Name + " replay");
                Seq([ToolCaller.Model], tool.Callers!.Value, tool.Name + " callers");
                Check(!tool.Deferred, tool.Name + " is offered upfront");
            }
            Equal("pisharp.interaction", UserInteraction.InteractionDoc.Definition.Kind, "document kind");
            Equal(DocumentScopeKind.Task, UserInteraction.InteractionDoc.Definition.Scope, "document scope");
            Equal("pisharp.questions", UserQuestions.DetailsKind, "details kind");
            var plan = PlanMode.CreateExtension();
            Equal("plan-mode", plan.Name, "plan extension");
            Check(plan.Tools is null or { Count: 0 }, "plan-mode has no tools");
            Seq(["plan_mode"], plan.Sections!.Select(section => section.Key), "plan-mode sections");
            Seq(["write", "edit"], plan.Wraps!.OfType<ToolWrap>().Select(wrap => wrap.Tool), "plan-mode wraps");
            Equal("pisharp.plan-mode", PlanMode.StateDoc.Definition.Kind, "state kind");
            Equal(DocumentFork.AsOf, PlanMode.StateDoc.Definition.Fork, "state fork");
            Seq(["read", "write", "edit", "bash", "powershell", "ask_user_question", "exit_plan_mode"], PlanMode.DefaultAllowedTools, "allowed tools");
        });
    }

    internal static IEnumerable<(string, Func<Task>)> Flows(string prefix, Func<Func<IStorage>> location)
    {
        yield return Case(prefix + "pending-committed-before-shown", async () =>
        {
            var open = location();
            var setup = NewSetup();
            var dirs = new Dirs("questions-pending");
            AskOnce(setup, Color, Features);
            var (harness, root) = await Open(open, setup, dirs);
            // The first view revision that shows the question: by then its document is committed and the slot's pending details are durable.
            var seen = new TaskCompletionSource<PendingInteraction>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var state = await root.ViewStateAsync(Ctx);
            using var subscription = state.Subscribe((view, _, _) =>
            {
                if (UserInteraction.PendingIn(view) is [var first, ..]) seen.TrySetResult(first);
            });
            var submission = await Submit(root, "go");
            var pending = await seen.Task.WaitAsync(InteractionTests.Timeout);
            var doc = await harness.SnapshotAsync(UserInteraction.InteractionDoc, pending.TaskId, Ctx);
            Equal(UserQuestions.DetailsKind, pending.Kind, "kind");
            Equal("q1", pending.CallId, "call id");
            Equal(root.Id, pending.ConversationId, "conversation");
            MatchJson("""{"kind":"pisharp.questions","version":1,"state":"pending"}""", pending.Details, "details");
            Seq([Color.ToJson().ToJson(), Features.ToJson().ToJson()], UserQuestions.QuestionsOf(pending.Details).Select(question => question.ToJson().ToJson()), "questions");
            MatchJson("""{"kind":"questions","createdAt":"<number>"}""", doc, "document");
            Check(!doc!.ContainsKey("response"), "no response yet");
            if (open() is ControlledStorage controlled)
            {
                var stored = await controlled.Inner.ScanDocumentsAsync(new DocumentQuery(DocumentScope.Task(pending.TaskId), DocumentPoint.Current), 10, null, Ctx);
                Check(stored.Items.Any(record => record.Kind == "pisharp.interaction"), "the document is in storage");
            }
            await UserQuestions.AnswerAsync(root, pending.TaskId,
                QuestionResponse.Answered(new QuestionAnswer(["Green"]), new QuestionAnswer(["Search", "Export"])), Ctx);
            Equal(SubmissionStatus.Done, (await Settled(submission)).Status, "submission");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "answer-single", async () =>
        {
            var setup = NewSetup();
            AskOnce(setup, Color);
            var (harness, root) = await Open(location(), setup, new Dirs("answer-single"));
            var (submission, pending) = await UntilPending(root);
            await UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Blue (Recommended)"])), Ctx);
            Equal(SubmissionStatus.Done, (await Settled(submission)).Status, "submission");
            var result = (await Results(root)).Single();
            Check(!IsError(result), "not an error");
            Equal("User answered your questions:\n1. [Color] Which color should the button be?\n   Answer: Blue (Recommended)\n" +
                "Continue with these answers in mind.", Text(result), "result");
            MatchJson("""{"details":{"kind":"pisharp.questions","version":1,"state":"answered","answers":[{"selected":["Blue (Recommended)"]}]}}""", result, "details");
            Equal("done", TextOf((await AllEntries(root)).Last(entry => entry.Kind == "pi.assistant").Model![0]), "final answer");
            Equal(0, (await UserInteraction.PendingAsync(root, Ctx)).Count, "nothing pending");
            await NoLiveTasks(harness);
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "answer-multi", async () =>
        {
            var setup = NewSetup();
            AskOnce(setup, Features);
            var (harness, root) = await Open(location(), setup, new Dirs("answer-multi"));
            var (submission, pending) = await UntilPending(root);
            // Selected out of order; the result lists them in the question's order.
            await UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Sharing", "Search"])), Ctx);
            await Settled(submission);
            Equal("User answered your questions:\n1. [Features] Which features should ship first?\n   Answer: Search, Sharing\n" +
                "Continue with these answers in mind.", Text((await Results(root)).Single()), "result");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "answer-other", async () =>
        {
            var setup = NewSetup();
            AskOnce(setup, Color, Features);
            var (harness, root) = await Open(location(), setup, new Dirs("answer-other"));
            var (submission, pending) = await UntilPending(root);
            await UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(
                new QuestionAnswer([], "  Teal, to match the logo  "), new QuestionAnswer(["Export"], "and a CLI")), Ctx);
            await Settled(submission);
            var result = (await Results(root)).Single();
            Equal("User answered your questions:\n1. [Color] Which color should the button be?\n   Other: \"Teal, to match the logo\"\n" +
                "2. [Features] Which features should ship first?\n   Answer: Export\n   Other: \"and a CLI\"\nContinue with these answers in mind.",
                Text(result), "result");
            MatchJson("""{"details":{"answers":[{"selected":[],"other":"Teal, to match the logo"},{"selected":["Export"],"other":"and a CLI"}]}}""", result, "details");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "answer-validation", async () =>
        {
            var setup = NewSetup();
            AskOnce(setup, Color, Features);
            var (harness, root) = await Open(location(), setup, new Dirs("answer-validation"));
            var (submission, pending) = await UntilPending(root);
            var id = pending.TaskId;
            async Task Rejected(string what, params QuestionAnswer[] answers) =>
                await ThrowsAsync<ArgumentException>(() => UserQuestions.AnswerAsync(root, id, QuestionResponse.Answered(answers), Ctx), what);
            await Rejected("too few answers", new QuestionAnswer(["Green"]));
            await Rejected("too many answers", new QuestionAnswer(["Green"]), new QuestionAnswer(["Search"]), new QuestionAnswer(["Search"]));
            await Rejected("unknown label", new QuestionAnswer(["Purple"]), new QuestionAnswer(["Search"]));
            await Rejected("label case differs", new QuestionAnswer(["green"]), new QuestionAnswer(["Search"]));
            await Rejected("two labels on single select", new QuestionAnswer(["Green", "Red"]), new QuestionAnswer(["Search"]));
            await Rejected("label and other on single select", new QuestionAnswer(["Green"], "teal"), new QuestionAnswer(["Search"]));
            await Rejected("nothing on single select", new QuestionAnswer([], "   "), new QuestionAnswer(["Search"]));
            await Rejected("nothing on multi select", new QuestionAnswer(["Green"]), new QuestionAnswer([]));
            await Rejected("duplicate label", new QuestionAnswer(["Green"]), new QuestionAnswer(["Search", "Search"]));
            await Rejected("other too long", new QuestionAnswer([], new string('x', 4001)), new QuestionAnswer(["Search"]));
            await ThrowsAsync<ArgumentException>(() => UserQuestions.AnswerAsync(root, new TaskId(999_999), QuestionResponse.Declined, Ctx), "unknown task");
            // Nothing landed: the call still waits, and a valid answer completes it.
            Check((await harness.SnapshotAsync(UserInteraction.InteractionDoc, id, Ctx))?.ContainsKey("response") == false, "no response stored");
            Equal(1, (await UserInteraction.PendingAsync(root, Ctx)).Count, "still pending");
            await UserQuestions.AnswerAsync(root, id, QuestionResponse.Answered(new QuestionAnswer([], new string('x', 4000)), new QuestionAnswer(["Search"])), Ctx);
            Equal(SubmissionStatus.Done, (await Settled(submission)).Status, "submission");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "answer-idempotent", async () =>
        {
            var setup = NewSetup();
            AskOnce(setup, Color);
            var storage = new ControlledStorage(location()());
            var (harness, root) = await Open(() => storage, setup, new Dirs("answer-idempotent"));
            var (submission, pending) = await UntilPending(root);
            // The same answer twice while the call is still open (the first answer's commit is held, so the second queues behind it).
            var hold = storage.HoldCommits();
            var first = UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Red"])), Ctx);
            await hold.Entered;
            var again = UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Red"], "  ")), Ctx);
            hold.Release();
            await first;
            await again;
            await Settled(submission);
            var results = await Results(root);
            Equal(1, results.Count, "one result");
            Check(Text(results[0]).Contains("Answer: Red", StringComparison.Ordinal), "answer");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "answer-conflict", async () =>
        {
            var setup = NewSetup();
            AskOnce(setup, Color);
            var storage = new ControlledStorage(location()());
            var dirs = new Dirs("answer-conflict");
            var (harness, root) = await Open(() => storage, setup, dirs);
            var (submission, pending) = await UntilPending(root);
            // Two hosts answer at once: the second commit sees the first answer. The tool's result commit is held, so the call is still open.
            var hold = storage.HoldCommits();
            var first = UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Red"])), Ctx);
            await hold.Entered;
            var same = UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Red"])), Ctx);
            var other = UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Green"])), Ctx);
            hold.Release();
            await first;
            await same;
            await ThrowsAsync<InteractionNotPendingException>(() => other, "a different second answer");
            await Settled(submission);
            Check(Text((await Results(root)).Single()).Contains("Answer: Red", StringComparison.Ordinal), "the first answer wins");
            await ThrowsAsync<InteractionNotPendingException>(() =>
                UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Red"])), Ctx), "answer after settle");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "decline", async () =>
        {
            var setup = NewSetup();
            AskOnce(setup, Color);
            var (harness, root) = await Open(location(), setup, new Dirs("decline"));
            var (submission, pending) = await UntilPending(root);
            await UserQuestions.DeclineAsync(root, pending.TaskId, Ctx);
            await Settled(submission);
            var result = (await Results(root)).Single();
            Check(!IsError(result), "a decline is not an error");
            Equal("The user declined to answer these questions. Proceed with your best judgement and state the assumptions you make, or ask in " +
                "plain text if you cannot proceed.", Text(result), "result");
            MatchJson("""{"details":{"kind":"pisharp.questions","state":"declined"}}""", result, "details");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "abort", async () =>
        {
            var setup = NewSetup();
            AskOnce(setup, Color);
            var (harness, root) = await Open(location(), setup, new Dirs("abort"));
            var (submission, pending) = await UntilPending(root);
            await root.AbortAsync(Ctx);
            MatchJson("""{"status":"unanswered","reason":"aborted"}""", (await Settled(submission)).ToJson(), "submission");
            var result = (await Results(root)).Single();
            Check(IsError(result) && Text(result).Contains("[error] Tool ask_user_question was aborted", StringComparison.Ordinal), "aborted result: " + Text(result));
            Equal(0, (await UserInteraction.PendingAsync(root, Ctx)).Count, "nothing pending");
            await NoLiveTasks(harness);
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "answer-after-abort", async () =>
        {
            var setup = NewSetup();
            AskOnce(setup, Color);
            var (harness, root) = await Open(location(), setup, new Dirs("answer-after-abort"));
            var (submission, pending) = await UntilPending(root);
            await root.AbortAsync(Ctx);
            await Settled(submission);
            await ThrowsAsync<InteractionNotPendingException>(() =>
                UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Red"])), Ctx), "answer");
            await ThrowsAsync<InteractionNotPendingException>(() => UserQuestions.DeclineAsync(root, pending.TaskId, Ctx), "decline");
            Equal(1, (await Results(root)).Count, "one result");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "restart-while-waiting", async () =>
        {
            var open = location();
            var setup = NewSetup();
            var dirs = new Dirs("restart-while-waiting");
            AskOnce(setup, Color);
            var (harness, root) = await Open(open, setup, dirs);
            var (submission, pending) = await UntilPending(root);
            var submissionId = submission.Id;
            await harness.CloseAsync(Ctx);

            (harness, root) = await Open(open, setup, dirs, first: false);
            // The rerun publishes the same call as pending again.
            var again = (await WaitPending(root))[0];
            Equal(pending.TaskId, again.TaskId, "same tool task");
            Equal(pending.CallId, again.CallId, "same call");
            await UserQuestions.AnswerAsync(root, again.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Green"])), Ctx);
            Equal(SubmissionStatus.Done, (await Settled((await harness.SubmissionAsync(submissionId, Ctx))!)).Status, "submission");
            var results = await Results(root);
            Equal(1, results.Count, "exactly one result");
            Check(Text(results[0]).Contains("Answer: Green", StringComparison.Ordinal), "answer");
            var tool = (await harness.GetTaskAsync(pending.TaskId, Ctx))!;
            MatchJson("""{"status":"completed"}""", OutcomeJson(tool), "tool outcome");
            await NoLiveTasks(harness);
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "answered-then-settle-fails", async () =>
        {
            var open = location();
            var setup = NewSetup();
            var dirs = new Dirs("settle-fails");
            AskOnce(setup, Color);
            var store = open();
            var (harness, root) = await OpenAndResume(FailingSettle(store), setup, dirs);
            var (submission, pending) = await UntilPending(root);
            var submissionId = submission.Id;
            // The answer lands; the result commit fails in Storage, which fails and closes the Session.
            await UserQuestions.AnswerAsync(root, pending.TaskId, QuestionResponse.Answered(new QuestionAnswer(["Red"])), Ctx);
            await ClosedByFailure(harness);
            Equal(0, ToolsTests.Results(await StoredEntries(open)).Count, "no result landed");

            (harness, root) = await Open(open, setup, dirs, first: false);
            Equal(SubmissionStatus.Done, (await Settled((await harness.SubmissionAsync(submissionId, Ctx))!)).Status, "submission");
            var results = await Results(root);
            Equal(1, results.Count, "exactly one result");
            Check(Text(results[0]).Contains("Answer: Red", StringComparison.Ordinal), "the stored answer");
            await NoLiveTasks(harness);
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "parallel-two", async () =>
        {
            var setup = NewSetup();
            Script(setup, (_, results) => results.Count == 0
                ? Call((UserQuestions.ToolName, Questions(Color), "q1"), (UserQuestions.ToolName, Questions(Features), "q2"))
                : Done);
            var (harness, root) = await Open(location(), setup, new Dirs("parallel-two"));
            var submission = await Submit(root, "go");
            var pending = await WaitPending(root, 2);
            Check(pending[0].TaskId.Value < pending[1].TaskId.Value, "lowest task first");
            Seq(["q1", "q2"], pending.Select(item => item.CallId), "calls");
            // The second first: each call waits only for its own answer.
            await UserQuestions.AnswerAsync(root, pending[1].TaskId, QuestionResponse.Answered(new QuestionAnswer(["Export"])), Ctx);
            var left = await WaitPending(root, 1);
            Equal(pending[0].TaskId, left[0].TaskId, "the first still waits");
            await UserQuestions.AnswerAsync(root, pending[0].TaskId, QuestionResponse.Answered(new QuestionAnswer(["Green"])), Ctx);
            Equal(SubmissionStatus.Done, (await Settled(submission)).Status, "submission");
            var results = await Results(root);
            Equal(2, results.Count, "two results");
            var byCall = results.ToDictionary(result => S(result["toolCallId"]));
            Check(Text(byCall["q1"]).Contains("Answer: Green", StringComparison.Ordinal), "q1");
            Check(Text(byCall["q2"]).Contains("Answer: Export", StringComparison.Ordinal), "q2");
            Equal(2, setup.Faux.State.CallCount, "requests");
            await harness.CloseAsync(Ctx);
        });

        yield return Case(prefix + "invalid-questions", async () =>
        {
            var setup = NewSetup();
            var bad = new[]
            {
                Question("Dup", "Duplicate labels?", false, "Same", "same"),
                Question("Other", "An Other option?", false, "Yes", "Other"),
                Question("Blank", "   ", false, "Yes", "No"),
            };
            var turn = 0;
            Script(setup, (_, results) => results.Count == 0
                ? Call((UserQuestions.ToolName, Questions(bad[Interlocked.Increment(ref turn) - 1]), "q1"))
                : Done);
            var (harness, root) = await Open(location(), setup, new Dirs("invalid-questions"));
            for (var index = 0; index < bad.Length; index++) await Settled(await Submit(root, $"go {index}"));
            var results = await Results(root);
            Equal(3, results.Count, "results");
            Check(results.All(IsError), "every result is an error");
            Check(Text(results[0]).Contains("repeats the option", StringComparison.Ordinal), Text(results[0]));
            Check(Text(results[1]).Contains("\"Other\" option", StringComparison.Ordinal), Text(results[1]));
            Check(Text(results[2]).Contains("question 1 is empty", StringComparison.Ordinal), Text(results[2]));
            var tasks = (await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery { Kind = "pi.tool" }, 10), Ctx)).Items;
            Check(tasks.All(task => OutcomeJson(task) is JsonObjectValue outcome && S(outcome["status"]) == "completed"), "the calls completed");
            await harness.CloseAsync(Ctx);
        });
    }

    /// <summary>Opens and resumes over a given storage.</summary>
    private static async Task<OpenedChat> OpenAndResume(IStorage storage, ChatSetup setup, Dirs dirs) => await Open(() => storage, setup, dirs);

    /// <summary>Every committed entry, read straight from a reopened storage.</summary>
    private static async Task<List<EntryRecord>> StoredEntries(Func<IStorage> open)
    {
        var storage = open();
        try
        {
            var page = await storage.ScanEntriesAsync(new EntryQuery(ConversationId.Root), 1000, null, Ctx);
            return [.. page.Items];
        }
        finally
        {
            if (storage is not ControlledStorage) await storage.CloseAsync(Ctx);
        }
    }
}
