// Decision 0011 (run W4, retest): fixes the W4 retest made where units meet. Settings › Tools' folder browser (GET /api/settings/folders)
// is a GET another website can send, like agent compare's project inspection was before its fix: it now answers only the app's own pages
// (Sec-Fetch-Site same-origin or none), and a network or device path is refused before anything touches it, since listing \\server\share
// would offer the user's Windows credentials to that host. No case calls a provider or reaches the network (the host is .invalid).
// After the W5 live test: This PC waits at most FolderBrowser.DriveWait for drives other than fixed disks, all asked at once, so a drive that
// does not answer (a disconnected network drive) is left out instead of holding the list.
using PiSharp.WebAgent.Settings;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> W4RetestCases() =>
    [
        Case("w4.retest.the-folder-browser-refuses-network-paths-and-other-sites", async () =>
        {
            var root = Temp("w4-retest-folders");
            try
            {
                var tree = Path.Join(root, "tree");
                Directory.CreateDirectory(Path.Join(tree, "repo"));
                var network = new[] { @"\\pisharp-test.invalid\share\project", "//pisharp-test.invalid/share", @"\\?\UNC\pisharp-test.invalid\share", @"\\.\C:\" };
                if (OperatingSystem.IsWindows())
                {
                    foreach (var path in network)
                    {
                        var refused = false;
                        try { FolderBrowser.List(path); }
                        catch (ArgumentException error) when (error.Message == FolderBrowser.NetworkPathMessage) { refused = true; }
                        Check(refused, path + " is refused before it is listed");
                    }
                }
                Equal("repo", FolderBrowser.List(tree).Folders.Single().Name, "a local folder is still listed");

                await using var app = await WebAgentApp.StartAsync(root, new AppStart { DataDirectory = Path.Join(root, "data") });
                async Task<(int Status, string Text)> FetchAsync(string path, string? site)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(app.Base, path));
                    if (site is not null) request.Headers.Add("Sec-Fetch-Site", site);
                    using var response = await app.Http.SendAsync(request);
                    return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
                }
                var local = "/api/settings/folders?path=" + Uri.EscapeDataString(tree);
                Equal(200, (await FetchAsync(local, "same-origin")).Status, "the settings page's own request");
                Equal(200, (await FetchAsync(local, "none")).Status, "a typed address");
                Equal(200, (await FetchAsync(local, null)).Status, "a client that sends no Sec-Fetch-Site (HostGuard's Host check still applies)");
                var crossSite = await FetchAsync(local, "cross-site");
                Equal(403, crossSite.Status, "another website's <img> or no-cors fetch: " + crossSite.Text);
                Check(crossSite.Text.Contains("\"error\"", StringComparison.Ordinal) && !crossSite.Text.Contains("repo", StringComparison.Ordinal),
                    "it says why and lists nothing");
                Equal(403, (await FetchAsync(local, "same-site")).Status, "another port of localhost");
                Equal(403, (await FetchAsync("/api/settings/folders", "cross-site")).Status, "not even the drives");
                if (OperatingSystem.IsWindows())
                {
                    foreach (var path in network)
                    {
                        var answer = await FetchAsync("/api/settings/folders?path=" + Uri.EscapeDataString(path), "same-origin");
                        Equal(400, answer.Status, path + " is refused: " + answer.Text);
                        Check(answer.Text.Contains("network path", StringComparison.Ordinal), path + " says why: " + answer.Text);
                    }
                }
                Check(!app.Log.Contains("fail:", StringComparison.Ordinal), "no server errors: " + app.Log);
            }
            finally
            {
                SqliteClearPools();
                DeleteTemp(root);
            }
        }),

        Case("w4.retest.the-drive-list-does-not-wait-for-a-drive-that-does-not-answer", () =>
        {
            using var hold = new ManualResetEventSlim();
            var asked = 0;
            bool Silent()
            {
                Interlocked.Increment(ref asked);
                hold.Wait();
                return true;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var ready = FolderBrowser.ReadyDrives(
            [
                (@"C:\", true, () => true),
                (@"D:\", true, () => false),
                (@"Y:\", false, Silent),
                (@"Z:\", false, Silent),
                (@"F:\", false, () => true),
                (@"G:\", false, () => throw new InvalidOperationException("device error")),
            ], TimeSpan.FromMilliseconds(500));
            var took = watch.Elapsed;
            hold.Set();
            Equal(@"C:\|F:\", string.Join("|", ready), "the drives that said they are ready in time, in order");
            Check(took < TimeSpan.FromSeconds(3), $"the list waited {took} for two drives that never answered");
            Equal(2, Volatile.Read(ref asked), "both silent drives were asked, at once");
            Equal(@"C:\", string.Join("|", FolderBrowser.ReadyDrives([(@"C:\", true, () => true), (@"G:\", false, () => throw new IOException("gone"))],
                TimeSpan.FromSeconds(5))), "a drive whose check fails is left out once every drive has answered");

            // The real list: the system drive, within the wait (a disconnected network drive held it for seconds before).
            if (OperatingSystem.IsWindows())
            {
                watch.Restart();
                var top = FolderBrowser.List(null);
                Check(top.Folders.Any(folder => folder.Path == Path.GetPathRoot(Environment.SystemDirectory)),
                    "This PC lists the system drive: " + string.Join(", ", top.Folders.Select(folder => folder.Name)));
                Check(watch.Elapsed < FolderBrowser.DriveWait + TimeSpan.FromSeconds(3), $"This PC took {watch.Elapsed}");
            }
        }),
    ];
}
