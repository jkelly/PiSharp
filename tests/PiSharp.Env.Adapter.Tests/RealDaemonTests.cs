using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using PiSharp.Durable.Chord;
using PiSharp.Durable;
using PiSharp.Durable.Env;
using PiSharp.Durable.Env.Local;
using PiSharp.Durable.Testing;
using PiSharp.Durable.Tools;
using PiSharp.Durable.Tools.Env;
using PiSharp.Env;

internal static class RealDaemonTests
{
    internal static async Task RunAsync()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot find the repository root for the daemon build");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var daemon = Path.Combine(root.FullName, "src", "PiSharp.Env.Daemon", "bin", configuration, "net10.0", "PiSharp.Env.Daemon.dll");
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        if (!File.Exists(daemon) || !File.Exists(dotnet)) throw new InvalidOperationException("Daemon or matching dotnet host is missing");
        await using var connection = new Connection(new() { Command = [dotnet, daemon], StartupTimeout = TimeSpan.FromSeconds(20) });
        var shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files", "Git", "bin", "bash.exe")
            : "/bin/sh";
        foreach (var mode in new[] { (WatchMode?)null, WatchMode.Polling })
        {
            var cases = EnvConformance.Create(new EnvConformanceOptions
            {
                Shell = [shell, "-c"], Symlinks = !OperatingSystem.IsWindows(), ListFiles = true,
                WithEnv = async use =>
                {
                    var directory = Directory.CreateTempSubdirectory("pisharp-env-conformance-").FullName;
                    var env = new RemoteExecutionEnv(new()
                    {
                        Connection = connection, Id = "real-daemon", Cwd = directory, ShellPath = shell,
                        Watch = new() { Mode = mode, PollIntervalMs = 100 },
                    });
                    try { await use(env); }
                    finally
                    {
                        await env.CleanupAsync(DurableContext.Background);
                        Directory.Delete(directory, true);
                    }
                },
            });
            foreach (var test in cases)
            {
                try { await test.Run().WaitAsync(TimeSpan.FromMilliseconds(test.TimeoutMs ?? 30000)); }
                catch (Exception error) { throw new InvalidOperationException($"Real daemon ({mode?.ToString() ?? "default"}): {test.Name}", error); }
                Console.WriteLine($"PASS real daemon {mode?.ToString() ?? "default"}: {test.Slug}");
            }
        }
        await RandomTraceAsync(connection, shell);
        await ToolTraceAsync(connection, shell);
    }

    private static async Task RandomTraceAsync(Connection connection, string shell)
    {
        const int seed = 730150;
        var directory = Directory.CreateTempSubdirectory("pisharp-env-trace-").FullName;
        var localRoot = Directory.CreateDirectory(Path.Combine(directory, "local")).FullName;
        var remoteRoot = Directory.CreateDirectory(Path.Combine(directory, "remote")).FullName;
        var local = new LocalFileSystem(localRoot);
        var remote = new RemoteExecutionEnv(new() { Connection = connection, Id = "trace", Cwd = remoteRoot, ShellPath = shell });
        var context = DurableContext.Background;
        var random = new Random(seed);
        var trace = new List<string>();
        try
        {
            for (var step = 0; step < 150; step++)
            {
                var path = $"f{random.Next(8)}.txt";
                var operation = random.Next(5);
                var bytes = new byte[random.Next(80)]; random.NextBytes(bytes);
                trace.Add($"{step}:{operation}:{path}:{Convert.ToHexString(bytes)}");
                switch (operation)
                {
                    case 0: Same(await local.WriteFileAsync(path, bytes, context), await remote.WriteFileAsync(path, bytes, context)); break;
                    case 1: Same(await local.AppendFileAsync(path, bytes, context), await remote.AppendFileAsync(path, bytes, context)); break;
                    case 2: Same(await local.RemoveAsync(path, new() { Force = true }, context), await remote.RemoveAsync(path, new() { Force = true }, context)); break;
                    case 3:
                        var length = random.Next(80);
                        Same(await local.TruncateFileAsync(path, length, context), await remote.TruncateFileAsync(path, length, context)); break;
                    default:
                        var destination = $"f{random.Next(8)}.txt";
                        Same(await local.RenameFileAsync(path, destination, context), await remote.RenameFileAsync(path, destination, context)); break;
                }
                for (var index = 0; index < 8; index++)
                {
                    var expected = await local.ReadBinaryFileAsync($"f{index}.txt", context);
                    var actual = await remote.ReadBinaryFileAsync($"f{index}.txt", context);
                    if (expected.IsOk != actual.IsOk || !expected.IsOk && expected.Error.Code != actual.Error!.Code || expected.IsOk && !expected.Value.AsSpan().SequenceEqual(actual.Value))
                        throw new InvalidOperationException($"File state differs after trace step {step}, file {index}");
                }
            }
            Console.WriteLine($"PASS real daemon random filesystem trace seed={seed}, steps=150");
        }
        catch (Exception error)
        {
            // Keep a reproducible, deterministic trace next to the test binary on failure.
            var artifact = Path.Combine(AppContext.BaseDirectory, $"failed-env-trace-{seed}.txt");
            await File.WriteAllLinesAsync(artifact, trace);
            throw new InvalidOperationException($"Filesystem trace seed {seed}; operations saved to {artifact}", error);
        }
        finally { await remote.CleanupAsync(context); await local.CleanupAsync(context); Directory.Delete(directory, true); }
    }

    private static void Same(Result<Unit, FileError> expected, Result<Unit, FileError> actual)
    {
        if (expected.IsOk != actual.IsOk || !expected.IsOk && expected.Error.Code != actual.Error!.Code)
            throw new InvalidOperationException($"Local/remote results differ: {expected} / {actual}");
    }

    private static async Task ToolTraceAsync(Connection connection, string shell)
    {
        var directory = Directory.CreateTempSubdirectory("pisharp-env-tools-").FullName;
        var local = new LocalExecutionEnv(new LocalExecutionEnvOptions { Cwd = Directory.CreateDirectory(Path.Combine(directory, "local")).FullName, ShellPath = shell });
        var remote = new RemoteExecutionEnv(new() { Connection = connection, Id = "tools", Cwd = Directory.CreateDirectory(Path.Combine(directory, "remote")).FullName, ShellPath = shell });
        try
        {
            foreach (var (tool, arguments) in new (ToolRegistration, string)[]
            {
                (CodingTools.CreateWriteTool(), """{"path":"example.txt","content":"alpha\nbeta\n"}"""),
                (CodingTools.CreateReadTool(), """{"path":"example.txt","offset":2,"limit":1}"""),
                (CodingTools.CreateEditTool(), """{"path":"example.txt","edits":[{"oldText":"beta","newText":"gamma"}]}"""),
                (CodingTools.CreateReadTool(), """{"path":"example.txt"}"""),
                (CodingTools.CreateBashTool(), """{"command":"printf 'tool-output'"}"""),
            })
            {
                var localApi = new AdapterToolApi(local); var remoteApi = new AdapterToolApi(remote);
                var expected = await tool.Execute(JsonValue.Parse(arguments), localApi, DurableContext.Background);
                var actual = await tool.Execute(JsonValue.Parse(arguments), remoteApi, DurableContext.Background);
                if (expected.IsError != actual.IsError || expected.StructuredOutput?.ToJson() != actual.StructuredOutput?.ToJson() || localApi.Text.ToString() != remoteApi.Text.ToString())
                    throw new InvalidOperationException($"Actual Durable {tool.Name} tool differs between local and remote environments");
            }
            var text = (await remote.ReadTextFileAsync("example.txt", DurableContext.Background)).GetOrThrow();
            if (text != "alpha\ngamma\n") throw new InvalidOperationException("Durable edit tool did not update the remote file");
            Console.WriteLine("PASS real daemon actual Durable write/read/edit/bash tool differential trace");
        }
        finally { await remote.CleanupAsync(DurableContext.Background); await local.CleanupAsync(DurableContext.Background); Directory.Delete(directory, true); }
    }
}

// Match the repository's tool tests: unrelated durable APIs fail if accidentally invoked.
public class AdapterToolStub : System.Reflection.DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
        throw new NotSupportedException($"{targetMethod?.Name} is outside this tool integration fixture");
}

internal sealed class AdapterToolApi(IExecutionEnv env) : DelegatingToolExecutionApi(System.Reflection.DispatchProxy.Create<IToolExecutionApi, AdapterToolStub>())
{
    internal StringBuilder Text { get; } = new();
    public override IExecutionEnv Env => env;
    public override ShellOutputWindow? OutputWindow => null;
    public override void Output(string chunk, ShellOutputSkip? skipped = null) => Text.Append(chunk);
    public override void Output(ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped = null) => Text.Append(Encoding.UTF8.GetString(chunk));
    public override RetainedOutput RetainedOutput() => new(Text.ToString(), false);
    public override void Diagnostic(ToolDiagnostic diagnostic) { }
}
