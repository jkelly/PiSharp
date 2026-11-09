using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using PiSharp.Tools.Processes.Unix;

namespace PiSharp.Tools.Processes;

/// <summary>A child admitted before execution into a private Windows job or anchored Unix process group.</summary>
/// <remarks>Exit completes after retained descendant containment is stopped, even after natural leader exit. Streams are borrowed;
/// register their original reader tasks with OwnReader. Dispose stops the owned tree, joins readers and native cleanup, and reports failures.
/// Windows descendants cannot break out of the private job. On Unix, descendants that deliberately leave the admitted group are outside
/// the ownership receipt. This is a process owner, not a command authorization or sandbox boundary.</remarks>
public sealed class OwnedChildProcess : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Func<Task> stop;
    private readonly IAsyncDisposable native;
    private readonly List<Task> readers = [];
    private Task? disposal;
    private Task? stopping;
    private readonly Task<int> leader;
    private readonly Stream? input;
    public int Id { get; }
    public Task<int> Exit { get; }
    /// <summary>The leader's original exit, before any descendant cleanup. <see cref="Exit"/> also joins tree stop.</summary>
    public Task<int> LeaderExit => leader;
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    /// <summary>Writable bytes when RedirectStandardInput was requested. Dispose this stream to deliver EOF.</summary>
    public Stream StandardInput => input ?? throw new InvalidOperationException("Standard input is not redirected.");
    public bool HasExited => Exit.IsCompleted;
    public int ExitCode => leader.IsCompletedSuccessfully ? leader.Result : throw new InvalidOperationException("Child has not exited successfully");
    private OwnedChildProcess(int id, Task<int> leader, Stream output, Stream error, Stream? input, Func<Task> stop, IAsyncDisposable native)
    {
        Id = id; this.leader = leader; this.stop = stop; this.native = native;
        this.input = input;
        StandardOutput = new(output, new UTF8Encoding(false), false, 4096, true); StandardError = new(error, new UTF8Encoding(false), false, 4096, true);
        Exit = ObserveAsync();
    }
    public static async Task<OwnedChildProcess> StartAsync(ProcessStartInfo start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        cancellationToken.ThrowIfCancellationRequested();
        if (start.UseShellExecute || !string.IsNullOrEmpty(start.Arguments)) throw new ArgumentException("Use explicit executable/ArgumentList without a shell", nameof(start));
        if (!Path.IsPathFullyQualified(start.FileName) || start.FileName.Contains('\0') || !File.Exists(start.FileName))
            throw new ArgumentException("An existing absolute executable is required.", nameof(start));
        var cwd = Path.GetFullPath(string.IsNullOrEmpty(start.WorkingDirectory) ? Environment.CurrentDirectory : start.WorkingDirectory);
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException("Child working directory does not exist.");
        if (start.ArgumentList.Any(argument => argument.Contains('\0')) ||
            start.Environment.Any(pair => pair.Key.Length == 0 || pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value?.Contains('\0') == true))
            throw new ArgumentException("Arguments and environment must contain complete strings without NUL bytes.", nameof(start));
        var environment = start.Environment.Where(pair => pair.Value is not null).ToImmutableDictionary(pair => pair.Key, pair => pair.Value!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var request = new ProcessRequest(start.FileName, start.ArgumentList.ToImmutableArray(), cwd, environment, "");
        var redirectInput = start.RedirectStandardInput;
        if (OperatingSystem.IsWindows())
        {
            var process = await WindowsProcessLifetime.StartAsync(request, null, cancellationToken, redirectInput).ConfigureAwait(false);
            async Task<int> Leader() => await process.Exit.ConfigureAwait(false) ?? throw new IOException("The owned leader exit code is unavailable.");
            async Task Stop()
            {
                // The callback escapes this guarded block; retain platform proof at its actual invocation site too.
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows job cleanup requires Windows.");
                if (!await process.TerminateAndConfirmAsync().ConfigureAwait(false)) throw new IOException("Owned Windows job cleanup could not be confirmed");
            }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(process.ProcessId, Leader(), process.StandardOutput, process.StandardError,
                    redirectInput ? process.StandardInput : null, Stop, process);
            }
            catch (Exception error)
            {
                try
                {
                    await process.DisposeAsync().ConfigureAwait(false);
                    if (!process.CleanupConfirmed) throw new IOException("Failed launch cleanup could not be confirmed.");
                }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var process = await new PosixSpawnProcessAdmission().LaunchAsync(request, cancellationToken, redirectInput, cancellablePipes: true).ConfigureAwait(false);
            async Task Stop() { if (!await process.StopGroupAsync().ConfigureAwait(false)) throw new IOException("Owned Unix group cleanup could not be confirmed"); }
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new(process.ProcessId, process.Exit, process.StandardOutput, process.StandardError,
                    redirectInput ? PosixSpawnProcessAdmission.InteractiveInput(process) : null, Stop, process);
            }
            catch (Exception error)
            {
                var errors = new List<Exception> { error };
                try { await Stop().ConfigureAwait(false); } catch (Exception cleanup) { errors.Add(cleanup); }
                try { await process.DisposeAsync().ConfigureAwait(false); } catch (Exception cleanup) { errors.Add(cleanup); }
                if (errors.Count > 1) throw new AggregateException(errors);
                throw;
            }
        }
        throw new PlatformNotSupportedException("Owned process admission requires Windows, Linux, or macOS");
    }
    private async Task<int> ObserveAsync()
    {
        int code = 0; Exception? failure = null;
        try { code = await leader.ConfigureAwait(false); } catch (Exception error) { failure = error; }
        try { await StopAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { if (failure is not null) throw new AggregateException(failure, cleanup); throw; }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return code;
    }
    public Task WaitForExitAsync(CancellationToken cancellationToken = default) => Exit.WaitAsync(cancellationToken);
    public Task StopAsync() { lock (sync) return stopping ??= StopCoreAsync(); }
    private async Task StopCoreAsync()
    {
        Exception? failure = null;
        try { await stop().ConfigureAwait(false); } catch (Exception error) { failure = error; }
        try { if (input is not null) await input.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) { if (failure is not null) throw new AggregateException(failure, error); throw; }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    public void OwnReader(Task original)
    {
        ArgumentNullException.ThrowIfNull(original);
        lock (sync) { if (disposal is not null) throw new ObjectDisposedException(nameof(OwnedChildProcess)); readers.Add(original); }
    }
    public ValueTask DisposeAsync() { lock (sync) return new(disposal ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        await Task.Yield(); var failures = new List<Exception>();
        try { await StopAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { await Exit.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        Task[] originals; lock (sync) originals = readers.ToArray();
        var joined = Task.WhenAll(originals);
        // Tree stop normally closes every pipe writer. If an escaped Unix process kept a pipe, physical read-side closure
        // unblocks owned readers before joining their originals. A held user callback still owns its task until it settles.
        if (await Task.WhenAny(joined, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != joined)
        {
            try { StandardOutput.BaseStream.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { StandardError.BaseStream.Dispose(); } catch (Exception error) { failures.Add(error); }
        }
        try { await joined.ConfigureAwait(false); }
        catch (Exception error) { if (joined.Exception is { } errors) failures.AddRange(errors.Flatten().InnerExceptions); else failures.Add(error); }
        try { StandardOutput.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { StandardError.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { await native.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (OperatingSystem.IsWindows() && native is WindowsProcessLifetime windows && !windows.CleanupConfirmed)
            failures.Add(new IOException("Owned Windows handle cleanup could not be confirmed."));
        if (failures.Count > 0) throw new AggregateException(failures);
    }
}
