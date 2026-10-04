using System.Collections.Immutable;
using System.Text;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

namespace PiSharp.Tui.Components.Text;

/// <summary>
/// Inert Source-width frames; control projection precedes layout and arbitrary background callbacks
/// are excluded. The host owns scrolling, focus, terminal I/O, invalidation and console restoration.
/// </summary>
public static class TerminalTextFrameFactory
{
    public static TerminalFrame Create(TerminalText component, int viewportRows, int columns,
        TerminalRenderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        var bounds = Validate(viewportRows, columns, limits);
        return Create(component.RenderForFrame(columns), viewportRows, columns, bounds);
    }
    public static TerminalFrame Create(TerminalTruncatedText component, int viewportRows, int columns,
        TerminalRenderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        var bounds = Validate(viewportRows, columns, limits);
        return Create(component.RenderForFrame(columns), viewportRows, columns, bounds);
    }
    private static TerminalRenderLimits Validate(int rows, int columns, TerminalRenderLimits? limits)
    {
        var bounds = limits ?? new(); bounds.Validate(); bounds.ValidateViewport(rows, columns);
        TerminalTextComponentBounds.ValidateWidth(columns); return bounds;
    }
    private static TerminalFrame Create(ImmutableArray<string> source, int viewportRows, int columns,
        TerminalRenderLimits bounds)
    {
        long input = 0;
        foreach (var row in source)
        { input += row.Length; if (input > bounds.MaximumInputCharacters) throw TerminalTextComponentBounds.Limit(); }
        var rows = ImmutableArray.CreateBuilder<TerminalFrameRow>(viewportRows);
        var clipped = source.Length > viewportRows; var characters = 0; var largest = 0;
        for (var at = 0; at < viewportRows; at++)
        {
            var text = at < source.Length ? source[at] : "";
            var accepted = new StringBuilder(); var width = 0;
            foreach (var segment in TerminalSourceGraphemeSegmenter.Elements(text))
            {
                if (segment.Length > bounds.MaximumGraphemeCharacters) throw TerminalTextComponentBounds.Limit();
                var cells = TerminalTextSource.Width(segment);
                if (cells > columns - width) { clipped = true; break; }
                if (characters > bounds.MaximumFrameCharacters - segment.Length) throw TerminalTextComponentBounds.Limit();
                accepted.Append(segment); width += cells; characters += segment.Length;
                largest = Math.Max(largest, segment.Length);
            }
            rows.Add(new(accepted.ToString(), width));
        }
        var frame = new TerminalFrame(columns, rows.MoveToImmutable(), new(Visible: false),
            "pisharp-text-source-safe-v1", clipped, characters, largest,
            positionCursor: false, isSourcePresentation: true);
        if (VtRenderer.FullFrameCharacterCount(frame) > bounds.MaximumFrameCharacters) throw TerminalTextComponentBounds.Limit();
        return frame;
    }
}
