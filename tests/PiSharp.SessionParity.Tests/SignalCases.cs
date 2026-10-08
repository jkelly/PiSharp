using System.Runtime.InteropServices;
using System.Text;
using PiSharp.Cli;
using PiSharp.Cli.Commands;

// Pi v1.1.0 packages/coding-agent/src/modes/rpc/rpc-mode.ts and modes/print-mode.ts (registerSignalHandlers), by reading.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> SignalCases() =>
    [
        Case("signals.exit-codes-first-signal-wins-and-cancellation", SignalExitCodes),
        Case("signals.sigterm-shuts-the-rpc-host-down-gracefully-with-143", SignalRpcHost),
    ];

    private static Task SignalExitCodes()
    {
        using (var signals = new ShutdownSignals(register: false))
        {
            Check(signals.ExitCode is null && signals.Exit(7) == 7 && !signals.Token.IsCancellationRequested, "A host without a signal changed its exit.");
            signals.Signal(PosixSignal.SIGHUP); signals.Signal(PosixSignal.SIGTERM);
            Check(signals.Token.IsCancellationRequested && signals.Received == PosixSignal.SIGHUP && signals.ExitCode == 129 && signals.Exit(1) == 129,
                "SIGHUP did not exit 129 or a later signal replaced the first.");
        }
        using (var signals = new ShutdownSignals(register: false))
        {
            signals.Signal(PosixSignal.SIGTERM);
            Check(signals.ExitCode == 143 && signals.Exit(0) == 143, "SIGTERM did not exit 143.");
        }
        return Task.CompletedTask;
    }

    /// <summary>One get_state command, then input that stays open until the host cancels the read.</summary>
    private sealed class OpenInput : Stream
    {
        private byte[]? pending = Encoding.UTF8.GetBytes("{\"type\":\"get_state\",\"id\":\"state\"}\n");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (pending is { } bytes) { pending = null; bytes.CopyTo(buffer); return bytes.Length; }
            await Task.Delay(Timeout.Infinite, token); return 0;
        }
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    }
    private sealed class WatchedOutput : MemoryStream
    {
        internal readonly TaskCompletionSource State = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await base.WriteAsync(buffer, token);
            if (Encoding.UTF8.GetString(buffer.Span).Contains("\"command\":\"get_state\"", StringComparison.Ordinal)) State.TrySetResult();
        }
    }

    private static async Task SignalRpcHost()
    {
        var root = Temp("signals"); var script = Path.Combine(root, "script.json"); var session = Path.Combine(root, "session.jsonl");
        await File.WriteAllTextAsync(script, "{\"schemaVersion\":1,\"turns\":[{\"events\":[{\"type\":\"response.completed\"}]}]}");
        using var signals = new ShutdownSignals(register: false);
        using var output = new WatchedOutput(); using var error = new StringWriter();
        var running = RpcSessionCommand.RunAsync(["session", "rpc", "--session", session, "--workspace", root, "--offline-script", script,
            "--session-mode", "new-lazy"], new OpenInput(), output, error, signals.Token);
        await Task.WhenAny(output.State.Task, running).WaitAsync(TimeSpan.FromSeconds(60));
        Check(output.State.Task.IsCompletedSuccessfully, "The RPC host did not start: " + error);
        signals.Signal(PosixSignal.SIGTERM);
        var result = await running.WaitAsync(TimeSpan.FromSeconds(60));
        Equal(143, signals.Exit(result), "exit code after SIGTERM");
        Check(!error.ToString().Contains("CleanupFailed", StringComparison.Ordinal), "The signal shutdown did not settle cleanly: " + error);
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }
}
