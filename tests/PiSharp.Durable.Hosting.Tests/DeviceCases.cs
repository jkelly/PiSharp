using System.Collections.Immutable;
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
        }),
    ];
}
