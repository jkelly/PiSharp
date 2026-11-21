// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/core/extensions/loader.ts (createExtensionRuntime
// reportError, createExtensionAPI events.on reportingHandler), packages/coding-agent/src/core/extensions/runner.ts (the runner's
// runtime.reportError) and packages/coding-agent/test/suite/regressions/11264-pi-events-handler-errors.test.ts.
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;

/// <summary>Pi 1.6.0 sync, #11264, for the native registry's owner event bus (synchronous listeners): a failed listener of an
/// extension is an extension error with event <c>events:&lt;channel&gt;</c> (ExtensionRegistry.EventBusErrorReporter), not a console
/// line; without a reporter it is dropped, and so is one that fails after its owner closed or its runtime was replaced. Authored
/// from the pinned sources; never captured from an upstream run.</summary>
internal static class Sync160EventBusCases
{
    internal static (string Name, Func<Task> Run)[] All =>
    [
        ("sync160 owner listener failure is reported as events:<channel> and not written to the console", Reported),
        ("sync160 owner listener failure without a reporter is dropped", DroppedWithoutReporter),
        ("sync160 owner listener failure after its owner started closing is dropped", DroppedAfterOwnerClosed),
        ("sync160 owner listener failure after its registrations were withdrawn is dropped", DroppedAfterWithdrawal),
        ("sync160 reporter failure cannot stop later listeners or the emitter", ReporterFaultIsolation),
    ];

    private static void Require(bool value, string what) { if (!value) throw new InvalidOperationException("Control failed: " + what); }

    private sealed class Reports
    {
        private readonly List<(ExtensionEventDiagnostic Diagnostic, Exception Error)> items = [];
        internal void Add(ExtensionEventDiagnostic diagnostic, Exception error) { lock (items) items.Add((diagnostic, error)); }
        internal (ExtensionEventDiagnostic Diagnostic, Exception Error)[] Items { get { lock (items) return [.. items]; } }
    }

    /// <summary>Runs the control with Console.Error captured (the bus's own diagnostic is a console line) and returns what was written.</summary>
    private static async Task<string> WithConsoleError(Func<Task> run)
    {
        var previous = Console.Error; using var captured = new StringWriter();
        Console.SetError(captured);
        try { await run(); }
        finally { Console.SetError(previous); }
        return captured.ToString();
    }

    // "reports a throwing handler to the extension error listener instead of stderr".
    private static async Task Reported()
    {
        var reports = new Reports(); var fault = new InvalidOperationException("handler failed"); var later = 0; long generation = 0;
        var console = await WithConsoleError(async () =>
        {
            await using var host = new ExtensionRegistry { EventBusErrorReporter = reports.Add };
            var receiver = new Probe(); var sender = new Probe();
            await using var one = await host.ActivateAsync("one", receiver);
            await using var two = await host.ActivateAsync("two", sender);
            generation = one.OwnerGeneration;
            using var failing = receiver.Bus!.On("test:boom", _ => throw fault);
            using var following = receiver.Bus.On("test:boom", _ => later++);
            sender.Bus!.Emit("test:boom", null);
        });
        var items = reports.Items;
        Require(items.Length == 1, "one report, got " + items.Length);
        var (diagnostic, error) = items[0];
        Require(diagnostic.EventName == "events:test:boom", "event " + diagnostic.EventName);
        Require(diagnostic.OwnerId == "one" && diagnostic.OwnerGeneration == generation, "owner " + diagnostic.OwnerId);
        Require(diagnostic.Failure == ExtensionEventFailure.HandlerFailed && diagnostic.Message == "handler failed" && diagnostic.ErrorText == "handler failed", "message " + diagnostic.ErrorText);
        Require(diagnostic.RegistrationId.StartsWith("eventbus-", StringComparison.Ordinal), "registration " + diagnostic.RegistrationId);
        Require(ReferenceEquals(error, fault), "the listener's exception is passed on");
        Require(later == 1, "the later listener still ran");
        Require(console.Length == 0, "nothing on the console: " + console);
    }

    // loader.ts createExtensionRuntime: "Nothing can show an error yet" (reportError is a no-op until a runner exists).
    private static async Task DroppedWithoutReporter()
    {
        var later = 0;
        var console = await WithConsoleError(async () =>
        {
            await using var host = new ExtensionRegistry();
            var receiver = new Probe(); var sender = new Probe();
            await using var one = await host.ActivateAsync("one", receiver);
            await using var two = await host.ActivateAsync("two", sender);
            using var failing = receiver.Bus!.On("test:boom", _ => throw new InvalidOperationException("handler failed"));
            using var following = receiver.Bus.On("test:boom", _ => later++);
            sender.Bus!.Emit("test:boom", null);
        });
        Require(later == 1, "the later listener still ran");
        Require(console.Length == 0, "nothing on the console: " + console);
    }

    // "drops an error from a handler that fails after its session was disposed".
    private static async Task DroppedAfterOwnerClosed()
    {
        var reports = new Reports();
        var console = await WithConsoleError(async () =>
        {
            await using var host = new ExtensionRegistry { EventBusErrorReporter = reports.Add };
            var receiver = new Probe(); var sender = new Probe();
            var one = await host.ActivateAsync("one", receiver);
            await using var two = await host.ActivateAsync("two", sender);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
            using var listener = receiver.Bus!.On("test:late", _ =>
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("not released");
                throw new InvalidOperationException("late failure");
            });
            var emission = Task.Run(() => sender.Bus!.Emit("test:late", null));
            Task? close = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                close = one.DisposeAsync().AsTask();
                Require(!close.IsCompleted, "the close waits for the held listener");
            }
            finally { release.Set(); await emission; if (close is not null) await close; }
            Require(receiver.Disposals == 1, "the owner closed");
        });
        Require(reports.Items.Length == 0, "the late failure is dropped: " + string.Join("|", reports.Items.Select(item => item.Diagnostic.ErrorText)));
        Require(console.Length == 0, "nothing on the console: " + console);
    }

    // The native form of a replaced runtime: the previous owners withdraw their registrations (RegistrationScope.WithdrawRegistrations)
    // and new owners take their place; the previous owner's running listener must not report into the replacement.
    private static async Task DroppedAfterWithdrawal()
    {
        var reports = new Reports();
        var console = await WithConsoleError(async () =>
        {
            await using var host = new ExtensionRegistry { EventBusErrorReporter = reports.Add };
            var receiver = new Probe(); var sender = new Probe(); var replacement = new Probe();
            await using var one = await host.ActivateAsync("one", receiver);
            await using var two = await host.ActivateAsync("two", sender);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
            using var listener = receiver.Bus!.On("test:late", _ =>
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("not released");
                throw new InvalidOperationException("late failure");
            });
            var emission = Task.Run(() => sender.Bus!.Emit("test:late", null));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                one.WithdrawRegistrations(keepTools: true);
            }
            finally { release.Set(); await emission; }
            Require(reports.Items.Length == 0, "the withdrawn owner's late failure is dropped");
            await using var three = await host.ActivateAsync("three", replacement);
            using var current = replacement.Bus!.On("test:late", _ => throw new InvalidOperationException("current failure"));
            sender.Bus!.Emit("test:late", null);
        });
        var items = reports.Items;
        Require(items.Length == 1 && items[0].Diagnostic.OwnerId == "three" && items[0].Diagnostic.EventName == "events:test:late" &&
            items[0].Diagnostic.Message == "current failure", "only the replacement owner's failure is reported: " +
            string.Join("|", items.Select(item => item.Diagnostic.OwnerId + ":" + item.Diagnostic.ErrorText)));
        Require(console.Length == 0, "nothing on the console: " + console);
    }

    private static async Task ReporterFaultIsolation()
    {
        var calls = 0; var later = 0;
        var console = await WithConsoleError(async () =>
        {
            await using var host = new ExtensionRegistry { EventBusErrorReporter = (_, _) => { calls++; throw new InvalidOperationException("reporter"); } };
            var receiver = new Probe(); var sender = new Probe();
            await using var one = await host.ActivateAsync("one", receiver);
            await using var two = await host.ActivateAsync("two", sender);
            using var failing = receiver.Bus!.On("test:boom", _ => throw new InvalidOperationException("handler failed"));
            using var following = receiver.Bus.On("test:boom", _ => later++);
            sender.Bus!.Emit("test:boom", null);
            sender.Bus.Emit("test:boom", null);
        });
        Require(calls == 2 && later == 2, $"reporter calls {calls}, later listener calls {later}");
        Require(console.Length == 0, "nothing on the console: " + console);
    }
}
