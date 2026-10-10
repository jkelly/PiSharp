using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using PiSharp.Cli.Pi;

// utils/tools-manager.ts (getToolPath, ensureTool, getLatestVersion, downloadTool, fetchWithRetry) against a fake GitHub release server,
// and core/tools/grep.ts / core/tools/find.ts through the Pi entry (the binary is ensured on first use).
internal static partial class Program
{
    /// <summary>A fake github.com: routes by absolute URL (each route answers in turn, the last one repeats), 404 otherwise.</summary>
    private sealed class Releases : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];
        public Dictionary<string, Queue<Func<HttpResponseMessage>>> Routes { get; } = new(StringComparer.Ordinal);
        public Releases Route(string url, params Func<HttpResponseMessage>[] answers) { Routes[url] = new(answers); return this; }
        public Releases Latest(string repo, string tag) => Route($"https://gh.test/{repo}/releases/latest",
            () => Redirect($"https://gh.test/{repo}/releases/tag/{tag}"));
        public Releases Asset(string repo, string tag, string asset, byte[] archive) =>
            Route($"https://gh.test/{repo}/releases/download/{tag}/{asset}", () => Redirect($"https://objects.test/{asset}"))
            .Route($"https://objects.test/{asset}", () => new(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Seen) Seen.Add(url);
            if (!Routes.TryGetValue(url, out var answers)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(answers.Count > 1 ? answers.Dequeue()() : answers.Peek()());
        }
        public static HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri(location);
            return response;
        }
    }

    private static byte[] Zip(string entry, byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry(entry).Open();
            stream.Write(content);
        }
        return buffer.ToArray();
    }

    private static byte[] TarGz(string entry, byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, entry[..entry.LastIndexOf('/')] + "/"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, entry)
            { DataStream = new MemoryStream(content), Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        }
        return buffer.ToArray();
    }

    private static string ExecutableSuffix => OperatingSystem.IsWindows() ? ".exe" : "";

    /// <summary>The first executable called <paramref name="name"/> on this process's PATH.</summary>
    private static string? OnProcessPath(string name) => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(directory => Path.Combine(directory.Trim('"'), name + ExecutableSuffix))
        .FirstOrDefault(File.Exists);

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static IEnumerable<(string, Func<Task>)> ToolsManagerCases() =>
    [
        ("tools-manager.download-resolves-latest-follows-redirects-retries-and-extracts-into-bin", async () =>
        {
            using var sandbox = new Sandbox("tools-download");
            var bin = Path.Combine(sandbox.AgentDir, "bin");
            var statuses = new List<string>();
            void Status(PiToolStatus status) => statuses.Add(status.Type + ":" + status.Message);
            // ripgrep on linux/x64: the latest tag has no prefix; the first asset attempt is a retryable 503.
            var releases = new Releases().Latest("BurntSushi/ripgrep", "14.1.1")
                .Route("https://gh.test/BurntSushi/ripgrep/releases/download/14.1.1/ripgrep-14.1.1-x86_64-unknown-linux-musl.tar.gz",
                    () => new(HttpStatusCode.ServiceUnavailable), () => Releases.Redirect("https://objects.test/rg.tar.gz"))
                .Route("https://objects.test/rg.tar.gz", () => new(HttpStatusCode.OK)
                { Content = new ByteArrayContent(TarGz("ripgrep-14.1.1-x86_64-unknown-linux-musl/rg", "RG-LINUX"u8.ToArray())) });
            var linux = new PiToolsManager(bin, _ => null, () => releases, "https://gh.test") { Platform = "linux", Architecture = "x64" };
            var rg = await linux.EnsureToolAsync("rg", Status, CancellationToken.None);
            Equal(Path.Join(bin, "rg"), rg, "binary path");
            Equal("RG-LINUX", File.ReadAllText(rg!), "extracted binary");
            Names(["https://gh.test/BurntSushi/ripgrep/releases/latest",
                "https://gh.test/BurntSushi/ripgrep/releases/download/14.1.1/ripgrep-14.1.1-x86_64-unknown-linux-musl.tar.gz",
                "https://gh.test/BurntSushi/ripgrep/releases/download/14.1.1/ripgrep-14.1.1-x86_64-unknown-linux-musl.tar.gz",
                "https://objects.test/rg.tar.gz"], releases.Seen, "requests");
            Names(["info:ripgrep not found. Downloading...", "info:ripgrep installed to " + Path.Join(bin, "rg")], statuses, "status messages");
            Names(["rg"], Directory.EnumerateFileSystemEntries(bin).Select(Path.GetFileName)!, "archive and extraction directory removed");
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(rg!).HasFlag(UnixFileMode.UserExecute), "chmod 755");
            releases.Seen.Clear();
            Equal(rg, await linux.EnsureToolAsync("rg", Status, CancellationToken.None), "the bin copy is reused");
            Equal(0, releases.Seen.Count, "no request once installed");

            // fd on darwin/x64 is pinned to 10.3.0 (no latest lookup); fd on win32/arm64 strips the v of the latest tag.
            var fdBin = Path.Combine(sandbox.Root, "bin-fd");
            releases = new Releases().Asset("sharkdp/fd", "v10.3.0", "fd-v10.3.0-x86_64-apple-darwin.tar.gz",
                TarGz("fd-v10.3.0-x86_64-apple-darwin/fd", "FD-DARWIN"u8.ToArray()));
            var darwin = new PiToolsManager(fdBin, _ => null, () => releases, "https://gh.test") { Platform = "darwin", Architecture = "x64" };
            Equal("FD-DARWIN", File.ReadAllText((await darwin.EnsureToolAsync("fd", null, CancellationToken.None))!), "darwin fd");
            Check(!releases.Seen.Any(url => url.EndsWith("/releases/latest", StringComparison.Ordinal)), "fd 10.3.0 is pinned on darwin/x64");
            var winBin = Path.Combine(sandbox.Root, "bin-win");
            releases = new Releases().Latest("sharkdp/fd", "v10.2.0").Asset("sharkdp/fd", "v10.2.0", "fd-v10.2.0-aarch64-pc-windows-msvc.zip",
                Zip("fd-v10.2.0-aarch64-pc-windows-msvc/fd.exe", "FD-WIN"u8.ToArray()));
            var windows = new PiToolsManager(winBin, _ => null, () => releases, "https://gh.test") { Platform = "win32", Architecture = "arm64" };
            var fd = await windows.EnsureToolAsync("fd", null, CancellationToken.None);
            Equal(Path.Join(winBin, "fd.exe"), fd, "win32 binary name");
            Equal("FD-WIN", File.ReadAllText(fd!), "win32 fd");
        }),
        ("tools-manager.offline-path-and-bin-precedence-and-download-failures", async () =>
        {
            using var sandbox = new Sandbox("tools-lookup");
            var bin = Path.Combine(sandbox.AgentDir, "bin");
            var path = Path.Combine(sandbox.Root, "path-dir");
            var env = new Dictionary<string, string?>(StringComparer.Ordinal) { ["PI_OFFLINE"] = "1" };
            var releases = new Releases();
            var manager = new PiToolsManager(bin, name => env.GetValueOrDefault(name), () => releases, "https://gh.test");
            var statuses = new List<string>();
            Equal(null, await manager.EnsureToolAsync("fd", status => statuses.Add(status.Type + ":" + status.Message), CancellationToken.None), "offline");
            Names(["warning:fd not found. Offline mode enabled, skipping download."], statuses, "offline status");
            Equal(0, releases.Seen.Count, "offline sends nothing");
            // PATH: the system names in order (fd, then fdfind as Debian names it).
            var fdfind = sandbox.Write(Path.Combine(path, "fdfind" + ExecutableSuffix), "system fd"); MakeExecutable(fdfind);
            env["PATH"] = path;
            Equal(fdfind, await manager.EnsureToolAsync("fd", null, CancellationToken.None), "fdfind on PATH");
            var local = sandbox.Write(Path.Combine(bin, "fd" + ExecutableSuffix), "managed fd"); MakeExecutable(local);
            Equal(local, manager.GetToolPath("fd"), "the managed bin copy wins over PATH");
            // A latest page without a redirect, and an asset that answers 404, fail with upstream's messages and leave nothing behind.
            env.Remove("PI_OFFLINE"); statuses.Clear();
            releases.Route("https://gh.test/BurntSushi/ripgrep/releases/latest", () => new(HttpStatusCode.OK));
            Equal(null, await manager.EnsureToolAsync("rg", status => statuses.Add(status.Type + ":" + status.Message), CancellationToken.None), "no redirect");
            Names(["info:ripgrep not found. Downloading...",
                "warning:Failed to download ripgrep: Failed to resolve latest BurntSushi/ripgrep release: HTTP 200 without redirect"], statuses, "failure status");
            statuses.Clear();
            var linux = new PiToolsManager(Path.Combine(sandbox.Root, "bin-404"), _ => null, () => releases, "https://gh.test") { Platform = "linux", Architecture = "arm64" };
            releases.Latest("BurntSushi/ripgrep", "15.0.0");
            Equal(null, await linux.EnsureToolAsync("rg", status => statuses.Add(status.Type + ":" + status.Message), CancellationToken.None), "missing asset");
            Equal("warning:Failed to download ripgrep: Download failed with HTTP 404: https://gh.test/BurntSushi/ripgrep/releases/download/15.0.0/ripgrep-15.0.0-aarch64-unknown-linux-musl.tar.gz",
                statuses[^1], "asset failure");
            Check(!Directory.Exists(Path.Combine(sandbox.Root, "bin-404")) || !Directory.EnumerateFileSystemEntries(Path.Combine(sandbox.Root, "bin-404")).Any(), "nothing left behind");
        }),
        ("tools.grep-and-find-registered-in-the-pi-entry-report-missing-binaries-offline", async () =>
        {
            using var sandbox = new Sandbox("search-offline");
            sandbox.Vars["PI_OFFLINE"] = "1";
            sandbox.Respond = (_, index) => index switch
            {
                0 => AnthropicToolCall("grep", new { pattern = "needle" }, "toolu_g"),
                1 => AnthropicToolCall("find", new { pattern = "*.txt" }, "toolu_f"),
                _ => AnthropicText("done")
            };
            var (code, _, stderr) = await sandbox.Run("-p", "--tools", "grep,find", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "search");
            Equal(0, code, "exit; " + stderr);
            Names(["grep", "find"], sandbox.Requests[0].Json.GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!), "tools offered");
            Check(ToolResultText(sandbox.Requests[1]).Contains("ripgrep (rg) is not available and could not be downloaded", StringComparison.Ordinal), "grep error: " + ToolResultText(sandbox.Requests[1]));
            Check(ToolResultText(sandbox.Requests[2]).Contains("fd is not available and could not be downloaded", StringComparison.Ordinal), "find error: " + ToolResultText(sandbox.Requests[2]));
        }),
        ("tools.grep-and-find-download-missing-binaries-on-first-use-and-search", async () =>
        {
            // The release server hands out this machine's own rg and fd, packed as the platform's release assets.
            if (OnProcessPath("rg") is not { } realRg || (OnProcessPath("fd") ?? OnProcessPath("fdfind")) is not { } realFd)
                throw new SkipException("rg and fd are not on this machine's PATH.");
            using var sandbox = new Sandbox("search-download");
            var probe = new PiToolsManager(sandbox.Root, _ => null);
            byte[] Pack(string tool, string version, string binary)
            {
                var asset = PiToolsManager.Tools[tool].AssetName(version, probe.Platform, probe.Architecture)!;
                var folder = asset.EndsWith(".zip", StringComparison.Ordinal) ? asset[..^4] : asset[..^7];
                var entry = folder + "/" + PiToolsManager.Tools[tool].BinaryName + ExecutableSuffix;
                return asset.EndsWith(".zip", StringComparison.Ordinal) ? Zip(entry, File.ReadAllBytes(binary)) : TarGz(entry, File.ReadAllBytes(binary));
            }
            var fdVersion = probe.Platform == "darwin" && probe.Architecture == "x64" ? "10.3.0" : "10.2.0";
            var releases = new Releases()
                .Latest("BurntSushi/ripgrep", "14.1.1").Asset("BurntSushi/ripgrep", "14.1.1", PiToolsManager.Tools["rg"].AssetName("14.1.1", probe.Platform, probe.Architecture)!, Pack("rg", "14.1.1", realRg))
                .Latest("sharkdp/fd", "v" + fdVersion).Asset("sharkdp/fd", "v" + fdVersion, PiToolsManager.Tools["fd"].AssetName(fdVersion, probe.Platform, probe.Architecture)!, Pack("fd", fdVersion, realFd));
            sandbox.Write(Path.Combine(sandbox.Cwd, "notes.txt"), "first line\nthe needle is here\n");
            sandbox.Write(Path.Combine(sandbox.Cwd, "src", "other.md"), "nothing to see\n");
            sandbox.Respond = (_, index) => index switch
            {
                0 => AnthropicToolCall("grep", new { pattern = "needle" }, "toolu_g"),
                1 => AnthropicToolCall("find", new { pattern = "*.txt" }, "toolu_f"),
                _ => AnthropicText("done")
            };
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null) with { ToolsHttp = () => releases, ToolsReleaseBase = "https://gh.test" };
            var code = await PiCommand.RunAsync(["-p", "--tools", "grep,find", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "search"], host, CancellationToken.None);
            Equal(0, code, "exit; " + stderr);
            var grep = ToolResultText(sandbox.Requests[1]); var find = ToolResultText(sandbox.Requests[2]);
            Check(grep.Contains("notes.txt:2: the needle is here", StringComparison.Ordinal), "grep result: " + grep);
            Check(find.Contains("notes.txt", StringComparison.Ordinal) && !find.Contains("other.md", StringComparison.Ordinal), "find result: " + find);
            var bin = Path.Combine(sandbox.AgentDir, "bin");
            Names(["fd" + ExecutableSuffix, "rg" + ExecutableSuffix], Directory.EnumerateFiles(bin).Select(Path.GetFileName).Order(StringComparer.Ordinal)!, "installed into <agentDir>/bin");
            Check(!Directory.Exists(Path.Combine(sandbox.Cwd, ".pisharp-search")), "per-search spill root removed");
            Equal("", stderr.ToString(), "print mode stays silent about downloads");
        }),
        // grep.ts, find.ts and ls.ts have no workspace bounds: under the pi tool policy they search any path, read context lines from
        // any file, list linked entries wherever they point, and grep stops ripgrep at the match limit.
        ("tools.pi-policy-grep-find-and-ls-reach-outside-the-cwd", async () =>
        {
            if (OnProcessPath("rg") is not { } realRg || (OnProcessPath("fd") ?? OnProcessPath("fdfind")) is not { } realFd)
                throw new SkipException("rg and fd are not on this machine's PATH.");
            using var sandbox = new Sandbox("search-outside");
            var bin = Path.Combine(sandbox.AgentDir, "bin"); Directory.CreateDirectory(bin);
            File.Copy(realRg, Path.Combine(bin, "rg" + ExecutableSuffix)); File.Copy(realFd, Path.Combine(bin, "fd" + ExecutableSuffix));
            MakeExecutable(Path.Combine(bin, "rg" + ExecutableSuffix)); MakeExecutable(Path.Combine(bin, "fd" + ExecutableSuffix));
            sandbox.Write(Path.Combine(sandbox.Root, "outside", "far.txt"), "alpha\nneedle far\nomega\n");
            sandbox.Write(Path.Combine(sandbox.Cwd, "many.txt"), string.Concat(Enumerable.Repeat("hit\n", 50)));
            var linked = false;
            try { Directory.CreateSymbolicLink(Path.Combine(sandbox.Cwd, "link"), Path.Combine(sandbox.Root, "outside")); linked = true; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            sandbox.Respond = (_, index) => index switch
            {
                0 => AnthropicToolCall("grep", new { pattern = "needle", path = "../outside", context = 1 }, "toolu_g"),
                1 => AnthropicToolCall("find", new { pattern = "*.txt", path = "../outside" }, "toolu_f"),
                2 => AnthropicToolCall("ls", new { path = "." }, "toolu_l"),
                3 => AnthropicToolCall("grep", new { pattern = "hit", limit = 3 }, "toolu_m"),
                _ => AnthropicText("done")
            };
            var (code, _, stderr) = await sandbox.Run("-p", "--tools", "grep,find,ls", "--provider", "anthropic", "--model", "claude-sonnet-4-5", "search");
            Equal(0, code, "exit; " + stderr);
            static string Content(Seen request)
            {
                var messages = request.Json.GetProperty("messages");
                return messages[messages.GetArrayLength() - 1].GetProperty("content")[0].GetProperty("content").GetString()!;
            }
            Equal("far.txt-1- alpha\nfar.txt:2: needle far\nfar.txt-3- omega", Content(sandbox.Requests[1]), "grep outside the cwd with context");
            Equal("far.txt", Content(sandbox.Requests[2]), "find outside the cwd");
            var ls = Content(sandbox.Requests[3]);
            Check(ls.Contains("many.txt", StringComparison.Ordinal) && (!linked || ls.Contains("link/", StringComparison.Ordinal)), "ls lists the outside link: " + ls);
            var limited = Content(sandbox.Requests[4]);
            Check(limited == "many.txt:1: hit\nmany.txt:2: hit\nmany.txt:3: hit\n\n[3 matches limit reached. Use limit=6 for more, or refine pattern]", "grep limit: " + limited);
        }),
    ];
}
