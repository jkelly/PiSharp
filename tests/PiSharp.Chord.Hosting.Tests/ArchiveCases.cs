// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/archive.ts, packages/chord/src/node/app/tar.ts.
// Authored native regression; not an upstream execution capture.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Chord;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Data;
using PiSharp.Chord.Host;
using PiSharp.Chord.Hosting.App;
using PiSharp.Chord.Hosting.Workers;
using PiSharp.Chord.Store;
using AppArchive = PiSharp.Chord.Hosting.App.Archive;
using AppHostData = PiSharp.Chord.Hosting.App.HostData;
using AppManifest = PiSharp.Chord.Hosting.App.ArchiveManifest;

internal static class ArchiveCases
{
    internal static async Task RunAsync()
    {
        await TarGuardsAsync();
        await SnapshotAndStagingAsync();
        await IdleServiceAsync();
    }

    private static async Task TarGuardsAsync()
    {
        using var archive = new MemoryStream();
        await Tar.WriteAsync(archive, Entries(("nested/hello", "hello"), ("empty", "")));
        archive.Position = 0;
        var names = new List<string>();
        await foreach (var entry in Tar.ReadAsync(archive))
        {
            names.Add(entry.Path);
            using var text = new StreamReader(entry.Data, leaveOpen: true);
            var value = await text.ReadToEndAsync();
            Assert(value == (entry.Path == "empty" ? "" : "hello"), "tar data differs");
        }
        Assert(names.SequenceEqual(["nested/hello", "empty"]), "tar entries differ");
        foreach (var path in new[] { "", "/root", "a//b", "a/../b", "a/./b", "a\\b", "a\0b", "C:/outside" })
        {
            await RefusesAsync(() => Tar.WriteAsync(new MemoryStream(), Entries((path, "x"))));
        }
        var good = archive.ToArray();
        var checksum = (byte[])good.Clone(); checksum[0] ^= 1;
        await RefusesAsync(() => DrainAsync(checksum));
        foreach (var flag in new[] { '1', '2', '3', '4', '6', 'x', 'g', 'L' })
        {
            var bad = (byte[])good.Clone(); bad[156] = (byte)flag; Rechecksum(bad);
            await RefusesAsync(() => DrainAsync(bad));
        }
        var invalidPath = (byte[])good.Clone();
        Array.Clear(invalidPath, 0, 100); Encoding.ASCII.GetBytes("../outside").CopyTo(invalidPath, 0); Rechecksum(invalidPath);
        await RefusesAsync(() => DrainAsync(invalidPath));
        await RefusesAsync(() => DrainAsync(good[..514]));
    }

    private static async Task SnapshotAndStagingAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-archive-" + Guid.NewGuid().ToString("N"));
        Layout.PrivateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source"); Layout.PrivateDirectory(source);
            var files = Path.Combine(source, "files"); Layout.PrivateDirectory(files);
            Layout.PrivateDirectory(Path.Combine(files, "blobs"));
            await File.WriteAllTextAsync(Path.Combine(files, "blobs", "finished"), "immutable");
            Layout.PrivateDirectory(Path.Combine(files, ".uploads"));
            await File.WriteAllTextAsync(Path.Combine(files, ".uploads", "partial"), "not finished");
            await using var store = await Layout.OpenStoreAsync(Path.Combine(source, "store.sqlite"), ChordContext.Background);
            var worker = new HostKind("archive.worker", new() { ["id"] = Schemas.Str() }).Address(("id", "one"));
            var server = new JsonObject { ["kind"] = "server", ["id"] = "one" };
            await using var host = Hosts.Create(new HostOptions
            {
                Address = worker, Principal = server, Store = store, OnError = _ => { },
            });
            await host.LoadAsync([new("archive.data", AppHostData.CreateFacet(store, files, server))], ChordContext.Background);
            var serverArchive = Path.Combine(root, "server.archive");
            await using (var output = File.Create(serverArchive))
                await foreach (var part in Tables.EncodeJsonl(host.ExportHostData(worker, ChordContext.Background))) await output.WriteAsync(part);
            var manifest = new AppManifest(AppArchive.Format, "archive-test", "1", new(worker.Kind, worker.Key));
            var packed = Path.Combine(root, "packed.tar");
            // The private endpoint snapshots the open store under the canonical server principal.
            await host.RootAsync(async context =>
            {
                var reference = await context.GetAsync(AppHostData.Type, new JsonObject { ["address"] = AppHostData.AddressParam(worker) });
                await reference.CallAsync("exportTo", new JsonObject
                {
                    ["path"] = packed, ["manifest"] = JsonSerializer.Serialize(manifest), ["server"] = serverArchive,
                }, context.Context);
                return true;
            }, ChordContext.Background, new RootOptions { Principal = new JsonObject { ["id"] = "one", ["kind"] = "server" } });
            var refused = false;
            try
            {
                await host.RootAsync(async context =>
                {
                    _ = await context.GetAsync(AppHostData.Type, new JsonObject { ["address"] = AppHostData.AddressParam(worker) });
                    return true;
                }, ChordContext.Background, new RootOptions { Principal = new JsonObject { ["kind"] = "visitor" } });
            }
            catch (Exception) { refused = true; }
            Assert(refused, "non-server principal reached the snapshot endpoint");
            var parent = Path.Combine(root, "imports");
            await using (var staged = await AppArchive.UnpackStagingAsync(packed, parent, value => Assert(value == manifest, "manifest differs"), null, ChordContext.Background))
            {
                Assert(File.ReadAllText(Path.Combine(staged.Staging, "files", "blobs", "finished")) == "immutable", "finished blob missing");
                Assert(!Directory.Exists(Path.Combine(staged.Staging, "files", ".uploads")), "unfinished upload exported");
                await using var restored = await Layout.OpenStoreAsync(Path.Combine(staged.Staging, "store.sqlite"), ChordContext.Background);
                Assert(restored.LastSeq == store.LastSeq, "store point differs");
            }
            Assert(!Directory.EnumerateFileSystemEntries(parent).Any(), "staging cleanup leaked");
            await RefusesAsync(async () => { await using var ignored = await AppArchive.UnpackStagingAsync(packed, parent, _ => { }, new(0), ChordContext.Background); });
            Assert(!Directory.EnumerateFileSystemEntries(parent).Any(), "quota failure leaked staging");
            await RefusesAsync(async () => { await using var ignored = await AppArchive.UnpackStagingAsync(packed, parent, _ => { }, new(Entries: 1), ChordContext.Background); });
            var broken = Path.Combine(root, "broken.tar");
            await using (var output = File.Create(broken))
                await Tar.WriteAsync(output, Entries(("manifest.json", JsonSerializer.Serialize(manifest)), ("store.archive", "{}\n"), ("server.archive", "{}\n")));
            try { await using var ignored = await AppArchive.UnpackStagingAsync(broken, parent, _ => { }, null, ChordContext.Background); throw new Exception("broken store was accepted"); }
            catch (ChordError) { }
            Assert(!Directory.EnumerateFileSystemEntries(parent).Any(), "store import failure leaked staging");
            await using (var staged = await AppArchive.UnpackStagingAsync(packed, parent, _ => { }, null, ChordContext.Background))
            {
                var target = Path.Combine(parent, "target");
                staged.CommitTo(target);
                Assert(File.Exists(Path.Combine(target, "store.sqlite")), "commit did not install store");
            }
            Assert(!Directory.EnumerateDirectories(root, ".chord-export-*").Any(), "export staging leaked");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async IAsyncEnumerable<TarEntry> Entries(params (string Path, string Text)[] entries)
    {
        await Task.CompletedTask;
        foreach (var entry in entries)
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(entry.Text));
            yield return new(entry.Path, data.Length, data);
        }
    }

    private static async Task IdleServiceAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "pisharp-archive-service-" + Guid.NewGuid().ToString("N"));
        Layout.PrivateDirectory(root);
        try
        {
            await using var opened = await Layout.OpenAsync(root, ChordContext.Background);
            var layout = opened.Layout;
            await using var store = await Layout.OpenStoreAsync(layout.StoreFile, ChordContext.Background);
            var serverPrincipal = new JsonObject { ["kind"] = "server" };
            await using var host = Hosts.Create(new HostOptions
            {
                Address = new HostKind("archive.server").Address(), Principal = serverPrincipal,
                Store = store, OnError = _ => { },
            });
            var kind = new HostKind("archive.worker", new() { ["id"] = Schemas.Str() });
            await using var workers = new ProcessWorkers("archive.workers", host, kind, new()
            {
                Executable = Environment.ProcessPath!, LockDirectory = Path.Combine(root, "locks"),
                Plugins = (_, _) => throw new Exception("Idle archive unexpectedly started worker"),
                Authorize = (_, _) => true, Principal = _ => new JsonObject { ["kind"] = "worker" },
                OnError = (_, _) => { },
            }, ChordContext.Background);
            await host.LoadAsync([new("archive.workers", workers.Facet)], ChordContext.Background);
            var service = new AppArchiveService("archive-test", "1", layout, host,
                new Dictionary<string, ProcessWorkers> { [kind.Kind] = workers });
            var source = kind.Address(("id", "source"));
            var sourceDirectory = Layout.WorkerDirectory(layout, source);
            await using (var sourceStore = await Layout.OpenStoreAsync(Path.Combine(sourceDirectory, "store.sqlite"), ChordContext.Background)) { }
            Layout.PrivateDirectory(Path.Combine(sourceDirectory, "files", "blobs"));
            await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "files", "blobs", "first"), "first");
            var archive = Path.Combine(root, "worker.tar");
            await service.ExportAsync(source, archive, ChordContext.Background);
            var copy = kind.Address(("id", "copy"));
            var target = Layout.WorkerDirectory(layout, copy);
            await service.ImportAsync(archive, copy, ChordContext.Background);
            Assert(File.ReadAllText(Path.Combine(target, "files", "blobs", "first")) == "first", "idle import did not restore blob");
            var refused = false;
            try { await service.ImportAsync(archive, copy, ChordContext.Background); }
            catch (IOException) { refused = true; }
            Assert(refused, "existing address was overwritten by default");
            await File.WriteAllTextAsync(Path.Combine(target, "files", "blobs", "first"), "changed");
            await service.ImportAsync(archive, copy, ChordContext.Background, new(HostExisting.Replace));
            Assert(File.ReadAllText(Path.Combine(target, "files", "blobs", "first")) == "first", "replace import failed");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var cancelled = false;
            try { await service.ImportAsync(archive, kind.Address(("id", "cancelled")), ChordContext.WithAbortSignal(cancel.Token, ChordContext.Background)); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && !Directory.Exists(Layout.WorkerDirectory(layout, kind.Address(("id", "cancelled")))), "cancelled import published a target");
            await service.DeleteAsync(copy, ChordContext.Background);
            Assert(!Directory.Exists(target), "delete left worker directory");
            Assert(Directory.Exists(sourceDirectory), "copy import changed source directory");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static async Task DrainAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        await foreach (var entry in Tar.ReadAsync(stream)) await entry.Data.CopyToAsync(Stream.Null);
    }
    private static void Rechecksum(byte[] bytes)
    {
        Array.Fill(bytes, (byte)' ', 148, 8);
        var checksum = bytes.Take(512).Sum(value => (int)value);
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ").CopyTo(bytes, 148);
    }
    private static async Task RefusesAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("archive guard accepted invalid input");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
