// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/read.ts and core/tools/write.ts.
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;
using PiSharp.Tools.Images;

namespace PiSharp.Tools.Files;

/// <summary>Source read/write tools: bounded adapters, owned declarations and mandatory-policy invoker composition.</summary>
public sealed class ReadWriteTools
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    // Source buffer.toString("utf-8"): malformed sequences become U+FFFD and a BOM is kept.
    private static readonly Encoding LenientUtf8 = new UTF8Encoding(false, false);
    public const string NonVisionImageNote = "[Current model does not support images. The image will be omitted from this request.]";
    private readonly IFileOperations _operations;
    private readonly PathResolver _paths;
    private readonly FileMutationQueue _mutations;
    private readonly ReadWriteToolOptions _options;
    public ImmutableArray<IPreparedToolAdapter> Adapters { get; }
    public ImmutableArray<JsonData> Declarations { get; }
    public JsonData ToolsAdded { get; }
    public FileMutationQueueSnapshot MutationSnapshot => _mutations.Snapshot;

    public ReadWriteTools(string workingDirectory, string homeDirectory, IFileOperations? operations = null,
        ReadWriteToolOptions? options = null, FileMutationQueue? mutationQueue = null)
    {
        _options = options ?? new();
        if (_options.MaximumReadBytes is < 1 or > 64 * 1024 * 1024 || _options.MaximumWriteBytes is < 1 or > 64 * 1024 * 1024 ||
            _options.MaximumArgumentCharacters is < 1 or > 8 * 1024 * 1024 || _options.MaximumPathCharacters is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(options));
        _operations = operations ?? new LocalFileOperations();
        _paths = new(workingDirectory, homeDirectory, _operations, _options.MaximumPathCharacters);
        _mutations = mutationQueue ?? new FileMutationQueue(async (path, token) =>
        {
            var key = await _operations.CanonicalizeAsync(path, token).ConfigureAwait(false);
            // Existing segments have already been resolved. Windows folding conservatively groups case aliases;
            // case-sensitive directories may be over-serialized, and no universal file identity is claimed.
            return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
        }, new(MaximumKeyCharacters: _options.MaximumPathCharacters));
        Adapters = [new Adapter(this, "read"), new Adapter(this, "write")];
        Declarations = [ReadDeclaration, WriteDeclaration];
        ToolsAdded = JsonData.Parse("[" + string.Join(',', Declarations.Select(value => value.ToString())) + "]");
    }

    /// <summary>Source createReadToolDefinition name, description, TypeBox parameters and constrainedSampling.</summary>
    public static JsonData ReadDeclaration { get; } = JsonData.Parse("""{"name":"read","description":"Read the contents of a file. Supports text files and images (jpg, png, gif, webp, bmp). Images are sent as attachments. For text files, output is truncated to 2000 lines or 50KB (whichever is hit first). Use offset/limit for large files. When you need the full file, continue with offset until complete.","parameters":{"type":"object","required":["path"],"properties":{"path":{"type":"string","description":"Path to the file to read (relative or absolute)"},"offset":{"type":"number","description":"Line number to start reading from (1-indexed)"},"limit":{"type":"number","description":"Maximum number of lines to read"}}},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}""");
    /// <summary>Source createWriteToolDefinition name, description, TypeBox parameters and constrainedSampling.</summary>
    public static JsonData WriteDeclaration { get; } = JsonData.Parse("""{"name":"write","description":"Write content to a file. Creates the file if it doesn't exist, overwrites if it does. Automatically creates parent directories.","parameters":{"type":"object","required":["path","content"],"properties":{"path":{"type":"string","description":"Path to the file to write (relative or absolute)"},"content":{"type":"string","description":"Content to write to the file"}}},"constrainedSampling":{"type":"json_schema","strict":"prefer"}}""");

    /// <summary>
    /// Source readOutputSchema: the result for programmatic callers (codemode), the text for text files or an image block
    /// with its note. It is tool metadata, not part of the model-facing declaration. Authored rendering of the TypeBox
    /// schema; property descriptions are left out as in the source.
    /// </summary>
    public static JsonData ReadOutputSchema { get; } = JsonData.Parse("""
        {"anyOf":[{"type":"string"},{"type":"object","properties":{"type":{"const":"image","type":"string"},"data":{"type":"string"},"mimeType":{"type":"string"},"note":{"type":"string"}},"required":["type","data","mimeType","note"]}]}
        """);

    private static readonly JsonSerializerOptions OutputJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Source toReadOutput: the first image block with the first text as its note, otherwise the first text.</summary>
    public static JsonData ToReadOutput(JsonData content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Value.ValueKind != JsonValueKind.Array) throw new ArgumentException("Read content must be an array.", nameof(content));
        var blocks = content.Value.EnumerateArray().Where(block => block.ValueKind == JsonValueKind.Object).ToArray();
        var text = blocks.Where(block => Type(block) == "text").Select(block =>
            block.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null).FirstOrDefault() ?? "";
        if (blocks.FirstOrDefault(block => Type(block) == "image") is not { ValueKind: JsonValueKind.Object } image)
            return JsonData.Parse(JsonSerializer.Serialize(text, OutputJson));
        return JsonData.Parse(JsonSerializer.Serialize(new
        {
            type = "image", data = image.GetProperty("data").GetString(), mimeType = image.GetProperty("mimeType").GetString(), note = text
        }, OutputJson));
        static string? Type(JsonElement block) =>
            block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
    }

    public ToolInvoker CreateInvoker(IToolActionPolicy policy, IEnumerable<ToolActionTransform>? transforms = null,
        IEnumerable<ToolResultTransform>? resultTransforms = null) => new(Adapters, policy, transforms, resultTransforms,
            new(MaximumArgumentCharacters: _options.MaximumArgumentCharacters,
                MaximumActionCharacters: checked(_options.MaximumArgumentCharacters + 2 * _options.MaximumPathCharacters + 128),
                MaximumResultCharacters: ResultCharacters(_options)) { MaximumStructuredContentCharacters = ResultCharacters(_options) + 65_536 });

    /// <summary>Text results are truncated to 50KB; an image result carries base64 data of at most the resize limit, or of the
    /// whole admitted file when automatic resizing is off.</summary>
    public static int ResultCharacters(ReadWriteToolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var image = options.AutoResizeImages ? (long)(options.ImageResizeOptions ?? new()).MaxBytes : (options.MaximumReadBytes + 2L) / 3 * 4;
        return (int)Math.Min(12 * 1024 * 1024, Math.Max(512 * 1024, image + 65_536));
    }

    public ImmutableArray<ToolDefinition> CreateDefinitions(ToolInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        return [new("read", invoker), new("write", invoker)];
    }

    private sealed class Adapter(ReadWriteTools owner, string name) : IToolArgumentSchemaAdapter
    {
        public string Name => name;
        /// <summary>Source read/write TypeBox parameters, checked by validateToolArguments before the tool runs.</summary>
        public ToolArgumentSchema? ArgumentSchema { get; } = ToolArgumentSchema.FromDeclaration(name == "read" ? ReadDeclaration : WriteDeclaration, ToolSchemaOrigin.TypeBox);
        public async ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var input = owner.Parse(invocation.Call.Arguments, name, normalized: false);
            if (input.Path.Contains('\0'))
            {
                // Source read access / write mkdir(dirname) and writeFile: Node rejects the first path with a NUL byte.
                var absolute = owner._paths.ResolveWithNul(input.Path);
                if (name == "write" && Path.GetDirectoryName(absolute) is { } directory && directory.Contains('\0')) absolute = directory;
                throw new ToolSourceErrorException(NodeArgumentErrors.NullBytePath(absolute));
            }
            var lexical = name == "read" ? await owner._paths.ResolveReadAsync(input.Path, token).ConfigureAwait(false) : owner._paths.Resolve(input.Path);
            var target = owner._paths.Absolute(await owner._operations.CanonicalizeAsync(lexical, token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            var arguments = new Dictionary<string, object?> { ["path"] = target, ["displayPath"] = input.Path };
            if (name == "read")
            {
                if (input.Offset is { } offset) arguments["offset"] = offset;
                if (input.Limit is { } limit) arguments["limit"] = limit;
            }
            else arguments["content"] = input.Content;
            return new(name, name, PreparedToolActionKind.Path, target, JsonData.Parse(JsonSerializer.Serialize(arguments)),
                [], owner._paths.WorkingDirectory, ImmutableDictionary<string, string>.Empty);
        }

        public ValueTask<bool> ValidateAsync(PreparedToolAction action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var input = owner.Parse(action.Arguments, name, normalized: true);
                return ValueTask.FromResult(action.ToolName == name && action.Operation == name && action.Kind == PreparedToolActionKind.Path &&
                    !action.CommandArguments.IsDefault && action.CommandArguments.IsEmpty && action.Environment is { Count: 0 } &&
                    action.WorkingDirectory == owner._paths.WorkingDirectory && action.Target == input.Path &&
                    action.Target == owner._paths.Absolute(action.Target));
            }
            catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException or FileToolException)
            { return ValueTask.FromResult(false); }
        }

        public async ValueTask<ToolResult> ExecuteAsync(PreparedToolAction action, CancellationToken token)
        {
            if (!await ValidateAsync(action, token).ConfigureAwait(false))
                return ToolResult.Error(ToolFailureKind.InvalidArguments, "Invalid final file action.");
            var input = owner.Parse(action.Arguments, name, normalized: true);
            return name == "read" ? await owner.ReadAsync(action.Target, input, token).ConfigureAwait(false)
                : await owner.WriteAsync(action.Target, input, token).ConfigureAwait(false);
        }
    }

    private sealed record Input(string Path, string DisplayPath, double? Offset, double? Limit, string? Content);
    private Input Parse(JsonData arguments, string name, bool normalized)
    {
        if (arguments is null || arguments.ToString().Length > _options.MaximumArgumentCharacters || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid file arguments.");
        var value = arguments.Value;
        var allowed = name == "read" ? new[] { "path", "offset", "limit" } : new[] { "path", "content" };
        foreach (var property in value.EnumerateObject())
            // Source read/write schemas admit additional properties; execute reads only the declared ones.
            if (normalized && !allowed.Contains(property.Name, StringComparer.Ordinal) && property.Name != "displayPath")
                throw new ArgumentException("Unsupported file argument.");
        // Source resolveToCwd("") is the working directory; the file system reports what it does with it.
        var path = String(value, "path", empty: !normalized, allowNulData: !normalized);
        if (path.Length > _options.MaximumPathCharacters) throw new ArgumentException("Oversized file path.");
        var display = normalized ? String(value, "displayPath", empty: true) : path;
        if (display.Length > _options.MaximumPathCharacters) throw new ArgumentException("Oversized display path.");
        double? offset = null, limit = null; string? content = null;
        if (name == "read")
        {
            // Source offset/limit are TypeBox numbers used with JavaScript arithmetic in execute (any finite value).
            // Pi validation.ts normalizeOptionalNulls: an optional property sent as null (strict tool schemas make optional properties nullable) is absent.
            offset = Number(value, "offset"); limit = Number(value, "limit");
        }
        else
        {
            // write.ts writeFile(path, content, "utf-8"): Node encodes a lone surrogate of the content as U+FFFD.
            content = String(value, "content", empty: true, allowNulData: true, toWellFormed: true);
            if (Utf8.GetByteCount(content) > _options.MaximumWriteBytes) throw new FileToolException(FileToolFailure.ResourceLimit);
        }
        return new(path, display, offset, limit, content);
    }

    private static double? Number(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new ArgumentException("Read " + name + " must be a finite number.");
        return number;
    }

    /// <summary>JavaScript Number#toString, as the source's template literals print numbers.</summary>
    private static string Js(double value) => ToolArgumentValidation.Stringify(System.Text.Json.Nodes.JsonValue.Create(value))!;

    /// <summary>JavaScript Array#slice(start, end) bounds: truncated toward zero, negative values count from the end.</summary>
    private static (int From, int Count) Slice(int length, double start, double end)
    {
        int Bound(double value)
        {
            var truncated = double.IsNaN(value) ? 0 : Math.Truncate(value);
            return (int)(truncated < 0 ? Math.Max(length + truncated, 0) : Math.Min(truncated, length));
        }
        var from = Bound(start);
        return (from, Math.Max(Bound(end) - from, 0));
    }

    private static string String(JsonElement value, string name, bool empty, bool allowNulData = false, bool toWellFormed = false)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Required string file argument is absent.");
        var text = toWellFormed ? JsonUtf16.ToWellFormed(JsonUtf16.GetString(item)) : item.GetString()!;
        if ((!empty && text.Length == 0) || (!allowNulData && text.Contains('\0'))) throw new ArgumentException("Unsupported file argument text.");
        _ = Utf8.GetByteCount(text); // Strict encoder validates UTF-16 without replacing malformed input.
        return text;
    }

    private async ValueTask<ToolResult> ReadAsync(string target, Input input, CancellationToken token)
    {
        byte[] owned;
        try
        {
            var supplied = await _operations.ReadAsync(target, _options.MaximumReadBytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (supplied.Length > _options.MaximumReadBytes) throw new FileToolException(FileToolFailure.ResourceLimit);
            owned = supplied.ToArray();
        }
        catch (FileToolException error) { return FileError(ToolFailureKind.ExecutionError, error.Message, error.Failure.ToString()); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return ToolResult.Error(ToolFailureKind.ExecutionError, $"ENOENT: no such file or directory, access '{target}'"); }
        catch (UnauthorizedAccessException)
        {
            // Windows reports opening a directory as denied access; Node's readFile reports EISDIR.
            var directory = false;
            if (_operations is IDirectoryFileOperations directories)
                try { directory = await directories.IsDirectoryAsync(target, token).ConfigureAwait(false); }
                catch (Exception probe) when (probe is IOException or UnauthorizedAccessException) { }
            return ToolResult.Error(ToolFailureKind.ExecutionError, directory
                ? "EISDIR: illegal operation on a directory, read" : $"EACCES: permission denied, access '{target}'");
        }
        catch (IOException) { return ToolResult.Error(ToolFailureKind.ExecutionError, $"EIO: i/o error, read '{target}'"); }
        // Source detectSupportedImageMimeTypeFromFile sniffs the leading bytes; everything else is text.
        var mimeType = ImageMime.DetectSupportedImageMimeType(owned.AsSpan(0, Math.Min(owned.Length, ImageMime.SniffBytes)));
        if (mimeType is not null) return await ReadImageAsync(owned, mimeType, token).ConfigureAwait(false);
        var lines = LenientUtf8.GetString(owned).Split('\n');
        // Source: a falsy offset starts at line 1; otherwise Math.max(0, offset - 1). All arithmetic is JavaScript number arithmetic.
        var start = input.Offset is { } offset && offset != 0 ? Math.Max(0, offset - 1) : 0;
        var startDisplay = start + 1;
        if (start >= lines.Length)
            return ToolResult.Error(ToolFailureKind.InvalidArguments, $"Offset {Js(input.Offset!.Value)} is beyond end of file ({lines.Length} lines total)");
        double? userLimitedLines = null;
        (int From, int Count) range;
        if (input.Limit is { } limit)
        {
            var endLine = Math.Min(start + limit, lines.Length);
            range = Slice(lines.Length, start, endLine);
            userLimitedLines = endLine - start;
        }
        else range = Slice(lines.Length, start, lines.Length);
        var selected = string.Join("\n", lines, range.From, range.Count);
        var truncation = ToolOutputTruncator.Head(selected);
        string output; var details = JsonData.Null;
        if (truncation.FirstLineExceedsLimit)
        {
            // Source allLines[startLine]: a fractional start indexes no line, and Buffer.byteLength(undefined) throws.
            if (start != Math.Floor(start))
                return ToolResult.Error(ToolFailureKind.ExecutionError,
                    "The \"string\" argument must be of type string or an instance of Buffer or ArrayBuffer. Received undefined");
            output = $"[Line {Js(startDisplay)} is {FormatSize(Utf8.GetByteCount(lines[(int)start]))}, exceeds 50.0KB limit. Use bash: sed -n '{Js(startDisplay)}p' {input.DisplayPath} | head -c 51200]";
            details = TruncationDetails(truncation);
        }
        else if (truncation.Truncated)
        {
            var end = startDisplay + truncation.OutputLines - 1;
            var suffix = truncation.TruncatedBy == ToolOutputTruncationLimit.Bytes ? " (50.0KB limit)" : "";
            output = truncation.Content + $"\n\n[Showing lines {Js(startDisplay)}-{Js(end)} of {lines.Length}{suffix}. Use offset={Js(end + 1)} to continue.]";
            details = TruncationDetails(truncation);
        }
        else if (userLimitedLines is { } limited && start + limited < lines.Length)
            output = truncation.Content + $"\n\n[{Js(lines.Length - (start + limited))} more lines in file. Use offset={Js(start + limited + 1)} to continue.]";
        else output = truncation.Content;
        ToolResult result = new([new TextContent(output)], details);
        result = result with { StructuredContent = ToReadOutput(result.ContentValue) };
        // Source read: details is undefined (absent from the result) unless the output was truncated.
        return details.Value.ValueKind == JsonValueKind.Null ? result.WithProperty("details", null) : result;
    }

    /// <summary>Source read image branch: processImage, the "Read image file" note with hints, and the non-vision note.</summary>
    private async ValueTask<ToolResult> ReadImageAsync(byte[] bytes, string mimeType, CancellationToken token)
    {
        var processed = await Task.Run(() => ImageProcessor.Process(bytes, mimeType, _options.AutoResizeImages,
            _options.ImageResizeOptions, _options.ImageCodec), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var nonVision = _options.CurrentModelSupportsImages?.Invoke() == false ? NonVisionImageNote : null;
        string content;
        if (!processed.Ok)
        {
            var note = $"Read image file [{mimeType}]\n{processed.Message}" + (nonVision is null ? "" : "\n" + nonVision);
            content = JsonSerializer.Serialize(new object[] { new { type = "text", text = note } }, OutputJson);
        }
        else
        {
            var note = $"Read image file [{processed.MimeType}]" + (processed.Hints.IsEmpty ? "" : "\n" + string.Join("\n", processed.Hints)) +
                (nonVision is null ? "" : "\n" + nonVision);
            content = JsonSerializer.Serialize(new object[]
            {
                new { type = "text", text = note }, new { type = "image", data = processed.Data, mimeType = processed.MimeType }
            }, OutputJson);
        }
        var value = JsonData.Parse(content);
        return (new ToolResult([], JsonData.Null) { ContentValue = value, StructuredContent = ToReadOutput(value) }).WithProperty("details", null);
    }

    private async ValueTask<ToolResult> WriteAsync(string target, Input input, CancellationToken token)
    {
        var bytes = Utf8.GetBytes(input.Content!);
        return await _mutations.RunAsync<ToolResult>(target, async operationToken =>
        {
            var directoryAttempted = false; var directoryCompleted = false; var writeAttempted = false; var writeCompleted = false;
            try
            {
                operationToken.ThrowIfCancellationRequested();
                directoryAttempted = true;
                await _operations.CreateDirectoryAsync(Path.GetDirectoryName(target) ?? Path.GetPathRoot(target)!, operationToken).ConfigureAwait(false);
                directoryCompleted = true;
                operationToken.ThrowIfCancellationRequested();
                writeAttempted = true;
                await _operations.WriteAsync(target, bytes, operationToken).ConfigureAwait(false);
                writeCompleted = true;
                // Source throwIfAborted after the write settles: "Operation aborted". The details record the effect.
                if (operationToken.IsCancellationRequested)
                    return FileError(ToolFailureKind.Canceled, "Operation aborted", "WriteCompletedAfterCancellation",
                        directoryAttempted, directoryCompleted, writeAttempted, writeCompleted);
                // Source write: details: undefined.
                return new ToolResult([new TextContent($"Successfully wrote to {input.DisplayPath}")], JsonData.Null).WithProperty("details", null);
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            { return FileError(ToolFailureKind.Canceled, "Operation aborted", "WriteCanceled",
                directoryAttempted, directoryCompleted, writeAttempted, writeCompleted); }
            catch (Exception error)
            { return FileError(ToolFailureKind.ExecutionError, NodeWriteError(error, target, directoryCompleted), "WriteIoFailure",
                directoryAttempted, directoryCompleted, writeAttempted, writeCompleted); }
        }, token).ConfigureAwait(false);
    }

    /// <summary>The Node fs error text the source propagates from mkdir (recursive) or writeFile.</summary>
    private static string NodeWriteError(Exception error, string target, bool directoryCompleted)
    {
        var directory = Path.GetDirectoryName(target) ?? target;
        if (!directoryCompleted)
            return error is UnauthorizedAccessException ? $"EACCES: permission denied, mkdir '{directory}'" : $"ENOTDIR: not a directory, mkdir '{directory}'";
        if (Directory.Exists(target)) return $"EISDIR: illegal operation on a directory, open '{target}'";
        if (error is UnauthorizedAccessException) return $"EACCES: permission denied, open '{target}'";
        // Host exception text is not propagated; a Node-style code names the failed operation.
        return $"EIO: i/o error, write '{target}'";
    }

    private static ToolResult FileError(ToolFailureKind kind, string message, string code, bool directoryAttempted = false,
        bool directoryCompleted = false, bool writeAttempted = false, bool writeCompleted = false) =>
        new([new TextContent(message)], JsonData.Parse(JsonSerializer.Serialize(new { fileOperation = new
            { code, directoryAttempted, directoryCompleted, writeAttempted, writeCompleted } })), true, Failure: new(kind, message));

    private static JsonData TruncationDetails(ToolOutputTruncationResult value) => JsonData.Parse(JsonSerializer.Serialize(new
    {
        truncation = new { content = value.Content, truncated = value.Truncated,
            truncatedBy = value.TruncatedBy == ToolOutputTruncationLimit.Lines ? "lines" : "bytes",
            totalLines = value.TotalLines, totalBytes = value.TotalBytes, outputLines = value.OutputLines, outputBytes = value.OutputBytes,
            lastLinePartial = value.LastLinePartial, firstLineExceedsLimit = value.FirstLineExceedsLimit,
            maxLines = value.MaxLines, maxBytes = value.MaxBytes }
    }));

    private static string FormatSize(int bytes) => bytes < 1024 ? bytes.ToString(CultureInfo.InvariantCulture) + "B"
        : bytes < 1024 * 1024 ? (bytes / 1024d).ToString("F1", CultureInfo.InvariantCulture) + "KB"
        : (bytes / (1024d * 1024)).ToString("F1", CultureInfo.InvariantCulture) + "MB";
}
