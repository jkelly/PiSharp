using PiSharp.Tui.Pi;
using static Expect;

internal static class GapOverlaySnapshotCases
{
    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        yield return ("gap.overlay-snapshot-bounds-stable-during-composition", () =>
        {
            using var loop = UiLoop.CreateManual();
            var tui = new SnapshotTui(new QuietTerminal(), loop);
            IOverlayHandle? handle = null;
            var visible = true; var inspect = false;
            OverlayBounds? predicateBounds = null; OverlayBounds? renderingBounds = null;
            handle = tui.ShowOverlay(new Rows(() => { if (inspect) renderingBounds = handle!.CaptureSnapshot().Bounds; }),
                new OverlayOptions { Width = 20, Visible = (_, _) =>
                {
                    if (inspect) predicateBounds = handle!.CaptureSnapshot().Bounds;
                    return visible;
                }});
            tui.Frame();
            var completed = handle.CaptureSnapshot().Bounds;
            Check(completed is { Width: 20, Height: 1 }, "first completed bounds");
            inspect = true;
            tui.Frame();
            Equal(completed, predicateBounds, "visibility callback reads previous completed bounds");
            Equal(completed, renderingBounds, "render callback reads previous completed bounds");
            visible = false;
            tui.Frame();
            Equal<OverlayBounds?>(null, handle.CaptureSnapshot().Bounds, "completed hidden frame clears bounds");
            visible = true;
            tui.Frame();
            Equal(completed, handle.CaptureSnapshot().Bounds, "visible frame commits bounds again");
            handle.Hide();
            Equal<OverlayBounds?>(null, handle.CaptureSnapshot().Bounds, "disposed overlay has no bounds");
            return Task.CompletedTask;
        });
    }

    private sealed class SnapshotTui(ITerminal terminal, UiLoop loop) : TuiBase(terminal, loop)
    {
        public override TuiMode Mode => TuiMode.Regular;
        protected override void DoRender() { }
        internal void Frame() => CompositeOverlays([], Terminal.Columns, Terminal.Rows);
    }
    private sealed class Rows(Action onRender) : IComponent
    {
        public List<string> Render(int width) { onRender(); return ["overlay"]; }
        public void Invalidate() { }
    }
    private sealed class QuietTerminal : ITerminal
    {
        public int Columns => 100;
        public int Rows => 30;
        public bool KittyProtocolActive => false;
        public void Start(Action<string> onInput, Action onResize) { }
        public void Stop() { }
        public Task DrainInputAsync(int maxMs = 1000, int idleMs = 50) => Task.CompletedTask;
        public void Write(string data) { }
        public void MoveBy(int lines) { }
        public void HideCursor() { }
        public void ShowCursor() { }
        public void ClearLine() { }
        public void ClearFromCursor() { }
        public void ClearScreen() { }
        public void SetTitle(string title) { }
        public void SetProgress(bool active) { }
        public void SetProgramStatus(ProgramStatus status) { }
    }
}
