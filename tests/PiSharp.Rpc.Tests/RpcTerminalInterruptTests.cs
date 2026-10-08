using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;
using Fixture = RpcSessionCreationTests.Fixture;

// Authored only. All original operations and cleanup are joined without timeout wrappers.
internal static class RpcTerminalInterruptTests
{
    internal const string Prefix = "rpc.terminal-interrupt.";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "restore-abort-held-output-and-provider-cleanup-join", Held),
        (Prefix + "oversized-queue-preserved-but-run-still-aborted", Capacity),
        (Prefix + "stale-generation-cannot-abort-replacement", Stale),
        (Prefix + "queued-interrupt-loses-authority-during-switch", QueuedSwitch)
    ];

    private static async Task Held()
    {
        var transport = new HeldTransport(); using var output = new HeldClear();
        await using var f = await Fixture.Create([], output: output, transport: transport);
        using var cancellation = new CancellationTokenSource(); Task? interrupt = null;
        try
        {
            await f.Send(new { id = "start", type = "prompt", message = "start" }); await transport.Entered.Task.WaitAsync(Deadline);
            await f.Send(new { id = "q", type = "steer", message = "queued" });
            output.Armed = true;
            interrupt = f.Dispatcher.SubmitAsync(Request("stop", 1), cancellation.Token);
            await transport.Canceled.Task.WaitAsync(Deadline); await output.Entered.Task.WaitAsync(Deadline);
            cancellation.Cancel();
            Check(!interrupt.IsCompleted && !transport.Exited.Task.IsCompleted, "Cancellation detached held output/provider work.");
            Check(f.Owner.Current.Session.GetPendingInputQueueSnapshot().SteeringMessages.IsEmpty, "Exact queue was not removed.");
            output.Release.TrySetResult(); await output.Response.Task.WaitAsync(Deadline);
            Check(!interrupt.IsCompleted, "Interrupt returned before original provider cleanup joined.");
        }
        finally
        {
            output.Release.TrySetResult(); transport.Release.TrySetResult();
            if (interrupt is not null) await interrupt;
        }
        var data = f.Response("stop").Value.GetProperty("data");
        Check(data.GetProperty("text").GetString() == "queued\n\ndraft" && data.GetProperty("count").GetInt32() == 1,
            "Interrupted queue receipt lost text or order.");
        Check(transport.Exited.Task.IsCompleted && !f.Owner.Current.Session.Snapshot.Agent.IsRunning, "Abort did not join the captured run.");
        await f.Dispatcher.SubmitAsync(Request("again", 1));
        Check(f.Response("again").Value.GetProperty("data").GetProperty("count").GetInt32() == 0, "Repeated interrupt restored duplicates.");
    }

    private static async Task Capacity()
    {
        var transport = new HeldTransport(); await using var f = await Fixture.Create([], transport: transport);
        Task? interrupt = null;
        var queued = new[] { 'x', 'y' }.Select(value => new TranscriptEntry("user", JsonData.Parse(JsonSerializer.Serialize(new
            { role = "user", content = new string(value, 32_768), timestamp = 0 })))).ToArray();
        Check(queued.All(message => message.WireBody.Value.GetRawText().Length <= 65_536), "Fixture messages exceed individual queue admission.");
        try
        {
            await f.Send(new { id = "start", type = "prompt", message = "start" }); await transport.Entered.Task.WaitAsync(Deadline);
            foreach (var message in queued) f.Owner.Current.Session.Steer(message);
            Check(f.Owner.Current.Session.GetPendingInputQueueSnapshot().SteeringMessages.SequenceEqual(queued),
                "Both individually admissible messages were not queued before restore.");
            interrupt = f.Dispatcher.SubmitAsync(Request("large", 1)); await transport.Canceled.Task.WaitAsync(Deadline);
            Check(!interrupt.IsCompleted, "Rejected restore detached abort cleanup.");
        }
        finally { transport.Release.TrySetResult(); if (interrupt is not null) await interrupt; }
        Check(!f.Response("large").Value.GetProperty("success").GetBoolean(), "Oversized restore succeeded.");
        Check(f.Owner.Current.Session.GetPendingInputQueueSnapshot().SteeringMessages.SequenceEqual(queued),
            "Abort silently discarded or reordered unrestorable queue text.");
        Check(transport.Exited.Task.IsCompleted && !f.Owner.Current.Session.Snapshot.Agent.IsRunning,
            "Rejected restore did not abort and join the captured run.");
    }

    private static async Task Stale()
    {
        var transport = new HeldTransport(); await using var f = await Fixture.Create([], transport: transport);
        Task? interrupt = null;
        try
        {
            await f.Send(new { id = "switch", type = "new_session" });
            await f.Send(new { id = "start", type = "prompt", message = "replacement" }); await transport.Entered.Task.WaitAsync(Deadline);
            await f.Dispatcher.SubmitAsync(Request("stale", 1));
            Check(!f.Response("stale").Value.GetProperty("success").GetBoolean() && !transport.Canceled.Task.IsCompleted &&
                f.Owner.Current.Session.Snapshot.Agent.IsRunning, "Stale interrupt canceled the new attachment.");
            interrupt = f.Dispatcher.SubmitAsync(Request("current", 2)); await transport.Canceled.Task.WaitAsync(Deadline);
        }
        finally { transport.Release.TrySetResult(); if (interrupt is not null) await interrupt; }
        Check(f.Response("current").Value.GetProperty("success").GetBoolean(), "Current attachment interrupt failed.");
    }

    private static async Task QueuedSwitch()
    {
        await using var f = await Fixture.Create([]);
        var publication = (SemaphoreSlim)typeof(RpcSessionDispatcher).GetField("_queuePublication",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Dispatcher)!;
        await publication.WaitAsync(); var entered = Gate(); var original = f.Owner.AttachmentChanged!;
        f.Owner.AttachmentChanged = async change =>
        {
            change.Current.Session.Steer(new("user", JsonData.Parse("""{"role":"user","content":"replacement","timestamp":0}""")));
            var publishing = original(change).AsTask(); entered.TrySetResult(); await publishing;
        };
        Task? interrupt = null, switching = null;
        try
        {
            interrupt = f.Dispatcher.SubmitAsync(Request("stale", 1));
            switching = f.Send(new { id = "switch", type = "new_session" });
            await entered.Task.WaitAsync(Deadline);
            Check(!interrupt.IsCompleted && !switching.IsCompleted, "Held publication did not retain original owners.");
        }
        finally { publication.Release(); await Task.WhenAll(new[] { interrupt, switching }.OfType<Task>()); }
        Check(!f.Response("stale").Value.GetProperty("success").GetBoolean() && f.Owner.Current.Generation == 2 &&
            f.Owner.Current.Session.GetPendingInputQueueSnapshot().SteeringMessages.Length == 1,
            "Queued interrupt crossed replacement authority or consumed its queue.");
    }

    private static JsonData Request(string id, long generation) => JsonData.Parse(JsonSerializer.Serialize(new
        { id, type = "pisharp_interrupt", generation, currentText = "draft" }));
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class HeldTransport : IChatTransport
    {
        internal readonly TaskCompletionSource Entered = Gate(), Canceled = Gate(), Release = Gate(), Exited = Gate();
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        {
            using var registration = token.Register(() => Canceled.TrySetResult()); Entered.TrySetResult();
            try { await Release.Task; token.ThrowIfCancellationRequested(); yield break; }
            finally { Exited.TrySetResult(); }
        }
    }
    private sealed class HeldClear : MemoryStream
    {
        internal bool Armed;
        internal readonly TaskCompletionSource Entered = Gate(), Release = Gate(), Response = Gate();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
        {
            var body = JsonData.Parse(Encoding.UTF8.GetString(bytes.Span)).Value;
            if (Armed && body.GetProperty("type").GetString() == "queue_update")
            { Entered.TrySetResult(); await Release.Task; }
            await base.WriteAsync(bytes, token);
            if (body.TryGetProperty("id", out var id) && id.GetString() == "stop") Response.TrySetResult();
        }
    }
}
