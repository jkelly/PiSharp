using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions.Mcp.Authentication;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;

// Upstream: Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/mcp/src/transports/streamable-http.ts (authorizedFetch with the
// send signal), packages/mcp/src/client.ts (sendController aborted once a request settles) and packages/mcp/test/streamable-http.test.ts
// ("does not retry requests that were cancelled while authorization was recovering", #10598). Authored by reading; the fake server
// answers in process.
internal static partial class Program
{
    private static readonly Uri DispatchEndpoint = new("https://mcp.example.test/mcp");

    /// <summary>A streamable HTTP server that rejects tools/call with the old token and records the tools it executed.</summary>
    private sealed class DispatchServer : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<string> Executed = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            using var message = JsonDocument.Parse(body);
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            var method = root.GetProperty("method").GetString();
            if (method == "tools/call" && request.Headers.Authorization?.Parameter == "old")
            {
                var denied = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                denied.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer");
                return denied;
            }
            if (method == "tools/call") Executed.Enqueue(root.GetProperty("params").GetProperty("name").GetString()!);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]}}",
                    Encoding.UTF8, "application/json")
            };
        }
    }

    // A request cancelled or timed out while it waits for a shared authorization refresh is not sent afterwards; the refresh itself
    // is not aborted, and the request that is still wanted is sent once with the new token.
    private static async Task NoDispatchAfterSettle()
    {
        var token = "old";
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? refresh = null; var refreshes = 0; var waiting = 0; var gate = new object();
        var authentication = new McpAdmittedHttpAuthentication(_ => ValueTask.FromResult<string?>(Volatile.Read(ref token)), async (_, cancellation) =>
        {
            Task shared;
            lock (gate)
            {
                if (refresh is null) { refreshes++; refresh = releaseRefresh.Task.ContinueWith(_ => Volatile.Write(ref token, "new"), TaskScheduler.Default); }
                shared = refresh;
                if (++waiting == 3) allWaiting.TrySetResult();
            }
            // Each request waits for the one shared refresh; giving up waiting does not abort it for the others.
            await shared.WaitAsync(cancellation);
        });
        var server = new DispatchServer();
        using var client = new HttpClient(server, false);
        var entry = new McpServerEntry("docs", McpConfigurationReader.Validate("docs", JsonData.Parse("{\"url\":\"https://mcp.example.test/mcp\"}").Value).Config!,
            "synthetic.json", McpConfigurationScope.Global);
        var options = new McpRuntimeOptions(1, "1.2.0");
        var factory = AdmittedMcpHttpChannelFactory.Create(entry, new(DispatchEndpoint, ImmutableDictionary<string, string>.Empty, new(false, false)), client, options,
            authentication: authentication);
        var channel = await factory(entry, CancellationToken.None);
        try
        {
            await channel.StartAsync(CancellationToken.None);
            JsonData Call(string name) => JsonData.Parse("{\"name\":\"" + name + "\",\"arguments\":{}}");
            using var abort = new CancellationTokenSource();
            var aborted = channel.RequestAsync("tools/call", Call("aborted"), new(60_000), abort.Token).AsTask();
            var timedOut = channel.RequestAsync("tools/call", Call("timed-out"), new(200), CancellationToken.None).AsTask();
            var kept = channel.RequestAsync("tools/call", Call("kept"), new(60_000), CancellationToken.None).AsTask();
            await allWaiting.Task.WaitAsync(TimeSpan.FromSeconds(30));
            abort.Cancel();
            var abortedFailure = await Failure(aborted);
            Check(abortedFailure is OperationCanceledException || Find<OperationCanceledException>(abortedFailure) is not null, "aborted: " + abortedFailure);
            Check(Find<McpRequestTimeoutException>(await Failure(timedOut)) is not null, "timed out");
            releaseRefresh.TrySetResult();
            var result = await kept.WaitAsync(TimeSpan.FromSeconds(30));
            Equal("hello", result.Value.GetProperty("content")[0].GetProperty("text").GetString());
            Equal("kept", string.Join(",", server.Executed));
            Equal(1, refreshes);
        }
        finally { releaseRefresh.TrySetResult(); await channel.CloseAsync(); }
    }
}
