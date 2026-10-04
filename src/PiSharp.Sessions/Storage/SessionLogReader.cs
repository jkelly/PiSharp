using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;

namespace PiSharp.Sessions.Storage;

public sealed record SessionLogReaderOptions(int MaximumInputBytes = 16_777_216,
    int MaximumLineBytes = 4_194_304, int MaximumLines = 100_000, int MaximumRecords = 100_000,
    int ReadBufferBytes = 8_192, SessionEntryCodecOptions? CodecOptions = null);

public enum SessionLogReadStatus { Complete, RecoveryRequired, UnsupportedVersion, ResourceLimit }
public enum SessionLogDiagnosticCode
{
    EmptyLog, MissingHeader, EmptyHeader, InvalidHeader, UnsupportedHeaderVersion, FutureHeaderVersion,
    UnexpectedHeader, InvalidRecord, InvalidUtf8, Utf8Bom, IncompleteFinalUtf8, IncompleteFinalJson,
    FinalLineWithoutNewline, InputByteLimit, LineByteLimit, LineCountLimit, RecordCountLimit, CodecLimit
}
public enum SessionLogReadFailure { ReadFailed, CleanupFailed }

public sealed class SessionLogReadException : Exception
{
    public SessionLogReadFailure Failure { get; }
    internal SessionLogReadException(SessionLogReadFailure failure) : base(failure == SessionLogReadFailure.ReadFailed
        ? "Session log could not be read." : "Session log stream cleanup failed.") => Failure = failure;
}

public sealed record SessionLogDiagnostic(SessionLogDiagnosticCode Code, int LineNumber,
    int ByteOffset, int ByteLength, SessionEntryCodecFailure? CodecFailure = null)
{
    public bool IsBlocking => Code != SessionLogDiagnosticCode.FinalLineWithoutNewline;
    public string Message => Code switch
    {
        SessionLogDiagnosticCode.EmptyLog => "Session log is empty.",
        SessionLogDiagnosticCode.MissingHeader => "Session log has no leading session header.",
        SessionLogDiagnosticCode.EmptyHeader => "Session header identity is empty.",
        SessionLogDiagnosticCode.InvalidHeader => "Session header is invalid.",
        SessionLogDiagnosticCode.UnsupportedHeaderVersion => "Session header version requires a separate importer.",
        SessionLogDiagnosticCode.FutureHeaderVersion => "Future session version is retained for inspection only.",
        SessionLogDiagnosticCode.UnexpectedHeader => "Session log contains another header.",
        SessionLogDiagnosticCode.InvalidRecord => "Session log contains an invalid record.",
        SessionLogDiagnosticCode.InvalidUtf8 => "Session log contains invalid UTF-8.",
        SessionLogDiagnosticCode.Utf8Bom => "Session log contains an unsupported byte order mark.",
        SessionLogDiagnosticCode.IncompleteFinalUtf8 => "Final session line has incomplete UTF-8.",
        SessionLogDiagnosticCode.IncompleteFinalJson => "Final session line has incomplete JSON.",
        SessionLogDiagnosticCode.FinalLineWithoutNewline => "Valid final session record has no line terminator.",
        SessionLogDiagnosticCode.InputByteLimit => "Session log exceeds the input byte limit.",
        SessionLogDiagnosticCode.LineByteLimit => "Session log line exceeds the byte limit.",
        SessionLogDiagnosticCode.LineCountLimit => "Session log exceeds the physical line limit.",
        SessionLogDiagnosticCode.RecordCountLimit => "Session log exceeds the record limit.",
        _ => "Session log record exceeds codec limits."
    };
}

/// <summary>Offsets are relative to the input stream's position when reading starts.</summary>
public sealed record SessionLogRecord(SessionEntry Entry, int LineNumber, int ByteOffset,
    int ContentByteLength, int PhysicalByteLength, bool HasLineTerminator);

public sealed class SessionLogReadResult
{
    public ImmutableArray<byte> OriginalBytes { get; }
    public ImmutableArray<SessionLogRecord> ValidatedPrefix { get; }
    public ImmutableArray<SessionLogDiagnostic> Diagnostics { get; }
    public int ValidatedPrefixByteLength { get; }
    public bool SourceComplete { get; }
    public SessionLogReadStatus Status { get; }
    public SessionEntry? Header => ValidatedPrefix.IsEmpty ? null : ValidatedPrefix[0].Entry;
    public JsonData? OpaqueHeader { get; }
    public long? DetectedVersion { get; }

    internal SessionLogReadResult(ImmutableArray<byte> bytes, ImmutableArray<SessionLogRecord> prefix,
        ImmutableArray<SessionLogDiagnostic> diagnostics, int prefixBytes, bool complete,
        SessionLogReadStatus status, JsonData? opaqueHeader, long? detectedVersion)
    {
        OriginalBytes = bytes; ValidatedPrefix = prefix; Diagnostics = diagnostics;
        ValidatedPrefixByteLength = prefixBytes; SourceComplete = complete; Status = status;
        OpaqueHeader = opaqueHeader; DetectedVersion = detectedVersion;
    }
}

/// <summary>Read-only bounded JSONL recovery inspection. It never skips damaged records or changes source bytes.</summary>
public sealed class SessionLogReader
{
    private readonly SessionLogReaderOptions _options;
    private readonly SessionEntryCodec _codec;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public SessionLogReader(SessionLogReaderOptions? options = null)
    {
        _options = options ?? new();
        if (_options.MaximumInputBytes is < 1 or int.MaxValue || _options.MaximumLineBytes <= 0 ||
            _options.MaximumLines <= 0 || _options.MaximumRecords <= 0 || _options.ReadBufferBytes is < 1 or > 65_536)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid session log reader limits.");
        _codec = new(_options.CodecOptions);
    }

    public async Task<SessionLogReadResult> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileStream source;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, _options.ReadBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException(cancellationToken); }
        catch (Exception) { throw new SessionLogReadException(SessionLogReadFailure.ReadFailed); }
        return await ReadAsync(source, leaveOpen: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionLogReadResult> ReadAsync(Stream source, bool leaveOpen = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        SessionLogReadResult? result = null;
        Exception? failure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.CanRead) throw new SessionLogReadException(SessionLogReadFailure.ReadFailed);
            using var observed = new MemoryStream();
            var buffer = new byte[_options.ReadBufferBytes];
            var complete = false;
            while (observed.Length <= _options.MaximumInputBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, _options.MaximumInputBytes + 1L - observed.Length);
                var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (read is < 0 || read > requested) throw new SessionLogReadException(SessionLogReadFailure.ReadFailed);
                if (read == 0) { complete = true; break; }
                observed.Write(buffer, 0, read);
            }
            result = Scan(ImmutableArray.CreateRange(observed.ToArray()), complete, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { failure = new OperationCanceledException(cancellationToken); }
        catch (Exception) { failure = new SessionLogReadException(SessionLogReadFailure.ReadFailed); }
        finally
        {
            if (!leaveOpen)
            {
                try { await source.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { failure ??= new SessionLogReadException(SessionLogReadFailure.CleanupFailed); }
            }
        }
        if (failure is not null) throw failure;
        cancellationToken.ThrowIfCancellationRequested();
        return result!;
    }

    private SessionLogReadResult Scan(ImmutableArray<byte> original, bool sourceComplete, CancellationToken token)
    {
        var limited = original.Length > _options.MaximumInputBytes;
        var bytes = original.AsSpan(0, Math.Min(original.Length, _options.MaximumInputBytes));
        var records = ImmutableArray.CreateBuilder<SessionLogRecord>();
        var diagnostics = ImmutableArray.CreateBuilder<SessionLogDiagnostic>();
        var lineStart = 0; var lineNumber = 0; var prefixBytes = 0;
        JsonData? opaqueHeader = null; long? version = null;
        while (lineStart < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var newline = bytes[lineStart..].IndexOf((byte)'\n');
            var terminated = newline >= 0;
            var physicalEnd = terminated ? lineStart + newline + 1 : bytes.Length;
            var contentEnd = terminated ? physicalEnd - 1 : physicalEnd;
            if (terminated && contentEnd > lineStart && bytes[contentEnd - 1] == '\r') contentEnd--;
            var content = bytes[lineStart..contentEnd];
            lineNumber++;
            if (lineNumber > _options.MaximumLines)
            { diagnostics.Add(new(SessionLogDiagnosticCode.LineCountLimit, lineNumber, lineStart, physicalEnd - lineStart)); break; }
            if (content.Length > _options.MaximumLineBytes)
            { diagnostics.Add(new(SessionLogDiagnosticCode.LineByteLimit, lineNumber, lineStart, physicalEnd - lineStart)); break; }
            // A bounded capture stopped before EOF. Its unterminated fragment is not a completed final record.
            if (!terminated && !sourceComplete) break;
            if (IsBlank(content))
            {
                if (records.Count != 0) prefixBytes = physicalEnd;
                lineStart = physicalEnd; continue;
            }
            if (records.Count >= _options.MaximumRecords)
            { diagnostics.Add(new(SessionLogDiagnosticCode.RecordCountLimit, lineNumber, lineStart, physicalEnd - lineStart)); break; }
            if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            { diagnostics.Add(new(SessionLogDiagnosticCode.Utf8Bom, lineNumber, lineStart, physicalEnd - lineStart)); break; }
            var utf8Failure = CheckUtf8(content);
            if (utf8Failure is not null)
            {
                var code = utf8Failure == OperationStatus.NeedMoreData && !terminated && sourceComplete
                    ? SessionLogDiagnosticCode.IncompleteFinalUtf8 : SessionLogDiagnosticCode.InvalidUtf8;
                diagnostics.Add(new(code, lineNumber, lineStart, physicalEnd - lineStart)); break;
            }
            SessionEntry entry;
            try { entry = _codec.ParseUtf8(content); }
            catch (SessionEntryCodecException error)
            {
                var code = records.Count == 0 ? SessionLogDiagnosticCode.InvalidHeader : SessionLogDiagnosticCode.InvalidRecord;
                if (error.Failure is SessionEntryCodecFailure.CharacterLimit or SessionEntryCodecFailure.Utf8ByteLimit or SessionEntryCodecFailure.DepthLimit)
                    code = SessionLogDiagnosticCode.CodecLimit;
                else if (error.Failure == SessionEntryCodecFailure.MalformedJson && !terminated && sourceComplete && IsIncompleteJson(content))
                    code = SessionLogDiagnosticCode.IncompleteFinalJson;
                else if (error.Failure == SessionEntryCodecFailure.UnsupportedVersion && records.Count == 0)
                {
                    // Codec has already checked strict JSON, duplicates, Unicode and configured bounds.
                    opaqueHeader = JsonData.Parse(StrictUtf8.GetString(content));
                    if (opaqueHeader.Value.TryGetProperty("version", out var declared) && declared.ValueKind == JsonValueKind.Number && declared.TryGetInt64(out var number))
                        version = number;
                    code = version > SessionEntryCodec.CurrentVersion ? SessionLogDiagnosticCode.FutureHeaderVersion
                        : SessionLogDiagnosticCode.UnsupportedHeaderVersion;
                }
                diagnostics.Add(new(code, lineNumber, lineStart, physicalEnd - lineStart, error.Failure)); break;
            }
            if (records.Count == 0 && !entry.IsHeader)
            { diagnostics.Add(new(SessionLogDiagnosticCode.MissingHeader, lineNumber, lineStart, physicalEnd - lineStart)); break; }
            if (records.Count == 0 && entry.Id.Length == 0)
            { diagnostics.Add(new(SessionLogDiagnosticCode.EmptyHeader, lineNumber, lineStart, physicalEnd - lineStart)); break; }
            if (records.Count != 0 && entry.IsHeader)
            { diagnostics.Add(new(SessionLogDiagnosticCode.UnexpectedHeader, lineNumber, lineStart, physicalEnd - lineStart)); break; }
            records.Add(new(entry, lineNumber, lineStart, content.Length, physicalEnd - lineStart, terminated));
            prefixBytes = physicalEnd;
            if (records.Count == 1) version = SessionEntryCodec.CurrentVersion;
            if (!terminated) diagnostics.Add(new(SessionLogDiagnosticCode.FinalLineWithoutNewline, lineNumber, lineStart, physicalEnd - lineStart));
            lineStart = physicalEnd;
        }
        if (records.Count == 0 && diagnostics.Count == 0 && sourceComplete)
            diagnostics.Add(new(original.IsEmpty ? SessionLogDiagnosticCode.EmptyLog : SessionLogDiagnosticCode.MissingHeader, 1, 0, original.Length));
        if (limited) diagnostics.Add(new(SessionLogDiagnosticCode.InputByteLimit, 0, _options.MaximumInputBytes, 1));
        var status = limited || diagnostics.Any(item => item.Code is SessionLogDiagnosticCode.LineByteLimit or
            SessionLogDiagnosticCode.LineCountLimit or SessionLogDiagnosticCode.RecordCountLimit or SessionLogDiagnosticCode.CodecLimit)
            ? SessionLogReadStatus.ResourceLimit
            : diagnostics.Any(item => item.Code is SessionLogDiagnosticCode.FutureHeaderVersion or SessionLogDiagnosticCode.UnsupportedHeaderVersion)
                ? SessionLogReadStatus.UnsupportedVersion
                : diagnostics.Any(item => item.IsBlocking) ? SessionLogReadStatus.RecoveryRequired : SessionLogReadStatus.Complete;
        return new(original, records.ToImmutable(), diagnostics.ToImmutable(), prefixBytes, sourceComplete, status, opaqueHeader, version);
    }

    private static bool IsBlank(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r')) return false;
        return true;
    }
    private static OperationStatus? CheckUtf8(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var status = Rune.DecodeFromUtf8(bytes[offset..], out _, out var consumed);
            if (status != OperationStatus.Done) return status;
            offset += consumed;
        }
        return null;
    }
    private static bool IsIncompleteJson(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock: false,
                new JsonReaderState(new JsonReaderOptions { MaxDepth = 64 }));
            var containers = 0;
            while (reader.Read())
            {
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) containers++;
                else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) containers--;
            }
            return containers != 0 || reader.BytesConsumed < bytes.Length;
        }
        catch (JsonException) { return false; }
    }
}
