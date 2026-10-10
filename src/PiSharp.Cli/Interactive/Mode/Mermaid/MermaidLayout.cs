// grok-mermaid 0.2.3 (Apache-2.0): src/layout.ts — used by Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/mermaid.ts.
using static PiSharp.Cli.Interactive.Mode.Mermaid.MermaidCanvas;
using static PiSharp.Cli.Interactive.Mode.Mermaid.MermaidJs;

namespace PiSharp.Cli.Interactive.Mode.Mermaid;

internal sealed record MermaidPlaced(int X, int Y, int W, int H, int Cx, int Cy, int Rank);

internal enum MermaidExtraKind { Plain, Frame, Compartments }

/// <summary>What to draw inside a node box.</summary>
internal sealed record MermaidNodeExtra(MermaidExtraKind Kind, MermaidCanvas? Sub = null, List<List<string>>? Sections = null)
{
    public static readonly MermaidNodeExtra Plain = new(MermaidExtraKind.Plain);
}

/// <summary>
/// Graph layout: rank, order, place, route, draw. Follows the Sugiyama outline — assign ranks along the flow axis,
/// reorder within ranks to cut crossings, then relax positions on the cross axis so chains stay straight.
/// <c>BT</c> and <c>RL</c> reuse the <c>TD</c>/<c>LR</c> layouts and flip the finished canvas.
/// </summary>
internal static class MermaidLayout
{
    /// <summary>Cells of padding between a box border and its text.</summary>
    private const int Pad = 1;
    /// <summary>Minimum horizontal / vertical space between boxes.</summary>
    private const int GapX = 3;
    private const int GapY = 2;
    /// <summary>Refuse to allocate a canvas larger than this many cells.</summary>
    private const int MaxCanvasCells = 1 << 21;

    private sealed record NodeSizes(int[] BoxW, int[] BoxH, int[] LayW, int[] LayH, int[] ExtraH, int[] SelfLabelW);

    private sealed record RoutePlan(int CanvasW, int CanvasH, int[] BandEnd, int[] EdgeBus, int LaneBase, int[] EdgeLane);

    private static List<int>[] Lists(int n)
    {
        var lists = new List<int>[n];
        for (var i = 0; i < n; i++) lists[i] = [];
        return lists;
    }

    // ------------------------------------------------------------------ ranking

    /// <summary>Longest-path ranking over the graph's DAG; back edges are excluded by a DFS colouring pass.</summary>
    public static int[] ComputeRanks(MermaidGraph graph)
    {
        var n = graph.Nodes.Count;
        var children = Lists(n);
        var indeg = new int[n];
        foreach (var e in graph.Edges)
        {
            if (e.From != e.To)
            {
                children[e.From].Add(e.To);
                indeg[e.To]++;
            }
        }

        var color = new byte[n];
        var dag = Lists(n);
        var order = new List<int>();

        // Roots first so ranks grow from natural entry points, then any leftovers.
        var starts = Enumerable.Range(0, n).Where(i => indeg[i] == 0).Concat(Enumerable.Range(0, n));
        foreach (var start in starts)
        {
            if (color[start] == 0) DfsDag(start, children, color, dag, order);
        }

        var rank = new int[n];
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var u = order[i];
            foreach (var v in dag[u]) rank[v] = Math.Max(rank[v], rank[u] + 1);
        }
        return rank;
    }

    /// <summary>Iterative DFS recording postorder and skipping edges back into the stack.</summary>
    private static void DfsDag(int start, List<int>[] children, byte[] color, List<int>[] dag, List<int> order)
    {
        var stack = new List<int[]> { new[] { start, 0 } };
        color[start] = 1;
        while (stack.Count > 0)
        {
            var frame = stack[^1];
            var u = frame[0];
            if (frame[1] < children[u].Count)
            {
                var v = children[u][frame[1]];
                frame[1]++;
                if (color[v] == 1) continue; // grey: a back edge, ignore it
                dag[u].Add(v);
                if (color[v] == 0)
                {
                    color[v] = 1;
                    stack.Add([v, 0]);
                }
            }
            else
            {
                color[u] = 2;
                order.Add(u);
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    /// <summary>Reorder nodes within each rank to minimise edge crossings (barycenter sweeps).</summary>
    public static void OrderRanks(List<List<int>> byRank, List<MermaidEdge> edges, int[] ranks)
    {
        var n = ranks.Length;
        if (byRank.Count < 2 || n < 3) return;

        var parents = Lists(n);
        var children = Lists(n);
        foreach (var e in edges)
        {
            if (e.From != e.To && ranks[e.To] > ranks[e.From])
            {
                parents[e.To].Add(e.From);
                children[e.From].Add(e.To);
            }
        }

        var pos = new int[n];
        void Reindex(List<int> row)
        {
            for (var i = 0; i < row.Count; i++) pos[row[i]] = i;
        }
        foreach (var row in byRank) Reindex(row);

        var best = byRank.Select(row => row.ToList()).ToList();
        var bestCrossings = CountCrossings(edges, ranks, pos);
        if (bestCrossings == 0) return;

        for (var it = 0; it < 8; it++)
        {
            // Alternate sweeping down (sort by parents) and up (sort by children).
            var rows = it % 2 == 0 ? byRank.Skip(1).ToList() : byRank.Take(byRank.Count - 1).Reverse().ToList();
            var neigh = it % 2 == 0 ? parents : children;
            foreach (var row in rows)
            {
                SortByBarycenter(row, neigh, pos);
                Reindex(row);
            }
            var crossings = CountCrossings(edges, ranks, pos);
            if (crossings < bestCrossings)
            {
                bestCrossings = crossings;
                best = byRank.Select(row => row.ToList()).ToList();
            }
            if (bestCrossings == 0) break;
        }

        for (var i = 0; i < byRank.Count; i++)
        {
            byRank[i].Clear();
            byRank[i].AddRange(best[i]);
        }
    }

    private static void SortByBarycenter(List<int> row, List<int>[] neigh, int[] pos)
    {
        var keyed = row.Select(v => (Key: neigh[v].Count == 0 ? pos[v] : Mean(neigh[v], u => pos[u]), V: v)).ToList();
        StableSort(keyed, (a, b) => a.Key.CompareTo(b.Key));
        for (var i = 0; i < keyed.Count; i++) row[i] = keyed[i].V;
    }

    /// <summary><c>neigh.reduce((s, u) =&gt; s + f(u), 0) / neigh.length</c>, summed in order as doubles.</summary>
    private static double Mean(List<int> neigh, Func<int, double> f)
    {
        var s = 0.0;
        foreach (var u in neigh) s += f(u);
        return s / neigh.Count;
    }

    public static int CountCrossings(List<MermaidEdge> edges, int[] ranks, int[] pos)
    {
        var adjacent = edges.Where(e => e.From != e.To && ranks[e.To] == ranks[e.From] + 1)
            .Select(e => (R: ranks[e.From], F: pos[e.From], T: pos[e.To])).ToList();
        var crossings = 0;
        for (var i = 0; i < adjacent.Count; i++)
        {
            var a = adjacent[i];
            for (var j = i + 1; j < adjacent.Count; j++)
            {
                var b = adjacent[j];
                if (a.R == b.R && (a.F < b.F && a.T > b.T || a.F > b.F && a.T < b.T)) crossings++;
            }
        }
        return crossings;
    }

    /// <summary>Assign a cross-axis centre to every node so nodes line up under their neighbours.</summary>
    public static int[] AssignPositions(List<List<int>> byRank, int[] size, int sep, List<MermaidEdge> edges, int[] ranks)
    {
        var n = size.Length;
        var parents = Lists(n);
        var children = Lists(n);
        foreach (var e in edges)
        {
            if (e.From != e.To && ranks[e.To] > ranks[e.From])
            {
                parents[e.To].Add(e.From);
                children[e.From].Add(e.To);
            }
        }

        var pos = new double[n];
        foreach (var row in byRank)
        {
            var x = 0.0;
            foreach (var v in row)
            {
                var h = size[v] / 2.0;
                x += h;
                pos[v] = x;
                x += h + sep;
            }
        }

        for (var it = 0; it < 10; it++)
        {
            var rows = it % 2 == 0 ? byRank : Enumerable.Reverse(byRank).ToList();
            var neigh = it % 2 == 0 ? parents : children;
            foreach (var row in rows) RelaxRank(row, neigh, pos, size, sep);
        }

        var minLeft = double.PositiveInfinity;
        for (var v = 0; v < n; v++) minLeft = Math.Min(minLeft, pos[v] - size[v] / 2.0);
        if (!double.IsFinite(minLeft)) minLeft = 0;
        var result = new int[n];
        for (var v = 0; v < n; v++) result[v] = (int)Math.Max(0, Round(pos[v] - minLeft));
        return result;
    }

    private static void RelaxRank(List<int> nodes, List<int>[] neigh, double[] pos, int[] size, int sep)
    {
        var n = nodes.Count;
        if (n == 0) return;

        var desired = nodes.Select(v => neigh[v].Count == 0 ? pos[v] : Mean(neigh[v], u => pos[u])).ToArray();
        double HalfOf(int i) => size[nodes[i]] / 2.0;

        // Sweep right then left, then take the midpoint.
        var left = new double[n];
        for (var i = 0; i < n; i++)
            left[i] = i == 0 ? desired[i] : Math.Max(desired[i], left[i - 1] + HalfOf(i - 1) + sep + HalfOf(i));
        var right = new double[n];
        for (var i = n - 1; i >= 0; i--)
            right[i] = i == n - 1 ? desired[i] : Math.Min(desired[i], right[i + 1] - HalfOf(i + 1) - sep - HalfOf(i));
        for (var i = 0; i < n; i++) pos[nodes[i]] = (left[i] + right[i]) / 2;
        for (var i = 1; i < n; i++)
        {
            var minP = pos[nodes[i - 1]] + HalfOf(i - 1) + sep + HalfOf(i);
            if (pos[nodes[i]] < minP) pos[nodes[i]] = minP;
        }
    }

    // ------------------------------------------------------------------- tracks

    /// <summary>Pack <c>[start, end, from, to, edgeIndex]</c> spans into as few parallel tracks as possible.</summary>
    public static (List<(int Idx, int Slot)> Assigned, int Count) AssignTracks(List<int[]> spans)
    {
        var sorted = spans.ToList();
        StableSort(sorted, (a, b) =>
        {
            for (var i = 0; i < 5; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return 0;
        });
        var tracks = new List<List<int[]>>();
        var assigned = new List<(int, int)>();
        foreach (var span in sorted)
        {
            int s = span[0], e = span[1], f = span[2], t = span[3], idx = span[4];
            var slot = tracks.FindIndex(members => members.All(m => m[1] + 2 <= s || e + 2 <= m[0] || m[2] == f || m[3] == t));
            if (slot == -1)
            {
                tracks.Add([]);
                slot = tracks.Count - 1;
            }
            tracks[slot].Add([s, e, f, t]);
            assigned.Add((idx, slot));
        }
        return (assigned, tracks.Count);
    }

    /// <summary>Edges from rank <paramref name="r"/> to <c>r + 1</c> that must jog sideways, so need a bus row.</summary>
    private static List<int[]> BusSpans(MermaidGraph graph, int[] ranks, int[] centers, int r, bool exact)
    {
        var spans = new List<int[]>();
        for (var i = 0; i < graph.Edges.Count; i++)
        {
            var e = graph.Edges[i];
            var jogs = exact ? centers[e.From] != centers[e.To] : Math.Abs(centers[e.From] - centers[e.To]) > 1;
            if (e.From != e.To && ranks[e.From] == r && ranks[e.To] == r + 1 && jogs)
                spans.Add([Math.Min(centers[e.From], centers[e.To]), Math.Max(centers[e.From], centers[e.To]), e.From, e.To, i]);
        }
        return spans;
    }

    /// <summary>Edges skipping a rank or running backwards; these go around in a lane.</summary>
    private static List<int[]> LaneSpans(MermaidGraph graph, int[] ranks, MermaidPlaced[] placed, bool vertical)
    {
        var spans = new List<int[]>();
        for (var i = 0; i < graph.Edges.Count; i++)
        {
            var e = graph.Edges[i];
            if (e.From == e.To || ranks[e.To] == ranks[e.From] + 1) continue;
            var pf = placed[e.From];
            var pt = placed[e.To];
            var a = vertical ? Math.Min(pf.Cy, pt.Cy) : Math.Min(pf.Cx, pt.Cx);
            var b = vertical ? Math.Max(pf.Cy, pt.Cy) : Math.Max(pf.Cx, pt.Cx);
            spans.Add([a, b, e.From, e.To, i]);
        }
        return spans;
    }

    // ----------------------------------------------------------------- placement

    private static RoutePlan PlaceTd(int[] ranks, int maxRank, List<List<int>> byRank, NodeSizes sizes, MermaidGraph graph, MermaidPlaced[] placed)
    {
        var centers = AssignPositions(byRank, sizes.LayW, GapX, graph.Edges, ranks);

        var edgeBus = new int[graph.Edges.Count];
        var busTracks = new int[maxRank + 1];
        for (var r = 0; r < maxRank; r++)
        {
            var spans = BusSpans(graph, ranks, centers, r, false);
            if (spans.Count == 0) continue;
            var (assigned, count) = AssignTracks(spans);
            foreach (var (idx, slot) in assigned) edgeBus[idx] = slot;
            busTracks[r] = count;
        }

        var rankH = byRank.Select(row => row.Count == 0 ? 3 : row.Max(i => sizes.BoxH[i] + sizes.ExtraH[i])).ToArray();
        var rankY = new int[maxRank + 1];
        for (var r = 1; r <= maxRank; r++) rankY[r] = rankY[r - 1] + rankH[r - 1] + Math.Max(GapY, busTracks[r - 1] + 1);
        var canvasH = rankY[maxRank] + rankH[maxRank];
        var bandEnd = Enumerable.Range(0, maxRank + 1).Select(r => rankY[r] + rankH[r]).ToArray();

        var diagramW = 1;
        for (var r = 0; r < byRank.Count; r++)
        {
            foreach (var idx in byRank[r])
            {
                var w = sizes.BoxW[idx];
                var h = sizes.BoxH[idx];
                var cx = centers[idx];
                var x = Sat(cx, Half(w));
                var y = rankY[r] + Half(rankH[r] - h - sizes.ExtraH[idx]);
                placed[idx] = new(x, y, w, h, cx, y + Half(h), r);
                diagramW = Math.Max(diagramW, x + w);
                if (sizes.ExtraH[idx] > 0 && sizes.SelfLabelW[idx] > 0) diagramW = Math.Max(diagramW, x + w + 2 + sizes.SelfLabelW[idx]);
            }
        }

        var contentW = diagramW;
        foreach (var e in graph.Edges)
        {
            if (e.From == e.To || e.Label is null) continue;
            var lw = Math.Min(MermaidWidth.StringWidth(e.Label), MermaidLabels.MaxLabel);
            contentW = ranks[e.To] == ranks[e.From] + 1 ? Math.Max(contentW, placed[e.To].Cx + 2 + lw) : Math.Max(contentW, diagramW + lw + 1);
        }

        var edgeLane = new int[graph.Edges.Count];
        var lanes = LaneSpans(graph, ranks, placed, true);
        var canvasW = contentW;
        var laneBase = 0;
        if (lanes.Count > 0)
        {
            var (assigned, count) = AssignTracks(lanes);
            foreach (var (idx, slot) in assigned) edgeLane[idx] = slot;
            canvasW = contentW + 1 + count;
            laneBase = contentW + 1;
        }

        return new(canvasW, canvasH, bandEnd, edgeBus, laneBase, edgeLane);
    }

    private static RoutePlan PlaceLr(int[] ranks, int maxRank, List<List<int>> byRank, NodeSizes sizes, MermaidGraph graph, MermaidPlaced[] placed)
    {
        var colW = byRank.Select(row => row.Count == 0 ? 0 : row.Max(i => sizes.BoxW[i])).ToArray();

        // Left-to-right edge labels sit in the gap between columns, so the gap has to be wide enough for the widest.
        var labelWidths = graph.Edges
            .Where(e => e.From == e.To || ranks[e.To] == ranks[e.From] + 1)
            .Where(e => e.Label is not null)
            .Select(e => Math.Min(MermaidWidth.StringWidth(e.Label!), MermaidLabels.MaxLabel))
            .ToList();
        var maxLabel = labelWidths.Count == 0 ? 0 : labelWidths.Max();
        var baseGap = Math.Max(GapX + 1, maxLabel + 3);

        var centers = AssignPositions(byRank, sizes.LayH, 1, graph.Edges, ranks);

        var edgeBus = new int[graph.Edges.Count];
        var busTracks = new int[maxRank + 1];
        for (var r = 0; r < maxRank; r++)
        {
            var spans = BusSpans(graph, ranks, centers, r, true);
            if (spans.Count == 0) continue;
            var (assigned, count) = AssignTracks(spans);
            foreach (var (idx, slot) in assigned) edgeBus[idx] = slot;
            busTracks[r] = count;
        }

        var rankX = new int[maxRank + 1];
        for (var r = 1; r <= maxRank; r++) rankX[r] = rankX[r - 1] + colW[r - 1] + Math.Max(baseGap, busTracks[r - 1] + 1);
        var selfTails = byRank[maxRank].Where(i => sizes.ExtraH[i] > 0 && sizes.SelfLabelW[i] > 0).Select(i => 2 + sizes.SelfLabelW[i]).ToList();
        var canvasW = rankX[maxRank] + colW[maxRank] + (selfTails.Count == 0 ? 0 : selfTails.Max());
        var bandEnd = Enumerable.Range(0, maxRank + 1).Select(r => rankX[r] + colW[r]).ToArray();

        var diagramH = 1;
        for (var r = 0; r < byRank.Count; r++)
        {
            var x = rankX[r];
            foreach (var idx in byRank[r])
            {
                var w = sizes.BoxW[idx];
                var h = sizes.BoxH[idx];
                var cy = centers[idx];
                var y = Sat(cy, Half(h + sizes.ExtraH[idx]));
                placed[idx] = new(x, y, w, h, x + Half(w), y + Half(h), r);
                diagramH = Math.Max(diagramH, y + h + sizes.ExtraH[idx]);
            }
        }

        var edgeLane = new int[graph.Edges.Count];
        var lanes = LaneSpans(graph, ranks, placed, false);
        var canvasH = diagramH;
        var laneBase = 0;
        if (lanes.Count > 0)
        {
            var (assigned, count) = AssignTracks(lanes);
            foreach (var (idx, slot) in assigned) edgeLane[idx] = slot;
            canvasH = diagramH + 1 + count;
            laneBase = diagramH + 1;
        }

        return new(canvasW, canvasH, bandEnd, edgeBus, laneBase, edgeLane);
    }

    // -------------------------------------------------------------------- canvas

    /// <summary>Rank, place, draw and route a graph onto a fresh canvas; <c>null</c> when empty or over the cell cap.</summary>
    public static MermaidCanvas? LayoutCanvas(MermaidGraph graph, List<MermaidNodeExtra> extras)
    {
        var n = graph.Nodes.Count;
        if (n == 0) return null;

        var ranks = ComputeRanks(graph);
        var maxRank = Math.Max(ranks.Max(), 0);

        var byRank = Enumerable.Range(0, maxRank + 1).Select(_ => new List<int>()).ToList();
        for (var idx = 0; idx < ranks.Length; idx++) byRank[ranks[idx]].Add(idx);
        OrderRanks(byRank, graph.Edges, ranks);

        var wrapped = graph.Nodes.Select(node => MermaidLabels.WrapLabel(node.Label, MermaidLabels.WrapWidth, MermaidLabels.MaxLines)).ToList();
        static int Widest(IReadOnlyCollection<string> lines) => Math.Max(1, lines.Count == 0 ? 1 : lines.Max(MermaidWidth.StringWidth));

        var boxW = extras.Select((extra, i) => extra.Kind switch
        {
            MermaidExtraKind.Frame => Math.Max(extra.Sub!.W + 2, MermaidWidth.StringWidth(MermaidLabels.FitLabel(graph.Nodes[i].Label, MermaidLabels.WrapWidth)) + 4),
            MermaidExtraKind.Compartments => Widest(extra.Sections!.SelectMany(s => s).ToList()) + 2 * Pad + 2,
            _ => Widest(wrapped[i]) + 2 * Pad + 2,
        }).ToArray();
        var boxH = extras.Select((extra, i) => extra.Kind switch
        {
            MermaidExtraKind.Frame => extra.Sub!.H + 2,
            MermaidExtraKind.Compartments => extra.Sections!.Sum(sec => sec.Count) + Sat(extra.Sections!.Count(s => s.Count > 0), 1) + 2,
            _ => wrapped[i].Count + 2,
        }).ToArray();

        // A self-edge needs two rows below its box, and room beside it for a label.
        var extraH = new int[n];
        var selfLabelW = new int[n];
        foreach (var e in graph.Edges)
        {
            if (e.From != e.To) continue;
            extraH[e.From] = 2;
            if (e.Label is not null) selfLabelW[e.From] = Math.Max(selfLabelW[e.From], Math.Min(MermaidWidth.StringWidth(e.Label), MermaidLabels.MaxLabel));
        }
        for (var i = 0; i < n; i++) if (extraH[i] > 0) boxW[i] = Math.Max(boxW[i], 7);

        var sizes = new NodeSizes(
            boxW,
            boxH,
            boxW.Select((w, i) => w + (selfLabelW[i] > 0 ? 2 * (selfLabelW[i] + 3) : 0)).ToArray(),
            boxH.Select((h, i) => h + extraH[i]).ToArray(),
            extraH,
            selfLabelW);

        var placed = Enumerable.Range(0, n).Select(_ => new MermaidPlaced(0, 0, 0, 0, 0, 0, 0)).ToArray();

        var vertical = graph.Dir is MermaidDir.Down or MermaidDir.Up;
        var plan = vertical ? PlaceTd(ranks, maxRank, byRank, sizes, graph, placed) : PlaceLr(ranks, maxRank, byRank, sizes, graph, placed);

        if ((long)plan.CanvasW * plan.CanvasH > MaxCanvasCells) return null;

        var canvas = new MermaidCanvas(plan.CanvasW, plan.CanvasH);
        for (var idx = 0; idx < n; idx++)
        {
            var extra = extras[idx];
            if (extra.Kind == MermaidExtraKind.Frame) DrawFrame(canvas, placed[idx], graph.Nodes[idx].Label, extra.Sub!);
            else if (extra.Kind == MermaidExtraKind.Compartments) DrawClassBox(canvas, placed[idx], extra.Sections!);
            else DrawBox(canvas, placed[idx], wrapped[idx], graph.Nodes[idx].Shape);
        }

        for (var i = 0; i < graph.Edges.Count; i++)
        {
            var edge = graph.Edges[i];
            canvas.CurStyle = edge.Line == MermaidLineKind.Dotted ? StyDot : edge.Line == MermaidLineKind.Thick ? StyThick : StySolid;
            if (edge.From == edge.To)
            {
                RouteSelf(canvas, placed[edge.From], edge);
                continue;
            }
            var from = placed[edge.From];
            var to = placed[edge.To];
            var adjacent = to.Rank == from.Rank + 1;
            var bus = plan.BandEnd[from.Rank] + plan.EdgeBus[i];
            var lane = plan.LaneBase + plan.EdgeLane[i];
            if (vertical)
            {
                if (adjacent) RouteForward(canvas, from, to, edge, bus);
                else RouteBack(canvas, from, to, edge, lane);
            }
            else if (adjacent)
            {
                RouteForwardLr(canvas, from, to, edge, bus);
            }
            else
            {
                RouteBackLr(canvas, from, to, edge, lane);
            }
        }

        canvas.FinalizeMask();
        return canvas;
    }

    /// <summary>Apply the direction flip a finished canvas needs for <c>BT</c> / <c>RL</c>.</summary>
    public static MermaidCanvas Orient(MermaidCanvas canvas, MermaidGraph graph)
    {
        if (graph.Dir == MermaidDir.Up) canvas.FlipVertical();
        else if (graph.Dir == MermaidDir.Left) canvas.FlipHorizontal();
        return canvas;
    }

    /// <summary>Flowchart and state diagrams: plain boxes, no extra content.</summary>
    public static MermaidCanvas? LayoutFlowchart(MermaidGraph graph)
    {
        var extras = graph.Nodes.Select(_ => MermaidNodeExtra.Plain).ToList();
        var canvas = LayoutCanvas(graph, extras);
        return canvas is null ? null : Orient(canvas, graph);
    }

    /// <summary>Class and ER diagrams: boxes divided into title / attribute / method rows.</summary>
    public static MermaidCanvas? LayoutClass(MermaidGraph graph, List<MermaidClassInfo> infos)
    {
        var extras = graph.Nodes.Select((node, i) =>
        {
            var title = new List<string>();
            if (infos[i].Annotation is not null) title.Add($"«{infos[i].Annotation}»");
            title.Add(DisplayGenerics(node.Label));
            return new MermaidNodeExtra(MermaidExtraKind.Compartments, Sections: [title, infos[i].Attrs, infos[i].Methods]);
        }).ToList();
        var canvas = LayoutCanvas(graph, extras);
        return canvas is null ? null : Orient(canvas, graph);
    }

    private static string DisplayGenerics(string s)
    {
        var sb = new System.Text.StringBuilder();
        var open = false;
        foreach (var c in MermaidJs.CodePoints(s))
        {
            if (c == "~")
            {
                sb.Append(open ? '>' : '<');
                open = !open;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // -------------------------------------------------------------------- groups

    private static string NodeKey(int i) => $"n{i}";
    private static string GroupKey(int i) => $"g{i}";

    /// <summary>Lay out a flowchart that uses <c>subgraph</c>: each subgraph becomes a framed box holding its own canvas.</summary>
    public static MermaidCanvas? LayoutGrouped(MermaidGraph graph)
    {
        // A node whose id matches a subgraph id stands in for that subgraph.
        var proxy = new Dictionary<int, int>();
        for (var gi = 0; gi < graph.Groups.Count; gi++)
        {
            if (graph.Index.TryGetValue(graph.Groups[gi].Id, out var ni)) proxy[ni] = gi;
        }

        List<int> GroupChain(int g)
        {
            var chain = new List<int>();
            var cur = g;
            while (cur != -1)
            {
                chain.Add(cur);
                cur = graph.Groups[cur].Parent;
            }
            chain.Reverse();
            return chain;
        }
        (string Key, List<int> Chain) Endpoint(int n) =>
            proxy.TryGetValue(n, out var gi) ? (GroupKey(gi), GroupChain(graph.Groups[gi].Parent)) : (NodeKey(n), GroupChain(graph.NodeGroup[n]));

        // Edges bucketed by the scope that draws them; -1 is the top level.
        var scopeEdges = new Dictionary<int, List<(string F, string T, int Ei)>>();
        var referenced = new bool[graph.Groups.Count];
        for (var ei = 0; ei < graph.Edges.Count; ei++)
        {
            var e = graph.Edges[ei];
            var f = Endpoint(e.From);
            var t = Endpoint(e.To);
            var k = 0;
            while (k < f.Chain.Count && k < t.Chain.Count && f.Chain[k] == t.Chain[k]) k++;
            var scope = k == 0 ? -1 : f.Chain[k - 1];
            var fKey = f.Chain.Count > k ? GroupKey(f.Chain[k]) : f.Key;
            var tKey = t.Chain.Count > k ? GroupKey(t.Chain[k]) : t.Key;
            foreach (var key in new[] { fKey, tKey })
            {
                if (key.StartsWith('g')) referenced[int.Parse(key[1..], System.Globalization.CultureInfo.InvariantCulture)] = true;
            }
            if (scopeEdges.TryGetValue(scope, out var list)) list.Add((fKey, tKey, ei));
            else scopeEdges[scope] = [(fKey, tKey, ei)];
        }

        var directNodes = new Dictionary<int, List<int>>();
        for (var ni = 0; ni < graph.NodeGroup.Count; ni++)
        {
            if (proxy.ContainsKey(ni)) continue;
            var g = graph.NodeGroup[ni];
            if (directNodes.TryGetValue(g, out var list)) list.Add(ni);
            else directNodes[g] = [ni];
        }

        // Drop empty subgraphs, but keep any that an edge attaches to.
        var keep = new bool[graph.Groups.Count];
        for (var gi = graph.Groups.Count - 1; gi >= 0; gi--)
        {
            var hasNodes = directNodes.TryGetValue(gi, out var direct) && direct.Count > 0;
            var hasChildren = false;
            for (var c = 0; c < graph.Groups.Count; c++) if (graph.Groups[c].Parent == gi && keep[c]) { hasChildren = true; break; }
            keep[gi] = hasNodes || hasChildren || referenced[gi];
        }

        var canvas = BuildScope(graph, -1, scopeEdges, directNodes, keep);
        return canvas is null ? null : Orient(canvas, graph);
    }

    private static MermaidCanvas? BuildScope(MermaidGraph graph, int scope, Dictionary<int, List<(string F, string T, int Ei)>> scopeEdges, Dictionary<int, List<int>> directNodes, bool[] keep)
    {
        var items = (directNodes.TryGetValue(scope, out var direct) ? direct : []).Select(NodeKey).ToList();
        for (var gi = 0; gi < graph.Groups.Count; gi++) if (graph.Groups[gi].Parent == scope && keep[gi]) items.Add(GroupKey(gi));

        if (items.Count == 0) return new MermaidCanvas(1, 1);

        var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodes = new List<MermaidNode>();
        var extras = new List<MermaidNodeExtra>();
        foreach (var item in items)
        {
            indexOf[item] = nodes.Count;
            var i = int.Parse(item[1..], System.Globalization.CultureInfo.InvariantCulture);
            if (item.StartsWith('n'))
            {
                nodes.Add(new(graph.Nodes[i].Label, graph.Nodes[i].Shape));
                extras.Add(MermaidNodeExtra.Plain);
            }
            else
            {
                var sub = BuildScope(graph, i, scopeEdges, directNodes, keep);
                if (sub is null) return null;
                nodes.Add(new(graph.Groups[i].Label, MermaidShape.Rect));
                extras.Add(new(MermaidExtraKind.Frame, Sub: sub));
            }
        }

        var edges = new List<MermaidEdge>();
        foreach (var (f, t, ei) in scopeEdges.TryGetValue(scope, out var list) ? list : [])
        {
            if (!indexOf.TryGetValue(f, out var fi) || !indexOf.TryGetValue(t, out var ti)) continue;
            var e = graph.Edges[ei];
            edges.Add(e with { From = fi, To = ti });
        }

        // Layout only reads nodes/edges/dir, so a bare graph carrying those is enough.
        var synth = new MermaidGraph(graph.Dir) { Nodes = nodes, Edges = edges };
        return LayoutCanvas(synth, extras);
    }

    // ------------------------------------------------------------------- drawing

    public static void DrawBox(MermaidCanvas canvas, MermaidPlaced p, List<string> lines, MermaidShape shape)
    {
        var (x, y, w, h) = (p.X, p.Y, p.W, p.H);
        var right = x + w - 1;
        var bottom = y + h - 1;

        var rounded = shape is MermaidShape.Round or MermaidShape.Diamond;
        canvas.Set(x, y, rounded ? "╭" : "┌", MermaidCls.Border);
        canvas.Set(right, y, rounded ? "╮" : "┐", MermaidCls.Border);
        canvas.Set(x, bottom, rounded ? "╰" : "└", MermaidCls.Border);
        canvas.Set(right, bottom, rounded ? "╯" : "┘", MermaidCls.Border);

        // The perimeter is drawn as bits so edges can tee into it, but it claims `border` rather than `edge`.
        for (var cx = x + 1; cx < right; cx++)
        {
            canvas.AddBits(cx, y, L | R, MermaidCls.Border);
            canvas.AddBits(cx, bottom, L | R, MermaidCls.Border);
        }
        for (var cy = y + 1; cy < bottom; cy++)
        {
            canvas.AddBits(x, cy, U | D, MermaidCls.Border);
            canvas.AddBits(right, cy, U | D, MermaidCls.Border);
        }

        for (var cy = y; cy <= bottom; cy++)
        {
            for (var cx = x; cx <= right; cx++)
            {
                var i = canvas.Idx(cx, cy);
                if (canvas.InGrid(i)) canvas.Occupied[i] = 1;
            }
        }

        var inner = Math.Max(1, Sat(w, 2 * Pad + 2));
        for (var li = 0; li < lines.Count; li++)
        {
            var text = MermaidLabels.FitLabel(lines[li], inner);
            var textX = x + 1 + Pad + Half(Sat(inner, MermaidWidth.StringWidth(text)));
            DrawText(canvas, text, textX, y + 1 + li, MermaidCls.Text);
        }
    }

    /// <summary>A class or ER box: sections separated by horizontal rules, title centred.</summary>
    private static void DrawClassBox(MermaidCanvas canvas, MermaidPlaced p, List<List<string>> sections)
    {
        DrawBox(canvas, p, [], MermaidShape.Rect);
        var inner = Math.Max(1, Sat(p.W, 2 * Pad + 2));
        var row = p.Y + 1;
        var first = true;
        for (var si = 0; si < sections.Count; si++)
        {
            var section = sections[si];
            if (section.Count == 0) continue;
            if (!first)
            {
                canvas.Set(p.X, row, "├", MermaidCls.Border);
                for (var x = p.X + 1; x < p.X + p.W - 1; x++) canvas.Set(x, row, "─", MermaidCls.Border);
                canvas.Set(p.X + p.W - 1, row, "┤", MermaidCls.Border);
                row++;
            }
            first = false;
            foreach (var line in section)
            {
                var text = MermaidLabels.FitLabel(line, inner);
                var tx = si == 0 ? p.X + 1 + Pad + Half(Sat(inner, MermaidWidth.StringWidth(text))) : p.X + 1 + Pad;
                DrawTextOverEdges(canvas, text, tx, row, MermaidCls.Text);
                row++;
            }
        }
    }

    /// <summary>A subgraph frame: a titled box with a finished sub-canvas centred inside.</summary>
    private static void DrawFrame(MermaidCanvas canvas, MermaidPlaced p, string title, MermaidCanvas sub)
    {
        DrawBox(canvas, p, [], MermaidShape.Rect);
        var t = MermaidLabels.FitLabel(title, Sat(p.W, 4));
        DrawTextOverEdges(canvas, $" {t} ", p.X + 1, p.Y, MermaidCls.Text);
        canvas.Blit(sub, p.X + 1 + Half(p.W - 2 - sub.W), p.Y + 1 + Half(p.H - 2 - sub.H));
    }

    // ------------------------------------------------------------------- routing

    private static string HeadGlyph(MermaidHead head, string arrow) => head switch
    {
        MermaidHead.Circle => "o",
        MermaidHead.Cross => "×",
        MermaidHead.DiamondFill => "◆",
        MermaidHead.DiamondOpen => "◇",
        MermaidHead.Triangle => arrow switch { "▼" => "▽", "▲" => "△", "◄" => "◁", "▶" => "▷", _ => arrow },
        _ => arrow,
    };

    /// <summary>Adjacent ranks, top-down: drop, jog along the bus row, drop into the head.</summary>
    private static void RouteForward(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge, int bus)
    {
        var tx = to.Cx;
        // A jog of one column reads as a kink; snap straight instead.
        var bx = Math.Abs(from.Cx - tx) <= 1 ? tx : from.Cx;
        var by = from.Y + from.H - 1;
        var headRow = to.Y - 1;

        canvas.Junction(bx, by, D);
        canvas.SegV(bx, by, bus);
        if (bx == tx)
        {
            canvas.SegV(bx, bus, headRow);
        }
        else
        {
            canvas.SegH(bus, bx, tx);
            canvas.SegV(tx, bus, headRow);
        }

        if (edge.HeadTo == MermaidHead.None) canvas.AddBits(tx, headRow, U);
        else canvas.Set(tx, headRow, HeadGlyph(edge.HeadTo, "▼"), MermaidCls.Edge);
        if (edge.HeadFrom != MermaidHead.None) canvas.Set(bx, by, HeadGlyph(edge.HeadFrom, "▲"), MermaidCls.Edge);

        if (edge.Label is not null) PlaceLabel(canvas, edge.Label, headRow, tx + 1);
    }

    /// <summary>A self-edge: a stub loop hanging below the box.</summary>
    private static void RouteSelf(MermaidCanvas canvas, MermaidPlaced p, MermaidEdge edge)
    {
        var bottom = p.Y + p.H - 1;
        var exitX = p.Cx + 1;
        var retX = p.X + p.W - 2;
        if (retX <= exitX || bottom + 2 >= canvas.H) return;

        var (v, h, bl, br) = edge.Line switch
        {
            MermaidLineKind.Dotted => ("╎", "╌", "╰", "╯"),
            MermaidLineKind.Thick => ("┃", "━", "┗", "┛"),
            _ => ("│", "─", "╰", "╯"),
        };

        canvas.Junction(exitX, bottom, D);
        canvas.Set(exitX, bottom + 1, v, MermaidCls.Edge);
        canvas.Set(exitX, bottom + 2, bl, MermaidCls.Edge);
        for (var x = exitX + 1; x < retX; x++) canvas.Set(x, bottom + 2, h, MermaidCls.Edge);
        canvas.Set(retX, bottom + 2, br, MermaidCls.Edge);
        canvas.Set(retX, bottom + 1, HeadGlyph(edge.HeadTo, "▲"), MermaidCls.Edge);
        if (edge.Label is not null) PlaceLabel(canvas, edge.Label, bottom + 1, p.X + p.W + 1);
    }

    /// <summary>Skip or back edge, top-down: out the right side, up a lane, back in.</summary>
    private static void RouteBack(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge, int laneX)
    {
        var sx = from.X + from.W - 1;
        var sy = from.Cy;
        var tx = to.X + to.W - 1;
        var tyc = to.Cy;

        canvas.Junction(sx, sy, R);
        canvas.SegH(sy, sx, laneX);
        canvas.SegV(laneX, sy, tyc);
        canvas.SegH(tyc, tx + 1, laneX);

        if (edge.HeadTo == MermaidHead.None) canvas.AddBits(tx + 1, tyc, R);
        else canvas.Set(tx + 1, tyc, HeadGlyph(edge.HeadTo, "◄"), MermaidCls.Edge);
        if (edge.HeadFrom != MermaidHead.None) canvas.Set(sx, sy, HeadGlyph(edge.HeadFrom, "◄"), MermaidCls.Edge);

        if (edge.Label is not null) PlaceLabel(canvas, edge.Label, Sat(tyc, 1), Sat(laneX, MermaidWidth.StringWidth(edge.Label) + 1));
    }

    /// <summary>Adjacent ranks, left-to-right: out the right side, jog on the bus column.</summary>
    private static void RouteForwardLr(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge, int bus)
    {
        var rx = from.X + from.W - 1;
        var ry = from.Cy;
        var ly = to.Cy;
        var headCol = to.X - 1;

        canvas.Junction(rx, ry, R);
        canvas.SegH(ry, rx, bus);
        if (ry == ly)
        {
            canvas.SegH(ry, bus, headCol);
        }
        else
        {
            canvas.SegV(bus, ry, ly);
            canvas.SegH(ly, bus, headCol);
        }

        if (edge.HeadTo == MermaidHead.None) canvas.AddBits(headCol, ly, R);
        else canvas.Set(headCol, ly, HeadGlyph(edge.HeadTo, "▶"), MermaidCls.Edge);
        if (edge.HeadFrom != MermaidHead.None) canvas.Set(rx, ry, HeadGlyph(edge.HeadFrom, "◄"), MermaidCls.Edge);

        if (edge.Label is not null) PlaceLabel(canvas, edge.Label, Sat(ly, 1), bus + 1);
    }

    /// <summary>Skip or back edge, left-to-right: down out the bottom, along a lane, back up.</summary>
    private static void RouteBackLr(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge, int laneY)
    {
        var sx = from.Cx;
        var sy = from.Y + from.H - 1;
        var tx = to.Cx;
        var ty = to.Y + to.H - 1;

        canvas.Junction(sx, sy, D);
        canvas.SegV(sx, sy, laneY);
        canvas.SegH(laneY, sx, tx);
        canvas.SegV(tx, laneY, ty + 1);

        if (edge.HeadTo == MermaidHead.None) canvas.AddBits(tx, ty + 1, D);
        else canvas.Set(tx, ty + 1, HeadGlyph(edge.HeadTo, "▲"), MermaidCls.Edge);
        if (edge.HeadFrom != MermaidHead.None) canvas.Set(sx, sy, HeadGlyph(edge.HeadFrom, "▲"), MermaidCls.Edge);

        if (edge.Label is not null) PlaceLabel(canvas, edge.Label, Sat(laneY, 1), Half(sx + tx));
    }

    /// <summary>Write an edge label, stopping at the first cell already occupied.</summary>
    private static void PlaceLabel(MermaidCanvas canvas, string label, int row, int startX)
    {
        if (row >= canvas.H) return;
        var text = MermaidLabels.FitLabel(label, MermaidLabels.MaxLabel);
        var x = startX;
        foreach (var (c, cw) in MermaidWidth.Measured(text))
        {
            if (cw == 0) continue;
            if (x + cw > canvas.W) break;
            var blocked = false;
            for (var k = 0; k < cw; k++)
            {
                var i = canvas.Idx(x + k, row);
                // Outside the grid the original reads `undefined`, which is not ' ', so the cell counts as blocked.
                if (!canvas.InGrid(i) || canvas.Ch[i] != " " || canvas.Mask[i] != 0 || canvas.Occupied[i] != 0) blocked = true;
            }
            if (blocked) break;
            canvas.Set(x, row, c, MermaidCls.EdgeLabel);
            for (var k = 1; k < cw; k++) canvas.Set(x + k, row, Cont, MermaidCls.EdgeLabel);
            x += cw;
        }
    }
}
