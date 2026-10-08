// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/mcp/index.ts (session start: load the
// config, connect enabled servers, servers with direct tools before the first prompt and the others in the background,
// reportProblems, describeState), packages/coding-agent/src/extensions/mcp/runtime.ts (createDefaultTransport, usesOAuth,
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
/// The MCP servers of a production session, from the global <c>&lt;agent dir&gt;/mcp.json</c> read once at session start.
/// Servers with direct tools connect before the session opens; a server that fails is reported and left out, so the session
/// still starts (the original reports it and continues). The other servers connect in the background. Stdio servers inherit
/// the process environment; HTTP servers that use OAuth read and refresh their tokens in the durable <c>mcp-auth.json</c> store.
/// The project <c>.pi/mcp.json</c> is not read: PiSharp has no project-trust store. Servers with `deferred` tools connect in the
/// background and the built-in <c>tool_search</c> (<see cref="McpToolSearch"/>) loads their tools. Servers whose tools are
/// reached through codemode need the codemode tool PiSharp does not implement yet; without <see cref="Discovery"/> they are
/// reported and not connected.
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
    /// <summary>Executable codemode/tool_search definitions for one generation (tests); null: PiSharp has no codemode, so servers that
    /// need it are skipped. The built-in tool_search is added when a server has `deferred` tools and none is supplied here.</summary>
    public Func<long, ImmutableArray<McpDiscoveryExecutableDefinition>>? Discovery { get; init; }
    public Func<double> UnixMilliseconds { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    /// <summary>Each background connection once it connected (its tools published) or failed.</summary>
    public Action<McpBackgroundConnectionReport>? ObserveBackgroundConnection { get; init; }

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

    /// <summary>Reads the global mcp.json and returns the profile admission, or null when no server is enabled. Configuration
    /// errors, the ignored project file and skipped servers are written to <paramref name="diagnostics"/>.</summary>
    internal McpProfileRuntimeAdmission? CreateAdmission(string cwd, TextWriter diagnostics)
    {
        ArgumentException.ThrowIfNullOrEmpty(cwd); ArgumentNullException.ThrowIfNull(diagnostics);
        var reporter = new Reporter(diagnostics);
        var globalConfig = Path.Combine(AgentDirectory, "mcp.json");
        var projectConfig = Path.Combine(cwd, ".pi", "mcp.json");
        if (File.Exists(projectConfig)) reporter.Notice($"{projectConfig} is ignored because PiSharp does not read project trust.");
        if (!File.Exists(globalConfig)) return null;
        McpLoadedConfiguration loaded;
        try { loaded = McpConfigurationReader.Load(new(globalConfig, File.ReadAllText(globalConfig)), null, false); }
        catch (IOException error) { reporter.Notice($"MCP failed to load: Could not read {globalConfig}: {error.Message}"); return null; }
        var problems = loaded.Errors.Select(error => "config: " + error).ToList();
        var environment = InheritedEnvironment();
        var admitted = ImmutableArray.CreateBuilder<McpServerEntry>();
        foreach (var entry in loaded.Servers.Where(entry => entry.Config.Enabled))
        {
            if (Discovery is null && McpConfigurationReader.ConfiguredExposures(entry.Config).Contains(McpExposure.Codemode))
            { problems.Add($"{entry.Name}: not connected: its codemode tools need the codemode tool, which PiSharp does not implement yet; set \"exposure\": \"deferred\" or \"direct\" to use it"); continue; }
            if (entry.Config.AuthProvider is not null)
            { problems.Add($"{entry.Name}: not connected: auth.provider is not supported by PiSharp"); continue; }
            admitted.Add(entry);
        }
        if (admitted.Count == 0) { reporter.Problems(problems); return null; }
        var catalog = new McpServerCatalog(admitted.ToImmutable(), []);
        var autoEnableCodemode = loaded.EffectiveAutoEnableCodemode;
        return async (currentCwd, generation, nativeRegistry, exactPolicy, token) =>
        {
            var generationProblems = generation == 1 ? new List<string>(problems) : [];
            var owned = new OwnedResources(CreateClient); IAsyncDisposable discovery = new Disposer(() => ValueTask.CompletedTask);
            // Pi trusts the servers of mcp.json: the profile's final-action policy admits the tools of this generation's servers.
            var grants = new McpCallGrants();
            try
            {
                var options = new McpRuntimeOptions(generation, ClientVersion, Roots(currentCwd));
                McpAdmittedChannelFactory Channels(McpServerEntry entry) => Channel(entry, currentCwd, options, () => owned.Client, environment);
                // Servers with direct tools: connected here, in catalog order, so a failure leaves only that server out.
                var preOpen = new List<(McpServerEntry Entry, McpPreOpenServerCapture Capture)>();
                var current = nativeRegistry;
                foreach (var entry in catalog.Servers.Where(entry => McpConfigurationReader.HasDirectTools(entry.Config)))
                {
                    token.ThrowIfCancellationRequested();
                    var registry = owned.Track(new ExtensionRegistry());
                    try
                    {
                        var scope = await registry.ActivateAsync("mcp-" + entry.Name, new EmptyExtension(), token).ConfigureAwait(false);
                        var capture = await McpPreOpenServerCapture.AcquireAsync(entry, registry, scope, current, exactPolicy, ValidateArguments,
                            Channels(entry), options, ComposeHooks, token).ConfigureAwait(false);
                        preOpen.Add((entry, owned.Hold(capture, current))); current = capture.Registry;
                        grants.AdmitServer(scope);
                    }
                    catch (Exception error) when (!token.IsCancellationRequested) { generationProblems.Add($"{entry.Name}: {Describe(entry, error)}"); }
                }
                var background = catalog.Servers.Where(entry => !McpConfigurationReader.HasDirectTools(entry.Config))
                    .Select(entry => new McpBackgroundServerAdmission(entry.Name, actual => Same(actual, entry), async (actual, owner, attachment, cancellation) =>
                    {
                        var registry = owned.Track(new ExtensionRegistry());
                        var scope = await registry.ActivateAsync("mcp-" + actual.Name, new EmptyExtension(), cancellation).ConfigureAwait(false);
                        grants.AdmitServer(scope);
                        return new McpPreparedServer(actual, registry, scope, owner, exactPolicy, ValidateArguments, Channels(actual),
                            new McpRuntimeOptions(attachment.Generation, ClientVersion, Roots(currentCwd)), ComposeHooks);
                    })).ToImmutableArray();
                McpDiscoveryCatalogPreparation prepare = (_, registry) => new(registry, []);
                var definitions = Discovery?.Invoke(generation) is { IsDefault: false } supplied ? supplied : [];
                // The original registers tool_search with every session and the MCP extension activates it for `deferred` servers;
                // here it is registered (active) for them. A tool selection that leaves tool_search out leaves their tools unreachable.
                if (catalog.Servers.Any(entry => McpConfigurationReader.ConfiguredExposures(entry.Config).Contains(McpExposure.Deferred)) &&
                    !definitions.Any(definition => definition.Kind == McpDiscoveryKind.ToolSearch))
                {
                    if (nativeRegistry.LifetimeToolSelection?.IsAllowed(McpToolSearch.Name) != false) definitions = definitions.Add(McpToolSearch.Create());
                    else if (generation == 1 && !definitions.Any(definition => definition.Kind == McpDiscoveryKind.Codemode))
                        reporter.Notice("MCP tools are only reachable from the codemode or tool_search tool, but neither is active; they cannot be called.");
                }
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
                }
                reporter.Problems(generationProblems);
                var included = preOpen.Select(row => row.Entry).Concat(background.Select(row => catalog.Servers.Single(entry => entry.Name == row.Name)))
                    .ToHashSet();
                return new McpSessionRuntimeAdmission(nativeRegistry, owned, discovery, exactPolicy,
                    new McpServerCatalog([.. catalog.Servers.Where(included.Contains)], []),
                    [.. preOpen.Select(row => new McpServerActivationAdmission(row.Entry.Name, actual => Same(actual, row.Entry),
                        (actual, registry, _) => Task.FromResult(owned.Release(row.Capture, registry))))],
                    autoEnableCodemode, prepare)
                {
                    BackgroundServers = background, CallGrants = grants,
                    ServersPromptSource = new McpServersPromptSource(),
                    ReportBackgroundConnection = report =>
                    {
                        if (report.Failure is { } failure) reporter.Problems([$"{report.Entry.Name}: {Describe(report.Entry, failure)}"]);
                        ObserveBackgroundConnection?.Invoke(report);
                    }
                };
            }
            catch
            {
                await discovery.DisposeAsync().ConfigureAwait(false); await owned.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        };
    }

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
                new McpOAuthCancellationAdmission(), settings.ClientId is { Length: > 0 } id ? new McpOAuthAdmittedClient(id, settings.ClientSecret) : null);
            authentication = adapter.CreateAuthentication();
        }
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
