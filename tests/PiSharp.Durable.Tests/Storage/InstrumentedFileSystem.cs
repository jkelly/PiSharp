using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/jsonl-storage.test.ts (InstrumentedEnv). A fault-injecting
// IFileSystem over the local file system: it records every append, flush, write, rename and remove as "<operation>:<file name>" and
// fails the configured call of one operation before it runs, after it ran ("after": the effect happened but the call reports failure),
// or after writing only the first half of the bytes ("short": a torn append or write). Upstream subclasses NodeExecutionEnv; this
// decorator wraps any IFileSystem.
internal enum FailureOperation { Append, Flush, Write, Rename, Remove }

internal enum FailureMode { Before, After, Short }

internal sealed record Failure(FailureOperation Operation, int Call, FailureMode Mode)
{
    public override string ToString() => $"{Mode.ToString().ToLowerInvariant()} failure at {Operation.ToString().ToLowerInvariant()} {Call}";
}

internal sealed class InstrumentedFileSystem(IFileSystem inner) : IFileSystem
{
    private readonly Dictionary<FailureOperation, int> calls = [];
    private Failure? failure;

    public InstrumentedFileSystem(string directory) : this(new LocalFileSystem(directory)) { }

    public List<string> Operations { get; } = [];

    public void Fail(Failure next)
    {
        failure = next;
        ResetObservations();
    }

    public void Clear()
    {
        failure = null;
        ResetObservations();
    }

    private void ResetObservations()
    {
        calls.Clear();
        Operations.Clear();
    }

    /// <summary>Counts and records the call, and returns the failure to inject into it, if any.</summary>
    private Failure? Observe(FailureOperation operation, string description)
    {
        calls[operation] = calls.GetValueOrDefault(operation) + 1;
        Operations.Add(description);
        return failure is { } next && next.Operation == operation && next.Call == calls[operation] ? next : null;
    }

    private static FileError Injected(string what, string path) => new(FileErrorCode.Unknown, "injected " + what, path);

    public async Task<Result<Unit, FileError>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context)
    {
        if (Observe(FailureOperation.Append, "append:" + Path.GetFileName(path)) is not { } injected) return await inner.AppendFileAsync(path, content, context);
        if (injected.Mode == FailureMode.Before) return Injected("append failure", path);
        if (injected.Mode == FailureMode.Short)
        {
            var partial = await inner.AppendFileAsync(path, content[..Math.Max(1, content.Length / 2)], context);
            return partial.IsOk ? Injected("short append", path) : partial;
        }
        var written = await inner.AppendFileAsync(path, content, context);
        return written.IsOk ? Injected("post-append failure", path) : written;
    }

    public async Task<Result<Unit, FileError>> FlushFileAsync(string path, DurableContext context)
    {
        if (Observe(FailureOperation.Flush, "flush:" + Path.GetFileName(path)) is not { } injected) return await inner.FlushFileAsync(path, context);
        if (injected.Mode == FailureMode.After)
        {
            var flushed = await inner.FlushFileAsync(path, context);
            if (!flushed.IsOk) return flushed;
        }
        return Injected("flush failure", path);
    }

    public async Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context)
    {
        if (Observe(FailureOperation.Write, "write:" + Path.GetFileName(path)) is not { } injected) return await inner.WriteFileAsync(path, content, context);
        if (injected.Mode == FailureMode.Before) return Injected("write failure", path);
        if (injected.Mode == FailureMode.Short)
        {
            var partial = await inner.WriteFileAsync(path, content[..Math.Max(1, content.Length / 2)], context);
            return partial.IsOk ? Injected("short write", path) : partial;
        }
        var written = await inner.WriteFileAsync(path, content, context);
        return written.IsOk ? Injected("post-write failure", path) : written;
    }

    public async Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, DurableContext context)
    {
        var description = $"rename:{Path.GetFileName(sourcePath)}->{Path.GetFileName(destinationPath)}";
        if (Observe(FailureOperation.Rename, description) is not { } injected) return await inner.RenameFileAsync(sourcePath, destinationPath, context);
        if (injected.Mode == FailureMode.After)
        {
            var renamed = await inner.RenameFileAsync(sourcePath, destinationPath, context);
            if (!renamed.IsOk) return renamed;
        }
        return Injected("rename failure", sourcePath);
    }

    public async Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, DurableContext context)
    {
        if (Observe(FailureOperation.Remove, "remove:" + Path.GetFileName(path)) is not { } injected) return await inner.RemoveAsync(path, options, context);
        if (injected.Mode == FailureMode.After)
        {
            var removed = await inner.RemoveAsync(path, options, context);
            if (!removed.IsOk) return removed;
        }
        return Injected("remove failure", path);
    }

    public string Id => inner.Id;
    public string Cwd { get => inner.Cwd; set => inner.Cwd = value; }
    public Task<Result<string, FileError>> AbsolutePathAsync(string path, DurableContext context) => inner.AbsolutePathAsync(path, context);
    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, DurableContext context) => inner.JoinPathAsync(parts, context);
    public Task<Result<string, FileError>> ReadTextFileAsync(string path, DurableContext context) => inner.ReadTextFileAsync(path, context);
    public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, DurableContext context) => inner.ReadBinaryFileAsync(path, context);
    public Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(string path, BinaryReaderOptions? options, DurableContext context) =>
        inner.OpenBinaryReaderAsync(path, options, context);
    public Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, DurableContext context) => inner.TruncateFileAsync(path, size, context);
    public Task<Result<FileMetadata, FileError>> FileInfoAsync(string path, DurableContext context) => inner.FileInfoAsync(path, context);
    public Task<Result<ImmutableArray<FileMetadata>, FileError>> ListDirAsync(string path, DurableContext context) => inner.ListDirAsync(path, context);
    public Task<Result<IDirReader, FileError>> OpenDirReaderAsync(string path, DurableContext context) => inner.OpenDirReaderAsync(path, context);
    public Task<Result<string, FileError>> CanonicalPathAsync(string path, DurableContext context) => inner.CanonicalPathAsync(path, context);
    public Task<Result<bool, FileError>> ExistsAsync(string path, DurableContext context) => inner.ExistsAsync(path, context);
    public Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, DurableContext context) => inner.CreateDirAsync(path, options, context);
}
