using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Threading.Channels;
using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Tui;
using PiSharp.Tui.Input;

internal static class FocusCallerTests
{
    internal static async Task<object> Run(string evidenceRoot)
    {
        var root = Path.Combine(evidenceRoot, "actual-focus-caller-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var session = Path.Combine(root, "session.jsonl"); var script = Path.Combine(root, "offline.json");
        await File.WriteAllTextAsync(script, JsonSerializer.Serialize(new { schemaVersion = 1, turns = new[] { new { requiredInputTexts = Array.Empty<string>(), events = new object[] {
            new { type = "response.output_item.added", output_index = 0, item = new { type = "message", id = "unused", content = Array.Empty<object>() } },
            new { type = "response.output_text.delta", output_index = 0, item_id = "unused", delta = "unused" },
            new { type = "response.output_item.done", output_index = 0, item = new { type = "message", id = "unused", content = new[] { new { type = "output_text", text = "unused" } } } },
            new { type = "response.completed", response = new { status = "completed", output = Array.Empty<object>(), usage = new { input_tokens = 1, output_tokens = 1, total_tokens = 2 } } }
        } } } }));
        using var created = new StringWriter(); using var errors = new StringWriter();
        var createResult = await SessionCommands.RunAsync(["session", "create", "--session", session, "--workspace", root, "--offline-api", "openai-responses"], created, errors);
        if (createResult != 0) throw new InvalidOperationException("Offline actual caller setup failed: " + errors);
        var sink = new CallerTerminal(); var history = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var running = TerminalSessionCommand.RunObservedAsync(["session", "terminal", "--terminal-preview", "--session", session, "--workspace", root,
            "--offline-api", "openai-responses", "--offline-script", script], sink, sink, errors, Observe, stop.Token);
        int result;
        try { await history.Task.WaitAsync(stop.Token); await sink.Input.Writer.WriteAsync("a\u0015\u0004", stop.Token); result = await running.WaitAsync(stop.Token); }
        finally { stop.Cancel(); sink.Input.Writer.TryComplete(); await running; }
        var calls = typeof(TerminalSessionCommand).Assembly.GetTypes().Where(t => t.FullName!.StartsWith(typeof(TerminalSessionCommand).FullName!, StringComparison.Ordinal))
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)).SelectMany(CalledConstructors).ToArray();
        var ownerConstructions = calls.Count(c => c.DeclaringType == typeof(TerminalEditorFocusOwner));
        var viewOwnerBindings = calls.Count(c => c.DeclaringType?.Name == "TerminalSessionView" && c.GetParameters().Any(p => p.ParameterType == typeof(TerminalEditorFocusOwner)));
        var inputOwnerBindings = calls.Count(c => c.DeclaringType?.Name == "TerminalChatInput" && c.GetParameters().Any(p => p.ParameterType == typeof(TerminalEditorFocusOwner)));
        // Actual view validates its snapshot against the explicitly supplied owner's unique editor lifetime.
        // A successful captured paint plus one compiled owner construction proves this caller shares it.
        var pass = result == 0 && errors.ToString().Length == 0 && ownerConstructions == 1 && viewOwnerBindings == 1 && inputOwnerBindings == 1 &&
            sink.StartedReads == sink.SettledReads && sink.ActiveReads == 0 && sink.StartedWrites == sink.SettledWrites && sink.ActiveWrites == 0 && sink.Disposals == 0 && sink.Frames.Any(f => f.Contains("a\u001b[7m", StringComparison.Ordinal)) && sink.Frames.All(f => !f.Contains("\u001b[?25h", StringComparison.Ordinal));
        var report = new { root, session, script, createResult, result, diagnostic = errors.ToString(), ownerConstructions, viewOwnerBindings, inputOwnerBindings,
            capturedPaintValidatedAgainstExplicitOwner = result == 0, sink.StartedReads, sink.SettledReads, sink.ActiveReads, sink.StartedWrites, sink.SettledWrites, sink.ActiveWrites, sink.Disposals,
            frames = sink.Frames, pass, runtime = "Private injected offline real caller, not the shared Commands/Input or physical gate" };
        await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return report;
        ValueTask Observe(JsonData record, CancellationToken token)
        { var body = record.Value; if (body.GetProperty("type").GetString() == "response" && body.GetProperty("command").GetString() == "get_messages") history.TrySetResult(); return ValueTask.CompletedTask; }
    }
    private static IEnumerable<ConstructorInfo> CalledConstructors(MethodInfo method)
    {
        var bytes = method.GetMethodBody()?.GetILAsByteArray(); if (bytes is null) yield break;
        var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(c => unchecked((ushort)c.Value));
        for (var i = 0; i < bytes.Length;)
        {
            ushort value = bytes[i++]; if (value == 0xfe) value = (ushort)(0xfe00 | bytes[i++]); var code = codes[value];
            var size = code.OperandType switch { OperandType.InlineNone => 0, OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2, OperandType.InlineI8 or OperandType.InlineR => 8, OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i), _ => 4 };
            if (code == OpCodes.Newobj && method.Module.ResolveMethod(BitConverter.ToInt32(bytes, i)) is ConstructorInfo constructor) yield return constructor;
            i += size;
        }
    }
    private sealed class CallerTerminal : IConsoleTerminal, ITerminalViewportSource
    {
        internal readonly Channel<string> Input = Channel.CreateUnbounded<string>(); internal readonly List<string> Frames = [];
        internal int StartedReads, SettledReads, ActiveReads, StartedWrites, SettledWrites, ActiveWrites, Disposals;
        public TerminalViewport ReadViewport() => new(20, 24, 0, 0, 20, 24);
        public TerminalLeaseSnapshot Snapshot { get { var s = new TerminalConsoleState(0, 0, 65001, 65001, 25, true, 0, 0); return new(s, s, null, false, false, ActiveReads, ActiveWrites, StartedReads, SettledReads, StartedWrites, SettledWrites); } }
        public async ValueTask<int> ReadAsync(Memory<char> destination, CancellationToken token = default)
        { StartedReads++; ActiveReads++; try { if (!await Input.Reader.WaitToReadAsync(token)) return 0; if (!Input.Reader.TryRead(out var text)) throw new IOException("Read ownership"); text.AsMemory().CopyTo(destination); return text.Length; } finally { ActiveReads--; SettledReads++; } }
        public ValueTask WriteAsync(ReadOnlyMemory<char> frame, CancellationToken token = default)
        { StartedWrites++; ActiveWrites++; try { token.ThrowIfCancellationRequested(); Frames.Add(frame.ToString()); return ValueTask.CompletedTask; } finally { ActiveWrites--; SettledWrites++; } }
        public ValueTask DisposeAsync() { Disposals++; throw new InvalidOperationException("Borrowed caller terminal disposed"); }
    }
}
