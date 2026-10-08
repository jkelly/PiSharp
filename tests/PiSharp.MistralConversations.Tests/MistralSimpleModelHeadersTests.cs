using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.AI.Providers;
using PiSharp.AI.Protocols.MistralConversations;
using PiSharp.Contracts;

internal static class MistralSimpleModelHeadersTests
{
    internal static async Task Run()
    {
        var model = new ModelDescriptor("header-fixture", "mistral-conversations", "mistral"); var endpoint = new Uri("https://api.mistral.ai/");
        foreach (var mode in new[] { "absent", "null", "supplied" }) foreach (var requestOverride in new[] { false, true })
        {
            var row = new JsonObject { ["id"] = model.Id, ["api"] = model.Api, ["provider"] = model.Provider,
                ["contextWindow"] = 20000, ["maxTokens"] = 1000, ["reasoning"] = false };
            if (mode == "null") row["headers"] = null;
            if (mode == "supplied") row["headers"] = new JsonObject { ["Authorization"] = "Bearer metadata", ["x-affinity"] = "metadata", ["X-Metadata"] = "supplied" };
            using var handler = new Handler(request =>
            {
                var expected = requestOverride ? "request" : mode == "supplied" ? "metadata" : "explicit";
                Check(request.Headers.Authorization?.ToString() == "Bearer " + expected);
                Check(request.Headers.GetValues("x-affinity").Single() == (requestOverride ? "request" : mode == "supplied" ? "metadata" : "session"));
                Check(!request.Headers.Contains("X-Stale") && request.Headers.Contains("X-Metadata") == (mode == "supplied"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") });
            });
            var options = new MistralTextOptions(endpoint, true, new(0, 0, 0, 0), "offline-fixture")
            {
                SessionId = "session",
                ModelHeaders = ImmutableDictionary<string, string?>.Empty.Add("Authorization", "Bearer stale").Add("x-affinity", "stale").Add("X-Stale", "stale"),
                Headers = requestOverride ? ImmutableDictionary<string, string?>.Empty.Add("authorization", "Bearer request").Add("X-Affinity", "request") : null
            };
            using var provider = NativeProviderFactory.CreateMistralSimple(model, endpoint, "explicit", JsonData.Parse(row.ToJsonString()), options, handler);
            StreamEvent? last = null; await foreach (var observation in provider.StreamAsync(new(model, [new("user", JsonData.Parse("{\"role\":\"user\",\"content\":\"fixture\",\"timestamp\":1}"))]))) last = observation;
            Check(last is StreamDone && handler.Calls == 1 && options.ModelHeaders!.ContainsKey("X-Stale"));
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return send(request); }
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Mistral model header provenance assertion failed."); }
}
