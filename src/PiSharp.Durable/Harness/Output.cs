// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/durable/src/harness/output.ts.
//
// Mappings:
// - TextEncoder and TextDecoder("utf-8", {ignoreBOM: true}) are a UTF8Encoding without a preamble that substitutes invalid data: a lone
//   surrogate encodes as U+FFFD, and decoding keeps a U+FEFF and replaces each maximal invalid subpart with one U+FFFD, as the WHATWG
//   decoder does. The streaming `decode(chunk, {stream: true})` is a System.Text.Decoder, and `decode()` is its flush.
// - Byte offsets and counts are long; `Uint8Array.indexOf` and `lastIndexOf` are span searches with upstream's start positions.
// - Progress: Date.now and setTimeout are the clock and timers of a TimeProvider (the Harness's in a tool task, so fake time drives it as
//   vi fake timers would). As upstream, mark() starts the write synchronously, so what it captures is the state at that call, and a write
//   that completes synchronously settles its waiters inline. A lock guards the state: timers fire and tools report on other threads. The
//   write itself runs outside the lock.
using System.Text;
using PiSharp.Durable.Env;

namespace PiSharp.Durable;

/// <summary>Retention limits of one tool's output (upstream <c>OutputLimits</c>).</summary>
internal readonly record struct OutputLimits(long MaxBytes, long MaxLines, ToolOutputRetention Retain);

/// <summary>Retained output and what the limits dropped (upstream <c>BoundedOutput</c>).</summary>
internal readonly record struct BoundedOutput(string Text, long DroppedBytes, long DroppedLines);

/// <summary>An exact slice of the input within the limits, and what it left out (upstream <c>OutputSlice</c>).</summary>
internal readonly record struct OutputSlice(string Text, long Bytes, long DroppedBytes, long DroppedLines);

/// <summary>Bounds and sanitizing of tool output (upstream <c>output.ts</c>).</summary>
internal static class ToolOutput
{
    private const byte Newline = 0x0a;

    /// <summary>Each progress commit also buys a pause proportional to what it wrote (upstream <c>PROGRESS_BYTES_PER_SECOND</c>).</summary>
    public const double ProgressBytesPerSecond = 100 * 1024;

    /// <summary>TextEncoder and TextDecoder: no byte-order mark is written or dropped, and invalid data is replaced.</summary>
    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Removes control characters that break display and transcripts; tabs and newlines stay (upstream <c>sanitizeOutput</c>,
    /// <c>/[\x00-\x08\x0b-\x1f￹-￻]/g</c>).</summary>
    public static string SanitizeOutput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var index = 0;
        while (index < text.Length && !Invalid(text[index])) index++;
        if (index == text.Length) return text;
        var builder = new StringBuilder(text.Length);
        builder.Append(text, 0, index);
        for (; index < text.Length; index++)
            if (!Invalid(text[index])) builder.Append(text[index]);
        return builder.ToString();
    }

    private static bool Invalid(char character) =>
        character <= '\x08' || (character >= '\x0b' && character <= '\x1f') || (character >= '￹' && character <= '￻');

    /// <summary>
    /// Bounds <paramref name="text"/> to whole lines within the limits: the first lines for head, the last lines for tail (upstream
    /// <c>boundOutput</c>). The result is an exact slice, trailing newline included. A single line longer than <c>MaxBytes</c> is cut at the
    /// byte limit on a character boundary.
    /// </summary>
    public static OutputSlice BoundOutput(string text, OutputLimits limits)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Utf8.GetBytes(text);
        var (from, to) = limits.Retain == ToolOutputRetention.Head ? HeadRange(bytes, limits) : TailRange(bytes, limits);
        var kept = bytes.AsSpan(from, to - from);
        return new OutputSlice(
            kept.Length == bytes.Length ? text : Utf8.GetString(kept),
            kept.Length,
            bytes.Length - kept.Length,
            LineCount(bytes) - LineCount(kept));
    }

    private static (int From, int To) HeadRange(byte[] bytes, OutputLimits limits)
    {
        if (limits.MaxLines == 0 || limits.MaxBytes == 0) return (0, 0);
        var end = bytes.Length;
        long lines = 0;
        for (var index = IndexOf(bytes, 0); index != -1; index = IndexOf(bytes, index + 1))
        {
            if (++lines == limits.MaxLines)
            {
                end = index + 1;
                break;
            }
        }
        if (end > limits.MaxBytes)
        {
            var newline = LastIndexOf(bytes, (int)limits.MaxBytes - 1);
            end = newline == -1 ? CharacterEnd(bytes, (int)limits.MaxBytes) : newline + 1;
        }
        return (0, end);
    }

    private static (int From, int To) TailRange(byte[] bytes, OutputLimits limits)
    {
        if (limits.MaxLines == 0 || limits.MaxBytes == 0) return (bytes.Length, bytes.Length);
        // A trailing newline ends the last line rather than starting another.
        var last = bytes.Length > 0 && bytes[^1] == Newline ? bytes.Length - 2 : bytes.Length - 1;
        var start = 0;
        long lines = 1;
        for (var index = last < 0 ? -1 : LastIndexOf(bytes, last); index != -1;)
        {
            if (lines == limits.MaxLines)
            {
                start = index + 1;
                break;
            }
            lines++;
            index = index == 0 ? -1 : LastIndexOf(bytes, index - 1);
        }
        if (bytes.Length - start > limits.MaxBytes)
        {
            var from = (int)(bytes.Length - limits.MaxBytes);
            var newline = IndexOf(bytes, from - 1);
            // The first line starting inside the byte window, or a cut of the last line when it alone is too long.
            start = newline != -1 && newline + 1 < bytes.Length ? newline + 1 : CharacterStart(bytes, from);
        }
        return (start, bytes.Length);
    }

    /// <summary>The last character boundary at or before <paramref name="index"/> (upstream <c>characterEnd</c>).</summary>
    public static int CharacterEnd(ReadOnlySpan<byte> bytes, int index)
    {
        var end = index;
        while (end > 0 && ((end < bytes.Length ? bytes[end] : 0) & 0xc0) == 0x80) end--;
        return end;
    }

    /// <summary>The first character boundary at or after <paramref name="index"/>.</summary>
    private static int CharacterStart(ReadOnlySpan<byte> bytes, int index)
    {
        var start = index;
        while (start < bytes.Length && (bytes[start] & 0xc0) == 0x80) start++;
        return start;
    }

    internal static long LineCount(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return 0;
        long newlines = bytes.Count(Newline);
        return newlines + (bytes[^1] == Newline ? 0 : 1);
    }

    /// <summary>The first newline at or after <paramref name="from"/> (<c>Uint8Array.indexOf(NEWLINE, from)</c>).</summary>
    internal static int IndexOf(ReadOnlySpan<byte> bytes, int from)
    {
        if (from < 0) from = 0;
        if (from >= bytes.Length) return -1;
        var found = bytes[from..].IndexOf(Newline);
        return found == -1 ? -1 : from + found;
    }

    /// <summary>The last newline at or before <paramref name="from"/> (<c>Uint8Array.lastIndexOf(NEWLINE, from)</c> for
    /// <c>from &gt;= 0</c>).</summary>
    internal static int LastIndexOf(ReadOnlySpan<byte> bytes, int from)
    {
        if (from < 0) return -1;
        return bytes[..Math.Min(from + 1, bytes.Length)].LastIndexOf(Newline);
    }

    /// <summary>The shortest suffix of <paramref name="text"/> with more than <c>MaxBytes</c> bytes or more than <c>MaxLines</c> newlines,
    /// or all of it (upstream <c>tailMargin</c>). The tail window of any text that ends with this suffix, followed by anything, is the same as
    /// of <paramref name="text"/> followed by it.</summary>
    internal static string TailMargin(string text, OutputLimits limits)
    {
        var bytes = Utf8.GetBytes(text);
        var byteStart = bytes.Length > limits.MaxBytes ? CharacterEnd(bytes, (int)(bytes.Length - limits.MaxBytes - 1)) : 0;
        var lineStart = 0;
        long newlines = 0;
        for (var index = LastIndexOf(bytes, bytes.Length - 1); index != -1; index = LastIndexOf(bytes, index - 1))
        {
            if (++newlines > limits.MaxLines)
            {
                lineStart = index;
                break;
            }
            if (index == 0) break;
        }
        var start = Math.Max(byteStart, lineStart);
        return start == 0 ? text : Utf8.GetString(bytes.AsSpan(start));
    }

    /// <summary>Lines of text with <paramref name="newlines"/> newlines; a final unterminated line counts.</summary>
    internal static long Lines(long newlines, bool terminated) => newlines + (terminated ? 0 : 1);

    internal static long CountNewlines(string text) => text.AsSpan().Count('\n');
}

/// <summary>
/// Bounded running output of one tool call (upstream <c>OutputBuffer</c>). Accepting a chunk costs time proportional to the chunk: head
/// retention stops storing once the window is full, and tail retention drops stored text the window no longer needs when it snapshots.
/// Counts of the whole stream are kept so the dropped totals stay exact. Not thread-safe; the tool task serializes its use.
/// </summary>
internal sealed class OutputBuffer(OutputLimits limits)
{
    private readonly record struct Chunk(string Text, long Bytes, long Newlines);

    /// <summary>
    /// Decoder of byte chunks. A string chunk or a skip ends an incomplete character of earlier bytes (it becomes U+FFFD); the next byte
    /// chunk then starts a new character. As in <c>StreamDecoder</c>, only a byte-order mark at the very start of the output is dropped,
    /// never a U+FEFF later in it.
    /// </summary>
    private readonly Decoder decoder = ToolOutput.Utf8.GetDecoder();
    private bool started;
    /// <summary>Stored chunks: for head the start of the stream, for tail a suffix that still contains the next window.</summary>
    private Queue<Chunk> chunks = new();
    private long storedBytes;
    private long storedNewlines;
    private bool full;
    private long totalBytes;
    private long totalNewlines;
    private bool endsWithNewline = true;

    /// <summary>Bytes currently held; bounded by the limits plus one chunk.</summary>
    public long StoredBytes => storedBytes;

    /// <summary>
    /// Accepts a string chunk; returns whether anything was accepted. <paramref name="skipped"/> is output omitted right before the chunk,
    /// which must be more than the tail window by at least one byte or line (<c>ShellOutputInfo.skipped</c>); only tail retention accepts it.
    /// </summary>
    public bool Push(string chunk, ShellOutputSkip? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        // Bytes of an incomplete character from an earlier byte chunk come first.
        return Accept(Decode([], flush: true), chunk, isString: true, skipped);
    }

    /// <summary>Accepts a byte chunk; see <see cref="Push(string, ShellOutputSkip?)"/>.</summary>
    public bool Push(ReadOnlySpan<byte> chunk, ShellOutputSkip? skipped = null)
    {
        var pending = skipped is not null ? Decode([], flush: true) : "";
        return Accept(pending, Decode(chunk, flush: false), isString: false, skipped);
    }

    private bool Accept(string pending, string text, bool isString, ShellOutputSkip? skipped)
    {
        var first = !started && pending.Length == 0 && skipped is null;
        if (pending.Length > 0 || text.Length > 0 || skipped is not null) started = true;
        if (first && !isString && text.StartsWith('﻿')) text = text[1..];
        if (skipped is null) return Accept(pending + text);
        if (limits.Retain != ToolOutputRetention.Tail) throw new InvalidOperationException("Skipped output requires tail retention");
        Accept(pending);
        Skip(skipped);
        Accept(text);
        return true;
    }

    private string Decode(ReadOnlySpan<byte> bytes, bool flush)
    {
        var count = decoder.GetCharCount(bytes, flush);
        if (count == 0)
        {
            // Still advance the decoder: an incomplete character is held until the next chunk or flush.
            decoder.GetChars(bytes, [], flush);
            return "";
        }
        var chars = new char[count];
        var written = decoder.GetChars(bytes, chars, flush);
        return new string(chars, 0, written);
    }

    /// <summary>Counts omitted output; nothing stored before it can be in the window once the text after it arrives.</summary>
    private void Skip(ShellOutputSkip skipped)
    {
        if (skipped.Bytes == 0) return;
        totalBytes += skipped.Bytes;
        totalNewlines += skipped.Newlines;
        endsWithNewline = skipped.EndsWithNewline;
        chunks = new Queue<Chunk>();
        storedBytes = 0;
        storedNewlines = 0;
    }

    /// <summary>Flushes an incomplete trailing character as a replacement character; call when the stream ends.</summary>
    public void End() => Accept(Decode([], flush: true));

    private bool Accept(string text)
    {
        if (text.Length == 0) return false;
        var bytes = Truncation.Utf8ByteLength(text);
        var newlines = ToolOutput.CountNewlines(text);
        totalBytes += bytes;
        totalNewlines += newlines;
        endsWithNewline = text.EndsWith('\n');
        if (full) return true;
        chunks.Enqueue(new Chunk(text, bytes, newlines));
        storedBytes += bytes;
        storedNewlines += newlines;
        if (limits.Retain == ToolOutputRetention.Head)
        {
            // Nothing past a full window is ever needed.
            full = storedBytes > limits.MaxBytes || storedNewlines >= limits.MaxLines;
            return true;
        }
        // Drop leading chunks while the rest still holds more than a window: more than `maxBytes` bytes or `maxLines` newlines, plus one,
        // so the window's line start can still be found. Each chunk is dropped once.
        while (chunks.Count > 1)
        {
            var head = chunks.Peek();
            var bytesAfter = storedBytes - head.Bytes;
            var newlinesAfter = storedNewlines - head.Newlines;
            if (bytesAfter <= limits.MaxBytes + 1 && newlinesAfter <= limits.MaxLines + 1) break;
            chunks.Dequeue();
            storedBytes = bytesAfter;
            storedNewlines = newlinesAfter;
        }
        return true;
    }

    /// <summary>Retained, sanitized output and what the limits dropped from the whole stream.</summary>
    public BoundedOutput Snapshot()
    {
        var stored = chunks.Count == 1 ? chunks.Peek().Text : string.Concat(chunks.Select(chunk => chunk.Text));
        var kept = ToolOutput.BoundOutput(stored, limits);
        var storedLines = ToolOutput.Lines(storedNewlines, stored.Length == 0 || stored.EndsWith('\n'));
        var keptLines = storedLines - kept.DroppedLines;
        // Tail windows never reach back before this one, but finding a later window's first line needs what precedes it: keep the shortest
        // suffix longer than the window by a byte or a line, as Accept does.
        if (limits.Retain == ToolOutputRetention.Tail || chunks.Count > 1)
        {
            var text = limits.Retain == ToolOutputRetention.Tail ? ToolOutput.TailMargin(stored, limits) : stored;
            var bytes = limits.Retain == ToolOutputRetention.Tail ? Truncation.Utf8ByteLength(text) : storedBytes;
            chunks = new Queue<Chunk>();
            if (text.Length > 0) chunks.Enqueue(new Chunk(text, bytes, ToolOutput.CountNewlines(text)));
            storedBytes = bytes;
            storedNewlines = chunks.Count > 0 ? chunks.Peek().Newlines : 0;
        }
        return new BoundedOutput(
            ToolOutput.SanitizeOutput(kept.Text),
            totalBytes - kept.Bytes,
            ToolOutput.Lines(totalNewlines, endsWithNewline) - keptLines);
    }
}

/// <summary>
/// Adaptive progress commits (upstream <c>Progress</c>): the first change after an idle period commits at once; each commit then delays
/// the next by at least <c>minIntervalMs</c> and by its written size at 100 KiB/s. At most one commit is in flight; changes made meanwhile
/// coalesce into the next one.
/// </summary>
internal sealed class Progress(Func<Task<long>> write, Action<Exception> onError, double minIntervalMs, TimeProvider? timeProvider = null)
{
    private readonly Func<Task<long>> write = write;
    private readonly Action<Exception> onError = onError;
    private readonly double minIntervalMs = minIntervalMs;
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly Lock gate = new();
    private List<TaskCompletionSource> waiters = [];
    private ITimer? timer;
    private Task? inFlight;
    private double nextAt;
    private bool dirty;
    private bool stopped;

    /// <summary>Schedules a commit.</summary>
    public void Mark()
    {
        Flight? flight;
        lock (gate)
        {
            dirty = true;
            flight = ScheduleLocked();
        }
        flight?.Start();
    }

    /// <summary>Schedules a commit; the task settles with the commit that includes this change.</summary>
    public Task MarkAndWait()
    {
        // Settled inline and in order, as promise reactions run in resolution order; never under the lock.
        var waiter = new TaskCompletionSource();
        lock (gate) waiters.Add(waiter);
        Mark();
        return waiter.Task;
    }

    /// <summary>Stops committing and waits for the commit in flight; returns the waiters the final commit must settle.</summary>
    public async Task<List<TaskCompletionSource>> StopAsync()
    {
        Task? flying;
        lock (gate)
        {
            stopped = true;
            timer?.Dispose();
            timer = null;
            flying = inFlight;
        }
        if (flying is not null) await flying.ConfigureAwait(false);
        lock (gate)
        {
            var pending = waiters;
            waiters = [];
            return pending;
        }
    }

    private double Now() => time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Upstream <c>#schedule</c>; returns a flush to start once the lock is released.</summary>
    private Flight? ScheduleLocked()
    {
        if (stopped || timer is not null || inFlight is not null) return null;
        var wait = nextAt - Now();
        if (wait <= 0) return FlushLocked();
        timer = time.CreateTimer(_ => OnTimer(), null, TimeSpan.FromMilliseconds(wait), Timeout.InfiniteTimeSpan);
        return null;
    }

    private void OnTimer()
    {
        Flight? flight;
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
            flight = FlushLocked();
        }
        flight?.Start();
    }

    /// <summary>Upstream <c>#flush</c>: claims the change and its waiters; the returned flight runs the write.</summary>
    private Flight? FlushLocked()
    {
        if (stopped || !dirty) return null;
        dirty = false;
        var claimed = waiters;
        waiters = [];
        var flight = new Flight(this, claimed, Now());
        inFlight = flight.Done;
        return flight;
    }

    /// <summary>One write and its settlement.</summary>
    private sealed class Flight(Progress owner, List<TaskCompletionSource> waiters, double started)
    {
        private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Done => done.Task;

        public void Start()
        {
            Task<long> written;
            try
            {
                written = owner.write();
            }
            catch (Exception error)
            {
                written = Task.FromException<long>(error);
            }
            // Runs inline when the write completed synchronously, as upstream's microtask runs before any timer.
            written.ContinueWith(Settle, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void Settle(Task<long> written)
        {
            if (written.IsCompletedSuccessfully)
            {
                var bytes = written.GetAwaiter().GetResult();
                lock (owner.gate) owner.nextAt = started + Math.Max(owner.minIntervalMs, bytes * 1000 / ToolOutput.ProgressBytesPerSecond);
                foreach (var waiter in waiters) waiter.TrySetResult();
            }
            else
            {
                var error = written.Exception?.InnerException ?? new OperationCanceledException();
                lock (owner.gate) owner.nextAt = started + owner.minIntervalMs;
                foreach (var waiter in waiters) waiter.TrySetException(error);
                owner.onError(error);
            }
            Flight? next;
            lock (owner.gate)
            {
                owner.inFlight = null;
                next = owner.dirty ? owner.ScheduleLocked() : null;
            }
            done.TrySetResult();
            next?.Start();
        }
    }
}
