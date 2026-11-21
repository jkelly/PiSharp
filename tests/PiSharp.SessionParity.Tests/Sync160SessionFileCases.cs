// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/src/core/session-manager.ts (buildSessionInfo,
// listSessionsFromDir, mapWithConcurrency, SessionManager.list, SessionManager.listAll) and
// packages/coding-agent/test/session-manager/file-operations.test.ts ("SessionManager listing abort").
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Pi;
using PiSharp.Sessions.Lifecycle;

// Pi 1.6.0 sync, #11956: pi exited with an uncaught AbortError when the session picker cancelled loading after the listing had
// skipped a .jsonl file that is not a session, and such files stayed open. Upstream stopped handing the abort signal to the file
// stream: the listing checks the signal itself and destroys every stream when its read stops. The cases hold the native listing
// (PiSessions, and the header catalog of PiSharp.Sessions) to that contract: a skipped file is closed at once, and a cancelled listing
// ends with OperationCanceledException at its caller and nothing else. Authored from the pinned sources; never captured.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync160SessionFileCases() =>
    [
        Case("sync160.session-list-cancelled-after-skipping-a-non-session-file-does-not-crash", SessionListAbortAfterSkip),
        Case("sync160.session-list-closes-a-skipped-non-session-file", SessionListClosesSkippedFile),
        Case("sync160.session-list-cancelled-between-files-throws-only-cancellation", SessionListCancelledBetweenFiles),
        Case("sync160.session-list-cancelled-while-reading-a-file-throws-cancellation-and-closes-it", SessionListCancelledWhileReading),
        Case("sync160.session-catalog-cancelled-after-skipping-a-non-session-file-closes-every-file", SessionCatalogCancelledAfterSkip),
    ];

    /// <summary>The upstream fixture: a first line that is not a session header, then more than one 64 KB stream chunk, so the file
    /// is still being read when the listing skips it.</summary>
    private static string Sync160WriteNonSession(string directory, string name = "other.jsonl")
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "{\"type\":\"other\"}\n" + new string('x', 1024 * 1024) + "\n", new UTF8Encoding(false));
        return path;
    }

    private static string Sync160WriteSession(string directory, string name, string id, int messages = 1)
    {
        var path = Path.Combine(directory, name);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { NewLine = "\n" };
        writer.WriteLine(JsonSerializer.Serialize(new { type = "session", version = 3, id, timestamp = "2026-01-01T00:00:00.000Z", cwd = directory }));
        for (var index = 0; index < messages; index++)
            writer.WriteLine(JsonSerializer.Serialize(new { type = "message", id = "m" + index, parentId = index == 0 ? null : "m" + (index - 1), timestamp = "2026-01-01T00:00:01.000Z",
                message = new { role = "user", content = "hello " + index, timestamp = 1_767_225_601_000L + index } }));
        return path;
    }

    /// <summary>True when nothing holds the file: it opens for exclusive access (on Windows an open reader denies it).</summary>
    private static bool Sync160IsClosed(string path)
    {
        try { using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return true; }
        catch (IOException) { return false; }
    }

    /// <summary>Unobserved task failures raised by the listing while <paramref name="run"/> and a collection ran (upstream collects
    /// process 'uncaughtException').</summary>
    private static async Task<List<string>> Sync160Unobserved(Func<Task> run)
    {
        GC.Collect(); GC.WaitForPendingFinalizers();
        var seen = new List<string>();
        EventHandler<UnobservedTaskExceptionEventArgs> observer = (_, args) =>
        {
            var text = args.Exception.ToString();
            if (text.Contains(nameof(PiSessions), StringComparison.Ordinal) || text.Contains(nameof(SessionCatalog), StringComparison.Ordinal)) lock (seen) seen.Add(text);
        };
        TaskScheduler.UnobservedTaskException += observer;
        try
        {
            await run();
            await Task.Delay(20);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }
        finally { TaskScheduler.UnobservedTaskException -= observer; }
        lock (seen) return [.. seen];
    }

    // "does not crash when aborted after a file that is not a session was skipped".
    private static async Task SessionListAbortAfterSkip()
    {
        var directory = Temp("sync160-list-abort");
        try
        {
            Sync160WriteNonSession(directory);
            using var controller = new CancellationTokenSource();
            var unobserved = await Sync160Unobserved(() =>
            {
                Equal(0, PiSessions.ListAll(directory, Path.Combine(directory, "agent"), controller.Token).Length, "the file that is not a session is skipped");
                // The session picker cancels its loads when it closes, after files were skipped.
                controller.Cancel();
                return Task.CompletedTask;
            });
            Equal(0, unobserved.Count, "nothing fails after the cancellation: " + string.Join("\n", unobserved));
            // A listing started with the cancelled signal ends as a cancellation (listAll: abortSignal.throwIfAborted()).
            Throws<OperationCanceledException>(() => PiSessions.ListAll(directory, Path.Combine(directory, "agent"), controller.Token), "listAll with an aborted signal");
            Throws<OperationCanceledException>(() => PiSessions.List(directory, directory, Path.Combine(directory, "agent"), controller.Token), "list with an aborted signal");
        }
        finally { Directory.Delete(directory, true); }
    }

    // "fixed such files staying open": the read of a skipped file stops at its first entry, and the file is closed there.
    private static void SessionListClosesSkippedFile()
    {
        var directory = Temp("sync160-list-closes");
        try
        {
            var other = Sync160WriteNonSession(directory);
            var empty = Path.Combine(directory, "empty.jsonl"); File.WriteAllText(empty, "\n\nnot json\n");
            var session = Sync160WriteSession(directory, "2026-01-01T00-00-00-000Z_a.jsonl", "a");
            Check(PiSessions.BuildSessionInfo(other) is null, "a first entry that is not a session header is skipped");
            Check(Sync160IsClosed(other), "the skipped file is closed when buildSessionInfo returns");
            var listed = PiSessions.ListAll(directory, Path.Combine(directory, "agent"));
            Equal("a", listed.Single().Id, "the session beside the skipped files is listed");
            foreach (var file in new[] { other, empty, session }) Check(Sync160IsClosed(file), "closed after the listing: " + Path.GetFileName(file));
            // Nothing holds the files: they can be deleted at once (an open reader would keep the name in use on Windows).
            File.Delete(other); File.Delete(empty); File.Delete(session);
            Equal(0, Directory.GetFiles(directory, "*.jsonl").Length, "the listed files are gone");
        }
        finally { Directory.Delete(directory, true); }
    }

    // mapWithConcurrency: signal.throwIfAborted() before each file. The skip returns normally; the cancellation that follows it is
    // what the next file's load reports, as OperationCanceledException and not as a skipped file or an I/O failure.
    private static void SessionListCancelledBetweenFiles()
    {
        var directory = Temp("sync160-list-between");
        try
        {
            var other = Sync160WriteNonSession(directory);
            var session = Sync160WriteSession(directory, "2026-01-01T00-00-00-000Z_b.jsonl", "b");
            var missing = Path.Combine(directory, "missing.jsonl");
            using var controller = new CancellationTokenSource();
            Check(PiSessions.BuildSessionInfo(other, controller.Token) is null, "skipped before the cancellation");
            controller.Cancel();
            var error = Throws<OperationCanceledException>(() => PiSessions.BuildSessionInfo(session, controller.Token), "the next file after the cancellation");
            Equal(controller.Token, error.CancellationToken, "the cancellation is the caller's");
            // buildSessionInfo's catch: signal.throwIfAborted() before a failed read becomes "not a session".
            Throws<OperationCanceledException>(() => PiSessions.BuildSessionInfo(missing, controller.Token), "an unreadable file after the cancellation");
            Check(PiSessions.BuildSessionInfo(missing) is null, "an unreadable file is skipped without a cancellation");
            Check(Sync160IsClosed(other) && Sync160IsClosed(session), "both files are closed");
            Equal("b", PiSessions.BuildSessionInfo(session)!.Id, "the session still loads without the cancelled signal");
        }
        finally { Directory.Delete(directory, true); }
    }

    // buildSessionInfo: signal.throwIfAborted() for every line, and the stream destroyed in finally. The session file is far larger
    // than can be read before the cancellation fires, so the listing is cancelled while it reads.
    private static async Task SessionListCancelledWhileReading()
    {
        var directory = Temp("sync160-list-reading");
        try
        {
            Sync160WriteNonSession(directory, "0-other.jsonl");
            var large = Sync160WriteSession(directory, "2026-01-01T00-00-00-000Z_c.jsonl", "c", messages: 200_000);
            using var controller = new CancellationTokenSource();
            Exception? failure = null;
            var unobserved = await Sync160Unobserved(() =>
            {
                controller.CancelAfter(TimeSpan.FromMilliseconds(15));
                try { PiSessions.ListAll(directory, Path.Combine(directory, "agent"), controller.Token); }
                catch (Exception error) { failure = error; }
                return Task.CompletedTask;
            });
            Check(failure is OperationCanceledException cancelled && cancelled.CancellationToken == controller.Token,
                "the cancelled listing throws OperationCanceledException to its caller: " + (failure?.ToString() ?? "it completed"));
            Equal(0, unobserved.Count, "nothing else fails: " + string.Join("\n", unobserved));
            Check(Sync160IsClosed(large) && Sync160IsClosed(Path.Combine(directory, "0-other.jsonl")), "the file being read and the skipped file are closed");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>The local files, recording every opened stream and cancelling once the first one (the skipped file) is closed.</summary>
    private sealed class Sync160CatalogFiles(CancellationTokenSource controller) : ISessionCatalogFileSystem
    {
        internal List<(string Path, Sync160TrackedStream Stream)> Opened { get; } = [];
        public bool IsDirectoryLink(string directory) => false;
        public IEnumerable<string> EnumerateFileNames(string directory) =>
            Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal);
        public SessionCatalogFileMetadata GetMetadata(string path) { var info = new FileInfo(path); return new(info.Length, info.LastWriteTimeUtc.Ticks, false); }
        public ValueTask<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new Sync160TrackedStream(path, Opened.Count == 0 ? controller.Cancel : null);
            Opened.Add((path, stream));
            return ValueTask.FromResult<Stream>(stream);
        }
    }

    private sealed class Sync160TrackedStream(string path, Action? closed)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 512, FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        internal bool Closed { get; private set; }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); if (!Closed) { Closed = true; closed?.Invoke(); } }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing && !Closed) { Closed = true; closed?.Invoke(); } }
    }

    // The native header catalog (PiSharp.Sessions) has the same contract: ReadHeaderAsync closes its stream in finally, also for a
    // first line that is not a session header, and a cancellation after that skip is rethrown as the caller's cancellation.
    private static async Task SessionCatalogCancelledAfterSkip()
    {
        var directory = Temp("sync160-catalog");
        try
        {
            var other = Sync160WriteNonSession(directory, "0-other.jsonl");
            var session = Sync160WriteSession(directory, "1-session.jsonl", "d");
            var baseline = await new SessionCatalog([new SessionCatalogStore("store", directory)]).ListAsync(new());
            Equal(1, baseline.Items.Length, "the session is listed");
            Equal(1, baseline.SkippedFiles, "the file that is not a session is skipped");
            Check(Sync160IsClosed(other) && Sync160IsClosed(session), "the catalog closed both files");

            using var controller = new CancellationTokenSource();
            var files = new Sync160CatalogFiles(controller);
            var catalog = new SessionCatalog([new SessionCatalogStore("store", directory)], fileSystem: files);
            Exception? failure = null;
            var unobserved = await Sync160Unobserved(async () =>
            {
                try { await catalog.ListAsync(new(), controller.Token); }
                catch (Exception error) { failure = error; }
            });
            Check(failure is OperationCanceledException, "the cancelled catalog listing throws OperationCanceledException: " + (failure?.ToString() ?? "it completed"));
            Equal(0, unobserved.Count, "nothing else fails: " + string.Join("\n", unobserved));
            Equal("0-other.jsonl", Path.GetFileName(files.Opened.Single().Path), "the listing stopped after the skipped file");
            Check(files.Opened.All(item => item.Stream.Closed), "every opened stream was closed");
            Check(Sync160IsClosed(other) && Sync160IsClosed(session), "nothing holds the files");
        }
        finally { Directory.Delete(directory, true); }
    }
}
