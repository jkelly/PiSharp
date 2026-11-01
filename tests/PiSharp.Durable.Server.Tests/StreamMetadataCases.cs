using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PiSharp.AI;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Client;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Server;

// Decision 0011 (WU1): DurableDeviceOptions.SessionStream behind the device API. A killed device recovers an in-flight request with the op
// and account it was prepared with; the next request gets a new op and the new binding; no provider request carries the metadata.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> StreamMetadataCases() =>
    [
        Case("stream-metadata.a-killed-device-recovers-the-request-with-its-op-and-account", async () =>
        {
            await using var child = new ChildServer(Temp("metadata-kill"), "--serve-metadata-child");
            await File.WriteAllTextAsync(Path.Join(child.Root, "binding"), "acct-1");
            await child.StartAsync();
            var id = (await child.Client!.SendAsync(HttpMethod.Post, "/v1/sessions", new JsonObject
            {
                ["name"] = "Metadata recovery",
                ["cwd"] = child.Work,
                ["model"] = new JsonObject { ["provider"] = "faux", ["modelId"] = "echo" },
            }))!["id"]!.GetValue<string>();
            var main = $"/v1/sessions/{id}/agents/main";
            await child.Client.SendAsync(HttpMethod.Post, main + "/messages", Message("hold: across a kill", "mk-1"));
            await WaitUntilAsync(() => Requests(child.Root).Count == 1, "the held request reached the model");
            var first = Requests(child.Root)[0];

            // Killed mid-request; the chat moves to another account while the device is down.
            await child.KillAsync();
            await File.WriteAllTextAsync(Path.Join(child.Root, "binding"), "acct-2");
            await File.WriteAllTextAsync(Path.Join(child.Root, "release"), "");
            await child.StartAsync();
            await WaitUntilAsync(() => Requests(child.Root).Count == 2, "the recovered request reached the model", TimeSpan.FromSeconds(60));
            var recovered = Requests(child.Root)[1];
            Equal("hold: across a kill", recovered["text"]!.GetValue<string>(), "the same request");
            Equal(first["pisharp"]!["op"]!.GetValue<string>(), recovered["pisharp"]!["op"]!.GetValue<string>(), "the recovered request keeps its op");
            Equal("acct-1", recovered["pisharp"]!["account"]!.GetValue<string>(), "and its account");
            var held = await child.Client!.SendAsync(HttpMethod.Post, main + "/messages", Message("hold: across a kill", "mk-1", waitMs: 30000));
            Equal("released", AnswerText(held), "answered after the restart");

            var next = await child.Client.SendAsync(HttpMethod.Post, main + "/messages", Message("after the kill", "mk-2", waitMs: 30000));
            Equal("Echo: after the kill", AnswerText(next), "a new request");
            var fresh = Requests(child.Root)[2];
            Check(fresh["pisharp"]!["op"]!.GetValue<string>() != first["pisharp"]!["op"]!.GetValue<string>(), "a new request gets a new op");
            Equal("acct-2", fresh["pisharp"]!["account"]!.GetValue<string>(), "and the new binding");
            var items = (await child.Client.GetAsync(main + "/transcript"))!["items"]!.AsArray();
            Equal(1, items.Count(item => item!.ToJsonString().Contains("\"released\"", StringComparison.Ordinal)), "exactly one answer to the held message");
        }),

        Case("stream-metadata.no-provider-request-carries-it", async () =>
        {
            var root = Temp("metadata-native");
            var (data, work) = (Path.Join(root, "data"), Path.Join(root, "work"));
            Directory.CreateDirectory(work);
            var handler = new ProviderRecorder();
            var routed = new System.Collections.Concurrent.ConcurrentQueue<DurableStreamOptions?>();
            using var models = new NativeDurableModels(new NativeDurableModelsOptions
            {
                Providers = ["openai"],
                HttpHandler = handler,
                ResolveApiKey = (_, _) => ValueTask.FromResult<string?>("test-provider-key"),
                HostTransport = (_, options, _) =>
                {
                    routed.Enqueue(options);
                    return ValueTask.FromResult<IChatTransport?>(null);
                },
            });
            await using (var host = await DurableDeviceHost.OpenAsync(DeviceOptions(data, work) with
            {
                Models = models,
                DefaultModel = new ModelRef("openai", "gpt-4.1"),
                SessionStream = _ => SessionStreamMetadata.Fields(("account", "acct-over-http"), ("tier", "default")),
            }))
            {
                var token = DeviceTokens.Generate();
                await using var app = DurableDeviceServer.Build(host, new DurableDeviceServerOptions { Token = token }, "http://127.0.0.1:0");
                await app.StartAsync();
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
                using var client = new DurableDeviceClient(new Uri(address), token);
                var id = (await client.SendAsync(HttpMethod.Post, "/v1/sessions", new JsonObject
                {
                    ["name"] = "Native",
                    ["cwd"] = work,
                    ["model"] = new JsonObject { ["provider"] = "openai", ["modelId"] = "gpt-4.1" },
                }))!["id"]!.GetValue<string>();
                var main = $"/v1/sessions/{id}/agents/main";
                Equal("Hello world", AnswerText(await client.SendAsync(HttpMethod.Post, main + "/messages", Message("hi", "nat-1", waitMs: 15000))), "answered");
                Equal("Hello world", AnswerText(await client.SendAsync(HttpMethod.Post, main + "/messages", Message("again", "nat-2", waitMs: 15000))), "again");
                await app.StopAsync();
            }
            try { Directory.Delete(root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A temporary directory. */ }

            Equal(2, routed.Count, "the host route saw each request");
            var ops = routed.Select(SessionStreamMetadata.Operation).ToList();
            Check(ops.All(op => op is not null) && ops.Distinct().Count() == 2, "each with its own op");
            Check(routed.All(options => SessionStreamMetadata.Field(options, "account") == "acct-over-http"), "and the account");
            Equal(2, handler.Requests.Count, "two provider requests");
            foreach (var request in handler.Requests)
            {
                var wire = request.Uri + "\n" + string.Join("\n", request.Headers.Select(pair => pair.Key + ": " + pair.Value)) + "\n" + request.Body;
                Check(!request.Body.Contains("\"pisharp\"", StringComparison.OrdinalIgnoreCase), "no pisharp key in the body");
                Check(!wire.Contains("acct-over-http", StringComparison.Ordinal), "no account");
                foreach (var op in ops) Check(!wire.Contains(op!, StringComparison.Ordinal), "no op");
            }
        }),
    ];

    /// <summary>The requests the metadata child's model saw: <c>{text, pisharp}</c> per line, in arrival order.</summary>
    private static List<JsonObject> Requests(string root)
    {
        var path = Path.Join(root, "requests.jsonl");
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        // A line still being written is left for the next read.
        return [.. text[..(text.LastIndexOf('\n') + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => (JsonObject)JsonNode.Parse(line)!)];
    }

    private static readonly Lock RequestLog = new();

    /// <summary>The child of the metadata kill test: a faux device whose stream hook binds sessions to the account in <c>root/binding</c>, whose
    /// model logs every request to <c>root/requests.jsonl</c> and holds <c>hold:</c> prompts until <c>root/release</c> exists (they answer
    /// <c>released</c>). It reads the device token from <c>root/token</c>, serves on a free port, prints <c>listening &lt;url&gt;</c>, and runs
    /// until killed.</summary>
    private static async Task<int> ServeMetadataChildAsync(string root)
    {
        var token = (await File.ReadAllTextAsync(Path.Join(root, "token"))).Trim();
        var models = new FauxDurableModels
        {
            RespondAsync = async (chat, cancellationToken) =>
            {
                var line = new JsonObject
                {
                    ["text"] = chat.Text,
                    ["pisharp"] = JsonNode.Parse(SessionStreamMetadata.Read(chat.Options)?.ToJson() ?? "null"),
                }.ToJsonString() + "\n";
                lock (RequestLog)
                {
                    using var log = new FileStream(Path.Join(root, "requests.jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    log.Write(Encoding.UTF8.GetBytes(line));
                }
                if (!chat.Text.StartsWith("hold:", StringComparison.Ordinal)) return null;
                while (!File.Exists(Path.Join(root, "release"))) await Task.Delay(25, cancellationToken);
                return "released";
            },
        };
        var binding = Path.Join(root, "binding");
        var options = DeviceOptions(Path.Join(root, "data"), Path.Join(root, "work"), "metadata-box", models) with
        {
            SessionStream = _ => SessionStreamMetadata.Fields(("account", File.ReadAllText(binding).Trim()), ("tier", "default")),
        };
        await using var host = await DurableDeviceHost.OpenAsync(options);
        await using var app = DurableDeviceServer.Build(host, new DurableDeviceServerOptions { Token = token }, "http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Console.WriteLine("listening " + address);
        await Console.Out.FlushAsync();
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}
