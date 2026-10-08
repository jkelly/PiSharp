using System.Text.Json;
using PiSharp.Cli.Commands;
using PiSharp.Cli.Interactive;
using PiSharp.Tui;

// Authored only / UNEXECUTED. Calls the actual Program terminal-entrypoint lane;
// the injected console opener must never run on rejection. No OS console/process is opened.
internal static class TerminalStartupValidationCases
{
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases() =>
    [
        ("terminal-entrypoint-invalid-args-precede-console-failure-exact-rpc-diagnostic", e => Rejected(e, extension: false)),
        ("terminal-entrypoint-unapproved-extension-exact-rpc-diagnostic-no-acquisition", e => Rejected(e, extension: true)),
        ("terminal-configured-unapproved-extension-rejects-before-terminal-settings-or-ui", Configured),
        ("terminal-entrypoint-valid-args-still-observe-owned-console-failure", ConsoleFailure)
    ];
    private static string[] ValidArgs()
    {
        var root = Path.Combine(Path.GetTempPath(), "authored-no-acquisition");
        return ["session", "terminal", "--terminal-preview", "--session", Path.Combine(root, "session.jsonl"),
            "--workspace", root, "--offline-script", Path.Combine(root, "script.json"), "--offline-api", "openai-responses"];
    }
    private static string[] Unapproved() => ValidArgs().Concat(new[] { "--extension-package", Path.Combine(Path.GetTempPath(), "authored-unapproved-package") }).ToArray();
    private static async Task Rejected(ConsumerEvidence e, bool extension)
    {
        string[] args = extension ? Unapproved() : ["session", "terminal", "--terminal-preview", "--unknown-option", "value"];
        using var diagnostics = new StringWriter(); var opens = 0;
        ValueTask<WindowsConsoleTerminal> Open(CancellationToken token)
        { opens++; return ValueTask.FromException<WindowsConsoleTerminal>(new TerminalException(TerminalFailure.NotConsole)); }
        var actual = await PiSharp.Cli.Program.RunTerminalHostAsync(args, default, Open, diagnostics);
        var expected = await RpcDiagnostic(args);
        e.Observe("startup-diagnostic-comparison", new { extension, actual, expectedResult = expected.Result, opens,
            actualDiagnostic = DiagnosticFacts(diagnostics.ToString()), expectedDiagnostic = DiagnosticFacts(expected.Text) });
        Check(actual == expected.Result && actual == 2 && opens == 0 && diagnostics.ToString() == expected.Text,
            "Startup rejection acquired console resources, lost the original RPC diagnostic, or was replaced by console failure.");
        using var body = JsonDocument.Parse(diagnostics.ToString());
        Check(body.RootElement.GetProperty("cleanupFailureCount").GetInt32() == 0 &&
            body.RootElement.EnumerateObject().Count() == 6, "Pre-acquisition startup rejection changed the fixed RPC diagnostic shape.");
        Check(body.RootElement.GetProperty("code").GetString() == (extension ? "ExecutionApprovalRequired" : "InvalidArguments"),
            "Typed startup error collapsed into a generic terminal failure.");
        e.Observe("actual-entrypoint-rejection-before-console", new { extension, actual, opens, diagnostic = body.RootElement.Clone(), exactRpcDiagnostic = true });
    }
    private static async Task Configured(ConsumerEvidence e)
    {
        var args = Unapproved(); var terminal = new UnenteredTerminal(); using var diagnostics = new StringWriter();
        var configuration = TerminalKeybindingConfigurationLoader.Load("authored-keybindings", "win32", readText: _ => null);
        var actual = await TerminalSessionCommand.RunConfiguredAsync(args, terminal, terminal, diagnostics, configuration);
        var expected = await RpcDiagnostic(args);
        e.Observe("configured-startup-diagnostic-comparison", new { actual, expectedResult = expected.Result, terminal.Acquisitions,
            actualDiagnostic = DiagnosticFacts(diagnostics.ToString()), expectedDiagnostic = DiagnosticFacts(expected.Text) });
        Check(actual == 2 && diagnostics.ToString() == expected.Text && terminal.Acquisitions == 0,
            "Explicit configured startup bypassed validation or acquired terminal/UI before rejecting unapproved extension.");
        e.Observe("actual-configured-rejection-no-resource-acquisition", new { actual, terminal.Acquisitions, diagnostic = diagnostics.ToString() });
    }
    private static async Task ConsoleFailure(ConsumerEvidence e)
    {
        using var diagnostics = new StringWriter(); var opens = 0;
        ValueTask<WindowsConsoleTerminal> Open(CancellationToken token)
        { opens++; return ValueTask.FromException<WindowsConsoleTerminal>(new TerminalException(TerminalFailure.NotConsole)); }
        var actual = await PiSharp.Cli.Program.RunTerminalHostAsync(ValidArgs(), default, Open, diagnostics);
        e.Observe("console-failure-diagnostic-facts", new { actual, opens, diagnostic = DiagnosticFacts(diagnostics.ToString()) });
        using var body = JsonDocument.Parse(diagnostics.ToString());
        Check(!body.RootElement.TryGetProperty("cleanupFailureCount", out _) && body.RootElement.EnumerateObject().Count() == 4,
            "Console admission failure changed its existing diagnostic shape.");
        Check(actual == 1 && opens == 1 && body.RootElement.GetProperty("code").GetString() == "NotConsole",
            "Valid arguments bypassed the real console admission/failure path.");
        e.Observe("actual-entrypoint-valid-args-console-failure-control", new { actual, opens, diagnostic = body.RootElement.Clone() });
    }
    private static async Task<(int Result, string Text)> RpcDiagnostic(string[] terminalArgs)
    {
        var args = terminalArgs.Where(value => value != "--terminal-preview").ToArray(); args[1] = "rpc";
        using var input = new MemoryStream(); using var output = new MemoryStream(); using var error = new StringWriter();
        var result = await RpcSessionCommand.RunAsync(args, input, output, error);
        Check(output.Length == 0, "Rejected RPC startup produced output/effects."); return (result, error.ToString());
    }
    // Never retain raw messages, paths, payloads, or arbitrary property values. Parse/hash only bounded output.
    private static object DiagnosticFacts(string text)
    {
        const int limit = 4096;
        if (text.Length > limit) return new { characters = text.Length, overLimit = true };
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new { characters = text.Length, hash, objectBody = false };
            int? Integer(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var number) ? number : null;
            var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            return new { characters = text.Length, hash, objectBody = true, fields = root.EnumerateObject().Count(),
                schemaVersion = Integer("schemaVersion"), cleanupFailureCount = Integer("cleanupFailureCount"),
                code = code is "InvalidArguments" or "ExecutionApprovalRequired" or "NotConsole" ? code : "other",
                failed = root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "failed",
                effectsMayHaveCompleted = root.TryGetProperty("effectsMayHaveCompleted", out var effects) && effects.ValueKind == JsonValueKind.True };
        }
        catch (JsonException) { return new { characters = text.Length, hash, invalidJson = true }; }
    }
    private sealed class UnenteredTerminal : IConsoleTerminal, ITerminalViewportSource
    {
        internal int Acquisitions;
        private Exception Unexpected() { Acquisitions++; return new InvalidOperationException("Rejected startup entered a terminal resource."); }
        public TerminalLeaseSnapshot Snapshot => throw Unexpected();
        public TerminalViewport ReadViewport() => throw Unexpected();
        public ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default) => throw Unexpected();
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default) => throw Unexpected();
        public ValueTask DisposeAsync() => throw Unexpected();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
