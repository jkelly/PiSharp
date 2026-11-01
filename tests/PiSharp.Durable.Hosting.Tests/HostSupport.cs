using PiSharp.Durable;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;
using PiSharp.Durable.Sqlite;

internal static partial class Program
{
    /// <summary>How long a case waits for an answer.</summary>
    internal static readonly TimeSpan Answer = TimeSpan.FromSeconds(15);

    /// <summary>What a test device opens with; null fields take the defaults.</summary>
    internal sealed record DeviceSetup
    {
        public FauxDurableModels? Models { get; init; }
        public ModelRef? DefaultModel { get; init; }
        public Func<HostedSessionContext, IRegistry>? Registry { get; init; }
        public Func<string, Task<IStorage>>? Storage { get; init; }
        public IReadOnlyList<JobDefinition>? Jobs { get; init; }
        public TimeProvider? Time { get; init; }
        public bool RunSchedules { get; init; }
        public RetrievalOptions? Retrieval { get; init; }
        /// <summary>Changes the options last, for what the fields above do not cover (another model, a stream metadata hook).</summary>
        public Func<DurableDeviceOptions, DurableDeviceOptions>? Configure { get; init; }
    }

    /// <summary>A device on a fresh temporary data directory with one start directory, <see cref="Work"/>.</summary>
    internal sealed class TestDevice : IAsyncDisposable
    {
        private TestDevice(string root)
        {
            Root = root;
            Data = Path.Join(root, "data");
            Work = Path.Join(root, "work");
            Directory.CreateDirectory(Work);
        }

        public string Root { get; }
        public string Data { get; }
        public string Work { get; }
        public DurableDeviceHost Host { get; private set; } = null!;

        public static async Task<TestDevice> OpenAsync(string name, DeviceSetup? setup = null)
        {
            var device = new TestDevice(Temp(name));
            device.Host = await DurableDeviceHost.OpenAsync(device.Options(setup));
            return device;
        }

        public DurableDeviceOptions Options(DeviceSetup? setup = null)
        {
            var options = new DurableDeviceOptions
            {
                DataDirectory = Data,
                StartDirectories = [Work],
                DeviceName = "test-device",
                Models = setup?.Models ?? new FauxDurableModels(),
                DefaultModel = setup?.DefaultModel ?? FauxDurableModels.EchoRef,
                CreateRegistry = setup?.Registry,
                OpenStorage = setup?.Storage,
                ToolEffects = ToolEffects.Default.With("probe", ToolEffect.Read),
                Jobs = setup?.Jobs ?? SampleJobs.All,
                TimeProvider = setup?.Time ?? TimeProvider.System,
                RunSchedules = setup?.RunSchedules ?? false,
                SchedulePollInterval = TimeSpan.FromMilliseconds(20),
                Retrieval = setup?.Retrieval ?? new RetrievalOptions(),
            };
            return setup?.Configure?.Invoke(options) ?? options;
        }

        /// <summary>Closes the host and opens the data directory again, as a device restart does.</summary>
        public async Task ReopenAsync(DeviceSetup? setup = null)
        {
            await Host.DisposeAsync();
            Host = await DurableDeviceHost.OpenAsync(Options(setup));
        }

        public Task<HostedSession> CreateAsync(string? name = null, ModelRef? model = null, string? spawnKey = null) =>
            Host.CreateSessionAsync(new SessionCreateRequest { Cwd = Work, Name = name, Model = model, SpawnKey = spawnKey });

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A temporary directory. */ }
        }
    }

    /// <summary>A tool's start and its release.</summary>
    internal sealed class TestGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs;
    }

    /// <summary>The default registry plus <c>probe</c> (reads, answers "ok") and <c>hang</c> (unsafe; waits for the gate's release, then
    /// answers "released").</summary>
    internal static Func<HostedSessionContext, IRegistry> RegistryWith(TestGate gate) => context =>
    {
        var registry = DurableDeviceHost.DefaultRegistry(context);
        registry.Install(Define.Extension(new Extension
        {
            Name = "test-tools",
            Tools =
            [
                Define.Tool(new ToolRegistration
                {
                    Name = "probe",
                    Description = "Answers ok.",
                    Execute = (_, _, _) => Task.FromResult(Text("ok")),
                }),
                Define.Tool(new ToolRegistration
                {
                    Name = "hang",
                    Description = "Waits until the test releases it.",
                    Execute = async (_, _, context) =>
                    {
                        Interlocked.Increment(ref gate.Runs);
                        gate.Started.TrySetResult();
                        await gate.Release.Task.WaitAsync(context.CancellationToken);
                        return Text("released");
                    },
                }),
            ],
        }));
        return registry;
    };

    internal static ToolExecutionResult Text(string text) => new()
    {
        Output = JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", text)])]),
    };

    /// <summary>SQLite storage, as the host opens it by default.</summary>
    internal static async Task<IStorage> Sqlite(string path) => await SqliteStorage.OpenAsync(path);

    /// <summary>Waits until <paramref name="condition"/> holds, checking every 20 ms.</summary>
    internal static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Answer;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting until " + what);
            await Task.Delay(20);
        }
    }
}

/// <summary>A clock that runs in real time and can jump forward: timers fire when their due time passes on this clock, so durable sleeps
/// of hours end after <see cref="Advance"/> instead of waiting.</summary>
internal sealed class JumpClock : TimeProvider
{
    private readonly Lock gate = new();
    private readonly List<JumpTimer> timers = [];
    private long offset;

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddMilliseconds(Interlocked.Read(ref offset));

    /// <summary>Moves the clock forward and fires every timer now due.</summary>
    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref offset, (long)by.TotalMilliseconds);
        JumpTimer[] current;
        lock (gate) current = [.. timers];
        foreach (var timer in current) timer.Check();
    }

    /// <summary>Whether a timer is armed to fire at <paramref name="at"/>, within a second. A caller that reads the clock, then arms a
    /// relative timer, arms it from the new time if <see cref="Advance"/> runs in between; wait for this before advancing past it.</summary>
    public bool Armed(DateTimeOffset at)
    {
        JumpTimer[] current;
        lock (gate) current = [.. timers];
        return current.Any(timer => timer.Due is { } due && (due - at).Duration() <= TimeSpan.FromSeconds(1));
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new JumpTimer(this, callback, state);
        lock (gate) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    private void Remove(JumpTimer timer) { lock (gate) timers.Remove(timer); }

    private sealed class JumpTimer(JumpClock clock, TimerCallback callback, object? state) : ITimer
    {
        private readonly Lock gate = new();
        private Timer? real;
        private DateTimeOffset? due;
        private TimeSpan period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get { lock (gate) return due; } }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (gate)
            {
                this.period = period;
                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                Arm();
            }
            return true;
        }

        public void Check()
        {
            lock (gate)
            {
                if (due is not { } at || clock.GetUtcNow() < at) { Arm(); return; }
                due = period == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + period;
                Arm();
            }
            callback(state);
        }

        private void Arm()
        {
            real?.Dispose();
            real = null;
            if (due is not { } at) return;
            var wait = at - clock.GetUtcNow();
            real = new Timer(_ => Check(), null, wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait,
                Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            lock (gate) { real?.Dispose(); real = null; due = null; }
            clock.Remove(this);
        }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
