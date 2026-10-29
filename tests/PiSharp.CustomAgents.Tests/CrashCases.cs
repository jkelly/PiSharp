using System.Diagnostics;
using System.Reflection;
using System.Text;
using PiSharp.CustomAgents.Workflows;
using PiSharp.CustomAgents.Workflows.Hosting;

// Decision 0012, P2: a real process kill. A child process starts a run on SQLite whose second action records a charge with OnceAsync and
// then hangs; the parent kills the child's process tree and resumes the store itself. The charge is recorded once, the interrupted body
// reruns as a replay, and committed actions do not rerun.
internal static class CrashWorkflows
{
    public static Workflow<OrderData> Charging(string directory, bool hang) => Workflow.For<OrderData>("crashing")
        .Then("first", data =>
        {
            Effect(directory, "first");
            data.Trail.Add("first");
        })
        .Then("charge", async (data, context, token) =>
        {
            data.Note = await context.OnceAsync("charge", _ =>
            {
                Effect(directory, "charge");
                return Task.FromResult("receipt");
            }, token);
            Effect(directory, "body:" + context.IsReplay);
            data.Trail.Add("charge");
            if (hang)
            {
                Console.Out.WriteLine("READY");
                Console.Out.Flush();
                await Task.Delay(Timeout.Infinite, token);
            }
        })
        .Then("last", data =>
        {
            Effect(directory, "last");
            data.Trail.Add("last");
        })
        .Build();

    public static string[] Effects(string directory) => File.ReadAllLines(Path.Combine(directory, "effects.log"));

    private static void Effect(string directory, string line)
    {
        lock (typeof(CrashWorkflows)) File.AppendAllText(Path.Combine(directory, "effects.log"), line + "\n");
    }
}

internal static class CrashChild
{
    /// <summary>The child side: <c>--crash-child charge &lt;sqlite path&gt; &lt;directory&gt;</c>. Never returns on its own.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["charge", var path, var directory]) throw new ArgumentException("Use --crash-child charge <sqlite path> <directory>.");
        var workflow = CrashWorkflows.Charging(directory, hang: true);
        await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path));
        await host.Runner.StartAsync(workflow, new OrderData { OrderId = "crash" }, new WorkflowStartOptions { RunKey = "crash" });
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
}

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> CrashCases()
    {
        yield return Case("crash.process-kill", async () =>
        {
            var directory = Temp("crash");
            var path = Path.Combine(directory, "agents.sqlite");
            await RunUntilKilledAsync(["--crash-child", "charge", path, directory], "READY");
            var workflow = CrashWorkflows.Charging(directory, hang: false);
            await using var host = await TestHost.OpenAsync(catalog => catalog.Add(workflow), await TestHost.SqliteAsync(path));
            var run = await host.Runner.FindAsync(workflow, "crash") ?? throw new InvalidOperationException("The child's run is missing.");
            var data = (await run.WaitAsync()).EnsureCompleted();
            Equal("first,charge,last", string.Join(",", data.Trail), "trail");
            Equal("receipt", data.Note, "recorded charge");
            Equal("first,charge,body:False,body:True,last", string.Join(",", CrashWorkflows.Effects(directory)), "effects");
        });
    }

    /// <summary>Runs this test executable as a child until it prints <paramref name="sentinel"/>, then kills its process tree.</summary>
    private static async Task RunUntilKilledAsync(IEnumerable<string> arguments, string sentinel)
    {
        var host = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("No entry assembly"));
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
        {
            var runtime = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
            if (runtime.Parent?.Parent?.Parent is { } root && File.Exists(Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")))
                start.Environment["DOTNET_ROOT"] = root.FullName;
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errors = new StringBuilder();
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is null) seen.TrySetResult("EOF");
            else if (line.Data == sentinel) seen.TrySetResult(sentinel);
        };
        process.ErrorDataReceived += (_, line) => { if (line.Data is not null) lock (errors) errors.AppendLine(line.Data); };
        if (!process.Start()) throw new InvalidOperationException("The crash child did not start.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var result = await seen.Task.WaitAsync(TimeSpan.FromSeconds(60));
            if (result != sentinel) lock (errors) throw new InvalidOperationException("The crash child ended early:\n" + errors);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
