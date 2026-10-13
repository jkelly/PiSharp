using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;
using PiSharp.Durable.Testing;
using PiSharp.Durable.Tools.Env;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/env-node-conformance.test.ts, as env.node-conformance.<suite>.<case>.
// The environment conformance kit registers through ConformanceRunner (upstream registerEnvConformance) on LocalExecutionEnv twice: with the
// platform's watch mode (suite "conformance"; polling on Windows) and with polling watches every 100 ms (suite "polling"). As upstream, Windows
// runs the kit's shell cases through Git Bash with symbolic links off (Git Bash's `ln -s` copies unless native links are enabled). The watch
// limits case is suite "watch-limits" and the readers cases suite "readers"; "refuses a FIFO without waiting for a writer" is POSIX only, as
// upstream (Windows has no FIFOs).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> EnvNodeConformanceCases()
    {
        var context = DurableContext.Background;
        // Git Bash's `ln -s` copies instead of linking unless native symlinks are enabled.
        System.Collections.Immutable.ImmutableArray<string> shell = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Git", "bin", "bash.exe"), "-c"]
            : default;
        var symlinks = !OperatingSystem.IsWindows();
        var runner = new CaseRunner("env.node-conformance");
        ConformanceRunner.RegisterEnvConformance(runner, "conformance", async use =>
        {
            var cwd = Temp("env-conformance");
            try { await use(new LocalExecutionEnv(cwd)); }
            finally { await RemoveTempDirAsync(cwd); }
        }, shell, symlinks);
        ConformanceRunner.RegisterEnvConformance(runner, "polling", async use =>
        {
            var cwd = Temp("env-conformance");
            try { await use(new LocalExecutionEnv(new LocalExecutionEnvOptions { Cwd = cwd, Watch = new LocalWatchOptions { Mode = WatchMode.Polling, PollIntervalMs = 100 } })); }
            finally { await RemoveTempDirAsync(cwd); }
        }, shell, symlinks);
        foreach (var testCase in runner.Cases) yield return testCase;

        yield return Case("env.node-conformance.watch-limits.refuses-a-tree-over-the-directory-budget-and-stops-with-an-error-when-one-grows-past-it", () =>
            WithEnvRoot(async cwd =>
            {
                var env = new LocalExecutionEnv(new LocalExecutionEnvOptions { Cwd = cwd, Watch = new LocalWatchOptions { MaxDirectories = 3 } });
                (await env.CreateDirAsync("tree/a/b", null, context)).GetOrThrow();
                (await env.CreateDirAsync("tree/c", null, context)).GetOrThrow();
                var refused = await env.WatchAsync([new WatchTarget("tree") { Recursive = true }], _ => { }, context);
                Check(!refused.IsOk && refused.Error.Code == FileErrorCode.Invalid, "a tree over the budget: " + refused);

                (await env.RemoveAsync("tree/c", new RemoveOptions { Recursive = true }, context)).GetOrThrow();
                var changes = new List<WatchChange>();
                var watcher = (await env.WatchAsync([new WatchTarget("tree") { Recursive = true }], change => { lock (changes) changes.Add(change); }, context))
                    .GetOrThrow();
                (await env.CreateDirAsync("tree/d", null, context)).GetOrThrow();
                var deadline = Environment.TickCount64 + 3000;
                while (Environment.TickCount64 < deadline)
                {
                    lock (changes) if (changes.Any(change => change is WatchErrorChange)) break;
                    await Task.Delay(20);
                }
                lock (changes) Check(changes.LastOrDefault() is WatchErrorChange { Error.Code: FileErrorCode.Invalid }, "the last change is the budget error: " +
                    string.Join(", ", changes));
                await watcher.CloseAsync(context);
            }));

        yield return Case("env.node-conformance.readers.reads-ranges-spanning-several-internal-chunks-exactly", () => WithEnvRoot(async cwd =>
        {
            var bytes = new byte[(int)(2.5 * 1024 * 1024)];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index * 31 % 251);
            File.WriteAllBytes(Path.Combine(cwd, "big.bin"), bytes);
            var reader = (await new LocalExecutionEnv(cwd).OpenBinaryReaderAsync("big.bin", null, context)).GetOrThrow();
            try
            {
                var all = (await reader.ReadAsync(0, bytes.Length + 10, context)).GetOrThrow();
                Equal(bytes.Length, all.Length, "length");
                Check(all.AsSpan().SequenceEqual(bytes), "every byte");
                var middle = (await reader.ReadAsync(1024 * 1024 - 3, 7, context)).GetOrThrow();
                Check(middle.AsSpan().SequenceEqual(bytes.AsSpan(1024 * 1024 - 3, 7)), "a range across a chunk boundary");
            }
            finally { await reader.CloseAsync(context); }
        }));

        yield return Case("env.node-conformance.readers.refuses-a-fifo-without-waiting-for-a-writer", () => WithEnvRoot(async cwd =>
        {
            if (OperatingSystem.IsWindows()) return; // Windows has no FIFOs (upstream skips it there).
            using (var mkfifo = System.Diagnostics.Process.Start("mkfifo", [Path.Combine(cwd, "pipe")])) await mkfifo.WaitForExitAsync();
            var result = await new LocalExecutionEnv(cwd).OpenBinaryReaderAsync("pipe", null, context).WaitAsync(TimeSpan.FromSeconds(10));
            Check(!result.IsOk && result.Error.Code == FileErrorCode.Invalid, "result: " + result);
        }));
    }

    /// <summary>Removes a test directory, best effort.</summary>
    internal static void DeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A conformance runner that turns <c>describe</c>/<c>it</c> into this suite's cases, <c>&lt;prefix&gt;.&lt;describe&gt;.&lt;it slug&gt;</c>.</summary>
    private sealed class CaseRunner(string prefix) : IConformanceTestRunner
    {
        private readonly Stack<string> suites = new();
        public List<(string, Func<Task>)> Cases { get; } = [];
        public IConformanceAssertions Assertions { get; } = ConformanceAssertions.Create(message => new InvalidOperationException(message));

        public void Describe(string name, Action suite)
        {
            suites.Push(name);
            try { suite(); }
            finally { suites.Pop(); }
        }

        public void It(string name, Func<Task> test, int? timeoutMs = null)
        {
            var id = string.Join(".", new[] { prefix }.Concat(suites.Reverse()).Append(new EnvConformanceCase(name, test).Slug));
            Cases.Add(Case(id, timeoutMs is { } timeout ? () => test().WaitAsync(TimeSpan.FromMilliseconds(timeout)) : test));
        }
    }
}
