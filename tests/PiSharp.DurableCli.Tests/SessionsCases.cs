// Authored for PiSharp (upstream packages/coding-agent/src/experimental/durable has no tests): the session directories and the
// proper-lockfile lock of src/PiSharp.Cli/Durable/DurableSessions.cs (upstream sessions.ts), offline in temporary agent directories.
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Cli.Durable;
using PiSharp.Cli.Pi;

internal static partial class Program
{
    private static readonly Regex SessionName = new("^[0-9]{13}-[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[0-9a-f]{4}-[0-9a-f]{12}$");

    private static IEnumerable<(string, Func<Task>)> SessionsCases() =>
    [
        Case("sessions.layout.new-session-path-and-lock", async () =>
        {
            await using var agent = new TestAgent("layout");
            var now = DateTimeOffset.FromUnixTimeMilliseconds(1_791_912_120_624);
            var location = await DurableSessions.SelectAsync(agent.Cwd, false, agent.AgentDir, now: () => now);
            var real = DurableSessions.RealPath(agent.Cwd);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(real)))[..24];
            Equal(Path.Join(agent.AgentDir, "experimental", "durable-sessions", hash), Path.GetDirectoryName(location.Directory), "root");
            Check(SessionName.IsMatch(location.Id) && location.Id.StartsWith("1791912120624-", StringComparison.Ordinal), "name " + location.Id);
            Equal(Path.Join(location.Directory, "session.sqlite"), location.Database, "database");
            Equal(real, location.Cwd, "cwd is the real path");
            Check(location.Created, "created");
            Check(Directory.Exists(location.Directory + ".lock"), "proper-lockfile lock directory beside the session directory");
            await location.ReleaseAsync();
            Check(!Directory.Exists(location.Directory + ".lock"), "released");
            // A small timestamp is padded to 13 digits.
            var early = await DurableSessions.SelectAsync(agent.Cwd, false, agent.AgentDir, now: () => DateTimeOffset.FromUnixTimeMilliseconds(42));
            Check(early.Id.StartsWith("0000000000042-", StringComparison.Ordinal), "padded " + early.Id);
            await early.ReleaseAsync();
        }),
        Case("sessions.layout.realpath-spelling-and-links", async () =>
        {
            await using var agent = new TestAgent("realpath");
            var actual = Path.Combine(agent.Root, "CaseDir");
            Directory.CreateDirectory(actual);
            if (OperatingSystem.IsWindows())
            {
                // Node's realpath reports the on-disk spelling (GetFinalPathNameByHandleW), so both spellings share one session root.
                var lower = Path.Combine(agent.Root, "casedir");
                Equal(DurableSessions.RealPath(actual), DurableSessions.RealPath(lower), "case-folded spelling");
                Check(DurableSessions.RealPath(lower).EndsWith(Path.DirectorySeparatorChar + "CaseDir", StringComparison.Ordinal), "on-disk spelling");
                Check(char.IsUpper(DurableSessions.RealPath(lower.ToLowerInvariant())[0]), "drive letter as the volume reports it");
            }
            var link = Path.Combine(agent.Root, "linked");
            try { Directory.CreateSymbolicLink(link, actual); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; } // No symlink privilege.
            Equal(DurableSessions.RealPath(actual), DurableSessions.RealPath(link), "link resolved");
            var viaLink = await DurableSessions.SelectAsync(link, false, agent.AgentDir);
            Equal(DurableSessions.RealPath(actual), viaLink.Cwd, "session cwd through a link");
            await viaLink.ReleaseAsync();
        }),
        Case("sessions.continue.picks-newest-valid-directory", async () =>
        {
            await using var agent = new TestAgent("newest");
            var root = DurableSessions.RootFor(agent.AgentDir, DurableSessions.RealPath(agent.Cwd));
            string Name(long ms) => $"{ms:D13}-{Guid.NewGuid():D}";
            var older = Name(1_700_000_000_000);
            var newest = Name(1_790_000_000_000);
            foreach (var name in new[] { Name(5), older, newest, "1999999999999-not-a-uuid", "zzzz", "9999999999999" })
                Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, Name(1_800_000_000_000)), "a file, not a session directory");
            var location = await DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir);
            Equal(newest, location.Id, "newest");
            Check(!location.Created, "continued");
            await location.ReleaseAsync();
        }),
        Case("sessions.continue.none-fails", async () =>
        {
            await using var agent = new TestAgent("none");
            var error = await ThrowsAsync<InvalidOperationException>(() => DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir), "continue without sessions");
            Equal($"No durable session exists for {DurableSessions.RealPath(agent.Cwd)}", error.Message, "message");
        }),
        Case("sessions.lock.contention-fails-after-retries", async () =>
        {
            await using var agent = new TestAgent("contention");
            var held = await DurableSessions.SelectAsync(agent.Cwd, false, agent.AgentDir);
            try
            {
                var started = DateTime.UtcNow;
                var error = await ThrowsAsync<DurableSessionLockedException>(() => DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir,
                    new PiLockOptions { Retries = 2, RetryDelay = TimeSpan.FromMilliseconds(200) }), "second open");
                Equal($"Session is already open in another process: {held.Directory}", error.Message, "message");
                Check(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(350), "two retries waited");
            }
            finally { await held.ReleaseAsync(); }
            // Released: the next open takes it.
            var next = await DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir, new PiLockOptions());
            Equal(held.Directory, next.Directory, "reopened");
            await next.ReleaseAsync();
        }),
        Case("sessions.lock.retries-until-released", async () =>
        {
            await using var agent = new TestAgent("retry");
            var held = await DurableSessions.SelectAsync(agent.Cwd, false, agent.AgentDir);
            _ = Task.Delay(700).ContinueWith(_ => held.ReleaseAsync());
            var next = await DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir, new PiLockOptions { Retries = 12, RetryDelay = TimeSpan.FromMilliseconds(250) });
            Equal(held.Directory, next.Directory, "waited for the release");
            await next.ReleaseAsync();
        }),
        Case("sessions.lock.stale-after-10s-is-taken-over", async () =>
        {
            await using var agent = new TestAgent("stale");
            var first = await DurableSessions.SelectAsync(agent.Cwd, false, agent.AgentDir);
            await first.ReleaseAsync();
            // A crashed holder leaves its lock directory: renewed 5 s ago it is live, 11 s ago it is stale (proper-lockfile's 10 s).
            var lockPath = first.Directory + ".lock";
            Directory.CreateDirectory(lockPath);
            Directory.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - TimeSpan.FromSeconds(5));
            await ThrowsAsync<DurableSessionLockedException>(() => DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir, new PiLockOptions()), "live lock");
            Directory.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - TimeSpan.FromSeconds(11));
            var started = DateTime.UtcNow;
            var taken = await DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir, new PiLockOptions());
            Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(2), "taken over at once");
            Check(DateTime.UtcNow - Directory.GetLastWriteTimeUtc(lockPath) < TimeSpan.FromSeconds(3), "the new holder's mtime");
            await taken.ReleaseAsync();
        }),
        Case("sessions.lock.holder-renews-mtime", async () =>
        {
            await using var agent = new TestAgent("renew");
            var held = await DurableSessions.SelectAsync(agent.Cwd, false, agent.AgentDir, new PiLockOptions { Stale = TimeSpan.FromSeconds(2) });
            try
            {
                var lockPath = held.Directory + ".lock";
                var before = Directory.GetLastWriteTimeUtc(lockPath);
                await Task.Delay(2600);
                var after = Directory.GetLastWriteTimeUtc(lockPath);
                Check(after > before, $"renewed: {before:O} -> {after:O}");
                // With a 2 s stale time the renewed lock stays live for others.
                await ThrowsAsync<DurableSessionLockedException>(() => DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir,
                    new PiLockOptions { Stale = TimeSpan.FromSeconds(2) }), "renewed lock is live");
            }
            finally { await held.ReleaseAsync(); }
        }),
        Case("sessions.lock.removed-lock-is-compromised", async () =>
        {
            await using var agent = new TestAgent("compromised");
            var compromised = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var held = await DurableSessions.SelectAsync(agent.Cwd, false, agent.AgentDir,
                new PiLockOptions { Stale = TimeSpan.FromSeconds(2), OnCompromised = error => compromised.TrySetResult(error) });
            Directory.Delete(held.Directory + ".lock");
            await compromised.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(held.Lock.Compromised, "compromised");
            await held.ReleaseAsync();
        }),
        Case("sessions.lock.cross-process", async () =>
        {
            await using var agent = new TestAgent("cross");
            var created = await DurableSessions.SelectAsync(agent.Cwd, false, agent.AgentDir);
            await created.ReleaseAsync();
            // Another process holds the lock (and renews it every second with a 2 s stale time).
            await using (var child = new Child(["--durable-lock-child", agent.AgentDir, agent.Cwd]))
            {
                var line = await child.WaitForLineAsync("LOCKED ", TimeSpan.FromSeconds(60));
                Equal(created.Directory, line["LOCKED ".Length..], "the child locked the same session");
                await Task.Delay(2500);
                var error = await ThrowsAsync<DurableSessionLockedException>(() => DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir,
                    new PiLockOptions { Stale = TimeSpan.FromSeconds(2) }), "held by the other process");
                Check(error.Message.EndsWith(created.Directory, StringComparison.Ordinal), error.Message);
                // Killed: its lock stays behind, stops being renewed and goes stale.
                await child.KillAsync();
            }
            Check(Directory.Exists(created.Directory + ".lock"), "a killed holder leaves its lock");
            var taken = await DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir,
                new PiLockOptions { Stale = TimeSpan.FromSeconds(2), Retries = 10, RetryDelay = TimeSpan.FromMilliseconds(500) });
            Equal(created.Directory, taken.Directory, "taken over once stale");
            await taken.ReleaseAsync();
        }),
        // Pi and PiSharp continue each other's sessions. Runs the pinned upstream sessions.ts under Node when PISHARP_DURABLE_UPSTREAM names a
        // Pi checkout at the pinned commit (with node_modules) and node is on PATH; otherwise it checks nothing.
        Case("sessions.interop.pinned-upstream-under-node", async () =>
        {
            var upstream = Environment.GetEnvironmentVariable("PISHARP_DURABLE_UPSTREAM");
            if (string.IsNullOrEmpty(upstream)) { Console.Error.WriteLine("sessions.interop: PISHARP_DURABLE_UPSTREAM unset; skipped"); return; }
            await using var agent = new TestAgent("interop");
            var sessions = Path.Combine(upstream, "packages", "coding-agent", "src", "experimental", "durable", "sessions.ts");
            var probe = Path.Combine(agent.Root, "probe.ts");
            File.WriteAllText(probe, $$"""
                import { selectSession } from "{{new Uri(sessions).AbsoluteUri}}";
                const [cwd, mode, holdMs] = process.argv.slice(2);
                try {
                	const location = await selectSession(cwd, mode === "continue");
                	console.log(JSON.stringify({ id: location.id, directory: location.directory, cwd: location.cwd }));
                	await new Promise((resolve) => setTimeout(resolve, Number(holdMs)));
                	await location.release();
                } catch (error) {
                	console.log(JSON.stringify({ error: (error as Error).message }));
                }
                """);
            async Task<System.Text.Json.JsonElement> Node(string mode, int holdMs)
            {
                var start = new System.Diagnostics.ProcessStartInfo("node") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var argument in new[] { "--no-warnings", "--conditions=source", "--experimental-strip-types", probe,
                    agent.Cwd.ToLowerInvariant(), mode, holdMs.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
                start.Environment["PI_CODING_AGENT_DIR"] = agent.AgentDir;
                using var process = System.Diagnostics.Process.Start(start)!;
                var line = await process.StandardOutput.ReadLineAsync();
                await process.WaitForExitAsync();
                return System.Text.Json.JsonDocument.Parse(line ?? "{}").RootElement.Clone();
            }
            // Pi creates; PiSharp continues it, from a differently spelled cwd on Windows.
            var created = await Node("new", 0);
            var continued = await DurableSessions.SelectAsync(OperatingSystem.IsWindows() ? agent.Cwd.ToUpperInvariant() : agent.Cwd, true, agent.AgentDir);
            Equal(created.GetProperty("directory").GetString(), continued.Directory, "PiSharp continues Pi's session");
            Equal(created.GetProperty("cwd").GetString(), continued.Cwd, "the same real cwd");
            // While PiSharp holds it, Pi gives up after its retries with upstream's message.
            var refused = await Node("continue", 0);
            Equal($"Session is already open in another process: {continued.Directory}", refused.GetProperty("error").GetString(), "Pi refused");
            await continued.ReleaseAsync();
            // PiSharp creates a newer session; Pi continues it, and while Pi holds it PiSharp is refused.
            await Task.Delay(5);
            var mine = await DurableSessions.SelectAsync(agent.Cwd, false, agent.AgentDir);
            await mine.ReleaseAsync();
            var holding = Node("continue", 3000);
            while (!Directory.Exists(mine.Directory + ".lock") && !holding.IsCompleted) await Task.Delay(50);
            await ThrowsAsync<DurableSessionLockedException>(() => DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir, new PiLockOptions()), "held by Pi");
            Equal(mine.Directory, (await holding).GetProperty("directory").GetString(), "Pi continues PiSharp's session");
            var after = await DurableSessions.SelectAsync(agent.Cwd, true, agent.AgentDir, new PiLockOptions());
            Equal(mine.Directory, after.Directory, "released by Pi");
            await after.ReleaseAsync();
        }),
        Case("sessions.lockfile.settings-step-is-exclusive", () =>
        {
            var root = Temp("lockfile");
            var lockPath = Path.Combine(root, "settings.json.lock");
            Check(PiLockfile.TryAcquire(lockPath, TimeSpan.FromSeconds(10)), "first mkdir");
            Check(!PiLockfile.TryAcquire(lockPath, TimeSpan.FromSeconds(10)), "second mkdir fails while held");
            var error = Throws<IOException>(() => PiLockfile.WithLock(Path.Combine(root, "settings.json"), () => { }, attempts: 3), "held");
            Equal("Lock file is already being held", error.Message, "lockSync message");
            Directory.Delete(lockPath);
            var ran = false;
            PiLockfile.WithLock(Path.Combine(root, "settings.json"), () => ran = Directory.Exists(lockPath));
            Check(ran && !Directory.Exists(lockPath), "held while running, removed after");
        }),
    ];

    /// <summary><c>--durable-lock-child &lt;agent dir&gt; &lt;cwd&gt;</c>: continues the newest session with a 2 s stale time, prints
    /// <c>LOCKED &lt;dir&gt;</c> and holds the lock until killed.</summary>
    internal static async Task<int> RunLockChildAsync(string agentDir, string cwd)
    {
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => Environment.Exit(3), TaskScheduler.Default);
        try
        {
            var location = await DurableSessions.SelectAsync(cwd, true, agentDir, new PiLockOptions { Stale = TimeSpan.FromSeconds(2) });
            Emit("LOCKED " + location.Directory);
            await new TaskCompletionSource().Task;
            return 2;
        }
        catch (Exception error)
        {
            Emit("ERROR " + error.Message.ReplaceLineEndings(" "));
            return 1;
        }
    }
}
