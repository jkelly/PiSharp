// lovely-mermaid 0.3.3 (Apache-2.0): src/diagrams/sequence.ts.
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidJs;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidLabels;
using static PiSharp.Cli.Interactive.Mode.LovelyMermaid.MermaidStatements;
namespace PiSharp.Cli.Interactive.Mode.LovelyMermaid;

internal sealed class MermaidActivation(int at, int from)
{
    public int At { get; } = at;
    public int From { get; } = from;
    public int? To { get; set; }
}

internal enum MermaidSeqHead { Arrow, Cross }

internal enum MermaidNoteKind { Over, Left, Right }

/// <summary>Where a note sits; <c>left</c>/<c>right</c> keep their participant in <see cref="From"/>.</summary>
internal sealed record MermaidNoteAnchor(MermaidNoteKind Kind, int From, int To);

internal enum MermaidSeqItemKind { Message, Note, Divider }

internal sealed record MermaidSeqItem(
    MermaidSeqItemKind Kind,
    int From = 0,
    int To = 0,
    string? Text = null,
    bool Dashed = false,
    MermaidSeqHead Head = MermaidSeqHead.Arrow,
    MermaidNoteAnchor? Anchor = null);

internal sealed class MermaidSequence
{
    public List<string> Labels { get; } = [];
    public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);
    public List<MermaidSeqItem> Items { get; } = [];

    public List<MermaidActivation> Activations { get; } = [];
    public string? Truncated { get; set; }
    public List<string> Warnings { get; } = [];
    public void PushItem(MermaidSeqItem item)
    {
        if (Items.Count >= MermaidGraph.MaxEdges) Truncated ??= $"item cap ({MermaidGraph.MaxEdges}) reached";
        else Items.Add(item);
    }
    public void Drop(string st)
    {
        if (Truncated is null) Warnings.Add($"dropped, unreadable statement: \"{st}\"");
    }
    public void Activate(int at, int from) => Activations.Add(new(at, from));
    public void Deactivate(int at, int to)
    {
        for (var i = Activations.Count - 1; i >= 0; i--)
            if (Activations[i].At == at && Activations[i].To is null) { Activations[i].To = to; return; }
    }

    /// <summary>Index of participant <paramref name="id"/>, creating it if new; -1 past the node cap.</summary>
    public int Participant(string id, string? label)
    {
        if (Index.TryGetValue(id, out var existing))
        {
            if (label is not null) Labels[existing] = label;
            return existing;
        }
        if (Labels.Count >= MermaidGraph.MaxNodes)
        {
            Truncated ??= $"participant cap ({MermaidGraph.MaxNodes}) reached";
            return -1;
        }
        Index[id] = Labels.Count;
        Labels.Add(label ?? id);
        return Labels.Count - 1;
    }
}

internal static class MermaidSequenceParser
{
    /// <summary>Message operators, longest-first so <c>--&gt;&gt;</c> wins over <c>--&gt;</c>.</summary>
    private static readonly (string Op, bool Dashed, MermaidSeqHead Head)[] SeqOps =
    [
        ("-->>", true, MermaidSeqHead.Arrow),
        ("->>", false, MermaidSeqHead.Arrow),
        ("--x", true, MermaidSeqHead.Cross),
        ("-x", false, MermaidSeqHead.Cross),
        ("--)", true, MermaidSeqHead.Arrow),
        ("-)", false, MermaidSeqHead.Arrow),
        ("-->", true, MermaidSeqHead.Arrow),
        ("->", false, MermaidSeqHead.Arrow),
    ];

    private const int MaxSeqOp = 4;

    private static readonly string[] SeqSkipped = ["create", "destroy", "title", "acctitle", "accdescr", "links", "link", "properties"];
    private static readonly string[] SeqBlocks = ["loop", "alt", "opt", "par", "critical", "break", "else", "and", "option"];
    private static readonly string[] SeqContinuations = ["else", "and", "option"];

    public static MermaidDrawn? Render(string src)
    {
        var seq = Parse(src);
        if (seq is null) return null;
        var canvas = MermaidLayoutSeq.LayoutSequence(seq);
        return canvas is null ? null : new(canvas, seq.Warnings, []);
    }

    public static MermaidSequence? Parse(string src)
    {
        var statements = StatementsOf(src);
        if (HeaderKind(statements) != "sequencediagram") return null;
        var seq = new MermaidSequence();
        var autonumber = false;
        var msgCount = 0;
        var blocks = new List<bool>();
        foreach (var st in statements.Skip(1))
        {
            var first = FirstWord(st);
            var lower = AsciiLower(first);
            if (lower is "participant" or "actor")
            {
                var rest = Trim(st[first.Length..]);
                if (rest == "") seq.Drop(st);
                else
                {
                    var a = SplitOnce(rest, " as ");
                    seq.Participant(a is {} pair ? Trim(pair.Item1) : rest, a is {} pair2 ? CleanLabel(pair2.Item2) : null);
                }
            }
            else if (lower == "autonumber") autonumber = true;
            else if (lower is "activate" or "deactivate")
            {
                if (seq.Index.TryGetValue(Trim(st[first.Length..]), out var who) && seq.Items.Count > 0)
                {
                    if (lower == "activate") seq.Activate(who, seq.Items.Count - 1);
                    else seq.Deactivate(who, seq.Items.Count - 1);
                }
            }
            else if (SeqSkipped.Contains(lower)) { }
            else if (lower == "note")
            {
                if (ParseNoteAnchor(Trim(st[first.Length..]), seq) is {} note)
                    seq.PushItem(new(MermaidSeqItemKind.Note, Text: note.Text, Anchor: note.Anchor));
                else seq.Drop(st);
            }
            else if (SeqBlocks.Contains(lower))
            {
                var continues = SeqContinuations.Contains(lower);
                if (!continues) blocks.Add(true);
                if (!continues || (blocks.Count > 0 && blocks[^1]))
                    seq.PushItem(new(MermaidSeqItemKind.Divider, Text: DecodeHtmlEntities(st)));
            }
            else if (lower is "rect" or "box") blocks.Add(false);
            else if (lower == "end")
            {
                if (blocks.Count > 0)
                {
                    var opened = blocks[^1];
                    blocks.RemoveAt(blocks.Count - 1);
                    if (opened) seq.PushItem(new(MermaidSeqItemKind.Divider, Text: "end"));
                }
            }
            else if (ParseSeqMessage(st, seq) is {} msg)
            {
                var text = msg.Text;
                if (autonumber) { msgCount++; text = text is null ? $"{msgCount}." : $"{msgCount}. {text}"; }
                var item = seq.Items.Count;
                seq.PushItem(new(MermaidSeqItemKind.Message, msg.From, msg.To, text, msg.Dashed, msg.Head));
                if (seq.Items.Count == item + 1)
                {
                    if (msg.Marks.Contains('+')) seq.Activate(msg.To, item);
                    if (msg.Marks.Contains('-')) seq.Deactivate(msg.From, item);
                }
            }
            else seq.Drop(st);
            if (seq.Truncated is not null)
            {
                seq.Warnings.Add($"diagram truncated: {seq.Truncated}");
                break;
            }
        }
        return seq.Labels.Count == 0 ? null : seq;
    }

    private static (string Text, MermaidNoteAnchor Anchor)? ParseNoteAnchor(string rest, MermaidSequence seq)
    {
        var lower = AsciiLower(rest);
        MermaidNoteKind kind;
        string idsAndText;
        if (lower.StartsWith("over ", StringComparison.Ordinal))
        {
            kind = MermaidNoteKind.Over;
            idsAndText = rest["over ".Length..];
        }
        else if (lower.StartsWith("left of ", StringComparison.Ordinal))
        {
            kind = MermaidNoteKind.Left;
            idsAndText = rest["left of ".Length..];
        }
        else if (lower.StartsWith("right of ", StringComparison.Ordinal))
        {
            kind = MermaidNoteKind.Right;
            idsAndText = rest["right of ".Length..];
        }
        else
        {
            return null;
        }

        if (SplitOnce(idsAndText, ":") is not (var ids, var body)) return null;
        var text = DecodeHtmlEntities(Trim(body));
        var parts = ids.Split(',').Select(Trim).Where(s => s != "").ToList();
        if (parts.Count == 0) return null;
        var a = seq.Participant(parts[0], null);
        if (a == -1) return null;

        if (kind != MermaidNoteKind.Over) return (text, new(kind, a, a));
        var b = a;
        if (parts.Count > 1)
        {
            var second = seq.Participant(parts[1], null);
            if (second == -1) return null;
            b = second;
        }
        return (text, new(MermaidNoteKind.Over, Math.Min(a, b), Math.Max(a, b)));
    }

    private static (int From, int To, string? Text, bool Dashed, MermaidSeqHead Head, string Marks)? ParseSeqMessage(string st, MermaidSequence seq)
    {
        var chars = CodePoints(st);
        (int Pos, string Op, bool Dashed, MermaidSeqHead Head)? found = null;
        for (var pos = 0; pos < chars.Length && found is null; pos++)
        {
            var tail = Join(chars, pos, pos + MaxSeqOp);
            foreach (var (op, dashed, head) in SeqOps)
            {
                if (tail.StartsWith(op, StringComparison.Ordinal))
                {
                    var after = At(chars, pos + op.Length);
                    if ((op.EndsWith('x') || op.EndsWith(')')) && (after is "-" or ">")) continue;
                    found = (pos, op, dashed, head);
                    break;
                }
            }
        }
        if (found is not { } hit) return null;

        var fromId = Trim(Join(chars, 0, hit.Pos));
        if (fromId == "") return null;
        var afterOp = TrimStart(Join(chars, hit.Pos + hit.Op.Length));
        var markCount = 0;
        while (markCount < afterOp.Length && afterOp[markCount] is '+' or '-') markCount++;
        var marks = afterOp[..markCount];
        var rest = afterOp[markCount..];

        var split = SplitOnce(rest, ":");
        var toId = Trim(split is { } s1 ? s1.Item1 : rest);
        var text = split is { } s2 ? NonEmpty(DecodeHtmlEntities(Trim(s2.Item2))) : null;
        if (toId == "") return null;

        var from = seq.Participant(fromId, null);
        if (from == -1) return null;
        var to = seq.Participant(toId, null);
        if (to == -1) return null;
        return (from, to, text, hit.Dashed, hit.Head, marks);
    }
}
