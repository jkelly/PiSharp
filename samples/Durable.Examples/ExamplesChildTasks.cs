// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/examples/24-child-tasks.ts. Upstream times its fake bank with
// Date.now() and setTimeout; here an ExampleClock drives the Harness clock (HarnessOptions.TimeProvider), so a payment's sleep ends only when
// the example advances the clock, and two barriers stand in for what Node's event loop orders: a declined card is declined only after the
// checkout's other payments were charged (upstream runs every reserved payment's first step before the decline's commit lands), and the
// example inspects or stops the payments once all four were charged (upstream waits 20 ms). Upstream keeps the storage in a temporary SQLite
// file; here that holds on the memory run, and a file backend reopens the runner's storage. Abort handlers print in task order.
using PiSharp.Durable.Chord;
using PiSharp.Durable.Sqlite;
using PiSharp.Durable.Storage;

namespace PiSharp.Durable.Examples;

public static partial class Examples
{
    // 24: a task that owns child tasks: a checkout charges four payments at once and waits for them. The task graph view prints the
    // checkout's tree while its payments run.
    private static async Task ChildTasksAsync(Func<Task<IStorage>> open, TextWriter output)
    {
        string? directory = null;
        var probe = await open();
        await probe.CloseAsync(Context);
        if (probe is MemoryStorage)
        {
            directory = ExampleDirectory("pi-durable-example-");
            var databasePath = Path.Combine(directory, "session.sqlite");
            open = async () => await SqliteStorage.OpenAsync(databasePath);
        }
        var clock = new ExampleClock();

        // A fake bank: it declines an expired card at once, and confirms other charges after a moment.
        var charged = new HashSet<string>();
        // Barrier: the cards charged per checkout, released once every card of the checkout that is not expired was charged.
        var chargedPerCheckout = new Dictionary<TaskId, (int Count, TaskCompletionSource All)>();
        (int Count, TaskCompletionSource All) Charges(TaskId checkout)
        {
            lock (chargedPerCheckout)
            {
                if (!chargedPerCheckout.TryGetValue(checkout, out var entry))
                    chargedPerCheckout[checkout] = entry = (0, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                return entry;
            }
        }
        async Task<string[]> Cards(ITaskRuntime runtime, TaskId checkout, DurableContext context) =>
            [.. ((JsonArrayValue)((JsonObjectValue)(await runtime.GetTaskAsync(checkout, context))!.Input)["cards"]).Select(card => ((JsonStringValue)card).Value)];

        string Card(RunningTask task) => ((JsonStringValue)((JsonObjectValue)task.Input)["card"]).Value;

        var payment = Tasks.DefineTask(new TaskDefinition
        {
            Name = "example.payment",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "charge"), new("at", clock.NowMs() + 100)]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["charge"] = async (task, runtime, taskContext) =>
                {
                    var card = Card(task);
                    var checkout = task.Owner!.Value;
                    if (card.StartsWith("expired", StringComparison.Ordinal))
                    {
                        await Charges(checkout).All.Task.WaitAsync(taskContext.CancellationToken);
                        await runtime.CommitAsync((_, _) => NextTaskState.Failed($"{card} declined"), taskContext);
                        return;
                    }
                    lock (charged) charged.Add(card);
                    var others = (await Cards(runtime, checkout, taskContext)).Count(other => !other.StartsWith("expired", StringComparison.Ordinal));
                    lock (chargedPerCheckout)
                    {
                        var (count, all) = Charges(checkout);
                        chargedPerCheckout[checkout] = (count + 1, all);
                        if (count + 1 == others) all.TrySetResult();
                    }
                    await runtime.SleepAsync(((JsonNumberValue)((JsonObjectValue)task.Checkpoint)["at"]).Value, taskContext);
                    await runtime.CommitAsync((_, _) => NextTaskState.Completed(JsonValue.Object([new("card", card)])), taskContext);
                },
            },
            // Each payment undoes its own effect when aborted.
            Abort = async (task, runtime, taskContext) =>
            {
                // Barrier: abort handlers run one after another in task order, as upstream's do; wait for the checkout's earlier payments.
                if (task.Owner is { } owner && (await runtime.GetTaskAsync(owner, taskContext))?.State is LiveTaskState { Checkpoint: JsonObjectValue checkpoint } &&
                    checkpoint.TryGetValue("payments", out var siblings))
                    foreach (var sibling in ((JsonArrayValue)siblings).Select(value => new TaskId((long)((JsonNumberValue)value).Value)).Where(sibling => sibling.Value < task.Id.Value))
                        await runtime.WaitForTaskAsync(sibling, taskContext);
                bool refunded;
                lock (charged) refunded = charged.Remove(Card(task));
                await output.WriteLineAsync($"  payment {Card(task)} aborted{(refunded ? ", refunded" : "")}");
                await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), taskContext);
            },
        });

        // The checkout creates its payments as child tasks and waits for all of them. With FailFast, the first payment that fails aborts the
        // others; the checkout itself is not aborted and decides its outcome once every payment is done.
        var checkoutTask = Tasks.DefineTask(new TaskDefinition
        {
            Name = "example.checkout",
            Version = 1,
            Initial = _ => JsonValue.Object([new("phase", "pay")]),
            Phases = new Dictionary<string, TaskPhaseHandler>
            {
                ["pay"] = (task, runtime, taskContext) => runtime.CommitAsync(async (tx, _) =>
                {
                    var payments = new List<TaskId>();
                    foreach (var card in (JsonArrayValue)((JsonObjectValue)task.Input)["cards"])
                        payments.Add(await tx.CreateTaskAsync(payment, JsonValue.Object([new("card", card)]), new TaskOptions(TaskOwnership.Task(task.Id))));
                    return (TaskState?)NextTaskState.Waiting(
                        JsonValue.Object([new("phase", "decide"), new("payments", JsonValue.Array(payments.Select(id => (JsonValue)id.ToJson())))]),
                        payments, JoinPolicy.FailFast);
                }, taskContext),
                ["decide"] = async (task, runtime, taskContext) =>
                {
                    var ids = ((JsonArrayValue)((JsonObjectValue)task.Checkpoint)["payments"]).Select(id => new TaskId((long)((JsonNumberValue)id).Value)).ToList();
                    var outcomes = await runtime.OutcomesAsync(ids, taskContext);
                    await output.WriteLineAsync($"  payments: {string.Join(", ", outcomes.Select(outcome => outcome.Status.ToString().ToLowerInvariant()))}");
                    var paid = outcomes.All(outcome => outcome.Status == TaskOutcomeStatus.Completed);
                    await runtime.CommitAsync((_, _) => paid ? NextTaskState.Completed("order placed") : NextTaskState.Failed("payment failed"), taskContext);
                },
            },
            // Runs only after every payment is done, so the refunds have already happened.
            Abort = async (_, runtime, taskContext) =>
            {
                await output.WriteLineAsync("  checkout aborted");
                await runtime.CommitAsync((_, _) => NextTaskState.Aborted(), taskContext);
            },
        });

        var registry = Registry.Create();
        registry.Install(new Extension { Name = "checkout", Tasks = [payment, checkoutTask] });
        async Task<IHarness> Open() => await Harness.OpenAsync(await open(),
            new HarnessOptions { Models = EmptyDurableModels.Instance, Registry = registry, TimeProvider = clock }, Context);

        // Prints the live tasks as a tree along their owner edges.
        async Task PrintGraph(TaskGraph graph)
        {
            var nodes = graph.Tasks.Values.ToList();
            async Task Print(TaskGraphNode node, int depth)
            {
                var state = node.State;
                var status = state.Status == DurableTaskStatus.Waiting
                    ? "waiting on " + string.Join(", ", state.On.Select(id => id.Value))
                    : state.Status.ToString().ToLowerInvariant();
                await output.WriteLineAsync($"  {new string(' ', 2 * depth)}{node.Kind} {node.Id.Value}: {status}");
                foreach (var child in nodes.Where(candidate => candidate.Owner == node.Id)) await Print(child, depth + 1);
            }
            foreach (var node in nodes.Where(candidate => candidate.Owner is null)) await Print(node, 0);
        }
        Task<TaskId> Checkout(IConversation root, params string[] cards) => root.CommitAsync(tx => tx.CreateTaskAsync(checkoutTask,
            JsonValue.Object([new("cards", JsonValue.Array(cards.Select(card => (JsonValue)card)))]), new TaskOptions(TaskOwnership.Conversation)), Context);
        static string Outcome(TaskRecord record) => ((SettlingTaskState)record.State).Outcome.Status.ToString().ToLowerInvariant();

        var harness = await Open();
        var root = await harness.RootAsync(Context);

        await output.WriteLineAsync("One card is declined:");
        var id = await Checkout(root, "visa-1", "expired-2", "visa-3", "visa-4");
        await output.WriteLineAsync($"  checkout: {Outcome(await harness.WaitForTaskAsync(id, Context))}");

        await output.WriteLineAsync("The customer cancels:");
        id = await Checkout(root, "visa-5", "visa-6", "visa-7", "visa-8");
        harness.Resume();
        await Charges(id).All.Task;
        using (var graph = await harness.TaskGraphAsync(Context)) await PrintGraph(graph.Value);
        await harness.AbortTaskAsync(id, Context);
        await output.WriteLineAsync($"  checkout: {Outcome(await harness.WaitForTaskAsync(id, Context))}");

        await output.WriteLineAsync("The process stops while the payments run, and a new one continues:");
        id = await Checkout(root, "visa-9", "visa-10", "visa-11", "visa-12");
        await Charges(id).All.Task;
        await harness.CloseAsync(Context);
        harness = await Open();
        root = await harness.RootAsync(Context);
        // The payments' moment passes; their sleeps, rerun from the saved checkpoints, end at once.
        clock.Advance(TimeSpan.FromSeconds(1));
        await output.WriteLineAsync($"  checkout: {Outcome(await harness.WaitForTaskAsync(id, Context))}");

        await harness.CloseAsync(Context);
        if (directory is not null)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
