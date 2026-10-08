using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Storage;

/// <summary>Node-free regression for explicitly approved native packages with zero enabled extension tools.</summary>
internal static class NativeHookOnlyActivationTests
{
    private static string host = "", cli = "", published = "";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string cliDll)
    {
        Check(Path.IsPathFullyQualified(dotnetHost) && File.Exists(dotnetHost) && Path.IsPathFullyQualified(cliDll) && File.Exists(cliDll), "Explicit built host/CLI required.");
        host = dotnetHost; cli = cliDll;
        var directory = new DirectoryInfo(Path.GetDirectoryName(cli)!);
        while (!File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))) directory = directory.Parent ?? throw new IOException("Owned fixture repository missing.");
        published = Path.Combine(directory.FullName, "artifacts", "extensions", "published-fixtures", "cli");
        return [("native before-start actual CLI persists custom messages and preserves user history", BeforeStart),
            ("native request-context actual CLI preserves history and transforms provider input", RequestContext),
            ("native hook-only approval enables actual hooks with zero LLM tool grants and final policy", Hooks),
            ("native hook-only missing/mismatched/hash/scope approvals execute no compiled package or durable effects", Admission)];
    }
    private static async Task BeforeStart()
    {
        using var files = await Files.Create();
        Equal(0, (await Run(files, "create", [])).ExitCode);
        var prefix = await File.ReadAllBytesAsync(files.Session);
        await Script(files, Text("before-complete", "before-start:original", "before-start-custom"));
        var result = await Run(files, "prompt", ["--message", "before-start:original"]);
        Equal(0, result.ExitCode); Equal("", result.Error);
        var report = JsonData.Parse(result.Output).Value;
        Check(report.GetProperty("requests")[0].GetProperty("historyRequirementsSatisfied").GetBoolean(), "Before-start custom content missed provider request.");
        var log = await Complete(files); Check(log.OriginalBytes.AsSpan().StartsWith(prefix), "Before-start rewrote earlier bytes.");
        var raw = Encoding.UTF8.GetString(log.OriginalBytes.AsSpan());
        Check(!raw.Contains("before-start-forced", StringComparison.Ordinal), "Forced prompt leaked into history.");
        var entries = log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray();
        var custom = entries.Single(entry => entry.Type == "custom_message");
        Equal("fixture-before-start", custom.WireBody.Value.GetProperty("customType").GetString());
        Check(custom.WireBody.Value.GetProperty("details").GetProperty("retained").GetBoolean(), "Custom metadata lost.");
        Check(!custom.WireBody.Value.GetProperty("display").GetBoolean(), "Custom display flag changed.");
        Equal(1, files.MarkerLines.Count(line => line == "before-agent-start"));
        // A fresh CLI process reopens the same durable custom message and sends its content again.
        await Script(files, Text("reopened", "before-start-custom"));
        Equal(0, (await Run(files, "prompt", ["--message", "after-reopen"])).ExitCode);
        Equal(0, Directory.GetDirectories(files.Snapshots).Length);
    }

    private static async Task RequestContext()
    {
        using var files = await Files.Create();
        var created = await Run(files, "create", []); Equal(0, created.ExitCode);
        var before = await File.ReadAllBytesAsync(files.Session);
        await Script(files, Text("context-complete", "request-context|with-system"));
        var result = await Run(files, "prompt", ["--message", "context-request:canonical"]);
        Equal(0, result.ExitCode); Equal("", result.Error);
        var report = JsonData.Parse(result.Output).Value;
        Equal(1, report.GetProperty("usedScriptTurns").GetInt32());
        Check(report.GetProperty("requests")[0].GetProperty("historyRequirementsSatisfied").GetBoolean(), "Transformed input did not reach provider request.");
        var log = await Complete(files); Check(log.OriginalBytes.AsSpan().StartsWith(before), "Context rewrote existing history.");
        var raw = Encoding.UTF8.GetString(log.OriginalBytes.AsSpan());
        Check(raw.Contains("context-request:canonical", StringComparison.Ordinal), "Canonical input missing.");
        Check(!raw.Contains("request-context|with-system", StringComparison.Ordinal), "Request transformation leaked into history.");
        Check(files.MarkerLines.Where(line => line is "context" or "context-with-system").SequenceEqual(["context", "context-with-system"]), "Context phase order changed.");
        Equal(0, Directory.GetDirectories(files.Snapshots).Length);
    }

    private static async Task Hooks()
    {
        using var files = await Files.Create();
        var created = await Run(files, "create", []); Equal(0, created.ExitCode); Equal("", created.Error);
        var initial = await Complete(files); Loadout(initial);
        await Script(files, Tool("write", "blocked", new { path = files.Target, content = "extension-block" }, "begin|cli"),
            Tool("write", "denied", new { path = files.Denied, content = "no authority" }), Text("native-final"));
        var result = await Run(files, "prompt", ["--message", "transform:begin", "--allow-write", files.Target]);
        Equal(1, result.ExitCode); Equal("", result.Error);
        var report = JsonData.Parse(result.Output).Value;
        Check(report.GetProperty("durableCheckpointAcknowledged").GetBoolean(), "Response preceded durable checkpoint.");
        Equal(3, report.GetProperty("usedScriptTurns").GetInt32());
        foreach (var request in report.GetProperty("requests").EnumerateArray())
            Check(request.GetProperty("toolNames").EnumerateArray().Select(name => name.GetString()).SequenceEqual(["read", "write"]), "Disabled extension tool was advertised.");
        Equal(1, report.GetProperty("actions").GetArrayLength());
        Check(!report.GetProperty("actions")[0].GetProperty("allowed").GetBoolean(), "Empty tool grants bypassed native policy.");
        Check(!File.Exists(files.Target) && !File.Exists(files.Denied), "Actual hook or native policy allowed a rejected write.");
        var log = await Complete(files); Loadout(log);
        Check(log.OriginalBytes.AsSpan().StartsWith(initial.OriginalBytes.AsSpan()), "Hook activation rewrote prior durable bytes.");
        var context = new SessionContextProjector().ProjectLatest(log.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray());
        Equal("begin|cli", context.LlmMessages.Single(message => message.Role == "user").WireBody.Value.GetProperty("content")[0].GetProperty("text").GetString());
        Equal(2, context.LlmMessages.Count(message => message.Role == "toolResult"));
        Check(context.LlmMessages.Where(message => message.Role == "toolResult").All(message => message.WireBody.Value.GetProperty("isError").GetBoolean()), "Rejected tool result became success.");
        Check(files.MarkerLines.Count(line => line == "call") >= 2 && files.MarkerLines.Contains("input"), "Approved package's actual hooks did not execute.");
        Check(!files.MarkerLines.Any(line => line.StartsWith("tool:", StringComparison.Ordinal)), "Disabled registered extension tool executed.");
        foreach (var stage in new[] { "module", "constructor", "initialize", "dispose" }) Equal(2, files.MarkerLines.Count(line => line == stage));
        Equal("dispose", files.MarkerLines[^1]); Equal(0, Directory.GetDirectories(files.Snapshots).Length);
    }
    private static async Task Admission()
    {
        foreach (var failure in new[] { "approval-absent", "scope", "enabled-grant", "artifact-hash" })
        {
            using var files = await Files.Create();
            if (failure is "scope" or "enabled-grant")
            {
                var approval = JsonNode.Parse(await File.ReadAllTextAsync(files.Approval))!;
                if (failure == "scope") approval["effectiveScopeId"] = "different-scope";
                else approval["enabledTools"] = new JsonArray("fixture.cli.echo");
                await File.WriteAllTextAsync(files.Approval, approval.ToJsonString(), Utf8);
            }
            if (failure == "artifact-hash")
            { await using var artifact = new FileStream(Path.Combine(files.Package, "PublishedFixture.Cli.dll"), FileMode.Append, FileAccess.Write); artifact.WriteByte(0); }
            var result = await Run(files, "create", [], includeApproval: failure != "approval-absent");
            Equal(2, result.ExitCode); Equal("", result.Output);
            var code = JsonData.Parse(result.Error).Value.GetProperty("code").GetString();
            Equal(failure == "approval-absent" ? "ExecutionApprovalRequired" : failure == "artifact-hash" ? "InvalidConfiguration" : "ApprovalMismatch", code);
            Equal(0, files.MarkerLines.Length); Check(!File.Exists(files.Session) && !File.Exists(files.Target), "Rejected approval executed package or durable mutation.");
            Equal(0, Directory.GetDirectories(files.Snapshots).Length);
        }
    }
    private static void Loadout(SessionLogReadResult log)
    {
        var system = log.ValidatedPrefix.Skip(1).Select(record => record.Entry.WireBody.Value)
            .Single(entry => entry.TryGetProperty("message", out var message) && message.GetProperty("role").GetString() == "system");
        Check(system.GetProperty("message").GetProperty("toolsAdded").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(["read", "write"]), "Initial durable system gave a disabled extension tool grant.");
    }
    private static async Task<SessionLogReadResult> Complete(Files files)
    {
        var log = await new SessionLogReader().ReadFileAsync(files.Session);
        Check(log.SourceComplete && log.Status == SessionLogReadStatus.Complete && log.ValidatedPrefixByteLength == log.OriginalBytes.Length, "Native hook-only durable source incomplete.");
        await using var opened = await SessionLogStore.OpenAsync(files.Session); Equal((long)log.OriginalBytes.Length, opened.Snapshot.CommittedByteLength); return log;
    }
    private static Task Script(Files files, params object[] turns) => File.WriteAllTextAsync(files.Script, JsonSerializer.Serialize(new { schemaVersion = 1, turns }), Utf8);
    private static object Tool(string name, string call, object arguments, params string[] required) => new { requiredInputTexts = required, events = new object[]
    {
        new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = "" } },
        new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id = "fc-" + call, call_id = call, name, arguments = JsonSerializer.Serialize(arguments) } }, Finish()
    } };
    private static object Text(string text, params string[] required) => new { requiredInputTexts = required, events = new object[]
    {
        new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "msg", content = Array.Empty<object>() } },
        new { type = "response.output_text.delta", output_index = 0, item_id = "msg", delta = text },
        new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "msg", content = new[] { new { type = "output_text", text } } } }, Finish()
    } };
    private static object Finish() => new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 8, output_tokens = 4, total_tokens = 12 } } };
    private sealed record Result(int ExitCode, string Output, string Error);
    private static async Task<Result> Run(Files files, string command, string[] extra, bool includeApproval = true)
    {
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = files.Root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        start.Environment["PISHARP_NATIVE_FIXTURE_MARKERS"] = files.Markers; start.ArgumentList.Add(cli);
        foreach (var arg in new[] { "session", command, "--session", files.Session, "--workspace", files.Root }
            .Concat(command == "create" ? [] : new[] { "--offline-script", files.Script }).Concat(files.ExtensionArgs(includeApproval)).Concat(extra)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Compiled native hook-only CLI failed to start.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); process.StandardInput.Close();
        var output = Read(process.StandardOutput, deadline.Token); var error = Read(process.StandardError, deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); return new(process.ExitCode, await output, await error); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } foreach (var pending in new[] { output, error }) try { await pending; } catch (Exception) { } }
    }
    private static async Task<string> Read(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[8192];
        while (true) { var count = await reader.ReadAsync(buffer, token); if (count == 0) return result.ToString(); Check(count <= 2_097_152 - result.Length, "Native fixture I/O bound."); result.Append(buffer, 0, count); }
    }
    private sealed class Files : IDisposable
    {
        private readonly string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "pisharp-native-hook-only-" + Guid.NewGuid().ToString("N"));
        internal string Session => In("session.jsonl"); internal string Script => In("script.json"); internal string Package => In("published");
        internal string Manifest => In("manifest.json"); internal string Approval => In("approval.json"); internal string Snapshots => In("snapshots");
        internal string Markers => In("markers"); internal string Target => In("blocked.txt"); internal string Denied => In("denied.txt");
        private string In(string name) => Path.Combine(Root, name);
        internal string[] MarkerLines => File.Exists(Path.Combine(Markers, "PublishedFixture.Cli.markers")) ? File.ReadAllLines(Path.Combine(Markers, "PublishedFixture.Cli.markers")) : [];
        internal IEnumerable<string> ExtensionArgs(bool approval) => new[] { "--extension-package", Package, "--extension-manifest", Manifest, "--extension-snapshot-root", Snapshots }
            .Concat(approval ? new[] { "--extension-approval", Approval } : []);
        internal static async Task<Files> Create()
        {
            var files = new Files();
            try
            {
                Check(File.Exists(Path.Combine(published, "PublishedFixture.Cli.dll")), "Root must publish existing native PluginCli fixture.");
                foreach (var path in new[] { files.Root, files.Package, files.Snapshots, files.Markers }) Directory.CreateDirectory(path);
                foreach (var source in Directory.GetFiles(published, "*", SearchOption.AllDirectories))
                { var target = Path.Combine(files.Package, Path.GetRelativePath(published, source)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target); }
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in Directory.GetFiles(files.Package, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    hashes.Add(Path.GetRelativePath(files.Package, file).Replace('\\', '/'), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))));
                var metadata = JsonSerializer.Serialize(new { schemaVersion = 0, id = "fixture.cli", packageVersion = "0.0.1", hostApiRange = new { minimum = "0.0.0", maximumExclusive = "0.1.0" },
                    runtimeKind = "native", assembly = "PublishedFixture.Cli.dll", entryType = "PublishedCliFixture.Entry", tfm = "net10.0", rids = new[] { "win-x64" },
                    requiredFeatures = new[] { "owned-descriptor-callbacks", "registered-input-tool-reducers" }, declaredCapabilities = new[] { "tools", "observations" },
                    resourcePaths = hashes.Keys.Where(path => path != "PublishedFixture.Cli.dll").ToArray(), explicitOverrides = Array.Empty<object>(), artifactHashes = hashes });
                await File.WriteAllTextAsync(files.Manifest, metadata, Utf8);
                await File.WriteAllTextAsync(files.Approval, JsonSerializer.Serialize(new { schemaVersion = 1, execution = "ApprovePublishedFixtureExecution", packageRoot = files.Package,
                    manifestValueSha256 = Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(metadata))), artifactHashes = hashes, sourceScope = "Explicit", effectiveScopeId = "cli-native-explicit",
                    policyRevision = "experimental-policy-0", hostGeneration = 1, sessionPath = files.Session, workspace = files.Root, snapshotRoot = files.Snapshots, enabledTools = Array.Empty<string>() }), Utf8);
                return files;
            }
            catch { files.Dispose(); throw; }
        }
        public void Dispose()
        {
            var root = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(root) != parent || !Path.GetFileName(root).StartsWith("pisharp-native-hook-only-", StringComparison.Ordinal)) throw new IOException("Refusing unowned fixture cleanup.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), "Native hook-only values differ: expected=" + expected + " actual=" + actual);
}
