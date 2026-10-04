using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.AI.Protocols.OpenAIResponses;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Tools.Files;

namespace PiSharp.Cli;

public enum OfflineDemoFailure { InvalidPath, ExistingWorkspace, ExistingSession, SessionOutsideWorkspace, ReservedSessionName, MissingWorkspaceParent }
public sealed class OfflineDemoException : Exception
{
    public OfflineDemoFailure Failure { get; }
    internal OfflineDemoException(OfflineDemoFailure failure) : base(failure switch
    {
        OfflineDemoFailure.ExistingWorkspace => "Offline demo requires a new workspace directory.",
        OfflineDemoFailure.ExistingSession => "Offline demo requires a new session path.",
        OfflineDemoFailure.SessionOutsideWorkspace => "Offline demo session must be a direct child of the new workspace.",
        OfflineDemoFailure.ReservedSessionName => "Offline demo session name conflicts with its input or output file.",
        OfflineDemoFailure.MissingWorkspaceParent => "Offline demo workspace parent must already exist.",
        _ => "Offline demo paths must be bounded, valid absolute filesystem paths."
    }) => Failure = failure;
}
public sealed record OfflineDemoResult(JsonData Report);

/// <summary>A fresh-directory offline exercise of the native HTTP, agent, tools and durable session stack.</summary>
public static class OfflineDemo
{
    public const string InputText = "Native offline input: \u6587\U0001f642\n";
    public const string OutputText = "Native offline tool write: \u6587\U0001f642\n";
    public const string InitialPrompt = "Read input.txt and write the demo result to answer.txt.";
    public const string ResumePrompt = "Summarize the work preserved in this reopened session.";
    public const string FirstFinalText = "Read input.txt and wrote answer.txt through native tools.";
    public const string ResumedFinalText = "Reopened history includes the read and completed write.";
    private const string InertKey = "pisharp-offline-demo-inert-key";
    private static readonly Uri Endpoint = new("https://offline.invalid/v1/responses");
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static async Task<OfflineDemoResult> RunAsync(string workspace, string sessionPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        workspace = ValidateAbsolute(workspace); sessionPath = ValidateAbsolute(sessionPath);
        // All freshness checks precede the first directory/file creation. This is a trusted demo profile,
        // not an atomic hostile-filesystem race defense.
        if (Exists(workspace)) throw new OfflineDemoException(OfflineDemoFailure.ExistingWorkspace);
        if (Exists(sessionPath)) throw new OfflineDemoException(OfflineDemoFailure.ExistingSession);
        if (!string.Equals(Path.GetDirectoryName(sessionPath), workspace, PathComparison))
            throw new OfflineDemoException(OfflineDemoFailure.SessionOutsideWorkspace);
        if (string.Equals(Path.GetFileName(sessionPath), "input.txt", PathComparison) || string.Equals(Path.GetFileName(sessionPath), "answer.txt", PathComparison))
            throw new OfflineDemoException(OfflineDemoFailure.ReservedSessionName);
        var parent = Path.GetDirectoryName(workspace);
        if (parent is null || !Directory.Exists(parent)) throw new OfflineDemoException(OfflineDemoFailure.MissingWorkspaceParent);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(workspace);
        var local = new LocalFileOperations();
        var canonicalRoot = await local.CanonicalizeAsync(workspace, cancellationToken).ConfigureAwait(false);
        var inputPath = Path.Combine(canonicalRoot, "input.txt"); var outputPath = Path.Combine(canonicalRoot, "answer.txt");
        await using (var seed = new FileStream(inputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous))
        {
            await seed.WriteAsync(Utf8.GetBytes(InputText), cancellationToken).ConfigureAwait(false);
            await seed.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        var policy = new DemoPolicy(canonicalRoot, inputPath, outputPath);
        var files = new ReadWriteTools(canonicalRoot, canonicalRoot, local,
            new(MaximumReadBytes: 65_536, MaximumWriteBytes: 65_536, MaximumArgumentCharacters: 65_536));
        var invoker = files.CreateInvoker(policy);
        var model = new ModelDescriptor("offline-demo-text-tool", "openai-responses", "openai");
        using var handler = new DemoHandler(outputPath);
        using var client = new HttpClient(handler);
        var factory = new ResponsesKeyAuthRequestFactory(Endpoint, model, new(Reasoning: false, MaximumMessages: 64),
            new(MaximumPayloadBytes: 65_536));
        var transport = new ResponsesHttpSseTransport(client, request => factory.Create(request, InertKey),
            new(MaximumDataEvents: 32, MaximumTotalDataCharacters: 65_536));
        var configuration = new AgentConfiguration(model, transport, files.CreateDefinitions(invoker), ExecutionMode: ToolExecutionMode.Sequential);
        var baseTime = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long ticks = 0; var identity = 0;
        long Clock() => baseTime + Interlocked.Increment(ref ticks);
        string NextId() => "offline-" + Interlocked.Increment(ref identity).ToString("D4", CultureInfo.InvariantCulture);
        var codec = new SessionEntryCodec();
        var header = codec.Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = "offline-demo-header",
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Clock()).ToString("O", CultureInfo.InvariantCulture), cwd = canonicalRoot }));
        var options = new PersistentAgentSessionOptions(AgentOptions: new(Loop: new(MaximumTurns: 4, MaximumTranscriptMessages: 64)),
            SessionLogStoreOptions: new(ReaderOptions: new(MaximumInputBytes: 1_048_576, MaximumLines: 128, MaximumRecords: 128)));
        AgentLoopResult first; long firstAcknowledgedBytes; string? firstLeaf;
        await using (var session = await PersistentAgentSession.CreateAsync(sessionPath, header, configuration, Clock, NextId, options,
            cancellationToken).ConfigureAwait(false))
        {
            var system = new TranscriptEntry("system", JsonData.Parse(JsonSerializer.Serialize(new { role = "system", content = "Run the bounded offline file demo.",
                timestamp = Clock(), toolsAdded = files.ToolsAdded.Value })));
            first = await session.PromptAsync([system, User(InitialPrompt, Clock())], cancellationToken).ConfigureAwait(false);
            Check(first.Reason == AgentLoopStopReason.Completed && FinalText(first) == FirstFinalText, "First offline run did not complete.");
            firstAcknowledgedBytes = session.Snapshot.Log.CommittedByteLength; firstLeaf = session.Snapshot.Context.LeafId;
            Check(session.Snapshot.Fault is null && session.Snapshot.Log.Entries.Any(entry => IsToolResult(entry, "write")),
                "Completed write lacks an acknowledged durable session snapshot.");
        }
        var firstBytes = await BoundedSessionBytesAsync(sessionPath, cancellationToken).ConfigureAwait(false);
        Check(firstBytes.Length == firstAcknowledgedBytes, "First acknowledged byte length differs from the settled session file.");
        var firstLog = await new SessionLogReader(new(MaximumInputBytes: 1_048_576)).ReadFileAsync(sessionPath, cancellationToken).ConfigureAwait(false);
        Check(firstLog.Status == SessionLogReadStatus.Complete && firstLog.ValidatedPrefix.Any(record => IsToolResult(record.Entry, "write")),
            "Completed write result was not preserved in the session.");
        AgentLoopResult resumed; long resumedAcknowledgedBytes;
        await using (var session = await PersistentAgentSession.OpenAsync(sessionPath, configuration, Clock, NextId, options, cancellationToken).ConfigureAwait(false))
        {
            Check(session.Snapshot.Context.LeafId == firstLeaf && session.Snapshot.Context.LlmMessages.Length == first.Transcript.Length &&
                session.Snapshot.Log.CommittedByteLength == firstAcknowledgedBytes, "Reopened selected context differs from the acknowledged branch.");
            resumed = await session.PromptAsync(User(ResumePrompt, Clock()), cancellationToken).ConfigureAwait(false);
            Check(resumed.Reason == AgentLoopStopReason.Completed && FinalText(resumed) == ResumedFinalText && handler.RestoredHistoryVerified,
                "Reopened run did not use persisted history.");
            resumedAcknowledgedBytes = session.Snapshot.Log.CommittedByteLength;
            Check(session.Snapshot.Fault is null, "Resumed session did not acknowledge its commits.");
        }
        var finalBytes = await BoundedSessionBytesAsync(sessionPath, cancellationToken).ConfigureAwait(false);
        Check(finalBytes.Length == resumedAcknowledgedBytes, "Resumed acknowledged byte length differs from the settled session file.");
        Check(finalBytes.AsSpan().StartsWith(firstBytes), "Reopened session changed its committed prefix.");
        Check(await File.ReadAllTextAsync(outputPath, Utf8, cancellationToken).ConfigureAwait(false) == OutputText, "Native output file differs.");
        var finalLog = await new SessionLogReader(new(MaximumInputBytes: 1_048_576)).ReadFileAsync(sessionPath, cancellationToken).ConfigureAwait(false);
        Check(finalLog.Status == SessionLogReadStatus.Complete && handler.Requests.Count == 4 && policy.Actions.Count == 2,
            "Offline evidence is incomplete.");
        return new(JsonData.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, status = "completed", scope = "authored-offline-native-agent-tools-session-cli", workspace, canonicalWorkspace = canonicalRoot, sessionPath,
            inputFile = inputPath, outputFile = outputPath, outputText = OutputText, firstFinalText = FinalText(first), resumedFinalText = FinalText(resumed),
            requests = handler.Requests.Select((payload, index) => new { sequence = index + 1, endpoint = Endpoint.AbsoluteUri, method = "POST",
                inertAuthorizationVerified = true, payload = payload.Value }),
            finalActions = policy.Actions.Select(action => new { action.ToolName, action.Operation, action.Target, arguments = action.Arguments.Value }),
            session = new { version = 3, reopened = true, restoredHistoryVerified = handler.RestoredHistoryVerified,
                firstCommittedByteLength = firstBytes.Length, finalCommittedByteLength = finalBytes.Length,
                firstAcknowledgedByteLength = firstAcknowledgedBytes, resumedAcknowledgedByteLength = resumedAcknowledgedBytes, firstSelectedLeafId = firstLeaf,
                firstCommittedSha256 = Hash(firstBytes), finalCommittedSha256 = Hash(finalBytes),
                appendOnlyPrefixVerified = true, validatedRecords = finalLog.ValidatedPrefix.Length },
            networkUsed = false, providerKeySource = "explicit-inert-authored-demo-value", upstreamDifferential = false, phaseAcceptanceClaimed = false
        })));
    }

    private static TranscriptEntry User(string text, long time) => new("user", JsonData.Parse(JsonSerializer.Serialize(new { role = "user", content = text, timestamp = time })));
    private static string FinalText(AgentLoopResult result) => string.Concat(PiWireJson.ReadMessage(result.Transcript.Last(value => value.Role == "assistant").WireBody.Value)
        .Content.OfType<TextContent>().Select(value => value.Text));
    private static bool IsToolResult(SessionEntry entry, string name) => entry.Kind == SessionEntryKind.Message &&
        entry.WireBody.Value.GetProperty("message").GetProperty("role").GetString() == "toolResult" &&
        entry.WireBody.Value.GetProperty("message").GetProperty("toolName").GetString() == name;
    private static async Task<byte[]> BoundedSessionBytesAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidOperationException("Demo session exceeds its byte limit.");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        Check(bytes.Length <= 1_048_576, "Demo session exceeds its byte limit."); return bytes;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string ValidateAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 2048 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new OfflineDemoException(OfflineDemoFailure.InvalidPath);
        if (OperatingSystem.IsWindows() && path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new OfflineDemoException(OfflineDemoFailure.InvalidPath);
        try { _ = Utf8.GetByteCount(path); return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new OfflineDemoException(OfflineDemoFailure.InvalidPath); }
    }
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class DemoPolicy(string root, string inputPath, string outputPath) : IToolActionPolicy
    {
        public List<PreparedToolAction> Actions { get; } = [];
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction finalAction, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var confined = finalAction.Target.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
            var allowed = confined && ((finalAction.Operation == "read" && string.Equals(finalAction.Target, inputPath, PathComparison)) ||
                (finalAction.Operation == "write" && string.Equals(finalAction.Target, outputPath, PathComparison)));
            Actions.Add(finalAction); return ValueTask.FromResult(new ToolActionAuthorization(allowed));
        }
    }

    private sealed class DemoHandler(string outputPath) : HttpMessageHandler
    {
        public List<JsonData> Requests { get; } = [];
        public bool RestoredHistoryVerified { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var authorization = request.Headers.Authorization;
            Check(request.Method == HttpMethod.Post && request.RequestUri == Endpoint && authorization is not null && authorization.Scheme == "Bearer" &&
                authorization.Parameter == InertKey, "Demo request profile differs.");
            var bytes = await request.Content!.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            Check(bytes.Length <= 65_536 && Requests.Count < 4, "Demo request exceeds its bounded script.");
            var payload = JsonData.Parse(Utf8.GetString(bytes)); var body = payload.Value;
            Check(body.GetProperty("model").GetString() == "offline-demo-text-tool" && body.GetProperty("stream").GetBoolean() && !body.GetProperty("store").GetBoolean(),
                "Demo request factory fields differ.");
            var declarations = body.GetProperty("tools").EnumerateArray().ToArray();
            Check(declarations.Length == 2 && declarations.Select(tool => tool.GetProperty("name").GetString()).SequenceEqual(new[] { "read", "write" }),
                "Native read/write declarations did not reach the HTTP request.");
            var input = body.GetProperty("input"); string sse;
            switch (Requests.Count)
            {
                case 0:
                    Check(HasText(input, "user", InitialPrompt), "Initial prompt did not reach the HTTP request.");
                    sse = ToolSse("read", "offline-read-1", "fc_read_1", JsonSerializer.Serialize(new { path = "input.txt" })); break;
                case 1:
                    Check(HasOutput(input, "offline-read-1", InputText), "Real read output did not reach the follow-up HTTP request.");
                    sse = ToolSse("write", "offline-write-1", "fc_write_1", JsonSerializer.Serialize(new { path = "answer.txt", content = OutputText })); break;
                case 2:
                    Check(HasOutput(input, "offline-write-1", "Successfully wrote to answer.txt"), "Write result did not reach the final HTTP request.");
                    Check(await File.ReadAllTextAsync(outputPath, Utf8, token).ConfigureAwait(false) == OutputText, "Final response preceded the native write effect.");
                    sse = TextSse("msg_offline_first", FirstFinalText); break;
                default:
                    RestoredHistoryVerified = HasText(input, "user", InitialPrompt) && HasText(input, "user", ResumePrompt) &&
                        HasText(input, "assistant", FirstFinalText) && HasOutput(input, "offline-read-1", InputText) &&
                        HasOutput(input, "offline-write-1", "Successfully wrote to answer.txt");
                    Check(RestoredHistoryVerified, "Persisted branch history did not reach the reopened HTTP request.");
                    sse = TextSse("msg_offline_resumed", ResumedFinalText); break;
            }
            Requests.Add(payload);
            var content = new ByteArrayContent(Utf8.GetBytes(sse)); content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream") { CharSet = "utf-8" };
            return new(HttpStatusCode.OK) { Content = content };
        }
        private static bool HasOutput(JsonElement input, string call, string text) => input.EnumerateArray().Any(item =>
            item.TryGetProperty("type", out var kind) && kind.GetString() == "function_call_output" && item.GetProperty("call_id").GetString() == call && item.GetProperty("output").GetString() == text);
        private static bool HasText(JsonElement input, string role, string text) => input.EnumerateArray().Any(item =>
            item.TryGetProperty("role", out var actual) && actual.GetString() == role &&
            item.GetProperty("content").EnumerateArray().Any(part => part.TryGetProperty("text", out var value) && value.GetString() == text));
        private static string ToolSse(string name, string call, string id, string arguments) => Sse(
            JsonSerializer.Serialize(new { type = "response.output_item.added", output_index = 0, item = new { type = "function_call", id, call_id = call, name, arguments = "" } }),
            JsonSerializer.Serialize(new { type = "response.function_call_arguments.delta", output_index = 0, item_id = id, delta = arguments[..(arguments.Length / 2)] }),
            JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 0, item = new { type = "function_call", id, call_id = call, name, arguments } }), Completed);
        private static string TextSse(string id, string text) => Sse(
            JsonSerializer.Serialize(new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id, content = Array.Empty<object>() } }),
            JsonSerializer.Serialize(new { type = "response.output_text.delta", output_index = 0, item_id = id, delta = text }),
            JsonSerializer.Serialize(new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id, content = new[] { new { type = "output_text", text } } } }), Completed);
        private const string Completed = "{\"type\":\"response.completed\",\"response\":{\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":8,\"output_tokens\":4,\"total_tokens\":12}}}";
        private static string Sse(params string[] events) => string.Concat(events.Append("[DONE]").Select(value => "data: " + value + "\n\n"));
    }
}
