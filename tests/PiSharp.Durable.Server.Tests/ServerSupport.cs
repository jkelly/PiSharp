using System.Diagnostics;
using System.Net.Http.Headers;
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
    /// <summary>How long a case waits for something to happen.</summary>
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    internal static DurableDeviceOptions DeviceOptions(string data, string work, string name = "test-box") => new()
    {
        DataDirectory = data,
        StartDirectories = [work],
        DeviceName = name,
        Models = new FauxDurableModels(),
        DefaultModel = FauxDurableModels.EchoRef,
    };

    /// <summary>A device and its server on a free loopback port, on a fresh temporary directory.</summary>
    internal sealed class TestServer : IAsyncDisposable
    {
        private TestServer(string root)
        {
            Root = root;
            Data = Path.Join(root, "data");
            Work = Path.Join(root, "work");
            Directory.CreateDirectory(Path.Join(Work, "project"));
        }

        public string Root { get; }
        public string Data { get; }
        public string Work { get; }
        public string Token { get; } = DeviceTokens.Generate();
        public DurableDeviceHost Host { get; private set; } = null!;
        public WebApplication App { get; private set; } = null!;
        public Uri Base { get; private set; } = null!;
        public DurableDeviceClient Client { get; private set; } = null!;
        public HttpClient Raw { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

        public static async Task<TestServer> StartAsync(string name, string deviceName = "test-box")
        {
            var server = new TestServer(Temp(name));
            server.Host = await DurableDeviceHost.OpenAsync(DeviceOptions(server.Data, server.Work, deviceName));
            server.App = DurableDeviceServer.Build(server.Host, new DurableDeviceServerOptions { Token = server.Token }, "http://127.0.0.1:0");
            await server.App.StartAsync();
            var address = server.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            server.Base = new Uri(address);
            server.Client = new DurableDeviceClient(server.Base, server.Token);
            return server;
        }

        public Task<JsonNode?> Get(string path) => Client.GetAsync(path);

        public Task<JsonNode?> Post(string path, JsonNode? body = null, string? key = null) => Client.SendAsync(HttpMethod.Post, path, body ?? new JsonObject(), key);

        public Task<JsonNode?> Patch(string path, JsonNode body) => Client.SendAsync(HttpMethod.Patch, path, body);

        public Task<JsonNode?> Delete(string path) => Client.SendAsync(HttpMethod.Delete, path);

        /// <summary>A request with the token and its raw response.</summary>
        public async Task<(int Status, JsonNode? Body)> RawAsync(HttpMethod method, string path, string? body = null, string? contentType = "application/json",
            bool token = true)
        {
            using var request = new HttpRequestMessage(method, new Uri(Base, path));
            if (token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "text/plain");
            using var response = await Raw.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            JsonNode? json = null;
            try { json = text.Length == 0 ? null : JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { /* Not JSON. */ }
            return ((int)response.StatusCode, json);
        }

        public async Task<string> CreateSessionAsync(string name, string? key = null) =>
            (await Post("/v1/sessions", new JsonObject
            {
                ["name"] = name,
                ["cwd"] = Work,
                ["model"] = new JsonObject { ["provider"] = "faux", ["modelId"] = "echo" },
            }, key))!["id"]!.GetValue<string>();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Raw.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            await Host.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A temporary directory. */ }
        }
    }

    /// <summary>Frames of a stream, collected in the background until disposed.</summary>
    internal sealed class FrameLog : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly List<JsonObject> frames = [];
        private readonly Task reading;

        public FrameLog(DurableDeviceClient client, string path, Action<JsonObject>? onFrame = null)
        {
            reading = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in client.StreamAsync(path, cancellationToken: stop.Token))
                    {
                        lock (frames)
                        {
                            frames.Add(frame.Data);
                            onFrame?.Invoke(frame.Data);
                        }
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or DurableApiException) { /* Stopped. */ }
            });
        }

        public IReadOnlyList<JsonObject> Frames
        {
            get { lock (frames) return [.. frames]; }
        }

        public bool Any(Func<JsonObject, bool> predicate)
        {
            lock (frames) return frames.Any(predicate);
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await reading;
            stop.Dispose();
        }
    }

    internal static string Type(JsonObject frame) => frame["type"]?.GetValue<string>() ?? "";

    /// <summary>Waits until <paramref name="condition"/> holds, checking every 25 ms.</summary>
    internal static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, TimeSpan? limit = null)
    {
        var deadline = DateTime.UtcNow + (limit ?? Patience);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting until " + what);
            await Task.Delay(25);
        }
    }

    internal static Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? limit = null) =>
        WaitUntilAsync(() => Task.FromResult(condition()), what, limit);

    internal static async Task<DurableApiException> FailsAsync(Func<Task> run, string code, string what)
    {
        var error = await ThrowsAsync<DurableApiException>(run, what);
        Equal(code, error.Code, what + " (code)");
        return error;
    }

    // ─── Crash child ────────────────────────────────────────────────────────

    /// <summary>The child of a kill test: serves the data directory on a free port, prints <c>listening &lt;url&gt;</c>, and runs until
    /// killed.</summary>
    private static async Task<int> ServeChildAsync(string data, string work, string token)
    {
        await using var host = await DurableDeviceHost.OpenAsync(DeviceOptions(data, work, "crash-box"));
        await using var app = DurableDeviceServer.Build(host, new DurableDeviceServerOptions { Token = token }, "http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Console.WriteLine("listening " + address);
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.Infinite);
        return 0;
    }

    /// <summary>A server in a child process, which a test can kill.</summary>
    internal sealed class ChildServer : IAsyncDisposable
    {
        private Process? process;

        public ChildServer(string root)
        {
            Root = root;
            Data = Path.Join(root, "data");
            Work = Path.Join(root, "work");
            Directory.CreateDirectory(Work);
        }

        public string Root { get; }
        public string Data { get; }
        public string Work { get; }
        public string Token { get; } = DeviceTokens.Generate();
        public DurableDeviceClient? Client { get; private set; }

        public async Task StartAsync()
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) is "dotnet") start.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (var arg in new[] { "--serve-child", Data, Work, Token }) start.ArgumentList.Add(arg);
            process = Process.Start(start)!;
            _ = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(limit.Token) ?? throw new InvalidOperationException("The child server exited.");
                if (!line.StartsWith("listening ", StringComparison.Ordinal)) continue;
                Client?.Dispose();
                Client = new DurableDeviceClient(new Uri(line["listening ".Length..]), Token);
                return;
            }
        }

        /// <summary>Kills the child at once, as SIGKILL does.</summary>
        public async Task KillAsync()
        {
            if (process is null) return;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            process = null;
        }

        public async ValueTask DisposeAsync()
        {
            await KillAsync();
            Client?.Dispose();
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A temporary directory. */ }
        }
    }
}
