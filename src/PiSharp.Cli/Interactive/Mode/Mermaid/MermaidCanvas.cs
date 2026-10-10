// grok-mermaid 0.2.3 (Apache-2.0): src/canvas.ts — used by Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/mermaid.ts.
using System.Text;

namespace PiSharp.Cli.Interactive.Mode.Mermaid;

/// <summary>Semantic class of a run of cells (types.ts <c>Cls</c>).</summary>
internal static class MermaidCls
{
    public const string Border = "border";
    public const string Text = "text";
    public const string Edge = "edge";
    public const string EdgeLabel = "edgeLabel";
    public const string Title = "title";
    public const string None = "none";
}

/// <summary>
/// A grid of cells. Edges accumulate as direction bits rather than glyphs so that crossings and junctions resolve
/// correctly whatever order they are drawn in; <see cref="FinalizeMask"/> turns the accumulated bits into characters.
/// Cell indices are <c>y * w + x</c> exactly as in the original, so an x past the right edge lands in the next row
/// wherever the original does that too; an index outside the grid is ignored, as a typed array ignores it.
/// </summary>
internal sealed class MermaidCanvas
{
    /// <summary>Sentinel occupying the trailing column of a wide glyph. Never emitted.</summary>
    public const string Cont = "\0";

    public const int U = 1, D = 2, L = 4, R = 8;
    public const int StyDot = 1, StyThick = 2, StySolid = 4;

    public int W { get; }
    public int H { get; }
    public string[] Ch { get; }
    public string[] Cls { get; }
    public byte[] Mask { get; }
    public byte[] Style { get; }
    public byte[] Occupied { get; }
    public int CurStyle { get; set; } = StySolid;

    public MermaidCanvas(int w, int h)
    {
        var n = w * h;
        W = w;
        H = h;
        Ch = new string[n];
        Array.Fill(Ch, " ");
        Cls = new string[n];
        Array.Fill(Cls, MermaidCls.None);
        Mask = new byte[n];
        Style = new byte[n];
        Occupied = new byte[n];
    }

    public int Idx(int x, int y) => y * W + x;

    public bool InGrid(int i) => i >= 0 && i < Ch.Length;

    public void Set(int x, int y, string c, string cls)
    {
        if (x >= W || y >= H) return;
        var i = Idx(x, y);
        if (!InGrid(i)) return;
        Ch[i] = c;
        Cls[i] = cls;
    }

    /// <summary>Accumulate direction bits on a free cell; <c>border</c> cells are never reclassified.</summary>
    public void AddBits(int x, int y, int bits, string cls = MermaidCls.Edge)
    {
        if (x >= W || y >= H) return;
        var i = Idx(x, y);
        if (!InGrid(i) || Occupied[i] != 0) return;
        Mask[i] |= (byte)bits;
        Style[i] |= (byte)CurStyle;
        if (Cls[i] != MermaidCls.Border) Cls[i] = cls;
    }

    /// <summary>Stamp a finished sub-canvas (a subgraph frame's contents) at an offset.</summary>
    public void Blit(MermaidCanvas sub, int ox, int oy)
    {
        for (var sy = 0; sy < sub.H; sy++)
        {
            for (var sx = 0; sx < sub.W; sx++)
            {
                var x = ox + sx;
                var y = oy + sy;
                if (x >= W || y >= H) continue;
                var si = sub.Idx(sx, sy);
                var di = Idx(x, y);
                if (!InGrid(di)) continue;
                Ch[di] = sub.Ch[si];
                Cls[di] = sub.Cls[si];
                Style[di] = sub.Style[si];
                Occupied[di] = 1;
            }
        }
    }

    /// <summary>Add direction bits even to an occupied cell, so an edge can meet a border.</summary>
    public void Junction(int x, int y, int bits)
    {
        if (x >= W || y >= H) return;
        var i = Idx(x, y);
        if (!InGrid(i)) return;
        Mask[i] |= (byte)bits;
        if (Cls[i] != MermaidCls.Border) Cls[i] = MermaidCls.Edge;
    }

    public void SegV(int x, int y0, int y1)
    {
        var a = Math.Min(y0, y1);
        var b = Math.Max(y0, y1);
        for (var y = a; y <= b; y++)
        {
            var bits = 0;
            if (y > a) bits |= U;
            if (y < b) bits |= D;
            AddBits(x, y, bits);
        }
    }

    public void SegH(int y, int x0, int x1)
    {
        var a = Math.Min(x0, x1);
        var b = Math.Max(x0, x1);
        for (var x = a; x <= b; x++)
        {
            var bits = 0;
            if (x > a) bits |= L;
            if (x < b) bits |= R;
            AddBits(x, y, bits);
        }
    }

    /// <summary>Resolve accumulated direction bits into glyphs, honouring line style.</summary>
    public void FinalizeMask()
    {
        for (var i = 0; i < Ch.Length; i++)
        {
            if (Mask[i] != 0 && Ch[i] == " ")
            {
                var c = MaskChar(Mask[i]);
                Ch[i] = Style[i] == StyDot ? DottedChar(c) : Style[i] == StyThick ? ThickChar(c) : c;
            }
        }
    }

    /// <summary>Mirror top-to-bottom for <c>BT</c>. Rows reorder but within-row text does not.</summary>
    public void FlipVertical()
    {
        for (var y = 0; y < H / 2; y++)
        {
            var y2 = H - 1 - y;
            for (var x = 0; x < W; x++)
            {
                var i = Idx(x, y);
                var j = Idx(x, y2);
                (Ch[i], Ch[j]) = (Ch[j], Ch[i]);
                (Cls[i], Cls[j]) = (Cls[j], Cls[i]);
            }
        }
        for (var i = 0; i < Ch.Length; i++) Ch[i] = FlipGlyphV(Ch[i]);
    }

    /// <summary>Mirror left-to-right for <c>RL</c>, then reverse each text/label run back to reading order.</summary>
    public void FlipHorizontal()
    {
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W / 2; x++)
            {
                var x2 = W - 1 - x;
                var i = Idx(x, y);
                var j = Idx(x2, y);
                (Ch[i], Ch[j]) = (Ch[j], Ch[i]);
                (Cls[i], Cls[j]) = (Cls[j], Cls[i]);
            }
        }
        for (var i = 0; i < Ch.Length; i++) Ch[i] = FlipGlyphH(Ch[i]);
        for (var y = 0; y < H; y++)
        {
            var x = 0;
            while (x < W)
            {
                var cls = Cls[Idx(x, y)];
                if (cls is MermaidCls.Text or MermaidCls.EdgeLabel)
                {
                    var start = Idx(x, y);
                    while (x < W && Cls[Idx(x, y)] == cls) x++;
                    var end = Idx(x, y);
                    Array.Reverse(Ch, start, end - start);
                }
                else
                {
                    x++;
                }
            }
        }
    }

    /// <summary>Group each row into runs of one class, dropping wide-glyph continuations.</summary>
    public (List<string> Plain, List<List<MermaidSpan>> Styled, int Width) ToLines()
    {
        var plain = new List<string>();
        var styled = new List<List<MermaidSpan>>();
        var width = 0;
        for (var y = 0; y < H; y++)
        {
            // A trailing CONT counts as painted: it is the second cell of a wide glyph.
            var last = 0;
            for (var x = W - 1; x >= 0; x--)
            {
                if (Ch[Idx(x, y)] != " ")
                {
                    last = x + 1;
                    break;
                }
            }
            width = Math.Max(width, last);
            var spans = new List<MermaidSpan>();
            var plainRow = new StringBuilder();
            var run = new StringBuilder();
            var runCls = MermaidCls.None;
            for (var x = 0; x < last; x++)
            {
                var i = Idx(x, y);
                var c = Ch[i];
                if (c == Cont) continue;
                var cls = Cls[i];
                plainRow.Append(c);
                if (cls != runCls && run.Length > 0)
                {
                    spans.Add(new(run.ToString(), runCls));
                    run.Clear();
                }
                runCls = cls;
                run.Append(c);
            }
            if (run.Length > 0) spans.Add(new(run.ToString(), runCls));
            styled.Add(spans);
            // Only ASCII spaces, which is all a blank cell ever holds.
            plain.Add(plainRow.ToString().TrimEnd(' '));
        }
        var first = 0;
        while (first < plain.Count && plain[first].Length == 0) first++;
        var end = plain.Count;
        while (end > first && plain[end - 1].Length == 0) end--;
        return (plain[first..end], styled[first..end], width);
    }

    /// <summary>Paint <paramref name="text"/> one grapheme cluster per cell; a wide cluster claims a CONT cell.</summary>
    public static void DrawText(MermaidCanvas canvas, string text, int x, int y, string cls)
    {
        var cur = x;
        foreach (var (cluster, cw) in MermaidWidth.Measured(text))
        {
            if (cw == 0) continue;
            canvas.Set(cur, y, cluster, cls);
            for (var k = 1; k < cw; k++) canvas.Set(cur + k, y, Cont, cls);
            cur += cw;
        }
    }

    /// <summary>Paint <paramref name="text"/>, clearing any edge bits underneath first.</summary>
    public static void DrawTextOverEdges(MermaidCanvas canvas, string text, int x, int y, string cls)
    {
        var cur = x;
        foreach (var (cluster, cw) in MermaidWidth.Measured(text))
        {
            if (cw == 0) continue;
            for (var k = 0; k < cw; k++)
            {
                if (cur + k < canvas.W && y < canvas.H)
                {
                    var i = canvas.Idx(cur + k, y);
                    if (canvas.InGrid(i)) canvas.Mask[i] = 0;
                }
                canvas.Set(cur + k, y, k == 0 ? cluster : Cont, cls);
            }
            cur += cw;
        }
    }

    public static string MaskChar(int mask) => mask switch
    {
        0 => " ",
        U or D or U | D => "│",
        L or R or L | R => "─",
        D | R => "┌",
        D | L => "┐",
        U | R => "└",
        U | L => "┘",
        U | D | R => "├",
        U | D | L => "┤",
        D | L | R => "┬",
        U | L | R => "┴",
        _ => "┼",
    };

    public static string DottedChar(string c) => c switch { "─" => "╌", "│" => "╎", _ => c };

    public static string ThickChar(string c) => c switch
    {
        "─" => "━",
        "│" => "┃",
        "┌" => "┏",
        "┐" => "┓",
        "└" => "┗",
        "┘" => "┛",
        "├" => "┣",
        "┤" => "┫",
        "┬" => "┳",
        "┴" => "┻",
        "┼" => "╋",
        _ => c,
    };

    public static string FlipGlyphV(string c) => c switch
    {
        "┌" => "└",
        "└" => "┌",
        "┐" => "┘",
        "┘" => "┐",
        "┏" => "┗",
        "┗" => "┏",
        "┓" => "┛",
        "┛" => "┓",
        "╭" => "╰",
        "╰" => "╭",
        "╮" => "╯",
        "╯" => "╮",
        "┬" => "┴",
        "┴" => "┬",
        "┳" => "┻",
        "┻" => "┳",
        "▼" => "▲",
        "▲" => "▼",
        "▽" => "△",
        "△" => "▽",
        _ => c,
    };

    public static string FlipGlyphH(string c) => c switch
    {
        "┌" => "┐",
        "┐" => "┌",
        "└" => "┘",
        "┘" => "└",
        "┏" => "┓",
        "┓" => "┏",
        "┗" => "┛",
        "┛" => "┗",
        "╭" => "╮",
        "╮" => "╭",
        "╰" => "╯",
        "╯" => "╰",
        "├" => "┤",
        "┤" => "├",
        "┣" => "┫",
        "┫" => "┣",
        "▶" => "◄",
        "◄" => "▶",
        "▷" => "◁",
        "◁" => "▷",
        _ => c,
    };
}
