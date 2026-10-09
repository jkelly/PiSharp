using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using PiSharp.Tools.Processes;

namespace PiSharp.Tools.Files;

/// <summary>Trusted host admission facts; never read from grep tool arguments or PATH.</summary>
public sealed record RipgrepBinaryDescriptor(string Executable, int Length, string Sha256, string Version,
    string RuntimeIdentifier, string AdmissionReceipt);
public delegate ValueTask<bool> RipgrepBinaryAdmission(RipgrepBinaryDescriptor binary, CancellationToken cancellationToken);
/// <summary>Explicit Windows ripgrep backend using verified bytes and the original joined separated process receipt.</summary>
public sealed class RipgrepExecutor : IGrepExecutor
{
    private readonly RipgrepBinaryDescriptor _binary;
    private readonly ISeparatedProcessRunner _runner;
    private readonly IDirectoryFileOperations _files;
    private readonly RipgrepBinaryAdmission _admission;
    private readonly IFdSpillLeaseFactory _spills;
    private readonly ImmutableDictionary<string, string> _environment;
    private readonly string _workspace;
    private readonly string _spillRoot;

    public RipgrepExecutor(RipgrepBinaryDescriptor binary, string workspace, ImmutableDictionary<string, string> environment,
        ISeparatedProcessRunner runner, RipgrepBinaryAdmission admission,
        IFdSpillLeaseFactory spills, IDirectoryFileOperations? files = null)
    {
        ArgumentNullException.ThrowIfNull(binary); ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(spills); ArgumentNullException.ThrowIfNull(environment);
        if (!Absolute(binary.Executable) || binary.Length is < 1 or > 64 * 1024 * 1024 || binary.Sha256 is not { Length: 64 } ||
            !binary.Sha256.All(char.IsAsciiHexDigit) || !Text(binary.RuntimeIdentifier, 64) ||
            !Text(binary.Version, 128) || !Text(binary.AdmissionReceipt, 256) || !Absolute(workspace) || environment.Count > 1024 ||
            environment.Any(pair => !Text(pair.Key, 4096) || pair.Key.Contains('=') || pair.Value is null || pair.Value.Length > 16_384 || pair.Value.Contains('\0')) ||
            environment.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != environment.Count ||
            !Absolute(spills.Root) || !Within(workspace, spills.Root))
            throw new ArgumentException("Invalid explicit ripgrep admission/configuration.");
        _binary = binary; _workspace = workspace; _environment = environment.ToImmutableDictionary(StringComparer.Ordinal);
        _runner = runner; _files = files ?? new LocalFileOperations(); _admission = admission; _spills = spills;
        _spillRoot = spills.Root;
    }


    public async ValueTask<ImmutableArray<GrepMatch>> GrepAsync(GrepExecutionRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request is null || !GrepTool.SearchText(request.Pattern, 1024, allowEmpty: true) ||
            request.Glob is not null && !GrepTool.SearchText(request.Glob, 1024, allowEmpty: true) ||
            !Absolute(request.SearchPath) || !Within(_workspace, request.SearchPath) || request.Limit is < 1 or > GrepTool.MaximumMatches)
            throw new ArgumentException("Invalid ripgrep executor request.");
        if (_spills.Root != _spillRoot || _spillRoot != await _files.CanonicalizeAsync(_spillRoot, token).ConfigureAwait(false) ||
            _workspace != await _files.CanonicalizeAsync(_workspace, token).ConfigureAwait(false) ||
            request.SearchPath != await _files.CanonicalizeAsync(request.SearchPath, token).ConfigureAwait(false) ||
            _binary.Executable != await _files.CanonicalizeAsync(_binary.Executable, token).ConfigureAwait(false))
            throw new IOException("Ripgrep admission path identity changed.");
        if (!await _admission(_binary, token).ConfigureAwait(false)) throw new IOException("Ripgrep binary is not admitted by the host.");
        // Retain the actual read-only image handle through process and spill joins. No write/delete sharing.
        // The host admission also owns the immutable snapshot directory; this is not an arbitrary PATH executable.
        await using var image = new FileStream(_binary.Executable, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (image.Length != _binary.Length || !Convert.ToHexStringLower(await SHA256.HashDataAsync(image, token).ConfigureAwait(false))
            .Equals(_binary.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Ripgrep admitted binary size/hash changed.");
        if (!await _files.ExistsAsync(request.SearchPath, token).ConfigureAwait(false)) throw new IOException("Path not found: " + request.SearchPath);
        var directory = await _files.IsDirectoryAsync(request.SearchPath, token).ConfigureAwait(false);
        var arguments = BuildArguments(request);
        token.ThrowIfCancellationRequested();
        var lease = await _spills.CreateAsync(token).ConfigureAwait(false);
        if (lease is null) throw new IOException("Ripgrep spill ownership was not acquired.");
        ImmutableArray<GrepMatch> matches = default; Exception? failure = null;
        try
        {
            if (!Absolute(lease.SpillPath) || !Within(_workspace, lease.SpillPath) ||
                !Within(_spillRoot, lease.SpillPath) || lease.SpillPath != await _files.CanonicalizeAsync(lease.SpillPath, token).ConfigureAwait(false))
                throw new IOException("Ripgrep spill lease escaped the admitted workspace.");
            token.ThrowIfCancellationRequested();
            if (_binary.Executable != await _files.CanonicalizeAsync(_binary.Executable, token).ConfigureAwait(false))
                throw new IOException("Ripgrep binary launch path identity changed.");
            var result = await _runner.RunSeparatedAsync(new(_binary.Executable, arguments, _workspace, _environment, lease.SpillPath), token).ConfigureAwait(false);
            // No process/pump/output disposal is raced against cancellation; the original receipt is already joined.
            if (result?.Process is null || result.StandardOutput is null || result.StandardError is null ||
                !result.Process.CleanupConfirmed || !result.Process.CapturedOutputComplete || result.StandardOutput.Truncated || result.StandardError.Truncated ||
                result.Process.Diagnostics.IsDefault || result.Process.Diagnostics.Any(diagnostic => !Enum.IsDefined(diagnostic)) ||
                result.Process.Output is null || result.Process.Output.FullOutputPath is { } fullOutput && fullOutput != lease.SpillPath ||
                result.StandardOutput.Content is null || result.StandardError.Content is null)
                throw new IOException("Ripgrep original process/output cleanup or capture is incomplete.");
            token.ThrowIfCancellationRequested();
            if (result.Process.Status == ProcessRunStatus.Canceled) throw new IOException("Ripgrep process canceled without the caller cancellation token.");
            if (!result.Process.ProcessStarted || result.Process.Status is not (ProcessRunStatus.Exited or ProcessRunStatus.NonZeroExit) ||
                result.Process.ExitCode is null || !result.Process.Diagnostics.IsEmpty)
                throw new IOException("Failed to run ripgrep: admitted process did not complete normally.");
            if ((result.Process.Status == ProcessRunStatus.Exited) != (result.Process.ExitCode == 0))
                throw new IOException("Ripgrep process status/exit receipt is inconsistent.");
            matches = ParseOutput(result.StandardOutput.Content, result.StandardError.Content, result.Process.ExitCode.Value);
            foreach (var match in matches)
            {
                if (directory ? !Within(request.SearchPath, match.Path) : !string.Equals(request.SearchPath, match.Path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Ripgrep match escaped the admitted search target.");
                var canonical = await _files.CanonicalizeAsync(match.Path, token).ConfigureAwait(false);
                if (!Absolute(canonical)) throw new IOException("Ripgrep canonical match identity is unsupported.");
                if (directory ? !Within(request.SearchPath, canonical) : !string.Equals(request.SearchPath, canonical, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Ripgrep match link escaped the admitted search target.");
            }
        }
        catch (Exception original) { failure = original; }
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch (Exception cleanup) { failure = failure is null ? cleanup : new AggregateException("Ripgrep operation and owned spill cleanup failed.", failure, cleanup); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        token.ThrowIfCancellationRequested();
        return matches;
    }

    internal static ImmutableArray<string> BuildArguments(GrepExecutionRequest request)
    {
        var args = ImmutableArray.CreateBuilder<string>();
        args.Add("--json"); args.Add("--line-number"); args.Add("--color=never"); args.Add("--hidden");
        if (request.IgnoreCase) args.Add("--ignore-case");
        if (request.Literal) args.Add("--fixed-strings");
        if (!string.IsNullOrEmpty(request.Glob)) { args.Add("--glob"); args.Add(request.Glob); }
        args.Add("--"); args.Add(request.Pattern); args.Add(request.SearchPath); return args.ToImmutable();
    }
    private static ImmutableArray<GrepMatch> ParseOutput(string stdout, string stderr, int exitCode)
    {
        if (stdout.Length > 2 * 1024 * 1024 || stderr.Length > 2 * 1024 * 1024 || stdout.Contains('\0') || stderr.Contains('\0'))
            throw new IOException("Ripgrep stream receipt exceeds the supported text profile.");
        if (exitCode is not (0 or 1)) throw new IOException(stderr.Trim() is { Length: > 0 } error ? error : $"ripgrep exited with code {exitCode}");
        var matches = ImmutableArray.CreateBuilder<GrepMatch>(); long characters = 0;
        foreach (var line in stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                    throw new IOException("Malformed ripgrep event.");
                if (type.GetString() is "begin" or "end" or "summary") continue;
                if (type.GetString() != "match") throw new IOException("Unsupported ripgrep event.");
                var data = root.GetProperty("data"); var path = data.GetProperty("path").GetProperty("text").GetString();
                var number = data.GetProperty("line_number"); var text = data.GetProperty("lines").GetProperty("text").GetString();
                if (!Absolute(path) || !number.TryGetInt32(out var lineNumber) || lineNumber < 1 || text is null || text.Contains('\0') || text.Any(char.IsSurrogate))
                    throw new IOException("Unsupported ripgrep match encoding/line number.");
                var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
                if (normalized.EndsWith('\n')) normalized = normalized[..^1];
                if (normalized.Contains('\n')) throw new IOException("Multiline ripgrep match text is unsupported.");
                characters += path!.Length + text.Length;
                if (matches.Count >= GrepTool.MaximumMatches || characters > GrepTool.MaximumMatchCharacters)
                    throw new IOException("Ripgrep output exceeds admitted match bounds; streaming limit qualification is pending.");
                matches.Add(new(path!, lineNumber, text));
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            { throw new IOException("Malformed or unsupported ripgrep JSON event.", error); }
        }
        return matches.ToImmutable();
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
