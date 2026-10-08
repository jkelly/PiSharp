using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Rpc.Protocol;

internal static class OriginalCompactionWireTests
{
    internal const string Prefix = "rpc.original-compaction-wire.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "all-three-pinned-start-reasons", Starts),
        (Prefix + "success-result-fields-order-and-actual-values", Success),
        (Prefix + "failure-and-abort-undefined-fields-omitted", Failure),
        (Prefix + "optional-details-preserves-null-and-nested-json", Optional),
        (Prefix + "native-status-payload-and-inconsistent-input-refused", Invalid),
        (Prefix + "complete-frame-output-budget", Budget)
    ];

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Exact(JsonData value, string expected) => Check(value.ToString() == expected, "Pinned compaction wire differs: " + value);
    private static void Refused<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " for invalid projection input.");
    }
    private static Task Starts()
    {
        Exact(OriginalCompactionEventProjector.Start(SessionCompactionReason.Manual), "{\"type\":\"compaction_start\",\"reason\":\"manual\"}");
        Exact(OriginalCompactionEventProjector.Start(SessionCompactionReason.Threshold), "{\"type\":\"compaction_start\",\"reason\":\"threshold\"}");
        Exact(OriginalCompactionEventProjector.Start(SessionCompactionReason.Overflow), "{\"type\":\"compaction_start\",\"reason\":\"overflow\"}");
        Refused<ArgumentOutOfRangeException>(() => OriginalCompactionEventProjector.Start((SessionCompactionReason)99));
        return Task.CompletedTask;
    }
    private static Task Success()
    {
        // Independently fixed pinned property order, not serialized with the implementation's field list.
        var result = OriginalCompactionEventProjector.Result("actual summary", "kept-id", 45001, 22505,
            JsonData.Parse("{\"totalTokens\":7}"), JsonData.Parse("{\"readFiles\":[\"a\"],\"modifiedFiles\":[\"b\"]}"));
        const string expected = "{\"summary\":\"actual summary\",\"firstKeptEntryId\":\"kept-id\",\"tokensBefore\":45001,\"estimatedTokensAfter\":22505,\"usage\":{\"totalTokens\":7},\"details\":{\"readFiles\":[\"a\"],\"modifiedFiles\":[\"b\"]}}";
        Exact(result, expected);
        foreach (var (reason, text, retry) in new[] { (SessionCompactionReason.Manual, "manual", false),
            (SessionCompactionReason.Threshold, "threshold", false), (SessionCompactionReason.Overflow, "overflow", true) })
            Exact(OriginalCompactionEventProjector.End(reason, result, false, retry),
                "{\"type\":\"compaction_end\",\"reason\":\"" + text + "\",\"result\":" + expected + ",\"aborted\":false,\"willRetry\":" + (retry ? "true" : "false") + "}");
        // Input order is deliberately different; the original result is always projected in pinned order.
        var reordered = JsonData.Parse("{\"estimatedTokensAfter\":11,\"tokensBefore\":37,\"firstKeptEntryId\":\"k\",\"summary\":\"s\"}");
        Exact(OriginalCompactionEventProjector.End(SessionCompactionReason.Overflow, reordered, false, false),
            "{\"type\":\"compaction_end\",\"reason\":\"overflow\",\"result\":{\"summary\":\"s\",\"firstKeptEntryId\":\"k\",\"tokensBefore\":37,\"estimatedTokensAfter\":11},\"aborted\":false,\"willRetry\":false}");
        return Task.CompletedTask;
    }
    private static Task Failure()
    {
        foreach (var (reason, text, message) in new[] { (SessionCompactionReason.Manual, "manual", "Compaction failed: actual error"),
            (SessionCompactionReason.Threshold, "threshold", "Auto-compaction failed: actual error"),
            (SessionCompactionReason.Overflow, "overflow", "Context overflow recovery failed: actual error") })
        {
            Exact(OriginalCompactionEventProjector.End(reason, null, false, false, message),
                "{\"type\":\"compaction_end\",\"reason\":\"" + text + "\",\"aborted\":false,\"willRetry\":false,\"errorMessage\":\"" + message + "\"}");
            Exact(OriginalCompactionEventProjector.End(reason, null, true, false),
                "{\"type\":\"compaction_end\",\"reason\":\"" + text + "\",\"aborted\":true,\"willRetry\":false}");
        }
        return Task.CompletedTask;
    }
    private static Task Optional()
    {
        Exact(OriginalCompactionEventProjector.Result("s", "k", 2.5, 1),
            "{\"summary\":\"s\",\"firstKeptEntryId\":\"k\",\"tokensBefore\":2.5,\"estimatedTokensAfter\":1}");
        var details = JsonData.Parse("{\"extension\":{\"items\":[null,1,true]},\"empty\":[]}");
        var result = OriginalCompactionEventProjector.Result("s", "k", 9, 3, details: details);
        Check(result.Value.GetProperty("details").GetRawText() == details.ToString() && !result.Value.TryGetProperty("usage", out _), "Optional unknown details or absent usage changed.");
        var explicitNull = OriginalCompactionEventProjector.Result("s", "k", 9, 3, details: JsonData.Null);
        Check(OriginalCompactionEventProjector.End(SessionCompactionReason.Manual, explicitNull, false, false)
            .Value.GetProperty("result").GetProperty("details").ValueKind == JsonValueKind.Null, "Explicit details null was omitted.");
        return Task.CompletedTask;
    }
    private static Task Invalid()
    {
        foreach (var payload in new[] { "null", "{\"checkpointAcknowledged\":true,\"skipped\":false,\"entryId\":\"native\"}",
            "{\"status\":\"committed\",\"checkpointId\":\"native\"}",
            "{\"summary\":\"s\",\"firstKeptEntryId\":\"k\",\"tokensBefore\":7}",
            "{\"summary\":\"s\",\"firstKeptEntryId\":\"k\",\"tokensBefore\":7,\"estimatedTokensAfter\":2,\"generation\":1}",
            "{\"summary\":\"s\",\"firstKeptEntryId\":\"k\",\"tokensBefore\":7,\"estimatedTokensAfter\":-2}" })
            Refused<ArgumentException>(() => OriginalCompactionEventProjector.End(SessionCompactionReason.Overflow, JsonData.Parse(payload), false, false));
        var result = OriginalCompactionEventProjector.Result("s", "k", 9, 3);
        Refused<ArgumentException>(() => OriginalCompactionEventProjector.End(SessionCompactionReason.Threshold, result, false, true));
        Refused<ArgumentException>(() => OriginalCompactionEventProjector.End(SessionCompactionReason.Manual, result, true, false));
        Refused<ArgumentException>(() => OriginalCompactionEventProjector.End(SessionCompactionReason.Overflow, null, false, true));
        Refused<ArgumentException>(() => OriginalCompactionEventProjector.End(SessionCompactionReason.Overflow, null, true, false, "invented error"));
        Refused<ArgumentException>(() => OriginalCompactionEventProjector.End(SessionCompactionReason.Manual, result, false, false, "invented error"));
        foreach (var number in new[] { double.NaN, double.PositiveInfinity, -1d })
            Refused<ArgumentOutOfRangeException>(() => OriginalCompactionEventProjector.Result("s", "k", number, 3));
        Refused<ArgumentException>(() => OriginalCompactionEventProjector.Result("s", "k", 9, 3, usage: JsonData.Null));
        return Task.CompletedTask;
    }
    private static Task Budget()
    {
        var limits = new RpcDispatchOptions(MaximumOutputBytes: 256);
        var result = OriginalCompactionEventProjector.Result(new string('x', 120), "k", 9, 3, options: limits);
        Check(System.Text.Encoding.UTF8.GetByteCount(result.ToString()) < 256, "Fixture result must fit before complete event overhead.");
        Refused<RpcDispatchException>(() => OriginalCompactionEventProjector.End(SessionCompactionReason.Overflow, result, false, true, options: limits));
        Refused<RpcDispatchException>(() => OriginalCompactionEventProjector.End(SessionCompactionReason.Threshold, null, false, false, new string('x', 256), limits));
        Refused<RpcDispatchException>(() => OriginalCompactionEventProjector.Result(new string('x', 256), "k", 9, 3, options: limits));
        Refused<ArgumentOutOfRangeException>(() => OriginalCompactionEventProjector.Start(SessionCompactionReason.Manual, limits with { MaximumOutputBytes = 0 }));
        return Task.CompletedTask;
    }
}
