using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Mcp;
using PiSharp.Cli.Output;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class McpProfileIntegrationTests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mcp-profile. ordinary create overloads preserve native catalog and join nonempty capture", Create),
        ("mcp-profile. durable resume admits fresh discovery before provider declarations", Resume),
        ("mcp-profile. profile replacement binds fresh generation and retires old resources", Replacement),
        ("mcp-profile. changed native registry or final policy joins rejected ownership", Mismatch),
        ("mcp-profile. ordinary RPC admits nonempty capture and EOF joins owned discovery", Rpc)
    ];
    private static readonly UTF8Encoding Utf8 = new(false);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }

    private static async Task Create()
    {
        foreach (var jsonLimitsOverload in new[] { false, true })
        {
            using var files = new Files(); var host = new Host(files.Root);
            var output = new StringWriter(); var error = new StringWriter();
            var args = files.Command("create");
            var code = jsonLimitsOverload
                ? await SessionCommands.RunAsync(args, output, error, new SessionJsonEventOutputOptions(), mcpAdmission: host.Acquire)
                : await SessionCommands.RunAsync(args, output, error, mcpAdmission: host.Acquire);
            Check(code == 0 && error.ToString() == "", "Ordinary create rejected supplied runtime admission.");
            Check(File.Exists(files.Session), "Create failed to acknowledge a durable session.");
            Check((await File.ReadAllLinesAsync(files.Session)).Select(line => JsonData.Parse(line)).Any(record =>
                record.Value.TryGetProperty("message", out var message) && message.TryGetProperty("toolsAdded", out var tools) &&
                tools.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == host.ToolName)),
                "Create exposed a captured direct tool without acknowledging its actual activation declaration.");
            Check(host.Acquisitions.Count == 1 && host.Acquisitions[0].Generation == 1, "Create did not reserve generation one.");
            host.AssertReleased(1);
            await AssertWithdrawalReport(files, host.ToolName, output.ToString());
        }
    }

    private static async Task Resume()
    {
        using var files = new Files(); var host = new Host(files.Root);
        Check(await SessionCommands.RunAsync(files.Command("create"), new StringWriter(), new StringWriter(), mcpAdmission: host.Acquire) == 0,
            "Initial command failed.");
        var original = await File.ReadAllBytesAsync(files.Session);
        await File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[] { TextTurn("resumed fixture") } }), Utf8);
        var output = new StringWriter(); var error = new StringWriter();
        Check(await SessionCommands.RunAsync(files.Command("resume", "--offline-script", files.Script, "--message", "resume fixture"),
            output, error, mcpAdmission: host.Acquire) == 0 && error.ToString() == "", "Ordinary resume failed.");
        using var document = JsonDocument.Parse(output.ToString());
        Check(document.RootElement.GetProperty("requests").EnumerateArray().Any(request =>
            request.GetProperty("toolNames").EnumerateArray().Any(name => name.GetString() == host.ToolName)),
            "Actual resumed provider request omitted discovered MCP declaration.");
        Check((await File.ReadAllBytesAsync(files.Session)).AsSpan().StartsWith(original), "Resume rewrote acknowledged history.");
        Check(host.Acquisitions.Select(item => item.Generation).SequenceEqual(new long[] { 1, 1 }),
            "Independent command resume reused an old owning attachment.");
        host.AssertReleased(2);
        await AssertWithdrawalReport(files, host.ToolName, output.ToString());
    }

    private static async Task AssertWithdrawalReport(Files files, string toolName, string output)
    {
        var log = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete &&
            log.ValidatedPrefixByteLength == log.OriginalBytes.Length, "Command cleanup left an incomplete durable log.");
        var entries = log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray();
        Check(entries.Any(entry => entry.WireBody.Value.TryGetProperty("message", out var message) &&
            message.TryGetProperty("toolsRemoved", out var removed) && removed.EnumerateArray().Any(tool =>
                tool.GetProperty("name").GetString() == toolName)), "Command disposed its writer before acknowledging MCP withdrawal.");
        var leaf = entries.Last().Id;
        var context = new SessionContextProjector().Project(entries, leaf);
        using var document = JsonDocument.Parse(output); var report = document.RootElement;
        Check(report.GetProperty("committedByteLength").GetInt64() == log.OriginalBytes.Length &&
            report.GetProperty("physicalLeafId").GetString() == leaf && report.GetProperty("selectedLeafId").GetString() == context.LeafId &&
            report.GetProperty("messageCount").GetInt32() == context.LlmMessages.Length,
            "Command report describes the pre-withdrawal checkpoint instead of its acknowledged closed file.");
    }

    private static async Task Replacement()
    {
        using var files = new Files(); var host = new Host(files.Root);
        await using var profile = await OfflineSessionProfile.CreateAsync(files.Root, files.Session, null, [], [], [],
            CancellationToken.None, mcpAdmission: host.Acquire);
        var backend = new SessionStorageBackend(files.Root, SessionStorageMode.InMemory);
        var sequence = 0;
        var lifecycle = profile.CreateLifecycle(() => 0, () => "entry-" + ++sequence, backend: backend);
        var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
            id = "profile", timestamp = "2026-10-05T00:00:00.000Z", cwd = files.Root }));
        await using var session = await lifecycle.CreateAsync(files.Session, header, profile.SelectedModel);
        await session.ConfigureAsync(new(SystemMessage: new("system", profile.InitialSystem)));
        await profile.AttachOwnerAsync(session, lifecycle: lifecycle);
        await profile.ApplyInitialToolSelectionAsync(session, CancellationToken.None);
        AssertBound(profile, host, 1);
        var owner = profile.Sessions ?? throw new InvalidOperationException("Profile did not attach an owning session.");
        await owner.CreateAsync(owner.Current, new(AgentSessionCreationKind.New));
        AssertBound(profile, host, 2);
        Check(host.Acquisitions.Select(item => item.Generation).SequenceEqual(new long[] { 1, 2 }),
            "Replacement acquired without reserving a fresh generation.");
        Check(host.Acquisitions[0].Channel.Closes == 1 && host.Acquisitions[0].DiscoveryCloses == 1 &&
            host.Acquisitions[0].NativeCloses == 1 && host.Acquisitions[1].NativeCloses == 0,
            "Replacement did not retire old ownership before exposing the new attachment.");
        await profile.DisposeAsync();
        host.AssertReleased(2);
        Check(backend.ActiveWriterCount == 0, "Replacement left an active session writer.");
    }

    private static void AssertBound(OfflineSessionProfile profile, Host host, long generation)
    {
        var actual = profile.Sessions!.Current.Session.CaptureToolCatalogRegistry();
        var admitted = host.Acquisitions.Single(item => item.Generation == generation);
        Check(actual.InvocationOwnerGeneration == generation && actual.UsesFinalActionPolicy(admitted.Policy),
            "Exposed attachment changed generation or final policy.");
        Check(actual.RegisteredTools.Any(tool => tool.Adapter.Name == host.ToolName), "Exposed attachment omitted actual MCP tool.");
        Check(profile.Sessions!.Current.Session.GetActiveTools().Contains(host.ToolName),
            "Exposed attachment did not activate its actual direct MCP declaration.");
        foreach (var native in admitted.Native.RegisteredTools)
            Check(actual.UsesCapturedToolBinding(native.Adapter.Name, native.Declaration, native.Adapter),
                "Profile activation replaced a native executable binding.");
    }

    private static async Task Mismatch()
    {
        foreach (var changePolicy in new[] { false, true })
        {
            using var files = new Files(); var entered = Gate(); var release = Gate(); var nativeEntered = false;
            var discoveryFault = new IOException("rejected discovery original");
            var nativeFault = new IOException("rejected native original");
            McpProfileRuntimeAdmission admission = (cwd, generation, registry, policy, token) =>
                ValueTask.FromResult(new McpSessionRuntimeAdmission(
                    changePolicy ? registry : registry.WithToolCatalog(registry.RegisteredTools, registry.PreparedToolHooks),
                    new Resource(() => { nativeEntered = true; throw nativeFault; }),
                    new Resource(async () => { entered.TrySetResult(); await release.Task; throw discoveryFault; }),
                    changePolicy ? new RejectPolicy() : policy, new([], []), [], false, (plan, current) => new(current, [])));
            await using var profile = await OfflineSessionProfile.CreateAsync(files.Root, files.Session, null, [], [], [],
                CancellationToken.None, mcpAdmission: admission);
            var backend = new SessionStorageBackend(files.Root, SessionStorageMode.InMemory);
            var lifecycle = profile.CreateLifecycle(() => 0, () => "entry", backend: backend);
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3,
                id = "rejected", timestamp = "2026-10-05T00:00:00.000Z", cwd = files.Root }));
            var work = lifecycle.CreateAsync(files.Session, header, profile.SelectedModel);
            Exception? assertion = null;
            try
            {
                Check(ReferenceEquals(await Task.WhenAny(entered.Task, work), entered.Task),
                    "Rejected acquisition returned before initiating admitted discovery cleanup.");
                Check(!work.IsCompleted && !nativeEntered && profile.Sessions is null,
                    "Rejected profile escaped before original discovery cleanup settled.");
            }
            catch (Exception failure) { assertion = failure; }
            finally { release.TrySetResult(); }
            Exception? failed = null;
            try { await work; } catch (Exception failure) { failed = failure; }
            if (assertion is not null) throw assertion;
            Check(failed is not null && nativeEntered && backend.ActiveWriterCount == 0, "Rejected admission retained ownership.");
            var leaves = Leaves(failed!).ToArray();
            Check(leaves.Length == 3 && leaves.Count(item => ReferenceEquals(item, discoveryFault)) == 1 &&
                leaves.Count(item => ReferenceEquals(item, nativeFault)) == 1 && leaves.Any(item => item is InvalidOperationException),
                "Profile mismatch replaced or duplicated original cleanup faults.");
        }
    }

    private static async Task Rpc()
    {
        using var files = new Files();
        Check(await SessionCommands.RunAsync(files.Command("create"), new StringWriter(), new StringWriter()) == 0,
            "RPC fixture create failed.");
        await File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[] { TextTurn("unused fixture") } }), Utf8);
        var host = new Host(files.Root);
        using var input = new GatedInput(Utf8.GetBytes("{\"id\":\"state\",\"type\":\"get_state\"}\n"));
        using var output = new ResponseOutput(); var error = new StringWriter();
        var work = RpcSessionCommand.RunAsync(files.Command("rpc", "--offline-script", files.Script), input, output, error,
            mcpAdmission: host.Acquire);
        Exception? assertion = null;
        try
        {
            Check(ReferenceEquals(await Task.WhenAny(output.StateWritten.Task, work), output.StateWritten.Task),
                "RPC completed before acknowledging startup state.");
            Check(!work.IsCompleted && host.Acquisitions.Single().NativeCloses == 0,
                "RPC retired native ownership before explicit EOF.");
        }
        catch (Exception failure) { assertion = failure; }
        finally { input.Eof.TrySetResult(); }
        var code = await work;
        if (assertion is not null) throw assertion;
        Check(code == 0 && error.ToString() == "", "Ordinary RPC startup or EOF shutdown failed.");
        var records = Utf8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonData.Parse(line)).ToArray();
        var response = records.Single(record => record.Value.TryGetProperty("id", out var id) && id.GetString() == "state").Value;
        Check(response.GetProperty("success").GetBoolean() && host.ToolName.Length != 0,
            "Ordinary RPC did not acknowledge startup with a nonempty admitted capture.");
        host.AssertReleased(1);
    }

    // Follow only the admitted factory wrapper, once through its evidence edge. Its
    // Original.Exception aliases that edge and must not be counted a second time.
    private static IEnumerable<Exception> Leaves(Exception failure) => failure switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Leaves),
        McpFactoryDisposalException { InnerException: { } evidence } => Leaves(evidence),
        _ => [failure]
    };
    private static object TextTurn(string text) => new { requiredInputTexts = new[] { "resume fixture" }, events = new object[]
    {
        new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "fixture-final", content = Array.Empty<object>() } },
        new { type = "response.output_text.delta", output_index = 0, item_id = "fixture-final", delta = text },
        new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "fixture-final", content = new[] { new { type = "output_text", text } } } },
        new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(),
            usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } }
    } };

    private sealed class Host(string cwd)
    {
        internal readonly List<Acquisition> Acquisitions = [];
        internal string ToolName = "";
        internal async ValueTask<McpSessionRuntimeAdmission> Acquire(string actualCwd, long generation,
            SessionRuntimeRegistry native, IToolActionPolicy policy, CancellationToken token)
        {
            Check(actualCwd == cwd && generation >= 1 && native.UsesFinalActionPolicy(policy), "Profile supplied a substituted native admission.");
            var extensions = new ExtensionRegistry();
            var scope = await extensions.ActivateAsync("profile-server", new EmptyExtension());
            var item = new Acquisition(generation, native, policy); Acquisitions.Add(item);
            var entry = new McpServerEntry("profile", McpConfigurationReader.Validate("profile",
                JsonData.Parse("{\"command\":\"synthetic-only\",\"exposure\":\"direct\"}").Value).Config!, "fixture", McpConfigurationScope.Extension);
            return new(native, new Resource(() => { item.NativeCloses++; return Task.CompletedTask; }),
                new Resource(async () => { item.DiscoveryCloses++; await extensions.DisposeAsync(); }), policy,
                new([entry], []), [new("profile", actual => Check(ReferenceEquals(actual, entry), "Server entry changed."),
                    async (actual, current, cancellation) =>
                    {
                        var capture = await McpPreOpenServerCapture.AcquireAsync(actual, extensions, scope, current, policy,
                            (name, arguments, validationToken) => ValueTask.FromResult(true),
                            (configured, acquireToken) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(item.Channel),
                            new(generation, "0.99.1"), (registry, binding) => binding.PreparedHooks ?? registry.PreparedToolHooks, cancellation);
                        ToolName = capture.Registry.RegisteredTools.Single(tool => !native.RegisteredTools.Any(original =>
                            original.Adapter.Name == tool.Adapter.Name)).Adapter.Name;
                        return capture;
                    })], false, (plan, current) =>
                    {
                        Check(current.UsesFinalActionPolicy(policy), "Discovery changed actual final policy.");
                        foreach (var original in native.RegisteredTools)
                            Check(current.UsesCapturedToolBinding(original.Adapter.Name, original.Declaration, original.Adapter), "Discovery replaced native binding.");
                        Check(current.RegisteredTools.Length == native.RegisteredTools.Length + 1, "Synthetic discovery was not nonempty.");
                        return new(current, []);
                    });
        }
        internal void AssertReleased(int count)
        {
            Check(Acquisitions.Count == count && Acquisitions.All(item => item.Channel.Closes == 1 &&
                item.Channel.Initializes == 1 && item.Channel.Lists == 1 && item.NativeCloses == 1 && item.DiscoveryCloses == 1),
                "Command did not join every actual discovery and original resource exactly once.");
        }
    }
    private sealed class Acquisition(long generation, SessionRuntimeRegistry native, IToolActionPolicy policy)
    {
        internal readonly long Generation = generation;
        internal readonly SessionRuntimeRegistry Native = native;
        internal readonly IToolActionPolicy Policy = policy;
        internal readonly Channel Channel = new();
        internal int NativeCloses, DiscoveryCloses;
    }
    private sealed class Channel : IMcpAdmittedRequestChannel
    {
        internal int Initializes, Lists, Closes;
        private Task? close;
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (method == "initialize")
            {
                Initializes++;
                return ValueTask.FromResult(JsonData.Parse("{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"synthetic\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}"));
            }
            if (method == "tools/list")
            {
                Lists++;
                return ValueTask.FromResult(JsonData.Parse("{\"tools\":[{\"name\":\"echo\",\"description\":\"synthetic echo\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}]}"));
            }
            throw new InvalidOperationException("This profile control has no admitted MCP execution or provider request.");
        }
        public Task CloseAsync() => close ??= CloseCore();
        private Task CloseCore() { Closes++; return Task.CompletedTask; }
    }
    private sealed class EmptyExtension : IPiSharpExtension
    {
        public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Resource(Func<Task> close) : IAsyncDisposable
    { public ValueTask DisposeAsync() => new(close()); }
    private sealed class RejectPolicy : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken token) =>
            ValueTask.FromResult(new ToolActionAuthorization(false));
    }
    private sealed class GatedInput(byte[] bytes) : Stream
    {
        private readonly MemoryStream source = new(bytes);
        internal readonly TaskCompletionSource Eof = Gate();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (source.Position < source.Length) return await source.ReadAsync(buffer, token);
            await Eof.Task.WaitAsync(token); return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) source.Dispose(); base.Dispose(disposing); }
    }
    private sealed class ResponseOutput : MemoryStream
    {
        internal readonly TaskCompletionSource StateWritten = Gate();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await base.WriteAsync(buffer, token);
            var text = Utf8.GetString(ToArray());
            var lines = text.Split('\n');
            foreach (var line in lines.Take(lines.Length - 1))
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == "state")
                    StateWritten.TrySetResult();
            }
        }
    }
    private sealed class Files : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "pisharp-mcp-profile-" + Guid.NewGuid().ToString("N"));
        internal string Session => Path.Combine(Root, "session.jsonl");
        internal string Script => Path.Combine(Root, "script.json");
        internal Files() => Directory.CreateDirectory(Root);
        internal string[] Command(string command, params string[] tail) => ["session", command, "--session", Session, "--workspace", Root, .. tail];
        public void Dispose() => Directory.Delete(Root, true);
    }
}
