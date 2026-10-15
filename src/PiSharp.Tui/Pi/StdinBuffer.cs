// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/tui/src/stdin-buffer.ts (based on OpenTUI, MIT Copyright (c) 2025 opentui).
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

/// <summary>Buffers terminal input and emits complete sequences; bracketed paste content is emitted whole.</summary>
public sealed partial class StdinBuffer
{
    private const string Esc = "\u001b", PasteStart = "\u001b[200~", PasteEnd = "\u001b[201~";
    private readonly Func<Action, double, IDisposable> setTimeout;
    private readonly double timeoutMs, escapeTimeoutMs;
    private string buffer = "";
    private IDisposable? timer;
    private bool pasteMode;
    private string pasteBuffer = "";
    private int? pendingKittyPrintable;
    public event Action<string>? Data;
    public event Action<string>? Paste;

    /// <param name="timeoutMs">Maximum time to wait for an incomplete keyboard or mouse sequence. Strings with a recognized terminal
    /// reply header wait for their terminator instead, so delayed replies cannot become keypresses. Ambiguous legacy Alt-key input
    /// still uses this timeout.</param>
    /// <param name="escapeTimeoutMs">Maximum time to wait after a lone ESC before treating it as Escape.</param>
    public StdinBuffer(UiLoop loop, double timeoutMs = 50, double escapeTimeoutMs = 10) : this(loop.SetTimeout, timeoutMs, escapeTimeoutMs) { }

    /// <summary>A buffer whose timeouts are scheduled by <paramref name="setTimeout"/> (Node's setTimeout; tests pass fake timers).</summary>
    public StdinBuffer(Func<Action, double, IDisposable> setTimeout, double timeoutMs = 50, double escapeTimeoutMs = 10)
    { this.setTimeout = setTimeout; this.timeoutMs = timeoutMs; this.escapeTimeoutMs = escapeTimeoutMs; }

    // Color and program-status OSC replies, XTVersion, and Kitty graphics replies.
    // Until their header is complete, the input may be batched legacy Alt keystrokes.
    [GeneratedRegex(@"^\x1b(?:\](?:1[01];|4;[0-9]{1,3};|7501;\?)|P>\||_G(?:[a-z]=[0-9]+,)*[a-z]=[0-9]+;)", RegexOptions.CultureInvariant)]
    private static partial Regex TerminalReplyHeader();

    private static bool IsControlString(string data) =>
        data.StartsWith("\u001b]", StringComparison.Ordinal) || data.StartsWith("\u001bP", StringComparison.Ordinal) || data.StartsWith("\u001b_", StringComparison.Ordinal);

    private enum Status { Complete, Incomplete, NotEscape }
    [GeneratedRegex(@"^<\d+;\d+;\d+[Mm]$")] private static partial Regex SgrMouse();
    [GeneratedRegex(@"^\x1b\[(\d+)(?::\d*)?(?::\d+)?u$")] private static partial Regex KittyPrintable();

    private static Status Check(string data)
    {
        if (!data.StartsWith(Esc, StringComparison.Ordinal)) return Status.NotEscape;
        if (data.Length == 1) return Status.Incomplete;
        var after = data[1];
        if (after == '[')
        {
            if (data.Length >= 3 && data[2] == 'M') return data.Length >= 6 ? Status.Complete : Status.Incomplete;
            if (data.Length < 3) return Status.Incomplete;
            var payload = data[2..]; var last = payload[^1];
            if (last is >= '@' and <= '~')
            {
                if (payload.StartsWith('<'))
                {
                    if (SgrMouse().IsMatch(payload)) return Status.Complete;
                    if (last is 'M' or 'm')
                    {
                        var parts = payload[1..^1].Split(';');
                        if (parts.Length == 3 && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit))) return Status.Complete;
                    }
                    return Status.Incomplete;
                }
                return Status.Complete;
            }
            return Status.Incomplete;
        }
        if (after == ']') return data.EndsWith("\u001b\\", StringComparison.Ordinal) || data.EndsWith('\u0007') ? Status.Complete : Status.Incomplete;
        if (after is 'P' or '_') return data.EndsWith("\u001b\\", StringComparison.Ordinal) ? Status.Complete : Status.Incomplete;
        if (after == 'O') return data.Length >= 3 ? Status.Complete : Status.Incomplete;
        return Status.Complete;
    }

    private static (List<string> Sequences, string Remainder) Extract(string input)
    {
        var sequences = new List<string>(); var pos = 0;
        while (pos < input.Length)
        {
            if (input[pos] == '\u001b')
            {
                var remaining = input.Length - pos; var end = 1; var done = false;
                var rest = input[pos..];
                var terminalReply = IsControlString(rest) && TerminalReplyHeader().IsMatch(rest);
                while (end <= remaining)
                {
                    if (terminalReply && end > 2)
                    {
                        var c = input[pos + end - 1];
                        // CAN and SUB cancel the string, not subsequent keyboard input.
                        if (c is '\u0018' or '\u001a') { pos += end; done = true; break; }
                        // A new escape sequence aborts an unterminated reply. Keep a trailing ESC buffered until we can distinguish it from ST.
                        if (c == '\u001b' && end < remaining && input[pos + end] != '\\') { pos += end - 1; done = true; break; }
                    }
                    var candidate = input.Substring(pos, end);
                    var status = Check(candidate);
                    if (status == Status.Complete)
                    {
                        if (candidate == "\u001b\u001b" && pos + end < input.Length && input[pos + end] is '[' or ']' or 'O' or 'P' or '_')
                        { sequences.Add(Esc); pos += 1; done = true; break; }
                        sequences.Add(candidate); pos += end; done = true; break;
                    }
                    if (status == Status.Incomplete) { end++; continue; }
                    sequences.Add(candidate); pos += end; done = true; break;
                }
                if (!done) return (sequences, input[pos..]);
            }
            else
            {
                // Node iterates UTF-16 code units here; keep surrogate pairs together so .NET strings stay valid.
                var length = char.IsHighSurrogate(input[pos]) && pos + 1 < input.Length && char.IsLowSurrogate(input[pos + 1]) ? 2 : 1;
                sequences.Add(input.Substring(pos, length)); pos += length;
            }
        }
        return (sequences, "");
    }

    /// <summary>Feeds raw input (already decoded to text).</summary>
    public void Process(string str)
    {
        timer?.Dispose(); timer = null;
        if (str.Length == 0 && buffer.Length == 0) { EmitData(""); return; }
        buffer += str;
        if (pasteMode)
        {
            pasteBuffer += buffer; buffer = "";
            var endIndex = pasteBuffer.IndexOf(PasteEnd, StringComparison.Ordinal);
            if (endIndex != -1) FinishPaste(endIndex);
            return;
        }
        var startIndex = buffer.IndexOf(PasteStart, StringComparison.Ordinal);
        if (startIndex != -1)
        {
            if (startIndex > 0) foreach (var sequence in Extract(buffer[..startIndex]).Sequences) EmitData(sequence);
            pendingKittyPrintable = null;
            pasteMode = true; pasteBuffer = buffer[(startIndex + PasteStart.Length)..]; buffer = "";
            var endIndex = pasteBuffer.IndexOf(PasteEnd, StringComparison.Ordinal);
            if (endIndex != -1) FinishPaste(endIndex);
            return;
        }
        var (sequences, remainder) = Extract(buffer);
        buffer = remainder;
        foreach (var sequence in sequences) EmitData(sequence);
        if (buffer.Length > 0)
        {
            // Only a recognizable reply header establishes a delimited string. Flushing it would turn a delayed tail into text and an
            // OSC's BEL terminator into Ctrl+G (the external editor shortcut). Other input, including Alt+] followed by text, must
            // retain bounded recovery.
            if (TerminalReplyHeader().IsMatch(buffer)) return;
            timer = setTimeout(() =>
            {
                foreach (var sequence in Flush())
                {
                    if (sequence.Length > 2 && IsControlString(sequence))
                    {
                        // Recover the Alt key and reparse its tail so batched text, Enter, and control keys are not swallowed as one event.
                        EmitData(sequence[..2]);
                        Process(sequence[2..]);
                    }
                    else EmitData(sequence);
                }
            }, buffer == Esc ? escapeTimeoutMs : timeoutMs);
        }
    }

    private void FinishPaste(int endIndex)
    {
        var content = pasteBuffer[..endIndex];
        var remaining = pasteBuffer[(endIndex + PasteEnd.Length)..];
        pasteMode = false; pasteBuffer = ""; pendingKittyPrintable = null;
        Paste?.Invoke(content);
        if (remaining.Length > 0) Process(remaining);
    }

    private void EmitData(string sequence)
    {
        if (sequence.Length == 1 && pendingKittyPrintable is { } pending && sequence[0] == pending) { pendingKittyPrintable = null; return; }
        var match = KittyPrintable().Match(sequence);
        pendingKittyPrintable = match.Success && int.TryParse(match.Groups[1].Value, out var codepoint) && codepoint >= 32 ? codepoint : null;
        Data?.Invoke(sequence);
    }

    public List<string> Flush()
    {
        timer?.Dispose(); timer = null;
        if (buffer.Length == 0) return [];
        var result = new List<string> { buffer };
        buffer = ""; pendingKittyPrintable = null;
        return result;
    }

    public void Clear() { timer?.Dispose(); timer = null; buffer = ""; pasteMode = false; pasteBuffer = ""; pendingKittyPrintable = null; }
    public string Buffered => buffer;
    public void Destroy() => Clear();
}
