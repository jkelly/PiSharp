using System.Diagnostics;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;
using PiSharp.Durable.Tools.Env;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/env/daemon/src/list.rs (its unit tests, as env.list-files.daemon.<case>),
// packages/env/test/remote.test.ts ("cancels a listing in the daemon", as env.list-files.remote.<case>) and packages/env/test/ssh.test.ts ("lists
// files over SSH as the local environment lists them", as env.list-files.ssh.<case>), on LocalFileSystem's listFiles (LocalFileListing.cs), which
// ports the daemon's walk; the env conformance kit's listFiles cases run in EnvNodeConformanceCases.cs. Adapted:
// - The daemon's symbolic link test is Unix only upstream; Windows creates symbolic links only with a privilege, so there it links with
//   junctions, which Rust (and this port) lists as links too.
// - The daemon cancels a running walk when the request is cancelled; here the walk's abort callback is true from the start, bypassing
//   ListFilesAsync's own check before the walk, as the upstream test bypasses the client's.
// env.list-files.gitignore.<case> are the gitignore tests of the `ignore` crate 0.4.33 (BurntSushi/ripgrep crates/ignore/src/gitignore.rs at
// 3fce3b5bb0236da2df6d99672afb8a719642eca7; Unlicense/MIT), on GitignoreMatcher; the crate's case-insensitive builder test is left out because
// listFiles never matches case-insensitively. env.list-files.walk.<case> are authored: each listing's set of paths was checked against
// fd 10.4.2 (`fd --hidden --exclude .git --no-follow`, the same `ignore` crate) on Windows; orders follow list.rs (depth first, byte order).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> EnvListFilesCases()
    {
        var context = DurableContext.Background;

        // list.rs tests.
        yield return ListCase("daemon.lists-files-and-directories-depth-first-in-name-order", root =>
        {
            Tree(root, "b.txt", "a/z.txt", "a/b/c.txt", "empty/", ".hidden/x", "A.txt");
            var (entries, _, truncated) = List(root);
            Equal(Lines("d .hidden", "f .hidden/x", "f A.txt", "d a", "d a/b", "f a/b/c.txt", "f a/z.txt", "f b.txt", "d empty"), entries, "entries");
            Equal(false, truncated, "truncated");
            Check(!List(root, new ListFilesOptions { Hidden = false }).Entries.Contains(".hidden"), "hidden entries are skipped");
        });

        yield return ListCase("daemon.skips-what-git-ignores-only-in-work-trees", root =>
        {
            Tree(root, "keep.txt", "out/x.js", "sub/keep.txt", "sub/drop.log", "node_modules/m/i.js");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "out/\n*.log\n");
            File.WriteAllText(Path.Combine(root, "sub", ".gitignore"), "!keep.txt\n");
            // Not a work tree yet: everything is listed.
            Check(List(root).Entries.Contains("f out/x.js"), "outside a work tree out/x.js is listed");
            Tree(root, ".git/HEAD", ".git/info/exclude");
            File.WriteAllText(Path.Combine(root, ".git", "info", "exclude"), "keep.txt\n");
            Equal(Lines("f .gitignore", "d sub", "f sub/.gitignore", "f sub/keep.txt"),
                List(root, new ListFilesOptions { Exclude = ["node_modules"] }).Entries, "work tree entries");
            var all = List(root, new ListFilesOptions { Gitignore = false }).Entries;
            Check(all.Contains("f out/x.js"), "without gitignore out/x.js is listed");
            Check(!all.Contains(".git/"), ".git is never listed");
            // A subdirectory of a work tree applies the rules of its parents.
            Equal(Lines("f .gitignore", "f keep.txt"), List(Path.Combine(root, "sub")).Entries, "sub entries");
        });

        yield return ListCase("daemon.lists-symbolic-links-without-following-them", root =>
        {
            Tree(root, "dir/inner.txt");
            Link(Path.Combine(root, "link"), Path.Combine(root, "dir"));
            DanglingLink(Path.Combine(root, "dangling"), Path.Combine(root, "missing"));
            Link(Path.Combine(root, "dir", "loop"), root);
            Equal(Lines("l dangling", "d dir", "f dir/inner.txt", "l dir/loop", "l link"), List(root).Entries, "entries");
            // The listed directory itself may be a link.
            Equal(Lines("f inner.txt", "l loop"), List(Path.Combine(root, "link")).Entries, "entries of the link");
        });

        yield return ListCase("daemon.stops-at-bounds-with-truncated", root =>
        {
            Tree(root, "a", "b", "c");
            var two = List(root, new ListFilesOptions { MaxEntries = 2 });
            Equal((2, true), (two.Lines.Length, two.Truncated), "two entries");
            var three = List(root, new ListFilesOptions { MaxEntries = 3 });
            Equal((3, false), (three.Lines.Length, three.Truncated), "three entries");
            var bytes = List(root, new ListFilesOptions { MaxBytes = 6 });
            Equal(Lines("f a", "f b"), bytes.Entries, "entries within six bytes");
            Equal(true, bytes.Truncated, "truncated by bytes");
            var timedOut = List(root, new ListFilesOptions { TimeoutMs = 0 });
            Check(timedOut.Truncated && timedOut.Lines.Length == 0, "a zero timeout lists nothing and is truncated");
        });

        yield return ListCase("daemon.fails-on-missing-roots-files-and-aborts", root =>
        {
            Tree(root, "file");
            Equal(FileErrorCode.NotFound, Failure(Path.Combine(root, "missing")).Code, "missing root");
            Equal(FileErrorCode.NotDirectory, Failure(Path.Combine(root, "file")).Code, "file root");
            Equal(FileErrorCode.Aborted, Failure(root, aborted: true).Code, "aborted walk");
        });

        // remote.test.ts: an abort stops the walk itself, and large listings are complete or bounded.
        yield return Case("env.list-files.remote.cancels-a-listing", () => WithEnvRoot(async root =>
        {
            for (var dir = 0; dir < 100; dir++)
            {
                Directory.CreateDirectory(Path.Combine(root, $"d{dir}"));
                for (var file = 0; file < 100; file++) File.WriteAllBytes(Path.Combine(root, $"d{dir}", $"f{file}"), []);
            }
            var calls = 0;
            var cancelled = LocalFileListing.List(root, null, () => ++calls > 1);
            Check(!cancelled.IsOk && cancelled.Error.Code == FileErrorCode.Aborted, "a walk aborted while running fails with aborted: " + cancelled);
            var env = new LocalExecutionEnv(root);
            var listed = (await env.ListFilesAsync(".", null, context)).GetOrThrow();
            Equal((10_100, false), (listed.Entries.Length, listed.Truncated), "the whole tree");
            var bounded = (await env.ListFilesAsync(".", new ListFilesOptions { MaxEntries = 5000 }, context)).GetOrThrow();
            Equal((5000, true), (bounded.Entries.Length, bounded.Truncated), "a bounded listing");
            var cancellation = context.WithCancel();
            var running = env.ListFilesAsync(".", null, cancellation.Context);
            cancellation.Cancel();
            var result = await running;
            Check(!result.IsOk && result.Error.Code == FileErrorCode.Aborted, "a listing whose context is cancelled fails with aborted: " + result);
        }));

        // ssh.test.ts: the expected listing of the local environment.
        yield return Case("env.list-files.ssh.lists-files-as-the-local-environment-lists-them", () => WithEnvRoot(async root =>
        {
            var tree = Path.Combine(root, "tree");
            foreach (var file in new[] { "a/b.txt", "a/c/d.txt", ".hidden", "out/x.js", "skip/y.txt", ".git/HEAD" }) Tree(tree, file);
            Directory.CreateDirectory(Path.Combine(tree, "empty"));
            File.WriteAllText(Path.Combine(tree, ".gitignore"), "out/\n");
            var listing = (await new LocalExecutionEnv(root).ListFilesAsync("tree", new ListFilesOptions { Exclude = ["skip"] }, context)).GetOrThrow();
            FileListEntry[] expected =
            [
                new(".gitignore", FileKind.File),
                new(".hidden", FileKind.File),
                new("a", FileKind.Directory),
                new("a/b.txt", FileKind.File),
                new("a/c", FileKind.Directory),
                new("a/c/d.txt", FileKind.File),
                new("empty", FileKind.Directory),
            ];
            Check(listing.Entries.SequenceEqual(expected), "listing: " + string.Join(", ", listing.Entries));
            Equal(false, listing.Truncated, "truncated");
        }));

        // The optional member and the bounds' validation.
        yield return Case("env.list-files.api.an-environment-without-listfiles-reports-not-supported", () => WithEnvRoot(async root =>
        {
            IFileSystem plain = new InstrumentedFileSystem(root);
            var result = await plain.ListFilesAsync(".", null, context);
            Check(!result.IsOk && result.Error.Code == FileErrorCode.NotSupported, "result: " + result);
        }));

        yield return Case("env.list-files.api.bounds-must-be-non-negative-safe-integers", () => WithEnvRoot(async root =>
        {
            var env = new LocalExecutionEnv(root);
            foreach (var options in new ListFilesOptions[]
            {
                new() { MaxEntries = -1 }, new() { MaxEntries = 0.5 }, new() { MaxBytes = double.NaN }, new() { MaxBytes = double.PositiveInfinity },
                new() { TimeoutMs = 9007199254740992 }, new() { TimeoutMs = -1 }, new() { Exclude = [null!] },
            })
            {
                var result = await env.ListFilesAsync(".", options, context);
                Check(!result.IsOk && result.Error.Code == FileErrorCode.Invalid, $"{options}: {result}");
            }
            var refused = await env.ListFilesAsync(".", new ListFilesOptions { MaxEntries = -1 }, context);
            Equal("maxEntries must be a non-negative safe integer", refused.Error!.Message, "message");
            // The largest safe integer, and negative zero, are bounds.
            var largest = await env.ListFilesAsync(".", new ListFilesOptions { MaxEntries = 9007199254740991, MaxBytes = -0.0, TimeoutMs = 9007199254740991 }, context);
            Check(largest.IsOk && largest.Value.Entries.IsEmpty && !largest.Value.Truncated, "safe bounds: " + largest);
        }));

        // Walks whose paths fd 10.4.2 lists alike.
        yield return ListCase("walk.gitignore-patterns", root =>
        {
            Tree(root, ".git/", "app.log", "important.log", "keep.txt", "build/out.o", "notbuild/build", "rootonly.txt", "sub/rootonly.txt", "docs/a.md",
                "docs/x/y/b.md", "docs/c.txt", "tmp/t", "sub/tmp/t", "abc.txt", "aXc.txt", "a/c.txt", "b1.dat", "a1.dat", "c1.dat", "x.txt", "y.txt",
                "z.txt", "foo bar", "#hash", "!bang", "dirfile", "deep/a/deep/q/z", "deep/z", "s.c", "s.h", "s.o", "[abc", "zz", "trail",
                "e/f/g/h.txt", "e/f/i.txt", "k/l/m", "n.txt");
            File.WriteAllText(Path.Combine(root, ".gitignore"), string.Join('\n', "*.log", "!important.log", "build/", "/rootonly.txt", "docs/**/*.md", "**/tmp",
                "a?c.txt", "[ab]*.dat", "{x,y}.txt", "foo\\ bar\\ ", "\\#hash", "\\!bang", "dirfile/", "**/deep/**/z", "*.[ch]", "[abc", "[z-a]",
                "trail   ", "# comment", "", "e/**", "!e/f/", "k/l/") + "\n");
            Equal(Lines("f .gitignore", "d a", "f a/c.txt", "f c1.dat", "d deep", "d deep/a", "d deep/a/deep", "d deep/a/deep/q", "f dirfile", "d docs",
                "f docs/c.txt", "d docs/x", "d docs/x/y", "d e", "d e/f", "f foo bar", "f important.log", "d k", "f keep.txt", "f n.txt", "d notbuild",
                "f notbuild/build", "f s.o", "d sub", "f sub/rootonly.txt", "f z.txt", "f zz"), List(root).Entries, "entries");
        });

        yield return ListCase("walk.nested-ignore-files-a-nested-repository-crlf-and-a-byte-order-mark", root =>
        {
            Tree(root, ".git/", "a.tmp", "b.tmp", "sub/a.tmp", "sub/b.tmp", "sub/c.txt", "inner/.git/HEAD", "inner/x.tmp", "inner/y.txt", "other/deep/w.tmp");
            File.WriteAllBytes(Path.Combine(root, ".gitignore"), [0xEF, 0xBB, 0xBF, .. "*.tmp\r\n!b.tmp\r\n"u8]);
            File.WriteAllText(Path.Combine(root, "sub", ".gitignore"), "!a.tmp\n*.txt\n");
            File.WriteAllText(Path.Combine(root, "inner", ".gitignore"), "y.txt\n");
            Equal(Lines("f .gitignore", "f b.tmp", "d inner", "f inner/.gitignore", "f inner/x.tmp", "d other", "d other/deep", "d sub", "f sub/.gitignore",
                "f sub/a.tmp", "f sub/b.tmp"), List(root).Entries, "root");
            Equal(Lines("f .gitignore", "f a.tmp", "f b.tmp"), List(Path.Combine(root, "sub")).Entries, "sub");
            Equal(Lines("f .gitignore", "f x.tmp"), List(Path.Combine(root, "inner")).Entries, "inner");
        });

        yield return ListCase("walk.a-worktree-reads-the-common-info-exclude", root =>
        {
            Tree(root, "main/.git/info/exclude", "main/.git/worktrees/wt/commondir", "wt/excluded.txt", "wt/kept.txt", "main/excluded.txt", "main/kept.txt",
                "plain/.git/info/exclude", "plain/excluded.txt", "plain/kept.txt", "detached/.git", "detached/excluded.txt");
            File.WriteAllText(Path.Combine(root, "main", ".git", "info", "exclude"), "excluded.txt\n");
            File.WriteAllText(Path.Combine(root, "main", ".git", "worktrees", "wt", "commondir"), "../..\n");
            File.WriteAllText(Path.Combine(root, "wt", ".git"), $"gitdir: {Path.Combine(root, "main", ".git", "worktrees", "wt")}\n");
            Equal(Lines("f kept.txt"), List(Path.Combine(root, "wt")).Entries, "worktree");
            Equal(Lines("f kept.txt"), List(Path.Combine(root, "main")).Entries, "main work tree");
            // A .git file whose directory has no commondir (a submodule's) has no exclude file, but is a work tree.
            File.WriteAllText(Path.Combine(root, "detached", ".git"), $"gitdir: {Path.Combine(root, "plain", ".git")}\n");
            File.WriteAllText(Path.Combine(root, "plain", ".git", "info", "exclude"), "excluded.txt\n");
            Equal(Lines("f excluded.txt"), List(Path.Combine(root, "detached")).Entries, "no common directory");
        });

        yield return ListCase("walk.hidden-entries-and-whitelisted-hidden-entries", root =>
        {
            Tree(root, ".git/", ".keep", ".drop", ".dir/x", "visible/.inner", "visible/y", "attr.txt");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "!.keep\n");
            if (OperatingSystem.IsWindows()) File.SetAttributes(Path.Combine(root, "attr.txt"), FileAttributes.Hidden);
            Equal(Lines("d .dir", "f .dir/x", "f .drop", "f .gitignore", "f .keep", "f attr.txt", "d visible", "f visible/.inner", "f visible/y"),
                List(root).Entries, "with hidden entries");
            // A whitelisted name is listed although hidden; on Windows the hidden attribute hides a file too.
            Equal(OperatingSystem.IsWindows() ? Lines("f .keep", "d visible", "f visible/y") : Lines("f .keep", "f attr.txt", "d visible", "f visible/y"),
                List(root, new ListFilesOptions { Hidden = false }).Entries, "without hidden entries");
        });

        yield return ListCase("walk.a-subdirectory-applies-anchored-rules-of-its-parents", root =>
        {
            Tree(root, ".git/", "sub/x.txt", "sub/y.txt", "sub/deeper/x.txt", "sub/z.log", "other/x.txt");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "/sub/x.txt\nsub/deeper/\n*.log\n");
            Equal(Lines("f y.txt"), List(Path.Combine(root, "sub")).Entries, "sub");
        });

        yield return ListCase("walk.globs-match-utf-8-bytes", root =>
        {
            Tree(root, ".git/", "é.txt", "e.txt", "z.txt", "Y.txt", "ä/ü.md", "日本.txt", "😀.txt", "ab.txt", "a.txt");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "?.txt\n");
            Equal(Lines("f .gitignore", "f ab.txt", "d ä", "f ä/ü.md", "f é.txt", "f 日本.txt", "f 😀.txt"), List(root).Entries, "one byte");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "??.txt\n[é].md\n");
            Equal(Lines("f .gitignore", "f Y.txt", "f a.txt", "f e.txt", "f z.txt", "d ä", "f ä/ü.md", "f 日本.txt", "f 😀.txt"),
                List(root).Entries, "two bytes");
        });

        yield return ListCase("walk.outside-work-trees-only-nested-repositories-ignore", root =>
        {
            Tree(root, "a.log", "repo/.git/HEAD", "repo/b.log", "repo/c.txt");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
            File.WriteAllText(Path.Combine(root, "repo", ".gitignore"), "*.log\n");
            Equal(Lines("f .gitignore", "f a.log", "d repo", "f repo/.gitignore", "f repo/c.txt"), List(root).Entries, "entries");
        });

        yield return ListCase("walk.alternates-classes-and-stars", root =>
        {
            Tree(root, ".git/", "a/b/c/d.rs", "a/x.rs", "src/main.rs", "src/lib.rs", "src/bin/m.rs", "foo.js", "foo.ts", "foo.jsx", "f-o.txt", "f_o.txt",
                "]x", "-y", "t/u/v/w.json", "q.c", "q.cc");
            File.WriteAllText(Path.Combine(root, ".gitignore"),
                string.Join('\n', "src/**", "!src/main.rs", "*.{js,ts}", "a/**/c", "f[-_]o.txt", "[]]x", "[-]y", "**/*.json", "*.c?") + "\n");
            Equal(Lines("f .gitignore", "d a", "d a/b", "f a/x.rs", "f foo.jsx", "f q.c", "d src", "f src/main.rs", "d t", "d t/u", "d t/u/v"),
                List(root).Entries, "entries");
        });

        // Authored from the crate source (dir.rs): a .jj directory marks a work tree too, and the global excludes file applies inside work
        // trees, relative to the listed directory.
        yield return ListCase("walk.a-jj-directory-marks-a-work-tree", root =>
        {
            Tree(root, ".jj/repo", "a.log", "b.txt");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
            Equal(Lines("f .gitignore", "d .jj", "f .jj/repo", "f b.txt"), List(root).Entries, "entries");
        });

        yield return ListCase("walk.the-global-excludes-file-applies-inside-work-trees", root =>
        {
            Tree(root, "config/x", "work/.git/", "work/a.secret", "work/sub/b.secret", "work/c.txt", "plain/a.secret");
            // The long form of the path: `~` in a configured path is the home directory, and Windows temporary paths can be 8.3 short names.
            var excludes = Path.Combine(LocalFileSystem.RealPath(Path.Combine(root, "config")), "excludes");
            Check(!excludes.Contains('~'), "the excludes path has no ~: " + excludes);
            File.WriteAllText(excludes, "*.secret\n/c.txt\n");
            File.WriteAllText(Path.Combine(root, "config", "gitconfig"), $"[core]\n\texcludesFile = \"{excludes.Replace('\\', '/')}\"\n");
            var previous = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", Path.Combine(root, "config", "gitconfig"));
            try
            {
                Equal(Lines("d sub"), List(Path.Combine(root, "work")).Entries, "work tree");
                Equal(Lines("f a.secret"), List(Path.Combine(root, "plain")).Entries, "outside work trees");
                Equal(Lines("f b.secret"), List(Path.Combine(root, "work", "sub"), new ListFilesOptions { Gitignore = false }).Entries, "gitignore off");
            }
            finally { Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", previous); }
        });

        yield return ListCase("walk.siblings-sort-by-utf-8-bytes", root =>
        {
            Tree(root, ".txt", "😀.txt", "~.txt", "A.txt", "_.txt");
            Equal(Lines("f A.txt", "f _.txt", "f ~.txt", "f .txt", "f 😀.txt"), List(root).Entries, "entries");
            Check(LocalFileListing.CompareNames("", "😀") < 0 && LocalFileListing.CompareNames("a", "ab") < 0 && LocalFileListing.CompareNames("b", "a") > 0,
                "code point order");
        });

        // The ignore crate's gitignore tests.
        const string ROOT = "/home/foobar/rust/rg";
        (string Name, string Root, string Gitignore, string Path, bool IsDirectory, bool Ignored)[] gitignore =
        [
            ("ig1", ROOT, "months", "months", false, true),
            ("ig2", ROOT, "*.lock", "Cargo.lock", false, true),
            ("ig3", ROOT, "*.rs", "src/main.rs", false, true),
            ("ig4", ROOT, "src/*.rs", "src/main.rs", false, true),
            ("ig5", ROOT, "/*.c", "cat-file.c", false, true),
            ("ig6", ROOT, "/src/*.rs", "src/main.rs", false, true),
            ("ig7", ROOT, "!src/main.rs\n*.rs", "src/main.rs", false, true),
            ("ig8", ROOT, "foo/", "foo", true, true),
            ("ig9", ROOT, "**/foo", "foo", false, true),
            ("ig10", ROOT, "**/foo", "src/foo", false, true),
            ("ig11", ROOT, "**/foo/**", "src/foo/bar", false, true),
            ("ig12", ROOT, "**/foo/**", "wat/src/foo/bar/baz", false, true),
            ("ig13", ROOT, "**/foo/bar", "foo/bar", false, true),
            ("ig14", ROOT, "**/foo/bar", "src/foo/bar", false, true),
            ("ig15", ROOT, "abc/**", "abc/x", false, true),
            ("ig16", ROOT, "abc/**", "abc/x/y", false, true),
            ("ig17", ROOT, "abc/**", "abc/x/y/z", false, true),
            ("ig18", ROOT, "a/**/b", "a/b", false, true),
            ("ig19", ROOT, "a/**/b", "a/x/b", false, true),
            ("ig20", ROOT, "a/**/b", "a/x/y/b", false, true),
            ("ig21", ROOT, @"\!xy", "!xy", false, true),
            ("ig22", ROOT, @"\#foo", "#foo", false, true),
            ("ig23", ROOT, "foo", "./foo", false, true),
            ("ig24", ROOT, "target", "grep/target", false, true),
            ("ig25", ROOT, "Cargo.lock", "./tabwriter-bin/Cargo.lock", false, true),
            ("ig26", ROOT, "/foo/bar/baz", "./foo/bar/baz", false, true),
            ("ig27", ROOT, "foo/", "xyz/foo", true, true),
            ("ig28", "./src", "/llvm/", "./src/llvm", true, true),
            ("ig29", ROOT, "node_modules/ ", "node_modules", true, true),
            ("ig30", ROOT, "**/", "foo/bar", true, true),
            ("ig31", ROOT, "path1/*", "path1/foo", false, true),
            ("ig32", ROOT, ".a/b", ".a/b", false, true),
            ("ig33", "./", ".a/b", ".a/b", false, true),
            ("ig34", ".", ".a/b", ".a/b", false, true),
            ("ig35", "./.", ".a/b", ".a/b", false, true),
            ("ig36", "././", ".a/b", ".a/b", false, true),
            ("ig37", "././.", ".a/b", ".a/b", false, true),
            ("ig38", ROOT, "\\[", "[", false, true),
            ("ig39", ROOT, "\\?", "?", false, true),
            ("ig40", ROOT, "\\*", "*", false, true),
            ("ig41", ROOT, "\\a", "a", false, true),
            ("ig42", ROOT, "s*.rs", "sfoo.rs", false, true),
            ("ig43", ROOT, "**", "foo.rs", false, true),
            ("ig44", ROOT, "**/**/*", "a/foo.rs", false, true),
            ("ignot1", ROOT, "amonths", "months", false, false),
            ("ignot2", ROOT, "monthsa", "months", false, false),
            ("ignot3", ROOT, "/src/*.rs", "src/grep/src/main.rs", false, false),
            ("ignot4", ROOT, "/*.c", "mozilla-sha1/sha1.c", false, false),
            ("ignot5", ROOT, "/src/*.rs", "src/grep/src/main.rs", false, false),
            ("ignot6", ROOT, "*.rs\n!src/main.rs", "src/main.rs", false, false),
            ("ignot7", ROOT, "foo/", "foo", false, false),
            ("ignot8", ROOT, "**/foo/**", "wat/src/afoo/bar/baz", false, false),
            ("ignot9", ROOT, "**/foo/**", "wat/src/fooa/bar/baz", false, false),
            ("ignot10", ROOT, "**/foo/bar", "foo/src/bar", false, false),
            ("ignot11", ROOT, "#foo", "#foo", false, false),
            ("ignot12", ROOT, "\n\n\n", "foo", false, false),
            ("ignot13", ROOT, "foo/**", "foo", true, false),
            ("ignot14", "./third_party/protobuf", "m4/ltoptions.m4", "./third_party/protobuf/csharp/src/packages/repositories.config", false, false),
            ("ignot15", ROOT, "!/bar", "foo/bar", false, false),
            ("ignot16", ROOT, "*\n!**/", "foo", true, false),
            ("ignot17", ROOT, "src/*.rs", "src/grep/src/main.rs", false, false),
            ("ignot18", ROOT, "path1/*", "path2/path1/foo", false, false),
            ("ignot19", ROOT, "s*.rs", "src/foo.rs", false, false),
            ("cs1", ROOT, "*.html", "foo.html", false, true),
            ("cs2", ROOT, "*.html", "foo.HTML", false, false),
            ("cs3", ROOT, "*.html", "foo.htm", false, false),
            ("cs4", ROOT, "*.html", "foo.HTM", false, false),
        ];
        foreach (var (name, gitignoreRoot, lines, path, isDirectory, ignored) in gitignore)
            yield return Case("env.list-files.gitignore." + name, () =>
                Equal(ignored, GitignoreMatcher.FromLines(gitignoreRoot, lines).Matched(path, isDirectory) == IgnoreMatch.Ignore, $"{lines} on {path}"));

        yield return Case("env.list-files.gitignore.parse-excludes-file", () =>
        {
            var home = "/home/user";
            Equal("/foo/bar", GitignoreMatcher.ParseExcludesFile("[core]\nexcludesFile = /foo/bar"u8.ToArray(), home), "excludes_file1");
            Equal(home + "/foo/bar", GitignoreMatcher.ParseExcludesFile("[core]\nexcludesFile = ~/foo/bar"u8.ToArray(), home), "excludes_file2");
            Equal(null, GitignoreMatcher.ParseExcludesFile("[core]\nexcludeFile = /foo/bar"u8.ToArray(), home), "excludes_file3");
            Equal(home + "/foo/bar", GitignoreMatcher.ParseExcludesFile("[core]\nexcludesFile = \"~/foo/bar\""u8.ToArray(), home), "excludes_file4");
            Equal(null, GitignoreMatcher.ParseExcludesFile("[core]\nexcludesFile = \" \"~/foo/bar \" \""u8.ToArray(), home), "excludes_file5");
        });

        yield return Case("env.list-files.gitignore.regression-106", () => { GitignoreMatcher.FromLines("/", " "); });

        (string, Func<Task>) ListCase(string slug, Action<string> body) =>
            Case("env.list-files." + slug, () => WithEnvRoot(root => { body(root); return Task.CompletedTask; }));
    }

    /// <summary>Creates files (and, for names ending in <c>/</c>, directories) below <paramref name="root"/>.</summary>
    private static void Tree(string root, params string[] paths)
    {
        foreach (var path in paths)
        {
            var full = Path.Combine(root, path);
            if (path.EndsWith('/')) { Directory.CreateDirectory(full); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, []);
        }
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    /// <summary>A listing through LocalFileListing as list.rs's tests read it: <c>&lt;kind letter&gt; &lt;path&gt;</c> lines.</summary>
    private static (string Entries, string[] Lines, bool Truncated) List(string root, ListFilesOptions? options = null)
    {
        var listed = LocalFileListing.List(root, options, () => false);
        Check(listed.IsOk, "listing " + root + ": " + listed);
        var lines = listed.Value.Entries.Select(entry => (entry.Kind switch { FileKind.File => "f", FileKind.Directory => "d", _ => "l" }) + " " + entry.Path).ToArray();
        return (string.Join("\n", lines), lines, listed.Value.Truncated);
    }

    private static FileError Failure(string root, bool aborted = false)
    {
        var listed = LocalFileListing.List(root, null, () => aborted);
        Check(!listed.IsOk, "listing " + root + " fails");
        return listed.Error!;
    }

    /// <summary>A link to an existing directory: a symbolic link, or on Windows (where creating one needs a privilege) a junction.</summary>
    private static void Link(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", path, target]) { RedirectStandardOutput = true, UseShellExecute = false })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Check(process.ExitCode == 0 && Directory.Exists(path), "junction " + path);
    }

    /// <summary>A link whose target does not exist (on Windows a junction whose directory was removed).</summary>
    private static void DanglingLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { File.CreateSymbolicLink(path, Path.GetFileName(target)); return; }
        Directory.CreateDirectory(target);
        Link(path, target);
        Directory.Delete(target);
    }
}
