using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

public sealed partial class LegacyHost
{
    private LegacyNetwork? Network;
    private readonly Dictionary<LegacyConnection, LegacyPeerSession> Sessions = [];
    private readonly Dictionary<string, HashSet<LegacyConnection>> RootLinks = [];
    private readonly Dictionary<string, (LegacyGeneration Generation, HashSet<LegacyConnection> Links)> ForeignRoots = [];
    private readonly LinkedList<LegacyObject> RemoteLingers = new();
    private LegacyNetwork GetNetwork() => Network ??= new LegacyNetwork(this, Report, onPeer: peer =>
    {
        var session = new LegacyPeerSession(peer.Connection, new()
        {
            LocalName = Name, OnError = Report,
            Provider = (frame, principal, token) => ServeRemoteAsync(frame, principal, peer.Connection, token),
            RelayResolver = (name, principal, still, token) => GetNetwork().ResolveRelayAsync(name, principal, still, token),
            AdmitRoot = (root, principal, token) => AdmitRemoteRootAsync(root, principal, peer.Connection, token),
            RootSent = (root, connection) => Fire(() => { NoteRootLink(root.RootId, connection); return Task.CompletedTask; }),
            RootEnded = root => Fire(() => { EndForeignRoot(root, peer.Connection); return Task.CompletedTask; }),
        });
        peer.Connection.Received += frame =>
        {
            if (frame.Type == "invalidate" && frame["pattern"] is JsonObject pattern) Fire(() => InvalidatePrincipalAsync(pattern));
            if (frame.Type == "root_end") Fire(() => { EndForeignRoot(frame.String("rootId")!, peer.Connection); return Task.CompletedTask; });
        };
        peer.Connection.Closed += _ => Fire(() => { foreach (var root in ForeignRoots.Keys.ToArray()) EndForeignRoot(root, peer.Connection); Sessions.Remove(peer.Connection); return Task.CompletedTask; });
        Invoke(() => Sessions[peer.Connection] = session);
    });
    public LegacyClusterPeer Accept(ILegacyLink link, LegacyAcceptOptions options) => Invoke(GetNetwork).Accept(link, options);
    public LegacyClusterPeer Connect(ILegacyLink link, JsonNode? principal, LegacyConnectionOptions? options = null)
    {
        var peer = Invoke(GetNetwork).Connect(link, principal, options);
        Invoke(() => { foreach (var owner in Objects.Values.Where(x => x.RemoteContract is not null && x.Status == "loading").ToArray()) Fire(() => ConnectRemoteAsync(owner)); });
        return peer;
    }
    public void Route(string prefix, LegacyRoute route) => Invoke(() => GetNetwork().Route(prefix, route));
    public Task<LegacyReloadReport> ReloadClusterAsync(IReadOnlyList<LegacyFacet> facets, LegacyReloadOptions? options = null, CancellationToken cancellationToken = default) =>
        GetNetwork().Cluster.ReloadPreparedAsync(async () => await PrepareReloadAsync(facets, options), options, cancellationToken);
    private static JsonNode? PrincipalJson(object? principal) => principal is JsonNode json ? json.DeepClone() : JsonSerializer.SerializeToNode(principal);
    private LegacyReference AcquireRemote(LegacyContract contract, JsonNode? input, object? principal, bool state, string address)
    {
        if (!Objects.TryGetValue(address, out var owner))
        {
            owner = new(this, address, input, principal) { RemoteContract = contract }; Objects.Add(address, owner);
            Fire(() => ConnectRemoteAsync(owner));
        }
        var result = new LegacyReference(this, owner, contract, state); owner.Add(result); result.Changed(); return result;
    }
    private async Task ConnectRemoteAsync(LegacyObject selected)
    {
        if (selected.RemoteConnecting || selected.Status == "disposed") return; selected.RemoteConnecting = true;
        var contract = selected.RemoteContract!;
        try
        {
                    var connection = await GetNetwork().ResolveAsync(contract.Placement!.Resolve(contract.Name, selected.Input), PrincipalJson(selected.Principal), selected.Abort.Token);
                    if (connection is null) throw new ChordException("unavailable", "no route");
                    var session = Invoke(() => Sessions.GetValueOrDefault(connection)) ?? LegacyPeerSession.For(connection) ?? throw new ChordException("unavailable", "missing peer session");
                    selected.RemoteViaUpstream = connection.Role == "upstream";
                    var wantsState = selected.References.Any(reference => reference.WantsState);
                    selected.RemoteSubscribed = wantsState;
                    var remote = session.Hold(contract, selected.Input, wantsState, PrincipalJson(selected.Principal), again: selected.HasRemoteShape);
                    selected.External = remote;
                    await remote.Ready.WaitAsync(Options.ReadyTimeout, selected.Abort.Token);
                    void Bind()
                    {
                        if (selected.Status == "disposed") return;
                        var members = new Dictionary<string, object?>(); var variant = remote.Variant ?? "";
                        foreach (var (name, spec) in contract.Variants[variant])
                            if (spec is LegacyStateMember) members[name] = new RemoteReadable(this, remote, name);
                            else if (spec is LegacyMethodMember) members[name] = (LegacyMethod)(async (args, context) =>
                            {
                                if (Kind != "client") NoteRootLink(context.RootId, connection);
                                return await remote.CallAsync(name, args, Kind == "client" ? null : new(context.RootId, context.CodeVersion), context.CancellationToken);
                            });
                        var service = new LegacyService(contract, (_, _, _) => throw new InvalidOperationException("remote service"), new(), "");
                        var run = new LegacyRun { Service = service, Implementation = new(members, remote.Variant) };
                        selected.Current = run; selected.Runs[Current.Id] = run; selected.Status = "live"; selected.HasRemoteShape = true;
                        selected.RemoteStateReady = selected.RemoteSubscribed;
                        foreach (var reference in selected.References.ToArray()) reference.Changed(); selected.Ready.TrySetResult();
                    }
                    remote.ShapeChanged += () => Fire(() => { Bind(); return Task.CompletedTask; }); Bind();
                    remote.Failed += error => Fire(() =>
                    {
                        if (!ReferenceEquals(selected.External, remote) || selected.Status == "disposed") return Task.CompletedTask;
                        if (error is not ChordException { Code: "unavailable" } || !error.Message.Contains("link closed", StringComparison.Ordinal))
                        { Fire(() => selected.DisposeAsync(error)); return Task.CompletedTask; }
                        selected.Status = "loading"; selected.External = null;
                        foreach (var reference in selected.References) reference.Loading();
                        if (!selected.RemoteViaUpstream) Fire(() => ConnectRemoteAsync(selected));
                        return Task.CompletedTask;
                    });
                    connection.Closed += _ => Fire(() =>
                    {
                        if (!ReferenceEquals(selected.External, remote) || selected.Status == "disposed") return Task.CompletedTask;
                        selected.Status = "loading"; selected.External = null;
                        foreach (var reference in selected.References) reference.Loading();
                        return Task.CompletedTask;
                    });
        }
        catch (Exception error) { selected.Error = ChordException.Wrap(error, selected.Address); selected.Ready.TrySetException(selected.Error); await selected.DisposeAsync(selected.Error); }
        finally { selected.RemoteConnecting = false; }
    }
    internal void RemoteActive(LegacyObject owner) => RemoteLingers.Remove(owner);
    internal void UpdateRemoteSubscription(LegacyObject owner)
    {
        if (owner.RemoteContract is not { } contract || owner.External is not LegacyRemoteReference remote || owner.Status != "live") return;
        var wants = owner.References.Any(reference => reference.WantsState);
        if (wants == owner.RemoteSubscribed) return;
        owner.RemoteSubscribed = wants; owner.RemoteStateReady = false;
        foreach (var subscription in owner.RemoteSnapshotObservers) subscription.Dispose(); owner.RemoteSnapshotObservers.Clear();
        if (wants)
        {
            var pending = contract.Variants[remote.Variant ?? ""].Where(x => x.Value is LegacyStateMember).Select(x => x.Key).ToHashSet();
            foreach (var name in pending.ToArray())
            {
                var skipCached = false; try { _ = remote.Read(name); skipCached = true; } catch (ChordException) { }
                owner.RemoteSnapshotObservers.Add(remote.Subscribe(name, _ =>
                {
                    if (skipCached) { skipCached = false; return; }
                    Fire(() =>
                    {
                        if (!ReferenceEquals(owner.External, remote) || !owner.RemoteSubscribed) return Task.CompletedTask;
                        if (pending.Remove(name) && pending.Count == 0)
                        {
                            owner.RemoteStateReady = true;
                            foreach (var subscription in owner.RemoteSnapshotObservers) subscription.Dispose(); owner.RemoteSnapshotObservers.Clear();
                            foreach (var reference in owner.References.ToArray()) reference.Changed();
                        }
                        return Task.CompletedTask;
                    });
                }));
            }
            if (pending.Count == 0) owner.RemoteStateReady = true;
        }
        remote.SetStateSubscription(wants);
    }
    internal void RemoteIdle(LegacyObject owner)
    {
        var linger = owner.RemoteContract is { } contract && Options.RemoteLingerTypes.TryGetValue(contract.Name, out var duration) ? duration : TimeSpan.Zero;
        RemoteLingers.Remove(owner);
        if (!owner.HasRemoteShape || linger <= TimeSpan.Zero || Options.RemoteLingerCap <= 0) { owner.ScheduleLinger(TimeSpan.Zero); return; }
        RemoteLingers.AddLast(owner); owner.ScheduleLinger(linger);
        while (RemoteLingers.Count > Options.RemoteLingerCap) { var oldest = RemoteLingers.First!.Value; RemoteLingers.RemoveFirst(); Fire(() => oldest.DisposeAsync()); }
    }
    private async Task<ILegacyServedReference> ServeRemoteAsync(LegacyFrame frame, JsonNode? principal, LegacyConnection connection, CancellationToken cancellationToken)
    {
        var contract = LegacyContract.FromDescription(frame["token"]!.AsObject());
        var reference = await GetAsync(contract, frame["input"], new LegacyGetOptions { Principal = principal, HasPrincipal = true, State = true }, cancellationToken);
        return new ServedReference(this, reference, principal, connection);
    }
    private void NoteRootLink(string root, LegacyConnection connection)
    { if (!RootLinks.TryGetValue(root, out var links)) RootLinks[root] = links = []; links.Add(connection); }
    private void EndRootLinks(string root)
    { if (RootLinks.Remove(root, out var links)) foreach (var connection in links) if (connection.Open) connection.Send(LegacyProtocol.Frame("root_end", ("rootId", JsonValue.Create(root)))); }
    private void EndForeignRoot(string root, LegacyConnection connection)
    {
        if (!ForeignRoots.TryGetValue(root, out var foreign) || !foreign.Links.Remove(connection) || foreign.Links.Count != 0) return;
        ForeignRoots.Remove(root); foreign.Generation.Roots--; _leases.Remove(root); EndRootLinks(root); TryRetire();
    }
    private Task<LegacyRootLease> AdmitRemoteRootAsync(LegacyRoot? incoming, JsonNode? principal, LegacyConnection connection, CancellationToken cancellationToken) => RunAsync(() =>
    {
        CheckOpen(); var root = incoming ?? new LegacyRoot(Name + ":ingress:" + Interlocked.Increment(ref _rootSequence), Current.Id);
        if (!Generations.TryGetValue(root.CodeVersion, out var generation)) throw new ChordException("superseded", "remote root code version was retired");
        generation.Roots++;
        if (incoming is not null)
        {
            if (!ForeignRoots.TryGetValue(root.RootId, out var foreign)) { foreign = (generation, []); ForeignRoots[root.RootId] = foreign; generation.Roots++; }
            foreign.Links.Add(connection);
        }
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _close.Token, generation.Abort.Token);
        return Task.FromResult(new LegacyRootLease(root, stop.Token, () => Fire(() =>
        {
            stop.Cancel(); stop.Dispose(); generation.Roots--;
            if (incoming is null) { EndForeignRoot(root.RootId, connection); _leases.Remove(root.RootId); EndRootLinks(root.RootId); }
            TryRetire(); return Task.CompletedTask;
        }), () => HasCodeVersion(root.CodeVersion) ? new ChordException("aborted", "root aborted") : new ChordException("superseded", "root code version was retired")));
    });
    private Task<object?> RemoteCallAsync(LegacyReference reference, string member, bool hasArgs, JsonNode? args, LegacyRoot? root, object? principal, LegacyConnection connection, CancellationToken cancellationToken) => RunAsync(async () =>
    {
        var version = root?.CodeVersion ?? Current.Id;
        if (!Generations.TryGetValue(version, out var generation)) throw new ChordException("superseded", "remote root code version was retired");
        if (root is not null)
        {
            if (!ForeignRoots.TryGetValue(root.RootId, out var foreign)) { foreign = (generation, []); ForeignRoots[root.RootId] = foreign; generation.Roots++; }
            foreign.Links.Add(connection);
        }
        using var context = new LegacyContext(this, root?.RootId ?? Name + ":remote:" + Interlocked.Increment(ref _rootSequence), version, principal, _close.Token, cancellationToken, generation.Abort.Token);
        generation.Roots++; _roots.Add(context);
        try { return hasArgs ? await reference.CallAsync(member, args, context) : await reference.CallAsync(member, context); }
        finally { context.Dispose(); _roots.Remove(context); generation.Roots--; if (root is null) { _leases.Remove(context.RootId); EndRootLinks(context.RootId); } TryRetire(); }
    });
    private sealed class RemoteReadable(LegacyHost host, LegacyRemoteReference reference, string name) : ILegacyReadable
    {
        public JsonNode? Value => reference.Read(name);
        public IDisposable Subscribe(Action<JsonNode?> listener)
        {
            var subscription = reference.Subscribe(name, value => host.Fire(() => { listener(value); return Task.CompletedTask; }));
            return subscription;
        }
        public IDisposable Events(Action<LegacyStateEvent> listener) => Subscribe(value => listener(new("reset", value)));
    }
    private sealed class ServedReference : ILegacyServedReference
    {
        private readonly LegacyHost _host; private readonly LegacyReference _reference; private readonly object? _principal; private readonly LegacyConnection _connection;
        private readonly List<IDisposable> _subscriptions = [];
        private readonly IDisposable _shape;
        internal ServedReference(LegacyHost host, LegacyReference reference, object? principal, LegacyConnection connection)
        {
            _host = host; _reference = reference; _principal = principal; _connection = connection;
            _shape = reference.OnShape(_ => { Attach(); StateChanged?.Invoke(LegacyProtocol.Frame("shape")); }); Attach();
        }
        public string? Variant => _reference.Variant;
        public string ContractFingerprint => _reference.Shape!.Contract;
        public IReadOnlyList<string> Members => _reference.Shape!.Members;
        public IReadOnlyDictionary<string, JsonNode?> State => _host.Invoke(() => StateNames().ToDictionary(x => x, x => _reference.Member(x).Value));
        public event Action<LegacyFrame>? StateChanged;
        private IEnumerable<string> StateNames() => _reference.Contract.Variants[_reference.Contract.Scope == "local" ? "" : Variant!].Where(x => x.Value is LegacyStateMember).Select(x => x.Key);
        private void Attach()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose(); _subscriptions.Clear();
            foreach (var name in StateNames())
            {
                var first = true;
                _subscriptions.Add(_reference.Member(name).Events(value => { if (first) { first = false; return; } StateChanged?.Invoke(LegacyProtocol.Frame("state", ("m", JsonValue.Create(name)), ("as", JsonValue.Create("reset")), ("value", value.Value))); }));
            }
        }
        public async Task<LegacyCallResult> CallAsync(string member, bool hasArgs, JsonNode? args, LegacyRoot? root, CancellationToken cancellationToken)
        {
            var value = await _host.RemoteCallAsync(_reference, member, hasArgs, args, root, _principal, _connection, cancellationToken);
            var spec = (LegacyMethodMember)_reference.Contract.Variants[_reference.Contract.Scope == "local" ? "" : Variant!][member];
            return new(spec.RawResult is not null, value as JsonNode);
        }
        public void Dispose() { _shape.Dispose(); foreach (var subscription in _subscriptions) subscription.Dispose(); _reference.Release(); }
    }
}
