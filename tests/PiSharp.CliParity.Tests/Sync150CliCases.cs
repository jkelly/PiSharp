using PiSharp.Cli.Pi;

// Pi 1.5.0 sync, cli rows (f9a26365568127461c47a07a37dcb42c140a3ac5): packages/coding-agent/src/main.ts (a deleted working directory,
// --api-key and model references), core/model-resolver.ts and cli/credential-print.ts.
// Expectations are authored from reading the v1.5.0 sources; nothing is captured from an upstream run.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150CliCases() =>
    [
        ("sync150.deleted-cwd-ends-every-command-with-one-red-line", DeletedCwdStartup),
        ("sync150.model-references-choose-providers-by-prefix-and-credentials", ModelReferenceProviders),
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

    // model-resolver.ts resolveModelReference through main.ts and credential-print.ts (#10913, #11397): a provider id that contains "/"
    // is matched as a whole prefix, so --model reaches its model; the same id on two configured providers is ambiguous, and --api-key
    // does not choose between them; auth print-api-key --model counts only the catalog models of configured providers.
    private static async Task ModelReferenceProviders()
    {
        using var sandbox = new Sandbox("model-references");
        sandbox.Write(Path.Combine(sandbox.AgentDir, "models.json"), """
            {"providers":{
              "llama=http://host:10800":{"baseUrl":"https://replay.test","api":"anthropic-messages","apiKey":"k","models":[{"id":"unsloth/qwen-gguf:BF16","reasoning":true}]},
              "gw-a":{"baseUrl":"https://replay.test","api":"anthropic-messages","apiKey":"a","models":[{"id":"shared"}]},
              "gw-b":{"baseUrl":"https://replay.test","api":"anthropic-messages","apiKey":"b","models":[{"id":"shared"}]}}}
            """);
        var (code, _, stderr) = await sandbox.Run("-p", "--no-session", "--model", "llama=http://host:10800/unsloth/qwen-gguf:BF16:high", "hi");
        Equal(0, code, "slash provider exit; " + stderr);
        Check(sandbox.Requests.Count == 1 && sandbox.Requests[0].Json.GetProperty("model").GetString() == "unsloth/qwen-gguf:BF16",
            "the request names the model: " + string.Join(" | ", sandbox.Requests.Select(request => request.Body)));

        (code, _, stderr) = await sandbox.Run("-p", "--no-session", "--model", "shared", "hi");
        Check(code == 1 && stderr.Contains("Model \"shared\" is ambiguous across providers: gw-a/shared, gw-b/shared. More than one matching provider is authenticated. Use --provider or provider/model.",
            StringComparison.Ordinal), "two configured providers: " + stderr);
        (code, _, stderr) = await sandbox.Run("-p", "--no-session", "--model", "shared", "--api-key", "inline", "hi");
        Check(code == 1 && stderr.Contains("Model \"shared\" is ambiguous across providers: gw-a/shared, gw-b/shared. --api-key does not choose a provider. Use --provider or provider/model.",
            StringComparison.Ordinal), "--api-key: " + stderr);
        Equal(1, sandbox.Requests.Count, "no request for an ambiguous model");

        // anthropic has no gpt-5.5; an uncatalogued id on a configured provider is not a match.
        sandbox.Write(Path.Combine(sandbox.AgentDir, "auth.json"),
            """{"openai":{"type":"api_key","key":"openai-test-key"},"anthropic":{"type":"api_key","key":"anthropic-test-key"}}""");
        var (printCode, stdout, printErr) = await sandbox.Run("auth", "print-api-key", "--model", "gpt-5.5");
        Check(printCode == 0 && stdout == "openai-test-key\n", $"print-api-key --model: {printCode} {stdout} {printErr}");
    }
}
