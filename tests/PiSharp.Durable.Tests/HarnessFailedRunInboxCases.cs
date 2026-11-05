// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-failed-run-inbox.test.ts (a30c2a81d, 440a5fcdb),
// ported in the Pi v1.5.0 sync (area reports). settings.afterFailedRun: the inputs queued behind a run that fails stay queued (keep, the
// default), start the next run (continue), or settle unanswered with run_failed (withdraw). Verbatim apart from the adaptations in
// HarnessSupport.cs and ChatSupport.cs, and these:
// - `setup.settings.afterFailedRun = policy` is ChatSetup.AfterFailedRun, read at every resolution as upstream's settings object is.
// - toMatchObject of a submission record compares its status and reason.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessFailedRunInboxCases() => FailedRunInboxTests.Cases("harness.failed-run-inbox.");
}

/// <summary>The harness.failed-run-inbox.* cases (upstream harness-failed-run-inbox.test.ts).</summary>
internal static class FailedRunInboxTests
{
    private static readonly DurableContext Context = DurableContext.Background;

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

    private static void Settled(SubmissionRecord record, SubmissionStatus status, string? reason, string what) =>
        Check(record.Status == status && (reason is null || record.Reason == reason),
            $"{what}: expected {status} {reason}, actual {record.Status} {record.Reason}");

    private sealed record Failed(IHarness Harness, IConversation Root, ISubmission Second);

    /// <summary>A run that fails with an input queued behind it, under <paramref name="policy"/>; returns how the queued input settled
    /// (upstream <c>failWithQueuedInput</c>).</summary>
    private static async Task<Failed> FailWithQueuedInput(AfterFailedRun? policy)
    {
        var setup = Setup();
        if (policy is not null) setup.AfterFailedRun = policy;
        var failing = Failing();
        setup.Faux.SetResponses(failing.Step, FauxAssistantMessage("second answer"), FauxAssistantMessage("third answer"));
        var (harness, root) = await OpenChat(new MemoryStorage(), setup);
        var first = await root.SubmitAsync(SubmissionDraft.Input("Analyze this report"), Context);
        await failing.Reached.Task;
        var second = await root.SubmitAsync(SubmissionDraft.Input("Email me the summary", WhenBusy.FollowUp), Context);
        failing.Gate.Resolve();
        Settled(await first.WaitAsync(Context), SubmissionStatus.Unanswered, "model_error", "first");
        return new Failed(harness, root, second);
    }

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        yield return Case(prefix + "inputs-queued-behind-a-failed-run.stay-queued-by-default", async () =>
        {
            var (harness, root, second) = await FailWithQueuedInput(null);
            await harness.WaitForIdleAsync(Context);
            Settled(await second.StatusAsync(Context), SubmissionStatus.Queued, null, "second");
            // The next input runs the queue, as before.
            var third = await root.SubmitAsync(SubmissionDraft.Input("Anything else?"), Context);
            Equal(SubmissionStatus.Done, (await second.WaitAsync(Context)).Status, "second settled");
            Equal(SubmissionStatus.Done, (await third.WaitAsync(Context)).Status, "third settled");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "inputs-queued-behind-a-failed-run.start-the-next-run-with-continue", async () =>
        {
            var (harness, root, second) = await FailWithQueuedInput(AfterFailedRun.Continue);
            var settled = await second.WaitAsync(Context);
            if (settled.Status != SubmissionStatus.Done || settled.Answer is not { } answerId) throw new InvalidOperationException($"Unexpected {settled.Status}");
            var answer = await root.CommitAsync(tx => tx.GetEntryAsync(Entries.AssistantEntry, answerId), Context);
            Equal("second answer", TextOf(answer?.Model?[0]), "answer");
            await harness.CloseAsync(Context);
        });

        yield return Case(prefix + "inputs-queued-behind-a-failed-run.place-queued-writes-and-keep-them-with-withdraw", async () =>
        {
            foreach (var policy in new[] { AfterFailedRun.Continue, AfterFailedRun.Withdraw })
            {
                var setup = Setup();
                setup.AfterFailedRun = policy;
                var failing = Failing();
                setup.Faux.SetResponses(failing.Step, FauxAssistantMessage("second answer"));
                var (harness, root) = await OpenChat(new MemoryStorage(), setup);
                var first = await root.SubmitAsync(SubmissionDraft.Input("first"), Context);
                await failing.Reached.Task;
                var note = SessionTests.Obj(("role", "user"), ("content", "a note the model sees"), ("timestamp", 1));
                var write = await root.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = "test.note", Model = JsonValue.Array(note) }), Context);
                Settled(await write.StatusAsync(Context), SubmissionStatus.Queued, null, $"{policy} write before");
                failing.Gate.Resolve();
                await first.WaitAsync(Context);
                await harness.WaitForIdleAsync(Context);
                // `continue` places the write at the failed run's boundary; `withdraw` withdraws inputs only.
                Settled(await write.StatusAsync(Context), policy == AfterFailedRun.Continue ? SubmissionStatus.Done : SubmissionStatus.Queued, null,
                    $"{policy} write after");
                await harness.CloseAsync(Context);
            }
        });

        yield return Case(prefix + "inputs-queued-behind-a-failed-run.settle-unanswered-with-run_failed-with-withdraw", async () =>
        {
            var (harness, _, second) = await FailWithQueuedInput(AfterFailedRun.Withdraw);
            Settled(await second.WaitAsync(Context), SubmissionStatus.Unanswered, "run_failed", "second");
            await harness.CloseAsync(Context);
        });
    }
}
