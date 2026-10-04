using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.CodingAgent;
using PiSharp.Sessions.Compaction;
using PiSharp.Sessions.Lifecycle;

namespace PiSharp.Cli.Commands;

/// <summary>One explicit offline summary operation through actual provider transport and durable checkpoint.</summary>
public static class SessionSummaryCommand
{
    public const string Usage = "session compact|branch-summary --session <absolute JSONL> --workspace <existing directory> " +
        "--offline-script <absolute authored provider JSON> [--offline-api openai-responses|anthropic-messages|openai-completions] " +
        "[--leaf <id>|--root] [--keep-recent <tokens>] [--reserve <tokens>] [--focus <text>] " +
        "[--first-kept <id>|--retain-none] [--automatic --context-window <tokens>]; branch-summary requires --target <id|root>";
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        var acknowledged = false; var writeMayStart = false; var outputStarted = false;
        try
        {
            if (args is not ["session", "compact" or "branch-summary", ..] || args.Length is < 8 or > 29) throw Invalid();
            var values = new Dictionary<string, string>(StringComparer.Ordinal); var flags = new HashSet<string>(StringComparer.Ordinal);
            var allowed = new HashSet<string>(["--session", "--workspace", "--offline-script", "--offline-api", "--leaf", "--keep-recent", "--reserve", "--focus", "--first-kept", "--context-window", "--target"], StringComparer.Ordinal);
            for (var i = 2; i < args.Length; i++)
                if (args[i] is "--root" or "--retain-none" or "--automatic") { if (!flags.Add(args[i])) throw Invalid(); }
                else if (!allowed.Contains(args[i]) || i + 1 >= args.Length || !values.TryAdd(args[i], args[++i])) throw Invalid();
            string Required(string name) => values.TryGetValue(name, out var value) ? value : throw Invalid();
            double Number(string name, double fallback) => !values.TryGetValue(name, out var value) ? fallback :
                value.Length <= 128 && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) &&
                double.IsFinite(result) && result >= 0 && result <= 1_000_000_000 ? result : throw Invalid();
            var sessionPath = SessionCommands.Absolute(Required("--session")); var workspace = SessionCommands.Absolute(Required("--workspace"));
            var scriptPath = SessionCommands.Absolute(Required("--offline-script"));
            if (!Directory.Exists(workspace)) throw new SessionCommandException(SessionCommandFailure.WorkspaceMissing);
            if (flags.Contains("--root") && values.ContainsKey("--leaf") || flags.Contains("--retain-none") && values.ContainsKey("--first-kept") ||
                args[1] == "compact" && values.ContainsKey("--target") || args[1] == "branch-summary" &&
                (flags.Contains("--retain-none") || flags.Contains("--automatic") || values.ContainsKey("--first-kept"))) throw Invalid();
            var api = values.GetValueOrDefault("--offline-api") ?? "openai-responses"; _ = OfflineSessionProfile.SelectModel(api);
            var latest = !flags.Contains("--root") && !values.ContainsKey("--leaf"); var leaf = values.GetValueOrDefault("--leaf");
            var focus = values.GetValueOrDefault("--focus"); if (focus?.Length > 65_536) throw Invalid();
            var summaryOptions = new SessionSummaryRequestOptions(CustomInstructions: focus);
            var reserve = Number("--reserve", 16_384); var keep = Number("--keep-recent", 20_000); var window = Number("--context-window", 128_000);
            var target = args[1] == "branch-summary" ? Required("--target") : null;
            if (target is { Length: 0 } || leaf is { Length: 0 } || values.GetValueOrDefault("--first-kept") is { Length: 0 }) throw Invalid();
            var turns = await SessionCommands.ScriptAsync(scriptPath, cancellationToken).ConfigureAwait(false);
            string? report = null;
            await using (var profile = await OfflineSessionProfile.CreateAsync(workspace, sessionPath, scriptPath,
                turns, [], [], cancellationToken, offlineApi: api).ConfigureAwait(false))
            {
                var options = new PersistentAgentSessionOptions(UseLatestLeaf: latest, SelectedLeafId: leaf,
                    AgentOptions: new(Loop: new(MaximumTurns: 64, MaximumTranscriptMessages: 1024)),
                    SessionLogStoreOptions: new(ReaderOptions: new(MaximumInputBytes: 8_388_608, MaximumLines: 10_000, MaximumRecords: 10_000)));
                long Clock() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); string NextId() => "cli-summary-" + Guid.NewGuid().ToString("N");
                var lifecycle = new PersistentSessionLifecycle(profile.Registry, Clock, NextId, options);
                await using var session = await lifecycle.OpenAsync(new(sessionPath, latest, leaf), profile.SelectedModel, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(SessionCommands.Absolute(session.WorkingDirectory), profile.Workspace,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new SessionCommandException(SessionCommandFailure.WorkspaceMismatch);
                profile.AttachOwner(session, options, Clock, NextId, lifecycle: lifecycle); var attachment = profile.Sessions!.Current;
                string Report(string? entryId, bool committed) => JsonSerializer.Serialize(new
                { schemaVersion = 1, status = committed ? "committed" : "skipped", checkpointAcknowledged = committed,
                    sessionFile = sessionPath, sessionId = attachment.Session.Snapshot.Log.Header.Id,
                    generation = attachment.Generation, entryId, providerRequests = profile.UsedTurns });
                ValueTask Preflight(SessionSummaryCheckpointPreview prospective, CancellationToken token)
                {
                    token.ThrowIfCancellationRequested(); report = Report(prospective.Entry.Id, true);
                    if (Encoding.UTF8.GetByteCount(report) > 1_048_576) throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
                    writeMayStart = true; return ValueTask.CompletedTask;
                }
                var receipt = args[1] == "compact"
                    ? await profile.Sessions.CompactAsync(attachment, new(new(ReserveTokens: reserve, KeepRecentTokens: keep),
                        flags.Contains("--automatic"), window, summaryOptions, OverrideRetainedBoundary: flags.Contains("--retain-none") || values.ContainsKey("--first-kept"),
                        FirstKeptEntryId: values.GetValueOrDefault("--first-kept")), profile.SummaryGenerator, cancellationToken, Preflight).ConfigureAwait(false)
                    : await profile.Sessions.SummarizeBranchAsync(attachment, new(target == "root" ? null : target, window, reserve, summaryOptions),
                        profile.SummaryGenerator, cancellationToken, Preflight).ConfigureAwait(false);
                acknowledged = receipt?.Append.CheckpointAcknowledged == true;
                report ??= Report(null, false);
                await profile.Sessions.DisposeAsync().ConfigureAwait(false);
            }
            outputStarted = true; await stdout.WriteLineAsync(report).ConfigureAwait(false); await stdout.FlushAsync().ConfigureAwait(false); return 0;
        }
        catch (Exception error)
        {
            var code = error switch { SessionCommandException command => command.Failure.ToString(), SessionCompactionException summary => summary.Failure.ToString(),
                PersistentAgentSessionException session => session.Fault.Failure.ToString(), OperationCanceledException => "Cancelled", _ => acknowledged && outputStarted ? "OutputFailed" : "SummaryFailed" };
            await stderr.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", code,
                checkpointAcknowledged = acknowledged, commitMayHaveOccurred = acknowledged || writeMayStart,
                message = acknowledged || writeMayStart ? "Inspect the session checkpoint before retrying." : "Summary operation failed; owned work has settled." })).ConfigureAwait(false);
            await stderr.FlushAsync().ConfigureAwait(false);
            return error is SessionCommandException { Failure: SessionCommandFailure.InvalidArguments or SessionCommandFailure.InvalidPath } ? 2 : 1;
        }
    }
    private static SessionCommandException Invalid() => new(SessionCommandFailure.InvalidArguments);
}
