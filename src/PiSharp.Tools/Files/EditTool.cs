// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/edit.ts.
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

/// <summary>Memory bounds for the edit tool. Pi has no size limits; the defaults are the largest admitted values. The display
/// diff is bounded separately: an edit whose diff exceeds <see cref="DiffOptions"/> still succeeds, with the diff omitted.</summary>
public sealed record EditToolOptions(int MaximumInputBytes = 64 * 1024 * 1024, int MaximumOutputBytes = 64 * 1024 * 1024,
    int MaximumArgumentCharacters = 8 * 1024 * 1024, int MaximumPathCharacters = 4096, int MaximumEdits = 1024,
    DiffFormatterOptions? DiffOptions = null)
{
    /// <summary>The diff bounds used when <see cref="DiffOptions"/> is absent.</summary>
    public static DiffFormatterOptions DefaultDiffOptions { get; } = new(MaximumLines: 1_000_000, MaximumWork: 50_000_000,
        MaximumTraceCells: 4_000_000, MaximumOutputCharacters: 262_144);
}

/// <summary>Prepared edit adapter. Hosts share its required mutation queue with all coordinated writers and route it through policy.</summary>
public sealed class EditTool : IToolArgumentSchemaAdapter, IInitialToolArgumentPreparationAdapter
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
    /// <summary>Source editSchema (TypeBox), checked by validateToolArguments after prepareEditArguments.</summary>
    public ToolArgumentSchema? ArgumentSchema { get; } = ToolArgumentSchema.FromDeclaration(SourceDeclaration, ToolSchemaOrigin.TypeBox);
    public FileMutationQueueSnapshot MutationSnapshot => _mutations.Snapshot;

    public EditTool(string workingDirectory, string homeDirectory, FileMutationQueue mutationQueue,
        IFileOperations? operations = null, EditToolOptions? options = null, IFileAccessProbe? accessProbe = null)
    {
        ArgumentNullException.ThrowIfNull(mutationQueue);
        _options = options ?? new(); _diffOptions = _options.DiffOptions ?? EditToolOptions.DefaultDiffOptions;
        if (_options.MaximumInputBytes is < 1 or > 64 * 1024 * 1024 || _options.MaximumOutputBytes is < 1 or > 64 * 1024 * 1024 ||
            _options.MaximumArgumentCharacters is < 1 or > 8 * 1024 * 1024 || _options.MaximumPathCharacters is < 1 or > 65_536 || _options.MaximumEdits is < 1 or > 1024 ||
            _diffOptions.MaximumOutputCharacters is < 1 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(options));
        DiffFormatter.ValidateOptions(_diffOptions);
        _operations = operations ?? new LocalFileOperations(); _mutations = mutationQueue;
        _accessProbe = accessProbe ?? new LocalFileAccessProbe();
        _paths = new(workingDirectory, homeDirectory, _operations, _options.MaximumPathCharacters);
        Declaration = SourceDeclaration;
    }

    /// <summary>Source createEditToolDefinition name, description, TypeBox parameters and constrainedSampling.</summary>
    public static JsonData SourceDeclaration { get; } = JsonData.Parse("""{"name":"edit","description":"Edit a single file using exact text replacement. Every edits[].oldText must match a unique, non-overlapping region of the original file. If two changes affect the same block or nearby lines, merge them into one edit instead of emitting overlapping edits. Do not include large unchanged regions just to connect distant changes.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Path to the file to edit (relative or absolute)"},"edits":{"type":"array","items":{"type":"object","properties":{"oldText":{"type":"string","description":"Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call."},"newText":{"type":"string","description":"Replacement text for this targeted edit."}},"required":["oldText","newText"]},"description":"One or more targeted replacements. Each edit is matched against the original file, not incrementally. Do not include overlapping or nested edits. If two changes touch the same block or nearby lines, merge them into one edit instead."}},"required":["path","edits"]},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}""");

    public ToolInvoker CreateInvoker(IToolActionPolicy policy, IEnumerable<ToolActionTransform>? transforms = null,
        IEnumerable<ToolResultTransform>? resultTransforms = null) => new([this], policy, transforms, resultTransforms,
            new(MaximumArgumentCharacters: _options.MaximumArgumentCharacters,
                MaximumActionCharacters: checked(_options.MaximumArgumentCharacters + 2 * _options.MaximumPathCharacters + 128),
                MaximumResultCharacters: checked(_diffOptions.MaximumOutputCharacters * 12 + _options.MaximumPathCharacters * 6 + 1024)));
    public ToolDefinition CreateDefinition(ToolInvoker invoker) => new(Name, invoker ?? throw new ArgumentNullException(nameof(invoker)));

    /// <summary>Source prepareArguments (prepareEditArguments), run before validateToolArguments checks the schema.</summary>
    public ValueTask<JsonData> PrepareInitialArgumentsAsync(ToolInvocation invocation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(PrepareEditArguments(invocation.Call.Arguments));
    }

    /// <summary>Source prepareEditArguments: edits sent as a JSON string or as a single edit object become an edits array, and a
    /// legacy top-level oldText/newText pair is appended to the edits.</summary>
    public static JsonData PrepareEditArguments(JsonData arguments)
    {
        if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object) return arguments!;
        JsonObject args;
        try { args = JsonNode.Parse(arguments.ToString())!.AsObject(); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return arguments; }
        var changed = false;
        if (args["edits"] is JsonValue encoded && encoded.GetValueKind() == JsonValueKind.String)
        {
            try
            {
                var parsed = JsonNode.Parse(encoded.GetValue<string>());
                if (parsed is JsonArray) { args["edits"] = parsed; changed = true; }
                else if (IsSingleEdit(parsed)) { args["edits"] = new JsonArray(parsed); changed = true; }
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { }
        }
        else if (IsSingleEdit(args["edits"])) { args["edits"] = new JsonArray(args["edits"]!.DeepClone()); changed = true; }
        if (IsString(args["oldText"]) && IsString(args["newText"]))
        {
            var edits = args["edits"] is JsonArray existing ? existing.DeepClone().AsArray() : new JsonArray();
            edits.Add(new JsonObject { ["oldText"] = args["oldText"]!.DeepClone(), ["newText"] = args["newText"]!.DeepClone() });
            args.Remove("oldText"); args.Remove("newText");
            args["edits"] = edits; changed = true;
        }
        return changed ? JsonData.Parse(args.ToJsonString()) : arguments;

        static bool IsString(JsonNode? value) => value is JsonValue text && text.GetValueKind() == JsonValueKind.String;
        static bool IsSingleEdit(JsonNode? value) => value is JsonObject edit && IsString(edit["oldText"]) && IsString(edit["newText"]);
    }

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
                JsonData details;
                try
                {
                    var formatted = DiffFormatter.Format(input.DisplayPath, plan.BaseContent, plan.NewContent, _diffOptions, operationToken);
                    // Construct/own result metadata before the only write; bounds/finalization failures cannot follow a successful effect here.
                    details = JsonData.Parse(JsonSerializer.Serialize(new { diff = formatted.Diff, patch = formatted.Patch, firstChangedLine = formatted.FirstChangedLine }));
                }
                catch (EditPlanException error) when (error.Failure == EditPlanFailure.ResourceLimit)
                {
                    // Pi always edits; only the display diff is bounded natively. Keep the edit and omit the oversized diff.
                    details = JsonData.Parse(JsonSerializer.Serialize(new { diff = "", patch = "", firstChangedLine = FirstChangedLine(plan.BaseContent, plan.NewContent) }));
                }
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
            // Source editSchema admits additional properties; execute reads only path and edits.
            if (normalized && property.Name is not ("path" or "edits" or "displayPath"))
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
    private static int? FirstChangedLine(string before, string after)
    {
        var line = 1;
        for (var index = 0; index < Math.Min(before.Length, after.Length); index++)
        {
            if (before[index] != after[index]) return line;
            if (before[index] == '\n') line++;
        }
        return before.Length == after.Length ? null : line;
    }
    private static bool IsEdit(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("oldText", out var old) && old.ValueKind == JsonValueKind.String && value.TryGetProperty("newText", out var next) && next.ValueKind == JsonValueKind.String;
    private static string Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("Required edit string is absent.");
        // Source edits accept any string, including NUL; the strict encoder still rejects unpaired surrogates.
        var text = value.GetString()!; _ = Utf8.GetByteCount(text); return text;
    }
    private async ValueTask<byte[]> ReadOwnedAsync(string path, CancellationToken token)
    {
        var memory = await _operations.ReadAsync(path, _options.MaximumInputBytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested(); if (memory.Length > _options.MaximumInputBytes) throw new FileToolException(FileToolFailure.ResourceLimit);
        return memory.ToArray();
    }
    /// <summary>Source buffer.toString("utf-8"): control characters and NUL are ordinary text, and each undecodable sequence becomes
    /// U+FFFD (WHATWG maximal subparts, as Node decodes), so the edited file is written back as UTF-8 with those replacements.</summary>
    internal static string DecodeText(byte[] bytes) => LossyUtf8.GetString(bytes);
    private static readonly UTF8Encoding LossyUtf8 = new(false, false);
    private static ToolResult Failure(ToolFailureKind kind, string message, string code, bool writeAttempted, bool writeCompleted) =>
        new([new TextContent(message)], JsonData.Parse(JsonSerializer.Serialize(new { fileOperation = new { code, writeAttempted, writeCompleted } })), true, Failure: new(kind, message));
}
