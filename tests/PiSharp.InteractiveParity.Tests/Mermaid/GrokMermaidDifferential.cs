// grok-mermaid 0.2.3 (Apache-2.0): differential test against goldens from the original — used by Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/modes/interactive/components/mermaid.ts.
using System.Text.Json;

namespace PiSharp.Cli.Interactive.Mode.Mermaid.Differential;

/// <summary>
/// Compares the C# port with goldens produced by the original library under Node (<c>tools/gen-goldens.mjs</c>).
/// Reads <c>mermaid-corpus.json</c> and <c>mermaid-goldens.json</c> from <paramref name="directory"/> and yields one
/// result per corpus case. Every string is compared ordinally, code unit for code unit.
/// </summary>
internal static class GrokMermaidDifferential
{
    public static IEnumerable<(string Id, bool Passed, string Detail)> Run(string directory)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "mermaid-corpus.json")));
        using var goldens = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "mermaid-goldens.json")));
        var cases = goldens.RootElement.GetProperty("cases");
        var results = new List<(string, bool, string)>();
        foreach (var item in corpus.RootElement.EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var src = item.GetProperty("src").GetString()!;
            string? failure;
            try
            {
                failure = cases.TryGetProperty(id, out var golden) ? Check(src, golden) : "no golden for this case";
            }
            catch (Exception ex)
            {
                failure = $"threw {ex.GetType().Name}: {ex.Message}";
            }
            results.Add((id, failure is null, failure ?? "ok"));
        }
        return results;
    }

    private static string? Check(string src, JsonElement golden)
    {
        var kind = GrokMermaid.DiagramKind(src);
        var expectedKind = golden.GetProperty("kind") is { ValueKind: JsonValueKind.String } k ? k.GetString() : null;
        if (kind != expectedKind) return $"diagramKind: expected {Show(expectedKind)}, got {Show(kind)}";

        var art = GrokMermaid.Render(src);
        if (CompareArt("render", golden.GetProperty("render"), art) is { } renderDiff) return renderDiff;

        if (golden.TryGetProperty("ansi", out var ansi))
        {
            if (art is null != (ansi.ValueKind == JsonValueKind.Null)) return "ansi: null mismatch";
            if (art is not null && CompareStrings("ansi", ansi, GrokMermaid.ToAnsi(art)) is { } ansiDiff) return ansiDiff;
        }
        if (golden.TryGetProperty("box", out var box) && CompareArt("sourceBox", box, GrokMermaid.SourceBox(src)) is { } boxDiff) return boxDiff;
        if (golden.TryGetProperty("box30", out var box30) && CompareArt("sourceBox(30)", box30, GrokMermaid.SourceBox(src, 30)) is { } box30Diff) return box30Diff;
        return null;
    }

    private static string? CompareArt(string what, JsonElement expected, MermaidArt? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null) return actual is null ? null : $"{what}: expected null, got art of width {actual.Width}";
        if (actual is null) return $"{what}: expected art, got null";
        var width = expected.GetProperty("width").GetInt32();
        if (width != actual.Width) return $"{what}.width: expected {width}, got {actual.Width}";
        if (CompareStrings($"{what}.plain", expected.GetProperty("plain"), actual.Plain) is { } plainDiff) return plainDiff;
        if (CompareStrings($"{what}.warnings", expected.GetProperty("warnings"), actual.Warnings) is { } warnDiff) return warnDiff;
        var rows = expected.GetProperty("styled");
        if (rows.GetArrayLength() != actual.Styled.Count) return $"{what}.styled: expected {rows.GetArrayLength()} rows, got {actual.Styled.Count}";
        var y = 0;
        foreach (var row in rows.EnumerateArray())
        {
            var spans = actual.Styled[y];
            if (row.GetArrayLength() != spans.Count) return $"{what}.styled[{y}]: expected {row.GetArrayLength()} spans, got {spans.Count}";
            var x = 0;
            foreach (var span in row.EnumerateArray())
            {
                // Each span is written as [cls, text].
                var cls = span[0].GetString();
                var text = span[1].GetString();
                if (!string.Equals(text, spans[x].Text, StringComparison.Ordinal) || !string.Equals(cls, spans[x].Cls, StringComparison.Ordinal))
                    return $"{what}.styled[{y}][{x}]: expected {Show(text)}/{cls}, got {Show(spans[x].Text)}/{spans[x].Cls}";
                x++;
            }
            y++;
        }
        return null;
    }

    private static string? CompareStrings(string what, JsonElement expected, IReadOnlyList<string> actual)
    {
        if (expected.GetArrayLength() != actual.Count) return $"{what}: expected {expected.GetArrayLength()} entries, got {actual.Count}";
        var i = 0;
        foreach (var e in expected.EnumerateArray())
        {
            var s = e.GetString();
            if (!string.Equals(s, actual[i], StringComparison.Ordinal)) return $"{what}[{i}]: expected {Show(s)}, got {Show(actual[i])}";
            i++;
        }
        return null;
    }

    private static string Show(string? s) => s is null ? "null" : JsonSerializer.Serialize(s);
}
