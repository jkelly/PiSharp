// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/src/core/tools/grep.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

/// <summary>The grep tool of the <c>pi</c> tool policy (owner decision 0004), as grep.ts: ripgrep over any path, streamed and stopped at
/// the match limit, context lines streamed from ripgrep. The explicit policy keeps <see cref="GrepTool"/>.</summary>
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
    public JsonData Declaration { get; } = JsonData.Parse("""{"name":"grep","description":"Search file contents for a pattern. Returns matching lines with file paths and line numbers. Respects .gitignore. Output is truncated to 100 matches or 50KB (whichever is hit first). Long lines are truncated to 500 chars.","parameters":{"type":"object","required":["pattern"],"properties":{"pattern":{"type":"string","description":"Search pattern (regex or literal string)"},"path":{"type":"string","description":"Directory or file to search (default: current directory)"},"glob":{"type":"string","description":"Filter files by glob pattern, e.g. '*.ts' or '**/*.spec.ts'"},"ignoreCase":{"type":"boolean","description":"Case-insensitive search (default: false)"},"literal":{"type":"boolean","description":"Treat pattern as literal string instead of regex (default: false)"},"context":{"type":"number","description":"Number of lines to show before and after each match (default: 0)"},"limit":{"type":"number","description":"Maximum number of matches to return (default: 100)"}}}}""");

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
        // Source grep.ts: existsSync(path with a NUL byte) is false.
        if (input.Path.Contains('\0')) throw new ToolSourceErrorException("Path not found: " + _paths.ResolveWithNul(input.Path));
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
        var contextValue = Math.Clamp(Math.Floor(input.Context), 0, int.MaxValue - 1);
        var effectiveLimit = PiFindTool.NormalizeLimit(input.Limit, DefaultLimit);
        string FormatPath(string filePath)
        {
            if (isDirectory)
            {
                var relative = Path.GetRelativePath(searchPath, Path.GetFullPath(filePath, searchPath));
                if (relative.Length != 0 && relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                    return relative.Replace('\\', '/');
            }
            return Path.GetFileName(filePath);
        }
        // --no-config: the tool's literal, glob and limit semantics must not depend on RIPGREP_CONFIG_PATH.
        var args = new List<string> { "--no-config", "--json", "--line-number", "--color=never", "--hidden" };
        if (input.IgnoreCase) args.Add("--ignore-case");
        if (input.Literal) args.Add("--fixed-strings");
        if (!string.IsNullOrEmpty(input.Glob)) { args.Add("--glob"); args.Add(NormalizeGlob(input.Glob)); }
        if (contextValue > 0) { args.Add("--context"); args.Add(JsNumber(contextValue)); }
        args.Add("--"); args.Add(input.Pattern); args.Add(isDirectory ? "." : searchPath);
        // Source spawn(rg, args): Node rejects an argument with a NUL byte.
        if (NodeArgumentErrors.SpawnArguments(args) is { } spawnError) throw new PiSearchException(spawnError);
        var records = new List<(string Path, double Number, string Text, bool Match)>();
        var matchCount = 0; var matchLimitReached = false; var linesTruncated = false;
        long outputBytes = 0; var outputCount = 0;
        string? lastFile = null; double trailingEnd = 0;
        string Render(string path, double number, string text, bool match) => match
            ? $"{FormatPath(path)}:{JsNumber(number)}: {text}" : $"{FormatPath(path)}-{JsNumber(number)}- {text}";
        PiSearchProcess.Outcome outcome;
        try
        {
            outcome = await PiSearchProcess.RunAsync(rg, args, isDirectory ? searchPath : _paths.WorkingDirectory, _environment, line =>
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (!root.TryGetProperty("type", out var type)) return false;
                    var isMatch = type.GetString() == "match";
                    if (!isMatch && type.GetString() != "context") return false;
                    var data = root.GetProperty("data");
                    string? Field(string name)
                    {
                        if (!data.TryGetProperty(name, out var field)) return null;
                        if (field.TryGetProperty("text", out var text)) return text.GetString();
                        if (field.TryGetProperty("bytes", out var bytes)) return Encoding.UTF8.GetString(Convert.FromBase64String(bytes.GetString()!));
                        return null;
                    }
                    var path = Field("path"); var text = Field("lines");
                    if (string.IsNullOrEmpty(path) || text is null || !data.TryGetProperty("line_number", out var number)) return false;
                    var lineNumber = number.GetDouble();
                    var inWindow = path == lastFile && lineNumber <= trailingEnd;
                    var accepted = matchCount < effectiveLimit;
                    if (!accepted && isMatch) matchLimitReached = true;
                    if (accepted || inWindow)
                    {
                        if (text.EndsWith('\n')) { text = text[..^1]; if (text.EndsWith('\r')) text = text[..^1]; }
                        if (text.Length > 500) { text = UnicodeText.SliceHead(text, 500) + "... [truncated]"; linesTruncated = true; }
                        var displayMatch = isMatch && accepted;
                        var rendered = Render(path, lineNumber, text, displayMatch);
                        if (outputBytes - 1 <= 51200) records.Add((path, lineNumber, text, displayMatch));
                        outputBytes += Encoding.UTF8.GetByteCount(rendered) + 1L; outputCount++;
                        if (displayMatch)
                        {
                            matchCount++; lastFile = path; trailingEnd = lineNumber + contextValue;
                        }
                    }
                    return matchLimitReached && !(inWindow && lineNumber < trailingEnd);
                }
                catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return false; }
            }, token).ConfigureAwait(false);
        }
        catch (IOException error) { throw new PiSearchException($"Failed to run ripgrep: {error.Message}"); }
        if (!outcome.Stopped && outcome.ExitCode is not (0 or 1))
            throw new PiSearchException(outcome.StandardError.Trim() is { Length: > 0 } message ? message : $"ripgrep exited with code {outcome.ExitCode}");
        if (matchCount == 0) return Result("No matches found", null);
        var outputLines = new List<string>();
        var admitted = new Dictionary<string, bool>();
        foreach (var record in records)
        {
            token.ThrowIfCancellationRequested();
            if (!admitted.TryGetValue(record.Path, out var allowed))
            {
                var absolute = Path.GetFullPath(record.Path, isDirectory ? searchPath : _paths.WorkingDirectory);
                allowed = _authorizeRead is null || await _authorizeRead(absolute, token).ConfigureAwait(false);
                admitted.Add(record.Path, allowed);
            }
            outputLines.Add(Render(record.Path, record.Number, allowed ? record.Text : "(unable to read file)", record.Match));
        }
        var truncation = ToolOutputTruncator.Head(string.Join('\n', outputLines), new(int.MaxValue));
        truncation = truncation with { TotalLines = outputCount, TotalBytes = (int)Math.Min(int.MaxValue, Math.Max(0, outputBytes - 1)) };
        var details = new Dictionary<string, object?>(); var notices = new List<string>();
        if (matchLimitReached)
        {
            notices.Add($"{JsNumber(effectiveLimit)} matches limit reached. Use limit={JsNumber(effectiveLimit * 2d)} for more, or refine pattern");
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

    private static string NormalizeGlob(string glob)
    {
        var prefix = glob.StartsWith('!') ? "!" : "";
        var pattern = prefix.Length == 0 ? glob : glob[1..];
        while (pattern.StartsWith("./", StringComparison.Ordinal)) pattern = pattern[2..];
        return prefix + pattern;
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
