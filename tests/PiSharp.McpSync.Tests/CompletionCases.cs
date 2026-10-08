using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.AI;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Mcp.Authentication;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Extensions.Runtime.Mcp.Authentication;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

// Authored offline expectations for the remaining v1.1.0 MCP rows: background connection with late tool registration
// and section refresh (index.ts, docs/mcp.md), default client_name (oauth.ts createProvider, config.ts APP_NAME), the
// durable mcp-auth.json backend (core/auth-storage.ts FileAuthStorageBackend) and `mcp login`/`logout` (cli.ts). Fake
// authorization servers answer in process, the browser is simulated against the loopback callback, and every store
// lives in a fresh temporary directory. Nothing is captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string Id, Func<Task> Run)> CompletionCases() =>
    [
        ("exposure.background-connection-late-tools-section-and-failures", BackgroundConnection),
        ("exposure.background-partition-moves-admitted-servers-including-direct", BackgroundPartition),
        ("oauth.default-client-name-from-config-or-app-name", DefaultClientName),
        ("oauth.mcp-auth-json-location-lock-modes-and-durability", FileBackend),
        ("cli.mcp-help-usage-and-errors", McpCliErrors),
        ("cli.mcp-logout-removes-stored-credentials", McpCliLogout),
        ("cli.mcp-login-browser-callback-end-to-end", McpCliLogin),
        ("cli.mcp-login-refreshes-without-the-browser", McpCliRefresh),
        ("cli.mcp-login-pasted-redirect-url", McpCliPastedRedirect),
        ("cli.mcp-login-timeout-covers-the-whole-sign-in", McpCliTimeout),
        .. CommandCases()
    ];

    private static string FreshDirectory(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "pisharp-mcp-sync", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static void Delete(string path) { try { Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    // ---------- background connection ----------

    private static readonly ModelDescriptor SyncModel = new("mcp-sync", "openai-responses", "fixture");
    private sealed class AllowPolicy : IToolActionPolicy
    { public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) => ValueTask.FromResult(new ToolActionAuthorization(true)); }
    private sealed class NoTransport : IChatTransport
    {
        public async IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken token = default)
        { await Task.FromException(new IOException("No provider execution admitted.")); yield break; }
    }
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Disposer(Func<ValueTask> close) : IAsyncDisposable { public ValueTask DisposeAsync() => close(); }
    private sealed class FakeChannel(string? instructions, Exception? failure = null) : IMcpAdmittedRequestChannel
    {
        internal int Closes;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize")
                return failure is not null ? ValueTask.FromException<JsonData>(failure) : ValueTask.FromResult(JsonData.Parse(
                    "{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"fixture\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}" +
                    (instructions is null ? "" : ",\"instructions\":" + JsonSerializer.Serialize(instructions)) + "}"));
            if (method == "tools/list")
                return ValueTask.FromResult(JsonData.Parse("{\"tools\":[{\"name\":\"search\",\"description\":\"Search the docs.\",\"inputSchema\":{\"type\":\"object\"}}]}"));
            return ValueTask.FromException<JsonData>(new IOException("No tool execution admitted."));
        }
        public Task CloseAsync() { Interlocked.Increment(ref Closes); return Task.CompletedTask; }
    }

    private static async Task BackgroundConnection()
    {
        var policy = new AllowPolicy(); var source = new McpServersPromptSource(); var registries = new ConcurrentBag<ExtensionRegistry>();
        var reports = new ConcurrentQueue<McpBackgroundConnectionReport>();
        var brokenReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var docsReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var docsRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var docsChannel = new FakeChannel("Docs instructions.\nMore detail.");
        var brokenFailure = new IOException("initialize refused");
        var docs = Entry("{\"command\":\"docs-server\"}", "docs");
        var broken = Entry("{\"command\":\"broken-server\"}", "broken");
        var slow = Entry("{\"command\":\"slow-server\"}", "slow");
        McpBackgroundServerFactory Bind(McpAdmittedChannelFactory channels) => async (entry, owner, attachment, token) =>
        {
            var serverRegistry = new ExtensionRegistry(); registries.Add(serverRegistry);
            var scope = await serverRegistry.ActivateAsync("mcp-" + entry.Name, new EmptyExtension(), token);
            return new McpPreparedServer(entry, serverRegistry, scope, owner, policy, (_, _, _) => ValueTask.FromResult(true), channels,
                new McpRuntimeOptions(attachment.Generation, "1.1.0"), (current, binding) => current.PreparedToolHooks ?? binding.PreparedHooks);
        };
        var factory = new McpSessionRuntimeFactory(async (cwd, generation, token) =>
        {
            var discoveryRegistry = new ExtensionRegistry(); registries.Add(discoveryRegistry);
            var scope = await discoveryRegistry.ActivateAsync("discovery", new EmptyExtension(), token);
            var discovery = new McpRegisteredProfileDiscoveryAdmission(discoveryRegistry, scope,
                [McpDiscoveryExecutableDefinition.CreateCodemode("sync-codemode", "Fixture codemode",
                    (_, _, _) => ValueTask.FromResult(JsonData.Parse("{\"content\":[]}")))],
                policy, generation, (_, _, _) => ValueTask.FromResult(true), (current, binding) => binding.PreparedHooks ?? current.PreparedToolHooks);
            return new McpSessionRuntimeAdmission(new([new(SyncModel, new NoTransport())], [], policy, new SessionRuntimeRegistryOptions { BindNestedCallsToSessionOwner = true }),
                new Disposer(() => ValueTask.CompletedTask), new Disposer(() => ValueTask.CompletedTask), policy,
                new McpServerCatalog([docs, broken, slow], []), [], true, discovery.Prepare)
            {
                BackgroundServers =
                [
                    new("docs", entry => Check(ReferenceEquals(entry, docs)), Bind(async (_, cancellation) =>
                    { await docsRelease.Task.WaitAsync(cancellation); return docsChannel; })),
                    new("broken", _ => { }, Bind((_, _) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(new FakeChannel(null, brokenFailure)))),
                    new("slow", _ => { }, Bind(async (_, cancellation) =>
                    {
                        slowEntered.TrySetResult();
                        try { await Task.Delay(Timeout.Infinite, cancellation); }
                        catch (OperationCanceledException) { slowCancelled.TrySetResult(); throw; }
                        throw new InvalidOperationException("unreachable");
                    }))
                ],
                ServersPromptSource = source,
                ReportBackgroundConnection = report =>
                {
                    reports.Enqueue(report);
                    if (report.Entry.Name == "broken") brokenReported.TrySetResult();
                    if (report.Entry.Name == "docs") docsReported.TrySetResult();
                }
            };
        });
        var backend = new SessionStorageBackend(Path.Combine(Path.GetTempPath(), "mcp-sync-background-" + Guid.NewGuid().ToString("N")), SessionStorageMode.InMemory);
        var ids = 0;
        var lifecycle = new PersistentSessionLifecycle(new([new(SyncModel, new NoTransport())], [], policy, new SessionRuntimeRegistryOptions { BindNestedCallsToSessionOwner = true }),
            () => 0, () => "entry-" + ++ids, nextSessionId: () => "session-" + ++ids, backend: backend, runtimeForAttachment: factory.AcquireAsync);
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "initial", timestamp = "2026-10-08T00:00:00.000Z", cwd = backend.Directory }));
        var initial = await lifecycle.CreateAsync(Path.Combine(backend.Directory, "initial.jsonl"), header, SyncModel);
        ReplaceableAgentSession? owner = null;
        try
        {
            // The session opens while `docs` and `slow` are still connecting: the first prompt does not wait for them.
            owner = await lifecycle.AttachAsync(initial).WaitAsync(TimeSpan.FromSeconds(20));
            Check(!docsRelease.Task.IsCompleted);
            string[] Tools() => [.. owner.Current.Session.CaptureToolCatalogRegistry().RegisteredTools.Select(tool => tool.Adapter.Name)];
            Check(Tools().Contains("codemode") && !Tools().Any(name => name.StartsWith("mcp__", StringComparison.Ordinal)), string.Join(',', Tools()));
            // Every configured server is listed before it connects; codemode is required from the config.
            const string Intro = "MCP servers whose tools are not declared to you. Call the tools of `codemode` servers from codemode scripts.";
            Equal(Intro + "\n- mcp__broken (codemode)\n- mcp__docs (codemode)\n- mcp__slow (codemode)", source.Render(owner.Current.Generation));
            // A failure is reported without affecting the session.
            await brokenReported.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var failed = reports.Single(report => report.Entry.Name == "broken");
            Check(failed.Snapshot is null && failed.Failure is not null && Find<IOException>(failed.Failure) == brokenFailure, failed.Failure?.ToString());
            Check(!owner.Current.Session.Snapshot.IsRetired);
            // `docs` connects later: its tools are registered with the session and its instructions reach the section.
            await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            docsRelease.TrySetResult();
            await docsReported.Task.WaitAsync(TimeSpan.FromSeconds(20));
            var connected = reports.Single(report => report.Entry.Name == "docs");
            Check(connected.Failure is null && connected.Snapshot!.Catalog.Instructions == "Docs instructions.\nMore detail.");
            var tool = owner.Current.Session.CaptureToolCatalogRegistry().RegisteredTools.Single(tool => tool.Adapter.Name == "mcp__docs__search");
            Equal(ToolExposure.Deferred, tool.Exposure); Equal("mcp__docs", tool.Namespace?.Name);
            Equal(Intro + "\n- mcp__broken (codemode)\n- mcp__docs (codemode): Docs instructions.\n- mcp__slow (codemode)", source.Render(owner.Current.Generation));
            Equal(2, reports.Count);
        }
        finally
        {
            // Retiring the attachment cancels the connection still pending and joins it; it is not reported.
            if (owner is not null) await owner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
            else await initial.DisposeAsync();
            foreach (var registry in registries) await registry.DisposeAsync();
        }
        await slowCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Equal(2, reports.Count); Equal(1, docsChannel.Closes);
    }

    private static Task BackgroundPartition()
    {
        var direct = Entry("{\"command\":\"x\",\"exposure\":\"direct\"}", "direct");
        var mixed = Entry("{\"command\":\"x\",\"toolExposure\":{\"one\":\"direct\"}}", "mixed");
        var indirect = Entry("{\"command\":\"x\",\"exposure\":\"deferred\"}", "indirect");
        var disabled = Entry("{\"command\":\"x\",\"enabled\":false}", "disabled");
        var unadmitted = Entry("{\"command\":\"x\"}", "unadmitted");
        McpBackgroundServerFactory none = (_, _, _, _) => throw new InvalidOperationException("Not bound in this case.");
        var validated = new List<string>();
        var catalog = new McpServerCatalog([direct, indirect, disabled, unadmitted], []);
        static (McpServerCatalog, ImmutableArray<(McpServerEntry Entry, McpBackgroundServerFactory Bind)>) Partition(McpServerCatalog input,
            params McpBackgroundServerAdmission[] admissions) => McpBackgroundConnections.Partition(input, [.. admissions]);
        // Background admissions move only enabled admitted servers; the rest keep their pre-open admission.
        var (preOpen, background) = Partition(catalog, new("indirect", entry => validated.Add(entry.Name), none), new("disabled", entry => validated.Add(entry.Name), none));
        Equal("direct,disabled,unadmitted", string.Join(',', preOpen.Servers.Select(entry => entry.Name)));
        Equal("indirect", string.Join(',', background.Select(row => row.Item1.Name))); Equal("indirect", string.Join(',', validated));
        // IMPL-H: servers with direct tools connect in the background too, as in the original; the first prompt waits up to 10 s
        // for them (McpBackgroundConnections.BeforeInputAsync, PiSharp.McpParity.Tests).
        var (none2, mixedBackground) = Partition(new([mixed], []), new McpBackgroundServerAdmission("mixed", _ => { }, none));
        Check(none2.Servers.IsEmpty && mixedBackground.Single().Item1.Name == "mixed");
        Throws<ArgumentException>(() => Partition(catalog, new McpBackgroundServerAdmission("indirect", _ => { }, none), new McpBackgroundServerAdmission("indirect", _ => { }, none)));
        // Without background admissions every server stays pre-open, as before.
        Check(ReferenceEquals(catalog, Partition(catalog).Item1));
        return Task.CompletedTask;
    }

    // ---------- OAuth: client name and durable store ----------

    private static async Task DefaultClientName()
    {
        var plain = Entry("{\"url\":\"https://a.example/mcp\"}");
        Equal("pi", McpDefaultOAuthClientMetadata.DefaultClientName); Equal("pi", McpDefaultOAuthClientMetadata.ClientName(plain.Config));
        Equal("{\"client_name\":\"pi\",\"redirect_uris\":[\"http://127.0.0.1:43123/callback\"],\"grant_types\":[\"authorization_code\",\"refresh_token\"]," +
            "\"response_types\":[\"code\"],\"token_endpoint_auth_method\":\"none\"}",
            McpDefaultOAuthClientMetadata.Create(plain.Config, LoopbackCallback.AbsoluteUri, false).ToString());
        var named = Entry("{\"url\":\"https://a.example/mcp\",\"oauth\":{\"clientName\":\"Claude Code\",\"clientId\":\"x\",\"clientSecret\":\"s\"}}");
        Equal("Claude Code", McpDefaultOAuthClientMetadata.ClientName(named.Config));
        var confidential = McpDefaultOAuthClientMetadata.Create(named.Config, LoopbackCallback.AbsoluteUri, true).Value;
        Equal("Claude Code", confidential.GetProperty("client_name").GetString());
        Equal("client_secret_post", confidential.GetProperty("token_endpoint_auth_method").GetString());
        // Through the host, dynamic registration sends the default name.
        var store = new McpOAuthCredentialStore(new MemoryBackend()).ForServer("docs", Server);
        var server = new FakeServer { Respond = (request, _, _) => Task.FromResult(DiscoveryResponse(request.RequestUri!, "{\"client_id\":\"registered\"}")) };
        var host = McpDefaultOAuthHost.Install(Resources(store, server, (_, _) => ValueTask.CompletedTask) with
            { ClientMetadata = McpDefaultOAuthClientMetadata.Create(plain.Config, LoopbackCallback.AbsoluteUri, false) });
        try
        {
            Equal(McpOAuthAuthorizationOutcome.Redirect, (await host.AuthorizeAsync(host.Options())).Outcome);
            var registration = server.Log.Single(row => row.Uri.AbsolutePath == "/register").Body;
            Check(registration.StartsWith("{\"client_name\":\"pi\",\"redirect_uris\":[\"http://127.0.0.1:43123/callback\"],", StringComparison.Ordinal), registration);
        }
        finally { await host.DisposeAsync(); }
    }

    private static HttpResponseMessage DiscoveryResponse(Uri uri, string registration) => uri.AbsolutePath switch
    {
        "/.well-known/oauth-protected-resource/mcp" => Response(200, "{\"resource\":\"" + Server.AbsoluteUri + "\",\"authorization_servers\":[\"" + Issuer + "\"]}"),
        "/.well-known/oauth-authorization-server" => Response(200, Metadata()),
        "/register" => Response(201, registration),
        _ => Response(404, "{}")
    };

    private static async Task FileBackend()
    {
        var root = FreshDirectory("auth-store");
        try
        {
            var agent = Path.Combine(root, "agent");
            var backend = McpOAuthFileCredentialBackend.InAgentDirectory(agent);
            Equal(Path.Combine(agent, "mcp-auth.json"), backend.FilePath); Equal(backend.FilePath + ".lock", backend.LockPath);
            Throws<ArgumentException>(() => new McpOAuthFileCredentialBackend("relative/mcp-auth.json"));
            // The first use creates the directory and an empty document, and releases the lock.
            Equal("{}", backend.WithLock(current => (current, (string?)null)));
            Check(File.Exists(backend.FilePath) && !File.Exists(backend.LockPath) && !Directory.Exists(backend.LockPath));
            if (!OperatingSystem.IsWindows())
            {
                Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(agent));
                Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(backend.FilePath));
            }
            // Credentials written through one backend are read by another process's backend on the same file.
            await new McpOAuthCredentialStore(backend).ForServer("docs", Server).SaveAsync(new(Server.AbsoluteUri, Tokens: new("durable", "Bearer", RefreshToken: "r")));
            Equal("durable", new McpOAuthCredentialStore(McpOAuthFileCredentialBackend.InAgentDirectory(agent)).Tokens("docs", Server)!.AccessToken);
            Equal("{\n  \"mcp__docs|https://mcp.example.test/mcp\": {\n    \"serverUrl\": \"https://mcp.example.test/mcp\",\n    \"tokens\": {\n" +
                "      \"access_token\": \"durable\",\n      \"token_type\": \"Bearer\",\n      \"refresh_token\": \"r\"\n    }\n  }\n}\n",
                File.ReadAllText(backend.FilePath));
            // While another process holds the original's lock directory, an update gives up after its retries.
            Directory.CreateDirectory(backend.LockPath);
            var started = System.Diagnostics.Stopwatch.StartNew();
            Throws<IOException>(() => backend.WithLock(current => (0, (string?)"{\"lost\":true}")));
            Check(started.Elapsed < TimeSpan.FromSeconds(5)); Check(!File.ReadAllText(backend.FilePath).Contains("lost", StringComparison.Ordinal));
            // A lock its holder stopped renewing (the process died) is taken over.
            Directory.SetLastWriteTimeUtc(backend.LockPath, DateTime.UtcNow - TimeSpan.FromMinutes(1));
            Equal(1, backend.WithLock(current => (1, (string?)null)));
            Check(!Directory.Exists(backend.LockPath) && !File.Exists(backend.LockPath));
            // Separate instances (as separate processes would) never lose each other's updates. Like upstream's
            // acquireLockSyncWithRetry, an update gives up after 10 x 20 ms while another holds the lock; a busy runner can
            // exceed that, so a writer that gave up (and changed nothing) tries again.
            var counter = Path.Combine(root, "counter.json");
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            {
                var own = new McpOAuthFileCredentialBackend(counter);
                for (var index = 0; index < 20; index++)
                    for (var attempt = 1; ; attempt++)
                        try
                        {
                            own.WithLock(current => (0, (string?)(int.Parse(current == "{}" ? "0" : current!, System.Globalization.CultureInfo.InvariantCulture) + 1)
                                .ToString(System.Globalization.CultureInfo.InvariantCulture)));
                            break;
                        }
                        catch (IOException error) when (attempt < 50 && error.Message.StartsWith("The MCP credential store is locked", StringComparison.Ordinal)) { }
            })));
            Equal("60", File.ReadAllText(counter));
        }
        finally { Delete(root); }
    }

    // ---------- mcp login / logout ----------

    private const string McpJson = """
        {"mcpServers":{
          "docs":{"url":"https://mcp.example.test/mcp","oauth":{"scope":"read  read"}},
          "keyed":{"url":"https://keyed.example.test/mcp","headers":{"Authorization":"Bearer x"}},
          "local":{"command":"local-server"}}}
        """;
    private static (string Root, McpCommandOptions Options) CliFixture(string mcpJson = McpJson)
    {
        var root = FreshDirectory("cli");
        Directory.CreateDirectory(Path.Combine(root, "agent")); Directory.CreateDirectory(Path.Combine(root, "project"));
        File.WriteAllText(Path.Combine(root, "agent", "mcp.json"), mcpJson);
        return (root, new(Path.Combine(root, "project"), Path.Combine(root, "agent")) { ReadRedirectUrl = WaitForCancellation });
    }
    private static async Task<string?> WaitForCancellation(CancellationToken token)
    {
        try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
        return null;
    }
    private static async Task<(int Code, string Output, string Error)> Mcp(McpCommandOptions options, params string[] args)
    {
        using var output = new StringWriter { NewLine = "\n" }; using var error = new StringWriter { NewLine = "\n" };
        var code = await McpCommand.RunAsync(args, output, error, options).WaitAsync(TimeSpan.FromSeconds(60));
        return (code, output.ToString(), error.ToString());
    }
    private sealed class FakeAuthorizationServer : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<(string Method, Uri Uri, string Body)> Log = new();
        internal Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> Respond = (request, body, _) => Task.FromResult(Default(request, body));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Log.Enqueue((request.Method.Method, request.RequestUri!, body)); Interlocked.Increment(ref requests);
            return await Respond(request, body, token);
        }
        internal static HttpResponseMessage Default(HttpRequestMessage request, string body) => request.RequestUri!.AbsolutePath switch
        {
            // The MCP server itself: it accepts only the tokens this authorization server issued, and offers one tool.
            "/mcp" => McpEndpoint(request, body),
            // Dynamic registration echoes the proposed redirect URIs, like a real server.
            "/register" => Response(201, "{\"client_id\":\"registered\",\"redirect_uris\":" +
                JsonDocument.Parse(body).RootElement.GetProperty("redirect_uris").GetRawText() + "}"),
            "/token" => Response(200, "{\"access_token\":\"issued-" + (body.Contains("grant_type=refresh_token", StringComparison.Ordinal) ? "refresh" : "code") +
                "\",\"token_type\":\"Bearer\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}"),
            _ => DiscoveryResponse(request.RequestUri!, "{}")
        };
        internal static HttpResponseMessage McpEndpoint(HttpRequestMessage request, string body)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.MethodNotAllowed);
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.OK);
            if (request.Headers.Authorization?.ToString() is not { } authorization || !authorization.StartsWith("Bearer issued-", StringComparison.Ordinal))
            {
                var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                denied.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer resource_metadata=\"https://mcp.example.test/.well-known/oauth-protected-resource/mcp\"");
                return denied;
            }
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            var method = json.RootElement.GetProperty("method").GetString();
            var result = method switch
            {
                "initialize" => """{"protocolVersion":"2025-11-25","serverInfo":{"name":"docs","version":"1"},"capabilities":{"tools":{}}}""",
                "tools/list" => """{"tools":[{"name":"lookup","description":"Looks up.","inputSchema":{"type":"object"}}]}""",
                _ => "{}"
            };
            var response = Response(200, "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}");
            if (method == "initialize") response.Headers.Add("Mcp-Session-Id", "session-1");
            return response;
        }
    }

    private static async Task McpCliErrors()
    {
        var (root, options) = CliFixture();
        try
        {
            var help = (0, McpCommand.Help + "\n", "");
            Equal(help, await Mcp(options)); Equal(help, await Mcp(options, "--help")); Equal(help, await Mcp(options, "login", "docs", "-h"));
            Check(McpCommand.Help.StartsWith("Usage:\n  PiSharp.Cli mcp add <server> [options] -- <command> [args...]\n  PiSharp.Cli mcp add <server> [options] --url <url>\n" +
                "  PiSharp.Cli mcp remove <server> [-l]\n  PiSharp.Cli mcp list [--json]\n  PiSharp.Cli mcp login <server> [--timeout <seconds>]\n  PiSharp.Cli mcp logout <server>\n", StringComparison.Ordinal));
            Check(McpCommand.Help.EndsWith("  --timeout <seconds>     How long login waits for the browser (default: 300)", StringComparison.Ordinal));
            const string Hint = "Use \"PiSharp.Cli mcp --help\" for usage.\n";
            Equal((1, "", "Unknown mcp command \"bogus\".\n" + Hint), await Mcp(options, "bogus"));
            Equal((1, "", "Usage: PiSharp.Cli mcp login <server>\n" + Hint), await Mcp(options, "login"));
            Equal((1, "", "Usage: PiSharp.Cli mcp logout <server>\n" + Hint), await Mcp(options, "logout", "docs", "extra"));
            Equal((1, "", "Unknown option --bogus.\n" + Hint), await Mcp(options, "login", "docs", "--bogus"));
            Equal((1, "", "Unknown option --timeout.\n" + Hint), await Mcp(options, "logout", "docs", "--timeout", "5"));
            Equal((1, "", "--timeout needs a value.\n"), await Mcp(options, "login", "docs", "--timeout"));
            foreach (var invalid in new[] { "0", "-1", "abc", "Infinity", "" })
                Equal((1, "", "--timeout must be a positive number of seconds.\n"), await Mcp(options, "login", "docs", "--timeout", invalid));
            Equal((1, "", "No MCP server named \"nope\". Configured: docs, keyed, local.\n"), await Mcp(options, "login", "nope"));
            Equal((1, "", "MCP server \"keyed\" does not use OAuth. Only HTTP servers without an Authorization header do.\n"), await Mcp(options, "login", "keyed"));
            Equal((1, "", "MCP server \"local\" does not use OAuth. Only HTTP servers without an Authorization header do.\n"), await Mcp(options, "logout", "local"));
            // A project mcp.json is not read without project trust, and the message says so; a trusted project's servers are listed.
            Directory.CreateDirectory(Path.Combine(options.Cwd, ".pi"));
            File.WriteAllText(Path.Combine(options.Cwd, ".pi", "mcp.json"), "{\"mcpServers\":{\"team\":{\"command\":\"team-server\"},\"local\":{\"enabled\":false}}}");
            Equal((1, "", $"No MCP server named \"nope\". {Path.Combine(options.Cwd, ".pi", "mcp.json")} is ignored because the project is not trusted. Start PiSharp.Cli in the project to trust it. Configured: docs, keyed, local.\n"),
                await Mcp(options, "login", "nope"));
            Equal((1, "", "No MCP server named \"nope\". Configured: docs, keyed, local, team.\n"),
                await Mcp(options with { IsProjectTrusted = cwd => cwd == options.Cwd }, "login", "nope"));
            Equal((1, "", "No MCP server named \"docs\". Configured: none.\n"), await Mcp(CliFixture("{}").Options with { }, "login", "docs"));
            // Client secrets resolve like the original's config values, `!command` values included (decision 0004).
            Equal("a-s3cret$!", McpCommand.ResolveConfigValue("a-${SECRET}$$$!", "secret", name => name == "SECRET" ? "s3cret" : null));
            Equal("x$", McpCommand.ResolveConfigValue("x$", "secret", _ => null));
            Equal("Failed to resolve d from environment variables: A, B",
                Throws<InvalidOperationException>(() => McpCommand.ResolveConfigValue("$A-${B}", "d", _ => null)).Message);
            Equal("mcp-secret", McpCommand.ResolveConfigValue("!echo mcp-secret", "d", _ => null));
            Equal("Failed to resolve d from shell command: exit 1",
                Throws<InvalidOperationException>(() => McpCommand.ResolveConfigValue("!exit 1", "d", _ => "x")).Message);
        }
        finally { Delete(root); }
    }

    private static async Task McpCliLogout()
    {
        var (root, options) = CliFixture();
        try
        {
            var store = new McpOAuthCredentialStore(McpOAuthFileCredentialBackend.InAgentDirectory(options.AgentDirectory));
            await store.ForServer("docs", Server).SaveAsync(new(Server.AbsoluteUri, Tokens: new("t", "Bearer")));
            Equal((0, "Signed out of MCP server \"docs\".\n", ""), await Mcp(options, "logout", "docs"));
            Equal("{}\n", File.ReadAllText(Path.Combine(options.AgentDirectory, "mcp-auth.json")));
            Equal((0, "No stored credentials for MCP server \"docs\".\n", ""), await Mcp(options, "logout", "docs"));
        }
        finally { Delete(root); }
    }

    private static async Task McpCliLogin()
    {
        var (root, options) = CliFixture();
        try
        {
            var server = new FakeAuthorizationServer(); Task<(HttpStatusCode Status, string Page)>? browser = null; string? opened = null;
            options = options with
            {
                HttpHandler = server,
                OpenUrl = url =>
                {
                    opened = url;
                    // The user approves in the browser, which follows the redirect to the loopback callback.
                    browser = Task.Run(async () =>
                    {
                        var authorize = new Uri(url);
                        using var client = new HttpClient();
                        using var page = await client.GetAsync(Query(authorize, "redirect_uri") + "?code=granted&state=" + Uri.EscapeDataString(Query(authorize, "state")!));
                        return (page.StatusCode, await page.Content.ReadAsStringAsync());
                    });
                }
            };
            var (code, output, error) = await Mcp(options, "login", "docs", "--timeout", "30");
            Equal((0, ""), (code, error));
            Check(opened is not null && opened.StartsWith(Issuer + "authorize?", StringComparison.Ordinal), opened);
            // IMPL-H (cli.ts login): the command connects first (the server answers 401), signs in, connects again and reports the tools.
            Equal($"Sign in to MCP server \"docs\" in your browser:\n{opened}\nSigned in to MCP server \"docs\" (1 tools).\n", output);
            Check(server.Log.Where(row => row.Uri.AbsolutePath == "/mcp" && row.Method == "POST").Select(row => JsonDocument.Parse(row.Body).RootElement.GetProperty("method").GetString())
                .SequenceEqual(["initialize", "initialize", "notifications/initialized", "tools/list"]), "connect first, then again after the sign-in");
            // Signed in already: the command connects and reports it without a browser.
            Equal((0, "Already signed in to MCP server \"docs\" (1 tools).\n", ""), await Mcp(options with { OpenUrl = _ => throw new InvalidOperationException("No browser expected.") }, "login", "docs"));
            var authorize = new Uri(opened!);
            var redirect = Query(authorize, "redirect_uri")!;
            Check(System.Text.RegularExpressions.Regex.IsMatch(redirect, @"^http://127\.0\.0\.1:\d+/callback$"), redirect);
            Equal("registered", Query(authorize, "client_id")); Equal("S256", Query(authorize, "code_challenge_method"));
            // The configured scope, each scope once.
            Equal("read", Query(authorize, "scope"));
            Check(System.Text.RegularExpressions.Regex.IsMatch(Query(authorize, "state")!, "^[0-9a-f]{64}$"));
            var (status, page) = await browser!;
            Equal(HttpStatusCode.OK, status); Check(page.Contains("Signed in to the MCP server. You may now close this page.", StringComparison.Ordinal));
            // Dynamic registration uses pi's client name and the loopback redirect URI.
            var registration = server.Log.Single(row => row.Uri.AbsolutePath == "/register").Body;
            Check(registration.StartsWith("{\"client_name\":\"pi\",\"redirect_uris\":[" + JsonSerializer.Serialize(redirect) + "],\"grant_types\":[\"authorization_code\",\"refresh_token\"]," +
                "\"response_types\":[\"code\"],\"token_endpoint_auth_method\":\"none\",\"application_type\":\"native\"", StringComparison.Ordinal), registration);
            var exchange = server.Log.Single(row => row.Uri.AbsolutePath == "/token").Body;
            Check(exchange.StartsWith("grant_type=authorization_code&code=granted&code_verifier=", StringComparison.Ordinal) &&
                exchange.Contains("&redirect_uri=" + Uri.EscapeDataString(redirect), StringComparison.Ordinal), exchange);
            // The tokens are in the agent directory's mcp-auth.json, keyed by server name and URL.
            using var stored = JsonDocument.Parse(File.ReadAllText(Path.Combine(options.AgentDirectory, "mcp-auth.json")));
            var state = stored.RootElement.GetProperty("mcp__docs|https://mcp.example.test/mcp");
            Equal("issued-code", state.GetProperty("tokens").GetProperty("access_token").GetString());
            Equal("read", state.GetProperty("tokens").GetProperty("scope").GetString());
            Equal("registered", state.GetProperty("clientInformation").GetProperty("client_id").GetString());
            // The callback listener is closed once the command returns.
            await RefusedAsync(new Uri(redirect).Port);
        }
        finally { Delete(root); }
    }

    private static async Task McpCliRefresh()
    {
        var port = FreePort();
        var (root, options) = CliFixture("{\"mcpServers\":{\"docs\":{\"url\":\"https://mcp.example.test/mcp\",\"oauth\":{\"callbackUrl\":\"http://127.0.0.1:" + port + "/callback\"}}}}");
        try
        {
            var redirect = $"http://127.0.0.1:{port}/callback";
            var store = new McpOAuthCredentialStore(McpOAuthFileCredentialBackend.InAgentDirectory(options.AgentDirectory)).ForServer("docs", Server);
            await store.SaveAsync(Cached(Metadata(), tokens: new("stale", "Bearer", Scope: "read", RefreshToken: "refresh-0")) with
                { ClientInformation = JsonData.Parse("{\"client_id\":\"admitted-client\",\"redirect_uris\":[\"" + redirect + "\"]}") });
            var server = new FakeAuthorizationServer(); var opened = 0;
            var result = await Mcp(options with { HttpHandler = server, OpenUrl = _ => opened++ }, "login", "docs");
            // The connection refreshes the rejected token with the stored refresh token, so the command reports the server as signed in
            // without the browser (cli.ts login connects first); the registered client is kept.
            Equal((0, "Already signed in to MCP server \"docs\" (1 tools).\n", ""), result); Equal(0, opened);
            var refresh = server.Log.Single(row => row.Uri.AbsolutePath == "/token").Body;
            Check(refresh.StartsWith("grant_type=refresh_token&refresh_token=refresh-0", StringComparison.Ordinal), refresh);
            var saved = (await store.LoadAsync())!;
            Equal("issued-refresh", saved.Tokens!.AccessToken); Equal("read", saved.Tokens.Scope);
        }
        finally { Delete(root); }
    }

    private static async Task McpCliPastedRedirect()
    {
        var (root, options) = CliFixture();
        try
        {
            var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pasted = new List<string>();
            var server = new FakeAuthorizationServer();
            // The browser runs on another machine: the user pastes the URL it was redirected to.
            options = options with
            {
                HttpHandler = server, OpenUrl = url => opened.TrySetResult(new(url)),
                ReadRedirectUrl = async token =>
                {
                    var authorize = await opened.Task.WaitAsync(token);
                    var url = $"  {Query(authorize, "redirect_uri")}?code=pasted&state={Query(authorize, "state")}  ";
                    pasted.Add(url); return url;
                }
            };
            Equal(0, (await Mcp(options, "login", "docs")).Code);
            Check(server.Log.Single(row => row.Uri.AbsolutePath == "/token").Body.Contains("code=pasted", StringComparison.Ordinal));
            // A URL from another sign-in is rejected before any code exchange.
            var other = new FakeAuthorizationServer(); var otherOpened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            File.Delete(Path.Combine(options.AgentDirectory, "mcp-auth.json"));
            var rejected = await Mcp(options with
            {
                HttpHandler = other, OpenUrl = url => otherOpened.TrySetResult(new(url)),
                ReadRedirectUrl = async token => { var authorize = await otherOpened.Task.WaitAsync(token); return $"{Query(authorize, "redirect_uri")}?code=x&state=wrong"; }
            }, "login", "docs");
            Equal((1, "Sign in to MCP server \"docs\" in your browser:\n" + (await otherOpened.Task).AbsoluteUri + "\n",
                "Sign-in to MCP server \"docs\" failed: The redirect URL belongs to a different sign-in\n"), rejected);
            Check(!other.Log.Any(row => row.Uri.AbsolutePath == "/token"));
            Equal("The redirect URL does not match this sign-in's redirect URI", Throws<InvalidOperationException>(() =>
                McpSignIn.ResponseFromRedirectUrl("http://127.0.0.1:1/other?code=c&state=s", "s", new("http://127.0.0.1:1/callback"))).Message);
            Equal("access_denied", Throws<InvalidOperationException>(() =>
                McpSignIn.ResponseFromRedirectUrl("http://127.0.0.1:1/callback?error=access_denied&state=s", "s", new("http://127.0.0.1:1/callback"))).Message);
            Equal(new McpOAuthCallback("c", "s", "https://issuer.example.test/"),
                McpSignIn.ResponseFromRedirectUrl("http://127.0.0.1:1/callback?code=c&state=s&iss=https%3A%2F%2Fissuer.example.test%2F", "s", new("http://127.0.0.1:1/callback")));
        }
        finally { Delete(root); }
    }

    private static async Task McpCliTimeout()
    {
        var (root, options) = CliFixture();
        try
        {
            // The authorization server never answers the registration: the 15 s request timeout would come later, but
            // --timeout covers the whole sign-in.
            var hanging = new FakeAuthorizationServer();
            hanging.Respond = async (request, body, token) =>
            {
                if (request.RequestUri!.AbsolutePath == "/register") await Task.Delay(Timeout.Infinite, token);
                return FakeAuthorizationServer.Default(request, body);
            };
            var started = System.Diagnostics.Stopwatch.StartNew();
            Equal((1, "", "Sign-in to MCP server \"docs\" was cancelled or not completed within 1 seconds.\n"),
                await Mcp(options with { HttpHandler = hanging, OpenUrl = _ => throw new InvalidOperationException("No browser expected.") }, "login", "docs", "--timeout", "1"));
            Check(started.Elapsed < TimeSpan.FromSeconds(10), started.Elapsed.ToString());
            // The browser never comes back: the sign-in ends at the timeout and the callback listener is closed.
            string? opened = null;
            var waiting = await Mcp(options with { HttpHandler = new FakeAuthorizationServer(), OpenUrl = url => opened = url }, "login", "docs", "--timeout", "1.2");
            Equal((1, $"Sign in to MCP server \"docs\" in your browser:\n{opened}\n", "Sign-in to MCP server \"docs\" was cancelled or not completed within 1 seconds.\n"), waiting);
            await RefusedAsync(new Uri(Query(new Uri(opened!), "redirect_uri")!).Port);
        }
        finally { Delete(root); }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
    private static async Task RefusedAsync(int port)
    {
        using var client = new TcpClient();
        try { await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (SocketException) { return; }
        throw new InvalidOperationException($"The callback listener on port {port} is still open.");
    }
}
