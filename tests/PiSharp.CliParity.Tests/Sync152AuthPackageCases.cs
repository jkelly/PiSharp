// Authored from Pi b4afdddf44e059c67ccf5097e5317d913521b470: auth-login/logout, radius-mcp and llama/client.
// Synthetic stores and transports only; no upstream capture or live credentials.
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Llama;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Packages;
using PiSharp.Cli.Pi;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync152AuthPackageCases() =>
    [
        ("sync152.auth.unknown-logout-and-credential-kind", Sync152Logout),
        ("sync152.auth.radius-global-plan-and-shell-login", Sync152Radius),
        ("sync152.auth.catalog-refresh-diagnostics", Sync152LoginRefresh),
        ("sync152.llama.network-detail-and-cancellation", Sync152LlamaConnection),
        ("sync152.mcp.command-scoped-provider-token-and-json", Sync152McpProvider),
        ("sync152.packages.oauth-refresh-warning-classification", Sync152CatalogFailure),
    ];

    private static async Task Sync152Logout()
    {
        using var sandbox = new Sandbox("sync152-logout");
        using var output = new StringWriter(); using var errors = new StringWriter();
        var host = sandbox.Host(output, errors, null);
        var store = new AuthJsonCredentialStore(host.LiveRuntime.AuthPath!);
        await store.WriteApiKeyAsync("extension-z", "synthetic", null, default);
        await store.WriteApiKeyAsync("extension-a", "synthetic", null, default);
        Equal(1, await PiAuthCommand.RunAsync(["auth", "logout", "missing\u001b[31m"], host, default), "unknown logout fails");
        Check(!errors.ToString().Contains('\u001b') && errors.ToString().Contains("Stored credentials: extension-a, extension-z", StringComparison.Ordinal), "sanitized reference and sorted stored ids");
        output.GetStringBuilder().Clear();
        Equal(0, await PiAuthCommand.RunAsync(["auth", "logout", "extension-z"], host, default), "extension id can log out");
        Check(output.ToString().Contains("Removed stored API key for extension-z.", StringComparison.Ordinal), "API key wording");
        Check(await store.ReadEntryAsync("extension-z", default) is null && await store.ReadEntryAsync("extension-a", default) is not null, "only selected key removed");
        output.GetStringBuilder().Clear();
        Equal(0, await PiAuthCommand.RunAsync(["auth", "logout", "Anthropic"], host, default), "known provider without credential");
        Check(output.ToString().Contains("No stored credentials for Anthropic.", StringComparison.Ordinal), "known provider absence succeeds");
    }

    private static async Task Sync152Radius()
    {
        using var sandbox = new Sandbox("sync152-radius");
        var path = Path.Combine(sandbox.AgentDir, "mcp.json");
        sandbox.Write(path, """{"keep":true,"mcpServers":{"radius":{"command":"other"},"radius_mcp":{"command":"other"}}}""");
        sandbox.Write(Path.Combine(sandbox.Cwd, ".pi", "mcp.json"), """{"mcpServers":{"existing":{"url":"https://radius.pi.dev/mcp","auth":{"provider":"radius"}}}}""");
        var plan = RadiusMcpServer.Plan("radius", sandbox.AgentDir, sandbox.Cwd)!;
        Equal("radius-mcp-2", plan.Name, "namespace collision avoids dash and underscore aliases");
        Check(!plan.Apply(), "new global server");
        Check(RadiusMcpServer.Plan("radius", sandbox.AgentDir, sandbox.Cwd) is null, "idempotent matching login");
        Check(JsonNode.Parse(File.ReadAllText(path))!["keep"]!.GetValue<bool>(), "unrelated document data preserved");
        sandbox.Write(path, """{"mcpServers":{"custom":{"url":"https://radius.pi.dev/mcp///","headers":{"X-Test":"one"},"exposure":"direct","oauth":{"clientId":"old"}}}}""");
        plan = RadiusMcpServer.Plan("radius", sandbox.AgentDir, sandbox.Cwd)!;
        Equal("custom", plan.Name, "existing matching URL retained");
        Check(plan.Apply(), "existing server replaced");
        var config = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["custom"]!;
        Equal("one", config["headers"]!["X-Test"]!.GetValue<string>(), "headers preserved");
        Equal("direct", config["exposure"]!.GetValue<string>(), "exposure preserved");
        Check(config["oauth"] is null && config["auth"]!["provider"]!.GetValue<string>() == "radius", "provider login replaces MCP OAuth");

        File.Delete(path);
        using var output = new StringWriter(); using var errors = new StringWriter();
        var host = sandbox.Host(output, errors, null, interactive: true) with { Stdin = new StringReader("synthetic-radius-key\n") };
        Equal(0, await PiAuthCommand.RunAsync(["auth", "login", "radius", "--api-key"], host, default), "shell Radius login");
        Check(File.Exists(path) && output.ToString().Contains("Added MCP server \"radius\"", StringComparison.Ordinal), "shell applies same plan");
    }

    private static async Task Sync152LoginRefresh()
    {
        using var output = new StringWriter();
        await PiAuthCommand.RefreshLoginCatalogAsync("synthetic", "Synthetic", output,
            _ => Task.FromResult<IReadOnlyDictionary<string, Exception>>(new Dictionary<string, Exception> { ["synthetic"] = new IOException("offline") }), default);
        Check(output.ToString().Contains("could not be refreshed; pisharp retries on next start.", StringComparison.Ordinal), "per-provider failure diagnostic");
        output.GetStringBuilder().Clear();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await PiAuthCommand.RefreshLoginCatalogAsync("synthetic", "Synthetic", output,
            token => Task.FromCanceled<IReadOnlyDictionary<string, Exception>>(token), cancelled.Token);
        Check(output.ToString().Contains("Skipped the Synthetic model catalog refresh", StringComparison.Ordinal), "post-save Ctrl+C skips refresh");
        output.GetStringBuilder().Clear();
        await PiAuthCommand.RefreshLoginCatalogAsync("synthetic", "Synthetic", output, async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new Dictionary<string, Exception>();
        }, default, TimeSpan.FromMilliseconds(10));
        Check(output.ToString().Contains("refresh timed out", StringComparison.Ordinal), "bounded timeout diagnostic");
    }

    private sealed class Sync152NetworkFailure(bool cancel = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => cancel
            ? Task.FromCanceled<HttpResponseMessage>(token)
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("fetch failed", new SocketException((int)SocketError.ConnectionRefused)));
    }

    private static async Task Sync152LlamaConnection()
    {
        using var http = new HttpClient(new Sync152NetworkFailure());
        var client = new LlamaClient("http://127.0.0.1:43210/v1", http: http);
        foreach (var request in new Func<Task>[] { async () => _ = await client.ListAsync(), () => client.WatchAsync(_ => { }, default) })
        {
            try { await request(); throw new InvalidOperationException("missing connection error"); }
            catch (LlamaConnectionException error)
            {
                Check(error.Message.StartsWith("Could not reach llama.cpp server at http://127.0.0.1:43210: ", StringComparison.Ordinal), "server URL in error");
                Check(error.InnerException is HttpRequestException { InnerException: SocketException } && !error.Message.EndsWith("fetch failed", StringComparison.Ordinal), "network cause preserved");
            }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var cancelledHttp = new HttpClient(new Sync152NetworkFailure(cancel: true));
        try { await new LlamaClient("http://local.test", http: cancelledHttp).ListAsync(token: cancelled.Token); throw new InvalidOperationException("missing cancellation"); }
        catch (OperationCanceledException) { }
    }

    private static async Task Sync152McpProvider()
    {
        using var sandbox = new Sandbox("sync152-mcp-provider");
        await new AuthJsonCredentialStore(Path.Combine(sandbox.AgentDir, "auth.json")).WriteApiKeyAsync("radius", "only-this-agent", null, default);
        var options = new McpCommandOptions(sandbox.Cwd, sandbox.AgentDir) { Environment = _ => null };
        Equal("only-this-agent", await options.CreateSessionHost().ProviderToken!("radius", default), "command agent auth path");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "mcp.json"), """{"mcpServers":{"radius":{"enabled":false,"url":"https://radius.pi.dev/mcp","auth":{"provider":"radius"}}}}""");
        using var output = new StringWriter(); using var errors = new StringWriter();
        Equal(0, await McpCommand.RunAsync(["list", "--json"], output, errors, options), "list disabled provider server");
        Equal("radius", JsonNode.Parse(output.ToString())!["servers"]![0]!["authProvider"]!.GetValue<string>(), "JSON authProvider field");
    }

    private static async Task Sync152CatalogFailure()
    {
        Check(PiPackageCommands.IsStaleCatalogLogin(new OAuthLifecycleException(OAuthLifecycleFailure.Refresh)), "refresh is stale login");
        Check(PiPackageCommands.IsStaleCatalogLogin(new OAuthLifecycleException(OAuthLifecycleFailure.MinimumValidity)), "short-lived token is stale login");
        foreach (var failure in new[] { OAuthLifecycleFailure.Read, OAuthLifecycleFailure.Modify, OAuthLifecycleFailure.Reauthentication, OAuthLifecycleFailure.CallbackReentry })
            Check(!PiPackageCommands.IsStaleCatalogLogin(new OAuthLifecycleException(failure)), "storage/synchronization remains fatal: " + failure);
        Check(!PiPackageCommands.IsStaleCatalogLogin(new IOException("network failure")), "other catalog error remains fatal");
        using var sandbox = new Sandbox("sync152-catalog-warning");
        using var output = new StringWriter(); using var errors = new StringWriter();
        var runtime = sandbox.Runtime() with { OAuthFlows = (_, _) => new ShellTestOAuth() };
        await new AuthJsonCredentialStore(runtime.AuthPath!).ModifyAsync("openrouter",
            (_, _) => Task.FromResult<OAuthCredentialSnapshot?>(new("expired", "synthetic-refresh", 0)), default);
        var host = sandbox.Host(output, errors, null) with { LiveRuntime = runtime };
        Equal(0, await PiPackageCommands.RunAsync(["update", "--models"], host, default), "stale OAuth warning does not fail other catalogs");
        Check(errors.ToString().Contains("Warning: skipped model catalog for openrouter:", StringComparison.Ordinal) &&
            errors.ToString().Contains("pisharp auth login openrouter", StringComparison.Ordinal), "actionable login hint");
        Check(output.ToString().Contains("Model catalogs refreshed", StringComparison.Ordinal), "other catalogs refresh");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "models-store.json"), "invalid json");
        Equal(1, await PiPackageCommands.RunAsync(["update", "--models"], host, default), "non-OAuth catalog failures stay fatal");
    }
}
