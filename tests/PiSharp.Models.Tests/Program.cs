using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Cli.Models;

// Authored offline expectations for IMPL-A2 (model catalogs and model selection) of Pi v1.1.0: the provider catalog shards of
// @earendil-works/pi-ai@1.1.0, env-api-keys.ts, core/model-config.ts, core/provider-composer.ts, core/model-resolver.ts,
// core/resolve-config-value.ts, core/virtual-models.ts, core/remote-catalog-provider.ts, cli/list-models.ts and the CLI live routes.
// Expectations are derived from the pinned upstream source and its tests (test/model-resolver.test.ts, test/model-registry.test.ts,
// test/resolve-config-value.test.ts, test/virtual-models.test.ts, test/remote-catalog-provider.test.ts) by reading, never captured
// from an upstream run. Fake HTTP endpoints answer in process; no network or live credentials are used.
// Optional: --tarball <pi-ai-1.1.0.tgz> also compares every embedded shard byte-for-byte with the package archive.
internal static partial class Program
{
    private const string Upstream = "abe508e1b89912adde45528136c3221eb69acdd7";
    internal static string? Tarball;

    private static async Task<int> Main(string[] args)
    {
        string? report = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--report" && report is null && index + 1 < args.Length) report = Path.GetFullPath(args[++index]);
            else if (args[index] == "--tarball" && Tarball is null && index + 1 < args.Length) Tarball = Path.GetFullPath(args[++index]);
            else throw new ArgumentException("Use [--report <fresh path>] [--tarball <pi-ai-1.1.0.tgz>].");
        }
        var cases = new List<(string Id, Func<Task> Run)>();
        cases.AddRange(CatalogCases());
        cases.AddRange(ResolverCases());
        cases.AddRange(ModelsJsonCases());
        cases.AddRange(ConfigValueCases());
        cases.AddRange(ListingCases());
        cases.AddRange(VirtualCases());
        cases.AddRange(RemoteCases());
        cases.AddRange(LiveRouteCases());
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(120)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var output = new { sourceSha = Upstream, status = "AUTHORED NATIVE; NO UPSTREAM CAPTURE", cases = cases.Count, failures,
            tarballVerified = Tarball is not null, genuineSourceCasesCaptured = 0, liveRoutes = LiveRoutes, results };
        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
        if (report is not null)
        {
            await using var file = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }
    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual, string what) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal), $"{what}: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}].");
    private static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run, string what) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }

    /// <summary>A chat model as the upstream tests write them.</summary>
    internal static RegistryModel M(string provider, string id, string? name = null, string api = "anthropic-messages", bool reasoning = false,
        string[]? input = null, double contextWindow = 128000, double maxTokens = 8192, string baseUrl = "https://example.invalid")
    {
        var json = new JsonObject
        {
            ["id"] = id, ["name"] = name ?? id, ["api"] = api, ["provider"] = provider, ["baseUrl"] = baseUrl, ["reasoning"] = reasoning,
            ["input"] = new JsonArray([.. (input ?? ["text"]).Select(kind => (JsonNode?)kind)]),
            ["cost"] = new JsonObject { ["input"] = 1, ["output"] = 2, ["cacheRead"] = 0.1, ["cacheWrite"] = 1 },
            ["contextWindow"] = contextWindow, ["maxTokens"] = maxTokens
        };
        return RegistryModel.FromJson(json);
    }

    internal static Func<string, string?> Env(params (string Name, string Value)[] values) => name =>
    {
        foreach (var (key, value) in values) if (key == name) return value;
        return null;
    };

    /// <summary>A fresh owned temp directory, deleted after the run.</summary>
    internal static async Task WithTemp(string name, Func<string, Task> run)
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "pisharp-models-tests", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await run(root); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
