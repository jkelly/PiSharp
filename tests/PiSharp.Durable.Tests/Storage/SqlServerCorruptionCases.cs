// PiSharp-only: Authored regression for the native SQL Server adapter; no upstream execution capture.
// Authored native regressions: SQL Server has no upstream adapter. The offline executor exercises the real CommitAsync validation,
// materialization, refusal and receipt paths with a transactional in-memory write log. It is not evidence of live SQL Server behavior.
using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.MSSQL;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> StorageMssqlCorruptionCases()
    {
        foreach (var fault in new[]
        {
            "record-json", "record-shape", "copy-record-json", "copy-record-shape", "latest-version", "missing-revision",
            "base-json", "base-shape", "base-missing", "base-version", "delta-json", "delta-shape", "delta-op",
            "delta-root", "delta-version",
        })
            yield return Case("storage.mssql.offline.corruption-rolls-back-" + fault, async () =>
            {
                var database = new CorruptMssqlDatabase(fault);
                var storage = new SqlServerStorage(database, "[dbo]", 1, "offline", 100);
                var receipts = new List<int>();
                await ThrowsAsync<Exception>(() => storage.CommitAsync(
                    [[new EntryWrite(MssqlEntry(new EntryId(20)))], [database.Write]],
                    (index, _) => receipts.Add(index), MssqlContext), fault);
                Equal(0, receipts.Count, "no receipts before whole-call success");
                Equal(0, database.Entries, "earlier entry batch rolled back");
                Equal(3L, database.NextSeq, "sequence allocator rolled back");
                Equal(100L, database.NextId, "ID allocator rolled back");
                Equal(0, database.Commits, "no transaction committed");
                Equal(1, database.Rollbacks, "whole transaction rolled back");
                Equal(1, database.AttemptedEntries, "earlier batch actually reached its write");
            });

        foreach (var reason in new[] { "missing-source", "mismatched-source", "unretained-history", "retired-document", "caller-version" })
            yield return Case("storage.mssql.offline.semantic-refusal-keeps-prefix-" + reason, async () =>
            {
                var database = new CorruptMssqlDatabase(reason);
                var storage = new SqlServerStorage(database, "[dbo]", 1, "offline", 100);
                var receipts = new List<int>();
                var result = await storage.CommitAsync([[new EntryWrite(MssqlEntry(new EntryId(20)))], [database.Write]],
                    (index, _) =>
                    {
                        Equal(1, database.Commits, "receipt follows transaction commit");
                        receipts.Add(index);
                    }, MssqlContext);
                Equal(1, result.Stored, "accepted prefix");
                Check(result.Refused is not null, "semantic refusal reported");
                Equal(1, receipts.Count, "prefix receipt");
                Equal(0, receipts[0], "first batch receipt");
                Equal(1, database.Entries, "prefix persisted");
                Equal(4L, database.NextSeq, "only prefix consumed a sequence");
                Equal(0, database.Rollbacks, "semantic refusal does not roll back the prefix");
            });
    }

    private sealed class CorruptMssqlDatabase : ISqlServerDatabase, ISqlServerExecutor
    {
        private readonly string fault;
        private readonly DocumentCreate source;
        private int pendingEntries;
        private long pendingNextSeq;
        private long pendingNextId;

        public CorruptMssqlDatabase(string fault)
        {
            this.fault = fault;
            source = new DocumentCreate
            {
                Id = new DocumentId(10), Kind = "copy", Scope = DocumentScope.Conversation(ConversationId.Root),
                History = fault == "unretained-history" ? DocumentHistory.Latest : DocumentHistory.Rewindable,
                Fork = fault == "unretained-history" ? DocumentFork.Current : DocumentFork.AsOf,
            };
        }

        public StorageWrite Write => fault is "record-json" or "record-shape" or "latest-version" or "missing-revision" or "retired-document" or "caller-version"
            ? new DocumentChangeWrite(source.Id, DocumentContent.Delta(fault == "caller-version" ? 2 : 1, []))
            : new DocumentCopyWrite(source with
            {
                Id = new DocumentId(11), Scope = DocumentScope.Conversation(new ConversationId(30)),
                Kind = fault == "mismatched-source" ? "mismatch" : source.Kind,
            }, new DocumentCopySource(source.Id, fault == "unretained-history" ? new Seq(1) : DocumentPoint.Current));

        public bool CallFailed { get; set; }
        public int Entries { get; private set; }
        public int AttemptedEntries { get; private set; }
        public long NextSeq { get; private set; } = 3;
        public long NextId { get; private set; } = 100;
        public int Commits { get; private set; }
        public int Rollbacks { get; private set; }

        public async Task<T> TransactionAsync<T>(Func<ISqlServerExecutor, Task<T>> callback)
        {
            pendingEntries = Entries;
            pendingNextSeq = NextSeq;
            pendingNextId = NextId;
            try
            {
                var result = await callback(this);
                Entries = pendingEntries;
                NextSeq = pendingNextSeq;
                NextId = pendingNextId;
                Commits++;
                return result;
            }
            catch
            {
                Rollbacks++;
                throw;
            }
        }

        public Task RunAsync(string sql, params object?[] parameters)
        {
            if (sql.Contains("INSERT INTO [dbo].durable_entries", StringComparison.Ordinal))
            {
                pendingEntries++;
                AttemptedEntries++;
            }
            else if (sql.Contains("UPDATE [dbo].durable_stores", StringComparison.Ordinal))
            {
                pendingNextId = (long)parameters[1]!;
                pendingNextSeq = (long)parameters[2]!;
            }
            else throw new InvalidOperationException("Unexpected offline write: " + sql);
            return Task.CompletedTask;
        }

        public Task<SqlServerRow?> GetAsync(string sql, params object?[] parameters)
        {
            SqlServerRow? row;
            if (sql.Contains("FROM [dbo].durable_stores", StringComparison.Ordinal))
                row = Row(("next_id", NextId), ("next_seq", NextSeq));
            else if (sql.StartsWith("SELECT record FROM [dbo].durable_documents", StringComparison.Ordinal))
                row = (long)parameters[1]! != source.Id.Value || fault == "missing-source" ? null : Row(("record",
                    fault is "record-json" or "copy-record-json" ? "{" :
                    fault is "record-shape" or "copy-record-shape" ? "{}" :
                    source.ToRecord(new Seq(1), fault == "retired-document" ? new Seq(2) : null).ToJson().ToJson()));
            else if (sql.StartsWith("SELECT TOP (1) version", StringComparison.Ordinal))
                row = fault == "missing-revision" ? null : Row(("version", fault == "latest-version" ? long.MaxValue : 1L));
            else if (sql.StartsWith("SELECT TOP (1) id", StringComparison.Ordinal))
                row = (long)parameters[3]! == 0L ? Row(("id", source.Id.Value)) : null;
            else if (sql.Contains("kind = N'base'", StringComparison.Ordinal))
                row = fault == "base-missing" ? null : Row(("seq", 1L), ("kind", "base"),
                    ("version", fault == "base-version" ? 0L : 1L),
                    ("content", fault == "base-json" ? "{" : fault == "base-shape" ? "[]" : "{}"));
            else throw new InvalidOperationException("Unexpected offline query: " + sql);
            return Task.FromResult(row);
        }

        public Task<IReadOnlyList<SqlServerRow>> AllAsync(string sql, params object?[] parameters)
        {
            IReadOnlyList<SqlServerRow> rows;
            if (sql.Contains("durable_record_ids", StringComparison.Ordinal))
                rows = parameters.Skip(1).Any(id => (long)id! == source.Id.Value)
                    ? [Row(("id", source.Id.Value), ("record_type", "document"))] : [];
            else if (sql.Contains("durable_document_revisions", StringComparison.Ordinal))
                rows = fault.StartsWith("delta-", StringComparison.Ordinal)
                    ? [Row(("seq", 2L), ("kind", "delta"), ("version", fault == "delta-version" ? 2L : 1L), ("content", fault switch
                    {
                        "delta-json" => "[", "delta-shape" => "{}", "delta-op" => "[[\"unknown\"]]",
                        "delta-root" => "[[\"r\",1]]", _ => "[]",
                    }))] : [];
            else throw new InvalidOperationException("Unexpected offline scan: " + sql);
            return Task.FromResult(rows);
        }

        public Task<T> HoldAsync<T>(Func<ISqlServerExecutor, Task<T>> callback) => callback(this);
        public Task CloseAsync(Func<ISqlServerExecutor, Task>? beforeClose = null) => Task.CompletedTask;
        public Task<SqlServerRow?> GetWaitingAsync(int timeoutSeconds, string sql, params object?[] parameters) =>
            throw new NotSupportedException("Offline executor does not acquire live server locks");
        private static SqlServerRow Row(params (string Key, object? Value)[] columns) =>
            new([.. columns.Select(column => new KeyValuePair<string, object?>(column.Key, column.Value))]);
    }
}
