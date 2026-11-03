using System.Text.Json.Nodes;

// Pi 1.5.0 through the Node bridge (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5, and
// extension-keybinding-hints.test.ts): message and entry renderers receive keyText()/keyHint() bound to the mode's effective
// keybindings (editor.configure), whichever pi-coding-agent copy the extension imports.
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        ("sync150.bridge-render-options-carry-the-modes-key-hints", BridgeKeyHints),
    ];

    private static async Task BridgeKeyHints()
    {
        using var sandbox = NodeSandbox("sync150-key-hints");
        var extension = sandbox.Write(Path.Combine(sandbox.Cwd, "hints.ts"), """
            import { Text } from "@earendil-works/pi-tui";
            const strip = (text: string) => text.replace(/\x1b\[[0-9;]*m/g, "");
            const hints = (options: any) => `${options.keyText("app.tools.expand")} | ${strip(options.keyHint("app.tools.expand", "to expand"))}`;
            export default function (pi: any) {
              pi.registerMessageRenderer("note", (_message: any, options: any) => new Text("message " + hints(options), 0, 0));
              pi.registerEntryRenderer("marker", (_entry: any, options: any) => new Text("entry " + hints(options), 0, 0));
            }
            """);
        await using var host = await StartHost(sandbox, extension);
        // A user override proves the hints read the mode's keybindings, not the defaults.
        await host.ConfigureEditorAsync(new Dictionary<string, IReadOnlyList<string>> { ["app.tools.expand"] = ["ctrl+e"], ["app.interrupt"] = ["escape"] },
            [], CancellationToken.None);
        var message = await host.RenderMessageAsync("note", new JsonObject { ["role"] = "custom", ["customType"] = "note", ["content"] = "x" }, 60, false, CancellationToken.None);
        Equal("message ctrl+e | ctrl+e to expand", message!.Value.Single().TrimEnd(), "message renderer options");
        var entry = await host.RenderEntryAsync("marker", new JsonObject { ["data"] = new JsonObject() }, 60, false, CancellationToken.None);
        Equal("entry ctrl+e | ctrl+e to expand", entry!.Value.Single().TrimEnd(), "entry renderer options");
    }
}
