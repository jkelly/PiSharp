// Separate authored startup consumer. The R49 fixture remains byte-identical.
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Rpc;
using PiSharp.Rpc.Ui;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal sealed class StartupInputFixture : IAsyncDisposable
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

    private StartupInputFixture(bool kitty, SelectorHold? heldRead, string? configuration)
    {
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
        Reading = new(Terminal, null, Focus, bindings, kittyProtocolActive: kitty, selectListRouter: router);
    }
    private TerminalChatInput Reading { get; }

    internal static async Task<StartupInputFixture> Create(Task beginPhysicalRead, bool kitty = false, SelectorHold? heldRead = null, string? configuration = null)
    {
        var fixture = new StartupInputFixture(kitty, heldRead, configuration);
        try
        {
            await fixture.View.StartAsync(default);
            fixture.Input = fixture.Reading.RunAcknowledgedAsync(fixture.Frontend.LineAsync,
                async token => { await fixture.Frontend.LineAsync("/cancel", token); }, fixture.Paint, fixture.receipts,
                (editor, accepted) => { if (accepted.SessionReplaced) editor.Reset(); editor.AddToHistory(accepted.Text); },
                _ => fixture.BeginHostClose(), fixture.Cancellation.Cancel, fixture.Cancellation.Token,
                fixture.View.CaptureEditorGeometry, fixture.InputCompleted, beginPhysicalRead: beginPhysicalRead);
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
        lock (gate) { completedInputs++; signal = changed; changed = Signal(); }
        signal.TrySetResult();
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
    internal SelectorHold HoldReply()
    { lock (gate) { if (heldReply is not null) throw new InvalidOperationException("Only one held reply"); return heldReply = new(); } }
    private async Task Send(JsonData record, CancellationToken token)
    {
        SelectorHold? held;
        lock (gate) { commands.Add(record); held = record.Value.GetProperty("type").GetString() == "extension_ui_response" ? heldReply : null; heldReply = null; }
        if (held is not null) { held.Entered.TrySetResult(); using var registration = token.Register(() => held.Canceled.TrySetResult()); await held.Release.Task; }
        token.ThrowIfCancellationRequested();
        if (record.Value.GetProperty("type").GetString() == "extension_ui_response") Ui.AcceptResponse(record);
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

