using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

// Decision 0011 (run W2, WU10; W3 fix): the Web Agent as a child process for the browser cases. W2 started it with --faux only; W3's T3 kept
// a second launcher (W3App) for priced faux and a sealed live app, and the W3 integration fixes merged it back here: Agent runs in one of
// three modes (AgentMode). Faux: --faux, the free faux provider, no provider code path. FauxMetered: --faux-metered, still offline, but the
// faux provider is Metered at a list price and reports usage, so the Spend Guard has something to reserve. Live: the real routing with
// nothing connected (a first run, for the setup wizard), sealed: it also blanks every provider variable and AWS or Google setting the app
// could read as an account, and points HTTP_PROXY, HTTPS_PROXY and ALL_PROXY at a closed loopback port, so a request that tried to leave this
// computer would fail at once instead of reaching a provider. KillAndRestartAsync crashes the app (Process.Kill of its tree) and starts it
// again on the same data, on a new port. FreePort and WaitUntilAsync are shared by every case.
internal static partial class Program
{
    /// <summary>How an <see cref="Agent"/> runs.</summary>
    internal enum AgentMode
    {
        /// <summary><c>--faux</c>: the free faux provider, no provider code path.</summary>
        Faux,

        /// <summary><c>--faux-metered</c>: still offline, but the faux provider is Metered at a list price and reports usage.</summary>
        FauxMetered,

        /// <summary>The real routing with nothing connected (a first run); outbound requests go to a closed loopback proxy.</summary>
        Live,
    }

    /// <summary>The Web Agent as a child process, as PiSharp.WebAgent.Tests' WebAgentApp starts it: a temporary data folder (never the user's
    /// own), a free loopback port and an environment without this machine's provider keys, tokens or URL settings, in one of the
    /// <see cref="AgentMode"/>s (see the file's header). Its Settings › Tools working directory is the case's own <c>work</c> folder, set
    /// before any chat exists.</summary>
    internal sealed class Agent : IAsyncDisposable
    {
        private readonly AgentMode mode;
        private readonly StringBuilder log = new();
        private Process process;

        private Agent(string root, AgentMode mode, Process process, Uri baseUri)
        {
            Root = root;
            this.mode = mode;
            this.process = process;
            Base = baseUri;
        }

        public string Root { get; }

        /// <summary>The app's data folder.</summary>
        public string Data => Path.Join(Root, "data");

        public string Work => Path.Join(Root, "work");

        /// <summary>The app's address; a restart moves it to a new port.</summary>
        public Uri Base { get; private set; }

        public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>The app's console output so far, across restarts.</summary>
        public string Log { get { lock (log) return log.ToString(); } }

        /// <summary>Starts the app with <c>--faux</c>; tool calls ask first (<paramref name="approvalMode"/> <c>ask</c>) unless told otherwise.</summary>
        public static Task<Agent> StartAsync(string name, string approvalMode = "ask") => StartAsync(name, AgentMode.Faux, approvalMode);

        /// <summary>Starts the app in <paramref name="mode"/> on a fresh data folder, with Tools working in the case's <c>work</c> folder
        /// (<paramref name="approvalMode"/>).</summary>
        public static async Task<Agent> StartAsync(string name, AgentMode mode, string approvalMode = "ask")
        {
            var root = Temp(name);
            Directory.CreateDirectory(Path.Join(root, "work"));
            Agent? agent = null;
            try
            {
                var (process, baseUri) = Launch(root, mode);
                agent = new Agent(root, mode, process, baseUri);
                agent.Follow(process);
                await agent.ReadyAsync();
                var tools = await agent.PostAsync("/api/settings/tools",
                    new JsonObject { ["workingDirectory"] = agent.Work, ["approvalMode"] = approvalMode, ["allowList"] = new JsonArray() });
                Equal(200, tools.Status, "the working directory is the case's own: " + tools.Text);
                var health = await agent.Get("/api/health");
                Equal(mode != AgentMode.Live, health.Body?["faux"]?.GetValue<bool>() == true, "the faux switch");
                Equal(mode == AgentMode.FauxMetered, health.Body?["fauxMetered"]?.GetValue<bool>() == true, "the priced faux switch");
                return agent;
            }
            catch
            {
                if (agent is not null) await agent.DisposeAsync();
                else DeleteFolder(root);
                throw;
            }
        }

        private static (Process Process, Uri Base) Launch(string root, AgentMode mode)
        {
            var port = FreePort();
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(AppDll());
            if (mode == AgentMode.Faux) info.ArgumentList.Add("--faux");
            if (mode == AgentMode.FauxMetered) info.ArgumentList.Add("--faux-metered");
            foreach (var argument in new[] { "--data", Path.Join(root, "data"), "--urls", baseUri.GetLeftPart(UriPartial.Authority), "--Logging:LogLevel:Default=Warning" })
                info.ArgumentList.Add(argument);
            foreach (var variable in info.Environment.Keys.ToList())
                if (Unwanted(variable, mode)) info.Environment.Remove(variable);
            if (mode == AgentMode.Live)
            {
                // Nothing leaves this computer: a closed port on loopback is every proxy, and nothing bypasses it.
                foreach (var proxy in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) info.Environment[proxy] = "http://127.0.0.1:9";
                info.Environment["NO_PROXY"] = "";
                info.Environment["no_proxy"] = "";
                // No ambient AWS or Google credentials from this machine's files either.
                info.Environment["AWS_SHARED_CREDENTIALS_FILE"] = Path.Join(root, "no-aws-credentials");
                info.Environment["AWS_CONFIG_FILE"] = Path.Join(root, "no-aws-config");
                info.Environment["CLOUDSDK_CONFIG"] = Path.Join(root, "no-gcloud");
            }
            var process = Process.Start(info)!;
            return (process, baseUri);
        }

        /// <summary>Whether the child must not see <paramref name="variable"/>: provider keys, tokens and secrets, URL and port settings, the data
        /// folder and the environment name; in Live mode also every AWS, Google, Azure or Cloudflare setting and every proxy setting.</summary>
        private static bool Unwanted(string variable, AgentMode mode) =>
            variable.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase) ||
            variable.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("_URLS", StringComparison.OrdinalIgnoreCase) ||
            variable.EndsWith("HTTP_PORTS", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("HTTPS_PORTS", StringComparison.OrdinalIgnoreCase) ||
            variable.Equals("PISHARP_WEBAGENT_DATA", StringComparison.OrdinalIgnoreCase) ||
            variable.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) || variable.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) ||
            (mode == AgentMode.Live && (variable.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase) || variable.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                variable.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) || variable.StartsWith("GOOGLE_", StringComparison.OrdinalIgnoreCase) ||
                variable.StartsWith("GCLOUD_", StringComparison.OrdinalIgnoreCase) || variable.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) ||
                variable.StartsWith("CLOUDFLARE_", StringComparison.OrdinalIgnoreCase) || variable.Contains("PROXY", StringComparison.OrdinalIgnoreCase)));

        /// <summary>The built app: <c>src/PiSharp.WebAgent/bin/&lt;configuration&gt;/net10.0/PiSharp.WebAgent.dll</c>.</summary>
        private static string AppDll()
        {
            var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
            return Path.GetFullPath(Path.Join(metadata["WebProject"]!, "bin", metadata["Configuration"]!, "net10.0", "PiSharp.WebAgent.dll"));
        }

        private void Follow(Process child)
        {
            child.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            child.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
        }

        private Task ReadyAsync() => WaitUntilAsync(async () =>
        {
            if (process.HasExited) throw new InvalidOperationException($"The app exited ({process.ExitCode}): " + Log);
            try { return (await Http.GetAsync(new Uri(Base, "/api/health"))).IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
        }, "the app answers /api/health", TimeSpan.FromSeconds(60));

        /// <summary>Kills the app's process tree (no shutdown: a crash) and starts it again on the same data, on a new port
        /// (<see cref="Base"/> changes; open a new <see cref="Window"/> on it, or <see cref="Window.ReopenAsync"/>).</summary>
        public async Task KillAndRestartAsync()
        {
            await StopAsync();
            process.Dispose();
            var (next, baseUri) = Launch(Root, mode);
            process = next;
            Base = baseUri;
            Follow(next);
            await ReadyAsync();
        }

        public async Task<(int Status, JsonNode? Body, string Text)> SendAsync(HttpMethod method, string path, JsonNode? body = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(Base, path));
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            JsonNode? json = null;
            try { json = text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { /* Not JSON. */ }
            return ((int)response.StatusCode, json, text);
        }

        public Task<(int Status, JsonNode? Body, string Text)> Get(string path) => SendAsync(HttpMethod.Get, path);

        public Task<(int Status, JsonNode? Body, string Text)> PostAsync(string path, JsonNode body) => SendAsync(HttpMethod.Post, path, body);

        /// <summary>Creates a chat in <see cref="Work"/> with its first message, through the API (as the new chat page does, without the spend
        /// check); returns its id.</summary>
        public async Task<string> CreateChatAsync(string text, string requestId)
        {
            var created = await PostAsync("/api/chats", new JsonObject { ["requestId"] = requestId, ["text"] = text, ["cwd"] = Work });
            Equal(200, created.Status, "created: " + created.Text);
            return created.Body!["id"]!.GetValue<string>();
        }

        /// <summary>Waits until the chat's run is over and returns its current branch as Markdown (<c>/chat/{id}/export.md</c>): the device says
        /// the chat is not running, the branch holds at least <paramref name="messages"/> of your messages and its last one has a stored answer
        /// (an agent entry with an id, not one still working), and <paramref name="done"/> holds for the Markdown when given. W4 L3: the
        /// running flag is the device's last reading (refreshed from its coalesced changes, about every 250 ms), so right after a send it can
        /// still say false before the run began; only the stored answer shows that the run happened. Pass <paramref name="messages"/> after a
        /// send to a chat that already has answers, and <paramref name="done"/> after a restart (a recovered answer is stored after the cut-off
        /// partial, which is an answer too).</summary>
        public async Task<string> WaitIdleAsync(string chatId, int messages = 1, Func<string, bool>? done = null)
        {
            var markdown = "";
            await WaitUntilAsync(async () =>
            {
                if ((await Get("/api/chats/" + chatId)).Body?["running"]?.GetValue<bool>() != false) return false;
                var export = await Get($"/chat/{chatId}/export.md");
                if (export.Status != 200) return false;
                markdown = export.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
                var yours = 0;
                for (var at = markdown.IndexOf("\n### You (#", StringComparison.Ordinal); at >= 0; at = markdown.IndexOf("\n### You (#", at + 1, StringComparison.Ordinal)) yours++;
                var you = markdown.LastIndexOf("\n### You (#", StringComparison.Ordinal);
                var answer = markdown.LastIndexOf("\n### Agent (#", StringComparison.Ordinal);
                var working = markdown.LastIndexOf("\n### Agent (working)", StringComparison.Ordinal);
                return yours >= messages && answer > you && working < answer && (done is null || done(markdown));
            }, $"chat {chatId} is idle with an answer to message {messages}", TimeSpan.FromSeconds(60));
            return markdown;
        }

        /// <summary>The lines of a file in <see cref="Work"/> (none when it does not exist).</summary>
        public string[] Lines(string file)
        {
            var path = Path.Join(Work, file);
            return File.Exists(path) ? [.. File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0)] : [];
        }

        /// <summary>The ledger's rows (<c>GET /api/spend/ledger</c>), newest first.</summary>
        public async Task<List<JsonObject>> LedgerAsync()
        {
            var ledger = await Get("/api/spend/ledger?size=100");
            Equal(200, ledger.Status, "the ledger: " + ledger.Text);
            return [.. ledger.Body!["items"]!.AsArray().Select(item => item!.AsObject())];
        }

        private async Task StopAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            process.Dispose();
            Http.Dispose();
            DeleteFolder(Root);
        }
    }

    /// <summary>A text field of a ledger item's reservation (<c>op</c>, <c>state</c>, <c>run</c>, ...), or null.</summary>
    internal static string? Reservation(JsonObject item, string name) =>
        item["reservation"]?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static void DeleteFolder(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A temporary directory. */ }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? limit = null)
    {
        var deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(20));
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting until " + what);
            await Task.Delay(50);
        }
    }
}
