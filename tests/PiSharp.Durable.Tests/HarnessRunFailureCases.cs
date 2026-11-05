// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-run-failure.test.ts (added in f3255de76), ported in the Pi
// v1.5.0 sync (area run-failure): pi.live.runFailure, why the newest run ended without an answer, as state a renderer shows (#11327).
// describe("pi.live.runFailure") is harness.run-failure.*, on MemoryStorage as upstream. Verbatim apart from the adaptations in
// ChatSupport.cs and HarnessSupport.cs, and these:
// - live() reads harness.snapshot(LiveDoc) as a JSON object, {} when absent. toEqual is ChatTests.MatchExactly and toMatchObject MatchJson,
//   with "<number>" for expect.any(Number); `not.toHaveProperty("detail")` checks the record's Detail is null.
// - runFailure.first (7e15f9601, area run-end: the failed run's pi.live.run.first) is "<number>" in the two toEqual checks.
// - The tool's parameters are pi-schema's obj({}) (6536f8012), the JSON Schema of an object without properties.
// PiSharp-only cases, authored: harness.run-failure.pisharp.reads-and-writes-every-reason-in-upstream-spelling (RunFailureReason declares every
// v1.5.0 value once, the work list's rule), and harness.run-failure.pisharp.the-event-stream-carries-the-run-failure: the PiSharp-only event
// projection (PiSharp.Durable.Hosting's AgentEvents.cs since area events) carries pi.live.runFailure in its snapshot and on the run_end
// of a run that ended without an answer, a continue's run_start in the same batch. Its wire keeps area run-failure's {taskId, inputs, reason,
// detail?}: AgentEvents.cs leaves runFailure's first (area run-end) off, as it does run.turns.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessRunFailureCases() => RunFailureTests.Cases("harness.run-failure.");
}

/// <summary>The harness.run-failure.* cases (upstream harness-run-failure.test.ts).</summary>
internal static class RunFailureTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly JsonObjectValue NoParameters = O("""{"type":"object","properties":{}}""");

    /// <summary>The conversation's <c>pi.live</c>, <c>{}</c> when absent (upstream <c>live</c>).</summary>
    private static async Task<JsonObjectValue> LiveOf(IHarness harness, IConversation conversation) =>
        await harness.SnapshotAsync(Live.LiveDoc, conversation.Id, Context) ?? JsonObjectValue.Empty;

    private static JsonValue? Field(JsonObjectValue value, string key) => value.TryGetValue(key, out var item) ? item : null;

    /// <summary>A response that fails for good once <c>Gate</c> opens; <c>Reached</c> completes when it was requested (upstream
    /// <c>failingOnce</c>).</summary>
    private sealed record FailingOnce(FauxStep Step, Deferred Gate, Deferred Reached);

    private static FailingOnce Failing()
    {
        var gate = new Deferred();
        var reached = new Deferred();
        FauxStep step = new FauxResponseFactory(async _ =>
        {
            reached.Resolve();
            await gate.Task;
            return FauxAssistantMessage([], stopReason: "error", errorMessage: "Invalid request");
        });
        return new FailingOnce(step, gate, reached);
    }

    private static void Settled(SubmissionRecord record, SubmissionStatus status, string reason, string what) =>
        Check(record.Status == status && record.Reason == reason, $"{what}: expected {status} {reason}, actual {record.Status} {record.Reason}");

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "records-a-model-error-with-its-text-and-a-later-run-clears-it", async () =>
        {
            var setup = Setup();
            setup.Faux.SetResponses(FauxAssistantMessage([], stopReason: "error", errorMessage: "Invalid request"), FauxAssistantMessage([FauxText("fine")]));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            var settled = await submission.WaitAsync(Context);
            Settled(settled, SubmissionStatus.Unanswered, "model_error", "settled");
            SameJson("\"Invalid request\"", settled.Detail, "settled detail");
            var failure = Field(await LiveOf(harness, root), "runFailure");
            MatchExactly($$"""{"taskId":"<number>","inputs":[{{submission.Id.Value}}],"reason":"model_error","detail":"Invalid request","first":"<number>"}""", failure,
                "runFailure");
            // The typed reading of the same value.
            var typed = Live.RunFailureOf(await LiveOf(harness, root))!;
            Equal(RunFailureReason.ModelError, typed.Reason, "typed reason");
            Seq([submission.Id], typed.Inputs, "typed inputs");
            var next = await root.SubmitAsync(SubmissionDraft.Input("again"), Context);
            // The commit that starts the next run removes it.
            Equal<JsonValue?>(null, Field(await LiveOf(harness, root), "runFailure"), "runFailure after the next run starts");
            Equal(SubmissionStatus.Done, (await next.WaitAsync(Context)).Status, "next settled");
            SameJson("{}", await LiveOf(harness, root), "live");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "records-a-missing-model-with-which-one", async () =>
        {
            var setup = Setup();
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await root.ConfigureAsync(new AgentChange { Model = new ModelRef("faux", "gone") }, Context);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            var settled = await submission.WaitAsync(Context);
            Settled(settled, SubmissionStatus.Unanswered, "no_model", "settled");
            // The settlement is unchanged: no detail.
            Equal<JsonValue?>(null, settled.Detail, "settled detail");
            MatchJson($$"""{"inputs":[{{submission.Id.Value}}],"reason":"no_model","detail":"Model faux/gone is not available"}""",
                Field(await LiveOf(harness, root), "runFailure"), "runFailure");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "records-an-abort", async () =>
        {
            var setup = Setup();
            var (step, reached) = Unanswered();
            setup.Faux.SetResponses(step);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var submission = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            await reached;
            var taskId = (long)N(((JsonObjectValue)(await LiveOf(harness, root))["run"])["taskId"]);
            await root.AbortAsync(Context);
            Settled(await submission.WaitAsync(Context), SubmissionStatus.Unanswered, "aborted", "settled");
            MatchExactly($$"""{"taskId":{{taskId}},"inputs":[{{submission.Id.Value}}],"reason":"aborted","first":"<number>"}""", Field(await LiveOf(harness, root), "runFailure"),
                "runFailure");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "records-a-reset-that-cut-the-run-before-an-answer-kept-by-the-run-its-queued-input-starts", async () =>
        {
            var setup = Setup();
            var gate = new Deferred();
            var started = new Deferred();
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "hold",
                Description = "Waits",
                Parameters = NoParameters,
                Execute = async (_, _, _) =>
                {
                    started.Resolve();
                    await gate.Task;
                    return new ToolExecutionResult();
                },
            }));
            var second = Unanswered();
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("hold", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"), second.Step);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var first = await root.SubmitAsync(SubmissionDraft.Input("go"), Context);
            await started.Task;
            await root.ResetAsync(null, Context);
            var followUp = await root.SubmitAsync(SubmissionDraft.Input("next", WhenBusy.FollowUp), Context);
            gate.Resolve();
            Settled(await first.WaitAsync(Context), SubmissionStatus.Unanswered, "reset", "first");
            await second.Reached;
            var state = await LiveOf(harness, root);
            // The follow-up's run started in the commit that ended the first; the failure stays until a later run starts.
            SameJson($"[{followUp.Id.Value}]", Field((JsonObjectValue)state["run"], "inputs"), "run inputs");
            MatchJson($$"""{"inputs":[{{first.Id.Value}}],"reason":"reset"}""", Field(state, "runFailure"), "runFailure");
            await root.AbortAsync(Context);
            MatchJson($$"""{"inputs":[{{followUp.Id.Value}}],"reason":"aborted"}""", Field(await LiveOf(harness, root), "runFailure"), "runFailure after abort");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "keeps-the-failure-while-a-run-continues-the-queue-in-the-same-commit-and-replaces-it-with-the-next", async () =>
        {
            foreach (var policy in new[] { AfterFailedRun.Continue, AfterFailedRun.Keep })
            {
                var setup = Setup();
                setup.AfterFailedRun = policy;
                var failing = Failing();
                var busy = Unanswered();
                setup.Faux.SetResponses(failing.Step, busy.Step, FauxAssistantMessage([FauxText("third")]));
                var (harness, root) = await OpenChat(new MemoryStorage(), setup);
                var first = await root.SubmitAsync(SubmissionDraft.Input("first"), Context);
                await failing.Reached.Task;
                var second = await root.SubmitAsync(SubmissionDraft.Input("second", WhenBusy.FollowUp), Context);
                failing.Gate.Resolve();
                await first.WaitAsync(Context);
                if (policy == AfterFailedRun.Continue)
                {
                    await busy.Reached;
                    var state = await LiveOf(harness, root);
                    SameJson($"[{second.Id.Value}]", Field((JsonObjectValue)state["run"], "inputs"), $"{policy} run inputs");
                    MatchJson($$"""{"inputs":[{{first.Id.Value}}],"reason":"model_error"}""", Field(state, "runFailure"), $"{policy} runFailure");
                }
                else
                {
                    await harness.WaitForIdleAsync(Context);
                    MatchJson($$"""{"inputs":[{{first.Id.Value}}]}""", Field(await LiveOf(harness, root), "runFailure"), $"{policy} runFailure");
                }
                await harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "pisharp.the-event-stream-carries-the-run-failure", async () =>
        {
            var setup = Setup();
            setup.AfterFailedRun = AfterFailedRun.Continue;
            var failing = Failing();
            var busy = Unanswered();
            setup.Faux.SetResponses(failing.Step, busy.Step);
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            var listening = await EventTests.Listen(harness, root);
            var first = await root.SubmitAsync(SubmissionDraft.Input("first"), Context);
            await failing.Reached.Task;
            var second = await root.SubmitAsync(SubmissionDraft.Input("second", WhenBusy.FollowUp), Context);
            failing.Gate.Resolve();
            await first.WaitAsync(Context);
            await busy.Reached;
            await Eventually(() => listening.Events().Any(item => item is RunStartEvent { Inputs: [var input] } && input == second.Id));
            // One batch ends the failed run with its failure and starts the continue, which keeps it.
            var batch = listening.Batches.Snapshot().Single(items => items.Any(item => item is RunEndEvent));
            var ended = batch.OfType<RunEndEvent>().Single();
            MatchExactly($$$"""
                {"type":"run_end","inputs":[{{{first.Id.Value}}}],
                 "failure":{"taskId":"<number>","inputs":[{{{first.Id.Value}}}],"reason":"model_error","detail":"Invalid request"}}
                """, ended.ToJson(), "run_end");
            Check(batch.FindIndex(item => item is RunStartEvent) > batch.IndexOf(ended), "the continue starts after the run ends");
            // A stream attached now starts with the failure the continue kept.
            var late = await harness.WatchEventsAsync(root.Id, Context);
            MatchJson($$"""{"inputs":[{{first.Id.Value}}],"reason":"model_error"}""", Field(late.Snapshot.ToJson(), "runFailure"), "snapshot runFailure");
            await late.StopAsync();
            // The continue's abort records its own failure.
            await root.AbortAsync(Context);
            Equal(SubmissionStatus.Unanswered, (await second.WaitAsync(Context)).Status, "second settled");
            await Eventually(() => listening.Events().OfType<RunEndEvent>().Count() == 2);
            MatchJson($$"""{"inputs":[{{second.Id.Value}}],"reason":"aborted"}""", listening.Events().OfType<RunEndEvent>().Last().Failure, "aborted run_end");
            await listening.Stream.StopAsync();
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "pisharp.reads-and-writes-every-reason-in-upstream-spelling", () =>
        {
            // PiSharp: RunFailureReason declares every v1.5.0 value once (the work list), spelled as upstream's RunFailureReason union.
            Seq(["model_error", "no_model", "aborted", "reset", "faulted", "orphaned", "interrupted", "restart_limit", "nothing_to_answer", "turn_limit"],
                Enum.GetValues<RunFailureReason>().Select(RunFailure.ReasonName), "reason names");
            var failure = new RunFailure(new TaskId(7), [new SubmissionId(3), new SubmissionId(4)], RunFailureReason.TurnLimit, "2 turns");
            SameJson("""{"taskId":7,"inputs":[3,4],"reason":"turn_limit","detail":"2 turns"}""", failure.ToJson(), "json");
            Equal(failure, RunFailure.FromJson(failure.ToJson()), "round trip");
            // The failed run's first entry (area run-end), its last key.
            var started = new RunFailure(new TaskId(7), [new SubmissionId(3)], RunFailureReason.Aborted, null, new EntryId(2));
            SameJson("""{"taskId":7,"inputs":[3],"reason":"aborted","first":2}""", started.ToJson(), "first json");
            Equal(started, RunFailure.FromJson(started.ToJson()), "first round trip");
            Throws<FormatException>(() => RunFailure.FromJson(O("""{"taskId":7,"inputs":[3],"reason":"bored"}""")), "unknown reason");
        });
    }
}
