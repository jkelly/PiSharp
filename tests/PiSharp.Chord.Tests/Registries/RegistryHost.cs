using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Data;
using PiSharp.Chord.Registries;
using PiSharp.Chord.State;
using PiSharp.Telemetry;

// The registries' hand-built host (area cv2-registries), on the state layer's (State/StateHost.cs). Upstream's registry tests build their
// registries through createHost (packages/chord/src/next/host, phase 6): plugins whose facets serve registries (host.serve) and contribute
// (self.contribute), host-lifetime objects whose factories read registries (self.registry), contribute writables, derives and other
// registries' values, and go live, fail, retry and end, and host.registry and host.close. Until the host lands, the cases run against the
// registries layer with the host's wiring done by hand, as upstream's host does it:
// - Load is load's publication: in one commit, each plugin's serves (checkProvider, then record.serve) and facet contributions, in load
//   order, then every served registry composed once (record.composeServed), in the order the records were made (host.ts #publish).
// - Activate is an object's attempt: its factory declares (registry reads, writables, derives, contributions). While a registry it reads
//   has no usable value it parks (Parked holds the value's error, upstream's failed { waiting }) and waits on the value (value.wait); a
//   wake-up tries again (TryGoLive). Once every read is usable, its reads are registered (value.read()), its waits released, and its
//   contributions enter the tables in one commit (object.ts). Fail and End withdraw them in one commit, then detach its writables and
//   derives and release its reads (attempt.ts). What a contribution is follows host/members.ts' contentOf: a native source, a JSON source
//   or constant, or a plain value.
// - RegistryAsync is host.registry after the publication: the version's value once usable, woken by its waiter (value.wait), which resolves
//   on the host's scheduler (upstream's queueMicrotask), so a case flushes it with Delivered(); it rejects with the value's last error at
//   readyTimeout. Close ends every object, then closes every record (host.ts #close).
// What only the host has (the staging of plugins and their failures at load, the setup-only check of host.serve, a parked attempt's
// state and retryAt, backoff and retries, reloads' staging, the publication promise) waits for phase 6; each case file says which of its cases do.
internal sealed class RegistryHost
{
    private readonly OrderedDictionary<string, RegistryRecord> records = new(StringComparer.Ordinal);
    private readonly List<RegistryObject> objects = [];

    public RegistryHost(ITelemetryMetrics? metrics = null) => State = new StateHost(metrics);

    public StateHost State { get; }

    public Batch Batch => State.Batch;

    /// <summary>What the registries reported (the host's <c>onError</c>, registry kinds).</summary>
    public List<RegistryError> Errors { get; } = [];

    /// <summary>Upstream's <c>delivered()</c> and <c>flushed()</c>: runs every queued delivery and wake-up.</summary>
    public void Delivered() => State.Delivered();

    /// <summary>The record of registry <paramref name="name"/>, made by its first use (<c>registryRecord</c>).</summary>
    public RegistryRecord Record(string name)
    {
        if (!records.TryGetValue(name, out var record))
        {
            record = new RegistryRecord(name, Batch, Errors.Add);
            records.Add(name, record);
        }
        return record;
    }

    /// <summary>Load's publication of <paramref name="plugins"/>, in one commit: their serves and facet contributions, then every served
    /// registry composed once.</summary>
    public void Load(params (string Plugin, Action<RegistryFacet> Setup)[] plugins)
    {
        var facets = plugins.Select(plugin =>
        {
            var facet = new RegistryFacet(this, plugin.Plugin);
            plugin.Setup(facet);
            return facet;
        }).ToList();
        Batch.Commit(() =>
        {
            foreach (var facet in facets) facet.Publish();
            foreach (var record in records.Values) record.ComposeServed();
        }, ChordContext.Background);
    }

    /// <summary>A reload's commit: <paramref name="setup"/>'s serves replace the providers it serves again, each composed once.</summary>
    public void Reload(string plugin, Action<RegistryFacet> setup)
    {
        var facet = new RegistryFacet(this, plugin, contributes: false);
        setup(facet);
        Batch.Commit(() =>
        {
            facet.Publish();
            foreach (var name in facet.Served) Record(name).ComposeServed();
        }, ChordContext.Background);
    }

    /// <summary>A host-lifetime object of <paramref name="type"/> from <paramref name="plugin"/> going live: <paramref name="factory"/>
    /// declares, then its reads are registered and its contributions enter the tables in one commit.</summary>
    public RegistryObject Activate(string plugin, string type, Action<RegistryObject> factory)
    {
        var self = new RegistryObject(this, plugin, type);
        factory(self);
        objects.Add(self);
        self.TryGoLive();
        return self;
    }

    /// <summary>host.registry: <paramref name="registry"/>'s value as its version sees it, once usable; rejects with its last error at
    /// <paramref name="readyTimeout"/>, and <c>released/closed</c> when the host closes meanwhile.</summary>
    public async Task<IRegistryValue<V>> RegistryAsync<C, V>(Registry<C, V> registry, TimeSpan? readyTimeout = null)
    {
        Batch.AssertOutside("registry");
        var value = Record(registry.Name).Value<V>(registry.Version);
        Exception? last = null;
        bool Usable()
        {
            var start = value.Start();
            if (!start.Failed || start.HasLast) return true;
            last = start.Error;
            return false;
        }
        if (Usable()) return value;
        var changed = new TaskCompletionSource();
        var waiter = new RegistryHostWaiter(() => Batch.Schedule(() => changed.TrySetResult()), error => changed.TrySetException(error));
        var release = value.Wait(waiter);
        try
        {
            while (!Usable())
            {
                try
                {
                    await changed.Task.WaitAsync(readyTimeout ?? TimeSpan.FromSeconds(30));
                }
                catch (TimeoutException)
                {
                    ExceptionDispatchInfo.Throw(last!);
                }
                changed = new TaskCompletionSource();
            }
        }
        finally
        {
            release();
        }
        return value;
    }

    /// <summary>host.close: every live object ends, then every record closes.</summary>
    public void Close()
    {
        foreach (var self in Enumerable.Reverse(objects).ToList()) self.End();
        foreach (var record in records.Values) record.Close();
        records.Clear();
    }

    internal void Ended(RegistryObject self) => objects.Remove(self);

    /// <summary>What Chord follows behind a contribution (host/members.ts' contentOf): its native or JSON source when it is a Chord
    /// watchable, otherwise the value itself.</summary>
    internal static ContributionContent ContentOf(object? value) => value switch
    {
        NativeSource native => ContributionContent.Native(native),
        Source source => ContributionContent.Json(source),
        Constant constant => ContributionContent.Json(constant),
        _ => ContributionContent.Plain(value),
    };
}

/// <summary>A plugin's facet during load: what its setup serves and contributes, published in load's commit.</summary>
internal sealed class RegistryFacet(RegistryHost host, string plugin, bool contributes = true)
{
    private readonly List<Action> serves = [];
    private readonly List<(string Registry, long Version, object? Value)> contributions = [];

    /// <summary>The registries it serves, by name.</summary>
    public List<string> Served { get; } = [];

    /// <summary><c>host.serve(R, provider)</c>: checked now (the setup's checkProvider), served at the publication.</summary>
    public void Serve<C, V>(Registry<C, V> registry, RegistryProvider<C, V> provider)
    {
        RegistriesInternal.CheckProvider(registry.Name, provider);
        Served.Add(registry.Name);
        serves.Add(() => host.Record(registry.Name).Serve(registry.Version, provider));
    }

    /// <summary><c>self.contribute(R, value)</c> of the facet: in the table while its plugin is loaded.</summary>
    public void Contribute<C, V>(Registry<C, V> registry, object? value)
    {
        if (!contributes) throw new InvalidOperationException("a reload's facet here only serves");
        contributions.Add((registry.Name, registry.Version, value));
    }

    internal void Publish()
    {
        foreach (var serve in serves) serve();
        foreach (var (name, version, value) in contributions)
        {
            var record = host.Record(name);
            record.Add(new RegistryContribution(record, new Contributor(plugin, null), version, RegistryHost.ContentOf(value)));
        }
    }
}

/// <summary>
/// A host-lifetime object's attempt: what its factory declares, its contributions while live, and its end. Its registry reads are declared
/// dependencies: while one has no usable value it is parked (upstream's <c>failed { waiting }</c>, on the first in declaration order), its
/// waiters woken by the value, and goes live at the next check that finds them all usable.
/// </summary>
internal sealed class RegistryObject
{
    private readonly RegistryHost host;
    private readonly string plugin;
    private readonly string type;
    private readonly List<(string Registry, long Version, object? Value)> declared = [];
    private readonly List<RegistryContribution> contributions = [];
    private readonly List<Dependency> dependencies = [];
    private readonly List<Action> released = [];
    private readonly List<Action> waits = [];
    private readonly List<Derive> derives = [];
    private readonly List<Action<Exception>> detaches = [];
    private bool stopped;

    public RegistryObject(RegistryHost host, string plugin, string type)
    {
        this.host = host;
        this.plugin = plugin;
        this.type = type;
        Owner = new StateOwner(type);
        Waiter = new RegistryHostWaiter(() => host.Batch.Schedule(() =>
        {
            if (!Live && !stopped) TryGoLive();
        }), error => Stop(error));
    }

    /// <summary>The attempt its writables belong to.</summary>
    public StateOwner Owner { get; }

    /// <summary>What it registers on the registries it is parked on.</summary>
    public RegistryHostWaiter Waiter { get; }

    /// <summary>Whether it is live (not parked, failed or ended).</summary>
    public bool Live { get; private set; }

    /// <summary>While parked, the error of the first registry it reads that has no usable value.</summary>
    public Exception? Parked { get; private set; }

    /// <summary>The derive failures it reported (<c>derive_failed</c>).</summary>
    public List<Exception> DeriveFailures { get; } = [];

    /// <summary><c>self.registry(R)</c>: the value of <paramref name="registry"/>'s version, a declared dependency read while the object is
    /// live; a derive source and a contribution.</summary>
    public IRegistryValue<V> Registry<C, V>(Registry<C, V> registry)
    {
        var value = host.Record(registry.Name).Value<V>(registry.Version);
        dependencies.Add(new Dependency(value.Read, value.Wait, () =>
        {
            var start = value.Start();
            return !start.Failed || start.HasLast ? null : start.Error;
        }));
        return value;
    }

    /// <summary><c>self.writable(initial)</c>.</summary>
    public WritableSource<T> Writable<T>(T initial)
    {
        var writable = host.State.Writable(initial, Owner);
        detaches.Add(writable.Detach);
        return writable;
    }

    /// <summary><c>self.derive(sources, compute)</c>: a registry value given by <see cref="Registry{C, V}"/> is a native source; computed
    /// once as the object goes live.</summary>
    public Derive Derive(object[] sources, Func<IReadOnlyList<object?>, JsonNode?> compute)
    {
        var derive = host.State.Derive(sources, compute, DeriveFailures.Add);
        derives.Add(derive);
        detaches.Add(derive.Detach);
        return derive;
    }

    /// <summary><c>self.contribute(R, value)</c>: in the table from live until it fails or ends.</summary>
    public void Contribute<C, V>(Registry<C, V> registry, object? value) => declared.Add((registry.Name, registry.Version, value));

    /// <summary>It fails (its runtime failed, <c>failLive</c>): its contributions leave the tables in one commit, its writables and derives
    /// are detached, its reads released.</summary>
    public void Fail() => Stop(new ChordError("released", $"{type}: failed", "ended"));

    /// <summary>It ends (its last reference went, host.close).</summary>
    public void End() => Stop(Owner.EndError());

    /// <summary>The dependency check, then live: its reads registered, its derives computed, its contributions entered in one commit;
    /// parked instead while a registry it reads has no usable value.</summary>
    internal void TryGoLive()
    {
        foreach (var dependency in dependencies)
        {
            if (dependency.Unusable() is not { } error) continue;
            Parked = error;
            if (waits.Count == 0)
            {
                foreach (var each in dependencies) waits.Add(each.Wait(Waiter));
                // The first start of a wait wakes nobody (value.ts #tell): the waiter reads it itself, so a later change of it wakes it.
                foreach (var each in dependencies) _ = each.Unusable();
            }
            return;
        }
        Parked = null;
        foreach (var release in waits) release();
        waits.Clear();
        foreach (var dependency in dependencies) released.Add(dependency.Read());
        foreach (var derive in derives) _ = derive.Value;
        foreach (var (name, version, value) in declared)
        {
            var record = host.Record(name);
            contributions.Add(new RegistryContribution(record, new Contributor(plugin, type), version, RegistryHost.ContentOf(value)));
        }
        if (contributions.Count > 0)
        {
            host.Batch.Commit(() =>
            {
                foreach (var contribution in contributions) contribution.Record.Add(contribution);
            }, Owner.Context);
        }
        Live = true;
    }

    private void Stop(Exception error)
    {
        if (stopped) return;
        stopped = true;
        if (Live && contributions.Count > 0)
        {
            host.Batch.Within(() =>
            {
                foreach (var contribution in contributions) contribution.Record.Remove(contribution);
            }, Owner.Context);
        }
        Live = false;
        Owner.Ended = true;
        foreach (var detach in detaches) detach(error);
        foreach (var release in waits.Concat(released)) release();
        waits.Clear();
        released.Clear();
        host.Ended(this);
    }

    /// <summary>A registry read: its reader registration, its wait, and its error while it has no usable value.</summary>
    private sealed record Dependency(Func<Action> Read, Func<IRegistryWaiter, Action> Wait, Func<Exception?> Unusable);
}

/// <summary>A waiter calling back: what an attempt parked on a registry, or a waiting host.registry, does when told.</summary>
internal sealed class RegistryHostWaiter(Action moved, Action<ChordError>? closed = null) : IRegistryWaiter
{
    public int Moves { get; private set; }

    public void DependencyMoved()
    {
        Moves++;
        moved();
    }

    public void Closed(ChordError error) => closed?.Invoke(error);
}
