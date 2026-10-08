using PiSharp.Cli.Commands;
using PiSharp.Contracts;
using PiSharp.Tui.Input;
using System.Text.Json;

// Authored only: actual configured command, offline owned fixtures, no provider/live calls.
internal static class TerminalNativeInterruptCases
{
    internal static IEnumerable<(string Id, Func<ConsumerEvidence, Task> Run)> Cases(string[] args) =>
    [("interrupt-actual-command-idle-draft-and-held-shutdown-joins", e => Run(args[1], e))];

    private static async Task Run(string root, ConsumerEvidence e)
    {
        await DequeueWireModes(e);
        var files = await StartupOwnedFiles.Create(root, plugin: false, e); var before = StartupOwnedFiles.Hash(files.Session);
        var trace = new StartupTrace(); var terminal = new StartupControlledTerminal(trace);
        using var errors = new StringWriter(); using var cancellation = new CancellationTokenSource();
        var original = TerminalSessionCommand.RunObservedConfiguredAsync(files.Args(), terminal, terminal, errors,
            trace.Observe, files.Configuration, cancellation.Token, kittyProtocolActive: true);
        try
        {
            await terminal.WaitWrite("[history]"); await terminal.WaitReads(1);
            await terminal.Feed("draft\u001b[27u\u001b[113;3u");
            var kept = await trace.WaitRecord(r => r.Value.TryGetProperty("command", out var command) && command.GetString() == "pisharp_restore_queue");
            Check(kept.Value.GetProperty("data").GetProperty("text").GetString() == "draft", "Idle Escape discarded actual editor text.");
            await terminal.Feed("\u0003"); await terminal.WaitWrite("[draft discarded]");
            Check(!original.IsCompleted, "First Ctrl+C shut down the actual command.");
            var held = terminal.HoldWrite("BLOCKED"); await terminal.Feed("BLOCKED");
            await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await terminal.Feed("\u0003\u0003"); await held.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!original.IsCompleted && terminal.Snapshot.ActiveWrites == 1 && errors.ToString().Length == 0,
                "Command returned or diagnosed before its entered physical write joined.");
            held.Release.TrySetResult(); var result = await original;
            Check(result == 0 && errors.ToString().Length == 0, "Clean user shutdown did not return success after original joins.");
            terminal.AssertJoined(); await files.Complete(e);
            Check(before == StartupOwnedFiles.Hash(files.Session) && !File.Exists(files.Target), "Idle interrupt changed durable data/effects.");
            Check(!trace.Records().Any(r => r.Value.GetProperty("type").GetString() == "agent_start"), "Idle control started provider work.");
            e.Observe("actual-command-interrupt-physical-join", new { result, errors = errors.ToString(), terminal = terminal.Evidence, trace = trace.Rows() });
        }
        finally { cancellation.Cancel(); terminal.End(); terminal.Release(); await original; terminal.AssertJoined(); }
    }
    private static async Task DequeueWireModes(ConsumerEvidence e)
    {
        foreach (var kitty in new[] { false, true })
        {
            var decoder = new TerminalInputDecoder(kittyProtocolActive: kitty);
            var legacy = decoder.Feed("\u001bq").Single();
            var encoded = decoder.Feed("\u001b[113;3u").Single();
            Check(TerminalInputDecoder.MatchesKey(legacy, "alt+q") == !kitty,
                "Legacy Alt+Q eligibility no longer follows original protocol mode.");
            Check(TerminalInputDecoder.MatchesKey(encoded, "alt+q"), "Kitty Alt+Q did not match the dequeue binding.");
        }
        SelectorFixture? owner = null; var requests = new List<JsonData>();
        await using var f = await SelectorFixture.Create(kitty: true, commandHandler: async (request, token) =>
        {
            requests.Add(request);
            Check(request.Value.GetProperty("type").GetString() == "pisharp_restore_queue", "Wrong dequeue command.");
            await owner!.Frontend.ObserveAsync(JsonData.Parse(JsonSerializer.Serialize(new
            {
                type = "response", id = request.Value.GetProperty("id").GetString(),
                command = "pisharp_restore_queue", success = true,
                data = new { generation = request.Value.GetProperty("generation").GetInt64(), count = 0,
                    text = request.Value.GetProperty("currentText").GetString() }
            })), token);
        });
        owner = f; await f.TypeDraft("draft");
        await f.Key("\u001b[27u"); await f.Key("\u001bq");
        await f.Key("\u001b[113;3:2u"); await f.Key("\u001b[113;3:3u");
        Check(requests.Count == 0 && f.LastDraft!.Text == "draft", "Idle Escape, legacy Alt+Q or repeat/release restored or discarded text.");
        await f.Key("\u001b[113;3u");
        Check(requests.Count == 1 && f.LastDraft!.Text == "draft", "Kitty dequeue press did not preserve the empty-queue draft.");
        await f.StopAsync();
        Check(f.Terminal.ActiveReads == 0 && f.Terminal.ActiveWrites == 0 && f.Terminal.MaximumReaders == 1 && f.Terminal.Disposals == 0,
            "Dequeue controls detached physical I/O or disposed the borrowed terminal.");
        e.Observe("native-kitty-dequeue-wire-mode-and-empty-queue-controls", new { requests,
            draft = f.LastDraft!.Text, f.Terminal.MaximumReaders, f.Terminal.Disposals });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
