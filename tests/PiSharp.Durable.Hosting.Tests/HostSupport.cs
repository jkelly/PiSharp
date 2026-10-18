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

        public DurableDeviceOptions Options(DeviceSetup? setup = null) => new()
        {
            DataDirectory = Data,
            StartDirectories = [Work],
            DeviceName = "test-device",
            Models = setup?.Models ?? new FauxDurableModels(),
            DefaultModel = setup?.DefaultModel ?? FauxDurableModels.EchoRef,
            CreateRegistry = setup?.Registry,
            OpenStorage = setup?.Storage,
            ToolEffects = ToolEffects.Default.With("probe", ToolEffect.Read),
        };

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
