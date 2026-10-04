using System.Collections.Immutable;

namespace PiSharp.Tui.Components.Text;

/// <summary>Pi's first-LF single-row truncation, including its default three-dot ellipsis and padding.</summary>
public sealed class TerminalTruncatedText
{
    private readonly string text;
    private readonly int paddingX, paddingY;

    public TerminalTruncatedText(string text, int paddingX = 0, int paddingY = 0)
    {
        TerminalTextComponentBounds.ValidateText(text);
        TerminalTextComponentBounds.ValidatePadding(paddingX, paddingY);
        this.text = text; this.paddingX = paddingX; this.paddingY = paddingY;
    }
    public void Invalidate() { } // Source has no cache or setters.
    public ImmutableArray<string> Render(int width) => RenderCore(width, safe: false);
    internal ImmutableArray<string> RenderForFrame(int width) => RenderCore(width, safe: true);

    private ImmutableArray<string> RenderCore(int width, bool safe)
    {
        TerminalTextComponentBounds.ValidateWidth(width);
        var rows = new TerminalTextComponentBounds.Rows();
        var empty = new string(' ', width);
        for (var i = 0; i < paddingY; i++) rows.Add(empty);
        var firstLf = text.IndexOf('\n');
        var firstLine = firstLf < 0 ? text : text[..firstLf]; // A lone CR is content in Source TruncatedText.
        if (safe) firstLine = TerminalTextSource.SafeRow(firstLine);
        var available = Math.Max(1, width - paddingX * 2);
        var display = TerminalTextSource.Truncate(firstLine, available, sourceReset: !safe);
        var margin = new string(' ', paddingX); // Source does not shrink TruncatedText's margins.
        rows.Add(TerminalTextSource.Pad(margin + display + margin, width));
        for (var i = 0; i < paddingY; i++) rows.Add(empty);
        return rows.ToArray().ToImmutableArray();
    }
}
