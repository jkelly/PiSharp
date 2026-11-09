using PiSharp.Rpc;

internal static partial class RpcSessionDispatcherTests
{
    private static IEnumerable<(string Name, Func<Task> Run)> IdleShutdownCases() =>
    [
        ("rpc.idle-shutdown-wakes-input-without-a-command", IdleShutdown),
        ("rpc.idle-shutdown-waits-for-startup", StartupShutdown),
        ("rpc.idle-shutdown-accounts-for-buffered-command", BufferedShutdown),
        ("rpc.idle-shutdown-waits-for-command-response-flush", ResponseShutdown),
        ("rpc.idle-shutdown-waits-for-successful-active-run", RunShutdown)
    ];

    private static async Task IdleShutdown()
    {
        await using var fixture = await Fixture.Create(new Script([]));
        using var input = new IdleShutdownInput();
        var run = fixture.Dispatcher.RunAsync(new JsonlReader(input));
        await input.Entered.Task.WaitAsync(Deadline);
        fixture.Dispatcher.RequestShutdownWhenIdle();
        fixture.Dispatcher.RequestShutdownWhenIdle();
        await run.WaitAsync(Deadline);
        Check(input.Canceled, "Idle shutdown did not wake the blocked input read.");
        Check(fixture.Output.Records().Length == 0, "Idle shutdown required or invented a command.");
    }

    private static async Task StartupShutdown()
    {
        var entered = Gate(); var release = Gate();
        await using var fixture = await Fixture.Create(new Script([]), startup: async _ =>
        { entered.TrySetResult(); await release.Task; });
        using var input = new IdleShutdownInput();
        var run = fixture.Dispatcher.RunAsync(new JsonlReader(input));
        try
        {
            await entered.Task.WaitAsync(Deadline);
            fixture.Dispatcher.RequestShutdownWhenIdle();
            await Task.Delay(75); // Let the queued idle check run while the startup original is held.
            Check(!run.IsCompleted && !input.Canceled, "Shutdown interrupted session startup.");
        }
        finally { release.TrySetResult(); }
        await run.WaitAsync(Deadline);
    }

    private static async Task BufferedShutdown()
    {
        await using var fixture = await Fixture.Create(new Script([]));
        using var input = new IdleShutdownInput("{\"type\":\"get_state\",\"id\":\"buffered\"}\n"u8.ToArray());
        fixture.Dispatcher.RequestShutdownWhenIdle();
        await fixture.Dispatcher.RunAsync(new JsonlReader(input)).WaitAsync(Deadline);
        Check(fixture.Response("buffered").Value.GetProperty("success").GetBoolean(), "Startup shutdown lost a buffered command before admission.");
    }

    private static async Task ResponseShutdown()
    {
        var entered = Gate(); var release = Gate();
        var output = new Capture { BeforeFlush = async (_, _) => { entered.TrySetResult(); await release.Task; } };
        await using var fixture = await Fixture.Create(new Script([]), output: output);
        using var input = new IdleShutdownInput();
        var run = fixture.Dispatcher.RunAsync(new JsonlReader(input));
        await input.Entered.Task.WaitAsync(Deadline);
        var command = fixture.Send("get_state", "held-response");
        try
        {
            await entered.Task.WaitAsync(Deadline);
            fixture.Dispatcher.RequestShutdownWhenIdle();
            await Task.Delay(75); // The response flush remains held across the queued check.
            Check(!command.IsCompleted && !run.IsCompleted && !input.Canceled, "Shutdown overtook the response flush.");
        }
        finally { release.TrySetResult(); }
        await command.WaitAsync(Deadline); await run.WaitAsync(Deadline);
        Check(fixture.Response("held-response").Value.GetProperty("success").GetBoolean(), "Command response was lost.");
    }

    private static async Task RunShutdown()
    {
        var step = new Step(Message("completed before shutdown"), Pause: true);
        await using var fixture = await Fixture.Create(new Script([step]));
        using var input = new IdleShutdownInput();
        var run = fixture.Dispatcher.RunAsync(new JsonlReader(input));
        await input.Entered.Task.WaitAsync(Deadline);
        await fixture.Send("prompt", "run", "hi");
        try
        {
            await step.Entered.Task.WaitAsync(Deadline);
            fixture.Dispatcher.RequestShutdownWhenIdle();
            Check(!run.IsCompleted && !input.Canceled, "Graceful request aborted the active run.");
        }
        finally { step.Release.TrySetResult(); }
        await run.WaitAsync(Deadline);
        var settled = fixture.Output.Records().Single(value => Type(value) == "agent_settled");
        Check(!settled.Value.GetProperty("aborted").GetBoolean(), "Active run was aborted by an extension shutdown request.");
    }

    private sealed class IdleShutdownInput(byte[]? first = null) : Stream
    {
        private ReadOnlyMemory<byte> remaining = first ?? [];
        public TaskCompletionSource Entered { get; } = Gate();
        public bool Canceled;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!remaining.IsEmpty)
            {
                var count = Math.Min(buffer.Length, remaining.Length);
                remaining[..count].CopyTo(buffer); remaining = remaining[count..]; return count;
            }
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
