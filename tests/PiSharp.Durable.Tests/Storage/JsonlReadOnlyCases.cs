// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/durable/test/jsonl-storage.test.ts read-only cases.
// Additional native checks cover snapshot lifetime, the five-attempt limit and every mutating filesystem entry point.
using System.Text;
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;
using PiSharp.Durable.Storage.Jsonl;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> JsonlReadOnlyCases()
    {
        yield return JsonlFixtureCase("storage.jsonl.read-only.skips-repair-and-reclamation-without-writes", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var writerFs = new InstrumentedFileSystem(directory);
            var writer = await fixture.Open(directory, writerFs);
            await CreateRoot(writer);
            var live = await writer.MintIdAsync<TaskId>();
            var ended = await writer.MintIdAsync<TaskId>();
            var document = await writer.MintIdAsync<DocumentId>();
            await Commit(writer, new TaskWrite(PendingTask(live)), new TaskWrite(PendingTask(ended)),
                new DocumentCreateWrite(SessionDocument(document), DocumentContent.Base(1, JO("{\"n\":0}"))));
            // Leave reclamation due for both a terminal task and a newer current-only document base.
            writerFs.Fail(new(FailureOperation.Remove, 1, FailureMode.Before));
            await Commit(writer, new TaskWrite(TerminalTask(ended)));
            writerFs.Fail(new(FailureOperation.Write, 1, FailureMode.Before));
            await Commit(writer, new DocumentChangeWrite(document, DocumentContent.Base(1, JO("{\"n\":1}"))));
            writerFs.Clear();
            Check(File.Exists(Path.Combine(directory, $"task-{ended}.jsonl")), "terminal sidecar awaits reclamation");
            Equal(2, ReadJsonlLines(Path.Combine(directory, $"doc-{document}.jsonl")).Length, "old base awaits reclamation");
            var unconfirmed = "{\"format\":1,\"type\":\"record\",\"seq\":5,\"ordinal\":0,\"payload\":{\"type\":\"task\",\"value\":" +
                PendingTask(live, "unconfirmed").ToJson().ToJson() + "}}";
            AppendBytes(Path.Combine(directory, $"task-{live}.jsonl"), Encoding.UTF8.GetBytes(unconfirmed + "\n{\"format\":1"));
            AppendBytes(Path.Combine(directory, "main.jsonl"), Encoding.UTF8.GetBytes("{\"format\":1,\"type\":\"commit\""));
            File.WriteAllText(Path.Combine(directory, $"task-{live}.jsonl.reclaim"), "leftover");
            var before = JsonlDirectoryBytes(directory);
            var fs = new ReadOnlyAuditFileSystem(directory);
            var viewer = await fixture.Track(JsonlStorage.OpenAsync(directory, fs, JsonlContext, new() { ReadOnly = true, Fsync = true }));
            Equal<TaskRecord?>(PendingTask(live), await viewer.GetTaskAsync(live, JsonlContext), "confirmed live task");
            Equal<TaskRecord?>(TerminalTask(ended), await viewer.GetTaskAsync(ended, JsonlContext), "terminal task");
            Equal("{\"n\":1}", (await viewer.GetDocumentAsync(document, DocumentPoint.Current, JsonlContext))!.Value.ToJson(), "latest base");
            var receipts = 0;
            var error = await ThrowsAsync<JsonlStorageReadOnlyException>(() => viewer.CommitAsync(
                [[new TaskWrite(PendingTask(live, "next"))]], (_, _) => receipts++, JsonlContext), "read-only commit");
            Equal("JSONL storage is open read-only", error.Message, "read-only error");
            await ThrowsAsync<JsonlStorageReadOnlyException>(() => viewer.CommitAsync([], (_, _) => receipts++, JsonlContext), "empty read-only commit");
            Equal(0, receipts, "no receipts");
            await viewer.MintIdAsync<TaskId>();
            await viewer.CloseAsync(JsonlContext);
            Equal("", string.Join(",", fs.Mutations), "no mutating filesystem calls, including close and mint");
            Equal(before, JsonlDirectoryBytes(directory), "all file names and bytes preserved");
            var closed = await ThrowsAsync<InvalidOperationException>(() => viewer.CommitAsync([], (_, _) => { }, JsonlContext), "closed before read-only error");
            Check(closed.Message.Contains("closed", StringComparison.Ordinal), "closed-state precedence");

            await fixture.Open(directory);
            Check(before != JsonlDirectoryBytes(directory), "writable recovery repairs the fixture");
            Check(!File.Exists(Path.Combine(directory, $"task-{ended}.jsonl")), "terminal sidecar reclaimed");
            Check(!File.Exists(Path.Combine(directory, $"task-{live}.jsonl.reclaim")), "reclaim leftover removed");
            Equal(1, ReadJsonlLines(Path.Combine(directory, $"doc-{document}.jsonl")).Length, "old base reclaimed");
        });

        yield return JsonlFixtureCase("storage.jsonl.read-only.keeps-torn-only-main", async fixture =>
        {
            var directory = fixture.NewDirectory();
            File.WriteAllText(Path.Combine(directory, "main.jsonl"), "{\"format\":1,\"type\":\"commit\"", new UTF8Encoding(false));
            var before = JsonlDirectoryBytes(directory);
            var fs = new ReadOnlyAuditFileSystem(directory);
            var viewer = await fixture.Track(JsonlStorage.OpenAsync(directory, fs, JsonlContext, new() { ReadOnly = true }));
            Check(await viewer.GetConversationAsync(ConversationId.Root, JsonlContext) is null, "empty recovered snapshot");
            Equal(before, JsonlDirectoryBytes(directory), "torn main preserved");
            Equal(0, fs.Mutations.Count, "no mutation calls");
        });

        yield return JsonlFixtureCase("storage.jsonl.read-only.creates-no-missing-directory", async fixture =>
        {
            var directory = Path.Combine(fixture.NewDirectory(), "missing");
            var fs = new ReadOnlyAuditFileSystem(directory);
            var error = await ThrowsAsync<Exception>(() => JsonlStorage.OpenAsync(directory, fs, JsonlContext, new() { ReadOnly = true }), "missing directory");
            Check(error.Message.Contains("directory listing", StringComparison.Ordinal), "failure at directory listing");
            Check(!Directory.Exists(directory), "directory remains absent");
            Equal(0, fs.Mutations.Count, "no directory creation attempt");
        });

        yield return JsonlFixtureCase("storage.jsonl.read-only.local-open-holds-snapshot", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var writer = await fixture.Open(directory);
            await CreateRoot(writer);
            var task = await writer.MintIdAsync<TaskId>();
            await Commit(writer, new TaskWrite(PendingTask(task)));
            var viewer = await fixture.Track(JsonlStorage.OpenAsync(directory, JsonlContext, new() { ReadOnly = true }));
            await Commit(writer, new TaskWrite(TerminalTask(task)));
            Equal<TaskRecord?>(PendingTask(task), await viewer.GetTaskAsync(task, JsonlContext), "existing viewer stays at opening snapshot");
            var next = await fixture.Track(JsonlStorage.OpenAsync(directory, JsonlContext, new() { ReadOnly = true }));
            Equal<TaskRecord?>(TerminalTask(task), await next.GetTaskAsync(task, JsonlContext), "new viewer sees later commit");
        });

        yield return JsonlFixtureCase("storage.jsonl.read-only.retries-writer-reclamation-race", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var writer = await fixture.Open(directory);
            await CreateRoot(writer);
            var task = await writer.MintIdAsync<TaskId>();
            await Commit(writer, new TaskWrite(PendingTask(task)));
            var fs = new ReadOnlyAuditFileSystem(directory);
            var raced = false;
            fs.BeforeRead = async path =>
            {
                if (Path.GetFileName(path) != $"task-{task}.jsonl" || raced) return;
                raced = true;
                await Commit(writer, new TaskWrite(TerminalTask(task)));
            };
            var viewer = await fixture.Track(JsonlStorage.OpenAsync(directory, fs, JsonlContext, new() { ReadOnly = true }));
            Equal<TaskRecord?>(TerminalTask(task), await viewer.GetTaskAsync(task, JsonlContext), "retry sees writer's newer commit");
            Equal(2, fs.MainReads, "fresh recovery after race");
            Equal(1, fs.MainInfoReads, "one change check");
            Equal(0, fs.Mutations.Count, "viewer never writes during retry");
        });

        yield return JsonlFixtureCase("storage.jsonl.read-only.rejects-stable-corruption-without-retry", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var writer = await fixture.Open(directory);
            await CreateRoot(writer);
            var task = await writer.MintIdAsync<TaskId>();
            await Commit(writer, new TaskWrite(PendingTask(task)));
            File.WriteAllBytes(Path.Combine(directory, $"task-{task}.jsonl"), []);
            var fs = new ReadOnlyAuditFileSystem(directory);
            var error = await ThrowsAsync<JsonlCorruptionException>(() => JsonlStorage.OpenAsync(directory, fs, JsonlContext, new() { ReadOnly = true }), "confirmed record missing");
            Check(error.Message.Contains("Missing confirmed sidecar record", StringComparison.Ordinal), "corruption reported");
            Equal(1, fs.MainReads, "unchanged main is not retried");
            Equal(1, fs.MainInfoReads, "one change check");
            Equal(0, fs.Mutations.Count, "no mutation calls");
        });

        yield return JsonlFixtureCase("storage.jsonl.read-only.caps-recovery-at-five-attempts", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var writer = await fixture.Open(directory);
            await CreateRoot(writer);
            var task = await writer.MintIdAsync<TaskId>();
            await Commit(writer, new TaskWrite(PendingTask(task)));
            File.WriteAllBytes(Path.Combine(directory, $"task-{task}.jsonl"), []);
            var fs = new ReadOnlyAuditFileSystem(directory)
            {
                // Simulate a writer changing main after each read. Spaces form a torn tail, so the missing confirmed
                // sidecar remains the corruption each new attempt detects.
                AfterMainRead = path => { AppendBytes(path, [(byte)' ']); return Task.CompletedTask; },
            };
            await ThrowsAsync<JsonlCorruptionException>(() => JsonlStorage.OpenAsync(directory, fs, JsonlContext, new() { ReadOnly = true }), "bounded retries");
            Equal(5, fs.MainReads, "at most five recovery attempts");
            Equal(4, fs.MainInfoReads, "last failure does not check for another attempt");
            Equal(0, fs.Mutations.Count, "viewer makes no mutations");
        });

        yield return JsonlFixtureCase("storage.jsonl.read-only.does-not-retry-filesystem-failure", async fixture =>
        {
            var directory = fixture.NewDirectory();
            var writer = await fixture.Open(directory);
            await CreateRoot(writer);
            var task = await writer.MintIdAsync<TaskId>();
            await Commit(writer, new TaskWrite(PendingTask(task)));
            var fs = new ReadOnlyAuditFileSystem(directory) { FailRead = $"task-{task}.jsonl" };
            var error = await ThrowsAsync<Exception>(() => JsonlStorage.OpenAsync(directory, fs, JsonlContext, new() { ReadOnly = true }), "I/O failure");
            Check(error.Message.Contains("injected read failure", StringComparison.Ordinal), "filesystem failure reported");
            Equal(1, fs.MainReads, "I/O error not retried");
            Equal(0, fs.MainInfoReads, "only corruption checks main for changes");
            Equal(0, fs.Mutations.Count, "no mutation calls");
        });
    }

    private static string JsonlDirectoryBytes(string directory) => string.Join("\n", Directory.GetFiles(directory)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetFileName(path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))));

    /// <summary>Rejects and records every mutating filesystem call; its read hooks simulate a separate writer.</summary>
    private sealed class ReadOnlyAuditFileSystem(string directory) : LocalFileSystem(directory)
    {
        public List<string> Mutations { get; } = [];
        public int MainReads { get; private set; }
        public int MainInfoReads { get; private set; }
        public Func<string, Task>? BeforeRead { get; set; }
        public Func<string, Task>? AfterMainRead { get; init; }
        public string? FailRead { get; init; }

        public override async Task<Result<byte[], FileError>> ReadBinaryFileAsync(string path, DurableContext context)
        {
            var isMain = Path.GetFileName(path) == "main.jsonl";
            if (isMain) MainReads++;
            if (BeforeRead is { } before) await before(path);
            if (Path.GetFileName(path) == FailRead) return new FileError(FileErrorCode.Unknown, "injected read failure", path);
            var result = await base.ReadBinaryFileAsync(path, context);
            if (isMain && AfterMainRead is { } after) await after(path);
            return result;
        }

        public override Task<Result<FileMetadata, FileError>> FileInfoAsync(string path, DurableContext context)
        {
            if (Path.GetFileName(path) == "main.jsonl") MainInfoReads++;
            return base.FileInfoAsync(path, context);
        }

        private Task<Result<Unit, FileError>> Mutation(string operation)
        {
            Mutations.Add(operation);
            return Task.FromResult<Result<Unit, FileError>>(new FileError(FileErrorCode.Unknown, "read-only audit rejected " + operation));
        }

        private Task<Result<string, FileError>> TempMutation(string operation)
        {
            Mutations.Add(operation);
            return Task.FromResult<Result<string, FileError>>(new FileError(FileErrorCode.Unknown, "read-only audit rejected " + operation));
        }

        public override Task<Result<Unit, FileError>> WriteFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) => Mutation("write");
        public override Task<Result<Unit, FileError>> AppendFileAsync(string path, ReadOnlyMemory<byte> content, DurableContext context) => Mutation("append");
        public override Task<Result<Unit, FileError>> TruncateFileAsync(string path, long size, DurableContext context) => Mutation("truncate");
        public override Task<Result<Unit, FileError>> FlushFileAsync(string path, DurableContext context) => Mutation("flush");
        public override Task<Result<Unit, FileError>> RenameFileAsync(string sourcePath, string destinationPath, DurableContext context) => Mutation("rename");
        public override Task<Result<Unit, FileError>> CreateDirAsync(string path, CreateDirOptions? options, DurableContext context) => Mutation("create-directory");
        public override Task<Result<Unit, FileError>> RemoveAsync(string path, RemoveOptions? options, DurableContext context) => Mutation("remove");
        public override Task<Result<string, FileError>> CreateTempDirAsync(string? prefix, DurableContext context) => TempMutation("create-temp-directory");
        public override Task<Result<string, FileError>> CreateTempFileAsync(TempFileOptions? options, DurableContext context) => TempMutation("create-temp-file");
        public override Task CleanupAsync(DurableContext context) { Mutations.Add("cleanup"); return Task.CompletedTask; }
    }
}
