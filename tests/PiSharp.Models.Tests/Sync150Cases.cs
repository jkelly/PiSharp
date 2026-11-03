using System.Diagnostics;
using PiSharp.Cli.Models;

// Pi 1.5.0 (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5): the minimatch 11.0.3 upgrade (b4c2e92ac:
// brace-expansion 5 limits and nonrecursive-glob-match) as model scoping's Minimatch ports its protection, from the brace-expansion
// 5.0.12 and minimatch 11.0.3 sources. Expectations are written from the sources by reading, never captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        ("sync150.model-scope-amplified-globs-answer-at-once", Sync(ModelScopeGlobAmplification)),
    ];

    // Many stars, many ** and amplified brace sets are bounded: a segment is matched without backtracking, ** without a recursive walk
    // per split, and brace expansion stops at 100,000 results (later expansions are not matched) and 1,000 nesting levels.
    private static void ModelScopeGlobAmplification()
    {
        var watch = Stopwatch.StartNew();
        var stars = string.Concat(Enumerable.Repeat("*a", 30));
        Check(!Minimatch.IsMatch(new string('a', 300), stars + "*b", true), "many stars, no final literal");
        Check(Minimatch.IsMatch(new string('a', 300) + "B", stars + "*b", true), "many stars and the final literal, ignoring case");
        var path = string.Join('/', Enumerable.Repeat("a", 40));
        var globstars = string.Concat(Enumerable.Repeat("**/a/", 20));
        Check(!Minimatch.IsMatch(path, globstars + "b", false), "many globstars, no final segment");
        Check(Minimatch.IsMatch(path + "/b", globstars + "b", false), "many globstars and the final segment");
        Check(!Minimatch.IsMatch(path + "/.x/b", globstars + "b", false), "a ** never swallows a dot segment");
        var braces = string.Concat(Enumerable.Repeat("{a,b}", 20));
        Check(Minimatch.IsMatch(new string('a', 20), braces, false), "the first of 2^20 expansions");
        Check(!Minimatch.IsMatch(new string('b', 20), braces, false), "an expansion past the 100,000 cap");
        Check(!Minimatch.IsMatch("x", string.Concat(Enumerable.Repeat("{a,", 5000)) + "b" + new string('}', 5000), false), "deep nesting");
        Check(watch.Elapsed < TimeSpan.FromSeconds(20), "pathological patterns answer at once: " + watch.Elapsed);
        // Ordinary patterns are unchanged.
        Check(Minimatch.IsMatch("openai/gpt-4o", "openai/gpt-{4o,5}", true) && Minimatch.IsMatch("gpt-5", "{gpt,o}-[0-9]*", false) &&
            !Minimatch.IsMatch("anthropic/claude", "*claude*", true) && Minimatch.IsMatch("x/y", "x/[!z]", false), "ordinary patterns");
    }
}
