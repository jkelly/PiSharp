// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/harness-structured.test.ts, describe "tool rounds": the one test
// that generation alone (P8) unblocks, "keeps run control with a faulted generation until its owned work drains, and retries a rejected
// final commit". It runs no tool. The other tests of "tool rounds" and "tool rounds and events" are pending (see HarnessStructuredCases.cs).
// Adaptations beyond those of HarnessStructuredSupport.cs and ChatSupport.cs: invalidFinalStream is GenerationTests.InvalidFinalStream (a
// non-finite usage count instead of a function, see HarnessGenerationCases.cs).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class StructuredTests
{
    private static IEnumerable<(string, Func<Task>)> ToolRounds(string prefix, Func<Task<IStorage>> fresh)
    {
        yield return Case(prefix + "keeps-run-control-with-a-faulted-generation-until-its-owned-work-drains-and-retries-a-rejected-final-commit", async () =>
        {
            var nodes = new Nodes();
            var setupBase = Setup();
            var setup = setupBase.WithModels(new ModelsOverride(setupBase.Models) { StreamSimpleOverride = (_, _, _, token) => GenerationTests.InvalidFinalStream(token) });
            AddTask(setup.Registry, nodes.Node);
            IHarness? opened = null;
            AddHooks(setup.Registry, HarnessTasks.GenerationTask, new GenerationHooks
            {
                BeforeRequest = async (_, api, callContext) =>
                {
                    await opened!.CommitAsync(tx => tx.CreateTaskAsync(nodes.Node, NodeInput("hooked"), Owned(api.TaskId)), callContext);
                    return null;
                },
            });
            nodes.Script("hooked", new Behavior
            {
                Abort = async (runtime, ctx) =>
                {
                    await nodes.Gate("abort.hooked").Task;
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx);
                },
            });
            var storage = new RejectingStorage(await fresh());
            var (harness, root) = await OpenChat(storage, setup);
            opened = harness;
            async Task<TaskId?> RunTask() =>
                await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context) is { } live && live.TryGetValue("run", out var run)
                    ? new TaskId((long)N(((JsonObjectValue)run)["taskId"])) : null;
            var submission = await root.SubmitAsync(SubmissionDraft.Input("hi"), Context);
            await Eventually(async () => await RunTask() is { } id && (await State(harness, id)).Status == DurableTaskStatus.Completing);
            var generation = (await RunTask())!.Value;
            MatchJson("""{"status":"completing","outcome":{"status":"faulted"}}""", (await State(harness, generation)).ToJson(), "generation state");
            Check(!await Settled(submission.WaitAsync(Context)), "the submission waits");
            Equal(generation, await RunTask(), "run control");
            // The faulted outcome is cancellation intent: the hooked work is aborted, then the run settles.
            await Eventually(() => nodes.Logged("abort:hooked"));
            storage.Reject = generation;
            nodes.Open("abort.hooked");
            // The final commit, with the run's cleanup, is rejected once: nothing of the cleanup lands.
            await Eventually(() => setup.Reports.Snapshot().Any(error => error is StorageRejectedException));
            Equal(generation, await RunTask(), "run control after the rejected commit");
            Seq(["pi.user"], await Kinds(root), "kinds before the retry");
            Check(!await Settled(submission.WaitAsync(Context)), "the submission still waits");
            // The next commit retries it; the partial becomes one aborted entry.
            await root.CommitAsync(tx => tx.AppendEntryAsync(root.Id, new EntryDraft { Kind = "note" }), Context);
            MatchJson("""{"status":"unanswered","reason":"faulted"}""", (await submission.WaitAsync(Context)).ToJson(), "settled");
            SameJson("{}", await harness.SnapshotAsync(Live.LiveDoc, root.Id, Context), "live");
            Seq(["pi.user", "note", "pi.assistant"], await Kinds(root), "kinds");
            await harness.CloseAsync(Context);
        });
    }
}
