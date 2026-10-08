using System.Diagnostics;
using System.Text.Json;
using PiSharp.Extensions;

// Trusted authored fixture only. No production process owner or cancellation policy is replaced.
namespace OwnedChildShutdownFixture;

public sealed record Configuration(string Root, string DotnetHost, string ChildAssembly, bool FailCleanup = false);

public sealed class Entry : IPiSharpExtension
{
    public const string ConfigurationEnvironment = "PISHARP_SHUTDOWN_CHILD_CONFIGURATION";
    public const string ChildSwitch = "--owned-shutdown-child-fixture";
    private readonly object gate = new();
    private readonly Configuration? supplied;
    private Configuration? configuration;
    private Process? child;
    private Task? exit, stdout, stderr, disposal;
    private bool initialized;

    public Entry() { }
    internal Entry(Configuration supplied) => this.supplied = supplied;

    public async ValueTask InitializeAsync(IExtensionRegistry registry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (initialized || disposal is not null) throw new InvalidOperationException("Owned child fixture initialization is one-shot.");
            initialized = true;
        }
        var config = supplied ?? JsonSerializer.Deserialize<Configuration>(await File.ReadAllTextAsync(
            Environment.GetEnvironmentVariable(ConfigurationEnvironment) ?? throw new InvalidOperationException("Explicit owned child configuration required."), token))
            ?? throw new InvalidOperationException("Invalid owned child configuration.");
        Validate(config); configuration = config;
        var start = new ProcessStartInfo(config.DotnetHost)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = config.Root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(config.ChildAssembly); start.ArgumentList.Add(ChildSwitch); start.ArgumentList.Add(config.Root);
        child = Process.Start(start) ?? throw new InvalidOperationException("Owned fixture child did not start.");
        // Publish every original task before any fallible marker or stdin close. No PID lookup/kill is used.
        exit = JoinExit(child, config); stdout = JoinStream(child.StandardOutput, config, "stdout");
        stderr = JoinStream(child.StandardError, config, "stderr");
        try { child.StandardInput.Close(); MarkIdentity(config.Root, "owner.started", child.Id); }
        catch (Exception initializationFailure)
        {
            ReleaseAll(config.Root);
            try { await DisposeAsync(); }
            catch (Exception cleanupFailure) { throw new AggregateException(initializationFailure, cleanupFailure); }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    { lock (gate) return new(disposal ??= Close()); }

    private async Task Close()
    {
        await Task.Yield(); // The original disposal task is published before trusted cleanup can run.
        if (configuration is not { } config || child is not { } process) return;
        var failures = new List<Exception>();
        try { Mark(config.Root, "dispose.entered"); } catch (Exception error) { failures.Add(error); }
        try { Mark(config.Root, "stop.requested"); } catch (Exception error) { failures.Add(error); }
        foreach (var original in new[] { exit!, stdout!, stderr! })
            try { await original.ConfigureAwait(false); } catch (Exception error) { failures.Add(error); }
        try { process.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { Mark(config.Root, "owner.joined"); } catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0) throw new AggregateException("Owned child fixture cleanup failures.", failures);
    }

    private static async Task JoinExit(Process child, Configuration config)
    {
        await child.WaitForExitAsync().ConfigureAwait(false);
        Mark(config.Root, "exit.joined");
        if (child.ExitCode != 0) throw new IOException("Owned child fixture exited unsuccessfully.");
        // Failure injection follows the original physical exit; it does not forge an exit receipt.
        if (config.FailCleanup) throw new IOException("AUTHORED_OWNED_CHILD_EXIT_CLEANUP");
    }

    private static async Task JoinStream(StreamReader stream, Configuration config, string name)
    {
        var failures = new List<Exception>();
        try
        {
            var output = await stream.ReadToEndAsync().ConfigureAwait(false);
            if (output != name + "-owned-output\n") throw new IOException("Owned redirected output differs.");
            Mark(config.Root, name + ".eof");
            await Wait(config.Root, name + ".release").ConfigureAwait(false);
        }
        catch (Exception error) { failures.Add(error); }
        try { stream.Dispose(); } catch (Exception error) { failures.Add(error); }
        try { Mark(config.Root, name + ".closed"); } catch (Exception error) { failures.Add(error); }
        if (config.FailCleanup) failures.Add(new IOException("AUTHORED_OWNED_CHILD_" + name.ToUpperInvariant() + "_CLOSE"));
        if (failures.Count > 0) throw new AggregateException("Owned redirected stream cleanup failures.", failures);
    }

    public static async Task<int> RunChildAsync(string[] args)
    {
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 2;
        var root = Path.GetFullPath(args[0]);
        if (!File.Exists(Path.Combine(root, "fixture.owned"))) return 2;
        await Console.Out.WriteAsync("stdout-owned-output\n"); await Console.Out.FlushAsync();
        await Console.Error.WriteAsync("stderr-owned-output\n"); await Console.Error.FlushAsync();
        MarkIdentity(root, "child.ready", Environment.ProcessId);
        await Wait(root, "stop.requested"); await Wait(root, "exit.release");
        return 0; // This fixture starts no descendants and exits naturally through the owned protocol.
    }

    internal static void Validate(Configuration config)
    {
        foreach (var path in new[] { config.Root, config.DotnetHost, config.ChildAssembly })
            if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl)) throw new InvalidOperationException("Absolute fixture inputs required.");
        if (!Directory.Exists(config.Root) || !File.Exists(Path.Combine(config.Root, "fixture.owned")) ||
            !File.Exists(config.DotnetHost) || !File.Exists(config.ChildAssembly) ||
            Path.GetFileName(config.ChildAssembly) != "PiSharp.CodingAgent.Tests.dll")
            throw new InvalidOperationException("Admitted owned fixture inputs are unavailable.");
    }
    public static void ReleaseAll(string root)
    { foreach (var name in new[] { "stop.requested", "exit.release", "stdout.release", "stderr.release" }) Mark(root, name); }
    internal static void Mark(string root, string name) => File.WriteAllText(Path.Combine(root, name), "owned fixture\n");
    private static void MarkIdentity(string root, string name, int processId)
    {
        var temporary = Path.Combine(root, name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { processId, protocol = ChildSwitch }) + "\n");
        File.Move(temporary, Path.Combine(root, name)); // Readers see a complete immutable identity marker.
    }
    internal static async Task Wait(string root, string name, CancellationToken token = default)
    { while (!File.Exists(Path.Combine(root, name))) await Task.Delay(10, token).ConfigureAwait(false); }
}
