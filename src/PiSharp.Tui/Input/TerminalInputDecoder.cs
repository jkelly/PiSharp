using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Input;

/// <summary>Single-consumer incremental UTF-16 decoder. Unknown sequences are data, never output instructions.</summary>
public sealed class TerminalInputDecoder
{
    private sealed record KittyAlternateOrigin(string Raw, int Code, int? Shifted, int? Base, int RawModifiers);
    private sealed record PrintableTarget(string? Text, string? ModifyOtherKeysRaw = null, int RawModifiers = 0,
        KittyAlternateOrigin? Kitty = null);
    private static readonly ConditionalWeakTable<TerminalKey, PrintableTarget> printableTargets = new();
    private sealed record BindingOrigin(string Raw, bool KittyActive, bool WindowsTerminal);
    private static readonly ConditionalWeakTable<TerminalInputEvent, BindingOrigin> bindingOrigins = new();
    private readonly bool kittyProtocolActive, windowsTerminal;

    /// <summary>Projects the original decoded key's Source printable identity only while the editor awaits a jump.
    /// Named event equality, hashing, formatting and record cloning remain unchanged. Provenance belongs to the
    /// original decoder event instance; constructed or cloned named keys retain their ordinary named identity.</summary>
    public static TerminalInputEvent ForPendingCharacterJump(TerminalInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input is TerminalKey key && printableTargets.TryGetValue(key, out var target) && target.Text is not null && key.Key != target.Text
            ? ProjectKey(key, target.Text) : input;
    }

    /// <summary>Original printable eligibility without projecting default bindings. A known non-printable
    /// wire result remains null; constructed/cloned keys use only their named scalar identity.</summary>
    public static string? GetPrintableText(TerminalInputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input is TerminalText text) { ArgumentNullException.ThrowIfNull(text.Text); return text.Text; }
        if (input is not TerminalKey key) return null;
        if (printableTargets.TryGetValue(key, out var target)) return target.Text;
        return key.Modifiers is TerminalModifiers.None or TerminalModifiers.Shift &&
            !string.IsNullOrEmpty(key.Key) && Rune.TryGetRuneAt(key.Key, 0, out var rune) &&
            rune.Value >= 32 && rune.Utf16SequenceLength == key.Key.Length ? key.Key : null;
    }

    /// <summary>Resolves Source wire binding/printable ordering before editor or host bindings.
    /// Constructed and cloned keys gain no wire provenance; canonical event record identity stays unchanged.</summary>
    public static TerminalInputEvent ForEditorInput(TerminalInputEvent input, bool isCharacterJumpPending)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input is TerminalKey alternate && printableTargets.TryGetValue(alternate, out var alternateTarget) &&
            alternateTarget.Kitty is { } origin)
        {
            if (isCharacterJumpPending && alternateTarget.Text is not null) return ForPendingCharacterJump(input);
            // Source matches bindings before printable text. parseKey alone loses dual matches, for example
            // a digit plus a base-layout Ctrl+A, or keypad Left plus a base-layout Backspace.
            var name = KittyEditorBinding(origin) ?? alternateTarget.Text;
            if (isCharacterJumpPending && alternateTarget.Text is null && name == " ")
            {
                // A non-printable shifted field cancels the pending jump before Source Shift+Space
                // inserts. Keep a non-scalar named marker until ordinary projection, so the existing
                // controller does not mistake the eventual space insertion for a printable jump target.
                if (alternate.Key == "Space") return input;
                return ProjectKey(alternate, "Space");
            }
            return name is null ? new TerminalUnknownSequence(origin.Raw) :
                alternate.Key == name ? input : ProjectKey(alternate, name);
        }
        if (input is TerminalKey key && printableTargets.TryGetValue(key, out var target) && target.ModifyOtherKeysRaw is { } raw)
        {
            if (isCharacterJumpPending && target.Text is not null) return ForPendingCharacterJump(input);
            // Source legacy binding matching uses the exact mask, while printable matching ignores lock state.
            if ((target.RawModifiers & (64 | 128)) != 0)
                return target.Text is not null ? new TerminalText(target.Text) : new TerminalUnknownSequence(raw);
            // The Source default editor ignores unmodified and Alt+Enter in this wire form.
            // Shift+Enter remains a newline; ordinary CR and other Enter wire forms retain their own bindings.
            if (key.Key == "Enter" && key.Modifiers is TerminalModifiers.None or TerminalModifiers.Alt)
                return new TerminalUnknownSequence(raw);
        }
        return isCharacterJumpPending ? ForPendingCharacterJump(input) : input;
    }

    // Internal projections retain original weak provenance; caller-created record clones do not.
    private static TerminalKey ProjectKey(TerminalKey original, string name)
    {
        var projected = original with { Key = name };
        if (printableTargets.TryGetValue(original, out var target)) printableTargets.Add(projected, target);
        if (bindingOrigins.TryGetValue(original, out var binding)) bindingOrigins.Add(projected, binding);
        return projected;
    }

    /// <summary>Source-observed ordinary release eligibility for a non-Latin base-layout binding.</summary>
    public static bool IsNonLatinAlternateKeyRelease(TerminalInputEvent input)
    {
        if (input is not TerminalKey { Action: TerminalKeyAction.Release } key ||
            !printableTargets.TryGetValue(key, out var target) || target.Kitty is not { Base: not null } origin) return false;
        var code = KittySourceCode(origin.Code);
        if ((origin.RawModifiers & 1) != 0 && code is >= 65 and <= 90) code += 32;
        return code is not (>= 97 and <= 122) && !"`-=[]\\;',./!@#$%^&*()_+|~{}:<>?".Contains((char)(code & 0xffff));
    }

    /// <summary>Matches original wire data in its captured protocol mode. Constructed keys use named identity only.</summary>
    public static bool MatchesKey(TerminalInputEvent input, string keyId)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(keyId);
        if (input is TerminalPaste or TerminalProtocol or TerminalUnknownSequence) return false;
        if (bindingOrigins.TryGetValue(input, out var origin))
            return MatchesRawKey(origin.Raw, keyId, origin.KittyActive, origin.WindowsTerminal);
        var parts = keyId.ToLowerInvariant().Split('+'); var name = parts[^1];
        var modifiers = KeyModifiers(parts);
        if (input is TerminalText text) return MatchesRawKey(text.Text, keyId);
        name = name switch { "esc" => "escape", "return" => "enter", _ => name };
        return input is TerminalKey key && key.Modifiers == modifiers &&
            string.Equals(key.Key == " " ? "space" : key.Key, name, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetOriginalInput(TerminalInputEvent input, out string raw, out bool kittyActive)
    {
        if (bindingOrigins.TryGetValue(input, out var origin)) { raw = origin.Raw; kittyActive = origin.KittyActive; return true; }
        raw = ""; kittyActive = false; return false;
    }

    private static TerminalModifiers KeyModifiers(string[] parts) =>
        (parts.Contains("shift") ? TerminalModifiers.Shift : TerminalModifiers.None) | (parts.Contains("alt") ? TerminalModifiers.Alt : TerminalModifiers.None) |
        (parts.Contains("ctrl") ? TerminalModifiers.Control : TerminalModifiers.None) | (parts.Contains("super") ? TerminalModifiers.Super : TerminalModifiers.None);

    // Translated matching rules from pinned Pi keys.ts, under the retained upstream MIT notice.
    public static bool MatchesRawKey(string data, string keyId, bool kittyActive = false, bool windowsTerminal = false)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(keyId);
        var parts = keyId.ToLowerInvariant().Split('+'); var name = parts[^1]; if (name.Length == 0) return false;
        var modifiers = KeyModifiers(parts); var mask = (int)modifiers;
        bool Kitty(int code) => RawKitty(data) is { } origin && KittyMatches(origin, code, modifiers);
        bool Mok(int code, bool printable = false)
        {
            var match = Regex.Match(data, "^\\u001b\\[27;(\\d+);(\\d+)~$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success || !Number(match.Groups[1].Value, out var encoded) || !Number(match.Groups[2].Value, out var actual) || encoded - 1 != mask)
                return false;
            if (printable && (mask & 1) != 0) { if (actual is >= 65 and <= 90) actual += 32; if (code is >= 65 and <= 90) code += 32; }
            return actual == code;
        }
        switch (name)
        {
            case "escape": case "esc": return mask == 0 && (data == "\u001b" || Kitty(27) || Mok(27));
            case "space": return !kittyActive && (mask == 4 && data == "\0" || mask == 2 && data == "\u001b ") ||
                mask == 0 && data == " " || Kitty(32) || Mok(32);
            case "tab": return mask == 0 ? data == "\t" || Kitty(9) :
                mask == 1 && data == "\u001b[Z" || Kitty(9) || Mok(9);
            case "enter": case "return":
                if (mask == 0) return data == "\r" || !kittyActive && data == "\n" || data == "\u001bOM" || Kitty(13) || Kitty(57414);
                if (mask == 1) return Kitty(13) || Kitty(57414) || Mok(13) || kittyActive && (data is "\u001b\r" or "\n");
                if (mask == 2) return Kitty(13) || Kitty(57414) || Mok(13) || !kittyActive && data == "\u001b\r";
                return Kitty(13) || Kitty(57414) || Mok(13);
            case "backspace":
                var raw = data == "\u007f" && mask == 0 || data == "\b" && mask == (windowsTerminal ? 4 : 0) ||
                    mask == 2 && (data is "\u001b\u007f" or "\u001b\b");
                return raw || Kitty(127) || Mok(127);
        }
        var namedCode = name switch { "up" => -1, "down" => -2, "right" => -3, "left" => -4,
            "delete" => -10, "insert" => -11, "pageup" => -12, "pagedown" => -13, "home" => -14, "end" => -15, _ => 0 };
        if (namedCode != 0 || name == "clear")
        {
            if (name == "up" && mask == 2) return data == "\u001bp" || Kitty(-1);
            if (name == "down" && mask == 2) return data == "\u001bn" || Kitty(-2);
            if (name == "left" && mask == 2) return data == "\u001b[1;3D" || data == "\u001bb" || !kittyActive && data == "\u001bB" || Kitty(-4);
            if (name == "right" && mask == 2) return data == "\u001b[1;3C" || data == "\u001bf" || !kittyActive && data == "\u001bF" || Kitty(-3);
            return LegacyMatch(data, name, mask) || namedCode != 0 && Kitty(namedCode);
        }
        if (name.Length > 1 && name[0] == 'f' && int.TryParse(name.AsSpan(1), out var function) && function is >= 1 and <= 12)
            return mask == 0 && LegacyMatch(data, name, 0);
        if (name.Length != 1 || !(name[0] is >= 'a' and <= 'z' or >= '0' and <= '9' ||
            "`-=[]\\;',./!@#$%^&*()_+|~{}:<>?".Contains(name[0]))) return false;
        var key = name[0]; var ctrl = key is >= 'a' and <= 'z' or '[' or '\\' or ']' or '_' ? (char)(key & 31) : key == '-' ? (char)31 : (char?)null;
        if (!kittyActive && (mask == 6 && ctrl is not null && data == "\u001b" + ctrl || mask == 2 && data == "\u001b" + key)) return true;
        if (mask == 4 && ctrl is not null && data == ctrl.ToString()) return true;
        if (mask == 1 && key is >= 'a' and <= 'z' && data == char.ToUpperInvariant(key).ToString()) return true;
        if (mask == 0) return data == name || Kitty(key);
        return Kitty(key) || Mok(key, printable: true);
    }

    private static KittyAlternateOrigin? RawKitty(string raw)
    {
        var match = Regex.Match(raw, "^\\u001b\\[(\\d+)(?::(\\d*))?(?::(\\d+))?(?:;(\\d+))?(?::(\\d+))?u$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (match.Success && Number(match.Groups[1].Value, out var code))
        {
            int? shifted = Number(match.Groups[2].Value, out var shift) ? shift : null;
            int? baseKey = Number(match.Groups[3].Value, out var baseValue) ? baseValue : null;
            var mask = match.Groups[4].Success ? Number(match.Groups[4].Value, out var mod) ? mod - 1 : int.MinValue : 0;
            return new(raw, code, shifted, baseKey, mask);
        }
        match = Regex.Match(raw, "^\\u001b\\[1;(\\d+)(?::\\d+)?([ABCDHF])$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (match.Success && Number(match.Groups[1].Value, out var encoded))
            return new(raw, match.Groups[2].Value switch { "A" => -1, "B" => -2, "C" => -3, "D" => -4, "H" => -14, _ => -15 }, null, null, encoded - 1);
        match = Regex.Match(raw, "^\\u001b\\[(\\d+)(?:;(\\d+))?(?::\\d+)?~$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || !Number(match.Groups[1].Value, out var number)) return null;
        var functional = number switch { 2 => -11, 3 => -10, 5 => -12, 6 => -13, 7 => -14, 8 => -15, _ => 0 };
        if (functional == 0) return null;
        var modifiers = match.Groups[2].Success ? Number(match.Groups[2].Value, out var modifier) ? modifier - 1 : int.MinValue : 0;
        return new(raw, functional, null, null, modifiers);
    }

    private static bool LegacyMatch(string data, string name, int mask)
    {
        var sequences = name switch
        {
            "up" => new[] { "\u001b[A", "\u001bOA" }, "down" => new[] { "\u001b[B", "\u001bOB" },
            "right" => new[] { "\u001b[C", "\u001bOC" }, "left" => new[] { "\u001b[D", "\u001bOD" },
            "home" => new[] { "\u001b[H", "\u001bOH", "\u001b[1~", "\u001b[7~" },
            "end" => new[] { "\u001b[F", "\u001bOF", "\u001b[4~", "\u001b[8~" },
            "insert" => new[] { "\u001b[2~" }, "delete" => new[] { "\u001b[3~" },
            "pageup" => new[] { "\u001b[5~", "\u001b[[5~" }, "pagedown" => new[] { "\u001b[6~", "\u001b[[6~" },
            "clear" => new[] { "\u001b[E", "\u001bOE" },
            "f1" => new[] { "\u001bOP", "\u001b[11~", "\u001b[[A" }, "f2" => new[] { "\u001bOQ", "\u001b[12~", "\u001b[[B" },
            "f3" => new[] { "\u001bOR", "\u001b[13~", "\u001b[[C" }, "f4" => new[] { "\u001bOS", "\u001b[14~", "\u001b[[D" },
            "f5" => new[] { "\u001b[15~", "\u001b[[E" }, "f6" => new[] { "\u001b[17~" }, "f7" => new[] { "\u001b[18~" },
            "f8" => new[] { "\u001b[19~" }, "f9" => new[] { "\u001b[20~" }, "f10" => new[] { "\u001b[21~" },
            "f11" => new[] { "\u001b[23~" }, "f12" => new[] { "\u001b[24~" }, _ => []
        };
        if (mask == 0) return sequences.Contains(data);
        var suffix = name switch { "up" => "a", "down" => "b", "right" => "c", "left" => "d", "clear" => "e",
            "insert" => "2", "delete" => "3", "pageup" => "5", "pagedown" => "6", "home" => "7", "end" => "8", _ => "" };
        if (suffix.Length == 0 || mask is not (1 or 4)) return false;
        return char.IsLetter(suffix[0]) ? data == (mask == 1 ? "\u001b[" : "\u001bO") + suffix :
            data == "\u001b[" + suffix + (mask == 1 ? "$" : "^");
    }

    private enum State { Text, Escape, Csi, Ss3, Osc, Dcs, Paste }
    private const string PasteEnd = "\u001b[201~";
    private readonly TerminalInputDecoderOptions options;
    private readonly TimeProvider time;
    private readonly TimeSpan escapeTimeout, sequenceTimeout;
    private readonly StringBuilder sequence = new(), paste = new(), pasteEnd = new(), text = new();
    private State state;
    private long started;
    private char highSurrogate;
    private bool closed;

    public TerminalInputDecoder(TerminalInputDecoderOptions? options = null, TimeProvider? timeProvider = null, bool kittyProtocolActive = false)
    {
        this.kittyProtocolActive = kittyProtocolActive;
        windowsTerminal = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")) &&
            new[] { "SSH_CONNECTION", "SSH_CLIENT", "SSH_TTY" }.All(name => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)));
        this.options = options ?? new(); time = timeProvider ?? TimeProvider.System;
        escapeTimeout = this.options.EscapeTimeout ?? TimeSpan.FromMilliseconds(35);
        sequenceTimeout = this.options.SequenceTimeout ?? TimeSpan.FromMilliseconds(250);
        if (this.options.MaximumSequenceCharacters is < 6 or > 65_536 ||
            this.options.MaximumPasteCharacters is < 1 or > 1_048_576 ||
            this.options.MaximumChunkCharacters is < 1 or > 65_536 ||
            escapeTimeout <= TimeSpan.Zero || sequenceTimeout < escapeTimeout || sequenceTimeout > TimeSpan.FromMinutes(1))
            throw new TerminalInputException(TerminalInputFailure.InvalidOptions);
    }

    public ImmutableArray<TerminalInputEvent> Feed(ReadOnlySpan<char> chunk)
    {
        EnsureOpen();
        if (chunk.Length > options.MaximumChunkCharacters) Fail(TerminalInputFailure.ResourceLimit);
        var events = ImmutableArray.CreateBuilder<TerminalInputEvent>(); Expire(events);
        foreach (var value in chunk)
        {
            if (highSurrogate != '\0')
            {
                if (!char.IsLowSurrogate(value)) Fail(TerminalInputFailure.InvalidUnicode);
                var pair = new string([highSurrogate, value]); highSurrogate = '\0';
                if (state == State.Escape) { EmitText(events); EmitBound(events, new TerminalKey(pair, TerminalModifiers.Alt), "\u001b" + pair); Reset(); }
                else if (state is State.Csi or State.Ss3)
                {
                    if (sequence.Length + pair.Length > options.MaximumSequenceCharacters) Fail(TerminalInputFailure.ResourceLimit);
                    events.Add(new TerminalUnknownSequence(sequence.ToString() + pair)); Reset();
                }
                else { Consume(pair[0], events); Consume(pair[1], events); }
            }
            else if (char.IsHighSurrogate(value)) highSurrogate = value;
            else if (char.IsLowSurrogate(value)) Fail(TerminalInputFailure.InvalidUnicode);
            else Consume(value, events);
        }
        EmitText(events); return events.ToImmutable();
    }

    /// <summary>The host calls this when its injected clock reaches a pending escape/sequence deadline.</summary>
    public ImmutableArray<TerminalInputEvent> FlushTimeouts()
    {
        EnsureOpen(); var events = ImmutableArray.CreateBuilder<TerminalInputEvent>();
        Expire(events); EmitText(events); return events.ToImmutable();
    }

    public ImmutableArray<TerminalInputEvent> Complete()
    {
        EnsureOpen();
        if (highSurrogate != '\0') Fail(TerminalInputFailure.InvalidUnicode);
        if (state == State.Paste) Fail(TerminalInputFailure.IncompleteInput);
        var events = ImmutableArray.CreateBuilder<TerminalInputEvent>();
        if (state == State.Escape) EmitBound(events, new TerminalKey("Escape"), "\u001b");
        else if (state != State.Text) events.Add(new TerminalUnknownSequence(sequence.ToString()));
        EmitText(events); Reset(); closed = true; return events.ToImmutable();
    }

    private void Consume(char value, ImmutableArray<TerminalInputEvent>.Builder events)
    {
        if (state == State.Paste) { Paste(value, events); return; }
        if (state == State.Text)
        {
            if (value == '\u001b') { EmitText(events); BeginEscape(); }
            else if (value < ' ' || value == '\u007f') { EmitText(events); EmitBound(events, Control(value), value.ToString()); }
            else text.Append(value);
            return;
        }
        if (state == State.Escape)
        {
            if (value == '\u001b') { EmitBound(events, new TerminalKey("Escape"), "\u001b"); BeginEscape(); return; }
            if (value is '[' or 'O' or ']' or 'P')
            {
                sequence.Append(value); state = value switch { '[' => State.Csi, 'O' => State.Ss3, ']' => State.Osc, _ => State.Dcs };
                return;
            }
            EmitBound(events, value < ' ' || value == '\u007f'
                ? Control(value) with { Modifiers = Control(value).Modifiers | TerminalModifiers.Alt }
                : new TerminalKey(value.ToString(), TerminalModifiers.Alt), "\u001b" + value);
            Reset(); return;
        }
        if (sequence.Length >= options.MaximumSequenceCharacters) Fail(TerminalInputFailure.ResourceLimit);
        // An ESC inside CSI/SS3 begins a new sequence; string protocols instead consume their ST terminator.
        if (value == '\u001b' && state is State.Csi or State.Ss3)
        { events.Add(new TerminalUnknownSequence(sequence.ToString())); BeginEscape(); return; }
        sequence.Append(value);
        if (state is State.Osc or State.Dcs)
        {
            if ((state == State.Osc && value == '\a') || (value == '\\' && sequence.Length >= 2 && sequence[^2] == '\u001b'))
            { events.Add(new TerminalProtocol(sequence.ToString())); Reset(); }
            return;
        }
        if (value is >= '\u0040' and <= '\u007e')
        {
            var raw = sequence.ToString(); var decoded = Decode(raw);
            if (raw == "\u001b[200~") { state = State.Paste; sequence.Clear(); paste.Clear(); pasteEnd.Clear(); }
            else { EmitBound(events, decoded, raw); Reset(); }
        }
        else if (value is < '\u0020' or > '\u003f')
        { events.Add(new TerminalUnknownSequence(sequence.ToString())); Reset(); }
    }

    private void Paste(char value, ImmutableArray<TerminalInputEvent>.Builder events)
    {
        pasteEnd.Append(value);
        while (!PasteEnd.AsSpan().StartsWith(pasteEnd.ToString().AsSpan(), StringComparison.Ordinal))
        {
            if (paste.Length >= options.MaximumPasteCharacters) Fail(TerminalInputFailure.ResourceLimit);
            paste.Append(pasteEnd[0]); pasteEnd.Remove(0, 1);
        }
        if (pasteEnd.Length == PasteEnd.Length)
        { events.Add(new TerminalPaste(paste.ToString())); paste.Clear(); pasteEnd.Clear(); Reset(); }
    }

    private void BeginEscape() { sequence.Clear(); sequence.Append('\u001b'); state = State.Escape; started = time.GetTimestamp(); }
    private void Reset() { state = State.Text; sequence.Clear(); }
    private void Expire(ImmutableArray<TerminalInputEvent>.Builder events)
    {
        if (state is State.Text or State.Paste) return;
        if (time.GetElapsedTime(started) < (state == State.Escape ? escapeTimeout : sequenceTimeout)) return;
        EmitBound(events, state == State.Escape ? new TerminalKey("Escape") : new TerminalUnknownSequence(sequence.ToString()), sequence.ToString()); Reset();
    }
    private void EmitText(ImmutableArray<TerminalInputEvent>.Builder events)
    { if (text.Length != 0) { var raw = text.ToString(); EmitBound(events, new TerminalText(raw), raw); text.Clear(); } }
    private void EmitBound(ImmutableArray<TerminalInputEvent>.Builder events, TerminalInputEvent input, string raw)
    { bindingOrigins.Add(input, new(raw, kittyProtocolActive, windowsTerminal)); events.Add(input); }
    private void EnsureOpen() { if (closed) throw new TerminalInputException(TerminalInputFailure.Closed); }
    private void Fail(TerminalInputFailure failure)
    { closed = true; highSurrogate = '\0'; text.Clear(); sequence.Clear(); paste.Clear(); pasteEnd.Clear(); throw new TerminalInputException(failure); }

    private static TerminalKey Control(char value) => value switch
    {
        '\0' => new("Space", TerminalModifiers.Control), '\t' => new("Tab"), '\r' => new("Enter"),
        '\n' => new("j", TerminalModifiers.Control),
        '\b' or '\u007f' => new("Backspace"),
        '\u001f' => new("-", TerminalModifiers.Control),
        >= '\u0001' and <= '\u001a' => new(((char)('a' + value - 1)).ToString(), TerminalModifiers.Control),
        _ => new(((char)('@' + value)).ToString(), TerminalModifiers.Control)
    };

    private static TerminalInputEvent Decode(string raw)
    {
        var final = raw[^1]; var body = raw[2..^1];
        if (raw == "\u001bOM") return new TerminalKey("Enter");
        if (raw == "\u001b[I" || raw == "\u001b[O") return new TerminalProtocol(raw);
        if (raw[1] == '[' && ((final == 'c' && (body.StartsWith('?') || body.StartsWith('>') || body.StartsWith('='))) ||
            (final == 'R' && body.Split(';') is var report && report.Length == 2 && report.All(IsNumber))))
            return new TerminalProtocol(raw);
        if (final == 'u' && raw[1] == '[') return Kitty(body, raw);
        if (final == '~' && raw[1] == '[' && body.StartsWith("27;", StringComparison.Ordinal)) return ModifyOtherKeys(body, raw);
        var values = body.Length == 0 ? Array.Empty<string>() : body.Split(';');
        // Kitty's CSI navigation form carries modifier:event, including release events.
        // Keep its action and wire provenance rather than rejecting the colon as non-numeric.
        if (raw[1] == '[' && (final is 'A' or 'B' or 'C' or 'D' or 'H' or 'F') &&
            values.Length == 2 && values[0] == "1" && values[1].Contains(':'))
        {
            var parts = values[1].Split(':');
            if (parts.Length != 2 || !KittyModifiers(parts[0], out var navigationModifiers) ||
                !Number(parts[1], out var kind) || kind is < 1 or > 3) return new TerminalUnknownSequence(raw);
            var navigationKey = final switch
            {
                'A' => "Up", 'B' => "Down", 'C' => "Right", 'D' => "Left", 'H' => "Home", _ => "End"
            };
            return new TerminalKey(navigationKey, navigationModifiers, (TerminalKeyAction)(kind - 1));
        }
        if (values.Any(value => !IsNumber(value)) || values.Length > 2) return new TerminalUnknownSequence(raw);
        var modifiers = TerminalModifiers.None;
        if (values.Length == 2 && !Modifiers(values[1], out modifiers)) return new TerminalUnknownSequence(raw);
        var key = final switch
        {
            'A' => "Up", 'B' => "Down", 'C' => "Right", 'D' => "Left", 'H' => "Home", 'F' => "End",
            'P' => "F1", 'Q' => "F2", 'R' => "F3", 'S' => "F4", 'Z' => "Tab", _ => null
        };
        if (key is not null)
        {
            if (values.Length != 0 && values[0] != "1") return new TerminalUnknownSequence(raw);
            return new TerminalKey(key, final == 'Z' ? modifiers | TerminalModifiers.Shift : modifiers);
        }
        if (final == '~' && values.Length is 1 or 2)
        {
            key = values[0] switch
            {
                "1" or "7" => "Home", "2" => "Insert", "3" => "Delete", "4" or "8" => "End", "5" => "PageUp", "6" => "PageDown",
                "11" => "F1", "12" => "F2", "13" => "F3", "14" => "F4", "15" => "F5", "17" => "F6",
                "18" => "F7", "19" => "F8", "20" => "F9", "21" => "F10", "23" => "F11", "24" => "F12", _ => null
            };
            if (key is not null) return new TerminalKey(key, modifiers);
        }
        return new TerminalUnknownSequence(raw);
    }

    private static TerminalInputEvent Kitty(string body, string raw)
    {
        // Scalar primary and optional shifted/base fields; binding modifiers plus lock state and key action.
        var fields = body.Split(';');
        if (fields.Length is < 1 or > 2) return new TerminalUnknownSequence(raw);
        var keys = fields[0].Split(':');
        // The Source regex also permits an event after a complete alternate triple without a modifier field.
        var implicitModifierEvent = fields.Length == 1 && keys.Length == 4;
        if (keys.Length is < 1 or > 4 || (keys.Length == 4 && !implicitModifierEvent) ||
            !Number(keys[0], out var code) || !Rune.IsValid(code)) return new TerminalUnknownSequence(raw);
        int? shifted = null, baseKey = null;
        if (keys.Length >= 2 && keys[1].Length != 0)
        {
            if (!Number(keys[1], out var value) || !Rune.IsValid(value)) return new TerminalUnknownSequence(raw);
            shifted = value;
        }
        if (keys.Length >= 3)
        {
            if (!Number(keys[2], out var value) || !Rune.IsValid(value)) return new TerminalUnknownSequence(raw);
            baseKey = value;
        }
        var modifiers = TerminalModifiers.None; var action = TerminalKeyAction.Press;
        var rawModifiers = 0;
        if (implicitModifierEvent)
        {
            if (!Number(keys[3], out var kind) || kind is < 1 or > 3) return new TerminalUnknownSequence(raw);
            action = (TerminalKeyAction)(kind - 1);
        }
        if (fields.Length == 2)
        {
            var parts = fields[1].Split(':');
            if (parts.Length is < 1 or > 2 || !KittyModifiers(parts[0], out modifiers)) return new TerminalUnknownSequence(raw);
            _ = Number(parts[0], out var encoded); rawModifiers = encoded - 1;
            if (parts.Length == 2)
            {
                if (!Number(parts[1], out var kind) || kind is < 1 or > 3) return new TerminalUnknownSequence(raw);
                action = (TerminalKeyAction)(kind - 1);
            }
        }
        var key = code switch
        {
            9 => "Tab", 13 or 57414 => "Enter", 27 => "Escape", 127 => "Backspace",
            57348 or 57425 => "Insert", 57426 => "Delete",
            57350 or 57417 => "Left", 57351 or 57418 => "Right",
            57352 or 57419 => "Up", 57353 or 57420 => "Down",
            57354 or 57421 => "PageUp", 57355 or 57422 => "PageDown",
            57356 or 57423 => "Home", 57357 or 57424 => "End",
            >= 57399 and <= 57408 => ((char)('0' + code - 57399)).ToString(),
            57409 => ".", 57410 => "/", 57411 => "*", 57412 => "-", 57413 => "+",
            57415 => "=", 57416 => ",",
            >= 57364 and <= 57375 => "F" + (code - 57363).ToString(CultureInfo.InvariantCulture),
            _ => new Rune(code).ToString()
        };
        var decoded = new TerminalKey(key, modifiers, action);
        if (keys.Length > 1)
        {
            var origin = new KittyAlternateOrigin(raw, code, shifted, baseKey, rawModifiers);
            printableTargets.Add(decoded, new PrintableTarget(KittyPrintable(origin), Kitty: origin));
        }
        // Source keypad Enter is a named binding normally, but a printable target while a jump is pending.
        else if (code == 57414 && (modifiers is TerminalModifiers.None or TerminalModifiers.Shift))
            printableTargets.Add(decoded, new PrintableTarget(new Rune(code).ToString()));
        return decoded;
    }

    // Default editor ordering follows the pinned Source editor; global Ctrl+C remains ahead of component
    // dispatch through the existing host projection. Custom bindings, selectable widgets and viewport
    // interception remain their owners' contracts. No event record or mutable protocol mode is introduced.
    private static readonly (int Code, TerminalModifiers Modifiers, string Key)[] kittyEditorBindings =
    [
        (99, TerminalModifiers.Control, "c"), (45, TerminalModifiers.Control, "-"), (9, TerminalModifiers.None, "Tab"),
        (107, TerminalModifiers.Control, "k"), (117, TerminalModifiers.Control, "u"),
        (119, TerminalModifiers.Control, "w"), (127, TerminalModifiers.Alt, "Backspace"),
        (100, TerminalModifiers.Alt, "d"), (-10, TerminalModifiers.Alt, "Delete"),
        (127, TerminalModifiers.None, "Backspace"), (127, TerminalModifiers.Shift, "Backspace"),
        (-10, TerminalModifiers.None, "Delete"), (100, TerminalModifiers.Control, "d"), (-10, TerminalModifiers.Shift, "Delete"),
        (121, TerminalModifiers.Control, "y"), (121, TerminalModifiers.Alt, "y"),
        (-14, TerminalModifiers.None, "Home"), (-14, TerminalModifiers.Control, "Home"), (97, TerminalModifiers.Control, "a"),
        (-15, TerminalModifiers.None, "End"), (-15, TerminalModifiers.Control, "End"), (101, TerminalModifiers.Control, "e"),
        (-4, TerminalModifiers.Alt, "Left"), (-4, TerminalModifiers.Control, "Left"), (98, TerminalModifiers.Alt, "b"),
        (-3, TerminalModifiers.Alt, "Right"), (-3, TerminalModifiers.Control, "Right"), (102, TerminalModifiers.Alt, "f"),
        (13, TerminalModifiers.Shift, "Enter"), (57414, TerminalModifiers.Shift, "Enter"), (106, TerminalModifiers.Control, "j"),
        (13, TerminalModifiers.None, "Enter"), (57414, TerminalModifiers.None, "Enter"),
        (-1, TerminalModifiers.None, "Up"), (-2, TerminalModifiers.None, "Down"),
        (-3, TerminalModifiers.None, "Right"), (102, TerminalModifiers.Control, "f"),
        (-4, TerminalModifiers.None, "Left"), (98, TerminalModifiers.Control, "b"),
        (-12, TerminalModifiers.None, "PageUp"), (-12, TerminalModifiers.Control, "PageUp"),
        (-13, TerminalModifiers.None, "PageDown"), (-13, TerminalModifiers.Control, "PageDown"),
        (93, TerminalModifiers.Control, "]"), (93, TerminalModifiers.Control | TerminalModifiers.Alt, "]"),
        (32, TerminalModifiers.Shift, " "),
    ];

    private static string? KittyEditorBinding(KittyAlternateOrigin origin)
    {
        foreach (var binding in kittyEditorBindings)
            if (KittyMatches(origin, binding.Code, binding.Modifiers)) return binding.Key;
        return null;
    }

    private static bool KittyMatches(KittyAlternateOrigin origin, int expected, TerminalModifiers modifiers)
    {
        if ((origin.RawModifiers & ~(64 | 128)) != (int)modifiers) return false;
        var code = KittySourceCode(origin.Code);
        var expectedCode = KittySourceCode(expected);
        if ((origin.RawModifiers & 1) != 0 && code is >= 65 and <= 90) code += 32;
        if ((modifiers & TerminalModifiers.Shift) != 0 && expectedCode is >= 65 and <= 90) expectedCode += 32;
        if (code == expectedCode) return true;
        // Source matchesKey permits digit fallback even though parseKey keeps a digit's primary identity.
        // Base is compared to the requested raw codepoint, without independently normalizing it.
        return origin.Base == expected && code is not (>= 97 and <= 122) &&
            !"`-=[]\\;',./!@#$%^&*()_+|~{}:<>?".Contains((char)(code & 0xffff));
    }

    private static string? KittyPrintable(KittyAlternateOrigin origin)
    {
        if ((origin.RawModifiers & ~(1 | 64 | 128)) != 0) return null;
        var code = KittySourceCode((origin.RawModifiers & 1) != 0 && origin.Shifted is { } shifted ? shifted : origin.Code);
        return code >= 32 && Rune.IsValid(code) ? new Rune(code).ToString() : null;
    }

    private static int KittySourceCode(int code) => code switch
    {
        >= 57399 and <= 57408 => code - 57399 + 48,
        57409 => 46, 57410 => 47, 57411 => 42, 57412 => 45, 57413 => 43, 57415 => 61, 57416 => 44,
        57417 => -4, 57418 => -3, 57419 => -1, 57420 => -2,
        57421 => -12, 57422 => -13, 57423 => -14, 57424 => -15, 57425 => -11, 57426 => -10,
        _ => code,
    };
    private static TerminalInputEvent ModifyOtherKeys(string body, string raw)
    {
        var fields = body.Split(';');
        if (fields.Length != 3 || fields[0] != "27" || !KittyModifiers(fields[1], out var modifiers) ||
            !Number(fields[2], out var code) || !Rune.IsValid(code)) return new TerminalUnknownSequence(raw);
        _ = Number(fields[1], out var encoded);
        var mask = encoded - 1;
        var scalar = new Rune(code).ToString();
        var name = code switch { 9 => "Tab", 13 => "Enter", 27 => "Escape", 127 => "Backspace", _ => scalar };
        var decoded = new TerminalKey(name, modifiers);
        var printable = (mask & ~(1 | 64 | 128)) == 0 && code >= 32 ? scalar : null;
        printableTargets.Add(decoded, new PrintableTarget(printable, raw, mask));
        return decoded;
    }

    private static bool KittyModifiers(string value, out TerminalModifiers modifiers)
    {
        modifiers = TerminalModifiers.None;
        if (!Number(value, out var encoded) || encoded < 1) return false;
        var mask = encoded - 1;
        const int lockMask = 64 | 128; // Caps Lock and Num Lock are state, not binding modifiers.
        const int semanticMask = (int)(TerminalModifiers.Shift | TerminalModifiers.Alt | TerminalModifiers.Control | TerminalModifiers.Super);
        if ((mask & ~(semanticMask | lockMask)) != 0) return false;
        modifiers = (TerminalModifiers)(mask & semanticMask);
        return true;
    }
    private static bool IsNumber(string value) => Number(value, out _);
    private static bool Number(string value, out int number) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    private static bool Modifiers(string value, out TerminalModifiers modifiers)
    {
        modifiers = TerminalModifiers.None;
        if (!Number(value, out var encoded) || encoded is < 1 or > 16) return false;
        modifiers = (TerminalModifiers)(encoded - 1); return true;
    }
}
