// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/chord-guide.test.ts, the parts that do not need Chord facets,
// services or the wire (decision 0005 excludes them). The guide's document definitions, commits, document states and watch are kept; the
// facet host and remote binding around them are replaced by direct subscriptions, and the Harness by a Session:
// - canvas: the provider facet's state and addStroke and the consumer's subscription run directly; the shutdown order (detach clients,
//   withdraw services, then close) becomes disposing the states before closing the Session.
// - diff reviews: the keyed facet instance becomes one document state per review key.
// - job output: as upstream, on a Harness: the job's phase handler appends through runtime.CommitAsync, the observer watches until
//   WaitForTaskAsync settles, and completion retires the task's documents. Upstream runs it on MemoryStorage only; here it runs on every
//   backend of the session.* groups.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using static Program;

internal static partial class SessionTests
{
    private static IEnumerable<(string, Func<Task>)> ChordGuide(string prefix, Func<IStorage> storageFactory)
    {
        // ─── 1. A Session-wide canvas ──────────────────────────────────────
        // Store a complete base after at most 99 replayed deltas.
        var canvasDoc = Documents.DefineSessionDoc("app.canvas", 1, () => O("{\"strokes\":[]}"),
            checkpointWhen: (_, _, info) => info.DeltasSinceBase >= 99);

        async Task<AttachedReplicatedState<JsonObjectValue?>> CreateCanvas(Session session)
        {
            // Creation is explicit; observation never writes.
            await session.CommitAsync(tx => tx.DocAsync(canvasDoc), Context);
            return await session.DocumentStateAsync(canvasDoc, Context) ?? throw new InvalidOperationException("canvas was retired during setup");
        }

        static Task AddStroke(Session session, SessionDocToken canvasDoc, JsonObjectValue stroke, DurableContext context) =>
            session.CommitAsync(async tx => (await tx.DocAsync(canvasDoc)).GetArray("strokes").Add(stroke), context);

        static int Strokes(JsonObjectValue? value) => value is null ? -1 : ((JsonArrayValue)value["strokes"]).Count;

        yield return Case(prefix + "runs-the-canvas-with-direct-subscriptions-and-detach-before-session-close", async () =>
        {
            var logged = new List<string>();
            var (_, session, _) = Open(storageFactory);
            var state = await CreateCanvas(session);
            var subscription = state.Subscribe((value, _, delivery) =>
            {
                lock (logged) logged.Add($"{delivery.Kind.ToString().ToLowerInvariant()} {delivery.Sequence} {Strokes(value)}");
            });
            await AddStroke(session, canvasDoc, O("{\"color\":\"black\",\"points\":[{\"x\":10,\"y\":20},{\"x\":30,\"y\":40}]}"), Context);
            subscription.Dispose();
            state.Dispose(); // Unsubscribes; does not delete the canvas or close the Session.
            lock (logged) Seq(["hydrate 0 0", "update 1 1"], logged, "logged");
            Equal(1, Strokes(await session.SnapshotAsync(canvasDoc, Context)), "strokes");

            // A worker attaches the canvas again; a late client hydrates the stroke, then follows updates.
            lock (logged) logged.Clear();
            var worker = await CreateCanvas(session);
            var client = worker.Subscribe((value, _, delivery) =>
            {
                lock (logged) logged.Add($"{delivery.Kind.ToString().ToLowerInvariant()} {Strokes(value)}");
            });
            await AddStroke(session, canvasDoc, O("{\"color\":\"red\",\"points\":[]}"), Context);
            await Flush(session);
            lock (logged) Seq(["hydrate 1", "update 2"], logged, "late client");
            // Shutdown order: clients detach and states release while the Session is still open, then it closes.
            client.Dispose();
            worker.Dispose();
            await session.CloseAsync(Context);
            await RejectsAsync(() => session.CommitAsync(_ => { }, Context), "closed", "commit after close");
        });

        // ─── 2. Conversation-scoped diff reviews ───────────────────────────
        var reviewDoc = Documents.DefineConversationDocFamily("app.diff-review", 1, DocumentFork.Current,
            seed => Obj(("path", ((JsonObjectValue)seed)["path"]), ("patch", ((JsonObjectValue)seed)["patch"]), ("comments", JsonArrayValue.Empty)),
            checkpointWhen: (_, _, info) => info.DeltasSinceBase >= 49);

        yield return Case(prefix + "runs-the-diff-reviews-with-a-keyed-consumer", async () =>
        {
            var (_, session, _) = Open(storageFactory);
            var root = await session.CommitWithAsync(tx => tx.CreateRootConversationAsync(), Context);
            var (key, seed) = ("review-7", O("{\"path\":\"a.ts\",\"patch\":\"-old\\n+new\"}"));
            await session.CommitAsync(tx => tx.DocAsync(reviewDoc, root.Id, key, seed), Context);
            var state = await session.DocumentStateAsync(reviewDoc, root.Id, key, Context) ?? throw new InvalidOperationException("review was retired during setup");
            var seen = new List<string>();
            using (state)
            {
                state.Subscribe((value, _, _) =>
                {
                    lock (seen) seen.Add($"{key} {(value is null ? -1 : ((JsonArrayValue)value["comments"]).Count)}");
                });
                // The consumer's addComment: Chord copies the assigned comment by value.
                await session.CommitAsync(async tx =>
                    (await tx.DocAsync(reviewDoc, root.Id, key, seed)).GetArray("comments").Add(O("{\"id\":\"c1\",\"line\":1,\"text\":\"why?\"}")), Context);
                await Flush(session);
                lock (seen) Seq(["review-7 0", "review-7 1"], seen, "seen");
            }
            var snapshot = (await session.SnapshotAsync(reviewDoc, root.Id, key, Context))!;
            Equal("a.ts", S(snapshot["path"]), "path");
            Equal("c1", S(At(snapshot, "comments", 0, "id")), "comment");
            await session.CloseAsync(Context);
        });

        // ─── 3. Task-scoped output and a tool/task watch ─────────────────────
        var jobOutputDoc = Documents.DefineTaskDoc("app.job-output", 1, () => O("{\"stdout\":\"\",\"chunks\":0}"),
            checkpointWhen: (_, _, info) => info.DeltasSinceBase >= 99);
        Task AppendJobOutput(ITaskRuntime runtime, string chunk, DurableContext context) =>
            // Read process output outside this callback. The runtime gates the live task.
            runtime.CommitAsync(async (tx, _) =>
            {
                var draft = await tx.DocAsync(jobOutputDoc, runtime.TaskId);
                var stdout = S(draft["stdout"]) + chunk;
                draft["stdout"] = stdout.Length > 50_000 ? stdout[^50_000..] : stdout;
                draft["chunks"] = N(draft["chunks"]) + 1;
            }, context);

        async Task ObserveJob(IDocumentObserver api, TaskId producerTaskId, Task finished, List<string> logged, List<string> rendered,
            DurableContext context)
        {
            var watch = await api.WatchDocAsync(jobOutputDoc, producerTaskId, context);
            if (watch is null) return;
            lock (logged) logged.Add(watch.Value is null ? "retired" : S(watch.Value["stdout"]));
            try
            {
                watch.Start((value, _, _) =>
                {
                    lock (rendered) rendered.Add(value is null ? "retired" : S(value["stdout"]));
                    return Task.CompletedTask;
                }); // Serialized callbacks never overlap.
                await finished; // Caller-supplied observation lifetime; outside any commit.
            }
            finally
            {
                await watch.StopAsync(); // Idempotent; prevents another callback from starting.
            }
        }

        yield return Case(prefix + "runs-the-job-output-watch-until-the-producer-retires-its-document", async () =>
        {
            var logged = new List<string>();
            var rendered = new List<string>();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var job = Tasks.DefineTask(new TaskDefinition
            {
                Name = "app.job",
                Version = 1,
                Initial = _ => O("{\"phase\":\"running\"}"),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["running"] = async (task, runtime, ctx) =>
                    {
                        await AppendJobOutput(runtime, $"$ {S(((JsonObjectValue)task.Input)["command"])}\n", ctx);
                        await gate.Task;
                        await AppendJobOutput(runtime, "ok\n", ctx);
                        await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Null), ctx);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
            });
            var registry = Registry.Create();
            registry.Install(new Extension { Name = "task:app.job", Tasks = [job] });
            var harness = await Harness.OpenAsync(storageFactory(), new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry }, Context);
            var root = await harness.RootAsync(Context);
            var id = await root.CommitAsync(tx => tx.CreateTaskAsync(job, O("{\"command\":\"make\"}"), new TaskOptions(TaskOwnership.Conversation)), Context);
            harness.Resume();
            // Upstream: eventually(() => snapshot(JobOutputDoc, id).chunks === 1); the job then waits at its gate.
            while (await harness.SnapshotAsync(jobOutputDoc, id, Context) is not { } first || N(first["chunks"]) != 1) await Task.Delay(1);
            Task finished = harness.WaitForTaskAsync(id, Context);
            var observing = ObserveJob(harness, id, finished, logged, rendered, Context);
            while (true)
            {
                lock (logged) if (logged.Count == 1) break;
                await Task.Yield();
            }
            gate.SetResult();
            await observing;
            lock (logged) Seq(["$ make\n"], logged, "logged");
            // Stopped once the task finished; the retirement frame may or may not have been delivered before.
            lock (rendered) Seq(["$ make\nok\n"], rendered.Take(1), "rendered");
            Check(await harness.WatchDocAsync(jobOutputDoc, id, Context) is null, "retired with the task");
            await harness.CloseAsync(Context);
        });
    }
}
