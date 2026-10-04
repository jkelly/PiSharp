using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace PiSharp.Tui.Rendering;

/// <summary>
/// Bounded unstyled text layout. StringInfo segmentation is a runtime policy, not pinned Pi Unicode parity.
/// The caller must explicitly supply a display-cell width policy for its terminal.
/// </summary>
public sealed class TerminalTextLayout
{
    private readonly ITerminalWidthPolicy widthPolicy;
    private readonly TerminalRenderLimits limits;
    private readonly string widthPolicyId;

    public TerminalTextLayout(ITerminalWidthPolicy widthPolicy, TerminalRenderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(widthPolicy);
        this.limits = limits ?? new(); this.limits.Validate();
        widthPolicyId = widthPolicy.Id;
        if (string.IsNullOrWhiteSpace(widthPolicyId) || widthPolicyId.Length > 128)
            throw new TerminalRenderException(TerminalRenderFailure.InvalidOptions);
        this.widthPolicy = widthPolicy;
    }

    public TerminalFrame CreateFrame(string text, int rows, int columns, TerminalCursor? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(text); limits.ValidateViewport(rows, columns);
        if (text.Length > limits.MaximumInputCharacters) throw Error(TerminalRenderFailure.ResourceLimit);
        var position = cursor ?? new();
        if (position.Row < 0 || position.Row >= rows || position.Column < 0 || position.Column >= columns)
            throw Error(TerminalRenderFailure.InvalidCursor);
        var clean = Normalize(text);
        var content = new StringBuilder[rows]; var widths = new int[rows];
        // Cell ownership stays bounded even for many zero-width input clusters.
        var continuation = new bool[rows][];
        for (var index = 0; index < rows; index++) { content[index] = new(); continuation[index] = new bool[columns]; }
        var row = 0; var characters = 0; var largestGrapheme = 0; var clipped = false;
        var elements = StringInfo.GetTextElementEnumerator(clean);
        while (elements.MoveNext())
        {
            var grapheme = elements.GetTextElement();
            if (grapheme.Length > limits.MaximumGraphemeCharacters) throw Error(TerminalRenderFailure.ResourceLimit);
            if (grapheme == "\n")
            {
                row++;
                // A final newline may end precisely at the bottom edge without losing visible content.
                continue;
            }
            if (row >= rows) { clipped = true; break; }
            var width = widthPolicy.GetWidth(grapheme);
            if (width < 0 || width > limits.MaximumColumns) throw Error(TerminalRenderFailure.InvalidWidth);
            // A zero-cell standalone cluster has no safe cell owner in this unstyled viewport profile.
            if (width == 0) continue;
            if (width > columns) { clipped = true; continue; }
            if (widths[row] + width > columns)
            {
                if (++row >= rows) { clipped = true; break; }
            }
            if (characters > limits.MaximumFrameCharacters - grapheme.Length)
                throw Error(TerminalRenderFailure.ResourceLimit);
            content[row].Append(grapheme); characters += grapheme.Length;
            largestGrapheme = Math.Max(largestGrapheme, grapheme.Length);
            for (var offset = 1; offset < width; offset++) continuation[row][widths[row] + offset] = true;
            widths[row] += width;
        }
        if (continuation[position.Row][position.Column]) throw Error(TerminalRenderFailure.InvalidCursor);
        var result = ImmutableArray.CreateBuilder<TerminalFrameRow>(rows);
        for (var index = 0; index < rows; index++) result.Add(new(content[index].ToString(), widths[index]));
        return new(columns, result.MoveToImmutable(), position, widthPolicyId, clipped, characters, largestGrapheme);
    }

    private static string Normalize(string text)
    {
        // The input bound also bounds normalization to at most three times that many UTF-16 code units.
        var clean = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (char.IsHighSurrogate(value))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    throw Error(TerminalRenderFailure.InvalidUnicode);
                clean.Append(value).Append(text[++index]); continue;
            }
            if (char.IsLowSurrogate(value)) throw Error(TerminalRenderFailure.InvalidUnicode);
            if (value == '\r') { clean.Append('\n'); if (index + 1 < text.Length && text[index + 1] == '\n') index++; }
            else if (value == '\n') clean.Append('\n');
            else if (value == '\t') clean.Append("   ");
            else if (value < 0x20 || value is >= '\u007f' and <= '\u009f') clean.Append('?');
            else clean.Append(value);
        }
        return clean.ToString();
    }

    private static TerminalRenderException Error(TerminalRenderFailure failure) => new(failure);

    // Editor source spans are normalized before trusted styling, never parsed for ANSI authority.
    internal static string EscapeSourceSpan(string text)
    {
        var safe = new StringBuilder(text.Length);
        foreach (var scalar in text.EnumerateRunes())
        {
            if (scalar.Value == '\t') safe.Append("   ");
            else if (scalar.Value < 0x20 || scalar.Value is >= 0x7f and <= 0x9f)
                safe.Append("\\u").Append(scalar.Value.ToString("x4", CultureInfo.InvariantCulture));
            else if (scalar.Value == 0x0e33) safe.Append("\u0e4d\u0e32");
            else if (scalar.Value == 0x0eb3) safe.Append("\u0ecd\u0eb2");
            else safe.Append(scalar.ToString());
        }
        return safe.ToString();
    }

    internal static string EscapeTranscriptRow(string text)
    {
        TerminalTextEditorUnicode(text);
        var safe = new StringBuilder(text.Length);
        foreach (var scalar in text.EnumerateRunes())
        {
            // The actual view wraps its declared inert ASCII projection before this boundary.
            // Preserve that printable data, including its backslashes; raw controls/Unicode
            // supplied by other callers still receive an inert escape projection here.
            if (scalar.Value is >= 0x20 and <= 0x7e) safe.Append(scalar.ToString());
            else safe.Append(scalar.Value <= 0xffff ? "\\u" : "\\U")
                .Append(scalar.Value.ToString(scalar.Value <= 0xffff ? "x4" : "x8", CultureInfo.InvariantCulture));
        }
        return safe.ToString();
    }

    private static void TerminalTextEditorUnicode(string text)
    {
        for (var at = 0; at < text.Length; at++)
        {
            if (char.IsHighSurrogate(text[at]))
            { if (++at >= text.Length || !char.IsLowSurrogate(text[at])) throw Error(TerminalRenderFailure.InvalidUnicode); }
            else if (char.IsLowSurrogate(text[at])) throw Error(TerminalRenderFailure.InvalidUnicode);
        }
    }
}
