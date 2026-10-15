// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/testing/env-conformance.ts.
// Cases keep upstream's names, inputs and expected values. Adapted: upstream's read(0, 1.5) check is dropped because offsets and lengths are
// integers here; the line-scan case's reference decoding (`new TextDecoder()`) is this port's StreamDecoder and range decoder, which the
// env.line-scan cases pin against Node-captured output; upstream's `{ ...info }` partial matches compare the named properties; the listFiles
// cases' `{ entries, truncated }` comparisons compare both fields.
using System.Collections.Immutable;
using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;

namespace PiSharp.Durable.Testing;

/// <summary>The runner-independent <see cref="IExecutionEnv"/> conformance suite (upstream <c>createEnvConformance</c>).</summary>
public static class EnvConformance
{
    private static readonly DurableContext Context = DurableContext.Background;

    /// <summary>Creates the cases with default options. <paramref name="withEnv"/> must call and await its callback exactly once per case
    /// with an environment whose <see cref="IFileSystem.Cwd"/> is a fresh, empty, writable directory.</summary>
    public static IReadOnlyList<EnvConformanceCase> Create(EnvConformanceProvider withEnv) => Create(new EnvConformanceOptions { WithEnv = withEnv });

    /// <summary>Creates the cases.</summary>
    public static IReadOnlyList<EnvConformanceCase> Create(EnvConformanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.WithEnv);
        var assert = options.Assertions ?? ConformanceAssertions.Create(message => new EnvConformanceException(message));
        var shell = options.Shell.IsDefaultOrEmpty ? ["sh", "-c"] : options.Shell;
        EnvConformanceCase Case(string name, Func<IExecutionEnv, Task> test, int? timeoutMs = null) =>
            new(name, () => options.WithEnv(test)) { TimeoutMs = timeoutMs };
        // Watch cases wait up to three seconds per step, longer than test runners allow by default.
        EnvConformanceCase WatchCase(string name, Func<IExecutionEnv, Task> test) => Case(name, test, 30_000);
        ShellCommand Shell(params string[] rest) => ShellCommand.FromArgv([.. shell, .. rest]);

        var cases = new List<EnvConformanceCase>
        {
            Case("binary reader reads byte ranges of the opened file", async env =>
            {
                (await env.WriteFileAsync("data.txt", "hello world", Context)).GetOrThrow();
                var reader = (await env.OpenBinaryReaderAsync("data.txt", null, Context)).GetOrThrow();
                var info = (await reader.InfoAsync(Context)).GetOrThrow();
                assert.PartialDeepEqual(info, new { Name = "data.txt", Kind = FileKind.File, Size = 11L });
                assert.StrictEqual(Text((await reader.ReadAsync(0, 5, Context)).GetOrThrow()), "hello");
                assert.StrictEqual(Text((await reader.ReadAsync(6, 100, Context)).GetOrThrow()), "world");
                assert.StrictEqual((await reader.ReadAsync(11, 4, Context)).GetOrThrow().Length, 0);
                assert.StrictEqual((await reader.ReadAsync(50, 1, Context)).GetOrThrow().Length, 0);
                assert.StrictEqual((await reader.ReadAsync(3, 0, Context)).GetOrThrow().Length, 0);
                assert.StrictEqual(Code(await reader.ReadAsync(-1, 1, Context)), "invalid");
                assert.StrictEqual(Code(await reader.ReadAsync(0, 1, AbortedContext())), "aborted");
                await reader.CloseAsync(Context);
                await reader.CloseAsync(Context);
                assert.StrictEqual(Code(await reader.ReadAsync(0, 1, Context)), "invalid");
                assert.StrictEqual(Code(await reader.InfoAsync(Context)), "invalid");
            }),

            Case("binary reader scans lines like decoding the whole file", async env =>
            {
                // A byte-order mark, an invalid sequence before a newline, an empty line, a later U+FEFF, and no final newline.
                byte[] bytes = [0xef, 0xbb, 0xbf, 0x61, 0x0a, 0xe2, 0x82, 0x0a, 0x0a, 0xef, 0xbb, 0xbf, 0x62, 0x0a, 0xc3, 0xa9];
                (await env.WriteFileAsync("lines.txt", bytes, Context)).GetOrThrow();
                var lines = DecodeWhole(bytes).Split('\n');
                var reader = (await env.OpenBinaryReaderAsync("lines.txt", null, Context)).GetOrThrow();
                try
                {
                    (long Start, long? End)[] ranges = [(0, null), (0, 1), (1, 3), (2, 3), (3, null), (4, 9)];
                    foreach (var (startLine, endLine) in ranges)
                    {
                        var scan = (await reader.ScanLinesAsync(startLine, endLine, Context)).GetOrThrow();
                        var selected = lines[(int)Math.Min(startLine, lines.Length)..(int)Math.Min(endLine ?? lines.Length, lines.Length)];
                        string Range(long from, long to)
                        {
                            var slice = bytes.AsSpan((int)from, (int)(to - from));
                            return from > 0 ? DecodeRange(slice) : DecodeWhole(slice);
                        }
                        assert.StrictEqual(scan.Newlines, lines.Length - 1L);
                        assert.StrictEqual(Range(scan.Start, scan.End), string.Join('\n', selected));
                        assert.StrictEqual(scan.SelectedBytes, (long)Encoding.UTF8.GetByteCount(string.Join('\n', selected)));
                        assert.StrictEqual(Range(scan.Start, scan.FirstLineEnd), lines[(int)startLine]);
                        assert.StrictEqual(scan.FirstLineBytes, (long)Encoding.UTF8.GetByteCount(lines[(int)startLine]));
                    }
                    assert.PartialDeepEqual((await reader.ScanLinesAsync(9, null, Context)).GetOrThrow(),
                        new { Start = (long)bytes.Length, End = (long)bytes.Length, SelectedBytes = 0L });
                    assert.StrictEqual(Code(await reader.ScanLinesAsync(2, 2, Context)), "invalid");
                }
                finally { await reader.CloseAsync(Context); }
            }),

            Case("binary reader keeps reading the file it opened after a rename", async env =>
            {
                (await env.WriteFileAsync("a.txt", "one", Context)).GetOrThrow();
                var reader = (await env.OpenBinaryReaderAsync("a.txt", null, Context)).GetOrThrow();
                try
                {
                    (await env.RenameFileAsync("a.txt", "b.txt", Context)).GetOrThrow();
                    (await env.WriteFileAsync("a.txt", "two", Context)).GetOrThrow();
                    assert.StrictEqual(Text((await reader.ReadAsync(0, 10, Context)).GetOrThrow()), "one");
                }
                finally { await reader.CloseAsync(Context); }
            }),

            Case("binary reader refuses directories, missing files and aborted opens", async env =>
            {
                (await env.CreateDirAsync("dir", null, Context)).GetOrThrow();
                (await env.WriteFileAsync("file.txt", "x", Context)).GetOrThrow();
                assert.StrictEqual(Code(await env.OpenBinaryReaderAsync("dir", null, Context)), "is_directory");
                assert.StrictEqual(Code(await env.OpenBinaryReaderAsync("missing.txt", null, Context)), "not_found");
                assert.StrictEqual(Code(await env.OpenBinaryReaderAsync("file.txt", null, AbortedContext())), "aborted");
            }),

            Case("directory reader pages every entry exactly once", async env =>
            {
                string[] names = ["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"];
                foreach (var name in names) (await env.WriteFileAsync(name, name, Context)).GetOrThrow();
                (await env.CreateDirAsync("sub", null, Context)).GetOrThrow();
                var (pages, done) = await ReadAll(env, ".", 2);
                assert.Ok(done, "directory reader reached the end");
                foreach (var page in pages) assert.Ok(page.Count <= 2, "page within maxEntries");
                var entries = pages.SelectMany(page => page).ToList();
                assert.DeepEqual(entries.Select(entry => entry.Name).Order(StringComparer.Ordinal), names.Append("sub").Order(StringComparer.Ordinal));
                assert.StrictEqual(entries.FirstOrDefault(entry => entry.Name == "sub")?.Kind, FileKind.Directory);
                assert.PartialDeepEqual(entries.FirstOrDefault(entry => entry.Name == "a.txt"), new { Kind = FileKind.File, Size = 5L });
            }),

            Case("directory reader reports the end and refuses use after close", async env =>
            {
                (await env.CreateDirAsync("empty", null, Context)).GetOrThrow();
                var reader = (await env.OpenDirReaderAsync("empty", Context)).GetOrThrow();
                for (var call = 0; call < 2; call++)
                {
                    var page = (await reader.NextAsync(10, Context)).GetOrThrow();
                    assert.DeepEqual(page.Entries, Array.Empty<FileMetadata>());
                    assert.StrictEqual(page.Done, true);
                }
                assert.StrictEqual(Code(await reader.NextAsync(0, Context)), "invalid");
                assert.StrictEqual(Code(await reader.NextAsync(1, AbortedContext())), "aborted");
                await reader.CloseAsync(Context);
                await reader.CloseAsync(Context);
                assert.StrictEqual(Code(await reader.NextAsync(1, Context)), "invalid");
            }),

            Case("directory reader refuses missing paths and files", async env =>
            {
                (await env.WriteFileAsync("file.txt", "x", Context)).GetOrThrow();
                assert.StrictEqual(Code(await env.OpenDirReaderAsync("missing", Context)), "not_found");
                assert.StrictEqual(Code(await env.OpenDirReaderAsync("file.txt", Context)), "not_directory");
                assert.StrictEqual(Code(await env.OpenDirReaderAsync(".", AbortedContext())), "aborted");
            }),

            Case("directory reader skips entries removed during enumeration", async env =>
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
                    assert.DeepEqual(entries, Array.Empty<FileMetadata>());
                }
                finally { await reader.CloseAsync(Context); }
            }),

            WatchCase("watch reports a missing file's creation, changes, replacement and removal", env =>
                Watching(env, [new WatchTarget("AGENTS.md")], async watch =>
                {
                    await watch.ExpectChange("AGENTS.md", async () => (await env.WriteFileAsync("AGENTS.md", "one", Context)).GetOrThrow());
                    await watch.ExpectChange("AGENTS.md", async () => (await env.WriteFileAsync("AGENTS.md", "two!", Context)).GetOrThrow());
                    // Editors replace a file by renaming a new one over it.
                    await watch.ExpectChange("AGENTS.md", async () =>
                    {
                        (await env.WriteFileAsync("AGENTS.md.tmp", "three", Context)).GetOrThrow();
                        (await env.RenameFileAsync("AGENTS.md.tmp", "AGENTS.md", Context)).GetOrThrow();
                    });
                    await watch.ExpectChange("AGENTS.md", async () => (await env.WriteFileAsync("AGENTS.md", "four", Context)).GetOrThrow());
                    await watch.ExpectChange("AGENTS.md", async () => (await env.RemoveAsync("AGENTS.md", null, Context)).GetOrThrow());
                })),

            WatchCase("watch reports a missing target whose ancestors are created", env =>
                Watching(env, [new WatchTarget("a/b/c/AGENTS.md")], async watch =>
                    await watch.ExpectChange("a/b/c/AGENTS.md", async () => (await env.WriteFileAsync("a/b/c/AGENTS.md", "x", Context)).GetOrThrow()))),

            WatchCase("watch follows directories created together with their contents", async env =>
            {
                (await env.CreateDirAsync("skills", null, Context)).GetOrThrow();
                await Watching(env, [new WatchTarget("skills") { Recursive = true }], async watch =>
                {
                    // Written before any watcher on the new directories can exist.
                    await watch.ExpectChange("skills/a/b/SKILL.md", async () => (await env.WriteFileAsync("skills/a/b/SKILL.md", "one", Context)).GetOrThrow());
                    await watch.ExpectChange("skills/a/b/SKILL.md", async () => (await env.WriteFileAsync("skills/a/b/SKILL.md", "two!", Context)).GetOrThrow());
                    await watch.ExpectChange("skills/a/b/c/SKILL.md", async () => (await env.WriteFileAsync("skills/a/b/c/SKILL.md", "deeper", Context)).GetOrThrow());
                });
            }),

            WatchCase("watch keeps watching a path whose parent is renamed and recreated", async env =>
            {
                (await env.WriteFileAsync("proj/.pi/skills/x.md", "x", Context)).GetOrThrow();
                await Watching(env, [new WatchTarget("proj/.pi/skills") { Recursive = true }], async watch =>
                {
                    await watch.ExpectChange("proj/.pi/skills", async () => (await env.RenameFileAsync("proj/.pi", "proj/old", Context)).GetOrThrow());
                    await watch.ExpectChange("proj/.pi/skills/y.md", async () => (await env.WriteFileAsync("proj/.pi/skills/y.md", "y", Context)).GetOrThrow());
                    await watch.ExpectChange("proj/.pi/skills/y.md", async () => (await env.WriteFileAsync("proj/.pi/skills/y.md", "yy", Context)).GetOrThrow());
                });
            }),

            WatchCase("watch skips excluded entries and reports a rename out of them", async env =>
            {
                (await env.CreateDirAsync("skills", null, Context)).GetOrThrow();
                WatchTarget[] targets = [new("skills") { Recursive = true, Exclude = new WatchExclusions { Hidden = true, Names = ["node_modules"] } }];
                await Watching(env, targets, async watch =>
                {
                    (await env.WriteFileAsync("skills/node_modules/dep/SKILL.md", "dep", Context)).GetOrThrow();
                    (await env.WriteFileAsync("skills/.SKILL.md.tmp", "draft", Context)).GetOrThrow();
                    await watch.ExpectChange("skills/SKILL.md", async () => (await env.RenameFileAsync("skills/.SKILL.md.tmp", "skills/SKILL.md", Context)).GetOrThrow());
                    string[] hidden = [await watch.Absolute("skills/node_modules"), await watch.Absolute("skills/.SKILL.md.tmp")];
                    foreach (var change in watch.Changes)
                    {
                        if (change is not WatchPathsChange paths) continue;
                        foreach (var path in paths.Paths)
                            assert.Ok(!hidden.Any(excluded => path == excluded || path.StartsWith(excluded, StringComparison.Ordinal)), $"excluded {path}");
                    }
                });
            }),

            WatchCase("watch keeps recursive coverage where a non-recursive target overlaps", async env =>
            {
                (await env.WriteFileAsync("skills/a/one.md", "one", Context)).GetOrThrow();
                await Watching(env, [new WatchTarget("skills"), new WatchTarget("skills") { Recursive = true }], async watch =>
                    await watch.ExpectChange("skills/a/two.md", async () => (await env.WriteFileAsync("skills/a/two.md", "two", Context)).GetOrThrow()));
            }),

            WatchCase("watch follows a directory replaced at the same path", async env =>
            {
                (await env.WriteFileAsync("skills/a/x.md", "x", Context)).GetOrThrow();
                await Watching(env, [new WatchTarget("skills") { Recursive = true }], async watch =>
                {
                    await watch.ExpectChange("skills/a", async () =>
                    {
                        (await env.RenameFileAsync("skills/a", "skills-old", Context)).GetOrThrow();
                        (await env.CreateDirAsync("skills/a", null, Context)).GetOrThrow();
                    });
                    await watch.ExpectChange("skills/a/y.md", async () => (await env.WriteFileAsync("skills/a/y.md", "y", Context)).GetOrThrow());
                    await watch.ExpectChange("skills/a/y.md", async () => (await env.WriteFileAsync("skills/a/y.md", "yy", Context)).GetOrThrow());
                });
            }),

            WatchCase("watch stops reporting once closed", async env =>
            {
                var changes = new List<WatchChange>();
                var watcher = (await env.WatchAsync([new WatchTarget("file.txt")], change => { lock (changes) changes.Add(change); }, Context)).GetOrThrow();
                assert.Ok(watcher.Mode is WatchMode.Native or WatchMode.Polling, "watcher reports its mode");
                await watcher.CloseAsync(Context);
                await watcher.CloseAsync(Context);
                (await env.WriteFileAsync("file.txt", "x", Context)).GetOrThrow();
                await Task.Delay(300);
                lock (changes) assert.DeepEqual(changes, Array.Empty<WatchChange>());
            }),

            Case("argv exec passes arguments to the program without shell parsing", async env =>
            {
                const string hostile = "it's $(touch pwned) `touch pwned` *; touch pwned";
                var (result, stdout, _) = await ExecCollect(env, Shell("printf \"%s|%s\" \"$1\" \"$2\"", "argv0", hostile, "a b"));
                assert.StrictEqual(result.GetOrThrow().ExitCode, 0);
                assert.StrictEqual(stdout, $"{hostile}|a b");
                assert.StrictEqual((await env.ExistsAsync("pwned", Context)).GetOrThrow(), false);
            }),

            Case("exec reports the stream of every chunk in both forms", async env =>
            {
                const string script = "printf out; printf err >&2; printf more";
                var argv = await ExecCollect(env, Shell(script));
                assert.StrictEqual(argv.Result.GetOrThrow().ExitCode, 0);
                assert.StrictEqual(argv.Stdout, "outmore");
                assert.StrictEqual(argv.Stderr, "err");
                var text = await ExecCollect(env, script);
                assert.StrictEqual(text.Result.GetOrThrow().ExitCode, 0);
                assert.StrictEqual(text.Stdout, "outmore");
                assert.StrictEqual(text.Stderr, "err");
            }),

            Case("argv exec honors cwd and exit codes", async env =>
            {
                (await env.CreateDirAsync("sub", null, Context)).GetOrThrow();
                var made = await ExecCollect(env, Shell("printf x > made.txt; exit 3"), "sub");
                assert.StrictEqual(made.Result.GetOrThrow().ExitCode, 3);
                assert.StrictEqual((await env.ReadTextFileAsync("sub/made.txt", Context)).GetOrThrow(), "x");
            }),

            Case("argv exec reports missing programs and empty argv as spawn errors", async env =>
            {
                assert.StrictEqual(Code(await env.ExecAsync(ShellCommand.FromArgv(["pi-durable-conformance-missing-program"]), null, Context)), "spawn_error");
                assert.StrictEqual(Code(await env.ExecAsync(ShellCommand.FromArgv([]), null, Context)), "spawn_error");
            }),

            Case("windowed exec keeps the exact tail and counts what it skips", async env =>
            {
                const int lines = 2000;
                var window = new ShellOutputWindow(MaxBytes: 200, MaxLines: 5, MinIntervalMs: 0, BytesPerSecond: 1_000_000_000);
                long bytes = 0, newlines = 0;
                var tail = new StringBuilder();
                var result = await env.ExecAsync(Shell($"i=0; while [ $i -lt {lines} ]; do echo line-$i; i=$((i+1)); done"), new ShellExecOptions
                {
                    Window = window,
                    OnOutput = (text, _, info) =>
                    {
                        if (info.Skipped is { } skipped)
                        {
                            bytes += skipped.Bytes;
                            newlines += skipped.Newlines;
                            var after = Encoding.UTF8.GetByteCount(text);
                            var afterNewlines = text.Count(character => character == '\n');
                            assert.Ok(after > window.MaxBytes || afterNewlines > window.MaxLines, "a skip is followed by more than the window");
                            tail.Clear();
                        }
                        bytes += Encoding.UTF8.GetByteCount(text);
                        newlines += text.Count(character => character == '\n');
                        tail.Append(text);
                    },
                }, Context);
                assert.StrictEqual(result.GetOrThrow().ExitCode, 0);
                var expected = Enumerable.Range(0, lines).Select(index => $"line-{index}\n").ToList();
                assert.StrictEqual(bytes, (long)string.Concat(expected).Length);
                assert.StrictEqual(newlines, (long)lines);
                assert.Ok(tail.ToString().EndsWith(string.Concat(expected.TakeLast((int)window.MaxLines)), StringComparison.Ordinal),
                    "the delivered output ends with the tail");
            }),

            Case("argv exec distinguishes timeout from abort", async env =>
            {
                var timedOut = await env.ExecAsync(Shell("sleep 2"), new ShellExecOptions { Timeout = 0.1 }, Context);
                assert.StrictEqual(Code(timedOut), "timeout");
                var cancellation = Context.WithCancel();
                var running = env.ExecAsync(Shell("sleep 2"), null, cancellation.Context);
                _ = Task.Delay(100).ContinueWith(_ => cancellation.Cancel(), TaskScheduler.Default);
                assert.StrictEqual(Code(await running), "aborted");
            }),
        };

        if (options.ListFiles)
        {
            async Task WriteAll(IExecutionEnv env, params string[] files)
            {
                foreach (var file in files) (await env.WriteFileAsync(file, "", Context)).GetOrThrow();
            }

            cases.Add(Case("listFiles lists files and directories depth first in name order", async env =>
            {
                await WriteAll(env, "b.txt", "a/z.txt", "a/b/c.txt", ".hidden/x", "A.txt");
                (await env.CreateDirAsync("empty", null, Context)).GetOrThrow();
                var all = await Listed(env, ".");
                assert.DeepEqual(all.Entries, new[]
                {
                    "d .hidden",
                    "f .hidden/x",
                    "f A.txt",
                    "d a",
                    "d a/b",
                    "f a/b/c.txt",
                    "f a/z.txt",
                    "f b.txt",
                    "d empty",
                });
                assert.StrictEqual(all.Truncated, false);
                assert.DeepEqual((await Listed(env, ".", new ListFilesOptions { Hidden = false })).Entries, new[]
                {
                    "f A.txt",
                    "d a",
                    "d a/b",
                    "f a/b/c.txt",
                    "f a/z.txt",
                    "f b.txt",
                    "d empty",
                });
                string[] expected = ["d b", "f b/c.txt", "f z.txt"];
                var relative = await Listed(env, "a");
                assert.DeepEqual(relative.Entries, expected);
                assert.StrictEqual(relative.Truncated, false);
                var absolute = await Listed(env, (await env.AbsolutePathAsync("a", Context)).GetOrThrow());
                assert.DeepEqual(absolute.Entries, expected);
                assert.StrictEqual(absolute.Truncated, false);
                var empty = await Listed(env, "empty");
                assert.DeepEqual(empty.Entries, Array.Empty<string>());
                assert.StrictEqual(empty.Truncated, false);
            }));

            cases.Add(Case("listFiles skips what git ignores in work trees, .git, and excluded names", async env =>
            {
                await WriteAll(env, "keep.txt", "out/x.js", "sub/keep.txt", "sub/drop.log", "node_modules/m/i.js");
                (await env.WriteFileAsync(".gitignore", "out/\n*.log\n", Context)).GetOrThrow();
                (await env.WriteFileAsync("sub/.gitignore", "!keep.txt\n", Context)).GetOrThrow();
                // Not a work tree yet: `.gitignore` files have no effect.
                assert.Ok((await Listed(env, ".")).Entries.Contains("f out/x.js"), "outside a work tree out/x.js is listed");
                (await env.WriteFileAsync(".git/HEAD", "ref: refs/heads/main\n", Context)).GetOrThrow();
                (await env.WriteFileAsync(".git/info/exclude", "keep.txt\n", Context)).GetOrThrow();
                assert.DeepEqual((await Listed(env, ".", new ListFilesOptions { Exclude = ["node_modules"] })).Entries, new[]
                {
                    "f .gitignore",
                    "d sub",
                    "f sub/.gitignore",
                    "f sub/keep.txt",
                });
                // A directory inside the work tree applies its parents' rules.
                assert.DeepEqual((await Listed(env, "sub")).Entries, new[] { "f .gitignore", "f keep.txt" });
                var all = (await Listed(env, ".", new ListFilesOptions { Gitignore = false })).Entries;
                assert.Ok(all.Contains("f out/x.js") && all.Contains("f node_modules/m/i.js") && all.Contains("f keep.txt"),
                    "without gitignore everything is listed");
                assert.Ok(!all.Any(entry => entry.Contains(".git/", StringComparison.Ordinal) || entry == "d .git"), ".git is never listed");
            }));

            cases.Add(Case("listFiles stops at its bounds and reports truncated", async env =>
            {
                await WriteAll(env, "a", "b", "c");
                var two = await Listed(env, ".", new ListFilesOptions { MaxEntries = 2 });
                assert.DeepEqual(two.Entries, new[] { "f a", "f b" });
                assert.StrictEqual(two.Truncated, true);
                var three = await Listed(env, ".", new ListFilesOptions { MaxEntries = 3 });
                assert.DeepEqual(three.Entries, new[] { "f a", "f b", "f c" });
                assert.StrictEqual(three.Truncated, false);
                // Each entry counts its path plus two bytes.
                var bytes = await Listed(env, ".", new ListFilesOptions { MaxBytes = 6 });
                assert.DeepEqual(bytes.Entries, new[] { "f a", "f b" });
                assert.StrictEqual(bytes.Truncated, true);
                var none = await Listed(env, ".", new ListFilesOptions { MaxEntries = 0 });
                assert.DeepEqual(none.Entries, Array.Empty<string>());
                assert.StrictEqual(none.Truncated, true);
                assert.StrictEqual(Code(await env.ListFilesAsync(".", new ListFilesOptions { MaxEntries = -1 }, Context)), "invalid");
                assert.StrictEqual(Code(await env.ListFilesAsync(".", new ListFilesOptions { TimeoutMs = 1.5 }, Context)), "invalid");
            }));

            cases.Add(Case("listFiles fails on missing directories, files, and aborts", async env =>
            {
                (await env.WriteFileAsync("file.txt", "", Context)).GetOrThrow();
                assert.StrictEqual(Code(await env.ListFilesAsync("missing", null, Context)), "not_found");
                assert.StrictEqual(Code(await env.ListFilesAsync("file.txt", null, Context)), "not_directory");
                assert.StrictEqual(Code(await env.ListFilesAsync(".", null, AbortedContext())), "aborted");
            }));
        }

        if (options.Symlinks && options.ListFiles)
        {
            cases.Add(Case("listFiles lists symbolic links without following them", async env =>
            {
                (await env.WriteFileAsync("dir/inner.txt", "", Context)).GetOrThrow();
                var linked = await env.ExecAsync(Shell("ln -s dir link && ln -s missing dangling && ln -s .. dir/loop"), null, Context);
                assert.StrictEqual(linked.GetOrThrow().ExitCode, 0);
                assert.DeepEqual((await Listed(env, ".")).Entries, new[]
                {
                    "l dangling",
                    "d dir",
                    "f dir/inner.txt",
                    "l dir/loop",
                    "l link",
                });
                // The listed directory itself may be a link.
                assert.DeepEqual((await Listed(env, "link")).Entries, new[] { "f inner.txt", "l loop" });
            }));
        }

        if (options.Symlinks)
        {
            cases.Add(Case("binary reader follows symlinks unless noFollow refuses the final one", async env =>
            {
                (await env.WriteFileAsync("target.txt", "target", Context)).GetOrThrow();
                (await env.CreateDirAsync("sub", null, Context)).GetOrThrow();
                (await env.WriteFileAsync("sub/inner.txt", "inner", Context)).GetOrThrow();
                var linked = await env.ExecAsync(Shell("ln -s target.txt link.txt && ln -s sub dirlink"), null, Context);
                assert.StrictEqual(linked.GetOrThrow().ExitCode, 0);

                var followed = (await env.OpenBinaryReaderAsync("link.txt", null, Context)).GetOrThrow();
                assert.StrictEqual(Text((await followed.ReadAsync(0, 10, Context)).GetOrThrow()), "target");
                await followed.CloseAsync(Context);

                assert.StrictEqual(Code(await env.OpenBinaryReaderAsync("link.txt", new BinaryReaderOptions { NoFollow = true }, Context)), "invalid");

                // Only the final component is refused; earlier symlinked directories still resolve.
                var inner = (await env.OpenBinaryReaderAsync("dirlink/inner.txt", new BinaryReaderOptions { NoFollow = true }, Context)).GetOrThrow();
                assert.StrictEqual(Text((await inner.ReadAsync(0, 10, Context)).GetOrThrow()), "inner");
                await inner.CloseAsync(Context);
            }));

            cases.Add(WatchCase("watch reports changes to the file a watched symbolic link points to", async env =>
            {
                (await env.WriteFileAsync("data/real.md", "one", Context)).GetOrThrow();
                (await env.CreateDirAsync("config", null, Context)).GetOrThrow();
                var linked = await env.ExecAsync(Shell("ln -s ../data/real.md config/AGENTS.md"), null, Context);
                assert.StrictEqual(linked.GetOrThrow().ExitCode, 0);
                await Watching(env, [new WatchTarget("config/AGENTS.md")], async watch =>
                    await watch.ExpectChange("config/AGENTS.md", async () => (await env.WriteFileAsync("data/real.md", "two!", Context)).GetOrThrow()));
            }));
        }

        return cases;

        async Task<(Result<ShellExecResult, ExecutionError> Result, string Stdout, string Stderr)> ExecCollect(IExecutionEnv env, ShellCommand command, string? cwd = null)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var result = await env.ExecAsync(command, new ShellExecOptions
            {
                Cwd = cwd,
                OnOutput = (text, _, info) => (info.Stream == ShellStream.Stdout ? stdout : stderr).Append(text),
            }, Context);
            return (result, stdout.ToString(), stderr.ToString());
        }
    }

    /// <summary>A listing as <c>&lt;kind letter&gt; &lt;path&gt;</c> lines (<c>f</c>, <c>d</c>, <c>l</c>), with its truncation (upstream
    /// <c>listed</c>). An environment without <see cref="IFileSystem.ListFilesAsync"/> fails with not_supported, which this throws.</summary>
    private static async Task<(string[] Entries, bool Truncated)> Listed(IExecutionEnv env, string path, ListFilesOptions? options = null)
    {
        var listing = (await env.ListFilesAsync(path, options, Context)).GetOrThrow();
        var entries = listing.Entries.Select(entry => (entry.Kind switch { FileKind.File => "f", FileKind.Directory => "d", _ => "l" }) + " " + entry.Path);
        return ([.. entries], listing.Truncated);
    }

    /// <summary>The change helpers of upstream's <c>watching</c>.</summary>
    private sealed class Watch(IExecutionEnv env)
    {
        private readonly List<WatchChange> changes = [];

        public IReadOnlyList<WatchChange> Changes { get { lock (changes) return [.. changes]; } }

        public void Add(WatchChange change) { lock (changes) changes.Add(change); }

        public async Task<string> Absolute(string path) => (await env.AbsolutePathAsync(path, Context)).GetOrThrow();

        /// <summary>Waits up to three seconds for a change, after the call, that reports <paramref name="path"/>.</summary>
        public async Task ExpectChange(string path, Func<Task> change)
        {
            var target = await Absolute(path);
            int from;
            lock (changes) from = changes.Count;
            await change();
            var deadline = Environment.TickCount64 + 3000;
            while (true)
            {
                List<WatchChange> since;
                lock (changes) since = changes.Skip(from).ToList();
                if (since.Any(entry => Covers(entry, target))) return;
                if (Environment.TickCount64 > deadline)
                    throw new EnvConformanceException($"No change reported {target}; got {string.Join(", ", since.Select(Describe))}");
                await Task.Delay(20);
            }
        }
    }

    /// <summary>Watches <paramref name="targets"/> while <paramref name="run"/> changes files.</summary>
    private static async Task Watching(IExecutionEnv env, IReadOnlyList<WatchTarget> targets, Func<Watch, Task> run)
    {
        var watch = new Watch(env);
        var watcher = (await env.WatchAsync(targets, watch.Add, Context)).GetOrThrow();
        try { await run(watch); }
        finally { await watcher.CloseAsync(Context); }
    }

    /// <summary>Whether a change reports <paramref name="path"/>: an overflow, or a reported path at or above it.</summary>
    private static bool Covers(WatchChange change, string path) => change switch
    {
        WatchOverflowChange => true,
        WatchPathsChange paths => paths.Paths.Any(reported =>
            path == reported || path.StartsWith(reported + "/", StringComparison.Ordinal) || path.StartsWith(reported + "\\", StringComparison.Ordinal)),
        _ => false,
    };

    private static string Describe(WatchChange change) => change switch
    {
        WatchPathsChange paths => "[" + string.Join(", ", paths.Paths) + "]",
        WatchErrorChange error => "error: " + error.Error.Message,
        _ => "overflow",
    };

    private static async Task<(List<IReadOnlyList<FileMetadata>> Pages, bool Done)> ReadAll(IExecutionEnv env, string path, int maxEntries)
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

    /// <summary><c>new TextDecoder().decode(bytes)</c>: a leading byte-order mark is dropped.</summary>
    private static string DecodeWhole(ReadOnlySpan<byte> bytes)
    {
        var decoder = new StreamDecoder();
        return decoder.Decode(bytes) + decoder.Finish();
    }

    /// <summary><c>new TextDecoder("utf-8", { ignoreBOM: true }).decode(bytes)</c>: a byte-order mark is kept.</summary>
    private static string DecodeRange(ReadOnlySpan<byte> bytes) => Utf8Decoding.Decode(Utf8Decoding.RangeDecoder(), bytes, flush: true);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static string? Code<T>(Result<T, FileError> result) => result.IsOk ? null : result.Error.CodeName;

    private static string? Code<T>(Result<T, ExecutionError> result) => result.IsOk ? null : result.Error.CodeName;
}
