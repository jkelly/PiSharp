using PiSharp.Cli.Pi;

// Pi 1.5.0 sync, cli rows (f9a26365568127461c47a07a37dcb42c140a3ac5): packages/coding-agent/src/main.ts (a deleted working directory).
// Expectations are authored from reading the v1.5.0 sources; nothing is captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150CliCases() =>
    [
        ("sync150.deleted-cwd-ends-every-command-with-one-red-line", DeletedCwdStartup),
    ];

    // main.ts: the first process.cwd() is wrapped; ENOENT prints the red line and exits 1 for every command, --version and --help
    // included, while other errors propagate. runAuthCommand runs before that read, so auth still works. The deleted directory is
    // simulated with an injected reader (Windows does not let a process's directory be deleted).
    private static async Task DeletedCwdStartup()
    {
        Equal(null, PiCommand.ReadCwd(() => throw new FileNotFoundException("Could not find file")), "ENOENT on Linux");
        Equal(null, PiCommand.ReadCwd(() => throw new DirectoryNotFoundException()), "ENOENT as a directory");
        Equal("/work", PiCommand.ReadCwd(() => "/work"), "existing directory");
        Throws<UnauthorizedAccessException>(() => PiCommand.ReadCwd(() => throw new UnauthorizedAccessException("EACCES")), "other errors propagate");

        using var sandbox = new Sandbox("deleted-cwd");
        foreach (var args in new[] { new[] { "--version" }, ["--help"], ["--list-models"], ["-p", "hi"], ["install", "npm:x"], ["mcp", "list"] })
        {
            using var stdout = new StringWriter(); using var stderr = new StringWriter();
            var host = sandbox.Host(stdout, stderr, null) with { Cwd = "", CwdMissing = true, Color = true };
            Equal(1, await PiCommand.RunAsync(args, host, CancellationToken.None), string.Join(' ', args) + " exit");
            Equal("\u001b[31m" + PiCommand.DeletedCwdMessage + "\u001b[39m\n", stderr.ToString().Replace("\r\n", "\n"), string.Join(' ', args) + " stderr");
            Equal("", stdout.ToString(), string.Join(' ', args) + " stdout");
        }
        Equal("Error: The current working directory no longer exists. Change to an existing directory and try again.", PiCommand.DeletedCwdMessage, "message");
        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            var host = sandbox.Host(stdout, stderr, null) with { Cwd = "", CwdMissing = true };
            await PiCommand.RunAsync(["auth", "--help"], host, CancellationToken.None);
            Check(!stderr.ToString().Contains(PiCommand.DeletedCwdMessage, StringComparison.Ordinal), "auth runs before the cwd read: " + stderr);
        }
    }
}
