using System.Text;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Resources;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime.Mcp.Resources;

// Synthetic owned callbacks only. Cases remain unregistered/unexecuted until coordinator admission.
internal static class McpResourceTests
{
    internal const string Prefix = "mcp-resources.";
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        (Prefix + "selected-page-filter-and-template-shape", SelectedPage),
        (Prefix + "aggregate-pages-errors-and-call-time-capture", Aggregate),
        (Prefix + "read-text-image-binary-and-structured-originals", Read),
        (Prefix + "held-request-cancellation-joins-original", HeldRequest),
        (Prefix + "held-output-cancellation-and-reentry-joins-original", HeldSave),
        (Prefix + "cursor-cycle-and-page-response-item-bounds", Bounds),
        (Prefix + "utf8-middle-truncation-full-output-and-image-retention", Truncation),
        (Prefix + "generation-domains-and-native-invocation-identity", Identity),
        (Prefix + "multicast-request-and-saver-refuse-before-effects", Multicast),
        (Prefix + "held-selected-request-retains-complete-original-faults", RequestFaults),
        (Prefix + "aggregate-request-fault-evidence-survives-result-projection", AggregateFaults),
        (Prefix + "held-binary-and-truncation-saver-faults-retained", SaverFaults),
        (Prefix + "faulted-request-oce-remains-faulted", RequestFaultedCancellation),
        (Prefix + "faulted-saver-oce-remains-fallback-with-evidence", SaverFaultedCancellation),
        (Prefix + "owned-original-cancellation-and-mismatched-canceled-original", CancellationProvenance),
        (Prefix + "owner-cancel-does-not-supersede-held-request-or-saver-faults", CancellationWithFaults)
    ];
    private sealed class Context : IExtensionToolInvocationContext
    {
        public string OwnerId => "resource-owner";
        public long OwnerGeneration => 3;
        public long SessionGeneration => 11;
        public string ToolCallId => "resource-call";
        public string? ParentToolCallId => "parent";
        public CancellationToken OperationCancellationToken { get; init; }
        public CancellationToken SessionCancellationToken => default;
        public CancellationToken ExtensionLifetimeCancellationToken => default;
        public ValueTask ReportUpdateAsync(JsonData update, CancellationToken token) => ValueTask.CompletedTask;
    }
    private static JsonData Json(string value) => JsonData.Parse(value);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Resource control failed"); }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ValueTask<string> Saver(ReadOnlyMemory<byte> data, string extension, McpInvocationIdentity identity, CancellationToken token) => ValueTask.FromResult("synthetic/output" + extension);
    private static McpResourceServer Server(string name, Func<string, JsonData?, Task<JsonData>> request) =>
        new(name, 7, 2000, (generation, method, parameters, options, token) => new(request(method, parameters)));
    private static async Task SelectedPage()
    {
        var calls = 0;
        var server = Server("a", (method, args) =>
        {
            calls++; Check(method == "resources/templates/list"); Check(args!.Value.GetProperty("cursor").GetString() == "next");
            return Task.FromResult(Json("""{"resourceTemplates":[{"uriTemplate":"data://{x}","name":"data","icons":[{}],"_meta":{},"description":"yes"},{"uriTemplate":"ui://app","name":"ui"},{"uriTemplate":"data://app","name":"app","mimeType":"text/html; profile=\"mcp-app\""}],"nextCursor":"last"}"""));
        });
        var result = await new McpResourceTools(() => [server], Saver).ExecuteAsync(McpResourceTools.ListTemplates, Json("""{"server":" a ","cursor":"next"}"""), new Context());
        var items = result.StructuredContent.Value.GetProperty("resourceTemplates");
        Check(calls == 1 && items.GetArrayLength() == 1 && !items[0].TryGetProperty("icons", out _) && !items[0].TryGetProperty("_meta", out _));
        Check(result.StructuredContent.Value.GetProperty("nextCursor").GetString() == "last");
    }
    private static async Task Aggregate()
    {
        var captured = 0; var pages = 0;
        var a = Server("a", (_, args) => { pages++; return Task.FromResult(args is null ?
            Json("""{"resources":[{"uri":"data://a","name":"a"}],"nextCursor":"two"}""") : Json("""{"resources":[{"uri":"data://b","name":"b"}]}""")); });
        var b = Server("b", (_, _) => Task.FromException<JsonData>(new IOException("synthetic fault")));
        var tools = new McpResourceTools(() => { captured++; return new[] { b, a }; }, Saver);
        var result = await tools.ExecuteAsync(McpResourceTools.ListResources, Json("{}"), new Context());
        Check(captured == 1 && pages == 2 && result.StructuredContent.Value.GetProperty("resources").GetArrayLength() == 2);
        Check(result.StructuredContent.Value.GetProperty("errors")[0].GetProperty("server").GetString() == "b");
    }
    private static async Task Read()
    {
        var writes = 0;
        var server = Server("a", (method, args) => { Check(method == "resources/read" && args!.Value.GetProperty("uri").GetString() == "data://dir"); return Task.FromResult(Json("""{"contents":[{"uri":"data://text","text":"hi","_meta":{"ignored":true}},{"uri":"data://json","mimeType":"application/problem+json","blob":"e30="},{"uri":"data://image","mimeType":"image/png","blob":"AQI="},{"uri":"data://file.bin","blob":"AQI="}]}""")); });
        var tools = new McpResourceTools(() => [server], (data, ext, id, token) => { writes++; Check(data.Span.SequenceEqual(new byte[] { 1, 2 }) && ext == ".bin"); return ValueTask.FromResult("synthetic/bin"); });
        var result = await tools.ExecuteAsync(McpResourceTools.ReadResource, Json("""{"server":"a","uri":"data://dir"}"""), new Context());
        Check(writes == 1 && result.Content.Length == 8 && result.Content[3].Value.GetProperty("text").GetString() == "{}");
        Check(result.Content[5].Value.GetProperty("type").GetString() == "image");
        Check(!result.StructuredContent.Value.GetProperty("contents")[0].TryGetProperty("_meta", out _));
        Check(result.ToToolResult().Value.TryGetProperty("structuredContent", out _));
    }
    private static async Task HeldRequest()
    {
        using var cancel = new CancellationTokenSource(); var entered = Gate(); var release = Gate();
        var server = Server("a", async (_, _) => { entered.SetResult(); await release.Task; return Json("""{"resources":[]}"""); });
        var original = new McpResourceTools(() => [server], Saver).ExecuteAsync(McpResourceTools.ListResources, Json("""{"server":"a"}"""), new Context { OperationCancellationToken = cancel.Token });
        await entered.Task; cancel.Cancel(); Check(!original.IsCompleted); release.SetResult();
        await Throws<OperationCanceledException>(original);
    }
    private static async Task HeldSave()
    {
        using var cancel = new CancellationTokenSource(); var entered = Gate(); var release = Gate();
        var server = Server("a", (_, _) => Task.FromResult(Json("""{"contents":[{"uri":"data://file","blob":"AQI="}]}""")));
        McpResourceTools? tools = null;
        tools = new(() => [server], async (data, ext, id, token) =>
        {
            await Throws<InvalidOperationException>(tools!.ExecuteAsync(McpResourceTools.ListResources, Json("{}"), new Context()));
            entered.SetResult(); await release.Task; return "synthetic/file";
        });
        var original = tools.ExecuteAsync(McpResourceTools.ReadResource, Json("""{"server":"a","uri":"data://file"}"""), new Context { OperationCancellationToken = cancel.Token });
        await entered.Task; cancel.Cancel(); Check(!original.IsCompleted); release.SetResult(); await Throws<OperationCanceledException>(original);
    }
    private static async Task Bounds()
    {
        var cycle = Server("a", (_, _) => Task.FromResult(Json("""{"resources":[],"nextCursor":"same"}""")));
        var result = await new McpResourceTools(() => [cycle], Saver).ExecuteAsync(McpResourceTools.ListResources, Json("{}"), new Context());
        Check(result.StructuredContent.Value.GetProperty("errors")[0].GetProperty("error").GetString()!.Contains("duplicate cursor"));
        await Throws<ArgumentException>(new McpResourceTools(() => [cycle], Saver).ExecuteAsync(McpResourceTools.ListResources, Json("""{"cursor":"x"}"""), new Context()));
        var large = Server("a", (_, _) => Task.FromResult(Json("""{"resources":[{"uri":"a","name":"a"},{"uri":"b","name":"b"}]}""")));
        await Throws<McpRuntimeProtocolException>(new McpResourceTools(() => [large], Saver, new(MaximumItems: 1)).ExecuteAsync(McpResourceTools.ListResources, Json("""{"server":"a"}"""), new Context()));
        await Throws<McpRuntimeProtocolException>(new McpResourceTools(() => [large], Saver, new(MaximumResponseBytes: 2)).ExecuteAsync(McpResourceTools.ListResources, Json("""{"server":"a"}"""), new Context()));
        result = await new McpResourceTools(() => [cycle], Saver, new(MaximumPages: 1)).ExecuteAsync(McpResourceTools.ListResources, Json("{}"), new Context());
        Check(result.StructuredContent.Value.GetProperty("errors")[0].GetProperty("error").GetString()!.Contains("page limit"));
    }
    private static async Task Truncation()
    {
        var server = Server("a", (_, _) => Task.FromResult(Json("""{"contents":[{"uri":"a","text":"😀😀😀😀😀😀😀😀"},{"uri":"b","mimeType":"image/png","blob":"AQI="}]}""")));
        var saved = "";
        var result = await new McpResourceTools(() => [server], (data, ext, id, token) => { saved = Encoding.UTF8.GetString(data.Span); return ValueTask.FromResult("synthetic/full.txt"); }, new(MaximumModelTextBytes: 8))
            .ExecuteAsync(McpResourceTools.ReadResource, Json("""{"server":"a","uri":"a"}"""), new Context());
        Check(saved.Contains("😀😀😀😀😀😀😀😀") && result.FullOutputPath == "synthetic/full.txt" && result.Content.Length == 2);
        var text = result.Content[0].Value.GetProperty("text").GetString()!;
        // truncate.ts truncateMiddle: `\u2026N chars truncated\u2026`.
        Check(text.Contains("chars truncated\u2026") && !text.Contains('\ufffd') && result.Content[1].Value.GetProperty("type").GetString() == "image");
        Check(result.StructuredContent.Value.GetProperty("contents")[0].GetProperty("text").GetString() == "😀😀😀😀😀😀😀😀");
    }
    private static async Task Identity()
    {
        var calls = 0;
        var server = new McpResourceServer("a", 7, 2000, (generation, method, args, options, token) =>
        {
            calls++; Check(generation == 7 && options.InvocationIdentity is { OwnerId: "resource-owner", OwnerGeneration: 3, SessionGeneration: 11, ToolCallId: "resource-call", ParentToolCallId: "parent" });
            return ValueTask.FromResult(Json("""{"resources":[]}"""));
        });
        await new McpResourceTools(() => [server], Saver).ExecuteAsync(McpResourceTools.ListResources, Json("{}"), new Context()); Check(calls == 1);
        var stale = server with { Request = (_, _, _, _, _) => ValueTask.FromException<JsonData>(new InvalidOperationException("stale captured server generation")) };
        await Throws<McpResourceCallbackException>(new McpResourceTools(() => [stale], Saver).ExecuteAsync(McpResourceTools.ListResources, Json("""{"server":"a"}"""), new Context()));
    }
    private static async Task Multicast()
    {
        var effects = 0; var held = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously);
        McpResourceRequest first = (_, _, _, _, _) => { effects++; return new(held.Task); };
        McpResourceRequest last = (_, _, _, _, _) => { effects++; throw new IOException("later synchronous fault"); };
        var server = new McpResourceServer("a", 7, 2000, first + last);
        await Throws<InvalidOperationException>(new McpResourceTools(() => [server], Saver).ExecuteAsync(McpResourceTools.ListResources, Json("{}"), new Context()));
        Check(effects == 0 && !held.Task.IsCompleted);
        var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        McpResourceOutputSaver saverFirst = (_, _, _, _) => { effects++; return new(saved.Task); };
        McpResourceOutputSaver saverLast = (_, _, _, _) => { effects++; return ValueTask.FromResult("later"); };
        try { _ = new McpResourceTools(() => [], saverFirst + saverLast); throw new InvalidOperationException("Multicast saver admitted"); }
        catch (ArgumentException) { }
        Check(effects == 0 && !saved.Task.IsCompleted); held.SetResult(Json("{}")); saved.SetResult("unused");
    }
    private static McpResourceServer HeldServer(TaskCompletionSource<JsonData> original, TaskCompletionSource entered) =>
        new("a", 7, 2000, (_, _, _, _, _) => { entered.SetResult(); return new(original.Task); });
    private static void Retained(McpResourceCallbackException failure, Task original, params Exception[] faults)
    {
        Check(ReferenceEquals(failure.Original, original));
        // Task.Exception returns a fresh aggregate view; verify exact nested fault identities.
        var flattened = ((AggregateException)failure.InnerException!).Flatten().InnerExceptions;
        Check(flattened.Count == faults.Length && faults.All(fault => flattened.Any(found => ReferenceEquals(found, fault))));
    }
    private static async Task RequestFaults()
    {
        var original = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = Gate();
        var operation = new McpResourceTools(() => [HeldServer(original, entered)], Saver).ExecuteAsync(McpResourceTools.ListResources, Json("""{"server":"a"}"""), new Context());
        await entered.Task; Check(!operation.IsCompleted);
        var first = new IOException("request first"); var second = new IOException("request cleanup"); original.SetException(new[] { first, second });
        try { await operation; throw new InvalidOperationException("Expected request fault"); }
        catch (McpResourceCallbackException failure) { Retained(failure, original.Task, first, second); Check(operation.IsFaulted); }
    }
    private static async Task AggregateFaults()
    {
        var original = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = Gate();
        var operation = new McpResourceTools(() => [HeldServer(original, entered)], Saver).ExecuteAsync(McpResourceTools.ListResources, Json("{}"), new Context());
        await entered.Task; Check(!operation.IsCompleted);
        var first = new IOException("list fault"); var second = new IOException("list cleanup"); original.SetException(new[] { first, second });
        var result = await operation; Check(result.OriginalCallbackFailures.Length == 1);
        Retained(result.OriginalCallbackFailures[0], original.Task, first, second);
        Check(result.StructuredContent.Value.GetProperty("errors")[0].GetProperty("error").GetString()!.Contains("list cleanup"));
        Check(!result.ToToolResult().ToString().Contains("OriginalCallbackFailures"));
    }
    private static async Task SaverFaults()
    {
        foreach (var truncation in new[] { false, true })
        {
            var original = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = Gate();
            var server = Server("a", (_, _) => Task.FromResult(truncation ? Json("""{"contents":[{"uri":"a","text":"long text exceeding four bytes"}]}""") : Json("""{"contents":[{"uri":"a","blob":"AQI="}]}""")));
            var operation = new McpResourceTools(() => [server], (_, _, _, _) => { entered.SetResult(); return new(original.Task); }, new(MaximumModelTextBytes: truncation ? 4 : 20480))
                .ExecuteAsync(McpResourceTools.ReadResource, Json("""{"server":"a","uri":"a"}"""), new Context());
            await entered.Task; Check(!operation.IsCompleted);
            var first = new IOException("save first"); var second = new IOException("save cleanup"); original.SetException(new[] { first, second });
            var result = await operation; Check(result.OriginalCallbackFailures.Length == 1); Retained(result.OriginalCallbackFailures[0], original.Task, first, second);
            Check(result.Content[0].Value.GetProperty("text").GetString()!.Contains(truncation ? "Could not save the full output" : "could not be saved"));
        }
    }
    private static async Task RequestFaultedCancellation()
    {
        var original = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = Gate();
        var operation = new McpResourceTools(() => [HeldServer(original, entered)], Saver).ExecuteAsync(McpResourceTools.ListResources, Json("""{"server":"a"}"""), new Context());
        await entered.Task; var oce = new OperationCanceledException("faulted callback OCE"); var cleanup = new IOException("request OCE cleanup"); original.SetException(new Exception[] { oce, cleanup });
        try { await operation; throw new InvalidOperationException("Faulted OCE became success"); }
        catch (McpResourceCallbackException failure) { Retained(failure, original.Task, oce, cleanup); Check(original.Task.IsFaulted && operation.IsFaulted && !operation.IsCanceled); }
    }
    private static async Task SaverFaultedCancellation()
    {
        var original = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = Gate();
        var server = Server("a", (_, _) => Task.FromResult(Json("""{"contents":[{"uri":"a","blob":"AQI="}]}""")));
        var operation = new McpResourceTools(() => [server], (_, _, _, _) => { entered.SetResult(); return new(original.Task); })
            .ExecuteAsync(McpResourceTools.ReadResource, Json("""{"server":"a","uri":"a"}"""), new Context());
        await entered.Task; var oce = new OperationCanceledException("faulted saver OCE"); var cleanup = new IOException("saver OCE cleanup"); original.SetException(new Exception[] { oce, cleanup });
        var result = await operation; Check(operation.IsCompletedSuccessfully && original.Task.IsFaulted);
        Retained(result.OriginalCallbackFailures.Single(), original.Task, oce, cleanup); Check(result.Content[0].ToString().Contains("could not be saved"));
    }
    private static async Task CancellationProvenance()
    {
        foreach (var saveCancellation in new[] { false, true })
        foreach (var matching in new[] { true, false })
        {
            using var cancel = new CancellationTokenSource(); using var unrelated = new CancellationTokenSource();
            var original = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously); var entered = Gate(); CancellationToken captured = default;
            var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = saveCancellation ? Server("a", (_, _) => Task.FromResult(Json("""{"contents":[{"uri":"a","blob":"AQI="}]}"""))) :
                new McpResourceServer("a", 7, 2000, (_, _, _, _, token) => { captured = token; entered.SetResult(); return new(original.Task); });
            McpResourceOutputSaver saver = (_, _, _, token) => { captured = token; entered.SetResult(); return new(saved.Task); };
            var operation = new McpResourceTools(() => [server], saveCancellation ? saver : Saver).ExecuteAsync(
                saveCancellation ? McpResourceTools.ReadResource : McpResourceTools.ListResources,
                saveCancellation ? Json("""{"server":"a","uri":"a"}""") : Json("""{"server":"a"}"""), new Context { OperationCancellationToken = cancel.Token });
            await entered.Task; cancel.Cancel(); Check(!operation.IsCompleted);
            if (saveCancellation) saved.SetCanceled(matching ? captured : unrelated.Token); else original.SetCanceled(matching ? captured : unrelated.Token);
            if (matching) { await Throws<OperationCanceledException>(operation); Check(operation.IsCanceled); }
            else if (saveCancellation)
            {
                try { await operation; throw new InvalidOperationException("Mismatched saver cancellation suppressed"); }
                catch (AggregateException failure) { Check(operation.IsFaulted && failure.InnerExceptions.OfType<McpResourceCallbackException>().Single().Original == saved.Task); }
            }
            else { await Throws<McpResourceCallbackException>(operation); Check(operation.IsFaulted); }
        }
    }
    private static async Task CancellationWithFaults()
    {
        foreach (var saveFault in new[] { false, true })
        {
            using var cancel = new CancellationTokenSource(); var entered = Gate();
            var request = new TaskCompletionSource<JsonData>(TaskCreationOptions.RunContinuationsAsynchronously);
            var saved = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var server = saveFault ? Server("a", (_, _) => Task.FromResult(Json("""{"contents":[{"uri":"a","blob":"AQI="}]}"""))) : HeldServer(request, entered);
            var tools = new McpResourceTools(() => [server], (_, _, _, _) => { entered.SetResult(); return new(saved.Task); });
            var operation = tools.ExecuteAsync(saveFault ? McpResourceTools.ReadResource : McpResourceTools.ListResources,
                saveFault ? Json("""{"server":"a","uri":"a"}""") : Json("{}"), new Context { OperationCancellationToken = cancel.Token });
            await entered.Task; cancel.Cancel(); Check(!operation.IsCompleted);
            var first = new IOException("cancel race fault"); var second = new IOException("cancel race cleanup");
            if (saveFault) saved.SetException(new[] { first, second }); else request.SetException(new[] { first, second });
            try { await operation; throw new InvalidOperationException("Callback faults lost after cancellation"); }
            catch (AggregateException failure)
            {
                Check(operation.IsFaulted); var retained = failure.InnerExceptions.OfType<McpResourceCallbackException>().Single();
                Retained(retained, saveFault ? saved.Task : request.Task, first, second);
                Check(failure.InnerExceptions.Any(fault => fault is OperationCanceledException));
            }
        }
    }
    private static async Task Throws<T>(Task original) where T : Exception
    { try { await original; } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
