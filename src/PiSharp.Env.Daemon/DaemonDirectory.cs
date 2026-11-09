using System.Collections;
using System.Runtime.InteropServices;
using System.Text;

namespace PiSharp.Env.Daemon;

internal sealed record DirectoryName(string Name, byte[]? Raw);

/// <summary>Retains original Unix name bytes while applying Node's replacement decoding to the name used for lstat.</summary>
internal sealed class DaemonDirectory : IEnumerator<DirectoryName>
{
    private readonly IEnumerator<string>? managed;
    private readonly object gate = new();
    private bool disposed;
    private IntPtr native;
    public DaemonDirectory(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            native = opendir(path);
            if (native == IntPtr.Zero) throw new IOException($"Cannot open directory '{path}'", Marshal.GetLastPInvokeError());
        }
        else managed = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
    }
    public DirectoryName Current { get; private set; } = null!;
    object IEnumerator.Current => Current;
    public bool MoveNext()
    {
        lock (gate) return !disposed && ReadNext();
    }
    private bool ReadNext()
    {
        if (managed is not null)
        {
            if (!managed.MoveNext()) return false;
            Current = new(Path.GetFileName(managed.Current), null); return true;
        }
        if (native == IntPtr.Zero) return false;
        while (true)
        {
            Marshal.SetLastPInvokeError(0);
            var entry = readdir(native);
            if (entry == IntPtr.Zero)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 0) throw new IOException("Cannot read directory", error);
                return false;
            }
            var nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;
            var recordLength = (ushort)Marshal.ReadInt16(entry, 16);
            var count = 0;
            while (nameOffset + count < recordLength && Marshal.ReadByte(entry, nameOffset + count) != 0) count++;
            var raw = new byte[count]; Marshal.Copy(entry + nameOffset, raw, 0, count);
            var name = Encoding.UTF8.GetString(raw);
            if (name is "." or "..") continue;
            Current = new(name, raw.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(name)) ? null : raw);
            return true;
        }
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            managed?.Dispose();
            var value = Interlocked.Exchange(ref native, IntPtr.Zero);
            if (value != IntPtr.Zero) closedir(value);
        }
    }
    [DllImport("libc", SetLastError = true)] private static extern IntPtr opendir([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("libc", SetLastError = true)] private static extern IntPtr readdir(IntPtr directory);
    [DllImport("libc")] private static extern int closedir(IntPtr directory);
}
