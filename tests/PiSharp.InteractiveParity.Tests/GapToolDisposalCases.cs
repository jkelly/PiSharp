using System.Text.Json.Nodes;
using PiSharp.Cli.Interactive.Mode.Components;
using PiSharp.Tui.Pi;
using static Expect;

internal static partial class ToolRenderingCases
{
    private sealed class DisposalView : IComponent, IDisposableComponent
    {
        internal int Disposals, Renders;
        public List<string> Render(int width) { Check(Disposals == 0, "disposed renderer was painted"); Renders++; return ["view"]; }
        public void Invalidate() => Check(Disposals == 0, "disposed renderer was invalidated");
        public void Dispose() => Disposals++;
    }

    private static IEnumerable<(string Id, Func<Task> Run)> GapDisposalTests()
    {
        yield return ("gap.tool-disposal-replacement-shared-slots-and-reset", Sync(() =>
        {
            var first = new DisposalView(); var second = new DisposalView(); var third = new DisposalView();
            var selected = first;
            var row = new ToolExecutionComponent("custom", "gap-disposal", new JsonObject(), null,
                new ToolRenderers(RenderCall: (_, _, _) => selected, RenderResult: (_, _, _, _) => selected), Ui(), "/");
            row.UpdateResult(TextResult("partial"), true);
            row.Render(80); Equal(0, first.Disposals, "same component in two slots stays owned");
            selected = second; row.UpdateResult(TextResult("complete"));
            Equal(1, first.Disposals, "shared replaced component disposed once");
            Equal(0, second.Disposals, "active replacement retained");
            row.SetExpanded(true); Equal(0, second.Disposals, "same instance reused during redraw");
            selected = third; row.UpdateArgs(new JsonObject { ["x"] = 1 }); row.Render(80);
            Equal(1, second.Disposals, "argument redraw retires previous call and result");
            var transcript = new ToolExecutionContainer(); transcript.AddChild(row); transcript.Clear(); transcript.Clear(); row.Dispose();
            Equal(1, third.Disposals, "reset retires final view once");
            var paints = third.Renders; row.UpdateResult(TextResult("late")); row.UpdateArgs(new JsonObject()); row.Invalidate();
            Equal(0, row.Render(80).Count, "disposed row renders nothing"); Equal(paints, third.Renders, "late callback cannot repaint disposed view");
        }));
        yield return ("gap.tool-disposal-preserves-call-to-result-ownership-transfer", Sync(() =>
        {
            var shared = new DisposalView(); var nextCall = new DisposalView(); var swap = false;
            var row = new ToolExecutionComponent("custom", "gap-transfer", new JsonObject(), null,
                new ToolRenderers(RenderCall: (_, _, _) => swap ? nextCall : shared, RenderResult: (_, _, _, _) => shared), Ui(), "/");
            swap = true; row.UpdateResult(TextResult("done"));
            Equal(0, shared.Disposals, "old call retained by result renderer"); row.Render(80);
            var transcript = new ToolExecutionContainer(); transcript.AddChild(row); transcript.RemoveChild(row); transcript.RemoveChild(row);
            Equal(1, shared.Disposals, "transferred view disposed once"); Equal(1, nextCall.Disposals, "new call disposed once");
        }));
    }
}
