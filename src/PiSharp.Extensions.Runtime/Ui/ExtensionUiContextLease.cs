using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime;

internal sealed class ExtensionUiContextLease : IAsyncDisposable
{
    private readonly IExtensionUiScope ui;
    private readonly List<ExtensionUiContextLease> replacements = [];
    private readonly object gate = new();
    private bool closed;
    internal ExtensionContext Context { get; }
    internal ExtensionUiContextLease(ExtensionContext context, IExtensionUiProvider? provider)
    {
        Context = context;
        ui = provider?.OpenScope(context) ?? new UnavailableExtensionUiScope(context);
        context.SetUi(ui);
    }
    internal void OwnReplacement(ExtensionUiContextLease replacement)
    {
        lock (gate)
        {
            if (closed) throw new InvalidOperationException("Originating callback scope has closed.");
            if (replacements.Count >= 128) throw new InvalidOperationException("Callback replacement limit reached.");
            replacements.Add(replacement);
        }
    }
    public async ValueTask DisposeAsync()
    {
        var failures = new List<Exception>();
        ExtensionUiContextLease[] children;
        lock (gate) { closed = true; children = replacements.ToArray(); }
        if (Context.SessionActions is { } actions)
            try { await actions.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        foreach (var replacement in children)
            try { await replacement.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { await ui.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        if (failures.Count != 0) throw new AggregateException(failures);
    }
}

/// <summary>Explicit headless mode binding, without a renderer or remote confirmation service.</summary>
public sealed class UnavailableExtensionUiProvider : IExtensionUiProvider
{
    private readonly ExtensionUiCapabilities capabilities;
    public UnavailableExtensionUiProvider(ExtensionUiMode mode = ExtensionUiMode.Print)
    { if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode)); capabilities = new(mode, 0, 0, []); }
    public IExtensionUiScope OpenScope(IExtensionContext context)
    { ArgumentNullException.ThrowIfNull(context); return new UnavailableExtensionUiScope(context, capabilities); }
}

internal sealed class UnavailableExtensionUiScope(IExtensionContext? context = null, ExtensionUiCapabilities? capabilities = null) : IExtensionUiScope
{
    private int closed;
    internal static IExtensionUi Default { get; } = new UnavailableExtensionUiScope();
    public ExtensionUiCapabilities Capabilities => capabilities ?? ExtensionUiCapabilities.NoUi;
    private ValueTask<ExtensionUiOutcome<T>> Result<T>(CancellationToken token) => ValueTask.FromResult(Volatile.Read(ref closed) != 0
        ? ExtensionUiOutcome<T>.Unavailable(ExtensionUiUnavailableReason.StaleContext) : token.IsCancellationRequested ||
        context is not null && (context.OperationCancellationToken.IsCancellationRequested || context.SessionCancellationToken.IsCancellationRequested || context.ExtensionLifetimeCancellationToken.IsCancellationRequested)
        ? ExtensionUiOutcome<T>.Cancelled() : ExtensionUiOutcome<T>.Unavailable(ExtensionUiUnavailableReason.NoUi));
    public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices,
        ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Result<string>(cancellationToken);
    public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message,
        ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Result<bool>(cancellationToken);
    public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null,
        ExtensionUiDialogOptions? options = null, CancellationToken cancellationToken = default) => Result<string>(cancellationToken);
    public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null,
        CancellationToken cancellationToken = default) => Result<string>(cancellationToken);
    public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification,
        CancellationToken cancellationToken = default) => Result<ExtensionUiPublication>(cancellationToken);
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref closed, 1); return ValueTask.CompletedTask; }
}
