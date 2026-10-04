using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace PiSharp.Cli.Output;

/// <summary>Opens compiled CLI output without treating a closed redirected Windows pipe as a successful write.</summary>
public static class StandardOutputStream
{
    /// <summary>
    /// The caller owns the returned stream. Windows pipe/file output owns a noninheritable duplicate only;
    /// console devices and other platforms retain the framework standard-output behavior.
    /// </summary>
    public static Stream Open()
    {
        if (!OperatingSystem.IsWindows() || !Console.IsOutputRedirected) return Console.OpenStandardOutput();
        var handle = Native.GetStdHandle(unchecked((uint)-11));
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw Unavailable();
        var type = Native.GetFileType(handle);
        if (type == 2) return Console.OpenStandardOutput();
        if (type is not (1 or 3)) throw Unavailable();
        using var borrowed = new SafeFileHandle(handle, ownsHandle: false);
        return DuplicateRedirectedOutput(borrowed);
    }

    /// <summary>
    /// Owns an unbuffered file stream over a same-access duplicate of a trusted writable pipe/file handle.
    /// The caller retains the original handle and must serialize writes, await admitted I/O, then dispose the stream.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static FileStream DuplicateRedirectedOutput(SafeHandle borrowedHandle)
    {
        ArgumentNullException.ThrowIfNull(borrowedHandle);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows redirected output is required.");
        var held = false;
        SafeFileHandle? duplicate = null;
        try
        {
            borrowedHandle.DangerousAddRef(ref held);
            if (borrowedHandle.IsInvalid || Native.GetFileType(borrowedHandle.DangerousGetHandle()) is not (1 or 3))
                throw Unavailable();
            var process = Native.GetCurrentProcess();
            if (!Native.DuplicateHandle(process, borrowedHandle.DangerousGetHandle(), process, out duplicate,
                    0, false, 2) || duplicate.IsInvalid) throw Unavailable();
            // This overload detects the actual handle mode. Buffer size 1 disables managed output buffering.
            var stream = new FileStream(duplicate, FileAccess.Write, bufferSize: 1);
            duplicate = null;
            return stream;
        }
        catch (Exception error) when (error is ObjectDisposedException or ArgumentException or IOException or UnauthorizedAccessException)
        { throw Unavailable(); }
        finally
        {
            duplicate?.Dispose();
            if (held) borrowedHandle.DangerousRelease();
        }
    }

    private static IOException Unavailable() => new("Standard output pipe or file is unavailable.");
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GetStdHandle(uint identifier);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern uint GetFileType(IntPtr handle);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
            out SafeFileHandle duplicate, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    }
}
