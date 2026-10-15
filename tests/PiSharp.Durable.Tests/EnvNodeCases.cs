using System.Text;
using Microsoft.Win32.SafeHandles;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools.Env;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/env-node.test.ts, as env.node.<case>, on LocalExecutionEnv (upstream
// NodeExecutionEnv). Adapted:
// - Truncation sizes a long cannot hold (1.5, NaN, Infinity) are dropped.
// - Spies on Node's FileHandle become LocalFileSystem's internal seams: "syncs through an opened handle" overrides SyncFile, and "rejects a
//   read cancelled while pending" holds ReadTextChunkAsync on a gate.
// - Commands that upstream runs through `node -e` (split UTF-8, raw bytes, large output, a detached descendant holding stdio) use the shell's
//   printf, head and tr, so the cases need no Node; "settles after the shell exits when a detached descendant retains inherited stdio" runs on
//   every OS (upstream: Windows only), with a background `sleep` in the command's job or process group.
// - "uses stdin command transport for legacy WSL bash paths" needs no platform override: the WSL path pattern is recognized on every OS, so the
//   POSIX run creates a file of that name on PATH as upstream does. On Windows that name is the real inbox launcher, so it is skipped there,
//   as upstream skips it.
// - "ignores asynchronous taskkill spawn errors during abort" fakes win32 so Node's taskkill spawn fails; no taskkill exists here (jobs and
//   process groups are killed directly), so the case keeps its observable claim: an abort reports aborted although the command's process was
//   killed by another hand. POSIX only, as upstream ($$ is an MSYS pid under Git Bash).
// - "maps signal-killed processes to a non-zero exit code" is POSIX only, as upstream (Windows has no signals).
// Symbolic-link assertions run only where the process may create links (on Windows that needs Developer Mode or elevation).
// Authored: env.node.reads-output-written-before-the-exit-while-the-thread-pool-is-saturated (see the case), and
// env.node.replaces-and-truncates-files-that-other-handles-hold-open, which pins the sharing behaviour JSONL relies on: appends, flushes
// and truncation work while another handle (such as a binary reader) holds the file open; a rename over a held file replaces it on POSIX and
// fails cleanly on Windows, as Node's MoveFileEx-based rename does.
internal static partial class Program
{
    private static readonly DurableContext EnvContext = DurableContext.Background;

    private static IEnumerable<(string, Func<Task>)> EnvNodeCases() =>
    [
        // NodeExecutionEnv filesystem
        EnvCase("reads-writes-lists-and-removes-files-and-directories", async (root, env) =>
        {
            Equal(Path.Combine(root, "nested", "child"), (await env.AbsolutePathAsync("nested/child", EnvContext)).GetOrThrow(), "absolute path");
            Equal(Path.Combine(root, "nested", "child"), (await env.JoinPathAsync([root, "nested", "child"], EnvContext)).GetOrThrow(), "joined path");
            (await env.CreateDirAsync("nested/child", null, EnvContext)).GetOrThrow();
            (await env.WriteFileAsync("nested/child/file.txt", "hel", EnvContext)).GetOrThrow();
            (await env.AppendFileAsync("nested/child/file.txt", "lo", EnvContext)).GetOrThrow();
            Equal("hello", (await env.ReadTextFileAsync("nested/child/file.txt", EnvContext)).GetOrThrow(), "text");
            Equal("hello", string.Join("|", (await env.ReadTextLinesAsync("nested/child/file.txt", new ReadTextLinesOptions { MaxLines = 1 }, EnvContext)).GetOrThrow()),
                "lines");
            Equal("hello", Encoding.UTF8.GetString((await env.ReadBinaryFileAsync("nested/child/file.txt", EnvContext)).GetOrThrow()), "bytes");

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

        EnvCase("stops-reading-text-lines-at-the-requested-limit", async (root, env) =>
        {
            (await env.WriteFileAsync("file.txt", "one\ntwo\nthree", EnvContext)).GetOrThrow();
            Equal("one", string.Join("|", (await env.ReadTextLinesAsync("file.txt", new ReadTextLinesOptions { MaxLines = 1 }, EnvContext)).GetOrThrow()), "lines");
        }),

        EnvCase("returns-fileerror-for-missing-paths-and-keeps-exists-false-for-missing-paths", async (root, env) =>
        {
            var info = await env.FileInfoAsync("missing.txt", EnvContext);
            Check(!info.IsOk && info.Error is { Code: FileErrorCode.NotFound, CodeName: "not_found" } && info.Error.Path == Path.Combine(root, "missing.txt"),
                "missing info: " + info);
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

        EnvCase("creates-temporary-directories-and-files", async (root, env) =>
        {
            var tempDir = (await env.CreateTempDirAsync("node-env-test-", EnvContext)).GetOrThrow();
            try
            {
                Check(Directory.Exists(tempDir) && Path.GetFileName(tempDir).StartsWith("node-env-test-", StringComparison.Ordinal), "temporary directory " + tempDir);
                var tempFile = (await env.CreateTempFileAsync(new TempFileOptions { Prefix = "prefix-", Suffix = ".txt" }, EnvContext)).GetOrThrow();
                try
                {
                    Check(File.Exists(tempFile), "temporary file " + tempFile);
                    Check(tempFile.EndsWith(".txt", StringComparison.Ordinal) && Path.GetFileName(tempFile).StartsWith("prefix-", StringComparison.Ordinal), "name " + tempFile);
                    Equal("", (await env.ReadTextFileAsync(tempFile, EnvContext)).GetOrThrow(), "content");
                }
                finally { DeleteDirectory(Path.GetDirectoryName(tempFile)!); }
            }
            finally { DeleteDirectory(tempDir); }
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
            var context = AbortedEnvContext();
            var codes = new List<FileErrorCode?>
            {
                Code(await env.ReadTextFileAsync("file.txt", context)),
                Code(await env.ReadTextLinesAsync("file.txt", null, context)),
                Code(await env.ReadBinaryFileAsync("file.txt", context)),
                Code(await env.OpenTextLineReaderAsync("file.txt", context)),
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
                Code(await env.CreateTempDirAsync(null, context)),
                Code(await env.CreateTempFileAsync(null, context)),
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

        Case("env.node.syncs-through-an-opened-handle-returns-sync-failures-and-always-closes-the-handle", () => WithEnvRoot(async root =>
        {
            var env = new SyncSpyEnv(root);
            (await env.WriteFileAsync("file.txt", "durable", EnvContext)).GetOrThrow();
            (await env.FlushFileAsync("file.txt", EnvContext)).GetOrThrow();
            Equal(1, env.Synced.Count, "syncs");
            Check(env.Synced[0].SafeFileHandle.IsClosed, "the synced handle is closed");

            env.FailNext = true;
            var failed = await env.FlushFileAsync("file.txt", EnvContext);
            Check(!failed.IsOk && failed.Error is { Code: FileErrorCode.Unknown, Message: "EIO: i/o error, fsync" } && failed.Error.Path == Path.Combine(root, "file.txt"),
                "a failed sync: " + failed);
            Equal(2, env.Synced.Count, "syncs");
            Check(env.Synced[1].SafeFileHandle.IsClosed, "the failed handle is closed");
        })),

        EnvCase("cleanup-is-best-effort", async (root, env) => await env.CleanupAsync(EnvContext)),

        // NodeExecutionEnv text line reader
        EnvCase("reports-whether-each-line-was-newline-terminated-and-preserves-carriage-returns", async (root, env) =>
        {
            (await env.WriteFileAsync("lines.txt", "one\r\n\ntwo\npartial", EnvContext)).GetOrThrow();
            var reader = (await env.OpenTextLineReaderAsync("lines.txt", EnvContext)).GetOrThrow();
            try
            {
                var lines = new List<TextLine>();
                for (var line = (await reader.ReadLineAsync(EnvContext)).GetOrThrow(); line is not null; line = (await reader.ReadLineAsync(EnvContext)).GetOrThrow())
                    lines.Add(line);
                Equal("one\r:True|:True|two:True|partial:False", string.Join("|", lines.Select(line => $"{line.Text}:{line.Terminated}")), "lines");
                Equal<TextLine?>(null, (await reader.ReadLineAsync(EnvContext)).GetOrThrow(), "after the end");
            }
            finally { await reader.CloseAsync(EnvContext); }
        }),

        EnvCase("returns-no-lines-for-an-empty-file-and-one-terminated-empty-line-for-a-lone-newline", async (root, env) =>
        {
            (await env.WriteFileAsync("empty.txt", "", EnvContext)).GetOrThrow();
            (await env.WriteFileAsync("newline.txt", "\n", EnvContext)).GetOrThrow();
            var empty = (await env.OpenTextLineReaderAsync("empty.txt", EnvContext)).GetOrThrow();
            Equal<TextLine?>(null, (await empty.ReadLineAsync(EnvContext)).GetOrThrow(), "empty file");
            await empty.CloseAsync(EnvContext);
            var newline = (await env.OpenTextLineReaderAsync("newline.txt", EnvContext)).GetOrThrow();
            Equal<TextLine?>(new TextLine("", true), (await newline.ReadLineAsync(EnvContext)).GetOrThrow(), "lone newline");
            Equal<TextLine?>(null, (await newline.ReadLineAsync(EnvContext)).GetOrThrow(), "after it");
            await newline.CloseAsync(EnvContext);
        }),

        EnvCase("decodes-multi-byte-characters-split-across-read-chunks-and-lines-longer-than-one-chunk", async (root, env) =>
        {
            // The reader uses 64 KiB chunks; place a four-byte character across the first boundary.
            var first = new string('a', 64 * 1024 - 2) + "😀tail";
            var second = new string('é', 100_000);
            (await env.WriteFileAsync("large.txt", first + "\n" + second, EnvContext)).GetOrThrow();
            var reader = (await env.OpenTextLineReaderAsync("large.txt", EnvContext)).GetOrThrow();
            try
            {
                Equal<TextLine?>(new TextLine(first, true), (await reader.ReadLineAsync(EnvContext)).GetOrThrow(), "first line");
                Equal<TextLine?>(new TextLine(second, false), (await reader.ReadLineAsync(EnvContext)).GetOrThrow(), "second line");
                Equal<TextLine?>(null, (await reader.ReadLineAsync(EnvContext)).GetOrThrow(), "after the end");
            }
            finally { await reader.CloseAsync(EnvContext); }
        }),

        Case("env.node.rejects-a-read-cancelled-while-pending-without-consuming-its-bytes", () => WithEnvRoot(async root =>
        {
            var env = new GatedReadEnv(root);
            (await env.WriteFileAsync("lines.txt", "one\ntwo\n", EnvContext)).GetOrThrow();
            var reader = (await env.OpenTextLineReaderAsync("lines.txt", EnvContext)).GetOrThrow();
            try
            {
                var cancellation = EnvContext.WithCancel();
                var pending = reader.ReadLineAsync(cancellation.Context);
                await env.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancellation.Cancel();
                env.Release.TrySetResult();
                var aborted = await pending;
                Check(!aborted.IsOk && aborted.Error.Code == FileErrorCode.Aborted, "the pending read: " + aborted);
                Equal<TextLine?>(new TextLine("one", true), (await reader.ReadLineAsync(EnvContext)).GetOrThrow(), "first line");
                Equal<TextLine?>(new TextLine("two", true), (await reader.ReadLineAsync(EnvContext)).GetOrThrow(), "second line");
                Equal<TextLine?>(null, (await reader.ReadLineAsync(EnvContext)).GetOrThrow(), "after the end");
            }
            finally { await reader.CloseAsync(EnvContext); }
        })),

        EnvCase("rejects-reads-after-close-and-closes-idempotently", async (root, env) =>
        {
            (await env.WriteFileAsync("lines.txt", "one\n", EnvContext)).GetOrThrow();
            var reader = (await env.OpenTextLineReaderAsync("lines.txt", EnvContext)).GetOrThrow();
            await reader.CloseAsync(EnvContext);
            await reader.CloseAsync(EnvContext);
            var read = await reader.ReadLineAsync(EnvContext);
            Check(!read.IsOk && read.Error.Code == FileErrorCode.Invalid && read.Error.Path == Path.Combine(root, "lines.txt"), "read after close: " + read);
        }),

        EnvCase("reports-missing-files-when-opening-a-reader", async (root, env) =>
        {
            var opened = await env.OpenTextLineReaderAsync("missing.txt", EnvContext);
            Check(!opened.IsOk && opened.Error.Code == FileErrorCode.NotFound && opened.Error.Path == Path.Combine(root, "missing.txt"), "open: " + opened);
        }),

        // NodeExecutionEnv shell
        EnvCase("executes-commands-in-cwd-with-env-overrides", async (root, env) =>
        {
            var (result, output) = await CollectShellOutput(env, "printf '%s' \"$NODE_ENV_TEST\" > cwd-marker.txt; printf '%s:%s' \"$PWD\" \"$NODE_ENV_TEST\"",
                new ShellExecOptions { Env = new Dictionary<string, string> { ["NODE_ENV_TEST"] = "ok" } }, EnvContext);
            Equal(0, result.GetOrThrow().ExitCode, "exit code");
            Equal("ok", File.ReadAllText(Path.Combine(root, "cwd-marker.txt")), "marker");
            // Git Bash on Windows reports $PWD as an MSYS path (/tmp/...), not the Windows path.
            if (!OperatingSystem.IsWindows()) Equal((await env.CanonicalPathAsync(root, EnvContext)).GetOrThrow() + ":ok", output, "output");
        }),

        .. ShellEnvironmentOverrides(),

        EnvCase("can-replace-rather-than-inherit-the-default-shell-environment", async (root, _) =>
        {
            const string inheritedKey = "PI_NODE_ENV_INHERITED_TEST", configuredKey = "PI_NODE_ENV_CONFIGURED_TEST", explicitKey = "PI_NODE_ENV_EXPLICIT_TEST";
            var previous = Environment.GetEnvironmentVariable(inheritedKey);
            Environment.SetEnvironmentVariable(inheritedKey, "host");
            try
            {
                var env = new LocalExecutionEnv(new LocalExecutionEnvOptions { Cwd = root, ShellEnv = new Dictionary<string, string> { [configuredKey] = "configured" } });
                var (result, output) = await CollectShellOutput(env, $"printf '%s:%s:%s' \"${{{inheritedKey}-}}\" \"${{{configuredKey}-}}\" \"${{{explicitKey}-}}\"",
                    new ShellExecOptions { InheritEnv = false, Env = new Dictionary<string, string> { [explicitKey] = "explicit" } }, EnvContext);
                result.GetOrThrow();
                Equal("::explicit", output, "output");
            }
            finally { Environment.SetEnvironmentVariable(inheritedKey, previous); }
        }),

        EnvCase("uses-stdin-command-transport-for-legacy-wsl-bash-paths", async (root, env) =>
        {
            // On Windows this path is the real inbox WSL launcher (upstream skips the case there too).
            if (OperatingSystem.IsWindows()) return;
            const string shellPath = "C:\\Windows\\System32\\bash.exe";
            (await env.WriteFileAsync(shellPath, "#!/bin/sh\nprintf 'args:%s\\n' \"$*\" >&2\nexec /bin/bash \"$@\"\n", EnvContext)).GetOrThrow();
            File.SetUnixFileMode(Path.Combine(root, shellPath), (UnixFileMode)0b111_101_101);
            var originalCwd = Environment.CurrentDirectory;
            var originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.CurrentDirectory = root;
                Environment.SetEnvironmentVariable("PATH", root + Path.PathSeparator + originalPath);
                var wsl = new LocalExecutionEnv(new LocalExecutionEnvOptions { Cwd = root, ShellPath = shellPath });
                var (result, output) = await CollectShellOutput(wsl, "name='World'; echo \"Hello, ${name}!\"", null, EnvContext);
                Check(output.Contains("Hello, World!", StringComparison.Ordinal), "output: " + output);
                Check(output.Contains("args:-s", StringComparison.Ordinal), "output: " + output);
                Equal(0, result.GetOrThrow().ExitCode, "exit code");
            }
            finally
            {
                Environment.CurrentDirectory = originalCwd;
                Environment.SetEnvironmentVariable("PATH", originalPath);
            }
        }),

        EnvCase("settles-after-the-shell-exits-when-a-detached-descendant-retains-inherited-stdio", async (root, env) =>
        {
            var cancellation = EnvContext.WithCancel();
            try
            {
                var collected = CollectShellOutput(env, "sleep 60 & echo child-exiting", null, cancellation.Context);
                var (result, output) = await collected.WaitAsync(TimeSpan.FromSeconds(3));
                result.GetOrThrow();
                Check(output.Contains("child-exiting", StringComparison.Ordinal), "output: " + output);
            }
            finally { cancellation.Cancel(); }
        }),

        // Authored: output a command wrote before it exited is delivered even when the thread pool cannot start the reads promptly (on
        // macOS CI, two reads blocked by a hung command left a three-thread pool unable to read the next command's output within the
        // post-exit grace period).
        EnvCase("reads-output-written-before-the-exit-while-the-thread-pool-is-saturated", async (root, env) =>
        {
            using var release = new ManualResetEventSlim();
            var blockers = Enumerable.Range(0, Environment.ProcessorCount * 4)
                .Select(_ => Task.Factory.StartNew(() => release.Wait(TimeSpan.FromSeconds(30)), CancellationToken.None, TaskCreationOptions.None,
                    TaskScheduler.Default)).ToList();
            try
            {
                var (result, output) = await CollectShellOutput(env, "sleep 60 & echo child-exiting", null, EnvContext).WaitAsync(TimeSpan.FromSeconds(30));
                result.GetOrThrow();
                Check(output.Contains("child-exiting", StringComparison.Ordinal), "output: " + output);
            }
            finally
            {
                release.Set();
                await Task.WhenAll(blockers);
            }
        }),

        EnvCase("cleanup-terminates-active-shell-processes", async (root, env) =>
        {
            var execution = env.ExecAsync("touch started; sleep 60", null, EnvContext);
            try { await WaitForAsync(async () => (await env.ExistsAsync("started", EnvContext)).GetOrThrow(), 10_000); }
            finally { await env.CleanupAsync(EnvContext); }
            Check((await execution.WaitAsync(TimeSpan.FromSeconds(3))).IsOk, "the command settles successfully after cleanup");
        }),

        EnvCase("streams-combined-stdout-and-stderr", async (root, env) =>
        {
            var (result, output) = await CollectShellOutput(env, "printf out; printf err >&2", null, EnvContext);
            Equal(new ShellExecResult(0), result.GetOrThrow(), "result");
            Check(output.Contains("out", StringComparison.Ordinal) && output.Contains("err", StringComparison.Ordinal), "output: " + output);
        }),

        EnvCase("decodes-utf-8-split-across-raw-process-chunks", async (root, env) =>
        {
            var (_, output) = await CollectShellOutput(env, "printf '\\360\\237'; sleep 0.05; printf '\\230\\200'", null, EnvContext);
            Equal("😀", output, "output");
        }),

        Case("env.node.reports-a-missing-working-directory-before-spawning", () => WithEnvRoot(async root =>
        {
            var env = new LocalExecutionEnv(Path.Combine(root, "missing"));
            var result = await env.ExecAsync("printf ok", null, EnvContext);
            Check(!result.IsOk && result.Error.Code == ExecutionErrorCode.SpawnError && result.Error.Message.Contains("Working directory does not exist", StringComparison.Ordinal),
                "result: " + result);
        })),

        EnvCase("returns-non-zero-command-exit-codes-as-successful-execution-results", async (root, env) =>
            Equal(new ShellExecResult(7), (await env.ExecAsync("exit 7", null, EnvContext)).GetOrThrow(), "result")),

        EnvCase("maps-signal-killed-processes-to-a-non-zero-exit-code", async (root, env) =>
        {
            if (OperatingSystem.IsWindows()) return; // Windows has no signals (upstream skips it there).
            Equal(128 + 9, (await env.ExecAsync("kill -9 $$", null, EnvContext)).GetOrThrow().ExitCode, "exit code");
        }),

        EnvCase("returns-timeout-errors-for-commands-exceeding-the-timeout", async (root, env) =>
        {
            var result = await env.ExecAsync("sleep 5", new ShellExecOptions { Timeout = 0.01 }, EnvContext);
            Check(!result.IsOk && result.Error.Code == ExecutionErrorCode.Timeout, "result: " + result);
        }),

        EnvCase("rejects-invalid-timeouts-before-spawning", async (root, env) =>
        {
            foreach (var timeout in new[] { 0, -1, double.NaN, double.PositiveInfinity, 2_147_484 })
            {
                var result = await env.ExecAsync("touch spawned", new ShellExecOptions { Timeout = timeout }, EnvContext);
                Check(!result.IsOk && result.Error is { Code: ExecutionErrorCode.Timeout, CodeName: "timeout" } && result.Error.Message.Contains("Invalid timeout", StringComparison.Ordinal),
                    $"timeout {timeout}: {result}");
            }
            Equal(false, (await env.ExistsAsync("spawned", EnvContext)).GetOrThrow(), "spawned");
        }),

        EnvCase("returns-callback-errors-from-exec-stream-handlers", async (root, env) =>
        {
            var result = await env.ExecAsync("printf out", new ShellExecOptions { OnOutput = (_, _, _) => throw new InvalidOperationException("callback failed") }, EnvContext);
            Check(!result.IsOk && result.Error is { Code: ExecutionErrorCode.CallbackError, Message: "callback failed" }, "result: " + result);
        }),

        EnvCase("returns-shell-unavailable-and-spawn-errors", async (root, env) =>
        {
            var missingShell = await new LocalExecutionEnv(new LocalExecutionEnvOptions { Cwd = root, ShellPath = Path.Combine(root, "missing-shell") })
                .ExecAsync("printf ok", null, EnvContext);
            Check(!missingShell.IsOk && missingShell.Error is { Code: ExecutionErrorCode.ShellUnavailable, CodeName: "shell_unavailable" }, "missing shell: " + missingShell);

            var shellPath = Path.Combine(root, "not-executable-shell");
            (await env.WriteFileAsync(shellPath, "not executable", EnvContext)).GetOrThrow();
            var spawnError = await new LocalExecutionEnv(new LocalExecutionEnvOptions { Cwd = root, ShellPath = shellPath }).ExecAsync("printf ok", null, EnvContext);
            Check(!spawnError.IsOk && spawnError.Error is { Code: ExecutionErrorCode.SpawnError, CodeName: "spawn_error" }, "spawn error: " + spawnError);
        }),

        EnvCase("returns-an-aborted-result-for-pre-aborted-and-aborted-commands", async (root, env) =>
        {
            var preAborted = await env.ExecAsync("touch spawned", null, AbortedEnvContext());
            Check(!preAborted.IsOk && preAborted.Error.Code == ExecutionErrorCode.Aborted, "pre-aborted: " + preAborted);
            Equal(false, (await env.ExistsAsync("spawned", EnvContext)).GetOrThrow(), "spawned");

            var cancellation = EnvContext.WithCancel();
            var promise = env.ExecAsync("sleep 5", null, cancellation.Context);
            cancellation.Cancel();
            var result = await promise;
            Check(!result.IsOk && result.Error.Code == ExecutionErrorCode.Aborted, "aborted: " + result);
        }),

        EnvCase("ignores-asynchronous-taskkill-spawn-errors-during-abort", async (root, env) =>
        {
            if (OperatingSystem.IsWindows()) return; // As upstream: $$ names an MSYS process under Git Bash.
            var pidFile = Path.Combine(root, "shell.pid");
            var cancellation = EnvContext.WithCancel();
            var execution = env.ExecAsync($"echo $$ > '{pidFile}'; exec sleep 60", null, cancellation.Context);
            for (var attempt = 0; attempt < 100 && !File.Exists(pidFile); attempt++) await Task.Delay(10);
            Check(File.Exists(pidFile), "the shell wrote its pid");
            await WaitForAsync(() => Task.FromResult(File.ReadAllText(pidFile).Trim().Length > 0), 2000);
            cancellation.Cancel();
            try { System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture)).Kill(); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { } // Already gone.
            var result = await execution;
            Check(!result.IsOk && result.Error.Code == ExecutionErrorCode.Aborted, "result: " + result);
        }),

        EnvCase("does-not-create-a-spill-before-output-crosses-its-thresholds", async (root, env) =>
            Equal<string?>(null, (await env.ExecAsync("printf short", new ShellExecOptions { Spill = new ShellSpillOptions(100, 10) }, EnvContext)).GetOrThrow().SpillPath,
                "spill path")),

        EnvCase("preserves-exact-raw-bytes-in-the-spill-while-streaming-decoded-text", async (root, env) =>
        {
            var result = (await env.ExecAsync("printf 'f\\200\\000o'", new ShellExecOptions { Spill = new ShellSpillOptions(1, 10) }, EnvContext)).GetOrThrow();
            Check(result.SpillPath is not null, "a spill");
            try { Equal("6680006F", Convert.ToHexString((await env.ReadBinaryFileAsync(result.SpillPath!, EnvContext)).GetOrThrow()), "spilled bytes"); }
            finally { DeleteDirectory(Path.GetDirectoryName(result.SpillPath!)!); }
        }),

        EnvCase("reports-the-spill-of-a-command-that-times-out", async (root, env) =>
        {
            var result = await env.ExecAsync("printf 12345678901234567890; sleep 5",
                new ShellExecOptions { Timeout = 0.3, Spill = new ShellSpillOptions(10, 10) }, EnvContext);
            Check(!result.IsOk && result.Error.Code == ExecutionErrorCode.Timeout && result.Error.SpillPath is not null, "result: " + result);
            try { Equal("12345678901234567890", (await env.ReadTextFileAsync(result.Error!.SpillPath!, EnvContext)).GetOrThrow(), "spill"); }
            finally { DeleteDirectory(Path.GetDirectoryName(result.Error!.SpillPath!)!); }
        }),

        Case("env.node.fails-rather-than-silently-losing-a-requested-spill", () => WithEnvRoot(async root =>
        {
            var env = new FailingSpillEnv(root);
            var result = await env.ExecAsync("printf 12345678901234567890", new ShellExecOptions { Spill = new ShellSpillOptions(10, 10) }, EnvContext);
            Check(!result.IsOk && result.Error.Code == ExecutionErrorCode.Unknown &&
                result.Error.Message.Contains("Failed to preserve complete shell output", StringComparison.Ordinal), "result: " + result);
        })),

        EnvCase("preserves-complete-large-output-in-the-spill", async (root, env) =>
        {
            const int size = 500_000;
            var result = (await env.ExecAsync($"head -c {size} /dev/zero | tr '\\0' x", new ShellExecOptions { Spill = new ShellSpillOptions(10, 10) }, EnvContext))
                .GetOrThrow();
            Check(result.SpillPath is not null, "a spill");
            try { Equal(size, (await env.ReadTextFileAsync(result.SpillPath!, EnvContext)).GetOrThrow().Length, "spilled length"); }
            finally { DeleteDirectory(Path.GetDirectoryName(result.SpillPath!)!); }
        }),

        EnvCase("streams-every-line-and-spills-them-all-once-output-crosses-its-line-threshold", async (root, env) =>
        {
            var (collected, output) = await CollectShellOutput(env, "i=1; while [ $i -le 15000 ]; do echo line-$i; i=$((i+1)); done",
                new ShellExecOptions { Spill = new ShellSpillOptions(1024 * 1024, 100) }, EnvContext);
            var result = collected.GetOrThrow();
            Equal(string.Concat(Enumerable.Range(1, 15000).Select(index => $"line-{index}\n")), output, "output");
            Check(result.SpillPath is not null, "a spill");
            try
            {
                var spilled = (await env.ReadTextLinesAsync(result.SpillPath!, null, EnvContext)).GetOrThrow();
                Equal(15000, spilled.Length, "spilled lines");
                Equal("line-15000", spilled[^1], "last spilled line");
            }
            finally { DeleteDirectory(Path.GetDirectoryName(result.SpillPath!)!); }
        }),

        // Authored.
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
                Equal("old", Encoding.UTF8.GetString((await env.ReadBinaryFileAsync("held.txt", EnvContext)).GetOrThrow()), "truncated while held");
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
                Equal("old", Encoding.UTF8.GetString((await reader.ReadAsync(0, 10, EnvContext)).GetOrThrow()), "the reader keeps its file");
            }
            finally { await reader.CloseAsync(EnvContext); }
            if (OperatingSystem.IsWindows()) (await env.RenameFileAsync("replacement.txt", "held.txt", EnvContext)).GetOrThrow();
            Equal("new", (await env.ReadTextFileAsync("held.txt", EnvContext)).GetOrThrow(), "replaced");
            (await env.RemoveAsync("held.txt", null, EnvContext)).GetOrThrow();
        }),
    ];

    /// <summary>Upstream's it.each "applies string shell environment overrides when %s".</summary>
    private static IEnumerable<(string, Func<Task>)> ShellEnvironmentOverrides()
    {
        (string Slug, Dictionary<string, string>? Overrides, string Expected)[] rows =
        [
            ("a-missing-override-preserves-the-base-value", null, "x:/stale/parent.jsonl"),
            ("an-empty-override-shadows-the-base-value", new() { ["PI_SESSION_FILE"] = "" }, "x:"),
            ("a-string-override-replaces-the-base-value", new() { ["PI_SESSION_FILE"] = "/sessions/current.jsonl" }, "x:/sessions/current.jsonl"),
        ];
        foreach (var (slug, overrides, expected) in rows)
            yield return Case("env.node.applies-string-shell-environment-overrides-when-" + slug, () => WithEnvRoot(async root =>
            {
                var env = new LocalExecutionEnv(new LocalExecutionEnvOptions
                {
                    Cwd = root,
                    ShellEnv = new Dictionary<string, string>
                    {
                        ["PI_SESSION_FILE"] = "/stale/parent.jsonl",
                        ["PI_CODING_AGENT"] = "true",
                        ["PI_NODE_ENV_PRESERVED_TEST"] = "preserved",
                    },
                });
                var (result, output) = await CollectShellOutput(env,
                    "printf '%s:%s|%s|%s' \"${PI_SESSION_FILE+x}\" \"${PI_SESSION_FILE-}\" \"$PI_CODING_AGENT\" \"$PI_NODE_ENV_PRESERVED_TEST\"",
                    new ShellExecOptions { Env = overrides }, EnvContext);
                result.GetOrThrow();
                Equal($"{expected}|true|preserved", output, "output");
            }));
    }

    private static (string, Func<Task>) EnvCase(string slug, Func<string, LocalExecutionEnv, Task> body) =>
        Case("env.node." + slug, () => WithEnvRoot(root => body(root, new LocalExecutionEnv(root))));

    /// <summary>Runs <paramref name="body"/> in a fresh directory, then removes it. On Windows a killed command's processes can hold the
    /// directory for a moment after exec settles, so removal is retried.</summary>
    internal static async Task WithEnvRoot(Func<string, Task> body)
    {
        var root = Temp("env");
        try { await body(root); }
        finally { await RemoveTempDirAsync(root); }
    }

    internal static async Task RemoveTempDirAsync(string directory)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 80) { await Task.Delay(100); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        }
    }

    private static async Task<(Result<ShellExecResult, ExecutionError> Result, string Output)> CollectShellOutput(IExecutionEnv env, string command,
        ShellExecOptions? options, DurableContext context)
    {
        var output = new StringBuilder();
        var result = await env.ExecAsync(command, (options ?? new ShellExecOptions()) with { OnOutput = (text, _, _) => { lock (output) output.Append(text); } }, context);
        lock (output) return (result, output.ToString());
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"Condition not met within {timeoutMs}ms");
            await Task.Delay(10);
        }
    }

    private static DurableContext AbortedEnvContext()
    {
        var cancellation = DurableContext.Background.WithCancel();
        cancellation.Cancel();
        return cancellation.Context;
    }

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

    /// <summary>Upstream's spy on FileHandle.sync: records each synced stream and fails the next one on request.</summary>
    private sealed class SyncSpyEnv(string root) : LocalExecutionEnv(root)
    {
        public List<FileStream> Synced { get; } = [];
        public bool FailNext { get; set; }

        internal override void SyncFile(FileStream stream)
        {
            Synced.Add(stream);
            if (!FailNext)
            {
                base.SyncFile(stream);
                return;
            }
            FailNext = false;
            throw new IOException("EIO: i/o error, fsync");
        }
    }

    /// <summary>Upstream's one-shot spy on FileHandle.read: the first text-line read waits for <see cref="Release"/>.</summary>
    private sealed class GatedReadEnv(string root) : LocalExecutionEnv(root)
    {
        private int reads;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal override async ValueTask<int> ReadTextChunkAsync(SafeFileHandle handle, Memory<byte> buffer, long offset)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                Started.TrySetResult();
                await Release.Task;
            }
            return await base.ReadTextChunkAsync(handle, buffer, offset);
        }
    }

    /// <summary>Upstream's FailingSpillExecutionEnv: the spill file is in a directory that does not exist.</summary>
    private sealed class FailingSpillEnv(string root) : LocalExecutionEnv(root)
    {
        public override Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, DurableContext context) =>
            options?.Prefix == "pi-output-" ? Task.FromResult<Result<string, FileError>>(Path.Combine(Cwd, "missing", "spill.log"))
                : base.CreateTempFileAsync(options, context);
    }
}
