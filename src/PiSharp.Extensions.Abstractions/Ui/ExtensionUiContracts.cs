using System.Collections.Immutable;

namespace PiSharp.Extensions;

public enum ExtensionUiMode { Print, Json, Rpc, Tui }
public enum ExtensionUiFeature { Select, Confirm, Input, Editor, Notify, Status, TextWidget, Title, EditorText, CustomTerminalComponent }
public enum ExtensionUiOutcomeKind { Value, Cancelled, TimedOut, Unavailable }
public enum ExtensionUiUnavailableReason { NoUi, NotConnected, Disconnected, StaleContext, UnsupportedCapability, InvalidResponse, ResourceLimit, OutputFailed }
public enum ExtensionUiPublication { Published }
public enum ExtensionUiNotifyKind { Info, Warning, Error }
public enum ExtensionUiWidgetPlacement { AboveEditor, BelowEditor }

/// <summary>Offered capabilities, not proof that the remote client renders them. Generation is connection/session identity, not an Agent turn.</summary>
public sealed record ExtensionUiCapabilities(ExtensionUiMode Mode, long ConnectionGeneration,
    long SessionGeneration, ImmutableArray<ExtensionUiFeature> Features)
{
    public bool Supports(ExtensionUiFeature feature) => !Features.IsDefault && Features.Contains(feature);
    public static ExtensionUiCapabilities NoUi { get; } = new(ExtensionUiMode.Print, 0, 0, []);
}

/// <summary>A non-value outcome has no readable default value. In particular it cannot supply approval.</summary>
public sealed class ExtensionUiOutcome<T>
{
    private readonly T? value;
    public ExtensionUiOutcomeKind Kind { get; }
    public ExtensionUiUnavailableReason? UnavailableReason { get; }
    public T Value => Kind == ExtensionUiOutcomeKind.Value ? value! : throw new InvalidOperationException("UI outcome has no value.");
    private ExtensionUiOutcome(ExtensionUiOutcomeKind kind, T? value = default, ExtensionUiUnavailableReason? reason = null)
    { Kind = kind; this.value = value; UnavailableReason = reason; }
    public static ExtensionUiOutcome<T> FromValue(T value)
    { ArgumentNullException.ThrowIfNull(value); return new(ExtensionUiOutcomeKind.Value, value); }
    public static ExtensionUiOutcome<T> Cancelled() => new(ExtensionUiOutcomeKind.Cancelled);
    public static ExtensionUiOutcome<T> TimedOut() => new(ExtensionUiOutcomeKind.TimedOut);
    public static ExtensionUiOutcome<T> Unavailable(ExtensionUiUnavailableReason reason) => new(ExtensionUiOutcomeKind.Unavailable, reason: reason);
}

/// <summary>Milliseconds preserve the source optional numeric field; zero disables its timer. Editor has no source timeout field.</summary>
public sealed record ExtensionUiDialogOptions(double? TimeoutMilliseconds = null);
public abstract record ExtensionUiNotification;
public sealed record ExtensionUiNotify(string Message, ExtensionUiNotifyKind? Kind = null) : ExtensionUiNotification;
public sealed record ExtensionUiStatus(string Key, string? Text) : ExtensionUiNotification;
public sealed record ExtensionUiTextWidget(string Key, ImmutableArray<string>? Lines,
    ExtensionUiWidgetPlacement? Placement = null) : ExtensionUiNotification;
public sealed record ExtensionUiTitle(string Title) : ExtensionUiNotification;
public sealed record ExtensionUiEditorText(string Text) : ExtensionUiNotification;

public interface IExtensionUi
{
    ExtensionUiCapabilities Capabilities { get; }
    ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices,
        ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default);
    ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message,
        ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default);
    ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null,
        ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default);
    ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null,
        CancellationToken cancellationToken = default);
    ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional discoverable context feature; existing extension callback interfaces remain unchanged.</summary>
public interface IExtensionUiContext : IExtensionContext { IExtensionUi Ui { get; } }
/// <summary>Host-owned scope. Plugins see IExtensionUi through their context, not this cleanup interface.</summary>
public interface IExtensionUiScope : IExtensionUi, IAsyncDisposable { }
public interface IExtensionUiProvider { IExtensionUiScope OpenScope(IExtensionContext context); }

public static class ExtensionUiSourceDefaults
{
    public static bool Confirmation(ExtensionUiOutcome<bool> outcome) =>
        outcome.Kind == ExtensionUiOutcomeKind.Value && outcome.Value;
    public static string? Text(ExtensionUiOutcome<string> outcome) =>
        outcome.Kind == ExtensionUiOutcomeKind.Value ? outcome.Value : null;
}
