// Decision 0011 (run W4, integration): where the W4 units meet. runs.db's agent compare tree is three levels (R5: the run, one row per
// model, then each model's provider calls), so RunStore's Delete, Clear and retention follow a run's whole tree (R0 wrote them for two), and
// Settings › Data's retention count uses the store's own rule; an agent compare run in History › Runs, the Run inspector and Settings › Data
// & storage links to its page (R6's /compare?mode=agent&run=). In process over temporary files; no provider is called.
using PiSharp.WebAgent.DataStorage;
using PiSharp.WebAgent.Runs;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> W4IntegrationCases() =>
    [
        Case("w4.integration.runs-store-follows-an-agent-compare-run-at-every-level", () =>
        {
            var root = Temp("w4-integration-run-tree");
            try
            {
                var store = new RunStore(new RunsDb(Path.Join(root, RunsDb.FileName)));
                var now = DateTimeOffset.UtcNow;
                var old = now.AddDays(-40);
                // An agent compare run `run` with models a and b, each with provider calls; and a plain old run.
                void Tree(string run, DateTimeOffset created)
                {
                    store.Insert(AgentCompareRow(run, created, null));
                    foreach (var slot in new[] { "a", "b" })
                    {
                        store.Insert(AgentCompareRow(run + "-" + slot, created, run));
                        for (var call = 1; call <= 2; call++)
                            store.Insert(RunsRow($"{run}-{slot}-call{call}", created) with { Source = "agent-compare", ParentId = run + "-" + slot });
                    }
                }

                Tree("ac1", now);
                store.Insert(RunsRow("solo", now));
                Equal(7, store.Delete(["ac1"]), "deleting the run deletes its models and their calls");
                Equal("solo", Ids(store), "no call of the run is left behind");

                Tree("ac2", old);
                Tree("ac3", old);
                store.Insert(RunsRow("old", old));
                Check(store.Pin("ac2", true) && store.Pin("ac3-b-call1", true), "pinned");
                var count = store.CountPastRetention(TimeSpan.FromDays(30), now);
                var cleanup = store.Cleanup(RunHistorySettings.CreateDefault(), now);
                Equal(5L, count, "the count Settings › Data shows is what retention removes");
                Equal(5, cleanup.Runs, "the old run, model a of ac3 with its calls, and ac3-b's unpinned call");
                Equal("ac2,ac2-a,ac2-a-call1,ac2-a-call2,ac2-b,ac2-b-call1,ac2-b-call2,ac3,ac3-b,ac3-b-call1,solo", Ids(store),
                    "a pinned run keeps its models and their calls; a pinned call keeps its model and run");
                Equal(0, store.Cleanup(RunHistorySettings.CreateDefault(), now).Runs, "a second cleanup removes nothing");

                Equal(1, store.Clear(keepPinned: true), "Clear history removes only what no pin keeps");
                Equal("ac2,ac2-a,ac2-a-call1,ac2-a-call2,ac2-b,ac2-b-call1,ac2-b-call2,ac3,ac3-b,ac3-b-call1", Ids(store), "the pinned trees stay whole");
                Equal(10, store.Clear(keepPinned: false), "Clear history without keeping pins removes them all");
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("w4.integration.an-agent-compare-run-links-to-its-agent-view", () =>
        {
            var now = DateTimeOffset.UtcNow;
            Equal("/compare?mode=agent&run=ac1", RunHistoryText.AgentCompareHref(AgentCompareRow("ac1", now, null)), "the run's top row");
            Check(RunHistoryText.AgentCompareHref(AgentCompareRow("ac1-a", now, "ac1")) is null, "not a model's row");
            Check(RunHistoryText.AgentCompareHref(RunsRow("p1", now) with { Kind = RunKind.Parent }) is null, "not another source");
            var row = new DataWorkspaceRow("ac1", "a", "faux/echo", "git worktree", true, null, null, null, now.AddDays(7), "C:\\project", true, null);
            Equal("/compare?mode=agent&run=ac1", row.RunHref, "Settings › Data's View run opens the agent view, not request mode");
        }),
    ];

    private static RunRecord AgentCompareRow(string id, DateTimeOffset created, string? parent) =>
        RunsRow(id, created, kind: RunKind.Parent) with { Source = "agent-compare", ParentId = parent, Provider = "", Model = "" };
}
