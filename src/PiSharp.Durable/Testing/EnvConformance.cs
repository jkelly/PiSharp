// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/testing/env-conformance.ts (the file-system cases: binary and
// directory readers). Cases keep upstream's names, inputs and expected values; upstream's read(0, 1.5) check is dropped because offsets
// and lengths are integers here. Still to come with the ExecutionEnv (P9): "binary reader scans lines like decoding the whole file"
// (BinaryReader.scanLines), the watch cases, the exec cases and the symlink cases (which create links through the shell).
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Testing;

/// <summary>The runner-independent environment conformance suite, file-system half (upstream <c>createEnvConformance</c>).</summary>
public static class EnvConformance
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Creates the cases. <paramref name="withFileSystem"/> must call and await its callback exactly once per case with a file
    /// system whose <see cref="IFileSystem.Cwd"/> is a fresh, empty, writable directory.</summary>
    public static IReadOnlyList<EnvConformanceCase> Create(EnvConformanceProvider withFileSystem)
    {
        ArgumentNullException.ThrowIfNull(withFileSystem);
        EnvConformanceCase Case(string name, Func<IFileSystem, Task> test) => new(name, () => withFileSystem(test));
        return
        [
            Case("binary reader reads byte ranges of the opened file", BinaryReaderReadsRanges),
            Case("binary reader keeps reading the file it opened after a rename", BinaryReaderKeepsOpenedFile),
            Case("binary reader refuses directories, missing files and aborted opens", BinaryReaderRefuses),
            Case("directory reader pages every entry exactly once", DirReaderPagesEveryEntry),
            Case("directory reader reports the end and refuses use after close", DirReaderReportsEnd),
            Case("directory reader refuses missing paths and files", DirReaderRefuses),
            Case("directory reader skips entries removed during enumeration", DirReaderSkipsRemoved),
        ];
    }

    private static async Task BinaryReaderReadsRanges(IFileSystem env)
    {
        (await env.WriteFileAsync("data.txt", "hello world", Context)).GetOrThrow();
        var reader = (await env.OpenBinaryReaderAsync("data.txt", null, Context)).GetOrThrow();
        var info = (await reader.InfoAsync(Context)).GetOrThrow();
        Equal("data.txt", info.Name, "info name");
        Equal(FileKind.File, info.Kind, "info kind");
        Equal(11L, info.Size, "info size");
        Equal("hello", Text((await reader.ReadAsync(0, 5, Context)).GetOrThrow()), "first range");
        Equal("world", Text((await reader.ReadAsync(6, 100, Context)).GetOrThrow()), "range past the end");
        Equal(0, (await reader.ReadAsync(11, 4, Context)).GetOrThrow().Length, "read at the end");
        Equal(0, (await reader.ReadAsync(50, 1, Context)).GetOrThrow().Length, "read past the end");
        Equal(0, (await reader.ReadAsync(3, 0, Context)).GetOrThrow().Length, "empty read");
        Equal(FileErrorCode.Invalid, Code(await reader.ReadAsync(-1, 1, Context)), "negative offset");
        Equal(FileErrorCode.Aborted, Code(await reader.ReadAsync(0, 1, AbortedContext())), "aborted read");
        await reader.CloseAsync(Context);
        await reader.CloseAsync(Context);
        Equal(FileErrorCode.Invalid, Code(await reader.ReadAsync(0, 1, Context)), "read after close");
        Equal(FileErrorCode.Invalid, Code(await reader.InfoAsync(Context)), "info after close");
    }

    private static async Task BinaryReaderKeepsOpenedFile(IFileSystem env)
    {
        (await env.WriteFileAsync("a.txt", "one", Context)).GetOrThrow();
        var reader = (await env.OpenBinaryReaderAsync("a.txt", null, Context)).GetOrThrow();
        try
        {
            (await env.RenameFileAsync("a.txt", "b.txt", Context)).GetOrThrow();
            (await env.WriteFileAsync("a.txt", "two", Context)).GetOrThrow();
            Equal("one", Text((await reader.ReadAsync(0, 10, Context)).GetOrThrow()), "the opened file");
        }
        finally { await reader.CloseAsync(Context); }
    }

    private static async Task BinaryReaderRefuses(IFileSystem env)
    {
        (await env.CreateDirAsync("dir", null, Context)).GetOrThrow();
        (await env.WriteFileAsync("file.txt", "x", Context)).GetOrThrow();
        Equal(FileErrorCode.IsDirectory, Code(await env.OpenBinaryReaderAsync("dir", null, Context)), "directory");
        Equal(FileErrorCode.NotFound, Code(await env.OpenBinaryReaderAsync("missing.txt", null, Context)), "missing file");
        Equal(FileErrorCode.Aborted, Code(await env.OpenBinaryReaderAsync("file.txt", null, AbortedContext())), "aborted open");
    }

    private static async Task DirReaderPagesEveryEntry(IFileSystem env)
    {
        string[] names = ["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"];
        foreach (var name in names) (await env.WriteFileAsync(name, name, Context)).GetOrThrow();
        (await env.CreateDirAsync("sub", null, Context)).GetOrThrow();
        var (pages, done) = await ReadAll(env, ".", 2);
        Check(done, "directory reader reached the end");
        foreach (var page in pages) Check(page.Count <= 2, "page within maxEntries");
        var entries = pages.SelectMany(page => page).ToList();
        Equal(string.Join(",", names.Append("sub").Order(StringComparer.Ordinal)),
            string.Join(",", entries.Select(entry => entry.Name).Order(StringComparer.Ordinal)), "entries");
        Equal(FileKind.Directory, entries.Single(entry => entry.Name == "sub").Kind, "sub kind");
        var file = entries.Single(entry => entry.Name == "a.txt");
        Check(file.Kind == FileKind.File && file.Size == 5, "a.txt is a five-byte file: " + file);
    }

    private static async Task DirReaderReportsEnd(IFileSystem env)
    {
        (await env.CreateDirAsync("empty", null, Context)).GetOrThrow();
        var reader = (await env.OpenDirReaderAsync("empty", Context)).GetOrThrow();
        for (var call = 0; call < 2; call++)
        {
            var page = (await reader.NextAsync(10, Context)).GetOrThrow();
            Check(page.Entries.IsEmpty && page.Done, $"empty page {call} at the end");
        }
        Equal(FileErrorCode.Invalid, Code(await reader.NextAsync(0, Context)), "zero maxEntries");
        Equal(FileErrorCode.Aborted, Code(await reader.NextAsync(1, AbortedContext())), "aborted page");
        await reader.CloseAsync(Context);
        await reader.CloseAsync(Context);
        Equal(FileErrorCode.Invalid, Code(await reader.NextAsync(1, Context)), "page after close");
    }

    private static async Task DirReaderRefuses(IFileSystem env)
    {
        (await env.WriteFileAsync("file.txt", "x", Context)).GetOrThrow();
        Equal(FileErrorCode.NotFound, Code(await env.OpenDirReaderAsync("missing", Context)), "missing directory");
        Equal(FileErrorCode.NotDirectory, Code(await env.OpenDirReaderAsync("file.txt", Context)), "file");
        Equal(FileErrorCode.Aborted, Code(await env.OpenDirReaderAsync(".", AbortedContext())), "aborted open");
    }

    private static async Task DirReaderSkipsRemoved(IFileSystem env)
    {
        (await env.CreateDirAsync("dir", null, Context)).GetOrThrow();
        foreach (var name in new[] { "x", "y", "z" }) (await env.WriteFileAsync("dir/" + name, name, Context)).GetOrThrow();
        var reader = (await env.OpenDirReaderAsync("dir", Context)).GetOrThrow();
        try
        {
            foreach (var name in new[] { "x", "y", "z" }) (await env.RemoveAsync("dir/" + name, null, Context)).GetOrThrow();
            var entries = new List<FileMetadata>();
            for (var page = 0; page < 10; page++)
            {
                var next = (await reader.NextAsync(10, Context)).GetOrThrow();
                entries.AddRange(next.Entries);
                if (next.Done) break;
            }
            Equal("", string.Join(",", entries.Select(entry => entry.Name)), "entries after removal");
        }
        finally { await reader.CloseAsync(Context); }
    }

    private static async Task<(List<IReadOnlyList<FileMetadata>> Pages, bool Done)> ReadAll(IFileSystem env, string path, int maxEntries)
    {
        var reader = (await env.OpenDirReaderAsync(path, Context)).GetOrThrow();
        var pages = new List<IReadOnlyList<FileMetadata>>();
        try
        {
            for (var page = 0; page < 1000; page++)
            {
                var next = (await reader.NextAsync(maxEntries, Context)).GetOrThrow();
                pages.Add(next.Entries);
                if (next.Done) return (pages, true);
            }
            return (pages, false);
        }
        finally { await reader.CloseAsync(Context); }
    }

    private static DurableContext AbortedContext()
    {
        var cancellation = DurableContext.Background.WithCancel();
        cancellation.Cancel();
        return cancellation.Context;
    }

    private static string Text(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    private static FileErrorCode? Code<T>(Result<T, FileError> result) => result.IsOk ? null : result.Error.Code;

    private static void Check(bool value, string message)
    {
        if (!value) throw new EnvConformanceException(message);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new EnvConformanceException($"{what}: expected <{expected}>, actual <{actual}>");
    }
}
