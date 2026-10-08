// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/tools/read.ts (outputSchema, structuredContent).
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Agent.Tools;
using PiSharp.Contracts;

namespace PiSharp.Tools.Files;

/// <summary>Bounded UTF-8 read/write adapters, owned declarations and mandatory-policy invoker composition.</summary>
public sealed class ReadWriteTools
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
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
        Declarations =
        [
            JsonData.Parse("""{"name":"read","description":"Read UTF-8 text file contents, capped at 2000 lines or 50 KiB. Use offset/limit to continue. Images, binary and other encodings are unsupported in this profile.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Path to the file to read (relative or absolute)"},"offset":{"type":"integer","minimum":1,"description":"Line number to start reading from (1-indexed)"},"limit":{"type":"integer","minimum":0,"description":"Maximum number of lines to read"}},"required":["path"],"additionalProperties":false}}"""),
            JsonData.Parse("""{"name":"write","description":"Write UTF-8 text content to a file, creating parent directories and overwriting existing contents. Bounded text profile; this is not atomic replacement.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Path to the file to write (relative or absolute)"},"content":{"type":"string","description":"Content to write to the file"}},"required":["path","content"],"additionalProperties":false}}""")
        ];
        ToolsAdded = JsonData.Parse("[" + string.Join(',', Declarations.Select(value => value.ToString())) + "]");
    }

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
                MaximumResultCharacters: 512 * 1024));

    public ImmutableArray<ToolDefinition> CreateDefinitions(ToolInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);
        return [new("read", invoker), new("write", invoker)];
    }

    private sealed class Adapter(ReadWriteTools owner, string name) : IPreparedToolAdapter
    {
        public string Name => name;
        public async ValueTask<PreparedToolAction> PrepareAsync(ToolInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var input = owner.Parse(invocation.Call.Arguments, name, normalized: false);
            var lexical = name == "read" ? await owner._paths.ResolveReadAsync(input.Path, token).ConfigureAwait(false) : owner._paths.Resolve(input.Path);
            var target = owner._paths.Absolute(await owner._operations.CanonicalizeAsync(lexical, token).ConfigureAwait(false));
            token.ThrowIfCancellationRequested();
            var arguments = new Dictionary<string, object?> { ["path"] = target, ["displayPath"] = input.Path };
            if (name == "read")
            {
                arguments["offset"] = input.Offset;
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

    private sealed record Input(string Path, string DisplayPath, int Offset, int? Limit, string? Content);
    private Input Parse(JsonData arguments, string name, bool normalized)
    {
        if (arguments is null || arguments.ToString().Length > _options.MaximumArgumentCharacters || arguments.Value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid file arguments.");
        var value = arguments.Value;
        var allowed = name == "read" ? new[] { "path", "offset", "limit" } : new[] { "path", "content" };
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) && !(normalized && property.Name == "displayPath"))
                throw new ArgumentException("Unsupported file argument.");
        var path = String(value, "path", empty: false);
        if (path.Length > _options.MaximumPathCharacters) throw new ArgumentException("Oversized file path.");
        var display = normalized ? String(value, "displayPath", empty: false) : path;
        if (display.Length > _options.MaximumPathCharacters) throw new ArgumentException("Oversized display path.");
        var offset = 1; int? limit = null; string? content = null;
        if (name == "read")
        {
            if (value.TryGetProperty("offset", out var number) && (!number.TryGetInt32(out offset) || offset < 1))
                throw new ArgumentException("Offset must be a positive 32-bit integer.");
            if (value.TryGetProperty("limit", out number))
            {
                if (!number.TryGetInt32(out var count) || count < 0) throw new ArgumentException("Limit must be a nonnegative 32-bit integer.");
                limit = count;
            }
        }
        else
        {
            content = String(value, "content", empty: true, allowNulData: true);
            if (Utf8.GetByteCount(content) > _options.MaximumWriteBytes) throw new FileToolException(FileToolFailure.ResourceLimit);
        }
        return new(path, display, offset, limit, content);
    }

    private static string String(JsonElement value, string name, bool empty, bool allowNulData = false)
    {
        if (!value.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Required string file argument is absent.");
        var text = item.GetString()!;
        if ((!empty && text.Length == 0) || (!allowNulData && text.Contains('\0'))) throw new ArgumentException("Unsupported file argument text.");
        _ = Utf8.GetByteCount(text); // Strict encoder validates UTF-16 without replacing malformed input.
        return text;
    }

    private async ValueTask<ToolResult> ReadAsync(string target, Input input, CancellationToken token)
    {
        try
        {
            var supplied = await _operations.ReadAsync(target, _options.MaximumReadBytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (supplied.Length > _options.MaximumReadBytes) throw new FileToolException(FileToolFailure.ResourceLimit);
            var owned = supplied.ToArray();
            if (UnsupportedBytes(owned)) throw new FileToolException(FileToolFailure.UnsupportedContent);
            string text;
            try { text = Utf8.GetString(owned); }
            catch (DecoderFallbackException) { throw new FileToolException(FileToolFailure.UnsupportedContent); }
            if (text.Any(character => (char.IsControl(character) && character is not ('\t' or '\n' or '\r'))))
                throw new FileToolException(FileToolFailure.UnsupportedContent);
            var lines = text.Split('\n');
            var start = input.Offset - 1;
            if (start >= lines.Length)
                return ToolResult.Error(ToolFailureKind.InvalidArguments, $"Offset {input.Offset} is beyond end of file ({lines.Length} lines total)");
            var count = input.Limit is { } requested ? Math.Min(requested, lines.Length - start) : lines.Length - start;
            var selected = string.Join("\n", lines, start, count);
            var truncation = ToolOutputTruncator.Head(selected);
            string output; var details = JsonData.Null;
            if (truncation.FirstLineExceedsLimit)
            {
                output = $"[Line {input.Offset} is {FormatSize(Utf8.GetByteCount(lines[start]))}, exceeds 50.0KB limit. Use bash: sed -n '{input.Offset}p' {input.DisplayPath} | head -c 51200]";
                details = TruncationDetails(truncation);
            }
            else if (truncation.Truncated)
            {
                var end = input.Offset + truncation.OutputLines - 1;
                var suffix = truncation.TruncatedBy == ToolOutputTruncationLimit.Bytes ? " (50.0KB limit)" : "";
                output = truncation.Content + $"\n\n[Showing lines {input.Offset}-{end} of {lines.Length}{suffix}. Use offset={end + 1} to continue.]";
                details = TruncationDetails(truncation);
            }
            else if (input.Limit is not null && start + count < lines.Length)
                output = truncation.Content + $"\n\n[{lines.Length - start - count} more lines in file. Use offset={start + count + 1} to continue.]";
            else output = truncation.Content;
            ToolResult result = new([new TextContent(output)], details);
            return result with { StructuredContent = ToReadOutput(result.ContentValue) };
        }
        catch (FileToolException error) { return FileError(ToolFailureKind.ExecutionError, error.Message, error.Failure.ToString()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return FileError(ToolFailureKind.ExecutionError, "Cannot read file contents.", "ReadIoFailure"); }
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
                if (operationToken.IsCancellationRequested)
                    return FileError(ToolFailureKind.Canceled, "Write completed after cancellation was requested.", "WriteCompletedAfterCancellation",
                        directoryAttempted, directoryCompleted, writeAttempted, writeCompleted);
                return new([new TextContent($"Successfully wrote to {input.DisplayPath}")], JsonData.Null);
            }
            catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
            { return FileError(ToolFailureKind.Canceled, "Write canceled; an attempted file operation may have changed the filesystem.", "WriteCanceled",
                directoryAttempted, directoryCompleted, writeAttempted, writeCompleted); }
            catch (Exception)
            { return FileError(ToolFailureKind.ExecutionError, "Cannot write file contents; an attempted file operation may have changed the filesystem.", "WriteIoFailure",
                directoryAttempted, directoryCompleted, writeAttempted, writeCompleted); }
        }, token).ConfigureAwait(false);
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

    private static bool UnsupportedBytes(ReadOnlySpan<byte> bytes) => bytes.Contains((byte)0) ||
        bytes.StartsWith(new byte[] { 0xff, 0xfe }) || bytes.StartsWith(new byte[] { 0xfe, 0xff }) ||
        bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }) ||
        bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }) || bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) ||
        (bytes.Length >= 12 && bytes.StartsWith("RIFF"u8) && bytes[8..].StartsWith("WEBP"u8)) ||
        IsBmp(bytes);

    private static bool IsBmp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 26 || !bytes.StartsWith("BM"u8)) return false;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]);
        var pixels = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        var header = BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        if ((size != 0 && size < 26) || pixels < 14L + header || (size != 0 && pixels >= size)) return false;
        var offset = header == 12 ? 22 : header is >= 40 and <= 124 && bytes.Length >= 30 ? 26 : -1;
        return offset >= 0 && BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]) == 1 &&
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[(offset + 2)..]) is 1 or 4 or 8 or 16 or 24 or 32;
    }
}
