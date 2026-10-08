using System.Collections.Immutable;
using PiSharp.AI.ModelOperations;
using PiSharp.Cli.Extensions;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions.Runtime.Facade.Context;

// core/extensions/types.ts ExtensionContext.modelRegistry: a native extension command classifies and generates images
// through the admitted host's registry, as upstream extensions call ctx.modelRegistry.classify/generateImages.
internal static partial class Program
{
    private sealed class CommandContext : IExtensionCommandContext
    {
        public string OwnerId => "owner"; public long OwnerGeneration => 1;
        public CancellationToken OperationCancellationToken => default; public CancellationToken SessionCancellationToken => default;
        public CancellationToken ExtensionLifetimeCancellationToken => default;
    }

    /// <summary>A minimal admitted host: plain reads plus, optionally, the model registry capability over the CLI bridge.</summary>
    private class ReadHost : IExtensionContextReadHost
    {
        public string GetCwd(IExtensionContext context) => "/work"; public JsonData? GetModel(IExtensionContext context) => null;
        public bool IsIdle(IExtensionContext context) => true; public bool HasPendingMessages(IExtensionContext context) => false;
        public string GetSystemPrompt(IExtensionContext context) => "";
    }

    private sealed class ModelHost(NativeExtensionModelOperations operations) : ReadHost, IExtensionModelOperationsHost
    {
        public ImmutableArray<JsonData> GetModelsOfType(IExtensionContext context, ModelType type, string? provider) => operations.GetModelsOfType(type, provider);
        public JsonData? GetModelOfType(IExtensionContext context, ModelType type, string provider, string id) => operations.GetModelOfType(type, provider, id);
        public Task<ImmutableArray<JsonData>> GetAvailableOfTypeAsync(IExtensionContext context, ModelType type, string? provider, CancellationToken cancellationToken) =>
            operations.GetAvailableOfTypeAsync(type, provider, cancellationToken);
        public Task<ClassifierResult> ClassifyAsync(IExtensionContext context, JsonData model, ClassifierContext classifierContext,
            ExtensionModelRequestOptions? options, CancellationToken cancellationToken) => operations.ClassifyAsync(model, classifierContext, options, cancellationToken);
        public Task<AssistantImages> GenerateImagesAsync(IExtensionContext context, JsonData model, ImagesContext imagesContext,
            ExtensionModelRequestOptions? options, CancellationToken cancellationToken) => operations.GenerateImagesAsync(model, imagesContext, options, cancellationToken);
    }

    private static async Task RunCommand(IExtensionContextReadHost host, Func<IExtensionModelOperationsFacade, Task> body)
    {
        var command = ExtensionCommandFacade.CreateCommand("registration", "classify", "Classify", host,
            async (_, facade, _) => await body((IExtensionModelOperationsFacade)facade));
        await command.ExecuteAsync(JsonData.EmptyObject, new CommandContext(), CancellationToken.None);
    }

    /// <summary>The fake HTTP transport reaches the registry's API calls through a provider whose APIs carry it.</summary>
    private static NativeExtensionModelOperations ExtensionOperations(FakeHttp http)
    {
        var registry = BuiltinRegistry(new Dictionary<string, string> { ["OPENAI_API_KEY"] = "env-openai", ["OPENROUTER_API_KEY"] = "env-openrouter" });
        foreach (var provider in registry.GetProviders())
            registry.SetProvider(provider with
            {
                Classifiers = provider.Classifiers.ToImmutableDictionary(pair => pair.Key, pair => (IClassifierApi)new WithHttp(pair.Value, http)),
                Images = provider.Images.ToImmutableDictionary(pair => pair.Key, pair => (IImagesApi)new WithHttp(pair.Value, http))
            });
        return new(registry);
    }

    private sealed class WithHttp(object inner, FakeHttp http) : IClassifierApi, IImagesApi
    {
        public string Api => inner is IClassifierApi classifier ? classifier.Api : ((IImagesApi)inner).Api;
        public Task<ClassifierResult> ClassifyAsync(ClassifierModel model, ClassifierContext context, ClassifierOptions? options = null, CancellationToken cancellationToken = default) =>
            ((IClassifierApi)inner).ClassifyAsync(model, context, (options ?? new()) with { Http = http.Client }, cancellationToken);
        public Task<AssistantImages> GenerateImagesAsync(ImageModel model, ImagesContext context, ImagesOptions? options = null, CancellationToken cancellationToken = default) =>
            ((IImagesApi)inner).GenerateImagesAsync(model, context, (options ?? new()) with { Http = http.Client }, cancellationToken);
    }

    private static IEnumerable<(string, Func<Task>)> ExtensionCases() =>
    [
        ("extension.command-facade-classifies-and-generates-through-the-host-registry", async () =>
        {
            var http = new FakeHttp((request, _, _) => Task.FromResult(request.Url.EndsWith("/decisions", StringComparison.Ordinal)
                ? FakeHttp.Json("""{"answers":[{"type":"predicate","name":"approved","probability":0.8}]}""") : FakeHttp.Json(ImageResponse)));
            ClassifierResult? classified = null; AssistantImages? generated = null; JsonData? found = null; int available = 0, images = 0;
            await RunCommand(new ModelHost(ExtensionOperations(http)), async registry =>
            {
                found = registry.GetModelOfType(ModelType.Classifier, "openai", "gpt-6-luna");
                images = registry.GetModelsOfType(ModelType.Image, "openrouter").Length;
                available = (await registry.GetAvailableOfTypeAsync(ModelType.Classifier, "openai")).Length;
                classified = await registry.ClassifyAsync(found!, ApprovalContext, new ExtensionModelRequestOptions { Headers = [new("x-extension", "1")] });
                generated = await registry.GenerateImagesAsync(registry.GetModelOfType(ModelType.Image, "openrouter", "google/gemini-2.5-flash-image")!, DogPrompt);
            });
            Equal("openai-decisions", found!.Value.GetProperty("api").GetString(), "model json");
            Check(images > 0, "image models listed"); Equal(1, available, "available classifier");
            Equal(ModelOperationStopReason.Stop, classified!.StopReason, classified.ErrorMessage ?? "classified");
            Equal(new ClassifierBoolAnswer(0.8), Answer<ClassifierBoolAnswer>(classified, "approved"), "answer");
            Equal(ModelOperationStopReason.Stop, generated!.StopReason, generated.ErrorMessage ?? "generated");
            Equal("Bearer env-openai", http.Requests[0].Headers["authorization"], "registry auth");
            Equal("1", http.Requests[0].Headers["x-extension"], "extension header");
            Equal("Bearer env-openrouter", http.Requests[1].Headers["authorization"], "image auth");
        }),
        ("extension.supplied-models-are-used-as-given-with-their-provider-credentials", async () =>
        {
            var http = new FakeHttp((request, _, _) => Task.FromResult(request.Url.EndsWith("/decisions", StringComparison.Ordinal)
                ? FakeHttp.Json("""{"answers":[{"type":"predicate","name":"approved","probability":0.8}]}""") : FakeHttp.Json(ImageResponse)));
            var operations = ExtensionOperations(http);
            var luna = operations.GetModelOfType(ModelType.Classifier, "openai", "gpt-6-luna")!;
            Equal(OperationModel.FromJson(luna).ToJson().ToString(), luna.ToString(), "catalog JSON as the registry holds it");
            // A custom base URL and headers: Pi resolves model.provider's auth and applies it to the model as given.
            var custom = System.Text.Json.Nodes.JsonNode.Parse(luna.ToString())!.AsObject();
            custom["id"] = "my-decider"; custom["baseUrl"] = "https://gateway.example/openai/v1/";
            custom["headers"] = new System.Text.Json.Nodes.JsonObject { ["x-gateway"] = "g1" };
            var result = await operations.ClassifyAsync(JsonData.Parse(custom.ToJsonString()), ApprovalContext,
                new ExtensionModelRequestOptions { Headers = [new("x-extension", "1")] }, default);
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "custom classifier");
            Equal("my-decider", result.Model, "result names the supplied model");
            Equal("https://gateway.example/openai/v1/decisions", http.Requests[0].Url, "supplied base URL");
            Equal("Bearer env-openai", http.Requests[0].Headers["authorization"], "openai credentials applied");
            Equal("g1", http.Requests[0].Headers["x-gateway"], "model header"); Equal("1", http.Requests[0].Headers["x-extension"], "request header");
            Equal("my-decider", http.Requests[0].Json.GetProperty("model").GetString(), "supplied id");
            var image = System.Text.Json.Nodes.JsonNode.Parse("""
                {"type":"image","id":"house/painter","name":"Painter","api":"openrouter-images","provider":"openrouter","baseUrl":"https://images.example/api","input":["text"],"output":["image"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"headers":{"Authorization":"Bearer model-owned"}}
                """)!;
            var generated = await operations.GenerateImagesAsync(JsonData.Parse(image.ToJsonString()), DogPrompt, null, default);
            Equal(ModelOperationStopReason.Stop, generated.StopReason, generated.ErrorMessage ?? "custom image model");
            Equal("https://images.example/api/chat/completions", http.Requests[1].Url, "image base URL");
            // As in Pi, a model's own Authorization header replaces the bearer the provider key would add.
            Equal("Bearer model-owned", http.Requests[1].Headers["authorization"], "model authorization header wins");
            // A model's provider decides the credentials and the API implementations available to it.
            custom["api"] = "typesafe-system-one";
            Equal("Provider openai has no classifier implementation for \"typesafe-system-one\"",
                (await operations.ClassifyAsync(JsonData.Parse(custom.ToJsonString()), ApprovalContext, null, default)).ErrorMessage, "provider apis");
            Equal("Model openai/gpt-6-luna is not an image model", (await operations.GenerateImagesAsync(luna, DogPrompt, null, default)).ErrorMessage, "wrong type");
            Equal("Model openai/x is not a classifier model", (await operations.ClassifyAsync(JsonData.Parse(
                """{"id":"x","api":"openai-decisions","provider":"openai","baseUrl":"","input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}"""),
                ApprovalContext, null, default)).ErrorMessage, "untyped models are chat models");
            Equal("Unknown provider: ghost", (await operations.ClassifyAsync(JsonData.Parse(
                """{"type":"classifier","id":"x","api":"openai-decisions","provider":"ghost","baseUrl":"https://ghost.test","input":["text"],"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"contextWindow":1}"""),
                ApprovalContext, null, default)).ErrorMessage, "ghost");
            Equal("Model id must be a string.", (await operations.ClassifyAsync(JsonData.Parse("{}"), ApprovalContext, null, default)).ErrorMessage, "shape");
            Equal(2, http.Requests.Count, "only the two valid calls were sent");
        }),
        ("extension.hosts-without-the-capability-and-closed-callbacks-refuse", async () =>
        {
            var refused = false;
            try { await RunCommand(new ReadHost(), registry => { registry.GetModelsOfType(ModelType.Classifier); return Task.CompletedTask; }); }
            catch (NotSupportedException error) { refused = error.Message == "The admitted host has no model registry binding."; }
            Check(refused, "capability required");
            IExtensionModelOperationsFacade? escaped = null;
            await RunCommand(new ModelHost(ExtensionOperations(FakeHttp.Always(() => FakeHttp.Json("{}")))), registry => { escaped = registry; return Task.CompletedTask; });
            var closed = false;
            try { escaped!.GetModelsOfType(ModelType.Image); } catch (InvalidOperationException) { closed = true; }
            Check(closed, "the facade is valid only inside its callback");
        })
    ];
}
