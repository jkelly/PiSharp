// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/durable/test/examples/38-signals.ts and 39-ask-a-person.ts (Pi v1.6.0).
// Each prints what upstream's console.log prints, its arguments joined with spaces: a string as it is, any other value as JSON in place of
// util.inspect (a delivery's result is the string upstream's is, such as `delivered`; `harness.wake()` is `{"active":false}`).
// pi-schema's obj({ text: str() }) is its JSON Schema. `await new Promise((r) => setTimeout(r, 1))` loops are UntilAsync.
// PiSharp-only:
// - ExampleTurn fixes the order of a task's line and the host's where Node's single thread fixes upstream's: tasks run on other threads
//   here. In 38 the first greeter starts its work once the host made its four deliveries (so the retry finds the task alive, as it does
//   upstream, where the task has not run yet), and each greeter prints once the host printed the delivery that woke it; in 39 the task
//   prints once the host printed its delivery.
// - 39 waits for the last onWake call instead of 1 ms: onWake is called from the scheduler here, not in a microtask.
// - On a file backend the storage is reopened in a plain Session and the tasks' outcomes read back, as every example's read-back.
using System.Collections.Concurrent;
using PiSharp.Durable.Chord;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    /// <summary>PiSharp-only: holds a task's line until the host printed its own (see the file header).</summary>
    private sealed class ExampleTurn
    {
        private volatile TaskCompletionSource gate = Open();

        private static TaskCompletionSource Open()
        {
            var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            open.SetResult();
            return open;
        }

        /// <summary>The next <see cref="WaitAsync"/> waits for <see cref="Release"/>.</summary>
        public void Hold() => gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the waiting task go on.</summary>
        public void Release() => gate.TrySetResult();

        /// <summary>Resolves once the host released the turn; at once when it does not hold it.</summary>
        public Task WaitAsync(CancellationToken cancellationToken) => gate.Task.WaitAsync(cancellationToken);
    }

    /// <summary><c>while (!condition) await new Promise((r) =&gt; setTimeout(r, 1))</c>, for at most a minute (ExamplesTranscript.cs has the
    /// synchronous form).</summary>
    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        for (var started = Environment.TickCount64; !await condition();)
        {
            if (Environment.TickCount64 - started > 60_000) throw new TimeoutException("The example's work did not reach the state it waits for.");
            await Task.Delay(1);
        }
    }

    /// <summary>pi-schema's <c>obj({ [name]: str() })</c>: a closed object with the property required.</summary>
    private static JsonObjectValue TextSchema(string name) => (JsonObjectValue)JsonValue.Parse(
        $$$"""{"type":"object","properties":{"{{{name}}}":{"type":"string"}},"required":["{{{name}}}"],"additionalProperties":false}""");

    /// <summary>A delivery's result as upstream names it.</summary>
    private static string ResultName(SignalResult result) => result switch
    {
        SignalResult.Delivered => "delivered",
        SignalResult.Duplicate => "duplicate",
        SignalResult.Invalid => "invalid",
        SignalResult.Closed => "closed",
        _ => "not_found",
    };

    /// <summary><c>harness.wake()</c> as JSON: <c>{active, at?}</c>.</summary>
    private static string WakeJson(Wake wake) =>
        Json(wake.At is { } at ? JsonValue.Object([new("active", wake.Active), new("at", at)]) : JsonValue.Object([new("active", wake.Active)]));

    /// <summary>A signal key's state as <c>JSON.stringify</c> prints upstream's: <c>{value}</c>, <c>{expired: true}</c>, or undefined.</summary>
    private static string StateJson(SignalState? state) => state is null ? "undefined"
        : state.Expired ? """{"expired":true}""" : Json(JsonValue.Object([new("value", state.Value!)]));

    private static async Task<string> OutcomeJsonAsync(IHarness harness, TaskId id) =>
        Json(((SettlingTaskState)(await harness.WaitForTaskAsync(id, Context)).State).Outcome.ToJson());

    private static async Task PrintOutcomesAsync(ISession session, TextWriter output, params TaskId[] ids)
    {
        foreach (var id in ids)
        {
            var record = await session.CommitAsync(tx => tx.GetTaskAsync(id), Context);
            await output.WriteLineAsync($"task {id}: " + (record?.State is SettlingTaskState settled ? Json(settled.Outcome.ToJson()) : "missing"));
        }
    }

    // 38: signals: a typed JSON value delivered to a live task from outside, and what each delivery returns. A value that arrives before the
    // task sleeps on its key is kept, and the sleep wakes at once.
    private static async Task SignalsAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        // A signal kind: a name, a version, and a JSON Schema of a JSON value. Deliveries are checked against it.
        var greeting = Signals.DefineSignal(new SignalDefinition { Name = "example.greeting", Version = 1, Schema = TextSchema("text") });
        var started = new ExampleTurn();
        var turn = new ExampleTurn();

        // A task that first does some work, then sleeps until a greeting arrives under the key "hello".
        var greeter = Tasks.DefineTask(new TaskDefinition
        {
            Name = "example.greeter",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "work")]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["work"] = async (_, runtime, ctx) =>
                {
                    await started.WaitAsync(ctx.CancellationToken);
                    await runtime.CommitAsync((_, _) => NextTaskState.Sleeping(JsonValue.Object([new("phase", "greeted")]), null, [greeting.Key("hello")]), ctx);
                },
                ["greeted"] = async (task, runtime, ctx) =>
                {
                    // The wake names the key; ReadSignalAsync is how the value is read.
                    var wake = task.Wake;
                    var state = await runtime.ReadSignalAsync(greeting, "hello", ctx);
                    var text = state?.Value is JsonObjectValue value ? ((JsonStringValue)value["text"]).Value : "(none)";
                    await turn.WaitAsync(ctx.CancellationToken);
                    await output.WriteLineAsync($"  [greeter] woke by {(wake is null ? "undefined" : Json(wake.ToJson()))}, read \"{text}\"");
                    await runtime.CommitAsync((_, _) => NextTaskState.Completed(text), ctx);
                },
            },
            Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
        });

        var registry = Registry.Create();
        registry.Install(new Extension { Name = "greeter", Tasks = [greeter] });
        var harness = await Harness.OpenAsync(await open(),
            new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry, OnWake = _ => { } }, Context);
        var root = await harness.RootAsync(Context);
        started.Hold();
        var id = await root.CommitAsync(tx => tx.CreateTaskAsync(greeter, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
        static JsonObjectValue Text(string text) => JsonValue.Object([new("text", text)]);

        // 1. Delivered before the task ran at all: kept in the task. SignalAsync also starts scheduling.
        await LogAsync(output, "early:", ResultName(await harness.SignalAsync(id, greeting, "hello", Text("hi there"), Context)));
        // 2. Webhook retries of the same key are answered `duplicate`.
        await LogAsync(output, "retry:", ResultName(await harness.SignalAsync(id, greeting, "hello", Text("hi again"), Context)));
        // 3. A value that does not match the schema is `invalid`; nothing is stored.
        await LogAsync(output, "bad value:",
            ResultName(await harness.SignalAsync(id, greeting, "other", JsonValue.Object([new("text", 42)]), Context)));
        // 4. No such task.
        await LogAsync(output, "unknown task:", ResultName(await harness.SignalAsync(new TaskId(999_999), greeting, "hello", Text("?"), Context)));
        started.Release();

        // The sleep finds the early value and wakes at once.
        await LogAsync(output, "result:", await OutcomeJsonAsync(harness, id));
        // 5. The task ended: every later delivery is `closed`.
        await LogAsync(output, "after the end:", ResultName(await harness.SignalAsync(id, greeting, "hello", Text("too late"), Context)));

        // A second greeter sleeps first. While it sleeps it holds no invocation: the Session needs no process (`active` false) and has no
        // deadline (`at` absent), so a host could let it hibernate until a delivery comes.
        var second = await root.CommitAsync(tx => tx.CreateTaskAsync(greeter, JsonValue.Null, new TaskOptions(TaskOwnership.Conversation)), Context);
        harness.Resume();
        await UntilAsync(async () => (await harness.GetTaskAsync(second, Context))?.State.Status == DurableTaskStatus.Sleeping);
        await UntilAsync(() => !harness.Wake().Active);
        await LogAsync(output, "asleep, wake:", WakeJson(harness.Wake()));
        turn.Hold();
        await LogAsync(output, "late:", ResultName(await harness.SignalAsync(second, greeting, "hello", Text("good morning"), Context)));
        turn.Release();
        await LogAsync(output, "result:", await OutcomeJsonAsync(harness, second));
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, reopened => PrintOutcomesAsync(reopened, output, id, second));
    }

    // 39: a plugin task that asks a person and sleeps until the answer comes or a deadline passes, holding no process meanwhile. The first
    // question is answered; the second expires, and its late answer is refused.
    private static async Task AskAPersonAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        const double hour = 60 * 60 * 1000;

        // The Harness clock, moved by hand so the deadline comes without waiting four hours: Date.UTC(2026, 10, 9, 9).
        double clock = new DateTimeOffset(2026, 11, 9, 9, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var answer = Signals.DefineSignal(new SignalDefinition { Name = "example.answer", Version = 1, Schema = TextSchema("text") });
        var turn = new ExampleTurn();

        // What shows the question to a person (an inbox, a card, a chat bridge) keeps its own record of it: Durable stores no description of
        // what a sleep waits for, only its keys and deadline.
        var inbox = new ConcurrentDictionary<TaskId, string>();

        var askPerson = Tasks.DefineTask(new TaskDefinition
        {
            Name = "example.ask-person",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "ask")]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["ask"] = async (task, runtime, ctx) =>
                {
                    inbox[runtime.TaskId] = ((JsonStringValue)((JsonObjectValue)task.Input)["question"]).Value;
                    // Sleep until the answer arrives or four hours pass, whichever comes first.
                    await runtime.CommitAsync((_, _) =>
                        NextTaskState.Sleeping(JsonValue.Object([new("phase", "answered")]), runtime.Now() + 4 * hour, [answer.Key("answer")]), ctx);
                },
                ["answered"] = async (task, runtime, ctx) =>
                {
                    inbox.TryRemove(runtime.TaskId, out _);
                    // `signal` when answered, `expired` when the deadline came first.
                    var state = await runtime.ReadSignalAsync(answer, "answer", ctx);
                    JsonValue text = state?.Value is JsonObjectValue value ? value["text"] : JsonValue.Null;
                    await turn.WaitAsync(ctx.CancellationToken);
                    var reason = task.Wake is null ? "undefined" : ((JsonStringValue)task.Wake.ToJson()["reason"]).Value;
                    await output.WriteLineAsync($"  [ask] woke: {reason}, read: {StateJson(state)}");
                    await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Object([new("answer", text)])), ctx);
                },
            },
            Abort = (_, runtime, ctx) => runtime.CommitAsync((_, _) => NextTaskState.Aborted(), ctx),
        });

        // A host that wakes the process itself, as a Durable Object alarm does: it is told when work needs a process.
        var wakes = new ConcurrentQueue<Wake>();
        var registry = Registry.Create();
        registry.Install(new Extension { Name = "ask", Tasks = [askPerson] });
        var harness = await Harness.OpenAsync(await open(), new HarnessOptions
        {
            Models = EmptyDurableModels.Instance, Registry = registry, Now = () => Volatile.Read(ref clock), OnWake = wakes.Enqueue,
        }, Context);
        var root = await harness.RootAsync(Context);
        Task<TaskId> Ask(string question) => root.CommitAsync(tx =>
            tx.CreateTaskAsync(askPerson, JsonValue.Object([new("question", question)]), new TaskOptions(TaskOwnership.Conversation)), Context);
        async Task Asleep(TaskId id)
        {
            await UntilAsync(async () => (await harness.GetTaskAsync(id, Context))?.State.Status == DurableTaskStatus.Sleeping);
            await UntilAsync(() => !harness.Wake().Active);
        }
        string Inbox() => Json(JsonValue.Array(inbox.Values.Select(question => (JsonValue)question)));

        // 1. Answered in time.
        var first = await Ask("Deploy to production?");
        harness.Resume();
        await Asleep(first);
        await LogAsync(output, "inbox:", Inbox());
        // Nothing runs: no process needed until the deadline, four hours from now.
        await LogAsync(output, "wake while asleep:", WakeJson(harness.Wake()), "deadline in",
            Number((harness.Wake().At!.Value - clock) / hour), "h");
        turn.Hold();
        await LogAsync(output, "deliver:",
            ResultName(await harness.SignalAsync(first, answer, "answer", JsonValue.Object([new("text", "yes")]), Context)));
        turn.Release();
        await LogAsync(output, "result:", await OutcomeJsonAsync(harness, first));

        // 2. Nobody answers. The host's alarm fires at the deadline and resumes the Harness.
        var second = await Ask("Delete the staging database?");
        await Asleep(second);
        await LogAsync(output, "inbox:", Inbox());
        Volatile.Write(ref clock, harness.Wake().At!.Value);
        harness.Resume();
        await LogAsync(output, "result:", await OutcomeJsonAsync(harness, second));
        // The key expired with the deadline: a late answer is refused.
        await LogAsync(output, "late deliver:",
            ResultName(await harness.SignalAsync(second, answer, "answer", JsonValue.Object([new("text", "yes")]), Context)));
        // onWake is called after the commit that changed the value.
        await UntilAsync(() => wakes.LastOrDefault() is { Active: false, At: null });
        await LogAsync(output, "last onWake:", WakeJson(wakes.Last()));
        await harness.CloseAsync(Context);

        await ReopenAsync(open, output, reopened => PrintOutcomesAsync(reopened, output, first, second));
    }
}
