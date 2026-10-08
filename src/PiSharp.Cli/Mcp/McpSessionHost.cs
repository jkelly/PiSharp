// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts (session start: load the
// config, connect every enabled server in the background, the first prompt waiting for servers with direct tools,
// ensureDiscoveryActive, reportProblems, describeState), packages/coding-agent/src/extensions/tool-search/index.ts (tool_search
// registered inactive), packages/coding-agent/src/extensions/mcp/runtime.ts (createDefaultTransport, usesOAuth, auth.provider,
// expandHome, roots), packages/coding-agent/src/extensions/mcp/config.ts (loadMcpConfig), packages/mcp/src/transports/stdio.ts
// (inherited environment) and cross-spawn 7 (Windows command resolution and cmd.exe escaping).
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using PiSharp.Agent;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Mcp.Authentication;
using PiSharp.Cli.Mcp.Transport;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Agent;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Mcp.Authentication;
using PiSharp.Extensions.Runtime.Mcp.Transport;
using PiSharp.Tools.Processes.Mcp;

namespace PiSharp.Cli.Mcp;

/// <summary>
/// The MCP servers of a production session, from the global <c>&lt;agent dir&gt;/mcp.json</c> and, when the project is trusted
/// (<see cref="IsProjectTrusted"/>), the project <c>.pi/mcp.json</c>, read once at session start. Every enabled server connects in
/// the background after the session opens; the first prompt waits up to <see cref="StartupWait"/> for servers with `direct`
/// tools, and a server that fails is reported and left out, so the session still starts. Stdio servers inherit the process
/// environment; HTTP servers that use OAuth read and refresh their tokens in the durable <c>mcp-auth.json</c> store, and servers
/// with `auth.provider` send the provider's current login token. The built-in <c>codemode</c> tool (<see cref="McpCodemode"/>, a
/// Jint sandbox) and <c>tool_search</c> (<see cref="McpToolSearch"/>) are registered with every session the tool selection keeps
/// them in, inactive unless servers need them (codemode for `codemode` tools unless autoEnableCodemode is false, tool_search for
/// `deferred` tools) or the selection names them. Servers with resources are reached through the resource tools
/// (<see cref="McpResourceToolsPublisher"/>), and each generation's servers are managed through an <see cref="McpServerManager"/>.
/// </summary>
internal sealed record McpSessionHost(string AgentDirectory, string HomeDirectory, Func<IEnumerable<KeyValuePair<string, string>>> ProcessEnvironment)
{
    internal const string ClientVersion = "1.1.0";
    private static readonly TimeSpan OAuthRequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Physical HTTP for HTTP servers and their OAuth refreshes; defaults to a socket handler without redirects.</summary>
    public Func<HttpMessageHandler>? CreateHttpHandler { get; init; }
    /// <summary>The durable OAuth credential document; defaults to <c>mcp-auth.json</c> in the agent directory.</summary>
    public IMcpOAuthCredentialBackend? Credentials { get; init; }
    /// <summary>A replacement channel for a server (tests); null keeps the stdio or HTTP channel of its config.</summary>
    public Func<McpServerEntry, McpAdmittedChannelFactory?>? CreateChannel { get; init; }
    /// <summary>Executable codemode/tool_search definitions for one generation (tests). The built-in codemode and tool_search are
    /// added unless supplied here or left out by the tool selection.</summary>
    public Func<long, ImmutableArray<McpDiscoveryExecutableDefinition>>? Discovery { get; init; }
    public Func<double> UnixMilliseconds { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>Each background connection once it connected (its tools published) or failed.</summary>
    public Action<McpBackgroundConnectionReport>? ObserveBackgroundConnection { get; init; }
    /// <summary>The model registry codemode scripts reach as <c>models</c>; defaults to the CLI registry (embedded catalogs, the
    /// environment and auth.json), built on first use.</summary>
    public Func<PiSharp.Codemode.ICodemodeModelRuntime?>? CodemodeModels { get; init; }
    /// <summary>Whether the session's project is trusted, so its <c>.pi/mcp.json</c> is read (ctx.isProjectTrusted()). The project
    /// trust store and its startup flow supply it; the default is the original's non-interactive answer without a stored decision
    /// (defaultProjectTrust "ask" without a UI): not trusted.</summary>
    public Func<string, bool> IsProjectTrusted { get; init; } = _ => false;
    /// <summary>The current token of a provider for servers with `auth.provider` (modelRegistry.getApiKeyForProvider); defaults to
    /// the CLI's credential resolution (auth.json, then the environment).</summary>
    public Func<string, CancellationToken, ValueTask<string?>>? ProviderToken { get; init; }
    /// <summary>How long the first prompt waits for servers with `direct` tools (the original's startupWaitMs).</summary>
    public TimeSpan StartupWait { get; init; } = McpBackgroundConnections.DefaultStartupWait;
    /// <summary>Opens an OAuth authorization URL for a sign-in from `/mcp`; defaults to the platform browser.</summary>
    public Action<string>? OpenUrl { get; init; }
    /// <summary>Each generation's server manager once its session is bound: the seam for the terminal's `/mcp` view and the slash
    /// command (<see cref="McpServerManager.ManageAsync"/>, <see cref="McpServerManager.ExecuteCommandAsync"/>).</summary>
    public Action<McpServerManager>? ObserveManager { get; init; }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<McpProfileRuntimeAdmission, TaskCompletionSource> hostStarts = new();

    /// <summary>The host started dispatching on the session the admission opened: its background servers may connect now.</summary>
    internal void HostStarted(McpProfileRuntimeAdmission admission)
    { if (hostStarts.TryGetValue(admission, out var started)) started.TrySetResult(); }

    /// <summary>The agent directory from PI_CODING_AGENT_DIR or <c>~/.pi/agent</c>, and the real process environment.</summary>
    internal static McpSessionHost CreateDefault()
    {
        var platform = OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agent = TerminalKeybindingConfigurationLoader.ResolveAgentDirectory(home, platform,
            new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") });
        return new(agent, home, () => Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Select(entry => KeyValuePair.Create((string)entry.Key, (string?)entry.Value ?? "")));
    }

    /// <summary>Reads the global mcp.json and, in a trusted project, the project's (unless <paramref name="noMcp"/>) and returns the
    /// profile admission. Every session gets the built-in codemode and tool_search tools, inactive unless MCP servers need them or
    /// the tool selection names them, as the original registers them with every session. Configuration errors and failed servers
    /// are written to <paramref name="diagnostics"/> once the servers settled.</summary>
    internal McpProfileRuntimeAdmission? CreateAdmission(string cwd, TextWriter diagnostics, JsonData? settings = null, bool noMcp = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(cwd); ArgumentNullException.ThrowIfNull(diagnostics);
        var reporter = new Reporter(diagnostics);
        var globalConfig = Path.Combine(AgentDirectory, "mcp.json");
        var projectConfig = Path.Combine(cwd, ".pi", "mcp.json");
        var problems = new List<string>();
        var configErrors = ImmutableArray<string>.Empty;
        var configured = ImmutableArray<McpServerEntry>.Empty;
        string? trustedProject = null;
        bool? configuredAutoEnable = null;
        if (!noMcp)
        {
            McpConfigurationDocument? Read(string path)
            {
                try { return File.Exists(path) ? new(path, File.ReadAllText(path)) : null; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { reporter.Notice($"MCP failed to load: Could not read {path}: {error.Message}"); return null; }
            }
            // config.ts loadMcpConfig: the global file, then the project file when the project is trusted. Project entries replace
            // global ones; an entry without command, url or type overrides only enabled, exposure and toolExposure.
            var trusted = IsProjectTrusted(cwd);
            var loaded = McpConfigurationReader.Load(Read(globalConfig), trusted ? Read(projectConfig) : null, trusted);
            configErrors = loaded.Errors;
            problems.AddRange(loaded.Errors.Select(error => "config: " + error));
            configuredAutoEnable = loaded.AutoEnableCodemode;
            configured = loaded.Servers;
            if (trusted) trustedProject = projectConfig;
            if (!configured.Any(entry => entry.Config.Enabled)) { reporter.Problems(problems); problems.Clear(); }
        }
        var admitted = configured.Where(entry => entry.Config.Enabled).ToImmutableArray();
        var environment = InheritedEnvironment();
        var autoEnableCodemode = configuredAutoEnable ?? true;
        var (codemodeMode, inlineBudget) = PiSharp.Codemode.CodemodeToolDefinition.ReadSettings(settings?.Value);
        var codemodeModels = CodemodeModels ?? (() => McpCodemode.ModelRuntime.CreateDefault());
        var credentials = new McpOAuthCredentialStore(Credentials ?? McpOAuthFileCredentialBackend.InAgentDirectory(AgentDirectory));
        var hostStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        McpProfileRuntimeAdmission admission = async (currentCwd, generation, nativeRegistry, exactPolicy, token) =>
        {
            var generationProblems = generation == 1 ? new List<string>(problems) : [];
            var failures = new List<string>();
            var owned = new OwnedResources(CreateClient); IAsyncDisposable discovery = new Disposer(() => ValueTask.CompletedTask);
            // Pi trusts the servers of mcp.json: the profile's final-action policy admits the tools of this generation's servers.
            var grants = new McpCallGrants();
            try
            {
                var options = new McpRuntimeOptions(generation, ClientVersion, Roots(currentCwd));
                McpAdmittedChannelFactory Channels(McpServerEntry entry) => Channel(entry, currentCwd, options, () => owned.Client, environment);
                var resourceRegistry = owned.Track(new ExtensionRegistry());
                var resourceScope = await resourceRegistry.ActivateAsync("mcp-resources", new EmptyExtension(), token).ConfigureAwait(false);
                var resources = new McpResourceToolsPublisher(resourceRegistry, resourceScope, exactPolicy, ValidateArguments, ComposeHooks, grants);
                McpServerManager? manager = null;
                async ValueTask<McpPreparedServer> Bind(McpServerEntry actual, ReplaceableAgentSession owner, AgentSessionAttachment attachment, CancellationToken cancellation)
                {
                    var registry = owned.Track(new ExtensionRegistry());
                    var scope = await registry.ActivateAsync("mcp-" + actual.Name, new EmptyExtension(), cancellation).ConfigureAwait(false);
                    grants.AdmitServer(scope);
                    // The original records nothing at session_shutdown, so a resumed session declares the tools again once the server connects.
                    return new McpPreparedServer(actual, registry, scope, owner, exactPolicy, ValidateArguments, Channels(actual),
                        new McpRuntimeOptions(attachment.Generation, ClientVersion, Roots(currentCwd)), ComposeHooks) { DurableWithdrawalOnShutdown = false };
                }
                var background = admitted.Select(entry => new McpBackgroundServerAdmission(entry.Name, actual => Same(actual, entry), async (actual, owner, attachment, cancellation) =>
                {
                    var server = await Bind(actual, owner, attachment, cancellation).ConfigureAwait(false);
                    manager?.Track(actual, server);
                    return server;
                })).ToImmutableArray();
                McpDiscoveryCatalogPreparation prepare = (_, registry) => new(registry, []);
                var definitions = Discovery?.Invoke(generation) is { IsDefault: false } supplied ? supplied : [];
                var exposures = admitted.SelectMany(entry => McpConfigurationReader.ConfiguredExposures(entry.Config)).ToHashSet();
                var selection = nativeRegistry.LifetimeToolSelection;
                bool Named(string name) => selection?.IsNamed(name) == true || selection?.InitialNames.Contains(name) == true;
                // The original registers codemode (inactive) with every session; the MCP extension activates it for `codemode`
                // servers unless autoEnableCodemode is false. --tools, --exclude-tools and defaultTools select it like any tool.
                var codemodeActivated = exposures.Contains(McpExposure.Codemode) && autoEnableCodemode;
                bool codemodeReachable;
                if (!definitions.Any(definition => definition.Kind == McpDiscoveryKind.Codemode) && selection?.IsAllowed(McpCodemode.Name) != false)
                {
                    definitions = definitions.Add(McpCodemode.Create(codemodeMode, inlineBudget, codemodeModels, codemodeActivated));
                    codemodeReachable = codemodeActivated || Named(McpCodemode.Name);
                }
                else codemodeReachable = definitions.Any(definition => definition.Kind == McpDiscoveryKind.Codemode);
                var codemodeOff = exposures.Contains(McpExposure.Codemode) && !autoEnableCodemode && selection?.IsAllowed(McpCodemode.Name) != false;
                // tool-search/index.ts registers tool_search inactive with every session; ensureDiscoveryActive activates it for
                // `deferred` servers. A tool selection that leaves it out leaves their tools unreachable.
                var toolSearchActivated = exposures.Contains(McpExposure.Deferred);
                bool toolSearchReachable;
                if (!definitions.Any(definition => definition.Kind == McpDiscoveryKind.ToolSearch) && selection?.IsAllowed(McpToolSearch.Name) != false)
                {
                    definitions = definitions.Add(McpToolSearch.Create(toolSearchActivated));
                    toolSearchReachable = toolSearchActivated || Named(McpToolSearch.Name);
                }
                else toolSearchReachable = definitions.Any(definition => definition.Kind == McpDiscoveryKind.ToolSearch);
                // ensureDiscoveryActive: tools that are not declared need codemode or tool_search; warn once when neither is active.
                if (generation == 1 && (exposures.Contains(McpExposure.Codemode) || exposures.Contains(McpExposure.Deferred)) && !codemodeReachable && !toolSearchReachable)
                    reporter.Notice($"MCP tools are only reachable from the codemode or tool_search tool, but neither is active{(codemodeOff ? " (autoEnableCodemode is false)" : "")}; they cannot be called.");
                if (!definitions.IsEmpty)
                {
                    var discoveryRegistry = new ExtensionRegistry();
                    discovery = new Disposer(() => discoveryRegistry.DisposeAsync());
                    var scope = await discoveryRegistry.ActivateAsync("mcp-discovery", new EmptyExtension(), token).ConfigureAwait(false);
                    prepare = new McpRegisteredProfileDiscoveryAdmission(discoveryRegistry, scope, definitions, exactPolicy, generation,
                        ValidateArguments, ComposeHooks).Prepare;
                    // The built-in tool_search is host code that only changes the session's tool selection: the profile admits its exact action.
                    if (definitions.Any(definition => definition.Kind == McpDiscoveryKind.ToolSearch && definition.Descriptor.RegistrationId == McpToolSearch.RegistrationId))
                        grants.AdmitExact(McpToolSearch.Name, $"{scope.OwnerId}/{scope.OwnerGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{McpToolSearch.RegistrationId}");
                    // The built-in codemode runs scripts whose nested calls each pass the session's own final-action policy.
                    if (definitions.Any(definition => definition.Kind == McpDiscoveryKind.Codemode && definition.Descriptor.RegistrationId == McpCodemode.RegistrationId))
                        grants.AdmitExact(McpCodemode.Name, $"{scope.OwnerId}/{scope.OwnerGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{McpCodemode.RegistrationId}");
                }
                manager = new McpServerManager(configured, configErrors, trustedProject, new(Bind, resources, credentials, CreateClient,
                    Resolve, OpenUrl ?? PiSharp.Cli.Commands.McpCommand.OpenBrowser, AgentDirectory));
                return new McpSessionRuntimeAdmission(nativeRegistry, owned, discovery, exactPolicy, new McpServerCatalog(admitted, []), [],
                    autoEnableCodemode, prepare)
                {
                    BackgroundServers = background, CallGrants = grants,
                    // The first generation's background servers wait until the host started dispatching; later generations (reload)
                    // open on a running host.
                    ConnectAfter = generation == 1 ? hostStarted.Task : null,
                    ServersPromptSource = new McpServersPromptSource(),
                    StartupWait = StartupWait, Notify = reporter.Notice,
                    ReportBackgroundConnection = report =>
                    {
                        if (report.Failure is { } failure) lock (failures) failures.Add($"{report.Entry.Name}: {Describe(report.Entry, failure)}");
                        manager.Settled(report);
                        ObserveBackgroundConnection?.Invoke(report);
                    },
                    // reportProblems: one message for everything that needs the user, once every server settled.
                    BackgroundSettled = () => { string[] lines; lock (failures) lines = [.. generationProblems, .. failures]; reporter.Problems(lines); },
                    BackgroundConnected = async (entry, server, snapshot, cancellation) =>
                    {
                        try { await resources.UpdateAsync(entry.Name, server, entry.Config.Exposure, snapshot.Catalog.HasResources, cancellation).ConfigureAwait(false); }
                        catch (Exception) when (!cancellation.IsCancellationRequested) { /* The resource tools follow on the next change. */ }
                    },
                    BindManager = (owner, attachment, connections) =>
                    {
                        resources.Bind(owner, attachment);
                        manager.Bind(owner, attachment, connections);
                        // index.ts turn_start: servers waiting for a sign-in reconnect once it happened outside the session.
                        var gate = attachment.Session.BeforeInputAdmission;
                        attachment.Session.BeforeInputAdmission = async cancellation =>
                        {
                            await manager.ReconnectSignedInAsync(cancellation).ConfigureAwait(false);
                            if (gate is not null) await gate(cancellation).ConfigureAwait(false);
                        };
                        try { ObserveManager?.Invoke(manager); }
                        catch (Exception) { /* An observer must not affect the session. */ }
                    }
                };
            }
            catch
            {
                await discovery.DisposeAsync().ConfigureAwait(false); await owned.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        };
        hostStarts.Add(admission, hostStarted);
        return admission;
    }

    /// <summary>The provider token for `auth.provider` servers (<see cref="ProviderToken"/>, else the CLI credentials).</summary>
    private ValueTask<string?> ProviderTokenAsync(string provider, CancellationToken token) =>
        ProviderToken?.Invoke(provider, token) ?? McpProviderTokens.ResolveAsync(PiSharp.Cli.Commands.LiveSessionRuntime.Default, provider, token);

    private Dictionary<string, string> InheritedEnvironment()
    {
        var values = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (key, value) in ProcessEnvironment())
            if (!string.IsNullOrEmpty(key) && !key.Contains('=') && !key.Contains('\0') && value is not null && !value.Contains('\0')) values[key] = value;
        return values;
    }

    private static JsonData Roots(string cwd) => JsonData.Parse(JsonSerializer.Serialize(new[]
        { new { uri = new Uri(Path.GetFullPath(cwd)).AbsoluteUri, name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd))) } }));

    private static void Same(McpServerEntry actual, McpServerEntry admitted)
    { if (!ReferenceEquals(actual, admitted)) throw new InvalidOperationException("MCP admission belongs to the exact configured server entry."); }

    /// <summary>The MCP server validates its own arguments; the host admits any JSON object. tool_search admits its schema.</summary>
    private static ValueTask<bool> ValidateArguments(ExtensionToolRegistrationInfo tool, JsonData arguments, CancellationToken token) =>
        ValueTask.FromResult(arguments.Value.ValueKind == JsonValueKind.Object &&
            (tool.Name != McpToolSearch.Name || McpToolSearch.ValidArguments(arguments)));

    private static IPreparedToolHooks? ComposeHooks(SessionRuntimeRegistry current, ExtensionAgentBinding binding) =>
        binding.PreparedHooks ?? current.PreparedToolHooks;

    /// <summary>describeState for the startup report: "needs sign-in" or "failed: &lt;first line&gt;".</summary>
    internal static string Describe(McpServerEntry entry, Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is McpOAuthAuthorizationRequiredException) return $"needs sign-in (run \"{PiSharp.Cli.Commands.McpCommand.AppName} mcp login {entry.Name}\")";
        var message = error is AggregateException { InnerExceptions.Count: 1 } single ? single.InnerExceptions[0].Message : error.Message;
        var first = message.Split('\n')[0].TrimEnd('\r');
        return "failed: " + (first.Length == 0 ? "unknown error" : first);
    }

    /// <summary>createDefaultTransport: the server's stdio or streamable HTTP channel (or the <see cref="CreateChannel"/> replacement).</summary>
    internal McpAdmittedChannelFactory Channel(McpServerEntry entry, string cwd, McpRuntimeOptions options, Func<HttpClient> client,
        Dictionary<string, string>? inherited = null) =>
        CreateChannel?.Invoke(entry) ?? (entry.Config.Transport == McpTransportKind.Http
            ? HttpChannel(entry, options, client()) : StdioChannel(entry, cwd, inherited ?? InheritedEnvironment()));

    /// <summary>A client for HTTP servers and their OAuth requests; an injected handler stays the caller's.</summary>
    internal HttpClient CreateClient() => new(CreateHttpHandler?.Invoke() ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: CreateHttpHandler is null)
    { Timeout = Timeout.InfiniteTimeSpan };

    private McpAdmittedChannelFactory HttpChannel(McpServerEntry entry, McpRuntimeOptions options, HttpClient client)
    {
        var raw = entry.Config.Raw.Value;
        var url = new Uri(raw.GetProperty("url").GetString()!);
        var headers = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw.TryGetProperty("headers", out var configured) && configured.ValueKind == JsonValueKind.Object)
            foreach (var header in configured.EnumerateObject())
                headers[header.Name] = Resolve(header.Value.GetString() ?? "", $"MCP server \"{entry.Name}\" header \"{header.Name}\"");
        var binding = new McpHttpBinding(url, headers.ToImmutable(), new(OpenGetStream: true, DeleteSessionOnClose: true, MaximumReconnects: 5));
        McpAdmittedHttpAuthentication? authentication = null;
        if (McpConfigurationReader.UsesOAuth(entry.Config))
        {
            var store = new McpOAuthCredentialStore(Credentials ?? McpOAuthFileCredentialBackend.InAgentDirectory(AgentDirectory)).ForServer(entry.Name, url);
            var settings = McpOAuthSettings.From(entry, Resolve);
            var adapter = new McpAdmittedOAuthRefreshAdapter(url, store, (request, token) => new(ExchangeAsync(client, request, token)), UnixMilliseconds,
                new McpOAuthCancellationAdmission(), settings.ClientId is { Length: > 0 } id ? new McpOAuthAdmittedClient(id, settings.ClientSecret) : null)
            { RefreshLock = McpOAuthRefreshLock.For(AgentDirectory, entry.Name, url) };
            authentication = adapter.CreateAuthentication();
        }
        // runtime.ts auth.provider: the provider's current token, read on every request so its refreshes apply.
        else if (entry.Config.AuthProvider is not null && entry.Scope != McpConfigurationScope.Project)
            authentication = McpProviderTokenAuthentication.Create(entry, ProviderTokenAsync);
        return AdmittedMcpHttpChannelFactory.Create(entry, binding, client, options, authentication: authentication);
    }

    /// <summary>OAuth metadata and refresh requests, each bounded by the original's 15 s timeout.</summary>
    private static async Task<McpOAuthExchangeResponse> ExchangeAsync(HttpClient client, McpOAuthExchangeRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(OAuthRequestTimeout);
        using var message = new HttpRequestMessage(request.Method, request.Endpoint);
        if (request.Body is not null) message.Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(request.Body));
        foreach (var (name, value) in request.Headers)
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) message.Content?.Headers.TryAddWithoutValidation(name, value);
            else message.Headers.TryAddWithoutValidation(name, value);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return new((int)response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException canceled) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        { throw new TimeoutException("OAuth request timed out after 15 s.", canceled); }
    }

    private McpAdmittedChannelFactory StdioChannel(McpServerEntry entry, string cwd, Dictionary<string, string> inherited)
    {
        var raw = entry.Config.Raw.Value;
        var environment = new Dictionary<string, string>(inherited, inherited.Comparer);
        if (raw.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
            foreach (var pair in env.EnumerateObject())
                environment[pair.Name] = Resolve(pair.Value.GetString() ?? "", $"MCP server \"{entry.Name}\" env \"{pair.Name}\"");
        var command = ExpandHome(raw.GetProperty("command").GetString()!);
        var arguments = raw.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array
            ? args.EnumerateArray().Select(arg => ExpandHome(arg.GetString() ?? "")).ToImmutableArray() : [];
        var directory = Path.GetFullPath(ExpandHome(raw.TryGetProperty("cwd", out var configured) ? configured.GetString() ?? "." : "."), cwd);
        var admission = WindowsCommand.Resolve(command, arguments, directory, environment);
        return (actual, token) =>
        {
            token.ThrowIfCancellationRequested();
            Same(actual, entry);
            return ValueTask.FromResult<IMcpAdmittedRequestChannel>(new McpJsonRpcRequestChannel(new McpStdioTransport(new McpNativeDuplexLease(admission))));
        };
    }

    /// <summary>`~` and `~/…` (also `~\…` on Windows) name the home directory.</summary>
    private string ExpandHome(string value) =>
        value == "~" ? HomeDirectory :
        value.StartsWith("~/", StringComparison.Ordinal) || OperatingSystem.IsWindows() && value.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(HomeDirectory, value[2..]) : value;

    /// <summary>resolveConfigValueOrThrow over the process environment, `!command` values included.</summary>
    private string Resolve(string value, string description)
    {
        var environment = InheritedEnvironment();
        return PiSharp.Cli.Commands.McpCommand.ResolveConfigValue(value, description, name => environment.GetValueOrDefault(name));
    }

    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Disposer(Func<ValueTask> close) : IAsyncDisposable
    {
        private int closed;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref closed, 1) == 0 ? close() : ValueTask.CompletedTask;
    }

    /// <summary>The generation's native resources: server registries, captures not handed to the activation and the HTTP client.</summary>
    private sealed class OwnedResources(Func<HttpClient> createClient) : IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly List<ExtensionRegistry> registries = [];
        private readonly List<McpPreOpenServerCapture> held = [];
        private HttpClient? client;
        private bool closed;
        public HttpClient Client
        {
            get
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(closed, this);
                    return client ??= createClient();
                }
            }
        }
        public ExtensionRegistry Track(ExtensionRegistry registry)
        {
            lock (gate) { if (!closed) { registries.Add(registry); return registry; } }
            _ = registry.DisposeAsync().AsTask();
            throw new ObjectDisposedException(nameof(OwnedResources));
        }
        private readonly Dictionary<McpPreOpenServerCapture, SessionRuntimeRegistry> bases = new(ReferenceEqualityComparer.Instance);
        public McpPreOpenServerCapture Hold(McpPreOpenServerCapture capture, SessionRuntimeRegistry acquiredOn)
        { lock (gate) { held.Add(capture); bases[capture] = acquiredOn; } return capture; }
        /// <summary>Hands a pre-connected capture to the activation, which owns it from then on. The activation chains the same
        /// registries in the same order, so the capture must arrive on the registry it was acquired on.</summary>
        public McpPreOpenServerCapture Release(McpPreOpenServerCapture capture, SessionRuntimeRegistry current)
        {
            lock (gate)
            {
                if (!bases.TryGetValue(capture, out var acquiredOn) || !ReferenceEquals(acquiredOn, current))
                    throw new InvalidOperationException("MCP capture was acquired on another registry.");
                if (!held.Remove(capture)) throw new InvalidOperationException("MCP capture was already handed out.");
            }
            return capture;
        }
        public async ValueTask DisposeAsync()
        {
            McpPreOpenServerCapture[] captures; ExtensionRegistry[] owned; HttpClient? physical;
            lock (gate) { if (closed) return; closed = true; captures = [.. held]; held.Clear(); owned = [.. registries]; physical = client; }
            var failures = new List<Exception>();
            foreach (var capture in captures) try { await capture.CloseAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            foreach (var registry in owned) try { await registry.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
            physical?.Dispose();
            if (failures.Count != 0) throw new AggregateException("MCP session resources failed to close.", failures);
        }
    }

    /// <summary>Startup report: the original's "MCP servers need attention" notice as one JSON diagnostic line.</summary>
    private sealed class Reporter(TextWriter output)
    {
        private readonly object gate = new();
        public void Notice(string message)
        {
            lock (gate)
            {
                try { output.WriteLine(JsonSerializer.Serialize(new { schemaVersion = 1, type = "mcp_diagnostic", message })); output.Flush(); }
                catch (Exception) { /* A diagnostic failure must not affect the session. */ }
            }
        }
        public void Problems(IReadOnlyCollection<string> lines)
        {
            if (lines.Count == 0) return;
            Notice("MCP servers need attention:\n" + string.Join("\n", lines.Select(line => "  " + line)));
        }
    }
}

/// <summary>cross-spawn on Windows: resolve the command like node-which (the cwd, then PATH, with PATHEXT), run `.exe`/`.com`
/// files directly and everything else (batch files and npm `.cmd` shims) through `cmd.exe /d /s /c "…"` with cmd escaping.</summary>
internal static partial class WindowsCommand
{
    [GeneratedRegex(@"([()\][%!^""`<>&|;, *?])")] private static partial Regex MetaCharacters();
    [GeneratedRegex(@"(\\*)""")] private static partial Regex QuotedBackslashes();
    [GeneratedRegex(@"(\\*)$")] private static partial Regex TrailingBackslashes();
    [GeneratedRegex(@"node_modules[\\/].bin[\\/][^\\/]+\.cmd$", RegexOptions.IgnoreCase)] private static partial Regex CmdShim();

    internal static McpProcessAdmission Resolve(string command, ImmutableArray<string> arguments, string cwd, IReadOnlyDictionary<string, string> environment)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environment) builder[key] = value;
        var env = builder.ToImmutable();
        var file = Which(command, cwd, env);
        if (file is not null && Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".com")
            return new(file, arguments, cwd, env);
        var shim = file is not null && CmdShim().IsMatch(file);
        var line = string.Join(' ', new[] { MetaCharacters().Replace(Path.TrimEndingDirectorySeparator(command.Replace('/', '\\')), "^$1") }
            .Concat(arguments.Select(argument => EscapeArgument(argument, shim))));
        var shell = env.GetValueOrDefault("ComSpec") is { Length: > 0 } comspec && Path.IsPathFullyQualified(comspec) ? comspec
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System), "cmd.exe");
        return new(shell, [], cwd, env) { VerbatimArguments = $"/d /s /c \"{line}\"" };
    }

    /// <summary>escape.argument: quote for the CRT, then caret-escape cmd metacharacters (twice for npm shims).</summary>
    internal static string EscapeArgument(string argument, bool doubleEscape)
    {
        var escaped = QuotedBackslashes().Replace(argument, "$1$1\\\"");
        escaped = "\"" + TrailingBackslashes().Replace(escaped, "$1$1") + "\"";
        escaped = MetaCharacters().Replace(escaped, "^$1");
        return doubleEscape ? MetaCharacters().Replace(escaped, "^$1") : escaped;
    }

    /// <summary>node-which on Windows: a command with a path separator resolves against the cwd only; otherwise the cwd and
    /// then each PATH entry are tried with each PATHEXT extension (and the bare name first when it has an extension).</summary>
    internal static string? Which(string command, string cwd, IReadOnlyDictionary<string, string> environment)
    {
        string? Variable(string name) => environment.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        var extensions = (Variable("PATHEXT") is { Length: > 0 } pathExt ? pathExt : ".EXE;.CMD;.BAT;.COM")
            .Split(';').ToList();
        if (command.Contains('.') && extensions[0].Length != 0) extensions.Insert(0, "");
        IEnumerable<string> directories = command.Contains('/') || command.Contains('\\') ? [""]
            : new[] { cwd }.Concat((Variable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim('"')));
        foreach (var directory in directories)
            foreach (var extension in extensions)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.Length == 0 ? cwd : Path.GetFullPath(directory, cwd), command + extension));
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}
