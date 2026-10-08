using System.Runtime.ExceptionServices;
using PiSharp.CodingAgent;
using PiSharp.CodingAgent.Configuration;
using PiSharp.Contracts;

internal static class CleanRetryCoordinatorTests
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("clean retry coordinator start omission delay and terminal originals order", Ordered),
        ("clean retry coordinator abort joins held original delay and terminal observer", AbortOriginals),
        ("clean retry coordinator dynamic budget exclusion exhaustion and assistant reset", Budget),
        ("clean retry coordinator original body and final observer faults retain references", Faults),
        ("clean retry coordinator external unsafe cancellation reentry and fault stay owned", Cancellation)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Clean retry coordinator contract failed."); }
    private static async Task<Exception> Failure(Task original)
    { try { await original; } catch (Exception error) { return error; } throw new InvalidOperationException("Expected original fault."); }
    private static IEnumerable<Exception> Leaves(Exception error) => error is AggregateException aggregate ? aggregate.InnerExceptions.SelectMany(Leaves) : [error];
    private static void Reject(Action enter)
    { var rejected = false; try { enter(); } catch (InvalidOperationException) { rejected = true; } Check(rejected); }
    private static async Task Join(Exception? body, Func<Exception, bool>? expected, params Task[] originals)
    {
        var errors = new List<Exception>(); if (body is not null) errors.Add(body);
        foreach (var original in originals.Distinct())
            try { await original; } catch (Exception error) { if (!(expected?.Invoke(error) ?? false) && !errors.Any(value => ReferenceEquals(value, error))) errors.Add(error); }
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count != 0) throw new AggregateException(errors);
    }
    private static async Task Ordered()
    {
        var start = Gate(); var omission = Gate(); var sleep = Gate(); var omissionEntered = Gate(); var sleepEntered = Gate();
        var ended = Gate(); var endEntered = Gate(); var events = new List<SessionRetryEvent>();
        SessionRetryCoordinator? driver = null;
        driver = new(() => AgentRetryPolicy.Default, value =>
        {
            events.Add(value); var owned = driver ?? throw new InvalidOperationException("Coordinator not assigned."); Check(owned.IsOwnedCallback);
            Reject(() => owned.JoinAsync()); Reject(() => owned.AbortRetryAsync());
            if (value is SessionRetryStarted) return start.Task;
            endEntered.TrySetResult(); return ended.Task;
        }, (_, _) => { sleepEntered.TrySetResult(); return sleep.Task; });
        var prepare = driver.PrepareRetryAsync(StopReason.Error, "503", false, () => { omissionEntered.TrySetResult(); return omission.Task; });
        Task? finish = null; Exception? body = null;
        try
        {
            Check(!driver.IsRetrying && !omissionEntered.Task.IsCompleted && !prepare.IsCompleted);
            start.TrySetResult(); await omissionEntered.Task;
            Check(!driver.IsRetrying && !sleepEntered.Task.IsCompleted && !prepare.IsCompleted);
            omission.TrySetResult(); await sleepEntered.Task; Check(driver.IsRetrying);
            sleep.TrySetResult(); Check(await prepare && !driver.IsRetrying);
            finish = driver.CompleteAssistantAsync(StopReason.ToolUse); await endEntered.Task;
            Check(!finish.IsCompleted && driver.Attempt == 0);
            ended.TrySetResult(); await finish;
            Check(events is [SessionRetryStarted { Attempt: 1, MaxAttempts: 3, DelayMs: 2000 }, SessionRetryEnded { Success: true, Attempt: 1, FinalError: null }]);
        }
        catch (Exception error) { body = error; }
        finally { start.TrySetResult(); omission.TrySetResult(); sleep.TrySetResult(); ended.TrySetResult(); await Join(body, null, prepare, finish ?? Task.CompletedTask, driver.JoinAsync()); }
    }
    private static async Task AbortOriginals()
    {
        var sleep = Gate(); var entered = Gate(); var terminal = Gate(); var terminalEntered = Gate(); CancellationToken admittedToken = default;
        var driver = new SessionRetryCoordinator(() => AgentRetryPolicy.Default, value =>
        { if (value is SessionRetryEnded ended) { Check(!ended.Success && ended.FinalError == "Retry cancelled"); terminalEntered.TrySetResult(); return terminal.Task; } return Task.CompletedTask; },
            (_, token) => { admittedToken = token; entered.TrySetResult(); return sleep.Task; });
        var prepare = driver.PrepareRetryAsync(StopReason.Error, "overloaded", false, () => Task.CompletedTask);
        Task? abort = null; Exception? body = null;
        try
        {
            await entered.Task; abort = driver.AbortRetryAsync();
            Check(admittedToken.IsCancellationRequested && ReferenceEquals(abort, driver.AbortRetryAsync()) && !prepare.IsCompleted && !abort.IsCompleted);
            sleep.TrySetResult(); await terminalEntered.Task;
            Check(driver.IsRetrying && !prepare.IsCompleted && !abort.IsCompleted);
            terminal.TrySetResult(); Check(!await prepare); await abort;
            Check(!driver.IsRetrying && ReferenceEquals(abort, driver.AbortRetryAsync()));
        }
        catch (Exception error) { body = error; }
        finally { sleep.TrySetResult(); terminal.TrySetResult(); await Join(body, null, prepare, abort ?? Task.CompletedTask, driver.JoinAsync()); }
    }
    private static async Task Budget()
    {
        var settings = new AgentRetryPolicy(maxRetries: 1, baseDelayMs: 0); var events = new List<SessionRetryEvent>(); var omissions = 0;
        var driver = new SessionRetryCoordinator(() => settings, value => { events.Add(value); return Task.CompletedTask; }, (_, _) => Task.CompletedTask);
        Task Omit() { omissions++; return Task.CompletedTask; }
        Check(!await driver.PrepareRetryAsync(StopReason.Error, "503 context", true, Omit));
        Check(!await driver.PrepareRetryAsync(StopReason.Error, "billing 503", false, Omit));
        settings = new(false); Check(!await driver.PrepareRetryAsync(StopReason.Error, "503", false, Omit));
        settings = new(maxRetries: 1, baseDelayMs: 0);
        Check(await driver.PrepareRetryAsync(StopReason.Error, "503", false, Omit));
        Check(!await driver.PrepareRetryAsync(StopReason.Error, "503", false, Omit));
        await driver.FinishAsync(StopReason.Error, "503 final");
        Check(events is [SessionRetryStarted { Attempt: 1 }, SessionRetryEnded { Success: false, Attempt: 1, FinalError: "503 final" }] && omissions == 1);
        events.Clear(); Check(await driver.PrepareRetryAsync(StopReason.Error, "503", false, Omit));
        await driver.CompleteAssistantAsync(StopReason.ToolUse);
        Check(await driver.PrepareRetryAsync(StopReason.Error, "503", false, Omit));
        await driver.CompleteAssistantAsync(StopReason.Stop); await driver.JoinAsync();
        Check(events.OfType<SessionRetryStarted>().All(value => value.Attempt == 1) && omissions == 3);
        events.Clear(); Check(await driver.PrepareRetryAsync(StopReason.Error, "503", false, Omit));
        await driver.FinishCancelledAsync();
        Check(events is [SessionRetryStarted { Attempt: 1 }, SessionRetryEnded { Success: false, Attempt: 1, FinalError: "Retry cancelled" }]);
    }
    private static async Task Faults()
    {
        var original = new IOException("original omission body"); var cleanup = new IOException("original terminal observer");
        var omission = Gate(); var end = Gate(); var endEntered = Gate();
        var driver = new SessionRetryCoordinator(() => AgentRetryPolicy.Default, value =>
        { if (value is SessionRetryEnded) { endEntered.TrySetResult(); return end.Task; } return Task.CompletedTask; }, (_, _) => throw new Exception("Delay cannot begin after failed omission."));
        var prepare = driver.PrepareRetryAsync(StopReason.Error, "503", false, () => omission.Task); Exception? body = null;
        try
        {
            omission.TrySetException(original); await endEntered.Task; Check(!prepare.IsCompleted && !driver.IsRetrying);
            end.TrySetException(cleanup); var failure = await Failure(prepare); var leaves = Leaves(failure).ToArray();
            Check(leaves.Length == 2 && ReferenceEquals(leaves[0], original) && ReferenceEquals(leaves[1], cleanup));
        }
        catch (Exception error) { body = error; }
        finally
        {
            omission.TrySetException(original); end.TrySetException(cleanup);
            await Join(body, error => Leaves(error).All(value => ReferenceEquals(value, original) || ReferenceEquals(value, cleanup)), prepare, driver.JoinAsync());
        }
    }
    private static async Task Cancellation()
    {
        var sleep = Gate(); var entered = Gate(); var canceled = new IOException("original cancellation callback");
        var terminal = Gate(); var endEntered = Gate(); var endFault = new IOException("original end observer"); CancellationToken token = default;
        var driver = new SessionRetryCoordinator(() => AgentRetryPolicy.Default, value =>
        { if (value is SessionRetryEnded) { endEntered.TrySetResult(); return terminal.Task; } return Task.CompletedTask; },
            (_, cancellation) => { token = cancellation; entered.TrySetResult(); return sleep.Task; });
        var prepare = driver.PrepareRetryAsync(StopReason.Error, "503", false, () => Task.CompletedTask);
        await entered.Task;
        using var registration = token.UnsafeRegister(_ =>
        { Check(driver.IsOwnedCallback); Reject(() => driver.AbortRetryAsync()); Reject(() => driver.JoinAsync()); throw canceled; }, null);
        var abort = driver.AbortRetryAsync(); Exception? body = null;
        try
        {
            Check(!abort.IsCompleted && !prepare.IsCompleted); sleep.TrySetResult(); await endEntered.Task;
            Check(!abort.IsCompleted); terminal.TrySetException(endFault);
            var failure = await Failure(prepare); var leaves = Leaves(failure).ToArray();
            Check(leaves.Length == 2 && leaves.Any(value => ReferenceEquals(value, canceled)) && leaves.Any(value => ReferenceEquals(value, endFault)));
            Check(ReferenceEquals(await Failure(abort), failure) && ReferenceEquals(abort, driver.AbortRetryAsync()));
        }
        catch (Exception error) { body = error; }
        finally
        {
            sleep.TrySetResult(); terminal.TrySetException(endFault);
            await Join(body, error => Leaves(error).All(value => ReferenceEquals(value, canceled) || ReferenceEquals(value, endFault)), prepare, abort, driver.JoinAsync());
        }
    }
}
