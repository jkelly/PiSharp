using System.Runtime.InteropServices;

namespace PiSharp.Tools.Files;

/// <summary>Nonmutating OS access profile: Windows metadata/ReadOnly check; Linux/macOS libc access with real IDs.</summary>
public sealed class LocalFileAccessProbe : IFileAccessProbe
{
    public ValueTask CheckAsync(string absolutePath, FileAccessModes modes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(absolutePath) || absolutePath.Contains('\0') || !Path.IsPathFullyQualified(absolutePath) ||
            (modes & ~(FileAccessModes.Read | FileAccessModes.Write)) != 0) throw new ArgumentException("Invalid filesystem access probe input.");
        if (OperatingSystem.IsWindows())
        {
            var attributes = GetFileAttributes(absolutePath);
            if (attributes == uint.MaxValue) throw new FileAccessProbeException(WindowsCode(Marshal.GetLastPInvokeError()));
            // libuv 1.52.1 fs__access: no ACL/open check; ReadOnly directories do not fail W_OK.
            if ((modes & FileAccessModes.Write) != 0 && (attributes & 1) != 0 && (attributes & 16) == 0)
                throw new FileAccessProbeException("EPERM");
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            while (Access(absolutePath, (int)modes) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                cancellationToken.ThrowIfCancellationRequested();
                if (error == 4) continue; // libuv retries an interrupted access call.
                throw new FileAccessProbeException(UnixCode(error, OperatingSystem.IsMacOS()));
            }
        }
        else throw new PlatformNotSupportedException("Local access probes are implemented for Windows, Linux and macOS.");
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private static string WindowsCode(int error) => error switch
    {
        2 or 3 or 15 or 123 or 161 or 203 or 267 or 4392 => "ENOENT",
        5 or 1314 => "EPERM", 32 or 33 => "EBUSY", 87 => "EINVAL",
        206 or 111 => "ENAMETOOLONG", 8 or 14 => "ENOMEM", 19 => "EROFS",
        1920 or 740 => "EACCES", 1921 => "ELOOP", 50 => "ENOTSUP", 995 => "ECANCELED",
        1 => "EISDIR", 6 or 1004 => "EBADF", 31 or 1117 => "EIO", _ => "UNKNOWN"
    };
    private static string UnixCode(int error, bool macOS) => error switch
    {
        1 => "EPERM", 2 => "ENOENT", 5 => "EIO", 12 => "ENOMEM", 13 => "EACCES",
        20 => "ENOTDIR", 22 => "EINVAL", 30 => "EROFS",
        36 when !macOS => "ENAMETOOLONG", 40 when !macOS => "ELOOP",
        63 when macOS => "ENAMETOOLONG", 62 when macOS => "ELOOP", _ => "UNKNOWN"
    };

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFileAttributes(string path);
    [DllImport("libc", EntryPoint = "access", ExactSpelling = true, SetLastError = true)]
    private static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}
