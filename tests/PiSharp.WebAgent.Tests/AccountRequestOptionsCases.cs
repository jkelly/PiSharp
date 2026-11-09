using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.Durable;
using PiSharp.WebAgent.Routing;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> AccountRequestOptionsCases()
    {
        yield return Case("routing.request-options.budgets-main-summary-and-no-leak", async () =>
        {
            await using var host = new RoutingHost("account-budgets");
            var account = await host.AddKeyAsync("anthropic", "fake-budget-key");
            foreach (var summary in new[] { false, true })
            {
                var result = await host.SendAsync("anthropic", "claude-haiku-4-5", account, options: new()
                {
                    Reasoning = "high", ThinkingBudgets = (ChordObject)ChordJson.Parse("{\"high\":2345}"),
                    CacheRetention = summary ? "none" : "short", MaxTokens = summary ? 5000 : null,
                });
                Equal(StopReason.Stop, result.Terminal.Reason, "request succeeded");
                using var body = JsonDocument.Parse(host.Provider.Requests.Last().Body!);
                Equal(2345, body.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32(), "custom budget and thinking capability reach real account route");
            }
            await host.SendAsync("anthropic", "claude-haiku-4-5", account, options: new() { Reasoning = "high" });
            using var baseline = JsonDocument.Parse(host.Provider.Requests.Last().Body!);
            var expected = baseline.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32();
            Check(expected > 2345, "custom budget did not leak into cached route");
            await host.SendAsync("anthropic", "claude-haiku-4-5", account, options: new() { Reasoning = "high", WebsocketConnectTimeoutMs = 30 });
            using var timeoutOnly = JsonDocument.Parse(host.Provider.Requests.Last().Body!);
            Equal(expected, timeoutOnly.RootElement.GetProperty("thinking").GetProperty("budget_tokens").GetInt32(), "timeout-only isolated route retains reasoning");
        });
        yield return Case("routing.request-options.websocket-handshake-deadline", async () =>
        {
            var endpoint = new AccountHandshakeHandler();
            await using var host = new RoutingHost("account-handshake", routingOptions: new() { ProviderHandler = endpoint, CodexTransport = "websocket" });
            var account = await host.AddCodexAsync("fake-account", CodexJwt("fake-account"), "fake-refresh", false);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var model = host.Models.GetModel("openai-codex", host.Catalog.Models("openai-codex")[0].Id)!;
            await host.Models.CompleteSimpleAsync(model, UserTurn("handshake"), new()
            {
                Metadata = ChatMetadata(account, "standard", "handshake-options"), WebsocketConnectTimeoutMs = 30,
            }, deadline.Token);
            Check(endpoint.HandshakeCanceled, "request handshake deadline cancelled fake upgrade");
            Check(endpoint.Elapsed < TimeSpan.FromSeconds(3), "request uses explicit 30ms timeout instead of default");
            Check(!deadline.IsCancellationRequested, "caller deadline did not cause the cancellation");
        });
        yield return Case("routing.request-options.owned-handler-waits-for-stream-and-disposes-once", async () =>
        {
            await using var host = new RoutingHost("account-owned-handler", ownProvider: true);
            var account = await host.AddKeyAsync("openai", "fake-owned-key");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stream = host.SendAsync("openai", "gpt-5", account, options: new()
            {
                WebsocketConnectTimeoutMs = 30,
                OnPayload = async (payload, _, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return payload; },
            });
            await entered.Task;
            var disposing = host.Services.GetRequiredService<AccountRouter>().DisposeAsync().AsTask();
            var concurrentDisposing = host.Services.GetRequiredService<AccountRouter>().DisposeAsync().AsTask();
            Check(!disposing.IsCompleted && !concurrentDisposing.IsCompleted, "all shutdown callers wait for active isolated route");
            Equal(0, host.Provider.DisposalCount, "active request retains owned handler");
            release.SetResult();
            Equal(StopReason.Stop, (await stream).Terminal.Reason, "active request finishes");
            await Task.WhenAll(disposing, concurrentDisposing);
            Equal(1, host.Provider.DisposalCount, "owned default handler disposed once");
            await host.Services.GetRequiredService<AccountRouter>().DisposeAsync();
            Equal(1, host.Provider.DisposalCount, "repeated disposal is idempotent");
        });
        yield return Case("routing.request-options.cached-and-isolated-routes-share-owned-pool", async () =>
        {
            await using var host = new RoutingHost("account-shared-pool", ownProvider: true);
            var account = await host.AddKeyAsync("openai", "fake-pool-key");
            await host.SendAsync("openai", "gpt-5", account);
            await host.SendAsync("openai", "gpt-5", account, options: new() { WebsocketConnectTimeoutMs = 30 });
            Equal(0, host.Provider.DisposalCount, "isolated route leaves generation pool open");
            await host.Services.GetRequiredService<AccountRouter>().DisposeAsync();
            Equal(1, host.Provider.DisposalCount, "shared pool has one owner");
        });
        yield return Case("routing.request-options.shutdown-joins-admitted-generation", async () =>
        {
            await using var host = new RoutingHost("account-admission-close");
            var account = await host.AddKeyAsync("openai", "fake-admission-key");
            var vault = new DelayedAccountVault(host.Secrets);
            await using var router = new AccountRouter((RuntimeModelCatalog)host.Catalog, host.Accounts, vault,
                host.Services.GetRequiredService<CredentialsOptions>(), new RoutingOptions
                { CreateDefaultProviderHandler = () => host.Provider, CodexTransport = "sse" });
            var model = host.Models.GetModel("openai", "gpt-5")!;
            var admitted = router.RouteAsync(model, new() { Metadata = ChatMetadata(account, "standard", "pending-admission") }, default).AsTask();
            await vault.Entered.Task;
            var closing = router.DisposeAsync().AsTask();
            var joining = router.DisposeAsync().AsTask();
            Check(!closing.IsCompleted && !joining.IsCompleted, "shutdown joins the admitted pending generation");
            vault.Release.SetResult();
            try { await admitted; } catch (ObjectDisposedException) { }
            await Task.WhenAll(closing, joining);
            await ThrowsAsync<ObjectDisposedException>(() => router.RouteAsync(model,
                new() { Metadata = ChatMetadata(account, "standard", "after-close") }, default).AsTask(), "no generation admitted after close");
            Equal(0, host.Provider.Requests.Count, "preparation never sent a request");
        });
        yield return Case("routing.request-options.borrowed-handler-remains-open", async () =>
        {
            await using var host = new RoutingHost("account-borrowed-handler");
            var account = await host.AddKeyAsync("openai", "fake-borrowed-key");
            await host.SendAsync("openai", "gpt-5", account);
            await host.SendAsync("openai", "gpt-5", account, options: new() { WebsocketConnectTimeoutMs = 30 });
            await host.Services.GetRequiredService<AccountRouter>().DisposeAsync();
            Equal(0, host.Provider.DisposalCount, "borrowed handler is never disposed by router");
        });
    }

    private sealed class DelayedAccountVault(ICredentialVault inner) : ICredentialVault
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<AccountCredential?> ReadAsync(string accountId, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.ReadAsync(accountId, cancellationToken);
        }
        public IAdmittedOAuthCredentialSource OAuthSource(string accountId) => inner.OAuthSource(accountId);
    }

    private sealed class AccountHandshakeHandler : HttpMessageHandler
    {
        internal bool HandshakeCanceled { get; private set; }
        internal TimeSpan Elapsed { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Headers.Upgrade.Any(value => value.Name.Equals("websocket", StringComparison.OrdinalIgnoreCase)))
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { HandshakeCanceled = true; Elapsed = elapsed.Elapsed; throw; }
            }
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":{\"message\":\"offline handshake probe\"}}", Encoding.UTF8, "application/json") };
        }
    }
}
