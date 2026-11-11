// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/layout.ts.
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PiSharp.Chord.Contracts;
using PiSharp.Chord.Hosting.Workers;
using PiSharp.Chord.Store;
using PiSharp.Durable.Store;

namespace PiSharp.Chord.Hosting.App;

/// <summary>The app's private on-disk layout. Cache files are expendable; stores and secrets are not.</summary>
public sealed record AppLayout(string DataDir, string ServerDirectory, string StoreFile, string CookieSecretFile,
    string FilesDirectory, string WorkersDirectory, string PluginsDirectory, string LogsDirectory, string CacheDirectory)
{
    public string OwnerTokenFile => Path.Combine(DataDir, "owner-token");
    public string WorkerBasesDirectory => Path.Combine(CacheDirectory, "workers");
}

/// <summary>Owns the exclusive process lock on a canonical application directory.</summary>
public sealed class AppDataDirectory : IAsyncDisposable
{
    private ProcessAddressLock? ownedLock;
    internal AppDataDirectory(AppLayout layout, ProcessAddressLock ownedLock) { Layout = layout; this.ownedLock = ownedLock; }
    public AppLayout Layout { get; }
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref ownedLock, null)?.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>Creates and protects the app's directories, files and SQLite stores.</summary>
public static class Layout
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeBits = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string DefaultDataDir(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("An app name must be a single directory name.", nameof(name));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "." + name);
    }

    /// <summary>Resolves links before checking ownership or changing permissions; a second server is refused after one second.</summary>
    public static async Task<AppDataDirectory> OpenAsync(string dataDir, ChordContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.AbortSignal?.ThrowIfCancellationRequested();
        var canonical = CanonicalPath(dataDir);
        CheckNotRootOrHome(canonical);
        CreateMissingDirectories(canonical);
        canonical = CanonicalPath(canonical);
        CheckNotRootOrHome(canonical);
        CheckOwner(canonical, directory: true);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.AbortSignal ?? CancellationToken.None);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        ProcessAddressLock ownedLock;
        try
        {
            // Windows path spellings and short-name aliases resolve before computing this lock's identity.
            ownedLock = await ProcessAddressLock.AcquireAsync("chord-app", OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && context.AbortSignal?.IsCancellationRequested != true)
        { throw new IOException($"Another server runs on {canonical}."); }
        try
        {
            var server = Path.Combine(canonical, "server");
            var layout = new AppLayout(canonical, server, Path.Combine(server, "store.sqlite"), Path.Combine(server, "cookie-secret"),
                Path.Combine(server, "files"), Path.Combine(canonical, "workers"), Path.Combine(canonical, "plugins"),
                Path.Combine(canonical, "logs"), Path.Combine(canonical, "cache"));
            foreach (var path in new[] { canonical, layout.ServerDirectory, layout.FilesDirectory, layout.WorkersDirectory,
                         layout.PluginsDirectory, layout.LogsDirectory, layout.CacheDirectory, layout.WorkerBasesDirectory })
                PrivateDirectory(path);
            return new(layout, ownedLock);
        }
        catch { ownedLock.Dispose(); throw; }
    }

    /// <summary>One case-distinguishing, filesystem-safe directory name for an address, including upper-case and dot characters.</summary>
    public static string WorkerDirectory(AppLayout layout, Address address)
    {
        ArgumentNullException.ThrowIfNull(layout); ArgumentNullException.ThrowIfNull(address);
        if (address.Kind is "" or "." or ".." || address.Kind.Contains('/') || address.Kind.Contains('\\') || address.Kind.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A worker kind must be a single directory name.", nameof(address));
        return Path.Combine(layout.WorkersDirectory, address.Kind, EncodeKey(address.Key));
    }

    internal static string EncodeKey(string key)
    {
        var encoded = new StringBuilder();
        foreach (var value in Encoding.UTF8.GetBytes(key))
            if (value is >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_') encoded.Append((char)value);
            else encoded.Append('%').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        return encoded.Length <= 160 ? encoded.ToString() : "sha256-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    /// <summary>Makes a private directory without changing any existing ancestor's permissions. Internal symlinks are refused.</summary>
    public static void PrivateDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        CheckNotRootOrHome(CanonicalPath(full));
        RefuseLinks(full);
        CreateMissingDirectories(full);
        var normalizeOwner = CheckOwner(full, directory: true);
        if (OperatingSystem.IsWindows()) new DirectoryInfo(full).SetAccessControl(DirectorySecurity(includeOwner: normalizeOwner));
        else File.SetUnixFileMode(full, DirectoryMode);
    }

    /// <summary>Makes a regular file private. Existing links and files belonging to another user are refused.</summary>
    public static void PrivateFile(string path)
    {
        var full = Path.GetFullPath(path);
        RefuseLinks(full);
        if (!File.Exists(full))
        {
            try { using var created = CreatePrivateFile(full); }
            catch (IOException) when (File.Exists(full)) { }
        }
        var normalizeOwner = CheckOwner(full, directory: false);
        if (OperatingSystem.IsWindows()) new System.IO.FileInfo(full).SetAccessControl(FileSecurity(includeOwner: normalizeOwner));
        else File.SetUnixFileMode(full, FileModeBits);
    }

    /// <summary>Opens an initialized Chord store. Both the database and existing SQLite sidecars are private before opening.</summary>
    public static async Task<IStore> OpenStoreAsync(string file, ChordContext context)
    {
        var backend = await OpenBackendAsync(file).ConfigureAwait(false);
        var store = await Tables.OpenStoreAsync(backend, context).ConfigureAwait(false);
        try
        {
            // Schema initialization can create sidecars after OpenBackendAsync inspected them.
            PrivateSidecars(file);
            return store;
        }
        catch { await store.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Opens an uninitialized SQLite backend for archive import.</summary>
    public static async Task<Backend> OpenBackendAsync(string file)
    {
        PrivateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        PrivateFile(file);
        PrivateSidecars(file);
        var backend = await StoreBackends.OpenSqliteAsync(file).ConfigureAwait(false);
        try
        {
            PrivateSidecars(file);
            return backend;
        }
        catch { await backend.Db.CloseAsync().ConfigureAwait(false); throw; }
    }

    private static void PrivateSidecars(string file)
    {
        foreach (var sidecar in new[] { file + "-wal", file + "-shm", file + "-journal" })
            if (File.Exists(sidecar)) PrivateFile(sidecar);
    }

    internal static FileStream CreatePrivateFile(string path)
    {
        RefuseLinks(path);
        if (OperatingSystem.IsWindows())
            return new System.IO.FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 8192, FileOptions.Asynchronous, FileSecurity());
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, BufferSize = 8192,
            Options = FileOptions.Asynchronous, UnixCreateMode = FileModeBits,
        });
    }

    internal static void RefuseLinks(string path)
    {
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!; var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var item = new System.IO.FileInfo(current);
            if (item.LinkTarget is not null || (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("App data paths must not contain symbolic links or reparse points.");
        }
    }

    private static string CanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!; var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null)
                current = directory.ResolveLinkTarget(true)?.FullName ?? throw new IOException("The data directory contains a dangling link.");
            else if (File.Exists(current)) throw new IOException("The data directory contains a regular file.");
        }
        current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        return OperatingSystem.IsWindows() && Directory.Exists(current) ? WindowsCanonicalDirectory(current) : current;
    }

    private static void CheckNotRootOrHome(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var home = CanonicalPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (string.Equals(full, home, PathComparison) || string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), PathComparison))
            throw new IOException("The app data directory must not be the home directory or a filesystem root.");
    }

    private static void CreateMissingDirectories(string path)
    {
        if (Directory.Exists(path)) return;
        var parent = Path.GetDirectoryName(path) ?? throw new IOException("The app directory has no parent.");
        if (!Directory.Exists(parent)) CreateMissingDirectories(parent);
        if (OperatingSystem.IsWindows()) new DirectoryInfo(path).Create(DirectorySecurity());
        else Directory.CreateDirectory(path, DirectoryMode);
    }

    private static bool CheckOwner(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var current = identity.User ?? throw new UnauthorizedAccessException("No current Windows owner identity.");
            var owner = directory ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))
                : new System.IO.FileInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            // Windows applies the token's default owner to ordinary and SQLite-created files.
            // For an elevated token this may be Administrators rather than the account SID.
            // Admit that exact default owner, then PrivateDirectory/PrivateFile set the account
            // owner and protected ACL explicitly; membership in other token groups is not enough.
            if (!current.Equals(owner) && identity.Owner?.Equals(owner) != true)
                throw new UnauthorizedAccessException("The app data path belongs to another user.");
            return !current.Equals(owner);
        }
        var status = UnixStatus(path);
        if (status.Owner != geteuid()) throw new UnauthorizedAccessException("The app data path belongs to another user.");
        if ((status.Mode & 0xF000) != (directory ? 0x4000 : 0x8000)) throw new IOException("The app data path has an invalid file type.");
        if (!directory && status.Links != 1) throw new IOException("App data files must not have hard-link aliases.");
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity DirectorySecurity(bool includeOwner = true)
    {
        var security = new DirectorySecurity(); var owner = WindowsIdentity.GetCurrent().User!;
        // Account-owned paths need only a DACL update. The token's alternate default owner
        // is normalized once; avoid requesting WRITE_OWNER again on account-owned paths.
        if (includeOwner) security.SetOwner(owner);
        security.SetAccessRuleProtection(true, false);
        foreach (var identity in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity FileSecurity(bool includeOwner = true)
    {
        var security = new FileSecurity(); var owner = WindowsIdentity.GetCurrent().User!;
        if (includeOwner) security.SetOwner(owner);
        security.SetAccessRuleProtection(true, false);
        foreach (var identity in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static (uint Owner, ushort Mode, ulong Links) UnixStatus(string path)
    {
        var bytes = new byte[256];
        if (OperatingSystem.IsLinux())
        {
            if (statx(-100, path, 0x100, 0x7ff, bytes) != 0) throw new IOException("Cannot examine ownership of app data.", new Win32Exception(Marshal.GetLastPInvokeError()));
            return (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(28)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        }
        if (OperatingSystem.IsMacOS())
        {
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? mac_lstat_x64(path, bytes) : mac_lstat_arm64(path, bytes);
            if (result != 0) throw new IOException("Cannot examine ownership of app data.", new Win32Exception(Marshal.GetLastPInvokeError()));
            return (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)));
        }
        throw new PlatformNotSupportedException("Private app data requires native ownership checks on this operating system.");
    }

    [SupportedOSPlatform("windows")]
    private static string WindowsCanonicalDirectory(string path)
    {
        using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Cannot resolve the app data directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var value = new StringBuilder(32768); var size = GetFinalPathNameByHandleW(handle, value, (uint)value.Capacity, 0);
        if (size == 0 || size >= value.Capacity) throw new IOException("Cannot resolve the app data directory.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var final = value.ToString();
        return final.StartsWith("\\\\?\\UNC\\", StringComparison.Ordinal) ? "\\\\" + final[8..] : final.StartsWith("\\\\?\\", StringComparison.Ordinal) ? final[4..] : final;
    }

    [DllImport("libc", SetLastError = true)] private static extern uint geteuid();
    [DllImport("libc", SetLastError = true)] private static extern int statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mask, byte[] buffer);
    [DllImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true)] private static extern int mac_lstat_x64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)] private static extern int mac_lstat_arm64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, byte[] buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
