// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/grep.ts.
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

/// <summary>Bounded grep adapter with mandatory final-action policy and result containment.</summary>
public sealed class GrepTool
{
    public const int MaximumMatches = 10_000;
    public const int MaximumMatchCharacters = 1024 * 1024;
    public const int MaximumContext = 100;
    public const int MaximumContextFileBytes = 256 * 1024;
    public const int MaximumContextCacheBytes = 1024 * 1024;
    public const int MaximumContextFiles = 128;
    public const int MaximumContextFileLines = 100_000;
    public const int MaximumContextOutputBytes = 8 * 1024 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly IGrepContextReader? _contextReader;
    private readonly IGrepExecutor _executor;
    private readonly IDirectoryFileOperations _files;
    private readonly PathResolver _paths;
    public IPreparedToolAdapter Adapter { get; }
    public JsonData Declaration { get; } = JsonData.Parse("""{"name":"grep","description":"Search file contents for a pattern. Returns matching lines with file paths and line numbers. Respects .gitignore. Output is truncated to 100 matches or 50KB (whichever is hit first). Long lines are truncated to 500 chars.","parameters":{"type":"object","required":["pattern"],"properties":{"pattern":{"type":"string","description":"Search pattern (regex or literal string)"},"path":{"type":"string","description":"Directory or file to search (default: current directory)"},"glob":{"type":"string","description":"Filter files by glob pattern, e.g. '*.ts' or '**/*.spec.ts'"},"ignoreCase":{"type":"boolean","description":"Case-insensitive search (default: false)"},"literal":{"type":"boolean","description":"Treat pattern as literal string instead of regex (default: false)"},"context":{"type":"number","description":"Number of lines to show before and after each match (default: 0)"},"limit":{"type":"number","description":"Maximum number of matches to return (default: 100)"}}}}""");
    public GrepTool(string workingDirectory, string homeDirectory, IGrepExecutor executor, IDirectoryFileOperations? files = null,
        IGrepContextReader? contextReader = null)
    {
        ArgumentNullException.ThrowIfNull(executor); _executor = executor; _contextReader = contextReader; _files = files ?? new LocalFileOperations();
        _paths = new(workingDirectory, homeDirectory, _files); Adapter = new SearchAdapter(this);
    }
    public ToolInvoker CreateInvoker(IToolActionPolicy policy, IEnumerable<ToolActionTransform>? transforms = null,
        IEnumerable<ToolResultTransform>? resultTransforms = null) => new([Adapter], policy, transforms, resultTransforms,
            new(MaximumResultCharacters: 512 * 1024));
    public ToolDefinition CreateDefinition(ToolInvoker invoker) => new("grep", invoker ?? throw new ArgumentNullException(nameof(invoker)));
    private sealed record Input(string Pattern, string Path, string? Glob, bool IgnoreCase, bool Literal, int Limit, int Context);
    private static Input Parse(JsonData arguments)
    {
        if (arguments is null || arguments.ToString().Length > 16_384 || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid grep arguments.");
        string? pattern = null, glob = null; var path = "."; var ignore = false; var literal = false; var limit = 100; var contextLines = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.Value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate grep argument.");
            // Pi validation.ts normalizeOptionalNulls: an optional property sent as null (strict tool schemas make optional properties nullable) is absent.
            if (property.Name is "path" or "glob" or "ignoreCase" or "literal" or "context" or "limit" && property.Value.ValueKind == JsonValueKind.Null) continue;
            if (property.Name == "pattern" && property.Value.ValueKind == JsonValueKind.String) pattern = property.Value.GetString();
            else if (property.Name == "path" && property.Value.ValueKind == JsonValueKind.String) path = property.Value.GetString()!;
            else if (property.Name == "glob" && property.Value.ValueKind == JsonValueKind.String) glob = property.Value.GetString();
            else if (property.Name == "ignoreCase" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) ignore = property.Value.GetBoolean();
            else if (property.Name == "literal" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) literal = property.Value.GetBoolean();
            // Source: context > 0 ? context : 0, and Math.max(1, limit ?? 100). Native bounds keep both integral and bounded.
            else if (property.Name == "context" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var context) &&
                double.IsFinite(context) && (context <= 0 || context <= MaximumContext && context == Math.Truncate(context))) contextLines = context > 0 ? (int)context : 0;
            else if (property.Name == "limit" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number) && double.IsFinite(number) &&
                (number < 1 || number <= MaximumMatches && number == Math.Truncate(number))) limit = Math.Max(1, (int)Math.Max(number, 1));
            else throw new ArgumentException("Unsupported grep option; bounded integer context and positive bounded integer limits are required.");
        }
        if (!SearchText(pattern, 1024, allowEmpty: true) || glob is not null && !SearchText(glob, 1024, allowEmpty: true))
            throw new ArgumentException("Unsupported grep pattern/glob text.");
        return new(pattern!, path.Length == 0 ? "." : path, glob, ignore, literal, limit, contextLines);
    }
    internal static bool SearchText(string? value, int bound, bool allowEmpty = false) => value is not null &&
        (allowEmpty || value.Length != 0) && value.Length <= bound && !value.Any(char.IsControl) && !value.Any(char.IsSurrogate);
    internal static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private sealed class SearchAdapter(GrepTool owner) : IToolArgumentSchemaAdapter
    {
        /// <summary>Source TypeBox parameters, checked by validateToolArguments before the tool runs.</summary>
        public ToolArgumentSchema? ArgumentSchema => ToolArgumentSchema.FromDeclaration(owner.Declaration, ToolSchemaOrigin.TypeBox);
        public string Name => "grep";
        public async ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var input = Parse(invocation.Call.Arguments);
            if (input.Context > 0 && owner._contextReader is null)
                throw new ArgumentException("Positive grep context requires an explicitly admitted context reader.");
            var resolved = owner._paths.Resolve(input.Path);
            if (!Within(owner._paths.WorkingDirectory, resolved)) throw new ArgumentException("Grep target is outside the explicit workspace.");
            var target = owner._paths.Absolute(await owner._files.CanonicalizeAsync(resolved, token).ConfigureAwait(false));
            if (!Within(owner._paths.WorkingDirectory, target)) throw new ArgumentException("Grep target link is outside the explicit workspace.");
            return new(Name, Name, PreparedToolActionKind.Path, target,
                JsonData.Parse(JsonSerializer.Serialize(new { pattern = input.Pattern, path = target, glob = input.Glob ?? "", ignoreCase = input.IgnoreCase, literal = input.Literal, context = input.Context, limit = input.Limit })), [],
                owner._paths.WorkingDirectory, ImmutableDictionary<string, string>.Empty);
        }
        public async ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var input = Parse(action.Arguments);
                return (input.Context == 0 || owner._contextReader is not null) && action.ToolName == Name && action.Operation == Name && action.Kind == PreparedToolActionKind.Path &&
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
            long characters = 0; var selected = new List<(GrepMatch Match, string Absolute, string Relative, string Text)>();
            var linesTruncated = false;
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
                if (selected.Count < input.Limit) selected.Add((match, absolute, relative, text));
            }
            token.ThrowIfCancellationRequested();
            if (matches.IsEmpty) return Result("No matches found", null);
            // Validate the complete receipt before any context reads, including undisplayed events.
            var output = new BoundedHeadOutput(input.Context > 0);
            var cache = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var cacheBytes = 0;
            foreach (var item in selected)
            {
                token.ThrowIfCancellationRequested();
                if (input.Context == 0)
                {
                    output.Add($"{item.Relative}:{item.Match.LineNumber}: {Compact(item.Text)}");
                    continue;
                }
                if (!cache.TryGetValue(item.Absolute, out var lines))
                {
                    if (cache.Count == MaximumContextFiles || cacheBytes == MaximumContextCacheBytes)
                        throw new FileToolException(FileToolFailure.ResourceLimit);
                    // Recheck immediately before the borrowed read, after the original executor joined.
                    var canonical = _paths.Absolute(await _files.CanonicalizeAsync(item.Absolute, token).ConfigureAwait(false));
                    if (directory ? !Within(target, canonical) : !string.Equals(target, canonical, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Grep context link escaped the admitted search target.");
                    token.ThrowIfCancellationRequested();
                    ReadOnlyMemory<byte> bytes = default;
                    var readable = true;
                    var remaining = Math.Min(MaximumContextFileBytes, MaximumContextCacheBytes - cacheBytes);
                    try { bytes = await _contextReader!.ReadAsync(item.Absolute, remaining, token).ConfigureAwait(false); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { readable = false; }
                    token.ThrowIfCancellationRequested();
                    if (bytes.Length > remaining) throw new FileToolException(FileToolFailure.ResourceLimit);
                    cacheBytes += bytes.Length;
                    if (!readable) lines = [];
                    else
                    {
                        string content;
                        try { content = StrictUtf8.GetString(bytes.Span); }
                        catch (DecoderFallbackException) { throw new FileToolException(FileToolFailure.UnsupportedContent); }
                        if (content.Contains('\0')) throw new FileToolException(FileToolFailure.UnsupportedContent);
                        content = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
                        if (content.Count(character => character == '\n') >= MaximumContextFileLines)
                            throw new FileToolException(FileToolFailure.ResourceLimit);
                        lines = content.Split('\n');
                    }
                    cache.Add(item.Absolute, lines);
                }
                if (lines.Length == 0)
                {
                    output.Add($"{item.Relative}:{item.Match.LineNumber}: (unable to read file)");
                    continue;
                }
                var start = Math.Max(1L, (long)item.Match.LineNumber - input.Context);
                var end = Math.Min(lines.Length, (long)item.Match.LineNumber + input.Context);
                // Pi repeats overlapping blocks in match order; it does not merge them.
                for (var current = start; current <= end; current++)
                {
                    token.ThrowIfCancellationRequested();
                    var text = Compact(lines[(int)current - 1]);
                    output.Add(current == item.Match.LineNumber
                        ? $"{item.Relative}:{current}: {text}"
                        : $"{item.Relative}-{current}- {text}");
                }
            }
            token.ThrowIfCancellationRequested();
            var truncation = output.Finish();
            string Compact(string text)
            {
                if (text.Length <= 500) return text;
                linesTruncated = true;
                return text[..500] + "... [truncated]";
            }
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileToolException)
        { return ToolResult.Error(ToolFailureKind.ExecutionError, error.Message); }
    }
    // Retain only the whole-line 50KiB head; continue bounded counting for truthful upstream metadata.
    private sealed class BoundedHeadOutput(bool context)
    {
        private readonly List<string> _head = [];
        private int _totalLines, _totalBytes, _outputBytes;
        private bool _clipped;
        public void Add(string line)
        {
            var bytes = Encoding.UTF8.GetByteCount(line);
            var total = (long)_totalBytes + bytes + (_totalLines > 0 ? 1 : 0);
            if (total > (context ? MaximumContextOutputBytes : int.MaxValue))
                throw new FileToolException(FileToolFailure.ResourceLimit);
            _totalBytes = (int)total; _totalLines++;
            var output = (long)_outputBytes + bytes + (_head.Count > 0 ? 1 : 0);
            if (_clipped || output > ToolOutputTruncator.DefaultMaxBytes) { _clipped = true; return; }
            _head.Add(line); _outputBytes = (int)output;
        }
        public ToolOutputTruncationResult Finish() => new(string.Join('\n', _head), _clipped,
            _clipped ? ToolOutputTruncationLimit.Bytes : null, _totalLines, _totalBytes,
            _head.Count, _outputBytes, false, _clipped && _head.Count == 0 && _totalLines > 0,
            int.MaxValue, ToolOutputTruncator.DefaultMaxBytes);
    }
    private static ToolResult Result(string text, Dictionary<string, object?>? details) => details is null
        ? new ToolResult([new TextContent(text)], JsonData.EmptyObject).WithProperty("details", null)
        : new([new TextContent(text)], JsonData.Parse(JsonSerializer.Serialize(details)));
}
