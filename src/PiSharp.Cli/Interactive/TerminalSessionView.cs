using System.Globalization;
using System.Collections.Immutable;
using System.Text;
using PiSharp.Tui;
using PiSharp.Tui.Components.SelectList;
using PiSharp.Tui.Components.Text;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

namespace PiSharp.Cli.Interactive;

/// <summary>Owned terminal presentation over a borrowed console. It owns no session/editor state.</summary>
internal sealed class TerminalSessionView : IInteractiveSessionPresentation, ITerminalPendingQueuePresentation, IAsyncDisposable
{
    private const int RetainedCharacters = 32_768, MaximumDraftCharacters = 65_536;
    private readonly IConsoleTerminal terminal;
    private readonly ITerminalViewportSource viewport;
    private readonly TerminalEditorFocusOwner? focusOwner;
    internal TerminalKeybindings? Keybindings { get; }
    private readonly VtRenderer renderer;
    private readonly TerminalTextLayout layout = new(new EscapedAsciiWidth());
    private readonly Action<TerminalEditorVisualMap, TerminalEditorRenderedFrame>? observeSourceFrame;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly object gate = new();
    private readonly Queue<string> display = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TerminalViewport? geometry, observedGeometry;
    private readonly Guid viewLifetime = Guid.NewGuid();
    private long geometryRevision;
    private TerminalDraftSnapshot draft = new("", 0);
    private TerminalSelectList? selectList;
    private string selectTitle = "";
    private TerminalPendingQueueSnapshot pendingQueue = TerminalPendingQueueSnapshot.Empty(1);
    private bool selectListFocused;
    private bool draftReceived;
    private Guid presentationEditorLifetime;
    private long presentationScrollResetRevision = -1;
    private int sourceScroll;
    private int retained, pending;
    private bool clipped, entered;
    private Task? closing;

    internal TerminalSessionView(IConsoleTerminal terminal, ITerminalViewportSource viewport,
        Action<TerminalEditorVisualMap, TerminalEditorRenderedFrame>? observeSourceFrame = null)
        : this(terminal, viewport, observeSourceFrame, null) { }

    internal TerminalSessionView(IConsoleTerminal terminal, ITerminalViewportSource viewport,
        Action<TerminalEditorVisualMap, TerminalEditorRenderedFrame>? observeSourceFrame, TerminalEditorFocusOwner? focusOwner)
        : this(terminal, viewport, observeSourceFrame, focusOwner, null) { }

    internal TerminalSessionView(IConsoleTerminal terminal, ITerminalViewportSource viewport,
        Action<TerminalEditorVisualMap, TerminalEditorRenderedFrame>? observeSourceFrame, TerminalEditorFocusOwner? focusOwner,
        TerminalKeybindings? keybindings)
    {
        ArgumentNullException.ThrowIfNull(terminal); ArgumentNullException.ThrowIfNull(viewport);
        this.terminal = terminal; this.viewport = viewport; renderer = new(terminal);
        this.observeSourceFrame = observeSourceFrame; this.focusOwner = focusOwner;
        Keybindings = keybindings?.CreateSnapshot();
        Diagnostics = new DiagnosticWriter(this);
    }
    internal TextWriter Diagnostics { get; }
    internal string DiagnosticText => ((DiagnosticWriter)Diagnostics).Text;

    internal ValueTask StartAsync(CancellationToken token) => Run(async () =>
    {
        if (entered) throw new InvalidOperationException("Terminal preview was already started.");
        geometry = ReadGeometry();
        // Mark ownership before the write: partial/faulted entry still requires an awaited leave attempt.
        entered = true;
        await terminal.WriteAsync("\u001b[?1049h\u001b[?2004h".AsMemory(), token).ConfigureAwait(false);
        renderer.Invalidate(); Append("PiSharp terminal session\n");
        // The input owner's first captured layout supplies the first editor frame.
    }, token);

    public ValueTask PresentAsync(string displayText, CancellationToken token) => Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(displayText);
        if (displayText.Length > 8 * 1024 * 1024 + 1) throw new InvalidOperationException("Terminal presentation exceeds its input bound.");
        Append(displayText); await Draw(token).ConfigureAwait(false);
    }, token);

    public ValueTask PresentPendingAsync(TerminalPendingQueueSnapshot snapshot, CancellationToken token) => Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SessionGeneration < pendingQueue.SessionGeneration) return;
        pendingQueue = snapshot;
        if (entered) await Draw(token).ConfigureAwait(false);
    }, token);

    internal ValueTask SetDraftAsync(TerminalDraftSnapshot value, CancellationToken token) => Run(async () =>
    {
        ArgumentNullException.ThrowIfNull(value); ChatEditor.Validate(value.Text);
        if (value.Text.Length > MaximumDraftCharacters || value.CursorUtf16Offset < 0 || value.CursorUtf16Offset > value.Text.Length ||
            value.CursorUtf16Offset > 0 && value.CursorUtf16Offset < value.Text.Length &&
            char.IsHighSurrogate(value.Text[value.CursorUtf16Offset - 1]) && char.IsLowSurrogate(value.Text[value.CursorUtf16Offset]))
            throw new InvalidOperationException("The terminal preview cursor must be at a valid draft scalar boundary.");
        if (value.Layout is { } captured && (captured.Snapshot.Text != value.Text ||
            captured.Snapshot.CursorUtf16Offset != value.CursorUtf16Offset))
            throw new TerminalEditorLayoutException(TerminalEditorLayoutFailure.InvalidInput);
        if (focusOwner is not null && (value.EditorFocus is not { } ownership ||
            !focusOwner.IsCurrent(ownership) || ownership.EditorOwnsFocus != value.EditorOwnsFocus ||
            value.Layout is { } nextLayout && draft.Layout is { } previousLayout &&
                nextLayout.Identity.EditorLifetimeId == previousLayout.Identity.EditorLifetimeId &&
                nextLayout.Identity.EditorRevision < previousLayout.Identity.EditorRevision ||
            value.Layout is { } ownedLayout && ownedLayout.Identity.EditorLifetimeId != ownership.EditorLifetimeId))
            throw new TerminalEditorFocusException(TerminalEditorFocusFailure.StaleIdentity);
        draft = value; draftReceived = true; await Draw(token).ConfigureAwait(false);
    }, token);

    internal TerminalEditorGeometrySnapshot CaptureEditorGeometry()
    {
        lock (gate) ObjectDisposedException.ThrowIf(closing is not null, this);
        return ReadGeometryState().Editor;
    }

    // The controller's input turn dispatches the action; this existing render turn alone
    // changes component state. Resize and transcript paints cannot observe a half mutation.
    internal ValueTask SetSelectListAsync(TerminalSelectList? value, CancellationToken token, bool focused = true,
        string? title = null) => Run(async () =>
    {
        selectList = value; selectTitle = title ?? ""; selectListFocused = focused;
        renderer.Invalidate(); if (entered) await Draw(token).ConfigureAwait(false);
    }, token);

    internal ValueTask UpdateSelectListAsync(TerminalSelectList expected, Action<TerminalSelectList> update,
        CancellationToken token) => Run(async () =>
    {
        if (!ReferenceEquals(selectList, expected)) throw new InvalidOperationException("Stale selector render turn.");
        update(expected); await Draw(token).ConfigureAwait(false);
    }, token);

    internal ValueTask RefreshViewportAsync(CancellationToken token) => Run(async () =>
    {
        var actual = ReadGeometry();
        if (actual != geometry) { geometry = actual; renderer.Invalidate(); await Draw(token).ConfigureAwait(false); }
    }, token);

    private TerminalViewport ReadGeometry() => ReadGeometryState().Viewport;

    private (TerminalViewport Viewport, TerminalEditorGeometrySnapshot Editor) ReadGeometryState()
    {
        var actual = viewport.ReadViewport();
        if (actual.Columns is < 1 or > 256 || actual.Rows is < 1 or > 256 || actual.Left < 0 || actual.Top < 0 ||
            actual.BufferColumns < actual.Columns || actual.BufferRows < actual.Rows ||
            actual.Left > actual.BufferColumns - actual.Columns || actual.Top > actual.BufferRows - actual.Rows)
            throw new InvalidOperationException("The actual terminal viewport is outside the supported 256-column/256-row bounds.");
        lock (gate)
        {
            if (actual != observedGeometry)
            {
                if (geometryRevision == long.MaxValue) throw new InvalidOperationException("Terminal geometry revision is exhausted.");
                observedGeometry = actual; geometryRevision++;
            }
            return (actual, new(new(viewLifetime, geometryRevision, TerminalEditorVisualMapBuilder.SourcePolicyId),
                actual.Columns, actual.Rows));
        }
    }

    private async Task Draw(CancellationToken token)
    {
        if (!entered) throw new InvalidOperationException("Terminal preview has not started.");
        if (!draftReceived) return;
        var (actual, editorGeometry) = ReadGeometryState();
        if (actual != geometry) { geometry = actual; renderer.Invalidate(); }
        var columns = actual.Columns; var rows = actual.Rows;
        if (selectList is not null)
        {
            var selected = TerminalExtensionSelectorFrameFactory.Create(selectTitle, selectList, rows, columns, selectListFocused);
            await renderer.RenderAsync(selected.Frame, token).ConfigureAwait(false); return;
        }
        if (draft.Layout is { } captured)
        {
            TerminalEditorVisualMap map;
            try { map = new TerminalEditorVisualMapBuilder().BuildForTerminal(captured, editorGeometry); }
            catch (TerminalEditorLayoutException error) when (columns <= 2 &&
                error.Failure == TerminalEditorLayoutFailure.ResourceLimit)
            {
                await DrawUnavailable(rows, columns, token).ConfigureAwait(false); return;
            }
            if (presentationEditorLifetime != map.EditorIdentity.EditorLifetimeId ||
                presentationScrollResetRevision != map.ScrollResetRevision)
            {
                presentationEditorLifetime = map.EditorIdentity.EditorLifetimeId;
                presentationScrollResetRevision = map.ScrollResetRevision; sourceScroll = 0;
            }
            var transcript = Wrap((clipped ? new[] { "[earlier view clipped]", "\n" } : []).Concat(display), columns).ToImmutableArray();
            var presented = TerminalEditorFrameFactory.Create(map, sourceScroll, transcript, draft.EditorOwnsFocus);
            presented = TerminalPendingQueueFrameFactory.Create(presented, pendingQueue.Steering, pendingQueue.FollowUp);
            // Source rendering mutates its scalar scroll before transport completion. A held
            // physical write must not change the next captured controller/layout identity.
            sourceScroll = presented.FirstVisibleSourceRow;
            observeSourceFrame?.Invoke(map, presented);
            await renderer.RenderAsync(presented.Frame, token).ConfigureAwait(false); return;
        }
        // Existing literal/no-geometry adapter callers retain their established projection.
        // Actual session terminal always supplies the controller's captured Source layout.
        var projected = TerminalTextEditorProjection.Create(new(draft.Text, draft.CursorUtf16Offset), columns, Math.Min(rows, 3));
        var input = projected.Rows;
        var inputRows = input.Length;
        var historyRows = rows - inputRows;
        var history = Wrap((clipped ? new[] { "[earlier view clipped]", "\n" } : []).Concat(display), columns);
        var visible = new List<string>(rows);
        if (historyRows > 0)
        {
            visible.AddRange(history.TakeLast(historyRows));
            while (visible.Count < historyRows) visible.Add("");
        }
        visible.AddRange(input);
        var cursor = new TerminalCursor(historyRows + projected.CursorRow, projected.CursorColumn, draft.EditorOwnsFocus);
        var frame = layout.CreateFrame(string.Join('\n', visible), rows, columns, cursor);
        await renderer.RenderAsync(frame, token).ConfigureAwait(false);
    }

    private async Task DrawUnavailable(int rows, int columns, CancellationToken token)
    {
        // Host-owned ASCII diagnostic only. Canonical draft, registered pastes and source
        // scroll stay intact and a later actual resize retries the same captured layout.
        var frame = layout.CreateFrame("Editor needs a wider window", rows, columns, new TerminalCursor(0, 0, false));
        await renderer.RenderAsync(frame, token).ConfigureAwait(false);
    }

    private void Append(string text)
    {
        foreach (var fragment in Project(text))
        {
            if (fragment.Length > RetainedCharacters) { clipped = true; continue; }
            while (display.Count > 0 && retained > RetainedCharacters - fragment.Length)
            { retained -= display.Dequeue().Length; clipped = true; }
            display.Enqueue(fragment); retained += fragment.Length;
        }
    }

    private static IEnumerable<string> Project(string text)
    {
        for (var index = 0; index < text.Length; index++)
            if (char.IsHighSurrogate(text[index]))
            { if (++index >= text.Length || !char.IsLowSurrogate(text[index])) throw new TerminalRenderException(TerminalRenderFailure.InvalidUnicode); }
            else if (char.IsLowSurrogate(text[index])) throw new TerminalRenderException(TerminalRenderFailure.InvalidUnicode);
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var cluster = elements.GetTextElement();
            if (cluster == "\n") { yield return "\n"; continue; }
            if (cluster is "\r" or "\r\n") { yield return "\n"; continue; }
            if (cluster.Length > 256) { yield return "[oversized cluster]"; continue; }
            var projected = new StringBuilder();
            foreach (var rune in cluster.EnumerateRunes())
                if (rune.Value == '\\') projected.Append("\\\\");
                else if (rune.Value is >= 0x20 and <= 0x7e) projected.Append((char)rune.Value);
                else if (rune.Value == 9) projected.Append("\\t");
                else projected.Append(rune.Value <= 0xffff ? "\\u" : "\\U")
                    .Append(rune.Value.ToString(rune.Value <= 0xffff ? "x4" : "x8", CultureInfo.InvariantCulture));
            yield return projected.ToString();
        }
    }

    private static List<string> Wrap(IEnumerable<string> fragments, int columns)
    {
        var rows = new List<string>(); var row = new StringBuilder();
        foreach (var source in fragments)
        {
            if (source == "\n") { rows.Add(row.ToString()); row.Clear(); continue; }
            // Escape projections are indivisible, including in a one-column viewport.
            var fragment = source.Length > columns ? "?" : source;
            if (row.Length + fragment.Length > columns) { rows.Add(row.ToString()); row.Clear(); }
            row.Append(fragment);
        }
        rows.Add(row.ToString()); return rows;
    }

    private async ValueTask Run(Func<Task> operation, CancellationToken token)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closing is not null, this);
            if (pending >= 16) throw new InvalidOperationException("Terminal view admission exceeds its bounded profile.");
            pending++;
        }
        var held = false;
        try { await serial.WaitAsync(token).ConfigureAwait(false); held = true; await operation().ConfigureAwait(false); }
        finally
        {
            if (held) serial.Release();
            lock (gate) { if (--pending == 0 && closing is not null) drained.TrySetResult(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; Task result;
        lock (gate)
        {
            if (closing is null)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously); closing = completion.Task;
                if (pending == 0) drained.TrySetResult();
            }
            result = closing;
        }
        if (completion is not null) _ = Close(completion); return new(result);
    }
    internal void CompleteDiagnosticPresentation() => ((DiagnosticWriter)Diagnostics).CompletePresentation();
    private async Task Close(TaskCompletionSource completion)
    {
        Exception? failure = null;
        await drained.Task.ConfigureAwait(false);
        try { await renderer.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        if (entered)
            try { await terminal.WriteAsync("\u001b[?2004l\u001b[?1049l".AsMemory(), CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { failure ??= error; }
        display.Clear(); retained = 0; draft = new("", 0); draftReceived = false; selectList = null; selectTitle = "";
        pendingQueue = TerminalPendingQueueSnapshot.Empty(1);
        presentationEditorLifetime = Guid.Empty; presentationScrollResetRevision = -1; sourceScroll = 0; serial.Dispose();
        if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
    }

    private sealed class EscapedAsciiWidth : ITerminalWidthPolicy
    {
        public string Id => "pisharp-escaped-ascii-preview-v1";
        public int GetWidth(string grapheme) => grapheme.Length == 1 && grapheme[0] is >= ' ' and <= '~'
            ? 1 : throw new TerminalRenderException(TerminalRenderFailure.InvalidWidth);
    }
    private sealed class DiagnosticWriter(TerminalSessionView view) : TextWriter
    {
        private readonly StringBuilder text = new(); private readonly object gate = new();
        private bool presentationCompleted;
        public override Encoding Encoding => Encoding.UTF8;
        internal string Text { get { lock (gate) return text.ToString(); } }
        internal void CompletePresentation() { lock (gate) presentationCompleted = true; }
        public override Task WriteAsync(string? value) => WriteAsync((value ?? "").AsMemory(), CancellationToken.None);
        public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            var value = buffer.ToString(); bool present;
            lock (gate)
            {
                if (value.Length > 8192 - text.Length) throw new InvalidOperationException("Terminal diagnostics exceed their bound.");
                text.Append(value);
                present = !presentationCompleted;
            }
            if (present) await view.PresentAsync(value, cancellationToken).ConfigureAwait(false);
        }
        public override Task FlushAsync() => Task.CompletedTask;
        public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
