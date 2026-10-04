using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

/// <summary>Bounded zero-context grep adapter with mandatory final-action policy and result containment.</summary>
public sealed class GrepTool
{
    public const int MaximumMatches = 10_000;
    public const int MaximumMatchCharacters = 1024 * 1024;
    private readonly IGrepExecutor _executor;
    private readonly IDirectoryFileOperations _files;
    private readonly PathResolver _paths;
    public IPreparedToolAdapter Adapter { get; }
    public JsonData Declaration { get; } = JsonData.Parse("""{"name":"grep","description":"Search file contents through an explicitly admitted ripgrep executor. Regex, literal, ignoreCase and glob options; zero context only. Match lines include paths and line numbers. Output limited to 100 matches, 50KB and 500 characters per line. Complete bounded capture required; no implicit binary acquisition.","parameters":{"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string"},"glob":{"type":"string"},"ignoreCase":{"type":"boolean"},"literal":{"type":"boolean"},"context":{"type":"number","const":0},"limit":{"type":"number","minimum":1,"maximum":10000}},"required":["pattern"],"additionalProperties":false}}""");
    public GrepTool(string workingDirectory, string homeDirectory, IGrepExecutor executor, IDirectoryFileOperations? files = null)
    {
        ArgumentNullException.ThrowIfNull(executor); _executor = executor; _files = files ?? new LocalFileOperations();
        _paths = new(workingDirectory, homeDirectory, _files); Adapter = new SearchAdapter(this);
    }
    public ToolInvoker CreateInvoker(IToolActionPolicy policy, IEnumerable<ToolActionTransform>? transforms = null,
        IEnumerable<ToolResultTransform>? resultTransforms = null) => new([Adapter], policy, transforms, resultTransforms,
            new(MaximumResultCharacters: 512 * 1024));
    public ToolDefinition CreateDefinition(ToolInvoker invoker) => new("grep", invoker ?? throw new ArgumentNullException(nameof(invoker)));
    private sealed record Input(string Pattern, string Path, string? Glob, bool IgnoreCase, bool Literal, int Limit);
    private static Input Parse(JsonData arguments)
    {
        if (arguments is null || arguments.ToString().Length > 16_384 || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid grep arguments.");
        string? pattern = null, glob = null; var path = "."; var ignore = false; var literal = false; var limit = 100;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.Value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate grep argument.");
            if (property.Name == "pattern" && property.Value.ValueKind == JsonValueKind.String) pattern = property.Value.GetString();
            else if (property.Name == "path" && property.Value.ValueKind == JsonValueKind.String) path = property.Value.GetString()!;
            else if (property.Name == "glob" && property.Value.ValueKind == JsonValueKind.String) glob = property.Value.GetString();
            else if (property.Name == "ignoreCase" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) ignore = property.Value.GetBoolean();
            else if (property.Name == "literal" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) literal = property.Value.GetBoolean();
            else if (property.Name == "context" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var context) && context == 0) { }
            else if (property.Name == "limit" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 1 && number <= MaximumMatches && number == Math.Truncate(number)) limit = (int)number;
            else throw new ArgumentException("Unsupported grep option; only zero context and positive bounded integer limits are admitted.");
        }
        if (!SearchText(pattern, 1024, allowEmpty: true) || glob is not null && !SearchText(glob, 1024, allowEmpty: true))
            throw new ArgumentException("Unsupported grep pattern/glob text.");
        return new(pattern!, path.Length == 0 ? "." : path, glob, ignore, literal, limit);
    }
    internal static bool SearchText(string? value, int bound, bool allowEmpty = false) => value is not null &&
        (allowEmpty || value.Length != 0) && value.Length <= bound && !value.Any(char.IsControl) && !value.Any(char.IsSurrogate);
    internal static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private sealed class SearchAdapter(GrepTool owner) : IPreparedToolAdapter
    {
        public string Name => "grep";
        public async ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var input = Parse(invocation.Call.Arguments);
            var resolved = owner._paths.Resolve(input.Path);
            if (!Within(owner._paths.WorkingDirectory, resolved)) throw new ArgumentException("Grep target is outside the explicit workspace.");
            var target = owner._paths.Absolute(await owner._files.CanonicalizeAsync(resolved, token).ConfigureAwait(false));
            if (!Within(owner._paths.WorkingDirectory, target)) throw new ArgumentException("Grep target link is outside the explicit workspace.");
            return new(Name, Name, PreparedToolActionKind.Path, target,
                JsonData.Parse(JsonSerializer.Serialize(new { pattern = input.Pattern, path = target, glob = input.Glob ?? "", ignoreCase = input.IgnoreCase, literal = input.Literal, context = 0, limit = input.Limit })), [],
                owner._paths.WorkingDirectory, ImmutableDictionary<string, string>.Empty);
        }
        public async ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var input = Parse(action.Arguments);
                return action.ToolName == Name && action.Operation == Name && action.Kind == PreparedToolActionKind.Path &&
                    !action.CommandArguments.IsDefault && action.CommandArguments.IsEmpty && action.Environment is { Count: 0 } &&
                    action.WorkingDirectory == owner._paths.WorkingDirectory && action.Target == input.Path &&
                    Within(owner._paths.WorkingDirectory, action.Target) && action.Target == owner._paths.Absolute(action.Target) &&
                    action.Target == await owner._files.CanonicalizeAsync(action.Target, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is ArgumentException or JsonException or IOException or UnauthorizedAccessException) { return false; }
        }
        public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        {
            if (!await ValidateAsync(action, token).ConfigureAwait(false)) return ToolResult.Error(ToolFailureKind.InvalidArguments, "Invalid final grep action.");
            return await owner.SearchAsync(action.Target, Parse(action.Arguments), token).ConfigureAwait(false);
        }
    }
    private async ValueTask<ToolResult> SearchAsync(string target, Input input, CancellationToken token)
    {
        try
        {
            if (!await _files.ExistsAsync(target, token).ConfigureAwait(false)) return ToolResult.Error(ToolFailureKind.ExecutionError, "Path not found: " + target);
            var directory = await _files.IsDirectoryAsync(target, token).ConfigureAwait(false);
            var matches = await _executor.GrepAsync(new(input.Pattern, target, input.Glob, input.IgnoreCase, input.Literal, input.Limit), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (matches.IsDefault || matches.Length > MaximumMatches) throw new IOException("Grep executor exceeded admitted match count.");
            long characters = 0; var output = new List<string>(); var linesTruncated = false;
            foreach (var match in matches)
            {
                token.ThrowIfCancellationRequested();
                if (match is null || !SearchText(match.Path, 4096) || !Path.IsPathFullyQualified(match.Path) ||
                    match.LineNumber < 1 || match.LineText is null || match.LineText.Contains('\0') || match.LineText.Any(char.IsSurrogate))
                    throw new IOException("Unsupported grep match event.");
                characters += match.Path.Length + match.LineText.Length;
                if (characters > MaximumMatchCharacters) throw new IOException("Grep match receipt exceeds admitted size.");
                var absolute = _paths.Absolute(match.Path);
                if (directory ? !Within(target, absolute) : !string.Equals(target, absolute, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Grep match escaped the admitted search target.");
                var canonical = _paths.Absolute(await _files.CanonicalizeAsync(absolute, token).ConfigureAwait(false));
                if (directory ? !Within(target, canonical) : !string.Equals(target, canonical, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Grep match link escaped the admitted search target.");
                // Validate every received event, including events beyond the displayed limit.
                var relative = directory ? Path.GetRelativePath(target, absolute) : "";
                relative = relative.Length != 0 && !relative.StartsWith("..", StringComparison.Ordinal) ? relative.Replace('\\', '/') : Path.GetFileName(absolute);
                var text = match.LineText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
                if (text.EndsWith('\n')) text = text[..^1];
                if (text.Contains('\n')) throw new IOException("Multiline grep match text is outside the admitted profile.");
                if (output.Count >= input.Limit) continue;
                if (text.Length > 500) { text = text[..500] + "... [truncated]"; linesTruncated = true; }
                output.Add($"{relative}:{match.LineNumber}: {text}");
            }
            token.ThrowIfCancellationRequested();
            if (matches.IsEmpty) return Result("No matches found", null);
            var truncation = ToolOutputTruncator.Head(string.Join('\n', output), new(int.MaxValue));
            var details = new Dictionary<string, object?>(); var notices = new List<string>();
            if (matches.Length >= input.Limit)
            {
                details["matchLimitReached"] = input.Limit;
                notices.Add($"{input.Limit} matches limit reached. Use limit={input.Limit * 2} for more, or refine pattern");
            }
            if (truncation.Truncated)
            {
                notices.Add("50.0KB limit reached");
                details["truncation"] = new { content = truncation.Content, truncated = true, truncatedBy = "bytes", totalLines = truncation.TotalLines,
                    totalBytes = truncation.TotalBytes, outputLines = truncation.OutputLines, outputBytes = truncation.OutputBytes,
                    lastLinePartial = truncation.LastLinePartial, firstLineExceedsLimit = truncation.FirstLineExceedsLimit,
                    maxLines = 9007199254740991L, maxBytes = truncation.MaxBytes };
            }
            if (linesTruncated) { details["linesTruncated"] = true; notices.Add("Some lines truncated to 500 chars. Use read tool to see full lines"); }
            return Result(truncation.Content + (notices.Count == 0 ? "" : "\n\n[" + string.Join(". ", notices) + "]"), details.Count == 0 ? null : details);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return ToolResult.Error(ToolFailureKind.ExecutionError, error.Message); }
    }
    private static ToolResult Result(string text, Dictionary<string, object?>? details) => details is null
        ? new ToolResult([new TextContent(text)], JsonData.EmptyObject).WithProperty("details", null)
        : new([new TextContent(text)], JsonData.Parse(JsonSerializer.Serialize(details)));
}