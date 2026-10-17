// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/types.test.ts, its third test ("types submissions, task waits,
// compaction, and tasks erased into extensions"); TypesCases.cs has the other two. Upstream's test is compile-time only (expectTypeOf and
// @ts-expect-error); so is most of this one: TypedWaits below is compiled, never run, and pins the C# types of the same calls. What C# can
// check at run time, it checks: the two submission shapes, and that the shapes upstream rejects at compile time cannot be built here either
// (no public constructor, no public setters, no busy policy on a write). Mappings, recorded in the port map:
// - Tasks are JsonValue-typed, as documents are JsonObjectValue-typed: upstream's phantom result type (TaskId<R>, SettledTask<R>), its typed
//   task input and its exhaustive phase map have no compile-time counterpart. WaitForTaskAsync returns the TaskRecord; a missing phase fails
//   when the task reaches it.
// - A task's hooks are a handler class (upstream's Hooks type parameter); EachAsync's selector types the handler, so `handler` below has the
//   hook's delegate type, as upstream's expectTypeOf(handler) checks. Define.Hook takes the handler object.
// - Erasure into an extension needs no cast: every task is a DurableTask.
using System.Reflection;
using PiSharp.Durable;
using PiSharp.Durable.Chord;

internal static partial class Program
{
    private sealed record StepDecision(bool Skip);

    // The stepper's custom hooks (upstream `type Hooks = { beforeStep(step, api, context): { skip } | undefined }`).
    private sealed class StepperHooks
    {
        public Func<int, IHookApi, DurableContext, StepDecision?>? BeforeStep { get; init; }
    }

    private static IEnumerable<(string, Func<Task>)> TypesHarnessCases() =>
    [
        Case("types.types-submissions-task-waits-compaction-and-tasks-erased-into-extensions", () =>
        {
            var input = SubmissionDraft.Input("hi", WhenBusy.Steer);
            var write = SubmissionDraft.Write(new EntryDraft { Kind = "note" });
            Equal(SubmissionType.Input, input.Type, "input type");
            Equal(SubmissionType.Write, write.Type, "write type");
            // @ts-expect-error: an input's entry has no model (its model message is the input's content), a write carries no content and no
            // busy policy. The factories are the only constructors.
            Check(typeof(SubmissionDraft).GetConstructors().Length == 0, "SubmissionDraft has a public constructor");
            Check(typeof(SubmissionDraft).GetProperties().All(property => property.SetMethod is not { IsPublic: true }), "SubmissionDraft has a public setter");
            Check(typeof(SubmissionDraft).GetMethod(nameof(SubmissionDraft.Write))!.GetParameters().All(parameter => parameter.ParameterType != typeof(WhenBusy?)),
                "a write takes a busy policy");
            Check(typeof(SubmissionDraft).GetMethod(nameof(SubmissionDraft.Input))!.GetParameters().All(parameter => parameter.ParameterType != typeof(EntryDraft)),
                "an input takes an entry draft");
            Check(typeof(InputEntry).GetProperties().All(property => property.Name is "Kind" or "Data"), "an input entry has a model, head or edits");
            Equal(new InputEntry("chat.mention"), SubmissionDraft.Input("hi", entry: new InputEntry("chat.mention")).InputEntry, "input entry");

            // A task with input, several phases and custom hooks.
            var stepper = Tasks.DefineTask(new TaskDefinition
            {
                Name = "test.stepper",
                Version = 1,
                Initial = taskInput => JsonValue.Object([new("phase", "plan"), new("steps", ((JsonObjectValue)taskInput)["steps"])]),
                Phases = new Dictionary<string, TaskPhaseHandler>
                {
                    ["plan"] = (_, runtime, context) =>
                        runtime.CommitAsync((_, _) => NextTaskState.Running(JsonValue.Object([new("phase", "run"), new("step", 0)])), context),
                    ["run"] = async (task, runtime, context) =>
                    {
                        await runtime.Hooks.EachAsync<StepperHooks, Func<int, IHookApi, DurableContext, StepDecision?>>(hooks => hooks.BeforeStep, handler =>
                        {
                            Func<int, IHookApi, DurableContext, StepDecision?> typed = handler;
                            _ = typed;
                            return Task.CompletedTask;
                        });
                        await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Object([new("ran", 1)])), context);
                    },
                },
                Abort = (_, _, _) => Task.CompletedTask,
                Hooks = new StepperHooks(),
            });
            var registration = Define.Hook(stepper, new StepperHooks { BeforeStep = (step, _, _) => step > 1 ? new StepDecision(true) : null });
            Equal("test.stepper", registration.Task, "hook target");
            // Erased into an extension, whatever its input, phases and hooks.
            DurableTask erased = stepper;
            var extension = new Extension { Name = "stepper", Tasks = [erased], Hooks = [registration] };
            Equal(1, extension.Tasks.Count, "extension tasks");

            // Compiled, never run: the waits and compaction are typed by the Harness surface.
            static async Task TypedWaits(IHarness harness, IConversation conversation, ITaskRuntime runtime, DurableTask stepper)
            {
                TaskId id = await conversation.CommitAsync(tx => tx.CreateTaskAsync(stepper, JsonValue.Object([new("steps", 2)]),
                    new TaskOptions(TaskOwnership.Conversation)), DurableContext.Background);
                TaskRecord settled = await harness.WaitForTaskAsync(id, DurableContext.Background);
                if (settled.State is TerminalTaskState { Outcome: CompletedTaskOutcome completed })
                {
                    JsonValue? result = completed.Result;
                    _ = result;
                }
                TaskRecord fromRuntime = await runtime.WaitForTaskAsync(id, DurableContext.Background);
                TaskId compaction = await conversation.CompactAsync(null, DurableContext.Background);
                TaskRecord compacted = await harness.WaitForTaskAsync(compaction, DurableContext.Background);
                _ = (fromRuntime, compacted);
            }
            Func<IHarness, IConversation, ITaskRuntime, DurableTask, Task> typedWaits = TypedWaits;
            Check(typedWaits.Method.ReturnType == typeof(Task), "typed waits compile");
        }),
    ];
}
