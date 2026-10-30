using System.Collections.Immutable;
using PiSharp.Contracts;
using PiSharp.Durable.Chord;
using PiSharp.Durable.Hosting;
using PiSharp.Durable.Hosting.Testing;

internal static partial class Program
{
    internal static IEnumerable<(string, Func<Task>)> DeviceCases() =>
    [
        Case("device.directories-for-choosing-a-working-directory", async () =>
        {
            await using var device = await TestDevice.OpenAsync("device-dirs");
            foreach (var name in new[] { "alpha", "Beta", ".hidden", "node_modules" }) Directory.CreateDirectory(Path.Join(device.Work, name));
            File.WriteAllText(Path.Join(device.Work, "file.txt"), "not a directory");

            var starts = device.Host.BrowseDirectories("");
            Equal("", starts.Path, "the start directories' listing");
            Equal(device.Work, starts.Directories.Single().Path, "the one start directory");
            var listing = device.Host.BrowseDirectories(device.Work);
            Equal("alpha,Beta", string.Join(",", listing.Directories.Select(entry => entry.Name)), "children, by name, without dot directories or node_modules");
            Equal(Directory.GetParent(device.Work)!.FullName, listing.Parent, "parent");
            Throws<HostNotFoundException>(() => device.Host.BrowseDirectories(Path.Join(device.Work, "missing")), "a missing directory");
        }),

        Case("device.models-list-the-available-chat-models", async () =>
        {
            await using var device = await TestDevice.OpenAsync("device-models");
            var models = await device.Host.ModelsAsync();
            Equal("faux/echo,faux/echo-small", string.Join(",", models.Select(model => $"{model.Provider}/{model.Id}")), "models");
            Equal(128_000.0, models[0].ContextWindow, "window");
            Equal(0.0, models[0].Cost?.Input, "cost");
            Equal(FauxDurableModels.EchoRef, device.Host.Info.DefaultModel, "default model");
        }),

        Case("device.faux-model-grammar", () =>
        {
            static JsonValue User(string text) => JsonValue.Object([new("role", "user"), new("content", text)]);
            Equal("Echo: hello", FauxDurableModels.Reply([User("hello")]).Text, "echo");
            var bash = FauxDurableModels.Reply([User("!ls -la")]).Call!;
            Equal("bash", bash.Name, "a ! command calls bash");
            Check(bash.Arguments.ToString().Contains("ls -la"), "with the command: " + bash.Arguments);
            var probe = FauxDurableModels.Reply([User("tool: probe {\"x\": 1}")]).Call!;
            Equal("probe", probe.Name, "tool: calls a named tool");
            Check(probe.Arguments.ToString().Contains("\"x\""), "with its arguments: " + probe.Arguments);
            var slow = FauxDurableModels.Reply([User("slow: one two")]);
            Check(slow.Slow && slow.Text == "one two", "slow: streams slowly");
            var result = JsonValue.Object([new("role", "toolResult"), new("toolName", "bash"),
                new("content", JsonValue.Array([JsonValue.Object([new("type", "text"), new("text", "out")])]))]);
            Equal("Tool finished: bash: out", FauxDurableModels.Reply(ImmutableArray.Create(User("!x"), result)).Text, "a tool result");
            Equal("Echo: ", FauxDurableModels.Reply([]).Text, "no message");
            Equal("upstream down", FauxDurableModels.Reply([User("error: upstream down")]).Error, "error: fails with the message");
            Equal(FauxDurableModels.ErrorMessage, FauxDurableModels.Reply([User("error:")]).Error, "or with the default one");
            Check(FauxDurableModels.Reply([User("errors: are echoed")]).Error is null, "only the exact prefix fails");
        }),

        Case("device.faux-error-prompt-ends-with-a-provider-error", async () =>
        {
            var faux = new FauxDurableModels();
            var model = faux.GetModel(FauxDurableModels.ProviderId, FauxDurableModels.Echo.Id)!;
            var events = new List<StreamEvent>();
            await foreach (var item in faux.StreamSimple(model, [JsonValue.Object([new("role", "user"), new("content", "error: The upstream said no.")])], null, default))
                events.Add(item);
            Check(events[0] is StreamStarted, "it starts as any answer does");
            var end = events[^1] as StreamError ?? throw new InvalidOperationException("ends with a StreamError: " + events[^1]);
            Equal(StopReason.Error, end.Reason, "stop reason error");
            Equal(StopReason.Error, end.Message.StopReason, "on the message too");
            Check(end.Message.Content.IsEmpty, "no content");
            Check(end.Message.ExtraProperties is { } extra && extra.TryGet("errorMessage", out var message) && message!.Value.GetString() == "The upstream said no.",
                "the message is its errorMessage");
            Equal(2, events.Count, "nothing streams in between");
        }),
    ];
}
