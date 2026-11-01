using Microsoft.Data.Sqlite;
using PiSharp.Durable;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> LifecycleRecoveryCases()
    {
        yield return Case("lifecycle.initialization-recovers-the-requested-agent", async () =>
        {
            await using var device = await TestDevice.OpenAsync("initialization-recovery", new DeviceSetup
            {
                Storage = _ => Task.FromException<IStorage>(new IOException("injected open failure")),
            });
            var created = await device.Host.CreateSessionAsync(new() { Cwd = device.Work, Name = "pending", ThinkingLevel = "high" });
            Check(!created.IsAvailable, "the failed initialization is unavailable");
            Equal("high", device.Host.Catalog.Sessions().Single().InitialThinking, "initialization intent survives the failure");
            await device.ReopenAsync();
            var recovered = device.Host.ResolveSession(created.Id);
            var config = await recovered.ConfigurationAsync(1);
            Equal(FauxDurableModels.EchoRef, config.Model, "recovered model");
            Equal(device.Work, config.Cwd, "recovered working directory");
            Equal("high", config.ThinkingLevel, "recovered thinking level");
            Check(device.Host.Catalog.Sessions().Single().InitialThinking is null, "initialization was acknowledged");
            await device.ReopenAsync();
            Equal("Echo: recovered", (await device.Host.ResolveSession(created.Id).SendAsync(1,
                new SendRequest("recovered", "recovered") { Wait = Answer })).AnswerText, "usable after another reopen");
        });

        yield return Case("lifecycle.completed-root-is-not-overwritten-by-pending-initialization", async () =>
        {
            await using var device = await TestDevice.OpenAsync("initialization-checkpoint");
            var session = await device.CreateAsync("configured");
            await session.ConfigureAsync(1, new AgentConfigurationChange { ThinkingLevel = "medium", Instructions = "Keep this configuration." });
            await device.Host.DisposeAsync();
            // Disk state after the root commit but before the catalog acknowledgment.
            CatalogSql(device, "UPDATE sessions SET initial_thinking = 'high';");
            await device.ReopenAsync();
            var config = await device.Host.ResolveSession(session.Id).ConfigurationAsync(1);
            Equal("medium", config.ThinkingLevel, "the committed root is authoritative");
            Equal("Keep this configuration.", config.Instructions, "existing instructions");
            Check(device.Host.Catalog.Sessions().Single().InitialThinking is null, "pending marker cleared");
        });

        yield return Case("lifecycle.verified-creation-keys-survive-reopen-and-renames", async () =>
        {
            await using var device = await TestDevice.OpenAsync("creation-identity");
            var request = new SessionCreateRequest { Cwd = device.Work, Name = "original", SpawnKey = "verified", SpawnRequest = "request-1" };
            var created = await device.Host.CreateSessionAsync(request);
            await device.Host.RenameSessionAsync(created.Id, "renamed");
            await device.ReopenAsync();
            Equal(created.Id, (await device.Host.CreateSessionAsync(request)).Id, "replay uses saved request, not current name");
            await ThrowsAsync<HostInvalidException>(() => device.Host.CreateSessionAsync(request with { SpawnRequest = "request-2" }),
                "conflicting request after reopen");
        });

        foreach (var moved in new[] { false, true })
            yield return Case($"lifecycle.archive-recovers-{(moved ? "after" : "before")}-the-move", async () =>
            {
                await using var device = await TestDevice.OpenAsync("archive-recovery");
                var session = await device.CreateAsync("archive me", spawnKey: "archive-key");
                await session.SendAsync(1, new SendRequest("saved history", "before-archive") { Wait = Answer });
                var target = Path.Join(device.Data, "archive", session.Id + "-recover");
                device.Host.Catalog.BeginArchive(session.Id, target);
                await device.Host.DisposeAsync();
                if (moved)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    Directory.Move(session.DirectoryPath, target);
                }
                await device.ReopenAsync();
                Equal(0, device.Host.Sessions.Count, "the archived session is not reopened");
                Equal(0L, device.Host.Catalog.RowsFor(session.Id), "all catalog rows removed");
                Check(!Directory.Exists(session.DirectoryPath), "no replacement directory");
                Check(File.Exists(Path.Join(target, "agent.sqlite")), "archived database preserved");
                await device.ReopenAsync();
                Equal(0, device.Host.Sessions.Count, "recovery is idempotent");
            });

        yield return Case("lifecycle.missing-established-database-is-not-recreated", async () =>
        {
            await using var device = await TestDevice.OpenAsync("missing-database");
            var session = await device.CreateAsync("missing");
            await device.Host.DisposeAsync();
            var target = Path.Join(device.Root, "old-archive");
            Directory.Move(session.DirectoryPath, target);
            await device.ReopenAsync();
            Check(!device.Host.ResolveSession(session.Id).IsAvailable, "missing storage is unavailable");
            Check(!Directory.Exists(session.DirectoryPath), "no replacement storage created");
            Check(File.Exists(Path.Join(target, "agent.sqlite")), "the original is preserved");
        });

        yield return Case("lifecycle.failed-archive-stays-visible-and-can-be-retried", async () =>
        {
            await using var device = await TestDevice.OpenAsync("archive-retry");
            var session = await device.CreateAsync("retry archive");
            var target = Path.Join(device.Data, "archive", session.Id + "-blocked");
            Directory.CreateDirectory(target);
            device.Host.Catalog.BeginArchive(session.Id, target);
            await ThrowsAsync<IOException>(() => device.Host.ArchiveSessionAsync(session.Id), "existing destination");
            Check(!device.Host.ResolveSession(session.Id).IsAvailable, "failed archive stays visible with its error");
            Check(File.Exists(session.DatabasePath), "original database preserved");
            Directory.Delete(target); // The empty test-owned obstruction only.
            Equal(target, await device.Host.ArchiveSessionAsync(session.Id), "retry uses the same destination");
            Equal(0, device.Host.Sessions.Count, "removed after successful archive");
        });

        yield return Case("lifecycle.established-database-without-a-root-is-not-reinitialized", async () =>
        {
            await using var device = await TestDevice.OpenAsync("missing-root");
            var session = await device.CreateAsync("established");
            await device.Host.DisposeAsync();
            File.Move(session.DatabasePath, session.DatabasePath + ".original");
            var empty = await Sqlite(session.DatabasePath);
            await empty.CloseAsync(PiSharp.Durable.Chord.DurableContext.Background);
            await device.ReopenAsync();
            Check(!device.Host.ResolveSession(session.Id).IsAvailable, "a missing root is unavailable");
            var inspected = await Sqlite(session.DatabasePath);
            try
            {
                Check(await inspected.GetConversationAsync(ConversationId.Root, PiSharp.Durable.Chord.DurableContext.Background) is null,
                    "reopening did not invent a root conversation");
            }
            finally { await inspected.CloseAsync(PiSharp.Durable.Chord.DurableContext.Background); }
        });

        yield return Case("lifecycle.shutdown-joins-creation-and-rejects-queued-mutations", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var device = await TestDevice.OpenAsync("shutdown-creation", new DeviceSetup
            {
                Storage = async path => { entered.TrySetResult(); await release.Task; return await Sqlite(path); },
            });
            var creating = device.CreateAsync("admitted");
            await entered.Task.WaitAsync(Answer);
            var queued = device.CreateAsync("queued");
            var closing = device.Host.DisposeAsync().AsTask();
            var secondClose = device.Host.DisposeAsync().AsTask();
            var waited = !closing.IsCompleted;
            release.TrySetResult();
            var session = await creating;
            await closing;
            await secondClose;
            Check(waited, "shutdown waited for creation");
            Check(ReferenceEquals(closing, secondClose), "disposers join the same operation");
            await ThrowsAsync<ObjectDisposedException>(() => queued, "queued create rejected after shutdown admission");
            Check(!session.IsAvailable, "the admitted session closed before disposal completed");
            await device.ReopenAsync();
            Equal(1, device.Host.Sessions.Count, "only the admitted session was created");
            Check(device.Host.ResolveSession(session.Id).IsAvailable, "all locks released for reopen");
        });

        foreach (var version in new[] { 1, 2, 3, 4, 5, 6 })
            yield return Case($"lifecycle.catalog-v{version}-migration-preserves-sessions", async () =>
            {
                await using var device = await TestDevice.OpenAsync("catalog-migration");
                var session = await device.CreateAsync("migrated", spawnKey: "old-key");
                var uid = device.Host.Info.Uid;
                await device.Host.DisposeAsync();
                CatalogSql(device, "DROP TABLE preference_profiles; DROP TABLE feedback_events; " +
                    (version < 6 ? "DROP TABLE sources; DROP TABLE source_state; DROP TABLE source_items; DROP TABLE fetch_receipts; " +
                    "DROP TABLE retrieval_cache; DROP TABLE retrieval_health; " : "") +
                    (version < 5 ? "DROP TABLE operations; DROP TABLE blobs; DROP TABLE blob_refs; DROP TABLE budget_limits; DROP TABLE budget_reservations; " +
                    "DROP TABLE policies; DROP TABLE policy_versions; DROP TABLE judgment_cache; " : "") +
                    (version < 4 ? "DROP TABLE job_schedules; DROP TABLE trigger_credentials; " : "") +
                    (version < 3 ? "ALTER TABLE sessions DROP COLUMN initial_thinking; ALTER TABLE sessions DROP COLUMN archive_path; ALTER TABLE session_spawns DROP COLUMN request; " : "") +
                    (version == 1 ? "DROP TABLE app_state; DROP TABLE app_state_receipts; " : "") +
                    $"PRAGMA user_version = {version};");
                await device.ReopenAsync();
                Equal(uid, device.Host.Info.Uid, "device identity retained");
                Check(device.Host.ResolveSession(session.Id).IsAvailable, "existing session opens");
                Equal(session.Id, (await device.CreateAsync("same key", spawnKey: "old-key")).Id, "key-only replay retained");
                await ThrowsAsync<HostInvalidException>(() => device.Host.CreateSessionAsync(new()
                { Cwd = device.Work, SpawnKey = "old-key", SpawnRequest = "unverifiable" }), "legacy verified replay refused");
            });
    }

    private static void CatalogSql(TestDevice device, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Join(device.Data, "catalog.sqlite"), Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
