using PiSharp.Cli.Models;

// cli/list-models.ts output, authored from the source: header, two-space columns padded to the widest cell, rows sorted by
// provider then id with localeCompare, formatTokenCount, and the empty/no-match/models.json-warning texts.
internal static partial class Program
{
    private const string ListingModels = """
        {"providers":{
          "demo":{"baseUrl":"https://demo.invalid/v1","apiKey":"demo-key","api":"openai-completions","models":[
            {"id":"zeta-model","reasoning":true,"input":["text","image"],"contextWindow":1048576,"maxTokens":131072},
            {"id":"alpha","contextWindow":200000,"maxTokens":4096},
            {"id":"Beta-2","contextWindow":512,"maxTokens":64000}]},
          "aaa":{"baseUrl":"https://aaa.invalid/v1","apiKey":"aaa-key","api":"openai-completions","models":[
            {"id":"x","contextWindow":2000000,"maxTokens":1500}]}}}
        """;

    private static IEnumerable<(string, Func<Task>)> ListingCases() =>
    [
        ("list-models.table-text", () => WithModelsAsync(ListingModels, ListTable)),
        ("list-models.fuzzy-search-and-no-match", () => WithModelsAsync(ListingModels, ListSearch)),
        ("list-models.no-models-and-models-json-warning", () => WithModelsAsync("""{"providers":{"broken":{}}}""", ListEmpty)),
        ("list-models.token-count-format", Sync(TokenCounts)),
        ("list-models.fuzzy-match-scores", Sync(FuzzyScores)),
        ("list-models.cli-arguments", CliArguments),
    ];

    private static Task WithModelsAsync(string json, Func<string, Task> run) => WithTemp("listing", async root =>
    {
        var path = Path.Combine(root, "models.json");
        await File.WriteAllTextAsync(path, json);
        await run(path);
    });

    private static async Task<(string Output, string Error)> List(string path, string? search)
    {
        Registry(out var registry, modelsPath: path);
        using var output = new StringWriter(); using var error = new StringWriter();
        await ModelListing.ListAsync(registry, search, output, error, "/pi/docs");
        return (output.ToString(), error.ToString());
    }

    private static async Task ListTable(string path)
    {
        var (output, error) = await List(path, null);
        Equal("", error, "no warning");
        Equal("provider  model       context  max-out  thinking  images\n" +
              "aaa       x           2M       1.5K     no        no    \n" +
              "demo      alpha       200K     4.1K     no        no    \n" +
              "demo      Beta-2      512      64K      no        no    \n" +
              "demo      zeta-model  1.0M     131.1K   yes       yes   \n", output, "table");
    }

    private static async Task ListSearch(string path)
    {
        Equal("provider  model       context  max-out  thinking  images\n" +
              "demo      zeta-model  1.0M     131.1K   yes       yes   \n", (await List(path, "zeta")).Output, "fuzzy search");
        Equal("No models matching \"qqq\"\n", (await List(path, "qqq")).Output, "no match");
        Equal(4, (await List(path, "")).Output.Split('\n').Length - 2, "empty search lists everything");
    }

    private static async Task ListEmpty(string path)
    {
        var (output, error) = await List(path, null);
        Equal("No models available. Use /login to log into a provider via OAuth or API key. See:\n  " +
            Path.Combine("/pi/docs", "providers.md") + "\n  " + Path.Combine("/pi/docs", "models.md") + "\n", output, "no models");
        Equal("Warning: errors loading models.json:\nProvider \"broken\": Provider broken: must specify \"baseUrl\", \"headers\", \"compat\", \"modelOverrides\", or \"models\".\n",
            error, "models.json warning");
    }

    private static void TokenCounts()
    {
        // 999999 / 1000 = 999.999 and (999.999).toFixed(1) is "1000.0".
        Names(["0", "999", "1K", "1.5K", "200K", "1000.0K", "1M", "1.0M", "2.5M", "10M"],
            new double[] { 0, 999, 1000, 1500, 200000, 999999, 1000000, 1048576, 2500000, 10000000 }.Select(ModelListing.FormatTokenCount), "formatTokenCount");
    }

    private static void FuzzyScores()
    {
        Check(FuzzyFilter.FuzzyMatch("", "x").Matches && FuzzyFilter.FuzzyMatch("", "x").Score == 0, "empty query");
        Check(!FuzzyFilter.FuzzyMatch("abc", "ab").Matches, "longer query");
        Check(FuzzyFilter.FuzzyMatch("gpt5", "openai gpt-5").Matches, "in order across a gap");
        Check(FuzzyFilter.FuzzyMatch("5gpt", "openai gpt5").Matches, "digits/letters swapped");
        Check(FuzzyFilter.FuzzyMatch("gpt", "gpt").Score < FuzzyFilter.FuzzyMatch("gpt", "a gpt").Score, "exact match bonus");
        Names(["openai gpt-4o", "openrouter openai/gpt-4o:extended"],
            FuzzyFilter.Filter(["openrouter openai/gpt-4o:extended", "openai gpt-4o", "anthropic claude"], "openai/4o", text => text), "slash tokens, best first");
    }

    private static async Task CliArguments()
    {
        await WithTemp("list-cli", async root =>
        {
            var auth = Path.Combine(root, "auth.json"); var models = Path.Combine(root, "models.json");
            await File.WriteAllTextAsync(models, ListingModels);
            var runtime = new PiSharp.Cli.Commands.LiveSessionRuntime(Env(), () => null, auth, ModelsPath: models);
            using var output = new StringWriter(); using var error = new StringWriter();
            Equal(0, await ModelListing.RunAsync(["--list-models", "alpha"], output, error, runtime, CancellationToken.None), "search exit");
            Check(output.ToString().Contains("demo      alpha", StringComparison.Ordinal) && !output.ToString().Contains("zeta", StringComparison.Ordinal), "search output");
            using var bad = new StringWriter();
            Equal(2, await ModelListing.RunAsync(["--list-models", "--other"], new StringWriter(), bad, runtime, CancellationToken.None), "flag after --list-models");
            Check(bad.ToString().Contains("InvalidArguments", StringComparison.Ordinal), "usage failure");
            Check(!File.Exists(auth) && !File.Exists(Path.Combine(root, "models-store.json")), "listing creates no files");
        });
    }
}
