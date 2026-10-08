using System.Collections.Immutable;
using PiSharp.Tools.Processes.Mcp;

internal static class McpProcessLeaseTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mcp-process. admission rejects relative paths and invalid environment", Admission),
        ("mcp-process. close before start acquires nothing and is stable", CloseBeforeStart),
        ("mcp-process. transferred duplex streams and bounded stderr", Capture),
        ("mcp-process. close joins held start and stop originals", Held),
        ("mcp-process. close preserves stop and disposal faults", Failures),
        ("mcp-process. canceled launch is joined without replacement acquisition", Canceled),
        ("mcp-process. launcher lifecycle reentry rejects before mutation", LauncherReentry),
        ("mcp-process. stop lifecycle reentry rejects while independent close joins", StopReentry),
        ("mcp-process. disposal lifecycle reentry preserves repeated original fault", DisposalReentry)
    ];

    private static McpProcessAdmission Request(int cap = 8) => new(
        Path.Combine(Path.GetTempPath(), "admitted-fixture.exe"), [], Path.GetTempPath(),
        ImmutableDictionary<string, string>.Empty, cap);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("MCP process fixture assertion failed."); }
    private static Task Admission()
    {
        try { (Request() with { Executable = "relative.exe" }).Validate(); throw new Exception("accepted relative executable"); }
        catch (ArgumentException) { }
        try { (Request() with { Environment = ImmutableDictionary<string, string>.Empty.Add("bad=key", "x") }).Validate(); throw new Exception("accepted invalid environment"); }
        catch (ArgumentException) { }
        return Task.CompletedTask;
    }
    private static async Task CloseBeforeStart()
    {
        var calls = 0; var lease = new McpProcessLease(Request(), (_, _) => { calls++; throw new Exception("unexpected acquisition"); });
        var close = lease.CloseAsync(); Check(ReferenceEquals(close, lease.CloseAsync())); await close;
        Check(calls == 0);
        try { _ = lease.StartAsync(); throw new Exception("started closed lease"); } catch (InvalidOperationException) { }
    }
    private static async Task Capture()
    {
        var process = new Fake(); var lease = new McpProcessLease(Request(3), (_, _) => ValueTask.FromResult<IMcpOwnedDuplexProcess>(process));
        await lease.StartAsync(); Check(ReferenceEquals(lease.Input, process.Input) && ReferenceEquals(lease.Output, process.Output));
        await lease.CloseAsync(); Check(lease.CapturedStderr.SequenceEqual(new byte[] { 1, 2, 3 }) && lease.StderrTruncated);
        Check(process.StopCount == 1 && process.DisposeCount == 1);
    }
    private static async Task Held()
    {
        var entered = Gate(); var release = Gate(); var stopEntered = Gate(); var stopRelease = Gate();
        var process = new Fake { Stop = async () => { stopEntered.TrySetResult(); await stopRelease.Task; } };
        var lease = new McpProcessLease(Request(), async (_, _) => { entered.TrySetResult(); await release.Task; return process; });
        var start = lease.StartAsync(); var close = Task.CompletedTask;
        try
        {
            await entered.Task; close = lease.CloseAsync(); Check(!close.IsCompleted); release.TrySetResult();
            await stopEntered.Task; Check(!close.IsCompleted && ReferenceEquals(close, lease.CloseAsync()));
        }
        finally
        {
            release.TrySetResult(); stopRelease.TrySetResult();
            await JoinAll(start, close);
        }
    }
    private static async Task Failures()
    {
        var stop = new IOException("stop-original"); var dispose = new IOException("dispose-original");
        var process = new Fake { Stop = () => Task.FromException(stop), DisposalFailure = dispose };
        var lease = new McpProcessLease(Request(), (_, _) => ValueTask.FromResult<IMcpOwnedDuplexProcess>(process));
        await lease.StartAsync(); var close = lease.CloseAsync(); Exception? first = null;
        try { await close; } catch (AggregateException error) { first = error; Check(error.InnerExceptions.Contains(stop) && error.InnerExceptions.Contains(dispose)); }
        Check(first is not null && process.DisposeCount == 1 && ReferenceEquals(close, lease.CloseAsync()));
        try { await lease.CloseAsync(); } catch (Exception error) { Check(ReferenceEquals(first, error)); }
    }
    private static async Task Canceled()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var calls = 0;
        var lease = new McpProcessLease(Request(), (_, token) => { calls++; token.ThrowIfCancellationRequested(); throw new Exception("unexpected"); });
        var start = lease.StartAsync(cancel.Token);
        try { await start; } catch (OperationCanceledException) { }
        try { await lease.CloseAsync(); } catch (OperationCanceledException) { }
        Check(calls == 1);
    }
    private static void RejectLifecycle(McpProcessLease lease)
    {
        var startRejected = false; var closeRejected = false;
        try { _ = lease.StartAsync(); } catch (InvalidOperationException) { startRejected = true; }
        try { _ = lease.CloseAsync(); } catch (InvalidOperationException) { closeRejected = true; }
        Check(startRejected && closeRejected);
    }
    private static async Task LauncherReentry()
    {
        var entered = Gate(); var release = Gate(); var process = new Fake(); McpProcessLease? lease = null;
        lease = new McpProcessLease(Request(), async (_, _) =>
        {
            RejectLifecycle(lease!); entered.TrySetResult(); await release.Task;
            RejectLifecycle(lease!); return process;
        });
        var start = lease.StartAsync(); Task? close = null;
        try
        {
            await entered.Task; Check(ReferenceEquals(start, lease.StartAsync()));
            close = lease.CloseAsync(); Check(!close.IsCompleted && ReferenceEquals(close, lease.CloseAsync()));
        }
        finally { release.TrySetResult(); await JoinAll(start, close ?? lease.CloseAsync()); }
        Check(process.StopCount == 1 && process.DisposeCount == 1);
    }
    private static async Task StopReentry()
    {
        var entered = Gate(); var release = Gate(); McpProcessLease? lease = null;
        var process = new Fake { Stop = async () =>
        {
            RejectLifecycle(lease!); entered.TrySetResult(); await release.Task; RejectLifecycle(lease!);
        }};
        lease = new McpProcessLease(Request(), (_, _) => ValueTask.FromResult<IMcpOwnedDuplexProcess>(process));
        await lease.StartAsync(); var close = lease.CloseAsync();
        try { await entered.Task; Check(!close.IsCompleted && ReferenceEquals(close, lease.CloseAsync())); }
        finally { release.TrySetResult(); await close; }
        Check(process.StopCount == 1 && process.DisposeCount == 1);
    }
    private static async Task DisposalReentry()
    {
        var entered = Gate(); var release = Gate(); var original = new IOException("disposal-original"); McpProcessLease? lease = null;
        var process = new Fake { Dispose = async () =>
        {
            RejectLifecycle(lease!); entered.TrySetResult(); await release.Task; RejectLifecycle(lease!); throw original;
        }};
        lease = new McpProcessLease(Request(), (_, _) => ValueTask.FromResult<IMcpOwnedDuplexProcess>(process));
        await lease.StartAsync(); var close = lease.CloseAsync(); Exception? first = null;
        try { await entered.Task; Check(!close.IsCompleted && ReferenceEquals(close, lease.CloseAsync())); }
        finally
        {
            release.TrySetResult(); try { await close; } catch (Exception error) { first = error; }
        }
        Check(ReferenceEquals(first, original) && process.StopCount == 1 && process.DisposeCount == 1);
        try { await lease.CloseAsync(); } catch (Exception error) { Check(ReferenceEquals(first, error)); }
    }
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task JoinAll(params Task[] originals)
    {
        var faults = new List<Exception>();
        foreach (var original in originals) try { await original; } catch (Exception error) { faults.Add(error); }
        if (faults.Count > 0) throw new AggregateException(faults);
    }
    private sealed class Fake : IMcpOwnedDuplexProcess
    {
        public Stream Input { get; } = new MemoryStream();
        public Stream Output { get; } = new MemoryStream();
        public Stream Error { get; } = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        public Func<Task> Stop { get; init; } = () => Task.CompletedTask;
        public Exception? DisposalFailure { get; init; }
        public Func<ValueTask>? Dispose { get; init; }
        public int StopCount, DisposeCount;
        public Task StopAsync() { StopCount++; return Stop(); }
        public ValueTask DisposeAsync() { DisposeCount++; return Dispose is not null ? Dispose() : DisposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposalFailure); }
    }

    /// <summary>Unregistered native fixture. Qualification supplies an explicitly admitted echo
    /// command/environment and token; this method acquires no defaults or executable discovery.</summary>
    public static async Task NativeAdmittedRoundTrip(McpProcessAdmission admitted,
        ReadOnlyMemory<byte> input, ReadOnlyMemory<byte> expected, CancellationToken token)
    {
        var lease = new McpProcessLease(admitted); Task? start = null, write = null, read = null;
        var faults = new List<Exception>(); var received = new byte[expected.Length];
        try
        {
            start = lease.StartAsync(token); await start;
            read = lease.Output.ReadExactlyAsync(received, token).AsTask();
            write = Send();
            await JoinAll(write, read);
            Check(received.AsSpan().SequenceEqual(expected.Span));
            async Task Send()
            {
                await lease.Input.WriteAsync(input, token);
                await lease.Input.FlushAsync(token);
            }
        }
        catch (Exception error) { faults.Add(error); }
        finally
        {
            // Stop the actual tree before joining reads/writes that may be blocked in pipes.
            var close = lease.CloseAsync();
            foreach (var original in new[] { close, start, write, read })
                if (original is not null) try { await original; } catch (Exception error) { faults.Add(error); }
        }
        if (faults.Count > 0) throw new AggregateException(faults);
    }

    // Native qualification MUST separately admit executable/arguments/environment and execute through
    // the exact Windows owner: echo stdin to stdout, emit >cap stderr, child tree holding inherited pipes,
    // cancel during launch, exit during frame read, stop while writes/read callbacks are held, repeated
    // close fault identity. These definitions are deliberately NOT registered or executed here.
}
