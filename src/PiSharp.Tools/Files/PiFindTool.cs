// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/find.ts.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

/// <summary>The find tool of the <c>pi</c> tool policy (owner decision 0004), as find.ts: fd over any path, its results relativized to
/// the search path. The explicit policy keeps <see cref="FindTool"/>.</summary>
public sealed class PiFindTool : IPreparedToolAdapter
{
    private const double DefaultLimit = 1000;
    private readonly PathResolver _paths;
    private readonly Func<CancellationToken, ValueTask<string?>> _fd;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    public string Name => "find";
    public JsonData Declaration { get; } = JsonData.Parse("""{"name":"find","description":"Search for files by glob pattern. Returns matching file paths relative to the search directory. Respects .gitignore. Output is truncated to 1000 results or 50KB (whichever is hit first).","parameters":{"type":"object","properties":{"pattern":{"type":"string","description":"Glob pattern to match files, e.g. '*.ts', '**/*.json', or 'src/**/*.spec.ts'"},"path":{"type":"string","description":"Directory to search in (default: current directory)"},"limit":{"type":"number","description":"Maximum number of results (default: 1000)"}},"required":["pattern"]}}""");

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
        var effectiveLimit = input.Limit ?? DefaultLimit;
        var fd = await _fd(token).ConfigureAwait(false) ?? throw new PiSearchException("fd is not available and could not be downloaded");
        var args = new List<string> { "--glob", "--color=never", "--hidden" };
        // fd normally ignores .gitignore outside git repos, so keep --no-require-git there; inside repos use fd's git-aware default.
        var insideGitRepo = false;
        for (var current = searchPath; ;)
        {
            if (Path.Exists(Path.Join(current, ".git"))) { insideGitRepo = true; break; }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
        if (!insideGitRepo) args.Add("--no-require-git");
        args.Add("--max-results"); args.Add(PiGrepTool.JsNumber(effectiveLimit));
        // fd --glob matches the basename unless --full-path is set; a path-containing pattern needs a leading '**/'.
        var effectivePattern = input.Pattern;
        if (input.Pattern.Contains('/'))
        {
            args.Add("--full-path");
            if (!input.Pattern.StartsWith('/') && !input.Pattern.StartsWith("**/", StringComparison.Ordinal) && input.Pattern != "**")
                effectivePattern = "**/" + input.Pattern;
            // fd matches full paths using native separators on Windows.
            if (OperatingSystem.IsWindows()) effectivePattern = effectivePattern.Replace("/", @"[/\\]", StringComparison.Ordinal);
        }
        args.Add("--"); args.Add(effectivePattern); args.Add(searchPath);
        var lines = new List<string>();
        PiSearchProcess.Outcome outcome;
        try { outcome = await PiSearchProcess.RunAsync(fd, args, _paths.WorkingDirectory, _environment, line => { lines.Add(line); return false; }, token).ConfigureAwait(false); }
        catch (IOException error) { throw new PiSearchException($"Failed to run fd: {error.Message}"); }
        var output = string.Join('\n', lines);
        if (outcome.ExitCode != 0 && output.Length == 0)
            throw new PiSearchException(outcome.StandardError.Trim() is { Length: > 0 } message ? message : $"fd exited with code {outcome.ExitCode}");
        if (output.Length == 0) return PiGrepTool.Result("No files found matching pattern", null);
        var relativized = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length != 0) relativized.Add(Relativize(line, searchPath));
        }
        var truncation = ToolOutputTruncator.Head(string.Join('\n', relativized), new(int.MaxValue));
        var details = new Dictionary<string, object?>(); var notices = new List<string>();
        if (relativized.Count >= effectiveLimit)
        {
            notices.Add($"{PiGrepTool.JsNumber(effectiveLimit)} results limit reached. Use limit={PiGrepTool.JsNumber(effectiveLimit * 2)} for more, or refine pattern");
            details["resultLimitReached"] = effectiveLimit;
        }
        if (truncation.Truncated) { notices.Add("50.0KB limit reached"); details["truncation"] = PiGrepTool.TruncationDetails(truncation); }
        return PiGrepTool.Result(truncation.Content + (notices.Count == 0 ? "" : "\n\n[" + string.Join(". ", notices) + "]"), details.Count == 0 ? null : details);
    }

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
