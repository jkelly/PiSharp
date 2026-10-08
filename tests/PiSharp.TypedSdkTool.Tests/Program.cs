using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PiSharp.AI;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Runtime;

if (args.Length != 0 && (args.Length != 2 || args[0] != "--report"))
    throw new ArgumentException("Expected --report <new-path> or no arguments.");
var cases = new (string Name, Func<Task> Run)[]
{
    ("prepared final arguments typed details schema and original invocation progress", Prepared),
    ("optional result fields preserve absent explicit null false and opaque structured numbers", OptionalFields),
    ("typed deserialization and prepared schema policy reject before user callback", InvalidArguments),
    ("cancellation and owner close join original typed callback finally", CancellationDrain),
    ("quiescence holds typed callback denies fresh dispatch and resumes", Quiescence),
    ("callback original aggregate DAG and synchronous exception retain identity", FaultIdentity),
    ("host result and update limits still reject typed conversion", ResultLimits),
    ("single consume ValueTask callback is awaited exactly once", SingleConsumption)
};
var failed = 0;
foreach (var test in cases)
{
    Task? original = null; Exception? caught = null;
    try { original = test.Run(); }
    catch (Exception error) { caught = error; }
    if (original is not null)
    {
        try { await original; }
        catch (Exception error) { caught = error; }
    }
    var passed = original?.Status == TaskStatus.RanToCompletion && caught is null;
    Audit.Record(test.Name, original, caught, passed);
    if (passed) Console.WriteLine("PASS " + test.Name);
    else { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + (original?.Exception ?? caught)); }
}
if (args.Length == 2)
{
    var report = new Report("native-typed-sdk-tool-r594", "d86654abb8862e201933517d6f1fce9f88dd117f",
        cases.Length, cases.Length, cases.Length - failed, failed, Audit.Rows.ToArray());
    var bytes = JsonSerializer.SerializeToUtf8Bytes(report, Metadata.Default.Report);
    using var stream = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
    stream.Write(bytes, 0, bytes.Length);
}
return failed == 0 ? 0 : 1;

static async Task Prepared()
{
    await using var registry = new ExtensionRegistry();
    var updateEntered = Gate(); var releaseUpdate = Gate(); var executions = 0; var authorization = new Policy();
    IExtensionToolInvocationContext? actual = null;
    var descriptor = Define(async (input, context, token) =>
    {
        executions++; Check(input.Value == "final" && input.Keep is null);
        actual = context as IExtensionToolInvocationContext ?? throw new InvalidOperationException("No native invocation context.");
        Check(actual.ToolCallId == "typed-real-id");
        await actual.ReportUpdateAsync(Result("partial", actual.ToolCallId), Metadata.Default.Details, token);
        return Result("final result", actual.ToolCallId) with { StructuredContent = JsonData.Parse("{\"value\":17}"), IsError = false };
    });
    await using var scope = await registry.ActivateAsync("typed-owner", new Probe(descriptor));
    var final = JsonData.Parse("{\"value\":\"final\",\"keep\":null}");
    var binding = new ExtensionAgentBinding(registry, authorization, Validate,
        transforms: [(_, action, _) => ValueTask.FromResult(action with { Arguments = final })]);
    Check(ReferenceEquals(binding.Registrations.Single().Parameters, Schema()));
    var original = binding.Tools.Single().Executor.ExecuteAsync(Call("typed-real-id", Input("initial")),
        async (partial, _) => { Check(partial.Details.Value.GetProperty("id").GetString() == "typed-real-id"); updateEntered.TrySetResult(); await releaseUpdate.Task; },
        CancellationToken.None).AsTask();
    ToolResult? result = null; Exception? caught = null;
    try { await updateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check(!original.IsCompleted && executions == 1); }
    finally { releaseUpdate.TrySetResult(); try { result = await original; } catch (Exception error) { caught = error; } Audit.Record("prepared invocation", original, caught); }
    if (caught is not null) throw caught;
    Check(ReferenceEquals(authorization.Arguments, final) && authorization.Calls == 1);
    Check(result!.Details.Value.GetProperty("id").GetString() == "typed-real-id");
    Check(result.Content.Single().Text == "final result" && result.ToJson().Value.GetProperty("isError").ValueKind == JsonValueKind.False);
    Task? stale = null; Exception? staleError = null;
    try { stale = actual!.ReportUpdateAsync(Result("stale", "id"), Metadata.Default.Details).AsTask(); }
    catch (Exception error) { staleError = error; Audit.Record("stale typed update synchronous rejection", null, error); }
    if (stale is not null) staleError = await Observe(stale, "stale typed update");
    Check(staleError is ExtensionRegistrationException { Failure: ExtensionRegistrationFailure.InactiveScope });
}

static Task OptionalFields()
{
    // A source-generated metadata override distinguishes the supplied contract from reflection/default metadata.
    var explicitOptions = new JsonSerializerOptions
    {
        TypeInfoResolver = Metadata.Default.WithAddedModifier(typeInfo =>
        {
            if (typeInfo.Type == typeof(Details))
                typeInfo.Properties.Single(property => property.Name == "id").Name = "provided_metadata_id";
        })
    };
    var explicitDetails = (JsonTypeInfo<Details>)explicitOptions.GetTypeInfo(typeof(Details));
    var explicitJson = Result("text", "metadata-marker").ToJson(explicitDetails);
    Check(explicitJson.Value.GetProperty("details").GetProperty("provided_metadata_id").GetString() == "metadata-marker" &&
        !explicitJson.Value.GetProperty("details").TryGetProperty("id", out _));
    var absent = Result("text", "id").ToJson(Metadata.Default.Details);
    Check(!absent.Value.TryGetProperty("structuredContent", out _) && !absent.Value.TryGetProperty("usage", out _) &&
        !absent.Value.TryGetProperty("isError", out _) && !absent.Value.TryGetProperty("terminate", out _));
    var present = Result("text", "id") with { StructuredContent = JsonData.Null, Usage = JsonData.Null, IsError = false, Terminate = true };
    var json = present.ToJson(Metadata.Default.Details);
    Check(json.Value.GetProperty("structuredContent").ValueKind == JsonValueKind.Null &&
        json.Value.GetProperty("usage").ValueKind == JsonValueKind.Null && json.Value.GetProperty("isError").ValueKind == JsonValueKind.False);
    Check(JsonElement.DeepEquals(json.Value, ToolResultValueCodec.Read(json).ToJson().Value));
    var opaque = present with { StructuredContent = JsonData.Parse("{\"integer\":9007199254740993,\"negativeZero\":-0}") };
    Check(opaque.ToJson(Metadata.Default.Details).Value.GetProperty("structuredContent").GetRawText() == "{\"integer\":9007199254740993,\"negativeZero\":-0}");
    var nullDetails = new ExtensionTypedToolResult<Details>(JsonData.Parse("[]"), null!).ToJson(Metadata.Default.Details);
    Check(nullDetails.Value.GetProperty("details").ValueKind == JsonValueKind.Null);
    Check(JsonElement.DeepEquals(absent.Value, ToolResultValueCodec.Read(absent).ToJson().Value));
    return Task.CompletedTask;
}

static async Task InvalidArguments()
{
    await using var registry = new ExtensionRegistry(); var calls = 0;
    await using var scope = await registry.ActivateAsync("typed-owner", new Probe(Define((_, _, _) =>
    { calls++; return ValueTask.FromResult(Result("never", "id")); })));
    foreach (var input in new[] { "{}", "{\"value\":17}", "{\"value\":\"ok\",\"extra\":true}" })
    {
        var original = registry.InvokeToolAsync(registry.CaptureSnapshot(), "typed_tool", JsonData.Parse(input)).AsTask();
        Check(await Observe(original, "typed malformed arguments") is JsonException && calls == 0);
    }
    var policy = new Policy(); var binding = new ExtensionAgentBinding(registry, policy, (_, _, _) => ValueTask.FromResult(false));
    var invalid = binding.Tools.Single().Executor.ExecuteAsync(Call("invalid", Input("value")), CancellationToken.None).AsTask();
    var result = await Join(invalid, "prepared invalid argument result");
    Check(result.Failure?.Kind == ToolFailureKind.InvalidArguments && policy.Calls == 0 && calls == 0);
    var denied = new ExtensionAgentBinding(registry, new Policy(false), Validate);
    var deniedOriginal = denied.Tools.Single().Executor.ExecuteAsync(Call("denied", Input("value")), CancellationToken.None).AsTask();
    Check((await Join(deniedOriginal, "prepared denied result")).Failure?.Kind == ToolFailureKind.Blocked && calls == 0);
}

static async Task CancellationDrain()
{
    await using var registry = new ExtensionRegistry(); using var cancellation = new CancellationTokenSource();
    var entered = Gate(); var cleanupEntered = Gate(); var releaseCleanup = Gate(); var callbackSettled = false; var disposals = 0;
    Task<ExtensionTypedToolResult<Details>>? callbackOriginal = null;
    async Task<ExtensionTypedToolResult<Details>> Body(CancellationToken token)
    {
        try { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Result("never", "id"); }
        finally { cleanupEntered.TrySetResult(); await releaseCleanup.Task; callbackSettled = true; }
    }
    var scope = await registry.ActivateAsync("typed-owner", new Probe(Define((_, _, token) =>
    { callbackOriginal = Body(token); return new(callbackOriginal); }), () => { Check(callbackSettled); disposals++; return ValueTask.CompletedTask; }));
    var original = registry.InvokeToolAsync(registry.CaptureSnapshot(), "typed_tool", Input("value"), cancellation.Token).AsTask();
    Task? closeOriginal = null; Exception? invocationError = null;
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)); closeOriginal = scope.DisposeAsync().AsTask();
        Check(!original.IsCompleted && !closeOriginal.IsCompleted && !callbackOriginal!.IsCompleted && disposals == 0);
    }
    finally
    {
        cancellation.Cancel(); releaseCleanup.TrySetResult();
        invocationError = await Observe(original, "canceled registry invocation");
        if (callbackOriginal is not null) _ = await Observe(callbackOriginal, "original typed canceled callback");
        closeOriginal ??= scope.DisposeAsync().AsTask(); await JoinVoid(closeOriginal, "owner close after typed cancellation");
    }
    Check(invocationError is OperationCanceledException && callbackOriginal!.IsCanceled && callbackSettled && disposals == 1);
}

static async Task Quiescence()
{
    await using var registry = new ExtensionRegistry(); var entered = Gate(); var release = Gate(); var calls = 0;
    Task<ExtensionTypedToolResult<Details>>? callbackOriginal = null;
    async Task<ExtensionTypedToolResult<Details>> Body() { calls++; entered.TrySetResult(); await release.Task; return Result("done", "id"); }
    await using var scope = await registry.ActivateAsync("typed-owner", new Probe(Define((_, _, _) =>
    { callbackOriginal = Body(); return new(callbackOriginal); })));
    var captured = registry.CaptureSnapshot();
    var original = registry.InvokeToolAsync(captured, "typed_tool", Input("value")).AsTask();
    Task<RegistrationQuiescenceLease>? pauseOriginal = null; RegistrationQuiescenceLease? lease = null;
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); pauseOriginal = scope.QuiesceAsync().AsTask();
        Check(!pauseOriginal.IsCompleted && !original.IsCompleted);
        var denied = registry.InvokeToolAsync(captured, "typed_tool", Input("value")).AsTask();
        Check(await Observe(denied, "typed dispatch while quiescing") is ExtensionRegistrationException && calls == 1);
    }
    finally
    {
        release.TrySetResult(); await Join(original, "held typed registry invocation");
        if (callbackOriginal is not null) await Join(callbackOriginal, "original held typed callback");
        if (pauseOriginal is not null) lease = await Join(pauseOriginal, "typed quiescence");
        lease?.Dispose();
    }
    var resumed = registry.InvokeToolAsync(registry.CaptureSnapshot(), "typed_tool", Input("value")).AsTask();
    await Join(resumed, "resumed typed invocation");
    await Join(callbackOriginal!, "original resumed typed callback"); Check(calls == 2);
}

static async Task FaultIdentity()
{
    try { _ = new ExtensionTypedToolResult<ConverterDetails>(JsonData.Parse("[]"), new()).ToJson(Metadata.Default.ConverterDetails); throw new InvalidOperationException("Converter fault lost."); }
    catch (Exception error) when (ReferenceEquals(error, FaultingConverter.Fault)) { }
    foreach (var faultedCancellation in new[] { false, true })
    {
        await using var registry = new ExtensionRegistry();
        Exception first = faultedCancellation ? new OperationCanceledException("Faulted callback with clear token", CancellationToken.None) : new InvalidOperationException("First original cause");
        var second = new ArgumentException("Second original cause");
        var source = new TaskCompletionSource<ExtensionTypedToolResult<Details>>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetException(faultedCancellation ? new[] { first } : new[] { first, second });
        var callbackOriginal = source.Task;
        await using var scope = await registry.ActivateAsync("typed-owner", new Probe(Define((_, _, _) => new(callbackOriginal))));
        var invocation = registry.InvokeToolAsync(registry.CaptureSnapshot(), "typed_tool", Input("value")).AsTask();
        var caught = await Observe(invocation, faultedCancellation ? "faulted OCE becomes native canceled invocation" : "multiple original faults preserve await-selected identity");
        var callbackCaught = await Observe(callbackOriginal, faultedCancellation ? "original faulted OCE callback" : "original two-cause callback full DAG");
        Check(callbackOriginal.IsFaulted && ReferenceEquals(callbackCaught, first));
        Check(callbackOriginal.Exception!.InnerExceptions.Count == (faultedCancellation ? 1 : 2));
        if (faultedCancellation) Check(invocation.IsCanceled && caught is OperationCanceledException);
        else Check(ReferenceEquals(caught, first) && ReferenceEquals(callbackOriginal.Exception.InnerExceptions[1], second));
    }
    foreach (var synchronous in new[] { false, true })
    {
        await using var registry = new ExtensionRegistry();
        var shared = new InvalidOperationException("same shared leaf");
        var fault = new AggregateException("typed original", shared, new AggregateException("nested", shared, new ArgumentException("second")));
        Task<ExtensionTypedToolResult<Details>>? callbackOriginal = null;
        await using var scope = await registry.ActivateAsync("typed-owner", new Probe(Define((_, _, _) =>
        {
            if (synchronous) throw fault;
            callbackOriginal = Task.FromException<ExtensionTypedToolResult<Details>>(fault); return new(callbackOriginal);
        })));
        var invocation = registry.InvokeToolAsync(registry.CaptureSnapshot(), "typed_tool", Input("value")).AsTask();
        var caught = await Observe(invocation, synchronous ? "synchronous typed fault" : "typed aggregate invocation fault");
        Check(ReferenceEquals(caught, fault));
        if (callbackOriginal is not null)
        { var callbackCaught = await Observe(callbackOriginal, "original typed aggregate callback"); Check(ReferenceEquals(callbackCaught, fault)); }
        Check(ReferenceEquals(fault.InnerExceptions[0], ((AggregateException)fault.InnerExceptions[1]).InnerExceptions[0]));
    }
}

static async Task ResultLimits()
{
    try { _ = Result(new string('x', 8192), "id").ToJson(Metadata.Default.Details, new(MaximumUtf8Bytes: 512)); throw new InvalidOperationException("Unbounded content accepted."); }
    catch (JsonException) { }
    try { _ = Result("text", new string('x', 8192)).ToJson(Metadata.Default.Details, new(MaximumUtf8Bytes: 512)); throw new InvalidOperationException("Unbounded details accepted."); }
    catch (JsonException) { }
    try { _ = (Result("text", "id") with { Usage = JsonData.Parse("{\"v\":\"" + new string('x', 8192) + "\"}") }).ToJson(Metadata.Default.Details, new(MaximumUtf8Bytes: 512)); throw new InvalidOperationException("Unbounded optional metadata accepted."); }
    catch (JsonException) { }
    try { _ = (Result("text", "id") with { StructuredContent = JsonData.Parse("{\"a\":{\"b\":{\"c\":1}}}") }).ToJson(Metadata.Default.Details, new(MaximumJsonDepth: 2)); throw new InvalidOperationException("Deep metadata accepted."); }
    catch (Exception error) when ((error is JsonException or InvalidOperationException) && error.Message != "Deep metadata accepted.") { }
    await using var registry = new ExtensionRegistry(new() { MaximumJsonCharacters = 256 });
    await using var scope = await registry.ActivateAsync("typed-owner", new Probe(Define((_, _, _) => ValueTask.FromResult(Result(new string('x', 512), "id")))));
    var invocation = registry.InvokeToolAsync(registry.CaptureSnapshot(), "typed_tool", Input("value")).AsTask();
    Check(await Observe(invocation, "typed oversized result") is ExtensionRegistrationException { Failure: ExtensionRegistrationFailure.InvalidDescriptor });
    await using var updateRegistry = new ExtensionRegistry(); var deliveries = 0;
    await using var updateScope = await updateRegistry.ActivateAsync("typed-owner", new Probe(Define(async (_, context, token) =>
    {
        await ((IExtensionToolInvocationContext)context).ReportUpdateAsync(Result(new string('x', 512), "id"), Metadata.Default.Details, token);
        return Result("unused", "id");
    })));
    var binding = new ExtensionAgentBinding(updateRegistry, new Policy(), Validate, options: new() { ResultValues = new(MaximumCharacters: 64) });
    var original = binding.Tools.Single().Executor.ExecuteAsync(Call("bounded", Input("value")),
        (_, _) => { deliveries++; return ValueTask.CompletedTask; }, CancellationToken.None).AsTask();
    Check((await Join(original, "typed oversized update outcome")).Failure?.Kind == ToolFailureKind.ExecutionError && deliveries == 0);
}

static async Task SingleConsumption()
{
    await using var registry = new ExtensionRegistry(); var source = new SingleUseResult();
    await using var scope = await registry.ActivateAsync("typed-owner", new Probe(Define((_, _, _) => source.Original())));
    var original = registry.InvokeToolAsync(registry.CaptureSnapshot(), "typed_tool", Input("value")).AsTask();
    var result = await Join(original, "single consume typed invocation");
    Check(source.Reads == 1 && result.Value.GetProperty("details").GetProperty("id").GetString() == "single");
}

static ExtensionToolDescriptor Define(ExtensionTypedToolCallback<Arguments, Details> callback) =>
    ExtensionTypedTool.Define("typed-registration", "typed_tool", "Typed authored probe", Schema(), Metadata.Default.Arguments, Metadata.Default.Details, callback);
static JsonData Schema() => Data.Schema;
static JsonData Input(string value) => JsonData.Parse("{\"value\":" + JsonSerializer.Serialize(value, Metadata.Default.String) + "}");
static ExtensionTypedToolResult<Details> Result(string text, string id) => new(
    JsonData.Parse("[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text, Metadata.Default.String) + "}]"), new(id, null));
static ToolInvocation Call(string id, JsonData input)
{
    var call = new ToolCallContent(id, "typed_tool", input);
    var message = new AssistantMessage("openai-responses", "authored-provider", "authored-model", 1, [call], TokenUsage.Zero, StopReason.ToolUse);
    return new(message, call, 0);
}
static ValueTask<bool> Validate(ExtensionToolRegistrationInfo tool, JsonData input, CancellationToken token)
{
    token.ThrowIfCancellationRequested();
    return ValueTask.FromResult(ReferenceEquals(tool.Parameters, Schema()) && input.Value.ValueKind == JsonValueKind.Object &&
        input.Value.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String &&
        input.Value.EnumerateObject().All(property => property.Name is "value" or "keep"));
}
static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static void Check(bool value) { if (!value) throw new InvalidOperationException("Typed SDK control failed."); }
static async Task<Exception?> Observe(Task original, string name)
{
    Exception? caught = null; try { await original; } catch (Exception error) { caught = error; }
    Audit.Record(name, original, caught); return caught;
}
static async Task<T> Join<T>(Task<T> original, string name)
{
    Exception? caught = null;
    try { return await original; } catch (Exception error) { caught = error; throw; }
    finally { Audit.Record(name, original, caught); }
}
static async Task JoinVoid(Task original, string name)
{
    Exception? caught = null;
    try { await original; } catch (Exception error) { caught = error; throw; }
    finally { Audit.Record(name, original, caught); }
}

static class Data { internal static readonly JsonData Schema = JsonData.Parse("{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"},\"keep\":{\"type\":\"null\"}},\"required\":[\"value\"],\"additionalProperties\":false}"); }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
sealed record Arguments([property: JsonRequired, JsonPropertyName("value")] string Value, [property: JsonPropertyName("keep")] string? Keep);
sealed record Details([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("optional")] string? Optional);
[JsonConverter(typeof(FaultingConverter))]
sealed class ConverterDetails { }
sealed class FaultingConverter : JsonConverter<ConverterDetails>
{
    internal static readonly ArgumentException Fault = new("Explicit converter identity");
    public override ConverterDetails Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw Fault;
    public override void Write(Utf8JsonWriter writer, ConverterDetails value, JsonSerializerOptions options) => throw Fault;
}
sealed class Probe(ExtensionToolDescriptor descriptor, Func<ValueTask>? dispose = null) : IPiSharpExtension
{
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) { registry.RegisterTool(descriptor); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() => dispose?.Invoke() ?? ValueTask.CompletedTask;
}
sealed class Policy(bool allow = true) : IToolActionPolicy
{
    public int Calls; public JsonData? Arguments;
    public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Calls++; Arguments = action.Arguments; return ValueTask.FromResult(new ToolActionAuthorization(allow)); }
}
sealed class SingleUseResult : System.Threading.Tasks.Sources.IValueTaskSource<ExtensionTypedToolResult<Details>>
{
    public int Reads;
    public ValueTask<ExtensionTypedToolResult<Details>> Original() => new(this, 0);
    public ExtensionTypedToolResult<Details> GetResult(short token)
    {
        if (token != 0 || ++Reads != 1) throw new InvalidOperationException("Original consumed twice.");
        return new(JsonData.Parse("[]"), new("single", null));
    }
    public System.Threading.Tasks.Sources.ValueTaskSourceStatus GetStatus(short token) => System.Threading.Tasks.Sources.ValueTaskSourceStatus.Succeeded;
    public void OnCompleted(Action<object?> continuation, object? state, short token, System.Threading.Tasks.Sources.ValueTaskSourceOnCompletedFlags flags) => throw new InvalidOperationException("Completed source scheduled unexpectedly.");
}
sealed record FaultNode(int Index, string? Type, string Message, int HResult, string? StackTrace, bool Aggregate, int[] Inner, bool CancellationException, bool CancellationRequested);
sealed record FaultDag(int? OriginalAggregateRoot, int? CaughtRoot, FaultNode[] Nodes);
sealed record TaskRow(string Name, bool OriginalCaptured, bool OriginalJoined, string? Status, bool Canceled, bool Faulted, bool? Passed, FaultDag Faults);
sealed record Report(string Profile, string OriginalCommit, int Expected, int Executed, int Passed, int Failed, TaskRow[] Tasks);
static class Audit
{
    public static readonly List<TaskRow> Rows = [];
    public static void Record(string name, Task? original, Exception? caught, bool? passed = null)
    {
        var indices = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance); var nodes = new List<FaultNode>();
        int? Visit(Exception? error)
        {
            if (error is null) return null;
            if (indices.TryGetValue(error, out var existing)) return existing;
            var index = nodes.Count; indices.Add(error, index); nodes.Add(null!);
            var inner = error is AggregateException aggregate ? aggregate.InnerExceptions.Select(item => Visit(item)!.Value).ToArray()
                : error.InnerException is { } nested ? new[] { Visit(nested)!.Value } : [];
            nodes[index] = new(index, error.GetType().AssemblyQualifiedName, error.Message, error.HResult, error.StackTrace,
                error is AggregateException, inner, error is OperationCanceledException,
                error is OperationCanceledException cancellation && cancellation.CancellationToken.IsCancellationRequested);
            return index;
        }
        var aggregateRoot = Visit(original?.Exception); var caughtRoot = Visit(caught);
        Rows.Add(new(name, original is not null, original?.IsCompleted ?? false, original?.Status.ToString(),
            original?.IsCanceled ?? false, original?.IsFaulted ?? false, passed, new(aggregateRoot, caughtRoot, nodes.ToArray())));
    }
}
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Arguments))]
[JsonSerializable(typeof(Details))]
[JsonSerializable(typeof(ConverterDetails))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Report))]
partial class Metadata : JsonSerializerContext { }
