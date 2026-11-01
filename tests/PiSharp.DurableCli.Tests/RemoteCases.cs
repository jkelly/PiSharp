// PiSharp-only (decision 0010, M6): `pisharp durable <group> <command>`, the device's v1 API as commands (src/PiSharp.Cli/Durable/
// DurableRemote.cs and DurableCommands.cs), against a device in Kestrel on loopback with the faux model, a fake older device, and a closed
// port. The manifest's cli bindings are checked against the command table both ways.
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Pi;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Server;

internal static partial class Program
{
    private const string RemotePolicy = """
        {"title":"Relevance","kind":"classifier","model":{"provider":"faux","id":"judge"},"instructions":"Is the release relevant?",
         "inputSchema":{"type":"object","additionalProperties":false,"required":["title"],"properties":{"title":{"type":"string","maxLength":200}}},
         "labels":[{"id":"relevant","description":"It matters"},{"id":"ignore","description":"It does not"}]}
        """;

    private static IEnumerable<(string, Func<Task>)> RemoteCases() =>
    [
        Case("remote.manifest-cli-bindings-match-the-command-table", () =>
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "contracts", "durable-capabilities.json")))!.AsObject();
            var commands = DurableRemote.Commands;
            Equal(commands.Count, commands.Select(command => command.Name).Distinct(StringComparer.Ordinal).Count(), "command names are unique");
            Equal("", string.Join("; ", CliBindingProblems(manifest, commands)), "the manifest and the command table agree");
            foreach (var command in commands)
            {
                Check(command.Shape is not null, "usage parses: " + command.Name);
                Check(DurableRemote.Help().Contains("pisharp durable " + command.Name, StringComparison.Ordinal), "help lists " + command.Name);
            }
            // A removed command or a removed manifest binding is caught.
            var withoutOne = commands.Where(command => command.Name != "policies publish").ToList();
            Check(CliBindingProblems(manifest, withoutOne).Any(problem => problem.Contains("policies publish", StringComparison.Ordinal)), "a removed command is caught");
            var trimmed = manifest.DeepClone().AsObject();
            var policies = trimmed["features"]!.AsArray().OfType<JsonObject>().First(feature => feature["id"]!.GetValue<string>() == "policies");
            policies["cli"] = new JsonArray([.. policies["cli"]!.AsArray().Where(name => name!.GetValue<string>() != "pisharp durable policies activate").Select(name => name!.DeepClone())]);
            Check(CliBindingProblems(trimmed, commands).Any(problem => problem.Contains("policies activate", StringComparison.Ordinal)), "a removed binding is caught");
        }),

        Case("remote.usage-shapes-parse-options-flags-choices-and-repeats", () =>
        {
            var shape = UsageShape.Parse("--session <s> <id> approve|reject [--reason <text>] [--example] <rest>...");
            Check(shape.Options["--session"] && !shape.Options["--reason"], "required and optional options");
            Check(shape.Flags.Contains("--example"), "a flag");
            Equal("approve,reject", string.Join(",", shape.Positional[1].Choices!), "choices");
            Check(shape.Positional[2].Repeats, "a repeated argument");
            var parsed = RemoteArgs.Parse(["--session", "s", "j1", "approve", "a", "b", "--example"], shape);
            Equal("j1|approve|a|b", string.Join("|", parsed.Positional), "positional arguments");
            Check(parsed.Flag("--example") && parsed.Option("--reason") is null, "flag set, option absent");
            Throws<RemoteUsageException>(() => RemoteArgs.Parse(["--session", "s", "j1", "maybe", "a"], shape), "a choice outside the list");
            Throws<RemoteUsageException>(() => RemoteArgs.Parse(["j1", "approve", "a"], shape), "a missing required option");
            Throws<RemoteUsageException>(() => RemoteArgs.Parse(["--session", "s", "j1", "approve", "a", "--surprise", "x"], shape), "an unknown option");
            Throws<RemoteUsageException>(() => RemoteArgs.Parse(["--session", "s", "--token", "x", "j1", "approve", "a"], shape), "a token argument");
            var cursor = DurableRemote.Cursor(new JsonObject { ["before"] = 41, ["seq"] = "x/y+z" });
            Check(cursor.StartsWith("c1.", StringComparison.Ordinal) && !cursor.Contains('/') && !cursor.Contains('+'), "an object cursor is opaque and URL safe");
            Equal(41, DurableRemote.ObjectCursor(cursor)!["before"]!.GetValue<int>(), "and decodes");
            Throws<RemoteUsageException>(() => DurableRemote.ObjectCursor("not-a-cursor"), "a foreign cursor");
        }),

        Case("remote.commands-drive-a-device-with-text-json-request-ids-and-exit-codes", async () =>
        {
            await using var device = await RemoteDevice.StartAsync("remote-commands");
            var home = Temp("remote-home");
            var env = new Dictionary<string, string> { ["PISHARP_DEVICE_TOKEN"] = device.Token };
            var url = device.Url.ToString();
            var spec = Path.Combine(home, "policy.json");
            File.WriteAllText(spec, RemotePolicy);

            var info = await CliAsync(home, env, "device", "--device", url);
            Equal(0, info.Exit, "device: " + info.Err);
            Check(info.Out.Contains("Features: ", StringComparison.Ordinal) && info.Out.Contains("policies v1", StringComparison.Ordinal), "device text: " + info.Out);
            var capabilities = await CliAsync(home, env, "capabilities", "--json", "--device", url);
            Check(JsonNode.Parse(capabilities.Out)!["features"]!.AsArray().Count >= 10, "capabilities as JSON");

            var published = await CliAsync(home, env, "policies", "publish", "relevance", "--file", spec, "--request", "p1", "--json", "--device", url);
            Equal(0, published.Exit, "publish: " + published.Err);
            Equal(1, JsonNode.Parse(published.Out)!["version"]!.GetValue<int>(), "version 1");
            var replay = await CliAsync(home, env, "policies", "publish", "relevance", "--file", spec, "--request", "p1", "--json", "--device", url);
            Equal(1, JsonNode.Parse(replay.Out)!["version"]!.GetValue<int>(), "the same request replays version 1");
            File.WriteAllText(spec, RemotePolicy.Replace("Is the release relevant?", "Does it matter?", StringComparison.Ordinal));
            var reused = await CliAsync(home, env, "policies", "publish", "relevance", "--file", spec, "--request", "p1", "--device", url);
            Equal(DurableExit.Conflict, reused.Exit, "the same request with another body: " + reused.Err);
            var stale = await CliAsync(home, env, "policies", "activate", "relevance", "1", "--revision", "999", "--device", url);
            Equal(DurableExit.Conflict, stale.Exit, "a stale revision");
            var listed = await CliAsync(home, env, "policies", "list", "--device", url);
            Check(listed.Out.StartsWith("ID", StringComparison.Ordinal) && listed.Out.Contains("relevance", StringComparison.Ordinal), "a text table: " + listed.Out);
            Equal(DurableExit.NotFound, (await CliAsync(home, env, "policies", "show", "missing", "--device", url)).Exit, "not found");

            var created = await CliAsync(home, env, "sessions", "create", "--name", "cli", "--cwd", device.Work, "--request", "s1", "--json", "--device", url);
            Equal(0, created.Exit, "session: " + created.Err);
            var sessionId = JsonNode.Parse(created.Out)!["id"]!.GetValue<string>();
            var again = await CliAsync(home, env, "sessions", "create", "--name", "cli", "--cwd", device.Work, "--request", "s1", "--json", "--device", url);
            Equal(sessionId, JsonNode.Parse(again.Out)!["id"]!.GetValue<string>(), "the same request finds the same session");
            var executions = await CliAsync(home, env, "executions", "list", "--session", sessionId, "--device", url);
            Equal(0, executions.Exit, "executions: " + executions.Err);

            var budgets = JsonNode.Parse((await CliAsync(home, env, "budgets", "show", "--json", "--device", url)).Out)!;
            var revision = budgets["limits"]!["revision"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
            Equal(0, (await CliAsync(home, env, "budgets", "set", "requests=100", "--revision", revision, "--device", url)).Exit, "a limit");
            var budgetText = await CliAsync(home, env, "budgets", "show", "--device", url);
            Check(budgetText.Out.Contains("requests: 0 used of 100", StringComparison.Ordinal), "budget text: " + budgetText.Out);
            Equal(DurableExit.Usage, (await CliAsync(home, env, "budgets", "set", "requests=lots", "--revision", revision, "--device", url)).Exit, "a bad limit");

            var issued = await CliAsync(home, env, "credentials", "issue", "--label", "ci", "--device", url);
            Check(issued.Out.Contains("Token (shown once", StringComparison.Ordinal), "issue shows the token once: " + issued.Out);
            var trigger = issued.Out.Split(": ")[^1].Trim();
            var credentials = await CliAsync(home, env, "credentials", "list", "--json", "--device", url);
            Check(!credentials.Out.Contains(trigger, StringComparison.Ordinal), "a list never shows a token");
            var scoped = await CliAsync(home, new Dictionary<string, string> { ["PISHARP_DEVICE_TOKEN"] = trigger }, "policies", "list", "--device", url);
            Equal(DurableExit.Unauthorized, scoped.Exit, "a trigger credential lacks the scope: " + scoped.Err);
            var wrong = await CliAsync(home, new Dictionary<string, string> { ["PISHARP_DEVICE_TOKEN"] = "wrong-token-wrong-token-wrong-token" }, "sessions", "list", "--device", url);
            Equal(DurableExit.Unauthorized, wrong.Exit, "a wrong token");

            Equal(DurableExit.Usage, (await CliAsync(home, env, "policies", "list", "--bogus", "x", "--device", url)).Exit, "an unknown option");
            var tokenArgument = await CliAsync(home, env, "policies", "list", "--token", device.Token, "--device", url);
            Equal(DurableExit.Usage, tokenArgument.Exit, "a token argument is refused");
            Check(!tokenArgument.Err.Contains(device.Token, StringComparison.Ordinal), "and not echoed");
            Equal(DurableExit.Usage, (await CliAsync(home, NoEnvironment, "sessions", "list", "--device", url)).Exit, "no token for --device");
            Equal(DurableExit.Usage, (await CliAsync(home, env, "sessions", "list", "--device", "http://user:secret@127.0.0.1:1/")).Exit, "no credential in a URL");
            Equal(DurableExit.Usage, (await CliAsync(home, env, "policies")).Exit, "a group without a command");
            Check((await CliAsync(home, env, "policies")).Err.Contains("pisharp durable policies publish", StringComparison.Ordinal), "lists the group's commands");

            // The local device: device.json's address and the token file, no environment.
            var data = Path.Combine(home, "device");
            new DeviceConfig { Host = "127.0.0.1", Port = device.Url.Port }.Save(data);
            File.WriteAllText(DeviceConfig.TokenPath(data), device.Token);
            var local = await CliAsync(home, NoEnvironment, "sessions", "list", "--data-dir", data);
            Equal(0, local.Exit, "the local device: " + local.Err);
            Check(local.Out.Contains("cli", StringComparison.Ordinal), "lists the session: " + local.Out);
            Equal(DurableExit.Usage, (await CliAsync(home, NoEnvironment, "sessions", "list", "--data-dir", Path.Combine(home, "none"))).Exit, "no local token");

            var feedback = await CliAsync(home, env, "preferences", "list", "--json", "--device", url);
            Equal(0, feedback.Exit, "preferences: " + feedback.Err);
        }),

        Case("remote.offline-writes-print-their-request-id-and-old-devices-are-unsupported", async () =>
        {
            var home = Temp("remote-offline");
            var env = new Dictionary<string, string> { ["PISHARP_DEVICE_TOKEN"] = DeviceTokens.Generate() };
            var source = Path.Combine(home, "source.json");
            File.WriteAllText(source, """{"id":"feed","kind":"feed","url":"https://example.com/feed.xml"}""");
            var closed = $"http://127.0.0.1:{ClosedPort()}/";
            var offline = await CliAsync(home, env, "sessions", "create", "--name", "x", "--cwd", home, "--device", closed);
            Equal(DurableExit.Offline, offline.Exit, "a device that does not answer: " + offline.Err);
            Check(offline.Err.Contains("Retry with --request cli-", StringComparison.Ordinal), "the write's request ID is printed: " + offline.Err);
            var withId = await CliAsync(home, env, "sessions", "create", "--name", "x", "--cwd", home, "--request", "mine", "--device", closed);
            Check(!withId.Err.Contains("Retry with", StringComparison.Ordinal), "a given request ID is the user's already");
            var json = await CliAsync(home, env, "sessions", "create", "--name", "x", "--cwd", home, "--json", "--device", closed);
            Equal("offline", JsonNode.Parse(json.Err)!["error"]!["code"]!.GetValue<string>(), "a JSON error on stderr");
            Check(JsonNode.Parse(json.Err)!["error"]!["requestId"]!.GetValue<string>().StartsWith("cli-", StringComparison.Ordinal), "with the request ID");

            await using var old = await OldDevice.StartAsync();
            var unsupported = await CliAsync(home, env, "sources", "create", "--file", source, "--device", old.Url);
            Equal(DurableExit.Unsupported, unsupported.Exit, "a device without sources: " + unsupported.Err);
            Check(unsupported.Err.Contains("does not offer sources", StringComparison.Ordinal), "an actionable message: " + unsupported.Err);
            Check(!old.Paths.Contains("/v1/sources"), "nothing was sent to the missing route");
            var sessions = await CliAsync(home, env, "sessions", "list", "--device", old.Url);
            Equal(0, sessions.Exit, "commands older than the manifest still work: " + sessions.Err);
        }),
    ];

    /// <summary>What disagrees between the manifest's cli bindings and the command table, both ways.</summary>
    private static List<string> CliBindingProblems(JsonObject manifest, IReadOnlyList<RemoteCommand> commands)
    {
        var problems = new List<string>();
        var names = commands.Select(command => "pisharp durable " + command.Name).ToHashSet(StringComparer.Ordinal);
        var features = manifest["features"]!.AsArray().OfType<JsonObject>().ToDictionary(feature => feature["id"]!.GetValue<string>(), StringComparer.Ordinal);
        foreach (var (id, feature) in features)
        {
            var cli = feature["cli"]!.AsArray().Select(name => name!.GetValue<string>()).ToList();
            foreach (var name in cli.Where(name => !names.Contains(name))) problems.Add($"{id}: {name} is not a command");
            if (cli.Count == 0 && feature["unsupported"]?["cli"] is null) problems.Add($"{id}: no cli binding and no reason");
        }
        foreach (var command in commands.Where(command => command.Feature is not null))
        {
            if (!features.TryGetValue(command.Feature!, out var feature)) problems.Add($"{command.Name}: no feature {command.Feature}");
            else if (!feature["cli"]!.AsArray().Any(name => name!.GetValue<string>() == "pisharp durable " + command.Name))
                problems.Add($"{command.Feature}: pisharp durable {command.Name} is not bound");
        }
        return problems;
    }

    private static readonly Dictionary<string, string> NoEnvironment = [];

    private static async Task<(int Exit, string Out, string Err)> CliAsync(string home, IReadOnlyDictionary<string, string> env, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var host = new PiHost
        {
            Cwd = home, Home = home, GetEnvironment = name => env.TryGetValue(name, out var value) ? value : null, Stdout = stdout, Stderr = stderr,
            LiveRuntime = new LiveSessionRuntime(_ => null, () => null),
        };
        var exit = await DurableMain.RunAsync(args, host, CancellationToken.None);
        return (exit, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    private static int ClosedPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static Uri Address(WebApplication app) =>
        new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");

    /// <summary>A device on loopback with the faux model.</summary>
    private sealed class RemoteDevice : IAsyncDisposable
    {
        private DurableDeviceHost host = null!;
        private WebApplication app = null!;

        public Uri Url { get; private set; } = null!;
        public string Token { get; } = DeviceTokens.Generate();
        public string Work { get; private set; } = "";

        public static async Task<RemoteDevice> StartAsync(string name)
        {
            var root = Temp(name);
            var device = new RemoteDevice { Work = Path.Combine(root, "work") };
            Directory.CreateDirectory(device.Work);
            device.host = await DurableDeviceHost.OpenAsync(new DurableDeviceOptions
            {
                DataDirectory = Path.Combine(root, "data"),
                StartDirectories = [device.Work],
                // A classifier, so the policy's faux/judge model exists.
                Models = new FauxDurableModels
                {
                    Classify = (_, _) => Task.FromResult(PiSharp.Durable.Chord.JsonValue.Object([new("label", PiSharp.Durable.Chord.JsonValue.Object(
                        [new("type", "choice"), new("choice", "ignore"), new("confidence", 0.9)]))])),
                },
                DefaultModel = FauxDurableModels.EchoRef,
            }, CancellationToken.None);
            device.app = DurableDeviceServer.Build(device.host, new DurableDeviceServerOptions { Token = device.Token }, "http://127.0.0.1:0");
            await device.app.StartAsync();
            device.Url = Address(device.app);
            return device;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            await host.DisposeAsync();
        }
    }

    /// <summary>A device from before the capability manifest: <c>/v1/device</c> lists no features, and every request's path is kept.</summary>
    private sealed class OldDevice : IAsyncDisposable
    {
        private WebApplication app = null!;

        public string Url { get; private set; } = "";
        public System.Collections.Concurrent.ConcurrentBag<string> Paths { get; } = [];

        public static async Task<OldDevice> StartAsync()
        {
            var old = new OldDevice();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            old.app = builder.Build();
            old.app.Use(async (context, next) =>
            {
                old.Paths.Add(context.Request.Path.Value ?? "");
                await next(context);
            });
            old.app.MapGet("/v1/device", () => Results.Json(new { uid = "old", name = "Old device", apiVersion = 1, models = Array.Empty<object>(), startDirs = Array.Empty<string>() }));
            old.app.MapGet("/v1/sessions", () => Results.Json(Array.Empty<object>()));
            await old.app.StartAsync();
            old.Url = Address(old.app).ToString();
            return old;
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
