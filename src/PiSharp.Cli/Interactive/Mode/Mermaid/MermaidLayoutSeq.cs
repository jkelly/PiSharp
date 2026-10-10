// grok-mermaid 0.2.3 (Apache-2.0): src/layout-seq.ts — used by Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/mermaid.ts.
using static PiSharp.Cli.Interactive.Mode.Mermaid.MermaidCanvas;
using static PiSharp.Cli.Interactive.Mode.Mermaid.MermaidJs;

namespace PiSharp.Cli.Interactive.Mode.Mermaid;

/// <summary>
/// Sequence diagram layout. Participants get one column each, with lifelines running the full height and a box
/// repeated at top and bottom. Column gaps are solved from the widest thing that has to fit between any two columns,
/// then items stack down the canvas in source order.
/// </summary>
internal static class MermaidLayoutSeq
{
    private const int Pad = 1;
    /// <summary>Minimum columns between adjacent lifelines.</summary>
    private const int SeqGap = 5;
    private const int MaxCanvasCells = 1 << 21;

    /// <summary>Where a note box sits, given the lifeline positions.</summary>
    private static (int X, int W) NoteGeometry(int[] xs, MermaidNoteAnchor anchor, int textW)
    {
        if (anchor.Kind == MermaidNoteKind.Over)
        {
            var center = Half(xs[anchor.From] + xs[anchor.To]);
            var w = Math.Max(xs[anchor.To] - xs[anchor.From] + 5, textW + 2 * Pad + 2);
            return (Sat(center, Half(w)), w);
        }
        var nw = textW + 2 * Pad + 2;
        if (anchor.Kind == MermaidNoteKind.Left) return (Sat(xs[anchor.From], 2 + nw - 1), nw);
        return (xs[anchor.From] + 2, nw);
    }

    private static int ItemTextW(string? text) => text is null ? 0 : MermaidWidth.StringWidth(text);

    public static MermaidCanvas? LayoutSequence(MermaidSequence seq)
    {
        var n = seq.Labels.Count;
        var labels = seq.Labels.Select(l => MermaidLabels.FitLabel(l, MermaidLabels.WrapWidth)).ToList();
        var boxW = labels.Select(l => Math.Max(1, MermaidWidth.StringWidth(l)) + 2 * Pad + 2).ToArray();
        const int boxH = 3;

        var gaps = Enumerable.Range(0, Sat(n, 1)).Select(i => Math.Max(SeqGap, CeilHalf(boxW[i]) + CeilHalf(boxW[i + 1]) + 1)).ToArray();

        // Each requirement is "columns l..r together need at least `need` cells".
        var reqs = new List<(int L, int R, int Need)>();
        foreach (var item in seq.Items)
        {
            if (item.Kind == MermaidSeqItemKind.Message)
            {
                var tw = ItemTextW(item.Text);
                if (item.From != item.To) reqs.Add((Math.Min(item.From, item.To), Math.Max(item.From, item.To), Math.Max(tw + 2, 4)));
                else if (item.From + 1 < n) reqs.Add((item.From, item.From + 1, 5 + tw + 2));
            }
            else if (item.Kind == MermaidSeqItemKind.Note)
            {
                var tw = MermaidWidth.StringWidth(item.Text!);
                var a = item.Anchor!;
                if (a.Kind == MermaidNoteKind.Over && a.From < a.To)
                {
                    reqs.Add((a.From, a.To, Sat(tw, 1)));
                }
                else if (a.Kind == MermaidNoteKind.Over)
                {
                    var need = CeilHalf(tw + 4) + 2;
                    if (a.From > 0) reqs.Add((a.From - 1, a.From, need));
                    if (a.From + 1 < n) reqs.Add((a.From, a.From + 1, need));
                }
                else if (a.Kind == MermaidNoteKind.Left && a.From > 0)
                {
                    reqs.Add((a.From - 1, a.From, tw + 7));
                }
                else if (a.Kind == MermaidNoteKind.Right && a.From + 1 < n)
                {
                    reqs.Add((a.From, a.From + 1, tw + 7));
                }
            }
        }
        // Narrowest spans first, so a wide requirement absorbs what they already gave.
        StableSort(reqs, (a, b) => (a.R - a.L).CompareTo(b.R - b.L));
        foreach (var (l, r, need) in reqs)
        {
            var cur = 0;
            for (var i = l; i < r; i++) cur += gaps[i];
            if (cur < need) gaps[r - 1] += need - cur;
        }

        var xs = new int[n];
        xs[0] = Half(boxW[0]);
        for (var i = 1; i < n; i++) xs[i] = xs[i - 1] + gaps[i - 1];

        var canvasW = xs[n - 1] + CeilHalf(boxW[n - 1]) + 1;
        foreach (var item in seq.Items)
        {
            if (item.Kind == MermaidSeqItemKind.Message && item.From == item.To)
            {
                canvasW = Math.Max(canvasW, xs[item.From] + 5 + ItemTextW(item.Text) + 1);
            }
            else if (item.Kind == MermaidSeqItemKind.Note)
            {
                var g = NoteGeometry(xs, item.Anchor!, MermaidWidth.StringWidth(item.Text!));
                canvasW = Math.Max(canvasW, g.X + g.W + 1);
            }
            else if (item.Kind == MermaidSeqItemKind.Divider)
            {
                canvasW = Math.Max(canvasW, MermaidWidth.StringWidth(item.Text!) + 4);
            }
        }

        var rows = new List<int>();
        var y = boxH + 1;
        foreach (var item in seq.Items)
        {
            rows.Add(y);
            y += RowHeight(item);
        }
        var bottomTop = y;
        var canvasH = bottomTop + boxH;

        if ((long)canvasW * canvasH > MaxCanvasCells) return null;

        var canvas = new MermaidCanvas(canvasW, canvasH);

        for (var i = 0; i < n; i++)
        {
            foreach (var by in new[] { 0, bottomTop })
                MermaidLayout.DrawBox(canvas, Box(Sat(xs[i], Half(boxW[i])), by, boxW[i], boxH), [labels[i]], MermaidShape.Rect);
        }
        for (var k = 0; k < seq.Items.Count; k++)
        {
            var item = seq.Items[k];
            if (item.Kind != MermaidSeqItemKind.Note) continue;
            var g = NoteGeometry(xs, item.Anchor!, MermaidWidth.StringWidth(item.Text!));
            MermaidLayout.DrawBox(canvas, Box(g.X, rows[k], g.W, 3), [item.Text!], MermaidShape.Rect);
        }

        foreach (var x in xs)
        {
            canvas.Junction(x, boxH - 1, D);
            canvas.SegV(x, boxH, bottomTop - 1);
            canvas.Junction(x, bottomTop, U);
        }

        for (var k = 0; k < seq.Items.Count; k++)
        {
            var item = seq.Items[k];
            var r = rows[k];
            if (item.Kind == MermaidSeqItemKind.Message) DrawMessage(canvas, item, xs, r);
            else if (item.Kind == MermaidSeqItemKind.Divider) DrawDivider(canvas, item.Text!, r, canvasW);
        }

        canvas.FinalizeMask();
        return canvas;
    }

    private static int RowHeight(MermaidSeqItem item)
    {
        if (item.Kind == MermaidSeqItemKind.Note) return 4;
        if (item.Kind == MermaidSeqItemKind.Divider) return 2;
        if (item.From == item.To) return 4;
        return item.Text is not null ? 3 : 2;
    }

    /// <summary>Geometry for a box drawn by position and size; ranks are irrelevant here.</summary>
    private static MermaidPlaced Box(int x, int y, int w, int h) => new(x, y, w, h, x + Half(w), y + 1, 0);

    private static void DrawMessage(MermaidCanvas canvas, MermaidSeqItem item, int[] xs, int r)
    {
        var lineCh = item.Dashed ? "╌" : "─";

        if (item.From == item.To)
        {
            // A stub that leaves the lifeline and returns two rows down.
            var x = xs[item.From];
            canvas.Junction(x, r, R);
            canvas.Set(x + 1, r, lineCh, MermaidCls.Edge);
            canvas.Set(x + 2, r, lineCh, MermaidCls.Edge);
            canvas.Set(x + 3, r, "╮", MermaidCls.Edge);
            canvas.Set(x + 3, r + 1, "│", MermaidCls.Edge);
            canvas.Set(x + 1, r + 2, item.Head == MermaidSeqHead.Cross ? "×" : "◄", MermaidCls.Edge);
            canvas.Set(x + 2, r + 2, lineCh, MermaidCls.Edge);
            canvas.Set(x + 3, r + 2, "╯", MermaidCls.Edge);
            if (item.Text is not null) DrawTextOverEdges(canvas, item.Text, x + 5, r + 1, MermaidCls.Text);
            return;
        }

        var x0 = xs[item.From];
        var x1 = xs[item.To];
        var rightward = x1 > x0;
        // A labelled message writes its text on `r` and draws the arrow below it.
        var arrowRow = item.Text is not null ? r + 1 : r;
        var lo = Math.Min(x0, x1);
        var hi = Math.Max(x0, x1);

        canvas.Junction(x0, arrowRow, rightward ? R : L);
        for (var x = lo + 1; x < hi; x++) canvas.Set(x, arrowRow, lineCh, MermaidCls.Edge);
        var headCh = item.Head == MermaidSeqHead.Cross ? "×" : rightward ? "▶" : "◄";
        canvas.Set(rightward ? x1 - 1 : x1 + 1, arrowRow, headCh, MermaidCls.Edge);

        if (item.Text is not null)
        {
            var span = hi - lo - 1;
            var t = MermaidLabels.FitLabel(item.Text, Math.Max(1, span));
            DrawTextOverEdges(canvas, t, lo + 1 + Half(Sat(span, MermaidWidth.StringWidth(t))), r, MermaidCls.Text);
        }
    }

    /// <summary>A full-width rule labelling a <c>loop</c> / <c>alt</c> / <c>opt</c> block boundary.</summary>
    private static void DrawDivider(MermaidCanvas canvas, string text, int r, int canvasW)
    {
        for (var x = 0; x < canvasW; x++) canvas.Set(x, r, "─", MermaidCls.Edge);
        DrawTextOverEdges(canvas, $" {MermaidLabels.FitLabel(text, Sat(canvasW, 4))} ", 2, r, MermaidCls.EdgeLabel);
    }
}
