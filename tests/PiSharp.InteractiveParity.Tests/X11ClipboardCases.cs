using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PiSharp.Cli.Interactive.Mode.Utilities;
using static Expect;

/// <summary>packages/tui/native/linux/src/linux-platform-x11.c and native-clipboard-linux.test.ts: the libxcb reader against an
/// isolated Xvfb server (never the desktop clipboard), with an in-process CLIPBOARD owner that serves TARGETS, plain and INCR
/// transfers, refuses TARGETS or never answers. The Xvfb cases skip where Linux, Xvfb or libxcb is missing, as upstream's do; the
/// unavailable cases run everywhere.</summary>
internal static class X11ClipboardCases
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0];

    public static IEnumerable<(string Id, Func<Task> Run)> All() =>
    [
        ("x11-clipboard.unavailable-without-a-display-or-libxcb", Unavailable),
        ("x11-clipboard.xvfb-reads-text-images-and-incr", XvfbReads),
        ("x11-clipboard.xvfb-stalled-owner-fails-after-the-deadline", XvfbStalledOwner),
    ];

    private static ClipboardEnvironment Linux(string? display) => new()
    {
        Platform = "linux", Env = name => name == "DISPLAY" ? display : null,
        Run = (_, _, _) => Task.FromResult<byte[]?>(null), ReadProcVersion = () => null,
    };

    // getNativeClipboard: no helper without DISPLAY; an unreachable display (or, off Linux, no libxcb) is unavailable, not an error.
    private static async Task Unavailable()
    {
        Equal(true, Linux(null).GetNativeClipboard() is null, "no DISPLAY, no helper");
        var helper = Linux(":4093").GetNativeClipboard();
        Check(helper is X11Clipboard, "DISPLAY gives the X11 helper");
        Equal<string?>(null, await helper!.GetTextAsync(), "unreachable display text");
        var (available, bytes) = await helper.GetImageAsync();
        Equal(false, available, "unreachable display image is unavailable");
        Equal(true, bytes is null, "no image bytes");
        Equal<string?>(null, await Clipboard.ReadClipboardText(Linux(":4093")), "readClipboardText falls through to null");
        Equal(true, await ClipboardImageReader.ReadClipboardImage(Linux(":4093")) is null, "readClipboardImage falls through to null");
    }

    private static async Task XvfbReads()
    {
        using var server = Xvfb.Start();
        var helper = new X11Clipboard(server.Display);
        // No owner: an empty text read and an available, empty image read.
        Equal<string?>(null, await helper.GetTextAsync(), "no owner text");
        var (available, none) = await helper.GetImageAsync();
        Check(available && none is null, "no owner image is available and empty");

        using (var owner = Owner.Start(server.Display, new() { ["UTF8_STRING"] = Encoding.UTF8.GetBytes("héllo ✓"), ["STRING"] = [0x78] }))
            Equal<string?>("héllo ✓", await helper.GetTextAsync(), "UTF8_STRING before STRING");
        using (var owner = Owner.Start(server.Display, new() { ["text/plain"] = Encoding.UTF8.GetBytes("plain"), ["text/plain;charset=utf-8"] = Encoding.UTF8.GetBytes("first") }))
            Equal<string?>("first", await helper.GetTextAsync(), "text/plain;charset=utf-8 is preferred");
        using (var owner = Owner.Start(server.Display, new() { ["STRING"] = [0x63, 0x61, 0x66, 0xe9] }))
            Equal<string?>("café", await helper.GetTextAsync(), "STRING is Latin-1");
        using (var owner = Owner.Start(server.Display, new() { ["UTF8_STRING"] = Encoding.UTF8.GetBytes("no targets") }, refuseTargets: true))
            Equal<string?>("no targets", await helper.GetTextAsync(), "an owner refusing TARGETS still serves UTF8_STRING");
        using (var owner = Owner.Start(server.Display, new() { ["image/png"] = Png }))
        {
            Equal<string?>(null, await helper.GetTextAsync(), "an image-only owner has no text");
            var (imageAvailable, image) = await helper.GetImageAsync();
            Check(imageAvailable && image is not null && image.AsSpan().SequenceEqual(Png), "image/png bytes");
        }
        using (var owner = Owner.Start(server.Display, new() { ["image/jpeg"] = [0xff, 0xd8, 0xff, 0xe0], ["image/png"] = Png }))
        {
            var (_, image) = await helper.GetImageAsync();
            Check(image is not null && image.AsSpan().SequenceEqual(Png), "image/png before image/jpeg");
        }
        // INCR: a transfer larger than the owner's chunk arrives in pieces and is joined.
        var large = string.Concat(Enumerable.Range(0, 30_000).Select(index => $"line {index:D5}\n"));
        using (var owner = Owner.Start(server.Display, new() { ["UTF8_STRING"] = Encoding.UTF8.GetBytes(large) }))
        {
            Equal(large.Length, (await helper.GetTextAsync())?.Length ?? -1, "INCR text length");
            Check(owner.IncrTransfers > 0, "the owner used INCR");
        }
        // The source fallbacks: the commands fail, so readClipboardText and readClipboardImage use the native reader.
        using (var owner = Owner.Start(server.Display, new() { ["UTF8_STRING"] = Encoding.UTF8.GetBytes("fallback") }))
            Equal<string?>("fallback", await Clipboard.ReadClipboardText(Linux(server.Display)), "readClipboardText native fallback");
        using (var owner = Owner.Start(server.Display, new() { ["image/png"] = Png }))
        {
            var image = await ClipboardImageReader.ReadClipboardImage(Linux(server.Display));
            Check(image is { MimeType: "image/png" } && image.Bytes.AsSpan().SequenceEqual(Png), "readClipboardImage native fallback");
        }
    }

    // A stalled owner: the two-second transfer deadline fails the read ("Could not read X11 clipboard"), within the worker's wait.
    private static async Task XvfbStalledOwner()
    {
        using var server = Xvfb.Start();
        using var owner = Owner.Start(server.Display, [], stall: true);
        var helper = new X11Clipboard(server.Display);
        var clock = Stopwatch.StartNew();
        var error = await Throws(helper.GetTextAsync);
        Equal("Could not read X11 clipboard", error, "stalled transfer error");
        Check(clock.ElapsedMilliseconds is >= 1500 and < 3500, "failed at the deadline: " + clock.ElapsedMilliseconds + " ms");
        Equal<string?>(null, await Clipboard.ReadClipboardText(Linux(server.Display)), "readClipboardText swallows the native error");

        static async Task<string> Throws(Func<Task<string?>> run)
        {
            try { await run(); return "<no error>"; }
            catch (InvalidOperationException failure) { return failure.Message; }
        }
    }

    /// <summary>An isolated Xvfb server on a free display number.</summary>
    private sealed class Xvfb : IDisposable
    {
        private readonly Process _process;
        public string Display { get; }
        private Xvfb(Process process, string display) { _process = process; Display = display; }

        public static Xvfb Start()
        {
            if (!OperatingSystem.IsLinux()) throw new SkipCaseException("X11 cases need Linux.");
            if (!NativeLibrary.TryLoad(X11Clipboard.LibraryName, out var library)) throw new SkipCaseException("libxcb.so.1 is not installed.");
            NativeLibrary.Free(library);
            var xvfb = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(directory => Path.Combine(directory, "Xvfb")).FirstOrDefault(File.Exists);
            if (xvfb is null) throw new SkipCaseException("Xvfb is not installed.");
            // -displayfd: Xvfb picks a free display and writes its number once it accepts connections. Where /tmp/.X11-unix is not
            // writable (WSLg mounts it read-only) the local socket cannot be created, so a loopback TCP server is the fallback.
            return Launch(xvfb, ["-nolisten", "tcp"], "") ?? Launch(xvfb, ["-listen", "tcp", "-nolisten", "unix"], "127.0.0.1")
                ?? throw new InvalidOperationException("Xvfb did not start.");
        }

        private static Xvfb? Launch(string xvfb, string[] listen, string host)
        {
            var info = new ProcessStartInfo(xvfb) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in (string[])["-displayfd", "1", .. listen, "-screen", "0", "64x64x24"]) info.ArgumentList.Add(argument);
            var process = Process.Start(info)!;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            var line = process.StandardOutput.ReadLineAsync();
            if (line.Wait(TimeSpan.FromSeconds(8)) && int.TryParse(line.Result?.Trim(), out var number)) return new(process, host + ":" + number);
            Stop(process);
            return null;
        }

        // SIGTERM, so the server removes its /tmp/.X<n>-lock file (SIGKILL would leave it behind).
        private static void Stop(Process process)
        {
            try
            {
                if (!process.HasExited) _ = kill(process.Id, 15);
                if (!process.WaitForExit(5000)) process.Kill();
            }
            catch (InvalidOperationException) { }
            process.Dispose();
        }

        [DllImport("libc")] private static extern int kill(int pid, int signal);

        public void Dispose() => Stop(_process);
    }

    /// <summary>A CLIPBOARD owner on its own connection and thread: TARGETS lists the served types (or is refused), each type is sent
    /// whole or, above 64 KiB, by INCR; a stalled owner never answers.</summary>
    private sealed class Owner : IDisposable
    {
        private const int Chunk = 64 * 1024;
        private readonly IntPtr _connection;
        private readonly Thread _thread;
        private volatile bool _stop;
        private readonly Dictionary<uint, byte[]> _served = [];
        private readonly uint _window, _clipboard, _targets, _incr;
        private readonly bool _refuseTargets, _stall;
        private readonly List<(uint Requestor, uint Property, uint Type, byte[] Data, int Offset)> _incrTransfers = [];
        public int IncrTransfers { get; private set; }

        private Owner(string display, Dictionary<string, byte[]> served, bool refuseTargets, bool stall)
        {
            _refuseTargets = refuseTargets; _stall = stall;
            // A loaded CI runner can refuse a connection while the server is busy: a few tries, 200 ms apart, before failing.
            int screenNumber = 0;
            for (var attempt = 1; ; attempt++)
            {
                _connection = X.xcb_connect(display, out screenNumber);
                if (X.xcb_connection_has_error(_connection) == 0) break;
                X.xcb_disconnect(_connection);
                if (attempt == 5) throw new InvalidOperationException("Owner connection failed.");
                Thread.Sleep(200);
            }
            var screens = X.xcb_setup_roots_iterator(X.xcb_get_setup(_connection));
            while (screenNumber-- > 0) X.xcb_screen_next(ref screens);
            var root = (uint)Marshal.ReadInt32(screens.Data);
            _window = X.xcb_generate_id(_connection);
            X.xcb_create_window(_connection, 0, _window, root, 0, 0, 1, 1, 0, 1, 0, 0, IntPtr.Zero);
            _clipboard = Atom("CLIPBOARD"); _targets = Atom("TARGETS"); _incr = Atom("INCR");
            foreach (var (name, data) in served) _served[Atom(name)] = data;
            X.xcb_set_selection_owner(_connection, _window, _clipboard, 0);
            Atom("PI_OWNER_SYNC"); // A round trip: ownership is established before the reader asks.
            _thread = new Thread(Serve) { IsBackground = true, Name = "x11-owner" };
            _thread.Start();
        }

        public static Owner Start(string display, Dictionary<string, byte[]> served, bool refuseTargets = false, bool stall = false) =>
            new(display, served, refuseTargets, stall);

        private uint Atom(string name)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            var reply = X.xcb_intern_atom_reply(_connection, X.xcb_intern_atom(_connection, 0, (ushort)bytes.Length, bytes), IntPtr.Zero);
            var atom = (uint)Marshal.ReadInt32(reply, 8);
            X.free(reply);
            return atom;
        }

        private void Serve()
        {
            while (!_stop)
            {
                var generic = X.xcb_poll_for_event(_connection);
                if (generic == IntPtr.Zero)
                {
                    if (X.xcb_connection_has_error(_connection) != 0) return;
                    var descriptor = new X.PollFd { Fd = X.xcb_get_file_descriptor(_connection), Events = 1 };
                    _ = X.poll(ref descriptor, 1, 50);
                    continue;
                }
                try
                {
                    var type = Marshal.ReadByte(generic) & 0x7f;
                    if (type == 30 && !_stall) Request(generic);
                    else if (type == 28) PropertyChanged(generic);
                }
                finally { X.free(generic); }
                X.xcb_flush(_connection);
            }
        }

        // xcb_selection_request_event_t: requestor @12, selection @16, target @20, property @24.
        private void Request(IntPtr request)
        {
            var requestor = (uint)Marshal.ReadInt32(request, 12); var selection = (uint)Marshal.ReadInt32(request, 16);
            var target = (uint)Marshal.ReadInt32(request, 20); var property = (uint)Marshal.ReadInt32(request, 24);
            var answered = property;
            if (target == _targets && !_refuseTargets)
            {
                var atoms = new[] { _targets }.Concat(_served.Keys).SelectMany(BitConverter.GetBytes).ToArray();
                X.xcb_change_property(_connection, 0, requestor, property, 4, 32, (uint)(atoms.Length / 4), atoms);
            }
            else if (target != _targets && _served.TryGetValue(target, out var data))
            {
                if (data.Length > Chunk)
                {
                    IncrTransfers++;
                    X.xcb_change_window_attributes(_connection, requestor, 2048, [1u << 22]);
                    X.xcb_change_property(_connection, 0, requestor, property, _incr, 32, 1, BitConverter.GetBytes(data.Length));
                    _incrTransfers.Add((requestor, property, target, data, 0));
                }
                else X.xcb_change_property(_connection, 0, requestor, property, target, 8, (uint)data.Length, data);
            }
            else answered = 0;
            var notify = new byte[32];
            notify[0] = 31;
            BitConverter.GetBytes(requestor).CopyTo(notify, 8); BitConverter.GetBytes(selection).CopyTo(notify, 12);
            BitConverter.GetBytes(target).CopyTo(notify, 16); BitConverter.GetBytes(answered).CopyTo(notify, 20);
            X.xcb_send_event(_connection, 0, requestor, 0, notify);
        }

        // xcb_property_notify_event_t: window @4, atom @8, state @16 (1: deleted). Each deletion asks for the next INCR chunk; the
        // empty chunk ends the transfer.
        private void PropertyChanged(IntPtr notify)
        {
            if (Marshal.ReadByte(notify, 16) != 1) return;
            var window = (uint)Marshal.ReadInt32(notify, 4); var atom = (uint)Marshal.ReadInt32(notify, 8);
            var index = _incrTransfers.FindIndex(transfer => transfer.Requestor == window && transfer.Property == atom);
            if (index < 0) return;
            var transfer = _incrTransfers[index];
            var length = Math.Min(Chunk, transfer.Data.Length - transfer.Offset);
            var chunk = transfer.Data.AsSpan(transfer.Offset, Math.Max(0, length)).ToArray();
            X.xcb_change_property(_connection, 0, window, atom, transfer.Type, 8, (uint)chunk.Length, chunk);
            if (transfer.Offset >= transfer.Data.Length) _incrTransfers.RemoveAt(index);
            else _incrTransfers[index] = transfer with { Offset = transfer.Offset + chunk.Length };
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(5000);
            X.xcb_disconnect(_connection);
        }
    }

    private static class X
    {
        private const string Lib = X11Clipboard.LibraryName;
        [StructLayout(LayoutKind.Sequential)] internal struct ScreenIterator { public IntPtr Data; public int Rem; public int Index; }
        [StructLayout(LayoutKind.Sequential)] internal struct PollFd { public int Fd; public short Events; public short Revents; }
        [DllImport(Lib)] internal static extern IntPtr xcb_connect([MarshalAs(UnmanagedType.LPUTF8Str)] string display, out int screen);
        [DllImport(Lib)] internal static extern int xcb_connection_has_error(IntPtr connection);
        [DllImport(Lib)] internal static extern void xcb_disconnect(IntPtr connection);
        [DllImport(Lib)] internal static extern IntPtr xcb_get_setup(IntPtr connection);
        [DllImport(Lib)] internal static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);
        [DllImport(Lib)] internal static extern void xcb_screen_next(ref ScreenIterator iterator);
        [DllImport(Lib)] internal static extern uint xcb_generate_id(IntPtr connection);
        [DllImport(Lib)] internal static extern int xcb_flush(IntPtr connection);
        [DllImport(Lib)] internal static extern int xcb_get_file_descriptor(IntPtr connection);
        [DllImport(Lib)] internal static extern IntPtr xcb_poll_for_event(IntPtr connection);
        [DllImport(Lib)] internal static extern uint xcb_create_window(IntPtr connection, byte depth, uint window, uint parent, short x, short y,
            ushort width, ushort height, ushort borderWidth, ushort windowClass, uint visual, uint valueMask, IntPtr valueList);
        [DllImport(Lib)] internal static extern uint xcb_change_window_attributes(IntPtr connection, uint window, uint valueMask, uint[] valueList);
        [DllImport(Lib)] internal static extern uint xcb_intern_atom(IntPtr connection, byte onlyIfExists, ushort nameLength, byte[] name);
        [DllImport(Lib)] internal static extern IntPtr xcb_intern_atom_reply(IntPtr connection, uint cookie, IntPtr error);
        [DllImport(Lib)] internal static extern uint xcb_set_selection_owner(IntPtr connection, uint owner, uint selection, uint time);
        [DllImport(Lib)] internal static extern uint xcb_change_property(IntPtr connection, byte mode, uint window, uint property, uint type, byte format,
            uint dataLength, byte[] data);
        [DllImport(Lib)] internal static extern uint xcb_send_event(IntPtr connection, byte propagate, uint destination, uint eventMask, byte[] @event);
        [DllImport("libc")] internal static extern int poll(ref PollFd descriptors, ulong count, int timeout);
        [DllImport("libc")] internal static extern void free(IntPtr pointer);
    }
}
