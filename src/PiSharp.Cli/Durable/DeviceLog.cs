// PiSharp-only (decision 0010, M6). `pisharp durable serve --log <file>`: the device's own output and its server's log messages, appended to
// a file with a UTC timestamp on each line and rotated by size (device.log, then device.log.1 to .3), so a service manager that keeps no
// journal (a Windows scheduled task, a launchd agent) still leaves bounded logs. Logging is best effort: a write that fails is dropped
// rather than stopping the device.
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PiSharp.Cli.Durable;

/// <summary>A size-rotated log file, written as a <see cref="TextWriter"/>.</summary>
internal sealed class DeviceLog : TextWriter
{
    /// <summary>The size at which the file is rotated.</summary>
    internal const long DefaultMaxBytes = 10L * 1024 * 1024;
    /// <summary>How many rotated files are kept.</summary>
    internal const int DefaultKeep = 3;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly Lock gate = new();
    private readonly string path;
    private readonly long maxBytes;
    private readonly int keep;
    private readonly TimeProvider time;
    private FileStream? stream;
    private bool lineStart = true;

    /// <exception cref="IOException">The file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be opened.</exception>
    internal DeviceLog(string path, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep, TimeProvider? time = null)
    {
        this.path = Path.GetFullPath(path);
        this.maxBytes = maxBytes;
        this.keep = keep;
        this.time = time ?? TimeProvider.System;
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        stream = Open();
    }

    /// <summary>Where a service definition has the device write its log.</summary>
    internal static string DefaultPath(string dataDirectory) => Path.Join(dataDirectory, "logs", "device.log");

    public override Encoding Encoding => Utf8;

    public override void Write(char value) => Write(value.ToString());

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void WriteLine(string? value) => Write(value + NewLine);

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (gate)
        {
            var text = new StringBuilder(value.Length + 32);
            foreach (var character in value)
            {
                if (lineStart && character is not ('\r' or '\n'))
                {
                    text.Append(time.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z' ", CultureInfo.InvariantCulture));
                    lineStart = false;
                }
                text.Append(character);
                if (character == '\n') lineStart = true;
            }
            var bytes = Utf8.GetBytes(text.ToString());
            try
            {
                if (stream is null || (stream.Length > 0 && stream.Length + bytes.Length > maxBytes)) Rotate();
                stream!.Write(bytes);
                stream.Flush();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Best effort: the device keeps running without this line.
            }
        }
    }

    public override void Flush()
    {
        lock (gate)
        {
            try { stream?.Flush(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { /* Best effort. */ }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            lock (gate)
            {
                stream?.Dispose();
                stream = null;
            }
        base.Dispose(disposing);
    }

    private FileStream Open() => new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Moves device.log to device.log.1 (and each older file one further, dropping the oldest), then starts a new file. When a move
    /// fails the current file is reopened, so logging goes on.</summary>
    private void Rotate()
    {
        stream?.Dispose();
        stream = null;
        try
        {
            for (var index = keep - 1; index >= 1; index--)
                if (File.Exists($"{path}.{index}")) File.Move($"{path}.{index}", $"{path}.{index + 1}", overwrite: true);
            if (keep > 0 && File.Exists(path)) File.Move(path, path + ".1", overwrite: true);
        }
        finally
        {
            stream = Open();
        }
    }
}

/// <summary>The server's log messages as lines of a <see cref="TextWriter"/> such as a <see cref="DeviceLog"/>.</summary>
internal sealed class DeviceLogProvider(TextWriter log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(log, categoryName);

    public void Dispose() { }

    private sealed class Logger(TextWriter log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            log.WriteLine($"[{logLevel}] {category}: {formatter(state, exception)}" + (exception is null ? "" : Environment.NewLine + exception));
        }
    }
}
