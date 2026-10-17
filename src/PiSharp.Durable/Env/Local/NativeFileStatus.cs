// Pi 06a466935536f8c9bcacb2d5efc0fc021be1bdff (MIT): packages/durable/src/env/node.ts and node-watch.ts (the parts of Node's
// `stat`/`lstat` and `statfs` that System.IO does not expose: a file's device and inode, its exact file type, and a file system's type).
// Node reads them through libuv; here they come from the operating system directly: GetFileInformationByHandle on Windows (volume serial
// and file index, as libuv reports them), statx(2) on Linux (glibc 2.28+, musl 1.2.5+; its layout is the same on every architecture),
// lstat(2)/stat(2) on macOS (the 64-bit-inode layout), and statfs(2) on Linux. Where none is available, callers fall back to what
// System.IO reports.
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiSharp.Durable.Env.Local;

/// <summary>The type of a file as the operating system reports it.</summary>
internal enum NativeFileType { Regular, Directory, Symlink, Other }

/// <summary>A file's identity and type, from the operating system.</summary>
internal readonly record struct NativeFileStatus(long Device, ulong Inode, NativeFileType Type);

internal static class NativeFiles
{
    private static int linuxStatx = OperatingSystem.IsLinux() ? 1 : 0;

    /// <summary>Whether this platform reports file identities.</summary>
    public static bool Available => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || Volatile.Read(ref linuxStatx) == 1;

    /// <summary>The identity and type of <paramref name="path"/>, following a final symbolic link when <paramref name="follow"/>; null when
    /// the path cannot be examined or this platform has no way to.</summary>
    public static NativeFileStatus? TryGet(string path, bool follow)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return Windows(path, follow);
            if (OperatingSystem.IsLinux()) return Volatile.Read(ref linuxStatx) == 1 ? Linux(path, follow) : null;
            if (OperatingSystem.IsMacOS()) return MacOS(path, follow);
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            Volatile.Write(ref linuxStatx, 0);
        }
        return null;
    }

    private static NativeFileType UnixType(int mode) => (mode & 0xF000) switch
    {
        0x8000 => NativeFileType.Regular,
        0x4000 => NativeFileType.Directory,
        0xA000 => NativeFileType.Symlink,
        _ => NativeFileType.Other,
    };

    private static NativeFileStatus? Linux(string path, bool follow)
    {
        const int AtFdcwd = -100, AtSymlinkNoFollow = 0x100, StatxBasicStats = 0x7ff;
        var buffer = new byte[256];
        if (statx(AtFdcwd, path, follow ? 0 : AtSymlinkNoFollow, StatxBasicStats, buffer) != 0) return null;
        var mode = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(28));
        var inode = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(32));
        var device = ((long)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(136)) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(140));
        return new NativeFileStatus(device, inode, UnixType(mode));
    }

    private static NativeFileStatus? MacOS(string path, bool follow)
    {
        var buffer = new byte[256];
        var x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
        var result = follow ? x64 ? stat_x64(path, buffer) : stat_arm64(path, buffer) : x64 ? lstat_x64(path, buffer) : lstat_arm64(path, buffer);
        if (result != 0) return null;
        return new NativeFileStatus(BinaryPrimitives.ReadInt32LittleEndian(buffer), BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(8)),
            UnixType(BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(4))));
    }

    private static NativeFileStatus? Windows(string path, bool follow)
    {
        const uint ReadAttributes = 0x80, ShareAll = 7, OpenExisting = 3, BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        using var handle = CreateFileW(path, ReadAttributes, ShareAll, IntPtr.Zero, OpenExisting,
            BackupSemantics | (follow ? 0 : OpenReparsePoint), IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information)) return null;
        var type = (information.Attributes & 0x10) != 0 ? NativeFileType.Directory : NativeFileType.Regular;
        return new NativeFileStatus(information.VolumeSerialNumber, ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow, type);
    }

    /// <summary>Linux <c>statfs(2)</c>'s <c>f_type</c> of the file system holding <paramref name="path"/>, or null.</summary>
    public static uint? FileSystemType(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            // f_type is the first field, a long on 64-bit and an int on 32-bit; only its low 32 bits hold a magic number.
            var buffer = new byte[512];
            if (statfs(path, buffer) != 0) return null;
            return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mask, byte[] buffer);

    [DllImport("libc", EntryPoint = "statfs", SetLastError = true)]
    private static extern int statfs([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "stat", SetLastError = true)]
    private static extern int stat_arm64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int lstat_arm64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "stat$INODE64", SetLastError = true)]
    private static extern int stat_x64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)]
    private static extern int lstat_x64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);
}
