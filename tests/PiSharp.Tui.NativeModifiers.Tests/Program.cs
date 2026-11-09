using System.Reflection;
using PiSharp.Tui.Pi;

// Offline contract tests against the actual built POSIX implementation. Reflection only constructs
// its internal injection seam; ProcessTerminal itself and IRawTerminalIo dispatch are production code.
var cases = new (string Name, Action Run)[]
{
    ("macOS session flags mask only physical Shift", Flags),
    ("non-macOS never invokes CoreGraphics", NonMac),
    ("unavailable native library/symbol/image falls back", Unavailable),
    ("unexpected cancellation and errors propagate", Cancellation),
    ("actual host pipeline preserves Enter and Alt distinctions", InputPipeline),
    ("stop cancels queued input without querying modifiers", StopCancelsInput),
};
var failed = 0;
foreach (var test in cases)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {test.Name}: {error}"); }
}
Console.WriteLine($"{cases.Length - failed}/{cases.Length} passed; offline host={Environment.OSVersion}; physical macOS qualification NOT performed.");
return failed == 0 ? 0 : 1;

static IRawTerminalIo Native(bool isMacOS, Func<int, ulong> flags)
{
    var type = typeof(ProcessTerminal).Assembly.GetType("PiSharp.Tui.Pi.PosixRawTerminalIo", throwOnError: true)!;
    var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null, types: [typeof(bool), typeof(Func<int, ulong>)], modifiers: null)
        ?? throw new MissingMethodException($"{type.FullName} in {type.Assembly.Location}: expected (bool, Func<int, ulong>); " +
            "found " + string.Join("; ", type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(candidate => candidate.ToString())));
    return (IRawTerminalIo)constructor.Invoke([isMacOS, flags]);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}");
}

static void Flags()
{
    var calls = 0;
    ulong flags = 0;
    var native = Native(true, state => { Equal(0, state); calls++; return flags; });
    foreach (var sample in new[] { 0UL, 1UL << 16, 1UL << 18, 1UL << 19, 1UL << 20, 1UL << 63, 1UL << 17, ulong.MaxValue, 0UL })
    {
        flags = sample;
        Equal((sample & (1UL << 17)) != 0, native.IsShiftPressed());
    }
    Equal(9, calls); // Each key observes current flags; a prior press/release is never cached.
}

static void NonMac()
{
    Equal(false, Native(false, _ => throw new InvalidOperationException("CoreGraphics called on non-macOS")).IsShiftPressed());
    if (!OperatingSystem.IsMacOS())
    {
        var type = typeof(ProcessTerminal).Assembly.GetType("PiSharp.Tui.Pi.PosixRawTerminalIo", true)!;
        Equal(false, ((IRawTerminalIo)Activator.CreateInstance(type)!).IsShiftPressed());
    }
}

static void Unavailable()
{
    foreach (var failure in new Exception[] { new DllNotFoundException(), new EntryPointNotFoundException(), new BadImageFormatException() })
    {
        var calls = 0;
        var native = Native(true, _ => { calls++; if (calls == 1) throw failure; return 1UL << 17; });
        Equal(false, native.IsShiftPressed());
        Equal(true, native.IsShiftPressed());
        Equal(2, calls);
    }
}

static void Cancellation()
{
    using var source = new CancellationTokenSource(); source.Cancel();
    var cancellation = new OperationCanceledException(source.Token);
    foreach (var failure in new Exception[] { cancellation, new InvalidOperationException("unexpected") })
    {
        try { Native(true, _ => throw failure).IsShiftPressed(); throw new Exception("Failure swallowed"); }
        catch (Exception error) when (ReferenceEquals(error, failure)) { }
    }
}

static void InputPipeline()
{
    foreach (var termProgram in new[] { "Apple_Terminal", "other-terminal" })
    foreach (var wire in new[] { "\r", "\u001b\r", "x", "\u001b[13;2u", "\u001b[200~\r\u001b[201~" })
    foreach (var shift in new[] { false, true })
    foreach (var available in new[] { false, true })
    {
        var calls = 0;
        var native = Native(true, _ => { calls++; if (!available) throw new DllNotFoundException(); return shift ? 1UL << 17 : 0; });
        var io = new FakeIo(native);
        using var loop = UiLoop.CreateManual();
        var received = new List<string>();
        var terminal = new ProcessTerminal(loop, io, name => name == "TERM_PROGRAM" ? termProgram : null);
        terminal.Start(received.Add, () => { });
        try
        {
            io.Send(wire); loop.RunPending();
            var candidate = wire is "\r" or "\u001b\r";
            var nativeEnabled = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() && termProgram == "Apple_Terminal";
            Equal(1, received.Count);
            Equal(candidate && nativeEnabled && available && shift ? "\u001b[13;2u" : wire, received[0]);
            Equal(candidate && nativeEnabled ? 1 : 0, calls);
        }
        finally { terminal.Stop(); }
    }
}

static void StopCancelsInput()
{
    var io = new FakeIo(Native(true, _ => throw new Exception("Stopped input queried flags")));
    using var loop = UiLoop.CreateManual();
    var received = new List<string>();
    var terminal = new ProcessTerminal(loop, io, name => name == "TERM_PROGRAM" ? "Apple_Terminal" : null);
    terminal.Start(received.Add, () => { });
    io.Send("\u001b\r"); terminal.Stop(); loop.RunPending();
    Equal(0, received.Count);
}

sealed class FakeIo(IRawTerminalIo native) : IRawTerminalIo
{
    private Action<string>? onText;
    public void Send(string text) => onText?.Invoke(text);
    public bool IsShiftPressed() => native.IsShiftPressed();
    public void EnterRawMode() { }
    public void ExitRawMode() { }
    public void StartReading(Action<string> callback) => onText = callback;
    public void StopReading() => onText = null;
    public IDisposable ObserveActivity(Action onActivity) => new Empty();
    public void Write(string data) { }
    public (int Columns, int Rows) Size() => (80, 24);
    public void WatchResize(Action onResize) { }
    public void StopWatchingResize() { }
    private sealed class Empty : IDisposable { public void Dispose() { } }
}
