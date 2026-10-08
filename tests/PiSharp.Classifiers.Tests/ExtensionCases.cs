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
        ("extension.models-resolve-by-identity-so-credentials-stay-on-catalog-endpoints", async () =>
        {
            var http = FakeHttp.Always(() => FakeHttp.Json("""{"answers":[{"type":"predicate","name":"approved","probability":0.8}]}"""));
            var operations = ExtensionOperations(http);
            var luna = operations.GetModelOfType(ModelType.Classifier, "openai", "gpt-6-luna")!;
            var moved = System.Text.Json.Nodes.JsonNode.Parse(luna.ToString())!.AsObject(); moved["baseUrl"] = "https://attacker.test/v1";
            moved["headers"] = new System.Text.Json.Nodes.JsonObject { ["authorization"] = "Bearer stolen" };
            var result = await operations.ClassifyAsync(JsonData.Parse(moved.ToJsonString()), ApprovalContext, null, default);
            Equal(ModelOperationStopReason.Stop, result.StopReason, result.ErrorMessage ?? "stop");
            Equal("https://api.openai.com/v1/decisions", http.Requests[0].Url, "catalog endpoint");
            Equal("Bearer env-openai", http.Requests[0].Headers["authorization"], "catalog auth");
            Equal("Model openai/gpt-6-luna is not an image model", (await operations.GenerateImagesAsync(luna, DogPrompt, null, default)).ErrorMessage, "wrong type");
            Equal("Unknown classifier model \"openai/nope\"", (await operations.ClassifyAsync(JsonData.Parse("""{"provider":"openai","id":"nope"}"""), ApprovalContext, null, default)).ErrorMessage, "unknown");
            Equal("Unknown provider: ghost", (await operations.ClassifyAsync(JsonData.Parse("""{"provider":"ghost","id":"x"}"""), ApprovalContext, null, default)).ErrorMessage, "ghost");
            Equal("Expected a classifier model with a provider and an id", (await operations.ClassifyAsync(JsonData.Parse("{}"), ApprovalContext, null, default)).ErrorMessage, "shape");
            Equal(1, http.Requests.Count, "only the resolved call was sent");
            Check(operations.GetModelsOfType(ModelType.Image, "openrouter").All(model => !model.Value.TryGetProperty("headers", out _)), "headers are not exposed");
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
