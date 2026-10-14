using PiSharp.Cli.Pi;

// proper-lockfile's lockSync as Pi's settings, interactive settings and project-trust stores use it (PiLockfile): the lock is an exclusive
// mkdir of <file>.lock, so two holders never overlap. The previous ports checked Directory.Exists and then called
// Directory.CreateDirectory, which succeeds on an existing directory, so two processes or threads could both "take" the lock.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> LockfileCases() =>
    [
        ("lockfile.mkdir-is-exclusive-and-a-held-lock-is-refused", Sync(() =>
        {
            using var sandbox = new Sandbox("lockfile-held");
            var file = Path.Combine(sandbox.Root, "settings.json");
            var lockPath = PiLockfile.LockPath(file);
            Equal(true, PiLockfile.MakeDirectory(lockPath), "first mkdir creates the lock");
            Equal(false, PiLockfile.MakeDirectory(lockPath), "second mkdir reports EEXIST");
            Equal(false, PiLockfile.TryAcquire(lockPath, TimeSpan.FromSeconds(10)), "a fresh lock is held");
            var ran = false;
            var error = Throws<IOException>(() => PiLockfile.WithLock(file, () => ran = true), "held lock");
            Equal("Lock file is already being held", error.Message, "proper-lockfile ELOCKED message");
            Check(!ran && Directory.Exists(lockPath), "the holder's lock is left in place");
        })),
        ("lockfile.a-stale-lock-is-taken-over-and-removed-after-the-run", Sync(() =>
        {
            using var sandbox = new Sandbox("lockfile-stale");
            var file = Path.Combine(sandbox.Root, "settings.json");
            var lockPath = PiLockfile.LockPath(file);
            Directory.CreateDirectory(lockPath);
            Directory.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow - TimeSpan.FromSeconds(11));
            var ran = false;
            PiLockfile.WithLock(file, () => ran = Directory.Exists(lockPath));
            Check(ran, "ran while holding the taken-over lock");
            Check(!Directory.Exists(lockPath), "the lock is removed after the run");
        })),
        ("lockfile.concurrent-holders-never-overlap", async () =>
        {
            using var sandbox = new Sandbox("lockfile-race");
            var file = Path.Combine(sandbox.Root, "settings.json");
            var inside = 0; var maximum = 0; var entries = 0;
            using var start = new Barrier(8);
            var workers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                for (var round = 0; round < 40; round++)
                {
                    // Generous retries: this case checks exclusion, not contention timing.
                    PiLockfile.WithLock(file, () =>
                    {
                        var now = Interlocked.Increment(ref inside);
                        int seen; do { seen = Volatile.Read(ref maximum); } while (now > seen && Interlocked.CompareExchange(ref maximum, now, seen) != seen);
                        Thread.SpinWait(200);
                        Interlocked.Increment(ref entries);
                        Interlocked.Decrement(ref inside);
                    }, attempts: 100_000, delay: TimeSpan.Zero);
                }
            }, TaskCreationOptions.LongRunning)).ToArray();
            await Task.WhenAll(workers);
            Equal(320, entries, "every holder ran");
            Equal(1, maximum, "never two holders at once");
        }),
        // A <file>.lock beyond MAX_PATH: the native mkdir gets the extended-length form on Windows, as the managed file APIs around it do.
        ("lockfile.a-lock-path-longer-than-max-path-is-acquired", Sync(() =>
        {
            using var sandbox = new Sandbox("lockfile-long");
            var directory = sandbox.Root;
            while (directory.Length < 300) directory = Path.Combine(directory, new string('d', 40));
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "settings.json");
            var ran = false;
            PiLockfile.WithLock(file, () => ran = Directory.Exists(PiLockfile.LockPath(file)));
            Check(ran && !Directory.Exists(PiLockfile.LockPath(file)), $"held and released a {PiLockfile.LockPath(file).Length}-character lock path");
            if (OperatingSystem.IsWindows())
            {
                Equal(@"\\?\C:\a\b.lock", PiLockfile.ExtendedLengthPath(@"C:\a\b.lock"), "drive path");
                Equal(@"\\?\UNC\server\share\b.lock", PiLockfile.ExtendedLengthPath(@"\\server\share\b.lock"), "UNC path");
                Equal(@"\\?\C:\a\b.lock", PiLockfile.ExtendedLengthPath(@"\\?\C:\a\b.lock"), "already extended");
            }
        })),
        // Upstream's lockSync gives up after ten attempts 20 ms apart, so a writer may fail with ELOCKED under heavy contention; what the
        // exclusive lock guarantees is that no write that returned is lost to an overlapping read-modify-write.
        ("lockfile.concurrent-trust-writes-lose-no-completed-write", async () =>
        {
            using var sandbox = new Sandbox("lockfile-trust");
            var trust = new ProjectTrustStore(sandbox.AgentDir, sandbox.Home);
            var projects = Enumerable.Range(0, 24).Select(index => Path.Combine(sandbox.Root, "project-" + index)).ToArray();
            foreach (var project in projects) Directory.CreateDirectory(project);
            var written = new System.Collections.Concurrent.ConcurrentBag<string>();
            await Task.WhenAll(projects.Select(project => Task.Run(() =>
            {
                try { trust.Set(project, true); written.Add(project); }
                catch (IOException error) when (error.Message == "Lock file is already being held") { }
            })));
            Check(!written.IsEmpty, "some writes completed");
            foreach (var project in written) Equal(true, trust.Get(project), "completed write kept for " + Path.GetFileName(project));
            Check(!Directory.Exists(trust.TrustPath + ".lock"), "no lock left behind");
        }),
    ];
}
