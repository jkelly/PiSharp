// Authored for PiSharp (decision 0007). SQL Server application locks (sp_getapplock) stand in for the SQLite file's single owner: a store
// is open in one storage at a time, and a database's schemas migrate under one connection at a time. Lock resources are scoped to the
// database and compared byte for byte, so a store's lock is keyed by its schema's ID, not by the name a caller spelled: in a case-insensitive
// database "dbo" and "DBO" are one schema and must share one lock.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PiSharp.Durable.MSSQL;

internal static class SqlServerLocks
{
    /// <summary>The lock a migration holds for its transaction: one per database, since a schema may not exist yet to give an ID.</summary>
    public const string MigrationResource = "PiSharp.Durable.MSSQL/migrations";

    /// <summary>The lock an open store holds for its session: the schema's <c>SCHEMA_ID</c> and the store name.</summary>
    public static string StoreResource(int schemaId, string store) =>
        "PiSharp.Durable.MSSQL/store/" + schemaId.ToString(CultureInfo.InvariantCulture) + "/" + Digest(store);

    /// <summary>Takes an exclusive application lock owned by <paramref name="owner"/> (<c>Session</c> or <c>Transaction</c>), waiting at most
    /// <paramref name="timeoutMs"/>; fails with <paramref name="busyMessage"/> when another connection holds it past the timeout.</summary>
    public static async Task AcquireAsync(SqlServerExecutor executor, string resource, string owner, int timeoutMs, string busyMessage)
    {
        // The command outlasts the lock wait, which the server bounds.
        var row = await executor.GetWaitingAsync(timeoutMs / 1000 + 30, $"""
            DECLARE @result INT;
            EXEC @result = sp_getapplock @Resource = @p0, @LockMode = N'Exclusive', @LockOwner = N'{owner}', @LockTimeout = @p1;
            SELECT @result AS result;
            """, resource, timeoutMs).ConfigureAwait(false);
        var result = row?.GetInt64("result") ?? -999;
        if (result == -1) throw new InvalidOperationException(busyMessage);
        if (result < 0) throw new InvalidOperationException($"sp_getapplock failed with {result} for {resource}");
    }

    /// <summary>Releases a session-owned lock this connection holds.</summary>
    public static Task ReleaseAsync(SqlServerExecutor executor, string resource) =>
        executor.RunAsync("EXEC sp_releaseapplock @Resource = @p0, @LockOwner = N'Session'", resource);

    // The raw UTF-16 code units, so that names differing only in a lone surrogate never share a lock.
    private static string Digest(string text) => Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan())));
}
