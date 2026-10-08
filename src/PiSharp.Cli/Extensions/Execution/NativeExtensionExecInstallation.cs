using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Execution;
using PiSharp.Extensions.Runtime;
using PiSharp.Tools.Processes;

namespace PiSharp.Cli.Extensions.Execution;

/// <summary>One explicitly supplied native registry installation. Decorates its genuine initializer;
/// caller binders receive an optional capability, never a replacement registry or process grant.</summary>
public sealed class NativeExtensionExecInstallation
{
    private readonly ExtensionRegistry registry;
    private readonly ISeparatedProcessRunner runner;
    private readonly Func<NativeExtensionExecInvocation, CancellationToken, ValueTask<ProcessRequest>> admission;
    private readonly Action<IExtensionRegistry, IExtensionExecFacade> binder;
    private readonly string workingDirectory;
    private int decorated;
    private Installed? installed;
    public NativeExtensionExecOriginals Originals { get; } = new();
    public NativeExtensionExecInstallation(ExtensionRegistry registry, ISeparatedProcessRunner runner,
        Func<NativeExtensionExecInvocation, CancellationToken, ValueTask<ProcessRequest>> admission,
        string workingDirectory, Action<IExtensionRegistry, IExtensionExecFacade> binder)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(admission); ArgumentNullException.ThrowIfNull(binder);
        if (admission.GetInvocationList().Length != 1 || binder.GetInvocationList().Length != 1)
            throw new ArgumentException("One admission and initializer binder required.");
        if (!Path.IsPathFullyQualified(workingDirectory)) throw new ArgumentException("Absolute admitted cwd required.");
        this.registry = registry; this.runner = runner; this.admission = admission;
        this.workingDirectory = workingDirectory; this.binder = binder;
    }
    public bool IsBoundTo(ExtensionRegistry candidate) => ReferenceEquals(registry, candidate);
    internal IPiSharpExtension Decorate(ExtensionRegistry actualRegistry, IPiSharpExtension original)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!IsBoundTo(actualRegistry)) throw new InvalidOperationException("Exec installation belongs to another registry.");
        if (Interlocked.Exchange(ref decorated, 1) != 0) throw new InvalidOperationException("One genuine plugin initializer required.");
        return installed = new Installed(this, original);
    }
    internal NativeExtensionExecHost ResolveContext(PiSharp.Extensions.IExtensionCommandContext context)
    {
        var active = installed?.ResolveActive() ?? throw new NotSupportedException("No initialized execution owner.");
        if (active.OwnerId != context.OwnerId || active.OwnerGeneration != context.OwnerGeneration ||
            active.OwnerLifetimeCancellationToken != context.ExtensionLifetimeCancellationToken)
            throw new InvalidOperationException("Execution requires the exact installed owner generation and lifetime.");
        return active;
    }
    private sealed class Installed(NativeExtensionExecInstallation installation, IPiSharpExtension original) : IPiSharpExtension
    {
        private readonly object gate = new();
        private NativeExtensionExecHost? host;
        private Task? disposal;
        private int initialized;
        private bool retiring;
        internal NativeExtensionExecHost ResolveActive()
        { lock (gate) return !retiring && host is not null ? host : throw new ObjectDisposedException("Native execution context owner"); }
        public async ValueTask InitializeAsync(IExtensionRegistry native, CancellationToken token)
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0 || native is not RegistrationScope)
                throw new InvalidOperationException("One actual native owner initialization required.");
            token.ThrowIfCancellationRequested();
            lock (gate) host = new(native, installation.runner, installation.admission, installation.workingDirectory, installation.Originals);
            Task? initialization = null;
            try
            {
                installation.binder(native, host);
                initialization = original.InitializeAsync(native, token).AsTask();
                await initialization.ConfigureAwait(false);
                installation.Originals.Record("actual-plugin-initialization", initialization, null);
                token.ThrowIfCancellationRequested();
            }
            catch (Exception error)
            {
                lock (gate) retiring = true;
                var failures = new List<Exception> { initialization is null
                    ? new NativeExtensionExecFault("synchronous initializer binder or plugin", null, error)
                    : installation.Originals.Record("actual-plugin-initialization", initialization, error) ?? error };
                try { await installation.Originals.Join("failed-initialization-exec-close", host.RetireOwnedAsync().AsTask(), failures).ConfigureAwait(false); }
                catch (Exception cleanup) { failures.Add(cleanup); }
                NativeExtensionExecOriginals.Throw(failures);
            }
        }
        public ValueTask DisposeAsync()
        { lock (gate) { retiring = true; return new(disposal ??= CloseAsync()); } }
        private async Task CloseAsync()
        {
            var failures = new List<Exception>();
            if (host is not null)
            {
                try { await installation.Originals.Join("actual-exec-host-close", host.RetireOwnedAsync().AsTask(), failures).ConfigureAwait(false); }
                catch (Exception error) { failures.Add(error); }
            }
            Task? cleanup = null;
            try { cleanup = original.DisposeAsync().AsTask(); await cleanup.ConfigureAwait(false); installation.Originals.Record("actual-plugin-disposal", cleanup, null); }
            catch (Exception error)
            { failures.Add(cleanup is null ? new NativeExtensionExecFault("synchronous plugin disposal", null, error) : installation.Originals.Record("actual-plugin-disposal", cleanup, error)!); }
            NativeExtensionExecOriginals.Throw(failures);
        }
    }
}
