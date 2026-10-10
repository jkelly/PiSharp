// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/utils/output-files.ts (LocalProcessOutputStorage).
using System.Collections.Immutable;
using System.Threading.Channels;
using PiSharp.Agent.Tools;

namespace PiSharp.Tools.Processes;

public enum ProcessRunStatus { Exited, NonZeroExit, Canceled, TimedOut, Failed }
public enum ProcessDiagnostic
{
    InvalidRequest, UnsupportedPlatform, ExecutableUnavailable, WorkingDirectoryUnavailable,
    SpillDirectoryUnavailable, SpawnFailed, OutputLimitExceeded, OutputIoFailed,
    ProgressCallbackFailed, LifecycleCallbackFailed, CleanupFailed
}

/// <summary>Trusted effect input. This primitive does not authorize commands or enforce a sandbox.</summary>
public sealed record ProcessRequest(string Executable, ImmutableArray<string> Arguments,
    string WorkingDirectory, ImmutableDictionary<string, string> Environment, string SpillPath,
    double? TimeoutSeconds = null)
{
    /// <summary>MCP process launch only: a command line tail used as is instead of the quoted <see cref="Arguments"/>.</summary>
    public string? VerbatimArguments { get; init; }
    /// <summary>Bytes written to the child's standard input before it is closed (source commandTransport "stdin"). Null
    /// gives the child an empty standard input.</summary>
    public byte[]? StandardInput { get; init; }
}

public sealed record ProcessRunnerOptions(int MaximumRawBytes = 64 * 1024 * 1024,
    int ModelMaxLines = 2000, int ModelMaxBytes = 50 * 1024,
    int StructuredMaxBytes = 1024 * 1024, int ReadChunkBytes = 8192,
    int QueueChunks = 8, int PostExitIdleMilliseconds = 100);

public sealed record ProcessOutputSnapshot(string Content, ToolOutputTruncationResult Truncation,
    int RawBytes, int LastLineBytes, string? FullOutputPath);
public sealed record ProcessStructuredOutput(string Content, bool Truncated);
public sealed record ProcessRunResult(ProcessRunStatus Status, int? ExitCode, int? ProcessId,
    bool ProcessStarted, bool CleanupConfirmed, bool CapturedOutputComplete,
    ProcessOutputSnapshot Output, ProcessStructuredOutput StructuredOutput,
    double WallTimeSeconds, ImmutableArray<ProcessDiagnostic> Diagnostics)
{
    /// <summary>The Win32 error when CreateProcess refused the executable (the launch never started).</summary>
    public int? LaunchError { get; init; }
}

public delegate ValueTask ProcessOutputCallback(ProcessOutputSnapshot snapshot);
/// <summary>Raw stdout/stderr bytes in arrival order (source onData), awaited before the next chunk is collected.</summary>
public delegate ValueTask ProcessRawOutputCallback(ReadOnlyMemory<byte> chunk);
public enum ProcessLifecycleStage { BeforeResume, Started, BeforeCleanup, AfterCleanup }
public sealed record ProcessLifecycleObservation(ProcessLifecycleStage Stage, int ProcessId,
    bool ProcessStarted, bool CleanupConfirmed);
/// <summary>Trusted, awaited test/host observation. Callbacks run outside coordination locks.</summary>
public delegate ValueTask ProcessLifecycleCallback(ProcessLifecycleObservation observation);

public interface IProcessRunner
{
    ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Trusted output I/O seam. The returned stream is owned and must await its asynchronous cleanup.</summary>
public interface IProcessOutputStorage
{
    ValueTask<Stream> CreateNewAsync(string absolutePath);
}

/// <summary>
/// Source output files (utils/output-files.ts): output can carry private data, so a spill file is created exclusively
/// (never following or reusing an existing path or link) and, on Unix, readable and writable by its owner only (0600).
/// Windows has no POSIX mode: the file inherits the spill directory's ACL, as Node ignores the mode there too.
/// </summary>
public sealed class LocalProcessOutputStorage : IProcessOutputStorage
{
    public ValueTask<Stream> CreateNewAsync(string absolutePath) => ValueTask.FromResult<Stream>(new FileStream(absolutePath, Options()));

    internal static FileStreamOptions Options()
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, BufferSize = 8192,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
}

/// <summary>
/// Windows-only, bounded real process primitive. Completion follows job termination, pipe settlement,
/// output callbacks and owned output cleanup. Host tools must separately apply final-action policy.
/// </summary>
public sealed class NativeProcessRunner : ISeparatedProcessRunner
{
    private readonly ProcessRunnerOptions _options;
    private readonly TimeProvider _clock;
    private readonly IProcessOutputStorage _storage;
    private readonly ProcessLifecycleCallback? _lifecycle;

    public NativeProcessRunner(ProcessRunnerOptions? options = null, TimeProvider? timeProvider = null,
        IProcessOutputStorage? outputStorage = null, ProcessLifecycleCallback? lifecycle = null)
    {
        _options = options ?? new();
        if (_options.MaximumRawBytes < 1 ||
            _options.ModelMaxLines is < 1 or > 2000 || _options.ModelMaxBytes is < 1 or > 50 * 1024 ||
            _options.StructuredMaxBytes < _options.ModelMaxBytes || _options.StructuredMaxBytes > 1024 * 1024 ||
            _options.ReadChunkBytes is < 1 or > 64 * 1024 || _options.QueueChunks is < 1 or > 64 ||
            _options.PostExitIdleMilliseconds is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid process output limits.");
        _clock = timeProvider ?? TimeProvider.System;
        _storage = outputStorage ?? new LocalProcessOutputStorage();
        _lifecycle = lifecycle;
    }

    public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onUpdate = null,
        CancellationToken cancellationToken = default) => RunCoreAsync(request, onUpdate, null, cancellationToken);

    /// <summary>Runs like <see cref="RunAsync"/> and also delivers each raw output chunk, for hosts that sanitize streamed text themselves.</summary>
    public ValueTask<ProcessRunResult> RunStreamingAsync(ProcessRequest request, ProcessRawOutputCallback onData,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onData);
        return RunCoreAsync(request, null, null, cancellationToken, onData);
    }

    public async ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        using var capture = new SeparatedProcessCapture(_options.StructuredMaxBytes);
        var original = await RunCoreAsync(request, null, capture, cancellationToken).ConfigureAwait(false);
        return capture.Result(original);
    }

    private async ValueTask<ProcessRunResult> RunCoreAsync(ProcessRequest request, ProcessOutputCallback? onUpdate,
        SeparatedProcessCapture? capture, CancellationToken cancellationToken, ProcessRawOutputCallback? onData = null)
    {
        var began = _clock.GetTimestamp();
        if (cancellationToken.IsCancellationRequested) return Empty(ProcessRunStatus.Canceled, null);
        if (!OperatingSystem.IsWindows()) return Empty(ProcessRunStatus.Failed, ProcessDiagnostic.UnsupportedPlatform);
        ProcessDiagnostic? invalid;
        try { invalid = Validate(request); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { invalid = ProcessDiagnostic.InvalidRequest; }
        if (invalid is { } diagnostic) return Empty(ProcessRunStatus.Failed, diagnostic);
        return await RunOwnedAsync(request, onUpdate, capture, cancellationToken, began, onData).ConfigureAwait(false);

        ProcessRunResult Empty(ProcessRunStatus status, ProcessDiagnostic? diagnostic)
        {
            var empty = ToolOutputTruncator.Tail(string.Empty, new(_options.ModelMaxLines, _options.ModelMaxBytes));
            return new(status, null, null, false, true, true,
                new(string.Empty, empty, 0, 0, null), new(string.Empty, false), 0,
                diagnostic is { } code ? [code] : []);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async ValueTask<ProcessRunResult> RunOwnedAsync(ProcessRequest request, ProcessOutputCallback? onUpdate,
        SeparatedProcessCapture? capture, CancellationToken caller, long began, ProcessRawOutputCallback? onData)
    {
        var diagnostics = new List<ProcessDiagnostic>();
        var diagnosticGate = new object();
        var stop = new TaskCompletionSource<ProcessRunStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new ShellOutputAccumulator(request.SpillPath, _options, _storage);
        var captureComplete = true;
        // Acquire the trusted timer before creating an OS process. It stays disarmed during launch.
        ITimer? timer;
        try
        {
            timer = request.TimeoutSeconds is not null
                ? _clock.CreateTimer(_ => stop.TrySetResult(ProcessRunStatus.TimedOut), null,
                    Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)
                : null;
        }
        catch (Exception)
        {
            Add(ProcessDiagnostic.LifecycleCallbackFailed);
            await output.DisposeAsync().ConfigureAwait(false);
            return Result(ProcessRunStatus.Failed, null, null, false, true);
        }
        using var timeoutTimer = timer;
        WindowsProcessLifetime lifetime;
        try
        {
            lifetime = await WindowsProcessLifetime.StartAsync(request, _lifecycle, caller).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        { await output.DisposeAsync().ConfigureAwait(false); return Result(ProcessRunStatus.Canceled, null, null, false, true); }
        catch (ProcessLaunchException error)
        {
            Add(error.Diagnostic); await output.DisposeAsync().ConfigureAwait(false);
            return Result(ProcessRunStatus.Failed, null, null, false, error.CleanupConfirmed) with { LaunchError = error.NativeError };
        }
        catch (Exception)
        { Add(ProcessDiagnostic.SpawnFailed); await output.DisposeAsync().ConfigureAwait(false); return Result(ProcessRunStatus.Failed, null, null, false, true); }

        using var reads = new CancellationTokenSource();
        using var callerRegistration = caller.Register(() => stop.TrySetResult(ProcessRunStatus.Canceled));
        var chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_options.QueueChunks)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
        var activity = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
        var lastActivity = _clock.GetTimestamp();
        var status = ProcessRunStatus.Exited;
        var cleanupConfirmed = false;
        int? exitCode = null;
        var activeCallback = onUpdate;
        var collect = CollectAsync();
        var pumps = CompletePumpsAsync();
        var drained = WaitForDrainAsync();
        try
        {
            if (request.TimeoutSeconds is { } seconds)
                timeoutTimer!.Change(TimeSpan.FromMilliseconds(seconds * 1000), Timeout.InfiniteTimeSpan);
            await ObserveAsync(ProcessLifecycleStage.Started).ConfigureAwait(false);
            var winner = await Task.WhenAny(stop.Task, drained).ConfigureAwait(false);
            if (winner == stop.Task) status = await stop.Task.ConfigureAwait(false);
            else await drained.ConfigureAwait(false);
        }
        catch (Exception) { Add(ProcessDiagnostic.LifecycleCallbackFailed); status = ProcessRunStatus.Failed; }

        try { await ObserveAsync(ProcessLifecycleStage.BeforeCleanup).ConfigureAwait(false); }
        catch (Exception) { Add(ProcessDiagnostic.LifecycleCallbackFailed); status = ProcessRunStatus.Failed; }
        cleanupConfirmed = await lifetime.TerminateAndConfirmAsync().ConfigureAwait(false);
        if (!cleanupConfirmed) Add(ProcessDiagnostic.CleanupFailed);
        // Owned overlapped pipe reads are cancelable even if an unsupported outside-job process retained a handle.
        try { await pumps.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception) { captureComplete = false; Add(ProcessDiagnostic.CleanupFailed); reads.Cancel(); }
        await pumps.ConfigureAwait(false);
        await drained.ConfigureAwait(false);
        await collect.ConfigureAwait(false);
        var beforeFinish = output.Snapshot();
        try
        {
            await output.FinishAsync().ConfigureAwait(false);
            var finalSnapshot = output.Snapshot();
            if (activeCallback is not null && finalSnapshot != beforeFinish)
            {
                try { await activeCallback(finalSnapshot).ConfigureAwait(false); }
                catch (Exception) { Add(ProcessDiagnostic.ProgressCallbackFailed); }
            }
        }
        catch (Exception) { captureComplete = false; Add(ProcessDiagnostic.OutputIoFailed); }
        try { await output.DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { captureComplete = false; Add(ProcessDiagnostic.CleanupFailed); }
        await lifetime.DisposeAsync().ConfigureAwait(false);
        cleanupConfirmed &= lifetime.CleanupConfirmed;
        if (!cleanupConfirmed) Add(ProcessDiagnostic.CleanupFailed);
        if (lifetime.Exit.IsCompletedSuccessfully) exitCode = lifetime.Exit.Result;
        if (exitCode is null) Add(ProcessDiagnostic.SpawnFailed);
        cleanupConfirmed &= !diagnostics.Contains(ProcessDiagnostic.CleanupFailed);
        try { await ObserveAsync(ProcessLifecycleStage.AfterCleanup).ConfigureAwait(false); }
        catch (Exception) { Add(ProcessDiagnostic.LifecycleCallbackFailed); }

        if (diagnostics.Contains(ProcessDiagnostic.CleanupFailed)) status = ProcessRunStatus.Failed;
        else if (caller.IsCancellationRequested) status = ProcessRunStatus.Canceled;
        else if (stop.Task.IsCompletedSuccessfully && stop.Task.Result == ProcessRunStatus.TimedOut) status = ProcessRunStatus.TimedOut;
        else if (diagnostics.Count > 0) status = ProcessRunStatus.Failed;
        else status = exitCode == 0 ? ProcessRunStatus.Exited : ProcessRunStatus.NonZeroExit;
        return Result(status, exitCode, lifetime.ProcessId, lifetime.Started, cleanupConfirmed);

        void Add(ProcessDiagnostic code)
        {
            lock (diagnosticGate) if (!diagnostics.Contains(code)) diagnostics.Add(code);
        }
        ProcessRunResult Result(ProcessRunStatus outcome, int? code, int? pid, bool started, bool cleaned) =>
            new(outcome, code, pid, started, cleaned, captureComplete,
                output.Snapshot(), output.Structured(),
                Math.Floor(_clock.GetElapsedTime(began).TotalSeconds * 10 + 0.5) / 10,
                diagnostics.ToImmutableArray());
        ValueTask ObserveAsync(ProcessLifecycleStage stage) => _lifecycle is null ? ValueTask.CompletedTask :
            _lifecycle(new(stage, lifetime.ProcessId, lifetime.Started, cleanupConfirmed));

        async Task PumpAsync(Stream stream, bool standardError)
        {
            try
            {
                while (true)
                {
                    var bytes = new byte[_options.ReadChunkBytes];
                    var count = await stream.ReadAsync(bytes, reads.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (count != bytes.Length) Array.Resize(ref bytes, count);
                    capture?.Append(standardError, bytes);
                    Interlocked.Exchange(ref lastActivity, _clock.GetTimestamp());
                    activity.Writer.TryWrite(0);
                    await chunks.Writer.WriteAsync(bytes, reads.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (reads.IsCancellationRequested) { }
            catch (Exception)
            { captureComplete = false; Add(ProcessDiagnostic.OutputIoFailed); stop.TrySetResult(ProcessRunStatus.Failed); }
        }
        async Task CompletePumpsAsync()
        {
            try { await Task.WhenAll(PumpAsync(lifetime.StandardOutput, false), PumpAsync(lifetime.StandardError, true)).ConfigureAwait(false); }
            finally { chunks.Writer.TryComplete(); }
        }
        async Task CollectAsync()
        {
            var accepting = true;
            await foreach (var bytes in chunks.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (!accepting) continue;
                try { await output.AppendAsync(bytes).ConfigureAwait(false); }
                catch (ProcessOutputLimitException)
                { captureComplete = false; Add(ProcessDiagnostic.OutputLimitExceeded); accepting = false; stop.TrySetResult(ProcessRunStatus.Failed); }
                catch (Exception)
                { captureComplete = false; Add(ProcessDiagnostic.OutputIoFailed); accepting = false; stop.TrySetResult(ProcessRunStatus.Failed); }
                if (accepting && onData is not null)
                {
                    try { await onData(bytes).ConfigureAwait(false); }
                    catch (Exception)
                    { Add(ProcessDiagnostic.ProgressCallbackFailed); accepting = false; stop.TrySetResult(ProcessRunStatus.Failed); }
                }
                if (activeCallback is not null)
                {
                    try { await activeCallback(output.Snapshot()).ConfigureAwait(false); }
                    catch (Exception)
                    { Add(ProcessDiagnostic.ProgressCallbackFailed); activeCallback = null; stop.TrySetResult(ProcessRunStatus.Failed); }
                }
            }
        }
        async Task WaitForDrainAsync()
        {
            await lifetime.Exit.ConfigureAwait(false);
            Interlocked.Exchange(ref lastActivity, _clock.GetTimestamp());
            while (!pumps.IsCompleted)
            {
                var elapsed = _clock.GetElapsedTime(Interlocked.Read(ref lastActivity));
                var remaining = TimeSpan.FromMilliseconds(_options.PostExitIdleMilliseconds) - elapsed;
                if (remaining <= TimeSpan.Zero) return;
                using var turn = new CancellationTokenSource();
                var idle = Task.Delay(remaining, _clock, turn.Token);
                var next = activity.Reader.ReadAsync(turn.Token).AsTask();
                await Task.WhenAny(pumps, idle, next).ConfigureAwait(false);
                turn.Cancel();
                try { await idle.ConfigureAwait(false); } catch (OperationCanceledException) { }
                try { await next.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
    }

    private static ProcessDiagnostic? Validate(ProcessRequest? request)
    {
        if (request is null || request.Arguments.IsDefault || request.Arguments.Length > 256 || request.Environment is null ||
            request.Environment.Count > 1024 || !Absolute(request.Executable) || !Absolute(request.WorkingDirectory) ||
            !Absolute(request.SpillPath) || request.Executable.Length > 4096 || request.WorkingDirectory.Length > 4096 ||
            request.SpillPath.Length > 4096 || request.StandardInput is { Length: > 16 * 1024 * 1024 } || request.TimeoutSeconds is { } seconds &&
            (!double.IsFinite(seconds) || seconds <= 0 || seconds * 1000 > int.MaxValue)) return ProcessDiagnostic.InvalidRequest;
        foreach (var argument in request.Arguments)
            if (!Text(argument)) return ProcessDiagnostic.InvalidRequest;
        // The operating system's own bounds: the CreateProcess command line on Windows, one argument (MAX_ARG_STRLEN) on Unix.
        if (OperatingSystem.IsWindows()
            ? WindowsProcessLifetime.CommandLineLength(request.Executable, request.Arguments) > 32_766
            : request.Arguments.Any(argument => System.Text.Encoding.UTF8.GetByteCount(argument) + 1 > 128 * 1024))
            return ProcessDiagnostic.InvalidRequest;
        long environmentCharacters = 2;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Environment)
        {
            if (string.IsNullOrEmpty(key) || key.Contains('=') || !Text(key) || !Text(value) || !keys.Add(key))
                return ProcessDiagnostic.InvalidRequest;
            environmentCharacters += (long)key.Length + value.Length + 2;
            if (environmentCharacters > 32767) return ProcessDiagnostic.InvalidRequest;
        }
        if (!File.Exists(request.Executable)) return ProcessDiagnostic.ExecutableUnavailable;
        if (!Directory.Exists(request.WorkingDirectory)) return ProcessDiagnostic.WorkingDirectoryUnavailable;
        if (!Directory.Exists(Path.GetDirectoryName(request.SpillPath))) return ProcessDiagnostic.SpillDirectoryUnavailable;
        if (File.Exists(request.SpillPath) || Directory.Exists(request.SpillPath)) return ProcessDiagnostic.InvalidRequest;
        return null;

        static bool Absolute(string? path) => Text(path) && Path.IsPathFullyQualified(path!) &&
            !path!.StartsWith("\\\\", StringComparison.Ordinal) && Path.GetFullPath(path) == path;
        static bool Text(string? value)
        {
            if (value is null || value.Contains('\0')) return false;
            for (var index = 0; index < value.Length; index++)
                if (char.IsHighSurrogate(value[index]))
                { if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false; }
                else if (char.IsLowSurrogate(value[index])) return false;
            return true;
        }
    }
}
