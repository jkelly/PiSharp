// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): coding-agent/src/modes/interactive/interactive-mode.ts
// (handleClipboardPaste, handleRightClickPaste's clipboard read, quoteIfNeeded), extracted so the Ctrl+V flow can be tested with
// fake clipboard sources. InteractiveMode wires an IClipboardPasteHost over its editor, TUI and showError.
using System.Text.RegularExpressions;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>The members of InteractiveMode that handleClipboardPaste touches.</summary>
internal interface IClipboardPasteHost
{
    bool IsBashMode { get; }
    /// <summary><c>editor.getCursor?.()</c>; null when the editor has no cursor API.</summary>
    (int Line, int Col)? GetCursor();
    string GetText();
    void InsertTextAtCursor(string text);
    void RequestRender();
    void ShowError(string message);
}

/// <summary>The clipboard readers handleClipboardPaste uses; defaults read the system clipboard.</summary>
internal sealed class ClipboardPasteSources
{
    public Func<Task<IReadOnlyList<string>?>> ReadClipboardFilePaths { get; init; } = () => Clipboard.ReadClipboardFilePaths();
    public Func<Task<ClipboardImage?>> ReadClipboardImage { get; init; } = () => ClipboardImageReader.ReadClipboardImage();
    public Func<Task<string?>> ReadClipboardText { get; init; } = () => Clipboard.ReadClipboardText();
    public string TempDirectory { get; init; } = Path.GetTempPath();
    public static ClipboardPasteSources Default { get; } = new();
}

internal static partial class ClipboardPaste
{
    [GeneratedRegex(@"[^a-zA-Z0-9_\-./~:@]")]
    private static partial Regex UnsafeShellCharacter();

    /// <summary>Source <c>quoteIfNeeded</c>: single-quotes a value unless it is non-empty and shell-safe.</summary>
    public static string QuoteIfNeeded(string value)
    {
        if (value.Length > 0 && !UnsafeShellCharacter().IsMatch(value)) return value;
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Ctrl+V: copied files use their original paths, images are attached via temporary files, and plain text is the
    /// final fallback.</summary>
    public static async Task HandleClipboardPaste(IClipboardPasteHost host, ClipboardPasteSources? sources = null)
    {
        sources ??= ClipboardPasteSources.Default;
        try
        {
            var filePaths = await sources.ReadClipboardFilePaths().ConfigureAwait(false);
            if (filePaths is not null)
            {
                if (filePaths.Any(filePath => filePath.Any(char.IsControl)))
                    throw new InvalidOperationException("Clipboard file path contains control characters");
                var paths = host.IsBashMode ? string.Join(" ", filePaths.Select(QuoteIfNeeded)) : string.Join("\n", filePaths);
                var cursor = host.GetCursor();
                var lines = cursor is not null ? host.GetText().Split('\n') : [];
                var currentLine = cursor is { } c && c.Line >= 0 && c.Line < lines.Length ? lines[c.Line] : "";
                char? before = cursor is { Col: > 0 } at && at.Col - 1 < currentLine.Length ? currentLine[at.Col - 1] : null;
                char? after = cursor is { } position && position.Col >= 0 && position.Col < currentLine.Length ? currentLine[position.Col] : null;
                var leadingSpace = before is { } b && !PiSharp.Tui.Pi.TextUtils.IsJsWhitespace(b) ? " " : "";
                var trailingSpace = after is { } a && !PiSharp.Tui.Pi.TextUtils.IsJsWhitespace(a) ? " " : "";
                host.InsertTextAtCursor($"{leadingSpace}{paths}{trailingSpace}");
                host.RequestRender();
                return;
            }

            var image = await sources.ReadClipboardImage().ConfigureAwait(false);
            if (image is not null)
            {
                var ext = ClipboardImageReader.ExtensionForImageMimeType(image.MimeType) ?? "png";
                var fileName = $"pi-clipboard-{Guid.NewGuid()}.{ext}";
                var filePath = Path.Combine(sources.TempDirectory, fileName);
                await File.WriteAllBytesAsync(filePath, image.Bytes).ConfigureAwait(false);
                host.InsertTextAtCursor(filePath);
                host.RequestRender();
                return;
            }

            var text = await sources.ReadClipboardText().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(text))
            {
                host.InsertTextAtCursor(text);
                host.RequestRender();
            }
        }
        catch (Exception error)
        {
            host.ShowError($"Failed to paste from clipboard: {error.Message}");
        }
    }
}
