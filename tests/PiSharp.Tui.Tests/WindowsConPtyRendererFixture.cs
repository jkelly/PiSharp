using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PiSharp.Tui;
using PiSharp.Tui.Rendering;

internal static class WindowsConPtyRendererFixture
{
    public const string WorkerSwitch = "--windows-conpty-renderer-worker";
    private const int InitialColumns = 24, InitialRows = 6, MaximumReceiptCharacters = 8192, MaximumOutputBytes = 65_536;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(12);
    private static readonly List<string> receipts = new();
    private static readonly List<object> witnesses = new();
    public static string[] WorkerReceipts => receipts.ToArray();
    public static object[] ParentWitnesses => witnesses.ToArray();
    public static string PlatformEvidence { get; private set; } = "not-exercised";

    public static IEnumerable<(string Name, Func<Task> Run)> Cases(string dotnetHost, string harnessDll)
    {
        foreach (var scenario in new[] { "frames-controls", "resize", "borrowed-lifetime", "parent-abort" })
        {
            var selected = scenario;
            yield return ("terminal-renderer-conpty.actual-hidden-" + selected, () => Run(dotnetHost, harnessDll, selected));
        }
    }

    public static async Task<int?> TryRunWorkerAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != WorkerSwitch) return null;
        if (args.Length != 4 || !OperatingSystem.IsWindows() || args[3].Length != 8 ||
            !args[3].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')) return 2;
        var scenario = args[2]; var nonce = args[3]; var phase = "connect";
        using var deadline = new CancellationTokenSource(Deadline);
        await using var control = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous);
        await control.ConnectAsync(deadline.Token);
        using var reader = new StreamReader(control, new UTF8Encoding(false, true), false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(control, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        SafeFileHandle? input = null, output = null; WindowsConsoleTerminal? terminal = null; VtRenderer? renderer = null;
        TerminalConsoleState? original = null, after = null; Aliases? aliases = null;
        Geometry? originalGeometry = null; Exception? failure = null; object? finalSnapshot = null;
        try
        {
            input = OpenConsole("CONIN$"); output = OpenConsole("CONOUT$"); aliases = ReadAliases();
            Check(Native.GetFileType(input) == 2 && Native.GetFileType(output) == 2, "Explicit handles are not console character devices.");
            originalGeometry = ReadGeometry(output); RequireSize(originalGeometry, InitialColumns, InitialRows);
            var cursorInfo = new CursorInfo { Size = 33, Visible = true };
            Api(Native.SetConsoleCursorInfo(output, ref cursorInfo), "SetConsoleCursorInfo");
            Api(Native.SetConsoleCursorPosition(output, new(3, 2)), "SetConsoleCursorPosition");
            original = ReadState(input, output);
            Check(original.CursorSize == 33 && original.CursorVisible && original.CursorColumn == originalGeometry.Left + 3 &&
                original.CursorRow == originalGeometry.Top + 2, "Native fixture cursor sentinel differs.");
            using var borrowedInput = new SafeFileHandle(input.DangerousGetHandle(), false);
            using var borrowedOutput = new SafeFileHandle(output.DangerousGetHandle(), false);
            terminal = await WindowsConsoleTerminal.OpenBorrowedHandlesAsync(borrowedInput, borrowedOutput);
            Equal(original, terminal.Snapshot.Original); var acquired = ReadState(input, output);
            Equal(acquired, terminal.Snapshot.Acquired);
            await Send(writer, new { stage = "ready", scenario, nonce, original, acquired, originalGeometry, aliases,
                processId = Environment.ProcessId, os = RuntimeInformation.OSDescription,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(), runtime = RuntimeInformation.FrameworkDescription }, deadline.Token);
            phase = "private-link";
            var prologue = new char[8]; var received = 0;
            while (received < prologue.Length)
            {
                var count = await terminal.ReadAsync(prologue.AsMemory(received), deadline.Token);
                Check(count > 0 && count <= prologue.Length - received, "Private console prologue returned an invalid count."); received += count;
            }
            Equal(nonce, new string(prologue));
            var layout = new TerminalTextLayout(new AsciiWidth());
            renderer = new VtRenderer(terminal);
            var rows = FilledRows(InitialColumns, InitialRows, nonce);
            await RenderWitness(writer, terminal, renderer, layout, input, output, rows, rows, new(2, 5),
                TerminalRenderKind.Full, "first-full", deadline.Token);
            switch (scenario)
            {
                case "frames-controls":
                {
                    var diff = rows.ToArray(); diff[1] = "SHORT";
                    var expected = rows.ToArray(); expected[1] = "SHORT".PadRight(InitialColumns);
                    await RenderWitness(writer, terminal, renderer, layout, input, output, diff, expected, new(1, 3, false),
                        TerminalRenderKind.Diff, "shorter-row-diff", deadline.Token);
                    await RenderWitness(writer, terminal, renderer, layout, input, output, diff, expected, new(1, 3, false),
                        TerminalRenderKind.Unchanged, "equal-frame", deadline.Token);
                    diff[2] = "\u001b[2J\u009bH\u0007\tOK"; expected[2] = "?[2J?H?   OK".PadRight(InitialColumns);
                    await RenderWitness(writer, terminal, renderer, layout, input, output, diff, expected, new(2, 4),
                        TerminalRenderKind.Diff, "literal-control-data", deadline.Token);
                    break;
                }
                case "resize":
                {
                    await Send(writer, new { stage = "resize-request", nonce, columns = 16, rows = 4 }, deadline.Token);
                    await Command(reader, "resized", nonce, deadline.Token);
                    var resizedGeometry = ReadGeometry(output); RequireSize(resizedGeometry, 16, 4);
                    renderer.Invalidate(); Check(!renderer.Snapshot.CacheKnown, "Resize did not invalidate the renderer cache.");
                    var resized = FilledRows(16, 4, nonce);
                    await RenderWitness(writer, terminal, renderer, layout, input, output, resized, resized, new(2, 4),
                        TerminalRenderKind.Full, "resized-full", deadline.Token);
                    await RenderWitness(writer, terminal, renderer, layout, input, output, resized, resized, new(2, 4),
                        TerminalRenderKind.Unchanged, "resized-equal", deadline.Token);
                    renderer.Invalidate();
                    await RenderWitness(writer, terminal, renderer, layout, input, output, resized, resized, new(2, 4),
                        TerminalRenderKind.Full, "same-size-invalidated-full", deadline.Token);
                    await Send(writer, new { stage = "restore-size-request", nonce, columns = InitialColumns, rows = InitialRows }, deadline.Token);
                    await Command(reader, "size-restored", nonce, deadline.Token);
                    RequireSize(ReadGeometry(output), InitialColumns, InitialRows); renderer.Invalidate();
                    break;
                }
                case "borrowed-lifetime":
                {
                    borrowedInput.Dispose(); borrowedOutput.Dispose();
                    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                    var frame = layout.CreateFrame(string.Join("\n", rows), InitialRows, InitialColumns, new(2, 5));
                    try { await renderer.RenderAsync(frame, cancellation.Token); throw new Exception("Precancelled renderer succeeded."); }
                    catch (OperationCanceledException error) { Equal(cancellation.Token, error.CancellationToken); }
                    Check(!renderer.Snapshot.CacheKnown && renderer.Snapshot.PendingRenders == 0, "Canceled renderer retained a known cache or pending turn.");
                    await RenderWitness(writer, terminal, renderer, layout, input, output, rows, rows, new(2, 5),
                        TerminalRenderKind.Full, "cancelled-full-recovery", deadline.Token);
                    await renderer.DisposeAsync();
                    Check(!terminal.Snapshot.IsClosing, "Renderer disposed its borrowed console lease.");
                    renderer = new VtRenderer(terminal);
                    rows[1] = "LEASE-STILL-OWNED".PadRight(InitialColumns);
                    await RenderWitness(writer, terminal, renderer, layout, input, output, rows, rows, new(1, 6),
                        TerminalRenderKind.Full, "fresh-renderer-borrowed-lease", deadline.Token);
                    break;
                }
                case "parent-abort":
                    await Send(writer, new { stage = "await-parent-abort", nonce }, deadline.Token);
                    await Command(reader, "unreachable", nonce, deadline.Token);
                    throw new Exception("Parent-abort worker unexpectedly resumed.");
                default: throw new Exception("Unknown renderer ConPTY scenario.");
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { if (renderer is not null) await renderer.DisposeAsync(); } catch (Exception error) { failure ??= error; }
            try { if (terminal is not null) await terminal.DisposeAsync(); } catch (Exception error) { failure ??= error; }
            try
            {
                if (input is not null && output is not null && original is not null)
                {
                    after = ReadState(input, output); Equal(original, after);
                    RequireSize(ReadGeometry(output), InitialColumns, InitialRows);
                    if (terminal is not null)
                    {
                        var snapshot = terminal.Snapshot; finalSnapshot = snapshot;
                        Check(snapshot.RestorationConfirmed && snapshot.Restored == original && snapshot.ActiveReads == 0 && snapshot.ActiveWrites == 0,
                            "Native lease restoration or actual worker settlement differs.");
                        Equal(snapshot.ReadWorkersStarted, snapshot.ReadWorkersSettled);
                        Equal(snapshot.WriteWorkersStarted, snapshot.WriteWorkersSettled);
                        await using (var next = await WindowsConsoleTerminal.OpenBorrowedHandlesAsync(input, output)) Equal(original, next.Snapshot.Original);
                        Equal(original, ReadState(input, output));
                    }
                    Equal(aliases, ReadAliases());
                }
            }
            catch (Exception error) { failure ??= error; }
            input?.Dispose(); output?.Dispose();
        }
        using var reporting = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Send(writer, new { stage = failure is null ? "done" : "failed", scenario, nonce, phase, original, after,
            originalGeometry, finalSnapshot, type = failure?.GetType().Name, message = failure?.Message }, reporting.Token);
        return failure is null ? 0 : 1;
    }

    private static async Task RenderWitness(StreamWriter writer, WindowsConsoleTerminal terminal, VtRenderer renderer,
        TerminalTextLayout layout, SafeFileHandle input, SafeFileHandle output, string[] textRows, string[] expected,
        TerminalCursor cursor, TerminalRenderKind kind, string step, CancellationToken token)
    {
        var geometry = ReadGeometry(output); RequireSize(geometry, expected[0].Length, expected.Length);
        var before = terminal.Snapshot.WriteWorkersStarted;
        var frame = layout.CreateFrame(string.Join("\n", textRows), geometry.Rows, geometry.Columns, cursor);
        var result = await renderer.RenderAsync(frame, token);
        Equal(kind, result.Kind); Check(result.CacheCommitted, "Real renderer frame did not commit after awaited output.");
        var screen = ReadScreen(output, geometry);
        Check(expected.SequenceEqual(screen), "Actual native console cells differ.");
        var nativeState = ReadState(input, output);
        Equal((short)(geometry.Left + cursor.Column), nativeState.CursorColumn); Equal((short)(geometry.Top + cursor.Row), nativeState.CursorRow);
        Equal(cursor.Visible, nativeState.CursorVisible);
        var snapshot = terminal.Snapshot;
        Equal(before + (kind == TerminalRenderKind.Unchanged ? 0 : 1), snapshot.WriteWorkersStarted);
        Check(snapshot.ActiveWrites == 0 && renderer.Snapshot.PendingRenders == 0, "An actual write or renderer turn remains active.");
        Equal(snapshot.WriteWorkersStarted, snapshot.WriteWorkersSettled);
        await Send(writer, new { stage = "witness", step, geometry, screen, nativeState, result, renderer = renderer.Snapshot,
            lease = snapshot }, token);
    }

    private static string[] FilledRows(int columns, int rows, string nonce)
    {
        var result = Enumerable.Range(0, rows).Select(row => new string((char)('A' + row), columns)).ToArray();
        result[0] = ("LINK:" + nonce).PadRight(columns); return result;
    }

    private static async Task Run(string host, string dll, string scenario)
    {
        if (!OperatingSystem.IsWindows()) { PlatformEvidence = "unsupported-non-windows"; return; }
        Check(Path.IsPathFullyQualified(host) && File.Exists(host) && Path.IsPathFullyQualified(dll) && File.Exists(dll), "Explicit compiled host or harness is unavailable.");
        var pipeName = "pisharp-renderer-conpty-" + Guid.NewGuid().ToString("N"); var nonce = Guid.NewGuid().ToString("N")[..8];
        using var deadline = new CancellationTokenSource(Deadline);
        await using var control = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var console = OwnedConsole.Start(host, dll, pipeName, scenario, nonce);
        var aborted = false;
        try
        {
            await control.WaitForConnectionAsync(deadline.Token);
            using var reader = new StreamReader(control, new UTF8Encoding(false, true), false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(control, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            var ready = await Receive(reader, scenario, deadline.Token);
            using (var receipt = JsonDocument.Parse(ready))
            {
                Equal("ready", receipt.RootElement.GetProperty("stage").GetString());
                Equal(nonce, receipt.RootElement.GetProperty("nonce").GetString());
            }
            await console.Input.WriteAsync(Encoding.ASCII.GetBytes(nonce), deadline.Token); await console.Input.FlushAsync(deadline.Token);
            var done = false; var observedWitnesses = 0;
            var expectedWitnesses = scenario switch
            {
                "frames-controls" => 4, "resize" => 4, "borrowed-lifetime" => 3, "parent-abort" => 1,
                _ => throw new Exception("Unknown parent renderer scenario.")
            };
            for (var index = 0; index < 10; index++)
            {
                var line = await Receive(reader, scenario, deadline.Token); using var receipt = JsonDocument.Parse(line);
                var root = receipt.RootElement; var stage = root.GetProperty("stage").GetString();
                if (stage == "witness")
                {
                    Check(++observedWitnesses <= expectedWitnesses, "Renderer worker exceeded its physical witness count.");
                    Check(root.GetProperty("screen")[0].GetString()?.StartsWith("LINK:" + nonce, StringComparison.Ordinal) == true,
                        "Native screen receipt did not contain its private console nonce.");
                    continue;
                }
                if (stage == "resize-request")
                {
                    console.Resize(16, 4); await Send(writer, new { stage = "resized", nonce }, deadline.Token); continue;
                }
                if (stage == "restore-size-request")
                {
                    console.Resize(InitialColumns, InitialRows); await Send(writer, new { stage = "size-restored", nonce }, deadline.Token); continue;
                }
                if (stage == "await-parent-abort" && scenario == "parent-abort")
                {
                    Equal(nonce, root.GetProperty("nonce").GetString());
                    await console.WaitForNonceOutput(deadline.Token); aborted = true; break;
                }
                Check(stage == "done", "Renderer worker failed: " + line);
                Equal(nonce, root.GetProperty("nonce").GetString());
                Equal(root.GetProperty("original").Deserialize<TerminalConsoleState>(), root.GetProperty("after").Deserialize<TerminalConsoleState>());
                done = true; break;
            }
            Check(done || aborted, "Renderer worker exceeded bounded protocol stages.");
            Equal(expectedWitnesses, observedWitnesses);
            if (!aborted) Equal(0u, await console.WaitForExit());
        }
        finally
        {
            try { await console.DisposeAsync(); }
            finally { witnesses.Add(console.Evidence(scenario, nonce, aborted)); }
        }
        var raw = console.OutputBytes;
        Check(raw.Length > 0 && !console.Truncated, "Real ConPTY output was empty or exceeded retention bound.");
        var text = new UTF8Encoding(false, true).GetString(raw);
        Check(text.Contains("LINK:" + nonce, StringComparison.Ordinal), "Real private ConPTY renderer output nonce was absent.");
        Check(text.Contains("\u001b[", StringComparison.Ordinal), "Actual ConPTY output contained no VT CSI witness.");
        Check(console.ExitConfirmed && console.CloseJoined && console.DrainJoined, "Physical child or ConPTY workers were not joined.");
        PlatformEvidence = "actual-hidden-windows-conpty-renderer-ascii-witness";
    }

    private static async Task Command(StreamReader reader, string expected, string nonce, CancellationToken token)
    {
        using var packet = JsonDocument.Parse(await ReadLine(reader, token));
        Equal(expected, packet.RootElement.GetProperty("stage").GetString()); Equal(nonce, packet.RootElement.GetProperty("nonce").GetString());
    }
    private static async Task<string> Receive(StreamReader reader, string scenario, CancellationToken token)
    {
        string line;
        try { line = await ReadLine(reader, token); }
        catch (Exception error) { receipts.Add(scenario + ":control-read:" + error.GetType().Name); throw; }
        receipts.Add(scenario + ":" + line); return line;
    }
    private static async Task<string> ReadLine(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var character = new char[1];
        while (true)
        {
            var count = await reader.ReadAsync(character.AsMemory(), token);
            if (count == 0) throw new EndOfStreamException("Renderer control pipe closed before its bounded receipt.");
            if (character[0] == '\n') return result.ToString();
            Check(result.Length < MaximumReceiptCharacters, "Renderer control receipt exceeds its hard bound."); result.Append(character[0]);
        }
    }
    private static Task Send(StreamWriter writer, object receipt, CancellationToken token)
    {
        var text = JsonSerializer.Serialize(receipt);
        Check(text.Length <= MaximumReceiptCharacters, "Authored renderer receipt exceeds its hard bound.");
        return writer.WriteLineAsync(text.AsMemory(), token);
    }
    private sealed class AsciiWidth : ITerminalWidthPolicy
    {
        public string Id => "real-conpty-ascii-v1";
        public int GetWidth(string grapheme)
        {
            Check(grapheme.Length == 1 && grapheme[0] is >= ' ' and <= '~', "Real-console fixture received unqualified width data."); return 1;
        }
    }
    private sealed record Aliases(long Input, long Output, uint InputType, uint OutputType);
    private static Aliases ReadAliases()
    {
        using var input = new SafeFileHandle(Native.GetStdHandle(unchecked((uint)-10)), false);
        using var output = new SafeFileHandle(Native.GetStdHandle(unchecked((uint)-11)), false);
        return new(input.DangerousGetHandle().ToInt64(), output.DangerousGetHandle().ToInt64(), Native.GetFileType(input), Native.GetFileType(output));
    }
    private sealed record Geometry(int Columns, int Rows, short Left, short Top, short Right, short Bottom, short BufferColumns, short BufferRows);
    private static Geometry ReadGeometry(SafeFileHandle output)
    {
        Api(Native.GetConsoleScreenBufferInfo(output, out var value), "GetConsoleScreenBufferInfo");
        return new(value.Window.Right - value.Window.Left + 1, value.Window.Bottom - value.Window.Top + 1,
            value.Window.Left, value.Window.Top, value.Window.Right, value.Window.Bottom, value.Size.X, value.Size.Y);
    }
    private static void RequireSize(Geometry geometry, int columns, int rows)
    {
        Check(columns is >= 1 and <= 32 && rows is >= 1 and <= 8 && columns * rows <= 256, "Native witness geometry exceeds its hard bound.");
        Check(geometry.Columns == columns && geometry.Rows == rows, "Actual native viewport differs from the committed ConPTY size.");
        Check(geometry.Left == 0 && geometry.Top == 0 && geometry.BufferColumns >= columns && geometry.BufferRows >= rows,
            "This bounded witness requires a native viewport at origin.");
    }
    private static string[] ReadScreen(SafeFileHandle output, Geometry geometry)
    {
        RequireSize(geometry, geometry.Columns, geometry.Rows);
        var cells = new CharacterInfo[geometry.Columns * geometry.Rows];
        var region = new SmallRect { Left = geometry.Left, Top = geometry.Top, Right = geometry.Right, Bottom = geometry.Bottom };
        Api(Native.ReadConsoleOutputW(output, cells, new((short)geometry.Columns, (short)geometry.Rows), new(0, 0), ref region), "ReadConsoleOutputW");
        Check(region.Left == geometry.Left && region.Top == geometry.Top && region.Right == geometry.Right && region.Bottom == geometry.Bottom,
            "Native screen observation was clipped.");
        var rows = new string[geometry.Rows];
        for (var row = 0; row < geometry.Rows; row++)
            rows[row] = new string(cells.AsSpan(row * geometry.Columns, geometry.Columns).ToArray().Select(cell => cell.Character).ToArray());
        return rows;
    }
    private static TerminalConsoleState ReadState(SafeFileHandle input, SafeFileHandle output)
    {
        Api(Native.GetConsoleMode(input, out var inputMode), "GetConsoleMode(input)");
        Api(Native.GetConsoleMode(output, out var outputMode), "GetConsoleMode(output)");
        Api(Native.GetConsoleCursorInfo(output, out var cursor), "GetConsoleCursorInfo");
        Api(Native.GetConsoleScreenBufferInfo(output, out var buffer), "GetConsoleScreenBufferInfo");
        var inputPage = Native.GetConsoleCP(); var outputPage = Native.GetConsoleOutputCP();
        Check(inputPage != 0 && outputPage != 0, "Actual console code pages are unavailable.");
        return new(inputMode, outputMode, inputPage, outputPage, cursor.Size, cursor.Visible, buffer.Cursor.X, buffer.Cursor.Y);
    }
    private static SafeFileHandle OpenConsole(string name)
    {
        var handle = Native.CreateFileW(name, 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new IOException("Opening private console device failed (Win32 " + error + ").");
    }
    private static void Api(bool success, string api)
    { if (!success) throw new IOException(api + " failed (Win32 " + Marshal.GetLastPInvokeError() + ")."); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Actual renderer witness differs."); }

    private sealed class OwnedConsole : IAsyncDisposable
    {
        private IntPtr console;
        private SafeFileHandle? job, process;
        private FileStream? input, output;
        private Task? drain, closing;
        private readonly MemoryStream captured = new();
        private readonly object gate = new();
        private readonly TaskCompletionSource nonceOutput = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[] nonceBytes = [];
        private byte[] retained = [];
        private long outputCount;
        private bool contained, exited, closeJoined, drainJoined;
        private bool? jobTerminationSucceeded, processTerminationSucceeded;
        private int? jobTerminationError, processTerminationError;
        private uint? exitCode, waitResult;
        private int? processId;
        public Stream Input => input!;
        public bool ExitConfirmed => exited;
        public bool CloseJoined => closeJoined;
        public bool DrainJoined => drainJoined;
        public bool Truncated { get { lock (gate) return outputCount > MaximumOutputBytes; } }
        public byte[] OutputBytes { get { lock (gate) return retained.ToArray(); } }

        public static OwnedConsole Start(string host, string dll, string pipe, string scenario, string nonce)
        {
            var result = new OwnedConsole { nonceBytes = Encoding.ASCII.GetBytes("LINK:" + nonce) };
            SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null, thread = null;
            var list = IntPtr.Zero; var initialized = false;
            try
            {
                Api(Native.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0), "CreatePipe(input)");
                Api(Native.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0), "CreatePipe(output)");
                var status = Native.CreatePseudoConsole(new(InitialColumns, InitialRows), inputRead!, outputWrite!, 0, out result.console);
                Check(status == 0, "Creating private renderer ConPTY failed (HRESULT " + status.ToString("X8") + ").");
                result.input = new FileStream(inputWrite!, FileAccess.Write, 4096, isAsync: false); inputWrite = null;
                result.output = new FileStream(outputRead!, FileAccess.Read, 4096, isAsync: false); outputRead = null;
                result.drain = Task.Factory.StartNew(result.Drain, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                result.job = Native.CreateJobObjectW(IntPtr.Zero, null); Check(!result.job.IsInvalid, "Private renderer containment job failed.");
                var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000;
                Api(Native.SetInformationJobObject(result.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()), "SetInformationJobObject");
                var bytes = IntPtr.Zero; _ = Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                Check(bytes != IntPtr.Zero && bytes.ToInt64() <= 65_536, "Renderer ConPTY attribute allocation is invalid.");
                list = Marshal.AllocHGlobal(bytes);
                Api(Native.InitializeProcThreadAttributeList(list, 1, 0, ref bytes), "InitializeProcThreadAttributeList"); initialized = true;
                Api(Native.UpdateProcThreadAttribute(list, 0, (IntPtr)0x20016, result.console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "UpdateProcThreadAttribute");
                var startup = new StartupEx { Startup = new Startup { Size = (uint)Marshal.SizeOf<StartupEx>(), Flags = 1, ShowWindow = 0 }, Attributes = list };
                var command = new StringBuilder(string.Join(" ", new[] { host, dll, WorkerSwitch, pipe, scenario, nonce }.Select(Quote)));
                Api(Native.CreateProcessW(host, command, IntPtr.Zero, IntPtr.Zero, false, 0x80004, IntPtr.Zero,
                    Path.GetDirectoryName(dll), ref startup, out var info), "CreateProcessW");
                result.process = new SafeFileHandle(info.Process, true); thread = new SafeFileHandle(info.Thread, true);
                result.processId = checked((int)info.ProcessId);
                Api(Native.AssignProcessToJobObject(result.job, result.process), "AssignProcessToJobObject");
                result.contained = true;
                Check(Native.ResumeThread(thread) != uint.MaxValue, "Resuming contained renderer worker failed.");
                return result;
            }
            catch (Exception error)
            {
                inputRead?.Dispose(); inputRead = null; outputWrite?.Dispose(); outputWrite = null;
                Exception? cleanupFailure = null;
                try { result.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch (Exception cleanup) { cleanupFailure = cleanup; }
                witnesses.Add(new { stage = "partial-launch-failure", type = error.GetType().Name, message = error.Message,
                    cleanupType = cleanupFailure?.GetType().Name, cleanupMessage = cleanupFailure?.Message,
                    physical = result.Evidence(scenario, nonce, true) });
                if (cleanupFailure is not null) throw new AggregateException(error, cleanupFailure);
                throw;
            }
            finally
            {
                thread?.Dispose(); inputRead?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose(); outputWrite?.Dispose();
                if (initialized) Native.DeleteProcThreadAttributeList(list);
                if (list != IntPtr.Zero) Marshal.FreeHGlobal(list);
            }
        }
        public void Resize(short columns, short rows)
        {
            Check(console != IntPtr.Zero && columns is >= 1 and <= 32 && rows is >= 1 and <= 8, "Renderer resize exceeds its owned console bounds.");
            var result = Native.ResizePseudoConsole(console, new(columns, rows)); Check(result == 0, "Actual ConPTY resize failed (HRESULT " + result.ToString("X8") + ").");
        }
        private void Drain()
        {
            var buffer = new byte[4096];
            while (true)
            {
                var count = output!.Read(buffer); if (count == 0) return;
                lock (gate)
                {
                    outputCount += count;
                    var retain = Math.Min(count, MaximumOutputBytes - checked((int)captured.Length));
                    if (retain > 0) captured.Write(buffer, 0, retain);
                    if (captured.GetBuffer().AsSpan(0, checked((int)captured.Length)).IndexOf(nonceBytes) >= 0) nonceOutput.TrySetResult();
                }
            }
        }
        public async Task WaitForNonceOutput(CancellationToken token)
        {
            // This observes actual retained bytes; cancellation detaches only this gate observer.
            // The owning Run finally still joins the real child and synchronous output drain.
            var completed = await Task.WhenAny(nonceOutput.Task, drain!).WaitAsync(token);
            if (completed == nonceOutput.Task) { await nonceOutput.Task; return; }
            await drain!; throw new EndOfStreamException("ConPTY output ended before the nonce witness.");
        }
        public async Task<uint> WaitForExit(bool cleanup = false)
        {
            var owned = process ?? throw new Exception("Renderer worker process handle is unavailable.");
            var result = await Task.Factory.StartNew(() => Native.WaitForSingleObject(owned, cleanup ? uint.MaxValue : 5000), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            waitResult = result; Check(result == 0, "Actual renderer worker did not signal exit within its native wait.");
            Api(Native.GetExitCodeProcess(owned, out var code), "GetExitCodeProcess"); exited = true; exitCode = code; return code;
        }
        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? completion = null; Task task;
            lock (gate)
            {
                if (closing is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); closing = completion.Task; }
                task = closing;
            }
            if (completion is not null) _ = Close(completion); return new(task);
        }
        private async Task Close(TaskCompletionSource completion)
        {
            Exception? failure = null;
            try
            {
                if (process is not null && !exited)
                {
                    if (contained && job is not null && !job.IsInvalid)
                    {
                        jobTerminationSucceeded = Native.TerminateJobObject(job, 3);
                        if (jobTerminationSucceeded == false)
                        {
                            jobTerminationError = Marshal.GetLastPInvokeError();
                            failure = new IOException("Owned renderer job termination failed (Win32 " + jobTerminationError + ").");
                        }
                    }
                    // A successful contained job request already terminates this process.
                    // Issuing a second request races its settlement; a failed immediate poll
                    // cannot establish that the first request failed. Partial launches that
                    // never joined the job still require direct process termination.
                    if (jobTerminationSucceeded != true)
                    {
                        processTerminationSucceeded = Native.TerminateProcess(process, 3);
                        if (processTerminationSucceeded == false)
                        {
                            processTerminationError = Marshal.GetLastPInvokeError();
                            if (Native.WaitForSingleObject(process, 0) != 0)
                                failure ??= new IOException("Owned renderer process termination failed (Win32 " + processTerminationError + ").");
                        }
                    }
                    // After requesting termination, retain the real wait worker/handle until settlement.
                    // An unsupported native hang requires the root's contained process watchdog.
                    try { await WaitForExit(cleanup: true); } catch (Exception error) { failure ??= error; }
                }
                input?.Dispose(); input = null;
                if (console != IntPtr.Zero)
                {
                    var owned = console;
                    try
                    {
                        await Task.Factory.StartNew(() => Native.ClosePseudoConsole(owned), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                        console = IntPtr.Zero; closeJoined = true;
                    }
                    catch (Exception error) { failure ??= error; }
                }
                if (drain is not null)
                {
                    try { await drain; drainJoined = true; } catch (Exception error) { failure ??= error; }
                }
            }
            catch (Exception error) { failure ??= error; }
            finally
            {
                lock (gate) retained = captured.ToArray();
                input?.Dispose(); output?.Dispose(); job?.Dispose(); process?.Dispose(); captured.Dispose();
                input = output = null; job = process = null;
            }
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }
        public object Evidence(string scenario, string nonce, bool parentAbort)
        {
            var bytes = OutputBytes;
            return new { scenario, nonce, parentAbort, processId, exitConfirmed = exited, nativeWaitResult = waitResult, exitCode,
                contained, jobTerminationSucceeded, jobTerminationError, processTerminationSucceeded, processTerminationError,
                closeJoined, drainJoined, actualNonceOutputObserved = nonceOutput.Task.IsCompletedSuccessfully,
                outputBytes = outputCount, retainedBytes = bytes.Length, truncated = Truncated,
                rawSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), rawBase64 = Convert.ToBase64String(bytes) };
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
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)] private struct CharacterInfo
    { [FieldOffset(0)] public char Character; [FieldOffset(2)] public ushort Attributes; }
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
        [DllImport("kernel32.dll")] internal static extern int ResizePseudoConsole(IntPtr console, Coord size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ReadConsoleOutputW(SafeFileHandle output, [Out] CharacterInfo[] buffer, Coord size, Coord origin, ref SmallRect region);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr bytes);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref StartupEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll")] internal static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeFileHandle process, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exit);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
    }
}
