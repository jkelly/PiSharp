using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

public sealed record EditToolOptions(int MaximumInputBytes = 1_048_576, int MaximumOutputBytes = 1_048_576,
    int MaximumArgumentCharacters = 524_288, int MaximumPathCharacters = 4096, int MaximumEdits = 64,
    DiffFormatterOptions? DiffOptions = null);

/// <summary>Prepared edit adapter. Hosts share its required mutation queue with all coordinated writers and route it through policy.</summary>
public sealed class EditTool : IPreparedToolAdapter
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly IFileOperations _operations;
    private readonly IFileAccessProbe _accessProbe;
    private readonly FileMutationQueue _mutations;
    private readonly PathResolver _paths;
    private readonly EditToolOptions _options;
    private readonly DiffFormatterOptions _diffOptions;
    public string Name => "edit";
    public JsonData Declaration { get; }
    public FileMutationQueueSnapshot MutationSnapshot => _mutations.Snapshot;

    public EditTool(string workingDirectory, string homeDirectory, FileMutationQueue mutationQueue,
        IFileOperations? operations = null, EditToolOptions? options = null, IFileAccessProbe? accessProbe = null)
    {
        ArgumentNullException.ThrowIfNull(mutationQueue);
        _options = options ?? new(); _diffOptions = _options.DiffOptions ?? new();
        if (_options.MaximumInputBytes is < 1 or > 64 * 1024 * 1024 || _options.MaximumOutputBytes is < 1 or > 64 * 1024 * 1024 ||
            _options.MaximumArgumentCharacters is < 1 or > 8 * 1024 * 1024 || _options.MaximumPathCharacters is < 1 or > 65_536 || _options.MaximumEdits is < 1 or > 1024 ||
            _diffOptions.MaximumOutputCharacters is < 1 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(options));
        DiffFormatter.ValidateOptions(_diffOptions);
        _operations = operations ?? new LocalFileOperations(); _mutations = mutationQueue;
        _accessProbe = accessProbe ?? new LocalFileAccessProbe();
        _paths = new(workingDirectory, homeDirectory, _operations, _options.MaximumPathCharacters);
        Declaration = JsonData.Parse("""{"name":"edit","description":"Edit one UTF-8 text file using unique non-overlapping replacements matched against original content. Includes bounded native diff/patch and an exact-byte outside-change check.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Path to the file to edit (relative or absolute)"},"edits":{"type":"array","items":{"type":"object","properties":{"oldText":{"type":"string","description":"Unique original text, without overlap with other edits"},"newText":{"type":"string","description":"Replacement text"}},"required":["oldText","newText"],"additionalProperties":false},"description":"One or more replacements matched against the original file"}},"required":["path","edits"],"additionalProperties":false},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}""");
    }

    public ToolInvoker CreateInvoker(IToolActionPolicy policy, IEnumerable<ToolActionTransform>? transforms = null,
        IEnumerable<ToolResultTransform>? resultTransforms = null) => new([this], policy, transforms, resultTransforms,
            new(MaximumArgumentCharacters: _options.MaximumArgumentCharacters,
                MaximumActionCharacters: checked(_options.MaximumArgumentCharacters + 2 * _options.MaximumPathCharacters + 128),
                MaximumResultCharacters: checked(_diffOptions.MaximumOutputCharacters * 12 + _options.MaximumPathCharacters * 6 + 1024)));
    public ToolDefinition CreateDefinition(ToolInvoker invoker) => new(Name, invoker ?? throw new ArgumentNullException(nameof(invoker)));

    public async ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var input = Parse(invocation.Call.Arguments, normalized: false);
        var target = _paths.Absolute(await _operations.CanonicalizeAsync(_paths.Resolve(input.Path), token).ConfigureAwait(false));
        token.ThrowIfCancellationRequested();
        var arguments = JsonData.Parse(JsonSerializer.Serialize(new { path = target, displayPath = input.Path,
            edits = input.Edits.Select(edit => new { oldText = edit.OldText, newText = edit.NewText }) }));
        return new(Name, Name, PreparedToolActionKind.Path, target, arguments, [], _paths.WorkingDirectory, ImmutableDictionary<string, string>.Empty);
    }
    public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var input = Parse(action.Arguments, normalized: true);
            return ValueTask.FromResult(action.ToolName == Name && action.Operation == Name && action.Kind == PreparedToolActionKind.Path &&
                !action.CommandArguments.IsDefault && action.CommandArguments.IsEmpty && action.Environment is { Count: 0 } &&
                action.WorkingDirectory == _paths.WorkingDirectory && action.Target == input.Path && action.Target == _paths.Absolute(action.Target));
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException) { return ValueTask.FromResult(false); }
    }
    public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
    {
        if (!await ValidateAsync(action, token).ConfigureAwait(false)) return ToolResult.Error(ToolFailureKind.InvalidArguments, "Invalid final edit action.");
        var input = Parse(action.Arguments, normalized: true);
        if (input.Edits.IsEmpty) return Failure(ToolFailureKind.InvalidArguments,
            "Edit tool input is invalid. edits must contain at least one replacement.", "EmptyEdits", false, false);
        return await _mutations.RunAsync<ToolResult>(action.Target, async operationToken =>
        {
            var writeAttempted = false; var writeCompleted = false;
            try
            {
                operationToken.ThrowIfCancellationRequested();
                try { await _accessProbe.CheckAsync(action.Target, FileAccessModes.Read | FileAccessModes.Write, operationToken).ConfigureAwait(false); }
                catch (FileAccessProbeException error)
                {
                    operationToken.ThrowIfCancellationRequested();
                    return Failure(ToolFailureKind.ExecutionError, $"Could not edit file: {input.DisplayPath}. Error code: {error.Code}.",
                        "EditAccessFailure", false, false);
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    operationToken.ThrowIfCancellationRequested();
                    return Failure(ToolFailureKind.ExecutionError, $"Could not edit file: {input.DisplayPath}. Access probe failed.",
                        "EditAccessFailure", false, false);
                }
                operationToken.ThrowIfCancellationRequested();
                var original = await ReadOwnedAsync(action.Target, operationToken).ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested(); var text = DecodeText(original);
                var plan = EditPlan.Create(text, input.Edits, input.DisplayPath,
                    new(MaximumCharacters: Math.Max(_options.MaximumInputBytes, _options.MaximumOutputBytes), MaximumEdits: _options.MaximumEdits));
                operationToken.ThrowIfCancellationRequested();
                var replacement = Utf8.GetBytes(plan.Content);
                if (replacement.Length > _options.MaximumOutputBytes) throw EditPlan.Limit();
                var formatted = DiffFormatter.Format(input.DisplayPath, plan.BaseContent, plan.NewContent, _diffOptions, operationToken);
                // Construct/own result metadata before the only write; bounds/finalization failures cannot follow a successful effect here.
                var details = JsonData.Parse(JsonSerializer.Serialize(new { diff = formatted.Diff, patch = formatted.Patch, firstChangedLine = formatted.FirstChangedLine }));
                var current = await ReadOwnedAsync(action.Target, operationToken).ConfigureAwait(false);
                operationToken.ThrowIfCancellationRequested();
                if (!current.AsSpan().SequenceEqual(original)) return Failure(ToolFailureKind.ExecutionError, "File changed outside the edit operation; no edit write was attempted.", "OutsideChange", false, false);
                writeAttempted = true;
                await _operations.WriteAsync(action.Target, replacement, operationToken).ConfigureAwait(false); writeCompleted = true;
                if (operationToken.IsCancellationRequested) return Failure(ToolFailureKind.Canceled,
                    "Edit write completed after cancellation was requested.", "EditCompletedAfterCancellation", writeAttempted, writeCompleted);
                return new([new TextContent($"Successfully replaced {input.Edits.Length} block(s) in {input.DisplayPath}.")], details);
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            { return Failure(ToolFailureKind.Canceled, "Edit canceled; an attempted overwrite may have changed the file.", "EditCanceled", writeAttempted, writeCompleted); }
            catch (EditPlanException error) { return Failure(ToolFailureKind.ExecutionError, error.Message, error.Failure.ToString(), writeAttempted, writeCompleted); }
            catch (FileToolException error) { return Failure(ToolFailureKind.ExecutionError, error.Message, error.Failure.ToString(), writeAttempted, writeCompleted); }
            catch (Exception) { return Failure(ToolFailureKind.ExecutionError, "Cannot edit file contents; an attempted overwrite may have changed the file.", "EditIoFailure", writeAttempted, writeCompleted); }
        }, token).ConfigureAwait(false);
    }

    private sealed record Input(string Path, string DisplayPath, ImmutableArray<TextEdit> Edits);
    private Input Parse(JsonData arguments, bool normalized)
    {
        if (arguments is null || arguments.ToString().Length > _options.MaximumArgumentCharacters || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid edit arguments.");
        var value = arguments.Value;
        foreach (var property in value.EnumerateObject())
            if (property.Name is not ("path" or "edits") && !(normalized ? property.Name == "displayPath" : property.Name is "oldText" or "newText"))
                throw new ArgumentException("Unsupported edit argument.");
        var path = Text(value.GetProperty("path")); var display = normalized ? Text(value.GetProperty("displayPath")) : path;
        if (path.Length == 0 || display.Length == 0 || path.Length > _options.MaximumPathCharacters || display.Length > _options.MaximumPathCharacters)
            throw new ArgumentException("Invalid edit path.");
        var edits = ImmutableArray.CreateBuilder<TextEdit>();
        var hasArray = false;
        var legacy = !normalized && value.TryGetProperty("oldText", out var legacyOld) && legacyOld.ValueKind == JsonValueKind.String &&
            value.TryGetProperty("newText", out var legacyNew) && legacyNew.ValueKind == JsonValueKind.String;
        if (value.TryGetProperty("edits", out var supplied))
        {
            if (!normalized && supplied.ValueKind == JsonValueKind.String)
            {
                try { var decoded = JsonData.Parse(supplied.GetString()!); supplied = decoded.Value; } // JsonData owns the parsed element.
                catch (Exception error) when (legacy && error is JsonException or InvalidOperationException or ArgumentException) { supplied = default; }
            }
            if (supplied.ValueKind == JsonValueKind.Array) { hasArray = true; foreach (var edit in supplied.EnumerateArray()) Add(edit); }
            else if (!normalized && IsEdit(supplied)) Add(supplied);
            else if (!legacy) throw new ArgumentException("Invalid edits array.");
        }
        if (legacy) AddPair(Text(value.GetProperty("oldText")), Text(value.GetProperty("newText")));
        if (edits.Count == 0 && !hasArray) throw new ArgumentException("Edit tool input is invalid. edits must contain at least one replacement.");
        return new(path, display, edits.ToImmutable());

        void Add(JsonElement edit)
        {
            if (!IsEdit(edit) || edit.EnumerateObject().Any(property => property.Name is not ("oldText" or "newText"))) throw new ArgumentException("Invalid edit replacement.");
            AddPair(Text(edit.GetProperty("oldText")), Text(edit.GetProperty("newText")));
        }
        void AddPair(string oldText, string newText)
        {
            if (edits.Count >= _options.MaximumEdits || Utf8.GetByteCount(newText) > _options.MaximumOutputBytes) throw new ArgumentException("Edit replacements exceed configured limits.");
            edits.Add(new(oldText, newText));
        }
    }
    private static bool IsEdit(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("oldText", out var old) && old.ValueKind == JsonValueKind.String && value.TryGetProperty("newText", out var next) && next.ValueKind == JsonValueKind.String;
    private static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Required edit string is absent.");
        var text = value.GetString()!; if (text.Contains('\0')) throw new ArgumentException("Unsupported edit text.");
        _ = Utf8.GetByteCount(text); return text;
    }
    private async ValueTask<byte[]> ReadOwnedAsync(string path, CancellationToken token)
    {
        var memory = await _operations.ReadAsync(path, _options.MaximumInputBytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); if (memory.Length > _options.MaximumInputBytes) throw new FileToolException(FileToolFailure.ResourceLimit);
        return memory.ToArray();
    }
    private static string DecodeText(byte[] bytes)
    {
        try
        {
            var text = Utf8.GetString(bytes);
            if (text.Any(character => char.IsControl(character) && character is not ('\t' or '\n' or '\r'))) throw new FileToolException(FileToolFailure.UnsupportedContent);
            if (text.StartsWith("GIF87a", StringComparison.Ordinal) || text.StartsWith("GIF89a", StringComparison.Ordinal) ||
                (text.Length >= 12 && text.StartsWith("RIFF", StringComparison.Ordinal) && text[8..12] == "WEBP")) throw new FileToolException(FileToolFailure.UnsupportedContent);
            return text;
        }
        catch (DecoderFallbackException) { throw new FileToolException(FileToolFailure.UnsupportedContent); }
    }
    private static ToolResult Failure(ToolFailureKind kind, string message, string code, bool writeAttempted, bool writeCompleted) =>
        new([new TextContent(message)], JsonData.Parse(JsonSerializer.Serialize(new { fileOperation = new { code, writeAttempted, writeCompleted } })), true, Failure: new(kind, message));
}
