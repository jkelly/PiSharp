// Pi faac585884ae2c013a6b1400f3e9e77f10b34bac (MIT): packages/coding-agent/test/agent-session-runtime-diagnostics.test.ts over
// packages/coding-agent/src/core/agent-session-runtime.ts (AgentSessionRuntime.takeUnreportedDiagnostics).
using PiSharp.CodingAgent;

internal static class Sync160RuntimeDiagnosticsCases
{
    internal static (string Name, Func<Task> Run)[] Cases() =>
    [
        // The runtime collects its diagnostics again on every session replacement, but modes only reported the startup diagnostics,
        // so a problem that first appeared on /new or /resume was never shown. Found while fixing #11264.
        ("sync160 runtime diagnostics returns each diagnostic once, at startup and after replacements", () =>
        {
            var settingsWarning = new AgentSessionRuntimeDiagnostic("warning", "settings warning");
            var extensionError = new AgentSessionRuntimeDiagnostic("error", "extension failed");
            var runtime = new AgentSessionRuntimeDiagnostics();
            runtime.Replace([settingsWarning]);
            Same([settingsWarning], runtime.TakeUnreported());
            Same([], runtime.TakeUnreported());
            // A replacement's runtime collects its diagnostics again.
            runtime.Replace([settingsWarning, extensionError]);
            Same([settingsWarning, extensionError], runtime.Current);
            Same([extensionError], runtime.TakeUnreported());
            Same([], runtime.TakeUnreported());
            runtime.Replace([settingsWarning, extensionError]);
            Same([], runtime.TakeUnreported());
            return Task.CompletedTask;
        }),
        ("sync160 runtime diagnostics keys on type and message and keeps the runtime's order", () =>
        {
            var runtime = new AgentSessionRuntimeDiagnostics();
            runtime.Replace([new("warning", "same text"), new("error", "same text"), new("warning", "same text")]);
            // The same type and message twice in one runtime is one report.
            Same([new("warning", "same text"), new("error", "same text")], runtime.TakeUnreported());
            runtime.Add(new("info", "later"));
            runtime.Add(new("warning", "same text"));
            Same([new("info", "later")], runtime.TakeUnreported());
            Check(runtime.Current.Length == 5, "Current keeps every diagnostic of the runtime.");
            runtime.Replace([]);
            Check(runtime.Current.IsEmpty && runtime.TakeUnreported().IsEmpty, "A runtime without diagnostics reports none.");
            var rejected = false;
            try { runtime.Replace([null!]); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "A null diagnostic was accepted.");
            return Task.CompletedTask;
        })
    ];

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Same(AgentSessionRuntimeDiagnostic[] expected, IEnumerable<AgentSessionRuntimeDiagnostic> actual)
    {
        var values = actual.ToArray();
        Check(expected.SequenceEqual(values), "Expected [" + string.Join(", ", expected.Select(value => value.ToString())) + "], actual [" +
            string.Join(", ", values.Select(value => value.ToString())) + "].");
    }
}
