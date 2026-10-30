using PiSharp.Durable.Hosting;

// Decision 0011 (run W4, fix 2): DurableDeviceHost.DeleteSessionAsync, which the Web Agent's agent compare runner uses to remove its models'
// sessions when a run is deleted (History's delete, Clear history, the retention). Unlike an archive nothing is kept, and a delete a crash
// interrupted finishes when the device opens again.
internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> SessionDeleteCases() =>
    [
        Case("catalog.delete-removes-the-files-and-every-row", async () =>
        {
            await using var device = await TestDevice.OpenAsync("catalog-delete");
            var keep = await device.CreateAsync("keep");
            var doomed = await device.CreateAsync("doomed", spawnKey: "spawn-del");
            await doomed.RenameAgentAsync(1, "Planner");
            var reply = await doomed.SendAsync(1, new SendRequest("hello", "del-1") { Wait = Answer });
            Equal("Echo: hello", reply.AnswerText, "the session worked");
            DeleteMarker(device.Work);
            Equal(3L, device.Host.Catalog.RowsFor(doomed.Id), "session, agent name and spawn rows");

            await device.Host.DeleteSessionAsync(doomed.Id);
            Check(!Directory.Exists(doomed.DirectoryPath), "the session directory is gone");
            Equal(0L, device.Host.Catalog.RowsFor(doomed.Id), "catalog rows");
            Throws<HostNotFoundException>(() => device.Host.ResolveSession(doomed.Id), "the deleted session");
            Equal(keep.Id, device.Host.Sessions.Single().Id, "the other session stays");
            Check(!Directory.Exists(Path.Join(device.Data, "archive")) || !Directory.EnumerateFileSystemEntries(Path.Join(device.Data, "archive")).Any(),
                "nothing was archived");
            Check(FolderEmpty(Path.Join(device.Data, "trash")), "nothing is left in trash/");
            Check(File.Exists(Path.Join(device.Work, "marker.txt")), "its working directory is untouched");
            await ThrowsAsync<HostNotFoundException>(() => device.Host.DeleteSessionAsync(doomed.Id), "deleting it again");

            await device.ReopenAsync();
            Equal(keep.Id, device.Host.Sessions.Single().Id, "only the kept session reopens");
            var respawned = await device.CreateAsync("new", spawnKey: "spawn-del");
            Check(respawned.Id != doomed.Id, "a deleted session's spawn key is free again");
        }),

        Case("catalog.a-delete-a-crash-interrupted-finishes-at-the-next-open", async () =>
        {
            await using var device = await TestDevice.OpenAsync("catalog-delete-crash");
            var keep = await device.CreateAsync("keep");
            var moved = await device.CreateAsync("moved");
            var marked = await device.CreateAsync("marked");
            await device.Host.DisposeAsync();

            // "moved": the crash came after its folder went to trash/, before its rows were removed. "marked": right after the delete was
            // recorded, before anything moved. A leftover folder in trash/ (a file that was still held) goes too.
            var trash = Path.Join(device.Data, "trash");
            Directory.CreateDirectory(trash);
            var movedTarget = Path.Join(trash, moved.Id + "-crash");
            Directory.Move(moved.DirectoryPath, movedTarget);
            var leftover = Path.Join(trash, "leftover");
            Directory.CreateDirectory(Path.Join(leftover, "sub"));
            File.WriteAllText(Path.Join(leftover, "sub", "agent.sqlite"), "x");
            using (var catalog = SessionCatalog.Open(Path.Join(device.Data, "catalog.sqlite")))
            {
                catalog.BeginArchive(moved.Id, movedTarget);
                catalog.BeginArchive(marked.Id, Path.Join(trash, marked.Id + "-crash"));
            }

            await device.ReopenAsync();
            Equal(keep.Id, device.Host.Sessions.Single().Id, "both interrupted deletes finished");
            Check(!Directory.Exists(marked.DirectoryPath), "the marked session's folder is gone");
            Equal(0L, device.Host.Catalog.RowsFor(moved.Id) + device.Host.Catalog.RowsFor(marked.Id), "their rows are gone");
            Check(FolderEmpty(trash), "trash/ is empty");
        }),
    ];

    private static void DeleteMarker(string work) => File.WriteAllText(Path.Join(work, "marker.txt"), "work");

    private static bool FolderEmpty(string folder) => !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();
}
