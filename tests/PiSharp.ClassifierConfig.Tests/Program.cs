using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.AI.ModelOperations;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Extensions.Pi;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Models;
using PiSharp.Contracts;
using PiSharp.Contracts.ModelOperations;
using PiSharp.Extensions.Facade.Context;
using PiSharp.Extensions;

// Authored from provider-composer.ts in v1.5.0 f9a2636 and 2026-11-08 7d073328.
// Real HTTP requests terminate at a loopback TCP server; no environment credentials or external providers are read.
internal static partial class Program
{
    private static readonly ClassifierContext Context = new(JsonData.Parse("{\"task\":\"Inspect this image\"}"),
        [KeyValuePair.Create<string, ClassifierQuestion>("safe", new ClassifierBoolQuestion("Is this safe?", "safe", "unsafe"))],
        [new("cG5n", "image/png")]);

    private static async Task Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-classifier-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ProviderRequests(root);
            await ReloadSnapshots(root);
            await HostReloadAndExtensions(root);
            await CredentialOwnership(root);
            await CopilotClassifier(root);
            Console.WriteLine("PASS: classifier provider configuration, loopback requests, reload snapshots, auth ownership, chat-only overrides, extension providers");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static JsonObject Provider(string url, string key, string imageInput = "state-image-urls") => new()
    {
        ["baseUrl"] = url, ["apiKey"] = key,
        ["headers"] = new JsonObject { ["X-Configured"] = "config-${MARKER}" },
        ["compat"] = new JsonObject { ["imageInput"] = imageInput, ["sendImageOptInHeader"] = false }
    };

    private static void Write(string path, params (string Id, JsonObject Config)[] providers)
    {
        var obj = new JsonObject();
        foreach (var (id, config) in providers) obj[id] = config;
        File.WriteAllText(path, new JsonObject { ["providers"] = obj }.ToJsonString());
    }

    private static string? Environment(string name) => name switch
    {
        "MARKER" => "local", "CONFIG_KEY" => "configured-key", "TYPESAFE_API_KEY" => "environment-key", _ => null
    };

    private static ClassifierModel Model(ModelOperationsRegistry registry, string provider = "typesafe", string id = "jev-latest") =>
        (ClassifierModel)(registry.GetModelOfType(ModelType.Classifier, provider, id) ?? throw new InvalidOperationException("Missing classifier " + provider + "/" + id));

    private static async Task ProviderRequests(string root)
    {
        await using var server = new Loopback();
        var path = Path.Combine(root, "models.json");
        var typesafe = Provider(server.Url + "/typesafe/v1", "${CONFIG_KEY}");
        typesafe["modelOverrides"] = new JsonObject { ["jev-latest"] = new JsonObject { ["name"] = "chat override", ["compat"] = new JsonObject { ["sendImageOptInHeader"] = true } } };
        typesafe["models"] = new JsonArray(new JsonObject { ["id"] = "jev-latest", ["api"] = "openai-completions", ["name"] = "chat twin" });
        Write(path, ("typesafe", typesafe), ("openrouter", Provider(server.Url + "/router/v1", "router-key")));
        var registry = NativeExtensionModelOperations.CreateDefaultRegistry(Environment, null, modelsPath: path);
        var model = Model(registry);
        Check(model.BaseUrl == server.Url + "/typesafe/v1" && model.Api == TypeSafeSystemOneClassifier.ApiId, "provider URL applies; chat definitions preserve classifier API");
        Check(model.ToJson().Value.GetProperty("name").GetString() != "chat override", "modelOverrides are chat-only");
        Check(registry.GetModelsOfType(ModelType.Chat, "typesafe").Any(m => m.Id == "jev-latest"), "same-id chat model is separately composed");
        Check((await registry.GetAvailableOfTypeAsync(ModelType.Classifier, "typesafe")).Length > 0, "configured key participates in availability");
        var pending = server.Receive();
        var result = await new NativeExtensionModelOperations(registry).ClassifyAsync(model.ToJson(), Context,
            new ExtensionModelRequestOptions { MaxRetries = 0 }, default);
        var request = await pending;
        Check(result.StopReason == ModelOperationStopReason.Stop, result.ErrorMessage ?? "classification failed");
        Check(request.Path == "/typesafe/v1/systemone" && request.Headers["authorization"] == "Bearer configured-key", "configured URL and key reach loopback together");
        Check(request.Headers["x-configured"] == "config-local" && !request.Headers.ContainsKey("typesafe-danger-zone-images"), "configured headers and classifier opt-in override reach HTTP");
        Check(request.Body.Contains("\"type\":\"image_url\"") && !request.Body.Contains("\"assets\""), "provider imageInput changes real wire body");
        var router = Model(registry, "openrouter", "~typesafe/jev-latest");
        pending = server.Receive();
        result = await registry.ClassifyAsync(router, Context, new ClassifierOptions { MaxRetries = 0 });
        request = await pending;
        Check(result.StopReason == ModelOperationStopReason.Stop && request.Path == "/router/v1/systemone" && request.Headers["authorization"] == "Bearer router-key", "another provider retains its own route and credential");
        Write(path, ("typesafe", new JsonObject { ["baseUrl"] = server.Url + "/detected/v1", ["apiKey"] = "detection-key" }));
        registry = NativeExtensionModelOperations.CreateDefaultRegistry(Environment, null, modelsPath: path);
        pending = server.Receive();
        result = await registry.ClassifyAsync(Model(registry), Context, new ClassifierOptions { MaxRetries = 0 });
        request = await pending;
        Check(result.StopReason == ModelOperationStopReason.Stop && request.Headers["typesafe-danger-zone-images"] == "true" && request.Body.Contains("\"assets\""), "omitted compat preserves provider detection");
    }

    private static async Task HostReloadAndExtensions(string root)
    {
        var path = Path.Combine(root, "host-models.json");
        Write(path, ("typesafe", Provider("http://127.0.0.1:1/first", "host-key")));
        await using var host = PiExtensionHost.CreateWithoutNode(new(root, root, "test", false) { GetEnvironment = Environment });
        var runtime = new LiveSessionRuntime(Environment, () => null, ModelsPath: path) { LlamaProvider = () => false };
        var models = await PiExtensionModels.CreateAsync(host, runtime, default);
        Check(Model(models.Operations).BaseUrl.EndsWith("/first", StringComparison.Ordinal), "normal Pi run loads configured classifiers");
        Write(path, ("typesafe", Provider("http://127.0.0.1:1/reloaded", "host-key")));
        await host.ReloadExtensionsAsync(default);
        Check(Model(models.Operations).BaseUrl.EndsWith("/reloaded", StringComparison.Ordinal), "extension reload refreshes model configuration");
        var customModel = Model(models.Operations).ToJson().ToString().Replace("\"typesafe\"", "\"custom-provider\"", StringComparison.Ordinal);
        host.NativeModelProviders.Register("test-owner", new ExtensionModelOperationProvider("custom-provider")
        {
            ApiKey = "custom-key", Models = [JsonData.Parse(customModel)],
            Classifiers = ImmutableDictionary<string, ExtensionClassifierImplementation>.Empty.Add(TypeSafeSystemOneClassifier.ApiId,
                (model, _, options, _) => Task.FromResult(new ClassifierResult(TypeSafeSystemOneClassifier.ApiId, "custom-provider", "jev-latest", [], ModelOperationStopReason.Stop, 0)
                { ErrorMessage = options?.ApiKey }))
        });
        Check((await models.Operations.GetAvailableOfTypeAsync(ModelType.Classifier, "custom-provider")).Length == 1, "registered custom provider stays available");
        var result = await models.Operations.ClassifyAsync(Model(models.Operations, "custom-provider"), Context);
        Check(result.StopReason == ModelOperationStopReason.Stop && result.ErrorMessage == "custom-key", "extension implementation keeps its own configured credential");
        host.NativeModelProviders.UnregisterOwner("test-owner");
        Check(models.Operations.GetProvider("custom-provider") is null && Model(models.Operations).BaseUrl.EndsWith("/reloaded", StringComparison.Ordinal), "extension removal keeps configured builtins");
    }

    private static async Task ReloadSnapshots(string root)
    {
        await using var server = new Loopback();
        var path = Path.Combine(root, "reload.json");
        Write(path, ("typesafe", Provider(server.Url + "/before/v1", "before-key")));
        var chat = ModelRegistry.Create(new() { ModelsPath = path, Environment = Environment });
        var registry = NativeExtensionModelOperations.CreateDefaultRegistry(Environment, null, null, null, null, chat);
        var before = Model(registry);
        Write(path, ("typesafe", Provider(server.Url + "/after/v1", "after-key", "assets")));
        Check(Model(registry).BaseUrl == before.BaseUrl, "edits wait for explicit registry refresh");
        await chat.RefreshAsync(false, null, null, default);
        var after = Model(registry);
        Check(after.BaseUrl == server.Url + "/after/v1", "run-bound registry observes refreshed provider config");
        var pending = server.Receive();
        var result = await registry.ClassifyAsync(before, Context, new ClassifierOptions { MaxRetries = 0 });
        var request = await pending;
        Check(result.StopReason == ModelOperationStopReason.Stop && request.Path == "/before/v1/systemone" && request.Headers["authorization"] == "Bearer after-key" && request.Body.Contains("\"image_url\""), "held model keeps route/compat; credentials resolve from current snapshot");
        pending = server.Receive();
        result = await registry.ClassifyAsync(after, Context, new ClassifierOptions { MaxRetries = 0 });
        request = await pending;
        Check(result.StopReason == ModelOperationStopReason.Stop && request.Path == "/after/v1/systemone" && request.Body.Contains("\"assets\""), "fresh model uses refreshed URL and image form");
    }

    private static async Task CredentialOwnership(string root)
    {
        await using var server = new Loopback();
        var path = Path.Combine(root, "auth-models.json");
        var auth = Path.Combine(root, "auth.json");
        Write(path, ("typesafe", Provider(server.Url + "/auth/v1", "configured-key")));
        File.WriteAllText(auth, "{\"typesafe\":{\"type\":\"api_key\",\"key\":\"stored-key\"}}");
        var registry = NativeExtensionModelOperations.CreateDefaultRegistry(Environment, auth, modelsPath: path);
        foreach (var key in new string?[] { null, "explicit-key" })
        {
            var pending = server.Receive();
            var result = await registry.ClassifyAsync(Model(registry), Context, new ClassifierOptions { ApiKey = key, MaxRetries = 0 });
            var request = await pending;
            Check(result.StopReason == ModelOperationStopReason.Stop && request.Headers["authorization"] == "Bearer " + (key ?? "stored-key"), "explicit/stored keys own auth ahead of configured/environment keys");
        }
        File.WriteAllText(auth, "{\"typesafe\":{\"type\":\"unsupported\"}}");
        var failure = await registry.ClassifyAsync(Model(registry), Context, new ClassifierOptions { MaxRetries = 0 });
        Check(failure.StopReason == ModelOperationStopReason.Error && failure.ErrorMessage!.Contains("not configured"), "unsupported stored credential does not fall through to configured/environment key");
        Write(path, ("typesafe", Provider(server.Url + "/auth/v1", "${MISSING}")));
        registry = NativeExtensionModelOperations.CreateDefaultRegistry(Environment, null, modelsPath: path);
        Check((await registry.GetAvailableOfTypeAsync(ModelType.Classifier, "typesafe")).IsEmpty, "unresolved configured key blocks environment fallback");
    }
}

internal sealed record Request(string Path, Dictionary<string, string> Headers, string Body);

internal sealed class Loopback : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    public string Url { get; }
    public Loopback() { listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port; }
    public async Task<Request> Receive()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var line = await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Missing request");
        var path = line.Split(' ')[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while ((line = await reader.ReadLineAsync(timeout.Token)) is { Length: > 0 })
        { var colon = line.IndexOf(':'); headers[line[..colon]] = line[(colon + 1)..].Trim(); }
        var body = new char[int.Parse(headers["Content-Length"], System.Globalization.CultureInfo.InvariantCulture)];
        if (await reader.ReadBlockAsync(body.AsMemory(), timeout.Token) != body.Length) throw new IOException("Truncated request body");
        const string response = "{\"answers\":{\"safe\":{\"type\":\"noul\",\"noul\":0.99}}}";
        var data = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\nContent-Length: " + Encoding.UTF8.GetByteCount(response) + "\r\n\r\n" + response);
        await stream.WriteAsync(data, timeout.Token);
        return new(path, headers, new string(body));
    }
    public ValueTask DisposeAsync() { listener.Stop(); return ValueTask.CompletedTask; }
}
