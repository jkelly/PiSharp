// PiSharp-only (no upstream Pi source): there is no upstream test file. The cases are PiSharp's own (authored,
// CompatTests.AuthoredOverCapture), closing out the Pi v1.6.0 sync (faac585884ae2c013a6b1400f3e9e77f10b34bac): v1.6.0 code opens and
// continues what PiSharp 1.5.2 wrote. v1.6.0 changes no storage schema; it adds optional fields to records (a pending or running state's
// `wake`, a sleeping state's `signals` and its now optional `until`, a submission's `notify`, an inbox item's `onAbort`, a nested call
// document's `kind`, pi.live.run.yield) and defines pi.tool at version 4 and pi.generation at version 3, both with migrations that keep
// older checkpoints as they are.
// The 1.5.x records come from two places:
// - Fixtures/v1.5.0/upstream-sessions (first run): SQLite and JSONL sessions Pi v1.5.0 itself wrote (tools/DurableFixtures150), which
//   PiSharp.Durable150.Capture.Tests reads. Pi v1.5.1 and v1.5.2 changed no record shape and no task version (pi.tool 3, pi.generation
//   2), so they are what PiSharp 1.5.2 writes too: a task asleep with `until`, a task a dead process left running, and a chat with
//   version 2 generations and settled submissions.
// - Records authored in 1.5.2's shape where no capture holds one: a pi.tool task at version 3, before and after its intent.
// compat.upgrade-from-v1.5.2.<stage>.<backend>.read: v1.6.0's storage reads every record of a stage equal to the capture's, key order
//   included, so no field is added to a record that is only read.
// compat.upgrade-from-v1.5.2.sleeping.<backend>.wakes-a-sleep-stored-with-until: the sleep shows in wake() and inspection with its time,
//   and its task runs again once due, with the wake {reason: "time"}.
// compat.upgrade-from-v1.5.2.process-exit.<backend>.counts-the-restart-and-runs-the-task-again: open writes the running task back as
//   pending, counting the restart, without a wake, and it runs to its end.
// compat.upgrade-from-v1.5.2.compaction.<backend>.answers-the-next-input-with-a-version-3-generation: the chat takes another input; its
//   generation is created at version 3 and the stored terminal ones keep version 2.
// compat.upgrade-from-v1.5.2.tool-task.<backend>.*: a stored version 3 tool task is reserved under version 4: a pending one runs, and
//   one a crash cut off after a replay-safe intent ({phase: "execute", arguments, replay}, without the wake and checkpoint fields v1.6.0
//   adds) runs again; both results carry a durationMs.
// Covered elsewhere: a version 1 or 2 generation resumed in its sequential round
// (harness.tool-hooks.calls-to-tools-the-request-did-not-offer.keep-their-flag-in-a-version-2-generation-s-...), version 1 and 2 tool
// tasks (harness.nested-tools.version-1-and-2.*), and nested call documents without `kind`, which a resumed caller reattaches to
// (compat.nested-tools-restart.*.resume, over the v1.2.0 capture).
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage.Jsonl;
using PiSharp.Durable.Testing;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> CompatUpgrade152Cases() => CompatTests.Upgrade152Cases();
}

internal static partial class CompatTests
{
    /// <summary>The first run of the Pi v1.5.0 capture (Fixtures/v1.5.0/upstream-sessions/first/&lt;backend&gt;/&lt;stage&gt;).</summary>
    private static string Fixtures150(string backend) =>
        Path.Combine(RepositoryRoot(), "tests", "PiSharp.Durable.Tests", "Fixtures", "v1.5.0", "upstream-sessions", "first", backend);

    private static string CopyStage(string stage, string backend)
    {
        var target = Temp($"compat-152-{stage}-{backend}");
        foreach (var file in Directory.GetFiles(Path.Combine(Fixtures150(backend), stage)))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        return target;
    }

    private static DurableTask Captured(string name, Dictionary<string, TaskPhaseHandler> phases) => Tasks.DefineTask(new TaskDefinition
    {
        Name = name,
        Version = 1,
        Initial = _ => O("""{"phase":"start"}"""),
        Phases = phases,
        Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => AbortedWith("test"), ctx),
    });

    internal static IEnumerable<(string, Func<Task>)> Upgrade152Cases()
    {
        const string prefix = "compat.upgrade-from-v1.5.2.";
        foreach (var backend in Backends)
        {
            var records = (JsonObjectValue)JsonValue.Parse(File.ReadAllText(Path.Combine(Fixtures150(backend), "records.json")));
            foreach (var (stage, expected) in records)
                yield return Case(AuthoredCase($"{prefix}{stage}.{backend}.read"), async () =>
                    SameJson(expected, await Dump(await OpenBackend(backend, CopyStage(stage, backend))), "records"));

            yield return Case(AuthoredCase($"{prefix}sleeping.{backend}.wakes-a-sleep-stored-with-until"), async () =>
            {
                var stored = (JsonObjectValue)((JsonArrayValue)((JsonObjectValue)records["sleeping"])["tasks"])[0];
                var id = new TaskId((long)N(stored["id"]));
                var until = N(((JsonObjectValue)stored["state"])["until"]);
                var wakes = new SyncList<string?>();
                var nap = Captured("capture150.nap", new Dictionary<string, TaskPhaseHandler>
                {
                    ["after"] = async (task, runtime, ctx) =>
                    {
                        wakes.Add(task.Wake?.ToJson().ToJson());
                        await runtime.CommitAsync((_, _) => Completed("woke"), ctx);
                    },
                });
                var clock = until - 1;
                var (harness, _, _) = await OpenTasks(await OpenBackend(backend, CopyStage("sleeping", backend)), [nap],
                    now: () => Volatile.Read(ref clock), onWake: _ => { });
                SameJson(stored["state"], (await harness.GetTaskAsync(id, Context))!.State.ToJson(), "the stored sleep");
                harness.Resume();
                await Eventually(() => !harness.Wake().Active);
                Equal(new Wake(false, until), harness.Wake(), "wake while asleep");
                var inspected = (await harness.InspectAsync(Context)).Tasks.First(task => task.Record.Id == id).State;
                Equal(TaskInspectionKind.Sleeping, inspected.Kind, "inspection kind");
                Equal<double?>(until, inspected.Until, "inspection until");
                Volatile.Write(ref clock, until);
                harness.Resume();
                var settled = await harness.WaitForTaskAsync(id, Context);
                SessionTests.SameJson("""{"status":"completed","result":"woke"}""", ((SettlingTaskState)settled.State).Outcome.ToJson(), "outcome");
                Check(wakes.Snapshot().SequenceEqual(["""{"reason":"time"}"""]), "wake: " + string.Join("|", wakes.Snapshot()));
                await harness.CloseAsync(Context);
            });

            yield return Case(AuthoredCase($"{prefix}process-exit.{backend}.counts-the-restart-and-runs-the-task-again"), async () =>
            {
                var stored = (JsonObjectValue)((JsonArrayValue)((JsonObjectValue)records["process-exit"])["tasks"])[0];
                var id = new TaskId((long)N(stored["id"]));
                var seen = new SyncList<string>();
                var restart = Captured("capture150.restart", new Dictionary<string, TaskPhaseHandler>
                {
                    ["start"] = async (task, runtime, ctx) =>
                    {
                        seen.Add($"restarts {task.Restarts?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, wake {task.Wake?.ToJson().ToJson() ?? "none"}");
                        await runtime.CommitAsync((_, _) => Completed("resumed"), ctx);
                    },
                });
                var (harness, _, _) = await OpenTasks(await OpenBackend(backend, CopyStage("process-exit", backend)), [restart]);
                var opened = (await harness.GetTaskAsync(id, Context))!;
                SessionTests.SameJson("""{"status":"pending","checkpoint":{"phase":"start"}}""", opened.State.ToJson(), "pending again, without a wake");
                Equal<int?>(1, opened.Restarts, "restarts");
                var settled = await harness.WaitForTaskAsync(id, Context);
                SessionTests.SameJson("""{"status":"completed","result":"resumed"}""", ((SettlingTaskState)settled.State).Outcome.ToJson(), "outcome");
                Check(seen.Snapshot().SequenceEqual(["restarts 1, wake none"]), "seen: " + string.Join("|", seen.Snapshot()));
                await harness.CloseAsync(Context);
            });

            yield return Case(AuthoredCase($"{prefix}compaction.{backend}.answers-the-next-input-with-a-version-3-generation"), async () =>
            {
                var before = ((JsonArrayValue)((JsonObjectValue)records["compaction"])["tasks"]).Cast<JsonObjectValue>()
                    .ToDictionary(task => (long)N(task["id"]), task => (int)N(task["version"]));
                var setup = new ChatSetup(new FauxModels(new FauxOptions { Api = "faux" }));
                setup.Faux.SetResponses(FauxAssistantMessage("after the upgrade"));
                var (harness, root) = await OpenChat(await OpenBackend(backend, CopyStage("compaction", backend)), setup);
                var submission = await root.SubmitAsync(SubmissionDraft.Input("one more"), Context);
                var settled = await submission.WaitAsync(Context);
                Equal(SubmissionStatus.Done, settled.Status, "the new input");
                var tasks = (await harness.CommitAsync(tx => tx.ScanTasksAsync(new TaskQuery(), 1000), Context)).Items;
                foreach (var task in tasks.Where(task => before.ContainsKey(task.Id.Value)))
                    Equal(before[task.Id.Value], task.Version, $"stored task {task.Id} keeps its version");
                var created = tasks.Where(task => !before.ContainsKey(task.Id.Value)).ToList();
                Check(created.Count == 1 && created[0] is { Kind: "pi.generation", Version: 3 },
                    "the new generation: " + string.Join(",", created.Select(task => $"{task.Kind} v{task.Version}")));
                Check((await AllEntries(root)).Any(entry => Entries.AssistantEntry.Is(entry) && entry.ToJson().ToJson().Contains("after the upgrade", StringComparison.Ordinal)),
                    "the answer is in the transcript");
                await harness.CloseAsync(Context);
            });

            foreach (var interrupted in new[] { false, true })
                yield return Case(AuthoredCase($"{prefix}tool-task.{backend}." +
                    (interrupted ? "runs-a-version-3-task-cut-off-after-a-replay-safe-intent-again" : "reserves-a-pending-version-3-task-under-version-4-and-runs-it")),
                    async () =>
                    {
                        var directory = Temp($"compat-152-tool-{backend}");
                        var setup = Setup();
                        var echo = new NestedToolsTests.EchoTool();
                        AddTool(setup.Registry, echo.Registration with { Replay = ToolReplay.Safe });
                        // No submission, so the Harness never starts scheduling and the task stays pending.
                        var (first, root) = await OpenChat(await OpenBackend(backend, directory), setup);
                        var id = await root.CommitAsync(async tx =>
                        {
                            var entry = await tx.AppendEntryAsync(Entries.AssistantEntry, root.Id,
                                new TypedEntryDraft { Model = JsonValue.Array(NestedToolsTests.Call("echo", Obj(("text", "old")))) });
                            return await tx.CreateTaskAsync(HarnessTasks.ToolTask,
                                Obj(("kind", "model"), ("assistant", entry.Id.ToJson()), ("callId", "c1")), new TaskOptions(TaskOwnership.Conversation));
                        }, Context);
                        await first.CloseAsync(Context);
                        // The record as 1.5.2 stored it: version 3, and for the interrupted one running at its intent since startedAt.
                        var storage = await OpenBackend(backend, directory);
                        var record = (await storage.GetTaskAsync(id, Context))!;
                        await storage.CommitWritesAsync([new TaskWrite(interrupted
                            ? record with
                            {
                                Version = 3, StartedAt = setup.Now() - 5,
                                State = new RunningTaskState(O("""{"phase":"execute","arguments":{"text":"old"},"replay":"safe"}""")),
                            }
                            : record with { Version = 3 })], Context);
                        await storage.CloseAsync(Context);
                        var (harness, reopened) = await OpenChat(await OpenBackend(backend, directory), setup);
                        Equal(3, (await harness.GetTaskAsync(id, Context))?.Version, "stored version");
                        harness.Resume();
                        var settled = await harness.WaitForTaskAsync(id, Context);
                        Equal(4, settled.Version, "version");
                        MatchJson("""{"status":"completed","result":{"kind":"model"}}""", NestedToolsTests.OutcomeOf(settled), "outcome");
                        Equal(1, echo.Runs, "runs");
                        var result = NestedToolsTests.Results(await AllEntries(reopened))[0];
                        Equal("c1 echo old", $"{S(result["toolCallId"])} {NestedToolsTests.Text(result)}", "result");
                        Check(result["durationMs"] is JsonNumberValue { Value: >= 0 }, "durationMs: " + result.ToJson());
                        await harness.CloseAsync(Context);
                    });
        }
    }
}
