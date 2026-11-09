using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace PiSharp.Env.Daemon;

/// <summary>Uses the opened descriptor for fstat, so rename and replacement cannot change its identity.</summary>
internal static class DaemonMetadata
{
    public static bool CreateTempDirectory(string path)
    {
        var success = OperatingSystem.IsWindows() ? CreateDirectoryW(path, IntPtr.Zero) : mkdir(path, 448) == 0;
        if (success) return true;
        var code = Marshal.GetLastPInvokeError();
        if (OperatingSystem.IsWindows() ? code is 80 or 183 : code == 17) return false;
        throw NativeError(path);
    }
    public static FileStream OpenRead(string path, bool readMode, bool noFollow)
    {
        SafeFileHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateFileW(path, 0x80000000, 7, IntPtr.Zero, 3, 0x02000000u | (noFollow && !readMode ? 0x00200000u : 0u), IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw NativeError(path); }
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Binary readers must never block on a FIFO and must reject the final symlink atomically.
            var flags = readMode ? 0 : OperatingSystem.IsMacOS() ? 4 | (noFollow ? 0x100 : 0) : 0x800 | (noFollow ? 0x20000 : 0);
            var fd = open(path, flags);
            if (fd < 0)
            {
                var error = NativeError(path);
                if (noFollow && error.Code == "ELOOP") throw new EnvDaemonException("SYMLINK", "Refusing to follow a symbolic link", path);
                throw error;
            }
            handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        }
        else handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (!readMode)
            {
                var kind = HandleInfo(handle, path)["kind"]!.GetValue<string>();
                if (noFollow && kind == "symlink") throw new EnvDaemonException("SYMLINK", "Refusing to follow a symbolic link", path);
                if (kind == "directory") throw new EnvDaemonException("EISDIR", "Is a directory", path);
                if (kind != "file") throw new EnvDaemonException("NOT_REGULAR", "Not a regular file", path);
            }
            return new FileStream(handle, FileAccess.Read, 1, isAsync: false);
        }
        catch { handle.Dispose(); throw; }
    }
    public static JsonObject PathInfo(string path, bool follow = false)
    {
        if (OperatingSystem.IsLinux())
        {
            var buffer = new byte[256];
            if (statx(-100, path, follow ? 0 : 0x100, 0x7ff, buffer) != 0) throw NativeError(path);
            return LinuxInfo(path, buffer);
        }
        if (OperatingSystem.IsMacOS())
        {
            var buffer = new byte[256];
            var x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
            var result = follow ? x64 ? stat64(path, buffer) : stat(path, buffer) : x64 ? lstat64(path, buffer) : lstat(path, buffer);
            if (result != 0) throw NativeError(path);
            return MacInfo(path, buffer);
        }
        if (OperatingSystem.IsWindows())
        {
            using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000u | (follow ? 0u : 0x00200000u), IntPtr.Zero);
            if (handle.IsInvalid) throw NativeError(path);
            return WindowsInfo(path, handle);
        }
        var info = new FileInfo(path); if (!info.Exists) throw new FileNotFoundException("No such file", path);
        return Build(path, info.LinkTarget is not null && !follow ? "symlink" : (info.Attributes & FileAttributes.Directory) != 0 ? "directory" : "file", info.Length,
            (info.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond, (info.LastWriteTimeUtc.Ticks % TimeSpan.TicksPerSecond) * 100, 0, 0);
    }
    public static JsonObject HandleInfo(SafeFileHandle handle, string path)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (OperatingSystem.IsWindows()) return WindowsInfo(path, handle);
            var buffer = new byte[256];
            var fd = handle.DangerousGetHandle().ToInt32();
            if (OperatingSystem.IsLinux())
            {
                if (statx(fd, "", 0x1000, 0x7ff, buffer) != 0) throw NativeError(path);
                return LinuxInfo(path, buffer);
            }
            if (OperatingSystem.IsMacOS())
            {
                if ((RuntimeInformation.ProcessArchitecture == Architecture.X64 ? fstat64(fd, buffer) : fstat(fd, buffer)) != 0) throw NativeError(path);
                return MacInfo(path, buffer);
            }
            return PathInfo(path, true);
        }
        finally { if (added) handle.DangerousRelease(); }
    }
    private static JsonObject LinuxInfo(string path, byte[] b)
    {
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28));
        var major = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(136));
        var minor = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(140));
        var dev = ((major & 0xfff) << 8) | (minor & 0xff) | ((major & ~0xfffUL) << 32) | ((minor & ~0xffUL) << 12);
        return Build(path, Kind(mode), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(40)), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(112)),
            BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(120)), dev, BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(32)));
    }
    private static JsonObject MacInfo(string path, byte[] b) => Build(path, Kind(BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(4))),
        BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(96)), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(48)), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(56)),
        BinaryPrimitives.ReadUInt32LittleEndian(b), BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(8)));
    private static JsonObject WindowsInfo(string path, SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw NativeError(path);
        var time = ((long)info.WriteHigh << 32) | info.WriteLow;
        var unixTicks = time - 116444736000000000L;
        var sec = Math.DivRem(unixTicks, TimeSpan.TicksPerSecond, out var sub);
        if (sub < 0) { sec--; sub += TimeSpan.TicksPerSecond; }
        return Build(path, (info.Attributes & 0x400) != 0 ? "symlink" : (info.Attributes & 0x10) != 0 ? "directory" : "file",
            ((long)info.SizeHigh << 32) | info.SizeLow, sec, sub * 100, info.Volume, ((ulong)info.IndexHigh << 32) | info.IndexLow);
    }
    private static JsonObject Build(string path, string kind, long size, long sec, long nanos, ulong dev, ulong ino) => new()
    { ["name"] = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), ["kind"] = kind, ["size"] = size, ["mtimeSec"] = sec, ["mtimeNsec"] = nanos, ["dev"] = dev, ["ino"] = ino };
    private static string Kind(int mode) => (mode & 0xf000) switch { 0x8000 => "file", 0x4000 => "directory", 0xa000 => "symlink", _ => "other" };
    private static EnvDaemonException NativeError(string path)
    {
        var error = Marshal.GetLastPInvokeError();
        var code = OperatingSystem.IsWindows() ? error switch
        { 2 or 3 => "ENOENT", 5 => "EACCES", 32 => "EBUSY", 80 or 183 => "EEXIST", 267 => "ENOTDIR", _ => "unknown" } : error switch
        { 1 or 13 => "EACCES", 2 => "ENOENT", 9 => "EBADF", 20 => "ENOTDIR", 21 => "EISDIR", 24 => "EMFILE", 40 or 62 => "ELOOP", _ => "unknown" };
        return new(code, new Win32Exception(error).Message, path, "stat");
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path, IntPtr attributes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mask, byte[] buffer);
    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "stat", SetLastError = true)] private static extern int stat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)] private static extern int lstat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int fstat(int fd, byte[] buffer);
    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)] private static extern int stat64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)] private static extern int lstat64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)] private static extern int fstat64(int fd, byte[] buffer);
}
