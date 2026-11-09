// Pi 7d073328e44337d3292a7db9b0f0308f679c5e97 (MIT): packages/env/daemon/src/{window,exec}.rs.
using System.Text;
using System.Text.Json.Nodes;

namespace PiSharp.Env.Daemon;

internal sealed class ProcessSpill : IAsyncDisposable
{
    private readonly long afterBytes, afterLines;
    private readonly List<byte[]> prefix = [];
    private long bytes, newlines;
    private FileStream? file;
    public string? Path { get; private set; }
    public ProcessSpill(JsonObject json)
    {
        afterBytes = json["afterBytes"]?.GetValue<long>() ?? throw new EnvDaemonException("EINVAL", "spill needs afterBytes");
        afterLines = json["afterLines"]?.GetValue<long>() ?? throw new EnvDaemonException("EINVAL", "spill needs afterLines");
        if (afterBytes < 0 || afterLines < 0) throw new EnvDaemonException("EINVAL", "Invalid spill threshold");
    }
    public async Task PushAsync(byte[] chunk)
    {
        if (chunk.Length == 0) return;
        try
        {
            if (file is null)
            {
                bytes = checked(bytes + chunk.Length); newlines = checked(newlines + chunk.LongCount(value => value == 10));
                var lines = newlines + (chunk[^1] == 10 ? 0 : 1);
                if (bytes <= afterBytes && lines <= afterLines) { prefix.Add(chunk); return; }
                var directory = Directory.CreateTempSubdirectory("tmp-");
                Path = System.IO.Path.Combine(directory.FullName, "pi-output-" + Guid.NewGuid() + ".log");
                file = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous);
                foreach (var held in prefix) await file.WriteAsync(held).ConfigureAwait(false);
                prefix.Clear();
            }
            await file.WriteAsync(chunk).ConfigureAwait(false);
        }
        catch (Exception error) { throw new EnvDaemonException("unknown", "Failed to preserve complete shell output: " + error.Message); }
    }
    public async ValueTask DisposeAsync() { if (file is not null) await file.DisposeAsync().ConfigureAwait(false); }
}

internal sealed class ProcessOutputWindow
{
    internal sealed record Output(int Stream, string Text, JsonObject? Skipped);
    private readonly long maxBytes, maxLines;
    private readonly double interval, bytesPerSecond;
    private readonly List<(int Stream, string Text)> segments = [];
    private long bytes, newlines, skippedBytes, skippedNewlines;
    private bool skippedEndsWithNewline;
    public long NextSend { get; private set; } = Environment.TickCount64;
    public bool IsEmpty => segments.Count == 0;
    public ProcessOutputWindow(JsonObject json)
    {
        maxBytes = json["maxBytes"]?.GetValue<long>() ?? throw new EnvDaemonException("EINVAL", "window needs maxBytes");
        maxLines = json["maxLines"]?.GetValue<long>() ?? throw new EnvDaemonException("EINVAL", "window needs maxLines");
        interval = json["minIntervalMs"]?.GetValue<double>() ?? throw new EnvDaemonException("EINVAL", "window needs minIntervalMs");
        bytesPerSecond = json["bytesPerSecond"]?.GetValue<double>() ?? throw new EnvDaemonException("EINVAL", "window needs bytesPerSecond");
        if (maxBytes < 0 || maxLines < 0 || maxBytes > int.MaxValue || maxLines > int.MaxValue || !double.IsFinite(interval) || !double.IsFinite(bytesPerSecond))
            throw new EnvDaemonException("EINVAL", "Invalid output window");
        interval = Math.Max(0, interval); bytesPerSecond = Math.Max(1, bytesPerSecond);
    }
    private static long Size(string text) => Encoding.UTF8.GetByteCount(text);
    private static long Lines(string text) => text.LongCount(character => character == '\n');
    public void Push(int stream, string text)
    {
        if (text.Length == 0) return;
        bytes += Size(text); newlines += Lines(text);
        if (segments.Count > 0 && segments[^1].Stream == stream) segments[^1] = (stream, segments[^1].Text + text);
        else segments.Add((stream, text));
        if (bytes > 4 * (maxBytes + 1) || newlines > 4 * (maxLines + 1)) Trim();
    }
    private bool Proves(long remainingBytes, long remainingLines) => remainingBytes > maxBytes || remainingLines > maxLines;
    private void Removed(string text)
    {
        var length = Size(text); var lines = Lines(text);
        bytes -= length; newlines -= lines; skippedBytes += length; skippedNewlines += lines;
        skippedEndsWithNewline = text.EndsWith('\n');
    }
    private void Trim()
    {
        while (segments.Count > 1 && Proves(bytes - Size(segments[0].Text), newlines - Lines(segments[0].Text)))
        { Removed(segments[0].Text); segments.RemoveAt(0); }
        var first = segments[0];
        var cut = 0; var removedBytes = 0L; var removedLines = 0L;
        // Keep the longest prefix removal whose remaining text still proves exclusion. Rune boundaries preserve UTF-8.
        foreach (var rune in first.Text.EnumerateRunes())
        {
            var nextBytes = removedBytes + rune.Utf8SequenceLength;
            var nextLines = removedLines + (rune.Value == 10 ? 1 : 0);
            if (!Proves(bytes - nextBytes, newlines - nextLines)) break;
            removedBytes = nextBytes; removedLines = nextLines; cut += rune.Utf16SequenceLength;
        }
        if (cut > 0)
        {
            Removed(first.Text[..cut]);
            if (cut == first.Text.Length) segments.RemoveAt(0);
            else segments[0] = (first.Stream, first.Text[cut..]);
        }
    }
    public IReadOnlyList<Output> Take()
    {
        var delay = Math.Max(interval, Math.Min(bytes, maxBytes) / bytesPerSecond * 1000);
        NextSend = Environment.TickCount64 + (long)Math.Min(delay, int.MaxValue);
        Output[] output;
        if (skippedBytes > 0)
        {
            output = [new(segments.Count == 0 ? 0 : segments[^1].Stream, string.Concat(segments.Select(segment => segment.Text)),
                new() { ["bytes"] = skippedBytes, ["newlines"] = skippedNewlines, ["endsWithNewline"] = skippedEndsWithNewline })];
        }
        else output = segments.Select(segment => new Output(segment.Stream, segment.Text, null)).ToArray();
        segments.Clear(); bytes = newlines = skippedBytes = skippedNewlines = 0;
        return output;
    }
}
