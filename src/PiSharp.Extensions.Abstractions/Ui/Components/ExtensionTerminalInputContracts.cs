namespace PiSharp.Extensions;

/// <summary>Null passes input unchanged. Consume wins over Data; an empty final Data suppresses forwarding.</summary>
public sealed record ExtensionTerminalInputResult(bool Consume = false, string? Data = null);
public delegate ExtensionTerminalInputResult? ExtensionTerminalInputHandler(string data);
/// <summary>Native asynchronous adaptation. The scope joins the actual returned task before retiring.</summary>
public delegate Task<ExtensionTerminalInputResult?> ExtensionTerminalInputAsyncHandler(
    string data, IExtensionContext context, CancellationToken cancellationToken);

public interface IExtensionTerminalInput
{
    IDisposable OnTerminalInput(ExtensionTerminalInputHandler handler);
    IDisposable OnTerminalInputAsync(ExtensionTerminalInputAsyncHandler handler);
}

/// <summary>Optional host-admitted UI feature; presence is not inferred from a TUI enum value.</summary>
public interface IExtensionTerminalInputContext : IExtensionContext
{
    IExtensionTerminalInput TerminalInput { get; }
}

public enum ExtensionTerminalInputDisposition { Forward, Consumed, Cancelled, Unavailable }
/// <summary>Only Forward carries downstream input. Other outcomes grant no terminal effect.</summary>
public sealed class ExtensionTerminalInputOutcome
{
    private readonly string? data;
    public ExtensionTerminalInputDisposition Disposition { get; }
    public ExtensionUiUnavailableReason? UnavailableReason { get; }
    public string Data => Disposition == ExtensionTerminalInputDisposition.Forward ? data! :
        throw new InvalidOperationException("Terminal input outcome cannot be forwarded.");
    private ExtensionTerminalInputOutcome(ExtensionTerminalInputDisposition disposition,
        string? data = null, ExtensionUiUnavailableReason? reason = null)
    { Disposition = disposition; this.data = data; UnavailableReason = reason; }
    public static ExtensionTerminalInputOutcome Forward(string data)
    { ArgumentNullException.ThrowIfNull(data); return new(ExtensionTerminalInputDisposition.Forward, data); }
    public static ExtensionTerminalInputOutcome Consumed() => new(ExtensionTerminalInputDisposition.Consumed);
    public static ExtensionTerminalInputOutcome Cancelled() => new(ExtensionTerminalInputDisposition.Cancelled);
    public static ExtensionTerminalInputOutcome Unavailable(ExtensionUiUnavailableReason reason) =>
        new(ExtensionTerminalInputDisposition.Unavailable, reason: reason);
}
