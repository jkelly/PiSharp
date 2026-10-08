// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts
// (session_start startConnection/reportProblems, waitForDirectServers, session_shutdown) and packages/coding-agent/docs/mcp.md.
using System.Collections.Immutable;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;

namespace PiSharp.Cli.Mcp;

/// <summary>Host binding of one background server to the session attachment it serves. The host supplies the
/// registry scope, channel, policy and hooks; the returned server registers itself with that attachment. The host owns
/// whatever it acquired when it throws. The token is cancelled when the attachment retires.</summary>
public delegate ValueTask<McpPreparedServer> McpBackgroundServerFactory(McpServerEntry entry, ReplaceableAgentSession owner,
    AgentSessionAttachment attachment, CancellationToken cancellationToken);
/// <summary>Explicit admission of a server that connects after the session opens.</summary>
public sealed record McpBackgroundServerAdmission(string Name, Action<McpServerEntry> ValidateConfiguration,
    McpBackgroundServerFactory Bind);
/// <summary>A background connection that settled: <paramref name="Snapshot"/> when it connected, else <paramref name="Failure"/>.</summary>
public sealed record McpBackgroundConnectionReport(long Generation, McpServerEntry Entry, McpRuntimeSnapshot? Snapshot, Exception? Failure);

/// <summary>Pi 1.1.0 background connection. The first prompt waits only for servers with `direct` tools, whose tools
/// its request must declare; those are acquired before the session opens. Every other enabled server connects here
/// after the attachment is bound: its tools are published to the session catalog when it connects (at the next idle
/// boundary, through <see cref="McpPreparedServer"/>), the `mcp_servers` section picks up its instructions, and a
/// failure is reported without affecting the session. Retiring the attachment cancels connections still pending and
/// joins them.</summary>
public sealed class McpBackgroundConnections
{
    private readonly ImmutableArray<(McpServerEntry Entry, McpBackgroundServerFactory Bind)> servers;
    private readonly long generation;
    private readonly McpServersPromptSource? section;
    private readonly Action<McpBackgroundConnectionReport>? report;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private Task? running;
    /// <summary>Connections wait for this before connecting; see <see cref="McpSessionRuntimeAdmission.ConnectAfter"/>.</summary>
    internal Task? ConnectAfter { get; init; }

    internal McpBackgroundConnections(ImmutableArray<(McpServerEntry Entry, McpBackgroundServerFactory Bind)> servers, long generation,
        McpServersPromptSource? section, Action<McpBackgroundConnectionReport>? report)
    { this.servers = servers; this.generation = generation; this.section = section; this.report = report; }

    /// <summary>Splits the enabled servers: those with `direct` tools stay pre-open (<see cref="McpServersSection.FirstPromptWaitsFor"/>);
    /// the others connect in the background when the host admitted them for it. Validates every background
    /// configuration before any acquisition.</summary>
    internal static (McpServerCatalog PreOpen, ImmutableArray<(McpServerEntry Entry, McpBackgroundServerFactory Bind)> Background) Partition(
        McpServerCatalog catalog, ImmutableArray<McpBackgroundServerAdmission> admissions)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (admissions.IsDefaultOrEmpty) return (catalog, []);
        if (admissions.Length > McpAdmittedActivationHost.MaximumServers) throw new ArgumentException("Bounded background admissions are required.");
        var admitted = new Dictionary<string, McpBackgroundServerAdmission>(StringComparer.Ordinal);
        foreach (var admission in admissions)
        {
            ArgumentNullException.ThrowIfNull(admission); ArgumentException.ThrowIfNullOrWhiteSpace(admission.Name);
            ArgumentNullException.ThrowIfNull(admission.ValidateConfiguration); ArgumentNullException.ThrowIfNull(admission.Bind);
            if (admission.ValidateConfiguration.GetInvocationList().Length != 1 || admission.Bind.GetInvocationList().Length != 1 ||
                !admitted.TryAdd(admission.Name, admission))
                throw new ArgumentException("Background admission must have one validator, one binder and a unique name.", nameof(admissions));
        }
        var waits = McpServersSection.FirstPromptWaitsFor(catalog.Servers).Select(entry => entry.Name).ToHashSet(StringComparer.Ordinal);
        var background = ImmutableArray.CreateBuilder<(McpServerEntry, McpBackgroundServerFactory)>();
        foreach (var entry in catalog.Servers)
        {
            if (!entry.Config.Enabled || !admitted.TryGetValue(entry.Name, out var admission)) continue;
            if (waits.Contains(entry.Name))
                throw new InvalidOperationException($"MCP server \"{entry.Name}\" has direct tools: the first prompt waits for it, so it needs a pre-open admission.");
            admission.ValidateConfiguration(entry); background.Add((entry, admission.Bind));
        }
        var moved = background.Select(row => row.Item1.Name).ToHashSet(StringComparer.Ordinal);
        return (catalog with { Servers = catalog.Servers.Where(entry => !moved.Contains(entry.Name)).ToImmutableArray() }, background.ToImmutable());
    }

    /// <summary>Called from the runtime binding of the exact attachment, after the pre-open servers bound. Registers this
    /// coordinator with the attachment (its stop cancels pending connections, its close joins them), then starts.</summary>
    internal void Start(ReplaceableAgentSession owner, AgentSessionAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(attachment);
        if (attachment.Generation != generation) throw new InvalidOperationException("Background connections belong to their reserved generation.");
        lock (gate)
        {
            if (running is not null) throw new InvalidOperationException("Background connections start once.");
            owner.RegisterOwnedResource(attachment, _ => JoinAsync(), () => stop.CancelAsync());
            // The binding callback's ambient transition state must not flow into work that outlives it.
            using (ExecutionContext.SuppressFlow()) running = Task.Run(() => RunAsync(owner, attachment));
        }
    }

    private Task RunAsync(ReplaceableAgentSession owner, AgentSessionAttachment attachment) =>
        Task.WhenAll(servers.Select(server => ConnectAsync(server.Entry, server.Bind, owner, attachment)));

    private async Task ConnectAsync(McpServerEntry entry, McpBackgroundServerFactory bind, ReplaceableAgentSession owner, AgentSessionAttachment attachment)
    {
        await Task.Yield();
        McpRuntimeSnapshot snapshot;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (ConnectAfter is { } started) await started.WaitAsync(stop.Token).ConfigureAwait(false);
            var server = await bind(entry, owner, attachment, stop.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Background admission returned no server.");
            snapshot = await server.ConnectAsync(stop.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The session ended meanwhile; like the original, late results of a retired session are dropped.
            if (stop.IsCancellationRequested) return;
            Report(new(generation, entry, null, error)); return;
        }
        if (stop.IsCancellationRequested) return;
        section?.Connected(generation, snapshot.Catalog);
        Report(new(generation, entry, snapshot, null));
    }

    private void Report(McpBackgroundConnectionReport value)
    {
        try { report?.Invoke(value); }
        catch (Exception) { /* A reporting failure must not affect the connection or the session. */ }
    }

    private async Task JoinAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        Task? work; lock (gate) work = running;
        if (work is not null) await work.ConfigureAwait(false);
    }
}
