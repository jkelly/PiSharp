using System.Collections.Immutable;

namespace PiSharp.Tui.Components.Text;

/// <summary>
/// Pi v0.99.1 Text translation for one UI owner. Render returns immutable Source comparison rows;
/// use TerminalTextFrameFactory for terminal output. The component owns no terminal or async resource.
/// </summary>
public sealed class TerminalText
{
    private string text;
    private readonly int paddingX, paddingY;
    private Func<string, string>? customBackground;
    private string? cachedText;
    private int cachedWidth;
    private ImmutableArray<string> cachedRows;

    public TerminalText(string text = "", int paddingX = 1, int paddingY = 1,
        Func<string, string>? customBackground = null)
    {
        TerminalTextComponentBounds.ValidateText(text);
        TerminalTextComponentBounds.ValidatePadding(paddingX, paddingY);
        this.text = text; this.paddingX = paddingX; this.paddingY = paddingY;
        this.customBackground = customBackground;
    }

    public void SetText(string text)
    { TerminalTextComponentBounds.ValidateText(text); this.text = text; Invalidate(); }

    public void SetCustomBackground(Func<string, string>? customBackground)
    { this.customBackground = customBackground; Invalidate(); }

    public void Invalidate() { cachedText = null; cachedRows = default; }

    public ImmutableArray<string> Render(int width)
    {
        TerminalTextComponentBounds.ValidateWidth(width);
        if (!cachedRows.IsDefault && cachedText == text && cachedWidth == width) return cachedRows;
        var result = RenderCore(text, width, sourceBackground: true);
        // Source records the current text after callbacks, even when a callback invoked SetText.
        cachedText = text; cachedWidth = width; cachedRows = result;
        return result;
    }

    internal ImmutableArray<string> RenderForFrame(int width)
    {
        TerminalTextComponentBounds.ValidateWidth(width);
        if (TerminalTextSource.IsBlank(text)) return [];
        // Independent of the raw cache and of callbacks: controls are data before width/wrap decisions.
        return RenderCore(TerminalTextSource.SafeMultiline(text), width, sourceBackground: false);
    }

    private ImmutableArray<string> RenderCore(string input, int width, bool sourceBackground)
    {
        if (TerminalTextSource.IsBlank(input)) return [];
        var horizontal = Math.Min(paddingX, Math.Max(0, (width - 1) / 2));
        var contentWidth = Math.Max(1, width - horizontal * 2);
        var wrapped = TerminalTextSource.Wrap(input.Replace("\t", "   ", StringComparison.Ordinal), contentWidth);
        if (wrapped.Length > TerminalTextComponentBounds.MaximumRows - paddingY * 2)
            throw TerminalTextComponentBounds.Limit();
        var margin = new string(' ', horizontal);
        var content = new TerminalTextComponentBounds.Rows();
        foreach (var line in wrapped) content.Add(Background(margin + line + margin));
        var empty = new string(' ', width);
        var padding = new TerminalTextComponentBounds.Rows();
        for (var i = 0; i < paddingY; i++) padding.Add(Background(empty));
        var result = new TerminalTextComponentBounds.Rows();
        var paddingRows = padding.ToArray();
        foreach (var row in paddingRows) result.Add(row);
        foreach (var row in content.ToArray()) result.Add(row);
        foreach (var row in paddingRows) result.Add(row); // Source invokes padding callbacks once, reuses both sides.
        return result.ToArray().ToImmutableArray();

        string Background(string line)
        {
            var padded = TerminalTextSource.Pad(line, width);
            // Look up the current callback per row, matching Source callback replacement during rendering.
            return sourceBackground && customBackground is { } transform ? transform(padded) : padded;
        }
    }
}
