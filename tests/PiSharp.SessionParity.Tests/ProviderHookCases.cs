using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Extensions;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Events;
using PiSharp.Extensions.Runtime;
using static EventFixture;

// Pi v1.1.0 packages/coding-agent/src/core/sdk.ts (transformHeaders, onPayload, onResponse, onProviderStreamEvent),
// core/extensions/runner.ts (emitBeforeProviderRequest, emitBeforeProviderHeaders, emitCacheWarmingDecision, withUIPrompt) and
// core/session-manager.ts (appendUsage), by reading.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> ProviderHookCases() =>
    [
        Case("hooks.provider-request-payload-headers-response-and-stream-events", ProviderHttpHooks),
        Case("hooks.reduce-event-last-result-wins-and-failures-continue", ReduceEventOrder),
        Case("hooks.ui-prompt-start-end-once-for-nested-prompts", UiPromptEvents),
        Case("hooks.ui-prompt-wraps-terminal-component-scopes-and-custom", UiPromptCustomComponents),
        Case("events.usage-entry-shape-and-entry-appended", UsageEntry),
    ];

    private sealed class FakeProvider : HttpMessageHandler
    {
        internal string? Body; internal HttpRequestMessage? Request;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Request = request; Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "data: {\"type\":\"delta\",\"text\":\"a\"}\n\nevent: x\ndata: {\"type\":\"done\"}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") };
            response.Headers.TryAddWithoutValidation("X-Request-Id", "req-1");
            return response;
        }
    }

    private static async Task ProviderHttpHooks()
    {
        await using var f = await CreateAsync(); var seen = new List<string>();
        await f.Activate(api =>
        {
            var handlers = (IExtensionEventHandlerRegistry)api;
            handlers.RegisterEventHandler(new("payload", "before_provider_request", (value, _, _) =>
            {
                seen.Add(value.ToString());
                var payload = System.Text.Json.Nodes.JsonNode.Parse(value.Value.GetProperty("payload").GetRawText())!.AsObject(); payload["max_tokens"] = 7;
                return ValueTask.FromResult<JsonData?>(JsonData.Parse(payload.ToJsonString()));
            }));
            handlers.RegisterEventHandler(new("headers", "before_provider_headers", (value, _, _) =>
            {
                var headers = System.Text.Json.Nodes.JsonNode.Parse(value.Value.GetProperty("headers").GetRawText())!.AsObject();
                headers["x-trace"] = "t-1"; headers["x-remove"] = null;
                return ValueTask.FromResult<JsonData?>(JsonData.Parse(headers.ToJsonString()));
            }));
            api.Observe(new("response", "after_provider_response", (value, _, _) => { lock (seen) seen.Add(value.ToString()); return ValueTask.CompletedTask; }));
            api.Observe(new("stream", "provider_stream_event", (value, _, _) => { lock (seen) seen.Add(value.ToString()); return ValueTask.CompletedTask; }));
        });
        var provider = new FakeProvider();
        using var client = new HttpClient(new ExtensionProviderHttpHandler(provider, f.Registry, f.Registry.CaptureSnapshot(), Model, f.Report));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://provider.invalid/v1/messages")
        { Content = new StringContent("""{"model":"m","max_tokens":1024}""", Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("x-remove", "gone"); request.Headers.TryAddWithoutValidation("authorization", "Bearer k");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var text = await response.Content.ReadAsStringAsync();
        Equal("""{"type":"before_provider_request","payload":{"model":"m","max_tokens":1024}}""", seen[0], "before_provider_request event");
        Equal("""{"model":"m","max_tokens":7}""", provider.Body, "replaced payload");
        Check(provider.Request!.Headers.GetValues("x-trace").Single() == "t-1" && !provider.Request.Headers.Contains("x-remove") &&
            provider.Request.Headers.Authorization?.ToString() == "Bearer k", "Header edits were not applied.");
        Check(seen[1].StartsWith("""{"type":"after_provider_response","status":200,"headers":{""", StringComparison.Ordinal) && seen[1].Contains("\"x-request-id\":\"req-1\"", StringComparison.Ordinal),
            "after_provider_response event: " + seen[1]);
        Check(seen.Skip(2).SequenceEqual([
            """{"data":{"type":"delta","text":"a"},"type":"provider_stream_event","provider":"fixture","api":"openai-responses","model":"parity-events"}""",
            """{"data":{"type":"done"},"type":"provider_stream_event","provider":"fixture","api":"openai-responses","model":"parity-events"}"""]),
            "provider_stream_event records: " + string.Join("\n", seen.Skip(2)));
        Check(text.StartsWith("data: {\"type\":\"delta\"", StringComparison.Ordinal) && text.EndsWith("data: [DONE]\n\n", StringComparison.Ordinal), "The SSE body was not passed through unchanged.");
    }

    private static async Task ReduceEventOrder()
    {
        await using var f = await CreateAsync();
        await f.Activate(api =>
        {
            var handlers = (IExtensionEventHandlerRegistry)api;
            handlers.RegisterEventHandler(new("one", "cache_warming_decision", (_, _, _) => ValueTask.FromResult<JsonData?>(JsonData.Parse("""{"action":"stop"}"""))));
            handlers.RegisterEventHandler(new("two", "cache_warming_decision", (_, _, _) => throw new InvalidOperationException("decision failed")));
            handlers.RegisterEventHandler(new("three", "cache_warming_decision", (value, _, _) =>
                ValueTask.FromResult<JsonData?>(value.Value.GetProperty("action").GetString() == "stop" ? JsonData.Parse("""{"action":"warm"}""") : null)));
        });
        var decision = new PiSharp.CodingAgent.Usage.CacheWarmingDecisionEvent(0.01, 0.2, 1, "stop");
        var final = await f.Registry.ReduceEventAsync(f.Registry.CaptureSnapshot(), "cache_warming_decision", decision.ToJson(), (current, result) =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(current.Value.GetRawText())!.AsObject(); node["action"] = result.Value.GetProperty("action").GetString();
            return JsonData.Parse(node.ToJsonString());
        }, f.Report);
        Equal("warm", final.Value.GetProperty("action").GetString(), "final action");
        Check(f.Diagnostics.Single() is { EventName: "cache_warming_decision", RegistrationId: "two", Message: "decision failed" }, "The failing handler was not reported.");
    }

    private sealed class DialogProvider : IExtensionUiProvider
    {
        internal Func<Task>? DuringSelect;
        public IExtensionUiScope OpenScope(IExtensionContext context) => new Scope(this);
        private sealed class Scope(DialogProvider owner) : IExtensionUiScope
        {
            public ExtensionUiCapabilities Capabilities { get; } = new(ExtensionUiMode.Rpc, 1, 1, [ExtensionUiFeature.Select, ExtensionUiFeature.Confirm]);
            public async ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken token = default)
            { if (owner.DuringSelect is { } during) await during(); return ExtensionUiOutcome<string>.FromValue(choices[0]); }
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken token = default) =>
                ValueTask.FromResult(ExtensionUiOutcome<bool>.FromValue(true));
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken token = default) =>
                ValueTask.FromResult(ExtensionUiOutcome<string>.Cancelled());
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken token = default) =>
                ValueTask.FromResult(ExtensionUiOutcome<string>.Cancelled());
            public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken token = default) =>
                ValueTask.FromResult(ExtensionUiOutcome<ExtensionUiPublication>.FromValue(ExtensionUiPublication.Published));
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static async Task UiPromptEvents()
    {
        await using var f = await CreateAsync(); var seen = new List<string>();
        var dialogs = new DialogProvider(); var prompts = new NativeUiPromptEvents(dialogs);
        await f.Activate(api =>
        {
            foreach (var topic in new[] { "ui_prompt_start", "ui_prompt_end" })
                api.Observe(new(topic, topic, (value, _, _) => { lock (seen) seen.Add(value.ToString()); return ValueTask.CompletedTask; }));
        });
        prompts.Bind(f.Registry, f.Registry.CaptureSnapshot(), f.Report);
        var context = new Context();
        await using var scope = prompts.OpenScope(context);
        dialogs.DuringSelect = async () => { await using var nested = prompts.OpenScope(context); await nested.ConfirmAsync("Nested", "ok?"); };
        Equal("a", (await scope.SelectAsync("Pick one", ["a", "b"])).Value, "select value");
        await scope.InputAsync("");
        for (var wait = 0; wait < 200 && seen.Count < 4; wait++) await Task.Delay(10);
        Check(seen.SequenceEqual([
            """{"type":"ui_prompt_start","reason":"ui_prompt","kind":"select","title":"Pick one"}""",
            """{"type":"ui_prompt_end","reason":"ui_prompt","kind":"select","title":"Pick one"}""",
            """{"type":"ui_prompt_start","reason":"ui_prompt","kind":"input"}""",
            """{"type":"ui_prompt_end","reason":"ui_prompt","kind":"input"}"""]), "ui prompt events: " + string.Join("\n", seen));
    }
    // runner.ts wrapUIPromptContext wraps every UI context, including the interactive one: dialogs report ui_prompt_start/end, and
    // custom() is a "custom" prompt (no title) until the component is done. The wrapped scope keeps its capability interfaces.
    private sealed class ComponentProvider(DialogProvider dialogs) : IExtensionUiProvider
    {
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IExtensionUiScope OpenScope(IExtensionContext context) => new Scope(dialogs.OpenScope(context), this);
        private sealed class Scope(IExtensionUiScope inner, ComponentProvider owner) : IExtensionUiScope, IExtensionCustomComponentUi
        {
            public ExtensionUiCapabilities Capabilities => inner.Capabilities;
            public Task OpenCustomComponentAsync(ExtensionCustomComponentCallbacks component, CancellationToken token = default) => owner.Done.Task;
            public Task SignalCustomComponentDoneAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default) { owner.Done.TrySetResult(); return Task.CompletedTask; }
            public Task InvalidateCustomComponentAsync(ExtensionCustomComponentIdentity identity, CancellationToken token = default) => Task.CompletedTask;
            public ValueTask<ExtensionUiOutcome<string>> SelectAsync(string title, ImmutableArray<string> choices, ExtensionUiDialogOptions? options = null, CancellationToken token = default) =>
                inner.SelectAsync(title, choices, options, token);
            public ValueTask<ExtensionUiOutcome<bool>> ConfirmAsync(string title, string message, ExtensionUiDialogOptions? options = null, CancellationToken token = default) =>
                inner.ConfirmAsync(title, message, options, token);
            public ValueTask<ExtensionUiOutcome<string>> InputAsync(string title, string? placeholder = null, ExtensionUiDialogOptions? options = null, CancellationToken token = default) =>
                inner.InputAsync(title, placeholder, options, token);
            public ValueTask<ExtensionUiOutcome<string>> EditorAsync(string title, string? prefill = null, CancellationToken token = default) => inner.EditorAsync(title, prefill, token);
            public ValueTask<ExtensionUiOutcome<ExtensionUiPublication>> PublishAsync(ExtensionUiNotification notification, CancellationToken token = default) =>
                inner.PublishAsync(notification, token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private static async Task UiPromptCustomComponents()
    {
        await using var f = await CreateAsync(); var seen = new List<string>();
        var components = new ComponentProvider(new DialogProvider()); var prompts = new NativeUiPromptEvents(components);
        await f.Activate(api =>
        {
            foreach (var topic in new[] { "ui_prompt_start", "ui_prompt_end" })
                api.Observe(new(topic, topic, (value, _, _) => { lock (seen) seen.Add(value.ToString()); return ValueTask.CompletedTask; }));
        });
        prompts.Bind(f.Registry, f.Registry.CaptureSnapshot(), f.Report);
        await using var scope = prompts.OpenScope(new Context());
        Check(scope is IExtensionCustomComponentUi && scope is not IExtensionTerminalInput && scope is not IExtensionToolComponentUi, "capability interfaces kept");
        await scope.ConfirmAsync("Sure?", "really");
        var open = ((IExtensionCustomComponentUi)scope).OpenCustomComponentAsync(null!);
        for (var wait = 0; wait < 200 && seen.Count < 3; wait++) await Task.Delay(10);
        Check(!open.IsCompleted && seen.Count == 3, "custom prompt open: " + string.Join("\n", seen));
        await ((IExtensionCustomComponentUi)scope).SignalCustomComponentDoneAsync(null!); await open;
        for (var wait = 0; wait < 200 && seen.Count < 4; wait++) await Task.Delay(10);
        Check(seen.SequenceEqual([
            """{"type":"ui_prompt_start","reason":"ui_prompt","kind":"confirm","title":"Sure?"}""",
            """{"type":"ui_prompt_end","reason":"ui_prompt","kind":"confirm","title":"Sure?"}""",
            """{"type":"ui_prompt_start","reason":"ui_prompt","kind":"custom"}""",
            """{"type":"ui_prompt_end","reason":"ui_prompt","kind":"custom"}"""]), "ui prompt events: " + string.Join("\n", seen));
    }

    private sealed class Context : IExtensionContext
    {
        public string OwnerId => "parity"; public long OwnerGeneration => 1;
        public CancellationToken OperationCancellationToken => default; public CancellationToken SessionCancellationToken => default;
        public CancellationToken ExtensionLifetimeCancellationToken => default;
    }

    private static async Task UsageEntry()
    {
        await using var f = await CreateAsync(); f.StartRpc();
        var usage = new TokenUsage(0, 1, 900, 0, 901, new(0, 0.000015m, 0.00027m, 0, 0.000285m));
        var entry = await f.Session.AppendUsageEntryAsync("cache_warm", "anthropic", "claude-x", usage, "extension override");
        var body = entry.WireBody.Value;
        Check(body.EnumerateObject().Select(property => property.Name).SequenceEqual(["type", "id", "parentId", "timestamp", "kind", "provider", "model", "usage", "note"]),
            "usage entry shape: " + body.GetRawText());
        Equal("cache_warm", body.GetProperty("kind").GetString(), "kind");
        Equal("""{"type":"entry_appended","entry":""" + body.GetRawText() + "}", f.Output.Lines().Single(), "entry_appended");
        Check(f.Session.Snapshot.Log.Entries[^1].Id == entry.Id && f.Session.Snapshot.Context.LlmMessages.All(message => message.Role != "usage"), "usage entry placement");
    }
}
