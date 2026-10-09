// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts (describeState,
// attentionRank, describeTransport, saveConfig, signIn, signOut, reconnect, setEnabled, setExposure, serversMenu, serverMenu,
// showTools, chooseExposure, signInWithUi, runAction, manage, formatStatus, pickServer, loginCommand, reconnectSignedIn and the
// `/mcp` command with its argument completions), packages/coding-agent/src/extensions/mcp/ui.ts (McpMenu, McpUi) and
// packages/coding-agent/src/extensions/mcp/config.ts (updateMcpServerConfig).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.CodingAgent;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Runtime.Mcp.Authentication;

namespace PiSharp.Cli.Mcp;

/// <summary>One item of a manager menu (pi-tui SelectItem).</summary>
public sealed record McpMenuItem(string Value, string Label, string? Description = null);

/// <summary>ui.ts McpMenu: a menu the manager view shows and rebuilds on every change.</summary>
public sealed record McpMenu(string Title, ImmutableArray<McpMenuItem> Items, string ConfirmLabel, string CancelLabel)
{
    /// <summary>Shown below the title.</summary>
    public string? Details { get; init; }
    /// <summary>Shown below the details in the error color.</summary>
    public string? Error { get; init; }
    /// <summary>Shown when there are no items.</summary>
    public string? Empty { get; init; }
    /// <summary>Value of the item selected when the menu opens.</summary>
    public string? Selected { get; init; }
}

/// <summary>ui.ts McpUi: what the terminal's `/mcp` manager view provides (IMPL-I implements it).</summary>
public interface IMcpManagerUi
{
    /// <summary>Show a menu and resolve to the chosen item's value, or null when cancelled. <paramref name="subscribe"/> rebuilds the
    /// menu on every change, keeping the selected item; it returns the unsubscription.</summary>
    Task<string?> MenuAsync(Func<McpMenu> build, Func<Action, IDisposable>? subscribe = null);
    /// <summary>Show a message while an operation runs. With <paramref name="onCancel"/>, the cancel key calls it.</summary>
    void Status(string title, string message, Action? onCancel = null);
    /// <summary>Show the authorization URL and wait for a pasted redirect URL. Null when cancelled or when <paramref name="signal"/>
    /// is cancelled (the browser reached the callback).</summary>
    Task<string?> RedirectUrlAsync(string title, string authorizationUrl, CancellationToken signal);
}

/// <summary>The part of an extension command context `/mcp` uses (ExtensionCommandContext: hasUI, mode, ui.notify/select/input),
/// plus the terminal's manager view.</summary>
public interface IMcpCommandUi
{
    bool HasUi { get; }
    /// <summary>The interactive terminal (ctx.mode === "tui"), where `/mcp` opens the manager view.</summary>
    bool IsTui { get; }
    /// <summary><paramref name="level"/> is "info", "warning" or "error".</summary>
    void Notify(string message, string level);
    Task<string?> SelectAsync(string title, IReadOnlyList<string> options, CancellationToken token);
    Task<string?> InputAsync(string title, string placeholder, CancellationToken token);
    /// <summary>showMcpManager: run <paramref name="manage"/> with the terminal's manager view. Only called when <see cref="IsTui"/>.</summary>
    Task ShowManagerAsync(Func<IMcpManagerUi, Task> manage);
}

/// <summary>One server as the manager knows it.</summary>
public sealed record McpServerStatus(string Name, McpServerEntry Entry, string State, string Description, int ToolCount,
    bool HasResources, McpExposure Exposure, string? Error, string? Message, bool UsesOAuth);

/// <summary>The MCP servers of one session generation as `/mcp` manages them: every configured server (disabled ones too), its
/// connection state, tools and exposure, and the actions the original's manager offers: reconnect, sign in and out, enable or
/// disable (saved to the mcp.json that defines the server, or as a project override), and change exposure. Production sessions
/// create one per generation (see <see cref="McpSessionHost.ObserveManager"/>); the terminal view and the slash command reach it
/// through <see cref="ManageAsync"/> and <see cref="ExecuteCommandAsync"/>.</summary>
public sealed class McpServerManager
{
    internal const string Usage = "Usage: /mcp, /mcp login [server], /mcp logout [server], /mcp reconnect [server]";
    private static readonly ImmutableArray<(McpExposure Exposure, string Name, string Description)> Exposures =
    [
        (McpExposure.Codemode, "codemode", "called from codemode scripts, which find them with searchTools()"),
        (McpExposure.Deferred, "deferred", "not declared until tool_search loads them, then called directly; no codemode needed"),
        (McpExposure.Direct, "direct", "declared to the model like built-in tools")
    ];

    internal enum ConnectionState { Starting, Connecting, Connected, NeedsAuth, Failed, Closed }

    /// <summary>A configured server. A disabled server has no connection.</summary>
    private sealed class Slot(McpServerEntry entry)
    {
        public McpServerEntry Entry = entry;
        /// <summary>For servers extensions registered: the config as registered, to detect re-registrations (`/mcp` changes do not count).</summary>
        public readonly string? Registered = entry.Scope == McpConfigurationScope.Extension ? entry.Config.Raw.ToString() : null;
        public McpPreparedServer? Server;
        public ConnectionState State;
        public string? Error, Message;
        /// <summary>Stored tokens when a sign-in was needed, to notice a sign-in done outside the session.</summary>
        public string? TokensAtSignIn;
        /// <summary>Settles when the connection started for the server connected or failed.</summary>
        public Task Ready = Task.CompletedTask;
        /// <summary>Resources the server listed when it connected, without MCP App resources.</summary>
        public int Resources;
    }

    internal sealed record Dependencies(
        Func<McpServerEntry, ReplaceableAgentSession, AgentSessionAttachment, CancellationToken, ValueTask<McpPreparedServer>> Bind,
        McpResourceToolsPublisher? Resources, McpOAuthCredentialStore Credentials, Func<HttpClient> CreateClient,
        Func<string, string, string> ResolveSecret, Action<string> OpenUrl, string AgentDirectory)
    {
        /// <summary>Saves a change to the mcp.json that defines the server (its project override when set).</summary>
        public Action<McpServerEntry, bool?, McpExposure?, bool>? UpdateConfig { get; init; }
        /// <summary>autoEnableCodemode of the configuration: whether `codemode` servers activate the codemode tool.</summary>
        public bool AutoEnableCodemode { get; init; } = true;
        /// <summary>The servers changed (enabled, disabled, exposure), for the `mcp_servers` prompt section.</summary>
        public Action<ImmutableArray<McpServerEntry>>? ServersChanged { get; init; }
        /// <summary>The server's last `WWW-Authenticate` challenge (connection.challenge), which a sign-in answers; null clears it.</summary>
        public Func<string, McpOAuthChallenge?>? Challenge { get; init; }
        public Action<string>? ClearChallenge { get; init; }
    }

    private readonly object gate = new();
    private readonly List<Slot> servers;
    private readonly Dependencies dependencies;
    private readonly List<Action> listeners = [];
    private ReplaceableAgentSession? owner;
    private AgentSessionAttachment? attachment;
    private Task pending = Task.CompletedTask;

    internal McpServerManager(ImmutableArray<McpServerEntry> entries, ImmutableArray<string> configErrors, string? projectConfig,
        Dependencies dependencies)
    {
        servers = [.. entries.Select(entry => new Slot(entry))];
        ConfigErrors = configErrors; ProjectConfig = projectConfig; this.dependencies = dependencies;
    }

    /// <summary>Configuration errors found at session start.</summary>
    public ImmutableArray<string> ConfigErrors { get; }
    /// <summary>The trusted project's mcp.json, where project overrides are saved; null when the project is not trusted.</summary>
    public string? ProjectConfig { get; }
    /// <summary>Registered servers that mcp.json overrides, shown in the status and the manager.</summary>
    public ImmutableArray<string> Overridden { get; internal set; } = [];

    /// <summary>Subscribes to changes of any server's state; dispose the result to unsubscribe.</summary>
    public IDisposable Subscribe(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (gate) listeners.Add(listener);
        return new Unsubscription(() => { lock (gate) listeners.Remove(listener); });
    }

    private sealed class Unsubscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    private void Changed()
    {
        Action[] current; lock (gate) current = [.. listeners];
        foreach (var listener in current) try { listener(); } catch (Exception) { /* A view must not break the manager. */ }
    }

    // ---------------------------------------------------------------------------------------------
    // Session binding and background connections
    // ---------------------------------------------------------------------------------------------

    internal void Bind(ReplaceableAgentSession actualOwner, AgentSessionAttachment actualAttachment, McpBackgroundConnections? connections)
    {
        lock (gate)
        {
            owner = actualOwner; attachment = actualAttachment;
            pending = connections?.WhenSettled(_ => true) ?? Task.CompletedTask;
            foreach (var slot in servers.Where(slot => slot.Entry.Config.Enabled))
                slot.Ready = connections?.WhenSettled(entry => entry.Name == slot.Entry.Name) ?? Task.CompletedTask;
        }
    }

    /// <summary>index.ts waitForServers: wait for the latest connection attempts of the enabled servers <paramref name="include"/>
    /// selects, including attempts started while waiting, or until <paramref name="token"/> is cancelled (then it returns).</summary>
    internal async Task WaitForServersAsync(Func<McpServerEntry, bool> include, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(include);
        for (; ; )
        {
            (Slot Slot, Task Ready)[] waiting;
            lock (gate) waiting = [.. servers.Where(slot => IsEnabled(slot) && include(slot.Entry)).Select(slot => (slot, slot.Ready))];
            if (waiting.Length == 0 || token.IsCancellationRequested) return;
            try { await Task.WhenAll(waiting.Select(row => row.Ready)).WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            lock (gate)
                if (waiting.All(row => !IsEnabled(row.Slot) || !servers.Contains(row.Slot) || ReferenceEquals(row.Ready, row.Slot.Ready))) return;
        }
    }

    /// <summary>server.ready: the server's readiness follows the attempt <paramref name="start"/> begins, from before it begins;
    /// readiness never faults.</summary>
    private Task Attempt(Slot slot, Func<Task> start)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) slot.Ready = ready.Task;
        Task attempt;
        try { attempt = start(); }
        catch (Exception error) { attempt = Task.FromException(error); }
        _ = attempt.ContinueWith(_ => ready.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return attempt;
    }

    /// <summary>A background connection bound its server (it is connecting now).</summary>
    internal void Track(McpServerEntry entry, McpPreparedServer server)
    {
        lock (gate)
        {
            if (Find(entry.Name) is not { } slot) return;
            slot.Server = server; slot.State = ConnectionState.Connecting; slot.Error = null;
        }
        Changed();
    }

    private readonly SemaphoreSlim registrations = new(1, 1);

    /// <summary>index.ts mcp_servers_change: the servers extensions registered changed during the session. Unregistered servers and
    /// servers registered again with another config are closed and dropped (their tools become unreachable); new ones are added and
    /// connect right away.</summary>
    internal async Task ApplyRegistrationsAsync((ImmutableArray<McpServerEntry> Servers, ImmutableArray<string> Overridden) current)
    {
        await registrations.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Session() is null) return;
            var next = current.Servers.Where(entry => entry.Scope == McpConfigurationScope.Extension).ToDictionary(entry => entry.Name, StringComparer.Ordinal);
            Slot[] removed; Slot[] added;
            lock (gate)
            {
                removed = [.. servers.Where(slot => slot.Entry.Scope == McpConfigurationScope.Extension &&
                    (!next.TryGetValue(slot.Entry.Name, out var entry) || entry.Config.Raw.ToString() != slot.Registered || entry.Source != slot.Entry.Source))];
                foreach (var slot in removed) servers.Remove(slot);
                added = [.. next.Values.Where(entry => !servers.Any(slot => slot.Entry.Name == entry.Name)).Select(entry => new Slot(entry))];
                servers.AddRange(added);
                Overridden = current.Overridden;
            }
            Changed();
            foreach (var slot in removed) try { await CloseServerAsync(slot).ConfigureAwait(false); } catch (Exception) { /* The state is gone with the slot. */ }
            ImmutableArray<McpServerEntry> entries; lock (gate) entries = [.. servers.Select(slot => slot.Entry)];
            try { dependencies.ServersChanged?.Invoke(entries); } catch (Exception) { /* The section follows on the next change. */ }
            await Task.WhenAll(added.Where(IsEnabled).Select(slot => Attempt(slot, () => StartAsync(slot, CancellationToken.None)))).ConfigureAwait(false);
            EnsureDiscoveryActive();
            Changed();
        }
        catch (Exception) { /* A session that ended meanwhile keeps nothing to apply. */ }
        finally { registrations.Release(); }
    }

    /// <summary>runtime.ts refreshTools: a changed tool list could not be read; the state shows why.</summary>
    internal void RefreshFailed(string name, Exception error)
    {
        lock (gate) { if (Find(name) is { } slot) slot.Error = "Failed to refresh tools: " + FailureText(error); }
        Changed();
    }

    /// <summary>A background connection settled.</summary>
    internal void Settled(McpBackgroundConnectionReport report)
    {
        lock (gate)
        {
            if (Find(report.Entry.Name) is not { } slot) return;
            Record(slot, report.Failure);
        }
        Changed();
    }

    private void Record(Slot slot, Exception? failure)
    {
        if (failure is null) { slot.State = ConnectionState.Connected; slot.Error = null; slot.TokensAtSignIn = null; return; }
        if (NeedsSignIn(failure, slot.Entry))
        {
            slot.State = ConnectionState.NeedsAuth; slot.Error = null;
            slot.TokensAtSignIn ??= StoredTokens(slot);
        }
        else { slot.State = ConnectionState.Failed; slot.Error = FailureText(failure); slot.TokensAtSignIn = null; }
    }

    /// <summary>runtime.ts needsSignIn: the OAuth sign-in is required, or a server that authenticates (OAuth or `auth.provider`)
    /// still answers 401 (McpAuthRequiredError).</summary>
    internal static bool NeedsSignIn(Exception failure, McpServerEntry? entry = null)
    {
        var authenticated = entry is not null && entry.Config.Transport == McpTransportKind.Http &&
            (McpConfigurationReader.UsesOAuth(entry.Config) || entry.Config.AuthProvider is not null);
        bool Matches(Exception? current)
        {
            for (; current is not null; current = current.InnerException)
            {
                if (current is McpOAuthAuthorizationRequiredException) return true;
                if (authenticated && current is PiSharp.Extensions.Mcp.Transport.McpHttpStatusException { StatusCode: 401 }) return true;
                if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(Matches)) return true;
            }
            return false;
        }
        return Matches(failure);
    }

    /// <summary>runtime.ts withClient: a call the server rejects for authentication (after any refresh) drops the connection and
    /// marks the server as needing a sign-in (markNeedsAuth), so `/mcp` offers it and a sign-in elsewhere reconnects it.</summary>
    internal void CallFailed(string name, Exception failure)
    {
        lock (gate)
        {
            if (Find(name) is not { } slot || !IsEnabled(slot) || !NeedsSignIn(failure, slot.Entry)) return;
            slot.State = ConnectionState.NeedsAuth; slot.Error = null;
            slot.TokensAtSignIn ??= StoredTokens(slot);
        }
        Changed();
    }

    private static string FailureText(Exception failure)
    {
        var current = failure;
        while (current is AggregateException { InnerExceptions.Count: 1 } single) current = single.InnerExceptions[0];
        return current.Message.Length == 0 ? "unknown error" : current.Message;
    }

    private string StoredTokens(Slot slot) => OAuthUrl(slot) is { } url
        ? JsonSerializer.Serialize(dependencies.Credentials.Tokens(slot.Entry.Name, url)) : "null";

    /// <summary>index.ts turn_start reconnectSignedIn: servers waiting for a sign-in reconnect once their stored credentials
    /// changed, for example after `mcp login` in another process.</summary>
    internal async Task ReconnectSignedInAsync(CancellationToken token)
    {
        Slot[] signedIn;
        lock (gate) signedIn = [.. servers.Where(slot => slot.State == ConnectionState.NeedsAuth && slot.TokensAtSignIn is { } tokens && tokens != StoredTokens(slot))];
        if (signedIn.Length == 0) return;
        foreach (var slot in signedIn) lock (gate) slot.TokensAtSignIn = null;
        await Task.WhenAll(signedIn.Select(slot => ReconnectCoreAsync(slot, token))).ConfigureAwait(false);
    }

    private Slot? Find(string name) => servers.FirstOrDefault(slot => slot.Entry.Name == name);
    private static bool IsEnabled(Slot slot) => slot.Entry.Config.Enabled;
    private static Uri? OAuthUrl(Slot slot) =>
        McpConfigurationReader.UsesOAuth(slot.Entry.Config) && slot.Entry.Config.Raw.Value.TryGetProperty("url", out var url) &&
        Uri.TryCreate(url.GetString(), UriKind.Absolute, out var parsed) ? parsed : null;
    /// <summary>usesOAuth: an enabled HTTP server without an Authorization header that started connecting.</summary>
    private static bool UsesOAuth(Slot slot) => IsEnabled(slot) && slot.Server is not null && OAuthUrl(slot) is not null;

    /// <summary>The connection state, with a dropped connection shown as disconnected.</summary>
    private static string StateName(Slot slot) => slot.State switch
    {
        ConnectionState.Connected when slot.Server?.Snapshot.Catalog.Connected == false => "disconnected",
        ConnectionState.Connected => "connected", ConnectionState.Connecting => "connecting", ConnectionState.NeedsAuth => "needs-auth",
        ConnectionState.Failed => "failed", ConnectionState.Closed => "closed", _ => "starting"
    };

    private static int ToolCount(Slot slot) => slot.Server?.Snapshot.Catalog.Tools.Length ?? 0;

    /// <summary>describeState: short state for lists; <paramref name="withError"/> appends the first line of a failure.</summary>
    private static string DescribeState(Slot slot, bool withError = true)
    {
        if (!IsEnabled(slot)) return "disabled";
        if (slot.Server is null && slot.State == ConnectionState.Starting) return "starting";
        switch (StateName(slot))
        {
            case "needs-auth": return "needs sign-in";
            case "failed": return withError ? "failed: " + FirstLine(slot.Error ?? "unknown error") : "failed";
            case "connected":
                var tools = ToolCount(slot);
                var resources = slot.Resources > 0 ? $" · {slot.Resources} resource{(slot.Resources == 1 ? "" : "s")}" : "";
                return $"connected · {tools} tool{(tools == 1 ? "" : "s")}{resources}";
            case "connecting": return "connecting…";
            case var other: return other;
        }
    }

    private static int AttentionRank(Slot slot) => !IsEnabled(slot) ? 5 : StateName(slot) switch
    { "needs-auth" => 0, "failed" => 1, "disconnected" => 2, "connected" => 4, _ => 3 };

    private static string FirstLine(string text) => text.Split('\n', 2)[0];

    private static string DescribeTransport(McpServerEntry entry)
    {
        var raw = entry.Config.Raw.Value;
        if (raw.TryGetProperty("url", out var url)) return url.GetString() ?? "";
        var parts = new List<string> { raw.GetProperty("command").GetString() ?? "" };
        if (raw.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array) parts.AddRange(args.EnumerateArray().Select(arg => arg.GetString() ?? ""));
        return string.Join(' ', parts);
    }

    private static string ExposureName(McpExposure exposure) => exposure.ToString().ToLowerInvariant();
    private static string ScopeName(McpServerEntry entry) => entry.Scope switch
    { McpConfigurationScope.Project => "project", McpConfigurationScope.Extension => "extension", _ => "global" };

    /// <summary>Every configured server as it is now.</summary>
    public IReadOnlyList<McpServerStatus> Servers
    {
        get
        {
            lock (gate)
                return [.. servers.Select(slot => new McpServerStatus(slot.Entry.Name, slot.Entry, IsEnabled(slot) ? StateName(slot) : "disabled",
                    DescribeState(slot), ToolCount(slot), slot.Server?.Snapshot.Catalog.HasResources == true, slot.Entry.Config.Exposure,
                    StateName(slot) == "connected" ? null : slot.Error, slot.Message, UsesOAuth(slot)))];
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Actions
    // ---------------------------------------------------------------------------------------------

    private (ReplaceableAgentSession Owner, AgentSessionAttachment Attachment)? Session()
    {
        lock (gate) return owner is { } o && attachment is { } a && ReferenceEquals(o.Current, a) && !a.LifetimeToken.IsCancellationRequested ? (o, a) : null;
    }

    /// <summary>reconnect: connect the server again; failures show in its state. Returns an error message.</summary>
    public async Task<string?> ReconnectAsync(string name, CancellationToken token = default)
    {
        Slot? slot; lock (gate) slot = Find(name);
        if (slot is null) return $"No MCP server named \"{name}\".";
        if (!IsEnabled(slot) || slot.Server is null) return $"MCP server \"{name}\" is disabled.";
        await ReconnectCoreAsync(slot, token).ConfigureAwait(false);
        EnsureDiscoveryActive();
        lock (gate) return ReconnectFailure(slot);
    }

    /// <summary>What reconnect reports when the connection failed: runtime.ts connectFailed or signInRequiredMessage.</summary>
    private static string? ReconnectFailure(Slot slot) => slot.State switch
    {
        ConnectionState.Failed => $"MCP server \"{slot.Entry.Name}\" failed to connect: {slot.Error}",
        ConnectionState.NeedsAuth => McpProviderTokenAuthentication.SignInRequiredMessage(slot.Entry),
        _ => null
    };

    private async Task ReconnectCoreAsync(Slot slot, CancellationToken token)
    {
        Task previous; lock (gate) previous = slot.Ready;
        await Attempt(slot, () => ReconnectAfterAsync(previous)).ConfigureAwait(false);

        async Task ReconnectAfterAsync(Task before)
        {
            await before.ConfigureAwait(false);
            await ReconnectServerAsync(slot, token).ConfigureAwait(false);
        }
    }

    private async Task ReconnectServerAsync(Slot slot, CancellationToken token)
    {
        McpPreparedServer? server; lock (gate) { server = slot.Server; slot.State = ConnectionState.Connecting; slot.Error = null; }
        Changed();
        Exception? failure = null;
        try
        {
            if (server is null) throw new InvalidOperationException($"MCP server \"{slot.Entry.Name}\" is disabled.");
            try { await server.ReconnectAsync(token).ConfigureAwait(false); }
            // A server whose earlier publication or connection cannot be resumed is replaced by a fresh one.
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException && !token.IsCancellationRequested)
            { await RebindAsync(slot, token).ConfigureAwait(false); return; }
            await UpdateResourcesAsync(slot, token).ConfigureAwait(false);
        }
        catch (Exception error) when (!token.IsCancellationRequested) { failure = error; }
        lock (gate) Record(slot, failure);
        Changed();
    }

    /// <summary>Close the server's connection (withdrawing its tools) and connect a new one with the current configuration.</summary>
    private async Task RebindAsync(Slot slot, CancellationToken token)
    {
        await CloseServerAsync(slot).ConfigureAwait(false);
        await StartAsync(slot, token).ConfigureAwait(false);
    }

    private async Task CloseServerAsync(Slot slot)
    {
        McpPreparedServer? server; lock (gate) { server = slot.Server; slot.Server = null; }
        if (dependencies.Resources is { } resources)
            try { await resources.UpdateAsync(slot.Entry.Name, null, slot.Entry.Config.Exposure, false, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* Resource tools follow on the next change. */ }
        if (server is not null) await server.CloseAsync().ConfigureAwait(false);
    }

    private async Task StartAsync(Slot slot, CancellationToken token)
    {
        if (Session() is not { } session) throw new InvalidOperationException("The MCP session has ended.");
        Exception? failure = null;
        lock (gate) { slot.State = ConnectionState.Connecting; slot.Error = null; }
        Changed();
        try
        {
            var server = await dependencies.Bind(slot.Entry, session.Owner, session.Attachment, token).ConfigureAwait(false);
            lock (gate) slot.Server = server;
            await server.ConnectAsync(token).ConfigureAwait(false);
            await UpdateResourcesAsync(slot, token).ConfigureAwait(false);
        }
        catch (Exception error) when (!token.IsCancellationRequested) { failure = error; }
        lock (gate) Record(slot, failure);
        Changed();
    }

    private async Task UpdateResourcesAsync(Slot slot, CancellationToken token)
    {
        McpPreparedServer? server; lock (gate) server = slot.Server;
        if (dependencies.Resources is { } resources)
            await resources.UpdateAsync(slot.Entry.Name, server, slot.Entry.Config.Exposure, server?.Snapshot.Catalog is { Connected: true, HasResources: true }, token)
                .ConfigureAwait(false);
        if (server is not null) await CountResourcesAsync(slot, server, token).ConfigureAwait(false);
    }

    /// <summary>The resources a connected server lists (fetchResources at connect), for describeState.</summary>
    internal async Task CountResourcesAsync(string name, McpPreparedServer server, CancellationToken token)
    {
        Slot? slot; lock (gate) slot = Find(name);
        if (slot is null || !ReferenceEquals(slot.Server, server)) return;
        await CountResourcesAsync(slot, server, token).ConfigureAwait(false);
        Changed();
    }

    private async Task CountResourcesAsync(Slot slot, McpPreparedServer server, CancellationToken token)
    {
        var count = 0;
        if (server.Snapshot.Catalog is { Connected: true, HasResources: true })
            try { count = (await server.CountResourcesAsync(token).ConfigureAwait(false)).Resources; }
            catch (Exception) when (!token.IsCancellationRequested) { /* A server whose lists fail still connects. */ }
        lock (gate) slot.Resources = count;
    }

    /// <summary>saveConfig: save a change to the server's mcp.json (or a project override); returns an error message.</summary>
    private string? SaveConfig(Slot slot, bool? enabled, McpExposure? exposure, bool inProject)
    {
        var entry = slot.Entry;
        var overrideFile = inProject ? ProjectConfig : entry.Override;
        if (overrideFile is not null) entry = entry with { Override = overrideFile };
        if (entry.Scope != McpConfigurationScope.Extension)
        {
            try
            {
                if (dependencies.UpdateConfig is { } update) update(entry, enabled, exposure, entry.Override is not null);
                else UpdateConfigFile(entry.Override ?? entry.Source, entry.Name, enabled, exposure, entry.Override is not null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException)
            { return $"Could not update {entry.Override ?? entry.Source}: {error.Message}"; }
        }
        var raw = JsonNode.Parse(entry.Config.Raw.ToString())!.AsObject();
        if (enabled is { } on) raw["enabled"] = on;
        if (exposure is { } chosen) raw["exposure"] = ExposureName(chosen);
        var validated = McpConfigurationReader.Validate(entry.Name, JsonSerializer.SerializeToElement(raw));
        if (validated.Config is not { } config) return validated.Error;
        ImmutableArray<McpServerEntry> current;
        lock (gate) { slot.Entry = entry with { Config = config }; current = [.. servers.Select(item => item.Entry)]; }
        try { dependencies.ServersChanged?.Invoke(current); } catch (Exception) { /* The section follows on the next change. */ }
        return null;
    }

    /// <summary>config.ts updateMcpServerConfig: change `enabled` or `exposure` of one server in the mcp.json that defines or overrides
    /// it. With <paramref name="asOverride"/> a missing entry is added as an override, and overrides keep default values; otherwise
    /// `enabled: true` and `exposure: "codemode"` (the defaults) remove the key. Other content is kept.</summary>
    internal static void UpdateConfigFile(string path, string name, bool? enabled, McpExposure? exposure, bool asOverride) =>
        PiSharp.Cli.Commands.McpCommand.EditMcpServers(path, (configured, root) =>
        {
            var server = configured?[name];
            if (server is null && asOverride)
            {
                configured ??= [];
                server = new JsonObject(); configured[name] = server; root["mcpServers"] = configured;
            }
            if (server is not JsonObject value) throw new InvalidOperationException($"{path} does not define MCP server \"{name}\"");
            var keepDefaults = !value.ContainsKey("command") && !value.ContainsKey("url") && !value.ContainsKey("type");
            if (enabled is { } on) { if (on && !keepDefaults) value.Remove("enabled"); else value["enabled"] = on; }
            if (exposure is { } chosen) { if (chosen == McpExposure.Codemode && !keepDefaults) value.Remove("exposure"); else value["exposure"] = ExposureName(chosen); }
            return true;
        });

    /// <summary>setEnabled: save the change, then connect or disconnect the server. Returns an error message when the config could
    /// not be saved; connection errors show in the state.</summary>
    public async Task<string?> SetEnabledAsync(string name, bool enabled, bool inProject = false, CancellationToken token = default)
    {
        Slot? slot; lock (gate) slot = Find(name);
        if (slot is null) return $"No MCP server named \"{name}\".";
        if (SaveConfig(slot, enabled, null, inProject) is { } failed) return failed;
        McpPreparedServer? server; Task previous;
        lock (gate) { server = slot.Server; previous = slot.Ready; if (!enabled) { slot.State = ConnectionState.Starting; slot.Error = null; } }
        if (!enabled)
        {
            Changed();
            // hideTools: the server disconnects and its tools leave the session; its registration stays for a re-enable.
            if (server is not null) await Attempt(slot, () => DisableAsync(previous, server)).ConfigureAwait(false);
            Changed();
            return null;
        }
        // startConnection: the server connects again with the registration it had.
        await Attempt(slot, () => server is null ? StartAsync(slot, token) : EnableAsync(previous, server, token)).ConfigureAwait(false);
        EnsureDiscoveryActive();
        return null;
    }

    private async Task DisableAsync(Task previous, McpPreparedServer server)
    {
        await previous.ConfigureAwait(false);
        McpServerEntry entry; lock (gate) entry = FindEntry(server) ?? server.Entry;
        if (dependencies.Resources is { } resources)
            try { await resources.UpdateAsync(entry.Name, null, entry.Config.Exposure, false, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* Resource tools follow on the next change. */ }
        try { await server.ReconfigureAsync(entry, connect: false, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* A closed server has nothing left to withdraw. */ }
    }

    private async Task EnableAsync(Task previous, McpPreparedServer server, CancellationToken token)
    {
        await previous.ConfigureAwait(false);
        Slot? slot; lock (gate) { slot = servers.FirstOrDefault(item => ReferenceEquals(item.Server, server)); if (slot is not null) { slot.State = ConnectionState.Connecting; slot.Error = null; } }
        if (slot is null) return;
        Changed();
        Exception? failure = null;
        try
        {
            try { await server.ReconfigureAsync(slot.Entry, connect: true, token).ConfigureAwait(false); }
            // A server whose earlier publication or connection cannot be resumed is replaced by a fresh one.
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException && !NeedsSignIn(error, slot.Entry) && !token.IsCancellationRequested)
            { await RebindAsync(slot, token).ConfigureAwait(false); return; }
            await UpdateResourcesAsync(slot, token).ConfigureAwait(false);
        }
        catch (Exception error) when (!token.IsCancellationRequested) { failure = error; }
        lock (gate) Record(slot, failure);
        Changed();
    }

    private McpServerEntry? FindEntry(McpPreparedServer server) => servers.FirstOrDefault(item => ReferenceEquals(item.Server, server))?.Entry;

    /// <summary>setExposure: save the change and register the server's tools again with the new exposure (when it is connected),
    /// without reconnecting; tools no longer exposed directly leave the declared set, tools that became direct are activated.</summary>
    public async Task<string?> SetExposureAsync(string name, McpExposure exposure, CancellationToken token = default)
    {
        Slot? slot; lock (gate) slot = Find(name);
        if (slot is null) return $"No MCP server named \"{name}\".";
        if (SaveConfig(slot, null, exposure, false) is { } failed) return failed;
        if (IsEnabled(slot) && slot.Server is { } server)
        {
            try { await server.ReconfigureAsync(slot.Entry, connect: false, token).ConfigureAwait(false); }
            catch (Exception error) when (!token.IsCancellationRequested) { lock (gate) slot.Error = FailureText(error); }
            // syncResourceTools: the resource tools follow the widest exposure of the servers they reach.
            if (dependencies.Resources is { } resources)
                try { await resources.UpdateAsync(slot.Entry.Name, server, slot.Entry.Config.Exposure, server.Snapshot.Catalog is { Connected: true, HasResources: true }, token).ConfigureAwait(false); }
                catch (Exception) when (!token.IsCancellationRequested) { /* The resource tools follow on the next change. */ }
        }
        if (Session() is { } session)
        {
            var indirect = session.Attachment.Session.CaptureToolCatalogRegistry().RegisteredTools
                .Where(tool => tool.Exposure != PiSharp.Contracts.ToolExposure.Direct).Select(tool => tool.Adapter.Name).ToHashSet(StringComparer.Ordinal);
            var serverTools = ServerToolNames(session.Attachment, slot.Entry.Name);
            var active = session.Attachment.Session.GetToolActivationSelection().Names;
            var kept = active.Where(tool => !serverTools.Contains(tool) || !indirect.Contains(tool)).ToImmutableArray();
            if (kept.Length != active.Length)
                try { session.Attachment.Session.ScheduleToolActivation(kept, token); }
                catch (InvalidOperationException) { /* Another selected-state change runs; the tools stay until the next change. */ }
        }
        EnsureDiscoveryActive();
        Changed();
        return null;
    }

    /// <summary>ensureDiscoveryActive after a change: activate codemode for enabled `codemode` servers (unless autoEnableCodemode is
    /// false) and tool_search for `deferred` ones, when the session registers them and they are not active yet.</summary>
    private void EnsureDiscoveryActive()
    {
        if (Session() is not { } session) return;
        HashSet<McpExposure> exposures;
        lock (gate) exposures = [.. servers.Where(IsEnabled).SelectMany(slot => McpConfigurationReader.ConfiguredExposures(slot.Entry.Config))];
        var registered = session.Attachment.Session.CaptureToolCatalogRegistry().RegisteredTools.Select(tool => tool.Adapter.Name).ToHashSet(StringComparer.Ordinal);
        var active = session.Attachment.Session.GetToolActivationSelection().Names;
        var activate = new List<string>();
        if (exposures.Contains(McpExposure.Codemode) && dependencies.AutoEnableCodemode && registered.Contains(McpCodemode.Name) && !active.Contains(McpCodemode.Name))
            activate.Add(McpCodemode.Name);
        if (exposures.Contains(McpExposure.Deferred) && registered.Contains(McpToolSearch.Name) && !active.Contains(McpToolSearch.Name))
            activate.Add(McpToolSearch.Name);
        if (activate.Count == 0) return;
        try { session.Attachment.Session.ScheduleToolActivation([.. active, .. activate]); }
        catch (InvalidOperationException) { /* A run started meanwhile; the next change activates them. */ }
    }

    /// <summary>The registered tools of a server: those in its namespace.</summary>
    private static HashSet<string> ServerToolNames(AgentSessionAttachment attachment, string server)
    {
        var namespaceName = McpCatalogPlanner.Namespace(server);
        return attachment.Session.CaptureToolCatalogRegistry().RegisteredTools.Where(tool => tool.Namespace?.Name == namespaceName)
            .Select(tool => tool.Adapter.Name).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>signIn: run the sign-in, then reconnect. Returns an error message; <paramref name="cancel"/> or the session's end
    /// stops the sign-in.</summary>
    public async Task<string?> SignInAsync(string name, McpSignInPromptAdapter prompt, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        Slot? slot; lock (gate) slot = Find(name);
        if (slot is null || !UsesOAuth(slot) || OAuthUrl(slot) is not { } url) return $"MCP server \"{name}\" does not use OAuth.";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, Session()?.Attachment.LifetimeToken ?? new CancellationToken(true));
        try
        {
            var settings = Authentication.McpOAuthSettings.From(slot.Entry, dependencies.ResolveSecret);
            using var client = dependencies.CreateClient();
            await Authentication.McpSignIn.SignInAsync(new(url, dependencies.Credentials.ForServer(slot.Entry.Name, url), settings,
                new(prompt.ShowAuthorizationUrl, prompt.PromptForRedirectUrl), AdmittedHttpClientRequestFactory.Create(client))
            { Challenge = dependencies.Challenge?.Invoke(slot.Entry.Name) }, linked.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is Authentication.McpSignInCancelledException || error is OperationCanceledException && linked.IsCancellationRequested)
        { return "Sign-in cancelled."; }
        catch (Exception error) { return "Sign-in failed: " + FailureText(error); }
        // The challenge that asked for this sign-in (for example for more scope) is answered.
        try { dependencies.ClearChallenge?.Invoke(slot.Entry.Name); } catch (Exception) { /* Only the next sign-in reads it. */ }
        lock (gate) slot.TokensAtSignIn = null;
        await ReconnectCoreAsync(slot, cancel).ConfigureAwait(false);
        EnsureDiscoveryActive();
        lock (gate) return slot.State is ConnectionState.Failed or ConnectionState.NeedsAuth
            ? "Signed in, but " + (slot.State == ConnectionState.NeedsAuth ? $"MCP server \"{name}\" requires sign-in. Run /mcp to sign in." : slot.Error) : null;
    }

    /// <summary>signOut: delete the stored credentials and disconnect; returns whether credentials were stored.</summary>
    public async Task<bool> SignOutAsync(string name, CancellationToken token = default)
    {
        Slot? slot; lock (gate) slot = Find(name);
        if (slot is null || !UsesOAuth(slot) || OAuthUrl(slot) is not { } url) return false;
        var removed = dependencies.Credentials.Remove(slot.Entry.Name, url);
        if (slot.Server is { } server) try { await server.DisconnectAsync(token).ConfigureAwait(false); } catch (Exception) { /* State shows it. */ }
        lock (gate) { slot.State = ConnectionState.NeedsAuth; slot.Error = null; slot.TokensAtSignIn = StoredTokens(slot); }
        Changed();
        return removed;
    }

    // ---------------------------------------------------------------------------------------------
    // Manager menus (`/mcp` in the terminal)
    // ---------------------------------------------------------------------------------------------

    private IEnumerable<string> Notices() => ConfigErrors.Select(error => "config: " + error).Concat(Overridden.Select(line => "overridden: " + line));

    private string NoServers() =>
        $"No MCP servers configured. Add them to {Path.GetFullPath(Path.Combine(dependencies.AgentDirectory, "mcp.json"))} or .pi/mcp.json.";

    /// <summary>serversMenu: every server, those needing attention first.</summary>
    public McpMenu ServersMenu()
    {
        lock (gate)
        {
            var notices = string.Join("\n", Notices());
            return new("MCP servers", [.. servers.OrderBy(AttentionRank).ThenBy(slot => slot.Entry.Name, StringComparer.Ordinal)
                .Select(slot => new McpMenuItem(slot.Entry.Name, slot.Entry.Name,
                    $"{DescribeState(slot)} · {ExposureName(slot.Entry.Config.Exposure)} · {(slot.Entry.Override is not null ? "global, project override" : ScopeName(slot.Entry))}"))],
                "manage", "close")
            { Error = notices.Length == 0 ? null : notices, Empty = NoServers() };
        }
    }

    /// <summary>serverMenu: the actions of one server.</summary>
    public McpMenu ServerMenu(string name)
    {
        lock (gate)
        {
            if (Find(name) is not { } slot)
                return new(name, [], "", "back") { Empty = "This server is no longer configured." };
            var entry = slot.Entry;
            var saved = entry.Scope == McpConfigurationScope.Extension ? "for this session"
                : entry.Override is not null ? "saved to the project mcp.json" : $"saved to the {ScopeName(entry)} mcp.json";
            // Global servers without an override can be turned on or off for the trusted project alone.
            var inProject = entry.Scope == McpConfigurationScope.Global && entry.Override is null && ProjectConfig is not null;
            const string InProjectSaved = "saved to the project mcp.json";
            var items = ImmutableArray.CreateBuilder<McpMenuItem>();
            if (!IsEnabled(slot))
            {
                items.Add(new("enable", "Enable", saved));
                if (inProject) items.Add(new("enable-project", "Enable in this project", InProjectSaved));
            }
            else
            {
                var state = StateName(slot);
                if (state == "needs-auth") items.Add(new("signin", "Sign in", "opens the browser"));
                if (state == "connected") items.Add(new("tools", "Tools", $"{ToolCount(slot)} offered"));
                if (state is "failed" or "disconnected" or "connected" or "needs-auth") items.Add(new("reconnect", "Reconnect"));
                if (state == "connected" && OAuthUrl(slot) is not null) items.Add(new("signout", "Sign out", "deletes the stored credentials"));
                items.Add(new("exposure", "Exposure", ExposureName(entry.Config.Exposure)));
                items.Add(new("disable", "Disable", saved));
                if (inProject) items.Add(new("disable-project", "Disable in this project", InProjectSaved));
            }
            var details = new List<string> { DescribeTransport(entry), $"{ScopeName(entry)}: {entry.Source}" };
            if (entry.Override is not null) details.Add("project override: " + entry.Override);
            details.Add("State: " + DescribeState(slot, false));
            var error = string.Join("\n", new[] { slot.Message, StateName(slot) == "connected" ? null : slot.Error }.Where(line => line is not null));
            return new($"MCP server {name}", items.ToImmutable(), "select", "back")
            { Details = string.Join("\n", details), Error = error.Length == 0 ? null : error, Selected = items.Count == 0 ? null : items[0].Value };
        }
    }

    /// <summary>showTools: the server's tools with their effective exposure.</summary>
    public McpMenu ToolsMenu(string name)
    {
        lock (gate)
        {
            var slot = Find(name);
            var exposure = slot?.Entry.Config.Exposure ?? McpExposure.Codemode;
            var overridden = slot?.Entry.Config.ToolExposure.Length > 0;
            var description = exposure == McpExposure.Hidden ? "unreachable" : Exposures.Single(row => row.Exposure == exposure).Description;
            var tools = slot?.Server?.Snapshot.Catalog.Tools ?? [];
            return new($"Tools of {name}", [.. tools.Select(tool =>
            {
                var toolExposure = McpConfigurationReader.GetToolExposure(slot!.Entry.Config, tool.Name);
                var line = FirstLine(tool.Description ?? "");
                return new McpMenuItem(tool.Name, tool.Name, toolExposure == exposure ? line : $"[{ExposureName(toolExposure)}] {line}");
            })], "back", "back")
            { Details = $"Exposure {ExposureName(exposure)}: {description}{(overridden ? "\nSome tools override it with toolExposure." : "")}", Empty = "The server offers no tools." };
        }
    }

    /// <summary>chooseExposure's menu.</summary>
    public McpMenu ExposureMenu(string name)
    {
        lock (gate)
        {
            var slot = Find(name);
            var current = slot?.Entry.Config.Exposure ?? McpExposure.Codemode;
            var details = slot?.Entry.Scope == McpConfigurationScope.Extension
                ? $"Applies to this session; the server is registered by {slot.Entry.Source}." : $"Saved to {slot?.Entry.Override ?? slot?.Entry.Source}.";
            return new($"Exposure of {name}", [.. Exposures.Select(row => new McpMenuItem(row.Name, (row.Exposure == current ? "✓ " : "  ") + row.Name, row.Description))],
                "save", "back") { Details = details, Selected = ExposureName(current) };
        }
    }

    /// <summary>manage: the servers menu, then a server's actions, until closed.</summary>
    public async Task ManageAsync(IMcpManagerUi ui, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(ui);
        for (; ; )
        {
            var name = await ui.MenuAsync(ServersMenu, Subscribe).ConfigureAwait(false);
            if (name is null || token.IsCancellationRequested) return;
            for (; ; )
            {
                var action = await ui.MenuAsync(() => ServerMenu(name), Subscribe).ConfigureAwait(false);
                Slot? slot; lock (gate) slot = Find(name);
                if (action is null || slot is null || token.IsCancellationRequested) break;
                await RunActionAsync(ui, slot, action, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>runAction: one action of the server menu. Connecting and disconnecting run in the background so the menu stays usable.</summary>
    public async Task RunActionAsync(IMcpManagerUi ui, string name, string action, CancellationToken token = default)
    {
        Slot? slot; lock (gate) slot = Find(name);
        if (slot is not null) await RunActionAsync(ui, slot, action, token).ConfigureAwait(false);
    }

    private async Task RunActionAsync(IMcpManagerUi ui, Slot slot, string action, CancellationToken token)
    {
        string? message = null;
        switch (action)
        {
            case "signin": message = await SignInWithUiAsync(ui, slot.Entry.Name).ConfigureAwait(false); break;
            case "reconnect": RunInBackground(slot, async () => { await ReconnectCoreAsync(slot, token).ConfigureAwait(false); return null; }); break;
            case "signout": await SignOutAsync(slot.Entry.Name, token).ConfigureAwait(false); break;
            case "tools": await ui.MenuAsync(() => ToolsMenu(slot.Entry.Name)).ConfigureAwait(false); break;
            case "exposure":
                var current = ExposureName(slot.Entry.Config.Exposure);
                var choice = await ui.MenuAsync(() => ExposureMenu(slot.Entry.Name)).ConfigureAwait(false);
                if (choice is not null && choice != current && Exposures.FirstOrDefault(row => row.Name == choice) is { Name: not null } chosen)
                    message = await SetExposureAsync(slot.Entry.Name, chosen.Exposure, token).ConfigureAwait(false);
                break;
            case "enable": case "disable": case "enable-project": case "disable-project":
                var enable = action.StartsWith("enable", StringComparison.Ordinal);
                RunInBackground(slot, () => SetEnabledAsync(slot.Entry.Name, enable, action.EndsWith("-project", StringComparison.Ordinal), token));
                break;
        }
        lock (gate) slot.Message = message;
        Changed();
    }

    private void RunInBackground(Slot slot, Func<Task<string?>> operation)
    {
        _ = Task.Run(async () =>
        {
            string? message;
            try { message = await operation().ConfigureAwait(false); }
            catch (Exception error) { message = FailureText(error); }
            lock (gate) slot.Message = message;
            Changed();
        });
    }

    /// <summary>signInWithUi: the manager view's sign-in screen, which shows the URL and cancels with its cancel key.</summary>
    public Task<string?> SignInWithUiAsync(IMcpManagerUi ui, string name)
    {
        var title = $"Sign in to {name}";
        var cancel = new CancellationTokenSource();
        void Status(string message) => ui.Status(title, message, () => cancel.Cancel());
        var authorizationUrl = "";
        Status("Contacting the authorization server…");
        return SignInAsync(name, new(url => { authorizationUrl = url.AbsoluteUri; dependencies.OpenUrl(url.AbsoluteUri); }, async signal =>
        {
            var value = await ui.RedirectUrlAsync(title, authorizationUrl, signal).ConfigureAwait(false);
            Status("Connecting…");
            return value;
        }), cancel.Token);
    }

    // ---------------------------------------------------------------------------------------------
    // `/mcp` command (status, login, logout, reconnect) outside the manager view
    // ---------------------------------------------------------------------------------------------

    /// <summary>formatStatus: every server's state, tools and exposure, then config errors.</summary>
    public string FormatStatus()
    {
        lock (gate)
        {
            if (servers.Count == 0 && ConfigErrors.IsEmpty && Overridden.IsEmpty) return NoServers();
            var lines = servers.Select(slot =>
            {
                var name = slot.Entry.Name; var exposure = ExposureName(slot.Entry.Config.Exposure);
                var stateName = StateName(slot);
                if (IsEnabled(slot) && stateName == "needs-auth") return $"{name}: needs sign-in, run /mcp login {name} ({exposure})";
                var tools = IsEnabled(slot) && stateName == "connected" ? $", {ToolCount(slot)} tools" : "";
                var state = !IsEnabled(slot) ? "disabled" : stateName == "disconnected" ? "disconnected, reconnects on next call" : stateName;
                var error = IsEnabled(slot) && slot.Error is { } failure && stateName != "connected" ? "\n    " + failure.Replace("\n", "\n    ", StringComparison.Ordinal) : "";
                return $"{name}: {state}{tools} ({exposure}){error}";
            }).ToList();
            lines.AddRange(ConfigErrors.Select(error => "config error: " + error));
            lines.AddRange(Overridden.Select(line => "overridden: " + line));
            return string.Join("\n", lines);
        }
    }

    /// <summary>getArgumentCompletions of `/mcp`.</summary>
    public IReadOnlyList<McpMenuItem>? GetArgumentCompletions(string prefix)
    {
        var parts = prefix.TrimStart().Split((char[]?)null, StringSplitOptions.None);
        if (parts.Length > 2) return null;
        var action = parts[0];
        if (parts.Length == 1)
            return [.. new[] { "login", "logout", "reconnect" }.Where(item => item.StartsWith(action, StringComparison.Ordinal)).Select(item => new McpMenuItem(item + " ", item))];
        if (action is not ("login" or "logout" or "reconnect")) return null;
        lock (gate)
        {
            var items = servers.Where(slot => action == "reconnect" ? IsEnabled(slot) && slot.Server is not null : UsesOAuth(slot))
                .Where(slot => slot.Entry.Name.StartsWith(parts[1], StringComparison.Ordinal))
                .Select(slot => new McpMenuItem($"{action} {slot.Entry.Name}", slot.Entry.Name, DescribeState(slot))).ToList();
            return items.Count > 0 ? items : null;
        }
    }

    /// <summary>The `/mcp` command handler: without arguments the manager view in the terminal, else the status; `login`, `logout`
    /// and `reconnect` act on one server, asking when the name is omitted and ambiguous.</summary>
    public async Task ExecuteCommandAsync(string arguments, IMcpCommandUi ui, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(ui);
        var parts = arguments.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            if (ui.IsTui) await ui.ShowManagerAsync(manager => ManageAsync(manager, token)).ConfigureAwait(false);
            else { await PendingAsync(token).ConfigureAwait(false); ui.Notify(FormatStatus(), "info"); }
            return;
        }
        if (parts.Length > 2) { ui.Notify(Usage, "warning"); return; }
        var name = parts.Length > 1 ? parts[1] : null;
        await PendingAsync(token).ConfigureAwait(false);
        const string NoOAuth = "No enabled MCP server uses OAuth. Only HTTP servers without an Authorization header do.";
        switch (parts[0])
        {
            case "login":
                if (await PickAsync(name, ui, UsesOAuth, slot => slot.State == ConnectionState.NeedsAuth, NoOAuth, token).ConfigureAwait(false) is { } login)
                    await LoginAsync(login, ui).ConfigureAwait(false);
                return;
            case "logout":
                if (await PickAsync(name, ui, UsesOAuth, slot => slot.State == ConnectionState.NeedsAuth, NoOAuth, token).ConfigureAwait(false) is not { } logout) return;
                var removed = await SignOutAsync(logout.Entry.Name, token).ConfigureAwait(false);
                ui.Notify(removed ? $"Signed out of MCP server \"{logout.Entry.Name}\"." : $"No stored credentials for MCP server \"{logout.Entry.Name}\".", "info");
                return;
            case "reconnect":
                if (await PickAsync(name, ui, slot => IsEnabled(slot) && slot.Server is not null,
                    slot => StateName(slot) is "failed" or "disconnected", "No enabled MCP server to reconnect.", token).ConfigureAwait(false) is not { } server) return;
                await ReconnectCoreAsync(server, token).ConfigureAwait(false);
                string? failure; lock (gate) failure = ReconnectFailure(server);
                if (failure is not null) ui.Notify(failure, "error");
                else { string state; lock (gate) state = DescribeState(server); ui.Notify($"Reconnected to MCP server \"{server.Entry.Name}\" ({state}).", "info"); }
                return;
            default: ui.Notify(Usage, "warning"); return;
        }
    }

    private Task PendingAsync(CancellationToken token) { Task current; lock (gate) current = pending; return current.WaitAsync(token); }

    /// <summary>pickServer: the named server, or the only (or only preferred) eligible one, else a choice.</summary>
    private async Task<Slot?> PickAsync(string? name, IMcpCommandUi ui, Func<Slot, bool> eligible, Func<Slot, bool> preferred, string none, CancellationToken token)
    {
        Slot[] candidates;
        lock (gate)
        {
            if (name is not null)
            {
                var named = Find(name);
                if (named is null) { ui.Notify($"No MCP server named \"{name}\".", "error"); return null; }
                if (!eligible(named)) { ui.Notify(none, "error"); return null; }
                return named;
            }
            candidates = [.. servers.Where(eligible)];
        }
        if (candidates.Length == 0) { ui.Notify(none, "info"); return null; }
        if (candidates.Length == 1) return candidates[0];
        var preferredOnes = candidates.Where(preferred).ToArray();
        if (preferredOnes.Length == 1) return preferredOnes[0];
        var choice = await ui.SelectAsync("MCP server", [.. candidates.Select(slot => slot.Entry.Name)], token).ConfigureAwait(false);
        return candidates.FirstOrDefault(slot => slot.Entry.Name == choice);
    }

    /// <summary>loginCommand: sign in with the manager view in the terminal, else with notifications and an input for a pasted URL.</summary>
    private async Task LoginAsync(Slot slot, IMcpCommandUi ui)
    {
        var name = slot.Entry.Name;
        if (!ui.HasUi) { ui.Notify($"Signing in to MCP server \"{name}\" requires interactive mode.", "error"); return; }
        string? failure = null;
        if (ui.IsTui) await ui.ShowManagerAsync(async manager => failure = await SignInWithUiAsync(manager, name).ConfigureAwait(false)).ConfigureAwait(false);
        else
            failure = await SignInAsync(name, new(url =>
            {
                ui.Notify($"Sign in to MCP server \"{name}\" in your browser:\n{url.AbsoluteUri}", "info");
                dependencies.OpenUrl(url.AbsoluteUri);
            }, signal => ui.InputAsync($"Waiting for sign-in to \"{name}\". If the browser cannot reach this machine, paste the URL it was redirected to.",
                "http://127.0.0.1:.../callback?code=...", signal))).ConfigureAwait(false);
        if (Session() is null) return;
        if (failure is not null) { ui.Notify(failure, failure == "Sign-in cancelled." ? "info" : "error"); return; }
        int tools; lock (gate) tools = ToolCount(slot);
        ui.Notify($"Signed in to MCP server \"{name}\" ({tools} tools).", "info");
    }
}

/// <summary>oauth.ts McpSignInPrompt for the manager: show the authorization URL, and ask for a pasted redirect URL (cancelled once
/// the browser reached the callback; null or blank cancels the sign-in).</summary>
public sealed record McpSignInPromptAdapter(Action<Uri> ShowAuthorizationUrl, Func<CancellationToken, Task<string?>> PromptForRedirectUrl);
