// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/ls.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

/// <summary>Bounded directory listing through the existing mandatory final-action policy pipeline.</summary>
public sealed class LsTool
{
    public const int MaximumEntries = 100_000;
    public const int MaximumNameCharacters = 1024 * 1024;
    private readonly IDirectoryFileOperations _operations;
    private readonly PathResolver _paths;
    public IPreparedToolAdapter Adapter { get; }
    public JsonData Declaration { get; } = JsonData.Parse("""{"name":"ls","description":"List directory contents. Returns entries sorted alphabetically, with '/' suffix for directories. Includes dotfiles. Output is truncated to 500 entries or 50KB (whichever is hit first).","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Directory to list (default: current directory)"},"limit":{"type":"number","description":"Maximum number of entries to return (default: 500)"}}}}""");

    private readonly bool _pi;

    /// <param name="pi">The <c>pi</c> tool policy (owner decision 0004): as ls.ts, entries are stat'ed through links wherever they point,
    /// unknown arguments are ignored and the listing has no size bounds.</param>
    public LsTool(string workingDirectory, string homeDirectory, IDirectoryFileOperations? operations = null, bool pi = false)
    {
        _operations = operations ?? new LocalFileOperations();
        _paths = new(workingDirectory, homeDirectory, _operations);
        _pi = pi;
        Adapter = new ListingAdapter(this);
    }

    public ToolInvoker CreateInvoker(IToolActionPolicy policy, IEnumerable<ToolActionTransform>? transforms = null,
        IEnumerable<ToolResultTransform>? resultTransforms = null) => new([Adapter], policy, transforms, resultTransforms,
            new(MaximumResultCharacters: 512 * 1024));
    public ToolDefinition CreateDefinition(ToolInvoker invoker) => new("ls", invoker ?? throw new ArgumentNullException(nameof(invoker)));

    private sealed record Input(string Path, double Limit);
    private Input Parse(JsonData arguments)
    {
        if (arguments is null || !_pi && arguments.ToString().Length > 16_384 || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid ls arguments.");
        var path = "."; var limit = 500d; var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.Value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate ls argument.");
            // Pi validation.ts normalizeOptionalNulls: an optional property sent as null (strict tool schemas make optional properties nullable) is absent.
            if (property.Name is "path" or "limit" && property.Value.ValueKind == JsonValueKind.Null) continue;
            if (property.Name == "path" && property.Value.ValueKind == JsonValueKind.String)
                path = property.Value.GetString()!;
            else if (property.Name == "limit" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number)
                && double.IsFinite(number) && (_pi || number <= MaximumEntries)) limit = number;
            // TypeBox ignores properties the schema does not name.
            else if (!_pi || property.Name is "path" or "limit") throw new ArgumentException("Unsupported ls argument.");
        }
        return new(path.Length == 0 ? "." : path, limit);
    }

    private sealed class ListingAdapter(LsTool owner) : IToolArgumentSchemaAdapter
    {
        /// <summary>Source TypeBox parameters, checked by validateToolArguments before the tool runs.</summary>
        public ToolArgumentSchema? ArgumentSchema => ToolArgumentSchema.FromDeclaration(owner.Declaration, ToolSchemaOrigin.TypeBox);
        public string Name => "ls";
        public async ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var input = owner.Parse(invocation.Call.Arguments);
            var target = owner._paths.Absolute(await owner._operations.CanonicalizeAsync(owner._paths.Resolve(input.Path), token).ConfigureAwait(false));
            return new(Name, Name, PreparedToolActionKind.Path, target,
                JsonData.Parse(JsonSerializer.Serialize(new { path = target, limit = input.Limit })), [],
                owner._paths.WorkingDirectory, ImmutableDictionary<string, string>.Empty);
        }
        public async ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var input = owner.Parse(action.Arguments);
                return action.ToolName == Name && action.Operation == Name && action.Kind == PreparedToolActionKind.Path &&
                    !action.CommandArguments.IsDefault && action.CommandArguments.IsEmpty && action.Environment is { Count: 0 } &&
                    action.WorkingDirectory == owner._paths.WorkingDirectory && action.Target == input.Path &&
                    action.Target == owner._paths.Absolute(action.Target) &&
                    action.Target == await owner._operations.CanonicalizeAsync(action.Target, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
            { return false; }
        }
        public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        {
            if (!await ValidateAsync(action, token).ConfigureAwait(false))
                return ToolResult.Error(ToolFailureKind.InvalidArguments, "Invalid final ls action.");
            return await owner.ListAsync(action.Target, owner.Parse(action.Arguments).Limit, token).ConfigureAwait(false);
        }
    }

    private async ValueTask<ToolResult> ListAsync(string target, double limit, CancellationToken token)
    {
        try
        {
            if (!await _operations.ExistsAsync(target, token).ConfigureAwait(false))
                return ToolResult.Error(ToolFailureKind.ExecutionError, $"Path not found: {target}");
            if (!await _operations.IsDirectoryAsync(target, token).ConfigureAwait(false))
                return ToolResult.Error(ToolFailureKind.ExecutionError, $"Not a directory: {target}");
            ImmutableArray<string> entries;
            var maximumEntries = _pi ? int.MaxValue : MaximumEntries; var maximumNames = _pi ? int.MaxValue : MaximumNameCharacters;
            try { entries = await _operations.ReadDirectoryAsync(target, maximumEntries, maximumNames, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { return ToolResult.Error(ToolFailureKind.ExecutionError, $"Cannot read directory: {error.Message}"); }
            if (entries.IsDefault || entries.Length > maximumEntries) throw new FileToolException(FileToolFailure.ResourceLimit);
            long characters = 0;
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested(); characters += entry?.Length ?? 0;
                // Trusted seams still cannot turn a returned child name into a second traversal target.
                if (string.IsNullOrEmpty(entry) || entry is "." or ".." || entry.Contains('\0') || entry.Contains(Path.DirectorySeparatorChar) ||
                    entry.Contains(Path.AltDirectorySeparatorChar) || Path.IsPathRooted(entry) || characters > maximumNames)
                    throw new ArgumentException("Invalid directory entry.");
                _paths.Absolute(Path.Combine(target, entry));
            }
            // LINQ ordering is stable: equal lowercase names retain the filesystem's original relative order.
            var sorted = entries.OrderBy(name => name.ToLowerInvariant(), StringComparer.InvariantCulture);
            var results = new List<string>(); var entryLimitReached = false;
            foreach (var entry in sorted)
            {
                token.ThrowIfCancellationRequested();
                if (results.Count >= limit) { entryLimitReached = true; break; }
                try
                {
                    var child = _paths.Absolute(await _operations.CanonicalizeAsync(Path.Combine(target, entry), token).ConfigureAwait(false));
                    var relative = Path.GetRelativePath(target, child);
                    // Listing admission does not authorize following a child link outside the admitted directory (explicit policy);
                    // ls.ts stat()s every entry through its link wherever it points.
                    if (!_pi && (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))) continue;
                    var directory = await _operations.IsDirectoryAsync(child, token).ConfigureAwait(false);
                    results.Add(entry + (directory ? "/" : ""));
                }
                catch (Exception error) when (error is not OperationCanceledException) { /* Source skips failed child stats; native cancellation still joins. */ }
            }
            token.ThrowIfCancellationRequested();
            if (results.Count == 0) return Result("(empty directory)", null);
            var truncation = ToolOutputTruncator.Head(string.Join('\n', results), new(int.MaxValue));
            var details = new Dictionary<string, object?>(); var notices = new List<string>();
            if (entryLimitReached)
            {
                notices.Add($"{Number(limit)} entries limit reached. Use limit={Number(limit * 2)} for more");
                details["entryLimitReached"] = limit;
            }
            if (truncation.Truncated)
            {
                notices.Add("50.0KB limit reached");
                details["truncation"] = new { content = truncation.Content, truncated = true, truncatedBy = "bytes",
                    totalLines = truncation.TotalLines, totalBytes = truncation.TotalBytes, outputLines = truncation.OutputLines,
                    outputBytes = truncation.OutputBytes, lastLinePartial = truncation.LastLinePartial,
                    firstLineExceedsLimit = truncation.FirstLineExceedsLimit, maxLines = 9007199254740991L, maxBytes = truncation.MaxBytes };
            }
            return Result(truncation.Content + (notices.Count == 0 ? "" : "\n\n[" + string.Join(". ", notices) + "]"), details.Count == 0 ? null : details);
        }
        catch (FileToolException error) { return ToolResult.Error(ToolFailureKind.ExecutionError, error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return ToolResult.Error(ToolFailureKind.ExecutionError, error.Message); }
    }

    private static string Number(double value) => value.ToString("G", CultureInfo.InvariantCulture);
    private static ToolResult Result(string text, Dictionary<string, object?>? details) =>
        details is null ? new ToolResult([new TextContent(text)], JsonData.EmptyObject).WithProperty("details", null)
            : new([new TextContent(text)], JsonData.Parse(JsonSerializer.Serialize(details)));
}
