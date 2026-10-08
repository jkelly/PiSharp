using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime.Dispatch;

internal static class EventReducerTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("complete source catalog retains all 41 pinned event subscriptions and mandatory semantics", SourceCatalogCoverage),
        ("input transforms compose, preserve nullish images and stop when handled", InputComposition),
        ("input no-result, invalid proposal and handler failure retain prior state", InputDiagnostics),
        ("input cancellation owns separate tokens and joins admitted callback work", InputCancellation),
        ("tool argument replacements compose and the first block stops dispatch", ToolCallComposition),
        ("tool-call failure blocks execution while requested cancellation remains cancellation", ToolCallFailureAndCancellation),
        ("tool-result patches compose and content redaction clears stale structured content", ToolResultComposition),
        ("tool-result absence/null/raw numbers/opaque fields and outcome error remain distinct", ToolResultRawPresence),
        ("tool-result invalid or failed hook continues; cancellation does not produce partial success", ToolResultDiagnosticsAndCancellation),
        ("all three captured dispatches survive removal and defer newly added handlers", CurrentDispatchSnapshots),
        ("handler identities, bounds and foreign snapshots are admitted explicitly", HandlerOwnershipAndLimits),
        ("nested and concurrent reducer dispatch admission cannot wait on itself", ReentrancyAndConcurrency),
        ("strict retained JSON and finite/Unicode/UTF8 admission rejects permissive and oversized inputs", StrictAdmissionAndLimits)
    ];

    private static ExtensionEventDispatcher Dispatcher(ExtensionEventDispatchOptions? options = null) =>
        new(value => _ = ToolResultValueCodec.Read(value), options);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static JsonData Json(string raw) => JsonData.Parse(raw);
    private static ExtensionInputEvent Input(string text = "base", JsonData? images = null) =>
        new(text, ExtensionInputSource.Rpc, images, "followUp");
    private static ExtensionToolCallEvent Call(JsonData? arguments = null) =>
        new("read", "parent/1", arguments ?? Json("{\"path\":\"first\",\"n\":9007199254740993}"), "parent");
    private static ExtensionToolResultEvent Result(JsonData? result = null) =>
        new("read", "parent/1", Json("{\"path\":\"first\"}"), result ?? Json("{\"content\":[{\"type\":\"text\",\"text\":\"original\"}],\"details\":{\"a\":1},\"structuredContent\":{\"secret\":\"private\"},\"usage\":{\"n\":1.0},\"isError\":false,\"terminate\":true,\"unknown\":{\"n\":9007199254740993,\"nil\":null}}"), "parent", OutcomeIsError: true);

    private static Task SourceCatalogCoverage()
    {
        var root = FindRepo();
        var inventoryBytes = File.ReadAllBytes(Path.Combine(root, "compatibility/extensions/native-surface.json"));
        Equal("11cc23450f834eab5c4d6dfe31b2e8e518be1bb64cdd69b6a9aa1f168b026b4f", Convert.ToHexString(SHA256.HashData(inventoryBytes)).ToLowerInvariant());
        using var source = JsonDocument.Parse(inventoryBytes);
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "compatibility/extensions/event-catalog.json")));
        Equal("d86654abb8862e201933517d6f1fce9f88dd117f", catalog.RootElement.GetProperty("sourceSha").GetString());
        var expected = source.RootElement.GetProperty("events").EnumerateArray().ToArray();
        var actual = catalog.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Equal(41, expected.Length); Equal(41, actual.Length);
        Equal(41, actual.Select(row => row.GetProperty("name").GetString()).Distinct(StringComparer.Ordinal).Count());
        for (var index = 0; index < expected.Length; index++)
        {
            var baseline = expected[index]; var row = actual[index];
            Equal(baseline.GetProperty("name").GetString(), row.GetProperty("name").GetString());
            True(row.GetProperty("required").GetBoolean());
            Equal(baseline.GetProperty("eventType").GetString(), row.GetProperty("eventType").GetString());
            var evidence = row.GetProperty("source");
            JsonEqual(baseline.GetProperty("eventSource"), evidence.GetProperty("eventDeclaration"));
            JsonEqual(baseline.GetProperty("dispatchSource"), evidence.GetProperty("dispatcher"));
            Equal(baseline.GetProperty("subscriptionMemberId").GetString(), evidence.GetProperty("subscriptionMemberId").GetString());
            var subscription = evidence.GetProperty("subscription");
            True(subscription.GetProperty("sha256").GetString()!.Length == 64);
            True(subscription.GetProperty("startLine").GetInt32() >= 1540);
            foreach (var field in new[] { "allowedModes", "context", "ordering", "snapshot", "reducer", "validation", "failure", "cancellation", "timeout", "persistence", "reentrancy", "fixtures", "actualStatus" })
                True(row.TryGetProperty(field, out _), "Every mandatory catalog field remains present: " + field);
            Equal("tui,rpc,json,print", string.Join(',', row.GetProperty("allowedModes").EnumerateArray().Select(value => value.GetString())));
            False(row.GetProperty("actualStatus").GetProperty("mandatoryRowRemoved").GetBoolean());
            Equal("Deferred", row.GetProperty("actualStatus").GetProperty("nativeHostIntegration").GetString());
            Equal("Deferred", row.GetProperty("actualStatus").GetProperty("sourceDifferential").GetString());
        }
        Equal("HOLD", catalog.RootElement.GetProperty("gates").GetProperty("P6_G").GetString());
        False(catalog.RootElement.GetProperty("nativeProfile").GetProperty("abiFrozen").GetBoolean());
        var bus = catalog.RootElement.GetProperty("dynamicEventBus"); True(bus.GetProperty("required").GetBoolean());
        Equal(2, bus.GetProperty("members").GetArrayLength());
        var busExpected = source.RootElement.GetProperty("eventBusMembers").EnumerateArray().ToArray();
        for (var index = 0; index < busExpected.Length; index++)
            JsonEqual(busExpected[index], bus.GetProperty("members")[index]);
        Equal("Deferred", bus.GetProperty("actualStatus").GetProperty("native").GetString());
        return Task.CompletedTask;
    }

    private static async Task InputComposition()
    {
        var dispatcher = Dispatcher(); var order = new List<string>();
        var images = Json("[{\"type\":\"image\",\"data\":\"AA==\",\"mimeType\":\"image/png\",\"opaque\":9007199254740993}]");
        dispatcher.InputHandlers.Register("one", 1, "first", (snapshot, _, _) =>
        { order.Add("first"); Equal("base", snapshot.Text); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, "base-one", JsonData.Null)); });
        dispatcher.InputHandlers.Register("two", 2, "second", (snapshot, _, _) =>
        { order.Add("second"); Equal("base-one", snapshot.Text); Equal("9007199254740993", snapshot.Images!.Value[0].GetProperty("opaque").GetRawText()); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, "base-one-two", snapshot.Images)); });
        dispatcher.InputHandlers.Register("two", 2, "handled", (_, _, _) =>
        { order.Add("handled"); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Handled)); });
        dispatcher.InputHandlers.Register("three", 1, "unreached", (_, _, _) => throw new InvalidOperationException("Propagation must have ended."));
        var original = Input(images: images);
        var result = await dispatcher.DispatchInputAsync(dispatcher.InputHandlers.CaptureSnapshot(), original);
        Equal(ExtensionInputAction.Handled, result.Action); Equal("base-one-two", result.Event.Text);
        Equal("first,second,handled", string.Join(',', order)); Equal(0, result.Diagnostics.Length);
        Equal("base", original.Text); Equal("followUp", result.Event.StreamingBehavior); Equal(ExtensionInputSource.Rpc, result.Event.Source);

        var noOp = Dispatcher();
        noOp.InputHandlers.Register("one", 1, "same", (snapshot, _, _) => ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, snapshot.Text, snapshot.Images)));
        Equal(ExtensionInputAction.Continue, (await noOp.DispatchInputAsync(noOp.InputHandlers.CaptureSnapshot(), Input(images: Json("[]")))).Action);
        noOp.InputHandlers.Register("one", 1, "new-array", (snapshot, _, _) => ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, snapshot.Text, Json("[]"))));
        Equal(ExtensionInputAction.Transform, (await noOp.DispatchInputAsync(noOp.InputHandlers.CaptureSnapshot(), Input(images: Json("[]")))).Action);
    }

    private static async Task InputDiagnostics()
    {
        var dispatcher = Dispatcher();
        dispatcher.InputHandlers.Register("one", 1, "none", (_, _, _) => ValueTask.FromResult<ExtensionInputPatch?>(null));
        dispatcher.InputHandlers.Register("one", 1, "throw", (_, _, _) => throw new InvalidOperationException("private payload must not enter diagnostics"));
        dispatcher.InputHandlers.Register("one", 1, "action", (_, _, _) => ValueTask.FromResult<ExtensionInputPatch?>(new((ExtensionInputAction)99)));
        dispatcher.InputHandlers.Register("one", 1, "text", (_, _, _) => ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, null)));
        dispatcher.InputHandlers.Register("one", 1, "images", (_, _, _) => ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, "must-not-commit", Json("{}"))));
        dispatcher.InputHandlers.Register("one", 1, "valid", (snapshot, _, _) =>
        { Equal("base", snapshot.Text); return ValueTask.FromResult<ExtensionInputPatch?>(new(ExtensionInputAction.Transform, "final")); });
        var result = await dispatcher.DispatchInputAsync(dispatcher.InputHandlers.CaptureSnapshot(), Input());
        Equal("final", result.Event.Text); Equal(4, result.Diagnostics.Length);
        Equal(ExtensionEventFailure.HandlerFailed, result.Diagnostics[0].Failure);
        True(result.Diagnostics.Skip(1).All(row => row.Failure == ExtensionEventFailure.InvalidResult));
        False(string.Join(',', result.Diagnostics).Contains("private payload", StringComparison.Ordinal));
        var unsolicited = Dispatcher();
        unsolicited.InputHandlers.Register("one", 1, "foreign", (_, _, _) => throw new OperationCanceledException("unrequested"));
        Equal(ExtensionEventFailure.HandlerFailed, (await unsolicited.DispatchInputAsync(unsolicited.InputHandlers.CaptureSnapshot(), Input())).Diagnostics.Single().Failure);
    }

    private static async Task InputCancellation()
    {
        for (var kind = 0; kind < 3; kind++)
        {
            using var operation = new CancellationTokenSource(); using var session = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
            var dispatcher = Dispatcher(); var entered = Gate(); var release = Gate(); var subsequent = 0;
            dispatcher.InputHandlers.Register("owner", 7, "gated", async (_, context, token) =>
            {
                Equal("owner", context.OwnerId); Equal(7L, context.OwnerGeneration);
                Equal(operation.Token, context.OperationCancellationToken); Equal(session.Token, context.SessionCancellationToken); Equal(lifetime.Token, context.ExtensionLifetimeCancellationToken);
                False(context is PiSharp.Extensions.IExtensionCommandContext or PiSharp.Extensions.IExtensionToolContext);
                entered.SetResult(); await release.Task; token.ThrowIfCancellationRequested();
                return new(ExtensionInputAction.Transform, "never-returned");
            }, lifetime.Token);
            dispatcher.InputHandlers.Register("owner", 7, "later", (_, _, _) => { subsequent++; return ValueTask.FromResult<ExtensionInputPatch?>(null); });
            var work = dispatcher.DispatchInputAsync(dispatcher.InputHandlers.CaptureSnapshot(), Input(), operation.Token, session.Token).AsTask();
            await entered.Task;
            (kind == 0 ? operation : kind == 1 ? session : lifetime).Cancel();
            False(work.IsCompleted, "Requested cancellation must not detach admitted callback work."); release.SetResult();
            await ThrowsAsync<OperationCanceledException>(() => work); Equal(0, subsequent);
        }
    }

    private static async Task ToolCallComposition()
    {
        var dispatcher = Dispatcher(); var later = 0;
        dispatcher.ToolCallHandlers.Register("one", 1, "replace", (_, _, _) => ValueTask.FromResult<ExtensionToolCallPatch?>(new(Json("{\"path\":\"second\",\"n\":9007199254740993}"), Json("{\"block\":false,\"reason\":\"first\",\"opaque\":null}"))));
        dispatcher.ToolCallHandlers.Register("two", 1, "replace-only", (snapshot, _, _) =>
        { Equal("second", snapshot.Arguments.Value.GetProperty("path").GetString()); return ValueTask.FromResult<ExtensionToolCallPatch?>(new(Json("{\"path\":\"third\",\"n\":9007199254740993}"))); });
        dispatcher.ToolCallHandlers.Register("two", 1, "block", (snapshot, _, _) =>
        { Equal("third", snapshot.Arguments.Value.GetProperty("path").GetString()); return ValueTask.FromResult<ExtensionToolCallPatch?>(new(Decision: Json("{\"block\":true,\"reason\":\"denied\",\"terminate\":true}"))); });
        dispatcher.ToolCallHandlers.Register("two", 1, "later", (_, _, _) => { later++; return ValueTask.FromResult<ExtensionToolCallPatch?>(null); });
        var original = Call(); var result = await dispatcher.DispatchToolCallAsync(dispatcher.ToolCallHandlers.CaptureSnapshot(), original);
        True(result.Blocked); True(result.ArgumentsReplaced); Equal(0, later);
        Equal("denied", result.Decision!.Value.GetProperty("reason").GetString()); Equal("9007199254740993", result.Event.Arguments.Value.GetProperty("n").GetRawText());
        Equal("first", original.Arguments.Value.GetProperty("path").GetString()); Equal("parent", result.Event.ParentToolCallId);

        var last = Dispatcher();
        last.ToolCallHandlers.Register("one", 1, "first", (_, _, _) => ValueTask.FromResult<ExtensionToolCallPatch?>(new(Decision: Json("{\"block\":false,\"reason\":\"not-merged\"}"))));
        last.ToolCallHandlers.Register("one", 1, "last", (_, _, _) => ValueTask.FromResult<ExtensionToolCallPatch?>(new(Decision: Json("{}"))));
        var final = await last.DispatchToolCallAsync(last.ToolCallHandlers.CaptureSnapshot(), Call());
        Equal("{}", final.Decision!.ToString()); False(final.Blocked); False(final.ArgumentsReplaced);
        var empty = Dispatcher();
        empty.ToolCallHandlers.Register("one", 1, "none", (_, _, _) => ValueTask.FromResult<ExtensionToolCallPatch?>(null));
        True((await empty.DispatchToolCallAsync(empty.ToolCallHandlers.CaptureSnapshot(), Call())).Decision is null);
    }

    private static async Task ToolCallFailureAndCancellation()
    {
        var failure = Dispatcher(); var later = 0;
        failure.ToolCallHandlers.Register("one", 1, "failure", (_, _, _) => throw new OperationCanceledException("foreign cancellation"));
        failure.ToolCallHandlers.Register("one", 1, "later", (_, _, _) => { later++; return ValueTask.FromResult<ExtensionToolCallPatch?>(null); });
        var error = await ThrowsAsync<ExtensionEventDispatchException>(() => failure.DispatchToolCallAsync(failure.ToolCallHandlers.CaptureSnapshot(), Call()).AsTask());
        Equal(ExtensionEventFailure.HandlerFailed, error.Diagnostic.Failure); False(error.Message.Contains("foreign cancellation", StringComparison.Ordinal)); Equal(0, later);
        foreach (var patch in new[] { new ExtensionToolCallPatch(Json("[]")), new ExtensionToolCallPatch(Decision: Json("{\"block\":\"yes\"}")) })
        {
            var invalid = Dispatcher(); invalid.ToolCallHandlers.Register("one", 1, "invalid", (_, _, _) => ValueTask.FromResult<ExtensionToolCallPatch?>(patch));
            Equal(ExtensionEventFailure.InvalidResult, (await ThrowsAsync<ExtensionEventDispatchException>(() => invalid.DispatchToolCallAsync(invalid.ToolCallHandlers.CaptureSnapshot(), Call()).AsTask())).Diagnostic.Failure);
        }
        using var cancel = new CancellationTokenSource();
        var canceled = Dispatcher();
        canceled.ToolCallHandlers.Register("one", 1, "cancel", (_, _, _) => { cancel.Cancel(); return ValueTask.FromResult<ExtensionToolCallPatch?>(new(Decision: Json("{\"block\":true}"))); });
        await ThrowsAsync<OperationCanceledException>(() => canceled.DispatchToolCallAsync(canceled.ToolCallHandlers.CaptureSnapshot(), Call(), cancel.Token).AsTask());
    }

    private static async Task ToolResultComposition()
    {
        var dispatcher = Dispatcher();
        dispatcher.ToolResultHandlers.Register("one", 1, "redact", (_, _, _) => ValueTask.FromResult<ExtensionToolResultPatch?>(new(Json("{\"content\":[{\"type\":\"text\",\"text\":\"redacted\"}],\"details\":null}"))));
        dispatcher.ToolResultHandlers.Register("two", 1, "augment", (snapshot, _, _) =>
        {
            False(snapshot.Result.Value.TryGetProperty("structuredContent", out _)); True(snapshot.OutcomeIsError);
            Equal(JsonValueKind.Null, snapshot.Result.Value.GetProperty("details").ValueKind);
            return ValueTask.FromResult<ExtensionToolResultPatch?>(new(Json("{\"structuredContent\":{\"safe\":true},\"usage\":{\"n\":0.123456789012345678901},\"isError\":false}")));
        });
        var original = Result(); var result = await dispatcher.DispatchToolResultAsync(dispatcher.ToolResultHandlers.CaptureSnapshot(), original);
        True(result.Modified); Equal(2, result.ReturnedPatches.Length); Equal(0, result.Diagnostics.Length);
        Equal("redacted", result.Event.Result.Value.GetProperty("content")[0].GetProperty("text").GetString());
        Equal("0.123456789012345678901", result.Event.Result.Value.GetProperty("usage").GetProperty("n").GetRawText());
        Equal("9007199254740993", result.Event.Result.Value.GetProperty("unknown").GetProperty("n").GetRawText());
        True(result.Event.Result.Value.GetProperty("terminate").GetBoolean()); True(result.Event.OutcomeIsError);
        False(result.Event.Result.Value.GetProperty("isError").GetBoolean());
        Equal("original", original.Result.Value.GetProperty("content")[0].GetProperty("text").GetString());
        True(original.Result.Value.TryGetProperty("structuredContent", out _));
    }

    private static async Task ToolResultRawPresence()
    {
        var dispatcher = Dispatcher();
        dispatcher.ToolResultHandlers.Register("one", 1, "none", (_, _, _) => ValueTask.FromResult<ExtensionToolResultPatch?>(null));
        dispatcher.ToolResultHandlers.Register("one", 1, "ignored", (_, _, _) => ValueTask.FromResult<ExtensionToolResultPatch?>(new(Json("{\"terminate\":false,\"opaquePatch\":null}"))));
        var unchanged = await dispatcher.DispatchToolResultAsync(dispatcher.ToolResultHandlers.CaptureSnapshot(), Result());
        False(unchanged.Modified); Equal(1, unchanged.ReturnedPatches.Length); True(unchanged.Event.Result.Value.GetProperty("terminate").GetBoolean()); False(unchanged.Event.Result.Value.TryGetProperty("opaquePatch", out _));
        var original = Json("{\"content\":null,\"details\":null,\"structuredContent\":null,\"usage\":null,\"isError\":null,\"terminate\":null,\"opaque\":\"\\u0000\",\"decimal\":0.123456789012345678901}");
        var clearing = Dispatcher();
        clearing.ToolResultHandlers.Register("one", 1, "clear", (_, _, _) => ValueTask.FromResult<ExtensionToolResultPatch?>(new(Json("{\"content\":[],\"details\":null}"))));
        var cleared = await clearing.DispatchToolResultAsync(clearing.ToolResultHandlers.CaptureSnapshot(), Result(original));
        False(cleared.Event.Result.Value.TryGetProperty("structuredContent", out _));
        foreach (var property in new[] { "details", "usage", "isError", "terminate" }) Equal(JsonValueKind.Null, cleared.Event.Result.Value.GetProperty(property).ValueKind);
        Equal("\"\\u0000\"", cleared.Event.Result.Value.GetProperty("opaque").GetRawText()); Equal("0.123456789012345678901", cleared.Event.Result.Value.GetProperty("decimal").GetRawText());
        clearing.ToolResultHandlers.Register("one", 1, "null", (_, _, _) => ValueTask.FromResult<ExtensionToolResultPatch?>(new(Json("{\"structuredContent\":null}"))));
        Equal(JsonValueKind.Null, (await clearing.DispatchToolResultAsync(clearing.ToolResultHandlers.CaptureSnapshot(), Result(original))).Event.Result.Value.GetProperty("structuredContent").ValueKind);
        var absent = Dispatcher();
        var missing = await absent.DispatchToolResultAsync(absent.ToolResultHandlers.CaptureSnapshot(), Result(Json("{}")));
        False(missing.Event.Result.Value.TryGetProperty("details", out _));
    }

    private static async Task ToolResultDiagnosticsAndCancellation()
    {
        var dispatcher = Dispatcher();
        dispatcher.ToolResultHandlers.Register("one", 1, "throw", (_, _, _) => throw new InvalidOperationException("secret"));
        dispatcher.ToolResultHandlers.Register("one", 1, "invalid-content", (_, _, _) => ValueTask.FromResult<ExtensionToolResultPatch?>(new(Json("{\"content\":\"bad\",\"details\":\"not-committed\"}"))));
        dispatcher.ToolResultHandlers.Register("one", 1, "invalid-shape", (_, _, _) => ValueTask.FromResult<ExtensionToolResultPatch?>(new(Json("[]"))));
        dispatcher.ToolResultHandlers.Register("one", 1, "valid", (snapshot, _, _) =>
        { Equal("original", snapshot.Result.Value.GetProperty("content")[0].GetProperty("text").GetString()); return ValueTask.FromResult<ExtensionToolResultPatch?>(new(Json("{\"details\":{\"safe\":true}}"))); });
        var result = await dispatcher.DispatchToolResultAsync(dispatcher.ToolResultHandlers.CaptureSnapshot(), Result());
        Equal(3, result.Diagnostics.Length); Equal(1, result.ReturnedPatches.Length); True(result.Modified);
        Equal(ExtensionEventFailure.HandlerFailed, result.Diagnostics[0].Failure); True(result.Diagnostics.Skip(1).All(row => row.Failure == ExtensionEventFailure.InvalidResult));
        True(result.Event.Result.Value.GetProperty("details").GetProperty("safe").GetBoolean());
        using var cancel = new CancellationTokenSource(); var canceled = Dispatcher();
        canceled.ToolResultHandlers.Register("one", 1, "cancel", (_, _, _) => { cancel.Cancel(); return ValueTask.FromResult<ExtensionToolResultPatch?>(new(Json("{\"details\":null}"))); });
        await ThrowsAsync<OperationCanceledException>(() => canceled.DispatchToolResultAsync(canceled.ToolResultHandlers.CaptureSnapshot(), Result(), sessionCancellationToken: cancel.Token).AsTask());
        var foreign = Dispatcher(); foreign.ToolResultHandlers.Register("one", 1, "foreign", (_, _, _) => throw new OperationCanceledException("not requested"));
        Equal(ExtensionEventFailure.HandlerFailed, (await foreign.DispatchToolResultAsync(foreign.ToolResultHandlers.CaptureSnapshot(), Result())).Diagnostics.Single().Failure);
    }

    private static async Task CurrentDispatchSnapshots()
    {
        var input = Dispatcher(); await Snapshot(input.InputHandlers, snapshot => input.DispatchInputAsync(snapshot, Input()).AsTask());
        var call = Dispatcher(); await Snapshot(call.ToolCallHandlers, snapshot => call.DispatchToolCallAsync(snapshot, Call()).AsTask());
        var result = Dispatcher(); await Snapshot(result.ToolResultHandlers, snapshot => result.DispatchToolResultAsync(snapshot, Result()).AsTask());
    }
    private static async Task Snapshot<TEvent, TPatch>(ExtensionReducerHandlerSet<TEvent, TPatch> set,
        Func<ExtensionReducerSnapshot<TEvent, TPatch>, Task> dispatch) where TPatch : class
    {
        var entered = Gate(); var release = Gate(); var order = new List<string>(); var calls = 0;
        set.Register("one", 1, "first", async (_, _, _) =>
        { order.Add("first"); if (++calls == 1) { entered.SetResult(); await release.Task; } return null; });
        var second = set.Register("two", 1, "second", (_, _, _) => { order.Add("second"); return ValueTask.FromResult<TPatch?>(null); });
        var old = set.CaptureSnapshot(); var work = dispatch(old); await entered.Task;
        second.Dispose(); second.Dispose(); set.Register("one", 1, "new", (_, _, _) => { order.Add("new"); return ValueTask.FromResult<TPatch?>(null); });
        Equal(2, old.Registrations.Length); False(work.IsCompleted); release.SetResult(); await work;
        Equal("first,second", string.Join(',', order));
        var next = set.CaptureSnapshot(); True(next.Revision > old.Revision); await dispatch(next);
        Equal("first,second,first,new", string.Join(',', order));
    }

    private static async Task HandlerOwnershipAndLimits()
    {
        var set = new ExtensionReducerHandlerSet<ExtensionInputEvent, ExtensionInputPatch>(2);
        ExtensionReducerCallback<ExtensionInputEvent, ExtensionInputPatch> callback = (_, _, _) => ValueTask.FromResult<ExtensionInputPatch?>(null);
        using var first = set.Register("owner", 1, "id", callback);
        Throws<InvalidOperationException>(() => set.Register("owner", 1, "id", callback));
        using var other = set.Register("other", 1, "id", callback);
        Throws<InvalidOperationException>(() => set.Register("third", 1, "id", callback));
        Throws<ArgumentException>(() => set.Register("bad owner", 1, "x", callback)); Throws<ArgumentException>(() => set.Register("owner", 0, "x", callback));
        var dispatcher = Dispatcher(); await ThrowsAsync<ArgumentException>(() => dispatcher.DispatchInputAsync(set.CaptureSnapshot(), Input()).AsTask());
        other.Dispose(); using var generation = set.Register("owner", 2, "id", callback);
        Equal(2L, set.CaptureSnapshot().Registrations.Last().OwnerGeneration);
        var grouped = Dispatcher(); var order = new List<string>();
        grouped.InputHandlers.Register("a", 1, "a1", (_, _, _) => { order.Add("a1"); return ValueTask.FromResult<ExtensionInputPatch?>(null); });
        grouped.InputHandlers.Register("b", 1, "b1", (_, _, _) => { order.Add("b1"); return ValueTask.FromResult<ExtensionInputPatch?>(null); });
        grouped.InputHandlers.Register("a", 1, "a2", (_, _, _) => { order.Add("a2"); return ValueTask.FromResult<ExtensionInputPatch?>(null); });
        await grouped.DispatchInputAsync(grouped.InputHandlers.CaptureSnapshot(), Input()); Equal("a1,a2,b1", string.Join(',', order));
        var explicitOrder = Dispatcher(); order.Clear();
        explicitOrder.InputHandlers.Register("a", 1, "a1", (_, _, _) => { order.Add("a1"); return ValueTask.FromResult<ExtensionInputPatch?>(null); }, ownerDispatchOrder: 2);
        explicitOrder.InputHandlers.Register("b", 1, "b1", (_, _, _) => { order.Add("b1"); return ValueTask.FromResult<ExtensionInputPatch?>(null); }, ownerDispatchOrder: 1);
        explicitOrder.InputHandlers.Register("a", 1, "a2", (_, _, _) => { order.Add("a2"); return ValueTask.FromResult<ExtensionInputPatch?>(null); }, ownerDispatchOrder: 2);
        await explicitOrder.DispatchInputAsync(explicitOrder.InputHandlers.CaptureSnapshot(), Input()); Equal("b1,a1,a2", string.Join(',', order));
        Throws<ArgumentException>(() => explicitOrder.InputHandlers.Register("a", 1, "changed", callback, ownerDispatchOrder: 3));
        var cumulative = new ExtensionReducerHandlerSet<ExtensionInputEvent, ExtensionInputPatch>(2, 1);
        cumulative.Register("a", 1, "id", callback).Dispose();
        Throws<InvalidOperationException>(() => cumulative.Register("b", 1, "id", callback));
    }

    private static async Task ReentrancyAndConcurrency()
    {
        var nested = Dispatcher(new(MaximumDispatchDepth: 1)); ExtensionEventFailure? denied = null;
        nested.InputHandlers.Register("one", 1, "nested", async (_, _, _) =>
        { denied = (await ThrowsAsync<ExtensionEventDispatchException>(() => nested.DispatchInputAsync(nested.InputHandlers.CaptureSnapshot(), Input()).AsTask())).Diagnostic.Failure; return null; });
        Equal(0, (await nested.DispatchInputAsync(nested.InputHandlers.CaptureSnapshot(), Input())).Diagnostics.Length); Equal<ExtensionEventFailure?>(ExtensionEventFailure.ReentrantLimit, denied);
        var concurrent = Dispatcher(new(MaximumConcurrentDispatches: 1)); var entered = Gate(); var release = Gate();
        concurrent.InputHandlers.Register("one", 1, "wait", async (_, _, _) => { entered.SetResult(); await release.Task; return null; });
        var work = concurrent.DispatchInputAsync(concurrent.InputHandlers.CaptureSnapshot(), Input()).AsTask(); await entered.Task;
        Equal(ExtensionEventFailure.LimitExceeded, (await ThrowsAsync<ExtensionEventDispatchException>(() => concurrent.DispatchInputAsync(concurrent.InputHandlers.CaptureSnapshot(), Input()).AsTask())).Diagnostic.Failure);
        False(work.IsCompleted); release.SetResult(); await work;
    }

    private static async Task StrictAdmissionAndLimits()
    {
        var dispatcher = Dispatcher();
        using var permissive = JsonDocument.Parse("{\"path\":\"file\",}", new JsonDocumentOptions { AllowTrailingCommas = true });
        var retained = JsonData.FromElement(permissive.RootElement);
        await ThrowsAsync<JsonException>(() => dispatcher.DispatchToolCallAsync(dispatcher.ToolCallHandlers.CaptureSnapshot(), Call(retained)).AsTask());
        await ThrowsAsync<ArgumentException>(() => dispatcher.DispatchToolCallAsync(dispatcher.ToolCallHandlers.CaptureSnapshot(), Call(Json("{\"n\":1e999}"))).AsTask());
        var bounded = Dispatcher(new(MaximumTextCharacters: 1));
        await ThrowsAsync<ArgumentException>(() => bounded.DispatchInputAsync(bounded.InputHandlers.CaptureSnapshot(), Input("ab")).AsTask());
        await ThrowsAsync<ArgumentException>(() => dispatcher.DispatchInputAsync(dispatcher.InputHandlers.CaptureSnapshot(), Input("\ud800")).AsTask());
        var utf8 = Dispatcher(new(MaximumJsonBytes: 20));
        await ThrowsAsync<ArgumentException>(() => utf8.DispatchToolCallAsync(utf8.ToolCallHandlers.CaptureSnapshot(), Call(Json("{\"path\":\"\u03c0\ud83d\ude42\u03c0\ud83d\ude42\"}"))).AsTask());
        var numeric = Call(Json("{\"big\":9007199254740993,\"precise\":0.123456789012345678901,\"negativeZero\":-0}"));
        var admitted = await dispatcher.DispatchToolCallAsync(dispatcher.ToolCallHandlers.CaptureSnapshot(), numeric);
        Equal(numeric.Arguments.ToString(), admitted.Event.Arguments.ToString());
        Throws<JsonException>(() => Json("{\"x\":1,\"x\":2}"));
    }

    private static string FindRepo()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "compatibility/extensions/native-surface.json"))) return directory.FullName;
        throw new InvalidOperationException("Committed extension source inventory was not found.");
    }
    private static void JsonEqual(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind) throw new InvalidOperationException("JSON kinds differ.");
        if (expected.ValueKind == JsonValueKind.Object)
        {
            Equal(expected.EnumerateObject().Count(), actual.EnumerateObject().Count());
            foreach (var property in expected.EnumerateObject()) { True(actual.TryGetProperty(property.Name, out var other)); JsonEqual(property.Value, other); }
        }
        else if (expected.ValueKind == JsonValueKind.Array)
        {
            Equal(expected.GetArrayLength(), actual.GetArrayLength());
            for (var index = 0; index < expected.GetArrayLength(); index++) JsonEqual(expected[index], actual[index]);
        }
        else Equal(expected.GetRawText(), actual.GetRawText());
    }
    private static void True(bool value, string message = "Expected true.") { if (!value) throw new InvalidOperationException(message); }
    private static void False(bool value, string message = "Expected false.") => True(!value, message);
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; got {actual}."); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
