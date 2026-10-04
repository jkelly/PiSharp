using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using PiSharp.Sessions.Import;
using PiSharp.Sessions.Storage;
using PiSharp.CodingAgent;

namespace PiSharp.Cli.Commands;

/// <summary>Explicit-path PiSharp copy commands. No default home, identifier generator or provider authority.</summary>
public static class SessionCopyCommand
{
    public const string Usage = "session copy-inspect --source <absolute JSONL> [--id-plan <absolute JSON string-array file>]; " +
        "session copy --source <absolute JSONL> --destination <new absolute JSONL> --format native-exact|current-jsonl|native-archive-exact " +
        "[--id-plan <absolute JSON string-array file>]; session migrate --source <absolute JSONL> --destination <new absolute JSONL> " +
        "[--id-plan <absolute JSON string-array file>]";
    private const int PlanByteLimit = 1_048_576, RecordLimit = 10_000, IdentifierCharacterLimit = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SessionCopyOptions Bounds = new(new SessionLogReaderOptions(
        MaximumInputBytes: 8_388_608, MaximumLines: RecordLimit, MaximumRecords: RecordLimit), MaximumOutputBytes: 8_388_608);
    private sealed record Arguments(string Command, string Source, string? Destination, SessionCopyFormat? Format, string? Plan);
    private sealed class AdmissionException(string code, bool cleanupFailed = false, bool canceled = false) : Exception
    {
        public string Code { get; } = code;
        public bool CleanupFailed { get; } = cleanupFailed;
        public bool Canceled { get; } = canceled;
    }

    /// <param name="service">Trusted native service seam; never selected by serialized input.</param>
    /// <param name="idPlanFiles">Trusted read-only plan acquisition seam. Only OpenReadAsync is called.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr,
        CancellationToken cancellationToken = default, SessionCopyService? service = null, ISessionCopyFileSystem? idPlanFiles = null)
    {
        ArgumentNullException.ThrowIfNull(stdout); ArgumentNullException.ThrowIfNull(stderr);
        var publication = SessionCopyPublication.NotAttempted;
        var knownCleanupFailures = ImmutableArray<SessionCopyFailure>.Empty;
        try
        {
            var parsed = Parse(args);
            cancellationToken.ThrowIfCancellationRequested();
            var ids = parsed.Plan is null ? ImmutableArray<string>.Empty :
                await ReadPlanAsync(parsed.Plan, idPlanFiles ?? SessionCopyService.LocalFileSystem, cancellationToken).ConfigureAwait(false);
            var copier = service ?? new SessionCopyService(Bounds);
            var readOnly = service is null ? new SessionLifecycleReadOnly(new(CopyOptions: Bounds)) : null;
            SessionCopyResult? result = null;
            SessionCopyInspection inspected;
            if (parsed.Command == "copy-inspect")
                inspected = readOnly is null ? await copier.InspectAsync(parsed.Source, ids, cancellationToken).ConfigureAwait(false) :
                    (await readOnly.InspectAsync(parsed.Source, v1EntryIds: ids, cancellationToken: cancellationToken).ConfigureAwait(false)).CopyInspection;
            else
            {
                var request = new SessionCopyRequest(parsed.Source, parsed.Destination!, parsed.Format!.Value, ids);
                result = readOnly is null ? await copier.CopyAsync(request, cancellationToken).ConfigureAwait(false) :
                    parsed.Command == "migrate" ? await readOnly.ImportAsync(request, cancellationToken).ConfigureAwait(false) :
                    await readOnly.ExportAsync(request, cancellationToken).ConfigureAwait(false);
                inspected = result.Inspection;
                if (result.Published) publication = SessionCopyPublication.Published;
                if (result.Status == SessionCopyStatus.PublishedWithCleanupFailure) knownCleanupFailures = [SessionCopyFailure.CleanupFailed];
            }
            var status = result?.Status switch
            {
                SessionCopyStatus.Published => "published",
                SessionCopyStatus.PublishedWithCleanupFailure => "published_with_cleanup_failure",
                _ => inspected.CanPublishCurrent && result is null ? "inspected" : "blocked"
            };
            // A known publication survives late cancellation. Output delivery is awaited without abandoning a write.
            await WriteAsync(stdout, Report(parsed, inspected, result, status)).ConfigureAwait(false);
            return status is "inspected" or "published" ? 0 : 1;
        }
        catch (AdmissionException error)
        {
            var code = error.Canceled ? "Canceled" : error.Code;
            await FailAsync(stderr, code, publication, false, [], error.CleanupFailed).ConfigureAwait(false);
            return error.Canceled || code is "IdPlanReadFailed" or "IdPlanCleanupFailed" ? 1 : 2;
        }
        catch (SessionCopyCanceledException error)
        {
            await FailAsync(stderr, "Canceled", publication, error.TemporaryMayRemain, error.CleanupFailures, false).ConfigureAwait(false);
            return 1;
        }
        catch (SessionCopyException error)
        {
            await FailAsync(stderr, error.Failure.ToString(), error.Publication, error.TemporaryMayRemain, error.CleanupFailures, false).ConfigureAwait(false);
            return 1;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && publication == SessionCopyPublication.NotAttempted)
        {
            await FailAsync(stderr, "Canceled", publication, false, [], false).ConfigureAwait(false);
            return 1;
        }
        catch (Exception)
        {
            await FailAsync(stderr, "CommandFailed", publication, false, knownCleanupFailures, false).ConfigureAwait(false);
            return 1;
        }
    }

    private static Arguments Parse(string[] args)
    {
        if (args is null || args.Length is < 4 or > 10 || args[0] != "session" ||
            args[1] is not ("copy-inspect" or "copy" or "migrate")) throw new AdmissionException("InvalidArguments");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 2; index < args.Length; index += 2)
        {
            var key = args[index];
            if (key is not ("--source" or "--destination" or "--format" or "--id-plan") || index + 1 >= args.Length ||
                args[index + 1] is null || !values.TryAdd(key, args[index + 1])) throw new AdmissionException("InvalidArguments");
        }
        if (!values.TryGetValue("--source", out var source)) throw new AdmissionException("InvalidArguments");
        values.TryGetValue("--destination", out var destination); values.TryGetValue("--format", out var format);
        values.TryGetValue("--id-plan", out var plan);
        var command = args[1];
        if (command == "copy-inspect" && (destination is not null || format is not null) ||
            command != "copy-inspect" && destination is null || command == "migrate" && format is not null)
            throw new AdmissionException("InvalidArguments");
        SessionCopyFormat? selected = command switch
        {
            "copy-inspect" => null,
            "migrate" => SessionCopyFormat.CompatibleCurrentJsonl,
            _ => format switch
            {
                "native-exact" => SessionCopyFormat.NativeExact,
                "current-jsonl" => SessionCopyFormat.CompatibleCurrentJsonl,
                "native-archive-exact" => SessionCopyFormat.NativeArchiveExact,
                _ => throw new AdmissionException("InvalidArguments")
            }
        };
        return new(command, Absolute(source), destination is null ? null : Absolute(destination), selected,
            plan is null ? null : Absolute(plan));
    }

    private static string Absolute(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Unicode(path) || path.Any(char.IsControl) ||
                !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
                OperatingSystem.IsWindows() && path.AsSpan(2).IndexOf(':') >= 0 ||
                !string.Equals(path, Path.GetFullPath(path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                string.IsNullOrEmpty(Path.GetFileName(path))) throw new AdmissionException("InvalidPath");
            return path;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { throw new AdmissionException("InvalidPath"); }
    }

    private static async Task<ImmutableArray<string>> ReadPlanAsync(string path, ISessionCopyFileSystem files, CancellationToken token)
    {
        Stream? source = null; Exception? failure = null; var cleanupFailed = false;
        var ids = ImmutableArray<string>.Empty;
        try
        {
            token.ThrowIfCancellationRequested();
            source = await files.OpenReadAsync(path, token).ConfigureAwait(false);
            if (source is null || !source.CanRead) throw new IOException();
            using var captured = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var count = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, PlanByteLimit + 1 - (int)captured.Length)), token).ConfigureAwait(false);
                if (count == 0) break;
                if (count > PlanByteLimit - captured.Length) throw new AdmissionException("ResourceLimit");
                captured.Write(buffer, 0, count);
            }
            token.ThrowIfCancellationRequested();
            var json = Utf8.GetString(captured.GetBuffer(), 0, (int)captured.Length);
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new AdmissionException("InvalidIdPlan");
            if (document.RootElement.GetArrayLength() > RecordLimit) throw new AdmissionException("ResourceLimit");
            var builder = ImmutableArray.CreateBuilder<string>(); var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                if (item.ValueKind != JsonValueKind.String) throw new AdmissionException("InvalidIdPlan");
                var id = item.GetString()!;
                if (id.Length > IdentifierCharacterLimit) throw new AdmissionException("ResourceLimit");
                if (id.Length == 0 || !Unicode(id) || id.Any(char.IsControl) || !unique.Add(id)) throw new AdmissionException("InvalidIdPlan");
                builder.Add(id);
            }
            ids = builder.ToImmutable();
        }
        catch (Exception error) { failure = error; }
        if (source is not null)
            try { await source.DisposeAsync().ConfigureAwait(false); } catch (Exception) { cleanupFailed = true; }
        if (failure is not null)
        {
            if (failure is OperationCanceledException && token.IsCancellationRequested)
                throw new AdmissionException("Canceled", cleanupFailed, canceled: true);
            var code = failure is AdmissionException admitted ? admitted.Code :
                failure is JsonException or DecoderFallbackException or InvalidOperationException or ArgumentException ? "InvalidIdPlan" : "IdPlanReadFailed";
            throw new AdmissionException(code, cleanupFailed);
        }
        if (cleanupFailed) throw new AdmissionException("IdPlanCleanupFailed", cleanupFailed: true);
        if (token.IsCancellationRequested) throw new AdmissionException("Canceled", canceled: true);
        return ids;
    }

    private static string Report(Arguments args, SessionCopyInspection inspection, SessionCopyResult? result, string status)
    {
        var migration = inspection.Migration;
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1, type = "session_copy_command_result", status, command = args.Command,
            capability = "pisharp_explicit_session_copy", upstreamCliParity = false, piReaderInteroperabilityVerified = false,
            sourcePath = args.Source, destinationPath = args.Destination, idPlanPath = args.Plan,
            format = args.Format is null ? null : Format(args.Format.Value),
            migrationAppliedToOutput = result?.Published == true ?
                (bool?)(result.Format == SessionCopyFormat.CompatibleCurrentJsonl && inspection.Migration?.WasMigrated == true) : null,
            publication = result?.Published == true ? "published" : "not_attempted", sourceReadOnly = true, networkUsed = false,
            sourceVersion = inspection.SourceVersion,
            targetVersion = args.Format == SessionCopyFormat.NativeArchiveExact ? (int?)null : 3,
            sourceComplete = inspection.Log.SourceComplete,
            capturedBytes = inspection.OriginalBytes.Length, capturedSha256 = inspection.CapturedSha256, sourceSha256 = inspection.SourceSha256,
            readStatus = inspection.Log.Status.ToString(), validatedPrefixRecords = inspection.Log.ValidatedPrefix.Length,
            validatedPrefixBytes = inspection.Log.ValidatedPrefixByteLength, canPublishCurrent = inspection.CanPublishCurrent,
            currentRecordCount = inspection.CurrentRecords.Length, outputBytes = result?.OutputBytes ?? 0, outputSha256 = result?.OutputSha256,
            dataRetention = new
            {
                allJsonFieldsRetained = inspection.CanPublishCurrent, opaqueDataSemanticsVerified = false,
                omittedRecords = result?.Published == true ? (int?)result.OmittedRecords : null,
                omittedFields = result?.Published == true ? (int?)result.OmittedFields : null
            },
            exportRetention = result?.Retention is not { } retention ? null : new
            {
                exactSourceBytes = retention.ExactSourceBytes, excludedRecords = retention.ExcludedRecords,
                excludedFields = retention.ExcludedFields, inertUnknownEntryIds = retention.InertUnknownEntryIds,
                runnableCurrentFormat = retention.RunnableCurrentFormat,
                piReaderInteroperabilityGuaranteed = retention.PiReaderInteroperabilityGuaranteed
            },
            readerDiagnostics = inspection.Log.Diagnostics.Select(item => new
            { code = item.Code.ToString(), item.LineNumber, item.ByteOffset, item.ByteLength, codecFailure = item.CodecFailure?.ToString() }),
            diagnostics = (result?.Diagnostics ?? inspection.Diagnostics).Select(item => new
            { code = item.Code.ToString(), item.RecordIndex, graphFailure = item.GraphFailure?.ToString() }),
            migration = migration is null ? null : new
            {
                status = migration.Status.ToString(), migration.WasMigrated, migration.VersionDefaulted, migration.SourceVersion,
                migration.TargetVersion,
                receipts = migration.Receipts.Select(item => new
                {
                    item.RecordIndex, transform = item.Transform.ToString(), item.Field, item.BeforePresent,
                    before = item.Before?.Value, item.AfterPresent, after = item.After?.Value
                }),
                diagnostics = migration.Diagnostics.Select(item => new
                { code = item.Code.ToString(), item.RecordIndex, item.IsBlocking, codecFailure = item.CodecFailure?.ToString() })
            }
        });
    }

    private static string Format(SessionCopyFormat format) => format switch
    {
        SessionCopyFormat.NativeExact => "native-exact",
        SessionCopyFormat.CompatibleCurrentJsonl => "current-jsonl",
        SessionCopyFormat.NativeArchiveExact => "native-archive-exact",
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
    private static async Task WriteAsync(TextWriter writer, string record)
    {
        await writer.WriteAsync((record + "\n").AsMemory(), CancellationToken.None).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }
    private static async Task FailAsync(TextWriter stderr, string code, SessionCopyPublication publication,
        bool temporaryMayRemain, ImmutableArray<SessionCopyFailure> cleanupFailures, bool idPlanCleanupFailed)
    {
        try
        {
            await WriteAsync(stderr, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, type = "session_copy_command_result", status = "failed", code,
                message = "Session copy command did not complete cleanly; inspect the explicit destination and publication state before retrying.",
                publication = publication switch { SessionCopyPublication.Published => "published", SessionCopyPublication.Uncertain => "uncertain", _ => "not_attempted" },
                temporaryMayRemain, cleanupFailures = cleanupFailures.Select(item => item.ToString()), idPlanCleanupFailed
            })).ConfigureAwait(false);
        }
        catch (Exception) { /* Failed diagnostics delivery cannot turn a failed command into success. */ }
    }
    private static bool Unicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
            if (char.IsHighSurrogate(value[index])) { if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false; }
            else if (char.IsLowSurrogate(value[index])) return false;
        return true;
    }
}
