// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/tui-main-screen.ts.
using System.Globalization;
using System.Text;

namespace PiSharp.Tui.Pi;

/// <summary>Streams terminal output in 1 MiB chunks, never splitting a surrogate pair.</summary>
internal sealed class BoundedTerminalWriter(Action<string> write)
{
    private const int MaxChars = 1024 * 1024;
    private readonly StringBuilder buffer = new();
    private long written;
    public void Append(string value)
    {
        var offset = 0;
        while (offset < value.Length)
        {
            var capacity = MaxChars - buffer.Length;
            if (capacity == 0) { Flush(); continue; }
            var end = Math.Min(value.Length, offset + capacity);
            if (end < value.Length && char.IsHighSurrogate(value[end - 1]) && char.IsLowSurrogate(value[end])) end--;
            if (end == offset) { Flush(); continue; }
            buffer.Append(value, offset, end - offset); offset = end;
            if (buffer.Length == MaxChars) Flush();
        }
    }
    public void Flush()
    {
        if (buffer.Length == 0) return;
        write(buffer.ToString()); written += buffer.Length; buffer.Clear();
    }
    public long Length => written + buffer.Length;
}

public sealed record TuiMainScreenRenderState(List<string> PreviousLines, int PreviousWidth, int PreviousHeight, int CursorRow, int HardwareCursorRow, int MaxLinesRendered, int PreviousViewportTop);

/// <summary>The regular TUI: renders into the terminal's main screen and scrollback with differential updates.</summary>
public sealed class TuiMainScreen : TuiBase
{
    public override TuiMode Mode => TuiMode.Regular;
    private List<string> previousLines = [];
    private HashSet<long> previousKittyIds = [];
    private int previousWidth, previousHeight, cursorRow, hardwareCursorRow, maxLinesRendered, previousViewportTop;
    private readonly Func<string, string?> env;

    public TuiMainScreen(ITerminal terminal, UiLoop loop, bool? showHardwareCursor = null, string? logDirectory = null, Func<string, string?>? environment = null)
        : base(terminal, loop, showHardwareCursor, logDirectory) => env = environment ?? Environment.GetEnvironmentVariable;

    public TuiMainScreenRenderState CaptureRenderState() =>
        new([.. previousLines], previousWidth, previousHeight, cursorRow, hardwareCursorRow, maxLinesRendered, previousViewportTop);

    public void RestoreRenderState(TuiMainScreenRenderState state)
    {
        previousLines = state.PreviousLines.Select(line => TerminalImage.IsImageLine(line) ? "" : line).ToList();
        previousKittyIds = [];
        previousWidth = state.PreviousWidth; previousHeight = state.PreviousHeight; cursorRow = state.CursorRow;
        hardwareCursorRow = state.HardwareCursorRow; maxLinesRendered = state.MaxLinesRendered; previousViewportTop = state.PreviousViewportTop;
    }

    protected override void ResetRenderState()
    {
        previousLines = []; previousWidth = -1; previousHeight = -1; cursorRow = 0; hardwareCursorRow = 0; maxLinesRendered = 0; previousViewportTop = 0;
    }

    protected override void BeforeTerminalStop(bool preserveScreen)
    {
        if (preserveScreen || previousLines.Count == 0) return;
        Terminal.Write(" ");
        var lineDiff = previousLines.Count - hardwareCursorRow;
        if (lineDiff > 0) Terminal.Write($"\u001b[{lineDiff}B");
        else if (lineDiff < 0) Terminal.Write($"\u001b[{-lineDiff}A");
        Terminal.Write("\r\n");
    }

    private static (List<long> Ids, int Rows) KittyHeader(string line)
    {
        var start = line.IndexOf("\u001b_G", StringComparison.Ordinal);
        if (start == -1) return ([], 1);
        var paramsStart = start + 3; var paramsEnd = line.IndexOf(';', paramsStart);
        if (paramsEnd == -1) return ([], 1);
        var ids = new List<long>(); var rows = 1;
        foreach (var param in line[paramsStart..paramsEnd].Split(','))
        {
            var parts = param.Split('=', 2);
            if (parts.Length < 2 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0 || value > 0xffffffffL) continue;
            if (parts[0] == "i") ids.Add(value); else if (parts[0] == "r") rows = (int)Math.Min(value, int.MaxValue);
        }
        return (ids, rows);
    }

    private static HashSet<long> CollectKittyIds(List<string> lines)
    {
        var ids = new HashSet<long>();
        foreach (var line in lines) foreach (var id in KittyHeader(line).Ids) ids.Add(id);
        return ids;
    }
    private static string DeleteKittyImages(IEnumerable<long> ids) => string.Concat(ids.Select(TerminalImage.DeleteKittyImage));

    private static int ReservedRows(List<string> lines, int index, int maxIndex = int.MaxValue)
    {
        var rows = KittyHeader(index < lines.Count ? lines[index] : "").Rows;
        if (rows <= 1) return 1;
        if (maxIndex == int.MaxValue) maxIndex = lines.Count - 1;
        var maxRows = Math.Min(rows, Math.Min(maxIndex - index + 1, lines.Count - index));
        var reserved = 1;
        while (reserved < maxRows)
        {
            var line = index + reserved < lines.Count ? lines[index + reserved] : "";
            if (TerminalImage.IsImageLine(line) || TextUtils.VisibleWidth(line) > 0) break;
            reserved++;
        }
        return reserved;
    }

    private (int First, int Last) ExpandForKitty(int firstChanged, int lastChanged, List<string> newLines)
    {
        var first = firstChanged; var last = lastChanged;
        void Expand(List<string> lines)
        {
            for (var i = 0; i < lines.Count; i++)
            {
                if (KittyHeader(lines[i]).Ids.Count == 0) continue;
                var blockEnd = i + ReservedRows(lines, i) - 1;
                if (i >= firstChanged || i <= lastChanged && blockEnd >= firstChanged) { first = Math.Min(first, i); last = Math.Max(last, blockEnd); }
            }
        }
        Expand(previousLines); Expand(newLines);
        return (first, last);
    }

    private string DeleteChangedKittyImages(int first, int last)
    {
        if (first < 0 || last < first) return "";
        var ids = new HashSet<long>();
        for (var i = first; i <= Math.Min(last, previousLines.Count - 1); i++) foreach (var id in KittyHeader(previousLines[i]).Ids) ids.Add(id);
        return DeleteKittyImages(ids);
    }

    protected override void DoRender()
    {
        if (stopped) return;
        var width = Terminal.Columns; var height = Terminal.Rows;
        var widthChanged = previousWidth != 0 && previousWidth != width;
        var heightChanged = previousHeight != 0 && previousHeight != height;
        var previousBufferLength = previousHeight > 0 ? previousViewportTop + previousHeight : height;
        var prevViewportTop = heightChanged ? Math.Max(0, previousBufferLength - height) : previousViewportTop;
        var viewportTop = prevViewportTop;
        var hwRow = hardwareCursorRow;
        int LineDiff(int targetRow) => targetRow - viewportTop - (hwRow - prevViewportTop);

        var newLines = Render(width);
        if (HasOverlayEntries) newLines = CompositeOverlays(newLines, width, height);
        var cursorPos = ExtractCursorPosition(newLines, height);
        newLines = ApplyLineResets(newLines);

        void FullRender(bool clear)
        {
            fullRedrawCount++;
            var output = new BoundedTerminalWriter(Terminal.Write);
            output.Append("\u001b[?2026h");
            if (clear) { output.Append(DeleteKittyImages(previousKittyIds)); output.Append("\u001b[2J\u001b[H\u001b[3J"); }
            for (var i = 0; i < newLines.Count; i++)
            {
                if (i > 0) output.Append("\r\n");
                var line = newLines[i];
                var reserved = TerminalImage.IsImageLine(line) ? ReservedRows(newLines, i) : 1;
                if (reserved > 1 && reserved <= height)
                {
                    for (var row = 1; row < reserved; row++) output.Append("\r\n");
                    output.Append($"\u001b[{reserved - 1}A"); output.Append(line); output.Append($"\u001b[{reserved - 1}B");
                    i += reserved - 1; continue;
                }
                output.Append(line);
            }
            output.Append("\u001b[?2026l"); output.Flush();
            cursorRow = Math.Max(0, newLines.Count - 1); hardwareCursorRow = cursorRow;
            maxLinesRendered = clear ? newLines.Count : Math.Max(maxLinesRendered, newLines.Count);
            previousViewportTop = Math.Max(0, Math.Max(height, newLines.Count) - height);
            PositionHardwareCursor(cursorPos, newLines.Count);
            previousLines = newLines; previousKittyIds = CollectKittyIds(newLines); previousWidth = width; previousHeight = height;
        }
        void LogRedraw(string reason)
        {
            if (env("PI_TUI_DEBUG_REDRAW") != "1" || LogDirectory is null) return;
            try
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(Path.Join(LogDirectory, "pi-tui-debug.log"),
                    $"[{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}] fullRender: {reason} (prev={previousLines.Count}, new={newLines.Count}, height={height})\n");
            }
            catch { }
        }

        if (previousLines.Count == 0 && !widthChanged && !heightChanged) { LogRedraw("first render"); FullRender(false); return; }
        if (widthChanged) { LogRedraw($"terminal width changed ({previousWidth} -> {width})"); FullRender(true); return; }
        if (heightChanged && string.IsNullOrEmpty(env("TERMUX_VERSION"))) { LogRedraw($"terminal height changed ({previousHeight} -> {height})"); FullRender(true); return; }
        if (ClearOnShrink && newLines.Count < maxLinesRendered && !HasOverlayEntries) { LogRedraw($"clearOnShrink (maxLinesRendered={maxLinesRendered})"); FullRender(true); return; }

        int firstChanged = -1, lastChanged = -1;
        var maxLines = Math.Max(newLines.Count, previousLines.Count);
        for (var i = 0; i < maxLines; i++)
        {
            var oldLine = i < previousLines.Count ? previousLines[i] : "";
            var newLine = i < newLines.Count ? newLines[i] : "";
            if (!string.Equals(oldLine, newLine, StringComparison.Ordinal)) { if (firstChanged == -1) firstChanged = i; lastChanged = i; }
        }
        var appended = newLines.Count > previousLines.Count;
        if (appended) { if (firstChanged == -1) firstChanged = previousLines.Count; lastChanged = newLines.Count - 1; }
        if (firstChanged != -1) (firstChanged, lastChanged) = ExpandForKitty(firstChanged, lastChanged, newLines);
        var appendStart = appended && firstChanged == previousLines.Count && firstChanged > 0;

        if (firstChanged == -1)
        {
            PositionHardwareCursor(cursorPos, newLines.Count);
            previousViewportTop = prevViewportTop; previousHeight = height;
            return;
        }

        if (firstChanged >= newLines.Count)
        {
            if (previousLines.Count > newLines.Count)
            {
                var output = new BoundedTerminalWriter(Terminal.Write);
                output.Append("\u001b[?2026h");
                output.Append(DeleteChangedKittyImages(firstChanged, lastChanged));
                var targetRow = Math.Max(0, newLines.Count - 1);
                if (targetRow < prevViewportTop) { LogRedraw($"deleted lines moved viewport up ({targetRow} < {prevViewportTop})"); FullRender(true); return; }
                var diff = LineDiff(targetRow);
                if (diff > 0) output.Append($"\u001b[{diff}B"); else if (diff < 0) output.Append($"\u001b[{-diff}A");
                output.Append("\r");
                var extra = previousLines.Count - newLines.Count;
                if (extra > height) { LogRedraw($"extraLines > height ({extra} > {height})"); FullRender(true); return; }
                var clearStart = newLines.Count == 0 ? 0 : 1;
                if (extra > 0 && clearStart > 0) output.Append($"\u001b[{clearStart}B");
                for (var i = 0; i < extra; i++) { output.Append("\r\u001b[2K"); if (i < extra - 1) output.Append("\u001b[1B"); }
                var moveBack = Math.Max(0, extra - 1 + clearStart);
                if (moveBack > 0) output.Append($"\u001b[{moveBack}A");
                output.Append("\u001b[?2026l"); output.Flush();
                cursorRow = targetRow; hardwareCursorRow = targetRow;
            }
            PositionHardwareCursor(cursorPos, newLines.Count);
            previousLines = newLines; previousKittyIds = CollectKittyIds(newLines); previousWidth = width; previousHeight = height; previousViewportTop = prevViewportTop;
            return;
        }

        if (firstChanged < prevViewportTop) { LogRedraw($"firstChanged < viewportTop ({firstChanged} < {prevViewportTop})"); FullRender(true); return; }

        var writer = new BoundedTerminalWriter(Terminal.Write);
        writer.Append("\u001b[?2026h");
        writer.Append(DeleteChangedKittyImages(firstChanged, lastChanged));
        var prevViewportBottom = prevViewportTop + height - 1;
        var moveTargetRow = appendStart ? firstChanged - 1 : firstChanged;
        if (moveTargetRow > prevViewportBottom)
        {
            var currentScreenRow = Math.Max(0, Math.Min(height - 1, hwRow - prevViewportTop));
            var moveToBottom = height - 1 - currentScreenRow;
            if (moveToBottom > 0) writer.Append($"\u001b[{moveToBottom}B");
            var scroll = moveTargetRow - prevViewportBottom;
            writer.Append(TextUtils.Repeat("\r\n", scroll));
            prevViewportTop += scroll; viewportTop += scroll; hwRow = moveTargetRow;
        }
        var lineDiff = LineDiff(moveTargetRow);
        if (lineDiff > 0) writer.Append($"\u001b[{lineDiff}B"); else if (lineDiff < 0) writer.Append($"\u001b[{-lineDiff}A");
        writer.Append(appendStart ? "\r\n" : "\r");
        var renderEnd = Math.Min(lastChanged, newLines.Count - 1);
        for (var i = firstChanged; i <= renderEnd; i++)
        {
            if (i > firstChanged) writer.Append("\r\n");
            var line = newLines[i];
            var isImage = TerminalImage.IsImageLine(line);
            var reserved = isImage ? ReservedRows(newLines, i, renderEnd) : 1;
            if (reserved > 1)
            {
                var startScreenRow = i - viewportTop;
                if (startScreenRow < 0 || startScreenRow + reserved > height)
                { LogRedraw($"kitty image pre-clear would scroll ({startScreenRow} + {reserved} > {height})"); FullRender(true); return; }
                writer.Append("\u001b[2K");
                for (var row = 1; row < reserved; row++) writer.Append("\r\n\u001b[2K");
                writer.Append($"\u001b[{reserved - 1}A"); writer.Append(line); writer.Append($"\u001b[{reserved - 1}B");
                i += reserved - 1; continue;
            }
            writer.Append("\u001b[2K");
            if (!isImage && TextUtils.VisibleWidth(line) > width)
            {
                var crashLog = Path.Join(LogDirectory ?? Path.GetTempPath(), "pi-tui-crash.log");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(crashLog)!);
                    File.WriteAllText(crashLog, string.Join('\n', new[] { $"Crash at {DateTime.UtcNow:O}", $"Terminal width: {width}", $"Line {i} visible width: {TextUtils.VisibleWidth(line)}", "", "=== All rendered lines ===" }
                        .Concat(newLines.Select((l, index) => $"[{index}] (w={TextUtils.VisibleWidth(l)}) {l}")).Append("")));
                }
                catch { }
                Stop();
                throw new InvalidOperationException(string.Join('\n', $"Rendered line {i} exceeds terminal width ({TextUtils.VisibleWidth(line)} > {width}).", "",
                    "This is likely caused by a custom TUI component not truncating its output.", "Use visibleWidth() to measure and truncateToWidth() to truncate lines.", "",
                    $"Debug log written to: {crashLog}"));
            }
            writer.Append(line);
        }
        var finalCursorRow = renderEnd;
        if (previousLines.Count > newLines.Count)
        {
            if (renderEnd < newLines.Count - 1) { writer.Append($"\u001b[{newLines.Count - 1 - renderEnd}B"); finalCursorRow = newLines.Count - 1; }
            var extraLines = previousLines.Count - newLines.Count;
            for (var i = newLines.Count; i < previousLines.Count; i++) writer.Append("\r\n\u001b[2K");
            writer.Append($"\u001b[{extraLines}A");
        }
        writer.Append("\u001b[?2026l");
        writer.Flush();
        cursorRow = Math.Max(0, newLines.Count - 1);
        hardwareCursorRow = finalCursorRow;
        maxLinesRendered = Math.Max(maxLinesRendered, newLines.Count);
        previousViewportTop = Math.Max(prevViewportTop, finalCursorRow - height + 1);
        PositionHardwareCursor(cursorPos, newLines.Count);
        previousLines = newLines; previousKittyIds = CollectKittyIds(newLines); previousWidth = width; previousHeight = height;
    }

    private void PositionHardwareCursor((int Row, int Col)? cursorPos, int totalLines)
    {
        if (cursorPos is not { } position || totalLines <= 0) { Terminal.HideCursor(); return; }
        var targetRow = Math.Max(0, Math.Min(position.Row, totalLines - 1));
        var targetCol = Math.Max(0, position.Col);
        var rowDelta = targetRow - hardwareCursorRow;
        var buffer = rowDelta > 0 ? $"\u001b[{rowDelta}B" : rowDelta < 0 ? $"\u001b[{-rowDelta}A" : "";
        buffer += $"\u001b[{targetCol + 1}G";
        Terminal.Write(buffer);
        hardwareCursorRow = targetRow;
        if (ShowHardwareCursor) Terminal.ShowCursor(); else Terminal.HideCursor();
    }
}
