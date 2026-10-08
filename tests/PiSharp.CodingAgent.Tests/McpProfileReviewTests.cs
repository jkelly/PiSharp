using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Cli.Mcp;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Extensions;
using PiSharp.Extensions.Mcp.Configuration;
using PiSharp.Extensions.Mcp.Runtime;
using PiSharp.Extensions.Runtime;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class McpProfileReviewTests
{
    private const string Tool = "mcp__profile__echo";
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("mcp-profile-review. explicit and configured discovered create names activate durably", Create),
        ("mcp-profile-review. explicit discovered resume whitelist reaches actual provider request", Resume),
        ("mcp-profile-review. explicit discovered RPC whitelist reaches bound startup", Rpc),
        ("mcp-profile-review. unknown create resume RPC names are ignored and discovery rejection joins held acquired cleanup", Unknown),
        ("mcp-profile-review. summary and context-edit operation failures withdraw before writer close", ExceptionalCommands),
        ("mcp-profile-review. operation and distinct cleanup originals survive repeated owner joins", FaultIdentity)
    ];
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); }
    private static async Task Create()
    {
        foreach (var configured in new[] { false, true })
        {
            using var files = new Files(); var host = new Admission(); host.Release.TrySetResult();
            await File.WriteAllTextAsync(files.Settings, "{\"defaultTools\":[\"" + Tool + "\"]}");
            string[] flags = configured ? ["--user-settings", files.Settings] : ["--tools", Tool];
            var result = await Command(files, "create", host, flags);
            Check(result.Code == 0 && result.Error == "", "Valid discovered startup selection was rejected before acquisition.");
            Check(await Declared(files, "toolsAdded") && await Declared(files, "toolsRemoved"), "Selected discovered declaration was not activated and withdrawn durably.");
            host.AssertClosed();
        }
    }
    private static async Task Resume()
    {
        using var files = new Files(); await files.Seed();
        var host = new Admission(); host.Release.TrySetResult();
        var result = await Command(files, "resume", host, "--tools", Tool, "--offline-script", files.Script, "--message", "next");
        Check(result.Code == 0 && result.Error == "", "Explicit discovered resume failed.");
        using var report = JsonDocument.Parse(result.Output);
        var names = report.RootElement.GetProperty("requests")[0].GetProperty("toolNames").EnumerateArray().Select(name => name.GetString()).ToArray();
        Check(names.SequenceEqual(new[] { Tool }), "Explicit MCP whitelist changed or broadened actual provider declarations.");
        host.AssertClosed();
    }
    private static async Task Rpc()
    {
        using var files = new Files(); await files.Seed(); var host = new Admission(); host.Release.TrySetResult();
        var state = Gate(); JsonData? received = null;
        await using var connection = new BoundedRpcConnection((record, token) =>
        { if (record.Value.TryGetProperty("id", out var id) && id.GetString() == "state") { received = record; state.TrySetResult(); } return ValueTask.CompletedTask; });
        var error = new StringWriter();
        var original = RpcSessionCommand.RunAsync(files.Args("rpc", "--offline-script", files.Script, "--tools", Tool),
            connection.Input, connection.Output, error, mcpAdmission: host.Acquire);
        Exception? assertion = null;
        try
        {
            await connection.SendAsync(JsonData.Parse("{\"id\":\"state\",\"type\":\"get_state\"}"), CancellationToken.None);
            Check(ReferenceEquals(await Task.WhenAny(state.Task, original), state.Task) && received!.Value.GetProperty("success").GetBoolean(),
                "Explicit discovered RPC startup did not bind before state acknowledgment.");
            Check(await Declared(files, "toolsAdded"), "RPC startup did not acknowledge actual selected MCP tools.");
        }
        catch (Exception failure) { assertion = failure; }
        finally { connection.CompleteInput(); }
        var code = await original;
        if (assertion is not null) throw assertion;
        Check(code == 0 && error.ToString() == "" && await Declared(files, "toolsRemoved"), "RPC whitelist failed its owning cleanup."); host.AssertClosed();
    }
    private static async Task Unknown()
    {
        // Pi 1.1.0 sdk.ts/_applyToolLoadout ignore selected names the discovered catalog does not register: create, resume and RPC
        // startup succeed and declare no tool for the unknown name (an mcp__ entry filters the unnamed echo tool).
        foreach (var command in new[] { "create", "resume" })
        {
            using var files = new Files(); if (command != "create") await files.Seed();
            var host = new Admission(); host.Release.TrySetResult();
            var result = await Command(files, command, host, command == "create" ? ["--tools", "mcp__profile__missing"] :
                ["--tools", "mcp__profile__missing", "--offline-script", files.Script, "--message", "next"]);
            Check(result.Code == 0 && result.Error == "", $"Unknown {command} selection was rejected.");
            Check(!await Declared(files, "toolsAdded"), $"Unknown {command} selection declared an unnamed MCP tool.");
            if (command == "resume")
            {
                using var report = JsonDocument.Parse(result.Output);
                Check(report.RootElement.GetProperty("requests")[0].GetProperty("toolNames").GetArrayLength() == 0, "Unknown resume selection declared tools.");
            }
            host.AssertClosed();
        }
        {
            using var files = new Files(); await files.Seed(); var host = new Admission(); host.Release.TrySetResult();
            var state = Gate(); JsonData? received = null;
            await using var connection = new BoundedRpcConnection((record, token) =>
            { if (record.Value.TryGetProperty("id", out var id) && id.GetString() == "state") { received = record; state.TrySetResult(); } return ValueTask.CompletedTask; });
            var error = new StringWriter();
            var original = RpcSessionCommand.RunAsync(files.Args("rpc", "--offline-script", files.Script, "--tools", "mcp__profile__missing"),
                connection.Input, connection.Output, error, mcpAdmission: host.Acquire);
            Exception? assertion = null;
            try
            {
                await connection.SendAsync(JsonData.Parse("{\"id\":\"state\",\"type\":\"get_state\"}"), CancellationToken.None);
                Check(ReferenceEquals(await Task.WhenAny(state.Task, original), state.Task) && received!.Value.GetProperty("success").GetBoolean(),
                    "Unknown RPC selection did not bind before state acknowledgment.");
            }
            catch (Exception failure) { assertion = failure; }
            finally { connection.CompleteInput(); }
            var code = await original; if (assertion is not null) throw assertion;
            Check(code == 0 && error.ToString() == "", "Unknown RPC selection was rejected."); host.AssertClosed();
        }
        // A startup the discovered catalog rejects (an authored fixture rejection at the same discovery preparation point) still
        // joins the held acquired cleanup before it reports its failure.
        foreach (var command in new[] { "create", "resume", "rpc" })
        {
            using var files = new Files(); if (command != "create") await files.Seed();
            var host = new Admission { RejectDiscovery = true }; var output = new StringWriter(); var error = new StringWriter();
            using var input = new MemoryStream(); using var rpcOutput = new MemoryStream();
            string[] tail = command == "create" ? [] : command == "resume" ? ["--offline-script", files.Script, "--message", "next"] : ["--offline-script", files.Script];
            var original = command == "rpc" ? RpcSessionCommand.RunAsync(files.Args(command, tail), input, rpcOutput, error, mcpAdmission: host.Acquire) :
                SessionCommands.RunAsync(files.Args(command, tail), output, error, mcpAdmission: host.Acquire);
            Exception? assertion = null;
            try
            {
                Check(ReferenceEquals(await Task.WhenAny(host.Closing.Task, original), host.Closing.Task), "Rejected startup did not discover and join the actual admitted catalog.");
                Check(!original.IsCompleted && host.Initializes == 1 && host.Lists == 1 && host.NativeCloses == 0,
                    "Rejected startup escaped held capture cleanup or acquired a writer prematurely.");
                if (command == "create") Check(!File.Exists(files.Session), "Rejected actual catalog acquired a durable session writer.");
            }
            catch (Exception failure) { assertion = failure; }
            finally { host.Release.TrySetResult(); }
            var code = await original; if (assertion is not null) throw assertion;
            var publicCode = PublicFailureCode(error.ToString());
            Console.Error.WriteLine("DIAGNOSTIC " + JsonSerializer.Serialize(new
            { source = "mcp-profile-review.rejected-startup", variant = command, exitCode = code, publicCode }));
            host.AssertClosed();
            var diagnostic = $"variant={command}; exit={code}; publicCode={publicCode}";
            Check(code == 2, "Rejected actual catalog exit mismatch; " + diagnostic);
            Check(error.ToString().Contains("InvalidArguments", StringComparison.Ordinal), "Rejected actual catalog code mismatch; " + diagnostic);
            if (command == "rpc")
            {
                using var failure = JsonDocument.Parse(error.ToString());
                Check(failure.RootElement.GetProperty("cleanupFailureCount").GetInt32() == 0 && rpcOutput.Length == 0,
                    "Rejected RPC startup added cleanup failures or output frames.");
            }
        }
        var extensionHost = new Admission { RejectDiscovery = true };
        await NativeShutdownPlacementTests.RejectedStartup(extensionHost.Acquire, extensionHost.Closing.Task,
            () => extensionHost.Release.TrySetResult(), extensionHost.AssertClosed);
    }
    private static string PublicFailureCode(string error)
    {
        if (error.Length > 4096) return "oversized";
        try
        {
            using var document = JsonDocument.Parse(error);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String) return "missing";
            return code.GetString() switch
            {
                "InvalidArguments" => "InvalidArguments", "CommandFailed" => "CommandFailed",
                "CleanupFailed" => "CleanupFailed", "RpcHostFailed" => "RpcHostFailed", "Canceled" => "Canceled",
                _ => "other"
            };
        }
        catch (JsonException) { return "invalid-json"; }
    }
    private static async Task ExceptionalCommands()
    {
        foreach (var summary in new[] { false, true })
        {
            using var files = new Files(); await files.Seed(); await File.WriteAllTextAsync(files.Replacement, "null");
            string[] args = summary ? files.Args("branch-summary", "--offline-script", files.Script, "--target", "missing") :
                files.Args("context-edit", "--target", "missing", "--replacement", files.Replacement);
            async Task<(int Code, string Error)> Run(Admission? admitted)
            {
                var output = new StringWriter(); var error = new StringWriter();
                var code = summary ? await SessionSummaryCommand.RunAsync(args, output, error, mcpAdmission: admitted is null ? null : admitted.Acquire) :
                    await SessionContextEditCommand.RunAsync(args, output, error, mcpAdmission: admitted is null ? null : admitted.Acquire);
                Check(output.ToString() == "", "Failed operation delivered a success report."); return (code, error.ToString());
            }
            var baseline = await Run(null); var host = new Admission(); var original = Run(host); Exception? assertion = null;
            try
            {
                Check(ReferenceEquals(await Task.WhenAny(host.Closing.Task, original), host.Closing.Task) && !original.IsCompleted,
                    "Exceptional command did not join actual admitted physical close.");
            }
            catch (Exception failure) { assertion = failure; }
            finally { host.Release.TrySetResult(); }
            var failed = await original; if (assertion is not null) throw assertion;
            using var first = JsonDocument.Parse(baseline.Error); using var actual = JsonDocument.Parse(failed.Error);
            Check(failed.Code == 1 && first.RootElement.GetProperty("code").GetString() == actual.RootElement.GetProperty("code").GetString(),
                "Writer-first MCP cleanup replaced the original operation failure.");
            Check(await Declared(files, "toolsRemoved"), "Exceptional command closed its writer before reserved catalog withdrawal.");
            host.AssertClosed();
            await using var reopened = await SessionLogStore.OpenAsync(files.Session);
        }
    }
    private static async Task FaultIdentity()
    {
        using var files = new Files(); await files.Seed(); var host = new Admission();
        var primary = new IOException("original command failure"); var stop = new IOException("original channel stop failure");
        var discovery = new IOException("original discovery cleanup failure"); host.StopFault = stop; host.DiscoveryFault = discovery;
        var profile = await OfflineSessionProfile.CreateAsync(files.Root, files.Session, null, [], [], [], CancellationToken.None, mcpAdmission: host.Acquire);
        var lifecycle = profile.CreateLifecycle(() => 0, () => Guid.NewGuid().ToString("N"));
        var session = await lifecycle.OpenAsync(new(files.Session), profile.SelectedModel);
        await profile.AttachOwnerAsync(session, lifecycle: lifecycle);
        var original = profile.SettleOwnedCommandAsync(session, primary); Exception? assertion = null;
        try
        { Check(ReferenceEquals(await Task.WhenAny(host.Closing.Task, original), host.Closing.Task) && !original.IsCompleted, "Command cleanup bypassed the actual stop original."); }
        catch (Exception failure) { assertion = failure; }
        finally { host.Release.TrySetResult(); }
        Exception? observed = null; try { await original; } catch (Exception failure) { observed = failure; }
        if (assertion is not null) throw assertion;
        Exception[] leaves = observed is null ? [] : Leaves(observed).ToArray();
        Check(original.IsFaulted && !original.IsCanceled && observed is not null &&
            KnownEvidence(observed).OfType<PersistentAgentSessionException>().Any(error =>
                error.Fault.Failure == PersistentAgentSessionFailure.CleanupFailed && error.InnerException is AggregateException),
            "Faulting session cleanup lost its typed cleanup provenance.");
        var factoryFailure = KnownEvidence(observed!).OfType<McpFactoryDisposalException>().Single();
        Check(factoryFailure.Owner == "discovery" && factoryFailure.Original is { IsFaulted: true, IsCanceled: false } &&
            factoryFailure.InnerException is AggregateException { InnerExceptions.Count: 1 } retained &&
            ReferenceEquals(retained.InnerExceptions[0], discovery) &&
            factoryFailure.Original.Exception is { InnerExceptions.Count: 1 } actual &&
            ReferenceEquals(actual.InnerExceptions[0], discovery),
            "Factory cleanup lost its exact discovery task and original fault inventory.");
        Check(leaves.Length == 3 && new[] { primary, stop, discovery }.All(expected => leaves.Count(actual => ReferenceEquals(expected, actual)) == 1),
            "Primary and cleanup originals were replaced, duplicated, or lost across repeated owner/profile joins.");
        Check(await Declared(files, "toolsRemoved"), "Faulting stop prevented acknowledged reserved withdrawal."); host.AssertClosed();
    }
    private static IEnumerable<Exception> Children(Exception error) => error switch
    {
        AggregateException aggregate => aggregate.InnerExceptions,
        PersistentAgentSessionException { Fault.Failure: PersistentAgentSessionFailure.CleanupFailed, InnerException: AggregateException inner } => [inner],
        McpFactoryDisposalException { Owner: "discovery", Original.IsFaulted: true, InnerException: AggregateException inner } => [inner],
        _ => []
    };
    private static IEnumerable<Exception> KnownEvidence(Exception error)
    {
        yield return error;
        foreach (var child in Children(error)) foreach (var item in KnownEvidence(child)) yield return item;
    }
    private static IEnumerable<Exception> Leaves(Exception error)
    {
        var children = Children(error).ToArray();
        if (children.Length == 0) yield return error;
        else foreach (var child in children) foreach (var leaf in Leaves(child)) yield return leaf;
    }
    private static async Task<bool> Declared(Files files, string property)
    {
        // RPC startup keeps its acknowledged log writer open during this read.
        await using var stream = new FileStream(files.Session, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line) lines.Add(line);
        return lines.Select(JsonData.Parse).Any(record => record.Value.TryGetProperty("message", out var message) &&
            message.TryGetProperty(property, out var tools) && tools.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == Tool));
    }
    private static async Task<(int Code, string Output, string Error)> Command(Files files, string command, Admission? host, params string[] tail)
    { var output = new StringWriter(); var error = new StringWriter(); var code = await SessionCommands.RunAsync(files.Args(command, tail), output, error, mcpAdmission: host is null ? null : host.Acquire); return (code, output.ToString(), error.ToString()); }
    private sealed class Admission : IMcpAdmittedRequestChannel
    {
        internal readonly TaskCompletionSource Closing = Gate(), Release = Gate();
        internal int Initializes, Lists, Closes, NativeCloses, DiscoveryCloses;
        internal Exception? StopFault, DiscoveryFault; private Task? close;
        /// <summary>Reject the discovered catalog with InvalidArguments, as a startup-time catalog validation would.</summary>
        internal bool RejectDiscovery;
        internal async ValueTask<McpSessionRuntimeAdmission> Acquire(string cwd, long generation, SessionRuntimeRegistry native, IToolActionPolicy policy, CancellationToken token)
        {
            var extensions = new ExtensionRegistry(); var scope = await extensions.ActivateAsync("review-profile", new EmptyExtension());
            var entry = new McpServerEntry("profile", McpConfigurationReader.Validate("profile", JsonData.Parse("{\"command\":\"inert\",\"exposure\":\"direct\"}").Value).Config!, "fixture", McpConfigurationScope.Extension);
            return new(native, new Resource(() => { NativeCloses++; return Task.CompletedTask; }), new Resource(async () =>
                { DiscoveryCloses++; await extensions.DisposeAsync(); if (DiscoveryFault is not null) throw DiscoveryFault; }), policy, new([entry], []),
                [new("profile", actual => Check(ReferenceEquals(actual, entry), "Entry changed."), (actual, current, cancellation) =>
                    McpPreOpenServerCapture.AcquireAsync(actual, extensions, scope, current, policy, (name, arguments, validation) => ValueTask.FromResult(true),
                        (configured, acquireToken) => ValueTask.FromResult<IMcpAdmittedRequestChannel>(this), new(generation, "0.99.1"),
                        (registry, binding) => binding.PreparedHooks ?? registry.PreparedToolHooks, cancellation))], false, (plan, current) =>
                {
                    if (RejectDiscovery) throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
                    return new(current, []);
                });
        }
        public ValueTask StartAsync(CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask NotifyAsync(string method, JsonData? parameters, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask<JsonData> RequestAsync(string method, JsonData? parameters, McpRequestOptions options, CancellationToken token)
        {
            if (method == "initialize") { Initializes++; return ValueTask.FromResult(JsonData.Parse("{\"protocolVersion\":\"2025-11-25\",\"serverInfo\":{\"name\":\"fixture\",\"version\":\"1\"},\"capabilities\":{\"tools\":{}}}")); }
            if (method == "tools/list") { Lists++; return ValueTask.FromResult(JsonData.Parse("{\"tools\":[{\"name\":\"echo\",\"inputSchema\":{\"type\":\"object\",\"properties\":{}}}]}")); }
            throw new InvalidOperationException("No MCP execution admitted.");
        }
        public Task CloseAsync() => close ??= CloseCore();
        private async Task CloseCore() { Closes++; Closing.TrySetResult(); await Release.Task; if (StopFault is not null) throw StopFault; }
        internal void AssertClosed() => Check(Initializes == 1 && Lists == 1 && Closes == 1 && NativeCloses == 1 && DiscoveryCloses == 1, "Acquired cleanup was skipped or replayed.");
    }
    private sealed class Resource(Func<Task> close) : IAsyncDisposable { public ValueTask DisposeAsync() => new(close()); }
    private sealed class EmptyExtension : IPiSharpExtension
    { public ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token) => ValueTask.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    private sealed class Files : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "mcp-profile-reviewed-" + Guid.NewGuid().ToString("N"));
        internal string Session => Path.Combine(Root, "session.jsonl"); internal string Script => Path.Combine(Root, "script.json");
        internal string Settings => Path.Combine(Root, "settings.json"); internal string Replacement => Path.Combine(Root, "replacement.json");
        internal Files() => Directory.CreateDirectory(Root);
        internal string[] Args(string command, params string[] tail) => ["session", command, "--session", Session, "--workspace", Root, .. tail];
        internal async Task Seed()
        {
            var result = await Command(this, "create", null); Check(result.Code == 0, "Seed failed.");
            await File.WriteAllTextAsync(Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[] { new { events = new object[] {
                new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "final", content = Array.Empty<object>() } },
                new { type = "response.output_text.delta", output_index = 0, item_id = "final", delta = "final" },
                new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "final", content = new[] { new { type = "output_text", text = "final" } } } },
                new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 } } }
            } } } }));
        }
        public void Dispose()
        {
            var actual = Path.GetFullPath(Root);
            Check(actual.StartsWith(Path.GetFullPath(Path.GetTempPath()), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                Path.GetFileName(actual).StartsWith("mcp-profile-reviewed-", StringComparison.Ordinal), "Invalid owned fixture root.");
            Directory.Delete(actual, true);
        }
    }
}
