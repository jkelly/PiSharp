using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

internal static partial class Program
{
    /// <summary>The Web Agent as a child process, as PiSharp.WebAgent.Tests' WebAgentApp starts it: the faux switch (no provider code path, no
    /// network), a temporary data folder (never the user's own), a free loopback port and an environment without this machine's provider keys,
    /// tokens or URL settings. Its Settings › Tools working directory is the case's own <c>work</c> folder, set before any chat exists.</summary>
    internal sealed class Agent : IAsyncDisposable
    {
        private readonly Process process;
        private readonly StringBuilder log;

        private Agent(string root, Process process, StringBuilder log, Uri baseUri)
        {
            Root = root;
            this.process = process;
            this.log = log;
            Base = baseUri;
        }

        public string Root { get; }

        public string Work => Path.Join(Root, "work");

        public Uri Base { get; }

        public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>The app's console output so far.</summary>
        public string Log { get { lock (log) return log.ToString(); } }

        /// <summary>Starts the app; tool calls ask first (<paramref name="approvalMode"/> <c>ask</c>) unless told otherwise.</summary>
        public static async Task<Agent> StartAsync(string name, string approvalMode = "ask")
        {
            var root = Temp(name);
            Directory.CreateDirectory(Path.Join(root, "work"));
            var port = FreePort();
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { AppDll(), "--faux", "--data", Path.Join(root, "data"), "--urls", baseUri.GetLeftPart(UriPartial.Authority),
                         "--Logging:LogLevel:Default=Warning" })
                info.ArgumentList.Add(argument);
            foreach (var variable in info.Environment.Keys.ToList())
                if (variable.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                    variable.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("_URLS", StringComparison.OrdinalIgnoreCase) ||
                    variable.EndsWith("HTTP_PORTS", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("HTTPS_PORTS", StringComparison.OrdinalIgnoreCase) ||
                    variable.Equals("PISHARP_WEBAGENT_DATA", StringComparison.OrdinalIgnoreCase) ||
                    variable.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) || variable.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
                    info.Environment.Remove(variable);
            var log = new StringBuilder();
            var process = Process.Start(info)!;
            process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var agent = new Agent(root, process, log, baseUri);
            try
            {
                await WaitUntilAsync(async () =>
                {
                    if (process.HasExited) throw new InvalidOperationException($"The app exited ({process.ExitCode}): " + agent.Log);
                    try { return (await agent.Http.GetAsync(new Uri(baseUri, "/api/health"))).IsSuccessStatusCode; }
                    catch (HttpRequestException) { return false; }
                }, "the app answers /api/health", TimeSpan.FromSeconds(60));
                var tools = await agent.PostAsync("/api/settings/tools",
                    new JsonObject { ["workingDirectory"] = agent.Work, ["approvalMode"] = approvalMode, ["allowList"] = new JsonArray() });
                Equal(200, tools.Status, "the working directory is the case's own: " + tools.Text);
            }
            catch
            {
                await agent.DisposeAsync();
                throw;
            }
            return agent;
        }

        /// <summary>The built app: <c>src/PiSharp.WebAgent/bin/&lt;configuration&gt;/net10.0/PiSharp.WebAgent.dll</c>.</summary>
        private static string AppDll()
        {
            var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
            return Path.GetFullPath(Path.Join(metadata["WebProject"]!, "bin", metadata["Configuration"]!, "net10.0", "PiSharp.WebAgent.dll"));
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

        /// <summary>Creates a chat in <see cref="Work"/> with its first message, through the API (as the new chat page does); returns its id.</summary>
        public async Task<string> CreateChatAsync(string text, string requestId)
        {
            var created = await PostAsync("/api/chats", new JsonObject { ["requestId"] = requestId, ["text"] = text, ["cwd"] = Work });
            Equal(200, created.Status, "created: " + created.Text);
            return created.Body!["id"]!.GetValue<string>();
        }

        /// <summary>Waits until the chat's run is over.</summary>
        public Task WaitIdleAsync(string chatId) => WaitUntilAsync(async () => (await Get("/api/chats/" + chatId)).Body?["running"]?.GetValue<bool>() == false,
            $"chat {chatId} is idle", TimeSpan.FromSeconds(60));

        /// <summary>The lines of a file in <see cref="Work"/> (none when it does not exist).</summary>
        public string[] Lines(string file)
        {
            var path = Path.Join(Work, file);
            return File.Exists(path) ? [.. File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0)] : [];
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            Http.Dispose();
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A temporary directory. */ }
        }
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
