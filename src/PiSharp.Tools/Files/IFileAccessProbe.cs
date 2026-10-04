namespace PiSharp.Tools.Files;

[Flags]
public enum FileAccessModes { Exists = 0, Write = 2, Read = 4 }

/// <summary>Trusted nonmutating access callback. Settle owned work before returning or throwing; this is not authorization.</summary>
public interface IFileAccessProbe
{
    ValueTask CheckAsync(string absolutePath, FileAccessModes modes, CancellationToken cancellationToken);
}

/// <summary>Owned symbolic OS access error; arbitrary callback exception text is never forwarded to the model.</summary>
public sealed class FileAccessProbeException : Exception
{
    public string Code { get; }
    public FileAccessProbeException(string code) : base("Filesystem access probe failed.")
    {
        if (string.IsNullOrEmpty(code) || code.Length > 32 || code.Any(value => value is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            throw new ArgumentException("Invalid filesystem access error code.", nameof(code));
        Code = code;
    }
}
