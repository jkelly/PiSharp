using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PiSharp.Tui;

internal static class WindowsConPtyFixture
{
    public const string WorkerSwitch = "--windows-conpty-terminal-worker";
    private const string Input = "q\u4e2d\ud83d\ude42\u0003";
    private const string LinkOutput = "PTY-LINK:\u4e2d\ud83d\ude42:END";
    private const char UnreadMarker = '\u25a1';
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);
    private static readonly List<string> workerReceipts = new();
    public static string PlatformEvidence { get; private set; } = "not-exercised";
    public static string[] WorkerReceipts => workerReceipts.ToArray();

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string harnessDll)
    {
        foreach (var scenario in new[] { "unicode", "read-cancel", "dispose-read", "body-exception", "borrowed", "write-cancel" })
        {
            var selected = scenario;
            yield return ("terminal-conpty.actual-hidden-console-" + selected, () => Run(dotnetHost, harnessDll, selected));
        }
    }

    // Root must invoke this before ordinary test/report argument parsing. No production CLI switch is added.
    public static async Task<int?> TryRunWorkerAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != WorkerSwitch) return null;
        if (args.Length != 3 || !OperatingSystem.IsWindows()) return 2;
        await using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(Deadline);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        var phase = "connected-before-native";
        SafeFileHandle? consoleInput = null, consoleOutput = null;
        StandardAliases? standardAliases = null; ConsoleObservation[]? standardProbe = null;
        object? startupObservation = null;
        ConsoleObservation[]? beforeSentinel = null, afterSentinel = null;
        ConsoleObservation[]? sentinelCalls = null;
        var readObservations = new List<ReadObservation>();
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { stage = "probe", scenario = args[2], phase, processId = Environment.ProcessId }));
            standardAliases = ReadStandardAliases(); standardProbe = ProbeConsole();
            phase = "before-startup-observation";
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { stage = "probe", scenario = args[2], phase }));
            // GetStartupInfo returns borrowed pointers. A blittable view must never marshal/free them as output strings.
            Native.GetStartupInfoW(out var startup);
            startupObservation = new { flags = startup.Flags, showWindow = startup.ShowWindow,
                input = startup.StandardInput.ToInt64(), output = startup.StandardOutput.ToInt64(), error = startup.StandardError.ToInt64() };
            phase = "explicit-console-handles";
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { stage = "probe", scenario = args[2], phase, startupObservation }));
            // Sentinel state belongs only to this hidden test console.
            // ConPTY attachment and standard aliases are separate: redirected standard handles can still be pipes.
            // Open the attached private console explicitly without changing any process standard handle.
            consoleInput = OpenConsole("CONIN$"); consoleOutput = OpenConsole("CONOUT$");
            using var stdin = new SafeFileHandle(consoleInput.DangerousGetHandle(), false);
            using var stdout = new SafeFileHandle(consoleOutput.DangerousGetHandle(), false);
            beforeSentinel = ProbeConsole(consoleInput, consoleOutput);
            Check(Native.GetFileType(consoleInput) == 2 && Native.GetFileType(consoleOutput) == 2,
                "Explicit console handles are not actual character devices.");
            phase = "console-sentinel";
            var cursor = new CursorInfo { Size = 33, Visible = true };
            var cursorSet = Native.SetConsoleCursorInfo(stdout, ref cursor); var cursorError = cursorSet ? 0 : Marshal.GetLastPInvokeError();
            var positionSet = Native.SetConsoleCursorPosition(stdout, new(3, 2)); var positionError = positionSet ? 0 : Marshal.GetLastPInvokeError();
            sentinelCalls = [new("SetConsoleCursorInfo", cursorSet, cursorError, new { size = 33, visible = true }),
                new("SetConsoleCursorPosition", positionSet, positionError, new { column = 3, row = 2 })];
            afterSentinel = ProbeConsole(consoleInput, consoleOutput);
            Check(cursorSet && positionSet, "Fixture cursor sentinel failed.");
            var original = ReadState(consoleInput, consoleOutput);
            Check(original.CursorSize == 33 && original.CursorVisible && original.CursorColumn == 3 && original.CursorRow == 2,
                "Fixture cursor sentinel was not observed in actual console state.");
            phase = "terminal-acquisition";
            if (standardAliases.InputType == 3 || standardAliases.OutputType == 3)
                await Expect<TerminalException>(() => WindowsConsoleTerminal.OpenAsync().AsTask(), error => error.Failure == TerminalFailure.NotConsole);
            var terminal = await WindowsConsoleTerminal.OpenBorrowedHandlesAsync(stdin, stdout, new(MaximumWriteCharacters: 1_048_576));
            TerminalLeaseSnapshot snapshot;
            try
            {
                Equal(original, terminal.Snapshot.Original); var acquired = ReadState(consoleInput, consoleOutput); Equal(acquired, terminal.Snapshot.Acquired);
                Check((acquired.InputMode & 0x0047u) == 0 && (acquired.InputMode & 0x0280u) == 0x0280u, "Raw VT input was not acquired.");
                Check((acquired.OutputMode & 0x0005u) == 0x0005u && !acquired.CursorVisible, "VT output/cursor was not acquired.");
                Equal(original.InputCodePage, acquired.InputCodePage); Equal(original.OutputCodePage, acquired.OutputCodePage);
                await Expect<TerminalException>(() => WindowsConsoleTerminal.OpenAsync().AsTask(), error => error.Failure == TerminalFailure.AlreadyOwned);
                await writer.WriteLineAsync(JsonSerializer.Serialize(new { stage = "ready", scenario = args[2], original, acquired,
                    standardProbe, startupObservation, beforeSentinel, sentinelCalls, afterSentinel }));
                phase = "private-conpty-link";
                // Every case proves these console handles belong to the parent's exact private ConPTY.
                // Fully consume this prologue before testing any empty-read cancellation without a wake.
                var buffer = new char[2]; var received = new StringBuilder();
                while (received.Length < Input.Length)
                {
                    var count = await terminal.ReadAsync(buffer, timeout.Token);
                    ObserveRead(readObservations, "link-prologue", count, buffer, null, timeout.Token, terminal);
                    Check(count > 0, "Console unexpectedly returned EOF."); received.Append(buffer, 0, count);
                }
                Equal(Input, received.ToString()); await terminal.WriteAsync(LinkOutput.AsMemory(), timeout.Token);
                phase = "scenario-" + args[2];
                switch (args[2])
                {
                    case "unicode":
                    {
                        await terminal.WriteAsync((new string('x', 4095) + "\ud83d\ude42:OUT\u4e2d\ud83d\ude42:END").AsMemory(), timeout.Token);
                        break;
                    }
                    case "read-cancel":
                        for (var attempt = 0; attempt < 24; attempt++)
                        {
                            using var cancellation = new CancellationTokenSource(); var destination = UnreadBuffer(); var pending = terminal.ReadAsync(destination, cancellation.Token).AsTask();
                            if (attempt < 4)
                            {
                                await Task.Delay(30, timeout.Token); Check(!pending.IsCompleted, "Empty real console read did not block.");
                                await Expect<InvalidOperationException>(() => terminal.ReadAsync(new char[16]).AsTask());
                            }
                            cancellation.Cancel();
                            // Caller cancellation keeps its original token even when closing is also requested.
                            if (attempt == 23) await terminal.DisposeAsync();
                            await ExpectRead<OperationCanceledException>(pending, destination, readObservations, "cancel-" + attempt, cancellation.Token, terminal,
                                error => error.CancellationToken == cancellation.Token);
                            Check(terminal.Snapshot.ActiveReads == 0, "Canceled native read remains active.");
                            Equal(terminal.Snapshot.ReadWorkersStarted, terminal.Snapshot.ReadWorkersSettled);
                        }
                        break;
                    case "dispose-read":
                    {
                        var destination = UnreadBuffer(); var pending = terminal.ReadAsync(destination).AsTask(); await Task.Delay(30, timeout.Token);
                        Check(!pending.IsCompleted, "Empty real console read did not block.");
                        var first = terminal.DisposeAsync().AsTask(); var second = terminal.DisposeAsync().AsTask(); var third = terminal.DisposeAsync().AsTask();
                        await Task.WhenAll(first, second, third);
                        await ExpectRead<ObjectDisposedException>(pending, destination, readObservations, "dispose-read", CancellationToken.None, terminal); break;
                    }
                    case "body-exception":
                        try { await ThrowWithinLease(terminal); } catch (FixtureBodyException) { }
                        break;
                    case "borrowed":
                    {
                        // Dispose caller wrappers immediately. The owned duplicates must remain usable.
                        stdin.Dispose(); stdout.Dispose();
                        await terminal.WriteAsync("BORROWED:\u4e2d\ud83d\ude42".AsMemory(), timeout.Token);
                        using var cancellation = new CancellationTokenSource(); var destination = UnreadBuffer(); var pending = terminal.ReadAsync(destination, cancellation.Token).AsTask();
                        await Task.Delay(30, timeout.Token); cancellation.Cancel();
                        await ExpectRead<OperationCanceledException>(pending, destination, readObservations, "borrowed-read", cancellation.Token, terminal,
                            error => error.CancellationToken == cancellation.Token);
                        break;
                    }
                    case "write-cancel":
                    {
                        using var cancellation = new CancellationTokenSource();
                        var pending = terminal.WriteAsync(new string('w', 1_048_576).AsMemory(), cancellation.Token).AsTask();
                        cancellation.Cancel();
                        try { await pending; } catch (OperationCanceledException error) { Equal(cancellation.Token, error.CancellationToken); }
                        Check(terminal.Snapshot.ActiveWrites == 0, "Canceled native write remains active.");
                        await Expect<OperationCanceledException>(() => terminal.WriteAsync("PRE-CANCEL".AsMemory(), cancellation.Token).AsTask());
                        await terminal.WriteAsync("WRITE-REUSED".AsMemory(), timeout.Token); break;
                    }
                    default: throw new Exception("Unknown fixture scenario.");
                }
            }
            finally { await terminal.DisposeAsync(); }
            snapshot = terminal.Snapshot; var after = ReadState(consoleInput, consoleOutput); Equal(original, after);
            Check(snapshot.RestorationConfirmed && snapshot.Restored == original && snapshot.ActiveReads == 0 && snapshot.ActiveWrites == 0,
                "Exact restoration or joined native workers was not confirmed.");
            Equal(snapshot.ReadWorkersStarted, snapshot.ReadWorkersSettled); Equal(snapshot.WriteWorkersStarted, snapshot.WriteWorkersSettled);
            await Expect<ObjectDisposedException>(() => terminal.ReadAsync(new char[1]).AsTask());
            await Expect<ObjectDisposedException>(() => terminal.WriteAsync("closed".AsMemory()).AsTask());
            // Original explicit console handles survive and the process reservation is released.
            await using (var next = await WindowsConsoleTerminal.OpenBorrowedHandlesAsync(consoleInput, consoleOutput)) { Equal(original, next.Snapshot.Original); }
            Equal(original, ReadState(consoleInput, consoleOutput)); Equal(standardAliases, ReadStandardAliases());
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { stage = "done", scenario = args[2], original, after, snapshot, readObservations })); return 0;
        }
        catch (Exception error)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { stage = "failed", scenario = args[2], phase,
                type = error.GetType().Name, message = error.Message, standardProbe, startupObservation, beforeSentinel, sentinelCalls,
                afterSentinel, readObservations, current = ProbeConsole(consoleInput, consoleOutput) })); return 1;
        }
        finally { consoleInput?.Dispose(); consoleOutput?.Dispose(); }
    }

    private static async Task ThrowWithinLease(WindowsConsoleTerminal terminal)
    { await using (terminal) { await terminal.WriteAsync("BODY-EXCEPTION".AsMemory()); throw new FixtureBodyException(); } }
    private sealed class FixtureBodyException : Exception { }

    private static async Task Run(string dotnetHost, string harnessDll, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { PlatformEvidence = "unsupported-non-windows"; return; }
        Check(Path.IsPathFullyQualified(dotnetHost) && File.Exists(dotnetHost), "Explicit dotnet host is unavailable.");
        Check(Path.IsPathFullyQualified(harnessDll) && File.Exists(harnessDll), "Explicit compiled harness is unavailable.");
        var name = "pisharp-conpty-" + Guid.NewGuid().ToString("N");
        await using var control = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var console = HiddenConsole.Start(dotnetHost, harnessDll, name, scenario);
        using var timeout = new CancellationTokenSource(Deadline);
        await control.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(control, new UTF8Encoding(false, true), false, 1024, leaveOpen: true);
        string? ready = null;
        for (var receipt = 0; receipt < 4; receipt++)
        {
            ready = await reader.ReadLineAsync(timeout.Token); RetainReceipt(scenario, ready);
            if (ready is null)
            {
                var exit = JsonSerializer.Serialize(new { stage = "parent-observed-eof", evidence = console.ObserveExitAndOutput() });
                RetainReceipt(scenario, exit); throw new Exception("Worker never reported readiness: " + exit);
            }
            using var progress = JsonDocument.Parse(ready);
            if (progress.RootElement.GetProperty("stage").GetString() != "probe") break;
        }
        using (var report = JsonDocument.Parse(ready!))
            Check(report.RootElement.GetProperty("stage").GetString() == "ready", "Worker failed before acquisition: " + ready);
        await console.Input.WriteAsync(Encoding.UTF8.GetBytes(Input), timeout.Token);
        await console.Input.FlushAsync(timeout.Token);
        // The link prologue is completely consumed first. No case gets additional wake input or input EOF.
        var done = await reader.ReadLineAsync(timeout.Token); RetainReceipt(scenario, done);
        if (done is null)
        {
            var exit = JsonSerializer.Serialize(new { stage = "parent-observed-eof", evidence = console.ObserveExitAndOutput() });
            RetainReceipt(scenario, exit); throw new Exception("Worker never reported restoration: " + exit);
        }
        using (var report = JsonDocument.Parse(done!))
        {
            Check(report.RootElement.GetProperty("stage").GetString() == "done", "Worker failed: " + done);
            var before = report.RootElement.GetProperty("original").Deserialize<TerminalConsoleState>();
            var after = report.RootElement.GetProperty("after").Deserialize<TerminalConsoleState>(); Equal(before, after);
        }
        Equal(0u, await console.WaitForExit(timeout.Token));
        // Close/drain only after the worker has independently observed exact restoration.
        await console.CloseConsole();
        Check(console.OutputText.Contains(LinkOutput, StringComparison.Ordinal), "Actual private ConPTY input/output link was not confirmed.");
        if (scenario == "unicode") Check(console.OutputText.Contains("\ud83d\ude42:OUT\u4e2d\ud83d\ude42:END", StringComparison.Ordinal), "Actual ConPTY Unicode output differs.");
        if (scenario == "borrowed") Check(console.OutputText.Contains("BORROWED:\u4e2d\ud83d\ude42", StringComparison.Ordinal), "Borrowed-wrapper disposal broke output.");
        PlatformEvidence = "actual-hidden-windows-conpty-exercised";
    }

    private static SafeFileHandle Standard(int id) => new(Native.GetStdHandle(unchecked((uint)id)), false);
    private sealed record StandardAliases(long Input, long Output, uint InputType, uint OutputType);
    private static StandardAliases ReadStandardAliases()
    {
        using var input = Standard(-10); using var output = Standard(-11);
        return new(input.DangerousGetHandle().ToInt64(), output.DangerousGetHandle().ToInt64(), Native.GetFileType(input), Native.GetFileType(output));
    }
    private static SafeFileHandle OpenConsole(string device)
    {
        var handle = Native.CreateFileW(device, 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        var error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Opening explicit " + device + " failed (Win32 " + error + ")."); }
        return handle;
    }
    private sealed record ConsoleObservation(string Api, bool Success, int Error, object? Value);
    private static ConsoleObservation[] ProbeConsole(SafeFileHandle? selectedInput = null, SafeFileHandle? selectedOutput = null)
    {
        using var standardInput = Standard(-10); using var standardOutput = Standard(-11);
        var input = selectedInput ?? standardInput; var output = selectedOutput ?? standardOutput;
        var inputType = Native.GetFileType(input); var inputTypeError = inputType == 0 ? Marshal.GetLastPInvokeError() : 0;
        var outputType = Native.GetFileType(output); var outputTypeError = outputType == 0 ? Marshal.GetLastPInvokeError() : 0;
        var inputModeOk = Native.GetConsoleMode(input, out var inputMode); var inputModeError = inputModeOk ? 0 : Marshal.GetLastPInvokeError();
        var outputModeOk = Native.GetConsoleMode(output, out var outputMode); var outputModeError = outputModeOk ? 0 : Marshal.GetLastPInvokeError();
        var inputEventsOk = Native.GetNumberOfConsoleInputEvents(input, out var inputEvents); var inputEventsError = inputEventsOk ? 0 : Marshal.GetLastPInvokeError();
        var cursorOk = Native.GetConsoleCursorInfo(output, out var cursor); var cursorError = cursorOk ? 0 : Marshal.GetLastPInvokeError();
        var bufferOk = Native.GetConsoleScreenBufferInfo(output, out var buffer); var bufferError = bufferOk ? 0 : Marshal.GetLastPInvokeError();
        var inputCp = Native.GetConsoleCP(); var inputCpError = inputCp == 0 ? Marshal.GetLastPInvokeError() : 0;
        var outputCp = Native.GetConsoleOutputCP(); var outputCpError = outputCp == 0 ? Marshal.GetLastPInvokeError() : 0;
        return [new(selectedInput is null && selectedOutput is null ? "GetStdHandle" : "ExplicitConsoleHandles", !input.IsInvalid && !output.IsInvalid, 0,
                new { input = input.DangerousGetHandle().ToInt64(), output = output.DangerousGetHandle().ToInt64() }),
            new("GetFileType(input)", inputType != 0, inputTypeError, inputType), new("GetFileType(output)", outputType != 0, outputTypeError, outputType),
            new("GetConsoleMode(input)", inputModeOk, inputModeError, inputModeOk ? inputMode : null),
            new("GetConsoleMode(output)", outputModeOk, outputModeError, outputModeOk ? outputMode : null),
            new("GetNumberOfConsoleInputEvents", inputEventsOk, inputEventsError, inputEventsOk ? inputEvents : null),
            new("GetConsoleCursorInfo", cursorOk, cursorError, cursorOk ? new { size = cursor.Size, visible = cursor.Visible } : null),
            new("GetConsoleScreenBufferInfo", bufferOk, bufferError, bufferOk ? new { columns = buffer.Size.X, rows = buffer.Size.Y, column = buffer.Cursor.X, row = buffer.Cursor.Y } : null),
            new("GetConsoleCP", inputCp != 0, inputCpError, inputCp != 0 ? inputCp : null),
            new("GetConsoleOutputCP", outputCp != 0, outputCpError, outputCp != 0 ? outputCp : null)];
    }
    private static void RetainReceipt(string scenario, string? receipt)
    {
        Check(receipt is null || receipt.Length <= 8192, "Worker receipt exceeds fixed diagnostic bound.");
        // Retain before assertions or await-using teardown can kill the owned worker.
        workerReceipts.Add(scenario + ":" + (receipt ?? "<EOF>"));
    }
    // Independent Win32 inspection, rather than trusting an adapter snapshot as proof of restoration.
    private static TerminalConsoleState ReadState(SafeFileHandle input, SafeFileHandle output)
    {
        Check(Native.GetConsoleMode(input, out var inputMode), "Fixture console input mode unavailable.");
        Check(Native.GetConsoleMode(output, out var outputMode), "Fixture console output mode unavailable.");
        Check(Native.GetConsoleCursorInfo(output, out var cursor), "Fixture cursor unavailable.");
        Check(Native.GetConsoleScreenBufferInfo(output, out var buffer), "Fixture screen buffer unavailable.");
        return new(inputMode, outputMode, Native.GetConsoleCP(), Native.GetConsoleOutputCP(), cursor.Size, cursor.Visible, buffer.Cursor.X, buffer.Cursor.Y);
    }
    private static async Task Expect<T>(Func<Task> operation, Func<T, bool>? predicate = null) where T : Exception
    { try { await operation(); } catch (T error) { Check(predicate?.Invoke(error) ?? true, "Exception details differ."); return; } throw new Exception("Expected " + typeof(T).Name); }
    private sealed record ReadObservation(string Phase, int? Count, string? Utf16Hex, string? Exception, bool CallerCanceled, bool IsClosing);
    private static char[] UnreadBuffer() => Enumerable.Repeat(UnreadMarker, 16).ToArray();
    private static void ObserveRead(List<ReadObservation> observations, string phase, int? count, char[] destination, Exception? error,
        CancellationToken caller, WindowsConsoleTerminal terminal)
    {
        if (observations.Count == 8) observations.RemoveAt(0);
        var hex = count is null ? null : string.Join(" ", destination.Take(Math.Clamp(count.Value, 0, destination.Length)).Select(value => ((int)value).ToString("X4")));
        observations.Add(new(phase, count, hex, error?.GetType().Name, caller.IsCancellationRequested, terminal.Snapshot.IsClosing));
    }
    private static async Task ExpectRead<T>(Task<int> pending, char[] destination, List<ReadObservation> observations, string phase,
        CancellationToken caller, WindowsConsoleTerminal terminal, Func<T, bool>? predicate = null) where T : Exception
    {
        try
        {
            var count = await pending; ObserveRead(observations, phase, count, destination, null, caller, terminal);
            throw new Exception("Expected " + typeof(T).Name + "; actual normal read count " + count + ".");
        }
        catch (T error)
        {
            ObserveRead(observations, phase, null, destination, error, caller, terminal);
            Check(predicate?.Invoke(error) ?? true, "Read exception details differ.");
            Check(destination.All(value => value == UnreadMarker), "Canceled empty console read modified the caller destination.");
            Check(terminal.Snapshot.ActiveReads == 0, "Canceled empty console read retained active ownership.");
            Equal(terminal.Snapshot.ReadWorkersStarted, terminal.Snapshot.ReadWorkersSettled);
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Native terminal observed state differs."); }

    private sealed class HiddenConsole : IAsyncDisposable
    {
        private IntPtr console;
        private SafeFileHandle? job, nativeProcess;
        private FileStream? input, output;
        private Task? drain;
        private readonly MemoryStream captured = new();
        private readonly object captureGate = new();
        private Task? close;
        public Process Process { get; private set; } = null!;
        public Stream Input => input!;
        public string OutputText { get { lock (captureGate) return Encoding.UTF8.GetString(captured.ToArray()); } }
        public object ObserveExitAndOutput()
        {
            uint exit = 0;
            var inspected = nativeProcess is not null && Native.GetExitCodeProcess(nativeProcess, out exit);
            var error = inspected ? 0 : Marshal.GetLastPInvokeError();
            // These are observations before the fixture requests any termination or closes the ConPTY.
            var text = OutputText;
            return new { processId = Process.Id, exitObserved = inspected, hasExited = inspected && exit != 259, error,
                exitCode = inspected ? exit : (uint?)null, conPtyOutputPrefix = text[..Math.Min(text.Length, 1024)] };
        }
        public async Task<uint> WaitForExit(CancellationToken token)
        {
            // The CreateProcess-owned handle remains valid after exit; a lazily opened Process PID wrapper does not.
            while (true)
            {
                token.ThrowIfCancellationRequested(); var state = Native.WaitForSingleObject(nativeProcess!, 0);
                if (state == 0)
                {
                    Check(Native.GetExitCodeProcess(nativeProcess!, out var exit), "Owned native process exit code unavailable."); return exit;
                }
                Check(state == 258, "Owned native process wait failed."); await Task.Delay(10, token);
            }
        }

        public static HiddenConsole Start(string host, string dll, string pipe, string scenario)
        {
            var result = new HiddenConsole(); SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null, thread = null;
            IntPtr list = IntPtr.Zero; var initialized = false;
            try
            {
                Check(Native.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) && Native.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0), "ConPTY pipes failed.");
                var hr = Native.CreatePseudoConsole(new(80, 24), inputRead!, outputWrite!, 0, out result.console);
                Check(hr == 0, "Windows ConPTY unavailable (HRESULT " + hr.ToString("X8") + ").");
                result.input = new FileStream(inputWrite!, FileAccess.Write, 4096, isAsync: false); inputWrite = null;
                result.output = new FileStream(outputRead!, FileAccess.Read, 4096, isAsync: false); outputRead = null;
                result.drain = Task.Run(result.Drain);
                result.job = Native.CreateJobObjectW(IntPtr.Zero, null); Check(!result.job.IsInvalid, "Fixture job failed.");
                var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000;
                Check(Native.SetInformationJobObject(result.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()), "Fixture job configuration failed.");
                var bytes = IntPtr.Zero; _ = Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                Check(bytes != IntPtr.Zero && bytes.ToInt64() <= 65_536, "ConPTY launch attributes invalid."); list = Marshal.AllocHGlobal(bytes);
                Check(Native.InitializeProcThreadAttributeList(list, 1, 0, ref bytes), "ConPTY attributes failed."); initialized = true;
                Check(Native.UpdateProcThreadAttribute(list, 0, (IntPtr)0x20016, result.console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "ConPTY attribute failed.");
                // SW_HIDE is local startup presentation. No STARTF_USESTDHANDLES, no inherited handles,
                // and no CREATE_NO_WINDOW/DETACHED_PROCESS/CREATE_NEW_CONSOLE conflicts with ConPTY attachment.
                var start = new StartupEx { Startup = new Startup { Size = (uint)Marshal.SizeOf<StartupEx>(), Flags = 1, ShowWindow = 0 }, Attributes = list };
                var command = new StringBuilder(string.Join(" ", new[] { host, dll, WorkerSwitch, pipe, scenario }.Select(Quote)));
                Check(Native.CreateProcessW(host, command, IntPtr.Zero, IntPtr.Zero, false, 0x80000 | 0x4, IntPtr.Zero, Path.GetDirectoryName(dll), ref start, out var process), "Hidden ConPTY launch failed.");
                result.nativeProcess = new(process.Process, true); thread = new(process.Thread, true);
                Check(Native.AssignProcessToJobObject(result.job, result.nativeProcess), "ConPTY containment failed.");
                result.Process = System.Diagnostics.Process.GetProcessById(checked((int)process.ProcessId));
                Check(Native.ResumeThread(thread) != uint.MaxValue, "ConPTY resume failed."); return result;
            }
            catch
            {
                inputRead?.Dispose(); outputWrite?.Dispose();
                result.job?.Dispose();
                if (result.nativeProcess is not null) { _ = Native.TerminateProcess(result.nativeProcess, 1); _ = Native.WaitForSingleObject(result.nativeProcess, 5000); }
                result.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw;
            }
            finally
            {
                thread?.Dispose(); inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
                if (initialized) Native.DeleteProcThreadAttributeList(list); if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            }
        }
        private void Drain()
        {
            var buffer = new byte[4096];
            while (true)
            {
                var count = output!.Read(buffer); if (count == 0) return;
                lock (captureGate)
                {
                    var retain = Math.Min(count, Math.Max(0, 65_536 - checked((int)captured.Length)));
                    if (retain != 0) captured.Write(buffer, 0, retain);
                }
            }
        }
        public Task CloseConsole() => close ??= CloseCore();
        private async Task CloseCore()
        {
            input?.Dispose(); input = null;
            if (console != IntPtr.Zero) { var owned = console; console = IntPtr.Zero; await Task.Run(() => Native.ClosePseudoConsole(owned)); }
            if (drain is not null) await drain; output?.Dispose(); output = null;
        }
        public async ValueTask DisposeAsync()
        {
            var exited = true;
            try
            {
                if (nativeProcess is not null)
                {
                    job?.Dispose(); job = null;
                    _ = Native.TerminateProcess(nativeProcess, 1);
                    exited = Native.WaitForSingleObject(nativeProcess, 5000) == 0;
                }
            }
            finally
            {
                try { await CloseConsole(); }
                finally { job?.Dispose(); job = null; nativeProcess?.Dispose(); nativeProcess = null; Process?.Dispose(); captured.Dispose(); }
            }
            Check(exited, "Fixture child cleanup could not confirm exit.");
        }
        private static string Quote(string value)
        {
            var result = new StringBuilder("\""); var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? 2 * slashes + 1 : slashes).Append(character); slashes = 0;
            }
            return result.Append('\\', 2 * slashes).Append('"').ToString();
        }
    }

    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public uint Size; [MarshalAs(UnmanagedType.Bool)] public bool Visible; }
    [StructLayout(LayoutKind.Sequential)] private struct SmallRect { public short Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BufferInfo { public Coord Size, Cursor; public ushort Attributes; public SmallRect Window; public Coord MaximumWindowSize; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup
    {
        public uint Size; public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2; public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { public Startup Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupObservation
    {
        public uint Size; public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2; public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessUserTimeLimit, JobUserTimeLimit; public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
        public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GetStdHandle(uint id);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern void GetStartupInfoW(out StartupObservation startup);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetConsoleCP();
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetConsoleOutputCP();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetConsoleMode(SafeFileHandle handle, out uint mode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNumberOfConsoleInputEvents(SafeFileHandle handle, out uint events);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetConsoleCursorInfo(SafeFileHandle handle, out CursorInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetConsoleScreenBufferInfo(SafeFileHandle handle, out BufferInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleCursorInfo(SafeFileHandle handle, ref CursorInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetConsoleCursorPosition(SafeFileHandle handle, Coord coord);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr security, uint size);
        [DllImport("kernel32.dll")] internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] internal static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr bytes);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref StartupEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll")] internal static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeFileHandle process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exit);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    }
}
