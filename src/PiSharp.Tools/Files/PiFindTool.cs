// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/coding-agent/src/core/tools/find.ts.
using System.Collections.Immutable;
using System.Text.Json;
using System.Text;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

/// <summary>The find tool of the <c>pi</c> tool policy (owner decision 0004), as find.ts: fd over any path, its results relativized to
/// the search path. The explicit policy keeps <see cref="FindTool"/>.</summary>
public sealed class PiFindTool : IToolArgumentSchemaAdapter
{
    private const double DefaultLimit = 1000;
    private readonly PathResolver _paths;
    private readonly Func<CancellationToken, ValueTask<string?>> _fd;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    public string Name => "find";
    /// <summary>Source TypeBox parameters, checked by validateToolArguments before the tool runs.</summary>
    public ToolArgumentSchema? ArgumentSchema => ToolArgumentSchema.FromDeclaration(Declaration, ToolSchemaOrigin.TypeBox);
    public JsonData Declaration { get; } = JsonData.Parse("""{"name":"find","description":"Search for files by glob pattern. Returns matching file paths relative to the search directory. Respects .gitignore. Output is truncated to 1000 results or 50KB (whichever is hit first).","parameters":{"type":"object","required":["pattern"],"properties":{"pattern":{"type":"string","description":"Glob pattern to match files, e.g. '*.ts', '**/*.json', or 'src/**/*.spec.ts'"},"path":{"type":"string","description":"Directory to search in (default: current directory)"},"limit":{"type":"number","description":"Maximum number of results (default: 1000)"}}}}""");

    /// <param name="fd">ensureTool("fd"): the fd executable, or null when it is not available and could not be downloaded.</param>
    public PiFindTool(string workingDirectory, string homeDirectory, Func<CancellationToken, ValueTask<string?>> fd,
        IReadOnlyDictionary<string, string>? environment = null, IFileOperations? files = null)
    {
        ArgumentNullException.ThrowIfNull(fd);
        _paths = new(workingDirectory, homeDirectory, files ?? new LocalFileOperations()); _fd = fd; _environment = environment;
    }

    private sealed record Input(string Pattern, string Path, double? Limit);
    private static Input Parse(JsonData arguments)
    {
        if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid find arguments.");
        var value = arguments.Value;
        if (!value.TryGetProperty("pattern", out var pattern) || pattern.ValueKind != JsonValueKind.String) throw new ArgumentException("find requires a string pattern.");
        string? path = null; double? limit = null;
        if (value.TryGetProperty("path", out var field) && field.ValueKind != JsonValueKind.Null)
            path = field.ValueKind == JsonValueKind.String ? field.GetString() : throw new ArgumentException("find path must be a string.");
        if (value.TryGetProperty("limit", out field) && field.ValueKind != JsonValueKind.Null)
            limit = field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw new ArgumentException("find limit must be a number.");
        return new(pattern.GetString()!, path ?? "", limit);
    }

    public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var input = Parse(invocation.Call.Arguments);
        // Source find.ts passes the path to spawn(fd, args), which rejects an argument with a NUL byte.
        if (input.Path.Contains('\0'))
            throw new ToolSourceErrorException(NodeArgumentErrors.SpawnArguments(Arguments(input, _paths.ResolveWithNul(input.Path)))!);
        var target = _paths.Resolve(input.Path.Length == 0 ? "." : input.Path);
        return ValueTask.FromResult(new PreparedToolAction(Name, Name, PreparedToolActionKind.Path, target,
            JsonData.Parse(JsonSerializer.Serialize(new { pattern = input.Pattern, path = target, limit = input.Limit })), [],
            _paths.WorkingDirectory, ImmutableDictionary<string, string>.Empty));
    }

    public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var input = Parse(action.Arguments);
            return ValueTask.FromResult(action.ToolName == Name && action.Operation == Name && action.Kind == PreparedToolActionKind.Path &&
                !action.CommandArguments.IsDefault && action.CommandArguments.IsEmpty && action.Environment is { Count: 0 } &&
                action.WorkingDirectory == _paths.WorkingDirectory && action.Target == input.Path && action.Target == _paths.Absolute(action.Target));
        }
        catch (Exception error) when (error is ArgumentException or JsonException) { return ValueTask.FromResult(false); }
    }

    public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
    {
        if (!await ValidateAsync(action, token).ConfigureAwait(false)) return ToolResult.Error(ToolFailureKind.InvalidArguments, "Invalid final find action.");
        try { return await SearchAsync(Parse(action.Arguments), action.Target, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return ToolResult.Error(ToolFailureKind.Canceled, "Operation aborted"); }
        catch (PiSearchException error) { return ToolResult.Error(ToolFailureKind.ExecutionError, error.Message); }
    }

    private async Task<ToolResult> SearchAsync(Input input, string searchPath, CancellationToken token)
    {
        var effectiveLimit = NormalizeLimit(input.Limit, DefaultLimit);
        var fd = await _fd(token).ConfigureAwait(false) ?? throw new PiSearchException("fd is not available and could not be downloaded");
        var args = Arguments(input, searchPath, token);
        var pattern = EffectivePattern(input.Pattern);
        var windowsPathPattern = OperatingSystem.IsWindows() && pattern.Contains('/');
        if (windowsPathPattern && pattern.EndsWith("/**", StringComparison.Ordinal)) pattern += "/*";
        // Source spawn(fd, args): Node rejects an argument with a NUL byte.
        if (NodeArgumentErrors.SpawnArguments(args) is { } spawnError) throw new PiSearchException(spawnError);
        var relativized = new List<string>();
        var resultCount = 0; long outputBytes = 0;
        PiSearchProcess.Outcome outcome;
        token.ThrowIfCancellationRequested();
        try
        {
            outcome = await PiSearchProcess.RunAsync(fd, args, _paths.WorkingDirectory, _environment, raw =>
            {
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0 || windowsPathPattern && !WindowsFindGlob.Match(line, pattern, pattern == pattern.ToLowerInvariant())) return false;
                if (resultCount == effectiveLimit) { resultCount++; return true; }
                var relative = Relativize(line, searchPath);
                if (outputBytes - 1 <= 51200) relativized.Add(relative);
                outputBytes += Encoding.UTF8.GetByteCount(relative) + 1L;
                resultCount++;
                return false;
            }, token).ConfigureAwait(false);
        }
        catch (IOException error) { throw new PiSearchException($"Failed to run fd: {error.Message}"); }
        if (outcome.ExitCode != 0 && resultCount == 0)
            throw new PiSearchException(outcome.StandardError.Trim() is { Length: > 0 } message ? message : $"fd exited with code {outcome.ExitCode}");
        if (resultCount == 0) return PiGrepTool.Result("No files found matching pattern", null);
        var truncation = ToolOutputTruncator.Head(string.Join('\n', relativized), new(int.MaxValue));
        var details = new Dictionary<string, object?>(); var notices = new List<string>();
        if (resultCount > effectiveLimit)
        {
            notices.Add($"{PiGrepTool.JsNumber(effectiveLimit)} results limit reached. Use limit={PiGrepTool.JsNumber(effectiveLimit * 2d)} for more, or refine pattern");
            details["resultLimitReached"] = effectiveLimit;
        }
        if (truncation.Truncated) { notices.Add("50.0KB limit reached"); details["truncation"] = PiGrepTool.TruncationDetails(truncation); }
        return PiGrepTool.Result(truncation.Content + (notices.Count == 0 ? "" : "\n\n[" + string.Join(". ", notices) + "]"), details.Count == 0 ? null : details);
    }

    /// <summary>find.ts's fd arguments for this input and search path.</summary>
    private static List<string> Arguments(Input input, string searchPath, CancellationToken token = default)
    {
        var effectiveLimit = NormalizeLimit(input.Limit, DefaultLimit);
        var args = new List<string> { "--color=never", "--hidden" };
        // fd normally ignores .gitignore outside git repos, so keep --no-require-git there; inside repos use fd's git-aware default.
        var insideGitRepo = false;
        for (var current = searchPath; ;)
        {
            token.ThrowIfCancellationRequested();
            if (Path.Exists(Path.Join(current, ".git"))) { insideGitRepo = true; break; }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
        if (!insideGitRepo) args.Add("--no-require-git");
        var effectivePattern = EffectivePattern(input.Pattern);
        if (OperatingSystem.IsWindows() && effectivePattern.Contains('/'))
        {
            // fd versions disagree about native Windows full-path globs. Match enumerated paths ourselves.
            args.Add("--"); args.Add(""); args.Add(searchPath);
        }
        else
        {
            args.Add("--glob"); args.Add("--max-results"); args.Add(PiGrepTool.JsNumber(effectiveLimit + 1));
            if (effectivePattern.Contains('/')) args.Add("--full-path");
            args.Add("--"); args.Add(effectivePattern); args.Add(searchPath);
        }
        token.ThrowIfCancellationRequested();
        return args;
    }

    private static string EffectivePattern(string pattern)
    {
        if (OperatingSystem.IsWindows()) pattern = pattern.Replace('\\', '/');
        return pattern.Contains('/') && !pattern.StartsWith('/') && !pattern.StartsWith("**/", StringComparison.Ordinal) && pattern != "**"
            ? "**/" + pattern : pattern;
    }

    internal static int NormalizeLimit(double? value, double fallback) => (int)Math.Clamp(Math.Floor(value ?? fallback), 1, int.MaxValue - 1);

    /// <summary>find.ts relativizeFindResultPath.</summary>
    internal static string Relativize(string resultPath, string searchPath)
    {
        var trailing = resultPath.EndsWith(Path.DirectorySeparatorChar) || Path.DirectorySeparatorChar == '\\' && resultPath.EndsWith('/');
        var relative = Path.IsPathRooted(resultPath) ? Path.GetRelativePath(searchPath, resultPath) : resultPath;
        if (relative == "." && Path.IsPathRooted(resultPath)) relative = "";
        var posix = relative.Replace(Path.DirectorySeparatorChar, '/');
        return trailing && !posix.EndsWith('/') ? posix + "/" : posix;
    }
}
