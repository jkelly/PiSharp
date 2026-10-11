// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts
// (session_start startConnection/reportProblems, waitForDirectServers, tool_call waits, session_shutdown) and
// packages/coding-agent/docs/mcp.md.
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

/// <summary>Pi 1.1.0 background connection: every enabled server connects here after the attachment is bound. Its tools are
/// published to the session catalog when it connects (during a run, at once and declared from the run's next request; see
/// <see cref="PersistentAgentSession.TryPublishToolCatalogDuringRunAsync"/>), the `mcp_servers` section picks up its
/// instructions, and a failure is reported without affecting the session. Before idle input is admitted,
/// <see cref="BeforeInputAsync"/> waits as the original's before_agent_start does: the first prompt up to
/// <see cref="StartupWait"/> for servers with `direct` tools (waitForDirectServers). Retiring the attachment cancels connections
/// still pending and joins them.</summary>
public sealed class McpBackgroundConnections
{
    /// <summary>The original's startupWaitMs default.</summary>
    internal static readonly TimeSpan DefaultStartupWait = TimeSpan.FromSeconds(10);
    internal const string StillConnecting = "MCP servers are still connecting; their tools become available once connected.";
    private readonly ImmutableArray<(McpServerEntry Entry, McpBackgroundServerFactory Bind)> servers;
    private readonly long generation;
    private readonly McpServersPromptSource? section;
    private readonly Action<McpBackgroundConnectionReport>? report;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private Task? running;
    private int waitedForStartup;
    /// <summary>Connections wait for this before connecting; see <see cref="McpSessionRuntimeAdmission.ConnectAfter"/>.</summary>
    internal Task? ConnectAfter { get; init; }
    /// <summary>How long the first prompt waits for servers with `direct` tools.</summary>
    internal TimeSpan StartupWait { get; init; } = DefaultStartupWait;
    /// <summary>Informational notices (the original's ctx.ui.notify "info").</summary>
    internal Action<string>? Notify { get; init; }
    /// <summary>Called once when every server settled, unless the attachment retired first (the original reports problems then).</summary>
    internal Action? AllSettled { get; init; }
    /// <summary>Called for each server once it connected, before it counts as settled (resource tools are published from here).</summary>
    internal Func<McpServerEntry, McpPreparedServer, McpRuntimeSnapshot, CancellationToken, Task>? Connected { get; init; }
    private readonly Dictionary<string, TaskCompletionSource> settled = new(StringComparer.Ordinal);

    internal McpBackgroundConnections(ImmutableArray<(McpServerEntry Entry, McpBackgroundServerFactory Bind)> servers, long generation,
        McpServersPromptSource? section, Action<McpBackgroundConnectionReport>? report)
    {
        this.servers = servers; this.generation = generation; this.section = section; this.report = report;
        foreach (var (entry, _) in servers) settled[entry.Name] = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Settles once each named server connected (its tools published) or failed, or the attachment retired.</summary>
    internal Task WhenSettled(Func<McpServerEntry, bool> include) =>
        Task.WhenAll(servers.Where(server => include(server.Entry)).Select(server => settled[server.Entry.Name].Task));

    /// <summary>Splits the enabled servers: those the host admitted for it connect in the background (servers with `direct` tools
    /// included, as in the original; the first prompt waits for them, see <see cref="BeforeInputAsync"/>); the others keep their
    /// pre-open admission. Validates every background configuration before any acquisition.</summary>
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
        var background = ImmutableArray.CreateBuilder<(McpServerEntry, McpBackgroundServerFactory)>();
        foreach (var entry in catalog.Servers)
        {
            if (!entry.Config.Enabled || !admitted.TryGetValue(entry.Name, out var admission)) continue;
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

    /// <summary>index.ts before_agent_start waitForDirectServers, before idle input is admitted: the first prompt waits up to
    /// <see cref="StartupWait"/> for servers with `direct` tools, then notes that the others are still connecting. tool_search,
    /// codemode and the resource tools wait for the servers they reach inside their calls (tool_call), not here.</summary>
    internal async Task BeforeInputAsync(PersistentAgentSession session, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Interlocked.Exchange(ref waitedForStartup, 1) == 0)
        {
            var direct = WhenSettled(entry => McpConfigurationReader.HasDirectTools(entry.Config));
            if (!direct.IsCompleted)
            {
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(token);
                var finished = await Task.WhenAny(direct, Task.Delay(StartupWait, delay.Token)).ConfigureAwait(false) == direct;
                await delay.CancelAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!finished && !stop.IsCancellationRequested)
                    try { Notify?.Invoke(StillConnecting); }
                    catch (Exception) { /* A notice failure must not affect the session. */ }
            }
        }
    }

    private async Task RunAsync(ReplaceableAgentSession owner, AgentSessionAttachment attachment)
    {
        await Task.WhenAll(servers.Select(server => ConnectAsync(server.Entry, server.Bind, owner, attachment))).ConfigureAwait(false);
        if (stop.IsCancellationRequested) return;
        try { AllSettled?.Invoke(); }
        catch (Exception) { /* A reporting failure must not affect the session. */ }
    }

    private async Task ConnectAsync(McpServerEntry entry, McpBackgroundServerFactory bind, ReplaceableAgentSession owner, AgentSessionAttachment attachment)
    {
        try { await ConnectCoreAsync(entry, bind, owner, attachment).ConfigureAwait(false); }
        finally { settled[entry.Name].TrySetResult(); }
    }

    private async Task ConnectCoreAsync(McpServerEntry entry, McpBackgroundServerFactory bind, ReplaceableAgentSession owner, AgentSessionAttachment attachment)
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
            if (Connected is { } connected) await connected(entry, server, snapshot, stop.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            // The session ended meanwhile; like the original, late results of a retired session are dropped. Only teardown cancels a
            // connection (the session's retirement or its server's disposal, which can run before `stop` is cancelled); timeouts
            // fail with their own errors (HttpClient's wraps a TimeoutException). So a cancelled connection is dropped, never reported as a failure.
            if (stop.IsCancellationRequested || attachment.LifetimeToken.IsCancellationRequested ||
                error is OperationCanceledException { InnerException: not TimeoutException }) return;
            Report(new(generation, entry, null, error)); return;
        }
        if (stop.IsCancellationRequested || attachment.LifetimeToken.IsCancellationRequested) return;
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
