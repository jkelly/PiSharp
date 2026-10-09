using System.Text.Json.Nodes;
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;

// Owner follow-up: OAuth for extension providers. Expectations follow core/provider-composer.ts (adaptOAuth: login callbacks over the
// auth interaction, refreshToken, getApiKey, modifyModels; the provider name falls back to oauth.name; OAuth-only providers get no
// API-key login), core/model-runtime.ts (login stores { type: "oauth", ... } in auth.json; requests resolve the stored credential,
// refreshing it when it expires) and core/extensions/types.ts (ProviderConfig.oauth).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> OAuthCases() =>
    [
        ("oauth.extension-provider-login-refresh-and-models", ExtensionProviderOAuth),
    ];

    private const string OAuthProviderExtension = """
        import { createAssistantMessageEventStream } from "@earendil-works/pi-ai";
        import { appendFileSync } from "node:fs";
        const log = (...items: unknown[]) => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(items) + "\n");
        export default function (pi: any) {
          pi.registerProvider("corp", {
            baseUrl: "https://corp.invalid/v1", api: "corp-api",
            models: [{ id: "m1", name: "Corp One", reasoning: false, input: ["text"], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 8000, maxTokens: 1000 }],
            oauth: {
              name: "Corp SSO",
              async login(callbacks: any) {
                callbacks.onAuth({ url: "https://corp.invalid/authorize", instructions: "Sign in with Corp" });
                callbacks.onProgress?.("waiting for the code");
                const code = await callbacks.onPrompt({ message: "Enter the code", placeholder: "1234" });
                const tenant = await callbacks.onSelect({ message: "Tenant", options: [{ id: "t1", label: "Tenant One" }, { id: "t2", label: "Tenant Two" }] });
                return { access: "acc-" + code, refresh: "ref-" + code, expires: Date.now() + 3600_000, tenant };
              },
              async refreshToken(credentials: any) { log("refresh", credentials.refresh); return { ...credentials, access: "acc-refreshed", expires: Date.now() + 3600_000 }; },
              getApiKey(credentials: any) { return "key-" + credentials.access + "-" + credentials.tenant; },
              modifyModels(models: any[], credentials: any) { return [...models, { ...models[0], id: "m1-" + credentials.tenant, name: "Corp Tenant" }]; },
            },
            streamSimple(model: any, context: any, options: any) {
              const stream = createAssistantMessageEventStream();
              log("stream", model.id, options?.apiKey ?? null);
              const output: any = { role: "assistant", content: [], api: model.api, provider: model.provider, model: model.id,
                usage: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0, totalTokens: 2, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } },
                stopReason: "stop", timestamp: Date.now() };
              queueMicrotask(() => {
                stream.push({ type: "start", partial: output });
                output.content.push({ type: "text", text: "corp ok" });
                stream.push({ type: "text_start", contentIndex: 0, partial: output });
                stream.push({ type: "text_delta", contentIndex: 0, delta: "corp ok", partial: output });
                stream.push({ type: "text_end", contentIndex: 0, content: "corp ok", partial: output });
                stream.push({ type: "done", reason: "stop", message: output });
                stream.end();
              });
              return stream;
            },
          });
        }
        """;

    private sealed class ScriptedInteraction : IProviderAuthInteraction
    {
        internal List<string> Seen { get; } = [];
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken)
        {
            lock (Seen) Seen.Add("prompt " + prompt.Kind + " " + prompt.Message + (prompt.Options is { } options ? " [" + string.Join(",", options.Select(option => option.Id + "=" + option.Label)) + "]" : ""));
            return Task.FromResult(prompt.Kind == AuthPromptKind.Select ? "t1" : "1234");
        }
        public void Notify(AuthEvent authEvent) { lock (Seen) Seen.Add("event " + authEvent.Kind + " " + (authEvent.Url ?? authEvent.Message)); }
    }

    // /login with an extension provider's oauth: the login runs in the extension with the host's interaction and auth.json keeps the
    // credential; requests use getApiKey of the stored credential, refreshed when it expired; modifyModels adds the tenant model.
    private static async Task ExtensionProviderOAuth()
    {
        RequirePiRuntime();
        using var sandbox = NodeSandbox("provider-oauth");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "corp.ts"), OAuthProviderExtension);
        var authPath = Path.Combine(sandbox.AgentDir, "auth.json");
        await using (var host = await StartHost(sandbox, extension))
        {
            await WaitUntil(() => !host.ProviderRegistrations.IsEmpty);
            ProviderAuthCatalog.Extensions = () => host.OAuthEntries();
            try
            {
                var options = ProviderAuthCatalog.LoginOptions().Where(option => option.Provider.Id == "corp").ToList();
                Names(["oauth"], options.Select(option => option.AuthType), "an OAuth-only provider has no API-key login");
                Equal("Corp SSO", options[0].Name, "provider name falls back to oauth.name");
                var interaction = new ScriptedInteraction();
                await new ProviderLoginHost(new AuthJsonCredentialStore(authPath), () => new HttpClient()).LoginAsync(options[0], interaction, CancellationToken.None);
                Names(["event AuthUrl https://corp.invalid/authorize", "event Progress waiting for the code", "prompt Text Enter the code",
                    "prompt Select Tenant [t1=Tenant One,t2=Tenant Two]"], interaction.Seen, "login callbacks");
            }
            finally { ProviderAuthCatalog.Extensions = null; }
        }
        var stored = JsonNode.Parse(File.ReadAllText(authPath))!["corp"]!;
        Equal("oauth", stored["type"]!.GetValue<string>(), "stored type");
        Equal("acc-1234", stored["access"]!.GetValue<string>(), "stored access");
        Equal("t1", stored["tenant"]!.GetValue<string>(), "the flow's other fields are kept");

        var (code, stdout, stderr) = await sandbox.Run("-p", "--provider", "corp", "--model", "m1", "-e", extension, "hello");
        Equal(0, code, "exit; " + stderr);
        Equal("corp ok", stdout.Trim(), "answered by the provider");
        Equal("""["stream","m1","key-acc-1234-t1"]""", LogLines(sandbox).Last(), "the request uses getApiKey of the stored credential");

        (code, _, stderr) = await sandbox.Run("-p", "--provider", "corp", "--model", "m1-t1", "-e", extension, "tenant");
        Equal(0, code, "modifyModels model exit; " + stderr);
        Equal("""["stream","m1-t1","key-acc-1234-t1"]""", LogLines(sandbox).Last(), "modifyModels added the tenant model");

        var expired = JsonNode.Parse(File.ReadAllText(authPath))!.AsObject();
        expired["corp"]!["expires"] = 0;
        File.WriteAllText(authPath, expired.ToJsonString());
        (code, _, stderr) = await sandbox.Run("-p", "--provider", "corp", "--model", "m1", "-e", extension, "again");
        Equal(0, code, "refresh exit; " + stderr);
        Check(LogLines(sandbox).Contains("""["refresh","ref-1234"]"""), "refreshToken ran: " + string.Join("|", LogLines(sandbox)));
        Equal("""["stream","m1","key-acc-refreshed-t1"]""", LogLines(sandbox).Last(), "the refreshed credential's key");
        Equal("acc-refreshed", JsonNode.Parse(File.ReadAllText(authPath))!["corp"]!["access"]!.GetValue<string>(), "the rotation is persisted");
    }
}
