using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Contracts;

// Pinned Source mapStopReason/finalizeResponse expectations. AUTHORED_UNCOMPILED_UNEXECUTED.
internal static class ResponsesIncompleteTests
{
    private static readonly ModelDescriptor Model = new("incomplete-model", "openai-responses", "openai");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private const string TextStart = """{"type":"response.output_item.added","output_index":0,"item":{"type":"message","id":"msg_1","content":[]}}""";
    private const string TextDelta = """{"type":"response.output_text.delta","output_index":0,"item_id":"msg_1","delta":"partial"}""";
    private const string TextEnd = """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","id":"msg_1","content":[{"type":"output_text","text":"partial"}]}}""";
    private const string ToolStart = """{"type":"response.output_item.added","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"inspect","arguments":""}}""";
    private const string ToolEnd = """{"type":"response.output_item.done","output_index":1,"item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"inspect","arguments":"{}"}}""";
    private const string Usage = """{"input_tokens":10,"output_tokens":7,"total_tokens":17,"input_tokens_details":{"cached_tokens":3,"cache_write_tokens":2},"output_tokens_details":{"reasoning_tokens":4}}""";
    private static string Terminal(string? details = "{\"reason\":\"max_output_tokens\"}", string output = "[]") =>
        "{\"type\":\"response.incomplete\",\"response\":{\"id\":\"resp_incomplete\",\"status\":\"incomplete\",\"output\":" + output +
        ",\"usage\":" + Usage + (details is null ? "" : ",\"incomplete_details\":" + details) + "}}";
    private static byte[] Wire(params string[] values) => Encoding.UTF8.GetBytes(string.Concat(values.Select(v => "data: " + v + "\n\n")));
    private static ChatRequest Request() => new(Model, [new("user", JsonData.Parse("""{"role":"user","content":"hello","timestamp":1}"""))], 1);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Field(ChatResult result, string name) => result.Message.ExtraProperties!.Values[name].Value.GetString()!;
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("responses-incomplete.reason-error-usage-and-cost", TerminalPolicy);
        yield return ("responses-incomplete.tool-finality-and-reasoning-backfill", Finality);
        yield return ("responses-incomplete.malformed-and-trailing-data", Malformed);
        yield return ("responses-incomplete.cleanup-cancellation-and-late-faults", Cleanup);
    }
    private static void UsageIsRetained(ChatResult result)
    {
        var usage = result.Message.Usage;
        Check(usage.Input == 5 && usage.Output == 7 && usage.CacheRead == 3 && usage.CacheWrite == 2 && usage.TotalTokens == 17, "Usage components lost");
        Check(usage.ExtraProperties!.Values["reasoning"].Value.GetInt64() == 4, "Reasoning usage lost");
        // models.ts:1214-1217 calculateCost in binary64 Numbers (installed pi-ai 1.1.0 prices this incomplete usage identically).
        Check(usage.Cost.Input == 0.000009999999999999999m && usage.Cost.Output == 0.000021000000000000002m && usage.Cost.CacheRead == 0.0000015m && usage.Cost.CacheWrite == 0.000002m,
            "Incomplete pricing differs from completed pricing");
        Check(Field(result, "responseId") == "resp_incomplete", "Response identity lost");
    }
    private static async Task TerminalPolicy()
    {
        foreach (var details in new string?[] { null, "null", "{}", "{\"reason\":null}", "{\"reason\":\"\"}", "{\"reason\":\"content_filter\"}", "{\"reason\":\"max_output_tokens\"}" })
        {
            using var fixture = new Fixture(Wire(TextStart, TextDelta, TextEnd, Terminal(details)));
            var result = await fixture.Complete();
            var length = details?.Contains("max_output_tokens", StringComparison.Ordinal) == true;
            var filtered = details?.Contains("content_filter", StringComparison.Ordinal) == true;
            Check(result.Message.StopReason == (length ? StopReason.Length : StopReason.Error), "Wrong incomplete reason");
            Check(Field(result, "rawStopReason") == (length ? "incomplete.max_output_tokens" : filtered ? "incomplete.content_filter" : "incomplete"), "Raw stop reason lost");
            Check(((TextContent)result.Message.Content.Single()).Text == "partial", "Partial content lost");
            UsageIsRetained(result);
            if (length) Check(result.Failure is null && !result.Message.ExtraProperties!.Values.ContainsKey("errorMessage"), "Length acquired error state");
            else
            {
                var expected = filtered ? "Response incomplete: content_filter" : "Response incomplete without a provider reason";
                Check(result.Failure?.Kind == ChatFailureKind.Provider && result.Failure.Message == expected && Field(result, "errorMessage") == expected, "Provider error channels disagree");
            }
        }
    }
    private static async Task Finality()
    {
        foreach (var ended in new[] { false, true })
        foreach (var filtered in new[] { false, true })
        {
            var terminal = Terminal(filtered ? "{\"reason\":\"content_filter\"}" : "{\"reason\":\"max_output_tokens\"}");
            var events = ended ? new[] { ToolStart, ToolEnd, terminal } : new[] { ToolStart, terminal };
            using var fixture = new Fixture(Wire(events));
            var result = await fixture.Complete();
            Check(result.Message.StopReason == (ended && !filtered ? StopReason.Length : StopReason.Error), "Incomplete tool gained successful execution reason");
            Check(result.Message.StopReason != StopReason.ToolUse, "Incomplete tool authority");
            if (!ended && !filtered) Check(Field(result, "errorMessage") == "Response incomplete with unfinished content.", "Unfinished length boundary");
            UsageIsRetained(result);
        }
        using (var unfinishedText = new Fixture(Wire(TextStart, TextDelta, Terminal())))
        {
            var result = await unfinishedText.Complete();
            Check(result.Message.StopReason == StopReason.Error && ((TextContent)result.Message.Content.Single()).Text == "partial", "Fabricated text end");
            UsageIsRetained(result);
        }
        using (var completeButUnfinished = new Fixture(Wire(ToolStart, """{"type":"response.completed","response":{"status":"completed"}}""")))
            Check((await completeButUnfinished.Complete()).Failure?.Kind == ChatFailureKind.MalformedStream, "Completed strict finality weakened");
        const string reasoning = """{"type":"response.output_item.done","output_index":2,"item":{"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"summary"}]}}""";
        const string output = """[{"type":"reasoning","id":"rs_1","encrypted_content":"late-cipher"}]""";
        foreach (var filtered in new[] { false, true })
        {
            using var fixture = new Fixture(Wire(reasoning, Terminal(filtered ? "{\"reason\":\"content_filter\"}" : "{\"reason\":\"max_output_tokens\"}", output)));
            var result = await fixture.Complete();
            var block = (ThinkingContent)result.Message.Content.Single();
            var signature = JsonData.Parse(block.ExtraProperties!.Values["thinkingSignature"].Value.GetString()!);
            Check(block.Thinking == "summary" && signature.Value.GetProperty("encrypted_content").GetString() == "late-cipher", "Incomplete backfill lost");
            Check(result.Message.StopReason == (filtered ? StopReason.Error : StopReason.Length), "Reasoning terminal reason");
            UsageIsRetained(result);
        }
    }
    private static async Task Malformed()
    {
        foreach (var invalid in new[]
        {
            Terminal("[]"), Terminal("false"), Terminal("{\"reason\":123}"), Terminal("{\"reason\":\"private-unrecognized\"}"),
            Terminal().Replace("\"status\":\"incomplete\"", "\"status\":\"completed\""),
            Terminal().Replace("\"total_tokens\":17", "\"total_tokens\":-1")
        })
        {
            using var fixture = new Fixture(Wire(TextStart, TextDelta, TextEnd, invalid));
            var result = await fixture.Complete();
            Check(result.Failure?.Kind == ChatFailureKind.MalformedStream && result.Message.StopReason == StopReason.Error, "Malformed incomplete accepted");
            Check(!result.Failure!.Message.Contains("private-unrecognized", StringComparison.Ordinal), "Unknown reason disclosed in diagnostic");
        }
        foreach (var tail in new[] { Terminal(), TextDelta, "{broken" })
        {
            using var fixture = new Fixture(Wire(TextStart, TextDelta, TextEnd, Terminal(), tail));
            Check((await fixture.Complete()).Failure?.Kind == ChatFailureKind.MalformedStream, "Trailing data ignored after incomplete");
        }
        using var eof = new Fixture(Wire(TextStart, TextDelta, TextEnd));
        Check((await eof.Complete()).Failure?.Kind == ChatFailureKind.MalformedStream, "EOF inferred incomplete success");
    }
    private static async Task Cleanup()
    {
        foreach (var filtered in new[] { false, true })
        foreach (var cancel in new[] { false, true })
        {
            var bytes = Wire(TextStart, TextDelta, TextEnd, Terminal(filtered ? "{\"reason\":\"content_filter\"}" : "{\"reason\":\"max_output_tokens\"}"));
            var body = new HttpCleanupGateStream(bytes);
            using var fixture = new Fixture(body);
            using var cancellation = new CancellationTokenSource();
            var task = fixture.Complete(cancellation.Token);
            try
            {
                await body.CleanupEntered.Task.WaitAsync(Deadline);
                if (cancel) cancellation.Cancel();
                Check(!task.IsCompleted && !fixture.Handler.Disposed, "Terminal escaped cleanup");
                body.ReleaseCleanup.TrySetResult();
                var result = await task;
                Check(result.Message.StopReason == (cancel ? StopReason.Aborted : filtered ? StopReason.Error : StopReason.Length), "Cleanup/cancel precedence");
                if (!cancel) UsageIsRetained(result);
                Check(body.AsyncDisposeCalls == 1 && body.Disposed && !fixture.Handler.Disposed, "Original cleanup not joined");
            }
            finally { body.ReleaseCleanup.TrySetResult(); await task; }
        }
        foreach (var failDispose in new[] { false, true })
        {
            using var fixture = new Fixture(new FaultStream(Wire(TextStart, TextDelta, TextEnd, Terminal()), failDispose));
            var result = await fixture.Complete();
            Check(result.Failure is not null && result.Message.StopReason == StopReason.Error, "Late read/release fault became successful length");
        }
    }
    private sealed class FaultStream(byte[] bytes, bool failDispose) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!failDispose && Position == Length) throw new IOException("inert late read failure");
            return base.ReadAsync(buffer, token);
        }
        public override ValueTask DisposeAsync()
        {
            if (failDispose) throw new IOException("inert cleanup failure");
            return base.DisposeAsync();
        }
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly ResponsesReasoningTests.Handler Handler;
        private readonly HttpClient _client;
        private readonly ResponsesHttpSseTransport _transport;
        internal Fixture(byte[] bytes) : this(new MemoryStream(bytes)) { }
        internal Fixture(Stream body)
        {
            Handler = new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamProbeContent(body) }));
            _client = new(Handler);
            var factory = new ResponsesKeyAuthRequestFactory(new Uri("https://synthetic.invalid/v1/responses"), Model, new(true));
            _transport = new(_client, request => factory.Create(request, "inert-incomplete-key"),
                responsesOptions: new(Rates: new(Input: 2, Output: 3, CacheRead: 0.5m, CacheWrite: 1)));
        }
        internal Task<ChatResult> Complete(CancellationToken token = default) => new ChatClient(_transport, capacity: 1).CompleteAsync(Request(), token);
        public void Dispose() => _client.Dispose();
    }
}
