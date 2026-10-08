// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/ansi.ts,
// packages/coding-agent/src/core/bash-executor.ts and packages/coding-agent/src/utils/shell.ts (sanitizeBinaryOutput).
// Patterns: ansi-regex and strip-ansi, MIT Copyright Sindre Sorhus <sindresorhus@gmail.com> (https://sindresorhus.com).
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tools.Processes;

/// <summary>Source ANSI stripping, including the hold-back of an escape sequence split across streamed chunks.</summary>
public static class ShellAnsiText
{
    // ST is BEL, ESC \ or 0x9c. OSC runs non-greedily to the first ST; CSI and related sequences take
    // optional intermediates and parameters, then a final byte. ECMAScript \d is ASCII, so digits are explicit.
    private const string St = @"(?:\u0007|\u001B\|\u009C)";
    private const string OscStart = @"\u001B\]";
    private const string CsiStart = @"[\u001B\u009B][[\]()#;?]*(?:[0-9]{1,4}(?:[;:][0-9]{0,4})*)?";
    private const string CsiFinal = @"[0-9A-PR-TZcf-nq-uy=><~]";
    private static readonly Regex Complete = new($"(?:{OscStart}[\\s\\S]*?{St})|{CsiStart}{CsiFinal}", RegexOptions.CultureInvariant);
    // Unfinished at the end: OSC without its ST (a trailing ESC may start ESC \), or CSI without its final byte.
    // \z is the ECMAScript non-multiline $; .NET $ would also match before a final newline.
    private static readonly Regex UnfinishedAtEnd = new($@"(?:{OscStart}(?:[^\u0007\u009C\u001B]|\u001B(?!\\))*|{CsiStart})\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex Binary = new(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F￹-￻]", RegexOptions.CultureInvariant);

    /// <summary>Longest unfinished sequence held back while streaming; longer ones are processed as they are.</summary>
    public const int MaximumPendingLength = 256;

    public static string StripAnsi(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Contains('\u001B') || value.Contains('\u009B') ? Complete.Replace(value, "") : value;
    }

    /// <summary>Splits streamed text into a part safe to strip now and a trailing unfinished escape sequence.</summary>
    public static (string Complete, string Pending) SplitIncompleteAnsiSuffix(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('\u001B') && !value.Contains('\u009B')) return (value, "");
        var windowStart = Math.Max(0, value.Length - MaximumPendingLength);
        var match = UnfinishedAtEnd.Match(value, windowStart);
        if (!match.Success) return (value, "");
        return (value[..match.Index], value[match.Index..]);
    }

    /// <summary>Source sanitizeBinaryOutput: removes C0 controls other than tab/LF/CR and Unicode interlinear annotations.</summary>
    public static string SanitizeBinaryOutput(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Binary.Replace(value, "");
    }

    /// <summary>The source user-bash display text: ANSI stripped, binary garbage removed and carriage returns dropped.</summary>
    public static string SanitizeShellText(string value) => SanitizeBinaryOutput(StripAnsi(value)).Replace("\r", "", StringComparison.Ordinal);
}

/// <summary>
/// Streaming user-bash text sanitizer (source bash-executor onData/flush). UTF-8 bytes are decoded across chunks and an
/// escape sequence split across chunks is held back until the next chunk completes it, so no fragment leaks as text.
/// Not thread-safe; one instance belongs to one command's output.
/// </summary>
public sealed class ShellTextStream
{
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private string _pending = "";
    private bool _flushed;

    /// <summary>Unfinished escape sequence carried to the next chunk.</summary>
    public string PendingAnsi => _pending;

    /// <summary>Returns the sanitized text this chunk completes; empty when everything is held back or removed.</summary>
    public string Append(ReadOnlySpan<byte> chunk)
    {
        if (_flushed) throw new InvalidOperationException("The shell text stream is flushed.");
        var chars = new char[Encoding.UTF8.GetMaxCharCount(chunk.Length)];
        var count = _decoder.GetChars(chunk, chars, flush: false);
        return AppendText(new string(chars, 0, count));
    }

    /// <summary>Text chunks that are already decoded follow the same hold-back rule.</summary>
    public string AppendText(string decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        if (_flushed) throw new InvalidOperationException("The shell text stream is flushed.");
        var (complete, pending) = ShellAnsiText.SplitIncompleteAnsiSuffix(_pending + decoded);
        _pending = pending;
        return ShellAnsiText.SanitizeShellText(complete);
    }

    /// <summary>Ends the stream: any held fragment and undecoded bytes are processed as they are.</summary>
    public string Flush()
    {
        if (_flushed) return "";
        _flushed = true;
        var chars = new char[Encoding.UTF8.GetMaxCharCount(0) + 4];
        var count = _decoder.GetChars([], chars, flush: true);
        var rest = _pending + new string(chars, 0, count);
        _pending = "";
        return ShellAnsiText.SanitizeShellText(rest);
    }
}
