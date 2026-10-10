// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/coding-agent/src/core/bug-report.ts.
using System.Collections;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.CodingAgent.Usage;
using PiSharp.Contracts;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Serialization;

namespace PiSharp.CodingAgent.Diagnostics;

/// <summary>The provider facts <c>describeProvider</c> reads from the model runtime. <see cref="AuthStatus"/> is
/// <c>modelRuntime.getProviderAuthStatus(id)</c> as JSON (<c>{"configured":true,"source":"stored"}</c>, ...).</summary>
public sealed record BugReportProviderInfo(string Id, string Name, string? BaseUrl, IReadOnlyList<string> HeaderNames, bool ApiKeyAuth,
    bool OAuthAuth, JsonData AuthStatus, bool UsingOAuth, bool RegisteredByExtension);

/// <summary><c>CollectBugReportMetadataOptions</c>. <see cref="Model"/> is the session model as a Pi model object (catalog shape);
/// settings are the raw global and project settings objects.</summary>
public sealed record BugReportMetadataOptions(string SessionId, string Cwd, bool IncludeSession, bool IncludeSummary, int MessageCount,
    string ThinkingLevel, JsonData GlobalSettings, JsonData ProjectSettings)
{
    public string? Id { get; init; }
    public string? Hint { get; init; }
    public JsonData? Model { get; init; }
    public BugReportProviderInfo? Provider { get; init; }
    public IReadOnlyList<DiagnosticExtensionInfo> Extensions { get; init; } = [];
    public IReadOnlyList<(string Path, string Error)> ExtensionErrors { get; init; } = [];
    /// <summary>The <c>environment</c> block; null collects it from this process (<see cref="BugReport.CollectEnvironment"/>).</summary>
    public JsonData? Environment { get; init; }
    public TimeProvider? TimeProvider { get; init; }
}

/// <summary>A report's files: <c>report.json</c> metadata, <c>diagnostics.json</c>, the optional transcript and summary.</summary>
public sealed record BugReportBundle(JsonData Metadata, JsonData Diagnostics, string? SessionJsonl = null, string? Summary = null)
{
    public string Id => Metadata.Value.GetProperty("id").GetString()!;
    public string CreatedAt => Metadata.Value.GetProperty("createdAt").GetString()!;
    public string? Hint => Metadata.Value.GetProperty("hint").ValueKind == JsonValueKind.String ? Metadata.Value.GetProperty("hint").GetString() : null;
    public bool SessionIncluded => Metadata.Value.GetProperty("session").GetProperty("included").GetBoolean();
    public bool SummaryIncluded => Metadata.Value.GetProperty("session").GetProperty("summaryIncluded").GetBoolean();
    public int CrashCount => Diagnostics.Value.GetProperty("crashes").GetArrayLength();
}

/// <summary>bug-report.ts <c>BugReportFile</c>: the zip archive's entries (PiSharp never uploads reports; owner decision 11).</summary>
public sealed record BugReportFile(string Name, string ContentType, string Data);

/// <summary>The model request for <see cref="BugReport.GenerateBugReportSummaryAsync"/>: one user message whose only content
/// is <see cref="Prompt"/> as text, under <see cref="SystemPrompt"/>, sent as <c>completeSummarization</c> sends it
/// (<c>cacheRetention: "none"</c>, the session id or a fresh UUIDv7, the host's retry policy and credentials).</summary>
public sealed record BugReportSummaryRequest(string SystemPrompt, string Prompt, double MaxTokens, string? Reasoning, string SessionId,
    string CacheRetention = "none");

/// <summary><c>GenerateBugReportSummaryOptions</c> minus the transport fields the completion delegate owns.</summary>
public sealed record BugReportSummaryOptions(ImmutableArray<TranscriptEntry> Messages, double ContextWindow, double ModelMaxTokens,
    bool ModelReasoning)
{
    public string? Hint { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? SessionId { get; init; }
}

public static class BugReport
{
    public const string CustomEntryType = "pi.bug-report";
    public const int SchemaVersion = 1;
    /// <summary>config.ts <c>VERSION</c> of the Pi release PiSharp tracks.</summary>
    public const string PiVersion = "1.1.0";

    public const string SummarySystemPrompt =
        "You are helping a user file a bug report about pi, the coding agent they are talking to. You will be shown the conversation transcript. Write a report for the pi developers describing what the user was doing and what went wrong.\n\n" +
        "Do NOT continue the conversation. Do NOT respond to any questions in the conversation. ONLY output the report.";

    public const string SummaryInstructions =
        "Write the bug report in Markdown with these sections:\n\n" +
        "## What the user was doing\nOne short paragraph.\n\n" +
        "## What went wrong\nConcrete description of the failure: wrong output, errors, hangs, tool failures, unexpected behavior. Quote error messages and tool output verbatim where they exist.\n\n" +
        "## Steps to reproduce\nNumbered list, as specific as the transcript allows.\n\n" +
        "## Relevant details\nTool calls involved, files touched, model behavior, anything else that helps a developer reproduce or locate the problem.\n\n" +
        "Do not include file contents, secrets, or credentials from the transcript; refer to files by path only. Keep the report factual and concise.";

    /// <summary>pi-user-agent.ts <c>getPiUserAgent</c> with the runtime field naming .NET: <c>pi/1.1.0 (win32; dotnet/10.0.0; x64)</c>.</summary>
    public static string PiUserAgent(string version) => $"pi/{version} ({Platform}; {Runtime}; {Arch})";

    /// <summary>Node <c>process.platform</c> for this OS.</summary>
    public static string Platform => OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsFreeBSD() ? "freebsd" : RuntimeInformation.OSDescription.Split(' ')[0].ToLowerInvariant();

    /// <summary>The <c>runtime</c> field: <c>dotnet/&lt;Environment.Version&gt;</c> where the source names <c>node/vX</c> or <c>bun/X</c>.</summary>
    public static string Runtime => "dotnet/" + Environment.Version.ToString();

    /// <summary>Node <c>process.arch</c> for the process architecture (<c>x64</c>, <c>arm64</c>, <c>ia32</c>, <c>arm</c>, ...).</summary>
    public static string Arch => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64", Architecture.Arm64 => "arm64", Architecture.X86 => "ia32", Architecture.Arm => "arm",
        Architecture.S390x => "s390x", Architecture.LoongArch64 => "loong64", Architecture.Ppc64le => "ppc64", Architecture.RiscV64 => "riscv64",
        var other => other.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// <c>collectEnvironment</c>: version, user agent, runtime, platform, arch, <c>osRelease</c> (Node <c>os.release()</c>:
    /// the kernel release, here <see cref="Environment.OSVersion"/>'s version on Windows and macOS and the release field of
    /// <see cref="RuntimeInformation.OSDescription"/> on Linux), <c>osVersion</c> (Node <c>os.version()</c>, here
    /// <see cref="RuntimeInformation.OSDescription"/>), the shell's file name from <c>SHELL</c>, terminal facts and the sorted
    /// names (never values) of <c>PI_*</c> variables.
    /// </summary>
    public static JsonData CollectEnvironment(IReadOnlyDictionary<string, string?>? environment = null, string version = PiVersion)
    {
        var env = environment ?? Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);
        string? Get(string name) => env.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;
        JsonNode? Nullable(string name) => Get(name) is { } value ? JsonValue.Create(value) : null;
        var shell = Get("SHELL")?.Split('\\', '/')[^1];
        var description = RuntimeInformation.OSDescription;
        var release = OperatingSystem.IsLinux() && description.Split(' ') is { Length: > 1 } words ? words[1] : Environment.OSVersion.Version.ToString();
        var node = new JsonObject
        {
            ["version"] = version,
            ["userAgent"] = PiUserAgent(version),
            ["runtime"] = Runtime,
            ["platform"] = Platform,
            ["arch"] = Arch,
            ["osRelease"] = release,
            ["osVersion"] = description,
            ["shell"] = string.IsNullOrEmpty(shell) ? null : shell,
            ["terminal"] = new JsonObject
            {
                ["term"] = Nullable("TERM"),
                ["program"] = Nullable("TERM_PROGRAM"),
                ["programVersion"] = Nullable("TERM_PROGRAM_VERSION"),
                ["colorterm"] = Nullable("COLORTERM"),
                ["tmux"] = Get("TMUX") is not null,
                ["ssh"] = Get("SSH_CONNECTION") is not null || Get("SSH_CLIENT") is not null || Get("SSH_TTY") is not null,
                ["ci"] = Get("CI") is not null,
            },
            // Names help diagnose configuration; values never leave the machine.
            ["piEnvironmentVariables"] = new JsonArray([.. env.Keys.Where(name => name.StartsWith("PI_", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal).Select(name => (JsonNode)JsonValue.Create(name)!)]),
        };
        return JsonData.Parse(JsJson.Stringify(node));
    }

    /// <summary><c>collectBugReportMetadata</c>: the <c>report.json</c> object.</summary>
    public static JsonData CollectBugReportMetadata(BugReportMetadataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var hint = options.Hint is null ? "" : JsJson.Trim(options.Hint);
        var session = new JsonObject
        {
            ["id"] = options.SessionId, ["included"] = options.IncludeSession, ["summaryIncluded"] = options.IncludeSummary,
            ["messageCount"] = options.MessageCount,
        };
        if (options.IncludeSession) session["cwd"] = options.Cwd;
        var node = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["id"] = options.Id ?? Guid.CreateVersion7().ToString(),
            ["createdAt"] = JsDate.ToIsoString((options.TimeProvider ?? TimeProvider.System).GetUtcNow()),
            ["hint"] = hint.Length == 0 ? null : hint,
            ["environment"] = Node(options.Environment ?? CollectEnvironment()),
            ["session"] = session,
            ["model"] = options.Model is { } model ? DescribeModel(model.Value) : null,
            ["provider"] = options.Model is not null && options.Provider is { } provider ? DescribeProvider(provider) : null,
            ["thinkingLevel"] = options.ThinkingLevel,
            ["extensions"] = new JsonArray([.. options.Extensions.Select(extension => (JsonNode)new JsonObject
            {
                ["path"] = extension.Path, ["source"] = BugReportRedaction.RedactUrl(extension.Source), ["scope"] = extension.Scope,
                ["origin"] = extension.Origin, ["hidden"] = extension.Hidden,
            })]),
            ["extensionErrors"] = new JsonArray([.. options.ExtensionErrors.Select(error => (JsonNode)new JsonObject { ["path"] = error.Path, ["error"] = error.Error })]),
            ["settings"] = new JsonObject { ["global"] = RedactSettings(options.GlobalSettings), ["project"] = RedactSettings(options.ProjectSettings) },
        };
        return JsonData.Parse(JsJson.Stringify(node));
    }

    private static JsonNode? RedactSettings(JsonData settings)
    {
        var node = Node(settings);
        if (node is JsonObject settingsObject) { settingsObject.Remove("trackingId"); settingsObject.Remove("deviceId"); }
        return BugReportRedaction.RedactJsonValue(node);
    }

    private static JsonObject DescribeModel(JsonElement model)
    {
        JsonNode? Field(string name) => model.TryGetProperty(name, out var value) ? JsonNode.Parse(value.GetRawText()) : null;
        bool Truthy(string name) => model.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False) &&
            !(value.ValueKind == JsonValueKind.Number && value.GetDouble() == 0) && !(value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0);
        var node = new JsonObject();
        void Copy(string name) { if (model.TryGetProperty(name, out _)) node[name] = Field(name); }
        Copy("provider"); Copy("id"); Copy("name"); Copy("api");
        if (model.TryGetProperty("baseUrl", out var baseUrl))
            node["baseUrl"] = baseUrl.ValueKind == JsonValueKind.String ? BugReportRedaction.RedactUrl(baseUrl.GetString()!) : Field("baseUrl");
        Copy("reasoning"); Copy("input"); Copy("contextWindow"); Copy("maxTokens");
        node["samplingParams"] = Truthy("samplingParams") ? BugReportRedaction.RedactJsonValue(Field("samplingParams")) : null;
        node["compat"] = Truthy("compat") ? BugReportRedaction.RedactJsonValue(Field("compat")) : null;
        node["thinkingLevelMap"] = model.TryGetProperty("thinkingLevelMap", out var map) && map.ValueKind != JsonValueKind.Null ? Field("thinkingLevelMap") : null;
        node["headerNames"] = new JsonArray([.. (model.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object
            ? headers.EnumerateObject().Select(header => header.Name) : []).Order(StringComparer.Ordinal).Select(name => (JsonNode)JsonValue.Create(name)!)]);
        return node;
    }

    private static JsonObject DescribeProvider(BugReportProviderInfo provider)
    {
        var authTypes = new JsonArray();
        if (provider.ApiKeyAuth) authTypes.Add("api_key");
        if (provider.OAuthAuth) authTypes.Add("oauth");
        return new()
        {
            ["id"] = provider.Id, ["name"] = provider.Name,
            ["baseUrl"] = string.IsNullOrEmpty(provider.BaseUrl) ? null : BugReportRedaction.RedactUrl(provider.BaseUrl),
            ["headerNames"] = new JsonArray([.. provider.HeaderNames.Order(StringComparer.Ordinal).Select(name => (JsonNode)JsonValue.Create(name)!)]),
            ["authTypes"] = authTypes, ["authStatus"] = Node(provider.AuthStatus), ["usingOAuth"] = provider.UsingOAuth,
            ["registeredByExtension"] = provider.RegisteredByExtension,
        };
    }

    /// <summary><c>collectBugReportDiagnostics</c>: failed or diagnosed assistant turns, without conversation content, and the
    /// crash records (minus <c>notified</c>). <paramref name="entries"/> are the session's entries (<c>getEntries()</c>);
    /// a header entry is skipped.</summary>
    public static JsonData CollectBugReportDiagnostics(string sessionId, IEnumerable<SessionEntry> entries, IEnumerable<CrashRecord>? crashes = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var list = entries.Where(entry => !entry.IsHeader).ToList();
        var assistant = new JsonArray(); var assistantMessageCount = 0;
        foreach (var entry in list)
        {
            if (entry.Kind != SessionEntryKind.Message || !entry.WireBody.Value.TryGetProperty("message", out var message) ||
                UsageWire.OptionalString(message, "role") != "assistant") continue;
            assistantMessageCount++;
            var diagnostics = message.TryGetProperty("diagnostics", out var stored) && stored.ValueKind != JsonValueKind.Null
                ? JsonNode.Parse(stored.GetRawText()) : new JsonArray();
            var empty = diagnostics is JsonArray { Count: 0 } || diagnostics is JsonValue text && text.GetValueKind() == JsonValueKind.String && ((string)text!).Length == 0;
            var stopReason = UsageWire.OptionalString(message, "stopReason");
            message.TryGetProperty("errorMessage", out var errorMessage);
            var hasError = errorMessage.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False) &&
                !(errorMessage.ValueKind == JsonValueKind.String && errorMessage.GetString()!.Length == 0) &&
                !(errorMessage.ValueKind == JsonValueKind.Number && errorMessage.GetDouble() == 0);
            if (empty && stopReason is not ("error" or "aborted") && !hasError) continue;
            var item = new JsonObject
            {
                ["entryId"] = entry.Id, ["timestamp"] = entry.Timestamp,
                ["provider"] = Raw(message, "provider"), ["model"] = Raw(message, "model"), ["api"] = Raw(message, "api"),
                ["stopReason"] = Raw(message, "stopReason"),
            };
            if (message.TryGetProperty("rawStopReason", out _)) item["rawStopReason"] = Raw(message, "rawStopReason");
            if (errorMessage.ValueKind != JsonValueKind.Undefined) item["errorMessage"] = Raw(message, "errorMessage");
            item["diagnostics"] = diagnostics;
            assistant.Add(item);
        }
        var node = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion, ["sessionId"] = sessionId, ["entryCount"] = list.Count,
            ["assistantMessageCount"] = assistantMessageCount, ["assistant"] = assistant,
            ["crashes"] = new JsonArray([.. (crashes ?? []).Select(crash =>
            {
                var record = (JsonObject)crash.Node.DeepClone(); record.Remove("notified"); return (JsonNode)record;
            })]),
        };
        return JsonData.Parse(JsJson.Stringify(node));
    }

    /// <summary><c>bugReportFiles</c>: <c>report.json</c>, <c>diagnostics.json</c>, then <c>session.jsonl</c> and <c>summary.md</c>
    /// when present.</summary>
    public static IReadOnlyList<BugReportFile> BugReportFiles(BugReportBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var files = new List<BugReportFile>
        {
            new("report.json", "application/json", JsJson.Stringify(Node(bundle.Metadata), 2) + "\n"),
            new("diagnostics.json", "application/json", JsJson.Stringify(Node(bundle.Diagnostics), 2) + "\n"),
        };
        if (bundle.SessionJsonl is not null) files.Add(new("session.jsonl", "application/x-ndjson", bundle.SessionJsonl));
        if (bundle.Summary is not null) files.Add(new("summary.md", "text/markdown", bundle.Summary.EndsWith('\n') ? bundle.Summary : bundle.Summary + "\n"));
        return files;
    }

    public static Task WriteBugReportArchiveAsync(BugReportBundle bundle, string filePath, TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) =>
        ZipArchiveWriter.WriteZipArchiveAsync(filePath, [.. BugReportFiles(bundle).Select(file => new ZipEntry(file.Name, file.Data))], timeProvider, cancellationToken);

    public static string BugReportArchiveFileName(string id) => $"pi-bug-report-{id}.zip";

    /// <summary>The <c>pi.bug-report</c> custom entry data (<c>BugReportSessionEntryData</c>) recorded after a successful
    /// export. <paramref name="delivery"/> is <c>"zip"</c> or PiSharp's <c>"github-issue"</c> (Pi's <c>"upload"</c> is never
    /// recorded: PiSharp does not upload reports); <paramref name="path"/> is the archive.</summary>
    public static JsonData BugReportSessionEntryData(BugReportBundle bundle, string delivery, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var node = new JsonObject
        {
            ["id"] = bundle.Id, ["createdAt"] = bundle.CreatedAt, ["hint"] = bundle.Hint, ["sessionIncluded"] = bundle.SessionIncluded,
            ["summaryIncluded"] = bundle.SummaryIncluded, ["delivery"] = delivery,
        };
        if (path is not null) node["path"] = path;
        return JsonData.Parse(JsJson.Stringify(node));
    }

    /// <summary><c>selectMessages</c>: the newest messages whose <c>estimateTokens</c> sum fits the budget (at least one).</summary>
    internal static ImmutableArray<TranscriptEntry> SelectMessages(ImmutableArray<TranscriptEntry> messages, double tokenBudget)
    {
        var selected = new List<TranscriptEntry>(); double tokens = 0;
        for (var index = messages.Length - 1; index >= 0; index--)
        {
            var next = SessionCompactionTokenEstimator.EstimateTokens(messages[index]);
            if (selected.Count > 0 && tokens + next > tokenBudget) break;
            selected.Add(messages[index]); tokens += next;
        }
        selected.Reverse();
        return [.. selected];
    }

    /// <summary>The summary request <c>generateBugReportSummary</c> sends: the newest messages fitting 60% of the context
    /// window (128k when unknown), serialized as compaction does, the user's report and the instructions.</summary>
    public static BugReportSummaryRequest CreateBugReportSummaryRequest(BugReportSummaryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Messages.IsDefault) throw new ArgumentException("Messages are required.", nameof(options));
        var contextWindow = options.ContextWindow > 0 ? options.ContextWindow : 128_000;
        var messages = SelectMessages(options.Messages, Math.Floor(contextWindow * 0.6));
        var hint = options.Hint is null ? "" : JsJson.Trim(options.Hint);
        var parts = new List<string>();
        if (messages.Length < options.Messages.Length) parts.Add($"Note: only the last {messages.Length} of {options.Messages.Length} messages are shown.");
        parts.Add($"<conversation>\n{SessionSummaryRequestBuilder.SerializeConversation(messages)}\n</conversation>");
        if (hint.Length != 0) parts.Add($"<user-report>\n{hint}\n</user-report>");
        parts.Add(SummaryInstructions);
        return new(SummarySystemPrompt, string.Join("\n\n", parts), Math.Min(4096, options.ModelMaxTokens > 0 ? options.ModelMaxTokens : double.PositiveInfinity),
            options.ModelReasoning && options.ThinkingLevel is { Length: > 0 } level && level != "off" ? level : null,
            options.SessionId ?? Guid.CreateVersion7().ToString());
    }

    /// <summary><c>generateBugReportSummary</c>: ask the session model for a report when the user does not share the transcript.
    /// <paramref name="complete"/> runs the request (the host's <c>completeSummarization</c>: stream function, retry policy,
    /// credentials). Throws <see cref="InvalidOperationException"/> with the source's messages.</summary>
    public static async Task<string> GenerateBugReportSummaryAsync(BugReportSummaryOptions options,
        Func<BugReportSummaryRequest, CancellationToken, Task<AssistantMessage>> complete, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(complete);
        var response = await complete(CreateBugReportSummaryRequest(options), cancellationToken).ConfigureAwait(false);
        if (response.StopReason == StopReason.Aborted) throw new InvalidOperationException("Bug report summary was cancelled");
        if (GetSummarizationFailure(response, "Bug report summary") is { } failure) throw new InvalidOperationException(failure);
        if (response.Content.Any(block => block is ToolCallContent)) throw new InvalidOperationException("Bug report summary attempted to call a tool");
        var text = JsJson.Trim(string.Join("\n", response.Content.OfType<TextContent>().Select(block => block.Text)));
        if (text.Length == 0) throw new InvalidOperationException("Bug report summary was empty");
        return text;
    }

    /// <summary>compaction.ts <c>getSummarizationFailure</c>.</summary>
    internal static string? GetSummarizationFailure(AssistantMessage response, string label)
    {
        if (response.StopReason == StopReason.Error)
        {
            var error = response.ExtraProperties is { } extras && extras.TryGet("errorMessage", out var value) && value is not null &&
                value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : null;
            return $"{label} failed: {(string.IsNullOrEmpty(error) ? "Unknown error" : error)}";
        }
        if (response.StopReason == StopReason.Length) return $"{label} failed: generation hit the token cap and the summary is incomplete";
        return null;
    }

    private static JsonNode? Node(JsonData value) => JsonNode.Parse(value.ToString());
    private static JsonNode? Raw(JsonElement value, string name) => value.TryGetProperty(name, out var field) ? JsonNode.Parse(field.GetRawText()) : null;
}
