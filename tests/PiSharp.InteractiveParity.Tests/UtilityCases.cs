using System.Net;
using System.Text;
using PiSharp.Cli.Interactive.Mode.Utilities;
using PiSharp.CodingAgent.Diagnostics;
using PiSharp.Contracts;
using PiSharp.Sessions.Serialization;
using static Expect;

/// <summary>utils/clipboard*.ts, wsl.ts, changelog.ts, version-check.ts, open-browser.ts, git.ts, paths.ts, the interactive
/// handleClipboardPaste flow and modes/interactive/bug-report.ts. Expectations are authored from the sources and ported from
/// clipboard.test.ts, clipboard-command.test.ts, clipboard-image*.test.ts, clipboard-paste-file-paths.test.ts, changelog.test.ts,
/// version-check.test.ts, git-ssh-url.test.ts, paths.test.ts and bug-report.test.ts. Fake runners only: no real clipboard, no network.</summary>
internal static class UtilityCases
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    // ------------------------------------------------------------------ fakes

    private sealed class FakeNative : INativeClipboard
    {
        public Func<Task<string?>> GetText = () => Task.FromResult<string?>(null);
        public Func<Task<(bool, byte[]?)>> GetImage = () => Task.FromResult<(bool, byte[]?)>((true, Png));
        public Func<string, Task>? SetText = _ => Task.CompletedTask;
        public Func<Task<IReadOnlyList<string>?>>? GetFilePaths;
        public int TextCalls, ImageCalls;
        public List<string> Written = [];
        public Task<string?> GetTextAsync() { TextCalls++; return GetText(); }
        public Task<(bool Available, byte[]? Bytes)> GetImageAsync() { ImageCalls++; return GetImage(); }
        public bool CanSetText => SetText is not null;
        public Task SetTextAsync(string text) { Written.Add(text); return SetText!(text); }
        public bool CanGetFilePaths => GetFilePaths is not null;
        public Task<IReadOnlyList<string>?> GetFilePathsAsync() => GetFilePaths!();
    }

    private sealed class ClipFixture
    {
        public string Platform = "darwin";
        public Dictionary<string, string> Env = [];
        public FakeNative? Native = new();
        public int NativeCalls;
        public List<(string Command, IReadOnlyList<string> Args, ClipboardCommandOptions? Options)> Calls = [];
        public Func<string, IReadOnlyList<string>, ClipboardCommandOptions?, Task<byte[]?>> Handler = (_, _, _) => Task.FromResult<byte[]?>([]);
        public List<string> Osc = [];
        public string? ProcVersion { get; set; }
        public string TempDirectory = Path.GetTempPath();

        public IEnumerable<string> Names => Calls.Select(call => call.Command);

        public ClipboardEnvironment Build() => new()
        {
            Platform = Platform,
            Env = name => Env.TryGetValue(name, out var value) ? value : null,
            Run = (command, args, options) => { Calls.Add((command, args, options)); return Handler(command, args, options); },
            NativeClipboardFactory = () => { NativeCalls++; return Native; },
            WriteOutput = text => { if (text.StartsWith("\u001b]52;c;", StringComparison.Ordinal)) Osc.Add(text); },
            ReadProcVersion = () => ProcVersion,
            TempDirectory = TempDirectory,
        };
    }

    private static async Task<Exception> ThrowsAsync(Func<Task> run, string what)
    {
        try { await run(); } catch (Exception error) { return error; }
        throw new InvalidOperationException(what + ": expected an exception.");
    }

    private static void Seq<T>(IEnumerable<T> expected, IEnumerable<T> actual, string what) =>
        Check(expected.SequenceEqual(actual), $"{what}: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}].");

    private static void Bytes(byte[] expected, byte[]? actual, string what) =>
        Check(actual is not null && expected.AsSpan().SequenceEqual(actual), what + ": bytes differ.");

    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);

    private sealed class FakeHttp(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, Requests.Count);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static byte[] TinyBmp()
    {
        var buffer = new byte[58];
        buffer[0] = (byte)'B'; buffer[1] = (byte)'M';
        BitConverter.GetBytes(58u).CopyTo(buffer, 2);
        BitConverter.GetBytes(54u).CopyTo(buffer, 10);
        BitConverter.GetBytes(40u).CopyTo(buffer, 14);
        BitConverter.GetBytes(1).CopyTo(buffer, 18);
        BitConverter.GetBytes(1).CopyTo(buffer, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(buffer, 26);
        BitConverter.GetBytes((ushort)24).CopyTo(buffer, 28);
        BitConverter.GetBytes(4u).CopyTo(buffer, 34);
        buffer[56] = 0xff;
        return buffer;
    }

    public static IEnumerable<(string Id, Func<Task> Run)> All()
    {
        foreach (var item in ClipboardTextCases()) yield return item;
        foreach (var item in CopyCases()) yield return item;
        foreach (var item in CommandCases()) yield return item;
        foreach (var item in ImageCases()) yield return item;
        foreach (var item in NativeCommandCases()) yield return item;
        foreach (var item in PasteCases()) yield return item;
        foreach (var item in ChangelogCases()) yield return item;
        foreach (var item in VersionCases()) yield return item;
        foreach (var item in GitCases()) yield return item;
        foreach (var item in PathCases()) yield return item;
        foreach (var item in BrowserCases()) yield return item;
        foreach (var item in BugReportCases()) yield return item;
    }

    // ------------------------------------------------------------------ clipboard.test.ts: readClipboardText

    private static IEnumerable<(string, Func<Task>)> ClipboardTextCases()
    {
        yield return ("util.clipboard.read-native-text-and-rejection", async () =>
        {
            var f = new ClipFixture();
            f.Native!.GetText = () => Task.FromResult<string?>("clipboard text");
            Equal("clipboard text", await Clipboard.ReadClipboardText(f.Build()), "native text");
            f.Native.GetText = () => Task.FromException<string?>(new InvalidOperationException("clipboard unavailable"));
            Equal(null, await Clipboard.ReadClipboardText(f.Build()), "rejected read");
        });
        var commandCases = new (string Env, string Command, string[] Args, string[] Calls)[]
        {
            ("WAYLAND_DISPLAY", "wl-paste", ["--no-newline", "--type", "text"], ["wl-paste"]),
            ("DISPLAY", "xclip", ["-selection", "clipboard", "-out"], ["xclip"]),
            ("DISPLAY", "xsel", ["--clipboard", "--output"], ["xclip", "xsel"]),
        };
        foreach (var (envName, command, args, calls) in commandCases)
        {
            foreach (var text in new[] { "clipboard text", "" })
            {
                yield return ($"util.clipboard.{command}-result-{(text.Length == 0 ? "empty" : "text")}-stops-fallback", async () =>
                {
                    // Regression test for #7248: empty Wayland content must not fall through to stale X11.
                    var f = new ClipFixture { Platform = "linux" };
                    f.Env["DISPLAY"] = ":0";
                    f.Env[envName] = "1";
                    f.Handler = (name, _, _) => Task.FromResult(name == command ? B(text) : null);
                    Equal(text.Length == 0 ? null : text, await Clipboard.ReadClipboardText(f.Build()), "text");
                    Seq(calls, f.Names, "commands");
                    Seq(args, f.Calls[^1].Args, "args");
                    Equal(new ClipboardCommandOptions(TimeoutMs: 5000), f.Calls[^1].Options, "options");
                    Equal(0, f.NativeCalls, "native not called");
                });
            }
        }
        foreach (var text in new[] { "native text", "", null })
        {
            yield return ($"util.clipboard.native-x11-after-failures-{text?.Replace(' ', '-') ?? "null"}{(text == "" ? "empty" : "")}", async () =>
            {
                var f = new ClipFixture { Platform = "linux" };
                f.Env["DISPLAY"] = ":0";
                f.Env["WAYLAND_DISPLAY"] = "wayland-0";
                f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
                f.Native!.GetText = () => Task.FromResult(text);
                Equal(string.IsNullOrEmpty(text) ? null : text, await Clipboard.ReadClipboardText(f.Build()), "text");
                Equal(1, f.NativeCalls, "native called once");
                Seq(["wl-paste", "xclip", "xsel"], f.Names, "commands");
            });
        }
        foreach (var text in new[] { "clipboard text", "" })
        {
            yield return ($"util.clipboard.termux-get-{(text.Length == 0 ? "empty" : "text")}", async () =>
            {
                // Regression test for #10391: Termux reports platform "android".
                var f = new ClipFixture { Platform = "android" };
                f.Env["TERMUX_VERSION"] = "0.119";
                f.Handler = (_, _, _) => Task.FromResult<byte[]?>(B(text));
                Equal(text.Length == 0 ? null : text, await Clipboard.ReadClipboardText(f.Build()), "text");
                Equal(1, f.Calls.Count, "one command");
                Equal("termux-clipboard-get", f.Calls[0].Command, "command");
                Equal(0, f.Calls[0].Args.Count, "no args");
                Equal(new ClipboardCommandOptions(TimeoutMs: 5000), f.Calls[0].Options, "options");
                Equal(0, f.NativeCalls, "native not called");
            });
        }
        yield return ("util.clipboard.wl-paste-unavailable-falls-back-to-x11", async () =>
        {
            var f = new ClipFixture { Platform = "linux" };
            f.Env["WAYLAND_DISPLAY"] = "wayland-0";
            f.Env["DISPLAY"] = ":0";
            f.Handler = (name, _, _) => Task.FromResult(name == "wl-paste" ? null : B("X11 text"));
            Equal("X11 text", await Clipboard.ReadClipboardText(f.Build()), "text");
            Equal(0, f.NativeCalls, "native not called");
        });
        yield return ("util.clipboard.file-paths-native-only", async () =>
        {
            var f = new ClipFixture();
            Equal(null, await Clipboard.ReadClipboardFilePaths(f.Build()), "unsupported helper");
            f.Native!.GetFilePaths = () => Task.FromResult<IReadOnlyList<string>?>(["/tmp/a.png"]);
            Seq(["/tmp/a.png"], (await Clipboard.ReadClipboardFilePaths(f.Build()))!, "paths");
            f.Native.GetFilePaths = () => Task.FromResult<IReadOnlyList<string>?>([]);
            Equal(null, await Clipboard.ReadClipboardFilePaths(f.Build()), "empty list");
            f.Native = null;
            Equal(null, await Clipboard.ReadClipboardFilePaths(f.Build()), "no helper");
        });
        yield return ("util.wsl.detection", Sync(() =>
        {
            Check(Wsl.IsWSL(name => name == "WSL_DISTRO_NAME" ? "Ubuntu" : null, () => null), "WSL_DISTRO_NAME");
            Check(Wsl.IsWSL(name => name == "WSLENV" ? "x" : null, () => null), "WSLENV");
            Check(Wsl.IsWSL(_ => null, () => "Linux version 5.15.90.1-microsoft-standard-WSL2"), "/proc/version");
            Check(!Wsl.IsWSL(_ => null, () => "Linux version 6.1.0-generic"), "plain Linux");
            Check(!Wsl.IsWSL(_ => null, () => null), "unreadable /proc/version");
        }));
    }

    // ------------------------------------------------------------------ clipboard.test.ts: copyToClipboard

    private static IEnumerable<(string, Func<Task>)> CopyCases()
    {
        yield return ("util.copy.native-success-skips-osc-and-commands", async () =>
        {
            var f = new ClipFixture();
            await Clipboard.CopyToClipboard("hello", f.Build());
            Seq(["hello"], f.Native!.Written, "native write");
            Equal(0, f.Osc.Count, "no OSC 52");
            Equal(0, f.Calls.Count, "no commands");
        });
        yield return ("util.copy.linux-skips-native-writer", async () =>
        {
            var f = new ClipFixture { Platform = "linux" };
            f.Env["DISPLAY"] = ":0";
            await Clipboard.CopyToClipboard("hello", f.Build());
            Equal(0, f.NativeCalls, "native not requested");
            Equal("xclip", f.Calls[0].Command, "xclip");
            Seq(["-selection", "clipboard"], f.Calls[0].Args, "args");
            Equal(new ClipboardCommandOptions(Input: "hello", TimeoutMs: 5000), f.Calls[0].Options, "options");
        });
        yield return ("util.copy.waits-for-native-before-remote-osc52", async () =>
        {
            var f = new ClipFixture();
            f.Env["SSH_CONNECTION"] = "client server";
            var gate = new TaskCompletionSource();
            f.Native!.SetText = _ => gate.Task;
            var copy = Clipboard.CopyToClipboard("hello", f.Build());
            Equal(0, f.Osc.Count, "no OSC before native completes");
            gate.SetResult();
            await copy;
            Equal(1, f.Osc.Count, "one OSC after");
            Equal(0, f.Calls.Count, "no commands");
        });
        yield return ("util.copy.rejected-native-falls-back-to-pbcopy", async () =>
        {
            var f = new ClipFixture();
            f.Native!.SetText = _ => Task.FromException(new InvalidOperationException("native failed"));
            await Clipboard.CopyToClipboard("hello", f.Build());
            Equal("pbcopy", f.Calls.Single().Command, "pbcopy");
            Equal(new ClipboardCommandOptions(Input: "hello", TimeoutMs: 5000), f.Calls[0].Options, "options");
            Equal(0, f.Osc.Count, "no OSC");
        });
        yield return ("util.copy.read-only-native-uses-command", async () =>
        {
            var f = new ClipFixture();
            f.Native!.SetText = null;
            await Clipboard.CopyToClipboard("hello", f.Build());
            Equal(1, f.Calls.Count, "one command");
        });
        yield return ("util.copy.xclip-xsel-after-wl-copy", async () =>
        {
            var f = new ClipFixture { Platform = "linux" };
            f.Env["WAYLAND_DISPLAY"] = "wayland-0";
            f.Env["DISPLAY"] = ":0";
            f.Handler = (name, _, _) => Task.FromResult(name == "xsel" ? Array.Empty<byte>() : null);
            await Clipboard.CopyToClipboard("hello", f.Build());
            Seq(["wl-copy", "xclip", "xsel"], f.Names, "commands");
            Equal(0, f.Osc.Count, "no OSC");
        });
        yield return ("util.copy.local-linux-failure-reports-x11", async () =>
        {
            // Regression test for #9618.
            var f = new ClipFixture { Platform = "linux" };
            f.Env["DISPLAY"] = ":0";
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
            var error = await ThrowsAsync(() => Clipboard.CopyToClipboard("hello", f.Build()), "copy");
            Equal("Clipboard unavailable: install `xclip` or `xsel`, or check X11 access", error.Message, "message");
            Seq(["xclip", "xsel"], f.Names, "commands");
            Equal(0, f.Osc.Count, "no OSC");
        });
        yield return ("util.copy.display-less-linux-osc52", async () =>
        {
            // Regression test for #9688: containers without X11/Wayland access.
            var f = new ClipFixture { Platform = "linux" };
            await Clipboard.CopyToClipboard("hello", f.Build());
            Equal(0, f.Calls.Count, "no commands");
            Seq([$"\u001b]52;c;{Convert.ToBase64String(B("hello"))}\u0007"], f.Osc, "OSC 52 payload");
        });
        yield return ("util.copy.wsl-without-display-powershell", async () =>
        {
            // Regression test for #9688: WSL with WSLg disabled.
            using var dir = new TempDir();
            var f = new ClipFixture { Platform = "linux", TempDirectory = dir.Path };
            f.Env["WSL_DISTRO_NAME"] = "Ubuntu";
            string? written = null;
            f.Handler = (name, args, _) =>
            {
                if (name != "wslpath") return Task.FromResult<byte[]?>([]);
                written = File.ReadAllText(args[1], Encoding.UTF8);
                return Task.FromResult<byte[]?>(B("\\\\wsl.localhost\\Ubuntu\\tmp\\clip.txt\n"));
            };
            await Clipboard.CopyToClipboard("héllo", f.Build());
            Seq(["wslpath", "powershell.exe"], f.Names, "commands");
            Equal("héllo", written, "temp file text");
            Check(!File.Exists(f.Calls[0].Args[1]), "temp file removed");
            Contains(f.Calls[1].Args[2], "Set-Clipboard", "script");
            Contains(f.Calls[1].Args[2], "'\\\\wsl.localhost\\Ubuntu\\tmp\\clip.txt'", "script path");
            Equal(0, f.Osc.Count, "no OSC");
        });
        yield return ("util.copy.wsl-interop-unavailable-osc52", async () =>
        {
            var f = new ClipFixture { Platform = "linux" };
            f.Env["WSL_DISTRO_NAME"] = "Ubuntu";
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
            await Clipboard.CopyToClipboard("hello", f.Build());
            Seq(["wslpath"], f.Names, "commands");
            Equal(1, f.Osc.Count, "OSC");
        });
        yield return ("util.copy.wsl-windows-terminal-prefers-osc52", async () =>
        {
            var f = new ClipFixture { Platform = "linux" };
            f.Env["WSL_DISTRO_NAME"] = "Ubuntu";
            f.Env["WT_SESSION"] = "session";
            await Clipboard.CopyToClipboard("hello", f.Build());
            Equal(0, f.Calls.Count, "no commands");
            Equal(1, f.Osc.Count, "OSC");
        });
        yield return ("util.copy.wsl-windows-terminal-remote-osc52-once", async () =>
        {
            var f = new ClipFixture { Platform = "linux" };
            f.Env["WSL_DISTRO_NAME"] = "Ubuntu";
            f.Env["WT_SESSION"] = "session";
            f.Env["SSH_CONNECTION"] = "client server";
            await Clipboard.CopyToClipboard("hello", f.Build());
            Equal(0, f.Calls.Count, "no commands");
            Equal(1, f.Osc.Count, "OSC once");
        });
        yield return ("util.copy.wsl-windows-terminal-oversized-powershell", async () =>
        {
            var f = new ClipFixture { Platform = "linux" };
            f.Env["WSL_DISTRO_NAME"] = "Ubuntu";
            f.Env["WT_SESSION"] = "session";
            f.Handler = (name, _, _) => Task.FromResult<byte[]?>(name == "wslpath" ? B("C:\\clip.txt") : []);
            await Clipboard.CopyToClipboard(new string('x', 80_000), f.Build());
            Seq(["wslpath", "powershell.exe"], f.Names, "commands");
            Equal(0, f.Osc.Count, "no OSC");
        });
        yield return ("util.copy.wsl-with-display-prefers-linux-tools", async () =>
        {
            var f = new ClipFixture { Platform = "linux" };
            f.Env["WSL_DISTRO_NAME"] = "Ubuntu";
            f.Env["WAYLAND_DISPLAY"] = "wayland-0";
            await Clipboard.CopyToClipboard("hello", f.Build());
            Seq(["wl-copy"], f.Names, "commands");
            Equal(0, f.Osc.Count, "no OSC");
        });
        yield return ("util.copy.wayland-error-message", async () =>
        {
            var f = new ClipFixture { Platform = "linux" };
            f.Env["WAYLAND_DISPLAY"] = "wayland-0";
            f.Env["DISPLAY"] = ":0";
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
            var error = await ThrowsAsync(() => Clipboard.CopyToClipboard("hello", f.Build()), "copy");
            Equal("Clipboard unavailable: install `wl-clipboard` (`wl-copy`) or check Wayland access", error.Message, "message");
            Seq(["wl-copy", "xclip", "xsel"], f.Names, "commands");
        });
        yield return ("util.copy.termux-set", async () =>
        {
            var f = new ClipFixture { Platform = "android", Native = null };
            f.Env["TERMUX_VERSION"] = "0.119";
            await Clipboard.CopyToClipboard("hello", f.Build());
            Equal("termux-clipboard-set", f.Calls.Single().Command, "command");
            Equal(new ClipboardCommandOptions(Input: "hello", TimeoutMs: 5000), f.Calls[0].Options, "options");
            Equal(0, f.Osc.Count, "no OSC");
        });
        yield return ("util.copy.termux-api-requirement", async () =>
        {
            // Regression test for #10391: Termux reports platform "android".
            var f = new ClipFixture { Platform = "android", Native = null };
            f.Env["TERMUX_VERSION"] = "0.119";
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
            var error = await ThrowsAsync(() => Clipboard.CopyToClipboard("hello", f.Build()), "copy");
            Equal("Clipboard unavailable: install the Termux:API app and `termux-api` package", error.Message, "message");
        });
        yield return ("util.copy.remote-osc52-after-failures", async () =>
        {
            var f = new ClipFixture();
            f.Env["SSH_CONNECTION"] = "client server";
            f.Native!.SetText = _ => Task.FromException(new InvalidOperationException("native failed"));
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
            await Clipboard.CopyToClipboard("hello", f.Build());
            Equal(1, f.Osc.Count, "OSC");
        });
        yield return ("util.copy.oversized-osc52-refused", async () =>
        {
            var f = new ClipFixture();
            f.Env["SSH_CONNECTION"] = "client server";
            f.Native!.SetText = _ => Task.FromException(new InvalidOperationException("native failed"));
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
            var error = await ThrowsAsync(() => Clipboard.CopyToClipboard(new string('x', 80_000), f.Build()), "copy");
            Equal("Clipboard unavailable: text exceeds the OSC 52 size limit", error.Message, "message");
            Equal(0, f.Osc.Count, "no OSC");
        });
        yield return ("util.copy.windows-native-then-clip", async () =>
        {
            var f = new ClipFixture { Platform = "win32" };
            f.Native!.SetText = _ => Task.FromException(new InvalidOperationException("native failed"));
            await Clipboard.CopyToClipboard("hello", f.Build());
            Equal("clip", f.Calls.Single().Command, "clip fallback");
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
            var error = await ThrowsAsync(() => Clipboard.CopyToClipboard("hello", f.Build()), "copy");
            Equal("Clipboard unavailable", error.Message, "generic message");
        });
    }

    // ------------------------------------------------------------------ clipboard-command.test.ts (real processes, no clipboard)

    private static IEnumerable<(string, Func<Task>)> CommandCases()
    {
        static (string, string[]) Shell(string unix, string windows) =>
            OperatingSystem.IsWindows() ? ("cmd", ["/d", "/c", windows]) : ("sh", ["-c", unix]);

        yield return ("util.clipboard-command.empty-success-failure-missing", async () =>
        {
            var (ok, okArgs) = Shell("exit 0", "exit 0");
            var empty = await ClipboardCommand.RunClipboardCommand(ok, okArgs);
            Check(empty is { Length: 0 }, "empty success is an empty array");
            var (fail, failArgs) = Shell("exit 1", "exit 1");
            Equal(null, await ClipboardCommand.RunClipboardCommand(fail, failArgs), "non-zero exit");
            Equal(null, await ClipboardCommand.RunClipboardCommand("pi-clipboard-command-does-not-exist", []), "missing command");
        });
        yield return ("util.clipboard-command.binary-output", async () =>
        {
            UnixOnly();
            Bytes([0, 255, 10], await ClipboardCommand.RunClipboardCommand("sh", ["-c", "printf '\\000\\377\\n'"]), "binary output");
        });
        yield return ("util.clipboard-command.unicode-input", async () =>
        {
            UnixOnly();
            var result = await ClipboardCommand.RunClipboardCommand("sh", ["-c", "[ \"$(cat)\" = 'café 日本語' ]"], new(Input: "café 日本語"));
            Check(result is { Length: 0 }, "writer received UTF-8 input");
        });
        yield return ("util.clipboard-command.timeout", async () =>
        {
            var (command, args) = OperatingSystem.IsWindows() ? ("ping", new[] { "-n", "6", "127.0.0.1" }) : ("sleep", new[] { "5" });
            var started = DateTime.UtcNow;
            Equal(null, await ClipboardCommand.RunClipboardCommand(command, args, new(TimeoutMs: 200)), "timed out");
            Check(DateTime.UtcNow - started < TimeSpan.FromSeconds(4), "returned at the timeout");
        });
        yield return ("util.clipboard-command.buffer-limit", async () =>
        {
            var (command, args) = Shell("head -c 1024 /dev/zero", "echo 0123456789012345678901234567890123456789");
            Equal(null, await ClipboardCommand.RunClipboardCommand(command, args, new(MaxBufferBytes: 16)), "over the limit");
        });
    }

    // ------------------------------------------------------------------ clipboard-image*.test.ts

    private static IEnumerable<(string, Func<Task>)> ImageCases()
    {
        static ClipFixture ImageFixture(string platform)
        {
            var f = new ClipFixture { Platform = platform };
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
            f.Native!.GetImage = () => Task.FromResult<(bool, byte[]?)>((true, Png));
            return f;
        }

        foreach (var (backend, command, env) in new (string, string, (string, string)[])[]
                 { ("wayland", "wl-paste", [("WAYLAND_DISPLAY", "1"), ("DISPLAY", ":0")]), ("x11", "xclip", [("DISPLAY", ":0")]) })
        {
            foreach (var present in new[] { true, false })
            {
                yield return ($"util.clipboard-image.{backend}-present-{present.ToString().ToLowerInvariant()}-stops-fallback", async () =>
                {
                    var f = ImageFixture("linux");
                    foreach (var (name, value) in env) f.Env[name] = value;
                    f.Handler = (name, args, _) =>
                    {
                        Equal(command, name, "backend command");
                        var listing = args.Contains("--list-types") || args.Contains("TARGETS");
                        return Task.FromResult<byte[]?>(listing ? B(present ? "text/plain\nimage/png\n" : "text/plain\n") : Png);
                    };
                    var image = await ClipboardImageReader.ReadClipboardImage(f.Build());
                    if (present) { Bytes(Png, image?.Bytes, "image"); Equal("image/png", image!.MimeType, "mime"); }
                    else Equal(null, image, "no image");
                    Equal(present ? 2 : 1, f.Calls.Count, "command count");
                    Equal(0, f.NativeCalls, "native not called");
                });
            }
        }
        yield return ("util.clipboard-image.x11-targets-failure-no-probe", async () =>
        {
            // Regression test for #9786.
            var f = ImageFixture("linux");
            f.Env["DISPLAY"] = ":0";
            f.Native!.GetImage = () => Task.FromResult<(bool, byte[]?)>((true, null));
            f.Handler = (_, args, _) => Task.FromResult(args.Contains("TARGETS") ? null : B("hello"));
            Equal(null, await ClipboardImageReader.ReadClipboardImage(f.Build()), "no image");
            Equal(1, f.Calls.Count, "one command");
            Seq(["-selection", "clipboard", "-t", "TARGETS", "-o"], f.Calls[0].Args, "TARGETS args");
            Equal(new ClipboardCommandOptions(TimeoutMs: 1000), f.Calls[0].Options, "options");
            Equal(1, f.Native.ImageCalls, "native once");
        });
        yield return ("util.clipboard-image.x11-unadvertised-types-not-probed", async () =>
        {
            var f = ImageFixture("linux");
            f.Env["DISPLAY"] = ":0";
            f.Native!.GetImage = () => Task.FromResult<(bool, byte[]?)>((true, null));
            f.Handler = (_, args, _) => Task.FromResult(args.Contains("TARGETS") ? B("image/png\n") : args.Contains("image/png") ? null : B("hello"));
            Equal(null, await ClipboardImageReader.ReadClipboardImage(f.Build()), "no image");
            Seq(["TARGETS", "image/png"], f.Calls.Select(call => call.Args[3]), "probed types");
            Equal(1, f.Native.ImageCalls, "native once");
        });
        foreach (var (label, bytes) in new (string, byte[]?)[] { ("png", Png), ("null", null), ("empty", []) })
        {
            yield return ($"util.clipboard-image.native-x11-{label}-stops-fallback", async () =>
            {
                var f = ImageFixture("linux");
                f.Env["DISPLAY"] = ":0";
                f.Native!.GetImage = () => Task.FromResult<(bool, byte[]?)>((true, bytes));
                var image = await ClipboardImageReader.ReadClipboardImage(f.Build());
                if (bytes is { Length: > 0 }) { Bytes(bytes, image?.Bytes, "image"); Equal("image/png", image!.MimeType, "mime"); }
                else Equal(null, image, "no image");
                Equal(1, f.NativeCalls, "native requested once");
                Equal(1, f.Native.ImageCalls, "native image once");
                Seq(["xclip"], f.Names, "commands");
            });
        }
        foreach (var failure in new[] { "missing-module", "unavailable-display" })
        {
            yield return ($"util.clipboard-image.wayland-falls-back-to-x11-{failure}", async () =>
            {
                var f = ImageFixture("linux");
                f.Env["WAYLAND_DISPLAY"] = "1";
                if (failure == "missing-module") f.Native = null;
                else f.Native!.GetImage = () => Task.FromResult<(bool, byte[]?)>((false, null));
                f.Handler = (name, args, _) => Task.FromResult(name == "wl-paste" ? null : args.Contains("TARGETS") ? B("image/png\n") : Png);
                var image = await ClipboardImageReader.ReadClipboardImage(f.Build());
                Bytes(Png, image?.Bytes, "image");
                Equal("image/png", image!.MimeType, "mime");
                Equal(0, f.NativeCalls, "native not called");
            });
        }
        yield return ("util.clipboard-image.wsl-powershell-before-native", async () =>
        {
            using var dir = new TempDir();
            var f = ImageFixture("linux");
            f.TempDirectory = dir.Path;
            f.Env["WSL_DISTRO_NAME"] = "Ubuntu";
            f.Native!.GetImage = () => Task.FromException<(bool, byte[]?)>(new InvalidOperationException("Broken X11 bridge"));
            string? tmpFile = null;
            f.Handler = (command, args, _) =>
            {
                if (command is "wl-paste" or "xclip") return Task.FromResult<byte[]?>(null);
                if (command == "wslpath") { tmpFile = args[1]; return Task.FromResult<byte[]?>(B("C:\\Users\\O'Hare\\clip.png\n")); }
                if (command == "powershell.exe")
                {
                    Contains(args[2], "$path = 'C:\\Users\\O''Hare\\clip.png'", "quoted path");
                    File.WriteAllBytes(tmpFile ?? throw new InvalidOperationException("wslpath first"), Png);
                    return Task.FromResult<byte[]?>(B("ok\n"));
                }
                throw new InvalidOperationException("Unexpected command: " + command);
            };
            var image = await ClipboardImageReader.ReadClipboardImage(f.Build());
            Bytes(Png, image?.Bytes, "image");
            Equal("image/png", image!.MimeType, "mime");
            Equal(0, f.NativeCalls, "native not called");
            Check(!File.Exists(tmpFile), "temp file removed");
        });
        foreach (var platform in new[] { "darwin", "win32" })
        {
            foreach (var (label, available, bytes) in new (string, bool, byte[]?)[] { ("png", true, Png), ("null", true, null), ("empty", true, []), ("unavailable", false, null) })
            {
                yield return ($"util.clipboard-image.{platform}-native-{label}-once", async () =>
                {
                    var f = ImageFixture(platform);
                    f.Native!.GetImage = () => Task.FromResult<(bool, byte[]?)>((available, bytes));
                    var image = await ClipboardImageReader.ReadClipboardImage(f.Build());
                    if (bytes is { Length: > 0 }) { Bytes(bytes, image?.Bytes, "image"); Equal("image/png", image!.MimeType, "mime"); }
                    else Equal(null, image, "no image");
                    Equal(1, f.Native.ImageCalls, "native once");
                    Equal(0, f.Calls.Count, "no commands");
                });
            }
        }
        yield return ("util.clipboard-image.no-native-helper", async () =>
        {
            var f = ImageFixture("win32");
            var native = f.Native!;
            f.Native = null;
            Equal(null, await ClipboardImageReader.ReadClipboardImage(f.Build()), "no image");
            Equal(0, native.ImageCalls, "no read");
        });
        foreach (var platform in new[] { "linux", "win32" })
        {
            yield return ($"util.clipboard-image.{platform}-native-errors-propagate", async () =>
            {
                var f = ImageFixture(platform);
                f.Env["WAYLAND_DISPLAY"] = "1";
                f.Env["DISPLAY"] = ":0";
                var failure = new InvalidOperationException("Native clipboard operation failed");
                f.Native!.GetImage = () => Task.FromException<(bool, byte[]?)>(failure);
                var error = await ThrowsAsync(() => ClipboardImageReader.ReadClipboardImage(f.Build()), "read");
                Check(ReferenceEquals(failure, error), "same error");
                Seq(platform == "linux" ? ["wl-paste", "xclip"] : [], f.Names, "commands");
            });
            yield return ($"util.clipboard-image.{platform}-bmp-converted-to-png", async () =>
            {
                var f = ImageFixture(platform);
                f.Env["WAYLAND_DISPLAY"] = "wayland-0";
                f.Native!.GetImage = () => Task.FromResult<(bool, byte[]?)>((true, TinyBmp()));
                f.Handler = (name, args, _) => Task.FromResult(name != "wl-paste" ? null
                    : args.Contains("--list-types") ? B("image/bmp\n") : args.Contains("image/bmp") ? TinyBmp() : null);
                var image = await ClipboardImageReader.ReadClipboardImage(f.Build());
                Check(image is not null, "image converted");
                Equal("image/png", image!.MimeType, "mime");
                Bytes([0x89, 0x50, 0x4e, 0x47], image.Bytes[..4], "PNG signature");
            });
        }
        yield return ("util.clipboard-image.termux-no-image", async () =>
        {
            var f = ImageFixture("linux");
            f.Env["TERMUX_VERSION"] = "0.119";
            Equal(null, await ClipboardImageReader.ReadClipboardImage(f.Build()), "no image");
            Equal(0, f.NativeCalls, "native not called");
            Equal(0, f.Calls.Count, "no commands");
        });
        yield return ("util.clipboard-image.extension-and-wayland", Sync(() =>
        {
            Equal("png", ClipboardImageReader.ExtensionForImageMimeType("image/png"), "png");
            Equal("jpg", ClipboardImageReader.ExtensionForImageMimeType("IMAGE/JPEG; charset=x"), "jpeg with parameters");
            Equal("webp", ClipboardImageReader.ExtensionForImageMimeType("image/webp"), "webp");
            Equal("gif", ClipboardImageReader.ExtensionForImageMimeType("image/gif"), "gif");
            Equal(null, ClipboardImageReader.ExtensionForImageMimeType("image/bmp"), "bmp");
            Check(ClipboardImageReader.IsWaylandSession(name => name == "WAYLAND_DISPLAY" ? "wayland-0" : null), "WAYLAND_DISPLAY");
            Check(ClipboardImageReader.IsWaylandSession(name => name == "XDG_SESSION_TYPE" ? "wayland" : null), "XDG_SESSION_TYPE");
            Check(!ClipboardImageReader.IsWaylandSession(name => name == "XDG_SESSION_TYPE" ? "x11" : null), "x11 session");
        }));
        yield return ("util.clipboard-image.prefers-png-then-any-image", async () =>
        {
            var f = ImageFixture("linux");
            f.Env["DISPLAY"] = ":0";
            f.Handler = (_, args, _) => Task.FromResult<byte[]?>(args.Contains("TARGETS") ? B("TARGETS\nimage/x-foo\nimage/jpeg\nimage/png\n") : B("data"));
            var image = await ClipboardImageReader.ReadClipboardImage(f.Build());
            Equal("image/png", f.Calls[1].Args[3], "png preferred over jpeg");
            Equal("image/png", image!.MimeType, "mime");
        });
    }

    // ------------------------------------------------------------------ command-backed native helpers (deviation)

    private static IEnumerable<(string, Func<Task>)> NativeCommandCases()
    {
        yield return ("util.native-clipboard.helpers-per-platform", Sync(() =>
        {
            Check(NativeClipboard.Create(new ClipboardEnvironment { Platform = "darwin" }) is not null, "darwin helper");
            Check(NativeClipboard.Create(new ClipboardEnvironment { Platform = "win32" }) is not null, "win32 helper");
            Equal(null, NativeClipboard.Create(new ClipboardEnvironment { Platform = "linux" }), "no linux helper");
            Equal(null, NativeClipboard.Create(new ClipboardEnvironment { Platform = "android" }), "no android helper");
            Check(NativeClipboard.Create(new ClipboardEnvironment { Platform = "win32" })!.CanSetText, "win32 writes");
            Check(!NativeClipboard.Create(new ClipboardEnvironment { Platform = "darwin" })!.CanSetText, "darwin leaves writes to pbcopy");
            Check(NativeClipboard.Create(new ClipboardEnvironment { Platform = "darwin" })!.CanGetFilePaths, "darwin file paths");
            Check(!NativeClipboard.Create(new ClipboardEnvironment { Platform = "win32" })!.CanGetFilePaths, "win32 has no file paths");
        }));
        yield return ("util.native-clipboard.darwin-text-and-files", async () =>
        {
            var f = new ClipFixture { Platform = "darwin" };
            f.Handler = (name, args, _) => Task.FromResult<byte[]?>(name == "pbpaste" ? B("héllo") : B("/Users/me/a.png\n/Users/me/My Photos/b.png\n"));
            var native = NativeClipboard.Create(f.Build())!;
            Equal("héllo", await native.GetTextAsync(), "text");
            Seq(["/Users/me/a.png", "/Users/me/My Photos/b.png"], (await native.GetFilePathsAsync())!, "paths");
            Seq(["pbpaste", "osascript"], f.Names, "commands");
            Seq(["-l", "JavaScript", "-e"], f.Calls[1].Args.Take(3), "JXA");
        });
        yield return ("util.native-clipboard.darwin-image", async () =>
        {
            using var dir = new TempDir();
            var f = new ClipFixture { Platform = "darwin", TempDirectory = dir.Path };
            var output = "ok";
            f.Handler = (_, args, _) => { if (output == "ok") File.WriteAllBytes(args[^1], Png); return Task.FromResult<byte[]?>(B(output + "\n")); };
            var native = NativeClipboard.Create(f.Build())!;
            var (available, bytes) = await native.GetImageAsync();
            Check(available, "available");
            Bytes(Png, bytes, "png");
            Contains(string.Join(" ", f.Calls[0].Args), "«class PNGf»", "AppleScript PNG coercion");
            Check(!File.Exists(f.Calls[0].Args[^1]), "temp file removed");
            output = "empty";
            Check(await native.GetImageAsync() is (true, null), "no image");
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
            Check(await native.GetImageAsync() is (false, null), "osascript failure is unavailable");
        });
        yield return ("util.native-clipboard.win32-text-and-write", async () =>
        {
            using var dir = new TempDir();
            var f = new ClipFixture { Platform = "win32", TempDirectory = dir.Path };
            string? written = null;
            f.Handler = (_, args, _) =>
            {
                var script = args[^1];
                if (script.Contains("Set-Clipboard", StringComparison.Ordinal))
                {
                    var start = script.IndexOf("ReadAllText('", StringComparison.Ordinal) + "ReadAllText('".Length;
                    var path = script[start..script.IndexOf("', [System.Text.Encoding]", StringComparison.Ordinal)].Replace("''", "'", StringComparison.Ordinal);
                    written = File.ReadAllText(path, Encoding.UTF8);
                    return Task.FromResult<byte[]?>([]);
                }
                return Task.FromResult<byte[]?>(B("日本語 text"));
            };
            var native = NativeClipboard.Create(f.Build())!;
            Equal("日本語 text", await native.GetTextAsync(), "text");
            Contains(f.Calls[0].Args[^1], "Get-Clipboard -Raw", "Get-Clipboard");
            Contains(f.Calls[0].Args[^1], "UTF8Encoding", "UTF-8 output");
            await native.SetTextAsync("café");
            Equal("café", written, "UTF-8 file");
            Equal(0, Directory.GetFiles(dir.Path).Length, "temp file removed");
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(null);
            await ThrowsAsync(() => native.SetTextAsync("x"), "failed write throws");
        });
        yield return ("util.native-clipboard.win32-image", async () =>
        {
            using var dir = new TempDir();
            var f = new ClipFixture { Platform = "win32", TempDirectory = dir.Path };
            f.Handler = (_, args, _) => Task.FromResult<byte[]?>(B("empty\r\n"));
            var native = NativeClipboard.Create(f.Build())!;
            Check(await native.GetImageAsync() is (true, null), "empty clipboard");
            var script = f.Calls[0].Args[^1];
            Contains(script, "GetDataPresent('PNG')", "PNG format first");
            Contains(script, "[System.Windows.Forms.Clipboard]::GetImage()", "bitmap fallback");
            f.Handler = (_, _, _) => Task.FromResult<byte[]?>(B("broken"));
            await ThrowsAsync(() => native.GetImageAsync(), "unexpected output is a transfer failure");
        });
    }

    // ------------------------------------------------------------------ clipboard-paste-file-paths.test.ts, clipboard-image-native-errors.test.ts

    private sealed class PasteHost : IClipboardPasteHost
    {
        public bool IsBashMode { get; set; }
        public (int Line, int Col)? Cursor;
        public string Text = "";
        public List<string> Inserted = [];
        public List<string> Errors = [];
        public int Renders;
        public (int Line, int Col)? GetCursor() => Cursor;
        public string GetText() => Text;
        public void InsertTextAtCursor(string text) => Inserted.Add(text);
        public void RequestRender() => Renders++;
        public void ShowError(string message) => Errors.Add(message);
    }

    private static IEnumerable<(string, Func<Task>)> PasteCases()
    {
        ClipboardPasteSources Sources(Func<Task<IReadOnlyList<string>?>> paths, Func<Task<ClipboardImage?>>? image = null, Func<Task<string?>>? text = null,
            List<string>? log = null) => new()
        {
            ReadClipboardFilePaths = () => { log?.Add("paths"); return paths(); },
            ReadClipboardImage = () => { log?.Add("image"); return (image ?? (() => Task.FromResult<ClipboardImage?>(null)))(); },
            ReadClipboardText = () => { log?.Add("text"); return (text ?? (() => Task.FromResult<string?>(null)))(); },
        };

        yield return ("util.paste.file-paths-before-icon-image", async () =>
        {
            // Regression test for #9999.
            var log = new List<string>();
            var host = new PasteHost();
            await ClipboardPaste.HandleClipboardPaste(host, Sources(() => Task.FromResult<IReadOnlyList<string>?>(["/tmp/screenshot.png", "/tmp/My Photos/photo.png"]),
                () => Task.FromResult<ClipboardImage?>(new([0x89, 0x50, 0x4e, 0x47], "image/png")), log: log));
            Seq(["/tmp/screenshot.png\n/tmp/My Photos/photo.png"], host.Inserted, "inserted");
            Seq(["paths"], log, "image not read");
        });
        yield return ("util.paste.control-characters-rejected", async () =>
        {
            var log = new List<string>();
            var host = new PasteHost();
            await ClipboardPaste.HandleClipboardPaste(host, Sources(() => Task.FromResult<IReadOnlyList<string>?>(["/tmp/photo\u001b]0;unsafe\u0007.png"]), log: log));
            Equal(0, host.Inserted.Count, "nothing inserted");
            Seq(["paths"], log, "image not read");
            Seq(["Failed to paste from clipboard: Clipboard file path contains control characters"], host.Errors, "error");
        });
        yield return ("util.paste.native-file-path-error", async () =>
        {
            var log = new List<string>();
            var host = new PasteHost();
            await ClipboardPaste.HandleClipboardPaste(host, Sources(() => Task.FromException<IReadOnlyList<string>?>(new InvalidOperationException("Native clipboard file read failed")), log: log));
            Equal(0, host.Inserted.Count, "nothing inserted");
            Seq(["paths"], log, "image not read");
            Seq(["Failed to paste from clipboard: Native clipboard file read failed"], host.Errors, "error");
        });
        foreach (var (label, editorText, col) in new[] { ("punctuation", "Review:", 7), ("unicode", "確認", 2) })
        {
            yield return ($"util.paste.separated-from-preceding-{label}", async () =>
            {
                var host = new PasteHost { Cursor = (0, col), Text = editorText };
                await ClipboardPaste.HandleClipboardPaste(host, Sources(() => Task.FromResult<IReadOnlyList<string>?>(["/tmp/photo.png"])));
                Seq([" /tmp/photo.png"], host.Inserted, "inserted");
            });
        }
        yield return ("util.paste.bash-mode-shell-quotes", async () =>
        {
            var host = new PasteHost { Cursor = (0, 3), Text = "catDEST", IsBashMode = true };
            await ClipboardPaste.HandleClipboardPaste(host, Sources(() => Task.FromResult<IReadOnlyList<string>?>(
                ["/tmp/My Photos/photo.png", "/tmp/$(touch hacked).png", "/tmp/plain.png"])));
            Seq([" '/tmp/My Photos/photo.png' '/tmp/$(touch hacked).png' /tmp/plain.png "], host.Inserted, "inserted");
            Equal("'it'\\''s'", ClipboardPaste.QuoteIfNeeded("it's"), "quote escaping");
            Equal("''", ClipboardPaste.QuoteIfNeeded(""), "empty value");
        });
        yield return ("util.paste.native-image-error-aborts", async () =>
        {
            var log = new List<string>();
            var host = new PasteHost();
            await ClipboardPaste.HandleClipboardPaste(host, Sources(() => Task.FromResult<IReadOnlyList<string>?>(null),
                () => Task.FromException<ClipboardImage?>(new InvalidOperationException("Native clipboard operation failed")), log: log));
            Seq(["paths", "image"], log, "text not read");
            Equal(0, host.Inserted.Count, "editor unchanged");
            Equal(0, host.Renders, "no render");
            Seq(["Failed to paste from clipboard: Native clipboard operation failed"], host.Errors, "error");
        });
        yield return ("util.paste.image-written-to-temp-file", async () =>
        {
            using var dir = new TempDir();
            var host = new PasteHost();
            var sources = new ClipboardPasteSources
            {
                ReadClipboardFilePaths = () => Task.FromResult<IReadOnlyList<string>?>(null),
                ReadClipboardImage = () => Task.FromResult<ClipboardImage?>(new(Png, "image/png")),
                ReadClipboardText = () => throw new InvalidOperationException("text must not be read"),
                TempDirectory = dir.Path,
            };
            await ClipboardPaste.HandleClipboardPaste(host, sources);
            var path = host.Inserted.Single();
            Check(Path.GetFileName(path).StartsWith("pi-clipboard-", StringComparison.Ordinal) && path.EndsWith(".png", StringComparison.Ordinal), "file name");
            Bytes(Png, File.ReadAllBytes(path), "written bytes");
            Equal(1, host.Renders, "render");
        });
        yield return ("util.paste.text-fallback", async () =>
        {
            var host = new PasteHost();
            await ClipboardPaste.HandleClipboardPaste(host, Sources(() => Task.FromResult<IReadOnlyList<string>?>(null), text: () => Task.FromResult<string?>("plain")));
            Seq(["plain"], host.Inserted, "text inserted");
            var empty = new PasteHost();
            await ClipboardPaste.HandleClipboardPaste(empty, Sources(() => Task.FromResult<IReadOnlyList<string>?>(null)));
            Equal(0, empty.Inserted.Count + empty.Renders, "nothing on an empty clipboard");
        });
    }

    // ------------------------------------------------------------------ changelog.test.ts

    private static IEnumerable<(string, Func<Task>)> ChangelogCases()
    {
        var entry = new ChangelogEntry(0, 79, 0, "");
        yield return ("util.changelog.package-relative-links", Sync(() =>
        {
            var markdown = string.Join("\n",
                "[Project Trust](README.md#project-trust)",
                "[Extensions](docs/extensions.md#project_trust)",
                "[Examples](examples/extensions/)",
                "[Root README](../../README.md#supply-chain-hardening)");
            Equal(string.Join("\n",
                "[Project Trust](https://github.com/earendil-works/pi/blob/v0.79.0/packages/coding-agent/README.md#project-trust)",
                "[Extensions](https://github.com/earendil-works/pi/blob/v0.79.0/packages/coding-agent/docs/extensions.md#project_trust)",
                "[Examples](https://github.com/earendil-works/pi/tree/v0.79.0/packages/coding-agent/examples/extensions/)",
                "[Root README](https://github.com/earendil-works/pi/blob/v0.79.0/README.md#supply-chain-hardening)"),
                Changelog.NormalizeChangelogLinks(markdown, entry), "normalized");
        }));
        yield return ("util.changelog.legacy-urls-and-external", Sync(() =>
        {
            var markdown = string.Join("\n",
                "[#5167](https://github.com/earendil-works/pi-mono/pull/5167)",
                "[#4163](https://github.com/badlogic/pi-mono/issues/4163)",
                "[Agent README](https://github.com/badlogic/pi-mono/blob/main/packages/agent/README.md)",
                "[External](https://example.com/docs)",
                "[Local anchor](#settings)");
            Equal(string.Join("\n",
                "[#5167](https://github.com/earendil-works/pi/pull/5167)",
                "[#4163](https://github.com/earendil-works/pi/issues/4163)",
                "[Agent README](https://github.com/earendil-works/pi/blob/v0.79.0/packages/agent/README.md)",
                "[External](https://example.com/docs)",
                "[Local anchor](#settings)"),
                Changelog.NormalizeChangelogLinks(markdown, "0.79.0"), "normalized");
        }));
        yield return ("util.changelog.encoded-paths-titles-and-queries", Sync(() =>
        {
            Equal("![shot](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/my%2520file.png \"Title\")", Changelog.NormalizeChangelogLinks("![shot](docs/my%20file.png \"Title\")", "v1.1.0"), "image link with title (encodeURI re-encodes %)");
            Equal("[q](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/a.md?x=1#y)",
                Changelog.NormalizeChangelogLinks("[q](docs/a.md?x=1#y)", "1.1.0"), "query and fragment kept");
            Equal("[up](../../../outside.md)", Changelog.NormalizeChangelogLinks("[up](../../../outside.md)", "1.1.0"), "escaping the repository");
            Equal("[mail](mailto:a@b.c)", Changelog.NormalizeChangelogLinks("[mail](mailto:a@b.c)", "1.1.0"), "scheme kept");
            Equal("[é](https://github.com/earendil-works/pi/blob/v1.1.0/packages/coding-agent/docs/%C3%A9.md)",
                Changelog.NormalizeChangelogLinks("[é](docs/é.md)", "1.1.0"), "encodeURI");
        }));
        yield return ("util.changelog.parse-and-new-entries", Sync(() =>
        {
            using var dir = new TempDir();
            var path = Path.Combine(dir.Path, "CHANGELOG.md");
            File.WriteAllText(path, "# Changelog\n\n## [Unreleased]\n\n- pending\n\n## [1.1.0] - 2026-05-01\n\n### Added\n- one\n\n## 1.0.4\n- two\n\n## [0.99.1.1]\n- three\n");
            var entries = Changelog.ParseChangelog(path);
            Seq(["1.1.0", "1.0.4", "0.99.1"], entries.Select(e => $"{e.Major}.{e.Minor}.{e.Patch}"), "versions");
            Equal("## [1.1.0] - 2026-05-01\n\n### Added\n- one", entries[0].Content, "first content");
            Equal("## [0.99.1.1]\n- three", entries[2].Content, "last content trimmed");
            Seq(["1.1.0"], Changelog.GetNewEntries(entries, "1.0.4").Select(e => $"{e.Major}.{e.Minor}.{e.Patch}"), "newer than 1.0.4");
            Equal(3, Changelog.GetNewEntries(entries, "0.1").Count, "missing patch counts as 0");
            Equal(3, Changelog.GetNewEntries(entries, "garbage").Count, "non-numeric counts as 0");
            Equal(0, Changelog.ParseChangelog(Path.Combine(dir.Path, "missing.md")).Count, "missing file");
        }));
        yield return ("util.changelog.path", Sync(() =>
        {
            Equal(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md")), Changelog.GetChangelogPath(_ => null), "next to the executable");
            using var dir = new TempDir();
            Equal(Path.Combine(dir.Path, "CHANGELOG.md"), Changelog.GetChangelogPath(name => name == "PI_PACKAGE_DIR" ? dir.Path : null), "PI_PACKAGE_DIR");
        }));
    }

    // ------------------------------------------------------------------ version-check.test.ts

    private static IEnumerable<(string, Func<Task>)> VersionCases()
    {
        static VersionCheckOptions Options(FakeHttp http, Dictionary<string, string>? env = null, bool retry = false) => new()
        {
            Http = new HttpMessageInvoker(http),
            Env = name => env is not null && env.TryGetValue(name, out var value) ? value : null,
            Retry = retry,
        };

        yield return ("util.version.compare", Sync(() =>
        {
            Check(VersionCheck.ComparePackageVersions("0.70.6", "0.70.5") > 0, "newer patch");
            Equal(0, VersionCheck.ComparePackageVersions("0.70.5", "0.70.5"), "equal");
            Check(VersionCheck.ComparePackageVersions("0.70.4", "0.70.5") < 0, "older patch");
            Check(VersionCheck.ComparePackageVersions("5.0.0-beta.20", "5.0.0-beta.9") > 0, "numeric prerelease");
            Check(VersionCheck.ComparePackageVersions("1.0.0", "1.0.0-rc.1") > 0, "release after prerelease");
            Check(VersionCheck.ComparePackageVersions("1.0.0-alpha", "1.0.0-1") > 0, "alphanumeric after numeric");
            Equal(0, VersionCheck.ComparePackageVersions(" v1.2.3 ", "1.2.3+build"), "v prefix, trim, build ignored");
            Equal(null, VersionCheck.ComparePackageVersions("1.2", "1.2.3"), "invalid");
            Equal(null, VersionCheck.ComparePackageVersions("01.2.3", "1.2.3"), "leading zero");
            Check(!VersionCheck.IsNewerPackageVersion("0.70.5", "0.70.5"), "same is not newer");
            Check(VersionCheck.IsNewerPackageVersion("0.70.6", "0.70.5"), "newer");
            Check(VersionCheck.IsNewerPackageVersion("nightly", "0.70.5"), "invalid differing versions count as newer");
            Check(!VersionCheck.IsNewerPackageVersion(" nightly ", "nightly"), "invalid equal after trim");
        }));
        yield return ("util.version.only-newer", async () =>
        {
            var http = new FakeHttp((_, _) => Task.FromResult(Json("{\"version\":\"1.2.3\"}")));
            Equal(null, await VersionCheck.CheckForNewPiVersion("1.2.3", Options(http)), "same version");
            Equal(new LatestPiRelease("1.2.3"), await VersionCheck.CheckForNewPiVersion("1.2.2", Options(http)), "newer");
        });
        yield return ("util.version.endpoint-and-user-agent", async () =>
        {
            var http = new FakeHttp((_, _) => Task.FromResult(Json("{\"version\":\"1.2.4\"}")));
            Equal("1.2.4", await VersionCheck.GetLatestPiVersion("1.2.3", Options(http)), "version");
            var request = http.Requests.Single();
            Equal("https://pi.dev/api/latest-version", request.RequestUri!.ToString(), "url");
            Equal(HttpMethod.Get, request.Method, "method");
            Check(string.Join(" ", request.Headers.GetValues("User-Agent")).StartsWith("pi/1.2.3 ", StringComparison.Ordinal), "user agent");
            Equal("application/json", string.Join(",", request.Headers.GetValues("accept")), "accept");
        });
        yield return ("util.version.retries-when-requested", async () =>
        {
            var http = new FakeHttp((_, attempt) => attempt < 3 ? Task.FromException<HttpResponseMessage>(new HttpRequestException("fetch failed"))
                : Task.FromResult(Json("{\"version\":\"1.2.4\"}")));
            Equal(new LatestPiRelease("1.2.4"), await VersionCheck.GetLatestPiRelease("1.2.3", Options(http, retry: true)), "release");
            Equal(3, http.Requests.Count, "three attempts");
        });
        yield return ("util.version.retries-retryable-status", async () =>
        {
            var http = new FakeHttp((_, attempt) => Task.FromResult(attempt == 1 ? Json("{}", HttpStatusCode.ServiceUnavailable) : Json("{\"version\":\"2.0.0\"}")));
            Equal("2.0.0", await VersionCheck.GetLatestPiVersion("1.2.3", Options(http, retry: true)), "after a 503");
            var once = new FakeHttp((_, _) => Task.FromResult(Json("{}", HttpStatusCode.ServiceUnavailable)));
            Equal(null, await VersionCheck.GetLatestPiVersion("1.2.3", Options(once)), "non-ok response");
            Equal(1, once.Requests.Count, "no retry by default");
        });
        yield return ("util.version.automatic-check-one-request", async () =>
        {
            var http = new FakeHttp((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("fetch failed")));
            Equal(null, await VersionCheck.CheckForNewPiVersion("1.2.3", Options(http)), "swallowed");
            Equal(1, http.Requests.Count, "one request");
            await ThrowsAsync(() => VersionCheck.GetLatestPiRelease("1.2.3", Options(http)), "direct call throws");
        });
        yield return ("util.version.format-error", Sync(() =>
        {
            Exception Coded(string message, string code) { var error = new Exception(message); error.Data["code"] = code; return error; }
            var error = new HttpRequestException("fetch failed", new AggregateException(Coded("connect timeout", "ETIMEDOUT"), Coded("network unreachable", "ENETUNREACH")));
            Equal("fetch failed (ETIMEDOUT, ENETUNREACH)", VersionCheck.FormatVersionCheckError(error), "codes");
            Equal("fetch failed (ECONNREFUSED)", VersionCheck.FormatVersionCheckError(new HttpRequestException("fetch failed",
                new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))), "socket errno");
            Equal("fetch failed (cause: boom)", VersionCheck.FormatVersionCheckError(new HttpRequestException("fetch failed", new Exception("boom"))), "cause message");
            Equal("plain", VersionCheck.FormatVersionCheckError(new Exception("plain")), "no cause");
            Equal("text", VersionCheck.FormatVersionCheckError("text"), "non-error value");
        }));
        yield return ("util.version.package-metadata", async () =>
        {
            var http = new FakeHttp((_, _) => Task.FromResult(Json("{\"packageName\":\"@new-scope/pi\",\"version\":\"1.2.4\"}")));
            Equal(new LatestPiRelease("1.2.4", "@new-scope/pi"), await VersionCheck.GetLatestPiRelease("1.2.3", Options(http)), "metadata");
        });
        yield return ("util.version.update-note", async () =>
        {
            var http = new FakeHttp((_, _) => Task.FromResult(Json("{\"note\":\" **Read this** \",\"version\":\"1.2.4\"}")));
            Equal(new LatestPiRelease("1.2.4", null, "**Read this**"), await VersionCheck.GetLatestPiRelease("1.2.3", Options(http)), "note");
            var blank = new FakeHttp((_, _) => Task.FromResult(Json("{\"version\":\"  \",\"note\":\"x\"}")));
            Equal(null, await VersionCheck.GetLatestPiRelease("1.2.3", Options(blank)), "blank version");
        });
        yield return ("util.version.skip-automatic-check", async () =>
        {
            var http = new FakeHttp((_, _) => Task.FromResult(Json("{\"version\":\"1.2.4\"}")));
            var env = new Dictionary<string, string> { ["PI_SKIP_VERSION_CHECK"] = "1" };
            Equal(null, await VersionCheck.CheckForNewPiVersion("1.2.3", Options(http, env)), "skipped");
            Equal(0, http.Requests.Count, "no request");
            Equal("1.2.4", await VersionCheck.GetLatestPiVersion("1.2.3", Options(http, env)), "direct call allowed");
            Equal(1, http.Requests.Count, "one request");
        });
        yield return ("util.version.offline", async () =>
        {
            var http = new FakeHttp((_, _) => Task.FromResult(Json("{\"version\":\"1.2.4\"}")));
            Equal(null, await VersionCheck.GetLatestPiRelease("1.2.3", Options(http, new() { ["PI_OFFLINE"] = "1" })), "offline");
            Equal(0, http.Requests.Count, "no request");
        });
    }

    // ------------------------------------------------------------------ git-ssh-url.test.ts

    private static IEnumerable<(string, Func<Task>)> GitCases()
    {
        static void Git(string source, string host, string path, string repo, string? gitRef = null)
        {
            var result = PiSharp.Cli.Interactive.Mode.Utilities.Git.ParseGitUrl(source) ?? throw new InvalidOperationException("Not parsed: " + source);
            Equal(host, result.Host, source + " host");
            Equal(path, result.Path, source + " path");
            Equal(repo, result.Repo, source + " repo");
            Equal(gitRef, result.Ref, source + " ref");
            Equal(gitRef is not null, result.Pinned, source + " pinned");
            Equal("git", result.Type, source + " type");
        }

        yield return ("util.git.https-url", Sync(() => Git("https://github.com/user/repo", "github.com", "user/repo", "https://github.com/user/repo")));
        yield return ("util.git.ssh-protocol-url", Sync(() => Git("ssh://git@github.com/user/repo", "github.com", "user/repo", "ssh://git@github.com/user/repo")));
        yield return ("util.git.protocol-url-with-ref", Sync(() => Git("https://github.com/user/repo@v1.0.0", "github.com", "user/repo", "https://github.com/user/repo", "v1.0.0")));
        yield return ("util.git.scp-with-prefix", Sync(() => Git("git:git@github.com:user/repo", "github.com", "user/repo", "git@github.com:user/repo")));
        yield return ("util.git.host-path-shorthand-with-prefix", Sync(() => Git("git:github.com/user/repo", "github.com", "user/repo", "https://github.com/user/repo")));
        yield return ("util.git.scp-with-ref-and-prefix", Sync(() => Git("git:git@github.com:user/repo@v1.0.0", "github.com", "user/repo", "git@github.com:user/repo", "v1.0.0")));
        yield return ("util.git.rejects-unsafe-install-paths", Sync(() =>
        {
            foreach (var source in new[]
                     {
                         "git:git@evil.example:../../victim/repo",
                         "https://evil.example/..%2F..%2Fvictim/repo",
                         "https://evil.example/..%2F..%2Fvictim/repo%",
                         "git:git@evil.example:/absolute/repo",
                         "git:git@evil.example:user\\repo/name",
                         "git:git@evil.example:user/repo\0name",
                     })
                Equal(null, PiSharp.Cli.Interactive.Mode.Utilities.Git.ParseGitUrl(source), source.Replace("\0", "\\0", StringComparison.Ordinal));
        }));
        yield return ("util.git.rejects-shorthand-without-prefix", Sync(() =>
        {
            Equal(null, PiSharp.Cli.Interactive.Mode.Utilities.Git.ParseGitUrl("git@github.com:user/repo"), "scp");
            Equal(null, PiSharp.Cli.Interactive.Mode.Utilities.Git.ParseGitUrl("github.com/user/repo"), "host/path");
            Equal(null, PiSharp.Cli.Interactive.Mode.Utilities.Git.ParseGitUrl("user/repo"), "user/repo");
        }));
        yield return ("util.git.other-hosts-and-generic", Sync(() =>
        {
            Git("https://gitlab.com/group/sub/repo.git", "gitlab.com", "group/sub/repo", "https://gitlab.com/group/sub/repo.git");
            Git("git:git@bitbucket.org:team/repo#main", "bitbucket.org", "team/repo", "git@bitbucket.org:team/repo#main", "main");
            Git("https://git.example.com/team/tool@v2", "git.example.com", "team/tool", "https://git.example.com/team/tool", "v2");
            Git("git:localhost/team/tool", "localhost", "team/tool", "https://localhost/team/tool");
            Git("https://www.github.com/user/repo.git", "github.com", "user/repo", "https://www.github.com/user/repo.git");
            Equal(null, PiSharp.Cli.Interactive.Mode.Utilities.Git.ParseGitUrl("git:intranet/team/tool"), "dotless host");
            Equal(null, PiSharp.Cli.Interactive.Mode.Utilities.Git.ParseGitUrl("https://github.com/user"), "single segment");
        }));
    }

    // ------------------------------------------------------------------ paths.test.ts

    private static IEnumerable<(string, Func<Task>)> PathCases()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return ("util.paths.canonicalize-regular-and-missing", Sync(() =>
        {
            using var dir = new TempDir();
            var file = Path.Combine(dir.Path, "file.txt");
            File.WriteAllText(file, "hello");
            var canonical = Paths.CanonicalizePath(file);
            Check(File.Exists(canonical) && Path.GetFileName(canonical) == "file.txt", "regular file");
            var missing = Path.Combine(dir.Path, "no-such-file");
            Equal(missing, Paths.CanonicalizePath(missing), "missing target");
        }));
        yield return ("util.paths.canonicalize-symlinks", Sync(() =>
        {
            UnixOnly();
            using var dir = new TempDir();
            var target = Path.Combine(dir.Path, "target.txt");
            File.WriteAllText(target, "hello");
            var link = Path.Combine(dir.Path, "link.txt");
            File.CreateSymbolicLink(link, target);
            Equal(Paths.CanonicalizePath(target), Paths.CanonicalizePath(link), "file link");
            var targetDir = Directory.CreateDirectory(Path.Combine(dir.Path, "target-dir")).FullName;
            var linkDir = Path.Combine(dir.Path, "link-dir");
            Directory.CreateSymbolicLink(linkDir, targetDir);
            Equal(Paths.CanonicalizePath(targetDir), Paths.CanonicalizePath(linkDir), "directory link");
            var dangling = Path.Combine(dir.Path, "dangling.txt");
            File.CreateSymbolicLink(dangling, Path.Combine(dir.Path, "nothing.txt"));
            Equal(dangling, Paths.CanonicalizePath(dangling), "dangling link");
        }));
        yield return ("util.paths.cwd-relative", Sync(() =>
        {
            var cwd = Path.Combine(Path.GetTempPath(), "pi-paths-cwd");
            Equal(Path.Combine("..config", "AGENTS.md"), Paths.GetCwdRelativePath(Path.Combine(cwd, "..config", "AGENTS.md"), cwd), "dot-prefixed name");
            Equal(null, Paths.GetCwdRelativePath(Path.Combine(cwd, "..", "AGENTS.md"), cwd), "parent traversal");
            Equal(".", Paths.GetCwdRelativePath(cwd, cwd), "cwd itself");
            Equal("sub/file.ts", Paths.FormatPathRelativeToCwdOrAbsolute(Path.Combine("sub", "file.ts"), cwd), "relative with forward slashes");
            var outside = Path.GetFullPath(Path.Combine(cwd, "..", "other.ts"));
            Equal(outside.Replace('\\', '/'), Paths.FormatPathRelativeToCwdOrAbsolute(outside, cwd), "outside is absolute");
        }));
        yield return ("util.paths.tilde-and-relative", Sync(() =>
        {
            var cwd = Path.Combine(Path.GetTempPath(), "pi-paths-cwd");
            Equal(home, Paths.NormalizePath("~"), "~");
            Equal(Path.Join(home, "file.txt"), Paths.NormalizePath("~/file.txt"), "~/file.txt");
            Equal(Path.GetFullPath(Path.Combine(cwd, "~draft.md")), Paths.ResolvePath("~draft.md", cwd), "~draft.md resolved");
            Equal("~draft.md", Paths.NormalizePath("~draft.md"), "~draft.md literal");
            Equal(Path.GetFullPath(Path.Combine(cwd, "subdir/file.txt")), Paths.ResolvePath("subdir/file.txt", cwd), "relative");
            Equal(Path.GetFullPath(Path.Combine(cwd, "subdir/file.txt")), Paths.ResolvePath("subdir/file.txt", new Uri(cwd).AbsoluteUri), "file URL base");
            Equal("file name.txt", Paths.NormalizePath("@file\u00A0name.txt", new(StripAtPrefix: true, NormalizeUnicodeSpaces: true)), "@ and unicode spaces");
            Equal("x", Paths.NormalizePath("  x  ", new(Trim: true)), "trim");
            Equal("~/x", Paths.NormalizePath("~/x", new(ExpandTilde: false)), "no tilde expansion");
        }));
        yield return ("util.paths.file-urls", Sync(() =>
        {
            using var dir = new TempDir();
            var filePath = Path.Combine(dir.Path, "file with spaces.txt");
            Equal(Path.GetFullPath(filePath), Paths.ResolvePath(new Uri(filePath).AbsoluteUri, Path.Combine(dir.Path, "base")), "file URL");
            Throws<FormatException>(() => Paths.ResolvePath("file:///%E0%A4%A"), "invalid file URL");
        }));
        yield return ("util.paths.posix-literal-percent", Sync(() =>
        {
            UnixOnly();
            using var dir = new TempDir();
            foreach (var name in new[] { "report%2026.md", "foo%2Fbar", "malformed%A.md" })
            {
                var filePath = Path.Combine(dir.Path, name);
                Equal(filePath, Paths.ResolvePath(filePath, Path.Combine(dir.Path, "base")), name);
            }
        }));
        yield return ("util.paths.windows-shell-paths", Sync(() =>
        {
            Equal("C:\\Users\\example\\project", Paths.NormalizeWindowsShellPath("/c/Users/example/project"), "git bash");
            Equal("D:\\work", Paths.NormalizeWindowsShellPath("/cygdrive/d/work"), "cygwin");
            Equal("E:\\source", Paths.NormalizeWindowsShellPath("/mnt/e/source"), "wsl");
            Equal("C:\\", Paths.NormalizeWindowsShellPath("/c"), "drive root");
            foreach (var path in new[] { "C:/Users/example", "C:\\Users\\example", "//server/share/file", "/c/Users\\example", "relative/file", "/tmp/file" })
                Equal(path, Paths.NormalizeWindowsShellPath(path), path);
            if (OperatingSystem.IsWindows())
            {
                Equal("C:\\Users\\example", Paths.NormalizePath("/c/Users/example"), "applied on Windows");
                Equal(Path.GetFullPath("C:/Users/example"), Paths.ResolvePath("/mnt/c/Users/example", "D:\\work"), "resolve on Windows");
            }
        }));
        yield return ("util.paths.is-local", Sync(() =>
        {
            Check(Paths.IsLocalPath("my-package"), "bare name");
            Check(Paths.IsLocalPath("./foo"), "relative");
            Check(Paths.IsLocalPath("file:///tmp/foo"), "file URL");
            Check(!Paths.IsLocalPath("npm:package"), "npm");
            Check(!Paths.IsLocalPath("git://repo"), "git");
            Check(!Paths.IsLocalPath("https://example.com"), "https");
        }));
    }

    // ------------------------------------------------------------------ open-browser.ts

    private static IEnumerable<(string, Func<Task>)> BrowserCases()
    {
        yield return ("util.open-browser.launchers-without-shell", Sync(() =>
        {
            var target = "https://example.com/?a=1&b=2|x^y";
            foreach (var (platform, command, args) in new (string, string, string[])[]
                     {
                         ("darwin", "open", [target]),
                         ("win32", "rundll32", ["url.dll,FileProtocolHandler", target]),
                         ("linux", "xdg-open", [target]),
                     })
            {
                (string, IReadOnlyList<string>)? spawned = null;
                BrowserOpener.OpenBrowser(target, (c, a) => spawned = (c, a), platform);
                Equal(command, spawned!.Value.Item1, platform + " command");
                Seq(args, spawned.Value.Item2, platform + " args");
            }
            BrowserOpener.OpenBrowser("x", platform: "pisharp-missing-platform-launcher-test-" + Guid.NewGuid().ToString("N"),
                spawn: (c, _) => Equal("xdg-open", c, "fallback launcher"));
        }));
    }

    // ------------------------------------------------------------------ bug-report.test.ts

    private sealed class FakeLoader(bool cancelled) : IBugReportLoader
    {
        private readonly CancellationTokenSource _source = cancelled ? new CancellationTokenSource(0) : new();
        public bool Disposed;
        public CancellationToken Signal => _source.Token;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBugUi : IBugReportUi
    {
        public Queue<string?> Answers = [];
        public List<(string Kind, string Title, IReadOnlyList<string> Options, string? Description, string? Initial)> Prompts = [];
        public List<string> Status = [], Errors = [], Loaders = [], Opened = [];
        public bool CancelLoaders;
        /// <summary>The fake URL opener: records the link and answers whether a browser opened (never a real browser).</summary>
        public Func<string, bool> Open = _ => true;
        public List<FakeLoader> LoaderInstances = [];
        public Task<string?> Input(string title, string description, string? initialValue)
        {
            Prompts.Add(("input", title, [], description, initialValue));
            return Task.FromResult(Answers.Dequeue());
        }
        public Task<string?> Choose(string title, IReadOnlyList<string> options, string? description)
        {
            Prompts.Add(("choose", title, options, description, null));
            return Task.FromResult(Answers.Dequeue());
        }
        public IBugReportLoader ShowLoader(string message) { Loaders.Add(message); var loader = new FakeLoader(CancelLoaders); LoaderInstances.Add(loader); return loader; }
        public void ShowStatus(string message) => Status.Add(message);
        public void ShowError(string message) => Errors.Add(message);
        public bool OpenUrl(string url) { Opened.Add(url); return Open(url); }
    }

    private sealed class FakeBugSession : IBugReportSession
    {
        public string? ModelName { get; set; } = "Claude Test";
        public string? ModelProvider { get; set; } = "anthropic";
        public Func<string?, CancellationToken, Task<string>> Summarize = (_, _) => Task.FromResult("summary text");
        public string? SummaryHint;
        public List<(string Type, JsonData Data)> CustomEntries = [];
        public int SerializeCalls;
        public JsonData ReportEnvironment = JsonData.EmptyObject;
        public JsonData? ReportModel;
        public string SessionId => "session-1";
        public Task<string> SummarizeForBugReportAsync(string? hint, CancellationToken cancellationToken) { SummaryHint = hint; return Summarize(hint, cancellationToken); }
        public BugReportMetadataOptions GetMetadataOptions() =>
            new("session-1", "/work", false, false, 3, "medium", JsonData.EmptyObject, JsonData.EmptyObject) { Environment = ReportEnvironment, Model = ReportModel, Id = "report-1" };
        public IEnumerable<SessionEntry> GetEntries() => [];
        public string SerializeSessionBranch() { SerializeCalls++; return "{\"type\":\"session\"}\n"; }
        public void AppendCustomEntry(string customType, JsonData data) => CustomEntries.Add((customType, data));
    }

    private static readonly JsonData BugEnvironment = JsonData.Parse(
        """{"version":"1.1.0","userAgent":"pi/1.1.0 (linux; dotnet/10.0.0; x64)","runtime":"dotnet/10.0.0","platform":"linux","arch":"x64","osRelease":"6.8.0","osVersion":"Ubuntu 24.04.1 LTS","shell":"bash","terminal":{},"piEnvironmentVariables":["PI_OFFLINE"]}""");
    private static readonly JsonData BugModel = JsonData.Parse(
        """{"provider":"anthropic","id":"claude-test-1","name":"Claude Test","api":"anthropic-messages","baseUrl":"https://user:secret@api.example/v1"}""");

    /// <summary>The issue link's decoded <c>title</c> and <c>body</c> query parameters.</summary>
    private static (string Title, string Body) IssueQuery(string url)
    {
        Check(url.StartsWith(BugReportIssue.NewIssueUrl + "?title=", StringComparison.Ordinal), "issue link base: " + url);
        var query = url[(url.IndexOf('?') + 1)..].Split('&');
        Equal(2, query.Length, "two query parameters");
        Check(query[0].StartsWith("title=", StringComparison.Ordinal) && query[1].StartsWith("body=", StringComparison.Ordinal), "title then body");
        return (Uri.UnescapeDataString(query[0]["title=".Length..]), Uri.UnescapeDataString(query[1]["body=".Length..]));
    }

    private static IEnumerable<(string, Func<Task>)> BugReportCases()
    {
        yield return ("util.bug-report.prompt-preserves-line-breaks-and-cancels", async () =>
        {
            var ui = new FakeBugUi { Answers = new(["Request failed\n  ↳ pi exiting...\nstack trace", "No", "No", "Cancel"]) };
            var session = new FakeBugSession { ModelName = null, ModelProvider = null };
            await InteractiveBugReport.ReportBug(session, ui, new() { Env = _ => null });
            Equal("Report a bug", ui.Prompts[0].Title, "description editor");
            Equal($"{InteractiveBugReport.Disclaimer}\n\nWhat went wrong? (optional)", ui.Prompts[0].Description, "editor description");
            Check(!InteractiveBugReport.Disclaimer.Contains("Earendil", StringComparison.Ordinal) && InteractiveBugReport.Disclaimer.StartsWith("Nothing is uploaded.", StringComparison.Ordinal), "consent names the new destination");
            Equal("Include the session transcript?", ui.Prompts[1].Title, "transcript selector");
            Seq(["Yes, include the transcript", "No"], ui.Prompts[1].Options, "transcript options");
            Equal(InteractiveBugReport.TranscriptNote, ui.Prompts[1].Description, "transcript note");
            Equal("Attach a summary written by the current model instead?", ui.Prompts[2].Title, "summary selector");
            Contains(ui.Prompts[2].Description!, "The transcript is sent to your provider with your credentials", "summary note");
            Equal("Bug report", ui.Prompts[3].Title, "delivery selector");
            Seq(["Open GitHub Issue", "Export as Zip", "Cancel"], ui.Prompts[3].Options, "delivery options");
            var lines = ui.Prompts[3].Description!.Split('\n');
            Check(lines.Any(line => line.Contains("Description: Request failed", StringComparison.Ordinal)), "description line");
            Check(lines.Any(line => line.Contains("↳ pi exiting...", StringComparison.Ordinal)), "continuation line");
            Check(lines.Any(line => line.Contains("stack trace", StringComparison.Ordinal)), "stack line");
            Contains(ui.Prompts[3].Description!, "Transcript: not included\nSummary: none\n\nOpen GitHub Issue writes a zip archive to the current directory and opens a prefilled issue at github.com/jkelly/PiSharp/issues; attach the zip there. Nothing is uploaded. Export writes only the zip archive.", "delivery summary");
            Seq(["Bug report cancelled"], ui.Status, "status");
            Equal(0, ui.Opened.Count, "nothing opened");
        });
        yield return ("util.bug-report.cancel-at-description", async () =>
        {
            var ui = new FakeBugUi { Answers = new([null]) };
            await InteractiveBugReport.ReportBug(new FakeBugSession(), ui, new() { Env = _ => null }, "initial hint");
            Equal("initial hint", ui.Prompts.Single().Initial, "initial hint");
            Seq(["Bug report cancelled"], ui.Status, "status");
        });
        // Owner decision 11: "Open GitHub Issue" writes the zip, records the report and opens the prefilled issue with the URL
        // opener; the link carries the description, the summary and the environment, exactly encoded. PI_OFFLINE does not
        // matter: nothing goes over the network.
        yield return ("util.bug-report.github-issue-opens-prefilled-link", async () =>
        {
            using var dir = new TempDir();
            var ui = new FakeBugUi { Answers = new(["  it broke & stopped\nsecond line?  ", "No", "Yes, generate a summary", "Open GitHub Issue"]) };
            var session = new FakeBugSession
            {
                ReportEnvironment = BugEnvironment, ReportModel = BugModel,
                Summarize = (_, _) => Task.FromResult("## What went wrong\nThe `edit` tool failed: 100% #fail\n"),
            };
            await InteractiveBugReport.ReportBug(session, ui, new() { Env = name => name == "PI_OFFLINE" ? "1" : null, Cwd = () => dir.Path, Version = "1.1.0.2" });
            Equal(0, ui.Errors.Count, "no error: " + string.Join("; ", ui.Errors));
            var archive = Path.Combine(dir.Path, "pi-bug-report-report-1.zip");
            Check(File.Exists(archive), "archive written");
            var title = "Bug report: it broke & stopped";
            var body =
                "## Description\n\nit broke & stopped\nsecond line?\n\n" +
                "## Summary\n\n## What went wrong\nThe `edit` tool failed: 100% #fail\n\n" +
                "## Environment\n\n" +
                "- PiSharp: 1.1.0.2\n- Pi baseline: 1.1.0\n- OS: Ubuntu 24.04.1 LTS (linux x64)\n- Runtime: dotnet/10.0.0\n" +
                "- Model: anthropic/claude-test-1 (Claude Test)\n- API: anthropic-messages\n- Thinking level: medium\n" +
                "- Extensions: 0 loaded, 0 failed\n- Failed or diagnosed turns: 0\n- Crashes: 0\n\n" +
                "## Report\n\nReport ID: `report-1`. The full report is `pi-bug-report-report-1.zip` (report.json, diagnostics.json, summary.md) " +
                "on the reporter's machine. Attach it to this issue: GitHub cannot attach files from a link.\n";
            var url = ui.Opened.Single();
            Equal("https://github.com/jkelly/PiSharp/issues/new?title=Bug%20report%3A%20it%20broke%20%26%20stopped&body=" +
                "%23%23%20Description%0A%0Ait%20broke%20%26%20stopped%0Asecond%20line%3F%0A%0A%23%23%20Summary%0A%0A%23%23%20What%20went%20wrong%0A" +
                "The%20%60edit%60%20tool%20failed%3A%20100%25%20%23fail%0A%0A%23%23%20Environment%0A%0A-%20PiSharp%3A%201.1.0.2%0A",
                url[..url.IndexOf("-%20Pi%20baseline", StringComparison.Ordinal)], "exact encoding");
            Equal(BugReportIssue.NewIssueUrl + "?title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body), url, "issue link");
            Equal((title, body), IssueQuery(url), "decoded title and body");
            Check(!url.Contains("secret", StringComparison.Ordinal) && !url.Contains("api.example", StringComparison.Ordinal) && !url.Contains("%2Fwork", StringComparison.Ordinal),
                "no base URL, credentials or paths in the link");
            Seq([$"Bug report exported to: {archive}\nReport ID: report-1\nOpened a prefilled GitHub issue in your browser. Attach the zip to it before submitting. Issue link:\n{url}"],
                ui.Status, "status prints the report path and the link");
            var (type, data) = session.CustomEntries.Single();
            Equal("pi.bug-report", type, "custom entry type");
            Equal("github-issue", data.Value.GetProperty("delivery").GetString(), "delivery");
            Equal(archive, data.Value.GetProperty("path").GetString(), "path");
            using var zip = System.IO.Compression.ZipFile.OpenRead(archive);
            Seq(["report.json", "diagnostics.json", "summary.md"], zip.Entries.Select(entry => entry.FullName), "zip contents unchanged");
        });
        // Headless (no browser) and a failing opener: the report path and the link are printed instead.
        yield return ("util.bug-report.github-issue-headless-prints-path", async () =>
        {
            foreach (var (label, open) in new (string, Func<string, bool>)[] { ("headless", _ => false), ("opener throws", _ => throw new InvalidOperationException("no launcher")) })
            {
                using var dir = new TempDir();
                var ui = new FakeBugUi { Answers = new(["", "Yes, include the transcript", "Open GitHub Issue"]), Open = open };
                var session = new FakeBugSession();
                await InteractiveBugReport.ReportBug(session, ui, new() { Env = _ => null, Cwd = () => dir.Path, Version = "1.1.0.2" });
                var archive = Path.Combine(dir.Path, "pi-bug-report-report-1.zip");
                Check(File.Exists(archive), label + ": archive written");
                var url = ui.Opened.Single();
                Seq([$"Bug report exported to: {archive}\nReport ID: report-1\nNo browser could be opened. Open this link to file the issue, then attach the zip:\n{url}"],
                    ui.Status, label + ": status");
                var (title, body) = IssueQuery(url);
                Equal("Bug report from PiSharp 1.1.0.2", title, label + ": title without a description");
                Contains(body, "## Description\n\n_No description given._\n\n## Environment\n\n- PiSharp: 1.1.0.2\n- Pi baseline: 1.1.0\n- OS: unknown (unknown unknown)\n- Runtime: unknown\n- Model: none\n", label + ": body");
                Contains(body, "(report.json, diagnostics.json, session.jsonl) on the reporter's machine. Attach it to this issue: GitHub cannot attach files from a link. It contains the session transcript; review it before attaching.\n", label + ": transcript warning");
                Check(!body.Contains("{\"type\":\"session\"}", StringComparison.Ordinal), label + ": transcript stays out of the link");
                Equal(1, session.SerializeCalls, label + ": transcript in the zip");
            }
        });
        yield return ("util.bug-report.browser-availability", Sync(() =>
        {
            Func<string, string?> Env(params string[] set) => name => set.Contains(name) ? "x" : null;
            Check(BrowserOpener.CanOpenBrowser(Env(), "win32"), "windows");
            Check(BrowserOpener.CanOpenBrowser(Env(), "darwin"), "macOS");
            Check(!BrowserOpener.CanOpenBrowser(Env(), "linux"), "linux without a display");
            Check(BrowserOpener.CanOpenBrowser(Env("DISPLAY"), "linux"), "X11");
            Check(BrowserOpener.CanOpenBrowser(Env("WAYLAND_DISPLAY"), "freebsd"), "Wayland");
            foreach (var ssh in new[] { "SSH_CONNECTION", "SSH_CLIENT", "SSH_TTY" })
            {
                Check(!BrowserOpener.CanOpenBrowser(Env(ssh), "win32"), ssh + " on Windows");
                Check(!BrowserOpener.CanOpenBrowser(Env(ssh, "DISPLAY"), "linux"), ssh + " with a display");
            }
        }));
        // The link stays within GitHub's limit: the summary is cut first (with a note naming summary.md), then the description.
        yield return ("util.bug-report.github-issue-truncates-long-text", Sync(() =>
        {
            JsonData Metadata(string? hint) => BugReport.CollectBugReportMetadata(
                new("s", "/work", false, true, 1, "off", JsonData.EmptyObject, JsonData.EmptyObject) { Hint = hint, Id = "r", Environment = BugEnvironment, Model = BugModel });
            var diagnostics = BugReport.CollectBugReportDiagnostics("s", []);
            var summary = "Start. " + string.Concat(Enumerable.Repeat("data 😀 & more; ", 3000)) + "END";
            var url = BugReportIssue.CreateIssueUrl(new(Metadata("short"), diagnostics, null, summary), "1.1.0.2", "pi-bug-report-r.zip");
            Check(url.Length <= BugReportIssue.MaxUrlLength && url.Length > BugReportIssue.MaxUrlLength - 200, "summary cut close to the limit: " + url.Length);
            var (title, body) = IssueQuery(url);
            Equal("Bug report: short", title, "title");
            Contains(body, "## Description\n\nshort\n\n## Summary\n\nStart. data 😀 & more; ", "summary kept from the start");
            Contains(body, "\n\n" + BugReportIssue.SummaryTruncatedNote + "\n\n## Environment\n\n- PiSharp: 1.1.0.2\n", "summary note");
            Check(!body.Contains("END", StringComparison.Ordinal) && !body.Contains('�'), "cut summary, no broken surrogate pair");
            Contains(body, "## Report\n\nReport ID: `r`. The full report is `pi-bug-report-r.zip` (report.json, diagnostics.json, summary.md)", "report section kept");

            var hint = "Huge " + string.Concat(Enumerable.Repeat("description line\n", 2000));
            var both = BugReportIssue.CreateIssueUrl(new(Metadata(hint), diagnostics, null, summary), "1.1.0.2", "pi-bug-report-r.zip");
            Check(both.Length <= BugReportIssue.MaxUrlLength, "description cut too: " + both.Length);
            var (_, cutBody) = IssueQuery(both);
            Contains(cutBody, "## Description\n\nHuge description line\n", "description kept from the start");
            Contains(cutBody, "\n\n" + BugReportIssue.DescriptionTruncatedNote + "\n\n## Summary\n\n" + BugReportIssue.SummaryTruncatedNote + "\n\n## Environment\n\n", "both notes");

            Equal("Bug report: " + new string('x', 85) + "...", BugReportIssue.Title(new string('x', 200) + "\nmore", "1"), "long title cut to 100 characters");
            Equal("Bug report: first", BugReportIssue.Title("  first  \r\nsecond", "1"), "first line only");
        }));
        yield return ("util.bug-report.zip-export-with-summary", async () =>
        {
            using var dir = new TempDir();
            var ui = new FakeBugUi { Answers = new(["  it broke  ", "No", "Yes, generate a summary", "Export as Zip"]) };
            var session = new FakeBugSession();
            await InteractiveBugReport.ReportBug(session, ui, new() { Env = _ => null, Cwd = () => dir.Path });
            Contains(ui.Prompts[3].Description!, "Description: it broke\nTranscript: not included\nSummary: written by Claude Test", "delivery summary");
            Seq(["Writing summary with Claude Test..."], ui.Loaders, "loader");
            Check(ui.LoaderInstances[0].Disposed, "editor restored");
            Equal("it broke", session.SummaryHint, "summary hint");
            Equal(0, session.SerializeCalls, "no transcript");
            var archive = Path.Combine(dir.Path, "pi-bug-report-report-1.zip");
            Check(File.Exists(archive), "archive written");
            Seq([$"Bug report exported to: {archive}\nReport ID: report-1"], ui.Status, "status");
            Equal(0, ui.Opened.Count, "no issue for a plain export");
            var (type, data) = session.CustomEntries.Single();
            Equal("pi.bug-report", type, "custom entry type");
            Equal("zip", data.Value.GetProperty("delivery").GetString(), "delivery");
            Equal(archive, data.Value.GetProperty("path").GetString(), "path");
            Equal("it broke", data.Value.GetProperty("hint").GetString(), "hint");
            Check(data.Value.GetProperty("summaryIncluded").GetBoolean(), "summary included");
            Check(!data.Value.GetProperty("sessionIncluded").GetBoolean(), "session not included");
        });
        yield return ("util.bug-report.summary-cancelled-and-failed", async () =>
        {
            var ui = new FakeBugUi { Answers = new(["x", "No", "Yes, generate a summary", "Open GitHub Issue"]), CancelLoaders = true };
            await InteractiveBugReport.ReportBug(new FakeBugSession(), ui, new() { Env = _ => null });
            Seq(["Bug report cancelled"], ui.Status, "cancelled while summarizing");
            var failing = new FakeBugUi { Answers = new(["x", "No", "Yes, generate a summary", "Open GitHub Issue"]) };
            var session = new FakeBugSession { Summarize = (_, _) => Task.FromException<string>(new InvalidOperationException("No model selected")) };
            await InteractiveBugReport.ReportBug(session, failing, new() { Env = _ => null });
            Seq(["Failed to write bug report summary: No model selected"], failing.Errors, "summary error");
            Check(failing.LoaderInstances[0].Disposed, "editor restored after failure");
            Equal(0, ui.Opened.Count + failing.Opened.Count, "no issue opened");
        });
        // Nothing uploads: Pi's uploader (bug-report-upload.ts, Radius) is not part of PiSharp.
        yield return ("util.bug-report.no-uploader", Sync(() =>
        {
            Check(typeof(BugReport).Assembly.GetType("PiSharp.CodingAgent.Diagnostics.BugReportUpload") is null, "no bug report uploader");
            Check(typeof(BugReportEnvironment).GetProperties().All(property => property.PropertyType != typeof(HttpMessageInvoker)), "the /bug flow has no HTTP client");
        }));
        yield return ("util.bug-report.redaction", Sync(() =>
        {
            Equal("https://proxy.example.com:8080/", BugReportRedaction.RedactUrl("https://user:pass@proxy.example.com:8080/"), "credentials");
            Equal("git:https://github.com/org/repo", BugReportRedaction.RedactUrl("git:https://pat@github.com/org/repo"), "git prefix");
            Equal("https://api.example/v1?api-key=%3Credacted%3E&model=x", BugReportRedaction.RedactUrl("https://api.example/v1?api-key=abc&model=x"), "query");
        }));
    }
}
