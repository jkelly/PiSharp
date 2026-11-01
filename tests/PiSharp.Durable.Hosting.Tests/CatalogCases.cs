using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> CatalogCases() =>
    [
        Case("catalog.sessions-survive-a-reopen", async () =>
        {
            await using var device = await TestDevice.OpenAsync("catalog-reopen");
            Directory.CreateDirectory(Path.Join(device.Work, "sub"));
            var alpha = await device.CreateAsync("alpha");
            var second = await device.Host.CreateSessionAsync(new SessionCreateRequest { Cwd = "sub", Name = "   " });
            Equal("New session", second.Name, "a blank name");
            Equal(Path.Join(device.Work, "sub"), second.Cwd, "a relative working directory resolves against the first start directory");
            await device.Host.RenameSessionAsync(alpha.Id, "Alpha renamed");
            var uid = device.Host.Info.Uid;

            await device.ReopenAsync();
            var sessions = device.Host.Sessions;
            Equal(2, sessions.Count, "sessions after reopen");
            Equal(alpha.Id, sessions[0].Id, "oldest first");
            Equal("Alpha renamed", sessions[0].Name, "the rename");
            Equal(FauxDurableModels.EchoRef, sessions[0].Model, "Main's model");
            Check(sessions.All(session => session.IsAvailable), "every session reopened");
            Equal(uid, device.Host.Info.Uid, "the device identity");
            Equal("test-device", device.Host.Info.Name, "the device name");
            Equal(OperatingSystem.IsWindows() ? "win32" : OperatingSystem.IsMacOS() ? "darwin" : "linux", device.Host.Info.Platform, "platform");
        }),

        Case("catalog.a-second-host-is-refused", async () =>
        {
            await using var device = await TestDevice.OpenAsync("catalog-lock");
            await ThrowsAsync<DeviceLockedException>(() => DurableDeviceHost.OpenAsync(device.Options()), "a second host on the data directory");
            await device.ReopenAsync();
            Check(device.Host.Sessions.Count == 0, "the first host's close released the lock");
        }),

        Case("catalog.a-spawn-key-creates-one-session", async () =>
        {
            await using var device = await TestDevice.OpenAsync("catalog-spawn");
            var first = await device.CreateAsync("spawned", spawnKey: "start_session:abc:7");
            var again = await device.CreateAsync("spawned again", spawnKey: "start_session:abc:7");
            Equal(first.Id, again.Id, "the same key");
            await device.ReopenAsync();
            var afterReopen = await device.CreateAsync("spawned a third time", spawnKey: "start_session:abc:7");
            Equal(first.Id, afterReopen.Id, "the same key after a reopen");
            Equal(1, device.Host.Sessions.Count, "sessions");
        }),

        Case("catalog.archive-moves-the-files-and-removes-every-row", async () =>
        {
            await using var device = await TestDevice.OpenAsync("catalog-archive");
            var keep = await device.CreateAsync("keep");
            var doomed = await device.CreateAsync("doomed", spawnKey: "spawn-1");
            await doomed.RenameAgentAsync(1, "Planner");
            doomed.UpdateCompaction(new SessionCompactionChange { ReserveTokens = 20000 });
            Equal(3L, device.Host.Catalog.RowsFor(doomed.Id), "session, agent name and spawn rows");

            var archived = await device.Host.ArchiveSessionAsync("DOOMED");
            Check(File.Exists(Path.Join(archived, "agent.sqlite")), "the database moved to the archive: " + archived);
            Check(!Directory.Exists(doomed.DirectoryPath), "the session directory is gone");
            Check(Path.GetFileName(archived).StartsWith(doomed.Id + "-", StringComparison.Ordinal), "archive/<id>-<time>");
            Equal(0L, device.Host.Catalog.RowsFor(doomed.Id), "catalog rows");
            Throws<HostNotFoundException>(() => device.Host.ResolveSession("doomed"), "the archived session");

            await device.ReopenAsync();
            Equal(keep.Id, device.Host.Sessions.Single().Id, "only the kept session reopens");
            var respawned = await device.CreateAsync("new", spawnKey: "spawn-1");
            Check(respawned.Id != doomed.Id, "an archived session's spawn key is free again");
        }),

        Case("catalog.bad-requests-are-invalid", async () =>
        {
            await using var device = await TestDevice.OpenAsync("catalog-invalid");
            var unknownModel = await ThrowsAsync<HostInvalidException>(() => device.CreateAsync(model: new("faux", "nope")), "an unknown model");
            Equal("invalid", unknownModel.Code, "code");
            await ThrowsAsync<HostInvalidException>(() => device.Host.CreateSessionAsync(new SessionCreateRequest { Cwd = "missing" }), "a missing directory");
            await ThrowsAsync<HostInvalidException>(
                () => device.Host.CreateSessionAsync(new SessionCreateRequest { Cwd = device.Work, ThinkingLevel = "max" }), "an unknown thinking level");
            Equal(0, device.Host.Sessions.Count, "nothing was created");
        }),

        Case("catalog.a-session-that-fails-to-open-is-isolated", async () =>
        {
            await using var device = await TestDevice.OpenAsync("catalog-isolation");
            var good = await device.CreateAsync("good");
            var bad = await device.CreateAsync("bad");
            var badDatabase = bad.DatabasePath;
            await device.ReopenAsync(new DeviceSetup
            {
                Storage = path => path == badDatabase ? Task.FromException<PiSharp.Durable.IStorage>(new IOException("disk on fire")) : Sqlite(path),
            });

            var broken = device.Host.ResolveSession("bad");
            Check(!broken.IsAvailable, "the bad session is unavailable");
            Check(broken.Error?.Message.Contains("disk on fire") == true, "its error: " + broken.Error);
            var unavailable = await ThrowsAsync<SessionUnavailableException>(() => broken.AgentsAsync(), "using it");
            Equal("unavailable", unavailable.Code, "code");
            var summaries = await device.Host.SessionSummariesAsync();
            Check(summaries.Single(summary => summary.Id == bad.Id) is { Error: not null, Agents.Count: 0 }, "its summary carries the error");

            var working = device.Host.ResolveSession(good.Id);
            var reply = await working.SendAsync(1, new SendRequest("still here?", "iso-1") { Wait = Answer });
            Equal("Echo: still here?", reply.AnswerText, "the other session works");
        }),
    ];
}
