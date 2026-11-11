// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/mcp/src/oauth/flow.ts, packages/mcp/src/client.ts.
// Authored native regression; not an upstream execution capture.
using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Runtime.Mcp.Authentication;
using PiSharp.Extensions.Runtime.Mcp.Resources;

internal static partial class Program
{
    internal static IEnumerable<(string Id, Func<Task> Run)> Sync152McpCases() =>
    [
        ("mcp152.shared-fields-preserve-application-data", SharedFields152),
        ("mcp152.optional-output-save-and-unicode", Output152),
        ("mcp152.split-signin-persisted-and-superseded", Split152),
        ("mcp152.cancel-queued-lock-without-refresh", QueuedLock152),
        ("mcp152.cancel-sent-refresh-persists-rotated-token", SentRefresh152),
        ("mcp152.lock-busy-only-falls-back", BusyLock152),
        ("mcp152.refresh-only-preserves-authorization", RefreshOnly152),
        ("mcp152.generic-client-metadata-path", MetadataPath152),
        ("mcp152.direct-host-provider-credentials-stay-in-agent-directory", ProviderDirectories152)
    ];

    private static void Assert152(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
    private sealed class Store152 : IMcpAdmittedOAuthStateStore
    {
        internal McpOAuthState? State = new("https://mcp.invalid/api");
        public ValueTask<McpOAuthState?> LoadAsync() => ValueTask.FromResult(State);
        public ValueTask SaveAsync(McpOAuthState state) { State = state; return ValueTask.CompletedTask; }
    }
    private sealed class Lock152 : IAsyncDisposable
    { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private static McpOAuthClientOptions Client152(Store152 store, McpSignInFlow flow,
        Func<CancellationToken, Task<IAsyncDisposable>>? acquire = null) =>
        new(new("https://mcp.invalid/api"), store, new(), "test-client", flow,
            acquire ?? (_ => Task.FromResult<IAsyncDisposable>(new Lock152())));

    private static Task SharedFields152()
    {
        using var raw = JsonDocument.Parse("""{"url":"https://mcp.invalid","exposure":42,"custom":{"x":1},"oauth":{"clientRegistration":"cimd","callbackUrl":"http://localhost/any"}}""");
        var missing = McpConfigurationReader.ValidateServer("docs", raw.RootElement);
        Assert152(!missing.IsValid && missing.Error!.Contains("no Client ID Metadata Documents"), "shared CIMD needs caller callback check");
        var valid = McpConfigurationReader.ValidateServer("docs", raw.RootElement, _ => null);
        Assert152(valid.IsValid && valid.Config!.Value.GetProperty("exposure").GetInt32() == 42, "shared validator must retain unchecked app fields");
        Assert152(!McpConfigurationReader.Validate("docs", raw.RootElement).IsValid, "CLI still validates app exposure");
        var schema = McpCatalogPlanner.NormalizeInputSchema(JsonData.Parse("""{"type":null,"additionalProperties":false}"""));
        Assert152(schema.Value.GetProperty("type").GetString() == "object" && schema.Value.GetProperty("properties").ValueKind == JsonValueKind.Object, "schema object defaults");
        return Task.CompletedTask;
    }

    private static async Task Output152()
    {
        var input = ImmutableArray.Create(JsonData.Parse(JsonSerializer.Serialize(new { type = "text", text = new string('x', 15000) + string.Concat(Enumerable.Repeat("😀", 4000)) })));
        var result = await McpContentLimiter.LimitAsync(input);
        var text = result.Content[0].Value.GetProperty("text").GetString()!;
        Assert152(result.FullOutputPath is null && !text.Contains("Full output:") && !text.Contains('\uFFFD'), "optional saver and UTF8 boundaries");
        var failure = await McpContentLimiter.LimitAsync(input, (_, _, _) => ValueTask.FromException<string>(new InvalidOperationException("save failed")));
        Assert152(failure.Content[0].Value.GetProperty("text").GetString()!.Contains("Could not save the full output: save failed"), "all ordinary saver errors reported");
    }

    private static async Task Split152()
    {
        var store = new Store152(); var exchanges = 0;
        var client = Client152(store, (request, _) =>
        {
            if (request.Code is not null) { exchanges++; return Task.FromResult(new McpSignInFlowResult(McpOAuthAuthorizationOutcome.Authorized)); }
            store.State = store.State! with { OAuthState = "fresh-state", CodeVerifier = "verifier" };
            return Task.FromResult(new McpSignInFlowResult(McpOAuthAuthorizationOutcome.Redirect,
                new("https://auth.invalid/authorize?redirect_uri=https%3A%2F%2Fapp.invalid%2Fcallback&scope=read")));
        });
        var options = new PiSharp.Extensions.Mcp.Authentication.McpSignInOptions(client, "https://app.invalid/callback");
        var first = (await new McpOAuthSignIn().StartAsync(options)).Redirect!;
        var persisted = JsonSerializer.Deserialize<McpSignInRedirect>(JsonSerializer.Serialize(first))!;
        await new McpOAuthSignIn().CompleteAsync(options, persisted, new("code", "fresh-state"));
        Assert152(exchanges == 1 && persisted.Scope == "read", "separate invocation completion");
        store.State = store.State! with { OAuthState = "newer-signin" };
        try { await new McpOAuthSignIn().CompleteAsync(options, persisted, new("code", "fresh-state")); throw new Exception("accepted superseded state"); }
        catch (InvalidOperationException) { }
        Assert152(exchanges == 1, "must reject before exchange");
    }

    private static async Task QueuedLock152()
    {
        var store = new Store152 { State = new("https://mcp.invalid/api", Tokens: new("old", "Bearer", RefreshToken: "refresh")) };
        var gate = new TaskCompletionSource<IAsyncDisposable>(TaskCreationOptions.RunContinuationsAsynchronously); var requests = 0;
        var client = Client152(store, (_, _) => { requests++; return Task.FromResult(new McpSignInFlowResult(McpOAuthAuthorizationOutcome.Authorized)); }, _ => gate.Task);
        var engine = new McpOAuthSignIn(); using var cancel = new CancellationTokenSource();
        var start = engine.StartAsync(new(client, "https://app.invalid/callback"), cancel.Token);
        cancel.Cancel();
        try { await start.WaitAsync(TimeSpan.FromSeconds(2)); throw new Exception("accepted cancelled sign-in"); }
        catch (McpSignInCancellationException) { }
        gate.SetResult(new Lock152()); await engine.SettledAsync();
        Assert152(requests == 0, "a delayed lock must not refresh after cancellation");
    }

    private static async Task SentRefresh152()
    {
        var store = new Store152 { State = new("https://mcp.invalid/api", Tokens: new("old", "Bearer", RefreshToken: "old-refresh")) };
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client152(store, async (request, token) =>
        {
            Assert152(request.RefreshOnly && !token.CanBeCanceled, "refresh owns persistence independently of caller");
            sent.SetResult(); await reply.Task;
            store.State = store.State! with { Tokens = new("new", "Bearer", RefreshToken: "rotated") };
            return new(McpOAuthAuthorizationOutcome.Authorized);
        });
        using var cancel = new CancellationTokenSource(); var engine = new McpOAuthSignIn();
        var start = engine.StartAsync(new(client, "https://app.invalid/callback"), cancel.Token);
        await sent.Task; cancel.Cancel();
        try { await start; throw new Exception("accepted cancelled sign-in"); } catch (McpSignInCancellationException) { }
        var settling = engine.SettledAsync(); Assert152(!settling.IsCompleted, "settlement must await token save");
        reply.SetResult(); await settling;
        Assert152(store.State!.Tokens!.RefreshToken == "rotated", "rotated token saved");
    }

    private static async Task BusyLock152()
    {
        async Task Check(Exception error, bool browser)
        {
            var store = new Store152(); var calls = 0;
            var client = Client152(store, (_, _) => { calls++; store.State = store.State! with { OAuthState = "s" }; return Task.FromResult(new McpSignInFlowResult(McpOAuthAuthorizationOutcome.Redirect, new("https://auth.invalid"))); },
                _ => Task.FromException<IAsyncDisposable>(error));
            try { await new McpOAuthSignIn().StartAsync(new(client, "https://app.invalid/callback")); Assert152(browser, "unexpected fallback"); }
            catch (IOException) { Assert152(!browser, "busy lock must fall back"); }
            Assert152(calls == (browser ? 1 : 0), "only typed busy lock allows browser step");
        }
        await Check(new McpRefreshLockBusyException(), true);
        await Check(new IOException("storage failed"), false);
    }

    private static async Task RefreshOnly152()
    {
        var mutations = new List<string>(); McpOAuthAdmittedClient? client = null;
        var dependencies = new McpOAuthOrchestrationDependencies(
            (_, _) => ValueTask.FromResult(new McpOAuthDiscoveredServer("https://auth.invalid", null, null)), null,
            _ => ValueTask.FromResult(client), null,
            (_, _, _, _) => { mutations.Add("register"); return ValueTask.FromResult(new McpOAuthAdmittedClient("new")); },
            _ => ValueTask.FromResult<McpOAuthTokens?>(null), (_, _, _) => throw new Exception("refresh"),
            _ => throw new Exception("read verifier"), (_, _, _, _) => throw new Exception("exchange"),
            (_, _) => { mutations.Add("authorize"); throw new Exception("authorize"); },
            (_, _) => ValueTask.CompletedTask, (_, _) => { mutations.Add("verifier"); return ValueTask.CompletedTask; },
            (_, _) => { mutations.Add("redirect"); return ValueTask.CompletedTask; }, null);
        await using var flow = new McpAdmittedOAuthOrchestrator(dependencies, new());
        var outcome = await flow.AuthorizeAsync(new(new("https://mcp.invalid/api"), JsonData.Parse("{}"), RefreshOnly: true));
        Assert152(outcome.Outcome == McpOAuthAuthorizationOutcome.Redirect && mutations.Count == 0, "refresh-only with no client is inert");
    }

    private static Task MetadataPath152()
    {
        var metadata = JsonData.Parse("""{"client_id_metadata_document_supported":true,"token_endpoint_auth_methods_supported":["none"]}""");
        var result = McpOAuthClientMetadataDocuments.TryCreate(new("https://app.invalid/oauth"), new("https://mcp.invalid/api"), "https://app.invalid/custom/", metadata)!;
        Assert152(new Uri(result.RedirectUrl).AbsolutePath.StartsWith("/custom/") && !new Uri(result.RedirectUrl).AbsolutePath.Contains("//"), "append caller path exactly once");
        return Task.CompletedTask;
    }

    private sealed class ProviderHandler152 : HttpMessageHandler
    {
        internal readonly List<string?> Tokens = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Tokens.Add(request.Headers.Authorization?.Parameter);
            using var message = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":" + message.RootElement.GetProperty("id").GetRawText() + ",\"result\":{}}",
                    System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private static async Task ProviderDirectories152()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-mcp152-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        using var handlerA = new ProviderHandler152(); using var handlerB = new ProviderHandler152();
        // Direct construction exercises the fallback, without McpCommandOptions' supplied ProviderToken callback.
        var hostA = new PiSharp.Cli.Mcp.McpSessionHost(first, root, () => []) { CreateHttpHandler = () => handlerA };
        var hostB = new PiSharp.Cli.Mcp.McpSessionHost(second, root, () => []) { CreateHttpHandler = () => handlerB };
        using var httpA = hostA.CreateClient(); using var httpB = hostB.CreateClient();
        var entry = Entry("{\"url\":\"https://mcp.invalid/api\",\"auth\":{\"provider\":\"openai\"}}", "test");
        var options = new PiSharp.Extensions.Mcp.Runtime.McpRuntimeOptions(1, "test");
        var channelA = await hostA.Channel(entry, root, options, () => httpA)(entry, CancellationToken.None);
        var channelB = await hostB.Channel(entry, root, options, () => httpB)(entry, CancellationToken.None);
        try
        {
            await channelA.StartAsync(CancellationToken.None); await channelB.StartAsync(CancellationToken.None);
            // Created after hosts and channels: credential resolution must be lazy.
            await Write(first, "first-1");
            await Send(channelA); await Send(channelB);
            Assert152(handlerA.Tokens.SequenceEqual(new[] { "first-1" }), "first host used another directory");
            Assert152(handlerB.Tokens.Count == 1 && handlerB.Tokens[0] is null, "empty directory must not fall back to global credentials");
            await Write(first, "first-2"); await Write(second, "second-1");
            await Send(channelA); await Send(channelB);
            Assert152(handlerA.Tokens[1] == "first-2" && handlerB.Tokens[1] == "second-1", "current credentials must be reread independently");
        }
        finally
        {
            await channelA.CloseAsync(); await channelB.CloseAsync();
            Directory.Delete(root, recursive: true);
        }
        static Task Write(string directory, string key) => File.WriteAllTextAsync(Path.Combine(directory, "auth.json"),
            JsonSerializer.Serialize(new Dictionary<string, object> { ["openai"] = new { type = "api_key", key } }));
        static async Task Send(PiSharp.Extensions.Mcp.Runtime.IMcpAdmittedRequestChannel channel) =>
            _ = await channel.RequestAsync("test", null, new(5000), CancellationToken.None);
    }
}
