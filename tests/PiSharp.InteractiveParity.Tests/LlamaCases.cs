// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/extensions/llama/index.ts and ui.ts (the /llama manager),
// packages/coding-agent/src/extensions/llama/provider.ts (the llama.cpp /login api-key flow) and
// packages/coding-agent/src/modes/interactive/interactive-mode.ts (the built-in extension command in autocomplete).
using System.Text.Json.Nodes;
using static Expect;

/// <summary>/llama end to end on the virtual terminal against a fake llama.cpp router and a fake Hugging Face API on 127.0.0.1
/// (LlamaServer). Texts are upstream's; nothing is captured from a real server.</summary>
internal static class LlamaCases
{
    private static readonly string[] Regular = ["--provider", "anthropic", "--model", "claude-sonnet-4-5", "--tui-mode", "regular"];
    private const string Down = "\u001b[B", Up = "\u001b[A", Enter = "\r", Escape = "\u001b";

    private static async Task<InteractiveHarness> Started(string name, Action<InteractiveHarness>? setup = null)
    {
        var pi = new InteractiveHarness(name, rows: 40);
        setup?.Invoke(pi);
        pi.Start(Regular);
        await pi.WaitFor("escape interrupt");
        await pi.WaitUntil(text => text.Contains("claude-sonnet-4-5", StringComparison.Ordinal), "footer");
        return pi;
    }

    /// <summary>Types <paramref name="text"/> until <paramref name="expected"/> shows, clearing the line between tries: the first
    /// keystrokes after startup can reach the editor before autocomplete is attached.</summary>
    private static async Task TypeUntil(InteractiveHarness pi, string text, string expected)
    {
        for (var attempt = 1; ; attempt++)
        {
            pi.Type(text);
            try { await pi.WaitFor(expected, 5_000); return; }
            catch (TimeoutException) when (attempt < 3) { pi.Type(""); await Task.Delay(200); }
        }
    }

    /// <summary>A router with <c>alpha</c> loaded and <c>beta</c> unloaded; load and unload change the status after a moment.</summary>
    private sealed class Router : IDisposable
    {
        public readonly Dictionary<string, string> Status = new(StringComparer.Ordinal) { ["alpha"] = "loaded", ["beta"] = "unloaded" };
        public LlamaServer Server { get; }
        public Router()
        {
            Server = new LlamaServer(Handle);
        }
        private LlamaServer.Response Handle(LlamaServer.Request request)
        {
            lock (Status)
            {
                if (request.Method == "POST" && request.Path is "/models/load" or "/models/unload")
                {
                    var model = JsonNode.Parse(request.Body)!["model"]!.GetValue<string>();
                    var next = request.Path == "/models/load" ? "loaded" : "unloaded";
                    Status[model] = request.Path == "/models/load" ? "loading" : "loaded";
                    _ = Task.Run(async () => { await Task.Delay(150); lock (Status) Status[model] = next; });
                    return LlamaServer.Json("""{"success":true}""");
                }
                if (request.Path == "/models")
                {
                    var data = new JsonArray([.. Status.Select(pair => (JsonNode)new JsonObject
                    {
                        ["id"] = pair.Key, ["status"] = new JsonObject { ["value"] = pair.Value },
                        ["meta"] = pair.Value == "loaded" ? new JsonObject { ["n_ctx"] = 32768 } : new JsonObject { ["n_ctx_train"] = 131072 }
                    })]);
                    return LlamaServer.Json(new JsonObject { ["data"] = data }.ToJsonString());
                }
                if (request.Path == "/props") return LlamaServer.Json("{}");
            }
            return LlamaServer.NotFound();
        }
        public void Dispose() => Server.Dispose();
    }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        // index.ts configuredClient: without a server URL the command asks for /login.
        yield return ("e2e.llama.unconfigured-asks-for-login", async () =>
        {
            await using var pi = await Started("llama-unconfigured");
            await TypeUntil(pi, "/lla", "Manage llama.cpp router models");
            pi.Type(Escape);
            await Task.Delay(100);
            pi.Type("\u0015");
            await pi.Submit("/llama");
            await pi.WaitFor("Warning: Configure llama.cpp with /login llama.cpp");
        });

        yield return ("e2e.llama.manager-loads-and-unloads-router-models", async () =>
        {
            using var router = new Router();
            await using var pi = await Started("llama-manager", harness => harness.Vars["LLAMA_BASE_URL"] = router.Server.Url + "/v1");
            await pi.Submit("/llama");
            await pi.WaitFor("llama.cpp models");
            await pi.WaitFor("Download model…");
            var screen = pi.Terminal.Text;
            Contains(screen, router.Server.Url, "server URL");
            Check(pi.Terminal.Lines.Any(line => line.Contains("→ alpha", StringComparison.Ordinal) && line.Contains("loaded · 33k context", StringComparison.Ordinal)), "loaded model first with its context:\n" + screen);
            Contains(screen, "Hugging Face owner/repository[:quant]", "download entry description");
            Contains(screen, "enter load/unload/download", "hint");
            // Selecting the unloaded model while another is loaded asks what to do with it.
            pi.Type(Down);
            await Task.Delay(100);
            pi.Type(Enter);
            await pi.WaitFor("1 model is loaded");
            Contains(pi.Terminal.Text, "Unload all and load", "replace option");
            pi.Type(Down);
            await Task.Delay(100);
            pi.Type(Enter);
            await pi.WaitFor("Loaded beta");
            await pi.WaitUntil(text => pi.Terminal.Lines.Any(line => line.Contains("beta", StringComparison.Ordinal) && line.Contains("loaded · 33k context", StringComparison.Ordinal)), "beta loaded in the list");
            Check(router.Server.Seen().Contains("POST /models/load {\"model\":\"beta\"}"), "load request: " + string.Join("\n", router.Server.Seen()));
            Check(!router.Server.Seen().Any(line => line.StartsWith("POST /models/unload", StringComparison.Ordinal)), "alpha kept loaded");
            // Unloading asks for confirmation (the list starts at its first model again).
            pi.Type(Enter);
            await pi.WaitFor("Unload model?");
            pi.Type(Enter);
            await pi.WaitFor("Unloaded alpha");
            Check(router.Server.Seen().Contains("POST /models/unload {\"model\":\"alpha\"}"), "unload request");
            // The manager reads the catalog again before it shows the list; keys sent before that are not the list's.
            await pi.WaitUntil(text => text.Contains("Download model…", StringComparison.Ordinal)
                && !pi.Terminal.Lines.Any(line => line.Contains("alpha", StringComparison.Ordinal) && line.Contains("loaded ·", StringComparison.Ordinal)), "list after unload");
            await Task.Delay(100);
            pi.Type(Escape);
            await pi.WaitUntil(text => !text.Contains("Download model…", StringComparison.Ordinal), "manager closed");
            // The loaded model is selectable in /model.
            // Filter by name: the list shows 10 rows and the catalog's own models come first.
            await pi.Submit("/model");
            await pi.WaitFor("Enter to select");
            pi.Type("beta");
            await pi.WaitUntil(text => pi.Terminal.Lines.Any(line => line.Contains("beta", StringComparison.Ordinal) && line.Contains("[llama.cpp]", StringComparison.Ordinal)), "llama.cpp model in /model");
            pi.Type(Escape);
        });

        // index.ts readCatalog: an unreachable router offers Retry and Close.
        yield return ("e2e.llama.unreachable-router-offers-retry-and-close", async () =>
        {
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0)) { probe.Start(); port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop(); }
            await using var pi = await Started("llama-unreachable", harness => harness.Vars["LLAMA_BASE_URL"] = $"http://127.0.0.1:{port}");
            await pi.Submit("/llama");
            await pi.WaitFor("llama.cpp unavailable");
            Contains(pi.Terminal.Text, "Could not connect to the server.", "message");
            Contains(pi.Terminal.Text, "Retry", "retry");
            pi.Type(Enter);
            // Retry reconnects (and fails again) before the same choice returns.
            await Task.Delay(3000);
            await pi.WaitFor("llama.cpp unavailable");
            pi.Type(Down);
            await Task.Delay(100);
            pi.Type(Enter);
            await pi.WaitUntil(text => !text.Contains("llama.cpp unavailable", StringComparison.Ordinal), "closed");
        });

        // index.ts downloadModel: Hugging Face search, the access notice of a gated repository, the quantization select, then the
        // router's download with byte progress.
        yield return ("e2e.llama.download-searches-hugging-face", async () =>
        {
            using var server = new LlamaServer();
            var downloading = false; var finished = false;
            server.Handle = request =>
            {
                switch (request.Target)
                {
                    case "/models" when request.Method == "POST":
                        downloading = true;
                        _ = Task.Run(async () =>
                        {
                            for (var wait = 0; wait < 100 && server.OpenStreams == 0; wait++) await Task.Delay(20);
                            server.Send("""{"model":"owner/qwen-GGUF:Q4_K_M","event":"download_progress","data":{"progress":{"a":{"done":512,"total":1024}}}}""");
                            await Task.Delay(400);
                            finished = true;
                            server.Send("""{"model":"owner/qwen-GGUF:Q4_K_M","event":"download_finished","data":{}}""");
                        });
                        return LlamaServer.Json("""{"success":true}""");
                    case "/models" or "/models?reload=1":
                        return LlamaServer.Json(!downloading ? """{"data":[]}""" : finished
                            ? """{"data":[{"id":"owner/qwen-GGUF:Q4_K_M","status":{"value":"unloaded"}}]}"""
                            : """{"data":[{"id":"owner/qwen-GGUF:Q4_K_M","status":{"value":"downloading","progress":{"a":{"done":512,"total":1024}}}}]}""");
                    case "/api/models?search=qwen&filter=gguf&sort=downloads&direction=-1&limit=20":
                        return LlamaServer.Json("""[{"id":"owner/qwen-GGUF","downloads":1200}]""");
                    case "/api/models/owner/qwen-GGUF?blobs=true":
                        return LlamaServer.Json("""{"id":"owner/qwen-GGUF","gated":"manual","siblings":[{"rfilename":"model-Q5_K_M.gguf","size":6000},{"rfilename":"model-Q4_K_M.gguf","size":5000}]}""");
                    default: return LlamaServer.NotFound();
                }
            };
            await using var pi = await Started("llama-download", harness =>
            {
                harness.Vars["LLAMA_BASE_URL"] = server.Url;
                harness.Configure = context => context with { Llama = context.Llama! with { HuggingFaceUrl = server.Url, FindHuggingFaceToken = () => Task.FromResult<string?>("hf-test") } };
            });
            await pi.Submit("/llama");
            await pi.WaitFor("Download model…");
            pi.Type(Enter);
            await pi.WaitFor("Type at least 2 characters");
            Contains(pi.Terminal.Text, "Model name or owner/repository[:quant]", "search hint");
            pi.Type("qwen");
            await pi.WaitFor("owner/qwen-GGUF  1.2k downloads");
            pi.Type(Enter);
            await pi.WaitFor("Hugging Face access required");
            Contains(pi.Terminal.Text, "Manual approval is required at:", "approval");
            Contains(pi.Terminal.Text, "https://huggingface.co/owner/qwen-GGUF", "access page");
            pi.Type(Enter);
            await pi.WaitFor("Select quantization");
            Contains(pi.Terminal.Text, "Q4_K_M · 4.88 KiB · recommended", "recommended quantization first");
            Contains(pi.Terminal.Text, "Q5_K_M · 5.86 KiB", "other quantization");
            pi.Type(Enter);
            await pi.WaitFor("Downloaded owner/qwen-GGUF:Q4_K_M");
            Check(server.Seen().Contains("POST /models {\"model\":\"owner/qwen-GGUF:Q4_K_M\"}"), "download request: " + string.Join("\n", server.Seen()));
            Check(server.Requests.Where(request => request.Path.StartsWith("/api/", StringComparison.Ordinal)).All(request => request.Header("authorization") == "Bearer hf-test"),
                "the Hugging Face token");
            pi.Type(Escape);
        });

        // provider.ts login through /login: the server URL and optional key, checked against the router, stored as env.LLAMA_BASE_URL.
        yield return ("e2e.llama.login-stores-the-server-url", async () =>
        {
            using var router = new Router();
            await using var pi = await Started("llama-login", harness => harness.Configure = context => context with
            {
                Login = new PiSharp.Cli.Authentication.ProviderLoginHost(new PiSharp.Cli.Authentication.AuthJsonCredentialStore(Path.Combine(harness.AgentDir, "auth.json")),
                    () => new HttpClient()) { ReadEnvironment = harness.Vars.GetValueOrDefault }
            });
            pi.Type("/login llama.cpp");
            await Task.Delay(300);
            pi.Type(Enter);
            await Task.Delay(300);
            pi.Type(Enter);
            await pi.WaitFor("llama.cpp server URL");
            Contains(pi.Terminal.Text, "http://127.0.0.1:8080", "default URL placeholder");
            pi.Type(router.Server.Url);
            await Task.Delay(100);
            pi.Type(Enter);
            await pi.WaitFor("API key (optional)");
            pi.Type(Enter);
            await pi.WaitFor("Saved API key for llama.cpp");
            Equal($$$$"""{"llama.cpp":{"type":"api_key","env":{"LLAMA_BASE_URL":"{{{{router.Server.Url}}}}"}}}""",
                JsonNode.Parse(File.ReadAllText(Path.Combine(pi.AgentDir, "auth.json")))!.ToJsonString(), "auth.json");
            Check(router.Server.Seen().Contains("GET /models"), "the router's catalog was read");
        });
    }
}
