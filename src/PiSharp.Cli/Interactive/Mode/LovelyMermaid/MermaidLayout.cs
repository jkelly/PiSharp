// lovely-mermaid 0.3.3 (Apache-2.0): src/layout.ts.
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidCanvas;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidJs;

namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

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
    public const int Pad = 1;
    /// <summary>Minimum horizontal / vertical space between boxes.</summary>
    private const int GapX = 3;
    private const int GapY = 2;
    /// <summary>Refuse to allocate a canvas larger than this many cells.</summary>
    public const int MaxCanvasCells = 1 << 21;

    private sealed record NodeSizes(int[] BoxW, int[] BoxH, int[] LayW, int[] LayH, int[] ExtraH, int[] SelfLabelW);

    private sealed record RoutePlan(int CanvasW, int CanvasH, int[] BandEnd, int[] RankStart, int[] EdgeBus,
        int LaneBase, int[] EdgeLane, int[] SkipApproach, int[] EdgeEntryX, bool[] EdgeStraight,
        int[] FwdEntryX, bool[] EdgeLabelLeft);

    public static int Sat(int a, int b) => Math.Max(0, a - b);
    public static int Half(int n) => (int)Math.Floor(n / 2.0);

    private static string? EdgeText(MermaidEdge edge)
    {
        var joined = string.Join(" ", new[] { edge.CardFrom ?? "", edge.Label ?? "", edge.CardTo ?? "" }.Where(part => part != ""));
        return joined == "" ? null : joined;
    }

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

    /// <summary>A covered coordinate range competing for a routing track.</summary>
    internal sealed record TrackSpan(int Start, int End, int From, int To, int Edge, bool Labeled = false);

    /// <summary>Pack spans; labelled lanes do not share endpoints, and short lanes take inner tracks.</summary>
    public static (List<(int Idx, int Slot)> Assigned, int Count) AssignTracks(List<TrackSpan> spans, bool shortestFirst = false)
    {
        var sorted = spans.ToList();
        StableSort(sorted, (a, b) =>
        {
            var cmp = shortestFirst ? (a.End - a.Start).CompareTo(b.End - b.Start) : 0;
            if (cmp != 0) return cmp;
            cmp = a.Start.CompareTo(b.Start); if (cmp != 0) return cmp;
            cmp = a.End.CompareTo(b.End); if (cmp != 0) return cmp;
            cmp = a.From.CompareTo(b.From); if (cmp != 0) return cmp;
            cmp = a.To.CompareTo(b.To); if (cmp != 0) return cmp;
            return a.Edge.CompareTo(b.Edge);
        });
        var tracks = new List<List<TrackSpan>>();
        var assigned = new List<(int, int)>();
        foreach (var span in sorted)
        {
            var slot = tracks.FindIndex(members => members.All(m =>
                m.End + 2 <= span.Start || span.End + 2 <= m.Start ||
                ((m.From == span.From || m.To == span.To) && !m.Labeled && !span.Labeled)));
            if (slot == -1)
            {
                tracks.Add([]);
                slot = tracks.Count - 1;
            }
            tracks[slot].Add(span);
            assigned.Add((span.Edge, slot));
        }
        return (assigned, tracks.Count);
    }

    public static bool IsAdjacentBack(int[] ranks, MermaidEdge e) => e.From != e.To && ranks[e.From] == ranks[e.To] + 1;

    private static List<TrackSpan> BusSpans(MermaidGraph graph, int[] ranks, int[] centers, int r, bool exact, bool withBack)
    {
        var result = new List<TrackSpan>();
        for (var i = 0; i < graph.Edges.Count; i++)
        {
            var e = graph.Edges[i];
            var jogs = exact ? centers[e.From] != centers[e.To] : Math.Abs(centers[e.From] - centers[e.To]) > 1;
            var lo = Math.Min(ranks[e.From], ranks[e.To]);
            var adjacent = withBack ? Math.Abs(ranks[e.From] - ranks[e.To]) == 1 : ranks[e.To] == ranks[e.From] + 1;
            if (e.From != e.To && adjacent && lo == r && jogs)
                result.Add(new(Math.Min(centers[e.From], centers[e.To]), Math.Max(centers[e.From], centers[e.To]), e.From, e.To, i));
        }
        return result;
    }

    private static List<TrackSpan> LaneSpans(MermaidGraph graph, int[] ranks, MermaidPlaced[] placed, bool vertical)
    {
        var result = new List<TrackSpan>();
        for (var i = 0; i < graph.Edges.Count; i++)
        {
            var e = graph.Edges[i];
            if (e.From == e.To || ranks[e.To] == ranks[e.From] + 1) continue;
            if (vertical && IsAdjacentBack(ranks, e)) continue;
            var pf = placed[e.From];
            var pt = placed[e.To];
            var a = vertical ? Math.Min(pf.Cy, pt.Cy) : Math.Min(pf.Cx, pt.Cx);
            var b = vertical ? Math.Max(pf.Cy, pt.Cy) : Math.Max(pf.Cx, pt.Cx);
            result.Add(new(a, b, e.From, e.To, i, EdgeText(e) is not null));
        }
        return result;
    }

    // ----------------------------------------------------------------- placement

    private static RoutePlan PlaceTd(int[] ranks, int maxRank, List<List<int>> byRank, NodeSizes sizes, MermaidGraph graph, MermaidPlaced[] placed)
    {
        var centers = AssignPositions(byRank, sizes.LayW, GapX, graph.Edges, ranks);

        // Spread a target's forward cluster and skip entries over its top border.
        int BoxL(int j) => Sat(centers[j], Half(sizes.BoxW[j]));
        int BoxR(int j) => BoxL(j) + sizes.BoxW[j] - 1;
        bool IsSkip(MermaidEdge e) => e.From != e.To && ranks[e.To] - ranks[e.From] > 1;
        var edgeEntryX = Enumerable.Repeat(-1, graph.Edges.Count).ToArray();
        var edgeStraight = new bool[graph.Edges.Count];
        var fwdEntryX = Enumerable.Repeat(-1, graph.Nodes.Count).ToArray();
        var edgeLabelLeft = new bool[graph.Edges.Count];
        static int LabelW(MermaidEdge e)
        {
            var parts = new[] { e.Label, e.CardTo }.Where(p => p is not null).ToList();
            return parts.Count == 0 ? -1 : parts.Max(p => Math.Min(MermaidWidth.StringWidth(p!), MermaidLabels.MaxLabel));
        }
        var skipsInto = Lists(graph.Nodes.Count);
        var fwdsInto = Lists(graph.Nodes.Count);
        for (var i = 0; i < graph.Edges.Count; i++)
        {
            var e = graph.Edges[i];
            if (e.From == e.To) continue;
            if (IsSkip(e)) skipsInto[e.To].Add(i);
            else if (ranks[e.To] == ranks[e.From] + 1) fwdsInto[e.To].Add(i);
        }
        for (var t = 0; t < graph.Nodes.Count; t++)
        {
            var skips = skipsInto[t];
            if (skips.Count == 0) continue;
            var fwds = fwdsInto[t];
            var cx = centers[t];
            var left = BoxL(t);
            var right = BoxR(t);
            var hasFwd = fwds.Count > 0;
            var aligned = hasFwd && fwds.Any(i => Math.Abs(centers[graph.Edges[i].From] - cx) <= 1);
            var spreadL = hasFwd && aligned ? cx : left;
            var slots = hasFwd && aligned ? skips.Count : (hasFwd ? 1 : 0) + skips.Count;
            int Slot(int i) => spreadL + (int)Round(((right - spreadL) * (i + 1)) / (double)(slots + 1));
            var items = new List<(int Slot, int W, int Skip)>();
            if (hasFwd) items.Add((aligned ? cx : Slot(0), fwds.Max(i => LabelW(graph.Edges[i])), -1));
            for (var j = 0; j < skips.Count; j++)
            {
                var si = skips[j];
                items.Add((Slot(hasFwd && aligned ? j : j + (hasFwd ? 1 : 0)), LabelW(graph.Edges[si]), si));
            }
            var cols = new List<int>();
            var lefts = new List<bool>();
            var cursor = left;
            var fits = true;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var x = Math.Max(item.Slot, cursor);
                if (x > right - 1) { fits = false; break; }
                var next = i + 1 < items.Count ? items[i + 1].Slot : int.MaxValue;
                var w = item.W;
                if (w >= 0 && x + w + 2 > next && x - cursor >= w)
                {
                    lefts.Add(true);
                    cursor = x + 2;
                }
                else
                {
                    lefts.Add(false);
                    cursor = w >= 0 ? x + w + 2 : x + 2;
                }
                cols.Add(x);
            }
            if (fits)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item.Skip == -1)
                    {
                        fwdEntryX[t] = cols[i];
                        if (lefts[i]) foreach (var fi in fwds) edgeLabelLeft[fi] = true;
                    }
                    else
                    {
                        edgeEntryX[item.Skip] = cols[i];
                        edgeLabelLeft[item.Skip] = lefts[i];
                    }
                }
                continue;
            }
            // Overflow: retain the centre forward port and place skips beyond arrival labels or to the left.
            var reach = hasFwd ? Math.Max(cx, fwds.Max(i => cx + 1 + LabelW(graph.Edges[i]))) : -1;
            foreach (var si in skips)
            {
                var clear = reach == -1 ? cx + 2 : reach + 2;
                var capped = Math.Min(clear, right - 1);
                if (capped > Math.Max(cx, reach)) edgeEntryX[si] = capped;
                else
                {
                    edgeEntryX[si] = Math.Max(left + 1, cx - 2);
                    edgeLabelLeft[si] = true;
                }
            }
        }
        for (var i = 0; i < graph.Edges.Count; i++)
        {
            var e = graph.Edges[i];
            if (!IsSkip(e)) continue;
            var entryX = edgeEntryX[i];
            edgeStraight[i] = Enumerable.Range(0, graph.Nodes.Count).All(j =>
                ranks[j] <= ranks[e.From] || ranks[j] >= ranks[e.To] || entryX < BoxL(j) || entryX > BoxR(j));
        }

        var edgeBus = new int[graph.Edges.Count];
        var busTracks = new int[maxRank + 1];
        for (var r = 0; r < maxRank; r++)
        {
            var spans = BusSpans(graph, ranks, centers, r, false, true);
            for (var i = 0; i < graph.Edges.Count; i++)
            {
                var e = graph.Edges[i];
                if (!IsSkip(e) || ranks[e.From] != r) continue;
                var endX = edgeStraight[i] ? edgeEntryX[i] : 1 << 29;
                spans.Add(new(Math.Min(centers[e.From], endX), Math.Max(centers[e.From], endX), e.From, e.To, i));
            }
            if (spans.Count == 0) continue;
            var back = spans.Where(s => IsAdjacentBack(ranks, graph.Edges[s.Edge])).ToList();
            var fwd = spans.Where(s => !IsAdjacentBack(ranks, graph.Edges[s.Edge])).ToList();
            var trackBase = graph.Edges.Any(e => IsAdjacentBack(ranks, e) && ranks[e.To] == r) ? 1 : 0;
            var b = AssignTracks(back);
            foreach (var (idx, slot) in b.Assigned) edgeBus[idx] = trackBase + slot;
            var f = AssignTracks(fwd);
            foreach (var (idx, slot) in f.Assigned) edgeBus[idx] = trackBase + b.Count + slot;
            busTracks[r] = trackBase + b.Count + f.Count;
        }
        var entersInto = new bool[maxRank + 1];
        for (var i = 0; i < graph.Edges.Count; i++)
            if (IsSkip(graph.Edges[i]) && !edgeStraight[i]) entersInto[ranks[graph.Edges[i].To]] = true;
        for (var r = 0; r < maxRank; r++) if (entersInto[r + 1]) busTracks[r]++;

        var rankH = byRank.Select(row => row.Count == 0 ? 3 : row.Max(i => sizes.BoxH[i] + sizes.ExtraH[i])).ToArray();
        var hasCards = graph.Edges.Any(e => e.CardFrom is not null || e.CardTo is not null);
        var gapY = hasCards ? Math.Max(GapY, 3) : GapY;
        var rankY = new int[maxRank + 1];
        for (var r = 1; r <= maxRank; r++) rankY[r] = rankY[r - 1] + rankH[r - 1] + Math.Max(gapY, busTracks[r - 1] + 1);
        var canvasH = rankY[maxRank] + rankH[maxRank];
        var bandEnd = Enumerable.Range(0, maxRank + 1).Select(r => rankY[r] + rankH[r]).ToArray();
        var skipApproach = Enumerable.Repeat(-1, maxRank + 1).ToArray();
        for (var r = 0; r < maxRank; r++) if (entersInto[r + 1]) skipApproach[r + 1] = rankY[r + 1] - 2;

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
            if (e.From == e.To) continue;
            if (ranks[e.To] == ranks[e.From] + 1)
            {
                foreach (var part in new[] { e.Label, e.CardTo }.Where(part => part is not null))
                    contentW = Math.Max(contentW, placed[e.To].Cx + 2 + Math.Min(MermaidWidth.StringWidth(part!), MermaidLabels.MaxLabel));
                if (e.CardFrom is not null) contentW = Math.Max(contentW, placed[e.From].Cx + 2 + MermaidWidth.StringWidth(e.CardFrom));
            }
            else if (IsAdjacentBack(ranks, e))
            {
                var text = EdgeText(e);
                if (text is not null) contentW = Math.Max(contentW, placed[e.To].Cx + 2 + Math.Min(MermaidWidth.StringWidth(text), MermaidLabels.MaxLabel));
            }
            else
            {
                var text = EdgeText(e);
                if (text is not null) contentW = Math.Max(contentW, diagramW + Math.Min(MermaidWidth.StringWidth(text), MermaidLabels.MaxLabel) + 1);
            }
        }
        var edgeLane = new int[graph.Edges.Count];
        var lanes = LaneSpans(graph, ranks, placed, true).Where(s => !edgeStraight[s.Edge]).ToList();
        var canvasW = contentW;
        var laneBase = 0;
        if (lanes.Count > 0)
        {
            var (assigned, count) = AssignTracks(lanes, true);
            foreach (var (idx, slot) in assigned) edgeLane[idx] = slot;
            canvasW = contentW + 1 + count;
            laneBase = contentW + 1;
        }
        return new(canvasW, canvasH, bandEnd, rankY, edgeBus, laneBase, edgeLane, skipApproach, edgeEntryX, edgeStraight, fwdEntryX, edgeLabelLeft);
    }

    private static RoutePlan PlaceLr(int[] ranks, int maxRank, List<List<int>> byRank, NodeSizes sizes, MermaidGraph graph, MermaidPlaced[] placed)
    {
        var colW = byRank.Select(row => row.Count == 0 ? 0 : row.Max(i => sizes.BoxW[i])).ToArray();
        var centers = AssignPositions(byRank, sizes.LayH, 1, graph.Edges, ranks);
        bool IsSkip(MermaidEdge e) => e.From != e.To && ranks[e.To] - ranks[e.From] > 1;
        var stubRows = new HashSet<int>();
        foreach (var e in graph.Edges)
        {
            if (e.From == e.To || ranks[e.To] >= ranks[e.From]) continue;
            var t = e.To;
            stubRows.Add(Sat(centers[t], Half(sizes.BoxH[t] + sizes.ExtraH[t])) + sizes.BoxH[t]);
        }
        var edgeStraight = new bool[graph.Edges.Count];
        for (var i = 0; i < graph.Edges.Count; i++)
        {
            var e = graph.Edges[i];
            if (!IsSkip(e)) continue;
            var row = centers[e.To];
            edgeStraight[i] = !stubRows.Contains(row) && Enumerable.Range(0, graph.Nodes.Count).All(j =>
                ranks[j] <= ranks[e.From] || ranks[j] >= ranks[e.To] ||
                Math.Abs(centers[j] - row) > Half(sizes.BoxH[j] + sizes.ExtraH[j]));
        }
        var bandLabel = new int[maxRank + 1];
        for (var i = 0; i < graph.Edges.Count; i++)
        {
            var e = graph.Edges[i];
            if (e.From == e.To) continue;
            if (ranks[e.To] != ranks[e.From] + 1 && !edgeStraight[i]) continue;
            var verb = e.Label is null ? 0 : Math.Min(MermaidWidth.StringWidth(e.Label), MermaidLabels.MaxLabel);
            var cards = new[] { e.CardFrom, e.CardTo }.Where(c => c is not null).Sum(c => MermaidWidth.StringWidth(c!) + 1);
            bandLabel[ranks[e.From]] = Math.Max(bandLabel[ranks[e.From]], verb + cards);
        }
        var edgeBus = new int[graph.Edges.Count];
        var busTracks = new int[maxRank + 1];
        for (var r = 0; r < maxRank; r++)
        {
            var spans = BusSpans(graph, ranks, centers, r, true, false);
            for (var i = 0; i < graph.Edges.Count; i++)
            {
                var e = graph.Edges[i];
                if (!IsSkip(e) || !edgeStraight[i] || ranks[e.From] != r) continue;
                spans.Add(new(Math.Min(centers[e.From], centers[e.To]), Math.Max(centers[e.From], centers[e.To]), e.From, e.To, i));
            }
            if (spans.Count == 0) continue;
            var (assigned, count) = AssignTracks(spans);
            foreach (var (idx, slot) in assigned) edgeBus[idx] = slot;
            busTracks[r] = count;
        }
        var rankX = new int[maxRank + 1];
        for (var r = 1; r <= maxRank; r++)
        {
            var gap = Math.Max(Math.Max(GapX + 1, bandLabel[r - 1] + 3), busTracks[r - 1] + 1);
            rankX[r] = rankX[r - 1] + colW[r - 1] + gap;
        }
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
        var lanes = LaneSpans(graph, ranks, placed, false).Where(s => !edgeStraight[s.Edge]).ToList();
        var canvasH = diagramH;
        var laneBase = 0;
        if (lanes.Count > 0)
        {
            var (assigned, count) = AssignTracks(lanes, true);
            foreach (var (idx, slot) in assigned) edgeLane[idx] = slot;
            canvasH = diagramH + 1 + count;
            laneBase = diagramH + 1;
        }
        return new(canvasW, canvasH, bandEnd, rankX, edgeBus, laneBase, edgeLane,
            Enumerable.Repeat(-1, maxRank + 1).ToArray(), Enumerable.Repeat(-1, graph.Edges.Count).ToArray(),
            edgeStraight, Enumerable.Repeat(-1, graph.Nodes.Count).ToArray(), new bool[graph.Edges.Count]);
    }

    // -------------------------------------------------------------------- canvas

    /// <summary>Rank, place, draw and route a graph onto a fresh canvas; <c>null</c> when empty or over the cell cap.</summary>
    public static MermaidCanvas? LayoutCanvas(MermaidGraph graph, List<MermaidNodeExtra> extras)
    {
        var n = graph.Nodes.Count;
        if (n == 0) return null;

        // Parallel edges share cells: collect all labels on their first edge before sizing.
        var firstOf = new Dictionary<(int From, int To), int>();
        for (var i = 0; i < graph.Edges.Count; i++)
        {
            var e = graph.Edges[i];
            if (e.From == e.To) continue;
            var key = (e.From, e.To);
            if (!firstOf.TryGetValue(key, out var first)) { firstOf[key] = i; continue; }
            if (e.Label is not null)
            {
                var head = graph.Edges[first].Label;
                graph.Edges[first] = graph.Edges[first] with { Label = head is null ? e.Label : $"{head} / {e.Label}" };
                graph.Edges[i] = e with { Label = null };
            }
        }

        var ranks = ComputeRanks(graph);
        var maxRank = Math.Max(ranks.Max(), 0);

        var byRank = Enumerable.Range(0, maxRank + 1).Select(_ => new List<int>()).ToList();
        for (var idx = 0; idx < ranks.Length; idx++) byRank[ranks[idx]].Add(idx);
        OrderRanks(byRank, graph.Edges, ranks);

        var vertical = graph.Dir is MermaidDir.Down or MermaidDir.Up;
        var inLane = new bool[graph.Nodes.Count];
        foreach (var e in graph.Edges)
        {
            if (vertical && IsAdjacentBack(ranks, e)) continue;
            if (e.From != e.To && ranks[e.To] != ranks[e.From] + 1) inLane[e.From] = inLane[e.To] = true;
        }
        foreach (var row in byRank) StableSort(row, (a, b) => inLane[a].CompareTo(inLane[b]));

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
            var text = EdgeText(e);
            if (text is not null) selfLabelW[e.From] = Math.Max(selfLabelW[e.From], Math.Min(MermaidWidth.StringWidth(text), MermaidLabels.MaxLabel));
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

        var plan = vertical ? PlaceTd(ranks, maxRank, byRank, sizes, graph, placed) : PlaceLr(ranks, maxRank, byRank, sizes, graph, placed);

        if ((long)plan.CanvasW * plan.CanvasH > MaxCanvasCells) return null;

        var canvas = new MermaidCanvas(plan.CanvasW, plan.CanvasH);
        for (var idx = 0; idx < n; idx++)
        {
            var extra = extras[idx];
            canvas.CurTag = graph.Nodes[idx].Classes is { } classes ? string.Join(" ", classes) : null;
            canvas.CurHref = graph.Nodes[idx].Href;
            var mirrored = graph.Dir == MermaidDir.Up;
            if (extra.Kind == MermaidExtraKind.Frame) DrawFrame(canvas, placed[idx], graph.Nodes[idx].Label, extra.Sub!, mirrored);
            else if (extra.Kind == MermaidExtraKind.Compartments) DrawClassBox(canvas, placed[idx], extra.Sections!, mirrored);
            else DrawBox(canvas, placed[idx], wrapped[idx], graph.Nodes[idx].Shape, mirrored);
        }
        canvas.CurTag = null;
        canvas.CurHref = null;

        var laneLabels = new List<LaneLabel>();
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
            var adjacentBack = from.Rank == to.Rank + 1;
            var bus = plan.BandEnd[(adjacentBack ? to : from).Rank] + plan.EdgeBus[i];
            var lane = plan.LaneBase + plan.EdgeLane[i];
            if (vertical)
            {
                if (adjacent) RouteForward(canvas, from, to, edge, bus, plan.FwdEntryX[edge.To], plan.EdgeLabelLeft[i]);
                else if (adjacentBack) RouteBackAdjacent(canvas, from, to, edge, bus);
                else if (to.Rank > from.Rank)
                    RouteSkip(canvas, from, to, edge, lane, plan.EdgeEntryX[i], bus, plan.SkipApproach[to.Rank], plan.EdgeStraight[i], plan.EdgeLabelLeft[i]);
                else RouteBack(canvas, from, to, edge, lane);
            }
            else if (adjacent)
            {
                RouteForwardLr(canvas, from, to, edge, bus);
            }
            else if (to.Rank > from.Rank && plan.EdgeStraight[i])
            {
                RouteSkipLr(canvas, from, to, edge, bus);
            }
            else RouteBackLr(canvas, from, to, edge, lane, laneLabels);
        }

        PlaceLaneLabels(canvas, laneLabels);
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
    public static MermaidCanvas? LayoutClass(MermaidGraph graph)
    {
        var extras = graph.Nodes.Select(node => new MermaidNodeExtra(MermaidExtraKind.Compartments,
            Sections: node.Sections ?? [[node.Label]])).ToList();
        var canvas = LayoutCanvas(graph, extras);
        return canvas is null ? null : Orient(canvas, graph);
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

        // State region reparenting can put parents after children; traverse actual child relations.
        var childGroups = Lists(graph.Groups.Count);
        for (var gi = 0; gi < graph.Groups.Count; gi++)
            if (graph.Groups[gi].Parent != -1) childGroups[graph.Groups[gi].Parent].Add(gi);
        var keep = new bool[graph.Groups.Count];
        bool Visit(int gi)
        {
            var kept = referenced[gi] || (directNodes.TryGetValue(gi, out var direct) && direct.Count > 0);
            foreach (var c in childGroups[gi]) if (Visit(c)) kept = true;
            keep[gi] = kept;
            return kept;
        }
        for (var gi = 0; gi < graph.Groups.Count; gi++) if (graph.Groups[gi].Parent == -1) Visit(gi);

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
                nodes.Add(new(graph.Nodes[i].Label, graph.Nodes[i].Shape) { Classes = graph.Nodes[i].Classes, Href = graph.Nodes[i].Href });
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
            edges.Add(new(fi, ti, e.Label, e.HeadTo, e.HeadFrom, e.Line));
        }

        // Layout only reads nodes/edges/dir, so a bare graph carrying those is enough.
        var synth = new MermaidGraph(graph.Dir) { Nodes = nodes, Edges = edges };
        return LayoutCanvas(synth, extras);
    }

    // ------------------------------------------------------------------- drawing

    public static void DrawBox(MermaidCanvas canvas, MermaidPlaced p, List<string> lines, MermaidShape shape, bool mirrored = false)
    {
        var (x, y, w, h) = (p.X, p.Y, p.W, p.H);
        var right = x + w - 1;
        var bottom = y + h - 1;
        var (tl, tr, bl, br) = shape switch
        {
            MermaidShape.Diamond => ("╔", "╗", "╚", "╝"),
            MermaidShape.Round => ("╭", "╮", "╰", "╯"),
            _ => ("┌", "┐", "└", "┘"),
        };
        canvas.Set(x, y, tl, MermaidCls.Border);
        canvas.Set(right, y, tr, MermaidCls.Border);
        canvas.Set(x, bottom, bl, MermaidCls.Border);
        canvas.Set(right, bottom, br, MermaidCls.Border);
        if (shape == MermaidShape.Diamond)
        {
            for (var cx = x + 1; cx < right; cx++)
            {
                canvas.Set(cx, y, "═", MermaidCls.Border);
                canvas.Set(cx, bottom, "═", MermaidCls.Border);
            }
            for (var cy = y + 1; cy < bottom; cy++)
            {
                canvas.Set(x, cy, "║", MermaidCls.Border);
                canvas.Set(right, cy, "║", MermaidCls.Border);
            }
        }
        else
        {
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
        }
        for (var cy = y; cy <= bottom; cy++)
        {
            for (var cx = x; cx <= right; cx++)
            {
                var i = canvas.Idx(cx, cy);
                if (!canvas.InGrid(i)) continue;
                canvas.Occupied[i] = 1;
                if (canvas.CurTag is not null) canvas.Tag[i] = canvas.CurTag;
                if (canvas.CurHref is not null) canvas.Href[i] = canvas.CurHref;
            }
        }
        var inner = Math.Max(1, Sat(w, 2 * Pad + 2));
        var ordered = mirrored ? Enumerable.Reverse(lines).ToList() : lines;
        for (var li = 0; li < ordered.Count; li++)
        {
            var text = MermaidLabels.FitLabel(ordered[li], inner);
            var textX = x + 1 + Pad + Half(Sat(inner, MermaidWidth.StringWidth(text)));
            DrawText(canvas, text, textX, y + 1 + li, MermaidCls.Text);
        }
    }

    private static void DrawClassBox(MermaidCanvas canvas, MermaidPlaced p, List<List<string>> sections, bool mirrored = false)
    {
        DrawBox(canvas, p, [], MermaidShape.Rect);
        var inner = Math.Max(1, Sat(p.W, 2 * Pad + 2));
        var rows = new List<(bool Sep, string Text, bool Center)>();
        for (var si = 0; si < sections.Count; si++)
        {
            var section = sections[si];
            if (section.Count == 0) continue;
            if (rows.Count > 0) rows.Add((true, "", false));
            foreach (var line in section) rows.Add((false, MermaidLabels.FitLabel(line, inner), si == 0));
        }
        if (mirrored) rows.Reverse();
        for (var ri = 0; ri < rows.Count; ri++)
        {
            var r = rows[ri];
            var row = p.Y + 1 + ri;
            if (r.Sep)
            {
                canvas.Set(p.X, row, "├", MermaidCls.Border);
                for (var x = p.X + 1; x < p.X + p.W - 1; x++) canvas.Set(x, row, "─", MermaidCls.Border);
                canvas.Set(p.X + p.W - 1, row, "┤", MermaidCls.Border);
            }
            else
            {
                var tx = r.Center ? p.X + 1 + Pad + Half(Sat(inner, MermaidWidth.StringWidth(r.Text))) : p.X + 1 + Pad;
                DrawTextOverEdges(canvas, r.Text, tx, row, MermaidCls.Text);
            }
        }
    }

    private static void DrawFrame(MermaidCanvas canvas, MermaidPlaced p, string title, MermaidCanvas sub, bool mirrored = false)
    {
        DrawBox(canvas, p, [], MermaidShape.Rect);
        if (title != "")
        {
            var t = MermaidLabels.FitLabel(title, Sat(p.W, 4));
            DrawTextOverEdges(canvas, $" {t} ", p.X + 1, mirrored ? p.Y + p.H - 1 : p.Y, MermaidCls.Text);
        }
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

    private static void RouteForward(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge, int bus, int entryX = -1, bool labelLeft = false)
    {
        var tx = entryX == -1 ? to.Cx : entryX;
        var bx = Math.Abs(from.Cx - tx) <= 1 ? tx : from.Cx;
        var by = from.Y + from.H - 1;
        var headRow = to.Y - 1;
        canvas.Junction(bx, by, D);
        canvas.SegV(bx, by, bus);
        if (bx == tx) canvas.SegV(bx, bus, headRow);
        else
        {
            canvas.SegH(bus, bx, tx);
            canvas.SegV(tx, bus, headRow);
        }
        if (edge.HeadTo == MermaidHead.None) canvas.AddBits(tx, headRow, U);
        else canvas.Set(tx, headRow, HeadGlyph(edge.HeadTo, "▼"), MermaidCls.Edge);
        if (edge.HeadFrom != MermaidHead.None) canvas.Set(bx, by, HeadGlyph(edge.HeadFrom, "▲"), MermaidCls.Edge);
        if (edge.CardFrom is null && edge.CardTo is null)
        {
            if (edge.Label is not null)
            {
                var start = labelLeft ? Sat(tx, Math.Min(MermaidWidth.StringWidth(edge.Label), MermaidLabels.MaxLabel)) : tx + 1;
                PlaceLabel(canvas, edge.Label, headRow, start);
            }
            return;
        }
        var srcRow = by + 1;
        if (edge.CardFrom is not null) PlaceLabel(canvas, edge.CardFrom, srcRow, bx + 1);
        if (edge.CardTo is not null) PlaceLabel(canvas, edge.CardTo, headRow, tx + 1);
        if (edge.Label is not null)
        {
            var midRow = headRow - 1;
            if (midRow > srcRow)
            {
                var lineX = midRow > bus ? tx : bx;
                PlaceLabel(canvas, edge.Label, midRow, lineX + 1);
            }
            else
                PlaceLabel(canvas, edge.Label, headRow, tx + 1 + (edge.CardTo is null ? 0 : MermaidWidth.StringWidth(edge.CardTo) + 1));
        }
    }

    private static void RouteBackAdjacent(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge, int bus)
    {
        var tx = Math.Min(to.X + to.W - 2, to.Cx + 2);
        var bx0 = Math.Min(from.X + from.W - 2, from.Cx + 2);
        var bx = Math.Abs(bx0 - tx) <= 1 ? tx : bx0;
        var fy = from.Y;
        var headRow = to.Y + to.H;
        canvas.Junction(bx, fy, U);
        if (bx == tx) canvas.SegV(bx, headRow, fy);
        else
        {
            canvas.SegV(bx, bus, fy);
            canvas.SegH(bus, bx, tx);
            canvas.SegV(tx, headRow, bus);
        }
        if (edge.HeadTo == MermaidHead.None) canvas.AddBits(tx, headRow, D);
        else canvas.Set(tx, headRow, HeadGlyph(edge.HeadTo, "▲"), MermaidCls.Edge);
        if (edge.HeadFrom != MermaidHead.None) canvas.Set(bx, fy, HeadGlyph(edge.HeadFrom, "▼"), MermaidCls.Edge);
        var text = EdgeText(edge);
        if (text is not null) PlaceLabel(canvas, text, headRow, tx + 1);
    }

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
        var text = EdgeText(edge);
        if (text is not null) PlaceLabel(canvas, text, bottom + 1, p.X + p.W + 1);
    }

    private static void RouteSkip(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge,
        int laneX, int entryX, int busY, int approachY, bool straight, bool labelLeft)
    {
        var bx = from.Cx;
        var bottom = from.Y + from.H - 1;
        canvas.Junction(bx, bottom, D);
        canvas.SegV(bx, bottom, busY);
        if (straight)
        {
            canvas.SegH(busY, bx, entryX);
            canvas.SegV(entryX, busY, to.Y - 1);
        }
        else
        {
            canvas.SegH(busY, bx, laneX);
            canvas.SegV(laneX, busY, approachY);
            canvas.SegH(approachY, entryX, laneX);
            canvas.SegV(entryX, approachY, to.Y - 1);
        }
        if (edge.HeadTo == MermaidHead.None) canvas.AddBits(entryX, to.Y - 1, D);
        else canvas.Set(entryX, to.Y - 1, HeadGlyph(edge.HeadTo, "▼"), MermaidCls.Edge);
        if (edge.HeadFrom != MermaidHead.None) canvas.Set(bx, bottom, HeadGlyph(edge.HeadFrom, "▲"), MermaidCls.Edge);
        var text = EdgeText(edge);
        if (text is not null)
        {
            var start = labelLeft ? Sat(entryX, Math.Min(MermaidWidth.StringWidth(text), MermaidLabels.MaxLabel)) : entryX + 1;
            PlaceLabel(canvas, text, to.Y - 1, start);
        }
    }

    private static void RouteBack(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge, int laneX)
    {
        var sx = from.X + from.W - 1;
        var sy = from.Cy;
        var text = EdgeText(edge);
        canvas.Junction(sx, sy, R);
        canvas.SegH(sy, sx, laneX);
        if (edge.HeadFrom != MermaidHead.None) canvas.Set(sx, sy, HeadGlyph(edge.HeadFrom, "◄"), MermaidCls.Edge);
        var tx = to.X + to.W - 1;
        var tyc = to.Cy;
        canvas.SegV(laneX, sy, tyc);
        canvas.SegH(tyc, tx + 1, laneX);
        if (edge.HeadTo == MermaidHead.None) canvas.AddBits(tx + 1, tyc, R);
        else canvas.Set(tx + 1, tyc, HeadGlyph(edge.HeadTo, "◄"), MermaidCls.Edge);
        if (text is not null) PlaceLabel(canvas, text, Sat(tyc, 1), Sat(laneX, MermaidWidth.StringWidth(text) + 1));
    }

    private static void RouteForwardLr(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge, int bus)
    {
        var rx = from.X + from.W - 1;
        var ry = from.Cy;
        var ly = to.Cy;
        var headCol = to.X - 1;
        canvas.Junction(rx, ry, R);
        canvas.SegH(ry, rx, bus);
        if (ry == ly) canvas.SegH(ry, bus, headCol);
        else
        {
            canvas.SegV(bus, ry, ly);
            canvas.SegH(ly, bus, headCol);
        }
        if (edge.HeadTo == MermaidHead.None) canvas.AddBits(headCol, ly, R);
        else canvas.Set(headCol, ly, HeadGlyph(edge.HeadTo, "▶"), MermaidCls.Edge);
        if (edge.HeadFrom != MermaidHead.None) canvas.Set(rx, ry, HeadGlyph(edge.HeadFrom, "◄"), MermaidCls.Edge);
        if (edge.Label is not null) PlaceLabel(canvas, edge.Label, Sat(ly, 1), bus + 1);
        if (edge.CardFrom is not null) PlaceLabel(canvas, edge.CardFrom, Sat(ry, 1), rx + 1);
        if (edge.CardTo is not null) PlaceLabel(canvas, edge.CardTo, Sat(ly, 1), Sat(headCol, MermaidWidth.StringWidth(edge.CardTo)));
    }

    private static void RouteSkipLr(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge, int bus)
    {
        var ry = from.Cy;
        var ty = to.Cy;
        var rx = from.X + from.W - 1;
        var headCol = to.X - 1;
        canvas.Junction(rx, ry, R);
        canvas.SegH(ry, rx, bus);
        if (ry != ty) canvas.SegV(bus, ry, ty);
        canvas.SegH(ty, bus, headCol);
        if (edge.HeadTo == MermaidHead.None) canvas.AddBits(headCol, ty, R);
        else canvas.Set(headCol, ty, HeadGlyph(edge.HeadTo, "▶"), MermaidCls.Edge);
        if (edge.HeadFrom != MermaidHead.None) canvas.Set(rx, ry, HeadGlyph(edge.HeadFrom, "◄"), MermaidCls.Edge);
        var text = EdgeText(edge);
        if (text is not null) PlaceLabel(canvas, text, Sat(ty, 1), bus + 1);
    }

    private sealed record LaneLabel(string Text, int Y, int Lo, int Hi);

    private static void RouteBackLr(MermaidCanvas canvas, MermaidPlaced from, MermaidPlaced to, MermaidEdge edge, int laneY, List<LaneLabel> laneLabels)
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
        var text = EdgeText(edge);
        if (text is not null) laneLabels.Add(new($" {MermaidLabels.FitLabel(text, MermaidLabels.MaxLabel)} ", laneY, Math.Min(sx, tx), Math.Max(sx, tx)));
    }

    private static void PlaceLaneLabels(MermaidCanvas canvas, List<LaneLabel> labels)
    {
        foreach (var label in labels)
        {
            var (text, y, lo, hi) = label;
            var tw = MermaidWidth.StringWidth(text);
            var lastStart = hi - 1 - tw;
            if (lastStart < lo + 1 || y >= canvas.H) continue;
            bool Clear(int start)
            {
                for (var x = start; x < start + tw; x++)
                {
                    var i = canvas.Idx(x, y);
                    if (canvas.Occupied[i] == 1 || (canvas.Mask[i] & (U | D)) != 0 || canvas.Ch[i] != " ") return false;
                }
                return true;
            }
            var mid = Math.Min(Math.Max(Half(lo + hi) - Half(tw), lo + 1), lastStart);
            var at = mid;
            for (var d = 0; ; d++)
            {
                var left = mid - d;
                var right = mid + d;
                if (left < lo + 1 && right > lastStart) break;
                if (left >= lo + 1 && Clear(left)) { at = left; break; }
                if (right <= lastStart && Clear(right)) { at = right; break; }
            }
            DrawTextOverEdges(canvas, text, at, y, MermaidCls.EdgeLabel);
        }
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
                if (!canvas.InGrid(i) || canvas.Ch[i] != " " || canvas.Mask[i] != 0 || canvas.Occupied[i] != 0) blocked = true;
            }
            if (blocked) break;
            canvas.Set(x, row, c, MermaidCls.EdgeLabel);
            for (var k = 1; k < cw; k++) canvas.Set(x + k, row, Cont, MermaidCls.EdgeLabel);
            x += cw;
        }
    }
}
