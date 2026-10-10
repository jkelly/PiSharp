// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/ai/src/utils/json-parse.ts.
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.AI;

// StreamingJson/parse-streaming-json-goldens.json was produced by tools/StreamingJson/gen-goldens.mjs, which runs the installed
// @earendil-works/pi-ai@1.1.0 parseStreamingJson/repairJson (over partial-json 0.1.7) under Node 22 on a deterministic corpus.
internal static partial class Program
{
    private static Task StreamingJsonGoldens()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "StreamingJson", "parse-streaming-json-goldens.json")));
        int checkedCases = 0, nonObjects = 0, tooDeep = 0, wellFormed = 0;
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var text = item.TryGetProperty("units", out var units) ? Units(units.GetString()!) : item.GetProperty("text").GetString()!;
            var repaired = item.TryGetProperty("repairedUnits", out var repairedUnits) ? Units(repairedUnits.GetString()!) : item.GetProperty("repaired").GetString()!;
            var expected = item.GetProperty("json").GetString()!;
            var label = "parseStreamingJson(" + JsonSerializer.Serialize(text) + ")";
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
        if (checkedCases < 3000 || nonObjects < 400 || tooDeep < 3 || wellFormed < 10)
            throw new InvalidOperationException($"corpus {checkedCases}/{nonObjects}/{tooDeep}/{wellFormed}");
        Equal("{}", StreamingJson.Parse(null).ToString());
        var deepest = new string('[', PiSharp.Contracts.JsonData.MaximumDepth) + new string(']', PiSharp.Contracts.JsonData.MaximumDepth);
        Equal(deepest, StreamingJson.Parse(deepest).ToString());
        try { StreamingJson.Parse("[" + deepest + "]"); throw new InvalidOperationException("1,001 levels did not fail."); } catch (JsonException) { }
        return Task.CompletedTask;

        static string Units(string hex)
        {
            var builder = new StringBuilder(hex.Length / 4);
            for (var index = 0; index < hex.Length; index += 4) builder.Append((char)int.Parse(hex.AsSpan(index, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }
}
