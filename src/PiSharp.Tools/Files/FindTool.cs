using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

/// <summary>Bounded, policy-mediated adapter for an explicitly admitted pinned Pi find profile.</summary>
public sealed class FindTool
{
    public const int MaximumResults = 10_000;
    public const int MaximumResultPathCharacters = 1024 * 1024;
    private readonly IFindExecutor _executor;
    private readonly FindExecutionProfile _profile;
    private readonly IFileOperations _files;
    private readonly PathResolver _paths;
    public IPreparedToolAdapter Adapter { get; }
    public JsonData Declaration { get; } = JsonData.Parse("""{"name":"find","description":"Search for files by glob pattern through an explicitly admitted executor. Returns matching paths relative to the search directory in executor order. Supported glob and ignore behavior is declared by that executor; unsupported syntax is rejected. Output is truncated to 1000 results or 50KB. Bounded admitted-executor profile; no implicit fd acquisition.","parameters":{"type":"object","properties":{"pattern":{"type":"string","description":"Glob pattern supported by the admitted executor"},"path":{"type":"string","description":"Directory to search in (default: current directory)"},"limit":{"type":"number","minimum":1,"maximum":10000,"description":"Maximum number of results (default: 1000); bounded integer profile"}},"required":["pattern"],"additionalProperties":false}}""");

    public FindTool(string workingDirectory, string homeDirectory, IFindExecutor executor, IFileOperations? files = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _profile = executor.Profile;
        if (!Enum.IsDefined(_profile)) throw new ArgumentException("Unknown find executor profile.", nameof(executor));
        _executor = executor; _files = files ?? new LocalFileOperations();
        _paths = new(workingDirectory, homeDirectory, _files); Adapter = new SearchAdapter(this);
    }
    public ToolInvoker CreateInvoker(IToolActionPolicy policy, IEnumerable<ToolActionTransform>? transforms = null,
        IEnumerable<ToolResultTransform>? resultTransforms = null) => new([Adapter], policy, transforms, resultTransforms,
            new(MaximumResultCharacters: 512 * 1024));
    public ToolDefinition CreateDefinition(ToolInvoker invoker) => new("find", invoker ?? throw new ArgumentNullException(nameof(invoker)));

    private sealed record Input(string Pattern, string Path, int Limit);
    private Input Parse(JsonData arguments)
    {
        if (arguments is null || arguments.ToString().Length > 16_384 || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid find arguments.");
        string? pattern = null; var path = "."; var limit = 1000; var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.Value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate find argument.");
            if (property.Name == "pattern" && property.Value.ValueKind == JsonValueKind.String) pattern = property.Value.GetString();
            else if (property.Name == "path" && property.Value.ValueKind == JsonValueKind.String) path = property.Value.GetString()!;
            else if (property.Name == "limit" && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var count) &&
                double.IsFinite(count) && count >= 1 && count <= MaximumResults && count == Math.Truncate(count)) limit = (int)count;
            else throw new ArgumentException("Unsupported find argument.");
        }
        if (string.IsNullOrEmpty(pattern) || pattern.Length > 1024 || pattern.Contains('\0') || !ValidScalars(pattern) || !_executor.SupportsPattern(pattern))
            throw new ArgumentException("Unsupported find glob pattern.");
        return new(pattern, path.Length == 0 ? "." : path, limit);
    }
    private sealed class SearchAdapter(FindTool owner) : IPreparedToolAdapter
    {
        public string Name => "find";
        public async ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var input = owner.Parse(invocation.Call.Arguments);
            var target = owner._paths.Absolute(await owner._files.CanonicalizeAsync(owner._paths.Resolve(input.Path), token).ConfigureAwait(false));
            return new(Name, Name, PreparedToolActionKind.Path, target,
                JsonData.Parse(JsonSerializer.Serialize(new { pattern = input.Pattern, path = target, limit = input.Limit })), [],
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
                    action.Target == await owner._files.CanonicalizeAsync(action.Target, token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is ArgumentException or JsonException or IOException or UnauthorizedAccessException) { return false; }
        }
        public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        {
            if (!await ValidateAsync(action, token).ConfigureAwait(false)) return ToolResult.Error(ToolFailureKind.InvalidArguments, "Invalid final find action.");
            return await owner.SearchAsync(action.Target, owner.Parse(action.Arguments), token).ConfigureAwait(false);
        }
    }
    private async ValueTask<ToolResult> SearchAsync(string target, Input input, CancellationToken token)
    {
        try
        {
            if (!await _files.ExistsAsync(target, token).ConfigureAwait(false)) return ToolResult.Error(ToolFailureKind.ExecutionError, "Path not found: " + target);
            var request = new FindExecutionRequest(input.Pattern, target, input.Limit,
                _profile == FindExecutionProfile.Fd ? [] : ["**/node_modules/**", "**/.git/**"],
                MaximumResults, MaximumResultPathCharacters);
            var found = await _executor.FindAsync(request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (found.IsDefault || found.Length > input.Limit || found.Length > MaximumResults)
                throw new IOException("Find executor exceeded the admitted result bound.");
            var relativePaths = new List<string>(); long characters = 0;
            foreach (var result in found)
            {
                token.ThrowIfCancellationRequested(); characters += result?.Length ?? 0;
                if (string.IsNullOrEmpty(result) || characters > MaximumResultPathCharacters || !ValidScalars(result))
                    throw new IOException("Find executor returned an invalid or oversized path.");
                var absolute = ownerPath(result);
                if (!Within(target, absolute)) throw new IOException("Find executor returned a path outside the admitted search directory.");
                var canonical = _paths.Absolute(await _files.CanonicalizeAsync(absolute, token).ConfigureAwait(false));
                if (!Within(target, canonical)) throw new IOException("Find executor returned a link outside the admitted search directory.");
                var trailing = result.EndsWith(Path.DirectorySeparatorChar) || result.EndsWith(Path.AltDirectorySeparatorChar);
                var relative = Path.IsPathFullyQualified(result) ? Path.GetRelativePath(target, absolute) : result;
                var output = relative.Replace(Path.DirectorySeparatorChar, '/');
                if (trailing && !output.EndsWith('/')) output += "/";
                relativePaths.Add(output);
            }
            token.ThrowIfCancellationRequested();
            if (relativePaths.Count == 0) return Result("No files found matching pattern", null);
            var truncation = ToolOutputTruncator.Head(string.Join('\n', relativePaths), new(int.MaxValue));
            var details = new Dictionary<string, object?>(); var notices = new List<string>();
            if (relativePaths.Count >= input.Limit)
            {
                details["resultLimitReached"] = input.Limit;
                notices.Add(input.Limit.ToString(CultureInfo.InvariantCulture) + " results limit reached" +
                    (_profile == FindExecutionProfile.Fd ? $". Use limit={input.Limit * 2} for more, or refine pattern" : ""));
            }
            if (truncation.Truncated)
            {
                notices.Add("50.0KB limit reached");
                details["truncation"] = new { content = truncation.Content, truncated = true, truncatedBy = "bytes", totalLines = truncation.TotalLines,
                    totalBytes = truncation.TotalBytes, outputLines = truncation.OutputLines, outputBytes = truncation.OutputBytes,
                    lastLinePartial = truncation.LastLinePartial, firstLineExceedsLimit = truncation.FirstLineExceedsLimit,
                    maxLines = 9007199254740991L, maxBytes = truncation.MaxBytes };
            }
            return Result(truncation.Content + (notices.Count == 0 ? "" : "\n\n[" + string.Join(". ", notices) + "]"), details.Count == 0 ? null : details);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return ToolResult.Error(ToolFailureKind.ExecutionError, error.Message); }

        string ownerPath(string result) => _paths.Absolute(Path.GetFullPath(result, target));
    }
    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static bool ValidScalars(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\0') return false;
            if (char.IsHighSurrogate(text[index])) { if (++index == text.Length || !char.IsLowSurrogate(text[index])) return false; }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }
    private static ToolResult Result(string text, Dictionary<string, object?>? details) => details is null
        ? new ToolResult([new TextContent(text)], JsonData.EmptyObject).WithProperty("details", null)
        : new([new TextContent(text)], JsonData.Parse(JsonSerializer.Serialize(details)));
}
