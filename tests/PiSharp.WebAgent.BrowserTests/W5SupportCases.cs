using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

// Decision 0011 (run W5, T5): the helpers of the W5 browser cases.
//
// Drift needs a transport whose response model changes, and the faux provider reports none, so the end-to-end flow (W5FlowCases) runs on a
// scripted provider instead: W5ScriptedProvider is an OpenAI-compatible chat completions endpoint on a loopback port of this process (a
// TcpListener: this project has no ASP.NET Core), answering every request at once with "Answer: <the last user message>", 10 input and 5
// output tokens, and the requested model's id plus a suffix as the model it reports (kestrel-7b-2026-05), which a case switches
// (Suffix = "2026-09") to script a silent model update. It keeps each request's body (Bodies), so a case reads what a run put on the wire
// (W5SamplingCases). W5LiveApp starts the app without --faux, sealed as Agent's Live mode is (no
// provider keys, tokens, AWS, Google, Azure or Cloudflare settings from this machine, and every proxy a closed loopback port) except that
// NO_PROXY is 127.0.0.1, so the one place the app can reach is this computer's loopback address, where the scripted provider listens; its
// custom provider (Settings › Providers' POST /api/custom-providers: openai-completions, no key) is loopback and so Free: nothing is paid
// and nothing leaves the computer. Agent (and so Window) takes no extra environment, so this launcher and W5Window (a browser profile
// that fails the case on any page or console error, as Window does) stand beside them; a request asks Agent to take extra environment so
// they can go.
//
// The phone cases (W5LayoutCases) run on the shared Agent with priced faux and Window; W5SuiteAsync and W5BattleAsync fill the app through
// the pages' own JSON APIs (arena battles and votes, a suite with cases, runs, a baseline, a confirmed broken case and a schedule), so every
// page shows real records.
internal static partial class Program
{
    /// <summary>The scripted provider's id, name and models: words no page shows unless it names a contestant.</summary>
    internal const string W5Provider = "zephyrlab";

    internal const string W5ProviderName = "Zephyr Lab";

    internal static readonly (string Id, string Name)[] W5Models = [("kestrel-7b", "Kestrel 7B"), ("osprey-9b", "Osprey 9B")];

    /// <summary>What must not be anywhere in a battle's page, fragments or JSON before the vote.</summary>
    internal static readonly string[] W5Names = ["zephyr", "kestrel", "osprey"];

    /// <summary>Fails when <paramref name="text"/> names a contestant (any of <see cref="W5Names"/>, any case).</summary>
    internal static void W5Blind(string text, string what)
    {
        foreach (var name in W5Names)
        {
            var at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
            Check(at < 0, $"{what} names a contestant before the vote: …{text[Math.Max(0, at - 80)..Math.Min(text.Length, at + 80)]}…");
        }
    }

    /// <summary>A case on a sealed live app (<see cref="W5LiveApp"/>) whose one provider is a <see cref="W5ScriptedProvider"/>, in a fresh
    /// browser profile (<see cref="W5Window"/>) that fails the case on any page or console error.</summary>
    internal static (string, Func<Task>) BrowseScripted(string id, Func<W5LiveApp, W5ScriptedProvider, W5Window, Task> run, int width = 1440, int height = 900) =>
        Case(id, async () =>
        {
            await using var provider = W5ScriptedProvider.Start();
            await using var app = await W5LiveApp.StartAsync(id.Replace("browser.", "", StringComparison.Ordinal));
            await app.AddProviderAsync(provider);
            await using var window = await W5Window.OpenAsync(app, width, height);
            try
            {
                await run(app, provider, window);
                window.NoErrors();
            }
            catch
            {
                await window.SaveAsync(id);
                throw;
            }
        });

    /// <summary>An OpenAI-compatible chat completions endpoint on a loopback port (see the file's header). Every answer streams at once.</summary>
    internal sealed class W5ScriptedProvider : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Lock gate = new();
        private readonly List<string> asked = [];
        private readonly List<JsonObject> bodies = [];
        private readonly List<string> other = [];
        private readonly Task loop;
        private string suffix = "2026-05";
        private TaskCompletionSource? held;

        private W5ScriptedProvider(TcpListener listener)
        {
            this.listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = Task.Run(AcceptAsync);
        }

        public int Port { get; }

        /// <summary>The base URL a custom provider is given.</summary>
        public string BaseUrl => $"http://127.0.0.1:{Port}/v1";

        /// <summary>What follows the requested model's id in the model the answers report (<c>kestrel-7b-2026-05</c>).</summary>
        public string Suffix
        {
            get { lock (gate) return suffix; }
            set { lock (gate) suffix = value; }
        }

        /// <summary>Holds every completion asked from now on (each is counted in <see cref="Asked"/> and waits) until
        /// <see cref="Release"/> (W5 fix 2: so a page shows a run going before it ends).</summary>
        public void Hold()
        {
            lock (gate) held ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>Lets the held completions answer, and answers the ones after them at once again.</summary>
        public void Release()
        {
            TaskCompletionSource? open;
            lock (gate)
            {
                open = held;
                held = null;
            }
            open?.TrySetResult();
        }

        /// <summary>The models of the completions asked so far, in order.</summary>
        public IReadOnlyList<string> Asked { get { lock (gate) return [.. asked]; } }

        /// <summary>The request bodies of the completions asked so far, in order (copies).</summary>
        public IReadOnlyList<JsonObject> Bodies { get { lock (gate) return [.. bodies.Select(body => (JsonObject)body.DeepClone())]; } }

        /// <summary>Every other request (method and path), which the provider answered 404.</summary>
        public IReadOnlyList<string> Other { get { lock (gate) return [.. other]; } }

        public static W5ScriptedProvider Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new W5ScriptedProvider(listener);
        }

        private async Task AcceptAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new BufferedStream(stream);
                    var (method, path, body) = await ReadRequestAsync(reader);
                    if (method == "POST" && path.EndsWith("/chat/completions", StringComparison.Ordinal))
                    {
                        var answer = Completion(body);
                        Task? wait;
                        lock (gate) wait = held?.Task;
                        if (wait is not null) await wait.WaitAsync(stop.Token);
                        await WriteAsync(stream, "200 OK", "text/event-stream", answer);
                    }
                    else
                    {
                        lock (gate) other.Add(method + " " + path);
                        await WriteAsync(stream, "404 Not Found", "application/json", """{"error":{"message":"not found"}}""");
                    }
                }
                catch (Exception error) when (error is IOException or SocketException or System.Text.Json.JsonException or InvalidOperationException or FormatException
                    or OperationCanceledException)
                {
                    // A dropped connection or a request this endpoint does not read: the app sees a failed call.
                }
            }
        }

        /// <summary>The server-sent events of one answer.</summary>
        private string Completion(string body)
        {
            var request = JsonNode.Parse(body);
            var model = request?["model"]?.GetValue<string>() ?? "model";
            var last = "";
            foreach (var message in request?["messages"]?.AsArray() ?? [])
            {
                if (message?["role"]?.GetValue<string>() != "user") continue;
                last = message["content"] switch
                {
                    JsonValue text => text.GetValue<string>(),
                    JsonArray parts => string.Join(" ", parts.Select(part => part?["text"]?.GetValue<string>()).Where(text => text is not null)),
                    _ => "",
                };
            }
            string reported;
            lock (gate)
            {
                asked.Add(model);
                if (request is JsonObject json) bodies.Add((JsonObject)json.DeepClone());
                reported = model + "-" + suffix;
            }
            JsonObject Chunk(JsonArray choices) => new() { ["id"] = "chatcmpl-w5", ["object"] = "chat.completion.chunk", ["created"] = 1, ["model"] = reported, ["choices"] = choices };
            var frames = new[]
            {
                Chunk(new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["role"] = "assistant", ["content"] = "Answer: " + last }, ["finish_reason"] = null })),
                Chunk(new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = new JsonObject(), ["finish_reason"] = "stop" })),
                new JsonObject
                {
                    ["id"] = "chatcmpl-w5", ["object"] = "chat.completion.chunk", ["created"] = 1, ["model"] = reported, ["choices"] = new JsonArray(),
                    ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5, ["total_tokens"] = 15 },
                },
            };
            return string.Concat(frames.Select(frame => "data: " + frame.ToJsonString() + "\n\n")) + "data: [DONE]\n\n";
        }

        private static async Task WriteAsync(NetworkStream stream, string status, string type, string text)
        {
            var body = Encoding.UTF8.GetBytes(text);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nCache-Control: no-cache\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }

        /// <summary>One request: its method, path and body (by Content-Length, or chunked).</summary>
        private static async Task<(string Method, string Path, string Body)> ReadRequestAsync(Stream stream)
        {
            var start = (await LineAsync(stream)).Split(' ');
            if (start.Length < 2) throw new FormatException("Not an HTTP request.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var line = await LineAsync(stream); line.Length > 0; line = await LineAsync(stream))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            var body = new MemoryStream();
            if (headers.TryGetValue("Content-Length", out var length))
                await CopyAsync(stream, body, int.Parse(length, System.Globalization.CultureInfo.InvariantCulture));
            else if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                for (var size = Convert.ToInt32((await LineAsync(stream)).Split(';')[0].Trim(), 16); size > 0;
                    size = Convert.ToInt32((await LineAsync(stream)).Split(';')[0].Trim(), 16))
                {
                    await CopyAsync(stream, body, size);
                    await LineAsync(stream);
                }
                while ((await LineAsync(stream)).Length > 0) { /* Trailers. */ }
            }
            return (start[0], start[1], Encoding.UTF8.GetString(body.ToArray()));
        }

        private static async Task<string> LineAsync(Stream stream)
        {
            var line = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(one) == 0) throw new IOException("The connection closed.");
                if (one[0] == '\n') return line.ToString().TrimEnd('\r');
                line.Append((char)one[0]);
            }
        }

        private static async Task CopyAsync(Stream from, Stream to, int count)
        {
            var buffer = new byte[8192];
            while (count > 0)
            {
                var read = await from.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, count)));
                if (read == 0) throw new IOException("The connection closed.");
                await to.WriteAsync(buffer.AsMemory(0, read));
                count -= read;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Release();
            await stop.CancelAsync();
            listener.Stop();
            try { await loop; } catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { }
            stop.Dispose();
        }
    }

    /// <summary>The app without <c>--faux</c>, sealed but for this computer's loopback address (see the file's header).</summary>
    internal sealed class W5LiveApp : IAsyncDisposable
    {
        private readonly Process process;
        private readonly StringBuilder log = new();

        private W5LiveApp(string root, Process process, Uri baseUri)
        {
            Root = root;
            this.process = process;
            Base = baseUri;
            process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public string Root { get; }

        public Uri Base { get; }

        public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

        public string Log { get { lock (log) return log.ToString(); } }

        public static async Task<W5LiveApp> StartAsync(string name)
        {
            var root = Temp(name);
            var port = FreePort();
            var baseUri = new Uri($"http://127.0.0.1:{port}/");
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add(W5AppDll());
            foreach (var argument in new[] { "--data", Path.Join(root, "data"), "--urls", baseUri.GetLeftPart(UriPartial.Authority), "--Logging:LogLevel:Default=Warning" })
                info.ArgumentList.Add(argument);
            foreach (var variable in info.Environment.Keys.ToList())
                if (W5Unwanted(variable)) info.Environment.Remove(variable);
            foreach (var proxy in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" }) info.Environment[proxy] = "http://127.0.0.1:9";
            // The loopback address alone goes direct (not "localhost", and nothing else): there the scripted provider listens.
            info.Environment["NO_PROXY"] = "127.0.0.1";
            info.Environment["no_proxy"] = "127.0.0.1";
            info.Environment["AWS_SHARED_CREDENTIALS_FILE"] = Path.Join(root, "no-aws-credentials");
            info.Environment["AWS_CONFIG_FILE"] = Path.Join(root, "no-aws-config");
            info.Environment["CLOUDSDK_CONFIG"] = Path.Join(root, "no-gcloud");
            var app = new W5LiveApp(root, Process.Start(info)!, baseUri);
            try
            {
                await WaitUntilAsync(async () =>
                {
                    if (app.process.HasExited) throw new InvalidOperationException($"The app exited ({app.process.ExitCode}): " + app.Log);
                    try { return (await app.Http.GetAsync(new Uri(baseUri, "/api/health"))).IsSuccessStatusCode; }
                    catch (HttpRequestException) { return false; }
                }, "the app answers /api/health", TimeSpan.FromSeconds(60));
                var health = await app.Get("/api/health");
                Check(health.Body?["faux"]?.GetValue<bool>() == false, "the app runs without the faux switch: " + health.Text);
                return app;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        /// <summary>Adds <paramref name="provider"/> as the custom provider <see cref="W5Provider"/> with <see cref="W5Models"/>, no key.</summary>
        public async Task AddProviderAsync(W5ScriptedProvider provider)
        {
            var added = await PostAsync("/api/custom-providers", new JsonObject
            {
                ["id"] = W5Provider,
                ["name"] = W5ProviderName,
                ["baseUrl"] = provider.BaseUrl,
                ["api"] = "openai-completions",
                ["key"] = new JsonObject { ["source"] = "none" },
                ["models"] = new JsonArray([.. W5Models.Select(model => (JsonNode)new JsonObject { ["id"] = model.Id, ["name"] = model.Name })]),
            });
            Equal(200, added.Status, "the scripted provider is added: " + added.Text);
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

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            process.Dispose();
            Http.Dispose();
            DeleteFolder(Root);
        }
    }

    /// <summary>Whether the sealed app must not see <paramref name="variable"/> (Agent's Live mode rules): provider keys, tokens and secrets,
    /// URL and port settings, the data folder, the environment name, every AWS, Google, Azure or Cloudflare setting and every proxy setting.</summary>
    private static bool W5Unwanted(string variable) =>
        variable.EndsWith("_KEY", StringComparison.OrdinalIgnoreCase) || variable.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
        variable.EndsWith("_SECRET", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("_URLS", StringComparison.OrdinalIgnoreCase) ||
        variable.EndsWith("HTTP_PORTS", StringComparison.OrdinalIgnoreCase) || variable.EndsWith("HTTPS_PORTS", StringComparison.OrdinalIgnoreCase) ||
        variable.Equals("PISHARP_WEBAGENT_DATA", StringComparison.OrdinalIgnoreCase) ||
        variable.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) || variable.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase) ||
        variable.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) || variable.StartsWith("GOOGLE_", StringComparison.OrdinalIgnoreCase) ||
        variable.StartsWith("GCLOUD_", StringComparison.OrdinalIgnoreCase) || variable.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) ||
        variable.StartsWith("CLOUDFLARE_", StringComparison.OrdinalIgnoreCase) || variable.Contains("PROXY", StringComparison.OrdinalIgnoreCase);

    private static string W5AppDll()
    {
        var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(a => a.Key, a => a.Value);
        return Path.GetFullPath(Path.Join(metadata["WebProject"]!, "bin", metadata["Configuration"]!, "net10.0", "PiSharp.WebAgent.dll"));
    }

    /// <summary>A browser profile on a <see cref="W5LiveApp"/>: every tab records uncaught errors and console errors, as Window does.</summary>
    internal sealed class W5Window : IAsyncDisposable
    {
        private readonly List<string> errors = [];
        private readonly List<IPage> pages = [];

        private W5Window(W5LiveApp app, IBrowserContext context)
        {
            App = app;
            Context = context;
        }

        public W5LiveApp App { get; }

        public IBrowserContext Context { get; }

        public IPage Page { get; private set; } = null!;

        public static async Task<W5Window> OpenAsync(W5LiveApp app, int width, int height)
        {
            var context = await (await BrowserAsync()).NewContextAsync(new BrowserNewContextOptions
            {
                BaseURL = app.Base.ToString(),
                ViewportSize = new ViewportSize { Width = width, Height = height },
                ReducedMotion = ReducedMotion.Reduce,
            });
            context.SetDefaultTimeout(15_000);
            var window = new W5Window(app, context);
            window.Page = await window.NewPageAsync();
            return window;
        }

        public async Task<IPage> NewPageAsync()
        {
            var page = await Context.NewPageAsync();
            var tab = pages.Count + 1;
            page.PageError += (_, error) => { lock (errors) errors.Add($"tab {tab} page error: {error}"); };
            page.Console += (_, message) =>
            {
                if (message.Type == "error") lock (errors) errors.Add($"tab {tab} console error at {message.Location}: {message.Text}");
            };
            pages.Add(page);
            return page;
        }

        public void NoErrors()
        {
            lock (errors) Check(errors.Count == 0, "page or console errors: " + string.Join(" | ", errors));
        }

        public async Task SaveAsync(string id)
        {
            if (Artifacts is null) return;
            Directory.CreateDirectory(Artifacts);
            for (var i = 0; i < pages.Count; i++)
            {
                if (pages[i].IsClosed) continue;
                var name = Path.Join(Artifacts, id + (i == 0 ? "" : "-tab" + (i + 1)));
                try
                {
                    await pages[i].ScreenshotAsync(new PageScreenshotOptions { Path = name + ".png", FullPage = true });
                    await File.WriteAllTextAsync(name + ".html", await pages[i].ContentAsync());
                }
                catch (PlaywrightException) { /* The page went away. */ }
            }
            lock (errors) if (errors.Count > 0) File.AppendAllLines(Path.Join(Artifacts, id + "-errors.txt"), errors);
            await File.WriteAllTextAsync(Path.Join(Artifacts, id + "-app.log"), App.Log);
        }

        public async ValueTask DisposeAsync() => await Context.DisposeAsync();
    }

    // ─── Seeding through the pages' own APIs (priced faux) ──────────────────

    /// <summary>A request battle on faux <paramref name="models"/> (<c>POST /api/evals/arena</c>); returns its id once it is
    /// <paramref name="until"/> (null: as created).</summary>
    internal static async Task<string> W5BattleAsync(this Agent app, string prompt, string? until, params string[] models)
    {
        var created = await app.PostAsync("/api/evals/arena", new JsonObject
        {
            ["mode"] = "request",
            ["draft"] = new JsonObject
            {
                ["prompt"] = prompt,
                ["columns"] = new JsonArray([.. models.Select(model => (JsonNode)new JsonObject { ["provider"] = "faux", ["model"] = model })]),
            },
        });
        Check(created.Status == 200 && created.Body?["posted"]?.GetValue<bool>() == true, "a blind battle: " + created.Text);
        var id = created.Body!["battleId"]!.GetValue<string>();
        if (until is not null)
            await WaitUntilAsync(async () => (await app.Get("/api/evals/arena/" + id)).Body?["status"]?.GetValue<string>() == until,
                $"battle {id} is {until}", TimeSpan.FromSeconds(60));
        return id;
    }

    /// <summary>Votes on battle <paramref name="id"/> (<c>POST /api/evals/arena/{id}/vote</c>) and returns the revealed battle.</summary>
    internal static async Task<JsonNode> W5VoteAsync(this Agent app, string id, string kind, string? pick = null)
    {
        var body = new JsonObject { ["kind"] = kind };
        if (pick is not null) body["pick"] = pick;
        var voted = await app.PostAsync($"/api/evals/arena/{id}/vote", body);
        Check(voted.Status == 200 && voted.Body?["revealed"] is JsonArray, "a vote: " + voted.Text);
        return voted.Body!;
    }

    /// <summary>A blind agent battle (faux <paramref name="models"/>, copies of a small folder, tools on Auto) once both agents are done;
    /// returns its id.</summary>
    internal static async Task<string> W5AgentBattleAsync(this Agent app, string requestId, params string[] models)
    {
        var created = await app.PostAsync("/api/evals/arena", new JsonObject
        {
            ["mode"] = "agent",
            ["agent"] = new JsonObject
            {
                ["requestId"] = requestId,
                ["task"] = "!echo done > result.txt",
                ["projectPath"] = app.W4Project("arena-" + requestId),
                ["models"] = new JsonArray([.. models.Select(model => (JsonNode)new JsonObject { ["provider"] = "faux", ["model"] = model })]),
                ["isolation"] = "copy",
                ["approval"] = "auto",
                ["checkCommand"] = "echo checked",
            },
        });
        Equal(200, created.Status, "a blind agent battle: " + created.Text);
        var id = created.Body!["battleId"]!.GetValue<string>();
        await WaitUntilAsync(async () => (await app.Get("/api/evals/arena/" + id)).Body?["status"]?.GetValue<string>() is "ready" or "void",
            $"agent battle {id} finished", TimeSpan.FromSeconds(90));
        Equal("ready", (await app.Get("/api/evals/arena/" + id)).Body?["status"]?.GetValue<string>(), "the agent battle is ready to vote");
        return id;
    }

    /// <summary>What the W5 phone cases show of a suite.</summary>
    /// <param name="Suite">The suite: two faux targets, two cases, a daily schedule, run #1 as its baseline.</param>
    /// <param name="Case">Its first case.</param>
    /// <param name="First">Run #1 (complete, the baseline).</param>
    /// <param name="Last">The last run (complete, drift: it confirms the broken case run #2 saw first).</param>
    internal sealed record W5Suite(string Suite, string Case, string First, string Last);

    /// <summary>A suite through the Suites unit's API: echo and echo-small, k = 3, two cases checked for what the echo says; run #1 accepted
    /// as the baseline; then the first case's check changed so it fails every repeat, and two more runs (run #2 suspects a broken case,
    /// run #3 confirms it: drift); a daily schedule.</summary>
    internal static async Task<W5Suite> W5SuiteAsync(this Agent app, string name)
    {
        var created = await app.PostAsync("/api/evals/suites", new JsonObject
        {
            ["name"] = name,
            ["k"] = 3,
            ["targets"] = new JsonArray(new JsonObject { ["provider"] = "faux", ["model"] = "echo" }, new JsonObject { ["provider"] = "faux", ["model"] = "echo-small" }),
        });
        Check(created.Status == 200 && created.Body?["created"]?.GetValue<bool>() == true, "a suite: " + created.Text);
        var suite = created.Body!["suite"]!["id"]!.GetValue<string>();
        var cases = new List<string>();
        foreach (var (title, prompt) in new[] { ("SemVer pre-release", "Is 1.0.0-rc.1 valid SemVer?"), ("Prime number", "Name a prime number below ten.") })
        {
            var added = await app.PostAsync("/api/evals/cases", new JsonObject
            {
                ["suiteId"] = suite,
                ["name"] = title,
                ["spec"] = new JsonObject
                {
                    ["provider"] = "faux", ["model"] = "echo", ["maxTokens"] = 256,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["text"] = prompt }),
                },
                ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["text"] = "Echo" }),
            });
            Check(added.Status == 200 && added.Body?["created"]?.GetValue<bool>() == true, "a case: " + added.Text);
            cases.Add(added.Body!["case"]!["id"]!.GetValue<string>());
        }
        var first = await app.W5SuiteRunAsync(suite);
        var baseline = await app.PostAsync($"/api/evals/suites/{suite}/baseline", new JsonObject { ["runId"] = first });
        Check(baseline.Status == 200 && baseline.Body?["accepted"]?.AsArray().Count == 2, "run #1 is the baseline: " + baseline.Text);
        var changed = await app.PostAsync("/api/evals/cases/" + cases[0], new JsonObject
        {
            ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["text"] = "a phrase no echo says" }),
        });
        Equal(200, changed.Status, "the first case's check changes: " + changed.Text);
        await app.W5SuiteRunAsync(suite);
        var last = await app.W5SuiteRunAsync(suite);
        var schedule = await app.PostAsync($"/api/evals/suites/{suite}/schedule", new JsonObject
        {
            ["enabled"] = true, ["repeat"] = "daily", ["at"] = "07:00", ["timeZone"] = "UTC", ["capMicros"] = 1_000_000,
        });
        Check(schedule.Status == 200 && schedule.Body?["saved"]?.GetValue<bool>() == true, "a daily schedule: " + schedule.Text);
        return new W5Suite(suite, cases[0], first, last);
    }

    /// <summary>Run now (<c>POST /api/evals/suites/{id}/run</c>), waiting until the run is complete; returns its id.</summary>
    internal static async Task<string> W5SuiteRunAsync(this Agent app, string suite)
    {
        var started = await app.PostAsync($"/api/evals/suites/{suite}/run", new JsonObject());
        Check(started.Status == 200 && started.Body?["posted"]?.GetValue<bool>() == true, "Run now: " + started.Text);
        var run = started.Body!["run"]!["id"]!.GetValue<string>();
        await WaitUntilAsync(async () =>
        {
            var view = (await app.Get("/api/evals/runs/" + run)).Body;
            return view?["run"]?["status"]?.GetValue<string>() == "complete" && view["running"]?.GetValue<bool>() == false;
        }, $"suite run {run} is complete", TimeSpan.FromSeconds(60));
        // W5 fix 2: no wait for its drift signals any more: they are stored before the run reads complete (W5 integration).
        return run;
    }

    /// <summary>Waits until the run page shows run <paramref name="number"/> ended as <paramref name="status"/> (the page polls every 3 s).</summary>
    internal static async Task W5RunPageEndedAsync(this IPage page, int number, string status)
    {
        var live = page.Locator("[data-eval-run-live]");
        await Expect(page.Locator("[data-eval-run-header]")).ToContainTextAsync("Run #" + number);
        await Expect(live).ToHaveAttributeAsync("data-running", "false", new() { Timeout = 60_000 });
        await Expect(live).ToHaveAttributeAsync("data-run-status", status);
    }
}
