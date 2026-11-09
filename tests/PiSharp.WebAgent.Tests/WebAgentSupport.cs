using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.Web.Common;
using PiSharp.WebAgent.Hosting;

// Decision 0011 (run W2, WU4): support for every PiSharp.WebAgent.Tests case. WebAgentApp starts the built app as a child process with the
// faux switch, a temporary data folder (by --data or PISHARP_WEBAGENT_DATA, never the user's own) and a free loopback port, with provider
// keys and URL settings removed from its environment; RenderHost runs the app's Razor views in process through Web.Common's
// FragmentRenderer; ReadDb reads webagent.db read-only. Area units reuse these for their own cases.
internal static partial class Program
{
    /// <summary>How <see cref="WebAgentApp"/> starts the app.</summary>
    internal sealed record AppStart
    {
        /// <summary>The <c>--data</c> folder, or null for no <c>--data</c> argument (then <see cref="EnvironmentData"/> must be set).</summary>
        public string? DataDirectory { get; init; }

        /// <summary><c>PISHARP_WEBAGENT_DATA</c> for the child, or null to leave it unset.</summary>
        public string? EnvironmentData { get; init; }

        /// <summary>Whether to pass the faux switch (default true: no provider code path, no network).</summary>
        public bool Faux { get; init; } = true;

        /// <summary>More arguments, after the others.</summary>
        public IReadOnlyList<string> Arguments { get; init; } = [];

        /// <summary>More environment variables for the child.</summary>
        public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>The Web Agent as a child process on a free loopback port.</summary>
    internal sealed class WebAgentApp : IAsyncDisposable
    {
        private readonly Process process;
        private readonly StringBuilder log;
        private readonly bool ownsRoot;

        private WebAgentApp(Process process, StringBuilder log, string root, Uri baseUri, bool ownsRoot)
        {
            this.process = process;
            this.log = log;
            this.ownsRoot = ownsRoot;
            Root = root;
            Base = baseUri;
        }

        /// <summary>The temporary folder of this case.</summary>
        public string Root { get; }

        /// <summary>The app's address, such as <c>http://127.0.0.1:53123/</c>.</summary>
        public Uri Base { get; }

        /// <summary>The origin of <see cref="Base"/>, for same-origin writes.</summary>
        public string Origin => Base.GetLeftPart(UriPartial.Authority);

        public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

        /// <summary>The app's console output so far.</summary>
        public string Log { get { lock (log) return log.ToString(); } }

        /// <summary>The app with its data in <c>&lt;temp&gt;/data</c> (by <c>--data</c>); the temporary folder goes when the app is disposed.</summary>
        public static Task<WebAgentApp> StartAsync(string name)
        {
            var root = Temp(name);
            return LaunchAsync(root, new AppStart { DataDirectory = Path.Join(root, "data") }, ownsRoot: true);
        }

        /// <summary>The app in <paramref name="root"/> as <paramref name="start"/> says; the caller removes <paramref name="root"/>.</summary>
        public static Task<WebAgentApp> StartAsync(string root, AppStart start) => LaunchAsync(root, start, ownsRoot: false);

        private static async Task<WebAgentApp> LaunchAsync(string root, AppStart start, bool ownsRoot)
        {
            var port = FreePort();
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var log = new StringBuilder();
            var process = Process.Start(StartInfo(start, ["--urls", baseUri.GetLeftPart(UriPartial.Authority)]))!;
            process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var app = new WebAgentApp(process, log, root, baseUri, ownsRoot);
            try
            {
                await WaitUntilAsync(async () =>
                {
                    if (process.HasExited) throw new InvalidOperationException($"The app exited ({process.ExitCode}): " + app.Log);
                    try { return (await app.Http.GetAsync(new Uri(baseUri, "/api/health"))).IsSuccessStatusCode; }
                    catch (HttpRequestException) { return false; }
                }, "the app answers /api/health", TimeSpan.FromSeconds(60));
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
            return app;
        }

        /// <summary>Runs the app until it exits by itself (a start it must refuse, or an idle exit), and returns its exit code and output;
        /// after <paramref name="limit"/> (default 60 seconds) it is killed and the call fails.</summary>
        public static async Task<(int ExitCode, string Output)> RunToExitAsync(AppStart start, IReadOnlyList<string> hostArguments, TimeSpan? limit = null)
        {
            var output = new StringBuilder();
            using var process = Process.Start(StartInfo(start, hostArguments))!;
            process.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(limit ?? TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("The app did not exit: " + output);
            }
            process.WaitForExit();
            lock (output) return (process.ExitCode, output.ToString());
        }

        private static ProcessStartInfo StartInfo(AppStart start, IReadOnlyList<string> hostArguments)
        {
            if (start.DataDirectory is null && start.EnvironmentData is null)
                throw new InvalidOperationException("A test app needs a temporary data folder (--data or PISHARP_WEBAGENT_DATA), never the user's own.");
            var dotnetHost = Environment.GetEnvironmentVariable("PISHARP_TEST_DOTNET");
            var info = new ProcessStartInfo(string.IsNullOrWhiteSpace(dotnetHost) ? "dotnet" : dotnetHost)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(AppDll());
            if (start.Faux) info.ArgumentList.Add("--faux");
            if (start.DataDirectory is not null)
            {
                info.ArgumentList.Add("--data");
                info.ArgumentList.Add(start.DataDirectory);
            }
            foreach (var argument in hostArguments) info.ArgumentList.Add(argument);
            info.ArgumentList.Add("--Logging:LogLevel:Default=Warning");
            // Test children keep console warnings, without writing to the machine-wide Windows Event Log.
            info.ArgumentList.Add("--Logging:EventLog:LogLevel:Default=None");
            foreach (var argument in start.Arguments) info.ArgumentList.Add(argument);
            // No provider key, URL setting or data folder of this machine reaches the app under test.
            foreach (var name in info.Environment.Keys.ToList())
                if (name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_URLS", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("HTTP_PORTS", StringComparison.OrdinalIgnoreCase) || name.EndsWith("HTTPS_PORTS", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(WebAgentOptions.DataEnvironmentVariable, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) || name.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
                    info.Environment.Remove(name);
            if (start.EnvironmentData is not null) info.Environment[WebAgentOptions.DataEnvironmentVariable] = start.EnvironmentData;
            foreach (var (name, value) in start.Environment) info.Environment[name] = value;
            return info;
        }

        /// <summary>The built app: <c>src/PiSharp.WebAgent/bin/&lt;configuration&gt;/net10.0/PiSharp.WebAgent.dll</c>.</summary>
        public static string AppDll()
        {
            var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
            return Path.GetFullPath(Path.Join(metadata["WebProject"]!, "bin", metadata["Configuration"]!, "net10.0", "PiSharp.WebAgent.dll"));
        }

        public async Task<(int Status, JsonNode? Body, string Text, HttpResponseMessage Response)> SendAsync(HttpMethod method, string path,
            JsonNode? body = null, string? host = null, string? origin = null, string contentType = "application/json", string? text = null)
        {
            using var request = new HttpRequestMessage(method, new Uri(Base, path));
            if (text is not null) request.Content = new StringContent(text, Encoding.UTF8, contentType);
            else if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, contentType);
            if (host is not null) request.Headers.Host = host;
            if (origin is not null) request.Headers.Add("Origin", origin);
            var response = await Http.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            JsonNode? json = null;
            try { json = content.Length > 0 && content[0] is '{' or '[' ? JsonNode.Parse(content) : null; } catch (System.Text.Json.JsonException) { /* Not JSON. */ }
            return ((int)response.StatusCode, json, content, response);
        }

        public async Task<(int Status, JsonNode? Body, string Text, HttpResponseMessage Response)> Get(string path) =>
            await SendAsync(HttpMethod.Get, path);

        /// <summary>Stops the app (it does not shut down gracefully: a kill, as a crash would).</summary>
        public async Task StopAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            process.Dispose();
            Http.Dispose();
            if (ownsRoot) DeleteTemp(Root);
        }
    }

    /// <summary>Removes a temporary folder, ignoring what is still in use.</summary>
    internal static void DeleteTemp(string path)
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

    /// <summary>Rows of <paramref name="sql"/> from the database at <paramref name="path"/>, opened read-only and not pooled (so the file is
    /// free again at once).</summary>
    internal static List<T> ReadDb<T>(string path, string sql, Func<SqliteDataReader, T> read)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    /// <summary>An in-process MVC app with the Web Agent's and Web.Common's views, for rendering partials with
    /// <see cref="FragmentRenderer"/>. <paramref name="configure"/> registers what the views inject.</summary>
    internal sealed class RenderHost : IAsyncDisposable
    {
        private readonly WebApplication app;

        private RenderHost(WebApplication app, string root)
        {
            this.app = app;
            Root = root;
        }

        public string Root { get; }

        public IServiceProvider Services => app.Services;

        public FragmentRenderer Renderer => app.Services.GetRequiredService<FragmentRenderer>();

        public static RenderHost Create(string name, Action<IServiceCollection, string> configure)
        {
            var root = Temp(name);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.Services.AddControllersWithViews().ConfigureApplicationPartManager(parts =>
            {
                foreach (var assembly in new[] { typeof(WebAgentHost).Assembly, typeof(FragmentRenderer).Assembly })
                    if (!parts.ApplicationParts.OfType<AssemblyPart>().Any(part => part.Assembly == assembly))
                        foreach (var part in ApplicationPartFactory.GetApplicationPartFactory(assembly).GetApplicationParts(assembly))
                            parts.ApplicationParts.Add(part);
            });
            builder.Services.AddWebCommon();
            configure(builder.Services, root);
            return new RenderHost(builder.Build(), root);
        }

        public Task<string> RenderAsync(string partial, object? model) => Renderer.RenderAsync("/Views/Shared/" + partial + ".cshtml", model);

        public async ValueTask DisposeAsync()
        {
            await app.DisposeAsync();
            DeleteTemp(Root);
        }
    }
}
