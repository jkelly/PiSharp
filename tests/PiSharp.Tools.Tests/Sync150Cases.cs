// Pi f9a26365568127461c47a07a37dcb42c140a3ac5 (MIT): packages/coding-agent/test/tools.test.ts (grep --no-config and a lone CR in
// context), test/ansi-utils.test.ts (private-parameter CSI), test/windows-shell-config.test.ts (legacy WSL bash under SystemRoot)
// and test/image-processing.test.ts (the smaller of PNG and JPEG). Pi 1.5.0 behaviour; inputs and expectations are the upstream
// tests' where they exist, read from the source and not captured from a run, and authored otherwise.
using System.Text;
using PiSharp.Tools.Images;
using PiSharp.Tools.Processes;
using PiSharp.Tools.Skia;

internal static partial class GrepToolTests
{
    public static (string Name, Func<Task> Run)[] Sync150Cases() =>
    [
        ("sync150.grep-no-config leads the ripgrep argv so RIPGREP_CONFIG_PATH cannot change the search", Sync150NoConfig),
        ("sync150.grep-lone-cr-context keeps context lines on ripgrep's line numbers", Sync150LoneCrContext),
    ];

    private static async Task Sync150NoConfig()
    {
        using var f = new Fixture();
        await f.Executor().GrepAsync(new("a.b", f.Root, null, false, false, 100), default);
        Check(f.Runner.Last!.Arguments.SequenceEqual(["--no-config", "--json", "--line-number", "--color=never", "--hidden", "--", "a.b", f.Root]),
            "argv: " + string.Join(' ', f.Runner.Last!.Arguments));
    }

    private static async Task Sync150LoneCrContext()
    {
        // Upstream: "before\rprogress\nneedle\nafter" searched for needle with context 1; ripgrep reports needle on line 2.
        using var f = new Fixture(); var reader = new ContextReader();
        reader.Contents[f.File] = Encoding.UTF8.GetBytes("before\rprogress\nneedle\nafter");
        f.Runner.Result = Receipt(Event(f.File, 2, "needle\n"), "", 0);
        var result = await Invoke(f.Tool(reader), new { pattern = "needle", context = 1 });
        Check(!result.IsError && Text(result) == "a.cs-1- beforeprogress\na.cs:2: needle\na.cs-3- after", "context: " + Text(result));
    }
}

internal static class Sync150Tests
{
    public static (string Name, Func<Task> Run)[] Cases() =>
    [
        ("sync150.strip-ansi-private-csi strips private-parameter CSI and keeps digits after keypad modes", Sync(StripPrivateCsi)),
        ("sync150.windows-shell-install-dirs finds the legacy WSL launcher under SystemRoot", Sync(LegacyWslUnderSystemRoot)),
        ("sync150.windows-store-shell-aliases counts app execution aliases as present", Sync(StoreAliases)),
        ("sync150.image-resize-smaller-encoding picks JPEG over a larger PNG and keeps PNG when it is smaller", Sync(SmallerEncoding)),
    ];

    private static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };

    private static void StripPrivateCsi()
    {
        Equal("step 1", ShellAnsiText.StripAnsi("\u001b[<35;59;1Mstep 1"), "SGR mouse report");
        Equal("ab", ShellAnsiText.StripAnsi("a\u001b[<0;10;20mb"), "< prefix");
        Equal("ab", ShellAnsiText.StripAnsi("a\u001b[>0;276;0cb"), "> prefix with parameters");
        Equal("ab", ShellAnsiText.StripAnsi("a\u001b[>cb"), "> prefix alone");
        Equal("ab", ShellAnsiText.StripAnsi("a\u001b[=5hb"), "= prefix");
        Equal("ab", ShellAnsiText.StripAnsi("a\u009b<35;59;1Mb"), "C1 introducer");
        Equal("123 tests passed", ShellAnsiText.StripAnsi("\u001b[?1h\u001b=123 tests passed"), "ESC = keeps the digits");
        Equal("42 files", ShellAnsiText.StripAnsi("\u001b[?1l\u001b>42 files"), "ESC > keeps the digits");
        Equal("10 items", ShellAnsiText.StripAnsi("\u001b<10 items"), "ESC < keeps the digits");
        Equal(("x", "\u001b[<35;59"), ShellAnsiText.SplitIncompleteAnsiSuffix("x\u001b[<35;59"), "unfinished private CSI held back");
        Equal(("x\u001b=", ""), ShellAnsiText.SplitIncompleteAnsiSuffix("x\u001b="), "ESC = is complete");
        // Streaming: a mouse report split across chunks leaks nothing.
        var stream = new ShellTextStream();
        Equal("x", stream.AppendText("x\u001b[<35;5"), "first chunk");
        Equal("9y", stream.AppendText("9;1M9y"), "second chunk");
    }

    private static void LegacyWslUnderSystemRoot()
    {
        (string? SystemRoot, string Shell, bool Stdin)[] cases =
        [
            ("D:\\WinNT\\", "d:/WINNT/System32/BASH.EXE", true),
            ("D:\\WinNT", "D:\\WinNT\\Sysnative\\bash.exe", true),
            (null, "E:\\Windows\\System32\\bash.exe", true),
            ("D:\\WinNT", "D:\\Git\\bin\\bash.exe", false),
            ("D:\\WinNT", "E:\\Windows\\System32\\bash.exe", false),
            ("WinNT", "C:\\Windows\\System32\\bash.exe", false),
            // Authored: an empty SystemRoot counts as unset, as `if (systemRoot)` has it; "." and ".." segments are resolved.
            ("", "C:\\Windows\\System32\\bash.exe", true),
            ("D:\\WinNT", "D:\\WinNT\\Temp\\..\\System32\\.\\bash.exe", true),
        ];
        foreach (var (systemRoot, shell, stdin) in cases)
        {
            var host = new ShellHost(true, name => name == "SystemRoot" ? systemRoot : null, _ => true, "C:\\Users\\pi");
            var config = ShellDiscovery.Resolve(shell, host);
            var what = $"SystemRoot={systemRoot ?? "unset"} shell={shell}";
            Equal(shell, config.Shell, what);
            Check(config.Arguments.SequenceEqual(stdin ? ["-s"] : ["-c"]) &&
                config.CommandTransport == (stdin ? ShellCommandTransport.Stdin : ShellCommandTransport.Argv), what);
        }
    }

    private static void StoreAliases()
    {
        // Source shellPathExists (access F_OK) accepts an app execution alias, a reparse point existsSync rejects. File.Exists reads
        // attributes without following it, so ShellHost.Current, which uses it, accepts the alias too.
        Check(ShellHost.Current.FileExists.Method == typeof(File).GetMethod(nameof(File.Exists), [typeof(string)]),
            "ShellHost.Current checks files with File.Exists");
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("LOCALAPPDATA") is not { Length: > 0 } local) return;
        var apps = Path.Combine(local, "Microsoft", "WindowsApps");
        if (!Directory.Exists(apps)) return;
        foreach (var alias in Directory.EnumerateFiles(apps, "*.exe").Where(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)).Take(3))
        {
            Check(File.Exists(alias), "alias present: " + alias);
            Equal(alias, ShellDiscovery.Resolve(alias, ShellHost.Current).Shell, "custom shell path " + alias);
        }
    }

    private static void SmallerEncoding()
    {
        // Upstream photoLikeJpeg: smooth gradients with light deterministic noise, so PNG compresses it poorly.
        const int width = 1200, height = 800;
        var pixels = new byte[width * height * 4];
        var seed = 1L;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                seed = (seed * 1103515245 + 12345) & 0x7fffffff;
                var noise = (int)(seed % 6);
                var i = (y * width + x) * 4;
                pixels[i] = (byte)(int)((double)x / width * 200 + noise);
                pixels[i + 1] = (byte)(int)((double)y / height * 200 + noise);
                pixels[i + 2] = (byte)(int)(128 + Math.Sin(x / 30.0) * 60 + noise);
                pixels[i + 3] = 255;
            }
        var input = SkiaCodecTests.Jpeg(new SkiaCodecTests.Raster(width, height, pixels), 85);
        var result = ImageProcessor.Resize(input, "image/jpeg", new(MaxWidth: 1000, MaxHeight: 1000), SkiaImageCodec.Instance)
            ?? throw new InvalidOperationException("The photo did not resize.");
        Check(result.WasResized && result.Width == 1000, $"resized to {result.Width}");
        Equal("image/jpeg", result.MimeType, "photo encoding");
        Check(result.Data.Length < (input.Length + 2) / 3 * 4 * 2, $"base64 {result.Data.Length} for a {input.Length}-byte input");
        // Authored: a flat image is smaller as PNG, so PNG still wins there.
        var flat = new byte[width * height * 4];
        for (var index = 0; index < flat.Length; index += 4) { flat[index] = 40; flat[index + 1] = 80; flat[index + 2] = 120; flat[index + 3] = 255; }
        var screenshot = ImageProcessor.Resize(SkiaCodecTests.Jpeg(new SkiaCodecTests.Raster(width, height, flat), 95), "image/jpeg",
            new(MaxWidth: 1000, MaxHeight: 1000), SkiaImageCodec.Instance)!;
        Equal("image/png", screenshot.MimeType, "flat encoding");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>.");
    }
}
