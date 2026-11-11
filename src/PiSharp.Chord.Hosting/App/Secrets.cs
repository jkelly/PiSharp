// Pi b4afdddf44e059c67ccf5097e5317d913521b470 (MIT): packages/chord/src/node/app/secrets.ts.
using System.Security.Cryptography;
using System.Text;
using System.Buffers;
using PiSharp.Chord.Hosting.Workers;

namespace PiSharp.Chord.Hosting.App;

/// <summary>Random bearer secrets, irreversible stored hashes, and atomic owner-only secret files.</summary>
public static class Secrets
{
    public static string NewToken(int bytes = 32)
    {
        if (bytes < 1 || bytes > 4096) throw new ArgumentOutOfRangeException(nameof(bytes));
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool MatchesHash(string token, string hash)
    {
        if (hash.Length != 64) return false;
        Span<byte> kept = stackalloc byte[32];
        if (Convert.FromHexString(hash, kept, out _, out var written) != OperationStatus.Done || written != 32) return false;
        Span<byte> given = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), given);
        return CryptographicOperations.FixedTimeEquals(given, kept);
    }

    /// <summary>Reads an existing owner-only regular secret file, or null when absent. Content is never logged.</summary>
    public static async Task<string?> ReadSecretAsync(string path)
    {
        Layout.RefuseLinks(path);
        if (!File.Exists(path)) return null;
        Layout.PrivateFile(path);
        if (new System.IO.FileInfo(path).Length > 16 * 1024) throw new InvalidDataException("The secret file is too large.");
        return (await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false)).Trim();
    }

    /// <summary>Creates a cookie key exactly once; simultaneous creators use the same winning value.</summary>
    public static async Task<string> SecretFileAsync(string path)
    {
        var existing = await ReadSecretAsync(path).ConfigureAwait(false);
        if (existing is not null) return existing;
        var secret = NewToken();
        if (await WriteSecretAsync(path, secret, replace: false).ConfigureAwait(false)) return secret;
        return await ReadSecretAsync(path).ConfigureAwait(false) ?? throw new IOException("The secret file disappeared during creation.");
    }

    /// <summary>Writes and flushes an owner-only temporary file before atomic rename. False means create-only found an existing target.</summary>
    public static async Task<bool> WriteSecretAsync(string path, string text, bool replace = true)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > 16 * 1024) throw new ArgumentException("The secret is too large.", nameof(text));
        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(path)!;
        Layout.PrivateDirectory(parent);
        // File.Move(overwrite: false) can use check-then-rename on Unix. Serialize
        // cooperating creators across threads and processes before checking the target.
        var lockParent = OperatingSystem.IsWindows() ? parent.ToUpperInvariant() : parent;
        var lockName = "secret:" + (OperatingSystem.IsWindows() ? Path.GetFileName(path).ToUpperInvariant() : Path.GetFileName(path));
        using var writing = await ProcessAddressLock.AcquireAsync(lockName, lockParent).ConfigureAwait(false);
        Layout.RefuseLinks(path);
        if (!replace && (File.Exists(path) || Directory.Exists(path))) return false;
        if (replace && File.Exists(path)) Layout.PrivateFile(path);
        var temporary = Path.Combine(parent, "." + NewToken(12) + ".tmp");
        try
        {
            await using (var file = Layout.CreatePrivateFile(temporary))
            {
                await file.WriteAsync(Encoding.UTF8.GetBytes(text + "\n")).ConfigureAwait(false);
                await file.FlushAsync().ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path, overwrite: replace); }
            catch (IOException) when (!replace && (File.Exists(path) || Directory.Exists(path))) { return false; }
            return true;
        }
        finally { File.Delete(temporary); }
    }
}
