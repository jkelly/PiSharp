// highlight.js 10.7.3 (BSD-3-Clause): differential test against goldens produced by the real lib/core.js + lib/index.js — ported for Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/syntax-highlight.ts.
using System.Text.Json;

namespace PiSharp.Cli.Interactive.Mode;

/// <summary>
/// Replays hljs-corpus.json on a fresh <see cref="HljsEngine"/> exactly like tools/HljsGrammars/generate-goldens.mjs
/// replays it on highlight.js (eager phase, then LoadAllLanguages, same order) and compares byte-for-byte with
/// hljs-goldens.json: highlight values (or the "Unknown language" error), renderHighlightedHtml output, supportsLanguage
/// answers and standalone render cases. <see cref="CheckGrammars"/> compiles every grammar regex/matcher in .NET.
/// </summary>
internal static class HljsDifferentialTests
{
    public static IReadOnlyList<(string Id, bool Passed, string Detail)> Run(string directory)
    {
        var results = new List<(string, bool, string)>();
        using var corpus = JsonDocument.Parse(Gunzip(directory, "hljs-corpus.json.gz"));
        using var golden = JsonDocument.Parse(Gunzip(directory, "hljs-goldens.json.gz"));
        var expected = golden.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        var theme = golden.RootElement.GetProperty("themeScopes").EnumerateArray().Select(e => e.GetString()!)
            .ToDictionary(s => s, s => (Func<string, string>)(t => $"«{s}|{t}»"));
        var supportNames = corpus.RootElement.GetProperty("supportsLanguage").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var supports = golden.RootElement.GetProperty("supports").EnumerateArray()
            .ToDictionary(e => (e.GetProperty("phase").GetString()!, e.GetProperty("name").GetString()!), e => e.GetProperty("result").GetBoolean());

        var engine = HljsEngine.CreateWithEagerLanguages();
        var loaded = false;
        void CheckSupports(string phase)
        {
            foreach (var name in supportNames)
            {
                var actual = engine.GetLanguage(name) != null;
                var want = supports[(phase, name)];
                results.Add(($"supports/{phase}/{name}", actual == want, actual == want ? "" : $"expected {want}, got {actual}"));
            }
        }

        foreach (var c in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString()!;
            if (c.GetProperty("phase").GetString() == "all" && !loaded)
            {
                CheckSupports("eager");
                engine.LoadAllLanguages();
                loaded = true;
            }
            var want = expected[id];
            string? value = null, error = null;
            var auto = c.TryGetProperty("auto", out var a) && a.GetBoolean();
            try
            {
                if (auto)
                {
                    var subset = c.GetProperty("languageSubset") is { ValueKind: JsonValueKind.Array } s ? s.EnumerateArray().Select(e => e.GetString()!).ToArray() : null;
                    var r = engine.HighlightAuto(c.GetProperty("code").GetString()!, subset);
                    value = r.Value;
                    var wantLanguage = want.GetProperty("language").ValueKind == JsonValueKind.Null ? null : want.GetProperty("language").GetString();
                    var wantRelevance = want.GetProperty("relevance").GetDouble();
                    if (r.Language != wantLanguage || r.Relevance != wantRelevance)
                    {
                        results.Add((id, false, $"auto: expected {wantLanguage}/{wantRelevance}, got {r.Language}/{r.Relevance}"));
                        continue;
                    }
                }
                else value = engine.Highlight(c.GetProperty("code").GetString()!, c.GetProperty("language").GetString()!, c.GetProperty("ignoreIllegals").GetBoolean()).Value;
            }
            catch (ArgumentException ex) { error = ex.Message; }
            catch (Exception ex) { error = "UNEXPECTED " + ex; }
            if (want.TryGetProperty("error", out var wantError))
            {
                var ok = error != null && wantError.GetString()!.StartsWith("Unknown language", StringComparison.Ordinal) && error.StartsWith("Unknown language", StringComparison.Ordinal);
                results.Add((id, ok, ok ? "" : $"expected error '{wantError.GetString()}', got {(error ?? "value")}"));
                continue;
            }
            if (value == null) { results.Add((id, false, "threw " + error)); continue; }
            var wantValue = want.GetProperty("value").GetString()!;
            if (value != wantValue) { results.Add((id, false, "value " + Diff(wantValue, value))); continue; }
            var rendered = HighlightRenderer.Render(value, theme);
            var wantRendered = want.GetProperty("rendered").GetString()!;
            results.Add((id, rendered == wantRendered, rendered == wantRendered ? "" : "rendered " + Diff(wantRendered, rendered)));
        }
        if (!loaded) engine.LoadAllLanguages();
        CheckSupports("all");

        var i = 0;
        foreach (var r in golden.RootElement.GetProperty("renderCases").EnumerateArray())
        {
            var actual = HighlightRenderer.Render(r.GetProperty("html").GetString()!, theme);
            var want = new string([.. r.GetProperty("rendered").EnumerateArray().Select(e => (char)e.GetInt32())]);
            results.Add(($"render/{i++}", actual == want, actual == want ? "" : Diff(want, actual)));
        }
        return results;
    }

    /// <summary>Thread-safety: all non-auto "all"-phase cases highlighted concurrently (4 passes) on one fresh engine.</summary>
    public static IReadOnlyList<(string Id, bool Passed, string Detail)> RunParallel(string directory)
    {
        using var corpus = JsonDocument.Parse(Gunzip(directory, "hljs-corpus.json.gz"));
        using var golden = JsonDocument.Parse(Gunzip(directory, "hljs-goldens.json.gz"));
        var expected = golden.RootElement.GetProperty("cases").EnumerateArray()
            .Where(e => e.TryGetProperty("value", out _)).ToDictionary(e => e.GetProperty("id").GetString()!, e => e.GetProperty("value").GetString()!);
        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray()
            .Where(c => c.GetProperty("phase").GetString() == "all" && !c.TryGetProperty("auto", out _) && expected.ContainsKey(c.GetProperty("id").GetString()!))
            .Select(c => (Id: c.GetProperty("id").GetString()!, Code: c.GetProperty("code").GetString()!, Language: c.GetProperty("language").GetString()!, Ignore: c.GetProperty("ignoreIllegals").GetBoolean()))
            .ToArray();
        var engine = HljsEngine.CreateWithEagerLanguages();
        engine.LoadAllLanguages();
        var work = Enumerable.Range(0, 4).SelectMany(pass => pass % 2 == 0 ? cases : cases.Reverse()).ToArray();
        var results = new (string, bool, string)[work.Length];
        Parallel.For(0, work.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) }, i =>
        {
            var (id, code, language, ignore) = work[i];
            var value = engine.Highlight(code, language, ignore).Value;
            results[i] = ($"parallel/{id}", value == expected[id], value == expected[id] ? "" : Diff(expected[id], value));
        });
        return results;
    }

    /// <summary>Compiles every registered language and builds every resumable matcher at every start index.</summary>
    public static IReadOnlyList<(string Id, bool Passed, string Detail)> CheckGrammars()
    {
        var results = new List<(string, bool, string)>();
        var engine = HljsEngine.CreateWithEagerLanguages();
        engine.LoadAllLanguages();
        foreach (var name in engine.ListLanguages())
        {
            try
            {
                var root = engine.GetLanguage(name)!.Compile();
                var seen = new HashSet<HljsCompiledMode>(ReferenceEqualityComparer.Instance);
                var stack = new Stack<HljsCompiledMode>([root]);
                var regexes = 0;
                while (stack.Count > 0)
                {
                    var m = stack.Pop();
                    if (!seen.Add(m)) continue;
                    if (m.Starts != null) stack.Push(m.Starts);
                    _ = m.KeywordPattern?.Search;
                    _ = m.EndRe?.Anchored;
                    if (m.Matcher == null) continue;
                    for (var k = 0; k <= m.Matcher.Rules.Count; k++) { m.Matcher.GetMatcher(k); regexes++; }
                    foreach (var r in m.Matcher.Rules) if (r.Mode != null) stack.Push(r.Mode);
                }
                results.Add(($"grammar/{name}", true, $"{seen.Count} modes, {regexes} matchers"));
            }
            catch (Exception ex)
            {
                results.Add(($"grammar/{name}", false, ex.GetType().Name + ": " + ex.Message));
            }
        }
        return results;
    }

    /// <summary>The corpus and goldens ship gzip-compressed.</summary>
    static byte[] Gunzip(string directory, string name)
    {
        using var input = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(directory, name)), System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray();
    }

    static string Diff(string expected, string actual)
    {
        var i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i]) i++;
        static string Clip(string s, int at) => Quote(s.Substring(Math.Max(0, at - 60), Math.Min(s.Length, at + 60) - Math.Max(0, at - 60)));
        return $"differs at {i} (expected length {expected.Length}, actual {actual.Length}); expected ...{Clip(expected, i)}... actual ...{Clip(actual, i)}...";
    }

    static string Quote(string s)
    {
        var sb = new System.Text.StringBuilder("\"");
        foreach (var ch in s)
            sb.Append(ch switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", < ' ' => $"\\u{(int)ch:X4}", _ => ch.ToString() });
        return sb.Append('"').ToString();
    }
}
