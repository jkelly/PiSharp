// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/ai/src/utils/json-parse.ts.
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.AI;

// StreamingJson/parse-streaming-json-goldens.json was produced by tools/StreamingJson/gen-goldens.mjs, which runs the installed
// @earendil-works/pi-ai@1.1.0 parseStreamingJson/repairJson (over partial-json 0.1.7) under Node 22 on a deterministic corpus.
// Pi f9a26365 json-parse.ts removed "u" from VALID_JSON_ESCAPES, so repairJson now doubles the backslash of a \u inside a string
// that is not followed by four hex digits. The corpus cases holding one no longer describe 1.5.0: they are skipped (and counted),
// and the authored sync150 cases cover that repair.
internal static partial class Program
{
    private static Task StreamingJsonGoldens()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "StreamingJson", "parse-streaming-json-goldens.json")));
        int checkedCases = 0, nonObjects = 0, tooDeep = 0, wellFormed = 0, invalidUnicodeEscapes = 0;
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var text = item.TryGetProperty("units", out var units) ? Units(units.GetString()!) : item.GetProperty("text").GetString()!;
            var repaired = item.TryGetProperty("repairedUnits", out var repairedUnits) ? Units(repairedUnits.GetString()!) : item.GetProperty("repaired").GetString()!;
            var expected = item.GetProperty("json").GetString()!;
            var label = "parseStreamingJson(" + JsonSerializer.Serialize(text) + ")";
            if (InvalidUnicodeEscape(text)) { invalidUnicodeEscapes++; continue; }
            if (StreamingJson.RepairJson(text) != repaired) throw new InvalidOperationException("repairJson differs for " + label);
            // The corpus's deep values (formerly beyond the 64 levels a JsonData held) are now kept, as upstream keeps them.
            if (item.GetProperty("depth").GetInt32() > 64) tooDeep++;
            if (item.GetProperty("depth").GetInt32() > PiSharp.Contracts.JsonData.MaximumDepth)
            {
                // Upstream keeps the value; a JsonData holds JsonData.MaximumDepth levels, so the native boundary reports it instead of truncating it.
                try { StreamingJson.Parse(text); throw new InvalidOperationException(label + " is not representable but did not fail."); }
                catch (JsonException) { }
                continue;
            }
            var actual = StreamingJson.ParseToJson(text);
            if (actual != expected) throw new InvalidOperationException($"{label}{Environment.NewLine}Expected {expected}{Environment.NewLine}Actual   {actual}");
            // The owned value keeps every name and string value exactly (a lone surrogate as its escape, see JsonUtf16); the cases with a
            // "native" field hold lone surrogates (formerly a lone surrogate of a name became U+FFFD there).
            if (item.TryGetProperty("native", out _)) wellFormed++;
            Equal(expected, StreamingJson.Parse(text).ToString());
            if (expected[0] != '{') nonObjects++;
            checkedCases++;
        }
        if (checkedCases < 3000 || nonObjects < 400 || tooDeep < 3 || wellFormed < 10 || invalidUnicodeEscapes == 0)
            throw new InvalidOperationException($"corpus {checkedCases}/{nonObjects}/{tooDeep}/{wellFormed}/{invalidUnicodeEscapes}");
        Equal("{}", StreamingJson.Parse(null).ToString());
        var deepest = new string('[', PiSharp.Contracts.JsonData.MaximumDepth) + new string(']', PiSharp.Contracts.JsonData.MaximumDepth);
        Equal(deepest, StreamingJson.Parse(deepest).ToString());
        try { StreamingJson.Parse("[" + deepest + "]"); throw new InvalidOperationException("1,001 levels did not fail."); } catch (JsonException) { }
        return Task.CompletedTask;

        // Scans strings the way repairJson does for a \u that four hex digits do not follow.
        static bool InvalidUnicodeEscape(string text)
        {
            var inString = false;
            for (var index = 0; index < text.Length; index++)
            {
                var c = text[index];
                if (!inString) { inString = c == '"'; continue; }
                if (c == '"') { inString = false; continue; }
                if (c != '\\' || index + 1 >= text.Length) continue;
                var next = text[index + 1];
                if (next == 'u')
                {
                    if (index + 6 <= text.Length && text.AsSpan(index + 2, 4).ToString().All(char.IsAsciiHexDigit)) { index += 5; continue; }
                    return true;
                }
                if (next is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't') index++;
            }
            return false;
        }

        static string Units(string hex)
        {
            var builder = new StringBuilder(hex.Length / 4);
            for (var index = 0; index < hex.Length; index += 4) builder.Append((char)int.Parse(hex.AsSpan(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }
}
