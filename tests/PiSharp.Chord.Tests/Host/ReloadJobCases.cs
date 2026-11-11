using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Host;
using PiSharp.Chord.Jobs;
using static PiSharp.Chord.Contracts.Schemas;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HostReloadJobCases() =>
    [
        Case("host.reload.pure-restart-aborts-old-in-memory-job", async () =>
        {
            var type = H2Type(); var started = H2Signal(); var finish = H2Signal(); var aborted = false;
            var definition = JobDefinition.Define("work", new() { Version = 1, Args = Nul(), Result = Str() });
            IJobHandle? oldHandle = null, newHandle = null;
            await using var host = H2Host();
            PluginFacet Plugin(string name) => new("item", Facet.Define((h, _) => h.Serve(type, (_, _, self) =>
            {
                var handle = self.Job(definition, new() { Run = async (_, context) =>
                {
                    if (name == "old")
                    {
                        started.TrySetResult();
                        try { await finish.Task.WaitAsync(context.AbortSignal); }
                        catch (OperationCanceledException) { aborted = true; throw; }
                    }
                    return JsonValue.Create(name);
                } });
                if (self is Self) { if (name == "old") oldHandle = handle; else newHandle = handle; }
                return H2Read(name);
            })));
            await host.LoadAsync([Plugin("old")], ChordContext.Background);
            var oldRun = await oldHandle!.StartAsync(null, ChordContext.Background);
            await started.Task;
            await host.ReloadAsync([Plugin("new")], ChordContext.Background);
            await host.DrainReloadsAsync(ChordContext.Background);
            Check(aborted, "every reload cancels the old in-memory invocation");
            var oldOutcome = await oldRun.OutcomeAsync(ChordContext.Background);
            Equal("aborted", oldOutcome.Error!.Kind, "old run abort kind");
            Equal("reload", oldOutcome.Error.Reason, "old run abort reason");
            var newRun = await newHandle!.StartAsync(null, ChordContext.Background);
            var newOutcome = await newRun.OutcomeAsync(ChordContext.Background);
            Equal("new", ((JsonNode)newOutcome.Result!).GetValue<string>(), "new invocations use new code");
            await host.DrainReloadsAsync(ChordContext.Background);
        }),
        Case("host.reload.native-restart-cuts-transient-job-with-reload-reason", async () =>
        {
            var type = H2Type(); var started = H2Signal();
            var definition = JobDefinition.Define("work", new() { Version = 1, Args = Nul(), Result = Str() });
            IJobHandle? handle = null; await using var host = H2Host();
            PluginFacet Plugin(string name) => new("item", Facet.Define((h, _) => h.Serve(type, (_, _, self) =>
            {
                self.Open(_ => Task.FromResult(name), (_, _, _) => Task.CompletedTask);
                var job = self.Job(definition, new() { Run = async (_, context) =>
                {
                    if (name == "old") { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, context.AbortSignal); }
                    return JsonValue.Create(name);
                } });
                if (self is Self && name == "old") handle = job;
                return H2Read(name);
            })));
            await host.LoadAsync([Plugin("old")], ChordContext.Background);
            var run = await handle!.StartAsync(null, ChordContext.Background); await started.Task;
            var report = await host.ReloadAsync([Plugin("new")], ChordContext.Background);
            Equal("restarted", report.Objects.Single().Fate, "native job owner restarts");
            var outcome = await run.OutcomeAsync(ChordContext.Background);
            Equal("aborted", outcome.Error!.Kind, "transient run aborted"); Equal("reload", outcome.Error.Reason, "reload cut classification");
        }),
    ];
}
