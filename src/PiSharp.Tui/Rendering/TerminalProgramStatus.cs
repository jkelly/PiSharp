// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/program-status.ts and packages/tui/src/terminal.ts (ProcessTerminal program status).
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Rendering;

/// <summary>What the program is doing. <see cref="Clear"/> removes the status instead of reporting one.</summary>
public enum TerminalProgramState { Idle, Working, Blocked, Done, Error, Clear }
/// <summary>What a blocked program waits for.</summary>
public enum TerminalProgramBlockedKind { Permission, Question, Auth }

/// <summary>One OSC 7501 report. <paramref name="App"/> must match <c>[A-Za-z0-9_.+-]{1,32}</c> or it is omitted;
/// <paramref name="Kind"/> is sent only for blocked; <paramref name="Message"/> is one human-readable line.</summary>
public sealed record TerminalProgramStatus(TerminalProgramState State, string? App = null,
    TerminalProgramBlockedKind? Kind = null, string? Message = null);

/// <summary>Program Status Protocol (OSC 7501): a program tells the terminal whether it is idle, working,
/// blocked on the user, done, or failed. Only the root record is supported.</summary>
public static partial class TerminalProgramStatusProtocol
{
    /// <summary>Feature detection query. A supporting terminal replies with the same body.</summary>
    public const string Query = "\u001b]7501;?\u001b\\";
    /// <summary>Decoded <c>msg</c> limit. Its base64 encoding stays under the 2732-byte encoded limit.</summary>
    public const int MaximumMessageBytes = 2048;

    [GeneratedRegex(@"^\u001b\]7501;\?[^\u0007\u001b]*(?:\u0007|\u001b\\)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPattern();
    [GeneratedRegex(@"^[A-Za-z0-9_.+-]{1,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex AppPattern();
    [GeneratedRegex(@"[\u0000-\u001f\u007f-\u009f]+", RegexOptions.CultureInvariant)]
    private static partial Regex ControlCharacters();

    /// <summary>The reply to <see cref="Query"/>. Later spec revisions may add pairs after the <c>?</c>.</summary>
    public static bool IsReply(string sequence) => sequence is not null && ReplyPattern().IsMatch(sequence);

    /// <summary>Encode a report. Terminals discard reports whose text contains control characters, so they become spaces.</summary>
    public static string Format(TerminalProgramStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var pairs = new List<string> { "state=" + Name(status.State) };
        if (status.App is not null && AppPattern().IsMatch(status.App)) pairs.Add("app=" + status.App);
        if (status.State == TerminalProgramState.Blocked && status.Kind is { } kind) pairs.Add("kind=" + kind.ToString().ToLowerInvariant());
        var message = TruncateUtf8(ControlCharacters().Replace(status.Message ?? "", " ").Trim(JsWhitespace), MaximumMessageBytes);
        if (message.Length != 0) pairs.Add("msg=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(message)));
        return "\u001b]7501;" + string.Join(":", pairs) + "\u001b\\";
    }

    private static string Name(TerminalProgramState state) => state switch
    {
        TerminalProgramState.Idle => "idle", TerminalProgramState.Working => "working", TerminalProgramState.Blocked => "blocked",
        TerminalProgramState.Done => "done", TerminalProgramState.Error => "error", TerminalProgramState.Clear => "clear",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    // ECMAScript String.prototype.trim: WhiteSpace and LineTerminator code points.
    private static readonly char[] JsWhitespace = [.. new[] { 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x20, 0xa0, 0x1680, 0x2000, 0x2001, 0x2002, 0x2003,
        0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200a, 0x2028, 0x2029, 0x202f, 0x205f, 0x3000, 0xfeff }.Select(code => (char)code)];

    // Whole code points only, like iterating the string by code point. Lone surrogates count as the 3-byte replacement.
    private static string TruncateUtf8(string text, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maximumBytes) return text;
        var bytes = 0; var end = 0;
        while (end < text.Length)
        {
            var width = char.IsHighSurrogate(text[end]) && end + 1 < text.Length && char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
            var size = width == 2 ? 4 : text[end] < 0x80 ? 1 : text[end] < 0x800 ? 2 : 3;
            if (bytes + size > maximumBytes) break;
            bytes += size; end += width;
        }
        return text[..end];
    }
}

/// <summary>A terminal's OSC 7501 state: reports go out only once the terminal confirmed support (its reply to
/// <see cref="TerminalProgramStatusProtocol.Query"/> before the DA1 sentinel) or <c>PI_PROGRAM_STATUS=1</c>; the latest
/// status is re-sent when support is confirmed or the terminal restarts, and cleared while stopped. Methods return the
/// exact text to write (empty for none), so the owning terminal keeps its write order.</summary>
public sealed class TerminalProgramStatusChannel
{
    private readonly object gate = new();
    private TerminalProgramStatus? latest;
    private bool supported, queryPending;

    /// <summary>Whether reports are currently written.</summary>
    public bool IsSupported { get { lock (gate) return supported; } }

    /// <summary>Start for a terminal that sends the Kitty/DA1 negotiation. Returns the query to place before the DA1
    /// query (empty when <c>PI_PROGRAM_STATUS</c> is <c>1</c> or <c>0</c>). Write <see cref="ReportLatest"/> after it.</summary>
    public string StartNegotiation(string? programStatusOverride)
    {
        lock (gate)
        {
            supported = programStatusOverride == "1";
            queryPending = programStatusOverride is not ("1" or "0");
            return queryPending ? TerminalProgramStatusProtocol.Query : "";
        }
    }

    /// <summary>Start for a terminal without DA1 negotiation: only <c>PI_PROGRAM_STATUS=1</c> enables reports.</summary>
    public string Start(string? programStatusOverride)
    {
        lock (gate) { supported = programStatusOverride == "1"; queryPending = false; return LatestLocked(); }
    }

    /// <summary>The latest status when reports are enabled.</summary>
    public string ReportLatest() { lock (gate) return LatestLocked(); }

    /// <summary>Consume a program status reply. A reply to the pending query confirms support and re-sends the latest status.</summary>
    public bool TryObserveReply(string sequence, out string write)
    {
        write = "";
        if (!TerminalProgramStatusProtocol.IsReply(sequence)) return false;
        lock (gate)
        {
            if (!queryPending) return true;
            queryPending = false; supported = true; write = LatestLocked(); return true;
        }
    }

    /// <summary>A DA1 reply arrived. The last owed one answers the latest query, which got no program status reply first.</summary>
    public void ObserveDeviceAttributes(int remainingOwed)
    {
        if (remainingOwed == 0) lock (gate) queryPending = false;
    }

    public string Set(TerminalProgramStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (gate)
        {
            latest = status.State == TerminalProgramState.Clear ? null : status;
            return supported ? TerminalProgramStatusProtocol.Format(status) : "";
        }
    }

    /// <summary>Remove the status while stopped (exit or suspend). The next start reports it again.</summary>
    public string Stop()
    {
        lock (gate)
        {
            var write = supported && latest is not null ? TerminalProgramStatusProtocol.Format(new(TerminalProgramState.Clear)) : "";
            supported = false; queryPending = false; return write;
        }
    }

    private string LatestLocked() => supported && latest is not null ? TerminalProgramStatusProtocol.Format(latest) : "";
}
