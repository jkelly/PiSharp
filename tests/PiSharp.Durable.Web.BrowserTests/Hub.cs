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
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Server;

internal static partial class Program
{
    /// <summary>A faux device in this process, published as this computer's device, and the hub as a child process (PiSharp.Durable.Web.Tests'
    /// fixture, plus a prompt file and a device that can be stopped and started again on its port).</summary>
    internal sealed class Hub : IAsyncDisposable
    {
        /// <summary>The device's one prompt file, for the Extend tab's checklist.</summary>
        public const string PromptFile = "release-voice.md";

        private Process? process;
        private readonly StringBuilder log = new();

        private Hub(string root) => Root = root;

        public string Root { get; }
        public string Work => Path.Join(Root, "work");
        public string DeviceDirectory => Path.Join(Root, "device");
        public string Token { get; private set; } = "";
        public Uri DeviceUrl { get; private set; } = null!;
        public DurableDeviceHost Device { get; private set; } = null!;
        public WebApplication? DeviceApp { get; private set; }
        public Uri Base { get; private set; } = null!;
        public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

        public static async Task<Hub> StartAsync(string name)
        {
            var hub = new Hub(Temp(name));
            Directory.CreateDirectory(hub.Work);
            File.WriteAllText(Path.Join(hub.Work, "README.md"), "# Work\n");
            var prompts = Path.Join(hub.DeviceDirectory, "prompts");
            Directory.CreateDirectory(prompts);
            File.WriteAllText(Path.Join(prompts, PromptFile), "# Write release notes in a calm voice\n\nShort sentences. No exclamation marks.\n");
            hub.Token = DeviceConfig.RotateToken(hub.DeviceDirectory);
            hub.Device = await DurableDeviceHost.OpenAsync(new DurableDeviceOptions
            {
                DataDirectory = Path.Join(hub.DeviceDirectory, "data"),
                StartDirectories = [hub.Work],
                DeviceName = "faux-box",
                Models = new FauxDurableModels(),
                DefaultModel = FauxDurableModels.EchoRef,
                PromptsDirectory = prompts,
            });
            await hub.StartDeviceAsync("http://127.0.0.1:0");
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
            await hub.WaitOnlineAsync();
            return hub;
        }

        public string Log { get { lock (log) return log.ToString(); } }

        /// <summary>Serves the device at <paramref name="url"/>; port 0 picks one.</summary>
        public async Task StartDeviceAsync(string url)
        {
            DeviceApp = DurableDeviceServer.Build(Device, new DurableDeviceServerOptions { Token = Token }, url);
            await DeviceApp.StartAsync();
            DeviceUrl = new Uri(DeviceApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        }

        /// <summary>Stops serving the device, as if its process died; its data stays open in this process.</summary>
        public async Task StopDeviceAsync()
        {
            if (DeviceApp is null) return;
            using (var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2))) await DeviceApp.StopAsync(limit.Token);
            await DeviceApp.DisposeAsync();
            DeviceApp = null;
        }

        /// <summary>Serves the device again on its port.</summary>
        public Task RestartDeviceAsync() => StartDeviceAsync($"http://127.0.0.1:{DeviceUrl.Port}");

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

        public Task<(int Status, JsonNode? Body, string Text)> Post(string path, JsonNode? body) => SendAsync(HttpMethod.Post, path, body ?? new JsonObject());

        /// <summary>A session made through the proxy, as another client would, once the hub's tree lists it. (A page that opens it sooner
        /// may get a network frame whose tree is older than the session, and the page then leaves the selection.)</summary>
        public async Task<string> SessionAsync(string name)
        {
            var created = await Post("/api/devices/local/v1/sessions", new JsonObject { ["name"] = name, ["cwd"] = Work });
            Equal(201, created.Status, "create a session through the proxy: " + created.Text);
            var id = created.Body!["id"]!.GetValue<string>();
            await WaitInTreeAsync(id, 1);
            return id;
        }

        /// <summary>A new agent in <paramref name="session"/>, made through the proxy, once the hub's tree lists it.</summary>
        public async Task<long> AgentAsync(string session, string name)
        {
            var created = await Post($"/api/devices/local/v1/sessions/{session}/agents", new JsonObject { ["name"] = name });
            Check(created.Status is 200 or 201, "create an agent through the proxy: " + created.Text);
            var id = created.Body!["id"]!.GetValue<long>();
            await WaitInTreeAsync(session, id);
            return id;
        }

        private Task WaitInTreeAsync(string session, long agent) => WaitUntilAsync(async () =>
            (await Get("/api/network")).Body?["devices"]?.AsArray().FirstOrDefault(d => d!["id"]!.GetValue<string>() == "local")?["tree"]?["sessions"]?.AsArray()
                .FirstOrDefault(s => s!["id"]!.GetValue<string>() == session)?["agents"]?.AsArray().Any(a => a!["id"]!.GetValue<long>() == agent) == true,
            $"the hub's tree lists agent {agent} of session {session}");

        public Task WaitOnlineAsync() => WaitUntilAsync(async () =>
            (await Get("/api/network")).Body?["devices"]?.AsArray().Any(d => d!["id"]!.GetValue<string>() == "local" && d["status"]!.GetValue<string>() == "online") == true,
            "local is online", TimeSpan.FromSeconds(30));

        public async ValueTask DisposeAsync()
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            if (process is not null) await process.WaitForExitAsync();
            process?.Dispose();
            Http.Dispose();
            await StopDeviceAsync();
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
}
