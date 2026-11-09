// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): cluster.ts.
using System.Text.Json.Nodes;

namespace PiSharp.Chord.Legacy;

/// <summary>Protocol 4 cluster reload: unanimous prepare, parent-first commit, rollback and catch-up.</summary>
public sealed class LegacyCluster : IDisposable
{
    private readonly object gate = new();
    private readonly LegacyNetwork network;
    private readonly ILegacyReloadHost host;
    private readonly Action<Exception> report;
    private readonly Dictionary<(LegacyClusterPeer Peer, long Id), TaskCompletionSource<LegacyReloadReport?>> votes = [];
    private readonly Dictionary<string, IReadOnlyList<string>> remote = new(StringComparer.Ordinal), removed = new(StringComparer.Ordinal);
    private ChildOperation? child;
    private static long sequence;
    private bool disposed;
    internal LegacyCluster(LegacyNetwork network, ILegacyReloadHost host, Action<Exception> report)
    {
        this.network = network; this.host = host; this.report = report;
        network.Frame += Frame; network.PeerRemoved += PeerRemoved;
    }
    public Task<LegacyReloadReport> ReloadAsync(IReadOnlyList<string> modules, LegacyReloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var actual = options;
        return ReloadPreparedAsync(() => host.BeginReloadAsync(modules, actual, host.CodeVersion + 1, cancellationToken), actual, cancellationToken);
    }
    /// <summary>The concrete host also uses this entry for already-resolved facet objects.</summary>
    public async Task<LegacyReloadReport> ReloadPreparedAsync(Func<Task<ILegacyPreparedReload>> begin,
        LegacyReloadOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (network.Upstream is not null && host.Kind != "client") throw new ChordException("unavailable", "reloads are coordinated by the server", "not_coordinator");
        options ??= new();
        network.BeginReload();
        var participants = network.Peers.Where(p => p.Role == "downstream" && p.Kind == "host" && p.Synced && p.Connection.Open).ToArray();
        ILegacyPreparedReload? reload = null;
        var id = Interlocked.Increment(ref sequence);
        var committed = false;
        try
        {
            reload = await begin().ConfigureAwait(false);
            await reload.WaitForRetirementAsync(cancellationToken).ConfigureAwait(false);
            participants = participants.Where(p => p.Connection.Open).ToArray();
            var version = reload.Summary().CodeVersion;
            var deadline = options.VoteDeadline ?? (options.GateDeadline ?? TimeSpan.FromSeconds(5)) +
                (options.RetireDeadline ?? TimeSpan.FromSeconds(5)) + TimeSpan.FromSeconds(5);
            var pending = participants.Select(peer => VoteAsync(peer, id, deadline, cancellationToken)).ToArray();
            for (var i = 0; i < participants.Length; i++) participants[i].Connection.Send(Prepare(id, version,
                Modules(options.Remote, participants[i].Name), options with { Remove = Modules(options.RemoteRemove, participants[i].Name) }));
            // Await every participant even if one fails; all candidates finish preparing before rollback.
            var prepare = reload.PrepareAsync(cancellationToken);
            Exception? failure = null;
            try { await Task.WhenAll(pending.Cast<Task>().Append(prepare)).ConfigureAwait(false); }
            catch (Exception error) { failure = error; }
            if (failure is not null) throw failure;
            var result = reload.Commit(() =>
            {
                foreach (var peer in participants)
                {
                    peer.Connection.Send(LegacyProtocol.Frame("commit", ("reload", JsonValue.Create(id))));
                    peer.CodeVersion = version;
                }
            });
            committed = true;
            Remember(options.Remote, options.RemoteRemove);
            var children = new Dictionary<string, LegacyReloadReport>(StringComparer.Ordinal);
            for (var i = 0; i < pending.Length; i++) if (pending[i].Result is { } value) children[participants[i].Name ?? "child"] = value;
            return result with { Children = children };
        }
        catch
        {
            if (!committed)
            {
                foreach (var peer in participants) peer.Connection.Send(LegacyProtocol.Frame("abort", ("reload", JsonValue.Create(id))));
                if (reload is not null) await reload.RollbackAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            foreach (var peer in participants) lock (gate) votes.Remove((peer, id));
            if (reload is not null) host.EndReload();
            network.EndReload();
        }
    }
    private async Task<LegacyReloadReport?> VoteAsync(LegacyClusterPeer peer, long id, TimeSpan deadline, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<LegacyReloadReport?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); votes.Add((peer, id), completion); }
        try { return await completion.Task.WaitAsync(deadline, network.TimeProvider, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException error) { throw new ChordException("aborted", $"{peer.Name}: votes missing after {deadline.TotalMilliseconds} ms", "vote_deadline", cause: error); }
        catch (ChordException error) { throw new ChordException(error.Code, $"{peer.Name}: {error.Message}", error.Reason, cause: error); }
        finally { lock (gate) votes.Remove((peer, id)); }
    }
    internal async Task ReconcileAsync(LegacyClusterPeer peer)
    {
        while (peer.Connection.Open)
        {
            await network.WaitReloadAsync().ConfigureAwait(false);
            if (!peer.Connection.Open) return;
            var version = host.CodeVersion;
            if (peer.CodeVersion == version)
            {
                if (network.TryMarkSynced(peer, version)) return;
                continue;
            }
            network.AnnounceRetired(peer);
            var id = Interlocked.Increment(ref sequence);
            var vote = VoteAsync(peer, id, TimeSpan.FromSeconds(30), default);
            IReadOnlyList<string> modules, removals;
            lock (gate) { modules = Modules(remote, peer.Name); removals = Modules(removed, peer.Name); }
            peer.Connection.Send(Prepare(id, version, modules, new() { Remove = removals }));
            await vote.ConfigureAwait(false);
            if (host.CodeVersion != version)
            {
                peer.Connection.Send(LegacyProtocol.Frame("abort", ("reload", JsonValue.Create(id)))); continue;
            }
            peer.Connection.Send(LegacyProtocol.Frame("commit", ("reload", JsonValue.Create(id))));
            peer.CodeVersion = version;
        }
    }
    private void Frame(LegacyClusterPeer peer, LegacyFrame frame)
    {
        if (disposed) return;
        if (frame.Type == "vote")
        {
            TaskCompletionSource<LegacyReloadReport?>? vote;
            lock (gate) votes.TryGetValue((peer, frame.Number("reload")), out vote);
            if (vote is null) return;
            if (frame["error"] is JsonObject error) vote.TrySetException(new ChordException(error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>(), error["reason"]?.GetValue<string>()));
            else vote.TrySetResult(frame["report"] is JsonObject value ? FromJson(value) : null);
            return;
        }
        if (peer.Role != "upstream" || host.Kind == "client") return;
        if (frame.Type == "retire") { host.AllowRetirement(checked((int)frame.Number("codeVersion"))); return; }
        if (frame.Type == "prepare")
        {
            ChildOperation operation;
            lock (gate)
            {
                if (child is not null)
                {
                    peer.Connection.Send(VoteError(frame.Number("reload"), new ChordException("unavailable", "a reload is already running", "reload_running"))); return;
                }
                child = operation = new(frame.Number("reload"));
            }
            _ = RunChild(operation, peer, frame); return;
        }
        ChildOperation? current;
        lock (gate) current = child?.Id == frame.Number("reload") ? child : null;
        if (current is null) return;
        if (frame.Type == "abort") { Cancel(current); return; }
        if (frame.Type != "commit") return;
        lock (gate)
        {
            if (!current.Prepared || current.Cancelled) return;
            child = null;
        }
        try { current.Reload!.Commit(); }
        catch (Exception error) { report(error); }
        finally { current.Cancellation.Dispose(); host.EndReload(); }
    }
    private async Task RunChild(ChildOperation operation, LegacyClusterPeer peer, LegacyFrame frame)
    {
        try
        {
            var modules = Strings(frame["plugins"]);
            var options = ReadOptions(frame["options"] as JsonObject);
            operation.Reload = await host.BeginReloadAsync(modules, options, checked((int)frame.Number("codeVersion")), operation.Cancellation.Token).ConfigureAwait(false);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            await operation.Reload.PrepareAsync(operation.Cancellation.Token).ConfigureAwait(false);
            lock (gate)
            {
                operation.Cancellation.Token.ThrowIfCancellationRequested();
                operation.Prepared = true;
            }
            peer.Connection.Send(LegacyProtocol.Frame("vote", ("reload", JsonValue.Create(operation.Id)), ("report", ToJson(operation.Reload.Summary()))));
        }
        catch (Exception error)
        {
            if (operation.Reload is not null)
            {
                try { await operation.Reload.RollbackAsync().ConfigureAwait(false); } catch (Exception cleanup) { report(cleanup); }
                host.EndReload();
            }
            lock (gate) if (ReferenceEquals(child, operation)) child = null;
            if (!operation.Cancelled) peer.Connection.Send(VoteError(operation.Id, error));
            operation.Cancellation.Dispose();
        }
    }
    private void Cancel(ChildOperation operation)
    {
        bool prepared;
        lock (gate)
        {
            if (operation.Cancelled) return;
            operation.Cancelled = true; prepared = operation.Prepared;
            if (prepared && ReferenceEquals(child, operation)) child = null;
        }
        operation.Cancellation.Cancel();
        if (prepared) _ = RollbackPrepared(operation);
    }
    private async Task RollbackPrepared(ChildOperation operation)
    {
        try { if (operation.Reload is not null) await operation.Reload.RollbackAsync().ConfigureAwait(false); }
        catch (Exception error) { report(error); }
        finally { operation.Cancellation.Dispose(); host.EndReload(); }
    }
    private void PeerRemoved(LegacyClusterPeer peer)
    {
        TaskCompletionSource<LegacyReloadReport?>[] pending;
        ChildOperation? operation;
        lock (gate) { pending = votes.Where(p => ReferenceEquals(p.Key.Peer, peer)).Select(p => p.Value).ToArray(); operation = peer.Role == "upstream" ? child : null; }
        foreach (var vote in pending) vote.TrySetException(new ChordException("unavailable", "host disconnected before vote"));
        if (operation is not null) Cancel(operation);
    }
    private void Remember(IReadOnlyDictionary<string, IReadOnlyList<string>>? add, IReadOnlyDictionary<string, IReadOnlyList<string>>? remove)
    {
        lock (gate)
        {
            foreach (var prefix in (add?.Keys ?? []).Concat(remove?.Keys ?? []).Distinct(StringComparer.Ordinal))
            {
                var additions = add?.GetValueOrDefault(prefix) ?? [];
                var removals = remove?.GetValueOrDefault(prefix) ?? [];
                remote[prefix] = (remote.GetValueOrDefault(prefix) ?? []).Concat(additions).Distinct().Except(removals).ToArray();
                removed[prefix] = (removed.GetValueOrDefault(prefix) ?? []).Concat(removals).Distinct().Except(additions).ToArray();
            }
        }
    }
    private static IReadOnlyList<string> Modules(IReadOnlyDictionary<string, IReadOnlyList<string>>? map, string? name) =>
        map?.Where(p => name?.StartsWith(p.Key, StringComparison.Ordinal) == true).OrderByDescending(p => p.Key.Length).FirstOrDefault().Value?.ToArray() ?? [];
    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    private static LegacyFrame Prepare(long id, int version, IReadOnlyList<string> modules, LegacyReloadOptions options)
    {
        var json = new JsonObject { ["remove"] = Array(options.Remove) };
        if (options.GateDeadline is { } gate) json["gateDeadline"] = gate.TotalMilliseconds;
        if (options.RetireDeadline is { } retire) json["retireDeadline"] = retire.TotalMilliseconds;
        return LegacyProtocol.Frame("prepare", ("reload", JsonValue.Create(id)), ("codeVersion", JsonValue.Create(version)), ("plugins", Array(modules)), ("options", json));
    }
    private static IReadOnlyList<string> Strings(JsonNode? node) => node is JsonArray values ? values.Select(v => v!.GetValue<string>()).ToArray() : [];
    private static LegacyReloadOptions ReadOptions(JsonObject? value) => new()
    {
        Remove = Strings(value?["remove"]), GateDeadline = value?["gateDeadline"] is { } g ? TimeSpan.FromMilliseconds(g.GetValue<double>()) : null,
        RetireDeadline = value?["retireDeadline"] is { } r ? TimeSpan.FromMilliseconds(r.GetValue<double>()) : null,
    };
    private static JsonObject ToJson(LegacyReloadReport report) => new()
    { ["codeVersion"] = report.CodeVersion, ["previous"] = report.Previous, ["changed"] = Array(report.Changed), ["removed"] = Array(report.Removed), ["prepared"] = Array(report.Prepared), ["gated"] = Array(report.Gated) };
    private static LegacyReloadReport FromJson(JsonObject value) => new()
    { CodeVersion = value["codeVersion"]!.GetValue<int>(), Previous = value["previous"]!.GetValue<int>(), Changed = Strings(value["changed"]), Removed = Strings(value["removed"]), Prepared = Strings(value["prepared"]), Gated = Strings(value["gated"]) };
    private static LegacyFrame VoteError(long id, Exception error)
    {
        var cause = ChordException.Wrap(error, "reload");
        var wire = new JsonObject { ["code"] = cause.Code, ["message"] = cause.Message };
        if (cause.Reason is not null) wire["reason"] = cause.Reason;
        return LegacyProtocol.Frame("vote", ("reload", JsonValue.Create(id)), ("error", wire));
    }
    public void Dispose()
    {
        ChildOperation? current;
        TaskCompletionSource<LegacyReloadReport?>[] pending;
        lock (gate)
        {
            if (disposed) return; disposed = true; current = child;
            pending = votes.Values.ToArray(); votes.Clear();
        }
        network.Frame -= Frame; network.PeerRemoved -= PeerRemoved;
        if (current is not null) Cancel(current);
        foreach (var vote in pending) vote.TrySetException(new ObjectDisposedException(nameof(LegacyCluster)));
    }
    private sealed class ChildOperation(long id)
    {
        internal long Id { get; } = id;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal ILegacyPreparedReload? Reload;
        internal bool Prepared, Cancelled;
    }
}
