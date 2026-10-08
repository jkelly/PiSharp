using System.Collections.Immutable;

namespace PiSharp.Tools.Processes.Mcp;

/// <summary>Explicit effect admission. The caller authorizes all values; no ambient environment is read.</summary>
public sealed record McpProcessAdmission(string Executable, ImmutableArray<string> Arguments,
    string WorkingDirectory, ImmutableDictionary<string, string> Environment,
    int MaximumStderrBytes = 1024 * 1024)
{
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(Executable) || !Path.IsPathFullyQualified(WorkingDirectory) ||
            Arguments.IsDefault || MaximumStderrBytes is < 1 or > 1024 * 1024 ||
            Executable.Contains('\0') || WorkingDirectory.Contains('\0') ||
            Arguments.Any(a => a is null || a.Contains('\0')) ||
            Environment is null || Environment.Any(e => string.IsNullOrEmpty(e.Key) ||
                e.Key.Contains('=') || e.Key.Contains('\0') || e.Value is null || e.Value.Contains('\0')))
            throw new ArgumentException("Invalid explicit MCP process admission.");
    }
}

/// <summary>Transferred process ownership. Stop must release readers before disposal joins them.</summary>
public interface IMcpOwnedDuplexProcess : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Stream Error { get; }
    Task StopAsync();
}

public delegate ValueTask<IMcpOwnedDuplexProcess> McpProcessLauncher(McpProcessAdmission admission,
    CancellationToken cancellationToken);

/// <summary>One independently closeable lease. Readers/writers are borrowed by the protocol adapter.
/// The adapter must stop this owner before joining its own pipe operations.</summary>
public sealed class McpProcessLease
{
    private readonly object _gate = new();
    private readonly McpProcessAdmission _admission;
    private readonly McpProcessLauncher _launch;
    private readonly CancellationTokenSource _launchStop = new();
    private readonly AsyncLocal<int> _dependencyDepth = new();
    private Task? _start, _close, _stderr;
    private IMcpOwnedDuplexProcess? _process;
    private readonly MemoryStream _capture = new();
    private bool _stderrTruncated;

    public McpProcessLease(McpProcessAdmission admission, McpProcessLauncher? launcher = null)
    {
        admission.Validate(); _admission = admission; _launch = launcher ?? LaunchNativeAsync;
    }

    public Stream Input { get { lock (_gate) return Ready().Input; } }
    public Stream Output { get { lock (_gate) return Ready().Output; } }
    public byte[] CapturedStderr { get { lock (_gate) return _capture.ToArray(); } }
    public bool StderrTruncated { get { lock (_gate) return _stderrTruncated; } }
    private IMcpOwnedDuplexProcess Ready() => _close is null && _start?.IsCompletedSuccessfully == true
        ? _process! : throw new InvalidOperationException("MCP process lease is not started or is closing.");

    public Task StartAsync(CancellationToken token = default)
    {
        RejectDependencyReentry();
        lock (_gate)
        {
            if (_close is not null) throw new InvalidOperationException("MCP process lease is closing.");
            return _start ??= Task.Run(() => StartCoreAsync(token));
        }
    }

    private async Task StartCoreAsync(CancellationToken token)
    {
        using var dependency = new DependencyScope(_dependencyDepth);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _launchStop.Token);
        var process = await _launch(_admission, linked.Token).ConfigureAwait(false);
        lock (_gate) { _process = process; _stderr = DrainStderrAsync(process.Error); }
    }

    private async Task DrainStderrAsync(Stream error)
    {
        var bytes = new byte[8192];
        while (true)
        {
            var count = await error.ReadAsync(bytes).ConfigureAwait(false);
            if (count == 0) return;
            lock (_gate)
            {
                var remaining = _admission.MaximumStderrBytes - checked((int)_capture.Length);
                _capture.Write(bytes, 0, Math.Min(count, remaining));
                _stderrTruncated |= count > remaining;
            }
        }
    }

    public Task CloseAsync()
    {
        RejectDependencyReentry();
        lock (_gate) return _close ??= Task.Run(CloseCoreAsync);
    }

    private void RejectDependencyReentry()
    {
        if (_dependencyDepth.Value != 0)
            throw new InvalidOperationException("MCP lease lifecycle cannot reenter from its borrowed owner dependencies.");
    }

    // Execution-context-local and lease-specific: independent callers can still initiate/join close.
    // Scope covers invocation AND settlement, including continuations and cancellation registrations.
    private sealed class DependencyScope : IDisposable
    {
        private readonly AsyncLocal<int> _depth;
        private readonly int _previous;
        public DependencyScope(AsyncLocal<int> depth) { _depth = depth; _previous = depth.Value; depth.Value = _previous + 1; }
        public void Dispose() => _depth.Value = _previous;
    }

    private async Task CloseCoreAsync()
    {
        var faults = new List<Exception>();
        var cancel = _launchStop.CancelAsync();
        Task? start; lock (_gate) start = _start;
        if (start is not null) await Join(start).ConfigureAwait(false);
        IMcpOwnedDuplexProcess? process; Task? stderr;
        lock (_gate) { process = _process; stderr = _stderr; }
        if (process is not null)
        {
            using var dependency = new DependencyScope(_dependencyDepth);
            // Initiate stop before joining cancellation callbacks or any pipe read original.
            Task stop;
            try { stop = process.StopAsync(); } catch (Exception error) { faults.Add(error); stop = Task.CompletedTask; }
            await Join(stop).ConfigureAwait(false);
            // Confirmed stop gives stderr EOF. Drain before closing its handle so normal shutdown
            // cannot convert an in-flight capture read into an ObjectDisposedException.
            if (stop.IsCompletedSuccessfully && stderr is not null)
            { await Join(stderr).ConfigureAwait(false); stderr = null; }
            // Disposal closes owned pipe handles even when termination reports a failure.
            try { await process.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { faults.Add(error); }
        }
        await Join(cancel).ConfigureAwait(false);
        if (stderr is not null) await Join(stderr).ConfigureAwait(false);
        _launchStop.Dispose();
        if (faults.Count == 1) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(faults[0]).Throw();
        if (faults.Count > 1) throw new AggregateException(faults);
        async Task Join(Task original) { try { await original.ConfigureAwait(false); } catch (Exception error) { faults.Add(error); } }
    }

    private static async ValueTask<IMcpOwnedDuplexProcess> LaunchNativeAsync(McpProcessAdmission admission, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("MCP process ownership requires Windows.");
        var lifetime = await McpWindowsProcessLifetime.StartAsync(new(admission.Executable, admission.Arguments,
            admission.WorkingDirectory, admission.Environment, string.Empty), null, token).ConfigureAwait(false);
        return new NativeOwnedProcess(lifetime);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class NativeOwnedProcess(McpWindowsProcessLifetime lifetime) : IMcpOwnedDuplexProcess
    {
        public Stream Input => lifetime.StandardInput;
        public Stream Output => lifetime.StandardOutput;
        public Stream Error => lifetime.StandardError;
        private Task? _stop;
        private readonly object _gate = new();
        public Task StopAsync() { lock (_gate) return _stop ??= StopCoreAsync(); }
        private async Task StopCoreAsync()
        {
            if (!await lifetime.TerminateAndConfirmAsync().ConfigureAwait(false))
                throw new IOException("MCP process tree termination could not be confirmed.");
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.DisposeAsync().ConfigureAwait(false);
            if (!lifetime.CleanupConfirmed) throw new IOException("MCP process owner cleanup could not be confirmed.");
        }
    }
}
