// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/env/test/process.test.ts and
// packages/env/daemon/src/{exec,process,output}.rs.
using System.Text.Json.Nodes;
using PiSharp.Env;

// Authored from the cited Pi 1.6.0 source; nothing here runs upstream code. Pi 1.6.0 fixed the end of a program's output going
// missing when the program exited while the link was backed up: the daemon took a reader that the link held up for a silent
// pipe, and stopped reading a grace period after the exit.
internal static partial class Program
{
    /// <summary>More than the native daemon reads ahead of the link (one chunk at the link, eight queued, one in hand, 16 KiB
    /// each), and not a whole number of chunks, so a last partial chunk is still in the pipe when the program has exited.</summary>
    private const int Sync160Tail = 256 * 1024 + 5 * 1024 + 123;

    // process.test.ts "delivers output left in the pipe when the program exits while the link is backed up", as upstream runs it:
    // on input, after a pause, the program writes 96 KiB and exits, while a second program floods the link and this client stalls
    // for longer than the grace after exit. The native daemon read these 96 KiB ahead of the link before the fix too; the two
    // cases below reach the grace it had wrong.
    private static async Task Sync160UpstreamBackedUpLink()
    {
        const int size = 96 * 1024;
        await using var connection = Connect();
        long received = 0; var other = 0;
        await using var program = await RemoteProcess.OpenAsync(connection, new()
        {
            Cwd = Environment.CurrentDirectory, Argv = Command("--child", "burst-after-input", "300", size.ToString()),
            OnStdout = bytes => { received += bytes.Length; if (bytes.Span.IndexOfAnyExcept((byte)120) >= 0) other++; },
        });
        await program.WriteAsync("go");
        var stalled = false;
        var flood = await RemoteProcess.OpenAsync(connection, new()
        {
            Cwd = Environment.CurrentDirectory, Argv = Command("--child", "endless"),
            // Events are delivered on the connection's reader: sleeping here stops this client reading the link.
            OnStdout = _ => { if (stalled) return; stalled = true; Thread.Sleep(1500); },
        });
        try { Check(await program.Exited.WaitAsync(TimeSpan.FromSeconds(20)) == 0, "program exit code"); }
        finally { flood.Close(); try { await flood.Exited.WaitAsync(TimeSpan.FromSeconds(10)); } catch (RemoteError) { } }
        Check(stalled, "the client stalled the link");
        Check(Volatile.Read(ref received) == size && other == 0, $"all output delivered: {Volatile.Read(ref received)} of {size} bytes");
    }

    // The same failure where the native daemon had it. Its loop hands one chunk at a time to the link and restarted the grace
    // after exit before the hand-over, so a hand-over the link held up for longer than the grace ended the reading: the chunk a
    // reader had in hand and the rest of the pipe were dropped. The client stalls on each of the program's output events, so every
    // hand-over waits for the link, also after the program has exited with its last chunks unread.
    private static async Task Sync160HeldLink(bool exec)
    {
        await using var connection = Connect();
        var flood = await RemoteProcess.OpenAsync(connection, new()
        { Cwd = Environment.CurrentDirectory, Argv = Command("--child", "endless"), OnStdout = _ => { } });
        long received = 0; var events = 0; var other = 0;
        // exec waits 100 ms for silence and then 100 ms more; process waits 100 ms.
        var stall = exec ? 350 : 250;
        void Output(ReadOnlySpan<byte> bytes)
        {
            received += bytes.Length; events++;
            if (bytes.IndexOfAnyExcept((byte)120) >= 0) other++;
            Thread.Sleep(stall);
        }
        int exitCode;
        try
        {
            if (exec)
            {
                // No window: output is not coalesced, so reading waits for the link.
                var json = new JsonObject
                {
                    ["argv"] = new JsonArray(Command("--child", "burst", "0", Sync160Tail.ToString()).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()),
                    ["cwd"] = Environment.CurrentDirectory,
                };
                var reply = await connection.RequestAsync("exec", json, new()
                { OnEvent = (output, bytes) => { if (output["stream"]?.GetValue<string>() == "stdout") Output(bytes.Span); } }).WaitAsync(TimeSpan.FromSeconds(35));
                exitCode = reply.Json["exitCode"]!.GetValue<int>();
            }
            else
            {
                await using var program = await RemoteProcess.OpenAsync(connection, new()
                {
                    Cwd = Environment.CurrentDirectory, Argv = Command("--child", "burst-after-input", "0", Sync160Tail.ToString()),
                    OnStdout = bytes => Output(bytes.Span),
                });
                await program.WriteAsync("go");
                exitCode = await program.Exited.WaitAsync(TimeSpan.FromSeconds(35));
            }
        }
        finally { flood.Close(); try { await flood.Exited.WaitAsync(TimeSpan.FromSeconds(10)); } catch (RemoteError) { } }
        Check(exitCode == 0, "program exit code");
        Check(events > 10, $"the output outran the daemon's read-ahead: {events} events");
        Check(Volatile.Read(ref received) == Sync160Tail && other == 0, $"all output delivered: {Volatile.Read(ref received)} of {Sync160Tail} bytes in {events} events");
    }

    private static async Task<int> Sync160Child(string[] args, Stream stdout)
    {
        switch (args[0])
        {
            // On input (burst-after-input) or at once (burst): after a pause, write the given number of bytes in one write, then exit.
            case "burst-after-input":
            case "burst":
                if (args[0] == "burst-after-input" && await Console.OpenStandardInput().ReadAsync(new byte[16]) == 0) return 3;
                await Task.Delay(int.Parse(args[1]));
                var output = new byte[int.Parse(args[2])]; Array.Fill(output, (byte)120);
                await stdout.WriteAsync(output); await stdout.FlushAsync(); return 0;
            // Floods its output until it is killed.
            case "endless":
                var chunk = new byte[1024 * 1024];
                while (true) await stdout.WriteAsync(chunk);
            default: return 99;
        }
    }
}
