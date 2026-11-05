// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-hook-reads.test.ts (65fba65b2, #11141), ported in the Pi
// v1.5.0 sync (area run-end). IHookApi.EntryAsync and EntriesAsync, and ITaskRuntime's inherited EntriesAsync: a hook or task reads its
// conversation's raw history. On MemoryStorage, as upstream (the file is not in upstream's vitest.store.config.ts). Verbatim apart from the
// adaptations in HarnessSupport.cs and ChatSupport.cs, and this: upstream reads `api.entry(GenerationTask as never, id)`, a token of no
// entry kind, to show a kind mismatch gives undefined; C# types the token, so the case reads the user entry with another entry kind's
// token (pi.assistant), which gives null the same way.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessHookReadsCases() => HookReadsTests.Cases("harness.hook-reads.");
}

/// <summary>The harness.hook-reads.* cases (upstream harness-hook-reads.test.ts).</summary>
internal static class HookReadsTests
{
    private static readonly DurableContext Context = DurableContext.Background;

    private static readonly EntryQueryBounds Ascending = new() { Order = ScanOrder.Ascending };

    internal static IEnumerable<(string, Func<Task>)> Cases(string prefix)
    {
        // #11141
        yield return Case(prefix + "reads-raw-history-and-single-entries-from-a-hook", async () =>
        {
            var setup = Setup();
            var seen = new SyncList<(List<string> Kinds, string? User)>();
            setup.Registry.Install(Define.Extension(new Extension
            {
                Name = "reader",
                Hooks = [Define.Hook(HarnessTasks.GenerationTask, new GenerationHooks
                {
                    OnYield = async (_, api, context) =>
                    {
                        var page = await api.EntriesAsync(Ascending, 10, null, context);
                        var first = page.Items[0];
                        var user = await api.EntryAsync(Entries.UserEntry, first.Id, context);
                        seen.Add(([.. page.Items.Select(entry => entry.Kind)], user?.Kind));
                        // Another kind: null.
                        Equal(null, await api.EntryAsync(Entries.AssistantEntry, first.Id, context), "another kind");
                        return null;
                    },
                })],
            }));
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            // The answer is not committed yet when onYield asks.
            var asked = seen.Snapshot();
            Equal(1, asked.Count, "asked");
            Seq(["pi.user"], asked[0].Kinds, "kinds");
            Equal("pi.user", asked[0].User, "user");
            Equal(0, setup.Reports.Count, "reports");
            await harness.CloseAsync(Context);
        });

        // #11141
        yield return Case(prefix + "pages-a-task-s-conversation-history-by-entry-id-range", async () =>
        {
            var setup = Setup();
            var pages = new SyncList<List<EntryId>>();
            var reader = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.reader",
                Version = 1,
                Initial = _ => Obj(("phase", "run")),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["run"] = async (_, runtime, context) =>
                    {
                        var all = await runtime.EntriesAsync(Ascending, 1, null, context);
                        pages.Add([.. all.Items.Select(entry => entry.Id)]);
                        var rest = await runtime.EntriesAsync(Ascending, 1, all.Next, context);
                        pages.Add([.. rest.Items.Select(entry => entry.Id)]);
                        var bounded = await runtime.EntriesAsync(new EntryQueryBounds { MinEntryId = rest.Items[0].Id, MaxEntryId = rest.Items[0].Id },
                            10, null, context);
                        pages.Add([.. bounded.Items.Select(entry => entry.Id)]);
                        await runtime.CommitAsync((_, _) => Completed(null), context);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            setup.Registry.Install(Define.Extension(new Extension { Name = "reader", Tasks = [reader] }));
            setup.Faux.SetResponses(FauxAssistantMessage("done"));
            var (harness, root) = await OpenChat(new MemoryStorage(), setup);
            await (await root.SubmitAsync(SubmissionDraft.Input("go"), Context)).WaitAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(reader, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            await harness.WaitForTaskAsync(id, Context);
            var entries = await AllEntries(root);
            Seq(["pi.user", "pi.assistant"], entries.Select(entry => entry.Kind), "kinds");
            var read = pages.Snapshot();
            Equal(3, read.Count, "pages");
            Seq([entries[0].Id], read[0], "first page");
            Seq([entries[1].Id], read[1], "next page");
            Seq([entries[1].Id], read[2], "bounded");
            await harness.CloseAsync(Context);
        });
    }
}
