using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

namespace PiSharp.Sessions.Import;

public enum SessionCopyFormat { NativeExact, CompatibleCurrentJsonl, NativeArchiveExact }
public enum SessionCopyStatus { Blocked, Published, PublishedWithCleanupFailure }
public enum SessionCopyPublication { NotAttempted, Published, Uncertain }
public enum SessionCopyFailure { InvalidRequest, ReadFailed, CreateFailed, WriteFailed, FlushFailed, PublishFailed, CleanupFailed }
public enum SessionCopyDiagnosticCode
{
    SourceIncomplete, SourceInvalid, ResourceLimit, MigrationBlocked, InvalidGraph, UnsupportedEntryOrder,
    ExactCopyRequiresCurrentVersion, AllJsonFieldsRetained, SemanticCompatibilityUnverified, CleanupFailed,
    ArchivedSourceExact, ArchiveNotRunnable, UnknownEntriesRetainedInert,
    ArchiveIdentityInventoryIncomplete, ArchiveIdentityInventoryUnavailable
}
public sealed record SessionCopyDiagnostic(SessionCopyDiagnosticCode Code, int? RecordIndex = null,
    SessionContextProjectionFailure? GraphFailure = null);
public sealed record SessionCopyOptions(SessionLogReaderOptions? ReaderOptions = null,
    int MaximumOutputBytes = 16_777_216);
public sealed record SessionCopyRequest(string SourcePath, string DestinationPath,
    SessionCopyFormat Format = SessionCopyFormat.NativeExact, ImmutableArray<string> V1EntryIds = default);

public sealed record SessionCopyInspection(SessionLogReadResult Log, string CapturedSha256,
    SessionEntryMigrationResult? Migration, ImmutableArray<SessionCopyDiagnostic> Diagnostics, bool CanPublishCurrent)
{
    public ImmutableArray<byte> OriginalBytes => Log.OriginalBytes;
    public string? SourceSha256 => Log.SourceComplete ? CapturedSha256 : null;
    public long? SourceVersion => Migration?.SourceVersion ?? Log.DetectedVersion;
    public ImmutableArray<SessionEntry> CurrentRecords => Migration?.Records ?? [];
}
public sealed record SessionExportRetentionReport(bool ExactSourceBytes, int ExcludedRecords,
    int ExcludedFields, ImmutableArray<string> InertUnknownEntryIds, bool RunnableCurrentFormat,
    bool PiReaderInteroperabilityGuaranteed = false);
public sealed record SessionCopyResult(SessionCopyStatus Status, SessionCopyFormat Format,
    SessionCopyInspection Inspection, string DestinationPath, string? OutputSha256, int OutputBytes,
    ImmutableArray<SessionCopyDiagnostic> Diagnostics, int OmittedRecords = 0, int OmittedFields = 0)
{
    public bool Published => Status is SessionCopyStatus.Published or SessionCopyStatus.PublishedWithCleanupFailure;
    public SessionLogStorageDurability StorageDurability { get; init; } = SessionLogStorageDurability.LocalFileFlush;
    public SessionExportRetentionReport? Retention { get; init; }
}
public sealed class SessionCopyException : Exception
{
    public SessionCopyFailure Failure { get; }
    public SessionCopyPublication Publication { get; }
    public bool TemporaryMayRemain { get; }
    public ImmutableArray<SessionCopyFailure> CleanupFailures { get; }
    internal SessionCopyException(SessionCopyFailure failure, SessionCopyPublication publication,
        bool temporaryMayRemain, ImmutableArray<SessionCopyFailure> cleanup) : base("Session copy operation failed.")
    { Failure = failure; Publication = publication; TemporaryMayRemain = temporaryMayRemain; CleanupFailures = cleanup; }
}
public sealed class SessionCopyCanceledException : OperationCanceledException
{
    public bool TemporaryMayRemain { get; }
    public ImmutableArray<SessionCopyFailure> CleanupFailures { get; }
    internal SessionCopyCanceledException(CancellationToken token, bool temporaryMayRemain,
        ImmutableArray<SessionCopyFailure> cleanup) : base("Session copy canceled after owned cleanup.", token)
    { TemporaryMayRemain = temporaryMayRemain; CleanupFailures = cleanup; }
}

/// <summary>Trusted I/O seam. Creation must settle and clean acquired resources if it throws before returning ownership.</summary>
public interface ISessionCopyFileSystem
{
    bool FileExists(string path) => File.Exists(path);
    bool DirectoryExists(string path) => Directory.Exists(path);
    SessionLogStorageDurability GetDurability(string path) => SessionLogStorageDurability.LocalFileFlush;
    ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken);
    ValueTask<Stream> CreateNewTemporaryAsync(string path);
    ValueTask PublishNewAsync(string temporaryPath, string destinationPath);
    ValueTask DeleteTemporaryAsync(string path);
}

/// <summary>Explicit non-destructive copy/migration. It owns no default home, ID generator, extension or provider authority.</summary>
public sealed class SessionCopyService
{
    private readonly SessionLogReaderOptions _bounds;
    private readonly int _outputLimit;
    private readonly ISessionCopyFileSystem _files;
    private readonly SessionEntryCodec _codec;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static ISessionCopyFileSystem LocalFileSystem { get; } = new LocalFiles();

    public SessionCopyService(SessionCopyOptions? options = null, ISessionCopyFileSystem? fileSystem = null)
    {
        var configured = options ?? new(); _bounds = configured.ReaderOptions ?? new();
        _ = new SessionLogReader(_bounds);
        if (_bounds.MaximumInputBytes > 67_108_864 || configured.MaximumOutputBytes is < 1 or > 67_108_864)
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid session copy bounds.");
        _outputLimit = Math.Min(configured.MaximumOutputBytes, _bounds.MaximumInputBytes);
        _files = fileSystem ?? LocalFileSystem; _codec = new(_bounds.CodecOptions);
    }

    public async Task<SessionCopyInspection> InspectAsync(string sourcePath, ImmutableArray<string> v1EntryIds = default,
        CancellationToken cancellationToken = default)
    {
        ValidatePath(sourcePath); cancellationToken.ThrowIfCancellationRequested();
        Stream? source = null; SessionCopyInspection? result = null; Exception? failure = null;
        var cleanup = ImmutableArray.CreateBuilder<SessionCopyFailure>();
        try
        {
            source = await _files.OpenReadAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            result = await InspectOwnedAsync(source, v1EntryIds, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { failure = error; }
        if (source is not null)
            try { await source.DisposeAsync().ConfigureAwait(false); } catch (Exception) { cleanup.Add(SessionCopyFailure.CleanupFailed); }
        if (failure is not null || cleanup.Count != 0)
            Throw(failure, failure is null ? SessionCopyFailure.CleanupFailed : SessionCopyFailure.ReadFailed,
                SessionCopyPublication.NotAttempted, false, cleanup.ToImmutable(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return result!;
    }

    public async Task<SessionCopyResult> CopyAsync(SessionCopyRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null || !Enum.IsDefined(request.Format)) throw Invalid();
        ValidatePath(request.SourcePath); ValidatePath(request.DestinationPath);
        if (SamePath(request.SourcePath, request.DestinationPath) || _files.FileExists(request.DestinationPath) ||
            _files.DirectoryExists(request.DestinationPath) || !_files.DirectoryExists(Path.GetDirectoryName(request.DestinationPath)!)) throw Invalid();
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = Path.Combine(Path.GetDirectoryName(request.DestinationPath)!, ".pisharp-copy-" + Guid.NewGuid().ToString("N") + ".tmp");
        Stream? source = null; Stream? temporary = null;
        var temporaryOwned = false; var temporaryMayRemain = false;
        var publication = SessionCopyPublication.NotAttempted; var stage = SessionCopyFailure.ReadFailed;
        SessionCopyResult? result = null; Exception? failure = null;
        var cleanup = ImmutableArray.CreateBuilder<SessionCopyFailure>();
        try
        {
            source = await _files.OpenReadAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);
            var inspection = await InspectOwnedAsync(source, request.V1EntryIds, cancellationToken).ConfigureAwait(false);
            var diagnostics = inspection.Diagnostics.ToBuilder();
            var archive = request.Format == SessionCopyFormat.NativeArchiveExact;
            // An archive is an opaque byte-preservation operation, never current-session admission.
            var canArchive = inspection.Log.SourceComplete && inspection.OriginalBytes.Length <= _outputLimit;
            if (archive ? !canArchive : !inspection.CanPublishCurrent || request.Format == SessionCopyFormat.NativeExact && inspection.SourceVersion != 3)
            {
                if (archive && inspection.Log.SourceComplete) diagnostics.Add(new(SessionCopyDiagnosticCode.ResourceLimit));
                else if (!archive && inspection.CanPublishCurrent) diagnostics.Add(new(SessionCopyDiagnosticCode.ExactCopyRequiresCurrentVersion));
                result = new(SessionCopyStatus.Blocked, request.Format, inspection, request.DestinationPath, null, 0, diagnostics.ToImmutable());
            }
            else
            {
                var bytes = request.Format is SessionCopyFormat.NativeExact or SessionCopyFormat.NativeArchiveExact ? inspection.OriginalBytes.ToArray() :
                    Encode(inspection.CurrentRecords, cancellationToken);
                if (bytes is null || bytes.Length > _outputLimit)
                {
                    diagnostics.Add(new(SessionCopyDiagnosticCode.ResourceLimit));
                    result = new(SessionCopyStatus.Blocked, request.Format, inspection, request.DestinationPath, null, 0, diagnostics.ToImmutable());
                }
                else
                {
                    var outputLog = await new SessionLogReader(_bounds).ReadAsync(new MemoryStream(bytes, writable: false),
                        leaveOpen: false, cancellationToken).ConfigureAwait(false);
                    if (!outputLog.SourceComplete || !archive && outputLog.Status != SessionLogReadStatus.Complete)
                    {
                        diagnostics.Add(new(SessionCopyDiagnosticCode.ResourceLimit));
                        result = new(SessionCopyStatus.Blocked, request.Format, inspection, request.DestinationPath, null, 0, diagnostics.ToImmutable());
                    }
                    else
                    {
                        if (request.Format == SessionCopyFormat.CompatibleCurrentJsonl)
                            diagnostics.Add(new(SessionCopyDiagnosticCode.SemanticCompatibilityUnverified));
                        if (archive)
                        {
                            diagnostics.Add(new(SessionCopyDiagnosticCode.ArchivedSourceExact));
                            diagnostics.Add(new(SessionCopyDiagnosticCode.ArchiveNotRunnable));
                        }
                        var (unknown, unknownPresent, identityDisclosure) = ExportUnknownIdentities(inspection, archive, cancellationToken);
                        if (unknownPresent) diagnostics.Add(new(SessionCopyDiagnosticCode.UnknownEntriesRetainedInert));
                        if (identityDisclosure is { } disclosure) diagnostics.Add(new(disclosure));
                        cancellationToken.ThrowIfCancellationRequested();
                        stage = SessionCopyFailure.CreateFailed;
                        // Creation is shielded; ownership must be returned before cancellation can request cleanup.
                        temporaryMayRemain = true;
                        temporary = await _files.CreateNewTemporaryAsync(temporaryPath).ConfigureAwait(false);
                        temporaryOwned = true;
                        if (!temporary.CanWrite) throw new IOException();
                        stage = SessionCopyFailure.WriteFailed;
                        await temporary.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        stage = SessionCopyFailure.FlushFailed;
                        await temporary.FlushAsync(cancellationToken).ConfigureAwait(false);
                        stage = SessionCopyFailure.CleanupFailed;
                        var closingTemporary = temporary; temporary = null;
                        await closingTemporary.DisposeAsync().ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        stage = SessionCopyFailure.PublishFailed; publication = SessionCopyPublication.Uncertain;
                        await _files.PublishNewAsync(temporaryPath, request.DestinationPath).ConfigureAwait(false);
                        publication = SessionCopyPublication.Published; temporaryOwned = false; temporaryMayRemain = false;
                        // Publication is committed. Late cancellation cannot turn the known receipt into a canceled result.
                        result = new(SessionCopyStatus.Published, request.Format, inspection, request.DestinationPath,
                            Hash(bytes), bytes.Length, diagnostics.ToImmutable())
                            { StorageDurability = _files.GetDurability(request.DestinationPath),
                              Retention = new(request.Format != SessionCopyFormat.CompatibleCurrentJsonl, 0, 0, unknown, !archive) };
                    }
                }
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (temporary is not null)
                try { await temporary.DisposeAsync().ConfigureAwait(false); } catch (Exception) { cleanup.Add(SessionCopyFailure.CleanupFailed); }
            if (temporaryOwned)
            {
                try { await _files.DeleteTemporaryAsync(temporaryPath).ConfigureAwait(false); temporaryMayRemain = false; }
                catch (Exception) { cleanup.Add(SessionCopyFailure.CleanupFailed); }
            }
            if (source is not null)
                try { await source.DisposeAsync().ConfigureAwait(false); } catch (Exception) { cleanup.Add(SessionCopyFailure.CleanupFailed); }
        }
        if (failure is not null) Throw(failure, stage, publication, temporaryMayRemain, cleanup.ToImmutable(), cancellationToken);
        if (cleanup.Count != 0)
        {
            if (publication != SessionCopyPublication.Published)
                throw new SessionCopyException(SessionCopyFailure.CleanupFailed, publication, temporaryMayRemain, cleanup.ToImmutable());
            return result! with { Status = SessionCopyStatus.PublishedWithCleanupFailure,
                Diagnostics = result.Diagnostics.Add(new(SessionCopyDiagnosticCode.CleanupFailed)) };
        }
        if (publication != SessionCopyPublication.Published) cancellationToken.ThrowIfCancellationRequested();
        return result!;
    }

    private async Task<SessionCopyInspection> InspectOwnedAsync(Stream source, ImmutableArray<string> ids, CancellationToken token)
    {
        var log = await new SessionLogReader(_bounds).ReadAsync(source, cancellationToken: token).ConfigureAwait(false);
        var diagnostics = ImmutableArray.CreateBuilder<SessionCopyDiagnostic>();
        SessionEntryMigrationResult? migration = null;
        if (!log.SourceComplete) diagnostics.Add(new(SessionCopyDiagnosticCode.SourceIncomplete));
        else if (log.Status == SessionLogReadStatus.ResourceLimit) diagnostics.Add(new(SessionCopyDiagnosticCode.ResourceLimit));
        else
        {
            try
            {
                var records = ParseRecords(log.OriginalBytes, token);
                migration = new SessionEntryMigration(new(_bounds.MaximumRecords, _bounds.MaximumInputBytes,
                    _outputLimit, _bounds.CodecOptions)).Migrate(records, ids);
                token.ThrowIfCancellationRequested();
                if (migration.Status != SessionEntryMigrationStatus.Completed)
                    diagnostics.Add(new(SessionCopyDiagnosticCode.MigrationBlocked));
                else if (migration.Records.IsEmpty || migration.Records[0].Id.Length == 0 ||
                    migration.SourceVersion == 3 && log.Status != SessionLogReadStatus.Complete)
                    diagnostics.Add(new(SessionCopyDiagnosticCode.SourceInvalid));
                else
                {
                    var entries = migration.Records.RemoveAt(0);
                    new SessionContextProjector(new(MaximumEntries: _bounds.MaximumRecords,
                        MaximumInputCharacters: _bounds.MaximumInputBytes)).Project(entries, null, token);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    for (var index = 0; index < entries.Length; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (entries[index].ParentId is { } parent && !seen.Contains(parent))
                        { diagnostics.Add(new(SessionCopyDiagnosticCode.UnsupportedEntryOrder, index + 1)); break; }
                        seen.Add(entries[index].Id);
                    }
                }
            }
            catch (SessionContextProjectionException error)
            { diagnostics.Add(new(SessionCopyDiagnosticCode.InvalidGraph, GraphFailure: error.Failure)); }
            catch (CopyLimitException) { diagnostics.Add(new(SessionCopyDiagnosticCode.ResourceLimit)); }
            catch (Exception error) when (error is System.Text.Json.JsonException or DecoderFallbackException or ArgumentException)
            { diagnostics.Add(new(SessionCopyDiagnosticCode.SourceInvalid)); }
        }
        var valid = diagnostics.Count == 0;
        if (valid) diagnostics.Add(new(SessionCopyDiagnosticCode.AllJsonFieldsRetained));
        return new(log, Hash(log.OriginalBytes.AsSpan()), migration, diagnostics.ToImmutable(), valid);
    }

    private ImmutableArray<JsonData> ParseRecords(ImmutableArray<byte> bytes, CancellationToken token)
    {
        var records = ImmutableArray.CreateBuilder<JsonData>(); var start = 0; var lines = 0;
        var codec = _bounds.CodecOptions ?? new();
        while (start < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            var newline = bytes.AsSpan().Slice(start).IndexOf((byte)'\n');
            var end = newline < 0 ? bytes.Length : start + newline;
            var next = newline < 0 ? bytes.Length : end + 1;
            if (newline >= 0 && end > start && bytes[end - 1] == '\r') end--;
            if (++lines > _bounds.MaximumLines || end - start > _bounds.MaximumLineBytes) throw new CopyLimitException();
            var line = bytes.AsSpan(start, end - start); start = next;
            var blank = true; foreach (var item in line) if (item is not (32 or 9 or 13)) { blank = false; break; }
            if (blank) continue;
            if (records.Count >= _bounds.MaximumRecords || line.Length > codec.MaximumUtf8Bytes) throw new CopyLimitException();
            var text = Utf8.GetString(line);
            if (text.Length > codec.MaximumRecordCharacters) throw new CopyLimitException();
            records.Add(JsonData.Parse(text));
        }
        return records.ToImmutable();
    }

    private (ImmutableArray<string> Ids, bool UnknownPresent, SessionCopyDiagnosticCode? Disclosure)
        ExportUnknownIdentities(SessionCopyInspection inspection, bool archive, CancellationToken token)
    {
        if (!archive)
        {
            var current = inspection.CurrentRecords.Where(entry => entry.Kind == SessionEntryKind.Unknown)
                .Select(entry => entry.Id).ToImmutableArray();
            return (current, !current.IsEmpty, null);
        }
        // Exact archival identities come only from original bytes. A migration plan is a proposal,
        // never an identity source for the unchanged archive. Classification grants no admission.
        try
        {
            var records = ParseRecords(inspection.OriginalBytes, token);
            if (records.IsEmpty) return ([], false, SessionCopyDiagnosticCode.ArchiveIdentityInventoryUnavailable);
            var ids = ImmutableArray.CreateBuilder<string>(); var incomplete = false; var unknownPresent = false;
            foreach (var original in records)
            {
                token.ThrowIfCancellationRequested(); var body = original.Value;
                _codec.ValidateOpaqueJson(body);
                if (body.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !body.TryGetProperty("type", out var type) || type.ValueKind != System.Text.Json.JsonValueKind.String)
                    return ([], false, SessionCopyDiagnosticCode.ArchiveIdentityInventoryUnavailable);
                if (SessionEntryCodec.ClassifyType(type.GetString()!) != SessionEntryKind.Unknown) continue;
                unknownPresent = true;
                if (body.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
                    ids.Add(id.GetString()!);
                else incomplete = true;
            }
            return (ids.ToImmutable(), unknownPresent, incomplete ? SessionCopyDiagnosticCode.ArchiveIdentityInventoryIncomplete : null);
        }
        catch (Exception error) when (error is CopyLimitException or System.Text.Json.JsonException or
            DecoderFallbackException or ArgumentException or SessionEntryCodecException)
        {
            // Opaque preservation can succeed when a complete identity inventory cannot. Do not
            // return a partially parsed or projected identity list as authoritative metadata.
            return ([], false, SessionCopyDiagnosticCode.ArchiveIdentityInventoryUnavailable);
        }
    }

    private byte[]? Encode(ImmutableArray<SessionEntry> records, CancellationToken token)
    {
        using var output = new MemoryStream();
        foreach (var entry in records)
        {
            token.ThrowIfCancellationRequested();
            var bytes = Utf8.GetBytes(_codec.Serialize(entry));
            if ((long)bytes.Length + 1 > _outputLimit - output.Length) return null;
            output.Write(bytes); output.WriteByte((byte)'\n');
        }
        return output.ToArray();
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void ValidatePath(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path) ||
                path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
                !SamePath(path, Path.GetFullPath(path)) || string.IsNullOrEmpty(Path.GetFileName(path))) throw Invalid();
            for (var index = 0; index < path.Length; index++)
            {
                if (char.IsHighSurrogate(path[index]))
                { if (++index >= path.Length || !char.IsLowSurrogate(path[index])) throw Invalid(); }
                else if (char.IsLowSurrogate(path[index])) throw Invalid();
            }
        }
        catch (SessionCopyException) { throw; }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { throw Invalid(); }
    }
    private static SessionCopyException Invalid() => new(SessionCopyFailure.InvalidRequest, SessionCopyPublication.NotAttempted, false, []);
    private static void Throw(Exception? error, SessionCopyFailure failure, SessionCopyPublication publication,
        bool temporaryMayRemain, ImmutableArray<SessionCopyFailure> cleanup, CancellationToken token)
    {
        if (error is OperationCanceledException && token.IsCancellationRequested && publication == SessionCopyPublication.NotAttempted)
            throw new SessionCopyCanceledException(token, temporaryMayRemain, cleanup);
        throw new SessionCopyException(failure, publication, temporaryMayRemain, cleanup);
    }
    private sealed class CopyLimitException : Exception { }
    private sealed class LocalFiles : ISessionCopyFileSystem
    {
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        public ValueTask<Stream> CreateNewTemporaryAsync(string path) => ValueTask.FromResult<Stream>(new FileStream(path,
            FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan));
        public ValueTask PublishNewAsync(string temporaryPath, string destinationPath)
        { File.Move(temporaryPath, destinationPath, overwrite: false); return ValueTask.CompletedTask; }
        public ValueTask DeleteTemporaryAsync(string path)
        { File.Delete(path); return ValueTask.CompletedTask; }
    }
}
