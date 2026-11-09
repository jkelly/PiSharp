using PiSharp.Env.Daemon;

if (args.Length != 3 || args[0] != "serve" || args[1] != "--token" || args[2].Length != 32 || args[2].Any(c => !Uri.IsHexDigit(c)))
{
    Console.Error.WriteLine("Usage: PiSharp.Env.Daemon serve --token <32 hex characters>");
    return 2;
}
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
System.Runtime.InteropServices.PosixSignalRegistration? term = null, hup = null;
if (!OperatingSystem.IsWindows())
{
    term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; shutdown.Cancel(); });
    hup = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, ctx => { ctx.Cancel = true; shutdown.Cancel(); });
}
try
{
    var operations = EnvProcessOperations.Create().Concat(TerminalOperations.Create()).Append(new WatchOperation()).ToArray();
    await using var session = new EnvDaemonSession(Console.OpenStandardInput(), Console.OpenStandardOutput(), new() { Token = args[2], Operations = operations, Features = ["process"] });
    await session.RunAsync(shutdown.Token);
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
finally { term?.Dispose(); hup?.Dispose(); }
