using System.Globalization;
using System.Net;
using System.Text;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.AI.ModelOperations;
using PiSharp.Cli.Extensions;
using PiSharp.Contracts.ModelOperations;

internal static partial class Program
{
    // F09: authored from 7d073328 github-copilot.ts copilotSystemOne and github-copilot.models.ts.
    // Fake HTTP runs the real stored-OAuth refresh (GitHub token -> Copilot token -> model discovery).
    private static async Task CopilotClassifier(string root)
    {
        var authPath = Path.Combine(root, "copilot-auth.json");
        var modelsPath = Path.Combine(root, "copilot-models.json");
        Write(modelsPath, ("github-copilot", Provider("https://configured.invalid/copilot", "configured-copilot")));
        void Save(string allowed) => File.WriteAllText(authPath,
            "{\"github-copilot\":{\"type\":\"oauth\",\"access\":\"stale\",\"refresh\":\"github-token\",\"expires\":1,\"availableModelIds\":[\"" + allowed + "\"]}}");
        Save("chat-only");
        const string freshToken = "tid=fake;proxy-ep=proxy.enterprise.test;";
        var expires = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var authHttp = new CopilotHttp(request => request.Url.EndsWith("/copilot_internal/v2/token", StringComparison.Ordinal)
            ? "{\"token\":\"" + freshToken + "\",\"expires_at\":" + expires.ToString(CultureInfo.InvariantCulture) + "}"
            : "{\"data\":[{\"id\":\"microsoft-decision-1\",\"model_picker_enabled\":true}]}");
        var registry = NativeExtensionModelOperations.CreateDefaultRegistry(Environment, authPath,
            createAuthHttp: () => new HttpClient(authHttp, disposeHandler: false), modelsPath: modelsPath);
        var model = Model(registry, "github-copilot", "microsoft-decision-1");
        Check(model.Api == TypeSafeSystemOneClassifier.ApiId && model.ContextWindow == 32768 && model.Cost.Input == 0.042,
            "reviewed Copilot classifier is loaded with exact protocol and billing metadata");
        Check((await registry.GetAvailableOfTypeAsync(ModelType.Classifier, "github-copilot")).IsEmpty && authHttp.Requests.Count == 0,
            "Copilot availability filters stored allowed ids without refreshing");
        Save("microsoft-decision-1");
        var classification = new CopilotHttp(_ => "{\"answers\":{\"safe\":{\"type\":\"noul\",\"noul\":0.99}},\"usage\":{\"input_tokens\":1000,\"output_tokens\":10}}");
        using var client = new HttpClient(classification, disposeHandler: false);
        var result = await registry.ClassifyAsync(model, Context with { Images = [] }, new ClassifierOptions
        {
            Http = client, MaxRetries = 0, Headers = [new("Editor-Version", "test-editor")]
        });
        Check(result.StopReason == ModelOperationStopReason.Stop, result.ErrorMessage ?? "Copilot classification failed");
        Check(authHttp.Requests.Count == 2 && authHttp.Requests[0].Url == "https://api.github.com/copilot_internal/v2/token" &&
            authHttp.Requests[0].Headers["Authorization"] == "Bearer github-token" && authHttp.Requests[1].Url == "https://api.enterprise.test/models",
            "expired Copilot OAuth exchanges GitHub refresh token and fetches the account model list");
        var sent = classification.Requests.Single();
        Check(sent.Url == "https://api.enterprise.test/v1/systemone" && sent.Headers["Authorization"] == "Bearer " + freshToken,
            "account endpoint wins configured URL; classifier adds /v1 after auth routing");
        Check(sent.Headers["Editor-Version"] == "test-editor" && sent.Headers["Editor-Plugin-Version"] == "copilot-chat/0.35.0" &&
            sent.Headers["Copilot-Integration-Id"] == "vscode-chat" && sent.Headers["User-Agent"] == "GitHubCopilotChat/0.35.0",
            "Copilot static headers survive and explicit request header wins");
        Check(sent.Body.Contains("\"questions\"") && !sent.Body.Contains("\"messages\"") &&
            Math.Abs((double)result.Usage!.Cost.Input - 0.000042) < 1e-12, "Copilot classifier uses System One body and reviewed cost");
        Check((await registry.GetAvailableOfTypeAsync(ModelType.Classifier, "github-copilot")).Length == 1 && authHttp.Requests.Count == 2,
            "refreshed allowed-id list persists and listing does not exchange tokens");
        var persisted = await File.ReadAllTextAsync(authPath);
        Check(persisted.Contains(freshToken) && persisted.Contains("microsoft-decision-1"), "refreshed Copilot token and model list persist");
        await registry.ClassifyAsync(model, Context with { Images = [] }, new ClassifierOptions { Http = client, ApiKey = "explicit-copilot", MaxRetries = 0 });
        sent = classification.Requests.Last();
        Check(sent.Url == "https://configured.invalid/copilot/v1/systemone" && sent.Headers["Authorization"] == "Bearer explicit-copilot" && authHttp.Requests.Count == 2,
            "explicit key bypasses OAuth and keeps configured classifier route");
        var envRegistry = NativeExtensionModelOperations.CreateDefaultRegistry(name => name == "COPILOT_GITHUB_TOKEN" ? "environment-copilot" : null, null);
        await envRegistry.ClassifyAsync(Model(envRegistry, "github-copilot", "microsoft-decision-1"), Context with { Images = [] },
            new ClassifierOptions { Http = client, MaxRetries = 0 });
        sent = classification.Requests.Last();
        Check(sent.Url == "https://api.individual.githubcopilot.com/v1/systemone" && sent.Headers["Authorization"] == "Bearer environment-copilot",
            "environment Copilot token uses catalog endpoint plus /v1");
        var rejected = await envRegistry.ClassifyAsync(Model(envRegistry, "github-copilot", "microsoft-decision-1"), Context,
            new ClassifierOptions { Http = client, MaxRetries = 0 });
        Check(rejected.StopReason == ModelOperationStopReason.Error && classification.Requests.Count == 3,
            "text-only Copilot classifier rejects image input before HTTP");
    }

    private sealed record CopilotRequest(string Url, Dictionary<string, string> Headers, string Body);
    private sealed class CopilotHttp(Func<CopilotRequest, string> response) : HttpMessageHandler
    {
        internal List<CopilotRequest> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var recorded = new CopilotRequest(request.RequestUri!.AbsoluteUri,
                request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(" ", pair.Value), StringComparer.OrdinalIgnoreCase),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(token));
            Requests.Add(recorded);
            return new(HttpStatusCode.OK) { Content = new StringContent(response(recorded), Encoding.UTF8, "application/json") };
        }
    }
}
