// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/keys.ts.
using System.Globalization;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

/// <summary>Keyboard input matching over raw terminal data: legacy sequences, the Kitty keyboard protocol and xterm
/// modifyOtherKeys. Key identifiers are strings such as <c>ctrl+c</c>, <c>shift+enter</c> or <c>escape</c>.</summary>
public static partial class Keys
{
    private static volatile bool kittyProtocolActive;
    /// <summary>Set by the process terminal after detecting Kitty protocol support.</summary>
    public static void SetKittyProtocolActive(bool active) => kittyProtocolActive = active;
    public static bool IsKittyProtocolActive => kittyProtocolActive;
    /// <summary>Overrides the Windows Terminal detection used for raw 0x08 (tests and hosts that know better).</summary>
    public static Func<bool> IsWindowsTerminalSession { get; set; } = () =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")) &&
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CLIENT")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY"));

    private static readonly HashSet<string> SymbolKeys = ["`", "-", "=", "[", "]", "\\", ";", "'", ",", ".", "/", "!", "@", "#", "$", "%", "^", "&", "*", "(", ")", "_", "+", "|", "~", "{", "}", ":", "<", ">", "?"];
    internal static bool IsSymbolKey(string key) => SymbolKeys.Contains(key);
    private const int Shift = 1, Alt = 2, Ctrl = 4, Super = 8, LockMask = 64 + 128;
    private const int CpEscape = 27, CpTab = 9, CpEnter = 13, CpSpace = 32, CpBackspace = 127, CpKpEnter = 57414;
    private const int ArrowUp = -1, ArrowDown = -2, ArrowRight = -3, ArrowLeft = -4;
    private const int FnDelete = -10, FnInsert = -11, FnPageUp = -12, FnPageDown = -13, FnHome = -14, FnEnd = -15;

    private static readonly Dictionary<int, int> KittyFunctionalEquivalents = new()
    {
        [57399] = 48, [57400] = 49, [57401] = 50, [57402] = 51, [57403] = 52, [57404] = 53, [57405] = 54, [57406] = 55, [57407] = 56, [57408] = 57,
        [57409] = 46, [57410] = 47, [57411] = 42, [57412] = 45, [57413] = 43, [57415] = 61, [57416] = 44,
        [57417] = ArrowLeft, [57418] = ArrowRight, [57419] = ArrowUp, [57420] = ArrowDown, [57421] = FnPageUp, [57422] = FnPageDown,
        [57423] = FnHome, [57424] = FnEnd, [57425] = FnInsert, [57426] = FnDelete,
    };
    private static int NormalizeKittyFunctional(int codepoint) => KittyFunctionalEquivalents.TryGetValue(codepoint, out var value) ? value : codepoint;
    private static int NormalizeShiftedLetter(int codepoint, int modifier) =>
        ((modifier & ~LockMask) & Shift) != 0 && codepoint is >= 65 and <= 90 ? codepoint + 32 : codepoint;

    private static readonly Dictionary<string, string[]> LegacyKeySequences = new(StringComparer.Ordinal)
    {
        ["up"] = ["\u001b[A", "\u001bOA"], ["down"] = ["\u001b[B", "\u001bOB"], ["right"] = ["\u001b[C", "\u001bOC"], ["left"] = ["\u001b[D", "\u001bOD"],
        ["home"] = ["\u001b[H", "\u001bOH", "\u001b[1~", "\u001b[7~"], ["end"] = ["\u001b[F", "\u001bOF", "\u001b[4~", "\u001b[8~"],
        ["insert"] = ["\u001b[2~"], ["delete"] = ["\u001b[3~"], ["pageUp"] = ["\u001b[5~", "\u001b[[5~"], ["pageDown"] = ["\u001b[6~", "\u001b[[6~"],
        ["clear"] = ["\u001b[E", "\u001bOE"],
        ["f1"] = ["\u001bOP", "\u001b[11~", "\u001b[[A"], ["f2"] = ["\u001bOQ", "\u001b[12~", "\u001b[[B"], ["f3"] = ["\u001bOR", "\u001b[13~", "\u001b[[C"],
        ["f4"] = ["\u001bOS", "\u001b[14~", "\u001b[[D"], ["f5"] = ["\u001b[15~", "\u001b[[E"], ["f6"] = ["\u001b[17~"], ["f7"] = ["\u001b[18~"],
        ["f8"] = ["\u001b[19~"], ["f9"] = ["\u001b[20~"], ["f10"] = ["\u001b[21~"], ["f11"] = ["\u001b[23~"], ["f12"] = ["\u001b[24~"],
    };
    private static readonly Dictionary<string, string[]> LegacyShiftSequences = new(StringComparer.Ordinal)
    {
        ["up"] = ["\u001b[a"], ["down"] = ["\u001b[b"], ["right"] = ["\u001b[c"], ["left"] = ["\u001b[d"], ["clear"] = ["\u001b[e"],
        ["insert"] = ["\u001b[2$"], ["delete"] = ["\u001b[3$"], ["pageUp"] = ["\u001b[5$"], ["pageDown"] = ["\u001b[6$"], ["home"] = ["\u001b[7$"], ["end"] = ["\u001b[8$"],
    };
    private static readonly Dictionary<string, string[]> LegacyCtrlSequences = new(StringComparer.Ordinal)
    {
        ["up"] = ["\u001bOa"], ["down"] = ["\u001bOb"], ["right"] = ["\u001bOc"], ["left"] = ["\u001bOd"], ["clear"] = ["\u001bOe"],
        ["insert"] = ["\u001b[2^"], ["delete"] = ["\u001b[3^"], ["pageUp"] = ["\u001b[5^"], ["pageDown"] = ["\u001b[6^"], ["home"] = ["\u001b[7^"], ["end"] = ["\u001b[8^"],
    };
    private static readonly Dictionary<string, string> LegacySequenceKeyIds = new(StringComparer.Ordinal)
    {
        ["\u001bOA"] = "up", ["\u001bOB"] = "down", ["\u001bOC"] = "right", ["\u001bOD"] = "left", ["\u001bOH"] = "home", ["\u001bOF"] = "end",
        ["\u001b[E"] = "clear", ["\u001bOE"] = "clear", ["\u001bOe"] = "ctrl+clear", ["\u001b[e"] = "shift+clear",
        ["\u001b[2~"] = "insert", ["\u001b[2$"] = "shift+insert", ["\u001b[2^"] = "ctrl+insert", ["\u001b[3$"] = "shift+delete", ["\u001b[3^"] = "ctrl+delete",
        ["\u001b[[5~"] = "pageUp", ["\u001b[[6~"] = "pageDown", ["\u001b[a"] = "shift+up", ["\u001b[b"] = "shift+down", ["\u001b[c"] = "shift+right", ["\u001b[d"] = "shift+left",
        ["\u001bOa"] = "ctrl+up", ["\u001bOb"] = "ctrl+down", ["\u001bOc"] = "ctrl+right", ["\u001bOd"] = "ctrl+left",
        ["\u001b[5$"] = "shift+pageUp", ["\u001b[6$"] = "shift+pageDown", ["\u001b[7$"] = "shift+home", ["\u001b[8$"] = "shift+end",
        ["\u001b[5^"] = "ctrl+pageUp", ["\u001b[6^"] = "ctrl+pageDown", ["\u001b[7^"] = "ctrl+home", ["\u001b[8^"] = "ctrl+end",
        ["\u001bOP"] = "f1", ["\u001bOQ"] = "f2", ["\u001bOR"] = "f3", ["\u001bOS"] = "f4", ["\u001b[11~"] = "f1", ["\u001b[12~"] = "f2", ["\u001b[13~"] = "f3", ["\u001b[14~"] = "f4",
        ["\u001b[[A"] = "f1", ["\u001b[[B"] = "f2", ["\u001b[[C"] = "f3", ["\u001b[[D"] = "f4", ["\u001b[[E"] = "f5", ["\u001b[15~"] = "f5", ["\u001b[17~"] = "f6",
        ["\u001b[18~"] = "f7", ["\u001b[19~"] = "f8", ["\u001b[20~"] = "f9", ["\u001b[21~"] = "f10", ["\u001b[23~"] = "f11", ["\u001b[24~"] = "f12",
        ["\u001bb"] = "alt+left", ["\u001bf"] = "alt+right", ["\u001bp"] = "alt+up", ["\u001bn"] = "alt+down",
    };

    private static bool MatchesLegacy(string data, string[] sequences) => Array.IndexOf(sequences, data) >= 0;
    private static bool MatchesLegacyModifier(string data, string key, int modifier) =>
        modifier == Shift ? MatchesLegacy(data, LegacyShiftSequences[key]) : modifier == Ctrl && MatchesLegacy(data, LegacyCtrlSequences[key]);

    public enum KeyEventType { Press, Repeat, Release }
    private sealed record KittySequence(int Codepoint, int? ShiftedKey, int? BaseLayoutKey, int Modifier, KeyEventType EventType);

    private static readonly string[] ReleaseMarkers = [":3u", ":3~", ":3A", ":3B", ":3C", ":3D", ":3H", ":3F"];
    private static readonly string[] RepeatMarkers = [":2u", ":2~", ":2A", ":2B", ":2C", ":2D", ":2H", ":2F"];
    /// <summary>Kitty flag 2 release events (bracketed paste content never counts).</summary>
    public static bool IsKeyRelease(string data) => !data.Contains("\u001b[200~", StringComparison.Ordinal) && ReleaseMarkers.Any(m => data.Contains(m, StringComparison.Ordinal));
    public static bool IsKeyRepeat(string data) => !data.Contains("\u001b[200~", StringComparison.Ordinal) && RepeatMarkers.Any(m => data.Contains(m, StringComparison.Ordinal));
    private static KeyEventType EventType(string? value) => value is null or "" ? KeyEventType.Press : int.Parse(value, CultureInfo.InvariantCulture) switch { 2 => KeyEventType.Repeat, 3 => KeyEventType.Release, _ => KeyEventType.Press };

    [GeneratedRegex(@"^\x1b\[(\d+)(?::(\d*))?(?::(\d+))?(?:;(\d+))?(?::(\d+))?u$")] private static partial Regex CsiU();
    [GeneratedRegex(@"^\x1b\[1;(\d+)(?::(\d+))?([ABCD])$")] private static partial Regex ArrowModified();
    [GeneratedRegex(@"^\x1b\[(\d+)(?:;(\d+))?(?::(\d+))?~$")] private static partial Regex Functional();
    [GeneratedRegex(@"^\x1b\[1;(\d+)(?::(\d+))?([HF])$")] private static partial Regex HomeEnd();
    [GeneratedRegex(@"^\x1b\[27;(\d+);(\d+)~$")] private static partial Regex ModifyOtherKeysPattern();

    private static int Int(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) ? result : int.MaxValue;
    private static KittySequence? ParseKitty(string data)
    {
        if (data.Length < 3 || data[0] != '\u001b' || data[1] != '[') return null;
        var match = CsiU().Match(data);
        if (match.Success)
        {
            return new(Int(match.Groups[1].Value), match.Groups[2].Success && match.Groups[2].Length > 0 ? Int(match.Groups[2].Value) : null,
                match.Groups[3].Success ? Int(match.Groups[3].Value) : null, (match.Groups[4].Success ? Int(match.Groups[4].Value) : 1) - 1,
                EventType(match.Groups[5].Success ? match.Groups[5].Value : null));
        }
        match = ArrowModified().Match(data);
        if (match.Success)
            return new(match.Groups[3].Value switch { "A" => ArrowUp, "B" => ArrowDown, "C" => ArrowRight, _ => ArrowLeft }, null, null,
                Int(match.Groups[1].Value) - 1, EventType(match.Groups[2].Success ? match.Groups[2].Value : null));
        match = Functional().Match(data);
        if (match.Success)
        {
            int? codepoint = Int(match.Groups[1].Value) switch { 2 => FnInsert, 3 => FnDelete, 5 => FnPageUp, 6 => FnPageDown, 7 => FnHome, 8 => FnEnd, _ => null };
            if (codepoint is { } value)
                return new(value, null, null, (match.Groups[2].Success ? Int(match.Groups[2].Value) : 1) - 1, EventType(match.Groups[3].Success ? match.Groups[3].Value : null));
        }
        match = HomeEnd().Match(data);
        if (match.Success)
            return new(match.Groups[3].Value == "H" ? FnHome : FnEnd, null, null, Int(match.Groups[1].Value) - 1, EventType(match.Groups[2].Success ? match.Groups[2].Value : null));
        return null;
    }

    private static string CharOf(int codepoint) => codepoint is >= 0 and <= 0xffff ? ((char)codepoint).ToString() : "";
    private static bool MatchesKitty(string data, int expectedCodepoint, int expectedModifier)
    {
        var parsed = ParseKitty(data);
        if (parsed is null) return false;
        if ((parsed.Modifier & ~LockMask) != (expectedModifier & ~LockMask)) return false;
        var normalized = NormalizeShiftedLetter(NormalizeKittyFunctional(parsed.Codepoint), parsed.Modifier);
        var expected = NormalizeShiftedLetter(NormalizeKittyFunctional(expectedCodepoint), expectedModifier);
        if (normalized == expected) return true;
        if (parsed.BaseLayoutKey is { } baseKey && baseKey == expectedCodepoint)
        {
            var latin = normalized is >= 97 and <= 122;
            if (!latin && !SymbolKeys.Contains(CharOf(normalized))) return true;
        }
        return false;
    }

    private static (int Codepoint, int Modifier)? ParseModifyOtherKeys(string data)
    {
        if (data.Length < 7 || data[0] != '\u001b') return null;
        var match = ModifyOtherKeysPattern().Match(data);
        return match.Success ? (Int(match.Groups[2].Value), Int(match.Groups[1].Value) - 1) : null;
    }
    private static bool MatchesModifyOtherKeys(string data, int keycode, int modifier) =>
        ParseModifyOtherKeys(data) is { } parsed && parsed.Codepoint == keycode && parsed.Modifier == modifier;
    private static bool MatchesRawBackspace(string data, int modifier)
    {
        if (data == "\u007f") return modifier == 0;
        if (data != "\b") return false;
        return IsWindowsTerminalSession() ? modifier == Ctrl : modifier == 0;
    }
    private static string? RawCtrlChar(string key)
    {
        var c = key.ToLowerInvariant()[0];
        if (c is >= 'a' and <= 'z' or '[' or '\\' or ']' or '_') return ((char)(c & 0x1f)).ToString();
        return c == '-' ? "\u001f" : null;
    }
    private static bool MatchesPrintableModifyOtherKeys(string data, int keycode, int modifier)
    {
        if (modifier == 0) return false;
        if (ParseModifyOtherKeys(data) is not { } parsed || parsed.Modifier != modifier) return false;
        return NormalizeShiftedLetter(parsed.Codepoint, parsed.Modifier) == NormalizeShiftedLetter(keycode, modifier);
    }
    private static string? FormatWithModifiers(string keyName, int modifier)
    {
        var effective = modifier & ~LockMask;
        if ((effective & ~(Shift | Ctrl | Alt | Super)) != 0) return null;
        var mods = new List<string>();
        if ((effective & Shift) != 0) mods.Add("shift");
        if ((effective & Ctrl) != 0) mods.Add("ctrl");
        if ((effective & Alt) != 0) mods.Add("alt");
        if ((effective & Super) != 0) mods.Add("super");
        return mods.Count > 0 ? string.Join('+', mods) + "+" + keyName : keyName;
    }

    /// <summary>Matches raw terminal input against a key identifier such as <c>ctrl+c</c> or <c>shift+tab</c>.</summary>
    public static bool Matches(string data, string keyId)
    {
        var parts = keyId.ToLowerInvariant().Split('+');
        var key = parts[^1];
        if (key.Length == 0) return false;
        var modifier = 0;
        if (parts.Contains("shift")) modifier |= Shift;
        if (parts.Contains("alt")) modifier |= Alt;
        if (parts.Contains("ctrl")) modifier |= Ctrl;
        if (parts.Contains("super")) modifier |= Super;
        var kitty = kittyProtocolActive;
        switch (key)
        {
            case "escape" or "esc":
                return modifier == 0 && (data == "\u001b" || MatchesKitty(data, CpEscape, 0) || MatchesModifyOtherKeys(data, CpEscape, 0));
            case "space":
                if (!kitty)
                {
                    if (modifier == Ctrl && data == "\0") return true;
                    if (modifier == Alt && data == "\u001b ") return true;
                }
                if (modifier == 0) return data == " " || MatchesKitty(data, CpSpace, 0) || MatchesModifyOtherKeys(data, CpSpace, 0);
                return MatchesKitty(data, CpSpace, modifier) || MatchesModifyOtherKeys(data, CpSpace, modifier);
            case "tab":
                if (modifier == Shift) return data == "\u001b[Z" || MatchesKitty(data, CpTab, Shift) || MatchesModifyOtherKeys(data, CpTab, Shift);
                if (modifier == 0) return data == "\t" || MatchesKitty(data, CpTab, 0);
                return MatchesKitty(data, CpTab, modifier) || MatchesModifyOtherKeys(data, CpTab, modifier);
            case "enter" or "return":
                if (modifier == Shift)
                {
                    if (MatchesKitty(data, CpEnter, Shift) || MatchesKitty(data, CpKpEnter, Shift)) return true;
                    if (MatchesModifyOtherKeys(data, CpEnter, Shift)) return true;
                    return kitty && (data == "\u001b\r" || data == "\n");
                }
                if (modifier == Alt)
                {
                    if (MatchesKitty(data, CpEnter, Alt) || MatchesKitty(data, CpKpEnter, Alt)) return true;
                    if (MatchesModifyOtherKeys(data, CpEnter, Alt)) return true;
                    return !kitty && data == "\u001b\r";
                }
                if (modifier == 0)
                    return data == "\r" || !kitty && data == "\n" || data == "\u001bOM" || MatchesKitty(data, CpEnter, 0) || MatchesKitty(data, CpKpEnter, 0);
                return MatchesKitty(data, CpEnter, modifier) || MatchesKitty(data, CpKpEnter, modifier) || MatchesModifyOtherKeys(data, CpEnter, modifier);
            case "backspace":
                if (modifier == Alt)
                    return data is "\u001b\u007f" or "\u001b\b" || MatchesKitty(data, CpBackspace, Alt) || MatchesModifyOtherKeys(data, CpBackspace, Alt);
                if (modifier == Ctrl)
                    return MatchesRawBackspace(data, Ctrl) || MatchesKitty(data, CpBackspace, Ctrl) || MatchesModifyOtherKeys(data, CpBackspace, Ctrl);
                if (modifier == 0) return MatchesRawBackspace(data, 0) || MatchesKitty(data, CpBackspace, 0) || MatchesModifyOtherKeys(data, CpBackspace, 0);
                return MatchesKitty(data, CpBackspace, modifier) || MatchesModifyOtherKeys(data, CpBackspace, modifier);
            case "insert": return Functional(data, "insert", FnInsert, modifier);
            case "delete": return Functional(data, "delete", FnDelete, modifier);
            case "clear": return modifier == 0 ? MatchesLegacy(data, LegacyKeySequences["clear"]) : MatchesLegacyModifier(data, "clear", modifier);
            case "home": return Functional(data, "home", FnHome, modifier);
            case "end": return Functional(data, "end", FnEnd, modifier);
            case "pageup": return Functional(data, "pageUp", FnPageUp, modifier);
            case "pagedown": return Functional(data, "pageDown", FnPageDown, modifier);
            case "up":
                if (modifier == Alt) return data == "\u001bp" || MatchesKitty(data, ArrowUp, Alt);
                return Functional(data, "up", ArrowUp, modifier);
            case "down":
                if (modifier == Alt) return data == "\u001bn" || MatchesKitty(data, ArrowDown, Alt);
                return Functional(data, "down", ArrowDown, modifier);
            case "left":
                if (modifier == Alt) return data == "\u001b[1;3D" || !kitty && data == "\u001bB" || data == "\u001bb" || MatchesKitty(data, ArrowLeft, Alt);
                if (modifier == Ctrl) return data == "\u001b[1;5D" || MatchesLegacyModifier(data, "left", Ctrl) || MatchesKitty(data, ArrowLeft, Ctrl);
                return Functional(data, "left", ArrowLeft, modifier);
            case "right":
                if (modifier == Alt) return data == "\u001b[1;3C" || !kitty && data == "\u001bF" || data == "\u001bf" || MatchesKitty(data, ArrowRight, Alt);
                if (modifier == Ctrl) return data == "\u001b[1;5C" || MatchesLegacyModifier(data, "right", Ctrl) || MatchesKitty(data, ArrowRight, Ctrl);
                return Functional(data, "right", ArrowRight, modifier);
            case "f1" or "f2" or "f3" or "f4" or "f5" or "f6" or "f7" or "f8" or "f9" or "f10" or "f11" or "f12":
                return modifier == 0 && MatchesLegacy(data, LegacyKeySequences[key]);
        }
        if (key.Length == 1 && (key[0] is >= 'a' and <= 'z' or >= '0' and <= '9' || SymbolKeys.Contains(key)))
        {
            int codepoint = key[0];
            var rawCtrl = RawCtrlChar(key);
            var isLetter = key[0] is >= 'a' and <= 'z';
            var isDigit = key[0] is >= '0' and <= '9';
            if (modifier == Ctrl + Alt && !kitty && rawCtrl is not null && data == "\u001b" + rawCtrl) return true;
            if (modifier == Alt && !kitty && (isLetter || isDigit || SymbolKeys.Contains(key)) && data == "\u001b" + key) return true;
            if (modifier == Ctrl)
                return rawCtrl is not null && data == rawCtrl || MatchesKitty(data, codepoint, Ctrl) || MatchesPrintableModifyOtherKeys(data, codepoint, Ctrl);
            if (modifier == Shift + Ctrl)
                return MatchesKitty(data, codepoint, Shift + Ctrl) || MatchesPrintableModifyOtherKeys(data, codepoint, Shift + Ctrl);
            if (modifier == Shift)
                return isLetter && data == key.ToUpperInvariant() || MatchesKitty(data, codepoint, Shift) || MatchesPrintableModifyOtherKeys(data, codepoint, Shift);
            if (modifier != 0) return MatchesKitty(data, codepoint, modifier) || MatchesPrintableModifyOtherKeys(data, codepoint, modifier);
            return data == key || MatchesKitty(data, codepoint, 0);
        }
        return false;

        static bool Functional(string data, string name, int codepoint, int modifier)
        {
            if (modifier == 0) return MatchesLegacy(data, LegacyKeySequences[name]) || MatchesKitty(data, codepoint, 0);
            if (MatchesLegacyModifier(data, name, modifier)) return true;
            return MatchesKitty(data, codepoint, modifier);
        }
    }

    private static string? FormatParsedKey(int codepoint, int modifier, int? baseLayoutKey)
    {
        var identity = NormalizeShiftedLetter(NormalizeKittyFunctional(codepoint), modifier);
        var isLatin = identity is >= 97 and <= 122; var isDigit = identity is >= 48 and <= 57;
        var effective = isLatin || isDigit || SymbolKeys.Contains(CharOf(identity)) ? identity : baseLayoutKey ?? identity;
        string? name = effective switch
        {
            CpEscape => "escape", CpTab => "tab", CpEnter or CpKpEnter => "enter", CpSpace => "space", CpBackspace => "backspace",
            FnDelete => "delete", FnInsert => "insert", FnHome => "home", FnEnd => "end", FnPageUp => "pageUp", FnPageDown => "pageDown",
            ArrowUp => "up", ArrowDown => "down", ArrowLeft => "left", ArrowRight => "right",
            >= 48 and <= 57 or >= 97 and <= 122 => ((char)effective).ToString(),
            _ => SymbolKeys.Contains(CharOf(effective)) ? CharOf(effective) : null
        };
        return name is null ? null : FormatWithModifiers(name, modifier);
    }

    /// <summary>The key identifier of raw input, or null when it is not a recognized key.</summary>
    public static string? Parse(string data)
    {
        if (ParseKitty(data) is { } kittySequence) return FormatParsedKey(kittySequence.Codepoint, kittySequence.Modifier, kittySequence.BaseLayoutKey);
        if (ParseModifyOtherKeys(data) is { } other) return FormatParsedKey(other.Codepoint, other.Modifier, null);
        var kitty = kittyProtocolActive;
        if (kitty && data is "\u001b\r" or "\n") return "shift+enter";
        if (LegacySequenceKeyIds.TryGetValue(data, out var legacy)) return legacy;
        switch (data)
        {
            case "\u001b": return "escape";
            case "\u001c": return "ctrl+\\";
            case "\u001d": return "ctrl+]";
            case "\u001f": return "ctrl+-";
            case "\u001b\u001b": return "ctrl+alt+[";
            case "\u001b\u001c": return "ctrl+alt+\\";
            case "\u001b\u001d": return "ctrl+alt+]";
            case "\u001b\u001f": return "ctrl+alt+-";
            case "\t": return "tab";
        }
        if (data == "\r" || !kitty && data == "\n" || data == "\u001bOM") return "enter";
        if (data == "\0") return "ctrl+space";
        if (data == " ") return "space";
        if (data == "\u007f") return "backspace";
        if (data == "\b") return IsWindowsTerminalSession() ? "ctrl+backspace" : "backspace";
        if (data == "\u001b[Z") return "shift+tab";
        if (!kitty && data == "\u001b\r") return "alt+enter";
        if (!kitty && data == "\u001b ") return "alt+space";
        if (data is "\u001b\u007f" or "\u001b\b") return "alt+backspace";
        if (!kitty && data == "\u001bB") return "alt+left";
        if (!kitty && data == "\u001bF") return "alt+right";
        if (!kitty && data.Length == 2 && data[0] == '\u001b')
        {
            int code = data[1];
            if (code is >= 1 and <= 26) return "ctrl+alt+" + (char)(code + 96);
            var k = data[1].ToString();
            if (code is >= 97 and <= 122 or >= 48 and <= 57 || SymbolKeys.Contains(k)) return "alt+" + k;
        }
        switch (data)
        {
            case "\u001b[A": return "up";
            case "\u001b[B": return "down";
            case "\u001b[C": return "right";
            case "\u001b[D": return "left";
            case "\u001b[H" or "\u001bOH": return "home";
            case "\u001b[F" or "\u001bOF": return "end";
            case "\u001b[3~": return "delete";
            case "\u001b[5~": return "pageUp";
            case "\u001b[6~": return "pageDown";
        }
        if (data.Length == 1)
        {
            int code = data[0];
            if (code is >= 1 and <= 26) return "ctrl+" + (char)(code + 96);
            if (code is >= 32 and <= 126) return data;
        }
        return null;
    }

    /// <summary>The printable character of a Kitty CSI-u sequence for plain or shifted text keys.</summary>
    public static string? DecodeKittyPrintable(string data)
    {
        var match = CsiU().Match(data);
        if (!match.Success) return null;
        var codepoint = Int(match.Groups[1].Value);
        int? shifted = match.Groups[2].Success && match.Groups[2].Length > 0 ? Int(match.Groups[2].Value) : null;
        var modifier = (match.Groups[4].Success ? Int(match.Groups[4].Value) : 1) - 1;
        if ((modifier & ~(Shift | LockMask)) != 0) return null;
        if ((modifier & (Alt | Ctrl)) != 0) return null;
        var effective = (modifier & Shift) != 0 && shifted is { } s ? s : codepoint;
        effective = NormalizeKittyFunctional(effective);
        if (effective < 32 || effective > 0x10ffff || effective is >= 0xd800 and <= 0xdfff) return null;
        return char.ConvertFromUtf32(effective);
    }

    private static string? DecodeModifyOtherKeysPrintable(string data)
    {
        if (ParseModifyOtherKeys(data) is not { } parsed) return null;
        if (((parsed.Modifier & ~LockMask) & ~Shift) != 0) return null;
        if (parsed.Codepoint < 32 || parsed.Codepoint > 0x10ffff || parsed.Codepoint is >= 0xd800 and <= 0xdfff) return null;
        return char.ConvertFromUtf32(parsed.Codepoint);
    }

    public static string? DecodePrintableKey(string data) => DecodeKittyPrintable(data) ?? DecodeModifyOtherKeysPrintable(data);
}
