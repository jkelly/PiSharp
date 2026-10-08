using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI.ModelOperations;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;

// Authored offline expectations for the classifier and image-generation APIs of Pi v1.1.0
// (packages/ai/src/api/{openai-decisions,classifier-shared,system-one-shared,typesafe-system-one,
// cloudflare-workers-ai-system-one,llama-cpp-classify,openrouter-images}.ts, models.ts classify/generateImages and the
// coding-agent model registry). They are derived from the pinned upstream source and its tests (test/openai-decisions,
// typesafe-system-one, cloudflare-workers-ai-system-one, llama-cpp-classify, openrouter-images, classifier-models and
// images-models) by reading, never captured from an upstream run. Fake HTTP handlers answer in process; no network or
// live credentials are used.
internal static partial class Program
{
    private const string Upstream = "abe508e1b89912adde45528136c3221eb69acdd7";
    private static int bodyComparisons;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = new List<(string Id, Func<Task> Run)>();
        cases.AddRange(DecisionsCases()); cases.AddRange(SystemOneCases()); cases.AddRange(CloudflareCases());
        cases.AddRange(LlamaCases()); cases.AddRange(ImagesCases()); cases.AddRange(RegistryCases()); cases.AddRange(JsonCases());
        cases.AddRange(ExtensionCases());
        var results = new List<object>(); var failures = 0;
        foreach (var test in cases)
        {
            try { await test.Run().WaitAsync(TimeSpan.FromSeconds(90)); results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { suite = "classifiers-images-1.1.0", sourceSha = Upstream, status = "AUTHORED NATIVE; NO UPSTREAM CAPTURE",
            cases = cases.Count, failures, bodyComparisons, genuineSourceCasesCaptured = 0, results };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }

    private static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };

    private static void Check(bool value, string reason, [CallerLineNumber] int line = 0)
    { if (!value) throw new InvalidOperationException($"line {line}: {reason}"); }

    private static void Equal<T>(T expected, T actual, string what, [CallerLineNumber] int line = 0)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"line {line}: {what}: expected <{expected}>, actual <{actual}>."); }

    private static void Close(double expected, double actual, string what, double tolerance = 1e-12, [CallerLineNumber] int line = 0)
    { if (!(Math.Abs(expected - actual) <= tolerance)) throw new InvalidOperationException($"line {line}: {what}: expected ~{expected}, actual {actual}."); }

    /// <summary>A full request body pinned byte-for-byte.</summary>
    private static void Body(string expected, string actual, string what, [CallerLineNumber] int line = 0)
    {
        Interlocked.Increment(ref bodyComparisons);
        if (expected != actual) throw new InvalidOperationException($"line {line}: {what} body differs.\nexpected: {expected}\nactual:   {actual}");
    }

    private static void Contains(string expected, string? actual, string what, [CallerLineNumber] int line = 0)
    { if (actual is null || !actual.Contains(expected, StringComparison.Ordinal)) throw new InvalidOperationException($"line {line}: {what}: <{actual}> lacks <{expected}>."); }

    private static void Names(IEnumerable<string> expected, IEnumerable<string> actual, string what, [CallerLineNumber] int line = 0) =>
        Check(expected.SequenceEqual(actual, StringComparer.Ordinal), $"{what}: expected [{string.Join(',', expected)}], actual [{string.Join(',', actual)}]", line);

    private static T Answer<T>(ClassifierResult result, string id) where T : ClassifierAnswer =>
        result.GetAnswer(id) as T ?? throw new InvalidOperationException($"answer {id} is not {typeof(T).Name}: {result.ErrorMessage}");

    private static double Probability(ClassifierChoiceAnswer answer, string label) => answer.Probabilities.Single(pair => pair.Key == label).Value;

    private static JsonData Json(string json) => JsonData.Parse(json);

    private static ClassifierModel Classifier(string json) => (ClassifierModel)OperationModel.FromJson(Json(json));

    private static ImageModel Image(string json) => (ImageModel)OperationModel.FromJson(Json(json));

    /// <summary>Questions in source order, as a JavaScript object literal declares them.</summary>
    private static ImmutableArray<KeyValuePair<string, ClassifierQuestion>> Questions(params (string Id, ClassifierQuestion Question)[] questions) =>
        [.. questions.Select(entry => KeyValuePair.Create(entry.Id, entry.Question))];

    private static ImmutableArray<KeyValuePair<string, string>> Criteria(params (string Label, string Meaning)[] criteria) =>
        [.. criteria.Select(entry => KeyValuePair.Create(entry.Label, entry.Meaning))];

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "PiSharp.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>One recorded request: URL, lower-cased headers (content headers included, content-length excluded) and body.</summary>
internal sealed record Recorded(string Url, ImmutableDictionary<string, string> Headers, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
    public string Path => new Uri(Url).AbsolutePath;
}

/// <summary>A fake HTTP endpoint: records every request and answers through the supplied function.</summary>
internal sealed class FakeHttp(Func<Recorded, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly object gate = new();
    public List<Recorded> Requests { get; } = [];
    public HttpClient Client => new(this, disposeHandler: false);

    public static FakeHttp Always(Func<HttpResponseMessage> response) => new((_, _, _) => Task.FromResult(response()));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .Where(pair => !pair.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase))
            .ToImmutableDictionary(pair => pair.Key.ToLowerInvariant(), pair => string.Join(", ", pair.Value));
        var recorded = new Recorded(request.RequestUri!.AbsoluteUri, headers, body);
        int count;
        lock (gate) { Requests.Add(recorded); count = Requests.Count; }
        token.ThrowIfCancellationRequested();
        return await respond(recorded, count, token);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, params (string Name, string Value)[] headers) =>
        Text(json, status, "application/json", headers);

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/plain",
        params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }
}
