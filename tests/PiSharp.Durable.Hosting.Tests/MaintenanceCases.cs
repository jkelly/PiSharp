// PiSharp-only (decision 0010, M6): retention (src/PiSharp.Durable.Hosting/Storage/Retention.cs), backup and restore (DeviceBackup.cs)
// and the doctor (DeviceDoctor.cs) on real data directories: a retention purge in batches that keeps an active run's receipts, unresolved
// reservations and referenced blobs; a backup restored into a clean root; and doctor checks that change no byte of a healthy, damaged,
// old or newer store.
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;

internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> MaintenanceCases() =>
    [
        Case("maintenance.retention-purges-in-batches-and-keeps-what-is-still-needed", async () =>
        {
            var clock = new JumpClock();
            await using var device = await TestDevice.OpenAsync("retention", new DeviceSetup { Time = clock });
            var session = await device.CreateAsync("retention");
            var job = await session.RunJobAsync(1, "release-check", JsonValue.Object([new("commits", 1.0)]));
            await WaitForRunAsync(session, job.Value, "waiting");
            var catalog = device.Host.Catalog;
            var blobs = device.Host.Blobs;
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            var active = blobs.Put("active page"u8, "text/html", "fetch:op-active").Hash;
            var done = blobs.Put("finished page"u8, "text/html", "fetch:op-done").Hash;
            var body = blobs.Put("item body"u8, "text/plain", "source-item:src:item-1").Hash;
            var evidence = blobs.Put("evidence text"u8, "text/plain", "evidence:s:e-1").Hash;
            catalog.Transact(transaction =>
            {
                foreach (var (operation, run) in new[] { ("op-active", $"{session.Id}/{job.Value}"), ("op-done", $"{session.Id}/999999"), ("op-gone", "no-such-session/1") })
                    catalog.Run(transaction, """
                        INSERT INTO fetch_receipts (operation_id, source_id, run_key, provider, url, status, created_at, updated_at)
                        VALUES ($op, 'src', $run, 'direct', 'https://example.test/', 'done', $at, $at);
                        """, ("$op", operation), ("$run", run), ("$at", now));
                foreach (var (operation, status) in new[] { ("r-settled", "settled"), ("r-uncertain", "uncertain") })
                    catalog.Run(transaction, """
                        INSERT INTO budget_reservations (operation_id, run_key, provider, day, status, requested, cost_known, created_at, updated_at)
                        VALUES ($op, 'x/1', 'faux', '2026-01-01', $status, '{}', 1, $at, $at);
                        """, ("$op", operation), ("$status", status), ("$at", now));
                catalog.Run(transaction, "INSERT INTO judgment_cache (key, value, created_at, expires_at) VALUES ('j-1', '{}', $at, $expires);", ("$at", now), ("$expires", now + 3_600_000));
                catalog.Run(transaction, """
                    INSERT INTO source_items (source_id, item_id, title, content_hash, parser, text_hash, version, first_seen_at, last_seen_at, changed_at)
                    VALUES ('src', 'item-1', 'Item', 'sha256:x', 'rss/1', $text, 1, $at, $at, $at);
                    """, ("$text", body), ("$at", now));
                catalog.Run(transaction, "INSERT INTO retrieval_cache (key, provider, url, identity, fetched_at) VALUES ('c-1', 'direct', 'https://example.test/', 'anon', $at);", ("$at", now));
                return 0;
            });
            int Rows(string table) => catalog.Read(_ => Convert.ToInt32(catalog.Value(null, $"SELECT COUNT(*) FROM {table};"), System.Globalization.CultureInfo.InvariantCulture));
            var before = new[] { "fetch_receipts", "budget_reservations", "judgment_cache", "retrieval_cache", "blobs", "blob_refs" }.ToDictionary(table => table, Rows);

            clock.Advance(TimeSpan.FromDays(91));
            var dry = await device.Host.Retention.RunAsync(dryRun: true);
            RetentionCategory Category(RetentionReport report, string id) => report.Categories.Single(category => category.Id == id);
            Check(dry.DryRun && !dry.More, "a dry run");
            Equal(3, Category(dry, "fetch-receipts").Eligible, "three old fetch receipts");
            Equal(1, Category(dry, "fetch-receipts").Kept, "the waiting run's receipt is kept");
            Check(Category(dry, "budget-reservations").Kept >= 1, "the uncertain reservation is kept");
            Equal(1, Category(dry, "source-item-bodies").Eligible, "one old item body");
            Check(Category(dry, "judgment-cache").Eligible >= 1 && Category(dry, "retrieval-cache").Eligible >= 1, "expired cache entries");
            Check(dry.Categories.All(category => category.Purged == 0), "a dry run purges nothing");
            foreach (var (table, count) in before) Equal(count, Rows(table), "a dry run leaves " + table);

            var first = await device.Host.Retention.RunAsync(dryRun: false, batch: 1);
            Equal(1, Category(first, "fetch-receipts").Purged, "one receipt per batch");
            Check(first.More, "more remain after a batch of one");
            var second = await device.Host.Retention.RunAsync(dryRun: false);
            Check(!second.More, "the second pass finishes: " + string.Join(", ", second.Categories.Select(category => $"{category.Id} {category.Eligible}/{category.Purged}/{category.Kept}")));
            var third = await device.Host.Retention.RunAsync(dryRun: true);
            Equal(1, Category(third, "fetch-receipts").Eligible, "only the waiting run's receipt is left");
            Equal(0, Category(third, "source-item-bodies").Eligible, "the body is gone");
            Equal(0, Category(third, "judgment-cache").Eligible + Category(third, "retrieval-cache").Eligible, "the caches are clear");

            Check(blobs.Get(active) is not null && blobs.Read(active) is not null, "the waiting run's page is kept");
            Check(blobs.Get(evidence) is not null, "referenced evidence is kept");
            Check(blobs.Get(done) is null && !File.Exists(blobs.PathOf(done)), "a finished run's page is collected");
            Check(blobs.Get(body) is null, "the old item body is collected");
            Check(catalog.Read(_ => catalog.Value(null, "SELECT COUNT(*) FROM source_items WHERE item_id = 'item-1' AND text_hash IS NULL;")) is 1L, "the item stays, without a body hash");
            Equal("uncertain", catalog.Read(_ => catalog.Value(null, "SELECT status FROM budget_reservations WHERE operation_id = 'r-uncertain';")) as string,
                "the uncertain reservation stays");
            Check(catalog.Read(_ => catalog.Value(null, "SELECT COUNT(*) FROM budget_reservations WHERE operation_id = 'r-settled';")) is 0L, "the settled one is purged");
            await ThrowsAsync<HostInvalidException>(() => device.Host.Retention.RunAsync(dryRun: false, batch: 0), "a batch of 0");
            await session.DecideJobAsync(1, job.Value, approved: false);
        }),

        Case("maintenance.backup-restores-into-a-clean-root-and-detects-damage", async () =>
        {
            var device = await TestDevice.OpenAsync("backup");
            var root = device.Root;
            var session = await device.CreateAsync("backup");
            await session.SendAsync(1, new SendRequest("remember the backup", "b-1") { Wait = Answer });
            var blob = device.Host.Blobs.Put("evidence body"u8, "text/plain", $"evidence:{session.Id}:e-1").Hash;
            File.WriteAllText(Path.Join(device.Data, "token"), "secret-token-value-that-is-long-enough\n");
            File.WriteAllText(Path.Join(device.Data, "device.json"), """{"host":"127.0.0.1","port":7311}""");
            Throws<DeviceLockedException>(() => DeviceBackup.Create(device.Data, Path.Join(root, "while-running")), "a running device is not backed up");
            await device.Host.DisposeAsync();

            var backup = Path.Join(root, "backup");
            var manifest = DeviceBackup.Create(device.Data, backup);
            Check(!manifest.IncludesSecrets && manifest.Files.All(file => file.Path != "token"), "the token stays out");
            Check(manifest.Files.Any(file => file.Path == "catalog.sqlite") && manifest.Files.Any(file => file.Path == $"sessions/{session.Id}/agent.sqlite") &&
                manifest.Files.Any(file => file.Path == "device.json") && manifest.Files.Any(file => file.Path.StartsWith("blobs/sha256/", StringComparison.Ordinal)),
                "catalog, session, config and blobs: " + string.Join(", ", manifest.Files.Select(file => file.Path)));
            Check(!manifest.Files.Any(file => file.Path.EndsWith("-wal", StringComparison.Ordinal) || file.Path == "catalog.lock"), "no WAL or lock files");
            Equal(session.Id, string.Join(",", manifest.Sessions), "the session list");
            Check(DeviceBackup.Verify(backup).Ok, "a fresh backup verifies");
            Throws<IOException>(() => DeviceBackup.Create(device.Data, backup), "a backup never writes into a non-empty directory");

            var restored = Path.Join(root, "restored");
            DeviceBackup.Restore(backup, restored);
            Throws<IOException>(() => DeviceBackup.Restore(backup, restored), "a restore never writes into a non-empty directory");
            await using (var host = await DurableDeviceHost.OpenAsync(device.Options() with { DataDirectory = restored }))
            {
                var again = host.ResolveSession(session.Id);
                var transcript = string.Join("\n", (await AllEntriesAsync(again, 1)).Select(entry => entry.ToJson().ToJson()));
                Check(transcript.Contains("remember the backup") && transcript.Contains("Echo: remember the backup"), "the conversation is restored: " + transcript);
                Check(host.Blobs.Read(blob) is { } bytes && bytes.SequenceEqual("evidence body"u8.ToArray()), "the blob is restored");
                Equal(device.Host.Info.Uid, host.Info.Uid, "the same device identity");
            }
            Check(!File.Exists(Path.Join(restored, "token")), "no token restored");

            var damaged = Path.Join(root, "damaged");
            CopyTree(backup, damaged);
            var blobFile = Directory.EnumerateFiles(Path.Join(damaged, "blobs"), "*", SearchOption.AllDirectories).First();
            File.WriteAllText(blobFile, "tampered");
            File.WriteAllText(Path.Join(damaged, "stray.txt"), "?");
            var check = DeviceBackup.Verify(damaged);
            Check(!check.Ok && check.Problems.Any(problem => problem.StartsWith("blobs/sha256/", StringComparison.Ordinal)) &&
                check.Problems.Any(problem => problem.StartsWith("stray.txt", StringComparison.Ordinal)), "damage is named: " + string.Join("; ", check.Problems));
            Throws<InvalidDataException>(() => DeviceBackup.Restore(damaged, Path.Join(root, "from-damaged")), "a damaged backup is not restored");
            Check(!Directory.Exists(Path.Join(root, "from-damaged")), "nothing is written from a damaged backup");

            var withSecrets = DeviceBackup.Create(device.Data, Path.Join(root, "with-secrets"), includeSecrets: true);
            Check(withSecrets.IncludesSecrets && withSecrets.Files.Any(file => file.Path == "token"), "the token only when asked for");
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }),

        Case("maintenance.doctor-reads-without-writing-and-survives-damaged-old-and-newer-stores", async () =>
        {
            var device = await TestDevice.OpenAsync("doctor");
            var session = await device.CreateAsync("doctor");
            await session.SendAsync(1, new SendRequest("hello", "d-1") { Wait = Answer });
            device.Host.Blobs.Put("kept"u8, "text/plain", "evidence:s:e");
            var running = DeviceDoctor.Check(device.Data);
            Equal("info", running.Single(check => check.Id == "device").Status, "a running device is noted");
            Throws<DeviceLockedException>(() => DeviceDoctor.Repair(device.Data), "repair needs the device stopped");
            await device.Host.DisposeAsync();

            var healthy = Snapshot(device.Data);
            var checks = DeviceDoctor.Check(device.Data, new DoctorOptions { VerifyBlobs = true });
            Check(checks.All(check => check.Status is not "fail"), "a healthy store: " + Describe(checks));
            Equal("ok", checks.Single(check => check.Id == "catalog").Status, "catalog");
            Equal("ok", checks.Single(check => check.Id == "sessions").Status, "sessions");
            Equal("ok", checks.Single(check => check.Id == "blobs").Status, "blobs");
            Equal(healthy, Snapshot(device.Data), "checking changes nothing");
            Equal("warn", DeviceDoctor.Check(Path.Join(device.Root, "missing")).Single().Status, "a missing directory is a warning");

            var damagedSession = Copy(device.Data, "session-damaged");
            File.WriteAllBytes(Path.Join(damagedSession, "sessions", session.Id, "agent.sqlite"), new byte[4096]);
            var sessionChecks = DeviceDoctor.Check(damagedSession);
            Equal("fail", sessionChecks.Single(check => check.Id == "sessions").Status, "a damaged session: " + Describe(sessionChecks));
            var missingBlob = Copy(device.Data, "blob-missing");
            File.Delete(Directory.EnumerateFiles(Path.Join(missingBlob, "blobs", "sha256"), "*", SearchOption.AllDirectories).First());
            Equal("fail", DeviceDoctor.Check(missingBlob).Single(check => check.Id == "blobs").Status, "a missing blob");

            var damagedCatalog = Copy(device.Data, "catalog-damaged");
            File.WriteAllText(Path.Join(damagedCatalog, "catalog.sqlite"), "this is not a database, it only sits where one should");
            var before = Snapshot(damagedCatalog);
            var catalogChecks = DeviceDoctor.Check(damagedCatalog);
            Equal("fail", catalogChecks.Single(check => check.Id == "catalog").Status, "a damaged catalog: " + Describe(catalogChecks));
            Equal(before, Snapshot(damagedCatalog), "a damaged catalog is left as it was");

            var newer = Copy(device.Data, "catalog-newer");
            Execute(Path.Join(newer, "catalog.sqlite"), "PRAGMA user_version = 99;");
            Equal("fail", DeviceDoctor.Check(newer).Single(check => check.Id == "catalog").Status, "a newer catalog");

            var old = Copy(device.Data, "catalog-old");
            Execute(Path.Join(old, "catalog.sqlite"), """
                DROP TABLE preference_profiles; DROP TABLE feedback_events; DROP TABLE sources; DROP TABLE source_state; DROP TABLE source_items;
                DROP TABLE fetch_receipts; DROP TABLE retrieval_cache; DROP TABLE retrieval_health; DROP TABLE operations; DROP TABLE blobs; DROP TABLE blob_refs;
                DROP TABLE budget_limits; DROP TABLE budget_reservations; DROP TABLE policies; DROP TABLE policy_versions; DROP TABLE judgment_cache;
                PRAGMA user_version = 4;
                """);
            Directory.Delete(Path.Join(old, "blobs"), recursive: true);
            var oldBefore = Snapshot(old);
            var oldChecks = DeviceDoctor.Check(old);
            Equal("warn", oldChecks.Single(check => check.Id == "catalog").Status, "an old catalog migrates later: " + Describe(oldChecks));
            Equal("skip", oldChecks.Single(check => check.Id == "application-records").Status, "records the old catalog cannot have are skipped");
            Equal(oldBefore, Snapshot(old), "checking an old store changes nothing");
            var repaired = DeviceDoctor.Repair(old);
            Check(repaired.Any(action => action.Contains("from schema version 4 to 7", StringComparison.Ordinal)), "repair migrates: " + string.Join(" ", repaired));
            Check(File.Exists(Path.Join(old, "catalog.v4.bak.sqlite")), "after the catalog's own copy");
            var after = DeviceDoctor.Check(old);
            Equal("ok", after.Single(check => check.Id == "catalog").Status, "migrated: " + Describe(after));
            Check(after.All(check => check.Status is not "fail"), "the migrated store is healthy: " + Describe(after));
            try { Directory.Delete(device.Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }

            string Copy(string from, string name)
            {
                var target = Path.Join(device.Root, name);
                CopyTree(from, target);
                return target;
            }
        }),
    ];

    private static string Describe(IEnumerable<DoctorCheck> checks) => string.Join("; ", checks.Select(check => $"{check.Id} {check.Status}: {check.Message}"));

    /// <summary>Every file under <paramref name="root"/> with its size and hash.</summary>
    private static string Snapshot(string root) => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => (Path: Path.GetRelativePath(root, path), Bytes: File.ReadAllBytes(path)))
        .OrderBy(file => file.Path, StringComparer.Ordinal)
        .Select(file => $"{file.Path} {file.Bytes.Length} {Convert.ToHexStringLower(SHA256.HashData(file.Bytes))}"));

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Join(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
