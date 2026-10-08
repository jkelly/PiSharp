using System.Reflection;
using System.Text.Json;
using PiSharp.Compatibility.Node;
using PiSharp.Contracts;
using PiSharp.Extensions;

// Reflect only the internal sink under test; no extra production API or friend-assembly access is introduced.
internal static class NativeToolProgressTests
{
    internal static async Task RunAsync()
    {
        var calls = 0; var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = Sink(new Context(async (_, token) => { calls++; entered.TrySetResult(); await hold.Task.WaitAsync(token); }));
        foreach (var field in new[] { "operationId", "callbackId", "toolCallId" })
            await Reject<InvalidOperationException>(() => Deliver(sink, Payload(1, field)));
        Check(calls == 0, "foreign operation, callback and parent identity publish nothing");
        using var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = Deliver(sink, Payload(1), testDeadline.Token).AsTask();
        Exception? heldFailure = null;
        try
        {
            await entered.Task.WaitAsync(testDeadline.Token);
            Check(!pending.IsCompleted, "native publication is joined");
            await Reject<InvalidOperationException>(() => Deliver(sink, Payload(2)));
            var closeRejected = false;
            try { Close(sink); } catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { closeRejected = true; }
            Check(closeRejected, "owner lease cannot close while delivery is pending");
            hold.TrySetResult(); Check(await pending == 1 && calls == 1, "matching receipt follows actual sink completion");
        }
        catch (Exception error) { heldFailure = error; throw; }
        finally
        {
            hold.TrySetResult();
            try { await pending.ConfigureAwait(false); }
            catch (Exception cleanup) when (heldFailure is not null) { heldFailure.Data["HeldDeliveryCleanupFailure"] = cleanup; }
        }
        await Reject<InvalidOperationException>(() => Deliver(sink, Payload(1)));
        Close(sink); await Reject<InvalidOperationException>(() => Deliver(sink, Payload(2)));
        Check(calls == 1, "duplicate sequence and stale sink publish nothing");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledSink = Sink(new Context(async (_, token) => { cancellationEntered.TrySetResult(); await never.Task.WaitAsync(token); }));
        var cancelled = Deliver(cancelledSink, Payload(1), cancellation.Token).AsTask();
        Exception? cancellationFailure = null;
        try
        {
            await cancellationEntered.Task.WaitAsync(cancellation.Token); cancellation.Cancel();
            await Reject<OperationCanceledException>(async () => { await cancelled; });
            await Reject<InvalidOperationException>(() => Deliver(cancelledSink, Payload(2)));
            Close(cancelledSink);
        }
        catch (Exception error) { cancellationFailure = error; throw; }
        finally
        {
            // Release even if entry waiting or an assertion failed before explicit cancellation.
            never.TrySetResult();
            try { await cancelled.ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception cleanup) when (cancellationFailure is not null) { cancellationFailure.Data["CancellationDeliveryCleanupFailure"] = cleanup; }
        }

        var failedSink = Sink(new Context((_, _) => throw new IOException("authored delivery failure")));
        await Reject<IOException>(() => Deliver(failedSink, Payload(1)));
        await Reject<InvalidOperationException>(() => Deliver(failedSink, Payload(2)));
        var bounded = Sink(new Context((_, _) => ValueTask.CompletedTask));
        for (var i = 1; i <= 16; i++) Check(await Deliver(bounded, Payload(i)) == i, "inclusive native update count");
        await Reject<InvalidOperationException>(() => Deliver(bounded, Payload(17)));
        await Reject<IOException>(() => Deliver(Sink(new Context((_, _) => ValueTask.CompletedTask)), Payload(1, raw: "{\"text\":\"" + new string('x', 65536) + "\"}")));
        var byteBounded = Sink(new Context((_, _) => ValueTask.CompletedTask));
        var large = "{\"text\":\"" + new string('x', 60000) + "\"}";
        for (var i = 1; i <= 4; i++) await Deliver(byteBounded, Payload(i, raw: large));
        await Reject<InvalidOperationException>(() => Deliver(byteBounded, Payload(5, raw: large)));
        Console.WriteLine("PASS native progress identity, serialization, join, cancellation, failure, limits and stale controls (authored sink tests)");
    }
    private static readonly Type SinkType = typeof(NodeCommandInputExtension).Assembly.GetType("PiSharp.Compatibility.Node.NodeToolProgressDelivery", throwOnError: true)!;
    private static object Sink(Context context) => Activator.CreateInstance(SinkType, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
        binder: null, args: ["op", "callback", context], culture: null)!;
    private static ValueTask<int> Deliver(object sink, JsonElement payload, CancellationToken token = default) =>
        (ValueTask<int>)SinkType.GetMethod("DeliverAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sink, [payload, token])!;
    private static void Close(object sink) => SinkType.GetMethod("Close", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sink, null);
    private static JsonElement Payload(int sequence, string? foreign = null, string? raw = null) => JsonData.Parse(JsonSerializer.Serialize(new
    {
        operationId = foreign == "operationId" ? "foreign" : "op", callbackId = foreign == "callbackId" ? "foreign" : "callback",
        toolCallId = foreign == "toolCallId" ? "foreign-parent" : "parent", sequence,
        partialResultJson = raw ?? "{\"content\":[{\"type\":\"text\",\"text\":\"partial\"}]}"
    })).Value;
    private static async Task Reject<T>(Func<ValueTask<int>> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Context(Func<JsonData, CancellationToken, ValueTask> publish) : IExtensionToolInvocationContext
    {
        public string OwnerId => "owner";
        public long OwnerGeneration => 1;
        public string ToolCallId => "parent";
        public CancellationToken OperationCancellationToken => default;
        public CancellationToken SessionCancellationToken => default;
        public CancellationToken ExtensionLifetimeCancellationToken => default;
        public ValueTask ReportUpdateAsync(JsonData value, CancellationToken token = default) => publish(value, token);
    }
}
