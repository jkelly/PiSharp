// Decision 0011 (run W2, WU6): the routing area in process. RoutingHost composes the core, the credentials area and the routing area (not
// --faux) over a temporary data folder, a fake environment (never this machine's), a fake auth server (FakeOAuth) and a fake provider
// (ProviderFake: every provider request is recorded with its headers and body, and answered from memory with a recorded OpenAI Responses or
// Anthropic Messages stream). Requests go through WebDurableModels.StreamSimple with the metadata.pisharp object a chat's requests carry. No
// request leaves the process: no paid or live provider call, no real key. The cases: the catalog and GetModel, the module in both modes, two
// keys of one provider, metadata never sent, OAuth refreshes per account, expired/removed/unknown/unbound/mismatched accounts refused (never
// another account), 401s, environment accounts and literal keys, an account's base URL, and the chats' account seam.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;
using AuthKind = PiSharp.WebAgent.Contracts.ProviderAuthKind;
using ChordJson = PiSharp.Durable.Chord.JsonValue;
using ChordObject = PiSharp.Durable.Chord.JsonObjectValue;

internal static partial class Program
{
    /// <summary>Answers provider requests from memory (OpenAI Responses and Codex: "Hello world"; Anthropic Messages: "Hello there") and
    /// records each with its headers (lower-case names, content headers included) and body. <see cref="Answer"/> may answer first.</summary>
    internal sealed class ProviderFake : HttpMessageHandler
    {
        public ConcurrentQueue<FakeRequest> Requests { get; } = new();
        public int DisposalCount { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposalCount++;
            base.Dispose(disposing);
        }

        public Func<FakeRequest, HttpResponseMessage?>? Answer { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var header in request.Headers) headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
            if (request.Content is not null)
                foreach (var header in request.Content.Headers) headers[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
            string? body = null;
            if (request.Content is not null)
            {
                // OpenAI Codex sends its SSE request body as a zstd frame.
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                if (headers.TryGetValue("content-encoding", out var encoding) && encoding == "zstd") bytes = PiSharp.AI.Compression.ZstdDecoder.Decompress(bytes);
                body = Encoding.UTF8.GetString(bytes);
            }
            var recorded = new FakeRequest(request.Method.Method, request.RequestUri!, headers, body);
            Requests.Enqueue(recorded);
            if (Answer?.Invoke(recorded) is { } answer) return answer;
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/messages", StringComparison.Ordinal)) return Sse(AnthropicFrames);
            if (path.EndsWith("/responses", StringComparison.Ordinal)) return Sse(ResponsesFrames);
            return FakeOAuth.Json(404, new JsonObject { ["error"] = "no fake provider route for " + recorded.Key });
        }

        /// <summary>Every recorded request as one text (URLs, headers and bodies), to search for what must not be there.</summary>
        public string Text => string.Join("\n", Requests.Select(request =>
            request.Key + "\n" + string.Join("\n", request.Headers.Select(pair => pair.Key + ": " + pair.Value)) + "\n" + request.Body));
    }

    internal static HttpResponseMessage Sse(string frames) =>
        new(HttpStatusCode.OK) { Content = new StringContent(frames, Encoding.UTF8, "text/event-stream") };

    /// <summary>Recorded OpenAI Responses stream: "Hello world" (PiSharp.Durable.Tests' ModelsCases, via Durable.Hosting.Tests' fixtures).</summary>
    internal const string ResponsesFrames =
        "event: response.created\ndata: {\"type\":\"response.created\",\"sequence_number\":0,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"in_progress\",\"output\":[]}}\n\n" +
        "event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"sequence_number\":1,\"output_index\":0,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"in_progress\",\"role\":\"assistant\",\"content\":[]}}\n\n" +
        "event: response.content_part.added\ndata: {\"type\":\"response.content_part.added\",\"sequence_number\":2,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\",\"annotations\":[]}}\n\n" +
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":3,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hello\"}\n\n" +
        "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"sequence_number\":4,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\" world\"}\n\n" +
        "event: response.output_text.done\ndata: {\"type\":\"response.output_text.done\",\"sequence_number\":5,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"text\":\"Hello world\"}\n\n" +
        "event: response.content_part.done\ndata: {\"type\":\"response.content_part.done\",\"sequence_number\":6,\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"Hello world\",\"annotations\":[]}}\n\n" +
        "event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"sequence_number\":7,\"output_index\":0,\"item\":{\"id\":\"msg_1\",\"type\":\"message\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello world\",\"annotations\":[]}]}}\n\n" +
        "event: response.completed\ndata: {\"type\":\"response.completed\",\"sequence_number\":8,\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":12,\"input_tokens_details\":{\"cached_tokens\":0},\"output_tokens\":3,\"output_tokens_details\":{\"reasoning_tokens\":0},\"total_tokens\":15}}}\n\n";

    /// <summary>Recorded Anthropic Messages stream: "Hello there".</summary>
    internal const string AnthropicFrames =
        "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_01\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\",\"content\":[],\"stop_reason\":null,\"usage\":{\"input_tokens\":9,\"output_tokens\":1}}}\n\n" +
        "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}\n\n" +
        "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\" there\"}}\n\n" +
        "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n" +
        "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":2}}\n\n" +
        "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";

    /// <summary>The credentials options of a routing test: <paramref name="environment"/> only, <paramref name="auth"/> as the auth server,
    /// <paramref name="root"/> as the home folder.</summary>
    internal static CredentialsOptions RoutingCredentials(IReadOnlyDictionary<string, string> environment, HttpMessageHandler auth, string root) => new()
    {
        Environment = variable => environment.TryGetValue(variable, out var value) ? value : null,
        FileExists = _ => false,
        Home = root,
        CreateHttp = () => new HttpMessageInvoker(auth, disposeHandler: false),
        OpenAIAuthBaseUrl = FakeOAuth.AuthBase,
        TestTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>The core, the credentials area and the routing area (real routing, not --faux) over fakes.</summary>
    internal sealed class RoutingHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        public RoutingHost(string name, IReadOnlyDictionary<string, string>? environment = null, RoutingOptions? routingOptions = null, bool ownProvider = false)
        {
            Root = Temp(name);
            Environment = new Dictionary<string, string>(environment ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            Options = new WebAgentOptions { DataDirectory = Path.Join(Root, "data") };
            Options.EnsureDirectories();
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(new ListLoggerProvider(Logs)));
            services.AddSingleton(RoutingCredentials(Environment, Auth, Root));
            services.AddSingleton(routingOptions ?? new RoutingOptions
            {
                ProviderHandler = ownProvider ? null : Provider,
                CreateDefaultProviderHandler = ownProvider ? () => Provider : null,
                CodexTransport = "sse",
            });
            services.AddWebAgentCore(Options);
            services.AddCredentialsArea(Options);
            services.AddRoutingArea(Options);
            services.AddWebAgentFallbacks(Options);
            provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebAgentDb>().Migrate();
        }

        public string Root { get; }

        public WebAgentOptions Options { get; }

        public Dictionary<string, string> Environment { get; }

        public FakeOAuth Auth { get; } = new();

        public ProviderFake Provider { get; } = new();

        public ConcurrentQueue<string> Logs { get; } = new();

        public IServiceProvider Services => provider;

        public CredentialVault Vault => provider.GetRequiredService<CredentialVault>();

        public IAccountStore Accounts => provider.GetRequiredService<IAccountStore>();

        public ICredentialVault Secrets => provider.GetRequiredService<ICredentialVault>();

        public IDurableModels Models => provider.GetRequiredService<IDurableModels>();

        public IModelCatalog Catalog => provider.GetRequiredService<IModelCatalog>();

        public string LogText => string.Join("\n", Logs);

        public async Task<string> AddKeyAsync(string providerId, string key, string? label = null, IReadOnlyDictionary<string, string>? settings = null) =>
            (await Accounts.AddApiKeyAsync(new ApiKeyAccountRequest(providerId, key) { Label = label, Settings = settings })).Account.Id;

        /// <summary>A ChatGPT sign-in stored as an account, due for a refresh when <paramref name="dueSoon"/>.</summary>
        public async Task<string> AddCodexAsync(string identity, string access, string refresh, bool dueSoon) =>
            (await Vault.SaveSignInAsync("openai-codex", new OAuthCredentialSnapshot(access, refresh,
                DateTimeOffset.UtcNow.Add(dueSoon ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(1)).ToUnixTimeMilliseconds(),
                new Dictionary<string, string> { ["accountId"] = identity }), identity, new Dictionary<string, string>(), "ChatGPT " + identity, null, default)).Account.Id;

        /// <summary>One request of <paramref name="providerId"/>/<paramref name="modelId"/> with the stream options a chat bound to
        /// <paramref name="account"/> sends (metadata.pisharp with the account, a tier and an operation id); its terminal event and error text.</summary>
        public async Task<(StreamTerminalEvent Terminal, string? Error)> SendAsync(string providerId, string modelId, string? account, string text = "hello",
            DurableStreamOptions? options = null)
        {
            var model = Models.GetModel(providerId, modelId) ?? throw new InvalidOperationException($"{providerId}/{modelId} is not a model");
            options ??= new DurableStreamOptions();
            if (options.Metadata is null) options = options with { Metadata = ChatMetadata(account, "standard", "op-" + Guid.NewGuid().ToString("N")) };
            StreamTerminalEvent? terminal = null;
            await foreach (var value in Models.StreamSimple(model, UserTurn(text), options, CancellationToken.None))
                if (value is StreamTerminalEvent end) terminal = end;
            Check(terminal is not null, "the stream ended with a terminal event");
            return (terminal!, AccountRouter.ErrorText(terminal!.Message));
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteClearPools();
            DeleteTemp(Root);
        }
    }

    /// <summary>The <c>metadata</c> of a chat's request: <c>pisharp</c> with the account, tier and operation id.</summary>
    internal static ChordObject ChatMetadata(string? account, string tier, string op) =>
        ChordObject.Empty.SetItem(SessionStreamMetadata.Key, SessionStreamMetadata.Fields(("account", account), ("tier", tier)).SetItem("op", op));

    internal static ImmutableArray<ChordJson> UserTurn(string text) => [ChordJson.Object([new("role", "user"), new("content", text), new("timestamp", 1)])];

    /// <summary>A model of <paramref name="providerId"/> on <paramref name="api"/> from the real catalog (<paramref name="preferred"/> when listed).</summary>
    internal static string ModelOf(IModelCatalog catalog, string providerId, string api, string? preferred = null)
    {
        var routed = (RuntimeModelCatalog)catalog;
        if (preferred is not null && routed.Find(providerId, preferred) is { Durable.Api: var found } && found == api) return preferred;
        return routed.Models(providerId).First(model => routed.Find(providerId, model.Id)!.Durable.Api == api).Id;
    }

    internal static IEnumerable<(string, Func<Task>)> RoutingCases() =>
    [
        Case("routing.catalog.lists-the-built-in-routable-models", () =>
        {
            var catalog = new RuntimeModelCatalog();
            var ids = catalog.Providers.Select(provider => provider.Id).ToList();
            Check(ids.Count > 10, "many providers: " + string.Join(",", ids));
            Equal("openai-codex", ids[0], "OpenAI Codex comes first");
            Equal("anthropic|openai", string.Join("|", ids.Skip(1).Take(2)), "then Anthropic and OpenAI");
            Check(!ids.Contains("faux") && !ids.Contains("llama.cpp"), "no faux or local CLI providers");
            Equal(ids.Count, ids.Distinct().Count(), "each provider once");
            var featured = ids.Where(RuntimeModelCatalog.FeaturedProviders.Contains).ToList();
            Equal(string.Join(",", RuntimeModelCatalog.FeaturedProviders.Where(ids.Contains)), string.Join(",", featured), "featured providers in their order");
            Check(ids.Skip(featured.Count).SequenceEqual(ids.Skip(featured.Count).OrderBy(id => catalog.FindProvider(id)!.DisplayName, StringComparer.OrdinalIgnoreCase)),
                "the rest by name");
            foreach (var provider in catalog.Providers) Check(catalog.Models(provider.Id).Count > 0, provider.Id + " has models");

            var codex = catalog.FindProvider("openai-codex")!;
            Equal("OpenAI Codex", codex.DisplayName, "codex name");
            Equal("codex", codex.LogoKey, "codex logo");
            Equal(AuthKind.OAuth, codex.AuthKinds.Single(), "Codex signs in only");
            Equal(BillingKind.Subscription, codex.Billing, "a subscription");
            var anthropic = catalog.FindProvider("anthropic")!;
            Check(!anthropic.AuthKinds.Contains(AuthKind.OAuth) && !anthropic.AuthKinds.Contains(AuthKind.DeviceCode), "Anthropic has no subscription sign-in");
            Check(anthropic.AuthKinds[0] == AuthKind.ApiKey && anthropic.AuthKinds.Contains(AuthKind.Environment), "Anthropic: API key, environment");
            Equal(BillingKind.Metered, anthropic.Billing, "metered");
            var copilot = catalog.FindProvider("github-copilot")!;
            Equal(AuthKind.DeviceCode, copilot.AuthKinds[0], "Copilot signs in with a device code");
            Check(!copilot.AuthKinds.Contains(AuthKind.ApiKey), "no Copilot API key");
            Check(catalog.FindProvider("amazon-bedrock")!.AuthKinds.Contains(AuthKind.Ambient), "Bedrock ambient credentials");
            foreach (var provider in catalog.Providers.Where(provider => provider.Id is not ("openai-codex" or "github-copilot")))
                Check(!provider.AuthKinds.Contains(AuthKind.OAuth) && !provider.AuthKinds.Contains(AuthKind.DeviceCode), provider.Id + " offers no sign-in here");

            var openai = catalog.Models("openai");
            var gpt = catalog.FindModel("openai", "gpt-4.1")!;
            Check(gpt is { ContextWindow: > 0, MaxTokens: > 0 } && gpt.Cost is { Input: > 0, Output: > 0 }, "limits and price from the catalog");
            Check(openai.Any(model => model.Reasoning && model.ThinkingLevels.Count > 1 && model.ThinkingLevels[0] == "off"), "reasoning models list their levels");
            Check(openai.Any(model => model.Input.Contains("image")), "image input is listed");
            Equal(null, catalog.FindModel("openai", "no-such-model"), "unknown model");
            Equal(0, catalog.Models("no-such-provider").Count, "unknown provider");
            Equal(null, catalog.FindProvider("faux"), "no faux provider");
        }),

        Case("routing.models.every-catalog-model-resolves-through-get-model", async () =>
        {
            await using var host = new RoutingHost("route-models");
            Check(host.Catalog is RuntimeModelCatalog, "the real catalog is registered");
            Check(host.Models is WebDurableModels, "the routed models are registered");
            var count = 0;
            foreach (var provider in host.Catalog.Providers)
                foreach (var model in host.Catalog.Models(provider.Id))
                {
                    var durable = host.Models.GetModel(provider.Id, model.Id);
                    Check(durable is not null, $"{provider.Id}/{model.Id} resolves");
                    Equal(provider.Id + "/" + model.Id + "/" + model.Name, durable!.Provider + "/" + durable.Id + "/" + durable.Name, "the same model");
                    Equal(model.Reasoning, durable.Reasoning, model.Id + " reasoning");
                    Check(durable.Wire is not null && durable.ContextWindow == model.ContextWindow, model.Id + " carries its catalog JSON and limits");
                    count++;
                }
            Check(count > 50, "every model was checked: " + count);
            Equal(null, host.Models.GetModel("faux", "echo"), "no faux model");
            Equal(null, host.Models.GetModel("openai", "no-such-model"), "no unknown model");
            Equal(0, host.Provider.Requests.Count, "listing sent nothing");
            Equal(0, host.Auth.Requests.Count, "nor reached an auth server");
        }),

        Case("routing.module.faux-registers-no-provider-path", async () =>
        {
            var root = Temp("route-module");
            try
            {
                foreach (var faux in new[] { true, false })
                {
                    var options = new WebAgentOptions { DataDirectory = Path.Join(root, faux ? "faux" : "real"), Faux = faux };
                    options.EnsureDirectories();
                    var services = new ServiceCollection().AddLogging();
                    services.AddSingleton(RoutingCredentials(new Dictionary<string, string>(), new FakeOAuth(), root));
                    services.AddWebAgentCore(options);
                    services.AddCredentialsArea(options);
                    services.AddRoutingArea(options);
                    services.AddWebAgentFallbacks(options);
                    await using var built = services.BuildServiceProvider();
                    Check(built.GetRequiredService<IChatAccounts>() is AccountChatAccounts, "the chats bind accounts in every mode");
                    if (faux)
                    {
                        Check(built.GetRequiredService<IModelCatalog>() is FauxModelCatalog, "--faux keeps the faux catalog");
                        Check(built.GetRequiredService<IDurableModels>() is FauxDurableModels, "--faux keeps the faux models");
                        Check(built.GetService<AccountRouter>() is null && built.GetService<RuntimeModelCatalog>() is null && built.GetService<WebDurableModels>() is null,
                            "--faux registers no provider code path");
                    }
                    else
                    {
                        Check(built.GetRequiredService<IModelCatalog>() is RuntimeModelCatalog, "the real catalog replaces the faux one");
                        Check(built.GetRequiredService<IDurableModels>() is WebDurableModels, "the routed models replace the faux ones");
                        Equal(1, built.GetServices<IDurableModels>().Count(), "one models registration");
                        Check(ReferenceEquals(built.GetRequiredService<IModelCatalog>(), built.GetRequiredService<RuntimeModelCatalog>()), "one catalog instance");
                    }
                }
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("routing.accounts.two-keys-of-one-provider-each-request-uses-its-own", async () =>
        {
            await using var host = new RoutingHost("route-two-keys");
            var model = ModelOf(host.Catalog, "openai", "openai-responses", "gpt-4.1");
            var keyA = Planted("sk-proj-a");
            var keyB = Planted("sk-proj-b");
            var a = await host.AddKeyAsync("openai", keyA, "Personal");
            var b = await host.AddKeyAsync("openai", keyB, "Work");
            Equal(a, host.Accounts.Active("openai")!.Id, "the first key is active");

            var results = await Task.WhenAll(host.SendAsync("openai", model, a, "from-a-1"), host.SendAsync("openai", model, b, "from-b-1"));
            Check(results.All(result => result.Terminal is StreamDone && result.Error is null), "both answered: " + results[0].Error + results[1].Error);
            string AuthorizationOf(string text) => host.Provider.Requests.Single(request => request.Body!.Contains(text, StringComparison.Ordinal)).Header("authorization")!;
            Equal("Bearer " + keyA, AuthorizationOf("from-a-1"), "A's request carries A's key");
            Equal("Bearer " + keyB, AuthorizationOf("from-b-1"), "B's request carries B's key");

            await host.Accounts.SetActiveAsync(b);
            await host.SendAsync("openai", model, a, "from-a-2");
            Equal("Bearer " + keyA, AuthorizationOf("from-a-2"), "a request bound to A still uses A after B became active");

            var keyA2 = Planted("sk-proj-a2");
            await host.Accounts.ReplaceApiKeyAsync(a, keyA2);
            await host.SendAsync("openai", model, a, "from-a-3");
            Equal("Bearer " + keyA2, AuthorizationOf("from-a-3"), "a replaced key is used by the next request");
            NoSecret(host.LogText, "the log", keyA, keyB, keyA2);
        }),

        Case("routing.metadata.no-pisharp-metadata-reaches-a-provider", async () =>
        {
            await using var host = new RoutingHost("route-metadata");
            var openai = await host.AddKeyAsync("openai", Planted("sk-proj-"));
            var anthropic = await host.AddKeyAsync("anthropic", Planted("sk-ant-"));
            var tier = "tier" + Planted("x");
            var op = "op" + Planted("x");
            var user = "user" + Planted("x");
            ChordObject Metadata(string account) => ChatMetadata(account, tier, op).SetItem("user_id", user);
            var openaiModel = ModelOf(host.Catalog, "openai", "openai-responses", "gpt-4.1");
            var anthropicModel = ModelOf(host.Catalog, "anthropic", "anthropic-messages", "claude-sonnet-4-5");
            var headers = new Dictionary<string, string> { ["x-pisharp-account"] = openai };
            var sent = new[]
            {
                await host.SendAsync("openai", openaiModel, null, "main", new DurableStreamOptions { Metadata = Metadata(openai), Headers = headers, SessionId = "s-1" }),
                await host.SendAsync("openai", openaiModel, null, "summary", new DurableStreamOptions { Metadata = Metadata(openai), MaxTokens = 500, CacheRetention = "none" }),
                await host.SendAsync("anthropic", anthropicModel, null, "anthropic", new DurableStreamOptions { Metadata = Metadata(anthropic), Reasoning = "low" }),
            };
            Check(sent.All(result => result.Error is null), "all answered: " + string.Join(" | ", sent.Select(result => result.Error)));
            Equal(3, host.Provider.Requests.Count, "three provider requests");
            Check(host.Provider.Requests.Any(request => request.Url.Host == "api.anthropic.com") && host.Provider.Requests.Count(request => request.Url.Host == "api.openai.com") == 2,
                "the summary and main routes of OpenAI and Anthropic's route were used");
            // The client's user-agent is not metadata, so the key is searched for everywhere else; the planted values everywhere.
            var full = host.Provider.Text;
            var withoutAgent = string.Join("\n", host.Provider.Requests.Select(request => request.Key + "\n" + string.Join("\n",
                request.Headers.Where(header => header.Key != "user-agent").Select(header => header.Key + ": " + header.Value)) + "\n" + request.Body));
            foreach (var (value, what, text) in new[] { ("pisharp", "the metadata key", withoutAgent), (openai, "the OpenAI account id", full),
                         (anthropic, "the Anthropic account id", full), (tier, "the tier", full), (op, "the operation id", full) })
            {
                var at = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
                Check(at < 0, what + " never reaches a provider (body or header): ..." + (at < 0 ? "" : text[Math.Max(0, at - 60)..Math.Min(text.Length, at + 60)]));
            }
        }),

        Case("routing.oauth.a-refresh-per-account-never-crosses-accounts", async () =>
        {
            await using var host = new RoutingHost("route-refresh");
            var model = host.Catalog.Models("openai-codex")[0].Id;
            var (refreshA, refreshB) = (Planted("rt-a"), Planted("rt-b"));
            var (newA, newB) = (CodexJwt("acct-a"), CodexJwt("acct-b"));
            var (newRefreshA, newRefreshB) = (Planted("rt-a2"), Planted("rt-b2"));
            host.Auth.On("POST " + FakeOAuth.AuthBase + "/oauth/token", request => request.Form("grant_type") != "refresh_token"
                ? FakeOAuth.Json(400, new JsonObject { ["error"] = "unsupported_grant_type" })
                : request.Form("refresh_token") == refreshA
                    ? FakeOAuth.Json(200, new JsonObject { ["access_token"] = newA, ["refresh_token"] = newRefreshA, ["expires_in"] = 3600 })
                    : request.Form("refresh_token") == refreshB
                        ? FakeOAuth.Json(200, new JsonObject { ["access_token"] = newB, ["refresh_token"] = newRefreshB, ["expires_in"] = 3600 })
                        : FakeOAuth.Json(400, new JsonObject { ["error"] = "invalid_grant" }));
            var a = await host.AddCodexAsync("acct-a", CodexJwt("acct-a"), refreshA, dueSoon: true);
            var b = await host.AddCodexAsync("acct-b", CodexJwt("acct-b"), refreshB, dueSoon: true);

            var results = await Task.WhenAll(host.SendAsync("openai-codex", model, a, "codex-a"), host.SendAsync("openai-codex", model, b, "codex-b"));
            Check(results.All(result => result.Error is null && result.Terminal is StreamDone), "both answered: " + results[0].Error + " | " + results[1].Error);
            var refreshes = host.Auth.Requests.Where(request => request.Form("grant_type") == "refresh_token").Select(request => request.Form("refresh_token")).ToList();
            Equal(2, refreshes.Count, "one refresh per account");
            Check(refreshes.Contains(refreshA) && refreshes.Contains(refreshB), "each with its own refresh token");
            FakeRequest Sent(string text) => host.Provider.Requests.Single(request => request.Body!.Contains(text, StringComparison.Ordinal));
            Equal("Bearer " + newA, Sent("codex-a").Header("authorization"), "A's request uses A's refreshed token");
            Equal("acct-a", Sent("codex-a").Header("chatgpt-account-id"), "and A's ChatGPT account");
            Equal("Bearer " + newB, Sent("codex-b").Header("authorization"), "B's request uses B's refreshed token");
            Equal("acct-b", Sent("codex-b").Header("chatgpt-account-id"), "and B's ChatGPT account");
            Equal(newA, (await host.Secrets.ReadAsync(a))!.OAuth!.Access, "A's refresh is stored in A");
            Equal(newRefreshB, (await host.Secrets.ReadAsync(b))!.OAuth!.Refresh, "B's refresh is stored in B");

            await host.SendAsync("openai-codex", model, a, "codex-a-again");
            Equal(2, host.Auth.Requests.Count, "a fresh token is not refreshed again");
            Equal("Bearer " + newA, Sent("codex-a-again").Header("authorization"), "the stored token is used");
            NoSecret(host.LogText, "the log", refreshA, refreshB, newRefreshA, newRefreshB, newA, newB);
        }),

        Case("routing.expired.a-revoked-sign-in-is-expired-never-another-account", async () =>
        {
            await using var host = new RoutingHost("route-revoked");
            var model = host.Catalog.Models("openai-codex")[0].Id;
            var revoked = Planted("rt-revoked");
            host.Auth.On("POST " + FakeOAuth.AuthBase + "/oauth/token", 400, new JsonObject { ["error"] = "invalid_grant", ["error_description"] = "Refresh token revoked" });
            var a = await host.AddCodexAsync("acct-a", CodexJwt("acct-a"), revoked, dueSoon: true);
            var b = await host.AddCodexAsync("acct-b", CodexJwt("acct-b"), Planted("rt-b"), dueSoon: false);
            await host.Accounts.SetActiveAsync(b);

            var (terminal, error) = await host.SendAsync("openai-codex", model, a, "codex-a");
            Check(terminal is StreamError, "the request failed");
            Equal(AccountRouteErrors.Expired, AccountRouteErrors.Reason(error), "as expired: " + error);
            Check(error!.Contains("OpenAI Codex", StringComparison.Ordinal), "it names the provider: " + error);
            Equal(AccountStatus.Expired, host.Accounts.Get(a)!.Status, "the account is marked expired");
            Equal(AccountStatus.Connected, host.Accounts.Get(b)!.Status, "the other account is untouched");
            Equal(0, host.Provider.Requests.Count, "nothing was sent, with A or with B");
            Equal(1, host.Auth.Requests.Count, "the one refresh, of A");
            NoSecret(error, "the error", revoked, a, b);

            (terminal, error) = await host.SendAsync("openai-codex", model, a, "codex-a-again");
            Equal(AccountRouteErrors.Expired, AccountRouteErrors.Reason(error), "still expired");
            Equal(1, host.Auth.Requests.Count, "an expired account is not refreshed again");
            Equal(0, host.Provider.Requests.Count, "and nothing is sent");

            var network = await host.AddCodexAsync("acct-n", CodexJwt("acct-n"), Planted("rt-n"), dueSoon: true);
            host.Auth.Failure = new HttpRequestException("No route to host");
            (_, error) = await host.SendAsync("openai-codex", model, network, "codex-network");
            Check(error is not null && !AccountRouteErrors.IsAccountError(error) && error.StartsWith("Network error", StringComparison.Ordinal),
                "a refresh that failed on the network is a (retryable) network error: " + error);
            Equal(AccountStatus.Connected, host.Accounts.Get(network)!.Status, "and does not expire the account");
            Equal(0, host.Provider.Requests.Count, "nothing was sent");
        }),

        Case("routing.expired.removed-unknown-unbound-and-mismatched-accounts-are-refused", async () =>
        {
            await using var host = new RoutingHost("route-refused");
            var model = ModelOf(host.Catalog, "openai", "openai-responses", "gpt-4.1");
            var key = Planted("sk-proj-");
            var removed = await host.AddKeyAsync("openai", key, "Gone");
            var other = await host.AddKeyAsync("openai", Planted("sk-proj-"), "Kept");
            var anthropic = await host.AddKeyAsync("anthropic", Planted("sk-ant-"));
            await host.Accounts.RemoveAsync(removed);
            Equal(other, host.Accounts.Active("openai")!.Id, "another account is active now");

            foreach (var (account, reason) in new[] { (removed, AccountRouteErrors.Removed), ("acc-0000000000000000", AccountRouteErrors.Removed),
                         ((string?)null, AccountRouteErrors.Unbound), (anthropic, AccountRouteErrors.Mismatch) })
            {
                var (terminal, error) = await host.SendAsync("openai", model, account, "refused");
                Check(terminal is StreamError { Reason: StopReason.Error }, reason + " is an error");
                Equal(reason, AccountRouteErrors.Reason(error), "the reason: " + error);
                Check(AccountRouteErrors.IsAccountError(error) && AccountRouteErrors.Detail(error) is { Length: > 0 }, "an account error with a sentence");
                Check(error!.Contains("OpenAI", StringComparison.Ordinal), "it names the provider");
                NoSecret(error, "the error", key, removed, other, anthropic, "Gone", "Kept");
            }
            await host.Accounts.MarkExpiredAsync(other, "test");
            Equal(AccountRouteErrors.Expired, AccountRouteErrors.Reason((await host.SendAsync("openai", model, other)).Error), "an expired account");
            Equal(0, host.Provider.Requests.Count, "no request was sent with any account");
            Equal(null, AccountRouteErrors.Reason("429 Too many requests"), "another error has no reason");
        }),

        Case("routing.expired.a-401-expires-the-key-and-errors-never-echo-it", async () =>
        {
            await using var host = new RoutingHost("route-401");
            var model = ModelOf(host.Catalog, "openai", "openai-responses", "gpt-4.1");
            var key = Planted("sk-proj-");
            var a = await host.AddKeyAsync("openai", key);
            host.Provider.Answer = request => FakeOAuth.Json(400, new JsonObject { ["error"] = new JsonObject
                { ["message"] = "Invalid request for key " + request.Header("authorization")!["Bearer ".Length..], ["type"] = "invalid_request_error" } });
            var (_, error) = await host.SendAsync("openai", model, a, "bad-request");
            Check(error is not null && !AccountRouteErrors.IsAccountError(error) && error.Contains("(400)", StringComparison.Ordinal), "a 400 passes through: " + error);
            Check(error!.Contains("[redacted]", StringComparison.Ordinal), "with the echoed key redacted: " + error);
            NoSecret(error, "the error", key);
            Equal(AccountStatus.Connected, host.Accounts.Get(a)!.Status, "a 400 does not expire the account");

            host.Provider.Answer = request => FakeOAuth.Json(401, new JsonObject { ["error"] = new JsonObject
                { ["message"] = "Incorrect API key provided: " + request.Header("authorization")!["Bearer ".Length..], ["type"] = "invalid_request_error", ["code"] = "invalid_api_key" } });
            (_, error) = await host.SendAsync("openai", model, a, "unauthorized");
            Equal(AccountRouteErrors.Expired, AccountRouteErrors.Reason(error), "a 401 is expired: " + error);
            Equal(AccountStatus.Expired, host.Accounts.Get(a)!.Status, "and marks the account expired");
            NoSecret(error!, "the error", key);
            var sent = host.Provider.Requests.Count;
            (_, error) = await host.SendAsync("openai", model, a, "after-401");
            Equal(AccountRouteErrors.Expired, AccountRouteErrors.Reason(error), "still expired");
            Equal(sent, host.Provider.Requests.Count, "nothing more is sent");
            await host.Accounts.ReplaceApiKeyAsync(a, Planted("sk-proj-"));
            host.Provider.Answer = null;
            (_, error) = await host.SendAsync("openai", model, a, "replaced");
            Equal(null, error, "a replaced key connects the account again");

            var anthropicKey = Planted("sk-ant-");
            var b = await host.AddKeyAsync("anthropic", anthropicKey);
            host.Provider.Answer = request => request.Url.Host == "api.anthropic.com" ? FakeOAuth.Json(401, new JsonObject
                { ["type"] = "error", ["error"] = new JsonObject { ["type"] = "authentication_error", ["message"] = "invalid x-api-key" } }) : null;
            (_, error) = await host.SendAsync("anthropic", ModelOf(host.Catalog, "anthropic", "anthropic-messages", "claude-sonnet-4-5"), b, "anthropic-401");
            Equal(AccountRouteErrors.Expired, AccountRouteErrors.Reason(error), "Anthropic's 401 is expired too: " + error);
            Equal(AccountStatus.Expired, host.Accounts.Get(b)!.Status, "and marks that account expired");
            Equal(AccountStatus.Connected, host.Accounts.Get(a)!.Status, "and no other");
            NoSecret(error!, "the error", anthropicKey);
        }),

        Case("routing.environment.accounts-read-only-their-own-credential", async () =>
        {
            var envKey = Planted("sk-env-");
            var home = "C:\\Users\\" + Planted("home");
            await using var host = new RoutingHost("route-env", new Dictionary<string, string>
            {
                ["OPENAI_API_KEY"] = envKey, ["ANTHROPIC_API_KEY"] = Planted("sk-ant-env-"), ["HOME"] = home, ["USERPROFILE"] = home,
            });
            var model = ModelOf(host.Catalog, "openai", "openai-responses", "gpt-4.1");
            var environment = host.Accounts.List("openai").Single(account => account.Kind == AccountKind.Environment);
            Equal(CredentialVault.EnvironmentId("openai", "OPENAI_API_KEY"), environment.Id, "the variable is an account");
            var (_, error) = await host.SendAsync("openai", model, environment.Id, "from-env");
            Equal(null, error, "the environment account answers");
            Equal("Bearer " + envKey, host.Provider.Requests.Single().Header("authorization"), "with its variable");

            // A stored account without a key never borrows the variable (nor any other account's key).
            var settingsOnly = (await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("openai", null)
                { Settings = new Dictionary<string, string> { ["OPENAI_ORG"] = "org-1" } })).Account.Id;
            (_, error) = await host.SendAsync("openai", model, settingsOnly, "no-key");
            Equal(AccountRouteErrors.Unavailable, AccountRouteErrors.Reason(error), "no key, no request: " + error);
            Equal(1, host.Provider.Requests.Count, "nothing was sent with the variable");

            // Keys are sent as typed: no command, no variable is ever resolved from them.
            var literal = "!sk$HOME${USERPROFILE}$$" + Planted("x");
            var a = await host.AddKeyAsync("openai", literal);
            (_, error) = await host.SendAsync("openai", model, a, "literal-openai");
            Equal(null, error, "a key that looks like a command answers");
            Equal("Bearer " + literal, host.Provider.Requests.Single(request => request.Body!.Contains("literal-openai", StringComparison.Ordinal)).Header("authorization"),
                "the OpenAI key is sent as typed");
            var anthropicKey = "$HOME!" + Planted("sk-ant-");
            var b = await host.AddKeyAsync("anthropic", anthropicKey);
            (_, error) = await host.SendAsync("anthropic", ModelOf(host.Catalog, "anthropic", "anthropic-messages", "claude-sonnet-4-5"), b, "literal-anthropic");
            Equal(null, error, "the Anthropic key answers");
            Equal(anthropicKey, host.Provider.Requests.Single(request => request.Body!.Contains("literal-anthropic", StringComparison.Ordinal)).Header("x-api-key"),
                "the Anthropic key is sent as typed");
            NoSecret(host.Provider.Text, "the provider requests", home);
        }),

        Case("routing.accounts.an-account-base-url-is-where-its-requests-go", async () =>
        {
            await using var host = new RoutingHost("route-base-url");
            var model = ModelOf(host.Catalog, "openai", "openai-responses", "gpt-4.1");
            var proxied = await host.AddKeyAsync("openai", Planted("sk-proj-"), "Proxy",
                new Dictionary<string, string> { ["baseUrl"] = "https://proxy.example.test/v1" });
            var direct = await host.AddKeyAsync("openai", Planted("sk-proj-"), "Direct");
            await host.SendAsync("openai", model, proxied, "via-proxy");
            await host.SendAsync("openai", model, direct, "direct");
            FakeRequest Sent(string text) => host.Provider.Requests.Single(request => request.Body!.Contains(text, StringComparison.Ordinal));
            Equal("https://proxy.example.test/v1/responses", Sent("via-proxy").Url.GetLeftPart(UriPartial.Path), "the account's base URL");
            Equal("https://api.openai.com/v1/responses", Sent("direct").Url.GetLeftPart(UriPartial.Path), "another account keeps the provider's");
        }),

        Case("routing.chat-accounts.bind-the-active-account-and-find-a-chats-own", async () =>
        {
            await using var host = new RoutingHost("route-chat-accounts");
            var accounts = host.Services.GetRequiredService<IChatAccounts>();
            Check(accounts is AccountChatAccounts, "routing registers the chats' accounts");
            Equal(null, await accounts.BindAsync("openai"), "no account yet");
            var a = await host.AddKeyAsync("openai", Planted("sk-proj-"), "Personal");
            var b = await host.AddKeyAsync("openai", Planted("sk-proj-"), "Work");
            Equal(new ChatAccount(a, "Personal"), await accounts.BindAsync("openai"), "the active account");
            await host.Accounts.SetActiveAsync(b);
            Equal(new ChatAccount(b, "Work"), await accounts.BindAsync("openai"), "a new chat binds the new active account");
            Equal(new ChatAccount(a, "Personal"), await accounts.FindAsync("openai", a), "a chat's own account is still found");
            Equal(null, await accounts.FindAsync("anthropic", a), "not as another provider's");
            Equal(null, await accounts.FindAsync("openai", "acc-0000000000000000"), "an unknown account");
            Equal(null, await accounts.BindAsync("faux"), "the faux provider has none");
        }),
    ];
}
