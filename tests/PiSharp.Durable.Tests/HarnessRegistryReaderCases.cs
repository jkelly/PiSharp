// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/durable/test/harness-registry-reader.test.ts (Pi v1.5.0 sync, area
// registry-reader, 6eb99ad65): registryReader over a Chord v2 registry value (RegistryReader.Create, PiSharp.Durable.Store), whose
// composition is Durable's registry snapshot, and a Harness taking its extensions from it (p3's pattern, Chord v2 slice 8 §5).
//
// Hand-built, as Chord's host (createHost, packages/chord/src/next/host) is phase 6's: upstream builds each registry through createHost,
// defineFacet and host-lifetime objects. Here a registry is Chord v2's own registry record and value (src/PiSharp.Chord/Registries, through
// the suite's test-only InternalsVisibleTo, as StoreStorageCases.cs attaches a hand-built Batch), wired as RegistryHost.cs in
// PiSharp.Chord.Tests wires it: one Batch per host; load's one commit serves the registry with its compose and enters every contribution,
// then composes it once (host.ts #publish). A host-lifetime object with empty params is activated at load, so its self.writable
// contribution is in that commit; its method setting the writable is the writable's Set, in a batch of its own. host.registry(R) is the
// record's value of R's version, read after load. So the value, its composition, its synchronous observers (Observe, Advance inside the
// batch that composed, invalid/reentrant inside it) and its failures are Chord's code, not a stand-in. What only the host has waits for
// phase 6:
// - "reads the composed snapshot ..." and "wakes a Harness's scheduler ...": every assertion is ported; only the host calls building the
//   registry wait (createHost, host.load of defineFacet(serve), host.serve, local(Plugin) with self.writable and self.contribute,
//   host.registry, host.get(Plugin).install() and host.close): the plugin object (app.plugin) is a writable contributed at load, its
//   install is the writable's Set, and the host's close is the record's.
// - "a follower of a contributed Harness fails when that Harness's Session closes, and follows the next one" (S1) keeps Durable's part:
//   the Durable object's contribution is a writable holding the Harness it opened (its self.open handle's value) and it follows that
//   Harness's task graph with an observer (its self.follow), the Harnesses registry composes the last contribution, and the viewer follows
//   the root conversation of the registry's Harness with an observer (self.follow) and is woken by the registry's observer when the next
//   Harness is contributed (self.retryOn), then follows it. Its assertions on Durable hold as upstream's: the view sees "app.first"; the
//   Session's close ends both followers failed with "session closed" (S1); the next Harness's view does not hold "app.first" and sees
//   "app.second". Waiting for phase 6: the host's own parts of it, that the close call is cut off by its object's failure (durable.close
//   rejects with "session closed"), that the viewer's state is failed with a transient cause and then live (the host's mapping of a
//   follower's failure to its object's state), and durable.retry() opening the next Harness (here the case opens it and sets the
//   writable), with host.hold, local(), self.open, self.contribute, self.follow, self.retryOn and self.derive themselves.
// - The contract.* cases are authored, not upstream's: they pin what registry-reader.ts's doc comment says and its code does (a failed
//   composition calls no listener and snapshot() keeps the last good one; aborting the context detaches every listener; the disposer
//   detaches once).
// Adapted: createModels() is EmptyDurableModels; the error reason upstream reads from a thrown Chord error is ChordError.Reason; a delay of
// 10 ms is HarnessTests.Eventually on the condition upstream then checks.
using PiSharp.Chord.Data;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Storage;
using PiSharp.Durable.Store;
using static HarnessTests;
using static Program;
using ChordContext = PiSharp.Chord.ChordContext;
using ChordWatchEnd = PiSharp.Chord.WatchEnd;
using DurableRegistry = PiSharp.Durable.Registry;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessRegistryReaderCases() => HarnessRegistryReaderTests.Cases();
}

internal static class HarnessRegistryReaderTests
{
    private static readonly DurableContext Context = DurableContext.Background;
    private static readonly ChordContext ChordBackground = ChordContext.Background;

    /// <summary>A task that completes at once (upstream <c>Done</c>).</summary>
    private static readonly DurableTask Done = Tasks.DefineTask(new TaskDefinition
    {
        Name = "app.done",
        Version = 1,
        Initial = _ => SessionTests.O("""{"phase":"run"}"""),
        Phases = new Dictionary<string, TaskPhaseHandler>
        {
            ["run"] = async (_, runtime, taskContext) => await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), taskContext),
        },
        Abort = (_, _, _) => Task.CompletedTask,
    });

    /// <summary>p3's pattern: the extensions registry composes Durable's snapshot, leaving out an extension Durable refuses (upstream
    /// <c>compose</c>).</summary>
    private static IRegistrySnapshot Compose(IReadOnlyList<RegistryEntry<Extension>> table, ComposeContext compose)
    {
        var registry = DurableRegistry.Create();
        foreach (var entry in table)
        {
            try
            {
                registry.Install(entry.Value);
            }
            catch (Exception error)
            {
                compose.Report(new RegistryConflict(error.Message, [entry.Plugin]));
            }
        }
        return registry.Snapshot();
    }

    /// <summary>The extensions registry served by @app/core, with the plugin object's extension (one without the task app.done) contributed
    /// at load (upstream <c>serve</c>).</summary>
    private static HandBuiltRegistry<Extension, IRegistrySnapshot> Extensions(Func<IReadOnlyList<RegistryEntry<Extension>>, ComposeContext, IRegistrySnapshot>? compose = null) =>
        new("app.harness.extensions", compose ?? Compose, ("@app/core", "app.plugin", Define.Extension(new Extension { Name = "app.plugin" })));

    /// <summary>The plugin object's install (upstream <c>Plugin.install</c>): its extension now has the task app.done.</summary>
    private static void Install(IWritable<Extension> plugin) => plugin.Set(Define.Extension(new Extension { Name = "app.plugin", Tasks = [Done] }));

    internal static IEnumerable<(string, Func<Task>)> Cases() => [.. RegistryReaderCases(), .. ViewsCases(), .. ContractCases()];

    private static IEnumerable<(string, Func<Task>)> RegistryReaderCases()
    {
        yield return Case("harness.registry-reader.registry-reader.reads-the-composed-snapshot-a-listener-is-called-synchronously-inside-the-batch-that-composed", () =>
        {
            var host = Extensions();
            var value = host.Value;
            var reader = RegistryReader.Create(value, ChordBackground);
            Check(reader.Snapshot().GetExtension("app.plugin")?.Tasks is null, "no tasks before the install");
            var calls = new List<string>();
            var unsubscribe = reader.Subscribe(() =>
            {
                // Inside Chord's batch: a Chord call here is refused, so a listener only wakes its scheduler.
                try
                {
                    value.Observe(new NativeRecorder<IRegistrySnapshot>(), ChordBackground);
                    calls.Add("outside");
                }
                catch (Exception error)
                {
                    calls.Add((error as ChordError)?.Reason ?? "other");
                }
            });
            Install(host.Writables[0]);
            SessionTests.Seq(["reentrant"], calls, "calls");
            Check(ReferenceEquals(Done, reader.Snapshot().GetTask("app.done")), "the installed task");
            unsubscribe.Dispose();
            host.Close();
        });

        yield return Case("harness.registry-reader.registry-reader.wakes-a-harness-s-scheduler-a-task-blocked-on-its-missing-definition-runs-once-a-contribution-brings-it", async () =>
        {
            var host = Extensions();
            var harness = await Harness.OpenAsync(new MemoryStorage(),
                new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = RegistryReader.Create(host.Value, ChordBackground) }, Context);
            harness.Resume();
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(Done, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
            var done = harness.WaitForTaskAsync(id, Context);
            await Task.Delay(10);
            Install(host.Writables[0]);
            var record = await done;
            Check(JsonValue.DeepEquals(JsonValue.Parse("""{"status":"completed","result":null}"""), ((SettlingTaskState)record.State).Outcome.ToJson()),
                "the outcome");
            await harness.CloseAsync(Context);
            host.Close();
        });
    }

    private static IEnumerable<(string, Func<Task>)> ViewsCases()
    {
        yield return Case("harness.registry-reader.durable-s-views-from-other-objects-slice-8-5-s1.a-follower-of-a-contributed-harness-fails-when-that-harness-s-session-closes-and-follows-the-next-one", async () =>
        {
            // The Durable object opens a Harness and contributes it; the Harnesses registry composes the last contribution.
            var first = await OpenPlain();
            // The Durable object follows its own Harness's task graph (its self.follow).
            WatchEnd? graphEnd = null;
            await first.ObserveTaskGraphAsync(new Observer<TaskGraph>((_, _) => { }, ended => graphEnd = ended), Context);
            var host = new HandBuiltRegistry<IHarness?, IHarness?>("app.harnesses", (table, _) => table.Count == 0 ? null : table[^1].Value,
                ("@app/core", "app.durable", first));
            var durable = host.Writables[0];
            var viewer = new Viewer(host.Value);
            await viewer.Following;
            await Write(first, "app.first");
            await Eventually(() => viewer.Kinds.Contains("app.first"));
            await first.CloseAsync(Context);
            // S1: the Session's close fails both followers (each may follow the next Session).
            Check(viewer.End is { Kind: WatchEndKind.Failed }, "the viewer's follower failed");
            Equal("session closed", (viewer.End!.Error as Exception)?.Message, "the viewer's failure");
            Check(graphEnd is { Kind: WatchEndKind.Failed }, "the Durable object's follower failed");
            Equal("session closed", (graphEnd!.Error as Exception)?.Message, "the Durable object's failure");
            // The retry opens the next Harness, whose contribution wakes the viewer (retryOn), which follows it.
            var next = await OpenPlain();
            durable.Set(next);
            await Eventually(() => ReferenceEquals(viewer.Harness, next) && viewer.End is null);
            Check(!viewer.Kinds.Contains("app.first"), "the next Harness's view");
            await Write(next, "app.second");
            await Eventually(() => viewer.Kinds.Contains("app.second"));
            await next.CloseAsync(Context);
            host.Close();
        });
    }

    private static IEnumerable<(string, Func<Task>)> ContractCases()
    {
        yield return Case("harness.registry-reader.contract.keeps-the-last-good-snapshot-while-compose-fails-and-calls-no-listener-for-the-failure", () =>
        {
            var failing = false;
            var host = Extensions((table, compose) => failing ? throw new InvalidOperationException("compose broke") : Compose(table, compose));
            var reader = RegistryReader.Create(host.Value, ChordBackground);
            var calls = 0;
            using var subscription = reader.Subscribe(() => calls++);
            var good = reader.Snapshot();
            failing = true;
            Install(host.Writables[0]);
            Equal(0, calls, "listener calls for the failed composition");
            Check(ReferenceEquals(good, reader.Snapshot()), "the last good snapshot");
            Check(host.Reported.Any(error => error is RegistryFailedError { Error.Message: "compose broke" }), "registry_failed reported");
            failing = false;
            host.Writables[0].Set(Define.Extension(new Extension { Name = "app.plugin" }));
            Equal(1, calls, "listener calls once compose heals");
            Check(!ReferenceEquals(good, reader.Snapshot()), "a new snapshot");
            host.Close();
        });

        yield return Case("harness.registry-reader.contract.aborting-its-context-detaches-every-listener", () =>
        {
            var host = Extensions();
            var (context, cancel) = ChordContext.WithCancel(ChordBackground);
            var reader = RegistryReader.Create(host.Value, context);
            var calls = new List<string>();
            reader.Subscribe(() => calls.Add("a"));
            reader.Subscribe(() => calls.Add("b"));
            Install(host.Writables[0]);
            SessionTests.Seq(["a", "b"], calls, "calls before the abort");
            cancel();
            host.Writables[0].Set(Define.Extension(new Extension { Name = "app.plugin" }));
            SessionTests.Seq(["a", "b"], calls, "calls after the abort");
            host.Close();
        });

        yield return Case("harness.registry-reader.contract.its-disposer-detaches-the-listener-once-and-close-calls-none", () =>
        {
            var host = Extensions();
            var reader = RegistryReader.Create(host.Value, ChordBackground);
            var calls = new List<string>();
            var first = reader.Subscribe(() => calls.Add("first"));
            using var second = reader.Subscribe(() => calls.Add("second"));
            first.Dispose();
            first.Dispose();
            Install(host.Writables[0]);
            SessionTests.Seq(["second"], calls, "calls");
            host.Close();
            SessionTests.Seq(["second"], calls, "calls after the host closed");
        });
    }

    private static async Task<IHarness> OpenPlain()
    {
        var harness = await Harness.OpenAsync(new MemoryStorage(), new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = DurableRegistry.Create() },
            Context);
        await harness.RootAsync(Context);
        return harness;
    }

    /// <summary>The Durable object's write (upstream <c>Durable.write</c>): a write submission to the root conversation.</summary>
    private static async Task Write(IHarness harness, string kind)
    {
        var root = await harness.ConversationAsync(ConversationId.Root, Context);
        await root!.SubmitAsync(SubmissionDraft.Write(new EntryDraft { Kind = kind }), Context);
    }

    /// <summary>
    /// The viewer object (upstream <c>Viewer</c>): it follows the root conversation's entry kinds through the Harness the registry gives
    /// (self.follow over observeConversation), and the registry's observer wakes it (self.retryOn) to follow the next Harness, outside the
    /// batch that composed. <see cref="End"/> is how its follow ended, null while it follows.
    /// </summary>
    private sealed class Viewer : INativeObserver<IHarness?>
    {
        private readonly IRegistryValue<IHarness?> harnesses;
        private volatile IReadOnlyList<string> kinds = [];
        private volatile WatchEnd? end;

        public Viewer(IRegistryValue<IHarness?> harnesses)
        {
            this.harnesses = harnesses;
            harnesses.Observe(this, ChordBackground);
            Following = FollowAsync();
        }

        public IReadOnlyList<string> Kinds => kinds;
        public WatchEnd? End => end;
        public IHarness? Harness { get; private set; }
        public Task Following { get; private set; }

        public void Advance(PiSharp.Chord.NativeWatchCall<IHarness?> call, ChordContext context)
        {
            // Inside the batch that composed: only wake.
            if (call is PiSharp.Chord.NativeWatchValueCall<IHarness?>) Following = Task.Run(FollowAsync);
        }

        void INativeObserver<IHarness?>.End(ChordWatchEnd end) { }

        private async Task FollowAsync()
        {
            var harness = harnesses.Value;
            if (harness is null) return;
            var observation = await harness.ObserveConversationAsync(ConversationId.Root, new Observer<ConversationView>(
                (call, _) => kinds = [.. ((WatchValueCall<ConversationView>)call).Value.Entries.Select(entry => entry.Kind)],
                ended => end = ended), Context);
            kinds = [.. observation.Value.Entries.Select(entry => entry.Kind)];
            end = null;
            Harness = harness;
        }
    }

    /// <summary>A native observer that records nothing.</summary>
    private sealed class NativeRecorder<T> : INativeObserver<T>
    {
        public void Advance(PiSharp.Chord.NativeWatchCall<T> call, ChordContext context) { }
        public void End(ChordWatchEnd end) { }
    }
}

/// <summary>
/// One registry of a hand-built host (upstream's createHost, phase 6), on Chord v2's registry record: its own Batch; load's one commit
/// serves it at version 1 with <c>compose</c> and enters each plugin object's writable contribution (an object with empty params is
/// activated at load), then composes it once. <see cref="Value"/> is <c>host.registry(R)</c>; <see cref="Close"/> is the host's close.
/// </summary>
internal sealed class HandBuiltRegistry<C, V>
{
    private readonly RegistryRecord record;

    public HandBuiltRegistry(string name, Func<IReadOnlyList<RegistryEntry<C>>, ComposeContext, V> compose,
        params (string Plugin, string Type, C Initial)[] objects)
    {
        Batch = new Batch(Errors.Add, null);
        record = new RegistryRecord(name, Batch, Reported.Add);
        var owner = new Owner();
        var contributions = new List<RegistryContribution>();
        foreach (var (plugin, type, initial) in objects)
        {
            var writable = new WritableSource<C>(initial, owner, Batch);
            Writables.Add(writable);
            contributions.Add(new RegistryContribution(record, new Contributor(plugin, type), 1, ContributionContent.Native(writable)));
        }
        Batch.Commit(() =>
        {
            record.Serve(1, new RegistryProvider<C, V> { Compose = compose });
            foreach (var contribution in contributions) record.Add(contribution);
            record.ComposeServed();
        }, PiSharp.Chord.ChordContext.Background);
        Value = record.Value<V>(1);
    }

    public Batch Batch { get; }

    /// <summary>What the batch reported (the host's onError, batch kinds).</summary>
    public List<BatchError> Errors { get; } = [];

    /// <summary>What the registry reported (the host's onError, registry kinds).</summary>
    public List<RegistryError> Reported { get; } = [];

    /// <summary>The objects' writables, in order.</summary>
    public List<IWritable<C>> Writables { get; } = [];

    /// <summary>The registry's value for readers of version 1 (<c>host.registry(R)</c>).</summary>
    public IRegistryValue<V> Value { get; }

    /// <summary>The host's close: the record closes, its value's observers end <c>ended</c>.</summary>
    public void Close() => record.Close();

    /// <summary>The attempt a writable belongs to: live until the host closes.</summary>
    private sealed class Owner : IWritableOwner
    {
        public PiSharp.Chord.ChordContext Context => PiSharp.Chord.ChordContext.Background;
        public void AssertUsable() { }
    }
}
