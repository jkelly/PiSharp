using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Mcp.Transport;
using PiSharp.Extensions.Runtime;

internal static class McpRegisteredProfileCommandTests
{
    private sealed class CommandOriginalFailure(Task<int> original, Exception observed)
        : IOException("Registered MCP command original failed.", original.Exception ?? observed)
    {
        internal Task<int> Original { get; } = original;
        internal Exception Observed { get; } = observed;
    }
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
        [("mcp-registered-profile.ordinary-create-real-channel-durable-declaration-and-close", Create)];
    private static void Require(bool value) { if (!value) throw new IOException("Registered MCP profile command assertion failed."); }
    private sealed class Extension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Resource(Func<Task> close) : IAsyncDisposable
    { public ValueTask DisposeAsync() => new(close()); }
    private sealed class Wire : IMcpWireTransport
    {
        private McpWireCallbacks? callbacks;
        private Task? close;
        internal int Starts, Initializes, Lists, Closes;
        internal bool RootsAdvertised;
        public Task StartAsync(McpWireCallbacks value, CancellationToken token)
        { token.ThrowIfCancellationRequested(); callbacks = value; Starts++; return Task.CompletedTask; }
        public Task SetProtocolVersionAsync(string version, CancellationToken token) => Task.CompletedTask;
        public async Task SendAsync(JsonData frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var message = frame.Value;
            if (!message.TryGetProperty("method", out var method) || !message.TryGetProperty("id", out var id)) return;
            string result;
            if (method.GetString() == "initialize")
            {
                Initializes++;
                RootsAdvertised = message.GetProperty("params").GetProperty("capabilities").TryGetProperty("roots", out _);
                result = "{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"injected-wire\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}";
            }
            else if (method.GetString() == "tools/list")
            {
                Lists++;
                result = "{\"tools\":[{\"name\":\"echo\",\"description\":\"injected echo\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}]}";
            }
            else throw new IOException("Unexpected admitted wire request.");
            var delivery = callbacks!.Receive(JsonData.Parse("{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() + ",\"result\":" + result + "}")).AsTask();
            await delivery;
        }
        public Task CloseAsync() => close ??= Close();
        private Task Close() { Closes++; return Task.CompletedTask; }
    }
    private static async Task Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-registered-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var session = Path.Combine(root, "session.jsonl");
        var extensions = new ExtensionRegistry();
        var scope = await extensions.ActivateAsync("registered", new Extension());
        var bridge = new McpExtensionRegistrationBridge(extensions, McpConfigurationReader.Load(null, null, true));
        bridge.BindOwner(scope, root).RegisterMcpServer("registered", JsonData.Parse("{\"command\":\"inert\",\"exposure\":\"direct\"}"));
        var wire = new Wire(); var acquisitions = 0; var nativeCloses = 0; var discoveryCloses = 0; var toolName = "";
        var admission = new McpRegisteredProfileAdmission(bridge, (cwd, generation, native, policy, catalog, token) =>
        {
            Require(cwd == root && generation == 1 && native.UsesFinalActionPolicy(policy)); acquisitions++;
            var entry = catalog.Servers.Single();
            var channel = new McpRegisteredServerWireAdmission((actual, cancellation) => ValueTask.FromResult<IMcpWireTransport>(wire),
                _ => ValueTask.FromResult(JsonData.Parse("[]")));
            return ValueTask.FromResult(new McpSessionRuntimeAdmission(native,
                new Resource(() => { nativeCloses++; return Task.CompletedTask; }),
                new Resource(async () => { discoveryCloses++; var disposal = scope.DisposeAsync().AsTask(); await disposal;
                    await bridge.RetireClosedOwnerAsync(scope, disposal); }), policy, catalog,
                [new(entry.Name, actual => Require(ReferenceEquals(actual, entry)), async (actual, current, cancellation) =>
                {
                    var capture = await McpPreOpenServerCapture.AcquireAsync(actual, extensions, scope, current, policy,
                        (_, _, _) => ValueTask.FromResult(true), channel.CreateChannelFactory(entry), new(generation, "0.99.1"),
                        (registry, binding) => binding.PreparedHooks ?? registry.PreparedToolHooks, cancellation);
                    toolName = capture.Registry.RegisteredTools.Single(tool => !native.RegisteredTools.Any(old => old.Adapter.Name == tool.Adapter.Name)).Adapter.Name;
                    return capture;
                })], false, (_, current) => new(current, [])));
        });
        Exception? primary = null; Task<int>? command = null;
        try
        {
            var output = new StringWriter(); var error = new StringWriter();
            command = SessionCommands.RunAsync(["session", "create", "--session", session, "--workspace", root], output, error,
                mcpAdmission: admission.CreateProfileAdmission());
            Require(await command == 0 && error.ToString() == "");
            var lines = await File.ReadAllLinesAsync(session);
            Require(lines.Select(line => JsonData.Parse(line)).Any(record => record.Value.TryGetProperty("message", out var message) &&
                message.TryGetProperty("toolsAdded", out var tools) && tools.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == toolName)));
            Require(acquisitions == 1 && nativeCloses == 1 && discoveryCloses == 1 && wire.Starts == 1 &&
                wire.Initializes == 1 && wire.Lists == 1 && wire.Closes == 1 && wire.RootsAdvertised && bridge.CaptureSnapshot().RegisteredServers.IsEmpty);
        }
        catch (Exception failure) { primary = command?.IsFaulted == true ? new CommandOriginalFailure(command, failure) : failure; }
        finally
        {
            Task? cleanup = null;
            try { cleanup = extensions.DisposeAsync().AsTask(); await cleanup; }
            catch (Exception failure) { var evidence = cleanup?.Exception ?? failure; primary = primary is null ? evidence : new AggregateException(primary, evidence); }
            try { Directory.Delete(root, true); }
            catch (Exception failure) { primary = primary is null ? failure : new AggregateException(primary, failure); }
        }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
