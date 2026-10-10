// Partial-reader control flow adapted from partial-json 0.1.7, Copyright (c) 2023 Promplate Dev Team (MIT).
// Repair/attempt ordering adapted from Pi d86654abb8862e201933517d6f1fce9f88dd117f utils/json-parse.ts (MIT).
// See third-party notices and docs/contracts/json-preview.md. No JavaScript dependency is executed.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;

namespace PiSharp.AI;

public sealed record StreamingJsonPreviewOptions(int MaximumCharacters = 65_536, int MaximumDepth = JsonData.MaximumDepth);
public enum StreamingJsonPreviewStage { Empty, Strict, Repaired, Partial, RepairedPartial, Fallback }
public enum StreamingJsonPreviewFailure { CharacterLimit, DepthLimit, UnsupportedNumber, UnsupportedUnicode, DuplicateProperty }
public sealed class StreamingJsonPreviewException(StreamingJsonPreviewFailure failure, string message) : Exception(message)
{
    public StreamingJsonPreviewFailure Failure { get; } = failure;
}
/// <summary>Display data only. RawInput remains distinct from the repaired/partial Value; neither grants execution authority.</summary>
public sealed record StreamingJsonPreviewResult(string? RawInput, JsonData Value, StreamingJsonPreviewStage Stage);

/// <summary>Bounded, stateless preview of one accumulated fragment string; not a final-argument validator.</summary>
public sealed class StreamingJsonPreview
{
    private readonly StreamingJsonPreviewOptions _options;
    public StreamingJsonPreview(StreamingJsonPreviewOptions? options = null)
    {
        _options = options ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_options.MaximumDepth, JsonData.MaximumDepth);
    }

    public StreamingJsonPreviewResult Parse(string? rawInput)
    {
        if (rawInput is null) return new(null, JsonData.EmptyObject, StreamingJsonPreviewStage.Empty);
        if (rawInput.Length > _options.MaximumCharacters) Fail(StreamingJsonPreviewFailure.CharacterLimit);
        CheckRaw(rawInput);
        if (Trim(rawInput).Length == 0) return new(rawInput, JsonData.EmptyObject, StreamingJsonPreviewStage.Empty);
        if (TryStrict(rawInput, out var value)) return new(rawInput, value!, StreamingJsonPreviewStage.Strict);
        var repaired = Repair(rawInput);
        if (repaired != rawInput && TryStrict(repaired, out value)) return new(rawInput, value!, StreamingJsonPreviewStage.Repaired);
        if (TryPartial(rawInput, out value)) return new(rawInput, value!, StreamingJsonPreviewStage.Partial);
        if (repaired != rawInput && TryPartial(repaired, out value)) return new(rawInput, value!, StreamingJsonPreviewStage.RepairedPartial);
        return new(rawInput, JsonData.EmptyObject, StreamingJsonPreviewStage.Fallback);
    }

    private bool TryStrict(string input, out JsonData? value)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = _options.MaximumDepth }); }
        catch (JsonException) { value = null; return false; }
        using (document) { CheckValue(document.RootElement); value = JsonData.FromElement(document.RootElement); return true; }
    }
    private bool TryPartial(string input, out JsonData? value)
    {
        try
        {
            var node = new PartialReader(Trim(input), this).ReadAny(0);
            value = JsonData.Parse(node?.ToJsonString() ?? "{}"); return true;
        }
        catch (SyntaxFailure) { value = null; return false; }
    }
    private void CheckRaw(string input)
    {
        var depth = 0; var inString = false; var escape = false;
        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 == input.Length || !char.IsLowSurrogate(input[index + 1])) Fail(StreamingJsonPreviewFailure.UnsupportedUnicode);
                if (inString && escape) escape = false;
                index++; continue;
            }
            if (char.IsLowSurrogate(character)) Fail(StreamingJsonPreviewFailure.UnsupportedUnicode);
            if (inString)
            {
                if (escape) escape = false;
                else if (character == '\\') escape = true;
                else if (character == '"') inString = false;
            }
            else if (character == '"') inString = true;
            else if (character is '{' or '[') { if (++depth > _options.MaximumDepth) Fail(StreamingJsonPreviewFailure.DepthLimit); }
            else if (character is '}' or ']') depth = Math.Max(0, depth - 1);
        }
    }
    private static void CheckValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    string key;
                    try { key = property.Name; }
                    catch (InvalidOperationException) { Fail(StreamingJsonPreviewFailure.UnsupportedUnicode); throw; }
                    if (!keys.Add(key)) Fail(StreamingJsonPreviewFailure.DuplicateProperty);
                    CheckValue(property.Value);
                }
                break;
            case JsonValueKind.Array: foreach (var element in value.EnumerateArray()) CheckValue(element); break;
            case JsonValueKind.String:
                try { _ = value.GetString(); }
                catch (InvalidOperationException) { Fail(StreamingJsonPreviewFailure.UnsupportedUnicode); }
                break;
            case JsonValueKind.Number:
                var token = value.GetRawText();
                if (!value.TryGetDecimal(out var number) || number is > 9_007_199_254_740_991m or < -9_007_199_254_740_991m ||
                    number == 0 && token.StartsWith('-')) Fail(StreamingJsonPreviewFailure.UnsupportedNumber);
                var mantissa = token.Split('e', 'E')[0];
                var digits = new string(mantissa.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
                if (digits.Length > 15) Fail(StreamingJsonPreviewFailure.UnsupportedNumber);
                break;
        }
    }
    private static string Trim(string input)
    {
        static bool White(char c) => c == '\uFEFF' || c != '\u0085' && char.IsWhiteSpace(c);
        var start = 0; var end = input.Length;
        while (start < end && White(input[start])) start++;
        while (end > start && White(input[end - 1])) end--;
        return input[start..end];
    }
    private static string Repair(string input)
    {
        var output = new StringBuilder(input.Length); var inString = false;
        for (var index = 0; index < input.Length; index++)
        {
            var character = input[index];
            if (!inString) { output.Append(character); if (character == '"') inString = true; continue; }
            if (character == '"') { output.Append(character); inString = false; continue; }
            if (character == '\\')
            {
                if (index + 1 == input.Length) { output.Append("\\\\"); continue; }
                var next = input[index + 1];
                if (next == 'u' && index + 5 < input.Length && input.AsSpan(index + 2, 4).ToArray().All(char.IsAsciiHexDigit))
                { output.Append(input, index, 6); index += 5; continue; }
                if ("\"\\/bfnrtu".Contains(next)) { output.Append(character); output.Append(next); index++; continue; }
                output.Append("\\\\"); continue;
            }
            if (character < 0x20) output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            else output.Append(character);
        }
        return output.ToString();
    }
    private static void Fail(StreamingJsonPreviewFailure failure) => throw new StreamingJsonPreviewException(failure, failure switch
    {
        StreamingJsonPreviewFailure.CharacterLimit => "JSON preview input character limit exceeded.",
        StreamingJsonPreviewFailure.DepthLimit => "JSON preview nesting depth limit exceeded.",
        StreamingJsonPreviewFailure.UnsupportedNumber => "JSON preview number is outside the supported numeric policy.",
        StreamingJsonPreviewFailure.UnsupportedUnicode => "JSON preview contains an unsupported unpaired UTF-16 surrogate.",
        _ => "JSON preview contains a duplicate property name."
    });
    private sealed class SyntaxFailure : Exception;
    private sealed class PartialReader(string text, StreamingJsonPreview owner)
    {
        private int _index;
        public JsonNode? ReadAny(int depth)
        {
            Blank(); if (_index >= text.Length) throw new SyntaxFailure();
            if (text[_index] == '"') return ReadString();
            if (text[_index] is '{' or '[')
            {
                if (depth >= owner._options.MaximumDepth) Fail(StreamingJsonPreviewFailure.DepthLimit);
                return text[_index] == '{' ? ReadObject(depth + 1) : ReadArray(depth + 1);
            }
            foreach (var literal in new[] { "null", "true", "false" })
                if (text.AsSpan(_index).StartsWith(literal, StringComparison.Ordinal) || literal.StartsWith(text[_index..], StringComparison.Ordinal))
                { _index += literal.Length; return literal == "null" ? null : JsonValue.Create(literal == "true"); }
            // Inspect this token, not the entire remaining container. Unsupported values must
            // escape SyntaxFailure recovery even when followed by a delimiter or another member.
            var end = _index;
            while (end < text.Length && !" \t\r\n,]}".Contains(text[end])) end++;
            var token = text.AsSpan(_index, end - _index);
            if (token.Length > 0 && ("Infinity".AsSpan().StartsWith(token, StringComparison.Ordinal) ||
                token.Length > 1 && "-Infinity".AsSpan().StartsWith(token, StringComparison.Ordinal) ||
                "NaN".AsSpan().StartsWith(token, StringComparison.Ordinal)))
                Fail(StreamingJsonPreviewFailure.UnsupportedNumber);
            return ReadNumber();
        }
        private JsonNode ReadString()
        {
            if (_index >= text.Length || text[_index] != '"') throw new SyntaxFailure();
            var start = _index++; var escape = false;
            while (_index < text.Length && (text[_index] != '"' || escape))
            { escape = text[_index] == '\\' ? !escape : false; _index++; }
            var complete = _index < text.Length;
            var token = complete ? text[start..++_index] : text[start..(_index - (escape ? 1 : 0))] + '"';
            if (owner.TryStrict(token, out var value)) return JsonNode.Parse(value!.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!;
            if (!complete)
            {
                var lastSlash = text.LastIndexOf('\\');
                if (lastSlash > start && owner.TryStrict(text[start..lastSlash] + '"', out value)) return JsonNode.Parse(value!.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!;
            }
            throw new SyntaxFailure();
        }
        private JsonObject ReadObject(int depth)
        {
            _index++; var result = new JsonObject(); var keys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                Blank();
                while (_index < text.Length && text[_index] != '}')
                {
                    Blank(); if (_index >= text.Length) return result;
                    var key = ReadString().GetValue<string>(); Blank(); _index++;
                    var value = ReadAny(depth);
                    if (!keys.Add(key)) Fail(StreamingJsonPreviewFailure.DuplicateProperty);
                    result[key] = value; Blank(); if (_index < text.Length && text[_index] == ',') _index++;
                }
            }
            catch (SyntaxFailure) { return result; }
            if (_index < text.Length) _index++; return result;
        }
        private JsonArray ReadArray(int depth)
        {
            _index++; var result = new JsonArray();
            try
            {
                while (_index < text.Length && text[_index] != ']')
                { result.Add(ReadAny(depth)); Blank(); if (_index < text.Length && text[_index] == ',') _index++; }
            }
            catch (SyntaxFailure) { return result; }
            if (_index < text.Length) _index++; return result;
        }
        private JsonNode ReadNumber()
        {
            var start = _index;
            if (_index == 0) _index = text.Length;
            else while (_index < text.Length && !",]}".Contains(text[_index])) _index++;
            var token = text[start.._index];
            if (owner.TryStrict(token, out var value)) return JsonNode.Parse(value!.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!;
            var exponent = text.LastIndexOf('e');
            if (exponent > start && owner.TryStrict(text[start..exponent], out value)) return JsonNode.Parse(value!.ToString(), documentOptions: PiSharp.Contracts.JsonData.DocumentOptions)!;
            throw new SyntaxFailure();
        }
        private void Blank() { while (_index < text.Length && " \t\r\n".Contains(text[_index])) _index++; }
    }
}
