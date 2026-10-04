using System.Collections.Immutable;
using System.Security.Cryptography;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Tree;

namespace PiSharp.Sessions.Storage;

public enum SessionBranchPublication { NotAttempted, PublishedUnattached, Uncertain }
public enum SessionBranchPublishFailure { InvalidRequest, InvalidPlan, CreateFailed, WriteFailed, FlushFailed, PublishFailed, CleanupFailed }
public sealed class SessionBranchPublishException : Exception
{
    public SessionBranchPublishFailure Failure { get; }
    public SessionBranchPublication Publication { get; }
    public bool TemporaryMayRemain { get; }
    public ImmutableArray<SessionBranchPublishFailure> CleanupFailures { get; }
    internal SessionBranchPublishException(SessionBranchPublishFailure failure, SessionBranchPublication publication,
        bool temporaryMayRemain, ImmutableArray<SessionBranchPublishFailure> cleanup)
        : base("Session branch publication failed; inspect its explicit publication and cleanup disposition.")
    { Failure = failure; Publication = publication; TemporaryMayRemain = temporaryMayRemain; CleanupFailures = cleanup; }
}
public sealed class SessionBranchPublishCanceledException : OperationCanceledException
{
    public bool TemporaryMayRemain { get; }
    public ImmutableArray<SessionBranchPublishFailure> CleanupFailures { get; }
    internal SessionBranchPublishCanceledException(CancellationToken token, bool temporaryMayRemain,
        ImmutableArray<SessionBranchPublishFailure> cleanup) : base("Session branch preparation canceled after owned cleanup.", token)
    { TemporaryMayRemain = temporaryMayRemain; CleanupFailures = cleanup; }
}
public interface ISessionBranchOutput : IAsyncDisposable
{
    ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    ValueTask FlushAsync(CancellationToken cancellationToken);
    void FlushToDisk();
}
/// <summary>Trusted local I/O seam. Create must settle acquired resources if it throws before returning ownership;
/// PublishNew must not overwrite. FlushToDisk must be an actual file-content durability barrier.</summary>
public interface ISessionBranchFileSystem
{
    bool FileExists(string path) => File.Exists(path);
    bool DirectoryExists(string path) => Directory.Exists(path);
    SessionLogStorageDurability GetDurability(string path) => SessionLogStorageDurability.LocalFileFlush;
    ValueTask<ISessionBranchOutput> CreateNewTemporaryAsync(string path);
    ValueTask PublishNewAsync(string temporaryPath, string destinationPath);
    ValueTask DeleteOwnedAsync(string path);
}
public sealed record SessionBranchPublishOptions(SessionLogReaderOptions? ReaderOptions = null,
    SessionContextProjectionOptions? GraphOptions = null);

/// <summary>Owns one known newly published file until host attachment commits. A staged file is visible to
/// filesystem readers before attachment; this receipt never treats publication as attachment acknowledgment.</summary>
public sealed class PublishedSessionBranch : IAsyncDisposable
{
    private readonly ISessionBranchFileSystem files;
    private readonly object gate = new();
    private bool attached;
    private Task? disposal;
    public string Path { get; }
    public string Sha256 { get; }
    public int ByteLength { get; }
    public SessionBranchPlan Plan { get; }
    public SessionLogStorageDurability StorageDurability { get; }
    internal PublishedSessionBranch(string path, string hash, SessionBranchPlan plan, ISessionBranchFileSystem files)
    { Path = path; Sha256 = hash; Plan = plan; ByteLength = plan.JsonlBytes.Length; this.files = files;
        StorageDurability = files.GetDurability(path); }
    public void CommitAttachment()
    {
        lock (gate)
        {
            if (disposal is not null) throw new InvalidOperationException("Published session branch is already closed.");
            attached = true;
        }
    }
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; bool delete = false;
        lock (gate)
        {
            if (disposal is null)
            { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); disposal = completion.Task; delete = !attached; }
        }
        if (completion is not null) _ = CloseAsync(delete, completion);
        return new(disposal);
    }
    private async Task CloseAsync(bool delete, TaskCompletionSource completion)
    {
        try
        {
            if (delete) await files.DeleteOwnedAsync(Path).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception)
        { completion.TrySetException(new SessionBranchPublishException(SessionBranchPublishFailure.CleanupFailed,
            SessionBranchPublication.PublishedUnattached, false, [SessionBranchPublishFailure.CleanupFailed])); }
    }
}

/// <summary>Validates complete current-v3 plan bytes before any effects, durably stages a fresh file and
/// publishes without overwrite. It supplies no default home, model/provider, clock or ID authority.</summary>
public sealed class SessionBranchPublisher
{
    private readonly SessionBranchPublishOptions options;
    private readonly ISessionBranchFileSystem files;
    public static ISessionBranchFileSystem LocalFileSystem { get; } = new LocalFiles();
    public SessionBranchPublisher(SessionBranchPublishOptions? options = null, ISessionBranchFileSystem? fileSystem = null)
    {
        this.options = options ?? new(); this.files = fileSystem ?? LocalFileSystem;
        _ = new SessionLogReader(this.options.ReaderOptions); _ = new SessionContextProjector(this.options.GraphOptions);
        if ((this.options.ReaderOptions ?? new()).MaximumInputBytes > 67_108_864) throw new ArgumentOutOfRangeException(nameof(options));
    }
    public async Task<PublishedSessionBranch> PrepareAsync(SessionBranchPlan plan, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ValidatePath(destinationPath); cancellationToken.ThrowIfCancellationRequested();
        if (files.FileExists(destinationPath) || files.DirectoryExists(destinationPath) || !files.DirectoryExists(System.IO.Path.GetDirectoryName(destinationPath)!))
            throw Error(SessionBranchPublishFailure.InvalidRequest);
        await ValidatePlanAsync(plan, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var temporaryPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destinationPath)!, ".pisharp-branch-" + Guid.NewGuid().ToString("N") + ".tmp");
        ISessionBranchOutput? output = null; bool temporaryOwned = false, temporaryMayRemain = false;
        var stage = SessionBranchPublishFailure.CreateFailed; var publication = SessionBranchPublication.NotAttempted;
        PublishedSessionBranch? result = null; Exception? failure = null;
        var cleanup = ImmutableArray.CreateBuilder<SessionBranchPublishFailure>();
        try
        {
            temporaryMayRemain = true;
            output = await files.CreateNewTemporaryAsync(temporaryPath).ConfigureAwait(false); temporaryOwned = true;
            cancellationToken.ThrowIfCancellationRequested(); stage = SessionBranchPublishFailure.WriteFailed;
            await output.WriteAsync(plan.JsonlBytes.ToArray(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); stage = SessionBranchPublishFailure.FlushFailed;
            await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.FlushToDisk();
            stage = SessionBranchPublishFailure.CleanupFailed;
            var closing = output; output = null; await closing.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested(); stage = SessionBranchPublishFailure.PublishFailed;
            publication = SessionBranchPublication.Uncertain;
            await files.PublishNewAsync(temporaryPath, destinationPath).ConfigureAwait(false);
            publication = SessionBranchPublication.PublishedUnattached; temporaryOwned = false; temporaryMayRemain = false;
            // Publication is known. Late cancellation returns the owned receipt so the host can close it;
            // it cannot lose cleanup authority by turning a known publication into an ordinary canceled task.
            result = new(destinationPath, Convert.ToHexString(SHA256.HashData(plan.JsonlBytes.AsSpan())).ToLowerInvariant(), plan, files);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (output is not null) try { await output.DisposeAsync().ConfigureAwait(false); } catch (Exception) { cleanup.Add(SessionBranchPublishFailure.CleanupFailed); }
            if (temporaryOwned)
                try { await files.DeleteOwnedAsync(temporaryPath).ConfigureAwait(false); temporaryMayRemain = false; }
                catch (Exception) { cleanup.Add(SessionBranchPublishFailure.CleanupFailed); }
        }
        if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested && publication == SessionBranchPublication.NotAttempted)
            throw new SessionBranchPublishCanceledException(cancellationToken, temporaryMayRemain, cleanup.ToImmutable());
        if (failure is not null || cleanup.Count != 0)
            throw new SessionBranchPublishException(failure is null ? SessionBranchPublishFailure.CleanupFailed : stage,
                publication, temporaryMayRemain, cleanup.ToImmutable());
        return result!;
    }
    private async Task ValidatePlanAsync(SessionBranchPlan plan, CancellationToken token)
    {
        if (plan.JsonlBytes.IsDefaultOrEmpty || plan.Entries.IsDefault || plan.Header is null || plan.JsonlBytes[^1] != '\n')
            throw Error(SessionBranchPublishFailure.InvalidPlan);
        var log = await new SessionLogReader(options.ReaderOptions).ReadAsync(new MemoryStream(plan.JsonlBytes.ToArray(), false),
            leaveOpen: false, cancellationToken: token).ConfigureAwait(false);
        if (!log.SourceComplete || log.Status != SessionLogReadStatus.Complete || log.Header is null ||
            log.ValidatedPrefix.Length != plan.Entries.Length + 1 || log.Header.WireBody.ToString() != plan.Header.WireBody.ToString())
            throw Error(SessionBranchPublishFailure.InvalidPlan);
        for (var index = 0; index < plan.Entries.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (plan.Entries[index] is null || log.ValidatedPrefix[index + 1].Entry.WireBody.ToString() != plan.Entries[index].WireBody.ToString())
                throw Error(SessionBranchPublishFailure.InvalidPlan);
        }
        var entries = log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray();
        if (plan.LeafId != (entries.IsEmpty ? null : entries[^1].Id)) throw Error(SessionBranchPublishFailure.InvalidPlan);
        try { new SessionContextProjector(options.GraphOptions).Project(entries, null, token); }
        catch (SessionContextProjectionException) { throw Error(SessionBranchPublishFailure.InvalidPlan); }
    }
    private static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096 || !System.IO.Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            !string.Equals(path, System.IO.Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            string.IsNullOrEmpty(System.IO.Path.GetFileName(path))) throw Error(SessionBranchPublishFailure.InvalidRequest);
        for (var index = 0; index < path.Length; index++)
        {
            if (char.IsHighSurrogate(path[index])) { if (++index >= path.Length || !char.IsLowSurrogate(path[index])) throw Error(SessionBranchPublishFailure.InvalidRequest); }
            else if (char.IsLowSurrogate(path[index])) throw Error(SessionBranchPublishFailure.InvalidRequest);
        }
    }
    private static SessionBranchPublishException Error(SessionBranchPublishFailure failure) => new(failure, SessionBranchPublication.NotAttempted, false, []);
    private sealed class LocalFiles : ISessionBranchFileSystem
    {
        public ValueTask<ISessionBranchOutput> CreateNewTemporaryAsync(string path) => ValueTask.FromResult<ISessionBranchOutput>(new LocalOutput(
            new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan)));
        public ValueTask PublishNewAsync(string temporaryPath, string destinationPath)
        { File.Move(temporaryPath, destinationPath, overwrite: false); return ValueTask.CompletedTask; }
        public ValueTask DeleteOwnedAsync(string path) { File.Delete(path); return ValueTask.CompletedTask; }
    }
    private sealed class LocalOutput(FileStream stream) : ISessionBranchOutput
    {
        public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token) => stream.WriteAsync(bytes, token);
        public ValueTask FlushAsync(CancellationToken token) => new(stream.FlushAsync(token));
        public void FlushToDisk() => stream.Flush(flushToDisk: true);
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
