using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.CodingAgent;
using PiSharp.Contracts;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Lifecycle;
using PiSharp.Sessions.Storage;

namespace PiSharp.Cli.Commands;

public sealed record SessionContextEditCommandOptions(int MaximumReplacementBytes = 1_048_576,
    int MaximumOutputBytes = 1_048_576);

/// <summary>One explicit native context edit through the shared durable manager, with no provider request.</summary>
public static class SessionContextEditCommand
{
    public const string Usage = "session context-edit --session <absolute JSONL> --workspace <existing absolute directory> " +
        "--target <active entry id> --replacement <absolute JSON file; null omits content> " +
        "[--leaf <id>|--root] [--offline-api openai-responses|anthropic-messages|openai-completions]";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record Arguments(string Session, string Workspace, string Target, string Replacement,
        bool Latest, string? Leaf, string OfflineApi);

    /// <param name="replacementFiles">Trusted read-only file acquisition seam, never selected by serialized input.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken = default, SessionContextEditCommandOptions? options = null,
        Func<string, CancellationToken, ValueTask<Stream>>? replacementFiles = null,
        PiSharp.Cli.Mcp.McpProfileRuntimeAdmission? mcpAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(stdout); ArgumentNullException.ThrowIfNull(stderr);
        var limits = options ?? new();
        if (limits.MaximumReplacementBytes is < 1 or > 1_048_576 || limits.MaximumOutputBytes is < 256 or > 1_048_576)
            throw new ArgumentOutOfRangeException(nameof(options));
        var acknowledged = false; var writeMayStart = false; var outputStarted = false;
        try
        {
            var parsed = Parse(args);
            if (!Directory.Exists(parsed.Workspace)) throw new SessionCommandException(SessionCommandFailure.WorkspaceMissing);
            var replacement = await ReadReplacementAsync(parsed.Replacement, limits.MaximumReplacementBytes,
                replacementFiles, cancellationToken).ConfigureAwait(false);
            string? report = null;
            var profile = await OfflineSessionProfile.CreateAsync(parsed.Workspace, parsed.Session, null,
                ImmutableArray<JsonData>.Empty, [], [], cancellationToken, offlineApi: parsed.OfflineApi, mcpAdmission: mcpAdmission).ConfigureAwait(false);
            PersistentAgentSession? session = null; Exception? operationFailure = null;
            try
            {
                var sessionOptions = new PersistentAgentSessionOptions(UseLatestLeaf: parsed.Latest, SelectedLeafId: parsed.Leaf,
                    AgentOptions: new(Loop: new(MaximumTurns: 64, MaximumTranscriptMessages: 1024)),
                    SessionLogStoreOptions: new(ReaderOptions: new(MaximumInputBytes: 8_388_608, MaximumLines: 10_000, MaximumRecords: 10_000)));
                long sequence = 0; var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long Clock() => start + Interlocked.Increment(ref sequence);
                string NextId() => "cli-edit-" + Guid.NewGuid().ToString("N");
                var lifecycle = profile.CreateLifecycle(Clock, NextId, sessionOptions);
                session = await lifecycle.OpenAsync(new(parsed.Session, parsed.Latest, parsed.Leaf),
                    profile.SelectedModel, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(SessionCommands.Absolute(session.WorkingDirectory), profile.Workspace,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new SessionCommandException(SessionCommandFailure.WorkspaceMismatch);
                await profile.AttachOwnerAsync(session, sessionOptions, Clock, NextId, lifecycle: lifecycle).ConfigureAwait(false);
                var attachment = profile.Sessions!.Current;
                var receipt = await profile.Sessions.AppendContextEditAsync(attachment, new(parsed.Target, replacement),
                    cancellationToken, (prospective, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        report = JsonSerializer.Serialize(new { schemaVersion = 1, status = "committed", checkpointAcknowledged = true,
                            sessionFile = parsed.Session, sessionId = prospective.PreviousLog.Header.Id, generation = attachment.Generation,
                            entryId = prospective.Entry.Id, leafId = prospective.Entry.Id, targetId = parsed.Target, providerRequests = 0 });
                        if (Encoding.UTF8.GetByteCount(report) > limits.MaximumOutputBytes)
                            throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
                        writeMayStart = true;
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);
                acknowledged = receipt.Append.CheckpointAcknowledged;
                if (!acknowledged || report is null || profile.Requests.Length != 0)
                    throw new SessionCommandException(SessionCommandFailure.CommandFailed);
            }
            catch (Exception error) { operationFailure = error; }
            // Attached owner retires its catalog before writer/profile settlement, including failure paths.
            finally { await profile.SettleOwnedCommandAsync(session, operationFailure).ConfigureAwait(false); }
            outputStarted = true;
            await stdout.WriteLineAsync(report).ConfigureAwait(false);
            await stdout.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception error)
        {
            var code = error switch
            {
                SessionContextEditException edit => edit.Failure.ToString(),
                SessionCommandException command => command.Failure.ToString(),
                PersistentAgentSessionException persistent => persistent.Fault.Failure.ToString(),
                OperationCanceledException => "Canceled",
                JsonException or DecoderFallbackException => "InvalidReplacement",
                _ => acknowledged && outputStarted ? "OutputFailed" : "ContextEditFailed"
            };
            var uncertain = !acknowledged && writeMayStart;
            var message = acknowledged ? "Context edit checkpoint was acknowledged; inspect the session before retrying." :
                uncertain ? "Context edit may have committed; inspect the session before retrying." :
                "Context edit failed before an acknowledged write; owned work has settled.";
            await stderr.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, status = "failed", code,
                checkpointAcknowledged = acknowledged, commitMayHaveOccurred = acknowledged || uncertain, message })).ConfigureAwait(false);
            await stderr.FlushAsync().ConfigureAwait(false);
            return error is SessionCommandException { Failure: SessionCommandFailure.InvalidArguments or SessionCommandFailure.InvalidPath } ? 2 : 1;
        }
    }

    private static Arguments Parse(string[] args)
    {
        if (args is not ["session", "context-edit", ..] || args.Length is < 10 or > 17)
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        var values = new Dictionary<string, string>(StringComparer.Ordinal); var root = false;
        for (var index = 2; index < args.Length; index++)
        {
            var flag = args[index];
            if (flag == "--root") { if (root) throw new SessionCommandException(SessionCommandFailure.InvalidArguments); root = true; continue; }
            if (flag is not ("--session" or "--workspace" or "--target" or "--replacement" or "--leaf" or "--offline-api") ||
                ++index >= args.Length || !values.TryAdd(flag, args[index]))
                throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        }
        foreach (var required in new[] { "--session", "--workspace", "--target", "--replacement" })
            if (!values.ContainsKey(required)) throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        values.TryGetValue("--leaf", out var leaf);
        var target = values["--target"];
        if (target.Length is < 1 or > 4096 || root && leaf is not null || leaf is { Length: 0 or > 4096 })
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        var api = values.GetValueOrDefault("--offline-api") ?? "openai-responses";
        if (api is not ("openai-responses" or "anthropic-messages" or "openai-completions"))
            throw new SessionCommandException(SessionCommandFailure.InvalidArguments);
        return new(SessionCommands.Absolute(values["--session"]), SessionCommands.Absolute(values["--workspace"]), target,
            SessionCommands.Absolute(values["--replacement"]), !root && leaf is null, leaf, api);
    }

    private static async Task<JsonData> ReadReplacementAsync(string path, int maximum,
        Func<string, CancellationToken, ValueTask<Stream>>? files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await using var input = files is null ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan) : await files(path, token).ConfigureAwait(false);
        using var retained = new MemoryStream(); var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum - retained.Length + 1)), token).ConfigureAwait(false);
            if (read == 0) break;
            if (read > maximum - retained.Length) throw new SessionCommandException(SessionCommandFailure.ResourceLimit);
            retained.Write(buffer, 0, read);
        }
        token.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(Utf8.GetString(retained.ToArray()), new JsonDocumentOptions { MaxDepth = 32 });
        return JsonData.FromElement(document.RootElement);
    }
}
