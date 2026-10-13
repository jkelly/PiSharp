// The one execution environment the tool-task cases build (upstream's `new NodeExecutionEnv({ cwd })` where a test reads only `env.cwd`).
// It is the only test class implementing IExecutionEnv directly, so when the execution environment adds members (text line readers, watch,
// temp files, cleanup and the Shell half) this file is the one place to update. Its file operations reject: no harness.tools case touches a
// file system or a shell; the cases that do (the coding tools and the real bash interruption) are pending until the coding tools land.
using System.Collections.Immutable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

/// <summary>An environment that only reports its working directory, as given (POSIX paths stay as they are).</summary>
internal sealed class ToolTestEnv(string cwd) : IExecutionEnv
{
    private static Task<Result<T, FileError>> Unsupported<T>() =>
        Task.FromException<Result<T, FileError>>(new NotSupportedException("ToolTestEnv has no file system"));

    public string Id => "tool-test";
    public string Cwd { get; set; } = cwd;
    public Task<Result<string, FileError>> AbsolutePathAsync(string path, DurableContext context) => Unsupported<string>();
    public Task<Result<string, FileError>> JoinPathAsync(IReadOnlyList<string> parts, DurableContext context) => Unsupported<string>();
    public Task<Result<string, FileError>> ReadTextFileAsync(string path, DurableContext context) => Unsupported<string>();
    public Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, DurableContext context) => Unsupported<byte[]>();
    public Task<Result<IBinaryReader, FileError>> OpenBinaryReaderAsync(string path, BinaryReaderOptions? options, DurableContext context) =>
        Unsupported<IBinaryReader>();
    public Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) => Unsupported<Unit>();
    public Task<Result<Unit, FileError>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) => Unsupported<Unit>();
    public Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, DurableContext context) => Unsupported<Unit>();
    public Task<Result<Unit, FileError>> FlushFileAsync(string path, DurableContext context) => Unsupported<Unit>();
    public Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, DurableContext context) => Unsupported<Unit>();
    public Task<Result<FileMetadata, FileError>> FileInfoAsync(string path, DurableContext context) => Unsupported<FileMetadata>();
    public Task<Result<ImmutableArray<FileMetadata>, FileError>> ListDirAsync(string path, DurableContext context) =>
        Unsupported<ImmutableArray<FileMetadata>>();
    public Task<Result<IDirReader, FileError>> OpenDirReaderAsync(string path, DurableContext context) => Unsupported<IDirReader>();
    public Task<Result<string, FileError>> CanonicalPathAsync(string path, DurableContext context) => Unsupported<string>();
    public Task<Result<bool, FileError>> ExistsAsync(string path, DurableContext context) => Unsupported<bool>();
    public Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, DurableContext context) => Unsupported<Unit>();
    public Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, DurableContext context) => Unsupported<Unit>();
    public Task<Result<ITextLineReader, FileError>> OpenTextLineReaderAsync(string path, DurableContext context) => Unsupported<ITextLineReader>();
    public Task<Result<ImmutableArray<string>, FileError>> ReadTextLinesAsync(string path, ReadTextLinesOptions? options, DurableContext context) =>
        Unsupported<ImmutableArray<string>>();
    public Task<Result<IFileWatcher, FileError>> WatchAsync(IReadOnlyList<WatchTarget> targets, Action<WatchChange> onChange, DurableContext context) =>
        Unsupported<IFileWatcher>();
    public Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, DurableContext context) => Unsupported<string>();
    public Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, DurableContext context) => Unsupported<string>();
    public Task<Result<ShellExecResult, ExecutionError>> ExecAsync(ShellCommand command, ShellExecOptions? options, DurableContext context) =>
        Task.FromException<Result<ShellExecResult, ExecutionError>>(new NotSupportedException("ToolTestEnv has no shell"));
    public Task CleanupAsync(DurableContext context) => Task.CompletedTask;
}
