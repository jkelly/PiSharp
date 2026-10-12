using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/env-node.test.ts, the "NodeExecutionEnv filesystem" cases that
// cover what LocalFileSystem implements, as env.filesystem.local.<case>. Adapted: readTextLines, temporary files and cleanup are not part
// of LocalFileSystem yet and are left out of the cases that use them; truncation sizes that a long cannot hold (1.5, NaN, Infinity) are
// dropped; "syncs through an opened handle" spies on Node's FileHandle.sync and has no analogue (flush success and failure codes are
// covered by "flushes existing files"). Symbolic-link assertions run only where the process may create links (on Windows that needs
// Developer Mode or elevation). Still to come with LocalExecutionEnv (P9): text line readers, temporary files, cleanup and the shell.
// Authored: env.filesystem.local.replaces-and-truncates-files-that-other-handles-hold-open pins the sharing behaviour JSONL relies on:
// appends, flushes and truncation work while another handle (such as a binary reader) holds the file open; a rename over a held file
// replaces it on POSIX and fails cleanly on Windows, as Node's MoveFileEx-based rename does.
internal static partial class Program
{
    private static readonly DurableContext EnvContext = DurableContext.Background;

    private static IEnumerable<(string, Func<Task>)> EnvNodeCases() =>
    [
        EnvCase("reads-writes-lists-and-removes-files-and-directories", async (root, env) =>
        {
            Equal(Path.Combine(root, "nested", "child"), (await env.AbsolutePathAsync("nested/child", EnvContext)).GetOrThrow(), "absolute path");
            Equal(Path.Combine(root, "nested", "child"), (await env.JoinPathAsync([root, "nested", "child"], EnvContext)).GetOrThrow(), "joined path");
            (await env.CreateDirAsync("nested/child", null, EnvContext)).GetOrThrow();
            (await env.WriteFileAsync("nested/child/file.txt", "hel", EnvContext)).GetOrThrow();
            (await env.AppendFileAsync("nested/child/file.txt", "lo", EnvContext)).GetOrThrow();
            Equal("hello", (await env.ReadTextFileAsync("nested/child/file.txt", EnvContext)).GetOrThrow(), "text");
            Equal("hello", System.Text.Encoding.UTF8.GetString((await env.ReadBinaryFileAsync("nested/child/file.txt", EnvContext)).GetOrThrow()), "bytes");

            var entries = (await env.ListDirAsync("nested/child", EnvContext)).GetOrThrow();
            Equal(1, entries.Length, "entries");
            Check(entries[0] is { Name: "file.txt", Kind: FileKind.File, Size: 5 } && entries[0].Path == Path.Combine(root, "nested", "child", "file.txt") &&
                entries[0].MtimeMs > 0, "entry: " + entries[0]);

            Equal(true, (await env.ExistsAsync("nested/child/file.txt", EnvContext)).GetOrThrow(), "exists");
            (await env.RemoveAsync("nested/child/file.txt", null, EnvContext)).GetOrThrow();
            Equal(false, (await env.ExistsAsync("nested/child/file.txt", EnvContext)).GetOrThrow(), "exists after removal");
        }),

        EnvCase("expands-home-relative-paths-and-file-urls", async (root, env) =>
        {
            Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "pi-node-env-test"),
                (await env.AbsolutePathAsync("~/pi-node-env-test", EnvContext)).GetOrThrow(), "home-relative path");
            var filePath = Path.Combine(root, "file with spaces.txt");
            Equal(filePath, (await env.AbsolutePathAsync(new Uri(filePath).AbsoluteUri, EnvContext)).GetOrThrow(), "file URL");
        }),

        EnvCase("returns-fileinfo-for-files-directories-and-symlinks-without-following-symlinks", async (root, env) =>
        {
            (await env.CreateDirAsync("dir", new CreateDirOptions { Recursive = true }, EnvContext)).GetOrThrow();
            (await env.WriteFileAsync("dir/file.txt", "hello", EnvContext)).GetOrThrow();
            Check((await env.FileInfoAsync("dir", EnvContext)).GetOrThrow() is { Name: "dir", Kind: FileKind.Directory } directory &&
                directory.Path == Path.Combine(root, "dir"), "directory info");
            Check((await env.FileInfoAsync("dir/file.txt", EnvContext)).GetOrThrow() is { Name: "file.txt", Kind: FileKind.File, Size: 5 } file &&
                file.Path == Path.Combine(root, "dir", "file.txt"), "file info");
            if (!TryCreateSymlinks(root, ("file-link", Path.Combine(root, "dir", "file.txt"), false), ("dir-link", Path.Combine(root, "dir"), true))) return;
            Check((await env.FileInfoAsync("file-link", EnvContext)).GetOrThrow() is { Name: "file-link", Kind: FileKind.Symlink } fileLink &&
                fileLink.Path == Path.Combine(root, "file-link"), "file link info");
            Check((await env.FileInfoAsync("dir-link", EnvContext)).GetOrThrow() is { Name: "dir-link", Kind: FileKind.Symlink } directoryLink &&
                directoryLink.Path == Path.Combine(root, "dir-link"), "directory link info");
            Equal((await env.CanonicalPathAsync("dir/file.txt", EnvContext)).GetOrThrow(), (await env.CanonicalPathAsync("file-link", EnvContext)).GetOrThrow(),
                "canonical path of a link");
        }),

        EnvCase("lists-symlinks-as-symlinks", async (root, env) =>
        {
            (await env.WriteFileAsync("target.txt", "hello", EnvContext)).GetOrThrow();
            if (!TryCreateSymlinks(root, ("link.txt", Path.Combine(root, "target.txt"), false))) return;
            var entries = (await env.ListDirAsync(".", EnvContext)).GetOrThrow();
            Equal("link.txt:Symlink,target.txt:File", string.Join(",", entries.Select(entry => $"{entry.Name}:{entry.Kind}").Order(StringComparer.Ordinal)), "entries");
        }),

        EnvCase("returns-fileerror-for-missing-paths-and-keeps-exists-false-for-missing-paths", async (root, env) =>
        {
            var info = await env.FileInfoAsync("missing.txt", EnvContext);
            Check(!info.IsOk && info.Error.Code == FileErrorCode.NotFound && info.Error.Path == Path.Combine(root, "missing.txt"), "missing info: " + info);
            Equal(false, (await env.ExistsAsync("missing.txt", EnvContext)).GetOrThrow(), "exists");
        }),

        EnvCase("returns-fileerror-for-listing-non-directories", async (root, env) =>
        {
            (await env.WriteFileAsync("file.txt", "hello", EnvContext)).GetOrThrow();
            var result = await env.ListDirAsync("file.txt", EnvContext);
            Check(!result.IsOk && result.Error.Code == FileErrorCode.NotDirectory, "listing a file: " + result);
        }),

        EnvCase("appends-to-new-files-and-creates-parent-directories", async (root, env) =>
        {
            (await env.AppendFileAsync("new/nested/file.txt", "a", EnvContext)).GetOrThrow();
            (await env.AppendFileAsync("new/nested/file.txt", "b", EnvContext)).GetOrThrow();
            Equal("ab", (await env.ReadTextFileAsync("new/nested/file.txt", EnvContext)).GetOrThrow(), "text");
        }),

        EnvCase("atomically-renames-a-file-and-replaces-the-destination", async (root, env) =>
        {
            (await env.WriteFileAsync("source.txt", "new", EnvContext)).GetOrThrow();
            (await env.WriteFileAsync("destination.txt", "old", EnvContext)).GetOrThrow();
            (await env.RenameFileAsync("source.txt", "destination.txt", EnvContext)).GetOrThrow();
            Equal(false, (await env.ExistsAsync("source.txt", EnvContext)).GetOrThrow(), "source exists");
            Equal("new", (await env.ReadTextFileAsync("destination.txt", EnvContext)).GetOrThrow(), "destination");
        }),

        EnvCase("reports-the-source-path-when-rename-fails-because-the-source-is-missing", async (root, env) =>
        {
            (await env.WriteFileAsync("destination.txt", "unchanged", EnvContext)).GetOrThrow();
            var result = await env.RenameFileAsync("missing-source.txt", "destination.txt", EnvContext);
            Check(!result.IsOk && result.Error.Code == FileErrorCode.NotFound && result.Error.Path == Path.Combine(root, "missing-source.txt"), "rename: " + result);
            Equal("unchanged", (await env.ReadTextFileAsync("destination.txt", EnvContext)).GetOrThrow(), "destination");
        }),

        EnvCase("honors-createdir-recursive-false-and-remove-recursive-force-options", async (root, env) =>
        {
            var created = await env.CreateDirAsync("missing/child", new CreateDirOptions { Recursive = false }, EnvContext);
            Check(!created.IsOk && created.Error.Code == FileErrorCode.NotFound, "non-recursive creation: " + created);

            (await env.WriteFileAsync("dir/child/file.txt", "hello", EnvContext)).GetOrThrow();
            Check(!(await env.RemoveAsync("dir", new RemoveOptions { Recursive = false }, EnvContext)).IsOk, "non-recursive removal of a directory");
            (await env.RemoveAsync("dir", new RemoveOptions { Recursive = true }, EnvContext)).GetOrThrow();
            Equal(false, (await env.ExistsAsync("dir", EnvContext)).GetOrThrow(), "exists after recursive removal");

            Check(!(await env.RemoveAsync("missing", new RemoveOptions { Force = false }, EnvContext)).IsOk, "removal of a missing path");
            (await env.RemoveAsync("missing", new RemoveOptions { Force = true }, EnvContext)).GetOrThrow();
        }),

        EnvCase("returns-aborted-results-without-side-effects-for-pre-aborted-file-operations", async (root, env) =>
        {
            (await env.WriteFileAsync("file.txt", "hello", EnvContext)).GetOrThrow();
            var cancellation = EnvContext.WithCancel();
            cancellation.Cancel();
            var context = cancellation.Context;
            var codes = new List<FileErrorCode?>
            {
                Code(await env.ReadTextFileAsync("file.txt", context)),
                Code(await env.ReadBinaryFileAsync("file.txt", context)),
                Code(await env.OpenBinaryReaderAsync("file.txt", null, context)),
                Code(await env.WriteFileAsync("other.txt", "hello", context)),
                Code(await env.AppendFileAsync("file.txt", " world", context)),
                Code(await env.TruncateFileAsync("file.txt", 1, context)),
                Code(await env.FlushFileAsync("file.txt", context)),
                Code(await env.RenameFileAsync("file.txt", "renamed.txt", context)),
                Code(await env.FileInfoAsync("file.txt", context)),
                Code(await env.ListDirAsync(".", context)),
                Code(await env.OpenDirReaderAsync(".", context)),
                Code(await env.CanonicalPathAsync("file.txt", context)),
                Code(await env.ExistsAsync("file.txt", context)),
                Code(await env.CreateDirAsync("dir", null, context)),
                Code(await env.RemoveAsync("file.txt", null, context)),
            };
            Check(codes.All(code => code == FileErrorCode.Aborted), "every operation is aborted: " + string.Join(",", codes));
            Equal("hello", (await env.ReadTextFileAsync("file.txt", EnvContext)).GetOrThrow(), "file");
            Equal("file.txt", string.Join(",", (await env.ListDirAsync(".", EnvContext)).GetOrThrow().Select(entry => entry.Name)), "entries");
        }),

        EnvCase("truncates-and-extends-files-to-exact-byte-sizes", async (root, env) =>
        {
            (await env.WriteFileAsync("file.bin", new byte[] { 0x61, 0xc3, 0xa9, 0x0a, 0x62, 0x0a }, EnvContext)).GetOrThrow();
            // Truncation is byte-exact even when the boundary splits a UTF-8 sequence.
            (await env.TruncateFileAsync("file.bin", 2, EnvContext)).GetOrThrow();
            Equal("61C3", Convert.ToHexString((await env.ReadBinaryFileAsync("file.bin", EnvContext)).GetOrThrow()), "truncated");
            (await env.TruncateFileAsync("file.bin", 4, EnvContext)).GetOrThrow();
            Equal("61C30000", Convert.ToHexString((await env.ReadBinaryFileAsync("file.bin", EnvContext)).GetOrThrow()), "extended");
            (await env.TruncateFileAsync("file.bin", 0, EnvContext)).GetOrThrow();
            Equal(0L, (await env.FileInfoAsync("file.bin", EnvContext)).GetOrThrow().Size, "emptied");
        }),

        EnvCase("rejects-invalid-truncation-sizes-and-never-creates-missing-files", async (root, env) =>
        {
            (await env.WriteFileAsync("file.txt", "hello", EnvContext)).GetOrThrow();
            foreach (var size in new[] { -1L, Ids.MaxSafeInteger + 1 })
            {
                var result = await env.TruncateFileAsync("file.txt", size, EnvContext);
                Check(!result.IsOk && result.Error.Code == FileErrorCode.Invalid && result.Error.Path == Path.Combine(root, "file.txt"), $"size {size}: {result}");
            }
            Equal("hello", (await env.ReadTextFileAsync("file.txt", EnvContext)).GetOrThrow(), "file");
            var missing = await env.TruncateFileAsync("missing.txt", 0, EnvContext);
            Check(!missing.IsOk && missing.Error.Code == FileErrorCode.NotFound && missing.Error.Path == Path.Combine(root, "missing.txt"), "missing: " + missing);
            Equal(false, (await env.ExistsAsync("missing.txt", EnvContext)).GetOrThrow(), "missing file created");
        }),

        EnvCase("flushes-existing-files-without-changing-content-and-reports-missing-paths", async (root, env) =>
        {
            (await env.WriteFileAsync("file.txt", "durable", EnvContext)).GetOrThrow();
            (await env.FlushFileAsync("file.txt", EnvContext)).GetOrThrow();
            Equal("durable", (await env.ReadTextFileAsync("file.txt", EnvContext)).GetOrThrow(), "file");
            var missing = await env.FlushFileAsync("missing.txt", EnvContext);
            Check(!missing.IsOk && missing.Error.Code == FileErrorCode.NotFound && missing.Error.Path == Path.Combine(root, "missing.txt"), "missing: " + missing);
            Equal(false, (await env.ExistsAsync("missing.txt", EnvContext)).GetOrThrow(), "missing file created");
            (await env.CreateDirAsync("dir", null, EnvContext)).GetOrThrow();
            var directory = await env.FlushFileAsync("dir", EnvContext);
            Check(!directory.IsOk && directory.Error.Code == FileErrorCode.IsDirectory, "directory: " + directory);
        }),

        EnvCase("replaces-and-truncates-files-that-other-handles-hold-open", async (root, env) =>
        {
            (await env.WriteFileAsync("held.txt", "old contents", EnvContext)).GetOrThrow();
            (await env.WriteFileAsync("replacement.txt", "new", EnvContext)).GetOrThrow();
            var reader = (await env.OpenBinaryReaderAsync("held.txt", null, EnvContext)).GetOrThrow();
            try
            {
                (await env.AppendFileAsync("held.txt", "!", EnvContext)).GetOrThrow();
                (await env.FlushFileAsync("held.txt", EnvContext)).GetOrThrow();
                (await env.TruncateFileAsync("held.txt", 3, EnvContext)).GetOrThrow();
                Equal("old", System.Text.Encoding.UTF8.GetString((await env.ReadBinaryFileAsync("held.txt", EnvContext)).GetOrThrow()), "truncated while held");
                var renamed = await env.RenameFileAsync("replacement.txt", "held.txt", EnvContext);
                if (OperatingSystem.IsWindows())
                {
                    // MoveFileEx, which Node's rename also uses, cannot replace a file another handle holds open: the rename fails
                    // cleanly and changes nothing (JSONL then keeps its temporary replacement for the next open to retry).
                    Check(!renamed.IsOk && renamed.Error.Code == FileErrorCode.PermissionDenied, "rename over a held file: " + renamed);
                    Equal("old", (await env.ReadTextFileAsync("held.txt", EnvContext)).GetOrThrow(), "unchanged while held");
                    Equal("new", (await env.ReadTextFileAsync("replacement.txt", EnvContext)).GetOrThrow(), "source kept");
                }
                else
                {
                    renamed.GetOrThrow();
                    Equal("new", (await env.ReadTextFileAsync("held.txt", EnvContext)).GetOrThrow(), "replaced while held");
                }
                Equal("old", System.Text.Encoding.UTF8.GetString((await reader.ReadAsync(0, 10, EnvContext)).GetOrThrow()), "the reader keeps its file");
            }
            finally { await reader.CloseAsync(EnvContext); }
            if (OperatingSystem.IsWindows()) (await env.RenameFileAsync("replacement.txt", "held.txt", EnvContext)).GetOrThrow();
            Equal("new", (await env.ReadTextFileAsync("held.txt", EnvContext)).GetOrThrow(), "replaced");
            (await env.RemoveAsync("held.txt", null, EnvContext)).GetOrThrow();
        }),
    ];

    private static (string, Func<Task>) EnvCase(string slug, Func<string, IFileSystem, Task> body) => Case("env.filesystem.local." + slug, async () =>
    {
        var root = Temp("env");
        try { await body(root, new LocalFileSystem(root)); }
        finally { DeleteDirectory(root); }
    });

    private static FileErrorCode? Code<T>(Result<T, FileError> result) => result.IsOk ? null : result.Error.Code;

    /// <summary>Creates the links, or returns false when this process may not create symbolic links.</summary>
    private static bool TryCreateSymlinks(string root, params (string Name, string Target, bool Directory)[] links)
    {
        try
        {
            foreach (var (name, target, directory) in links)
            {
                if (directory) Directory.CreateSymbolicLink(Path.Combine(root, name), target);
                else File.CreateSymbolicLink(Path.Combine(root, name), target);
            }
            return true;
        }
        catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
