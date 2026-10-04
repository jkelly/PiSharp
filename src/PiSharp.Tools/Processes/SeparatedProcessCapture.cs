using System.Text;

namespace PiSharp.Tools.Processes;

/// <summary>Retains the original joined process receipt and each original pipe's independently bounded bytes.</summary>
public sealed record SeparatedProcessRunResult(ProcessRunResult Process,
    ProcessStructuredOutput StandardOutput, ProcessStructuredOutput StandardError);

public interface ISeparatedProcessRunner : IProcessRunner
{
    ValueTask<SeparatedProcessRunResult> RunSeparatedAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

// Only the corresponding original pipe pump appends to each buffer; snapshots occur after both pumps join.
public sealed class SeparatedProcessCapture : IDisposable
{
    private readonly int _maximumBytes;
    private readonly MemoryStream _stdout = new();
    private readonly MemoryStream _stderr = new();
    private bool _stdoutTruncated, _stderrTruncated;
    public SeparatedProcessCapture(int maximumBytes)
    {
        if (maximumBytes is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }
    public void Append(bool standardError, ReadOnlySpan<byte> bytes)
    {
        var buffer = standardError ? _stderr : _stdout;
        var count = Math.Min(bytes.Length, _maximumBytes - (int)buffer.Length);
        buffer.Write(bytes[..count]);
        if (count != bytes.Length) { if (standardError) _stderrTruncated = true; else _stdoutTruncated = true; }
    }
    public SeparatedProcessRunResult Result(ProcessRunResult original) => new(original,
        new(Encoding.UTF8.GetString(_stdout.GetBuffer().AsSpan(0, (int)_stdout.Length)), _stdoutTruncated),
        new(Encoding.UTF8.GetString(_stderr.GetBuffer().AsSpan(0, (int)_stderr.Length)), _stderrTruncated));
    public void Dispose() { _stdout.Dispose(); _stderr.Dispose(); }
}
