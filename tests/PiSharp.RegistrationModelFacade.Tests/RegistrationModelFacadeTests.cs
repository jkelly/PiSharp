using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Expected --report <fresh-path>.");
var cases = new (string Name, Func<Task> Run)[] {
    ("staged first default replacement and explicit host override", Defaults),
    ("invalid definition precedes native metadata admission", Validation),
    ("failed real initializer rolls back pending defaults", FailedInitializer),
    ("foreign owners removal and generation identity", Ownership),
    ("held real callback owner close joins original finally", HeldClose),
    ("held operation cancellation joins original multi fault inventory", HeldFault)
}.Concat(ProviderModelMessageTests.Cases()).ToArray();
var failed = 0;
foreach (var item in cases)
{
    Task? original = null; Exception? caught = null;
    try { original = item.Run(); await original; }
    catch (Exception error) { caught = error; failed++; }
    Audit.Record(item.Name, original, caught, caught is null);
}
var report = new Report("owner-bound-flags-providers-models-messages", "d86654abb8862e201933517d6f1fce9f88dd117f", cases.Length, cases.Length - failed, failed, Audit.Rows.ToArray());
try
{
    var json = JsonSerializer.Serialize(report, Metadata.Default.Report);
    if (args.Length != 0)
    {
        FileStream? file = null; var outputFailures = new List<Exception>();
        try { file = new FileStream(Path.GetFullPath(args[1]), FileMode.CreateNew, FileAccess.Write, FileShare.Read); var bytes = System.Text.Encoding.UTF8.GetBytes(json); file.Write(bytes, 0, bytes.Length); }
        catch (Exception error) { outputFailures.Add(error); }
        finally { try { file?.Dispose(); } catch (Exception error) { outputFailures.Add(error); } }
        if (outputFailures.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outputFailures[0]).Throw();
        if (outputFailures.Count > 1) throw new AggregateException("Report write and disposal failed.", outputFailures);
    }
    Console.WriteLine(json);
}
catch (Exception writerFailure)
{
    if (Audit.Failures.Count != 0) throw new AggregateException("Original controls and report output failed.", Audit.Failures.Append(writerFailure));
    throw;
}
return failed == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static async Task Join(string name, Task original)
{
    Exception? caught = null;
    try { await original; } catch (Exception error) { caught = error; throw; }
    finally { Audit.Record(name, original, caught); }
}
static async Task<RegistrationScope> Activate(ExtensionRegistry registry, string name, Extension extension)
{
    var original = registry.ActivateAsync(name, extension); Exception? caught = null;
    try { return await original; } catch (Exception error) { caught = error; throw; }
    finally { Audit.Record("activate-" + name, original, caught); }
}
static async Task Defaults()
{
    await using var registry = new ExtensionRegistry();
    var values = new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue> { ["override"] = ExtensionFlagValue.Boolean(false), ["hidden"] = ExtensionFlagValue.String("host") });
    using var host = new ExtensionFlagRegistrationHost(registry, values);
    IExtensionFlagRegistrationFacade? facade = null;
    var extension = new Extension(owner => {
        facade = host.Bind(owner);
        var first = facade.RegisterFlag("mode", new(ExtensionFlagKind.String, "first", ExtensionFlagValue.String("first")));
        var second = facade.RegisterFlag("mode", new(ExtensionFlagKind.String, "replacement", ExtensionFlagValue.String("second")));
        Check(ReferenceEquals(first, second), "Replacement must retain its actual native marker.");
        facade.RegisterFlag("override", new(ExtensionFlagKind.Boolean, DefaultValue: ExtensionFlagValue.Boolean(true)));
        facade.RegisterFlag("absent", new(ExtensionFlagKind.String));
        Check(facade.GetFlag("mode")?.StringValue == "first", "First pending default lost.");
        Check(facade.GetFlag("override")?.BooleanValue == false, "False override treated as missing.");
        Check(facade.GetFlag("hidden") is null && facade.GetFlag("absent") is null, "Absent or unregistered value became visible.");
        Check(host.CaptureFlags().IsEmpty && !values.TryGetValue("mode", out _), "Staged definition leaked before activation.");
        return ValueTask.CompletedTask;
    });
    var scope = await Activate(registry, "defaults", extension);
    facade!.RegisterFlag("mode", new(ExtensionFlagKind.String, "replacement", ExtensionFlagValue.String("third")));
    host.CommitOwnerFlags(scope); host.CommitOwnerFlags(scope);
    Check(host.CaptureFlags().Length == 3 && host.CaptureFlags().Single(x => x.Name == "mode").Options.Description == "replacement", "Published definitions mismatch.");
    Check(values.TryGetValue("mode", out var value) && value?.StringValue == "first", "Pending default not committed.");
    values.SetHostValue("mode", ExtensionFlagValue.String("override"));
    Check(facade!.GetFlag("mode")?.StringValue == "override", "Host value must remain authoritative.");
    facade!.RegisterFlag("late", new(ExtensionFlagKind.Boolean, DefaultValue: ExtensionFlagValue.Boolean(false)));
    Check(values.TryGetValue("late", out var late) && late?.BooleanValue == false, "Active registration did not commit its default.");
    var close = scope.DisposeAsync().AsTask(); await Join("defaults-close", close);
    Check(host.CaptureFlags().IsEmpty, "Closed owner metadata retained.");
    Throws<ObjectDisposedException>(() => facade!.GetFlag("mode"));
}
static async Task Validation()
{
    await using var registry = new ExtensionRegistry();
    var values = new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>());
    using var host = new ExtensionFlagRegistrationHost(registry, values);
    var scope = await Activate(registry, "validation", new Extension(owner => {
        var facade = host.Bind(owner);
        Throws<ArgumentException>(() => facade.RegisterFlag("bad", new(ExtensionFlagKind.Boolean, DefaultValue: ExtensionFlagValue.String("bad"))));
        Throws<ArgumentException>(() => facade.RegisterFlag(new string('x', 129), new(ExtensionFlagKind.String)));
        Throws<ArgumentException>(() => facade.RegisterFlag("huge", new(ExtensionFlagKind.String, DefaultValue: ExtensionFlagValue.String(new string('x', 4097)))));
        Throws<ArgumentException>(() => facade.RegisterFlag("enum", new((ExtensionFlagKind)99)));
        return ValueTask.CompletedTask;
    }));
    host.CommitOwnerFlags(scope);
    Check(registry.CaptureSnapshot().Registrations.IsEmpty && host.CaptureFlags().IsEmpty, "Invalid flag admitted a marker.");
    var close = scope.DisposeAsync().AsTask(); await Join("validation-close", close);
    await LegacyEventMessageRegression();
}
static async Task LegacyEventMessageRegression()
{
    // Preserve the existing experimental event reducer API alongside the distinct registration message API.
    var message = new PiSharp.Extensions.Events.ExtensionCustomMessage("legacy-before", true,
        JsonData.Parse("\"legacy-event-body\""), JsonData.Parse("{\"legacy\":true}"));
    var callbackOriginal = Task.FromResult<PiSharp.Extensions.Events.ExtensionBeforeAgentStartPatch?>(new(message));
    var admissions = 0;
    var dispatcher = new PiSharp.Extensions.Runtime.Dispatch.ExtensionEventDispatcher(_ => { },
        admitContextMessages: messages => {
            PiSharp.Agent.AgentLoopRunner.ValidateRequestMessages(messages);
            Check(messages.Length == 1 && messages[0].Role == "custom" &&
                messages[0].WireBody.Value.GetProperty("customType").GetString() == message.CustomType &&
                messages[0].WireBody.Value.GetProperty("content").GetString() == "legacy-event-body",
                "Legacy event was not admitted as its actual custom transcript.");
            admissions++;
        });
    var calls = 0;
    using var registration = dispatcher.BeforeAgentStartHandlers.Register("legacy-events", 1, "legacy-before",
        (_, _, _) => { calls++; return new(callbackOriginal); });
    var dispatchOriginal = dispatcher.DispatchBeforeAgentStartAsync(dispatcher.BeforeAgentStartHandlers.CaptureSnapshot(),
        new("prompt", "system")).AsTask();
    try { await Join("legacy-event-dispatch-original", dispatchOriginal); }
    finally { await Join("legacy-event-callback-original", callbackOriginal); }
    var result = dispatchOriginal.Result;
    Check(calls == 1 && admissions == 1 && result.Diagnostics.IsEmpty && result.Messages.Length == 1 &&
        ReferenceEquals(result.Messages[0], message), "Legacy event message contract changed after registration API addition.");
}
static async Task FailedInitializer()
{
    await using var registry = new ExtensionRegistry();
    var values = new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>());
    using var host = new ExtensionFlagRegistrationHost(registry, values);
    var fault = new InvalidOperationException("original initializer fault");
    IExtensionFlagRegistrationFacade? facade = null;
    var originalCallback = Task.FromException(fault);
    var extension = new Extension(async owner => {
        facade = host.Bind(owner); facade.RegisterFlag("pending", new(ExtensionFlagKind.String, DefaultValue: ExtensionFlagValue.String("never")));
        await Join("initializer-original", originalCallback);
    });
    Exception? caught = null;
    try { await Activate(registry, "failed", extension); } catch (Exception error) { caught = error; }
    Check(caught is ExtensionRegistrationException && Contains(caught, fault), "Native initializer fault identity lost.");
    Check(extension.Disposed && host.CaptureFlags().IsEmpty && registry.CaptureSnapshot().Registrations.IsEmpty && !values.TryGetValue("pending", out _), "Failed owner leaked pending values or metadata.");
    Throws<ObjectDisposedException>(() => facade!.GetFlag("pending"));
}
static async Task Ownership()
{
    await using var registry = new ExtensionRegistry(); await using var foreign = new ExtensionRegistry();
    var values = new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>());
    using var host = new ExtensionFlagRegistrationHost(registry, values);
    var other = await Activate(foreign, "foreign", new Extension(_ => ValueTask.CompletedTask));
    Throws<ArgumentException>(() => host.Bind(other));
    IExtensionRegistration? stale = null; IExtensionFlagRegistrationFacade? facade = null;
    var first = await Activate(registry, "owner", new Extension(owner => {
        facade = host.Bind(owner); stale = facade.RegisterFlag("same", new(ExtensionFlagKind.String)); return ValueTask.CompletedTask;
    }));
    host.CommitOwnerFlags(first); stale!.Dispose();
    Check(host.CaptureFlags().IsEmpty && facade!.GetFlag("same") is null, "Handle did not remove actual metadata.");
    var firstClose = first.DisposeAsync().AsTask(); await Join("first-generation-close", firstClose);
    var second = await Activate(registry, "owner", new Extension(owner => { host.Bind(owner).RegisterFlag("same", new(ExtensionFlagKind.String)); return ValueTask.CompletedTask; }));
    host.CommitOwnerFlags(second); stale!.Dispose();
    Check(second.OwnerGeneration > first.OwnerGeneration && host.CaptureFlags().Single().OwnerGeneration == second.OwnerGeneration, "Stale handle removed new generation.");
    var secondClose = second.DisposeAsync().AsTask(); await Join("second-generation-close", secondClose);
    var otherClose = other.DisposeAsync().AsTask(); await Join("foreign-close", otherClose);
}
static Task HeldClose() => Held(closeOwner: true);
static Task HeldFault() => Held(closeOwner: false);
static async Task Held(bool closeOwner)
{
    await using var registry = new ExtensionRegistry();
    using var operation = new CancellationTokenSource();
    using var host = new ExtensionFlagRegistrationHost(registry, new ExtensionHostFlagValues(new Dictionary<string, ExtensionFlagValue>()));
    var entered = Signal(); var released = Signal(); var finished = Signal(); var cancelled = Signal();
    var originalCallback = released.Task;
    IExtensionFlagRegistrationFacade? facade = null;
    var extension = new Extension(owner => {
        var bound = host.Bind(owner); facade = bound;
        bound.RegisterFlag("held", new(ExtensionFlagKind.Boolean, DefaultValue: ExtensionFlagValue.Boolean(true)));
        owner.Observe(new("user-held", "user-held", async (_, context, token) => {
            using var registration = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            try {
                Check(context.OwnerId == bound.OwnerId && context.OwnerGeneration == bound.OwnerGeneration && bound.GetFlag("held")?.BooleanValue == true, "Actual callback owner/value mismatch.");
                await Join("held-original-callback", originalCallback);
            }
            finally { finished.TrySetResult(); }
        })); return ValueTask.CompletedTask;
    });
    var scope = await Activate(registry, closeOwner ? "held-close" : "held-cancel", extension); host.CommitOwnerFlags(scope);
    var originalDispatch = registry.DispatchObservationsAsync(registry.CaptureSnapshot(), "user-held", JsonData.Parse("{}"), operation.Token).AsTask();
    Task? originalClose = null; Exception? caught = null; var dispatchJoined = false;
    var first = new InvalidOperationException("callback-first"); var second = new ArgumentException("callback-second");
    try
    {
        await Join("held-entered", entered.Task);
        Check(!originalDispatch.IsCompleted, "Callback failed before the held boundary.");
        if (closeOwner) originalClose = scope.DisposeAsync().AsTask(); else operation.Cancel();
        await Join("held-cancellation-observed", cancelled.Task);
        Check(!originalDispatch.IsCompleted && (originalClose is null || !originalClose.IsCompleted) && !extension.Disposed, "Cancellation detached original callback cleanup.");
        if (closeOwner) Throws<ObjectDisposedException>(() => facade!.GetFlag("held"));
        else Check(facade!.GetFlag("held")?.BooleanValue == true, "Operation cancellation retired the owner.");
        if (closeOwner) released.TrySetResult(); else released.TrySetException(new Exception[] { first, second });
        try { dispatchJoined = true; await Join("held-original-dispatch", originalDispatch); } catch (Exception error) { caught = error; }
        await Join("held-finally", finished.Task);
        var originalAggregate = originalCallback.Exception;
        if (!closeOwner) Check(ReferenceEquals(caught, first) && originalCallback.IsFaulted && originalAggregate is { InnerExceptions.Count: 2 } && ReferenceEquals(originalAggregate.InnerExceptions[1], second), "Original multi fault inventory or await-selected identity lost.");
        else Check(caught is null, "Held close changed successful raw callback semantics.");
    }
    finally
    {
        released.TrySetResult();
        if (!dispatchJoined) { try { await Join("held-dispatch-emergency-join", originalDispatch); } catch { } }
        originalClose ??= scope.DisposeAsync().AsTask(); await Join("held-owner-close", originalClose);
    }
    Check(extension.Disposed && host.CaptureFlags().IsEmpty, "Real owner cleanup did not retire flag metadata.");
}
static bool Contains(Exception? root, Exception target)
{
    var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
    bool Visit(Exception? item) => item is not null && (ReferenceEquals(item, target) || (seen.Add(item) && (item is AggregateException aggregate ? aggregate.InnerExceptions.Any(Visit) : Visit(item.InnerException))));
    return Visit(root);
}
sealed class Extension(Func<IExtensionRegistry, ValueTask> initialize) : IPiSharpExtension
{
    public bool Disposed { get; private set; }
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => initialize(registry);
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}
sealed record FaultNode(int Index, string? Type, string Message, int HResult, string? StackTrace, bool Aggregate, int[] Inner, bool CancellationException, bool CancellationRequested);
sealed record FaultDag(int? OriginalAggregateRoot, int? CaughtRoot, FaultNode[] Nodes, bool Truncated);
sealed record TaskRow(string Name, bool OriginalCaptured, bool OriginalJoined, string? Status, bool Canceled, bool Faulted, bool? Passed, FaultDag Faults);
sealed record Report(string Profile, string OriginalCommit, int Expected, int Passed, int Failed, TaskRow[] Tasks);
sealed record RawTaskEvidence(string Name, Task? Original, AggregateException? Inventory, Exception? Observed);
sealed class AuditMetadataException : AggregateException
{
    public RawTaskEvidence[] Originals { get; }
    public AuditMetadataException(IEnumerable<Exception> failures, RawTaskEvidence[] originals)
        : base("Original controls and audit metadata failed.", failures) => Originals = originals;
}
static class Audit
{
    public static readonly List<TaskRow> Rows = [];
    public static readonly List<Exception> Failures = [];
    public static readonly List<RawTaskEvidence> Originals = [];
    public static void Record(string name, Task? original, Exception? caught, bool? passed = null)
    {
        var originalAggregate = original?.Exception;
        Originals.Add(new(name, original, originalAggregate, caught));
        if (originalAggregate is not null && !Failures.Any(error => ReferenceEquals(error, originalAggregate))) Failures.Add(originalAggregate);
        if (caught is not null && !Failures.Any(error => ReferenceEquals(error, caught))) Failures.Add(caught);
        try { RecordMetadata(name, original, originalAggregate, caught, passed); }
        catch (Exception metadataFailure)
        {
            throw new AuditMetadataException(Failures.Append(metadataFailure), Originals.ToArray());
        }
    }
    private static void RecordMetadata(string name, Task? original, AggregateException? originalAggregate, Exception? caught, bool? passed)
    {
        var indices = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance); var queue = new List<Exception>(); var nodes = new List<FaultNode>(); var truncated = false; var edges = 0;
        int? Add(Exception? error)
        {
            if (error is null) return null;
            if (indices.TryGetValue(error, out var existing)) return existing;
            if (queue.Count == 1024) { truncated = true; return null; }
            var index = queue.Count; indices.Add(error, index); queue.Add(error);
            return index;
        }
        var aggregateRoot = Add(originalAggregate); var caughtRoot = Add(caught);
        for (var index = 0; index < queue.Count; index++)
        {
            var error = queue[index]; IEnumerable<Exception> children = error is AggregateException aggregate ? aggregate.InnerExceptions : error.InnerException is { } inner ? [inner] : [];
            var links = new List<int>();
            foreach (var child in children) { if (edges == 4096) { truncated = true; break; } edges++; if (Add(child) is { } childIndex) links.Add(childIndex); }
            nodes.Add(new(index, error.GetType().AssemblyQualifiedName, error.Message, error.HResult, error.StackTrace, error is AggregateException, links.ToArray(), error is OperationCanceledException, error is OperationCanceledException cancellation && cancellation.CancellationToken.IsCancellationRequested));
        }
        Rows.Add(new(name, original is not null, original?.IsCompleted ?? false, original?.Status.ToString(), original?.IsCanceled ?? false, original?.IsFaulted ?? false, passed, new(aggregateRoot, caughtRoot, nodes.ToArray(), truncated)));
    }
}
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata, WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Report))]
partial class Metadata : JsonSerializerContext { }
