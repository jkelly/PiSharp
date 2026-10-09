using System.Reflection;
using System.Text;
using System.Text.Json;
using PiSharp.Cli.Interactive.Mode;

// Authored expectations for IMPL-I (interactive mode and slash commands) of Pi v1.1.0: packages/tui/src/** and
// packages/coding-agent/src/modes/interactive/**, core/slash-commands.ts, core/keybindings.ts and core/footer-data-provider.ts.
// Component cases render to plain lines (ANSI stripped where the case says so) and compare with snapshots written from the pinned
// sources by reading, ported from the upstream tests where one exists; nothing is captured from an upstream run. Every case class
// is a static class whose name ends in "Cases" with a static All() returning (Id, Run) pairs. Unix-only cases skip on Windows.
internal static class Program
{
    private const string Upstream = "abe508e1b89912adde45528136c3221eb69acdd7";

    private static async Task<int> Main(string[] args)
    {
        string? report = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--report" && report is null && index + 1 < args.Length) report = Path.GetFullPath(args[++index]);
            else throw new ArgumentException("Use [--report <fresh path>].");
        }
        Expect.InitializeEnvironment();
        var cases = new List<(string Id, Func<Task> Run)>();
        foreach (var type in typeof(Program).Assembly.GetTypes().Where(type => type.Name.EndsWith("Cases", StringComparison.Ordinal) && type.IsAbstract && type.IsSealed)
                     .OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            var all = type.GetMethod("All", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (all?.Invoke(null, null) is IEnumerable<(string Id, Func<Task> Run)> found) cases.AddRange(found);
        }
        var filter = Environment.GetEnvironmentVariable("INTERACTIVEPARITY_FILTER");
        if (!string.IsNullOrEmpty(filter)) cases = [.. cases.Where(test => filter.Split('|').Any(part => test.Id.Contains(part, StringComparison.Ordinal)))];
        var results = new List<object>(); var failures = 0; var skipped = 0;
        foreach (var test in cases)
        {
            try
            {
                Expect.ResetPerCase();
                await test.Run().WaitAsync(TimeSpan.FromSeconds(180));
                results.Add(new { test.Id, status = "PASS_AUTHORED_NATIVE_ONLY" });
            }
            catch (SkipCaseException skip) { skipped++; results.Add(new { test.Id, status = "SKIPPED", reason = skip.Message }); }
            catch (Exception error) { failures++; results.Add(new { test.Id, status = "FAIL", failure = error.ToString() }); }
        }
        var output = new { sourceSha = Upstream, status = "AUTHORED NATIVE; NO UPSTREAM CAPTURE", cases = cases.Count, failures, skipped,
            genuineSourceCasesCaptured = 0, results };
        var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (report is not null)
        {
            await using var file = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await file.WriteAsync(Encoding.UTF8.GetBytes(json));
        }
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(json);
        return failures == 0 ? 0 : 1;
    }
}

internal sealed class SkipCaseException(string reason) : Exception(reason);

/// <summary>Assertions and fixtures shared by every case class.</summary>
internal static class Expect
{
    /// <summary>Truecolor, the dark theme and no terminal reports: deterministic escape sequences.</summary>
    public static void InitializeEnvironment()
    {
        PiSharp.Tui.Pi.TerminalImage.SetCapabilities(new PiSharp.Tui.Pi.TerminalCapabilities(PiSharp.Tui.Pi.ImageProtocol.None, true, true));
        ResetPerCase();
    }

    public static void ResetPerCase()
    {
        Themes.ResetForTests();
        Themes.Environment = name => name switch { "PI_CODING_AGENT_DIR" => Path.Combine(Path.GetTempPath(), "pisharp-interactive-parity-agent"), _ => null };
        Themes.ColorModeOverride = PiSharp.Tui.Pi.TerminalColorMode.TrueColor;
        Themes.InitTheme("dark");
        PiSharp.Tui.Pi.KeybindingsManager.SetGlobal(new PiSharp.Tui.Pi.KeybindingsManager(AppKeybindings.Definitions(_ => null)));
    }

    public static void UnixOnly() { if (OperatingSystem.IsWindows()) throw new SkipCaseException("Unix-only case."); }
    public static Func<Task> Sync(Action run) => () => { run(); return Task.CompletedTask; };
    public static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    public static void Equal<T>(T expected, T actual, string what)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected <{expected}>, actual <{actual}>."); }

    /// <summary>Removes SGR, OSC and APC sequences (ANSI styling, hyperlinks, cursor markers).</summary>
    public static string Strip(string text) => PiSharp.Tui.Pi.TextUtils.StripTerminalSequences(text);
    public static List<string> Strip(IEnumerable<string> lines) => [.. lines.Select(Strip)];

    /// <summary>Compares rendered lines (ANSI stripped, trailing spaces trimmed) with an expected snapshot.</summary>
    public static void Lines(IEnumerable<string> expected, IEnumerable<string> actual, string what, bool trimEnd = true)
    {
        var e = expected.Select(line => trimEnd ? line.TrimEnd() : line).ToList();
        var a = Strip(actual).Select(line => trimEnd ? line.TrimEnd() : line).ToList();
        if (!e.SequenceEqual(a, StringComparer.Ordinal))
            throw new InvalidOperationException($"{what}:\n--- expected ({e.Count})\n{string.Join("\n", e.Select(l => "|" + l + "|"))}\n--- actual ({a.Count})\n{string.Join("\n", a.Select(l => "|" + l + "|"))}");
    }

    public static void Contains(string haystack, string needle, string what) =>
        Check(haystack.Contains(needle, StringComparison.Ordinal), $"{what}: <{needle}> not found in <{haystack}>.");

    public static T Throws<T>(Action run, string what) where T : Exception
    {
        try { run(); } catch (T error) { return error; }
        throw new InvalidOperationException(what + ": expected " + typeof(T).Name + ".");
    }

    /// <summary>A fresh temporary directory removed on dispose.</summary>
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pisharp-interactive-parity", Guid.NewGuid().ToString("N")[..10]);
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
