// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/utils/streaming-json.ts.
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using PiSharp.Contracts;

namespace PiSharp.AI;

/// <summary>One step of a <see cref="JsonChange"/> path: a member key or an array index.</summary>
[Experimental("PISHARP_AI001")]
public readonly record struct JsonPathSegment
{
    private JsonPathSegment(string? key, int index) { Key = key; Index = index; }
    /// <summary>The member key, or null for an array index.</summary>
    public string? Key { get; }
    /// <summary>The array index; -1 for a member key.</summary>
    public int Index { get; }
    public static JsonPathSegment Member(string key) => new(key ?? throw new ArgumentNullException(nameof(key)), -1);
    public static JsonPathSegment Element(int index) =>
        new(null, index >= 0 ? index : throw new ArgumentOutOfRangeException(nameof(index)));
    public override string ToString() => Key ?? Index.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One change a piece of text made to the value (streaming-json.ts <c>JsonChange</c>); see <see cref="StreamingJsonParser"/>.</summary>
[Experimental("PISHARP_AI001")]
public abstract record JsonChange(ImmutableArray<JsonPathSegment> Path);

/// <summary>A key, an array item or a finished value appeared at <see cref="JsonChange.Path"/>.</summary>
[Experimental("PISHARP_AI001")]
public sealed record JsonSetChange(ImmutableArray<JsonPathSegment> Path, JsonData Value) : JsonChange(Path);

/// <summary>The piece extended the open string at <see cref="JsonChange.Path"/>.</summary>
[Experimental("PISHARP_AI001")]
public sealed record JsonAppendChange(ImmutableArray<JsonPathSegment> Path, string Text) : JsonChange(Path);

/// <summary>
/// Incremental, tolerant parsing of JSON text that arrives in pieces, such as streamed tool-call arguments.
/// <see cref="Append"/> reads each piece once, so a stream costs time and memory linear in its length, and <see cref="Snapshot"/> returns
/// the value of the text read so far. The rules for incomplete text follow <c>parseStreamingJson()</c>:
/// <list type="bullet">
/// <item>An unterminated string shows the characters read so far; an unterminated escape is held back until complete.</item>
/// <item>An unterminated container shows the members and elements read so far.</item>
/// <item>A member whose key or value has not started is omitted.</item>
/// <item>An unterminated literal shows its value once it is a prefix of <c>true</c>, <c>false</c> or <c>null</c>; an unterminated number
/// shows its value when the digits read so far form a number (an exponent without digits is ignored).</item>
/// </list>
/// Malformed text is read leniently, the way <c>repairJson()</c> and <c>parseStreamingJson()</c> treat it: raw control characters in strings
/// are kept, an invalid escape such as <c>\d</c> stays literal text, missing commas and colons are tolerated, values in invalid positions
/// and unknown words are dropped, and text after the root value is ignored. The parser never throws. Callers that need the exact value of
/// the finished text parse it once, with <see cref="StreamingJson.ParseToolCallArguments"/>.
/// <para>Differences from <c>parseStreamingJson()</c> on incomplete text: trailing whitespace inside an unterminated string is kept, a
/// string that contains a raw control character or an invalid escape stays visible while it streams, and <c>NaN</c> and <c>Infinity</c>
/// words are dropped.</para>
/// <para><see cref="Append"/> also returns the changes the piece made, for a writer that keeps its own copy of the value: a
/// <see cref="JsonSetChange"/> when a key, an array item or a finished value appears (a new string is set once with its first text, a
/// container empty), a <see cref="JsonAppendChange"/> when the piece extended an open string. Unterminated numbers and literals are not
/// reported until they end. A container nested more than 32 levels deep is reported as one set of its whole value when it closes, so
/// paths stay short.</para>
/// <para>Cost: upstream's snapshots share finished containers and copy only the open path. A snapshot here is an owned
/// <see cref="JsonData"/>, which costs the whole value, so the copy credit is spent on the text read so far instead: each append earns
/// 256 plus 8 per character, and <see cref="Snapshot"/> returns the previous snapshot while a fresh one is not yet paid for. A document
/// of a few hundred characters is fresh at every snapshot; a larger one shows a preview that lags a little behind the text, and no input
/// makes the total work grow faster than the text. A value nested deeper than <see cref="JsonData.MaximumDepth"/> levels cannot be
/// owned: snapshots then stay at the last value that could, and such a container's whole-value change is not reported.</para>
/// </summary>
[Experimental("PISHARP_AI001")]
public sealed class StreamingJsonParser
{
    // Changes are reported at most this deep, so a path costs O(1) to build.
    private const int MaximumReportedDepth = 32;
    // Copy credit per append and per appended character. The floor keeps small documents fresh at every snapshot.
    private const long CreditPerAppend = 256;
    private const long CreditPerCharacter = 8;
    // An unterminated number longer than this is not shown until it ends, so Snapshot() stays cheap.
    private const int MaximumPendingScalarLength = 64;

    private Node? _root;
    private bool _done;
    private readonly List<Frame> _stack = [];
    private OpenString? _string;
    private readonly StringBuilder _scalar = new();
    private Slot? _scalarSlot;
    private long _read;
    private int _deepest;
    private long _credit;
    private bool _dirty;
    private JsonData? _snapshot;
    private readonly List<OpenChange> _changes = [];

    /// <summary>Read the next piece of text; returns the changes it made, in order. Costs time linear in its length.</summary>
    public IReadOnlyList<JsonChange> Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _credit += CreditPerAppend + CreditPerCharacter * text.Length;
        _changes.Clear();
        if (_string is not null) _string.Change = null;
        var index = 0;
        while (index < text.Length && !_done)
        {
            var start = index;
            if (_string is { } open) index = ReadString(open, text, index);
            else if (_scalar.Length != 0) index = ReadScalar(text, index);
            else Structural(text[index++]);
            _read += index - start;
        }
        if (_changes.Count == 0) return [];
        var changes = new JsonChange[_changes.Count];
        for (var position = 0; position < changes.Length; position++) changes[position] = _changes[position].Freeze();
        _changes.Clear();
        return changes;
    }

    /// <summary>
    /// The value of the text read so far: null before a value starts, otherwise an owned JSON value. Returns the previous snapshot while
    /// the text read since did not change the value, or while a fresh copy is not yet paid for (see the class comment).
    /// </summary>
    public JsonData? Snapshot()
    {
        if (!_dirty || _deepest > JsonData.MaximumDepth) return _snapshot;
        if (_read > _credit) return _snapshot;
        _credit -= _read;
        _dirty = false;
        _snapshot = Copy();
        return _snapshot;
    }

    // --- Reading ---------------------------------------------------------------------------------------------------------------------

    // Read string characters from start until the string ends or the text does; returns the next index.
    private int ReadString(OpenString open, string text, int start)
    {
        var runStart = start;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (open.Escape.Length != 0)
            {
                if (ContinueEscape(open, character)) { runStart = index + 1; continue; }
                // Not part of the escape: read it as a normal string character below.
                runStart = index;
            }
            if (character == '"')
            {
                if (index > runStart) AppendString(open, text.AsSpan(runStart, index - runStart));
                EndString(open);
                return index + 1;
            }
            if (character == '\\')
            {
                if (index > runStart) AppendString(open, text.AsSpan(runStart, index - runStart));
                open.Escape.Append('\\');
                runStart = index + 1;
            }
        }
        if (text.Length > runStart) AppendString(open, text.AsSpan(runStart));
        return text.Length;
    }

    // Read number or literal characters from start up to the next delimiter; returns the delimiter's index.
    private int ReadScalar(string text, int start)
    {
        var end = start;
        while (end < text.Length && !IsDelimiter(text[end])) end++;
        if (end > start)
        {
            _scalar.Append(text, start, end - start);
            if (_scalarSlot is not null) _dirty = true;
        }
        if (end < text.Length) EndScalar();
        return end;
    }

    // Handle one character outside strings.
    private void Structural(char character)
    {
        switch (character)
        {
            case ' ' or '\t' or '\n' or '\r': return;
            case '"': StartString(); return;
            case '{': OpenContainer(new ObjectNode()); return;
            case '[': OpenContainer(new ArrayNode()); return;
            case '}' or ']': CloseContainer(); return;
            case ':':
            {
                if (Top is { Container: ObjectNode, Phase: Phase.Colon } frame) frame.Phase = Phase.Value;
                return;
            }
            case ',':
            {
                if (Top is { } frame) frame.Phase = frame.Container is ArrayNode ? Phase.Value : Phase.Key;
                return;
            }
            default:
                _scalar.Append(character);
                _scalarSlot = ValueSlot();
                if (_scalarSlot is not null) _dirty = true;
                return;
        }
    }

    private Frame? Top => _stack.Count == 0 ? null : _stack[^1];

    // Where a value starting now goes, or null when this position takes no value.
    private Slot? ValueSlot()
    {
        if (Top is not { } frame) return _root is null ? Slot.Root : null;
        if (frame.Container is ArrayNode array) return Slot.Element(array.Items.Count);
        // A missing colon is tolerated: {"a" 1} reads as {"a": 1}.
        if (frame.Phase is Phase.Value or Phase.Colon && frame.Key is { } key) return Slot.Member(key);
        return null;
    }

    private void StartString()
    {
        // A missing comma is tolerated: {"a": 1 "b": 2} reads the second member.
        if (Top is { Container: ObjectNode, Phase: Phase.Key or Phase.Comma })
        {
            _string = new OpenString(isKey: true, slot: null, path: null);
            return;
        }
        var slot = ValueSlot();
        var path = ReportedPath(slot);
        var open = new OpenString(isKey: false, slot, path);
        _string = open;
        Place(slot, open.Live);
        if (path is { } reported) open.Change = Report(OpenChange.Set(reported, StringValue("")));
    }

    // The path of a value at slot in the innermost open container, when its changes are reported.
    private ImmutableArray<JsonPathSegment>? ReportedPath(Slot? slot)
    {
        if (slot is not { } placed) return null;
        if (placed.IsRoot) return [];
        var frame = Top!;
        if (!frame.ReportsInside) return null;
        return frame.Path!.Value.Add(placed.Key is { } key ? JsonPathSegment.Member(key) : JsonPathSegment.Element(placed.Index));
    }

    private OpenChange Report(OpenChange change) { _changes.Add(change); return change; }

    // Continue an escape with character; returns false when it is not part of it and must be read normally.
    private bool ContinueEscape(OpenString open, char character)
    {
        if (open.Escape.Length == 1)
        {
            if (SimpleEscape(character) is { } decoded)
            {
                open.Escape.Clear();
                AppendString(open, [decoded]);
                return true;
            }
            if (character == 'u') { open.Escape.Append('u'); return true; }
            // Invalid escape: keep the backslash as text and read the character normally, as repairJson() does.
            open.Escape.Clear();
            AppendString(open, "\\");
            return false;
        }
        if (char.IsAsciiHexDigit(character))
        {
            open.Escape.Append(character);
            if (open.Escape.Length < 6) return true;
            var unit = (char)int.Parse(open.Escape.ToString(2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            open.Escape.Clear();
            AppendString(open, [unit]);
            return true;
        }
        // \u without four hex digits: keep it as text and read the character normally, as repairJson() does.
        var literal = open.Escape.ToString();
        open.Escape.Clear();
        AppendString(open, literal);
        return false;
    }

    private void AppendString(OpenString open, ReadOnlySpan<char> text)
    {
        open.Text.Append(text);
        if (open.IsKey) return;
        if (open.Slot is not null) _dirty = true;
        if (open.Path is not { } path) return;
        // Extend this string's newest change while nothing came after it; otherwise report an append.
        if (open.Change is { } change && _changes.Count != 0 && ReferenceEquals(_changes[^1], change))
        {
            change.Text.Append(text);
            return;
        }
        open.Change = Report(OpenChange.Append(path, text));
    }

    private void EndString(OpenString open)
    {
        _string = null;
        var frame = Top;
        if (open.IsKey)
        {
            if (frame is null) return;
            frame.Key = open.Text.ToString();
            frame.Phase = Phase.Colon;
            return;
        }
        // The finished text replaces the live one in place; the value it shows does not change.
        Replace(open.Slot, open.Live, new StringNode(open.Text.ToString()));
        ValueEnded(frame);
    }

    private void EndScalar()
    {
        var token = _scalar.ToString();
        var slot = _scalarSlot;
        _scalar.Clear();
        _scalarSlot = null;
        if (ScalarJson(token) is { } json)
        {
            var value = new ScalarNode(json);
            Place(slot, value);
            if (ReportedPath(slot) is { } path) Report(OpenChange.Set(path, JsonData.Parse(json)));
        }
        ValueEnded(Top);
    }

    // The JSON text of the unterminated scalar at the end of the text, if it shows.
    private (Slot Slot, string Json)? PendingScalar()
    {
        if (_scalarSlot is not { } slot || _scalar.Length == 0 || _scalar.Length > MaximumPendingScalarLength) return null;
        return PendingScalarJson(_scalar.ToString()) is { } json ? (slot, json) : null;
    }

    private void OpenContainer(Node container)
    {
        var slot = ValueSlot();
        var path = ReportedPath(slot);
        Place(slot, container);
        _stack.Add(new Frame(container, slot, path, path is { } reported && reported.Length < MaximumReportedDepth, _stack.Count + 1)
        {
            Phase = container is ArrayNode ? Phase.Value : Phase.Key
        });
        _deepest = Math.Max(_deepest, _stack.Count);
        _dirty = true;
        // A fresh container: the working one keeps changing, and a change never does.
        if (path is { } changed) Report(OpenChange.Set(changed, container is ArrayNode ? EmptyArray : JsonData.EmptyObject));
    }

    private void CloseContainer()
    {
        // A stray closer outside any container is ignored.
        if (Top is not { } frame) return;
        _stack.RemoveAt(_stack.Count - 1);
        _dirty = true;
        // Too deep to report inside: the whole value, final now that it is closed. A value deeper than an owned JsonData holds is not
        // reported (see the class comment).
        if (frame.Path is { } path && !frame.ReportsInside && _deepest - frame.Depth < JsonData.MaximumDepth)
            Report(OpenChange.Set(path, Own(frame.Container, null)));
        ValueEnded(Top);
    }

    // A value in frame, or the root value when there is no frame, has ended.
    private void ValueEnded(Frame? frame)
    {
        if (frame is null)
        {
            // Text after the root value is ignored, but only once a root value exists.
            if (_root is not null) _done = true;
            return;
        }
        frame.Phase = Phase.Comma;
    }

    // Set the value at slot in the open path; nothing for a value in an invalid position.
    private void Place(Slot? slot, Node value)
    {
        if (slot is not { } placed) return;
        _dirty = true;
        if (placed.IsRoot) { _root = value; return; }
        var frame = Top!;
        if (frame.Container is ArrayNode array)
        {
            if (placed.Index == array.Items.Count) array.Items.Add(value);
            else array.Items[placed.Index] = value;
            return;
        }
        ((ObjectNode)frame.Container).Set(placed.Key!, value);
    }

    // Replace the live string at slot once it ends, if the slot still holds it.
    private void Replace(Slot? slot, Node live, Node value)
    {
        if (slot is not { } placed) return;
        if (placed.IsRoot) { if (ReferenceEquals(_root, live)) _root = value; return; }
        var frame = Top!;
        if (frame.Container is ArrayNode array)
        {
            if (placed.Index < array.Items.Count && ReferenceEquals(array.Items[placed.Index], live)) array.Items[placed.Index] = value;
            return;
        }
        var members = (ObjectNode)frame.Container;
        if (members.Members.TryGetValue(placed.Key!, out var current) && ReferenceEquals(current, live)) members.Members[placed.Key!] = value;
    }

    // --- Snapshots -------------------------------------------------------------------------------------------------------------------

    // A fresh snapshot: the root as JSON text, with the unterminated scalar in the innermost open container.
    private JsonData? Copy()
    {
        var pending = PendingScalar();
        if (_stack.Count == 0) return pending is { Slot.IsRoot: true } root ? JsonData.Parse(root.Json) : _root is null ? null : Own(_root, null);
        if (_root is null) return null;
        return Own(_root, pending is { } scalar ? (_stack[^1].Container, scalar.Slot, scalar.Json) : null);
    }

    private static JsonData Own(Node value, (Node Container, Slot Slot, string Json)? pending)
    {
        var builder = new StringBuilder();
        Write(builder, value, pending);
        return JsonData.Parse(builder.ToString());
    }

    // JSON.stringify of the value: array-index keys first in ascending order, then the other keys in insertion order.
    private static void Write(StringBuilder builder, Node value, (Node Container, Slot Slot, string Json)? pending)
    {
        var overlay = pending is { } open && ReferenceEquals(open.Container, value) ? open : ((Node, Slot, string)?)null;
        switch (value)
        {
            case ScalarNode scalar: builder.Append(scalar.Json); return;
            case StringNode text: StreamingJson.Quote(builder, text.Text); return;
            case LiveStringNode live: StreamingJson.Quote(builder, live.Text.ToString()); return;
            case ArrayNode array:
                builder.Append('[');
                for (var index = 0; index < array.Items.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    Write(builder, array.Items[index], pending);
                }
                if (overlay is { } element && element.Item2.Index == array.Items.Count)
                {
                    if (array.Items.Count > 0) builder.Append(',');
                    builder.Append(element.Item3);
                }
                builder.Append(']');
                return;
            case ObjectNode members:
            {
                var keys = members.Order;
                var pendingKey = overlay?.Item2.Key;
                if (pendingKey is not null && !members.Members.ContainsKey(pendingKey)) keys = [.. keys, pendingKey];
                builder.Append('{');
                var first = true;
                foreach (var key in StreamingJson.PropertyOrder(keys))
                {
                    if (!first) builder.Append(',');
                    first = false;
                    StreamingJson.Quote(builder, key);
                    builder.Append(':');
                    if (key == pendingKey) builder.Append(overlay!.Value.Item3);
                    else Write(builder, members.Members[key], pending);
                }
                builder.Append('}');
                return;
            }
        }
    }

    // --- Values ----------------------------------------------------------------------------------------------------------------------

    private static readonly JsonData EmptyArray = JsonData.Parse("[]");

    private static JsonData StringValue(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        StreamingJson.Quote(builder, text);
        return JsonData.Parse(builder.ToString());
    }

    private static char? SimpleEscape(char character) => character switch
    {
        '"' => '"', '\\' => '\\', '/' => '/', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => null
    };

    private static bool IsDelimiter(char character) =>
        character is ' ' or '\t' or '\n' or '\r' or ',' or ':' or '{' or '}' or '[' or ']' or '"';

    // The JSON text of a complete number or literal token; null for anything else, which is dropped.
    private static string? ScalarJson(string token)
    {
        if (token is "true" or "false" or "null") return token;
        return IsNumber(token, out var length) && length == token.Length ? NumberJson(token) : null;
    }

    // The JSON text an unterminated number or literal token shows.
    private static string? PendingScalarJson(string token)
    {
        foreach (var literal in (ReadOnlySpan<string>)["true", "false", "null"])
            if (literal.StartsWith(token, StringComparison.Ordinal)) return literal;
        if (IsNumber(token, out var length) && length == token.Length) return NumberJson(token);
        // A number whose exponent has no digits yet shows its mantissa.
        if (length > 0 && length < token.Length && token[length] is 'e' or 'E')
        {
            var rest = token.Length - length - 1;
            if (rest == 0 || rest == 1 && token[^1] is '+' or '-') return NumberJson(token[..length]);
        }
        return null;
    }

    // Matches /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/ from the start: length is the longest prefix that is a whole number without
    // its exponent when the exponent is incomplete, so PendingScalarJson can read the mantissa.
    private static bool IsNumber(string token, out int length)
    {
        length = 0;
        var index = 0;
        if (index < token.Length && token[index] == '-') index++;
        if (index >= token.Length) return false;
        if (token[index] == '0') index++;
        else if (token[index] is >= '1' and <= '9') { while (index < token.Length && char.IsAsciiDigit(token[index])) index++; }
        else return false;
        if (index < token.Length && token[index] == '.')
        {
            var fraction = index + 1;
            while (fraction < token.Length && char.IsAsciiDigit(token[fraction])) fraction++;
            if (fraction == index + 1) { length = index; return false; }
            index = fraction;
        }
        length = index;
        if (index < token.Length && token[index] is 'e' or 'E')
        {
            var exponent = index + 1;
            if (exponent < token.Length && token[exponent] is '+' or '-') exponent++;
            var digits = exponent;
            while (digits < token.Length && char.IsAsciiDigit(token[digits])) digits++;
            if (digits == exponent) return false;
            length = digits;
        }
        return true;
    }

    // JSON.stringify(Number(token)).
    private static string NumberJson(string token) =>
        JsonNumber.Text(double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture));

    // --- State -----------------------------------------------------------------------------------------------------------------------

    private abstract class Node;
    private sealed class ScalarNode(string json) : Node { internal readonly string Json = json; }
    private sealed class StringNode(string text) : Node { internal readonly string Text = text; }
    // The open string, placed in its slot until it ends.
    private sealed class LiveStringNode(StringBuilder text) : Node { internal readonly StringBuilder Text = text; }
    private sealed class ArrayNode : Node { internal readonly List<Node> Items = []; }
    private sealed class ObjectNode : Node
    {
        internal readonly List<string> Order = [];
        internal readonly Dictionary<string, Node> Members = new(StringComparer.Ordinal);

        // A duplicate key keeps its first position and takes the last value, as JSON.parse does; "__proto__" is an own member.
        internal void Set(string key, Node value)
        {
            if (!Members.ContainsKey(key)) Order.Add(key);
            Members[key] = value;
        }
    }

    // Where a value sits in its parent; null for a value in an invalid position, which is read but dropped.
    private readonly record struct Slot(bool IsRoot, string? Key, int Index)
    {
        internal static Slot Root => new(true, null, -1);
        internal static Slot Member(string key) => new(false, key, -1);
        internal static Slot Element(int index) => new(false, null, index);
    }

    private enum Phase { Key, Colon, Value, Comma }

    // An open object or array. Its container is never handed out while open; snapshots are owned copies.
    private sealed class Frame(Node container, Slot? slot, ImmutableArray<JsonPathSegment>? path, bool reportsInside, int depth)
    {
        internal readonly Node Container = container;
        internal readonly Slot? Slot = slot;
        // Where the container sits, when its changes are reported.
        internal readonly ImmutableArray<JsonPathSegment>? Path = path;
        // Whether changes inside it are reported; when not, its whole value is reported as it closes.
        internal readonly bool ReportsInside = reportsInside;
        internal readonly int Depth = depth;
        // Objects: the key read last, where the member value goes.
        internal string? Key;
        internal Phase Phase;
    }

    // An unterminated string.
    private sealed class OpenString
    {
        internal OpenString(bool isKey, Slot? slot, ImmutableArray<JsonPathSegment>? path)
        {
            IsKey = isKey; Slot = slot; Path = path; Live = new LiveStringNode(Text);
        }
        internal readonly bool IsKey;
        internal readonly Slot? Slot;
        internal readonly StringBuilder Text = new();
        internal readonly LiveStringNode Live;
        // Escape read so far: empty for none, otherwise a backslash and up to five more characters.
        internal readonly StringBuilder Escape = new();
        // Where a value string sits, when its changes are reported.
        internal readonly ImmutableArray<JsonPathSegment>? Path;
        // The change this string extends, while it is the newest change of the piece.
        internal OpenChange? Change;
    }

    // A change still being extended by the piece being read.
    private sealed class OpenChange
    {
        private OpenChange(bool isSet, ImmutableArray<JsonPathSegment> path, JsonData? value, bool stringValue)
        {
            IsSet = isSet; Path = path; Value = value; StringSet = stringValue;
        }
        internal readonly bool IsSet;
        internal readonly ImmutableArray<JsonPathSegment> Path;
        // A set of a new string extends Text; any other set carries its final Value.
        private readonly JsonData? Value;
        private readonly bool StringSet;
        internal readonly StringBuilder Text = new();

        internal static OpenChange Set(ImmutableArray<JsonPathSegment> path, JsonData value) =>
            new(true, path, value, value.Value.ValueKind == System.Text.Json.JsonValueKind.String && value.Value.GetString()!.Length == 0);
        internal static OpenChange Append(ImmutableArray<JsonPathSegment> path, ReadOnlySpan<char> text)
        {
            var change = new OpenChange(false, path, null, false);
            change.Text.Append(text);
            return change;
        }

        internal JsonChange Freeze()
        {
            if (!IsSet) return new JsonAppendChange(Path, Text.ToString());
            return new JsonSetChange(Path, StringSet ? StringValue(Text.ToString()) : Value!);
        }
    }
}

/// <summary>
/// The arguments of one streaming tool call (streaming-json.ts <c>ToolCallArgumentsStream</c>): the raw text, for the one full parse
/// when the call ends, and a <see cref="StreamingJsonParser"/> for the partial arguments in between. Providers keep one per call in
/// their own stream state, never on the content block, so the raw text never reaches partial messages or transcripts.
/// </summary>
[Experimental("PISHARP_AI001")]
public sealed class ToolCallArgumentsStream
{
    private readonly StringBuilder _text = new();
    private string? _textValue = "";
    private StreamingJsonParser _parser = new();
    // The parser reads the text up to here; the rest waits until partial arguments are asked for.
    private int _parsed;
    private JsonData? _snapshot;
    private JsonData _arguments = JsonData.EmptyObject;

    /// <summary>The raw argument text received so far.</summary>
    public string Text => _textValue ??= _text.ToString();

    /// <summary>The length of <see cref="Text"/>, without building it.</summary>
    public int Length => _text.Length;

    /// <summary>Append a piece of argument text; returns the partial arguments, a new value whenever they changed.</summary>
    public JsonData Append(string delta)
    {
        Write(delta);
        return Partial();
    }

    /// <summary>
    /// Take the provider's complete argument text. When it continues the text so far, returns the missing piece as Delta; otherwise
    /// starts over with the text and Delta is null. Either way returns the partial arguments of the text.
    /// </summary>
    public (string? Delta, JsonData Arguments) Replace(string text)
    {
        var delta = Reset(text);
        return (delta, Partial());
    }

    /// <summary>The final arguments: one full parse of the complete text, which reports malformed arguments.</summary>
    public ToolCallArgumentsResult Finish() => StreamingJson.ParseToolCallArguments(Text);

    /// <summary>The partial arguments of the text so far. Tool arguments are an object; any other partial value shows as no arguments yet.</summary>
    public JsonData Partial()
    {
        if (_parsed < _text.Length)
        {
            _parser.Append(_text.ToString(_parsed, _text.Length - _parsed));
            _parsed = _text.Length;
        }
        var snapshot = _parser.Snapshot();
        if (ReferenceEquals(snapshot, _snapshot)) return _arguments;
        _snapshot = snapshot;
        _arguments = snapshot is { Value.ValueKind: System.Text.Json.JsonValueKind.Object } value ? value : JsonData.EmptyObject;
        return _arguments;
    }

    // Append without parsing, for a stream whose partial arguments are read only on demand: the parser reads the text, once, when
    // they are.
    internal void Write(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.Length == 0) return;
        _text.Append(delta);
        _textValue = null;
    }

    // Replace without taking a snapshot; returns the missing piece, or null when the text started over.
    internal string? Reset(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length >= _text.Length && _text.Equals(text.AsSpan(0, _text.Length)))
        {
            var delta = text[_text.Length..];
            if (delta.Length > 0) Write(delta);
            return delta;
        }
        _text.Clear();
        _textValue = "";
        _parser = new StreamingJsonParser();
        _parsed = 0;
        _snapshot = null;
        _arguments = JsonData.EmptyObject;
        Write(text);
        return null;
    }
}

/// <summary>json-parse.ts <c>parseToolCallArguments</c>: the arguments of a finished tool call, and why they are malformed when they are.</summary>
/// <param name="Arguments">The parsed arguments object; <c>{}</c> when the text was empty or malformed.</param>
/// <param name="ArgumentsError">Set when the complete text was not a valid JSON object: the problem and the text the model sent.</param>
[Experimental("PISHARP_AI001")]
public sealed record ToolCallArgumentsResult(JsonData Arguments, string? ArgumentsError = null)
{
    /// <summary>The ToolCall field that carries <see cref="ArgumentsError"/> (types.ts <c>ToolCall.argumentsError</c>).</summary>
    public const string ErrorField = "argumentsError";

    /// <summary><c>Object.assign(call, result)</c>: the arguments replace the call's, and an error is set after its other fields.</summary>
    public ToolCallContent AssignTo(ToolCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var properties = call.ExtraProperties;
        if (ArgumentsError is { } error)
        {
            var quoted = new StringBuilder(error.Length + 2);
            StreamingJson.Quote(quoted, error);
            properties = (properties ?? JsonFields.Empty).Set(ErrorField, JsonData.Parse(quoted.ToString()));
        }
        return new ToolCallContent(call.Id, call.Name, Arguments, properties);
    }

    /// <summary>The call's <c>argumentsError</c>, when it carries one.</summary>
    public static string? ErrorOf(ToolCallContent call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.ExtraProperties is { } properties && properties.TryGet(ErrorField, out var error) &&
            error!.Value.ValueKind == System.Text.Json.JsonValueKind.String ? JsonUtf16.GetString(error.Value) : null;
    }
}
