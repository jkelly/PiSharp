using System.Text.Json;
using System.Collections.Immutable;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Rpc.Ui;
using PiSharp.Tui;
using PiSharp.Tui.Input;
using PiSharp.Cli.Extensions;

namespace PiSharp.Cli.Commands;

internal sealed record TerminalSessionFailureObservation(Exception? Failure, int ExitCode, bool UserShutdown,
    CancellationToken CallerToken, CancellationToken InputToken, CancellationToken HostToken, CancellationToken ResizeToken);

/// <summary>Explicit Windows terminal preview over the existing durable RPC composition.</summary>
public static class TerminalSessionCommand
{
    internal sealed record StartupArguments(string[] RpcArgs, string Workspace);
    internal static async Task<(StartupArguments? Arguments, int Result)> ValidateStartupAsync(string[] args, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args); ArgumentNullException.ThrowIfNull(error);
        if (args is not ["session", "terminal", ..] || (args.Count(value => value == "--terminal-preview") is not (0 or 1) ||
            args.Count(value => value == "--terminal-preview") == 0 && args.Count(value => value == "--live") != 1))
        {
            await Failure("InvalidArguments", "Terminal requires session terminal with --terminal-preview or explicit --live.", error, startupRejection: true).ConfigureAwait(false);
            return (null, 2);
        }
        var rpcArgs = args.Where(value => value != "--terminal-preview").ToArray(); rpcArgs[1] = "rpc";
        try { return (new(rpcArgs, RpcSessionCommand.ResolveStartupWorkspace(rpcArgs)), 0); }
        catch (SessionCommandException exception)
        {
            await Failure(exception.Failure.ToString(), exception.Message, error, startupRejection: true).ConfigureAwait(false);
            return (null, exception.Failure == SessionCommandFailure.CommandFailed ? 1 : 2);
        }
        catch (LiveSessionException exception)
        {
            await Failure(exception.Code, exception.Message, error, startupRejection: true).ConfigureAwait(false);
            return (null, 2);
        }
        catch (NativeExtensionException exception)
        {
            await Failure(exception.Failure.ToString(), exception.Message, error, startupRejection: true).ConfigureAwait(false);
            return (null, exception.Failure == NativeExtensionFailure.CleanupFailed ? 1 : 2);
        }
    }
    public const string LiveUsage = "session terminal --live --provider openai|openrouter|anthropic --model <pinned model id> --session <absolute JSONL> --workspace <existing absolute directory> [--session-mode open|new-lazy|new-memory] [--max-output-tokens 1..8192] [existing exact tool grants]";
    public const string Usage = "session terminal --terminal-preview --session <existing absolute JSONL> --workspace <existing absolute directory> --offline-script <absolute JSON> [existing rpc options]; Windows terminal editor displays Unicode with inert controls; " + LiveUsage;
    public static Task<int> RunAsync(string[] args, IConsoleTerminal terminal, ITerminalViewportSource viewport,
        TextWriter error, CancellationToken token = default) =>
        RunOwnedAsync(args, terminal, viewport, error, null, null, token, null, kittyProtocolActive: false);

    internal static Task<int> RunWithLiveRuntimeAsync(string[] args, IConsoleTerminal terminal,
        ITerminalViewportSource viewport, TextWriter error, LiveSessionRuntime runtime, CancellationToken token = default) =>
        RunOwnedAsync(args, terminal, viewport, error, null, null, token, null, kittyProtocolActive: false, liveRuntime: runtime);
    internal static Task<int> RunWithTerminalRestoreAsync(string[] args, IConsoleTerminal terminal,
        ITerminalViewportSource viewport, TextWriter error, Func<ValueTask> restoreTerminalAndJoin,
        CancellationToken token = default, TerminalKeybindingConfiguration? keybindingConfiguration = null, LiveSessionRuntime? liveRuntime = null) =>
        RunOwnedAsync(args, terminal, viewport, error, null, null, token, keybindingConfiguration, kittyProtocolActive: false,
            restoreTerminalAndJoin: restoreTerminalAndJoin, liveRuntime: liveRuntime);

    public static Task<int> RunConfiguredAsync(string[] args, IConsoleTerminal terminal, ITerminalViewportSource viewport,
        TextWriter error, TerminalKeybindingConfiguration keybindingConfiguration, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(keybindingConfiguration);
        return RunOwnedAsync(args, terminal, viewport, error, null, null, token, keybindingConfiguration, kittyProtocolActive: false);
    }

    internal static Task<int> RunObservedAsync(string[] args, IConsoleTerminal terminal, ITerminalViewportSource viewport,
        TextWriter error, Func<JsonData, CancellationToken, ValueTask> observer, CancellationToken token = default,
        IRpcExtensionUiPresentationObserver? presentationObserver = null, Action<TerminalInputEvent>? observeInputCompletion = null,
        Action<TerminalSessionFailureObservation>? observeFailure = null,
        Action<RpcSessionShutdownSettlement>? shutdownObserver = null, LiveSessionRuntime? liveRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return RunOwnedAsync(args, terminal, viewport, error, observer, presentationObserver, token, null, kittyProtocolActive: false,
            observeInputCompletion: observeInputCompletion, observeFailure: observeFailure, shutdownObserver: shutdownObserver, liveRuntime: liveRuntime);
    }

    internal static Task<int> RunObservedConfiguredAsync(string[] args, IConsoleTerminal terminal, ITerminalViewportSource viewport,
        TextWriter error, Func<JsonData, CancellationToken, ValueTask> observer, TerminalKeybindingConfiguration keybindingConfiguration,
        CancellationToken token = default, bool kittyProtocolActive = false, TimeProvider? shutdownTimeProvider = null,
        Func<ValueTask>? restoreTerminalAndJoin = null, Action<RpcSessionShutdownSettlement>? shutdownObserver = null, LiveSessionRuntime? liveRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(observer); ArgumentNullException.ThrowIfNull(keybindingConfiguration);
        return RunOwnedAsync(args, terminal, viewport, error, observer, null, token, keybindingConfiguration, kittyProtocolActive,
            shutdownTimeProvider, restoreTerminalAndJoin, shutdownObserver, liveRuntime: liveRuntime);
    }

    internal static Task<int> RunWithTerminalInputAdmissionAsync(string[] args, IConsoleTerminal terminal,
        ITerminalViewportSource viewport, TextWriter error, TerminalExtensionInputAdmission terminalInputAdmission,
        CancellationToken token = default) =>
        RunOwnedAsync(args, terminal, viewport, error, null, null, token, null, kittyProtocolActive: false,
            terminalInputAdmission: terminalInputAdmission);

    private static async Task<int> RunOwnedAsync(string[] args, IConsoleTerminal terminal, ITerminalViewportSource viewport,
        TextWriter error, Func<JsonData, CancellationToken, ValueTask>? observer,
        IRpcExtensionUiPresentationObserver? presentationObserver, CancellationToken token,
        TerminalKeybindingConfiguration? keybindingConfiguration, bool kittyProtocolActive, TimeProvider? shutdownTimeProvider = null,
        Func<ValueTask>? restoreTerminalAndJoin = null, Action<RpcSessionShutdownSettlement>? shutdownObserver = null,
        Action<TerminalInputEvent>? observeInputCompletion = null, Action<TerminalSessionFailureObservation>? observeFailure = null,
        LiveSessionRuntime? liveRuntime = null, TerminalExtensionInputAdmission? terminalInputAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(args); ArgumentNullException.ThrowIfNull(terminal);
        ArgumentNullException.ThrowIfNull(viewport); ArgumentNullException.ThrowIfNull(error);
        var startupValidation = await ValidateStartupAsync(args, error).ConfigureAwait(false);
        if (startupValidation.Arguments is not { } parsedStartup) return startupValidation.Result;
        var rpcArgs = parsedStartup.RpcArgs;
        // The real Windows console profile does not negotiate Kitty. Only observed composition may explicitly inject it.
        TerminalKeybindingConfiguration ownerConfiguration;
        try
        {
            ownerConfiguration = keybindingConfiguration ??
                TerminalStartupConfigurationLoader.LoadDefault(parsedStartup.Workspace);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        { await Failure("TerminalConfigurationFailed", "Terminal startup settings paths could not be resolved.", error).ConfigureAwait(false); return 1; }
        var keybindings = ownerConfiguration.CreateBindings(kittyProtocolActive);
        using var hostCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var inputCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var resizeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var focusOwner = new TerminalEditorFocusOwner(initiallyFocused: true);
        // OSC 7501 (Pi 1.1.0): without DA1 negotiation on this console profile, PI_PROGRAM_STATUS=1 alone enables reports.
        var programStatusOverride = Environment.GetEnvironmentVariable("PI_PROGRAM_STATUS");
        var view = new TerminalSessionView(terminal, viewport, observeSourceFrame: null, focusOwner: focusOwner, keybindings: keybindings)
        { ProgramStatus = programStatusOverride == "1" ? new() : null, ProgramStatusOverride = programStatusOverride };
        var programStatus = view.ProgramStatus is null ? null : new ProgramStatusReporter();
        var receipts = new TerminalSubmissionReceipts();
        using var frontend = new InteractiveSessionFrontend(view, nativePresentation: true, submissionReceipts: receipts);
        var selectList = new TerminalSelectListDialogController(frontend, view, keybindings);
        var selectRouter = new TerminalSelectListInputRouter(frontend, selectList, focusOwner); frontend.BindSelectList(selectRouter);
        var beginInput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new BoundedRpcConnection(ObserveRecord); frontend.Bind(connection.SendAsync);
        terminalInputAdmission ??= new TerminalExtensionInputAdmission(1, frontend.CaptureSessionGeneration, hostCancellation.Token);
        Task<int>? host = null; Task<TerminalInputExit>? reading = null; Task? resizing = null;
        Exception? failure = null; var result = 1;
        var userShutdown = 0;
        var failureGate = new object(); var failures = new List<Exception>();
        var stopGate = new object(); Task<ImmutableArray<Exception>>? terminalStop = null;
        RpcSessionShutdownSettlement? shutdownSettlement = null;
        try
        {
            await view.StartAsync(token).ConfigureAwait(false);
            if (programStatus is not null) await view.ReportProgramStatusAsync(programStatus.Report, token).ConfigureAwait(false);
            // Attach the existing input/focus owner before startup extensions can publish UI.
            reading = ReadTerminalAsync();
            var presentation = presentationObserver is null ? (IRpcExtensionUiPresentationObserver)frontend :
                new ObservedPresentation(frontend, presentationObserver);
            if (programStatus is not null) presentation = new ProgramStatusPresentation(presentation, programStatus, view);
            host = RunHostAsync(presentation);
            await frontend.StartAsync(inputCancellation.Token).ConfigureAwait(false);
            beginInput.TrySetResult();
            var startup = await Task.WhenAny(host, frontend.Ready, reading).ConfigureAwait(false);
            if (startup == reading) { await reading.ConfigureAwait(false); result = await host.ConfigureAwait(false); }
            else if (startup == host) result = await host.ConfigureAwait(false);
            else
            {
                await frontend.Ready.ConfigureAwait(false); await frontend.HistoryAsync(inputCancellation.Token).ConfigureAwait(false);
                resizing = Resize(view, resizeCancellation.Token);
                var completed = await Task.WhenAny(host, reading, resizing).ConfigureAwait(false);
                if (completed == reading) { await reading.ConfigureAwait(false); connection.CompleteInput(); }
                else if (completed == resizing && !await JoinResizeStopAsync(resizing, resizeCancellation.Token).ConfigureAwait(false))
                    throw new IOException("Terminal resize observation stopped unexpectedly.");
                result = await host.ConfigureAwait(false);
            }
        }
        catch (Exception errorValue) { RecordFailure(errorValue); }
        finally
        {
            if (failure is not null) Cancel(hostCancellation);
            // This is the same original stop task the host acknowledgment awaits.
            // Never await the host from inside it: the host waits for terminal restoration.
            await StopTerminalAndJoin().ConfigureAwait(false);
            if (host is not null)
                try { result = await host.ConfigureAwait(false); } catch (Exception errorValue) { RecordFailure(errorValue); }
        }
        // No renderer/alternate-screen write remains when the borrowed diagnostic sink is used.
        if (token.IsCancellationRequested)
            failure ??= new OperationCanceledException(token);
        // Observed tests receive the chosen outcome after original owners and terminal leave have joined.
        observeFailure?.Invoke(new(failure, failure is null ? result : 1, Volatile.Read(ref userShutdown) != 0,
            token, inputCancellation.Token, hostCancellation.Token, resizeCancellation.Token));
        if (view.DiagnosticText.Length != 0)
        { await error.WriteAsync(view.DiagnosticText.AsMemory(), CancellationToken.None).ConfigureAwait(false); await error.FlushAsync(CancellationToken.None).ConfigureAwait(false); }
        if (failure is not null && view.DiagnosticText.Length == 0)
        {
            await Failure(token.IsCancellationRequested ? "Canceled" : "TerminalPreviewFailed",
                "Terminal preview failed after joining owned work; inspect durable state before retrying.", error).ConfigureAwait(false); return 1;
        }
        return failure is null ? result : 1;

        async Task<TerminalInputExit> ReadTerminalAsync()
        {
            try { return await new TerminalChatInput(terminal, clock: null, focusOwner: focusOwner, keybindings: keybindings,
                kittyProtocolActive: kittyProtocolActive, selectListRouter: selectRouter, queueRestoration: frontend,
                doubleEscapeAction: ownerConfiguration.DoubleEscapeAction, terminalInputAdmission: terminalInputAdmission, captureTerminalSessionGeneration: terminalInputAdmission is null ? null : frontend.CaptureSessionGeneration).RunAcknowledgedAsync(frontend.LineAsync,
                async canceledToken => { await frontend.LineAsync("/cancel", canceledToken).ConfigureAwait(false); },
                view.SetDraftAsync, receipts, (editor, accepted) =>
                {
                    if (accepted.SessionReplaced) editor.Reset();
                    editor.AddToHistory(accepted.Text);
                }, errorValue =>
                {
                    // EOF closes command admission before this owner waits for late responses.
                    if (errorValue is null && !token.IsCancellationRequested && !inputCancellation.IsCancellationRequested)
                        frontend.EndStartupInput();
                    connection.CompleteInput();
                    if (errorValue is not null) Cancel(hostCancellation);
                }, () =>
                {
                    Volatile.Write(ref userShutdown, 1);
                    hostCancellation.Cancel(); inputCancellation.Cancel();
                }, inputCancellation.Token,
                view.CaptureEditorGeometry, observeInputCompletion: observeInputCompletion,
                beginPhysicalRead: beginInput.Task, gracefulInterrupt: true,
                observeFailures: facts => ObserveInputFailureFacts(facts, RecordFailure)).ConfigureAwait(false); }
            finally { connection.CompleteInput(); }
        }

        async Task<int> RunHostAsync(IRpcExtensionUiPresentationObserver presentation)
        {
            try { return await RpcSessionCommand.RunWithPresentationAsync(rpcArgs, connection.Input, connection.Output,
                view.Diagnostics, presentation, hostCancellation.Token,
                userShutdown: () => Volatile.Read(ref userShutdown) != 0 && !token.IsCancellationRequested,
                stopTerminalAndJoin: async settlement =>
                {
                    shutdownSettlement = settlement;
                    // Input's receipt loop is part of the original input task. Completing
                    // receipts here breaks the host/input join cycle without detaching it.
                    receipts.Complete();
                    try { shutdownObserver?.Invoke(settlement); } catch (Exception errorValue) { RecordFailure(errorValue); }
                    var cleanupFailures = await StopTerminalAndJoin().ConfigureAwait(false);
                    return settlement.AcknowledgeTerminalStopped(cleanupFailures);
                }, liveRuntime: liveRuntime, terminalInputAdmission: terminalInputAdmission,
                decorateTerminalUi: inner => new TerminalCustomComponentUiProvider(inner, view, terminalInputAdmission!,
                    frontend.CaptureSessionGeneration)).ConfigureAwait(false); }
            finally { receipts.Complete(); } // Actual host and its awaited output callbacks have settled.
        }

        void Cancel(CancellationTokenSource source)
        { try { source.Cancel(); } catch (Exception errorValue) { RecordFailure(errorValue); } }

        void RecordFailure(Exception errorValue)
        {
            if (IsOwnedReceiptWaitCancellation(errorValue, receipts, inputCancellation.Token))
            {
                // Retain external caller cancellation as the command outcome, while
                // an owned receipt wait adds no new cleanup failure to the acknowledgment.
                lock (failureGate) if (token.IsCancellationRequested) failure ??= errorValue;
                return;
            }
            lock (failureGate)
            {
                failure ??= errorValue;
                if (!failures.Any(existing => ReferenceEquals(existing, errorValue))) failures.Add(errorValue);
            }
        }

        Task<ImmutableArray<Exception>> StopTerminalAndJoin()
        {
            TaskCompletionSource? begin = null; Task<ImmutableArray<Exception>> original;
            lock (stopGate)
            {
                if (terminalStop is null)
                {
                    begin = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    terminalStop = StopTerminalCoreAsync(begin.Task);
                }
                original = terminalStop;
            }
            // Publish the original stop task before any cancellation callback or I/O.
            begin?.TrySetResult(); return original;
        }

        async Task<ImmutableArray<Exception>> StopTerminalCoreAsync(Task begin)
        {
            await begin.ConfigureAwait(false);
            var orderlyQuit = false;
            receipts.Complete();
            Cancel(inputCancellation); Cancel(resizeCancellation); connection.CompleteInput();
            Task? terminalInputCloseOriginal = null;
            if (terminalInputAdmission is not null)
                try { terminalInputCloseOriginal = terminalInputAdmission.StopAdmissionAndJoinAsync().AsTask(); }
                catch (Exception errorValue) { RecordFailure(errorValue); }
            if (reading is not null)
                try { orderlyQuit = await reading.ConfigureAwait(false) == TerminalInputExit.Quit; }
                catch (Exception errorValue) { RecordFailure(errorValue); Cancel(hostCancellation); }
            if (terminalInputCloseOriginal is not null)
                try { await terminalInputCloseOriginal.ConfigureAwait(false); }
                catch when (terminalInputCloseOriginal.IsFaulted) { RecordFailure(terminalInputCloseOriginal.Exception!); }
                catch (Exception errorValue) { RecordFailure(errorValue); }
            if (resizing is not null)
                try { _ = await JoinResizeStopAsync(resizing, resizeCancellation.Token).ConfigureAwait(false); }
                catch (Exception errorValue) { RecordFailure(errorValue); }
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch (Exception errorValue) { RecordFailure(errorValue); }
            try { await selectList.DisposeAsync().ConfigureAwait(false); } catch (Exception errorValue) { RecordFailure(errorValue); }
            // Phase one has already stopped admission and joined application work.
            // Its owned user cancellation is not a drain failure; every other original
            // phase-one error prevents an orderly drain. Runtime cleanup is still held.
            var phaseOneAllowsDrain = shutdownSettlement is { } settlement && settlement.Failures.All(errorValue =>
                errorValue is OperationCanceledException canceled && canceled.CancellationToken == hostCancellation.Token &&
                hostCancellation.IsCancellationRequested && Volatile.Read(ref userShutdown) != 0 && !token.IsCancellationRequested);
            if (failure is null && phaseOneAllowsDrain && !token.IsCancellationRequested &&
                (orderlyQuit || Volatile.Read(ref userShutdown) != 0))
                try { await TerminalInputDrain.RunAsync(terminal, token, shutdownTimeProvider).ConfigureAwait(false); }
                catch (Exception errorValue) { RecordFailure(errorValue); }
            view.CompleteDiagnosticPresentation();
            try { await view.DisposeAsync().ConfigureAwait(false); } catch (Exception errorValue) { RecordFailure(errorValue); }
            if (restoreTerminalAndJoin is not null)
                try { await restoreTerminalAndJoin().ConfigureAwait(false); } catch (Exception errorValue) { RecordFailure(errorValue); }
            lock (failureGate) return failures.ToImmutableArray();
        }

        async ValueTask ObserveRecord(JsonData record, CancellationToken observedToken)
        {
            await frontend.ObserveAsync(record, observedToken).ConfigureAwait(false);
            if (programStatus is not null && ProgramStatusReporter.Observes(record.Value))
                await view.ReportProgramStatusAsync(() => programStatus.HandleEvent(record.Value), observedToken).ConfigureAwait(false);
            if (observer is not null) await observer(record, observedToken).ConfigureAwait(false);
        }
    }
    private sealed class ObservedPresentation(IRpcExtensionUiPresentationObserver frontend,
        IRpcExtensionUiPresentationObserver observer) : IRpcExtensionUiPresentationObserver
    {
        public async ValueTask PublishedAsync(RpcExtensionUiPresentation presentation, CancellationToken token)
        {
            await frontend.PublishedAsync(presentation, token).ConfigureAwait(false);
            await observer.PublishedAsync(presentation, token).ConfigureAwait(false);
        }
        public async ValueTask RetiredAsync(RpcExtensionUiRetirement retirement, CancellationToken token)
        {
            await frontend.RetiredAsync(retirement, token).ConfigureAwait(false);
            await observer.RetiredAsync(retirement, token).ConfigureAwait(false);
        }
    }
    // Pi interactive-mode.ts reports open extension dialogs as blocked: confirm waits for permission, the
    // others for an answer, each with its title. Queued dialogs are keyed by request; the latest open one wins.
    private sealed class ProgramStatusPresentation(IRpcExtensionUiPresentationObserver inner,
        ProgramStatusReporter reporter, TerminalSessionView view) : IRpcExtensionUiPresentationObserver
    {
        public async ValueTask PublishedAsync(RpcExtensionUiPresentation presentation, CancellationToken token)
        {
            await inner.PublishedAsync(presentation, token).ConfigureAwait(false);
            var request = presentation.Request.Value;
            if (request.TryGetProperty("method", out var method) && method.GetString() is "select" or "confirm" or "input" or "editor")
                await view.ReportProgramStatusAsync(() => reporter.SetBlocked(Source(presentation.Identity), new(
                    method.GetString() == "confirm" ? Tui.Rendering.TerminalProgramBlockedKind.Permission : Tui.Rendering.TerminalProgramBlockedKind.Question,
                    request.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString()! : "")), token).ConfigureAwait(false);
        }
        public async ValueTask RetiredAsync(RpcExtensionUiRetirement retirement, CancellationToken token)
        {
            await inner.RetiredAsync(retirement, token).ConfigureAwait(false);
            // Retirement may follow terminal stop, whose cleanup already cleared the status.
            if (!view.IsClosing) await view.ReportProgramStatusAsync(() => reporter.SetBlocked(Source(retirement.Identity), null), token).ConfigureAwait(false);
        }
        private static string Source(RpcExtensionUiPresentationIdentity identity) =>
            $"extension-dialog:{identity.SessionGeneration}:{identity.RequestId}";
    }
    // Only the actual receipt owner and contributors captured at its local wait may classify this cancellation.
    internal static bool IsOwnedReceiptWaitCancellation(Exception error, TerminalSubmissionReceipts owner, CancellationToken inputOwnerToken) =>
        error is TerminalReceiptWaitCanceledException canceled && ReferenceEquals(canceled.Owner, owner) &&
        ReferenceEquals(canceled.Original, canceled.InnerException) && canceled.Original.CancellationToken == canceled.WaitToken &&
        canceled.WaitToken.IsCancellationRequested && canceled.InputOwnerToken == inputOwnerToken &&
        canceled.InputOwnerRequested;

    internal static void ObserveInputFailureFacts(TerminalInputFailureFacts facts, Action<Exception> record)
    {
        if (facts.Primary is { } primary) record(primary);
        foreach (var secondary in facts.Secondary) record(secondary);
    }

    // Both the first-completion branch and shutdown join use the same original task and
    // the resize owner's existing cancellation rule. Foreign failures remain authoritative.
    internal static async Task<bool> JoinResizeStopAsync(Task original, CancellationToken ownedToken)
    {
        try { await original.ConfigureAwait(false); return false; }
        catch (OperationCanceledException canceled) when (ownedToken.IsCancellationRequested && canceled.CancellationToken == ownedToken)
        { return true; }
    }

    private static async Task Resize(TerminalSessionView view, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) await view.RefreshViewportAsync(token).ConfigureAwait(false);
    }
    private static async Task Failure(string code, string message, TextWriter error, bool startupRejection = false)
    {
        var diagnostic = startupRejection
            ? JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", code, message, effectsMayHaveCompleted = true, cleanupFailureCount = 0 })
            : JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", code, message, effectsMayHaveCompleted = true });
        await error.WriteAsync((diagnostic + "\n").AsMemory(), CancellationToken.None).ConfigureAwait(false);
        await error.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
