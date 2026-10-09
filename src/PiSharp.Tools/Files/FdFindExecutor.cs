using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using PiSharp.Tools.Processes;

namespace PiSharp.Tools.Files;

/// <summary>Trusted host admission facts; never read from find tool arguments or PATH.</summary>
public sealed record FdBinaryDescriptor(string Executable, int Length, string Sha256, string Version,
    string RuntimeIdentifier, string AdmissionReceipt);
public enum FdRepositoryPresence { Unknown, InsideGitRepository, OutsideGitRepository }
public delegate ValueTask<bool> FdBinaryAdmission(FdBinaryDescriptor binary, CancellationToken cancellationToken);
/// <summary>Host must return source-equivalent ancestor repository presence within its admitted probe scope; Unknown rejects.</summary>
public delegate ValueTask<FdRepositoryPresence> FdRepositoryLookup(string searchPath, CancellationToken cancellationToken);
public interface IFdSpillLease : IAsyncDisposable { string SpillPath { get; } }
public interface IFdSpillLeaseFactory
{
    string Root { get; }
    ValueTask<IFdSpillLease> CreateAsync(CancellationToken cancellationToken);
}

/// <summary>Explicit Windows fd backend using verified bytes and the original joined separated process receipt.</summary>
public sealed class FdFindExecutor : IFindExecutor
{
    private readonly FdBinaryDescriptor _binary;
    private readonly ISeparatedProcessRunner _runner;
    private readonly IFileOperations _files;
    private readonly FdBinaryAdmission _admission;
    private readonly FdRepositoryLookup _repository;
    private readonly IFdSpillLeaseFactory _spills;
    private readonly ImmutableDictionary<string, string> _environment;
    private readonly string _workspace;
    private readonly string _spillRoot;
    public FindExecutionProfile Profile => FindExecutionProfile.Fd;

    public FdFindExecutor(FdBinaryDescriptor binary, string workspace, ImmutableDictionary<string, string> environment,
        ISeparatedProcessRunner runner, FdBinaryAdmission admission, FdRepositoryLookup repository,
        IFdSpillLeaseFactory spills, IFileOperations? files = null)
    {
        ArgumentNullException.ThrowIfNull(binary); ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(admission); ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(spills); ArgumentNullException.ThrowIfNull(environment);
        if (!Absolute(binary.Executable) || binary.Length is < 1 or > 64 * 1024 * 1024 || binary.Sha256 is not { Length: 64 } ||
            !binary.Sha256.All(char.IsAsciiHexDigit) || !Text(binary.RuntimeIdentifier, 64) ||
            !Text(binary.Version, 128) || !Text(binary.AdmissionReceipt, 256) || !Absolute(workspace) || environment.Count > 1024 ||
            environment.Any(pair => !Text(pair.Key, 4096) || pair.Key.Contains('=') || pair.Value is null || pair.Value.Length > 16_384 || pair.Value.Contains('\0')) ||
            environment.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != environment.Count ||
            !Absolute(spills.Root) || !Within(workspace, spills.Root))
            throw new ArgumentException("Invalid explicit fd admission/configuration.");
        _binary = binary; _workspace = workspace; _environment = environment.ToImmutableDictionary(StringComparer.Ordinal);
        _runner = runner; _files = files ?? new LocalFileOperations(); _admission = admission; _repository = repository; _spills = spills;
        _spillRoot = spills.Root;
    }
    public bool SupportsPattern(string pattern) => Text(pattern, 1024);

    public async ValueTask<ImmutableArray<string>> FindAsync(FindExecutionRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request is null || !SupportsPattern(request.Pattern) || !Absolute(request.SearchPath) || !Within(_workspace, request.SearchPath) ||
            request.Limit is < 1 or > FindTool.MaximumResults || request.MaximumResults < request.Limit ||
            request.MaximumResults > FindTool.MaximumResults || request.MaximumTotalPathCharacters is < 1 or > FindTool.MaximumResultPathCharacters ||
            request.Ignore.IsDefault || !request.Ignore.IsEmpty) throw new ArgumentException("Invalid fd executor request.");
        if (_spills.Root != _spillRoot || _spillRoot != await _files.CanonicalizeAsync(_spillRoot, token).ConfigureAwait(false) ||
            _workspace != await _files.CanonicalizeAsync(_workspace, token).ConfigureAwait(false) ||
            request.SearchPath != await _files.CanonicalizeAsync(request.SearchPath, token).ConfigureAwait(false) ||
            _binary.Executable != await _files.CanonicalizeAsync(_binary.Executable, token).ConfigureAwait(false))
            throw new IOException("Fd admission path identity changed.");
        if (!await _admission(_binary, token).ConfigureAwait(false)) throw new IOException("Fd binary is not admitted by the host.");
        // Retain the actual read-only image handle through process and spill joins. No write/delete sharing.
        // The host admission also owns the immutable snapshot directory; this is not an arbitrary PATH executable.
        await using var image = new FileStream(_binary.Executable, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (image.Length != _binary.Length || !Convert.ToHexStringLower(await SHA256.HashDataAsync(image, token).ConfigureAwait(false))
            .Equals(_binary.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fd admitted binary size/hash changed.");
        var repository = await _repository(request.SearchPath, token).ConfigureAwait(false);
        if (repository is not (FdRepositoryPresence.InsideGitRepository or FdRepositoryPresence.OutsideGitRepository))
            throw new IOException("Fd repository/ignore context is not qualified.");
        var arguments = BuildArguments(request.Pattern, request.SearchPath, request.Limit, repository);
        token.ThrowIfCancellationRequested();
        var lease = await _spills.CreateAsync(token).ConfigureAwait(false);
        if (lease is null) throw new IOException("Fd spill ownership was not acquired.");
        ImmutableArray<string> paths = default; Exception? failure = null;
        try
        {
            if (!Absolute(lease.SpillPath) || !Within(_workspace, lease.SpillPath) ||
                !Within(_spillRoot, lease.SpillPath) || lease.SpillPath != await _files.CanonicalizeAsync(lease.SpillPath, token).ConfigureAwait(false))
                throw new IOException("Fd spill lease escaped the admitted workspace.");
            token.ThrowIfCancellationRequested();
            if (_binary.Executable != await _files.CanonicalizeAsync(_binary.Executable, token).ConfigureAwait(false))
                throw new IOException("Fd binary launch path identity changed.");
            var result = await _runner.RunSeparatedAsync(new(_binary.Executable, arguments, _workspace, _environment, lease.SpillPath), token).ConfigureAwait(false);
            // No process/pump/output disposal is raced against cancellation; the original receipt is already joined.
            if (result?.Process is null || result.StandardOutput is null || result.StandardError is null ||
                !result.Process.CleanupConfirmed || !result.Process.CapturedOutputComplete || result.StandardOutput.Truncated || result.StandardError.Truncated ||
                result.Process.Diagnostics.IsDefault || result.Process.Diagnostics.Any(diagnostic => !Enum.IsDefined(diagnostic)) ||
                result.Process.Output is null || result.Process.Output.FullOutputPath is { } fullOutput && fullOutput != lease.SpillPath ||
                result.StandardOutput.Content is null || result.StandardError.Content is null)
                throw new IOException("Fd original process/output cleanup or capture is incomplete.");
            token.ThrowIfCancellationRequested();
            if (result.Process.Status == ProcessRunStatus.Canceled) throw new IOException("Fd process canceled without the caller cancellation token.");
            if (!result.Process.ProcessStarted || result.Process.Status is not (ProcessRunStatus.Exited or ProcessRunStatus.NonZeroExit) ||
                result.Process.ExitCode is null || !result.Process.Diagnostics.IsEmpty)
                throw new IOException("Failed to run fd: admitted process did not complete normally.");
            if ((result.Process.Status == ProcessRunStatus.Exited) != (result.Process.ExitCode == 0))
                throw new IOException("Fd process status/exit receipt is inconsistent.");
            paths = ParseOutput(result.StandardOutput.Content, result.StandardError.Content, result.Process.ExitCode.Value, request);
        }
        catch (Exception original) { failure = original; }
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException("Fd operation and owned spill cleanup failed.", failure, cleanup); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        token.ThrowIfCancellationRequested();
        return paths;
    }

    internal static ImmutableArray<string> BuildArguments(string pattern, string searchPath, int limit, FdRepositoryPresence repository)
    {
        var args = ImmutableArray.CreateBuilder<string>();
        args.Add("--glob"); args.Add("--color=never"); args.Add("--hidden");
        if (repository == FdRepositoryPresence.OutsideGitRepository) args.Add("--no-require-git");
        args.Add("--max-results"); args.Add(limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (pattern.Contains('/'))
        {
            args.Add("--full-path");
            if (!pattern.StartsWith('/') && !pattern.StartsWith("**/", StringComparison.Ordinal) && pattern != "**") pattern = "**/" + pattern;
            pattern = pattern.Replace("/", @"[/\\]", StringComparison.Ordinal);
        }
        args.Add("--"); args.Add(pattern); args.Add(searchPath); return args.ToImmutable();
    }
    private static ImmutableArray<string> ParseOutput(string stdout, string stderr, int exitCode, FindExecutionRequest request)
    {
        if (stdout.Length > 2 * 1024 * 1024 || stderr.Length > 2 * 1024 * 1024 || stdout.Contains('\0') || stderr.Contains('\0'))
            throw new IOException("Fd stream receipt exceeds the supported text profile.");
        var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        var raw = string.Join('\n', lines);
        if (exitCode != 0 && raw.Length == 0) throw new IOException(stderr.Trim() is { Length: > 0 } error ? error : $"fd exited with code {exitCode}");
        if (raw.Length == 0) return [];
        var paths = lines.Select(line => line.Trim()).Where(line => line.Length != 0).ToImmutableArray();
        if (paths.IsEmpty) throw new IOException("Whitespace-only fd path output is outside the supported domain.");
        if (paths.Length > request.Limit || paths.Sum(path => (long)path.Length) > request.MaximumTotalPathCharacters)
            throw new IOException("Fd output exceeded the admitted path bounds.");
        return paths;
    }
    private static bool Absolute(string? value) => Text(value, 4096) && Path.IsPathFullyQualified(value!) && Path.GetFullPath(value!) == value;
    private static bool Text(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl) &&
        !value.Any(char.IsSurrogate); // Explicit non-surrogate BMP admission profile; broader Unicode is pending.
    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
