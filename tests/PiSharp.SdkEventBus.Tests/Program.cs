using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using System.Text.Json;

if (args.Length != 0 && (args.Length != 2 || args[0] != "--report"))
    throw new ArgumentException("Expected --report <new-path> or no arguments");
string? reportPath = args.Length == 2 ? args[1] : null;

var cases = new (string Name, Func<Task> Run)[]
{
    ("ordered synchronous delivery preserves payload identity and null", OrderAndPayload),
    ("unsubscription and additions use the current emission snapshot", Snapshot),
    ("duplicate subscriptions have independent idempotent handles", DuplicateHandles),
    ("listener and diagnostic faults cannot stop later listeners", FaultIsolation),
    ("clear and recursive emission capture current listeners", ClearAndReentry),
    ("owner bus is shared and isolated between host registries", SharedHost),
    ("owner disposal removes subscriptions and rejects stale bus", OwnerLifetime),
    ("failed initialization removes loading subscriptions", FailedInitialization),
    ("owner close joins a held synchronous listener before plugin cleanup", HeldClose),
    ("captured emitter listener cannot enter after owner retirement", StaleCapturedListener),
    ("quiescence joins held listeners denies new delivery and resumes", HeldQuiescence),
    ("nested listener frames reject same ancestor and registry lifecycle reentry", NestedReentry),
    ("retired held listener remains charged until its original callback returns", RetiredCharge),
    ("loading listeners stage with initialization and stay out of public catalogs", InitializationPublication)
};
var failures = 0;
var evidence = new List<object>();
for (var ordinal = 0; ordinal < cases.Length; ordinal++)
{
    var test = cases[ordinal];
    Task? original = null;
    Exception? caught = null;
    var synchronousFailure = false;
    try { original = test.Run() ?? throw new InvalidOperationException("Test returned no original Task"); }
    catch (Exception error) { caught = error; synchronousFailure = true; }
    if (original is not null)
    {
        // Capture once and directly await the actual original, with no proxy or timeout wrapper.
        try { await original; }
        catch (Exception error) { caught = error; }
    }
    var passed = !synchronousFailure && original?.Status == TaskStatus.RanToCompletion && caught is null;
    if (passed) Console.WriteLine($"PASS {test.Name}");
    else { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {original?.Exception ?? caught}"); }
    evidence.Add(new
    {
        ordinal, name = test.Name, method = test.Run.Method.Name,
        status = passed ? "PASS" : "FAIL", originalInvoked = true,
        originalCaptured = original is not null, originalTaskJoined = original is not null,
        originalStatus = original?.Status.ToString(), actuallyCanceled = original?.IsCanceled ?? false,
        faulted = original?.IsFaulted ?? false, synchronousFailure,
        faults = CaptureFaults(original?.Exception, caught)
    });
}
Console.WriteLine($"{cases.Length - failures}/{cases.Length} SDK event bus controls passed");
if (reportPath is not null)
{
    using var stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await JsonSerializer.SerializeAsync(stream, new
    {
        schemaVersion = 1, profile = "native-sdk-event-bus-14-owned-originals",
        originalOracle = "d86654abb8862e201933517d6f1fce9f88dd117f",
        expected = cases.Length, executed = evidence.Count, passed = cases.Length - failures, failed = failures,
        tests = evidence, directOriginalAwait = true, noNode = true
    }, new JsonSerializerOptions { WriteIndented = true });
}
return failures == 0 ? 0 : 1;

static object CaptureFaults(Exception? originalAggregate, Exception? awaitOrSynchronousFailure)
{
    var identities = new Dictionary<Exception, int>(ReferenceEqualityComparer.Instance);
    var nodes = new List<object>();
    int? Visit(Exception? error)
    {
        if (error is null) return null;
        if (identities.TryGetValue(error, out var retained)) return retained;
        var index = nodes.Count; identities.Add(error, index); nodes.Add(new { pending = true });
        var children = error is AggregateException aggregate
            ? aggregate.InnerExceptions.Select(item => Visit(item)!.Value).ToArray()
            : error.InnerException is { } inner ? new[] { Visit(inner)!.Value } : [];
        nodes[index] = new
        {
            index, type = error.GetType().AssemblyQualifiedName, error.Message, error.HResult, error.StackTrace,
            aggregate = error is AggregateException, inner = children,
            cancellationException = error is OperationCanceledException,
            cancellationRequested = error is OperationCanceledException canceled && canceled.CancellationToken.IsCancellationRequested
        };
        return index;
    }
    var aggregateRoot = Visit(originalAggregate); var caughtRoot = Visit(awaitOrSynchronousFailure);
    return new { originalAggregateRoot = aggregateRoot, awaitOrSynchronousRoot = caughtRoot, nodes };
}

static void Require(bool value) { if (!value) throw new InvalidOperationException("Control failed"); }
static void ThrowsCanceled(Action action)
{
    try { action(); } catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected stale lifetime rejection");
}
static void ThrowsRegistration(Action action, ExtensionRegistrationFailure expected)
{
    try { action(); }
    catch (ExtensionRegistrationException error) when (error.Failure == expected) { return; }
    throw new InvalidOperationException($"Expected {expected}");
}
static Task OrderAndPayload()
{
    var bus = new ExtensionEventBus();
    var payload = new object();
    var seen = new List<int>();
    using var first = bus.On("", data => { Require(ReferenceEquals(data, payload)); seen.Add(1); });
    using var second = bus.On("", data => { Require(ReferenceEquals(data, payload)); seen.Add(2); });
    bus.Emit("", payload);
    Require(seen.SequenceEqual([1, 2]));
    using var nullable = bus.On("null", data => Require(data is null));
    bus.Emit("null", null);
    bus.Emit("NULL", payload);
    return Task.CompletedTask;
}
static Task Snapshot()
{
    var bus = new ExtensionEventBus();
    var seen = new List<int>();
    IDisposable? second = null;
    IDisposable? added = null;
    using var first = bus.On("x", _ => { seen.Add(1); second!.Dispose(); added ??= bus.On("x", _ => seen.Add(3)); });
    second = bus.On("x", _ => seen.Add(2));
    bus.Emit("x", null);
    Require(seen.SequenceEqual([1, 2]));
    seen.Clear(); bus.Emit("x", null);
    Require(seen.SequenceEqual([1, 3]));
    second.Dispose(); added!.Dispose();
    return Task.CompletedTask;
}
static Task DuplicateHandles()
{
    var bus = new ExtensionEventBus(); var count = 0;
    Action<object?> listener = _ => count++;
    using var one = bus.On("x", listener); using var two = bus.On("x", listener);
    one.Dispose(); one.Dispose(); bus.Emit("x", null); Require(count == 1);
    two.Dispose(); bus.Emit("x", null); Require(count == 1);
    return Task.CompletedTask;
}
static Task FaultIsolation()
{
    var fault = new InvalidOperationException("listener"); var reports = 0; var later = 0;
    var bus = new ExtensionEventBus((channel, error) => { Require(channel == "x" && ReferenceEquals(error, fault)); reports++; throw new Exception("diagnostic"); });
    using var first = bus.On("x", _ => throw fault);
    using var second = bus.On("x", _ => later++);
    bus.Emit("x", null); Require(reports == 1 && later == 1);
    return Task.CompletedTask;
}
static Task ClearAndReentry()
{
    var bus = new ExtensionEventBus(); var seen = new List<int>();
    using var first = bus.On("x", _ => { seen.Add(1); bus.Clear(); bus.Emit("x", null); });
    using var second = bus.On("x", _ => seen.Add(2));
    bus.Emit("x", null); bus.Emit("x", null); Require(seen.SequenceEqual([1, 2]));
    return Task.CompletedTask;
}
static async Task SharedHost()
{
    await using var host = new ExtensionRegistry(); await using var other = new ExtensionRegistry();
    var receiver = new Probe(); var sender = new Probe(); var isolated = new Probe();
    await using var one = await host.ActivateAsync("one", receiver);
    await using var two = await host.ActivateAsync("two", sender);
    await using var three = await other.ActivateAsync("three", isolated);
    var count = 0;
    using var subscription = receiver.Bus!.On("x", _ => count++);
    isolated.Bus!.Emit("x", null); Require(count == 0);
    sender.Bus!.Emit("x", null); Require(count == 1);
}
static async Task OwnerLifetime()
{
    await using var host = new ExtensionRegistry(); var receiver = new Probe(); var sender = new Probe();
    var one = await host.ActivateAsync("one", receiver);
    await using var two = await host.ActivateAsync("two", sender);
    var count = 0; var subscription = receiver.Bus!.On("x", _ => count++);
    sender.Bus!.Emit("x", null); Require(count == 1);
    await one.DisposeAsync();
    sender.Bus.Emit("x", null); Require(count == 1);
    ThrowsCanceled(() => receiver.Bus.Emit("x", null));
    ThrowsCanceled(() => receiver.Bus.On("x", _ => { }));
    subscription.Dispose(); subscription.Dispose();
}
static async Task FailedInitialization()
{
    await using var host = new ExtensionRegistry(); var count = 0;
    var broken = new Probe(bus => { bus.On("x", _ => count++); throw new InvalidOperationException("initialization"); });
    try { await host.ActivateAsync("broken", broken); throw new Exception("Expected activation failure"); }
    catch (ExtensionRegistrationException) { }
    var sender = new Probe(); await using var active = await host.ActivateAsync("sender", sender);
    sender.Bus!.Emit("x", null); Require(count == 0);
    ThrowsCanceled(() => broken.Bus!.Emit("x", null));
}

static TaskCompletionSource Entered() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static async Task HeldClose()
{
    await using var host = new ExtensionRegistry(); var receiver = new Probe(); var sender = new Probe();
    var one = await host.ActivateAsync("one", receiver);
    await using var two = await host.ActivateAsync("two", sender);
    var entered = Entered(); using var release = new ManualResetEventSlim();
    using var listener = receiver.Bus!.On("x", _ => { entered.SetResult(); Require(release.Wait(TimeSpan.FromSeconds(10))); });
    var emission = Task.Run(() => sender.Bus!.Emit("x", null));
    Task? close = null;
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        close = one.DisposeAsync().AsTask();
        Require(!close.IsCompleted && receiver.Disposals == 0);
    }
    finally { release.Set(); await emission; if (close is not null) await close; }
    Require(receiver.Disposals == 1);
}
static async Task StaleCapturedListener()
{
    await using var host = new ExtensionRegistry(); var first = new Probe(); var late = new Probe(); var sender = new Probe();
    await using var one = await host.ActivateAsync("one", first);
    var two = await host.ActivateAsync("two", late);
    await using var three = await host.ActivateAsync("three", sender);
    var entered = Entered(); using var release = new ManualResetEventSlim(); var count = 0;
    using var blocker = first.Bus!.On("x", _ => { entered.SetResult(); Require(release.Wait(TimeSpan.FromSeconds(10))); });
    using var listener = late.Bus!.On("x", _ => count++);
    var emission = Task.Run(() => sender.Bus!.Emit("x", null));
    try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await two.DisposeAsync(); Require(late.Disposals == 1); }
    finally { release.Set(); await emission; }
    Require(count == 0);
}
static async Task HeldQuiescence()
{
    await using var host = new ExtensionRegistry(); var receiver = new Probe(); var sender = new Probe();
    await using var one = await host.ActivateAsync("one", receiver);
    await using var two = await host.ActivateAsync("two", sender);
    var entered = Entered(); using var release = new ManualResetEventSlim(); var count = 0;
    using var listener = receiver.Bus!.On("x", _ =>
    {
        if (Interlocked.Increment(ref count) == 1) { entered.SetResult(); Require(release.Wait(TimeSpan.FromSeconds(10))); }
    });
    var emission = Task.Run(() => sender.Bus!.Emit("x", null));
    Task<RegistrationQuiescenceLease>? pause = null;
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); pause = one.QuiesceAsync().AsTask();
        Require(!pause.IsCompleted); sender.Bus!.Emit("x", null); Require(count == 1);
        ThrowsRegistration(() => receiver.Bus.Emit("x", null), ExtensionRegistrationFailure.InactiveScope);
        ThrowsRegistration(() => receiver.Bus.On("x", _ => { }), ExtensionRegistrationFailure.InactiveScope);
        release.Set(); await emission;
        using var lease = await pause;
        sender.Bus!.Emit("x", null); Require(count == 1);
        lease.Dispose(); sender.Bus.Emit("x", null); Require(count == 2);
    }
    finally
    {
        release.Set(); await emission;
        if (pause is not null) { var lease = await pause; lease.Dispose(); }
    }
}
static async Task NestedReentry()
{
    await using var host = new ExtensionRegistry(); var first = new Probe(); var nested = new Probe(); var sender = new Probe();
    await using var one = await host.ActivateAsync("one", first);
    await using var two = await host.ActivateAsync("two", nested);
    await using var three = await host.ActivateAsync("three", sender); var checks = 0;
    using var outer = first.Bus!.On("outer", _ => first.Bus.Emit("inner", null));
    using var inner = nested.Bus!.On("inner", _ =>
    {
        ThrowsRegistration(() => { _ = one.DisposeAsync(); }, ExtensionRegistrationFailure.ReentrantDisposal); checks++;
        ThrowsRegistration(() => { _ = one.QuiesceAsync(); }, ExtensionRegistrationFailure.ReentrantDisposal); checks++;
        ThrowsRegistration(() => { _ = two.DisposeAsync(); }, ExtensionRegistrationFailure.ReentrantDisposal); checks++;
        ThrowsRegistration(() => { _ = two.QuiesceAsync(); }, ExtensionRegistrationFailure.ReentrantDisposal); checks++;
        ThrowsRegistration(() => { _ = host.DisposeAsync(); }, ExtensionRegistrationFailure.ReentrantDisposal); checks++;
    });
    sender.Bus!.Emit("outer", null); Require(checks == 5);
    sender.Bus.Emit("outer", null); Require(checks == 10 && first.Disposals == 0 && nested.Disposals == 0);
}
static async Task RetiredCharge()
{
    await using var host = new ExtensionRegistry(new() { MaximumRegistrations = 1, MaximumRegistrationsPerOwner = 1 });
    var receiver = new Probe(); var sender = new Probe();
    await using var one = await host.ActivateAsync("one", receiver);
    await using var two = await host.ActivateAsync("two", sender);
    var entered = Entered(); using var release = new ManualResetEventSlim();
    var listener = receiver.Bus!.On("x", _ => { entered.SetResult(); Require(release.Wait(TimeSpan.FromSeconds(10))); });
    var emission = Task.Run(() => sender.Bus!.Emit("x", null));
    try
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); listener.Dispose();
        ThrowsRegistration(() => receiver.Bus.On("y", _ => { }), ExtensionRegistrationFailure.LimitExceeded);
    }
    finally { release.Set(); await emission; listener.Dispose(); }
    using var replacement = receiver.Bus.On("y", _ => { });
}
static async Task InitializationPublication()
{
    await using var host = new ExtensionRegistry(); var count = 0;
    var receiver = new Probe(bus => { bus.On("x", _ => count++); bus.Emit("x", null); Require(count == 0); });
    await using var one = await host.ActivateAsync("one", receiver);
    var captured = host.CaptureSnapshot();
    Require(captured.Registrations.IsEmpty && captured.Tools.IsEmpty && captured.Commands.IsEmpty);
    receiver.Bus!.Emit("x", null); Require(count == 1);
}

sealed class Probe(Action<IExtensionEventBus>? initialize = null) : IPiSharpExtension
{
    public IExtensionEventBus? Bus { get; private set; }
    public int Disposals { get; private set; }
    public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken)
    {
        Bus = ((IExtensionEventBusRegistry)registry).Events;
        initialize?.Invoke(Bus);
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
}
