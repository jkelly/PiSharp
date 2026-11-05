// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/tui/test/fuzzy.test.ts.
// Ported expectations of the Pi 1.5.0 fuzzy.test.ts additions (#11220) for the --list-models fuzzy filter (tui fuzzy.ts), read
// from the source; nothing is captured from an upstream run.
using PiSharp.Cli.Models;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        ("sync150.fuzzy.contiguous-occurrence-beats-scattered-alignment", Sync(() =>
        {
            // Greedy alignment took o, p, u from "openrouter", so the real "opus" scored worst.
            var opus = FuzzyFilter.FuzzyMatch("opus", "openrouter openrouter/anthropic/claude-opus-4.1");
            var sonnet = FuzzyFilter.FuzzyMatch("opus", "openrouter openrouter/anthropic/claude-sonnet-4.5");
            var gptOss = FuzzyFilter.FuzzyMatch("opus", "openrouter openrouter/openai/gpt-oss-120b");
            Check(opus.Score < sonnet.Score, $"opus {opus.Score} < sonnet {sonnet.Score}");
            Check(opus.Score < gptOss.Score, $"opus {opus.Score} < gpt-oss {gptOss.Score}");
        })),
        ("sync150.fuzzy.best-contiguous-occurrence", Sync(() =>
            Equal(FuzzyFilter.FuzzyMatch("opus", "xxxxxx opus").Score, FuzzyFilter.FuzzyMatch("opus", "xxopus opus").Score, "uses the best contiguous occurrence"))),
        ("sync150.fuzzy.filter-ranks-contained-query-first", Sync(() =>
        {
            // "radius radius/gpt" contains a, s, t, r, a in order, so every radius model matched there.
            string[] items =
            [
                "radius radius/gpt-5.4 radius gpt-5.4 GPT 5.4",
                "radius radius/agentic radius agentic Agentic",
                "radius radius/gpt-6-sol radius gpt-6-sol GPT 6 Sol",
                "radius radius/gpt-6-astra radius gpt-6-astra GPT 6 Astra",
            ];
            Equal(items[3], FuzzyFilter.Filter(items, "astra", item => item)[0], "first result");
        })),
    ];
}
