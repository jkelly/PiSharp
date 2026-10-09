using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

public sealed record EditPreviewOptions(int MaximumReadBytes = 1_048_576, int MaximumPlanCharacters = 1_048_576,
    int MaximumArgumentCharacters = 524_288, int MaximumPathCharacters = 4096, int MaximumEdits = 64,
    DiffFormatterOptions? DiffOptions = null);

/// <summary>Preview is the owned source-shaped fulfilled body. Native admission/policy/cancellation failures leave it null.</summary>
public sealed record EditPreviewOutcome(JsonData? Preview, ToolResult Invocation);

/// <summary>Host read-only preview authority. Every compute call uses final-action policy; no queue, patch, reread or mutation.</summary>
public sealed class EditPreview
{
    public const string AuthorityName = "edit-preview";
    public const string Operation = "preview-edit";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly EditPreviewOptions _options;
    private readonly ToolInvoker _invoker;

    public EditPreview(string workingDirectory, string homeDirectory, IToolActionPolicy requiredReadPolicy,
        IFileOperations? operations = null, EditPreviewOptions? options = null, IFileAccessProbe? accessProbe = null,
        IEnumerable<ToolActionTransform>? transforms = null)
    {
        ArgumentNullException.ThrowIfNull(requiredReadPolicy);
        _options = options ?? new(); var diff = _options.DiffOptions ?? new();
        if (_options.MaximumReadBytes is < 1 or > 64 * 1024 * 1024 || _options.MaximumPlanCharacters is < 1 or > 64 * 1024 * 1024 ||
            _options.MaximumArgumentCharacters is < 1 or > 8 * 1024 * 1024 || _options.MaximumPathCharacters is < 1 or > 65_536 ||
            _options.MaximumEdits is < 1 or > 1024 || diff.MaximumOutputCharacters is < 1 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(options));
        DiffFormatter.ValidateOptions(diff);
        var adapter = new Adapter(workingDirectory, homeDirectory, operations ?? new LocalFileOperations(), accessProbe ?? new LocalFileAccessProbe(), _options, diff);
        _invoker = new([adapter], requiredReadPolicy, transforms, options: new(MaximumTools: 1,
            MaximumArgumentCharacters: _options.MaximumArgumentCharacters,
            MaximumActionCharacters: checked(_options.MaximumArgumentCharacters + _options.MaximumPathCharacters * 2 + 128),
            MaximumResultCharacters: checked(diff.MaximumOutputCharacters * 6 + _options.MaximumPathCharacters * 6 + 1024)));
    }

    public async ValueTask<EditPreviewOutcome> ComputeAsync(string path, ImmutableArray<TextEdit> edits, CancellationToken cancellationToken = default)
    {
        // This frame is explicitly authored by the calling host to use the accepted invoker; no provider/model execution is implied.
        var arguments = Arguments(path, edits);
        var call = new ToolCallContent("host-edit-preview", AuthorityName, arguments);
        var message = new AssistantMessage("native-host", "native-host", AuthorityName, 0, [call], TokenUsage.Zero, StopReason.ToolUse);
        var result = await _invoker.ExecuteAsync(new(message, call, 0), cancellationToken).ConfigureAwait(false);
        return new(result.IsError ? null : result.Details, result);
    }

    private JsonData Arguments(string path, ImmutableArray<TextEdit> edits)
    {
        try
        {
            if (path is null || path.Length == 0 || path.Length > _options.MaximumPathCharacters || edits.IsDefault || edits.Length > _options.MaximumEdits)
                return JsonData.Parse("{}");
            long characters = path.Length; CheckText(path);
            foreach (var edit in edits)
            {
                if (edit is null || edit.OldText is null || edit.NewText is null) return JsonData.Parse("{}");
                characters += (long)edit.OldText.Length + edit.NewText.Length;
                if (characters > _options.MaximumArgumentCharacters) return JsonData.Parse("{}");
                CheckText(edit.OldText); CheckText(edit.NewText);
            }
            return JsonData.Parse(JsonSerializer.Serialize(new { path, edits = edits.Select(edit => new { oldText = edit.OldText, newText = edit.NewText }) }));
        }
        catch (ArgumentException) { return JsonData.Parse("{}"); }
    }
    private static string CheckText(string text) { _ = Utf8.GetByteCount(text); return text; }

    private sealed class Adapter(string cwd, string home, IFileOperations operations, IFileAccessProbe access,
        EditPreviewOptions options, DiffFormatterOptions diff) : IPreparedToolAdapter
    {
        private readonly PathResolver _paths = new(cwd, home, operations, options.MaximumPathCharacters);
        public string Name => AuthorityName;
        private sealed record Input(string Path, string DisplayPath, ImmutableArray<TextEdit> Edits);

        public async ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var input = Parse(invocation.Call.Arguments, false);
            string target;
            try { target = _paths.Absolute(await operations.CanonicalizeAsync(_paths.Resolve(input.Path), token).ConfigureAwait(false)); }
            catch { token.ThrowIfCancellationRequested(); throw; }
            token.ThrowIfCancellationRequested();
            var arguments = JsonData.Parse(JsonSerializer.Serialize(new { path = target, displayPath = input.Path,
                edits = input.Edits.Select(edit => new { oldText = edit.OldText, newText = edit.NewText }) }));
            return new(Name, Operation, PreparedToolActionKind.Path, target, arguments, [], _paths.WorkingDirectory, ImmutableDictionary<string, string>.Empty);
        }
        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var input = Parse(action.Arguments, true);
                return ValueTask.FromResult(action.ToolName == Name && action.Operation == Operation && action.Kind == PreparedToolActionKind.Path &&
                    !action.CommandArguments.IsDefault && action.CommandArguments.IsEmpty && action.Environment is { Count: 0 } &&
                    action.WorkingDirectory == _paths.WorkingDirectory && action.Target == input.Path && action.Target == _paths.Absolute(action.Target));
            }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
            { return ValueTask.FromResult(false); }
        }
        public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        {
            if (!await ValidateAsync(action, token).ConfigureAwait(false)) return ToolResult.Error(ToolFailureKind.InvalidArguments, "Invalid final preview action.");
            var input = Parse(action.Arguments, true);
            try
            {
                try { await access.CheckAsync(action.Target, FileAccessModes.Read, token).ConfigureAwait(false); }
                catch (FileAccessProbeException error) { token.ThrowIfCancellationRequested(); return Error($"Could not edit file: {input.DisplayPath}. Error code: {error.Code}."); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { token.ThrowIfCancellationRequested(); return Error($"Could not edit file: {input.DisplayPath}. Access probe failed."); }
                token.ThrowIfCancellationRequested();
                ReadOnlyMemory<byte> memory;
                try { memory = await operations.ReadAsync(action.Target, options.MaximumReadBytes, token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (FileToolException) { token.ThrowIfCancellationRequested(); throw; }
                catch { token.ThrowIfCancellationRequested(); return Error("Cannot read file contents for edit preview."); }
                token.ThrowIfCancellationRequested();
                if (memory.Length > options.MaximumReadBytes) throw new FileToolException(FileToolFailure.ResourceLimit);
                var bytes = memory.ToArray(); var text = Decode(bytes);
                var normalized = EditMatcher.NormalizeToLF(text.StartsWith('\ufeff') ? text[1..] : text);
                token.ThrowIfCancellationRequested();
                var plan = EditPlan.Create(normalized, input.Edits, input.DisplayPath, new(options.MaximumPlanCharacters, options.MaximumEdits));
                token.ThrowIfCancellationRequested();
                var display = DiffFormatter.FormatDisplay(plan.BaseContent, plan.NewContent, diff, token);
                token.ThrowIfCancellationRequested();
                var body = new Dictionary<string, object> { ["diff"] = display.Diff };
                if (display.FirstChangedLine is { } line) body.Add("firstChangedLine", line);
                return new([], JsonData.Parse(JsonSerializer.Serialize(body)));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (EditPlanException error) { token.ThrowIfCancellationRequested(); return Error(error.Message); }
            catch (FileToolException error) { token.ThrowIfCancellationRequested(); return Error(error.Message); }
            catch { token.ThrowIfCancellationRequested(); return Error("Cannot compute edit preview."); }
        }
        private Input Parse(JsonData arguments, bool normalized)
        {
            if (arguments is null || arguments.ToString().Length > options.MaximumArgumentCharacters || arguments.Value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Invalid preview input.");
            var value = arguments.Value;
            foreach (var property in value.EnumerateObject())
                if (property.Name is not ("path" or "edits") && !(normalized && property.Name == "displayPath")) throw new ArgumentException("Invalid preview property.");
            var path = Text(value.GetProperty("path")); var display = normalized ? Text(value.GetProperty("displayPath")) : path;
            if (path.Length == 0 || display.Length == 0 || path.Length > options.MaximumPathCharacters || display.Length > options.MaximumPathCharacters)
                throw new ArgumentException("Invalid preview path.");
            var array = value.GetProperty("edits");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > options.MaximumEdits) throw new ArgumentException("Invalid preview edit array.");
            var edits = ImmutableArray.CreateBuilder<TextEdit>(array.GetArrayLength());
            foreach (var edit in array.EnumerateArray())
            {
                if (edit.ValueKind != JsonValueKind.Object || edit.EnumerateObject().Any(property => property.Name is not ("oldText" or "newText"))) throw new ArgumentException("Invalid preview replacement.");
                edits.Add(new(Text(edit.GetProperty("oldText")), Text(edit.GetProperty("newText"))));
            }
            return new(path, display, edits.ToImmutable());
        }
        private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? CheckText(value.GetString()!) : throw new ArgumentException("Preview text is required.");
        // Source readFile(path, "utf-8"): any content previews, undecodable bytes as U+FFFD.
        private static string Decode(byte[] bytes) => EditTool.DecodeText(bytes);
        private static ToolResult Error(string message) => new([], JsonData.Parse(JsonSerializer.Serialize(new { error = message })));
    }
}
