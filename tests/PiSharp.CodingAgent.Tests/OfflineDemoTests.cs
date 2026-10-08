using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;

internal static class OfflineDemoTests
{
    private const string InputText = "Native offline input: \u6587\U0001f642\n";
    private const string OutputText = "Native offline tool write: \u6587\U0001f642\n";
    private static string? _dotnetHost; private static string? _cliDll;
    public static JsonElement? Evidence { get; private set; }
    public static void Configure(string dotnetHost, string cliDll)
    {
        if (!Path.IsPathFullyQualified(dotnetHost) || !Path.IsPathFullyQualified(cliDll) || !File.Exists(dotnetHost) || !File.Exists(cliDll))
            throw new ArgumentException("CLI child tests require existing explicit absolute host and CLI DLL paths.");
        _dotnetHost = Path.GetFullPath(dotnetHost); _cliDll = Path.GetFullPath(cliDll);
    }
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("offline CLI child sends real projected requests executes files durably reopens history", EndToEnd),
        ("offline CLI refuses existing workspace or session before writing", Freshness),
        ("offline CLI bounds explicit paths confines session ownership and rejects invalid arguments", Admission)
    ];

    private static async Task EndToEnd()
    {
        using var temp = new TemporaryFiles(); var workspace = temp.Child("work space \u6587"); var session = Path.Combine(workspace, "session.jsonl");
        var result = await Execute(temp.Root, "--offline-demo", "--workspace", workspace, "--session", session);
        Equal(0, result.ExitCode); Equal("", result.Error);
        using var report = JsonDocument.Parse(result.Output); var body = report.RootElement;
        Equal("completed", body.GetProperty("status").GetString()); Equal(1, body.GetProperty("schemaVersion").GetInt32());
        Check(!body.GetProperty("networkUsed").GetBoolean() && !body.GetProperty("upstreamDifferential").GetBoolean() && !body.GetProperty("phaseAcceptanceClaimed").GetBoolean(),
            "Offline report makes an unsupported external/acceptance claim.");
        Equal("explicit-inert-authored-demo-value", body.GetProperty("providerKeySource").GetString());
        Equal(InputText, await File.ReadAllTextAsync(Path.Combine(workspace, "input.txt")));
        var actualOutput = await File.ReadAllBytesAsync(Path.Combine(workspace, "answer.txt"));
        Check(actualOutput.SequenceEqual(Encoding.UTF8.GetBytes(OutputText)), "Actual native tool overwrite bytes differ.");
        Equal(OutputText, body.GetProperty("outputText").GetString());
        Equal("Read input.txt and wrote answer.txt through native tools.", body.GetProperty("firstFinalText").GetString());
        Equal("Reopened history includes the read and completed write.", body.GetProperty("resumedFinalText").GetString());
        var requests = body.GetProperty("requests").EnumerateArray().ToArray(); Equal(4, requests.Length);
        foreach (var request in requests)
        {
            Equal("POST", request.GetProperty("method").GetString()); Equal("https://offline.invalid/v1/responses", request.GetProperty("endpoint").GetString());
            Check(request.GetProperty("inertAuthorizationVerified").GetBoolean(), "Actual inert authorization was not checked.");
            var payload = request.GetProperty("payload"); Equal("offline-demo-text-tool", payload.GetProperty("model").GetString());
            Check(payload.GetProperty("stream").GetBoolean() && !payload.GetProperty("store").GetBoolean(), "Request factory stream/store fields differ.");
            var tools = payload.GetProperty("tools").EnumerateArray().ToArray();
            Check(tools.Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(new[] { "read", "write" }), "Native declarations did not reach actual requests.");
            Check(tools.All(tool => tool.GetProperty("type").GetString() == "function" && tool.GetProperty("parameters").GetProperty("type").GetString() == "object"),
                "Actual request tool schemas differ.");
        }
        var secondInput = requests[1].GetProperty("payload").GetProperty("input");
        Check(HasOutput(secondInput, "offline-read-1", InputText), "Actual read result is absent from follow-up input.");
        var fourthInput = requests[3].GetProperty("payload").GetProperty("input");
        Check(HasOutput(fourthInput, "offline-read-1", InputText) && HasOutput(fourthInput, "offline-write-1", "Successfully wrote to answer.txt"),
            "Reopened HTTP input lacks preserved tool results.");
        Check(fourthInput.EnumerateArray().Count(item => item.TryGetProperty("role", out var role) && role.GetString() == "user") == 2,
            "Reopened request does not include both persisted and new user input.");
        Equal(2, body.GetProperty("finalActions").GetArrayLength());
        foreach (var action in body.GetProperty("finalActions").EnumerateArray())
        {
            Equal(action.GetProperty("Target").GetString(), action.GetProperty("arguments").GetProperty("path").GetString());
            var root = body.GetProperty("canonicalWorkspace").GetString()!;
            Check(action.GetProperty("Target").GetString()!.StartsWith(root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "Policy-authorized final target is outside the owned demo root.");
        }
        var sessionEvidence = body.GetProperty("session"); Check(sessionEvidence.GetProperty("reopened").GetBoolean() && sessionEvidence.GetProperty("restoredHistoryVerified").GetBoolean(),
            "Actual session reopen was not verified.");
        Check(new FileInfo(session).Length < 1_048_576, "Actual session exceeds its declared demo cap.");
        var bytes = await File.ReadAllBytesAsync(session); Equal(bytes.Length, sessionEvidence.GetProperty("finalCommittedByteLength").GetInt32());
        Equal((long)bytes.Length, sessionEvidence.GetProperty("resumedAcknowledgedByteLength").GetInt64());
        Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), sessionEvidence.GetProperty("finalCommittedSha256").GetString());
        var firstLength = sessionEvidence.GetProperty("firstCommittedByteLength").GetInt32();
        Check(firstLength < bytes.Length && sessionEvidence.GetProperty("appendOnlyPrefixVerified").GetBoolean(), "Reopen did not append to the existing committed bytes.");
        Equal((long)firstLength, sessionEvidence.GetProperty("firstAcknowledgedByteLength").GetInt64());
        Equal(Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, firstLength))), sessionEvidence.GetProperty("firstCommittedSha256").GetString());
        var inspected = await new SessionLogReader(new(MaximumInputBytes: 1_048_576)).ReadFileAsync(session);
        Equal(SessionLogReadStatus.Complete, inspected.Status); Equal(3L, inspected.DetectedVersion!.Value);
        Equal(inspected.ValidatedPrefix.Length, sessionEvidence.GetProperty("validatedRecords").GetInt32());
        var entries = inspected.ValidatedPrefix.Where(record => record.Entry.Kind == SessionEntryKind.Message).Select(record => record.Entry.WireBody.Value.GetProperty("message")).ToArray();
        Check(entries.Select(message => message.GetProperty("role").GetString()).SequenceEqual(new[] { "system", "user", "assistant", "toolResult", "assistant", "toolResult", "assistant", "user", "assistant" }),
            "Durable session records do not reflect the actual native loop and reopened prompt.");
        var ids = inspected.ValidatedPrefix.Select(record => record.Entry.Id).ToArray(); Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        var text = Encoding.UTF8.GetString(bytes); Check(!text.Contains("pisharp-offline-demo-inert-key", StringComparison.Ordinal), "Authorization value was persisted as conversation data.");
        Evidence = body.Clone(); // Independently owned actual child report survives this document and temporary filesystem cleanup.
    }

    private static async Task Freshness()
    {
        using var temp = new TemporaryFiles(); var existing = temp.Child("existing"); Directory.CreateDirectory(existing);
        var sentinel = Path.Combine(existing, "sentinel"); await File.WriteAllTextAsync(sentinel, "owned sentinel");
        var result = await Execute(temp.Root, "--offline-demo", "--workspace", existing, "--session", Path.Combine(existing, "session.jsonl"));
        Refused(result, "ExistingWorkspace"); Equal("owned sentinel", await File.ReadAllTextAsync(sentinel));
        Check(Directory.GetFileSystemEntries(existing).Length == 1, "Existing workspace received demo writes.");
        var occupiedSession = temp.Child("occupied.jsonl"); await File.WriteAllTextAsync(occupiedSession, "existing authored bytes");
        var fresh = temp.Child("fresh");
        result = await Execute(temp.Root, "--offline-demo", "--workspace", fresh, "--session", occupiedSession);
        Refused(result, "ExistingSession"); Equal("existing authored bytes", await File.ReadAllTextAsync(occupiedSession));
        Check(!Directory.Exists(fresh), "Existing session rejection created a workspace.");
    }

    private static async Task Admission()
    {
        using var temp = new TemporaryFiles(); var workspace = temp.Child("new");
        Refused(await Execute(temp.Root, "--offline-demo", "--workspace", workspace, "--session", temp.Child("outside.jsonl")), "SessionOutsideWorkspace");
        Refused(await Execute(temp.Root, "--offline-demo", "--workspace", workspace, "--session", Path.Combine(workspace, "answer.txt")), "ReservedSessionName");
        Refused(await Execute(temp.Root, "--offline-demo", "--workspace", "relative", "--session", Path.Combine(workspace, "session.jsonl")), "InvalidPath");
        Refused(await Execute(temp.Root, "--offline-demo", "--workspace", temp.Child(new string('x', 2049)), "--session", Path.Combine(workspace, "session.jsonl")), "InvalidPath");
        if (OperatingSystem.IsWindows())
            Refused(await Execute(temp.Root, "--offline-demo", "--workspace", "\\\\offline.invalid\\share\\new", "--session", Path.Combine(workspace, "session.jsonl")), "InvalidPath");
        Refused(await Execute(temp.Root, "--offline-demo", "--workspace", workspace, "--unknown", Path.Combine(workspace, "session.jsonl")), "InvalidArguments");
        Check(Directory.GetFileSystemEntries(temp.Root).Length == 0, "Rejected demo arguments created filesystem effects.");
    }

    private sealed record ChildResult(int ExitCode, string Output, string Error);
    private static async Task<ChildResult> Execute(string workingDirectory, params string[] arguments)
    {
        if (_dotnetHost is null || _cliDll is null) throw new InvalidOperationException("Call OfflineDemoTests.Configure before running child tests.");
        var start = new ProcessStartInfo(_dotnetHost) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(_cliDll); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Check(process.Start(), "Could not start the actual compiled offline CLI.");
        var output = ReadBoundedAsync(process.StandardOutput, timeout.Token); var error = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Settle(output, error);
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer, token); if (count == 0) return result.ToString();
            if (count > 262_144 - result.Length) throw new InvalidOperationException("CLI child diagnostics exceed the authored bound.");
            result.Append(buffer, 0, count);
        }
    }
    private static bool HasOutput(JsonElement input, string call, string text) => input.EnumerateArray().Any(item =>
        item.TryGetProperty("type", out var kind) && kind.GetString() == "function_call_output" && item.GetProperty("call_id").GetString() == call && item.GetProperty("output").GetString() == text);
    private static void Refused(ChildResult result, string code)
    {
        Equal(2, result.ExitCode); Equal("", result.Output); using var error = JsonDocument.Parse(result.Error);
        Equal("failed", error.RootElement.GetProperty("status").GetString()); Equal(code, error.RootElement.GetProperty("code").GetString());
    }
    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public TemporaryFiles() { Root = Path.Combine(_parent, "pisharp-offline-cli-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public string Child(string name) => Path.Combine(Root, name);
        public void Dispose()
        {
            if (Path.GetDirectoryName(Root) != _parent || !Path.GetFileName(Root).StartsWith("pisharp-offline-cli-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the owned CLI test root.");
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
    private static async Task Settle(params Task[] tasks) { foreach (var task in tasks) { try { await task; } catch { } } }
}
