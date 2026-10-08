using System.Collections.Immutable;

namespace PiSharp.Extensions.Runtime;

/// <summary>A bounded host value dependency with first-default-wins and explicit host override updates.
/// It is not the CLI parser. A host must call CommitOwnerFlags after the actual ActivateAsync succeeds.</summary>
public sealed class ExtensionHostFlagValues : IExtensionHostFlagValues
{
    private readonly object gate = new();
    private readonly Dictionary<string, ExtensionFlagValue> values = new(StringComparer.Ordinal);
    public ExtensionHostFlagValues(IReadOnlyDictionary<string, ExtensionFlagValue> admittedValues)
    {
        ArgumentNullException.ThrowIfNull(admittedValues);
        foreach (var pair in admittedValues)
        {
            if (values.Count == 1_024) throw new ArgumentException("Flag value budget exceeded.", nameof(admittedValues));
            Validate(pair.Key, pair.Value); values.Add(pair.Key, pair.Value);
        }
    }
    public bool TryGetValue(string name, out ExtensionFlagValue? value)
    { lock (gate) return values.TryGetValue(name, out value); }
    public void SetHostValue(string name, ExtensionFlagValue value)
    {
        Validate(name, value);
        lock (gate)
        {
            if (!values.ContainsKey(name) && values.Count == 1_024) throw new InvalidOperationException("Flag value budget exceeded.");
            values[name] = value;
        }
    }
    public void CommitDefaults(ImmutableArray<KeyValuePair<string, ExtensionFlagValue>> defaults)
    {
        if (defaults.IsDefault || defaults.Length > 128) throw new ArgumentException("Invalid flag default batch.", nameof(defaults));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in defaults)
        { Validate(pair.Key, pair.Value); if (!names.Add(pair.Key)) throw new ArgumentException("Duplicate flag default.", nameof(defaults)); }
        lock (gate)
        {
            if (values.Count + defaults.Count(pair => !values.ContainsKey(pair.Key)) > 1_024)
                throw new InvalidOperationException("Flag value budget exceeded.");
            foreach (var pair in defaults) values.TryAdd(pair.Key, pair.Value);
        }
    }
    internal static void Validate(string name, ExtensionFlagValue value)
    {
        if (name is not { Length: > 0 and <= 128 }) throw new ArgumentException("Invalid bounded flag name.", nameof(name));
        ArgumentNullException.ThrowIfNull(value);
        if (value.Kind == ExtensionFlagKind.String && value.StringValue!.Length > 4_096)
            throw new ArgumentException("Flag value budget exceeded.", nameof(value));
    }
}

/// <summary>Real registry/snapshot/lifetime metadata bridge. Marker observations carry no effects or user callbacks.
/// The explicitly supplied host values are authoritative; provider/model/message and CLI parser wiring are separate.</summary>
public sealed class ExtensionFlagRegistrationHost : IDisposable
{
    private readonly object gate = new();
    private readonly ExtensionRegistry registry;
    private readonly IExtensionHostFlagValues values;
    private readonly Dictionary<RegistrationScope, Owner> owners = new(ReferenceEqualityComparer.Instance);
    private bool disposed;
    public ExtensionFlagRegistrationHost(ExtensionRegistry registry, IExtensionHostFlagValues admittedHostValues)
    { this.registry = registry ?? throw new ArgumentNullException(nameof(registry)); values = admittedHostValues ?? throw new ArgumentNullException(nameof(admittedHostValues)); }

    public IExtensionFlagRegistrationFacade Bind(IExtensionRegistry registration)
    {
        if (registration is not RegistrationScope scope || !ReferenceEquals(scope.Registry, registry))
            throw new ArgumentException("An actual owner of this registry is required.", nameof(registration));
        lock (gate)
        {
            Available(scope, write: true);
            if (owners.TryGetValue(scope, out var existing)) return existing;
            if (owners.Count == 128) throw new InvalidOperationException("Flag owner budget exceeded.");
            var owner = new Owner(this, scope); owners.Add(scope, owner);
            owner.Lifetime = scope.ExtensionLifetimeCancellationToken.UnsafeRegister(static state =>
            { var current = (Owner)state!; current.Host.Retire(current); }, owner);
            return owner;
        }
    }

    /// <summary>Host activation boundary: call only after directly awaiting the actual native ActivateAsync.
    /// Initializer failure or a cancelled owner cannot publish pending defaults.</summary>
    public void CommitOwnerFlags(RegistrationScope scope)
    {
        lock (gate)
        {
            Available(scope, write: true);
            if (scope.State != RegistrationScopeState.Active || !owners.TryGetValue(scope, out var owner))
                throw new InvalidOperationException("An activated bound flag owner is required.");
            if (owner.Committed) return;
            var snapshot = registry.CaptureSnapshot();
            foreach (var flag in owner.Flags.Values)
                if (!Published(snapshot, flag)) throw new InvalidOperationException("Flag metadata was not committed by the actual registry.");
            var defaults = owner.Pending.Where(pair => owner.Flags.ContainsKey(pair.Key)).ToImmutableArray();
            // A synchronous host dependency, not a plugin callback or detached task.
            values.CommitDefaults(defaults);
            owner.Committed = true;
        }
    }

    public ImmutableArray<ExtensionFlagRegistrationInfo> CaptureFlags()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var snapshot = registry.CaptureSnapshot();
            return owners.Values.OrderBy(owner => owner.OwnerGeneration)
                .SelectMany(owner => owner.Flags.Values.OrderBy(flag => flag.Name, StringComparer.Ordinal))
                .Where(flag => Published(snapshot, flag))
                .Select(flag => new ExtensionFlagRegistrationInfo(flag.OwnerId, flag.OwnerGeneration, flag.Name, flag.Options)).ToImmutableArray();
        }
    }
    private static bool Published(ExtensionRegistrySnapshot snapshot, Flag flag) => snapshot.Registrations.Any(row =>
        row.OwnerId == flag.OwnerId && row.OwnerGeneration == flag.OwnerGeneration && row.RegistrationId == flag.RegistrationId);
    private void Available(RegistrationScope scope, bool write)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (scope.ExtensionLifetimeCancellationToken.IsCancellationRequested ||
            scope.State is RegistrationScopeState.Closing or RegistrationScopeState.Disposed ||
            (write && scope.State is not (RegistrationScopeState.Initializing or RegistrationScopeState.Active)))
            throw new ObjectDisposedException(nameof(IExtensionFlagRegistrationFacade));
    }
    private void Retire(Owner owner)
    {
        lock (gate)
        {
            if (!owners.Remove(owner.Scope)) return;
            foreach (var flag in owner.Flags.Values) flag.NativeHandle.Dispose();
            owner.Flags.Clear(); owner.Pending.Clear();
        }
    }
    public void Dispose()
    {
        Owner[] current;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; current = owners.Values.ToArray();
            foreach (var owner in current) Retire(owner);
        }
        // Dispose outside the host lock: a concurrent synchronous lifetime callback can acquire it.
        foreach (var owner in current) owner.Lifetime.Dispose();
    }

    private sealed class Owner(ExtensionFlagRegistrationHost host, RegistrationScope scope) : IExtensionFlagRegistrationFacade
    {
        internal ExtensionFlagRegistrationHost Host { get; } = host;
        internal RegistrationScope Scope { get; } = scope;
        internal Dictionary<string, Flag> Flags { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, ExtensionFlagValue> Pending { get; } = new(StringComparer.Ordinal);
        internal CancellationTokenRegistration Lifetime;
        internal bool Committed;
        public string OwnerId => Scope.OwnerId;
        public long OwnerGeneration => Scope.OwnerGeneration;
        public IExtensionRegistration RegisterFlag(string name, ExtensionFlagOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (name is not { Length: > 0 and <= 128 } || !Enum.IsDefined(options.Type) || options.Description?.Length > 4_096)
                throw new ArgumentException("Invalid bounded flag definition.", nameof(options));
            if (options.DefaultValue is { } supplied)
            {
                ExtensionHostFlagValues.Validate(name, supplied);
                if (supplied.Kind != options.Type) throw new ArgumentException("Flag default type differs from its definition.", nameof(options));
            }
            lock (Host.gate)
            {
                Host.Available(Scope, write: true);
                if (!Host.owners.ContainsKey(Scope)) throw new ObjectDisposedException(nameof(IExtensionFlagRegistrationFacade));
                var setDefault = options.DefaultValue is not null && !Host.values.TryGetValue(name, out _);
                var added = false;
                if (!Flags.TryGetValue(name, out var flag))
                {
                    if (Flags.Count == 128) throw new InvalidOperationException("Flag definition budget exceeded.");
                    var id = "facade-flag-" + Guid.NewGuid().ToString("N");
                    var marker = Scope.Observe(new(id, "facade-flag-metadata", static (_, _, _) => ValueTask.CompletedTask));
                    flag = new(this, marker, name, options); Flags.Add(name, flag); added = true;
                }
                try
                {
                    if (setDefault && options.DefaultValue is { } value)
                    {
                        // Native activation and the explicit host commit are separate boundaries.
                        // Keep the first staged value even if registration races that host boundary.
                        if (!Committed) Pending.TryAdd(name, value);
                        else Host.values.CommitDefaults([new(name, value)]);
                    }
                    // Original flags.set replaces the definition, not its first pending value.
                    flag.Options = options;
                }
                catch { if (added) { Flags.Remove(name); flag.NativeHandle.Dispose(); } throw; }
                return flag;
            }
        }
        public ExtensionFlagValue? GetFlag(string name)
        {
            lock (Host.gate)
            {
                Host.Available(Scope, write: false);
                if (!Host.owners.ContainsKey(Scope)) throw new ObjectDisposedException(nameof(IExtensionFlagRegistrationFacade));
                if (!Flags.ContainsKey(name)) return null;
                if (Host.values.TryGetValue(name, out var value)) return value;
                return Pending.GetValueOrDefault(name);
            }
        }
    }
    private sealed class Flag(Owner owner, IExtensionRegistration nativeHandle, string name, ExtensionFlagOptions options) : IExtensionRegistration
    {
        internal IExtensionRegistration NativeHandle { get; } = nativeHandle;
        internal string Name { get; } = name;
        internal ExtensionFlagOptions Options { get; set; } = options;
        public string OwnerId => owner.OwnerId;
        public long OwnerGeneration => owner.OwnerGeneration;
        public string RegistrationId => NativeHandle.RegistrationId;
        public void Dispose()
        {
            lock (owner.Host.gate)
            {
                if (owner.Flags.TryGetValue(Name, out var current) && ReferenceEquals(current, this)) owner.Flags.Remove(Name);
                NativeHandle.Dispose();
            }
        }
    }
}
