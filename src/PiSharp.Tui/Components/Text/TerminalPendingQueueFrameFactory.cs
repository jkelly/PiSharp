using System.Collections.Immutable;
using System.Text;
using PiSharp.Tui.Input;
using PiSharp.Tui.Rendering;

namespace PiSharp.Tui.Components.Text;

/// <summary>Inert TruncatedText queue rows in the transcript slots above an existing Source editor frame.</summary>
public static class TerminalPendingQueueFrameFactory
{
    public static TerminalEditorRenderedFrame Create(TerminalEditorRenderedFrame editor,
        ImmutableArray<string> steering, ImmutableArray<string> followUp, TerminalRenderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var original = editor.Frame; var bounds = limits ?? new(); bounds.Validate();
        bounds.ValidateViewport(original.Rows.Length, original.Columns); TerminalTextComponentBounds.ValidateWidth(original.Columns);
        if (editor.EditorRowOrigin < 0 || editor.EditorRowOrigin > original.Rows.Length)
            throw new ArgumentOutOfRangeException(nameof(editor));
        Validate(steering, "Steering: "); Validate(followUp, "Follow-up: ");
        if (steering.Length + followUp.Length == 0) return editor;

        var visible = Math.Min(editor.EditorRowOrigin, steering.Length + followUp.Length);
        var rows = original.Rows.ToBuilder(); var clipped = original.IsClipped || visible < steering.Length + followUp.Length;
        for (var at = 0; at < visible; at++)
        {
            var text = at < steering.Length ? "Steering: " + steering[at] : "Follow-up: " + followUp[at - steering.Length];
            var lf = text.IndexOf('\n'); var first = lf < 0 ? text : text[..lf];
            clipped |= lf >= 0 || TerminalTextSource.Width(TerminalTextSource.SafeRow(first)) > Math.Max(1, original.Columns - 2);
            var row = new TerminalTruncatedText(text, 1, 0).RenderForFrame(original.Columns)[0];
            var accepted = new StringBuilder(); var width = 0;
            foreach (var segment in TerminalSourceGraphemeSegmenter.Elements(row))
            {
                var cells = TerminalTextSource.Width(segment);
                if (cells > original.Columns - width) { clipped = true; break; }
                accepted.Append(segment); width += cells;
            }
            rows[editor.EditorRowOrigin - visible + at] = new(accepted.ToString(), width);
        }
        var characters = 0; var largest = 0;
        foreach (var row in rows)
        {
            if (characters > bounds.MaximumInputCharacters - row.Text.Length || characters > bounds.MaximumFrameCharacters - row.Text.Length)
                throw TerminalTextComponentBounds.Limit();
            characters += row.Text.Length;
            foreach (var segment in TerminalSourceGraphemeSegmenter.Elements(row.Text))
            {
                if (segment.Length > bounds.MaximumGraphemeCharacters) throw TerminalTextComponentBounds.Limit();
                largest = Math.Max(largest, segment.Length);
            }
        }
        var frame = new TerminalFrame(original.Columns, rows.ToImmutable(), original.Cursor,
            original.WidthPolicyId + "+pending-truncated-v1", clipped, characters, largest,
            original.InverseSpan, original.PositionCursor, original.IsSourcePresentation);
        if (VtRenderer.FullFrameCharacterCount(frame) > bounds.MaximumFrameCharacters) throw TerminalTextComponentBounds.Limit();
        return editor with { Frame = frame };
    }

    private static void Validate(ImmutableArray<string> queue, string prefix)
    {
        if (queue.IsDefault || queue.Length > 256) throw TerminalTextComponentBounds.Limit();
        var characters = 0;
        foreach (var text in queue)
        {
            TerminalTextComponentBounds.ValidateText(text);
            if (text.Length > TerminalTextComponentBounds.MaximumTextCharacters - prefix.Length || characters > 1_048_576 - text.Length)
                throw TerminalTextComponentBounds.Limit();
            characters += text.Length;
        }
    }
}
