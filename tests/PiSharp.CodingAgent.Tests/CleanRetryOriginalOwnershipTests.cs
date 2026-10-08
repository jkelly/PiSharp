using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using PiSharp.AI;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class CleanRetryOriginalOwnershipTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("clean retry ownership multicast persistence joins every original before preference idle and close", PersistenceOriginals),
        ("clean retry ownership multicast synchronous and asynchronous faults retain each reference", PersistenceFaults),
        ("clean retry ownership aggregate persistence original retains every WhenAll fault", PersistenceAggregateFaults),
        ("clean retry ownership coordinator delay aggregate retains body and terminal observer faults", CoordinatorDelayFaults),
        ("clean retry ownership coordinator observer aggregate retains body and terminal observer faults", CoordinatorObserverFaults),
        ("clean retry ownership coordinator omission aggregate retains body and terminal observer faults", CoordinatorOmissionFaults),
        ("clean retry ownership multicast coordinator injections reject before admission", InjectionAdmission),
        ("clean retry ownership nested external backoff cancellation retains ancestor self-wait guards", NestedBackoffs),
        ("clean retry ownership nested external settings cancellation retains ancestors and acknowledged policy", NestedSettings)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Clean retry original ownership assertion failed."); }
    private static void Reject(Action enter)
    { var rejected = false; try { enter(); } catch (InvalidOperationException) { rejected = true; } Check(rejected); }
    private static void RejectAdmission(Action enter)
    { var rejected = false; try { enter(); } catch (ArgumentException) { rejected = true; } Check(rejected); }
    private static IEnumerable<Exception> Leaves(Exception error) => error switch
    {
        AggregateException { InnerExceptions.Count: > 0 } aggregate => aggregate.InnerExceptions.SelectMany(Leaves),
        PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed,
            InnerException: AggregateException { InnerExceptions.Count: > 0 } cleanup } => cleanup.InnerExceptions.SelectMany(Leaves),
        _ => [error]
    };
    private static async Task DisposeExpected(PersistentAgentSession session, params Exception[] expected)
    {
        var original = session.DisposeAsync().AsTask();
        if (expected.Length == 0) { await original; return; }
        var error = await Failure(original);
        Check(original.IsFaulted && error is PersistentAgentSessionException
            { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed, InnerException: AggregateException { InnerExceptions.Count: > 0 } } cleanup &&
            ReferenceEquals(session.Snapshot.Fault, cleanup.Fault) && session.Snapshot.IsDisposed);
        Check(Leaves(error).SequenceEqual(expected, ReferenceEqualityComparer.Instance));
        Check(ReferenceEquals(original, session.DisposeAsync().AsTask()) && ReferenceEquals(await Failure(original), error));
    }
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected original failure."); }
    private static async Task Join(Exception? body, Exception[] expected, params Task[] originals)
    {
        var failures = new List<Exception>(); if (body is not null) failures.Add(body);
        foreach (var original in originals.Distinct())
            try { await original; }
            catch (Exception error)
            {
                foreach (var leaf in Leaves(error))
                    if (!expected.Any(value => ReferenceEquals(value, leaf)) && !failures.Any(value => ReferenceEquals(value, leaf))) failures.Add(leaf);
            }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count != 0) throw new AggregateException(failures);
    }
    private static async Task PersistenceOriginals()
    {
        var first = Gate(); var second = Gate(); var calls = 0; var session = await Open();
        Func<bool, CancellationToken, Task> writers = (_, _) => { calls++; return first.Task; };
        writers += (_, _) => { calls++; return second.Task; };
        session.ConfigureAutomaticRetry(new(), writers);
        var write = session.SetAutoRetryEnabledAsync(false); var idle = session.WaitForIdleAsync(); var close = session.StopAdmissionAndJoinAsync();
        Exception? body = null;
        try
        {
            Check(calls == 2 && session.AutoRetryEnabled && !write.IsCompleted && !idle.IsCompleted && !close.IsCompleted);
            first.TrySetResult(); Check(session.AutoRetryEnabled && !write.IsCompleted && !close.IsCompleted);
            second.TrySetResult(); await write; await idle; await close; Check(!session.AutoRetryEnabled);
        }
        catch (Exception error) { body = error; }
        finally
        { first.TrySetResult(); second.TrySetResult(); await Join(body, [], first.Task, second.Task, write, idle, close, session.DisposeAsync().AsTask()); }
    }
    private static async Task PersistenceFaults()
    {
        var synchronous = new IOException("first synchronous writer"); var middleFault = new IOException("middle original writer"); var lastFault = new IOException("last original writer");
        var middle = Gate(); var last = Gate(); var calls = 0; var session = await Open();
        Func<bool, CancellationToken, Task> writers = (_, _) => { calls++; throw synchronous; };
        writers += (_, _) => { calls++; return middle.Task; };
        writers += (_, _) => { calls++; return last.Task; };
        session.ConfigureAutomaticRetry(new(), writers);
        var write = session.SetAutoRetryEnabledAsync(false); var idle = session.WaitForIdleAsync(); var close = session.StopAdmissionAndJoinAsync();
        Exception? body = null; Exception[] expected = [synchronous, middleFault, lastFault];
        try
        {
            Check(calls == 3 && !write.IsCompleted && !idle.IsCompleted && !close.IsCompleted);
            middle.TrySetException(middleFault); Check(!write.IsCompleted && session.AutoRetryEnabled);
            last.TrySetException(lastFault); var leaves = Leaves(await Failure(write)).ToArray();
            Check(leaves.Length == 3 && expected.All(marker => leaves.Any(value => ReferenceEquals(value, marker))) && session.AutoRetryEnabled);
            Check(Leaves(await Failure(close)).All(value => expected.Any(marker => ReferenceEquals(value, marker))));
        }
        catch (Exception error) { body = error; }
        finally
        { middle.TrySetException(middleFault); last.TrySetException(lastFault); await Join(body, expected, middle.Task, last.Task, write, idle, close, DisposeExpected(session, expected)); }
    }
    private static async Task PersistenceAggregateFaults()
    {
        var first = Gate(); var second = Gate(); var tail = Gate(); var combined = Task.WhenAll(first.Task, second.Task);
        var firstFault = new IOException("first WhenAll original"); var secondFault = new IOException("second WhenAll original"); var tailFault = new IOException("later writer original");
        Exception[] expected = [firstFault, secondFault, tailFault]; var session = await Open(); var calls = 0;
        Func<bool, CancellationToken, Task> writers = (_, _) => { calls++; return combined; };
        writers += (_, _) => { calls++; return tail.Task; };
        session.ConfigureAutomaticRetry(new(), writers);
        var write = session.SetAutoRetryEnabledAsync(false); var idle = session.WaitForIdleAsync(); var close = session.StopAdmissionAndJoinAsync();
        Exception? body = null;
        try
        {
            Check(calls == 2 && !write.IsCompleted && !idle.IsCompleted && !close.IsCompleted && session.AutoRetryEnabled);
            first.TrySetException(firstFault); Check(!combined.IsCompleted && !write.IsCompleted);
            second.TrySetException(secondFault); Check(!write.IsCompleted && !close.IsCompleted && session.AutoRetryEnabled);
            tail.TrySetException(tailFault); var leaves = Leaves(await Failure(write)).ToArray();
            Check(leaves.Length == 3 && expected.All(marker => leaves.Any(value => ReferenceEquals(value, marker))) && session.AutoRetryEnabled);
            var idleLeaves = Leaves(await Failure(idle)).ToArray(); var closeLeaves = Leaves(await Failure(close)).ToArray();
            Check(idleLeaves.Length == 3 && expected.All(marker => idleLeaves.Any(value => ReferenceEquals(value, marker))));
            Check(closeLeaves.Length == 3 && expected.All(marker => closeLeaves.Any(value => ReferenceEquals(value, marker))));
        }
        catch (Exception error) { body = error; }
        finally
        {
            first.TrySetException(firstFault); second.TrySetException(secondFault); tail.TrySetException(tailFault);
            await Join(body, expected, first.Task, second.Task, combined, tail.Task, write, idle, close, DisposeExpected(session, expected));
        }
    }
    private enum OriginalKind { Delay, Observer, Omission }
    private static Task CoordinatorDelayFaults() => CoordinatorAggregateOriginals(OriginalKind.Delay);
    private static Task CoordinatorObserverFaults() => CoordinatorAggregateOriginals(OriginalKind.Observer);
    private static Task CoordinatorOmissionFaults() => CoordinatorAggregateOriginals(OriginalKind.Omission);
    private static async Task CoordinatorAggregateOriginals(OriginalKind kind)
    {
        var bodyFirst = Gate(); var bodySecond = Gate(); var endFirst = Gate(); var endSecond = Gate(); var endEntered = Gate();
        var bodyOriginal = Task.WhenAll(bodyFirst.Task, bodySecond.Task); var endOriginal = Task.WhenAll(endFirst.Task, endSecond.Task);
        var bodyFaultA = new IOException("first physical " + kind); var bodyFaultB = new IOException("second physical " + kind);
        var endFaultA = new IOException("first physical terminal observer"); var endFaultB = new AggregateException("empty aggregate physical terminal observer");
        Exception[] expected = [bodyFaultA, bodyFaultB, endFaultA, endFaultB]; var starts = 0; var ends = 0; var omits = 0; var delays = 0;
        var coordinator = new SessionRetryCoordinator(() => new(), value =>
        {
            if (value is SessionRetryStarted) { starts++; return kind == OriginalKind.Observer ? bodyOriginal : Task.CompletedTask; }
            ends++; endEntered.TrySetResult(); return endOriginal;
        }, (_, _) => { delays++; return bodyOriginal; });
        var prepare = coordinator.PrepareRetryAsync(StopReason.Error, "503", false,
            () => { omits++; return kind == OriginalKind.Omission ? bodyOriginal : Task.CompletedTask; });
        var joined = coordinator.JoinAsync(); Exception? testFailure = null;
        try
        {
            Check(starts == 1 && !prepare.IsCompleted && !joined.IsCompleted);
            Check(omits == (kind == OriginalKind.Observer ? 0 : 1) && delays == (kind == OriginalKind.Delay ? 1 : 0));
            bodyFirst.TrySetException(bodyFaultA); Check(!bodyOriginal.IsCompleted && !prepare.IsCompleted);
            bodySecond.TrySetException(bodyFaultB); await endEntered.Task;
            Check(ends == 1 && !prepare.IsCompleted && !joined.IsCompleted);
            endFirst.TrySetException(endFaultA); Check(!endOriginal.IsCompleted && !prepare.IsCompleted);
            endSecond.TrySetException(endFaultB);
            var preparationLeaves = Leaves(await Failure(prepare)).ToArray(); var joinLeaves = Leaves(await Failure(joined)).ToArray();
            Check(preparationLeaves.Length == 4 && expected.All(marker => preparationLeaves.Any(value => ReferenceEquals(value, marker))));
            Check(joinLeaves.Length == 4 && expected.All(marker => joinLeaves.Any(value => ReferenceEquals(value, marker))));
            Check(!coordinator.IsRetrying && coordinator.Attempt == 0);
        }
        catch (Exception error) { testFailure = error; }
        finally
        {
            bodyFirst.TrySetException(bodyFaultA); bodySecond.TrySetException(bodyFaultB); endFirst.TrySetException(endFaultA); endSecond.TrySetException(endFaultB);
            await Join(testFailure, expected, bodyFirst.Task, bodySecond.Task, bodyOriginal, endFirst.Task, endSecond.Task, endOriginal, prepare, joined, coordinator.JoinAsync());
        }
    }
    private static async Task InjectionAdmission()
    {
        var calls = 0; Func<AgentRetryPolicy> policy = () => new();
        Func<SessionRetryEvent, Task> observer = _ => { calls++; return Task.CompletedTask; };
        Func<long, CancellationToken, Task> delay = (_, _) => { calls++; return Task.CompletedTask; };
        var multipleObservers = observer + observer; var multipleDelays = delay + delay; var multiplePolicies = policy + policy;
        RejectAdmission(() => new SessionRetryCoordinator(policy, multipleObservers));
        RejectAdmission(() => new SessionRetryCoordinator(policy, observer, multipleDelays));
        RejectAdmission(() => new SessionRetryCoordinator(multiplePolicies, observer));
        var coordinator = new SessionRetryCoordinator(policy, observer, delay);
        Func<Task> omit = () => { calls++; return Task.CompletedTask; }; var multipleOmits = omit + omit;
        RejectAdmission(() => coordinator.PrepareRetryAsync(StopReason.Error, "503", false, multipleOmits));
        Check(calls == 0 && coordinator.Attempt == 0 && !coordinator.IsRetrying); await coordinator.JoinAsync();
        await using var session = await Open(); session.ConfigureAutomaticRetry(new(enabled: false));
        RejectAdmission(() => session.ConfigureAutomaticRetry(new(), originalDelay: multipleDelays));
        Check(!session.AutoRetryEnabled && calls == 0);
    }
    private static async Task NestedBackoffs()
    {
        var first = Gate(); var second = Gate(); CancellationToken firstToken = default, secondToken = default;
        var fault = new IOException("nested external backoff callback"); var nested = false;
        var a = new SessionRetryCoordinator(() => new(), _ => Task.CompletedTask, (_, token) => { firstToken = token; return first.Task; });
        var b = new SessionRetryCoordinator(() => new(), _ => Task.CompletedTask, (_, token) => { secondToken = token; return second.Task; });
        var prepareA = a.PrepareRetryAsync(StopReason.Error, "503", false, () => Task.CompletedTask);
        var prepareB = b.PrepareRetryAsync(StopReason.Error, "503", false, () => Task.CompletedTask);
        Task? abortA = null, abortB = null; Exception? body = null;
        // These external registrations have no inherited owned AsyncLocal scope.
        using var inner = secondToken.UnsafeRegister(_ =>
        {
            Check(a.IsOwnedCallback && b.IsOwnedCallback); Reject(() => a.JoinAsync()); Reject(() => a.AbortRetryAsync());
            nested = true; throw fault;
        }, null);
        using var outer = firstToken.UnsafeRegister(_ =>
        { Check(a.IsOwnedCallback); abortB = b.AbortRetryAsync(); Check(a.IsOwnedCallback && !b.IsOwnedCallback); }, null);
        try
        {
            Check(a.IsRetrying && b.IsRetrying && !a.IsOwnedCallback && !b.IsOwnedCallback);
            abortA = a.AbortRetryAsync(); Check(nested && !abortA.IsCompleted && abortB is not null && !abortB.IsCompleted);
            Check(!a.IsOwnedCallback && !b.IsOwnedCallback); first.TrySetResult(); second.TrySetResult();
            Check(!await prepareA && ReferenceEquals(await Failure(prepareB), fault));
            await abortA; Check(ReferenceEquals(await Failure(abortB!), fault));
        }
        catch (Exception error) { body = error; }
        finally
        {
            first.TrySetResult(); second.TrySetResult();
            await Join(body, [fault], first.Task, second.Task, prepareA, prepareB, abortA ?? Task.CompletedTask, abortB ?? Task.CompletedTask, a.JoinAsync(), b.JoinAsync());
        }
    }
    private static async Task NestedSettings()
    {
        var a = await Open(); var b = await Open(); var first = Gate(); var second = Gate();
        CancellationToken firstToken = default, secondToken = default; var fault = new IOException("nested external settings callback"); var nested = false;
        a.ConfigureAutomaticRetry(new(), (_, token) => { firstToken = token; return first.Task; });
        b.ConfigureAutomaticRetry(new(), (_, token) => { secondToken = token; return second.Task; });
        using var cancelA = new CancellationTokenSource(); using var cancelB = new CancellationTokenSource();
        var writeA = a.SetAutoRetryEnabledAsync(false, cancelA.Token); var writeB = b.SetAutoRetryEnabledAsync(false, cancelB.Token);
        using var inner = secondToken.UnsafeRegister(_ =>
        {
            Check(a.IsRetryOwnedCallback && b.IsRetryOwnedCallback);
            Reject(() => a.WaitForIdleAsync()); Reject(() => a.StopAdmissionAndJoinAsync());
            nested = true; throw fault;
        }, null);
        using var outer = firstToken.UnsafeRegister(_ =>
        { Check(a.IsRetryOwnedCallback); cancelB.Cancel(); Check(a.IsRetryOwnedCallback && !b.IsRetryOwnedCallback); }, null);
        Exception? body = null;
        try
        {
            Check(!a.IsRetryOwnedCallback && !b.IsRetryOwnedCallback); cancelA.Cancel();
            Check(nested && !writeA.IsCompleted && !writeB.IsCompleted && a.AutoRetryEnabled && b.AutoRetryEnabled);
            Check(!a.IsRetryOwnedCallback && !b.IsRetryOwnedCallback); first.TrySetResult(); second.TrySetResult();
            await writeA; Check(ReferenceEquals(await Failure(writeB), fault));
            // Both physical acknowledgements succeeded; a cancellation callback fault does not undo persisted truth.
            Check(!a.AutoRetryEnabled && !b.AutoRetryEnabled);
        }
        catch (Exception error) { body = error; }
        finally
        {
            first.TrySetResult(); second.TrySetResult();
            await Join(body, [fault], first.Task, second.Task, writeA, writeB, a.StopAdmissionAndJoinAsync(), b.StopAdmissionAndJoinAsync(), DisposeExpected(a), DisposeExpected(b, fault));
        }
    }
    private static Task<PersistentAgentSession> Open()
    {
        var cwd = Path.GetFullPath(Path.GetTempPath()); var ids = 0;
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "retry-original-fixture", timestamp = "2026-10-05T00:00:00.000Z", cwd }));
        return PersistentAgentSession.CreateAsync(Path.Combine(cwd, "clean-retry-original-memory.jsonl"), header,
            new(new("clean-retry-original", "openai-responses", "authored"), new UnusedTransport(), []),
            () => 1000, () => "retry-original-" + Interlocked.Increment(ref ids), new(SessionLogStoreOptions: new(StorageFactory: new StorageFactory())));
    }
    private sealed class UnusedTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { yield return await Task.FromException<StreamEvent>(new InvalidOperationException("No provider operation admitted by original ownership controls.")); }
    }
    private sealed class StorageFactory : ISessionLogStorageFactory
    { public ValueTask<ISessionLogStorage> OpenAsync(string path, bool createNew, CancellationToken token) => ValueTask.FromResult<ISessionLogStorage>(new Storage()); }
    private sealed class Storage : ISessionLogStorage
    {
        private readonly MemoryStream bytes = new(); public Stream ReadStream => bytes;
        public SessionLogStorageDurability Durability => SessionLogStorageDurability.VolatileMemory; public long Length => bytes.Length;
        public void PositionForAppend(long length) { if (bytes.Length != length) throw new IOException("Original length changed."); bytes.Position = length; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> value) => bytes.WriteAsync(value); public ValueTask FlushAsync() => ValueTask.CompletedTask;
        public void FlushToDisk() { } public ValueTask BeforeCheckpointAsync() => ValueTask.CompletedTask; public ValueTask DisposeAsync() => bytes.DisposeAsync();
    }
}
