using System.IO.Pipes;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PiSharp.Cli;

internal static class StandardInputCancellationTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("CLI real empty stdin pipe cancels without a frame or EOF and preserves borrowed ownership", PendingCancellation),
        ("CLI real stdin pipe rejects competing reads and shares awaited disposal", ConcurrentDisposal),
        ("CLI real stdin preserves bounded raw chunks Unicode NUL and buffered EOF", BytesAndEof),
        ("CLI real message pipe preserves partial message reads", MessagePartials),
        ("CLI stdin duplicate admission sanitizes invalid and disposed handles", InvalidHandles)
    ];

    private static async Task PendingCancellation()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows stdin pipe qualification is required.");
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None, 65536);
        using var borrowed = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        await using var input = CancellableStandardInput.DuplicateSynchronousPipe(borrowed.SafePipeHandle);
        using var caller = new CancellationTokenSource();
        var buffer = new byte[128];
        var read = input.ReadAsync(buffer, caller.Token).AsTask();
        try
        {
            Check(!read.IsCompleted, "Empty live pipe completed before cancellation.");
            caller.Cancel();
            var error = await ThrowsAsync<OperationCanceledException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(5)));
            Check(error.CancellationToken == caller.Token, "Cancellation lost the actual caller token.");
            // No write or EOF occurs until the canceled read has actually settled.
            Check(!borrowed.SafePipeHandle.IsClosed, "Cancellation closed the borrowed stdin handle.");
            var payload = Encoding.UTF8.GetBytes("after-cancel \u03c0\U0001f642\0\n");
            await writer.WriteAsync(payload);
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var count = await input.ReadAsync(buffer, guard.Token);
            Check(buffer.AsSpan(0, count).SequenceEqual(payload), "Canceled stream could not perform a later exact read.");
            using var precanceled = new CancellationTokenSource(); precanceled.Cancel();
            var before = await ThrowsAsync<OperationCanceledException>(async () =>
            {
                var unexpected = await input.ReadAsync(buffer, precanceled.Token);
                Check(unexpected >= 0 && unexpected <= buffer.Length, "Read returned an invalid byte count.");
                throw new InvalidOperationException("Precanceled read returned bytes instead of throwing.");
            });
            Check(before.CancellationToken == precanceled.Token, "Precancel lost caller token.");
        }
        finally { caller.Cancel(); await input.DisposeAsync(); await Join(read); }
    }

    private static async Task ConcurrentDisposal()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows stdin pipe qualification is required.");
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None, 65536);
        using var borrowed = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        var input = CancellableStandardInput.DuplicateSynchronousPipe(borrowed.SafePipeHandle);
        var read = input.ReadAsync(new byte[32]).AsTask();
        try
        {
            Check(!read.IsCompleted, "Fixture never established a pending read.");
            Throws<InvalidOperationException>(() => input.ReadAsync(new byte[1]));
            var first = input.DisposeAsync().AsTask(); var second = input.DisposeAsync().AsTask();
            Check(ReferenceEquals(first, second), "Concurrent disposal did not share one completion.");
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            Check(read.IsCompleted, "Disposal returned before the pending read settled.");
            await ThrowsAsync<ObjectDisposedException>(async () => await read);
            Check(!input.CanRead && !borrowed.SafePipeHandle.IsClosed, "Disposal changed borrowed pipe ownership.");
            Throws<ObjectDisposedException>(() => input.ReadAsync(new byte[1]));
            // Verify the original endpoint, rather than merely checking an in-memory disposed flag.
            await writer.WriteAsync(new byte[] { 0x41, 0, 0x42 });
            var buffer = new byte[3];
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var count = await borrowed.ReadAsync(buffer, guard.Token);
            Check(count == 3 && buffer.AsSpan().SequenceEqual(new byte[] { 0x41, 0, 0x42 }), "Duplicate disposal closed original endpoint.");
        }
        finally { await input.DisposeAsync(); await Join(read); }
    }

    private static async Task BytesAndEof()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows stdin pipe qualification is required.");
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None, 65536);
        using var borrowed = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        await using var input = CancellableStandardInput.DuplicateSynchronousPipe(borrowed.SafePipeHandle);
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\u03c0\U0001f642e\u0301\0\r\n", 1500)));
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await writer.WriteAsync(payload, guard.Token);
        writer.Dispose(); // EOF while original bytes are still buffered must preserve the entire prefix.
        var observed = new List<byte>(); var buffer = new byte[32768];
        while (true)
        {
            var count = await input.ReadAsync(buffer, guard.Token);
            if (count == 0) break;
            Check(count <= 8192, "Native stdin read exceeded its internal chunk bound.");
            observed.AddRange(buffer.AsSpan(0, count).ToArray());
        }
        Check(observed.SequenceEqual(payload), "Buffered EOF lost or altered raw input bytes.");
        Check(await input.ReadAsync(Memory<byte>.Empty, guard.Token) == 0, "Empty read did not return zero.");
    }

    private static async Task MessagePartials()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows stdin pipe qualification is required.");
        var name = "pisharp-stdin-" + Guid.NewGuid().ToString("N");
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var writer = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 8192, 8192);
        // Windows requires FILE_WRITE_ATTRIBUTES for changing read mode. The owned duplex
        // client supplies that right; production never changes the borrowed stdin mode.
        using var borrowed = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.None);
        var connected = writer.WaitForConnectionAsync(guard.Token);
        try
        {
            await borrowed.ConnectAsync(guard.Token); await connected;
            borrowed.ReadMode = PipeTransmissionMode.Message;
            await using var input = CancellableStandardInput.DuplicateSynchronousPipe(borrowed.SafePipeHandle);
            var payload = Encoding.UTF8.GetBytes("message \u03c0\U0001f642\0 remainder");
            await writer.WriteAsync(payload, guard.Token);
            var observed = new List<byte>(); var buffer = new byte[3];
            while (observed.Count < payload.Length)
            {
                var count = await input.ReadAsync(buffer, guard.Token);
                Check(count is > 0 and <= 3, "Partial message read lost its authoritative byte count.");
                observed.AddRange(buffer.AsSpan(0, count).ToArray());
            }
            Check(observed.SequenceEqual(payload), "ERROR_MORE_DATA discarded a partial message.");
            await writer.DisposeAsync();
            Check(await input.ReadAsync(buffer, guard.Token) == 0, "Closed message pipe did not produce EOF.");
        }
        finally
        {
            guard.Cancel(); await writer.DisposeAsync();
            try { await connected; } catch (OperationCanceledException) { } catch (ObjectDisposedException) { }
        }
    }

    private static Task InvalidHandles()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows stdin pipe qualification is required.");
        using var invalid = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        var error = Throws<IOException>(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var unexpected = CancellableStandardInput.DuplicateSynchronousPipe(invalid);
            throw new InvalidOperationException("Invalid pipe handle was admitted.");
        });
        Check(error.Message == "Standard input pipe is unavailable." && error.InnerException is null, "Invalid handle leaked native failure details.");
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var borrowed = new AnonymousPipeClientStream(PipeDirection.In, writer.ClientSafePipeHandle);
        var sourceHandle = borrowed.SafePipeHandle;
        borrowed.Dispose();
        var closed = Throws<IOException>(() =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var unexpected = CancellableStandardInput.DuplicateSynchronousPipe(sourceHandle);
            throw new InvalidOperationException("Disposed pipe handle was admitted.");
        });
        Check(closed.Message == error.Message && closed.InnerException is null, "Disposed source was not sanitized.");
        return Task.CompletedTask;
    }

    private static async Task Join(Task read)
    { try { await read; } catch (OperationCanceledException) { } catch (ObjectDisposedException) { } }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
