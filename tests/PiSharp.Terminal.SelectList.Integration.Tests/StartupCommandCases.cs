using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;

internal static class StartupCommandCases
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [
        ("real-public-configured-command-startup-eof-and-durable-owner-release", e => PublicEof(args[1], e)),
        ("real-command-native-lifecycle-selector-configured-input-and-downstream-effect", e => NativeStartup(args[1], e)),
        ("real-command-cancel-startup-selector-with-held-original-read", e => CancelRead(args[1], e)),
        ("real-command-startup-selector-eof-without-default-or-effect", e => StartupEof(args[1], e)),
        ("real-command-initial-frame-fault-joined-cleanup-and-diagnostic", e => InitialFrame(args[1], e, held: false)),
        ("real-command-cancel-held-initial-frame-original-join", e => InitialFrame(args[1], e, held: true))
    ];
    private static async Task PublicEof(string root, ConsumerEvidence e)
    {
        var f = await StartupOwnedFiles.Create(root, plugin: false, e); var before = StartupOwnedFiles.Hash(f.Session);
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunConfiguredAsync(f.Args(), terminal, terminal, errors, f.Configuration, cancellation.Token);
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1); await f.AssertWriterOwned();
            terminal.End(); var result = await original.WaitAsync(Deadline);
            e.Observe("actual-public-configured-route-eof", new { result, error = errors.ToString(), terminal = terminal.Evidence, trace = trace.Rows() });
            Check(result == 0 && errors.ToString().Length == 0, "Real public configured terminal route did not complete ordinary EOF.");
            terminal.AssertJoined(); await f.Complete(e);
            Check(StartupOwnedFiles.Hash(f.Session) == before && !File.Exists(f.Target), "Idle startup/EOF mutated durable context or performed an effect.");
        }
        finally { await Cleanup(original, terminal, cancellation, e); }
    }
    private static async Task NativeStartup(string root, ConsumerEvidence e)
    {
        var f = await StartupOwnedFiles.Create(root, plugin: true, e); var trace = new StartupTrace();
        var read = new SelectorHold(); var terminal = new StartupControlledTerminal(trace, read);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = Observed(f, terminal, errors, trace, cancellation.Token);
        try
        {
            await read.Entered.Task.WaitAsync(Deadline);
            var request = await trace.WaitRecord(IsStartupSelect); await terminal.WaitWrite("STARTUP choose");
            await f.AssertWriterOwned();
            e.Observe("real-native-startup-selector-before-held-input-release", new { request, terminal = terminal.Evidence, trace = trace.Rows(), settings = f.Configuration.ConfigPath, effectExists = File.Exists(f.Target) });
            Check(!original.IsCompleted && !File.Exists(f.Target), "Startup selector was bypassed or an effect preceded its answer.");
            // x is configured down; ctrl+j/newline confirms. Defaults would leave the first empty choice unanswered.
            await terminal.Feed("x\n"); read.Release.TrySetResult();
            await trace.WaitRecord(record => IsResponse(record, "chat-start")); await terminal.WaitWrite("[history]");
            await terminal.Feed(f.Prompt + "\r"); await terminal.WaitWrite("STARTUP_DONE");
            await terminal.Feed("/quit\r"); var result = await original.WaitAsync(Deadline);
            e.Observe("real-native-startup-complete", new { result, error = errors.ToString(), terminal = terminal.Evidence,
                records = trace.Records(), trace = trace.Rows(), effectHash = File.Exists(f.Target) ? StartupOwnedFiles.Hash(f.Target) : null });
            Check(result == 0 && errors.ToString().Length == 0 && await File.ReadAllTextAsync(f.Target) == f.Saved, "Actual lifecycle selector did not authorize exactly the authored downstream effect.");
            Check(trace.Records().Count(IsStartupSelect) == 1 && trace.Records().Any(record => record.ToString().Contains("startup:startup-approved", StringComparison.Ordinal)), "The real startup tool result or single UI request is missing.");
            terminal.AssertJoined(); await f.Complete(e);
            // The held physical read proves attached input can paint startup UI. Direct barrier cases separately
            // prove get_state SEND-admission gating; this trace does not claim response-before-read ordering.
        }
        finally { await Cleanup(original, terminal, cancellation, e); }
    }
    private static async Task CancelRead(string root, ConsumerEvidence e)
    {
        var f = await StartupOwnedFiles.Create(root, plugin: true, e); var trace = new StartupTrace();
        var read = new SelectorHold(); var terminal = new StartupControlledTerminal(trace, read);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = Observed(f, terminal, errors, trace, cancellation.Token);
        try
        {
            await read.Entered.Task.WaitAsync(Deadline); await trace.WaitRecord(IsStartupSelect); await terminal.WaitWrite("STARTUP choose");
            await f.AssertWriterOwned(); cancellation.Cancel(); await read.Canceled.Task.WaitAsync(Deadline);
            e.Observe("real-command-canceled-original-read-still-held", new { commandComplete = original.IsCompleted, terminal = terminal.Evidence, diagnostics = errors.ToString(), trace = trace.Rows() });
            Check(!original.IsCompleted && terminal.Snapshot.ActiveReads == 1 && terminal.Snapshot.ReadWorkersSettled == 0 && terminal.Disposals == 0,
                "Real command canceled by detaching its original physical reader.");
            Check(errors.ToString().Length == 0, "Final command diagnostics escaped before original I/O joined.");
            read.Release.TrySetResult(); var result = await original.WaitAsync(Deadline);
            e.Observe("real-command-canceled-read-after-release", new { result, error = errors.ToString(), terminal = terminal.Evidence, trace = trace.Rows() });
            Check(result == 1 && !File.Exists(f.Target), "Canceled startup selected a default or falsely succeeded.");
            terminal.AssertJoined(); await f.Complete(e);
        }
        finally { await Cleanup(original, terminal, cancellation, e); }
    }
    private static async Task StartupEof(string root, ConsumerEvidence e)
    {
        await StartupEofResponses(e);
        var f = await StartupOwnedFiles.Create(root, plugin: true, e); var trace = new StartupTrace();
        var before = StartupOwnedFiles.Hash(f.Session);
        var terminal = new StartupControlledTerminal(trace);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunObservedConfiguredAsync(f.Args(), terminal, terminal, errors,
            trace.Observe, f.Configuration, cancellation.Token,
            shutdownObserver: settlement => e.Observe("startup-eof-original-host-causes-before-terminal-join",
                StartupCauseDiagnostics.Capture(settlement.Failures)));
        try
        {
            await trace.WaitRecord(IsStartupSelect); await terminal.WaitWrite("STARTUP choose");
            await f.AssertWriterOwned(); terminal.End();
            var result = await original.WaitAsync(Deadline);
            e.Observe("real-native-startup-eof", new { result, error = errors.ToString(), terminal = terminal.Evidence, trace = trace.Rows() });
            Check(result == 0 && !File.Exists(f.Target) && !trace.Records().Any(record => record.ToString().Contains("startup:startup-approved", StringComparison.Ordinal)),
                "Startup EOF failed the host route, supplied a default approval or executed an effect.");
            terminal.AssertJoined(); await f.Complete(e);
            Check(errors.ToString().Length == 0 && StartupOwnedFiles.Hash(f.Session) == before,
                "Startup EOF emitted a failure diagnostic or changed the durable session.");
        }
        finally { await Cleanup(original, terminal, cancellation, e); }
    }
    private static async Task StartupEofResponses(ConsumerEvidence e)
    {
        // Reproduce the exact canceled-before-acceptance packet from the dispatcher.
        // Only the existing input owner's ordinary EOF marker makes it an inert late response.
        const string canceled = "{\"type\":\"response\",\"id\":\"chat-start\",\"command\":\"get_state\",\"success\":false,\"error\":\"Command canceled before acceptance.\"}";
        const string valid = "{\"type\":\"response\",\"id\":\"chat-start\",\"command\":\"get_state\",\"success\":true,\"data\":{\"isStreaming\":false}}";
        foreach (var (ended, packet, rejects) in new[]
        {
            (false, canceled, true), (true, canceled, false),
            (true, canceled.Replace("Command canceled before acceptance.", "RPC command failed.", StringComparison.Ordinal), true),
            (true, canceled.Replace("get_state", "get_messages", StringComparison.Ordinal), true),
            (true, valid.Replace("{\"isStreaming\":false}", "{}", StringComparison.Ordinal), true),
            (true, valid, false), (false, valid, false)
        })
        {
            using var output = new StringWriter();
            using var frontend = new InteractiveSessionFrontend(output, nativePresentation: true);
            if (ended) frontend.EndStartupInput();
            Exception? failure = null;
            try { await frontend.ObserveAsync(JsonData.Parse(packet), default); }
            catch (Exception error) { failure = error; }
            Check((failure is not null) == rejects, "Startup EOF response classification hid a failure or rejected owned EOF.");
            if (!rejects && ended)
                Check(!frontend.Ready.IsCompleted && output.ToString().Length == 0,
                    "Late startup response reopened readiness or repainted after ordinary EOF.");
            if (!rejects && !ended)
                Check(frontend.Ready.IsCompletedSuccessfully && output.ToString().Contains("[state] idle", StringComparison.Ordinal),
                    "Live valid startup no longer completes its expected output.");
            e.Observe("startup-eof-response-control", new { ended, packet, rejects,
                failureType = failure?.GetType().FullName, failureMessage = failure?.Message, ready = frontend.Ready.Status,
                output = output.ToString() });
        }
        var held = new HeldStartupPresentation();
        using var pending = new InteractiveSessionFrontend(held, nativePresentation: true);
        var original = pending.ObserveAsync(JsonData.Parse(valid), default).AsTask();
        try
        {
            await held.Entered.Task.WaitAsync(Deadline); pending.EndStartupInput();
            Check(!original.IsCompleted && !pending.Ready.IsCompleted, "EOF detached the original startup presentation.");
        }
        finally { held.Release.TrySetResult(); await original; }
        Check(!pending.Ready.IsCompleted, "Startup presentation completing after EOF reopened readiness.");
        e.Observe("startup-eof-held-response-original-joined", new { original.Status, ready = pending.Ready.Status });
    }
    private sealed class HeldStartupPresentation : IInteractiveSessionPresentation
    {
        internal readonly TaskCompletionSource Entered = SelectorFixture.Signal(), Release = SelectorFixture.Signal();
        public async ValueTask PresentAsync(string displayText, CancellationToken token)
        { Entered.TrySetResult(); await Release.Task; token.ThrowIfCancellationRequested(); }
    }
    private static async Task InitialFrame(string root, ConsumerEvidence e, bool held)
    {
        var f = await StartupOwnedFiles.Create(root, plugin: false, e); var before = StartupOwnedFiles.Hash(f.Session);
        var trace = new StartupTrace(); var paint = held ? new SelectorHold() : null;
        var terminal = new StartupControlledTerminal(trace, firstFrameHold: paint, failFirstFrame: !held);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = Observed(f, terminal, errors, trace, cancellation.Token);
        try
        {
            if (held)
            {
                await paint!.Entered.Task.WaitAsync(Deadline); cancellation.Cancel(); await paint.Canceled.Task.WaitAsync(Deadline);
                e.Observe("real-command-canceled-first-frame-before-release", new { commandComplete = original.IsCompleted, terminal = terminal.Evidence, diagnostics = errors.ToString(), trace = trace.Rows() });
                Check(!original.IsCompleted && terminal.Snapshot.ActiveWrites == 1 && terminal.Disposals == 0 && errors.ToString().Length == 0,
                    "Cancellation bypassed original startup paint or emitted final diagnostics before joining it.");
                paint.Release.TrySetResult();
            }
            var result = await original.WaitAsync(Deadline);
            e.Observe("real-command-first-frame-cleanup", new { held, result, error = errors.ToString(), terminal = terminal.Evidence, trace = trace.Rows() });
            Check(result == 1 && errors.ToString().Length > 0 && !errors.ToString().Contains('\u001b'), "Initial frame failure/cancellation omitted its bounded inert diagnostic or falsely succeeded.");
            terminal.AssertJoined(); await f.Complete(e);
            Check(StartupOwnedFiles.Hash(f.Session) == before && !File.Exists(f.Target), "Initial-frame shutdown mutated context or performed a tool effect.");
        }
        finally { await Cleanup(original, terminal, cancellation, e); }
    }
    private static Task<int> Observed(StartupOwnedFiles files, StartupControlledTerminal terminal, TextWriter errors, StartupTrace trace, CancellationToken token) =>
        TerminalSessionCommand.RunObservedConfiguredAsync(files.Args(), terminal, terminal, errors, trace.Observe, files.Configuration, token);
    private static async Task Cleanup(Task<int> original, StartupControlledTerminal terminal, CancellationTokenSource cancellation, ConsumerEvidence e)
    {
        cancellation.Cancel(); terminal.End(); terminal.Release();
        // Await the actual original command, including on an assertion/deadline failure. No detached continuation
        // replaces ownership evidence and no child process or another worker's operation is terminated.
        await original;
        e.Observe("original-command-finally-joined", new { original.Status, terminal = terminal.Evidence }); terminal.AssertJoined();
    }
    private static bool IsStartupSelect(JsonData record) => record.Value.GetProperty("type").GetString() == "extension_ui_request" &&
        record.Value.TryGetProperty("method", out var method) && method.GetString() == "select" &&
        record.Value.TryGetProperty("title", out var title) && title.GetString() == "STARTUP choose 文";
    private static bool IsResponse(JsonData record, string id) => record.Value.GetProperty("type").GetString() == "response" &&
        record.Value.TryGetProperty("id", out var identity) && identity.GetString() == id && record.Value.GetProperty("success").GetBoolean();
    private static void Check(bool value, string message) => StartupBarrierCases.Check(value, message);
}
