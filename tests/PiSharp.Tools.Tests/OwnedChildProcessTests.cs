using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using PiSharp.Tools.Processes;
using PiSharp.Tools.Processes.Unix;

internal static partial class NativeProcessRunnerTests
{
    private static IEnumerable<(string Name, Func<Task> Run)> OwnedChildCases() =>
    [
        ("owned child natural parent exit stops retained descendants before completion", OwnedNaturalExit),
        ("owned child stop dispose races join original readers and preserve faults", OwnedStopRaces),
        ("owned child interactive stdin and prelaunch admission preserve bytes", OwnedInputAndAdmission),
        ("owned child blocked pipe reads and writes retire without leaked syscalls", OwnedCancellablePipes)
    ];

    private static ProcessStartInfo OwnedStart(Scratch scratch, bool input, params string[] args)
    {
        var (executable, arguments) = ChildCommand(args);
        var start = new ProcessStartInfo(executable) { WorkingDirectory = scratch.Root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = input, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear();
        foreach (var pair in ParentEnvironment()) start.Environment[pair.Key] = pair.Value;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task OwnedNaturalExit()
    {
        using var scratch = new Scratch(); using var root = new Control(); using var branch = new Control(); using var leaf = new Control();
        await using var process = await OwnedChildProcess.StartAsync(OwnedStart(scratch, false, "tree", root.Name, branch.Name, leaf.Name));
        var output = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.OwnReader(output); process.OwnReader(stderr);
        var ids = await Task.WhenAll(root.ReadyAsync(), branch.ReadyAsync(), leaf.ReadyAsync());
        Equal(ids[0], process.Id); Check(!process.HasExited, "Active owned tree reported completion.");
        await root.SendAsync("EXIT");
        Equal(0, await process.LeaderExit.WaitAsync(TimeSpan.FromSeconds(5)));
        Equal(0, await process.Exit.WaitAsync(TimeSpan.FromSeconds(10)));
        Check(process.HasExited && process.ExitCode == 0 && (await output).Contains("root-output", StringComparison.Ordinal),
            "Natural parent exit lost code or output.");
        foreach (var id in ids) Dead(id);
        var stop = process.StopAsync(); Check(ReferenceEquals(stop, process.StopAsync()), "Stop task identity changed."); await stop;
    }

    private static async Task OwnedStopRaces()
    {
        using var scratch = new Scratch(); using var control = new Control();
        var process = await OwnedChildProcess.StartAsync(OwnedStart(scratch, false, "block", control.Name));
        var output = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.OwnReader(output); process.OwnReader(stderr);
        var held = NewGate<bool>(); var fault = new IOException("original owned reader fault"); process.OwnReader(held.Task);
        Task? disposal = null;
        try
        {
            var id = await control.ReadyAsync();
            using var canceledWait = new CancellationTokenSource(); canceledWait.Cancel();
            try { await process.WaitForExitAsync(canceledWait.Token); throw new InvalidOperationException("Canceled wait completed normally."); }
            catch (OperationCanceledException error) { Equal(canceledWait.Token, error.CancellationToken); }
            Check(!process.LeaderExit.IsCompleted, "Canceling a wait implicitly stopped the owned process.");
            var stops = Enumerable.Range(0, 8).Select(_ => process.StopAsync()).ToArray();
            Check(stops.All(task => ReferenceEquals(task, stops[0])), "Concurrent stops replaced their original.");
            disposal = process.DisposeAsync().AsTask();
            await Task.WhenAll(stops); await process.Exit; Dead(id);
            Check(!disposal.IsCompleted, "Disposal abandoned a registered original reader.");
            held.TrySetException(fault);
            try { await disposal; throw new InvalidOperationException("Reader fault was hidden."); }
            catch (AggregateException failures) { Check(failures.Flatten().InnerExceptions.Any(error => ReferenceEquals(error, fault)), "Original reader fault was replaced."); }
            Throws<ObjectDisposedException>(() => process.OwnReader(Task.CompletedTask));
        }
        finally
        {
            held.TrySetResult(true);
            try { await (disposal ?? process.DisposeAsync().AsTask()); } catch (AggregateException) { }
        }
    }

    private static async Task OwnedInputAndAdmission()
    {
        using var scratch = new Scratch();
        var start = OwnedStart(scratch, true, "echo-stdin");
        await using (var process = await OwnedChildProcess.StartAsync(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            process.OwnReader(output); process.OwnReader(error);
            const string text = "π🙂 exact input\n";
            await process.StandardInput.WriteAsync(Encoding.UTF8.GetBytes(text));
            await process.StandardInput.FlushAsync(); await process.StandardInput.DisposeAsync();
            Equal(0, await process.Exit.WaitAsync(TimeSpan.FromSeconds(10))); Equal(text, await output);
        }
        var inspect = OwnedStart(scratch, false, "inspect", "space value", "quote\"value", "π🙂");
        inspect.Environment["PISHARP_PROCESS_TEST"] = "owned-value";
        await using (var process = await OwnedChildProcess.StartAsync(inspect))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            process.OwnReader(output); process.OwnReader(error);
            Throws<InvalidOperationException>(() => _ = process.StandardInput);
            Equal(0, await process.Exit.WaitAsync(TimeSpan.FromSeconds(10)));
            using var actual = JsonDocument.Parse(await output);
            Equal("owned-value", actual.RootElement.GetProperty("value").GetString());
            Equal(scratch.Root, actual.RootElement.GetProperty("cwd").GetString());
            Check(actual.RootElement.GetProperty("stdinEof").GetBoolean(), "Unredirected stdin did not deliver EOF.");
            Sequence(new[] { "space value", "quote\"value", "π🙂" }, actual.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()!));
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var touched = scratch.File("not-created");
        try { await OwnedChildProcess.StartAsync(OwnedStart(scratch, false, "touch", touched), canceled.Token); throw new InvalidOperationException("Pre-cancellation was ignored."); }
        catch (OperationCanceledException error) { Equal(canceled.Token, error.CancellationToken); }
        Check(!File.Exists(touched), "Canceled launch executed child instructions.");
        var invalid = OwnedStart(scratch, false, "touch", touched); invalid.Arguments = "raw arguments";
        try { await OwnedChildProcess.StartAsync(invalid); throw new InvalidOperationException("Raw arguments were accepted."); }
        catch (ArgumentException) { }
        Check(!File.Exists(touched), "Rejected launch executed child instructions.");
    }

    private static async Task OwnedCancellablePipes()
    {
        using var scratch = new Scratch(); using var control = new Control();
        await using (var process = await OwnedChildProcess.StartAsync(OwnedStart(scratch, true, "block", control.Name)))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            process.OwnReader(output); process.OwnReader(stderr);
            await control.ReadyAsync();
            // The child deliberately never reads its stdin. Stop must retire the caller's pending write.
            var write = process.StandardInput.WriteAsync(new byte[2 * 1024 * 1024]).AsTask();
            try
            {
                await Task.Delay(30); Check(!write.IsCompleted, "Blocked input fixture did not fill its pipe.");
                await process.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await PipeStopped(write);
            }
            finally { await process.StopAsync(); try { await write; } catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { } }
        }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            Console.WriteLine("Owned POSIX retained-end pipe checks require Linux/macOS; Windows owned-pipe stop ran.");
            return;
        }
        // Keep the opposite ends open ourselves: equivalent to an escaped descendant retaining inherited stdio, without
        // creating an escaped process or relying on a global PID kill for fixture cleanup.
        foreach (var writer in new[] { false, true })
        {
            var fds = new int[2]; Check(OwnedPipeNative.Pipe(fds) == 0, "POSIX fixture pipe failed.");
            try
            {
                using var stream = new PosixOwnedPipeStream(fds[writer ? 1 : 0], writer); fds[writer ? 1 : 0] = -1;
                Task pending = writer ? stream.WriteAsync(new byte[2 * 1024 * 1024]).AsTask() : stream.ReadAsync(new byte[8]).AsTask();
                await Task.Delay(30); Check(!pending.IsCompleted, "Retained pipe-end fixture did not block.");
                stream.Dispose(); await PipeStopped(pending);
            }
            finally { foreach (var descriptor in fds) if (descriptor >= 0) _ = OwnedPipeNative.Close(descriptor); }
        }
    }

    private static async Task PipeStopped(Task original)
    {
        try { await original.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
        Check(original.IsCompleted, "Original pipe operation escaped disposal.");
    }

    private static class OwnedPipeNative
    {
        [DllImport("libc", EntryPoint = "pipe", SetLastError = true)] internal static extern int Pipe([Out] int[] descriptors);
        [DllImport("libc", EntryPoint = "close")] internal static extern int Close(int descriptor);
    }
}
