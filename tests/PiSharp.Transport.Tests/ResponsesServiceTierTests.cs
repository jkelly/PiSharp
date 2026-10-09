using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

// Authored against pinned Pi policy. UNCOMPILED_UNEXECUTED.
internal static class ResponsesServiceTierTests
{
    private const string Usage = """{"input_tokens":10,"output_tokens":7,"total_tokens":17,"input_tokens_details":{"cached_tokens":3,"cache_write_tokens":2},"output_tokens_details":{"reasoning_tokens":4}}""";
    private static readonly ResponsesTokenRates Rates = new(2, 3, 0.5m, 1);
    private static ChatRequest Request(string model = "gpt-5.5") => new(new(model, "openai-responses", "openai"), [], 1);
    private static string Terminal(string status, string? echo, string? usage = Usage) =>
        "{\"type\":\"response." + (status == "completed" ? "completed" : "incomplete") + "\",\"response\":{\"status\":\"" +
        (status == "completed" ? "completed" : "incomplete") + "\",\"output\":[]" +
        (status == "completed" ? "" : ",\"incomplete_details\":{\"reason\":\"" + (status == "length" ? "max_output_tokens" : "content_filter") + "\"}") +
        (echo is null ? "" : ",\"service_tier\":" + echo) + (usage is null ? "" : ",\"usage\":" + usage) + "}}";
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static async IAsyncEnumerable<JsonData> Source(string value, [EnumeratorCancellation] CancellationToken token)
    { token.ThrowIfCancellationRequested(); yield return JsonData.Parse(value); await Task.CompletedTask; }
    private static Task<ChatResult> Run(string model, string? requested, string status, string? echo, string? usage = Usage, ResponsesTokenRates? rates = null) =>
        new ChatClient(new ResponsesTextToolTransport((_, token) => Source(Terminal(status, echo, usage), token), new(Rates: rates ?? Rates, ServiceTier: requested)))
            .CompleteAsync(Request(model));
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("responses-tier.echo-precedence-model-rates-and-terminal-kinds", Matrix);
        yield return ("responses-tier.request-admission-budget-and-malformed-response", Admission);
        yield return ("responses-tier.fake-http-request-price-and-owned-cleanup", Http);
        yield return ("responses-tier.factory-bound-request-and-pricing", BoundComposition);
        yield return ("responses-tier.split-config-rejected-before-send", RejectSplit);
    }
    private static void Price(ChatResult result, decimal factor)
    {
        var u = result.Message.Usage;
        Check(u.Input == 5 && u.Output == 7 && u.CacheRead == 3 && u.CacheWrite == 2 && u.TotalTokens == 17 &&
            u.ExtraProperties!.Values["reasoning"].Value.GetInt64() == 4, "Tier changed token counts");
        // models.ts:1214-1218 calculateCost and openai-responses.ts:411-418 applyServiceTierPricing run in binary64 Numbers; these are
        // the costs the installed pi-ai 1.1.0 reports for this usage against a local fake server (flex 0.5, priority/fast 2 or 2.5).
        var (input, output, read, write, total) = factor switch
        {
            1m => (0.000009999999999999999m, 0.000021000000000000002m, 0.0000015m, 0.000002m, 0.000034500000000000005m),
            0.5m => (0.0000049999999999999996m, 0.000010500000000000001m, 0.00000075m, 0.000001m, 0.000017250000000000003m),
            2m => (0.000019999999999999998m, 0.000042000000000000004m, 0.000003m, 0.000004m, 0.00006900000000000001m),
            2.5m => (0.000024999999999999998m, 0.0000525m, 0.00000375m, 0.0000049999999999999996m, 0.00008625m),
            _ => throw new InvalidOperationException("Unpinned tier factor")
        };
        Check(u.Cost.Input == input && u.Cost.Output == output && u.Cost.CacheRead == read && u.Cost.CacheWrite == write &&
            u.Cost.Total == total, "Tier cost multiplier or total differs");
    }
    private static async Task Matrix()
    {
        foreach (var model in new[] { "gpt-5.5", "gpt-5.5-mini", "other" })
        foreach (var status in new[] { "completed", "length", "filter" })
        foreach (var (requested, echo, expected) in new (string?, string?, decimal)[]
        {
            (null, null, 1), ("flex", null, 0.5m), ("flex", "null", 0.5m),
            ("priority", null, model == "gpt-5.5" ? 2.5m : 2), ("fast", null, model == "gpt-5.5" ? 2.5m : 2),
            ("fast", "\"default\"", 1), ("flex", "\"priority\"", model == "gpt-5.5" ? 2.5m : 2),
            ("priority", "\"flex\"", 0.5m), ("priority", "\"\"", 1), ("priority", "\"future\"", 1),
            ("auto", null, 1), ("default", null, 1), ("scale", null, 1), ("ultrafast", null, 1)
        })
        {
            var result = await Run(model, requested, status, echo);
            Price(result, expected);
            Check(result.Message.StopReason == (status == "completed" ? StopReason.Stop : status == "length" ? StopReason.Length : StopReason.Error), "Tier changed terminal reason");
        }
        Check((await Run("gpt-5.5", "priority", "completed", null, null)).Message.Usage.Cost.Total == 0, "Missing usage acquired costs");
    }
    private static async Task Admission()
    {
        foreach (var tier in new string?[] { null, "auto", "default", "flex", "scale", "priority", "fast", "ultrafast" })
        {
            var request = Request();
            var factory = new ResponsesKeyAuthRequestFactory(new("https://synthetic.invalid/v1/responses"), request.Model, new(false), new(ServiceTier: tier));
            using var message = factory.Create(request, "inert-key");
            var payload = JsonData.Parse(await message.Content!.ReadAsStringAsync()).Value;
            Check(payload.TryGetProperty("service_tier", out var field) == (tier is not null), "Tier omission differs");
            if (tier is not null) Check(field.GetString() == tier, "Request tier differs");
            var size = (await message.Content.ReadAsByteArrayAsync()).Length;
            using var exact = new ResponsesKeyAuthRequestFactory(new("https://synthetic.invalid/v1/responses"), request.Model, new(false), new(ServiceTier: tier, MaximumPayloadBytes: size)).Create(request, "inert-key");
            try { using var tooSmall = new ResponsesKeyAuthRequestFactory(new("https://synthetic.invalid/v1/responses"), request.Model, new(false), new(ServiceTier: tier, MaximumPayloadBytes: size - 1)).Create(request, "inert-key"); }
            catch (ResponsesKeyAuthRequestException e) when (e.Failure == ResponsesKeyAuthRequestFailure.ResourceLimit) { continue; }
            throw new InvalidOperationException("Tier escaped byte budget");
        }
        foreach (var tier in new[] { "", "PRIORITY", "future" })
        {
            var rejected = false;
            try { _ = new ResponsesKeyAuthRequestFactory(new("https://synthetic.invalid"), Request().Model, new(false), new(ServiceTier: tier)); }
            catch (ResponsesKeyAuthRequestException e) when (e.Failure == ResponsesKeyAuthRequestFailure.UnsupportedOptions) { rejected = true; }
            Check(rejected, "Unknown request tier admitted");
            rejected = false;
            try { _ = new ResponsesTextToolTransport((_, token) => Source(Terminal("completed", null), token), new(ServiceTier: tier)); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Unknown fallback tier admitted");
        }
        foreach (var echo in new[] { "0", "true", "{}", "[]" })
            Check((await Run("gpt-5.5", "flex", "completed", echo)).Failure?.Kind == ChatFailureKind.MalformedStream, "Malformed tier accepted");
        Check((await Run("gpt-5.5", "priority", "completed", null, """{"input_tokens":1000000}""", new(decimal.MaxValue))).Failure?.Kind == ChatFailureKind.MalformedStream, "Cost overflow escaped existing protocol classification");
    }
    private static async Task BoundComposition()
    {
        foreach (var requested in new string?[] { null, "flex", "fast", "priority" })
        foreach (var echo in new string?[] { null, "null", "\"default\"" })
        {
            var sends = 0;
            var options = new ResponsesKeyAuthRequestOptions(ServiceTier: requested);
            var factory = new ResponsesKeyAuthRequestFactory(new("https://synthetic.invalid/v1/responses"), Request().Model, new(false), options);
            using var handler = new ResponsesReasoningTests.Handler(async request =>
            {
                sends++;
                var raw = await request.Content!.ReadAsStringAsync();
                var payload = JsonData.Parse(raw).Value;
                Check(payload.TryGetProperty("service_tier", out var actual) == (requested is not null), "Bound request omission changed");
                if (requested is not null) Check(actual.GetString() == requested, "Bound request tier changed");
                Check(!raw.Contains("PiSharp.Responses", StringComparison.Ordinal) && !request.Headers.ToString().Contains("PiSharp.Responses", StringComparison.Ordinal), "Local binding metadata leaked onto wire");
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("data: " + Terminal("completed", echo) + "\n\n", Encoding.UTF8, "text/event-stream") };
            });
            using var http = new HttpClient(handler);
            var mapperOptions = new ResponsesTextToolOptions(Rates: Rates);
            var transport = new ResponsesHttpSseTransport(http, factory, "inert-key", responsesOptions: mapperOptions);
            Check(options.ServiceTier == requested && mapperOptions.ServiceTier is null, "Binding mutated caller options");
            var factor = echo == "\"default\"" ? 1m : requested == "flex" ? 0.5m : requested is "fast" or "priority" ? 2.5m : 1m;
            for (var i = 0; i < 2; i++) Price(await new ChatClient(transport).CompleteAsync(Request()), factor);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Check((await new ChatClient(transport).CompleteAsync(Request(), canceled.Token)).Failure?.Kind == ChatFailureKind.Cancelled && sends == 2, "Bound pre-cancel acquired HTTP");
        }
    }
    private static async Task RejectSplit()
    {
        foreach (var (requested, fallback) in new (string?, string?)[] { (null, "flex"), ("flex", null), ("flex", "fast") })
        {
            var sends = 0;
            HttpRequestMessage? created = null;
            using var handler = new ResponsesReasoningTests.Handler(_ =>
            { sends++; throw new InvalidOperationException("Split options must not send HTTP"); });
            using var http = new HttpClient(handler);
            var factory = new ResponsesKeyAuthRequestFactory(new("https://synthetic.invalid/v1/responses"), Request().Model, new(false), new(ServiceTier: requested));
            var transport = new ResponsesHttpSseTransport(http, request => { var value = factory.Create(request, "inert-key"); created = value; return value; }, responsesOptions: new(Rates: Rates, ServiceTier: fallback));
            var rejected = false;
            try { await foreach (var _ in transport.StreamAsync(Request())) { } }
            catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.InvalidConfiguration) { rejected = true; }
            Check(rejected && sends == 0 && created is not null, "Legacy delegate split silently admitted");
            var disposed = false;
            try { _ = await created!.Content!.ReadAsByteArrayAsync(); }
            catch (ObjectDisposedException) { disposed = true; }
            Check(disposed, "Rejected factory request content not disposed");
            if (fallback is not null)
            {
                rejected = false;
                try { _ = new ResponsesHttpSseTransport(http, factory, "inert-key", responsesOptions: new(ServiceTier: fallback)); }
                catch (ResponsesKeyAuthRequestException error) when (error.Failure == ResponsesKeyAuthRequestFailure.InvalidConfiguration) { rejected = true; }
                Check(rejected && sends == 0, "Conflicting explicit bound override admitted");
            }
        }
        using var matchingHttp = new HttpClient(new ResponsesReasoningTests.Handler(_ => throw new InvalidOperationException("Construction only")));
        var matching = new ResponsesKeyAuthRequestFactory(new("https://synthetic.invalid"), Request().Model, new(false), new(ServiceTier: "flex"));
        _ = new ResponsesHttpSseTransport(matchingHttp, matching, "inert-key", responsesOptions: new(ServiceTier: "flex"));
        using var cancellation = new CancellationTokenSource();
        var cancelDuringFactory = new ResponsesHttpSseTransport(matchingHttp, request =>
        {
            var message = matching.Create(request, "inert-key");
            cancellation.Cancel();
            return message;
        });
        var cancelled = false;
        try { await foreach (var _ in cancelDuringFactory.StreamAsync(Request(), cancellation.Token)) { } }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Binding validation changed post-factory cancellation precedence");
    }
    private static async Task Http()
    {
        var body = new HttpCleanupGateStream(Encoding.UTF8.GetBytes("data: " + Terminal("length", "\"priority\"") + "\n\n"));
        using var handler = new ResponsesReasoningTests.Handler(async request =>
        {
            Check(JsonData.Parse(await request.Content!.ReadAsStringAsync()).Value.GetProperty("service_tier").GetString() == "flex", "HTTP request tier missing");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamProbeContent(body) };
        });
        using var http = new HttpClient(handler);
        var factory = new ResponsesKeyAuthRequestFactory(new("https://synthetic.invalid/v1/responses"), Request().Model, new(false), new(ServiceTier: "flex"));
        var transport = new ResponsesHttpSseTransport(http, factory, "inert-key", responsesOptions: new(Rates: Rates));
        var task = new ChatClient(transport).CompleteAsync(Request());
        try
        {
            await body.CleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!task.IsCompleted, "Priced terminal escaped owned cleanup");
            body.ReleaseCleanup.TrySetResult();
            var result = await task;
            Price(result, 2.5m);
            Check(result.Message.StopReason == StopReason.Length && body.Disposed && body.AsyncDisposeCalls == 1, "Cleanup/finality changed");
        }
        finally { body.ReleaseCleanup.TrySetResult(); await task; }
    }
}