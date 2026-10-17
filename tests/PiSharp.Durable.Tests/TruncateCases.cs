using System.Text;
using PiSharp.Durable;

// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/test/env-truncate.test.ts. The "runtime without Buffer" case ran the
// JavaScript fallback byte counter in a child process; Truncation.Utf8ByteLength is that counter, checked against .NET's UTF-8 encoder
// (which, like TextEncoder, writes U+FFFD for a lone surrogate).
internal static partial class Program
{
    private static IEnumerable<(string, Func<Task>)> TruncateCases() =>
    [
        Case("truncate.reports-utf-8-byte-counts-in-truncation-results", () =>
        {
            var content = "aé\U0001F642\nb";
            var result = Truncation.TruncateHead(content, new(MaxLines: 10, MaxBytes: 100));
            Check(!result.Truncated, "not truncated");
            Equal((long)Encoding.UTF8.GetByteCount(content), result.TotalBytes, "total bytes");
            Equal((long)Encoding.UTF8.GetByteCount(content), result.OutputBytes, "output bytes");
            Equal(9L, result.TotalBytes, "nine bytes");
        }),
        Case("truncate.counts-utf-8-bytes-and-truncates-correctly-in-a-runtime-without-buffer", () =>
        {
            string[] inputs = ["", "ascii", "é", "中", "\U0001F642", "\ud83d", "\ude42", "a🙂b", "߿ࠀ￿"];
            foreach (var input in inputs) Equal((long)Encoding.UTF8.GetByteCount(input), Truncation.Utf8ByteLength(input), "bytes of " + input);
            var head = Truncation.TruncateHead("aé\U0001F642\nb", new(MaxLines: 10, MaxBytes: 7));
            Equal("aé\U0001F642", head.Content, "content");
            Equal(7L, head.OutputBytes, "output bytes");
            Equal<TruncationLimit?>(TruncationLimit.Bytes, head.TruncatedBy, "truncated by");
        }),
        Case("truncate.does-not-count-a-trailing-newline-as-an-extra-line", () =>
        {
            var head = Truncation.TruncateHead(string.Join("\n", Enumerable.Repeat("line", 3)) + "\n", new(MaxLines: 3, MaxBytes: 100));
            Check(!head.Truncated, "not truncated");
            Equal(3, head.TotalLines, "total lines");
            Equal(3, head.OutputLines, "output lines");
        }),
        Case("truncate.truncates-head-by-line-limits", () =>
        {
            var head = Truncation.TruncateHead("one\ntwo\nthree\nfour", new(MaxLines: 2, MaxBytes: 100));
            Equal("one\ntwo", head.Content, "content");
            Check(head.Truncated, "truncated");
            Equal<TruncationLimit?>(TruncationLimit.Lines, head.TruncatedBy, "truncated by");
            Equal(4, head.TotalLines, "total lines");
            Equal(2, head.OutputLines, "output lines");
        }),
        Case("truncate.reports-bytes-when-only-a-trailing-newline-exceeds-limits-at-the-line-cap", () =>
        {
            var head = Truncation.TruncateHead("hello\nworld\n", new(MaxLines: 2, MaxBytes: 11));
            Equal("hello\nworld", head.Content, "content");
            Check(head.Truncated, "truncated");
            Equal<TruncationLimit?>(TruncationLimit.Bytes, head.TruncatedBy, "truncated by");
            Equal(2, head.TotalLines, "total lines");
            Equal(2, head.OutputLines, "output lines");
        }),
        Case("truncate.truncates-head-on-utf-8-byte-limits-without-partial-lines", () =>
        {
            var result = Truncation.TruncateHead("éé\nabc", new(MaxLines: 10, MaxBytes: 4));
            Equal("éé", result.Content, "content");
            Check(result.Truncated, "truncated");
            Equal<TruncationLimit?>(TruncationLimit.Bytes, result.TruncatedBy, "truncated by");
            Equal(4L, result.OutputBytes, "output bytes");
            Check(!result.FirstLineExceedsLimit, "first line fits");
        }),
        Case("truncate.reports-head-truncation-when-the-first-line-exceeds-the-byte-limit", () =>
        {
            var result = Truncation.TruncateHead("éé\nabc", new(MaxLines: 10, MaxBytes: 3));
            Equal("", result.Content, "content");
            Check(result.Truncated, "truncated");
            Equal<TruncationLimit?>(TruncationLimit.Bytes, result.TruncatedBy, "truncated by");
            Check(result.FirstLineExceedsLimit, "first line exceeds the limit");
        }),
        Case("truncate.formats-sizes", () =>
        {
            Equal("1023B", Truncation.FormatSize(1023), "bytes");
            Equal("1.5KB", Truncation.FormatSize(1536), "kilobytes");
            Equal("3.0MB", Truncation.FormatSize(3 * 1024 * 1024), "megabytes");
        }),
    ];
}
