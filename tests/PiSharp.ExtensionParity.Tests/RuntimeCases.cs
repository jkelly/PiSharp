using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PiSharp.Cli.Extensions.Pi;

// The Pi packages extensions run against (owner decision: install the exact Pi 1.1.0 packages with the user's npm on first extension
// use and load extensions with Pi's own jiti and getAliases; PiSharp's compatibility modules only offline or without npm).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> RuntimeCases() =>
    [
        ("runtime.pi-packages-installed-with-verified-provenance", InstalledProvenance),
        ("runtime.tsx-extension-through-pi-jiti-and-real-packages", RealPackagesTsx),
        ("runtime.offline-fallback-is-reported", OfflineFallback),
        ("runtime.integrity-mismatch-rejects-the-install", IntegrityMismatch),
    ];

    private static Task InstalledProvenance()
    {
        var modules = RequirePiRuntime();
        var provenance = JsonNode.Parse(File.ReadAllText(Path.Combine(SharedPiRuntimeDirectory, PiNodeRuntime.ProvenanceFile)))!;
        Equal("1.1.0", provenance["pi"]!.GetValue<string>(), "Pi version");
        Equal(true, provenance["verified"]!.GetValue<bool>(), "verified");
        Equal(SharedPiRuntimeDirectory, provenance["directory"]!.GetValue<string>(), "install directory");
        var packages = provenance["packages"]!.AsArray().Select(item => item!).ToList();
        string Version(string name) => packages.Single(item => item["name"]!.GetValue<string>() == name)["version"]!.GetValue<string>();
        foreach (var name in new[] { "@earendil-works/pi-coding-agent", "@earendil-works/pi-ai", "@earendil-works/pi-tui", "@earendil-works/pi-agent-core" })
            Equal("1.1.0", Version(name), name);
        Equal("1.3.27", Version("typebox"), "typebox");
        Equal("2.7.0", Version("jiti"), "jiti");
        Check(packages.All(item => item["integrity"]!.GetValue<string>().StartsWith("sha512-", StringComparison.Ordinal) || item["integrity"]!.GetValue<string>().StartsWith("sha1-", StringComparison.Ordinal)), "integrity hashes");
        Check(File.Exists(Path.Combine(modules, "@earendil-works", "pi-coding-agent", "dist", "core", "extensions", "loader.js")), "coding agent installed");
        // The installer's own pins: every @earendil-works package of the release at exactly 1.1.0.
        var manifest = PiNodeRuntime.PackageJson();
        Equal("1.1.0", manifest["dependencies"]!["@earendil-works/pi-coding-agent"]!.GetValue<string>(), "pinned coding agent");
        Check(packages.Where(item => item["name"]!.GetValue<string>().StartsWith("@earendil-works/", StringComparison.Ordinal)).All(item => item["version"]!.GetValue<string>() == "1.1.0"), "every Pi package at 1.1.0");
        return Task.CompletedTask;
    }

    // loader.ts loadExtensionModule: jiti transforms .tsx (non-erasable syntax such as enums included) and resolves Pi's packages through
    // getAliases to the installed dist; ctx.ui.theme is the coding agent's own Theme.
    private static async Task RealPackagesTsx()
    {
        RequirePiRuntime();
        using var sandbox = NodeSandbox("real-tsx");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "real.tsx"), """
            import { VERSION, Theme, getAgentDir } from "@earendil-works/pi-coding-agent";
            import { Type } from "@sinclair/typebox";
            import { Text } from "@mariozechner/pi-tui";
            import { appendFileSync } from "node:fs";
            enum Kind { Probe = "probe" }
            const first = <T,>(items: T[]): T => items[0];
            export default function (pi: any) {
              pi.registerCommand("probe", { description: "Probe", handler: async (_args: string, ctx: any) => {
                appendFileSync(process.cwd() + "/probe.log", JSON.stringify([Kind.Probe, VERSION, ctx.ui.theme instanceof Theme,
                  typeof ctx.ui.theme.fg("accent", "x"), first([Type.String().type]), typeof Text, getAgentDir() === process.env.PI_CODING_AGENT_DIR]) + "\n");
              } });
            }
            """);
        await using var host = await StartHost(sandbox, extension);
        Equal("pi@1.1.0", host.Modules, "modules");
        Check(host.RuntimeFallback is null, "no fallback");
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/probe"]);
        Equal(0, code, "exit; " + stderr);
        Check(!stderr.Contains("compatibility modules", StringComparison.Ordinal), "no fallback warning: " + stderr);
        Equal("""["probe","1.1.0",true,"string","string","function",true]""", LogLines(sandbox).Single(), "real packages");
    }

    // PI_OFFLINE: nothing is installed, the compatibility modules run the extension, and the run says so.
    private static async Task OfflineFallback()
    {
        using var sandbox = NodeSandbox("offline");
        sandbox.Vars["PI_OFFLINE"] = "1";
        sandbox.Vars["PISHARP_PI_RUNTIME_DIR"] = Path.Combine(sandbox.Root, "no-runtime");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "offline.ts"), """
            import { appendFileSync } from "node:fs";
            import { truncateToWidth } from "@earendil-works/pi-tui";
            export default function (pi: any) {
              pi.registerCommand("probe", { description: "Probe", handler: async () => appendFileSync(process.cwd() + "/probe.log", JSON.stringify(["offline", truncateToWidth("abcdef", 3)]) + "\n") });
            }
            """);
        var (code, _, stderr) = await sandbox.Run([.. new[] { "-p" }, .. Model, "-e", extension, "/probe"]);
        Equal(0, code, "exit; " + stderr);
        Check(stderr.Contains("Warning: Extensions are not running against the Pi 1.1.0 packages: offline (PI_OFFLINE)", StringComparison.Ordinal), "fallback warning: " + stderr);
        Check(!Directory.Exists(Path.Combine(sandbox.Root, "no-runtime")), "nothing installed offline");
        Check(LogLines(sandbox).Single().StartsWith("""["offline",""", StringComparison.Ordinal), "extension ran: " + string.Join("|", LogLines(sandbox)));
    }

    // Every lockfile package's integrity must equal the registry's dist.integrity of that version.
    private static async Task IntegrityMismatch()
    {
        using var sandbox = new Sandbox("integrity");
        var directory = Path.Combine(sandbox.Root, "runtime");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "package-lock.json"), """
            {"lockfileVersion":3,"packages":{"":{"name":"x"},
              "node_modules/@earendil-works/pi-ai":{"version":"1.1.0","resolved":"https://registry.example/@earendil-works/pi-ai/-/pi-ai-1.1.0.tgz","integrity":"sha512-good"},
              "node_modules/a/node_modules/b":{"version":"2.0.0","resolved":"https://registry.example/b/-/b-2.0.0.tgz","integrity":"sha512-bee"},
              "node_modules/c/node_modules/d":{"version":"1.0.0","inBundle":true}}}
            """);
        var asked = new List<string>();
        HttpClient Client(string bIntegrity) => new(new Registry(asked, bIntegrity));
        var provenance = await PiNodeRuntime.VerifyAsync(directory, () => Client("sha512-bee"), CancellationToken.None);
        Names(["https://registry.example/@earendil-works%2fpi-ai/1.1.0", "https://registry.example/b/2.0.0"], asked.Order(StringComparer.Ordinal), "registry lookups");
        Names(["@earendil-works/pi-ai", "b"], provenance["packages"]!.AsArray().Select(item => item!["name"]!.GetValue<string>()), "verified packages");
        Names(["https://registry.example"], provenance["registries"]!.AsArray().Select(item => item!.GetValue<string>()), "registries");
        var error = await Assert<InvalidDataException>(() => PiNodeRuntime.VerifyAsync(directory, () => Client("sha512-other"), CancellationToken.None));
        Check(error.Message.Contains("b@2.0.0 integrity differs from the registry", StringComparison.Ordinal), error.Message);
        Check(!PiNodeRuntime.IsInstalled(directory), "an unverified folder is not an installed runtime");
    }

    private static async Task<T> Assert<T>(Func<Task> run) where T : Exception
    {
        try { await run(); } catch (T error) { return error; }
        throw new InvalidOperationException("expected " + typeof(T).Name);
    }

    private sealed class Registry(List<string> asked, string bIntegrity) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.OriginalString;
            lock (asked) asked.Add(url);
            var integrity = url.EndsWith("/b/2.0.0", StringComparison.Ordinal) ? bIntegrity : "sha512-good";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"dist\":{\"integrity\":\"" + integrity + "\"}}", Encoding.UTF8, "application/json") });
        }
    }
}
