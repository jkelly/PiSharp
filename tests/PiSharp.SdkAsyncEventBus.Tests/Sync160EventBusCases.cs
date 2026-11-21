// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/core/extensions/loader.ts (createExtensionRuntime
// reportError, createExtensionAPI events.on reportingHandler), packages/coding-agent/src/core/extensions/runner.ts (the runner's
// runtime.reportError) and packages/coding-agent/test/suite/regressions/11264-pi-events-handler-errors.test.ts.
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

/// <summary>Pi 1.6.0 sync, #11264, for the native registry's owner event bus (asynchronous listeners): a listener whose task fails
/// is an extension error with event <c>events:&lt;channel&gt;</c> (ExtensionRegistry.EventBusErrorReporter) once it settles, not a
/// console line; one that fails after its owner started closing is dropped. Authored from the pinned sources; never captured from an
/// upstream run. Listener originals are registered with the suite's callback evidence (ObservedOnAsync), as the other controls do.</summary>
internal static class Sync160EventBusCases
{
    internal static (string Name, Func<Task> Run)[] All =>
    [
        ("sync160 async owner listener failure is reported as events:<channel> after settlement", ReportedAfterSettlement),
        ("sync160 async owner listener failure after its owner started closing is dropped", DroppedAfterOwnerClosed),
        ("sync160 synchronous throw and multiple faults of an async owner listener report the caught exception", CaughtExceptionReported),
    ];

    private static void Require(bool value, string what) { if (!value) throw new InvalidOperationException("Control failed: " + what); }

    private static TaskCompletionSource Pending(string name)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CallbackOriginals.All.Add((name, source.Task)); return source;
    }

    private sealed class Reports
    {
        private readonly List<(ExtensionEventDiagnostic Diagnostic, Exception Error)> items = [];
        internal void Add(ExtensionEventDiagnostic diagnostic, Exception error) { lock (items) items.Add((diagnostic, error)); }
        internal (ExtensionEventDiagnostic Diagnostic, Exception Error)[] Items { get { lock (items) return [.. items]; } }
    }

    private static async Task<string> WithConsoleError(Func<Task> run)
    {
        var previous = Console.Error; using var captured = new StringWriter();
        Console.SetError(captured);
        try { await run(); }
        finally { Console.SetError(previous); }
        return captured.ToString();
    }

    // "reports a throwing handler to the extension error listener instead of stderr", for a handler that fails after an await.
    private static async Task ReportedAfterSettlement()
    {
        var reports = new Reports(); var later = 0;
        var console = await WithConsoleError(async () =>
        {
            await using var host = new ExtensionRegistry { EventBusErrorReporter = reports.Add };
            var receiver = new Probe(); var sender = new Probe();
            await using var one = await host.ActivateAsync("one", receiver);
            await using var two = await host.ActivateAsync("two", sender);
            var held = Pending("sync160-reported-release");
            using var failing = receiver.Bus.ObservedOnAsync("test:late", async _ => { await held.Task; throw new InvalidOperationException("late failure"); });
            using var following = receiver.Bus.On("test:late", _ => later++);
            try
            {
                sender.Bus.Emit("test:late", null);
                Require(later == 1 && reports.Items.Length == 0, "the emitter does not wait for the pending listener");
            }
            finally { held.TrySetResult(); await held.Task; await sender.Bus.DrainAsync(); }
        });
        var items = reports.Items;
        Require(items.Length == 1 && items[0].Diagnostic is { EventName: "events:test:late", OwnerId: "one", Message: "late failure", Failure: ExtensionEventFailure.HandlerFailed } &&
            items[0].Error is InvalidOperationException { Message: "late failure" },
            "one report: " + string.Join("|", items.Select(item => item.Diagnostic.EventName + ":" + item.Diagnostic.ErrorText)));
        Require(console.Length == 0, "nothing on the console: " + console);
    }

    // "drops an error from a handler that fails after its session was disposed": emit, dispose, release, settle; no error.
    private static async Task DroppedAfterOwnerClosed()
    {
        var reports = new Reports();
        var console = await WithConsoleError(async () =>
        {
            await using var host = new ExtensionRegistry { EventBusErrorReporter = reports.Add };
            var receiver = new Probe(); var sender = new Probe();
            var one = await host.ActivateAsync("one", receiver);
            await using var two = await host.ActivateAsync("two", sender);
            var held = Pending("sync160-dropped-release");
            using var failing = receiver.Bus.ObservedOnAsync("test:late", async _ => { await held.Task; throw new InvalidOperationException("late failure"); });
            sender.Bus.Emit("test:late", null);
            var close = one.DisposeAsync().AsTask();
            try { Require(!close.IsCompleted && receiver.Disposals == 0, "the close waits for the held listener"); }
            finally { held.TrySetResult(); await held.Task; await close; await sender.Bus.DrainAsync(); }
            Require(receiver.Disposals == 1, "the owner closed");
        });
        Require(reports.Items.Length == 0, "the late failure is dropped: " + string.Join("|", reports.Items.Select(item => item.Diagnostic.ErrorText)));
        Require(console.Length == 0, "nothing on the console: " + console);
    }

    // loader.ts reportingHandler: `catch (error)` of `await handler(data)`: the thrown value, or the first fault of the listener's task.
    private static async Task CaughtExceptionReported()
    {
        var reports = new Reports(); var thrown = new InvalidOperationException("thrown"); var first = new InvalidOperationException("first"); var second = new ArgumentException("second");
        var console = await WithConsoleError(async () =>
        {
            await using var host = new ExtensionRegistry { EventBusErrorReporter = reports.Add };
            var receiver = new Probe(); var sender = new Probe();
            await using var one = await host.ActivateAsync("one", receiver);
            await using var two = await host.ActivateAsync("two", sender);
            var original = Pending("sync160-multi-fault-original");
            using var synchronous = receiver.Bus.ObservedOnAsync("test:throw", _ => throw thrown);
            using var faulted = receiver.Bus.ObservedOnAsync("test:faults", _ => original.Task);
            try { sender.Bus.Emit("test:throw", null); sender.Bus.Emit("test:faults", null); }
            finally { original.TrySetException([first, second]); await sender.Bus.DrainAsync(); }
        });
        var items = reports.Items;
        Require(items.Length == 2, "two reports, got " + items.Length);
        Require(items[0].Diagnostic.EventName == "events:test:throw" && items[0].Diagnostic.Message == "thrown" && ReferenceEquals(items[0].Error, thrown), "synchronous throw: " + items[0].Diagnostic.ErrorText);
        Require(items[1].Diagnostic.EventName == "events:test:faults" && items[1].Diagnostic.Message == "first" && ReferenceEquals(items[1].Error, first), "first fault: " + items[1].Diagnostic.ErrorText);
        Require(console.Length == 0, "nothing on the console: " + console);
    }
}
