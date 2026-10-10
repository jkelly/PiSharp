using System.Text;
using PiSharp.Tui.Pi;

/// <summary>A terminal for interactive-mode cases: raw I/O backed by an in-memory screen that interprets the escape sequences the
/// renderers emit (cursor movement, erase, autowrap, scrolling, the alternate screen) and answers device-attribute queries.</summary>
internal sealed class VirtualTerminal(int columns = 100, int rows = 30) : IRawTerminalIo
{
    private readonly object gate = new();
    private Action<string>? onText;
    private Action? onResize;
    private readonly StringBuilder raw = new();
    private string pending = "";
    private char[][] main = Blank(columns, rows), alt = Blank(columns, rows);
    private bool altActive, wrapPending;
    private int cursorRow, cursorCol, savedRow, savedCol;
    public int Columns { get; private set; } = columns;
    public int Rows { get; private set; } = rows;
    public bool RawMode { get; private set; }
    public bool AnswerDeviceAttributes { get; set; } = true;

    private static char[][] Blank(int columns, int rows) => [.. Enumerable.Range(0, rows).Select(_ => Enumerable.Repeat(' ', columns).ToArray())];
    private char[][] Screen => altActive ? alt : main;

    public void EnterRawMode() => RawMode = true;
    public void ExitRawMode() => RawMode = false;
    public void StartReading(Action<string> handler) => onText = handler;
    public void StopReading() => onText = null;
    public IDisposable ObserveActivity(Action onActivity) => new Nothing();
    public (int Columns, int Rows) Size() => (Columns, Rows);
    public void WatchResize(Action handler) => onResize = handler;
    public void StopWatchingResize() => onResize = null;
    public bool IsShiftPressed() => false;
    private sealed class Nothing : IDisposable { public void Dispose() { } }

    /// <summary>Keyboard input as the terminal would deliver it.</summary>
    public void Send(string data) => onText?.Invoke(data);

    public void Resize(int newColumns, int newRows)
    {
        lock (gate) { Columns = newColumns; Rows = newRows; main = Blank(newColumns, newRows); alt = Blank(newColumns, newRows); cursorRow = 0; cursorCol = 0; }
        onResize?.Invoke();
    }

    public string RawOutput { get { lock (gate) return raw.ToString(); } }

    /// <summary>The visible screen, trailing spaces trimmed.</summary>
    public string[] Lines { get { lock (gate) return [.. Screen.Select(row => new string(row).TrimEnd())]; } }
    public string Text => string.Join("\n", Lines);

    public void Write(string data)
    {
        string? reply = null;
        lock (gate)
        {
            raw.Append(data);
            var text = pending + data; pending = "";
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '\u001b')
                {
                    var consumed = Escape(text, i, ref reply);
                    if (consumed < 0) { pending = text[i..]; break; }
                    i += consumed; continue;
                }
                if (c == '\r') { cursorCol = 0; wrapPending = false; i++; continue; }
                if (c == '\n') { LineFeed(); i++; continue; }
                if (c == '\b') { cursorCol = Math.Max(0, cursorCol - 1); wrapPending = false; i++; continue; }
                if (c < ' ') { i++; continue; }
                var length = char.IsHighSurrogate(c) && i + 1 < text.Length ? 2 : 1;
                Print(text.Substring(i, length));
                i += length;
            }
        }
        if (reply is not null && AnswerDeviceAttributes) Task.Run(() => onText?.Invoke(reply));
    }

    private void Print(string grapheme)
    {
        var width = Math.Max(1, TextUtils.VisibleWidth(grapheme));
        if (wrapPending) { cursorCol = 0; LineFeed(); wrapPending = false; }
        if (cursorCol + width > Columns) { cursorCol = 0; LineFeed(); }
        var screen = Screen;
        screen[cursorRow][cursorCol] = grapheme.Length == 1 ? grapheme[0] : '?';
        if (width == 2 && cursorCol + 1 < Columns) screen[cursorRow][cursorCol + 1] = ' ';
        cursorCol += width;
        if (cursorCol >= Columns) { cursorCol = Columns - 1; wrapPending = true; }
    }

    private void LineFeed()
    {
        wrapPending = false;
        if (cursorRow < Rows - 1) { cursorRow++; return; }
        var screen = Screen;
        for (var r = 0; r < Rows - 1; r++) screen[r] = screen[r + 1];
        screen[Rows - 1] = Enumerable.Repeat(' ', Columns).ToArray();
    }

    /// <summary>Interprets one escape sequence; returns its length, or -1 when incomplete.</summary>
    private int Escape(string text, int start, ref string? reply)
    {
        if (start + 1 >= text.Length) return -1;
        var kind = text[start + 1];
        if (kind == '[')
        {
            var end = start + 2;
            while (end < text.Length && (text[end] < 0x40 || text[end] > 0x7e)) end++;
            if (end >= text.Length) return -1;
            Csi(text[(start + 2)..end], text[end], ref reply);
            return end - start + 1;
        }
        if (kind is ']' or '_' or 'P' or '^')
        {
            for (var end = start + 2; end < text.Length; end++)
            {
                if (text[end] == '\u0007') return end - start + 1;
                if (text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\') return end - start + 2;
            }
            return -1;
        }
        if (kind == '7') { savedRow = cursorRow; savedCol = cursorCol; return 2; }
        if (kind == '8') { cursorRow = savedRow; cursorCol = savedCol; return 2; }
        return 2;
    }

    private void Csi(string parameters, char final, ref string? reply)
    {
        var isPrivate = parameters.StartsWith('?') || parameters.StartsWith('>') || parameters.StartsWith('<') || parameters.StartsWith('=');
        var body = isPrivate ? parameters[1..] : parameters;
        var values = body.Split(';').Select(part => int.TryParse(part.Split(':')[0], out var value) ? value : 0).ToArray();
        int N(int index, int fallback) => index < values.Length && values[index] > 0 ? values[index] : fallback;
        if (isPrivate)
        {
            if (parameters.StartsWith('?') && (final == 'h' || final == 'l') && values.Contains(1049))
            {
                altActive = final == 'h';
                if (altActive) alt = Blank(Columns, Rows);
            }
            return;
        }
        wrapPending = false;
        switch (final)
        {
            case 'c': reply = "\u001b[?62;22c"; break;
            case 'A': cursorRow = Math.Max(0, cursorRow - N(0, 1)); break;
            case 'B': cursorRow = Math.Min(Rows - 1, cursorRow + N(0, 1)); break;
            case 'C': cursorCol = Math.Min(Columns - 1, cursorCol + N(0, 1)); break;
            case 'D': cursorCol = Math.Max(0, cursorCol - N(0, 1)); break;
            case 'E': cursorRow = Math.Min(Rows - 1, cursorRow + N(0, 1)); cursorCol = 0; break;
            case 'F': cursorRow = Math.Max(0, cursorRow - N(0, 1)); cursorCol = 0; break;
            case 'G': cursorCol = Math.Min(Columns - 1, N(0, 1) - 1); break;
            case 'H': case 'f': cursorRow = Math.Min(Rows - 1, N(0, 1) - 1); cursorCol = Math.Min(Columns - 1, N(1, 1) - 1); break;
            case 'J':
            {
                var mode = values.Length > 0 ? values[0] : 0;
                var screen = Screen;
                if (mode is 2 or 3) { for (var r = 0; r < Rows; r++) Array.Fill(screen[r], ' '); }
                else if (mode == 0)
                {
                    Array.Fill(screen[cursorRow], ' ', cursorCol, Columns - cursorCol);
                    for (var r = cursorRow + 1; r < Rows; r++) Array.Fill(screen[r], ' ');
                }
                else
                {
                    for (var r = 0; r < cursorRow; r++) Array.Fill(screen[r], ' ');
                    Array.Fill(screen[cursorRow], ' ', 0, Math.Min(Columns, cursorCol + 1));
                }
                break;
            }
            case 'K':
            {
                var mode = values.Length > 0 ? values[0] : 0;
                var line = Screen[cursorRow];
                if (mode == 0) Array.Fill(line, ' ', cursorCol, Columns - cursorCol);
                else if (mode == 1) Array.Fill(line, ' ', 0, Math.Min(Columns, cursorCol + 1));
                else Array.Fill(line, ' ');
                break;
            }
            case 'S':
                for (var n = 0; n < N(0, 1); n++) { var keep = cursorRow; cursorRow = Rows - 1; LineFeed(); cursorRow = keep; }
                break;
        }
    }

    /// <summary>Waits until the screen satisfies <paramref name="predicate"/>.</summary>
    public async Task<string> WaitForAsync(Func<string, bool> predicate, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var text = Text;
            if (predicate(text)) return text;
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Timed out waiting for {what}. Screen:\n{text}");
            await Task.Delay(25);
        }
    }

    public Task<string> WaitForTextAsync(string needle, int timeoutMs = 20_000) =>
        WaitForAsync(text => text.Contains(needle, StringComparison.Ordinal), $"\"{needle}\"", timeoutMs);
}
