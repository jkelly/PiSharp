using System.Text.Json.Nodes;
using System.Net;
using System.Text;
using PiSharp.Samples;
using PiSharp.Samples.ProductContentStudio;
if (args.Contains("--self-test", StringComparer.Ordinal)) {
    var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture.json")))!.AsObject();
    var workspace = Path.Combine(Path.GetTempPath(), "pisharp-productcontentstudio-" + Guid.NewGuid().ToString("N"));
    try { var context = new SampleContext(workspace, fixture); var spec = Workflow.Create(context);
        foreach (var call in spec.DemoCalls) { var tool = spec.Tools.Single(t => t.Name == call.Tool); await tool.Execute(call.Arguments, CancellationToken.None); }
        if (!Directory.EnumerateFiles(workspace).Any()) throw new Exception("No artifact was written.");
        var imageInput = (JsonObject)fixture.DeepClone(); imageInput["generateImage"] = true;
        var imageContext = new SampleContext(Path.Combine(workspace, "image-test"), imageInput, approved: true)
        { Provider = "openrouter", Model = "test/image", BaseUrl = "https://fake.test/v1" };
        using var fake = new FakeImageHandler();
        var generated = await Workflow.GenerateImageAsync(imageContext, "offline-test-key", fake);
        var imagePath = imageContext.ResolvePath("product-image.png");
        var bytes = File.Exists(imagePath) ? await File.ReadAllBytesAsync(imagePath) : [];
        if (!fake.Called || generated["generatedImage"]?.GetValue<bool>() != true ||
            bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new Exception("Image adapter regression failed.");
        Console.WriteLine("PASS ProductContentStudio: fixture tools and fake HTTP image generation"); return 0;
    } finally { Directory.Delete(workspace, true); }
}
if (args.Contains("--generate-image", StringComparer.Ordinal)) {
    try {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++) {
            if (args[i] is "--generate-image" or "--approve") { values.Add(args[i], "true"); continue; }
            if (args[i] is "--provider" or "--model" or "--key-env" or "--base-url" or "--input" or "--workspace" or "--timeout") {
                if (++i >= args.Length) throw new ArgumentException("Missing option value.");
                values.Add(args[i - 1], args[i]); continue;
            }
            throw new ArgumentException("Unknown option for image generation: " + args[i]);
        }
        if (!values.ContainsKey("--approve")) throw new ArgumentException("Image generation requires --approve.");
        if (values.GetValueOrDefault("--provider") != "openrouter" || string.IsNullOrWhiteSpace(values.GetValueOrDefault("--model")))
            throw new ArgumentException("Use --provider openrouter --model IMAGE_MODEL.");
        var inputPath = values.GetValueOrDefault("--input", Path.Combine(AppContext.BaseDirectory, "fixture.json"));
        var input = JsonNode.Parse(await File.ReadAllTextAsync(inputPath))?.AsObject() ?? throw new ArgumentException("Input must be a JSON object.");
        input["generateImage"] = true;
        var workspace = values.GetValueOrDefault("--workspace", Path.Combine(Path.GetTempPath(), "pisharp-product-image", Guid.NewGuid().ToString("N")));
        var context = new SampleContext(workspace, input, approved: true) {
            Provider = "openrouter", Model = values["--model"],
            BaseUrl = values.GetValueOrDefault("--base-url", "https://openrouter.ai/api/v1"),
            KeyEnvironment = values.GetValueOrDefault("--key-env", "OPENROUTER_API_KEY")
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(values.GetValueOrDefault("--timeout", "120"), System.Globalization.CultureInfo.InvariantCulture)));
        var result = await SampleHost.ExecuteAsync(Workflow.Create(context), context, timeout.Token);
        result.Report["mode"] = "fixture-agent/live-image-provider";
        await context.WriteJsonAsync("report.json", result.Report, timeout.Token);
        Console.WriteLine(result.Report.ToJsonString(SampleContext.JsonOptions));
        return result.Success ? 0 : 1;
    } catch (Exception error) { Console.Error.WriteLine(SecretRedactor.Redact(error.Message)); return 1; }
}
return await SampleHost.RunAsync(args, Workflow.Create, Path.Combine(AppContext.BaseDirectory, "fixture.json"));

sealed class FakeImageHandler : HttpMessageHandler {
    public bool Called { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        Called = true;
        if (request.RequestUri?.AbsoluteUri != "https://fake.test/v1/chat/completions" ||
            request.Headers.Authorization?.Scheme != "Bearer" ||
            request.Headers.Authorization?.Parameter != "offline-test-key")
            throw new Exception("Unexpected image API request.");
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        if (!body.Contains("test/image", StringComparison.Ordinal) || !body.Contains("A reusable mug", StringComparison.Ordinal))
            throw new Exception("Image request payload is missing model or brief.");
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ZkAAAAASUVORK5CYII=";
        var response = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject {
            ["images"] = new JsonArray(new JsonObject { ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + png } })
        } }) };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
