using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using PiSharp.Contracts;

namespace PiSharp.Extensions.Runtime;

/// <summary>Admitted provider catalog plus real native owner-leased stream dispatch. Application selection
/// and protocol/config/credential adapters are explicit host dependencies, not inferred capabilities.</summary>
public sealed class ExtensionProviderRegistrationHost : IDisposable
{
    private readonly object gate = new();
    private readonly ExtensionRegistry registry;
    private readonly IExtensionProviderConfigurationAdapter configuration;
    private readonly ImmutableArray<ExtensionProviderDefinition> builtins;
    private readonly Dictionary<RegistrationScope, Owner> owners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, Entry> current = new(StringComparer.Ordinal);
    private bool disposed;
    internal ExtensionRegistry Registry => registry;
    public ExtensionProviderRegistrationHost(ExtensionRegistry registry, IEnumerable<ExtensionProviderDefinition> admittedBuiltins,
        IExtensionProviderConfigurationAdapter admittedConfigurationAdapter)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        configuration = admittedConfigurationAdapter ?? throw new ArgumentNullException(nameof(admittedConfigurationAdapter));
        ArgumentNullException.ThrowIfNull(admittedBuiltins);
        var builder = ImmutableArray.CreateBuilder<ExtensionProviderDefinition>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in admittedBuiltins)
        {
            if (builder.Count == 128) throw new ArgumentException("Built-in provider bound exceeded.");
            Validate(item); if (!names.Add(item.Name)) throw new ArgumentException("Duplicate built-in provider."); builder.Add(item);
        }
        builtins = builder.ToImmutable();
    }
    public IExtensionProviderRegistrationFacade Bind(IExtensionRegistry registration)
    {
        if (registration is not RegistrationScope scope || !ReferenceEquals(scope.Registry, registry)) throw new ArgumentException("Actual registry owner required.");
        lock (gate)
        {
            Available(scope); if (owners.TryGetValue(scope, out var existing)) return existing;
            if (owners.Count == 128) throw new InvalidOperationException("Provider owner bound exceeded.");
            var owner = new Owner(this, scope); owners.Add(scope, owner);
            owner.Lifetime = scope.ExtensionLifetimeCancellationToken.UnsafeRegister(static state =>
            { var value = (Owner)state!; value.Host.Retire(value); }, owner);
            return owner;
        }
    }
    public void CommitOwnerProviders(RegistrationScope scope)
    {
        lock (gate)
        {
            Available(scope);
            if (scope.State != RegistrationScopeState.Active || !owners.TryGetValue(scope, out var owner)) throw new InvalidOperationException("Activated provider owner required.");
            if (owner.Committed) return;
            foreach (var entry in owner.Providers.Values) EnsurePublished(entry);
            foreach (var entry in owner.Providers.Values) current[entry.Definition.Name] = entry;
            owner.Committed = true;
        }
    }
    public ImmutableArray<ExtensionProviderModel> CaptureModels()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var effective = builtins.ToDictionary(x => x.Name, StringComparer.Ordinal);
            foreach (var pair in current) if (Published(pair.Value)) effective[pair.Key] = pair.Value.Definition;
            return effective.OrderBy(pair => pair.Key, StringComparer.Ordinal).SelectMany(pair => pair.Value.Models).ToImmutableArray();
        }
    }
    public IAsyncEnumerable<StreamEvent> StreamAsync(ExtensionProviderStreamRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request); token.ThrowIfCancellationRequested();
        if (request.Messages.IsDefault || request.Messages.Length > 4096 || request.Model is null) throw new ArgumentException("Invalid bounded provider request.");
        long bytes = 0;
        foreach (var message in request.Messages)
        {
            if (message is null || message.WireBody is null || message.Role is not { Length: > 0 and <= 128 }) throw new ArgumentException("Invalid provider transcript.");
            bytes += System.Text.Encoding.UTF8.GetByteCount(message.WireBody.ToString());
            if (bytes > 1048576) throw new ArgumentException("Provider transcript byte bound exceeded.");
        }
        if (request.Options is { } options && options.ToString().Length > 1048576) throw new ArgumentException("Provider options bound exceeded.");
        return StreamCore(request, token);
    }
    private async IAsyncEnumerable<StreamEvent> StreamCore(ExtensionProviderStreamRequest request, [EnumeratorCancellation] CancellationToken token)
    {
        Entry? selected; ExtensionProviderDefinition definition;
        ImmutableArray<(RegistrationScope Scope, RegistrationEntry Entry)> admission = default;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (current.TryGetValue(request.Model.Provider, out selected))
            {
                EnsurePublished(selected); definition = selected.Definition;
                if (!definition.Models.Any(x => x.Model == request.Model)) throw new ArgumentException("Unknown provider model identity.");
                admission = registry.AdmitProviderMarker(selected.Owner.Scope, selected.Marker, selected.Topic, token);
            }
            else
            {
                definition = builtins.FirstOrDefault(x => x.Name == request.Model.Provider) ?? throw new ArgumentException("Unknown provider.");
                if (!definition.Models.Any(x => x.Model == request.Model)) throw new ArgumentException("Unknown built-in model identity.");
            }
        }
        var errors = new List<Exception>(); IAsyncEnumerator<StreamEvent>? iterator = null;
        ExtensionUiContextLease? context = null; CancellationTokenSource? linked = null;
        try
        {
            try
            {
                using var factoryFrame = selected is null ? null : new CallbackFrame(selected.Owner.Scope);
                if (selected is not null)
                {
                    linked = CancellationTokenSource.CreateLinkedTokenSource(token, selected.Owner.Scope.ExtensionLifetimeCancellationToken);
                    var originalContext = registry.CreateUiContextAsync(selected.Owner.Scope, linked.Token, default).AsTask();
                    context = await AwaitOriginal(originalContext, "context", linked.Token).ConfigureAwait(false);
                }
                var owned = linked?.Token ?? token;
                iterator = definition.StreamSimple(request, context?.Context, owned)?.GetAsyncEnumerator(owned) ??
                    throw new InvalidOperationException("Provider returned no stream.");
            }
            catch (Exception error) { errors.Add(error is ExtensionProviderOriginalFaultException or ExtensionProviderCanceledOriginalException ? error : new ExtensionProviderOriginalFaultException("create", null, error)); }
            for (var count = 0; iterator is not null && errors.Count == 0; count++)
            {
                bool moved = false; StreamEvent? value = null; Task<bool>? original = null;
                try
                {
                    using var callbackFrame = selected is null ? null : new CallbackFrame(selected.Owner.Scope);
                    original = iterator.MoveNextAsync().AsTask();
                    moved = await AwaitOriginal(original, "move-next", linked?.Token ?? token).ConfigureAwait(false);
                    if (moved)
                    {
                        if (count >= 65536) throw new InvalidOperationException("Provider stream frame bound exceeded.");
                        try { value = iterator.Current ?? throw new InvalidOperationException("Provider emitted no frame."); }
                        catch (Exception error) { throw new ExtensionProviderOriginalFaultException("current", null, error); }
                    }
                }
                catch (Exception error) { errors.Add(original is null ? new ExtensionProviderOriginalFaultException("move-next", null, error) : error); }
                if (!moved || errors.Count != 0) break;
                yield return value!;
            }
        }
        finally
        {
            if (iterator is not null)
            {
                Task? original = null;
                try { using var disposeFrame = selected is null ? null : new CallbackFrame(selected.Owner.Scope); original = iterator.DisposeAsync().AsTask(); await AwaitOriginal(original, "dispose", linked?.Token ?? token).ConfigureAwait(false); }
                catch (Exception error) { errors.Add(original is null ? new ExtensionProviderOriginalFaultException("dispose", null, error) : error); }
            }
            if (context is not null)
            {
                Task? original = null;
                try { using var disposeFrame = selected is null ? null : new CallbackFrame(selected.Owner.Scope); original = context.DisposeAsync().AsTask(); await AwaitOriginal(original, "context-dispose", linked?.Token ?? token).ConfigureAwait(false); }
                catch (Exception error) { errors.Add(original is null ? new ExtensionProviderOriginalFaultException("context-dispose", null, error) : error); }
            }
            linked?.Dispose();
            if (!admission.IsDefault) registry.ReleaseAdmission(admission);
            Throw(errors);
        }
    }
    private static async Task<T> AwaitOriginal<T>(Task<T> original, string phase, CancellationToken owned)
    {
        try { return await original.ConfigureAwait(false); }
        catch (OperationCanceledException error) when (original.IsCanceled && owned.IsCancellationRequested && error.CancellationToken == owned) { throw new ExtensionProviderCanceledOriginalException(original, error); }
        catch (Exception error) { throw new ExtensionProviderOriginalFaultException(phase, original, original.IsFaulted ? original.Exception! : error); }
    }
    private static async Task AwaitOriginal(Task original, string phase, CancellationToken owned)
    {
        try { await original.ConfigureAwait(false); }
        catch (OperationCanceledException error) when (original.IsCanceled && owned.IsCancellationRequested && error.CancellationToken == owned) { throw new ExtensionProviderCanceledOriginalException(original, error); }
        catch (Exception error) { throw new ExtensionProviderOriginalFaultException(phase, original, original.IsFaulted ? original.Exception! : error); }
    }
    private static void Throw(List<Exception> errors)
    { if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw(); if (errors.Count > 1) throw new AggregateException(errors); }
    private bool Published(Entry entry) => registry.CaptureSnapshot().Registrations.Any(x =>
        x.OwnerId == entry.Owner.OwnerId && x.OwnerGeneration == entry.Owner.OwnerGeneration && x.RegistrationId == entry.Marker.RegistrationId);
    private void EnsurePublished(Entry entry) { if (!Published(entry)) throw new InvalidOperationException("Provider owner metadata is not active."); }
    private void Available(RegistrationScope scope)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (scope.ExtensionLifetimeCancellationToken.IsCancellationRequested || scope.State is not (RegistrationScopeState.Initializing or RegistrationScopeState.Active))
            throw new ObjectDisposedException(nameof(IExtensionProviderRegistrationFacade));
    }
    private static void Validate(ExtensionProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.Name is not { Length: > 0 and <= 128 } || provider.Models.IsDefaultOrEmpty || provider.Models.Length > 256 ||
            provider.StreamSimple is null || provider.StreamSimple.GetInvocationList().Length != 1) throw new ArgumentException("Invalid bounded provider definition.");
        var identities = new HashSet<ModelDescriptor>(); long bytes = 0;
        foreach (var item in provider.Models)
        {
            if (item is null || item.Model is null || item.Model.Provider != provider.Name || item.Model.Id is not { Length: > 0 and <= 128 } ||
                item.Model.Api is not { Length: > 0 and <= 128 } || item.Metadata is null || item.Metadata.Value.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !identities.Add(item.Model)) throw new ArgumentException("Invalid provider model identity/metadata.");
            var metadata = item.Metadata.Value;
            foreach (var pair in new[] { ("id", item.Model.Id), ("api", item.Model.Api), ("provider", item.Model.Provider) })
                if (!metadata.TryGetProperty(pair.Item1, out var field) || field.ValueKind != System.Text.Json.JsonValueKind.String || field.GetString() != pair.Item2)
                    throw new ArgumentException("Provider metadata differs from its exact model identity.");
            bytes += System.Text.Encoding.UTF8.GetByteCount(item.Metadata.ToString());
            if (bytes > 1048576) throw new ArgumentException("Provider model metadata byte bound exceeded.");
        }
    }
    private void Retire(Owner owner)
    {
        lock (gate)
        {
            if (!owners.Remove(owner.Scope)) return;
            foreach (var entry in owner.Providers.Values)
            {
                if (current.TryGetValue(entry.Definition.Name, out var winner) && ReferenceEquals(winner, entry)) current.Remove(entry.Definition.Name);
                entry.Marker.Dispose();
            }
            owner.Providers.Clear();
        }
    }
    public void Dispose()
    {
        Owner[] retained; lock (gate) { if (disposed) return; disposed = true; retained = owners.Values.ToArray(); foreach (var owner in retained) Retire(owner); }
        foreach (var owner in retained) owner.Lifetime.Dispose();
    }
    private sealed record Entry(Owner Owner, ExtensionProviderDefinition Definition, IExtensionRegistration Marker, string Topic);
    private sealed class Owner(ExtensionProviderRegistrationHost host, RegistrationScope scope) : IExtensionProviderRegistrationFacade
    {
        internal ExtensionProviderRegistrationHost Host => host;
        internal RegistrationScope Scope => scope;
        internal Dictionary<string, Entry> Providers { get; } = new(StringComparer.Ordinal);
        internal CancellationTokenRegistration Lifetime;
        internal bool Committed;
        public string OwnerId => scope.OwnerId;
        public long OwnerGeneration => scope.OwnerGeneration;
        public void RegisterProvider(string name, JsonData configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            if (name is not { Length: > 0 and <= 128 } || configuration.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new ArgumentException("Bounded provider name and configuration object required.");
            if (System.Text.Encoding.UTF8.GetByteCount(configuration.ToString()) > 1048576) throw new ArgumentException("Provider config bound exceeded.");
            lock (host.gate) host.Available(scope);
            var admitted = host.configuration.Resolve(name, configuration);
            ArgumentNullException.ThrowIfNull(admitted);
            if (admitted.Name != name) throw new ArgumentException("Configuration adapter changed provider identity.");
            RegisterProvider(admitted);
        }
        public void RegisterProvider(ExtensionProviderDefinition provider)
        {
            Validate(provider);
            lock (host.gate)
            {
                host.Available(scope);
                if (!host.owners.ContainsKey(scope)) throw new ObjectDisposedException(nameof(IExtensionProviderRegistrationFacade));
                if (!Providers.ContainsKey(provider.Name) && Providers.Count == 128) throw new InvalidOperationException("Owner provider bound exceeded.");
                Providers.TryGetValue(provider.Name, out var previous);
                var topic = previous?.Topic ?? "provider-" + Guid.NewGuid().ToString("N");
                var marker = previous?.Marker ?? scope.Observe(new(topic, topic, static (_, _, _) => ValueTask.CompletedTask));
                var entry = new Entry(this, provider, marker, topic);
                // Definition replacement snapshots the new callback without temporarily charging another
                // native entry. Already admitted streams retain the old immutable definition and lease.
                Providers[provider.Name] = entry;
                if (Committed) host.current[provider.Name] = entry;
            }
        }
        public void UnregisterProvider(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            lock (host.gate)
            {
                host.Available(scope);
                if (!Providers.Remove(name, out var entry)) return;
                if (host.current.TryGetValue(name, out var winner) && ReferenceEquals(winner, entry)) host.current.Remove(name);
                entry.Marker.Dispose();
            }
        }
    }
}
