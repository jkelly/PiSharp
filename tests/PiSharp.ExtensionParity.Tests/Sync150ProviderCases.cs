// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/ai/src/auth/types.ts (AuthEvent device_code openBrowserOnConfirm and
// copyCodeOnConfirm), packages/ai/src/compat/extension-oauth-types.ts (OAuthDeviceCodeInfo) and packages/coding-agent/src/core/
// provider-composer.ts (adaptOAuth: onDeviceCode notifies { type: "device_code", ...info }).
using PiSharp.AI.Authentication.OAuth;
using PiSharp.Cli.Authentication;

// Pi 1.5.0 sync (ai-providers). Authored expectations derived by reading the Pi v1.5.0 sources above; nothing here is an upstream capture.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150ProviderCases() =>
    [
        ("sync150.oauth-device-code-confirm-options-reach-the-auth-event", ExtensionDeviceCodeConfirmOptions),
    ];

    private const string DeviceCodeExtension = """
        export default function (pi: any) {
          pi.registerProvider("corp", {
            baseUrl: "https://corp.invalid/v1", api: "corp-api",
            models: [{ id: "m1", name: "Corp One", reasoning: false, input: ["text"], cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 }, contextWindow: 8000, maxTokens: 1000 }],
            oauth: {
              name: "Corp Device",
              async login(callbacks: any) {
                callbacks.onDeviceCode({ userCode: "ABCD-1234", verificationUri: "https://corp.invalid/device", openBrowserOnConfirm: false, copyCodeOnConfirm: false, intervalSeconds: 5 });
                callbacks.onDeviceCode({ userCode: "EFGH-5678", verificationUri: "https://corp.invalid/device2", openBrowserOnConfirm: true });
                callbacks.onDeviceCode({ userCode: "IJKL-9012", verificationUri: "https://corp.invalid/device3", copyCodeOnConfirm: "no" });
                return { access: "acc", refresh: "ref", expires: Date.now() + 3600_000 };
              },
              async refreshToken(credentials: any) { return credentials; },
              getApiKey(credentials: any) { return "key-" + credentials.access; },
            },
          });
        }
        """;

    private sealed class DeviceCodeInteraction : IProviderAuthInteraction
    {
        internal List<AuthEvent> Events { get; } = [];
        public Task<string> PromptAsync(AuthPrompt prompt, CancellationToken cancellationToken) => Task.FromResult("");
        public void Notify(AuthEvent authEvent) { lock (Events) Events.Add(authEvent); }
    }

    // onDeviceCode info carries the two optional confirm flags into the device_code AuthEvent; absent (or not a boolean) stays absent,
    // which clients read as true.
    private static async Task ExtensionDeviceCodeConfirmOptions()
    {
        RequirePiRuntime();
        using var sandbox = NodeSandbox("provider-device-code");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "corp.ts"), DeviceCodeExtension);
        await using var host = await StartHost(sandbox, extension);
        ProviderAuthCatalog.Extensions = () => host.OAuthEntries();
        try
        {
            var option = ProviderAuthCatalog.LoginOptions().Single(item => item.Provider.Id == "corp");
            var interaction = new DeviceCodeInteraction();
            await new ProviderLoginHost(new AuthJsonCredentialStore(Path.Combine(sandbox.AgentDir, "auth.json")), () => new HttpClient())
                .LoginAsync(option, interaction, CancellationToken.None);
            Names(["DeviceCode ABCD-1234 https://corp.invalid/device False False 5", "DeviceCode EFGH-5678 https://corp.invalid/device2 True - -",
                "DeviceCode IJKL-9012 https://corp.invalid/device3 - - -"],
                interaction.Events.Select(item => $"{item.Kind} {item.UserCode} {item.VerificationUri} {Flag(item.OpenBrowserOnConfirm)} {Flag(item.CopyCodeOnConfirm)} {item.IntervalSeconds?.ToString() ?? "-"}"),
                "device code events");
        }
        finally { ProviderAuthCatalog.Extensions = null; }
        static string Flag(bool? value) => value?.ToString() ?? "-";
    }
}
