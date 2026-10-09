// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/bash.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Processes;

/// <summary>Explicit Bash argv adapter. Hosts execute it through the mandatory final-action ToolInvoker policy.</summary>
public sealed class BashTool : IToolArgumentSchemaAdapter
{
    private readonly IProcessRunner _runner;
    private readonly BashToolOptions _options;
    private readonly Func<string> _nextSpillFileName;
    public string Name => "bash";
    /// <summary>Source createShellToolDefinition for bash: description, TypeBox parameters and constrainedSampling.</summary>
    public JsonData Declaration { get; } = SourceDeclaration;
    /// <summary>Source bashSchema (TypeBox), checked by validateToolArguments before the tool runs.</summary>
    public ToolArgumentSchema? ArgumentSchema => ToolArgumentSchema.FromDeclaration(Declaration, ToolSchemaOrigin.TypeBox);
    public static JsonData SourceDeclaration { get; } = JsonData.Parse("""
        {"name":"bash","description":"Execute a bash command in the current working directory. Returns stdout and stderr. Output is truncated to last 2000 lines or 50KB (whichever is hit first). If truncated, full output is saved to a temp file. Optionally provide a timeout in seconds.","parameters":{"type":"object","properties":{"command":{"type":"string","description":"Shell command to execute"},"timeout":{"type":"number","description":"Timeout in seconds (optional, no default timeout)"}},"required":["command"]},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}
        """);
    /// <summary>Source MAX_TIMEOUT_MS.</summary>
    public const double MaximumTimeoutMilliseconds = 2_147_483_647;
    /// <summary>Source promptGuidelines: the PI_* guideline only when session variables are exposed.</summary>
    public ImmutableArray<string> PromptGuidelines => BuiltinToolPrompts.BashGuidelines(_options.ExposeSessionEnvironment);

    /// <summary>
    /// Source bashOutputSchema (1.0.3 shortened descriptions): the structuredContent shape for programmatic callers.
    /// It is tool metadata, not part of the model-facing declaration. Authored rendering of the TypeBox schema.
    /// </summary>
    public static JsonData OutputSchema { get; } = JsonData.Parse("""
        {"type":"object","properties":{"output":{"type":"string","description":"Combined stdout and stderr, possibly truncated"},"truncated":{"type":"boolean"},"full_output_path":{"type":"string","description":"Full output, when truncated"},"exit_code":{"type":"number"},"wall_time_seconds":{"type":"number"}},"required":["output","truncated","exit_code","wall_time_seconds"]}
        """);

    public BashTool(IProcessRunner runner, BashToolOptions options, Func<string>? nextSpillFileName = null)
    {
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumCommandCharacters is < 1 or > 96_000 || options.MaximumArgumentCharacters is < 1 or > 96_000 ||
            options.ShellArguments.IsDefault || options.ShellArguments.Length > 16 || options.ShellArguments.Any(argument => !Text(argument)) ||
            !Enum.IsDefined(options.CommandTransport) || options.CommandPrefix is { } prefix && (!Text(prefix) || prefix.Length > 12_000) ||
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
        // Source resolveSpawnContext: the prefixed command, the environment with the current PI_* values, then spawnHook.
        var environment = BashSessionEnvironment.Apply(_options.Environment,
            _options.ExposeSessionEnvironment ? _options.SessionEnvironment?.Invoke() : null);
        var context = new BashSpawnContext(Resolve(input.Command), _options.WorkingDirectory, environment);
        if (_options.SpawnHook is { } hook) context = hook(context) ?? throw Invalid();
        // Source spawn(shell, [...args, command]): Node rejects an argument with a NUL byte.
        if (_options.CommandTransport == ShellCommandTransport.Argv && context.Command.Contains('\0') &&
            NodeArgumentErrors.SpawnArguments(Shell.CommandArguments(context.Command)) is { } spawnError)
            throw new ToolSourceErrorException(spawnError);
        if (!Text(context.Command) || context.Command.Length > _options.MaximumCommandCharacters + (_options.CommandPrefix?.Length + 1 ?? 0) &&
            _options.SpawnHook is null || !Absolute(context.WorkingDirectory) || !EnvironmentValid(context.Environment)) throw Invalid();
        var arguments = new Dictionary<string, object?> { ["command"] = input.Command, ["outputPath"] = outputPath };
        if (input.TimeoutToken is { } timeout) arguments["timeout"] = timeout;
        if (_options.CommandTransport == ShellCommandTransport.Stdin) arguments["standardInput"] = context.Command;
        var action = new PreparedToolAction(Name, Name, PreparedToolActionKind.Command, _options.Executable,
            JsonData.Parse(JsonSerializer.Serialize(arguments)), Shell.CommandArguments(context.Command),
            context.WorkingDirectory, context.Environment.ToImmutableDictionary(StringComparer.Ordinal));
        return ValueTask.FromResult(action);
    }

    private ShellConfiguration Shell => new(_options.Executable, _options.ShellArguments, _options.CommandTransport);

    /// <summary>Node's child_process.spawn error when the operating system refuses the command line: on Windows CreateProcess takes at
    /// most 32,767 characters (libuv reports ENAMETOOLONG); on Unix one argument is at most 128 KiB (E2BIG).</summary>
    private static string? SpawnLimitError(PreparedToolAction action) => NodeArgumentErrors.SpawnLimit(action.Target, action.CommandArguments);
    /// <summary>Source <c>commandPrefix ? `${commandPrefix}\n${command}` : command</c>.</summary>
    private string Resolve(string command) => string.IsNullOrEmpty(_options.CommandPrefix) ? command : _options.CommandPrefix + "\n" + command;

    public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (action is null) return ValueTask.FromResult(false);
            var input = Parse(action.Arguments, normalized: true);
            // Without a spawn hook the shell command is exactly the prefixed model command; a hook may rewrite it.
            var shellCommand = _options.CommandTransport == ShellCommandTransport.Stdin ? input.StandardInput
                : action.CommandArguments.IsDefault || action.CommandArguments.Length == 0 ? null : action.CommandArguments[^1];
            var valid = action.ToolName == Name && action.Operation == Name && action.Kind == PreparedToolActionKind.Command &&
                action.Target == _options.Executable && File.Exists(action.Target) && shellCommand is not null && Text(shellCommand) &&
                (_options.SpawnHook is not null || shellCommand == Resolve(input.Command)) &&
                !action.CommandArguments.IsDefault && action.CommandArguments.SequenceEqual(Shell.CommandArguments(shellCommand)) &&
                (input.StandardInput is null) == (_options.CommandTransport == ShellCommandTransport.Argv) &&
                Absolute(action.WorkingDirectory) && EnvironmentValid(action.Environment) && SpillPath(input.OutputPath);
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
        // Source resolveTimeoutMs and the working-directory check run inside exec, after the initial empty update.
        string? setupError = null;
        if (input.Timeout is { } requested && requested <= 0) setupError = "Invalid timeout: must be a finite number of seconds";
        else if (input.Timeout is { } large && large * 1000 > MaximumTimeoutMilliseconds)
            setupError = "Invalid timeout: maximum is " + (MaximumTimeoutMilliseconds / 1000).ToString("R", CultureInfo.InvariantCulture) + " seconds";
        else if (!Directory.Exists(action.WorkingDirectory))
            setupError = $"Working directory does not exist: {action.WorkingDirectory}\nCannot execute bash commands.";
        else if (SpawnLimitError(action) is { } spawnError) setupError = spawnError;
        if (setupError is not null)
        {
            try { await ToolProgressDelivery.ReportAndWaitAsync(onProgress, new([], JsonData.Null), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return ToolResult.Error(ToolFailureKind.Canceled, "Command aborted"); }
            return ToolResult.Error(ToolFailureKind.ExecutionError, setupError);
        }
        ProcessOutputSnapshot? lastOutput = null;
        try
        {
            // Source's initial empty update. Native required Details uses explicit null for JS undefined.
            await ToolProgressDelivery.ReportAndWaitAsync(onProgress, new([], JsonData.Null), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var request = new ProcessRequest(action.Target, action.CommandArguments, action.WorkingDirectory!,
                action.Environment, input.OutputPath!, input.Timeout)
            { StandardInput = input.StandardInput is null ? null : System.Text.Encoding.UTF8.GetBytes(input.StandardInput) };
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

    private sealed record Input(string Command, double? Timeout, JsonElement? TimeoutToken, string? OutputPath, string? StandardInput = null);
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
            // Source bashSchema admits additional properties; execute reads only command and timeout.
            if (!names.Add(property.Name) || normalized && property.Name is not ("command" or "timeout" or "outputPath" or "standardInput"))
                throw Invalid();
        if (!value.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String) throw Invalid();
        var text = command.GetString()!;
        // A NUL byte reaches spawn, which reports it (PrepareAsync); the final action never carries one.
        if (!Text(text, allowNul: !normalized) || text.Length > _options.MaximumCommandCharacters) throw Invalid();
        double? seconds = null; JsonElement? token = null;
        // Pi validation.ts normalizeOptionalNulls: an optional property sent as null (strict tool schemas make optional properties nullable) is absent.
        if (value.TryGetProperty("timeout", out var timeout) && timeout.ValueKind != JsonValueKind.Null)
        {
            // Out-of-range values reach execution, which reports the source resolveTimeoutMs error.
            if (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetDouble(out var number) || !double.IsFinite(number)) throw Invalid();
            seconds = number; token = timeout.Clone();
        }
        string? output = null, standardInput = null;
        if (normalized)
        {
            if (!value.TryGetProperty("outputPath", out var path) || path.ValueKind != JsonValueKind.String) throw Invalid();
            output = path.GetString();
            if (!SpillPath(output)) throw Invalid();
            if (value.TryGetProperty("standardInput", out var stdin))
            {
                if (stdin.ValueKind != JsonValueKind.String || !Text(stdin.GetString())) throw Invalid();
                standardInput = stdin.GetString();
            }
        }
        return new(text, seconds, token, output, standardInput);
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
            if (characters > (OperatingSystem.IsWindows() ? 32767 : 128 * 1024)) return false; // The Windows environment block limit; Unix argv+env share a larger budget.
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
            output.Truncation is null || output.Truncation.Content != output.Content || output.RawBytes < 0 ||
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
    private static ToolResult Failure(ProcessOutputSnapshot? output, ToolFailureKind kind, string status, string empty = "") =>
        new([new TextContent(Append(output is null ? "" : Format(output, empty), status))],
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
            return Failure(result.Output, ToolFailureKind.ExecutionError, "Command terminated without an exit code", "(no output)");
        var text = Format(result.Output, "(no output)");
        if (code != 0) text = Append(text, "Command exited with code " + code.ToString(CultureInfo.InvariantCulture));
        var structured = new Dictionary<string, object?> { ["output"] = result.StructuredOutput.Content,
            ["truncated"] = result.StructuredOutput.Truncated, ["exit_code"] = code, ["wall_time_seconds"] = result.WallTimeSeconds };
        if (result.StructuredOutput.Truncated && result.Output.FullOutputPath is { } path) structured["full_output_path"] = path;
        return new([new TextContent(text)], Details(result.Output), IsError: code != 0)
        { StructuredContent = JsonData.Parse(JsonSerializer.Serialize(structured)) };
    }
}
