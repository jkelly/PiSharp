using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;
using PiSharp.Durable.Testing;

// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/test/env-node-conformance.test.ts, file-system half. The environment
// conformance kit's file-system cases run on LocalFileSystem as env.filesystem.conformance.<case>, each in a fresh directory; the readers
// case "reads ranges spanning several internal chunks exactly" is env.filesystem.local.<case>. Still to come with LocalExecutionEnv (P9):
// the polling-watch suite, the watch limits case, the shell-dependent kit cases, and "refuses a FIFO without waiting for a writer" (POSIX
// only; .NET opens FIFOs blocking, so LocalFileSystem cannot refuse one without waiting yet).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> EnvNodeConformanceCases()
    {
        foreach (var testCase in EnvConformance.Create(async use =>
        {
            var cwd = Temp("env-conformance");
            try { await use(new LocalFileSystem(cwd)); }
            finally { DeleteDirectory(cwd); }
        }))
            yield return Case("env.filesystem.conformance." + testCase.Slug, testCase.Run);

        yield return Case("env.filesystem.local.reads-ranges-spanning-several-internal-chunks-exactly", async () =>
        {
            var cwd = Temp("env-readers");
            try
            {
                var bytes = new byte[(int)(2.5 * 1024 * 1024)];
                for (var index = 0; index < bytes.Length; index++) bytes[index] = (byte)(index * 31 % 251);
                File.WriteAllBytes(Path.Combine(cwd, "big.bin"), bytes);
                var reader = (await new LocalFileSystem(cwd).OpenBinaryReaderAsync("big.bin", null, DurableContext.Background)).GetOrThrow();
                try
                {
                    var all = (await reader.ReadAsync(0, bytes.Length + 10, DurableContext.Background)).GetOrThrow();
                    Equal(bytes.Length, all.Length, "length");
                    Check(all.AsSpan().SequenceEqual(bytes), "every byte");
                    var middle = (await reader.ReadAsync(1024 * 1024 - 3, 7, DurableContext.Background)).GetOrThrow();
                    Check(middle.AsSpan().SequenceEqual(bytes.AsSpan(1024 * 1024 - 3, 7)), "a range across a chunk boundary");
                }
                finally { await reader.CloseAsync(DurableContext.Background); }
            }
            finally { DeleteDirectory(cwd); }
        });
    }

    /// <summary>Removes a test directory, best effort.</summary>
    internal static void DeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
