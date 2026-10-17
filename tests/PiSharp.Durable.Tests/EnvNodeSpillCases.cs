using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Tools.Env;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/env-node-spill.test.ts, as env.node-spill.<case>. Upstream mocks
// node:fs createWriteStream so every spill write takes 600 ms and is immediately backpressured; here LocalExecutionEnv's OpenSpillStream seam
// returns a stream whose writes take 600 ms. A pending spill write holds the output pump (upstream pauses the pipes on backpressure), so
// upstream's "rejected writes" count becomes the count of writes through the slow stream. Upstream skips the case on Windows; the job-object
// lifetime keeps a background descendant's stdout readable there as well, so it runs on every OS.
internal static partial class Program
{
    // Longer than the shell's post-exit stdio grace period plus the descendant's delayed write.
    private const int SpillWriteDelayMs = 600;

    private static IEnumerable<(string, Func<Task>)> EnvNodeSpillCases() =>
    [
        Case("env.node-spill.keeps-inherited-stdio-open-past-the-exit-grace-period-while-a-spill-write-is-pending", () => WithEnvRoot(async root =>
        {
            var env = new SlowSpillEnv(root);
            // The shell exits after the first chunk crosses the spill threshold and backpressures the spill. A background descendant retains
            // stdout and writes after the post-exit grace period. Without the pending-spill guard, settlement stops reading stdout before
            // that descendant output is read.
            const string command = "printf '%020d' 0 | tr 0 a; (sleep 0.2; printf '%01000d' 0 | tr 0 b) &";
            var output = new StringBuilder();
            var result = (await env.ExecAsync(command, new ShellExecOptions
            {
                Spill = new ShellSpillOptions(10, 10),
                OnOutput = (text, _, _) => { lock (output) output.Append(text); },
            }, DurableContext.Background).WaitAsync(TimeSpan.FromSeconds(10))).GetOrThrow();

            Check(env.Writes > 0, "spill writes were slow and pending");
            lock (output) Equal(1020, output.Length, "output length");
            Check(result.SpillPath is not null, "a spill");
            try { Equal(new string('a', 20) + new string('b', 1000), (await env.ReadTextFileAsync(result.SpillPath!, DurableContext.Background)).GetOrThrow(), "spill"); }
            finally { DeleteDirectory(Path.GetDirectoryName(result.SpillPath!)!); }
        })),
    ];

    /// <summary>An environment whose spill writes each take <see cref="SpillWriteDelayMs"/>.</summary>
    private sealed class SlowSpillEnv(string root) : LocalExecutionEnv(root)
    {
        private int writes;
        public int Writes => Volatile.Read(ref writes);

        protected override Stream OpenSpillStream(string path) => new SlowStream(base.OpenSpillStream(path), () => Interlocked.Increment(ref writes));

        private sealed class SlowStream(Stream inner, Action onWrite) : Stream
        {
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => inner.Length;
            public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                onWrite();
                await Task.Delay(SpillWriteDelayMs, cancellationToken);
                await inner.WriteAsync(buffer, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                await base.DisposeAsync();
            }
        }
    }
}
