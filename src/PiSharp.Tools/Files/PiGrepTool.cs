// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/grep.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

/// <summary>The grep tool of the <c>pi</c> tool policy (owner decision 0004), as grep.ts: ripgrep over any path, streamed and stopped at
/// the match limit, context lines read from the files themselves. The explicit policy keeps <see cref="GrepTool"/>.</summary>
public sealed class PiGrepTool : IToolArgumentSchemaAdapter
{
    private const double DefaultLimit = 100;
    private readonly PathResolver _paths;
    private readonly IDirectoryFileOperations _files;
    private readonly Func<CancellationToken, ValueTask<string?>> _ripgrep;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly Func<string, CancellationToken, ValueTask<bool>>? _authorizeRead;
    public string Name => "grep";
    /// <summary>Source TypeBox parameters, checked by validateToolArguments before the tool runs.</summary>
    public ToolArgumentSchema? ArgumentSchema => ToolArgumentSchema.FromDeclaration(Declaration, ToolSchemaOrigin.TypeBox);
    public JsonData Declaration { get; } = JsonData.Parse("""{"name":"grep","description":"Search file contents for a pattern. Returns matching lines with file paths and line numbers. Respects .gitignore. Output is truncated to 100 matches or 50KB (whichever is hit first). Long lines are truncated to 500 chars.","parameters":{"type":"object","properties":{"pattern":{"type":"string","description":"Search pattern (regex or literal string)"},"path":{"type":"string","description":"Directory or file to search (default: current directory)"},"glob":{"type":"string","description":"Filter files by glob pattern, e.g. '*.ts' or '**/*.spec.ts'"},"ignoreCase":{"type":"boolean","description":"Case-insensitive search (default: false)"},"literal":{"type":"boolean","description":"Treat pattern as literal string instead of regex (default: false)"},"context":{"type":"number","description":"Number of lines to show before and after each match (default: 0)"},"limit":{"type":"number","description":"Maximum number of matches to return (default: 100)"}},"required":["pattern"]}}""");

    /// <param name="ripgrep">ensureTool("rg"): the rg executable, or null when it is not available and could not be downloaded.</param>
    /// <param name="authorizeRead">Whether a context read of a file is allowed (the policy's protected files); refused reads show as unreadable.</param>
    public PiGrepTool(string workingDirectory, string homeDirectory, Func<CancellationToken, ValueTask<string?>> ripgrep,
        IReadOnlyDictionary<string, string>? environment = null, IDirectoryFileOperations? files = null,
        Func<string, CancellationToken, ValueTask<bool>>? authorizeRead = null)
    {
        ArgumentNullException.ThrowIfNull(ripgrep);
        _files = files ?? new LocalFileOperations(); _paths = new(workingDirectory, homeDirectory, _files);
        _ripgrep = ripgrep; _environment = environment; _authorizeRead = authorizeRead;
    }

    private sealed record Input(string Pattern, string Path, string? Glob, bool IgnoreCase, bool Literal, double Context, double? Limit);
    private static Input Parse(JsonData arguments)
    {
        if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid grep arguments.");
        var value = arguments.Value;
        // Pi validation.ts: optional properties sent as null are absent; TypeBox ignores other properties.
        string? Text(string name, bool required)
        {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null && !required)
                return required ? throw new ArgumentException($"grep requires {name}.") : null;
            return field.ValueKind == JsonValueKind.String ? field.GetString() : throw new ArgumentException($"grep {name} must be a string.");
        }
        bool Flag(string name) => value.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null &&
            (field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : throw new ArgumentException($"grep {name} must be a boolean."));
        double? Number(string name) => !value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null ? null :
            field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw new ArgumentException($"grep {name} must be a number.");
        return new(Text("pattern", true)!, Text("path", false) ?? "", Text("glob", false), Flag("ignoreCase"), Flag("literal"), Number("context") ?? 0, Number("limit"));
    }

    public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var input = Parse(invocation.Call.Arguments);
        var target = _paths.Resolve(input.Path.Length == 0 ? "." : input.Path);
        return ValueTask.FromResult(new PreparedToolAction(Name, Name, PreparedToolActionKind.Path, target, Arguments(input, target), [],
            _paths.WorkingDirectory, ImmutableDictionary<string, string>.Empty));
    }
    private static JsonData Arguments(Input input, string target) => JsonData.Parse(JsonSerializer.Serialize(new
    { pattern = input.Pattern, path = target, glob = input.Glob, ignoreCase = input.IgnoreCase, literal = input.Literal, context = input.Context, limit = input.Limit }));

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
        if (!await ValidateAsync(action, token).ConfigureAwait(false)) return ToolResult.Error(ToolFailureKind.InvalidArguments, "Invalid final grep action.");
        var input = Parse(action.Arguments); var searchPath = action.Target;
        try { return await SearchAsync(input, searchPath, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return ToolResult.Error(ToolFailureKind.Canceled, "Operation aborted"); }
        catch (PiSearchException error) { return ToolResult.Error(ToolFailureKind.ExecutionError, error.Message); }
    }

    private async Task<ToolResult> SearchAsync(Input input, string searchPath, CancellationToken token)
    {
        var rg = await _ripgrep(token).ConfigureAwait(false) ?? throw new PiSearchException("ripgrep (rg) is not available and could not be downloaded");
        bool isDirectory;
        try
        {
            if (!await _files.ExistsAsync(searchPath, token).ConfigureAwait(false)) throw new FileNotFoundException();
            isDirectory = await _files.IsDirectoryAsync(searchPath, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new PiSearchException($"Path not found: {searchPath}"); }
        var contextValue = input.Context > 0 ? input.Context : 0;
        var effectiveLimit = Math.Max(1, input.Limit ?? DefaultLimit);
        string FormatPath(string filePath)
        {
            if (isDirectory)
            {
                var relative = Path.GetRelativePath(searchPath, filePath);
                if (relative.Length != 0 && relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                    return relative.Replace('\\', '/');
            }
            return Path.GetFileName(filePath);
        }
        var fileCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        async ValueTask<string[]> FileLines(string filePath)
        {
            if (fileCache.TryGetValue(filePath, out var lines)) return lines;
            try
            {
                if (_authorizeRead is not null && !await _authorizeRead(filePath, token).ConfigureAwait(false)) throw new UnauthorizedAccessException();
                // Source fs.readFile(path, "utf-8"): the whole file, undecodable bytes as U+FFFD.
                var content = EditTool.DecodeText(await File.ReadAllBytesAsync(filePath, token).ConfigureAwait(false));
                lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or OutOfMemoryException) { lines = []; }
            fileCache[filePath] = lines;
            return lines;
        }
        var args = new List<string> { "--json", "--line-number", "--color=never", "--hidden" };
        if (input.IgnoreCase) args.Add("--ignore-case");
        if (input.Literal) args.Add("--fixed-strings");
        if (!string.IsNullOrEmpty(input.Glob)) { args.Add("--glob"); args.Add(input.Glob); }
        args.Add("--"); args.Add(input.Pattern); args.Add(searchPath);
        var matches = new List<(string FilePath, double LineNumber, string? LineText)>();
        var matchCount = 0; var matchLimitReached = false;
        PiSearchProcess.Outcome outcome;
        try
        {
            outcome = await PiSearchProcess.RunAsync(rg, args, _paths.WorkingDirectory, _environment, line =>
            {
                if (line.Trim().Length == 0 || matchCount >= effectiveLimit) return false;
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                        type.GetString() != "match") return false;
                    matchCount++;
                    var data = root.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
                    string? Field(string name, string inner) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var outer) &&
                        outer.ValueKind == JsonValueKind.Object && outer.TryGetProperty(inner, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                    var filePath = Field("path", "text"); var lineText = Field("lines", "text");
                    if (!string.IsNullOrEmpty(filePath) && data.TryGetProperty("line_number", out var number) && number.ValueKind == JsonValueKind.Number)
                        matches.Add((filePath, number.GetDouble(), lineText));
                }
                catch (JsonException) { return false; }
                if (matchCount < effectiveLimit) return false;
                matchLimitReached = true;
                return true;
            }, token).ConfigureAwait(false);
        }
        catch (IOException error) { throw new PiSearchException($"Failed to run ripgrep: {error.Message}"); }
        if (!outcome.Stopped && outcome.ExitCode is not (0 or 1))
            throw new PiSearchException(outcome.StandardError.Trim() is { Length: > 0 } message ? message : $"ripgrep exited with code {outcome.ExitCode}");
        if (matchCount == 0) return Result("No matches found", null);
        var linesTruncated = false; var outputLines = new List<string>();
        string TruncateLine(string line)
        {
            if (line.Length <= 500) return line;
            linesTruncated = true; return line[..500] + "... [truncated]";
        }
        foreach (var match in matches)
        {
            token.ThrowIfCancellationRequested();
            var relativePath = FormatPath(match.FilePath);
            if (contextValue == 0 && match.LineText is not null)
            {
                var sanitized = match.LineText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
                if (sanitized.EndsWith('\n')) sanitized = sanitized[..^1];
                outputLines.Add($"{relativePath}:{JsNumber(match.LineNumber)}: {TruncateLine(sanitized)}");
                continue;
            }
            var lines = await FileLines(match.FilePath).ConfigureAwait(false);
            if (lines.Length == 0) { outputLines.Add($"{relativePath}:{JsNumber(match.LineNumber)}: (unable to read file)"); continue; }
            var start = contextValue > 0 ? Math.Max(1, match.LineNumber - contextValue) : match.LineNumber;
            var end = contextValue > 0 ? Math.Min(lines.Length, match.LineNumber + contextValue) : match.LineNumber;
            for (var current = start; current <= end; current++)
            {
                // lines[current - 1] ?? "": a fractional or out-of-range index is undefined in JavaScript.
                var lineText = current == Math.Floor(current) && current >= 1 && current <= lines.Length ? lines[(int)current - 1] : "";
                var text = TruncateLine(lineText.Replace("\r", "", StringComparison.Ordinal));
                outputLines.Add(current == match.LineNumber ? $"{relativePath}:{JsNumber(current)}: {text}" : $"{relativePath}-{JsNumber(current)}- {text}");
            }
        }
        var truncation = ToolOutputTruncator.Head(string.Join('\n', outputLines), new(int.MaxValue));
        var details = new Dictionary<string, object?>(); var notices = new List<string>();
        if (matchLimitReached)
        {
            notices.Add($"{JsNumber(effectiveLimit)} matches limit reached. Use limit={JsNumber(effectiveLimit * 2)} for more, or refine pattern");
            details["matchLimitReached"] = effectiveLimit;
        }
        if (truncation.Truncated)
        {
            notices.Add("50.0KB limit reached");
            details["truncation"] = TruncationDetails(truncation);
        }
        if (linesTruncated) { notices.Add("Some lines truncated to 500 chars. Use read tool to see full lines"); details["linesTruncated"] = true; }
        return Result(truncation.Content + (notices.Count == 0 ? "" : "\n\n[" + string.Join(". ", notices) + "]"), details.Count == 0 ? null : details);
    }

    internal static object TruncationDetails(ToolOutputTruncationResult truncation) => new
    {
        content = truncation.Content, truncated = true, truncatedBy = "bytes", totalLines = truncation.TotalLines, totalBytes = truncation.TotalBytes,
        outputLines = truncation.OutputLines, outputBytes = truncation.OutputBytes, lastLinePartial = truncation.LastLinePartial,
        firstLineExceedsLimit = truncation.FirstLineExceedsLimit, maxLines = 9007199254740991L, maxBytes = truncation.MaxBytes
    };

    /// <summary>JavaScript Number to string for the integral and simple fractional values tool arguments carry.</summary>
    internal static string JsNumber(double value) => value == Math.Floor(value) && Math.Abs(value) < 1e21
        ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("R", CultureInfo.InvariantCulture).Replace("E+", "e+", StringComparison.Ordinal).Replace("E-", "e-", StringComparison.Ordinal);

    internal static ToolResult Result(string text, Dictionary<string, object?>? details) => details is null
        ? new ToolResult([new TextContent(text)], JsonData.EmptyObject).WithProperty("details", null)
        : new([new TextContent(text)], JsonData.Parse(JsonSerializer.Serialize(details)));
}

/// <summary>A Pi search tool's rejection: the error result's text.</summary>
internal sealed class PiSearchException(string message) : Exception(message);
