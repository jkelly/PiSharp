// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/src/terminal.ts, packages/tui/src/program-status.ts,
// packages/tui/src/native-modifiers.ts.
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PiSharp.Tui.Pi;

public enum ProgramState { Idle, Working, Blocked, Done, Error, Clear }
public enum ProgramBlockedKind { Permission, Question, Auth }
/// <summary>An OSC 7501 program status report.</summary>
public sealed record ProgramStatus(ProgramState State, string? App = null, ProgramBlockedKind? Kind = null, string? Message = null)
{
    public const string Query = "\u001b]7501;?\u001b\\";
    public static bool IsReply(string sequence) => Regex.IsMatch(sequence, @"^\x1b\]7501;\?[^\x07\x1b]*(?:\x07|\x1b\\)$");
    public string Format()
    {
        var pairs = new List<string> { "state=" + State.ToString().ToLowerInvariant() };
        if (App is not null && Regex.IsMatch(App, "^[A-Za-z0-9_.+-]{1,32}$")) pairs.Add("app=" + App);
        if (State == ProgramState.Blocked && Kind is { } kind) pairs.Add("kind=" + kind.ToString().ToLowerInvariant());
        var message = TruncateUtf8(Regex.Replace(Message ?? "", "[\u0000-\u001f\u007f-\u009f]+", " ").Trim(), 2048);
        if (message.Length > 0) pairs.Add("msg=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(message)));
        return "\u001b]7501;" + string.Join(':', pairs) + "\u001b\\";
    }
    private static string TruncateUtf8(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        var bytes = 0; var end = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > maxBytes) break;
            bytes += rune.Utf8SequenceLength; end += rune.Utf16SequenceLength;
        }
        return text[..end];
    }
}

/// <summary>The minimal terminal Pi's TUI drives.</summary>
public interface ITerminal
{
    void Start(Action<string> onInput, Action onResize);
    void Stop();
    Task DrainInputAsync(int maxMs = 1000, int idleMs = 50);
    void Write(string data);
    int Columns { get; }
    int Rows { get; }
    bool KittyProtocolActive { get; }
    void MoveBy(int lines);
    void HideCursor();
    void ShowCursor();
    void ClearLine();
    void ClearFromCursor();
    void ClearScreen();
    void SetTitle(string title);
    void SetProgress(bool active);
    void SetProgramStatus(ProgramStatus status);
}

/// <summary>Raw terminal I/O over the process's standard streams: Windows console handles with VT input and output, or a
/// POSIX tty in raw mode. All callbacks are posted to the UI loop.</summary>
public sealed partial class ProcessTerminal : ITerminal
{
    private const string ProgressActive = "\u001b]9;4;3\u0007", ProgressClear = "\u001b]9;4;0\u0007";
    private const string KittyQuery = "\u001b[>7u\u001b[?u", DeviceAttributesQuery = "\u001b[c";
    private readonly UiLoop loop;
    private readonly IRawTerminalIo io;
    private readonly Func<string, string?> env;
    private Action<string>? inputHandler;
    private Action? resizeHandler;
    private bool kittyActive, modifyOtherKeysActive, keyboardProtocolPushed;
    private int pendingDeviceAttributes;
    private string negotiationBuffer = "";
    private IDisposable? negotiationFlushTimer, progressInterval;
    private StdinBuffer? stdinBuffer;
    private ProgramStatus? programStatus;
    private bool programStatusSupported, programStatusQueryPending;
    private readonly string writeLogPath;
    private int lastColumns, lastRows;
    private IDisposable? resizePoll;

    public ProcessTerminal(UiLoop loop, IRawTerminalIo? io = null, Func<string, string?>? environment = null)
    {
        this.loop = loop; env = environment ?? Environment.GetEnvironmentVariable;
        this.io = io ?? (OperatingSystem.IsWindows() ? new WindowsRawTerminalIo() : new PosixRawTerminalIo());
        writeLogPath = ResolveWriteLog(env("PI_TUI_WRITE_LOG") ?? "");
    }

    private static string ResolveWriteLog(string value)
    {
        if (value.Length == 0) return "";
        try
        {
            if (Directory.Exists(value))
                return Path.Join(value, $"tui-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}-{Environment.ProcessId}.log");
        }
        catch { }
        return value;
    }

    public bool KittyProtocolActive => kittyActive;
    public bool ModifyOtherKeysActive => modifyOtherKeysActive;
    public int Columns { get { var size = io.Size(); return size.Columns > 0 ? size.Columns : int.TryParse(env("COLUMNS"), out var c) && c > 0 ? c : 80; } }
    public int Rows { get { var size = io.Size(); return size.Rows > 0 ? size.Rows : int.TryParse(env("LINES"), out var r) && r > 0 ? r : 24; } }

    public static double ResolveEscapeTimeoutMs(Func<string, string?> env)
    {
        if (double.TryParse(env("PI_TUI_ESC_TIMEOUT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var configured) && configured > 0) return configured;
        return !string.IsNullOrEmpty(env("SSH_CONNECTION")) || !string.IsNullOrEmpty(env("SSH_TTY")) ? 100 : 10;
    }

    public void Start(Action<string> onInput, Action onResize)
    {
        inputHandler = onInput; resizeHandler = onResize;
        io.EnterRawMode();
        Write("\u001b[?2004h");
        var size = io.Size(); lastColumns = size.Columns; lastRows = size.Rows;
        io.WatchResize(() => loop.Post(CheckResize));
        // Windows has no SIGWINCH for console hosts; poll the window size as libuv does.
        if (OperatingSystem.IsWindows()) resizePoll = loop.SetInterval(CheckResize, 100);
        QueryAndEnableKittyProtocol();
    }

    private void CheckResize()
    {
        var size = io.Size();
        if (size.Columns == lastColumns && size.Rows == lastRows) return;
        lastColumns = size.Columns; lastRows = size.Rows;
        resizeHandler?.Invoke();
    }

    private void SetupStdinBuffer()
    {
        stdinBuffer = new StdinBuffer(loop, escapeTimeoutMs: ResolveEscapeTimeoutMs(env));
        stdinBuffer.Data += sequence =>
        {
            if (ProgramStatus.IsReply(sequence))
            {
                if (programStatusQueryPending) { programStatusQueryPending = false; programStatusSupported = true; WriteProgramStatus(); }
                return;
            }
            var negotiation = ReadNegotiation(sequence);
            if (negotiation.Pending) { ScheduleNegotiationFlush(); return; }
            if (negotiation.Parsed is { } parsed && HandleNegotiation(parsed)) return;
            ForwardInput(negotiation.Sequence ?? sequence);
        };
        stdinBuffer.Paste += content => inputHandler?.Invoke("\u001b[200~" + content + "\u001b[201~");
    }

    private void QueryAndEnableKittyProtocol()
    {
        SetupStdinBuffer();
        io.StartReading(text => loop.Post(() => stdinBuffer?.Process(text)));
        keyboardProtocolPushed = true;
        pendingDeviceAttributes++;
        ClearNegotiationBuffer();
        var overrideValue = env("PI_PROGRAM_STATUS");
        programStatusSupported = overrideValue == "1";
        programStatusQueryPending = overrideValue is not ("1" or "0");
        Write(KittyQuery + (programStatusQueryPending ? ProgramStatus.Query : "") + DeviceAttributesQuery);
        WriteProgramStatus();
    }

    private sealed record Negotiation(bool KittyFlags, int Flags);
    [GeneratedRegex(@"^\x1b\[\?(\d+)u$")] private static partial Regex KittyFlagsReply();
    [GeneratedRegex(@"^\x1b\[\?[\d;]*c$")] private static partial Regex DeviceAttributesReply();
    [GeneratedRegex(@"^\x1b\[\?[\d;]*$")] private static partial Regex NegotiationPrefix();
    private static Negotiation? ParseNegotiation(string sequence)
    {
        var match = KittyFlagsReply().Match(sequence);
        if (match.Success) return new(true, int.TryParse(match.Groups[1].Value, out var flags) ? flags : 0);
        return DeviceAttributesReply().IsMatch(sequence) ? new(false, 0) : null;
    }
    private static bool IsNegotiationPrefix(string sequence) => sequence == "\u001b[" || NegotiationPrefix().IsMatch(sequence);

    private bool HandleNegotiation(Negotiation negotiation)
    {
        ClearNegotiationBuffer();
        if (!negotiation.KittyFlags)
        {
            if (pendingDeviceAttributes == 0) return false;
            pendingDeviceAttributes--;
            if (pendingDeviceAttributes == 0) programStatusQueryPending = false;
        }
        if (negotiation.KittyFlags)
        {
            if (negotiation.Flags != 0)
            {
                DisableModifyOtherKeys();
                if (!kittyActive) { kittyActive = true; Keys.SetKittyProtocolActive(true); }
            }
            else EnableModifyOtherKeys();
            return true;
        }
        if (!kittyActive) EnableModifyOtherKeys();
        return true;
    }

    private (bool Pending, Negotiation? Parsed, string? Sequence) ReadNegotiation(string sequence)
    {
        if (negotiationBuffer.Length > 0)
        {
            var buffered = negotiationBuffer + sequence;
            if (ParseNegotiation(buffered) is { } parsed) { ClearNegotiationBuffer(); return (false, parsed, buffered); }
            if (IsNegotiationPrefix(buffered)) { SetNegotiationBuffer(buffered); return (true, null, null); }
            FlushNegotiationBufferAsInput();
        }
        if (ParseNegotiation(sequence) is { } direct) return (false, direct, sequence);
        if (IsNegotiationPrefix(sequence)) { SetNegotiationBuffer(sequence); return (true, null, null); }
        return (false, null, null);
    }
    private void SetNegotiationBuffer(string sequence) { negotiationFlushTimer?.Dispose(); negotiationFlushTimer = null; negotiationBuffer = sequence; }
    private void ClearNegotiationBuffer() { negotiationFlushTimer?.Dispose(); negotiationFlushTimer = null; negotiationBuffer = ""; }
    private void FlushNegotiationBufferAsInput()
    {
        if (negotiationBuffer.Length == 0) return;
        var sequence = negotiationBuffer; ClearNegotiationBuffer(); ForwardInput(sequence);
    }
    private void ScheduleNegotiationFlush()
    {
        if (negotiationBuffer.Length == 0 || negotiationFlushTimer is not null) return;
        negotiationFlushTimer = loop.SetTimeout(() => { negotiationFlushTimer = null; FlushNegotiationBufferAsInput(); }, 150);
    }

    private void ForwardInput(string sequence)
    {
        if (inputHandler is null) return;
        var detect = sequence == "\r" && (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() && env("TERM_PROGRAM") == "Apple_Terminal");
        inputHandler(detect && io.IsShiftPressed() ? "\u001b[13;2u" : sequence);
    }

    private void EnableModifyOtherKeys()
    {
        if (kittyActive || modifyOtherKeysActive) return;
        Write("\u001b[>4;2m"); modifyOtherKeysActive = true;
    }
    private void DisableModifyOtherKeys()
    {
        if (!modifyOtherKeysActive) return;
        Write("\u001b[>4;0m"); modifyOtherKeysActive = false;
    }

    public async Task DrainInputAsync(int maxMs = 1000, int idleMs = 50)
    {
        var disableKitty = keyboardProtocolPushed || kittyActive;
        ClearNegotiationBuffer();
        if (disableKitty) { Write("\u001b[<u"); keyboardProtocolPushed = false; kittyActive = false; Keys.SetKittyProtocolActive(false); }
        DisableModifyOtherKeys();
        var previous = inputHandler; inputHandler = null;
        var lastData = Environment.TickCount64; var end = lastData + maxMs;
        var watcher = io.ObserveActivity(() => Interlocked.Exchange(ref lastData, Environment.TickCount64));
        try
        {
            while (true)
            {
                var now = Environment.TickCount64; var left = end - now;
                if (left <= 0 || now - Interlocked.Read(ref lastData) >= idleMs) break;
                await Task.Delay((int)Math.Min(idleMs, left)).ConfigureAwait(true);
            }
        }
        finally { watcher.Dispose(); inputHandler = previous; }
    }

    public void Stop()
    {
        if (progressInterval is not null) { progressInterval.Dispose(); progressInterval = null; Write(ProgressClear); }
        if (programStatusSupported && programStatus is not null) Write(new ProgramStatus(ProgramState.Clear).Format());
        programStatusSupported = false; programStatusQueryPending = false;
        Write("\u001b[?2004l");
        var disableKitty = keyboardProtocolPushed || kittyActive;
        ClearNegotiationBuffer();
        if (disableKitty) { Write("\u001b[<u"); keyboardProtocolPushed = false; kittyActive = false; Keys.SetKittyProtocolActive(false); }
        DisableModifyOtherKeys();
        stdinBuffer?.Destroy(); stdinBuffer = null;
        io.StopReading();
        inputHandler = null;
        resizePoll?.Dispose(); resizePoll = null;
        io.StopWatchingResize(); resizeHandler = null;
        io.ExitRawMode();
    }

    public void Write(string data)
    {
        io.Write(data);
        if (writeLogPath.Length > 0) try { File.AppendAllText(writeLogPath, data, Encoding.UTF8); } catch { }
    }

    public void MoveBy(int lines) { if (lines > 0) Write($"\u001b[{lines}B"); else if (lines < 0) Write($"\u001b[{-lines}A"); }
    public void HideCursor() => Write("\u001b[?25l");
    public void ShowCursor() => Write("\u001b[?25h");
    public void ClearLine() => Write("\u001b[K");
    public void ClearFromCursor() => Write("\u001b[J");
    public void ClearScreen() => Write("\u001b[2J\u001b[H");
    public void SetTitle(string title) => Write("\u001b]0;" + title + "\u0007");
    public void SetProgramStatus(ProgramStatus status)
    {
        programStatus = status.State == ProgramState.Clear ? null : status;
        if (programStatusSupported) Write(status.Format());
    }
    private void WriteProgramStatus() { if (programStatusSupported && programStatus is not null) Write(programStatus.Format()); }
    public void SetProgress(bool active)
    {
        if (active)
        {
            Write(ProgressActive);
            progressInterval ??= loop.SetInterval(() => Write(ProgressActive), 1000);
        }
        else { progressInterval?.Dispose(); progressInterval = null; Write(ProgressClear); }
    }
}

/// <summary>Platform raw I/O under <see cref="ProcessTerminal"/>.</summary>
public interface IRawTerminalIo
{
    void EnterRawMode();
    void ExitRawMode();
    void StartReading(Action<string> onText);
    void StopReading();
    IDisposable ObserveActivity(Action onActivity);
    void Write(string data);
    (int Columns, int Rows) Size();
    void WatchResize(Action onResize);
    void StopWatchingResize();
    bool IsShiftPressed();
}

