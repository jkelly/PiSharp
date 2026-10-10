using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiSharp.Sessions.Context;
using PiSharp.Sessions.Serialization;
using PiSharp.Sessions.Storage;
using PiSharp.Sessions.Tree;

internal static class SessionBranchPublisherTests
{
    private static readonly SessionEntryCodec Codec = new();
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private const string Time = "2026-10-02T12:00:00.000Z";
    private const string PrivateCause = "private branch I/O payload";
    private static readonly byte[] CompetitorBytes = Encoding.UTF8.GetBytes("existing competitor bytes\n");

    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("session branch publishes exact durable local bytes before returning an owned receipt", DurableLocalContent),
        ("session new branch publishes a fresh header and refuses an existing destination", FreshAndNoOverwrite),
        ("session branch rollback shares awaited deletion and closes attachment authority", ReceiptRollbackJoins),
        ("session branch committed attachment retains its published file on shared disposal", CommitAttachmentRetains),
        ("session branch forged bytes metadata and invalid graphs fail before file effects", ForgedPlans),
        ("session branch invalid paths and precancellation have no file effects", Admission),
        ("session branch exact reader codec and graph limits admit only complete plans", ExactLimits),
        ("session branch held flush cancellation joins output close and owned temp deletion", HeldFlushCancellation),
        ("session branch late cancellation shields actual publication and returns cleanup authority", ShieldedPublication),
        ("session branch pre-move failure remains uncertain and retains an unproven final file", PublishBeforeMoveFailure),
        ("session branch post-move failure remains uncertain and retains actual published bytes", PublishAfterMoveFailure),
        ("session branch destination race uses actual nonoverwriting local move", CollisionDuringMove),
        ("session branch create write flush disk flush and close faults are sanitized", StageFailures),
        ("session branch preparation cleanup failures preserve the primary failure and uncertainty", PreparationCleanupFailures),
        ("session branch rollback cleanup faults are sanitized shared and never retried", RollbackCleanupFailures)
    ];

    private static async Task DurableLocalContent()
    {
        using var files = new Files(); var plan = ForkPlan(files); var expected = plan.JsonlBytes.ToArray();
        var io = new FaultFiles(files);
        var receipt = await new SessionBranchPublisher(fileSystem: io).PrepareAsync(plan, files.Destination);
        try
        {
            Equal(files.Destination, receipt.Path); Equal(expected.Length, receipt.ByteLength); Equal(Hash(expected), receipt.Sha256);
            Check(ReferenceEquals(plan, receipt.Plan), "Receipt changed the immutable plan identity.");
            Bytes(expected, await File.ReadAllBytesAsync(receipt.Path));
            var read = await new SessionLogReader().ReadFileAsync(receipt.Path);
            Equal(SessionLogReadStatus.Complete, read.Status); Check(read.SourceComplete, "Published content was not complete.");
            Equal(plan.Entries.Length + 1, read.ValidatedPrefix.Length);
            Equal("1.00e400", read.ValidatedPrefix[1].Entry.WireBody.Value.GetProperty("data").GetProperty("number").GetRawText());
            Equal("tail", read.ValidatedPrefix[^1].Entry.Id);
            Check(io.Events.SequenceEqual(new[] { "create", "write", "flush", "disk", "close", "publish" }),
                "Publication skipped or reordered the actual disk flush and closed-file move.");
            Equal(1, io.PublishCalls); Equal(0, io.DeleteCalls); NoTemps(files);
        }
        finally { await receipt.DisposeAsync(); }
        Check(!File.Exists(files.Destination), "Unattached receipt retained its owned final file.");
        Equal(1, io.DeleteCalls); Equal(files.Destination, io.DeletedPaths.Single());
    }

    private static async Task FreshAndNoOverwrite()
    {
        using var files = new Files(); var plan = new SessionBranchPlanner().New("fresh", Time, files.Root);
        await using var receipt = await new SessionBranchPublisher().PrepareAsync(plan, files.Destination);
        var bytes = await File.ReadAllBytesAsync(files.Destination); Bytes(plan.JsonlBytes.ToArray(), bytes);
        var read = await new SessionLogReader().ReadFileAsync(files.Destination);
        Equal(1, read.ValidatedPrefix.Length); Equal("fresh", read.Header!.Id);
        Check(plan.Entries.IsEmpty && plan.LeafId is null && !read.Header.WireBody.Value.TryGetProperty("parentSession", out _),
            "Fresh publication inherited a parent or historical entries.");
        receipt.CommitAttachment();
        var io = new FaultFiles(files);
        await Fails(() => new SessionBranchPublisher(fileSystem: io).PrepareAsync(ForkPlan(files), files.Destination),
            SessionBranchPublishFailure.InvalidRequest);
        NoEffects(io); Bytes(bytes, await File.ReadAllBytesAsync(files.Destination)); NoTemps(files);
    }

    private static async Task ReceiptRollbackJoins()
    {
        using var files = new Files(); var entered = Gate(); var release = Gate();
        var io = new FaultFiles(files) { DeleteEntered = entered, DeleteRelease = release };
        var receipt = await new SessionBranchPublisher(fileSystem: io).PrepareAsync(ForkPlan(files), files.Destination);
        var first = receipt.DisposeAsync().AsTask(); var second = receipt.DisposeAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(Bound);
            Check(ReferenceEquals(first, second), "Concurrent receipt closes did not share one deletion task.");
            Check(!first.IsCompleted && !second.IsCompleted && File.Exists(files.Destination), "Receipt close returned before held deletion.");
            Throws<InvalidOperationException>(receipt.CommitAttachment);
            release.TrySetResult(); await Task.WhenAll(first, second).WaitAsync(Bound);
            Check(!File.Exists(files.Destination), "Settled rollback left its known owned final file.");
            await receipt.DisposeAsync(); Equal(1, io.DeleteCalls); NoTemps(files);
            Throws<InvalidOperationException>(receipt.CommitAttachment);
        }
        finally { release.TrySetResult(); await Drain(first); }
    }

    private static async Task CommitAttachmentRetains()
    {
        using var files = new Files(); var io = new FaultFiles(files); var plan = ForkPlan(files);
        var receipt = await new SessionBranchPublisher(fileSystem: io).PrepareAsync(plan, files.Destination);
        receipt.CommitAttachment(); receipt.CommitAttachment();
        var first = receipt.DisposeAsync().AsTask(); var second = receipt.DisposeAsync().AsTask();
        Check(ReferenceEquals(first, second), "Attached receipt did not share close completion.");
        await Task.WhenAll(first, second); Equal(0, io.DeleteCalls);
        Bytes(plan.JsonlBytes.ToArray(), await File.ReadAllBytesAsync(files.Destination));
        Throws<InvalidOperationException>(receipt.CommitAttachment); NoTemps(files);
    }

    private static async Task ForgedPlans()
    {
        using var files = new Files(); var valid = ForkPlan(files);
        var alternateHeader = new SessionBranchPlanner().New("forged", Time, files.Root).Header;
        var invalid = new[]
        {
            valid with { Header = null! }, valid with { Header = alternateHeader },
            valid with { Entries = default }, valid with { Entries = [] },
            valid with { Entries = valid.Entries.SetItem(0, null!) },
            valid with { Entries = valid.Entries.SetItem(0, Entry("different")) },
            valid with { LeafId = null }, valid with { LeafId = "absent" },
            valid with { JsonlBytes = default }, valid with { JsonlBytes = [] },
            valid with { JsonlBytes = valid.JsonlBytes.RemoveAt(valid.JsonlBytes.Length - 1) },
            valid with { JsonlBytes = ImmutableArray.Create((byte)0xff, (byte)'\n') },
            valid with { JsonlBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid.JsonlBytes.AsSpan())
                .Replace("\"version\":3", "\"version\":4", StringComparison.Ordinal)).ToImmutableArray() },
            WirePlan(valid.Header, [Entry("duplicate"), Entry("duplicate")]),
            WirePlan(valid.Header, [Entry("orphan", "absent")]),
            WirePlan(valid.Header, [Entry("one", "two"), Entry("two", "one")]),
            WirePlan(valid.Header, [Entry("self", "self")])
        };
        foreach (var plan in invalid)
        {
            var io = new FaultFiles(files);
            await Fails(() => new SessionBranchPublisher(fileSystem: io).PrepareAsync(plan, files.Destination),
                SessionBranchPublishFailure.InvalidPlan);
            NoEffects(io); Check(!File.Exists(files.Destination), "Invalid plan produced a final file."); NoTemps(files);
        }
        var nullIo = new FaultFiles(files);
        await ThrowsAsync<ArgumentNullException>(() => new SessionBranchPublisher(fileSystem: nullIo).PrepareAsync(null!, files.Destination));
        NoEffects(nullIo);
    }

    private static async Task Admission()
    {
        using var files = new Files(); var plan = ForkPlan(files);
        var paths = new[]
        {
            "", "relative.jsonl", Path.Combine(files.Root, "inner", "..", "destination.jsonl"),
            "\\\\server\\share\\destination.jsonl", "//server/share/destination.jsonl",
            Path.Combine(files.Root, new string('x', 4097)), Path.Combine(files.Root, "\ud800.jsonl"),
            Path.Combine(files.Root, "\udfff.jsonl"), files.Root, files.File("missing/destination.jsonl")
        };
        foreach (var path in paths)
        {
            var io = new FaultFiles(files);
            await Fails(() => new SessionBranchPublisher(fileSystem: io).PrepareAsync(plan, path), SessionBranchPublishFailure.InvalidRequest);
            NoEffects(io); NoTemps(files);
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var canceledIo = new FaultFiles(files);
        var error = await ThrowsAsync<OperationCanceledException>(() =>
            new SessionBranchPublisher(fileSystem: canceledIo).PrepareAsync(plan, files.Destination, canceled.Token));
        Equal(canceled.Token, error.CancellationToken); NoEffects(canceledIo);
        Check(!Directory.EnumerateFileSystemEntries(files.Root).Any(), "Admission created file or directory artifacts.");
    }

    private static async Task ExactLimits()
    {
        using var files = new Files(); var plan = ForkPlan(files);
        var records = new[] { plan.Header }.Concat(plan.Entries).Select(entry => Codec.Serialize(entry)).ToArray();
        var lineBytes = records.Max(Encoding.UTF8.GetByteCount); var characters = records.Max(record => record.Length);
        var graphCharacters = plan.Entries.Sum(entry => entry.WireBody.Value.GetRawText().Length);
        var reader = new SessionLogReaderOptions(MaximumInputBytes: plan.JsonlBytes.Length, MaximumLineBytes: lineBytes,
            MaximumLines: records.Length, MaximumRecords: records.Length, ReadBufferBytes: 1,
            CodecOptions: new(MaximumRecordCharacters: characters, MaximumUtf8Bytes: lineBytes, MaximumJsonDepth: 3));
        var graph = new SessionContextProjectionOptions(MaximumEntries: plan.Entries.Length, MaximumInputCharacters: graphCharacters);
        var io = new FaultFiles(files);
        await using (var receipt = await new SessionBranchPublisher(new(reader, graph), io).PrepareAsync(plan, files.Destination))
            Bytes(plan.JsonlBytes.ToArray(), await File.ReadAllBytesAsync(receipt.Path));
        Check(!File.Exists(files.Destination), "Exact-budget receipt did not roll back.");
        foreach (var options in new[]
        {
            new SessionBranchPublishOptions(reader with { MaximumInputBytes = reader.MaximumInputBytes - 1 }, graph),
            new SessionBranchPublishOptions(reader with { MaximumLineBytes = lineBytes - 1 }, graph),
            new SessionBranchPublishOptions(reader with { MaximumLines = records.Length - 1 }, graph),
            new SessionBranchPublishOptions(reader with { MaximumRecords = records.Length - 1 }, graph),
            new SessionBranchPublishOptions(reader with { CodecOptions = reader.CodecOptions! with { MaximumRecordCharacters = characters - 1 } }, graph),
            new SessionBranchPublishOptions(reader with { CodecOptions = reader.CodecOptions! with { MaximumUtf8Bytes = lineBytes - 1 } }, graph),
            new SessionBranchPublishOptions(reader with { CodecOptions = reader.CodecOptions! with { MaximumJsonDepth = 2 } }, graph),
            new SessionBranchPublishOptions(reader, graph with { MaximumEntries = plan.Entries.Length - 1 }),
            new SessionBranchPublishOptions(reader, graph with { MaximumInputCharacters = graphCharacters - 1 })
        })
        {
            var rejectedIo = new FaultFiles(files);
            await Fails(() => new SessionBranchPublisher(options, rejectedIo).PrepareAsync(plan, files.Destination), SessionBranchPublishFailure.InvalidPlan);
            NoEffects(rejectedIo); Check(!File.Exists(files.Destination), "Budget rejection published a file."); NoTemps(files);
        }
        _ = new SessionBranchPublisher(new(new(MaximumInputBytes: 536_870_888)));
        Throws<ArgumentOutOfRangeException>(() => new SessionBranchPublisher(new(new(MaximumInputBytes: 536_870_889))));
        Throws<ArgumentOutOfRangeException>(() => new SessionBranchPublisher(new(new(ReadBufferBytes: 0))));
        Throws<ArgumentOutOfRangeException>(() => new SessionBranchPublisher(new(GraphOptions: new(MaximumEntries: 0))));
    }

    private static async Task HeldFlushCancellation()
    {
        using var files = new Files(); using var caller = new CancellationTokenSource();
        var flushEntered = Gate(); var flushRelease = Gate(); var closeEntered = Gate(); var closeRelease = Gate();
        var deleteEntered = Gate(); var deleteRelease = Gate();
        var io = new FaultFiles(files)
        {
            FlushEntered = flushEntered, FlushRelease = flushRelease, CloseEntered = closeEntered, CloseRelease = closeRelease,
            DeleteEntered = deleteEntered, DeleteRelease = deleteRelease
        };
        var prepare = new SessionBranchPublisher(fileSystem: io).PrepareAsync(ForkPlan(files), files.Destination, caller.Token);
        try
        {
            await flushEntered.Task.WaitAsync(Bound); caller.Cancel(); await closeEntered.Task.WaitAsync(Bound);
            Check(!prepare.IsCompleted && File.Exists(io.TemporaryPath), "Cancellation returned before held output close.");
            Equal(0, io.DiskFlushCalls); Equal(0, io.PublishCalls); Equal(0, io.DeleteCalls);
            closeRelease.TrySetResult(); await deleteEntered.Task.WaitAsync(Bound);
            Check(!prepare.IsCompleted && File.Exists(io.TemporaryPath), "Cancellation returned before held temp deletion.");
            deleteRelease.TrySetResult();
            var error = await ThrowsAsync<SessionBranchPublishCanceledException>(async () => await prepare.WaitAsync(Bound));
            Equal(caller.Token, error.CancellationToken); Check(!error.TemporaryMayRemain && error.CleanupFailures.IsEmpty, "Settled canceled cleanup reported a leftover.");
            Sanitized(error); Equal(1, io.CloseCalls); Equal(1, io.DeleteCalls); Equal(0, io.PublishCalls);
            Check(!File.Exists(files.Destination), "Held flush cancellation published a destination."); NoTemps(files);
        }
        finally
        {
            caller.Cancel(); flushRelease.TrySetResult(); closeRelease.TrySetResult(); deleteRelease.TrySetResult(); await Drain(prepare);
        }
    }

    private static async Task ShieldedPublication()
    {
        foreach (var cancelAfterMove in new[] { false, true })
        {
            using var files = new Files(); using var caller = new CancellationTokenSource();
            var entered = Gate(); var release = Gate(); var plan = ForkPlan(files);
            var io = new FaultFiles(files)
            {
                PublishEntered = cancelAfterMove ? null : entered, PublishRelease = cancelAfterMove ? null : release,
                CancelAfterMove = cancelAfterMove ? caller : null
            };
            var prepare = new SessionBranchPublisher(fileSystem: io).PrepareAsync(plan, files.Destination, caller.Token);
            PublishedSessionBranch? receipt = null;
            try
            {
                if (!cancelAfterMove)
                {
                    await entered.Task.WaitAsync(Bound); caller.Cancel();
                    Check(!prepare.IsCompleted && !File.Exists(files.Destination) && File.Exists(io.TemporaryPath), "Held publication did not shield cancellation.");
                    Equal(1, io.DiskFlushCalls); Equal(1, io.CloseCalls); release.TrySetResult();
                }
                receipt = await prepare.WaitAsync(Bound);
                Check(caller.IsCancellationRequested, "Late-cancellation fixture did not cancel.");
                Equal(Hash(plan.JsonlBytes.ToArray()), receipt.Sha256); Bytes(plan.JsonlBytes.ToArray(), await File.ReadAllBytesAsync(receipt.Path));
                Equal(0, io.DeleteCalls); NoTemps(files);
                await receipt.DisposeAsync(); Equal(1, io.DeleteCalls);
                Check(!File.Exists(files.Destination), "Late cancellation lost final-file rollback authority.");
            }
            finally
            {
                release.TrySetResult(); await Drain(prepare); if (receipt is not null) await receipt.DisposeAsync();
            }
        }
    }

    private static async Task PublishBeforeMoveFailure()
    {
        using var files = new Files(); var io = new FaultFiles(files) { PublishFailure = MoveFailure.Before, CreateCompetitor = true };
        var error = await Fails(() => new SessionBranchPublisher(fileSystem: io).PrepareAsync(ForkPlan(files), files.Destination),
            SessionBranchPublishFailure.PublishFailed, SessionBranchPublication.Uncertain);
        Check(!error.TemporaryMayRemain && error.CleanupFailures.IsEmpty, "Pre-move owned-temp cleanup did not settle.");
        Bytes(CompetitorBytes, await File.ReadAllBytesAsync(files.Destination));
        Check(io.DeletedPaths.SequenceEqual(new[] { io.TemporaryPath! }), "Uncertain publisher deleted an unproven final file."); NoTemps(files);
    }

    private static async Task PublishAfterMoveFailure()
    {
        using var files = new Files(); var plan = ForkPlan(files); var io = new FaultFiles(files) { PublishFailure = MoveFailure.After };
        var error = await Fails(() => new SessionBranchPublisher(fileSystem: io).PrepareAsync(plan, files.Destination),
            SessionBranchPublishFailure.PublishFailed, SessionBranchPublication.Uncertain);
        Check(!error.TemporaryMayRemain && error.CleanupFailures.IsEmpty, "Post-move absent-temp cleanup did not settle.");
        Bytes(plan.JsonlBytes.ToArray(), await File.ReadAllBytesAsync(files.Destination));
        Check(io.DeletedPaths.SequenceEqual(new[] { io.TemporaryPath! }), "Post-move failure guessed final-file deletion authority."); NoTemps(files);
    }

    private static async Task CollisionDuringMove()
    {
        using var files = new Files(); var io = new FaultFiles(files) { CreateCompetitor = true };
        var error = await Fails(() => new SessionBranchPublisher(fileSystem: io).PrepareAsync(ForkPlan(files), files.Destination),
            SessionBranchPublishFailure.PublishFailed, SessionBranchPublication.Uncertain);
        Check(!error.TemporaryMayRemain, "Collision cleanup left the owned temporary file.");
        Equal(1, io.PublishCalls); Bytes(CompetitorBytes, await File.ReadAllBytesAsync(files.Destination));
        Check(io.DeletedPaths.All(path => path != files.Destination), "Collision deleted the winning destination."); NoTemps(files);
    }

    private static async Task StageFailures()
    {
        foreach (var (fault, closeFault, expected) in new[]
        {
            (OutputFailure.Create, false, SessionBranchPublishFailure.CreateFailed),
            (OutputFailure.PartialWrite, false, SessionBranchPublishFailure.WriteFailed),
            (OutputFailure.ForeignCancellation, false, SessionBranchPublishFailure.WriteFailed),
            (OutputFailure.Flush, false, SessionBranchPublishFailure.FlushFailed),
            (OutputFailure.DiskFlush, false, SessionBranchPublishFailure.FlushFailed),
            (OutputFailure.None, true, SessionBranchPublishFailure.CleanupFailed)
        })
        {
            using var files = new Files(); var io = new FaultFiles(files) { OutputFault = fault, CloseFailure = closeFault };
            var error = await Fails(() => new SessionBranchPublisher(fileSystem: io).PrepareAsync(ForkPlan(files), files.Destination), expected);
            Equal(fault == OutputFailure.Create, error.TemporaryMayRemain);
            Check(error.CleanupFailures.IsEmpty, "Single-stage fixture produced an unexpected secondary cleanup fault.");
            Equal(0, io.PublishCalls); Equal(fault == OutputFailure.Create ? 0 : 1, io.CloseCalls);
            Equal(fault == OutputFailure.Create ? 0 : 1, io.DeleteCalls);
            Check(!File.Exists(files.Destination), "Prepublication stage fault produced a final file."); NoTemps(files);
        }
    }

    private static async Task PreparationCleanupFailures()
    {
        foreach (var deleteAfterEffect in new[] { false, true })
        {
            using var files = new Files(); var io = new FaultFiles(files)
            {
                OutputFault = OutputFailure.DiskFlush, CloseFailure = true,
                DeleteFault = deleteAfterEffect ? DeleteFailure.After : DeleteFailure.Before
            };
            var error = await Fails(() => new SessionBranchPublisher(fileSystem: io).PrepareAsync(ForkPlan(files), files.Destination),
                SessionBranchPublishFailure.FlushFailed);
            Check(error.TemporaryMayRemain && error.CleanupFailures.SequenceEqual(new[]
                { SessionBranchPublishFailure.CleanupFailed, SessionBranchPublishFailure.CleanupFailed }),
                "Cleanup faults replaced the primary disk-flush failure or lost conservative temp disposition.");
            Equal(1, io.CloseCalls); Equal(1, io.DeleteCalls); Equal(0, io.PublishCalls);
            Equal(!deleteAfterEffect, File.Exists(io.TemporaryPath)); Check(!File.Exists(files.Destination), "Cleanup failure produced a final file.");
        }
    }

    private static async Task RollbackCleanupFailures()
    {
        foreach (var deleteAfterEffect in new[] { false, true })
        {
            using var files = new Files(); var plan = ForkPlan(files);
            var io = new FaultFiles(files) { DeleteFault = deleteAfterEffect ? DeleteFailure.After : DeleteFailure.Before };
            var receipt = await new SessionBranchPublisher(fileSystem: io).PrepareAsync(plan, files.Destination);
            var first = receipt.DisposeAsync().AsTask(); var second = receipt.DisposeAsync().AsTask();
            Check(ReferenceEquals(first, second), "Failed receipt closes did not share completion.");
            var error = await Fails(() => first, SessionBranchPublishFailure.CleanupFailed, SessionBranchPublication.PublishedUnattached);
            await Fails(() => second, SessionBranchPublishFailure.CleanupFailed, SessionBranchPublication.PublishedUnattached);
            await Fails(() => receipt.DisposeAsync().AsTask(), SessionBranchPublishFailure.CleanupFailed, SessionBranchPublication.PublishedUnattached);
            Check(!error.TemporaryMayRemain && error.CleanupFailures.SequenceEqual(new[] { SessionBranchPublishFailure.CleanupFailed }),
                "Rollback cleanup fault reported temporary ownership or omitted its cleanup disposition.");
            Equal(1, io.DeleteCalls); Equal(!deleteAfterEffect, File.Exists(files.Destination));
            if (!deleteAfterEffect) Bytes(plan.JsonlBytes.ToArray(), await File.ReadAllBytesAsync(files.Destination));
            Throws<InvalidOperationException>(receipt.CommitAttachment); NoTemps(files);
        }
    }

    private static SessionEntry Entry(string id, string? parent = null) => Codec.Parse(
        "{\"type\":\"custom\",\"id\":" + JsonSerializer.Serialize(id) + ",\"parentId\":" + JsonSerializer.Serialize(parent) +
        ",\"timestamp\":\"" + Time + "\",\"customType\":\"state\",\"data\":{\"number\":1.00e400,\"text\":\"π🙂\",\"nested\":{\"nil\":null}}}");
    private static SessionBranchPlan ForkPlan(Files files)
    {
        var header = new SessionBranchPlanner().New("source", Time, files.Root).Header;
        return new SessionBranchPlanner().Fork(new(header, [Entry("root"), Entry("tail", "root")], "tail",
            SessionForkPosition.At, "fresh", Time, "source.jsonl"));
    }
    private static SessionBranchPlan WirePlan(SessionEntry header, ImmutableArray<SessionEntry> entries) => new(header, entries,
        entries.IsEmpty ? null : entries[^1].Id, null, null, false,
        Encoding.UTF8.GetBytes(string.Join("\n", new[] { header }.Concat(entries).Select(Codec.Serialize)) + "\n").ToImmutableArray());
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void NoTemps(Files files) => Check(!Directory.EnumerateFiles(files.Root, ".pisharp-branch-*.tmp").Any(), "Owned branch temporary file remained.");
    private static void NoEffects(FaultFiles io) => Check(io.CreateCalls == 0 && io.WriteCalls == 0 && io.FlushCalls == 0 &&
        io.DiskFlushCalls == 0 && io.CloseCalls == 0 && io.PublishCalls == 0 && io.DeleteCalls == 0, "Admission called the I/O seam.");
    private static void Bytes(byte[] expected, byte[] actual) => Check(expected.AsSpan().SequenceEqual(actual), "Actual local file bytes changed.");
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, observed {actual}.");
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Sanitized(Exception error) => Check(error.InnerException is null &&
        !error.ToString().Contains(PrivateCause, StringComparison.Ordinal), "Diagnostic exposed the trusted I/O cause.");
    private static async Task<SessionBranchPublishException> Fails(Func<Task> run, SessionBranchPublishFailure failure,
        SessionBranchPublication publication = SessionBranchPublication.NotAttempted)
    {
        var error = await ThrowsAsync<SessionBranchPublishException>(run); Equal(failure, error.Failure); Equal(publication, error.Publication);
        Sanitized(error); return error;
    }
    private static async Task<T> ThrowsAsync<T>(Func<Task> run) where T : Exception
    { try { await run().WaitAsync(Bound); } catch (T error) { return error; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }
    private static void Throws<T>(Action run) where T : Exception
    { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name + "."); }
    private static async Task Drain(Task task)
    { try { await task.WaitAsync(Bound); } catch (Exception) when (task.IsCompleted) { } }

    private sealed class Files : IDisposable
    {
        private const string Prefix = "pisharp-branch-tests-";
        private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private readonly string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        public string Root { get; }
        public string Destination => File("destination.jsonl");
        public Files()
        {
            Root = Path.GetFullPath(Path.Combine(temporaryRoot, Prefix + Guid.NewGuid().ToString("N")));
            ValidateRoot(); Directory.CreateDirectory(Root);
        }
        public string File(string name) { var path = Path.GetFullPath(Path.Combine(Root, name)); ValidateOwnedPath(path); return path; }
        public void ValidateOwnedPath(string path)
        {
            Check(Path.IsPathFullyQualified(path) && string.Equals(path, Path.GetFullPath(path), Comparison), "Fixture path is not an absolute canonical path.");
            var relative = Path.GetRelativePath(Root, path);
            Check(!Path.IsPathRooted(relative) && relative != "." && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, Comparison) && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, Comparison),
                "Fixture path escaped its owned temporary root.");
        }
        private void ValidateRoot()
        {
            Check(Path.IsPathFullyQualified(Root) && string.Equals(Root, Path.GetFullPath(Root), Comparison) &&
                string.Equals(Path.GetDirectoryName(Root), temporaryRoot, Comparison), "Fixture root escaped the absolute temporary directory.");
            var name = Path.GetFileName(Root);
            Check(name.StartsWith(Prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[Prefix.Length..], "N", out _), "Fixture root has no owned prefix and ID.");
        }
        public void Dispose()
        {
            ValidateRoot(); if (!Directory.Exists(Root)) return;
            Check((System.IO.File.GetAttributes(Root) & FileAttributes.ReparsePoint) == 0, "Fixture root became a reparse point.");
            foreach (var path in Directory.EnumerateFileSystemEntries(Root))
            {
                ValidateOwnedPath(path); var attributes = System.IO.File.GetAttributes(path);
                Check((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0, "Fixture cleanup encountered an unowned directory or reparse point.");
                System.IO.File.Delete(path);
            }
            Directory.Delete(Root, recursive: false);
        }
    }

    private enum OutputFailure { None, Create, PartialWrite, ForeignCancellation, Flush, DiskFlush }
    private enum MoveFailure { None, Before, After }
    private enum DeleteFailure { None, Before, After }
    private sealed class FaultFiles(Files owned) : ISessionBranchFileSystem
    {
        public OutputFailure OutputFault { get; init; }
        public MoveFailure PublishFailure { get; init; }
        public DeleteFailure DeleteFault { get; init; }
        public bool CloseFailure { get; init; }
        public bool CreateCompetitor { get; init; }
        public CancellationTokenSource? CancelAfterMove { get; init; }
        public TaskCompletionSource? FlushEntered { get; init; }
        public TaskCompletionSource? FlushRelease { get; init; }
        public TaskCompletionSource? CloseEntered { get; init; }
        public TaskCompletionSource? CloseRelease { get; init; }
        public TaskCompletionSource? PublishEntered { get; init; }
        public TaskCompletionSource? PublishRelease { get; init; }
        public TaskCompletionSource? DeleteEntered { get; init; }
        public TaskCompletionSource? DeleteRelease { get; init; }
        public int CreateCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int FlushCalls { get; private set; }
        public int DiskFlushCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int PublishCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public string? TemporaryPath { get; private set; }
        public List<string> DeletedPaths { get; } = [];
        public List<string> Events { get; } = [];
        public async ValueTask<ISessionBranchOutput> CreateNewTemporaryAsync(string path)
        {
            owned.ValidateOwnedPath(path);
            Check(string.Equals(Path.GetDirectoryName(path), owned.Root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                Path.GetFileName(path).StartsWith(".pisharp-branch-", StringComparison.Ordinal) && path.EndsWith(".tmp", StringComparison.Ordinal), "Publisher did not stage an owned sibling temporary file.");
            CreateCalls++; Events.Add("create"); TemporaryPath = path;
            if (OutputFault == OutputFailure.Create) throw new IOException(PrivateCause);
            return new Output(await SessionBranchPublisher.LocalFileSystem.CreateNewTemporaryAsync(path), this);
        }
        public async ValueTask PublishNewAsync(string temporaryPath, string destinationPath)
        {
            owned.ValidateOwnedPath(temporaryPath); owned.ValidateOwnedPath(destinationPath); Equal(TemporaryPath, temporaryPath);
            PublishCalls++; Events.Add("publish"); PublishEntered?.TrySetResult();
            if (PublishRelease is not null) await PublishRelease.Task.WaitAsync(Bound);
            if (CreateCompetitor)
            {
                await using var competitor = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await competitor.WriteAsync(CompetitorBytes); competitor.Flush(flushToDisk: true);
            }
            if (PublishFailure == MoveFailure.Before) throw new IOException(PrivateCause);
            await SessionBranchPublisher.LocalFileSystem.PublishNewAsync(temporaryPath, destinationPath);
            CancelAfterMove?.Cancel();
            if (PublishFailure == MoveFailure.After) throw new IOException(PrivateCause);
        }
        public async ValueTask DeleteOwnedAsync(string path)
        {
            owned.ValidateOwnedPath(path); Check(path == TemporaryPath || path == owned.Destination, "Publisher deleted a file outside its staged/final authority.");
            DeleteCalls++; DeletedPaths.Add(path); DeleteEntered?.TrySetResult();
            if (DeleteRelease is not null) await DeleteRelease.Task.WaitAsync(Bound);
            if (DeleteFault == DeleteFailure.Before) throw new IOException(PrivateCause);
            await SessionBranchPublisher.LocalFileSystem.DeleteOwnedAsync(path);
            if (DeleteFault == DeleteFailure.After) throw new IOException(PrivateCause);
        }
        private sealed class Output(ISessionBranchOutput inner, FaultFiles owner) : ISessionBranchOutput
        {
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
            {
                owner.WriteCalls++; owner.Events.Add("write");
                if (owner.OutputFault == OutputFailure.ForeignCancellation) throw new OperationCanceledException(PrivateCause);
                if (owner.OutputFault == OutputFailure.PartialWrite)
                { await inner.WriteAsync(bytes[..Math.Min(5, bytes.Length)], token); throw new IOException(PrivateCause); }
                await inner.WriteAsync(bytes, token);
            }
            public async ValueTask FlushAsync(CancellationToken token)
            {
                owner.FlushCalls++; owner.Events.Add("flush"); await inner.FlushAsync(token); owner.FlushEntered?.TrySetResult();
                if (owner.FlushRelease is not null) await owner.FlushRelease.Task.WaitAsync(Bound, token);
                if (owner.OutputFault == OutputFailure.Flush) throw new IOException(PrivateCause);
            }
            public void FlushToDisk()
            {
                owner.DiskFlushCalls++; owner.Events.Add("disk");
                if (owner.OutputFault == OutputFailure.DiskFlush) throw new IOException(PrivateCause);
                inner.FlushToDisk();
            }
            public async ValueTask DisposeAsync()
            {
                owner.CloseCalls++; owner.Events.Add("close"); await inner.DisposeAsync(); owner.CloseEntered?.TrySetResult();
                if (owner.CloseRelease is not null) await owner.CloseRelease.Task.WaitAsync(Bound);
                if (owner.CloseFailure) throw new IOException(PrivateCause);
            }
        }
    }
}
