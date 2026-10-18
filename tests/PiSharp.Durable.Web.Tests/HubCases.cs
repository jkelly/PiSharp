using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Server;

internal static partial class Program
{
    /// <summary>A faux device in this process, published as this computer's device, and the hub as a child process.</summary>
    internal sealed class Hub : IAsyncDisposable
    {
        private Process? process;
        private readonly StringBuilder log = new();

        private Hub(string root) => Root = root;

        public string Root { get; }
        public string Work => Path.Join(Root, "work");
        public string DeviceDirectory => Path.Join(Root, "device");
        public string Token { get; private set; } = "";
        public Uri DeviceUrl { get; private set; } = null!;
        public DurableDeviceHost Device { get; private set; } = null!;
        public WebApplication DeviceApp { get; private set; } = null!;
        public Uri Base { get; private set; } = null!;
        public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

        public static async Task<Hub> StartAsync(string name)
        {
            var hub = new Hub(Temp(name));
            Directory.CreateDirectory(hub.Work);
            hub.Token = DeviceConfig.RotateToken(hub.DeviceDirectory);
            hub.Device = await DurableDeviceHost.OpenAsync(new DurableDeviceOptions
            {
                DataDirectory = Path.Join(hub.DeviceDirectory, "data"),
                StartDirectories = [hub.Work],
                DeviceName = "faux-box",
                Models = new FauxDurableModels(),
                DefaultModel = FauxDurableModels.EchoRef,
            });
            hub.DeviceApp = DurableDeviceServer.Build(hub.Device, new DurableDeviceServerOptions { Token = hub.Token }, "http://127.0.0.1:0");
            await hub.DeviceApp.StartAsync();
            hub.DeviceUrl = new Uri(hub.DeviceApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
            new DeviceConfig { Port = hub.DeviceUrl.Port }.Save(hub.DeviceDirectory);

            var port = FreePort();
            hub.Base = new Uri($"http://127.0.0.1:{port}");
            var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
            var dll = Path.GetFullPath(Path.Join(metadata["WebProject"]!, "bin", metadata["Configuration"]!, "net10.0", "PiSharp.Durable.Web.dll"));
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { dll, "--urls", hub.Base.ToString().TrimEnd('/'), "--Hub:DataDirectory=" + Path.Join(hub.Root, "hub"),
                         "--Hub:LocalDeviceDirectory=" + hub.DeviceDirectory, "--Logging:LogLevel:Default=Warning" })
                start.ArgumentList.Add(arg);
            hub.process = Process.Start(start)!;
            hub.process.OutputDataReceived += (_, e) => { lock (hub.log) hub.log.AppendLine(e.Data); };
            hub.process.ErrorDataReceived += (_, e) => { lock (hub.log) hub.log.AppendLine(e.Data); };
            hub.process.BeginOutputReadLine();
            hub.process.BeginErrorReadLine();
            await WaitUntilAsync(async () =>
            {
                if (hub.process.HasExited) throw new InvalidOperationException("The hub exited: " + hub.Log);
                try { return (await hub.Http.GetAsync(new Uri(hub.Base, "/api/network"))).IsSuccessStatusCode; }
                catch (HttpRequestException) { return false; }
            }, "the hub answers", TimeSpan.FromSeconds(60));
            return hub;
        }

        public string Log { get { lock (log) return log.ToString(); } }

        public async Task<(int Status, JsonNode? Body, string Text)> SendAsync(HttpMethod method, string path, JsonNode? body = null, string? host = null,
            string? origin = null, string contentType = "application/json")
        {
            using var request = new HttpRequestMessage(method, new Uri(Base, path));
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, contentType);
            if (host is not null) request.Headers.Host = host;
            if (origin is not null) request.Headers.Add("Origin", origin);
            using var response = await Http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            JsonNode? json = null;
            try { json = text.Length > 0 && text[0] is '{' or '[' ? JsonNode.Parse(text) : null; } catch (System.Text.Json.JsonException) { /* Not JSON. */ }
            return ((int)response.StatusCode, json, text);
        }

        public Task<(int Status, JsonNode? Body, string Text)> Get(string path) => SendAsync(HttpMethod.Get, path);

        public Task<(int Status, JsonNode? Body, string Text)> Post(string path, JsonNode? body) => SendAsync(HttpMethod.Post, path, body ?? new JsonObject());

        public async Task<string> SessionAsync(string name)
        {
            await WaitOnlineAsync();
            var created = await Post("/api/devices/local/v1/sessions", new JsonObject { ["name"] = name, ["cwd"] = Work });
            Equal(201, created.Status, "create a session through the proxy: " + created.Text);
            return created.Body!["id"]!.GetValue<string>();
        }

        public Task WaitOnlineAsync() => WaitUntilAsync(async () =>
            (await Get("/api/network")).Body?["devices"]?.AsArray().Any(d => d!["id"]!.GetValue<string>() == "local" && d["status"]!.GetValue<string>() == "online") == true,
            "local is online");

        public async ValueTask DisposeAsync()
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            if (process is not null) await process.WaitForExitAsync();
            process?.Dispose();
            Http.Dispose();
            await DeviceApp.StopAsync();
            await DeviceApp.DisposeAsync();
            await Device.DisposeAsync();
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

    internal static IEnumerable<(string, Func<Task>)> HubCases() =>
    [
        Case("hub.guard-refuses-other-hosts-origins-and-forms", async () =>
        {
            await using var hub = await Hub.StartAsync("guard");
            Equal(421, (await hub.SendAsync(HttpMethod.Get, "/api/network", host: "evil.example")).Status, "another Host");
            Equal(403, (await hub.SendAsync(HttpMethod.Put, "/api/ui-state/x", new JsonObject { ["value"] = 1 }, origin: "http://evil.example")).Status, "another Origin");
            Equal(415, (await hub.SendAsync(HttpMethod.Put, "/api/ui-state/x", new JsonObject { ["value"] = 1 }, contentType: "text/plain")).Status, "a form post");
            Equal(200, (await hub.SendAsync(HttpMethod.Put, "/api/ui-state/x", new JsonObject { ["value"] = 1 }, origin: hub.Base.ToString().TrimEnd('/'))).Status, "same origin");
        }),

        Case("hub.local-device-registry-and-ui-state", async () =>
        {
            await using var hub = await Hub.StartAsync("registry");
            await hub.WaitOnlineAsync();
            var device = (await hub.Get("/api/network")).Body!["devices"]![0]!;
            Equal("local", device["kind"]!.GetValue<string>(), "kind");
            Equal("config", device["source"]!.GetValue<string>(), "this computer's device is read-only");
            Equal("faux-box", device["info"]!["name"]!.GetValue<string>(), "the device's description");
            Check(!device.ToJsonString().Contains(hub.Token, StringComparison.Ordinal), "the token never leaves the hub");
            Equal(400, (await hub.SendAsync(HttpMethod.Delete, "/api/devices/local")).Status, "a config device cannot be removed");
            var rejected = await hub.Post("/api/devices", new JsonObject { ["id"] = "second", ["url"] = hub.DeviceUrl.ToString(), ["token"] = "dpi_" + new string('x', 43) });
            Equal(401, rejected.Status, "a wrong token is refused before it is stored");
            Equal("unauthorized", rejected.Body!["error"]!["code"]!.GetValue<string>(), "code");
            var added = await hub.Post("/api/devices", new JsonObject { ["id"] = "second", ["url"] = hub.DeviceUrl.ToString(), ["token"] = hub.Token });
            Equal(200, added.Status, "a checked token: " + added.Text);
            Equal(409, (await hub.Post("/api/devices", new JsonObject { ["id"] = "second", ["url"] = hub.DeviceUrl.ToString(), ["token"] = hub.Token })).Status, "the same name twice");
            Equal(400, (await hub.Post("/api/devices", new JsonObject { ["id"] = "Bad Name", ["url"] = "http://x", ["token"] = hub.Token })).Status, "an invalid name");
            const string yaml = "defaults:\n  port: 4780\ndevices:\n  mac-mini:\n    kind: ssh\n    ssh: jk@mac-mini.local\n  vm-2:\n    url: https://vm-2.example.net:4780\n";
            var preview = await hub.Post("/api/devices/import", new JsonObject { ["yaml"] = yaml, ["apply"] = false });
            Equal("add,add", string.Join(",", preview.Body!["devices"]!.AsArray().Select(d => d!["result"]!.GetValue<string>())), "preview");
            Equal("http://mac-mini.local:4780", preview.Body!["devices"]![0]!["url"]!.GetValue<string>(), "an ssh device by its host and the default port");
            await hub.Post("/api/devices/import", new JsonObject { ["yaml"] = yaml, ["apply"] = true });
            var ids = (await hub.Get("/api/network")).Body!["devices"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()).ToList();
            Equal("local,second,mac-mini,vm-2", string.Join(",", ids), "devices");
            Equal(400, (await hub.Post("/api/devices/import", new JsonObject { ["yaml"] = "devices: [", ["apply"] = false })).Status, "bad YAML");
            Equal(200, (await hub.SendAsync(HttpMethod.Delete, "/api/devices/second")).Status, "remove a UI device");
            await hub.SendAsync(HttpMethod.Put, "/api/ui-state/selection", new JsonObject { ["value"] = new JsonObject { ["device"] = "local" } });
            Equal("local", (await hub.Get("/api/ui-state/selection")).Body!["value"]!["device"]!.GetValue<string>(), "UI state");
        }),

        Case("hub.proxy-pages-and-panels", async () =>
        {
            await using var hub = await Hub.StartAsync("pages");
            var session = await hub.SessionAsync("release-notes");
            var answered = await hub.Post($"/api/devices/local/v1/sessions/{session}/agents/main/messages",
                new JsonObject { ["content"] = "hello", ["requestId"] = "p-1", ["waitMs"] = 15000 });
            Equal("Echo: hello", answered.Body!["answer"]!["text"]!.GetValue<string>(), "a send through the proxy");
            Equal(400, (await hub.Get($"/api/devices/local/v1/sessions/{session}/events")).Status, "streams do not go through the proxy");
            Equal(404, (await hub.Get("/api/devices/nope/v1/device")).Status, "an unknown device");
            await WaitUntilAsync(async () => (await hub.Get("/")).Text.Contains("release-notes", StringComparison.Ordinal), "the tree has the session");
            var home = await hub.Get("/");
            Equal(200, home.Status, "the network page");
            Check(home.Text.Contains("tree-row", StringComparison.Ordinal), "the tree is rendered on the server");
            Equal(200, (await hub.Get($"/d/local/s/{session}/a/1")).Status, "a deep link");
            var panels = new Dictionary<string, string>
            {
                ["session"] = "data-session-tab",
                ["tools"] = "<span class=\"sig-f\">read</span>",
                ["extend"] = "Command guard",
                ["jobs"] = "Workspace snapshot",
            };
            foreach (var (panel, expected) in panels)
            {
                var html = await hub.Get($"/ui/local/{session}/1/{panel}");
                Equal(200, html.Status, panel);
                Check(html.Text.Contains(expected, StringComparison.Ordinal), $"the {panel} panel: {Cut(html.Text)}");
            }
            Check((await hub.Get($"/ui/local/{session}/runtime")).Text.Contains("Recovery inspection", StringComparison.Ordinal), "runtime details");
            var devices = await hub.Get("/devices");
            Check(devices.Text.Contains("faux-box", StringComparison.Ordinal) || devices.Text.Contains("local", StringComparison.Ordinal), "the devices page");
        }),

        Case("hub.stream-renders-agent-views", async () =>
        {
            await using var hub = await Hub.StartAsync("stream");
            var session = await hub.SessionAsync("streams");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var response = await hub.Http.GetAsync(new Uri(hub.Base, "/api/stream"), HttpCompletionOption.ResponseHeadersRead, stop.Token);
            await using var body = await response.Content.ReadAsStreamAsync(stop.Token);
            var frames = SseReader.ReadAsync(body, cancellationToken: stop.Token).GetAsyncEnumerator(stop.Token);
            var skipped = new List<JsonObject>();
            async Task<JsonObject> Next(Func<JsonObject, bool> wanted, string what)
            {
                if (skipped.FirstOrDefault(wanted) is { } earlier)
                {
                    skipped.Remove(earlier);
                    return earlier;
                }
                while (await frames.MoveNextAsync())
                {
                    if (wanted(frames.Current.Data)) return frames.Current.Data;
                    skipped.Add(frames.Current.Data);
                }
                throw new InvalidOperationException("The stream ended before " + what);
            }
            var hello = await Next(f => f["sub"]!.GetValue<string>() == "hello", "hello");
            var streamId = hello["frame"]!["streamId"]!.GetValue<string>();
            var network = await Next(f => f["sub"]!.GetValue<string>() == "network", "the network");
            Check(network["frame"]!["html"]!.GetValue<string>().Contains("tree-row", StringComparison.Ordinal), "the tree's HTML");
            var key = $"local/agent/{session}/1";
            var subscribed = await hub.SendAsync(HttpMethod.Put, $"/api/stream/{streamId}", new JsonObject { ["subs"] = new JsonArray(key, "local/device?docs=app.shared-state") });
            Equal(200, subscribed.Status, "subscribe: " + subscribed.Text);
            Equal(400, (await hub.SendAsync(HttpMethod.Put, $"/api/stream/{streamId}", new JsonObject { ["subs"] = new JsonArray("not a key") })).Status, "an invalid key");
            await hub.SendAsync(HttpMethod.Put, $"/api/stream/{streamId}", new JsonObject { ["subs"] = new JsonArray(key, "local/device?docs=app.shared-state") });
            var first = await Next(f => f["sub"]!.GetValue<string>() == key && f["frame"]!["type"]!.GetValue<string>() == "view", "the first view");
            Check(first["frame"]!["reset"]!.GetValue<bool>(), "the first view resets the thread");
            await Next(f => f["sub"]!.GetValue<string>() == "local/device?docs=app.shared-state" && f["frame"]!["type"]!.GetValue<string>() == "doc", "the App State");
            await hub.Post($"/api/devices/local/v1/sessions/{session}/agents/main/messages", new JsonObject { ["content"] = "**hello hub**", ["requestId"] = "s-1" });
            var answer = await Next(f => f["sub"]!.GetValue<string>() == key && f["frame"]!["items"]?["upsert"]?.AsArray()
                .Any(u => u!["html"]!.GetValue<string>().Contains("Echo: <strong>hello hub</strong>", StringComparison.Ordinal)) == true, "the rendered answer");
            var order = answer["frame"]!["items"]!["order"]!.AsArray().Select(k => k!.GetValue<string>()).ToList();
            Check(order.Count >= 2 && order.All(k => k.StartsWith('e')), "items in entry order: " + string.Join(",", order));
            Check(answer["frame"]!["state"]!["agents"]!.AsArray().Count == 1, "the session's agents ride along");
            await stop.CancelAsync();
        }),
    ];

    private static string Cut(string text) => text.Length > 400 ? text[..400] + "…" : text;
}
