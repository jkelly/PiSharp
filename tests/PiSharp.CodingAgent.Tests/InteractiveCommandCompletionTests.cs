using System.Text.Json;
using PiSharp.Cli.Interactive;
using PiSharp.Contracts;
using PiSharp.Rpc.Ui;

internal static class InteractiveCommandCompletionTests
{
    internal static IEnumerable<(string Name, Func<Task> Run)> Cases() =>
    [
        ("interactive.command-completion-preserves-original-prefix-array-null-and-held-dialog", HeldDialog),
        ("interactive.command-completion-bounds-admission-and-rejects-unsolicited-or-malformed-response", Bounds),
        ("interactive.command-completion-failed-send-releases-only-its-own-query", FailedSend)
    ];

    private static async Task HeldDialog()
    {
        using var output = new StringWriter();
        using var frontend = new InteractiveSessionFrontend(output, nativePresentation: true);
        var sent = new List<JsonData>();
        frontend.Bind((record, token) => { token.ThrowIfCancellationRequested(); sent.Add(record); return Task.CompletedTask; });
        await Catalog(frontend);
        var identity = new RpcExtensionUiPresentationIdentity("original-dialog", 1, 2, "commands-owner", 3);
        await frontend.PublishedAsync(new(identity, Record(new { type = "extension_ui_request", id = identity.RequestId,
            method = "select", title = "Available Commands", options = new[] { "one", "two" } })), default);
        const string prefix = " \u6587\U0001f642\0";
        await frontend.LineAsync("/complete commands " + prefix, default);
        Equal("pisharp_complete_extension_command", sent.Single().Value.GetProperty("type").GetString());
        Equal("commands", sent[0].Value.GetProperty("command").GetString());
        Equal(prefix, sent[0].Value.GetProperty("prefix").GetString());
        var id = sent[0].Value.GetProperty("id").GetString();
        var completions = JsonData.Parse("[{\"value\":\"one\",\"label\":\"one\\u0000\\u001b[31m\"}]");
        await Response(frontend, id!, completions);
        Check(output.ToString().Contains("[completion commands] " + completions, StringComparison.Ordinal), "Full completion array was changed.");
        Check(!output.ToString().Contains('\u001b'), "Completion executed a terminal escape.");
        await frontend.LineAsync("/complete commands", default);
        Equal("", sent[1].Value.GetProperty("prefix").GetString());
        await Response(frontend, sent[1].Value.GetProperty("id").GetString()!, JsonData.Parse("null"));
        Check(output.ToString().Contains("[completion commands] null", StringComparison.Ordinal), "Null completion was changed.");
        await frontend.LineAsync("2", default);
        Equal("extension_ui_response", sent[2].Value.GetProperty("type").GetString());
        Equal(identity.RequestId, sent[2].Value.GetProperty("id").GetString());
        Equal("two", sent[2].Value.GetProperty("value").GetString());
        await frontend.RetiredAsync(new(identity, PiSharp.Extensions.ExtensionUiOutcomeKind.Value, null, true, true), default);
        await frontend.LineAsync("after dialog", default);
        Equal("prompt", sent[3].Value.GetProperty("type").GetString());
    }

    private static async Task Bounds()
    {
        using var output = new StringWriter();
        using var frontend = new InteractiveSessionFrontend(output);
        var sent = new List<JsonData>();
        frontend.Bind((record, _) => { sent.Add(record); return Task.CompletedTask; });
        await Catalog(frontend);
        await frontend.LineAsync("/complete unavailable prefix", default);
        await Reject(async () => { await frontend.LineAsync("/complete commands " + new string('a', 65_537), default); });
        Equal(0, sent.Count);
        for (var index = 0; index < 16; index++) await frontend.LineAsync("/complete commands " + index, default);
        await frontend.LineAsync("/complete commands seventeenth", default);
        Equal(16, sent.Count);
        await Reject(() => Response(frontend, "foreign", JsonData.Parse("null")));
        var first = sent[0].Value.GetProperty("id").GetString()!;
        await Reject(() => Response(frontend, first, JsonData.Parse("{}")));
        await frontend.LineAsync("/complete commands released", default);
        Equal(17, sent.Count);
        await Response(frontend, sent[1].Value.GetProperty("id").GetString()!, JsonData.Parse("[]"));
        await Reject(() => Response(frontend, sent[1].Value.GetProperty("id").GetString()!, JsonData.Parse("[]")));
    }

    private static async Task FailedSend()
    {
        using var output = new StringWriter();
        using var frontend = new InteractiveSessionFrontend(output);
        await Catalog(frontend);
        var sent = new List<JsonData>();
        frontend.Bind((record, _) => { sent.Add(record); return Task.FromException(new IOException("actual sender failed")); });
        for (var index = 0; index < 17; index++)
        {
            try { await frontend.LineAsync("/complete commands " + index, default); throw new InvalidOperationException("Send did not fail."); }
            catch (IOException error) when (error.Message == "actual sender failed") { }
        }
        Equal(17, sent.Count);
        await Reject(() => Response(frontend, sent[0].Value.GetProperty("id").GetString()!, JsonData.Parse("null")));
        frontend.Bind((record, _) => { sent.Add(record); return Task.CompletedTask; });
        await frontend.LineAsync("/complete commands recovery", default);
        Equal(18, sent.Count);
        await Response(frontend, sent[^1].Value.GetProperty("id").GetString()!, JsonData.Parse("[]"));
    }

    private static ValueTask Catalog(InteractiveSessionFrontend frontend) => frontend.ObserveAsync(Record(new
    {
        type = "response", id = "chat-commands", command = "get_commands", success = true,
        data = new { commands = new[] { new { name = "commands" } } }
    }), default);
    private static Task Response(InteractiveSessionFrontend frontend, string id, JsonData completions) => frontend.ObserveAsync(
        JsonData.Parse("{\"type\":\"response\",\"id\":" + JsonSerializer.Serialize(id) +
            ",\"command\":\"pisharp_complete_extension_command\",\"success\":true,\"data\":{\"completions\":" + completions + "}}"), default).AsTask();
    private static JsonData Record(object value) => JsonData.Parse(JsonSerializer.Serialize(value));
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Malformed or unowned completion was admitted.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; actual {actual}.");
}
