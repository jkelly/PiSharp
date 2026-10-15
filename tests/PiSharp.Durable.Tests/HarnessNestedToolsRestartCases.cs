// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/harness-nested-tools-restart.test.ts, the one case that needs no
// nested call: "abandons the child tasks a replay-unsafe tool created and awaited in memory" (TaskOptions.abandonOnRestart, durable work list
// area c). The other cases make nested calls (api.executeTool) and arrive with area a (tool task v2), which owns this file's row.
//
// Adaptations (beyond those of HarnessSupport.cs and ChatSupport.cs):
// - Upstream's api.createTask() gives a child task of a tool that is not replay-safe abandonOnRestart by default; area a wires that default
//   (ToolExecutionApi.ChildTaskOptions), so until then the tool passes AbandonOnRestart = true itself, which is what the default produces.
// - PiSharp's pi.tool is still version 1 until area a: the interrupted call settles as version 1 does. The case checks what upstream's does
//   (the child's phases, with its abort reason, and that the run settles) and that the child record carries abandonOnRestart.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using static ChatTests;
using static HarnessTests;
using static Program;
using static SessionTests;
using Deferred = HarnessTests.Deferred;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> HarnessNestedToolsRestartCases() => SchedulerTests.NestedToolsRestartCases();
}

internal static partial class SchedulerTests
{
    internal static IEnumerable<(string, Func<Task>)> NestedToolsRestartCases()
    {
        const string prefix = "harness.nested-tools-restart.";

        // Authored: the hook area a applies in api.createTask() (ToolExecutionApi.ChildTaskOptions) gives a child task of a caller that does
        // not resume abandonOnRestart, as upstream's `{ abandonOnRestart: true, ...options }` does; an explicit value and a conversation-owned
        // task keep their options.
        yield return Case(prefix + "child-task-options-default-abandon-on-restart-for-callers-that-do-not-resume", () =>
        {
            var child = new TaskOptions(TaskOwnership.Task(new TaskId(7)));
            Equal(true, ToolExecutionApi.ChildTaskOptions(child, resumes: false).AbandonOnRestart, "a child of a caller that does not resume");
            Equal<bool?>(null, ToolExecutionApi.ChildTaskOptions(child, resumes: true).AbandonOnRestart, "a child of a replay-safe caller");
            Equal(false, ToolExecutionApi.ChildTaskOptions(child with { AbandonOnRestart = false }, resumes: false).AbandonOnRestart, "explicit");
            var top = new TaskOptions(TaskOwnership.Conversation);
            Equal<bool?>(null, ToolExecutionApi.ChildTaskOptions(top, resumes: false).AbandonOnRestart, "a conversation-owned task");
        });

        yield return Case(prefix + "abandons-the-child-tasks-a-replay-unsafe-tool-created-and-awaited-in-memory", async () =>
        {
            var path = Path.Combine(Temp("nested-restart"), "session.sqlite");
            var setup = Setup();
            var phases = new SyncList<string>();
            var started = new Deferred();
            var worker = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.direct-worker",
                Version = 1,
                Initial = _ => O("""{"phase":"work"}"""),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["work"] = async (_, runtime, _) =>
                    {
                        phases.Add("work");
                        started.Resolve();
                        await Aborted(runtime.Signal);
                    },
                },
                Abort = async (task, runtime, abortContext) =>
                {
                    phases.Add($"abort {(task.AbortReason is { } reason ? StructuredTests.Wire(reason) : "undefined")}");
                    await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), abortContext);
                },
            });
            setup.Registry.Install(Define.Extension(new Extension { Name = "direct-worker", Tasks = [worker] }));
            TaskId? workerId = null;
            AddTool(setup.Registry, Define.Tool(new ToolRegistration
            {
                Name = "work",
                Description = "work",
                Parameters = ToolRegistration.EmptyParameters,
                Execute = async (_, api, callContext) =>
                {
                    // Area a moves this option into api.createTask()'s default for tools that are not replay-safe.
                    var id = await api.CreateTaskAsync(worker, JsonValue.Null,
                        new TaskOptions(TaskOwnership.Task(api.TaskId)) { AbandonOnRestart = true }, callContext);
                    workerId = id;
                    await api.WaitForTaskAsync(id, callContext);
                    return new ToolExecutionResult();
                },
            }));
            setup.Faux.SetResponses(FauxAssistantMessage([FauxToolCall("work", JsonObjectValue.Empty, "c1")], stopReason: "toolUse"),
                FauxAssistantMessage("done"));
            var opened = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            var id = (await opened.Root.SubmitAsync(SubmissionDraft.Input("go"), Context)).Id;
            await started.Task;
            await opened.Harness.CloseAsync(Context);

            opened = await OpenChat(await SqliteStorage.OpenAsync(path), setup);
            opened.Harness.Resume();
            Equal(SubmissionStatus.Done, (await (await opened.Harness.SubmissionAsync(id, Context))!.WaitAsync(Context)).Status, "submission");
            Seq(["work", "abort restart"], phases.Snapshot(), "phases");
            Check((await opened.Harness.GetTaskAsync(workerId!.Value, Context))!.AbandonOnRestart, "the child is abandonOnRestart");
            await opened.Harness.CloseAsync(Context);
        });
    }
}
