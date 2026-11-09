using System.Collections.Concurrent;
using PiSharp.Contracts;
using PiSharp.Durable;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

#pragma warning disable PISHARP_IDENTITY001
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> AccountPayloadHookCases()
    {
        yield return Case("routing.payload-hooks.concurrent-account-scopes-and-no-leak", async () =>
        {
            await using var host = new RoutingHost("payload-scopes");
            var first = await host.AddKeyAsync("openai", "fake-first-key");
            var second = await host.AddKeyAsync("openai", "fake-second-key");
            var arrivals = 0;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new ConcurrentBag<string>();
            DurableStreamOptions Options(string tag) => new()
            {
                Identity = new PiSharp.AI.ClientIdentity { UserAgent = PiSharp.AI.ClientUserAgent.Custom(tag + "-agent/1") },
                OnPayload = async (payload, model, signal) =>
                {
                    calls.Add(tag);
                    Equal("gpt-5", ((PiSharp.Durable.Chord.JsonStringValue)((ChordObject)payload)["model"]).Value, "post-shaping provider body");
                    Equal("openai", model.Provider, "hook model");
                    if (Interlocked.Increment(ref arrivals) == 2) gate.SetResult();
                    await gate.Task.WaitAsync(signal);
                    return ((ChordObject)payload).SetItem("scope_tag", tag);
                },
            };
            var results = await Task.WhenAll(host.SendAsync("openai", "gpt-5", first, "first", Options("first")),
                host.SendAsync("openai", "gpt-5", second, "second", Options("second")));
            Check(results.All(value => value.Terminal.Reason == StopReason.Stop), "both requests completed");
            Equal(2, calls.Count, "exactly once per request");
            foreach (var request in host.Provider.Requests)
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(request.Body!)!;
                var tag = body["scope_tag"]!.GetValue<string>();
                Equal("Bearer fake-" + tag + "-key", request.Header("authorization"), "the hook preserves each account credential");
                Equal(tag + "-agent/1", request.Header("user-agent"), "the scoped identity is preserved");
                Check(request.Header("content-type")!.StartsWith("application/json"), "JSON content type is preserved");
            }
            await host.SendAsync("openai", "gpt-5", first, "plain");
            Check(!host.Provider.Requests.Last().Body!.Contains("scope_tag"), "following request has no inherited hook");
        });
        yield return Case("routing.payload-hooks.failures-and-cancellation-do-not-send", async () =>
        {
            await using var host = new RoutingHost("payload-errors");
            var account = await host.AddKeyAsync("openai", "fake-error-key");
            var failed = await host.SendAsync("openai", "gpt-5", account, options: new()
            { OnPayload = (_, _, _) => throw new InvalidOperationException("hook failed") });
            Equal(StopReason.Error, failed.Terminal.Reason, "hook throw is a stream error");
            Check(failed.Error!.Contains("hook failed"), "hook error is surfaced");
            Equal(0, host.Provider.Requests.Count, "exception prevents sending");
            using var cancel = new CancellationTokenSource();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = host.Models.CompleteSimpleAsync(host.Models.GetModel("openai", "gpt-5")!, UserTurn("cancel"), new()
            {
                Metadata = ChatMetadata(account, "standard", "cancel-hook"),
                OnPayload = async (_, _, signal) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, signal); return null; },
            }, cancel.Token);
            await entered.Task;
            cancel.Cancel();
            Equal(StopReason.Aborted, (await task).StopReason, "callback receives cancellation");
            Equal(0, host.Provider.Requests.Count, "cancelled callback prevents sending");
            Equal(StopReason.Stop, (await host.SendAsync("openai", "gpt-5", account)).Terminal.Reason, "failure scope did not leak");
        });
        yield return Case("routing.payload-hooks.codex-compressed-body", async () =>
        {
            await using var host = new RoutingHost("payload-codex");
            var access = CodexJwt("fake-account");
            var account = await host.AddCodexAsync("fake-account", access, "fake-refresh", false);
            var model = host.Catalog.Models("openai-codex")[0].Id;
            var calls = 0;
            var result = await host.SendAsync("openai-codex", model, account, options: new()
            {
                OnPayload = (payload, _, _) => { calls++; return Task.FromResult<ChordJson?>(((ChordObject)payload).SetItem("scope_tag", "codex")); },
            });
            Equal(StopReason.Stop, result.Terminal.Reason, "Codex request completed: " + result.Error);
            Equal(1, calls, "compressed body hook ran once");
            var request = host.Provider.Requests.Single();
            Check(request.Body!.Contains("scope_tag"), "replacement survived zstd serialization");
            Equal("Bearer " + access, request.Header("authorization"), "OAuth bearer retained");
            Equal("fake-account", request.Header("chatgpt-account-id"), "account header retained");
        });
    }
}
