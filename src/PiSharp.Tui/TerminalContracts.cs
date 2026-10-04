namespace PiSharp.Tui;

public sealed record TerminalLeaseOptions(int MaximumReadCharacters = 4096, int MaximumWriteCharacters = 65_536)
{
    /// <summary>Explicit full-screen host mode: each viewport/write borrows an owned handle to the active console buffer.
    /// The acquisition-time duplicate continues to own exact original-state restoration.</summary>
    public bool FollowActiveScreenBuffer { get; init; }
}
public sealed record TerminalConsoleState(uint InputMode, uint OutputMode, uint InputCodePage, uint OutputCodePage,
    uint CursorSize, bool CursorVisible, short CursorColumn, short CursorRow);
public sealed record TerminalLeaseSnapshot(TerminalConsoleState Original, TerminalConsoleState Acquired,
    TerminalConsoleState? Restored, bool IsClosing, bool RestorationConfirmed, int ActiveReads, int ActiveWrites,
    long ReadWorkersStarted, long ReadWorkersSettled, long WriteWorkersStarted, long WriteWorkersSettled);
public enum TerminalFailure { InvalidOptions, UnsupportedPlatform, NotConsole, AlreadyOwned, NativeIoFailed, RestorationFailed }
public sealed class TerminalException : IOException
{
    public TerminalFailure Failure { get; }
    public TerminalException(TerminalFailure failure) : base(failure switch
    {
        TerminalFailure.InvalidOptions => "Terminal limits are invalid.", TerminalFailure.UnsupportedPlatform => "The Windows console profile is unavailable on this platform.",
        TerminalFailure.NotConsole => "Terminal input and output require verified console handles.", TerminalFailure.AlreadyOwned => "This process already owns a terminal console lease.",
        TerminalFailure.RestorationFailed => "Terminal cleanup could not confirm exact console restoration.", _ => "Terminal native input or output failed."
    }) => Failure = failure;
}

[Flags] public enum TerminalModifiers { None = 0, Shift = 1, Alt = 2, Control = 4, Super = 8 }
public enum TerminalKeyAction { Press, Repeat, Release }
public abstract record TerminalInputEvent;
public sealed record TerminalText(string Text) : TerminalInputEvent;
public sealed record TerminalKey(string Key, TerminalModifiers Modifiers = TerminalModifiers.None, TerminalKeyAction Action = TerminalKeyAction.Press) : TerminalInputEvent;
public sealed record TerminalPaste(string Text) : TerminalInputEvent;
public sealed record TerminalProtocol(string Sequence) : TerminalInputEvent;
public sealed record TerminalUnknownSequence(string Sequence) : TerminalInputEvent;
public sealed record TerminalInputDecoderOptions(int MaximumSequenceCharacters = 4096, int MaximumPasteCharacters = 65_536,
    int MaximumChunkCharacters = 4096, TimeSpan? EscapeTimeout = null, TimeSpan? SequenceTimeout = null);
public enum TerminalInputFailure { InvalidOptions, ResourceLimit, InvalidUnicode, IncompleteInput, Closed }
public sealed class TerminalInputException : Exception
{
    public TerminalInputFailure Failure { get; }
    public TerminalInputException(TerminalInputFailure failure) : base(failure switch
    {
        TerminalInputFailure.InvalidOptions => "Terminal input limits or timeouts are invalid.", TerminalInputFailure.ResourceLimit => "Terminal input exceeds its bounded profile.",
        TerminalInputFailure.InvalidUnicode => "Terminal input contains invalid UTF-16.",
        TerminalInputFailure.IncompleteInput => "Terminal input ended inside bracketed paste.", _ => "Terminal input decoder is closed."
    }) => Failure = failure;
}

/// <summary>One exclusive Unicode console lease. Output is trusted terminal transport, not a data renderer.</summary>
public interface IConsoleTerminal : IAsyncDisposable
{
    TerminalLeaseSnapshot Snapshot { get; }
    ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default);
    ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default);
}

/// <summary>The actual window and buffer dimensions of the lease-owned output handle.</summary>
public sealed record TerminalViewport(int Columns, int Rows, int Left, int Top, int BufferColumns, int BufferRows);
public interface ITerminalViewportSource
{
    TerminalViewport ReadViewport();
}
