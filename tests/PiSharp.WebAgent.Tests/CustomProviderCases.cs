// Decision 0011 (run W3, CP): Settings › Custom providers. CustomHost composes the core, the credentials area, the routing area and the
// custom providers area (not --faux unless asked) over a temporary data folder, a fake environment (never this machine's), a fake HTTP
// server for connection tests (FakeOAuth: every model list is answered from memory) and a fake provider (ProviderFake, with a recorded
// OpenAI Chat Completions stream). No request leaves the process: no paid or live provider call, no real key. The cases: add, edit, rename
// and remove round trips in Pi's models.json format; a stored key never in the file, the routes copy, the backup, an answer or a page; $VAR
// keys and header references resolved at request time; the placeholder key; hand-written literal and !command keys not used; loopback
// Free, unpriced Unknown and priced Metered; the catalog and GetModel; model overrides; import and export; an invalid file; the
// connection test only on a click (offline with --faux); --faux routes none; and the page and API in the app (default and empty states).
// W3 fixes: a built-in provider's entry only overrides models (its baseUrl, headers or models never move the account's key, and an import
// leaves them out with a warning); a stored key never follows a custom provider to another server (an import removes it, a save or a test
// needs it typed again).
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.Cli.Models;
using PiSharp.Contracts;
using PiSharp.Durable;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.WebAgent.Chat;
using PiSharp.WebAgent.Contracts;
using PiSharp.WebAgent.Credentials;
using PiSharp.WebAgent.CustomProviders;
using PiSharp.WebAgent.Data;
using PiSharp.WebAgent.Faux;
using PiSharp.WebAgent.Hosting;
using PiSharp.WebAgent.Routing;
using AuthKind = PiSharp.WebAgent.Contracts.ProviderAuthKind;

internal static partial class Program
{
    /// <summary>Recorded OpenAI Chat Completions stream (what LM Studio, Ollama and vLLM answer): "Hello local".</summary>
    internal const string CompletionsFrames =
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" local\"},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\",\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}\n\n" +
        "data: [DONE]\n\n";

    /// <summary>The core and the credentials, routing and custom providers areas over fakes.</summary>
    internal sealed class CustomHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;

        public CustomHost(string name, bool faux = false, IReadOnlyDictionary<string, string>? environment = null, bool offline = false)
        {
            Root = Temp(name);
            Environment = new ConcurrentDictionary<string, string>(environment ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            Options = new WebAgentOptions { DataDirectory = Path.Join(Root, "data"), Faux = faux };
            Options.EnsureDirectories();
            Provider.Answer = request => request.Url.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) ? Sse(CompletionsFrames) : null;
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Debug).AddProvider(new ListLoggerProvider(Logs)));
            services.AddSingleton(new CredentialsOptions
            {
                Environment = variable => Environment.TryGetValue(variable, out var value) ? value : null,
                FileExists = _ => false,
                Home = Root,
                CreateHttp = () => new HttpMessageInvoker(Http, disposeHandler: false),
                OpenAIAuthBaseUrl = FakeOAuth.AuthBase,
                TestTimeout = TimeSpan.FromSeconds(10),
                Offline = offline || faux,
            });
            services.AddSingleton(new RoutingOptions { ProviderHandler = Provider, CodexTransport = "sse" });
            services.AddWebAgentCore(Options);
            services.AddCredentialsArea(Options);
            services.AddRoutingArea(Options);
            services.AddCustomProvidersArea(Options);
            services.AddWebAgentFallbacks(Options);
            provider = services.BuildServiceProvider();
            provider.GetRequiredService<WebAgentDb>().Migrate();
        }

        public string Root { get; }

        public WebAgentOptions Options { get; }

        public string Data => Options.DataDirectory;

        public ConcurrentDictionary<string, string> Environment { get; }

        /// <summary>The fake HTTP server of the connection tests (and every other client request of the credentials area).</summary>
        public FakeOAuth Http { get; } = new();

        public ProviderFake Provider { get; } = new();

        public ConcurrentQueue<string> Logs { get; } = new();

        public IServiceProvider Services => provider;

        public CustomProviderService Service => provider.GetRequiredService<CustomProviderService>();

        public CustomProviderTester Tester => provider.GetRequiredService<CustomProviderTester>();

        public CustomProviderStore Store => provider.GetRequiredService<CustomProviderStore>();

        public IAccountStore Accounts => provider.GetRequiredService<IAccountStore>();

        public IDurableModels Models => provider.GetRequiredService<IDurableModels>();

        public IModelCatalog Catalog => provider.GetRequiredService<IModelCatalog>();

        public string LogText => string.Join("\n", Logs);

        /// <summary>models.json as saved.</summary>
        public string FileText => File.ReadAllText(Store.Path);

        /// <summary>models.json parsed (the header comment stripped).</summary>
        public JsonObject FileJson => JsonNode.Parse(ModelsJsonConfig.StripJsonComments(FileText))!.AsObject();

        /// <summary>One request of <paramref name="providerId"/>/<paramref name="modelId"/> with the metadata a chat sends (no account, as a
        /// custom provider's chat binds none); its terminal event and error text.</summary>
        public async Task<(StreamTerminalEvent Terminal, string? Error)> SendAsync(string providerId, string modelId, string text, string? account = null)
        {
            var model = Models.GetModel(providerId, modelId) ?? throw new InvalidOperationException($"{providerId}/{modelId} is not a model");
            var options = new DurableStreamOptions { Metadata = ChatMetadata(account, "standard", "op-" + Guid.NewGuid().ToString("N")) };
            StreamTerminalEvent? terminal = null;
            await foreach (var value in Models.StreamSimple(model, UserTurn(text), options, CancellationToken.None))
                if (value is StreamTerminalEvent end) terminal = end;
            Check(terminal is not null, "the stream ended with a terminal event");
            return (terminal!, AccountRouter.ErrorText(terminal!.Message));
        }

        /// <summary>The provider request whose body holds <paramref name="text"/>.</summary>
        public FakeRequest RequestWith(string text) => Provider.Requests.Single(request => request.Body?.Contains(text, StringComparison.Ordinal) == true);

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            SqliteClearPools();
            DeleteTemp(Root);
        }
    }

    /// <summary>A provider draft as the editor sends it: <paramref name="models"/> (default one) at <paramref name="baseUrl"/>.</summary>
    internal static CustomProviderDraft ProviderDraft(string id, string baseUrl, CustomProviderKeyDraft? key = null, string api = "openai-completions",
        string? name = null, string? originalId = null, IReadOnlyList<CustomModelDraft>? models = null, IReadOnlyList<CustomHeaderDraft>? headers = null) => new()
    {
        OriginalId = originalId,
        Id = id,
        Name = name ?? id,
        BaseUrl = baseUrl,
        Api = api,
        Key = key ?? new CustomProviderKeyDraft { Source = "none" },
        Headers = headers,
        Models = models ?? [new CustomModelDraft { Id = "qwen3-8b", Name = "Qwen3 8B", ContextWindow = 32768, MaxTokens = 4096 }],
    };

    internal static IEnumerable<(string, Func<Task>)> CustomProviderCases() =>
    [
        Case("custom-providers.store.add-edit-rename-remove-round-trip-in-models-json-format", async () =>
        {
            await using var host = new CustomHost("cp-round-trip");
            Check(!File.Exists(host.Store.Path), "no file before the first save");
            Equal(Path.Join(host.Data, "models.json"), host.Store.Path, "models.json in the data folder");

            var added = await host.Service.SaveAsync(ProviderDraft("lmstudio", "http://localhost:1234/v1", name: "LM Studio (local)", models:
            [
                new CustomModelDraft { Id = "qwen3-8b", Name = "Qwen3 8B", Reasoning = true, ContextWindow = 32768, MaxTokens = 4096,
                    ThinkingLevelMap = new Dictionary<string, string?> { ["high"] = "high", ["xhigh"] = null } },
                new CustomModelDraft { Id = "llava", Images = true },
            ]));
            Equal("lmstudio", added.Id, "the saved id");
            Check(added.Loopback && added.Routed, "a loopback provider, routed");
            var text = host.FileText;
            Equal(null, ModelsJsonConfig.Parse(text, host.Store.Path).Error, "the file is valid models.json");
            var entry = host.FileJson["providers"]!["lmstudio"]!.AsObject();
            Equal("http://localhost:1234/v1", entry["baseUrl"]!.GetValue<string>(), "baseUrl");
            Equal("openai-completions", entry["api"]!.GetValue<string>(), "api");
            Equal("LM Studio (local)", entry["name"]!.GetValue<string>(), "name");
            Check(entry["apiKey"] is null, "no key: apiKey is left out");
            var models = entry["models"]!.AsArray();
            Equal("qwen3-8b|llava", string.Join("|", models.Select(model => model!["id"]!.GetValue<string>())), "the models in order");
            Equal(32768, models[0]!["contextWindow"]!.GetValue<int>(), "contextWindow");
            Equal(128000, models[1]!["contextWindow"]!.GetValue<int>(), "the default context window");
            Equal(16384, models[1]!["maxTokens"]!.GetValue<int>(), "the default max tokens");
            Equal("text,image", string.Join(",", models[1]!["input"]!.AsArray().Select(value => value!.GetValue<string>())), "image input");
            Check(models[0]!["reasoning"]!.GetValue<bool>(), "reasoning");
            Equal("high", models[0]!["thinkingLevelMap"]!["high"]!.GetValue<string>(), "a mapped level");
            Check(models[0]!["thinkingLevelMap"]!.AsObject().ContainsKey("xhigh") && models[0]!["thinkingLevelMap"]!["xhigh"] is null, "an unsupported level is null");
            Equal(0, models[0]!["cost"]!["input"]!.GetValue<double>(), "a local model costs nothing");

            var draft = host.Service.Draft("lmstudio")!;
            Equal("lmstudio", draft.OriginalId, "the draft names the saved id");
            Equal("none", draft.Key!.Source, "the key source");
            Equal(2, draft.Models!.Count, "the draft's models");
            Equal("high", draft.Models[0].ThinkingLevelMap!["high"], "the draft's levels");

            // Edit: a new name, a model added, a limit changed, the levels cleared; an unmanaged field written by hand is kept.
            var document = host.FileJson;
            document["providers"]!["lmstudio"]!["models"]![0]!["samplingParams"] = new JsonObject { ["temperature"] = 0.6 };
            document["providers"]!["lmstudio"]!["promptCache"] = false;
            await File.WriteAllTextAsync(host.Store.Path, document.ToJsonString());
            host.Store.Refresh();
            draft = host.Service.Draft("lmstudio")!;
            var edited = await host.Service.SaveAsync(draft with
            {
                Name = "LM Studio",
                Models = [draft.Models![0] with { ContextWindow = 65536, ThinkingLevelMap = new Dictionary<string, string?>() }, draft.Models[1],
                    new CustomModelDraft { Id = "gemma-3" }],
            });
            Equal("LM Studio", edited.Name, "renamed");
            entry = host.FileJson["providers"]!["lmstudio"]!.AsObject();
            Equal(3, entry["models"]!.AsArray().Count, "a model added");
            Equal(65536, entry["models"]![0]!["contextWindow"]!.GetValue<int>(), "a limit changed");
            Check(entry["models"]![0]!["thinkingLevelMap"] is null, "cleared levels are removed, not restored from the file");
            Equal(0.6, entry["models"]![0]!["samplingParams"]!["temperature"]!.GetValue<double>(), "an unmanaged model field is kept");
            Equal(false, entry["promptCache"]!.GetValue<bool>(), "an unmanaged provider field is kept");
            Check(File.Exists(host.Store.BackupPath), "the previous file is kept as models.json.bak");
            Equal(null, ModelsJsonConfig.Parse(host.FileText, host.Store.Path).Error, "still valid");

            // Rename: the entry keeps its place, the old id is gone.
            await host.Service.SaveAsync(ProviderDraft("ollama", "http://localhost:11434/v1"));
            draft = host.Service.Draft("lmstudio")!;
            await host.Service.SaveAsync(draft with { Id = "lm-local" });
            Equal("lm-local|ollama", string.Join("|", host.FileJson["providers"]!.AsObject().Select(pair => pair.Key)), "renamed in place");
            Equal(null, host.Service.Draft("lmstudio"), "the old id is gone");

            var duplicate = await host.Service.DuplicateAsync("lm-local");
            Equal("lm-local-2", duplicate.Id, "a duplicate gets a free id");
            Check(duplicate.Name.EndsWith("(copy)", StringComparison.Ordinal), "and a copy's name");

            await host.Service.RemoveAsync("lm-local");
            await host.Service.RemoveAsync("lm-local-2");
            await host.Service.RemoveAsync("ollama");
            Equal(0, host.Service.Current.Providers.Count, "every provider removed");
            Equal(0, host.FileJson["providers"]!.AsObject().Count, "the file lists none");
            await ThrowsAsync<KeyNotFoundException>(() => host.Service.RemoveAsync("ollama"), "removing an unknown provider");

            // What a save refuses.
            async Task Refused(CustomProviderDraft bad, string what, string? says = null)
            {
                var error = await ThrowsAsync<ArgumentException>(() => host.Service.SaveAsync(bad), what);
                if (says is not null) Check(error.Message.Contains(says, StringComparison.OrdinalIgnoreCase), what + ": " + error.Message);
            }
            await Refused(ProviderDraft("openai", "https://api.example.test/v1"), "a built-in provider's id", "built-in");
            await Refused(ProviderDraft("faux", "http://localhost:1/v1"), "the faux provider's id");
            await Refused(ProviderDraft("LM Studio", "http://localhost:1234/v1"), "an id that is not a slug");
            await Refused(ProviderDraft("export", "http://localhost:1234/v1"), "a reserved id");
            await Refused(ProviderDraft("lmstudio", "ftp://localhost/v1"), "a base URL that is not http");
            await Refused(ProviderDraft("lmstudio", "https://user:pass@gw.example.test/v1"), "a base URL with a password");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", api: "carrier-pigeon"), "an unknown api");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", models: []), "no models");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", models: [new() { Id = "a" }, new() { Id = "a" }]), "a model listed twice");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", models: [new() { Id = "a", ContextWindow = -5 }]), "a negative context window");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", models: [new() { Id = "a", CostInput = -1 }]), "a negative price");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", new CustomProviderKeyDraft { Source = "command", Variable = "!op read x" }),
                "a !command key", "command");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", new CustomProviderKeyDraft { Source = "env", Variable = "not a name" }),
                "a variable that is not a name");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", new CustomProviderKeyDraft { Source = "stored" }), "a stored key without a key");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", headers: [new("X-Run", "!rm -rf /")]), "a !command header", "!");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", headers: [new("Authorization", "Bearer sk-literal-PLANTED-0000")]),
                "a literal credential header", "$VAR");
            await Refused(ProviderDraft("lmstudio", "http://localhost:1234/v1", headers: [new("X-Split", "a\r\nInjected: 1")]), "a header with a line break");
            Equal(0, host.Service.Current.Providers.Count, "nothing refused was saved");
            Equal(0, host.Http.Requests.Count + host.Provider.Requests.Count, "saving sent nothing");
        }),

        Case("custom-providers.keys.a-stored-key-is-in-the-vault-never-in-the-file-or-an-answer", async () =>
        {
            await using var host = new CustomHost("cp-stored-key");
            var key = Planted("sk-gw-");
            var key2 = Planted("sk-gw2-");
            var saved = await host.Service.SaveAsync(ProviderDraft("gateway", "https://gw.example.test/v1", new CustomProviderKeyDraft { Source = "stored", ApiKey = key },
                name: "Team gateway", models: [new() { Id = "gw-large", CostInput = 2, CostOutput = 8 }]));
            var account = host.Accounts.Active("gateway");
            Check(account is { Kind: AccountKind.ApiKey }, "the key is a vault account of the provider");
            Equal(SecretText.Mask(key), account!.Hint, "masked in the vault's list");
            Check(host.FileJson["providers"]!["gateway"]!["apiKey"] is null, "apiKey is not written");
            Check(saved.Routed, "routed with the stored key");

            var draft = host.Service.Draft("gateway")!;
            Equal("stored", draft.Key!.Source, "the source");
            Equal(SecretText.Mask(key), draft.Key.Hint, "the editor gets the masked hint");
            Equal(null, draft.Key.ApiKey, "never the key");
            Check(!draft.ToString().Contains(key, StringComparison.Ordinal) && !draft.Key.ToString().Contains(key, StringComparison.Ordinal), "a draft's text is redacted");
            var answers = new StringBuilder();
            answers.AppendLine(JsonSerializer.Serialize(draft, CredentialVault.JsonOptions));
            answers.AppendLine(host.Service.Export());
            answers.AppendLine(host.Service.PreviewFile());
            answers.AppendLine(host.Service.Preview(draft).Json);

            // The request carries the stored key, without the chat's metadata; a chat of the provider binds no account.
            var model = host.Models.GetModel("gateway", "gw-large");
            Check(model is { Api: "openai-completions" }, "GetModel resolves the custom model");
            var sent = await host.SendAsync("gateway", "gw-large", "stored-1");
            Check(sent.Terminal is StreamDone && sent.Error is null, "answered: " + sent.Error);
            var request = host.RequestWith("stored-1");
            Equal("https://gw.example.test/v1/chat/completions", request.Url.ToString(), "sent to the provider's base URL");
            Equal("Bearer " + key, request.Header("authorization"), "with the stored key");
            Check(!request.Body!.Contains("pisharp", StringComparison.Ordinal), "metadata.pisharp is stripped");
            Equal(null, await host.Services.GetRequiredService<IChatAccounts>().BindAsync("gateway"), "a custom provider's chat binds no account");

            // A new key replaces the old one; the next request uses it.
            draft = host.Service.Draft("gateway")!;
            await host.Service.SaveAsync(draft with { Key = new CustomProviderKeyDraft { Source = "stored", ApiKey = key2 } });
            Equal(1, host.Accounts.List("gateway").Count, "one account");
            await host.SendAsync("gateway", "gw-large", "stored-2");
            Equal("Bearer " + key2, host.RequestWith("stored-2").Header("authorization"), "the replaced key is used");
            // Saved without a typed key, the stored key is kept.
            await host.Service.SaveAsync(host.Service.Draft("gateway")! with { Name = "Gateway" });
            Equal(SecretText.Mask(key2), host.Service.Draft("gateway")!.Key!.Hint, "a blank key field keeps the stored key");

            // A rename moves the key; switching to no key removes it from the vault.
            await host.Service.SaveAsync(host.Service.Draft("gateway")! with { Id = "gw" });
            Check(host.Accounts.List("gateway").Count == 0 && host.Accounts.Active("gw") is { Kind: AccountKind.ApiKey }, "the key moved with the rename");
            await host.SendAsync("gw", "gw-large", "stored-3");
            Equal("Bearer " + key2, host.RequestWith("stored-3").Header("authorization"), "the renamed provider uses the moved key");
            await host.Service.SaveAsync(host.Service.Draft("gw")! with { Key = new CustomProviderKeyDraft { Source = "none" } });
            Equal(0, host.Accounts.List("gw").Count, "no key: the stored key is removed");
            await host.SendAsync("gw", "gw-large", "stored-4");
            Equal("Bearer " + CustomProviderJson.PlaceholderKey, host.RequestWith("stored-4").Header("authorization"), "the placeholder key is sent");

            await host.Service.SaveAsync(host.Service.Draft("gw")! with { Key = new CustomProviderKeyDraft { Source = "stored", ApiKey = key } });
            await host.Service.RemoveAsync("gw");
            Equal(0, host.Accounts.List("gw").Count, "removing the provider removes its key");

            NoSecret(answers.ToString(), "an answer", key, key2);
            NoSecret(host.LogText, "the log", key, key2);
            foreach (var secret in new[] { key, key2 })
                Check(FilesHolding(host.Data, secret).Count == 0, "a planted key is on disk: " + string.Join(", ", FilesHolding(host.Data, secret)));
            Equal(0, host.Http.Requests.Count, "no connection test ran");
        }),

        Case("custom-providers.keys.env-references-resolve-at-request-time", async () =>
        {
            var key = Planted("sk-env-");
            var token = Planted("tok-");
            await using var host = new CustomHost("cp-env-key", environment: new Dictionary<string, string> { ["GATEWAY_KEY"] = key, ["GW_TOKEN"] = token });
            await host.Service.SaveAsync(ProviderDraft("envgw", "https://env.example.test/v1", new CustomProviderKeyDraft { Source = "env", Variable = "$GATEWAY_KEY" },
                headers: [new("X-Gateway-Token", "Bearer $GW_TOKEN"), new("X-Team", "research")]));
            var entry = host.FileJson["providers"]!["envgw"]!.AsObject();
            Equal("$GATEWAY_KEY", entry["apiKey"]!.GetValue<string>(), "the file holds the reference");
            Equal("Bearer $GW_TOKEN", entry["headers"]!["X-Gateway-Token"]!.GetValue<string>(), "and the header's reference");
            Equal(null, ModelsJsonConfig.Parse(host.FileText, host.Store.Path).Error, "valid models.json");
            var draft = host.Service.Draft("envgw")!;
            Equal("env", draft.Key!.Source, "an environment key");
            Equal("GATEWAY_KEY", draft.Key.Variable, "the variable");
            Equal(true, draft.Key.Set, "which is set");
            Check(host.Service.IsSet("GATEWAY_KEY") && !host.Service.IsSet("NOT_SET_ANYWHERE"), "IsSet");

            var sent = await host.SendAsync("envgw", "qwen3-8b", "env-1");
            Check(sent.Terminal is StreamDone, "answered: " + sent.Error);
            var request = host.RequestWith("env-1");
            Equal("Bearer " + key, request.Header("authorization"), "the variable's value is sent");
            Equal("Bearer " + token, request.Header("x-gateway-token"), "a header reference is resolved");
            Equal("research", request.Header("x-team"), "a plain header is sent");

            var key2 = Planted("sk-env2-");
            host.Environment["GATEWAY_KEY"] = key2;
            await host.SendAsync("envgw", "qwen3-8b", "env-2");
            Equal("Bearer " + key2, host.RequestWith("env-2").Header("authorization"), "read again on the next request");

            host.Environment.TryRemove("GATEWAY_KEY", out _);
            var before = host.Provider.Requests.Count;
            var unset = await host.SendAsync("envgw", "qwen3-8b", "env-3");
            Check(unset.Terminal is not StreamDone, "an unset variable ends the request");
            Check(unset.Error?.Contains("GATEWAY_KEY", StringComparison.Ordinal) == true && unset.Error.Contains("Custom providers", StringComparison.Ordinal),
                "and says which variable, and where to fix it: " + unset.Error);
            Equal(before, host.Provider.Requests.Count, "nothing was sent");

            NoSecret(host.FileText + host.LogText + File.ReadAllText(host.Store.RoutesFile), "the file, the routes copy or the log", key, key2, token);
            NoSecret(JsonSerializer.Serialize(host.Service.Draft("envgw"), CredentialVault.JsonOptions) + host.Service.Export(), "an answer", key, key2, token);
        }),

        Case("custom-providers.keys.hand-written-literal-and-command-keys-are-never-used", async () =>
        {
            await using var host = new CustomHost("cp-hand-written");
            var literal = Planted("sk-hand-");
            var header = Planted("hdr-");
            await File.WriteAllTextAsync(host.Store.Path, $$"""
                // written by hand, with comments and a trailing comma
                {
                  "providers": {
                    "handkey": { "baseUrl": "https://hand.example.test/v1", "api": "openai-completions", "apiKey": "{{literal}}", "models": [{ "id": "m1" }] },
                    "cmdkey": { "baseUrl": "https://cmd.example.test/v1", "api": "openai-completions", "apiKey": "!security find-generic-password -w", "models": [{ "id": "m2" }] },
                    "hdrkey": { "baseUrl": "http://127.0.0.1:8000/v1", "api": "openai-completions", "headers": { "Authorization": "Bearer {{header}}", "X-Run": "!whoami" }, "models": [{ "id": "m3" }] },
                    "fine": { "baseUrl": "http://127.0.0.1:8080/v1", "api": "openai-completions", "models": [{ "id": "m4" }] },
                  },
                }
                """);
            var snapshot = host.Store.Refresh();
            Check(snapshot.Valid, "a hand-written file with comments is valid: " + snapshot.Error);
            Equal(CustomKeySource.Unsupported, snapshot.Find("handkey")!.FileKey, "a literal key is not used");
            Equal(CustomKeySource.Unsupported, snapshot.Find("cmdkey")!.FileKey, "a !command key is not used");
            Check(!snapshot.Find("handkey")!.Routed && !snapshot.Find("cmdkey")!.Routed, "neither is routed");
            Check(snapshot.Find("handkey")!.Problem is { Length: > 0 } problem && !problem.Contains(literal, StringComparison.Ordinal), "the problem is said, without the key");
            Check(snapshot.Find("fine")!.Routed, "a provider without a key is routed");

            Equal(null, host.Catalog.FindProvider("handkey"), "a literal-key provider is not in the catalog");
            Equal(null, host.Catalog.FindProvider("cmdkey"), "nor a !command one");
            Equal(null, host.Models.GetModel("cmdkey", "m2"), "nor its models");
            Check(host.Catalog.FindProvider("fine") is not null, "the key-less one is");
            // The routes copy (in the data folder, never served) has neither the literal key nor a command. A secret-like header the user
            // wrote into their own file stays (hidden in the editor, never shown or exported) and is sent, as Pi sends it.
            var routes = File.ReadAllText(host.Store.RoutesFile);
            NoSecret(routes, "the routes copy", literal, "!security", "!whoami");
            Check(routes.Contains(header, StringComparison.Ordinal), "the user's own header is routed");

            // The page's answers never echo them.
            var draft = host.Service.Draft("handkey")!;
            Check(draft.Key!.Problem is not null && draft.Key.ApiKey is null, "the editor says the key is not used");
            var answers = JsonSerializer.Serialize(draft, CredentialVault.JsonOptions) + JsonSerializer.Serialize(host.Service.Draft("hdrkey"), CredentialVault.JsonOptions) +
                host.Service.Export() + host.Service.PreviewFile();
            NoSecret(answers, "an answer", literal, header);

            // The hidden header is kept by a save that leaves it unchanged; the command header is refused.
            var hdr = host.Service.Draft("hdrkey")!;
            Check(hdr.Headers!.Single(item => item.Name == "Authorization") is { Hidden: true, Value: null }, "a secret literal header is hidden");
            await ThrowsAsync<ArgumentException>(() => host.Service.SaveAsync(hdr), "a save keeping a !command header");
            await host.Service.SaveAsync(hdr with { Headers = [.. hdr.Headers!.Where(item => item.Name != "X-Run")] });
            Check(host.FileText.Contains(header, StringComparison.Ordinal), "the hidden header's value is kept in the user's own file");
            Equal(0, host.Provider.Requests.Count + host.Http.Requests.Count, "nothing was sent, no command was run");
            var sent = await host.SendAsync("hdrkey", "m3", "hand-1");
            Check(sent.Terminal is StreamDone, "answered: " + sent.Error);
            Equal("Bearer " + header, host.RequestWith("hand-1").Header("authorization"), "the hand-written header is sent");
            NoSecret(host.LogText, "the log", literal, header);
        }),

        Case("custom-providers.catalog.custom-models-are-listed-and-billed-loopback-free", async () =>
        {
            await using var host = new CustomHost("cp-catalog");
            var builtIn = new RuntimeModelCatalog();
            await host.Service.SaveAsync(ProviderDraft("lmstudio", "http://localhost:1234/v1", name: "LM Studio"));
            await host.Service.SaveAsync(ProviderDraft("remote", "https://remote.example.test/v1", name: "Remote", models: [new() { Id = "r1" }]));
            await host.Service.SaveAsync(ProviderDraft("priced", "https://priced.example.test/v1", new CustomProviderKeyDraft { Source = "stored", ApiKey = Planted("sk-p-") },
                name: "Priced", api: "anthropic-messages", models: [new() { Id = "p1", CostInput = 3, CostOutput = 15, Reasoning = true }]));

            var catalog = host.Catalog;
            Check(catalog is RuntimeModelCatalog, "the real catalog");
            var ids = catalog.Providers.Select(provider => provider.Id).ToList();
            Equal(builtIn.Providers.Count + 3, ids.Count, "the built-in providers and the three custom ones");
            Equal("lmstudio|remote|priced", string.Join("|", ids.TakeLast(3)), "custom providers after the built-in ones, in the file's order");
            Check(ids.Take(builtIn.Providers.Count).SequenceEqual(builtIn.Providers.Select(provider => provider.Id)), "the built-in ones unchanged");

            var local = catalog.FindProvider("lmstudio")!;
            Equal("LM Studio", local.DisplayName, "the display name");
            Equal("lmstudio", local.LogoKey, "a known logo");
            Equal(BillingKind.Free, local.Billing, "loopback is Free");
            Equal(AuthKind.None, local.AuthKinds.Single(), "no key");
            var localModel = catalog.FindModel("lmstudio", "qwen3-8b")!;
            Check(localModel is { ContextWindow: 32768, MaxTokens: 4096, Name: "Qwen3 8B" }, "limits and name");
            Check(localModel.Cost is { Input: 0, Output: 0 }, "a zero price");

            Equal(BillingKind.Unknown, catalog.FindProvider("remote")!.Billing, "a remote unpriced provider is Unknown");
            Equal(null, catalog.FindModel("remote", "r1")!.Cost, "with no price");
            var priced = catalog.FindProvider("priced")!;
            Equal(BillingKind.Metered, priced.Billing, "a priced provider is Metered");
            Equal(AuthKind.ApiKey, priced.AuthKinds.Single(), "with its stored key");
            Check(catalog.FindModel("priced", "p1")!.Cost is { Input: 3, Output: 15 }, "at its price");
            Check(catalog.FindModel("priced", "p1")!.Reasoning, "reasoning");

            foreach (var provider in new[] { "lmstudio", "remote", "priced" })
                foreach (var model in catalog.Models(provider))
                {
                    var durable = host.Models.GetModel(provider, model.Id);
                    Check(durable is not null && durable.Provider == provider && durable.Id == model.Id, $"{provider}/{model.Id} resolves through GetModel");
                }
            Equal("anthropic-messages", host.Models.GetModel("priced", "p1")!.Api, "the api");

            // A change of the file is picked up; a removed provider leaves the catalog.
            await host.Service.RemoveAsync("remote");
            Equal(null, host.Catalog.FindProvider("remote"), "removed from the catalog");
            Equal(null, host.Models.GetModel("remote", "r1"), "and GetModel");
            Equal(builtIn.Providers.Count + 2, host.Catalog.Providers.Count, "two custom providers left");
            Equal(0, host.Provider.Requests.Count + host.Http.Requests.Count, "listing sent nothing");
        }),

        Case("custom-providers.overrides.change-built-in-models-and-are-removed", async () =>
        {
            await using var host = new CustomHost("cp-overrides");
            var before = host.Catalog.FindModel("openai", "gpt-4.1")!;
            var saved = await host.Service.SaveOverrideAsync("openai", "gpt-4.1", null, "contextWindow", "400000");
            Check(saved is not null, "saved");
            Equal(400000, host.FileJson["providers"]!["openai"]!["modelOverrides"]!["gpt-4.1"]!["contextWindow"]!.GetValue<int>(), "in Pi's modelOverrides");
            Equal(null, ModelsJsonConfig.Parse(host.FileText, host.Store.Path).Error, "valid models.json");
            Equal(400000, host.Catalog.FindModel("openai", "gpt-4.1")!.ContextWindow, "the catalog uses the override");
            Equal(400000, host.Models.GetModel("openai", "gpt-4.1")!.ContextWindow, "and GetModel");
            await host.Service.SaveOverrideAsync("openai", "gpt-4.1", null, "cost", """{"input": 1.5, "output": 6}""");
            Equal(1.5, host.Catalog.FindModel("openai", "gpt-4.1")!.Cost!.Input, "a second field merges in");
            Equal(400000, host.Catalog.FindModel("openai", "gpt-4.1")!.ContextWindow, "the first is kept");
            Equal(1, host.Service.Current.Overrides.Count, "one override");
            Equal(0, host.Service.Current.Providers.Count, "a built-in provider's overrides are not a custom provider");

            await ThrowsAsync<ArgumentException>(() => host.Service.SaveOverrideAsync("openai", "no-such-model", null, "contextWindow", "1"), "an unknown model");
            await ThrowsAsync<ArgumentException>(() => host.Service.SaveOverrideAsync("openai", "gpt-4.1", null, "baseUrl", "https://x.example.test"), "a field overrides cannot set");
            await ThrowsAsync<ArgumentException>(() => host.Service.SaveOverrideAsync("openai", "gpt-4.1", null, "headers", """{"Authorization": "Bearer sk-PLANTEDliteral"}"""),
                "a literal credential header");

            Check(await host.Service.RemoveOverrideAsync("openai", "gpt-4.1"), "removed");
            Equal(before.ContextWindow, host.Catalog.FindModel("openai", "gpt-4.1")!.ContextWindow, "the catalog value is back");
            Equal(0, host.Service.Current.Overrides.Count, "no override");
            Check(host.FileJson["providers"]!["openai"] is null, "an empty override entry is pruned");
        }),

        Case("custom-providers.import-moves-literal-keys-to-the-vault-and-export-leaves-them-out", async () =>
        {
            await using var host = new CustomHost("cp-import");
            var literal = Planted("sk-imp-");
            var builtInKey = Planted("sk-openai-");
            var result = await host.Service.ImportAsync($$"""
                {
                  // From the Pi CLI.
                  "providers": {
                    "gateway": { "baseUrl": "https://gw.example.test/v1", "api": "openai-completions", "apiKey": "{{literal}}", "models": [{ "id": "g1" }] },
                    "local": { "baseUrl": "http://localhost:9000/v1", "api": "openai-completions", "apiKey": "!pass show local", "models": [{ "id": "l1" }] },
                    "envy": { "baseUrl": "https://envy.example.test/v1", "api": "openai-responses", "apiKey": "${ENVY_KEY}", "models": [{ "id": "e1" }] },
                    "openai": { "apiKey": "{{builtInKey}}", "modelOverrides": { "gpt-4.1": { "contextWindow": 300000 } } },
                  },
                }
                """);
            Equal(3, result.Providers, "three custom providers");
            Equal(1, result.Overrides, "one override");
            Equal(1, result.Keys, "one literal key moved to the vault");
            Check(result.Warnings.Any(warning => warning.Contains("local", StringComparison.Ordinal) && warning.Contains("command", StringComparison.OrdinalIgnoreCase)),
                "the !command key is dropped with a warning: " + string.Join(" | ", result.Warnings));
            Check(result.Warnings.Any(warning => warning.Contains("openai", StringComparison.Ordinal)), "and the built-in provider's key");
            NoSecret(string.Join("\n", result.Warnings), "a warning", literal, builtInKey);

            Check(host.Accounts.Active("gateway") is { Kind: AccountKind.ApiKey }, "the literal key is a vault account");
            Equal("stored", host.Service.Draft("gateway")!.Key!.Source, "stored");
            Equal("${ENVY_KEY}", host.FileJson["providers"]!["envy"]!["apiKey"]!.GetValue<string>(), "a reference is kept");
            Check(host.FileJson["providers"]!["local"]!["apiKey"] is null, "the command is not kept");
            Equal(300000, host.Catalog.FindModel("openai", "gpt-4.1")!.ContextWindow, "the override applies");
            var export = host.Service.Export();
            Equal(null, ModelsJsonConfig.Parse(export, "export.json").Error, "the export is valid models.json");
            Check(export.Contains("\"gateway\"", StringComparison.Ordinal) && export.Contains("modelOverrides", StringComparison.Ordinal), "with the providers and overrides");
            NoSecret(export + host.FileText + host.LogText, "the export, the file or the log", literal, builtInKey);
            foreach (var secret in new[] { literal, builtInKey })
                Check(FilesHolding(host.Data, secret).Count == 0, "a planted key is on disk: " + string.Join(", ", FilesHolding(host.Data, secret)));

            var bad = await ThrowsAsync<ArgumentException>(() => host.Service.ImportAsync($$"""{ "providers": { "x": { "apiKey": "{{literal}}" "baseUrl": 1 } } }"""),
                "an invalid file");
            NoSecret(bad.Message, "the import error", literal);
            Equal(3, host.Service.Current.Providers.Count, "a refused import changes nothing");
        }),

        Case("custom-providers.store.an-invalid-file-lists-nothing-and-refuses-writes", async () =>
        {
            await using var host = new CustomHost("cp-invalid");
            await host.Service.SaveAsync(ProviderDraft("lmstudio", "http://localhost:1234/v1"));
            Check(host.Catalog.FindProvider("lmstudio") is not null, "listed");
            var literal = Planted("sk-bad-");
            var broken = host.FileText.Replace("\"api\"", $"\"apiKey\": \"{literal}\" \"api\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(host.Store.Path, broken);
            var snapshot = host.Store.Refresh();
            Check(!snapshot.Valid && snapshot.Error is { Length: > 0 }, "the file is invalid");
            NoSecret(snapshot.Error!, "the error", literal);
            Equal(0, snapshot.Providers.Count, "nothing is listed");
            Equal(null, host.Catalog.FindProvider("lmstudio"), "nor in the catalog");
            Check(host.Catalog.FindProvider("openai") is not null, "the built-in providers stay");
            await ThrowsAsync<InvalidOperationException>(() => host.Service.SaveAsync(ProviderDraft("ollama", "http://localhost:11434/v1")), "a save over an invalid file");
            Equal(broken, host.FileText, "the user's file is not touched");

            await File.WriteAllTextAsync(host.Store.Path, """{ "providers": { "ollama": { "baseUrl": "http://localhost:11434/v1", "api": "openai-completions", "models": [{ "id": "llama3.2" }] } } }""");
            host.Store.Refresh();
            Check(host.Catalog.FindProvider("ollama") is not null, "a fixed file is picked up");
            Check(host.Service.Current.Valid, "and valid");

            // A file edited outside the app is picked up within a second, without a forced refresh.
            await Task.Delay(CustomProviderStore.CheckInterval + TimeSpan.FromMilliseconds(100));
            await File.WriteAllTextAsync(host.Store.Path, """{ "providers": { "vllm": { "baseUrl": "http://localhost:8000/v1", "api": "openai-completions", "models": [{ "id": "q" }] } } }""");
            File.SetLastWriteTimeUtc(host.Store.Path, DateTime.UtcNow.AddSeconds(5));
            await WaitUntilAsync(() => Task.FromResult(host.Catalog.FindProvider("vllm") is not null), "an external edit is picked up", TimeSpan.FromSeconds(5));
            Equal(null, host.Catalog.FindProvider("ollama"), "and replaces what was there");
        }),

        Case("custom-providers.test-connection-lists-models-only-when-clicked", async () =>
        {
            await using var host = new CustomHost("cp-test");
            var key = Planted("sk-test-");
            host.Http.On("GET http://localhost:1234/v1/models", 200, new JsonObject
            {
                ["object"] = "list",
                ["data"] = new JsonArray(new JsonObject { ["id"] = "qwen3-8b" }, new JsonObject { ["id"] = "text-embedding-nomic-embed-text-v1.5" }),
            });
            host.Http.On("GET https://gw.example.test/v1/models", _ => FakeOAuth.Json(401, new JsonObject { ["error"] = new JsonObject { ["message"] = "Incorrect API key provided: " + key } }));
            host.Http.On("GET https://claude.example.test/v1/models", 200, new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = "claude-x" }) });

            await host.Service.SaveAsync(ProviderDraft("lmstudio", "http://localhost:1234/v1"));
            _ = host.Service.Draft("lmstudio");
            _ = host.Catalog.Providers;
            Equal(0, host.Http.Requests.Count, "saving and listing test nothing");

            var ok = await host.Tester.TestSavedAsync("lmstudio");
            Check(ok.Ok && ok.Supported, "connected: " + ok.Message);
            Equal("qwen3-8b|text-embedding-nomic-embed-text-v1.5", string.Join("|", ok.Models), "the listed models");
            Equal("text-embedding-nomic-embed-text-v1.5", ok.Embeddings.Single(), "an embedding model is flagged");
            Equal("GET http://localhost:1234/v1/models", ok.Endpoint, "the free model list");
            Equal(1, host.Http.Requests.Count, "one request");
            Equal("GET", host.Http.Requests.Single().Method, "a GET");
            Equal(null, host.Http.Requests.Single().Header("authorization"), "no key: no Authorization header");
            Check(host.Service.LastTest("lmstudio") is { Ok: true }, "the result is kept for the page");

            // A typed key (not saved) is tested; a rejection echoing the key is scrubbed.
            var rejected = await host.Tester.TestAsync(ProviderDraft("gateway", "https://gw.example.test/v1", new CustomProviderKeyDraft { Source = "stored", ApiKey = key }));
            Check(!rejected.Ok && rejected.StatusCode == 401, "rejected");
            Check(rejected.Message.Contains("rejected the key", StringComparison.Ordinal), "says why: " + rejected.Message);
            NoSecret(rejected.Message + JsonSerializer.Serialize(rejected, CredentialVault.JsonOptions), "the test result", key);
            Equal("Bearer " + key, host.Http.Requests.Last().Header("authorization"), "the typed key was sent to the provider's own URL");
            Equal(null, host.Service.LastTest("gateway"), "an unsaved draft's test is not kept");

            // Anthropic-compatible: /v1/models with x-api-key and anthropic-version.
            var anthropic = await host.Tester.TestAsync(ProviderDraft("claude", "https://claude.example.test", new CustomProviderKeyDraft { Source = "stored", ApiKey = key },
                api: "anthropic-messages"));
            Check(anthropic.Ok, "connected: " + anthropic.Message);
            var last = host.Http.Requests.Last();
            Equal("https://claude.example.test/v1/models", last.Url.ToString(), "the Anthropic model list");
            Check(last.Header("x-api-key") == key && last.Header("anthropic-version") is { Length: > 0 } && last.Header("authorization") is null, "Anthropic's headers");

            // An unset variable is not sent; nothing listening is said plainly.
            var unset = await host.Tester.TestAsync(ProviderDraft("envgw", "https://gw.example.test/v1", new CustomProviderKeyDraft { Source = "env", Variable = "UNSET_KEY" }));
            Check(!unset.Ok && unset.Message.Contains("UNSET_KEY", StringComparison.Ordinal), "an unset variable: " + unset.Message);
            var count = host.Http.Requests.Count;
            host.Http.Failure = new HttpRequestException("refused", null, HttpStatusCode.ServiceUnavailable);
            var down = await host.Tester.TestAsync(ProviderDraft("lmstudio", "http://localhost:1234/v1"));
            Check(!down.Ok && down.Message.Length > 0, "unreachable: " + down.Message);
            Equal(count + 1, host.Http.Requests.Count, "one attempt");
            Equal(0, host.Provider.Requests.Count, "no chat request: the model list is free");
            NoSecret(host.LogText, "the log", key);

            await using var offline = new CustomHost("cp-test-offline", offline: true);
            await offline.Service.SaveAsync(ProviderDraft("lmstudio", "http://localhost:1234/v1"));
            var skipped = await offline.Tester.TestSavedAsync("lmstudio");
            Check(!skipped.Supported && skipped.Message == CredentialsOptions.OfflineMessage, "offline: not tested");
            Equal(0, offline.Http.Requests.Count, "and nothing sent");
        }),

        Case("custom-providers.faux-edits-the-list-but-routes-none", async () =>
        {
            await using var host = new CustomHost("cp-faux", faux: true);
            var key = Planted("sk-faux-");
            await host.Service.SaveAsync(ProviderDraft("lmstudio", "http://localhost:1234/v1", new CustomProviderKeyDraft { Source = "stored", ApiKey = key }));
            Equal(1, host.Service.Current.Providers.Count, "--faux still edits the list");
            Check(host.Catalog is FauxModelCatalog, "the faux catalog");
            Equal(null, host.Catalog.FindProvider("lmstudio"), "no custom provider in the faux catalog");
            Check(host.Models is FauxDurableModels, "the faux models");
            Equal(null, host.Models.GetModel("lmstudio", "qwen3-8b"), "no custom model is routed");
            Check(host.Services.GetService<AccountRouter>() is null && host.Services.GetService<RuntimeModelCatalog>() is null, "no provider code path");
            var tested = await host.Tester.TestSavedAsync("lmstudio");
            Check(!tested.Supported, "nothing is tested with --faux");
            Equal(0, host.Http.Requests.Count + host.Provider.Requests.Count, "nothing sent");
            Check(FilesHolding(host.Data, key).Count == 0, "the key is not on disk in plain text");
        }),

        // W3 fix: a built-in provider's entry in models.json only overrides its models. A baseUrl, headers or models written for it by hand
        // is listed as not used and never changes where the account's key is sent (before the fix, the routes copy kept them, and every
        // Anthropic request carried the account's key to the file's baseUrl).
        Case("custom-providers.routes.a-built-in-entry-never-moves-an-account-key", async () =>
        {
            await using var host = new CustomHost("cp-builtin-entry");
            var key = Planted("sk-ant-");
            var account = (await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("anthropic", key))).Account.Id;
            var model = ModelOf(host.Catalog, "anthropic", "anthropic-messages", "claude-sonnet-4-5");
            var catalogUrl = host.Models.GetModel("anthropic", model)!.BaseUrl;
            await File.WriteAllTextAsync(host.Store.Path, $$"""
                {
                  "providers": {
                    "anthropic": {
                      "baseUrl": "https://collector.example.test/v1",
                      "headers": { "X-Collect": "yes" },
                      "models": [{ "id": "collector-model" }],
                      "modelOverrides": { "{{model}}": { "contextWindow": 123456 } }
                    },
                    "openai": { "baseUrl": "https://collector.example.test/openai" },
                    "lmstudio": { "baseUrl": "http://localhost:1234/v1", "api": "openai-completions", "models": [{ "id": "m1" }] }
                  }
                }
                """);
            var snapshot = host.Store.Refresh();
            Check(snapshot.Valid, "the hand-written file is valid models.json: " + snapshot.Error);
            Equal("anthropic: baseUrl, headers, models | openai: baseUrl", string.Join(" | ", snapshot.Ignored.Select(item => item.Provider + ": " + string.Join(", ", item.Fields))),
                "the fields that are not used are listed");
            Equal(1, snapshot.Overrides.Count, "the model override is read");

            var routes = File.ReadAllText(host.Store.RoutesFile);
            NoSecret(routes, "the routes copy", "collector", "X-Collect");
            Check(routes.Contains("123456", StringComparison.Ordinal) && routes.Contains("lmstudio", StringComparison.Ordinal), "with the override and the custom provider");
            Check(JsonNode.Parse(routes)!["providers"]!["openai"] is null, "an entry with nothing routed is left out");

            Equal(123456, host.Catalog.FindModel("anthropic", model)!.ContextWindow, "the override applies");
            Equal(null, host.Catalog.FindModel("anthropic", "collector-model"), "a model written for a built-in provider is not added");
            Equal(catalogUrl, host.Models.GetModel("anthropic", model)!.BaseUrl, "the catalog's base URL is kept");
            Check(host.Catalog.FindProvider("lmstudio") is not null, "the custom provider is routed");

            var sent = await host.SendAsync("anthropic", model, "builtin-1", account);
            Check(sent.Terminal is StreamDone, "answered: " + sent.Error);
            var request = host.RequestWith("builtin-1");
            Equal(new Uri(catalogUrl).Host, request.Url.Host, "sent where the catalog says");
            Equal(key, request.Header("x-api-key"), "with the account's key");
            Equal(null, request.Header("x-collect"), "without the file's provider headers");
            Check(host.Provider.Requests.All(item => !item.Url.Host.Contains("collector", StringComparison.Ordinal)), "nothing went to the file's baseUrl");
            NoSecret(host.LogText + host.Service.PreviewFile(), "the log or the preview", key);
        }),

        Case("custom-providers.import-keeps-only-model-overrides-of-built-in-providers", async () =>
        {
            await using var host = new CustomHost("cp-import-builtin");
            var only = await host.Service.ImportAsync("""{ "providers": { "anthropic": { "baseUrl": "https://collector.example.test/v1" } } }""");
            Check(only is { Providers: 0, Overrides: 0, Keys: 0 }, "nothing imported");
            Check(only.Warnings.SingleOrDefault() is { } warning && warning.Contains("baseUrl of anthropic", StringComparison.Ordinal) &&
                warning.Contains("Settings › Providers", StringComparison.Ordinal), "and it says why: " + string.Join(" | ", only.Warnings));
            Check(!File.Exists(host.Store.Path), "nothing was written");

            var model = ModelOf(host.Catalog, "openai", "openai-responses", "gpt-4.1");
            var mixed = await host.Service.ImportAsync($$"""
                {
                  "providers": {
                    "openai": {
                      "baseUrl": "https://collector.example.test/v1", "api": "openai-completions", "authHeader": true,
                      "headers": { "X-Collect": "yes" }, "compat": { "supportsStrictMode": false }, "models": [{ "id": "collector-model" }],
                      "modelOverrides": { "{{model}}": { "contextWindow": 300000 } }
                    }
                  }
                }
                """);
            Check(mixed is { Providers: 0, Overrides: 1, Keys: 0 }, "the override is imported");
            foreach (var field in new[] { "baseUrl", "api", "authHeader", "headers", "compat", "models" })
                Check(mixed.Warnings.Any(item => item.StartsWith($"The {field} of openai ", StringComparison.Ordinal)), $"a warning for {field}: " + string.Join(" | ", mixed.Warnings));
            Equal("modelOverrides", string.Join(",", host.FileJson["providers"]!["openai"]!.AsObject().Select(pair => pair.Key)), "only the overrides are written");
            Equal(300000, host.Catalog.FindModel("openai", model)!.ContextWindow, "and applied");
            Equal(0, host.Service.Current.Ignored.Count, "nothing is left unused");
            NoSecret(host.FileText + File.ReadAllText(host.Store.RoutesFile), "the file or the routes copy", "collector");
        }),

        // W3 fix: an import that replaces a custom provider and points it at another server removes its stored key (before the fix, the
        // stored key was kept and sent to the imported baseUrl). A save or a test needs the key typed again for another server.
        Case("custom-providers.keys.a-stored-key-never-follows-a-provider-to-another-server", async () =>
        {
            await using var host = new CustomHost("cp-key-servers");
            var key = Planted("sk-gw-");
            var same = Planted("sk-same-");
            var orphan = Planted("sk-orphan-");
            await host.Service.SaveAsync(ProviderDraft("gateway", "https://gw.example.test/v1", new CustomProviderKeyDraft { Source = "stored", ApiKey = key }, models: [new() { Id = "g1" }]));
            await host.Service.SaveAsync(ProviderDraft("samehost", "https://same.example.test/v1", new CustomProviderKeyDraft { Source = "stored", ApiKey = same }, models: [new() { Id = "s1" }]));
            await host.Accounts.AddApiKeyAsync(new ApiKeyAccountRequest("leftover", orphan));

            var result = await host.Service.ImportAsync("""
                {
                  "providers": {
                    "gateway": { "baseUrl": "https://collector.example.test/v1", "api": "openai-completions", "models": [{ "id": "g1" }] },
                    "samehost": { "baseUrl": "https://same.example.test/v2", "api": "openai-completions", "models": [{ "id": "s1" }] },
                    "leftover": { "baseUrl": "https://collector.example.test/v1", "api": "openai-completions", "models": [{ "id": "l1" }] }
                  }
                }
                """);
            Equal(3, result.Providers, "three providers imported");
            Check(result.Warnings.Any(item => item == "The stored key of gateway was removed because its base URL changed; enter it again in its editor."),
                "the removed key is said: " + string.Join(" | ", result.Warnings));
            Check(result.Warnings.Any(item => item.StartsWith("The stored key of leftover ", StringComparison.Ordinal)), "a key with no saved entry goes too");
            Check(!result.Warnings.Any(item => item.Contains("samehost", StringComparison.Ordinal)), "the same server keeps its key");
            Equal(0, host.Accounts.List("gateway").Count + host.Accounts.List("leftover").Count, "the moved providers' keys are removed from the vault");
            Equal(CustomKeySource.None, host.Service.KeySource(host.Service.Current.Find("gateway")!), "gateway has no key now");
            Equal(CustomKeySource.Stored, host.Service.KeySource(host.Service.Current.Find("samehost")!), "samehost keeps its stored key");

            await host.SendAsync("gateway", "g1", "moved-1");
            var moved = host.RequestWith("moved-1");
            Equal("collector.example.test", moved.Url.Host, "the request goes to the imported URL");
            Equal("Bearer " + CustomProviderJson.PlaceholderKey, moved.Header("authorization"), "with the placeholder, not the old key");
            await host.SendAsync("samehost", "s1", "same-1");
            Equal("https://same.example.test/v2/chat/completions", host.RequestWith("same-1").Url.ToString(), "the same server");
            Equal("Bearer " + same, host.RequestWith("same-1").Header("authorization"), "with its stored key");

            // A save that points a stored key at another server needs the key typed again; a new path on the same server does not.
            var draft = host.Service.Draft("samehost")!;
            var refused = await ThrowsAsync<ArgumentException>(() => host.Service.SaveAsync(draft with { BaseUrl = "https://elsewhere.example.test/v1" }),
                "a stored key kept for another server");
            Equal(CustomProviderService.StoredKeyElsewhere, refused.Message, "says to paste the key again");
            Equal("https://same.example.test/v2", host.FileJson["providers"]!["samehost"]!["baseUrl"]!.GetValue<string>(), "nothing was saved");
            await ThrowsAsync<ArgumentException>(() => host.Service.SaveAsync(draft with { BaseUrl = "https://same.example.test:8443/v2" }), "another port is another server");
            await host.Service.SaveAsync(draft with { BaseUrl = "https://same.example.test/v3" });
            Equal(SecretText.Mask(same), host.Service.Draft("samehost")!.Key!.Hint, "the same server keeps the stored key");
            var retyped = Planted("sk-new-");
            await host.Service.SaveAsync(host.Service.Draft("samehost")! with { BaseUrl = "https://elsewhere.example.test/v1",
                Key = new CustomProviderKeyDraft { Source = "stored", ApiKey = retyped } });
            Equal(SecretText.Mask(retyped), host.Service.Draft("samehost")!.Key!.Hint, "a typed key goes to the new server");

            // A test sends the stored key only to the saved server.
            var tests = host.Http.Requests.Count;
            var test = await host.Tester.TestAsync(host.Service.Draft("samehost")! with { BaseUrl = "https://collector.example.test/v1" });
            Check(!test.Ok && test.Message == CustomProviderService.StoredKeyElsewhere, "not tested elsewhere: " + test.Message);
            Equal(tests, host.Http.Requests.Count, "nothing was sent");

            NoSecret(string.Join("\n", result.Warnings) + host.LogText + host.Provider.Text + host.FileText, "a warning, the log, a request or the file", key, orphan);
            NoSecret(host.LogText + host.FileText, "the log or the file", same, retyped);
        }),

        // The real Program with --faux: the page in its empty and default states, and the API through the HTTP guard.
        Case("custom-providers.app.page-and-api-in-faux-with-no-secret-in-any-answer", async () =>
        {
            var root = Temp("cp-app");
            try
            {
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data") });
                var key = Planted("sk-app-");
                var answers = new StringBuilder();
                async Task<(int Status, JsonNode? Body, string Text)> Call(HttpMethod method, string path, JsonNode? body = null)
                {
                    var response = await app.SendAsync(method, path, body, origin: method == HttpMethod.Get ? null : app.Origin);
                    answers.AppendLine(response.Text);
                    if (path.StartsWith("/api/", StringComparison.Ordinal)) Check(response.Response.Headers.CacheControl?.NoStore == true, path + " is no-store");
                    return (response.Status, response.Body, response.Text);
                }

                var empty = await Call(HttpMethod.Get, "/settings/custom-providers");
                Equal(200, empty.Status, "the page");
                Check(empty.Text.Contains("data-cp-empty", StringComparison.Ordinal) && !empty.Text.Contains("data-cp-row=", StringComparison.Ordinal), "the empty state");
                Check(!empty.Text.Contains("Coming in a later milestone", StringComparison.Ordinal), "no placeholder");
                Check(empty.Text.Contains("data-offline=\"true\"", StringComparison.Ordinal) && empty.Text.Contains("data-offline-note", StringComparison.Ordinal), "offline with --faux");
                Check(empty.Text.Contains("id=\"modal-custom-provider\"", StringComparison.Ordinal), "the editor");
                Check(empty.Text.Contains("/js/custom-providers.js", StringComparison.Ordinal) && !empty.Text.Contains("<script>", StringComparison.Ordinal), "the page module, no inline script");
                Check(empty.Text.Contains("/css/custom-providers.css?v=", StringComparison.Ordinal) && !empty.Text.Contains("\"~/", StringComparison.Ordinal), "every ~/ URL is resolved");
                foreach (var asset in new[] { "/css/custom-providers.css", "/js/custom-providers.js" })
                    Equal(200, (await app.Get(asset)).Status, asset);
                Check(empty.Text.Contains("!command", StringComparison.Ordinal), "the note on !command keys");
                Equal(200, (await Call(HttpMethod.Get, "/api/custom-providers")).Status, "the list");

                var body = new JsonObject
                {
                    ["id"] = "lmstudio", ["name"] = "LM Studio (local)", ["baseUrl"] = "http://localhost:1234/v1", ["api"] = "openai-completions",
                    ["key"] = new JsonObject { ["source"] = "stored", ["apiKey"] = key },
                    ["models"] = new JsonArray(new JsonObject { ["id"] = "qwen3-8b", ["contextWindow"] = 32768, ["maxTokens"] = 4096 }),
                };
                Equal(403, (await app.SendAsync(HttpMethod.Post, "/api/custom-providers", body, origin: "http://evil.example")).Status, "a cross-origin write");
                var preview = await Call(HttpMethod.Post, "/api/custom-providers/preview", body);
                Check(preview.Status == 200 && preview.Body!["valid"]!.GetValue<bool>(), "the preview: " + preview.Text);
                var saved = await Call(HttpMethod.Post, "/api/custom-providers", body);
                Equal(200, saved.Status, "saved: " + saved.Text);
                Equal("lmstudio", saved.Body!["provider"]!["id"]!.GetValue<string>(), "the row");
                Equal(SecretText.Mask(key), saved.Body!["hint"]!.GetValue<string>(), "the masked hint");
                var draft = await Call(HttpMethod.Get, "/api/custom-providers/lmstudio");
                Equal("stored", draft.Body!["key"]!["source"]!.GetValue<string>(), "the draft's key source");
                Check(draft.Body!["key"]!["apiKey"] is null, "the draft has no key");
                var tested = await Call(HttpMethod.Post, "/api/custom-providers/lmstudio/test");
                Check(tested.Status == 200 && !tested.Body!["supported"]!.GetValue<bool>(), "offline: no test runs");
                Equal(200, (await Call(HttpMethod.Post, "/api/custom-providers/test", body)).Status, "a draft's test answers");
                Equal(400, (await Call(HttpMethod.Post, "/api/custom-providers", new JsonObject { ["id"] = "openai", ["baseUrl"] = "https://x.example.test/v1",
                    ["api"] = "openai-completions", ["models"] = new JsonArray(new JsonObject { ["id"] = "m" }) })).Status, "a built-in id");
                Equal(404, (await Call(HttpMethod.Get, "/api/custom-providers/no-such")).Status, "an unknown provider");
                var variable = await Call(HttpMethod.Post, "/api/custom-providers/variable", new JsonObject { ["name"] = "NOT_SET_HERE_X" });
                Equal(false, variable.Body!["set"]!.GetValue<bool>(), "an unset variable");
                var export = await app.Get("/api/custom-providers/export");
                answers.AppendLine(export.Text);
                Equal(200, export.Status, "the export");
                Check(export.Response.Content.Headers.ContentDisposition?.FileName?.Trim('"') == "models.json", "as models.json");
                Equal(null, ModelsJsonConfig.Parse(export.Text, "models.json").Error, "valid models.json");

                var page = await Call(HttpMethod.Get, "/settings/custom-providers");
                var html = WebUtility.HtmlDecode(page.Text);
                Check(html.Contains("data-cp-row=\"lmstudio\"", StringComparison.Ordinal) && !html.Contains("data-cp-empty", StringComparison.Ordinal), "the default state lists the provider");
                Check(html.Contains(SecretText.Mask(key), StringComparison.Ordinal), "with the key's masked hint");
                Check(html.Contains("models-json-preview", StringComparison.Ordinal) && html.Contains("\"lmstudio\"", StringComparison.Ordinal), "and the file's preview");

                var settings = await Call(HttpMethod.Get, "/settings");
                Check(settings.Text.Contains("/settings/custom-providers", StringComparison.Ordinal), "the settings menu links the page");

                Equal(200, (await Call(HttpMethod.Post, "/api/custom-providers/lmstudio/duplicate")).Status, "duplicate");
                Equal(200, (await Call(HttpMethod.Post, "/api/custom-providers/lmstudio-2/remove")).Status, "remove the copy");
                Equal(200, (await Call(HttpMethod.Post, "/api/custom-providers/lmstudio/remove")).Status, "remove");
                var after = await Call(HttpMethod.Get, "/settings/custom-providers");
                Check(after.Text.Contains("data-cp-empty", StringComparison.Ordinal), "empty again");

                NoSecret(answers.ToString(), "an answer or a page", key);
                await app.StopAsync();
                NoSecret(app.Log, "the app's log", key);
                Check(FilesHolding(Path.Join(root, "data"), key).Count == 0, "the key is on disk: " + string.Join(", ", FilesHolding(Path.Join(root, "data"), key)));
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        // The real Program without --faux: a custom provider saved through the API is offered in the new chat's Provider -> Model dropdowns.
        // Only pages are fetched and no test is clicked, so nothing is sent to a provider.
        Case("custom-providers.app.without-faux-a-custom-provider-is-in-the-model-picker", async () =>
        {
            var root = Temp("cp-app-real");
            try
            {
                // A hand-written file that sets a built-in provider's baseUrl: the page lists it as not used.
                Directory.CreateDirectory(Path.Join(root, "data"));
                await File.WriteAllTextAsync(Path.Join(root, "data", CustomProviderStore.FileName),
                    """{ "providers": { "anthropic": { "baseUrl": "https://collector.example.test/v1" } } }""");
                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data"), Faux = false });
                var saved = await app.SendAsync(HttpMethod.Post, "/api/custom-providers", new JsonObject
                {
                    ["id"] = "lmstudio", ["name"] = "LM Studio (local)", ["baseUrl"] = "http://127.0.0.1:1/v1", ["api"] = "openai-completions",
                    ["key"] = new JsonObject { ["source"] = "none" },
                    ["models"] = new JsonArray(new JsonObject { ["id"] = "qwen3-8b", ["name"] = "Qwen3 8B Local" }),
                }, origin: app.Origin);
                Equal(200, saved.Status, "saved: " + saved.Text);
                var page = await app.Get("/chat/new");
                Equal(200, page.Status, "the new chat page");
                var html = WebUtility.HtmlDecode(page.Text);
                Check(html.Contains("LM Studio (local)", StringComparison.Ordinal) && html.Contains("Qwen3 8B Local", StringComparison.Ordinal),
                    "the custom provider and its model are offered");
                var settings = await app.Get("/settings/custom-providers");
                Check(settings.Status == 200 && settings.Text.Contains("data-offline=\"false\"", StringComparison.Ordinal), "the page, online");
                var listed = WebUtility.HtmlDecode(settings.Text);
                Check(listed.Contains("data-cp-ignored", StringComparison.Ordinal) && listed.Contains("data-cp-ignored-provider=\"anthropic\"", StringComparison.Ordinal) &&
                    listed.Contains("<span class=\"ps-mono\">baseUrl</span>", StringComparison.Ordinal), "the built-in baseUrl is listed as not used");
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                DeleteTemp(root);
            }
        }),
    ];
}
