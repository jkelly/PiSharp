using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.AI.Providers;
using PiSharp.AI;
using PiSharp.Contracts;
using OriginalRecord = OriginalTaskRecord;

var groups = new (string Name, Func<Task> Run)[] {
    ("payload-pure-factory-authoritative-and-configured-limits", Controls.Payload),
    ("response-original-task-before-start", Controls.Response),
    ("raw-original-task-before-normalization", Controls.Raw),
    ("acquisition-error-envelope-and-status-body", Controls.Acquisition),
    ("late-original-task-error-and-aggregate-siblings", Controls.Late),
    ("held-cancel-versus-faulted-original", Controls.Cancel),
    ("source-cleanup-identities-and-serialization", Controls.Cleanup)
};
var originals = new List<OriginalTaskRecord>();
try
{
    foreach (var group in groups) {
        var record = new OriginalTaskRecord(group.Name); originals.Add(record);
        try { var original = group.Run(); record.Original = original; await original; } catch (Exception error) { record.Direct = error; } finally { record.Capture(); }
    }
    Console.WriteLine(JsonSerializer.Serialize(new { groups = groups.Length, sourceDerived = true,
        records = originals.Select(QualificationReporter.Project).ToArray(), held = Controls.Originals.Select(QualificationReporter.Project).ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
    return originals.Any(record => record.Direct is not null || record.Original?.IsCompletedSuccessfully != true) ? 1 : 0;
}
catch (Exception failure) { throw new QualificationReportingFailure(failure, originals.ToArray(), Controls.Originals.ToArray()); }

static class Controls
{
    internal static readonly List<OriginalRecord> Originals = [];
    private static readonly ModelDescriptor Model = new("fixture", "openai-responses", "openai");
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/responses");
    private static ChatRequest Request => new(Model, []);
    private static ResponsesKeyAuthRequestOptions Options => new(SupportsMaxOutputTokens: true, MaxOutputTokens: 20);
    private static readonly string[] Partial = [
        "{\"type\":\"response.created\",\"response\":{\"id\":\"r\"}}",
        "{\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"m\"}}",
        "{\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"partial\"}" ];
    private const string Delta = "{\"type\":\"response.output_text.delta\",\"output_index\":0,\"delta\":\"must-not-appear\"}";
    private const string Completed = "{\"type\":\"response.completed\",\"response\":{\"id\":\"r\",\"status\":\"completed\",\"output\":[]}}";
    private static void Check(bool value, [CallerLineNumber] int line = 0) { if (!value) throw new InvalidOperationException($"Responses hooks fixture assertion at line {line}."); }
    private static NativeHttpModelProvider Bind(Handler handler, ResponsesKeyAuthRequestOptions options) => NativeProviderFactory.CreateResponses(Model, Endpoint, "SYNTHETIC", new(Reasoning: false), options, handler);
    private static async Task<List<StreamEvent>> Drain(IChatTransport transport, CancellationToken token = default) { var frames = new List<StreamEvent>(); await foreach (var frame in transport.StreamAsync(Request, token)) frames.Add(frame); return frames; }
    private static StreamError Error(List<StreamEvent> frames) { Check(frames[^1] is StreamError); return (StreamError)frames[^1]; }
    private static string? Field(StreamTerminalEvent terminal, string name) { if (terminal.Message.ExtraProperties is { } fields && fields.TryGet(name, out var value)) return value?.Value.GetString(); return null; }
    private static string Text(StreamTerminalEvent terminal) => string.Concat(terminal.Message.Content.OfType<TextContent>().Select(part => part.Text));
    private static JsonData Replace(JsonData payload, Action<JsonObject> edit) { var root = JsonNode.Parse(payload.ToString())!.AsObject(); edit(root); return JsonData.Parse(root.ToJsonString()); }
    private static HttpResponseMessage Success(params string[] events) => new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(events.Select(json => "data: " + json + "\n\n")), Encoding.UTF8, "text/event-stream") };

    internal static async Task Payload()
    {
        var calls = 0; JsonData? replacement = null; string? retained = null;
        var options = Options with { OnPayload = (payload, _, _) => {
            calls++; Check(payload.Value.GetProperty("max_output_tokens").GetInt32() == 20);
            replacement = Replace(payload, root => { root["max_output_tokens"] = 24; root["fixture_extension"] = true; }); retained = replacement.ToString();
            return ValueTask.FromResult<JsonData?>(replacement);
        } };
        var factory = new ResponsesKeyAuthRequestFactory(Endpoint, Model, new(Reasoning: false), options);
        using (factory.Create(Request, "SYNTHETIC")) Check(calls == 0);
        using (var handler = new Handler(() => Success(Completed)))
        using (var provider = Bind(handler, options)) {
            var frames = await Drain(provider); Check(calls == 1 && handler.Calls == 1 && frames[^1] is StreamDone);
            var sent = handler.Payload ?? throw new InvalidOperationException("Missing fixture payload.");
            Check(sent.GetProperty("max_output_tokens").GetInt32() == 24 && sent.GetProperty("fixture_extension").GetBoolean());
            Check(replacement!.ToString() == retained);
        }
        foreach (var variant in new[] { "bytes", "depth", "output" }) {
            using var handler = new Handler(() => Success(Completed));
            using var provider = Bind(handler, Options with { MaximumPayloadBytes = 512, MaximumPayloadDepth = 3, MaximumOutputTokens = 30,
                OnPayload = (payload, _, _) => ValueTask.FromResult<JsonData?>(Replace(payload, root => {
                    if (variant == "bytes") root["extension"] = new string('x', 1024);
                    if (variant == "depth") root["extension"] = JsonNode.Parse("{\"a\":{\"b\":{\"c\":1}}}");
                    if (variant == "output") root["max_output_tokens"] = 31;
                })) });
            var error = Error(await Drain(provider)); Check(handler.Calls == 0 && error.NativeSourceException is ResponsesKeyAuthRequestException { Failure: ResponsesKeyAuthRequestFailure.ResourceLimit });
        }
    }

    internal static async Task Response()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(() => { var response = Success(Completed); response.Headers.TryAddWithoutValidation("X-Fixture", "one"); return response; });
        using var provider = Bind(handler, Options with { OnResponse = (observation, model, _) => {
            Check(model == Model && observation.Value.GetProperty("status").GetInt32() == 200 && observation.Value.GetProperty("headers").GetProperty("x-fixture").GetString() == "one");
            entered.TrySetResult(); return new(release.Task);
        } });
        var enumerator = provider.StreamAsync(Request).GetAsyncEnumerator(); var first = enumerator.MoveNextAsync().AsTask(); Exception? checkFailure = null;
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Check(!release.Task.IsCompleted && !first.IsCompleted); }
        catch (Exception error) { checkFailure = error; } finally { release.TrySetResult(); }
        Exception? joinFailure = null, disposeFailure = null; var started = false;
        try { await JoinAll(("response-original", release.Task, null), ("response-first-pull-original", first, null)); started = first.Result && enumerator.Current is StreamStarted; }
        catch (Exception error) { joinFailure = error; }
        try { await Join("response-dispose-original", enumerator.DisposeAsync().AsTask()); } catch (Exception error) { disposeFailure = error; }
        var failures = new[] { checkFailure, joinFailure, disposeFailure }.OfType<Exception>().ToArray();
        if (failures.Length != 0) throw new AggregateException("Response fixture originals settled with faults.", failures);
        Check(started);
        var supplied = new InvalidOperationException("fixture response observer failure"); var original = Task.FromException(supplied);
        var rejectedRecord = Register("response-rejected-original", original, supplied);
        await WithJoinedOriginals(async () => {
            using var rejectHandler = new Handler(() => Success(Completed)); using var rejected = Bind(rejectHandler, Options with { OnResponse = (_, _, _) => new(original) });
            var errorFrame = Error(await Drain(rejected)); Check(ReferenceEquals(errorFrame.NativeSourceException, supplied) && ReferenceEquals(errorFrame.NativeSourceTask, original));
        }, rejectedRecord);
    }

    internal static async Task Raw()
    {
        var supplied = new InvalidOperationException("fixture observer failure"); var original = Task.FromException(supplied); var seen = new List<string>();
        var record = Register("raw-rejected-original", original, supplied);
        await WithJoinedOriginals(async () => {
            using var handler = new Handler(() => Success(Partial.Take(1).Concat(new[] { "{\"type\":\"response.in_progress\",\"response\":{\"id\":\"r\",\"status\":\"in_progress\",\"output\":[]}}" }).Concat(Partial.Skip(1)).Concat(new[] { Delta, Completed }).ToArray()));
            using var provider = Bind(handler, Options with { OnProviderStreamEvent = (dto, _, _) => {
                seen.Add(dto.Value.GetProperty("type").GetString()!);
                return dto.Value.TryGetProperty("delta", out var delta) && delta.GetString() == "must-not-appear" ? new(original) : ValueTask.CompletedTask;
            } });
            var terminal = Error(await Drain(provider)); Check(Text(terminal) == "partial" && Field(terminal, "errorMessage") == supplied.Message);
            Check(seen.SequenceEqual(new[] { "response.created", "response.in_progress", "response.output_item.added", "response.output_text.delta", "response.output_text.delta" }));
            Check(ReferenceEquals(terminal.NativeSourceException, supplied) && ReferenceEquals(terminal.NativeSourceTask, original));
        }, record);
    }

    internal static async Task Acquisition()
    {
        var supplied = new InvalidOperationException("fixture source failure");
        using (var handler = new Handler(() => throw supplied))
        using (var provider = Bind(handler, Options)) { var frames = await Drain(provider); var terminal = Error(frames); Check(frames.Count == 1 && ReferenceEquals(terminal.NativeSourceException, supplied) && Field(terminal, "errorMessage") == supplied.Message); }
        using (var handler = new Handler(() => new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(" fixture unavailable ") }))
        using (var provider = Bind(handler, Options)) { var frames = await Drain(provider); Check(frames.Count == 1 && Field(Error(frames), "errorMessage") == "OpenAI API error (503): fixture unavailable"); }
        foreach (var (body, expected) in new[] {
            ("{\"error\":{\"message\":\"blocked\",\"code\":\"fixture\"}}", "{\"message\":\"blocked\",\"code\":\"fixture\"}"),
            (new string('x', 4005), new string('x', 4000) + "... [truncated 5 chars]") })
        {
            using var handler = new Handler(() => new(HttpStatusCode.Forbidden) { Content = new StringContent(body) });
            using var provider = Bind(handler, Options);
            Check(Field(Error(await Drain(provider)), "errorMessage") == "OpenAI API error (403): " + expected);
        }
    }

    internal static async Task Late()
    {
        var a = new InvalidOperationException("fixture late failure"); var b = new ArgumentException("fixture late sibling");
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); pending.SetException(new Exception[] { a, b });
        var record = Register("late-original-two-siblings", pending.Task, a);
        await WithJoinedOriginals(async () => {
            var source = new FaultSource(pending.Task, Task.CompletedTask);
            var terminal = Error(await Drain(new ResponsesTextToolTransport((_, _) => source)));
            Check(Text(terminal) == "partial" && ReferenceEquals(terminal.NativeSourceException, a) && ReferenceEquals(terminal.NativeSourceTask, pending.Task));
            var captured = terminal.NativeSourceTask ?? throw new InvalidOperationException("Missing original fault task.");
            Check(captured.IsFaulted && ReferenceEquals(captured.Exception!.InnerExceptions[1], b));
        }, record);
        var usage = new InvalidOperationException("subscription_sharing_usage_limit_exceeded");
        var usageTask = Task.FromException<bool>(usage);
        var usageRecord = Register("usage-limit-original", usageTask, usage);
        await WithJoinedOriginals(async () => {
            var limited = Error(await Drain(new ResponsesTextToolTransport((_, _) => new FaultSource(usageTask, Task.CompletedTask))));
            Check(Field(limited, "errorMessage") == usage.Message + "\nCheck your ChatGPT usage: https://chatgpt.com/settings/usage");
        }, usageRecord);
    }

    internal static async Task Cancel()
    {
        foreach (var reject in new[] { false, true }) {
            using var stop = new CancellationTokenSource(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var supplied = new InvalidOperationException("fixture foreign fault after caller cancel");
            using var handler = new Handler(() => Success(Partial.Concat(new[] { Delta, Completed }).ToArray()));
            using var provider = Bind(handler, Options with { OnProviderStreamEvent = (dto, _, _) => {
                if (dto.Value.TryGetProperty("delta", out var delta) && delta.GetString() == "must-not-appear") { entered.TrySetResult(); return new(release.Task); }
                return ValueTask.CompletedTask;
            } });
            var drain = Drain(provider, stop.Token); Exception? checkFailure = null;
            try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); stop.Cancel(); Check(!drain.IsCompleted && !release.Task.IsCompleted); }
            catch (Exception error) { checkFailure = error; }
            finally { if (reject) release.TrySetException(supplied); else release.TrySetResult(); }
            await JoinAll(("cancel-held-hook-original", release.Task, reject ? supplied : null), ("cancel-held-drain-original", drain, null));
            var terminal = Error(drain.Result); Check(terminal.Reason == StopReason.Aborted && Text(terminal) == "partial");
            if (reject) Check(ReferenceEquals(terminal.NativeSourceException, supplied) && ReferenceEquals(terminal.NativeSourceTask, release.Task) && terminal.NativeSourceTask!.IsFaulted);
            else Check(release.Task.IsCompletedSuccessfully && terminal.NativeSourceTask?.IsCanceled == true && terminal.NativeSourceException is OperationCanceledException);
            if (checkFailure is not null) throw checkFailure;
        }
        using var foreign = new CancellationTokenSource(); foreign.Cancel();
        var original = Task.FromCanceled(foreign.Token);
        var canceledRecord = Register("foreign-canceled-hook-original", original); canceledRecord.ExpectedCanceled = true;
        await WithJoinedOriginals(async () => {
            using var cancelHandler = new Handler(() => Success(Partial.Concat(new[] { Delta, Completed }).ToArray()));
            using var cancelProvider = Bind(cancelHandler, Options with { OnProviderStreamEvent = (dto, _, _) =>
                dto.Value.TryGetProperty("delta", out var delta) && delta.GetString() == "must-not-appear" ? new(original) : ValueTask.CompletedTask });
            var foreignTerminal = Error(await Drain(cancelProvider));
            Check(foreignTerminal.Reason == StopReason.Error && Text(foreignTerminal) == "partial" && ReferenceEquals(foreignTerminal.NativeSourceTask, original) && original.IsCanceled);
        }, canceledRecord);
        Check(canceledRecord.Direct is OperationCanceledException canceled && canceled.CancellationToken == foreign.Token);
    }

    internal static async Task Cleanup()
    {
        var sourceFault = new InvalidOperationException("fixture source original"); var cleanupFault = new InvalidOperationException("fixture cleanup original");
        var cleanupSibling = new ArgumentException("fixture cleanup sibling");
        var cleanupSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cleanupSource.SetException(new Exception[] { cleanupFault, cleanupSibling });
        var sourceTask = Task.FromException<bool>(sourceFault); var cleanupTask = cleanupSource.Task;
        var sourceRecord = Register("source-cleanup-source-original", sourceTask, sourceFault);
        var cleanupRecord = Register("source-cleanup-dispose-original", cleanupTask, cleanupFault);
        await WithJoinedOriginals(async () => {
            var terminal = Error(await Drain(new ResponsesTextToolTransport((_, _) => new FaultSource(sourceTask, cleanupTask))));
            Check(ReferenceEquals(terminal.NativeSourceException, sourceFault) && ReferenceEquals(terminal.NativeSourceTask, sourceTask));
            Check(terminal.NativeCleanupExceptions!.Any(error => ReferenceEquals(error, cleanupFault)) && terminal.NativeCleanupTasks!.Any(task => ReferenceEquals(task, cleanupTask)));
            Check(ReferenceEquals(cleanupTask.Exception!.InnerExceptions[1], cleanupSibling) && Field(terminal, "errorMessage") == sourceFault.Message);
            var baseline = new StreamError(terminal.Reason, terminal.Message, terminal.ExtraProperties);
            var (reason, message, properties) = baseline; Check(reason == terminal.Reason && ReferenceEquals(message, terminal.Message) && properties == terminal.ExtraProperties);
            Check(JsonSerializer.Serialize(terminal) == JsonSerializer.Serialize(baseline));
            Check(!PiWireJson.WriteMessage(terminal.Message).ToString().Contains("NativeSource", StringComparison.Ordinal));
        }, sourceRecord, cleanupRecord);
    }

    private static async Task Join(string name, Task original, Exception? expected = null)
    {
        await JoinRegistered(Register(name, original, expected));
    }
    private static OriginalRecord Register(string name, Task original, Exception? expected = null)
    {
        var record = new OriginalRecord(name, original) { ExpectedDirect = expected }; Originals.Add(record); record.Capture(); return record;
    }
    private static async Task JoinRegistered(OriginalRecord record)
    {
        var original = record.Original ?? throw new InvalidOperationException("Missing registered original.");
        try { await original; } catch (Exception error) {
            record.Direct = error;
            if (!(record.ExpectedCanceled && original.IsCanceled && error is OperationCanceledException) &&
                (record.ExpectedDirect is null || !ReferenceEquals(error, record.ExpectedDirect))) throw;
        }
        finally { record.Capture(); }
        Check(original.IsCompleted);
    }
    private static async Task WithJoinedOriginals(Func<Task> invoke, params OriginalRecord[] registered)
    {
        var failures = new List<Exception>();
        try { await invoke(); } catch (Exception error) { failures.Add(error); }
        foreach (var record in registered) try { await JoinRegistered(record); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("Fixture originals joined despite invocation/assertion failure.",
            failures.Concat(registered.SelectMany(record => record.Faults())));
    }
    private static async Task JoinAll(params (string Name, Task Original, Exception? Expected)[] tasks)
    {
        var start = Originals.Count; var failures = new List<Exception>();
        foreach (var item in tasks) try { await Join(item.Name, item.Original, item.Expected); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException("All fixture originals joined with faults.",
            failures.Concat(Originals.Skip(start).SelectMany(record => record.Faults())));
    }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        internal int Calls; internal JsonElement? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
            Calls++; using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)); Payload = json.RootElement.Clone(); return response();
        }
    }
    private sealed class FaultSource(Task<bool> fault, Task disposal) : IAsyncEnumerable<JsonData>, IAsyncEnumerator<JsonData>
    {
        private int position = -1;
        public JsonData Current => JsonData.Parse(Partial[position]);
        public IAsyncEnumerator<JsonData> GetAsyncEnumerator(CancellationToken token = default) => this;
        public ValueTask<bool> MoveNextAsync() => ++position < Partial.Length ? ValueTask.FromResult(true) : new(fault);
        public ValueTask DisposeAsync() => new(disposal);
    }
}
