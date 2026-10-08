using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Ui;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal sealed class SelectorFixture : IAsyncDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly object gate = new();
    private readonly List<JsonData> commands = [];
    private readonly List<string> failures = [];
    private readonly TerminalSubmissionReceipts receipts = new();
    private readonly BoundedRpcConnection connection;
    private readonly JsonlWriter writer;
    private readonly TerminalSelectListDialogController controller;
    private readonly TerminalSelectListInputRouter router;
    private TaskCompletionSource changed = Signal();
    private readonly TaskCompletionSource firstPaint = Signal();
    private int completedInputs;
    private readonly Dictionary<string, int> completedRawInputs = new(StringComparer.Ordinal);
    private SelectorHold? heldReply;
    private Task? hostClose, stopping;
    internal readonly CancellationTokenSource Cancellation = new();
    internal readonly SelectorTerminal Terminal;
    internal readonly SelectorViewport Viewport = new();
    internal readonly TerminalEditorFocusOwner Focus = new();
    internal readonly TerminalSessionView View;
    internal readonly InteractiveSessionFrontend Frontend;
    internal readonly SelectorObserver Observer;
    internal readonly SelectorClock Clock = new();
    internal readonly RpcExtensionUiCoordinator Ui;
    internal Task<TerminalInputExit> Input { get; private set; } = null!;
    internal TerminalDraftSnapshot? LastDraft { get; private set; }

    private readonly Func<JsonData, CancellationToken, Task>? commandHandler;
    private SelectorFixture(bool kitty, SelectorHold? heldRead, string? configuration,
        Func<JsonData, CancellationToken, Task>? commandHandler, TimeProvider? inputClock, TerminalDoubleEscapeAction doubleEscapeAction)
    {
        this.commandHandler = commandHandler;
        Terminal = new(heldRead);
        var bindings = TerminalKeybindingConfigurationLoader.Load("C:/selector-fixture", "win32", readText: _ => configuration).CreateBindings(kitty);
        View = new(Terminal, Viewport, null, Focus, bindings);
        Frontend = new(View, nativePresentation: true, submissionReceipts: receipts);
        controller = new(Frontend, View, bindings); router = new(Frontend, controller, Focus); Frontend.BindSelectList(router);
        connection = new(Frontend.ObserveAsync); writer = new(connection.Output); Observer = new(Frontend);
        Ui = new(timeProvider: Clock, presentationObserver: Observer);
        Ui.Attach(async (record, suppress, token) => { suppress.ThrowIfCancellationRequested(); await writer.WriteAsync(record, token); },
            failure => { lock (gate) failures.Add(failure.ToString()); }, 1_048_576);
        Frontend.Bind(Send);
        Reading = new(Terminal, inputClock, Focus, bindings, kittyProtocolActive: kitty, selectListRouter: router, queueRestoration: Frontend,
            doubleEscapeAction: doubleEscapeAction);
    }
    private TerminalChatInput Reading { get; }

    internal static async Task<SelectorFixture> Create(bool kitty = false, SelectorHold? heldRead = null, string? configuration = null,
        Func<JsonData, CancellationToken, Task>? commandHandler = null, TimeProvider? inputClock = null,
        TerminalDoubleEscapeAction doubleEscapeAction = TerminalDoubleEscapeAction.Tree)
    {
        var fixture = new SelectorFixture(kitty, heldRead, configuration, commandHandler, inputClock, doubleEscapeAction);
        try
        {
            await fixture.View.StartAsync(default);
            fixture.Input = fixture.Reading.RunAcknowledgedAsync(fixture.Frontend.LineAsync,
                async token => { await fixture.Frontend.LineAsync("/cancel", token); }, fixture.Paint, fixture.receipts,
                (editor, accepted) => { if (accepted.SessionReplaced) editor.Reset(); editor.AddToHistory(accepted.Text); },
                _ => fixture.BeginHostClose(), fixture.Cancellation.Cancel, fixture.Cancellation.Token,
                fixture.View.CaptureEditorGeometry, fixture.InputCompleted);
            await fixture.firstPaint.Task.WaitAsync(Deadline); return fixture;
        }
        catch { heldRead?.Release.TrySetResult(); await fixture.DisposeAsync(); throw; }
    }

    private async ValueTask Paint(TerminalDraftSnapshot draft, CancellationToken token)
    {
        await View.SetDraftAsync(draft, token); LastDraft = draft; firstPaint.TrySetResult();
    }
    private void InputCompleted(TerminalInputEvent input)
    {
        TaskCompletionSource signal;
        lock (gate)
        {
            completedInputs++;
            if (TerminalInputDecoder.TryGetOriginalInput(input, out var raw, out _))
                completedRawInputs[raw] = completedRawInputs.GetValueOrDefault(raw) + 1;
            signal = changed; changed = Signal();
        }
        signal.TrySetResult();
    }
    internal int CompletedRawCount(string raw) { lock (gate) return completedRawInputs.GetValueOrDefault(raw); }
    internal async Task WaitCompletedRaw(string raw, int count)
    {
        while (true)
        {
            Task next; lock (gate) { if (completedRawInputs.GetValueOrDefault(raw) >= count) return; next = changed.Task; }
            await next.WaitAsync(Deadline);
        }
    }
    internal async Task Key(string wire)
    {
        int expected; lock (gate) expected = completedInputs + 1;
        Terminal.Feed(wire);
        while (true)
        {
            Task next; lock (gate) { if (completedInputs >= expected) return; next = changed.Task; }
            if (await Task.WhenAny(next, Input).WaitAsync(Deadline) == Input)
            { await Input; throw new InvalidOperationException("Input ended before the original event consumer completed"); }
            await next.WaitAsync(Deadline);
        }
    }
    internal async Task TypeDraft(string text) { foreach (var character in text) await Key(character.ToString()); }
    internal async Task WaitDraft(string text)
    {
        while (true)
        {
            Task next; lock (gate) { if (LastDraft?.Text == text) return; next = changed.Task; }
            if (await Task.WhenAny(next, Input).WaitAsync(Deadline) == Input)
            { await Input; throw new InvalidOperationException("Input ended before expected draft."); }
            await next.WaitAsync(Deadline);
        }
    }
    internal SelectorHold HoldReply()
    { lock (gate) { if (heldReply is not null) throw new InvalidOperationException("Only one held reply"); return heldReply = new(); } }
    private async Task Send(JsonData record, CancellationToken token)
    {
        SelectorHold? held;
        lock (gate) { commands.Add(record); held = record.Value.GetProperty("type").GetString() == "extension_ui_response" ? heldReply : null; heldReply = null; }
        if (held is not null) { held.Entered.TrySetResult(); using var registration = token.Register(() => held.Canceled.TrySetResult()); await held.Release.Task; }
        token.ThrowIfCancellationRequested();
        if (record.Value.GetProperty("type").GetString() == "extension_ui_response") Ui.AcceptResponse(record);
        else if (commandHandler is not null) await commandHandler(record, token);
        else throw new InvalidOperationException("Unexpected prompt/effect from selector input: " + record);
    }
    internal JsonData[] Replies() { lock (gate) return commands.Where(row => row.Value.GetProperty("type").GetString() == "extension_ui_response").ToArray(); }
    private void BeginHostClose() { lock (gate) hostClose ??= CloseHost(); }
    private async Task CloseHost()
    { try { await Ui.DisposeAsync(); } finally { receipts.Complete(); } }
    internal Task StopAsync() { lock (gate) return stopping ??= Stop(); }
    private async Task Stop()
    {
        Terminal.End();
        if (Input is not null)
            try { await Input; } catch (OperationCanceledException) when (Cancellation.IsCancellationRequested) { }
        else BeginHostClose();
        Task closing; lock (gate) closing = hostClose ??= CloseHost(); await closing;
        await writer.DisposeAsync(); await connection.DisposeAsync(); await controller.DisposeAsync(); await View.DisposeAsync();
        Frontend.Dispose();
        string[] errors; lock (gate) errors = failures.ToArray();
        TerminalSelectDialogCases.Equal(0, errors.Length);
    }
    internal object Snapshot() => new { input = Input.Status, terminal = Terminal.Snapshot, Terminal.MaximumReaders,
        Terminal.Disposals, replies = Replies(), retired = Observer.Retired(), LastDraft,
        editorOwnsFocus = LastDraft?.EditorOwnsFocus, appliedEditorFocus = LastDraft?.EditorFocus,
        editorLayoutIdentity = LastDraft?.Layout?.Identity };
    public async ValueTask DisposeAsync()
    {
        SelectorHold? held; lock (gate) held = heldReply; held?.Release.TrySetResult();
        Terminal.ReleaseOwnedHolds(); Cancellation.Cancel();
        try { await StopAsync(); } finally { Cancellation.Dispose(); }
    }
    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class SelectorHold
{
    internal readonly TaskCompletionSource Entered = SelectorFixture.Signal(), Release = SelectorFixture.Signal(), Canceled = SelectorFixture.Signal();
}

internal sealed class SelectorObserver(InteractiveSessionFrontend frontend) : IRpcExtensionUiPresentationObserver
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly object gate = new();
    private readonly List<RpcExtensionUiPresentation> published = [];
    private readonly List<RpcExtensionUiRetirement> retired = [];
    private TaskCompletionSource changed = SelectorFixture.Signal();
    private int completedPublications, enteredRetirements;
    public async ValueTask PublishedAsync(RpcExtensionUiPresentation value, CancellationToken token)
    {
        lock (gate) published.Add(value);
        await frontend.PublishedAsync(value, token);
        lock (gate) completedPublications++; Changed();
    }
    public async ValueTask RetiredAsync(RpcExtensionUiRetirement value, CancellationToken token)
    {
        lock (gate) retired.Add(value);
        var original = frontend.RetiredAsync(value, token); // frontend mutates matching identity before its first awaited paint
        lock (gate) enteredRetirements++; Changed();
        await original;
    }
    private void Changed() { TaskCompletionSource signal; lock (gate) { signal = changed; changed = SelectorFixture.Signal(); } signal.TrySetResult(); }
    internal RpcExtensionUiPresentation[] Published() { lock (gate) return published.ToArray(); }
    internal RpcExtensionUiRetirement[] Retired() { lock (gate) return retired.ToArray(); }
    internal Task WaitPublished(int count) => Wait(() => completedPublications >= count);
    internal Task WaitRetiredEntered(int count) => Wait(() => enteredRetirements >= count);
    private async Task Wait(Func<bool> ready)
    { while (true) { Task next; lock (gate) { if (ready()) return; next = changed.Task; } await next.WaitAsync(Deadline); } }
}

internal sealed class SelectorViewport : ITerminalViewportSource
{
    internal TerminalViewport Value = new(80, 25, 0, 0, 80, 25);
    public TerminalViewport ReadViewport() => Value;
}

internal sealed class SelectorTerminal(SelectorHold? heldRead) : IConsoleTerminal
{
    private readonly object gate = new();
    private readonly Channel<string> inputs = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly List<string> writes = [];
    private readonly List<SelectorHold> ownedHolds = heldRead is null ? [] : [heldRead];
    private TaskCompletionSource changed = SelectorFixture.Signal();
    private (string Text, SelectorHold Hold)? nextWrite;
    private bool ended;
    internal int ActiveReads, ActiveWrites, MaximumReaders, Disposals;
    internal long ReadsStarted, ReadsSettled, WritesStarted, WritesSettled;
    private static readonly TerminalConsoleState State = new(0, 0, 65001, 65001, 25, true, 0, 0);
    public TerminalLeaseSnapshot Snapshot => new(State, State, null, false, false, ActiveReads, ActiveWrites, ReadsStarted, ReadsSettled, WritesStarted, WritesSettled);
    internal string[] Writes() { lock (gate) return writes.ToArray(); }
    internal void Feed(string wire)
    { lock (gate) { if (ended || wire.Length == 0 || !inputs.Writer.TryWrite(wire)) throw new InvalidOperationException("Closed input or empty key"); } }
    internal void End() { lock (gate) { if (ended) return; ended = true; inputs.Writer.TryWrite(""); } }
    internal SelectorHold HoldWrite(string matchingText)
    { lock (gate) { if (nextWrite is not null) throw new InvalidOperationException("Existing held write"); var hold = new SelectorHold(); ownedHolds.Add(hold); nextWrite = (matchingText, hold); return hold; } }
    internal void ReleaseOwnedHolds() { lock (gate) foreach (var hold in ownedHolds) hold.Release.TrySetResult(); }
    public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
    {
        TaskCompletionSource signal; long sequence;
        lock (gate) { sequence = ++ReadsStarted; ActiveReads++; MaximumReaders = Math.Max(MaximumReaders, ActiveReads); signal = changed; changed = SelectorFixture.Signal(); } signal.TrySetResult();
        try
        {
            if (heldRead is not null && sequence == 1)
            { heldRead.Entered.TrySetResult(); using var registration = token.Register(() => heldRead.Canceled.TrySetResult()); await heldRead.Release.Task; }
            var value = await inputs.Reader.ReadAsync(token);
            if (value.Length > destination.Length) throw new IOException("Fixture input exceeds actual read capacity");
            value.AsMemory().CopyTo(destination); return value.Length;
        }
        finally { lock (gate) { ActiveReads--; ReadsSettled++; } }
    }
    // A subsequent physical read starts only after TerminalChatInput decoded/published
    // the previous one. This observes real producer sequencing, not simulated dispatch.
    internal async Task WaitDecodedReads(int count)
    { while (true) { Task next; lock (gate) { if (ReadsStarted >= count + 1) return; next = changed.Task; } await next.WaitAsync(TimeSpan.FromSeconds(10)); } }
    public async ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
    {
        SelectorHold? held = null;
        lock (gate)
        {
            var text = frame.ToString(); writes.Add(text); WritesStarted++; ActiveWrites++;
            if (nextWrite is { } next && text.Contains(next.Text, StringComparison.Ordinal)) { held = next.Hold; nextWrite = null; }
        }
        try
        {
            if (held is not null) { held.Entered.TrySetResult(); using var registration = token.Register(() => held.Canceled.TrySetResult()); await held.Release.Task; }
            token.ThrowIfCancellationRequested();
        }
        finally { lock (gate) { ActiveWrites--; WritesSettled++; } }
    }
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}

internal sealed class SelectorClock : TimeProvider
{
    private Timer? current;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => current = new(callback, state);
    internal void Fire() => (current ?? throw new InvalidOperationException("Actual coordinator timer absent")).Fire();
    private sealed class Timer(TimerCallback callback, object? state) : ITimer
    {
        private bool disposed, fired;
        internal void Fire() { if (disposed || fired) throw new InvalidOperationException("Timer already settled"); fired = true; callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
