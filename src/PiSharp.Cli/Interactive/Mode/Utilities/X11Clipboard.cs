// Pi abe508e1b89912adde45528136c3221eb69acdd7 (MIT): packages/tui/native/linux/src/linux-platform-x11.c,
// packages/tui/native/linux/src/clipboard-worker.h, packages/tui/native/clipboard.h and tui/src/native-platform.ts (getNativeClipboard).
// Upstream's Linux helper is an N-API addon over libxcb.so.1; this is the same reader over the same library through P/Invoke, with no
// native build of its own. As upstream, it only reads (text and images): Linux writes keep using wl-copy, xclip and xsel, whose
// processes retain selection ownership after the copy returns.
using System.Runtime.InteropServices;
using System.Text;

namespace PiSharp.Cli.Interactive.Mode.Utilities;

/// <summary>The source Linux X11 clipboard helper: CLIPBOARD reads through libxcb with a two-second transfer deadline shared by
/// discovery, replies and INCR chunks, a 50 MiB cap, TARGETS negotiation in the source's preference order (and UTF8_STRING from owners
/// that refuse TARGETS), STRING decoded as Latin-1. One read runs at a time on a private thread; the caller waits at most three
/// seconds, and while an earlier read is still running other reads are unavailable. A missing libxcb or an unreachable display is
/// unavailable (never a crash); a failed transfer throws "Could not read X11 clipboard".</summary>
internal sealed class X11Clipboard(string display) : INativeClipboard
{
    internal const int MaxClipboardBytes = 50 * 1024 * 1024;
    internal const int TransferTimeoutMs = 2000;
    internal const int WorkerTimeoutMs = 3000;
    internal const string LibraryName = "libxcb.so.1";
    private static readonly string[] TextTypes = ["text/plain;charset=utf-8", "text/plain;charset=UTF-8", "UTF8_STRING", "text/plain", "STRING"];
    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp", "image/tiff"];
    private static readonly object WorkerGate = new();
    private static bool s_workerBusy;
    private static readonly Lazy<bool> Library = new(() =>
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad(LibraryName, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    });

    private enum Outcome { Unavailable, Empty, Data, Failed }
    private readonly record struct ReadResult(Outcome Outcome, byte[]? Data = null, bool Latin1 = false);

    /// <summary>Source getNativeClipboard on Linux: no helper without DISPLAY or outside x64 and arm64; the display opens per read.</summary>
    public static INativeClipboard? Create(ClipboardEnvironment environment)
    {
        var display = environment.Env("DISPLAY");
        if (string.IsNullOrEmpty(display)) return null;
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) return null;
        return new X11Clipboard(display);
    }

    public async Task<string?> GetTextAsync()
    {
        var result = await ReadAsync(image: false).ConfigureAwait(false);
        return result.Outcome switch
        {
            Outcome.Failed => throw new InvalidOperationException("Could not read X11 clipboard"),
            Outcome.Data => result.Latin1 ? Encoding.Latin1.GetString(result.Data!) : Encoding.UTF8.GetString(result.Data!),
            _ => null,
        };
    }

    public async Task<(bool Available, byte[]? Bytes)> GetImageAsync()
    {
        var result = await ReadAsync(image: true).ConfigureAwait(false);
        return result.Outcome switch
        {
            Outcome.Failed => throw new InvalidOperationException("Could not read X11 clipboard"),
            Outcome.Data => (true, result.Data),
            Outcome.Empty => (true, null),
            _ => (false, null),
        };
    }

    /// <summary>clipboard-worker.h clipboard_execute: one private reader at a time, a bounded wait for it, and a late result discarded.</summary>
    private async Task<ReadResult> ReadAsync(bool image)
    {
        if (!Library.Value) return new(Outcome.Unavailable);
        lock (WorkerGate)
        {
            if (s_workerBusy) return new(Outcome.Unavailable);
            s_workerBusy = true;
        }
        var completion = new TaskCompletionSource<ReadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            ReadResult result;
            try { result = Read(display, image); }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { result = new(Outcome.Unavailable); }
            finally { lock (WorkerGate) s_workerBusy = false; }
            completion.TrySetResult(result);
        }) { IsBackground = true, Name = "pi.clipboard" };
        try { thread.Start(); }
        catch (Exception) { lock (WorkerGate) s_workerBusy = false; return new(Outcome.Unavailable); }
        var finished = await Task.WhenAny(completion.Task, Task.Delay(WorkerTimeoutMs)).ConfigureAwait(false);
        return finished == completion.Task ? await completion.Task.ConfigureAwait(false) : new(Outcome.Unavailable);
    }

    /// <summary>read_clipboard: an unopened display is unavailable; a failed transfer is an error.</summary>
    private static ReadResult Read(string display, bool image)
    {
        using var session = new Session();
        if (!session.Open(display)) return new(Outcome.Unavailable);
        var contents = new Property();
        if (!session.ReadSelection(image, contents)) return new(Outcome.Failed);
        if (!contents.HasData) return new(Outcome.Empty);
        return new(Outcome.Data, contents.Bytes.ToArray(), !image && contents.Type == Xcb.AtomString);
    }

    private sealed class Property
    {
        public readonly MemoryStream Bytes = new();
        public bool HasData;
        public uint Items, Type;
        public byte Format;

        public bool Append(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length > MaxClipboardBytes - Bytes.Length) return false;
            Bytes.Write(bytes); HasData = true;
            return true;
        }

        /// <summary>append_property: every INCR chunk has the first chunk's type and format and a consistent length.</summary>
        public bool AppendChunk(Property chunk)
        {
            if (chunk.Format is not (8 or 16 or 32) || chunk.Type == Xcb.None || (ulong)chunk.Items * (ulong)(chunk.Format / 8) != (ulong)chunk.Bytes.Length ||
                Type != Xcb.None && (Type != chunk.Type || Format != chunk.Format) || chunk.Items > uint.MaxValue - Items) return false;
            if (!Append(chunk.Bytes.GetBuffer().AsSpan(0, (int)chunk.Bytes.Length))) return false;
            Type = chunk.Type; Format = chunk.Format; Items += chunk.Items;
            return true;
        }
    }

    /// <summary>x11_clipboard: the connection, an unmapped 1x1 window receiving property changes, the atoms and the shared deadline.</summary>
    private sealed class Session : IDisposable
    {
        private IntPtr _connection;
        private uint _window, _clipboard, _property, _targets, _incr;
        private long _deadline;

        public bool Open(string display)
        {
            _deadline = Environment.TickCount64 + TransferTimeoutMs;
            _connection = Xcb.xcb_connect(display, out var screenNumber);
            if (_connection == IntPtr.Zero || Xcb.xcb_connection_has_error(_connection) != 0) return false;
            var screens = Xcb.xcb_setup_roots_iterator(Xcb.xcb_get_setup(_connection));
            while (screenNumber-- > 0 && screens.Rem != 0) Xcb.xcb_screen_next(ref screens);
            if (screens.Rem == 0) return false;
            var root = (uint)Marshal.ReadInt32(screens.Data); // xcb_screen_t.root is the first field.
            _window = Xcb.xcb_generate_id(_connection);
            var mask = new[] { Xcb.EventMaskPropertyChange };
            Xcb.xcb_create_window(_connection, 0, _window, root, 0, 0, 1, 1, 0, Xcb.WindowClassInputOutput, 0, Xcb.CwEventMask, mask);
            _clipboard = InternAtom("CLIPBOARD"); _property = InternAtom("PI_CLIPBOARD");
            _targets = InternAtom("TARGETS"); _incr = InternAtom("INCR");
            return _clipboard != 0 && _property != 0 && _targets != 0 && _incr != 0;
        }

        public void Dispose()
        {
            if (_connection != IntPtr.Zero) Xcb.xcb_disconnect(_connection);
            _connection = IntPtr.Zero;
        }

        private bool WaitForInput()
        {
            while (true)
            {
                var remaining = _deadline - Environment.TickCount64;
                if (remaining <= 0 || Xcb.xcb_connection_has_error(_connection) != 0) return false;
                var descriptor = new Xcb.PollFd { Fd = Xcb.xcb_get_file_descriptor(_connection), Events = Xcb.PollIn };
                var ready = Xcb.poll(ref descriptor, 1, (int)remaining);
                if (ready >= 0) return ready > 0 && (descriptor.Revents & Xcb.PollIn) != 0;
                if (Marshal.GetLastPInvokeError() != 4) return false; // EINTR
            }
        }

        private IntPtr WaitForReply(uint sequence)
        {
            if (Xcb.xcb_flush(_connection) <= 0) return IntPtr.Zero;
            while (Environment.TickCount64 < _deadline)
            {
                if (Xcb.xcb_poll_for_reply(_connection, sequence, out var reply, out var error) != 0)
                {
                    Xcb.free(error);
                    return reply;
                }
                if (!WaitForInput()) break;
            }
            Xcb.xcb_discard_reply(_connection, sequence);
            return IntPtr.Zero;
        }

        /// <summary>The next event of this type (others are dropped), or zero at the deadline.</summary>
        private IntPtr WaitForEvent(byte type)
        {
            if (Xcb.xcb_flush(_connection) <= 0) return IntPtr.Zero;
            while (Environment.TickCount64 < _deadline)
            {
                var generic = Xcb.xcb_poll_for_event(_connection);
                if (generic != IntPtr.Zero)
                {
                    if ((Marshal.ReadByte(generic) & 0x7f) == type) return generic;
                    Xcb.free(generic);
                }
                else if (!WaitForInput()) break;
            }
            return IntPtr.Zero;
        }

        private bool ReadProperty(bool delete, Property result)
        {
            var cookie = Xcb.xcb_get_property(_connection, (byte)(delete ? 1 : 0), _window, _property, 0, 0, MaxClipboardBytes / 4);
            var reply = WaitForReply(cookie);
            if (reply == IntPtr.Zero) return false;
            try
            {
                // xcb_get_property_reply_t: format @1, length @4, type @8, bytes_after @12, value_len @16.
                var format = Marshal.ReadByte(reply, 1);
                var replyLength = (uint)Marshal.ReadInt32(reply, 4);
                var type = (uint)Marshal.ReadInt32(reply, 8);
                var bytesAfter = (uint)Marshal.ReadInt32(reply, 12);
                var valueLength = (uint)Marshal.ReadInt32(reply, 16);
                var length = (ulong)valueLength * (ulong)(format / 8);
                if (bytesAfter != 0 || type == Xcb.None || format is not (8 or 16 or 32) || length > MaxClipboardBytes || length > (ulong)replyLength * 4)
                    return false;
                result.Type = type; result.Format = format; result.Items = valueLength;
                var bytes = new byte[(int)length];
                if (length > 0) Marshal.Copy(Xcb.xcb_get_property_value(reply), bytes, 0, bytes.Length);
                return result.Append(bytes);
            }
            finally { Xcb.free(reply); }
        }

        /// <summary>request_selection: ConvertSelection into PI_CLIPBOARD, then the property, or its INCR chunks until the empty one.
        /// True with nothing appended when there is no owner or the owner refuses the target.</summary>
        private bool RequestSelection(uint target, Property result)
        {
            Xcb.xcb_delete_property(_connection, _window, _property);
            Xcb.xcb_convert_selection(_connection, _window, _clipboard, target, _property, 0);
            while (true)
            {
                var notify = WaitForEvent(Xcb.SelectionNotify);
                if (notify == IntPtr.Zero) return false;
                // xcb_selection_notify_event_t: selection @12, target @16, property @20.
                var matches = (uint)Marshal.ReadInt32(notify, 12) == _clipboard && (uint)Marshal.ReadInt32(notify, 16) == target;
                var received = (uint)Marshal.ReadInt32(notify, 20) != Xcb.None;
                Xcb.free(notify);
                if (!matches) continue;
                if (!received) return true;
                if (!ReadProperty(false, result)) return false;
                break;
            }
            if (result.Type != _incr) return true;

            result.Bytes.SetLength(0); result.HasData = false; result.Type = Xcb.None; result.Format = 0; result.Items = 0;
            Xcb.xcb_delete_property(_connection, _window, _property);
            while (true)
            {
                var notify = WaitForEvent(Xcb.PropertyNotify);
                if (notify == IntPtr.Zero) return false;
                // xcb_property_notify_event_t: atom @8, state @16.
                var matches = (uint)Marshal.ReadInt32(notify, 8) == _property && Marshal.ReadByte(notify, 16) == Xcb.PropertyNewValue;
                Xcb.free(notify);
                if (!matches) continue;
                var chunk = new Property();
                if (!ReadProperty(true, chunk)) return false;
                if (!result.AppendChunk(chunk)) return false;
                if (chunk.Items == 0) return true;
            }
        }

        private uint InternAtom(string name)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            var reply = WaitForReply(Xcb.xcb_intern_atom(_connection, 0, (ushort)bytes.Length, bytes));
            if (reply == IntPtr.Zero) return Xcb.None;
            var atom = (uint)Marshal.ReadInt32(reply, 8);
            Xcb.free(reply);
            return atom;
        }

        /// <summary>preferred_target: the first wanted type the owner's TARGETS offers. An owner refusing TARGETS still serves
        /// UTF8_STRING text; no match leaves the target None (an empty read).</summary>
        private bool PreferredTarget(bool image, out uint target)
        {
            target = Xcb.None;
            var types = image ? ImageTypes : TextTypes;
            var wanted = new uint[types.Length];
            for (var index = 0; index < types.Length; index++)
                if ((wanted[index] = InternAtom(types[index])) == Xcb.None) return false;
            var targets = new Property();
            var received = RequestSelection(_targets, targets);
            var valid = targets.Type == Xcb.AtomAtom && targets.Format == 32 && targets.Bytes.Length % 4 == 0 && targets.Items == targets.Bytes.Length / 4;
            if (received && valid)
            {
                var offered = MemoryMarshal.Cast<byte, uint>(targets.Bytes.GetBuffer().AsSpan(0, (int)targets.Bytes.Length));
                foreach (var candidate in wanted)
                    if (offered.Contains(candidate)) { target = candidate; return true; }
            }
            if (received && !image && targets.Type == Xcb.None)
            {
                target = InternAtom("UTF8_STRING");
                return target != Xcb.None;
            }
            return received && (valid || targets.Type == Xcb.None);
        }

        public bool ReadSelection(bool image, Property result) =>
            PreferredTarget(image, out var target) && (target == Xcb.None || RequestSelection(target, result));
    }

    /// <summary>The libxcb and libc entry points the helper uses (xcb.h, xproto.h).</summary>
    internal static class Xcb
    {
        internal const uint None = 0, AtomAtom = 4, AtomString = 31, EventMaskPropertyChange = 1u << 22, CwEventMask = 2048;
        internal const ushort WindowClassInputOutput = 1;
        internal const byte SelectionNotify = 31, PropertyNotify = 28, PropertyNewValue = 0;
        internal const short PollIn = 1;

        [StructLayout(LayoutKind.Sequential)] internal struct ScreenIterator { public IntPtr Data; public int Rem; public int Index; }
        [StructLayout(LayoutKind.Sequential)] internal struct PollFd { public int Fd; public short Events; public short Revents; }

        [DllImport(LibraryName)] internal static extern IntPtr xcb_connect([MarshalAs(UnmanagedType.LPUTF8Str)] string? display, out int screen);
        [DllImport(LibraryName)] internal static extern int xcb_connection_has_error(IntPtr connection);
        [DllImport(LibraryName)] internal static extern void xcb_disconnect(IntPtr connection);
        [DllImport(LibraryName)] internal static extern IntPtr xcb_get_setup(IntPtr connection);
        [DllImport(LibraryName)] internal static extern ScreenIterator xcb_setup_roots_iterator(IntPtr setup);
        [DllImport(LibraryName)] internal static extern void xcb_screen_next(ref ScreenIterator iterator);
        [DllImport(LibraryName)] internal static extern uint xcb_generate_id(IntPtr connection);
        [DllImport(LibraryName)] internal static extern int xcb_flush(IntPtr connection);
        [DllImport(LibraryName)] internal static extern int xcb_get_file_descriptor(IntPtr connection);
        [DllImport(LibraryName)] internal static extern IntPtr xcb_poll_for_event(IntPtr connection);
        [DllImport(LibraryName)] internal static extern int xcb_poll_for_reply(IntPtr connection, uint request, out IntPtr reply, out IntPtr error);
        [DllImport(LibraryName)] internal static extern void xcb_discard_reply(IntPtr connection, uint sequence);
        [DllImport(LibraryName)] internal static extern uint xcb_create_window(IntPtr connection, byte depth, uint window, uint parent, short x, short y,
            ushort width, ushort height, ushort borderWidth, ushort windowClass, uint visual, uint valueMask, uint[] valueList);
        [DllImport(LibraryName)] internal static extern uint xcb_intern_atom(IntPtr connection, byte onlyIfExists, ushort nameLength, byte[] name);
        [DllImport(LibraryName)] internal static extern uint xcb_delete_property(IntPtr connection, uint window, uint property);
        [DllImport(LibraryName)] internal static extern uint xcb_convert_selection(IntPtr connection, uint requestor, uint selection, uint target, uint property, uint time);
        [DllImport(LibraryName)] internal static extern uint xcb_get_property(IntPtr connection, byte delete, uint window, uint property, uint type, uint longOffset, uint longLength);
        [DllImport(LibraryName)] internal static extern IntPtr xcb_get_property_value(IntPtr reply);
        [DllImport("libc", SetLastError = true)] internal static extern int poll(ref PollFd descriptors, ulong count, int timeout);
        [DllImport("libc")] internal static extern void free(IntPtr pointer);
    }
}
