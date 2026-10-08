using System.Text.Json;

// Authored native expectations for the Pi v1.1.0 sync rows wire.*, ext.* and tools.*.
// No upstream execution or genuine source capture credit.
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) throw new ArgumentException("Use [--report <fresh path>].");
        var cases = DurationTests.Cases().Concat(SettledTests.Cases()).Concat(ShellTests.Cases()).Concat(UserBashTests.Cases())
            .Concat(ExtensionTests.Cases()).Concat(ToolTests.Cases()).ToArray();
        var results = new List<object>(); var failures = 0;
        foreach (var (id, run) in cases)
        {
            try { await run().WaitAsync(TimeSpan.FromSeconds(60)); results.Add(new { id, status = "PASS_AUTHORED_NATIVE_ONLY" }); }
            catch (Exception error) { failures++; results.Add(new { id, status = "FAIL", failure = error.ToString() }); }
        }
        var report = new { sourceSha = "abe508e1b89912adde45528136c3221eb69acdd7", status = "AUTHORED NATIVE; SOURCE QUALIFICATION OPEN",
            failures, genuineSourceCasesCaptured = 0, results };
        if (args.Length == 2)
        {
            await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true });
        }
        else Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return failures == 0 ? 0 : 1;
    }
}

/// <summary>Deterministic monotonic clock: one timestamp tick is 0.1 ms.</summary>
internal sealed class ManualClock : TimeProvider
{
    private long _ticks = 5_000_000;
    public override long TimestampFrequency => 10_000;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
    public void Advance(double milliseconds) => Interlocked.Add(ref _ticks, (long)Math.Round(milliseconds * 10));
}

internal static class Assert
{
    public static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void Equal<T>(T expected, T actual, string? what = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException((what is null ? "" : what + ": ") + "expected <" + expected + "> actual <" + actual + ">");
    }
    public static async Task<T> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
