using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Processes;

/// <summary>Explicit Bash argv adapter. Hosts execute it through the mandatory final-action ToolInvoker policy.</summary>
public sealed class BashTool : IPreparedToolAdapter
{
    private readonly IProcessRunner _runner;
    private readonly BashToolOptions _options;
    private readonly Func<string> _nextSpillFileName;
    public string Name => "bash";
    public JsonData Declaration { get; } = JsonData.Parse("""
        {"name":"bash","description":"Execute a bash command in the current working directory. Returns stdout and stderr. Output is truncated to last 2000 lines or 50KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.","parameters":{"type":"object","properties":{"command":{"type":"string","description":"Shell command to execute"},"timeout":{"type":"number","description":"Timeout in seconds (optional, no default timeout)"}},"required":["command"],"additionalProperties":false},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}
        """);

    public BashTool(IProcessRunner runner, BashToolOptions options, Func<string>? nextSpillFileName = null)
    {
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumCommandCharacters is < 1 or > 12_000 || options.MaximumArgumentCharacters is < 1 or > 96_000 ||
            !Absolute(options.Executable) || !File.Exists(options.Executable) ||
            !Absolute(options.WorkingDirectory) || !Directory.Exists(options.WorkingDirectory) ||
            !Absolute(options.SpillDirectory) || !Directory.Exists(options.SpillDirectory) ||
            !EnvironmentValid(options.Environment))
            throw new ArgumentException("Invalid explicit Bash configuration.", nameof(options));
        _runner = runner;
        _options = options with { Environment = options.Environment.ToImmutableDictionary(StringComparer.Ordinal) };
        _nextSpillFileName = nextSpillFileName ?? (() => "pi-bash-" + Guid.NewGuid().ToString("N") + ".log");
    }

    public ToolInvoker CreateInvoker(IToolActionPolicy policy, IEnumerable<ToolActionTransform>? transforms = null,
        IEnumerable<ToolResultTransform>? resultTransforms = null) =>
        new([this], policy, transforms, resultTransforms, new(MaximumArgumentCharacters: _options.MaximumArgumentCharacters,
            MaximumActionCharacters: 192_000, MaximumActionEntries: 1026, MaximumResultCharacters: 512 * 1024)
            { MaximumStructuredContentCharacters = 8 * 1024 * 1024 });

    public ToolDefinition CreateDefinition(ToolInvoker invoker) =>
        new(Name, invoker ?? throw new ArgumentNullException(nameof(invoker)));

    public ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation is null || invocation.Call is null || invocation.AssistantMessage is null ||
            invocation.AssistantMessage.StopReason != StopReason.ToolUse || invocation.Call.Name != Name ||
            invocation.AssistantMessage.Content.IsDefault || invocation.SourceIndex < 0 ||
            invocation.SourceIndex >= invocation.AssistantMessage.Content.Length ||
            !ReferenceEquals(invocation.AssistantMessage.Content[invocation.SourceIndex], invocation.Call) ||
            !Text(invocation.Call.Id) || string.IsNullOrWhiteSpace(invocation.Call.Id))
            throw Invalid();
        var input = Parse(invocation.Call.Arguments, normalized: false);
        cancellationToken.ThrowIfCancellationRequested();
        var file = _nextSpillFileName();
        cancellationToken.ThrowIfCancellationRequested();
        if (!FileName(file)) throw Invalid();
        var outputPath = Path.Combine(_options.SpillDirectory, file);
        if (!SpillPath(outputPath)) throw Invalid();
        var arguments = new Dictionary<string, object?> { ["command"] = input.Command, ["outputPath"] = outputPath };
        if (input.TimeoutToken is { } timeout) arguments["timeout"] = timeout;
        var action = new PreparedToolAction(Name, Name, PreparedToolActionKind.Command, _options.Executable,
            JsonData.Parse(JsonSerializer.Serialize(arguments)), ["-c", input.Command],
            _options.WorkingDirectory, _options.Environment);
        return ValueTask.FromResult(action);
    }

    public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (action is null) return ValueTask.FromResult(false);
            var input = Parse(action.Arguments, normalized: true);
            var valid = action.ToolName == Name && action.Operation == Name && action.Kind == PreparedToolActionKind.Command &&
                action.Target == _options.Executable && File.Exists(action.Target) &&
                !action.CommandArguments.IsDefault && action.CommandArguments.Length == 2 &&
                action.CommandArguments[0] == "-c" && action.CommandArguments[1] == input.Command &&
                Absolute(action.WorkingDirectory) && Directory.Exists(action.WorkingDirectory) &&
                EnvironmentValid(action.Environment) && SpillPath(input.OutputPath);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(valid);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { return ValueTask.FromResult(false); }
    }

    public ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken cancellationToken) =>
        ExecuteAsync(action, static (_, _) => ValueTask.CompletedTask, cancellationToken);

    public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, ToolProgressCallback onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onProgress);
        if (cancellationToken.IsCancellationRequested) return ToolResult.Error(ToolFailureKind.Canceled, "Command aborted");
        if (!await ValidateAsync(action, cancellationToken).ConfigureAwait(false))
            return ToolResult.Error(ToolFailureKind.InvalidArguments, "Invalid or unsupported final Bash action.");
        var input = Parse(action.Arguments, normalized: true);
        ProcessOutputSnapshot? lastOutput = null;
        try
        {
            // Source's initial empty update. Native required Details uses explicit null for JS undefined.
            await ToolProgressDelivery.ReportAndWaitAsync(onProgress, new([], JsonData.Null), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ProcessRequest(action.Target, action.CommandArguments, action.WorkingDirectory!,
                action.Environment, input.OutputPath!, input.Timeout);
            var result = await _runner.RunAsync(request, async snapshot =>
            {
                CheckOutput(snapshot, input.OutputPath!); lastOutput = snapshot;
                await ToolProgressDelivery.ReportAndWaitAsync(onProgress,
                    new([new TextContent(snapshot.Content)], Details(snapshot, progress: true)),
                    cancellationToken).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            CheckResult(result, input.OutputPath!);
            // The runner has already awaited process/output cleanup. Preserve its known output even on cancellation.
            return Final(result, input.Timeout, cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Failure(lastOutput, ToolFailureKind.Canceled, "Command aborted"); }
        catch (Exception)
        { return Failure(lastOutput, ToolFailureKind.ExecutionError, "Command execution failed."); }
    }

    private sealed record Input(string Command, double? Timeout, JsonElement? TimeoutToken, string? OutputPath);
    private Input Parse(JsonData? arguments, bool normalized)
    {
        if (arguments is null) throw Invalid();
        var raw = arguments.ToString();
        if (raw.Length > _options.MaximumArgumentCharacters) throw Invalid();
        // Retained FromElement values may have admitted comments/trailing commas. Charge raw text, then parse strictly.
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 4 });
        var value = document.RootElement;
        if (value.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not ("command" or "timeout") &&
                !(normalized && property.Name == "outputPath")) throw Invalid();
        if (!value.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String) throw Invalid();
        var text = command.GetString()!;
        if (!Text(text) || text.Length > _options.MaximumCommandCharacters) throw Invalid();
        double? seconds = null; JsonElement? token = null;
        if (value.TryGetProperty("timeout", out var timeout))
        {
            if (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetDouble(out var number) ||
                !double.IsFinite(number) || number <= 0 || number * 1000 > int.MaxValue) throw Invalid();
            seconds = number; token = timeout.Clone();
        }
        string? output = null;
        if (normalized)
        {
            if (!value.TryGetProperty("outputPath", out var path) || path.ValueKind != JsonValueKind.String) throw Invalid();
            output = path.GetString();
            if (!SpillPath(output)) throw Invalid();
        }
        return new(text, seconds, token, output);
    }

    private bool SpillPath(string? path) => Absolute(path) && Directory.Exists(_options.SpillDirectory) &&
        string.Equals(Path.GetDirectoryName(path), _options.SpillDirectory, StringComparison.Ordinal) &&
        FileName(Path.GetFileName(path)) && !File.Exists(path) && !Directory.Exists(path);
    private static bool FileName(string? value) => value is not null && Text(value) && value.Length is > 0 and <= 128 &&
        value is not ("." or "..") && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static bool Absolute(string? value)
    {
        if (value is null || !Text(value) || value.Length is < 1 or > 4096) return false;
        try { return Path.IsPathFullyQualified(value) && !value.StartsWith(@"\\", StringComparison.Ordinal) && Path.GetFullPath(value) == value; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return false; }
    }
    private static bool EnvironmentValid(ImmutableDictionary<string, string>? values)
    {
        if (values is null || values.Count > 1024) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long characters = 2;
        foreach (var (key, value) in values)
        {
            if (!Text(key) || key.Length == 0 || key.Contains('=') || !names.Add(key) || !Text(value)) return false;
            characters += (long)key.Length + value.Length + 2;
            if (characters > 32767) return false;
        }
        return true;
    }
    private static bool Text(string? value, bool allowNul = false)
    {
        if (value is null) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (!allowNul && value[index] == '\0') return false;
            if (char.IsHighSurrogate(value[index]))
            { if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
    private static ArgumentException Invalid() => new("Invalid or unsupported Bash action.");

    private static void CheckOutput(ProcessOutputSnapshot output, string authorizedPath)
    {
        if (output is null || !Text(output.Content, allowNul: true) || output.Content.Length > 200_000 ||
            output.Truncation is null || output.Truncation.Content != output.Content || output.RawBytes is < 0 or > 64 * 1024 * 1024 ||
            output.LastLineBytes < 0 || output.FullOutputPath is not null && output.FullOutputPath != authorizedPath)
            throw new InvalidOperationException("Invalid process output.");
    }
    private static void CheckResult(ProcessRunResult result, string authorizedPath)
    {
        if (result is null) throw new InvalidOperationException("Invalid process result.");
        CheckOutput(result.Output, authorizedPath);
        if (!Enum.IsDefined(result.Status) || result.StructuredOutput is null ||
            !Text(result.StructuredOutput.Content, allowNul: true) || result.StructuredOutput.Content.Length > 2 * 1024 * 1024 ||
            !double.IsFinite(result.WallTimeSeconds) || result.WallTimeSeconds < 0 ||
            result.Diagnostics.IsDefault || result.Diagnostics.Any(value => !Enum.IsDefined(value)))
            throw new InvalidOperationException("Invalid process result.");
    }
    private static JsonData Details(ProcessOutputSnapshot output, bool progress = false)
    {
        var details = new Dictionary<string, object?>();
        if (output.Truncation.Truncated)
        {
            var value = output.Truncation;
            details["truncation"] = new { content = value.Content, truncated = true,
                truncatedBy = value.TruncatedBy == ToolOutputTruncationLimit.Lines ? "lines" : "bytes",
                totalLines = value.TotalLines, totalBytes = value.TotalBytes, outputLines = value.OutputLines,
                outputBytes = value.OutputBytes, lastLinePartial = value.LastLinePartial,
                firstLineExceedsLimit = value.FirstLineExceedsLimit, maxLines = value.MaxLines, maxBytes = value.MaxBytes };
        }
        if (output.FullOutputPath is not null) details["fullOutputPath"] = output.FullOutputPath;
        return details.Count == 0 && !progress ? JsonData.Null : JsonData.Parse(JsonSerializer.Serialize(details));
    }
    private static string Format(ProcessOutputSnapshot output, string empty)
    {
        var text = output.Content.Length == 0 ? empty : output.Content;
        var value = output.Truncation;
        if (!value.Truncated) return text;
        var start = value.TotalLines - value.OutputLines + 1;
        var end = value.TotalLines;
        return text + (value.LastLinePartial
            ? $"\n\n[Showing last {Size(value.OutputBytes)} of line {end} (line is {Size(output.LastLineBytes)}). Full output: {output.FullOutputPath}]"
            : value.TruncatedBy == ToolOutputTruncationLimit.Lines
                ? $"\n\n[Showing lines {start}-{end} of {value.TotalLines}. Full output: {output.FullOutputPath}]"
                : $"\n\n[Showing lines {start}-{end} of {value.TotalLines} ({Size(ToolOutputTruncator.DefaultMaxBytes)} limit). Full output: {output.FullOutputPath}]");
    }
    private static string Size(int bytes) => bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + "B" :
        bytes < 1024 * 1024 ? (bytes / 1024d).ToString("F1", CultureInfo.InvariantCulture) + "KB" :
        (bytes / (1024d * 1024)).ToString("F1", CultureInfo.InvariantCulture) + "MB";
    private static string Append(string text, string status) => text.Length == 0 ? status : text + "\n\n" + status;
    private static ToolResult Failure(ProcessOutputSnapshot? output, ToolFailureKind kind, string status) =>
        new([new TextContent(Append(output is null ? "" : Format(output, ""), status))],
            output is null ? JsonData.Null : Details(output), IsError: true, Failure: new(kind, status));
    private static ToolResult Final(ProcessRunResult result, double? timeout, bool canceled)
    {
        if (!result.CleanupConfirmed)
            return Failure(result.Output, ToolFailureKind.ExecutionError, "Command execution failed.");
        if (canceled || result.Status == ProcessRunStatus.Canceled)
            return Failure(result.Output, ToolFailureKind.Canceled, "Command aborted");
        if (result.Status == ProcessRunStatus.TimedOut)
            return Failure(result.Output, ToolFailureKind.ExecutionError,
                "Command timed out after " + timeout?.ToString("G", CultureInfo.InvariantCulture) + " seconds");
        if (!result.CapturedOutputComplete || result.Status == ProcessRunStatus.Failed || !result.Diagnostics.IsEmpty)
            return Failure(result.Output, ToolFailureKind.ExecutionError, "Command execution failed.");
        if (result.ExitCode is not { } code)
            return Failure(result.Output, ToolFailureKind.ExecutionError, "Command terminated without an exit code");
        var text = Format(result.Output, "(no output)");
        if (code != 0) text = Append(text, "Command exited with code " + code.ToString(CultureInfo.InvariantCulture));
        var structured = new Dictionary<string, object?> { ["output"] = result.StructuredOutput.Content,
            ["truncated"] = result.StructuredOutput.Truncated, ["exit_code"] = code, ["wall_time_seconds"] = result.WallTimeSeconds };
        if (result.StructuredOutput.Truncated && result.Output.FullOutputPath is { } path) structured["full_output_path"] = path;
        return new([new TextContent(text)], Details(result.Output), IsError: code != 0)
        { StructuredContent = JsonData.Parse(JsonSerializer.Serialize(structured)) };
    }
}
