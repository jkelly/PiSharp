using System.Collections.Immutable;
using System.Text.Json;
using PiSharp.Agent;
using PiSharp.Contracts;
using PiSharp.Tools;
using PiSharp.Tools.Processes;

internal static class PowerShellToolTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("PowerShell discovery resolves pwsh before Windows PowerShell and supports custom paths", Discovery),
        ("PowerShell tool enforces declaration policy validation unavailable-shell and catalog defaults", ContractAndPolicy),
        ("PowerShell tool executes commands UTF-8 non-zero exits timeouts and truncation", Execution),
    ];

    private static Task Discovery()
    {
        // Windows: pwsh.exe on PATH wins over ProgramFiles pwsh.exe and powershell.exe
        var winFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Tools\pwsh.exe",
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
        };
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\Tools;C:\Windows\System32\WindowsPowerShell\v1.0",
            ["ProgramFiles"] = @"C:\Program Files",
            ["SystemRoot"] = @"C:\Windows",
        };
        var host = new ShellHost(true, name => env.GetValueOrDefault(name), winFiles.Contains, @"C:\Users\dev");
        var resolved = PowerShellDiscovery.Resolve(null, host);
        Equal(@"C:\Tools\pwsh.exe", resolved.Shell);
        Check(resolved.Arguments.SequenceEqual(PowerShellDiscovery.DefaultArguments), "Default PowerShell args mismatch.");

        // Fallback to Program Files pwsh.exe when not on PATH
        winFiles.Remove(@"C:\Tools\pwsh.exe");
        Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", PowerShellDiscovery.Resolve(null, host).Shell);

        // Fallback to powershell.exe on PATH / SystemRoot
        winFiles.Remove(@"C:\Program Files\PowerShell\7\pwsh.exe");
        Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", PowerShellDiscovery.Resolve(null, host).Shell);

        // Missing completely throws ShellDiscoveryException
        winFiles.Clear();
        Throws<ShellDiscoveryException>(() => PowerShellDiscovery.Resolve(null, host));

        // Explicit custom path
        winFiles.Add(@"D:\Custom\pwsh.exe");
        Equal(@"D:\Custom\pwsh.exe", PowerShellDiscovery.Resolve(@"D:\Custom\pwsh.exe", host).Shell);
        Throws<ShellDiscoveryException>(() => PowerShellDiscovery.Resolve(@"D:\Custom\missing.exe", host));

        // Unix discovery
        var unixFiles = new HashSet<string>(StringComparer.Ordinal) { "/usr/local/bin/pwsh" };
        var unixHost = new ShellHost(false, name => name == "PATH" ? "/usr/bin:/usr/local/bin" : null, unixFiles.Contains, "/home/dev");
        Equal("/usr/local/bin/pwsh", PowerShellDiscovery.Resolve(null, unixHost).Shell);
        unixFiles.Clear();
        Throws<ShellDiscoveryException>(() => PowerShellDiscovery.Resolve(null, unixHost));
        return Task.CompletedTask;
    }

    private static async Task ContractAndPolicy()
    {
        using var temp = new Temp();
        var fakeExe = Path.Combine(temp.Root, OperatingSystem.IsWindows() ? "fake-pwsh.exe" : "fake-pwsh");
        await File.WriteAllTextAsync(fakeExe, "stub");

        var runner = new FakeRunner();
        var shell = PowerShellDiscovery.ForPowerShell(fakeExe);
        var options = BashToolOptions.FromShell(shell, temp.Root, ImmutableDictionary<string, string>.Empty, temp.Spill);
        var tool = new PowerShellTool(runner, options, () => "pi-powershell-fixed.log");

        Equal("powershell", tool.Name);
        Check(tool.Declaration.Value.TryGetProperty("constrainedSampling", out var cs) &&
            cs.GetProperty("strict").GetString() == "prefer", "Expected strict=prefer on PowerShellTool declaration.");
        Equal("Execute PowerShell commands", BuiltinToolPrompts.Snippets["powershell"]);
        Check(BuiltinToolPrompts.Guidelines["powershell"].Length == 1, "Expected PowerShell PI_* guideline.");

        // BuiltinToolCatalog registers powershell when provided, while CodingTools stays read/bash/edit/write
        var catalog = new BuiltinToolCatalog(temp.Root, temp.Root, powerShell: tool);
        Check(catalog.Registered.Any(item => item.Name == "powershell"), "PowerShell missing from catalog.");
        Check(catalog.Select(["read", "powershell"]).Select(item => item.Name).SequenceEqual(["read", "powershell"]), "Select powershell failed.");
        Check(BuiltinToolPrompts.DefaultToolNames.SequenceEqual(["read", "bash", "edit", "write"]), "Default tool names must remain unchanged.");

        // Policy denial prevents process runner execution
        var invoker = tool.CreateInvoker(new FixedPolicy(allow: false));
        var call = new ToolCallContent("ps-1", "powershell", JsonData.Parse("""{"command":"Write-Output hi"}"""));
        var assistant = new AssistantMessage("openai-responses", "openai", "m", 1, [call], TokenUsage.Zero, StopReason.ToolUse);
        var denied = await invoker.ExecuteAsync(new ToolInvocation(assistant, call, 0), CancellationToken.None);
        Check(denied.IsError && runner.Calls == 0, "Denied PowerShell action must not run.");

        // Prepared action validation rejects tampered command arguments
        var prepared = await tool.PrepareAsync(new ToolInvocation(assistant, call, 0), CancellationToken.None);
        Check(await tool.ValidateAsync(prepared, CancellationToken.None), "Valid prepared PowerShell action rejected.");
        var tampered = prepared with { CommandArguments = prepared.CommandArguments.SetItem(prepared.CommandArguments.Length - 1, "evil") };
        Check(!await tool.ValidateAsync(tampered, CancellationToken.None), "Tampered PowerShell action should fail ValidateAsync.");

        // Tight MaximumCommandCharacters still admits commands whose scriptblock wrapper and quote escaping exceed MaximumCommandCharacters
        var tightOptions = BashToolOptions.FromShell(shell, temp.Root, ImmutableDictionary<string, string>.Empty, temp.Spill) with
        {
            MaximumCommandCharacters = 32,
            MaximumArgumentCharacters = 4096
        };
        var tightTool = new PowerShellTool(runner, tightOptions);
        var quotedCall = new ToolCallContent("ps-tight", "powershell", JsonData.Parse("""{"command":"Write-Output 'hi'"}"""));
        var quotedAssistant = new AssistantMessage("openai-responses", "openai", "m", 1, [quotedCall], TokenUsage.Zero, StopReason.ToolUse);
        var tightPrepared = await tightTool.PrepareAsync(new ToolInvocation(quotedAssistant, quotedCall, 0), CancellationToken.None);
        Check(await tightTool.ValidateAsync(tightPrepared, CancellationToken.None), "Tight MaximumCommandCharacters rejected valid user command due to wrapper overhead.");

        // Large command near MaximumCommandCharacters (85,000 chars) fits within ToolInvoker's MaximumActionCharacters after Base64 encoding
        var largeOptions = BashToolOptions.FromShell(shell, temp.Root, ImmutableDictionary<string, string>.Empty, temp.Spill) with
        {
            MaximumCommandCharacters = 96_000,
            MaximumArgumentCharacters = 96_000
        };
        var largeTool = new PowerShellTool(runner, largeOptions);
        var largePolicy = new RecordingDenyPolicy();
        var largeInvoker = largeTool.CreateInvoker(largePolicy);
        var bigCommand = new string('a', 85_000);
        var bigCall = new ToolCallContent("ps-big", "powershell", JsonData.Parse(JsonSerializer.Serialize(new { command = bigCommand })));
        var bigAssistant = new AssistantMessage("openai-responses", "openai", "m", 1, [bigCall], TokenUsage.Zero, StopReason.ToolUse);
        var bigResult = await largeInvoker.ExecuteAsync(new ToolInvocation(bigAssistant, bigCall, 0), CancellationToken.None);
        Check(largePolicy.Invoked && bigResult.IsError && runner.Calls == 0,
            "Large valid PowerShell command was rejected before policy authorization (MaximumActionCharacters too small).");

        // UnavailableShellError fails without launching process
        var unavailable = new PowerShellTool(runner, BashToolOptions.Unavailable("No PowerShell installed.", "powershell.exe", temp.Root, ImmutableDictionary<string, string>.Empty, temp.Spill));
        var unavailInvoker = unavailable.CreateInvoker(new FixedPolicy(allow: true));
        var unavailResult = await unavailInvoker.ExecuteAsync(new ToolInvocation(assistant, call, 0), CancellationToken.None);
        Check(unavailResult.IsError && runner.Calls == 0, "Unavailable PowerShell should fail before launching.");
        Check(unavailResult.Content[0].Text.Contains("No PowerShell installed.", StringComparison.Ordinal), "Expected unavailable shell error text.");
    }

    private static async Task Execution()
    {
        ShellConfiguration shell;
        try { shell = PowerShellDiscovery.Resolve(); }
        catch (ShellDiscoveryException) when (!OperatingSystem.IsWindows()) { return; }

        using var temp = new Temp();
        IProcessRunner runner = OperatingSystem.IsWindows()
            ? new NativeProcessRunner(new ProcessRunnerOptions(MaximumRawBytes: int.MaxValue))
            : new PiSharp.Tools.Processes.Unix.UnixProcessRunner(
                new PiSharp.Tools.Processes.Unix.PosixSpawnProcessAdmission(),
                new ProcessRunnerOptions(MaximumRawBytes: int.MaxValue));

        var env = ShellDiscovery.ShellEnvironment(
            Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(e => e.Key is string k && k.Length > 0 && !k.Contains('=') && e.Value is string)
                .ToDictionary(e => (string)e.Key, e => (string)e.Value!, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal),
            ShellDiscovery.BinDirectory(), OperatingSystem.IsWindows());

        var tool = new PowerShellTool(runner, BashToolOptions.FromShell(shell, temp.Root, env, temp.Spill));
        var invoker = tool.CreateInvoker(new FixedPolicy(allow: true));

        async Task<ToolResult> Run(string command, double? timeout = null)
        {
            var payload = timeout is { } t
                ? JsonSerializer.Serialize(new { command, timeout = t })
                : JsonSerializer.Serialize(new { command });
            var call = new ToolCallContent("ps-" + Guid.NewGuid().ToString("N")[..6], "powershell", JsonData.Parse(payload));
            var assistant = new AssistantMessage("openai-responses", "openai", "m", 1, [call], TokenUsage.Zero, StopReason.ToolUse);
            return await invoker.ExecuteAsync(new ToolInvocation(assistant, call, 0), CancellationToken.None);
        }

        // 1. Standard output + UTF-8 preservation
        var hello = await Run("Write-Output 'hello-pisharp-ps café'");
        Check(!hello.IsError, "Expected exit 0: " + hello.Content[0].Text);
        Check(hello.Content[0].Text.Contains("hello-pisharp-ps café", StringComparison.Ordinal), "Unexpected output: " + hello.Content[0].Text);
        Equal(0, hello.StructuredContent!.Value.GetProperty("exit_code").GetInt32());

        // 1b. Leading `using namespace` statements and smart apostrophes parse cleanly in their own scriptblock boundary
        var usingResult = await Run("using namespace System.Text; [StringBuilder]::new('using-ok').ToString()");
        Check(!usingResult.IsError, "Expected using namespace command to succeed: " + usingResult.Content[0].Text);
        Check(usingResult.Content[0].Text.Contains("using-ok", StringComparison.Ordinal), "Unexpected using output: " + usingResult.Content[0].Text);

        var smartQuote = await Run("Write-Output \"don\u2019t\"");
        Check(!smartQuote.IsError, "Expected smart apostrophe command to succeed: " + smartQuote.Content[0].Text);
        Check(smartQuote.Content[0].Text.Contains("don\u2019t", StringComparison.Ordinal), "Unexpected smart apostrophe output: " + smartQuote.Content[0].Text);

        // 2. Non-zero exit code
        var fail = await Run("Write-Output 'before-fail'; exit 7");
        Check(fail.IsError, "Expected IsError on non-zero exit.");
        Check(fail.Content[0].Text.Contains("before-fail", StringComparison.Ordinal), "Missing stdout on failure.");
        Check(fail.Content[0].Text.Contains("Command exited with code 7", StringComparison.Ordinal), "Missing exit code message: " + fail.Content[0].Text);
        Equal(7, fail.StructuredContent!.Value.GetProperty("exit_code").GetInt32());

        // 3. Timeout kills child process tree cleanly
        var timedOut = await Run("Start-Sleep -Seconds 15", timeout: 0.4);
        Check(timedOut.IsError, "Expected timeout error.");
        Check(timedOut.Content[0].Text.Contains("Command timed out after 0.4 seconds", StringComparison.Ordinal), "Unexpected timeout text: " + timedOut.Content[0].Text);

        // 4. Tail truncation (> 2000 lines) spills full output to pi-powershell-*.log
        var big = await Run("1..2200 | ForEach-Object { \"ps-line-$_\" }");
        Check(!big.IsError, "Expected big output to succeed.");
        Check(big.Content[0].Text.Contains("ps-line-2200", StringComparison.Ordinal), "Tail should keep last line.");
        Check(!big.Content[0].Text.Contains("ps-line-1\n", StringComparison.Ordinal), "Tail should truncate earliest lines.");
        Check(big.Content[0].Text.Contains("Full output:", StringComparison.Ordinal), "Missing spill file notice.");
        var spillPath = big.Details.Value.GetProperty("fullOutputPath").GetString()!;
        Check(File.Exists(spillPath), "Spill file does not exist: " + spillPath);
        var fullText = await File.ReadAllTextAsync(spillPath);
        Check(fullText.Contains("ps-line-1", StringComparison.Ordinal) && fullText.Contains("ps-line-2200", StringComparison.Ordinal), "Spill file missing full lines.");
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public int Calls;
        public ValueTask<ProcessRunResult> RunAsync(ProcessRequest request, ProcessOutputCallback? onOutput, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("FakeRunner should not have been called.");
        }
    }

    private sealed class FixedPolicy(bool allow) : IToolActionPolicy
    {
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ToolActionAuthorization(allow));
    }

    private sealed class RecordingDenyPolicy : IToolActionPolicy
    {
        public bool Invoked;
        public ValueTask<ToolActionAuthorization> AuthorizeAsync(ToolInvocation invocation, PreparedToolAction action, CancellationToken cancellationToken)
        {
            Invoked = true;
            return ValueTask.FromResult(new ToolActionAuthorization(false));
        }
    }

    private sealed class Temp : IDisposable
    {
        public string Root { get; }
        public string Spill { get; }
        public Temp()
        {
            Root = Path.Combine(Path.GetTempPath(), "pisharp-ps-tests-" + Guid.NewGuid().ToString("N"));
            Spill = Path.Combine(Root, "spill");
            Directory.CreateDirectory(Spill);
        }
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch { } }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}"); }
    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
