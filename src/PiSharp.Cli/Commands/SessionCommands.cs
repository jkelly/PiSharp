using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Cli.Output;
using PiSharp.Cli.Extensions;
using PiSharp.Cli.Prompts;
using PiSharp.Cli.Settings;
using PiSharp.Cli.Skills;
using PiSharp.CodingAgent.Resources.Skills;
using PiSharp.CodingAgent.Configuration;
using PiSharp.CodingAgent.Resources;
using PiSharp.Extensions;
using PiSharp.Extensions.Runtime;

namespace PiSharp.Cli.Commands;

public enum SessionCommandFailure { InvalidArguments, InvalidPath, WorkspaceMissing, WorkspaceMismatch, ReservedTarget, InvalidScript, ResourceLimit, CommandFailed, OfflineProviderMismatch, InvalidBashConfiguration, UnsupportedBashPlatform }
public sealed class SessionCommandException : Exception
{
    public SessionCommandFailure Failure { get; }
    internal SessionCommandException(SessionCommandFailure failure) : base(failure switch
    {
        SessionCommandFailure.InvalidArguments => "Session command arguments are invalid.",
        SessionCommandFailure.InvalidPath => "Session commands require bounded absolute local paths.",
        SessionCommandFailure.WorkspaceMissing => "The explicit workspace directory must already exist.",
        SessionCommandFailure.WorkspaceMismatch => "Session working directory does not match the explicit workspace.",
        SessionCommandFailure.ReservedTarget => "File authorization must name workspace files other than the session or script.",
        SessionCommandFailure.InvalidScript => "The bounded authored offline provider script is invalid or does not match the request history.",
        SessionCommandFailure.ResourceLimit => "Session command exceeds configured bounds.",
        SessionCommandFailure.OfflineProviderMismatch => "Selected durable model has no binding in the requested authored offline API profile.",
        SessionCommandFailure.InvalidBashConfiguration => "Offline Bash requires explicit valid executable, workspace spill root and exact command authorization.",
        SessionCommandFailure.UnsupportedBashPlatform => "The configured offline Bash backend requires Windows.",
        _ => "Session command failed; inspect durable state before retrying."
    }) => Failure = failure;
    /// <summary>A failure whose message mirrors upstream CLI diagnostic text exactly.</summary>
    internal SessionCommandException(SessionCommandFailure failure, string message) : base(message) => Failure = failure;
}

/// <summary>One-shot explicit-path native commands. Reports settlement, not upstream RPC prompt acceptance.</summary>
public static class SessionCommands
{
    public const string Usage = "session create --session <new absolute JSONL> --workspace <existing absolute directory> [--offline-api openai-responses|anthropic-messages|openai-completions] " +
        "[[--bash-executable <absolute file>] --bash-spill-root <existing workspace directory> --allow-bash-command <exact command> [--bash-timeout <seconds>]]; " +
        "session prompt|resume --session <JSONL> --workspace <directory> --offline-script <JSON> --message <text> " +
        "[--offline-api openai-responses|anthropic-messages|openai-completions] [--offline-images true|false (anthropic-messages|openai-completions)] [--leaf <id>|--root] [--allow-read <absolute file>] [--allow-write <absolute file>] " +
        "[--output report|print|json] " +
        "[[--bash-executable <absolute file>] --bash-spill-root <existing workspace directory> --allow-bash-command <exact command> [--bash-timeout <seconds>]]; " +
        NativeExtensionConfiguration.Flags + " " + PromptTemplateCliConfiguration.Flags + " " + SettingsStartupConfiguration.Flags + " " + ToolSelectionCliConfiguration.Flags + " " + SkillCliConfiguration.Flags +
        " (startup settings/tools apply to create, prompt and resume); session inspect|tree|history --session <JSONL> [--leaf <id>|--root]";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SessionLogReaderOptions ReaderBounds = new(MaximumInputBytes: 8_388_608, MaximumLines: 10_000, MaximumRecords: 10_000);
    private sealed record Arguments(string Command, string Session, string? Workspace, string? Script, string? Message,
        bool Latest, string? Leaf, ImmutableArray<string> Reads, ImmutableArray<string> Writes, string OfflineApi,
        OfflineBashAuthorization? Bash, bool Print, bool Json, NativeExtensionConfiguration? Extension, bool SupportsImages,
        PromptTemplateCliConfiguration Prompts, StartupSettingsRequest? Settings, ToolSelectionCliOptions Tools, SkillCliConfiguration Skills);

    public static Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default,
        PiSharp.Cli.Mcp.McpProfileRuntimeAdmission? mcpAdmission = null,
        Func<bool, CancellationToken, Task>? persistRetryEnabledOriginal = null) =>
        RunCoreAsync(args, stdout, stderr, new(), cancellationToken, mcpAdmission, persistRetryEnabledOriginal);

    /// <summary>Explicit trusted native JSON delivery limits; other output modes keep their existing profile.</summary>
    public static Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr,
        SessionJsonEventOutputOptions jsonOutputOptions, CancellationToken cancellationToken = default,
        PiSharp.Cli.Mcp.McpProfileRuntimeAdmission? mcpAdmission = null,
        Func<bool, CancellationToken, Task>? persistRetryEnabledOriginal = null)
    {
        ArgumentNullException.ThrowIfNull(jsonOutputOptions); jsonOutputOptions.Validate();
        return RunCoreAsync(args, stdout, stderr, jsonOutputOptions, cancellationToken, mcpAdmission, persistRetryEnabledOriginal);
    }

    private static async Task<int> RunCoreAsync(string[] args, TextWriter stdout, TextWriter stderr,
        SessionJsonEventOutputOptions jsonOutputOptions, CancellationToken cancellationToken, PiSharp.Cli.Mcp.McpProfileRuntimeAdmission? mcpAdmission,
        Func<bool, CancellationToken, Task>? persistRetryEnabledOriginal)
    {
        ArgumentNullException.ThrowIfNull(stdout); ArgumentNullException.ThrowIfNull(stderr);
        var modifying = false;
        try
        {
            var parsed = Parse(args);
            modifying = parsed.Command is "create" or "prompt" or "resume";
            cancellationToken.ThrowIfCancellationRequested();
            var (report, exitCode, lastMessage) = parsed.Command is "inspect" or "tree" or "history" ?
                await InspectAsync(parsed, cancellationToken).ConfigureAwait(false) :
                await ModifyAsync(parsed, stdout, stderr, jsonOutputOptions, cancellationToken, mcpAdmission, persistRetryEnabledOriginal).ConfigureAwait(false);
            if (!parsed.Print && !parsed.Json) await WriteAsync(stdout, report).ConfigureAwait(false);
            else if (parsed.Print)
            {
                var printed = await SessionPrintOutput.WriteAsync(stdout, lastMessage).ConfigureAwait(false);
                if (printed is SessionPrintOutcome.ProviderError or SessionPrintOutcome.Aborted)
                {
                    await ErrorAsync(printed == SessionPrintOutcome.Aborted ? "Canceled" : "ProviderFailed",
                        "The final assistant request failed after durable settlement; inspect durable state.", true).ConfigureAwait(false);
                    return 1;
                }
                if (exitCode != 0)
                    await ErrorAsync("CompletedWithErrors", "Session completed with tool or run errors; inspect durable state.", true).ConfigureAwait(false);
            }
            else
            {
                var reason = lastMessage?.Role == "assistant" ? PiWireJson.ReadMessage(lastMessage.WireBody.Value).StopReason : StopReason.Stop;
                if (reason is StopReason.Error or StopReason.Aborted)
                {
                    await ErrorAsync(reason == StopReason.Aborted ? "Canceled" : "ProviderFailed",
                        "The final assistant request failed after durable settlement; inspect durable state.", true).ConfigureAwait(false);
                    return 1;
                }
                if (exitCode != 0)
                    await ErrorAsync("CompletedWithErrors", "Session completed with tool or run errors; inspect durable state.", true).ConfigureAwait(false);
            }
            return exitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { await ErrorAsync("Canceled", "Session command canceled after settling owned work; inspect durable state.", modifying).ConfigureAwait(false); return 1; }
        catch (SessionJsonEventOutputException error)
        { await ErrorAsync(error.Failure.ToString(), error.Message, modifying).ConfigureAwait(false); return 1; }
        catch (AgentSessionReplacementNotificationException error)
        { await ErrorAsync("ReplacementCommitted", error.Message, true).ConfigureAwait(false); return 1; }
        catch (NativeExtensionException error)
        { await ErrorAsync(error.Failure.ToString(), error.Message, modifying).ConfigureAwait(false); return error.Failure == NativeExtensionFailure.CleanupFailed ? 1 : 2; }
        catch (SessionRuntimeRegistryException error) when (error.Failure == SessionRuntimeRegistryFailure.UnknownModel)
        {
            var failure = new SessionCommandException(SessionCommandFailure.OfflineProviderMismatch);
            await ErrorAsync(failure.Failure.ToString(), failure.Message, modifying).ConfigureAwait(false); return 2;
        }
        catch (SessionCommandException error)
        { await ErrorAsync(error.Failure.ToString(), error.Message, modifying).ConfigureAwait(false); return error.Failure == SessionCommandFailure.CommandFailed ? 1 : 2; }
        catch (Exception)
        { await ErrorAsync("CommandFailed", "Session command failed; inspect durable state before retrying.", modifying).ConfigureAwait(false); return 1; }

        async Task ErrorAsync(string code, string message, bool effectsMayHaveCompleted) =>
            await WriteAsync(stderr, JsonData.Parse(JsonSerializer.Serialize(new
            { schemaVersion = 1, type = "session_command_result", status = "failed", code, message, effectsMayHaveCompleted }))).ConfigureAwait(false);
    }

    private static Arguments Parse(string[] args)
    {
        if (args is null || args.Length is < 2 or > 264 || args[0] != "session" ||
            args[1] is not ("create" or "prompt" or "resume" or "inspect" or "tree" or "history"))
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var tools = new ToolSelectionCliOptions();
        var skills = ImmutableArray.CreateBuilder<SkillPathSelection>();
        var reads = ImmutableArray.CreateBuilder<string>(); var writes = ImmutableArray.CreateBuilder<string>();
        var bashCommands = ImmutableArray.CreateBuilder<string>();
        var extensionTools = ImmutableArray.CreateBuilder<string>();
        var deniedExtensionTools = ImmutableArray.CreateBuilder<string>();
        var extensionCommands = ImmutableArray.CreateBuilder<string>();
        var prompts = ImmutableArray.CreateBuilder<PromptTemplatePathSelection>();
        var root = false;
        for (var index = 2; index < args.Length; index++)
        {
            if (SkillCliConfiguration.TryConsume(args, ref index, skills)) continue;
            if (PromptTemplateCliConfiguration.TryConsume(args, ref index, prompts)) continue;
            if (ToolSelectionCliConfiguration.TryConsume(args, ref index, ref tools)) continue;
            var key = args[index];
            if (key == "--root") { if (root) throw Invalid(); root = true; continue; }
            if (key is not ("--session" or "--workspace" or "--offline-script" or "--offline-api" or "--offline-images" or "--message" or "--leaf" or "--allow-read" or "--allow-write" or "--output" or
                "--bash-executable" or "--bash-spill-root" or "--allow-bash-command" or "--bash-timeout" or
                "--extension-package" or "--extension-manifest" or "--extension-approval" or "--extension-snapshot-root" or "--enable-extension-tool" or "--deny-extension-tool" or "--enable-extension-command" or
                "--user-settings" or "--project-settings" or "--steering-mode" or "--follow-up-mode") ||
                ++index >= args.Length) throw Invalid();
            var value = args[index];
            if (key == "--allow-read") reads.Add(Absolute(value));
            else if (key == "--allow-write") writes.Add(Absolute(value));
            else if (key == "--allow-bash-command") bashCommands.Add(value);
            else if (key == "--enable-extension-tool") extensionTools.Add(value);
            else if (key == "--deny-extension-tool") deniedExtensionTools.Add(value);
            else if (key == "--enable-extension-command") extensionCommands.Add(value);
            else if (!values.TryAdd(key, value)) throw Invalid();
        }
        if (!values.TryGetValue("--session", out var session) || root && values.ContainsKey("--leaf") ||
            reads.Count > 128 || writes.Count > 128) throw Invalid();
        var command = args[1];
        var writing = command is "prompt" or "resume";
        values.TryGetValue("--output", out var output);
        if (output is not null && (!writing || output is not ("report" or "print" or "json"))) throw Invalid();
        values.TryGetValue("--workspace", out var workspace); values.TryGetValue("--offline-script", out var script);
        values.TryGetValue("--message", out var message); values.TryGetValue("--leaf", out var leaf);
        values.TryGetValue("--offline-api", out var offlineApi);
        values.TryGetValue("--bash-executable", out var bashExecutable); values.TryGetValue("--bash-spill-root", out var bashSpillRoot);
        values.TryGetValue("--bash-timeout", out var bashTimeout);
        var bash = OfflineSessionProfile.BashOptions(bashExecutable, bashSpillRoot, bashCommands.ToImmutable(), bashTimeout);
        var model = OfflineSessionProfile.SelectModel(offlineApi);
        values.TryGetValue("--offline-images", out var imageInput);
        if (imageInput is not (null or "true" or "false") || imageInput == "true" && !OfflineSessionProfile.CanConfigureImageInput(model.Api)) throw Invalid();
        values.TryGetValue("--extension-package", out var extensionPackage); values.TryGetValue("--extension-manifest", out var extensionManifest);
        values.TryGetValue("--extension-approval", out var extensionApproval); values.TryGetValue("--extension-snapshot-root", out var extensionSnapshots);
        var extension = NativeExtensionConfiguration.Optional(extensionPackage, extensionManifest, extensionApproval, extensionSnapshots, extensionTools.ToImmutable(), deniedExtensionTools.ToImmutable(), extensionCommands.ToImmutable());
        var settings = SettingsStartupConfiguration.FromOptions(values);
        if ((command is "inspect" or "tree" or "history") && (tools.IsSpecified || skills.Count != 0 || settings is not null)) throw Invalid();
        if (!writing && prompts.Count != 0 || writing && (workspace is null || script is null || string.IsNullOrWhiteSpace(message)) ||
            command == "create" && (workspace is null || script is not null || message is not null || root || leaf is not null || reads.Count != 0 || writes.Count != 0) ||
            (command is "inspect" or "tree" or "history") && (workspace is not null || script is not null || offlineApi is not null || imageInput is not null || message is not null || reads.Count != 0 || writes.Count != 0 || bash is not null || extension is not null) ||
            leaf is { Length: 0 } || leaf?.Length > 4096 || message?.Length > 65_536 ||
            message is not null && !Unicode(message) || leaf is not null && (!Unicode(leaf) || leaf.Any(char.IsControl)))
            throw Invalid();
        return new(command, Absolute(session), workspace is null ? null : Absolute(workspace),
            script is null ? null : Absolute(script), message, !root && leaf is null, leaf, reads.ToImmutable(), writes.ToImmutable(), model.Api, bash,
            output == "print", output == "json", extension, imageInput == "true", new(prompts.ToImmutable()), settings, tools, new(skills.ToImmutable()));
        static SessionCommandException Invalid() => new(SessionCommandFailure.InvalidArguments);
    }

    internal static string Absolute(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Unicode(path) || path.Any(char.IsControl) ||
                !Path.IsPathFullyQualified(path) || OperatingSystem.IsWindows() && path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new SessionCommandException(SessionCommandFailure.InvalidPath);
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new SessionCommandException(SessionCommandFailure.InvalidPath); }
    }

    private static async Task<(JsonData Report, int ExitCode, TranscriptEntry? LastMessage)> ModifyAsync(Arguments args, TextWriter stdout, TextWriter stderr,
        SessionJsonEventOutputOptions jsonOutputOptions, CancellationToken token, PiSharp.Cli.Mcp.McpProfileRuntimeAdmission? mcpAdmission,
        Func<bool, CancellationToken, Task>? persistRetryEnabledOriginal)
    {
        if (!Directory.Exists(args.Workspace)) throw new SessionCommandException(SessionCommandFailure.WorkspaceMissing);
        var settings = await SettingsStartupConfiguration.LoadAsync(args.Settings, stderr, null, token).ConfigureAwait(false);
        var turns = args.Script is null ? ImmutableArray<JsonData>.Empty : await ScriptAsync(args.Script, token).ConfigureAwait(false);
        await using var profile = await OfflineSessionProfile.CreateAsync(args.Workspace!, args.Session, args.Script, turns, args.Reads, args.Writes, token,
            offlineApi: args.OfflineApi, bash: args.Bash, extension: args.Extension,
            extensionUi: new UnavailableExtensionUiProvider(args.Json ? ExtensionUiMode.Json : ExtensionUiMode.Print),
            modelSupportsImages: args.SupportsImages, toolSelection: ToolSelectionCliConfiguration.ResolveOptions(args.Tools, settings), mcpAdmission: args.Tools.NoMcp ? null : mcpAdmission,
            toolSettings: PiSharp.Tools.BuiltinToolSettings.FromSettings(settings?.Values)).ConfigureAwait(false);
        profile.ConfigureRetrySettings(settings, persistRetryEnabledOriginal);
        profile.ConfigureEffectiveSettings(settings);
        profile.BindSettingsThinkingReads();
        await profile.LoadPromptTemplatesAsync(args.Prompts, stderr, token).ConfigureAwait(false);
        await profile.LoadSkillsAsync(args.Skills, stderr, token).ConfigureAwait(false);
        var options = new PersistentAgentSessionOptions(UseLatestLeaf: args.Latest, SelectedLeafId: args.Leaf,
            AgentOptions: new(Loop: new(MaximumTurns: 64, MaximumTranscriptMessages: 1024)),
            SessionLogStoreOptions: new(ReaderOptions: ReaderBounds));
        var startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); long sequence = 0;
        long Clock() => startTime + Interlocked.Increment(ref sequence);
        string NextId() => "cli-" + Guid.NewGuid().ToString("N");
        var lifecycle = profile.CreateLifecycle(Clock, NextId, options,
            catalog: new SessionCatalog([new("session-directory", Path.GetDirectoryName(args.Session)!)]));
        PersistentAgentSessionSnapshot snapshot;
        var finalSessionPath = args.Session;
        AgentLoopResult? result = null; string? previousLeaf = null; SubmittedInputDisposition? inputDisposition = null;
        TranscriptEntry? operationLastMessage = null;
        if (args.Command == "create")
        {
            var header = new SessionEntryCodec().Parse(JsonSerializer.Serialize(new { type = "session", version = 3, id = NextId(),
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds(Clock()).ToString("O", CultureInfo.InvariantCulture), cwd = profile.Workspace }));
            var session = await lifecycle.CreateAsync(args.Session, header, profile.SelectedModel, token).ConfigureAwait(false);
            try
            {
                await session.ConfigureAsync(new(SystemMessage: new("system", profile.InitialSystem)), token).ConfigureAwait(false);
                await profile.AttachOwnerAsync(session, options, Clock, NextId, lifecycle: lifecycle).ConfigureAwait(false);
                await profile.ApplyInitialToolSelectionAsync(session, token).ConfigureAwait(false);
                if (settings is not null) { session.SteeringMode = settings.SteeringMode; session.FollowUpMode = settings.FollowUpMode; }
                await profile.StartLifecycleAsync("new", token).ConfigureAwait(false);
                await profile.Sessions!.Current.Session.WaitForIdleAsync().ConfigureAwait(false);
                operationLastMessage = profile.Sessions.Current.Session.Snapshot.Agent.Messages.LastOrDefault();
            }
            finally { await profile.CloseSessionOwnerAsync(session).ConfigureAwait(false); }
        }
        else
        {
            var session = await lifecycle.OpenAsync(new(args.Session, args.Latest, args.Leaf), profile.SelectedModel, token).ConfigureAwait(false);
            try
            {
                if (!string.Equals(Absolute(session.WorkingDirectory), profile.Workspace,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new SessionCommandException(SessionCommandFailure.WorkspaceMismatch);
                previousLeaf = session.Snapshot.Context.LeafId;
                await profile.ApplySkillsAsync(session, token).ConfigureAwait(false);
                await profile.AttachOwnerAsync(session, options, Clock, NextId, lifecycle: lifecycle).ConfigureAwait(false);
                await profile.ApplyInitialToolSelectionAsync(session, token).ConfigureAwait(false);
                if (settings is not null) { session.SteeringMode = settings.SteeringMode; session.FollowUpMode = settings.FollowUpMode; }
                await profile.StartLifecycleAsync("resume", token).ConfigureAwait(false);
                profile.ConfigureLifecycleModeStop(() => null); // One-shot intent; existing finally owns actual cleanup.
                async Task<AgentLoopResult?> SubmitAsync()
                {
                    var admission = profile.SelectOneShotInputAdmission(args.Message!);
                    if (admission is null)
                        return await session.PromptAsync(new TranscriptEntry("user", JsonData.Parse(JsonSerializer.Serialize(new
                            { role = "user", content = args.Message, timestamp = Clock() }))), token).ConfigureAwait(false);
                    var submitted = await session.SubmitInputAsync(new(args.Message!), admission,
                        cancellationToken: token).ConfigureAwait(false);
                    inputDisposition = submitted.Disposition;
                    return submitted.Run;
                }
                if (args.Json)
                {
                    await using var output = new SessionJsonEventOutput(session, stdout, jsonOutputOptions, profile.Sessions);
                    try
                    {
                        await output.StartAsync(token).ConfigureAwait(false);
                        result = await SubmitAsync().ConfigureAwait(false);
                        await session.WaitForIdleAsync().ConfigureAwait(false);
                        output.ThrowIfFailed();
                    }
                    catch
                    {
                        // Propagated sink failures can pass through tool/Agent wrappers. Preserve the original fixed output classification.
                        if (!token.IsCancellationRequested || output.Failure != SessionJsonEventOutputFailure.Canceled) output.ThrowIfFailed();
                        throw;
                    }
                }
                else result = await SubmitAsync().ConfigureAwait(false);
                await session.WaitForIdleAsync().ConfigureAwait(false);
                await profile.Sessions!.Current.Session.WaitForIdleAsync().ConfigureAwait(false);
                await profile.DrainLifecycleHandoffsAsync(session).ConfigureAwait(false);
                operationLastMessage = profile.Sessions.Current.Session.Snapshot.Agent.Messages.LastOrDefault();
            }
            finally { await profile.CloseSessionOwnerAsync(session).ConfigureAwait(false); }
        }
        // The attachment may have changed within a handled native command. Close all owned writers before reopening or reporting.
        await profile.Sessions!.DisposeAsync().ConfigureAwait(false);
        finalSessionPath = profile.Sessions.Current.Session.Path;
        snapshot = profile.Sessions.Current.Session.Snapshot;
        // Success is reported after the owned coordinator and writer have settled and closed.
        var bytes = new FileInfo(finalSessionPath).Length;
        if (snapshot.Fault is not null || bytes != snapshot.Log.CommittedByteLength)
            throw new SessionCommandException(SessionCommandFailure.CommandFailed);
        if (args.Json)
        {
            // Output and the coordinator have both settled. Prove this actual path can reacquire a complete writer lease without replay.
            await using var reopened = await SessionLogStore.OpenAsync(finalSessionPath, new(ReaderOptions: ReaderBounds), CancellationToken.None).ConfigureAwait(false);
            var actual = reopened.Snapshot;
            if (actual.CommittedByteLength != bytes || actual.Header.WireBody.ToString() != snapshot.Log.Header.WireBody.ToString() ||
                actual.LeafId != snapshot.Log.LeafId || actual.Entries.Length != snapshot.Log.Entries.Length ||
                !actual.Entries.Zip(snapshot.Log.Entries).All(pair => pair.First.WireBody.ToString() == pair.Second.WireBody.ToString()))
                throw new SessionCommandException(SessionCommandFailure.CommandFailed);
        }
        var toolErrors = snapshot.Agent.CompletedToolOutcomes.Any(outcome => outcome.IsError);
        var failed = result is not null && result.Reason != AgentLoopStopReason.Completed || toolErrors ||
            result is not null && profile.UsedTurns != profile.ScriptTurns;
        var final = result?.Transcript.LastOrDefault(entry => entry.Role == "assistant")?.WireBody;
        var lastMessage = inputDisposition == SubmittedInputDisposition.Handled ? null : operationLastMessage;
        if (args.Json) return (JsonData.EmptyObject, failed ? 1 : 0, lastMessage);
        var report = JsonData.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, type = "session_command_result", command = args.Command, status = failed ? "completed_with_errors" : "completed",
            sessionPath = finalSessionPath, workingDirectory = profile.Workspace, selectedLeafId = snapshot.Context.LeafId, previousSelectedLeafId = previousLeaf,
            physicalLeafId = snapshot.Log.LeafId, committedByteLength = snapshot.Log.CommittedByteLength,
            durableCheckpointAcknowledged = true, messageCount = snapshot.Context.LlmMessages.Length,
            model = snapshot.Context.Model, offlineApi = profile.SelectedModel.Api, thinkingLevel = snapshot.Context.ThinkingLevel,
            finalAssistant = final?.Value, stopReason = result?.Reason.ToString(), toolErrors,
            nativeDiagnostics = NativeSessionDiagnosticProjector.ToWire(NativeSessionDiagnosticProjector.Project(snapshot.Context)).Value,
            requests = profile.Requests, actions = profile.Actions, usedScriptTurns = profile.UsedTurns, scriptTurns = profile.ScriptTurns,
            offline = true, networkUsed = false, provenance = "authored-offline-wire-script-native-command", upstreamDifferential = false
        }));
        if (inputDisposition is not null)
            report = JsonData.Parse(report.ToString()[..^1] + ",\"inputDisposition\":" + JsonSerializer.Serialize(inputDisposition.ToString()) + "}");
        return (report, failed ? 1 : 0, lastMessage);
    }

    private static async Task<(JsonData Report, int ExitCode, TranscriptEntry? LastMessage)> InspectAsync(Arguments args, CancellationToken token)
    {
        var lifecycle = new SessionLifecycleReadOnly(new(CopyOptions: new(ReaderBounds, ReaderBounds.MaximumInputBytes)));
        var inspected = await lifecycle.InspectAsync(args.Session, args.Latest, args.Leaf, cancellationToken: token).ConfigureAwait(false);
        var read = inspected.CopyInspection.Log;
        var complete = read.SourceComplete && read.Status == SessionLogReadStatus.Complete && read.Header is not null;
        var entries = read.ValidatedPrefix.Skip(1).Select(record => record.Entry).ToImmutableArray();
        SessionContextProjection? projection = null;
        if (complete) projection = inspected.View?.Context;
        complete = complete && projection is not null;
        string? historyFailure = args.Command == "history" ? inspected.ContextFailure?.ToString() ?? inspected.TreeFailure?.ToString() : null;
        if (args.Command == "history" && complete)
        {
            SessionHistoryProjection? history = null;
            try { history = lifecycle.ProjectHistory(inspected.View!, token); }
            catch (SessionContextProjectionException error) { historyFailure = error.Failure.ToString(); complete = false; }
            if (history is not null)
            {
                object Statistics(SessionHistoryStatistics statistics) => new
                {
                    statistics.UserMessages, statistics.AssistantMessages, statistics.ToolCalls, statistics.ToolResults,
                    statistics.TotalMessages, status = statistics.Status.ToString(), statistics.Totals,
                    usage = statistics.Contributions.Select(contribution => new { sourceEntryId = contribution.SourceEntry.Id,
                        sourceEntryType = contribution.SourceEntry.Type, value = contribution.Usage.Value }),
                    unsupportedRecordIndexes = statistics.UnsupportedRecordIndexes
                };
                token.ThrowIfCancellationRequested();
                return (JsonData.Parse(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, type = "session_command_result", command = args.Command, status = "completed",
                    sessionPath = args.Session, sourceSha256 = inspected.SourceSha256, selectedLeafId = projection!.LeafId,
                    physicalLeafId = history.Tree.PhysicalLeafId, sessionName = history.Tree.SessionName,
                    labelsAvailable = history.Tree.LabelsAvailable, sessionNameAvailable = history.Tree.SessionNameAvailable,
                    labels = history.Tree.Labels,
                    fullHistory = history.FullHistory.Select(entry => entry.WireBody.Value),
                    branchHistory = history.BranchHistory.Select(entry => entry.WireBody.Value),
                    historyMessages = history.HistoryMessages.Select(message => message.WireBody.Value),
                    displayMessages = history.DisplayMessages.Select(message => message.WireBody.Value),
                    systemHistory = history.SystemHistory.Select(message => message.WireBody.Value),
                    effectiveSystemMessage = history.EffectiveSystemMessage?.WireBody.Value,
                    effectiveSystemPrompt = history.SystemState.Prompt,
                    effectiveTools = history.SystemState.Tools.Select(tool => tool.Value),
                    modelMessages = history.Context.LlmMessages.Select(message => message.WireBody.Value),
                    model = history.Context.Model, thinkingLevel = history.Context.ThinkingLevel,
                    nativeDiagnostics = NativeSessionDiagnosticProjector.ToWire(NativeSessionDiagnosticProjector.Project(history.Context,token)).Value,
                    sessionStatistics = Statistics(history.SessionStatistics), branchStatistics = Statistics(history.BranchStatistics),
                    readOnly = true, networkUsed = false
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })), 0, null);
            }
        }
        var children = entries.ToLookup(entry => entry.ParentId ?? "", StringComparer.Ordinal);
        var nodes = args.Command == "tree" && projection is not null ?
            entries.Select(entry => new { entry.Id, entry.ParentId, entry.Type,
                children = children[entry.Id].Select(child => child.Id).ToArray() }).ToArray() : null;
        var report = JsonData.Parse(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, type = "session_command_result", command = args.Command,
            status = complete ? "completed" : "recovery_required",
            resumeEligibility = complete ? "requires_explicit_runtime_binding_and_authorization" : "recovery_required",
            sessionPath = args.Session, sourceComplete = read.SourceComplete, readStatus = read.Status.ToString(),
            sourceByteLength = read.OriginalBytes.Length, sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(read.OriginalBytes.ToArray())),
            validatedPrefixByteLength = read.ValidatedPrefixByteLength,
            diagnostics = read.Diagnostics.Select(diagnostic => new { code = diagnostic.Code.ToString(), diagnostic.LineNumber, diagnostic.ByteOffset, diagnostic.ByteLength }),
            header = read.Header?.WireBody.Value, opaqueHeader = read.OpaqueHeader?.Value,
            selectedLeafId = projection?.LeafId, physicalLeafId = entries.LastOrDefault()?.Id,
            roots = projection?.RootIds, nodes,
            selectedEntries = args.Command == "inspect" ? projection?.Ancestry.Select(entry => entry.WireBody.Value).ToArray() : null,
            messages = args.Command == "inspect" ? projection?.Messages.Select(message => message.WireBody.Value).ToArray() : null,
            llmMessages = args.Command == "inspect" ? projection?.LlmMessages.Select(message => message.WireBody.Value).ToArray() : null,
            model = projection?.Model, thinkingLevel = projection?.ThinkingLevel,
            nativeDiagnostics = projection is null ? (JsonElement?)null : NativeSessionDiagnosticProjector.ToWire(NativeSessionDiagnosticProjector.Project(projection,token)).Value,
            readOnly = true, networkUsed = false
        }));
        if (historyFailure is not null)
            report = JsonData.Parse(report.ToString()[..^1] + ",\"historyProjectionFailure\":" + JsonSerializer.Serialize(historyFailure) + "}");
        return (report, complete ? 0 : 1, null);
    }

    internal static async Task<ImmutableArray<JsonData>> ScriptAsync(string path, CancellationToken token)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (source.Length > 1_048_576) throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
        using var collected = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var count = await source.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (count > 1_048_576 - collected.Length) throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
            collected.Write(buffer, 0, count);
        }
        try
        {
            var script = JsonData.Parse(Utf8.GetString(collected.ToArray())).Value;
            CheckScriptJson(script, 0, token);
            if (script.ValueKind != JsonValueKind.Object || script.GetProperty("schemaVersion").GetInt32() != 1)
                throw new SessionCommandException(SessionCommandFailure.InvalidScript);
            var turns = script.GetProperty("turns");
            if (turns.ValueKind != JsonValueKind.Array || turns.GetArrayLength() is < 1 or > 64)
                throw new SessionCommandException(SessionCommandFailure.InvalidScript);
            var result = ImmutableArray.CreateBuilder<JsonData>(); var eventCount = 0;
            foreach (var turn in turns.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (turn.ValueKind != JsonValueKind.Object) throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                var events = turn.GetProperty("events");
                if (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() is < 1 or > 256)
                    throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                foreach (var observation in events.EnumerateArray())
                    if (++eventCount > 4096 || observation.ValueKind != JsonValueKind.Object)
                        throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                if (turn.TryGetProperty("requiredInputTexts", out var requirements))
                {
                    if (requirements.ValueKind != JsonValueKind.Array || requirements.GetArrayLength() > 128)
                        throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                    foreach (var text in requirements.EnumerateArray())
                        if (text.ValueKind != JsonValueKind.String || text.GetString()!.Length > 4096 || !Unicode(text.GetString()!))
                            throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                }
                result.Add(JsonData.FromElement(turn));
            }
            return result.ToImmutable();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or DecoderFallbackException or FormatException)
        { throw new SessionCommandException(SessionCommandFailure.InvalidScript); }
    }

    private static async Task WriteAsync(TextWriter writer, JsonData report)
    {
        var text = report.ToString();
        if (text.Length > 1_048_576 || Utf8.GetByteCount(text) > 1_048_576)
            throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
        await writer.WriteAsync(text + "\n").ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }
    private static void CheckScriptJson(JsonElement value, int depth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (++depth > 32) throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var property in value.EnumerateObject())
                {
                    if (!Unicode(property.Name)) throw new SessionCommandException(SessionCommandFailure.InvalidScript);
                    CheckScriptJson(property.Value, depth, token);
                }
            else foreach (var child in value.EnumerateArray()) CheckScriptJson(child, depth, token);
        }
        else if (value.ValueKind == JsonValueKind.String && !Unicode(value.GetString()!))
            throw new SessionCommandException(SessionCommandFailure.InvalidScript);
    }
    private static bool Unicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index]))
            { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        return true;
    }
}
