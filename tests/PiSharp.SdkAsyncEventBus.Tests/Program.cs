using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;
using System.Text.Json;
using System.Threading.Tasks.Sources;

if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Expected --report <new-path>");
string? reportPath = args.Length == 2 ? args[1] : null;
var cases = new (string Name, Func<Task> Run)[]
{
 ("ordered synchronous starts do not await an earlier listener", OrderedStarts),
 ("standalone snapshot retains removed listener and defers additions", AsyncSnapshot),
 ("clear retains admitted original work for drain", ClearRetainsPending),
 ("drain snapshots exclude later emissions", DrainSnapshot),
 ("synchronous callback fault preserves exception identity", SyncFault),
 ("async original aggregate preserves all nested fault identities", AsyncFaultGraph),
 ("reporter fault is isolated and original is observed", ReporterFault),
 ("original cancellation retains requested token identity", OriginalCancellation),
 ("ValueTask source is captured and consumed exactly once", OneValueTaskConsumption),
 ("unmatched error emits the original Exception", UnhandledException),
 ("unmatched nonException error retains payload identity", UnhandledPayload),
 ("registered error handler failure is diagnosed without emit failure", RegisteredError),
 ("owner close joins held async work before extension cleanup", OwnerClose),
 ("retired listener remains charged until original async settlement", AsyncRetiredCharge),
 ("quiescence joins originals suppresses entry and readmits after resume", AsyncQuiescence),
 ("captured owner listener cannot start after retirement", OwnerStaleSnapshot),
 ("subscription async disposal joins held original after retirement", SubscriptionClose),
 ("bus and subscription drain reentry reject before mutation after await", StandaloneReentry),
 ("scope and ancestor lifecycle reentry reject after await", OwnerReentry),
 ("async fault settlement cannot prevent owner cleanup", OwnerFaultCleanup)
};var failures = 0;
var evidence = new List<object>();
var callbackEvidence = new List<object>();
var callbackOrdinal = 0;
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
    while (callbackOrdinal < CallbackOriginals.All.Count)
    {
        var callback = CallbackOriginals.All[callbackOrdinal++];
        Exception? callbackCaught = null;
        try { await callback.Original; } catch (Exception error) { callbackCaught = error; }
        callbackEvidence.Add(new { callback.Name, originalTaskJoined = true, status = callback.Original.Status.ToString(), canceled = callback.Original.IsCanceled, faulted = callback.Original.IsFaulted, faults = CaptureFaults(callback.Original.Exception, callbackCaught) });
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
        schemaVersion = 1, profile = "native-sdk-async-event-bus-20-owned-originals",
        originalOracle = "d86654abb8862e201933517d6f1fce9f88dd117f",
        expected = cases.Length, executed = evidence.Count, passed = cases.Length - failures, failed = failures,
        tests = evidence, callbackSynchronousFailures = CallbackOriginals.SynchronousFailures.Select(error => CaptureFaults(null,error)).ToArray(), callbackOriginals = callbackEvidence, directOriginalAwait = true, noNode = true
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
static TaskCompletionSource Pending(string name)
{
 var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
 CallbackOriginals.All.Add((name, source.Task)); return source;
}
static void Throws(Action action, Func<Exception, bool> predicate)
{
 try { action(); } catch (Exception error) when (predicate(error)) { return; }
 throw new InvalidOperationException("Expected guarded failure");
}
static bool Reentrant(Exception error) => error is ExtensionRegistrationException registration && registration.Failure == ExtensionRegistrationFailure.ReentrantDisposal;
static async Task OrderedStarts()
{
 var bus = new ExtensionEventBus(); var held = Pending("ordered-held"); var order = new List<int>(); var payload = new object();
 using var first = bus.ObservedOnAsync("x", data => { Require(ReferenceEquals(data,payload)); order.Add(1); return held.Task; });
 using var second = bus.On("x", data => { Require(ReferenceEquals(data,payload)); order.Add(2); });
 Task? drain=null;
 try { bus.Emit("x",payload); Require(order.SequenceEqual([1,2]) && !held.Task.IsCompleted); drain=bus.DrainAsync().AsTask(); Require(!drain.IsCompleted); }
 finally { held.TrySetResult(); await held.Task; if(drain is not null)await drain; await bus.DrainAsync(); }
}
static async Task AsyncSnapshot()
{
 var bus = new ExtensionEventBus(); var order = new List<int>(); IDisposable? late = null; IDisposable? added = null;
 using var first = bus.ObservedOnAsync("x", _ => { order.Add(1); late!.Dispose(); added ??= bus.On("x", _ => order.Add(3)); return Task.CompletedTask; });
 late = bus.On("x", _ => order.Add(2));
 try { bus.Emit("x",null); Require(order.SequenceEqual([1,2])); order.Clear(); bus.Emit("x",null); Require(order.SequenceEqual([1,3])); await bus.DrainAsync(); }
 finally { late.Dispose(); added?.Dispose(); }
}
static async Task ClearRetainsPending()
{
 var bus = new ExtensionEventBus(); var held = Pending("clear-held"); var count = 0;
 using var listener = bus.ObservedOnAsync("x", _ => { count++; return held.Task; });
 bus.Emit("x",null); bus.Clear(); bus.Emit("x",null); var drain = bus.DrainAsync().AsTask();
 try { Require(count==1 && !drain.IsCompleted); } finally { held.TrySetResult(); await held.Task; await drain; }
}
static async Task DrainSnapshot()
{
 var bus = new ExtensionEventBus(); var one = Pending("snapshot-one"); var two = Pending("snapshot-two"); var calls=0;
 using var listener = bus.ObservedOnAsync("x", _ => ++calls==1 ? one.Task : two.Task);
 bus.Emit("x",null); var drain = bus.DrainAsync().AsTask(); bus.Emit("x",null);
 try { one.SetResult(); await one.Task; await drain; Require(!two.Task.IsCompleted); }
 finally { one.TrySetResult(); two.TrySetResult(); await one.Task; await two.Task; await bus.DrainAsync(); }
}
static async Task SyncFault()
{
 var failure = new InvalidOperationException("sync-original"); Exception? seen=null; var later=0;
 var bus = new ExtensionEventBus((_,error)=>seen=error);
 using var one=bus.ObservedOnAsync("x", _ => throw failure); using var two=bus.On("x", _=>later++);
 bus.Emit("x",null); await bus.DrainAsync(); Require(ReferenceEquals(seen,failure) && later==1);
}
static async Task AsyncFaultGraph()
{
 var first=new InvalidOperationException("first"); var nested=new AggregateException("nested",new ArgumentException("leaf"));
 var original=Pending("multi-fault-original"); Exception? seen=null; var bus=new ExtensionEventBus((_,error)=>seen=error);
 using var listener=bus.ObservedOnAsync("x",_=>original.Task); bus.Emit("x",null);
 original.SetException([first,nested]); await bus.DrainAsync();
 Require(original.Task.IsFaulted && seen is AggregateException graph && graph.InnerExceptions.Count==2 && ReferenceEquals(graph.InnerExceptions[0],first) && ReferenceEquals(graph.InnerExceptions[1],nested));
}
static async Task ReporterFault()
{
 var source=Pending("reporter-fault-original"); var reports=0; var later=0; var unobserved=0;
 EventHandler<UnobservedTaskExceptionEventArgs> observer=(_,_)=>unobserved++;
 TaskScheduler.UnobservedTaskException+=observer;
 try
 {
  var bus=new ExtensionEventBus((_,_)=>{reports++;throw new Exception("reporter");});
  using var one=bus.ObservedOnAsync("x",_=>source.Task); using var two=bus.On("x",_=>later++);
  bus.Emit("x",null); source.SetException(new InvalidOperationException("observed")); await bus.DrainAsync();
  Require(source.Task.IsFaulted && reports==1 && later==1 && unobserved==0);
 }
 finally { TaskScheduler.UnobservedTaskException-=observer; }
}
static async Task OriginalCancellation()
{
 using var cancellation=new CancellationTokenSource(); cancellation.Cancel(); var source=Pending("canceled-original"); Exception? seen=null;
 var bus=new ExtensionEventBus((_,error)=>seen=error); using var listener=bus.ObservedOnAsync("x",_=>source.Task);
 bus.Emit("x",null); source.SetCanceled(cancellation.Token); await bus.DrainAsync();
 Require(source.Task.IsCanceled && seen is OperationCanceledException error && error.CancellationToken==cancellation.Token);
}
static async Task OneValueTaskConsumption()
{
 var bus=new ExtensionEventBus(); var source=new OneShotSource(); var calls=0;
 using var listener=bus.OnValueTask("x",_=>{calls++; return source.Create();});
 bus.Emit("x",null); var drain=bus.DrainAsync().AsTask();
 try { Require(calls==1 && source.Results==0 && !drain.IsCompleted); }
 finally { source.Complete(); await drain; }
 Require(source.Results==1);
}
static Task UnhandledException()
{
 var bus=new ExtensionEventBus(); var original=new InvalidOperationException("unhandled");
 Throws(()=>bus.Emit("error",original), error=>ReferenceEquals(error,original)); return Task.CompletedTask;
}
static Task UnhandledPayload()
{
 var bus=new ExtensionEventBus(); var payload=new object();
 Throws(()=>bus.Emit("error",payload), error=>error is ExtensionEventBusUnhandledErrorException wrapped && ReferenceEquals(wrapped.Payload,payload)); return Task.CompletedTask;
}
static async Task RegisteredError()
{
 var original=new Exception("handler"); Exception? reported=null; var bus=new ExtensionEventBus((_,error)=>reported=error);
 using var handler=bus.On("error",_=>throw original); bus.Emit("error",new object()); await bus.DrainAsync(); Require(ReferenceEquals(original,reported));
}
static async Task OwnerClose()
{
 // Exercise both raw Action and Task listener retirement without disposing the public handles.
 foreach(var asynchronous in new[]{false,true})
 {
  await using var host=new ExtensionRegistry(); var receiver=new Probe(); var sender=new Probe();
  await using var one=await host.ActivateAsync("one",receiver); await using var two=await host.ActivateAsync("two",sender);
  var held=Pending("owner-close-original:"+asynchronous); var calls=0;
  receiver.Bus!.ObservedOnAsync("x",_=>held.Task);
  if(asynchronous)receiver.Bus.ObservedOnAsync("error",_=>{calls++;return Task.CompletedTask;});
  else receiver.Bus.On("error",_=>calls++);
  var failure=new InvalidOperationException("unmatched-after-owner-fence:"+asynchronous);
  sender.Bus!.Emit("error",failure); Require(calls==1);
  using var cancellationRelease=new ManualResetEventSlim();
  var cancellationEntered=Pending("owner-cancellation-observer:"+asynchronous);
  // Registered last: CancelAsync invokes this first. Hold cancellation so the old lifetime-only
  // retirement path has not yet reached either owned subscription's cancellation registration.
  using var cancellationObserver=one.ExtensionLifetimeCancellationToken.Register(()=>
  {cancellationEntered.TrySetResult();cancellationRelease.Wait();});
  sender.Bus.Emit("x",null); var close=one.DisposeAsync().AsTask();
  try
  {
   await cancellationEntered.Task;
   Require(!close.IsCompleted && receiver.Disposals==0);
   Throws(()=>sender.Bus.Emit("error",failure),error=>ReferenceEquals(error,failure));
   Require(calls==1); sender.Bus.Emit("x",null);
   cancellationRelease.Set();
   Require(!close.IsCompleted); // Held actual user original still prevents owner cleanup.
  }
  finally
  {
   cancellationRelease.Set();held.TrySetResult();
   await cancellationEntered.Task;await held.Task;await close;
  }
  Require(receiver.Disposals==1 && calls==1);
  Throws(()=>sender.Bus.Emit("error",failure),error=>ReferenceEquals(error,failure));
 }
}
static async Task AsyncRetiredCharge()
{
 await using var host=new ExtensionRegistry(new(){MaximumRegistrations=1,MaximumRegistrationsPerOwner=1,MaximumConcurrentDispatches=1}); var receiver=new Probe();
 await using var owner=await host.ActivateAsync("one",receiver); var held=Pending("retired-charge-original"); var calls=0; var listener=receiver.Bus!.ObservedOnAsync("x",_=>{calls++;return held.Task;});
 receiver.Bus.Emit("x",null);
 // A held original occupies the sole dispatch admission; the second emission must not enter user code.
 receiver.Bus.Emit("x",null); listener.Dispose();
 try { Require(calls==1); Throws(()=>receiver.Bus.ObservedOnAsync("y",_=>Task.CompletedTask),error=>error is ExtensionRegistrationException registration && registration.Failure==ExtensionRegistrationFailure.LimitExceeded); }
 finally { held.TrySetResult(); await held.Task; await listener.DrainAsync(); }
 using var replacement=receiver.Bus.ObservedOnAsync("y",_=>{calls++;return Task.CompletedTask;});
 receiver.Bus.Emit("y",null); Require(calls==2);
}
static async Task AsyncQuiescence()
{
 await using var host=new ExtensionRegistry(); var receiver=new Probe(); var sender=new Probe();
 await using var one=await host.ActivateAsync("one",receiver); await using var two=await host.ActivateAsync("two",sender);
 var held=Pending("quiescence-original"); var calls=0; using var listener=receiver.Bus!.ObservedOnAsync("x",_=>++calls==1 ? held.Task : Task.CompletedTask);
 sender.Bus!.Emit("x",null); var pause=one.QuiesceAsync().AsTask(); RegistrationQuiescenceLease? lease=null;
 try
 {
  Require(!pause.IsCompleted); sender.Bus.Emit("x",null); Require(calls==1);
  held.SetResult(); await held.Task; lease=await pause; sender.Bus.Emit("x",null); Require(calls==1);
  lease.Dispose(); sender.Bus.Emit("x",null); Require(calls==2);
 }
 finally { held.TrySetResult(); await held.Task; lease ??= await pause; lease.Dispose(); }
}
static async Task OwnerStaleSnapshot()
{
 await using var host=new ExtensionRegistry(); var receiver=new Probe(); var sender=new Probe();
 await using var one=await host.ActivateAsync("one",receiver); await using var two=await host.ActivateAsync("two",sender); var calls=0;
 IExtensionEventBusSubscription? late=null;
 using var first=sender.Bus!.On("x",_=>late!.Dispose()); late=receiver.Bus!.ObservedOnAsync("x",_=>{calls++;return Task.CompletedTask;});
 try { sender.Bus.Emit("x",null); await sender.Bus.DrainAsync(); Require(calls==0); } finally { late.Dispose(); }
}
static async Task SubscriptionClose()
{
 await using var host=new ExtensionRegistry(); var receiver=new Probe(); var sender=new Probe();
 await using var one=await host.ActivateAsync("one",receiver); await using var two=await host.ActivateAsync("two",sender);
 var bus=receiver.Bus!; var held=Pending("subscription-close-original"); var calls=0;
 var listener=bus.ObservedOnAsync("x",_=>{calls++;return held.Task;}); sender.Bus!.Emit("x",null); var close=listener.DisposeAsync().AsTask();
 try { sender.Bus.Emit("x",null); Require(calls==1 && !close.IsCompleted && receiver.Disposals==0); }
 finally { held.TrySetResult(); await held.Task; await close; }
 using var replacement=bus.ObservedOnAsync("y",_=>Task.CompletedTask);
}
static async Task StandaloneReentry()
{
 var bus=new ExtensionEventBus(); var held=Pending("standalone-reentry-release"); var checks=0; IExtensionEventBusSubscription? listener=null;
 listener=bus.ObservedOnAsync("x",async _=>{
  await held.Task;
  Throws(()=>{_ = bus.DrainAsync();},error=>error is InvalidOperationException); checks++;
  Throws(()=>{_ = listener!.DisposeAsync();},error=>error is InvalidOperationException); checks++;
 });
 try { bus.Emit("x",null); held.SetResult(); await held.Task; await bus.DrainAsync(); Require(checks==2); bus.Emit("x",null); await bus.DrainAsync(); Require(checks==4); }
 finally { held.TrySetResult(); listener.Dispose(); await bus.DrainAsync(); }
}
static async Task OwnerReentry()
{
 await using var host=new ExtensionRegistry(); var outer=new Probe(); var inner=new Probe();
 await using var one=await host.ActivateAsync("one",outer); await using var two=await host.ActivateAsync("two",inner);
 var release=Pending("owner-reentry-release"); var innerDone=Pending("owner-reentry-inner-done"); var checks=0;
 using var nested=inner.Bus!.ObservedOnAsync("inner",async _=>{
  try {
   await release.Task;
   Throws(()=>{_ = one.DisposeAsync();},Reentrant); checks++;
   Throws(()=>{_ = two.QuiesceAsync();},Reentrant); checks++;
   Throws(()=>{_ = host.DisposeAsync();},Reentrant); checks++;
  } finally { innerDone.TrySetResult(); }
 });
 using var listener=outer.Bus!.ObservedOnAsync("outer",async _=>{ inner.Bus.Emit("inner",null); await release.Task; await innerDone.Task; });
 try { outer.Bus.Emit("outer",null); release.SetResult(); await release.Task; await outer.Bus.DrainAsync(); Require(checks==3 && outer.Disposals==0 && inner.Disposals==0); }
 finally { release.TrySetResult(); innerDone.TrySetResult(); await outer.Bus.DrainAsync(); }
}
static async Task OwnerFaultCleanup()
{
 await using var host=new ExtensionRegistry(); var receiver=new Probe(); await using var owner=await host.ActivateAsync("one",receiver);
 var original=Pending("owner-fault-original"); using var listener=receiver.Bus!.ObservedOnAsync("x",_=>original.Task);
 receiver.Bus.Emit("x",null); var close=owner.DisposeAsync().AsTask();
 try { Require(!close.IsCompleted); } finally { original.TrySetException(new InvalidOperationException("held failure")); await close; }
 Require(original.Task.IsFaulted && receiver.Disposals==1);
}

static class CallbackOriginals
{
 internal static readonly List<(string Name,Task Original)> All=[];
 internal static readonly List<Exception> SynchronousFailures=[];
 internal static IExtensionEventBusSubscription ObservedOnAsync(this IAsyncExtensionEventBus bus,string channel,Func<object?,Task> handler)
 {
  return bus.OnAsync(channel,data=>{
   Task original;
   try { original=handler(data); }
   catch(Exception error){SynchronousFailures.Add(error);throw;}
   All.Add(("listener:"+channel,original));return original;
  });
 }
}
sealed class Probe : IPiSharpExtension
{
 private IAsyncExtensionEventBus? bus;
 public IAsyncExtensionEventBus Bus => bus ?? throw new InvalidOperationException("Probe event bus accessed before initialization.");
 public int Disposals {get;private set;}
 public ValueTask InitializeAsync(IExtensionRegistry registry,CancellationToken cancellationToken)
 { bus=(IAsyncExtensionEventBus)((IExtensionEventBusRegistry)registry).Events; return ValueTask.CompletedTask; }
 public ValueTask DisposeAsync(){Disposals++;return ValueTask.CompletedTask;}
}
sealed class OneShotSource : IValueTaskSource
{
 private ManualResetValueTaskSourceCore<bool> core=new(){RunContinuationsAsynchronously=true};
 public int Results {get;private set;}
 public ValueTask Create()=>new(this,core.Version);
 public void Complete()=>core.SetResult(true);
 public void GetResult(short token){Results++; if(Results!=1)throw new InvalidOperationException("Original ValueTask consumed twice");core.GetResult(token);}
 public ValueTaskSourceStatus GetStatus(short token)=>core.GetStatus(token);
 public void OnCompleted(Action<object?> continuation,object? state,short token,ValueTaskSourceOnCompletedFlags flags)=>core.OnCompleted(continuation,state,token,flags);
}