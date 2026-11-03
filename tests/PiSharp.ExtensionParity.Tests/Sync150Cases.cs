using System.Text.Json.Nodes;

// Pi 1.5.0 through the Node bridge (authored from the v1.5.0 sources, f9a26365568127461c47a07a37dcb42c140a3ac5, and
// extension-keybinding-hints.test.ts): message and entry renderers receive keyText()/keyHint() bound to the mode's effective
// keybindings (editor.configure), whichever pi-coding-agent copy the extension imports; and the bridge's fallback SessionManager
// rolls back unwritten entries and forks through one fd (session-manager.ts).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> Sync150Cases() =>
    [
        ("sync150.bridge-render-options-carry-the-modes-key-hints", BridgeKeyHints),
        ("sync150.bridge-session-manager-rolls-back-unwritten-entries-and-forks-through-one-fd", BridgeSessionManagerWrites),
    ];

    // session-manager.ts _appendEntry and forkFrom, in the bridge's fallback copy (virtual/coding-agent/core/session-manager.mjs): an
    // entry whose write fails (ENOSPC) leaves memory and the leaf as they were, so the next entry chains to the last written one; a
    // fork opens its file once ("wx") and appends nothing per entry. The script patches node:fs and syncs the ESM bindings.
    private static async Task BridgeSessionManagerWrites()
    {
        using var sandbox = NodeSandbox("sync150-session-manager");
        var module = Path.Combine(AppContext.BaseDirectory, "node-bridge", "pi-host", "virtual", "coding-agent", "core", "session-manager.mjs");
        Check(File.Exists(module), "bridge session-manager.mjs next to the tests: " + module);
        var script = sandbox.Write("session-manager-writes.mjs", """
            import fs from "node:fs";
            import { syncBuiltinESMExports } from "node:module";
            import { pathToFileURL } from "node:url";
            const [modulePath, dir] = process.argv.slice(2);
            const { SessionManager } = await import(pathToFileURL(modulePath).href);
            const text = (value) => [{ type: "text", text: value }];
            const sm = SessionManager.create(dir, dir);
            const user = sm.appendMessage({ role: "user", content: text("hi"), timestamp: 1 });
            const assistant = sm.appendMessage({ role: "assistant", content: text("hello"), stopReason: "stop", timestamp: 2 });
            const append = fs.appendFileSync, open = fs.openSync;
            fs.appendFileSync = () => { const error = new Error("ENOSPC: no space left on device"); error.code = "ENOSPC"; throw error; };
            syncBuiltinESMExports();
            let failure = "none";
            try { sm.appendMessage({ role: "user", content: text("lost"), timestamp: 3 }); } catch (error) { failure = error.code; }
            fs.appendFileSync = append; syncBuiltinESMExports();
            const leafKept = sm.getLeafId() === assistant, entries = sm.getEntries().length;
            const next = sm.appendMessage({ role: "user", content: text("again"), timestamp: 4 });
            const name = (id) => id === user ? "user" : id === assistant ? "assistant" : id === next ? "next" : "other";
            const file = fs.readFileSync(sm.getSessionFile(), "utf8").trim().split("\n").map((line) => JSON.parse(line));
            const chain = file.filter((entry) => entry.type !== "session").map((entry) => name(entry.id) + "<" + (entry.parentId === null ? "null" : name(entry.parentId)));
            const opened = [], appended = [];
            fs.openSync = (...args) => { opened.push(String(args[1] ?? "r")); return open(...args); };
            fs.appendFileSync = (...args) => { appended.push(String(args[0])); return append(...args); };
            syncBuiltinESMExports();
            const forked = SessionManager.forkFrom(sm.getSessionFile(), dir, dir + "/forks");
            fs.openSync = open; fs.appendFileSync = append; syncBuiltinESMExports();
            const forkLines = fs.readFileSync(forked.getSessionFile(), "utf8").trim().split("\n").map((line) => JSON.parse(line));
            console.log(JSON.stringify({ failure, leafKept, entries, chain, forkOpens: opened.filter((flag) => flag === "wx").length, forkAppends: appended.length,
              forkHeader: forkLines[0].type === "session" && forkLines[0].parentSession === sm.getSessionFile(), forkEntries: forkLines.length - 1 }));
            """);
        var node = Environment.GetEnvironmentVariable("PISHARP_NODE") is { Length: > 0 } configured ? configured : "node";
        var start = new System.Diagnostics.ProcessStartInfo(node) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { script, module, Path.Combine(sandbox.Root, "sessions") }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Equal(0, process.ExitCode, "node exit code (" + (await stderr).Trim() + ")");
        Equal("""{"failure":"ENOSPC","leafKept":true,"entries":2,"chain":["user<null","assistant<user","next<assistant"],"forkOpens":1,"forkAppends":0,"forkHeader":true,"forkEntries":3}""",
            (await stdout).Trim(), "rollback and fork");
    }

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
