using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using PiSharp.Tui;

internal static class TerminalLeaseTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases()
    {
        yield return ("terminal-lease.pre-effect-option-and-cancellation-admission", Admission);
        yield return ("terminal-lease.actual-pipe-and-disposed-handle-rejection-releases-reservation", NonConsole);
    }
    private static async Task Admission()
    {
        foreach (var options in new TerminalLeaseOptions[] { new(MaximumReadCharacters: 0), new(MaximumReadCharacters: 65_537),
            new(MaximumWriteCharacters: 0), new(MaximumWriteCharacters: 1_048_577) })
            await Failure(TerminalFailure.InvalidOptions, () => WindowsConsoleTerminal.OpenAsync(options).AsTask());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await WindowsConsoleTerminal.OpenAsync(token: cancellation.Token); }
        catch (OperationCanceledException error) { if (error.CancellationToken != cancellation.Token) throw new Exception("Caller cancellation token changed."); return; }
        throw new Exception("Precancelled terminal acquisition succeeded.");
    }
    private static async Task NonConsole()
    {
        using var invalid = new SafeFileHandle(IntPtr.Zero, false);
        if (!OperatingSystem.IsWindows())
        { await Failure(TerminalFailure.UnsupportedPlatform, () => WindowsConsoleTerminal.OpenBorrowedHandlesAsync(invalid, invalid).AsTask()); return; }
        for (var attempt = 0; attempt < 2; attempt++)
            await Failure(TerminalFailure.NotConsole, () => WindowsConsoleTerminal.OpenBorrowedHandlesAsync(invalid, invalid).AsTask());
        using var disposed = new SafeFileHandle(new IntPtr(123), false); disposed.Dispose();
        await Failure(TerminalFailure.NotConsole, () => WindowsConsoleTerminal.OpenBorrowedHandlesAsync(disposed, invalid).AsTask());
        using var input = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var output = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var borrowedInput = new SafeFileHandle(input.SafePipeHandle.DangerousGetHandle(), false);
        using var borrowedOutput = new SafeFileHandle(output.SafePipeHandle.DangerousGetHandle(), false);
        for (var attempt = 0; attempt < 2; attempt++)
            await Failure(TerminalFailure.NotConsole, () => WindowsConsoleTerminal.OpenBorrowedHandlesAsync(borrowedInput, borrowedOutput).AsTask());
        if (input.SafePipeHandle.IsClosed || output.SafePipeHandle.IsClosed) throw new Exception("Rejected lease closed caller pipe handles.");
    }
    private static async Task Failure(TerminalFailure expected, Func<Task> operation)
    { try { await operation(); } catch (TerminalException error) { if (error.Failure != expected) throw new Exception("Terminal failure differs."); return; } throw new Exception("Expected terminal failure."); }
}
