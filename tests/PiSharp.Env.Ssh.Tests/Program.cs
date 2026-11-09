// PiSharp-only: offline subprocess fixtures exercise SSH command construction, integrity and lifecycle.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiSharp.Env;
using PiSharp.Env.Protocol;

internal static class Program
{
    private static string root = "";
    private static int passed;
    public static async Task Main(string[] args)
    {
        if (args.Contains("-T")) { Environment.ExitCode = await Worker(args); return; }
        root = Path.Combine(Path.GetTempPath(), "pisharp-ssh-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("PISHARP_SSH_FIXTURE", root);
        try
        {
            await Case("structured-options-and-quoting", () =>
            {
                var target = Target() with { User = "person", Port = 2222, KnownHostsFile = "C:/host keys/100%", IdentityFile = "key with space" };
                var command = Ssh.Arguments(target);
                Check(command.Contains("StrictHostKeyChecking=yes") && command.Contains("ClearAllForwardings=yes") && command.Contains("ConnectTimeout=15"), "transport restrictions");
                Check(command.Contains("UserKnownHostsFile=\"C:/host keys/100%%\"") && command[^2] == "--" && command[^1] == "fixture", "literal option values and terminator");
                foreach (var host in new[] { "-oBad", "bad host", "x\n" }) Throws<ArgumentException>(() => Ssh.Arguments(target with { Host = host }));
                Check(Ssh.LaunchCommand(new("linux", "x64", "/"), "/tmp/a'b").Contains("'\\''"), "POSIX apostrophe");
                Check(Ssh.LaunchCommand(new("windows", "x64", "C:\\", "powershell"), "C:\\a'b") == "& 'C:\\a''b'", "PowerShell apostrophe");
                Throws<ArgumentException>(() => Ssh.LaunchCommand(new("windows", "x64", "C:\\", "cmd"), "C:\\%USER%\\x"));
                return Task.CompletedTask;
            });
            await Case("manifest-corruption-and-atomic-cache", async () =>
            {
                var (catalog, artifact) = await Catalog();
                var cache = Path.Combine(root, "cache");
                var files = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Bootstrap.InstallAsync(catalog, artifact, cache)));
                Check(files.Distinct().Count() == 1 && (await File.ReadAllBytesAsync(files[0])).SequenceEqual(new byte[] { 0, 1, 255, 13, 10 }), "atomic cache");
                await File.WriteAllTextAsync(files[0], "corrupt");
                await Bootstrap.InstallAsync(catalog, artifact, cache);
                Check(new FileInfo(files[0]).Length == 5 && !Directory.GetFiles(cache, "*.tmp").Any(), "repair and cleanup");
                await File.WriteAllTextAsync(Path.Combine(root, artifact.Path), "changed");
                await ThrowsAsync<InvalidDataException>(() => catalog.ReadVerifiedAsync(artifact));
                Throws<ArgumentException>(() => new DaemonCatalog(root, [artifact with { Path = "../escape" }]));
                Throws<PlatformNotSupportedException>(() => catalog.Select("android", "arm64"));
            });
            await Case("detect-posix-windows-and-android", async () =>
            {
                Mode("linux"); var linux = await Ssh.DetectPlatformAsync(Target()); Check(linux.Platform == "linux" && linux.Arch == "arm64" && linux.Home == "/home/a'b", "Linux probe");
                Mode("windows"); var windows = await Ssh.DetectPlatformAsync(Target()); Check(windows.Platform == "windows" && windows.Shell == "cmd" && windows.Home == "C:\\Users\\A B", "Windows fallback");
                Mode("android"); var android = await Ssh.DetectPlatformAsync(Target()); Check(android.Platform == "android", "Android is detected honestly");
            });
            await Case("verify-upload-cache-and-reject-nohash", async () =>
            {
                var (catalog, artifact) = await Catalog(); var remote = new RemotePlatform("linux", "arm64", "/home/a'b");
                Mode("linux"); await Ssh.DeployDaemonAsync(Target(), remote, catalog, artifact); Check(File.Exists(Path.Combine(root, "uploaded")), "upload ran");
                var upload = ReadLog().Last(); Check(upload.Contains("mktemp") && upload.Contains("mv -f") && upload.Contains("chmod 700") && upload.Contains(artifact.Sha256), "verified atomic deployment command");
                Check((await File.ReadAllBytesAsync(Path.Combine(root, "payload"))).SequenceEqual(new byte[] { 0, 1, 255, 13, 10 }), "raw bytes");
                var before = ReadLog().Length; await Ssh.DeployDaemonAsync(Target(), remote, catalog, artifact); Check(ReadLog().Length == before + 1, "cache still verified and upload skipped");
                Mode("nohash"); await ThrowsAsync<InvalidOperationException>(() => Ssh.DeployDaemonAsync(Target(), remote, catalog, artifact));
                Mode("upload-fail"); var error = await ThrowsAsync<SshException>(() => Ssh.DeployDaemonAsync(Target(), remote, catalog, artifact)); Check(error.ExitCode == 7, "upload failure propagated");
            });
            await Case("windows-encoded-upload", async () =>
            {
                var (catalog, artifact) = await Catalog("windows", "x64"); Mode("windows");
                await Ssh.DeployDaemonAsync(Target(), new("windows", "x64", "C:\\Users\\A B", "cmd"), catalog, artifact);
                var command = ReadLog().Last(); Check(command.Contains("FromBase64String") && command.Contains("Get-FileHash") && command.Contains("Move-Item"), "Windows verified upload");
                var input = await File.ReadAllTextAsync(Path.Combine(root, "payload")); Check(input.EndsWith("\nPI-ENV-END\n") && Convert.FromBase64String(input.Split("PI-ENV-END")[0]).Length == 5, "Windows base64 input marker");
            });
            await Case("trust-and-sanitized-timeout-errors", async () =>
            {
                Mode("unknown"); await ThrowsAsync<HostKeyUnknownException>(() => Ssh.DetectPlatformAsync(Target()));
                Mode("changed"); await ThrowsAsync<HostKeyChangedException>(() => Ssh.DetectPlatformAsync(Target()));
                Mode("stderr"); var error = await ThrowsAsync<SshException>(() => Ssh.DetectPlatformAsync(Target())); Check(!error.Diagnostics.Contains('\x1b') && error.Diagnostics.Length <= 2000 && error.Diagnostics.Split('\n').Length <= 20, "bounded sanitized diagnostics");
                Mode("timeout"); using var cancel = new CancellationTokenSource(); var watch = Stopwatch.StartNew();
                var pending = Ssh.DetectPlatformAsync(Target(), cancel.Token);
                var pidFile = Path.Combine(root, "pid");
                while (!File.Exists(pidFile) && watch.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
                Check(File.Exists(pidFile), "timeout child actually started");
                var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
                watch.Restart(); cancel.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => pending); Check(watch.Elapsed < TimeSpan.FromSeconds(3), "cancel reaps process");
                try { using var child = Process.GetProcessById(pid); Check(child.HasExited, "cancel joins actual child exit"); } catch (ArgumentException) { /* Reaped processes disappear from the table. */ }
                watch.Restart(); await ThrowsAsync<SshException>(() => Ssh.DetectPlatformAsync(Target() with { CommandTimeout = TimeSpan.FromMilliseconds(100) }));
                Check(watch.Elapsed < TimeSpan.FromSeconds(3), "transport command deadline");
            });
            await Case("host-key-management", async () =>
            {
                Mode("linux"); var scan = await Ssh.ScanHostKeyAsync(Target()); Check(scan.Lines.Count == 1 && scan.Fingerprints[0].StartsWith("SHA256:"), "scan isolated");
                Check(!File.Exists(Target().KnownHostsFile), "scan does not trust");
                await ThrowsAsync<ArgumentException>(() => Ssh.AcceptHostKeyAsync(Target(), ["fixed-alias ssh-ed25519 Zm9v\nother ssh-ed25519 YmFy"]));
                await Task.WhenAll(Ssh.AcceptHostKeyAsync(Target(), scan.Lines), Ssh.AcceptHostKeyAsync(Target(), scan.Lines));
                Check((await File.ReadAllLinesAsync(Target().KnownHostsFile)).Length == 1, "atomic idempotent trust");
                await ThrowsAsync<HostKeyChangedException>(() => Ssh.AcceptHostKeyAsync(Target(), ["fixed-alias ssh-ed25519 YmFy"]));
                await Ssh.ForgetHostKeyAsync(Target()); Check(new FileInfo(Target().KnownHostsFile).Length == 0, "forget");
            });
            await Case("lazy-bootstrap-handshake-reconnect", async () =>
            {
                var (catalog, _) = await Catalog(); Mode("linux"); var before = ReadLog().Length;
                await using var ssh = new SshConnection(new() { Host = "fixture", HostKeyAlias = "fixed-alias", KnownHostsFile = Target().KnownHostsFile, Ssh = Target().Ssh, Catalog = catalog });
                Check(ReadLog().Length == before, "construction no network");
                var verificationCount = ReadLog().Count(line => line.Contains("hash()"));
                Check((await ssh.Connection.InfoAsync()).Version == "1.5.0", "protocol hello");
                var session = await ssh.Connection.SessionAsync(); await ThrowsAsync<RemoteError>(() => ssh.Connection.RequestAsync("die", new()));
                Check(await ssh.Connection.SessionAsync() != session, "restart");
                Check(ReadLog().Count(line => line.Contains("hash()")) == verificationCount + 3, "verification before reconnect");
            });
            await Case("verified-artifact-hello-version-mismatch", async () =>
            {
                var (catalog, _) = await Catalog(); Mode("version-mismatch");
                await using var ssh = new SshConnection(new() { Host = "fixture", HostKeyAlias = "fixed-alias", KnownHostsFile = Target().KnownHostsFile, Ssh = Target().Ssh, Catalog = catalog });
                var error = await ThrowsAsync<RemoteError>(() => ssh.Connection.InfoAsync()); Check(error.Message.Contains("version"), "catalog version pinned at handshake");
            });
            Console.WriteLine($"Passed {passed} offline SSH cases.");
        }
        finally { Environment.SetEnvironmentVariable("PISHARP_SSH_FIXTURE", null); Directory.Delete(root, true); }
    }

    private static SshTarget Target() => new() { Host = "fixture", HostKeyAlias = "fixed-alias", KnownHostsFile = Path.Combine(root, "known_hosts"), Ssh = Path.Combine(AppContext.BaseDirectory, "PiSharp.Env.Ssh.Tests" + (OperatingSystem.IsWindows() ? ".exe" : "")) };
    private static async Task<(DaemonCatalog, DaemonArtifact)> Catalog(string platform = "linux", string arch = "arm64")
    {
        byte[] bytes = [0, 1, 255, 13, 10]; await File.WriteAllBytesAsync(Path.Combine(root, "daemon"), bytes);
        var artifact = new DaemonArtifact(platform, arch, "1.5.0", "daemon", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return (new(root, [artifact]), artifact);
    }
    private static void Mode(string mode) { File.WriteAllText(Path.Combine(root, "mode"), mode); File.Delete(Path.Combine(root, "uploaded")); File.Delete(Path.Combine(root, "pid")); }
    private static string[] ReadLog() => File.Exists(Path.Combine(root, "log")) ? File.ReadAllLines(Path.Combine(root, "log")).Select(line => JsonSerializer.Deserialize<string>(line)!).ToArray() : [];
    private static async Task Case(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }

    private static async Task<int> Worker(string[] args)
    {
        root = Environment.GetEnvironmentVariable("PISHARP_SSH_FIXTURE") ?? throw new Exception("Missing fixture");
        var mode = File.ReadAllText(Path.Combine(root, "mode"));
        var command = args[Array.IndexOf(args, "fixture") + 1];
        if (command.StartsWith("powershell ")) command = Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ')[^1]));
        await File.AppendAllTextAsync(Path.Combine(root, "log"), JsonSerializer.Serialize(command) + "\n");
        if (mode == "unknown") { Console.Error.WriteLine("Host key verification failed"); return 255; }
        if (mode == "changed") { Console.Error.WriteLine("REMOTE HOST IDENTIFICATION HAS CHANGED"); return 255; }
        if (mode == "stderr") { for (var i = 0; i < 100; i++) Console.Error.WriteLine("\x1b[31mline" + i + new string('x', 100) + "\x1b[0m"); return 3; }
        if (mode == "timeout")
        {
            await File.WriteAllTextAsync(Path.Combine(root, "pid.tmp"), Environment.ProcessId.ToString());
            File.Move(Path.Combine(root, "pid.tmp"), Path.Combine(root, "pid"), true);
            await Task.Delay(30000); return 0;
        }
        if (args.Contains("StrictHostKeyChecking=accept-new"))
        {
            var setting = args.Single(a => a.StartsWith("UserKnownHostsFile=")); var file = setting.Split('=', 2)[1].Trim('"').Replace("%%", "%");
            await File.WriteAllTextAsync(file, "fixed-alias ssh-ed25519 Zm9v\n"); return 0;
        }
        if (command.Contains("PI-ENV-PROBE"))
        {
            if (mode == "windows" && command.StartsWith("sh ")) return 127;
            Console.WriteLine(mode == "windows" ? "banner\nPI-ENV-PROBE\nWindows\nAMD64\n-\nC:\\Users\\A B\n-\n-" : "banner\nPI-ENV-PROBE\nLinux\naarch64\n" + (mode == "android" ? "Android" : "GNU/Linux") + "\n/home/a'b\n/tmp\n-"); return 0;
        }
        if (command == "echo %OS%") { Console.WriteLine("Windows_NT"); return 0; }
        if (args.Contains("serve"))
        {
            var token = args[Array.IndexOf(args, "--token") + 1]; var output = Console.OpenStandardOutput();
            await output.WriteAsync(Encoding.UTF8.GetBytes("profile noise\nPI-ENV " + token + "\n")); await output.FlushAsync();
            while (await FrameCodec.ReadAsync(Console.OpenStandardInput()) is { } frame)
            {
                if (frame.Type == FrameType.Ping) continue;
                if (frame.Json["op"]?.GetValue<string>() == "die") return 4;
                await FrameCodec.WriteAsync(output, new(FrameType.Result, frame.Id, new() { ["protocol"] = 1, ["version"] = mode == "version-mismatch" ? "old" : "1.5.0", ["os"] = "linux", ["arch"] = "aarch64", ["home"] = "/home/a'b", ["tmpdir"] = "/tmp", ["separator"] = "/", ["cwd"] = "/", ["pid"] = Environment.ProcessId }));
            }
            return 0;
        }
        if (command.Contains("Move-Item") || command.Contains("mktemp"))
        {
            using var payload = new MemoryStream(); await Console.OpenStandardInput().CopyToAsync(payload); await File.WriteAllBytesAsync(Path.Combine(root, "payload"), payload.ToArray());
            if (mode == "upload-fail") { Console.Error.WriteLine("upload failed"); return 7; }
            await File.WriteAllTextAsync(Path.Combine(root, "uploaded"), "yes"); Console.WriteLine("deployed"); return 0;
        }
        Console.WriteLine(mode == "nohash" ? "nohash" : File.Exists(Path.Combine(root, "uploaded")) ? "present" : "missing"); return 0;
    }
}
